import base64
import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


# R0-C: this suite migrated from Modules/factory-release-controller/tests/
# together with the validator itself (RebuildPlan 4.3) -- candidate validation
# is ordinary Tools/ci gate code and must survive that module's R0-D deletion.
ROOT = Path(__file__).resolve(strict=True).parents[2]
SOURCE_PATH = ROOT / "Tools" / "ci" / "candidate_bom_validator.py"
CANONICAL_NUMBER_CORPUS_PATH = (
    ROOT
    / "governance"
    / "verification"
    / "release-bom.canonical-number.v1.corpus.json"
)
CANONICAL_STRING_CORPUS_PATH = (
    ROOT
    / "governance"
    / "verification"
    / "release-bom.canonical-string.v1.corpus.json"
)
CANONICAL_NUMBER_CORPUS_SHA256 = (
    "14f115b4acb3b11e4cc97b4fd657eea6b112841b3ee7bdc6b293e9fae4add4d3"
)
CANONICAL_STRING_CORPUS_SHA256 = (
    "a7a132a48170ce6495af87706faa722670d4ceb856620436b5906e78d1ee42f9"
)
SUBJECT_NAME = "_dps_toolsci_candidate_bom_validator_subject"
RELEASE_BINDING_COMPAT_CORPUS_ROOT = (
    ROOT / "Tests" / "ci" / "fixtures" / "r0c_release_binding_compat"
)
FIXTURE_GIT_DATE = "2026-07-14T00:00:00Z"


def load_subject():
    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        return existing
    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, SOURCE_PATH)
    if spec is None or spec.loader is None:
        raise ImportError("unable to load candidate BOM validator")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    spec.loader.exec_module(subject)
    return subject


SUBJECT = load_subject()
CandidateBomError = SUBJECT.CandidateBomError
CandidateBomValidator = SUBJECT.CandidateBomValidator
canonical_bytes = SUBJECT.canonical_bytes
sha256_bytes = SUBJECT.sha256_bytes


KEYS = {
    "controller-key": (
        int("d558ac64db3f45412cda262c2d9bc4d28aa5cc0b3f0e839fdf6689809133f0a7e73fcda74f1222650189276dc5043cedb3e3026227dd366abcad140d562da829b83cf4578a4e7070100874a151552fc41e295d435e19df44b76a6704cb6df1c071fd7baf2834b28e02d6be84c3a6528d8bd501bdb8f7cbba32e7ac63c2102aa1", 16),
        int("7f39d4048922a0000fe93f9e54cc718144a13e9eee498f80c54e766d2f2a14376c9605e3e229644d6baf08ce531105ec92bbab6e316b9fc9e31e2bb9104d45dc14763250168949a3f3516fa690baebf60aa1ddf82cce3e0dc99483fc00b8e8fd60492b2432dc8e3b62ef0a7979b4bd46f63e632e7a83ca9f5d0b803c31bab3a9", 16),
    ),
    # TEST ONLY: this fixed synthetic 2048-bit key is not trusted by the
    # deployed policy.  Its private component exists solely so BomFixture can
    # produce deterministic unit-test signatures; it is absent from every
    # production artifact, signer contract, and compatibility corpus.
    "external-bom-test-key": (
        int("b9b9543f12b6e26e7adb927254d4f9059a8ec34440100df3b13d0783597205d38de65933ec8d53cb8845123c103f847cefc93cda9e66edda9d915c443fb716cef66a5453db6568a6b7487906462c246a340922c4a5b30f1306fd63fc8a6e226399afdb3bd5a2b5bc87b6892c3db86e2a6f715b81d7d10b2bfc22ced70078c9720d1dce9e23bc5d21a06314a0df5a1cd0edbf663a9606bd1b471e2b0344ae0b9090ac9800a082b576a3d96d2378380e88d6088ef294a9f7ad26ff066831ac5175c6468e6980fe68f7395ba9ebc35a9a84cd2f1a8e2a80a7d2899d0e47ab338285efe41d46c81234fd2c60ddefc9a3ed9de69138efb85bde58b435856c5155098d", 16),
        int("2b44b44e2279c58c782675fa996d699ba6dbaa3dd90ff47428ff5f23f87f72408c6f552a5deaba1231abe7d8e2ef2d5a5f11dad1d2f40767766ca25a8b1e885b8cb88e6f5fee83004e347def9a8317b3bf6e3e71a269f231dc5fc5bed4f05e2626acbbee7771da15b367711343c8d72f9f398158617383ff0a1580eb41a2a2497fa357daedf81539117967511812f493e2eb4417ace03d678b7fd1fc01063a1a1312ee3aa7f4aa4724d321c36b20c61117b4a6848746a4825bfe9500cdbc3d321e320202cfb02f579bbf1bc94d66cf5ef705e720e3a30228285da9e3bd5b541b980222c62e9181de4f7cebd7c2387c65f89cff77c470bb71efdb14d337bd5461", 16),
    ),
    "evidence-key": (
        int("b0ad7dfaf32a99daecfe456959a57e743610c8d811d9ae383d9d3dcc4da74adbce538ac052b6fa46faab70ccacfc65a6a4e2aa9496c8d97de366c4cda27391bca41f2bdae3ba5c224aa48a418967e7acc6c7135d85fb87e9957f46cb370f7e6cc7b73924bceb7da79bbaf74daecfa07c4435c602a9d42d2430421a2b1b1e51db", 16),
        int("48214c819bad14cb305e4ef047cd2ce73cfb7543e165c19eec68b9c6231ddd8e079a4bd760ed9b1847569ee2b0ed0a83126607c64a190dd23b78c5783e8e783ca149fadb570c3b2169e11ae125e6e7c36d6274fd296a62bd212809bdeec8c2a705cebee2bff0246ff1df123593438255924331e8cd4aa78a399ef11cb67c5e21", 16),
    ),
    "approval-key": (
        int("d1208ab034058dd97b9cd29667c9c8c597c5469b8d5d44edd0d7726ca8564d08ddd6d5e847f78185408bc30b542a40af97e0850c9e9d9c7db847c68fa40a5f1ae215934e3a7c42950c7a199dd50e9c91b0397afb779a7e7c4a85a336c265c91d2692fb74b130cad1c297cb5dc8afda24124fb9caf37882282f0727ad7294e70d", 16),
        int("6bf021e52454a18c5912ee5697273d2b4f5491470445d9a7ed9ce600533a87f4459bf73836bcf6eaf20fb1120dd4e922387fb7ec7589e015bba1c048af6073091bf9d6921de862d29816fc4958dbcb562e3e299f1343843cdafe71253068ab36ca94d7bc00419a32f7c4155797456c8f2fb75f871d84c4372d10536a8f7384b9", 16),
    ),
    "trust-receipt-key": (
        int("b228f416291a348c125b0a63a0563c48263ffebc9c020353b7723ad4652da3ebdc4209438b0c74bf4a0a3eef3de84eb6842b389d31c3cf57a6a1f0b771c143883b8872ce503f680ea9ba456d150e54a4580a2ad68e8ee1bc8c0602dcfc2564697d57fa02881411701cd7c75130d191f6e2922e507e04e48a382156baa0279203", 16),
        int("40267aed57d375c451ffb217f96dd3baeefaf0109de423aa8b4d785d6e2926f310963e9bb17fc4b1f449082c41105af76153e6c8c3588c5a3547ee533f84a579d898bbc0115036cc9f94b0e7c502db0ed3623b3939e60f96709458ee8a35dbd93de49d4c645254c2a03f416dc584067a134379c450d6229a22a7588c209615d1", 16),
    ),
}


