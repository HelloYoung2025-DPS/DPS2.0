"""Recoverable DPS AI Factory composition root.

The host communicates with Factory modules only through versioned JSON
documents and injected adapters.  It deliberately imports no provider module.
"""

from __future__ import annotations

import copy
import datetime as dt
import hashlib
import json
import re
import threading
import unicodedata
from dataclasses import dataclass
from typing import Any, Callable, Iterable, Mapping, Protocol, Sequence, TypeVar

from native_stop_authority_trust import (
    NativeStopAuthorityTrustAuthority,
    NativeStopAuthorityTrustError,
)


ZERO_HASH = "0" * 64
_GuardedResult = TypeVar("_GuardedResult")
MAX_WORKFLOW_REQUEST_BYTES = 262_144
MAX_TARGET_MODULES = 32
MAX_REQUESTED_PATHS = 512
MAX_PUBLIC_CONTRACT_CHANGES = 128
MAX_REPOSITORY_PATH_LENGTH = 512
MAX_MODULE_RECEIPT_BYTES = 1_048_576
_WINDOWS_RESERVED_SEGMENTS = frozenset(
    {"con", "prn", "aux", "nul"}
    | {"com%d" % value for value in range(1, 10)}
    | {"lpt%d" % value for value in range(1, 10)}
)
ROLES = (
    "impact-planner",
    "contract-architect",
    "module-implementer",
    "independent-test-agent",
    "security-privacy-adversary",
    "reliability-reviewer",
    "windows-zenno-reviewer",
    "evidence-auditor",
    "release-rollback-controller",
)
STATES = (
    "REQUESTED", "SCOPE_RESOLVED", "INSTRUCTIONS_BOUND", "BASELINE_VERIFIED",
    "CONTRACT_FROZEN", "IMPLEMENTING", "CHANGESET_FROZEN", "CANDIDATE_BUILT",
    "CANDIDATE_VERIFIED", "BOM_SIGNED", "SHADOW", "CANARY", "ROLLING",
    "SOAKING", "COMPLETED", "STALE", "REWORKING", "WAITING_EXTERNAL",
    "QUARANTINED", "ROLLBACK_REQUIRED", "ROLLING_BACK", "ROLLED_BACK",
    "FAILED", "CANCELLED",
)
TERMINAL_STATES = frozenset({"COMPLETED", "ROLLED_BACK", "FAILED", "CANCELLED", "QUARANTINED"})
ROLLOUT_STATES = frozenset({"SHADOW", "CANARY", "ROLLING", "SOAKING", "COMPLETED"})
ROLLOUT_TRANSITIONS = {
    "BOM_SIGNED": "SHADOW",
    "SHADOW": "CANARY",
    "CANARY": "ROLLING",
    "ROLLING": "SOAKING",
    "SOAKING": "COMPLETED",
}
RECEIPT_STATUSES = frozenset({"PASS", "FAIL", "STALE", "WAITING_EXTERNAL", "INFRA_ERROR", "QUARANTINED"})
VERIFICATION_LEVELS = (
    "REPOSITORY_STATIC_VERIFIED", "CONTRACT_VERIFIED", "INTEGRATION_VERIFIED",
    "WINDOWS_VERIFIED", "DEVICE_VERIFIED", "CANARY_VERIFIED", "SCALE_VERIFIED",
)
EVIDENCE_LEVEL_BY_KIND = {
    "REPOSITORY": "REPOSITORY_STATIC_VERIFIED",
    "CONTRACT": "CONTRACT_VERIFIED",
    "INTEGRATION": "INTEGRATION_VERIFIED",
    "WINDOWS": "WINDOWS_VERIFIED",
    "DEVICE": "DEVICE_VERIFIED",
    "CANARY": "CANARY_VERIFIED",
    "SCALE": "SCALE_VERIFIED",
    "SIMULATION": "INTEGRATION_VERIFIED",
}
OPERATION_MINIMUM_LEVEL = {
    "validate-intent": "REPOSITORY_STATIC_VERIFIED",
    "bind-instructions": "REPOSITORY_STATIC_VERIFIED",
    "verify-baseline": "REPOSITORY_STATIC_VERIFIED",
    "freeze-contract-plan": "REPOSITORY_STATIC_VERIFIED",
    "plan-module-worktrees": "REPOSITORY_STATIC_VERIFIED",
    "lease-implementation-worktrees": "REPOSITORY_STATIC_VERIFIED",
    "lease-test-worktrees": "REPOSITORY_STATIC_VERIFIED",
    "lease-contract-worktree": "REPOSITORY_STATIC_VERIFIED",
    "lease-operations-worktrees": "REPOSITORY_STATIC_VERIFIED",
    "verify-implementation-ready": "CONTRACT_VERIFIED",
    "verify-changeset": "CONTRACT_VERIFIED",
    "security-privacy-review": "CONTRACT_VERIFIED",
    "reliability-review": "CONTRACT_VERIFIED",
    "windows-zenno-review": "CONTRACT_VERIFIED",
    "verify-merge-head": "CONTRACT_VERIFIED",
    "build-candidate": "CONTRACT_VERIFIED",
    "replay-audit": "INTEGRATION_VERIFIED",
    "verify-signed-bom": "INTEGRATION_VERIFIED",
    "run-shadow": "INTEGRATION_VERIFIED",
    "run-canary": "DEVICE_VERIFIED",
    "run-rolling": "CANARY_VERIFIED",
    "run-soak": "CANARY_VERIFIED",
    "complete-release": "CANARY_VERIFIED",
    "prepare-rollback": "INTEGRATION_VERIFIED",
    "execute-rollback": "INTEGRATION_VERIFIED",
}
PRODUCTION_STATE_CAP = {
    "REQUESTED": "REPOSITORY_STATIC_VERIFIED",
    "SCOPE_RESOLVED": "REPOSITORY_STATIC_VERIFIED",
    "INSTRUCTIONS_BOUND": "REPOSITORY_STATIC_VERIFIED",
    "BASELINE_VERIFIED": "REPOSITORY_STATIC_VERIFIED",
    "CONTRACT_FROZEN": "REPOSITORY_STATIC_VERIFIED",
    "IMPLEMENTING": "CONTRACT_VERIFIED",
    "CHANGESET_FROZEN": "CONTRACT_VERIFIED",
    "CANDIDATE_BUILT": "CONTRACT_VERIFIED",
    "CANDIDATE_VERIFIED": "INTEGRATION_VERIFIED",
    "BOM_SIGNED": "INTEGRATION_VERIFIED",
    "SHADOW": "INTEGRATION_VERIFIED",
    "CANARY": "DEVICE_VERIFIED",
    "ROLLING": "CANARY_VERIFIED",
    "SOAKING": "CANARY_VERIFIED",
    "COMPLETED": "CANARY_VERIFIED",
    "STALE": "CANARY_VERIFIED",
    "REWORKING": "CANARY_VERIFIED",
    "WAITING_EXTERNAL": "CANARY_VERIFIED",
    "QUARANTINED": "CANARY_VERIFIED",
    "ROLLBACK_REQUIRED": "CANARY_VERIFIED",
    "ROLLING_BACK": "CANARY_VERIFIED",
    "ROLLED_BACK": "CANARY_VERIFIED",
    "FAILED": "CANARY_VERIFIED",
    "CANCELLED": "CANARY_VERIFIED",
}
MODULES = frozenset({
    "factory-upgrade-intake", "factory-instruction-resolver",
    "factory-impact-analyzer", "factory-worktree-manager",
    "factory-trusted-runner", "factory-merge-controller",
    "factory-artifact-builder", "factory-evidence-ledger",
    "factory-release-controller", "factory-rollback-controller",
})
CONTRACT_PRODUCERS = {
    "upgrade.intent/v1": "factory-upgrade-intake",
    "instruction.receipt/v1": "factory-instruction-resolver",
    "module.change.plan/v1": "factory-impact-analyzer",
    "worktree.plan/v1": "factory-worktree-manager",
    "worktree.lease/v1": "factory-worktree-manager",
    "trusted.test.result/v1": "factory-trusted-runner",
    "merge.decision/v1": "factory-merge-controller",
    "artifact.descriptor/v1": "factory-artifact-builder",
    "upgrade.event/v1": "factory-evidence-ledger",
    "rollout.event/v1": "factory-release-controller",
    "rollback.plan/v1": "factory-rollback-controller",
    "rollback.result/v1": "factory-rollback-controller",
}
NATIVE_STOP_AUTHORITY_TRUST_FACT = "NATIVE_STOP_AUTHORITY_TRUST"
NATIVE_STOP_TRUST_DURABLE_FIELDS = frozenset({
    "verified", "fact_id", "fact_kind", "contract_id", "receipt_id",
    "receipt_sha256", "canonical_receipt_utf8", "release_bom_id",
    "release_bom_sha256", "integration_commit", "release_bom_generation",
    "activation_token_sha256", "trust_policy_id",
    "native_stop_authorities_sha256", "device_route_assignment_authorities_sha256",
    "native_stop_challenge_authorities_sha256", "authority_sets_sha256",
    "provider_attestation",
})
ROLE_PATH_CLASSES = {
    "impact-planner": (),
    "contract-architect": ("contracts", "governance"),
    "module-implementer": ("src", "migrations"),
    "independent-test-agent": ("tests",),
    "security-privacy-adversary": (),
    "reliability-reviewer": ("operations",),
    "windows-zenno-reviewer": (),
    "evidence-auditor": (),
    "release-rollback-controller": (),
}


class FactoryHostError(RuntimeError):
    pass


class InvalidWorkflowRequest(FactoryHostError):
    pass


class RoleSeparationError(FactoryHostError):
    pass


class StaleFence(FactoryHostError):
    pass


class IdempotencyConflict(FactoryHostError):
    pass


class ReceiptRejected(FactoryHostError):
    pass


class ExpiredWorktreeLease(ReceiptRejected):
    pass


class ExternalFactExpired(ReceiptRejected):
    pass


class CorruptWorkflow(FactoryHostError):
    pass


class IllegalTransition(FactoryHostError):
    pass


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
        allow_nan=False,
    ).encode("utf-8")


def sha256(value: Any) -> str:
    data = value if isinstance(value, bytes) else canonical_bytes(value)
    return hashlib.sha256(data).hexdigest()


def validate_native_stop_trust_durable_fact(
    fact: Mapping[str, Any],
) -> dict[str, Any]:
    """Validate the stable identity stored in the global append-only index."""
    if (
        not isinstance(fact, Mapping)
        or set(fact) != NATIVE_STOP_TRUST_DURABLE_FIELDS
        or fact.get("verified") is not True
        or fact.get("fact_kind") != NATIVE_STOP_AUTHORITY_TRUST_FACT
        or fact.get("contract_id")
        != "release.bom.native.stop.authority.trust/v1"
    ):
        raise CorruptWorkflow("native-stop trust durable fact is not exact")
    value = copy.deepcopy(dict(fact))
    receipt_id = value.get("receipt_id")
    if (
        not isinstance(receipt_id, str)
        or re.fullmatch(r"native-stop-trust-[a-f0-9]{32}", receipt_id) is None
        or value.get("fact_id") != receipt_id
    ):
        raise CorruptWorkflow("native-stop trust durable receipt identity is invalid")
    for name in (
        "receipt_sha256", "release_bom_sha256", "activation_token_sha256",
        "native_stop_authorities_sha256",
        "device_route_assignment_authorities_sha256",
        "native_stop_challenge_authorities_sha256", "authority_sets_sha256",
    ):
        if not isinstance(value.get(name), str) or re.fullmatch(
            r"[a-f0-9]{64}", value[name],
        ) is None:
            raise CorruptWorkflow("native-stop trust durable digest is invalid: " + name)
    if (
        not isinstance(value.get("integration_commit"), str)
        or re.fullmatch(r"[a-f0-9]{40}", value["integration_commit"]) is None
        or isinstance(value.get("release_bom_generation"), bool)
        or not isinstance(value.get("release_bom_generation"), int)
        or value["release_bom_generation"] < 1
    ):
        raise CorruptWorkflow("native-stop trust durable BOM tuple is invalid")
    canonical_text = value.get("canonical_receipt_utf8")
    if not isinstance(canonical_text, str):
        raise CorruptWorkflow("native-stop trust durable canonical bytes are missing")
    try:
        raw = canonical_text.encode("utf-8", errors="strict")
        receipt = json.loads(canonical_text)
    except (UnicodeEncodeError, json.JSONDecodeError) as exc:
        raise CorruptWorkflow("native-stop trust durable canonical bytes are invalid") from exc
    if (
        not isinstance(receipt, Mapping)
        or canonical_bytes(receipt) != raw
        or sha256(raw) != value["receipt_sha256"]
    ):
        raise CorruptWorkflow("native-stop trust durable receipt bytes or digest drifted")
    for fact_name, receipt_name in (
        ("receipt_id", "receipt_id"),
        ("release_bom_id", "release_bom_id"),
        ("release_bom_sha256", "release_bom_sha256"),
        ("integration_commit", "integration_commit"),
        ("release_bom_generation", "release_bom_generation"),
        ("activation_token_sha256", "activation_token_sha256"),
        ("authority_sets_sha256", "authority_sets_sha256"),
    ):
        if value.get(fact_name) != receipt.get(receipt_name):
            raise CorruptWorkflow("native-stop trust durable BOM identity drifted")
    if not isinstance(value.get("provider_attestation"), Mapping):
        raise CorruptWorkflow("native-stop trust durable provider attestation is missing")
    return value


def canonical_repository_path_key(path: str) -> str:
    """Canonical key for the supported case-insensitive macOS/Windows targets."""
    if not isinstance(path, str) or not path or len(path) > MAX_REPOSITORY_PATH_LENGTH:
        raise InvalidWorkflowRequest("requested path length is invalid")
    if path.startswith("/") or "\\" in path or any(ord(char) < 32 for char in path):
        raise InvalidWorkflowRequest("requested path is unsafe")
    segments = path.split("/")
    if any(not segment or segment in {".", ".."} or segment.startswith(".") for segment in segments):
        raise InvalidWorkflowRequest("requested path contains a hidden or relative segment")
    for segment in segments:
        normalized = unicodedata.normalize("NFC", segment)
        if normalized.endswith((" ", ".")) or any(char in '<>:"|?*' for char in normalized):
            raise InvalidWorkflowRequest("requested path is not portable to Windows")
        basename = normalized.split(".", 1)[0].casefold()
        if basename in _WINDOWS_RESERVED_SEGMENTS:
            raise InvalidWorkflowRequest("requested path uses a reserved Windows segment")
    return "/".join(unicodedata.normalize("NFC", segment).casefold() for segment in segments)


def owned_path_class(path: str) -> str:
    parts = path.split("/")
    if len(parts) < 3 or parts[0] != "Modules":
        raise InvalidWorkflowRequest("requested path is outside a module home")
    relative = parts[2:]
    if len(relative) == 1:
        if relative[0] in {"AGENTS.md", "module.yaml"}:
            return "governance"
        if relative[0] in {"CHANGELOG.md", "README.md"}:
            return "operations"
        raise InvalidWorkflowRequest("requested module-root file has no authorized path class")
    if relative[0] in {"src", "migrations", "tests", "contracts", "operations"}:
        return relative[0]
    raise InvalidWorkflowRequest("requested path has no authorized path class")


def writer_role_for_path(path: str) -> str:
    path_class = owned_path_class(path)
    for role, allowed in ROLE_PATH_CLASSES.items():
        if path_class in allowed:
            return role
    raise InvalidWorkflowRequest("requested path class has no writer role")


def opaque_idempotency(value: str) -> str:
    if re.fullmatch(r"idem_[a-f0-9]{64}", value):
        return value
    return "idem_" + sha256(value)


def logical_request_sha256(command: Mapping[str, Any]) -> str:
    """Digest the immutable provider request, excluding delivery-attempt facts."""
    fields = (
        "workflow_id", "request_id", "stage_id", "target_module", "operation",
        "actor_identity", "actor_role", "mode", "expected_output_contracts",
        "context_sha256",
    )
    return sha256({name: command.get(name) for name in fields})


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def _timestamp(value: Any, name: str) -> str:
    if not isinstance(value, str):
        raise InvalidWorkflowRequest(name + " must be a timestamp")
    try:
        parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise InvalidWorkflowRequest(name + " must be a timestamp") from exc
    if parsed.tzinfo is None:
        raise InvalidWorkflowRequest(name + " must have a timezone")
    return value


@dataclass(frozen=True)
class CallSpec:
    target_module: str
    operation: str
    role: str
    expected_outputs: tuple[str, ...]
    subject_module: str | None = None


@dataclass(frozen=True)
class PhaseSpec:
    name: str
    calls: tuple[CallSpec, ...]
    transition_to: str | None


