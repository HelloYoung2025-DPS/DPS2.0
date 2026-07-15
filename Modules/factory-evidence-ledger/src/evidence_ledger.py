"""Fail-closed authenticated append and replay for DPS Factory evidence.

The public JSON wire contracts remain ``upgrade.event.append/v1`` and
``upgrade.event/v1``.  Authorization is deliberately out-of-band: callers
must present an opaque, process-bound capability obtained by authenticating
the exact canonical command bytes.  A Mapping, callback, or deserialized copy
is never an append credential.
"""

from __future__ import annotations

import base64
import fcntl
import hashlib
import hmac
import json
import math
import os
import re
import secrets
import stat
import threading
import time
import weakref
from abc import ABC, abstractmethod
from collections.abc import Callable, Mapping
from contextlib import contextmanager
from copy import deepcopy
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Any, Final, Iterator


_ZERO_HASH: Final = "0" * 64
_SOURCE_MODULE = re.compile(r"^factory-[a-z0-9]+(?:-[a-z0-9]+)*\Z")
_ALLOWED_SOURCE_MODULES: Final = frozenset(
    {"factory-release-controller", "factory-rollback-controller"}
)
_EVENT_TYPE = re.compile(r"^[A-Z][A-Z0-9_]{1,63}\Z")
_STREAM_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{7,127}\Z")
_EVENT_ID = re.compile(r"^event-[0-9a-f]{32}\Z")
_SHA256 = re.compile(r"^[0-9a-f]{64}\Z")
_CANONICAL_UTC = re.compile(
    r"^[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
    r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,6})?Z\Z"
)
_OPAQUE_IDS = {
    "soul_id": re.compile(r"^soul_[a-f0-9]{64}\Z"),
    "device_binding_id": re.compile(r"^db_[a-f0-9]{32}\Z"),
    "platform_account_id": re.compile(r"^pa_[a-f0-9]{32}\Z"),
    "trace_id": re.compile(r"^trace_[a-f0-9]{32}\Z"),
    "idempotency_key": re.compile(r"^idem_[a-f0-9]{64}\Z"),
}
_COMMAND_FIELDS: Final = frozenset(
    {
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
        "stream_id",
        "expected_sequence",
        "event_type",
        "payload",
        "payload_sha256",
    }
)
_EVENT_FIELDS: Final = frozenset(
    {
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
)

_MAX_COMMAND_BYTES: Final = 64 * 1024
_MAX_PAYLOAD_BYTES: Final = 32 * 1024
_MAX_JSON_DEPTH: Final = 16
_MAX_JSON_NODES: Final = 2048
_MAX_CONTAINER_ITEMS: Final = 256
_MAX_STRING_CHARS: Final = 8192
_MAX_FILE_BYTES: Final = 64 * 1024 * 1024

_AUTH_SCHEMA: Final = "dps.factory-evidence-append-auth/v1"
_AUTH_ISSUER: Final = "dps-factory-auth-service"
_DEV_AUTH_ISSUER: Final = "dps-local-development-authority"
_AUTH_AUDIENCE: Final = "factory-evidence-ledger"
_AUTH_SCOPE: Final = "factory:evidence:append"
_AUTH_KEY_ID: Final = "factory-evidence-append-v1"
_AUTH_MAX_TTL_SECONDS: Final = 300
_AUTH_CLOCK_SKEW_SECONDS: Final = 5
_MAX_ACTIVE_CAPABILITIES: Final = 4096
_AUTH_FIELDS: Final = frozenset(
    {
        "schema_version",
        "issuer",
        "audience",
        "scope",
        "producer_module",
        "command_sha256",
        "issued_at",
        "expires_at",
        "revocation_epoch",
        "nonce",
        "key_id",
        "signature",
    }
)
_AUTH_SIGNED_FIELD_ORDER: Final = (
    "schema_version",
    "issuer",
    "audience",
    "scope",
    "producer_module",
    "command_sha256",
    "issued_at",
    "expires_at",
    "revocation_epoch",
    "nonce",
    "key_id",
)
_AUTH_NONCE = re.compile(r"^auth_[0-9a-f]{32}\Z")
_CAPABILITY_GUARD = object()
_REPOSITORY_GUARD = object()
_PG_RUNTIME_ROLE: Final = "dps_factory_evidence_runtime"


class LedgerError(RuntimeError):
    """Base class for fail-closed ledger errors."""


class InvalidEvent(LedgerError):
    pass


class SequenceConflict(LedgerError):
    pass


class IdempotencyConflict(LedgerError):
    pass


class CorruptEventStream(LedgerError):
    pass


class AppendAuthorizationError(LedgerError):
    pass


class ExternalAuthorizationRequired(AppendAuthorizationError):
    pass


class UnsafeFileFixture(CorruptEventStream):
    pass


class _DuplicateJsonMember(ValueError):
    pass


def _reject_duplicate_members(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise _DuplicateJsonMember("duplicate JSON member: " + key)
        value[key] = item
    return value


def _reject_nonfinite(token: str) -> None:
    raise ValueError("non-finite JSON number: " + token)


def _validate_json_tree(value: Any, *, context: str) -> None:
    nodes = 0
    stack: list[tuple[Any, int]] = [(value, 1)]
    while stack:
        item, depth = stack.pop()
        nodes += 1
        if nodes > _MAX_JSON_NODES:
            raise ValueError(f"{context} exceeds JSON node limit")
        if depth > _MAX_JSON_DEPTH:
            raise ValueError(f"{context} exceeds JSON depth limit")
        if item is None or isinstance(item, (str, bool, int, float)):
            if isinstance(item, str) and len(item) > _MAX_STRING_CHARS:
                raise ValueError(f"{context} contains an oversized string")
            if isinstance(item, int) and not isinstance(item, bool) and abs(item) > 2**63 - 1:
                raise ValueError(f"{context} integer is outside signed 64-bit range")
            if isinstance(item, float) and (not math.isfinite(item) or item == 0.0 and math.copysign(1.0, item) < 0):
                raise ValueError(f"{context} contains non-canonical float")
            continue
        if isinstance(item, list):
            if len(item) > _MAX_CONTAINER_ITEMS:
                raise ValueError(f"{context} array exceeds item limit")
            stack.extend((child, depth + 1) for child in reversed(item))
            continue
        if type(item) is dict:
            if len(item) > _MAX_CONTAINER_ITEMS:
                raise ValueError(f"{context} object exceeds member limit")
            for key, child in item.items():
                if not isinstance(key, str) or len(key) > _MAX_STRING_CHARS:
                    raise ValueError(f"{context} has an invalid object key")
                stack.append((child, depth + 1))
            continue
        raise ValueError(f"{context} contains a non-JSON value")


def canonical_bytes(value: Any) -> bytes:
    _validate_json_tree(value, context="canonical JSON")
    return json.dumps(
        value,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        allow_nan=False,
    ).encode("utf-8")


def sha256(value: Any) -> str:
    data = value if type(value) is bytes else canonical_bytes(value)
    return hashlib.sha256(data).hexdigest()


def _strict_decode(raw: bytes, *, context: str, max_bytes: int) -> dict[str, Any]:
    if type(raw) is not bytes or not raw or len(raw) > max_bytes or raw.startswith(b"\xef\xbb\xbf"):
        raise ValueError(f"{context} has invalid byte length or encoding marker")
    try:
        text = raw.decode("utf-8", errors="strict")
        value = json.loads(
            text,
            object_pairs_hook=_reject_duplicate_members,
            parse_constant=_reject_nonfinite,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, _DuplicateJsonMember, ValueError) as exc:
        raise ValueError(f"{context} is not strict JSON") from exc
    if type(value) is not dict:
        raise ValueError(f"{context} must be a JSON object")
    _validate_json_tree(value, context=context)
    if canonical_bytes(value) != raw:
        raise ValueError(f"{context} bytes are not canonical")
    return value


def _validate_timestamp(value: Any, *, error: type[LedgerError], field: str) -> None:
    if not isinstance(value, str) or _CANONICAL_UTC.fullmatch(value) is None:
        raise error(f"{field} must be canonical UTC")
    try:
        datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise error(f"{field} is not a real calendar instant") from exc


def _opaque_envelope_is_valid(value: Mapping[str, Any]) -> bool:
    for field, pattern in _OPAQUE_IDS.items():
        item = value.get(field)
        if field in {"soul_id", "device_binding_id", "platform_account_id"} and item is None:
            continue
        if not isinstance(item, str) or pattern.fullmatch(item) is None:
            return False
    return True


def _validate_command(command: Mapping[str, Any]) -> dict[str, Any]:
    if type(command) is not dict or set(command) != _COMMAND_FIELDS:
        raise InvalidEvent("append command has unknown or missing fields")
    if command.get("schema_version") != "1.0.0" or command.get("contract_id") != "upgrade.event.append/v1":
        raise InvalidEvent("unknown append command version")
    source = command.get("producer_module")
    if not isinstance(source, str) or _SOURCE_MODULE.fullmatch(source) is None or source not in _ALLOWED_SOURCE_MODULES:
        raise InvalidEvent("invalid source module")
    event_type = command.get("event_type")
    if not isinstance(event_type, str) or _EVENT_TYPE.fullmatch(event_type) is None:
        raise InvalidEvent("invalid event type")
    if not _opaque_envelope_is_valid(command):
        raise InvalidEvent("invalid opaque identifier envelope")
    if command.get("privacy_class") != "internal":
        raise InvalidEvent("privacy_class must be internal")
    _validate_timestamp(command.get("occurred_at"), error=InvalidEvent, field="occurred_at")
    stream_id = command.get("stream_id")
    if not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None:
        raise InvalidEvent("invalid stream_id")
    expected = command.get("expected_sequence")
    if isinstance(expected, bool) or not isinstance(expected, int) or expected < 0:
        raise InvalidEvent("expected_sequence must be a nonnegative integer, never boolean")
    payload = command.get("payload")
    if type(payload) is not dict:
        raise InvalidEvent("payload must be an object")
    try:
        payload_bytes = canonical_bytes(payload)
    except (TypeError, ValueError) as exc:
        raise InvalidEvent("payload is not bounded canonical JSON") from exc
    if len(payload_bytes) > _MAX_PAYLOAD_BYTES:
        raise InvalidEvent("payload exceeds byte limit")
    payload_digest = hashlib.sha256(payload_bytes).hexdigest()
    if command.get("payload_sha256") != payload_digest:
        raise InvalidEvent("payload_sha256 mismatch")
    return dict(command)


def _event_material(event: Mapping[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in event.items() if key not in {"event_sha256", "append_status"}}


def _derived_event_id(stream_id: str, idempotency_key: str) -> str:
    return "event-" + sha256({"stream_id": stream_id, "idempotency_key": idempotency_key})[:32]


def _validate_event(
    event: Mapping[str, Any],
    *,
    expected_sequence: int,
    expected_previous: str,
    command: Mapping[str, Any] | None,
    persisted: bool = True,
) -> dict[str, Any]:
    if type(event) is not dict or set(event) != _EVENT_FIELDS:
        raise CorruptEventStream("event has unknown or missing fields")
    if (
        event.get("schema_version") != "1.0.0"
        or event.get("contract_id") != "upgrade.event/v1"
        or event.get("producer_module") != "factory-evidence-ledger"
    ):
        raise CorruptEventStream("event contract or producer is invalid")
    source = event.get("source_module")
    if not isinstance(source, str) or source not in _ALLOWED_SOURCE_MODULES or _SOURCE_MODULE.fullmatch(source) is None:
        raise CorruptEventStream("event source module is invalid")
    if not _opaque_envelope_is_valid(event):
        raise CorruptEventStream("event opaque identifier envelope is invalid")
    if event.get("privacy_class") != "internal":
        raise CorruptEventStream("event privacy class is invalid")
    _validate_timestamp(event.get("occurred_at"), error=CorruptEventStream, field="event occurred_at")
    stream_id = event.get("stream_id")
    if not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None:
        raise CorruptEventStream("event stream_id is invalid")
    event_type = event.get("event_type")
    if not isinstance(event_type, str) or _EVENT_TYPE.fullmatch(event_type) is None:
        raise CorruptEventStream("event_type is invalid")
    sequence = event.get("sequence")
    if isinstance(sequence, bool) or not isinstance(sequence, int) or sequence != expected_sequence:
        raise CorruptEventStream("non-contiguous or non-integer event sequence")
    event_id = event.get("event_id")
    if not isinstance(event_id, str) or _EVENT_ID.fullmatch(event_id) is None or event_id != _derived_event_id(stream_id, event["idempotency_key"]):
        raise CorruptEventStream("event_id is not the deterministic identifier")
    payload = event.get("payload")
    if type(payload) is not dict:
        raise CorruptEventStream("event payload is not an object")
    try:
        payload_digest = sha256(payload)
    except (TypeError, ValueError) as exc:
        raise CorruptEventStream("event payload is not bounded JSON") from exc
    if event.get("payload_sha256") != payload_digest:
        raise CorruptEventStream("event payload digest mismatch")
    previous = event.get("previous_event_sha256")
    if not isinstance(previous, str) or _SHA256.fullmatch(previous) is None or previous != expected_previous:
        raise CorruptEventStream("previous-event hash mismatch")
    status = event.get("append_status")
    allowed_status = {"APPENDED"} if persisted else {"APPENDED", "IDEMPOTENT_REPLAY"}
    if status not in allowed_status:
        raise CorruptEventStream("event append_status is invalid for its storage context")
    calculated = sha256(_event_material(event))
    if event.get("event_sha256") != calculated:
        raise CorruptEventStream("event hash mismatch")
    if command is not None:
        exact_pairs = {
            "soul_id": "soul_id",
            "device_binding_id": "device_binding_id",
            "platform_account_id": "platform_account_id",
            "trace_id": "trace_id",
            "idempotency_key": "idempotency_key",
            "occurred_at": "occurred_at",
            "stream_id": "stream_id",
            "event_type": "event_type",
            "payload": "payload",
            "payload_sha256": "payload_sha256",
        }
        if any(event[event_key] != command[command_key] for event_key, command_key in exact_pairs.items()):
            raise CorruptEventStream("event fields do not match authenticated command")
        if source != command.get("producer_module") or sequence != command.get("expected_sequence") + 1:
            raise CorruptEventStream("event source or sequence does not match authenticated command")
    return dict(event)


def validate_stream(events: list[dict[str, Any]], command_wires: list[bytes] | None = None) -> None:
    if command_wires is not None and len(command_wires) != len(events):
        raise CorruptEventStream("stored command and event counts differ")
    previous = _ZERO_HASH
    for index, event in enumerate(events, start=1):
        command = None
        if command_wires is not None:
            try:
                command = _validate_command(
                    _strict_decode(command_wires[index - 1], context="stored append command", max_bytes=_MAX_COMMAND_BYTES)
                )
            except (ValueError, InvalidEvent) as exc:
                raise CorruptEventStream("stored append command is invalid") from exc
        validated = _validate_event(
            event,
            expected_sequence=index,
            expected_previous=previous,
            command=command,
        )
        previous = validated["event_sha256"]


class VerifiedAppendCapability:
    """Opaque process-bound capability.  It is intentionally not serializable."""

    __slots__ = ("__capability_id", "__weakref__")

    def __init__(self, capability_id: str, guard: object) -> None:
        if guard is not _CAPABILITY_GUARD:
            raise TypeError("append capabilities are issued only by a fixed authority")
        self.__capability_id = capability_id

    @property
    def capability_id(self) -> str:
        return self.__capability_id

    def __copy__(self):
        raise TypeError("append capabilities cannot be copied")

    def __deepcopy__(self, memo):
        raise TypeError("append capabilities cannot be copied")

    def __reduce__(self):
        raise TypeError("append capabilities cannot be serialized")


@dataclass(frozen=True)
class _AuthorizedCommand:
    raw_bytes: bytes
    authorization_bytes: bytes
    claims: dict[str, Any]


@dataclass(frozen=True)
class _CapabilityRecord:
    capability_ref: weakref.ReferenceType[VerifiedAppendCapability]
    authorized: _AuthorizedCommand


class _AppendAuthority(ABC):
    def __init__(self, *, key: bytes, issuer: str, revocation_epoch: int) -> None:
        if type(key) is not bytes or len(key) < 32:
            raise AppendAuthorizationError("authority key material is invalid")
        if issuer not in {_AUTH_ISSUER, _DEV_AUTH_ISSUER}:
            raise AppendAuthorizationError("authority issuer is not fixed")
        if isinstance(revocation_epoch, bool) or not isinstance(revocation_epoch, int) or revocation_epoch < 0:
            raise AppendAuthorizationError("revocation epoch is invalid")
        self.__key = key
        self.__issuer = issuer
        self.__revocation_epoch = revocation_epoch
        self.__process_id = os.getpid()
        self.__lock = threading.RLock()
        self.__records: dict[int, _CapabilityRecord] = {}
        self.__used_nonces: dict[str, int] = {}

    def __copy__(self):
        raise TypeError("append authorities cannot be copied")

    def __deepcopy__(self, memo):
        raise TypeError("append authorities cannot be copied")

    def __reduce__(self):
        raise TypeError("append authorities cannot be serialized")

    def _ensure_process(self) -> None:
        if os.getpid() != self.__process_id:
            raise AppendAuthorizationError("append authority and capabilities cannot cross a process boundary")

    @property
    def issuer(self) -> str:
        return self.__issuer

    @property
    def is_production(self) -> bool:
        return self.__issuer == _AUTH_ISSUER

    def _signature(self, claims_without_signature: Mapping[str, Any]) -> str:
        if set(claims_without_signature) != set(_AUTH_SIGNED_FIELD_ORDER):
            raise AppendAuthorizationError("authorization signing fields are incomplete")
        material = "|".join(str(claims_without_signature[field]) for field in _AUTH_SIGNED_FIELD_ORDER).encode("utf-8")
        return hmac.new(self.__key, material, hashlib.sha256).hexdigest()

    def _validate_authorization(self, command_raw: bytes, authorization_raw: bytes, *, current: bool) -> dict[str, Any]:
        try:
            command = _validate_command(
                _strict_decode(command_raw, context="authorized append command", max_bytes=_MAX_COMMAND_BYTES)
            )
            claims = _strict_decode(authorization_raw, context="append authorization", max_bytes=16 * 1024)
        except (InvalidEvent, ValueError) as exc:
            raise AppendAuthorizationError("authorization does not bind a valid canonical command") from exc
        if set(claims) != _AUTH_FIELDS:
            raise AppendAuthorizationError("authorization has unknown or missing fields")
        if (
            claims.get("schema_version") != _AUTH_SCHEMA
            or claims.get("issuer") != self.__issuer
            or claims.get("audience") != _AUTH_AUDIENCE
            or claims.get("scope") != _AUTH_SCOPE
            or claims.get("key_id") != _AUTH_KEY_ID
            or claims.get("producer_module") != command.get("producer_module")
            or claims.get("command_sha256") != hashlib.sha256(command_raw).hexdigest()
        ):
            raise AppendAuthorizationError("authorization issuer audience scope producer or command binding is invalid")
        issued_at = claims.get("issued_at")
        expires_at = claims.get("expires_at")
        epoch = claims.get("revocation_epoch")
        nonce = claims.get("nonce")
        if (
            isinstance(issued_at, bool)
            or not isinstance(issued_at, int)
            or isinstance(expires_at, bool)
            or not isinstance(expires_at, int)
            or expires_at <= issued_at
            or expires_at - issued_at > _AUTH_MAX_TTL_SECONDS
            or isinstance(epoch, bool)
            or not isinstance(epoch, int)
            or epoch != self.__revocation_epoch
            or not isinstance(nonce, str)
            or _AUTH_NONCE.fullmatch(nonce) is None
        ):
            raise AppendAuthorizationError("authorization currentness or revocation fields are invalid")
        signature = claims.get("signature")
        if not isinstance(signature, str) or _SHA256.fullmatch(signature) is None:
            raise AppendAuthorizationError("authorization signature encoding is invalid")
        unsigned = {key: value for key, value in claims.items() if key != "signature"}
        if not hmac.compare_digest(signature, self._signature(unsigned)):
            raise AppendAuthorizationError("authorization signature is invalid")
        if current:
            now = int(time.time())
            if issued_at > now + _AUTH_CLOCK_SKEW_SECONDS or expires_at < now:
                raise AppendAuthorizationError("authorization is not current")
        return claims

    def _bind_verified(self, command_raw: bytes, authorization_raw: bytes) -> VerifiedAppendCapability:
        with self.__lock:
            self._ensure_process()
            claims = self._validate_authorization(command_raw, authorization_raw, current=True)
            now = int(time.time())
            self.__records = {
                key: record
                for key, record in self.__records.items()
                if record.capability_ref() is not None and record.authorized.claims["expires_at"] >= now
            }
            self.__used_nonces = {
                nonce: expires_at for nonce, expires_at in self.__used_nonces.items() if expires_at >= now
            }
            if claims["nonce"] in self.__used_nonces:
                raise AppendAuthorizationError("authorization nonce replay is forbidden")
            if len(self.__records) >= _MAX_ACTIVE_CAPABILITIES:
                raise AppendAuthorizationError("active append capability quota is exhausted")
            capability_id = "append-capability-" + secrets.token_hex(16)
            capability = VerifiedAppendCapability(capability_id, _CAPABILITY_GUARD)
            record = _CapabilityRecord(
                weakref.ref(capability),
                _AuthorizedCommand(bytes(command_raw), bytes(authorization_raw), deepcopy(claims)),
            )
            self.__records[id(capability)] = record
            self.__used_nonces[claims["nonce"]] = claims["expires_at"]
            return capability

    def open(self, capability: VerifiedAppendCapability) -> _AuthorizedCommand:
        with self.__lock:
            self._ensure_process()
            if type(capability) is not VerifiedAppendCapability:
                raise AppendAuthorizationError("append requires the exact process-bound capability type")
            record = self.__records.get(id(capability))
            if record is None or record.capability_ref() is not capability:
                raise AppendAuthorizationError("capability was not issued by this authority")
            self._validate_authorization(
                record.authorized.raw_bytes,
                record.authorized.authorization_bytes,
                current=True,
            )
            return _AuthorizedCommand(
                bytes(record.authorized.raw_bytes),
                bytes(record.authorized.authorization_bytes),
                deepcopy(record.authorized.claims),
            )


class DevelopmentAppendAuthority(_AppendAuthority):
    """Explicitly non-production issuer for unit and local durable fixtures."""

    def __init__(self, guard: object) -> None:
        if guard is not _CAPABILITY_GUARD:
            raise TypeError("use DevelopmentAppendAuthority.for_local_tests()")
        super().__init__(key=secrets.token_bytes(32), issuer=_DEV_AUTH_ISSUER, revocation_epoch=0)

    @classmethod
    def for_local_tests(cls) -> "DevelopmentAppendAuthority":
        return cls(_CAPABILITY_GUARD)

    def issue(self, command_raw: bytes) -> VerifiedAppendCapability:
        if type(command_raw) is not bytes:
            raise AppendAuthorizationError("development issuer accepts canonical bytes, never Mapping")
        command = _validate_command(
            _strict_decode(command_raw, context="development append command", max_bytes=_MAX_COMMAND_BYTES)
        )
        now = int(time.time())
        unsigned = {
            "schema_version": _AUTH_SCHEMA,
            "issuer": _DEV_AUTH_ISSUER,
            "audience": _AUTH_AUDIENCE,
            "scope": _AUTH_SCOPE,
            "producer_module": command["producer_module"],
            "command_sha256": hashlib.sha256(command_raw).hexdigest(),
            "issued_at": now,
            "expires_at": now + 60,
            "revocation_epoch": 0,
            "nonce": "auth_" + secrets.token_hex(16),
            "key_id": _AUTH_KEY_ID,
        }
        authorization = dict(unsigned)
        authorization["signature"] = self._signature(unsigned)
        return self._bind_verified(command_raw, canonical_bytes(authorization))


class ExternalAppendAuthority(_AppendAuthority):
    """Fixed production HMAC verifier loaded only from the process environment."""

    def __init__(self, key: bytes, epoch: int, guard: object) -> None:
        if guard is not _CAPABILITY_GUARD:
            raise TypeError("use ExternalAppendAuthority.from_environment()")
        super().__init__(key=key, issuer=_AUTH_ISSUER, revocation_epoch=epoch)

    @classmethod
    def from_environment(cls) -> "ExternalAppendAuthority":
        encoded = os.environ.get("DPS_FACTORY_EVIDENCE_APPEND_HMAC_KEY_B64")
        epoch_text = os.environ.get("DPS_FACTORY_EVIDENCE_APPEND_REVOCATION_EPOCH")
        if not encoded or epoch_text is None:
            raise ExternalAuthorizationRequired(
                "WAITING_EXTERNAL: fixed append authentication key and revocation epoch are required"
            )
        try:
            key = base64.b64decode(encoded, validate=True)
            epoch = int(epoch_text, 10)
        except (ValueError, TypeError) as exc:
            raise ExternalAuthorizationRequired("WAITING_EXTERNAL: append authentication configuration is invalid") from exc
        return cls(key, epoch, _CAPABILITY_GUARD)

    def verify_and_bind(self, command_raw: bytes, authorization_raw: bytes) -> VerifiedAppendCapability:
        if type(command_raw) is not bytes or type(authorization_raw) is not bytes:
            raise AppendAuthorizationError("external verifier accepts exact byte strings, never Mapping or callable")
        return self._bind_verified(command_raw, authorization_raw)


@dataclass(frozen=True)
class AppendCandidate:
    command_wire: bytes
    event_wire: bytes
    command_sha256: str
    expected_sequence: int
    capability: VerifiedAppendCapability


def _decode_candidate(
    candidate: AppendCandidate,
    authority: _AppendAuthority,
) -> tuple[dict[str, Any], dict[str, Any], _AuthorizedCommand]:
    if type(candidate) is not AppendCandidate:
        raise AppendAuthorizationError("repository accepts only sealed append candidates")
    authorized = authority.open(candidate.capability)
    if authorized.raw_bytes != candidate.command_wire:
        raise AppendAuthorizationError("candidate swapped the authenticated command bytes")
    command_sha = hashlib.sha256(candidate.command_wire).hexdigest()
    if candidate.command_sha256 != command_sha:
        raise AppendAuthorizationError("candidate command digest changed")
    try:
        command = _validate_command(
            _strict_decode(candidate.command_wire, context="candidate command", max_bytes=_MAX_COMMAND_BYTES)
        )
        event = _strict_decode(candidate.event_wire, context="candidate event", max_bytes=_MAX_COMMAND_BYTES + _MAX_PAYLOAD_BYTES)
    except (InvalidEvent, ValueError) as exc:
        raise AppendAuthorizationError("candidate wire is not the authenticated canonical content") from exc
    if candidate.expected_sequence != command["expected_sequence"]:
        raise AppendAuthorizationError("candidate expected sequence changed")
    # Static candidate validation binds every event field to the command.  The
    # repository then compares previous_event_sha256 to its locked stream head.
    _validate_event(
        event,
        expected_sequence=command["expected_sequence"] + 1,
        expected_previous=event.get("previous_event_sha256"),
        command=command,
    )
    if canonical_bytes(event) != candidate.event_wire:
        raise AppendAuthorizationError("candidate event bytes changed")
    return command, event, authorized


def _quarantine_record(command: Mapping[str, Any], command_sha256: str, existing_sha256: str) -> dict[str, Any]:
    material = {
        "stream_id": command["stream_id"],
        "idempotency_key": command["idempotency_key"],
        "existing_command_sha256": existing_sha256,
        "conflicting_command_sha256": command_sha256,
    }
    return {
        "quarantine_id": "quarantine-" + sha256(material)[:32],
        **material,
        "reason": "IDEMPOTENCY_KEY_CONTENT_CONFLICT",
        "occurred_at": command["occurred_at"],
    }


def _validate_quarantine_record(record: Mapping[str, Any], *, allow_storage: bool) -> dict[str, Any]:
    if type(record) is not dict:
        raise CorruptEventStream("quarantine record is not an object")
    if record.get("stream_id") == "storage-corruption":
        fields = {"quarantine_id", "stream_id", "reason", "file_sha256", "file_name", "occurred_at"}
        if not allow_storage or set(record) != fields:
            raise CorruptEventStream("storage quarantine record is invalid in this repository")
        if (
            not isinstance(record.get("reason"), str)
            or not record["reason"]
            or not isinstance(record.get("file_name"), str)
            or not record["file_name"]
            or not isinstance(record.get("file_sha256"), str)
            or _SHA256.fullmatch(record["file_sha256"]) is None
        ):
            raise CorruptEventStream("storage quarantine fields are invalid")
        material = {
            "reason": record["reason"],
            "file_sha256": record["file_sha256"],
            "file_name": record["file_name"],
        }
        if record.get("quarantine_id") != "storage-quarantine-" + sha256(material)[:32]:
            raise CorruptEventStream("storage quarantine identifier mismatch")
    else:
        fields = {
            "quarantine_id", "stream_id", "idempotency_key", "existing_command_sha256",
            "conflicting_command_sha256", "reason", "occurred_at",
        }
        if set(record) != fields:
            raise CorruptEventStream("idempotency quarantine record has unknown or missing fields")
        if (
            not isinstance(record.get("stream_id"), str)
            or _STREAM_ID.fullmatch(record["stream_id"]) is None
            or not isinstance(record.get("idempotency_key"), str)
            or _OPAQUE_IDS["idempotency_key"].fullmatch(record["idempotency_key"]) is None
            or not isinstance(record.get("existing_command_sha256"), str)
            or _SHA256.fullmatch(record["existing_command_sha256"]) is None
            or not isinstance(record.get("conflicting_command_sha256"), str)
            or _SHA256.fullmatch(record["conflicting_command_sha256"]) is None
            or record.get("reason") != "IDEMPOTENCY_KEY_CONTENT_CONFLICT"
        ):
            raise CorruptEventStream("idempotency quarantine fields are invalid")
        material = {
            "stream_id": record["stream_id"],
            "idempotency_key": record["idempotency_key"],
            "existing_command_sha256": record["existing_command_sha256"],
            "conflicting_command_sha256": record["conflicting_command_sha256"],
        }
        if record.get("quarantine_id") != "quarantine-" + sha256(material)[:32]:
            raise CorruptEventStream("idempotency quarantine identifier mismatch")
    _validate_timestamp(record.get("occurred_at"), error=CorruptEventStream, field="quarantine occurred_at")
    return dict(record)


def _validate_stored_envelopes(envelopes: list[dict[str, Any]]) -> None:
    streams: dict[str, list[tuple[dict[str, Any], bytes]]] = {}
    seen_event_ids: set[str] = set()
    seen_idempotency: set[tuple[str, str]] = set()
    for envelope in envelopes:
        if type(envelope) is not dict or set(envelope) != {"command_sha256", "command_wire", "event"}:
            raise CorruptEventStream("stored envelope has unknown or missing fields")
        command_text = envelope.get("command_wire")
        if not isinstance(command_text, str):
            raise CorruptEventStream("stored command wire is not text")
        command_wire = command_text.encode("utf-8", errors="strict")
        try:
            command = _validate_command(
                _strict_decode(command_wire, context="stored command", max_bytes=_MAX_COMMAND_BYTES)
            )
        except (InvalidEvent, ValueError) as exc:
            raise CorruptEventStream("stored command is invalid") from exc
        if envelope.get("command_sha256") != hashlib.sha256(command_wire).hexdigest():
            raise CorruptEventStream("stored command digest mismatch")
        event = envelope.get("event")
        if type(event) is not dict:
            raise CorruptEventStream("stored event is not an object")
        event_id = event.get("event_id")
        idem_key = (command["stream_id"], command["idempotency_key"])
        if event_id in seen_event_ids or idem_key in seen_idempotency:
            raise CorruptEventStream("duplicate stored event or idempotency identity")
        seen_event_ids.add(event_id)
        seen_idempotency.add(idem_key)
        streams.setdefault(command["stream_id"], []).append((event, command_wire))
    for stream_id, pairs in streams.items():
        events = [pair[0] for pair in pairs]
        wires = [pair[1] for pair in pairs]
        if any(event.get("stream_id") != stream_id for event in events):
            raise CorruptEventStream("stored event and command stream identities differ")
        validate_stream(events, wires)


class EvidenceRepository(ABC):
    def __init__(self, authority: _AppendAuthority) -> None:
        if not isinstance(authority, _AppendAuthority):
            raise AppendAuthorizationError("repository requires a concrete fixed append authority")
        self._authority = authority

    @property
    def authority(self) -> _AppendAuthority:
        return self._authority

    @abstractmethod
    def append(self, candidate: AppendCandidate) -> dict[str, Any]:
        raise NotImplementedError

    @abstractmethod
    def read_stream(self, stream_id: str) -> list[dict[str, Any]]:
        raise NotImplementedError

    @abstractmethod
    def read_quarantine(self, stream_id: str) -> list[dict[str, Any]]:
        raise NotImplementedError


class InMemoryEvidenceRepository(EvidenceRepository):
    """Unit-test repository; never integration or production evidence."""

    def __init__(self, authority: _AppendAuthority) -> None:
        super().__init__(authority)
        self._envelopes: list[dict[str, Any]] = []
        self._quarantine: list[dict[str, Any]] = []

    def append(self, candidate: AppendCandidate) -> dict[str, Any]:
        command, event, _ = _decode_candidate(candidate, self._authority)
        _validate_stored_envelopes(deepcopy(self._envelopes))
        for envelope in self._envelopes:
            stored = envelope["event"]
            if stored["stream_id"] == command["stream_id"] and stored["idempotency_key"] == command["idempotency_key"]:
                if envelope["command_sha256"] != candidate.command_sha256:
                    record = _quarantine_record(command, candidate.command_sha256, envelope["command_sha256"])
                    if record not in self._quarantine:
                        self._quarantine.append(record)
                    raise IdempotencyConflict("same idempotency key has different authenticated command content")
                replay = deepcopy(stored)
                replay["append_status"] = "IDEMPOTENT_REPLAY"
                _validate_event(
                    replay,
                    expected_sequence=stored["sequence"],
                    expected_previous=stored["previous_event_sha256"],
                    command=_validate_command(_strict_decode(envelope["command_wire"].encode("utf-8"), context="replay command", max_bytes=_MAX_COMMAND_BYTES)),
                    persisted=False,
                )
                return replay
        stream = [item["event"] for item in self._envelopes if item["event"]["stream_id"] == command["stream_id"]]
        if candidate.expected_sequence != len(stream):
            raise SequenceConflict(f"expected sequence {candidate.expected_sequence}, actual {len(stream)}")
        expected_previous = stream[-1]["event_sha256"] if stream else _ZERO_HASH
        if event["previous_event_sha256"] != expected_previous:
            raise SequenceConflict("previous event digest changed before append")
        self._envelopes.append(
            {
                "command_sha256": candidate.command_sha256,
                "command_wire": candidate.command_wire.decode("utf-8"),
                "event": deepcopy(event),
            }
        )
        _validate_stored_envelopes(deepcopy(self._envelopes))
        return deepcopy(event)

    def read_stream(self, stream_id: str) -> list[dict[str, Any]]:
        if not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None:
            raise CorruptEventStream("read stream_id is invalid")
        _validate_stored_envelopes(deepcopy(self._envelopes))
        return deepcopy([item["event"] for item in self._envelopes if item["event"]["stream_id"] == stream_id])

    def read_quarantine(self, stream_id: str) -> list[dict[str, Any]]:
        if not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None:
            raise CorruptEventStream("quarantine stream_id is invalid")
        records = [_validate_quarantine_record(item, allow_storage=False) for item in self._quarantine]
        return deepcopy([item for item in records if item["stream_id"] == stream_id])


class FileEvidenceRepository(EvidenceRepository):
    """Development-only durable JSONL fixture with OS locking and no-follow IO."""

    def __init__(self, path: str | os.PathLike[str], authority: DevelopmentAppendAuthority) -> None:
        if type(authority) is not DevelopmentAppendAuthority:
            raise AppendAuthorizationError("file evidence is a development fixture and requires the explicit development authority")
        super().__init__(authority)
        if isinstance(path, Mapping) or callable(path):
            raise UnsafeFileFixture("file fixture path must be a concrete path")
        candidate = Path(path)
        if not candidate.is_absolute():
            candidate = Path.cwd() / candidate
        self._path = candidate.absolute()
        self._quarantine_path = self._path.with_name(self._path.name + ".quarantine.jsonl")
        self._assert_safe_parent()

    def _assert_safe_parent(self) -> None:
        parent = self._path.parent
        if not parent.is_dir() or parent.is_symlink() or parent.resolve(strict=True) != parent:
            raise UnsafeFileFixture("file fixture parent must exist and contain no symbolic-link hop")
        details = parent.stat()
        if details.st_uid != os.getuid() or stat.S_IMODE(details.st_mode) & 0o022:
            raise UnsafeFileFixture("file fixture parent must be owner-controlled and not group/world writable")

    @contextmanager
    def _locked_file(self, path: Path, *, create: bool, exclusive: bool) -> Iterator[int]:
        self._assert_safe_parent()
        existed = False
        before = None
        try:
            before = path.lstat()
            existed = True
            if stat.S_ISLNK(before.st_mode) or not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
                raise UnsafeFileFixture("file fixture rejects symbolic links hard links and non-regular files")
        except FileNotFoundError:
            if not create:
                yield -1
                return
        flags = os.O_CLOEXEC | os.O_NOFOLLOW | (os.O_RDWR if exclusive else os.O_RDONLY)
        if create:
            flags |= os.O_CREAT
        if exclusive:
            flags |= os.O_APPEND
        try:
            fd = os.open(path, flags, 0o600)
        except OSError as exc:
            raise UnsafeFileFixture("secure file fixture open failed") from exc
        try:
            details = os.fstat(fd)
            if (
                not stat.S_ISREG(details.st_mode)
                or details.st_nlink != 1
                or details.st_uid != os.getuid()
                or stat.S_IMODE(details.st_mode) & 0o077
                or before is not None and (details.st_dev != before.st_dev or details.st_ino != before.st_ino)
            ):
                raise UnsafeFileFixture("file fixture identity or ownership changed")
            if details.st_size > _MAX_FILE_BYTES:
                raise UnsafeFileFixture("file fixture exceeds bounded size")
            fcntl.flock(fd, fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH)
            locked_details = os.fstat(fd)
            try:
                named_details = path.lstat()
            except FileNotFoundError as exc:
                raise UnsafeFileFixture("file fixture path disappeared while acquiring its lock") from exc
            if (
                locked_details.st_nlink != 1
                or locked_details.st_ino != details.st_ino
                or locked_details.st_dev != details.st_dev
                or stat.S_ISLNK(named_details.st_mode)
                or named_details.st_ino != locked_details.st_ino
                or named_details.st_dev != locked_details.st_dev
            ):
                raise UnsafeFileFixture("file fixture identity changed while acquiring its lock")
            if not existed and create:
                directory_fd = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
                try:
                    os.fsync(directory_fd)
                finally:
                    os.close(directory_fd)
            yield fd
        finally:
            try:
                if "fd" in locals():
                    locked_details = os.fstat(fd)
                    try:
                        named_details = path.lstat()
                    except FileNotFoundError as exc:
                        raise UnsafeFileFixture("file fixture path disappeared while locked") from exc
                    if (
                        locked_details.st_nlink != 1
                        or stat.S_ISLNK(named_details.st_mode)
                        or named_details.st_ino != locked_details.st_ino
                        or named_details.st_dev != locked_details.st_dev
                    ):
                        raise UnsafeFileFixture("file fixture path identity changed while locked")
                fcntl.flock(fd, fcntl.LOCK_UN)
            finally:
                os.close(fd)

    @staticmethod
    def _append_line(fd: int, line: bytes) -> None:
        details = os.fstat(fd)
        if not line or len(line) > _MAX_FILE_BYTES or details.st_size > _MAX_FILE_BYTES - len(line):
            raise UnsafeFileFixture("file fixture append would exceed its bounded size")
        remaining = memoryview(line)
        while remaining:
            written = os.write(fd, remaining)
            if written <= 0:
                raise UnsafeFileFixture("file fixture append made no progress")
            remaining = remaining[written:]
        os.fsync(fd)
        if os.fstat(fd).st_size > _MAX_FILE_BYTES:
            raise UnsafeFileFixture("file fixture exceeded its bounded size after append")

    @staticmethod
    def _read_fd(fd: int) -> bytes:
        os.lseek(fd, 0, os.SEEK_SET)
        chunks: list[bytes] = []
        total = 0
        while True:
            chunk = os.read(fd, 64 * 1024)
            if not chunk:
                break
            total += len(chunk)
            if total > _MAX_FILE_BYTES:
                raise UnsafeFileFixture("file fixture exceeded bounded size during read")
            chunks.append(chunk)
        return b"".join(chunks)

    def _decode_jsonl(self, data: bytes, *, context: str) -> list[dict[str, Any]]:
        if not data:
            return []
        if not data.endswith(b"\n"):
            raise CorruptEventStream(f"{context} has a partial final line")
        values: list[dict[str, Any]] = []
        for line_number, line in enumerate(data[:-1].split(b"\n"), start=1):
            if not line:
                raise CorruptEventStream(f"{context} has an empty line at {line_number}")
            try:
                values.append(_strict_decode(line, context=f"{context} line {line_number}", max_bytes=_MAX_COMMAND_BYTES + _MAX_PAYLOAD_BYTES))
            except ValueError as exc:
                raise CorruptEventStream(f"{context} has invalid strict JSON at line {line_number}") from exc
        return values

    def _storage_quarantine_record(self, reason: str, data: bytes) -> dict[str, Any]:
        material = {"reason": reason, "file_sha256": hashlib.sha256(data).hexdigest(), "file_name": self._path.name}
        return {
            "quarantine_id": "storage-quarantine-" + sha256(material)[:32],
            "stream_id": "storage-corruption",
            **material,
            "occurred_at": datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
        }

    def _append_quarantine_locked(self, record: Mapping[str, Any]) -> None:
        _validate_quarantine_record(record, allow_storage=True)
        line = canonical_bytes(dict(record)) + b"\n"
        with self._locked_file(self._quarantine_path, create=True, exclusive=True) as fd:
            existing = self._decode_jsonl(self._read_fd(fd), context="quarantine fixture")
            existing = [_validate_quarantine_record(item, allow_storage=True) for item in existing]
            if any(item.get("quarantine_id") == record.get("quarantine_id") for item in existing):
                return
            self._append_line(fd, line)

    def _decode_envelopes_or_quarantine(self, data: bytes) -> list[dict[str, Any]]:
        try:
            envelopes = self._decode_jsonl(data, context="durable event fixture")
            _validate_stored_envelopes(envelopes)
            return envelopes
        except CorruptEventStream as exc:
            self._append_quarantine_locked(self._storage_quarantine_record(str(exc), data))
            raise

    def append(self, candidate: AppendCandidate) -> dict[str, Any]:
        command, event, _ = _decode_candidate(candidate, self._authority)
        with self._locked_file(self._path, create=True, exclusive=True) as fd:
            envelopes = self._decode_envelopes_or_quarantine(self._read_fd(fd))
            for envelope in envelopes:
                stored = envelope["event"]
                if stored["stream_id"] == command["stream_id"] and stored["idempotency_key"] == command["idempotency_key"]:
                    if envelope["command_sha256"] != candidate.command_sha256:
                        self._append_quarantine_locked(
                            _quarantine_record(command, candidate.command_sha256, envelope["command_sha256"])
                        )
                        raise IdempotencyConflict("same idempotency key has different authenticated command content")
                    replay = deepcopy(stored)
                    replay["append_status"] = "IDEMPOTENT_REPLAY"
                    _validate_event(
                        replay,
                        expected_sequence=stored["sequence"],
                        expected_previous=stored["previous_event_sha256"],
                        command=_validate_command(_strict_decode(envelope["command_wire"].encode("utf-8"), context="replay command", max_bytes=_MAX_COMMAND_BYTES)),
                        persisted=False,
                    )
                    return replay
            stream = [item["event"] for item in envelopes if item["event"]["stream_id"] == command["stream_id"]]
            if candidate.expected_sequence != len(stream):
                raise SequenceConflict(f"expected sequence {candidate.expected_sequence}, actual {len(stream)}")
            expected_previous = stream[-1]["event_sha256"] if stream else _ZERO_HASH
            if event["previous_event_sha256"] != expected_previous:
                raise SequenceConflict("previous event digest changed before append")
            envelope = {
                "command_sha256": candidate.command_sha256,
                "command_wire": candidate.command_wire.decode("utf-8"),
                "event": event,
            }
            self._append_line(fd, canonical_bytes(envelope) + b"\n")
            return deepcopy(event)

    def read_stream(self, stream_id: str) -> list[dict[str, Any]]:
        if not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None:
            raise CorruptEventStream("read stream_id is invalid")
        with self._locked_file(self._path, create=False, exclusive=False) as fd:
            if fd < 0:
                return []
            envelopes = self._decode_envelopes_or_quarantine(self._read_fd(fd))
        return [deepcopy(item["event"]) for item in envelopes if item["event"]["stream_id"] == stream_id]

    def read_quarantine(self, stream_id: str) -> list[dict[str, Any]]:
        if stream_id != "storage-corruption" and (not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None):
            raise CorruptEventStream("quarantine stream_id is invalid")
        with self._locked_file(self._quarantine_path, create=False, exclusive=False) as fd:
            if fd < 0:
                return []
            records = self._decode_jsonl(self._read_fd(fd), context="quarantine fixture")
        records = [_validate_quarantine_record(item, allow_storage=True) for item in records]
        return [deepcopy(item) for item in records if item.get("stream_id") == stream_id]


class PostgresEvidenceRepository(EvidenceRepository):
    """PostgreSQL repository that uses only protected functions and a fixed role.

    The public constructor is intentionally disabled.  Production composition
    requires ``production(dsn, ExternalAppendAuthority)``; test-only database
    composition is visibly separate and still requires the exact runtime role.
    Neither path accepts a connection factory, callback, Mapping, or role name.
    """

    def __init__(
        self,
        dsn: str,
        authority: _AppendAuthority,
        *,
        guard: object,
    ) -> None:
        if guard is not _REPOSITORY_GUARD:
            raise TypeError("use PostgresEvidenceRepository.production() or .for_integration_tests()")
        if not isinstance(dsn, str) or not dsn.strip() or callable(dsn) or isinstance(dsn, Mapping):
            raise TypeError("PostgreSQL DSN must be a fixed non-empty string")
        if type(authority) is not ExternalAppendAuthority:
            raise ExternalAuthorizationRequired("WAITING_EXTERNAL: production PostgreSQL requires external append authentication")
        super().__init__(authority)
        self.__dsn = dsn

    @classmethod
    def production(
        cls,
        dsn: str,
        authority: ExternalAppendAuthority | None,
    ) -> "PostgresEvidenceRepository":
        if type(authority) is not ExternalAppendAuthority:
            raise ExternalAuthorizationRequired(
                "WAITING_EXTERNAL: fixed external append authority is absent; zero events were appended"
            )
        return cls(dsn, authority, guard=_REPOSITORY_GUARD)

    @classmethod
    def for_integration_tests(
        cls,
        dsn: str,
        authority: ExternalAppendAuthority,
    ) -> "PostgresEvidenceRepository":
        if type(authority) is not ExternalAppendAuthority:
            raise ExternalAuthorizationRequired(
                "WAITING_EXTERNAL: PostgreSQL integration requires the real external append authority"
            )
        return cls(dsn, authority, guard=_REPOSITORY_GUARD)

    def _connect(self):
        try:
            import psycopg
        except ImportError as exc:  # pragma: no cover - required infrastructure gate
            raise RuntimeError("INFRA_ERROR: locked psycopg driver is unavailable") from exc
        connection = psycopg.connect(self.__dsn)
        cursor = connection.cursor()
        try:
            cursor.execute("SELECT current_user, session_user, current_setting('server_version_num')::integer")
            row = cursor.fetchone()
            if (
                row is None
                or len(row) != 3
                or row[0] != _PG_RUNTIME_ROLE
                or row[1] != _PG_RUNTIME_ROLE
                or isinstance(row[2], bool)
                or not isinstance(row[2], int)
                or row[2] < 180000
            ):
                raise AppendAuthorizationError(
                    "PostgreSQL connection is not the fixed PostgreSQL 18 factory evidence runtime identity"
                )
        except Exception:
            cursor.close()
            connection.close()
            raise
        cursor.close()
        return connection

    @staticmethod
    def _as_object(value: Any, *, context: str) -> dict[str, Any]:
        if type(value) is dict:
            return deepcopy(value)
        if isinstance(value, str):
            try:
                parsed = json.loads(value, object_pairs_hook=_reject_duplicate_members, parse_constant=_reject_nonfinite)
            except Exception as exc:
                raise CorruptEventStream(f"{context} is invalid JSON") from exc
            if type(parsed) is dict:
                return parsed
        raise CorruptEventStream(f"{context} is not an object")

    def _load_stream_cursor(self, cursor: Any, stream_id: str) -> tuple[list[dict[str, Any]], dict[str, str]]:
        cursor.execute(
            "SELECT event_id, stream_id, sequence, idempotency_key, command_sha256, "
            "payload_sha256, previous_event_sha256, event_sha256, event_type, source_module, "
            "privacy_class, occurred_at_text, command_wire, event_json "
            "FROM factory_evidence.read_upgrade_stream(%s)",
            (stream_id,),
        )
        rows = cursor.fetchall()
        cursor.execute(
            "SELECT last_sequence, last_event_sha256, head_event_id "
            "FROM factory_evidence.read_upgrade_stream_head(%s)",
            (stream_id,),
        )
        head = cursor.fetchone()
        events: list[dict[str, Any]] = []
        wires: list[bytes] = []
        command_hashes: dict[str, str] = {}
        for row in rows:
            if len(row) != 14:
                raise CorruptEventStream("protected read returned an unknown row shape")
            event = self._as_object(row[13], context="stored event")
            projected = {
                "event_id": row[0],
                "stream_id": row[1],
                "sequence": row[2],
                "idempotency_key": row[3],
                "payload_sha256": row[5],
                "previous_event_sha256": row[6],
                "event_sha256": row[7],
                "event_type": row[8],
                "source_module": row[9],
                "privacy_class": row[10],
                "occurred_at": row[11],
            }
            if any(event.get(key) != value for key, value in projected.items()):
                raise CorruptEventStream("database projected columns disagree with event JSON")
            wire = bytes(row[12])
            if row[4] != hashlib.sha256(wire).hexdigest():
                raise CorruptEventStream("database command digest disagrees with command bytes")
            events.append(event)
            wires.append(wire)
            command_hashes[event["idempotency_key"]] = row[4]
        validate_stream(events, wires)
        if events:
            expected_head = (len(events), events[-1]["event_sha256"], events[-1]["event_id"])
            if head != expected_head:
                raise CorruptEventStream("database stream head disagrees with ordered events")
        elif head not in {None, (0, _ZERO_HASH, None)}:
            raise CorruptEventStream("database empty-stream head is inconsistent")
        return events, command_hashes

    def _load_quarantine_cursor(self, cursor: Any, stream_id: str) -> list[dict[str, Any]]:
        cursor.execute(
            "SELECT record_json FROM factory_evidence.read_upgrade_event_quarantine(%s)",
            (stream_id,),
        )
        return [
            _validate_quarantine_record(
                self._as_object(row[0], context="quarantine record"),
                allow_storage=False,
            )
            for row in cursor.fetchall()
        ]

    def append(self, candidate: AppendCandidate) -> dict[str, Any]:
        command, event, authorized = _decode_candidate(candidate, self._authority)
        connection = self._connect()
        cursor = connection.cursor()
        outcome: tuple[str, dict[str, Any] | None] | None = None
        try:
            cursor.execute(
                "SELECT append_status, event_json FROM factory_evidence.append_upgrade_event(%s, %s::jsonb, %s::jsonb)",
                (
                    candidate.command_wire,
                    candidate.event_wire.decode("utf-8"),
                    authorized.authorization_bytes.decode("utf-8"),
                ),
            )
            row = cursor.fetchone()
            if row is None or row[0] not in {"APPENDED", "IDEMPOTENT_REPLAY", "IDEMPOTENCY_CONFLICT"}:
                raise CorruptEventStream("protected append returned an unknown outcome")
            returned = None if row[1] is None else self._as_object(row[1], context="protected append event")
            if row[0] == "IDEMPOTENCY_CONFLICT":
                if returned is not None:
                    raise CorruptEventStream("conflict outcome leaked an event")
                events, command_hashes = self._load_stream_cursor(cursor, command["stream_id"])
                existing_hash = command_hashes.get(command["idempotency_key"])
                if existing_hash is None or not any(
                    item["idempotency_key"] == command["idempotency_key"] for item in events
                ):
                    raise CorruptEventStream("conflict outcome has no existing authenticated event")
                expected_quarantine = _quarantine_record(command, candidate.command_sha256, existing_hash)
                quarantine = self._load_quarantine_cursor(cursor, command["stream_id"])
                if quarantine.count(expected_quarantine) != 1:
                    raise CorruptEventStream("conflict quarantine does not match the authenticated command")
                connection.commit()
                outcome = (row[0], None)
            else:
                if returned is None:
                    raise CorruptEventStream("protected append omitted its event")
                _validate_event(
                    returned,
                    expected_sequence=command["expected_sequence"] + 1,
                    expected_previous=returned.get("previous_event_sha256"),
                    command=command,
                    persisted=True,
                )
                events, _ = self._load_stream_cursor(cursor, command["stream_id"])
                if len(events) <= command["expected_sequence"]:
                    raise CorruptEventStream("protected append event is absent from ordered replay")
                stored = events[command["expected_sequence"]]
                if canonical_bytes(stored) != canonical_bytes(returned):
                    raise CorruptEventStream("protected append result differs from ordered replay")
                if row[0] == "APPENDED" and canonical_bytes(returned) != candidate.event_wire:
                    raise CorruptEventStream("database stored bytes differ from the sealed append candidate")
                displayed = deepcopy(returned)
                if row[0] == "IDEMPOTENT_REPLAY":
                    displayed["append_status"] = "IDEMPOTENT_REPLAY"
                    _validate_event(
                        displayed,
                        expected_sequence=command["expected_sequence"] + 1,
                        expected_previous=displayed["previous_event_sha256"],
                        command=command,
                        persisted=False,
                    )
                connection.commit()
                outcome = (row[0], displayed)
        except Exception:
            connection.rollback()
            raise
        finally:
            cursor.close()
            connection.close()
        if outcome[0] == "IDEMPOTENCY_CONFLICT":
            raise IdempotencyConflict("same idempotency key has different authenticated command content")
        if outcome[1] is None:  # pragma: no cover - guarded before commit
            raise CorruptEventStream("protected append omitted its event")
        return outcome[1]

    def read_stream(self, stream_id: str) -> list[dict[str, Any]]:
        if not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None:
            raise CorruptEventStream("read stream_id is invalid")
        connection = self._connect()
        cursor = connection.cursor()
        try:
            events, _ = self._load_stream_cursor(cursor, stream_id)
            return events
        finally:
            cursor.close()
            connection.close()

    def read_quarantine(self, stream_id: str) -> list[dict[str, Any]]:
        if not isinstance(stream_id, str) or _STREAM_ID.fullmatch(stream_id) is None:
            raise CorruptEventStream("quarantine stream_id is invalid")
        connection = self._connect()
        cursor = connection.cursor()
        try:
            return self._load_quarantine_cursor(cursor, stream_id)
        finally:
            cursor.close()
            connection.close()


class EvidenceLedger:
    def __init__(self, repository: EvidenceRepository, authority: _AppendAuthority) -> None:
        if not isinstance(repository, EvidenceRepository) or repository.authority is not authority:
            raise AppendAuthorizationError("ledger and repository must share the exact fixed authority instance")
        self._repository = repository
        self._authority = authority

    def append(self, capability: VerifiedAppendCapability) -> dict[str, Any]:
        if type(capability) is not VerifiedAppendCapability:
            raise AppendAuthorizationError("append accepts only a process-bound capability, never Mapping")
        authorized = self._authority.open(capability)
        try:
            command = _validate_command(
                _strict_decode(authorized.raw_bytes, context="append command", max_bytes=_MAX_COMMAND_BYTES)
            )
        except (ValueError, InvalidEvent) as exc:
            raise AppendAuthorizationError("authenticated append command is invalid") from exc
        stream_id = command["stream_id"]
        existing = self._repository.read_stream(stream_id)
        previous_digest = existing[-1]["event_sha256"] if existing else _ZERO_HASH
        event: dict[str, Any] = {
            "schema_version": "1.0.0",
            "contract_id": "upgrade.event/v1",
            "producer_module": "factory-evidence-ledger",
            "soul_id": command["soul_id"],
            "device_binding_id": command["device_binding_id"],
            "platform_account_id": command["platform_account_id"],
            "trace_id": command["trace_id"],
            "idempotency_key": command["idempotency_key"],
            "occurred_at": command["occurred_at"],
            "privacy_class": "internal",
            "event_id": _derived_event_id(stream_id, command["idempotency_key"]),
            "stream_id": stream_id,
            "sequence": command["expected_sequence"] + 1,
            "event_type": command["event_type"],
            "source_module": command["producer_module"],
            "payload": deepcopy(command["payload"]),
            "payload_sha256": command["payload_sha256"],
            "previous_event_sha256": previous_digest,
            "append_status": "APPENDED",
        }
        event["event_sha256"] = sha256(_event_material(event))
        event_wire = canonical_bytes(event)
        _validate_event(
            event,
            expected_sequence=command["expected_sequence"] + 1,
            expected_previous=previous_digest,
            command=command,
        )
        candidate = AppendCandidate(
            command_wire=authorized.raw_bytes,
            event_wire=event_wire,
            command_sha256=hashlib.sha256(authorized.raw_bytes).hexdigest(),
            expected_sequence=command["expected_sequence"],
            capability=capability,
        )
        return self._repository.append(candidate)

    def read_stream(self, stream_id: str) -> list[dict[str, Any]]:
        return self._repository.read_stream(stream_id)

    def read_quarantine(self, stream_id: str) -> list[dict[str, Any]]:
        return self._repository.read_quarantine(stream_id)

    def rebuild(
        self,
        stream_id: str,
        initial_state: Any,
        reducer: Callable[[Any, Mapping[str, Any]], Any],
    ) -> Any:
        if not callable(reducer):
            raise TypeError("reducer must be callable")
        state = initial_state
        for event in self.read_stream(stream_id):
            state = reducer(state, event)
        return state
