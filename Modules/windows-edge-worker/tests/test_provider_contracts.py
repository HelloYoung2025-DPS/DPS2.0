import base64
import copy
import json
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker


MODULE_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = MODULE_ROOT.parents[1]


def assert_owner_corpus(
    test: unittest.TestCase, corpus_path: Path, expected_case_ids: set[str]
) -> None:
    corpus = json.loads(corpus_path.read_text(encoding="utf-8"))
    test.assertEqual("dps.contract-corpus/v1", corpus["corpus_version"])
    schema = json.loads(
        (corpus_path.parent / corpus["schema_file"]).read_text(encoding="utf-8")
    )
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema, format_checker=FormatChecker())
    if "base_file" in corpus:
        base = json.loads(
            (corpus_path.parent / corpus["base_file"]).read_text(encoding="utf-8")
        )
    else:
        base = corpus["base_instance"]
    case_ids = [case["id"] for case in corpus["cases"]]
    test.assertEqual(len(case_ids), len(set(case_ids)))
    test.assertEqual(expected_case_ids, set(case_ids))
    for case in corpus["cases"]:
        with test.subTest(owner=corpus["owner_module"], case=case["id"]):
            instance = copy.deepcopy(base)
            instance.update(case["patch"])
            errors = list(validator.iter_errors(instance))
            test.assertEqual(case["expected"] == "FAIL", bool(errors))


