from __future__ import annotations

import copy
import datetime as dt
import importlib.util
import json
import os
import sys
import tempfile
import time
import unittest
from dataclasses import replace
from pathlib import Path
from unittest import mock

MODULE_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = (MODULE_ROOT / "src").resolve(strict=True)
if SOURCE_ROOT.parent != MODULE_ROOT:
    raise RuntimeError("test source root escaped its module")
sys.path.insert(0, str(SOURCE_ROOT))

import fixed_argv_adapter as fixed_adapter_module

from factory_control_plane_host import (
    EVIDENCE_LEVEL_BY_KIND,
    FactoryControlPlaneHost,
    FactoryHostError,
    IdempotencyConflict,
    IllegalTransition,
    InMemoryWorkflowRepository,
    InvalidWorkflowRequest,
    OPERATION_MINIMUM_LEVEL,
    ROLLOUT_TRANSITIONS,
    ReceiptRejected,
    RoleSeparationError,
    SimulationReceiptVerifier,
    StaticRuntimeControlAuthority,
    StaleFence,
    logical_request_sha256,
    sha256,
    utc_now,
)
from fixed_argv_adapter import (
    FixedArgvAdapter, FixedArgvProfile, cwd_tree_sha256,
    fixed_profile_sha256,
)
from postgres_repository import (
    PostgresSchemaMigrator, PostgresWorkflowRepository, discover_migrations,
    intake_replay_claim_key_sha256, intake_replay_guard_from_receipt,
    intake_upgrade_intent_sha256, verify_migration_history,
)
from provider_verifier_fixture import build_test_provider_verifier
from schema_contract_verifier import PROVIDER_SCHEMA_PATHS, SchemaProviderContractVerifier
from native_stop_authority_trust import (
    NativeStopAuthorityTrustAuthority,
    NativeStopAuthorityTrustError,
    NativeStopAuthorityTrustProvider,
    NativeStopTrustClock,
    NativeStopTrustCryptographicVerifier,
    NativeStopTrustEnvelope,
    NativeStopTrustProviderAttestation,
    NativeStopTrustRequest,
    PROVIDER_ATTESTATION_AUDIENCE,
    PROVIDER_ATTESTATION_ISSUER,
    SCHEMA_RELATIVE_PATH,
    VerifiedNativeStopAuthorityTrust,
    compose_native_stop_authority_trust_authority,
    provider_attestation_signing_bytes,
    release_receipt_signing_bytes,
)
from simulation_adapter import (
    CrashAfterProviderSuccessAdapter, DeterministicSimulationAdapter,
    SimulationExternalAuthority,
    SimulationRoleDirectory,
)


PROVIDER_VERIFIER = build_test_provider_verifier(Path(__file__).resolve().parents[3])
REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
RELEASE_SOURCE_PATH = (
    REPOSITORY_ROOT
    / "Modules/factory-release-controller/src/candidate_bom_validator.py"
)
RELEASE_SPEC = importlib.util.spec_from_file_location(
    "host_test_release_candidate_bom_validator", RELEASE_SOURCE_PATH,
)
if RELEASE_SPEC is None or RELEASE_SPEC.loader is None:
    raise RuntimeError("Release contract fixture source could not be loaded")
RELEASE_SUBJECT = importlib.util.module_from_spec(RELEASE_SPEC)
RELEASE_SPEC.loader.exec_module(RELEASE_SUBJECT)


def _canonical_runtime_utc(value):
    current = value.astimezone(dt.timezone.utc)
    return current.strftime("%Y-%m-%dT%H:%M:%S.") + "%06d0Z" % current.microsecond


class TestNativeStopTrustVerifier(NativeStopTrustCryptographicVerifier):
    def verify(self, *, purpose, algorithm, key_id, signing_bytes, signature):
        return (
            purpose in {
                "release-native-stop-trust-receipt",
                "native-stop-trust-provider-attestation",
            }
            and algorithm == "rsa-pss-sha256"
            and key_id in {"release-trust-key-001", "provider-trust-key-001"}
            and signature == sha256(signing_bytes)
        )


class TestNativeStopTrustClock(NativeStopTrustClock):
    def __init__(self, source):
        self.source = source

    def now(self):
        return self.source()


class TestNativeStopTrustProvider(NativeStopAuthorityTrustProvider):
    def __init__(self, *, available=True, mutate=None, now=None):
        self.available = available
        self.mutate = mutate
        self.now = now or (lambda: dt.datetime.now(dt.timezone.utc))

    @property
    def provider_identity(self):
        return "deployed-native-stop-trust-test-provider"

    def fetch(self, request_value):
        if not self.available:
            return None
        generation = 1
        activation = "a" * 64
        native = {
            "authority_id": "native-stop-authority-host-test",
            "producer_module": "windows-edge-worker",
            "worker_module_id": "windows-edge-worker",
            "worker_artifact_id": "dps.windows-edge-worker",
            "worker_artifact_sha256": "1" * 64,
            "worker_version": "1.0.0",
            "worker_slot": "A",
            "worker_instance_id": "wi_" + "1" * 32,
            "worker_generation": 1,
            "key_id": "worker-native-stop-key-host-test",
            "p256_spki_sha256": "2" * 64,
            "signature_algorithm": "ECDSA_P256_SHA256",
            "signature_format": "IEEE_P1363_FIXED_FIELD",
            "auth_scope": "policy-approval:native-stop-proof:v2:commit-unknown",
            "native_stop_contract_id": "native.stop.proof/v2",
            "policy_id": "RESULT-VERIFY-001",
            "release_bom_generation": generation,
            "activation_token_sha256": activation,
            "rotation_epoch": 1,
            "valid_from": "2026-01-01T00:00:00.0000000Z",
            "valid_until": "2099-01-01T00:00:00.0000000Z",
            "revoked": False,
            "worker_authority_sha256": "0" * 64,
        }
        native["worker_authority_sha256"] = RELEASE_SUBJECT._canonical_authority_hash(native)
        route_spki = "3" * 64
        route = {
            "route_authority_id": "device-route-authority-host-test",
            "producer_module": "factory-release-controller",
            "supervisor_module_id": "windows-edge-supervisor",
            "supervisor_artifact_id": "dps.windows-edge-supervisor",
            "supervisor_artifact_sha256": "4" * 64,
            "supervisor_version": "1.0.0",
            "supervisor_instance_id": "si_" + "2" * 32,
            "supervisor_generation": 1,
            "route_signer_key_id": "p256_spki_" + route_spki,
            "route_signer_p256_spki_sha256": route_spki,
            "signature_algorithm": "ECDSA_P256_SHA256",
            "signature_format": "IEEE_P1363_FIXED_FIELD_LOW_S",
            "auth_scope": "windows-edge-supervisor:device-route-assignment:issue",
            "policy_id": "SOUL-ISO-001",
            "release_bom_generation": generation,
            "activation_token_sha256": activation,
            "rotation_epoch": 1,
            "valid_from": "2026-01-01T00:00:00.0000000Z",
            "valid_until": "2099-01-01T00:00:00.0000000Z",
            "revoked": False,
            "route_authority_sha256": "0" * 64,
        }
        route["route_authority_sha256"] = RELEASE_SUBJECT._canonical_route_authority_hash(route)
        challenge = {
            "authority_id": "native-stop-challenge-authority-host-test",
            "producer_module": "policy-approval",
            "policy_module_id": "policy-approval",
            "policy_artifact_id": "dps.policy-approval",
            "policy_artifact_sha256": "5" * 64,
            "policy_version": "1.0.0",
            "policy_instance_id": "pi_" + "3" * 32,
            "policy_generation": 1,
            "key_id": "policy-challenge-key-host-test",
            "p256_spki_sha256": "6" * 64,
            "signature_algorithm": "ECDSA_P256_SHA256",
            "signature_format": "IEEE_P1363_FIXED_FIELD_LOW_S",
            "auth_scope": "policy-approval:native-stop-challenge:v1:issue",
            "native_stop_challenge_contract_id": "native.stop.challenge/v1",
            "policy_id": "NATIVE-STOP-CHALLENGE-001",
            "release_bom_generation": generation,
            "activation_token_sha256": activation,
            "rotation_epoch": 1,
            "valid_from": "2026-01-01T00:00:00.0000000Z",
            "valid_until": "2099-01-01T00:00:00.0000000Z",
            "revoked": False,
            "challenge_authority_sha256": "0" * 64,
        }
        challenge["challenge_authority_sha256"] = RELEASE_SUBJECT._canonical_challenge_authority_hash(challenge)
        native_hash = RELEASE_SUBJECT._canonical_authorities_hash([native])
        route_hash = RELEASE_SUBJECT._canonical_route_authorities_hash([route])
        challenge_hash = RELEASE_SUBJECT._canonical_challenge_authorities_hash([challenge])
        receipt_id = "native-stop-trust-" + sha256(request_value.workflow_id)[:32]
        payload = {
            "schema_version": "1.0.0",
            "contract_id": "release.bom.native.stop.authority.trust/v1",
            "producer_module": "factory-release-controller",
            "soul_id": None,
            "device_binding_id": None,
            "platform_account_id": None,
            "trace_id": "trace_" + "8" * 32,
            "idempotency_key": "",
            "occurred_at": "2026-07-14T00:00:00.0000001Z",
            "privacy_class": "internal",
            "receipt_id": receipt_id,
            "release_bom_id": "release-bom:" + sha256(request_value.workflow_id)[:32],
            "release_bom_sha256": request_value.release_bom_sha256,
            "integration_commit": "b" * 40,
            "release_bom_generation": generation,
            "activation_token_sha256": activation,
            "trust_policy_id": "native-stop-trust-policy-host-test",
            "native_stop_authorities_sha256": native_hash,
            "device_route_assignment_authorities_sha256": route_hash,
            "native_stop_challenge_authorities_sha256": challenge_hash,
            "authority_sets_sha256": RELEASE_SUBJECT._canonical_authority_sets_hash(
                native_hash, route_hash, challenge_hash,
            ),
            "native_stop_authorities": [native],
            "device_route_assignment_authorities": [route],
            "native_stop_challenge_authorities": [challenge],
        }
        payload["idempotency_key"] = "idem_" + sha256({
            "contract_id": payload["contract_id"],
            "receipt_id": payload["receipt_id"],
            "release_bom_sha256": payload["release_bom_sha256"],
        })
        receipt = dict(payload)
        receipt["signature"] = {
            "algorithm": "rsa-pss-sha256",
            "key_id": "release-trust-key-001",
            "value": sha256(RELEASE_SUBJECT.native_stop_trust_signing_bytes(payload)),
        }
        if self.mutate is not None:
            self.mutate(receipt)
        raw = json.dumps(
            receipt, ensure_ascii=False, allow_nan=False,
            separators=(",", ":"), sort_keys=True,
        ).encode("utf-8")
        now = self.now().astimezone(dt.timezone.utc)
        unsigned_attestation = NativeStopTrustProviderAttestation(
            attestation_id="native-stop-attestation-" + sha256({
                "workflow": request_value.workflow_id,
                "issued": _canonical_runtime_utc(now),
            })[:32],
            provider_identity=self.provider_identity,
            issuer=PROVIDER_ATTESTATION_ISSUER,
            audience=PROVIDER_ATTESTATION_AUDIENCE,
            workflow_id=request_value.workflow_id,
            request_sha256=request_value.request_sha256,
            external_context_ref=request_value.external_context_ref,
            receipt_id=receipt["receipt_id"],
            receipt_sha256=sha256(raw),
            release_bom_sha256=receipt["release_bom_sha256"],
            release_bom_generation=receipt["release_bom_generation"],
            issued_at=_canonical_runtime_utc(now),
            expires_at=_canonical_runtime_utc(now + dt.timedelta(minutes=10)),
            revoked=False,
            nonce="native-stop-nonce-" + sha256(raw)[:32],
            algorithm="rsa-pss-sha256",
            key_id="provider-trust-key-001",
            signature="pending",
        )
        attestation = replace(
            unsigned_attestation,
            signature=sha256(provider_attestation_signing_bytes(unsigned_attestation)),
        )
        return NativeStopTrustEnvelope(raw, attestation)


class StaticNativeStopTrustProvider(NativeStopAuthorityTrustProvider):
    def __init__(self, envelope, identity="deployed-native-stop-trust-test-provider"):
        self.envelope = envelope
        self.identity = identity

    @property
    def provider_identity(self):
        return self.identity

    def fetch(self, request_value):
        return self.envelope


def resign_native_stop_envelope(envelope, mutate, now=None):
    receipt = json.loads(envelope.canonical_receipt_bytes)
    mutate(receipt)
    payload = {key: value for key, value in receipt.items() if key != "signature"}
    receipt["signature"] = {
        "algorithm": "rsa-pss-sha256",
        "key_id": "release-trust-key-001",
        "value": sha256(RELEASE_SUBJECT.native_stop_trust_signing_bytes(payload)),
    }
    raw = json.dumps(
        receipt, ensure_ascii=False, allow_nan=False,
        separators=(",", ":"), sort_keys=True,
    ).encode("utf-8")
    issued = (now or dt.datetime.now(dt.timezone.utc)).astimezone(dt.timezone.utc)
    unsigned = replace(
        envelope.provider_attestation,
        attestation_id="native-stop-attestation-" + sha256({
            "receipt": sha256(raw), "issued": _canonical_runtime_utc(issued),
        })[:32],
        receipt_sha256=sha256(raw),
        receipt_id=receipt["receipt_id"],
        release_bom_sha256=receipt["release_bom_sha256"],
        release_bom_generation=receipt["release_bom_generation"],
        issued_at=_canonical_runtime_utc(issued),
        expires_at=_canonical_runtime_utc(issued + dt.timedelta(minutes=10)),
        nonce="native-stop-nonce-" + sha256(raw)[:32],
        signature="pending",
    )
    attestation = replace(
        unsigned, signature=sha256(provider_attestation_signing_bytes(unsigned)),
    )
    return NativeStopTrustEnvelope(raw, attestation)


def build_native_stop_trust_authority(provider=None, clock=None):
    verifier = TestNativeStopTrustVerifier()
    schema_sha = sha256((REPOSITORY_ROOT / SCHEMA_RELATIVE_PATH).read_bytes())
    return compose_native_stop_authority_trust_authority(
        REPOSITORY_ROOT,
        expected_schema_sha256=schema_sha,
        provider=provider or TestNativeStopTrustProvider(),
        release_signature_verifier=verifier,
        provider_attestation_verifier=TestNativeStopTrustVerifier(),
        release_signer_key_ids=("release-trust-key-001",),
        provider_attestation_key_ids=("provider-trust-key-001",),
        clock=clock,
    )


_DEFAULT_NATIVE_TRUST = object()


def request(workflow_id: str = "upgrade:factory-host-unit-0001", **changes):
    value = {
        "schema_version": "1.0.0",
        "contract_id": "factory.workflow.request/v1",
        "producer_module": "factory-control-plane-host",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + "2" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "workflow_id": workflow_id,
        "mode": "SIMULATION",
        "risk_tier": "R1",
        "baseline_commit": "a" * 40,
        "target_modules": ["factory-control-plane-host"],
        "requested_paths": ["Modules/factory-control-plane-host/src/factory_control_plane_host.py"],
        "public_contract_changes": [],
        "external_context_ref": None,
    }
    value.update(changes)
    return value


