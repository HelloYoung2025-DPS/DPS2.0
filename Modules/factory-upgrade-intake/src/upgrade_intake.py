"""Deterministic, side-effect-free validation for Factory upgrade intents."""

from __future__ import annotations

import copy
import datetime as dt
import fnmatch
import hashlib
import json
import re
import weakref
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import Any, Dict, Iterable, Mapping, Sequence, Tuple


class IntentValidationError(ValueError):
    """Raised when an upgrade intent fails closed."""


_MODULE_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*\Z")
_GIT_COMMIT = re.compile(r"^[0-9a-f]{40}\Z")
_CONTRACT_ID = re.compile(r"^[a-z][a-z0-9]*(?:\.[a-z0-9]+)+\Z")
_SOUL_ID = re.compile(r"^soul_[a-f0-9]{64}\Z")
_DEVICE_BINDING_ID = re.compile(r"^db_[a-f0-9]{32}\Z")
_PLATFORM_ACCOUNT_ID = re.compile(r"^pa_[a-f0-9]{32}\Z")
_TRACE_ID = re.compile(r"^trace_[a-f0-9]{32}\Z")
_IDEMPOTENCY_KEY = re.compile(r"^idem_[a-f0-9]{64}\Z")
_SHA256 = re.compile(r"^[a-f0-9]{64}\Z")
_OPAQUE_REQUEST_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{7,127}\Z")
_ACTOR_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{0,127}\Z")
_RECEIPT_ID = re.compile(r"^[a-z][a-z0-9-]*:[a-z0-9][a-z0-9._:-]{7,127}\Z")
_NONCE = re.compile(r"^nonce_[a-f0-9]{32}\Z")
_UTC_TIMESTAMP = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z\Z")
_RISK_TIERS = {"R0", "R1", "R2", "R3", "R4"}
_STAGES = {"development", "shadow", "canary", "rolling", "soaking"}
_PRODUCTION_STAGES = {"canary", "rolling", "soaking"}
_REQUESTER_ROLES = {
    "human-requester", "impact-planner", "contract-architect", "module-implementer"
}
_PROVIDED_CONTRACT_MODES = {"active", "quarantine-only", "retired"}
_CONTRACT_STATUSES = {"proposed", "active", "deprecated", "retired"}
_CONTRACT_CHANGE_KINDS = {
    "add-major", "additive-schema", "mode-transition",
    "introduce-quarantined-major",
}
_MAX_WIRE_BYTES = 256 * 1024
_MAX_JSON_DEPTH = 64
_INTAKE_AUDIENCE = "dps.factory-upgrade-intake"
_QUARANTINE_IMPORT_REASON = "historical-wire-import-no-baseline-major"

_TOP_LEVEL_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
    "occurred_at", "privacy_class", "intent_id", "auth_context_id",
    "requester_auth_context_sha256", "requester_auth_receipt_id",
    "requester_auth_nonce", "baseline_commit", "target_modules", "requested_paths",
    "manifest_ownership_sha256", "manifest_ownership_receipt_id",
    "public_contract_changes", "public_contract_changes_sha256",
    "contract_change_claims_status", "baseline_verification_required",
    "approval_subject_sha256", "upgrade_intent_sha256", "requested_risk_tier",
    "requested_stage", "requester", "authorization",
}
_PUBLIC_CONTRACT_CHANGE_FIELDS = {
    "contract_id", "major", "baseline_commit", "expected_mode", "expected_status",
    "expected_baseline_state", "change_kind", "expected_owner_module",
    "expected_source", "expected_source_sha256", "expected_previous_mode",
    "expected_previous_source_sha256", "quarantine_reason",
    "quarantine_evidence_sha256",
}
_REQUESTER_FIELDS = {"identity", "role"}
_AUTH_FIELDS = {
    "status", "approved_by", "approver_role", "approval_scope",
    "approval_receipt_id", "approval_nonce", "approved_at", "approval_expires_at",
}
_AUTH_RECORD_FIELDS = {
    "context_id", "subject", "role", "audience", "issued_at", "expires_at", "nonce",
    "receipt_id", "approvals", "verification_material",
}
_APPROVAL_RECORD_FIELDS = {
    "approver_subject", "approver_role", "scopes", "approval_subject_sha256",
    "intent_id", "baseline_commit", "requested_risk_tier", "requested_stage",
    "audience", "issued_at", "expires_at", "nonce", "receipt_id",
}


@dataclass(frozen=True)
class QuarantinedUpgradeIntent:
    """Fixed routing metadata only; it is never a processable domain DTO."""

    schema_version: str
    contract_id: str
    producer_module: str
    contract_mode: str
    payload_sha256: str
    byte_length: int
    disposition: str = "QUARANTINED"
    reason: str = "upgrade.intent/v1 routing identity is quarantine-only"


@dataclass(frozen=True, init=False)
class VerifiedApproval:
    approver_subject: str
    approver_role: str
    scopes: Tuple[str, ...]
    approval_subject_sha256: str
    intent_id: str
    baseline_commit: str
    requested_risk_tier: str
    requested_stage: str
    audience: str
    issued_at: str
    expires_at: str
    nonce: str
    receipt_id: str
    _issuer: Any
    _issuer_token: object

    def __init__(self, *, _issuer: Any, _issuer_token: object, **values: Any) -> None:
        if _issuer is None or _issuer_token is None:
            raise IntentValidationError("VerifiedApproval must be issued by the auth authority")
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])