class WindowsEdgeWorkerProviderContractTests(unittest.TestCase):
    def test_worker_consumes_exact_owner_corpora_without_schema_forks(self) -> None:
        supervisor = (
            REPOSITORY_ROOT
            / "Modules"
            / "windows-edge-supervisor"
            / "contracts"
            / "provided"
        )
        journal = (
            REPOSITORY_ROOT
            / "Modules"
            / "edge-local-journal"
            / "contracts"
            / "provided"
        )
        corpora = {
            supervisor / "edge.worker.exchange.v1.corpus.json": {
                "canonical-command",
                "request-digest-terminal-newline",
                "occurred-at-nonzero-offset",
                "occurred-at-year-zero",
                "occurred-at-leap-second",
                "lease-nonzero-offset",
                "lease-terminal-newline",
                "trace-terminal-newline",
            },
            supervisor / "edge.worker.drain.directive.v1.corpus.json": {
                "complete-signed-directive-shape",
                "unknown-signature-algorithm",
                "wrong-producer",
                "wrong-slot",
                "negative-epoch",
                "drain-id-terminal-newline",
                "unknown-field",
                "bom-uppercase",
                "key-id-uppercase",
                "signature-terminal-newline",
                "timestamp-z-not-exact",
                "worker-version-unicode",
            },
            supervisor / "edge.worker.drain.receipt.v1.corpus.json": {
                "complete-worker-only-shape",
                "unknown-worker-algorithm",
                "incomplete-drain",
                "remaining-in-flight",
                "wrong-slot",
                "drain-id-terminal-newline",
                "journal-owned-field-rejected",
                "worker-statement-uppercase",
                "worker-artifact-uppercase",
                "signature-terminal-newline",
                "timestamp-z-is-not-exact-owner-wire",
                "worker-version-unicode",
            },
            journal / "edge.journal.append.v1.corpus.json": {
                "canonical-append",
                "producer-supervisor-rejected",
                "safe-ascii-command-and-entry-tokens",
                "command-id-terminal-newline",
                "entry-id-terminal-newline",
                "command-id-whitespace",
                "entry-id-slash",
                "payload-digest-terminal-newline",
                "entry-type-terminal-newline",
                "entry-type-too-short",
                "occurred-at-nonzero-offset",
                "occurred-at-year-zero",
                "occurred-at-leap-second",
            },
            journal / "edge.journal.receipt.v1.corpus.json": {
                "canonical-receipt",
                "request-producer-supervisor-rejected",
                "safe-ascii-command-and-entry-tokens",
                "command-id-terminal-newline",
                "entry-id-terminal-newline",
                "command-id-whitespace",
                "entry-id-slash",
                "sequence-int64-max",
                "sequence-int64-overflow",
                "previous-checksum-terminal-newline",
                "entry-checksum-terminal-newline",
                "occurred-at-nonzero-offset",
            },
        }
        for path, expected_case_ids in corpora.items():
            assert_owner_corpus(self, path, expected_case_ids)

    def test_worker_binds_supervisor_owned_drain_auth_profile(self) -> None:
        contract_root = (
            REPOSITORY_ROOT
            / "Modules"
            / "windows-edge-supervisor"
            / "contracts"
            / "provided"
        )
        schema = json.loads(
            (contract_root / "edge.worker.drain.receipt.v1.schema.json").read_text(
                encoding="utf-8"
            )
        )
        auth = json.loads(
            (contract_root / "edge.worker.drain.receipt.v1.auth.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual("edge.worker.drain.receipt.v1.auth.json", schema["x-dps-auth-spec"])
        self.assertEqual("windows-edge-supervisor", auth["owner_module"])
        self.assertEqual("edge.worker.drain.receipt/v1", auth["contract_id"])
        self.assertEqual("RSA_PSS_SHA256", auth["signature_algorithm"])
        self.assertEqual(
            "dps.windows-edge-worker.durable-drain-receipt/v1",
            auth["worker_statement"]["domain"],
        )
        self.assertEqual(
            [
                "schema_version", "contract_id", "producer_module", "soul_id",
                "device_binding_id", "platform_account_id", "trace_id",
                "idempotency_key", "occurred_at", "privacy_class", "drain_id",
                "slot", "worker_version", "worker_artifact_sha256",
                "journal_artifact_sha256", "release_bom_sha256",
                "protected_policy_sha256", "routing_epoch", "intake_stopped",
                "worker_drained", "remaining_in_flight", "issued_at", "not_before",
                "expires_at",
            ],
            auth["worker_statement"]["fields"],
        )
        self.assertEqual(
            "serialize once after RSA-PSS signing, persist exact UTF-8 bytes, and return the same bytes for every retry",
            auth["worker_receipt_wire"]["rule"],
        )
        self.assertEqual(
            "the Worker appends the exact persisted wire digest; Journal treats it as opaque durable data and signs it in the independent rich attestation",
            auth["journal_payload"]["rule"],
        )

    def test_worker_binds_supervisor_owned_signed_drain_directive_profile(self) -> None:
        contract_root = (
            REPOSITORY_ROOT
            / "Modules"
            / "windows-edge-supervisor"
            / "contracts"
            / "provided"
        )
        schema = json.loads(
            (contract_root / "edge.worker.drain.directive.v1.schema.json").read_text(
                encoding="utf-8"
            )
        )
        auth = json.loads(
            (contract_root / "edge.worker.drain.directive.v1.auth.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(
            "edge.worker.drain.directive.v1.auth.json", schema["x-dps-auth-spec"]
        )
        self.assertEqual("windows-edge-supervisor", auth["owner_module"])
        self.assertEqual("windows-edge-supervisor", auth["producer_module"])
        self.assertEqual("edge.worker.drain.directive/v1", auth["contract_id"])
        self.assertEqual("RSA_PSS_SHA256", auth["signature_algorithm"])
        self.assertEqual(
            "dps.windows-edge-supervisor.drain-directive/v1",
            auth["statement"]["domain"],
        )
        self.assertIn("free-text edge.worker.exchange DRAIN is rejected", auth["verification_rule"])

    def test_worker_binds_final_owner_checksum_profile(self) -> None:
        profile_path = (
            REPOSITORY_ROOT
            / "Modules"
            / "edge-local-journal"
            / "contracts"
            / "provided"
            / "edge.journal.checksum.v1.json"
        )
        profile = json.loads(profile_path.read_text(encoding="utf-8"))
        self.assertEqual("edge.journal.checksum/v1", profile["profile_id"])
        self.assertEqual("edge-local-journal", profile["owner_module"])
        self.assertEqual("dps.length-prefixed-utf8/v1", profile["encoding"])
        self.assertEqual("strict-utf8-without-bom", profile["text_encoding"])
        self.assertEqual("invariant-decimal-text", profile["integer_encoding"])
        self.assertEqual("big-endian", profile["byte_order"])
        self.assertEqual(
            [
                "domain_byte_length:u32",
                "domain:utf8",
                "field_count:u32",
                "repeat(field_byte_length:u32,field:utf8)",
            ],
            profile["layout"],
        )
        self.assertEqual(
            "dps.edge-local-journal.identity-sha256/v1",
            profile["domains"]["identity_sha256"]["domain"],
        )
        self.assertEqual(
            [
                "schema_version",
                "contract_id",
                "producer_module",
                "command_id",
                "entry_id",
                "entry_type",
                "trace_id",
                "idempotency_key",
                "privacy_class",
                "soul_id",
                "device_binding_id",
                "platform_account_id",
                "occurred_at",
                "payload_sha256",
                "canonical_payload_json",
            ],
            profile["domains"]["identity_sha256"]["fields"],
        )
        self.assertEqual(
            "dps.edge-local-journal.entry-sha256/v1",
            profile["domains"]["entry_checksum"]["domain"],
        )
        self.assertEqual(
            [
                "sequence",
                "previous_checksum",
                "schema_version",
                "contract_id",
                "producer_module",
                "command_id",
                "entry_id",
                "entry_type",
                "trace_id",
                "idempotency_key",
                "privacy_class",
                "soul_id",
                "device_binding_id",
                "platform_account_id",
                "occurred_at",
                "payload_sha256",
                "checksum_encoding",
                "identity_sha256",
            ],
            profile["domains"]["entry_checksum"]["fields"],
        )
        self.assertEqual(
            {
                "field": "checksum_encoding",
                "value": "dps.length-prefixed-utf8/v1",
                "missing_or_unknown": "reject",
            },
            profile["journal_line_discriminator"],
        )

    def test_worker_quarantines_executor_owned_native_stop_proof_v1(self) -> None:
        schema_path = (
            REPOSITORY_ROOT
            / "Modules"
            / "executor-gateway"
            / "contracts"
            / "provided"
            / "native.stop.proof.v1.schema.json"
        )
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        self.assertEqual("native.stop.proof/v1", schema["title"])
        self.assertEqual(False, schema["additionalProperties"])
        self.assertEqual(
            [
                "NATIVE_NOT_STARTED",
                "NATIVE_TRANSPORT_ABORTED",
                "NATIVE_WORKER_PROCESS_EXITED",
            ],
            schema["properties"]["stop_kind"]["enum"],
        )
        self.assertIn("occurred_at", schema["required"])
        self.assertNotIn("confirmed_at", schema["properties"])

        canonical = {
            "schema_version": "1.0.0",
            "contract_id": "native.stop.proof/v1",
            "producer_module": "windows-edge-worker",
            "stopped": True,
            "submission_attempt_id": "7b000000-0000-0000-0000-00000000000b",
            "command_id": "71000000-0000-0000-0000-000000000001",
            "lease_id": "72000000-0000-0000-0000-000000000002",
            "attempt": 1,
            "native_request_binding_sha256": "1" * 64,
            "submitted_request_sha256": "2" * 64,
            "soul_id": "soul_" + "3" * 64,
            "device_binding_id": "db_" + "4" * 32,
            "platform_account_id": "pa_" + "5" * 32,
            "trace_id": "trace_" + "6" * 32,
            "idempotency_key": "idem_" + "7" * 64,
            "active_release_bom_sha256": "8" * 64,
            "active_release_bom_generation": 17,
            "active_release_bom_token_sha256": "9" * 64,
            "worker_instance_id": "wi_" + "a" * 32,
            "worker_generation": 23,
            "stop_kind": "NATIVE_TRANSPORT_ABORTED",
            "evidence_sha256": "b" * 64,
            "occurred_at": "2026-07-15T10:00:02.0000000Z",
            "privacy_class": "internal",
            "auth_scope": "executor-gateway.native-stop-proof",
            "key_id": "worker-native-stop-key-v1",
            "signature_base64": base64.b64encode(bytes(64)).decode("ascii"),
        }
        validator = Draft202012Validator(schema, format_checker=FormatChecker())
        self.assertEqual([], list(validator.iter_errors(canonical)))
        invalid_patches = {
            "not-stopped": {"stopped": False},
            "unknown-stop-kind": {"stop_kind": "UNKNOWN"},
            "zero-worker-generation": {"worker_generation": 0},
            "uppercase-key-id": {"key_id": "Worker-Key"},
            "wrong-signature-size": {"signature_base64": base64.b64encode(bytes(63)).decode("ascii")},
            "noncanonical-time": {"occurred_at": "2026-07-15T18:00:02.0000000+08:00"},
            "unknown-property": {"confirmed_at": "2026-07-15T10:00:02.0000000Z"},
        }
        for case_id, patch in invalid_patches.items():
            with self.subTest(case=case_id):
                instance = copy.deepcopy(canonical)
                instance.update(patch)
                self.assertNotEqual([], list(validator.iter_errors(instance)))

        manifest = json.loads((MODULE_ROOT / "module.yaml").read_text(encoding="utf-8"))
        declaration = next(
            item
            for item in manifest["contracts"]["consumed"]
            if item["contractId"] == "native.stop.proof" and item["major"] == 1
        )
        self.assertEqual("executor-gateway", declaration["ownerModule"])
        self.assertEqual("deprecated", declaration["status"])
        self.assertEqual("quarantine-only", declaration["mode"])
        self.assertFalse(
            any(
                edge["contractId"] == "native.stop.proof"
                for direction in ("inbound", "outbound")
                for edge in manifest["communication"][direction]
            )
        )

    def test_worker_has_no_local_copy_of_provider_schemas(self) -> None:
        local_provided = MODULE_ROOT / "contracts" / "provided"
        self.assertEqual([], list(local_provided.glob("*.schema.json")))


if __name__ == "__main__":
    unittest.main()