def host(
    repository=None, adapter=None, roles=None, external=None, control=None,
    verifier=None, native_trust=_DEFAULT_NATIVE_TRUST,
):
    repository = repository or InMemoryWorkflowRepository()
    adapter = adapter or DeterministicSimulationAdapter()
    return (
        FactoryControlPlaneHost(
            repository,
            roles or SimulationRoleDirectory(),
            adapter,
            verifier or SimulationReceiptVerifier(),
            PROVIDER_VERIFIER,
            external or SimulationExternalAuthority(),
            control or StaticRuntimeControlAuthority(),
            native_stop_authority_trust=(
                build_native_stop_trust_authority()
                if native_trust is _DEFAULT_NATIVE_TRUST
                else native_trust
            ),
        ),
        repository,
        adapter,
    )


def resign_trusted_result_payload(payload):
    attestation = dict(payload["runner_attestation"])
    attestation["payload_sha256"] = sha256({
        key: value for key, value in payload.items()
        if key != "runner_attestation"
    })
    payload["runner_attestation"] = attestation


def resign_simulation_receipt(receipt):
    for output in receipt["outputs"]:
        output["payload_sha256"] = sha256(output["payload"])
    receipt["attestation"]["payload_sha256"] = sha256({
        key: value for key, value in receipt.items()
        if key != "attestation"
    })


class OverlappingRoleDirectory(SimulationRoleDirectory):
    def resolve(self, workflow_id, request_sha256):
        value = dict(super().resolve(workflow_id, request_sha256))
        value["roles"] = dict(value["roles"])
        value["roles"]["module-implementer"] = value["roles"]["evidence-auditor"]
        return value


class SyntheticProductionReceiptVerifier:
    def verify(self, receipt, command):
        return (
            command.get("mode") == "PRODUCTION"
            and receipt.get("mode") == "PRODUCTION"
            and receipt.get("simulation_only") is False
            and EVIDENCE_LEVEL_BY_KIND.get(receipt.get("evidence_kind")) == receipt.get("verification_level")
            and receipt.get("side_effect_count") == 0
            and receipt.get("attestation", {}).get("kind") == "EXTERNAL_VERIFIED"
        )


class SyntheticProductionAdapter:
    """No-side-effect test double for approval-path tests; never release evidence."""

    def __init__(self):
        self.inner = DeterministicSimulationAdapter()

    def invoke(self, command):
        simulated_command = copy.deepcopy(dict(command))
        simulated_command["mode"] = "SIMULATION"
        simulated_command["logical_request_sha256"] = logical_request_sha256(simulated_command)
        receipt = copy.deepcopy(dict(self.inner.invoke(simulated_command)))
        level = OPERATION_MINIMUM_LEVEL[command["operation"]]
        kind = next(
            candidate for candidate, candidate_level in EVIDENCE_LEVEL_BY_KIND.items()
            if candidate != "SIMULATION" and candidate_level == level
        )
        receipt["mode"] = "PRODUCTION"
        receipt["simulation_only"] = False
        receipt["evidence_kind"] = kind
        receipt["verification_level"] = level
        receipt["logical_request_sha256"] = command["logical_request_sha256"]
        for output in receipt["outputs"]:
            if output["contract_id"] == "rollout.event/v1":
                output["payload"]["simulation_only"] = False
                output["payload"]["evidence_kind"] = kind
                output["payload"]["verification_level"] = level
            if output["contract_id"] == "trusted.test.result/v1":
                output["payload"]["evidence_level"] = level
                resign_trusted_result_payload(output["payload"])
            output["payload_sha256"] = sha256(output["payload"])
        receipt["attestation"] = {
            "kind": "EXTERNAL_VERIFIED",
            "verifier_identity": "synthetic-production-test-verifier",
            "payload_sha256": sha256({key: value for key, value in receipt.items() if key != "attestation"}),
            "reference": "test-only:synthetic-production-receipt",
        }
        return receipt


class CrossStageTamperingAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()

    def invoke(self, command):
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if command["operation"] == "bind-instructions":
            output = receipt["outputs"][0]
            output["payload"]["intent_id"] = "intent:" + "f" * 32
            output["payload_sha256"] = sha256(output["payload"])
            receipt["attestation"]["payload_sha256"] = sha256(
                {key: value for key, value in receipt.items() if key != "attestation"}
            )
        return receipt


class RollbackTargetTamperingAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()

    def invoke(self, command):
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if command["operation"] == "prepare-rollback":
            output = receipt["outputs"][0]
            output["payload"]["target_bom_sha256"] = "f" * 64
            output["payload_sha256"] = sha256(output["payload"])
            receipt["attestation"]["payload_sha256"] = sha256(
                {key: value for key, value in receipt.items() if key != "attestation"}
            )
        return receipt


class RollbackAuthorizationTamperingAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()

    def invoke(self, command):
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if command["operation"] == "execute-rollback":
            output = receipt["outputs"][0]
            output["payload"]["authorization_id"] = "attacker:unbound-authorization"
            output["payload_sha256"] = sha256(output["payload"])
            receipt["attestation"]["payload_sha256"] = sha256(
                {key: value for key, value in receipt.items() if key != "attestation"}
            )
        return receipt


class ProviderSchemaTamperingAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()

    def invoke(self, command):
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if receipt["outputs"]:
            output = receipt["outputs"][0]
            output["payload"]["unexpected_provider_field"] = "must-fail-closed"
            output["payload_sha256"] = sha256(output["payload"])
            receipt["attestation"]["payload_sha256"] = sha256(
                {key: value for key, value in receipt.items() if key != "attestation"}
            )
        return receipt


class StaticOnlyProductionAdapter(SyntheticProductionAdapter):
    """Adversarial test double that tries to advance with static evidence only."""

    def invoke(self, command):
        receipt = copy.deepcopy(dict(super().invoke(command)))
        receipt["evidence_kind"] = "REPOSITORY"
        receipt["verification_level"] = "REPOSITORY_STATIC_VERIFIED"
        for output in receipt["outputs"]:
            if output["contract_id"] == "rollout.event/v1":
                output["payload"]["evidence_kind"] = "REPOSITORY"
                output["payload"]["verification_level"] = "REPOSITORY_STATIC_VERIFIED"
            if output["contract_id"] == "trusted.test.result/v1":
                output["payload"]["evidence_level"] = "REPOSITORY_STATIC_VERIFIED"
                resign_trusted_result_payload(output["payload"])
            output["payload_sha256"] = sha256(output["payload"])
        unsigned = {key: value for key, value in receipt.items() if key != "attestation"}
        receipt["attestation"]["payload_sha256"] = sha256(unsigned)
        return receipt


class ArmKillSwitchAfterProviderAdapter:
    def __init__(self, control):
        self.control = control
        self.inner = DeterministicSimulationAdapter()

    def invoke(self, command):
        receipt = self.inner.invoke(command)
        self.control.kill_switch_armed = True
        return receipt


class ArmAfterSuccessfulAllowsControl(StaticRuntimeControlAuthority):
    """Reproduce a stale allow immediately before a guarded repository write."""

    def __init__(self):
        super().__init__()
        self.arm_after_next_allow = False

    def allows(self, operation, workflow_id):
        allowed = super().allows(operation, workflow_id)
        if allowed and self.arm_after_next_allow:
            self.arm_after_next_allow = False
            self.kill_switch_armed = True
        return allowed


class ArmBeforeReceiptGuardAdapter:
    def __init__(self, control):
        self.control = control
        self.inner = DeterministicSimulationAdapter()

    def invoke(self, command):
        receipt = self.inner.invoke(command)
        self.control.arm_after_next_allow = True
        return receipt


class GuardAuditAuthority(StaticRuntimeControlAuthority):
    def __init__(self):
        super().__init__()
        self.in_guard = False
        self.guarded_writes = 0

    def execute_if_allowed(self, operation, workflow_id, mutation):
        def audited_mutation():
            self.in_guard = True
            self.guarded_writes += 1
            try:
                return mutation()
            finally:
                self.in_guard = False

        return super().execute_if_allowed(operation, workflow_id, audited_mutation)


class GuardEnforcingRepository:
    _MUTATIONS = {
        "register", "acquire_fence", "acquire_fence_if_state", "schedule_phase",
        "record_attempt", "record_receipt", "append_phase_completed", "transition",
        "quarantine", "register_native_stop_authority_trust",
    }

    def __init__(self, inner, control):
        self.inner = inner
        self.control = control

    def __getattr__(self, name):
        value = getattr(self.inner, name)
        if name not in self._MUTATIONS:
            return value

        def guarded(*args, **kwargs):
            if not self.control.in_guard:
                raise AssertionError("repository mutation escaped runtime control guard: " + name)
            return value(*args, **kwargs)

        return guarded


def _approval_nonce(workflow_id, request_sha256, value):
    return "approval-nonce:" + sha256({
        "workflow_id": workflow_id,
        "request_sha256": request_sha256,
        "bom_sha256": value["bom_sha256"],
        "artifact_sha256": value["artifact_sha256"],
        "bom_signature_sha256": value["bom_signature_sha256"],
        "approver_identity": value["approver_identity"],
        "from_state": value["from_state"],
        "to_state": value["to_state"],
        "issued_at": value["issued_at"],
        "expires_at": value["expires_at"],
    })[:32]


class TamperingHumanAuthority(SimulationExternalAuthority):
    def __init__(self, attack):
        super().__init__(production_bom_available=True, production_human_available=True)
        self.attack = attack

    def verify_human_transition(
        self, workflow_id, request_sha256, external_context_ref, risk_tier,
        from_state, to_state, role_identities,
    ):
        value = dict(super().verify_human_transition(
            workflow_id, request_sha256, external_context_ref, risk_tier,
            from_state, to_state, role_identities,
        ))
        if self.attack == "wrong-bom":
            value["bom_sha256"] = "f" * 64
        elif self.attack == "wrong-signature":
            value["bom_signature_sha256"] = "e" * 64
        elif self.attack == "expired":
            issued = dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=20)
            expires = issued + dt.timedelta(minutes=10)
            value["issued_at"] = issued.isoformat().replace("+00:00", "Z")
            value["expires_at"] = expires.isoformat().replace("+00:00", "Z")
        elif self.attack == "role-overlap":
            value["approver_identity"] = role_identities[0]
        value["approval_nonce"] = _approval_nonce(workflow_id, request_sha256, value)
        value["approval_signature_sha256"] = sha256({
            "workflow_id": workflow_id,
            "nonce": value["approval_nonce"],
            "approver": value["approver_identity"],
        })
        return value


class ReplayHumanAuthority(SimulationExternalAuthority):
    def __init__(self):
        super().__init__(production_bom_available=True, production_human_available=True)
        self.cached_approval = None

    def verify_human_transition(
        self, workflow_id, request_sha256, external_context_ref, risk_tier,
        from_state, to_state, role_identities,
    ):
        if self.cached_approval is None:
            self.cached_approval = dict(super().verify_human_transition(
                workflow_id, request_sha256, external_context_ref, risk_tier,
                from_state, to_state, role_identities,
            ))
        return copy.deepcopy(self.cached_approval)


class TamperingRollbackAuthority(SimulationExternalAuthority):
    def __init__(self, attack):
        super().__init__()
        self.attack = attack

    def verify_rollback_authorization(
        self, workflow_id, request_sha256, external_context_ref, mode,
        reason_code, previous_stable_bom_sha256,
    ):
        value = dict(super().verify_rollback_authorization(
            workflow_id, request_sha256, external_context_ref, mode,
            reason_code, previous_stable_bom_sha256,
        ))
        if self.attack == "wrong-request":
            value["request_sha256"] = "f" * 64
        elif self.attack == "wrong-reason":
            value["reason_code"] = "UNRELATED_ROLLBACK_REASON"
        elif self.attack == "wrong-bom":
            value["previous_stable_bom_sha256"] = "e" * 64
        elif self.attack == "role-overlap":
            value["authorizer_identity"] = "sim:release-rollback-controller"
        elif self.attack == "expired":
            verified = dt.datetime.now(dt.timezone.utc) - dt.timedelta(minutes=20)
            value["verified_at"] = verified.isoformat().replace("+00:00", "Z")
            value["expires_at"] = (
                verified + dt.timedelta(minutes=10)
            ).isoformat().replace("+00:00", "Z")
        return value


class MutableClock:
    def __init__(self):
        self.value = dt.datetime.now(dt.timezone.utc)

    def __call__(self):
        return self.value

    def advance(self, delta):
        self.value += delta


class ToggleRollbackAuthority(SimulationExternalAuthority):
    def __init__(self, clock):
        super().__init__(clock=clock)
        self.rollback_available = True

    def verify_rollback_authorization(
        self, workflow_id, request_sha256, external_context_ref, mode,
        reason_code, previous_stable_bom_sha256,
    ):
        if not self.rollback_available:
            return None
        return super().verify_rollback_authorization(
            workflow_id, request_sha256, external_context_ref, mode,
            reason_code, previous_stable_bom_sha256,
        )


class ToggleHumanAuthority(SimulationExternalAuthority):
    def __init__(self, clock):
        super().__init__(
            production_bom_available=True,
            production_human_available=True,
            clock=clock,
        )


class RoleSignerAuthority(SimulationExternalAuthority):
    def __init__(self):
        super().__init__(production_bom_available=True)

    def verify_signed_bom(
        self, workflow_id, request_sha256, external_context_ref, mode,
    ):
        value = dict(super().verify_signed_bom(
            workflow_id, request_sha256, external_context_ref, mode,
        ))
        value["signer_identity"] = "sim:module-implementer"
        return value


class MixedTestedCommitAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()
        self.changed = False

    def invoke(self, command):
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if (
            command["operation"] == "reliability-review"
            and receipt["outputs"]
        ):
            output = receipt["outputs"][0]
            output["payload"]["tested_commit"] = "f" * 40
            resign_trusted_result_payload(output["payload"])
            output["payload_sha256"] = sha256(output["payload"])
            receipt["attestation"]["payload_sha256"] = sha256({
                key: value for key, value in receipt.items()
                if key != "attestation"
            })
            self.changed = True
        return receipt


class TrustedResultTamperingAdapter:
    def __init__(self, operation, attack):
        self.inner = DeterministicSimulationAdapter()
        self.operation = operation
        self.attack = attack
        self.operations = []

    def invoke(self, command):
        self.operations.append(command["operation"])
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if command["operation"] != self.operation:
            return receipt
        payload = receipt["outputs"][0]["payload"]
        if self.attack == "request-id":
            payload["request_id"] = "call:" + "f" * 32
        elif self.attack == "module-id":
            payload["module_id"] = "factory-worktree-manager"
        elif self.attack == "check-id":
            payload["check_id"] = "factory.reliability-review"
        elif self.attack == "suite-id":
            payload["suite_id"] = "factory.reliability-review"
        elif self.attack == "required-checks":
            payload["required_checks_sha256"] = "f" * 64
        elif self.attack == "runner-identity":
            payload["runner_identity"] = "attacker-runner"
        elif self.attack == "attestation-signer":
            payload["runner_attestation"]["signer_identity"] = "attacker-signer"
        elif self.attack == "attestation-digest":
            payload["runner_attestation"]["payload_sha256"] = "f" * 64
            resign_simulation_receipt(receipt)
            return receipt
        elif self.attack == "baseline-commit":
            payload["tested_commit"] = command["context"]["workflow_request"]["baseline_commit"]
        else:
            raise AssertionError("unknown trusted-result attack")
        resign_trusted_result_payload(payload)
        resign_simulation_receipt(receipt)
        return receipt


class DuplicateTrustedResultIdAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()
        self.first_result_id = None
        self.duplicated = False

    def invoke(self, command):
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if command["operation"] in {
            "verify-changeset", "security-privacy-review",
            "reliability-review", "windows-zenno-review",
        }:
            payload = receipt["outputs"][0]["payload"]
            if self.first_result_id is None:
                self.first_result_id = payload["result_id"]
            elif not self.duplicated:
                payload["result_id"] = self.first_result_id
                resign_trusted_result_payload(payload)
                resign_simulation_receipt(receipt)
                self.duplicated = True
        elif command["operation"] == "verify-merge-head" and self.duplicated:
            payload = receipt["outputs"][0]["payload"]
            payload["evidence_ids"] = list(dict.fromkeys(payload["evidence_ids"]))
            resign_simulation_receipt(receipt)
        return receipt


class DuplicateReadyResultIdAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()
        self.first_result_id = None

    def invoke(self, command):
        receipt = copy.deepcopy(dict(self.inner.invoke(command)))
        if command["operation"] == "verify-implementation-ready":
            payload = receipt["outputs"][0]["payload"]
            if self.first_result_id is None:
                self.first_result_id = payload["result_id"]
            else:
                payload["result_id"] = self.first_result_id
                resign_trusted_result_payload(payload)
                resign_simulation_receipt(receipt)
        return receipt


class CrashAfterReadyRecordingAdapter:
    def __init__(self):
        self.inner = DeterministicSimulationAdapter()
        self.operations = []
        self.crashed = False

    def invoke(self, command):
        self.operations.append(command["operation"])
        receipt = self.inner.invoke(command)
        if command["operation"] == "verify-implementation-ready" and not self.crashed:
            self.crashed = True
            raise RuntimeError("SIMULATED_PROCESS_CRASH_AFTER_CHANGESET_READY")
        return receipt


class ExpireLeaseBeforeReadyAdapter:
    def __init__(self, clock):
        self.inner = DeterministicSimulationAdapter()
        self.clock = clock
        self.operations = []
        self.expired = False

    def invoke(self, command):
        self.operations.append(command["operation"])
        if command["operation"] == "verify-implementation-ready" and not self.expired:
            self.expired = True
            self.clock.advance(dt.timedelta(minutes=11))
        return self.inner.invoke(command)


class CrashAfterOperationAdapter:
    def __init__(self, inner, operation):
        self.inner = inner
        self.operation = operation
        self.crashed = False

    def invoke(self, command):
        receipt = self.inner.invoke(command)
        if command["operation"] == self.operation and not self.crashed:
            self.crashed = True
            raise RuntimeError("SIMULATED_PROCESS_CRASH_AFTER_PROVIDER_SUCCESS")
        return receipt