@dataclass(frozen=True, init=False)
class VerifiedAuthContext:
    context_id: str
    requester_context_sha256: str
    subject: str
    role: str
    audience: str
    issued_at: str
    expires_at: str
    nonce: str
    receipt_id: str
    approvals: Tuple[VerifiedApproval, ...]
    _clock: Any
    _issuer: Any
    _issuer_token: object

    def __init__(self, *, _issuer: Any, _issuer_token: object, **values: Any) -> None:
        if _issuer is None or _issuer_token is None:
            raise IntentValidationError(
                "VerifiedAuthContext must be issued by a process-bound auth authority"
            )
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])


@dataclass(frozen=True, init=False)
class VerifiedModuleOwnership:
    baseline_commit: str
    snapshot_sha256: str
    receipt_id: str
    ownership: Tuple[Tuple[str, Tuple[str, ...]], ...]
    _issuer: Any
    _issuer_token: object

    def __init__(self, *, _issuer: Any, _issuer_token: object, **values: Any) -> None:
        if _issuer is None or _issuer_token is None:
            raise IntentValidationError(
                "VerifiedModuleOwnership must be issued by the Manifest authority"
            )
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])

    def as_mapping(self) -> Dict[str, Tuple[str, ...]]:
        return dict(self.ownership)


class AuthVerificationPort:
    """Process-composition port; request callers cannot supply a verifier callback."""

    def verify(self, record: Mapping[str, Any]) -> bool:
        raise NotImplementedError


class ManifestOwnershipVerificationPort:
    """Verifies one exact baseline ownership snapshot against an external trust root."""

    def verify(
        self, baseline_commit: str, snapshot_sha256: str, receipt_id: str
    ) -> bool:
        raise NotImplementedError


def _require_exact_fields(
    value: Mapping[str, Any], expected: Iterable[str], label: str
) -> None:
    expected_set = set(expected)
    actual = set(value)
    if any(not isinstance(key, str) for key in actual):
        raise IntentValidationError("%s field names must be strings" % label)
    missing = sorted(expected_set - actual)
    unknown = sorted(actual - expected_set)
    if missing or unknown:
        raise IntentValidationError(
            "%s fields invalid; missing=%s unknown=%s" % (label, missing, unknown)
        )


def _require_string(
    value: Any, label: str, pattern: re.Pattern[str] | None = None
) -> str:
    if not isinstance(value, str) or not value:
        raise IntentValidationError("%s must be a non-empty string" % label)
    if pattern is not None and pattern.fullmatch(value) is None:
        raise IntentValidationError("%s is not canonical" % label)
    return value


def _parse_datetime(value: Any, label: str) -> dt.datetime:
    text = _require_string(value, label)
    if _UTC_TIMESTAMP.fullmatch(text) is None:
        raise IntentValidationError("%s must use canonical UTC second precision" % label)
    try:
        parsed = dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError as exc:
        raise IntentValidationError("%s must be ISO-8601" % label) from exc
    if parsed.tzinfo is None:
        raise IntentValidationError("%s must include a timezone" % label)
    return parsed


def _canonical_json_bytes(value: Any) -> bytes:
    try:
        return json.dumps(
            value, ensure_ascii=False, allow_nan=False,
            separators=(",", ":"), sort_keys=True,
        ).encode("utf-8")
    except (TypeError, ValueError, UnicodeError, RecursionError) as exc:
        raise IntentValidationError("value is not canonical JSON") from exc


def _domain_sha256(domain: str, value: Any) -> str:
    framed = b"DPS\x00" + domain.encode("ascii") + b"\x00" + _canonical_json_bytes(value)
    return hashlib.sha256(framed).hexdigest()


def _validate_json_depth(text: str) -> None:
    depth = 0
    in_string = False
    escaped = False
    for char in text:
        if in_string:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == '"':
                in_string = False
            continue
        if char == '"':
            in_string = True
        elif char in "[{":
            depth += 1
            if depth > _MAX_JSON_DEPTH:
                raise IntentValidationError("upgrade intent JSON nesting exceeds the bound")
        elif char in "]}":
            depth -= 1


def _reject_duplicate_object_pairs(pairs: Sequence[Tuple[str, Any]]) -> Dict[str, Any]:
    result: Dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise IntentValidationError("duplicate JSON object key is forbidden")
        result[key] = value
    return result


def _bounded_json_int(text: str) -> int:
    if len(text.lstrip("-")) > 10:
        raise IntentValidationError("JSON integer exceeds the bound")
    return int(text)


def _reject_json_float(_text: str) -> float:
    raise IntentValidationError("JSON floating-point numbers are forbidden")


