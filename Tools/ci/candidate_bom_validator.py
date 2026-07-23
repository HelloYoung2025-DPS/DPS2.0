"""Fail-closed signed Release BOM validator used by the root release gate.

The module intentionally exposes no command execution surface.  It verifies
fixed JSON shapes, repository Git objects, immutable bundle bytes, RSA-PSS
signatures, independent evidence, risk approval, and the previous stable BOM.

R0-C (RebuildPlan 4.3) migrated this validator here from
Modules/factory-release-controller/src/ -- candidate validation is ordinary
gate code and must survive that module's R0-D deletion.  It validates only:
no signing, no deployment, no runtime state.  This copy is the one
scripts/release.sh invokes and the one the Phase 0 CI-integrity allowlist
pins; its code-bound trust policy lives under governance/policies/.  The
module-side original matches this copy after exactly the three declared
migration edits until R0-D removes it.

The owner provisioned the native-stop-trust signer out-of-repo on
2026-07-21: the deployed trust policy now carries the
native_stop_trust_signer_identities group (owner-native-stop-trust-signer-1)
and the single-purpose native-stop-trust key native-stop-trust-owner-key-1,
and the code-bound digest was re-anchored to the patched policy, so
from_deployed_anchor constructs the release validator against the live
anchor.
"""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import hmac
import json
import math
import os
import re
import shutil
import stat
import subprocess
import sys
from collections.abc import Mapping, Sequence
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any


_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_COMMIT = re.compile(r"^[0-9a-f]{40}$")
_MODULE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
_SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")
_OPAQUE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$")
_LOWER_OPAQUE_ID = re.compile(r"^[a-z0-9][a-z0-9._-]{7,127}$")
_SOUL_ID = re.compile(r"^soul_[a-f0-9]{64}$")
_DEVICE_BINDING_ID = re.compile(r"^db_[a-f0-9]{32}$")
_PLATFORM_ACCOUNT_ID = re.compile(r"^pa_[a-f0-9]{32}$")
_TRACE_ID = re.compile(r"^trace_[a-f0-9]{32}$")
_IDEMPOTENCY_KEY = re.compile(r"^idem_[a-f0-9]{64}$")
_WORKER_INSTANCE_ID = re.compile(r"^wi_[a-f0-9]{32}$")
_SUPERVISOR_INSTANCE_ID = re.compile(r"^si_[a-f0-9]{32}$")
_POLICY_INSTANCE_ID = re.compile(r"^pi_[a-f0-9]{32}$")
_ROUTE_SIGNER_KEY_ID = re.compile(r"^p256_spki_[a-f0-9]{64}$")
_KEY_ID = re.compile(r"^[a-z0-9][a-z0-9._-]{0,127}$")
_WORKER_SEMVER = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
_UTC_TIMESTAMP = re.compile(
    r"^[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
    r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,9})?Z$"
)
_DOTNET_UTC_TIMESTAMP = re.compile(
    r"^[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
    r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]\.[0-9]{7}Z$"
)
_MAX_CONTROL_JSON_BYTES = 4 * 1024 * 1024
_MAX_METADATA_JSON_BYTES = 16 * 1024 * 1024
_MAX_ARTIFACT_BYTES = 512 * 1024 * 1024
_MAX_JSON_DEPTH = 48
_MAX_JSON_NODES = 100_000
_MAX_JSON_COLLECTION_ITEMS = 20_000
_MAX_CANONICAL_NUMBER_DIGITS = 4_300
_MAX_MODULES = 256
_MAX_INSTRUCTION_HASHES = 4_096
_MAX_CONTRACTS = 4_096
_MAX_EVIDENCE = 1_024
_MAX_NATIVE_STOP_AUTHORITIES = 512
_MAX_DEVICE_ROUTE_AUTHORITIES = 512
_MAX_NATIVE_STOP_CHALLENGE_AUTHORITIES = 512
_MAX_RUNTIME_AUTHORITY_VALIDITY_TICKS = 31 * 24 * 60 * 60 * 10_000_000
_MAX_MANIFEST_DEPENDENCIES = 512
_MAX_MANIFEST_CONTRACTS = 2_048
_MAX_SBOM_FILES = 20_000
_MAX_URI_BYTES = 1_024
_SIGNATURE_FIELDS = {"algorithm", "key_id", "value"}
_BOM_FIELDS = {
    "schema_version", "bom_id", "status", "integration_commit", "created_at",
    "release_bom_generation", "activation_token_sha256",
    "modules", "instruction_hashes", "contracts", "database_versions",
    "dependency_dag_sha256", "compatibility_matrix_sha256", "feature_flags",
    "kill_switches", "ai_toolchain", "evidence", "risk", "release_approval",
    "rollout", "rollback", "previous_stable_bom",
    "previous_stable_bom_sha256", "native_stop_authorities",
    "device_route_assignment_authorities", "native_stop_challenge_authorities",
    "signature",
}
_MODULE_FIELDS = {
    "module_id", "version", "artifact_uri", "sha256", "signature",
    "descriptor_uri", "descriptor_sha256", "sbom_uri", "sbom_sha256",
    "provenance_uri", "provenance_sha256", "agents_sha256", "manifest_sha256",
}
_EVIDENCE_FIELDS = {
    "evidence_id", "artifact_uri", "sha256", "result", "required", "kind",
    "tested_commit", "verification_level", "issuer_identity", "signature",
}
_APPROVAL_FIELDS = {
    "required", "receipt_uri", "sha256", "approver_identity",
    "approver_role", "signature",
}
_NATIVE_STOP_AUTHORITY_FIELDS = {
    "authority_id", "producer_module", "worker_module_id", "worker_artifact_id",
    "worker_artifact_sha256", "worker_version", "worker_slot",
    "worker_instance_id", "worker_generation", "key_id", "p256_spki_sha256",
    "signature_algorithm", "signature_format", "auth_scope", "policy_id",
    "native_stop_contract_id",
    "release_bom_generation", "activation_token_sha256", "rotation_epoch",
    "valid_from", "valid_until", "revoked", "worker_authority_sha256",
}
_DEVICE_ROUTE_AUTHORITY_FIELDS = {
    "route_authority_id", "producer_module", "supervisor_module_id",
    "supervisor_artifact_id", "supervisor_artifact_sha256",
    "supervisor_version", "supervisor_instance_id", "supervisor_generation",
    "route_signer_key_id", "route_signer_p256_spki_sha256",
    "signature_algorithm", "signature_format", "auth_scope", "policy_id",
    "release_bom_generation", "activation_token_sha256", "rotation_epoch",
    "valid_from", "valid_until", "revoked", "route_authority_sha256",
}
_NATIVE_STOP_CHALLENGE_AUTHORITY_FIELDS = {
    "authority_id", "producer_module", "policy_module_id", "policy_artifact_id",
    "policy_artifact_sha256", "policy_version", "policy_instance_id",
    "policy_generation", "key_id", "p256_spki_sha256", "signature_algorithm",
    "signature_format", "auth_scope", "native_stop_challenge_contract_id",
    "policy_id", "release_bom_generation", "activation_token_sha256",
    "rotation_epoch", "valid_from", "valid_until", "revoked",
    "challenge_authority_sha256",
}
_NATIVE_STOP_TRUST_RECEIPT_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
    "occurred_at", "privacy_class", "receipt_id", "release_bom_id",
    "release_bom_sha256", "integration_commit", "release_bom_generation",
    "activation_token_sha256", "trust_policy_id",
    "native_stop_authorities_sha256", "device_route_assignment_authorities_sha256",
    "native_stop_challenge_authorities_sha256", "authority_sets_sha256",
    "native_stop_authorities", "device_route_assignment_authorities",
    "native_stop_challenge_authorities", "signature",
}
_NATIVE_STOP_TRUST_RECEIPT_PAYLOAD_FIELDS = (
    _NATIVE_STOP_TRUST_RECEIPT_FIELDS - {"signature"}
)
_NATIVE_STOP_AUTHORITY_HASH_DOMAIN = "dps.native-stop-worker-authority-sha256/v2"
_NATIVE_STOP_AUTHORITIES_HASH_DOMAIN = "dps.native-stop-authorities-sha256/v1"
_DEVICE_ROUTE_AUTHORITY_HASH_DOMAIN = "dps.device-route-assignment-authority-sha256/v1"
_DEVICE_ROUTE_AUTHORITIES_HASH_DOMAIN = "dps.device-route-assignment-authorities-sha256/v1"
_NATIVE_STOP_CHALLENGE_AUTHORITY_HASH_DOMAIN = "dps.native-stop-challenge-authority-sha256/v1"
_NATIVE_STOP_CHALLENGE_AUTHORITIES_HASH_DOMAIN = "dps.native-stop-challenge-authorities-sha256/v1"
_AUTHORITY_SETS_HASH_DOMAIN = "dps.release-bom-authority-sets-sha256/v1"
_NATIVE_STOP_TRUST_SIGNATURE_DOMAIN = b"dps-release-bom-native.stop.authority.trust/v1\n"
_DESCRIPTOR_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class", "artifact_id",
    "build_id", "module_id", "module_version", "integration_commit",
    "artifact_uri", "artifact_file", "artifact_sha256", "size_bytes",
    "merge_decision_id", "trusted_merge_policy_sha256",
    "source_tree_sha256", "agents_sha256", "manifest_sha256", "sbom",
    "provenance", "signature",
}
_DESCRIPTOR_METADATA_FIELDS = {"path", "sha256", "media_type"}
_DESCRIPTOR_SIGNATURE_FIELDS = {"status", "signer_required"}
_SBOM_FIELDS = {
    "spdxVersion", "dataLicense", "SPDXID", "name", "documentNamespace",
    "creationInfo", "packages", "files", "relationships",
}
_PROVENANCE_FIELDS = {"_type", "subject", "predicateType", "predicate"}
_VERIFICATION_RANK = {
    "REPOSITORY_STATIC_VERIFIED": 1,
    "CONTRACT_VERIFIED": 2,
    "INTEGRATION_VERIFIED": 3,
    "WINDOWS_VERIFIED": 4,
    "DEVICE_VERIFIED": 5,
    "CANARY_VERIFIED": 6,
    "SCALE_VERIFIED": 7,
}
_VERIFICATION_BY_RANK = {value: key for key, value in _VERIFICATION_RANK.items()}
_KIND_CEILING = {
    "REPOSITORY": 1,
    "CONTRACT": 2,
    "INTEGRATION": 3,
    "SIMULATION": 3,
    "WINDOWS": 4,
    "DEVICE": 5,
    "CANARY": 6,
    "SCALE": 7,
}
_DEPLOYED_TRUST_POLICY_RELATIVE = (
    "governance/policies/deployed-release-trust-policy.v1.json"
)
_DEPLOYED_TRUST_POLICY_SHA256 = "e30c17a21db42d88861bfb4eeb33372e383067f07f804b7327dfa461b055121b"
_DEPLOYED_TRUST_POLICY_ID = "dps-deployed-release-anchor-v1"
_DEFAULT_MINIMUM_REMAINING_LIFETIME_SECONDS = 24 * 60 * 60


class CandidateBomError(RuntimeError):
    """Candidate material failed a release-blocking verification."""


class _DuplicateJsonKey(ValueError):
    """Strict JSON parsing observed the same object member twice."""


def _strict_json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise _DuplicateJsonKey(f"duplicate JSON member: {key}")
        result[key] = value
    return result


def _reject_json_constant(value: str) -> None:
    raise ValueError(f"non-finite JSON number: {value}")


def _require_canonical_number_digit_budget(value: str) -> None:
    if sum(character.isdigit() for character in value) > _MAX_CANONICAL_NUMBER_DIGITS:
        raise ValueError("JSON number exceeds the canonical digit limit")


def _strict_json_integer(value: str) -> int:
    _require_canonical_number_digit_budget(value)
    return int(value, 10)


def _strict_json_float(value: str) -> float:
    _require_canonical_number_digit_budget(value)
    parsed = float(value)
    if not math.isfinite(parsed):
        raise ValueError("JSON float is not finite")
    return parsed


def _validate_json_budget(value: Any, label: str) -> None:
    stack: list[tuple[Any, int]] = [(value, 1)]
    nodes = 0
    while stack:
        current, depth = stack.pop()
        nodes += 1
        if nodes > _MAX_JSON_NODES:
            raise CandidateBomError(f"{label} exceeds the JSON node limit")
        if depth > _MAX_JSON_DEPTH:
            raise CandidateBomError(f"{label} exceeds the JSON nesting limit")
        if isinstance(current, Mapping):
            if len(current) > _MAX_JSON_COLLECTION_ITEMS:
                raise CandidateBomError(f"{label} contains an oversized object")
            for key, item in current.items():
                try:
                    encoded_key_length = len(key.encode("utf-8"))
                except (AttributeError, UnicodeEncodeError) as exc:
                    raise CandidateBomError(
                        f"{label} contains an invalid Unicode scalar sequence"
                    ) from exc
                if encoded_key_length > _MAX_CONTROL_JSON_BYTES:
                    raise CandidateBomError(
                        f"{label} contains an oversized object member name"
                    )
                stack.append((item, depth + 1))
        elif isinstance(current, Sequence) and not isinstance(current, (str, bytes, bytearray)):
            if len(current) > _MAX_JSON_COLLECTION_ITEMS:
                raise CandidateBomError(f"{label} contains an oversized array")
            stack.extend((item, depth + 1) for item in current)
        elif isinstance(current, str):
            try:
                encoded_length = len(current.encode("utf-8"))
            except UnicodeEncodeError as exc:
                raise CandidateBomError(
                    f"{label} contains an invalid Unicode scalar sequence"
                ) from exc
            if encoded_length > _MAX_CONTROL_JSON_BYTES:
                raise CandidateBomError(f"{label} contains an oversized string")


def _strict_json_loads(raw: bytes, label: str) -> Any:
    try:
        text = raw.decode("utf-8", errors="strict")
        value = json.loads(
            text,
            object_pairs_hook=_strict_json_object,
            parse_constant=_reject_json_constant,
            parse_int=_strict_json_integer,
            parse_float=_strict_json_float,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, _DuplicateJsonKey, ValueError) as exc:
        raise CandidateBomError(f"{label} is not strict JSON") from exc
    _validate_json_budget(value, label)
    return value


def _require_limited_sequence(value: Any, name: str, maximum: int) -> Sequence[Any]:
    sequence = _require_sequence(value, name)
    if len(sequence) > maximum:
        raise CandidateBomError(f"{name} exceeds the item limit")
    return sequence


def _require_utc_timestamp(value: Any, name: str) -> str:
    if not isinstance(value, str) or not _UTC_TIMESTAMP.fullmatch(value):
        raise CandidateBomError(f"{name} must be a canonical UTC timestamp")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise CandidateBomError(f"{name} must be a valid UTC timestamp") from exc
    if parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        raise CandidateBomError(f"{name} must be UTC")
    return value


def _utc_datetime(value: Any, name: str) -> datetime:
    canonical = _require_utc_timestamp(value, name)
    return datetime.fromisoformat(canonical[:-1] + "+00:00")


def _utc_instant_nanoseconds(value: Any, name: str, *, exact_dotnet: bool = False) -> int:
    pattern = _DOTNET_UTC_TIMESTAMP if exact_dotnet else _UTC_TIMESTAMP
    if not isinstance(value, str) or not pattern.fullmatch(value):
        requirement = (
            "exact yyyy-MM-ddTHH:mm:ss.fffffffZ canonical UTC"
            if exact_dotnet
            else "a canonical UTC timestamp"
        )
        raise CandidateBomError(f"{name} must use {requirement}")
    main, fractional = value[:-1].split(".", 1) if "." in value else (value[:-1], "")
    try:
        parsed = datetime.strptime(main, "%Y-%m-%dT%H:%M:%S")
    except ValueError as exc:
        raise CandidateBomError(f"{name} must be a valid canonical UTC timestamp") from exc
    if parsed.year < 2020 and exact_dotnet:
        raise CandidateBomError(f"{name} must be at or after 2020-01-01T00:00:00.0000000Z")
    fraction_ns = int((fractional + "000000000")[:9]) if fractional else 0
    seconds = (
        (parsed.toordinal() - 1) * 86_400
        + parsed.hour * 3_600
        + parsed.minute * 60
        + parsed.second
    )
    return seconds * 1_000_000_000 + fraction_ns


def _dotnet_utc_ticks(value: Any, name: str) -> int:
    if not isinstance(value, str) or not _DOTNET_UTC_TIMESTAMP.fullmatch(value):
        raise CandidateBomError(
            f"{name} must use exact yyyy-MM-ddTHH:mm:ss.fffffffZ canonical UTC"
        )
    nanoseconds = _utc_instant_nanoseconds(value, name, exact_dotnet=True)
    if nanoseconds % 100:
        raise CandidateBomError(f"{name} is not representable as a .NET 100ns UTC instant")
    return nanoseconds // 100


def _system_utc_now_nanoseconds() -> int:
    # Same proleptic UTC nanosecond scale as _utc_instant_nanoseconds.
    now = datetime.now(timezone.utc)
    seconds = (
        (now.toordinal() - 1) * 86_400
        + now.hour * 3_600
        + now.minute * 60
        + now.second
    )
    return seconds * 1_000_000_000 + now.microsecond * 1_000


def _require_bundle_uri(value: Any, name: str) -> str:
    if (
        not isinstance(value, str)
        or not value
        or len(value.encode("utf-8")) > _MAX_URI_BYTES
        or "\\" in value
        or any(ord(character) < 32 or ord(character) == 127 for character in value)
    ):
        raise CandidateBomError(f"{name} URI must be a bounded canonical relative path")
    relative = PurePosixPath(value)
    if (
        relative.is_absolute()
        or "." in relative.parts
        or ".." in relative.parts
        or relative.as_posix() != value
    ):
        raise CandidateBomError(f"{name} URI must be a bounded canonical relative path")
    return value


def _parse_semver(value: str) -> tuple[tuple[int, int, int], tuple[str, ...] | None]:
    if not _SEMVER.fullmatch(value):
        raise CandidateBomError("module version is invalid")
    without_build = value.split("+", 1)[0]
    core_text, separator, prerelease_text = without_build.partition("-")
    core = tuple(int(part) for part in core_text.split("."))
    prerelease = tuple(prerelease_text.split(".")) if separator else None
    return (core[0], core[1], core[2]), prerelease


def _compare_semver(left: str, right: str) -> int:
    left_core, left_pre = _parse_semver(left)
    right_core, right_pre = _parse_semver(right)
    if left_core != right_core:
        return -1 if left_core < right_core else 1
    if left_pre is None or right_pre is None:
        if left_pre is right_pre:
            return 0
        return 1 if left_pre is None else -1
    for left_part, right_part in zip(left_pre, right_pre):
        if left_part == right_part:
            continue
        left_numeric = left_part.isdigit()
        right_numeric = right_part.isdigit()
        if left_numeric and right_numeric:
            return -1 if int(left_part) < int(right_part) else 1
        if left_numeric != right_numeric:
            return -1 if left_numeric else 1
        return -1 if left_part < right_part else 1
    if len(left_pre) == len(right_pre):
        return 0
    return -1 if len(left_pre) < len(right_pre) else 1


def _version_satisfies_range(version: str, version_range: str) -> bool:
    tokens = version_range.split()
    if not tokens or len(tokens) > 8:
        return False
    for token in tokens:
        match = re.fullmatch(r"(>=|<=|>|<|=)?(.+)", token)
        if match is None or not _SEMVER.fullmatch(match.group(2)):
            return False
        comparison = _compare_semver(version, match.group(2))
        operator = match.group(1) or "="
        if not {
            "=": comparison == 0,
            ">": comparison > 0,
            ">=": comparison >= 0,
            "<": comparison < 0,
            "<=": comparison <= 0,
        }[operator]:
            return False
    return True


def canonical_bytes(value: Any) -> bytes:
    stack = [value]
    while stack:
        current = stack.pop()
        if isinstance(current, Mapping):
            stack.extend(current.values())
        elif isinstance(current, Sequence) and not isinstance(
            current, (str, bytes, bytearray)
        ):
            stack.extend(current)
        elif isinstance(current, bool) or current is None:
            continue
        elif isinstance(current, int):
            try:
                digits = str(abs(current))
            except ValueError as exc:
                raise ValueError(
                    "JSON integer exceeds the canonical digit limit"
                ) from exc
            if len(digits) > _MAX_CANONICAL_NUMBER_DIGITS:
                raise ValueError("JSON integer exceeds the canonical digit limit")
        elif isinstance(current, float) and not math.isfinite(current):
            raise ValueError("JSON float is not finite")
    return json.dumps(
        value,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=False,
        allow_nan=False,
    ).encode("utf-8")


