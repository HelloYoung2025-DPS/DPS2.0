from __future__ import annotations

import base64
import copy
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path
from unittest import mock

TOOLS_DIR = Path(__file__).resolve().parents[1]
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

import external_gate as external_gate_module  # noqa: E402
from external_gate import (  # noqa: E402
    ELIGIBLE,
    ExternalGateError,
    _openssl_verify_p1363,
    canonical_bytes,
    run_gate,
    validate_stage_payload,
)


def iso(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _actor_digests(prefix: str, count: int) -> list[str]:
    return [hashlib.sha256(f"{prefix}:{index}".encode("utf-8")).hexdigest() for index in range(count)]


def build_f9_load_artifact(name: str, run: dict, environment_id: str) -> dict:
    profiles = {
        "sustained": ("REAL_SUSTAINED", "REAL_DEVICE_BINDING", "real-device-scope-0001", "real", 100),
        "burst": ("REAL_BURST", "REAL_DEVICE_BINDING", "real-device-scope-0001", "real", 200),
        "simulated": (
            "SIMULATED_CAPACITY",
            "SIMULATED_DEVICE",
            "simulated-device-scope-0001",
            "simulated",
            400,
        ),
    }
    profile, actor_kind, actor_scope, prefix, actor_count = profiles[name]
    actor_set_id = f"f9-{name}-actor-set-0001"
    started = datetime.fromisoformat(run["started_at"].replace("Z", "+00:00"))
    finished = datetime.fromisoformat(run["finished_at"].replace("Z", "+00:00"))
    windows = []
    cursor = started
    sequence = 0
    while cursor < finished:
        window_finished = min(cursor + timedelta(seconds=300), finished)
        final = window_finished == finished
        windows.append(
            {
                "sequence": sequence,
                "started_at": iso(cursor),
                "finished_at": iso(window_finished),
                "actor_set_id": actor_set_id,
                "maximum_backlog_depth": 2 if final else 1,
                "maximum_oldest_backlog_age_seconds": 30,
                "backlog_depth_at_finish": 2 if final else 0,
                "oldest_backlog_age_seconds_at_finish": 30 if final else 0,
            }
        )
        cursor = window_finished
        sequence += 1
    recovery_samples = []
    for sequence in range(7):
        recovered = sequence > 0
        recovery_samples.append(
            {
                "sequence": sequence,
                "observed_at": iso(finished + timedelta(seconds=sequence * 60)),
                "backlog_depth": 0 if recovered else 2,
                "oldest_backlog_age_seconds": 0 if recovered else 30,
            }
        )
    return {
        "schema_version": "dps.f9-load-run-artifact/v1",
        "artifact_id": run["artifact_id"],
        "run_id": run["run_id"],
        "profile": profile,
        "evidence_kind": run["evidence_kind"],
        "environment_id": environment_id,
        "actor_kind": actor_kind,
        "actor_scope_id": actor_scope,
        "actor_digest_algorithm": "HMAC_SHA256_SCOPE_V1",
        "actor_sets": [{"actor_set_id": actor_set_id, "actor_digests": _actor_digests(prefix, actor_count)}],
        "windows": windows,
        "recovery_samples": recovery_samples,
    }


def synthetic_module_manifest(
    module_id: str,
    dependencies: list[tuple[str, str]] | None = None,
    provided: list[dict] | None = None,
    consumed: list[dict] | None = None,
    inbound: list[dict] | None = None,
    outbound: list[dict] | None = None,
    schema_version: str = "dps.module/v2",
) -> dict:
    return {
        "schemaVersion": schema_version,
        "module": {"id": module_id},
        "dependencies": [
            {
                "moduleId": provider,
                "versionRange": ">=0.1.0 <1.0.0",
                "required": True,
                "reason": reason,
            }
            for provider, reason in (dependencies or [])
        ],
        "contracts": {"provided": provided or [], "consumed": consumed or []},
        "communication": {"inbound": inbound or [], "outbound": outbound or []},
        "compatibility": {
            "unknownMajorBehavior": "reject",
            "missingMajorBehavior": "reject",
            "unknownModeBehavior": "reject",
            "missingModeBehavior": "reject",
        },
    }


def synthetic_communication_edge(
    peer: str, contract_id: str, direction: str, major: int = 1
) -> dict:
    return {
        "peerModule": peer,
        "contractId": contract_id,
        "major": major,
        "direction": direction,
        "transport": "in-process-api",
        "timeoutMs": 2000,
        "retryPolicy": "exact-candidate-test-redelivery",
        "idempotencyKey": "candidate:test",
        "authScope": "compatibility:test",
        "failureMode": "fail-closed",
    }


def dependency_artifact_from_manifests(manifests: list[dict]) -> dict:
    graph = {
        manifest["module"]["id"]: {item["moduleId"] for item in manifest["dependencies"]}
        for manifest in manifests
    }
    reasons = {
        (manifest["module"]["id"], item["moduleId"]): item["reason"]
        for manifest in manifests
        for item in manifest["dependencies"]
    }
    remaining = {module_id: set(dependencies) for module_id, dependencies in graph.items()}
    completed: set[str] = set()
    waves: list[list[str]] = []
    while remaining:
        ready = sorted(module_id for module_id, dependencies in remaining.items() if dependencies.issubset(completed))
        if not ready:
            raise AssertionError("synthetic dependency graph contains a cycle")
        waves.append(ready)
        completed.update(ready)
        for module_id in ready:
            del remaining[module_id]
    return {
        "schemaVersion": "dps.dependency-graph/v1",
        "generatedFrom": "Modules/*/module.yaml",
        "failOnCycle": True,
        "nodes": sorted(graph),
        "edges": [
            {"consumer": consumer, "provider": provider, "reason": reasons[(consumer, provider)]}
            for consumer in sorted(graph)
            for provider in sorted(graph[consumer])
        ],
        "parallelWaves": waves,
    }


def compatibility_artifact_from_manifests(
    manifests: list[dict],
    contract_producers: dict[tuple[str, int], set[str]] | None = None,
    policy_sha256: str | None = None,
) -> dict:
    if contract_producers is None:
        contract_producers = {
            (item["contractId"], item["major"]): {item["ownerModule"]}
            for manifest in manifests
            for item in manifest["contracts"]["provided"]
        }
    if policy_sha256 is None:
        policy_path = (
            Path(__file__).resolve().parents[3]
            / "governance"
            / "policies"
            / "compatibility-policy.yaml"
        )
        policy_sha256 = hashlib.sha256(policy_path.read_bytes()).hexdigest()
    return external_gate_module._build_f9_compatibility_artifact(
        {
            manifest["module"]["id"]: {
                "provided": manifest["contracts"]["provided"],
                "consumed": manifest["contracts"]["consumed"],
            }
            for manifest in manifests
        },
        {
            manifest["module"]["id"]: [
                {**edge, "moduleId": manifest["module"]["id"]}
                for direction in ("inbound", "outbound")
                for edge in manifest["communication"][direction]
            ]
            for manifest in manifests
        },
        contract_producers,
        policy_sha256,
    )


class F9CompatibilityV2BuilderTests(unittest.TestCase):
    @staticmethod
    def contract(module_id: str, major: int, mode: str, status: str = "proposed") -> dict:
        return {
            "contractId": "sample.event",
            "major": major,
            "source": f"Modules/{module_id}/contracts/provided/sample.event.v{major}.schema.json",
            "status": status,
            "mode": mode,
            "ownerModule": "provider",
        }

    def test_active_v1_pair_is_independently_deployable_but_execution_is_not_run(self) -> None:
        provided = self.contract("provider", 1, "active")
        consumed = dict(provided)
        artifact = compatibility_artifact_from_manifests(
            [
                synthetic_module_manifest(
                    "provider",
                    provided=[provided],
                    outbound=[
                        synthetic_communication_edge(
                            "consumer", "sample.event", "outbound"
                        )
                    ],
                ),
                synthetic_module_manifest(
                    "consumer",
                    [("provider", "sample provider")],
                    consumed=[consumed],
                    inbound=[
                        synthetic_communication_edge(
                            "provider", "sample.event", "inbound"
                        )
                    ],
                ),
            ]
        )
        self.assertEqual("dps.compatibility-matrix/v2", artifact["schemaVersion"])
        self.assertTrue(artifact["independentDeployable"])
        self.assertFalse(artifact["compatibilityGroupRequired"])
        self.assertTrue(artifact["candidateGreenEligible"])
        self.assertEqual(
            {"NOT_RUN"},
            {item["evidenceStatus"] for item in artifact["executionCombinations"]},
        )

    def test_quarantined_previous_major_requires_group_and_cannot_be_candidate_green(self) -> None:
        provided_v1 = self.contract("provider", 1, "quarantine-only", "deprecated")
        provided_v2 = self.contract("provider", 2, "active")
        consumed_v1 = dict(provided_v1)
        consumed_v2 = dict(provided_v2)
        artifact = compatibility_artifact_from_manifests(
            [
                synthetic_module_manifest(
                    "provider",
                    provided=[provided_v1, provided_v2],
                    outbound=[
                        synthetic_communication_edge(
                            "consumer", "sample.event", "outbound", 2
                        )
                    ],
                ),
                synthetic_module_manifest(
                    "consumer",
                    [("provider", "sample provider")],
                    consumed=[consumed_v1, consumed_v2],
                    inbound=[
                        synthetic_communication_edge(
                            "provider", "sample.event", "inbound", 2
                        )
                    ],
                ),
            ]
        )
        self.assertFalse(artifact["independentDeployable"])
        self.assertTrue(artifact["compatibilityGroupRequired"])
        self.assertFalse(artifact["candidateGreenEligible"])
        previous = next(
            row
            for row in artifact["declarationMatrix"]
            if row["direction"] == "previous-producer-to-current-consumer"
        )
        self.assertEqual("quarantine-only", previous["executionClass"])
        self.assertFalse(previous["runnable"])

    def test_external_rebuild_is_byte_identical_to_phase0_runtime_matrix(self) -> None:
        root = Path(__file__).resolve().parents[3]
        original_import_path = list(sys.path)
        try:
            ci_directory = root / "Tools" / "ci"
            if str(ci_directory) not in sys.path:
                sys.path.insert(0, str(ci_directory))
            import phase0 as phase0_module
        finally:
            sys.path[:] = original_import_path
        records = phase0_module.load_module_records_without_schema(root)
        expected = phase0_module.build_compatibility_snapshot(records, root)
        contracts_by_module = {
            module_id: {
                "provided": record.manifest["contracts"]["provided"],
                "consumed": record.manifest["contracts"]["consumed"],
            }
            for module_id, record in records.items()
        }
        communications_by_module = {
            module_id: [
                {**edge, "moduleId": module_id}
                for direction in ("inbound", "outbound")
                for edge in record.manifest["communication"][direction]
            ]
            for module_id, record in records.items()
        }
        _family_owners, _major_owners, contract_producers = (
            phase0_module._contract_runtime_inventory(root, records)
        )
        actual = external_gate_module._build_f9_compatibility_artifact(
            contracts_by_module,
            communications_by_module,
            contract_producers,
            expected["policySha256"],
        )
        self.assertEqual(expected, actual)
        self.assertEqual(canonical_bytes(expected), canonical_bytes(actual))


class Fixture:
    def __init__(self, root: Path) -> None:
        self.root = root
        self.key_path = root / "external-public-key.pem"
        self.key_path.write_text("TEST PUBLIC KEY - NOT A PRODUCTION KEY\n", encoding="utf-8")
        self.artifact_path = root / "raw-window.jsonl"
        self.artifact_path.write_text('{"raw":"external-observation"}\n', encoding="utf-8")
        self.module_artifact_path = root / "edge-worker.zip"
        self.module_artifact_path.write_bytes(b"signed edge worker candidate")
        self.module_digest = hashlib.sha256(self.module_artifact_path.read_bytes()).hexdigest()
        self.runner_binary_digest = hashlib.sha256(b"synthetic-f7-runner-binary").hexdigest()
        self.runner_sbom_digest = hashlib.sha256(b"synthetic-f7-runner-sbom").hexdigest()
        self.bom_path = root / "release-bom.json"
        self.bom = {
            "schema_version": "dps.release-bom/v1",
            "bom_id": "bom-test-0001",
            "status": "SIGNED",
            "modules": [
                {"module_id": "windows-edge-worker", "sha256": self.module_digest},
                {
                    "module_id": "f7-external-runner",
                    "version": "1.0.0",
                    "sha256": self.runner_binary_digest,
                    "sbom_sha256": self.runner_sbom_digest,
                },
            ],
            "rollout": {"shadow_artifact_sha256": self.module_digest},
            "signature": {
                "algorithm": "ecdsa-p256-sha256",
                "key_id": "bom-key-0001",
                "value": "not-a-real-test-signature",
            },
        }
        self.bom_path.write_bytes(canonical_bytes(self.bom))
        self.trust_path = root / "external-trust.json"
        key_digest = hashlib.sha256(self.key_path.read_bytes()).hexdigest()
        self.trust = {
            "schema_version": "dps.external-verification-trust-policy/v1",
            "policy_id": "trust-policy-test-0001",
            "trusted_issuers": [
                {
                    "issuer_identity": "external-evidence-issuer",
                    "runner_key_id": "runner-key-0001",
                    "algorithm": "ECDSA_P256_SHA256_P1363",
                    "public_key_pem_path": str(self.key_path.resolve()),
                    "public_key_sha256": key_digest,
                    "allowed_verification_levels": ["WINDOWS_VERIFIED"],
                }
            ],
            "trusted_bom_signers": [
                {
                    "key_id": "bom-key-0001",
                    "algorithm": "ecdsa-p256-sha256",
                    "public_key_pem_path": str(self.key_path.resolve()),
                    "public_key_sha256": key_digest,
                }
            ],
            "environment_policies": [
                {
                    "verification_level": "WINDOWS_VERIFIED",
                    "required_claims": {
                        "environment_id": "env_windows_lab_01",
                        "os_family": "Windows",
                        "windows_version": "10.0.22631.3593",
                        "zennodroid_version": "1.2.3-test",
                        "dotnet_framework_version": "4.8.1",
                        "csharp_language_version": "5.0",
                        "codedom_compile": "PASS",
                        "gac_resolution": "PASS",
                        "dll_load": "PASS",
                        "zenno_project_load": "PASS",
                        "bridge_abi": "dps.zenno-bridge/v1",
                        "adb_authorized_device_count": 1,
                        "adb_authorization": "PASS",
                        "loopback_host": "127.0.0.1",
                        "loopback_port": 32145,
                        "loopback_port_fixed": True,
                        "loopback_only": True,
                        "command_timeout_seconds": 30,
                        "timeout_semantics": "FAIL_CLOSED",
                        "error_semantics": "NATIVE_ERROR_PRESERVED",
                        "connection_continuity": "PASS",
                    },
                }
            ],
        }
        self.write_trust()
        self.evidence_path = root / "f6-evidence.json"
        self.started = datetime(2026, 7, 1, tzinfo=timezone.utc)
        self.finished = self.started + timedelta(hours=24)
        self.f6_finished = self.finished
        self.evidence = self._f6_evidence()
        self.reseal()
        self.f6_evidence_sha256 = hashlib.sha256(self.evidence_path.read_bytes()).hexdigest()
        self.f6_environment = copy.deepcopy(self.evidence["environment"])
        self.f6_measurement_window = copy.deepcopy(self.evidence["measurement_window"])

    def write_trust(self) -> None:
        self.trust_path.write_bytes(canonical_bytes(self.trust))
        self.trust_path.chmod(0o600)

    def _f6_evidence(self) -> dict:
        raw = self.artifact_path.read_bytes()
        cycles = []
        for sequence in range(1, 101):
            cycles.append(
                {
                    "sequence": sequence,
                    "direction": "A_TO_B" if sequence % 2 else "B_TO_A",
                    "installed_digest_verified": True,
                    "signature_verified": True,
                    "self_test": "PASS",
                    "shadow_side_effect_count": 0,
                    "drain": "PASS",
                    "route_switch": "PASS",
                    "rollback_check": "PASS",
                }
            )
        return {
            "schema_version": "dps.windows-zenno-verification-input/v1",
            "evidence_id": "external-evidence-0001",
            "evidence_kind": "REAL_EXTERNAL",
            "required": True,
            "status": "PASS",
            "baseline_commit": "a" * 40,
            "release_bom": {
                "bom_id": self.bom["bom_id"],
                "status": self.bom["status"],
                "path": str(self.bom_path.resolve()),
                "sha256": hashlib.sha256(self.bom_path.read_bytes()).hexdigest(),
                "artifact_sha256": self.module_digest,
            },
            "environment": {
                "environment_id": "env_windows_lab_01",
                "os_family": "Windows",
                "windows_version": "10.0.22631.3593",
                "zennodroid_version": "1.2.3-test",
                "dotnet_framework_version": "4.8.1",
                "csharp_language_version": "5.0",
                "codedom_compile": "PASS",
                "gac_resolution": "PASS",
                "dll_load": "PASS",
                "zenno_project_load": "PASS",
                "bridge_abi": "dps.zenno-bridge/v1",
                "adb_authorized_device_count": 1,
                "adb_authorization": "PASS",
                "loopback_host": "127.0.0.1",
                "loopback_port": 32145,
                "loopback_port_fixed": True,
                "loopback_only": True,
                "command_timeout_seconds": 30,
                "timeout_semantics": "FAIL_CLOSED",
                "error_semantics": "NATIVE_ERROR_PRESERVED",
                "connection_continuity": "PASS",
            },
            "measurement_window": {"started_at": iso(self.started), "finished_at": iso(self.finished)},
            "raw_artifacts": [
                {
                    "artifact_id": "raw-artifact-0001",
                    "path": str(self.artifact_path.resolve()),
                    "sha256": hashlib.sha256(raw).hexdigest(),
                    "size_bytes": len(raw),
                    "media_type": "application/x-ndjson",
                }
            ],
            "factory_binding": {
                "upgrade_stream_id": "upgrade-stream-0001",
                "instruction_receipt_id": "instruction-receipt-0001",
                "source_event_sha256": "b" * 64,
                "implementer_identity": "product-implementer",
                "evidence_issuer_identity": "external-evidence-issuer",
                "release_approver_identity": "human-release-approver",
            },
            "payload": {
                "capability_probe": {
                    "status": "PASS",
                    "windows_version": "10.0.22631.3593",
                    "zennodroid_version": "1.2.3-test",
                    "dotnet_framework_version": "4.8.1",
                    "csharp_language_version": "5.0",
                    "codedom_compile": "PASS",
                    "gac_resolution": "PASS",
                    "dll_load": "PASS",
                    "zenno_project_load": "PASS",
                    "bridge_abi": "dps.zenno-bridge/v1",
                    "adb_authorized_device_count": 1,
                    "adb_authorization": "PASS",
                    "loopback_host": "127.0.0.1",
                    "loopback_port": 32145,
                    "loopback_port_fixed": True,
                    "loopback_only": True,
                    "command_timeout_seconds": 30,
                    "timeout_semantics": "FAIL_CLOSED",
                    "error_semantics": "NATIVE_ERROR_PRESERVED",
                    "connection_continuity": "PASS",
                },
                "zenno_process": {
                    "pid_before": 1234,
                    "pid_after": 1234,
                    "started_at_before": iso(self.started - timedelta(hours=1)),
                    "started_at_after": iso(self.started - timedelta(hours=1)),
                    "observed_at_before": iso(self.started),
                    "observed_at_after": iso(self.finished),
                },
                "ab_cycles": cycles,
                "observation_hours": 24,
                "recovery_checks": {
                    "crash_window": "PASS",
                    "duplicate_delivery": "PASS",
                    "offline_recovery": "PASS",
                    "unknown_contract_rejected": "PASS",
                    "unknown_step_rejected": "PASS",
                },
                "rollback": {"status": "PASS", "maximum_minutes": 4.5, "old_bom_restored": True},
            },
            "attestation": {
                "facts": {
                    "schema_version": "1.0.0",
                    "runner_key_id": "runner-key-0001",
                    "algorithm": "ECDSA_P256_SHA256_P1363",
                    "issued_at": iso(self.finished + timedelta(minutes=1)),
                    "payload_sha256": "0" * 64,
                    "evidence_issuer_identity": "external-evidence-issuer",
                    "raw_artifacts_observed": True,
                    "role_separation_verified": True,
                    "real_environment_observed": True,
                },
                "signature_base64": "synthetic-and-never-issued",
            },
        }

    def use_f7(self) -> None:
        self.started = self.f6_finished + timedelta(hours=1)
        self.finished = self.started + timedelta(hours=2)
        payload, artifact_bindings = valid_f7_case(
            self.started,
            self.finished,
            self.evidence["release_bom"]["bom_id"],
            self.evidence["release_bom"]["sha256"],
        )
        raw_artifacts = []
        for artifact_id, binding in artifact_bindings.items():
            artifact_path = self.root / f"{artifact_id}.json"
            artifact_path.write_bytes(binding["bytes"])
            raw_artifacts.append(
                {
                    "artifact_id": artifact_id,
                    "path": str(artifact_path.resolve()),
                    "sha256": binding["sha256"],
                    "size_bytes": len(binding["bytes"]),
                    "media_type": binding["media_type"],
                }
            )
        environment = _f7_test_environment()
        self.evidence_path = self.root / "f7-evidence.json"
        self.evidence["schema_version"] = "dps.device-gbrain-verification-input/v3"
        self.evidence["evidence_id"] = "external-f7-evidence-0001"
        self.evidence["environment"] = environment
        self.evidence["measurement_window"] = {
            "started_at": iso(self.started),
            "finished_at": iso(self.finished),
        }
        self.evidence["raw_artifacts"] = raw_artifacts
        self.evidence["payload"] = payload
        self.evidence["attestation"]["facts"]["issued_at"] = iso(self.finished + timedelta(minutes=1))
        self.device_key_path = self.root / "f7-device-public.pem"
        self.bom_key_path = self.root / "f7-bom-public.pem"
        self.device_key_path.write_bytes(b"synthetic distinct F7 device public material")
        self.bom_key_path.write_bytes(b"synthetic distinct F7 BOM public material")
        windows_issuer = self.trust["trusted_issuers"][0]
        windows_issuer["issuer_identity"] = "external-windows-evidence-issuer"
        windows_issuer["runner_key_id"] = "runner-key-windows-0001"
        windows_issuer["allowed_verification_levels"] = ["WINDOWS_VERIFIED"]
        self.trust["trusted_issuers"].append(
            {
                "issuer_identity": "external-device-evidence-issuer",
                "runner_key_id": "runner-key-device-0001",
                "algorithm": "ECDSA_P256_SHA256_P1363",
                "public_key_pem_path": str(self.device_key_path.resolve()),
                "public_key_sha256": hashlib.sha256(self.device_key_path.read_bytes()).hexdigest(),
                "allowed_verification_levels": ["DEVICE_VERIFIED"],
            }
        )
        bom_signer = self.trust["trusted_bom_signers"][0]
        bom_signer["public_key_pem_path"] = str(self.bom_key_path.resolve())
        bom_signer["public_key_sha256"] = hashlib.sha256(self.bom_key_path.read_bytes()).hexdigest()
        self.evidence["factory_binding"]["evidence_issuer_identity"] = "external-device-evidence-issuer"
        self.evidence["attestation"]["facts"]["runner_key_id"] = "runner-key-device-0001"
        self.evidence["attestation"]["facts"]["evidence_issuer_identity"] = "external-device-evidence-issuer"
        self.trust["environment_policies"].append(
            {
                "verification_level": "DEVICE_VERIFIED",
                "required_claims": environment,
            }
        )
        self.trust["prerequisite_receipt_policy"] = {
            "repository_id": "repo:dps",
            "maximum_age_seconds": 7 * 24 * 3600,
            "maximum_clock_skew_seconds": 60,
            "revoked_receipt_ids": [],
            "required_source_evidence": {
                "evidence_id": "external-f6-evidence-0001",
                "evidence_sha256": self.f6_evidence_sha256,
                "environment_id": self.f6_environment["environment_id"],
                "environment_sha256": hashlib.sha256(canonical_bytes(self.f6_environment)).hexdigest(),
                "measurement_started_at": self.f6_measurement_window["started_at"],
                "measurement_finished_at": self.f6_measurement_window["finished_at"],
                "edge_installation_id": environment["edge_installation_id"],
                "zenno_installation_id": environment["zenno_installation_id"],
            },
        }
        self.write_trust()
        self.refresh_f7_prerequisite_receipt()
        self.reseal()

    def refresh_f7_prerequisite_receipt(self, private_key: Path | None = None) -> None:
        receipt_id = self.evidence["payload"]["f6_prerequisite"]["receipt_id"]
        artifact_id = self.evidence["payload"]["f6_prerequisite"]["raw_artifact_id"]
        receipt = {
            "schema_version": "dps.f7-windows-prerequisite-receipt/v1",
            "receipt_id": receipt_id,
            "repository_id": self.evidence["payload"]["repository_id"],
            "source_stage": "f6",
            "verification_level": "WINDOWS_VERIFIED",
            "status": "PASS",
            "required": True,
            "evidence_kind": "REAL_EXTERNAL",
            "evidence_id": "external-f6-evidence-0001",
            "source_evidence_sha256": self.f6_evidence_sha256,
            "source_environment_id": self.f6_environment["environment_id"],
            "source_environment_sha256": hashlib.sha256(canonical_bytes(self.f6_environment)).hexdigest(),
            "source_measurement_started_at": self.f6_measurement_window["started_at"],
            "source_measurement_finished_at": self.f6_measurement_window["finished_at"],
            "edge_installation_id": self.evidence["environment"]["edge_installation_id"],
            "zenno_installation_id": self.evidence["environment"]["zenno_installation_id"],
            "baseline_commit": self.evidence["baseline_commit"],
            "release_bom_id": self.evidence["release_bom"]["bom_id"],
            "release_bom_sha256": self.evidence["release_bom"]["sha256"],
            "candidate_artifact_sha256": self.evidence["release_bom"]["artifact_sha256"],
            "trust_policy_id": self.trust["policy_id"],
            "trust_policy_sha256": hashlib.sha256(canonical_bytes(self.trust)).hexdigest(),
            "issued_at": iso(self.f6_finished + timedelta(minutes=1)),
            "expires_at": iso(self.f6_finished + timedelta(days=7)),
            "evidence_issuer_identity": "external-windows-evidence-issuer",
            "signature": {
                "algorithm": "ECDSA_P256_SHA256_P1363",
                "runner_key_id": "runner-key-windows-0001",
                "value": "synthetic-f6-prerequisite-signature",
            },
        }
        if private_key is not None:
            unsigned_receipt = copy.deepcopy(receipt)
            unsigned_receipt.pop("signature")
            receipt["signature"]["value"] = _openssl_sign_p1363(
                private_key,
                b"dps-f7-windows-prerequisite-receipt/v1\n" + canonical_bytes(unsigned_receipt),
            )
        raw = canonical_bytes(receipt)
        digest = hashlib.sha256(raw).hexdigest()
        path = self.root / f"{artifact_id}.json"
        path.write_bytes(raw)
        metadata = next(
            (item for item in self.evidence["raw_artifacts"] if item["artifact_id"] == artifact_id),
            None,
        )
        value = {
            "artifact_id": artifact_id,
            "path": str(path.resolve()),
            "sha256": digest,
            "size_bytes": len(raw),
            "media_type": "application/json",
        }
        if metadata is None:
            self.evidence["raw_artifacts"].append(value)
        else:
            metadata.clear()
            metadata.update(value)
        self.evidence["payload"]["f6_prerequisite"]["raw_artifact_sha256"] = digest

    def use_f9(self) -> None:
        self.finished = self.started + timedelta(hours=200)
        dependency_artifact_id = "f9-dependency-dag-0001"
        compatibility_artifact_id = "f9-compatibility-matrix-0001"
        policy_artifact_id = "f9-compatibility-policy-0001"
        previous_bom_artifact_id = "f9-previous-stable-bom-0001"
        execution_artifact_id = "f9-compatibility-execution-0001"
        contract_schema_artifact_id = "f9-contract-schema-probe-0001"
        provider_manifest_artifact_id = "f9-module-manifest-edge-worker-0001"
        consumer_manifest_artifact_id = "f9-module-manifest-scale-consumer-0001"
        contract_id = "scale.compatibility.probe"
        contract_source = "Modules/windows-edge-worker/contracts/provided/scale.compatibility.probe.v1.schema.json"
        provided = {
            "contractId": contract_id,
            "major": 1,
            "source": contract_source,
            "status": "proposed",
            "mode": "active",
            "ownerModule": "windows-edge-worker",
        }
        consumed = dict(provided)
        provider_manifest = synthetic_module_manifest(
            "windows-edge-worker",
            provided=[provided],
            outbound=[
                synthetic_communication_edge(
                    "scale-contract-consumer", contract_id, "outbound"
                )
            ],
        )
        consumer_manifest = synthetic_module_manifest(
            "scale-contract-consumer",
            [("windows-edge-worker", "compatibility probe provider")],
            consumed=[consumed],
            inbound=[
                synthetic_communication_edge(
                    "windows-edge-worker", contract_id, "inbound"
                )
            ],
        )
        manifests = [provider_manifest, consumer_manifest]
        manifest_bindings = []
        manifest_files = []
        for manifest, artifact_id in (
            (provider_manifest, provider_manifest_artifact_id),
            (consumer_manifest, consumer_manifest_artifact_id),
        ):
            module_id = manifest["module"]["id"]
            raw = canonical_bytes(manifest)
            digest = hashlib.sha256(raw).hexdigest()
            path = self.root / f"f9-{module_id}-module.json"
            path.write_bytes(raw)
            manifest_bindings.append(
                {
                    "module_id": module_id,
                    "raw_artifact_id": artifact_id,
                    "manifest_sha256": digest,
                }
            )
            manifest_files.append((artifact_id, path, raw, digest))

        dependency_artifact = dependency_artifact_from_manifests(manifests)
        dependency_bytes = canonical_bytes(dependency_artifact)
        dependency_digest = hashlib.sha256(dependency_bytes).hexdigest()
        dependency_path = self.root / "f9-dependency-dag.json"
        dependency_path.write_bytes(dependency_bytes)

        compatibility_artifact = compatibility_artifact_from_manifests(manifests)
        compatibility_bytes = canonical_bytes(compatibility_artifact)
        compatibility_digest = hashlib.sha256(compatibility_bytes).hexdigest()
        compatibility_path = self.root / "f9-compatibility-matrix.json"
        compatibility_path.write_bytes(compatibility_bytes)

        policy_path = Path(__file__).resolve().parents[3] / "governance" / "policies" / "compatibility-policy.yaml"
        policy_bytes = policy_path.read_bytes()
        policy_digest = hashlib.sha256(policy_bytes).hexdigest()
        policy_copy = self.root / "f9-compatibility-policy.json"
        policy_copy.write_bytes(policy_bytes)

        contract_schema = {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "type": "object",
            "properties": {"producer_module": {"const": "windows-edge-worker"}},
        }
        contract_schema_bytes = canonical_bytes(contract_schema)
        contract_schema_digest = hashlib.sha256(contract_schema_bytes).hexdigest()
        contract_schema_path = self.root / "f9-contract-schema-probe.json"
        contract_schema_path.write_bytes(contract_schema_bytes)

        consumer_artifact_path = self.root / "scale-contract-consumer.zip"
        consumer_artifact_path.write_bytes(b"signed scale contract consumer candidate")
        consumer_artifact_digest = hashlib.sha256(consumer_artifact_path.read_bytes()).hexdigest()
        previous_provider_digest = hashlib.sha256(b"previous edge worker").hexdigest()
        previous_consumer_digest = hashlib.sha256(b"previous scale consumer").hexdigest()
        previous_bom = {
            "schema_version": "dps.release-bom/v1",
            "bom_id": "bom-stable-0001",
            "status": "STABLE",
            "release_bom_generation": 1,
            "activation_token_sha256": "8" * 64,
            "modules": [
                {"module_id": "windows-edge-worker", "version": "0.9.0", "sha256": previous_provider_digest},
                {"module_id": "scale-contract-consumer", "version": "0.9.0", "sha256": previous_consumer_digest},
            ],
            "rollout": {"shadow_artifact_sha256": previous_provider_digest},
            "signature": {
                "algorithm": "ecdsa-p256-sha256",
                "key_id": "bom-key-0001",
                "value": "not-a-real-previous-bom-signature",
            },
        }
        previous_bom_bytes = canonical_bytes(previous_bom)
        previous_bom_digest = hashlib.sha256(previous_bom_bytes).hexdigest()
        previous_bom_path = self.root / "f9-previous-stable-bom.json"
        previous_bom_path.write_bytes(previous_bom_bytes)

        self.bom["integration_commit"] = self.evidence["baseline_commit"]
        self.bom["release_bom_generation"] = 2
        self.bom["activation_token_sha256"] = "9" * 64
        self.bom["modules"] = [
            {
                "module_id": "windows-edge-worker",
                "version": "1.0.0",
                "sha256": self.module_digest,
                "manifest_sha256": manifest_bindings[0]["manifest_sha256"],
            },
            {
                "module_id": "scale-contract-consumer",
                "version": "1.0.0",
                "sha256": consumer_artifact_digest,
                "manifest_sha256": manifest_bindings[1]["manifest_sha256"],
            },
        ]
        self.bom["contracts"] = [
            {
                "contract_id": contract_id,
                "major": 1,
                "schema_sha256": contract_schema_digest,
                "owner_module": "windows-edge-worker",
            }
        ]
        self.bom["instruction_hashes"] = [
            {
                "path": "governance/policies/compatibility-policy.yaml",
                "sha256": policy_digest,
            }
        ]
        self.bom["dependency_dag_sha256"] = dependency_digest
        self.bom["compatibility_matrix_sha256"] = compatibility_digest
        self.bom["previous_stable_bom"] = previous_bom["bom_id"]
        self.bom["previous_stable_bom_sha256"] = previous_bom_digest
        self.bom_path.write_bytes(canonical_bytes(self.bom))
        bom_digest = hashlib.sha256(self.bom_path.read_bytes()).hexdigest()
        self.evidence["release_bom"]["sha256"] = bom_digest

        receipt_artifact_id = "f8-canary-receipt-artifact-0001"
        canary_receipt = {
            "schema_version": "dps.external-verification-receipt/v1",
            "receipt_id": "f8-canary-receipt-0001",
            "source_stage": "f8",
            "verification_level": "CANARY_VERIFIED",
            "status": "PASS",
            "required": True,
            "evidence_kind": "REAL_EXTERNAL",
            "evidence_id": "external-f8-evidence-0001",
            "baseline_commit": self.evidence["baseline_commit"],
            "release_bom_id": self.bom["bom_id"],
            "release_bom_sha256": bom_digest,
            "candidate_artifact_sha256": self.module_digest,
            "issued_at": iso(self.started - timedelta(minutes=1)),
            "evidence_issuer_identity": "external-evidence-issuer",
            "signature": {
                "algorithm": "ECDSA_P256_SHA256_P1363",
                "runner_key_id": "runner-key-0001",
                "value": "synthetic-and-never-issued",
            },
        }
        receipt_bytes = canonical_bytes(canary_receipt)
        receipt_digest = hashlib.sha256(receipt_bytes).hexdigest()
        receipt_path = self.root / "f8-canary-receipt.json"
        receipt_path.write_bytes(receipt_bytes)

        base_raw = self.artifact_path.read_bytes()
        self.evidence_path = self.root / "f9-evidence.json"
        self.evidence["schema_version"] = "dps.scale-verification-input/v1"
        self.evidence["evidence_id"] = "external-f9-evidence-0001"
        self.evidence["environment"] = {
            "environment_id": "env_scale_lab_01",
            "os_family": "Windows+Android",
        }
        self.evidence["measurement_window"] = {
            "started_at": iso(self.started),
            "finished_at": iso(self.finished),
        }
        self.evidence["raw_artifacts"] = [
            {
                "artifact_id": "raw-artifact-0001",
                "path": str(self.artifact_path.resolve()),
                "sha256": hashlib.sha256(base_raw).hexdigest(),
                "size_bytes": len(base_raw),
                "media_type": "application/x-ndjson",
            },
            {
                "artifact_id": dependency_artifact_id,
                "path": str(dependency_path.resolve()),
                "sha256": dependency_digest,
                "size_bytes": len(dependency_bytes),
                "media_type": "application/json",
            },
            {
                "artifact_id": compatibility_artifact_id,
                "path": str(compatibility_path.resolve()),
                "sha256": compatibility_digest,
                "size_bytes": len(compatibility_bytes),
                "media_type": "application/json",
            },
            {
                "artifact_id": policy_artifact_id,
                "path": str(policy_copy.resolve()),
                "sha256": policy_digest,
                "size_bytes": len(policy_bytes),
                "media_type": "application/json",
            },
            {
                "artifact_id": previous_bom_artifact_id,
                "path": str(previous_bom_path.resolve()),
                "sha256": previous_bom_digest,
                "size_bytes": len(previous_bom_bytes),
                "media_type": "application/json",
            },
            {
                "artifact_id": contract_schema_artifact_id,
                "path": str(contract_schema_path.resolve()),
                "sha256": contract_schema_digest,
                "size_bytes": len(contract_schema_bytes),
                "media_type": "application/json",
            },
            {
                "artifact_id": receipt_artifact_id,
                "path": str(receipt_path.resolve()),
                "sha256": receipt_digest,
                "size_bytes": len(receipt_bytes),
                "media_type": "application/json",
            },
        ] + [
            {
                "artifact_id": artifact_id,
                "path": str(path.resolve()),
                "sha256": digest,
                "size_bytes": len(raw),
                "media_type": "application/json",
            }
            for artifact_id, path, raw, digest in manifest_files
        ]
        payload = valid_f9_payload()
        for name in ("sustained", "burst", "simulated"):
            load_run = payload["load_runs"][name]
            load_artifact = build_f9_load_artifact(name, load_run, self.evidence["environment"]["environment_id"])
            load_bytes = canonical_bytes(load_artifact)
            load_digest = hashlib.sha256(load_bytes).hexdigest()
            load_run["artifact_sha256"] = load_digest
            load_path = self.root / f"{load_run['artifact_id']}.json"
            load_path.write_bytes(load_bytes)
            self.evidence["raw_artifacts"].append(
                {
                    "artifact_id": load_run["artifact_id"],
                    "path": str(load_path.resolve()),
                    "sha256": load_digest,
                    "size_bytes": len(load_bytes),
                    "media_type": "application/json",
                }
            )

        candidate_modules = external_gate_module._bom_module_inventory(
            self.bom, "candidate Release BOM"
        )
        previous_modules = external_gate_module._bom_module_inventory(
            previous_bom, "previous stable Release BOM"
        )
        matrix_rows = [
            row
            for row in compatibility_artifact["declarationMatrix"]
            if row["candidateGreenEligible"] is True
        ]
        row_results = []
        row_ids = []
        graph = {
            node: {
                edge["provider"]
                for edge in dependency_artifact["edges"]
                if edge["consumer"] == node
            }
            for node in dependency_artifact["nodes"]
        }
        for row_index, row in enumerate(matrix_rows):
            row_id, row_sha = external_gate_module._matrix_row_identity(row)
            row_ids.append(row_id)
            runtime_producer = row.get("runtimeProducerModule", row["producerModule"])
            transport_sender = row.get("transportSenderModule", runtime_producer)
            communication_sha = row.get(
                "communicationPairSha256", hashlib.sha256(canonical_bytes(row)).hexdigest()
            )
            combinations = []
            for combination_index, combination_id in enumerate(
                ("N/N", "N/N-1", "N-1/N", "N-1/N-1")
            ):
                producer_selection, consumer_selection = external_gate_module._combination_selection(
                    combination_id,
                    runtime_producer,
                    row["consumerModule"],
                    candidate_modules,
                    previous_modules,
                )
                expectation = "RUNNABLE"
                observation_id = f"f9-compat-observation-{row_index + 1:04d}-{combination_index + 1:04d}"
                observation = {
                    "schema_version": "dps.compatibility-combination-observation/v1",
                    "artifact_id": observation_id,
                    "evidence_kind": "REAL_EXTERNAL",
                    "matrix_row_id": row_id,
                    "combination_id": combination_id,
                    "expectation": expectation,
                    "environment_id": self.evidence["environment"]["environment_id"],
                    "candidate_bom_sha256": bom_digest,
                    "previous_stable_bom_sha256": previous_bom_digest,
                    "compatibility_snapshot_sha256": compatibility_digest,
                    "producer": producer_selection,
                    "consumer": consumer_selection,
                    "started_at": iso(self.started + timedelta(hours=1 + combination_index * 2)),
                    "finished_at": iso(self.started + timedelta(hours=2 + combination_index * 2)),
                    "executed_test_count": 10,
                    "skip_count": 0,
                    "partial_count": 0,
                    "not_run_count": 0,
                    "observed_outcome": "RUNNABLE",
                    "side_effect_count": 0,
                    "status": "PASS",
                }
                observation_bytes = canonical_bytes(observation)
                observation_digest = hashlib.sha256(observation_bytes).hexdigest()
                observation_path = self.root / f"{observation_id}.json"
                observation_path.write_bytes(observation_bytes)
                self.evidence["raw_artifacts"].append(
                    {
                        "artifact_id": observation_id,
                        "path": str(observation_path.resolve()),
                        "sha256": observation_digest,
                        "size_bytes": len(observation_bytes),
                        "media_type": "application/json",
                    }
                )
                combinations.append(
                    {
                        "combination_id": combination_id,
                        "expectation": expectation,
                        "producer": producer_selection,
                        "consumer": consumer_selection,
                        "raw_evidence_artifact_id": observation_id,
                        "raw_evidence_sha256": observation_digest,
                        "evidence_status": "PASS",
                        "evidence_class": "REAL_CANDIDATE_ARTIFACT",
                        "environment_id": self.evidence["environment"]["environment_id"],
                        "executed_test_count": 10,
                        "skip_count": 0,
                        "partial_count": 0,
                        "not_run_count": 0,
                    }
                )
            row_results.append(
                {
                    "matrix_row_id": row_id,
                    "matrix_row_sha256": row_sha,
                    "contract_id": row["contractId"],
                    "major": row["major"],
                    "owner_module": row["ownerModule"],
                    "runtime_producer_module": runtime_producer,
                    "transport_sender_module": transport_sender,
                    "consumer_module": row["consumerModule"],
                    "producer_mode": "active",
                    "consumer_mode": "active",
                    "communication_pair_sha256": communication_sha,
                    "combination_results": combinations,
                    "row_status": "PASS",
                }
            )
        execution_artifact = {
            "schema_version": "dps.compatibility-execution-evidence/v1",
            "artifact_id": execution_artifact_id,
            "evidence_kind": "REAL_EXTERNAL",
            "required": True,
            "status": "PASS",
            "integration_commit": self.bom["integration_commit"],
            "candidate_release_bom": {
                "bom_id": self.bom["bom_id"],
                "sha256": bom_digest,
                "generation": self.bom["release_bom_generation"],
                "activation_token_sha256": self.bom["activation_token_sha256"],
                "signature_sha256": hashlib.sha256(canonical_bytes(self.bom["signature"])).hexdigest(),
            },
            "previous_stable_release_bom": {
                "bom_id": previous_bom["bom_id"],
                "sha256": previous_bom_digest,
                "generation": previous_bom["release_bom_generation"],
                "activation_token_sha256": previous_bom["activation_token_sha256"],
                "signature_sha256": hashlib.sha256(canonical_bytes(previous_bom["signature"])).hexdigest(),
            },
            "compatibility_snapshot": {
                "schema_version": "dps.compatibility-matrix/v2",
                "sha256": compatibility_digest,
                "policy_sha256": policy_digest,
            },
            "environment_id": self.evidence["environment"]["environment_id"],
            "issued_at": iso(self.started - timedelta(hours=1)),
            "expires_at": iso(self.finished + timedelta(hours=1)),
            "row_set_sha256": hashlib.sha256(canonical_bytes(sorted(row_ids))).hexdigest(),
            "row_results": row_results,
            "compatibility_group": external_gate_module._expected_compatibility_group(
                compatibility_artifact,
                graph,
                candidate_modules,
                compatibility_digest,
                bom_digest,
            ),
            "candidate_green_eligible": True,
            "attestation": {
                "evidence_issuer_identity": "external-evidence-issuer",
                "runner_key_id": "runner-key-0001",
                "algorithm": "ECDSA_P256_SHA256_P1363",
                "signature_base64": "synthetic-and-never-issued",
            },
        }
        execution_bytes = canonical_bytes(execution_artifact)
        execution_digest = hashlib.sha256(execution_bytes).hexdigest()
        execution_path = self.root / "f9-compatibility-execution.json"
        execution_path.write_bytes(execution_bytes)
        self.evidence["raw_artifacts"].append(
            {
                "artifact_id": execution_artifact_id,
                "path": str(execution_path.resolve()),
                "sha256": execution_digest,
                "size_bytes": len(execution_bytes),
                "media_type": "application/json",
            }
        )
        payload["canary_prerequisite"] = {
            "receipt_id": canary_receipt["receipt_id"],
            "raw_artifact_id": receipt_artifact_id,
            "raw_artifact_sha256": receipt_digest,
        }
        payload["module_rollout_lines"] = {
            "dependency_graph_artifact_id": dependency_artifact_id,
            "dependency_graph_sha256": dependency_digest,
            "compatibility_matrix_artifact_id": compatibility_artifact_id,
            "compatibility_matrix_sha256": compatibility_digest,
            "compatibility_policy_artifact_id": policy_artifact_id,
            "compatibility_policy_sha256": policy_digest,
            "previous_stable_bom_artifact_id": previous_bom_artifact_id,
            "previous_stable_bom_sha256": previous_bom_digest,
            "compatibility_execution_artifact_id": execution_artifact_id,
            "compatibility_execution_sha256": execution_digest,
            "manifest_artifacts": manifest_bindings,
            "contract_schema_artifacts": [
                {
                    "contract_id": contract_id,
                    "major": 1,
                    "owner_module": "windows-edge-worker",
                    "raw_artifact_id": contract_schema_artifact_id,
                    "schema_sha256": contract_schema_digest,
                }
            ],
            "lines": [
                {
                    "line_id": "f9-rollout-line-0001",
                    "module_ids": ["windows-edge-worker", "scale-contract-consumer"],
                    "status": "PASS",
                }
            ],
        }
        self.evidence["payload"] = payload
        self.evidence["attestation"]["facts"]["issued_at"] = iso(self.finished + timedelta(minutes=1))
        self.trust["trusted_issuers"][0]["allowed_verification_levels"] = [
            "CANARY_VERIFIED",
            "SCALE_VERIFIED",
        ]
        self.trust["environment_policies"][0] = {
            "verification_level": "SCALE_VERIFIED",
            "required_claims": self.evidence["environment"],
        }
        self.write_trust()
        self.reseal()

    def reseal(self) -> None:
        unsigned = copy.deepcopy(self.evidence)
        unsigned.pop("attestation")
        self.evidence["attestation"]["facts"]["payload_sha256"] = hashlib.sha256(canonical_bytes(unsigned)).hexdigest()
        self.evidence_path.write_bytes(canonical_bytes(self.evidence))


def accept_signature(_key: bytes, _payload: bytes, _signature: object) -> None:
    return None


def reject_signature(_key: bytes, _payload: bytes, _signature: object) -> None:
    raise ExternalGateError("invalid_signature", "synthetic signature rejection")


def _read_der_length(value: bytes, offset: int) -> tuple[int, int]:
    first = value[offset]
    offset += 1
    if first < 0x80:
        return first, offset
    length_bytes = first & 0x7F
    if length_bytes == 0 or length_bytes > 2 or offset + length_bytes > len(value):
        raise AssertionError("invalid DER length in synthetic test signature")
    return int.from_bytes(value[offset : offset + length_bytes], "big"), offset + length_bytes


def _der_ecdsa_to_p1363(value: bytes) -> bytes:
    if not value or value[0] != 0x30:
        raise AssertionError("synthetic test signature is not a DER sequence")
    sequence_length, offset = _read_der_length(value, 1)
    if offset + sequence_length != len(value):
        raise AssertionError("synthetic test signature has an invalid sequence length")
    integers: list[bytes] = []
    for _ in range(2):
        if offset >= len(value) or value[offset] != 0x02:
            raise AssertionError("synthetic test signature is missing an integer")
        integer_length, offset = _read_der_length(value, offset + 1)
        integer = value[offset : offset + integer_length]
        offset += integer_length
        integer = integer.lstrip(b"\x00") or b"\x00"
        if len(integer) > 32:
            raise AssertionError("synthetic P-256 signature integer is too large")
        integers.append(integer.rjust(32, b"\x00"))
    if offset != len(value):
        raise AssertionError("synthetic test signature has trailing DER bytes")
    return integers[0] + integers[1]


def _openssl_sign_p1363(private_key: Path, payload: bytes) -> str:
    openssl = shutil.which("openssl")
    if openssl is None:
        raise unittest.SkipTest("OpenSSL is required for the real-signature fixture")
    with tempfile.TemporaryDirectory() as directory:
        payload_path = Path(directory) / "payload.bin"
        signature_path = Path(directory) / "signature.der"
        payload_path.write_bytes(payload)
        subprocess.run(
            [openssl, "dgst", "-sha256", "-sign", str(private_key), "-out", str(signature_path), str(payload_path)],
            check=True,
            capture_output=True,
            timeout=20,
        )
        return base64.b64encode(_der_ecdsa_to_p1363(signature_path.read_bytes())).decode("ascii")


class ExternalGateAttackTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.fixture = Fixture(Path(self.temporary.name))

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def decision(self, verifier=accept_signature):
        return run_gate("f6", self.fixture.evidence_path, self.fixture.trust_path.resolve(), verifier)

    def test_missing_evidence_is_waiting_external_and_nonzero(self) -> None:
        decision = run_gate("f6", None, self.fixture.trust_path.resolve(), accept_signature)
        self.assertEqual("WAITING_EXTERNAL", decision.status)
        self.assertNotEqual(0, decision.exit_code)

    def test_missing_trust_policy_is_waiting_external_and_nonzero(self) -> None:
        decision = run_gate("f6", self.fixture.evidence_path, None, accept_signature)
        self.assertEqual("WAITING_EXTERNAL", decision.status)
        self.assertEqual("trust_policy_missing", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    @unittest.skipIf(os.name == "nt", "POSIX no-follow descriptor test")
    def test_trust_policy_parent_symlink_is_rejected(self) -> None:
        real_parent = self.fixture.root / "real-trust-parent"
        real_parent.mkdir()
        policy = real_parent / "policy.json"
        policy.write_bytes(canonical_bytes(self.fixture.trust))
        policy.chmod(0o600)
        linked_parent = self.fixture.root / "linked-trust-parent"
        linked_parent.symlink_to(real_parent, target_is_directory=True)
        decision = run_gate(
            "f6",
            self.fixture.evidence_path,
            linked_parent / "policy.json",
            accept_signature,
        )
        self.assertEqual("unsafe_trust_policy", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    @unittest.skipIf(os.name == "nt", "POSIX descriptor swap test")
    def test_trust_policy_post_open_path_swap_cannot_change_read_bytes(self) -> None:
        policy_path = self.fixture.trust_path.resolve()
        attacker_path = self.fixture.root / "attacker-policy.json"
        attacker_path.write_text('{"attacker":true}', encoding="utf-8")
        attacker_path.chmod(0o600)
        original_read = external_gate_module.os.read
        swapped = False

        def swap_before_read(descriptor, count):
            nonlocal swapped
            if not swapped:
                os.replace(attacker_path, policy_path)
                swapped = True
            return original_read(descriptor, count)

        with mock.patch.object(external_gate_module.os, "read", side_effect=swap_before_read):
            decision = self.decision()
        self.assertTrue(swapped)
        self.assertEqual(0, decision.exit_code)
        self.assertEqual(ELIGIBLE, decision.decision)

    def test_mock_cannot_satisfy_external_gate(self) -> None:
        self.fixture.evidence["evidence_kind"] = "MOCK"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("FAIL", decision.status)
        self.assertEqual("non_real_evidence", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_skip_nested_outcome_is_failure(self) -> None:
        self.fixture.evidence["payload"]["ab_cycles"][7]["self_test"] = "SKIP"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("required_outcome_not_pass", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_partial_top_level_outcome_is_failure(self) -> None:
        self.fixture.evidence["status"] = "PARTIAL"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("required_outcome_not_pass", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_wrong_issuer_is_failure(self) -> None:
        self.fixture.evidence["attestation"]["facts"]["evidence_issuer_identity"] = "attacker-issuer"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("issuer_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_wrong_runner_key_is_failure(self) -> None:
        self.fixture.evidence["attestation"]["facts"]["runner_key_id"] = "attacker-key-0001"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("trust_policy_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_issuer_scope_is_computed_by_stage_and_enforced(self) -> None:
        self.fixture.trust["trusted_issuers"][0]["allowed_verification_levels"] = ["DEVICE_VERIFIED"]
        self.fixture.write_trust()
        decision = self.decision()
        self.assertEqual("issuer_scope_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_platform_mismatch_is_failure(self) -> None:
        self.fixture.evidence["environment"]["os_family"] = "macOS"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_environment_claim_value", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_environment_rejects_unallowlisted_extra_claim(self) -> None:
        self.fixture.evidence["environment"]["runner_note"] = "benign"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("environment_claim_not_allowlisted", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_trust_policy_cannot_expand_stage_environment_allowlist(self) -> None:
        synthetic_value = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd"
        self.fixture.evidence["environment"]["runner_note"] = synthetic_value
        self.fixture.trust["environment_policies"][0]["required_claims"]["runner_note"] = synthetic_value
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("unsafe_environment_policy", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_environment_rejects_secret_field_even_when_policy_allowlists_it(self) -> None:
        self.fixture.evidence["environment"]["api_key"] = "redacted"
        self.fixture.trust["environment_policies"][0]["required_claims"]["api_key"] = "redacted"
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("sensitive_environment_claim", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_environment_rejects_camel_case_token_field(self) -> None:
        self.fixture.evidence["environment"]["accessToken"] = "redacted"
        self.fixture.trust["environment_policies"][0]["required_claims"]["accessToken"] = "redacted"
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("sensitive_environment_claim", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_environment_rejects_secret_like_value_in_an_allowlisted_claim(self) -> None:
        self.fixture.evidence["environment"]["runner_note"] = "Bearer test-production-token"
        self.fixture.trust["environment_policies"][0]["required_claims"]["runner_note"] = (
            "Bearer test-production-token"
        )
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("sensitive_environment_claim", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_environment_rejects_nested_secret_object_even_when_allowlisted(self) -> None:
        nested = {"api_key": "synthetic-not-real"}
        self.fixture.evidence["environment"]["runner_note"] = nested
        self.fixture.trust["environment_policies"][0]["required_claims"]["runner_note"] = nested
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_environment_claim_type", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_environment_rejects_scope_or_credential_shaped_value(self) -> None:
        synthetic_value = "pa-A1b2C3d4E5f6G7h8J9k0A1b2C3d4E5f6G7h8J9k0"
        self.fixture.evidence["environment"]["runner_note"] = synthetic_value
        self.fixture.trust["environment_policies"][0]["required_claims"]["runner_note"] = synthetic_value
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("sensitive_environment_claim", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_environment_claim_types_must_match_exactly(self) -> None:
        self.fixture.evidence["environment"]["zennodroid_version"] = True
        self.fixture.trust["environment_policies"][0]["required_claims"]["zennodroid_version"] = 1
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_environment_claim_type", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_wrong_wave_order_is_failure(self) -> None:
        self.fixture.evidence["payload"]["ab_cycles"][0]["direction"] = "B_TO_A"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("wave_sequence_invalid", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_threshold_breach_is_failure(self) -> None:
        self.fixture.evidence["payload"]["observation_hours"] = 23
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("threshold_not_met", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_probe_requires_codedom_gac_dll_and_zenno_project_load(self) -> None:
        for field in ("codedom_compile", "gac_resolution", "dll_load", "zenno_project_load"):
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.evidence["payload"]["capability_probe"][field] = "SKIP"
                    fixture.reseal()
                    decision = run_gate("f6", fixture.evidence_path, fixture.trust_path.resolve(), accept_signature)
                    self.assertEqual("required_outcome_not_pass", decision.reason_code)
                    self.assertNotEqual(0, decision.exit_code)

    def test_f6_trust_policy_cannot_pin_failed_capability_as_acceptable(self) -> None:
        self.fixture.evidence["environment"]["codedom_compile"] = "SKIP"
        self.fixture.trust["environment_policies"][0]["required_claims"]["codedom_compile"] = "SKIP"
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_environment_claim_value", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_exact_zenno_version_must_match_trusted_environment(self) -> None:
        self.fixture.evidence["payload"]["capability_probe"]["zennodroid_version"] = "1.2.4-test"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("zennodroid_version_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_rejects_not_windows_even_when_trust_policy_matches(self) -> None:
        self.fixture.evidence["environment"]["os_family"] = "Linux"
        self.fixture.trust["environment_policies"][0]["required_claims"]["os_family"] = "Linux"
        self.fixture.write_trust()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("platform_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_rejects_untrusted_bridge_abi_v999(self) -> None:
        self.fixture.evidence["payload"]["capability_probe"]["bridge_abi"] = "dps.zenno-bridge/v999"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("capability_environment_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_rejects_untrusted_loopback_port_65432(self) -> None:
        self.fixture.evidence["payload"]["capability_probe"]["loopback_port"] = 65432
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("capability_environment_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_rejects_future_zenno_process_start_timestamp(self) -> None:
        future = iso(self.fixture.finished + timedelta(seconds=1))
        self.fixture.evidence["payload"]["zenno_process"]["started_at_before"] = future
        self.fixture.evidence["payload"]["zenno_process"]["started_at_after"] = future
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("zenno_process_time_invalid", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_process_observations_must_exactly_bound_signed_window(self) -> None:
        self.fixture.evidence["payload"]["zenno_process"]["observed_at_after"] = iso(
            self.fixture.finished + timedelta(seconds=1)
        )
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("zenno_measurement_window_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f6_requires_adb_authorization_and_connection_continuity(self) -> None:
        for field in ("adb_authorization", "connection_continuity"):
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.evidence["payload"]["capability_probe"][field] = "PARTIAL"
                    fixture.reseal()
                    decision = run_gate("f6", fixture.evidence_path, fixture.trust_path.resolve(), accept_signature)
                    self.assertEqual("required_outcome_not_pass", decision.reason_code)
                    self.assertNotEqual(0, decision.exit_code)

    def test_f6_rejects_non_loopback_or_nonfixed_bridge_port(self) -> None:
        for field, value, reason in (
            ("loopback_host", "0.0.0.0", "unsafe_bridge_endpoint"),
            ("loopback_port_fixed", False, "required_fact_false"),
        ):
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.evidence["payload"]["capability_probe"][field] = value
                    fixture.reseal()
                    decision = run_gate("f6", fixture.evidence_path, fixture.trust_path.resolve(), accept_signature)
                    self.assertEqual(reason, decision.reason_code)
                    self.assertNotEqual(0, decision.exit_code)

    def test_f6_rejects_unsafe_timeout_and_error_semantics(self) -> None:
        for field, value, reason in (
            ("timeout_semantics", "RETRY_FOREVER", "unsafe_timeout_semantics"),
            ("error_semantics", "COERCE_SUCCESS", "unsafe_error_semantics"),
        ):
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.evidence["payload"]["capability_probe"][field] = value
                    fixture.reseal()
                    decision = run_gate("f6", fixture.evidence_path, fixture.trust_path.resolve(), accept_signature)
                    self.assertEqual(reason, decision.reason_code)
                    self.assertNotEqual(0, decision.exit_code)

    def test_artifact_tampering_is_failure(self) -> None:
        self.fixture.artifact_path.write_text("tampered", encoding="utf-8")
        decision = self.decision()
        self.assertEqual("artifact_digest_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_raw_artifact_size_is_required_integer(self) -> None:
        self.fixture.evidence["raw_artifacts"][0]["size_bytes"] = None
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_value", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_bom_candidate_digest_must_match_exactly(self) -> None:
        self.fixture.evidence["release_bom"]["artifact_sha256"] = "c" * 64
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("bom_artifact_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_release_bom_duplicate_keys_are_rejected(self) -> None:
        bom_text = self.fixture.bom_path.read_text(encoding="utf-8")
        duplicate = bom_text.replace('"status":"SIGNED"', '"status":"STABLE","status":"SIGNED"', 1)
        self.fixture.bom_path.write_text(duplicate, encoding="utf-8")
        self.fixture.evidence["release_bom"]["sha256"] = hashlib.sha256(
            self.fixture.bom_path.read_bytes()
        ).hexdigest()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("duplicate_json_key", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_schema_id_fields_reject_pii_shaped_values(self) -> None:
        self.fixture.evidence["evidence_id"] = "person@example.com"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_external_id", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_schema_id_fields_reject_phone_shaped_numeric_values(self) -> None:
        self.fixture.evidence["evidence_id"] = "601234567890"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_external_id", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_invalid_signature_is_failure(self) -> None:
        decision = self.decision(reject_signature)
        self.assertEqual("invalid_signature", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_synthetic_acceptance_only_returns_eligibility_and_never_receipt(self) -> None:
        decision = self.decision(accept_signature)
        self.assertEqual(0, decision.exit_code)
        self.assertEqual(ELIGIBLE, decision.decision)
        self.assertFalse(decision.as_dict()["evidence_receipt_issued"])
        self.assertEqual("WINDOWS_VERIFIED", decision.target_verification_level)


class F7ExternalGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.binding_status = mock.patch.object(
            external_gate_module,
            "F7_GBRAIN_CONTRACT_BINDING_STATUS",
            "FROZEN",
        )
        self.binding_status.start()
        self.temporary = tempfile.TemporaryDirectory()
        self.fixture = Fixture(Path(self.temporary.name))
        self.fixture.use_f7()

    def tearDown(self) -> None:
        self.temporary.cleanup()
        self.binding_status.stop()

    def decision(self, verifier=accept_signature):
        return run_gate(
            "f7",
            self.fixture.evidence_path,
            self.fixture.trust_path.resolve(),
            verifier,
            clock=lambda: self.fixture.finished + timedelta(minutes=2),
        )

    def artifact_metadata(self, artifact_id: str) -> dict:
        return next(
            item
            for item in self.fixture.evidence["raw_artifacts"]
            if item["artifact_id"] == artifact_id
        )

    def artifact_document(self, artifact_id: str) -> dict:
        return json.loads(Path(self.artifact_metadata(artifact_id)["path"]).read_bytes())

    def _bind_artifact_digest(self, artifact_id: str, digest: str) -> None:
        payload = self.fixture.evidence["payload"]
        prerequisite = payload["f6_prerequisite"]
        if prerequisite["raw_artifact_id"] == artifact_id:
            prerequisite["raw_artifact_sha256"] = digest
        for group in ("projection_checks", "search_readback_checks", "semantic_artifacts"):
            for reference in payload[group]:
                if reference["raw_artifact_id"] == artifact_id:
                    reference["raw_artifact_sha256"] = digest

    def replace_artifact_raw(self, artifact_id: str, raw: bytes) -> None:
        metadata = self.artifact_metadata(artifact_id)
        Path(metadata["path"]).write_bytes(raw)
        digest = hashlib.sha256(raw).hexdigest()
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        self._bind_artifact_digest(artifact_id, digest)
        self.fixture.reseal()

    def replace_artifact_document(
        self,
        artifact_id: str,
        artifact: dict,
        *,
        recompute_content_digest: bool = True,
    ) -> None:
        if recompute_content_digest and "content" in artifact:
            artifact["content_sha256"] = hashlib.sha256(
                canonical_bytes(artifact["content"])
            ).hexdigest()
        self.replace_artifact_raw(artifact_id, canonical_bytes(artifact))

    def semantic_reference(self, kind: str, soul_index: int = 0) -> dict:
        soul_id = self.fixture.evidence["payload"]["devices"][soul_index]["soul_id"]
        return next(
            item
            for item in self.fixture.evidence["payload"]["semantic_artifacts"]
            if item["artifact_kind"] == kind and item["soul_id"] == soul_id
        )

    def semantic_observation(self, artifact_id: str) -> tuple[dict, dict]:
        artifact = self.artifact_document(artifact_id)
        observation = json.loads(base64.b64decode(artifact["content"]["observation_base64"]))
        return artifact, observation

    def replace_semantic_observation(
        self,
        artifact_id: str,
        artifact: dict,
        observation: dict,
    ) -> None:
        observation_bytes = canonical_bytes(observation)
        artifact["content"]["observation_base64"] = base64.b64encode(observation_bytes).decode("ascii")
        artifact["content"]["observation_sha256"] = hashlib.sha256(observation_bytes).hexdigest()
        self.replace_artifact_document(artifact_id, artifact)

    def mutate_semantic_exchange(
        self,
        kind: str,
        exchange_kind: str,
        mutate,
        soul_index: int = 0,
    ) -> None:
        reference = self.semantic_reference(kind, soul_index)
        artifact, observation = self.semantic_observation(reference["raw_artifact_id"])
        document = json.loads(base64.b64decode(observation[f"{exchange_kind}_base64"]))
        mutate(document)
        raw = canonical_bytes(document)
        observation[f"{exchange_kind}_base64"] = base64.b64encode(raw).decode("ascii")
        observation[f"{exchange_kind}_sha256"] = hashlib.sha256(raw).hexdigest()
        self.replace_semantic_observation(reference["raw_artifact_id"], artifact, observation)

    def refresh_f7_bom_chain(self) -> None:
        payload = self.fixture.evidence["payload"]
        semantic_ids = {item["raw_artifact_id"] for item in payload["semantic_artifacts"]}
        for group in ("projection_checks", "search_readback_checks", "semantic_artifacts"):
            for reference in payload[group]:
                artifact_id = reference["raw_artifact_id"]
                artifact = self.artifact_document(artifact_id)
                if artifact_id in semantic_ids:
                    observation = json.loads(
                        base64.b64decode(artifact["content"]["observation_base64"])
                    )
                    observation["release_bom_id"] = payload["release_bom_id"]
                    observation["release_bom_sha256"] = payload["release_bom_sha256"]
                    observation_bytes = canonical_bytes(observation)
                    artifact["content"]["observation_base64"] = base64.b64encode(
                        observation_bytes
                    ).decode("ascii")
                    artifact["content"]["observation_sha256"] = hashlib.sha256(
                        observation_bytes
                    ).hexdigest()
                else:
                    artifact["content"]["release_bom_id"] = payload["release_bom_id"]
                    artifact["content"]["release_bom_sha256"] = payload["release_bom_sha256"]
                self.replace_artifact_document(artifact_id, artifact)

    def _sign_attestation(self, private_key: Path) -> None:
        self.fixture.reseal()
        self.fixture.evidence["attestation"]["signature_base64"] = _openssl_sign_p1363(
            private_key,
            b"dps-external-runner-attestation/v1\n"
            + canonical_bytes(self.fixture.evidence["attestation"]["facts"]),
        )
        self.fixture.evidence_path.write_bytes(canonical_bytes(self.fixture.evidence))

    def sign_with_ephemeral_test_keys(self) -> tuple[Path, Path, Path]:
        openssl = shutil.which("openssl")
        if openssl is None:
            raise unittest.SkipTest("OpenSSL is required for the real-signature fixture")
        def generate(role: str, public_path: Path) -> Path:
            private_key = self.fixture.root / f"ephemeral-{role}-private.pem"
            subprocess.run(
                [openssl, "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(private_key)],
                check=True,
                capture_output=True,
                timeout=20,
            )
            subprocess.run(
                [openssl, "ec", "-in", str(private_key), "-pubout", "-out", str(public_path)],
                check=True,
                capture_output=True,
                timeout=20,
            )
            return private_key

        windows_private = generate("windows", self.fixture.key_path)
        device_private = generate("device", self.fixture.device_key_path)
        bom_private = generate("bom", self.fixture.bom_key_path)
        self.fixture.trust["trusted_issuers"][0]["public_key_sha256"] = hashlib.sha256(
            self.fixture.key_path.read_bytes()
        ).hexdigest()
        self.fixture.trust["trusted_issuers"][1]["public_key_sha256"] = hashlib.sha256(
            self.fixture.device_key_path.read_bytes()
        ).hexdigest()
        self.fixture.trust["trusted_bom_signers"][0]["public_key_sha256"] = hashlib.sha256(
            self.fixture.bom_key_path.read_bytes()
        ).hexdigest()

        unsigned_bom = copy.deepcopy(self.fixture.bom)
        unsigned_bom.pop("signature")
        self.fixture.bom["signature"]["value"] = _openssl_sign_p1363(
            bom_private,
            b"dps-release-bom/v1\n" + canonical_bytes(unsigned_bom),
        )
        self.fixture.bom_path.write_bytes(canonical_bytes(self.fixture.bom))
        self.fixture.evidence["release_bom"]["sha256"] = hashlib.sha256(
            self.fixture.bom_path.read_bytes()
        ).hexdigest()
        self.fixture.evidence["payload"]["release_bom_sha256"] = self.fixture.evidence[
            "release_bom"
        ]["sha256"]
        self.refresh_f7_bom_chain()
        self.fixture.write_trust()
        self.fixture.refresh_f7_prerequisite_receipt(windows_private)
        self._sign_attestation(device_private)
        return windows_private, device_private, bom_private

    @unittest.skipUnless(shutil.which("openssl"), "OpenSSL is required")
    def test_complete_f7_envelope_and_release_bom_verify_with_real_ecdsa(self) -> None:
        self.sign_with_ephemeral_test_keys()
        decision = run_gate(
            "f7",
            self.fixture.evidence_path,
            self.fixture.trust_path.resolve(),
            clock=lambda: self.fixture.finished + timedelta(minutes=2),
        )
        self.assertEqual(0, decision.exit_code)
        self.assertEqual(ELIGIBLE, decision.decision)
        self.assertFalse(decision.as_dict()["evidence_receipt_issued"])

    @unittest.skipUnless(shutil.which("openssl"), "OpenSSL is required")
    def test_complete_f7_envelope_rejects_tampered_real_bom_signature(self) -> None:
        windows_private, device_private, _bom_private = self.sign_with_ephemeral_test_keys()
        signature = self.fixture.bom["signature"]["value"]
        self.fixture.bom["signature"]["value"] = ("A" if signature[0] != "A" else "B") + signature[1:]
        self.fixture.bom_path.write_bytes(canonical_bytes(self.fixture.bom))
        self.fixture.evidence["release_bom"]["sha256"] = hashlib.sha256(
            self.fixture.bom_path.read_bytes()
        ).hexdigest()
        self.fixture.evidence["payload"]["release_bom_sha256"] = self.fixture.evidence[
            "release_bom"
        ]["sha256"]
        self.fixture.refresh_f7_prerequisite_receipt(windows_private)
        self._sign_attestation(device_private)
        decision = run_gate(
            "f7",
            self.fixture.evidence_path,
            self.fixture.trust_path.resolve(),
            clock=lambda: self.fixture.finished + timedelta(minutes=2),
        )
        self.assertEqual("invalid_signature", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    @unittest.skipUnless(shutil.which("openssl"), "OpenSSL is required")
    def test_complete_f7_envelope_rejects_tampered_real_prerequisite_signature(self) -> None:
        _windows_private, device_private, _bom_private = self.sign_with_ephemeral_test_keys()
        reference = self.fixture.evidence["payload"]["f6_prerequisite"]
        receipt = self.artifact_document(reference["raw_artifact_id"])
        value = receipt["signature"]["value"]
        receipt["signature"]["value"] = ("A" if value[0] != "A" else "B") + value[1:]
        self.replace_artifact_raw(reference["raw_artifact_id"], canonical_bytes(receipt))
        self._sign_attestation(device_private)
        decision = run_gate(
            "f7",
            self.fixture.evidence_path,
            self.fixture.trust_path.resolve(),
            clock=lambda: self.fixture.finished + timedelta(minutes=2),
        )
        self.assertEqual("invalid_signature", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_v3_synthetic_candidate_is_logically_eligible_but_issues_no_receipt(self) -> None:
        decision = self.decision()
        self.assertEqual(0, decision.exit_code)
        self.assertEqual(ELIGIBLE, decision.decision)
        self.assertEqual("DEVICE_VERIFIED", decision.target_verification_level)
        self.assertFalse(decision.as_dict()["evidence_receipt_issued"])

    def test_legacy_f7_v1_and_v2_are_historical_only(self) -> None:
        for version in ("v1", "v2"):
            with self.subTest(version=version):
                self.fixture.evidence["schema_version"] = f"dps.device-gbrain-verification-input/{version}"
                self.fixture.reseal()
                decision = self.decision()
                self.assertEqual("stage_schema_mismatch", decision.reason_code)
                self.assertNotEqual(0, decision.exit_code)

    def test_stale_candidate_gbrain_hashes_remain_drift_detectable_until_refreeze(self) -> None:
        root = Path(__file__).resolve().parents[3]
        projection_schema = root / "Modules/gbrain-projector/contracts/provided/gbrain.projection.v2.schema.json"
        binding_schema = root / "Modules/gbrain-projector/contracts/provided/gbrain.source-binding.v1.schema.json"
        projection_dto = root / "Modules/gbrain-projector/contracts/provided/Dps.GBrainProjector.Contracts/GBrainProjectionV2.cs"
        binding_dto = root / "Modules/gbrain-projector/contracts/provided/Dps.GBrainProjector.Contracts/GBrainSourceBindingV1.cs"
        stale = (
            external_gate_module.STALE_CANDIDATE_GBRAIN_PROJECTION_V2_SCHEMA_SHA256,
            external_gate_module.STALE_CANDIDATE_GBRAIN_SOURCE_BINDING_V1_SCHEMA_SHA256,
            external_gate_module.STALE_CANDIDATE_GBRAIN_PROJECTION_V2_DTO_SHA256,
            external_gate_module.STALE_CANDIDATE_GBRAIN_SOURCE_BINDING_V1_DTO_SHA256,
        )
        current = tuple(
            hashlib.sha256(path.read_bytes()).hexdigest()
            for path in (projection_schema, binding_schema, projection_dto, binding_dto)
        )
        self.assertNotEqual(stale, current)

    def test_projection_v1_is_quarantine_only_and_cannot_satisfy_f7(self) -> None:
        reference = self.fixture.evidence["payload"]["projection_checks"][0]
        artifact = self.artifact_document(reference["raw_artifact_id"])
        projection = json.loads(base64.b64decode(artifact["content"]["written_projection_base64"]))
        projection["schema_version"] = "1.0.0"
        projection["contract_id"] = "gbrain.projection/v1"
        for key in (
            "source_binding_algorithm",
            "source_binding_nonce",
            "source_binding_soul_hash",
            "source_binding_allocated_at",
            "source_binding_revision",
            "source_binding_checksum",
        ):
            projection.pop(key)
        raw_projection = canonical_bytes(projection)
        checksum = hashlib.sha256(raw_projection).hexdigest()
        artifact["content"]["written_projection_base64"] = base64.b64encode(raw_projection).decode("ascii")
        artifact["content"]["readback_projection_base64"] = base64.b64encode(raw_projection).decode("ascii")
        artifact["content"]["written_checksum"] = checksum
        artifact["content"]["read_checksum"] = checksum
        self.replace_artifact_document(reference["raw_artifact_id"], artifact)
        self.assertEqual("projection_contract_mismatch", self.decision().reason_code)

    def test_full_soul_nonce_source_mapping_rejects_legacy_prefix_collision(self) -> None:
        soul_a = "soul_" + "a" * 28 + "b" * 36
        soul_b = "soul_" + "a" * 28 + "c" * 36
        self.assertEqual("dps-" + "a" * 28, "dps-" + soul_a[5:33])
        self.assertEqual("dps-" + soul_a[5:33], "dps-" + soul_b[5:33])
        self.assertNotEqual(
            external_gate_module._gbrain_source_for_soul(soul_a, 0),
            external_gate_module._gbrain_source_for_soul(soul_b, 0),
        )

        mapping = self.fixture.evidence["payload"]["source_mappings"][0]
        mapping["logical_source_id"] = "dps-" + mapping["soul_id"][5:33]
        mapping["external_source_alias"] = external_gate_module._expected_external_source_alias(
            mapping["logical_source_id"]
        )
        self.fixture.reseal()
        self.assertEqual("source_mapping_mismatch", self.decision().reason_code)

    def test_source_binding_raw_bytes_nonce_revision_and_checksum_are_recomputed(self) -> None:
        reference = self.fixture.evidence["payload"]["projection_checks"][0]
        artifact = self.artifact_document(reference["raw_artifact_id"])
        binding = json.loads(base64.b64decode(artifact["content"]["source_binding_base64"]))
        binding["binding_checksum"] = "f" * 64
        raw_binding = canonical_bytes(binding)
        artifact["content"]["source_binding_base64"] = base64.b64encode(raw_binding).decode("ascii")
        artifact["content"]["source_binding_sha256"] = hashlib.sha256(raw_binding).hexdigest()
        self.replace_artifact_document(reference["raw_artifact_id"], artifact)
        self.assertEqual("source_binding_checksum_mismatch", self.decision().reason_code)

    def test_android_only_environment_is_rejected(self) -> None:
        self.fixture.evidence["environment"]["os_family"] = "Android"
        self.fixture.reseal()
        self.assertNotEqual(0, self.decision().exit_code)

    def test_missing_f6_prerequisite_is_rejected(self) -> None:
        del self.fixture.evidence["payload"]["f6_prerequisite"]
        self.fixture.reseal()
        self.assertNotEqual(0, self.decision().exit_code)

    def test_f6_prerequisite_wrong_bom_is_rejected(self) -> None:
        reference = self.fixture.evidence["payload"]["f6_prerequisite"]
        receipt = self.artifact_document(reference["raw_artifact_id"])
        receipt["release_bom_id"] = "bom-wrong-0001"
        self.replace_artifact_raw(reference["raw_artifact_id"], canonical_bytes(receipt))
        decision = self.decision()
        self.assertEqual("prerequisite_context_mismatch", decision.reason_code)

    def test_f6_prerequisite_expired_or_issued_after_f7_start_is_rejected(self) -> None:
        for field, value in (
            ("expires_at", iso(self.fixture.finished - timedelta(seconds=1))),
            ("issued_at", iso(self.fixture.started + timedelta(seconds=1))),
        ):
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f7()
                    reference = fixture.evidence["payload"]["f6_prerequisite"]
                    metadata = next(item for item in fixture.evidence["raw_artifacts"] if item["artifact_id"] == reference["raw_artifact_id"])
                    receipt = json.loads(Path(metadata["path"]).read_bytes())
                    receipt[field] = value
                    raw = canonical_bytes(receipt)
                    Path(metadata["path"]).write_bytes(raw)
                    digest = hashlib.sha256(raw).hexdigest()
                    metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
                    reference["raw_artifact_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f7",
                        fixture.evidence_path,
                        fixture.trust_path.resolve(),
                        accept_signature,
                        clock=lambda: fixture.finished + timedelta(minutes=2),
                    )
                    self.assertEqual("stale_prerequisite_receipt", decision.reason_code)

    def test_internally_consistent_old_signed_f6_and_f7_bundle_cannot_be_replayed(self) -> None:
        decision = run_gate(
            "f7",
            self.fixture.evidence_path,
            self.fixture.trust_path.resolve(),
            accept_signature,
            clock=lambda: self.fixture.finished + timedelta(days=8),
        )
        self.assertEqual("stale_f7_evidence", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_revoked_f6_prerequisite_is_rejected_under_current_trust_policy(self) -> None:
        receipt_id = self.fixture.evidence["payload"]["f6_prerequisite"]["receipt_id"]
        self.fixture.trust["prerequisite_receipt_policy"]["revoked_receipt_ids"] = [receipt_id]
        self.fixture.write_trust()
        self.fixture.refresh_f7_prerequisite_receipt()
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("revoked_prerequisite_receipt", decision.reason_code)

    def test_f6_prerequisite_exact_source_evidence_environment_window_and_instances_are_anchored(self) -> None:
        cases = (
            ("evidence_id", "external-f6-evidence-wrong", "prerequisite_source_evidence_mismatch"),
            ("source_evidence_sha256", "f" * 64, "prerequisite_source_evidence_mismatch"),
            ("source_environment_sha256", "e" * 64, "prerequisite_source_evidence_mismatch"),
            ("source_measurement_started_at", "2026-06-30T23:59:59Z", "prerequisite_source_evidence_mismatch"),
            ("edge_installation_id", "edge_wrong_installation_01", "prerequisite_environment_mismatch"),
            ("zenno_installation_id", "zenno_wrong_installation_01", "prerequisite_environment_mismatch"),
        )
        for field, value, expected in cases:
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f7()
                    reference = fixture.evidence["payload"]["f6_prerequisite"]
                    metadata = next(
                        item
                        for item in fixture.evidence["raw_artifacts"]
                        if item["artifact_id"] == reference["raw_artifact_id"]
                    )
                    receipt = json.loads(Path(metadata["path"]).read_bytes())
                    receipt[field] = value
                    raw = canonical_bytes(receipt)
                    Path(metadata["path"]).write_bytes(raw)
                    digest = hashlib.sha256(raw).hexdigest()
                    metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
                    reference["raw_artifact_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f7",
                        fixture.evidence_path,
                        fixture.trust_path.resolve(),
                        accept_signature,
                        clock=lambda: fixture.finished + timedelta(minutes=2),
                    )
                    self.assertEqual(expected, decision.reason_code)

    def test_prerequisite_context_level_status_kind_and_issuer_attacks_fail_closed(self) -> None:
        cases = (
            ("repository_id", "repo:wrong", "prerequisite_context_mismatch"),
            ("baseline_commit", "f" * 40, "prerequisite_context_mismatch"),
            ("candidate_artifact_sha256", "f" * 64, "prerequisite_context_mismatch"),
            ("trust_policy_id", "trust-policy-wrong-0001", "prerequisite_context_mismatch"),
            ("verification_level", "DEVICE_VERIFIED", "prerequisite_level_mismatch"),
            ("status", "FAIL", "prerequisite_level_mismatch"),
            ("evidence_kind", "SIMULATED", "non_real_prerequisite"),
            ("evidence_issuer_identity", "external-unknown-issuer", "trust_policy_mismatch"),
        )
        for field, value, expected in cases:
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f7()
                    reference = fixture.evidence["payload"]["f6_prerequisite"]
                    metadata = next(
                        item
                        for item in fixture.evidence["raw_artifacts"]
                        if item["artifact_id"] == reference["raw_artifact_id"]
                    )
                    receipt = json.loads(Path(metadata["path"]).read_bytes())
                    receipt[field] = value
                    raw = canonical_bytes(receipt)
                    Path(metadata["path"]).write_bytes(raw)
                    digest = hashlib.sha256(raw).hexdigest()
                    metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
                    reference["raw_artifact_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f7",
                        fixture.evidence_path,
                        fixture.trust_path.resolve(),
                        accept_signature,
                        clock=lambda: fixture.finished + timedelta(minutes=2),
                    )
                    self.assertEqual(expected, decision.reason_code)

    def test_runner_binary_sbom_producer_and_environment_are_exactly_bound(self) -> None:
        for field, value, expected in (
            ("sha256", "f" * 64, "f7_runner_bom_mismatch"),
            ("version", "1.0.1", "f7_runner_bom_mismatch"),
            ("sbom_sha256", "f" * 64, "f7_runner_sbom_bom_mismatch"),
        ):
            with self.subTest(case=f"runner-{field}-bom"):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f7()
                    fixture.bom["modules"][1][field] = value
                    fixture.bom_path.write_bytes(canonical_bytes(fixture.bom))
                    fixture.evidence["release_bom"]["sha256"] = hashlib.sha256(
                        fixture.bom_path.read_bytes()
                    ).hexdigest()
                    fixture.evidence["payload"]["release_bom_sha256"] = fixture.evidence[
                        "release_bom"
                    ]["sha256"]
                    helper = F7ExternalGateTests("runTest")
                    helper.fixture = fixture
                    helper.refresh_f7_bom_chain()
                    fixture.refresh_f7_prerequisite_receipt()
                    fixture.reseal()
                    decision = run_gate(
                        "f7",
                        fixture.evidence_path,
                        fixture.trust_path.resolve(),
                        accept_signature,
                        clock=lambda: fixture.finished + timedelta(minutes=2),
                    )
                    self.assertEqual(expected, decision.reason_code)
        for key, expected in (
            ("producer", "f7_artifact_producer_mismatch"),
            ("environment", "f7_artifact_environment_mismatch"),
        ):
            with self.subTest(case=key):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f7()
                    reference = fixture.evidence["payload"]["projection_checks"][0]
                    metadata = next(
                        item for item in fixture.evidence["raw_artifacts"]
                        if item["artifact_id"] == reference["raw_artifact_id"]
                    )
                    artifact = json.loads(Path(metadata["path"]).read_bytes())
                    if key == "producer":
                        artifact["producer"]["version"] = "1.0.1"
                    else:
                        artifact["environment"]["environment_id"] = "env_device_gbrain_lab_02"
                    raw = canonical_bytes(artifact)
                    Path(metadata["path"]).write_bytes(raw)
                    digest = hashlib.sha256(raw).hexdigest()
                    metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
                    reference["raw_artifact_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f7",
                        fixture.evidence_path,
                        fixture.trust_path.resolve(),
                        accept_signature,
                        clock=lambda: fixture.finished + timedelta(minutes=2),
                    )
                    self.assertEqual(expected, decision.reason_code)

    def test_f6_f7_and_bom_cryptographic_roles_must_use_distinct_keys(self) -> None:
        device_issuer = self.fixture.trust["trusted_issuers"][1]
        bom_signer = self.fixture.trust["trusted_bom_signers"][0]
        bom_signer["public_key_pem_path"] = device_issuer["public_key_pem_path"]
        bom_signer["public_key_sha256"] = device_issuer["public_key_sha256"]
        self.fixture.write_trust()
        self.fixture.refresh_f7_prerequisite_receipt()
        self.fixture.reseal()
        self.assertEqual("cryptographic_role_separation_failed", self.decision().reason_code)

    def test_four_projection_search_artifacts_cannot_replace_required_semantic_evidence(self) -> None:
        semantic_ids = {item["raw_artifact_id"] for item in self.fixture.evidence["payload"]["semantic_artifacts"]}
        self.fixture.evidence["payload"]["semantic_artifacts"] = []
        self.fixture.evidence["raw_artifacts"] = [
            item for item in self.fixture.evidence["raw_artifacts"] if item["artifact_id"] not in semantic_ids
        ]
        self.fixture.reseal()
        self.assertNotEqual(0, self.decision().exit_code)

    def test_raw_artifact_content_digest_is_recomputed(self) -> None:
        reference = self.semantic_reference("PERSONA_EXACT_CURRENT_READBACK")
        artifact = self.artifact_document(reference["raw_artifact_id"])
        artifact["content"]["read_checksum"] = "f" * 64
        self.replace_artifact_document(
            reference["raw_artifact_id"], artifact, recompute_content_digest=False
        )
        decision = self.decision()
        self.assertEqual("f7_artifact_content_digest_mismatch", decision.reason_code)

    def test_noncanonical_artifacts_are_rejected(self) -> None:
        reference = self.semantic_reference("PERSONA_EXACT_CURRENT_READBACK")
        artifact_id = reference["raw_artifact_id"]
        raw = Path(self.artifact_metadata(artifact_id)["path"]).read_bytes()
        self.replace_artifact_raw(artifact_id, raw + b"\n")
        self.assertEqual("noncanonical_f7_artifact", self.decision().reason_code)

    def test_duplicate_json_members_in_raw_artifacts_are_rejected(self) -> None:
        reference = self.semantic_reference("PERSONA_EXACT_CURRENT_READBACK")
        artifact_id = reference["raw_artifact_id"]
        raw = Path(self.artifact_metadata(artifact_id)["path"]).read_bytes()
        duplicate = raw[:-1] + b',"artifact_id":"' + artifact_id.encode("ascii") + b'"}'
        self.replace_artifact_raw(artifact_id, duplicate)
        self.assertEqual("duplicate_json_key", self.decision().reason_code)

    def test_artifact_exchange_and_duplicate_binding_are_rejected(self) -> None:
        first = self.semantic_reference("PERSONA_EXACT_CURRENT_READBACK", 0)
        second = self.semantic_reference("PERSONA_EXACT_CURRENT_READBACK", 1)
        second_artifact_id = second["raw_artifact_id"]
        second["raw_artifact_id"] = first["raw_artifact_id"]
        second["raw_artifact_sha256"] = first["raw_artifact_sha256"]
        removed = self.artifact_metadata(second_artifact_id)
        self.fixture.evidence["raw_artifacts"].remove(removed)
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("duplicate_f7_artifact", decision.reason_code)

    def test_raw_artifact_exchange_is_rejected(self) -> None:
        first = self.semantic_reference("DATA_SUBJECT_EXPORT", 0)
        second = self.semantic_reference("DATA_SUBJECT_EXPORT", 1)
        first_metadata = self.artifact_metadata(first["raw_artifact_id"])
        second_metadata = self.artifact_metadata(second["raw_artifact_id"])
        first_raw = Path(first_metadata["path"]).read_bytes()
        second_raw = Path(second_metadata["path"]).read_bytes()
        Path(first_metadata["path"]).write_bytes(second_raw)
        Path(second_metadata["path"]).write_bytes(first_raw)
        for metadata, reference, raw in (
            (first_metadata, first, second_raw),
            (second_metadata, second, first_raw),
        ):
            digest = hashlib.sha256(raw).hexdigest()
            metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
            reference["raw_artifact_sha256"] = digest
        self.fixture.reseal()
        self.assertEqual("f7_artifact_binding_mismatch", self.decision().reason_code)

    def test_cross_soul_scope_and_extra_raw_artifact_are_rejected(self) -> None:
        reference = self.semantic_reference("DATA_SUBJECT_EXPORT", 0)
        artifact = self.artifact_document(reference["raw_artifact_id"])
        artifact["scope"] = _f7_scope(
            self.fixture.evidence["payload"],
            self.fixture.evidence["payload"]["devices"][1]["soul_id"],
        )
        self.replace_artifact_document(reference["raw_artifact_id"], artifact)
        self.assertEqual("f7_artifact_scope_mismatch", self.decision().reason_code)

    def test_unreferenced_raw_artifact_is_rejected(self) -> None:
        path = self.fixture.root / "f7-unreferenced-artifact-0001.json"
        raw = canonical_bytes({"redacted": "unreferenced"})
        path.write_bytes(raw)
        self.fixture.evidence["raw_artifacts"].append(
            {
                "artifact_id": "f7-unreferenced-artifact-0001",
                "path": str(path.resolve()),
                "sha256": hashlib.sha256(raw).hexdigest(),
                "size_bytes": len(raw),
                "media_type": "application/json",
            }
        )
        self.fixture.reseal()
        self.assertEqual("f7_artifact_set_mismatch", self.decision().reason_code)

    def test_persona_oauth_and_delete_rebuild_semantics_are_recomputed(self) -> None:
        cases = (
            ("PERSONA_EXACT_CURRENT_READBACK", "read_revision", "f" * 64, "persona_exact_readback_mismatch"),
            ("SOUL_DEVICE_SOURCE_OAUTH_BINDING", "oauth_write_source_alias", self.fixture.evidence["payload"]["source_mappings"][1]["external_source_alias"], "source_oauth_binding_mismatch"),
            ("DELETE_REBUILD_PURGE", "embedding_count_after_delete", 1, "zero_tolerance_breach"),
        )
        for kind, field, value, expected in cases:
            with self.subTest(kind=kind, field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f7()
                    soul_id = fixture.evidence["payload"]["devices"][0]["soul_id"]
                    reference = next(item for item in fixture.evidence["payload"]["semantic_artifacts"] if item["artifact_kind"] == kind and item["soul_id"] == soul_id)
                    metadata = next(item for item in fixture.evidence["raw_artifacts"] if item["artifact_id"] == reference["raw_artifact_id"])
                    artifact = json.loads(Path(metadata["path"]).read_bytes())
                    observation = json.loads(base64.b64decode(artifact["content"]["observation_base64"]))
                    observation[field] = value
                    observation_bytes = canonical_bytes(observation)
                    artifact["content"]["observation_base64"] = base64.b64encode(observation_bytes).decode("ascii")
                    artifact["content"]["observation_sha256"] = hashlib.sha256(observation_bytes).hexdigest()
                    artifact["content_sha256"] = hashlib.sha256(canonical_bytes(artifact["content"])).hexdigest()
                    raw = canonical_bytes(artifact)
                    Path(metadata["path"]).write_bytes(raw)
                    digest = hashlib.sha256(raw).hexdigest()
                    metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
                    reference["raw_artifact_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f7",
                        fixture.evidence_path,
                        fixture.trust_path.resolve(),
                        accept_signature,
                        clock=lambda: fixture.finished + timedelta(minutes=2),
                    )
                    self.assertEqual(expected, decision.reason_code)

    def test_fixture_cross_scope_duplicate_and_unknown_outcome_attacks_fail_closed(self) -> None:
        cases = (
            ("FIXTURE_COMMAND_POSTCONDITION", "native_result", "UNKNOWN_OUTCOME", "fixture_postcondition_mismatch"),
            ("CROSS_SOUL_ATTACK", "returned_record_count", 1, "zero_tolerance_breach"),
            ("CROSS_DEVICE_ATTACK", "native_execution_count", 1, "zero_tolerance_breach"),
            ("CROSS_ACCOUNT_ATTACK", "side_effect_count", 1, "zero_tolerance_breach"),
            ("DUPLICATE_DELIVERY", "native_execution_count", 2, "duplicate_delivery_side_effect"),
            ("UNKNOWN_OUTCOME_RECONCILIATION", "automatic_retry_count", 1, "unknown_outcome_unsafe_retry"),
        )
        for kind, field, value, expected in cases:
            with self.subTest(kind=kind, field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f7()
                    soul_id = fixture.evidence["payload"]["devices"][0]["soul_id"]
                    reference = next(item for item in fixture.evidence["payload"]["semantic_artifacts"] if item["artifact_kind"] == kind and item["soul_id"] == soul_id)
                    metadata = next(item for item in fixture.evidence["raw_artifacts"] if item["artifact_id"] == reference["raw_artifact_id"])
                    artifact = json.loads(Path(metadata["path"]).read_bytes())
                    observation = json.loads(base64.b64decode(artifact["content"]["observation_base64"]))
                    observation[field] = value
                    observation_bytes = canonical_bytes(observation)
                    artifact["content"]["observation_base64"] = base64.b64encode(observation_bytes).decode("ascii")
                    artifact["content"]["observation_sha256"] = hashlib.sha256(observation_bytes).hexdigest()
                    artifact["content_sha256"] = hashlib.sha256(canonical_bytes(artifact["content"])).hexdigest()
                    raw = canonical_bytes(artifact)
                    Path(metadata["path"]).write_bytes(raw)
                    digest = hashlib.sha256(raw).hexdigest()
                    metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
                    reference["raw_artifact_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f7",
                        fixture.evidence_path,
                        fixture.trust_path.resolve(),
                        accept_signature,
                        clock=lambda: fixture.finished + timedelta(minutes=2),
                    )
                    self.assertEqual(expected, decision.reason_code)

    def test_semantic_raw_request_response_and_postcondition_bytes_are_independently_bound(self) -> None:
        reference = self.semantic_reference("FIXTURE_COMMAND_POSTCONDITION")
        artifact, observation = self.semantic_observation(reference["raw_artifact_id"])
        request = json.loads(base64.b64decode(observation["request_base64"]))
        request["payload"]["command_id"] = "f7-tampered-command-0001"
        request_bytes = canonical_bytes(request)
        observation["request_base64"] = base64.b64encode(request_bytes).decode("ascii")
        observation["request_sha256"] = hashlib.sha256(request_bytes).hexdigest()
        self.replace_semantic_observation(reference["raw_artifact_id"], artifact, observation)
        self.assertEqual("fixture_postcondition_mismatch", self.decision().reason_code)

    def test_semantic_exchange_outcomes_must_match_the_evidence_kind(self) -> None:
        self.mutate_semantic_exchange(
            "CROSS_SOUL_ATTACK",
            "response",
            lambda document: document.__setitem__("outcome", "OBSERVED"),
        )
        self.assertEqual("f7_exchange_outcome_mismatch", self.decision().reason_code)

    def test_oauth_whoami_must_bind_the_native_gbrain_source_id(self) -> None:
        other_source_id = self.fixture.evidence["payload"]["source_mappings"][1]["logical_source_id"]
        self.mutate_semantic_exchange(
            "SOUL_DEVICE_SOURCE_OAUTH_BINDING",
            "response",
            lambda document: document["payload"]["oauth_whoami"].__setitem__(
                "source_id", other_source_id
            ),
        )
        self.assertEqual("source_oauth_binding_mismatch", self.decision().reason_code)

    def test_fixture_native_receipt_must_bind_command_and_scope(self) -> None:
        self.mutate_semantic_exchange(
            "FIXTURE_COMMAND_POSTCONDITION",
            "response",
            lambda document: document["payload"]["native_receipts"][0].__setitem__(
                "command_id", "f7-substituted-command-0001"
            ),
        )
        self.assertEqual("fixture_postcondition_mismatch", self.decision().reason_code)

    def test_cross_scope_denial_audit_must_bind_actor_and_target(self) -> None:
        self.mutate_semantic_exchange(
            "CROSS_DEVICE_ATTACK",
            "response",
            lambda document: document["payload"]["audit_events"][0].__setitem__(
                "actor_scope_sha256", "f" * 64
            ),
        )
        self.assertEqual("cross_scope_attack_audit_mismatch", self.decision().reason_code)

    def test_duplicate_delivery_records_must_bind_idempotency_and_scope(self) -> None:
        self.mutate_semantic_exchange(
            "DUPLICATE_DELIVERY",
            "request",
            lambda document: document["payload"]["deliveries"][1].__setitem__(
                "idempotency_key", "idem-substituted-0001"
            ),
        )
        self.assertEqual("duplicate_delivery_record_mismatch", self.decision().reason_code)

    def test_unknown_outcome_exact_reads_must_bind_command_and_scope(self) -> None:
        self.mutate_semantic_exchange(
            "UNKNOWN_OUTCOME_RECONCILIATION",
            "postcondition",
            lambda document: document["payload"]["reads"][0].__setitem__(
                "scope_sha256", "f" * 64
            ),
        )
        self.assertEqual("unknown_outcome_readback_mismatch", self.decision().reason_code)

    def test_cross_scope_attacks_are_bidirectional_and_mutate_exactly_one_axis(self) -> None:
        payload = self.fixture.evidence["payload"]
        scope_keys = {
            "CROSS_SOUL_ATTACK": "soul_id",
            "CROSS_DEVICE_ATTACK": "device_binding_id",
            "CROSS_ACCOUNT_ATTACK": "platform_account_id",
        }
        for kind, changed_key in scope_keys.items():
            references = [item for item in payload["semantic_artifacts"] if item["artifact_kind"] == kind]
            self.assertEqual(2, len(references))
            self.assertEqual({item["soul_id"] for item in references}, {item["soul_id"] for item in payload["devices"]})
            for reference in references:
                artifact, observation = self.semantic_observation(reference["raw_artifact_id"])
                actor_scope = _f7_scope(payload, reference["soul_id"])
                target_scope = observation["target_scope"]
                self.assertEqual([changed_key], [key for key in actor_scope if actor_scope[key] != target_scope[key]])

        reference = self.semantic_reference("CROSS_SOUL_ATTACK")
        artifact, observation = self.semantic_observation(reference["raw_artifact_id"])
        other_scope = _f7_scope(payload, payload["devices"][1]["soul_id"])
        observation["target_scope"]["device_binding_id"] = other_scope["device_binding_id"]
        request = json.loads(base64.b64decode(observation["request_base64"]))
        request["payload"]["target_scope"] = observation["target_scope"]
        request_bytes = canonical_bytes(request)
        observation["request_base64"] = base64.b64encode(request_bytes).decode("ascii")
        observation["request_sha256"] = hashlib.sha256(request_bytes).hexdigest()
        self.replace_semantic_observation(reference["raw_artifact_id"], artifact, observation)
        self.assertEqual("cross_scope_attack_target_mismatch", self.decision().reason_code)

    def test_two_soul_device_oauth_attestations_and_cross_credentials_are_unique_and_bound(self) -> None:
        source_references = [
            item
            for item in self.fixture.evidence["payload"]["semantic_artifacts"]
            if item["artifact_kind"] == "SOUL_DEVICE_SOURCE_OAUTH_BINDING"
        ]
        source_observations = [self.semantic_observation(item["raw_artifact_id"])[1] for item in source_references]
        for field in (
            "adb_serial_hmac_sha256",
            "device_attestation_sha256",
            "oauth_client_id_sha256",
            "oauth_credential_lease_id",
            "oauth_token_fingerprint_sha256",
        ):
            self.assertEqual(2, len({item[field] for item in source_observations}))

        reference = self.semantic_reference("CROSS_ACCOUNT_ATTACK")
        artifact, observation = self.semantic_observation(reference["raw_artifact_id"])
        request = json.loads(base64.b64decode(observation["request_base64"]))
        request["payload"]["oauth_token_fingerprint_sha256"] = "f" * 64
        request_bytes = canonical_bytes(request)
        observation["request_base64"] = base64.b64encode(request_bytes).decode("ascii")
        observation["request_sha256"] = hashlib.sha256(request_bytes).hexdigest()
        self.replace_semantic_observation(reference["raw_artifact_id"], artifact, observation)
        self.assertEqual("f7_cross_credential_binding_mismatch", self.decision().reason_code)

    def test_run_trace_bom_phase_and_lifecycle_causal_chain_fail_closed(self) -> None:
        reference = self.semantic_reference("PERSONA_EXACT_CURRENT_READBACK")
        artifact, observation = self.semantic_observation(reference["raw_artifact_id"])
        observation["trace_id"] = "trace_" + "f" * 32
        for exchange_kind in ("request", "response", "postcondition"):
            document = json.loads(base64.b64decode(observation[f"{exchange_kind}_base64"]))
            document["trace_id"] = observation["trace_id"]
            raw = canonical_bytes(document)
            observation[f"{exchange_kind}_base64"] = base64.b64encode(raw).decode("ascii")
            observation[f"{exchange_kind}_sha256"] = hashlib.sha256(raw).hexdigest()
        self.replace_semantic_observation(reference["raw_artifact_id"], artifact, observation)
        self.assertEqual("f7_observation_chain_mismatch", self.decision().reason_code)

        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(Path(directory))
            fixture.use_f7()
            fixture.evidence["payload"]["operation_timeline"][2]["started_at"] = fixture.evidence[
                "payload"
            ]["operation_timeline"][1]["started_at"]
            fixture.reseal()
            decision = run_gate(
                "f7",
                fixture.evidence_path,
                fixture.trust_path.resolve(),
                accept_signature,
                clock=lambda: fixture.finished + timedelta(minutes=2),
            )
            self.assertEqual("operation_timeline_invalid", decision.reason_code)

        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(Path(directory))
            fixture.use_f7()
            soul_id = fixture.evidence["payload"]["devices"][0]["soul_id"]
            reference = next(
                item for item in fixture.evidence["payload"]["semantic_artifacts"]
                if item["artifact_kind"] == "DELETE_REBUILD_PURGE" and item["soul_id"] == soul_id
            )
            metadata = next(
                item for item in fixture.evidence["raw_artifacts"]
                if item["artifact_id"] == reference["raw_artifact_id"]
            )
            artifact = json.loads(Path(metadata["path"]).read_bytes())
            observation = json.loads(base64.b64decode(artifact["content"]["observation_base64"]))
            observation["pre_delete_projection_checksum"] = "f" * 64
            request = json.loads(base64.b64decode(observation["request_base64"]))
            request["payload"]["pre_delete_projection_checksum"] = "f" * 64
            request_raw = canonical_bytes(request)
            observation["request_base64"] = base64.b64encode(request_raw).decode("ascii")
            observation["request_sha256"] = hashlib.sha256(request_raw).hexdigest()
            observation_raw = canonical_bytes(observation)
            artifact["content"]["observation_base64"] = base64.b64encode(observation_raw).decode("ascii")
            artifact["content"]["observation_sha256"] = hashlib.sha256(observation_raw).hexdigest()
            artifact["content_sha256"] = hashlib.sha256(canonical_bytes(artifact["content"])).hexdigest()
            raw = canonical_bytes(artifact)
            Path(metadata["path"]).write_bytes(raw)
            digest = hashlib.sha256(raw).hexdigest()
            metadata["sha256"], metadata["size_bytes"] = digest, len(raw)
            reference["raw_artifact_sha256"] = digest
            fixture.reseal()
            decision = run_gate(
                "f7",
                fixture.evidence_path,
                fixture.trust_path.resolve(),
                accept_signature,
                clock=lambda: fixture.finished + timedelta(minutes=2),
            )
            self.assertEqual("f7_lifecycle_projection_chain_mismatch", decision.reason_code)

    def test_search_response_cannot_return_other_soul_or_stale_projection(self) -> None:
        reference = self.fixture.evidence["payload"]["search_readback_checks"][0]
        artifact = self.artifact_document(reference["raw_artifact_id"])
        response = json.loads(base64.b64decode(artifact["content"]["response_base64"]))
        response["results"][0]["soul_id"] = self.fixture.evidence["payload"]["devices"][1]["soul_id"]
        response_bytes = canonical_bytes(response)
        artifact["content"]["response_base64"] = base64.b64encode(response_bytes).decode("ascii")
        artifact["content"]["response_sha256"] = hashlib.sha256(response_bytes).hexdigest()
        self.replace_artifact_document(reference["raw_artifact_id"], artifact)
        self.assertEqual("search_response_scope_mismatch", self.decision().reason_code)


class F9ExternalGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.fixture = Fixture(Path(self.temporary.name))
        self.fixture.use_f9()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def decision(self, verifier=accept_signature):
        return run_gate("f9", self.fixture.evidence_path, self.fixture.trust_path.resolve(), verifier)

    def raw_metadata(self, artifact_id: str) -> dict:
        return next(item for item in self.fixture.evidence["raw_artifacts"] if item["artifact_id"] == artifact_id)

    def replace_receipt(self, receipt: dict) -> None:
        binding = self.fixture.evidence["payload"]["canary_prerequisite"]
        metadata = self.raw_metadata(binding["raw_artifact_id"])
        raw = canonical_bytes(receipt)
        Path(metadata["path"]).write_bytes(raw)
        digest = hashlib.sha256(raw).hexdigest()
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        binding["raw_artifact_sha256"] = digest
        self.fixture.reseal()

    def replace_load_artifact(self, name: str, artifact: dict) -> None:
        run = self.fixture.evidence["payload"]["load_runs"][name]
        metadata = self.raw_metadata(run["artifact_id"])
        raw = canonical_bytes(artifact)
        digest = hashlib.sha256(raw).hexdigest()
        Path(metadata["path"]).write_bytes(raw)
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        run["artifact_sha256"] = digest
        self.fixture.reseal()

    def load_artifact(self, name: str) -> dict:
        run = self.fixture.evidence["payload"]["load_runs"][name]
        return json.loads(Path(self.raw_metadata(run["artifact_id"])["path"]).read_bytes())

    def rewrite_bom_and_receipt(self) -> None:
        self.fixture.bom_path.write_bytes(canonical_bytes(self.fixture.bom))
        bom_digest = hashlib.sha256(self.fixture.bom_path.read_bytes()).hexdigest()
        self.fixture.evidence["release_bom"]["sha256"] = bom_digest
        receipt_binding = self.fixture.evidence["payload"]["canary_prerequisite"]
        receipt_metadata = self.raw_metadata(receipt_binding["raw_artifact_id"])
        receipt = json.loads(Path(receipt_metadata["path"]).read_bytes())
        receipt["release_bom_sha256"] = bom_digest
        receipt_bytes = canonical_bytes(receipt)
        receipt_digest = hashlib.sha256(receipt_bytes).hexdigest()
        Path(receipt_metadata["path"]).write_bytes(receipt_bytes)
        receipt_metadata["sha256"] = receipt_digest
        receipt_metadata["size_bytes"] = len(receipt_bytes)
        receipt_binding["raw_artifact_sha256"] = receipt_digest
        self.fixture.reseal()

    def refresh_dependency_bom_and_receipt(self, dependency_artifact: dict) -> None:
        dependency_binding = self.fixture.evidence["payload"]["module_rollout_lines"]
        dependency_metadata = self.raw_metadata(dependency_binding["dependency_graph_artifact_id"])
        dependency_bytes = canonical_bytes(dependency_artifact)
        dependency_digest = hashlib.sha256(dependency_bytes).hexdigest()
        Path(dependency_metadata["path"]).write_bytes(dependency_bytes)
        dependency_metadata["sha256"] = dependency_digest
        dependency_metadata["size_bytes"] = len(dependency_bytes)
        dependency_binding["dependency_graph_sha256"] = dependency_digest
        self.fixture.bom["dependency_dag_sha256"] = dependency_digest
        self.rewrite_bom_and_receipt()

    def refresh_compatibility_bom_and_receipt(self, compatibility_artifact: dict) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        metadata = self.raw_metadata(rollout["compatibility_matrix_artifact_id"])
        raw = canonical_bytes(compatibility_artifact)
        digest = hashlib.sha256(raw).hexdigest()
        Path(metadata["path"]).write_bytes(raw)
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        rollout["compatibility_matrix_sha256"] = digest
        self.fixture.bom["compatibility_matrix_sha256"] = digest
        self.rewrite_bom_and_receipt()

    def execution_artifact(self) -> dict:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        metadata = self.raw_metadata(rollout["compatibility_execution_artifact_id"])
        return json.loads(Path(metadata["path"]).read_bytes())

    def replace_execution_artifact(self, artifact: dict) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        metadata = self.raw_metadata(rollout["compatibility_execution_artifact_id"])
        raw = canonical_bytes(artifact)
        digest = hashlib.sha256(raw).hexdigest()
        Path(metadata["path"]).write_bytes(raw)
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        rollout["compatibility_execution_sha256"] = digest
        self.fixture.reseal()

    def replace_combination_observation(self, row_index: int, combination_index: int, observation: dict) -> None:
        artifact = self.execution_artifact()
        combination = artifact["row_results"][row_index]["combination_results"][combination_index]
        metadata = self.raw_metadata(combination["raw_evidence_artifact_id"])
        raw = canonical_bytes(observation)
        digest = hashlib.sha256(raw).hexdigest()
        Path(metadata["path"]).write_bytes(raw)
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        combination["raw_evidence_sha256"] = digest
        self.replace_execution_artifact(artifact)

    def replace_manifest(self, binding: dict, manifest: dict) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        metadata = self.raw_metadata(binding["raw_artifact_id"])
        raw = canonical_bytes(manifest)
        digest = hashlib.sha256(raw).hexdigest()
        Path(metadata["path"]).write_bytes(raw)
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        binding["manifest_sha256"] = digest
        module = next(
            item
            for item in self.fixture.bom["modules"]
            if item["module_id"] == binding["module_id"]
        )
        module["manifest_sha256"] = digest
        self.rewrite_bom_and_receipt()

    def replace_first_manifest(self, manifest: dict) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        self.replace_manifest(rollout["manifest_artifacts"][0], manifest)

    def add_dependent_module(self) -> dict:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        existing_manifests = [
            json.loads(
                Path(self.raw_metadata(binding["raw_artifact_id"])["path"]).read_bytes()
            )
            for binding in rollout["manifest_artifacts"]
        ]
        second_manifest = synthetic_module_manifest(
            "command-orchestrator",
            [("windows-edge-worker", "verified edge dependency")],
        )
        second_bytes = canonical_bytes(second_manifest)
        second_digest = hashlib.sha256(second_bytes).hexdigest()
        second_path = self.fixture.root / "f9-command-orchestrator-module.json"
        second_path.write_bytes(second_bytes)
        second_artifact_id = "f9-module-manifest-command-0001"
        self.fixture.evidence["raw_artifacts"].append(
            {
                "artifact_id": second_artifact_id,
                "path": str(second_path.resolve()),
                "sha256": second_digest,
                "size_bytes": len(second_bytes),
                "media_type": "application/json",
            }
        )
        rollout["manifest_artifacts"].append(
            {
                "module_id": "command-orchestrator",
                "raw_artifact_id": second_artifact_id,
                "manifest_sha256": second_digest,
            }
        )
        self.fixture.bom["modules"].append(
            {
                "module_id": "command-orchestrator",
                "version": "1.0.0",
                "sha256": "d" * 64,
                "manifest_sha256": second_digest,
            }
        )
        return dependency_artifact_from_manifests(existing_manifests + [second_manifest])

    def convert_to_compatibility_group(self) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        manifests = []
        for binding in rollout["manifest_artifacts"]:
            manifest = json.loads(
                Path(self.raw_metadata(binding["raw_artifact_id"])["path"]).read_bytes()
            )
            side = "provided" if binding["module_id"] == "windows-edge-worker" else "consumed"
            v1 = manifest["contracts"][side][0]
            v1["status"] = "deprecated"
            v1["mode"] = "quarantine-only"
            v2 = dict(v1)
            v2["major"] = 2
            v2["source"] = v1["source"].replace(".v1.schema.json", ".v2.schema.json")
            v2["status"] = "proposed"
            v2["mode"] = "active"
            manifest["contracts"][side].append(v2)
            edge_side = "outbound" if side == "provided" else "inbound"
            manifest["communication"][edge_side][0]["major"] = 2
            self.replace_manifest(binding, manifest)
            manifests.append(manifest)

        v1_schema_binding = rollout["contract_schema_artifacts"][0]
        v1_schema_metadata = self.raw_metadata(v1_schema_binding["raw_artifact_id"])
        schema_bytes = Path(v1_schema_metadata["path"]).read_bytes()
        schema_digest = hashlib.sha256(schema_bytes).hexdigest()
        v2_schema_artifact_id = "f9-contract-schema-probe-v2-0001"
        v2_schema_path = self.fixture.root / "f9-contract-schema-probe-v2.json"
        v2_schema_path.write_bytes(schema_bytes)
        self.fixture.evidence["raw_artifacts"].append(
            {
                "artifact_id": v2_schema_artifact_id,
                "path": str(v2_schema_path.resolve()),
                "sha256": schema_digest,
                "size_bytes": len(schema_bytes),
                "media_type": "application/json",
            }
        )
        rollout["contract_schema_artifacts"].append(
            {
                "contract_id": v1_schema_binding["contract_id"],
                "major": 2,
                "owner_module": v1_schema_binding["owner_module"],
                "raw_artifact_id": v2_schema_artifact_id,
                "schema_sha256": schema_digest,
            }
        )
        self.fixture.bom["contracts"].append(
            {
                "contract_id": v1_schema_binding["contract_id"],
                "major": 2,
                "schema_sha256": schema_digest,
                "owner_module": v1_schema_binding["owner_module"],
            }
        )

        compatibility = compatibility_artifact_from_manifests(manifests)
        self.refresh_compatibility_bom_and_receipt(compatibility)
        candidate_bom_digest = self.fixture.evidence["release_bom"]["sha256"]
        matrix_digest = rollout["compatibility_matrix_sha256"]
        previous_bom_metadata = self.raw_metadata(rollout["previous_stable_bom_artifact_id"])
        previous_bom = json.loads(Path(previous_bom_metadata["path"]).read_bytes())
        previous_bom_digest = rollout["previous_stable_bom_sha256"]
        candidate_modules = external_gate_module._bom_module_inventory(
            self.fixture.bom, "candidate Release BOM"
        )
        previous_modules = external_gate_module._bom_module_inventory(
            previous_bom, "previous stable Release BOM"
        )
        active_row = next(row for row in compatibility["declarationMatrix"] if row["runnable"] is True)
        row_id, row_sha = external_gate_module._matrix_row_identity(active_row)
        runtime_producer = active_row.get(
            "runtimeProducerModule", active_row["producerModule"]
        )
        transport_sender = active_row.get("transportSenderModule", runtime_producer)
        communication_sha = active_row.get(
            "communicationPairSha256",
            hashlib.sha256(canonical_bytes(active_row)).hexdigest(),
        )
        execution = self.execution_artifact()
        result = execution["row_results"][0]
        result.update(
            {
                "matrix_row_id": row_id,
                "matrix_row_sha256": row_sha,
                "contract_id": active_row["contractId"],
                "major": active_row["major"],
                "owner_module": active_row["ownerModule"],
                "runtime_producer_module": runtime_producer,
                "transport_sender_module": transport_sender,
                "consumer_module": active_row["consumerModule"],
                "producer_mode": "active",
                "consumer_mode": "active",
                "communication_pair_sha256": communication_sha,
            }
        )
        for combination in result["combination_results"]:
            combination_id = combination["combination_id"]
            expectation = (
                "FAIL_CLOSED_BY_GROUP"
                if combination_id in {"N/N-1", "N-1/N"}
                else "RUNNABLE"
            )
            producer_selection, consumer_selection = external_gate_module._combination_selection(
                combination_id,
                runtime_producer,
                active_row["consumerModule"],
                candidate_modules,
                previous_modules,
            )
            observation_metadata = self.raw_metadata(
                combination["raw_evidence_artifact_id"]
            )
            observation = json.loads(Path(observation_metadata["path"]).read_bytes())
            observation.update(
                {
                    "matrix_row_id": row_id,
                    "expectation": expectation,
                    "candidate_bom_sha256": candidate_bom_digest,
                    "previous_stable_bom_sha256": previous_bom_digest,
                    "compatibility_snapshot_sha256": matrix_digest,
                    "producer": producer_selection,
                    "consumer": consumer_selection,
                    "observed_outcome": (
                        "RUNNABLE"
                        if expectation == "RUNNABLE"
                        else "FAIL_CLOSED"
                    ),
                }
            )
            observation_bytes = canonical_bytes(observation)
            observation_digest = hashlib.sha256(observation_bytes).hexdigest()
            Path(observation_metadata["path"]).write_bytes(observation_bytes)
            observation_metadata["sha256"] = observation_digest
            observation_metadata["size_bytes"] = len(observation_bytes)
            combination.update(
                {
                    "expectation": expectation,
                    "producer": producer_selection,
                    "consumer": consumer_selection,
                    "raw_evidence_sha256": observation_digest,
                }
            )
        execution["candidate_release_bom"].update(
            {
                "sha256": candidate_bom_digest,
                "generation": self.fixture.bom["release_bom_generation"],
                "activation_token_sha256": self.fixture.bom["activation_token_sha256"],
                "signature_sha256": hashlib.sha256(
                    canonical_bytes(self.fixture.bom["signature"])
                ).hexdigest(),
            }
        )
        execution["compatibility_snapshot"]["sha256"] = matrix_digest
        execution["row_set_sha256"] = hashlib.sha256(
            canonical_bytes([row_id])
        ).hexdigest()
        execution["compatibility_group"] = external_gate_module._expected_compatibility_group(
            compatibility,
            {
                node: {
                    edge["provider"]
                    for edge in dependency_artifact_from_manifests(manifests)["edges"]
                    if edge["consumer"] == node
                }
                for node in dependency_artifact_from_manifests(manifests)["nodes"]
            },
            candidate_modules,
            matrix_digest,
            candidate_bom_digest,
        )
        self.replace_execution_artifact(execution)

    def test_f9_complete_signed_context_is_eligible_but_issues_no_receipt(self) -> None:
        decision = self.decision()
        self.assertEqual(0, decision.exit_code)
        self.assertEqual(ELIGIBLE, decision.decision)
        self.assertFalse(decision.as_dict()["evidence_receipt_issued"])

    def test_f9_compatibility_group_with_complete_external_evidence_is_eligible(
        self,
    ) -> None:
        self.convert_to_compatibility_group()
        decision = self.decision()
        self.assertEqual(0, decision.exit_code)
        self.assertEqual(ELIGIBLE, decision.decision)

    def test_f9_compatibility_group_rejects_incomplete_members_or_order(
        self,
    ) -> None:
        self.convert_to_compatibility_group()
        execution = self.execution_artifact()
        execution["compatibility_group"]["members"].pop()
        self.replace_execution_artifact(execution)
        decision = self.decision()
        self.assertNotEqual(0, decision.exit_code)
        self.assertEqual("compatibility_group_evidence_mismatch", decision.reason_code)

    def test_f9_explicitly_rejects_legacy_v1_compatibility_matrix(self) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        metadata = self.raw_metadata(rollout["compatibility_matrix_artifact_id"])
        compatibility = json.loads(Path(metadata["path"]).read_bytes())
        compatibility["schemaVersion"] = "dps.compatibility-matrix/v1"
        compatibility.pop("policySha256")
        self.refresh_compatibility_bom_and_receipt(compatibility)
        decision = self.decision()
        self.assertNotEqual(0, decision.exit_code)
        self.assertEqual(
            "compatibility_matrix_manifest_mismatch", decision.reason_code
        )

    def test_f9_accepts_current_v2_module_manifest(self) -> None:
        """The current major (dps.module/v2) is the default fixture world; F9
        accepts it end-to-end and the complete signed context is eligible.  This
        is the positive, full-eligibility evidence for the current major -- the
        removed resolver lives only in the agents block, which F9 does not
        inspect, so nothing about the migration weakens the F9 checks."""
        binding = self.fixture.evidence["payload"]["module_rollout_lines"][
            "manifest_artifacts"
        ][0]
        manifest = json.loads(Path(self.raw_metadata(binding["raw_artifact_id"])["path"]).read_bytes())
        self.assertEqual("dps.module/v2", manifest["schemaVersion"])
        decision = self.decision()
        self.assertEqual(0, decision.exit_code)
        self.assertEqual(ELIGIBLE, decision.decision)

    def test_f9_accepts_historical_v1_module_manifest(self) -> None:
        """Rollback window: F9 still accepts the historical major (dps.module/v1).
        Re-stamping the manifest bytes re-digests the module BOM, which this helper
        does not re-sign into the execution evidence, so only the version axis is
        asserted: v1 clears the manifest-version gate and reaches the later
        BOM-binding checks instead of being rejected as an unknown version
        (contrast test_f9_rejects_unknown_module_manifest_major)."""
        binding = self.fixture.evidence["payload"]["module_rollout_lines"][
            "manifest_artifacts"
        ][0]
        manifest = json.loads(Path(self.raw_metadata(binding["raw_artifact_id"])["path"]).read_bytes())
        manifest["schemaVersion"] = "dps.module/v1"
        self.replace_first_manifest(manifest)
        decision = self.decision()
        self.assertNotEqual("unknown_manifest_version", decision.reason_code)

    def test_f9_rejects_unknown_module_manifest_major(self) -> None:
        """An unrecognised major fails closed rather than being assumed
        structurally compatible from a loose version prefix."""
        binding = self.fixture.evidence["payload"]["module_rollout_lines"][
            "manifest_artifacts"
        ][0]
        manifest = json.loads(Path(self.raw_metadata(binding["raw_artifact_id"])["path"]).read_bytes())
        manifest["schemaVersion"] = "dps.module/v9"
        self.replace_first_manifest(manifest)
        decision = self.decision()
        self.assertNotEqual(0, decision.exit_code)
        self.assertEqual("unknown_manifest_version", decision.reason_code)

    def test_f9_execution_and_observation_examples_conform_to_schemas(
        self,
    ) -> None:
        from jsonschema import Draft202012Validator
        from referencing import Registry, Resource

        verification_root = (
            Path(__file__).resolve().parents[3]
            / "governance"
            / "verification"
        )
        schemas = {
            path.name: json.loads(path.read_text(encoding="utf-8"))
            for path in verification_root.glob("*.schema.json")
        }
        registry = Registry()
        for schema in schemas.values():
            registry = registry.with_resource(
                schema["$id"], Resource.from_contents(schema)
            )
        execution = self.execution_artifact()
        execution_schema = schemas[
            "f9-compatibility-execution-evidence.v1.schema.json"
        ]
        execution_errors = list(
            Draft202012Validator(
                execution_schema, registry=registry
            ).iter_errors(execution)
        )
        self.assertEqual([], execution_errors)

        observation_binding = execution["row_results"][0][
            "combination_results"
        ][0]
        observation_metadata = self.raw_metadata(
            observation_binding["raw_evidence_artifact_id"]
        )
        observation = json.loads(
            Path(observation_metadata["path"]).read_bytes()
        )
        observation_schema = schemas[
            "f9-compatibility-combination-observation.v1.schema.json"
        ]
        observation_errors = list(
            Draft202012Validator(
                observation_schema, registry=registry
            ).iter_errors(observation)
        )
        self.assertEqual([], observation_errors)

    def test_f9_static_matrix_without_execution_artifact_is_never_eligible(self) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        rollout.pop("compatibility_execution_artifact_id")
        rollout.pop("compatibility_execution_sha256")
        self.fixture.reseal()
        decision = self.decision()
        self.assertNotEqual(0, decision.exit_code)
        self.assertNotEqual(ELIGIBLE, decision.decision)

    def test_f9_requires_all_four_execution_combinations(self) -> None:
        artifact = self.execution_artifact()
        artifact["row_results"][0]["combination_results"].pop()
        self.replace_execution_artifact(artifact)
        decision = self.decision()
        self.assertEqual("compatibility_combination_inventory", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_duplicate_execution_combination(self) -> None:
        artifact = self.execution_artifact()
        combinations = artifact["row_results"][0]["combination_results"]
        combinations[-1]["combination_id"] = combinations[0]["combination_id"]
        self.replace_execution_artifact(artifact)
        decision = self.decision()
        self.assertEqual("compatibility_combination_inventory", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_skip_partial_or_not_run_combination_summary(self) -> None:
        for field, value in (
            ("evidence_status", "SKIP"),
            ("partial_count", 1),
            ("not_run_count", 1),
        ):
            with self.subTest(field=field):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f9()
                    artifact_binding = fixture.evidence["payload"]["module_rollout_lines"]
                    metadata = next(
                        item
                        for item in fixture.evidence["raw_artifacts"]
                        if item["artifact_id"] == artifact_binding["compatibility_execution_artifact_id"]
                    )
                    artifact = json.loads(Path(metadata["path"]).read_bytes())
                    artifact["row_results"][0]["combination_results"][0][field] = value
                    raw = canonical_bytes(artifact)
                    digest = hashlib.sha256(raw).hexdigest()
                    Path(metadata["path"]).write_bytes(raw)
                    metadata["sha256"] = digest
                    metadata["size_bytes"] = len(raw)
                    artifact_binding["compatibility_execution_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f9", fixture.evidence_path, fixture.trust_path.resolve(), accept_signature
                    )
                    self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_stale_execution_bom_or_matrix_binding(self) -> None:
        for target in ("candidate_release_bom", "compatibility_snapshot"):
            with self.subTest(target=target):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f9()
                    binding = fixture.evidence["payload"]["module_rollout_lines"]
                    metadata = next(
                        item
                        for item in fixture.evidence["raw_artifacts"]
                        if item["artifact_id"] == binding["compatibility_execution_artifact_id"]
                    )
                    artifact = json.loads(Path(metadata["path"]).read_bytes())
                    artifact[target]["sha256"] = "f" * 64
                    raw = canonical_bytes(artifact)
                    digest = hashlib.sha256(raw).hexdigest()
                    Path(metadata["path"]).write_bytes(raw)
                    metadata["sha256"] = digest
                    metadata["size_bytes"] = len(raw)
                    binding["compatibility_execution_sha256"] = digest
                    fixture.reseal()
                    decision = run_gate(
                        "f9", fixture.evidence_path, fixture.trust_path.resolve(), accept_signature
                    )
                    self.assertNotEqual(0, decision.exit_code)

    def test_f9_execution_artifact_requires_independent_external_signature(self) -> None:
        def reject_execution(_key: bytes, payload: bytes, _signature: object) -> None:
            if payload.startswith(b"dps-compatibility-execution-evidence/v1\n"):
                raise ExternalGateError("invalid_signature", "compatibility execution signature rejected")

        decision = self.decision(reject_execution)
        self.assertEqual("invalid_signature", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_boolean_canary_prerequisite_self_assertion(self) -> None:
        self.fixture.evidence["payload"]["canary_prerequisite"] = {"verified": True}
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("invalid_shape", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_canary_receipt_must_bind_exact_release_bom(self) -> None:
        binding = self.fixture.evidence["payload"]["canary_prerequisite"]
        metadata = self.raw_metadata(binding["raw_artifact_id"])
        receipt = json.loads(Path(metadata["path"]).read_bytes())
        receipt["release_bom_sha256"] = "f" * 64
        self.replace_receipt(receipt)
        decision = self.decision()
        self.assertEqual("prerequisite_bom_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_canary_receipt_requires_a_real_trusted_signature(self) -> None:
        calls = 0

        def reject_prerequisite(_key: bytes, _payload: bytes, _signature: object) -> None:
            nonlocal calls
            calls += 1
            if calls == 3:
                raise ExternalGateError("invalid_signature", "prerequisite signature rejected")

        decision = self.decision(reject_prerequisite)
        self.assertEqual(3, calls)
        self.assertEqual("invalid_signature", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_allows_at_most_four_rollout_lines(self) -> None:
        lines = self.fixture.evidence["payload"]["module_rollout_lines"]["lines"]
        for index in range(2, 6):
            lines.append(
                {
                    "line_id": f"f9-rollout-line-{index:04d}",
                    "module_ids": ["windows-edge-worker"],
                    "status": "PASS",
                }
            )
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("parallel_scope_exceeded", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_cross_line_dependency_from_verified_bom_graph(self) -> None:
        dependency_binding = self.fixture.evidence["payload"]["module_rollout_lines"]
        dependency_artifact = self.add_dependent_module()
        dependency_binding["lines"] = [
            {"line_id": "f9-rollout-line-0001", "module_ids": ["windows-edge-worker"], "status": "PASS"},
            {"line_id": "f9-rollout-line-0002", "module_ids": ["command-orchestrator"], "status": "PASS"},
        ]
        self.refresh_dependency_bom_and_receipt(dependency_artifact)
        decision = self.decision()
        self.assertEqual("rollout_lines_not_independent", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_dependency_edge_omitted_from_self_reported_graph(self) -> None:
        dependency_artifact = self.add_dependent_module()
        dependency_artifact["edges"] = []
        self.refresh_dependency_bom_and_receipt(dependency_artifact)
        decision = self.decision()
        self.assertEqual("dependency_graph_manifest_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_manifest_bytes_must_match_signed_bom_hash(self) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        manifest_binding = rollout["manifest_artifacts"][0]
        metadata = self.raw_metadata(manifest_binding["raw_artifact_id"])
        manifest = json.loads(Path(metadata["path"]).read_bytes())
        manifest["module"]["note"] = "tampered-after-bom-signing"
        raw = canonical_bytes(manifest)
        digest = hashlib.sha256(raw).hexdigest()
        Path(metadata["path"]).write_bytes(raw)
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(raw)
        manifest_binding["manifest_sha256"] = digest
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("manifest_bom_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_compatibility_matrix_not_rebuilt_from_manifests(self) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        metadata = self.raw_metadata(rollout["compatibility_matrix_artifact_id"])
        compatibility = json.loads(Path(metadata["path"]).read_bytes())
        compatibility["majorDeclarations"].append(
            {
                "contractId": "forged.contract",
                "moduleId": "windows-edge-worker",
                "declarationKind": "provided",
                "major": 1,
                "source": "Modules/windows-edge-worker/contracts/provided/forged.contract.v1.schema.json",
                "status": "proposed",
                "mode": "active",
                "ownerModule": "windows-edge-worker",
                "candidateGreenEligible": True,
            }
        )
        self.refresh_compatibility_bom_and_receipt(compatibility)
        decision = self.decision()
        self.assertEqual("compatibility_matrix_manifest_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_contract_without_explicit_mode(self) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        binding = rollout["manifest_artifacts"][0]
        manifest = json.loads(Path(self.raw_metadata(binding["raw_artifact_id"])["path"]).read_bytes())
        manifest["contracts"]["provided"].append(
            {
                "contractId": "sample.event",
                "major": 1,
                "source": "Modules/windows-edge-worker/contracts/provided/sample.event.v1.schema.json",
                "status": "proposed",
                "ownerModule": "windows-edge-worker",
            }
        )
        self.replace_first_manifest(manifest)
        decision = self.decision()
        self.assertEqual("invalid_shape", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_unknown_contract_mode(self) -> None:
        rollout = self.fixture.evidence["payload"]["module_rollout_lines"]
        binding = rollout["manifest_artifacts"][0]
        manifest = json.loads(Path(self.raw_metadata(binding["raw_artifact_id"])["path"]).read_bytes())
        manifest["contracts"]["provided"].append(
            {
                "contractId": "sample.event",
                "major": 1,
                "source": "Modules/windows-edge-worker/contracts/provided/sample.event.v1.schema.json",
                "status": "proposed",
                "mode": "future-mode",
                "ownerModule": "windows-edge-worker",
            }
        )
        self.replace_first_manifest(manifest)
        decision = self.decision()
        self.assertEqual("unknown_contract_mode", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_module_rollback_must_finish_within_five_minutes(self) -> None:
        module_drill = next(
            item for item in self.fixture.evidence["payload"]["rollback_drills"] if item["scope"] == "module"
        )
        module_drill["duration_minutes"] = 5.01
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("rollback_too_slow", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_does_not_accept_simulation_as_sustained_real_load(self) -> None:
        self.fixture.evidence["payload"]["load_runs"]["sustained"]["evidence_kind"] = "SIMULATED"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("load_evidence_kind_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_load_run_must_bind_a_distinct_envelope_artifact(self) -> None:
        self.fixture.evidence["payload"]["load_runs"]["burst"]["artifact_id"] = "missing-load-artifact-0001"
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("raw_artifact_binding_missing", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_load_run_ids_and_artifacts_must_be_independent(self) -> None:
        sustained = self.fixture.evidence["payload"]["load_runs"]["sustained"]
        simulated = self.fixture.evidence["payload"]["load_runs"]["simulated"]
        simulated["run_id"] = sustained["run_id"]
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("load_evidence_not_independent", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_rejects_marker_bytes_instead_of_versioned_load_json(self) -> None:
        run = self.fixture.evidence["payload"]["load_runs"]["sustained"]
        metadata = self.raw_metadata(run["artifact_id"])
        marker = b"sustained"
        Path(metadata["path"]).write_bytes(marker)
        digest = hashlib.sha256(marker).hexdigest()
        metadata["sha256"] = digest
        metadata["size_bytes"] = len(marker)
        metadata["media_type"] = "application/octet-stream"
        run["artifact_sha256"] = digest
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("raw_artifact_media_type", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_raw_sustained_concurrency_is_recomputed(self) -> None:
        artifact = self.load_artifact("sustained")
        artifact["actor_sets"][0]["actor_digests"] = artifact["actor_sets"][0]["actor_digests"][:99]
        self.fixture.evidence["payload"]["load_runs"]["sustained"]["concurrency"] = 99
        self.replace_load_artifact("sustained", artifact)
        decision = self.decision()
        self.assertEqual("load_threshold_not_met", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_raw_burst_and_simulated_concurrency_thresholds_are_recomputed(self) -> None:
        for name, concurrency in (("burst", 199), ("simulated", 399)):
            with self.subTest(name=name):
                with tempfile.TemporaryDirectory() as directory:
                    fixture = Fixture(Path(directory))
                    fixture.use_f9()
                    test_case = F9ExternalGateTests(methodName="test_f9_complete_signed_context_is_eligible_but_issues_no_receipt")
                    test_case.fixture = fixture
                    artifact = test_case.load_artifact(name)
                    artifact["actor_sets"][0]["actor_digests"] = artifact["actor_sets"][0]["actor_digests"][:concurrency]
                    fixture.evidence["payload"]["load_runs"][name]["concurrency"] = concurrency
                    test_case.replace_load_artifact(name, artifact)
                    decision = run_gate("f9", fixture.evidence_path, fixture.trust_path.resolve(), accept_signature)
                    self.assertEqual("load_threshold_not_met", decision.reason_code)
                    self.assertNotEqual(0, decision.exit_code)

    def test_f9_signed_duration_must_equal_raw_window_duration(self) -> None:
        self.fixture.evidence["payload"]["load_runs"]["burst"]["duration_seconds"] += 1
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("load_duration_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_raw_window_gap_cannot_be_hidden_by_signed_summary(self) -> None:
        artifact = self.load_artifact("sustained")
        second_start = datetime.fromisoformat(artifact["windows"][1]["started_at"].replace("Z", "+00:00"))
        artifact["windows"][1]["started_at"] = iso(second_start + timedelta(seconds=1))
        self.replace_load_artifact("sustained", artifact)
        decision = self.decision()
        self.assertEqual("load_window_discontinuity", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_raw_recovery_cannot_rebound_after_zero(self) -> None:
        artifact = self.load_artifact("burst")
        artifact["recovery_samples"][2]["backlog_depth"] = 1
        artifact["recovery_samples"][2]["oldest_backlog_age_seconds"] = 5
        self.replace_load_artifact("burst", artifact)
        decision = self.decision()
        self.assertEqual("backlog_rebounded", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_final_window_and_first_recovery_sample_must_be_continuous(self) -> None:
        artifact = self.load_artifact("sustained")
        final_window = artifact["windows"][-1]
        final_window["maximum_backlog_depth"] = 0
        final_window["maximum_oldest_backlog_age_seconds"] = 0
        final_window["backlog_depth_at_finish"] = 0
        final_window["oldest_backlog_age_seconds_at_finish"] = 0
        self.replace_load_artifact("sustained", artifact)
        decision = self.decision()
        self.assertEqual("recovery_state_discontinuity", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_recovery_samples_reject_long_term_backlog_age(self) -> None:
        artifact = self.load_artifact("burst")
        artifact["recovery_samples"][1]["backlog_depth"] = 1
        artifact["recovery_samples"][1]["oldest_backlog_age_seconds"] = 999999
        self.replace_load_artifact("burst", artifact)
        decision = self.decision()
        self.assertEqual("long_term_backlog", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_raw_load_windows_reject_long_term_backlog_before_final_recovery(self) -> None:
        artifact = self.load_artifact("sustained")
        for window in artifact["windows"]:
            window["maximum_backlog_depth"] = 1
            window["maximum_oldest_backlog_age_seconds"] = 999999
            window["backlog_depth_at_finish"] = 1
            window["oldest_backlog_age_seconds_at_finish"] = 999999
        self.replace_load_artifact("sustained", artifact)
        decision = self.decision()
        self.assertEqual("long_term_backlog", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_raw_load_windows_reject_monotonically_growing_backlog(self) -> None:
        artifact = self.load_artifact("sustained")
        first, second = artifact["windows"][:2]
        first.update(
            {
                "maximum_backlog_depth": 1,
                "maximum_oldest_backlog_age_seconds": 30,
                "backlog_depth_at_finish": 1,
                "oldest_backlog_age_seconds_at_finish": 30,
            }
        )
        second.update(
            {
                "maximum_backlog_depth": 2,
                "maximum_oldest_backlog_age_seconds": 60,
                "backlog_depth_at_finish": 2,
                "oldest_backlog_age_seconds_at_finish": 60,
            }
        )
        self.replace_load_artifact("sustained", artifact)
        decision = self.decision()
        self.assertEqual("growing_backlog", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)

    def test_f9_managed_device_count_is_recomputed_from_real_actor_union(self) -> None:
        self.fixture.evidence["payload"]["managed_devices"] = 201
        self.fixture.reseal()
        decision = self.decision()
        self.assertEqual("managed_device_cardinality_mismatch", decision.reason_code)
        self.assertNotEqual(0, decision.exit_code)


class CryptographicVerifierTests(unittest.TestCase):
    PUBLIC_KEY = """-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyQ8otqDDpmvcMwf6JNQyeAvLTv3+
KmlV8pWNn2xlzqSZkLbk+ShtSF8+XkP1TWPXmVh49Sdrj2x+gArd7mSAkA==
-----END PUBLIC KEY-----
"""
    SIGNATURE = "HAnyIYgIt8EQtpQ5/Mw7gD+uwC4kXrBoGUB+s8nQwBPez/tVgORerCqYK1psHAwpvKzvBwPZQrYUKgCw21KB3Q=="

    def test_openssl_verifies_p256_p1363_and_rejects_tampering(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            key = Path(directory) / "public.pem"
            key.write_text(self.PUBLIC_KEY, encoding="ascii")
            _openssl_verify_p1363(key.read_bytes(), b"known external runner payload", self.SIGNATURE)
            with self.assertRaises(ExternalGateError):
                _openssl_verify_p1363(key.read_bytes(), b"tampered external runner payload", self.SIGNATURE)


class StagePayloadTests(unittest.TestCase):
    def test_f7_contract_binding_policy_is_explicitly_stale_until_refreeze(self) -> None:
        policy_path = Path(__file__).resolve().parents[3] / "governance" / "verification" / "external-gate-policy.v1.json"
        policy = json.loads(policy_path.read_text(encoding="utf-8"))
        f7 = policy["stages"]["f7"]
        self.assertEqual("STALE", f7["contract_binding_status"])
        self.assertEqual("STALE", external_gate_module.F7_GBRAIN_CONTRACT_BINDING_STATUS)
        self.assertIn("independently re-frozen", f7["contract_binding_stale_reason"])
        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(Path(directory))
            fixture.use_f7()
            decision = run_gate(
                "f7",
                fixture.evidence_path,
                fixture.trust_path.resolve(),
                accept_signature,
                clock=lambda: fixture.finished + timedelta(minutes=2),
            )
            self.assertEqual(external_gate_module.WAITING_EXTERNAL, decision.status)
            self.assertEqual("f7_gbrain_contract_binding_stale", decision.reason_code)

    def test_stage_environment_schemas_match_runtime_exact_allowlists(self) -> None:
        verification_root = Path(__file__).resolve().parents[3] / "governance" / "verification"
        expected = {
            "f6-windows-zenno-input.v1.schema.json": {
                "environment_id",
                "os_family",
                "windows_version",
                "zennodroid_version",
                "dotnet_framework_version",
                "csharp_language_version",
                "codedom_compile",
                "gac_resolution",
                "dll_load",
                "zenno_project_load",
                "bridge_abi",
                "adb_authorized_device_count",
                "adb_authorization",
                "loopback_host",
                "loopback_port",
                "loopback_port_fixed",
                "loopback_only",
                "command_timeout_seconds",
                "timeout_semantics",
                "error_semantics",
                "connection_continuity",
            },
            "f7-device-gbrain-input.v3.schema.json": {
                "environment_id",
                "os_family",
                "gbrain_deployment_id",
                "parent_windows_environment_id",
                "edge_installation_id",
                "zenno_installation_id",
                "runner_component",
                "runner_version",
                "runner_binary_sha256",
                "runner_sbom_sha256",
            },
            "f8-canary-input.v1.schema.json": {"environment_id", "os_family"},
            "f9-scale-input.v1.schema.json": {"environment_id", "os_family"},
        }
        for filename, keys in expected.items():
            with self.subTest(filename=filename):
                schema = json.loads((verification_root / filename).read_text(encoding="utf-8"))
                environment = schema["allOf"][1]["properties"]["environment"]
                self.assertFalse(environment["additionalProperties"])
                self.assertEqual(keys, set(environment["required"]))
                self.assertEqual(keys, set(environment["properties"]))

    def test_f6_schema_requires_complete_capability_probe(self) -> None:
        schema_path = (
            Path(__file__).resolve().parents[3]
            / "governance"
            / "verification"
            / "f6-windows-zenno-input.v1.schema.json"
        )
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        capability = schema["allOf"][1]["properties"]["payload"]["properties"]["capability_probe"]
        for field in (
            "zennodroid_version",
            "dotnet_framework_version",
            "csharp_language_version",
            "codedom_compile",
            "gac_resolution",
            "dll_load",
            "zenno_project_load",
            "adb_authorization",
            "bridge_abi",
            "loopback_host",
            "loopback_port",
            "loopback_port_fixed",
            "command_timeout_seconds",
            "timeout_semantics",
            "error_semantics",
            "connection_continuity",
        ):
            self.assertIn(field, capability["required"])
        process = schema["allOf"][1]["properties"]["payload"]["properties"]["zenno_process"]
        self.assertIn("observed_at_before", process["required"])
        self.assertIn("observed_at_after", process["required"])

    def test_f9_schema_requires_signed_canary_and_bom_dependency_graph_bindings(self) -> None:
        verification_root = Path(__file__).resolve().parents[3] / "governance" / "verification"
        schema = json.loads((verification_root / "f9-scale-input.v1.schema.json").read_text(encoding="utf-8"))
        payload = schema["allOf"][1]["properties"]["payload"]
        self.assertIn("canary_prerequisite", payload["required"])
        self.assertIn("module_rollout_lines", payload["required"])
        rollout = payload["properties"]["module_rollout_lines"]
        self.assertEqual(4, rollout["properties"]["lines"]["maxItems"])
        for field in (
            "compatibility_matrix_sha256",
            "compatibility_policy_artifact_id",
            "compatibility_policy_sha256",
            "previous_stable_bom_artifact_id",
            "previous_stable_bom_sha256",
            "compatibility_execution_artifact_id",
            "compatibility_execution_sha256",
            "manifest_artifacts",
            "contract_schema_artifacts",
        ):
            self.assertIn(field, rollout["required"])
        for filename, property_name, version in (
            (
                "f8-canary-prerequisite-receipt.v1.schema.json",
                "schema_version",
                "dps.external-verification-receipt/v1",
            ),
            ("f9-module-dependency-dag.v1.schema.json", "schemaVersion", "dps.dependency-graph/v1"),
            ("f9-compatibility-matrix.v1.schema.json", "schemaVersion", "dps.compatibility-matrix/v1"),
            ("f9-compatibility-matrix.v2.schema.json", "schemaVersion", "dps.compatibility-matrix/v2"),
            (
                "f9-compatibility-execution-evidence.v1.schema.json",
                "schema_version",
                "dps.compatibility-execution-evidence/v1",
            ),
            (
                "f9-compatibility-combination-observation.v1.schema.json",
                "schema_version",
                "dps.compatibility-combination-observation/v1",
            ),
            ("f9-load-run-artifact.v1.schema.json", "schema_version", "dps.f9-load-run-artifact/v1"),
        ):
            artifact_schema = json.loads((verification_root / filename).read_text(encoding="utf-8"))
            self.assertFalse(artifact_schema["additionalProperties"])
            self.assertEqual(version, artifact_schema["properties"][property_name]["const"])

    def test_f7_v3_schema_requires_windows_prerequisite_and_exact_raw_evidence_set(self) -> None:
        verification_root = Path(__file__).resolve().parents[3] / "governance" / "verification"
        schema_path = verification_root / "f7-device-gbrain-input.v3.schema.json"
        schema = json.loads(schema_path.read_text(encoding="utf-8"))
        payload_schema = schema["allOf"][1]["properties"]["payload"]
        self.assertEqual("Windows+Android", schema["allOf"][1]["properties"]["environment"]["properties"]["os_family"]["const"])
        for field in (
            "repository_id",
            "f7_run_id",
            "trace_id",
            "release_bom_id",
            "release_bom_sha256",
            "operation_timeline",
            "f6_prerequisite",
            "semantic_artifacts",
        ):
            self.assertIn(field, payload_schema["required"])
        source_mapping = schema["$defs"]["sourceMapping"]
        self.assertIn("source_binding_nonce", source_mapping["required"])
        self.assertEqual(1023, source_mapping["properties"]["source_binding_nonce"]["maximum"])
        semantic = payload_schema["properties"]["semantic_artifacts"]
        self.assertEqual(24, semantic["minItems"])
        self.assertEqual(24, semantic["maxItems"])
        self.assertNotIn("gbrain_live", payload_schema["properties"])
        self.assertNotIn("cross_soul_read_failures", payload_schema["properties"])

        artifact_schema = json.loads((verification_root / "f7-raw-evidence-artifact.v1.schema.json").read_text(encoding="utf-8"))
        self.assertFalse(artifact_schema["additionalProperties"])
        self.assertEqual("dps.f7-raw-evidence-artifact/v1", artifact_schema["properties"]["schema_version"]["const"])
        self.assertIn("content_sha256", artifact_schema["required"])
        self.assertIn("scope", artifact_schema["required"])
        self.assertEqual(12, len(artifact_schema["oneOf"]))
        projection_content = artifact_schema["$defs"]["projectionContent"]
        self.assertIn("source_binding_sha256", projection_content["required"])
        self.assertIn("source_binding_base64", projection_content["required"])
        self.assertIn("gbrain.projection/v2", projection_content["description"])

        receipt_schema = json.loads((verification_root / "f7-windows-prerequisite-receipt.v1.schema.json").read_text(encoding="utf-8"))
        self.assertEqual("WINDOWS_VERIFIED", receipt_schema["properties"]["verification_level"]["const"])
        self.assertEqual("PASS", receipt_schema["properties"]["status"]["const"])
        for field in ("repository_id", "baseline_commit", "release_bom_sha256", "candidate_artifact_sha256", "trust_policy_sha256", "issued_at", "expires_at", "signature"):
            self.assertIn(field, receipt_schema["required"])

    def test_f7_v1_and_v2_schemas_are_explicitly_historical(self) -> None:
        verification_root = Path(__file__).resolve().parents[3] / "governance" / "verification"
        for version in ("v1", "v2"):
            schema = json.loads((verification_root / f"f7-device-gbrain-input.{version}.schema.json").read_text(encoding="utf-8"))
            comment = schema.get("$comment", "").lower()
            self.assertIn("historical", comment)
            self.assertIn("v3", comment)

    def test_projection_decimal_mirror_rejects_values_not_representable_by_system_decimal(self) -> None:
        for value in (Decimal("1e100"), Decimal("1e-29")):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ExternalGateError, "System.Decimal"):
                    external_gate_module._dotnet_decimal(value, "projection.test_decimal")

    def test_f8_wrong_wave_order_and_threshold_are_rejected(self) -> None:
        payload = valid_f8_payload()
        payload["waves"][3]["name"] = "3"
        with self.assertRaises(ExternalGateError):
            validate_stage_payload("f8", payload, utc_window(200))
        payload = valid_f8_payload()
        payload["technical_measurements"]["max_error_rate_ratio_over_5m"] = 2.0
        with self.assertRaises(ExternalGateError):
            validate_stage_payload("f8", payload, utc_window(200))

    def test_f9_payload_without_signed_prerequisite_context_is_rejected(self) -> None:
        payload = valid_f9_payload()
        payload["canary_prerequisite"] = {
            "receipt_id": "f8-canary-receipt-0001",
            "raw_artifact_id": "f8-canary-receipt-artifact-0001",
            "raw_artifact_sha256": "a" * 64,
        }
        payload["module_rollout_lines"] = {
            "dependency_graph_artifact_id": "f9-dependency-dag-0001",
            "dependency_graph_sha256": "b" * 64,
            "lines": [
                {"line_id": "f9-rollout-line-0001", "module_ids": ["windows-edge-worker"], "status": "PASS"}
            ],
        }
        with self.assertRaises(ExternalGateError):
            validate_stage_payload("f9", payload, utc_window(200))


def utc_window(hours: float) -> tuple[datetime, datetime]:
    start = datetime(2026, 7, 1, tzinfo=timezone.utc)
    return start, start + timedelta(hours=hours)


def _f7_scope(payload: dict, soul_id: str) -> dict:
    device = next(item for item in payload["devices"] if item["soul_id"] == soul_id)
    mapping = next(item for item in payload["source_mappings"] if item["soul_id"] == soul_id)
    return {
        "soul_id": soul_id,
        "device_binding_id": device["device_binding_id"],
        "platform_account_id": device["platform_account_id"],
        "logical_source_id": mapping["logical_source_id"],
        "external_source_alias": mapping["external_source_alias"],
    }


def _f7_phase_window(payload: dict, kind: str) -> tuple[datetime, datetime]:
    phase_name = external_gate_module.F7_PHASE_BY_ARTIFACT_KIND[kind]
    phase = next(item for item in payload["operation_timeline"] if item["phase"] == phase_name)
    return (
        datetime.fromisoformat(phase["started_at"].replace("Z", "+00:00")),
        datetime.fromisoformat(phase["finished_at"].replace("Z", "+00:00")),
    )


def _f7_observation_common(payload: dict, scope: dict, kind: str, observed_at: datetime) -> dict:
    return {
        "f7_run_id": payload["f7_run_id"],
        "trace_id": payload["trace_id"],
        "release_bom_id": payload["release_bom_id"],
        "release_bom_sha256": payload["release_bom_sha256"],
        "phase": external_gate_module.F7_PHASE_BY_ARTIFACT_KIND[kind],
        "observed_at": iso(observed_at),
        "scope_sha256": hashlib.sha256(canonical_bytes(scope)).hexdigest(),
    }


def _wrap_f7_semantic_observation(kind: str, observation: dict) -> dict:
    raw = canonical_bytes(observation)
    return {
        "observation_schema_version": external_gate_module._f7_observation_schema_version(kind),
        "observation_sha256": hashlib.sha256(raw).hexdigest(),
        "observation_base64": base64.b64encode(raw).decode("ascii"),
    }


def _f7_semantic_exchange(kind: str, index: int, scope: dict, observation: dict) -> dict:
    scope_sha256 = hashlib.sha256(canonical_bytes(scope)).hexdigest()
    request_id = (
        observation.get("request_id")
        or observation.get("delete_request_id")
        or observation.get("attack_id")
        or observation.get("command_id")
        or f"f7-{kind.lower().replace('_', '-')}-request-{index:04d}"
    )
    response_outcome = "OBSERVED"
    postcondition_outcome = "VERIFIED"
    if kind == "SOUL_DEVICE_SOURCE_OAUTH_BINDING":
        request_payload = {
            "operation": "VERIFY_DEVICE_SOURCE_OAUTH_BINDING",
            "device_transport": observation["device_transport"],
            "oauth_credential_lease_id": observation["oauth_credential_lease_id"],
            "requested_source_id": scope["logical_source_id"],
            "requested_source_alias": scope["external_source_alias"],
        }
        response_payload = {
            "adb_probe": {
                "serial_hmac_sha256": observation["adb_serial_hmac_sha256"],
                "attestation_sha256": observation["device_attestation_sha256"],
                "ownership_authorization_sha256": observation["ownership_authorization_sha256"],
                "inventory_class": observation["inventory_class"],
            },
            "oauth_whoami": {
                "client_id_sha256": observation["oauth_client_id_sha256"],
                "credential_lease_id": observation["oauth_credential_lease_id"],
                "token_fingerprint_sha256": observation["oauth_token_fingerprint_sha256"],
                "source_id": observation["oauth_whoami_source_id"],
                "source_alias": observation["oauth_whoami_source_alias"],
                "source_binding_nonce": observation["source_binding_nonce"],
                "source_binding_revision": observation["source_binding_revision"],
                "source_binding_checksum": observation["source_binding_checksum"],
            },
            "readable_source_ids": observation["oauth_read_source_ids"],
            "writable_source_ids": [observation["oauth_write_source_id"]],
            "readable_source_aliases": observation["oauth_read_source_aliases"],
            "writable_source_aliases": [observation["oauth_write_source_alias"]],
            "observed_full_soul_metadata_sha256": observation["observed_full_soul_metadata_sha256"],
            "source_binding_revision": observation["source_binding_revision"],
            "source_binding_checksum": observation["source_binding_checksum"],
        }
        postcondition_payload = {
            "binding_verified": True,
            "expected_full_soul_metadata_sha256": observation["expected_full_soul_metadata_sha256"],
            "observed_full_soul_metadata_sha256": observation["observed_full_soul_metadata_sha256"],
        }
    elif kind == "PERSONA_EXACT_CURRENT_READBACK":
        persona = {
            "schema_version": observation["persona_schema_version"],
            "soul_id": scope["soul_id"],
            "persona_revision": observation["expected_revision"],
            "traits": ["fixture-curious", "fixture-cautious"],
        }
        persona_bytes = canonical_bytes(persona)
        request_payload = {
            "read_mode": observation["read_mode"],
            "fixed_slug": observation["fixed_slug"],
            "semantic_search_invocations": [],
        }
        response_payload = {
            "persona_schema_version": observation["persona_schema_version"],
            "revision": observation["read_revision"],
            "checksum": observation["read_checksum"],
            "persona_base64": base64.b64encode(persona_bytes).decode("ascii"),
        }
        postcondition_payload = {
            "expected_persona_base64": base64.b64encode(persona_bytes).decode("ascii"),
            "read_persona_base64": base64.b64encode(persona_bytes).decode("ascii"),
        }
    elif kind == "DELETE_REBUILD_PURGE":
        request_payload = {
            "delete_request_id": observation["delete_request_id"],
            "rebuild_request_id": observation["rebuild_request_id"],
            "pre_delete_projection_checksum": observation["pre_delete_projection_checksum"],
        }
        response_payload = {
            "delete_observed_revision": observation["delete_observed_revision"],
            "backup_policy_id": observation["backup_policy_id"],
            "remaining": {
                "pages": [],
                "chunks": [],
                "embeddings": [],
                "cache_entries": [],
                "backup_references": [],
            },
        }
        postcondition_payload = {
            "expected_rebuild_revision": observation["expected_rebuild_revision"],
            "readback_rebuild_revision": observation["readback_rebuild_revision"],
            "expected_rebuild_checksum": observation["expected_rebuild_checksum"],
            "readback_rebuild_checksum": observation["readback_rebuild_checksum"],
            "rebuilt_pages": [
                {
                    "external_source_alias": scope["external_source_alias"],
                    "projection_revision": observation["readback_rebuild_revision"],
                    "projection_checksum": observation["readback_rebuild_checksum"],
                }
            ],
        }
    elif kind == "DATA_SUBJECT_EXPORT":
        request_payload = {"target_scope_sha256": observation["expected_scope_sha256"]}
        response_payload = {
            "records": [
                {"record_id": f"f7-export-record-{index:04d}-{record_index}", "scope_sha256": scope_sha256}
                for record_index in range(observation["exported_record_count"])
            ]
        }
        postcondition_payload = {"foreign_scope_records": []}
    elif kind == "DATA_SUBJECT_CORRECTION":
        request_payload = {
            "correction_event_id": observation["correction_event_id"],
            "before_revision": observation["before_revision"],
        }
        response_payload = {
            "live_records": [
                {
                    "scope_sha256": scope_sha256,
                    "revision": observation["observed_after_revision"],
                }
            ]
        }
        postcondition_payload = {"foreign_scope_writes": [], "stale_live_records": []}
    elif kind == "DATA_SUBJECT_DELETION":
        request_payload = {"target_scope_sha256": observation["target_scope_sha256"]}
        response_payload = {
            "remaining": {
                "live_primary": [],
                "pages": [],
                "chunks": [],
                "embeddings": [],
                "cache_entries": [],
                "backup_references": [],
            }
        }
        postcondition_payload = {"foreign_scope_deletes": []}
    elif kind == "FIXTURE_COMMAND_POSTCONDITION":
        side_effect_receipt = {
            "receipt_id": f"f7-side-effect-receipt-{index:04d}",
            "command_id": observation["command_id"],
            "idempotency_key": observation["idempotency_key"],
            "scope_sha256": scope_sha256,
        }
        request_payload = {
            "command_id": observation["command_id"],
            "approval_id": observation["approval_id"],
            "lease_id": observation["lease_id"],
            "idempotency_key": observation["idempotency_key"],
            "platform_authorization_id": observation["platform_authorization_id"],
            "owned_fixture_package_sha256": observation["owned_fixture_package_sha256"],
        }
        response_payload = {
            "native_receipts": [
                {
                    "receipt_id": f"f7-native-receipt-{index:04d}",
                    "command_id": observation["command_id"],
                    "lease_id": observation["lease_id"],
                    "trace_id": observation["trace_id"],
                    "scope_sha256": scope_sha256,
                    "result": observation["native_result"],
                }
            ]
        }
        postcondition_payload = {
            "side_effect_receipts": [side_effect_receipt],
            "duplicate_side_effect_receipts": [],
            "verified_postconditions": [
                {
                    "command_id": observation["command_id"],
                    "scope_sha256": scope_sha256,
                    "result": observation["postcondition_result"],
                    "verified_at": observation["postcondition_verified_at"],
                }
            ],
            "spoken_recorded_at": observation["spoken_recorded_at"],
        }
    elif kind in external_gate_module.F7_ATTACK_ARTIFACT_KINDS:
        source_digest = lambda name: hashlib.sha256(
            f"SOUL_DEVICE_SOURCE_OAUTH_BINDING:{index}:{name}".encode()
        ).hexdigest()
        request_payload = {
            "axis": observation["axis"],
            "actor_scope": scope,
            "target_scope": observation["target_scope"],
            "oauth_credential_lease_id": f"f7-oauth-lease-{index:04d}",
            "oauth_token_fingerprint_sha256": source_digest("oauth-token"),
            "requested_source_alias": observation["target_scope"]["external_source_alias"],
        }
        response_payload = {
            "authorization_decision": observation["authorization_decision"],
            "native_execution_receipts": [],
            "returned_records": [],
            "side_effect_receipts": [],
            "audit_events": [
                {
                    "audit_event_id": f"f7-denial-audit-{index:04d}-{n}",
                    "attack_id": observation["attack_id"],
                    "axis": observation["axis"],
                    "actor_scope_sha256": scope_sha256,
                    "target_scope_sha256": hashlib.sha256(
                        canonical_bytes(observation["target_scope"])
                    ).hexdigest(),
                    "decision": "DENY",
                }
                for n in range(3)
            ],
        }
        postcondition_payload = {
            "actor_scope_unchanged": True,
            "target_scope_unchanged": True,
        }
        response_outcome = "DENIED"
    elif kind == "DUPLICATE_DELIVERY":
        deliveries = [
            {
                "delivery_id": f"f7-delivery-{index:04d}-{n}",
                "ordinal": n + 1,
                "command_id": observation["command_id"],
                "idempotency_key": observation["idempotency_key"],
                "scope_sha256": scope_sha256,
            }
            for n in range(observation["delivery_count"])
        ]
        request_payload = {
            "command_id": observation["command_id"],
            "idempotency_key": observation["idempotency_key"],
            "deliveries": deliveries,
        }
        response_payload = {
            "native_execution_receipts": [
                {
                    "receipt_id": f"f7-native-receipt-{index:04d}",
                    "command_id": observation["command_id"],
                    "idempotency_key": observation["idempotency_key"],
                    "scope_sha256": scope_sha256,
                    "execution_ordinal": 1,
                }
            ],
            "distinct_results": [
                {
                    "result_id": f"f7-result-{index:04d}",
                    "command_id": observation["command_id"],
                    "scope_sha256": scope_sha256,
                }
            ],
        }
        postcondition_payload = {
            "side_effect_receipts": [
                {
                    "receipt_id": f"f7-side-effect-receipt-{index:04d}",
                    "command_id": observation["command_id"],
                    "idempotency_key": observation["idempotency_key"],
                    "scope_sha256": scope_sha256,
                }
            ],
            "verified_receipts": [
                {
                    "receipt_id": f"f7-verified-receipt-{index:04d}",
                    "command_id": observation["command_id"],
                    "scope_sha256": scope_sha256,
                }
            ],
            "duplicate_side_effect_receipts": [],
        }
    elif kind == "UNKNOWN_OUTCOME_RECONCILIATION":
        request_payload = {
            "command_id": observation["command_id"],
            "idempotency_key": observation["idempotency_key"],
            "automatic_retries": [],
        }
        response_payload = {
            "native_receipts": [
                {
                    "command_id": observation["command_id"],
                    "idempotency_key": observation["idempotency_key"],
                    "scope_sha256": scope_sha256,
                    "outcome": observation["unknown_outcome_code"],
                    "execution_ordinal": 1,
                }
            ]
        }
        postcondition_payload = {
            "mode": observation["reconciliation_mode"],
            "reads": [
                {
                    "read_id": f"f7-exact-read-{index:04d}-{n}",
                    "ordinal": n + 1,
                    "command_id": observation["command_id"],
                    "idempotency_key": observation["idempotency_key"],
                    "scope_sha256": scope_sha256,
                    "verified_outcome": observation["final_verified_outcome"],
                }
                for n in range(observation["reconciliation_read_count"])
            ],
            "final_verified_outcome": observation["final_verified_outcome"],
            "duplicate_side_effect_receipts": [],
        }
        response_outcome = "UNKNOWN_OUTCOME"
    else:
        raise AssertionError(f"unknown F7 exchange kind {kind}")

    common = {
        "request_id": request_id,
        "artifact_kind": kind,
        "f7_run_id": observation["f7_run_id"],
        "trace_id": observation["trace_id"],
        "scope_sha256": scope_sha256,
    }
    request = {"schema_version": "dps.f7-request-record/v1", **common, "payload": request_payload}
    response = {
        "schema_version": "dps.f7-response-record/v1",
        **common,
        "outcome": response_outcome,
        "payload": response_payload,
    }
    postcondition = {
        "schema_version": "dps.f7-postcondition-record/v1",
        **common,
        "outcome": postcondition_outcome,
        "payload": postcondition_payload,
    }
    result = {}
    for name, document in (("request", request), ("response", response), ("postcondition", postcondition)):
        raw = canonical_bytes(document)
        result[f"{name}_sha256"] = hashlib.sha256(raw).hexdigest()
        result[f"{name}_base64"] = base64.b64encode(raw).decode("ascii")
    return result


def _f7_test_environment() -> dict:
    return {
        "environment_id": "env_device_gbrain_lab_01",
        "os_family": "Windows+Android",
        "gbrain_deployment_id": "gbrain_live_test_01",
        "parent_windows_environment_id": "env_windows_lab_01",
        "edge_installation_id": "edge_lab_installation_01",
        "zenno_installation_id": "zenno_lab_installation_01",
        "runner_component": "dps-f7-external-runner",
        "runner_version": "1.0.0",
        "runner_binary_sha256": hashlib.sha256(b"synthetic-f7-runner-binary").hexdigest(),
        "runner_sbom_sha256": hashlib.sha256(b"synthetic-f7-runner-sbom").hexdigest(),
    }


def _make_f7_artifact(
    artifact_id: str,
    kind: str,
    scope: dict,
    content: dict,
    started: datetime,
    finished: datetime,
) -> dict:
    return {
        "schema_version": "dps.f7-raw-evidence-artifact/v1",
        "artifact_id": artifact_id,
        "artifact_kind": kind,
        "producer": {
            "identity": "external-device-evidence-issuer",
            "component": "dps-f7-external-runner",
            "version": "1.0.0",
        },
        "environment": _f7_test_environment(),
        "captured_at": {"started_at": iso(started), "finished_at": iso(finished)},
        "scope": scope,
        "content_summary": external_gate_module.F7_ARTIFACT_SUMMARIES[kind],
        "content_sha256": hashlib.sha256(canonical_bytes(content)).hexdigest(),
        "content": content,
    }


def _store_f7_artifact(reference: dict, artifact: dict, artifact_bindings: dict[str, dict[str, object]]) -> None:
    raw = canonical_bytes(artifact)
    digest = hashlib.sha256(raw).hexdigest()
    reference["raw_artifact_sha256"] = digest
    artifact_bindings[reference["raw_artifact_id"]] = {
        "sha256": digest,
        "media_type": "application/json",
        "bytes": raw,
    }


def _synthetic_source_binding(scope: dict, nonce: int, allocated_at: str) -> tuple[dict, bytes]:
    binding = {
        "schema_version": "1.0.0",
        "contract_id": "gbrain.source.binding/v1",
        "producer_module": "gbrain-projector",
        "soul_id": scope["soul_id"],
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": None,
        "idempotency_key": None,
        "occurred_at": allocated_at,
        "privacy_class": "personal",
        "source_id": scope["logical_source_id"],
        "algorithm": "dps.gbrain-source-binding.sha256-nonce/v1",
        "nonce": nonce,
        "soul_hash": scope["soul_id"][5:],
        "allocated_at": allocated_at,
        "binding_revision": "0" * 64,
        "binding_checksum": "0" * 64,
    }
    binding["binding_revision"] = hashlib.sha256(
        external_gate_module._gbrain_source_binding_canonical_bytes(
            binding,
            include_revision=False,
            include_checksum=False,
        )
    ).hexdigest()
    binding["binding_checksum"] = hashlib.sha256(
        external_gate_module._gbrain_source_binding_canonical_bytes(
            binding,
            include_revision=True,
            include_checksum=False,
        )
    ).hexdigest()
    raw = external_gate_module._gbrain_source_binding_canonical_bytes(
        binding,
        include_revision=True,
        include_checksum=True,
    )
    return binding, raw


def refresh_f7_projection_artifact(
    payload: dict,
    artifact_bindings: dict[str, dict[str, object]],
    index: int,
    measurement_started: datetime | None = None,
    measurement_finished: datetime | None = None,
) -> None:
    reference = payload["projection_checks"][index]
    scope = _f7_scope(payload, reference["soul_id"])
    started, finished = _f7_phase_window(payload, external_gate_module.F7_PROJECTION_KIND)
    existing = artifact_bindings.get(reference["raw_artifact_id"])
    if existing is None:
        external_revision = None
    else:
        previous = json.loads(existing["bytes"])
        external_revision = previous["content"].get("external_revision")
    projection_revision = hashlib.sha256(f"projection-revision-{index + 1}".encode()).hexdigest()
    source_mapping = next(
        item for item in payload["source_mappings"] if item["soul_id"] == scope["soul_id"]
    )
    source_binding, source_binding_bytes = _synthetic_source_binding(
        scope,
        source_mapping["source_binding_nonce"],
        "2026-07-01T00:00:00Z",
    )
    projection_dto = {
        "schema_version": "2.0.0",
        "contract_id": "gbrain.projection/v2",
        "producer_module": "gbrain-projector",
        "soul_id": scope["soul_id"],
        "device_binding_id": scope["device_binding_id"],
        "platform_account_id": scope["platform_account_id"],
        "trace_id": payload["trace_id"],
        "idempotency_key": "idem_" + hashlib.sha256(f"idempotency-f7-{index + 1}".encode()).hexdigest(),
        "occurred_at": "2026-07-01T00:00:00Z",
        "privacy_class": "personal",
        "source_id": scope["logical_source_id"],
        "source_binding_algorithm": source_binding["algorithm"],
        "source_binding_nonce": source_binding["nonce"],
        "source_binding_soul_hash": source_binding["soul_hash"],
        "source_binding_allocated_at": source_binding["allocated_at"],
        "source_binding_revision": source_binding["binding_revision"],
        "source_binding_checksum": source_binding["binding_checksum"],
        "projection_revision": projection_revision,
        "projection_checksum": "0" * 64,
        "render_status": "dto-rendered-not-written",
        "source_event_count": 0,
        "events": [],
        "interests": [],
    }
    projection_dto["projection_checksum"] = hashlib.sha256(
        external_gate_module._gbrain_projection_canonical_bytes(projection_dto, include_checksum=False)
    ).hexdigest()
    projection_bytes = external_gate_module._gbrain_projection_canonical_bytes(projection_dto, include_checksum=True)
    checksum = hashlib.sha256(projection_bytes).hexdigest()
    content = {
        "projection_revision": projection_revision,
        "written_checksum": checksum,
        "read_checksum": checksum,
        "source_binding_sha256": hashlib.sha256(source_binding_bytes).hexdigest(),
        "source_binding_base64": base64.b64encode(source_binding_bytes).decode("ascii"),
        "written_projection_base64": base64.b64encode(projection_bytes).decode("ascii"),
        "readback_projection_base64": base64.b64encode(projection_bytes).decode("ascii"),
    }
    content.update(
        _f7_observation_common(
            payload,
            scope,
            external_gate_module.F7_PROJECTION_KIND,
            finished,
        )
    )
    if external_revision is not None:
        content["external_revision"] = external_revision
    artifact = _make_f7_artifact(reference["raw_artifact_id"], external_gate_module.F7_PROJECTION_KIND, scope, content, started, finished)
    _store_f7_artifact(reference, artifact, artifact_bindings)


def refresh_f7_search_artifact(
    payload: dict,
    artifact_bindings: dict[str, dict[str, object]],
    index: int,
    measurement_started: datetime | None = None,
    measurement_finished: datetime | None = None,
) -> None:
    reference = payload["search_readback_checks"][index]
    scope = _f7_scope(payload, reference["soul_id"])
    projection_artifact = json.loads(
        artifact_bindings[payload["projection_checks"][index]["raw_artifact_id"]]["bytes"]
    )
    projection_dto = json.loads(base64.b64decode(projection_artifact["content"]["readback_projection_base64"]))
    started, finished = _f7_phase_window(payload, external_gate_module.F7_SEARCH_KIND)
    existing = artifact_bindings.get(reference["raw_artifact_id"])
    if existing is None:
        observed_at = finished - timedelta(seconds=60)
    else:
        previous = json.loads(existing["bytes"])
        observed_at = datetime.fromisoformat(previous["content"]["observed_at"].replace("Z", "+00:00"))
    query = {
        "schema_version": "dps.gbrain-source-search-query/v1",
        "soul_id": scope["soul_id"],
        "logical_source_id": scope["logical_source_id"],
        "external_source_alias": scope["external_source_alias"],
        "result_schema_version": "gbrain.search-result/v1",
    }
    response = {
        "schema_version": "gbrain.search-result/v1",
        "soul_id": scope["soul_id"],
        "logical_source_id": scope["logical_source_id"],
        "external_source_alias": scope["external_source_alias"],
        "provenance": "SOURCE_SCOPED_EXTERNAL_READBACK",
        "observed_at": iso(observed_at),
        "results": [
            {
                "soul_id": scope["soul_id"],
                "logical_source_id": scope["logical_source_id"],
                "external_source_alias": scope["external_source_alias"],
                "projection_revision": projection_dto["projection_revision"],
                "projection_checksum": projection_dto["projection_checksum"],
            }
        ],
    }
    query_bytes = canonical_bytes(query)
    response_bytes = canonical_bytes(response)
    content = {
        "result_schema_version": "gbrain.search-result/v1",
        "provenance": "SOURCE_SCOPED_EXTERNAL_READBACK",
        "observed_at": iso(observed_at),
        "freshness_seconds": int(
            (
                datetime.fromisoformat(payload["operation_timeline"][-1]["finished_at"].replace("Z", "+00:00"))
                - observed_at
            ).total_seconds()
        ),
        "query_sha256": hashlib.sha256(query_bytes).hexdigest(),
        "response_sha256": hashlib.sha256(response_bytes).hexdigest(),
        "query_base64": base64.b64encode(query_bytes).decode("ascii"),
        "response_base64": base64.b64encode(response_bytes).decode("ascii"),
        "matched_result_count": 1,
    }
    content.update(
        _f7_observation_common(
            payload,
            scope,
            external_gate_module.F7_SEARCH_KIND,
            observed_at,
        )
    )
    artifact = _make_f7_artifact(reference["raw_artifact_id"], external_gate_module.F7_SEARCH_KIND, scope, content, started, finished)
    _store_f7_artifact(reference, artifact, artifact_bindings)


def _semantic_content(kind: str, index: int, scope: dict, other_scope: dict, finished: datetime) -> dict:
    digest = lambda name: hashlib.sha256(f"{kind}:{index}:{name}".encode()).hexdigest()
    external_id = lambda name: f"f7-{name}-{index:04d}"
    if kind == "SOUL_DEVICE_SOURCE_OAUTH_BINDING":
        metadata = digest("full-soul-metadata")
        return {
            "device_transport": "PHYSICAL_ADB",
            "adb_serial_hmac_sha256": digest("adb-serial"),
            "device_attestation_sha256": digest("device-attestation"),
            "ownership_authorization_sha256": digest("ownership"),
            "inventory_class": "NON_PRODUCTION",
            "oauth_client_id_sha256": digest("oauth-client"),
            "oauth_credential_lease_id": external_id("oauth-lease"),
            "oauth_token_fingerprint_sha256": digest("oauth-token"),
            "expected_full_soul_metadata_sha256": metadata,
            "observed_full_soul_metadata_sha256": metadata,
            "oauth_write_source_id": scope["logical_source_id"],
            "oauth_read_source_ids": [scope["logical_source_id"]],
            "oauth_whoami_source_id": scope["logical_source_id"],
            "oauth_write_source_alias": scope["external_source_alias"],
            "oauth_read_source_aliases": [scope["external_source_alias"]],
            "oauth_whoami_source_alias": scope["external_source_alias"],
            "oauth_readable_source_count": 1,
            "oauth_write_source_count": 1,
            "source_binding_nonce": 0,
            "source_binding_revision": digest("source-binding-revision"),
            "source_binding_checksum": digest("source-binding-checksum"),
        }
    if kind == "PERSONA_EXACT_CURRENT_READBACK":
        revision = digest("persona-revision")
        persona_bytes = canonical_bytes(
            {
                "schema_version": "dps.persona-current/v1",
                "soul_id": scope["soul_id"],
                "persona_revision": revision,
                "traits": ["fixture-curious", "fixture-cautious"],
            }
        )
        checksum = content_digest = hashlib.sha256(persona_bytes).hexdigest()
        return {"read_mode": "EXACT_FIXED_SLUG", "fixed_slug": "persona-current", "semantic_search_invocation_count": 0, "persona_schema_version": "dps.persona-current/v1", "expected_revision": revision, "read_revision": revision, "expected_checksum": checksum, "read_checksum": checksum, "expected_content_sha256": content_digest, "read_content_sha256": content_digest}
    if kind == "DELETE_REBUILD_PURGE":
        rebuild_revision, rebuild_checksum = digest("rebuild-revision"), digest("rebuild-checksum")
        return {"delete_request_id": external_id("delete-request"), "pre_delete_projection_checksum": digest("pre-delete"), "delete_observed_revision": digest("delete-revision"), "page_count_after_delete": 0, "chunk_count_after_delete": 0, "embedding_count_after_delete": 0, "cache_entry_count_after_delete": 0, "backup_reference_count_after_delete": 0, "backup_policy_id": external_id("backup-policy"), "rebuild_request_id": external_id("rebuild-request"), "expected_rebuild_revision": rebuild_revision, "readback_rebuild_revision": rebuild_revision, "expected_rebuild_checksum": rebuild_checksum, "readback_rebuild_checksum": rebuild_checksum, "rebuild_page_count": 1}
    if kind == "DATA_SUBJECT_EXPORT":
        scope_digest = hashlib.sha256(canonical_bytes(scope)).hexdigest()
        return {"request_id": external_id("export-request"), "expected_scope_sha256": scope_digest, "exported_scope_sha256": scope_digest, "exported_record_count": 4, "foreign_scope_record_count": 0}
    if kind == "DATA_SUBJECT_CORRECTION":
        after = digest("after-revision")
        return {"request_id": external_id("correction-request"), "correction_event_id": external_id("correction-event"), "before_revision": digest("before-revision"), "expected_after_revision": after, "observed_after_revision": after, "stale_live_record_count": 0, "foreign_scope_write_count": 0}
    if kind == "DATA_SUBJECT_DELETION":
        scope_digest = hashlib.sha256(canonical_bytes(scope)).hexdigest()
        return {"request_id": external_id("subject-delete"), "target_scope_sha256": scope_digest, "deleted_scope_sha256": scope_digest, "live_primary_count_after": 0, "page_count_after": 0, "chunk_count_after": 0, "embedding_count_after": 0, "cache_count_after": 0, "backup_reference_count_after": 0, "foreign_scope_delete_count": 0}
    if kind == "FIXTURE_COMMAND_POSTCONDITION":
        return {"command_id": external_id("fixture-command"), "trace_id": "trace_" + digest("trace")[:32], "approval_id": external_id("approval"), "lease_id": external_id("lease"), "idempotency_key": "idem_" + digest("idempotency"), "native_result": "SUCCEEDED", "postcondition_result": "VERIFIED", "side_effect_count": 1, "duplicate_side_effect_count": 0, "postcondition_verified_at": iso(finished - timedelta(minutes=2)), "spoken_recorded_at": iso(finished - timedelta(minutes=1)), "owned_fixture_package_sha256": digest("fixture-package"), "platform_authorization_id": external_id("platform-authorization")}
    if kind in external_gate_module.F7_ATTACK_ARTIFACT_KINDS:
        axis = kind.removeprefix("CROSS_").removesuffix("_ATTACK")
        target_scope = dict(scope)
        axis_key = {
            "SOUL": "soul_id",
            "DEVICE": "device_binding_id",
            "ACCOUNT": "platform_account_id",
        }[axis]
        target_scope[axis_key] = other_scope[axis_key]
        return {"attack_id": external_id(f"{axis.lower()}-attack"), "axis": axis, "target_scope": target_scope, "request_count": 1, "authorization_decision": "DENY", "native_execution_count": 0, "returned_record_count": 0, "side_effect_count": 0, "audit_event_count": 3}
    if kind == "DUPLICATE_DELIVERY":
        fixture_digest = lambda name: hashlib.sha256(
            f"FIXTURE_COMMAND_POSTCONDITION:{index}:{name}".encode()
        ).hexdigest()
        return {"command_id": f"f7-fixture-command-{index:04d}", "idempotency_key": "idem_" + fixture_digest("idempotency"), "delivery_count": 3, "native_execution_count": 1, "side_effect_count": 1, "verified_receipt_count": 1, "distinct_result_count": 1, "duplicate_side_effect_count": 0}
    if kind == "UNKNOWN_OUTCOME_RECONCILIATION":
        return {"command_id": external_id("unknown-command"), "idempotency_key": "idem_" + digest("unknown-idempotency"), "unknown_outcome_code": "UNKNOWN_OUTCOME", "automatic_retry_count": 0, "reconciliation_read_count": 2, "reconciliation_mode": "EXACT_POSTCONDITION_READBACK", "final_verified_outcome": "VERIFIED_SUCCEEDED", "native_execution_upper_bound": 1, "duplicate_side_effect_count": 0}
    raise AssertionError(f"unknown synthetic F7 artifact kind {kind}")


def valid_f7_case(
    measurement_started: datetime | None = None,
    measurement_finished: datetime | None = None,
    release_bom_id: str = "release-bom-0001",
    release_bom_sha256: str = "0" * 64,
) -> tuple[dict, dict[str, dict[str, object]]]:
    if measurement_started is None or measurement_finished is None:
        measurement_started, measurement_finished = utc_window(200)
    devices, mappings, projections, searches = [], [], [], []
    for index in (1, 2):
        soul_id = "soul_" + hashlib.sha256(f"fixture-soul-{index}".encode()).hexdigest()
        source_binding_nonce = 0
        logical_source_id = external_gate_module._gbrain_source_for_soul(
            soul_id,
            source_binding_nonce,
        )
        external_alias = "gs_" + hashlib.sha256(("dps-gbrain-external-source/v1\n" + logical_source_id).encode()).hexdigest()[:16]
        devices.append({"soul_id": soul_id, "device_binding_id": "db_" + hashlib.sha256(f"fixture-device-{index}".encode()).hexdigest()[:32], "platform_account_id": "pa_" + hashlib.sha256(f"fixture-account-{index}".encode()).hexdigest()[:32]})
        mappings.append({"soul_id": soul_id, "logical_source_id": logical_source_id, "external_source_alias": external_alias, "source_binding_nonce": source_binding_nonce})
        projections.append({"soul_id": soul_id, "raw_artifact_id": f"f7-projection-artifact-{index:04d}", "raw_artifact_sha256": "0" * 64})
        searches.append({"soul_id": soul_id, "raw_artifact_id": f"f7-search-artifact-{index:04d}", "raw_artifact_sha256": "0" * 64})
    phase_names = ["OBSERVE", "VERIFY", "MEMORY_EVENT", "INTEREST", "GBRAIN_PROJECTION", "EXACT_READBACK", "DELETE_REBUILD"]
    phase_minutes = [10, 20, 10, 10, 20, 46, 4]
    if measurement_finished - measurement_started != timedelta(hours=2):
        raise AssertionError("synthetic F7 fixture requires an exact two-hour measurement window")
    operation_timeline = []
    cursor = measurement_started
    for phase_name, minutes in zip(phase_names, phase_minutes, strict=True):
        phase_finished = cursor + timedelta(minutes=minutes)
        operation_timeline.append(
            {
                "phase": phase_name,
                "started_at": iso(cursor),
                "finished_at": iso(phase_finished),
            }
        )
        cursor = phase_finished
    payload = {
        "repository_id": "repo:dps",
        "f7_run_id": "f7-run-0001",
        "trace_id": "trace_" + hashlib.sha256(b"f7-run-0001").hexdigest()[:32],
        "release_bom_id": release_bom_id,
        "release_bom_sha256": release_bom_sha256,
        "devices": devices,
        "source_mappings": mappings,
        "operation_sequence": phase_names,
        "operation_timeline": operation_timeline,
        "f6_prerequisite": {"receipt_id": "f6-windows-receipt-0001", "raw_artifact_id": "f6-windows-receipt-artifact-0001", "raw_artifact_sha256": "0" * 64},
        "projection_checks": projections,
        "search_readback_checks": searches,
        "semantic_artifacts": [],
    }
    artifact_bindings: dict[str, dict[str, object]] = {}
    for index in range(2):
        refresh_f7_projection_artifact(
            payload,
            artifact_bindings,
            index,
            measurement_started,
            measurement_finished,
        )
    for index in range(2):
        refresh_f7_search_artifact(
            payload,
            artifact_bindings,
            index,
            measurement_started,
            measurement_finished,
        )
    souls = [item["soul_id"] for item in devices]
    for kind in sorted(external_gate_module.F7_PER_SOUL_ARTIFACT_KINDS):
        for index, soul_id in enumerate(souls, start=1):
            artifact_id = f"f7-{kind.lower().replace('_', '-')}-{index:04d}"
            reference = {"artifact_kind": kind, "soul_id": soul_id, "raw_artifact_id": artifact_id, "raw_artifact_sha256": "0" * 64}
            scope = _f7_scope(payload, soul_id)
            other_scope = _f7_scope(payload, souls[1] if soul_id == souls[0] else souls[0])
            started, finished = _f7_phase_window(payload, kind)
            observed_at = started + (finished - started) / 2
            content = _semantic_content(kind, index, scope, other_scope, finished)
            if kind == "SOUL_DEVICE_SOURCE_OAUTH_BINDING":
                projection_artifact = json.loads(
                    artifact_bindings[payload["projection_checks"][index - 1]["raw_artifact_id"]]["bytes"]
                )
                source_binding = json.loads(
                    base64.b64decode(projection_artifact["content"]["source_binding_base64"])
                )
                content["source_binding_nonce"] = source_binding["nonce"]
                content["source_binding_revision"] = source_binding["binding_revision"]
                content["source_binding_checksum"] = source_binding["binding_checksum"]
            if kind == "DELETE_REBUILD_PURGE":
                projection_artifact = json.loads(
                    artifact_bindings[payload["projection_checks"][index - 1]["raw_artifact_id"]]["bytes"]
                )
                projection_dto = json.loads(
                    base64.b64decode(projection_artifact["content"]["readback_projection_base64"])
                )
                content["pre_delete_projection_checksum"] = projection_dto["projection_checksum"]
                content["expected_rebuild_revision"] = projection_dto["projection_revision"]
                content["readback_rebuild_revision"] = projection_dto["projection_revision"]
                content["expected_rebuild_checksum"] = projection_dto["projection_checksum"]
                content["readback_rebuild_checksum"] = projection_dto["projection_checksum"]
            content.update(_f7_observation_common(payload, scope, kind, observed_at))
            content.update(_f7_semantic_exchange(kind, index, scope, content))
            content = _wrap_f7_semantic_observation(kind, content)
            artifact = _make_f7_artifact(artifact_id, kind, scope, content, started, finished)
            _store_f7_artifact(reference, artifact, artifact_bindings)
            payload["semantic_artifacts"].append(reference)
    for kind in sorted(external_gate_module.F7_ATTACK_ARTIFACT_KINDS):
        for index, soul_id in enumerate(souls, start=1):
            artifact_id = f"f7-{kind.lower().replace('_', '-')}-{index:04d}"
            reference = {"artifact_kind": kind, "soul_id": soul_id, "raw_artifact_id": artifact_id, "raw_artifact_sha256": "0" * 64}
            scope = _f7_scope(payload, soul_id)
            other_scope = _f7_scope(payload, souls[1] if soul_id == souls[0] else souls[0])
            started, finished = _f7_phase_window(payload, kind)
            observed_at = started + (finished - started) / 2
            content = _semantic_content(kind, index, scope, other_scope, finished)
            content.update(_f7_observation_common(payload, scope, kind, observed_at))
            content.update(_f7_semantic_exchange(kind, index, scope, content))
            content = _wrap_f7_semantic_observation(kind, content)
            artifact = _make_f7_artifact(artifact_id, kind, scope, content, started, finished)
            _store_f7_artifact(reference, artifact, artifact_bindings)
            payload["semantic_artifacts"].append(reference)
    return payload, artifact_bindings


def valid_f8_payload() -> dict:
    start, _ = utc_window(200)
    names = ["simulator", "shadow", "test_soul", "1", "3", "8", "15", "30"]
    durations = [1, 1, 1, 2, 2, 2, 8, 24]
    counts = [200, 30, 2, 1, 3, 8, 15, 30]
    kinds = ["SIMULATED", "SHADOW", "TEST"] + ["PRODUCTION"] * 5
    waves = []
    cursor = start
    for name, duration, count, kind in zip(names, durations, counts, kinds, strict=True):
        finished = cursor + timedelta(hours=duration)
        waves.append(
            {
                "name": name,
                "device_count": count,
                "environment_kind": kind,
                "started_at": iso(cursor),
                "finished_at": iso(finished),
                "commands": 500 if name in {"1", "3", "8"} else 1,
                "status": "PASS",
                "real_side_effect_count": 0,
            }
        )
        cursor = finished
    zero_keys = (
        "cross_scope_leaks",
        "unauthorized_side_effects",
        "duplicate_side_effects",
        "false_successes",
        "unknown_contract_acceptances",
        "shadow_real_side_effects",
        "zenno_unexpected_restarts",
        "audit_chain_gaps",
    )
    return {
        "waves": waves,
        "parallel_module_count": 2,
        "parallel_modules_independent": True,
        "zero_tolerance": {key: 0 for key in zero_keys},
        "technical_measurements": {
            "max_consecutive_health_check_failures": 0,
            "max_error_rate_delta_percentage_points_over_5m": 0.5,
            "max_error_rate_ratio_over_5m": 1.1,
            "max_p95_latency_ratio_over_10m": 1.1,
            "max_oldest_growing_backlog_seconds": 30,
            "max_gbrain_projection_lag_seconds": 60,
        },
        "rollback_drill": {
            "status": "PASS",
            "duration_minutes": 4,
            "previous_bom_restored": True,
            "event_loss_count": 0,
            "duplicate_side_effect_count": 0,
        },
        "traceable_device_count": 30,
        "queryable_bom_device_count": 30,
    }


def valid_f9_payload() -> dict:
    start, _ = utc_window(200)
    names = ["2", "10", "20", "50", "100", "200"]
    waves = []
    cursor = start
    for name in names:
        finished = cursor + timedelta(hours=1)
        waves.append(
            {"name": name, "device_count": int(name), "started_at": iso(cursor), "finished_at": iso(finished), "status": "PASS"}
        )
        cursor = finished

    def run(run_id: str, kind: str, concurrency: int, begun: datetime, hours: float, marker: str) -> dict:
        finished = begun + timedelta(hours=hours)
        return {
            "run_id": run_id,
            "artifact_id": f"f9-load-{marker}-0001",
            "evidence_kind": kind,
            "concurrency": concurrency,
            "duration_seconds": hours * 3600,
            "started_at": iso(begun),
            "finished_at": iso(finished),
            "status": "PASS",
            "recovered_without_long_term_backlog": True,
            "artifact_sha256": "0" * 64,
        }

    return {
        "waves": waves,
        "managed_devices": 200,
        "load_runs": {
            "sustained": run("sustained-run-0001", "REAL_EXTERNAL", 100, start + timedelta(hours=10), 72, "sustained"),
            "burst": run("burst-run-0001", "REAL_EXTERNAL", 200, start + timedelta(hours=90), 1, "burst"),
            "simulated": run("simulated-run-0001", "SIMULATED", 400, start + timedelta(hours=92), 1, "simulated"),
        },
        "control_plane_instances": 2,
        "postgres_restore": {
            "status": "PASS",
            "declared_rpo_minutes": 10,
            "measured_rpo_minutes": 5,
            "declared_rto_minutes": 30,
            "measured_rto_minutes": 20,
        },
        "gbrain_capacity": {
            "status": "PASS",
            "modeled_sources": 200,
            "oauth_clients": 200,
            "connection_budget": 100,
            "projection_capacity_devices": 200,
        },
        "crash_recovery": {"factory": "PASS", "control_plane": "PASS", "edge_worker": "PASS"},
        "rollback_drills": [
            {"scope": scope, "status": "PASS", "duration_minutes": 4}
            for scope in ("site", "database", "edge", "gbrain", "module")
        ],
        "soak": {
            "status": "PASS",
            "duration_hours": 72,
            "cross_scope_leaks": 0,
            "unauthorized_side_effects": 0,
            "duplicate_side_effects": 0,
            "false_successes": 0,
            "long_term_backlog": False,
        },
        "previous_stable_bom": {"available": True, "artifacts_available": True, "compatible_schema_available": True},
        "legacy_runtime_adapter": {"status": "COMPATIBILITY_ONLY", "remaining_entries_documented": True},
    }


if __name__ == "__main__":
    unittest.main()