def _strict_json_object(payload: bytes | bytearray | str) -> Tuple[Dict[str, Any], bytes]:
    if isinstance(payload, str):
        try:
            raw = payload.encode("utf-8", errors="strict")
        except UnicodeError as exc:
            raise IntentValidationError("upgrade intent must be valid UTF-8") from exc
    elif isinstance(payload, (bytes, bytearray)):
        raw = bytes(payload)
    else:
        raise IntentValidationError("upgrade intent wire payload must be bytes or text")
    if not raw or len(raw) > _MAX_WIRE_BYTES:
        raise IntentValidationError("upgrade intent wire payload exceeds the bounded size")
    if raw.startswith(b"\xef\xbb\xbf"):
        raise IntentValidationError("upgrade intent wire payload must not contain a UTF-8 BOM")
    try:
        text = raw.decode("utf-8", errors="strict")
        _validate_json_depth(text)
        value = json.loads(
            text,
            object_pairs_hook=_reject_duplicate_object_pairs,
            parse_int=_bounded_json_int,
            parse_float=_reject_json_float,
            parse_constant=lambda _value: (_ for _ in ()).throw(
                IntentValidationError("non-finite JSON number is forbidden")
            ),
        )
    except IntentValidationError:
        raise
    except (UnicodeError, json.JSONDecodeError, ValueError, RecursionError) as exc:
        raise IntentValidationError("upgrade intent wire payload is not strict JSON") from exc
    if not isinstance(value, dict):
        raise IntentValidationError("upgrade intent wire payload must be an object")
    return value, raw


def _validate_repo_path(path: Any) -> str:
    text = _require_string(path, "repository path")
    if (
        len(text) > 512 or text[-1].isspace() or text.endswith("/")
        or text.startswith("/") or "\\" in text or "//" in text
        or any(char in text for char in "*?[]")
        or any(ord(char) < 32 or ord(char) == 127 for char in text)
    ):
        raise IntentValidationError("repository path must be canonical relative POSIX form")
    pure = PurePosixPath(text)
    if (
        not pure.parts
        or any(part in {"", ".", ".."} or part.startswith(".") for part in pure.parts)
        or pure.as_posix() != text
    ):
        raise IntentValidationError("repository path contains an alias or hidden segment")
    return text


def _normalize_ownership(
    ownership: Mapping[str, Sequence[str]], targets: set[str]
) -> Dict[str, Tuple[str, ...]]:
    if not isinstance(ownership, Mapping):
        raise IntentValidationError("module ownership snapshot must be an object")
    if not targets.issubset(ownership):
        raise IntentValidationError("target module is absent from the ownership snapshot")
    normalized: Dict[str, Tuple[str, ...]] = {}
    for module_id, patterns in ownership.items():
        _require_string(module_id, "ownership module id", _MODULE_ID)
        if not isinstance(patterns, Sequence) or isinstance(patterns, (str, bytes)):
            raise IntentValidationError("module ownership patterns are invalid")
        clean = []
        for pattern in patterns:
            if not isinstance(pattern, str):
                raise IntentValidationError("module ownership pattern must be a string")
            wildcard_count = sum(pattern.count(char) for char in "*?[")
            if pattern.endswith("/**") and wildcard_count == 2:
                _validate_repo_path(pattern[:-3])
            elif wildcard_count == 0:
                _validate_repo_path(pattern)
            else:
                raise IntentValidationError(
                    "module ownership pattern must be exact or a canonical /** suffix"
                )
            clean.append(pattern)
        if not clean or len(set(clean)) != len(clean):
            raise IntentValidationError("module ownership patterns are empty or duplicate")
        normalized[module_id] = tuple(sorted(clean))
    return normalized


def _owner_for_path(path: str, ownership: Mapping[str, Tuple[str, ...]]) -> str:
    owners = [
        module_id for module_id, patterns in ownership.items()
        if any(fnmatch.fnmatchcase(path, pattern) for pattern in patterns)
    ]
    if len(owners) != 1:
        raise IntentValidationError("repository path must have exactly one module owner")
    return owners[0]