class FactoryControlPlaneHostTests(unittest.TestCase):
    def test_request_resource_boundaries_and_portable_path_collisions_fail_closed(self):
        service, _, _ = host()
        prefix = "Modules/factory-control-plane-host/src/"
        exact_path = prefix + "a" * (512 - len(prefix))
        service.start(request("upgrade:factory-host-path-boundary", requested_paths=[exact_path]))
        with self.assertRaisesRegex(InvalidWorkflowRequest, "resource boundary"):
            service.start(request(
                "upgrade:factory-host-path-over",
                requested_paths=[exact_path + "a"],
            ))
        for name, value in (
            ("module-count", {"target_modules": ["module-%02d" % item for item in range(33)]}),
            ("path-count", {"requested_paths": [prefix + "p%03d.py" % item for item in range(513)]}),
            ("contract-count", {"public_contract_changes": ["factory.c%03d" % item for item in range(129)]}),
        ):
            with self.subTest(name=name), self.assertRaisesRegex(
                InvalidWorkflowRequest, "resource boundary",
            ):
                service.start(request("upgrade:factory-host-" + name, **value))
        large_paths = []
        for index in range(512):
            suffix = "%03d" % index
            large_paths.append(prefix + suffix + "x" * (512 - len(prefix) - len(suffix)))
        with self.assertRaisesRegex(InvalidWorkflowRequest, "canonical byte limit"):
            service.start(request(
                "upgrade:factory-host-request-bytes", requested_paths=large_paths,
            ))

        collision_cases = (
            [prefix + "Case.py", prefix + "case.py"],
            [prefix + "caf\u00e9.py", prefix + "cafe\u0301.py"],
        )
        for index, paths in enumerate(collision_cases):
            with self.subTest(collision=index), self.assertRaises(InvalidWorkflowRequest):
                service.start(request(
                    "upgrade:factory-host-path-collision-%d" % index,
                    requested_paths=paths,
                ))
        for index, unsafe in enumerate((
            prefix + "trailing.", prefix + "trailing ", prefix + "CON.txt",
            prefix + "illegal?.py",
        )):
            with self.subTest(unsafe=unsafe), self.assertRaises(InvalidWorkflowRequest):
                service.start(request(
                    "upgrade:factory-host-portable-path-%d" % index,
                    requested_paths=[unsafe],
                ))

    def test_worktree_plan_and_leases_separate_all_writer_path_classes(self):
        service, repository, _ = host()
        workflow_id = service.start(request(
            "upgrade:factory-host-writer-separation",
            requested_paths=[
                "Modules/factory-control-plane-host/src/domain.py",
                "Modules/factory-control-plane-host/tests/test_domain.py",
                "Modules/factory-control-plane-host/operations/README.md",
                "Modules/factory-control-plane-host/AGENTS.md",
                "Modules/factory-control-plane-host/contracts/provided/factory.sample.v1.schema.json",
            ],
            public_contract_changes=["factory.sample"],
        ))["workflow_id"]
        self.assertEqual(
            "COMPLETED",
            service.run_until_blocked(workflow_id, "writer-separation", maximum_steps=240)["state"],
        )
        outputs = [
            output["payload"]
            for receipt in repository.receipts(workflow_id)
            for output in receipt["outputs"]
        ]
        plan = next(item for item in outputs if item["contract_id"] == "worktree.plan/v1")
        entries = {(item["module_id"], item["writer_identity"]): item for item in plan["entries"]}
        roles = repository.role_binding(workflow_id)["roles"]
        for role in (
            "module-implementer", "independent-test-agent",
            "contract-architect", "reliability-reviewer",
        ):
            self.assertIn(("factory-control-plane-host", roles[role]), entries)
        self.assertEqual(roles["contract-architect"], plan["contract_worktree"]["writer_identity"])
        self.assertEqual(
            ["Modules/factory-control-plane-host/contracts/provided/factory.sample.v1.schema.json"],
            plan["contract_worktree"]["owned_paths"],
        )
        leases = [item for item in outputs if item["contract_id"] == "worktree.lease/v1"]
        self.assertEqual(4, len(leases))
        self.assertEqual(
            {roles[role] for role in (
                "module-implementer", "independent-test-agent",
                "contract-architect", "reliability-reviewer",
            )},
            {item["holder_identity"] for item in leases},
        )
        covered = [key for item in leases for key in item["lock_keys"] if key.startswith("path:")]
        self.assertEqual(len(covered), len(set(covered)))

    def test_worktree_plan_and_lease_provider_success_recover_after_crash(self):
        for operation in ("plan-module-worktrees", "lease-implementation-worktrees"):
            with self.subTest(operation=operation):
                repository = InMemoryWorkflowRepository()
                inner = DeterministicSimulationAdapter()
                crashing = CrashAfterProviderSuccessAdapter(inner, operation)
                service, _, _ = host(repository=repository, adapter=crashing)
                workflow_id = service.start(request(
                    "upgrade:factory-host-crash-" + operation,
                ))["workflow_id"]
                with self.assertRaisesRegex(RuntimeError, "SIMULATED_PROCESS_CRASH"):
                    service.run_until_blocked(workflow_id, "before-crash", maximum_steps=100)
                restarted, _, _ = host(repository=repository, adapter=inner)
                self.assertEqual(
                    "COMPLETED",
                    restarted.run_until_blocked(workflow_id, "after-crash", maximum_steps=240)["state"],
                )
                calls = [item for item in inner.calls if item["operation"] == operation]
                self.assertEqual(2, len(calls))
                self.assertEqual(calls[0]["request_id"], calls[1]["request_id"])
                self.assertEqual(
                    calls[0]["logical_request_sha256"],
                    calls[1]["logical_request_sha256"],
                )
                self.assertGreater(calls[1]["fencing_token"], calls[0]["fencing_token"])

    def test_requested_state_rejects_unrepresentable_waiting_or_stale_receipts(self):
        for status in ("WAITING_EXTERNAL", "STALE"):
            with self.subTest(status=status):
                adapter = DeterministicSimulationAdapter({"validate-intent": status})
                service, repository, _ = host(adapter=adapter)
                workflow_id = service.start(request(
                    "upgrade:factory-host-requested-" + status.lower(),
                ))["workflow_id"]
                with self.assertRaisesRegex(ReceiptRejected, "no legal recovery transition"):
                    service.run_until_blocked(workflow_id, "worker", maximum_steps=20)
                self.assertEqual("REQUESTED", service.status(workflow_id)["state"])
                self.assertEqual([], repository.receipts(workflow_id))
                self.assertEqual(
                    "QUARANTINED",
                    service.run_until_blocked(workflow_id, "quarantine-worker")["state"],
                )
    def test_start_is_idempotent_and_binds_nine_distinct_roles(self):
        service, repository, _ = host()
        first = service.start(request())
        second = service.start(request())
        self.assertEqual(first, second)
        roles = repository.role_binding(first["workflow_id"])["roles"]
        self.assertEqual(9, len(roles))
        self.assertEqual(9, len(set(roles.values())))

    def test_same_workflow_id_with_different_content_is_quarantined(self):
        service, _, _ = host()
        workflow_id = service.start(request())["workflow_id"]
        conflicting = request(risk_tier="R2")
        with self.assertRaises(IdempotencyConflict):
            service.start(conflicting)
        self.assertEqual(
            "QUARANTINED",
            service.run_until_blocked(workflow_id, "quarantine-worker")["state"],
        )

    def test_untrusted_request_cannot_supply_process_or_role_authority(self):
        service, _, _ = host()
        for field, value in (
            ("argv", ["sh", "-c", "true"]),
            ("shell", True),
            ("approval", True),
            ("roles", {"module-implementer": "attacker"}),
        ):
            attacked = request()
            attacked[field] = value
            with self.subTest(field=field), self.assertRaises(InvalidWorkflowRequest):
                service.start(attacked)

    def test_r4_and_path_escape_are_rejected(self):
        service, _, _ = host()
        with self.assertRaises(InvalidWorkflowRequest):
            service.start(request(risk_tier="R4"))
        with self.assertRaises(InvalidWorkflowRequest):
            service.start(request(requested_paths=["Modules/factory-control-plane-host/../AGENTS.md"]))

    def test_role_overlap_fails_before_workflow_registration(self):
        service, _, _ = host(roles=OverlappingRoleDirectory())
        with self.assertRaises(RoleSeparationError):
            service.start(request())

    def test_old_fence_cannot_schedule_or_append(self):
        service, repository, _ = host()
        workflow_id = service.start(request())["workflow_id"]
        old = repository.acquire_fence(workflow_id, "worker-old", utc_now())
        current = repository.acquire_fence(workflow_id, "worker-current", utc_now())
        self.assertGreater(current, old)
        with self.assertRaises(StaleFence):
            repository.transition(
                workflow_id, "FAILED", "STATE_TRANSITIONED", {"reason": "OLD_WORKER"},
                "old-worker-transition", old, utc_now(),
            )

    def test_identical_receipt_is_noop_and_conflicting_receipt_fails(self):
        service, repository, adapter = host()
        workflow_id = service.start(request())["workflow_id"]
        fence = repository.acquire_fence(workflow_id, "worker", utc_now())
        self.assertTrue(service._tick(workflow_id, fence))
        message = repository.pending_messages(workflow_id)[0]
        command = service._command(repository.request(workflow_id), message, fence)
        receipt = service._validate_receipt(adapter.invoke(command), command)
        self.assertTrue(repository.record_receipt(workflow_id, message["request_id"], receipt, fence, utc_now()))
        self.assertFalse(repository.record_receipt(workflow_id, message["request_id"], receipt, fence, utc_now()))
        conflicting = copy.deepcopy(receipt)
        conflicting["occurred_at"] = "2026-07-14T00:00:01Z"
        unsigned = {key: value for key, value in conflicting.items() if key != "attestation"}
        conflicting["attestation"]["payload_sha256"] = sha256(unsigned)
        with self.assertRaises(IdempotencyConflict):
            repository.record_receipt(workflow_id, message["request_id"], conflicting, fence, utc_now())
        self.assertEqual(
            "QUARANTINED",
            service.run_until_blocked(workflow_id, "quarantine-worker")["state"],
        )

    def test_stale_instruction_receipt_stops_without_implementation(self):
        adapter = DeterministicSimulationAdapter({"verify-baseline": "STALE"})
        service, _, adapter = host(adapter=adapter)
        workflow_id = service.start(request())["workflow_id"]
        status = service.run_until_blocked(workflow_id, "worker", maximum_steps=80)
        self.assertEqual("STALE", status["state"])
        self.assertFalse(any(call["actor_role"] == "module-implementer" for call in adapter.calls))
        adapter.failures.pop("verify-baseline")
        self.assertEqual(
            "COMPLETED",
            service.rework_stale(workflow_id, "rework-worker")["state"],
        )
        instruction_calls = [
            call for call in adapter.calls
            if call["operation"] in {"bind-instructions", "verify-baseline"}
        ]
        freeze_call = next(call for call in adapter.calls if call["operation"] == "freeze-contract-plan")
        head = freeze_call["context"]["causal_heads"]["instruction.receipt/v1"]
        self.assertEqual(instruction_calls[-1]["stage_id"], head["stage_id"])
        self.assertNotEqual(instruction_calls[0]["stage_id"], head["stage_id"])

    def test_schema_shaped_but_cross_stage_tampered_output_is_rejected(self):
        service, repository, _ = host(adapter=CrossStageTamperingAdapter())
        workflow_id = service.start(request("upgrade:factory-host-tamper-0001"))["workflow_id"]
        with self.assertRaisesRegex(ReceiptRejected, "upgrade intent"):
            service.run_until_blocked(workflow_id, "worker", maximum_steps=40)
        self.assertEqual("SCOPE_RESOLVED", service.status(workflow_id)["state"])
        self.assertEqual(1, len(repository.receipts(workflow_id)))
        self.assertEqual(
            "QUARANTINED",
            service.run_until_blocked(workflow_id, "quarantine-worker")["state"],
        )

    def test_provider_payload_unknown_field_is_rejected_at_runtime(self):
        service, repository, _ = host(adapter=ProviderSchemaTamperingAdapter())
        workflow_id = service.start(request("upgrade:factory-host-provider-schema"))["workflow_id"]
        with self.assertRaisesRegex(ReceiptRejected, "public JSON Schema"):
            service.run_until_blocked(workflow_id, "worker", maximum_steps=20)
        self.assertTrue(repository.quarantine_records(workflow_id))

    def test_pre_candidate_failure_is_terminal_and_rollout_failure_auto_rolls_back(self):
        failing = DeterministicSimulationAdapter({"freeze-contract-plan": "FAIL"})
        service, _, _ = host(adapter=failing)
        workflow_id = service.start(request("upgrade:factory-host-failed-0001"))["workflow_id"]
        self.assertEqual("FAILED", service.run_until_blocked(workflow_id, "worker", maximum_steps=100)["state"])

        rollout_failure = DeterministicSimulationAdapter({"run-canary": "FAIL"})
        service, _, adapter = host(adapter=rollout_failure)
        workflow_id = service.start(request("upgrade:factory-host-rollback-0001"))["workflow_id"]
        self.assertEqual("ROLLED_BACK", service.run_until_blocked(workflow_id, "worker", maximum_steps=220)["state"])
        self.assertIn("factory-rollback-controller", {call["target_module"] for call in adapter.calls})

    def test_active_workflow_can_be_cancelled_but_terminal_workflow_cannot(self):
        service, _, _ = host()
        workflow_id = service.start(request("upgrade:factory-host-cancel-0001"))["workflow_id"]
        self.assertEqual("CANCELLED", service.cancel(workflow_id, "operator", "USER_CANCELLED")["state"])
        with self.assertRaises(IllegalTransition):
            service.cancel(workflow_id, "operator", "CANCEL_AGAIN")

    def test_implementer_has_no_contract_test_evidence_or_approval_capability(self):
        service, _, adapter = host()
        workflow_id = service.start(request())["workflow_id"]
        self.assertEqual("COMPLETED", service.run_until_blocked(workflow_id, "worker", maximum_steps=200)["state"])
        implementer_calls = [call for call in adapter.calls if call["actor_role"] == "module-implementer"]
        self.assertTrue(implementer_calls)
        for call in implementer_calls:
            self.assertEqual(["src", "migrations"], call["context"]["allowed_path_classes"])
            self.assertNotIn(call["target_module"], {"factory-trusted-runner", "factory-evidence-ledger", "factory-release-controller", "factory-rollback-controller"})
        protected_roles = {
            call["actor_role"]
            for call in adapter.calls
            if call["target_module"] in {"factory-trusted-runner", "factory-evidence-ledger", "factory-release-controller", "factory-rollback-controller"}
        }
        self.assertNotIn("module-implementer", protected_roles)

    def test_rollback_is_illegal_before_signed_bom(self):
        service, repository, _ = host()
        workflow_id = service.start(request())["workflow_id"]
        before = repository.latest_fence(workflow_id)
        with self.assertRaises(IllegalTransition):
            service.request_rollback(workflow_id, "worker", "TOO_EARLY")
        self.assertEqual(before, repository.latest_fence(workflow_id))

    def test_logical_request_is_stable_across_attempt_fences_and_conflicts_fail(self):
        service, repository, adapter = host()
        workflow_id = service.start(request("upgrade:factory-host-logical-request"))["workflow_id"]
        first_fence = repository.acquire_fence(workflow_id, "first", utc_now())
        service._tick(workflow_id, first_fence)
        message = repository.pending_messages(workflow_id)[0]
        first = service._command(repository.request(workflow_id), message, first_fence)
        second_fence = repository.acquire_fence(workflow_id, "second", utc_now())
        second = service._command(repository.request(workflow_id), message, second_fence)
        self.assertNotEqual(sha256(first), sha256(second))
        self.assertEqual(first["logical_request_sha256"], second["logical_request_sha256"])
        adapter.invoke(first)
        adapter.invoke(second)
        attacked = copy.deepcopy(second)
        attacked["operation"] = "bind-instructions"
        attacked["logical_request_sha256"] = logical_request_sha256(attacked)
        with self.assertRaisesRegex(ValueError, "different logical content"):
            adapter.invoke(attacked)

    def test_transition_conflict_is_quarantined_before_more_work(self):
        service, repository, _ = host()
        workflow_id = service.start(request("upgrade:factory-host-transition-conflict"))["workflow_id"]
        fence = repository.acquire_fence(workflow_id, "worker", utc_now())
        repository.transition(
            workflow_id, "FAILED", "STATE_TRANSITIONED", {"reason": "ONE"},
            "same-transition-key", fence, utc_now(),
        )
        with self.assertRaises(IdempotencyConflict):
            repository.transition(
                workflow_id, "FAILED", "STATE_TRANSITIONED", {"reason": "TWO"},
                "same-transition-key", fence, utc_now(),
            )
        self.assertEqual("TRANSITION_IDEMPOTENCY_CONFLICT", repository.quarantine_records(workflow_id)[-1]["reason"])

    def test_kill_switch_blocks_intake_and_new_fences_but_keeps_status_readable(self):
        control = StaticRuntimeControlAuthority()
        service = FactoryControlPlaneHost(
            InMemoryWorkflowRepository(), SimulationRoleDirectory(),
            DeterministicSimulationAdapter(), SimulationReceiptVerifier(),
            PROVIDER_VERIFIER, SimulationExternalAuthority(), control,
        )
        workflow_id = service.start(request("upgrade:factory-host-kill-switch"))["workflow_id"]
        control.kill_switch_armed = True
        self.assertEqual("REQUESTED", service.status(workflow_id)["state"])
        with self.assertRaisesRegex(FactoryHostError, "kill switch"):
            service.run_until_blocked(workflow_id, "blocked-worker")
        with self.assertRaisesRegex(FactoryHostError, "kill switch"):
            service.start(request("upgrade:factory-host-kill-switch-new"))

    def test_kill_switch_interrupts_a_held_fence_before_schedule_and_provider_call(self):
        control = StaticRuntimeControlAuthority()
        adapter = DeterministicSimulationAdapter()
        service, repository, _ = host(adapter=adapter, control=control)
        workflow_id = service.start(request("upgrade:factory-host-held-fence"))["workflow_id"]
        fence = service._acquire_fence(workflow_id, "held-fence-worker")
        control.kill_switch_armed = True
        with self.assertRaisesRegex(FactoryHostError, "kill switch"):
            service._tick(workflow_id, fence)
        self.assertEqual([], adapter.calls)
        self.assertEqual([], repository.pending_messages(workflow_id))

    def test_kill_switch_armed_during_provider_call_prevents_receipt_and_advance(self):
        control = StaticRuntimeControlAuthority()
        adapter = ArmKillSwitchAfterProviderAdapter(control)
        service, repository, _ = host(adapter=adapter, control=control)
        workflow_id = service.start(request("upgrade:factory-host-inflight-kill"))["workflow_id"]
        with self.assertRaisesRegex(FactoryHostError, "kill switch"):
            service.run_until_blocked(workflow_id, "inflight-worker", maximum_steps=10)
        self.assertEqual(1, len(adapter.inner.calls))
        self.assertEqual([], repository.receipts(workflow_id))
        self.assertEqual("REQUESTED", service.status(workflow_id)["state"])

    def test_stale_allow_cannot_cross_atomic_repository_write_guard(self):
        control = ArmAfterSuccessfulAllowsControl()
        adapter = ArmBeforeReceiptGuardAdapter(control)
        service, repository, _ = host(adapter=adapter, control=control)
        workflow_id = service.start(request("upgrade:factory-host-atomic-kill-guard"))["workflow_id"]
        with self.assertRaisesRegex(FactoryHostError, "kill switch"):
            service.run_until_blocked(workflow_id, "atomic-guard-worker", maximum_steps=10)
        self.assertTrue(control.kill_switch_armed)
        self.assertEqual(1, len(adapter.inner.calls))
        self.assertEqual([], repository.receipts(workflow_id))
        self.assertEqual("REQUESTED", service.status(workflow_id)["state"])

    def test_complete_workflow_and_rollback_have_no_unguarded_repository_writes(self):
        control = GuardAuditAuthority()
        inner = InMemoryWorkflowRepository()
        repository = GuardEnforcingRepository(inner, control)
        service, _, _ = host(repository=repository, control=control)
        workflow_id = service.start(request("upgrade:factory-host-guard-audit"))["workflow_id"]
        self.assertEqual(
            "COMPLETED",
            service.run_until_blocked(workflow_id, "guard-worker", maximum_steps=220)["state"],
        )
        self.assertEqual(
            "ROLLED_BACK",
            service.request_rollback(workflow_id, "guard-worker", "GUARD_AUDIT_ROLLBACK")["state"],
        )
        self.assertGreater(control.guarded_writes, 20)

    def test_kill_switch_prevents_external_approval_fact_persistence(self):
        control = StaticRuntimeControlAuthority()
        external = SimulationExternalAuthority()
        service, repository, _ = host(external=external, control=control)
        raw = request("upgrade:factory-host-external-fact-kill")
        workflow_id = service.start(raw)["workflow_id"]
        fence = service._acquire_fence(workflow_id, "external-fact-worker")
        fact = service._validate_signed_bom_fact(
            repository.request(workflow_id),
            external.verify_signed_bom(
                workflow_id, sha256(repository.request(workflow_id)), None,
                "SIMULATION",
            ),
        )
        control.kill_switch_armed = True
        with self.assertRaisesRegex(FactoryHostError, "kill switch"):
            service._bind_external_fact(
                workflow_id, "REQUESTED", "SIGNED_BOM", fact, fence,
            )
        self.assertIsNone(FactoryControlPlaneHost._bound_external_fact(
            repository.events(workflow_id), "SIGNED_BOM",
        ))

    def test_rolling_back_waiting_external_resumes_new_activation(self):
        adapter = DeterministicSimulationAdapter({"run-canary": "FAIL", "execute-rollback": "WAITING_EXTERNAL"})
        service, _, _ = host(adapter=adapter)
        workflow_id = service.start(request("upgrade:factory-host-rollback-wait"))["workflow_id"]
        waiting = service.run_until_blocked(workflow_id, "worker", maximum_steps=220)
        self.assertEqual("WAITING_EXTERNAL", waiting["state"])
        self.assertIn("MODULE_RECEIPT_WAITING_EXTERNAL", waiting["waiting_reason"])
        adapter.failures.pop("execute-rollback")
        self.assertEqual("ROLLED_BACK", service.resume_waiting(workflow_id, "resume-worker")["state"])

    def test_rollback_provider_failures_wait_and_resume_without_terminal_failure(self):
        for operation, failure in (
            ("prepare-rollback", "INFRA_ERROR"),
            ("execute-rollback", "FAIL"),
        ):
            with self.subTest(operation=operation, failure=failure):
                adapter = DeterministicSimulationAdapter({
                    "run-canary": "FAIL", operation: failure,
                })
                service, _, _ = host(adapter=adapter)
                workflow_id = service.start(request(
                    "upgrade:factory-host-rollback-recover-" + operation,
                ))["workflow_id"]
                waiting = service.run_until_blocked(
                    workflow_id, "before-recovery", maximum_steps=240,
                )
                self.assertEqual("WAITING_EXTERNAL", waiting["state"])
                self.assertIn("MODULE_RECEIPT_" + failure, waiting["waiting_reason"])
                adapter.failures.pop(operation)
                recovered = service.resume_waiting(workflow_id, "after-recovery")
                self.assertEqual("ROLLED_BACK", recovered["state"])

    def test_expired_rollback_authorization_reenters_waiting_and_rebinds_after_restart(self):
        clock = MutableClock()
        authority = ToggleRollbackAuthority(clock)
        adapter = DeterministicSimulationAdapter({"execute-rollback": "WAITING_EXTERNAL"})
        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), adapter,
            SimulationReceiptVerifier(), PROVIDER_VERIFIER, authority,
            StaticRuntimeControlAuthority(), clock=clock,
        )
        workflow_id = service.start(request("upgrade:factory-host-expired-rollback-auth"))["workflow_id"]
        service.run_until_blocked(workflow_id, "worker", maximum_steps=220)
        waiting = service.request_rollback(
            workflow_id, "rollback-worker", "EXPIRED_AUTH_RECOVERY_TEST",
        )
        self.assertEqual("WAITING_EXTERNAL", waiting["state"])
        first = FactoryControlPlaneHost._bound_external_fact(
            repository.events(workflow_id), "ROLLBACK_AUTHORIZATION",
        )
        self.assertIsNotNone(first)

        clock.advance(dt.timedelta(minutes=11))
        authority.rollback_available = False
        restarted = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), adapter,
            SimulationReceiptVerifier(), PROVIDER_VERIFIER, authority,
            StaticRuntimeControlAuthority(), clock=clock,
        )
        still_waiting = restarted.resume_waiting(workflow_id, "restart-worker")
        self.assertEqual("WAITING_EXTERNAL", still_waiting["state"])
        authority.rollback_available = True
        adapter.failures.pop("execute-rollback")
        self.assertEqual(
            "ROLLED_BACK",
            restarted.resume_waiting(workflow_id, "restart-worker")["state"],
        )
        facts = [
            event["payload"]["fact"]
            for event in repository.events(workflow_id)
            if event["event_type"] == "EXTERNAL_FACT_BOUND"
            and event["payload"].get("fact_kind") == "ROLLBACK_AUTHORIZATION"
        ]
        self.assertEqual(2, len(facts))
        self.assertNotEqual(facts[0]["fact_id"], facts[1]["fact_id"])

    def test_rollback_target_must_match_previous_stable_signed_bom(self):
        adapter = RollbackTargetTamperingAdapter()
        service, repository, _ = host(adapter=adapter)
        workflow_id = service.start(request("upgrade:factory-host-rollback-target"))["workflow_id"]
        service.run_until_blocked(workflow_id, "worker", maximum_steps=220)
        with self.assertRaisesRegex(ReceiptRejected, "previous stable BOM"):
            service.request_rollback(workflow_id, "rollback-worker", "TARGET_TAMPER_TEST")
        self.assertEqual("ROLLBACK_REQUIRED", service.status(workflow_id)["state"])
        self.assertTrue(repository.quarantine_records(workflow_id))

    def test_rollback_result_must_bind_external_authorization_fact(self):
        adapter = RollbackAuthorizationTamperingAdapter()
        service, repository, _ = host(adapter=adapter)
        workflow_id = service.start(request("upgrade:factory-host-rollback-authorization"))["workflow_id"]
        service.run_until_blocked(workflow_id, "worker", maximum_steps=220)
        with self.assertRaisesRegex(ReceiptRejected, "authorization"):
            service.request_rollback(workflow_id, "rollback-worker", "AUTHORIZATION_TAMPER_TEST")
        self.assertNotEqual("ROLLED_BACK", service.status(workflow_id)["state"])
        self.assertTrue(repository.quarantine_records(workflow_id))

    def test_rollback_authorization_fact_is_exact_bounded_and_role_separated(self):
        for attack in ("wrong-request", "wrong-reason", "wrong-bom", "role-overlap", "expired"):
            with self.subTest(attack=attack):
                service, repository, _ = host(
                    external=TamperingRollbackAuthority(attack),
                )
                workflow_id = service.start(request(
                    "upgrade:factory-host-rollback-auth-" + attack,
                ))["workflow_id"]
                service.run_until_blocked(workflow_id, "worker", maximum_steps=220)
                with self.assertRaises(ReceiptRejected):
                    service.request_rollback(
                        workflow_id, "rollback-worker", "EXACT_AUTHORIZATION_TEST",
                    )
                self.assertEqual("ROLLBACK_REQUIRED", service.status(workflow_id)["state"])
                self.assertIsNone(FactoryControlPlaneHost._bound_external_fact(
                    repository.events(workflow_id), "ROLLBACK_AUTHORIZATION",
                ))

    def test_production_rollback_waits_for_external_authority(self):
        repository = InMemoryWorkflowRepository()
        authority = SimulationExternalAuthority(production_bom_available=True)
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("production-rollback"),
            SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER, authority, StaticRuntimeControlAuthority(),
            native_stop_authority_trust=build_native_stop_trust_authority(),
        )
        raw = request(
            "upgrade:factory-host-production-rollback",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:production-rollback",
        )
        workflow_id = service.start(raw)["workflow_id"]
        self.assertEqual(
            "COMPLETED",
            service.run_until_blocked(workflow_id, "production-worker", maximum_steps=220)["state"],
        )
        waiting = service.request_rollback(
            workflow_id, "rollback-worker", "PRODUCTION_ROLLBACK_TEST",
        )
        self.assertEqual("WAITING_EXTERNAL", waiting["state"])
        self.assertEqual(
            "ROLLBACK_EXTERNAL_AUTHORIZATION_REQUIRED", waiting["waiting_reason"],
        )
        authority.production_rollback_available = True
        self.assertEqual(
            "ROLLED_BACK",
            service.resume_waiting(workflow_id, "rollback-worker")["state"],
        )

    def test_r2_and_r3_production_wait_for_signed_bom_and_distinct_human_canary(self):
        for risk_tier in ("R2", "R3"):
            with self.subTest(risk_tier=risk_tier):
                repository = InMemoryWorkflowRepository()
                adapter = SyntheticProductionAdapter()
                authority = SimulationExternalAuthority(
                    production_bom_available=False,
                    production_human_available=False,
                )
                service = FactoryControlPlaneHost(
                    repository, SimulationRoleDirectory(risk_tier.lower()), adapter,
                    SyntheticProductionReceiptVerifier(), PROVIDER_VERIFIER, authority,
                    StaticRuntimeControlAuthority(),
                    native_stop_authority_trust=build_native_stop_trust_authority(),
                )
                raw = request(
                    "upgrade:factory-host-production-%s" % risk_tier.lower(),
                    mode="PRODUCTION", risk_tier=risk_tier,
                    external_context_ref="release:external-context-0001",
                )
                workflow_id = service.start(raw)["workflow_id"]
                waiting_bom = service.run_until_blocked(workflow_id, "production-worker", maximum_steps=160)
                self.assertEqual("WAITING_EXTERNAL", waiting_bom["state"])
                self.assertEqual("SIGNED_BOM_EXTERNAL_VERIFICATION_REQUIRED", waiting_bom["waiting_reason"])

                authority.production_bom_available = True
                waiting_human = service.resume_waiting(workflow_id, "production-worker")
                self.assertEqual("WAITING_EXTERNAL", waiting_human["state"])
                self.assertIn("HUMAN_PRODUCTION_CANARY_APPROVAL_REQUIRED", waiting_human["waiting_reason"])

                authority.production_human_available = True
                completed = service.resume_waiting(workflow_id, "production-worker")
                self.assertEqual("COMPLETED", completed["state"])
                self.assertTrue(completed["production_authorized"])
                self.assertEqual("CANARY_VERIFIED", completed["verification_ceiling"])

    def test_r3_requires_one_unique_human_fact_per_rollout_transition(self):
        repository = InMemoryWorkflowRepository()
        authority = SimulationExternalAuthority(
            production_bom_available=True,
            production_human_available=True,
        )
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("r3-scoped"),
            SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER, authority, StaticRuntimeControlAuthority(),
            native_stop_authority_trust=build_native_stop_trust_authority(),
        )
        workflow_id = service.start(request(
            "upgrade:factory-host-r3-scoped-approvals",
            mode="PRODUCTION", risk_tier="R3",
            external_context_ref="release:r3-scoped-approvals",
        ))["workflow_id"]
        completed = service.run_until_blocked(
            workflow_id, "production-worker", maximum_steps=300,
        )
        self.assertEqual("COMPLETED", completed["state"])
        approval_events = [
            event for event in repository.events(workflow_id)
            if event["event_type"] == "EXTERNAL_FACT_BOUND"
            and str(event["payload"].get("fact_kind", "")).startswith(
                "HUMAN_TRANSITION_APPROVAL:",
            )
        ]
        self.assertEqual(5, len(approval_events))
        scopes = {
            (
                event["payload"]["fact"]["from_state"],
                event["payload"]["fact"]["to_state"],
            )
            for event in approval_events
        }
        self.assertEqual(set(ROLLOUT_TRANSITIONS.items()), scopes)
        self.assertEqual(
            5,
            len({event["payload"]["fact"]["fact_id"] for event in approval_events}),
        )

    def test_one_human_fact_cannot_authorize_two_r3_transitions(self):
        authority = ReplayHumanAuthority()
        service, _, _ = host(
            adapter=SyntheticProductionAdapter(),
            roles=SimulationRoleDirectory("r3-replay"), external=authority,
            verifier=SyntheticProductionReceiptVerifier(),
        )
        workflow_id = service.start(request(
            "upgrade:factory-host-r3-transition-replay",
            mode="PRODUCTION", risk_tier="R3",
            external_context_ref="release:r3-transition-replay",
        ))["workflow_id"]
        with self.assertRaisesRegex(ReceiptRejected, "exact transition"):
            service.run_until_blocked(workflow_id, "r3-replay-worker", maximum_steps=240)
        self.assertEqual("SHADOW", service.status(workflow_id)["state"])

    def test_expired_human_fact_after_crash_reenters_waiting_and_renews(self):
        clock = MutableClock()
        authority = ToggleHumanAuthority(clock)
        repository = InMemoryWorkflowRepository()
        production = SyntheticProductionAdapter()
        crashing = CrashAfterOperationAdapter(production, "run-canary")
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("approval-expiry"), crashing,
            SyntheticProductionReceiptVerifier(), PROVIDER_VERIFIER, authority,
            StaticRuntimeControlAuthority(), clock=clock,
            native_stop_authority_trust=build_native_stop_trust_authority(
                TestNativeStopTrustProvider(now=clock),
                TestNativeStopTrustClock(clock),
            ),
        )
        workflow_id = service.start(request(
            "upgrade:factory-host-human-expiry",
            mode="PRODUCTION", risk_tier="R2",
            external_context_ref="release:human-expiry",
        ))["workflow_id"]
        with self.assertRaisesRegex(RuntimeError, "SIMULATED_PROCESS_CRASH"):
            service.run_until_blocked(workflow_id, "before-crash", maximum_steps=240)
        self.assertEqual("SHADOW", service.status(workflow_id)["state"])
        clock.advance(dt.timedelta(minutes=11))
        authority.production_human_available = False
        restarted = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("approval-expiry"), production,
            SyntheticProductionReceiptVerifier(), PROVIDER_VERIFIER, authority,
            StaticRuntimeControlAuthority(), clock=clock,
            native_stop_authority_trust=build_native_stop_trust_authority(
                TestNativeStopTrustProvider(now=clock),
                TestNativeStopTrustClock(clock),
            ),
        )
        waiting = restarted.run_until_blocked(
            workflow_id, "after-crash", maximum_steps=40,
        )
        self.assertEqual("WAITING_EXTERNAL", waiting["state"])
        authority.production_human_available = True
        self.assertEqual(
            "COMPLETED",
            restarted.resume_waiting(workflow_id, "after-renewal")["state"],
        )
        approvals = [
            event["payload"]["fact"] for event in repository.events(workflow_id)
            if event["event_type"] == "EXTERNAL_FACT_BOUND"
            and event["payload"].get("fact_kind")
            == "HUMAN_TRANSITION_APPROVAL:SHADOW:CANARY"
        ]
        self.assertEqual(2, len(approvals))
        self.assertNotEqual(approvals[0]["fact_id"], approvals[1]["fact_id"])

    def test_signed_bom_signer_cannot_overlap_any_factory_role(self):
        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), SyntheticProductionAdapter(),
            SyntheticProductionReceiptVerifier(), PROVIDER_VERIFIER,
            RoleSignerAuthority(), StaticRuntimeControlAuthority(),
        )
        workflow_id = service.start(request(
            "upgrade:factory-host-role-signer",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:role-signer",
        ))["workflow_id"]
        with self.assertRaisesRegex(ReceiptRejected, "signer overlaps"):
            service.run_until_blocked(workflow_id, "role-signer-worker", maximum_steps=200)
        self.assertEqual("CANDIDATE_VERIFIED", service.status(workflow_id)["state"])

    def test_mixed_tested_commits_are_rejected_before_merge_approval(self):
        adapter = MixedTestedCommitAdapter()
        service, repository, _ = host(adapter=adapter)
        workflow_id = service.start(request(
            "upgrade:factory-host-mixed-tested-commits",
        ))["workflow_id"]
        with self.assertRaisesRegex(ReceiptRejected, "changeset-ready barrier"):
            service.run_until_blocked(workflow_id, "mixed-commit-worker", maximum_steps=160)
        self.assertTrue(adapter.changed)
        self.assertEqual("IMPLEMENTING", service.status(workflow_id)["state"])
        self.assertTrue(repository.quarantine_records(workflow_id))

    def test_trusted_result_fields_are_bound_to_exact_operation_request_module_and_runner(self):
        attacks = (
            "request-id", "module-id", "check-id", "suite-id",
            "required-checks", "runner-identity", "attestation-signer",
            "attestation-digest",
        )
        for attack in attacks:
            with self.subTest(attack=attack):
                adapter = TrustedResultTamperingAdapter("verify-changeset", attack)
                service, repository, _ = host(adapter=adapter)
                workflow_id = service.start(request(
                    "upgrade:factory-host-exact-result-" + attack,
                ))["workflow_id"]
                with self.assertRaisesRegex(
                    ReceiptRejected, "exact host operation",
                ):
                    service.run_until_blocked(
                        workflow_id, "exact-result-worker", maximum_steps=160,
                    )
                self.assertEqual("IMPLEMENTING", service.status(workflow_id)["state"])
                self.assertTrue(repository.quarantine_records(workflow_id))

    def test_duplicate_trusted_result_ids_are_rejected_before_merge(self):
        adapter = DuplicateTrustedResultIdAdapter()
        service, repository, _ = host(adapter=adapter)
        workflow_id = service.start(request(
            "upgrade:factory-host-duplicate-result-id",
        ))["workflow_id"]
        with self.assertRaisesRegex(
            ReceiptRejected, "exact unique module/check set",
        ):
            service.run_until_blocked(
                workflow_id, "duplicate-result-worker", maximum_steps=180,
            )
        self.assertEqual("IMPLEMENTING", service.status(workflow_id)["state"])
        self.assertTrue(repository.quarantine_records(workflow_id))

    def test_duplicate_cross_module_changeset_ready_ids_fail_the_stage_barrier(self):
        adapter = DuplicateReadyResultIdAdapter()
        service, repository, _ = host(adapter=adapter)
        workflow_id = service.start(request(
            "upgrade:factory-host-duplicate-ready-id",
            target_modules=[
                "factory-control-plane-host", "factory-worktree-manager",
            ],
            requested_paths=[
                "Modules/factory-control-plane-host/src/factory_control_plane_host.py",
                "Modules/factory-worktree-manager/src/worktree_manager.py",
            ],
        ))["workflow_id"]
        with self.assertRaisesRegex(
            ReceiptRejected, "exact unique module/check set",
        ):
            service.run_until_blocked(
                workflow_id, "duplicate-ready-worker", maximum_steps=140,
            )
        self.assertEqual("IMPLEMENTING", service.status(workflow_id)["state"])
        self.assertTrue(repository.quarantine_records(workflow_id))
        self.assertFalse(any(
            event["event_type"] == "PHASE_COMPLETED"
            and event["payload"].get("phase") == "changeset-ready"
            for event in repository.events(workflow_id)
        ))

    def test_changeset_ready_receipt_is_a_crash_safe_waiting_barrier(self):
        adapter = CrashAfterReadyRecordingAdapter()
        service, repository, _ = host(adapter=adapter)
        workflow_id = service.start(request(
            "upgrade:factory-host-ready-crash-barrier",
        ))["workflow_id"]
        with self.assertRaisesRegex(
            RuntimeError, "CRASH_AFTER_CHANGESET_READY",
        ):
            service.run_until_blocked(
                workflow_id, "ready-crash-worker", maximum_steps=80,
            )
        independent_operations = {
            "verify-changeset", "security-privacy-review",
            "reliability-review", "windows-zenno-review",
        }
        self.assertFalse(independent_operations.intersection(adapter.operations))
        self.assertEqual("IMPLEMENTING", service.status(workflow_id)["state"])

        service.run_until_blocked(
            workflow_id, "ready-recovery-worker", maximum_steps=200,
        )
        events = repository.events(workflow_id)
        ready_completed = next(
            index for index, event in enumerate(events)
            if event["event_type"] == "PHASE_COMPLETED"
            and event["payload"].get("phase") == "changeset-ready"
        )
        independent_scheduled = next(
            index for index, event in enumerate(events)
            if event["event_type"] == "STAGE_SCHEDULED"
            and event["payload"].get("phase") == "independent-verification"
        )
        self.assertLess(ready_completed, independent_scheduled)

    def test_invalid_changeset_ready_receipt_stops_before_independent_verification(self):
        adapter = TrustedResultTamperingAdapter(
            "verify-implementation-ready", "baseline-commit",
        )
        service, repository, _ = host(adapter=adapter)
        workflow_id = service.start(request(
            "upgrade:factory-host-invalid-ready-barrier",
        ))["workflow_id"]
        with self.assertRaisesRegex(
            ReceiptRejected, "unchanged baseline",
        ):
            service.run_until_blocked(
                workflow_id, "invalid-ready-worker", maximum_steps=100,
            )
        self.assertFalse({
            "verify-changeset", "security-privacy-review",
            "reliability-review", "windows-zenno-review",
        }.intersection(adapter.operations))
        self.assertEqual("IMPLEMENTING", service.status(workflow_id)["state"])
        self.assertTrue(repository.quarantine_records(workflow_id))

    def test_expired_subject_lease_fails_closed_at_trusted_evidence_use_time(self):
        clock = MutableClock()
        adapter = ExpireLeaseBeforeReadyAdapter(clock)
        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory(), adapter,
            SimulationReceiptVerifier(), PROVIDER_VERIFIER,
            SimulationExternalAuthority(), StaticRuntimeControlAuthority(),
            clock=clock,
        )
        workflow_id = service.start(request(
            "upgrade:factory-host-expired-evidence-lease",
        ))["workflow_id"]
        status = service.run_until_blocked(
            workflow_id, "expired-lease-worker", maximum_steps=120,
        )
        self.assertEqual("STALE", status["state"])
        self.assertFalse(repository.quarantine_records(workflow_id))
        self.assertFalse({
            "verify-changeset", "security-privacy-review",
            "reliability-review", "windows-zenno-review",
        }.intersection(adapter.operations))
        leases = [
            output["payload"]
            for receipt in repository.receipts(workflow_id)
            for output in receipt["outputs"]
            if output["contract_id"] == "worktree.lease/v1"
        ]
        self.assertTrue(leases)
        self.assertTrue(all(
            dt.datetime.fromisoformat(
                lease["expires_at"].replace("Z", "+00:00"),
            ) < clock()
            for lease in leases
        ))
        self.assertFalse(any(
            event["event_type"] == "PHASE_COMPLETED"
            and event["payload"].get("phase") == "changeset-ready"
            for event in repository.events(workflow_id)
        ))
        self.assertTrue(any(
            event["state"] == "STALE"
            and event["payload"].get("reason")
            == "WORKTREE_LEASE_EXPIRED_BEFORE_EVIDENCE_USE"
            and len(event["payload"].get("receipt_sha256", "")) == 64
            for event in repository.events(workflow_id)
        ))

    def test_trusted_results_are_emitted_once_per_role_per_target_module(self):
        service, repository, _ = host()
        workflow_id = service.start(request(
            "upgrade:factory-host-two-module-evidence",
            target_modules=[
                "factory-control-plane-host", "factory-worktree-manager",
            ],
            requested_paths=[
                "Modules/factory-control-plane-host/src/factory_control_plane_host.py",
                "Modules/factory-worktree-manager/src/worktree_manager.py",
            ],
        ))["workflow_id"]
        completed = service.run_until_blocked(
            workflow_id, "two-module-worker", maximum_steps=240,
        )
        self.assertEqual("COMPLETED", completed["state"])
        results = [
            output["payload"]
            for receipt in repository.receipts(workflow_id)
            for output in receipt["outputs"]
            if output["contract_id"] == "trusted.test.result/v1"
        ]
        ready = [
            item for item in results
            if item["check_id"] == "factory.verify-implementation-ready"
        ]
        independent = [
            item for item in results
            if item["check_id"] != "factory.verify-implementation-ready"
        ]
        self.assertEqual(2, len(ready))
        self.assertEqual(8, len(independent))
        self.assertEqual(10, len({item["result_id"] for item in results}))
        self.assertEqual(
            {
                (module_id, check_id)
                for module_id in (
                    "factory-control-plane-host", "factory-worktree-manager",
                )
                for check_id in (
                    "factory.verify-changeset",
                    "factory.security-privacy-review",
                    "factory.reliability-review",
                    "factory.windows-zenno-review",
                )
            },
            {(item["module_id"], item["check_id"]) for item in independent},
        )

    def test_static_receipts_cannot_advance_production_or_create_state_based_evidence(self):
        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("static-attack"),
            StaticOnlyProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER,
            SimulationExternalAuthority(production_bom_available=True),
            StaticRuntimeControlAuthority(),
        )
        raw = request(
            "upgrade:factory-host-static-evidence-attack",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:static-evidence-attack",
        )
        workflow_id = service.start(raw)["workflow_id"]
        with self.assertRaisesRegex(ReceiptRejected, "below the fixed operation minimum"):
            service.run_until_blocked(workflow_id, "static-evidence-worker", maximum_steps=100)
        status = service.status(workflow_id)
        self.assertEqual("IMPLEMENTING", status["state"])
        self.assertEqual("REPOSITORY_STATIC_VERIFIED", status["verification_ceiling"])
        self.assertFalse(status["production_authorized"])
        self.assertTrue(repository.quarantine_records(workflow_id))

    def test_human_canary_approval_is_exact_expiring_and_role_separated(self):
        for attack in ("wrong-bom", "wrong-signature", "expired", "role-overlap"):
            with self.subTest(attack=attack):
                repository = InMemoryWorkflowRepository()
                service = FactoryControlPlaneHost(
                    repository, SimulationRoleDirectory("approval-" + attack),
                    SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
                    PROVIDER_VERIFIER, TamperingHumanAuthority(attack),
                    StaticRuntimeControlAuthority(),
                    native_stop_authority_trust=build_native_stop_trust_authority(),
                )
                raw = request(
                    "upgrade:factory-host-approval-" + attack,
                    mode="PRODUCTION", risk_tier="R2",
                    external_context_ref="release:approval-" + attack,
                )
                workflow_id = service.start(raw)["workflow_id"]
                with self.assertRaises(ReceiptRejected):
                    service.run_until_blocked(workflow_id, "approval-worker", maximum_steps=200)
                self.assertEqual("SHADOW", service.status(workflow_id)["state"])
                self.assertIsNone(FactoryControlPlaneHost._bound_external_fact(
                    repository.events(workflow_id), "HUMAN_CANARY_APPROVAL",
                ))

    def test_human_canary_approval_cannot_be_replayed_to_another_workflow(self):
        authority = ReplayHumanAuthority()
        first, _, _ = host(
            adapter=SyntheticProductionAdapter(),
            roles=SimulationRoleDirectory("replay-a"), external=authority,
            verifier=SyntheticProductionReceiptVerifier(),
        )
        first_raw = request(
            "upgrade:factory-host-approval-replay-a",
            mode="PRODUCTION", risk_tier="R2",
            external_context_ref="release:approval-replay",
        )
        self.assertEqual(
            "COMPLETED",
            first.run_until_blocked(
                first.start(first_raw)["workflow_id"], "replay-worker-a",
                maximum_steps=220,
            )["state"],
        )

        second, _, _ = host(
            adapter=SyntheticProductionAdapter(),
            roles=SimulationRoleDirectory("replay-b"), external=authority,
            verifier=SyntheticProductionReceiptVerifier(),
        )
        second_raw = request(
            "upgrade:factory-host-approval-replay-b",
            mode="PRODUCTION", risk_tier="R2",
            external_context_ref="release:approval-replay",
        )
        second_id = second.start(second_raw)["workflow_id"]
        with self.assertRaisesRegex(ReceiptRejected, "exact request and risk"):
            second.run_until_blocked(second_id, "replay-worker-b", maximum_steps=220)

    def test_provider_schema_set_requires_exact_digests_and_external_signature(self):
        root = Path(__file__).resolve().parents[3]
        expected = {
            contract_id: sha256((root / relative).read_bytes())
            for contract_id, relative in PROVIDER_SCHEMA_PATHS.items()
        }

        def trust_record(digests):
            return {
                "schema_set_sha256": sha256(digests),
                "trust_root_sha256": "a" * 64,
                "signer_identity": "test:schema-trust-signer",
                "signature_sha256": "b" * 64,
                "verified_at": "2026-07-14T00:00:00Z",
            }

        with self.assertRaisesRegex(ValueError, "external trust-root signature"):
            SchemaProviderContractVerifier(
                root, expected_schema_sha256s=expected,
                trust_record=trust_record(expected),
                signature_verifier=lambda _digests, _record: False,
            )
        drifted = dict(expected)
        drifted[next(iter(sorted(drifted)))] = "0" * 64
        with self.assertRaisesRegex(ValueError, "schema digest drift"):
            SchemaProviderContractVerifier(
                root, expected_schema_sha256s=drifted,
                trust_record=trust_record(drifted),
                signature_verifier=lambda _digests, _record: True,
            )

    def test_receipt_numeric_time_and_output_identity_attacks_fail_closed(self):
        service, repository, adapter = host()
        workflow_id = service.start(request(
            "upgrade:factory-host-receipt-strictness",
        ))["workflow_id"]
        fence = repository.acquire_fence(workflow_id, "receipt-worker", utc_now())
        service._tick(workflow_id, fence)
        message = repository.pending_messages(workflow_id)[0]
        command = service._command(repository.request(workflow_id), message, fence)
        baseline = adapter.invoke(command)

        for name, mutate in (
            ("negative-side-effect", lambda value: value.__setitem__("side_effect_count", -1)),
            ("boolean-side-effect", lambda value: value.__setitem__("side_effect_count", True)),
            ("bad-time", lambda value: value.__setitem__("occurred_at", "2026-07-14T00:00:00")),
        ):
            attacked = copy.deepcopy(baseline)
            mutate(attacked)
            attacked["attestation"]["payload_sha256"] = sha256({
                key: value for key, value in attacked.items() if key != "attestation"
            })
            with self.subTest(name=name), self.assertRaises(ReceiptRejected):
                service._validate_receipt(attacked, command)

        valid_other = {
            "soul_id": "soul_" + "a" * 64,
            "device_binding_id": "db_" + "b" * 32,
            "platform_account_id": "pa_" + "c" * 32,
            "trace_id": "trace_" + "d" * 32,
            "privacy_class": "restricted",
        }
        for field, other in valid_other.items():
            attacked = copy.deepcopy(baseline)
            attacked["outputs"][0]["payload"][field] = other
            attacked["outputs"][0]["payload_sha256"] = sha256(
                attacked["outputs"][0]["payload"],
            )
            attacked["attestation"]["payload_sha256"] = sha256({
                key: value for key, value in attacked.items() if key != "attestation"
            })
            with self.subTest(field=field), self.assertRaisesRegex(
                ReceiptRejected, "envelope drift",
            ):
                service._validate_receipt(attacked, command)

    def test_fixed_json_codec_rejects_duplicate_properties_and_nonfinite_numbers(self):
        for raw in ('{"status":"PASS","status":"FAIL"}', '{"count":NaN}'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                json.loads(
                    raw,
                    object_pairs_hook=fixed_adapter_module._strict_json_object,
                    parse_constant=fixed_adapter_module._reject_json_constant,
                )

    def test_postgres_readback_helpers_bind_json_digests_and_identity_columns(self):
        raw_request = request("upgrade:factory-host-pg-readback")
        request_row = (
            json.dumps(raw_request), sha256(raw_request), raw_request["workflow_id"],
            raw_request["idempotency_key"],
        )
        self.assertEqual(
            raw_request,
            PostgresWorkflowRepository._trusted_request_row(
                request_row, raw_request["workflow_id"],
            ),
        )
        for index in (1, 2, 3):
            attacked = list(request_row)
            attacked[index] = "f" * 64 if index == 1 else "attacker"
            with self.subTest(request_column=index), self.assertRaises(Exception):
                PostgresWorkflowRepository._trusted_request_row(
                    attacked, raw_request["workflow_id"],
                )

        message = {"request_id": "call:" + "a" * 32, "stage_id": "stage:" + "b" * 32}
        message_row = (
            json.dumps(message), sha256(message), raw_request["workflow_id"],
            message["request_id"], message["stage_id"],
        )
        self.assertEqual(
            message,
            PostgresWorkflowRepository._trusted_message_row(
                message_row, raw_request["workflow_id"],
            ),
        )
        attacked_message = list(message_row)
        attacked_message[1] = "0" * 64
        with self.assertRaisesRegex(Exception, "stored digest"):
            PostgresWorkflowRepository._trusted_message_row(
                attacked_message, raw_request["workflow_id"],
            )

        unsigned_receipt = {
            "workflow_id": raw_request["workflow_id"],
            "request_id": message["request_id"],
            "status": "PASS",
        }
        digest = sha256(unsigned_receipt)
        stored_receipt = dict(unsigned_receipt)
        stored_receipt["receipt_id"] = "module-receipt:" + digest[:32]
        receipt_row = (
            json.dumps(stored_receipt), digest, raw_request["workflow_id"],
            message["request_id"], stored_receipt["receipt_id"],
        )
        self.assertEqual(
            stored_receipt,
            PostgresWorkflowRepository._trusted_receipt_row(
                receipt_row, raw_request["workflow_id"],
            ),
        )
        attacked_receipt = list(receipt_row)
        attacked_receipt[4] = "module-receipt:" + "f" * 32
        with self.assertRaisesRegex(Exception, "identity or digest"):
            PostgresWorkflowRepository._trusted_receipt_row(
                attacked_receipt, raw_request["workflow_id"],
            )

        native_expectation = {
            "workflow_id": raw_request["workflow_id"],
            "request_sha256": sha256(raw_request),
            "external_context_ref": "release:postgres-readback-native-stop",
            "release_bom_sha256": "9" * 64,
        }
        native_authority = build_native_stop_trust_authority()
        native_fact = native_authority.to_durable_fact(
            native_authority.obtain(**native_expectation),
        )
        native_row = (
            json.dumps(native_fact), sha256(native_fact), native_fact["receipt_id"],
            native_fact["receipt_sha256"], native_fact["release_bom_id"],
            native_fact["release_bom_sha256"], native_fact["integration_commit"],
            native_fact["release_bom_generation"],
            native_fact["activation_token_sha256"],
            native_fact["authority_sets_sha256"], raw_request["workflow_id"],
        )
        self.assertEqual(
            native_fact,
            PostgresWorkflowRepository._trusted_native_stop_trust_row(native_row),
        )
        for index in range(1, len(native_row)):
            attacked_native = list(native_row)
            attacked_native[index] = (
                2 if index == 7 else "" if index == 10 else "f" * 64
            )
            with self.subTest(native_stop_column=index), self.assertRaises(Exception):
                PostgresWorkflowRepository._trusted_native_stop_trust_row(
                    attacked_native,
                )

    def test_postgres_connections_have_fixed_connect_statement_lock_and_idle_timeouts(self):
        calls = []

        class FakeDriver:
            @staticmethod
            def connect(*args, **kwargs):
                calls.append((args, kwargs))
                return object()

        repository = PostgresWorkflowRepository(
            "postgresql://test.invalid/factory",
        )
        with mock.patch.object(
            PostgresWorkflowRepository, "_driver", return_value=FakeDriver,
        ):
            repository._connect()
        self.assertEqual(1, len(calls))
        self.assertEqual(repository.CONNECT_TIMEOUT_SECONDS, calls[0][1]["connect_timeout"])
        self.assertEqual(5_000, calls[0][1]["tcp_user_timeout"])
        self.assertEqual(2, calls[0][1]["keepalives_count"])
        options = calls[0][1]["options"]
        self.assertIn(
            "statement_timeout=%d" % repository.STATEMENT_TIMEOUT_MS, options,
        )
        self.assertIn("lock_timeout=%d" % repository.LOCK_TIMEOUT_MS, options)
        self.assertIn(
            "idle_in_transaction_session_timeout=%d"
            % repository.IDLE_TRANSACTION_TIMEOUT_MS,
            options,
        )

    def test_fixed_tree_hash_enforces_file_count_single_and_total_byte_caps(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve(strict=True)
            (root / "one.bin").write_bytes(b"12")
            (root / "two.bin").write_bytes(b"34")
            with mock.patch.object(fixed_adapter_module, "MAX_DEPLOYMENT_FILES", 1):
                with self.assertRaisesRegex(FactoryHostError, "file-count"):
                    cwd_tree_sha256(root)
            with mock.patch.object(fixed_adapter_module, "MAX_SINGLE_DEPLOYMENT_FILE_BYTES", 1):
                with self.assertRaisesRegex(FactoryHostError, "byte limit"):
                    cwd_tree_sha256(root)
            with mock.patch.object(fixed_adapter_module, "MAX_TOTAL_DEPLOYMENT_BYTES", 3):
                with self.assertRaisesRegex(FactoryHostError, "total-byte"):
                    cwd_tree_sha256(root)

    def test_fixed_runner_kills_descendant_that_inherits_output_pipes(self):
        executable = Path(sys.executable).resolve(strict=True)
        profile = FixedArgvProfile(
            "factory-upgrade-intake", "validate-intent",
            (
                str(executable), "-c",
                "import os,time; pid=os.fork(); "
                "time.sleep(30) if pid == 0 else None",
            ),
            Path("/private/var/empty").resolve(strict=True), 5,
            "0" * 64, "0" * 64, "0" * 64,
        )
        processes = []
        original_popen = fixed_adapter_module.subprocess.Popen

        def capture_process(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process

        started = time.monotonic()
        with mock.patch.object(fixed_adapter_module.subprocess, "Popen", capture_process):
            with self.assertRaisesRegex(FactoryHostError, "descendant retained process pipes"):
                fixed_adapter_module._bounded_process_run(profile, {}, b"{}", 1024)
        self.assertLess(time.monotonic() - started, 4.0)
        self.assertEqual(1, len(processes))
        self.assertIsNotNone(processes[0].poll())
        with self.assertRaises(ProcessLookupError):
            os.killpg(processes[0].pid, 0)

    def test_fixed_argv_rejects_symlink_alias_and_caps_combined_output(self):
        with tempfile.TemporaryDirectory() as directory:
            alias = Path(directory) / "provider.py"
            alias.symlink_to("/usr/bin/true")
            with self.assertRaisesRegex(
                FactoryHostError, "canonical non-symlink",
            ):
                fixed_adapter_module._canonical_argument_file(alias)

        executable = Path(sys.executable).resolve(strict=True)
        profile = FixedArgvProfile(
            "factory-upgrade-intake", "validate-intent",
            (
                str(executable), "-c",
                "import sys; sys.stdout.write('a'*700); "
                "sys.stderr.write('b'*700)",
            ),
            Path("/private/var/empty").resolve(strict=True), 5,
            "0" * 64, "0" * 64, "0" * 64,
            maximum_output_bytes=1024,
        )
        with self.assertRaisesRegex(FactoryHostError, "bounded output"):
            fixed_adapter_module._bounded_process_run(profile, {}, b"{}", 1024)

    def test_fixed_argv_rejects_identity_replacement_between_validation_and_exec(self):
        with tempfile.TemporaryDirectory() as directory:
            provider = Path(directory).resolve(strict=True) / "provider.bin"
            provider.write_bytes(b"trusted-provider")
            expected = ((
                provider,
                False,
                fixed_adapter_module._path_identity_no_follow(provider),
            ),)
            replacement = provider.with_suffix(".replacement")
            replacement.write_bytes(b"attacker-provider")
            replacement.replace(provider)
            with self.assertRaisesRegex(
                FactoryHostError, "identity changed before process creation",
            ), mock.patch.object(
                fixed_adapter_module.subprocess, "Popen",
            ) as popen:
                fixed_adapter_module._bounded_process_run(
                    FixedArgvProfile(
                        "factory-upgrade-intake", "validate-intent",
                        (str(provider),), Path(directory).resolve(strict=True), 1,
                        "0" * 64, "0" * 64, "0" * 64,
                    ),
                    {}, b"{}", 1024, expected,
                )
            popen.assert_not_called()

    def test_fixed_adapter_rejects_shell_and_request_override(self):
        with tempfile.TemporaryDirectory() as directory:
            deployment = Path(directory).resolve(strict=True)
            config = deployment / "settings.json"
            config.write_text('{"mode":"test"}\n', encoding="utf-8")

            def finalized_profile(**changes):
                base = FixedArgvProfile(
                    "factory-upgrade-intake", "validate-intent",
                    ("/usr/bin/true",), deployment, 1,
                    sha256(Path("/usr/bin/true").read_bytes()),
                    cwd_tree_sha256(deployment), "0" * 64,
                    environment=(("PYTHONUTF8", "1"),),
                )
                base = replace(base, **changes)
                return replace(base, profile_sha256=fixed_profile_sha256(base))

            with self.assertRaises(FactoryHostError):
                FixedArgvAdapter(
                    [finalized_profile(argv=("/bin/sh", "-c", "true"))],
                    trusted_policy_sha256="b" * 64,
                    policy_verifier=lambda _profiles, _digest: True,
                )
            with self.assertRaisesRegex(
                FactoryHostError,
                "trusted system identity|group/world writable|service identity",
            ):
                FixedArgvAdapter(
                    [finalized_profile()], trusted_policy_sha256="b" * 64,
                    policy_verifier=lambda _profiles, _digest: True,
                )

            immutable_cwd = Path("/private/var/empty").resolve(strict=True)

            def safe_profile(**changes):
                base = FixedArgvProfile(
                    "factory-upgrade-intake", "validate-intent",
                    ("/usr/bin/true",), immutable_cwd, 1,
                    sha256(Path("/usr/bin/true").read_bytes()),
                    cwd_tree_sha256(immutable_cwd), "0" * 64,
                    environment=(("PYTHONUTF8", "1"),),
                )
                base = replace(base, **changes)
                return replace(base, profile_sha256=fixed_profile_sha256(base))

            profile = safe_profile()
            adapter = FixedArgvAdapter(
                [profile], trusted_policy_sha256="b" * 64,
                policy_verifier=lambda _profiles, _digest: True,
            )
            with self.assertRaises(FactoryHostError):
                adapter.invoke({"target_module": "factory-upgrade-intake", "operation": "validate-intent", "argv": ["attacker"]})

            with self.assertRaisesRegex(FactoryHostError, "secret-bearing"):
                FixedArgvAdapter(
                    [safe_profile(
                        environment=(("DEEPSEEK_API_KEY", "forbidden-test-value"),),
                    )],
                    trusted_policy_sha256="b" * 64,
                    policy_verifier=lambda _profiles, _digest: True,
                )

            with tempfile.TemporaryDirectory() as external_directory:
                external_file = Path(external_directory).resolve(strict=True) / "provider.json"
                external_file.write_text('{"provider":"test"}\n', encoding="utf-8")
                external_profile = safe_profile(
                    argv=("/usr/bin/true", str(external_file)),
                    external_file_sha256s=((external_file, sha256(external_file.read_bytes())),),
                )
                with self.assertRaisesRegex(
                    FactoryHostError,
                    "trusted system identity|group/world writable|service identity",
                ):
                    FixedArgvAdapter(
                        [external_profile], trusted_policy_sha256="b" * 64,
                        policy_verifier=lambda _profiles, _digest: True,
                    )

    def test_fixed_adapter_stream_cap_terminates_and_reaps_process_group(self):
        immutable_cwd = Path("/private/var/empty").resolve(strict=True)
        executable = Path("/usr/bin/yes").resolve(strict=True)
        base = FixedArgvProfile(
            "factory-upgrade-intake", "validate-intent", (str(executable),),
            immutable_cwd, 2, sha256(executable.read_bytes()),
            cwd_tree_sha256(immutable_cwd), "0" * 64,
            maximum_output_bytes=1024,
        )
        profile = replace(base, profile_sha256=fixed_profile_sha256(base))
        adapter = FixedArgvAdapter(
            [profile], trusted_policy_sha256="b" * 64,
            policy_verifier=lambda _profiles, _digest: True,
            maximum_output_bytes=1024,
        )
        processes = []
        original_popen = fixed_adapter_module.subprocess.Popen

        def capture_process(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process

        with mock.patch.object(fixed_adapter_module.subprocess, "Popen", capture_process):
            with self.assertRaisesRegex(FactoryHostError, "bounded output.*terminated"):
                adapter.invoke({
                    "target_module": "factory-upgrade-intake",
                    "operation": "validate-intent",
                })
        self.assertEqual(1, len(processes))
        self.assertIsNotNone(processes[0].poll())


def _intake_replay_payload(seed="a", *, approval=True):
    value = {
        "schema_version": "dps.upgrade-intent/v2",
        "contract_id": "upgrade.intent/v2",
        "producer_module": "factory-upgrade-intake",
        "intent_id": "intent:" + seed * 16,
        "idempotency_key": "idem_" + seed * 64,
        "requester_auth_nonce": "nonce_" + seed * 32,
        "authorization": {
            "approval_nonce": "nonce_" + ("f" if seed != "f" else "e") * 32
            if approval else None,
        },
        "upgrade_intent_sha256": "0" * 64,
    }
    value["upgrade_intent_sha256"] = intake_upgrade_intent_sha256(value)
    return value


def _intake_replay_receipt(payload):
    return {
        "workflow_id": "upgrade:factory-host-replay-unit",
        "request_id": "call:" + "1" * 32,
        "target_module": "factory-upgrade-intake",
        "operation": "validate-intent",
        "outputs": [{
            "contract_id": "upgrade.intent/v2",
            "producer_module": "factory-upgrade-intake",
            "payload_sha256": sha256(payload),
            "payload": copy.deepcopy(payload),
        }],
    }


class IntakeReplayAndMigrationGuardTests(unittest.TestCase):
    def test_intake_replay_claims_are_domain_separated_hashes(self):
        payload = _intake_replay_payload()
        guard = intake_replay_guard_from_receipt(_intake_replay_receipt(payload))
        self.assertIsNotNone(guard)
        self.assertEqual(
            ["INTENT_ID", "IDEMPOTENCY_KEY", "REQUESTER_AUTH_NONCE", "APPROVAL_NONCE"],
            [claim.kind for claim in guard.claims],
        )
        self.assertEqual(4, len({claim.key_sha256 for claim in guard.claims}))
        self.assertNotIn(payload["intent_id"], repr(guard))
        self.assertNotIn(payload["requester_auth_nonce"], repr(guard))
        same_value = "nonce_" + "9" * 32
        self.assertNotEqual(
            intake_replay_claim_key_sha256("REQUESTER_AUTH_NONCE", same_value),
            intake_replay_claim_key_sha256("APPROVAL_NONCE", same_value),
        )

    def test_intake_replay_guard_rejects_digest_and_boundary_attacks(self):
        payload = _intake_replay_payload(approval=False)
        receipt = _intake_replay_receipt(payload)
        guard = intake_replay_guard_from_receipt(receipt)
        self.assertEqual(3, len(guard.claims))

        attacked = copy.deepcopy(receipt)
        attacked["outputs"][0]["payload"]["intent_id"] = "intent:" + "b" * 16
        attacked["outputs"][0]["payload_sha256"] = sha256(attacked["outputs"][0]["payload"])
        with self.assertRaisesRegex(FactoryHostError, "full intent digest"):
            intake_replay_guard_from_receipt(attacked)

        attacked = copy.deepcopy(receipt)
        attacked["outputs"].append(copy.deepcopy(attacked["outputs"][0]))
        with self.assertRaisesRegex(FactoryHostError, "multiple"):
            intake_replay_guard_from_receipt(attacked)

        attacked = copy.deepcopy(receipt)
        attacked["operation"] = "bind-instructions"
        with self.assertRaisesRegex(FactoryHostError, "outside the Intake boundary"):
            intake_replay_guard_from_receipt(attacked)

        self.assertIsNone(intake_replay_guard_from_receipt({
            "outputs": [{"contract_id": "upgrade.intent/v1"}],
        }))

    def test_migration_discovery_rejects_gap_duplicate_symlink_and_bad_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "001_first.sql").write_text("SELECT 1;\n", encoding="utf-8")
            (root / "002_second.sql").write_text("SELECT 2;\n", encoding="utf-8")
            self.assertEqual([1, 2], [item.version for item in discover_migrations(root)])

        attacks = (
            (("001_first.sql", "003_third.sql"), "contiguous"),
            (("001_first.sql", "001_duplicate.sql"), "duplicate"),
            (("001_first.sql", "2_bad.sql"), "filename"),
        )
        for names, message in attacks:
            with self.subTest(names=names), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                for name in names:
                    (root / name).write_text("SELECT 1;\n", encoding="utf-8")
                with self.assertRaisesRegex(FactoryHostError, message):
                    discover_migrations(root)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.txt"
            source.write_text("SELECT 1;\n", encoding="utf-8")
            (root / "001_link.sql").symlink_to(source)
            with self.assertRaisesRegex(FactoryHostError, "symlink"):
                discover_migrations(root)

        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            real_root = parent / "real"
            real_root.mkdir()
            (real_root / "001_first.sql").write_text(
                "SELECT 1;\n", encoding="utf-8",
            )
            linked_root = parent / "linked"
            linked_root.symlink_to(real_root, target_is_directory=True)
            with self.assertRaisesRegex(FactoryHostError, "directory"):
                discover_migrations(linked_root)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "001_bad.sql").write_bytes(b"\xff")
            with self.assertRaisesRegex(FactoryHostError, "UTF-8"):
                discover_migrations(root)

    def test_migration_discovery_holds_directory_inode_during_path_replacement(self):
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary)
            root = parent / "migrations"
            replacement = parent / "replacement"
            original_after_swap = parent / "original"
            root.mkdir()
            replacement.mkdir()
            (root / "001_first.sql").write_text(
                "SELECT 'trusted';\n", encoding="utf-8",
            )
            (replacement / "001_first.sql").write_text(
                "SELECT 'attacker';\n", encoding="utf-8",
            )
            real_listdir = os.listdir

            def replace_path_after_open(directory_descriptor):
                root.rename(original_after_swap)
                replacement.rename(root)
                return real_listdir(directory_descriptor)

            with mock.patch.object(os, "listdir", side_effect=replace_path_after_open):
                migrations = discover_migrations(root)
            self.assertEqual("SELECT 'trusted';\n", migrations[0].sql)

    def test_migrator_admin_connection_has_bounded_failure_parameters(self):
        driver = mock.Mock()
        sentinel_connection = object()
        driver.connect.return_value = sentinel_connection
        migrator = PostgresSchemaMigrator(
            "postgresql://admin.invalid/factory", "factory_runtime",
        )
        self.assertIs(sentinel_connection, migrator._connect(driver))
        args, kwargs = driver.connect.call_args
        self.assertEqual(("postgresql://admin.invalid/factory",), args)
        self.assertFalse(kwargs["autocommit"])
        self.assertEqual(migrator.CONNECT_TIMEOUT_SECONDS, kwargs["connect_timeout"])
        self.assertIn(
            "statement_timeout=%d" % migrator.STATEMENT_TIMEOUT_MS,
            kwargs["options"],
        )
        self.assertIn(
            "lock_timeout=%d" % migrator.LOCK_TIMEOUT_MS,
            kwargs["options"],
        )
        self.assertIn(
            "idle_in_transaction_session_timeout=%d"
            % migrator.IDLE_TRANSACTION_TIMEOUT_MS,
            kwargs["options"],
        )
        self.assertEqual(migrator.TCP_USER_TIMEOUT_MS, kwargs["tcp_user_timeout"])
        self.assertEqual(1, kwargs["keepalives"])

    def test_migration_history_rejects_hash_name_gap_duplicate_and_future_drift(self):
        migrations = discover_migrations(MODULE_ROOT / "migrations")
        exact = [(item.version, item.name, item.sha256) for item in migrations]
        self.assertEqual(len(migrations), verify_migration_history(migrations, exact))
        attacks = (
            ([(1, migrations[0].name, "0" * 64)], "hash drift"),
            ([(1, "001_renamed.sql", migrations[0].sha256)], "name or hash drift"),
            ([(2, migrations[1].name, migrations[1].sha256)], "gap"),
            (exact + [exact[-1]], "duplicates"),
            (exact + [(len(exact) + 1, "999_future.sql", "f" * 64)], "future"),
        )
        for rows, message in attacks:
            with self.subTest(message=message), self.assertRaisesRegex(FactoryHostError, message):
                verify_migration_history(migrations, rows)


class NativeStopAuthorityTrustBoundaryTests(unittest.TestCase):
    @staticmethod
    def expectation(workflow_id="upgrade:native-stop-trust-boundary-0001"):
        return {
            "workflow_id": workflow_id,
            "request_sha256": sha256({"workflow": workflow_id, "kind": "request"}),
            "external_context_ref": "release:native-stop-trust-boundary",
            "release_bom_sha256": sha256({"workflow": workflow_id, "kind": "bom"}),
        }

    def test_real_release_wire_fixture_and_three_authority_sets_are_verified(self):
        expectation = self.expectation()
        provider = TestNativeStopTrustProvider()
        envelope = provider.fetch(NativeStopTrustRequest(**expectation))
        receipt = json.loads(envelope.canonical_receipt_bytes)
        payload = {key: value for key, value in receipt.items() if key != "signature"}
        self.assertEqual(
            RELEASE_SUBJECT.native_stop_trust_signing_bytes(payload),
            release_receipt_signing_bytes(payload),
        )
        authority = build_native_stop_trust_authority(provider)
        capability = authority.obtain(**expectation)
        self.assertTrue(authority.validate_capability(capability, **expectation))
        fact = authority.to_durable_fact(capability)
        self.assertEqual(receipt["receipt_id"], fact["receipt_id"])
        self.assertEqual(receipt["release_bom_generation"], fact["release_bom_generation"])
        self.assertEqual(receipt["activation_token_sha256"], fact["activation_token_sha256"])
        self.assertEqual(receipt["authority_sets_sha256"], fact["authority_sets_sha256"])
        serialized = json.dumps(fact, sort_keys=True)
        self.assertNotIn('"activation_token":', serialized)
        self.assertNotIn("PRIVATE KEY", serialized.upper())
        self.assertNotIn('"client_secret":', serialized)

    def test_unknown_missing_and_unpublished_hyphenated_major_fail_closed(self):
        attacks = {
            "unknown-major": lambda value: value.__setitem__(
                "contract_id", "release.bom.native.stop.authority.trust/v2",
            ),
            "missing-major": lambda value: value.__setitem__(
                "contract_id", "release.bom.native.stop.authority.trust",
            ),
            "hyphen-draft": lambda value: value.__setitem__(
                "contract_id", "release.bom.native-stop-authority-trust/v1",
            ),
        }
        for name, attack in attacks.items():
            with self.subTest(attack=name):
                authority = build_native_stop_trust_authority(
                    TestNativeStopTrustProvider(mutate=attack),
                )
                with self.assertRaises(NativeStopAuthorityTrustError):
                    authority.obtain(**self.expectation())

    def test_signature_bom_generation_and_authority_swaps_fail_closed(self):
        attacks = {
            "signature": lambda value: value["signature"].__setitem__("value", "attacker"),
            "bom": lambda value: value.__setitem__("release_bom_sha256", "f" * 64),
            "generation": lambda value: value["native_stop_authorities"][0].__setitem__(
                "release_bom_generation", 2,
            ),
            "authority-set": lambda value: value.__setitem__(
                "native_stop_authorities_sha256",
                value["device_route_assignment_authorities_sha256"],
            ),
        }
        for name, attack in attacks.items():
            with self.subTest(attack=name):
                authority = build_native_stop_trust_authority(
                    TestNativeStopTrustProvider(mutate=attack),
                )
                with self.assertRaises(NativeStopAuthorityTrustError):
                    authority.obtain(**self.expectation())

    def test_provider_attestation_issuer_audience_revocation_and_signature_are_current(self):
        expectation = self.expectation()
        base = TestNativeStopTrustProvider().fetch(NativeStopTrustRequest(**expectation))
        attacks = {
            "issuer": {"issuer": "attacker-native-stop-trust-provider"},
            "audience": {"audience": "dps.attacker"},
            "revoked": {"revoked": True},
            "receipt-sha": {"receipt_sha256": "f" * 64},
        }
        for name, changes in attacks.items():
            with self.subTest(attack=name):
                unsigned = replace(base.provider_attestation, signature="pending", **changes)
                attacked = replace(
                    unsigned,
                    signature=sha256(provider_attestation_signing_bytes(unsigned)),
                )
                authority = build_native_stop_trust_authority(
                    StaticNativeStopTrustProvider(
                        NativeStopTrustEnvelope(base.canonical_receipt_bytes, attacked),
                    ),
                )
                with self.assertRaises(NativeStopAuthorityTrustError):
                    authority.obtain(**expectation)

        replay_expectation = dict(expectation)
        replay_expectation["workflow_id"] = "upgrade:native-stop-trust-boundary-replay"
        replay_expectation["request_sha256"] = sha256({
            "workflow": replay_expectation["workflow_id"], "kind": "request",
        })
        replay_authority = build_native_stop_trust_authority(
            StaticNativeStopTrustProvider(base),
        )
        with self.assertRaisesRegex(NativeStopAuthorityTrustError, "binding"):
            replay_authority.obtain(**replay_expectation)

    def test_plain_mapping_lambda_direct_constructor_cross_authority_and_raw_swap_fail(self):
        expectation = self.expectation()
        with self.assertRaises(TypeError):
            build_native_stop_trust_authority(provider=lambda request_value: None)
        with self.assertRaises(TypeError):
            build_native_stop_trust_authority(provider={"fetch": "always-true"})
        attestation = TestNativeStopTrustProvider().fetch(
            NativeStopTrustRequest(**expectation),
        ).provider_attestation
        with self.assertRaisesRegex(TypeError, "composition-root sealed"):
            VerifiedNativeStopAuthorityTrust(
                object(), {}, b"{}", attestation, {}, "0" * 64,
            )
        with self.assertRaises(TypeError):
            FactoryControlPlaneHost(
                InMemoryWorkflowRepository(), SimulationRoleDirectory(),
                DeterministicSimulationAdapter(), SimulationReceiptVerifier(),
                PROVIDER_VERIFIER, SimulationExternalAuthority(),
                StaticRuntimeControlAuthority(),
                native_stop_authority_trust={"verify": lambda: True},
            )

        authority_a = build_native_stop_trust_authority()
        authority_b = build_native_stop_trust_authority()
        capability = authority_a.obtain(**expectation)
        self.assertFalse(authority_b.validate_capability(capability, **expectation))
        capability._raw = capability._raw + b" "
        self.assertFalse(authority_a.validate_capability(capability, **expectation))

        authority_swap = build_native_stop_trust_authority()
        authority_swap._provider = TestNativeStopTrustProvider()
        with self.assertRaisesRegex(NativeStopAuthorityTrustError, "swapped"):
            authority_swap.obtain(**expectation)

    def test_expiry_equality_and_replay_fail_currentness(self):
        clock = MutableClock()
        provider = TestNativeStopTrustProvider(now=clock)
        authority = build_native_stop_trust_authority(
            provider, TestNativeStopTrustClock(clock),
        )
        expectation = self.expectation()
        capability = authority.obtain(**expectation)
        fact = authority.to_durable_fact(capability)
        clock.advance(dt.timedelta(minutes=10))
        self.assertFalse(authority.validate_capability(capability, **expectation))
        with self.assertRaisesRegex(NativeStopAuthorityTrustError, "stale"):
            authority.revalidate_fact(fact, **expectation)

    def test_missing_deployed_provider_waits_before_any_rollout_phase(self):
        repository = InMemoryWorkflowRepository()
        adapter = SyntheticProductionAdapter()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("native-trust-missing"), adapter,
            SyntheticProductionReceiptVerifier(), PROVIDER_VERIFIER,
            SimulationExternalAuthority(production_bom_available=True),
            StaticRuntimeControlAuthority(), native_stop_authority_trust=None,
        )
        workflow_id = service.start(request(
            "upgrade:native-stop-trust-provider-missing",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:native-stop-trust-provider-missing",
        ))["workflow_id"]
        waiting = service.run_until_blocked(
            workflow_id, "native-trust-missing-worker", maximum_steps=240,
        )
        self.assertEqual("WAITING_EXTERNAL", waiting["state"])
        self.assertEqual(
            "NATIVE_STOP_AUTHORITY_TRUST_PROVIDER_REQUIRED",
            waiting["waiting_reason"],
        )
        events = repository.events(workflow_id)
        self.assertFalse(any(event["state"] == "BOM_SIGNED" for event in events))
        self.assertFalse(any(
            event["event_type"] == "STAGE_SCHEDULED"
            and event["payload"].get("phase") == "verify-signed-bom"
            for event in events
        ))
        self.assertNotIn(
            "verify-signed-bom",
            [call["operation"] for call in adapter.inner.calls],
        )

    def test_durable_fact_revalidates_after_restart_without_refetch(self):
        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("native-trust-restart"),
            SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER,
            SimulationExternalAuthority(production_bom_available=True),
            StaticRuntimeControlAuthority(),
            native_stop_authority_trust=build_native_stop_trust_authority(),
        )
        workflow_id = service.start(request(
            "upgrade:native-stop-trust-durable-restart",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:native-stop-trust-durable-restart",
        ))["workflow_id"]
        self.assertEqual(
            "COMPLETED",
            service.run_until_blocked(
                workflow_id, "native-trust-before-restart", maximum_steps=260,
            )["state"],
        )
        offline_provider = TestNativeStopTrustProvider(available=False)
        restarted = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("native-trust-restart"),
            SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER,
            SimulationExternalAuthority(production_bom_available=False),
            StaticRuntimeControlAuthority(),
            native_stop_authority_trust=build_native_stop_trust_authority(
                offline_provider,
            ),
        )
        status = restarted.status(workflow_id)
        self.assertEqual("COMPLETED", status["state"])
        self.assertTrue(status["production_authorized"])

    def test_same_receipt_id_with_different_canonical_bytes_is_quarantined(self):
        repository = InMemoryWorkflowRepository()
        original_provider = TestNativeStopTrustProvider()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("native-trust-conflict"),
            SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER,
            SimulationExternalAuthority(production_bom_available=True),
            StaticRuntimeControlAuthority(),
            native_stop_authority_trust=build_native_stop_trust_authority(
                original_provider,
            ),
        )
        workflow_id = service.start(request(
            "upgrade:native-stop-trust-receipt-conflict",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:native-stop-trust-receipt-conflict",
        ))["workflow_id"]
        self.assertEqual(
            "COMPLETED",
            service.run_until_blocked(
                workflow_id, "native-trust-conflict-first", maximum_steps=260,
            )["state"],
        )
        workflow_request = repository.request(workflow_id)
        signed = FactoryControlPlaneHost._bound_external_fact(
            repository.events(workflow_id), "SIGNED_BOM",
        )
        expectation = {
            "workflow_id": workflow_id,
            "request_sha256": sha256(workflow_request),
            "external_context_ref": workflow_request["external_context_ref"],
            "release_bom_sha256": signed["bom_sha256"],
        }
        original_envelope = original_provider.fetch(NativeStopTrustRequest(**expectation))
        conflicting_envelope = resign_native_stop_envelope(
            original_envelope,
            lambda value: value.__setitem__(
                "trust_policy_id", "native-stop-trust-policy-conflicting-bytes",
            ),
        )
        conflicting_authority = build_native_stop_trust_authority(
            StaticNativeStopTrustProvider(conflicting_envelope),
        )
        conflicting_capability = conflicting_authority.obtain(**expectation)
        conflicting_fact = conflicting_authority.to_durable_fact(
            conflicting_capability,
        )
        restarted = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("native-trust-conflict"),
            SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER,
            SimulationExternalAuthority(production_bom_available=True),
            StaticRuntimeControlAuthority(),
            native_stop_authority_trust=conflicting_authority,
        )
        fence = restarted._acquire_fence(workflow_id, "native-trust-conflict-second")
        with self.assertRaises(IdempotencyConflict):
            restarted._bind_external_fact(
                workflow_id, "COMPLETED", "NATIVE_STOP_AUTHORITY_TRUST",
                conflicting_fact, fence,
            )
        self.assertEqual(
            "NATIVE_STOP_TRUST_RECEIPT_HASH_CONFLICT",
            repository.quarantine_records(workflow_id)[-1]["reason"],
        )

    def test_global_receipt_binding_quarantines_cross_workflow_sha_conflict(self):
        repository = InMemoryWorkflowRepository()
        service = FactoryControlPlaneHost(
            repository, SimulationRoleDirectory("native-trust-global-index"),
            SyntheticProductionAdapter(), SyntheticProductionReceiptVerifier(),
            PROVIDER_VERIFIER, SimulationExternalAuthority(production_bom_available=True),
            StaticRuntimeControlAuthority(),
            native_stop_authority_trust=build_native_stop_trust_authority(),
        )
        raw_a = request(
            "upgrade:native-stop-global-index-a",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:native-stop-global-index-a",
        )
        raw_b = request(
            "upgrade:native-stop-global-index-b",
            mode="PRODUCTION", risk_tier="R1",
            external_context_ref="release:native-stop-global-index-b",
        )
        service.start(raw_a)
        service.start(raw_b)
        bom_sha = "9" * 64
        expectation_a = {
            "workflow_id": raw_a["workflow_id"],
            "request_sha256": sha256(raw_a),
            "external_context_ref": raw_a["external_context_ref"],
            "release_bom_sha256": bom_sha,
        }
        authority_a = build_native_stop_trust_authority()
        fact_a = authority_a.to_durable_fact(authority_a.obtain(**expectation_a))

        expectation_b = {
            "workflow_id": raw_b["workflow_id"],
            "request_sha256": sha256(raw_b),
            "external_context_ref": raw_b["external_context_ref"],
            "release_bom_sha256": bom_sha,
        }
        base_b = TestNativeStopTrustProvider().fetch(
            NativeStopTrustRequest(**expectation_b),
        )

        def reuse_first_receipt_id(value):
            value["receipt_id"] = fact_a["receipt_id"]
            value["idempotency_key"] = "idem_" + sha256({
                "contract_id": value["contract_id"],
                "receipt_id": value["receipt_id"],
                "release_bom_sha256": value["release_bom_sha256"],
            })

        conflicting_envelope = resign_native_stop_envelope(
            base_b, reuse_first_receipt_id,
        )
        authority_b = build_native_stop_trust_authority(
            StaticNativeStopTrustProvider(conflicting_envelope),
        )
        fact_b = authority_b.to_durable_fact(authority_b.obtain(**expectation_b))
        self.assertEqual(fact_a["receipt_id"], fact_b["receipt_id"])
        self.assertNotEqual(fact_a["receipt_sha256"], fact_b["receipt_sha256"])

        fence_a = repository.acquire_fence(raw_a["workflow_id"], "global-index-a", utc_now())
        fence_b = repository.acquire_fence(raw_b["workflow_id"], "global-index-b", utc_now())
        self.assertTrue(repository.register_native_stop_authority_trust(
            raw_a["workflow_id"], fact_a, fence_a, utc_now(),
        ))
        self.assertFalse(repository.register_native_stop_authority_trust(
            raw_b["workflow_id"], fact_a, fence_b, utc_now(),
        ))
        with self.assertRaisesRegex(IdempotencyConflict, "globally bound"):
            repository.register_native_stop_authority_trust(
                raw_b["workflow_id"], fact_b, fence_b, utc_now(),
            )
        self.assertEqual(
            fact_a,
            repository.native_stop_authority_trust(fact_a["receipt_id"]),
        )
        self.assertEqual(
            "NATIVE_STOP_TRUST_RECEIPT_HASH_CONFLICT",
            repository.quarantine_records(raw_b["workflow_id"])[-1]["reason"],
        )


if __name__ == "__main__":
    unittest.main()
    OPERATION_MINIMUM_LEVEL,
