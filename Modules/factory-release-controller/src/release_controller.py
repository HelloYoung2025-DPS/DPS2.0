"""Fail-closed DPS Factory release state machine.

Active v2 commands contain only transition intent and canonical receipt
references.  Actor identity, risk, approval, verification results, digests,
and kill-switch state come from a process-bound trusted resolver.  Frozen v1
contracts are classified for quarantine only and never enter the state
machine.  A transition is visible only after the append-only evidence ledger
acknowledges the exact v2 rollout event.
"""

from __future__ import annotations

import hashlib
import json
import re
from collections.abc import Callable, Mapping, Sequence
from dataclasses import asdict, dataclass
from datetime import datetime
from typing import Any


_ZERO_HASH = "0" * 64
_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_UPGRADE_ID = re.compile(
    r"^[a-z0-9](?:[a-z0-9]|[._:-](?=[a-z0-9])){7,127}\Z"
)
_CANONICAL_UTC = re.compile(
    r"^[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
    r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z\Z"
)
_ACTOR_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{0,127}\Z")
_ROLLOUT_EVENT_ID = re.compile(r"^rollout-[0-9a-f]{32}\Z")
_LEDGER_EVENT_ID = re.compile(r"^event-[0-9a-f]{32}\Z")
_ANCHOR_ID = re.compile(r"^anchor_[0-9a-f]{32}\Z")
_RECEIPT_ID = re.compile(
    r"^[a-z0-9](?:[a-z0-9]|[._-](?=[a-z0-9])){7,127}\Z"
)
_RECEIPT_KINDS = frozenset({
    "approval", "artifact", "candidate", "canary", "evidence", "impact",
    "instruction", "merge", "rollback", "shadow", "test", "worktree",
})
_MAX_EVIDENCE_REFS = 64
_MAX_COMMAND_WIRE_BYTES = 64 * 1024
_ACTIVE_V2 = "ACTIVE_V2"
_QUARANTINE_ONLY_V1 = "QUARANTINE_ONLY_V1"
_UNKNOWN_CONTRACT = "UNKNOWN_CONTRACT"
_OPAQUE_IDS = {
    "soul_id": re.compile(r"^soul_[a-f0-9]{64}\Z"),
    "device_binding_id": re.compile(r"^db_[a-f0-9]{32}\Z"),
    "platform_account_id": re.compile(r"^pa_[a-f0-9]{32}\Z"),
    "trace_id": re.compile(r"^trace_[a-f0-9]{32}\Z"),
    "idempotency_key": re.compile(r"^idem_[a-f0-9]{64}\Z"),
}
_COMMAND_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class", "upgrade_id",
    "from_state", "to_state", "transition_evidence",
}
_EVIDENCE_REFERENCE_FIELDS = {"evidence_refs"}
_ROLLOUT_EVENT_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class", "rollout_event_id",
    "upgrade_id", "previous_state", "current_state", "risk_tier",
    "actor_identity", "actor_role", "bom_sha256", "artifact_sha256",
    "evidence_kind", "verification_level", "simulation_only",
    "side_effect_count", "kill_switch_armed", "transition_request_sha256",
    "trusted_facts_sha256", "candidate_validation_sha256",
    "receipt_set_sha256", "evidence_refs",
}
_LEDGER_EVENT_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class", "event_id",
    "stream_id", "sequence", "event_type", "source_module", "payload",
    "payload_sha256", "previous_event_sha256", "event_sha256",
    "append_status",
}
_CANDIDATE_REPORT_FIELDS = {
    "result", "validation_kind", "verification_ceiling", "schema_sha256",
    "trust_policy_id", "bom_id", "bom_sha256", "integration_commit",
    "artifact_set_sha256", "bom_signer", "artifact_signers",
    "evidence_signers", "release_approver", "simulation_only",
    "canary_verified", "scale_verified",
}
_MAIN_STATES = (
    "REQUESTED", "SCOPE_RESOLVED", "INSTRUCTIONS_BOUND",
    "BASELINE_VERIFIED", "CONTRACT_FROZEN", "IMPLEMENTING",
    "CHANGESET_FROZEN", "CANDIDATE_BUILT", "CANDIDATE_VERIFIED",
    "BOM_SIGNED", "SHADOW", "CANARY", "ROLLING", "SOAKING",
    "COMPLETED",
)
_EXCEPTION_STATES = {
    "STALE", "REWORKING", "WAITING_EXTERNAL", "QUARANTINED",
    "ROLLBACK_REQUIRED", "ROLLING_BACK", "ROLLED_BACK", "FAILED",
    "CANCELLED",
}
_ALL_STATES = frozenset(_MAIN_STATES) | _EXCEPTION_STATES
_RISK_TIERS = {"R0", "R1", "R2", "R3", "R4"}
_EVIDENCE_KINDS = {
    "REPOSITORY", "CONTRACT", "INTEGRATION", "SIMULATION", "WINDOWS",
    "DEVICE", "CANARY", "SCALE",
}
_VERIFICATION_RANK = {
    "REPOSITORY_STATIC_VERIFIED": 1,
    "CONTRACT_VERIFIED": 2,
    "INTEGRATION_VERIFIED": 3,
    "WINDOWS_VERIFIED": 4,
    "DEVICE_VERIFIED": 5,
    "CANARY_VERIFIED": 6,
    "SCALE_VERIFIED": 7,
}