class ProcessBoundAuthAuthority:
    def __init__(
        self, port: AuthVerificationPort, *, audience: str = _INTAKE_AUDIENCE,
        clock: Any = None,
    ) -> None:
        if not isinstance(port, AuthVerificationPort):
            raise IntentValidationError("auth verifier must be a process-bound port")
        self._port = port
        self._audience = audience
        self._clock = clock or (lambda: dt.datetime.now(dt.timezone.utc))
        self.__issuer_token = object()
        self.__issued_contexts: weakref.WeakValueDictionary[int, VerifiedAuthContext] = (
            weakref.WeakValueDictionary()
        )

    def assert_issued(self, context: VerifiedAuthContext) -> None:
        if (
            not isinstance(context, VerifiedAuthContext)
            or context._issuer is not self
            or context._issuer_token is not self.__issuer_token
            or self.__issued_contexts.get(id(context)) is not context
        ):
            raise IntentValidationError(
                "authentication context was not issued by the supplied authority"
            )

    def verify(self, record: Mapping[str, Any]) -> VerifiedAuthContext:
        if not isinstance(record, Mapping):
            raise IntentValidationError("authentication record must be an object")
        _require_exact_fields(record, _AUTH_RECORD_FIELDS, "authentication record")
        if self._port.verify(record) is not True:
            raise IntentValidationError("authentication record was not externally verified")
        context_id = _require_string(record["context_id"], "auth context id", _OPAQUE_REQUEST_ID)
        subject = _require_string(record["subject"], "auth subject", _ACTOR_ID)
        role = _require_string(record["role"], "auth role")
        if role not in _REQUESTER_ROLES:
            raise IntentValidationError("auth role is not allowed")
        if record["audience"] != self._audience:
            raise IntentValidationError("auth audience mismatch")
        issued = _parse_datetime(record["issued_at"], "auth issued_at")
        expires = _parse_datetime(record["expires_at"], "auth expires_at")
        now = self._clock()
        if (
            not isinstance(now, dt.datetime) or now.tzinfo is None
            or issued > now or expires <= now or expires <= issued
        ):
            raise IntentValidationError("authentication record is not currently valid")
        nonce = _require_string(record["nonce"], "auth nonce", _NONCE)
        receipt_id = _require_string(record["receipt_id"], "auth receipt_id", _RECEIPT_ID)
        context_payload = {
            "context_id": context_id,
            "subject": subject,
            "role": role,
            "audience": self._audience,
            "issued_at": record["issued_at"],
            "expires_at": record["expires_at"],
            "nonce": nonce,
            "receipt_id": receipt_id,
        }
        raw_approvals = record["approvals"]
        if not isinstance(raw_approvals, list) or len(raw_approvals) > 16:
            raise IntentValidationError("verified approvals must be a bounded list")
        approvals = []
        approval_receipts = set()
        approval_nonces = set()
        for index, item in enumerate(raw_approvals):
            if not isinstance(item, Mapping):
                raise IntentValidationError("verified approval must be an object")
            _require_exact_fields(item, _APPROVAL_RECORD_FIELDS, "approval[%d]" % index)
            scopes = item["scopes"]
            if (
                not isinstance(scopes, list) or not scopes
                or any(not isinstance(value, str) or value not in _STAGES for value in scopes)
                or len(set(scopes)) != len(scopes)
            ):
                raise IntentValidationError("verified approval scopes are invalid")
            approval_issued = _parse_datetime(item["issued_at"], "approval issued_at")
            approval_expires = _parse_datetime(item["expires_at"], "approval expires_at")
            if (
                approval_issued > now or approval_expires <= now
                or approval_expires <= approval_issued
                or approval_expires - approval_issued > dt.timedelta(minutes=15)
            ):
                raise IntentValidationError("verified approval is not currently valid")
            values = {
                "approver_subject": _require_string(
                    item["approver_subject"], "approver subject", _ACTOR_ID
                ),
                "approver_role": item["approver_role"],
                "scopes": tuple(sorted(scopes)),
                "approval_subject_sha256": _require_string(
                    item["approval_subject_sha256"], "approval subject digest", _SHA256
                ),
                "intent_id": _require_string(item["intent_id"], "approval intent_id", _OPAQUE_REQUEST_ID),
                "baseline_commit": _require_string(item["baseline_commit"], "approval baseline", _GIT_COMMIT),
                "requested_risk_tier": item["requested_risk_tier"],
                "requested_stage": item["requested_stage"],
                "audience": item["audience"],
                "issued_at": item["issued_at"],
                "expires_at": item["expires_at"],
                "nonce": _require_string(item["nonce"], "approval nonce", _NONCE),
                "receipt_id": _require_string(item["receipt_id"], "approval receipt_id", _RECEIPT_ID),
            }
            if (
                not isinstance(values["approver_role"], str)
                or values["approver_role"] != "human-release-approver"
                or values["audience"] != self._audience
                or not isinstance(values["requested_risk_tier"], str)
                or values["requested_risk_tier"] not in _RISK_TIERS - {"R4"}
                or not isinstance(values["requested_stage"], str)
                or values["requested_stage"] not in _STAGES
                or values["receipt_id"] in approval_receipts
                or values["nonce"] in approval_nonces
            ):
                raise IntentValidationError("verified approval binding is invalid")
            approval_receipts.add(values["receipt_id"])
            approval_nonces.add(values["nonce"])
            approvals.append(VerifiedApproval(
                _issuer=self, _issuer_token=self.__issuer_token, **values
            ))
        context = VerifiedAuthContext(
            _issuer=self,
            _issuer_token=self.__issuer_token,
            context_id=context_id,
            requester_context_sha256=_domain_sha256(
                "dps.requester-auth-context/v1", context_payload
            ),
            subject=subject,
            role=role,
            audience=self._audience,
            issued_at=record["issued_at"],
            expires_at=record["expires_at"],
        nonce=nonce,
        receipt_id=receipt_id,
            approvals=tuple(approvals),
            _clock=self._clock,
        )
        self.__issued_contexts[id(context)] = context
        return context


