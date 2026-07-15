"""Fail-closed, declarative rollback orchestration for the DPS AI Factory.

The controller owns orchestration only.  Cryptographic BOM verification,
human authorization, durable evidence storage, and execution of the fixed
step vocabulary are injected process-bound ports.  Request JSON is never
allowed to report its own role, approval, signature status, or executable
command.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from enum import Enum
import hashlib
import json
import math
import re
import time
from typing import Any, Callable, Mapping, Protocol, Sequence


ZERO_HASH = "0" * 64
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
SOUL_PATTERN = re.compile(r"^soul_[a-f0-9]{64}\Z")
DEVICE_PATTERN = re.compile(r"^db_[a-f0-9]{32}\Z")
ACCOUNT_PATTERN = re.compile(r"^pa_[a-f0-9]{32}\Z")
TRACE_PATTERN = re.compile(r"^trace_[a-f0-9]{32}\Z")
IDEMPOTENCY_PATTERN = re.compile(r"^idem_[a-f0-9]{64}\Z")
ROLLOUT_EVENT_PATTERN = re.compile(r"^rollout-[0-9a-f]{32}$")
IDENTIFIER_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$")
EXTERNAL_EFFECT_PATTERN = re.compile(r"^[a-z][a-z0-9.-]{1,31}:[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$")
REASON_CODE_PATTERN = re.compile(r"^[A-Z][A-Z0-9_:-]{2,127}$")

REQUEST_FIELDS = {
    "schema_version",
    "contract_id",
    "producer_module",
    "soul_id",
    "device_binding_id",
    "platform_account_id",
    "trace_id",
    "idempotency_key",
    "occurred_at",
    "privacy_class",
    "rollback_id",
    "upgrade_id",
    "rollout_event_id",
    "rollback_unit",
    "current_bom_sha256",
    "previous_stable_bom_id",
    "previous_stable_bom_sha256",
    "deadline_seconds",
    "external_effects",
    "compensation_plan",
}

EVENT_FIELDS = {
    "schema_version",
    "contract_id",
    "producer_module",
    "soul_id",
    "device_binding_id",
    "platform_account_id",
    "trace_id",
    "idempotency_key",
    "occurred_at",
    "privacy_class",
    "event_id",
    "stream_id",
    "sequence",
    "event_type",
    "source_module",
    "payload",
    "payload_sha256",
    "previous_event_sha256",
    "event_sha256",
    "append_status",
}
EVENT_ID_PATTERN = re.compile(r"^event-[0-9a-f]{32}$")
EVENT_TYPE_PATTERN = re.compile(r"^[A-Z][A-Z0-9_]{1,63}$")

PLAN_FIELDS = {
    "schema_version",
    "contract_id",
    "producer_module",
    "soul_id",
    "device_binding_id",
    "platform_account_id",
    "trace_id",
    "idempotency_key",
    "occurred_at",
    "privacy_class",
    "rollback_id",
    "upgrade_id",
    "rollback_unit",
    "target_bom_id",
    "target_bom_sha256",
    "deadline_seconds",
    "ordered_steps",
    "compensation_plan",
    "request_sha256",
    "stable_bom_verification_id",
}

RESULT_FIELDS = {
    "schema_version",
    "contract_id",
    "producer_module",
    "soul_id",
    "device_binding_id",
    "platform_account_id",
    "trace_id",
    "idempotency_key",
    "occurred_at",
    "privacy_class",
    "rollback_id",
    "upgrade_id",
    "rollback_unit",
    "outcome",
    "completed_steps",
    "duration_seconds",
    "target_bom_sha256",
    "active_bom_sha256",
    "verified_postconditions",
    "compensation_evidence_ids",
    "reason",
    "request_sha256",
    "plan_sha256",
    "authorization_id",
    "stable_bom_verification_id",
}

ACCEPTED_PAYLOAD_FIELDS = {
    "rollback_id",
    "request_sha256",
    "plan_sha256",
    "plan",
    "authorization_id",
    "authorization_kind",
    "approver_identity",
    "stable_bom_verification_id",
    "started_at_logical",
    "deadline_at_logical",
}

STARTED_PAYLOAD_FIELDS = {
    "rollback_id",
    "request_sha256",
    "plan_sha256",
    "step",
    "step_execution_id",
}

OBSERVED_PAYLOAD_FIELDS = STARTED_PAYLOAD_FIELDS | {"receipt"}
TERMINAL_PAYLOAD_FIELDS = {
    "rollback_id",
    "request_sha256",
    "plan_sha256",
    "result",
}
CONFLICT_PAYLOAD_FIELDS = {
    "rollback_id",
    "accepted_request_sha256",
    "conflicting_request_sha256",
    "reason_code",
}


class RollbackError(RuntimeError):
    """Base class for a fail-closed rollback decision."""


class InvalidRollbackRequest(RollbackError):
    pass


class AuthorizationRejected(RollbackError):
    pass


class StableBomRejected(RollbackError):
    pass


class ConflictingRollbackRequest(RollbackError):
    pass


class CorruptEvidenceStream(RollbackError):
    pass


class EvidenceWriteFailed(RollbackError):
    pass


class InvalidStepReceipt(RollbackError):
    pass


class RollbackStep(str, Enum):
    STOP_ROUTING = "STOP_ROUTING"
    DRAIN = "DRAIN"
    RECONCILE = "RECONCILE"
    SWITCH_BOM = "SWITCH_BOM"
    COMPENSATE = "COMPENSATE"
    VERIFY = "VERIFY"


class StepOutcome(str, Enum):
    PASS = "PASS"
    FAIL = "FAIL"
    UNKNOWN_OUTCOME = "UNKNOWN_OUTCOME"


ROLLBACKABLE_STEPS = (
    RollbackStep.STOP_ROUTING,
    RollbackStep.DRAIN,
    RollbackStep.RECONCILE,
    RollbackStep.SWITCH_BOM,
    RollbackStep.VERIFY,
)
NON_ROLLBACKABLE_STEPS = (
    RollbackStep.STOP_ROUTING,
    RollbackStep.DRAIN,
    RollbackStep.RECONCILE,
    RollbackStep.COMPENSATE,
    RollbackStep.VERIFY,
)


@dataclass(frozen=True)
class VerifiedStableBom:
    """Result returned only by the process-bound signature verifier."""

    bom_id: str
    bom_sha256: str
    verification_id: str
    signer_identity: str
    status: str = "STABLE"


@dataclass(frozen=True)
class VerifiedRollbackGrant:
    """Human R3 approval already verified by the injected authority."""

    authorization_id: str
    authorization_kind: str
    approver_identity: str
    rollback_id: str
    upgrade_id: str
    rollout_event_id: str
    request_sha256: str
    plan_sha256: str
    target_bom_sha256: str
    rollback_unit: str
    allowed_steps: tuple[RollbackStep, ...]


@dataclass(frozen=True)
class StepInstruction:
    """Closed declarative instruction; it deliberately has no command text."""

    rollback_id: str
    upgrade_id: str
    step_execution_id: str
    step: RollbackStep
    current_bom_sha256: str
    target_bom_sha256: str
    deadline_at_logical: float
    external_effects: tuple[str, ...]
    compensation_plan: str | None


@dataclass(frozen=True)
class StepReceipt:
    receipt_id: str
    step: RollbackStep
    outcome: StepOutcome
    native_result_verified: bool
    postcondition_verified: bool
    drain_complete: bool
    reconciliation_complete: bool
    active_bom_sha256: str | None
    compensation_evidence_ids: tuple[str, ...]
    compensated_effects_sha256: str | None
    reason: str | None


class StableBomVerifier(Protocol):
    def verify(self, bom_id: str, bom_sha256: str) -> VerifiedStableBom: ...


class RollbackAuthority(Protocol):
    def authorize(
        self,
        *,
        rollback_id: str,
        upgrade_id: str,
        rollout_event_id: str,
        request_sha256: str,
        plan_sha256: str,
        target_bom_sha256: str,
        rollback_unit: str,
        ordered_steps: tuple[RollbackStep, ...],
    ) -> VerifiedRollbackGrant: ...


class DeclarativeStepExecutor(Protocol):
    def execute(self, instruction: StepInstruction) -> StepReceipt: ...


class EvidenceLedgerPort(Protocol):
    def read_stream(self, stream_id: str) -> Sequence[Mapping[str, Any]]: ...

    def append(self, command: Mapping[str, Any]) -> Mapping[str, Any]: ...


@dataclass
class _RecoveredState:
    accepted: dict[str, Any] | None
    started: dict[RollbackStep, dict[str, Any]]
    observed: dict[RollbackStep, StepReceipt]
    terminal_result: dict[str, Any] | None
    conflict_hashes: set[str]


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def sha256(value: Any) -> str:
    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def _event_material(event: Mapping[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in event.items() if key not in {"event_sha256", "append_status"}}


def _require_exact(value: Mapping[str, Any], fields: set[str], label: str) -> None:
    if set(value) != fields:
        missing = sorted(fields - set(value))
        unknown = sorted(set(value) - fields)
        raise InvalidRollbackRequest(f"{label} has missing={missing} unknown={unknown}")


def _require_text(value: Any, label: str, *, maximum: int = 128) -> str:
    if not isinstance(value, str) or not value or len(value) > maximum:
        raise InvalidRollbackRequest(f"{label} must be non-empty text of at most {maximum} characters")
    return value


def _require_sha256(value: Any, label: str) -> str:
    if not isinstance(value, str) or SHA256_PATTERN.fullmatch(value) is None:
        raise InvalidRollbackRequest(f"{label} must be a lowercase SHA-256 digest")
    return value


def _require_identity(value: Any, pattern: re.Pattern[str], label: str) -> None:
    if value is not None and (not isinstance(value, str) or pattern.fullmatch(value) is None):
        raise InvalidRollbackRequest(f"{label} is not canonical")


def _require_opaque_id(value: Any, pattern: re.Pattern[str], label: str) -> None:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise InvalidRollbackRequest(f"{label} is not canonical")


def _require_timestamp(value: Any, label: str) -> str:
    text = _require_text(value, label, maximum=64)
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError as exc:
        raise InvalidRollbackRequest(f"{label} must be RFC3339") from exc
    if parsed.tzinfo is None or parsed.utcoffset() is None:
        raise InvalidRollbackRequest(f"{label} must include a timezone")
    return text


def validate_request(request: Mapping[str, Any]) -> dict[str, Any]:
    if not isinstance(request, Mapping):
        raise InvalidRollbackRequest("rollback request must be an object")
    normalized = dict(request)
    _require_exact(normalized, REQUEST_FIELDS, "rollback request")
    if normalized["schema_version"] != "1.0.0" or normalized["contract_id"] != "rollback.request/v1":
        raise InvalidRollbackRequest("unknown rollback request version")
    if normalized["producer_module"] != "factory-release-controller":
        raise InvalidRollbackRequest("rollback request producer is not trusted")
    if normalized["privacy_class"] != "internal":
        raise InvalidRollbackRequest("rollback request privacy class must be internal")
    _require_identity(normalized["soul_id"], SOUL_PATTERN, "soul_id")
    _require_identity(normalized["device_binding_id"], DEVICE_PATTERN, "device_binding_id")
    _require_identity(normalized["platform_account_id"], ACCOUNT_PATTERN, "platform_account_id")
    _require_opaque_id(normalized["trace_id"], TRACE_PATTERN, "trace_id")
    _require_opaque_id(normalized["idempotency_key"], IDEMPOTENCY_PATTERN, "idempotency_key")
    _require_timestamp(normalized["occurred_at"], "occurred_at")
    rollback_id = _require_text(normalized["rollback_id"], "rollback_id")
    upgrade_id = _require_text(normalized["upgrade_id"], "upgrade_id")
    if IDENTIFIER_PATTERN.fullmatch(rollback_id) is None or IDENTIFIER_PATTERN.fullmatch(upgrade_id) is None:
        raise InvalidRollbackRequest("rollback_id and upgrade_id must use the canonical identifier alphabet")
    if not isinstance(normalized["rollout_event_id"], str) or ROLLOUT_EVENT_PATTERN.fullmatch(normalized["rollout_event_id"]) is None:
        raise InvalidRollbackRequest("rollout_event_id is not canonical")
    _require_sha256(normalized["current_bom_sha256"], "current_bom_sha256")
    previous_bom_id = _require_text(normalized["previous_stable_bom_id"], "previous_stable_bom_id")
    if len(previous_bom_id) < 8:
        raise InvalidRollbackRequest("previous_stable_bom_id must contain at least 8 characters")
    _require_sha256(normalized["previous_stable_bom_sha256"], "previous_stable_bom_sha256")
    deadline = normalized["deadline_seconds"]
    if isinstance(deadline, bool) or not isinstance(deadline, int) or not 1 <= deadline <= 300:
        raise InvalidRollbackRequest("deadline_seconds must be an integer from 1 through 300")
    effects = normalized["external_effects"]
    if not isinstance(effects, list) or len(effects) > 100:
        raise InvalidRollbackRequest("external_effects must be an array of at most 100 items")
    if any(not isinstance(item, str) or EXTERNAL_EFFECT_PATTERN.fullmatch(item) is None for item in effects):
        raise InvalidRollbackRequest("external_effects items must be opaque canonical references")
    if len(effects) != len(set(effects)):
        raise InvalidRollbackRequest("external_effects must be unique")
    unit = normalized["rollback_unit"]
    compensation = normalized["compensation_plan"]
    if unit == "ROLLBACKABLE":
        if effects or compensation is not None:
            raise InvalidRollbackRequest("ROLLBACKABLE requests cannot hide external effects or compensation")
    elif unit == "NON_ROLLBACKABLE":
        if (
            not effects
            or not isinstance(compensation, str)
            or IDENTIFIER_PATTERN.fullmatch(compensation) is None
        ):
            raise InvalidRollbackRequest("NON_ROLLBACKABLE requests require effect refs and an opaque compensation plan ID")
    else:
        raise InvalidRollbackRequest("unknown rollback_unit")
    return normalized


def _validate_verified_stable_bom(value: Any, request: Mapping[str, Any]) -> VerifiedStableBom:
    if not isinstance(value, VerifiedStableBom):
        raise StableBomRejected("stable BOM verifier returned an untrusted result type")
    if (
        value.bom_id != request["previous_stable_bom_id"]
        or value.bom_sha256 != request["previous_stable_bom_sha256"]
        or value.status != "STABLE"
        or not isinstance(value.bom_sha256, str)
        or SHA256_PATTERN.fullmatch(value.bom_sha256) is None
        or not isinstance(value.verification_id, str)
        or IDENTIFIER_PATTERN.fullmatch(value.verification_id) is None
        or not isinstance(value.signer_identity, str)
        or IDENTIFIER_PATTERN.fullmatch(value.signer_identity) is None
    ):
        raise StableBomRejected("previous stable BOM verification is not exact")
    return value


def _validate_grant(
    value: Any,
    *,
    controller_identity: str,
    rollback_id: str,
    upgrade_id: str,
    rollout_event_id: str,
    request_sha256: str,
    plan_sha256: str,
    target_bom_sha256: str,
    rollback_unit: str,
    ordered_steps: tuple[RollbackStep, ...],
) -> VerifiedRollbackGrant:
    if not isinstance(value, VerifiedRollbackGrant):
        raise AuthorizationRejected("authority returned an untrusted result type")
    if (
        value.authorization_kind != "HUMAN_R3"
        or value.approver_identity == controller_identity
        or not isinstance(value.approver_identity, str)
        or IDENTIFIER_PATTERN.fullmatch(value.approver_identity) is None
        or not isinstance(value.authorization_id, str)
        or IDENTIFIER_PATTERN.fullmatch(value.authorization_id) is None
        or value.rollback_id != rollback_id
        or value.upgrade_id != upgrade_id
        or value.rollout_event_id != rollout_event_id
        or value.request_sha256 != request_sha256
        or value.plan_sha256 != plan_sha256
        or value.target_bom_sha256 != target_bom_sha256
        or value.rollback_unit != rollback_unit
        or value.allowed_steps != ordered_steps
    ):
        raise AuthorizationRejected("rollback authorization does not match the exact request and plan")
    return value


def _build_plan(
    request: Mapping[str, Any],
    request_sha256: str,
    stable: VerifiedStableBom,
) -> tuple[dict[str, Any], tuple[RollbackStep, ...]]:
    ordered = ROLLBACKABLE_STEPS if request["rollback_unit"] == "ROLLBACKABLE" else NON_ROLLBACKABLE_STEPS
    plan = {
        "schema_version": "1.0.0",
        "contract_id": "rollback.plan/v1",
        "producer_module": "factory-rollback-controller",
        "soul_id": request["soul_id"],
        "device_binding_id": request["device_binding_id"],
        "platform_account_id": request["platform_account_id"],
        "trace_id": request["trace_id"],
        "idempotency_key": "idem_" + sha256({"rollback_id": request["rollback_id"], "purpose": "plan"}),
        "occurred_at": request["occurred_at"],
        "privacy_class": "internal",
        "rollback_id": request["rollback_id"],
        "upgrade_id": request["upgrade_id"],
        "rollback_unit": request["rollback_unit"],
        "target_bom_id": stable.bom_id,
        "target_bom_sha256": stable.bom_sha256,
        "deadline_seconds": request["deadline_seconds"],
        "ordered_steps": [step.value for step in ordered],
        "compensation_plan": request["compensation_plan"],
        "request_sha256": request_sha256,
        "stable_bom_verification_id": stable.verification_id,
    }
    if set(plan) != PLAN_FIELDS:
        raise AssertionError("internal rollback plan shape drift")
    return plan, ordered


def _receipt_to_dict(receipt: StepReceipt) -> dict[str, Any]:
    value = asdict(receipt)
    value["step"] = receipt.step.value
    value["outcome"] = receipt.outcome.value
    value["compensation_evidence_ids"] = list(receipt.compensation_evidence_ids)
    return value


def _receipt_from_mapping(value: Mapping[str, Any]) -> StepReceipt:
    expected = {
        "receipt_id",
        "step",
        "outcome",
        "native_result_verified",
        "postcondition_verified",
        "drain_complete",
        "reconciliation_complete",
        "active_bom_sha256",
        "compensation_evidence_ids",
        "compensated_effects_sha256",
        "reason",
    }
    if not isinstance(value, Mapping) or set(value) != expected:
        raise InvalidStepReceipt("step receipt has unknown or missing fields")
    try:
        step = RollbackStep(value["step"])
        outcome = StepOutcome(value["outcome"])
    except (TypeError, ValueError) as exc:
        raise InvalidStepReceipt("step receipt enum is unknown") from exc
    receipt_id = value["receipt_id"]
    if not isinstance(receipt_id, str) or IDENTIFIER_PATTERN.fullmatch(receipt_id) is None:
        raise InvalidStepReceipt("receipt_id is not canonical")
    boolean_fields = (
        "native_result_verified",
        "postcondition_verified",
        "drain_complete",
        "reconciliation_complete",
    )
    if any(type(value[field]) is not bool for field in boolean_fields):
        raise InvalidStepReceipt("step receipt verification flags must be booleans")
    active = value["active_bom_sha256"]
    if active is not None and (not isinstance(active, str) or SHA256_PATTERN.fullmatch(active) is None):
        raise InvalidStepReceipt("active_bom_sha256 is invalid")
    evidence = value["compensation_evidence_ids"]
    if (
        not isinstance(evidence, (list, tuple))
        or len(evidence) != len(set(evidence))
        or any(not isinstance(item, str) or IDENTIFIER_PATTERN.fullmatch(item) is None for item in evidence)
    ):
        raise InvalidStepReceipt("compensation evidence IDs are invalid")
    compensated = value["compensated_effects_sha256"]
    if compensated is not None and (not isinstance(compensated, str) or SHA256_PATTERN.fullmatch(compensated) is None):
        raise InvalidStepReceipt("compensated_effects_sha256 is invalid")
    reason = value["reason"]
    if reason is not None and (not isinstance(reason, str) or REASON_CODE_PATTERN.fullmatch(reason) is None):
        raise InvalidStepReceipt("receipt reason must be a bounded reason code or null")
    return StepReceipt(
        receipt_id=receipt_id,
        step=step,
        outcome=outcome,
        native_result_verified=value["native_result_verified"],
        postcondition_verified=value["postcondition_verified"],
        drain_complete=value["drain_complete"],
        reconciliation_complete=value["reconciliation_complete"],
        active_bom_sha256=active,
        compensation_evidence_ids=tuple(evidence),
        compensated_effects_sha256=compensated,
        reason=reason,
    )


def _validate_receipt(receipt: Any, instruction: StepInstruction) -> StepReceipt:
    if not isinstance(receipt, StepReceipt):
        raise InvalidStepReceipt("executor returned an untrusted receipt type")
    normalized = _receipt_from_mapping(_receipt_to_dict(receipt))
    if normalized.step != instruction.step:
        raise InvalidStepReceipt("receipt step does not match the fixed instruction")
    return normalized


def _validate_event_stream(events: Sequence[Mapping[str, Any]], stream_id: str) -> list[dict[str, Any]]:
    normalized: list[dict[str, Any]] = []
    previous = ZERO_HASH
    for expected_sequence, raw in enumerate(events, start=1):
        if not isinstance(raw, Mapping) or set(raw) != EVENT_FIELDS:
            raise CorruptEvidenceStream("upgrade event has unknown or missing fields")
        event = dict(raw)
        if (
            event["schema_version"] != "1.0.0"
            or event["contract_id"] != "upgrade.event/v1"
            or event["producer_module"] != "factory-evidence-ledger"
            or event["source_module"] != "factory-rollback-controller"
            or event["privacy_class"] != "internal"
            or event["append_status"] not in {"APPENDED", "IDEMPOTENT_REPLAY"}
            or event["stream_id"] != stream_id
            or event["sequence"] != expected_sequence
        ):
            raise CorruptEvidenceStream("upgrade event contract, source, stream, or sequence is invalid")
        try:
            _require_identity(event["soul_id"], SOUL_PATTERN, "event.soul_id")
            _require_identity(event["device_binding_id"], DEVICE_PATTERN, "event.device_binding_id")
            _require_identity(event["platform_account_id"], ACCOUNT_PATTERN, "event.platform_account_id")
            _require_opaque_id(event["trace_id"], TRACE_PATTERN, "event.trace_id")
            _require_opaque_id(event["idempotency_key"], IDEMPOTENCY_PATTERN, "event.idempotency_key")
            _require_timestamp(event["occurred_at"], "event.occurred_at")
        except InvalidRollbackRequest as exc:
            raise CorruptEvidenceStream(str(exc)) from exc
        if not isinstance(event["event_id"], str) or EVENT_ID_PATTERN.fullmatch(event["event_id"]) is None:
            raise CorruptEvidenceStream("upgrade event_id is invalid")
        if not isinstance(event["event_type"], str) or EVENT_TYPE_PATTERN.fullmatch(event["event_type"]) is None:
            raise CorruptEvidenceStream("upgrade event_type is invalid")
        if isinstance(event["sequence"], bool) or not isinstance(event["sequence"], int):
            raise CorruptEvidenceStream("upgrade event sequence is invalid")
        if not isinstance(event["payload"], Mapping) or event["payload_sha256"] != sha256(event["payload"]):
            raise CorruptEvidenceStream("upgrade event payload digest mismatch")
        if event["previous_event_sha256"] != previous:
            raise CorruptEvidenceStream("upgrade event hash chain is discontinuous")
        calculated = sha256(_event_material(event))
        if event["event_sha256"] != calculated:
            raise CorruptEvidenceStream("upgrade event digest mismatch")
        previous = calculated
        normalized.append(event)
    return normalized


def _validate_result_shape(result: Mapping[str, Any]) -> dict[str, Any]:
    if not isinstance(result, Mapping) or set(result) != RESULT_FIELDS:
        raise CorruptEvidenceStream("stored rollback result has unknown or missing fields")
    normalized = dict(result)
    if (
        normalized["schema_version"] != "1.0.0"
        or normalized["contract_id"] != "rollback.result/v1"
        or normalized["producer_module"] != "factory-rollback-controller"
        or normalized["privacy_class"] != "internal"
        or normalized["outcome"] not in {
            "ROLLED_BACK",
            "COMPENSATION_REQUIRED",
            "COMPENSATED",
            "FAILED",
            "DEADLINE_EXCEEDED",
        }
    ):
        raise CorruptEvidenceStream("stored rollback result is invalid")
    try:
        _require_identity(normalized["soul_id"], SOUL_PATTERN, "result.soul_id")
        _require_identity(normalized["device_binding_id"], DEVICE_PATTERN, "result.device_binding_id")
        _require_identity(normalized["platform_account_id"], ACCOUNT_PATTERN, "result.platform_account_id")
        _require_opaque_id(normalized["trace_id"], TRACE_PATTERN, "result.trace_id")
        _require_opaque_id(normalized["idempotency_key"], IDEMPOTENCY_PATTERN, "result.idempotency_key")
        _require_timestamp(normalized["occurred_at"], "result.occurred_at")
        _require_sha256(normalized["target_bom_sha256"], "result.target_bom_sha256")
        _require_sha256(normalized["request_sha256"], "result.request_sha256")
        _require_sha256(normalized["plan_sha256"], "result.plan_sha256")
    except InvalidRollbackRequest as exc:
        raise CorruptEvidenceStream(str(exc)) from exc
    if (
        not isinstance(normalized["rollback_id"], str)
        or IDENTIFIER_PATTERN.fullmatch(normalized["rollback_id"]) is None
        or not isinstance(normalized["upgrade_id"], str)
        or IDENTIFIER_PATTERN.fullmatch(normalized["upgrade_id"]) is None
        or normalized["rollback_unit"] not in {"ROLLBACKABLE", "NON_ROLLBACKABLE"}
    ):
        raise CorruptEvidenceStream("stored rollback result identity or unit is invalid")
    completed = normalized["completed_steps"]
    if (
        not isinstance(completed, list)
        or len(completed) > 5
        or len(completed) != len(set(completed))
        or any(item not in {step.value for step in RollbackStep} for item in completed)
    ):
        raise CorruptEvidenceStream("stored rollback completed_steps are invalid")
    duration = normalized["duration_seconds"]
    if isinstance(duration, bool) or not isinstance(duration, (int, float)) or not math.isfinite(duration) or duration < 0:
        raise CorruptEvidenceStream("stored rollback duration is invalid")
    active = normalized["active_bom_sha256"]
    if active is not None and (not isinstance(active, str) or SHA256_PATTERN.fullmatch(active) is None):
        raise CorruptEvidenceStream("stored active BOM digest is invalid")
    if type(normalized["verified_postconditions"]) is not bool:
        raise CorruptEvidenceStream("stored verified_postconditions must be a boolean")
    evidence_ids = normalized["compensation_evidence_ids"]
    if (
        not isinstance(evidence_ids, list)
        or len(evidence_ids) != len(set(evidence_ids))
        or any(not isinstance(item, str) or IDENTIFIER_PATTERN.fullmatch(item) is None for item in evidence_ids)
    ):
        raise CorruptEvidenceStream("stored compensation evidence IDs are invalid")
    reason = normalized["reason"]
    if reason is not None and (not isinstance(reason, str) or REASON_CODE_PATTERN.fullmatch(reason) is None):
        raise CorruptEvidenceStream("stored rollback reason is invalid")
    if (
        not isinstance(normalized["authorization_id"], str)
        or IDENTIFIER_PATTERN.fullmatch(normalized["authorization_id"]) is None
        or not isinstance(normalized["stable_bom_verification_id"], str)
        or IDENTIFIER_PATTERN.fullmatch(normalized["stable_bom_verification_id"]) is None
    ):
        raise CorruptEvidenceStream("stored authorization or stable BOM verification identity is invalid")
    return normalized


def _recover(events: Sequence[Mapping[str, Any]], stream_id: str) -> _RecoveredState:
    accepted: dict[str, Any] | None = None
    started: dict[RollbackStep, dict[str, Any]] = {}
    observed: dict[RollbackStep, StepReceipt] = {}
    terminal: dict[str, Any] | None = None
    conflicts: set[str] = set()
    for event in _validate_event_stream(events, stream_id):
        payload = dict(event["payload"])
        event_type = event["event_type"]
        if event_type == "ROLLBACK_PLAN_ACCEPTED":
            if accepted is not None or set(payload) != ACCEPTED_PAYLOAD_FIELDS:
                raise CorruptEvidenceStream("rollback plan acceptance is duplicated or malformed")
            if not isinstance(payload["plan"], Mapping) or set(payload["plan"]) != PLAN_FIELDS:
                raise CorruptEvidenceStream("stored rollback plan shape is invalid")
            try:
                _require_identity(payload["plan"]["soul_id"], SOUL_PATTERN, "plan.soul_id")
                _require_identity(payload["plan"]["device_binding_id"], DEVICE_PATTERN, "plan.device_binding_id")
                _require_identity(payload["plan"]["platform_account_id"], ACCOUNT_PATTERN, "plan.platform_account_id")
                _require_opaque_id(payload["plan"]["trace_id"], TRACE_PATTERN, "plan.trace_id")
                _require_opaque_id(payload["plan"]["idempotency_key"], IDEMPOTENCY_PATTERN, "plan.idempotency_key")
            except InvalidRollbackRequest as exc:
                raise CorruptEvidenceStream(str(exc)) from exc
            try:
                stored_order = tuple(RollbackStep(item) for item in payload["plan"]["ordered_steps"])
            except (TypeError, ValueError) as exc:
                raise CorruptEvidenceStream("stored rollback plan contains an unknown step") from exc
            expected_order = (
                ROLLBACKABLE_STEPS
                if payload["plan"]["rollback_unit"] == "ROLLBACKABLE"
                else NON_ROLLBACKABLE_STEPS
                if payload["plan"]["rollback_unit"] == "NON_ROLLBACKABLE"
                else None
            )
            if expected_order is None or stored_order != expected_order:
                raise CorruptEvidenceStream("stored rollback plan violates the exact step order")
            if payload["request_sha256"] != payload["plan"]["request_sha256"]:
                raise CorruptEvidenceStream("stored request digest is inconsistent")
            if payload["plan_sha256"] != sha256(payload["plan"]):
                raise CorruptEvidenceStream("stored plan digest mismatch")
            if payload["authorization_kind"] != "HUMAN_R3":
                raise CorruptEvidenceStream("stored authorization kind is invalid")
            if (
                not isinstance(payload["authorization_id"], str)
                or IDENTIFIER_PATTERN.fullmatch(payload["authorization_id"]) is None
                or not isinstance(payload["approver_identity"], str)
                or IDENTIFIER_PATTERN.fullmatch(payload["approver_identity"]) is None
                or not isinstance(payload["stable_bom_verification_id"], str)
                or IDENTIFIER_PATTERN.fullmatch(payload["stable_bom_verification_id"]) is None
            ):
                raise CorruptEvidenceStream("stored approval or stable BOM verification reference is invalid")
            if (
                not isinstance(payload["started_at_logical"], (int, float))
                or isinstance(payload["started_at_logical"], bool)
                or not math.isfinite(payload["started_at_logical"])
            ):
                raise CorruptEvidenceStream("stored logical start is invalid")
            if (
                not isinstance(payload["deadline_at_logical"], (int, float))
                or isinstance(payload["deadline_at_logical"], bool)
                or not math.isfinite(payload["deadline_at_logical"])
            ):
                raise CorruptEvidenceStream("stored logical deadline is invalid")
            if not math.isclose(
                payload["deadline_at_logical"] - payload["started_at_logical"],
                payload["plan"]["deadline_seconds"],
                rel_tol=0.0,
                abs_tol=1e-6,
            ):
                raise CorruptEvidenceStream("stored logical deadline does not match the plan")
            accepted = payload
        elif event_type == "ROLLBACK_STEP_STARTED":
            if accepted is None or terminal is not None or set(payload) != STARTED_PAYLOAD_FIELDS:
                raise CorruptEvidenceStream("step start is out of order or malformed")
            try:
                step = RollbackStep(payload["step"])
            except (TypeError, ValueError) as exc:
                raise CorruptEvidenceStream("step start contains an unknown step") from exc
            ordered_steps = tuple(RollbackStep(item) for item in accepted["plan"]["ordered_steps"])
            if any(candidate not in observed for candidate in started):
                raise CorruptEvidenceStream("a second step started before the first was observed")
            if any(receipt.outcome is not StepOutcome.PASS for receipt in observed.values()):
                raise CorruptEvidenceStream("a step started after a non-pass outcome")
            if len(observed) >= len(ordered_steps) or step is not ordered_steps[len(observed)]:
                raise CorruptEvidenceStream("step start violates the exact authorized order")
            if step in started or payload["request_sha256"] != accepted["request_sha256"] or payload["plan_sha256"] != accepted["plan_sha256"]:
                raise CorruptEvidenceStream("step start is duplicated or bound to another plan")
            started[step] = payload
        elif event_type == "ROLLBACK_STEP_OBSERVED":
            if accepted is None or terminal is not None or set(payload) != OBSERVED_PAYLOAD_FIELDS:
                raise CorruptEvidenceStream("step observation is out of order or malformed")
            try:
                step = RollbackStep(payload["step"])
            except (TypeError, ValueError) as exc:
                raise CorruptEvidenceStream("step observation contains an unknown step") from exc
            if step not in started or step in observed:
                raise CorruptEvidenceStream("step observation has no unique start")
            if payload["step_execution_id"] != started[step]["step_execution_id"]:
                raise CorruptEvidenceStream("step execution identity changed")
            if payload["request_sha256"] != accepted["request_sha256"] or payload["plan_sha256"] != accepted["plan_sha256"]:
                raise CorruptEvidenceStream("step observation is bound to another plan")
            try:
                receipt = _receipt_from_mapping(payload["receipt"])
            except InvalidStepReceipt as exc:
                raise CorruptEvidenceStream(str(exc)) from exc
            if receipt.step != step:
                raise CorruptEvidenceStream("stored receipt step mismatch")
            observed[step] = receipt
        elif event_type == "ROLLBACK_RESULT_RECORDED":
            if accepted is None or terminal is not None or set(payload) != TERMINAL_PAYLOAD_FIELDS:
                raise CorruptEvidenceStream("rollback result is duplicated, premature, or malformed")
            if payload["request_sha256"] != accepted["request_sha256"] or payload["plan_sha256"] != accepted["plan_sha256"]:
                raise CorruptEvidenceStream("stored result is bound to another plan")
            terminal = _validate_result_shape(payload["result"])
        elif event_type == "ROLLBACK_CONFLICT_QUARANTINED":
            if accepted is None or set(payload) != CONFLICT_PAYLOAD_FIELDS:
                raise CorruptEvidenceStream("rollback conflict evidence is malformed")
            if payload["accepted_request_sha256"] != accepted["request_sha256"] or payload["reason_code"] != "ROLLBACK_ID_HASH_CONFLICT":
                raise CorruptEvidenceStream("rollback conflict evidence is not bound to the accepted request")
            if (
                not isinstance(payload["conflicting_request_sha256"], str)
                or SHA256_PATTERN.fullmatch(payload["conflicting_request_sha256"]) is None
            ):
                raise CorruptEvidenceStream("rollback conflict digest is invalid")
            conflicts.add(payload["conflicting_request_sha256"])
        else:
            raise CorruptEvidenceStream(f"unknown rollback event type: {event_type}")
    return _RecoveredState(accepted, started, observed, terminal, conflicts)


class RollbackController:
    """Orchestrate one exact rollback stream with durable, replayable evidence."""

    def __init__(
        self,
        *,
        controller_identity: str,
        stable_bom_verifier: StableBomVerifier,
        authority: RollbackAuthority,
        executor: DeclarativeStepExecutor,
        evidence_ledger: EvidenceLedgerPort,
        logical_clock: Callable[[], float],
        utc_now: Callable[[], str] | None = None,
    ) -> None:
        if not isinstance(controller_identity, str) or IDENTIFIER_PATTERN.fullmatch(controller_identity) is None:
            raise ValueError("controller_identity must be a process-bound canonical identity")
        self._controller_identity = controller_identity
        self._stable_bom_verifier = stable_bom_verifier
        self._authority = authority
        self._executor = executor
        self._evidence_ledger = evidence_ledger
        self._logical_clock = logical_clock
        self._utc_now = utc_now or (lambda: datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"))

    @staticmethod
    def _stream_id(rollback_id: str) -> str:
        return f"rollback:{rollback_id}"

    def _read(self, stream_id: str) -> tuple[list[dict[str, Any]], _RecoveredState]:
        try:
            raw = self._evidence_ledger.read_stream(stream_id)
        except Exception as exc:
            raise CorruptEvidenceStream("cannot read the external evidence stream") from exc
        if not isinstance(raw, Sequence) or isinstance(raw, (str, bytes, bytearray)):
            raise CorruptEvidenceStream("evidence ledger returned a non-sequence stream")
        events = _validate_event_stream(raw, stream_id)
        return events, _recover(events, stream_id)

    def _append(
        self,
        *,
        request: Mapping[str, Any],
        events: list[dict[str, Any]],
        event_type: str,
        payload: Mapping[str, Any],
        idempotency_suffix: str,
    ) -> dict[str, Any]:
        stream_id = self._stream_id(request["rollback_id"])
        payload_value = dict(payload)
        now = self._utc_now()
        _require_timestamp(now, "utc_now")
        command = {
            "schema_version": "1.0.0",
            "contract_id": "upgrade.event.append/v1",
            "producer_module": "factory-rollback-controller",
            "soul_id": request["soul_id"],
            "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"],
            "trace_id": request["trace_id"],
            "idempotency_key": "idem_" + sha256({
                "rollback_id": request["rollback_id"],
                "suffix": idempotency_suffix,
            }),
            "occurred_at": now,
            "privacy_class": "internal",
            "stream_id": stream_id,
            "expected_sequence": len(events),
            "event_type": event_type,
            "payload": payload_value,
            "payload_sha256": sha256(payload_value),
        }
        try:
            raw = self._evidence_ledger.append(command)
        except Exception as exc:
            raise EvidenceWriteFailed(f"durable append failed for {event_type}") from exc
        combined = events + [dict(raw)] if isinstance(raw, Mapping) else events
        try:
            validated = _validate_event_stream(combined, stream_id)
        except (CorruptEvidenceStream, TypeError, ValueError) as exc:
            raise EvidenceWriteFailed(f"ledger did not return durable exact evidence for {event_type}") from exc
        appended = validated[-1]
        if appended["event_type"] != event_type or appended["payload_sha256"] != command["payload_sha256"]:
            raise EvidenceWriteFailed(f"ledger acknowledged different evidence for {event_type}")
        events.append(appended)
        return appended

    def _quarantine_conflict(
        self,
        request: Mapping[str, Any],
        request_sha256: str,
        events: list[dict[str, Any]],
        state: _RecoveredState,
    ) -> None:
        if request_sha256 not in state.conflict_hashes:
            self._append(
                request=request,
                events=events,
                event_type="ROLLBACK_CONFLICT_QUARANTINED",
                payload={
                    "rollback_id": request["rollback_id"],
                    "accepted_request_sha256": state.accepted["request_sha256"],
                    "conflicting_request_sha256": request_sha256,
                    "reason_code": "ROLLBACK_ID_HASH_CONFLICT",
                },
                idempotency_suffix=f"conflict:{request_sha256}",
            )
        raise ConflictingRollbackRequest("rollback_id was already bound to a different request digest")

    def _now_logical(self) -> float:
        now = self._logical_clock()
        if isinstance(now, bool) or not isinstance(now, (int, float)) or not math.isfinite(now) or now < 0:
            raise RollbackError("logical clock returned an invalid value")
        return float(now)

    def _validate_terminal_for_request(
        self,
        request: Mapping[str, Any],
        state: _RecoveredState,
    ) -> dict[str, Any]:
        if state.accepted is None or state.terminal_result is None:
            raise CorruptEvidenceStream("terminal validation requires an accepted plan and result")
        accepted = state.accepted
        plan = accepted["plan"]
        result = state.terminal_result
        if accepted["approver_identity"] == self._controller_identity:
            raise CorruptEvidenceStream("stored rollback evidence violates approver/controller separation")
        expected_plan_bindings = {
            "soul_id": request["soul_id"],
            "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"],
            "trace_id": request["trace_id"],
            "occurred_at": request["occurred_at"],
            "rollback_id": request["rollback_id"],
            "upgrade_id": request["upgrade_id"],
            "rollback_unit": request["rollback_unit"],
            "target_bom_id": request["previous_stable_bom_id"],
            "target_bom_sha256": request["previous_stable_bom_sha256"],
            "deadline_seconds": request["deadline_seconds"],
            "compensation_plan": request["compensation_plan"],
            "request_sha256": accepted["request_sha256"],
            "stable_bom_verification_id": accepted["stable_bom_verification_id"],
        }
        if any(plan.get(field) != value for field, value in expected_plan_bindings.items()):
            raise CorruptEvidenceStream("stored plan is not exactly bound to the accepted rollback request")
        if (
            result["rollback_id"] != request["rollback_id"]
            or result["upgrade_id"] != request["upgrade_id"]
            or result["rollback_unit"] != request["rollback_unit"]
            or result["request_sha256"] != accepted["request_sha256"]
            or result["plan_sha256"] != accepted["plan_sha256"]
            or result["authorization_id"] != accepted["authorization_id"]
            or result["stable_bom_verification_id"] != accepted["stable_bom_verification_id"]
            or result["target_bom_sha256"] != plan["target_bom_sha256"]
        ):
            raise CorruptEvidenceStream("stored terminal result is not bound to the accepted request and plan")
        try:
            _require_timestamp(result["occurred_at"], "result.occurred_at")
        except InvalidRollbackRequest as exc:
            raise CorruptEvidenceStream(str(exc)) from exc
        if (
            isinstance(result["duration_seconds"], bool)
            or not isinstance(result["duration_seconds"], (int, float))
            or not math.isfinite(result["duration_seconds"])
            or result["duration_seconds"] < 0
        ):
            raise CorruptEvidenceStream("stored terminal duration is invalid")

        ordered = tuple(RollbackStep(item) for item in plan["ordered_steps"])
        completed: list[RollbackStep] = []
        compensation_evidence: list[str] = []
        active_bom = request["current_bom_sha256"]
        for step in ordered:
            receipt = state.observed.get(step)
            if receipt is None:
                break
            passed, _ = RollbackController._pass_semantics(receipt, request=request, step=step)
            if not passed:
                break
            completed.append(step)
            compensation_evidence.extend(receipt.compensation_evidence_ids)
            if receipt.active_bom_sha256 is not None:
                active_bom = receipt.active_bom_sha256
        if result["completed_steps"] != [step.value for step in completed]:
            raise CorruptEvidenceStream("stored terminal completed_steps do not match verified step evidence")
        if result["active_bom_sha256"] != active_bom:
            raise CorruptEvidenceStream("stored terminal active BOM does not match verified step evidence")
        if result["compensation_evidence_ids"] != list(dict.fromkeys(compensation_evidence)):
            raise CorruptEvidenceStream("stored terminal compensation evidence does not match step evidence")

        outcome = result["outcome"]
        if outcome == "ROLLED_BACK":
            if (
                request["rollback_unit"] != "ROLLBACKABLE"
                or tuple(completed) != ROLLBACKABLE_STEPS
                or active_bom != request["previous_stable_bom_sha256"]
                or result["verified_postconditions"] is not True
                or result["reason"] is not None
            ):
                raise CorruptEvidenceStream("ROLLED_BACK terminal evidence is incomplete or contradictory")
        elif outcome == "COMPENSATED":
            compensation_receipt = state.observed.get(RollbackStep.COMPENSATE)
            if (
                request["rollback_unit"] != "NON_ROLLBACKABLE"
                or tuple(completed) != NON_ROLLBACKABLE_STEPS
                or active_bom != request["current_bom_sha256"]
                or compensation_receipt is None
                or compensation_receipt.compensated_effects_sha256 != sha256(request["external_effects"])
                or not compensation_evidence
                or result["verified_postconditions"] is not True
            ):
                raise CorruptEvidenceStream("COMPENSATED terminal evidence is incomplete or contradictory")
        else:
            if result["verified_postconditions"] is not False:
                raise CorruptEvidenceStream("non-success terminal evidence cannot claim verified postconditions")
            if outcome == "COMPENSATION_REQUIRED" and request["rollback_unit"] != "NON_ROLLBACKABLE":
                raise CorruptEvidenceStream("COMPENSATION_REQUIRED is invalid for a rollbackable unit")
        return dict(result)

    @staticmethod
    def _step_execution_id(rollback_id: str, plan_sha256: str, step: RollbackStep) -> str:
        return "step-" + sha256({"rollback_id": rollback_id, "plan_sha256": plan_sha256, "step": step.value})[:32]

    @staticmethod
    def _pass_semantics(
        receipt: StepReceipt,
        *,
        request: Mapping[str, Any],
        step: RollbackStep,
    ) -> tuple[bool, str | None]:
        if receipt.outcome is not StepOutcome.PASS:
            return False, receipt.reason or receipt.outcome.value
        if not receipt.native_result_verified or not receipt.postcondition_verified:
            return False, "STEP_RESULT_OR_POSTCONDITION_UNVERIFIED"
        if step is RollbackStep.DRAIN and not receipt.drain_complete:
            return False, "DRAIN_INCOMPLETE"
        if step is RollbackStep.RECONCILE and not receipt.reconciliation_complete:
            return False, "RECONCILIATION_INCOMPLETE"
        if step is RollbackStep.SWITCH_BOM and receipt.active_bom_sha256 != request["previous_stable_bom_sha256"]:
            return False, "ACTIVE_BOM_NOT_PREVIOUS_STABLE"
        if step is RollbackStep.COMPENSATE:
            expected = sha256(request["external_effects"])
            if not receipt.compensation_evidence_ids or receipt.compensated_effects_sha256 != expected:
                return False, "COMPENSATION_EVIDENCE_INCOMPLETE"
        if step is RollbackStep.VERIFY:
            expected_active = (
                request["previous_stable_bom_sha256"]
                if request["rollback_unit"] == "ROLLBACKABLE"
                else request["current_bom_sha256"]
            )
            if receipt.active_bom_sha256 != expected_active:
                return False, "FINAL_ACTIVE_BOM_MISMATCH"
        return True, None

    def _result(
        self,
        *,
        request: Mapping[str, Any],
        request_sha256: str,
        plan_sha256: str,
        stable: VerifiedStableBom,
        grant: VerifiedRollbackGrant,
        outcome: str,
        completed_steps: Sequence[RollbackStep],
        started_at: float,
        active_bom_sha256: str | None,
        compensation_evidence_ids: Sequence[str],
        reason: str | None,
    ) -> dict[str, Any]:
        duration = max(0.0, self._now_logical() - started_at)
        result = {
            "schema_version": "1.0.0",
            "contract_id": "rollback.result/v1",
            "producer_module": "factory-rollback-controller",
            "soul_id": request["soul_id"],
            "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"],
            "trace_id": request["trace_id"],
            "idempotency_key": "idem_" + sha256({"rollback_id": request["rollback_id"], "purpose": "result"}),
            "occurred_at": self._utc_now(),
            "privacy_class": "internal",
            "rollback_id": request["rollback_id"],
            "upgrade_id": request["upgrade_id"],
            "rollback_unit": request["rollback_unit"],
            "outcome": outcome,
            "completed_steps": [step.value for step in completed_steps],
            "duration_seconds": duration,
            "target_bom_sha256": stable.bom_sha256,
            "active_bom_sha256": active_bom_sha256,
            "verified_postconditions": outcome in {"ROLLED_BACK", "COMPENSATED"},
            "compensation_evidence_ids": list(dict.fromkeys(compensation_evidence_ids)),
            "reason": reason,
            "request_sha256": request_sha256,
            "plan_sha256": plan_sha256,
            "authorization_id": grant.authorization_id,
            "stable_bom_verification_id": stable.verification_id,
        }
        if set(result) != RESULT_FIELDS:
            raise AssertionError("internal rollback result shape drift")
        _require_timestamp(result["occurred_at"], "result.occurred_at")
        return result

    def _record_result(
        self,
        *,
        request: Mapping[str, Any],
        events: list[dict[str, Any]],
        result: Mapping[str, Any],
        request_sha256: str,
        plan_sha256: str,
    ) -> dict[str, Any]:
        self._append(
            request=request,
            events=events,
            event_type="ROLLBACK_RESULT_RECORDED",
            payload={
                "rollback_id": request["rollback_id"],
                "request_sha256": request_sha256,
                "plan_sha256": plan_sha256,
                "result": dict(result),
            },
            idempotency_suffix="result",
        )
        return dict(result)

    def execute(self, request: Mapping[str, Any]) -> dict[str, Any]:
        """Execute or resume one rollback; a terminal result is always durable first."""

        request_value = validate_request(request)
        request_digest = sha256(request_value)
        stream_id = self._stream_id(request_value["rollback_id"])
        events, state = self._read(stream_id)
        if state.accepted is not None and state.accepted["request_sha256"] != request_digest:
            self._quarantine_conflict(request_value, request_digest, events, state)

        try:
            stable_raw = self._stable_bom_verifier.verify(
                request_value["previous_stable_bom_id"],
                request_value["previous_stable_bom_sha256"],
            )
        except StableBomRejected:
            raise
        except Exception as exc:
            raise StableBomRejected("previous stable signed BOM could not be verified") from exc
        stable = _validate_verified_stable_bom(stable_raw, request_value)
        plan, ordered_steps = _build_plan(request_value, request_digest, stable)
        plan_digest = sha256(plan)
        try:
            grant_raw = self._authority.authorize(
                rollback_id=request_value["rollback_id"],
                upgrade_id=request_value["upgrade_id"],
                rollout_event_id=request_value["rollout_event_id"],
                request_sha256=request_digest,
                plan_sha256=plan_digest,
                target_bom_sha256=stable.bom_sha256,
                rollback_unit=request_value["rollback_unit"],
                ordered_steps=ordered_steps,
            )
        except AuthorizationRejected:
            raise
        except Exception as exc:
            raise AuthorizationRejected("external R3 rollback authorization was not verified") from exc
        grant = _validate_grant(
            grant_raw,
            controller_identity=self._controller_identity,
            rollback_id=request_value["rollback_id"],
            upgrade_id=request_value["upgrade_id"],
            rollout_event_id=request_value["rollout_event_id"],
            request_sha256=request_digest,
            plan_sha256=plan_digest,
            target_bom_sha256=stable.bom_sha256,
            rollback_unit=request_value["rollback_unit"],
            ordered_steps=ordered_steps,
        )

        if state.accepted is None:
            started_at = self._now_logical()
            self._append(
                request=request_value,
                events=events,
                event_type="ROLLBACK_PLAN_ACCEPTED",
                payload={
                    "rollback_id": request_value["rollback_id"],
                    "request_sha256": request_digest,
                    "plan_sha256": plan_digest,
                    "plan": plan,
                    "authorization_id": grant.authorization_id,
                    "authorization_kind": grant.authorization_kind,
                    "approver_identity": grant.approver_identity,
                    "stable_bom_verification_id": stable.verification_id,
                    "started_at_logical": started_at,
                    "deadline_at_logical": started_at + request_value["deadline_seconds"],
                },
                idempotency_suffix="plan",
            )
            _, state = self._read(stream_id)
        else:
            accepted = state.accepted
            if (
                accepted["plan_sha256"] != plan_digest
                or accepted["plan"] != plan
                or accepted["authorization_id"] != grant.authorization_id
                or accepted["approver_identity"] != grant.approver_identity
                or accepted["stable_bom_verification_id"] != stable.verification_id
            ):
                raise CorruptEvidenceStream("recovered plan, approval, or signed BOM verification drifted")

        if state.terminal_result is not None:
            return self._validate_terminal_for_request(request_value, state)

        accepted = state.accepted
        started_at = float(accepted["started_at_logical"])
        deadline_at = float(accepted["deadline_at_logical"])
        now = self._now_logical()
        if now < started_at:
            raise CorruptEvidenceStream("logical clock moved backwards across rollback recovery")

        completed: list[RollbackStep] = []
        compensation_evidence: list[str] = []
        active_bom = request_value["current_bom_sha256"]
        terminal_reason: str | None = None
        terminal_outcome: str | None = None

        for expected_step in ordered_steps:
            if expected_step in state.observed:
                receipt = state.observed[expected_step]
                passed, reason = self._pass_semantics(receipt, request=request_value, step=expected_step)
                if not passed:
                    terminal_reason = reason
                    terminal_outcome = (
                        "COMPENSATION_REQUIRED"
                        if request_value["rollback_unit"] == "NON_ROLLBACKABLE"
                        else "FAILED"
                    )
                    break
                completed.append(expected_step)
                compensation_evidence.extend(receipt.compensation_evidence_ids)
                if receipt.active_bom_sha256 is not None:
                    active_bom = receipt.active_bom_sha256
                continue
            if expected_step in state.started:
                terminal_reason = f"UNKNOWN_OUTCOME_AFTER_INTERRUPTION:{expected_step.value}"
                terminal_outcome = (
                    "COMPENSATION_REQUIRED"
                    if request_value["rollback_unit"] == "NON_ROLLBACKABLE"
                    else "FAILED"
                )
                break
            if self._now_logical() > deadline_at:
                terminal_reason = "DEADLINE_EXCEEDED"
                terminal_outcome = "DEADLINE_EXCEEDED"
                break

            step_execution_id = self._step_execution_id(request_value["rollback_id"], plan_digest, expected_step)
            started_payload = {
                "rollback_id": request_value["rollback_id"],
                "request_sha256": request_digest,
                "plan_sha256": plan_digest,
                "step": expected_step.value,
                "step_execution_id": step_execution_id,
            }
            self._append(
                request=request_value,
                events=events,
                event_type="ROLLBACK_STEP_STARTED",
                payload=started_payload,
                idempotency_suffix=f"{expected_step.value}:started",
            )
            instruction = StepInstruction(
                rollback_id=request_value["rollback_id"],
                upgrade_id=request_value["upgrade_id"],
                step_execution_id=step_execution_id,
                step=expected_step,
                current_bom_sha256=request_value["current_bom_sha256"],
                target_bom_sha256=stable.bom_sha256,
                deadline_at_logical=deadline_at,
                external_effects=tuple(request_value["external_effects"]),
                compensation_plan=request_value["compensation_plan"],
            )
            try:
                receipt = _validate_receipt(self._executor.execute(instruction), instruction)
            except InvalidStepReceipt:
                raise
            except Exception as exc:
                raise InvalidStepReceipt(f"executor failed without a verified receipt for {expected_step.value}") from exc
            observed_payload = dict(started_payload)
            observed_payload["receipt"] = _receipt_to_dict(receipt)
            self._append(
                request=request_value,
                events=events,
                event_type="ROLLBACK_STEP_OBSERVED",
                payload=observed_payload,
                idempotency_suffix=f"{expected_step.value}:observed",
            )
            passed, reason = self._pass_semantics(receipt, request=request_value, step=expected_step)
            if not passed:
                terminal_reason = reason
                terminal_outcome = (
                    "COMPENSATION_REQUIRED"
                    if request_value["rollback_unit"] == "NON_ROLLBACKABLE"
                    else "FAILED"
                )
                break
            completed.append(expected_step)
            compensation_evidence.extend(receipt.compensation_evidence_ids)
            if receipt.active_bom_sha256 is not None:
                active_bom = receipt.active_bom_sha256
            if self._now_logical() > deadline_at:
                terminal_reason = "DEADLINE_EXCEEDED"
                terminal_outcome = "DEADLINE_EXCEEDED"
                break

        if terminal_outcome is None:
            if tuple(completed) != ordered_steps:
                raise CorruptEvidenceStream("completed steps are not the exact authorized sequence")
            if request_value["rollback_unit"] == "ROLLBACKABLE":
                if active_bom != stable.bom_sha256:
                    terminal_outcome = "FAILED"
                    terminal_reason = "FINAL_ACTIVE_BOM_NOT_PREVIOUS_STABLE"
                else:
                    terminal_outcome = "ROLLED_BACK"
            else:
                expected_effects = sha256(request_value["external_effects"])
                compensation_receipt = state.observed.get(RollbackStep.COMPENSATE)
                if compensation_receipt is None:
                    # The current process receipt is not yet in recovered state; read exact evidence.
                    _, recovered_after_steps = self._read(stream_id)
                    compensation_receipt = recovered_after_steps.observed.get(RollbackStep.COMPENSATE)
                if (
                    compensation_receipt is None
                    or compensation_receipt.compensated_effects_sha256 != expected_effects
                    or not compensation_evidence
                ):
                    terminal_outcome = "COMPENSATION_REQUIRED"
                    terminal_reason = "COMPENSATION_EVIDENCE_INCOMPLETE"
                else:
                    terminal_outcome = "COMPENSATED"
                    terminal_reason = "EXTERNAL_EFFECTS_COMPENSATED_NOT_ROLLED_BACK"

        result = self._result(
            request=request_value,
            request_sha256=request_digest,
            plan_sha256=plan_digest,
            stable=stable,
            grant=grant,
            outcome=terminal_outcome,
            completed_steps=completed,
            started_at=started_at,
            active_bom_sha256=active_bom,
            compensation_evidence_ids=compensation_evidence,
            reason=terminal_reason,
        )
        return self._record_result(
            request=request_value,
            events=events,
            result=result,
            request_sha256=request_digest,
            plan_sha256=plan_digest,
        )


def monotonic_logical_clock() -> float:
    """Default monotonic logical clock for composition roots."""

    return time.monotonic()