def _allowed_transitions() -> dict[str, frozenset[str]]:
    allowed: dict[str, set[str]] = {state: set() for state in _ALL_STATES}
    for current, following in zip(_MAIN_STATES, _MAIN_STATES[1:]):
        allowed[current].add(following)
    for state in _MAIN_STATES[:-1]:
        allowed[state].update({"WAITING_EXTERNAL", "QUARANTINED", "FAILED", "CANCELLED"})
    for state in _MAIN_STATES[1:10]:
        allowed[state].add("STALE")
    for state in _MAIN_STATES[7:-1]:
        allowed[state].add("ROLLBACK_REQUIRED")
    allowed["STALE"].update({"REWORKING", "FAILED", "CANCELLED"})
    allowed["REWORKING"].update({"SCOPE_RESOLVED", "FAILED", "CANCELLED"})
    allowed["WAITING_EXTERNAL"].update({"REWORKING", "FAILED", "CANCELLED"})
    allowed["QUARANTINED"].update({"REWORKING", "ROLLBACK_REQUIRED", "FAILED", "CANCELLED"})
    allowed["ROLLBACK_REQUIRED"].update({"ROLLING_BACK", "FAILED"})
    allowed["ROLLING_BACK"].update({"ROLLED_BACK", "FAILED"})
    return {state: frozenset(targets) for state, targets in allowed.items()}


_ALLOWED_TRANSITIONS = _allowed_transitions()


class ReleaseError(RuntimeError):
    """Base class for fail-closed release errors."""


class InvalidReleaseCommand(ReleaseError):
    pass


class QuarantinedReleaseCommand(InvalidReleaseCommand):
    pass


class UnauthorizedTransition(ReleaseError):
    pass


class IllegalTransition(ReleaseError):
    pass


class DurableAppendError(ReleaseError):
    pass


class CorruptReleaseStream(ReleaseError):
    pass


class IdempotencyConflict(ReleaseError):
    pass


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def sha256(value: Any) -> str:
    data = value if isinstance(value, bytes) else canonical_bytes(value)
    return hashlib.sha256(data).hexdigest()


def _strict_json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise InvalidReleaseCommand(f"duplicate JSON member: {key}")
        value[key] = item
    return value


def parse_rollout_command_json(raw_wire: bytes | str) -> dict[str, Any]:
    if type(raw_wire) is bytes:
        try:
            text = raw_wire.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise InvalidReleaseCommand("rollout command is not strict UTF-8") from exc
    elif type(raw_wire) is str:
        text = raw_wire
    else:
        raise InvalidReleaseCommand("rollout command wire must be bytes or text")
    try:
        encoded = text.encode("utf-8", errors="strict")
    except UnicodeEncodeError as exc:
        raise InvalidReleaseCommand("rollout command contains invalid Unicode") from exc
    if not encoded or len(encoded) > _MAX_COMMAND_WIRE_BYTES:
        raise InvalidReleaseCommand("rollout command wire size is outside the allowed range")
    try:
        value = json.loads(
            text,
            object_pairs_hook=_strict_json_object,
            parse_constant=lambda item: (_ for _ in ()).throw(
                InvalidReleaseCommand(f"invalid JSON constant: {item}")
            ),
        )
    except InvalidReleaseCommand:
        raise
    except (TypeError, ValueError, json.JSONDecodeError) as exc:
        raise InvalidReleaseCommand("rollout command is not strict JSON") from exc
    if type(value) is not dict:
        raise InvalidReleaseCommand("rollout command JSON root must be an object")
    return value


def _is_canonical_utc(value: Any) -> bool:
    if not isinstance(value, str) or _CANONICAL_UTC.fullmatch(value) is None:
        return False
    try:
        datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError:
        return False
    return True


def _opaque_envelope_is_valid(value: Mapping[str, Any]) -> bool:
    for field, pattern in _OPAQUE_IDS.items():
        item = value.get(field)
        if field in {"soul_id", "device_binding_id", "platform_account_id"} and item is None:
            continue
        if not isinstance(item, str) or pattern.fullmatch(item) is None:
            return False
    return True