class ProcessBoundManifestAuthority:
    def __init__(self, port: ManifestOwnershipVerificationPort) -> None:
        if not isinstance(port, ManifestOwnershipVerificationPort):
            raise IntentValidationError("Manifest verifier must be a process-bound port")
        self._port = port
        self.__issuer_token = object()
        self.__issued_snapshots: weakref.WeakValueDictionary[int, VerifiedModuleOwnership] = (
            weakref.WeakValueDictionary()
        )

    def assert_issued(self, snapshot: VerifiedModuleOwnership) -> None:
        if (
            not isinstance(snapshot, VerifiedModuleOwnership)
            or snapshot._issuer is not self
            or snapshot._issuer_token is not self.__issuer_token
            or self.__issued_snapshots.get(id(snapshot)) is not snapshot
        ):
            raise IntentValidationError(
                "Manifest ownership snapshot was not issued by the supplied authority"
            )

    def verify(
        self, baseline_commit: str, ownership: Mapping[str, Sequence[str]], receipt_id: str
    ) -> VerifiedModuleOwnership:
        _require_string(baseline_commit, "ownership baseline_commit", _GIT_COMMIT)
        _require_string(receipt_id, "ownership receipt_id", _RECEIPT_ID)
        normalized = _normalize_ownership(ownership, set())
        snapshot_sha256 = _domain_sha256(
            "dps.manifest-ownership/v1",
            {"baseline_commit": baseline_commit, "ownership": normalized}
        )
        if self._port.verify(baseline_commit, snapshot_sha256, receipt_id) is not True:
            raise IntentValidationError("Manifest ownership snapshot was not externally verified")
        snapshot = VerifiedModuleOwnership(
            _issuer=self,
            _issuer_token=self.__issuer_token,
            baseline_commit=baseline_commit,
            snapshot_sha256=snapshot_sha256,
            receipt_id=receipt_id,
            ownership=tuple(sorted(normalized.items())),
        )
        self.__issued_snapshots[id(snapshot)] = snapshot
        return snapshot


def _contract_change_sort_key(item: Mapping[str, Any]) -> Tuple[Any, ...]:
    return (
        item["contract_id"], item["major"], item["baseline_commit"],
        item["expected_mode"], item["expected_status"],
        item["expected_baseline_state"], item["change_kind"],
        item["expected_owner_module"], item["expected_source"],
        item["expected_source_sha256"], item["expected_previous_mode"] or "",
        item["expected_previous_source_sha256"] or "",
        item["quarantine_reason"] or "", item["quarantine_evidence_sha256"] or "",
    )


def quarantine_import_evidence_sha256(item: Mapping[str, Any]) -> str:
    return _domain_sha256(
        "dps.upgrade-intent/v2/quarantine-import-evidence",
        {
            "baseline_commit": item["baseline_commit"],
            "contract_id": item["contract_id"],
            "major": item["major"],
            "expected_source": item["expected_source"],
            "expected_source_sha256": item["expected_source_sha256"],
            "quarantine_reason": item["quarantine_reason"],
        }
    )


def _normalize_public_contract_changes(
    value: Any,
    targets: set[str],
    ownership: Mapping[str, Tuple[str, ...]],
    baseline_commit: str,
) -> list[Dict[str, Any]]:
    if not isinstance(value, list) or len(value) > 128:
        raise IntentValidationError("public_contract_changes must be a bounded list")
    normalized = []
    identities = set()
    allowed_transitions = {
        ("active", "quarantine-only"), ("active", "retired"),
        ("quarantine-only", "retired"),
    }
    for index, raw in enumerate(value):
        if not isinstance(raw, Mapping):
            raise IntentValidationError("public contract change must be an object")
        _require_exact_fields(raw, _PUBLIC_CONTRACT_CHANGE_FIELDS, "change[%d]" % index)
        contract_id = _require_string(raw["contract_id"], "change contract_id", _CONTRACT_ID)
        major = raw["major"]
        if isinstance(major, bool) or not isinstance(major, int) or not 1 <= major <= 2147483647:
            raise IntentValidationError("change major is missing or unknown")
        if raw["baseline_commit"] != baseline_commit:
            raise IntentValidationError("change baseline_commit must equal the intent baseline")
        mode = raw["expected_mode"]
        status = raw["expected_status"]
        baseline_state = raw["expected_baseline_state"]
        kind = raw["change_kind"]
        if (
            not isinstance(mode, str) or mode not in _PROVIDED_CONTRACT_MODES
            or not isinstance(status, str) or status not in _CONTRACT_STATUSES
        ):
            raise IntentValidationError("change expected mode or status is unknown")
        if (
            not isinstance(baseline_state, str)
            or baseline_state not in {"absent", "present"}
            or not isinstance(kind, str) or kind not in _CONTRACT_CHANGE_KINDS
        ):
            raise IntentValidationError("change baseline state or kind is unknown")
        owner = _require_string(raw["expected_owner_module"], "expected owner", _MODULE_ID)
        if owner not in targets:
            raise IntentValidationError("expected contract owner must be a target module")
        source = _validate_repo_path(raw["expected_source"])
        if _owner_for_path(source, ownership) != owner:
            raise IntentValidationError("expected source is not owned by expected owner")
        source_sha = _require_string(raw["expected_source_sha256"], "expected source digest", _SHA256)
        previous_mode = raw["expected_previous_mode"]
        if (
            previous_mode is not None
            and (not isinstance(previous_mode, str) or previous_mode not in _PROVIDED_CONTRACT_MODES)
        ):
            raise IntentValidationError("expected previous mode is unknown")
        previous_sha = raw["expected_previous_source_sha256"]
        if previous_sha is not None:
            _require_string(previous_sha, "expected previous source digest", _SHA256)
        reason = raw["quarantine_reason"]
        evidence_sha = raw["quarantine_evidence_sha256"]

        if kind == "add-major":
            valid = (
                baseline_state == "absent" and mode == "active" and status == "proposed"
                and previous_mode is None and previous_sha is None
                and reason is None and evidence_sha is None
            )
        elif kind == "additive-schema":
            valid = (
                baseline_state == "present" and mode == "active"
                and status in {"proposed", "active"} and previous_mode == "active"
                and previous_sha is not None and previous_sha != source_sha
                and reason is None and evidence_sha is None
            )
        elif kind == "mode-transition":
            valid = (
                baseline_state == "present" and previous_mode is not None
                and previous_sha == source_sha and (previous_mode, mode) in allowed_transitions
                and status == ("retired" if mode == "retired" else "deprecated")
                and reason is None and evidence_sha is None
            )
        else:
            valid = (
                baseline_state == "absent" and mode == "quarantine-only"
                and status == "deprecated" and previous_mode is None and previous_sha is None
                and reason == _QUARANTINE_IMPORT_REASON
                and isinstance(evidence_sha, str) and _SHA256.fullmatch(evidence_sha) is not None
                and evidence_sha == quarantine_import_evidence_sha256(raw)
            )
        if not valid:
            raise IntentValidationError("public contract change kind invariants failed")
        identity = (contract_id, major)
        if identity in identities:
            raise IntentValidationError("duplicate or conflicting contract major")
        identities.add(identity)
        normalized.append(dict(raw))
    return sorted(normalized, key=_contract_change_sort_key)