PHASES: dict[str, tuple[PhaseSpec, ...]] = {
    "REQUESTED": (PhaseSpec("resolve-scope", (CallSpec("factory-upgrade-intake", "validate-intent", "impact-planner", ("upgrade.intent/v1",)),), "SCOPE_RESOLVED"),),
    "SCOPE_RESOLVED": (PhaseSpec("bind-instructions", (CallSpec("factory-instruction-resolver", "bind-instructions", "impact-planner", ("instruction.receipt/v1",)),), "INSTRUCTIONS_BOUND"),),
    "INSTRUCTIONS_BOUND": (PhaseSpec("verify-baseline", (CallSpec("factory-instruction-resolver", "verify-baseline", "reliability-reviewer", ("instruction.receipt/v1",)),), "BASELINE_VERIFIED"),),
    "BASELINE_VERIFIED": (PhaseSpec("freeze-contract-plan", (CallSpec("factory-impact-analyzer", "freeze-contract-plan", "contract-architect", ("module.change.plan/v1",)),), "CONTRACT_FROZEN"),),
    "CONTRACT_FROZEN": (
        PhaseSpec("plan-worktrees", (
            CallSpec("factory-worktree-manager", "plan-module-worktrees", "impact-planner", ("worktree.plan/v1",)),
        ), None),
        PhaseSpec("lease-worktrees", (
            CallSpec("factory-worktree-manager", "lease-implementation-worktrees", "module-implementer", ("worktree.lease/v1",)),
            CallSpec("factory-worktree-manager", "lease-test-worktrees", "independent-test-agent", ("worktree.lease/v1",)),
            CallSpec("factory-worktree-manager", "lease-contract-worktree", "contract-architect", ("worktree.lease/v1",)),
            CallSpec("factory-worktree-manager", "lease-operations-worktrees", "reliability-reviewer", ("worktree.lease/v1",)),
        ), "IMPLEMENTING"),
    ),
    "IMPLEMENTING": (
        PhaseSpec("changeset-ready", (
            CallSpec("factory-trusted-runner", "verify-implementation-ready", "independent-test-agent", ("trusted.test.result/v1",)),
        ), None),
        PhaseSpec("independent-verification", (
            CallSpec("factory-trusted-runner", "verify-changeset", "independent-test-agent", ("trusted.test.result/v1",)),
            CallSpec("factory-trusted-runner", "security-privacy-review", "security-privacy-adversary", ("trusted.test.result/v1",)),
            CallSpec("factory-trusted-runner", "reliability-review", "reliability-reviewer", ("trusted.test.result/v1",)),
            CallSpec("factory-trusted-runner", "windows-zenno-review", "windows-zenno-reviewer", ("trusted.test.result/v1",)),
        ), "CHANGESET_FROZEN"),
    ),
    "CHANGESET_FROZEN": (
        PhaseSpec("verify-merge-head", (CallSpec("factory-merge-controller", "verify-merge-head", "evidence-auditor", ("merge.decision/v1",)),), None),
        PhaseSpec("build-candidate", (CallSpec("factory-artifact-builder", "build-candidate", "module-implementer", ("artifact.descriptor/v1",)),), "CANDIDATE_BUILT"),
    ),
    "CANDIDATE_BUILT": (PhaseSpec("audit-candidate", (
        CallSpec("factory-evidence-ledger", "replay-audit", "evidence-auditor", ("upgrade.event/v1",)),
    ), "CANDIDATE_VERIFIED"),),
    "CANDIDATE_VERIFIED": (PhaseSpec("verify-signed-bom", (CallSpec("factory-release-controller", "verify-signed-bom", "evidence-auditor", ("rollout.event/v1",)),), "BOM_SIGNED"),),
    "BOM_SIGNED": (PhaseSpec("shadow", (CallSpec("factory-release-controller", "run-shadow", "release-rollback-controller", ("rollout.event/v1",)),), "SHADOW"),),
    "SHADOW": (PhaseSpec("canary", (CallSpec("factory-release-controller", "run-canary", "release-rollback-controller", ("rollout.event/v1",)),), "CANARY"),),
    "CANARY": (PhaseSpec("rolling", (CallSpec("factory-release-controller", "run-rolling", "release-rollback-controller", ("rollout.event/v1",)),), "ROLLING"),),
    "ROLLING": (PhaseSpec("soak", (CallSpec("factory-release-controller", "run-soak", "release-rollback-controller", ("rollout.event/v1",)),), "SOAKING"),),
    "SOAKING": (PhaseSpec("complete", (CallSpec("factory-release-controller", "complete-release", "release-rollback-controller", ("rollout.event/v1",)),), "COMPLETED"),),
    "ROLLBACK_REQUIRED": (PhaseSpec("prepare-rollback", (CallSpec("factory-rollback-controller", "prepare-rollback", "release-rollback-controller", ("rollback.plan/v1",)),), "ROLLING_BACK"),),
    "ROLLING_BACK": (PhaseSpec("execute-rollback", (CallSpec("factory-rollback-controller", "execute-rollback", "release-rollback-controller", ("rollback.result/v1",)),), "ROLLED_BACK"),),
}


LEGAL_TRANSITIONS: dict[str, frozenset[str]] = {
    "REQUESTED": frozenset({"SCOPE_RESOLVED", "FAILED", "QUARANTINED", "CANCELLED"}),
    "SCOPE_RESOLVED": frozenset({"INSTRUCTIONS_BOUND", "STALE", "FAILED", "QUARANTINED", "CANCELLED"}),
    "INSTRUCTIONS_BOUND": frozenset({"BASELINE_VERIFIED", "STALE", "FAILED", "QUARANTINED", "CANCELLED"}),
    "BASELINE_VERIFIED": frozenset({"CONTRACT_FROZEN", "STALE", "FAILED", "QUARANTINED", "CANCELLED"}),
    "CONTRACT_FROZEN": frozenset({"IMPLEMENTING", "STALE", "FAILED", "QUARANTINED", "CANCELLED"}),
    "IMPLEMENTING": frozenset({"CHANGESET_FROZEN", "STALE", "REWORKING", "FAILED", "QUARANTINED", "CANCELLED"}),
    "CHANGESET_FROZEN": frozenset({"CANDIDATE_BUILT", "REWORKING", "FAILED", "QUARANTINED", "CANCELLED"}),
    "CANDIDATE_BUILT": frozenset({"CANDIDATE_VERIFIED", "REWORKING", "FAILED", "QUARANTINED", "CANCELLED"}),
    "CANDIDATE_VERIFIED": frozenset({"BOM_SIGNED", "WAITING_EXTERNAL", "FAILED", "QUARANTINED", "CANCELLED"}),
    "BOM_SIGNED": frozenset({"SHADOW", "WAITING_EXTERNAL", "ROLLBACK_REQUIRED", "FAILED", "QUARANTINED", "CANCELLED"}),
    "SHADOW": frozenset({"CANARY", "WAITING_EXTERNAL", "ROLLBACK_REQUIRED", "FAILED", "QUARANTINED", "CANCELLED"}),
    "CANARY": frozenset({"ROLLING", "WAITING_EXTERNAL", "ROLLBACK_REQUIRED", "FAILED", "QUARANTINED", "CANCELLED"}),
    "ROLLING": frozenset({"SOAKING", "WAITING_EXTERNAL", "ROLLBACK_REQUIRED", "FAILED", "QUARANTINED", "CANCELLED"}),
    "SOAKING": frozenset({"COMPLETED", "WAITING_EXTERNAL", "ROLLBACK_REQUIRED", "FAILED", "QUARANTINED", "CANCELLED"}),
    "COMPLETED": frozenset({"ROLLBACK_REQUIRED"}),
    "STALE": frozenset({"REWORKING", "CANCELLED", "QUARANTINED"}),
    "REWORKING": frozenset({"SCOPE_RESOLVED", "INSTRUCTIONS_BOUND", "FAILED", "CANCELLED"}),
    "WAITING_EXTERNAL": frozenset({"CANDIDATE_VERIFIED", "BOM_SIGNED", "SHADOW", "CANARY", "ROLLING", "SOAKING", "ROLLBACK_REQUIRED", "ROLLING_BACK", "FAILED", "QUARANTINED", "CANCELLED"}),
    "ROLLBACK_REQUIRED": frozenset({"ROLLING_BACK", "WAITING_EXTERNAL", "FAILED", "QUARANTINED"}),
    "ROLLING_BACK": frozenset({"ROLLED_BACK", "FAILED", "WAITING_EXTERNAL", "QUARANTINED"}),
    "ROLLED_BACK": frozenset(), "FAILED": frozenset(), "CANCELLED": frozenset(), "QUARANTINED": frozenset(),
}


class TrustedRoleDirectory(Protocol):
    def resolve(self, workflow_id: str, request_sha256: str) -> Mapping[str, Any]: ...


class ModuleAdapter(Protocol):
    def invoke(self, command: Mapping[str, Any]) -> Mapping[str, Any]: ...


class ReceiptTrustVerifier(Protocol):
    def verify(self, receipt: Mapping[str, Any], command: Mapping[str, Any]) -> bool: ...


class ProviderContractVerifier(Protocol):
    def verify(self, contract_id: str, payload: Mapping[str, Any]) -> bool: ...


class ExternalReleaseAuthority(Protocol):
    def verify_signed_bom(self, workflow_id: str, request_sha256: str, external_context_ref: str | None, mode: str) -> Mapping[str, Any] | None: ...
    def verify_human_transition(self, workflow_id: str, request_sha256: str, external_context_ref: str | None, risk_tier: str, from_state: str, to_state: str, role_identities: Sequence[str]) -> Mapping[str, Any] | None: ...
    def verify_rollback_authorization(self, workflow_id: str, request_sha256: str, external_context_ref: str | None, mode: str, reason_code: str, previous_stable_bom_sha256: str) -> Mapping[str, Any] | None: ...


class RuntimeControlAuthority(Protocol):
    def allows(self, operation: str, workflow_id: str) -> bool: ...
    def execute_if_allowed(self, operation: str, workflow_id: str, mutation: Callable[[], _GuardedResult]) -> tuple[bool, _GuardedResult | None]: ...


class StaticRuntimeControlAuthority:
    """Linearizable test/local control; production must inject a durable equivalent."""

    def __init__(self, *, feature_enabled: bool = True, kill_switch_armed: bool = False) -> None:
        self._lock = threading.RLock()
        self._feature_enabled = bool(feature_enabled)
        self._kill_switch_armed = bool(kill_switch_armed)

    @property
    def feature_enabled(self) -> bool:
        with self._lock:
            return self._feature_enabled

    @feature_enabled.setter
    def feature_enabled(self, value: bool) -> None:
        with self._lock:
            self._feature_enabled = bool(value)

    @property
    def kill_switch_armed(self) -> bool:
        with self._lock:
            return self._kill_switch_armed

    @kill_switch_armed.setter
    def kill_switch_armed(self, value: bool) -> None:
        with self._lock:
            self._kill_switch_armed = bool(value)

    def _allows_unlocked(self, operation: str, workflow_id: str) -> bool:
        return bool(
            operation in {"START", "ACQUIRE_FENCE", "CONTINUE", "PROVIDER_INVOKE"}
            and workflow_id
            and self._feature_enabled
            and not self._kill_switch_armed
        )

    def allows(self, operation: str, workflow_id: str) -> bool:
        with self._lock:
            return self._allows_unlocked(operation, workflow_id)

    def execute_if_allowed(
        self,
        operation: str,
        workflow_id: str,
        mutation: Callable[[], _GuardedResult],
    ) -> tuple[bool, _GuardedResult | None]:
        """Linearize kill-switch activation and the protected repository write."""
        with self._lock:
            if not self._allows_unlocked(operation, workflow_id):
                return False, None
            return True, mutation()


class WorkflowRepository(Protocol):
    def register(self, request: Mapping[str, Any], request_sha256: str, role_binding: Mapping[str, Any]) -> bool: ...
    def request(self, workflow_id: str) -> dict[str, Any]: ...
    def role_binding(self, workflow_id: str) -> dict[str, Any]: ...
    def acquire_fence(self, workflow_id: str, worker_identity: str, occurred_at: str) -> int: ...
    def acquire_fence_if_state(self, workflow_id: str, worker_identity: str, occurred_at: str, allowed_states: Sequence[str]) -> int: ...
    def events(self, workflow_id: str) -> list[dict[str, Any]]: ...
    def receipts(self, workflow_id: str) -> list[dict[str, Any]]: ...
    def pending_messages(self, workflow_id: str) -> list[dict[str, Any]]: ...
    def schedule_phase(self, workflow_id: str, state: str, activation_sequence: int, phase: str, messages: Sequence[Mapping[str, Any]], fence: int, occurred_at: str) -> str: ...
    def stage_for_phase(self, workflow_id: str, state: str, activation_sequence: int, phase: str) -> str | None: ...
    def stage_receipts(self, workflow_id: str, stage_id: str) -> list[dict[str, Any]]: ...
    def record_attempt(self, workflow_id: str, request_id: str, command_sha256: str, fence: int, occurred_at: str) -> None: ...
    def record_receipt(self, workflow_id: str, request_id: str, receipt: Mapping[str, Any], fence: int, occurred_at: str) -> bool: ...
    def register_native_stop_authority_trust(self, workflow_id: str, fact: Mapping[str, Any], fence: int, occurred_at: str) -> bool: ...
    def native_stop_authority_trust(self, receipt_id: str) -> dict[str, Any] | None: ...
    def append_phase_completed(self, workflow_id: str, state: str, activation_sequence: int, phase: str, fence: int, occurred_at: str) -> None: ...
    def transition(self, workflow_id: str, state: str, event_type: str, payload: Mapping[str, Any], idempotency_key: str, fence: int, occurred_at: str) -> dict[str, Any]: ...
    def quarantine(self, workflow_id: str, reason: str, digest: str, fence: int, occurred_at: str) -> None: ...
    def quarantine_records(self, workflow_id: str) -> list[dict[str, Any]]: ...
    def latest_fence(self, workflow_id: str) -> int: ...