def _event_material(event: Mapping[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in event.items() if key not in {"event_sha256", "append_status"}}


def _is_valid_receipt_reference(value: Any) -> bool:
    if not isinstance(value, str):
        return False
    parts = value.split(":")
    return (
        len(parts) == 3
        and parts[0] == "receipt"
        and parts[1] in _RECEIPT_KINDS
        and _RECEIPT_ID.fullmatch(parts[2]) is not None
    )


def classify_rollout_command_major(value: Any) -> str:
    if not isinstance(value, Mapping):
        return _UNKNOWN_CONTRACT
    identity = (value.get("schema_version"), value.get("contract_id"))
    if identity == ("1.0.0", "rollout.command/v1"):
        return _QUARANTINE_ONLY_V1
    if identity == ("2.0.0", "rollout.command/v2"):
        return _ACTIVE_V2
    return _UNKNOWN_CONTRACT


def classify_rollout_event_major(value: Any) -> str:
    if not isinstance(value, Mapping):
        return _UNKNOWN_CONTRACT
    identity = (value.get("schema_version"), value.get("contract_id"))
    if identity == ("1.0.0", "rollout.event/v1"):
        return _QUARANTINE_ONLY_V1
    if identity == ("2.0.0", "rollout.event/v2"):
        return _ACTIVE_V2
    return _UNKNOWN_CONTRACT


@dataclass(frozen=True)
class TrustedTransitionFacts:
    """Facts returned by the process-bound evidence/authorization adapter."""

    upgrade_id: str
    resolved_evidence_refs: tuple[str, ...]
    receipt_set_sha256: str
    risk_tier: str
    evidence_kind: str
    verification_level: str
    simulation_only: bool
    side_effect_count: int
    kill_switch_armed: bool
    observed_bom_sha256: str | None
    observed_artifact_sha256: str | None
    candidate_validation: Mapping[str, Any] | None


@dataclass(frozen=True)
class _AuthenticatedLedgerHead:
    """Internal post-authentication replay input used by the semantic verifier."""

    anchor_id: str
    source_module: str
    upgrade_id: str
    sequence: int
    event_sha256: str


FactsResolver = Callable[[str, str, str, tuple[str, ...]], TrustedTransitionFacts]
DurableAppend = Callable[[Mapping[str, Any]], Mapping[str, Any]]


class ReleaseController:
    """Event-sourced release controller with process-bound authorization."""

    def __init__(
        self,
        upgrade_id: str,
        controller_identity: str,
        trusted_controller_identities: Sequence[str],
        facts_resolver: FactsResolver,
        durable_append: DurableAppend,
    ) -> None:
        trusted = frozenset(trusted_controller_identities)
        if not isinstance(upgrade_id, str) or _UPGRADE_ID.fullmatch(upgrade_id) is None:
            raise InvalidReleaseCommand("upgrade_id is invalid")
        if not isinstance(controller_identity, str) or _ACTOR_ID.fullmatch(controller_identity) is None:
            raise UnauthorizedTransition("controller identity is not canonical")
        if not trusted or controller_identity not in trusted:
            raise UnauthorizedTransition("controller identity is not process-bound trusted policy")
        if not callable(facts_resolver) or not callable(durable_append):
            raise InvalidReleaseCommand("trusted resolver and durable append adapter are required")
        self.upgrade_id = upgrade_id
        self.controller_identity = controller_identity
        self._facts_resolver = facts_resolver
        self._durable_append = durable_append
        self.state = "REQUESTED"
        self._sequence = 0
        self._last_event_sha256 = _ZERO_HASH
        self._locked_bom_sha256: str | None = None
        self._locked_artifact_sha256: str | None = None
        self._candidate_validation_sha256: str | None = None
        self._seen_requests: dict[str, tuple[str, dict[str, Any]]] = {}

    @property
    def sequence(self) -> int:
        return self._sequence

    @property
    def locked_bom_sha256(self) -> str | None:
        return self._locked_bom_sha256

    @classmethod
    def recover(
        cls,
        upgrade_id: str,
        controller_identity: str,
        trusted_controller_identities: Sequence[str],
        facts_resolver: FactsResolver,
        durable_append: DurableAppend,
        events: Sequence[Mapping[str, Any]],
    ) -> "ReleaseController":
        del (
            upgrade_id,
            controller_identity,
            trusted_controller_identities,
            facts_resolver,
            durable_append,
            events,
        )
        raise CorruptReleaseStream(
            "authenticated external ledger anchor provider is WAITING_EXTERNAL; "
            "production recovery is disabled"
        )

    @classmethod
    def _recover_after_authenticated_anchor(
        cls,
        upgrade_id: str,
        controller_identity: str,
        trusted_controller_identities: Sequence[str],
        facts_resolver: FactsResolver,
        durable_append: DurableAppend,
        events: Sequence[Mapping[str, Any]],
        trusted_anchor: _AuthenticatedLedgerHead,
    ) -> "ReleaseController":
        """Validate/replay only after a future fixed provider authenticates the head."""
        if isinstance(events, (str, bytes, bytearray)) or not isinstance(events, Sequence):
            raise CorruptReleaseStream("recovery events must be an ordered sequence")
        cls._validate_trusted_ledger_anchor(trusted_anchor, upgrade_id, len(events))
        controller = cls(
            upgrade_id,
            controller_identity,
            trusted_controller_identities,
            facts_resolver,
            durable_append,
        )
        for raw_event in events:
            if not isinstance(raw_event, Mapping):
                raise CorruptReleaseStream("upgrade stream contains a non-object event")
            event = dict(raw_event)
            controller._validate_ledger_event(event, controller._sequence + 1, controller._last_event_sha256)
            rollout = event["payload"]
            controller._validate_rollout_event_shape(rollout)
            controller._validate_ledger_rollout_binding(event, rollout)
            if rollout["upgrade_id"] != upgrade_id:
                raise CorruptReleaseStream("upgrade stream contains another upgrade identity")
            if rollout["actor_identity"] not in frozenset(trusted_controller_identities):
                raise CorruptReleaseStream("rollout event actor is not trusted")
            if rollout["actor_role"] != "release-controller":
                raise CorruptReleaseStream("rollout event actor role is invalid")
            if rollout["previous_state"] != controller.state:
                raise CorruptReleaseStream("rollout state history is discontinuous")
            if rollout["current_state"] not in _ALLOWED_TRANSITIONS[controller.state]:
                raise CorruptReleaseStream("rollout history contains an illegal transition")
            controller._enforce_replayed_rollout_invariants(rollout)
            request_key = rollout["idempotency_key"]
            previous = controller._seen_requests.get(request_key)
            if previous is not None and previous[0] != rollout["transition_request_sha256"]:
                raise CorruptReleaseStream("idempotency key is bound to conflicting transition requests")
            controller._seen_requests[request_key] = (
                rollout["transition_request_sha256"], dict(rollout)
            )
            controller.state = rollout["current_state"]
            controller._sequence = event["sequence"]
            controller._last_event_sha256 = event["event_sha256"]
        if (
            controller._sequence != trusted_anchor.sequence
            or controller._last_event_sha256 != trusted_anchor.event_sha256
        ):
            raise CorruptReleaseStream("replayed stream head does not match the trusted external anchor")
        return controller

    def transition(self, raw_command: Mapping[str, Any] | bytes | str) -> dict[str, Any]:
        if type(raw_command) in {bytes, str}:
            raw_command = parse_rollout_command_json(raw_command)
        command = self._validate_command(raw_command)
        request_sha = sha256(command)
        idempotency_key = command["idempotency_key"]
        seen = self._seen_requests.get(idempotency_key)
        if seen is not None:
            if seen[0] != request_sha:
                raise IdempotencyConflict("idempotency key has different transition content")
            return dict(seen[1])
        if command["upgrade_id"] != self.upgrade_id:
            raise InvalidReleaseCommand("command upgrade_id does not match controller stream")
        if command["from_state"] != self.state:
            raise IllegalTransition("command from_state does not match durable current state")
        if command["to_state"] not in _ALLOWED_TRANSITIONS[self.state]:
            raise IllegalTransition(f"transition {self.state}->{command['to_state']} is not allowed")

        evidence_refs = tuple(command["transition_evidence"]["evidence_refs"])
        facts = self._facts_resolver(
            self.upgrade_id, command["from_state"], command["to_state"], evidence_refs
        )
        self._validate_facts(facts, command)
        rollout = self._build_rollout_event(command, facts, request_sha)
        append_command = self._build_append_command(command, rollout)

        try:
            durable = dict(self._durable_append(append_command))
        except Exception as exc:
            raise DurableAppendError("durable upgrade-event append failed; state was not advanced") from exc
        self._validate_ledger_event(durable, self._sequence + 1, self._last_event_sha256)
        if durable["payload"] != rollout:
            raise DurableAppendError("durable ledger acknowledged different rollout content")

        self.state = command["to_state"]
        self._sequence = durable["sequence"]
        self._last_event_sha256 = durable["event_sha256"]
        if self.state == "BOM_SIGNED":
            self._locked_bom_sha256 = facts.observed_bom_sha256
            self._locked_artifact_sha256 = facts.observed_artifact_sha256
            self._candidate_validation_sha256 = rollout["candidate_validation_sha256"]
        self._seen_requests[idempotency_key] = (request_sha, dict(rollout))
        return dict(rollout)

    def _validate_command(self, raw: Mapping[str, Any]) -> dict[str, Any]:
        classification = classify_rollout_command_major(raw)
        if classification == _QUARANTINE_ONLY_V1:
            raise QuarantinedReleaseCommand(
                "rollout.command/v1 is frozen quarantine-only and cannot advance runtime state"
            )
        if classification != _ACTIVE_V2:
            raise InvalidReleaseCommand("rollout command major is unknown or unsupported")
        if not isinstance(raw, Mapping) or set(raw) != _COMMAND_FIELDS:
            raise InvalidReleaseCommand("rollout command has unknown or missing fields")
        command = dict(raw)
        if (
            command.get("schema_version") != "2.0.0"
            or command.get("contract_id") != "rollout.command/v2"
            or command.get("producer_module") != "factory-control-plane-host"
            or command.get("privacy_class") != "internal"
        ):
            raise InvalidReleaseCommand("rollout command contract or producer is invalid")
        if command.get("from_state") not in _ALL_STATES or command.get("to_state") not in _ALL_STATES:
            raise InvalidReleaseCommand("rollout command state is unknown")
        if (
            not isinstance(command.get("upgrade_id"), str)
            or _UPGRADE_ID.fullmatch(command["upgrade_id"]) is None
        ):
            raise InvalidReleaseCommand("rollout command upgrade_id is not canonical")
        if not _opaque_envelope_is_valid(command):
            raise InvalidReleaseCommand("rollout command opaque identifier envelope is invalid")
        if not _is_canonical_utc(command.get("occurred_at")):
            raise InvalidReleaseCommand("occurred_at must be a real canonical UTC second")
        evidence = command.get("transition_evidence")
        if not isinstance(evidence, Mapping) or set(evidence) != _EVIDENCE_REFERENCE_FIELDS:
            raise InvalidReleaseCommand("transition_evidence may contain only evidence_refs")
        references = evidence.get("evidence_refs")
        if (
            not isinstance(references, list)
            or not references
            or len(references) > _MAX_EVIDENCE_REFS
            or any(not _is_valid_receipt_reference(item) for item in references)
            or len(references) != len(set(references))
        ):
            raise InvalidReleaseCommand(
                "evidence_refs must be a bounded non-empty set of canonical receipt references"
            )
        return command

    def _validate_facts(self, facts: Any, command: Mapping[str, Any]) -> None:
        if not isinstance(facts, TrustedTransitionFacts):
            raise UnauthorizedTransition("trusted resolver returned an invalid facts object")
        command_refs = tuple(command["transition_evidence"]["evidence_refs"])
        if facts.upgrade_id != self.upgrade_id:
            raise UnauthorizedTransition("trusted facts resolve another upgrade identity")
        if facts.resolved_evidence_refs != command_refs:
            raise UnauthorizedTransition("trusted receipt references drifted from the rollout command")
        if (
            not isinstance(facts.receipt_set_sha256, str)
            or not _SHA256.fullmatch(facts.receipt_set_sha256)
            or facts.receipt_set_sha256 == _ZERO_HASH
        ):
            raise UnauthorizedTransition("trusted receipt set digest is invalid")
        if facts.risk_tier not in _RISK_TIERS:
            raise UnauthorizedTransition("trusted risk tier is invalid")
        if facts.risk_tier == "R4":
            raise UnauthorizedTransition("R4 releases are always rejected")
        if facts.evidence_kind not in _EVIDENCE_KINDS:
            raise UnauthorizedTransition("trusted evidence kind is invalid")
        if facts.verification_level not in _VERIFICATION_RANK:
            raise UnauthorizedTransition("trusted verification level is invalid")
        if type(facts.simulation_only) is not bool or type(facts.kill_switch_armed) is not bool:
            raise UnauthorizedTransition("trusted boolean facts are invalid")
        if (facts.evidence_kind == "SIMULATION") is not facts.simulation_only:
            raise UnauthorizedTransition("simulation evidence must be explicitly and exclusively labelled SIMULATION")
        if type(facts.side_effect_count) is not int or facts.side_effect_count < 0:
            raise UnauthorizedTransition("side-effect count is invalid")
        for digest in (facts.observed_bom_sha256, facts.observed_artifact_sha256):
            if digest is not None and (not isinstance(digest, str) or not _SHA256.fullmatch(digest)):
                raise UnauthorizedTransition("observed digest is invalid")

        target = command["to_state"]
        candidate_sha = self._validate_candidate_report(facts.candidate_validation, facts.risk_tier, target)
        if target == "BOM_SIGNED":
            report = facts.candidate_validation
            assert report is not None
            if facts.observed_bom_sha256 != report["bom_sha256"]:
                raise UnauthorizedTransition("observed BOM differs from validated signed BOM")
            if facts.observed_artifact_sha256 != report["artifact_set_sha256"]:
                raise UnauthorizedTransition("observed artifact set differs from validated candidate")
        elif self._locked_bom_sha256 is not None:
            if (
                facts.observed_bom_sha256 != self._locked_bom_sha256
                or facts.observed_artifact_sha256 != self._locked_artifact_sha256
                or candidate_sha != self._candidate_validation_sha256
            ):
                raise UnauthorizedTransition("BOM, artifact, or validator digest drifted after signing")

        if target in {"SHADOW", "CANARY", "ROLLING", "SOAKING", "COMPLETED"} and not facts.kill_switch_armed:
            raise UnauthorizedTransition("kill switch must be armed before rollout")
        if target == "SHADOW":
            if facts.side_effect_count != 0:
                raise UnauthorizedTransition("shadow produced a real side effect")
            if _VERIFICATION_RANK[facts.verification_level] < _VERIFICATION_RANK["INTEGRATION_VERIFIED"]:
                raise UnauthorizedTransition("shadow requires integration-grade trusted evidence")
        if target == "CANARY":
            self._require_non_simulated(facts, "DEVICE_VERIFIED", {"DEVICE", "CANARY"})
        if target in {"ROLLING", "SOAKING", "COMPLETED"}:
            self._require_non_simulated(facts, "CANARY_VERIFIED", {"CANARY", "SCALE"})

    def _validate_candidate_report(
        self, report: Mapping[str, Any] | None, risk_tier: str, target: str
    ) -> str | None:
        requires_report = target in {"BOM_SIGNED", "SHADOW", "CANARY", "ROLLING", "SOAKING", "COMPLETED"}
        if not requires_report:
            if report is not None:
                raise UnauthorizedTransition("candidate validation is not accepted before BOM_SIGNED")
            return None
        if not isinstance(report, Mapping) or set(report) != _CANDIDATE_REPORT_FIELDS:
            raise UnauthorizedTransition("BOM_SIGNED requires the exact trusted candidate-validator report")
        if (
            report.get("result") != "PASS"
            or report.get("validation_kind") != "CANDIDATE_BOM_STATIC"
            or report.get("verification_ceiling") != "INTEGRATION_VERIFIED"
            or report.get("simulation_only") is not False
            or report.get("canary_verified") is not False
            or report.get("scale_verified") is not False
        ):
            raise UnauthorizedTransition("candidate validator report is not an eligible PASS")
        for field in ("schema_sha256", "bom_sha256", "artifact_set_sha256"):
            if not isinstance(report.get(field), str) or not _SHA256.fullmatch(report[field]):
                raise UnauthorizedTransition(f"candidate report {field} is invalid")
        for field in ("trust_policy_id", "bom_id", "integration_commit", "bom_signer"):
            if not isinstance(report.get(field), str) or not report[field]:
                raise UnauthorizedTransition(f"candidate report {field} is invalid")
        for field in ("artifact_signers", "evidence_signers"):
            values = report.get(field)
            if not isinstance(values, list) or not values or any(not isinstance(item, str) or not item for item in values):
                raise UnauthorizedTransition(f"candidate report {field} is invalid")
        if self.controller_identity in set(report["evidence_signers"]):
            raise UnauthorizedTransition("release controller cannot issue its own test evidence")
        approver = report.get("release_approver")
        if risk_tier in {"R2", "R3"}:
            if not isinstance(approver, str) or not approver or approver == self.controller_identity:
                raise UnauthorizedTransition("R2/R3 requires a distinct verified human release approver")
        elif approver is not None:
            raise UnauthorizedTransition("R0/R1 candidate report contains an unnecessary approval")
        return sha256(dict(report))

    @staticmethod
    def _require_non_simulated(
        facts: TrustedTransitionFacts, minimum_level: str, allowed_kinds: set[str]
    ) -> None:
        if facts.simulation_only or facts.evidence_kind == "SIMULATION":
            raise UnauthorizedTransition("simulation evidence cannot authorize canary or rolling states")
        if facts.evidence_kind not in allowed_kinds:
            raise UnauthorizedTransition("evidence kind is insufficient for this rollout state")
        if _VERIFICATION_RANK[facts.verification_level] < _VERIFICATION_RANK[minimum_level]:
            raise UnauthorizedTransition("verification level is insufficient for this rollout state")

    def _build_rollout_event(
        self,
        command: Mapping[str, Any],
        facts: TrustedTransitionFacts,
        request_sha: str,
    ) -> dict[str, Any]:
        candidate_sha = sha256(dict(facts.candidate_validation)) if facts.candidate_validation is not None else None
        identity = {"upgrade_id": self.upgrade_id, "idempotency_key": command["idempotency_key"]}
        return {
            "schema_version": "2.0.0",
            "contract_id": "rollout.event/v2",
            "producer_module": "factory-release-controller",
            "soul_id": command["soul_id"],
            "device_binding_id": command["device_binding_id"],
            "platform_account_id": command["platform_account_id"],
            "trace_id": command["trace_id"],
            "idempotency_key": command["idempotency_key"],
            "occurred_at": command["occurred_at"],
            "privacy_class": "internal",
            "rollout_event_id": "rollout-" + sha256(identity)[:32],
            "upgrade_id": self.upgrade_id,
            "previous_state": command["from_state"],
            "current_state": command["to_state"],
            "risk_tier": facts.risk_tier,
            "actor_identity": self.controller_identity,
            "actor_role": "release-controller",
            "bom_sha256": facts.observed_bom_sha256,
            "artifact_sha256": facts.observed_artifact_sha256,
            "evidence_kind": facts.evidence_kind,
            "verification_level": facts.verification_level,
            "simulation_only": facts.simulation_only,
            "side_effect_count": facts.side_effect_count,
            "kill_switch_armed": facts.kill_switch_armed,
            "transition_request_sha256": request_sha,
            "trusted_facts_sha256": sha256(asdict(facts)),
            "candidate_validation_sha256": candidate_sha,
            "receipt_set_sha256": facts.receipt_set_sha256,
            "evidence_refs": list(facts.resolved_evidence_refs),
        }

    def _build_append_command(
        self, command: Mapping[str, Any], rollout: Mapping[str, Any]
    ) -> dict[str, Any]:
        return {
            "schema_version": "1.0.0",
            "contract_id": "upgrade.event.append/v1",
            "producer_module": "factory-release-controller",
            "soul_id": command["soul_id"],
            "device_binding_id": command["device_binding_id"],
            "platform_account_id": command["platform_account_id"],
            "trace_id": command["trace_id"],
            "idempotency_key": command["idempotency_key"],
            "occurred_at": command["occurred_at"],
            "privacy_class": "internal",
            "stream_id": self.upgrade_id,
            "expected_sequence": self._sequence,
            "event_type": "STATE_TRANSITIONED",
            "payload": dict(rollout),
            "payload_sha256": sha256(rollout),
        }

    @staticmethod
    def _validate_trusted_ledger_anchor(
        anchor: Any, upgrade_id: str, event_count: int
    ) -> None:
        if type(anchor) is not _AuthenticatedLedgerHead:
            raise CorruptReleaseStream("trusted ledger anchor has an invalid capability type")
        if (
            anchor.source_module != "factory-evidence-ledger"
            or anchor.upgrade_id != upgrade_id
            or type(anchor.sequence) is not int
            or anchor.sequence < 0
            or anchor.sequence != event_count
            or not isinstance(anchor.event_sha256, str)
            or _SHA256.fullmatch(anchor.event_sha256) is None
            or not isinstance(anchor.anchor_id, str)
            or _ANCHOR_ID.fullmatch(anchor.anchor_id) is None
        ):
            raise CorruptReleaseStream("trusted ledger anchor identity or stream head is invalid")
        if (anchor.sequence == 0) is not (anchor.event_sha256 == _ZERO_HASH):
            raise CorruptReleaseStream("trusted ledger anchor zero-head semantics are invalid")
        expected_anchor_id = "anchor_" + sha256({
            "source_module": anchor.source_module,
            "upgrade_id": anchor.upgrade_id,
            "sequence": anchor.sequence,
            "event_sha256": anchor.event_sha256,
        })[:32]
        if anchor.anchor_id != expected_anchor_id:
            raise CorruptReleaseStream("trusted ledger anchor commitment is invalid")

    @staticmethod
    def _validate_ledger_rollout_binding(
        ledger_event: Mapping[str, Any], rollout: Mapping[str, Any]
    ) -> None:
        envelope_fields = (
            "soul_id", "device_binding_id", "platform_account_id", "trace_id",
            "idempotency_key", "occurred_at", "privacy_class",
        )
        if any(ledger_event[field] != rollout[field] for field in envelope_fields):
            raise CorruptReleaseStream("ledger envelope is not bound to its rollout payload")

    def _validate_ledger_event(
        self, event: Mapping[str, Any], expected_sequence: int, previous_hash: str
    ) -> None:
        if not isinstance(event, Mapping) or set(event) != _LEDGER_EVENT_FIELDS:
            raise DurableAppendError("ledger acknowledgement has unknown or missing fields")
        if not _opaque_envelope_is_valid(event):
            raise DurableAppendError("ledger acknowledgement opaque identifier envelope is invalid")
        if not _is_canonical_utc(event.get("occurred_at")):
            raise DurableAppendError("ledger acknowledgement time is not canonical UTC")
        if type(event.get("sequence")) is not int or event["sequence"] < 1:
            raise DurableAppendError("ledger acknowledgement sequence is invalid")
        if type(event.get("payload")) is not dict:
            raise DurableAppendError("ledger acknowledgement payload is not an object")
        for field in (
            "payload_sha256", "previous_event_sha256", "event_sha256",
        ):
            if not isinstance(event.get(field), str) or _SHA256.fullmatch(event[field]) is None:
                raise DurableAppendError(f"ledger acknowledgement {field} is invalid")
        if (
            not isinstance(event.get("event_id"), str)
            or _LEDGER_EVENT_ID.fullmatch(event["event_id"]) is None
        ):
            raise DurableAppendError("ledger acknowledgement event id shape is invalid")
        if (
            event.get("schema_version") != "1.0.0"
            or event.get("contract_id") != "upgrade.event/v1"
            or event.get("producer_module") != "factory-evidence-ledger"
            or event.get("source_module") != "factory-release-controller"
            or event.get("event_type") != "STATE_TRANSITIONED"
            or event.get("stream_id") != self.upgrade_id
            or event.get("privacy_class") != "internal"
            or event.get("sequence") != expected_sequence
            or event.get("previous_event_sha256") != previous_hash
            or event.get("append_status") not in {"APPENDED", "IDEMPOTENT_REPLAY"}
        ):
            raise DurableAppendError("ledger acknowledgement identity or sequence is invalid")
        try:
            payload_sha256 = sha256(event["payload"])
            event_sha256 = sha256(_event_material(event))
        except (TypeError, ValueError, OverflowError) as exc:
            raise DurableAppendError(
                "ledger acknowledgement contains non-JSON digest material"
            ) from exc
        if event.get("payload_sha256") != payload_sha256:
            raise DurableAppendError("ledger acknowledgement payload digest is invalid")
        if event.get("event_sha256") != event_sha256:
            raise DurableAppendError("ledger acknowledgement event digest is invalid")
        expected_event_id = "event-" + sha256(
            {"stream_id": self.upgrade_id, "idempotency_key": event["idempotency_key"]}
        )[:32]
        if event.get("event_id") != expected_event_id:
            raise DurableAppendError("ledger acknowledgement event id is invalid")

    @staticmethod
    def _validate_rollout_event_shape(event: Mapping[str, Any]) -> None:
        classification = classify_rollout_event_major(event)
        if classification == _QUARANTINE_ONLY_V1:
            raise CorruptReleaseStream(
                "rollout.event/v1 is frozen quarantine-only and cannot be replayed"
            )
        if classification != _ACTIVE_V2:
            raise CorruptReleaseStream("rollout event major is unknown or unsupported")
        if not isinstance(event, Mapping) or set(event) != _ROLLOUT_EVENT_FIELDS:
            raise CorruptReleaseStream("rollout event has unknown or missing fields")
        if not _opaque_envelope_is_valid(event):
            raise CorruptReleaseStream("rollout event opaque identifier envelope is invalid")
        if (
            event.get("schema_version") != "2.0.0"
            or event.get("contract_id") != "rollout.event/v2"
            or event.get("producer_module") != "factory-release-controller"
            or event.get("privacy_class") != "internal"
            or event.get("current_state") not in _ALL_STATES
            or event.get("previous_state") not in _ALL_STATES
            or event.get("risk_tier") not in _RISK_TIERS.difference({"R4"})
        ):
            raise CorruptReleaseStream("rollout event contract or state is invalid")
        if (
            not isinstance(event.get("upgrade_id"), str)
            or _UPGRADE_ID.fullmatch(event["upgrade_id"]) is None
        ):
            raise CorruptReleaseStream("rollout event upgrade identity is invalid")
        if not _is_canonical_utc(event.get("occurred_at")):
            raise CorruptReleaseStream("rollout event time is not canonical UTC")
        if (
            not isinstance(event.get("rollout_event_id"), str)
            or _ROLLOUT_EVENT_ID.fullmatch(event["rollout_event_id"]) is None
        ):
            raise CorruptReleaseStream("rollout event identity shape is invalid")
        expected_rollout_id = "rollout-" + sha256({
            "upgrade_id": event["upgrade_id"],
            "idempotency_key": event["idempotency_key"],
        })[:32]
        if event["rollout_event_id"] != expected_rollout_id:
            raise CorruptReleaseStream("rollout event identity commitment is invalid")
        if (
            not isinstance(event.get("actor_identity"), str)
            or _ACTOR_ID.fullmatch(event["actor_identity"]) is None
            or event.get("actor_role") != "release-controller"
        ):
            raise CorruptReleaseStream("rollout event actor identity or role is invalid")
        if event.get("evidence_kind") not in _EVIDENCE_KINDS:
            raise CorruptReleaseStream("rollout event evidence kind is invalid")
        if event.get("verification_level") not in _VERIFICATION_RANK:
            raise CorruptReleaseStream("rollout event verification level is invalid")
        if (
            type(event.get("simulation_only")) is not bool
            or type(event.get("kill_switch_armed")) is not bool
        ):
            raise CorruptReleaseStream("rollout event boolean facts are invalid")
        if type(event.get("side_effect_count")) is not int or event["side_effect_count"] < 0:
            raise CorruptReleaseStream("rollout event side-effect count is invalid")
        for field in (
            "transition_request_sha256", "trusted_facts_sha256", "receipt_set_sha256",
        ):
            if not isinstance(event.get(field), str) or not _SHA256.fullmatch(event[field]):
                raise CorruptReleaseStream(f"rollout event {field} is invalid")
        if event["receipt_set_sha256"] == _ZERO_HASH:
            raise CorruptReleaseStream("rollout event receipt set digest is zero")
        for field in ("bom_sha256", "artifact_sha256"):
            digest = event.get(field)
            if digest is not None and (
                not isinstance(digest, str) or _SHA256.fullmatch(digest) is None
            ):
                raise CorruptReleaseStream(f"rollout event {field} is invalid")
        references = event.get("evidence_refs")
        if (
            not isinstance(references, list)
            or not references
            or len(references) > _MAX_EVIDENCE_REFS
            or any(not _is_valid_receipt_reference(item) for item in references)
            or len(references) != len(set(references))
        ):
            raise CorruptReleaseStream("rollout event receipt references are invalid")
        candidate_sha = event.get("candidate_validation_sha256")
        if candidate_sha is not None and (not isinstance(candidate_sha, str) or not _SHA256.fullmatch(candidate_sha)):
            raise CorruptReleaseStream("candidate validation digest is invalid")
        is_simulation = event["evidence_kind"] == "SIMULATION"
        if is_simulation is not event["simulation_only"]:
            raise CorruptReleaseStream("rollout event simulation semantics are inconsistent")
        if is_simulation and (
            event["verification_level"] != "INTEGRATION_VERIFIED"
            or event["side_effect_count"] != 0
        ):
            raise CorruptReleaseStream("simulation rollout evidence exceeds its authority ceiling")

        target = event["current_state"]
        if target in {"SHADOW", "CANARY", "ROLLING", "SOAKING", "COMPLETED"} and not event["kill_switch_armed"]:
            raise CorruptReleaseStream("rollout event lacks an armed kill switch")
        if target == "SHADOW" and (
            event["side_effect_count"] != 0
            or _VERIFICATION_RANK[event["verification_level"]]
            < _VERIFICATION_RANK["INTEGRATION_VERIFIED"]
        ):
            raise CorruptReleaseStream("shadow rollout evidence is insufficient or has side effects")
        if target == "CANARY" and (
            event["simulation_only"]
            or event["evidence_kind"] not in {"DEVICE", "CANARY"}
            or _VERIFICATION_RANK[event["verification_level"]]
            < _VERIFICATION_RANK["DEVICE_VERIFIED"]
        ):
            raise CorruptReleaseStream("canary rollout evidence is insufficient")
        if target in {"ROLLING", "SOAKING", "COMPLETED"} and (
            event["simulation_only"]
            or event["evidence_kind"] not in {"CANARY", "SCALE"}
            or _VERIFICATION_RANK[event["verification_level"]]
            < _VERIFICATION_RANK["CANARY_VERIFIED"]
        ):
            raise CorruptReleaseStream("rolling rollout evidence is insufficient")

    def _enforce_replayed_rollout_invariants(self, rollout: Mapping[str, Any]) -> None:
        current = rollout["current_state"]
        if current == "BOM_SIGNED":
            if (
                rollout["bom_sha256"] is None
                or rollout["artifact_sha256"] is None
                or rollout["candidate_validation_sha256"] is None
            ):
                raise CorruptReleaseStream("BOM_SIGNED event lacks signed candidate binding")
            self._locked_bom_sha256 = rollout["bom_sha256"]
            self._locked_artifact_sha256 = rollout["artifact_sha256"]
            self._candidate_validation_sha256 = rollout["candidate_validation_sha256"]
        elif self._locked_bom_sha256 is not None:
            if (
                rollout["bom_sha256"] != self._locked_bom_sha256
                or rollout["artifact_sha256"] != self._locked_artifact_sha256
                or rollout["candidate_validation_sha256"] != self._candidate_validation_sha256
            ):
                raise CorruptReleaseStream("replayed rollout digest continuity failed")
        if current in {"SHADOW", "CANARY", "ROLLING", "SOAKING", "COMPLETED"} and not rollout["kill_switch_armed"]:
            raise CorruptReleaseStream("replayed rollout has an unarmed kill switch")
        if current == "SHADOW" and rollout["side_effect_count"] != 0:
            raise CorruptReleaseStream("replayed shadow event contains a side effect")
        if current in {"CANARY", "ROLLING", "SOAKING", "COMPLETED"} and (
            rollout["simulation_only"] or rollout["evidence_kind"] == "SIMULATION"
        ):
            raise CorruptReleaseStream("simulation was replayed as a real rollout state")


__all__ = [
    "CorruptReleaseStream", "DurableAppendError", "IdempotencyConflict",
    "IllegalTransition", "InvalidReleaseCommand", "ReleaseController",
    "QuarantinedReleaseCommand", "ReleaseError", "TrustedTransitionFacts",
    "UnauthorizedTransition", "canonical_bytes",
    "classify_rollout_command_major", "classify_rollout_event_major",
    "parse_rollout_command_json", "sha256",
]