def canonical_public_contract_changes(
    value: Any,
    target_modules: Sequence[str],
    ownership: VerifiedModuleOwnership,
    baseline_commit: str,
    manifest_authority: ProcessBoundManifestAuthority,
) -> bytes:
    if type(manifest_authority) is not ProcessBoundManifestAuthority:
        raise IntentValidationError("the process-composed Manifest authority is required")
    manifest_authority.assert_issued(ownership)
    if ownership.baseline_commit != baseline_commit:
        raise IntentValidationError("ownership snapshot baseline mismatch")
    if (
        not isinstance(target_modules, Sequence)
        or isinstance(target_modules, (str, bytes))
        or not target_modules
        or len(target_modules) > 32
        or any(
            not isinstance(item, str) or _MODULE_ID.fullmatch(item) is None
            for item in target_modules
        )
        or len(set(target_modules)) != len(target_modules)
    ):
        raise IntentValidationError("target_modules is invalid")
    targets = set(target_modules)
    normalized_ownership = _normalize_ownership(ownership.as_mapping(), targets)
    changes = _normalize_public_contract_changes(
        value, targets, normalized_ownership, baseline_commit
    )
    return _canonical_json_bytes(
        {
            "baseline_commit": baseline_commit,
            "manifest_ownership_sha256": ownership.snapshot_sha256,
            "public_contract_changes": changes,
        }
    )


def public_contract_changes_sha256(
    value: Any,
    target_modules: Sequence[str],
    ownership: VerifiedModuleOwnership,
    baseline_commit: str,
    manifest_authority: ProcessBoundManifestAuthority,
) -> str:
    framed = (
        b"DPS\x00dps.upgrade-intent/v2/public-contract-changes\x00"
        + canonical_public_contract_changes(
            value, target_modules, ownership, baseline_commit, manifest_authority
        )
    )
    return hashlib.sha256(framed).hexdigest()


def approval_subject_sha256(intent: Mapping[str, Any]) -> str:
    payload = {
        key: value for key, value in intent.items()
        if key not in {"authorization", "approval_subject_sha256", "upgrade_intent_sha256"}
    }
    return _domain_sha256("dps.upgrade-intent/v2/approval-subject", payload)


def upgrade_intent_sha256(intent: Mapping[str, Any]) -> str:
    return _domain_sha256(
        "dps.upgrade-intent/v2/full-intent",
        {key: value for key, value in intent.items() if key != "upgrade_intent_sha256"},
    )


def quarantine_upgrade_intent_v1(
    payload: bytes | bytearray | str,
) -> QuarantinedUpgradeIntent:
    value, raw = _strict_json_object(payload)
    if (
        value.get("schema_version") != "dps.upgrade-intent/v1"
        or value.get("contract_id") != "upgrade.intent/v1"
        or value.get("producer_module") != "factory-upgrade-intake"
    ):
        raise IntentValidationError("payload lacks the legacy v1 routing identity")
    return QuarantinedUpgradeIntent(
        schema_version="dps.upgrade-intent/v1",
        contract_id="upgrade.intent/v1",
        producer_module="factory-upgrade-intake",
        contract_mode="quarantine-only",
        payload_sha256=hashlib.sha256(raw).hexdigest(),
        byte_length=len(raw),
    )


def _requires_human_approval(requested_risk_tier: str, stage: str) -> bool:
    return (
        (requested_risk_tier == "R3" and stage != "development")
        or (requested_risk_tier == "R2" and stage in _PRODUCTION_STAGES)
    )