def _require_canonical_bom_wire(
    value: Mapping[str, Any], exact_bytes: bytes, label: str
) -> None:
    try:
        canonical = canonical_bytes(value)
    except (OverflowError, ValueError) as exc:
        raise CandidateBomError(
            f"{label} contains a number outside the canonical JSON domain"
        ) from exc
    if exact_bytes != canonical:
        raise CandidateBomError(
            f"{label} must be the canonical sorted compact JSON wire"
        )


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _canonical_authority_hash(authority: Mapping[str, Any]) -> str:
    """Hash the Policy-owner authority tuple with an unambiguous wire profile.

    Every field is UTF-8 encoded and prefixed by an unsigned 32-bit big-endian
    byte length.  Integers use invariant base-10 and booleans use lowercase
    JSON spelling.  The digest field itself is deliberately excluded.
    """
    ordered_values = (
        _NATIVE_STOP_AUTHORITY_HASH_DOMAIN,
        authority["authority_id"],
        authority["producer_module"],
        authority["worker_module_id"],
        authority["worker_artifact_id"],
        authority["worker_artifact_sha256"],
        authority["worker_version"],
        authority["worker_slot"],
        authority["worker_instance_id"],
        str(authority["worker_generation"]),
        authority["key_id"],
        authority["p256_spki_sha256"],
        authority["signature_algorithm"],
        authority["signature_format"],
        authority["auth_scope"],
        authority["native_stop_contract_id"],
        authority["policy_id"],
        str(authority["release_bom_generation"]),
        authority["activation_token_sha256"],
        str(authority["rotation_epoch"]),
        authority["valid_from"],
        authority["valid_until"],
        "true" if authority["revoked"] else "false",
    )
    wire = bytearray()
    for value in ordered_values:
        encoded = str(value).encode("utf-8", errors="strict")
        if len(encoded) > 0xFFFFFFFF:
            raise CandidateBomError("native stop authority field exceeds the wire limit")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return sha256_bytes(bytes(wire))


def _canonical_authorities_hash(authorities: Sequence[Mapping[str, Any]]) -> str:
    wire = bytearray()
    ordered_values = (
        _NATIVE_STOP_AUTHORITIES_HASH_DOMAIN,
        str(len(authorities)),
        *(str(authority["worker_authority_sha256"]) for authority in authorities),
    )
    for value in ordered_values:
        encoded = value.encode("utf-8", errors="strict")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return sha256_bytes(bytes(wire))


def _canonical_route_authority_hash(authority: Mapping[str, Any]) -> str:
    ordered_values = (
        _DEVICE_ROUTE_AUTHORITY_HASH_DOMAIN,
        authority["route_authority_id"],
        authority["producer_module"],
        authority["supervisor_module_id"],
        authority["supervisor_artifact_id"],
        authority["supervisor_artifact_sha256"],
        authority["supervisor_version"],
        authority["supervisor_instance_id"],
        str(authority["supervisor_generation"]),
        authority["route_signer_key_id"],
        authority["route_signer_p256_spki_sha256"],
        authority["signature_algorithm"],
        authority["signature_format"],
        authority["auth_scope"],
        authority["policy_id"],
        str(authority["release_bom_generation"]),
        authority["activation_token_sha256"],
        str(authority["rotation_epoch"]),
        authority["valid_from"],
        authority["valid_until"],
        "true" if authority["revoked"] else "false",
    )
    wire = bytearray()
    for value in ordered_values:
        encoded = str(value).encode("utf-8", errors="strict")
        if len(encoded) > 0xFFFFFFFF:
            raise CandidateBomError("device route authority field exceeds the wire limit")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return sha256_bytes(bytes(wire))


def _canonical_route_authorities_hash(
    authorities: Sequence[Mapping[str, Any]],
) -> str:
    wire = bytearray()
    ordered_values = (
        _DEVICE_ROUTE_AUTHORITIES_HASH_DOMAIN,
        str(len(authorities)),
        *(str(authority["route_authority_sha256"]) for authority in authorities),
    )
    for value in ordered_values:
        encoded = value.encode("utf-8", errors="strict")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return sha256_bytes(bytes(wire))


def _canonical_challenge_authority_hash(authority: Mapping[str, Any]) -> str:
    ordered_values = (
        _NATIVE_STOP_CHALLENGE_AUTHORITY_HASH_DOMAIN,
        authority["authority_id"], authority["producer_module"],
        authority["policy_module_id"], authority["policy_artifact_id"],
        authority["policy_artifact_sha256"], authority["policy_version"],
        authority["policy_instance_id"], str(authority["policy_generation"]),
        authority["key_id"], authority["p256_spki_sha256"],
        authority["signature_algorithm"], authority["signature_format"],
        authority["auth_scope"], authority["native_stop_challenge_contract_id"],
        authority["policy_id"], str(authority["release_bom_generation"]),
        authority["activation_token_sha256"], str(authority["rotation_epoch"]),
        authority["valid_from"], authority["valid_until"],
        "true" if authority["revoked"] else "false",
    )
    wire = bytearray()
    for value in ordered_values:
        encoded = str(value).encode("utf-8", errors="strict")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return sha256_bytes(bytes(wire))


def _canonical_challenge_authorities_hash(
    authorities: Sequence[Mapping[str, Any]],
) -> str:
    wire = bytearray()
    for value in (
        _NATIVE_STOP_CHALLENGE_AUTHORITIES_HASH_DOMAIN,
        str(len(authorities)),
        *(str(authority["challenge_authority_sha256"]) for authority in authorities),
    ):
        encoded = value.encode("utf-8", errors="strict")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return sha256_bytes(bytes(wire))


def _canonical_authority_sets_hash(
    native_stop_authorities_sha256: str,
    device_route_assignment_authorities_sha256: str,
    native_stop_challenge_authorities_sha256: str,
) -> str:
    wire = bytearray()
    for value in (
        _AUTHORITY_SETS_HASH_DOMAIN,
        native_stop_authorities_sha256,
        device_route_assignment_authorities_sha256,
        native_stop_challenge_authorities_sha256,
    ):
        encoded = value.encode("utf-8", errors="strict")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return sha256_bytes(bytes(wire))