def _mgf1(seed, length):
    output = bytearray()
    for counter in range((length + 31) // 32):
        output.extend(hashlib.sha256(seed + counter.to_bytes(4, "big")).digest())
    return bytes(output[:length])


def _sign(key_id, message):
    modulus, private_exponent = KEYS[key_id]
    em_bits = modulus.bit_length() - 1
    em_length = (em_bits + 7) // 8
    salt = hashlib.sha256(key_id.encode() + message).digest()
    encoded_hash = hashlib.sha256(
        b"\x00" * 8 + hashlib.sha256(message).digest() + salt
    ).digest()
    padding_length = em_length - len(encoded_hash) - len(salt) - 2
    data_block = b"\x00" * padding_length + b"\x01" + salt
    masked = bytearray(
        left ^ right for left, right in zip(data_block, _mgf1(encoded_hash, len(data_block)))
    )
    unused_bits = 8 * em_length - em_bits
    if unused_bits:
        masked[0] &= 0xFF >> unused_bits
    encoded = bytes(masked) + encoded_hash + b"\xbc"
    raw = pow(int.from_bytes(encoded, "big"), private_exponent, modulus).to_bytes(
        (modulus.bit_length() + 7) // 8, "big"
    )
    return {
        "algorithm": "rsa-pss-sha256",
        "key_id": key_id,
        "value": base64.b64encode(raw).decode("ascii"),
    }


def _noncanonical_base64_pad_alias(value):
    alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/"
    padding = len(value) - len(value.rstrip("="))
    if padding not in (1, 2):
        raise AssertionError("fixture signature must contain Base64 padding")
    index = len(value) - padding - 1
    replacement = alphabet[alphabet.index(value[index]) ^ 1]
    alias = value[:index] + replacement + value[index + 1:]
    if base64.b64decode(alias, validate=True) != base64.b64decode(
        value, validate=True
    ):
        raise AssertionError("constructed Base64 alias changed decoded bytes")
    return alias


def _git(root, *args, environment=None):
    process_environment = None
    if environment is not None:
        process_environment = dict(os.environ)
        process_environment.update(environment)
    result = subprocess.run(
        ["git", "-C", str(root), *args], check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        env=process_environment,
    )
    return result.stdout.decode().strip()


def _write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = canonical_bytes(value) + b"\n"
    path.write_bytes(payload)
    return payload


def _write_bom_wire(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = canonical_bytes(value)
    path.write_bytes(payload)
    return payload


def _write_receipt_wire(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = canonical_bytes(value)
    path.write_bytes(payload)
    return payload


MODULE_SPECS = {
    "policy-approval": ("dps.policy-approval", "assembly", "modular-monolith"),
    "windows-edge-supervisor": (
        "dps.windows-edge-supervisor", "service", "out-of-process",
    ),
    "windows-edge-worker": ("dps.windows-edge-worker", "service", "out-of-process"),
}

PRODUCTION_BUILDER_ID = "dps:factory-artifact-builder:0.1.0"


class BomFixture:
    def __init__(
        self,
        root,
        *,
        candidate_activation_token_sha256="b" * 64,
        previous_activation_token_sha256="a" * 64,
        bom_signing_key_id="external-bom-test-key",
        bom_signer_identity="external-release-signer",
    ):
        root = Path(root)
        for label, digest in (
            ("candidate activation token", candidate_activation_token_sha256),
            ("previous activation token", previous_activation_token_sha256),
        ):
            if (
                not isinstance(digest, str)
                or len(digest) != 64
                or any(character not in "0123456789abcdef" for character in digest)
            ):
                raise ValueError(f"{label} SHA-256 must be lowercase hexadecimal")
        if bom_signing_key_id not in KEYS:
            raise ValueError("BOM signing key is not available to this test fixture")
        if bom_signing_key_id == "controller-key":
            raise ValueError(
                "BOM signing key must be separate from the artifact signing key"
            )
        self.bom_signing_key_id = bom_signing_key_id
        self.bom_signer_identity = bom_signer_identity
        self.candidate_activation_token_sha256 = candidate_activation_token_sha256
        self.previous_activation_token_sha256 = previous_activation_token_sha256
        self.repo = root / "repo"
        self.bundle = root / "bundle"
        self.repo.mkdir()
        self.bundle.mkdir()
        schema_source = ROOT / "governance" / "schemas" / "release-bom.schema.json"
        schema_path = self.repo / "governance" / "schemas" / "release-bom.schema.json"
        schema_path.parent.mkdir(parents=True)
        schema_path.write_bytes(schema_source.read_bytes())
        (self.repo / "AGENTS.md").write_text("# Root rules\n", encoding="utf-8")
        self.module_roots = {}
        for module_id, (artifact_id, kind, boundary) in MODULE_SPECS.items():
            module_root = self.repo / "Modules" / module_id
            module_root.mkdir(parents=True)
            (module_root / "AGENTS.md").write_text(f"# {module_id} rules\n", encoding="utf-8")
            manifest = {
                "module": {"id": module_id, "version": "1.0.0"},
                "artifacts": [{
                    "id": artifact_id, "kind": kind, "status": "buildable",
                    "build": "fixture-build", "versioning": "semver",
                }],
                "runtime": {"processBoundary": boundary},
                "contracts": {"provided": [], "consumed": []},
                "dependencies": [],
            }
            _write_json(module_root / "module.yaml", manifest)
            self.module_roots[module_id] = module_root
        graph_root = self.repo / "governance" / "modules"
        graph_root.mkdir(parents=True)
        nodes = sorted(MODULE_SPECS)
        _write_json(graph_root / "dependency-graph.yaml", {
            "schemaVersion": "dps.dependency-graph/v1",
            "generatedFrom": "Modules/*/module.yaml", "failOnCycle": True,
            "nodes": nodes, "edges": [], "parallelWaves": [nodes],
        })
        _write_json(graph_root / "compatibility.yaml", {
            "schemaVersion": "dps.compatibility-matrix/v1",
            "generatedFrom": "Modules/*/module.yaml",
            "policyRef": "governance/policies/compatibility-policy.yaml",
            "unknownMajorBehavior": "reject", "contracts": [],
            "requiredCombinations": ["N/N", "N/N-1", "N-1/N", "N-1/N-1"],
            "unknownNPlus1": "REJECT",
        })
        _git(self.repo, "init", "-q")
        _git(self.repo, "config", "user.email", "release-tests@dps.invalid")
        _git(self.repo, "config", "user.name", "DPS Release Tests")
        _git(self.repo, "add", ".")
        _git(
            self.repo,
            "commit",
            "-q",
            "-m",
            "authority fixture",
            environment={
                "GIT_AUTHOR_DATE": FIXTURE_GIT_DATE,
                "GIT_COMMITTER_DATE": FIXTURE_GIT_DATE,
            },
        )
        self.commit = _git(self.repo, "rev-parse", "HEAD")
        self.schema_sha = sha256_bytes(schema_path.read_bytes())
        self.root_agents_sha = sha256_bytes((self.repo / "AGENTS.md").read_bytes())
        self.dag_sha = sha256_bytes((graph_root / "dependency-graph.yaml").read_bytes())
        self.compat_sha = sha256_bytes((graph_root / "compatibility.yaml").read_bytes())
        self.modules = [self._build_module(module_id) for module_id in sorted(MODULE_SPECS)]
        self.policy = self._policy()
        self.policy_sha = sha256_bytes(canonical_bytes(self.policy))
        self.evidence_receipt = {
            "schema_version": "dps.release-evidence/v1",
            "evidence_id": "gate-integration-001", "result": "PASS",
            "required": True, "kind": "INTEGRATION", "tested_commit": self.commit,
            "verification_level": "INTEGRATION_VERIFIED",
            "issuer_identity": "evidence-issuer",
        }
        self.evidence_bytes = _write_json(
            self.bundle / "evidence" / "gate-integration-001.json",
            self.evidence_receipt,
        )
        self.bom = self._make_bom(
            2,
            self.candidate_activation_token_sha256,
            "candidate-bom-001",
            "SIGNED",
        )
        self._write_approval(self.bom)
        self.previous = copy.deepcopy(self.bom)
        self.previous.update({
            "bom_id": "previous-bom-001", "status": "STABLE",
            "release_bom_generation": 1,
            "activation_token_sha256": self.previous_activation_token_sha256,
            "previous_stable_bom": None, "previous_stable_bom_sha256": None,
        })
        self.previous.update(
            self._authorities(1, self.previous_activation_token_sha256)
        )
        self.previous["signature"] = self._sign_bom(self.previous)
        self.previous_path = self.bundle / "previous-bom.json"
        self.previous_bytes = _write_bom_wire(self.previous_path, self.previous)
        self.bom["previous_stable_bom"] = self.previous["bom_id"]
        self.bom["previous_stable_bom_sha256"] = sha256_bytes(self.previous_bytes)
        self.bom_path = self.bundle / "candidate-bom.json"
        self.receipt_path = self.bundle / "authority-trust-receipt.json"
        self.rewrite_candidate(update_scope=False)

    def _build_module(self, module_id):
        root = self.module_roots[module_id]
        artifact_id, _, _ = MODULE_SPECS[module_id]
        artifact = f"signed-{module_id}-artifact".encode()
        artifact_sha = sha256_bytes(artifact)
        artifact_name = f"{module_id}.bin"
        artifact_path = self.bundle / "artifacts" / artifact_name
        artifact_path.parent.mkdir(parents=True, exist_ok=True)
        artifact_path.write_bytes(artifact)
        agents_sha = sha256_bytes((root / "AGENTS.md").read_bytes())
        manifest_sha = sha256_bytes((root / "module.yaml").read_bytes())
        source_files = []
        for name in ("AGENTS.md", "module.yaml"):
            path = root / name
            raw = path.read_bytes()
            source_files.append({
                "path": f"Modules/{module_id}/{name}", "sha256": sha256_bytes(raw),
                "size_bytes": len(raw), "mode": "100644",
            })
        source_files.sort(key=lambda item: item["path"])
        source_tree_sha = sha256_bytes(canonical_bytes(source_files))
        metadata_prefix = f"metadata/{module_id}"
        sbom = {
            "spdxVersion": "SPDX-2.3", "dataLicense": "CC0-1.0",
            "SPDXID": "SPDXRef-DOCUMENT", "name": artifact_name,
            "documentNamespace": f"https://dps.local/spdx/{artifact_sha}",
            "creationInfo": {"created": "2026-07-14T00:00:00Z", "creators": ["Tool: dps-test"]},
            "packages": [{
                "name": module_id, "SPDXID": "SPDXRef-Package", "versionInfo": "1.0.0",
                "downloadLocation": "NOASSERTION", "filesAnalyzed": True,
                "checksums": [{"algorithm": "SHA256", "checksumValue": artifact_sha}],
            }],
            "files": [{
                "fileName": item["path"], "SPDXID": f"SPDXRef-File-{index}",
                "checksums": [{"algorithm": "SHA256", "checksumValue": item["sha256"]}],
            } for index, item in enumerate(source_files, 1)],
            "relationships": [{
                "spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES",
                "relatedSpdxElement": "SPDXRef-Package",
            }],
        }
        provenance = {
            "_type": "https://in-toto.io/Statement/v1",
            "subject": [{"name": artifact_name, "digest": {"sha256": artifact_sha}}],
            "predicateType": "https://slsa.dev/provenance/v1",
            "predicate": {
                "buildDefinition": {
                    "buildType": "https://dps.local/build/module-artifact/v1",
                    "externalParameters": {
                        "module_id": module_id, "module_version": "1.0.0",
                        "integration_commit": self.commit,
                        "source_tree_sha256": source_tree_sha,
                    },
                    "internalParameters": {"merge_decision_id": "merge-" + "1" * 32},
                    "resolvedDependencies": source_files,
                },
                "runDetails": {
                    "builder": {"id": PRODUCTION_BUILDER_ID},
                    "metadata": {
                        "invocationId": f"build-{module_id}",
                        "startedOn": "2026-07-14T00:00:00Z",
                        "finishedOn": "2026-07-14T00:00:00Z",
                    },
                },
            },
        }
        sbom_bytes = _write_json(self.bundle / f"{metadata_prefix}.spdx.json", sbom)
        provenance_bytes = _write_json(
            self.bundle / f"{metadata_prefix}.provenance.json", provenance
        )
        descriptor = {
            "schema_version": "1.0.0", "contract_id": "artifact.descriptor/v1",
            "producer_module": "factory-artifact-builder", "soul_id": None,
            "device_binding_id": None, "platform_account_id": None,
            "trace_id": "trace_" + hashlib.sha256(module_id.encode()).hexdigest()[:32],
            "idempotency_key": "idem_" + artifact_sha, "occurred_at": "2026-07-14T00:00:00Z",
            "privacy_class": "internal", "artifact_id": "artifact-" + artifact_sha[:32],
            "build_id": "build-" + module_id, "module_id": module_id,
            "module_version": "1.0.0", "integration_commit": self.commit,
            "artifact_uri": f"sha256:{artifact_sha}", "artifact_file": artifact_name,
            "artifact_sha256": artifact_sha, "size_bytes": len(artifact),
            "merge_decision_id": "merge-" + "1" * 32,
            "trusted_merge_policy_sha256": "2" * 64,
            "source_tree_sha256": source_tree_sha, "agents_sha256": agents_sha,
            "manifest_sha256": manifest_sha,
            "sbom": {"path": f"{module_id}.spdx.json", "sha256": sha256_bytes(sbom_bytes), "media_type": "application/json"},
            "provenance": {"path": f"{module_id}.provenance.json", "sha256": sha256_bytes(provenance_bytes), "media_type": "application/json"},
            "signature": {"status": "UNSIGNED_AWAITING_EXTERNAL_SIGNER", "signer_required": "external-controlled-signer"},
        }
        descriptor_bytes = _write_json(
            self.bundle / f"{metadata_prefix}.descriptor.json", descriptor
        )
        entry = {
            "module_id": module_id, "version": "1.0.0",
            "artifact_uri": f"artifacts/{artifact_name}", "sha256": artifact_sha,
            "signature": None,
            "descriptor_uri": f"{metadata_prefix}.descriptor.json",
            "descriptor_sha256": sha256_bytes(descriptor_bytes),
            "sbom_uri": f"{metadata_prefix}.spdx.json", "sbom_sha256": sha256_bytes(sbom_bytes),
            "provenance_uri": f"{metadata_prefix}.provenance.json",
            "provenance_sha256": sha256_bytes(provenance_bytes),
            "agents_sha256": agents_sha, "manifest_sha256": manifest_sha,
        }
        entry["signature"] = _sign(
            "controller-key",
            b"dps-module-artifact-bom-entry/v1\n"
            + canonical_bytes({key: value for key, value in entry.items() if key != "signature"}),
        )
        return entry

    def _policy(self):
        identities = {
            "controller-key": ("release-controller", ["artifact"]),
            "evidence-key": ("evidence-issuer", ["evidence"]),
            "approval-key": ("human-approver", ["approval"]),
            "trust-receipt-key": ("authority-trust-signer", ["native-stop-trust"]),
        }
        identities[self.bom_signing_key_id] = (
            self.bom_signer_identity,
            ["bom"],
        )
        release_controllers = ["release-controller"]
        if self.bom_signer_identity not in release_controllers:
            release_controllers.append(self.bom_signer_identity)
        return {
            "schema_version": "dps.release-trust-policy/v1",
            "policy_id": "release-policy-001",
            "required_gates": {"gate-integration-001": {
                "kind": "INTEGRATION", "minimum_verification_level": "INTEGRATION_VERIFIED",
            }},
            "implementer_identities": ["module-builder"],
            "evidence_issuer_identities": ["evidence-issuer"],
            "release_controller_identities": release_controllers,
            "release_approver_identities": ["human-approver"],
            "native_stop_trust_signer_identities": ["authority-trust-signer"],
            "allow_bootstrap": False,
            "keys": [{
                "key_id": key_id, "identity": identity, "algorithm": "rsa-pss-sha256",
                "modulus_hex": format(KEYS[key_id][0], "x"), "exponent": 65537,
                "purposes": purposes,
            } for key_id, (identity, purposes) in identities.items()],
        }

    def _authorities(self, generation, token):
        by_id = {item["module_id"]: item for item in self.modules}
        native = {
            "authority_id": "native-stop-authority-a", "producer_module": "windows-edge-worker",
            "worker_module_id": "windows-edge-worker", "worker_artifact_id": "dps.windows-edge-worker",
            "worker_artifact_sha256": by_id["windows-edge-worker"]["sha256"],
            "worker_version": "1.0.0", "worker_slot": "A",
            "worker_instance_id": "wi_" + "1" * 32, "worker_generation": 7,
            "key_id": "worker-native-stop-key-a", "p256_spki_sha256": "3" * 64,
            "signature_algorithm": "ECDSA_P256_SHA256",
            "signature_format": "IEEE_P1363_FIXED_FIELD",
            "auth_scope": "policy-approval:native-stop-proof:v2:commit-unknown",
            "native_stop_contract_id": "native.stop.proof/v2", "policy_id": "RESULT-VERIFY-001",
            "release_bom_generation": generation, "activation_token_sha256": token,
            "rotation_epoch": 5, "valid_from": "2026-07-01T00:00:00.0000001Z",
            "valid_until": "2026-07-31T00:00:00.0000001Z", "revoked": False,
            "worker_authority_sha256": "0" * 64,
        }
        native["worker_authority_sha256"] = SUBJECT._canonical_authority_hash(native)
        route_spki = "4" * 64
        route = {
            "route_authority_id": "device-route-authority-a",
            "producer_module": "factory-release-controller",
            "supervisor_module_id": "windows-edge-supervisor",
            "supervisor_artifact_id": "dps.windows-edge-supervisor",
            "supervisor_artifact_sha256": by_id["windows-edge-supervisor"]["sha256"],
            "supervisor_version": "1.0.0", "supervisor_instance_id": "si_" + "2" * 32,
            "supervisor_generation": 8, "route_signer_key_id": "p256_spki_" + route_spki,
            "route_signer_p256_spki_sha256": route_spki,
            "signature_algorithm": "ECDSA_P256_SHA256",
            "signature_format": "IEEE_P1363_FIXED_FIELD_LOW_S",
            "auth_scope": "windows-edge-supervisor:device-route-assignment:issue",
            "policy_id": "SOUL-ISO-001", "release_bom_generation": generation,
            "activation_token_sha256": token, "rotation_epoch": 6,
            "valid_from": "2026-07-01T00:00:00.0000002Z",
            "valid_until": "2026-07-31T00:00:00.0000002Z", "revoked": False,
            "route_authority_sha256": "0" * 64,
        }
        route["route_authority_sha256"] = SUBJECT._canonical_route_authority_hash(route)
        challenge = {
            "authority_id": "native-stop-challenge-authority-a",
            "producer_module": "policy-approval", "policy_module_id": "policy-approval",
            "policy_artifact_id": "dps.policy-approval",
            "policy_artifact_sha256": by_id["policy-approval"]["sha256"],
            "policy_version": "1.0.0", "policy_instance_id": "pi_" + "3" * 32,
            "policy_generation": 9, "key_id": "policy-challenge-key-a",
            "p256_spki_sha256": "5" * 64, "signature_algorithm": "ECDSA_P256_SHA256",
            "signature_format": "IEEE_P1363_FIXED_FIELD_LOW_S",
            "auth_scope": "policy-approval:native-stop-challenge:v1:issue",
            "native_stop_challenge_contract_id": "native.stop.challenge/v1",
            "policy_id": "NATIVE-STOP-CHALLENGE-001",
            "release_bom_generation": generation, "activation_token_sha256": token,
            "rotation_epoch": 7, "valid_from": "2026-07-01T00:00:00.0000003Z",
            "valid_until": "2026-07-31T00:00:00.0000003Z", "revoked": False,
            "challenge_authority_sha256": "0" * 64,
        }
        challenge["challenge_authority_sha256"] = SUBJECT._canonical_challenge_authority_hash(challenge)
        return {
            "native_stop_authorities": [native],
            "device_route_assignment_authorities": [route],
            "native_stop_challenge_authorities": [challenge],
        }

    def _scope(self, bom):
        return {
            "integration_commit": bom["integration_commit"],
            "modules": [{"module_id": item["module_id"], "version": item["version"], "sha256": item["sha256"]}
                        for item in sorted(bom["modules"], key=lambda value: value["module_id"])],
            "contracts": [], "database_versions": bom["database_versions"],
            "feature_flags": bom["feature_flags"], "kill_switches": bom["kill_switches"],
            "release_bom_generation": bom["release_bom_generation"],
            "activation_token_sha256": bom["activation_token_sha256"],
            "native_stop_authorities_sha256": SUBJECT._canonical_authorities_hash(bom["native_stop_authorities"]),
            "device_route_assignment_authorities_sha256": SUBJECT._canonical_route_authorities_hash(bom["device_route_assignment_authorities"]),
            "native_stop_challenge_authorities_sha256": SUBJECT._canonical_challenge_authorities_hash(bom["native_stop_challenge_authorities"]),
        }

    def _make_bom(self, generation, token, bom_id, status):
        evidence = {
            "evidence_id": "gate-integration-001", "artifact_uri": "evidence/gate-integration-001.json",
            "sha256": sha256_bytes(self.evidence_bytes), "result": "PASS", "required": True,
            "kind": "INTEGRATION", "tested_commit": self.commit,
            "verification_level": "INTEGRATION_VERIFIED", "issuer_identity": "evidence-issuer",
            "signature": _sign("evidence-key", b"dps-release-evidence/v1\n" + self.evidence_bytes),
        }
        authorities = self._authorities(generation, token)
        artifact_set = [{"module_id": item["module_id"], "sha256": item["sha256"]}
                        for item in sorted(self.modules, key=lambda value: value["module_id"])]
        bom = {
            "schema_version": "dps.release-bom/v1", "bom_id": bom_id, "status": status,
            "integration_commit": self.commit, "created_at": "2026-07-14T00:00:00Z",
            "release_bom_generation": generation, "activation_token_sha256": token,
            "modules": copy.deepcopy(self.modules),
            "instruction_hashes": [{"path": "AGENTS.md", "sha256": self.root_agents_sha}] + [{
                "path": f"Modules/{module_id}/AGENTS.md",
                "sha256": sha256_bytes((root / "AGENTS.md").read_bytes()),
            } for module_id, root in sorted(self.module_roots.items())],
            "contracts": [], "database_versions": {},
            "dependency_dag_sha256": self.dag_sha, "compatibility_matrix_sha256": self.compat_sha,
            "feature_flags": {"runtime_authority_v1": False},
            "kill_switches": {"native_stop": True},
            "ai_toolchain": {"models": {"planner": "test-v1"}, "prompts": {"release": "sha256:test"}, "tools": {"factory": "0.2.0"}},
            "evidence": [evidence], "risk": {"tier": "R3", "scope_sha256": "0" * 64, "requested_by": "module-builder"},
            "release_approval": {"required": True, "receipt_uri": None, "sha256": None, "approver_identity": None, "approver_role": "human-release-approver", "signature": None},
            "rollout": {"waves": ["shadow", "1"], "shadow_artifact_sha256": sha256_bytes(canonical_bytes(artifact_set)), "current_wave": "not-started"},
            "rollback": {"unit": "runtime-authority-group", "target_minutes": 5, "procedure": "stop-drain-switch-verify", "compensation_required": False},
            "previous_stable_bom": None, "previous_stable_bom_sha256": None,
            **authorities, "signature": None,
        }
        bom["risk"]["scope_sha256"] = sha256_bytes(canonical_bytes(self._scope(bom)))
        return bom

    def _write_approval(self, bom):
        scope_sha = sha256_bytes(canonical_bytes(self._scope(bom)))
        bom["risk"]["scope_sha256"] = scope_sha
        receipt = {
            "schema_version": "dps.release-approval/v1", "approval_id": "approval-001",
            "bom_id": bom["bom_id"], "integration_commit": self.commit, "risk_tier": "R3",
            "scope_sha256": scope_sha, "status": "APPROVED", "approver_identity": "human-approver",
            "approver_role": "human-release-approver", "approved_at": "2026-07-14T00:00:00Z",
        }
        raw = _write_json(self.bundle / "approvals" / "approval-001.json", receipt)
        bom["release_approval"] = {
            "required": True, "receipt_uri": "approvals/approval-001.json",
            "sha256": sha256_bytes(raw), "approver_identity": "human-approver",
            "approver_role": "human-release-approver",
            "signature": _sign("approval-key", b"dps-release-approval/v1\n" + raw),
        }

    def _sign_bom(self, bom):
        return _sign(self.bom_signing_key_id, b"dps-release-bom/v1\n" + canonical_bytes({
            key: value for key, value in bom.items() if key != "signature"
        }))

    def rewrite_candidate(self, *, update_scope=True, update_receipt=True):
        if update_scope:
            self._write_approval(self.bom)
        self.bom["signature"] = self._sign_bom(self.bom)
        bom_bytes = _write_bom_wire(self.bom_path, self.bom)
        if update_receipt:
            payload = SUBJECT.build_native_stop_trust_receipt_payload(
                self.bom, bom_bytes, self.policy["policy_id"],
                "native-stop-trust-" + "9" * 32, "trace_" + "8" * 32,
                "2026-07-14T00:00:00.0000001Z",
            )
            receipt = dict(payload)
            receipt["signature"] = _sign(
                "trust-receipt-key", SUBJECT.native_stop_trust_signing_bytes(payload)
            )
            _write_receipt_wire(self.receipt_path, receipt)

    def rehash_authorities(self):
        for authority in self.bom["native_stop_authorities"]:
            authority["worker_authority_sha256"] = SUBJECT._canonical_authority_hash(authority)
        for authority in self.bom["device_route_assignment_authorities"]:
            authority["route_authority_sha256"] = SUBJECT._canonical_route_authority_hash(authority)
        for authority in self.bom["native_stop_challenge_authorities"]:
            authority["challenge_authority_sha256"] = SUBJECT._canonical_challenge_authority_hash(authority)

    def rewrite_previous(self):
        for authority in self.previous["native_stop_authorities"]:
            authority["worker_authority_sha256"] = SUBJECT._canonical_authority_hash(
                authority
            )
        for authority in self.previous["device_route_assignment_authorities"]:
            authority["route_authority_sha256"] = (
                SUBJECT._canonical_route_authority_hash(authority)
            )
        for authority in self.previous["native_stop_challenge_authorities"]:
            authority["challenge_authority_sha256"] = (
                SUBJECT._canonical_challenge_authority_hash(authority)
            )
        self.previous["signature"] = self._sign_bom(self.previous)
        self.previous_bytes = _write_bom_wire(self.previous_path, self.previous)
        self.bom["previous_stable_bom_sha256"] = sha256_bytes(self.previous_bytes)
        self.rewrite_candidate(update_scope=False)

    def rewrite_module_builder_id(self, module_id, builder_id):
        module = next(item for item in self.bom["modules"] if item["module_id"] == module_id)
        provenance_path = self.bundle / module["provenance_uri"]
        provenance = json.loads(provenance_path.read_bytes())
        provenance["predicate"]["runDetails"]["builder"]["id"] = builder_id
        provenance_bytes = _write_json(provenance_path, provenance)
        module["provenance_sha256"] = sha256_bytes(provenance_bytes)

        descriptor_path = self.bundle / module["descriptor_uri"]
        descriptor = json.loads(descriptor_path.read_bytes())
        descriptor["provenance"]["sha256"] = module["provenance_sha256"]
        descriptor_bytes = _write_json(descriptor_path, descriptor)
        module["descriptor_sha256"] = sha256_bytes(descriptor_bytes)
        module["signature"] = _sign(
            "controller-key",
            b"dps-module-artifact-bom-entry/v1\n"
            + canonical_bytes({
                key: value for key, value in module.items() if key != "signature"
            }),
        )
        self.rewrite_candidate(update_scope=False)

    def validator(
        self,
        policy=None,
        policy_sha=None,
        validation_time="2026-07-14T00:00:00Z",
        minimum_remaining_lifetime_seconds=SUBJECT._DEFAULT_MINIMUM_REMAINING_LIFETIME_SECONDS,
    ):
        # validation_time is pinned to the fixture created_at so the suite is
        # deterministic; pass None to exercise the system-UTC-now default.
        value = self.policy if policy is None else policy
        digest = sha256_bytes(canonical_bytes(value)) if policy_sha is None else policy_sha
        return CandidateBomValidator(
            self.repo, self.bundle, value, digest, self.schema_sha,
            validation_time=validation_time,
            minimum_remaining_lifetime_seconds=minimum_remaining_lifetime_seconds,
        )

    def validate(self):
        return self.validator().validate(
            self.bom_path, self.previous_path, self.receipt_path
        )


class ReleaseCliEntryTests(unittest.TestCase):
    """TODAY-reachable behaviour of the actual release entry, main().

    scripts/release.sh invokes exactly this function; these tests pin what it
    does now, without pretending to be the owner-blocked happy path."""

    def test_the_cli_refuses_to_start_without_the_receipt_argument(self) -> None:
        # The argument release.sh forgot to pass until R0-C: argparse must hard-exit.
        with tempfile.TemporaryDirectory() as bundle:
            with contextlib.redirect_stderr(io.StringIO()) as stderr:
                with self.assertRaises(SystemExit) as raised:
                    SUBJECT.main([
                        "--bundle-root", bundle,
                        "--bom", "bom.json",
                        "--previous-bom", "previous.json",
                        "--schema-sha256", "0" * 64,
                    ])
        self.assertEqual(2, raised.exception.code)
        self.assertIn("--native-stop-trust-receipt", stderr.getvalue())

    def test_the_cli_reports_an_untrusted_bom_signer_as_a_structured_fail(self) -> None:
        # The deployed anchor is live (the owner provisioned the
        # native-stop-trust signer on 2026-07-21), so a complete argv now gets
        # past argparse AND the anchor constructor, then dies in validate():
        # the fixture BOM is signed by the synthetic test-only
        # external-bom-test-key, which the deployed policy does not trust for
        # "bom".  The CLI must report that as a JSON FAIL on stdout with exit
        # 1, not a traceback -- the structured-error contract release.sh
        # relies on.
        schema_sha = hashlib.sha256(
            (ROOT / "governance" / "schemas" / "release-bom.schema.json").read_bytes()
        ).hexdigest()
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            with contextlib.redirect_stdout(io.StringIO()) as stdout:
                exit_code = SUBJECT.main([
                    "--repo-root", str(ROOT),
                    "--bundle-root", str(fixture.bundle),
                    "--bom", str(fixture.bom_path),
                    "--previous-bom", str(fixture.previous_path),
                    "--native-stop-trust-receipt", str(fixture.receipt_path),
                    "--schema-sha256", schema_sha,
                ])
        self.assertEqual(1, exit_code)
        payload = json.loads(stdout.getvalue())
        self.assertEqual("FAIL", payload["result"])
        self.assertIn("not trusted for bom", payload["reason"])

    def test_the_cli_rejects_a_malformed_validation_time_as_a_structured_fail(self) -> None:
        # --validation-time is strict UTC: a malformed value fails closed in the
        # constructor and is reported as a JSON FAIL, never a traceback.
        schema_sha = hashlib.sha256(
            (ROOT / "governance" / "schemas" / "release-bom.schema.json").read_bytes()
        ).hexdigest()
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            with contextlib.redirect_stdout(io.StringIO()) as stdout:
                exit_code = SUBJECT.main([
                    "--repo-root", str(ROOT),
                    "--bundle-root", str(fixture.bundle),
                    "--bom", str(fixture.bom_path),
                    "--previous-bom", str(fixture.previous_path),
                    "--native-stop-trust-receipt", str(fixture.receipt_path),
                    "--schema-sha256", schema_sha,
                    "--validation-time", "2026-07-14 12:00:00",
                ])
        self.assertEqual(1, exit_code)
        payload = json.loads(stdout.getvalue())
        self.assertEqual("FAIL", payload["result"])
        self.assertIn("validation time", payload["reason"])

    def test_the_cli_rejects_a_negative_minimum_remaining_lifetime(self) -> None:
        # argparse hard-exits before any validation work starts.
        with tempfile.TemporaryDirectory() as bundle:
            with contextlib.redirect_stderr(io.StringIO()) as stderr:
                with self.assertRaises(SystemExit) as raised:
                    SUBJECT.main([
                        "--bundle-root", bundle,
                        "--bom", "bom.json",
                        "--previous-bom", "previous.json",
                        "--native-stop-trust-receipt", "receipt.json",
                        "--schema-sha256", "0" * 64,
                        "--minimum-remaining-lifetime-seconds", "-1",
                    ])
        self.assertEqual(2, raised.exception.code)
        self.assertIn("--minimum-remaining-lifetime-seconds", stderr.getvalue())

    def test_the_cli_rejects_a_zero_minimum_remaining_lifetime(self) -> None:
        with tempfile.TemporaryDirectory() as bundle:
            with contextlib.redirect_stderr(io.StringIO()) as stderr:
                with self.assertRaises(SystemExit) as raised:
                    SUBJECT.main([
                        "--bundle-root", bundle,
                        "--bom", "bom.json",
                        "--previous-bom", "previous.json",
                        "--native-stop-trust-receipt", "receipt.json",
                        "--schema-sha256", "0" * 64,
                        "--minimum-remaining-lifetime-seconds", "0",
                    ])
        self.assertEqual(2, raised.exception.code)
        self.assertIn("positive integer", stderr.getvalue())


class MigrationFidelityTests(unittest.TestCase):
    """R0-C moved the validator; until R0-D deletes the module-side original,
    nothing may drift between the two copies except the three declared edits
    (docstring, policy path, --repo-root depth)."""

    MODULE_SOURCE = ROOT / "Modules" / "factory-release-controller" / "src" / "candidate_bom_validator.py"
    MODULE_POLICY = (
        ROOT / "Modules" / "factory-release-controller" / "operations" / "deployed-release-trust-policy.v1.json"
    )
    MIGRATED_POLICY = ROOT / "governance" / "policies" / "deployed-release-trust-policy.v1.json"

    def test_the_validator_sources_differ_only_by_the_three_declared_migration_edits(self) -> None:
        module_docstring = (
            b'"""Fail-closed signed Release BOM validator used by the root release gate.\n'
            b"\n"
            b"The module intentionally exposes no command execution surface.  It verifies\n"
            b"fixed JSON shapes, repository Git objects, immutable bundle bytes, RSA-PSS\n"
            b"signatures, independent evidence, risk approval, and the previous stable BOM.\n"
            b'"""\n'
        )
        tools_docstring = (
            b'"""Fail-closed signed Release BOM validator used by the root release gate.\n'
            b"\n"
            b"The module intentionally exposes no command execution surface.  It verifies\n"
            b"fixed JSON shapes, repository Git objects, immutable bundle bytes, RSA-PSS\n"
            b"signatures, independent evidence, risk approval, and the previous stable BOM.\n"
            b"\n"
            b"R0-C (RebuildPlan 4.3) migrated this validator here from\n"
            b"Modules/factory-release-controller/src/ -- candidate validation is ordinary\n"
            b"gate code and must survive that module's R0-D deletion.  It validates only:\n"
            b"no signing, no deployment, no runtime state.  This copy is the one\n"
            b"scripts/release.sh invokes and the one the Phase 0 CI-integrity allowlist\n"
            b"pins; its code-bound trust policy lives under governance/policies/.  The\n"
            b"module-side original matches this copy after exactly the three declared\n"
            b"migration edits until R0-D removes it.\n"
            b"\n"
            b"The owner provisioned the native-stop-trust signer out-of-repo on\n"
            b"2026-07-21: the deployed trust policy now carries the\n"
            b"native_stop_trust_signer_identities group (owner-native-stop-trust-signer-1)\n"
            b"and the single-purpose native-stop-trust key native-stop-trust-owner-key-1,\n"
            b"and the code-bound digest was re-anchored to the patched policy, so\n"
            b"from_deployed_anchor constructs the release validator against the live\n"
            b"anchor.\n"
            b'"""\n'
        )
        module_policy_path = (
            b'_DEPLOYED_TRUST_POLICY_RELATIVE = (\n'
            b'    "Modules/factory-release-controller/operations/"\n'
            b'    "deployed-release-trust-policy.v1.json"\n'
            b')\n'
        )
        tools_policy_path = (
            b'_DEPLOYED_TRUST_POLICY_RELATIVE = (\n'
            b'    "governance/policies/deployed-release-trust-policy.v1.json"\n'
            b')\n'
        )
        module_repo_root = (
            b'    parser.add_argument("--repo-root", '
            b'default=str(Path(__file__).resolve().parents[3]))\n'
        )
        tools_repo_root = (
            b'    # Tools/ci/<this file> -> parents[2] is the repository root.\n'
            b'    parser.add_argument("--repo-root", '
            b'default=str(Path(__file__).resolve().parents[2]))\n'
        )
        declared_edits = (
            (tools_docstring, module_docstring),
            (tools_policy_path, module_policy_path),
            (tools_repo_root, module_repo_root),
        )
        normalized_tools = SOURCE_PATH.read_bytes()
        module_source = self.MODULE_SOURCE.read_bytes()
        for tools_block, module_block in declared_edits:
            self.assertEqual(1, normalized_tools.count(tools_block))
            self.assertEqual(1, module_source.count(module_block))
            normalized_tools = normalized_tools.replace(tools_block, module_block, 1)
        self.assertEqual(module_source, normalized_tools)

    def test_the_trust_policy_bytes_are_identical_in_both_homes(self) -> None:
        self.assertEqual(self.MODULE_POLICY.read_bytes(), self.MIGRATED_POLICY.read_bytes())

    def test_the_code_bound_policy_digest_did_not_change_in_migration(self) -> None:
        # Same bytes, same canonical digest: the migration re-pointed the path
        # but deliberately did not re-anchor trust.
        policy = json.loads(self.MIGRATED_POLICY.read_bytes())
        self.assertEqual(
            SUBJECT._DEPLOYED_TRUST_POLICY_SHA256,
            sha256_bytes(canonical_bytes(dict(policy))),
        )
        self.assertEqual(SUBJECT._DEPLOYED_TRUST_POLICY_ID, policy.get("policy_id"))

    def test_the_operational_anchor_entry_behaves_identically_in_both_copies(self) -> None:
        # Migration fidelity only: whatever from_deployed_anchor does, it must do
        # the same thing in both homes until R0-D deletes the original.  This
        # test takes NO position on whether the entry works.
        import importlib.util

        spec = importlib.util.spec_from_file_location("_dps_module_side_cbv_subject", self.MODULE_SOURCE)
        original = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(original)
        schema_sha = hashlib.sha256(
            (ROOT / "governance" / "schemas" / "release-bom.schema.json").read_bytes()
        ).hexdigest()
        outcomes = []
        for subject in (SUBJECT, original):
            with tempfile.TemporaryDirectory() as bundle:
                try:
                    subject.CandidateBomValidator.from_deployed_anchor(ROOT, bundle, schema_sha)
                    outcomes.append(None)
                except subject.CandidateBomError as exc:
                    outcomes.append(str(exc))
        self.assertEqual(outcomes[0], outcomes[1])

    def test_the_operational_anchor_entry_constructs_the_release_validator(self) -> None:
        # RESOLVED 2026-07-21: the owner provisioned the native-stop-trust
        # signer out-of-repo (identity owner-native-stop-trust-signer-1,
        # single-purpose key native-stop-trust-owner-key-1), the deployed
        # policy gained its native_stop_trust_signer_identities group, and
        # the code-bound digest was re-anchored to the patched policy.  The
        # operational anchor entry now constructs.
        #
        # Scope of this test: constructor instantiation ONLY.  That is the
        # tripwire, not the coverage -- the release entry is main() end to
        # end (argparse -> construct -> validate a signed BOM against a
        # bundle and receipt).  The remaining completion criterion is the
        # end-to-end main() happy-path test with an owner-signed fixture
        # receipt (Tests/ci/test_candidate_bom_validator_e2e.py); the CLI's
        # TODAY-reachable behaviour is covered by ReleaseCliEntryTests.
        schema_sha = hashlib.sha256(
            (ROOT / "governance" / "schemas" / "release-bom.schema.json").read_bytes()
        ).hexdigest()
        with tempfile.TemporaryDirectory() as bundle:
            validator = SUBJECT.CandidateBomValidator.from_deployed_anchor(ROOT, bundle, schema_sha)
        self.assertIsInstance(validator, SUBJECT.CandidateBomValidator)
        self.assertEqual("dps-deployed-release-anchor-v1", validator._trust.policy_id)
        self.assertEqual(
            "native-stop-trust-owner-key-1", validator._trust.native_stop_trust_key_id
        )


class CandidateBomValidatorTests(unittest.TestCase):
    def test_shared_release_binding_compat_corpus_passes_real_validator(self):
        corpus_root = RELEASE_BINDING_COMPAT_CORPUS_ROOT
        metadata_path = corpus_root / "corpus.json"
        metadata = json.loads(metadata_path.read_bytes())
        self.assertEqual(
            "dps.r0c-release-binding-compat-corpus/v1",
            metadata["schema_version"],
        )
        self.assertEqual(
            "generated once with a 2048-bit test-only RSA key held only in volatile "
            "process memory; the signing component was discarded and is absent "
            "from this corpus and repository",
            metadata["ephemeral_signing_component_disposition"],
        )
        public_key = metadata["controller_public_key"]
        self.assertEqual("rsa-pss-sha256", public_key["algorithm"])
        self.assertEqual(["bom"], public_key["purposes"])
        self.assertEqual(65537, public_key["exponent"])
        self.assertEqual(256, public_key["unsigned_modulus_octets"])
        self.assertEqual(
            256,
            (int(public_key["modulus_hex"], 16).bit_length() + 7) // 8,
        )

        expected_files = {
            item["path"]: item for item in metadata["files"]
        }
        self.assertEqual(len(expected_files), len(metadata["files"]))
        actual_files = {
            path.relative_to(corpus_root).as_posix()
            for path in corpus_root.rglob("*")
            if path.is_file() and path != metadata_path
        }
        self.assertEqual(set(expected_files), actual_files)
        for relative, expected in expected_files.items():
            with self.subTest(path=relative):
                raw = (corpus_root / relative).read_bytes()
                self.assertEqual(expected["size_bytes"], len(raw))
                self.assertEqual(expected["sha256"], sha256_bytes(raw))

        token_path = corpus_root / metadata["token_preimages_file"]
        tokens = json.loads(token_path.read_bytes())
        self.assertEqual(
            "dps.r0c-release-binding-token-preimages/v1",
            tokens["schema_version"],
        )
        previous_token = base64.b64decode(
            tokens["previous_execution_token_base64"], validate=True
        )
        candidate_token = base64.b64decode(
            tokens["candidate_execution_token_base64"], validate=True
        )
        self.assertEqual(32, len(previous_token))
        self.assertEqual(32, len(candidate_token))
        self.assertNotEqual(previous_token, candidate_token)

        wire_files = metadata["wire_files"]
        previous_signed_bytes = (
            corpus_root / wire_files["previous_signed"]["path"]
        ).read_bytes()
        previous_stable_bytes = (
            corpus_root / wire_files["previous_stable"]["path"]
        ).read_bytes()
        candidate_bytes = (
            corpus_root / wire_files["candidate"]["path"]
        ).read_bytes()
        previous_signed = json.loads(previous_signed_bytes)
        previous_stable = json.loads(previous_stable_bytes)
        candidate = json.loads(candidate_bytes)
        self.assertEqual("SIGNED", previous_signed["status"])
        self.assertEqual("STABLE", previous_stable["status"])
        self.assertEqual("SIGNED", candidate["status"])
        self.assertEqual(
            {"status", "signature"},
            {
                key
                for key in previous_signed
                if previous_signed[key] != previous_stable[key]
            },
        )
        self.assertEqual(
            hashlib.sha256(previous_token).hexdigest(),
            previous_signed["activation_token_sha256"],
        )
        self.assertEqual(
            hashlib.sha256(candidate_token).hexdigest(),
            candidate["activation_token_sha256"],
        )
        self.assertEqual(
            previous_stable["bom_id"],
            candidate["previous_stable_bom"],
        )
        self.assertEqual(
            sha256_bytes(previous_stable_bytes),
            candidate["previous_stable_bom_sha256"],
        )

        with tempfile.TemporaryDirectory(
            prefix="dps-r0c-release-binding-compat-"
        ) as directory:
            materialized_root = Path(directory)
            repo = materialized_root / "repo"
            bundle = materialized_root / "bundle"
            shutil.copytree(corpus_root / "repo-content", repo)
            shutil.copytree(corpus_root / "bundle", bundle)
            git_fixture = metadata["git_fixture"]
            _git(repo, "init", "-q")
            _git(repo, "config", "user.email", git_fixture["author_email"])
            _git(repo, "config", "user.name", git_fixture["author_name"])
            _git(repo, "add", ".")
            _git(
                repo,
                "commit",
                "-q",
                "-m",
                git_fixture["commit_message"],
                environment={
                    "GIT_AUTHOR_DATE": git_fixture["author_date"],
                    "GIT_COMMITTER_DATE": git_fixture["committer_date"],
                },
            )
            self.assertEqual(
                git_fixture["expected_commit"],
                _git(repo, "rev-parse", "HEAD"),
            )
            self.assertEqual("", _git(repo, "status", "--porcelain"))

            policy = json.loads(
                (corpus_root / metadata["trust_policy_file"]).read_bytes()
            )
            bom_key = next(
                key
                for key in policy["keys"]
                if key["key_id"] == public_key["key_id"]
            )
            self.assertEqual(
                {
                    key: value
                    for key, value in public_key.items()
                    if key != "unsigned_modulus_octets"
                },
                bom_key,
            )
            schema_path = (
                repo / "governance" / "schemas" / "release-bom.schema.json"
            )
            validator = CandidateBomValidator(
                repo,
                bundle,
                policy,
                sha256_bytes(canonical_bytes(policy)),
                sha256_bytes(schema_path.read_bytes()),
                validation_time="2026-07-14T00:00:00Z",
            )
            result = validator.validate(
                bundle / "candidate-bom.json",
                bundle / "previous-stable-bom.json",
                bundle / "authority-trust-receipt.json",
            )

        self.assertEqual("PASS", result["result"])
        self.assertEqual(
            "INTEGRATION_VERIFIED",
            result["verification_ceiling"],
        )

    def test_full_signed_bom_three_authorities_and_external_receipt_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            result = fixture.validate()
            self.assertEqual("PASS", result["result"])
            self.assertEqual("INTEGRATION_VERIFIED", result["verification_ceiling"])
            self.assertEqual(
                SUBJECT._canonical_challenge_authorities_hash(
                    fixture.bom["native_stop_challenge_authorities"]
                ), result["native_stop_challenge_authorities_sha256"],
            )
            self.assertFalse(result["canary_verified"])
            self.assertFalse(result["scale_verified"])

    def test_candidate_and_previous_bom_require_exact_canonical_wire(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom_path.write_bytes(canonical_bytes(fixture.bom) + b"\n")
            with self.assertRaisesRegex(
                CandidateBomError,
                "candidate Release BOM must be the canonical sorted compact JSON wire",
            ):
                fixture.validate()

        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            reversed_previous = dict(reversed(list(fixture.previous.items())))
            noncanonical_previous = json.dumps(
                reversed_previous,
                separators=(",", ":"),
                ensure_ascii=False,
            ).encode("utf-8")
            self.assertNotEqual(canonical_bytes(fixture.previous), noncanonical_previous)
            fixture.previous_path.write_bytes(noncanonical_previous)
            fixture.bom["previous_stable_bom_sha256"] = sha256_bytes(
                noncanonical_previous
            )
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(
                CandidateBomError,
                "previous stable BOM must be the canonical sorted compact JSON wire",
            ):
                fixture.validate()

        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["feature_flags"]["invalid"] = "\ud800"
            fixture.bom_path.write_bytes(
                json.dumps(
                    fixture.bom,
                    sort_keys=True,
                    separators=(",", ":"),
                    ensure_ascii=True,
                ).encode("utf-8")
            )
            with self.assertRaisesRegex(
                CandidateBomError,
                "candidate Release BOM contains an invalid Unicode scalar sequence",
            ):
                fixture.validate()

        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["feature_flags"] = {"\ud800": False}
            fixture.bom_path.write_bytes(
                json.dumps(
                    fixture.bom,
                    sort_keys=True,
                    separators=(",", ":"),
                    ensure_ascii=True,
                ).encode("utf-8")
            )
            with self.assertRaisesRegex(
                CandidateBomError,
                "candidate Release BOM contains an invalid Unicode scalar sequence",
            ):
                fixture.validate()

    def test_native_stop_receipt_signer_payload_builder_requires_the_exact_canonical_bom(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            common = (
                fixture.policy["policy_id"],
                "native-stop-trust-" + "9" * 32,
                "trace_" + "8" * 32,
                "2026-07-14T00:00:00.0000001Z",
            )
            payload = SUBJECT.build_native_stop_trust_receipt_payload(
                fixture.bom, fixture.bom_path.read_bytes(), *common
            )
            self.assertEqual(
                sha256_bytes(fixture.bom_path.read_bytes()),
                payload["release_bom_sha256"],
            )

            with self.assertRaisesRegex(
                CandidateBomError,
                "signed Release BOM must be the canonical sorted compact JSON wire",
            ):
                SUBJECT.build_native_stop_trust_receipt_payload(
                    fixture.bom, fixture.bom_path.read_bytes() + b"\n", *common
                )

            mismatched = copy.deepcopy(fixture.bom)
            mismatched["bom_id"] = "different-bom-001"
            with self.assertRaisesRegex(
                CandidateBomError,
                "signed Release BOM must be the canonical sorted compact JSON wire",
            ):
                SUBJECT.build_native_stop_trust_receipt_payload(
                    mismatched, fixture.bom_path.read_bytes(), *common
                )

    def test_canonical_number_corpus_matches_cross_language_contract(self):
        corpus_bytes = CANONICAL_NUMBER_CORPUS_PATH.read_bytes()
        self.assertEqual(
            CANONICAL_NUMBER_CORPUS_SHA256,
            hashlib.sha256(corpus_bytes).hexdigest(),
        )
        corpus = json.loads(corpus_bytes)
        self.assertEqual(
            "dps.release-bom-canonical-number-corpus/v1",
            corpus["schema_version"],
        )
        self.assertEqual(62, len(corpus["cases"]))
        self.assertEqual(
            {"accept": 18, "normalize": 35, "reject": 9},
            {
                outcome: sum(
                    item.get("outcome") == outcome for item in corpus["cases"]
                )
                for outcome in ("accept", "normalize", "reject")
            },
        )
        self.assertEqual(
            {"accept", "normalize", "reject"},
            {item.get("outcome") for item in corpus["cases"]},
        )
        for item in corpus["cases"]:
            with self.subTest(wire=item["wire"]):
                raw = ('{"n":' + item["wire"] + "}").encode("ascii")
                if item["outcome"] == "reject":
                    with self.assertRaises(CandidateBomError):
                        SUBJECT._strict_json_loads(raw, "number corpus")
                    continue
                parsed = SUBJECT._strict_json_loads(raw, "number corpus")
                actual = canonical_bytes(parsed)
                expected = ('{"n":' + item["canonical"] + "}").encode("ascii")
                self.assertEqual(expected, actual)
                self.assertEqual(item["outcome"] == "accept", raw == actual)

    def test_canonical_string_corpus_matches_cross_language_contract(self):
        corpus_bytes = CANONICAL_STRING_CORPUS_PATH.read_bytes()
        self.assertEqual(
            CANONICAL_STRING_CORPUS_SHA256,
            hashlib.sha256(corpus_bytes).hexdigest(),
        )
        corpus = json.loads(corpus_bytes)
        self.assertEqual(
            "dps.release-bom-canonical-string-corpus/v1",
            corpus["schema_version"],
        )
        self.assertEqual(4, len(corpus["cases"]))
        self.assertEqual(4, len({item.get("id") for item in corpus["cases"]}))
        for item in corpus["cases"]:
            with self.subTest(case=item["id"]):
                wire = base64.b64decode(item["wire_base64"], validate=True)
                expected = base64.b64decode(
                    item["canonical_base64"], validate=True
                )
                parsed = SUBJECT._strict_json_loads(wire, "string corpus")
                self.assertEqual(expected, canonical_bytes(parsed))

    def test_canonical_number_limits_and_nonfinite_builder_inputs_fail_closed(self):
        accepted = "1" + "0" * 4_299
        self.assertEqual(
            ('{"n":' + accepted + "}").encode("ascii"),
            canonical_bytes(
                SUBJECT._strict_json_loads(
                    ('{"n":' + accepted + "}").encode("ascii"),
                    "4300-digit number",
                )
            ),
        )
        rejected = "1" + "0" * 4_300
        with self.assertRaisesRegex(CandidateBomError, "strict JSON"):
            SUBJECT._strict_json_loads(
                ('{"n":' + rejected + "}").encode("ascii"),
                "4301-digit number",
            )

        for invalid in (float("nan"), float("inf"), float("-inf")):
            with self.subTest(invalid=invalid):
                with self.assertRaisesRegex(ValueError, "finite"):
                    canonical_bytes({"n": invalid})
                with self.assertRaisesRegex(CandidateBomError, "canonical JSON domain"):
                    SUBJECT._require_canonical_bom_wire(
                        {"feature_flags": {"invalid": invalid}},
                        b"{}",
                        "signed Release BOM",
                    )

    def test_missing_or_tampered_external_receipt_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            with self.assertRaisesRegex(CandidateBomError, "receipt is required"):
                fixture.validator().validate(fixture.bom_path, fixture.previous_path, None)
            receipt = json.loads(fixture.receipt_path.read_bytes())
            receipt["release_bom_sha256"] = "f" * 64
            payload = {key: value for key, value in receipt.items() if key != "signature"}
            receipt["signature"] = _sign(
                "trust-receipt-key", SUBJECT.native_stop_trust_signing_bytes(payload)
            )
            _write_receipt_wire(fixture.receipt_path, receipt)
            with self.assertRaisesRegex(CandidateBomError, "receipt mismatch"):
                fixture.validate()

    def test_unpublished_hyphenated_trust_contract_identity_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            receipt = json.loads(fixture.receipt_path.read_bytes())
            receipt["contract_id"] = "release.bom.native-stop-authority-trust/v1"
            payload = {key: value for key, value in receipt.items() if key != "signature"}
            receipt["signature"] = _sign(
                "trust-receipt-key", SUBJECT.native_stop_trust_signing_bytes(payload)
            )
            _write_receipt_wire(fixture.receipt_path, receipt)
            with self.assertRaisesRegex(CandidateBomError, "envelope"):
                fixture.validator()._validate_native_stop_trust_receipt(
                    fixture.bom,
                    fixture.bom_path.read_bytes(),
                    fixture.receipt_path,
                )

    def test_worker_authority_must_match_manifest_descriptor_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["native_stop_authorities"][0]["worker_artifact_sha256"] = "e" * 64
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "Manifest/descriptor truth"):
                fixture.validate()

    def test_route_authority_must_match_supervisor_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["device_route_assignment_authorities"][0]["supervisor_artifact_sha256"] = "e" * 64
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "Supervisor artifact tuple"):
                fixture.validate()

    def test_challenge_authority_must_match_policy_artifact(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["native_stop_challenge_authorities"][0]["policy_artifact_sha256"] = "e" * 64
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "Policy artifact tuple"):
                fixture.validate()

    def test_three_runtime_key_roles_are_pairwise_distinct(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            route = fixture.bom["device_route_assignment_authorities"][0]
            challenge = fixture.bom["native_stop_challenge_authorities"][0]
            challenge["p256_spki_sha256"] = route["route_signer_p256_spki_sha256"]
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "reused across roles"):
                fixture.validate()

    def test_native_stop_v1_scope_or_contract_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            authority = fixture.bom["native_stop_authorities"][0]
            authority["auth_scope"] = "policy-approval:native-stop-proof:commit-unknown"
            authority["native_stop_contract_id"] = "native.stop.proof/v1"
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "scope, or policy"):
                fixture.validate()

    def test_timestamp_uses_100ns_exact_z_and_2020_lower_bound(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["native_stop_authorities"][0]["valid_from"] = "2019-12-31T23:59:59.9999999Z"
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "2020-01-01"):
                fixture.validate()
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["native_stop_authorities"][0]["valid_from"] = "2026-07-01T00:00:00.000000001Z"
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "seven|yyyy-MM-dd"):
                fixture.validate()

    def test_same_native_spki_cannot_move_worker_incarnation_across_boms(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["native_stop_authorities"][0]["worker_instance_id"] = "wi_" + "f" * 32
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            with self.assertRaisesRegex(CandidateBomError, "cannot move"):
                fixture.validate()

    def test_artifact_provenance_and_evidence_tampering_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            (fixture.bundle / "artifacts" / "windows-edge-worker.bin").write_bytes(b"tampered")
            with self.assertRaisesRegex(CandidateBomError, "artifact hash"):
                fixture.validate()
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.rewrite_module_builder_id(
                "policy-approval", "dps:test-builder:1.0.0"
            )
            with self.assertRaisesRegex(
                CandidateBomError, "provenance run identity mismatch"
            ):
                fixture.validate()
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.evidence_receipt["result"] = "FAIL"
            raw = _write_json(
                fixture.bundle / "evidence" / "gate-integration-001.json",
                fixture.evidence_receipt,
            )
            fixture.bom["evidence"][0]["sha256"] = sha256_bytes(raw)
            fixture.bom["evidence"][0]["result"] = "FAIL"
            fixture.bom["evidence"][0]["signature"] = _sign(
                "evidence-key", b"dps-release-evidence/v1\n" + raw
            )
            fixture.rewrite_candidate(update_scope=False)
            with self.assertRaisesRegex(CandidateBomError, "not PASS"):
                fixture.validate()

    def test_duplicate_receipt_member_and_old_mixed_shape_fail_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            receipt = json.loads(fixture.receipt_path.read_bytes())
            fixture.receipt_path.write_bytes(
                b'{"authorities_sha256":"' + b"0" * 64 + b'",' + canonical_bytes(receipt)[1:]
            )
            with self.assertRaisesRegex(
                CandidateBomError, "strict JSON|invalid fields|canonical sorted compact"
            ):
                fixture.validate()

    def test_noncanonical_receipt_wires_fail_closed_with_the_same_signature(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            canonical = fixture.receipt_path.read_bytes()
            receipt = json.loads(canonical)
            variants = {
                "trailing-lf": canonical + b"\n",
                "whitespace": json.dumps(
                    receipt, ensure_ascii=False, indent=2
                ).encode("utf-8"),
                "reordered": json.dumps(
                    dict(reversed(list(receipt.items()))),
                    ensure_ascii=False,
                    separators=(",", ":"),
                    sort_keys=False,
                ).encode("utf-8"),
            }
            for label, wire in variants.items():
                with self.subTest(label=label):
                    self.assertNotEqual(canonical, wire)
                    fixture.receipt_path.write_bytes(wire)
                    with self.assertRaisesRegex(
                        CandidateBomError, "canonical sorted compact JSON wire"
                    ):
                        fixture.validate()

    def test_noncanonical_receipt_signature_base64_alias_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            receipt = json.loads(fixture.receipt_path.read_bytes())
            original = receipt["signature"]["value"]
            receipt["signature"]["value"] = _noncanonical_base64_pad_alias(original)
            self.assertNotEqual(original, receipt["signature"]["value"])
            _write_receipt_wire(fixture.receipt_path, receipt)
            with self.assertRaisesRegex(
                CandidateBomError, "signature value is not canonical base64"
            ):
                fixture.validate()

    def test_rsa_signature_representative_must_be_less_than_modulus(self):
        modulus, _ = KEYS["trust-receipt-key"]
        for counter in range(1_000):
            message = (
                b"rsa-representative-range-regression:"
                + str(counter).encode("ascii")
            )
            signature = base64.b64decode(
                _sign("trust-receipt-key", message)["value"], validate=True
            )
            representative = int.from_bytes(signature, "big")
            alias = representative + modulus
            if alias < 1 << (8 * len(signature)):
                break
        else:
            self.fail("unable to construct the deterministic s+n regression")
        alias_bytes = alias.to_bytes(len(signature), "big")
        self.assertNotEqual(signature, alias_bytes)
        self.assertTrue(
            SUBJECT._verify_rsa_pss(message, signature, modulus, 65537)
        )
        self.assertFalse(
            SUBJECT._verify_rsa_pss(message, alias_bytes, modulus, 65537)
        )

    def test_policy_roles_and_key_purposes_are_separated(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            policy = copy.deepcopy(fixture.policy)
            policy["native_stop_trust_signer_identities"] = ["release-controller"]
            with self.assertRaisesRegex(CandidateBomError, "separation of duties"):
                fixture.validator(policy=policy)
            policy = copy.deepcopy(fixture.policy)
            for key in policy["keys"]:
                if key["key_id"] == "trust-receipt-key":
                    key["purposes"].append("bom")
            with self.assertRaisesRegex(
                CandidateBomError,
                "external signer profile|assigned to bom|cannot be reused",
            ):
                fixture.validator(policy=policy)

            def bom_key(value):
                return next(
                    key
                    for key in value["keys"]
                    if key["key_id"] == "external-bom-test-key"
                )

            profile_mutations = {
                "multi-purpose": lambda key: key["purposes"].append("artifact"),
                "duplicate-purpose": lambda key: key.update(
                    purposes=["bom", "bom"]
                ),
                "wrong-algorithm": lambda key: key.update(
                    algorithm="rsa-pkcs1v15-sha256"
                ),
                "1024-bit-modulus": lambda key: key.update(
                    modulus_hex="a" + "1" * 255
                ),
                "public-exponent-3": lambda key: key.update(exponent=3),
                "leading-zero-modulus": lambda key: key.update(
                    modulus_hex="0" + key["modulus_hex"]
                ),
                "uppercase-modulus": lambda key: key.update(
                    modulus_hex=key["modulus_hex"].upper()
                ),
            }
            for label, mutate in profile_mutations.items():
                with self.subTest(external_bom_key_profile=label):
                    policy = copy.deepcopy(fixture.policy)
                    mutate(bom_key(policy))
                    with self.assertRaisesRegex(
                        CandidateBomError,
                        "external signer profile|invalid or duplicate trust key|"
                        "trusted RSA modulus",
                    ):
                        # This is the production parser path, not a test helper:
                        # CandidateBomValidator constructs ReleaseTrustPolicy.
                        fixture.validator(policy=policy)


class RuntimeAuthorityCurrencyTests(unittest.TestCase):
    """The live validation instant, not the BOM's self-declared created_at,
    must fall inside every current runtime authority window, with the
    configured minimum remaining lifetime (expired-authority finding)."""

    def test_previous_stable_authority_expired_at_validation_time_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.previous["created_at"] = "2026-07-13T00:00:00Z"
            fixture.previous["native_stop_authorities"][0]["valid_until"] = (
                "2026-07-13T23:59:59.0000001Z"
            )
            fixture.rewrite_previous()
            with self.assertRaisesRegex(
                CandidateBomError,
                "previous stable BOM native stop authority native-stop-authority-a"
                " is expired at the validation time",
            ):
                fixture.validate()

    def test_previous_stable_authority_inside_nonzero_interval_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.previous["native_stop_authorities"][0]["valid_until"] = (
                "2026-07-14T12:00:00.0000001Z"
            )
            fixture.rewrite_previous()
            with self.assertRaisesRegex(
                CandidateBomError,
                "previous stable BOM native stop authority native-stop-authority-a"
                " expires inside the minimum remaining lifetime",
            ):
                fixture.validate()

    def test_all_previous_stable_authority_sets_enforce_the_nonzero_interval(self):
        cases = (
            (
                "native_stop_authorities",
                "previous stable BOM native stop authority native-stop-authority-a",
            ),
            (
                "device_route_assignment_authorities",
                "previous stable BOM device route authority device-route-authority-a",
            ),
            (
                "native_stop_challenge_authorities",
                "previous stable BOM challenge authority native-stop-challenge-authority-a",
            ),
        )
        for authority_set, expected_label in cases:
            with self.subTest(authority_set=authority_set), tempfile.TemporaryDirectory() as directory:
                fixture = BomFixture(directory)
                fixture.previous[authority_set][0]["valid_until"] = (
                    "2026-07-14T12:00:00.0000001Z"
                )
                fixture.rewrite_previous()
                with self.assertRaisesRegex(
                    CandidateBomError,
                    expected_label + " expires inside the minimum remaining lifetime",
                ):
                    fixture.validate()

    def test_authority_set_expired_at_validation_time_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            validator = fixture.validator(validation_time="2026-08-01T00:00:00Z")
            with self.assertRaisesRegex(
                CandidateBomError,
                "native stop authority native-stop-authority-a is expired"
                " at the validation time",
            ):
                validator.validate(
                    fixture.bom_path, fixture.previous_path, fixture.receipt_path
                )

    def test_expired_device_route_authority_names_the_route_set(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["device_route_assignment_authorities"][0]["valid_until"] = (
                "2026-07-20T00:00:00.0000002Z"
            )
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            validator = fixture.validator(validation_time="2026-07-25T00:00:00Z")
            with self.assertRaisesRegex(
                CandidateBomError,
                "device route authority device-route-authority-a is expired"
                " at the validation time",
            ):
                validator.validate(
                    fixture.bom_path, fixture.previous_path, fixture.receipt_path
                )

    def test_expired_challenge_authority_names_the_challenge_set(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            fixture.bom["native_stop_challenge_authorities"][0]["valid_until"] = (
                "2026-07-20T00:00:00.0000003Z"
            )
            fixture.rehash_authorities()
            fixture.rewrite_candidate()
            validator = fixture.validator(validation_time="2026-07-25T00:00:00Z")
            with self.assertRaisesRegex(
                CandidateBomError,
                "challenge authority native-stop-challenge-authority-a is expired"
                " at the validation time",
            ):
                validator.validate(
                    fixture.bom_path, fixture.previous_path, fixture.receipt_path
                )

    def test_not_yet_valid_authority_is_rejected_at_validation_time(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            validator = fixture.validator(validation_time="2026-06-01T00:00:00Z")
            with self.assertRaisesRegex(
                CandidateBomError,
                "native stop authority native-stop-authority-a is not yet valid"
                " at the validation time",
            ):
                validator.validate(
                    fixture.bom_path, fixture.previous_path, fixture.receipt_path
                )

    def test_authority_below_minimum_remaining_lifetime_is_rejected(self):
        # Inside every window, but the earliest window end (native stop,
        # 2026-07-31T00:00:00.0000001Z) is only ~1 day out, below the 2-day
        # minimum remaining lifetime required for rollout and rollback.
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            validator = fixture.validator(
                validation_time="2026-07-30T00:00:00Z",
                minimum_remaining_lifetime_seconds=2 * 24 * 60 * 60,
            )
            with self.assertRaisesRegex(
                CandidateBomError,
                "native stop authority native-stop-authority-a expires inside"
                " the minimum remaining lifetime at the validation time",
            ):
                validator.validate(
                    fixture.bom_path, fixture.previous_path, fixture.receipt_path
                )

    def test_default_nonzero_interval_rejects_near_expiry_candidate(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            validator = fixture.validator(validation_time="2026-07-30T12:00:00Z")
            with self.assertRaisesRegex(
                CandidateBomError,
                "candidate Release BOM native stop authority native-stop-authority-a"
                " expires inside the minimum remaining lifetime",
            ):
                validator.validate(
                    fixture.bom_path, fixture.previous_path, fixture.receipt_path
                )

    def test_constructor_rejects_explicit_zero_interval(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            with self.assertRaisesRegex(CandidateBomError, "positive integer"):
                fixture.validator(minimum_remaining_lifetime_seconds=0)

    def test_authorities_valid_with_sufficient_remaining_lifetime_pass(self):
        # Same instant; a 1-day minimum is satisfied by every window.
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            result = fixture.validator(
                validation_time="2026-07-30T00:00:00Z",
                minimum_remaining_lifetime_seconds=24 * 60 * 60,
            ).validate(fixture.bom_path, fixture.previous_path, fixture.receipt_path)
            self.assertEqual("PASS", result["result"])

    def test_validation_time_defaults_to_system_utc_now(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = BomFixture(directory)
            before_ns = int((datetime.now(timezone.utc).timestamp() - 1) * 1_000_000_000)
            validator = fixture.validator(validation_time=None)
            after_ns = int((datetime.now(timezone.utc).timestamp() + 1) * 1_000_000_000)
        unix_epoch_offset_ns = 62_135_596_800 * 1_000_000_000
        self.assertTrue(
            before_ns + unix_epoch_offset_ns
            <= validator._validation_time_ns
            <= after_ns + unix_epoch_offset_ns
        )


if __name__ == "__main__":
    unittest.main()