def validate_upgrade_intent(
    intent: Mapping[str, Any],
    auth_context: VerifiedAuthContext,
    ownership_snapshot: VerifiedModuleOwnership,
    auth_authority: ProcessBoundAuthAuthority,
    manifest_authority: ProcessBoundManifestAuthority,
) -> Dict[str, Any]:
    """Validate a REQUESTED optimistic claim; this never verifies candidate truth."""

    if not isinstance(intent, Mapping):
        raise IntentValidationError("intent must be an object")
    if type(auth_authority) is not ProcessBoundAuthAuthority:
        raise IntentValidationError("the process-composed auth authority is required")
    if type(manifest_authority) is not ProcessBoundManifestAuthority:
        raise IntentValidationError("the process-composed Manifest authority is required")
    auth_authority.assert_issued(auth_context)
    manifest_authority.assert_issued(ownership_snapshot)
    _require_exact_fields(intent, _TOP_LEVEL_FIELDS, "upgrade intent")
    if intent["schema_version"] != "dps.upgrade-intent/v2" or intent["contract_id"] != "upgrade.intent/v2":
        raise IntentValidationError("unknown upgrade intent major")
    if intent["producer_module"] != "factory-upgrade-intake":
        raise IntentValidationError("unexpected upgrade intent producer")
    if (
        intent["contract_change_claims_status"] != "UNVERIFIED_EXPECTATIONS"
        or intent["baseline_verification_required"] is not True
    ):
        raise IntentValidationError("contract changes must remain unverified expectations")
    for field, pattern in {
        "soul_id": _SOUL_ID,
        "device_binding_id": _DEVICE_BINDING_ID,
        "platform_account_id": _PLATFORM_ACCOUNT_ID,
    }.items():
        value = intent[field]
        if value is not None and (not isinstance(value, str) or pattern.fullmatch(value) is None):
            raise IntentValidationError("%s is not a canonical opaque ID" % field)
    _require_string(intent["trace_id"], "trace_id", _TRACE_ID)
    _require_string(intent["idempotency_key"], "idempotency_key", _IDEMPOTENCY_KEY)
    if intent["privacy_class"] != "internal":
        raise IntentValidationError("Factory intake privacy_class must be internal")
    _parse_datetime(intent["occurred_at"], "occurred_at")
    intent_id = _require_string(intent["intent_id"], "intent_id", _OPAQUE_REQUEST_ID)
    if intent["auth_context_id"] != auth_context.context_id:
        raise IntentValidationError("auth context ID mismatch")
    if intent["requester_auth_context_sha256"] != auth_context.requester_context_sha256:
        raise IntentValidationError("requester auth context digest mismatch")
    if intent["requester_auth_receipt_id"] != auth_context.receipt_id:
        raise IntentValidationError("requester auth receipt mismatch")
    if intent["requester_auth_nonce"] != auth_context.nonce:
        raise IntentValidationError("requester auth nonce mismatch")
    now = auth_context._clock()
    auth_issued = _parse_datetime(auth_context.issued_at, "verified auth issued_at")
    auth_expires = _parse_datetime(auth_context.expires_at, "verified auth expires_at")
    if (
        not isinstance(now, dt.datetime) or now.tzinfo is None
        or auth_issued > now or auth_expires <= now
    ):
        raise IntentValidationError("verified authentication context has expired")
    baseline = _require_string(intent["baseline_commit"], "baseline_commit", _GIT_COMMIT)
    if ownership_snapshot.baseline_commit != baseline:
        raise IntentValidationError("Manifest ownership snapshot baseline mismatch")
    if intent["manifest_ownership_sha256"] != ownership_snapshot.snapshot_sha256:
        raise IntentValidationError("Manifest ownership snapshot digest mismatch")
    if intent["manifest_ownership_receipt_id"] != ownership_snapshot.receipt_id:
        raise IntentValidationError("Manifest ownership receipt mismatch")

    target_modules = intent["target_modules"]
    if (
        not isinstance(target_modules, list) or not target_modules
        or len(target_modules) > 32
        or any(not isinstance(item, str) or _MODULE_ID.fullmatch(item) is None for item in target_modules)
        or len(set(target_modules)) != len(target_modules)
    ):
        raise IntentValidationError("target_modules is invalid")
    targets = set(target_modules)
    ownership = _normalize_ownership(ownership_snapshot.as_mapping(), targets)
    requested_paths = intent["requested_paths"]
    if not isinstance(requested_paths, list) or not requested_paths or len(requested_paths) > 512:
        raise IntentValidationError("requested_paths is invalid")
    paths = [_validate_repo_path(path) for path in requested_paths]
    if len(set(paths)) != len(paths):
        raise IntentValidationError("requested_paths must be unique")
    if {_owner_for_path(path, ownership) for path in paths} != targets:
        raise IntentValidationError("every target module must own a requested path")

    changes = _normalize_public_contract_changes(
        intent["public_contract_changes"], targets, ownership, baseline
    )
    if any(change["expected_source"] not in paths for change in changes):
        raise IntentValidationError(
            "every expected contract source must be in requested_paths"
        )
    changes_digest = public_contract_changes_sha256(
        changes, target_modules, ownership_snapshot, baseline, manifest_authority
    )
    if intent["public_contract_changes_sha256"] != changes_digest:
        raise IntentValidationError("public contract changes digest mismatch")
    requested_risk = intent["requested_risk_tier"]
    if (
        not isinstance(requested_risk, str)
        or requested_risk not in _RISK_TIERS or requested_risk == "R4"
    ):
        raise IntentValidationError("requested risk tier is unknown or forbidden")
    stage = intent["requested_stage"]
    if not isinstance(stage, str) or stage not in _STAGES:
        raise IntentValidationError("requested stage is unknown")
    requester = intent["requester"]
    if not isinstance(requester, Mapping):
        raise IntentValidationError("requester must be an object")
    _require_exact_fields(requester, _REQUESTER_FIELDS, "requester")
    requester_identity = _require_string(requester["identity"], "requester identity", _ACTOR_ID)
    if (
        not isinstance(requester["role"], str)
        or requester["role"] not in _REQUESTER_ROLES
        or requester_identity != auth_context.subject
        or requester["role"] != auth_context.role
        or auth_context.audience != _INTAKE_AUDIENCE
    ):
        raise IntentValidationError("requester is not bound to verified authentication")

    normalized = copy.deepcopy(dict(intent))
    normalized["target_modules"] = sorted(target_modules)
    normalized["requested_paths"] = sorted(paths)
    normalized["public_contract_changes"] = changes
    normalized["public_contract_changes_sha256"] = changes_digest
    subject_digest = approval_subject_sha256(normalized)
    if intent["approval_subject_sha256"] != subject_digest:
        raise IntentValidationError("approval subject digest mismatch")

    authorization = intent["authorization"]
    if not isinstance(authorization, Mapping):
        raise IntentValidationError("authorization must be an object")
    _require_exact_fields(authorization, _AUTH_FIELDS, "authorization")
    status = authorization["status"]
    scope = authorization["approval_scope"]
    approval_required = _requires_human_approval(requested_risk, stage)
    if status == "not-required":
        if approval_required or authorization != {
            "status": "not-required", "approved_by": None,
            "approver_role": "not-applicable", "approval_scope": [],
            "approval_receipt_id": None, "approval_nonce": None,
            "approved_at": None, "approval_expires_at": None,
        }:
            raise IntentValidationError("not-required authorization fields are inconsistent")
    elif status == "approved":
        if (
            not isinstance(scope, list) or not scope
            or any(not isinstance(value, str) or value not in _STAGES for value in scope)
            or len(set(scope)) != len(scope) or stage not in scope
        ):
            raise IntentValidationError("approval scope is invalid")
        approved_by = _require_string(
            authorization["approved_by"], "approved_by", _ACTOR_ID
        )
        if approved_by == requester_identity or authorization["approver_role"] != "human-release-approver":
            raise IntentValidationError("approval must come from a distinct human approver")
        receipt_id = _require_string(
            authorization["approval_receipt_id"], "approval receipt_id", _RECEIPT_ID
        )
        nonce = _require_string(authorization["approval_nonce"], "approval nonce", _NONCE)
        _parse_datetime(authorization["approved_at"], "approved_at")
        _parse_datetime(authorization["approval_expires_at"], "approval_expires_at")
        matching = [
            approval for approval in auth_context.approvals
            if approval.approver_subject == approved_by
            and approval.receipt_id == receipt_id
            and approval.nonce == nonce
            and approval.approval_subject_sha256 == subject_digest
            and approval.intent_id == intent_id
            and approval.baseline_commit == baseline
            and approval.requested_risk_tier == requested_risk
            and approval.requested_stage == stage
            and approval.scopes == tuple(sorted(scope))
            and approval.issued_at == authorization["approved_at"]
            and approval.expires_at == authorization["approval_expires_at"]
            and _parse_datetime(approval.issued_at, "verified approval issued_at") <= now
            and _parse_datetime(approval.expires_at, "verified approval expires_at") > now
        ]
        if len(matching) != 1:
            raise IntentValidationError("approval is conflicting or unbound")
        normalized["authorization"]["approval_scope"] = sorted(scope)
    else:
        raise IntentValidationError("pending or rejected authorization is not routable")

    full_digest = upgrade_intent_sha256(normalized)
    if intent["upgrade_intent_sha256"] != full_digest:
        raise IntentValidationError("full upgrade intent digest mismatch")
    normalized["approval_subject_sha256"] = subject_digest
    normalized["upgrade_intent_sha256"] = full_digest
    return normalized


def decode_upgrade_intent_v2(
    payload: bytes | bytearray | str,
    auth_context: VerifiedAuthContext,
    ownership_snapshot: VerifiedModuleOwnership,
    auth_authority: ProcessBoundAuthAuthority,
    manifest_authority: ProcessBoundManifestAuthority,
) -> Dict[str, Any]:
    value, _raw = _strict_json_object(payload)
    return validate_upgrade_intent(
        value, auth_context, ownership_snapshot, auth_authority, manifest_authority
    )


def encode_upgrade_intent_v2(
    intent: Mapping[str, Any],
    auth_context: VerifiedAuthContext,
    ownership_snapshot: VerifiedModuleOwnership,
    auth_authority: ProcessBoundAuthAuthority,
    manifest_authority: ProcessBoundManifestAuthority,
) -> bytes:
    normalized = validate_upgrade_intent(
        intent, auth_context, ownership_snapshot, auth_authority, manifest_authority
    )
    encoded = _canonical_json_bytes(normalized)
    if len(encoded) > _MAX_WIRE_BYTES:
        raise IntentValidationError("upgrade intent wire payload exceeds the bounded size")
    return encoded
