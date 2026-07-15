import base64
import concurrent.futures
import copy
import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path


MODULE_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_FILE = SOURCE_ROOT / "artifact_builder.py"
if SOURCE_ROOT.is_symlink() or SOURCE_FILE.is_symlink():
    raise ImportError("factory-artifact-builder unit subject must not use symbolic links")
RESOLVED_SOURCE_ROOT = SOURCE_ROOT.resolve(strict=True)
RESOLVED_SOURCE_FILE = SOURCE_FILE.resolve(strict=True)
try:
    RESOLVED_SOURCE_FILE.relative_to(MODULE_ROOT)
except ValueError as exc:
    raise ImportError("factory-artifact-builder unit subject escapes its module root") from exc
if RESOLVED_SOURCE_FILE.parent != RESOLVED_SOURCE_ROOT or not RESOLVED_SOURCE_FILE.is_file():
    raise ImportError("factory-artifact-builder unit subject is not the declared source file")

SUBJECT_NAME = "dps_factory_artifact_builder_unit_subject"
if SUBJECT_NAME in sys.modules:
    raise ImportError("factory-artifact-builder unit subject identity is already loaded")
SUBJECT_SPEC = importlib.util.spec_from_file_location(SUBJECT_NAME, RESOLVED_SOURCE_FILE)
if SUBJECT_SPEC is None or SUBJECT_SPEC.loader is None:
    raise ImportError("factory-artifact-builder unit subject loader is unavailable")
SUBJECT = importlib.util.module_from_spec(SUBJECT_SPEC)
sys.modules[SUBJECT_NAME] = SUBJECT
try:
    SUBJECT_SPEC.loader.exec_module(SUBJECT)
except BaseException:
    sys.modules.pop(SUBJECT_NAME, None)
    raise

ArtifactBuildError = SUBJECT.ArtifactBuildError
ArtifactBuilder = SUBJECT._ArtifactBuilderCore
ProductionArtifactBuilder = SUBJECT.ArtifactBuilder
InMemoryBuildIdentityRegistry = SUBJECT.InMemoryBuildIdentityRegistry
PostgresBuildIdentityRegistry = SUBJECT.PostgresBuildIdentityRegistry
GitSourceTree = SUBJECT.GitSourceTree
MergeDecisionTrustStore = SUBJECT.MergeDecisionTrustStore
canonical_bytes = SUBJECT.canonical_bytes


RSA_N = int(
    "d558ac64db3f45412cda262c2d9bc4d28aa5cc0b3f0e839fdf6689809133f0a7"
    "e73fcda74f1222650189276dc5043cedb3e3026227dd366abcad140d562da829b8"
    "3cf4578a4e7070100874a151552fc41e295d435e19df44b76a6704cb6df1c071f"
    "d7baf2834b28e02d6be84c3a6528d8bd501bdb8f7cbba32e7ac63c2102aa1",
    16,
)
RSA_D = int(
    "7f39d4048922a0000fe93f9e54cc718144a13e9eee498f80c54e766d2f2a1437"
    "6c9605e3e229644d6baf08ce531105ec92bbab6e316b9fc9e31e2bb9104d45dc"
    "14763250168949a3f3516fa690baebf60aa1ddf82cce3e0dc99483fc00b8e8fd"
    "60492b2432dc8e3b62ef0a7979b4bd46f63e632e7a83ca9f5d0b803c31bab3a9",
    16,
)
RSA_E = 65537
POLICY_SHA = "d" * 64


def _mgf1(seed, length):
    output = bytearray()
    counter = 0
    while len(output) < length:
        output.extend(hashlib.sha256(seed + counter.to_bytes(4, "big")).digest())
        counter += 1
    return bytes(output[:length])


