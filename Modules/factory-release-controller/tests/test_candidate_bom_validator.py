import base64
import copy
import hashlib
import importlib.util
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_PATH = MODULE_ROOT / "src" / "candidate_bom_validator.py"
SUBJECT_NAME = "_dps_factory_release_candidate_bom_authority_subject"


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


def _git(root, *args):
    result = subprocess.run(
        ["git", "-C", str(root), *args], check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    )
    return result.stdout.decode().strip()


def _write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = canonical_bytes(value) + b"\n"
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
    def __init__(self, root):
        root = Path(root)
        self.repo = root / "repo"
        self.bundle = root / "bundle"
        self.repo.mkdir()
        self.bundle.mkdir()
        schema_source = MODULE_ROOT.parents[1] / "governance" / "schemas" / "release-bom.schema.json"
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
        _git(self.repo, "commit", "-q", "-m", "authority fixture")
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
        self.bom = self._make_bom(2, "b" * 64, "candidate-bom-001", "SIGNED")
        self._write_approval(self.bom)
        self.previous = copy.deepcopy(self.bom)
        self.previous.update({
            "bom_id": "previous-bom-001", "status": "STABLE",
            "release_bom_generation": 1, "activation_token_sha256": "a" * 64,
            "previous_stable_bom": None, "previous_stable_bom_sha256": None,
        })
        self.previous.update(self._authorities(1, "a" * 64))
        self.previous["signature"] = self._sign_bom(self.previous)
        self.previous_path = self.bundle / "previous-bom.json"
        self.previous_bytes = _write_json(self.previous_path, self.previous)
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
            "controller-key": ("release-controller", ["artifact", "bom"]),
            "evidence-key": ("evidence-issuer", ["evidence"]),
            "approval-key": ("human-approver", ["approval"]),
            "trust-receipt-key": ("authority-trust-signer", ["native-stop-trust"]),
        }
        return {
            "schema_version": "dps.release-trust-policy/v1",
            "policy_id": "release-policy-001",
            "required_gates": {"gate-integration-001": {
                "kind": "INTEGRATION", "minimum_verification_level": "INTEGRATION_VERIFIED",
            }},
            "implementer_identities": ["module-builder"],
            "evidence_issuer_identities": ["evidence-issuer"],
            "release_controller_identities": ["release-controller"],
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

    @staticmethod
    def _sign_bom(bom):
        return _sign("controller-key", b"dps-release-bom/v1\n" + canonical_bytes({
            key: value for key, value in bom.items() if key != "signature"
        }))

    def rewrite_candidate(self, *, update_scope=True, update_receipt=True):
        if update_scope:
            self._write_approval(self.bom)
        self.bom["signature"] = self._sign_bom(self.bom)
        bom_bytes = _write_json(self.bom_path, self.bom)
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
            _write_json(self.receipt_path, receipt)

    def rehash_authorities(self):
        for authority in self.bom["native_stop_authorities"]:
            authority["worker_authority_sha256"] = SUBJECT._canonical_authority_hash(authority)
        for authority in self.bom["device_route_assignment_authorities"]:
            authority["route_authority_sha256"] = SUBJECT._canonical_route_authority_hash(authority)
        for authority in self.bom["native_stop_challenge_authorities"]:
            authority["challenge_authority_sha256"] = SUBJECT._canonical_challenge_authority_hash(authority)

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

    def validator(self, policy=None, policy_sha=None):
        value = self.policy if policy is None else policy
        digest = sha256_bytes(canonical_bytes(value)) if policy_sha is None else policy_sha
        return CandidateBomValidator(self.repo, self.bundle, value, digest, self.schema_sha)

    def validate(self):
        return self.validator().validate(
            self.bom_path, self.previous_path, self.receipt_path
        )


class CandidateBomValidatorTests(unittest.TestCase):
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
            _write_json(fixture.receipt_path, receipt)
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
            _write_json(fixture.receipt_path, receipt)
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
            with self.assertRaisesRegex(CandidateBomError, "strict JSON|invalid fields"):
                fixture.validate()

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
            with self.assertRaisesRegex(CandidateBomError, "assigned to bom|cannot be reused"):
                fixture.validator(policy=policy)


if __name__ == "__main__":
    unittest.main()