def _der_length(length: int) -> bytes:
    if length < 0:
        raise CandidateBomError("invalid DER length")
    if length < 0x80:
        return bytes((length,))
    encoded = length.to_bytes((length.bit_length() + 7) // 8, "big")
    return bytes((0x80 | len(encoded),)) + encoded


def _der_value(tag: int, payload: bytes) -> bytes:
    return bytes((tag,)) + _der_length(len(payload)) + payload


def _der_integer(value: int) -> bytes:
    if value < 0:
        raise CandidateBomError("negative RSA integer is invalid")
    encoded = value.to_bytes(max(1, (value.bit_length() + 7) // 8), "big")
    if encoded[0] & 0x80:
        encoded = b"\x00" + encoded
    return _der_value(0x02, encoded)


def _rsa_spki_sha256(modulus: int, exponent: int) -> str:
    rsa_public_key = _der_value(0x30, _der_integer(modulus) + _der_integer(exponent))
    rsa_encryption_algorithm = bytes.fromhex("300d06092a864886f70d0101010500")
    subject_public_key = _der_value(0x03, b"\x00" + rsa_public_key)
    return sha256_bytes(_der_value(0x30, rsa_encryption_algorithm + subject_public_key))


def build_native_stop_trust_receipt_payload(
    bom: Mapping[str, Any],
    exact_bom_bytes: bytes,
    trust_policy_id: str,
    receipt_id: str,
    trace_id: str,
    occurred_at: str,
) -> dict[str, Any]:
    """Build only the deterministic *unsigned* external-signer payload.

    This helper owns the wire format; it does not issue authority and never has
    access to signing material.  Callers must still pass the resulting payload
    to the separately provisioned native-stop-trust signer and the validator
    must verify the resulting receipt before it can be consumed by Policy.
    """
    if not isinstance(exact_bom_bytes, bytes) or not exact_bom_bytes:
        raise CandidateBomError("exact Release BOM bytes are required for the trust receipt")
    if not isinstance(bom, Mapping):
        raise CandidateBomError("signed Release BOM must be one JSON object")
    _require_canonical_bom_wire(bom, exact_bom_bytes, "signed Release BOM")
    if not isinstance(receipt_id, str) or not re.fullmatch(
        r"native-stop-trust-[0-9a-f]{32}", receipt_id
    ):
        raise CandidateBomError("native stop trust receipt_id is invalid")
    if not isinstance(trace_id, str) or not _TRACE_ID.fullmatch(trace_id):
        raise CandidateBomError("native stop trust trace_id is invalid")
    _dotnet_utc_ticks(occurred_at, "native stop trust receipt occurred_at")
    if not isinstance(trust_policy_id, str) or not _OPAQUE_ID.fullmatch(trust_policy_id):
        raise CandidateBomError("native stop trust policy id is invalid")
    authorities = bom.get("native_stop_authorities")
    route_authorities = bom.get("device_route_assignment_authorities")
    challenge_authorities = bom.get("native_stop_challenge_authorities")
    if not isinstance(authorities, Sequence) or isinstance(authorities, (str, bytes, bytearray)):
        raise CandidateBomError("native stop authorities are required for the trust receipt")
    if not isinstance(route_authorities, Sequence) or isinstance(
        route_authorities, (str, bytes, bytearray)
    ):
        raise CandidateBomError("device route authorities are required for the trust receipt")
    if not isinstance(challenge_authorities, Sequence) or isinstance(
        challenge_authorities, (str, bytes, bytearray)
    ):
        raise CandidateBomError("native stop challenge authorities are required for the trust receipt")
    native_digest = _canonical_authorities_hash(authorities)
    route_digest = _canonical_route_authorities_hash(route_authorities)
    challenge_digest = _canonical_challenge_authorities_hash(challenge_authorities)
    bom_sha256 = sha256_bytes(exact_bom_bytes)
    idempotency_material = {
        "contract_id": "release.bom.native.stop.authority.trust/v1",
        "receipt_id": receipt_id,
        "release_bom_sha256": bom_sha256,
    }
    payload = {
        "schema_version": "1.0.0",
        "contract_id": "release.bom.native.stop.authority.trust/v1",
        "producer_module": "factory-release-controller",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": trace_id,
        "idempotency_key": "idem_" + sha256_bytes(canonical_bytes(idempotency_material)),
        "occurred_at": occurred_at,
        "privacy_class": "internal",
        "receipt_id": receipt_id,
        "release_bom_id": bom.get("bom_id"),
        "release_bom_sha256": bom_sha256,
        "integration_commit": bom.get("integration_commit"),
        "release_bom_generation": bom.get("release_bom_generation"),
        "activation_token_sha256": bom.get("activation_token_sha256"),
        "trust_policy_id": trust_policy_id,
        "native_stop_authorities_sha256": native_digest,
        "device_route_assignment_authorities_sha256": route_digest,
        "native_stop_challenge_authorities_sha256": challenge_digest,
        "authority_sets_sha256": _canonical_authority_sets_hash(
            native_digest, route_digest, challenge_digest
        ),
        "native_stop_authorities": list(authorities),
        "device_route_assignment_authorities": list(route_authorities),
        "native_stop_challenge_authorities": list(challenge_authorities),
    }
    _require_exact(payload, _NATIVE_STOP_TRUST_RECEIPT_PAYLOAD_FIELDS, "native stop trust payload")
    return payload


def native_stop_trust_signing_bytes(payload: Mapping[str, Any]) -> bytes:
    _require_exact(payload, _NATIVE_STOP_TRUST_RECEIPT_PAYLOAD_FIELDS, "native stop trust payload")
    ordered_values = (
        _NATIVE_STOP_TRUST_SIGNATURE_DOMAIN.rstrip(b"\n").decode("ascii"),
        payload["schema_version"],
        payload["contract_id"],
        payload["producer_module"],
        "" if payload["soul_id"] is None else payload["soul_id"],
        "" if payload["device_binding_id"] is None else payload["device_binding_id"],
        "" if payload["platform_account_id"] is None else payload["platform_account_id"],
        payload["trace_id"],
        payload["idempotency_key"],
        payload["occurred_at"],
        payload["privacy_class"],
        payload["receipt_id"],
        payload["release_bom_id"],
        payload["release_bom_sha256"],
        payload["integration_commit"],
        str(payload["release_bom_generation"]),
        payload["activation_token_sha256"],
        payload["trust_policy_id"],
        payload["native_stop_authorities_sha256"],
        payload["device_route_assignment_authorities_sha256"],
        payload["native_stop_challenge_authorities_sha256"],
        payload["authority_sets_sha256"],
    )
    wire = bytearray()
    for value in ordered_values:
        encoded = str(value).encode("utf-8", errors="strict")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return bytes(wire)


def _require_mapping(value: Any, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise CandidateBomError(f"{name} must be an object")
    return value


def _require_sequence(value: Any, name: str) -> Sequence[Any]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes, bytearray)):
        raise CandidateBomError(f"{name} must be an array")
    return value


def _require_exact(value: Mapping[str, Any], fields: set[str], name: str) -> None:
    missing = sorted(fields.difference(value))
    unknown = sorted(set(value).difference(fields))
    if missing or unknown:
        details = []
        if missing:
            details.append("missing=" + ",".join(missing))
        if unknown:
            details.append("unknown=" + ",".join(unknown))
        raise CandidateBomError(f"{name} has invalid fields: {'; '.join(details)}")


def _mgf1(seed: bytes, length: int) -> bytes:
    output = bytearray()
    counter = 0
    while len(output) < length:
        output.extend(hashlib.sha256(seed + counter.to_bytes(4, "big")).digest())
        counter += 1
    return bytes(output[:length])


def _verify_rsa_pss(message: bytes, signature: bytes, modulus: int, exponent: int) -> bool:
    if modulus.bit_length() < 1024 or exponent < 3 or exponent % 2 == 0:
        return False
    em_bits = modulus.bit_length() - 1
    em_length = (em_bits + 7) // 8
    if len(signature) != (modulus.bit_length() + 7) // 8:
        return False
    signature_representative = int.from_bytes(signature, "big")
    if signature_representative >= modulus:
        return False
    encoded = pow(signature_representative, exponent, modulus).to_bytes(
        em_length, "big"
    )
    digest_length = hashlib.sha256().digest_size
    salt_length = digest_length
    if em_length < digest_length + salt_length + 2 or encoded[-1] != 0xBC:
        return False
    masked_db = encoded[: em_length - digest_length - 1]
    encoded_hash = encoded[em_length - digest_length - 1 : -1]
    unused_bits = 8 * em_length - em_bits
    if unused_bits and masked_db[0] >> (8 - unused_bits):
        return False
    data_mask = _mgf1(encoded_hash, len(masked_db))
    data_block = bytearray(left ^ right for left, right in zip(masked_db, data_mask))
    if unused_bits:
        data_block[0] &= 0xFF >> unused_bits
    padding_length = em_length - digest_length - salt_length - 2
    if data_block[:padding_length] != b"\x00" * padding_length or data_block[padding_length] != 0x01:
        return False
    salt = bytes(data_block[-salt_length:])
    expected = hashlib.sha256(b"\x00" * 8 + hashlib.sha256(message).digest() + salt).digest()
    return hmac.compare_digest(encoded_hash, expected)


class ReleaseTrustPolicy:
    """Process-bound public verification facts and separation-of-duties policy."""

    _FIELDS = {
        "schema_version", "policy_id", "keys", "required_gates",
        "implementer_identities", "evidence_issuer_identities",
        "release_controller_identities", "release_approver_identities",
        "native_stop_trust_signer_identities", "allow_bootstrap",
    }
    _KEY_FIELDS = {"key_id", "identity", "algorithm", "modulus_hex", "exponent", "purposes"}
    _GATE_FIELDS = {"kind", "minimum_verification_level"}
    _PURPOSES = {"artifact", "evidence", "approval", "bom", "native-stop-trust"}

    def __init__(self, value: Mapping[str, Any]) -> None:
        _require_exact(value, self._FIELDS, "release trust policy")
        if value.get("schema_version") != "dps.release-trust-policy/v1":
            raise CandidateBomError("unknown release trust policy version")
        self.policy_id = value.get("policy_id")
        if not isinstance(self.policy_id, str) or len(self.policy_id) < 8:
            raise CandidateBomError("release trust policy id is invalid")
        required_gates = _require_mapping(value.get("required_gates"), "required_gates")
        if not required_gates:
            raise CandidateBomError("trusted required gates are missing")
        normalized_gates: dict[str, tuple[str, str]] = {}
        if len(required_gates) > _MAX_EVIDENCE:
            raise CandidateBomError("trusted required gates exceed the item limit")
        for gate_id, raw_gate in required_gates.items():
            if not isinstance(gate_id, str) or not _OPAQUE_ID.fullmatch(gate_id):
                raise CandidateBomError("trusted required gate id is invalid")
            gate = _require_mapping(raw_gate, f"required gate {gate_id}")
            _require_exact(gate, self._GATE_FIELDS, f"required gate {gate_id}")
            kind = gate.get("kind")
            minimum = gate.get("minimum_verification_level")
            if kind not in _KIND_CEILING or minimum not in _VERIFICATION_RANK:
                raise CandidateBomError("trusted required gate kind or level is invalid")
            if _VERIFICATION_RANK[minimum] > _KIND_CEILING[kind]:
                raise CandidateBomError("trusted required gate minimum exceeds its evidence-kind ceiling")
            normalized_gates[gate_id] = (kind, minimum)
        self.required_gates = normalized_gates
        identity_groups: list[frozenset[str]] = []
        for field, label in (
            ("implementer_identities", "implementers"),
            ("evidence_issuer_identities", "evidence issuers"),
            ("release_controller_identities", "release controllers"),
            ("release_approver_identities", "release approvers"),
            ("native_stop_trust_signer_identities", "native stop trust signers"),
        ):
            raw_identities = list(_require_limited_sequence(value.get(field), label, 256))
            if (
                len(set(raw_identities)) != len(raw_identities)
                or any(not isinstance(identity, str) or not _OPAQUE_ID.fullmatch(identity) for identity in raw_identities)
            ):
                raise CandidateBomError(f"{label} contain invalid or duplicate identities")
            identity_groups.append(frozenset(raw_identities))
        (
            self.implementers,
            self.evidence_issuers,
            self.release_controllers,
            self.release_approvers,
            self.native_stop_trust_signers,
        ) = identity_groups
        role_groups = [
            self.implementers,
            self.evidence_issuers,
            self.release_controllers,
            self.release_approvers,
            self.native_stop_trust_signers,
        ]
        if any(not group for group in role_groups):
            raise CandidateBomError("all trusted role groups must be assigned")
        if any(left & right for index, left in enumerate(role_groups) for right in role_groups[index + 1 :]):
            raise CandidateBomError("release trust policy violates separation of duties")
        if not isinstance(value.get("allow_bootstrap"), bool):
            raise CandidateBomError("allow_bootstrap must be boolean")
        self.allow_bootstrap = value["allow_bootstrap"]
        self._keys: dict[str, tuple[str, int, int, frozenset[str]]] = {}
        key_material_identities: dict[tuple[int, int], str] = {}
        for raw_key in _require_limited_sequence(value.get("keys"), "keys", 256):
            key = _require_mapping(raw_key, "trust key")
            _require_exact(key, self._KEY_FIELDS, "trust key")
            key_id = key.get("key_id")
            identity = key.get("identity")
            raw_purposes = list(_require_limited_sequence(key.get("purposes"), "key purposes", 4))
            purposes = frozenset(raw_purposes)
            if (
                not isinstance(key_id, str) or not _OPAQUE_ID.fullmatch(key_id) or key_id in self._keys
                or not isinstance(identity, str) or not _OPAQUE_ID.fullmatch(identity)
                or len(purposes) != len(raw_purposes)
                or not purposes or not purposes.issubset(self._PURPOSES)
                or key.get("algorithm") != "rsa-pss-sha256"
            ):
                raise CandidateBomError("invalid or duplicate trust key")
            try:
                modulus = int(str(key.get("modulus_hex")), 16)
                exponent = int(key.get("exponent"))
            except (TypeError, ValueError) as exc:
                raise CandidateBomError("invalid RSA public key") from exc
            if (
                not isinstance(key.get("modulus_hex"), str)
                or not re.fullmatch(r"[0-9a-f]+", key["modulus_hex"])
                or len(key["modulus_hex"]) > 2048
                or isinstance(key.get("exponent"), bool)
                or not isinstance(key.get("exponent"), int)
                or exponent < 3
                or exponent % 2 == 0
                or modulus.bit_length() < 1024
            ):
                raise CandidateBomError("trusted RSA modulus must be at least 1024 bits")
            if "bom" in purposes and (
                raw_purposes != ["bom"]
                or key.get("algorithm") != "rsa-pss-sha256"
                or not re.fullmatch(r"[1-9a-f][0-9a-f]*", key["modulus_hex"])
                or (modulus.bit_length() + 7) // 8 < 256
                or exponent != 65537
            ):
                raise CandidateBomError(
                    "BOM trust key does not match the external signer profile"
                )
            prior_identity = key_material_identities.get((modulus, exponent))
            if prior_identity is not None and prior_identity != identity:
                raise CandidateBomError("one public key cannot represent multiple separated identities")
            key_material_identities[(modulus, exponent)] = identity
            for purpose in purposes:
                allowed = {
                    "artifact": self.release_controllers,
                    "evidence": self.evidence_issuers,
                    "approval": self.release_approvers,
                    "bom": self.release_controllers,
                    "native-stop-trust": self.native_stop_trust_signers,
                }[purpose]
                if identity not in allowed:
                    raise CandidateBomError(f"key identity is not assigned to {purpose}")
            self._keys[key_id] = (identity, modulus, exponent, purposes)
        if not self._keys:
            raise CandidateBomError("release trust policy has no keys")

        self.release_trust_key_ids = frozenset(self._keys)
        self.release_trust_spki_sha256s = frozenset(
            _rsa_spki_sha256(modulus, exponent)
            for _, modulus, exponent, _ in self._keys.values()
        )

        receipt_keys = [
            key_id for key_id, (_, _, _, purposes) in self._keys.items()
            if "native-stop-trust" in purposes
        ]
        if len(receipt_keys) != 1:
            raise CandidateBomError("release trust policy must pin exactly one native stop trust receipt key")
        self.native_stop_trust_key_id = receipt_keys[0]
        _, receipt_modulus, receipt_exponent, receipt_purposes = self._keys[receipt_keys[0]]
        if receipt_purposes != frozenset({"native-stop-trust"}):
            raise CandidateBomError("native stop trust receipt key cannot be reused for another purpose")
        self.native_stop_trust_spki_sha256 = _rsa_spki_sha256(receipt_modulus, receipt_exponent)

    def verify_signature(self, signature: Mapping[str, Any], message: bytes, purpose: str) -> str:
        _require_exact(signature, _SIGNATURE_FIELDS, "signature")
        if signature.get("algorithm") != "rsa-pss-sha256":
            raise CandidateBomError("only rsa-pss-sha256 signatures are supported")
        trusted = self._keys.get(signature.get("key_id"))
        if trusted is None or purpose not in trusted[3]:
            raise CandidateBomError(f"signature key is not trusted for {purpose}")
        try:
            signature_value = str(signature.get("value"))
            raw_signature = base64.b64decode(signature_value, validate=True)
        except (binascii.Error, ValueError) as exc:
            raise CandidateBomError("signature value is not valid base64") from exc
        if base64.b64encode(raw_signature).decode("ascii") != signature_value:
            raise CandidateBomError("signature value is not canonical base64")
        identity, modulus, exponent, _ = trusted
        if not _verify_rsa_pss(message, raw_signature, modulus, exponent):
            raise CandidateBomError(f"{purpose} signature verification failed")
        return identity


def _read_stable_regular(
    path: Path,
    label: str,
    maximum_bytes: int = _MAX_CONTROL_JSON_BYTES,
) -> bytes:
    try:
        before = path.lstat()
    except OSError as exc:
        raise CandidateBomError(f"{label} does not exist") from exc
    if stat.S_ISLNK(before.st_mode) or not stat.S_ISREG(before.st_mode):
        raise CandidateBomError(f"{label} must be a non-symlink regular file")
    if before.st_size < 0 or before.st_size > maximum_bytes:
        raise CandidateBomError(f"{label} exceeds the byte limit")
    data = path.read_bytes()
    after = path.lstat()
    if (before.st_ino, before.st_dev, before.st_size, before.st_mtime_ns) != (
        after.st_ino, after.st_dev, after.st_size, after.st_mtime_ns
    ):
        raise CandidateBomError(f"{label} changed while being read")
    if len(data) != before.st_size:
        raise CandidateBomError(f"{label} size changed while being read")
    return data


def _resolve_bundle_file(root: Path, uri: Any, label: str) -> Path:
    canonical_uri = _require_bundle_uri(uri, label)
    relative = PurePosixPath(canonical_uri)
    candidate = root
    for part in relative.parts:
        candidate = candidate / part
        try:
            information = candidate.lstat()
        except OSError as exc:
            raise CandidateBomError(f"{label} path does not exist") from exc
        if stat.S_ISLNK(information.st_mode):
            raise CandidateBomError(f"{label} path contains a symbolic link")
    resolved = candidate.resolve(strict=True)
    try:
        resolved.relative_to(root)
    except ValueError as exc:
        raise CandidateBomError(f"{label} path escapes the bundle root") from exc
    return resolved


def _read_bundle_file(
    root: Path,
    uri: Any,
    label: str,
    maximum_bytes: int = _MAX_CONTROL_JSON_BYTES,
) -> tuple[Path, bytes]:
    resolved = _resolve_bundle_file(root, uri, label)
    return resolved, _read_stable_regular(resolved, label, maximum_bytes)


def _hash_bundle_file(root: Path, uri: Any, label: str) -> tuple[Path, str, int]:
    resolved = _resolve_bundle_file(root, uri, label)
    try:
        before = resolved.lstat()
    except OSError as exc:
        raise CandidateBomError(f"{label} does not exist") from exc
    if stat.S_ISLNK(before.st_mode) or not stat.S_ISREG(before.st_mode):
        raise CandidateBomError(f"{label} must be a non-symlink regular file")
    if before.st_size < 0 or before.st_size > _MAX_ARTIFACT_BYTES:
        raise CandidateBomError(f"{label} exceeds the artifact byte limit")
    digest = hashlib.sha256()
    count = 0
    with resolved.open("rb") as stream:
        while True:
            chunk = stream.read(1024 * 1024)
            if not chunk:
                break
            count += len(chunk)
            if count > _MAX_ARTIFACT_BYTES:
                raise CandidateBomError(f"{label} exceeds the artifact byte limit")
            digest.update(chunk)
    after = resolved.lstat()
    if (before.st_ino, before.st_dev, before.st_size, before.st_mtime_ns) != (
        after.st_ino, after.st_dev, after.st_size, after.st_mtime_ns
    ) or count != before.st_size:
        raise CandidateBomError(f"{label} changed while being hashed")
    return resolved, digest.hexdigest(), count


class GitObjectReader:
    """Read only an exact commit using fixed Git argv and a sanitized environment."""

    def __init__(self, repository_root: Path) -> None:
        executable = shutil.which("git")
        if executable is None:
            raise CandidateBomError("git executable is required")
        self._git = executable
        self._root = repository_root

    def _run(self, arguments: list[str], maximum_bytes: int = _MAX_CONTROL_JSON_BYTES) -> bytes:
        result = subprocess.run(
            [self._git, "-C", str(self._root), *arguments],
            check=False,
            shell=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=30,
            env={
                "PATH": str(Path(self._git).parent), "LC_ALL": "C", "LANG": "C",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_TERMINAL_PROMPT": "0",
            },
        )
        if result.returncode != 0:
            raise CandidateBomError("required Git object is absent from the integration commit")
        if len(result.stdout) > maximum_bytes or len(result.stderr) > _MAX_CONTROL_JSON_BYTES:
            raise CandidateBomError("Git output exceeds the byte limit")
        return result.stdout

    def read(
        self,
        commit: str,
        relative_path: str,
        maximum_bytes: int = _MAX_CONTROL_JSON_BYTES,
    ) -> bytes:
        if not _COMMIT.fullmatch(commit):
            raise CandidateBomError("invalid integration commit")
        path = PurePosixPath(relative_path)
        if (
            path.is_absolute()
            or "." in path.parts
            or ".." in path.parts
            or "\\" in relative_path
            or path.as_posix() != relative_path
            or len(relative_path.encode("utf-8")) > _MAX_URI_BYTES
        ):
            raise CandidateBomError("invalid Git object path")
        object_name = f"{commit}:{path.as_posix()}"
        size_bytes = self._run(["cat-file", "-s", object_name], 128)
        try:
            size = int(size_bytes.decode("ascii").strip(), 10)
        except (UnicodeDecodeError, ValueError) as exc:
            raise CandidateBomError("Git object size is invalid") from exc
        if size < 0 or size > maximum_bytes:
            raise CandidateBomError("required Git object exceeds the byte limit")
        content = self._run(["show", object_name], maximum_bytes)
        if len(content) != size:
            raise CandidateBomError("Git object size changed while being read")
        return content

    def is_ancestor(self, ancestor: str, descendant: str) -> bool:
        if not _COMMIT.fullmatch(ancestor) or not _COMMIT.fullmatch(descendant):
            raise CandidateBomError("invalid integration commit lineage")
        result = subprocess.run(
            [self._git, "-C", str(self._root), "merge-base", "--is-ancestor", ancestor, descendant],
            check=False,
            shell=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            timeout=30,
            env={
                "PATH": str(Path(self._git).parent), "LC_ALL": "C", "LANG": "C",
                "GIT_CONFIG_NOSYSTEM": "1", "GIT_TERMINAL_PROMPT": "0",
            },
        )
        if len(result.stdout) > 128 or len(result.stderr) > _MAX_CONTROL_JSON_BYTES:
            raise CandidateBomError("Git lineage output exceeds the byte limit")
        if result.returncode not in {0, 1}:
            raise CandidateBomError("Git lineage could not be verified")
        return result.returncode == 0


class CandidateBomValidator:
    """Validate a signed candidate without executing build or deployment commands."""

    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        bundle_root: str | os.PathLike[str],
        trust_policy: Mapping[str, Any],
        expected_trust_policy_sha256: str,
        expected_schema_sha256: str,
        *,
        validation_time: str | None = None,
        minimum_remaining_lifetime_seconds: int = _DEFAULT_MINIMUM_REMAINING_LIFETIME_SECONDS,
    ) -> None:
        if validation_time is None:
            self._validation_time_ns = _system_utc_now_nanoseconds()
        else:
            self._validation_time_ns = _utc_instant_nanoseconds(
                validation_time, "validation time"
            )
        if (
            isinstance(minimum_remaining_lifetime_seconds, bool)
            or not isinstance(minimum_remaining_lifetime_seconds, int)
            or minimum_remaining_lifetime_seconds <= 0
        ):
            raise CandidateBomError(
                "minimum remaining lifetime must be a positive integer number of seconds"
            )
        self._minimum_remaining_lifetime_seconds = minimum_remaining_lifetime_seconds
        repository = Path(repository_root)
        bundle = Path(bundle_root)
        if repository.is_symlink() or not repository.is_dir():
            raise CandidateBomError("repository root must be a non-symlink directory")
        if bundle.is_symlink() or not bundle.is_dir():
            raise CandidateBomError("bundle root must be a non-symlink directory")
        self._repository = repository.resolve(strict=True)
        self._bundle = bundle.resolve(strict=True)
        if not _SHA256.fullmatch(expected_trust_policy_sha256):
            raise CandidateBomError("trusted policy hash is invalid")
        actual_policy_sha = sha256_bytes(canonical_bytes(dict(trust_policy)))
        if actual_policy_sha != expected_trust_policy_sha256:
            raise CandidateBomError("release trust policy hash mismatch")
        self._trust = ReleaseTrustPolicy(trust_policy)
        schema_path = self._repository / "governance" / "schemas" / "release-bom.schema.json"
        schema_bytes = _read_stable_regular(schema_path, "Release BOM schema")
        if not _SHA256.fullmatch(expected_schema_sha256) or sha256_bytes(schema_bytes) != expected_schema_sha256:
            raise CandidateBomError("Release BOM schema hash mismatch")
        schema = _require_mapping(
            _strict_json_loads(schema_bytes, "Release BOM schema"),
            "Release BOM schema",
        )
        expected_required = _BOM_FIELDS
        if (
            schema.get("$id") != "https://dps.local/schemas/release-bom.schema.json"
            or schema.get("additionalProperties") is not False
            or set(schema.get("required", [])) != expected_required
        ):
            raise CandidateBomError("Release BOM schema is not the validator-compatible exact schema")
        self._schema_sha256 = expected_schema_sha256
        self._git = GitObjectReader(self._repository)

    @classmethod
    def from_deployed_anchor(
        cls,
        repository_root: str | os.PathLike[str],
        bundle_root: str | os.PathLike[str],
        expected_schema_sha256: str,
        *,
        validation_time: str | None = None,
        minimum_remaining_lifetime_seconds: int = _DEFAULT_MINIMUM_REMAINING_LIFETIME_SECONDS,
    ) -> "CandidateBomValidator":
        """Load the only policy accepted by the operational validation CLI.

        The path, canonical digest, and policy identity are code-bound deployment
        facts.  None can be selected or overridden by a candidate or caller.
        """
        raw_repository = Path(repository_root)
        if raw_repository.is_symlink() or not raw_repository.is_dir():
            raise CandidateBomError("repository root must be a non-symlink directory")
        repository = raw_repository.resolve(strict=True)
        policy_path = repository / _DEPLOYED_TRUST_POLICY_RELATIVE
        policy_bytes = _read_stable_regular(policy_path, "deployed release trust policy")
        policy = _require_mapping(
            _strict_json_loads(policy_bytes, "deployed release trust policy"),
            "deployed release trust policy",
        )
        if (
            sha256_bytes(canonical_bytes(dict(policy))) != _DEPLOYED_TRUST_POLICY_SHA256
            or policy.get("policy_id") != _DEPLOYED_TRUST_POLICY_ID
        ):
            raise CandidateBomError("deployed release trust anchor does not match the code-bound identity and digest")
        return cls(
            repository,
            bundle_root,
            policy,
            _DEPLOYED_TRUST_POLICY_SHA256,
            expected_schema_sha256,
            validation_time=validation_time,
            minimum_remaining_lifetime_seconds=minimum_remaining_lifetime_seconds,
        )

    def _validate_signature_shape(self, signature: Any, label: str) -> Mapping[str, Any]:
        value = _require_mapping(signature, label)
        _require_exact(value, _SIGNATURE_FIELDS, label)
        if value.get("algorithm") != "rsa-pss-sha256":
            raise CandidateBomError(f"{label} uses unsupported algorithm")
        if not isinstance(value.get("key_id"), str) or not value.get("key_id"):
            raise CandidateBomError(f"{label} key_id is required")
        if not isinstance(value.get("value"), str) or not value.get("value"):
            raise CandidateBomError(f"{label} value is required")
        return value

    def _validate_native_stop_authorities_shape(
        self,
        bom: Mapping[str, Any],
    ) -> list[Mapping[str, Any]]:
        generation = bom.get("release_bom_generation")
        if (
            isinstance(generation, bool)
            or not isinstance(generation, int)
            or not 1 <= generation <= 9_223_372_036_854_775_807
        ):
            raise CandidateBomError("release_bom_generation must be a positive signed 64-bit integer")
        activation_token = bom.get("activation_token_sha256")
        if not isinstance(activation_token, str) or not _SHA256.fullmatch(activation_token):
            raise CandidateBomError("activation_token_sha256 is invalid")

        authorities = list(_require_limited_sequence(
            bom.get("native_stop_authorities"),
            "native_stop_authorities",
            _MAX_NATIVE_STOP_AUTHORITIES,
        ))
        if not authorities:
            raise CandidateBomError("at least one native stop authority is required")
        authority_ids: set[str] = set()
        authority_hashes: set[str] = set()
        key_spki: dict[str, str] = {}
        spki_binding: dict[str, tuple[Any, ...]] = {}
        normalized: list[Mapping[str, Any]] = []
        intervals: dict[tuple[Any, ...], list[tuple[int, int]]] = {}
        for index, raw_authority in enumerate(authorities):
            label = f"native_stop_authorities[{index}]"
            authority = _require_mapping(raw_authority, label)
            _require_exact(authority, _NATIVE_STOP_AUTHORITY_FIELDS, label)
            authority_id = authority.get("authority_id")
            key_id = authority.get("key_id")
            if (
                not isinstance(authority_id, str)
                or not _LOWER_OPAQUE_ID.fullmatch(authority_id)
                or authority_id in authority_ids
            ):
                raise CandidateBomError("native stop authority_id is invalid or duplicated")
            if not isinstance(key_id, str) or not _KEY_ID.fullmatch(key_id):
                raise CandidateBomError("native stop authority key_id is invalid")
            if key_id in self._trust.release_trust_key_ids:
                raise CandidateBomError("Worker stop key id cannot reuse any Release trust key id")
            for field in ("worker_artifact_sha256", "p256_spki_sha256", "activation_token_sha256"):
                if not isinstance(authority.get(field), str) or not _SHA256.fullmatch(authority[field]):
                    raise CandidateBomError(f"native stop authority {field} is invalid")
            if authority["p256_spki_sha256"] in self._trust.release_trust_spki_sha256s:
                raise CandidateBomError("Worker P-256 key cannot reuse any Release trust key material")
            if (
                authority.get("producer_module") != "windows-edge-worker"
                or authority.get("worker_module_id") != "windows-edge-worker"
                or authority.get("worker_artifact_id") != "dps.windows-edge-worker"
                or not isinstance(authority.get("worker_version"), str)
                or not _WORKER_SEMVER.fullmatch(authority["worker_version"])
                or authority.get("worker_slot") not in {"A", "B"}
                or not isinstance(authority.get("worker_instance_id"), str)
                or not _WORKER_INSTANCE_ID.fullmatch(authority["worker_instance_id"])
                or authority.get("signature_algorithm") != "ECDSA_P256_SHA256"
                or authority.get("signature_format") != "IEEE_P1363_FIXED_FIELD"
                or authority.get("auth_scope") != "policy-approval:native-stop-proof:v2:commit-unknown"
                or authority.get("native_stop_contract_id") != "native.stop.proof/v2"
                or authority.get("policy_id") != "RESULT-VERIFY-001"
            ):
                raise CandidateBomError("native stop authority Worker, algorithm, format, scope, or policy is invalid")
            worker_generation = authority.get("worker_generation")
            rotation_epoch = authority.get("rotation_epoch")
            if any(
                isinstance(value, bool)
                or not isinstance(value, int)
                or not 1 <= value <= 9_223_372_036_854_775_807
                for value in (worker_generation, rotation_epoch)
            ):
                raise CandidateBomError("native stop authority generation or rotation epoch is invalid")
            if (
                authority.get("release_bom_generation") != generation
                or authority.get("activation_token_sha256") != activation_token
            ):
                raise CandidateBomError("native stop authority does not bind the top-level BOM generation and token")
            if authority.get("revoked") is not False:
                raise CandidateBomError("revoked native stop authority cannot authorize a release")
            valid_from = _dotnet_utc_ticks(authority.get("valid_from"), f"{label} valid_from")
            valid_until = _dotnet_utc_ticks(authority.get("valid_until"), f"{label} valid_until")
            if valid_from >= valid_until:
                raise CandidateBomError("native stop authority validity window is empty or reversed")
            if valid_until - valid_from > _MAX_RUNTIME_AUTHORITY_VALIDITY_TICKS:
                raise CandidateBomError("native stop authority validity exceeds the 31-day policy")
            expected_hash = _canonical_authority_hash(authority)
            if (
                not isinstance(authority.get("worker_authority_sha256"), str)
                or not hmac.compare_digest(authority["worker_authority_sha256"], expected_hash)
                or expected_hash in authority_hashes
            ):
                raise CandidateBomError("native stop authority hash is invalid or duplicated")
            prior_spki = key_spki.get(key_id)
            if prior_spki is not None and prior_spki != authority["p256_spki_sha256"]:
                raise CandidateBomError("one native stop key_id cannot name different P-256 SPKI material")
            if prior_spki is not None:
                raise CandidateBomError("one native stop key_id may authorize only one exact Worker incarnation")
            key_spki[key_id] = authority["p256_spki_sha256"]
            exact_key_binding = (
                key_id,
                authority["worker_module_id"], authority["worker_artifact_id"],
                authority["worker_artifact_sha256"], authority["worker_version"],
                authority["worker_slot"], authority["worker_instance_id"],
                authority["worker_generation"], authority["rotation_epoch"],
            )
            prior_binding = spki_binding.get(authority["p256_spki_sha256"])
            if prior_binding is not None:
                raise CandidateBomError(
                    "one P-256 SPKI may authorize only one key id and exact Worker incarnation"
                )
            spki_binding[authority["p256_spki_sha256"]] = exact_key_binding
            overlap_key = (
                authority["worker_module_id"], authority["worker_artifact_id"],
                authority["worker_artifact_sha256"], authority["worker_version"],
                authority["worker_slot"], authority["worker_instance_id"],
                authority["worker_generation"], authority["policy_id"],
                authority["release_bom_generation"], authority["activation_token_sha256"],
            )
            for prior_from, prior_until in intervals.setdefault(overlap_key, []):
                if valid_from < prior_until and prior_from < valid_until:
                    raise CandidateBomError("native stop authority tuples have overlapping validity")
            intervals[overlap_key].append((valid_from, valid_until))
            authority_ids.add(authority_id)
            authority_hashes.add(expected_hash)
            normalized.append(authority)
        if normalized != sorted(
            normalized,
            key=lambda item: (
                item["worker_module_id"], item["worker_artifact_sha256"],
                item["worker_slot"], item["worker_instance_id"],
                item["worker_generation"], item["rotation_epoch"], item["authority_id"],
            ),
        ):
            raise CandidateBomError("native stop authorities are not in canonical order")
        return normalized

    def _validate_device_route_authorities_shape(
        self,
        bom: Mapping[str, Any],
        native_authorities: Sequence[Mapping[str, Any]],
    ) -> list[Mapping[str, Any]]:
        generation = bom["release_bom_generation"]
        activation_token = bom["activation_token_sha256"]
        authorities = list(_require_limited_sequence(
            bom.get("device_route_assignment_authorities"),
            "device_route_assignment_authorities",
            _MAX_DEVICE_ROUTE_AUTHORITIES,
        ))
        if not authorities:
            raise CandidateBomError("at least one device route assignment authority is required")
        native_key_ids = {item["key_id"] for item in native_authorities}
        native_spki_hashes = {item["p256_spki_sha256"] for item in native_authorities}
        authority_ids: set[str] = set()
        authority_hashes: set[str] = set()
        key_ids: set[str] = set()
        spki_hashes: set[str] = set()
        normalized: list[Mapping[str, Any]] = []
        intervals: dict[tuple[Any, ...], list[tuple[int, int]]] = {}
        for index, raw_authority in enumerate(authorities):
            label = f"device_route_assignment_authorities[{index}]"
            authority = _require_mapping(raw_authority, label)
            _require_exact(authority, _DEVICE_ROUTE_AUTHORITY_FIELDS, label)
            authority_id = authority.get("route_authority_id")
            key_id = authority.get("route_signer_key_id")
            spki_hash = authority.get("route_signer_p256_spki_sha256")
            if (
                not isinstance(authority_id, str)
                or not _LOWER_OPAQUE_ID.fullmatch(authority_id)
                or authority_id in authority_ids
            ):
                raise CandidateBomError("device route authority id is invalid or duplicated")
            if (
                not isinstance(key_id, str)
                or not _ROUTE_SIGNER_KEY_ID.fullmatch(key_id)
                or not isinstance(spki_hash, str)
                or not _SHA256.fullmatch(spki_hash)
                or key_id != "p256_spki_" + spki_hash
            ):
                raise CandidateBomError("device route signer key id must equal its canonical P-256 SPKI digest")
            if (
                key_id in key_ids
                or spki_hash in spki_hashes
                or key_id in native_key_ids
                or spki_hash in native_spki_hashes
                or key_id in self._trust.release_trust_key_ids
                or spki_hash in self._trust.release_trust_spki_sha256s
            ):
                raise CandidateBomError(
                    "device route signer key must be unique and distinct from native stop and Release trust keys"
                )
            for field in (
                "supervisor_artifact_sha256", "activation_token_sha256",
                "route_authority_sha256",
            ):
                if not isinstance(authority.get(field), str) or not _SHA256.fullmatch(authority[field]):
                    raise CandidateBomError(f"device route authority {field} is invalid")
            if (
                authority.get("producer_module") != "factory-release-controller"
                or authority.get("supervisor_module_id") != "windows-edge-supervisor"
                or authority.get("supervisor_artifact_id") != "dps.windows-edge-supervisor"
                or not isinstance(authority.get("supervisor_version"), str)
                or not _WORKER_SEMVER.fullmatch(authority["supervisor_version"])
                or not isinstance(authority.get("supervisor_instance_id"), str)
                or not _SUPERVISOR_INSTANCE_ID.fullmatch(authority["supervisor_instance_id"])
                or authority.get("signature_algorithm") != "ECDSA_P256_SHA256"
                or authority.get("signature_format") != "IEEE_P1363_FIXED_FIELD_LOW_S"
                or authority.get("auth_scope") != "windows-edge-supervisor:device-route-assignment:issue"
                or authority.get("policy_id") != "SOUL-ISO-001"
            ):
                raise CandidateBomError(
                    "device route authority Supervisor, algorithm, format, scope, or policy is invalid"
                )
            if any(
                isinstance(value, bool)
                or not isinstance(value, int)
                or not 1 <= value <= 9_223_372_036_854_775_807
                for value in (
                    authority.get("supervisor_generation"),
                    authority.get("rotation_epoch"),
                )
            ):
                raise CandidateBomError("device route authority generation or rotation epoch is invalid")
            if (
                authority.get("release_bom_generation") != generation
                or authority.get("activation_token_sha256") != activation_token
            ):
                raise CandidateBomError("device route authority does not bind the top-level BOM generation and token")
            if authority.get("revoked") is not False:
                raise CandidateBomError("revoked device route authority cannot authorize a release")
            valid_from = _dotnet_utc_ticks(authority.get("valid_from"), f"{label} valid_from")
            valid_until = _dotnet_utc_ticks(authority.get("valid_until"), f"{label} valid_until")
            if valid_from >= valid_until:
                raise CandidateBomError("device route authority validity window is empty or reversed")
            if valid_until - valid_from > _MAX_RUNTIME_AUTHORITY_VALIDITY_TICKS:
                raise CandidateBomError("device route authority validity exceeds the 31-day policy")
            expected_hash = _canonical_route_authority_hash(authority)
            if (
                not hmac.compare_digest(authority["route_authority_sha256"], expected_hash)
                or expected_hash in authority_hashes
            ):
                raise CandidateBomError("device route authority hash is invalid or duplicated")
            overlap_key = (
                authority["supervisor_module_id"], authority["supervisor_artifact_id"],
                authority["supervisor_artifact_sha256"], authority["supervisor_version"],
                authority["supervisor_instance_id"], authority["supervisor_generation"],
                authority["policy_id"], authority["release_bom_generation"],
                authority["activation_token_sha256"],
            )
            for prior_from, prior_until in intervals.setdefault(overlap_key, []):
                if valid_from < prior_until and prior_from < valid_until:
                    raise CandidateBomError("device route authority tuples have overlapping validity")
            intervals[overlap_key].append((valid_from, valid_until))
            authority_ids.add(authority_id)
            authority_hashes.add(expected_hash)
            key_ids.add(key_id)
            spki_hashes.add(spki_hash)
            normalized.append(authority)
        if normalized != sorted(
            normalized,
            key=lambda item: (
                item["supervisor_module_id"], item["supervisor_artifact_sha256"],
                item["supervisor_instance_id"], item["supervisor_generation"],
                item["rotation_epoch"], item["route_authority_id"],
            ),
        ):
            raise CandidateBomError("device route authorities are not in canonical order")
        return normalized

    def _validate_native_stop_challenge_authorities_shape(
        self,
        bom: Mapping[str, Any],
        native_authorities: Sequence[Mapping[str, Any]],
        route_authorities: Sequence[Mapping[str, Any]],
    ) -> list[Mapping[str, Any]]:
        generation = bom["release_bom_generation"]
        activation_token = bom["activation_token_sha256"]
        authorities = list(_require_limited_sequence(
            bom.get("native_stop_challenge_authorities"),
            "native_stop_challenge_authorities",
            _MAX_NATIVE_STOP_CHALLENGE_AUTHORITIES,
        ))
        if not authorities:
            raise CandidateBomError("at least one native stop challenge authority is required")
        forbidden_key_ids = (
            {item["key_id"] for item in native_authorities}
            | {item["route_signer_key_id"] for item in route_authorities}
            | set(self._trust.release_trust_key_ids)
        )
        forbidden_spki = (
            {item["p256_spki_sha256"] for item in native_authorities}
            | {item["route_signer_p256_spki_sha256"] for item in route_authorities}
            | set(self._trust.release_trust_spki_sha256s)
        )
        authority_ids: set[str] = set()
        authority_hashes: set[str] = set()
        key_ids: set[str] = set()
        spki_hashes: set[str] = set()
        normalized: list[Mapping[str, Any]] = []
        for index, raw_authority in enumerate(authorities):
            label = f"native_stop_challenge_authorities[{index}]"
            authority = _require_mapping(raw_authority, label)
            _require_exact(authority, _NATIVE_STOP_CHALLENGE_AUTHORITY_FIELDS, label)
            authority_id = authority.get("authority_id")
            key_id = authority.get("key_id")
            spki_hash = authority.get("p256_spki_sha256")
            if (
                not isinstance(authority_id, str)
                or not _LOWER_OPAQUE_ID.fullmatch(authority_id)
                or authority_id in authority_ids
                or not isinstance(key_id, str)
                or not _KEY_ID.fullmatch(key_id)
                or key_id in key_ids
                or key_id in forbidden_key_ids
                or not isinstance(spki_hash, str)
                or not _SHA256.fullmatch(spki_hash)
                or spki_hash in spki_hashes
                or spki_hash in forbidden_spki
            ):
                raise CandidateBomError(
                    "challenge authority id/key/SPKI is invalid, duplicated, or reused across roles"
                )
            for field in (
                "policy_artifact_sha256", "activation_token_sha256",
                "challenge_authority_sha256",
            ):
                if not isinstance(authority.get(field), str) or not _SHA256.fullmatch(authority[field]):
                    raise CandidateBomError(f"challenge authority {field} is invalid")
            if (
                authority.get("producer_module") != "policy-approval"
                or authority.get("policy_module_id") != "policy-approval"
                or authority.get("policy_artifact_id") != "dps.policy-approval"
                or not isinstance(authority.get("policy_version"), str)
                or not _WORKER_SEMVER.fullmatch(authority["policy_version"])
                or not isinstance(authority.get("policy_instance_id"), str)
                or not _POLICY_INSTANCE_ID.fullmatch(authority["policy_instance_id"])
                or authority.get("signature_algorithm") != "ECDSA_P256_SHA256"
                or authority.get("signature_format") != "IEEE_P1363_FIXED_FIELD_LOW_S"
                or authority.get("auth_scope") != "policy-approval:native-stop-challenge:v1:issue"
                or authority.get("native_stop_challenge_contract_id") != "native.stop.challenge/v1"
                or authority.get("policy_id") != "NATIVE-STOP-CHALLENGE-001"
            ):
                raise CandidateBomError(
                    "challenge authority Policy artifact, algorithm, format, scope, contract, or policy is invalid"
                )
            if any(
                isinstance(value, bool) or not isinstance(value, int)
                or not 1 <= value <= 9_223_372_036_854_775_807
                for value in (authority.get("policy_generation"), authority.get("rotation_epoch"))
            ):
                raise CandidateBomError("challenge authority generation or rotation epoch is invalid")
            if (
                authority.get("release_bom_generation") != generation
                or authority.get("activation_token_sha256") != activation_token
                or authority.get("revoked") is not False
            ):
                raise CandidateBomError("challenge authority BOM binding or revocation state is invalid")
            valid_from = _dotnet_utc_ticks(authority.get("valid_from"), f"{label} valid_from")
            valid_until = _dotnet_utc_ticks(authority.get("valid_until"), f"{label} valid_until")
            if valid_from >= valid_until or valid_until - valid_from > _MAX_RUNTIME_AUTHORITY_VALIDITY_TICKS:
                raise CandidateBomError("challenge authority validity is reversed or exceeds 31 days")
            expected_hash = _canonical_challenge_authority_hash(authority)
            if (
                not hmac.compare_digest(authority["challenge_authority_sha256"], expected_hash)
                or expected_hash in authority_hashes
            ):
                raise CandidateBomError("challenge authority hash is invalid or duplicated")
            authority_ids.add(authority_id)
            authority_hashes.add(expected_hash)
            key_ids.add(key_id)
            spki_hashes.add(spki_hash)
            normalized.append(authority)
        if normalized != sorted(
            normalized,
            key=lambda item: (
                item["policy_module_id"], item["policy_artifact_sha256"],
                item["policy_instance_id"], item["policy_generation"],
                item["rotation_epoch"], item["authority_id"],
            ),
        ):
            raise CandidateBomError("native stop challenge authorities are not in canonical order")
        return normalized

    def _validate_exact_shape(self, bom: Mapping[str, Any], expected_status: str = "SIGNED") -> None:
        _require_exact(bom, _BOM_FIELDS, "Release BOM")
        if bom.get("schema_version") != "dps.release-bom/v1" or bom.get("status") != expected_status:
            raise CandidateBomError(f"Release BOM must be dps.release-bom/v1 with status {expected_status}")
        if not isinstance(bom.get("bom_id"), str) or not _OPAQUE_ID.fullmatch(bom["bom_id"]):
            raise CandidateBomError("bom_id is invalid")
        if not isinstance(bom.get("integration_commit"), str) or not _COMMIT.fullmatch(bom["integration_commit"]):
            raise CandidateBomError("integration_commit is invalid")
        _require_utc_timestamp(bom.get("created_at"), "Release BOM created_at")
        native_authorities = self._validate_native_stop_authorities_shape(bom)
        route_authorities = self._validate_device_route_authorities_shape(
            bom, native_authorities
        )
        self._validate_native_stop_challenge_authorities_shape(
            bom, native_authorities, route_authorities
        )

        modules = list(_require_limited_sequence(bom.get("modules"), "modules", _MAX_MODULES))
        if not modules:
            raise CandidateBomError("Release BOM has no modules")
        module_ids: list[str] = []
        for index, raw_module in enumerate(modules):
            module = _require_mapping(raw_module, f"modules[{index}]")
            _require_exact(module, _MODULE_FIELDS, f"modules[{index}]")
            module_id = module.get("module_id")
            if not isinstance(module_id, str) or not _MODULE.fullmatch(module_id):
                raise CandidateBomError("module_id is invalid")
            if not isinstance(module.get("version"), str) or not _SEMVER.fullmatch(module["version"]):
                raise CandidateBomError("module version is invalid")
            for uri_field in ("artifact_uri", "descriptor_uri", "sbom_uri", "provenance_uri"):
                _require_bundle_uri(module.get(uri_field), f"module {module_id} {uri_field}")
            for field in (
                "sha256", "descriptor_sha256", "sbom_sha256", "provenance_sha256",
                "agents_sha256", "manifest_sha256",
            ):
                if not isinstance(module.get(field), str) or not _SHA256.fullmatch(module[field]):
                    raise CandidateBomError(f"module {module_id} {field} is invalid")
            self._validate_signature_shape(module.get("signature"), f"module {module_id} signature")
            module_ids.append(module_id)
        if len(set(module_ids)) != len(module_ids):
            raise CandidateBomError("Release BOM has duplicate modules")

        instructions = list(_require_limited_sequence(
            bom.get("instruction_hashes"), "instruction_hashes", _MAX_INSTRUCTION_HASHES
        ))
        if not instructions:
            raise CandidateBomError("instruction hashes are missing")
        instruction_paths: set[str] = set()
        for raw_instruction in instructions:
            instruction = _require_mapping(raw_instruction, "instruction hash")
            _require_exact(instruction, {"path", "sha256"}, "instruction hash")
            if (
                not isinstance(instruction.get("path"), str)
                or not instruction["path"]
                or len(instruction["path"].encode("utf-8")) > _MAX_URI_BYTES
                or "\\" in instruction["path"]
                or PurePosixPath(instruction["path"]).is_absolute()
                or "." in PurePosixPath(instruction["path"]).parts
                or ".." in PurePosixPath(instruction["path"]).parts
                or PurePosixPath(instruction["path"]).as_posix() != instruction["path"]
                or not _SHA256.fullmatch(str(instruction.get("sha256")))
            ):
                raise CandidateBomError("instruction hash entry is invalid")
            if instruction["path"] in instruction_paths:
                raise CandidateBomError("instruction hash paths are duplicated")
            instruction_paths.add(instruction["path"])

        contracts = list(_require_limited_sequence(bom.get("contracts"), "contracts", _MAX_CONTRACTS))
        contract_keys: set[tuple[str, int]] = set()
        for raw_contract in contracts:
            contract = _require_mapping(raw_contract, "contract")
            _require_exact(contract, {"contract_id", "major", "schema_sha256", "owner_module"}, "contract")
            key = (contract.get("contract_id"), contract.get("major"))
            if (
                not isinstance(key[0], str) or not key[0]
                or isinstance(key[1], bool) or not isinstance(key[1], int) or key[1] < 1
                or not isinstance(contract.get("schema_sha256"), str)
                or not _SHA256.fullmatch(contract["schema_sha256"])
                or contract.get("owner_module") not in module_ids
            ):
                raise CandidateBomError("contract entry is invalid")
            if key in contract_keys:
                raise CandidateBomError("contract owner/version is duplicated")
            contract_keys.add(key)

        if not isinstance(bom.get("database_versions"), Mapping) or len(bom["database_versions"]) > 512:
            raise CandidateBomError("database_versions must be an object")
        if any(
            not isinstance(key, str) or not key or len(key) > 128
            or not isinstance(value, str) or not value or len(value) > 256
            for key, value in bom["database_versions"].items()
        ):
            raise CandidateBomError("database_versions contains an invalid entry")
        for field in ("dependency_dag_sha256", "compatibility_matrix_sha256"):
            if not isinstance(bom.get(field), str) or not _SHA256.fullmatch(bom[field]):
                raise CandidateBomError(f"{field} is invalid")
        if (
            not isinstance(bom.get("feature_flags"), Mapping)
            or not isinstance(bom.get("kill_switches"), Mapping)
            or len(bom["feature_flags"]) > 512
            or len(bom["kill_switches"]) > 512
        ):
            raise CandidateBomError("feature flags and kill switches must be objects")
        if any(not isinstance(key, str) or not key or len(key) > 128 for key in (*bom["feature_flags"], *bom["kill_switches"])):
            raise CandidateBomError("feature flag or kill-switch name is invalid")
        if any(value is not None and not isinstance(value, (str, int, float, bool)) for value in bom["feature_flags"].values()):
            raise CandidateBomError("feature flag value is invalid")
        if not bom["kill_switches"] or any(value is not True for value in bom["kill_switches"].values()):
            raise CandidateBomError("every declared kill switch must be armed before release")

        toolchain = _require_mapping(bom.get("ai_toolchain"), "ai_toolchain")
        _require_exact(toolchain, {"models", "prompts", "tools"}, "ai_toolchain")
        if any(
            not isinstance(toolchain[field], Mapping)
            or not toolchain[field]
            or len(toolchain[field]) > 256
            or any(
                not isinstance(key, str) or not key or len(key) > 128
                or not isinstance(value, str) or not value or len(value) > 256
                for key, value in toolchain[field].items()
            )
            for field in toolchain
        ):
            raise CandidateBomError("AI toolchain versions must be explicit and non-empty")

        evidence = list(_require_limited_sequence(bom.get("evidence"), "evidence", _MAX_EVIDENCE))
        if not evidence:
            raise CandidateBomError("Release BOM has no evidence")
        evidence_ids: set[str] = set()
        for raw_item in evidence:
            item = _require_mapping(raw_item, "evidence")
            _require_exact(item, _EVIDENCE_FIELDS, "evidence")
            evidence_id = item.get("evidence_id")
            if not isinstance(evidence_id, str) or not _OPAQUE_ID.fullmatch(evidence_id) or evidence_id in evidence_ids:
                raise CandidateBomError("evidence_id is invalid or duplicated")
            evidence_ids.add(evidence_id)
            _require_bundle_uri(item.get("artifact_uri"), f"evidence {evidence_id}")
            if not isinstance(item.get("sha256"), str) or not _SHA256.fullmatch(item["sha256"]):
                raise CandidateBomError("evidence SHA-256 is invalid")
            if item.get("result") not in {"PASS", "FAIL", "SKIP", "PARTIAL", "NOT_RUN", "INFRA_ERROR", "NOT_APPLICABLE"}:
                raise CandidateBomError("evidence result is invalid")
            if not isinstance(item.get("required"), bool) or item.get("kind") not in _KIND_CEILING:
                raise CandidateBomError("evidence required/kind is invalid")
            if item.get("tested_commit") != bom["integration_commit"]:
                raise CandidateBomError("evidence was not produced for the integration commit")
            level = item.get("verification_level")
            if level not in _VERIFICATION_RANK or _VERIFICATION_RANK[level] > _KIND_CEILING[item["kind"]]:
                raise CandidateBomError("evidence exceeds its verification ceiling")
            if item["kind"] == "SIMULATION" and level != "INTEGRATION_VERIFIED":
                raise CandidateBomError("simulation evidence is capped at INTEGRATION_VERIFIED")
            if not isinstance(item.get("issuer_identity"), str) or not item["issuer_identity"]:
                raise CandidateBomError("evidence issuer is missing")
            self._validate_signature_shape(item.get("signature"), "evidence signature")

        risk = _require_mapping(bom.get("risk"), "risk")
        _require_exact(risk, {"tier", "scope_sha256", "requested_by"}, "risk")
        if risk.get("tier") not in {"R0", "R1", "R2", "R3", "R4"}:
            raise CandidateBomError("risk tier is invalid")
        if risk.get("tier") == "R4":
            raise CandidateBomError("R4 releases are always rejected")
        if not isinstance(risk.get("scope_sha256"), str) or not _SHA256.fullmatch(risk["scope_sha256"]):
            raise CandidateBomError("risk scope SHA-256 is invalid")
        if not isinstance(risk.get("requested_by"), str) or not _OPAQUE_ID.fullmatch(risk["requested_by"]):
            raise CandidateBomError("risk requester identity is missing")

        approval = _require_mapping(bom.get("release_approval"), "release_approval")
        _require_exact(approval, _APPROVAL_FIELDS, "release_approval")
        if not isinstance(approval.get("required"), bool):
            raise CandidateBomError("release_approval.required must be boolean")
        if approval.get("signature") is not None:
            self._validate_signature_shape(approval["signature"], "release approval signature")
        if approval.get("receipt_uri") is not None:
            _require_bundle_uri(approval["receipt_uri"], "release approval receipt")

        rollout = _require_mapping(bom.get("rollout"), "rollout")
        _require_exact(rollout, {"waves", "shadow_artifact_sha256", "current_wave"}, "rollout")
        waves = list(_require_limited_sequence(rollout.get("waves"), "rollout waves", 256))
        if (
            not waves
            or len(set(waves)) != len(waves)
            or any(not isinstance(wave, str) or not wave or len(wave) > 128 for wave in waves)
            or not isinstance(rollout.get("current_wave"), str)
            or not rollout["current_wave"]
            or len(rollout["current_wave"]) > 128
            or not isinstance(rollout.get("shadow_artifact_sha256"), str)
            or not _SHA256.fullmatch(rollout["shadow_artifact_sha256"])
        ):
            raise CandidateBomError("rollout waves or shadow digest is invalid")
        rollback = _require_mapping(bom.get("rollback"), "rollback")
        _require_exact(rollback, {"unit", "target_minutes", "procedure", "compensation_required"}, "rollback")
        if (
            isinstance(rollback.get("target_minutes"), bool)
            or not isinstance(rollback.get("target_minutes"), int)
            or not 1 <= rollback["target_minutes"] <= 5
        ):
            raise CandidateBomError("ordinary rollback target must be within five minutes")
        if (
            not isinstance(rollback.get("unit"), str)
            or not rollback["unit"]
            or len(rollback["unit"]) > 128
            or not isinstance(rollback.get("procedure"), str)
            or not rollback["procedure"]
            or len(rollback["procedure"]) > 512
            or not isinstance(rollback.get("compensation_required"), bool)
        ):
            raise CandidateBomError("rollback procedure is missing")
        self._validate_signature_shape(bom.get("signature"), "Release BOM signature")

    @staticmethod
    def _topological_waves(nodes: list[str], edges: list[dict[str, str]]) -> list[list[str]]:
        remaining = set(nodes)
        providers_by_consumer = {node: set() for node in nodes}
        for edge in edges:
            providers_by_consumer[edge["consumer"]].add(edge["provider"])
        waves: list[list[str]] = []
        while remaining:
            wave = sorted(
                node for node in remaining
                if not (providers_by_consumer[node] & remaining)
            )
            if not wave:
                raise CandidateBomError("module dependency graph contains a cycle")
            waves.append(wave)
            remaining.difference_update(wave)
        return waves

    def _validate_repository_bindings(self, bom: Mapping[str, Any]) -> None:
        commit = bom["integration_commit"]
        module_manifest_contracts: dict[tuple[str, int], tuple[str, str]] = {}
        manifest_contract_sources: dict[tuple[str, int], str] = {}
        manifest_contract_declarations: dict[str, dict[str, list[Mapping[str, Any]]]] = {}
        manifest_dependencies: dict[str, list[Mapping[str, Any]]] = {}
        required_instruction_paths = {"AGENTS.md"}
        for module in bom["modules"]:
            module_id = module["module_id"]
            agents_path = f"Modules/{module_id}/AGENTS.md"
            manifest_path = f"Modules/{module_id}/module.yaml"
            agents_bytes = self._git.read(commit, agents_path)
            manifest_bytes = self._git.read(commit, manifest_path)
            if sha256_bytes(agents_bytes) != module["agents_sha256"]:
                raise CandidateBomError(f"module {module_id} AGENTS hash mismatch")
            if sha256_bytes(manifest_bytes) != module["manifest_sha256"]:
                raise CandidateBomError(f"module {module_id} Manifest hash mismatch")
            required_instruction_paths.add(agents_path)
            manifest = _require_mapping(
                _strict_json_loads(manifest_bytes, f"module {module_id} Manifest"),
                f"module {module_id} Manifest",
            )
            module_declaration = _require_mapping(manifest.get("module"), f"module {module_id} identity")
            if (
                module_declaration.get("id") != module_id
                or module_declaration.get("version") != module["version"]
            ):
                raise CandidateBomError(f"module {module_id} Manifest identity mismatch")
            contracts_declaration = _require_mapping(
                manifest.get("contracts"), f"module {module_id} contracts"
            )
            provided = list(_require_limited_sequence(
                contracts_declaration.get("provided"),
                f"module {module_id} provided contracts",
                _MAX_MANIFEST_CONTRACTS,
            ))
            consumed = list(_require_limited_sequence(
                contracts_declaration.get("consumed"),
                f"module {module_id} consumed contracts",
                _MAX_MANIFEST_CONTRACTS,
            ))
            manifest_contract_declarations[module_id] = {
                "provided": provided,
                "consumed": consumed,
            }
            for contract in provided:
                contract = _require_mapping(contract, "Manifest provided contract declaration")
                _require_exact(
                    contract,
                    {"contractId", "major", "source", "status", "ownerModule"},
                    "Manifest provided contract declaration",
                )
                contract_id = contract.get("contractId")
                major = contract.get("major")
                source = contract.get("source")
                owner = contract.get("ownerModule")
                if (
                    not isinstance(contract_id, str) or not contract_id
                    or isinstance(major, bool) or not isinstance(major, int) or major < 1
                    or not isinstance(source, str) or owner != module_id
                    or not source.startswith(f"Modules/{module_id}/contracts/")
                ):
                    raise CandidateBomError("Manifest contract declaration is invalid")
                source_path = PurePosixPath(source)
                if (
                    source_path.is_absolute()
                    or "." in source_path.parts
                    or ".." in source_path.parts
                    or source_path.as_posix() != source
                    or len(source.encode("utf-8")) > _MAX_URI_BYTES
                ):
                    raise CandidateBomError("Manifest contract source path is invalid")
                key = (contract_id, major)
                if key in module_manifest_contracts:
                    raise CandidateBomError("multiple module owners declared for a contract")
                schema_bytes = self._git.read(commit, source, _MAX_METADATA_JSON_BYTES)
                _require_mapping(
                    _strict_json_loads(schema_bytes, f"contract {contract_id}/v{major} schema"),
                    f"contract {contract_id}/v{major} schema",
                )
                module_manifest_contracts[key] = (module_id, sha256_bytes(schema_bytes))
                manifest_contract_sources[key] = source

            dependencies = list(_require_limited_sequence(
                manifest.get("dependencies"),
                f"module {module_id} dependencies",
                _MAX_MANIFEST_DEPENDENCIES,
            ))
            normalized_dependencies: list[Mapping[str, Any]] = []
            dependency_ids: set[str] = set()
            for raw_dependency in dependencies:
                dependency = _require_mapping(raw_dependency, "Manifest dependency")
                _require_exact(
                    dependency,
                    {"moduleId", "versionRange", "required", "reason"},
                    "Manifest dependency",
                )
                provider = dependency.get("moduleId")
                if (
                    not isinstance(provider, str)
                    or not _MODULE.fullmatch(provider)
                    or provider == module_id
                    or provider in dependency_ids
                    or not isinstance(dependency.get("versionRange"), str)
                    or not dependency["versionRange"]
                    or len(dependency["versionRange"]) > 128
                    or not isinstance(dependency.get("required"), bool)
                    or not isinstance(dependency.get("reason"), str)
                    or not dependency["reason"]
                    or len(dependency["reason"]) > 512
                ):
                    raise CandidateBomError("Manifest dependency declaration is invalid")
                dependency_ids.add(provider)
                normalized_dependencies.append(dependency)
            manifest_dependencies[module_id] = normalized_dependencies

            for raw_consumed in consumed:
                consumed_contract = _require_mapping(
                    raw_consumed, "Manifest consumed contract declaration"
                )
                _require_exact(
                    consumed_contract,
                    {"contractId", "major", "source", "status", "ownerModule"},
                    "Manifest consumed contract declaration",
                )
                if (
                    not isinstance(consumed_contract.get("contractId"), str)
                    or not consumed_contract["contractId"]
                    or isinstance(consumed_contract.get("major"), bool)
                    or not isinstance(consumed_contract.get("major"), int)
                    or consumed_contract["major"] < 1
                    or not isinstance(consumed_contract.get("ownerModule"), str)
                    or not _MODULE.fullmatch(consumed_contract["ownerModule"])
                    or not isinstance(consumed_contract.get("source"), str)
                    or not consumed_contract["source"]
                ):
                    raise CandidateBomError("Manifest consumed contract declaration is invalid")

        root_agents_sha = sha256_bytes(self._git.read(commit, "AGENTS.md"))
        instruction_map = {item["path"]: item["sha256"] for item in bom["instruction_hashes"]}
        if instruction_map.get("AGENTS.md") != root_agents_sha:
            raise CandidateBomError("root AGENTS instruction hash mismatch")
        for path in required_instruction_paths:
            if path not in instruction_map:
                raise CandidateBomError(f"required instruction hash is missing: {path}")
        for path, expected in instruction_map.items():
            if sha256_bytes(self._git.read(commit, path)) != expected:
                raise CandidateBomError(f"instruction hash mismatch: {path}")

        bom_contracts = {
            (item["contract_id"], item["major"]): (item["owner_module"], item["schema_sha256"])
            for item in bom["contracts"]
        }
        if bom_contracts != module_manifest_contracts:
            raise CandidateBomError("Release BOM contract inventory differs from module Manifests")
        dag_bytes = self._git.read(commit, "governance/modules/dependency-graph.yaml")
        compatibility_bytes = self._git.read(commit, "governance/modules/compatibility.yaml")
        if sha256_bytes(dag_bytes) != bom["dependency_dag_sha256"]:
            raise CandidateBomError("dependency DAG hash mismatch")
        if sha256_bytes(compatibility_bytes) != bom["compatibility_matrix_sha256"]:
            raise CandidateBomError("compatibility matrix hash mismatch")

        module_ids = sorted(manifest_dependencies)
        module_id_set = set(module_ids)
        module_versions = {
            str(module["module_id"]): str(module["version"])
            for module in bom["modules"]
        }
        expected_edges: list[dict[str, str]] = []
        for consumer, dependencies in manifest_dependencies.items():
            for dependency in dependencies:
                provider = str(dependency["moduleId"])
                if provider not in module_id_set:
                    raise CandidateBomError("Manifest dependency provider is absent from the Release BOM")
                if not _version_satisfies_range(
                    module_versions[provider], str(dependency["versionRange"])
                ):
                    raise CandidateBomError("Release BOM module version violates a dependency range")
                expected_edges.append({
                    "consumer": consumer,
                    "provider": provider,
                    "reason": str(dependency["reason"]),
                })
        expected_edges.sort(key=lambda value: (value["consumer"], value["provider"]))

        dag = _require_mapping(
            _strict_json_loads(dag_bytes, "dependency DAG"), "dependency DAG"
        )
        _require_exact(
            dag,
            {"schemaVersion", "generatedFrom", "failOnCycle", "nodes", "edges", "parallelWaves"},
            "dependency DAG",
        )
        raw_nodes = list(_require_limited_sequence(dag.get("nodes"), "dependency DAG nodes", _MAX_MODULES))
        if (
            dag.get("schemaVersion") != "dps.dependency-graph/v1"
            or dag.get("generatedFrom") != "Modules/*/module.yaml"
            or dag.get("failOnCycle") is not True
            or raw_nodes != module_ids
            or len(set(raw_nodes)) != len(raw_nodes)
        ):
            raise CandidateBomError("dependency DAG nodes do not match module Manifests")
        raw_edges = list(_require_limited_sequence(
            dag.get("edges"), "dependency DAG edges", _MAX_MODULES * _MAX_MANIFEST_DEPENDENCIES
        ))
        normalized_edges: list[dict[str, str]] = []
        for raw_edge in raw_edges:
            edge = _require_mapping(raw_edge, "dependency DAG edge")
            _require_exact(edge, {"consumer", "provider", "reason"}, "dependency DAG edge")
            if any(not isinstance(edge.get(field), str) or not edge[field] for field in ("consumer", "provider", "reason")):
                raise CandidateBomError("dependency DAG edge is invalid")
            normalized_edges.append(dict(edge))
        if normalized_edges != expected_edges:
            raise CandidateBomError("dependency DAG edges differ from module Manifests")
        expected_waves = self._topological_waves(module_ids, expected_edges)
        raw_waves = list(_require_limited_sequence(dag.get("parallelWaves"), "parallel waves", _MAX_MODULES))
        normalized_waves = [
            list(_require_limited_sequence(wave, "parallel wave", _MAX_MODULES))
            for wave in raw_waves
        ]
        if normalized_waves != expected_waves:
            raise CandidateBomError("dependency DAG parallel waves differ from module Manifests")

        owners_by_contract: dict[str, tuple[str, int]] = {}
        consumers_by_contract: dict[str, set[str]] = {}
        for (contract_id, major), (owner, _) in module_manifest_contracts.items():
            prior = owners_by_contract.get(contract_id)
            if prior is not None and prior[0] != owner:
                raise CandidateBomError("one contract id has multiple module owners")
            if prior is None or major > prior[1]:
                owners_by_contract[contract_id] = (owner, major)
            consumers_by_contract.setdefault(contract_id, set())
        for consumer, declarations in manifest_contract_declarations.items():
            for contract in declarations["consumed"]:
                contract_id = str(contract["contractId"])
                owner = owners_by_contract.get(contract_id)
                if owner is None or contract["ownerModule"] != owner[0]:
                    raise CandidateBomError("consumed contract has an unknown or mismatched owner")
                supported_majors = {owner[1]}
                if owner[1] > 1:
                    supported_majors.add(owner[1] - 1)
                if contract["major"] not in supported_majors:
                    raise CandidateBomError("consumed contract is outside the N/N-1 window")
                declared_key = (contract_id, contract["major"])
                declared_provider = module_manifest_contracts.get(declared_key)
                if (
                    declared_provider is None
                    or declared_provider[0] != contract["ownerModule"]
                    or manifest_contract_sources[declared_key] != contract["source"]
                ):
                    raise CandidateBomError("consumed contract does not bind the exact provided schema")
                consumers_by_contract[contract_id].add(consumer)
        expected_contract_matrix = [
            {
                "contractId": contract_id,
                "owner": owner,
                "currentMajor": major,
                "consumers": sorted(consumers_by_contract[contract_id]),
            }
            for contract_id, (owner, major) in sorted(owners_by_contract.items())
        ]
        compatibility = _require_mapping(
            _strict_json_loads(compatibility_bytes, "compatibility matrix"),
            "compatibility matrix",
        )
        _require_exact(
            compatibility,
            {
                "schemaVersion", "generatedFrom", "policyRef", "unknownMajorBehavior",
                "contracts", "requiredCombinations", "unknownNPlus1",
            },
            "compatibility matrix",
        )
        matrix_contracts = list(_require_limited_sequence(
            compatibility.get("contracts"), "compatibility contracts", _MAX_CONTRACTS
        ))
        normalized_matrix: list[dict[str, Any]] = []
        for raw_contract in matrix_contracts:
            contract = _require_mapping(raw_contract, "compatibility contract")
            _require_exact(
                contract, {"contractId", "owner", "currentMajor", "consumers"},
                "compatibility contract",
            )
            consumers = list(_require_limited_sequence(
                contract.get("consumers"), "compatibility consumers", _MAX_MODULES
            ))
            normalized_matrix.append({
                "contractId": contract.get("contractId"),
                "owner": contract.get("owner"),
                "currentMajor": contract.get("currentMajor"),
                "consumers": consumers,
            })
        if (
            compatibility.get("schemaVersion") != "dps.compatibility-matrix/v1"
            or compatibility.get("generatedFrom") != "Modules/*/module.yaml"
            or compatibility.get("policyRef") != "governance/policies/compatibility-policy.yaml"
            or compatibility.get("unknownMajorBehavior") != "reject"
            or compatibility.get("requiredCombinations") != ["N/N", "N/N-1", "N-1/N", "N-1/N-1"]
            or compatibility.get("unknownNPlus1") != "REJECT"
            or normalized_matrix != expected_contract_matrix
        ):
            raise CandidateBomError("compatibility matrix differs from module Manifests or N/N-1 policy")

    def _validate_descriptor(
        self,
        descriptor: Mapping[str, Any],
        module: Mapping[str, Any],
        bom: Mapping[str, Any],
        artifact_size: int,
    ) -> None:
        module_id = str(module["module_id"])
        _require_exact(descriptor, _DESCRIPTOR_FIELDS, f"module {module_id} descriptor")
        nullable_ids = (
            ("soul_id", _SOUL_ID),
            ("device_binding_id", _DEVICE_BINDING_ID),
            ("platform_account_id", _PLATFORM_ACCOUNT_ID),
        )
        for field, pattern in nullable_ids:
            value = descriptor.get(field)
            if value is not None and (not isinstance(value, str) or not pattern.fullmatch(value)):
                raise CandidateBomError(f"module {module_id} descriptor {field} is invalid")
        if (
            descriptor.get("schema_version") != "1.0.0"
            or descriptor.get("contract_id") != "artifact.descriptor/v1"
            or descriptor.get("producer_module") != "factory-artifact-builder"
            or descriptor.get("module_id") != module_id
            or descriptor.get("module_version") != module["version"]
            or descriptor.get("integration_commit") != bom["integration_commit"]
            or descriptor.get("artifact_uri") != f"sha256:{module['sha256']}"
            or descriptor.get("artifact_file") != PurePosixPath(str(module["artifact_uri"])).name
            or descriptor.get("artifact_sha256") != module["sha256"]
            or descriptor.get("size_bytes") != artifact_size
            or descriptor.get("agents_sha256") != module["agents_sha256"]
            or descriptor.get("manifest_sha256") != module["manifest_sha256"]
        ):
            raise CandidateBomError(f"module {module_id} descriptor linkage mismatch")
        if (
            not isinstance(descriptor.get("trace_id"), str)
            or not _TRACE_ID.fullmatch(descriptor["trace_id"])
            or not isinstance(descriptor.get("idempotency_key"), str)
            or not _IDEMPOTENCY_KEY.fullmatch(descriptor["idempotency_key"])
            or descriptor.get("privacy_class") != "internal"
            or not isinstance(descriptor.get("artifact_id"), str)
            or not re.fullmatch(r"artifact-[0-9a-f]{32}", descriptor["artifact_id"])
            or not isinstance(descriptor.get("build_id"), str)
            or not _OPAQUE_ID.fullmatch(descriptor["build_id"])
            or not isinstance(descriptor.get("merge_decision_id"), str)
            or not re.fullmatch(r"merge-[0-9a-f]{32}", descriptor["merge_decision_id"])
        ):
            raise CandidateBomError(f"module {module_id} descriptor identity is invalid")
        _require_utc_timestamp(descriptor.get("occurred_at"), f"module {module_id} descriptor occurred_at")
        for field in ("trusted_merge_policy_sha256", "source_tree_sha256"):
            if not isinstance(descriptor.get(field), str) or not _SHA256.fullmatch(descriptor[field]):
                raise CandidateBomError(f"module {module_id} descriptor {field} is invalid")
        for field, uri_field, expected_sha in (
            ("sbom", "sbom_uri", module["sbom_sha256"]),
            ("provenance", "provenance_uri", module["provenance_sha256"]),
        ):
            metadata = _require_mapping(descriptor.get(field), f"module {module_id} descriptor {field}")
            _require_exact(metadata, _DESCRIPTOR_METADATA_FIELDS, f"module {module_id} descriptor {field}")
            if (
                metadata.get("path") != PurePosixPath(str(module[uri_field])).name
                or metadata.get("sha256") != expected_sha
                or metadata.get("media_type") != "application/json"
            ):
                raise CandidateBomError(f"module {module_id} descriptor/{field} mismatch")
        unsigned = _require_mapping(descriptor.get("signature"), f"module {module_id} descriptor signature")
        _require_exact(unsigned, _DESCRIPTOR_SIGNATURE_FIELDS, f"module {module_id} descriptor signature")
        if (
            unsigned.get("status") != "UNSIGNED_AWAITING_EXTERNAL_SIGNER"
            or unsigned.get("signer_required") != "external-controlled-signer"
        ):
            raise CandidateBomError(f"module {module_id} builder descriptor signature state is invalid")

    def _validate_sbom(
        self,
        sbom: Mapping[str, Any],
        descriptor: Mapping[str, Any],
        module: Mapping[str, Any],
    ) -> None:
        module_id = str(module["module_id"])
        _require_exact(sbom, _SBOM_FIELDS, f"module {module_id} SBOM")
        creation = _require_mapping(sbom.get("creationInfo"), f"module {module_id} SBOM creationInfo")
        _require_exact(creation, {"created", "creators"}, f"module {module_id} SBOM creationInfo")
        creators = list(_require_limited_sequence(
            creation.get("creators"), f"module {module_id} SBOM creators", 32
        ))
        if (
            sbom.get("spdxVersion") != "SPDX-2.3"
            or sbom.get("dataLicense") != "CC0-1.0"
            or sbom.get("SPDXID") != "SPDXRef-DOCUMENT"
            or sbom.get("name") != descriptor["artifact_file"]
            or sbom.get("documentNamespace") != f"https://dps.local/spdx/{module['sha256']}"
            or not creators
            or any(not isinstance(creator, str) or not creator or len(creator) > 256 for creator in creators)
        ):
            raise CandidateBomError(f"module {module_id} SBOM document identity is invalid")
        _require_utc_timestamp(creation.get("created"), f"module {module_id} SBOM created")

        packages = list(_require_limited_sequence(sbom.get("packages"), f"module {module_id} SBOM packages", 256))
        if not packages:
            raise CandidateBomError(f"module {module_id} SBOM has no packages")
        artifact_digest_found = False
        for raw_package in packages:
            package = _require_mapping(raw_package, f"module {module_id} SBOM package")
            _require_exact(
                package,
                {"name", "SPDXID", "versionInfo", "downloadLocation", "filesAnalyzed", "checksums"},
                f"module {module_id} SBOM package",
            )
            checksums = list(_require_limited_sequence(
                package.get("checksums"), f"module {module_id} SBOM package checksums", 32
            ))
            for raw_checksum in checksums:
                checksum = _require_mapping(raw_checksum, f"module {module_id} SBOM checksum")
                _require_exact(checksum, {"algorithm", "checksumValue"}, f"module {module_id} SBOM checksum")
                if checksum.get("algorithm") != "SHA256" or not isinstance(checksum.get("checksumValue"), str) or not _SHA256.fullmatch(checksum["checksumValue"]):
                    raise CandidateBomError(f"module {module_id} SBOM checksum is invalid")
                artifact_digest_found |= checksum["checksumValue"] == module["sha256"]
            if (
                not isinstance(package.get("name"), str)
                or not package["name"]
                or not isinstance(package.get("SPDXID"), str)
                or not package["SPDXID"].startswith("SPDXRef-")
                or not isinstance(package.get("versionInfo"), str)
                or not isinstance(package.get("downloadLocation"), str)
                or not isinstance(package.get("filesAnalyzed"), bool)
            ):
                raise CandidateBomError(f"module {module_id} SBOM package is invalid")
        if not artifact_digest_found:
            raise CandidateBomError(f"module {module_id} SBOM does not describe artifact digest")

        files = list(_require_limited_sequence(sbom.get("files"), f"module {module_id} SBOM files", _MAX_SBOM_FILES))
        seen_files: set[str] = set()
        seen_spdx: set[str] = set()
        for raw_file in files:
            file_entry = _require_mapping(raw_file, f"module {module_id} SBOM file")
            _require_exact(file_entry, {"fileName", "SPDXID", "checksums"}, f"module {module_id} SBOM file")
            file_name = file_entry.get("fileName")
            spdx_id = file_entry.get("SPDXID")
            if (
                not isinstance(file_name, str)
                or not file_name
                or len(file_name.encode("utf-8")) > _MAX_URI_BYTES
                or file_name in seen_files
                or not isinstance(spdx_id, str)
                or not spdx_id.startswith("SPDXRef-File-")
                or spdx_id in seen_spdx
            ):
                raise CandidateBomError(f"module {module_id} SBOM file is invalid or duplicated")
            seen_files.add(file_name)
            seen_spdx.add(spdx_id)
            checksums = list(_require_limited_sequence(
                file_entry.get("checksums"), f"module {module_id} SBOM file checksums", 32
            ))
            if not checksums:
                raise CandidateBomError(f"module {module_id} SBOM file checksum is missing")
            for raw_checksum in checksums:
                checksum = _require_mapping(raw_checksum, f"module {module_id} SBOM file checksum")
                _require_exact(checksum, {"algorithm", "checksumValue"}, f"module {module_id} SBOM file checksum")
                if checksum.get("algorithm") != "SHA256" or not isinstance(checksum.get("checksumValue"), str) or not _SHA256.fullmatch(checksum["checksumValue"]):
                    raise CandidateBomError(f"module {module_id} SBOM file checksum is invalid")

        relationships = list(_require_limited_sequence(
            sbom.get("relationships"), f"module {module_id} SBOM relationships", 1_024
        ))
        for raw_relationship in relationships:
            relationship = _require_mapping(raw_relationship, f"module {module_id} SBOM relationship")
            _require_exact(
                relationship,
                {"spdxElementId", "relationshipType", "relatedSpdxElement"},
                f"module {module_id} SBOM relationship",
            )
            if any(not isinstance(relationship.get(field), str) or not relationship[field] for field in relationship):
                raise CandidateBomError(f"module {module_id} SBOM relationship is invalid")

    def _validate_provenance(
        self,
        provenance: Mapping[str, Any],
        descriptor: Mapping[str, Any],
        module: Mapping[str, Any],
        bom: Mapping[str, Any],
    ) -> None:
        module_id = str(module["module_id"])
        _require_exact(provenance, _PROVENANCE_FIELDS, f"module {module_id} provenance")
        subjects = list(_require_limited_sequence(
            provenance.get("subject"), f"module {module_id} provenance subjects", 64
        ))
        if not subjects:
            raise CandidateBomError(f"module {module_id} provenance subject is missing")
        subject_match = False
        for raw_subject in subjects:
            subject = _require_mapping(raw_subject, f"module {module_id} provenance subject")
            _require_exact(subject, {"name", "digest"}, f"module {module_id} provenance subject")
            digest = _require_mapping(subject.get("digest"), f"module {module_id} provenance digest")
            _require_exact(digest, {"sha256"}, f"module {module_id} provenance digest")
            if not isinstance(digest.get("sha256"), str) or not _SHA256.fullmatch(digest["sha256"]):
                raise CandidateBomError(f"module {module_id} provenance digest is invalid")
            subject_match |= subject.get("name") == descriptor["artifact_file"] and digest["sha256"] == module["sha256"]
        if not subject_match:
            raise CandidateBomError(f"module {module_id} provenance subject mismatch")
        predicate = _require_mapping(provenance.get("predicate"), f"module {module_id} provenance predicate")
        _require_exact(predicate, {"buildDefinition", "runDetails"}, f"module {module_id} provenance predicate")
        definition = _require_mapping(predicate.get("buildDefinition"), f"module {module_id} buildDefinition")
        _require_exact(
            definition,
            {"buildType", "externalParameters", "internalParameters", "resolvedDependencies"},
            f"module {module_id} buildDefinition",
        )
        external = _require_mapping(definition.get("externalParameters"), f"module {module_id} externalParameters")
        _require_exact(
            external,
            {"module_id", "module_version", "integration_commit", "source_tree_sha256"},
            f"module {module_id} externalParameters",
        )
        internal = _require_mapping(definition.get("internalParameters"), f"module {module_id} internalParameters")
        _require_exact(internal, {"merge_decision_id"}, f"module {module_id} internalParameters")
        if (
            provenance.get("_type") != "https://in-toto.io/Statement/v1"
            or provenance.get("predicateType") != "https://slsa.dev/provenance/v1"
            or definition.get("buildType") != "https://dps.local/build/module-artifact/v1"
            or external != {
                "module_id": module_id,
                "module_version": module["version"],
                "integration_commit": bom["integration_commit"],
                "source_tree_sha256": descriptor["source_tree_sha256"],
            }
            or internal.get("merge_decision_id") != descriptor["merge_decision_id"]
        ):
            raise CandidateBomError(f"module {module_id} provenance build linkage mismatch")
        dependencies = list(_require_limited_sequence(
            definition.get("resolvedDependencies"),
            f"module {module_id} resolved dependencies",
            _MAX_SBOM_FILES,
        ))
        normalized_dependencies: list[dict[str, Any]] = []
        seen_paths: set[str] = set()
        total_size = 0
        for raw_dependency in dependencies:
            dependency = _require_mapping(raw_dependency, f"module {module_id} resolved dependency")
            _require_exact(
                dependency, {"path", "sha256", "size_bytes", "mode"},
                f"module {module_id} resolved dependency",
            )
            path = dependency.get("path")
            size_bytes = dependency.get("size_bytes")
            if (
                not isinstance(path, str)
                or not path
                or len(path.encode("utf-8")) > _MAX_URI_BYTES
                or path in seen_paths
                or not isinstance(dependency.get("sha256"), str)
                or not _SHA256.fullmatch(dependency["sha256"])
                or isinstance(size_bytes, bool)
                or not isinstance(size_bytes, int)
                or size_bytes < 0
                or dependency.get("mode") not in {"100644", "100755"}
            ):
                raise CandidateBomError(f"module {module_id} resolved dependency is invalid")
            seen_paths.add(path)
            total_size += size_bytes
            if total_size > _MAX_ARTIFACT_BYTES:
                raise CandidateBomError(f"module {module_id} resolved dependencies exceed the byte limit")
            normalized_dependencies.append(dict(dependency))
        if normalized_dependencies != sorted(normalized_dependencies, key=lambda item: item["path"]):
            raise CandidateBomError(f"module {module_id} resolved dependencies are not canonical")
        if sha256_bytes(canonical_bytes(normalized_dependencies)) != descriptor["source_tree_sha256"]:
            raise CandidateBomError(f"module {module_id} source-tree provenance digest mismatch")
        dependency_by_path = {item["path"]: item for item in normalized_dependencies}
        if (
            dependency_by_path.get(f"Modules/{module_id}/AGENTS.md", {}).get("sha256") != module["agents_sha256"]
            or dependency_by_path.get(f"Modules/{module_id}/module.yaml", {}).get("sha256") != module["manifest_sha256"]
        ):
            raise CandidateBomError(f"module {module_id} provenance omits governed module inputs")
        run_details = _require_mapping(predicate.get("runDetails"), f"module {module_id} runDetails")
        _require_exact(run_details, {"builder", "metadata"}, f"module {module_id} runDetails")
        builder = _require_mapping(run_details.get("builder"), f"module {module_id} builder")
        metadata = _require_mapping(run_details.get("metadata"), f"module {module_id} run metadata")
        _require_exact(builder, {"id"}, f"module {module_id} builder")
        _require_exact(metadata, {"invocationId", "startedOn", "finishedOn"}, f"module {module_id} run metadata")
        if (
            builder.get("id") != "dps:factory-artifact-builder:0.1.0"
            or metadata.get("invocationId") != descriptor["build_id"]
        ):
            raise CandidateBomError(f"module {module_id} provenance run identity mismatch")
        _require_utc_timestamp(metadata.get("startedOn"), f"module {module_id} provenance startedOn")
        _require_utc_timestamp(metadata.get("finishedOn"), f"module {module_id} provenance finishedOn")

    def _validate_module_artifacts(self, bom: Mapping[str, Any]) -> list[str]:
        artifact_signers: list[str] = []
        for module in bom["modules"]:
            module_id = module["module_id"]
            _, artifact_sha256, artifact_size = _hash_bundle_file(
                self._bundle, module["artifact_uri"], f"{module_id} artifact"
            )
            if artifact_sha256 != module["sha256"]:
                raise CandidateBomError(f"module {module_id} artifact hash mismatch")
            signed_entry = {key: value for key, value in module.items() if key != "signature"}
            signer = self._trust.verify_signature(
                module["signature"],
                b"dps-module-artifact-bom-entry/v1\n" + canonical_bytes(signed_entry),
                "artifact",
            )
            artifact_signers.append(signer)

            _, descriptor_bytes = _read_bundle_file(
                self._bundle, module["descriptor_uri"], f"{module_id} descriptor"
            )
            _, sbom_bytes = _read_bundle_file(
                self._bundle, module["sbom_uri"], f"{module_id} SBOM", _MAX_METADATA_JSON_BYTES
            )
            _, provenance_bytes = _read_bundle_file(
                self._bundle, module["provenance_uri"], f"{module_id} provenance", _MAX_METADATA_JSON_BYTES
            )
            for label, value, expected in (
                ("descriptor", descriptor_bytes, module["descriptor_sha256"]),
                ("SBOM", sbom_bytes, module["sbom_sha256"]),
                ("provenance", provenance_bytes, module["provenance_sha256"]),
            ):
                if sha256_bytes(value) != expected:
                    raise CandidateBomError(f"module {module_id} {label} hash mismatch")
            descriptor = _require_mapping(
                _strict_json_loads(descriptor_bytes, f"module {module_id} descriptor"),
                f"module {module_id} descriptor",
            )
            sbom = _require_mapping(
                _strict_json_loads(sbom_bytes, f"module {module_id} SBOM"),
                f"module {module_id} SBOM",
            )
            provenance = _require_mapping(
                _strict_json_loads(provenance_bytes, f"module {module_id} provenance"),
                f"module {module_id} provenance",
            )
            self._validate_descriptor(descriptor, module, bom, artifact_size)
            self._validate_sbom(sbom, descriptor, module)
            self._validate_provenance(provenance, descriptor, module, bom)
        return artifact_signers

    def _validate_native_stop_authority_bindings(self, bom: Mapping[str, Any]) -> None:
        """Bind externally authorized runtime keys to commit/artifact truth.

        Git and the artifact descriptor can prove only the Worker module,
        stable artifact identifier, version, process boundary, and exact digest.
        Worker incarnation and P-256 key facts remain explicit signed deployment
        authorization input; this method deliberately does not claim to derive
        them from source control.
        """
        module_by_id = {module["module_id"]: module for module in bom["modules"]}
        worker = module_by_id.get("windows-edge-worker")
        if worker is None:
            raise CandidateBomError("Release BOM is missing the windows-edge-worker artifact")
        manifest_bytes = self._git.read(
            bom["integration_commit"], "Modules/windows-edge-worker/module.yaml"
        )
        manifest = _require_mapping(
            _strict_json_loads(manifest_bytes, "windows-edge-worker Manifest"),
            "windows-edge-worker Manifest",
        )
        artifacts = list(_require_limited_sequence(
            manifest.get("artifacts"), "windows-edge-worker artifacts", 64
        ))
        matching_artifacts: list[Mapping[str, Any]] = []
        for raw_artifact in artifacts:
            artifact = _require_mapping(raw_artifact, "windows-edge-worker artifact declaration")
            _require_exact(
                artifact,
                {"id", "kind", "status", "build", "versioning"},
                "windows-edge-worker artifact declaration",
            )
            if artifact.get("id") == "dps.windows-edge-worker":
                matching_artifacts.append(artifact)
        runtime = _require_mapping(manifest.get("runtime"), "windows-edge-worker runtime")
        if (
            len(matching_artifacts) != 1
            or matching_artifacts[0].get("kind") != "service"
            or matching_artifacts[0].get("status") not in {"proposed", "buildable"}
            or matching_artifacts[0].get("versioning") != "semver"
            or runtime.get("processBoundary") != "out-of-process"
        ):
            raise CandidateBomError("native stop authority does not identify the governed Worker service")

        _, descriptor_bytes = _read_bundle_file(
            self._bundle, worker["descriptor_uri"], "windows-edge-worker descriptor"
        )
        descriptor = _require_mapping(
            _strict_json_loads(descriptor_bytes, "windows-edge-worker descriptor"),
            "windows-edge-worker descriptor",
        )
        if (
            descriptor.get("module_id") != "windows-edge-worker"
            or descriptor.get("module_version") != worker["version"]
            or descriptor.get("artifact_sha256") != worker["sha256"]
            or descriptor.get("integration_commit") != bom["integration_commit"]
        ):
            raise CandidateBomError("native stop Worker descriptor does not match the signed artifact")

        created_at_ns = _utc_instant_nanoseconds(
            bom["created_at"], "Release BOM created_at"
        )
        for authority in bom["native_stop_authorities"]:
            if (
                authority["worker_module_id"] != worker["module_id"]
                or authority["worker_artifact_id"] != "dps.windows-edge-worker"
                or authority["worker_artifact_sha256"] != worker["sha256"]
                or authority["worker_version"] != worker["version"]
            ):
                raise CandidateBomError(
                    "native stop authority Worker artifact tuple differs from Manifest/descriptor truth"
                )
            valid_from_ns = 100 * _dotnet_utc_ticks(
                authority["valid_from"], "native stop authority valid_from"
            )
            valid_until_ns = 100 * _dotnet_utc_ticks(
                authority["valid_until"], "native stop authority valid_until"
            )
            if not valid_from_ns <= created_at_ns < valid_until_ns:
                raise CandidateBomError("native stop authority is not valid when the Release BOM is created")

    def _validate_device_route_authority_bindings(self, bom: Mapping[str, Any]) -> None:
        """Bind Supervisor route authority to the exact signed service artifact."""
        module_by_id = {module["module_id"]: module for module in bom["modules"]}
        supervisor = module_by_id.get("windows-edge-supervisor")
        if supervisor is None:
            raise CandidateBomError("Release BOM is missing the windows-edge-supervisor artifact")
        manifest_bytes = self._git.read(
            bom["integration_commit"], "Modules/windows-edge-supervisor/module.yaml"
        )
        manifest = _require_mapping(
            _strict_json_loads(manifest_bytes, "windows-edge-supervisor Manifest"),
            "windows-edge-supervisor Manifest",
        )
        artifacts = list(_require_limited_sequence(
            manifest.get("artifacts"), "windows-edge-supervisor artifacts", 64
        ))
        matching_artifacts: list[Mapping[str, Any]] = []
        for raw_artifact in artifacts:
            artifact = _require_mapping(raw_artifact, "windows-edge-supervisor artifact declaration")
            _require_exact(
                artifact,
                {"id", "kind", "status", "build", "versioning"},
                "windows-edge-supervisor artifact declaration",
            )
            if artifact.get("id") == "dps.windows-edge-supervisor":
                matching_artifacts.append(artifact)
        runtime = _require_mapping(manifest.get("runtime"), "windows-edge-supervisor runtime")
        if (
            len(matching_artifacts) != 1
            or matching_artifacts[0].get("kind") != "service"
            or matching_artifacts[0].get("status") not in {"proposed", "buildable"}
            or matching_artifacts[0].get("versioning") != "semver"
            or runtime.get("processBoundary") != "out-of-process"
        ):
            raise CandidateBomError("device route authority does not identify the governed Supervisor service")

        _, descriptor_bytes = _read_bundle_file(
            self._bundle, supervisor["descriptor_uri"], "windows-edge-supervisor descriptor"
        )
        descriptor = _require_mapping(
            _strict_json_loads(descriptor_bytes, "windows-edge-supervisor descriptor"),
            "windows-edge-supervisor descriptor",
        )
        if (
            descriptor.get("module_id") != "windows-edge-supervisor"
            or descriptor.get("module_version") != supervisor["version"]
            or descriptor.get("artifact_sha256") != supervisor["sha256"]
            or descriptor.get("integration_commit") != bom["integration_commit"]
        ):
            raise CandidateBomError("device route Supervisor descriptor does not match the signed artifact")

        created_at_ns = _utc_instant_nanoseconds(
            bom["created_at"], "Release BOM created_at"
        )
        for authority in bom["device_route_assignment_authorities"]:
            if (
                authority["supervisor_module_id"] != supervisor["module_id"]
                or authority["supervisor_artifact_id"] != "dps.windows-edge-supervisor"
                or authority["supervisor_artifact_sha256"] != supervisor["sha256"]
                or authority["supervisor_version"] != supervisor["version"]
            ):
                raise CandidateBomError(
                    "device route authority Supervisor artifact tuple differs from Manifest/descriptor truth"
                )
            valid_from_ns = 100 * _dotnet_utc_ticks(
                authority["valid_from"], "device route authority valid_from"
            )
            valid_until_ns = 100 * _dotnet_utc_ticks(
                authority["valid_until"], "device route authority valid_until"
            )
            if not valid_from_ns <= created_at_ns < valid_until_ns:
                raise CandidateBomError("device route authority is not valid when the Release BOM is created")

    def _validate_native_stop_challenge_authority_bindings(
        self, bom: Mapping[str, Any]
    ) -> None:
        module_by_id = {module["module_id"]: module for module in bom["modules"]}
        policy_module = module_by_id.get("policy-approval")
        if policy_module is None:
            raise CandidateBomError("Release BOM is missing the policy-approval artifact")
        manifest_bytes = self._git.read(
            bom["integration_commit"], "Modules/policy-approval/module.yaml"
        )
        manifest = _require_mapping(
            _strict_json_loads(manifest_bytes, "policy-approval Manifest"),
            "policy-approval Manifest",
        )
        artifacts = list(_require_limited_sequence(
            manifest.get("artifacts"), "policy-approval artifacts", 64
        ))
        matching: list[Mapping[str, Any]] = []
        for raw_artifact in artifacts:
            artifact = _require_mapping(raw_artifact, "policy-approval artifact declaration")
            _require_exact(
                artifact, {"id", "kind", "status", "build", "versioning"},
                "policy-approval artifact declaration",
            )
            if artifact.get("id") == "dps.policy-approval":
                matching.append(artifact)
        runtime = _require_mapping(manifest.get("runtime"), "policy-approval runtime")
        if (
            len(matching) != 1
            or matching[0].get("kind") != "assembly"
            or matching[0].get("status") not in {"proposed", "buildable"}
            or matching[0].get("versioning") != "semver"
            or runtime.get("processBoundary") != "modular-monolith"
        ):
            raise CandidateBomError("challenge authority does not identify the governed Policy assembly")
        _, descriptor_bytes = _read_bundle_file(
            self._bundle, policy_module["descriptor_uri"], "policy-approval descriptor"
        )
        descriptor = _require_mapping(
            _strict_json_loads(descriptor_bytes, "policy-approval descriptor"),
            "policy-approval descriptor",
        )
        if (
            descriptor.get("module_id") != "policy-approval"
            or descriptor.get("module_version") != policy_module["version"]
            or descriptor.get("artifact_sha256") != policy_module["sha256"]
            or descriptor.get("integration_commit") != bom["integration_commit"]
        ):
            raise CandidateBomError("Policy descriptor does not match the signed challenge artifact")
        created_at_ns = _utc_instant_nanoseconds(bom["created_at"], "Release BOM created_at")
        for authority in bom["native_stop_challenge_authorities"]:
            if (
                authority["policy_module_id"] != policy_module["module_id"]
                or authority["policy_artifact_id"] != "dps.policy-approval"
                or authority["policy_artifact_sha256"] != policy_module["sha256"]
                or authority["policy_version"] != policy_module["version"]
            ):
                raise CandidateBomError(
                    "challenge authority Policy artifact tuple differs from Manifest/descriptor truth"
                )
            valid_from_ns = 100 * _dotnet_utc_ticks(
                authority["valid_from"], "challenge authority valid_from"
            )
            valid_until_ns = 100 * _dotnet_utc_ticks(
                authority["valid_until"], "challenge authority valid_until"
            )
            if not valid_from_ns <= created_at_ns < valid_until_ns:
                raise CandidateBomError("challenge authority is not valid when the Release BOM is created")

    def _validate_runtime_authority_currency(
        self,
        bom: Mapping[str, Any],
        bom_label: str,
    ) -> None:
        """Bind one signed BOM's runtime authority windows to validation time.

        The candidate and its previous stable rollback target both need usable
        runtime authorities.  Their created_at checks pin windows only to each
        BOM's self-declared history, so the trusted validation time must also
        fall inside every authority window with the non-zero rollout/rollback
        interval still remaining.
        """
        minimum_remaining_ns = self._minimum_remaining_lifetime_seconds * 1_000_000_000
        authority_sets = (
            ("native stop authority", bom["native_stop_authorities"], "authority_id"),
            (
                "device route authority",
                bom["device_route_assignment_authorities"],
                "route_authority_id",
            ),
            ("challenge authority", bom["native_stop_challenge_authorities"], "authority_id"),
        )
        for label, entries, identity_field in authority_sets:
            for entry in entries:
                valid_from_ns = 100 * _dotnet_utc_ticks(
                    entry["valid_from"], f"{label} valid_from"
                )
                valid_until_ns = 100 * _dotnet_utc_ticks(
                    entry["valid_until"], f"{label} valid_until"
                )
                identity = entry[identity_field]
                if self._validation_time_ns < valid_from_ns:
                    raise CandidateBomError(
                        f"{bom_label} {label} {identity} is not yet valid at the validation time"
                    )
                if self._validation_time_ns >= valid_until_ns:
                    raise CandidateBomError(
                        f"{bom_label} {label} {identity} is expired at the validation time"
                    )
                if valid_until_ns - self._validation_time_ns < minimum_remaining_ns:
                    raise CandidateBomError(
                        f"{bom_label} {label} {identity} expires inside the minimum remaining"
                        " lifetime at the validation time"
                    )

    def _validate_evidence(self, bom: Mapping[str, Any]) -> tuple[list[str], str]:
        evidence_signers: list[str] = []
        evidence_by_id: dict[str, Mapping[str, Any]] = {}
        for item in bom["evidence"]:
            _, evidence_bytes = _read_bundle_file(self._bundle, item["artifact_uri"], f"evidence {item['evidence_id']}")
            if sha256_bytes(evidence_bytes) != item["sha256"]:
                raise CandidateBomError(f"evidence {item['evidence_id']} hash mismatch")
            signer = self._trust.verify_signature(
                item["signature"], b"dps-release-evidence/v1\n" + evidence_bytes, "evidence"
            )
            if signer != item["issuer_identity"] or signer not in self._trust.evidence_issuers:
                raise CandidateBomError("evidence signer identity mismatch")
            evidence_signers.append(signer)
            receipt = _strict_json_loads(evidence_bytes, "evidence receipt")
            expected_receipt = {
                "schema_version", "evidence_id", "result", "required", "kind",
                "tested_commit", "verification_level", "issuer_identity",
            }
            receipt = _require_mapping(receipt, "evidence receipt")
            _require_exact(receipt, expected_receipt, "evidence receipt")
            for field in expected_receipt.difference({"schema_version"}):
                if receipt.get(field) != item.get(field):
                    raise CandidateBomError(f"evidence receipt field mismatch: {field}")
            if receipt.get("schema_version") != "dps.release-evidence/v1":
                raise CandidateBomError("unknown evidence receipt version")
            if item["required"] and item["result"] != "PASS":
                raise CandidateBomError(f"required evidence {item['evidence_id']} is not PASS")
            evidence_by_id[item["evidence_id"]] = item
        missing = sorted(set(self._trust.required_gates).difference(evidence_by_id))
        if missing:
            raise CandidateBomError("trusted required gate evidence is missing: " + ", ".join(missing))
        passed_required_ranks: list[int] = []
        for gate_id, (required_kind, minimum_level) in self._trust.required_gates.items():
            item = evidence_by_id[gate_id]
            if item["required"] is not True or item["result"] != "PASS":
                raise CandidateBomError(
                    f"trusted required gate {gate_id} must be signed, required, and PASS"
                )
            if item["kind"] != required_kind:
                raise CandidateBomError(
                    f"trusted required gate {gate_id} evidence kind mismatch"
                )
            actual_rank = _VERIFICATION_RANK[item["verification_level"]]
            if actual_rank < _VERIFICATION_RANK[minimum_level]:
                raise CandidateBomError(
                    f"trusted required gate {gate_id} verification level is below policy minimum"
                )
            passed_required_ranks.append(actual_rank)
        actual_ceiling_rank = min(max(passed_required_ranks), _VERIFICATION_RANK["INTEGRATION_VERIFIED"])
        return evidence_signers, _VERIFICATION_BY_RANK[actual_ceiling_rank]

    def _validate_risk_and_approval(self, bom: Mapping[str, Any]) -> str | None:
        scope_material = {
            "integration_commit": bom["integration_commit"],
            "modules": [
                {"module_id": item["module_id"], "version": item["version"], "sha256": item["sha256"]}
                for item in sorted(bom["modules"], key=lambda value: value["module_id"])
            ],
            "contracts": sorted(bom["contracts"], key=lambda value: (value["contract_id"], value["major"])),
            "database_versions": bom["database_versions"],
            "feature_flags": bom["feature_flags"],
            "kill_switches": bom["kill_switches"],
            "release_bom_generation": bom["release_bom_generation"],
            "activation_token_sha256": bom["activation_token_sha256"],
            "native_stop_authorities_sha256": _canonical_authorities_hash(
                bom["native_stop_authorities"]
            ),
            "device_route_assignment_authorities_sha256": (
                _canonical_route_authorities_hash(
                    bom["device_route_assignment_authorities"]
                )
            ),
            "native_stop_challenge_authorities_sha256": (
                _canonical_challenge_authorities_hash(
                    bom["native_stop_challenge_authorities"]
                )
            ),
        }
        if sha256_bytes(canonical_bytes(scope_material)) != bom["risk"]["scope_sha256"]:
            raise CandidateBomError("risk approval scope is not bound to the Release BOM")
        if bom["risk"]["requested_by"] not in self._trust.implementers:
            raise CandidateBomError("release requester is not a trusted module implementer")
        approval = bom["release_approval"]
        risk_tier = bom["risk"]["tier"]
        required = risk_tier in {"R2", "R3"}
        if approval["required"] is not required:
            raise CandidateBomError("release approval requirement conflicts with trusted risk policy")
        if not required:
            if any(approval[field] is not None for field in ("receipt_uri", "sha256", "approver_identity", "signature")):
                raise CandidateBomError("non-required release approval must not contain an approval claim")
            if approval["approver_role"] != "not-applicable":
                raise CandidateBomError("non-required release approval role must be not-applicable")
            return None
        if (
            not isinstance(approval["receipt_uri"], str)
            or not isinstance(approval["sha256"], str) or not _SHA256.fullmatch(approval["sha256"])
            or not isinstance(approval["approver_identity"], str)
            or approval["approver_role"] != "human-release-approver"
            or not isinstance(approval["signature"], Mapping)
        ):
            raise CandidateBomError("R2/R3 release approval is incomplete")
        _, approval_bytes = _read_bundle_file(self._bundle, approval["receipt_uri"], "release approval receipt")
        if sha256_bytes(approval_bytes) != approval["sha256"]:
            raise CandidateBomError("release approval receipt hash mismatch")
        approver = self._trust.verify_signature(
            approval["signature"], b"dps-release-approval/v1\n" + approval_bytes, "approval"
        )
        if approver != approval["approver_identity"] or approver not in self._trust.release_approvers:
            raise CandidateBomError("release approval signer identity mismatch")
        if approver == bom["risk"]["requested_by"]:
            raise CandidateBomError("release requester cannot approve its own release")
        receipt = _require_mapping(
            _strict_json_loads(approval_bytes, "release approval receipt"),
            "release approval receipt",
        )
        fields = {
            "schema_version", "approval_id", "bom_id", "integration_commit",
            "risk_tier", "scope_sha256", "status", "approver_identity",
            "approver_role", "approved_at",
        }
        _require_exact(receipt, fields, "release approval receipt")
        expected = {
            "schema_version": "dps.release-approval/v1",
            "bom_id": bom["bom_id"],
            "integration_commit": bom["integration_commit"],
            "risk_tier": risk_tier,
            "scope_sha256": bom["risk"]["scope_sha256"],
            "status": "APPROVED",
            "approver_identity": approver,
            "approver_role": "human-release-approver",
        }
        for field, value in expected.items():
            if receipt.get(field) != value:
                raise CandidateBomError(f"release approval receipt mismatch: {field}")
        if not isinstance(receipt.get("approval_id"), str) or len(receipt["approval_id"]) < 8:
            raise CandidateBomError("release approval id is invalid")
        _require_utc_timestamp(receipt.get("approved_at"), "release approval approved_at")
        return approver

    def _validate_native_stop_trust_receipt(
        self,
        bom: Mapping[str, Any],
        bom_bytes: bytes,
        receipt_path: str | os.PathLike[str] | None,
    ) -> tuple[str, str]:
        if receipt_path is None:
            raise CandidateBomError("signed native stop authority trust receipt is required")
        receipt_bytes = _read_stable_regular(
            Path(receipt_path), "native stop authority trust receipt"
        )
        receipt = _require_mapping(
            _strict_json_loads(receipt_bytes, "native stop authority trust receipt"),
            "native stop authority trust receipt",
        )
        try:
            canonical_receipt_bytes = canonical_bytes(receipt)
        except (OverflowError, UnicodeEncodeError, ValueError) as exc:
            raise CandidateBomError(
                "native stop authority trust receipt is outside the canonical JSON domain"
            ) from exc
        if receipt_bytes != canonical_receipt_bytes:
            raise CandidateBomError(
                "native stop authority trust receipt must be the canonical sorted compact JSON wire"
            )
        _require_exact(
            receipt,
            _NATIVE_STOP_TRUST_RECEIPT_FIELDS,
            "native stop authority trust receipt",
        )
        if (
            receipt.get("schema_version") != "1.0.0"
            or receipt.get("contract_id") != "release.bom.native.stop.authority.trust/v1"
            or receipt.get("producer_module") != "factory-release-controller"
            or any(receipt.get(field) is not None for field in (
                "soul_id", "device_binding_id", "platform_account_id"
            ))
            or receipt.get("privacy_class") != "internal"
        ):
            raise CandidateBomError(
                "native stop authority trust receipt envelope is invalid or pretends to authorize a device route"
            )
        if (
            not isinstance(receipt.get("trace_id"), str)
            or not _TRACE_ID.fullmatch(receipt["trace_id"])
            or not isinstance(receipt.get("idempotency_key"), str)
            or not _IDEMPOTENCY_KEY.fullmatch(receipt["idempotency_key"])
            or not isinstance(receipt.get("receipt_id"), str)
            or not re.fullmatch(r"native-stop-trust-[0-9a-f]{32}", receipt["receipt_id"])
        ):
            raise CandidateBomError("native stop authority trust receipt identity is invalid")
        issued_at_ns = 100 * _dotnet_utc_ticks(
            receipt.get("occurred_at"), "native stop authority trust receipt occurred_at"
        )
        if issued_at_ns < _utc_instant_nanoseconds(
            bom["created_at"], "Release BOM created_at"
        ):
            raise CandidateBomError("native stop authority trust receipt predates its Release BOM")
        expected_bom_sha = sha256_bytes(bom_bytes)
        expected_native_sha = _canonical_authorities_hash(bom["native_stop_authorities"])
        expected_route_sha = _canonical_route_authorities_hash(
            bom["device_route_assignment_authorities"]
        )
        expected_challenge_sha = _canonical_challenge_authorities_hash(
            bom["native_stop_challenge_authorities"]
        )
        expected_bindings = {
            "release_bom_id": bom["bom_id"],
            "release_bom_sha256": expected_bom_sha,
            "integration_commit": bom["integration_commit"],
            "release_bom_generation": bom["release_bom_generation"],
            "activation_token_sha256": bom["activation_token_sha256"],
            "trust_policy_id": self._trust.policy_id,
            "native_stop_authorities_sha256": expected_native_sha,
            "device_route_assignment_authorities_sha256": expected_route_sha,
            "native_stop_challenge_authorities_sha256": expected_challenge_sha,
            "authority_sets_sha256": _canonical_authority_sets_hash(
                expected_native_sha, expected_route_sha, expected_challenge_sha
            ),
            "native_stop_authorities": bom["native_stop_authorities"],
            "device_route_assignment_authorities": bom[
                "device_route_assignment_authorities"
            ],
            "native_stop_challenge_authorities": bom[
                "native_stop_challenge_authorities"
            ],
        }
        for field, expected in expected_bindings.items():
            if receipt.get(field) != expected:
                raise CandidateBomError(f"native stop authority trust receipt mismatch: {field}")
        signature = self._validate_signature_shape(
            receipt.get("signature"), "native stop authority trust receipt signature"
        )
        if signature.get("key_id") != self._trust.native_stop_trust_key_id:
            raise CandidateBomError("native stop authority trust receipt uses an unpinned signer key")
        payload = {key: value for key, value in receipt.items() if key != "signature"}
        signer = self._trust.verify_signature(
            signature,
            native_stop_trust_signing_bytes(payload),
            "native-stop-trust",
        )
        if signer not in self._trust.native_stop_trust_signers:
            raise CandidateBomError("native stop authority trust receipt signer role is invalid")
        return sha256_bytes(receipt_bytes), signer

    def _validate_native_stop_rotation(
        self,
        bom: Mapping[str, Any],
        previous: Mapping[str, Any] | None,
    ) -> None:
        if previous is None:
            return
        if bom["release_bom_generation"] <= previous["release_bom_generation"]:
            raise CandidateBomError("Release BOM generation must increase from the previous stable BOM")
        if hmac.compare_digest(
            bom["activation_token_sha256"], previous["activation_token_sha256"]
        ):
            raise CandidateBomError("a new Release BOM must use a fresh activation token digest")

        previous_key_spki = {
            item["key_id"]: item["p256_spki_sha256"]
            for item in previous["native_stop_authorities"]
        }
        for item in bom["native_stop_authorities"]:
            prior_spki = previous_key_spki.get(item["key_id"])
            if prior_spki is not None and prior_spki != item["p256_spki_sha256"]:
                raise CandidateBomError("native stop key_id changed P-256 SPKI across BOM generations")

        def external_key_bindings(value: Mapping[str, Any]) -> dict[str, tuple[Any, ...]]:
            return {
                item["p256_spki_sha256"]: (
                    item["key_id"], item["worker_module_id"], item["worker_artifact_id"],
                    item["worker_artifact_sha256"], item["worker_version"],
                    item["worker_slot"], item["worker_instance_id"],
                    item["worker_generation"], item["rotation_epoch"],
                )
                for item in value["native_stop_authorities"]
            }

        previous_spki_bindings = external_key_bindings(previous)
        for spki, binding in external_key_bindings(bom).items():
            prior_binding = previous_spki_bindings.get(spki)
            if prior_binding is not None and prior_binding != binding:
                raise CandidateBomError(
                    "one P-256 SPKI cannot move to another key id, artifact, incarnation, or rotation epoch"
                )

        previous_by_authority = {
            item["authority_id"]: item for item in previous["native_stop_authorities"]
        }
        for item in bom["native_stop_authorities"]:
            prior = previous_by_authority.get(item["authority_id"])
            if prior is None:
                continue
            if item["rotation_epoch"] < prior["rotation_epoch"]:
                raise CandidateBomError("native stop authority rotation epoch moved backwards")
            key_changed = (
                item["key_id"] != prior["key_id"]
                or item["p256_spki_sha256"] != prior["p256_spki_sha256"]
            )
            if key_changed and item["rotation_epoch"] <= prior["rotation_epoch"]:
                raise CandidateBomError("native stop key rotation did not advance its epoch")

        current_epochs = [item["rotation_epoch"] for item in bom["native_stop_authorities"]]
        previous_epochs = [item["rotation_epoch"] for item in previous["native_stop_authorities"]]
        current_keys = {
            (item["key_id"], item["p256_spki_sha256"])
            for item in bom["native_stop_authorities"]
        }
        previous_keys = {
            (item["key_id"], item["p256_spki_sha256"])
            for item in previous["native_stop_authorities"]
        }
        if min(current_epochs) < max(previous_epochs):
            raise CandidateBomError("native stop rotation epoch is below the previous stable authority set")
        if current_keys != previous_keys and min(current_epochs) <= max(previous_epochs):
            raise CandidateBomError("native stop key-set rotation must advance beyond the prior epoch")

        current_worker = next(
            item for item in bom["modules"] if item["module_id"] == "windows-edge-worker"
        )
        previous_worker = next(
            item for item in previous["modules"] if item["module_id"] == "windows-edge-worker"
        )
        current_major = _parse_semver(current_worker["version"])[0][0]
        previous_major = _parse_semver(previous_worker["version"])[0][0]
        if current_major < previous_major or current_major > previous_major + 1:
            raise CandidateBomError("native stop Worker version is outside the N/N-1 major window")
        candidate_created_ns = _utc_instant_nanoseconds(
            bom["created_at"], "Release BOM created_at"
        )
        if any(
            100 * _dotnet_utc_ticks(
                item["valid_until"], "previous native stop authority valid_until"
            ) <= candidate_created_ns
            for item in previous["native_stop_authorities"]
        ):
            raise CandidateBomError("previous native stop authority is unavailable for rollback")

    def _validate_device_route_rotation(
        self,
        bom: Mapping[str, Any],
        previous: Mapping[str, Any] | None,
    ) -> None:
        if previous is None:
            return

        previous_by_spki = {
            item["route_signer_p256_spki_sha256"]: (
                item["route_signer_key_id"], item["supervisor_module_id"],
                item["supervisor_artifact_id"], item["supervisor_artifact_sha256"],
                item["supervisor_version"], item["supervisor_instance_id"],
                item["supervisor_generation"], item["rotation_epoch"],
            )
            for item in previous["device_route_assignment_authorities"]
        }
        for item in bom["device_route_assignment_authorities"]:
            binding = (
                item["route_signer_key_id"], item["supervisor_module_id"],
                item["supervisor_artifact_id"], item["supervisor_artifact_sha256"],
                item["supervisor_version"], item["supervisor_instance_id"],
                item["supervisor_generation"], item["rotation_epoch"],
            )
            prior_binding = previous_by_spki.get(item["route_signer_p256_spki_sha256"])
            if prior_binding is not None and prior_binding != binding:
                raise CandidateBomError(
                    "one route P-256 SPKI cannot move to another Supervisor artifact or incarnation"
                )

        previous_by_authority = {
            item["route_authority_id"]: item
            for item in previous["device_route_assignment_authorities"]
        }
        for item in bom["device_route_assignment_authorities"]:
            prior = previous_by_authority.get(item["route_authority_id"])
            if prior is None:
                continue
            if item["rotation_epoch"] < prior["rotation_epoch"]:
                raise CandidateBomError("device route authority rotation epoch moved backwards")
            key_changed = (
                item["route_signer_key_id"] != prior["route_signer_key_id"]
                or item["route_signer_p256_spki_sha256"]
                != prior["route_signer_p256_spki_sha256"]
            )
            if key_changed and item["rotation_epoch"] <= prior["rotation_epoch"]:
                raise CandidateBomError("device route signer rotation did not advance its epoch")

        current_epochs = [
            item["rotation_epoch"] for item in bom["device_route_assignment_authorities"]
        ]
        previous_epochs = [
            item["rotation_epoch"] for item in previous["device_route_assignment_authorities"]
        ]
        current_keys = {
            (item["route_signer_key_id"], item["route_signer_p256_spki_sha256"])
            for item in bom["device_route_assignment_authorities"]
        }
        previous_keys = {
            (item["route_signer_key_id"], item["route_signer_p256_spki_sha256"])
            for item in previous["device_route_assignment_authorities"]
        }
        if min(current_epochs) < max(previous_epochs):
            raise CandidateBomError("device route rotation epoch is below the previous stable set")
        if current_keys != previous_keys and min(current_epochs) <= max(previous_epochs):
            raise CandidateBomError("device route key-set rotation must advance beyond the prior epoch")

        previous_native_spki = {
            item["p256_spki_sha256"] for item in previous["native_stop_authorities"]
        }
        current_native_spki = {
            item["p256_spki_sha256"] for item in bom["native_stop_authorities"]
        }
        previous_route_spki = {
            item["route_signer_p256_spki_sha256"]
            for item in previous["device_route_assignment_authorities"]
        }
        current_route_spki = {
            item["route_signer_p256_spki_sha256"]
            for item in bom["device_route_assignment_authorities"]
        }
        if current_route_spki & previous_native_spki or current_native_spki & previous_route_spki:
            raise CandidateBomError("P-256 key purpose cannot change between native stop and device route authority")

        current_supervisor = next(
            item for item in bom["modules"] if item["module_id"] == "windows-edge-supervisor"
        )
        previous_supervisor = next(
            item for item in previous["modules"]
            if item["module_id"] == "windows-edge-supervisor"
        )
        current_major = _parse_semver(current_supervisor["version"])[0][0]
        previous_major = _parse_semver(previous_supervisor["version"])[0][0]
        if current_major < previous_major or current_major > previous_major + 1:
            raise CandidateBomError("route Supervisor version is outside the N/N-1 major window")
        candidate_created_ns = _utc_instant_nanoseconds(
            bom["created_at"], "Release BOM created_at"
        )
        if any(
            100 * _dotnet_utc_ticks(
                item["valid_until"], "previous device route authority valid_until"
            ) <= candidate_created_ns
            for item in previous["device_route_assignment_authorities"]
        ):
            raise CandidateBomError("previous device route authority is unavailable for rollback")

    def _validate_native_stop_challenge_rotation(
        self,
        bom: Mapping[str, Any],
        previous: Mapping[str, Any] | None,
    ) -> None:
        if previous is None:
            return
        previous_by_spki = {
            item["p256_spki_sha256"]: (
                item["key_id"], item["policy_module_id"], item["policy_artifact_id"],
                item["policy_artifact_sha256"], item["policy_version"],
                item["policy_instance_id"], item["policy_generation"],
                item["rotation_epoch"],
            )
            for item in previous["native_stop_challenge_authorities"]
        }
        for item in bom["native_stop_challenge_authorities"]:
            binding = (
                item["key_id"], item["policy_module_id"], item["policy_artifact_id"],
                item["policy_artifact_sha256"], item["policy_version"],
                item["policy_instance_id"], item["policy_generation"],
                item["rotation_epoch"],
            )
            prior_binding = previous_by_spki.get(item["p256_spki_sha256"])
            if prior_binding is not None and prior_binding != binding:
                raise CandidateBomError(
                    "one challenge P-256 SPKI cannot move to another Policy artifact or incarnation"
                )
        previous_by_authority = {
            item["authority_id"]: item
            for item in previous["native_stop_challenge_authorities"]
        }
        for item in bom["native_stop_challenge_authorities"]:
            prior = previous_by_authority.get(item["authority_id"])
            if prior is None:
                continue
            if item["rotation_epoch"] < prior["rotation_epoch"]:
                raise CandidateBomError("challenge authority rotation epoch moved backwards")
            changed = (
                item["key_id"] != prior["key_id"]
                or item["p256_spki_sha256"] != prior["p256_spki_sha256"]
            )
            if changed and item["rotation_epoch"] <= prior["rotation_epoch"]:
                raise CandidateBomError("challenge signer rotation did not advance its epoch")
        current_epochs = [
            item["rotation_epoch"] for item in bom["native_stop_challenge_authorities"]
        ]
        previous_epochs = [
            item["rotation_epoch"] for item in previous["native_stop_challenge_authorities"]
        ]
        current_keys = {
            (item["key_id"], item["p256_spki_sha256"])
            for item in bom["native_stop_challenge_authorities"]
        }
        previous_keys = {
            (item["key_id"], item["p256_spki_sha256"])
            for item in previous["native_stop_challenge_authorities"]
        }
        if min(current_epochs) < max(previous_epochs):
            raise CandidateBomError("challenge rotation epoch is below the previous stable set")
        if current_keys != previous_keys and min(current_epochs) <= max(previous_epochs):
            raise CandidateBomError("challenge key-set rotation must advance beyond the prior epoch")

        prior_all_other = {
            item["p256_spki_sha256"] for item in previous["native_stop_authorities"]
        } | {
            item["route_signer_p256_spki_sha256"]
            for item in previous["device_route_assignment_authorities"]
        }
        current_all_other = {
            item["p256_spki_sha256"] for item in bom["native_stop_authorities"]
        } | {
            item["route_signer_p256_spki_sha256"]
            for item in bom["device_route_assignment_authorities"]
        }
        current_challenge = {
            item["p256_spki_sha256"] for item in bom["native_stop_challenge_authorities"]
        }
        previous_challenge = {
            item["p256_spki_sha256"]
            for item in previous["native_stop_challenge_authorities"]
        }
        if current_challenge & prior_all_other or previous_challenge & current_all_other:
            raise CandidateBomError("P-256 key purpose cannot change to or from challenge authority")

        current_policy = next(
            item for item in bom["modules"] if item["module_id"] == "policy-approval"
        )
        previous_policy = next(
            item for item in previous["modules"] if item["module_id"] == "policy-approval"
        )
        current_major = _parse_semver(current_policy["version"])[0][0]
        previous_major = _parse_semver(previous_policy["version"])[0][0]
        if current_major < previous_major or current_major > previous_major + 1:
            raise CandidateBomError("Policy challenge version is outside the N/N-1 major window")
        candidate_created_ns = _utc_instant_nanoseconds(
            bom["created_at"], "Release BOM created_at"
        )
        if any(
            100 * _dotnet_utc_ticks(
                item["valid_until"], "previous challenge authority valid_until"
            ) <= candidate_created_ns
            for item in previous["native_stop_challenge_authorities"]
        ):
            raise CandidateBomError("previous challenge authority is unavailable for rollback")

    def _validate_previous_bom(
        self,
        bom: Mapping[str, Any],
        previous_bom_path: str | os.PathLike[str] | None,
    ) -> Mapping[str, Any] | None:
        previous_id = bom["previous_stable_bom"]
        previous_sha = bom["previous_stable_bom_sha256"]
        if previous_id is None:
            if previous_sha is not None or previous_bom_path is not None or not self._trust.allow_bootstrap:
                raise CandidateBomError("previous stable BOM is required unless trusted bootstrap is enabled")
            return None
        if not isinstance(previous_id, str) or len(previous_id) < 8 or not isinstance(previous_sha, str) or not _SHA256.fullmatch(previous_sha):
            raise CandidateBomError("previous stable BOM reference is invalid")
        if previous_bom_path is None:
            raise CandidateBomError("previous stable BOM file is required")
        previous_bytes = _read_stable_regular(Path(previous_bom_path), "previous stable BOM")
        if sha256_bytes(previous_bytes) != previous_sha:
            raise CandidateBomError("previous stable BOM hash mismatch")
        previous = _require_mapping(
            _strict_json_loads(previous_bytes, "previous stable BOM"),
            "previous stable BOM",
        )
        self._validate_exact_shape(previous, expected_status="STABLE")
        _require_canonical_bom_wire(previous, previous_bytes, "previous stable BOM")
        if previous.get("bom_id") != previous_id or previous_id == bom["bom_id"]:
            raise CandidateBomError("previous stable BOM identity or status mismatch")
        if previous.get("previous_stable_bom") == previous_id:
            raise CandidateBomError("previous stable BOM cannot reference itself")
        signature = self._validate_signature_shape(previous.get("signature"), "previous stable BOM signature")
        payload = {key: value for key, value in previous.items() if key != "signature"}
        self._trust.verify_signature(signature, b"dps-release-bom/v1\n" + canonical_bytes(payload), "bom")
        if not self._git.is_ancestor(previous["integration_commit"], bom["integration_commit"]):
            raise CandidateBomError("previous stable BOM is not in the candidate commit lineage")
        self._validate_repository_bindings(previous)
        self._validate_module_artifacts(previous)
        self._validate_native_stop_authority_bindings(previous)
        self._validate_device_route_authority_bindings(previous)
        self._validate_native_stop_challenge_authority_bindings(previous)
        previous_artifact_set = [
            {"module_id": item["module_id"], "sha256": item["sha256"]}
            for item in sorted(previous["modules"], key=lambda value: value["module_id"])
        ]
        if previous["rollout"]["shadow_artifact_sha256"] != sha256_bytes(canonical_bytes(previous_artifact_set)):
            raise CandidateBomError("previous stable BOM artifact set digest is invalid")
        return previous

    def validate(
        self,
        bom_path: str | os.PathLike[str],
        previous_bom_path: str | os.PathLike[str] | None = None,
        native_stop_trust_receipt_path: str | os.PathLike[str] | None = None,
    ) -> dict[str, Any]:
        bom_bytes = _read_stable_regular(Path(bom_path), "candidate Release BOM")
        bom = _require_mapping(
            _strict_json_loads(bom_bytes, "candidate Release BOM"),
            "candidate Release BOM",
        )
        self._validate_exact_shape(bom)
        _require_canonical_bom_wire(bom, bom_bytes, "candidate Release BOM")
        signed_payload = {key: value for key, value in bom.items() if key != "signature"}
        bom_signer = self._trust.verify_signature(
            bom["signature"], b"dps-release-bom/v1\n" + canonical_bytes(signed_payload), "bom"
        )
        self._validate_repository_bindings(bom)
        artifact_signers = self._validate_module_artifacts(bom)
        self._validate_native_stop_authority_bindings(bom)
        self._validate_device_route_authority_bindings(bom)
        self._validate_native_stop_challenge_authority_bindings(bom)
        self._validate_runtime_authority_currency(bom, "candidate Release BOM")
        evidence_signers, verification_ceiling = self._validate_evidence(bom)
        approver = self._validate_risk_and_approval(bom)
        previous = self._validate_previous_bom(bom, previous_bom_path)
        if previous is not None:
            self._validate_runtime_authority_currency(
                previous, "previous stable BOM"
            )
        self._validate_native_stop_rotation(bom, previous)
        self._validate_device_route_rotation(bom, previous)
        self._validate_native_stop_challenge_rotation(bom, previous)
        native_stop_trust_receipt_sha256, native_stop_trust_signer = (
            self._validate_native_stop_trust_receipt(
                bom, bom_bytes, native_stop_trust_receipt_path
            )
        )
        artifact_set = [
            {"module_id": item["module_id"], "sha256": item["sha256"]}
            for item in sorted(bom["modules"], key=lambda value: value["module_id"])
        ]
        artifact_set_sha = sha256_bytes(canonical_bytes(artifact_set))
        if bom["rollout"]["shadow_artifact_sha256"] != artifact_set_sha:
            raise CandidateBomError("shadow artifact set digest differs from signed module set")
        return {
            "result": "PASS",
            "validation_kind": "CANDIDATE_BOM_STATIC",
            "verification_ceiling": verification_ceiling,
            "schema_sha256": self._schema_sha256,
            "trust_policy_id": self._trust.policy_id,
            "bom_id": bom["bom_id"],
            "bom_sha256": sha256_bytes(bom_bytes),
            "integration_commit": bom["integration_commit"],
            "artifact_set_sha256": artifact_set_sha,
            "bom_signer": bom_signer,
            "artifact_signers": sorted(set(artifact_signers)),
            "evidence_signers": sorted(set(evidence_signers)),
            "release_approver": approver,
            "release_bom_generation": bom["release_bom_generation"],
            "activation_token_sha256": bom["activation_token_sha256"],
            "native_stop_authorities_sha256": _canonical_authorities_hash(
                bom["native_stop_authorities"]
            ),
            "device_route_assignment_authorities_sha256": (
                _canonical_route_authorities_hash(
                    bom["device_route_assignment_authorities"]
                )
            ),
            "native_stop_challenge_authorities_sha256": (
                _canonical_challenge_authorities_hash(
                    bom["native_stop_challenge_authorities"]
                )
            ),
            "native_stop_trust_receipt_sha256": native_stop_trust_receipt_sha256,
            "native_stop_trust_signer": native_stop_trust_signer,
            "simulation_only": False,
            "canary_verified": False,
            "scale_verified": False,
        }


def _load_json(path: str | os.PathLike[str], label: str) -> Mapping[str, Any]:
    raw = _read_stable_regular(Path(path), label)
    value = _strict_json_loads(raw, label)
    return _require_mapping(value, label)


def _minimum_remaining_lifetime_arg(value: str) -> int:
    if not re.fullmatch(r"[1-9][0-9]*", value):
        raise argparse.ArgumentTypeError(
            "must be a positive integer number of seconds"
        )
    return int(value, 10)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Validate a signed DPS candidate BOM without deploying it")
    # Tools/ci/<this file> -> parents[2] is the repository root.
    parser.add_argument("--repo-root", default=str(Path(__file__).resolve().parents[2]))
    parser.add_argument("--bundle-root", required=True)
    parser.add_argument("--bom", required=True)
    parser.add_argument("--previous-bom")
    parser.add_argument(
        "--native-stop-trust-receipt",
        required=True,
        help="externally signed release.bom.native.stop.authority.trust/v1 receipt",
    )
    parser.add_argument("--schema-sha256", required=True, help="SHA-256 of exact governance Release BOM schema bytes")
    parser.add_argument(
        "--validation-time",
        default=None,
        help="trusted validation instant as a canonical UTC timestamp (default: system UTC now)",
    )
    parser.add_argument(
        "--minimum-remaining-lifetime-seconds",
        type=_minimum_remaining_lifetime_arg,
        default=_DEFAULT_MINIMUM_REMAINING_LIFETIME_SECONDS,
        help="positive authority-window lifetime that must remain after validation (default: 86400)",
    )
    arguments = parser.parse_args(argv)
    try:
        validator = CandidateBomValidator.from_deployed_anchor(
            arguments.repo_root,
            arguments.bundle_root,
            arguments.schema_sha256,
            validation_time=arguments.validation_time,
            minimum_remaining_lifetime_seconds=arguments.minimum_remaining_lifetime_seconds,
        )
        result = validator.validate(
            arguments.bom,
            arguments.previous_bom,
            arguments.native_stop_trust_receipt,
        )
    except CandidateBomError as exc:
        print(json.dumps({"result": "FAIL", "reason": str(exc)}, sort_keys=True, separators=(",", ":")))
        return 1
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    sys.exit(main())