def _sign(message):
    em_bits = RSA_N.bit_length() - 1
    em_length = (em_bits + 7) // 8
    salt = hashlib.sha256(b"artifact-test-salt" + message).digest()
    message_hash = hashlib.sha256(message).digest()
    encoded_hash = hashlib.sha256(b"\x00" * 8 + message_hash + salt).digest()
    padding_length = em_length - len(encoded_hash) - len(salt) - 2
    data_block = b"\x00" * padding_length + b"\x01" + salt
    mask = _mgf1(encoded_hash, len(data_block))
    masked = bytearray(left ^ right for left, right in zip(data_block, mask))
    unused_bits = 8 * em_length - em_bits
    if unused_bits:
        masked[0] &= 0xFF >> unused_bits
    encoded = bytes(masked) + encoded_hash + b"\xbc"
    return pow(int.from_bytes(encoded, "big"), RSA_D, RSA_N).to_bytes((RSA_N.bit_length() + 7) // 8, "big")


def _git(root, *arguments):
    result = subprocess.run(
        ["git", "-C", str(root), *arguments],
        check=True,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return result.stdout.decode("utf-8").strip()


def signed_decision(commit, outcome="APPROVED"):
    decision = {
        "schema_version": "1.0.0",
        "contract_id": "merge.decision/v1",
        "producer_module": "factory-merge-controller",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "8" * 32,
        "idempotency_key": "idem_" + "c" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "decision_id": "merge-" + "c" * 32,
        "merge_request_id": "request-001",
        "integration_commit": commit,
        "outcome": outcome,
        "reasons": [] if outcome == "APPROVED" else ["rejected"],
        "evidence_ids": ["evidence-001"],
        "decided_by": "merge-decider",
        "verification_scope": "MERGE_HEAD_ONLY",
        "trusted_policy_sha256": POLICY_SHA,
        "runner_attestation_sha256": "e" * 64,
    }
    message = b"dps-merge-decision/v1\n" + canonical_bytes(decision)
    return {
        "decision": decision,
        "attestation": {
            "algorithm": "rsa-pss-sha256",
            "key_id": "merge-key-001",
            "signer_identity": "merge-decider",
            "payload_sha256": hashlib.sha256(message).hexdigest(),
            "signature_value": base64.b64encode(_sign(message)).decode("ascii"),
        },
    }


def resign_decision(envelope):
    message = b"dps-merge-decision/v1\n" + canonical_bytes(envelope["decision"])
    envelope["attestation"].update({
        "payload_sha256": hashlib.sha256(message).hexdigest(),
        "signature_value": base64.b64encode(_sign(message)).decode("ascii"),
    })
    return envelope


class RepositoryFixture:
    def __init__(self, root):
        # macOS exposes /var as a system symlink to /private/var.  Resolve the
        # test sandbox once so the subject can keep its no-symlink path policy.
        self.root = Path(root).resolve()
        _git(self.root, "init", "-q")
        _git(self.root, "config", "user.email", "factory-tests@dps.invalid")
        _git(self.root, "config", "user.name", "DPS Factory Tests")
        self.module = self.root / "Modules" / "module-one"
        (self.module / "dist").mkdir(parents=True)
        (self.module / "AGENTS.md").write_text("# Module rules\n", encoding="utf-8")
        manifest = {
            "module": {"id": "module-one", "version": "1.2.3"},
            "paths": {
                "actualRoot": "Modules/module-one",
                "canonicalRoot": "modules/module-one",
                "owned": ["Modules/module-one/**"],
            },
        }
        (self.module / "module.yaml").write_text(json.dumps(manifest), encoding="utf-8")
        self.artifact = self.module / "dist" / "input.bin"
        self.artifact.write_bytes(b"exact-artifact-bytes")
        (self.module / "src.py").write_bytes(b"print('source')\n")
        _git(self.root, "add", ".")
        _git(self.root, "commit", "-q", "-m", "fixture")
        self.commit = _git(self.root, "rev-parse", "HEAD")
        self.build_identity_registry = InMemoryBuildIdentityRegistry()

    def request(self, path=None, expected=None):
        relative = path if path is not None else "Modules/module-one/dist/input.bin"
        digest = expected if expected is not None else hashlib.sha256(self.artifact.read_bytes()).hexdigest()
        return {
            "schema_version": "1.0.0",
            "contract_id": "artifact.build.request/v1",
            "producer_module": "factory-release-controller",
            "soul_id": None,
            "device_binding_id": None,
            "platform_account_id": None,
            "trace_id": "trace_" + "8" * 32,
            "idempotency_key": "idem_" + "7" * 64,
            "occurred_at": "2026-07-14T00:00:00Z",
            "privacy_class": "internal",
            "build_id": "build-001",
            "module_id": "module-one",
            "module_version": "1.2.3",
            "integration_commit": self.commit,
            "artifact_path": relative,
            "expected_sha256": digest,
            "merge_decision_id": "merge-" + "c" * 32,
        }

    def builder(self, envelope=None, registry=None):
        record = envelope if envelope is not None else signed_decision(self.commit)
        trust = MergeDecisionTrustStore({
            "merge-key-001": {
                "identity": "merge-decider",
                "algorithm": "rsa-pss-sha256",
                "modulus_hex": format(RSA_N, "x"),
                "exponent": RSA_E,
            }
        })
        return ArtifactBuilder(
            self.root,
            lambda _: copy.deepcopy(record),
            trust,
            {POLICY_SHA},
            registry if registry is not None else self.build_identity_registry,
        )


class ArtifactBuilderTests(unittest.TestCase):
    def test_production_builder_rejects_non_postgres_registry(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            trust = MergeDecisionTrustStore({
                "merge-key-001": {
                    "identity": "merge-decider",
                    "algorithm": "rsa-pss-sha256",
                    "modulus_hex": format(RSA_N, "x"),
                    "exponent": RSA_E,
                }
            })
            with self.assertRaisesRegex(ValueError, "requires PostgresBuildIdentityRegistry"):
                ProductionArtifactBuilder(
                    fixture.root,
                    lambda _: signed_decision(fixture.commit),
                    trust,
                    {POLICY_SHA},
                    InMemoryBuildIdentityRegistry(),
                )

    def test_postgres_registry_commits_only_matching_atomic_claim_result(self):
        identity = {
            "schema_version": "dps.artifact-build-identity-claim/v1",
            "build_id": "build-001",
            "request_sha256": "1" * 64,
            "decision_sha256": "2" * 64,
            "artifact_sha256": "3" * 64,
            "source_tree_sha256": "4" * 64,
            "module_id": "module-one",
            "module_version": "1.2.3",
            "integration_commit": "5" * 40,
        }

        class Cursor:
            def __init__(self, results):
                self.results = list(results)
                self.arguments = []

            def execute(self, statement, arguments=None):
                self.arguments.append((statement, arguments))

            def fetchone(self):
                return self.results.pop(0)

            def fetchall(self):
                return self.results.pop(0)

            def close(self):
                pass

        class Connection:
            def __init__(self, results, *, autocommit=False):
                self._cursor = Cursor(results)
                self.commits = 0
                self.rollbacks = 0
                self.autocommit = autocommit

            def cursor(self):
                return self._cursor

            def commit(self):
                self.commits += 1

            def rollback(self):
                self.rollbacks += 1

            def close(self):
                pass

        runtime_role = "factory_artifact_runtime"
        security = (
            "180004", runtime_role, runtime_role, "on", "on", "on",
            True, False, False, False, False, False, False,
            False, False, False, False, False,
        )
        attestation = SUBJECT._SCHEMA_ATTESTATION
        claim_source = "reviewed claim function body"
        mutation_source = "reviewed mutation function body"
        claim_hash = SUBJECT._sha256_bytes(claim_source.encode("utf-8"))
        mutation_hash = SUBJECT._sha256_bytes(mutation_source.encode("utf-8"))
        object_row = (
            "factory_artifact_owner", "factory_artifact_owner",
            "factory_artifact_owner", "factory_artifact_owner",
            attestation, attestation, attestation, attestation,
            "r", "p", False, False, 10, 2,
            "f", True, "plpgsql", ["search_path=pg_catalog"],
            "boolean", 9, False, "v", "u", False, claim_source,
            "f", False, "plpgsql", None, "trigger", 0,
            False, "v", "u", False, mutation_source,
        )
        acl_row = (False, 1, True, False, False, 1, True, False)
        constraints = (10, True, True, 1, "PRIMARY KEY (build_id)", True, True)
        triggers = [
            (
                "reject_build_identity_truncate", 34, "O",
                "factory_artifact.reject_build_identity_mutation()",
            ),
            (
                "reject_build_identity_update_delete", 27, "O",
                "factory_artifact.reject_build_identity_mutation()",
            ),
        ]

        def valid_results(claim_result=True):
            return [
                security,
                object_row,
                acl_row,
                list(SUBJECT._EXPECTED_BUILD_IDENTITY_COLUMNS),
                constraints,
                triggers,
                (claim_result,),
            ]

        with mock.patch.object(SUBJECT, "_CLAIM_FUNCTION_PROSRC_SHA256", claim_hash), \
             mock.patch.object(SUBJECT, "_MUTATION_FUNCTION_PROSRC_SHA256", mutation_hash):
            accepted = Connection(valid_results())
            PostgresBuildIdentityRegistry(lambda: accepted).claim(identity)
        self.assertEqual(1, accepted.commits)
        self.assertEqual(1, accepted.rollbacks)
        self.assertEqual("SET LOCAL synchronous_commit = on", accepted._cursor.arguments[0][0])
        self.assertIn("server_version_num", accepted._cursor.arguments[1][0])
        self.assertIn("claim_build_identity", accepted._cursor.arguments[7][0])
        self.assertEqual(identity["build_id"], accepted._cursor.arguments[7][1][0])

        conflict = Connection(valid_results(False))
        with mock.patch.object(SUBJECT, "_CLAIM_FUNCTION_PROSRC_SHA256", claim_hash), \
             mock.patch.object(SUBJECT, "_MUTATION_FUNCTION_PROSRC_SHA256", mutation_hash), \
             self.assertRaisesRegex(ArtifactBuildError, "already claimed"):
            PostgresBuildIdentityRegistry(lambda: conflict).claim(identity)
        self.assertEqual(0, conflict.commits)
        self.assertEqual(2, conflict.rollbacks)

        security_mutations = (
            ("set-role", 2, "migration_owner"),
            ("sync-off", 3, "off"),
            ("inherit", 7, True),
            ("database-owner", 14, True),
            ("database-create", 15, True),
            ("database-temp", 16, True),
            ("schema-create", 17, True),
        )
        for label, index, value in security_mutations:
            with self.subTest(runtime_boundary=label):
                changed = list(security)
                changed[index] = value
                privileged = Connection([tuple(changed)])
                with self.assertRaisesRegex(ArtifactBuildError, "least-privilege"):
                    PostgresBuildIdentityRegistry(lambda: privileged).claim(identity)
                self.assertEqual(0, privileged.commits)
                self.assertEqual(2, privileged.rollbacks)

        drifted_objects = list(valid_results())
        drifted_object_row = list(object_row)
        drifted_object_row[24] = claim_source + " drift"
        drifted_objects[1] = tuple(drifted_object_row)
        drifted = Connection(drifted_objects)
        with mock.patch.object(SUBJECT, "_CLAIM_FUNCTION_PROSRC_SHA256", claim_hash), \
             mock.patch.object(SUBJECT, "_MUTATION_FUNCTION_PROSRC_SHA256", mutation_hash), \
             self.assertRaisesRegex(ArtifactBuildError, "reviewed migration"):
            PostgresBuildIdentityRegistry(lambda: drifted).claim(identity)

        weak_acl = list(valid_results())
        weak_acl[2] = (True, 1, True, False, False, 1, True, False)
        unauthorized = Connection(weak_acl)
        with mock.patch.object(SUBJECT, "_CLAIM_FUNCTION_PROSRC_SHA256", claim_hash), \
             mock.patch.object(SUBJECT, "_MUTATION_FUNCTION_PROSRC_SHA256", mutation_hash), \
             self.assertRaisesRegex(ArtifactBuildError, "ACL"):
            PostgresBuildIdentityRegistry(lambda: unauthorized).claim(identity)

        auto = Connection([], autocommit=True)
        with self.assertRaisesRegex(ArtifactBuildError, "non-autocommit"):
            PostgresBuildIdentityRegistry(lambda: auto).claim(identity)
        self.assertEqual(0, auto.commits)
        self.assertEqual(1, auto.rollbacks)

    def test_build_identity_migration_is_append_only_and_least_privilege(self):
        migration = (MODULE_ROOT / "migrations" / "001_build_identity_claims.sql").read_text(
            encoding="utf-8"
        )
        required = (
            "BEGIN;",
            "CREATE SCHEMA factory_artifact;",
            "CREATE TABLE factory_artifact.build_identity_claim",
            "PRIMARY KEY",
            "SECURITY DEFINER",
            "SET search_path = pg_catalog",
            "ON CONFLICT (build_id) DO NOTHING",
            "BEFORE UPDATE OR DELETE",
            "BEFORE TRUNCATE",
            "REVOKE ALL ON TABLE factory_artifact.build_identity_claim FROM PUBLIC",
            "REVOKE ALL ON FUNCTION factory_artifact.claim_build_identity",
            "REVOKE ALL ON FUNCTION factory_artifact.reject_build_identity_mutation() FROM PUBLIC",
            "COMMENT ON SCHEMA factory_artifact IS",
            "COMMENT ON TABLE factory_artifact.build_identity_claim IS",
            "COMMIT;",
        )
        for token in required:
            with self.subTest(token=token):
                self.assertIn(token, migration)
        self.assertNotIn("CREATE TABLE IF NOT EXISTS", migration)

    def test_builds_digest_addressed_bundle_from_real_git_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            output = fixture.root / "out"
            descriptor = fixture.builder().build(fixture.request(), output)
            expected = hashlib.sha256(fixture.artifact.read_bytes()).hexdigest()
            self.assertEqual(expected, descriptor["artifact_sha256"])
            self.assertEqual("idem_" + expected, descriptor["idempotency_key"])
            self.assertEqual("UNSIGNED_AWAITING_EXTERNAL_SIGNER", descriptor["signature"]["status"])
            sbom = json.loads((output / descriptor["sbom"]["path"]).read_text())
            source_entry = next(item for item in sbom["files"] if item["fileName"].endswith("src.py"))
            source_sha = hashlib.sha256((fixture.module / "src.py").read_bytes()).hexdigest()
            self.assertEqual(source_sha, source_entry["checksums"][0]["checksumValue"])
            provenance = json.loads((output / descriptor["provenance"]["path"]).read_text())
            self.assertEqual(fixture.commit, provenance["predicate"]["buildDefinition"]["externalParameters"]["integration_commit"])

    def test_absolute_traversal_and_symlink_escape_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            builder = fixture.builder()
            for path in ("/etc/hosts", "Modules/module-one/../../etc/hosts"):
                with self.assertRaises(ArtifactBuildError):
                    builder.build(fixture.request(path=path), fixture.root / "out")
            outside = fixture.root / "outside.bin"
            outside.write_bytes(b"outside")
            link = fixture.module / "dist" / "escape.bin"
            os.symlink(outside, link)
            request = fixture.request(path="Modules/module-one/dist/escape.bin", expected=hashlib.sha256(b"outside").hexdigest())
            with self.assertRaisesRegex(ArtifactBuildError, "links|unsafe"):
                builder.build(request, fixture.root / "out")

    def test_forged_approved_decision_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            forged = signed_decision(fixture.commit)
            forged["decision"]["outcome"] = "REJECTED"
            with self.assertRaisesRegex(ArtifactBuildError, "attestation digest|signature verification"):
                fixture.builder(forged).build(fixture.request(), fixture.root / "out")

    def test_worktree_tamper_after_signed_commit_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            fixture.artifact.write_bytes(b"TAMPERED-AFTER-SIGNED-COMMIT")
            tampered_sha = hashlib.sha256(fixture.artifact.read_bytes()).hexdigest()
            request = fixture.request(expected=tampered_sha)
            with self.assertRaisesRegex(ArtifactBuildError, "signed integration commit"):
                fixture.builder().build(request, fixture.root / "out")

    def test_request_cannot_supply_source_inventory_or_merge_decision(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            for field, value in (
                ("source_files", [{"path": "/etc/hosts", "sha256": "0" * 64}]),
                ("merge_decision", {"outcome": "APPROVED"}),
            ):
                request = fixture.request()
                request[field] = value
                with self.assertRaisesRegex(ArtifactBuildError, "unknown or missing"):
                    fixture.builder().build(request, fixture.root / "out")

    def test_custom_mutable_mapping_objects_are_rejected_at_trust_boundaries(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)

            class MutableMapping(dict):
                pass

            with self.assertRaisesRegex(ArtifactBuildError, "plain JSON object"):
                fixture.builder().build(MutableMapping(fixture.request()), fixture.root / "request-out")

            envelope = signed_decision(fixture.commit)
            envelope["decision"] = MutableMapping(envelope["decision"])
            with self.assertRaisesRegex(ArtifactBuildError, "decision and attestation"):
                fixture.builder(envelope).build(fixture.request(), fixture.root / "decision-out")

    def test_expected_digest_and_unapproved_policy_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            with self.assertRaisesRegex(ArtifactBuildError, "differs"):
                fixture.builder().build(fixture.request(expected="0" * 64), fixture.root / "out")
            envelope = signed_decision(fixture.commit)
            envelope["decision"]["trusted_policy_sha256"] = "f" * 64
            resign_decision(envelope)
            with self.assertRaisesRegex(ArtifactBuildError, "does not authorize"):
                fixture.builder(envelope).build(fixture.request(), fixture.root / "out")

    def test_signed_decision_must_match_its_exact_contract_not_only_its_signature(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            mutations = (
                ("unknown-field", lambda decision: decision.__setitem__("ignored", True)),
                ("idempotency_key", lambda decision: decision.__setitem__("idempotency_key", "merge-" + "c" * 32)),
                ("occurred_at", lambda decision: decision.__setitem__("occurred_at", "2026-02-30T00:00:00Z")),
                ("verification_scope", lambda decision: decision.__setitem__("verification_scope", "BRANCH_HEAD")),
                ("evidence_ids", lambda decision: decision.__setitem__("evidence_ids", [])),
                ("evidence_id_format", lambda decision: decision.__setitem__("evidence_ids", ["bad\n"])),
                ("reasons", lambda decision: decision.__setitem__("reasons", ["approved anyway"])),
            )
            for label, mutate in mutations:
                with self.subTest(field=label):
                    envelope = signed_decision(fixture.commit)
                    mutate(envelope["decision"])
                    resign_decision(envelope)
                    output = fixture.root / ("out-" + label)
                    with self.assertRaises(ArtifactBuildError):
                        fixture.builder(envelope).build(fixture.request(), output)
                    self.assertFalse(output.exists())

    def test_build_request_and_merge_decision_common_scope_are_exactly_bound(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            for field, different in (
                ("trace_id", "trace_" + "1" * 32),
                ("soul_id", "soul_" + "2" * 64),
                ("device_binding_id", "db_" + "3" * 32),
                ("platform_account_id", "pa_" + "4" * 32),
            ):
                with self.subTest(field=field):
                    envelope = signed_decision(fixture.commit)
                    envelope["decision"][field] = different
                    resign_decision(envelope)
                    with self.assertRaisesRegex(ArtifactBuildError, field):
                        fixture.builder(envelope).build(
                            fixture.request(),
                            fixture.root / ("out-" + field),
                        )

    def test_request_metadata_and_temporal_order_fail_closed_before_output(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            mutations = (
                ("build_id", "x"),
                ("privacy_class", "public"),
                ("occurred_at", "2026-02-30T00:00:00Z"),
                ("merge_decision_id", "merge-short"),
            )
            for field, value in mutations:
                with self.subTest(field=field):
                    request = fixture.request()
                    request[field] = value
                    output = fixture.root / ("out-" + field)
                    with self.assertRaises(ArtifactBuildError):
                        fixture.builder().build(request, output)
                    self.assertFalse(output.exists())

            envelope = signed_decision(fixture.commit)
            envelope["decision"]["occurred_at"] = "2026-07-14T00:00:01Z"
            resign_decision(envelope)
            with self.assertRaisesRegex(ArtifactBuildError, "occurred before"):
                fixture.builder(envelope).build(fixture.request(), fixture.root / "out-time")
            self.assertFalse((fixture.root / "out-time").exists())

    def test_manifest_version_must_equal_requested_module_version(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            request = fixture.request()
            request["module_version"] = "1.2.4"
            with self.assertRaisesRegex(ArtifactBuildError, "module version"):
                fixture.builder().build(request, fixture.root / "out")
            self.assertFalse((fixture.root / "out").exists())

    def test_same_input_is_deterministic(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            first = fixture.builder().build(fixture.request(), fixture.root / "first")
            second = fixture.builder().build(fixture.request(), fixture.root / "second")
            self.assertEqual(first, second)

    def test_build_id_exact_retry_is_idempotent_across_builders_and_output_directories(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            registry = InMemoryBuildIdentityRegistry()
            request = fixture.request()
            first = fixture.builder(registry=registry).build(request, fixture.root / "first")
            second = fixture.builder(registry=registry).build(request, fixture.root / "second")
            self.assertEqual(first, second)

    def test_build_id_cannot_be_reused_for_different_validated_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            registry = InMemoryBuildIdentityRegistry()
            fixture.builder(registry=registry).build(fixture.request(), fixture.root / "first")

            fixture.artifact.write_bytes(b"different-but-valid-artifact")
            _git(fixture.root, "add", ".")
            _git(fixture.root, "commit", "-q", "-m", "different artifact")
            fixture.commit = _git(fixture.root, "rev-parse", "HEAD")
            request = fixture.request()
            output = fixture.root / "conflict"
            with self.assertRaisesRegex(ArtifactBuildError, "build_id is already claimed"):
                fixture.builder(
                    signed_decision(fixture.commit),
                    registry=registry,
                ).build(request, output)
            self.assertFalse(output.exists())

    def test_build_id_binds_the_entire_validated_request_not_only_artifact_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            registry = InMemoryBuildIdentityRegistry()
            fixture.builder(registry=registry).build(fixture.request(), fixture.root / "first")
            changed = fixture.request()
            changed["occurred_at"] = "2026-07-14T00:00:01Z"
            with self.assertRaisesRegex(ArtifactBuildError, "build_id is already claimed"):
                fixture.builder(registry=registry).build(changed, fixture.root / "conflict")
            self.assertFalse((fixture.root / "conflict").exists())

    def test_shared_registry_serializes_concurrent_exact_retries(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            registry = InMemoryBuildIdentityRegistry()
            request = fixture.request()

            def build(index):
                return fixture.builder(registry=registry).build(
                    request,
                    fixture.root / f"parallel-{index}",
                )

            with concurrent.futures.ThreadPoolExecutor(max_workers=4) as executor:
                descriptors = list(executor.map(build, range(4)))
            self.assertTrue(all(item == descriptors[0] for item in descriptors))

    def test_identity_claim_occurs_after_validation_but_before_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)

            class RecordingRegistry(SUBJECT.BuildIdentityRegistry):
                def __init__(self):
                    self.claims = []

                def claim(self, claim):
                    self.claims.append(copy.deepcopy(dict(claim)))
                    self.assertion()

                def assertion(self):
                    self.test_case.assertFalse(self.output.exists())

            registry = RecordingRegistry()
            registry.test_case = self
            registry.output = fixture.root / "out"
            fixture.builder(registry=registry).build(fixture.request(), registry.output)
            self.assertEqual(1, len(registry.claims))

            invalid_registry = RecordingRegistry()
            invalid_registry.test_case = self
            invalid_registry.output = fixture.root / "invalid"
            invalid = fixture.request(expected="0" * 64)
            with self.assertRaisesRegex(ArtifactBuildError, "differs"):
                fixture.builder(registry=invalid_registry).build(invalid, invalid_registry.output)
            self.assertEqual([], invalid_registry.claims)

    def test_lost_claim_acknowledgement_publishes_nothing_and_exact_retry_recovers(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)

            class LostAcknowledgementRegistry(SUBJECT.BuildIdentityRegistry):
                def __init__(self):
                    self.delegate = InMemoryBuildIdentityRegistry()
                    self.fail_once = True

                def claim(self, claim):
                    self.delegate.claim(claim)
                    if self.fail_once:
                        self.fail_once = False
                        raise ArtifactBuildError("simulated lost durable claim acknowledgement")

            registry = LostAcknowledgementRegistry()
            output = fixture.root / "out"
            with self.assertRaisesRegex(ArtifactBuildError, "lost durable"):
                fixture.builder(registry=registry).build(fixture.request(), output)
            self.assertFalse(output.exists())

            descriptor = fixture.builder(registry=registry).build(fixture.request(), output)
            self.assertTrue((output / descriptor["artifact_file"]).is_file())

    def test_noncanonical_common_ids_fail_before_any_build_output(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            valid_ids = {
                "soul_id": "soul_" + "6" * 64,
                "device_binding_id": "db_" + "5" * 32,
                "platform_account_id": "pa_" + "4" * 32,
                "trace_id": "trace_" + "3" * 32,
                "idempotency_key": "idem_" + "2" * 64,
            }
            for field, valid in valid_ids.items():
                request = fixture.request()
                request[field] = valid + "\n"
                output = fixture.root / f"out-{field}"
                with self.subTest(field=field), self.assertRaisesRegex(ArtifactBuildError, f"invalid {field}"):
                    fixture.builder().build(request, output)
                self.assertFalse(output.exists())

    def test_same_output_is_idempotent_and_published_files_are_nonwritable(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            output = fixture.root / "out"
            first = fixture.builder().build(fixture.request(), output)
            second = fixture.builder().build(fixture.request(), output)
            self.assertEqual(first, second)
            published = [item for item in output.iterdir() if item.name != ".dps-artifact-builder.lock"]
            self.assertEqual(4, len(published))
            for item in published:
                information = item.stat()
                self.assertEqual(1, information.st_nlink)
                self.assertEqual(0, information.st_mode & 0o222)
            self.assertFalse(any(item.name.startswith("._dps_tmp_") for item in output.iterdir()))

    def test_same_artifact_from_different_valid_builds_coexists_in_one_content_store(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            registry = InMemoryBuildIdentityRegistry()
            output = fixture.root / "out"
            first = fixture.builder(registry=registry).build(fixture.request(), output)

            (fixture.module / "src.py").write_bytes(b"print('different source, same artifact')\n")
            _git(fixture.root, "add", ".")
            _git(fixture.root, "commit", "-q", "-m", "same binary from different source")
            fixture.commit = _git(fixture.root, "rev-parse", "HEAD")
            second_request = fixture.request()
            second_request["build_id"] = "build-002"
            second = fixture.builder(
                signed_decision(fixture.commit),
                registry=registry,
            ).build(second_request, output)

            self.assertEqual(first["artifact_sha256"], second["artifact_sha256"])
            self.assertEqual(first["artifact_file"], second["artifact_file"])
            self.assertNotEqual(first["source_tree_sha256"], second["source_tree_sha256"])
            self.assertNotEqual(first["sbom"]["path"], second["sbom"]["path"])
            self.assertNotEqual(first["provenance"]["path"], second["provenance"]["path"])
            self.assertEqual(2, len(list(output.glob("*.descriptor.json"))))
            for descriptor in (first, second):
                self.assertTrue((output / descriptor["sbom"]["path"]).is_file())
                self.assertTrue((output / descriptor["provenance"]["path"]).is_file())

    def test_file_name_swap_after_open_is_detected_without_reading_symlink_target(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            original = fixture.artifact.with_name("input.original")
            outside = fixture.root / "outside.bin"
            outside.write_bytes(b"outside-attacker-bytes")
            target_identity = (fixture.artifact.stat().st_dev, fixture.artifact.stat().st_ino)
            original_read = SUBJECT.os.read
            swapped = False

            def race_read(file_fd, count):
                nonlocal swapped
                information = os.fstat(file_fd)
                if not swapped and (information.st_dev, information.st_ino) == target_identity:
                    fixture.artifact.rename(original)
                    os.symlink(outside, fixture.artifact)
                    swapped = True
                return original_read(file_fd, count)

            with mock.patch.object(SUBJECT.os, "read", side_effect=race_read):
                with self.assertRaisesRegex(ArtifactBuildError, "changed|namespace|links"):
                    fixture.builder().build(fixture.request(), fixture.root / "out")
            self.assertTrue(swapped)
            self.assertFalse((fixture.root / "out").exists())

    def test_output_directory_swap_fails_without_writing_through_attacker_symlink(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            output = fixture.root / "out"
            displaced = fixture.root / "out.displaced"
            outside = fixture.root / "outside-output"
            outside.mkdir()
            original_write = SUBJECT._write_immutable_locked
            swapped = False

            def swap_then_write(directory_fd, name, payload, *, max_bytes):
                nonlocal swapped
                if not swapped:
                    output.rename(displaced)
                    os.symlink(outside, output, target_is_directory=True)
                    swapped = True
                return original_write(directory_fd, name, payload, max_bytes=max_bytes)

            with mock.patch.object(SUBJECT, "_write_immutable_locked", side_effect=swap_then_write):
                with self.assertRaisesRegex(ArtifactBuildError, "namespace changed"):
                    fixture.builder().build(fixture.request(), output)
            self.assertTrue(swapped)
            self.assertEqual([], list(outside.iterdir()))
            self.assertTrue(any(item.name.endswith(".descriptor.json") for item in displaced.iterdir()))

    def test_artifact_and_manifest_byte_limits_fail_before_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            with mock.patch.object(SUBJECT, "_MAX_ARTIFACT_BYTES", len(fixture.artifact.read_bytes()) - 1):
                with self.assertRaisesRegex(ArtifactBuildError, "byte limit"):
                    fixture.builder().build(fixture.request(), fixture.root / "artifact-out")
            self.assertFalse((fixture.root / "artifact-out").exists())
            with mock.patch.object(SUBJECT, "_MAX_MANIFEST_BYTES", 1):
                with self.assertRaisesRegex(ArtifactBuildError, "byte limit"):
                    fixture.builder().build(fixture.request(), fixture.root / "manifest-out")
            self.assertFalse((fixture.root / "manifest-out").exists())

    def test_git_inventory_rejects_duplicate_paths_before_blob_read(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            tree = GitSourceTree(fixture.root)
            object_id = b"a" * 40
            record = b"100644 blob " + object_id + b" 3\tModules/module-one/a.bin\x00"
            with mock.patch.object(tree, "_run", return_value=record + record):
                with self.assertRaisesRegex(ArtifactBuildError, "duplicate"):
                    tree.inventory(fixture.commit, "Modules/module-one")

    def test_git_subprocess_stdout_is_bounded_before_tree_parsing(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            tree = GitSourceTree(fixture.root)
            with mock.patch.object(SUBJECT, "_MAX_GIT_LISTING_BYTES", 1):
                with self.assertRaisesRegex(ArtifactBuildError, "resource limit"):
                    tree.inventory(fixture.commit, "Modules/module-one")

    def test_git_inventory_enforces_file_count_per_file_and_total_byte_limits(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            tree = GitSourceTree(fixture.root)
            object_id = b"a" * 40
            first = b"100644 blob " + object_id + b" 3\tModules/module-one/a.bin\x00"
            second = b"100644 blob " + object_id + b" 3\tModules/module-one/b.bin\x00"
            cases = (
                ("_MAX_SOURCE_FILES", 1, first + second, "file-count"),
                ("_MAX_SOURCE_FILE_BYTES", 2, first, "oversized"),
                ("_MAX_SOURCE_TOTAL_BYTES", 2, first, "total-byte"),
            )
            for constant, value, listing, message in cases:
                with self.subTest(limit=constant):
                    with mock.patch.object(SUBJECT, constant, value), mock.patch.object(tree, "_run", return_value=listing):
                        with self.assertRaisesRegex(ArtifactBuildError, message):
                            tree.inventory(fixture.commit, "Modules/module-one")

    def test_writable_or_hardlinked_existing_output_is_not_accepted_as_immutable(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            first_output = fixture.root / "first"
            descriptor = fixture.builder().build(fixture.request(), first_output)
            artifact = first_output / descriptor["artifact_file"]
            artifact.chmod(0o640)
            with self.assertRaisesRegex(ArtifactBuildError, "writable mode"):
                fixture.builder().build(fixture.request(), first_output)
            artifact.chmod(0o440)

            second_output = fixture.root / "second"
            second_output.mkdir()
            os.link(artifact, second_output / artifact.name)
            with self.assertRaisesRegex(ArtifactBuildError, "link count"):
                fixture.builder().build(fixture.request(), second_output)

    def test_duplicate_manifest_keys_fail_before_git_or_output(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = RepositoryFixture(directory)
            (fixture.module / "module.yaml").write_text(
                '{"module":{"id":"module-one","id":"module-one"},'
                '"paths":{"actualRoot":"Modules/module-one",'
                '"owned":["Modules/module-one/**"]}}',
                encoding="utf-8",
            )
            with self.assertRaisesRegex(ArtifactBuildError, "duplicate JSON key"):
                fixture.builder().build(fixture.request(), fixture.root / "out")
            self.assertFalse((fixture.root / "out").exists())

    @unittest.skipUnless(hasattr(os, "fork"), "POSIX crash-window test")
    def test_crash_before_and_after_atomic_link_never_publishes_partial_final_and_retry_recovers(self):
        payload = b"complete-content-addressed-payload"
        final_name = "artifact.bin"
        for stage, exit_code in (("before-link", 71), ("after-link", 72)):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as directory:
                output = Path(directory).resolve() / "out"
                output.mkdir()
                child = os.fork()
                if child == 0:
                    try:
                        with SUBJECT._SecureDirectory(output, create=False, label="test output") as secure:
                            original_link = SUBJECT.os.link

                            def crash_link(*args, **kwargs):
                                if stage == "after-link":
                                    original_link(*args, **kwargs)
                                os._exit(exit_code)

                            SUBJECT.os.link = crash_link
                            SUBJECT._write_immutable_at(
                                secure.fd,
                                final_name,
                                payload,
                                max_bytes=len(payload),
                            )
                    except BaseException:
                        os._exit(99)
                    os._exit(98)

                _, status = os.waitpid(child, 0)
                self.assertTrue(os.WIFEXITED(status))
                self.assertEqual(exit_code, os.WEXITSTATUS(status))
                final = output / final_name
                if stage == "before-link":
                    self.assertFalse(final.exists())
                else:
                    self.assertTrue(final.exists())
                    self.assertEqual(2, final.stat().st_nlink)

                with SUBJECT._SecureDirectory(output, create=False, label="test output") as secure:
                    digest = SUBJECT._write_immutable_at(
                        secure.fd,
                        final_name,
                        payload,
                        max_bytes=len(payload),
                    )
                self.assertEqual(hashlib.sha256(payload).hexdigest(), digest)
                self.assertEqual(payload, final.read_bytes())
                self.assertEqual(1, final.stat().st_nlink)
                self.assertEqual(0, final.stat().st_mode & 0o222)
                self.assertFalse(any(item.name.startswith("._dps_tmp_") for item in output.iterdir()))

    @unittest.skipUnless(hasattr(os, "fork"), "POSIX concurrent-writer test")
    def test_concurrent_duplicate_writers_serialize_to_one_exact_final(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve() / "out"
            output.mkdir()
            payload = b"same-concurrent-payload"
            final_name = "artifact.bin"
            children = []
            starts = []
            for index in range(4):
                read_fd, write_fd = os.pipe()
                child = os.fork()
                if child == 0:
                    try:
                        os.close(write_fd)
                        if os.read(read_fd, 1) != b"x":
                            os._exit(90)
                        os.close(read_fd)
                        with SUBJECT._SecureDirectory(output, create=False, label="test output") as secure:
                            digest = SUBJECT._write_immutable_at(
                                secure.fd,
                                final_name,
                                payload,
                                max_bytes=len(payload),
                            )
                        if digest != hashlib.sha256(payload).hexdigest():
                            os._exit(91)
                    except BaseException:
                        os._exit(92)
                    os._exit(0)
                os.close(read_fd)
                children.append(child)
                starts.append(write_fd)

            for descriptor in starts:
                os.write(descriptor, b"x")
                os.close(descriptor)
            exit_statuses = []
            for child in children:
                _, status = os.waitpid(child, 0)
                self.assertTrue(os.WIFEXITED(status))
                exit_statuses.append(os.WEXITSTATUS(status))
            self.assertEqual([0, 0, 0, 0], exit_statuses)

            final = output / final_name
            self.assertEqual(payload, final.read_bytes())
            self.assertEqual(1, final.stat().st_nlink)
            self.assertEqual(0, final.stat().st_mode & 0o222)
            self.assertFalse(any(item.name.startswith("._dps_tmp_") for item in output.iterdir()))


if __name__ == "__main__":
    unittest.main()