def validate_workflow_request(raw: Mapping[str, Any]) -> dict[str, Any]:
    expected = {
        "schema_version", "contract_id", "producer_module", "soul_id", "device_binding_id",
        "platform_account_id", "trace_id", "idempotency_key", "occurred_at", "privacy_class",
        "workflow_id", "mode", "risk_tier", "baseline_commit", "target_modules",
        "requested_paths", "public_contract_changes", "external_context_ref",
    }
    if not isinstance(raw, Mapping) or set(raw) != expected:
        raise InvalidWorkflowRequest("workflow request has unknown or missing fields")
    raw_targets = raw.get("target_modules")
    raw_paths = raw.get("requested_paths")
    raw_contracts = raw.get("public_contract_changes")
    if (
        not isinstance(raw_targets, list)
        or not 1 <= len(raw_targets) <= MAX_TARGET_MODULES
        or any(not isinstance(item, str) or len(item) > 64 for item in raw_targets)
    ):
        raise InvalidWorkflowRequest("target_modules exceeds its resource boundary")
    if (
        not isinstance(raw_paths, list)
        or not 1 <= len(raw_paths) <= MAX_REQUESTED_PATHS
        or any(not isinstance(item, str) or len(item) > MAX_REPOSITORY_PATH_LENGTH for item in raw_paths)
    ):
        raise InvalidWorkflowRequest("requested_paths exceeds its resource boundary")
    if (
        not isinstance(raw_contracts, list)
        or len(raw_contracts) > MAX_PUBLIC_CONTRACT_CHANGES
        or any(not isinstance(item, str) or len(item) > 128 for item in raw_contracts)
    ):
        raise InvalidWorkflowRequest("public_contract_changes exceeds its resource boundary")
    if len(canonical_bytes(dict(raw))) > MAX_WORKFLOW_REQUEST_BYTES:
        raise InvalidWorkflowRequest("workflow request exceeds its canonical byte limit")
    value = copy.deepcopy(dict(raw))
    if value["schema_version"] != "1.0.0" or value["contract_id"] != "factory.workflow.request/v1" or value["producer_module"] != "factory-control-plane-host":
        raise InvalidWorkflowRequest("unknown workflow request contract or producer")
    if value["privacy_class"] != "internal" or value["mode"] not in {"SIMULATION", "PRODUCTION"}:
        raise InvalidWorkflowRequest("workflow privacy class or mode is invalid")
    if value["risk_tier"] not in {"R0", "R1", "R2", "R3", "R4"}:
        raise InvalidWorkflowRequest("unknown risk tier")
    if value["risk_tier"] == "R4":
        raise InvalidWorkflowRequest("R4 workflows are always rejected")
    if not re.fullmatch(r"upgrade:[A-Za-z0-9][A-Za-z0-9._-]{7,119}", str(value["workflow_id"])):
        raise InvalidWorkflowRequest("workflow_id is invalid")
    if not re.fullmatch(r"[0-9a-f]{40}", str(value["baseline_commit"])):
        raise InvalidWorkflowRequest("baseline_commit is invalid")
    if not isinstance(value["trace_id"], str) or not re.fullmatch(r"trace_[a-f0-9]{32}", value["trace_id"]):
        raise InvalidWorkflowRequest("trace_id is not a canonical opaque identifier")
    if not isinstance(value["idempotency_key"], str) or not re.fullmatch(r"idem_[a-f0-9]{64}", value["idempotency_key"]):
        raise InvalidWorkflowRequest("idempotency_key is not a canonical opaque identifier")
    _timestamp(value["occurred_at"], "occurred_at")
    patterns = {
        "soul_id": r"soul_[a-f0-9]{64}", "device_binding_id": r"db_[a-f0-9]{32}",
        "platform_account_id": r"pa_[a-f0-9]{32}",
    }
    for name, pattern in patterns.items():
        if value[name] is not None and not re.fullmatch(pattern, str(value[name])):
            raise InvalidWorkflowRequest(name + " is not an opaque canonical identifier")
    targets = value["target_modules"]
    paths = value["requested_paths"]
    contracts = value["public_contract_changes"]
    if len(targets) != len(set(targets)) or any(not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", item) for item in targets):
        raise InvalidWorkflowRequest("target_modules is invalid")
    path_keys = [canonical_repository_path_key(path) for path in paths]
    if len(path_keys) != len(set(path_keys)):
        raise InvalidWorkflowRequest("requested_paths is invalid")
    seen_owners: set[str] = set()
    for path in paths:
        parts = path.split("/")
        if len(parts) < 3 or parts[0] != "Modules" or parts[1] not in targets:
            raise InvalidWorkflowRequest("requested path is outside declared module scope")
        owned_path_class(path)
        seen_owners.add(parts[1])
    if seen_owners != set(targets):
        raise InvalidWorkflowRequest("each target module must own a requested path")
    if len(contracts) != len(set(contracts)) or any(not re.fullmatch(r"[a-z][a-z0-9]*(?:\.[a-z0-9]+)+", item) for item in contracts):
        raise InvalidWorkflowRequest("public_contract_changes is invalid")
    contract_paths = [path for path in paths if owned_path_class(path) == "contracts"]
    if bool(contract_paths) is not bool(contracts):
        raise InvalidWorkflowRequest("contract paths and declared public contract changes must coexist")
    external_ref = value["external_context_ref"]
    if external_ref is not None and (not isinstance(external_ref, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{7,127}", external_ref)):
        raise InvalidWorkflowRequest("external_context_ref is invalid")
    value["target_modules"] = sorted(targets)
    value["requested_paths"] = sorted(paths)
    value["public_contract_changes"] = sorted(contracts)
    return value


def validate_role_binding(workflow_id: str, raw: Mapping[str, Any], request: Mapping[str, Any] | None = None) -> dict[str, Any]:
    expected = {"verified", "policy_sha256", "verifier_identity", "verified_at", "roles", "verification_ref"}
    if not isinstance(raw, Mapping) or set(raw) != expected or raw.get("verified") is not True:
        raise RoleSeparationError("role directory did not return an exact verified record")
    roles = raw.get("roles")
    if not isinstance(roles, Mapping) or set(roles) != set(ROLES):
        raise RoleSeparationError("all nine Factory roles are required")
    identities = []
    for role in ROLES:
        identity = roles[role]
        if not isinstance(identity, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,127}", identity):
            raise RoleSeparationError("role identity is invalid: " + role)
        identities.append(identity)
    if len(set(identities)) != len(identities):
        raise RoleSeparationError("all nine Factory role identities must be pairwise distinct")
    policy = raw.get("policy_sha256")
    if not isinstance(policy, str) or not re.fullmatch(r"[0-9a-f]{64}", policy):
        raise RoleSeparationError("role policy digest is invalid")
    verifier_identity = raw.get("verifier_identity")
    verification_ref = raw.get("verification_ref")
    if not isinstance(verifier_identity, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,127}", verifier_identity):
        raise RoleSeparationError("role verifier identity is invalid")
    if not isinstance(verification_ref, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{7,255}", verification_ref):
        raise RoleSeparationError("role verification reference is invalid")
    _timestamp(raw.get("verified_at"), "verified_at")
    material = {"workflow_id": workflow_id, "policy_sha256": policy, "roles": dict(roles), "verification_ref": raw.get("verification_ref")}
    envelope = request or {
        "soul_id": None, "device_binding_id": None, "platform_account_id": None,
        "trace_id": "trace_" + sha256(workflow_id)[:32],
    }
    return {
        "schema_version": "1.0.0", "contract_id": "factory.role.binding/v1",
        "producer_module": "factory-control-plane-host",
        "soul_id": envelope.get("soul_id"),
        "device_binding_id": envelope.get("device_binding_id"),
        "platform_account_id": envelope.get("platform_account_id"),
        "trace_id": envelope["trace_id"],
        "idempotency_key": "idem_" + sha256({"workflow_id": workflow_id, "kind": "role-binding"}),
        "occurred_at": raw["verified_at"], "privacy_class": "internal",
        "workflow_id": workflow_id,
        "binding_id": "roles:" + sha256(material)[:32], "policy_sha256": policy,
        "verifier_identity": verifier_identity, "verified_at": raw["verified_at"],
        "roles": {role: roles[role] for role in ROLES},
    }


def _event_material(event: Mapping[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in event.items() if key != "event_sha256"}


def validate_event_stream(events: Sequence[Mapping[str, Any]]) -> None:
    previous = ZERO_HASH
    for index, event in enumerate(events, start=1):
        if event.get("sequence") != index or event.get("previous_event_sha256") != previous:
            raise CorruptWorkflow("workflow event sequence or previous hash is corrupt")
        if event.get("payload_sha256") != sha256(event.get("payload")):
            raise CorruptWorkflow("workflow event payload hash is corrupt")
        if event.get("event_sha256") != sha256(_event_material(event)):
            raise CorruptWorkflow("workflow event hash is corrupt")
        previous = str(event["event_sha256"])


class InMemoryWorkflowRepository:
    """Thread-safe append-only repository used only for deterministic tests."""

    def __init__(self) -> None:
        self._lock = threading.RLock()
        self._requests: dict[str, dict[str, Any]] = {}
        self._request_hashes: dict[str, str] = {}
        self._roles: dict[str, dict[str, Any]] = {}
        self._events: dict[str, list[dict[str, Any]]] = {}
        self._fences: dict[str, list[dict[str, Any]]] = {}
        self._messages: dict[str, dict[str, dict[str, Any]]] = {}
        self._deliveries: dict[str, list[dict[str, Any]]] = {}
        self._receipts: dict[str, dict[str, dict[str, Any]]] = {}
        self._quarantine: dict[str, list[dict[str, Any]]] = {}
        self._native_stop_trust_bindings: dict[str, dict[str, Any]] = {}

    def register(self, request: Mapping[str, Any], request_sha256: str, role_binding: Mapping[str, Any]) -> bool:
        workflow_id = str(request["workflow_id"])
        with self._lock:
            if workflow_id in self._requests:
                if self._request_hashes[workflow_id] != request_sha256 or self._requests[workflow_id]["idempotency_key"] != request["idempotency_key"]:
                    self._quarantine.setdefault(workflow_id, []).append({"reason": "WORKFLOW_ID_HASH_CONFLICT", "digest": request_sha256, "occurred_at": request["occurred_at"]})
                    raise IdempotencyConflict("workflow identity is already bound to different content")
                return False
            self._requests[workflow_id] = copy.deepcopy(dict(request))
            self._request_hashes[workflow_id] = request_sha256
            self._roles[workflow_id] = copy.deepcopy(dict(role_binding))
            self._events[workflow_id] = []
            self._fences[workflow_id] = []
            self._messages[workflow_id] = {}
            self._deliveries[workflow_id] = []
            self._receipts[workflow_id] = {}
            self._quarantine[workflow_id] = []
            self._append_event(workflow_id, "WORKFLOW_REQUESTED", "REQUESTED", {"request_sha256": request_sha256, "role_binding_id": role_binding["binding_id"]}, "requested:" + request["idempotency_key"], 0, request["occurred_at"])
            return True

    def request(self, workflow_id: str) -> dict[str, Any]:
        with self._lock:
            if workflow_id not in self._requests:
                raise FactoryHostError("unknown workflow")
            return copy.deepcopy(self._requests[workflow_id])

    def request_sha256(self, workflow_id: str) -> str:
        with self._lock:
            return self._request_hashes[workflow_id]

    def role_binding(self, workflow_id: str) -> dict[str, Any]:
        with self._lock:
            return copy.deepcopy(self._roles[workflow_id])

    def acquire_fence(self, workflow_id: str, worker_identity: str, occurred_at: str) -> int:
        if not worker_identity or len(worker_identity) > 128:
            raise FactoryHostError("worker identity is invalid")
        with self._lock:
            token = len(self._fences[workflow_id]) + 1
            self._fences[workflow_id].append({"token": token, "worker_identity": worker_identity, "occurred_at": occurred_at})
            return token

    def acquire_fence_if_state(self, workflow_id: str, worker_identity: str, occurred_at: str, allowed_states: Sequence[str]) -> int:
        if not worker_identity or len(worker_identity) > 128:
            raise FactoryHostError("worker identity is invalid")
        allowed = frozenset(allowed_states)
        with self._lock:
            state, _activation, _reason = _current_state(self._events[workflow_id])
            if state not in allowed:
                raise IllegalTransition("workflow state does not allow this management operation: " + state)
            token = len(self._fences[workflow_id]) + 1
            self._fences[workflow_id].append({"token": token, "worker_identity": worker_identity, "occurred_at": occurred_at})
            return token

    def latest_fence(self, workflow_id: str) -> int:
        with self._lock:
            rows = self._fences[workflow_id]
            return rows[-1]["token"] if rows else 0

    def _assert_fence(self, workflow_id: str, fence: int) -> None:
        if fence <= 0 or fence != self.latest_fence(workflow_id):
            raise StaleFence("worker fencing token is stale")

    def events(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._lock:
            result = copy.deepcopy(self._events[workflow_id])
        validate_event_stream(result)
        return result

    def receipts(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._lock:
            return [copy.deepcopy(value) for value in self._receipts[workflow_id].values()]

    def pending_messages(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._lock:
            acknowledged = set(self._receipts[workflow_id])
            return [copy.deepcopy(value) for key, value in sorted(self._messages[workflow_id].items()) if key not in acknowledged]

    def schedule_phase(self, workflow_id: str, state: str, activation_sequence: int, phase: str, messages: Sequence[Mapping[str, Any]], fence: int, occurred_at: str) -> str:
        with self._lock:
            self._assert_fence(workflow_id, fence)
            existing = self.stage_for_phase(workflow_id, state, activation_sequence, phase)
            if existing is not None:
                return existing
            stage_id = "stage:" + sha256({"workflow_id": workflow_id, "state": state, "activation_sequence": activation_sequence, "phase": phase})[:32]
            prepared: list[dict[str, Any]] = []
            for raw in messages:
                message = copy.deepcopy(dict(raw))
                message["stage_id"] = stage_id
                request_id = "call:" + sha256({
                    "stage_id": stage_id, "target": message["target_module"],
                    "operation": message["operation"], "role": message["actor_role"],
                    "subject_module": message.get("context", {}).get("subject_module"),
                })[:32]
                message["request_id"] = request_id
                if request_id in self._messages[workflow_id]:
                    raise IdempotencyConflict("outbox request id collision")
                prepared.append(message)
            self._append_event(workflow_id, "STAGE_SCHEDULED", state, {"activation_sequence": activation_sequence, "phase": phase, "stage_id": stage_id, "request_ids": [item["request_id"] for item in prepared]}, "schedule:" + stage_id, fence, occurred_at)
            for message in prepared:
                self._messages[workflow_id][message["request_id"]] = message
            return stage_id

    def stage_for_phase(self, workflow_id: str, state: str, activation_sequence: int, phase: str) -> str | None:
        with self._lock:
            for event in self._events[workflow_id]:
                payload = event["payload"]
                if event["event_type"] == "STAGE_SCHEDULED" and event["state"] == state and payload.get("activation_sequence") == activation_sequence and payload.get("phase") == phase:
                    return str(payload["stage_id"])
            return None

    def stage_receipts(self, workflow_id: str, stage_id: str) -> list[dict[str, Any]]:
        with self._lock:
            request_ids = {key for key, value in self._messages[workflow_id].items() if value["stage_id"] == stage_id}
            return [copy.deepcopy(self._receipts[workflow_id][key]) for key in sorted(request_ids) if key in self._receipts[workflow_id]]

    def record_attempt(self, workflow_id: str, request_id: str, command_sha256: str, fence: int, occurred_at: str) -> None:
        with self._lock:
            self._assert_fence(workflow_id, fence)
            if request_id not in self._messages[workflow_id]:
                raise FactoryHostError("unknown outbox request")
            self._deliveries[workflow_id].append({"request_id": request_id, "status": "ATTEMPTED", "command_sha256": command_sha256, "fencing_token": fence, "occurred_at": occurred_at})

    def record_receipt(self, workflow_id: str, request_id: str, receipt: Mapping[str, Any], fence: int, occurred_at: str) -> bool:
        with self._lock:
            self._assert_fence(workflow_id, fence)
            if request_id not in self._messages[workflow_id]:
                raise FactoryHostError("unknown outbox request")
            receipt_hash = sha256(dict(receipt))
            existing = self._receipts[workflow_id].get(request_id)
            if existing is not None:
                existing_unsigned = {key: value for key, value in existing.items() if key != "receipt_id"}
                if sha256(existing_unsigned) != receipt_hash:
                    self._quarantine[workflow_id].append({"reason": "RECEIPT_HASH_CONFLICT", "digest": receipt_hash, "occurred_at": occurred_at})
                    raise IdempotencyConflict("provider returned conflicting content for one request")
                return False
            stored = copy.deepcopy(dict(receipt))
            stored["receipt_id"] = "module-receipt:" + receipt_hash[:32]
            self._receipts[workflow_id][request_id] = stored
            self._deliveries[workflow_id].append({"request_id": request_id, "status": "ACKNOWLEDGED", "receipt_sha256": receipt_hash, "fencing_token": fence, "occurred_at": occurred_at})
            return True

    def register_native_stop_authority_trust(
        self,
        workflow_id: str,
        fact: Mapping[str, Any],
        fence: int,
        occurred_at: str,
    ) -> bool:
        value = validate_native_stop_trust_durable_fact(fact)
        receipt_id = str(value["receipt_id"])
        with self._lock:
            self._assert_fence(workflow_id, fence)
            existing = self._native_stop_trust_bindings.get(receipt_id)
            if existing is not None:
                existing = validate_native_stop_trust_durable_fact(existing)
                if existing["receipt_sha256"] != value["receipt_sha256"]:
                    digest = sha256({
                        "receipt_id": receipt_id,
                        "bound_receipt_sha256": existing["receipt_sha256"],
                        "conflicting_receipt_sha256": value["receipt_sha256"],
                    })
                    self._quarantine[workflow_id].append({
                        "reason": "NATIVE_STOP_TRUST_RECEIPT_HASH_CONFLICT",
                        "digest": digest,
                        "occurred_at": occurred_at,
                        "fencing_token": fence,
                    })
                    raise IdempotencyConflict(
                        "native-stop trust receipt id is globally bound to different bytes",
                    )
                return False
            self._native_stop_trust_bindings[receipt_id] = copy.deepcopy(value)
            return True

    def native_stop_authority_trust(
        self, receipt_id: str,
    ) -> dict[str, Any] | None:
        with self._lock:
            value = self._native_stop_trust_bindings.get(receipt_id)
            return None if value is None else copy.deepcopy(
                validate_native_stop_trust_durable_fact(value),
            )

    def append_phase_completed(self, workflow_id: str, state: str, activation_sequence: int, phase: str, fence: int, occurred_at: str) -> None:
        with self._lock:
            self._assert_fence(workflow_id, fence)
            key = "phase:%s:%s:%s" % (state, activation_sequence, phase)
            if any(event["idempotency_key"] == opaque_idempotency(key) for event in self._events[workflow_id]):
                return
            self._append_event(workflow_id, "PHASE_COMPLETED", state, {"activation_sequence": activation_sequence, "phase": phase}, key, fence, occurred_at)

    def transition(self, workflow_id: str, state: str, event_type: str, payload: Mapping[str, Any], idempotency_key: str, fence: int, occurred_at: str) -> dict[str, Any]:
        with self._lock:
            self._assert_fence(workflow_id, fence)
            idempotency_key = opaque_idempotency(idempotency_key)
            for event in self._events[workflow_id]:
                if event["idempotency_key"] == idempotency_key:
                    if event["state"] != state or event["payload_sha256"] != sha256(dict(payload)):
                        self._quarantine[workflow_id].append({
                            "reason": "TRANSITION_IDEMPOTENCY_CONFLICT",
                            "digest": sha256({"state": state, "payload": dict(payload)}),
                            "occurred_at": occurred_at,
                            "fencing_token": fence,
                        })
                        raise IdempotencyConflict("transition idempotency key is bound to different content")
                    return copy.deepcopy(event)
            return copy.deepcopy(self._append_event(workflow_id, event_type, state, payload, idempotency_key, fence, occurred_at))

    def quarantine(self, workflow_id: str, reason: str, digest: str, fence: int, occurred_at: str) -> None:
        with self._lock:
            self._assert_fence(workflow_id, fence)
            self._quarantine[workflow_id].append({"reason": reason, "digest": digest, "occurred_at": occurred_at, "fencing_token": fence})

    def quarantine_records(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._lock:
            return copy.deepcopy(self._quarantine[workflow_id])

    def _append_event(self, workflow_id: str, event_type: str, state: str, payload: Mapping[str, Any], idempotency_key: str, fence: int, occurred_at: str) -> dict[str, Any]:
        idempotency_key = opaque_idempotency(idempotency_key)
        request = self._requests[workflow_id]
        events = self._events[workflow_id]
        sequence = len(events) + 1
        previous = events[-1]["event_sha256"] if events else ZERO_HASH
        body = {
            "schema_version": "1.0.0", "contract_id": "factory.workflow.event/v1",
            "producer_module": "factory-control-plane-host",
            "soul_id": request["soul_id"], "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"], "trace_id": request["trace_id"],
            "privacy_class": "internal", "workflow_id": workflow_id,
            "sequence": sequence, "event_id": "workflow-event:" + sha256({"workflow_id": workflow_id, "sequence": sequence, "idempotency_key": idempotency_key})[:32],
            "event_type": event_type, "state": state, "fencing_token": fence,
            "idempotency_key": idempotency_key, "payload": copy.deepcopy(dict(payload)),
            "payload_sha256": sha256(dict(payload)), "previous_event_sha256": previous,
            "occurred_at": occurred_at,
        }
        body["event_sha256"] = sha256(body)
        events.append(body)
        return body


class SimulationReceiptVerifier:
    def verify(self, receipt: Mapping[str, Any], command: Mapping[str, Any]) -> bool:
        return (
            command.get("mode") == "SIMULATION"
            and receipt.get("mode") == "SIMULATION"
            and receipt.get("simulation_only") is True
            and receipt.get("evidence_kind") == "SIMULATION"
            and receipt.get("verification_level") == "INTEGRATION_VERIFIED"
            and receipt.get("side_effect_count") == 0
            and isinstance(receipt.get("attestation"), Mapping)
            and receipt["attestation"].get("kind") == "SIMULATION_ONLY"
        )


def _current_state(events: Sequence[Mapping[str, Any]]) -> tuple[str, int, str | None]:
    state = "REQUESTED"
    activation = 1
    waiting_reason: str | None = None
    for event in events:
        if event["event_type"] in {"WORKFLOW_REQUESTED", "STATE_TRANSITIONED", "WAITING_EXTERNAL", "ROLLBACK_REQUESTED", "WORKFLOW_QUARANTINED"}:
            state = str(event["state"])
            activation = int(event["sequence"])
            waiting_reason = event["payload"].get("reason") if state == "WAITING_EXTERNAL" else None
    return state, activation, waiting_reason


class FactoryControlPlaneHost:
    def __init__(
        self,
        repository: WorkflowRepository,
        role_directory: TrustedRoleDirectory,
        module_adapter: ModuleAdapter,
        receipt_verifier: ReceiptTrustVerifier,
        provider_contract_verifier: ProviderContractVerifier,
        external_authority: ExternalReleaseAuthority,
        runtime_control: RuntimeControlAuthority,
        clock: Callable[[], dt.datetime] | None = None,
        native_stop_authority_trust: NativeStopAuthorityTrustAuthority | None = None,
    ) -> None:
        self._repository = repository
        self._role_directory = role_directory
        self._adapter = module_adapter
        self._receipt_verifier = receipt_verifier
        self._provider_contract_verifier = provider_contract_verifier
        self._external = external_authority
        self._runtime_control = runtime_control
        self._clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))
        if (
            native_stop_authority_trust is not None
            and type(native_stop_authority_trust) is not NativeStopAuthorityTrustAuthority
        ):
            raise TypeError(
                "native-stop trust must be the fixed composition-root authority",
            )
        self._native_stop_authority_trust = native_stop_authority_trust

    def _now(self) -> dt.datetime:
        value = self._clock()
        if not isinstance(value, dt.datetime) or value.tzinfo is None:
            raise FactoryHostError("trusted Factory clock must return a timezone-aware datetime")
        return value.astimezone(dt.timezone.utc)

    def _assert_runtime_control(self, operation: str, workflow_id: str) -> None:
        if self._runtime_control.allows(operation, workflow_id) is not True:
            raise FactoryHostError("runtime feature flag or kill switch denied " + operation)

    def _guarded_repository_write(
        self,
        operation: str,
        workflow_id: str,
        mutation: Callable[[], _GuardedResult],
    ) -> _GuardedResult | None:
        """Require the control authority to linearize a mutation with revocation."""
        executed, result = self._runtime_control.execute_if_allowed(
            operation, workflow_id, mutation,
        )
        if executed is not True:
            raise FactoryHostError("runtime feature flag or kill switch denied " + operation)
        return result

    def _acquire_fence(self, workflow_id: str, worker_identity: str) -> int:
        result = self._guarded_repository_write(
            "ACQUIRE_FENCE", workflow_id,
            lambda: self._repository.acquire_fence(workflow_id, worker_identity, utc_now()),
        )
        if not isinstance(result, int):
            raise FactoryHostError("runtime control returned an invalid fence")
        return result

    def _acquire_fence_if_state(self, workflow_id: str, worker_identity: str, allowed_states: Iterable[str]) -> int:
        result = self._guarded_repository_write(
            "ACQUIRE_FENCE", workflow_id,
            lambda: self._repository.acquire_fence_if_state(
                workflow_id, worker_identity, utc_now(), tuple(sorted(set(allowed_states))),
            ),
        )
        if not isinstance(result, int):
            raise FactoryHostError("runtime control returned an invalid conditional fence")
        return result

    def start(self, raw_request: Mapping[str, Any]) -> dict[str, Any]:
        request = validate_workflow_request(raw_request)
        request_hash = sha256(request)
        trusted_roles = self._role_directory.resolve(request["workflow_id"], request_hash)
        binding = validate_role_binding(request["workflow_id"], trusted_roles, request)
        self._guarded_repository_write(
            "START", request["workflow_id"],
            lambda: self._repository.register(request, request_hash, binding),
        )
        return self.status(request["workflow_id"])

    def status(self, workflow_id: str) -> dict[str, Any]:
        request = self._repository.request(workflow_id)
        events = self._repository.events(workflow_id)
        state, _activation, waiting = _current_state(events)
        receipts = self._repository.receipts(workflow_id)
        binding = self._repository.role_binding(workflow_id)
        request_hash = sha256(request)
        mode = request["mode"]
        verification_ceiling = (
            "INTEGRATION_VERIFIED"
            if mode == "SIMULATION"
            else self._production_evidence_ceiling(state, receipts)
        )
        production_authorized = False
        if mode == "PRODUCTION" and state in {"CANARY", "ROLLING", "SOAKING", "COMPLETED"}:
            required = "DEVICE_VERIFIED" if state == "CANARY" else "CANARY_VERIFIED"
            signed_bom = self._bound_external_fact(events, "SIGNED_BOM")
            approval_scopes = self._required_human_scopes_through_state(request, state)
            approvals_bound = all(
                self._bound_external_fact(events, self._human_fact_kind(*scope)) is not None
                for scope in approval_scopes
            )
            native_stop_trust_bound = False
            native_stop_fact = self._bound_external_fact(
                events, NATIVE_STOP_AUTHORITY_TRUST_FACT,
            )
            if native_stop_fact is not None and self._native_stop_authority_trust is not None:
                try:
                    self._validate_native_stop_authority_trust_fact(
                        request, native_stop_fact, events,
                    )
                    native_stop_trust_bound = True
                except (NativeStopAuthorityTrustError, ReceiptRejected, ExternalFactExpired):
                    native_stop_trust_bound = False
            production_authorized = bool(
                VERIFICATION_LEVELS.index(verification_ceiling) >= VERIFICATION_LEVELS.index(required)
                and signed_bom is not None
                and approvals_bound
                and native_stop_trust_bound
            )
        return {
            "schema_version": "1.0.0", "contract_id": "factory.workflow.status/v1",
            "producer_module": "factory-control-plane-host",
            "soul_id": request["soul_id"], "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"], "trace_id": request["trace_id"],
            "idempotency_key": "idem_" + sha256({"workflow_id": workflow_id, "state": state, "sequence": events[-1]["sequence"], "kind": "status"}),
            "occurred_at": events[-1]["occurred_at"], "privacy_class": "internal",
            "workflow_id": workflow_id,
            "request_sha256": request_hash, "state": state, "sequence": events[-1]["sequence"],
            "fencing_token": self._repository.latest_fence(workflow_id), "mode": mode,
            "risk_tier": request["risk_tier"], "simulation_only": mode == "SIMULATION",
            "production_authorized": production_authorized,
            "verification_ceiling": verification_ceiling,
            "role_binding_id": binding["binding_id"], "pending_outbox": len(self._repository.pending_messages(workflow_id)),
            "receipt_ids": sorted(receipt["receipt_id"] for receipt in receipts),
            "waiting_reason": waiting, "updated_at": events[-1]["occurred_at"],
        }

    @staticmethod
    def _production_evidence_ceiling(state: str, receipts: Sequence[Mapping[str, Any]]) -> str:
        achieved = "REPOSITORY_STATIC_VERIFIED"
        for receipt in receipts:
            if receipt.get("status") != "PASS":
                continue
            kind = receipt.get("evidence_kind")
            level = receipt.get("verification_level")
            if (
                receipt.get("mode") != "PRODUCTION"
                or receipt.get("simulation_only") is not False
                or kind == "SIMULATION"
                or EVIDENCE_LEVEL_BY_KIND.get(str(kind)) != level
            ):
                raise CorruptWorkflow("production status encountered an invalid evidence receipt")
            if VERIFICATION_LEVELS.index(str(level)) > VERIFICATION_LEVELS.index(achieved):
                achieved = str(level)
        cap = PRODUCTION_STATE_CAP.get(state)
        if cap is None:
            raise CorruptWorkflow("production workflow state has no evidence cap")
        return VERIFICATION_LEVELS[min(
            VERIFICATION_LEVELS.index(achieved), VERIFICATION_LEVELS.index(cap),
        )]

    @staticmethod
    def _human_fact_kind(from_state: str, to_state: str) -> str:
        return "HUMAN_TRANSITION_APPROVAL:%s:%s" % (from_state, to_state)

    @staticmethod
    def _required_human_scope(
        request: Mapping[str, Any],
        state: str,
    ) -> tuple[str, str] | None:
        if request["mode"] != "PRODUCTION":
            return None
        target = ROLLOUT_TRANSITIONS.get(state)
        if target is None:
            return None
        if request["risk_tier"] == "R3":
            return state, target
        if request["risk_tier"] == "R2" and state == "SHADOW":
            return state, target
        return None

    @classmethod
    def _required_human_scopes_through_state(
        cls,
        request: Mapping[str, Any],
        current_state: str,
    ) -> tuple[tuple[str, str], ...]:
        if request["mode"] != "PRODUCTION" or request["risk_tier"] not in {"R2", "R3"}:
            return ()
        ordered = tuple(ROLLOUT_TRANSITIONS.items())
        result: list[tuple[str, str]] = []
        for source, target in ordered:
            if request["risk_tier"] == "R3" or source == "SHADOW":
                result.append((source, target))
            if target == current_state:
                break
        return tuple(result)

    def run_until_blocked(self, workflow_id: str, worker_identity: str, *, maximum_steps: int = 100) -> dict[str, Any]:
        fence = self._acquire_fence(workflow_id, worker_identity)
        for _ in range(maximum_steps):
            self._assert_runtime_control("CONTINUE", workflow_id)
            before = self.status(workflow_id)
            if self._quarantine_if_needed(workflow_id, before["state"], fence):
                return self.status(workflow_id)
            if before["state"] in TERMINAL_STATES or before["state"] == "WAITING_EXTERNAL":
                return before
            progressed = self._tick(workflow_id, fence)
            after = self.status(workflow_id)
            if not progressed or after["state"] == "WAITING_EXTERNAL":
                return after
        raise FactoryHostError("maximum orchestration steps exceeded")

    def _quarantine_if_needed(self, workflow_id: str, state: str, fence: int) -> bool:
        records = self._repository.quarantine_records(workflow_id)
        if not records or state in TERMINAL_STATES:
            return False
        digest = sha256(records)
        self._transition(
            workflow_id, state, "QUARANTINED",
            {"reason": "CONFLICTING_IMMUTABLE_FACT", "quarantine_sha256": digest},
            "quarantine:" + digest, fence, event_type="WORKFLOW_QUARANTINED",
        )
        return True

    def _tick(self, workflow_id: str, fence: int) -> bool:
        self._assert_runtime_control("CONTINUE", workflow_id)
        request = self._repository.request(workflow_id)
        events = self._repository.events(workflow_id)
        state, activation, _waiting = _current_state(events)
        phases = PHASES.get(state)
        if not phases:
            return False

        phase = next((item for item in phases if not self._phase_completed(events, state, activation, item.name)), None)
        if phase is None:
            final = phases[-1]
            if final.transition_to is None:
                return False
            self._transition(workflow_id, state, final.transition_to, {"completed_phase": final.name}, "transition:%s:%s" % (activation, final.transition_to), fence)
            return True

        for fact_kind in self._required_fact_kinds(request, state):
            external_fact = self._bound_external_fact(events, fact_kind)
            if external_fact is not None:
                try:
                    external_fact = self._validate_external_fact(
                        request, fact_kind, external_fact, events,
                    )
                except ExternalFactExpired:
                    external_fact = None
            if external_fact is None:
                if (
                    state == "ROLLING_BACK"
                    and self._bound_external_fact(events, fact_kind) is None
                ):
                    raise CorruptWorkflow("rolling rollback lost its bound authorization fact")
                external_fact = self._obtain_external_fact(request, state, fact_kind)
                if external_fact is None:
                    self._transition(
                        workflow_id, state, "WAITING_EXTERNAL",
                        {
                            "resume_state": state,
                            "reason": self._external_reason(request, state, fact_kind),
                        },
                        "waiting:%s:%s:%s" % (activation, state, fact_kind),
                        fence, event_type="WAITING_EXTERNAL",
                    )
                    return True
                self._bind_external_fact(workflow_id, state, fact_kind, external_fact, fence)
                return True

        external_fact: Mapping[str, Any] | None = self._bound_external_fact(
            self._repository.events(workflow_id), "SIGNED_BOM",
        )

        stage_id = self._repository.stage_for_phase(workflow_id, state, activation, phase.name)
        if stage_id is None:
            messages = self._message_templates(
                request, self._repository.role_binding(workflow_id), state,
                activation, phase, external_fact, events,
            )
            self._guarded_repository_write(
                "CONTINUE", workflow_id,
                lambda: self._repository.schedule_phase(
                    workflow_id, state, activation, phase.name, messages, fence, utc_now(),
                ),
            )
            return True

        pending = [item for item in self._repository.pending_messages(workflow_id) if item["stage_id"] == stage_id]
        if pending:
            message = pending[0]
            command = self._command(request, message, fence)
            self._guarded_repository_write(
                "CONTINUE", workflow_id,
                lambda: self._repository.record_attempt(
                    workflow_id, message["request_id"], sha256(command), fence, utc_now(),
                ),
            )
            self._assert_runtime_control("PROVIDER_INVOKE", workflow_id)
            raw_receipt = self._adapter.invoke(command)
            self._assert_runtime_control("CONTINUE", workflow_id)
            try:
                receipt = self._validate_receipt(raw_receipt, command)
            except ExpiredWorktreeLease:
                digest = sha256(raw_receipt) if isinstance(raw_receipt, Mapping) else sha256(repr(raw_receipt))
                self._transition(
                    workflow_id, state, "STALE",
                    {
                        "reason": "WORKTREE_LEASE_EXPIRED_BEFORE_EVIDENCE_USE",
                        "request_id": message["request_id"],
                        "receipt_sha256": digest,
                    },
                    "stale-lease:%s:%s" % (stage_id, message["request_id"]),
                    fence,
                )
                return True
            except ReceiptRejected:
                digest = sha256(raw_receipt) if isinstance(raw_receipt, Mapping) else sha256(repr(raw_receipt))
                self._guarded_repository_write(
                    "CONTINUE", workflow_id,
                    lambda: self._repository.quarantine(
                        workflow_id, "UNTRUSTED_MODULE_RECEIPT", digest, fence, utc_now(),
                    ),
                )
                raise
            self._guarded_repository_write(
                "CONTINUE", workflow_id,
                lambda: self._repository.record_receipt(
                    workflow_id, message["request_id"], receipt, fence, utc_now(),
                ),
            )
            return True

        receipts = self._repository.stage_receipts(workflow_id, stage_id)
        expected_count = len(self._applicable_calls(phase, request))
        if len(receipts) != expected_count:
            raise CorruptWorkflow("scheduled phase has neither pending messages nor complete receipts")
        statuses = {receipt["status"] for receipt in receipts}
        if statuses == {"PASS"}:
            try:
                self._validate_trusted_stage_receipt_set(
                    phase, request, receipts,
                )
            except ReceiptRejected:
                digest = sha256({
                    "stage_id": stage_id,
                    "receipt_ids": sorted(
                        str(item["receipt_id"]) for item in receipts
                    ),
                })
                self._guarded_repository_write(
                    "CONTINUE", workflow_id,
                    lambda: self._repository.quarantine(
                        workflow_id, "UNTRUSTED_STAGE_RECEIPT_SET",
                        digest, fence, utc_now(),
                    ),
                )
                raise
            self._guarded_repository_write(
                "CONTINUE", workflow_id,
                lambda: self._repository.append_phase_completed(
                    workflow_id, state, activation, phase.name, fence, utc_now(),
                ),
            )
            if phase.transition_to is not None:
                self._transition(workflow_id, state, phase.transition_to, {"completed_phase": phase.name, "receipt_ids": sorted(item["receipt_id"] for item in receipts)}, "transition:%s:%s" % (activation, phase.transition_to), fence)
            return True
        failure = self._failure_state(state, statuses)
        event_type = "WAITING_EXTERNAL" if failure == "WAITING_EXTERNAL" else ("WORKFLOW_QUARANTINED" if failure == "QUARANTINED" else "STATE_TRANSITIONED")
        payload = {"resume_state": state, "reason": "MODULE_RECEIPT_" + "_".join(sorted(statuses)), "receipt_ids": sorted(item["receipt_id"] for item in receipts)}
        self._transition(workflow_id, state, failure, payload, "failure:%s:%s" % (activation, failure), fence, event_type=event_type)
        return True

    @staticmethod
    def _phase_completed(events: Sequence[Mapping[str, Any]], state: str, activation: int, phase: str) -> bool:
        return any(event["event_type"] == "PHASE_COMPLETED" and event["state"] == state and event["payload"].get("activation_sequence") == activation and event["payload"].get("phase") == phase for event in events)

    @staticmethod
    def _applicable_calls(
        phase: PhaseSpec,
        request: Mapping[str, Any],
    ) -> tuple[CallSpec, ...]:
        """Select only writer leases whose exact path class is in this request."""
        if phase.name in {"changeset-ready", "independent-verification"}:
            return tuple(
                CallSpec(
                    call.target_module, call.operation, call.role,
                    call.expected_outputs, str(module_id),
                )
                for module_id in request.get("target_modules", [])
                for call in phase.calls
            )
        if phase.name != "lease-worktrees":
            return phase.calls
        present_roles = {
            writer_role_for_path(str(path))
            for path in request.get("requested_paths", [])
        }
        return tuple(call for call in phase.calls if call.role in present_roles)

    def _validate_trusted_stage_receipt_set(
        self,
        phase: PhaseSpec,
        request: Mapping[str, Any],
        receipts: Sequence[Mapping[str, Any]],
    ) -> None:
        """Bind trusted-runner evidence as one exact, collision-free stage set."""
        if phase.name not in {"changeset-ready", "independent-verification"}:
            return
        expected_pairs = {
            (str(call.subject_module), "factory." + call.operation)
            for call in self._applicable_calls(phase, request)
        }
        results = [
            output["payload"]
            for receipt in receipts
            for output in receipt.get("outputs", [])
            if output.get("contract_id") == "trusted.test.result/v1"
        ]
        result_ids = [str(item.get("result_id")) for item in results]
        actual_pairs = [
            (str(item.get("module_id")), str(item.get("check_id")))
            for item in results
        ]
        tested_commits = {str(item.get("tested_commit")) for item in results}
        if (
            len(results) != len(expected_pairs)
            or len(result_ids) != len(set(result_ids))
            or len(actual_pairs) != len(set(actual_pairs))
            or set(actual_pairs) != expected_pairs
            or len(tested_commits) != 1
        ):
            raise ReceiptRejected(
                "trusted result stage is not one exact unique module/check set",
            )
        current_receipt_ids = {
            str(receipt["receipt_id"]) for receipt in receipts
        }
        earlier_result_ids = {
            str(output["payload"].get("result_id"))
            for receipt in self._repository.receipts(request["workflow_id"])
            if str(receipt["receipt_id"]) not in current_receipt_ids
            for output in receipt.get("outputs", [])
            if output.get("contract_id") == "trusted.test.result/v1"
        }
        if set(result_ids) & earlier_result_ids:
            raise ReceiptRejected(
                "trusted result id was reused across evidence stages",
            )

    @classmethod
    def _required_fact_kinds(
        cls, request: Mapping[str, Any], state: str,
    ) -> tuple[str, ...]:
        if state == "CANDIDATE_VERIFIED":
            if request.get("mode") == "PRODUCTION":
                return "SIGNED_BOM", NATIVE_STOP_AUTHORITY_TRUST_FACT
            return ("SIGNED_BOM",)
        if state in {"ROLLBACK_REQUIRED", "ROLLING_BACK"}:
            return ("ROLLBACK_AUTHORIZATION",)
        scope = cls._required_human_scope(request, state)
        if request.get("mode") == "PRODUCTION" and state in ROLLOUT_TRANSITIONS:
            if scope is not None:
                return (
                    NATIVE_STOP_AUTHORITY_TRUST_FACT,
                    cls._human_fact_kind(*scope),
                )
            return (NATIVE_STOP_AUTHORITY_TRUST_FACT,)
        if scope is not None:
            return (cls._human_fact_kind(*scope),)
        return ()

    @classmethod
    def _required_fact_kind(
        cls, request: Mapping[str, Any], state: str,
    ) -> str | None:
        """Compatibility projection for callers that only inspect the first gate."""
        kinds = cls._required_fact_kinds(request, state)
        return kinds[0] if kinds else None

    @staticmethod
    def _rollback_reason(events: Sequence[Mapping[str, Any]]) -> str:
        for event in reversed(events):
            if event.get("state") != "ROLLBACK_REQUIRED":
                continue
            payload = event.get("payload")
            reason = payload.get("reason") if isinstance(payload, Mapping) else None
            if (
                isinstance(reason, str)
                and reason != "EXTERNAL_FACT_VERIFIED"
                and re.fullmatch(r"[A-Z][A-Z0-9_:-]{2,127}", reason)
            ):
                return reason
        raise CorruptWorkflow("rollback authorization lacks a bounded causal reason")

    @staticmethod
    def _bound_external_fact(events: Sequence[Mapping[str, Any]], fact_kind: str) -> Mapping[str, Any] | None:
        for event in reversed(events):
            payload = event.get("payload")
            if event.get("event_type") != "EXTERNAL_FACT_BOUND" or not isinstance(payload, Mapping):
                continue
            if payload.get("fact_kind") != fact_kind:
                continue
            fact = payload.get("fact")
            if not isinstance(fact, Mapping) or payload.get("fact_sha256") != sha256(fact):
                raise CorruptWorkflow("bound external fact digest is corrupt")
            return copy.deepcopy(dict(fact))
        return None

    def _validate_external_fact(
        self,
        request: Mapping[str, Any],
        fact_kind: str,
        fact: Mapping[str, Any],
        events: Sequence[Mapping[str, Any]],
    ) -> dict[str, Any]:
        if fact_kind == "SIGNED_BOM":
            return self._validate_signed_bom_fact(request, fact)
        if fact_kind == NATIVE_STOP_AUTHORITY_TRUST_FACT:
            return self._validate_native_stop_authority_trust_fact(
                request, fact, events,
            )
        if fact_kind.startswith("HUMAN_TRANSITION_APPROVAL:"):
            parts = fact_kind.split(":")
            if len(parts) != 3:
                raise CorruptWorkflow("human transition approval scope is malformed")
            return self._validate_human_transition_fact(
                request, fact, parts[1], parts[2],
            )
        if fact_kind == "ROLLBACK_AUTHORIZATION":
            return self._validate_rollback_authorization_fact(
                request, fact, self._rollback_reason(events),
            )
        raise CorruptWorkflow("unknown required external fact kind")

    def _native_stop_trust_expectation(
        self,
        request: Mapping[str, Any],
        events: Sequence[Mapping[str, Any]],
    ) -> dict[str, Any]:
        signed_bom = self._bound_external_fact(events, "SIGNED_BOM")
        if not isinstance(signed_bom, Mapping):
            raise CorruptWorkflow("native-stop trust lacks its signed BOM fact")
        signed_bom = self._validate_signed_bom_fact(request, signed_bom)
        return {
            "workflow_id": str(request["workflow_id"]),
            "request_sha256": sha256(request),
            "external_context_ref": request["external_context_ref"],
            "release_bom_sha256": str(signed_bom["bom_sha256"]),
        }

    def _validate_native_stop_authority_trust_fact(
        self,
        request: Mapping[str, Any],
        fact: Mapping[str, Any],
        events: Sequence[Mapping[str, Any]],
    ) -> dict[str, Any]:
        if request.get("mode") != "PRODUCTION":
            raise ReceiptRejected("simulation must not consume production native-stop trust")
        authority = self._native_stop_authority_trust
        if authority is None:
            raise ExternalFactExpired(
                "native-stop trust cannot be revalidated without its deployed provider authority",
            )
        expectation = self._native_stop_trust_expectation(request, events)
        try:
            capability = authority.revalidate_fact(fact, **expectation)
            if authority.validate_capability(capability, **expectation) is not True:
                raise NativeStopAuthorityTrustError(
                    "native-stop trust capability failed currentness validation",
                )
            durable = authority.to_durable_fact(capability)
            globally_bound = self._repository.native_stop_authority_trust(
                str(durable["receipt_id"]),
            )
            if globally_bound is None:
                raise NativeStopAuthorityTrustError(
                    "native-stop trust durable receipt index is missing",
                )
            globally_bound = validate_native_stop_trust_durable_fact(globally_bound)
            for name in (
                "receipt_id", "receipt_sha256", "canonical_receipt_utf8",
                "release_bom_id", "release_bom_sha256", "integration_commit",
                "release_bom_generation", "activation_token_sha256",
                "authority_sets_sha256",
            ):
                if globally_bound.get(name) != durable.get(name):
                    raise NativeStopAuthorityTrustError(
                        "native-stop trust durable receipt index drifted",
                    )
            return durable
        except NativeStopAuthorityTrustError as exc:
            if "stale" in str(exc).casefold() or "window" in str(exc).casefold():
                raise ExternalFactExpired(str(exc)) from exc
            raise ReceiptRejected("native-stop authority trust fact is untrusted") from exc

    def _validate_signed_bom_fact(self, request: Mapping[str, Any], raw: Mapping[str, Any]) -> dict[str, Any]:
        fields = {
            "verified", "fact_id", "bom_sha256", "artifact_sha256",
            "previous_stable_bom_id", "previous_stable_bom_sha256",
            "previous_stable_verification_id", "signer_identity",
            "signature_sha256", "signature_key_id", "verified_at",
            "simulation_only", "request_sha256", "external_context_ref",
        }
        if not isinstance(raw, Mapping) or set(raw) != fields or raw.get("verified") is not True:
            raise ReceiptRejected("signed BOM fact is not an exact verified record")
        value = copy.deepcopy(dict(raw))
        for name in (
            "fact_id", "previous_stable_bom_id", "previous_stable_verification_id",
            "signer_identity", "signature_key_id",
        ):
            if not isinstance(value.get(name), str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{7,127}", value[name]):
                raise ReceiptRejected("signed BOM fact identity is invalid: " + name)
        for name in ("bom_sha256", "artifact_sha256", "previous_stable_bom_sha256", "signature_sha256"):
            if not isinstance(value.get(name), str) or not re.fullmatch(r"[a-f0-9]{64}", value[name]):
                raise ReceiptRejected("signed BOM fact digest is invalid: " + name)
        if value["bom_sha256"] == value["previous_stable_bom_sha256"]:
            raise ReceiptRejected("candidate and previous stable BOM must be distinct")
        if value.get("request_sha256") != sha256(request) or value.get("external_context_ref") != request["external_context_ref"]:
            raise ReceiptRejected("signed BOM fact is not bound to the workflow request")
        if value.get("simulation_only") is not (request["mode"] == "SIMULATION"):
            raise ReceiptRejected("signed BOM fact mode is inconsistent")
        binding = self._repository.role_binding(request["workflow_id"])
        if value["signer_identity"] in set(binding["roles"].values()):
            raise ReceiptRejected("signed BOM signer overlaps a Factory role")
        try:
            verified_at = dt.datetime.fromisoformat(str(value.get("verified_at")).replace("Z", "+00:00"))
        except ValueError as exc:
            raise ReceiptRejected("signed BOM verification timestamp is invalid") from exc
        if verified_at.tzinfo is None:
            raise ReceiptRejected("signed BOM verification timestamp lacks a timezone")
        return value

    def _validate_human_transition_fact(
        self,
        request: Mapping[str, Any],
        raw: Mapping[str, Any],
        from_state: str,
        to_state: str,
    ) -> dict[str, Any]:
        fields = {
            "verified", "fact_id", "approver_identity", "risk_tier",
            "request_sha256", "external_context_ref", "bom_sha256",
            "artifact_sha256", "bom_signature_sha256", "approval_nonce",
            "issued_at", "expires_at", "approval_signature_sha256",
            "approval_key_id", "from_state", "to_state",
        }
        if not isinstance(raw, Mapping) or set(raw) != fields or raw.get("verified") is not True:
            raise ReceiptRejected("human approval is not an exact verified record")
        value = copy.deepcopy(dict(raw))
        approver = value.get("approver_identity")
        binding = self._repository.role_binding(request["workflow_id"])
        if not isinstance(approver, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{7,127}", approver) or approver in set(binding["roles"].values()):
            raise ReceiptRejected("human approval identity is invalid or overlaps a Factory role")
        for name in ("fact_id", "approval_key_id"):
            if not isinstance(value.get(name), str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{7,127}", value[name]):
                raise ReceiptRejected("human approval identity is invalid: " + name)
        if value.get("risk_tier") != request["risk_tier"] or value.get("request_sha256") != sha256(request) or value.get("external_context_ref") != request["external_context_ref"]:
            raise ReceiptRejected("human approval is not bound to the exact request and risk")
        if (
            value.get("from_state") != from_state
            or value.get("to_state") != to_state
            or ROLLOUT_TRANSITIONS.get(from_state) != to_state
        ):
            raise ReceiptRejected("human approval is not bound to the exact transition")
        signed_bom = self._bound_external_fact(
            self._repository.events(request["workflow_id"]), "SIGNED_BOM",
        )
        if not isinstance(signed_bom, Mapping) or signed_bom.get("verified") is not True:
            raise ReceiptRejected("human approval lacks its bound signed BOM fact")
        for name, signed_name in (
            ("bom_sha256", "bom_sha256"),
            ("artifact_sha256", "artifact_sha256"),
            ("bom_signature_sha256", "signature_sha256"),
        ):
            if value.get(name) != signed_bom.get(signed_name):
                raise ReceiptRejected("human approval does not bind the exact BOM artifact and signature")
        for name in ("approval_signature_sha256", "bom_signature_sha256", "bom_sha256", "artifact_sha256"):
            if not isinstance(value.get(name), str) or re.fullmatch(r"[a-f0-9]{64}", value[name]) is None:
                raise ReceiptRejected("human approval digest is invalid: " + name)
        try:
            issued_at = dt.datetime.fromisoformat(str(value.get("issued_at")).replace("Z", "+00:00"))
            expires_at = dt.datetime.fromisoformat(str(value.get("expires_at")).replace("Z", "+00:00"))
        except ValueError as exc:
            raise ReceiptRejected("human approval validity window is invalid") from exc
        if issued_at.tzinfo is None or expires_at.tzinfo is None:
            raise ReceiptRejected("human approval validity window lacks a timezone")
        now = self._now()
        if expires_at <= now:
            raise ExternalFactExpired("human approval expired before use")
        if (
            issued_at >= expires_at
            or expires_at - issued_at > dt.timedelta(minutes=15)
            or issued_at > now + dt.timedelta(minutes=1)
        ):
            raise ReceiptRejected("human approval is outside its bounded validity window")
        expected_nonce = "approval-nonce:" + sha256({
            "workflow_id": request["workflow_id"],
            "request_sha256": sha256(request),
            "bom_sha256": value["bom_sha256"],
            "artifact_sha256": value["artifact_sha256"],
            "bom_signature_sha256": value["bom_signature_sha256"],
            "approver_identity": value["approver_identity"],
            "from_state": from_state,
            "to_state": to_state,
            "issued_at": value["issued_at"],
            "expires_at": value["expires_at"],
        })[:32]
        if value.get("approval_nonce") != expected_nonce:
            raise ReceiptRejected("human approval nonce is not bound to this workflow and BOM")
        return value

    def _validate_rollback_authorization_fact(
        self,
        request: Mapping[str, Any],
        raw: Mapping[str, Any],
        reason_code: str,
    ) -> dict[str, Any]:
        fields = {
            "verified", "fact_id", "authorizer_identity", "authorization_kind",
            "request_sha256", "external_context_ref", "reason_code",
            "candidate_bom_sha256", "previous_stable_bom_sha256",
            "previous_stable_verification_id", "authorization_signature_sha256",
            "authorization_key_id", "verified_at", "expires_at", "simulation_only",
        }
        if not isinstance(raw, Mapping) or set(raw) != fields or raw.get("verified") is not True:
            raise ReceiptRejected("rollback authorization is not an exact verified record")
        value = copy.deepcopy(dict(raw))
        for name in (
            "fact_id", "authorizer_identity", "authorization_key_id",
            "previous_stable_verification_id",
        ):
            if not isinstance(value.get(name), str) or re.fullmatch(
                r"[A-Za-z0-9][A-Za-z0-9._:-]{7,127}", value[name],
            ) is None:
                raise ReceiptRejected("rollback authorization identity is invalid: " + name)
        if value.get("authorization_kind") not in {
            "SIMULATION_POLICY", "AUTOMATED_SAFETY", "HUMAN_OPERATOR",
        }:
            raise ReceiptRejected("rollback authorization kind is invalid")
        if value.get("reason_code") != reason_code:
            raise ReceiptRejected("rollback authorization reason is not causal")
        if (
            value.get("request_sha256") != sha256(request)
            or value.get("external_context_ref") != request["external_context_ref"]
            or value.get("simulation_only") is not (request["mode"] == "SIMULATION")
        ):
            raise ReceiptRejected("rollback authorization is not bound to request and mode")
        if request["mode"] == "SIMULATION":
            if value.get("authorization_kind") != "SIMULATION_POLICY":
                raise ReceiptRejected("simulation rollback used non-simulation authority")
        elif value.get("authorization_kind") == "SIMULATION_POLICY":
            raise ReceiptRejected("production rollback used simulation authority")
        signed_bom = self._bound_external_fact(
            self._repository.events(request["workflow_id"]), "SIGNED_BOM",
        )
        if not isinstance(signed_bom, Mapping) or signed_bom.get("verified") is not True:
            raise ReceiptRejected("rollback authorization lacks the bound signed BOM")
        for authorization_name, bom_name in (
            ("candidate_bom_sha256", "bom_sha256"),
            ("previous_stable_bom_sha256", "previous_stable_bom_sha256"),
            ("previous_stable_verification_id", "previous_stable_verification_id"),
        ):
            if value.get(authorization_name) != signed_bom.get(bom_name):
                raise ReceiptRejected("rollback authorization is not bound to the signed BOM tuple")
        for name in (
            "candidate_bom_sha256", "previous_stable_bom_sha256",
            "authorization_signature_sha256",
        ):
            if not isinstance(value.get(name), str) or re.fullmatch(r"[a-f0-9]{64}", value[name]) is None:
                raise ReceiptRejected("rollback authorization digest is invalid: " + name)
        binding = self._repository.role_binding(request["workflow_id"])
        if value["authorizer_identity"] in set(binding["roles"].values()):
            raise ReceiptRejected("rollback authorizer overlaps a Factory role")
        try:
            verified_at = dt.datetime.fromisoformat(str(value.get("verified_at")).replace("Z", "+00:00"))
            expires_at = dt.datetime.fromisoformat(str(value.get("expires_at")).replace("Z", "+00:00"))
        except ValueError as exc:
            raise ReceiptRejected("rollback authorization validity window is invalid") from exc
        if verified_at.tzinfo is None or expires_at.tzinfo is None:
            raise ReceiptRejected("rollback authorization validity window lacks a timezone")
        now = self._now()
        if expires_at <= now:
            raise ExternalFactExpired("rollback authorization expired before use")
        if (
            verified_at >= expires_at
            or expires_at - verified_at > dt.timedelta(minutes=15)
            or verified_at > now + dt.timedelta(minutes=1)
        ):
            raise ReceiptRejected("rollback authorization is outside its bounded window")
        return value

    def _obtain_external_fact(
        self,
        request: Mapping[str, Any],
        state: str,
        fact_kind: str | None = None,
    ) -> Mapping[str, Any] | None:
        self._assert_runtime_control("CONTINUE", request["workflow_id"])
        selected_kind = fact_kind or self._required_fact_kind(request, state)
        if selected_kind == "SIGNED_BOM":
            raw = self._external.verify_signed_bom(
                request["workflow_id"], sha256(request),
                request["external_context_ref"], request["mode"],
            )
            self._assert_runtime_control("CONTINUE", request["workflow_id"])
            return None if raw is None else self._validate_signed_bom_fact(request, raw)
        if selected_kind == NATIVE_STOP_AUTHORITY_TRUST_FACT:
            if request.get("mode") != "PRODUCTION":
                raise CorruptWorkflow("simulation requested a production native-stop trust receipt")
            authority = self._native_stop_authority_trust
            if authority is None:
                return None
            expectation = self._native_stop_trust_expectation(
                request, self._repository.events(request["workflow_id"]),
            )
            try:
                capability = authority.obtain(**expectation)
            except NativeStopAuthorityTrustError as exc:
                raise ReceiptRejected(
                    "native-stop authority trust provider returned untrusted data",
                ) from exc
            self._assert_runtime_control("CONTINUE", request["workflow_id"])
            if capability is None:
                return None
            if authority.validate_capability(capability, **expectation) is not True:
                raise ReceiptRejected("native-stop trust capability lost currentness before binding")
            return authority.to_durable_fact(capability)
        human_scope = self._required_human_scope(request, state)
        if human_scope is not None and selected_kind == self._human_fact_kind(*human_scope):
            binding = self._repository.role_binding(request["workflow_id"])
            raw = self._external.verify_human_transition(
                request["workflow_id"], sha256(request), request["external_context_ref"],
                request["risk_tier"], human_scope[0], human_scope[1],
                tuple(binding["roles"].values()),
            )
            self._assert_runtime_control("CONTINUE", request["workflow_id"])
            return None if raw is None else self._validate_human_transition_fact(
                request, raw, human_scope[0], human_scope[1],
            )
        if (
            state in {"ROLLBACK_REQUIRED", "ROLLING_BACK"}
            and selected_kind == "ROLLBACK_AUTHORIZATION"
        ):
            events = self._repository.events(request["workflow_id"])
            reason_code = self._rollback_reason(events)
            signed_bom = self._bound_external_fact(events, "SIGNED_BOM")
            if not isinstance(signed_bom, Mapping):
                raise CorruptWorkflow("rollback authorization lacks a signed BOM fact")
            raw = self._external.verify_rollback_authorization(
                request["workflow_id"], sha256(request), request["external_context_ref"],
                request["mode"], reason_code, str(signed_bom["previous_stable_bom_sha256"]),
            )
            self._assert_runtime_control("CONTINUE", request["workflow_id"])
            return None if raw is None else self._validate_rollback_authorization_fact(
                request, raw, reason_code,
            )
        return {}

    def _bind_external_fact(self, workflow_id: str, state: str, fact_kind: str, fact: Mapping[str, Any], fence: int) -> None:
        if fact_kind == NATIVE_STOP_AUTHORITY_TRUST_FACT:
            self._guarded_repository_write(
                "CONTINUE", workflow_id,
                lambda: self._repository.register_native_stop_authority_trust(
                    workflow_id, fact, fence, utc_now(),
                ),
            )
            receipt_id = fact.get("receipt_id")
            receipt_sha = fact.get("receipt_sha256")
            for event in self._repository.events(workflow_id):
                payload = event.get("payload")
                prior = payload.get("fact") if isinstance(payload, Mapping) else None
                if (
                    event.get("event_type") == "EXTERNAL_FACT_BOUND"
                    and isinstance(prior, Mapping)
                    and payload.get("fact_kind") == fact_kind
                    and prior.get("receipt_id") == receipt_id
                    and prior.get("receipt_sha256") != receipt_sha
                ):
                    digest = sha256({
                        "receipt_id": receipt_id,
                        "prior_receipt_sha256": prior.get("receipt_sha256"),
                        "candidate_receipt_sha256": receipt_sha,
                    })
                    self._guarded_repository_write(
                        "CONTINUE", workflow_id,
                        lambda: self._repository.quarantine(
                            workflow_id, "NATIVE_STOP_TRUST_RECEIPT_HASH_CONFLICT",
                            digest, fence, utc_now(),
                        ),
                    )
                    raise IdempotencyConflict(
                        "native-stop trust receipt id is bound to different canonical bytes",
                    )
        payload = {"fact_kind": fact_kind, "fact_sha256": sha256(fact), "fact": copy.deepcopy(dict(fact))}
        fact_identity = str(fact["fact_id"])
        if fact_kind == NATIVE_STOP_AUTHORITY_TRUST_FACT:
            attestation = fact.get("provider_attestation")
            if not isinstance(attestation, Mapping):
                raise ReceiptRejected("native-stop trust fact lacks provider attestation")
            fact_identity += ":" + str(attestation.get("attestation_id"))
        self._guarded_repository_write(
            "CONTINUE", workflow_id,
            lambda: self._repository.transition(
                workflow_id, state, "EXTERNAL_FACT_BOUND", payload,
                "external-fact:%s:%s" % (fact_kind, fact_identity), fence, utc_now(),
            ),
        )

    @staticmethod
    def _external_reason(
        request: Mapping[str, Any], state: str, fact_kind: str | None = None,
    ) -> str:
        if fact_kind == NATIVE_STOP_AUTHORITY_TRUST_FACT:
            return "NATIVE_STOP_AUTHORITY_TRUST_PROVIDER_REQUIRED"
        if state == "CANDIDATE_VERIFIED":
            return "SIGNED_BOM_EXTERNAL_VERIFICATION_REQUIRED"
        if state == "ROLLBACK_REQUIRED":
            return "ROLLBACK_EXTERNAL_AUTHORIZATION_REQUIRED"
        target = ROLLOUT_TRANSITIONS.get(state, "UNKNOWN")
        return "%s_HUMAN_PRODUCTION_CANARY_APPROVAL_REQUIRED:%s:%s" % (
            request["risk_tier"], state, target,
        )

    def resume_waiting(self, workflow_id: str, worker_identity: str) -> dict[str, Any]:
        fence = self._acquire_fence_if_state(workflow_id, worker_identity, {"WAITING_EXTERNAL"})
        events = self._repository.events(workflow_id)
        state, activation, _reason = _current_state(events)
        if self._quarantine_if_needed(workflow_id, state, fence):
            return self.status(workflow_id)
        if state != "WAITING_EXTERNAL":
            raise IllegalTransition("workflow is not waiting for external evidence")
        resume_state = events[-1]["payload"].get("resume_state")
        if resume_state not in {"CANDIDATE_VERIFIED", "BOM_SIGNED", "SHADOW", "CANARY", "ROLLING", "SOAKING", "ROLLBACK_REQUIRED", "ROLLING_BACK"}:
            raise CorruptWorkflow("WAITING_EXTERNAL lacks a legal resume state")
        request = self._repository.request(workflow_id)
        for fact_kind in self._required_fact_kinds(request, str(resume_state)):
            events = self._repository.events(workflow_id)
            bound_fact = self._bound_external_fact(events, fact_kind)
            needs_fact = bound_fact is None
            if bound_fact is not None:
                try:
                    self._validate_external_fact(request, fact_kind, bound_fact, events)
                except ExternalFactExpired:
                    needs_fact = True
            if needs_fact:
                fact = self._obtain_external_fact(
                    request, str(resume_state), fact_kind,
                )
                if fact is None:
                    reason = self._external_reason(
                        request, str(resume_state), fact_kind,
                    )
                    if self.status(workflow_id)["waiting_reason"] != reason:
                        self._guarded_repository_write(
                            "CONTINUE", workflow_id,
                            lambda: self._repository.transition(
                                workflow_id, "WAITING_EXTERNAL", "WAITING_EXTERNAL",
                                {"resume_state": resume_state, "reason": reason},
                                "waiting:%s:%s:%s" % (
                                    activation, resume_state, fact_kind,
                                ),
                                fence, utc_now(),
                            ),
                        )
                    return self.status(workflow_id)
                self._bind_external_fact(workflow_id, state, fact_kind, fact, fence)
        self._transition(workflow_id, state, str(resume_state), {"reason": "EXTERNAL_FACT_VERIFIED"}, "resume:%s:%s" % (activation, resume_state), fence)
        return self.run_until_blocked(workflow_id, worker_identity)

    def request_rollback(self, workflow_id: str, worker_identity: str, reason_code: str) -> dict[str, Any]:
        if not re.fullmatch(r"[A-Z][A-Z0-9_:-]{2,127}", reason_code):
            raise InvalidWorkflowRequest("rollback reason must be a bounded reason code")
        fence = self._acquire_fence_if_state(
            workflow_id, worker_identity, ROLLOUT_STATES.union({"BOM_SIGNED"}),
        )
        events = self._repository.events(workflow_id)
        state, activation, _ = _current_state(events)
        if state not in ROLLOUT_STATES.union({"BOM_SIGNED"}):
            raise CorruptWorkflow("conditional fence admitted an illegal rollback state")
        self._transition(workflow_id, state, "ROLLBACK_REQUIRED", {"reason": reason_code, "from_state": state}, "rollback:%s" % activation, fence, event_type="ROLLBACK_REQUESTED")
        return self.run_until_blocked(workflow_id, worker_identity)

    def cancel(self, workflow_id: str, worker_identity: str, reason_code: str) -> dict[str, Any]:
        if not re.fullmatch(r"[A-Z][A-Z0-9_:-]{2,127}", reason_code):
            raise InvalidWorkflowRequest("cancellation reason must be a bounded reason code")
        cancellable = {state for state, targets in LEGAL_TRANSITIONS.items() if "CANCELLED" in targets}
        fence = self._acquire_fence_if_state(workflow_id, worker_identity, cancellable)
        events = self._repository.events(workflow_id)
        state, activation, _ = _current_state(events)
        if "CANCELLED" not in LEGAL_TRANSITIONS.get(state, frozenset()):
            raise CorruptWorkflow("conditional fence admitted an illegal cancellation state")
        self._transition(
            workflow_id, state, "CANCELLED",
            {"reason": reason_code, "from_state": state},
            "cancel:%s" % activation, fence,
        )
        return self.status(workflow_id)

    def rework_stale(self, workflow_id: str, worker_identity: str) -> dict[str, Any]:
        fence = self._acquire_fence_if_state(workflow_id, worker_identity, {"STALE"})
        events = self._repository.events(workflow_id)
        state, activation, _ = _current_state(events)
        if state != "STALE":
            raise CorruptWorkflow("conditional fence admitted a non-STALE rework")
        self._transition(workflow_id, "STALE", "REWORKING", {"reason": "REBIND_REQUIRED"}, "rework:%s" % activation, fence)
        events = self._repository.events(workflow_id)
        _state, activation, _ = _current_state(events)
        self._transition(workflow_id, "REWORKING", "SCOPE_RESOLVED", {"reason": "RESTART_FROM_SCOPE"}, "rebind:%s" % activation, fence)
        return self.run_until_blocked(workflow_id, worker_identity)

    def _message_templates(
        self,
        request: Mapping[str, Any],
        binding: Mapping[str, Any],
        state: str,
        activation: int,
        phase: PhaseSpec,
        external_fact: Mapping[str, Any] | None,
        events: Sequence[Mapping[str, Any]],
    ) -> list[dict[str, Any]]:
        receipts = self._repository.receipts(request["workflow_id"])
        references = [{"receipt_id": item["receipt_id"], "receipt_sha256": sha256({key: value for key, value in item.items() if key != "receipt_id"})} for item in receipts]
        prior_outputs = [
            copy.deepcopy(dict(output))
            for receipt in receipts
            for output in receipt.get("outputs", [])
        ]
        causal: dict[str, dict[str, Any]] = {}
        for receipt in receipts:
            stage_id = str(receipt["stage_id"])
            receipt_id = str(receipt["receipt_id"])
            for raw_output in receipt.get("outputs", []):
                output = copy.deepcopy(dict(raw_output))
                contract_id = str(output["contract_id"])
                group = causal.get(contract_id)
                if group is None or group["stage_id"] != stage_id:
                    group = {"stage_id": stage_id, "receipt_ids": [], "outputs": []}
                    causal[contract_id] = group
                group["receipt_ids"].append(receipt_id)
                group["outputs"].append(output)
        causal_heads = {
            contract_id: {
                "stage_id": group["stage_id"],
                "receipt_ids": list(group["receipt_ids"]),
                "payload_sha256s": [item["payload_sha256"] for item in group["outputs"]],
            }
            for contract_id, group in causal.items()
        }
        causal_outputs = {
            contract_id: copy.deepcopy(group["outputs"])
            for contract_id, group in causal.items()
        }
        result = []
        for call in self._applicable_calls(phase, request):
            context = {
                "workflow_request": copy.deepcopy(dict(request)),
                "prior_receipts": references,
                "prior_outputs": prior_outputs,
                "causal_heads": causal_heads,
                "causal_outputs": causal_outputs,
                "role_identities": copy.deepcopy(dict(binding["roles"])),
                "state": state,
                "activation_sequence": activation,
                "phase": phase.name,
                "subject_module": call.subject_module,
                "allowed_path_classes": list(ROLE_PATH_CLASSES[call.role]),
                "external_fact": copy.deepcopy(dict(external_fact)) if external_fact else None,
                "bound_release_fact": self._bound_external_fact(events, "SIGNED_BOM"),
                "bound_native_stop_authority_trust": self._bound_external_fact(
                    events, NATIVE_STOP_AUTHORITY_TRUST_FACT,
                ),
                "bound_rollback_authorization": self._bound_external_fact(
                    events, "ROLLBACK_AUTHORIZATION",
                ),
                "rollback_reason": (
                    self._rollback_reason(events)
                    if state in {"ROLLBACK_REQUIRED", "ROLLING_BACK"}
                    else None
                ),
            }
            result.append({
                "target_module": call.target_module, "operation": call.operation,
                "actor_identity": binding["roles"][call.role], "actor_role": call.role,
                "expected_output_contracts": list(call.expected_outputs), "context": context,
            })
        return result

    @staticmethod
    def _command(request: Mapping[str, Any], message: Mapping[str, Any], fence: int) -> dict[str, Any]:
        context = copy.deepcopy(dict(message["context"]))
        command = {
            "schema_version": "1.0.0", "contract_id": "factory.module.command/v1",
            "producer_module": "factory-control-plane-host",
            "soul_id": request["soul_id"], "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"], "trace_id": request["trace_id"],
            "idempotency_key": "idem_" + sha256(message["request_id"]), "privacy_class": "internal",
            "workflow_id": request["workflow_id"],
            "request_id": message["request_id"], "stage_id": message["stage_id"],
            "target_module": message["target_module"], "operation": message["operation"],
            "actor_identity": message["actor_identity"], "actor_role": message["actor_role"],
            "fencing_token": fence, "mode": request["mode"],
            "expected_output_contracts": list(message["expected_output_contracts"]),
            "context_sha256": sha256(context), "context": context, "occurred_at": utc_now(),
        }
        command["logical_request_sha256"] = logical_request_sha256(command)
        return command

    def _validate_receipt(self, raw: Mapping[str, Any], command: Mapping[str, Any]) -> dict[str, Any]:
        fields = {"schema_version", "contract_id", "producer_module", "soul_id", "device_binding_id", "platform_account_id", "trace_id", "idempotency_key", "occurred_at", "privacy_class", "workflow_id", "request_id", "stage_id", "target_module", "operation", "actor_identity", "actor_role", "fencing_token", "logical_request_sha256", "status", "mode", "evidence_kind", "verification_level", "simulation_only", "side_effect_count", "outputs", "attestation"}
        if not isinstance(raw, Mapping) or set(raw) != fields:
            raise ReceiptRejected("module receipt has unknown or missing fields")
        if len(canonical_bytes(dict(raw))) > MAX_MODULE_RECEIPT_BYTES:
            raise ReceiptRejected("module receipt exceeds its canonical byte limit")
        receipt = copy.deepcopy(dict(raw))
        for name in ("soul_id", "device_binding_id", "platform_account_id", "trace_id", "idempotency_key", "privacy_class", "workflow_id", "request_id", "stage_id", "target_module", "operation", "actor_identity", "actor_role", "fencing_token", "logical_request_sha256", "mode"):
            if receipt.get(name) != command.get(name):
                raise ReceiptRejected("module receipt does not match command: " + name)
        if receipt.get("schema_version") != "1.0.0" or receipt.get("contract_id") != "factory.module.receipt/v1" or receipt.get("producer_module") != "factory-control-plane-host":
            raise ReceiptRejected("unknown receipt contract")
        if receipt.get("status") not in RECEIPT_STATUSES:
            raise ReceiptRejected("unknown receipt status")
        if (
            isinstance(receipt.get("side_effect_count"), bool)
            or not isinstance(receipt.get("side_effect_count"), int)
            or not 0 <= receipt["side_effect_count"] <= 1_000_000_000
        ):
            raise ReceiptRejected("receipt side_effect_count is invalid")
        try:
            _timestamp(receipt.get("occurred_at"), "receipt occurred_at")
        except InvalidWorkflowRequest as exc:
            raise ReceiptRejected("receipt occurred_at is invalid") from exc
        if receipt["status"] != "PASS":
            current_state = command.get("context", {}).get("state")
            failure_state = self._failure_state(str(current_state), {str(receipt["status"])})
            if failure_state not in LEGAL_TRANSITIONS.get(str(current_state), frozenset()):
                raise ReceiptRejected("receipt status has no legal recovery transition from current state")
        evidence_kind = receipt.get("evidence_kind")
        verification_level = receipt.get("verification_level")
        if EVIDENCE_LEVEL_BY_KIND.get(str(evidence_kind)) != verification_level:
            raise ReceiptRejected("receipt evidence kind and verification level are inconsistent")
        simulated = command["mode"] == "SIMULATION"
        if simulated:
            if (
                receipt.get("simulation_only") is not True
                or evidence_kind != "SIMULATION"
                or verification_level != "INTEGRATION_VERIFIED"
                or receipt.get("side_effect_count") != 0
            ):
                raise ReceiptRejected("simulation receipt is not exact zero-side-effect simulation evidence")
        elif receipt.get("simulation_only") is not False or evidence_kind == "SIMULATION":
            raise ReceiptRejected("production receipt attempted to use simulation evidence")
        minimum_level = OPERATION_MINIMUM_LEVEL.get(str(command.get("operation")))
        if minimum_level is None:
            raise ReceiptRejected("receipt operation has no evidence policy")
        if (
            receipt["status"] == "PASS"
            and not simulated
            and VERIFICATION_LEVELS.index(str(verification_level)) < VERIFICATION_LEVELS.index(minimum_level)
        ):
            raise ReceiptRejected("PASS receipt evidence is below the fixed operation minimum")
        outputs = receipt.get("outputs")
        if not isinstance(outputs, list):
            raise ReceiptRejected("receipt outputs must be an array")
        output_ids = []
        for item in outputs:
            if not isinstance(item, Mapping) or set(item) != {"contract_id", "producer_module", "payload_sha256", "payload"}:
                raise ReceiptRejected("receipt output has unknown or missing fields")
            contract_id = item.get("contract_id")
            producer = CONTRACT_PRODUCERS.get(str(contract_id))
            if producer is None or item.get("producer_module") != producer:
                raise ReceiptRejected("receipt output contract or producer is unknown")
            if item.get("payload_sha256") != sha256(item.get("payload")):
                raise ReceiptRejected("receipt output payload digest mismatch")
            payload = item.get("payload")
            if not isinstance(payload, Mapping) or payload.get("contract_id") != contract_id or payload.get("producer_module") != producer:
                raise ReceiptRejected("receipt output payload identity mismatch")
            for envelope_name in (
                "soul_id", "device_binding_id", "platform_account_id", "trace_id",
                "privacy_class",
            ):
                if payload.get(envelope_name) != command.get(envelope_name):
                    raise ReceiptRejected("provider output envelope drift: " + envelope_name)
            if not self._provider_contract_verifier.verify(str(contract_id), payload):
                raise ReceiptRejected("receipt output fails its owning public JSON Schema")
            for payload_name, receipt_name in (
                ("evidence_kind", "evidence_kind"),
                ("verification_level", "verification_level"),
                ("simulation_only", "simulation_only"),
                ("evidence_level", "verification_level"),
            ):
                if payload_name in payload and payload.get(payload_name) != receipt.get(receipt_name):
                    raise ReceiptRejected("provider payload evidence metadata does not match its receipt")
            output_ids.append(str(contract_id))
        if receipt["status"] == "PASS" and sorted(output_ids) != sorted(command["expected_output_contracts"]):
            raise ReceiptRejected("PASS receipt did not return the exact expected contracts")
        if receipt["status"] == "PASS":
            for item in outputs:
                self._validate_output_semantics(item, command, outputs)
        attestation = receipt.get("attestation")
        if not isinstance(attestation, Mapping) or set(attestation) != {"kind", "verifier_identity", "payload_sha256", "reference"}:
            raise ReceiptRejected("receipt attestation is invalid")
        verifier_identity = attestation.get("verifier_identity")
        reference = attestation.get("reference")
        expected_attestation_kind = "SIMULATION_ONLY" if simulated else "EXTERNAL_VERIFIED"
        if attestation.get("kind") != expected_attestation_kind:
            raise ReceiptRejected("receipt attestation kind does not match workflow mode")
        if not isinstance(verifier_identity, str) or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,127}", verifier_identity) is None:
            raise ReceiptRejected("receipt verifier identity is invalid")
        if (
            not isinstance(reference, str)
            or not 8 <= len(reference) <= 256
            or reference != reference.strip()
            or any(control in reference for control in ("\x00", "\r", "\n"))
        ):
            raise ReceiptRejected("receipt attestation reference is invalid")
        unsigned = {key: value for key, value in receipt.items() if key != "attestation"}
        if attestation.get("payload_sha256") != sha256(unsigned):
            raise ReceiptRejected("receipt attestation payload digest mismatch")
        if not self._receipt_verifier.verify(receipt, command):
            raise ReceiptRejected("receipt trust verifier rejected the receipt")
        return receipt

    @staticmethod
    def _prior_payloads(command: Mapping[str, Any], contract_id: str) -> list[Mapping[str, Any]]:
        context = command.get("context")
        prior = context.get("prior_outputs") if isinstance(context, Mapping) else None
        if not isinstance(prior, list):
            raise ReceiptRejected("command lacks its immutable prior output set")
        result = []
        for item in prior:
            if not isinstance(item, Mapping) or set(item) != {"contract_id", "producer_module", "payload_sha256", "payload"}:
                raise ReceiptRejected("prior output reference is malformed")
            if item.get("payload_sha256") != sha256(item.get("payload")):
                raise ReceiptRejected("prior output reference digest mismatch")
            if item.get("contract_id") == contract_id:
                payload = item.get("payload")
                if not isinstance(payload, Mapping):
                    raise ReceiptRejected("prior output payload is malformed")
                result.append(payload)
        return result

    @staticmethod
    def _causal_payloads(command: Mapping[str, Any], contract_id: str) -> list[Mapping[str, Any]]:
        context = command.get("context")
        heads = context.get("causal_heads") if isinstance(context, Mapping) else None
        groups = context.get("causal_outputs") if isinstance(context, Mapping) else None
        if not isinstance(heads, Mapping) or not isinstance(groups, Mapping):
            raise ReceiptRejected("command lacks explicit causal output heads")
        head = heads.get(contract_id)
        outputs = groups.get(contract_id)
        if not isinstance(head, Mapping) or not isinstance(outputs, list) or not outputs:
            return []
        if set(head) != {"stage_id", "receipt_ids", "payload_sha256s"}:
            raise ReceiptRejected("causal output head is malformed")
        digests: list[str] = []
        payloads: list[Mapping[str, Any]] = []
        for item in outputs:
            if not isinstance(item, Mapping) or set(item) != {"contract_id", "producer_module", "payload_sha256", "payload"}:
                raise ReceiptRejected("causal output reference is malformed")
            if item.get("contract_id") != contract_id or item.get("producer_module") != CONTRACT_PRODUCERS.get(contract_id):
                raise ReceiptRejected("causal output contract identity drift")
            payload = item.get("payload")
            digest = item.get("payload_sha256")
            if not isinstance(payload, Mapping) or digest != sha256(payload):
                raise ReceiptRejected("causal output payload digest mismatch")
            digests.append(str(digest))
            payloads.append(payload)
        if head.get("payload_sha256s") != digests:
            raise ReceiptRejected("causal output head does not match its payloads")
        receipt_ids = head.get("receipt_ids")
        if not isinstance(receipt_ids, list) or len(receipt_ids) != len(outputs) or len(receipt_ids) != len(set(receipt_ids)):
            raise ReceiptRejected("causal output head receipt set is invalid")
        return payloads

    def _validate_output_semantics(
        self,
        output: Mapping[str, Any],
        command: Mapping[str, Any],
        sibling_outputs: Sequence[Mapping[str, Any]],
    ) -> None:
        contract_id = str(output["contract_id"])
        payload = output["payload"]
        context = command["context"]
        request = context["workflow_request"]

        if contract_id == "upgrade.intent/v1":
            expected = {
                "baseline_commit": request["baseline_commit"],
                "target_modules": request["target_modules"],
                "requested_paths": request["requested_paths"],
                "public_contract_changes": request["public_contract_changes"],
                "risk_tier": request["risk_tier"],
            }
            if any(payload.get(name) != value for name, value in expected.items()):
                raise ReceiptRejected("upgrade intent drifted from the immutable workflow request")
            return

        if contract_id == "instruction.receipt/v1":
            intents = self._causal_payloads(command, "upgrade.intent/v1")
            if not intents or payload.get("intent_id") != intents[-1].get("intent_id"):
                raise ReceiptRejected("instruction receipt is not bound to the upgrade intent")
            if payload.get("baseline_commit") != request["baseline_commit"] or payload.get("status") != "BOUND":
                raise ReceiptRejected("instruction receipt is stale or baseline-drifted")
            return

        if contract_id == "module.change.plan/v1":
            intents = self._causal_payloads(command, "upgrade.intent/v1")
            instructions = self._causal_payloads(command, "instruction.receipt/v1")
            if not intents or not instructions:
                raise ReceiptRejected("change plan lacks prior intent or instruction truth")
            expected = {
                "intent_id": intents[-1].get("intent_id"),
                "instruction_receipt_id": instructions[-1].get("receipt_id"),
                "baseline_commit": request["baseline_commit"],
                "affected_modules": request["target_modules"],
                "requested_paths": request["requested_paths"],
                "public_contract_changes": request["public_contract_changes"],
            }
            if any(payload.get(name) != value for name, value in expected.items()):
                raise ReceiptRejected("change plan does not preserve bound scope")
            return

        if contract_id == "worktree.plan/v1":
            plans = self._causal_payloads(command, "module.change.plan/v1")
            instructions = self._causal_payloads(command, "instruction.receipt/v1")
            if not plans or not instructions:
                raise ReceiptRejected("worktree plan lacks bound change-plan truth")
            if payload.get("change_plan_id") != plans[-1].get("plan_id") or payload.get("instruction_receipt_id") != instructions[-1].get("receipt_id"):
                raise ReceiptRejected("worktree plan reference drift")
            entries = payload.get("entries")
            if not isinstance(entries, list):
                raise ReceiptRejected("worktree plan entries are malformed")
            role_identities = context.get("role_identities")
            if not isinstance(role_identities, Mapping):
                raise ReceiptRejected("worktree plan lacks trusted role identities")
            declared_paths: list[str] = []
            entry_keys: set[tuple[str, str]] = set()
            for entry in entries:
                if not isinstance(entry, Mapping):
                    raise ReceiptRejected("worktree plan entry is malformed")
                paths = entry.get("owned_paths")
                module_id = entry.get("module_id")
                writer = entry.get("writer_identity")
                if (
                    module_id not in request["target_modules"]
                    or not isinstance(paths, list)
                    or not paths
                ):
                    raise ReceiptRejected("worktree entry is outside target ownership")
                roles = {writer_role_for_path(str(path)) for path in paths}
                if len(roles) != 1:
                    raise ReceiptRejected("worktree entry mixes writer path classes")
                expected_role = next(iter(roles))
                if writer != role_identities.get(expected_role):
                    raise ReceiptRejected("worktree entry writer does not own its path class")
                entry_key = (str(module_id), str(writer))
                if entry_key in entry_keys:
                    raise ReceiptRejected("worktree plan duplicates a module writer entry")
                entry_keys.add(entry_key)
                if any(
                    not isinstance(path, str)
                    or not path.startswith("Modules/%s/" % module_id)
                    or path not in request["requested_paths"]
                    or owned_path_class(path) == "contracts"
                    for path in paths
                ):
                    raise ReceiptRejected("worktree entry owns an undeclared path")
                expected_keys = {
                    "module:%s:writer:%s" % (module_id, expected_role),
                    *("path:" + path for path in paths),
                }
                if set(entry.get("lease_keys", [])) != expected_keys:
                    raise ReceiptRejected("worktree entry lease coverage is not exact")
                declared_paths.extend(paths)
            contract_worktree = payload.get("contract_worktree")
            if request["public_contract_changes"]:
                if (
                    not isinstance(contract_worktree, Mapping)
                    or contract_worktree.get("writer_identity") != role_identities.get("contract-architect")
                    or sorted(contract_worktree.get("contract_ids", [])) != sorted(request["public_contract_changes"])
                ):
                    raise ReceiptRejected("public contracts lack one explicit contract worktree")
                contract_paths = contract_worktree.get("owned_paths")
                if not isinstance(contract_paths, list) or any(
                    path not in request["requested_paths"]
                    or owned_path_class(str(path)) != "contracts"
                    for path in contract_paths
                ):
                    raise ReceiptRejected("contract worktree owns an undeclared non-contract path")
                expected_contract_keys = {
                    *("contract:" + item for item in request["public_contract_changes"]),
                    *("path:" + path for path in contract_paths),
                }
                if set(contract_worktree.get("lease_keys", [])) != expected_contract_keys:
                    raise ReceiptRejected("contract worktree lease coverage is not exact")
                declared_paths.extend(contract_paths)
            elif contract_worktree is not None:
                raise ReceiptRejected("contract worktree exists without a declared contract change")
            canonical_declared = [canonical_repository_path_key(path) for path in declared_paths]
            if (
                not declared_paths
                or len(canonical_declared) != len(set(canonical_declared))
                or sorted(canonical_declared)
                != sorted(canonical_repository_path_key(path) for path in request["requested_paths"])
            ):
                raise ReceiptRejected("worktree plan does not cover each requested path exactly once")
            return

        if contract_id == "worktree.lease/v1":
            prior_plans = self._causal_payloads(command, "worktree.plan/v1")
            if not prior_plans or payload.get("plan_id") != prior_plans[-1].get("plan_id"):
                raise ReceiptRejected("worktree lease is not bound to its causal plan")
            plan = prior_plans[-1]
            expected_lock_keys: set[str] = set()
            for entry in plan.get("entries", []):
                if (
                    isinstance(entry, Mapping)
                    and entry.get("writer_identity") == command["actor_identity"]
                ):
                    expected_lock_keys.update(entry.get("lease_keys", []))
            contract_entry = plan.get("contract_worktree")
            if (
                isinstance(contract_entry, Mapping)
                and contract_entry.get("writer_identity") == command["actor_identity"]
            ):
                expected_lock_keys.update(contract_entry.get("lease_keys", []))
            lock_keys = payload.get("lock_keys")
            lock_tokens = payload.get("lock_tokens")
            if (
                not expected_lock_keys
                or
                payload.get("holder_identity") != command["actor_identity"]
                or isinstance(payload.get("fencing_token"), bool)
                or not isinstance(payload.get("fencing_token"), int)
                or payload["fencing_token"] < 1
                or payload.get("status") != "ACTIVE"
                or not isinstance(lock_keys, list)
                or set(lock_keys) != expected_lock_keys
                or len(lock_keys) != len(expected_lock_keys)
                or not isinstance(lock_tokens, Mapping)
                or set(lock_tokens) != expected_lock_keys
                or any(value != payload["fencing_token"] for value in lock_tokens.values())
            ):
                raise ReceiptRejected("worktree lease holder or provider-domain fence drift")
            try:
                acquired_at = dt.datetime.fromisoformat(str(payload.get("acquired_at")).replace("Z", "+00:00"))
                expires_at = dt.datetime.fromisoformat(str(payload.get("expires_at")).replace("Z", "+00:00"))
            except ValueError as exc:
                raise ReceiptRejected("worktree lease time window is invalid") from exc
            now = self._now()
            if (
                acquired_at.tzinfo is None
                or expires_at.tzinfo is None
                or acquired_at >= expires_at
                or acquired_at > now + dt.timedelta(minutes=1)
                or expires_at <= now
                or expires_at - acquired_at > dt.timedelta(minutes=30)
            ):
                raise ReceiptRejected("worktree lease is expired or outside its bounded window")
            return

        if contract_id == "trusted.test.result/v1":
            instructions = self._causal_payloads(command, "instruction.receipt/v1")
            worktrees = self._causal_payloads(command, "worktree.plan/v1")
            leases = self._causal_payloads(command, "worktree.lease/v1")
            if not instructions or not worktrees or not leases:
                raise ReceiptRejected("test result lacks instruction or worktree truth")
            if payload.get("instruction_receipt_id") != instructions[-1].get("receipt_id") or payload.get("worktree_plan_id") not in {item.get("plan_id") for item in worktrees}:
                raise ReceiptRejected("test result reference drift")
            expected_check_id = "factory." + str(command["operation"])
            subject_module = context.get("subject_module")
            runner_attestation = payload.get("runner_attestation")
            role_identities = context.get("role_identities")
            if (
                subject_module not in request["target_modules"]
                or payload.get("request_id") != command["request_id"]
                or payload.get("module_id") != subject_module
                or payload.get("check_id") != expected_check_id
                or payload.get("suite_id") != expected_check_id
                or payload.get("required_checks_sha256") != sha256([expected_check_id])
                or not isinstance(runner_attestation, Mapping)
                or payload.get("runner_identity") != runner_attestation.get("signer_identity")
                or not isinstance(role_identities, Mapping)
                or payload.get("runner_identity") in set(role_identities.values())
                or runner_attestation.get("payload_sha256") != sha256({
                    key: value for key, value in payload.items()
                    if key != "runner_attestation"
                })
            ):
                raise ReceiptRejected("test result is not bound to its exact host operation")
            if payload.get("required") is not True or payload.get("status") != "PASS" or payload.get("release_allowed") is not True:
                raise ReceiptRejected("required verification is not an exact PASS")
            if payload.get("tested_commit") == request["baseline_commit"]:
                raise ReceiptRejected("verification tested the unchanged baseline instead of a changeset head")
            if command["operation"] != "verify-implementation-ready":
                ready_results = self._causal_payloads(
                    command, "trusted.test.result/v1",
                )
                matching_ready = [
                    item for item in ready_results
                    if item.get("module_id") == subject_module
                    and item.get("check_id") == "factory.verify-implementation-ready"
                    and item.get("status") == "PASS"
                    and item.get("release_allowed") is True
                ]
                if (
                    len(matching_ready) != 1
                    or matching_ready[0].get("tested_commit")
                    != payload.get("tested_commit")
                ):
                    raise ReceiptRejected(
                        "independent verification lacks its changeset-ready barrier",
                    )
            matching_leases = [item for item in leases if item.get("lease_id") == payload.get("lease_id")]
            if (
                not matching_leases
                or payload.get("fencing_token") != matching_leases[-1].get("fencing_token")
                or payload.get("worktree_plan_id") != matching_leases[-1].get("plan_id")
                or not any(
                    str(key).startswith("module:%s:" % subject_module)
                    or str(key).startswith("path:Modules/%s/" % subject_module)
                    for key in matching_leases[-1].get("lock_keys", [])
                )
            ):
                raise ReceiptRejected("test result is not bound to its provider-domain worktree lease")
            lease = matching_leases[-1]
            if lease.get("status") != "ACTIVE":
                raise ExpiredWorktreeLease(
                    "trusted result attempted to consume an inactive worktree lease",
                )
            try:
                acquired_at = dt.datetime.fromisoformat(
                    str(lease.get("acquired_at")).replace("Z", "+00:00"),
                )
                expires_at = dt.datetime.fromisoformat(
                    str(lease.get("expires_at")).replace("Z", "+00:00"),
                )
                started_at = dt.datetime.fromisoformat(
                    str(payload.get("started_at")).replace("Z", "+00:00"),
                )
                finished_at = dt.datetime.fromisoformat(
                    str(payload.get("finished_at")).replace("Z", "+00:00"),
                )
            except ValueError as exc:
                raise ReceiptRejected(
                    "trusted result or lease time window is invalid",
                ) from exc
            if any(
                value.tzinfo is None
                for value in (acquired_at, expires_at, started_at, finished_at)
            ):
                raise ReceiptRejected(
                    "trusted result or lease time window lacks a timezone",
                )
            now = self._now()
            if expires_at <= now:
                raise ExpiredWorktreeLease(
                    "worktree lease expired before trusted evidence consumption",
                )
            if not acquired_at <= started_at <= finished_at <= expires_at:
                raise ReceiptRejected(
                    "trusted result execution occurred outside its lease window",
                )
            if finished_at > now + dt.timedelta(minutes=1):
                raise ReceiptRejected(
                    "trusted result completion time is implausibly in the future",
                )
            return

        if contract_id == "merge.decision/v1":
            results = self._causal_payloads(command, "trusted.test.result/v1")
            result_ids = [item.get("result_id") for item in results]
            expected_ids = set(result_ids)
            tested_commits = {item.get("tested_commit") for item in results}
            evidence_ids = payload.get("evidence_ids", [])
            if (
                not expected_ids
                or len(result_ids) != len(expected_ids)
                or not isinstance(evidence_ids, list)
                or len(evidence_ids) != len(set(evidence_ids))
                or payload.get("outcome") != "APPROVED"
                or set(evidence_ids) != expected_ids
            ):
                raise ReceiptRejected("merge decision lacks all independent evidence")
            if (
                len(tested_commits) != 1
                or request["baseline_commit"] in tested_commits
                or payload.get("integration_commit") not in tested_commits
            ):
                raise ReceiptRejected("merge decision is not bound to one tested changeset head")
            return

        if contract_id == "artifact.descriptor/v1":
            decisions = self._causal_payloads(command, "merge.decision/v1")
            if not decisions or payload.get("merge_decision_id") != decisions[-1].get("decision_id"):
                raise ReceiptRejected("artifact lacks its merge decision")
            if payload.get("integration_commit") != decisions[-1].get("integration_commit"):
                raise ReceiptRejected("artifact integration commit drift")
            return

        if contract_id == "upgrade.event/v1":
            material = {key: value for key, value in payload.items() if key != "event_sha256"}
            if payload.get("payload_sha256") != sha256(payload.get("payload")) or payload.get("event_sha256") != sha256(material):
                raise ReceiptRejected("evidence event hash chain material is invalid")
            return

        if contract_id == "rollout.event/v1":
            transitions = {
                "verify-signed-bom": "BOM_SIGNED", "run-shadow": "SHADOW",
                "run-canary": "CANARY", "run-rolling": "ROLLING",
                "run-soak": "SOAKING", "complete-release": "COMPLETED",
            }
            if payload.get("current_state") != transitions.get(command["operation"]):
                raise ReceiptRejected("rollout event state does not match the fixed operation")
            artifacts = self._causal_payloads(command, "artifact.descriptor/v1")
            prior_rollouts = self._causal_payloads(command, "rollout.event/v1")
            external = context.get("external_fact")
            if not artifacts or payload.get("artifact_sha256") != artifacts[-1].get("artifact_sha256"):
                raise ReceiptRejected("rollout artifact digest drift")
            if command["operation"] == "verify-signed-bom":
                if not isinstance(external, Mapping) or external.get("verified") is not True:
                    raise ReceiptRejected("BOM verification lacks an external verified fact")
                if payload.get("bom_sha256") != external.get("bom_sha256") or payload.get("artifact_sha256") != external.get("artifact_sha256"):
                    raise ReceiptRejected("rollout does not preserve the externally verified BOM tuple")
                native_stop_trust = context.get("bound_native_stop_authority_trust")
                if command["mode"] == "PRODUCTION" and (
                    not isinstance(native_stop_trust, Mapping)
                    or native_stop_trust.get("verified") is not True
                    or native_stop_trust.get("contract_id")
                    != "release.bom.native.stop.authority.trust/v1"
                    or native_stop_trust.get("release_bom_sha256")
                    != payload.get("bom_sha256")
                ):
                    raise ReceiptRejected(
                        "production rollout lacks exact native-stop authority trust",
                    )
            elif not prior_rollouts or payload.get("bom_sha256") != prior_rollouts[-1].get("bom_sha256") or payload.get("artifact_sha256") != prior_rollouts[-1].get("artifact_sha256"):
                raise ReceiptRejected("rollout BOM or artifact continuity drift")
            simulated = command["mode"] == "SIMULATION"
            if payload.get("simulation_only") is not simulated:
                raise ReceiptRejected("rollout event simulation label does not match workflow mode")
            if simulated and (payload.get("evidence_kind") != "SIMULATION" or payload.get("side_effect_count") != 0):
                raise ReceiptRejected("simulated rollout reported non-simulation evidence or side effects")
            return

        if contract_id == "rollback.plan/v1":
            release_fact = context.get("bound_release_fact")
            authorization = context.get("bound_rollback_authorization")
            if (
                not isinstance(release_fact, Mapping)
                or release_fact.get("verified") is not True
                or not isinstance(authorization, Mapping)
                or authorization.get("verified") is not True
            ):
                raise ReceiptRejected("rollback plan lacks bound stable BOM or authorization truth")
            if (
                payload.get("upgrade_id") != request["workflow_id"]
                or payload.get("request_sha256") != sha256(request)
                or payload.get("deadline_seconds", 301) > 300
                or payload.get("rollback_unit") != "ROLLBACKABLE"
                or payload.get("target_bom_id") != release_fact.get("previous_stable_bom_id")
                or payload.get("target_bom_sha256") != release_fact.get("previous_stable_bom_sha256")
                or payload.get("stable_bom_verification_id") != release_fact.get("previous_stable_verification_id")
                or authorization.get("request_sha256") != sha256(request)
                or authorization.get("reason_code") != context.get("rollback_reason")
                or authorization.get("previous_stable_bom_sha256") != payload.get("target_bom_sha256")
                or authorization.get("previous_stable_verification_id") != payload.get("stable_bom_verification_id")
            ):
                raise ReceiptRejected("rollback plan is not bound to signed previous stable BOM and authorization truth")
            return

        if contract_id == "rollback.result/v1":
            plans = self._causal_payloads(command, "rollback.plan/v1")
            release_fact = context.get("bound_release_fact")
            authorization = context.get("bound_rollback_authorization")
            if not plans or not isinstance(release_fact, Mapping) or not isinstance(authorization, Mapping):
                raise ReceiptRejected("rollback result lacks its plan, stable BOM, or authorization fact")
            plan = plans[-1]
            if (
                payload.get("rollback_id") != plan.get("rollback_id")
                or payload.get("upgrade_id") != request["workflow_id"]
                or payload.get("rollback_unit") != plan.get("rollback_unit")
                or payload.get("request_sha256") != sha256(request)
                or payload.get("plan_sha256") != sha256(plan)
                or payload.get("target_bom_sha256") != plan.get("target_bom_sha256")
                or payload.get("target_bom_sha256") != release_fact.get("previous_stable_bom_sha256")
                or payload.get("stable_bom_verification_id") != plan.get("stable_bom_verification_id")
                or payload.get("stable_bom_verification_id") != release_fact.get("previous_stable_verification_id")
                or payload.get("outcome") != "ROLLED_BACK"
                or payload.get("verified_postconditions") is not True
                or payload.get("active_bom_sha256") != payload.get("target_bom_sha256")
                or payload.get("authorization_id") != authorization.get("fact_id")
                or authorization.get("request_sha256") != sha256(request)
                or authorization.get("reason_code") != context.get("rollback_reason")
                or authorization.get("previous_stable_bom_sha256") != payload.get("target_bom_sha256")
                or authorization.get("previous_stable_verification_id") != payload.get("stable_bom_verification_id")
            ):
                raise ReceiptRejected("rollback result does not preserve plan, authorization, and stable BOM continuity")
            return

        raise ReceiptRejected("output contract has no composition invariant")

    @staticmethod
    def _failure_state(current: str, statuses: set[str]) -> str:
        if "QUARANTINED" in statuses:
            return "QUARANTINED"
        if current in {"ROLLBACK_REQUIRED", "ROLLING_BACK"}:
            return "WAITING_EXTERNAL"
        if "STALE" in statuses:
            return "STALE"
        if "WAITING_EXTERNAL" in statuses:
            return "WAITING_EXTERNAL"
        if current in ROLLOUT_STATES.union({"BOM_SIGNED"}):
            return "ROLLBACK_REQUIRED"
        return "FAILED"

    def _transition(self, workflow_id: str, current: str, target: str, payload: Mapping[str, Any], idempotency_key: str, fence: int, *, event_type: str = "STATE_TRANSITIONED") -> None:
        if target not in LEGAL_TRANSITIONS.get(current, frozenset()):
            raise IllegalTransition("illegal workflow transition %s->%s" % (current, target))
        self._guarded_repository_write(
            "CONTINUE", workflow_id,
            lambda: self._repository.transition(
                workflow_id, target, event_type, payload, idempotency_key, fence, utc_now(),
            ),
        )


__all__ = [
    "FactoryControlPlaneHost", "FactoryHostError", "IllegalTransition",
    "IdempotencyConflict", "InMemoryWorkflowRepository", "InvalidWorkflowRequest",
    "NATIVE_STOP_AUTHORITY_TRUST_FACT", "ReceiptRejected", "RoleSeparationError",
    "SimulationReceiptVerifier",
    "StaleFence", "StaticRuntimeControlAuthority", "canonical_bytes",
    "logical_request_sha256", "opaque_idempotency", "sha256", "utc_now", "validate_event_stream",
    "validate_role_binding", "validate_workflow_request",
]
