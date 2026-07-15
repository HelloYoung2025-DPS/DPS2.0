"""Validate externally produced F6-F9 evidence without issuing evidence.

The validator is intentionally standard-library only. ECDSA verification is
delegated to a locally installed OpenSSL binary and always uses public keys
injected through a deployment-owned trust policy. A successful decision means
only that a separate evidence issuer may consider signing a receipt.
"""

from __future__ import annotations

import base64
import binascii
import hashlib
import json
import math
import os
import re
import shutil
import stat
import subprocess
import tempfile
import uuid
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence


PASS = "PASS"
WAITING_EXTERNAL = "WAITING_EXTERNAL"
FAIL = "FAIL"
ELIGIBLE = "ELIGIBLE_FOR_EXTERNAL_ISSUANCE"
P1363_ALGORITHM = "ECDSA_P256_SHA256_P1363"
BOM_ALGORITHM = "ecdsa-p256-sha256"
SHA256_RE = re.compile(r"^[0-9a-f]{64}\Z")
GIT_OBJECT_RE = re.compile(r"^[0-9a-f]{40}\Z")
EXTERNAL_ID_RE = re.compile(r"^[A-Za-z][A-Za-z0-9._:/-]{7,127}$")
SOUL_ID_RE = re.compile(r"^soul_[a-f0-9]{64}\Z")
DEVICE_BINDING_ID_RE = re.compile(r"^db_[a-f0-9]{32}\Z")
PLATFORM_ACCOUNT_ID_RE = re.compile(r"^pa_[a-f0-9]{32}\Z")
TRACE_ID_RE = re.compile(r"^trace_[a-f0-9]{32}\Z")
IDEMPOTENCY_KEY_RE = re.compile(r"^idem_[a-f0-9]{64}\Z")
MODULE_ID_RE = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
SEMVER_RE = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$"
)
CONTRACT_COMPATIBILITY_MODES = {"active", "compat-read", "quarantine-only", "retired"}
READABLE_CONSUMER_MODES = {"active", "compat-read"}
RUNNABLE_CONTRACT_MODE = "active"
EXTERNAL_COMMUNICATION_PEERS = {
    "gbrain-company",
    "postgresql",
    "windows-edge",
    "zennodroid",
    "external",
}
LOGICAL_GBRAIN_SOURCE_RE = re.compile(r"^dps-[a-f0-9]{28}\Z")
EXTERNAL_GBRAIN_SOURCE_ALIAS_RE = re.compile(r"^gs_[0-9a-f]{16}$")
REPOSITORY_ID_RE = re.compile(r"^repo:[a-z0-9]+(?:[._/-][a-z0-9]+)*\Z")
ENVIRONMENT_ID_RE = re.compile(r"^env_[a-z0-9][a-z0-9_-]{6,63}$")
ZENNO_VERSION_RE = re.compile(r"^[0-9]+(?:\.[0-9]+){1,3}(?:[-+][A-Za-z0-9.-]+)?$")
GBRAIN_DEPLOYMENT_ID_RE = re.compile(r"^gbrain_[a-z0-9][a-z0-9_-]{6,63}$")
EDGE_INSTALLATION_ID_RE = re.compile(r"^edge_[a-z0-9][a-z0-9_-]{6,63}$")
ZENNO_INSTALLATION_ID_RE = re.compile(r"^zenno_[a-z0-9][a-z0-9_-]{6,63}$")
_SENSITIVE_ENVIRONMENT_TOKEN_RE = re.compile(
    r"(?:^|[^a-z0-9])(?:secret|key|token|password|credential)(?:$|[^a-z0-9])"
    r"|api[_-]?key|private[_-]?key|access[_-]?key|signing[_-]?key|embedding[_-]?key",
    re.IGNORECASE,
)
_SECRET_VALUE_PREFIX_RE = re.compile(r"^(?:sk|pk|rk|api|token|secret)[_-][A-Za-z0-9_-]{8,}$", re.IGNORECASE)
_JWT_VALUE_RE = re.compile(r"^[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}$")
_SCOPE_VALUE_PREFIX_RE = re.compile(r"^(?:soul|db|pa|gs)[_-][A-Za-z0-9_-]{8,}$", re.IGNORECASE)
DOTNET_DECIMAL_MAX = Decimal("79228162514264337593543950335")
F7_GBRAIN_CONTRACT_BINDING_STATUS = "STALE"
STALE_CANDIDATE_GBRAIN_PROJECTION_V2_SCHEMA_SHA256 = "d77938579d957472ebdab1181d25b0a44a27e74c24f1aec324abf34c5a412255"
STALE_CANDIDATE_GBRAIN_SOURCE_BINDING_V1_SCHEMA_SHA256 = "e15a6dda03be8e33e0379d9501ca6f1bd1b1ba63742652679f8705df1550d815"
STALE_CANDIDATE_GBRAIN_PROJECTION_V2_DTO_SHA256 = "f2963bc454ceb774b403814e73b862b4bad0423f4fd34a2147b93448a98dc3ba"
STALE_CANDIDATE_GBRAIN_SOURCE_BINDING_V1_DTO_SHA256 = "161661959fc77100c40e9b00082ee83cc05327de5e088a30c1e0a1256f8d398b"

STAGE_SPECS: dict[str, dict[str, Any]] = {
    "f6": {
        "schema_version": "dps.windows-zenno-verification-input/v1",
        "verification_level": "WINDOWS_VERIFIED",
        "schema": "f6-windows-zenno-input.v1.schema.json",
    },
    "f7": {
        "schema_version": "dps.device-gbrain-verification-input/v3",
        "verification_level": "DEVICE_VERIFIED",
        "schema": "f7-device-gbrain-input.v3.schema.json",
    },
    "f8": {
        "schema_version": "dps.canary-verification-input/v1",
        "verification_level": "CANARY_VERIFIED",
        "schema": "f8-canary-input.v1.schema.json",
    },
    "f9": {
        "schema_version": "dps.scale-verification-input/v1",
        "verification_level": "SCALE_VERIFIED",
        "schema": "f9-scale-input.v1.schema.json",
    },
}

STAGE_ENVIRONMENT_KEYS: dict[str, set[str]] = {
    "f6": {
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
    "f7": {
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
    "f8": {"environment_id", "os_family"},
    "f9": {"environment_id", "os_family"},
}

STAGE_OS_FAMILIES: dict[str, set[str]] = {
    "f6": {"Windows"},
    "f7": {"Windows+Android"},
    "f8": {"Windows", "Android", "Windows+Android", "Linux"},
    "f9": {"Windows", "Android", "Windows+Android", "Linux"},
}

F7_PROJECTION_KIND = "GBRAIN_PROJECTION_EXACT_READBACK"
F7_SEARCH_KIND = "GBRAIN_SEARCH_REVALIDATION"
F7_PER_SOUL_ARTIFACT_KINDS = {
    "SOUL_DEVICE_SOURCE_OAUTH_BINDING",
    "PERSONA_EXACT_CURRENT_READBACK",
    "DELETE_REBUILD_PURGE",
    "DATA_SUBJECT_EXPORT",
    "DATA_SUBJECT_CORRECTION",
    "DATA_SUBJECT_DELETION",
    "FIXTURE_COMMAND_POSTCONDITION",
    "DUPLICATE_DELIVERY",
    "UNKNOWN_OUTCOME_RECONCILIATION",
}
F7_ATTACK_ARTIFACT_KINDS = {
    "CROSS_SOUL_ATTACK",
    "CROSS_DEVICE_ATTACK",
    "CROSS_ACCOUNT_ATTACK",
}
F7_PHASE_BY_ARTIFACT_KIND = {
    F7_PROJECTION_KIND: "GBRAIN_PROJECTION",
    F7_SEARCH_KIND: "EXACT_READBACK",
    "SOUL_DEVICE_SOURCE_OAUTH_BINDING": "VERIFY",
    "PERSONA_EXACT_CURRENT_READBACK": "EXACT_READBACK",
    "DELETE_REBUILD_PURGE": "DELETE_REBUILD",
    "DATA_SUBJECT_EXPORT": "EXACT_READBACK",
    "DATA_SUBJECT_CORRECTION": "DELETE_REBUILD",
    "DATA_SUBJECT_DELETION": "DELETE_REBUILD",
    "FIXTURE_COMMAND_POSTCONDITION": "VERIFY",
    "CROSS_SOUL_ATTACK": "VERIFY",
    "CROSS_DEVICE_ATTACK": "VERIFY",
    "CROSS_ACCOUNT_ATTACK": "VERIFY",
    "DUPLICATE_DELIVERY": "VERIFY",
    "UNKNOWN_OUTCOME_RECONCILIATION": "VERIFY",
}
F7_OBSERVATION_COMMON_KEYS = {
    "f7_run_id",
    "trace_id",
    "release_bom_id",
    "release_bom_sha256",
    "phase",
    "observed_at",
    "scope_sha256",
}
F7_SEMANTIC_RAW_EXCHANGE_KEYS = {
    "request_sha256",
    "request_base64",
    "response_sha256",
    "response_base64",
    "postcondition_sha256",
    "postcondition_base64",
}
F7_ARTIFACT_SUMMARIES = {
    F7_PROJECTION_KIND: "redacted exact projection write and readback",
    F7_SEARCH_KIND: "redacted source scoped search and result revalidation",
    "SOUL_DEVICE_SOURCE_OAUTH_BINDING": "redacted physical device source and oauth binding",
    "PERSONA_EXACT_CURRENT_READBACK": "redacted deterministic persona current readback",
    "DELETE_REBUILD_PURGE": "redacted delete rebuild and storage layer purge observations",
    "DATA_SUBJECT_EXPORT": "redacted data subject export scope observation",
    "DATA_SUBJECT_CORRECTION": "redacted data subject correction observation",
    "DATA_SUBJECT_DELETION": "redacted data subject deletion observation",
    "FIXTURE_COMMAND_POSTCONDITION": "redacted fixture command native result and postcondition",
    "CROSS_SOUL_ATTACK": "redacted cross soul attack rejection observation",
    "CROSS_DEVICE_ATTACK": "redacted cross device attack rejection observation",
    "CROSS_ACCOUNT_ATTACK": "redacted cross account attack rejection observation",
    "DUPLICATE_DELIVERY": "redacted duplicate delivery idempotency observation",
    "UNKNOWN_OUTCOME_RECONCILIATION": "redacted unknown outcome reconciliation observation",
}

_COMMON_KEYS = {
    "schema_version",
    "evidence_id",
    "evidence_kind",
    "required",
    "status",
    "baseline_commit",
    "release_bom",
    "environment",
    "measurement_window",
    "raw_artifacts",
    "factory_binding",
    "payload",
    "attestation",
}


class ExternalGateError(RuntimeError):
    """Evidence is present but cannot be trusted or does not meet the gate."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


class ExternalPrerequisiteMissing(RuntimeError):
    """Evidence or externally managed trust material has not been supplied."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class GateDecision:
    stage: str
    status: str
    exit_code: int
    decision: str
    target_verification_level: str
    reason_code: str
    message: str
    evidence_id: str | None = None
    evidence_sha256: str | None = None

    def as_dict(self) -> dict[str, Any]:
        return {
            "schema_version": "dps.external-gate-decision/v1",
            "stage": self.stage,
            "status": self.status,
            "exit_code": self.exit_code,
            "decision": self.decision,
            "target_verification_level": self.target_verification_level,
            "reason_code": self.reason_code,
            "message": self.message,
            "evidence_id": self.evidence_id,
            "evidence_sha256": self.evidence_sha256,
            "evidence_receipt_issued": False,
        }


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _fail(code: str, message: str) -> None:
    raise ExternalGateError(code, message)


def _wait(code: str, message: str) -> None:
    raise ExternalPrerequisiteMissing(code, message)


def _object(value: Any, label: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        _fail("invalid_shape", f"{label} must be an object")
    return value


def _array(value: Any, label: str) -> Sequence[Any]:
    if not isinstance(value, list):
        _fail("invalid_shape", f"{label} must be an array")
    return value


def _exact_keys(value: Mapping[str, Any], required: set[str], label: str) -> None:
    actual = set(value)
    if actual != required:
        missing = sorted(required - actual)
        unknown = sorted(actual - required)
        _fail("invalid_shape", f"{label} keys mismatch; missing={missing}, unknown={unknown}")


def _text(value: Any, label: str, minimum: int = 1) -> str:
    if not isinstance(value, str) or len(value) < minimum:
        _fail("invalid_value", f"{label} must be a non-empty string")
    return value


def _number(value: Any, label: str, minimum: float | None = None) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float, Decimal)):
        _fail("invalid_value", f"{label} must be numeric")
    result = float(value)
    if not math.isfinite(result):
        _fail("invalid_value", f"{label} must be finite")
    if minimum is not None and result < minimum:
        _fail("threshold_not_met", f"{label} must be at least {minimum}")
    return result


def _dotnet_decimal(value: Any, label: str) -> Decimal:
    if isinstance(value, bool) or not isinstance(value, (int, Decimal)):
        _fail("projection_contract_mismatch", f"{label} must be a .NET decimal JSON number")
    result = value if isinstance(value, Decimal) else Decimal(value)
    if not result.is_finite():
        _fail("projection_contract_mismatch", f"{label} must be finite")
    scale = max(-result.as_tuple().exponent, 0)
    if abs(result) > DOTNET_DECIMAL_MAX or scale > 28:
        _fail("projection_contract_mismatch", f"{label} exceeds System.Decimal range or scale")
    return result


def _integer(value: Any, label: str, minimum: int | None = None) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        _fail("invalid_value", f"{label} must be an integer")
    if minimum is not None and value < minimum:
        _fail("threshold_not_met", f"{label} must be at least {minimum}")
    return value


def _true(value: Any, label: str) -> None:
    if value is not True:
        _fail("required_fact_false", f"{label} must be true")


def _zero(value: Any, label: str) -> None:
    if isinstance(value, bool) or value != 0:
        _fail("zero_tolerance_breach", f"{label} must be zero")


def _pass(value: Any, label: str) -> None:
    if value != PASS:
        _fail("required_outcome_not_pass", f"{label} must be PASS, got {value!r}")


def _sha256(value: Any, label: str) -> str:
    if not isinstance(value, str) or SHA256_RE.fullmatch(value) is None:
        _fail("invalid_digest", f"{label} must be a lowercase SHA-256 digest")
    return value


def _external_revision(value: Any, label: str) -> int | str:
    """Validate optional platform metadata without treating it as DPS truth."""

    if isinstance(value, bool):
        _fail("invalid_external_revision", f"{label} must be a positive integer or bounded identifier")
    if isinstance(value, int):
        if value < 1:
            _fail("invalid_external_revision", f"{label} integer must be positive")
        return value
    if isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}", value):
        return value
    _fail("invalid_external_revision", f"{label} must be a positive integer or bounded identifier")


def _base64_content(value: Any, label: str, maximum_bytes: int = 8 * 1024 * 1024) -> bytes:
    text = _text(value, label)
    try:
        decoded = base64.b64decode(text, validate=True)
    except (binascii.Error, ValueError) as exc:
        raise ExternalGateError("invalid_projection_content", f"{label} must be canonical Base64") from exc
    if not decoded or len(decoded) > maximum_bytes:
        _fail("invalid_projection_content", f"{label} must contain 1..{maximum_bytes} bytes")
    if base64.b64encode(decoded).decode("ascii") != text:
        _fail("invalid_projection_content", f"{label} must use canonical padded Base64")
    return decoded


def _canonical_identifier(value: Any, pattern: re.Pattern[str], label: str) -> str:
    text = _text(value, label)
    if pattern.fullmatch(text) is None:
        _fail("noncanonical_scope_id", f"{label} is not a canonical opaque DPS identifier")
    return text


def _external_id(value: Any, label: str) -> str:
    text = _text(value, label, 8)
    if EXTERNAL_ID_RE.fullmatch(text) is None:
        _fail("invalid_external_id", f"{label} is not a bounded external evidence identifier")
    return text


def _expected_external_source_alias(logical_source_id: str) -> str:
    digest = sha256_bytes(("dps-gbrain-external-source/v1\n" + logical_source_id).encode("utf-8"))
    return "gs_" + digest[:16]


def _contains_sensitive_environment_token(value: str) -> bool:
    camel_expanded = re.sub(r"([a-z0-9])([A-Z])", r"\1_\2", value)
    return _SENSITIVE_ENVIRONMENT_TOKEN_RE.search(camel_expanded) is not None


def _environment_value_is_sensitive(value: Any) -> bool:
    if not isinstance(value, str):
        return False
    stripped = value.strip()
    if _contains_sensitive_environment_token(stripped):
        return True
    if (
        stripped.lower().startswith("bearer ")
        or _SECRET_VALUE_PREFIX_RE.fullmatch(stripped)
        or _SCOPE_VALUE_PREFIX_RE.fullmatch(stripped)
    ):
        return True
    return _JWT_VALUE_RE.fullmatch(stripped) is not None


def _reject_sensitive_environment_claims(claims: Mapping[str, Any], label: str) -> None:
    for key, value in claims.items():
        if not isinstance(key, str) or _contains_sensitive_environment_token(key):
            _fail("sensitive_environment_claim", f"{label} contains a forbidden secret/key/token/password field")
        if type(value) not in {str, int, float, bool, type(None)}:
            _fail("invalid_environment_claim_type", f"{label}.{key} must be a primitive JSON value")
        if isinstance(value, float) and not math.isfinite(value):
            _fail("invalid_environment_claim_type", f"{label}.{key} must be a finite JSON number")
        if isinstance(value, str) and (len(value) > 256 or any(ord(character) < 0x20 for character in value)):
            _fail("invalid_environment_claim_value", f"{label}.{key} must be a bounded single-line value")
        if _environment_value_is_sensitive(value):
            _fail("sensitive_environment_claim", f"{label}.{key} contains secret-like material")


def _validate_environment_claim_grammar(key: str, value: Any, label: str) -> None:
    if key in {"loopback_port_fixed", "loopback_only"}:
        if value is not True:
            _fail("invalid_environment_claim_value", f"{label}.{key} must be true")
        return
    if key == "adb_authorized_device_count":
        _integer(value, f"{label}.adb_authorized_device_count", 1)
        return
    if key == "loopback_port":
        if isinstance(value, bool) or not isinstance(value, int) or not 1024 <= value <= 65535:
            _fail("invalid_environment_claim_value", f"{label}.loopback_port must be an integer in 1024..65535")
        return
    if key == "command_timeout_seconds":
        timeout = _number(value, f"{label}.command_timeout_seconds", 0.001)
        if timeout > 300:
            _fail("invalid_environment_claim_value", f"{label}.command_timeout_seconds must be at most 300")
        return
    if not isinstance(value, str):
        _fail("invalid_environment_claim_type", f"{label}.{key} must be a string")
    if key in {
        "codedom_compile",
        "gac_resolution",
        "dll_load",
        "zenno_project_load",
        "adb_authorization",
        "connection_continuity",
    } and value != PASS:
        _fail("invalid_environment_claim_value", f"{label}.{key} must be PASS")
    if key == "loopback_host" and value != "127.0.0.1":
        _fail("invalid_environment_claim_value", f"{label}.loopback_host must be 127.0.0.1")
    if key == "timeout_semantics" and value != "FAIL_CLOSED":
        _fail("invalid_environment_claim_value", f"{label}.timeout_semantics must be FAIL_CLOSED")
    if key == "error_semantics" and value != "NATIVE_ERROR_PRESERVED":
        _fail(
            "invalid_environment_claim_value",
            f"{label}.error_semantics must be NATIVE_ERROR_PRESERVED",
        )
    if key in {"environment_id", "parent_windows_environment_id"} and ENVIRONMENT_ID_RE.fullmatch(value) is None:
        _fail("invalid_environment_claim_value", f"{label}.{key} must be a canonical env_ identifier")
    if key == "os_family" and value not in {"Windows", "Android", "Windows+Android", "Linux"}:
        _fail("invalid_environment_claim_value", f"{label}.os_family is not an allowed operating-system family")
    if key == "zennodroid_version" and ZENNO_VERSION_RE.fullmatch(value) is None:
        _fail("invalid_environment_claim_value", f"{label}.zennodroid_version is not a bounded version")
    if key in {"windows_version", "dotnet_framework_version", "csharp_language_version"} and re.fullmatch(
        r"[0-9]+(?:\.[0-9]+){1,3}", value
    ) is None:
        _fail("invalid_environment_claim_value", f"{label}.{key} must be an exact numeric version")
    if key == "bridge_abi" and re.fullmatch(r"dps\.zenno-bridge/v[1-9][0-9]*", value) is None:
        _fail("invalid_environment_claim_value", f"{label}.bridge_abi must be a versioned DPS bridge ABI")
    if key == "gbrain_deployment_id" and GBRAIN_DEPLOYMENT_ID_RE.fullmatch(value) is None:
        _fail(
            "invalid_environment_claim_value",
            f"{label}.gbrain_deployment_id must be a canonical gbrain_ identifier",
        )
    if key == "edge_installation_id" and EDGE_INSTALLATION_ID_RE.fullmatch(value) is None:
        _fail("invalid_environment_claim_value", f"{label}.edge_installation_id must be canonical")
    if key == "zenno_installation_id" and ZENNO_INSTALLATION_ID_RE.fullmatch(value) is None:
        _fail("invalid_environment_claim_value", f"{label}.zenno_installation_id must be canonical")
    if key == "runner_component" and value != "dps-f7-external-runner":
        _fail("invalid_environment_claim_value", f"{label}.runner_component is not the trusted F7 runner")
    if key == "runner_version" and SEMVER_RE.fullmatch(value) is None:
        _fail("invalid_environment_claim_value", f"{label}.runner_version must be exact SemVer")
    if key in {"runner_binary_sha256", "runner_sbom_sha256"}:
        _sha256(value, f"{label}.{key}")


def _decode_json_object(raw: bytes, label: str, *, preserve_decimals: bool = False) -> Mapping[str, Any]:
    def reject_duplicate_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                _fail("duplicate_json_key", f"{label} contains duplicate JSON key {key!r}")
            result[key] = value
        return result

    def reject_non_json_constant(value: str) -> None:
        _fail("invalid_json", f"{label} contains non-JSON numeric constant {value}")

    try:
        value = json.loads(
            raw.decode("utf-8"),
            object_pairs_hook=reject_duplicate_pairs,
            parse_float=Decimal if preserve_decimals else float,
            parse_constant=reject_non_json_constant,
        )
    except ExternalGateError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ExternalGateError("invalid_json", f"{label} is not valid UTF-8 JSON") from exc
    return _object(value, label)


def _system_text_json_string(value: str) -> str:
    """Encode a string the way Utf8JsonWriter's default encoder does.

    GBrainProjectionCanonicalizer is an owned C# wire contract.  Keeping this
    small encoder here lets the external evidence gate independently verify the
    exact bytes without importing product assemblies or trusting producer
    supplied checksum metadata.
    """

    escaped: list[str] = ['"']
    short_escapes = {
        '"': '\\"',
        "\\": "\\\\",
        "\b": "\\b",
        "\t": "\\t",
        "\n": "\\n",
        "\f": "\\f",
        "\r": "\\r",
    }
    for character in value:
        if character in short_escapes:
            escaped.append(short_escapes[character])
            continue
        codepoint = ord(character)
        if 0x20 <= codepoint <= 0x7E and character not in {"<", ">", "&", "'"}:
            escaped.append(character)
            continue
        encoded = character.encode("utf-16-be", errors="surrogatepass")
        for offset in range(0, len(encoded), 2):
            code_unit = int.from_bytes(encoded[offset : offset + 2], "big")
            escaped.append(f"\\u{code_unit:04X}")
    escaped.append('"')
    return "".join(escaped)


def _system_text_json_value(value: Any) -> str:
    if isinstance(value, str):
        return _system_text_json_string(value)
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return "null"
    if isinstance(value, bool):
        _fail("projection_contract_mismatch", "boolean cannot be serialized as a projection number")
    if isinstance(value, int):
        return str(value)
    if isinstance(value, Decimal):
        return format(_dotnet_decimal(value, "projection decimal"), "f")
    if isinstance(value, list):
        return "[" + ",".join(_system_text_json_value(item) for item in value) + "]"
    if isinstance(value, Mapping):
        return "{" + ",".join(
            _system_text_json_string(str(key)) + ":" + _system_text_json_value(item)
            for key, item in value.items()
        ) + "}"
    _fail("projection_contract_mismatch", "projection contains an unsupported JSON value")


_UTC_TEXT_RE = re.compile(
    r"^(?P<date>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})"
    r"(?:\.(?P<fraction>[0-9]{0,6}[1-9]))?Z\Z"
)


def _format_dotnet_utc(value: Any, label: str) -> str:
    text = _text(value, label)
    _parse_utc(text, label)
    match = _UTC_TEXT_RE.fullmatch(text)
    if match is None:
        _fail("projection_contract_mismatch", f"{label} cannot be represented by the owned UTC canonicalizer")
    fraction = match.group("fraction")
    return match.group("date") + ("." + fraction if fraction else "") + "Z"


def _gbrain_source_for_soul(soul_id: str, nonce: int) -> str:
    _canonical_identifier(soul_id, SOUL_ID_RE, "Source binding soul_id")
    if isinstance(nonce, bool) or not isinstance(nonce, int) or nonce < 0 or nonce > 1023:
        _fail("source_binding_nonce_invalid", "Source binding nonce must be an integer in 0..1023")
    domain = b"dps.gbrain-source-binding/source-id/v1\x00"
    payload = domain + soul_id.encode("ascii") + b"\x00" + nonce.to_bytes(8, "big", signed=True)
    return "dps-" + sha256_bytes(payload)[:28]


def _gbrain_source_binding_canonical_bytes(
    binding: Mapping[str, Any],
    *,
    include_revision: bool,
    include_checksum: bool,
) -> bytes:
    canonical: dict[str, Any] = {
        "schema_version": binding["schema_version"],
        "contract_id": binding["contract_id"],
        "producer_module": binding["producer_module"],
        "soul_id": binding["soul_id"],
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": None,
        "idempotency_key": None,
        "occurred_at": _format_dotnet_utc(binding["occurred_at"], "source binding.occurred_at"),
        "privacy_class": binding["privacy_class"],
        "source_id": binding["source_id"],
        "algorithm": binding["algorithm"],
        "nonce": binding["nonce"],
        "soul_hash": binding["soul_hash"],
        "allocated_at": _format_dotnet_utc(binding["allocated_at"], "source binding.allocated_at"),
    }
    if include_revision:
        canonical["binding_revision"] = str(binding["binding_revision"]).lower()
    if include_checksum:
        canonical["binding_checksum"] = str(binding["binding_checksum"]).lower()
    return _system_text_json_value(canonical).encode("utf-8")


def _validate_gbrain_source_binding_content(
    raw: bytes,
    expected_scope: Mapping[str, str],
    expected_nonce: int,
    label: str,
) -> tuple[str, str, str]:
    binding = _decode_json_object(raw, label)
    keys = {
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
        "source_id",
        "algorithm",
        "nonce",
        "soul_hash",
        "allocated_at",
        "binding_revision",
        "binding_checksum",
    }
    _exact_keys(binding, keys, label)
    nonce = _integer(binding["nonce"], f"{label}.nonce", 0)
    if nonce > 1023:
        _fail("source_binding_nonce_invalid", "Source binding nonce exceeds the candidate v1 range")
    if (
        binding["schema_version"] != "1.0.0"
        or binding["contract_id"] != "gbrain.source.binding/v1"
        or binding["producer_module"] != "gbrain-projector"
        or binding["soul_id"] != expected_scope["soul_id"]
        or binding["source_id"] != expected_scope["logical_source_id"]
        or nonce != expected_nonce
        or binding["device_binding_id"] is not None
        or binding["platform_account_id"] is not None
        or binding["trace_id"] is not None
        or binding["idempotency_key"] is not None
        or binding["privacy_class"] != "personal"
        or binding["algorithm"] != "dps.gbrain-source-binding.sha256-nonce/v1"
        or binding["soul_hash"] != expected_scope["soul_id"][5:]
        or binding["occurred_at"] != binding["allocated_at"]
        or binding["source_id"] != _gbrain_source_for_soul(expected_scope["soul_id"], nonce)
    ):
        _fail("source_binding_contract_mismatch", "Source binding does not match the candidate Soul-owned v1 contract")
    revision = _sha256(binding["binding_revision"], f"{label}.binding_revision")
    checksum = _sha256(binding["binding_checksum"], f"{label}.binding_checksum")
    expected_revision = sha256_bytes(
        _gbrain_source_binding_canonical_bytes(binding, include_revision=False, include_checksum=False)
    )
    expected_checksum = sha256_bytes(
        _gbrain_source_binding_canonical_bytes(binding, include_revision=True, include_checksum=False)
    )
    if revision != expected_revision or checksum != expected_checksum:
        _fail("source_binding_checksum_mismatch", "Source binding revision/checksum is not reproducible")
    if _gbrain_source_binding_canonical_bytes(binding, include_revision=True, include_checksum=True) != raw:
        _fail("noncanonical_source_binding", "Source binding bytes do not match the candidate C# canonicalizer")
    return revision, checksum, str(binding["allocated_at"])


def _guid(value: Any, label: str) -> uuid.UUID:
    try:
        result = uuid.UUID(_text(value, label))
    except ValueError as exc:
        raise ExternalGateError("projection_contract_mismatch", f"{label} must be a UUID") from exc
    if result.int == 0:
        _fail("projection_contract_mismatch", f"{label} cannot be the empty UUID")
    return result


def _ordinal_key(value: str) -> bytes:
    return value.encode("utf-16-be", errors="surrogatepass")


def _guid_sort_key(value: Any) -> tuple[int, int, int, int, int, int]:
    return _guid(value, "projection event_id").fields


def _gbrain_projection_canonical_bytes(
    projection: Mapping[str, Any],
    *,
    include_checksum: bool,
) -> bytes:
    """Mirror the currently stale candidate GBrainProjectionV2 canonicalizer."""

    canonical: dict[str, Any] = {
        "schema_version": projection["schema_version"],
        "contract_id": projection["contract_id"],
        "producer_module": projection["producer_module"],
        "soul_id": projection["soul_id"],
        "device_binding_id": projection["device_binding_id"],
        "platform_account_id": projection["platform_account_id"],
        "trace_id": projection["trace_id"],
        "idempotency_key": projection["idempotency_key"],
        "occurred_at": _format_dotnet_utc(projection["occurred_at"], "projection.occurred_at"),
        "privacy_class": projection["privacy_class"],
        "source_id": projection["source_id"],
        "source_binding_algorithm": projection["source_binding_algorithm"],
        "source_binding_nonce": projection["source_binding_nonce"],
        "source_binding_soul_hash": str(projection["source_binding_soul_hash"]).lower(),
        "source_binding_allocated_at": _format_dotnet_utc(
            projection["source_binding_allocated_at"],
            "projection.source_binding_allocated_at",
        ),
        "source_binding_revision": str(projection["source_binding_revision"]).lower(),
        "source_binding_checksum": str(projection["source_binding_checksum"]).lower(),
        "projection_revision": str(projection["projection_revision"]).lower(),
    }
    if include_checksum:
        canonical["projection_checksum"] = str(projection["projection_checksum"]).lower()
    canonical["render_status"] = projection["render_status"]
    canonical["source_event_count"] = projection["source_event_count"]

    events = list(_array(projection["events"], "projection.events"))
    events.sort(
        key=lambda item: (
            _format_dotnet_utc(
                _object(item, "projection event")["occurred_at"],
                "projection event occurred_at",
            ),
            _guid_sort_key(_object(item, "projection event")["event_id"]),
            str(_object(item, "projection event")["event_hash"]),
        )
    )
    canonical["events"] = [
        {
            "event_id": str(_guid(event["event_id"], "projection event_id")),
            "event_hash": str(event["event_hash"]).lower(),
            "content_digest": str(event["content_digest"]).lower(),
            "occurred_at": _format_dotnet_utc(event["occurred_at"], "projection event occurred_at"),
        }
        for event in (_object(item, "projection event") for item in events)
    ]

    interests = list(_array(projection["interests"], "projection.interests"))
    interests.sort(key=lambda item: _ordinal_key(str(_object(item, "projection interest")["topic"])))
    canonical_interests: list[dict[str, Any]] = []
    for item in interests:
        interest = _object(item, "projection interest")
        evidence_rows = list(_array(interest["evidence"], "projection interest evidence"))
        evidence_rows.sort(
            key=lambda value: (
                _format_dotnet_utc(
                    _object(value, "projection interest evidence")["occurred_at"],
                    "projection interest evidence occurred_at",
                ),
                _guid_sort_key(_object(value, "projection interest evidence")["event_id"]),
                str(_object(value, "projection interest evidence")["event_hash"]),
            )
        )
        canonical_interests.append(
            {
                "topic": interest["topic"],
                "original_confidence": interest["original_confidence"],
                "decayed_confidence": interest["decayed_confidence"],
                "half_life_seconds": interest["half_life_seconds"],
                "algorithm_version": interest["algorithm_version"],
                "evidence": [
                    {
                        "event_id": str(_guid(evidence["event_id"], "projection evidence event_id")),
                        "event_hash": str(evidence["event_hash"]).lower(),
                        "occurred_at": _format_dotnet_utc(
                            evidence["occurred_at"], "projection evidence occurred_at"
                        ),
                        "original_confidence": evidence["original_confidence"],
                        "decayed_confidence": evidence["decayed_confidence"],
                    }
                    for evidence in (
                        _object(value, "projection interest evidence") for value in evidence_rows
                    )
                ],
            }
        )
    canonical["interests"] = canonical_interests
    return _system_text_json_value(canonical).encode("utf-8")


def _validate_gbrain_projection_content(
    raw: bytes,
    expected_scope: Mapping[str, str],
    expected_nonce: int,
    verified_binding: tuple[str, str, str],
    label: str,
) -> str:
    projection = _decode_json_object(raw, label, preserve_decimals=True)
    if projection.get("schema_version") != "2.0.0" or projection.get("contract_id") != "gbrain.projection/v2":
        _fail(
            "projection_contract_mismatch",
            f"{label} must use the re-frozen gbrain.projection/v2 candidate; v1 is quarantine-only",
        )
    required_keys = {
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
        "source_id",
        "source_binding_algorithm",
        "source_binding_nonce",
        "source_binding_soul_hash",
        "source_binding_allocated_at",
        "source_binding_revision",
        "source_binding_checksum",
        "projection_revision",
        "projection_checksum",
        "render_status",
        "source_event_count",
        "events",
        "interests",
    }
    _exact_keys(projection, required_keys, label)
    if projection["contract_id"] != "gbrain.projection/v2" or projection["producer_module"] != "gbrain-projector":
        _fail("projection_contract_mismatch", f"{label} is not a gbrain.projection/v2 DTO")
    for field, expected in expected_scope.items():
        projection_field = "source_id" if field == "logical_source_id" else field
        if projection.get(projection_field) != expected:
            _fail("projection_content_scope_mismatch", f"{label}.{projection_field} does not match the F7 tuple")
    trace_id = _text(projection["trace_id"], f"{label}.trace_id")
    idempotency_key = _text(projection["idempotency_key"], f"{label}.idempotency_key")
    if TRACE_ID_RE.fullmatch(trace_id) is None or IDEMPOTENCY_KEY_RE.fullmatch(idempotency_key) is None:
        _fail("projection_contract_mismatch", f"{label} trace/idempotency identifiers are not canonical")
    projection_occurred_at = _parse_utc(projection["occurred_at"], f"{label}.occurred_at")
    if projection["privacy_class"] != "personal" or projection["render_status"] != "dto-rendered-not-written":
        _fail("projection_contract_mismatch", f"{label} privacy/render contract is invalid")
    source_binding_nonce = _integer(projection["source_binding_nonce"], f"{label}.source_binding_nonce", 0)
    source_binding_revision = _sha256(
        projection["source_binding_revision"],
        f"{label}.source_binding_revision",
    )
    source_binding_checksum = _sha256(
        projection["source_binding_checksum"],
        f"{label}.source_binding_checksum",
    )
    _parse_utc(projection["source_binding_allocated_at"], f"{label}.source_binding_allocated_at")
    if (
        source_binding_nonce > 1023
        or source_binding_nonce != expected_nonce
        or projection["source_binding_algorithm"] != "dps.gbrain-source-binding.sha256-nonce/v1"
        or projection["source_binding_soul_hash"] != expected_scope["soul_id"][5:]
        or source_binding_revision != verified_binding[0]
        or source_binding_checksum != verified_binding[1]
        or projection["source_binding_allocated_at"] != verified_binding[2]
        or projection["source_id"] != _gbrain_source_for_soul(expected_scope["soul_id"], expected_nonce)
    ):
        _fail("projection_source_binding_mismatch", "projection v2 does not embed the exact verified Source binding proof")
    _sha256(projection["projection_revision"], f"{label}.projection_revision")
    projection_checksum = _sha256(projection["projection_checksum"], f"{label}.projection_checksum")
    source_event_count = _integer(projection["source_event_count"], f"{label}.source_event_count", 0)
    events = _array(projection["events"], f"{label}.events")
    if source_event_count != len(events):
        _fail("projection_contract_mismatch", f"{label}.source_event_count does not match events")
    event_ids: set[uuid.UUID] = set()
    for index, item in enumerate(events):
        event = _object(item, f"{label}.events[{index}]")
        _exact_keys(event, {"event_id", "event_hash", "content_digest", "occurred_at"}, f"{label}.events[{index}]")
        event_id = _guid(event["event_id"], f"{label}.events[{index}].event_id")
        if event_id in event_ids:
            _fail("projection_contract_mismatch", "projection event identifiers must be unique")
        event_ids.add(event_id)
        _sha256(event["event_hash"], f"{label}.events[{index}].event_hash")
        _sha256(event["content_digest"], f"{label}.events[{index}].content_digest")
        _parse_utc(event["occurred_at"], f"{label}.events[{index}].occurred_at")
    interests = _array(projection["interests"], f"{label}.interests")
    topics: set[str] = set()
    for index, item in enumerate(interests):
        interest = _object(item, f"{label}.interests[{index}]")
        interest_keys = {
            "topic",
            "original_confidence",
            "decayed_confidence",
            "half_life_seconds",
            "algorithm_version",
            "evidence",
        }
        _exact_keys(interest, interest_keys, f"{label}.interests[{index}]")
        topic = _text(interest["topic"], f"{label}.interests[{index}].topic")
        algorithm = _text(interest["algorithm_version"], f"{label}.interests[{index}].algorithm_version")
        if topic.isspace() or algorithm.isspace() or len(topic) > 128 or len(algorithm) > 64:
            _fail("projection_contract_mismatch", "projection interest text exceeds the v2 contract")
        if topic in topics:
            _fail("projection_contract_mismatch", "projection interest topics must be unique")
        topics.add(topic)
        confidence_values: dict[str, Decimal] = {}
        for confidence_key in ("original_confidence", "decayed_confidence"):
            confidence = _dotnet_decimal(
                interest[confidence_key],
                f"{label}.interests[{index}].{confidence_key}",
            )
            if confidence < 0 or confidence > 1:
                _fail("projection_contract_mismatch", "projection confidence exceeds one")
            confidence_values[confidence_key] = confidence
        if confidence_values["decayed_confidence"] > confidence_values["original_confidence"]:
            _fail("projection_contract_mismatch", "projection decayed confidence exceeds original confidence")
        if _dotnet_decimal(
            interest["half_life_seconds"],
            f"{label}.interests[{index}].half_life_seconds",
        ) <= 0:
            _fail("projection_contract_mismatch", "projection half_life_seconds must be positive")
        evidence_rows = _array(interest["evidence"], f"{label}.interests[{index}].evidence")
        if not evidence_rows:
            _fail("projection_contract_mismatch", "projection interest evidence cannot be empty")
        evidence_event_ids: set[uuid.UUID] = set()
        for evidence_index, evidence_value in enumerate(evidence_rows):
            evidence = _object(evidence_value, f"{label}.interests[{index}].evidence[{evidence_index}]")
            evidence_keys = {
                "event_id",
                "event_hash",
                "occurred_at",
                "original_confidence",
                "decayed_confidence",
            }
            _exact_keys(evidence, evidence_keys, f"{label}.interests[{index}].evidence[{evidence_index}]")
            evidence_event_id = _guid(
                evidence["event_id"],
                f"{label}.interests[{index}].evidence[{evidence_index}].event_id",
            )
            if evidence_event_id in evidence_event_ids:
                _fail("projection_contract_mismatch", "interest evidence event identifiers must be unique")
            if evidence_event_id not in event_ids:
                _fail("projection_contract_mismatch", "interest evidence must reference a projected event")
            evidence_event_ids.add(evidence_event_id)
            _sha256(evidence["event_hash"], f"{label}.interests[{index}].evidence[{evidence_index}].event_hash")
            evidence_occurred_at = _parse_utc(
                evidence["occurred_at"],
                f"{label}.interests[{index}].evidence[{evidence_index}].occurred_at",
            )
            if evidence_occurred_at > projection_occurred_at:
                _fail("projection_contract_mismatch", "interest evidence cannot occur after the projection")
            evidence_confidence_values: dict[str, Decimal] = {}
            for confidence_key in ("original_confidence", "decayed_confidence"):
                confidence = _dotnet_decimal(
                    evidence[confidence_key],
                    f"{label}.interests[{index}].evidence[{evidence_index}].{confidence_key}",
                )
                if confidence < 0 or confidence > 1:
                    _fail("projection_contract_mismatch", "interest evidence confidence exceeds one")
                evidence_confidence_values[confidence_key] = confidence
            if evidence_confidence_values["decayed_confidence"] > evidence_confidence_values["original_confidence"]:
                _fail("projection_contract_mismatch", "interest evidence decayed confidence exceeds original")

    expected_checksum = sha256_bytes(
        _gbrain_projection_canonical_bytes(projection, include_checksum=False)
    )
    if projection_checksum != expected_checksum:
        _fail(
            "projection_checksum_mismatch",
            f"{label}.projection_checksum does not match the candidate gbrain.projection/v2 canonical content",
        )
    expected_bytes = _gbrain_projection_canonical_bytes(projection, include_checksum=True)
    if expected_bytes != raw:
        _fail(
            "noncanonical_projection_content",
            f"{label} does not match GBrainProjectionV2Canonicalizer.Serialize bytes",
        )
    return projection_checksum


def _parse_utc(value: Any, label: str) -> datetime:
    text = _text(value, label)
    try:
        parsed = datetime.fromisoformat(text.replace("Z", "+00:00"))
    except ValueError as exc:
        raise ExternalGateError("invalid_time", f"{label} is not a valid date-time") from exc
    if parsed.tzinfo is None or parsed.utcoffset() != timedelta(0):
        _fail("invalid_time", f"{label} must include a UTC offset")
    return parsed.astimezone(timezone.utc)


def _window(value: Any, label: str) -> tuple[datetime, datetime]:
    window = _object(value, label)
    _exact_keys(window, {"started_at", "finished_at"}, label)
    started = _parse_utc(window["started_at"], f"{label}.started_at")
    finished = _parse_utc(window["finished_at"], f"{label}.finished_at")
    if finished <= started:
        _fail("invalid_time_window", f"{label} must have positive duration")
    return started, finished


def _read_json(path: Path, label: str, missing_is_waiting: bool = False) -> Mapping[str, Any]:
    if not path.exists():
        if missing_is_waiting:
            _wait("external_input_missing", f"{label} does not exist")
        _fail("file_missing", f"{label} does not exist")
    if path.is_symlink() or not path.is_file():
        _fail("unsafe_file", f"{label} must be a regular non-symlink file")
    try:
        raw = path.read_bytes()
        value = _decode_json_object(raw, label)
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ExternalGateError("invalid_json", f"{label} is not readable canonical JSON") from exc
    return value


def _regular_external_file(path_value: Any, label: str) -> tuple[Path, bytes]:
    raw_path = _text(path_value, label)
    path = Path(raw_path)
    if not path.is_absolute():
        _fail("unsafe_file", f"{label} must be an absolute path")
    if not path.exists():
        _wait("external_artifact_missing", f"{label} is not mounted on this runner")
    if path.is_symlink():
        _fail("unsafe_file", f"{label} must not be a symlink")
    try:
        resolved = path.resolve(strict=True)
        mode = resolved.stat().st_mode
        if not stat.S_ISREG(mode):
            _fail("unsafe_file", f"{label} must be a regular file")
        return resolved, resolved.read_bytes()
    except OSError as exc:
        raise ExternalPrerequisiteMissing("external_artifact_unreadable", f"{label} is not readable") from exc


def _verify_file_binding(
    path_value: Any,
    expected_sha256: Any,
    expected_size: Any | None,
    label: str,
) -> tuple[Path, bytes]:
    path, data = _regular_external_file(path_value, label)
    digest = _sha256(expected_sha256, f"{label}.sha256")
    if sha256_bytes(data) != digest:
        _fail("artifact_digest_mismatch", f"{label} digest does not match the observed bytes")
    if expected_size is not None and len(data) != _integer(expected_size, f"{label}.size_bytes", 1):
        _fail("artifact_size_mismatch", f"{label} size does not match the observed bytes")
    return path, data


def _p1363_to_der(signature: bytes) -> bytes:
    if len(signature) != 64:
        _fail("invalid_signature", "ECDSA P-256 P1363 signatures must be exactly 64 bytes")

    def encode_integer(raw: bytes) -> bytes:
        normalized = raw.lstrip(b"\x00") or b"\x00"
        if normalized[0] & 0x80:
            normalized = b"\x00" + normalized
        return b"\x02" + bytes([len(normalized)]) + normalized

    body = encode_integer(signature[:32]) + encode_integer(signature[32:])
    return b"\x30" + bytes([len(body)]) + body


def _openssl_verify_p1363(public_key_bytes: bytes, signing_payload: bytes, signature_base64: Any) -> None:
    openssl = shutil.which("openssl")
    if openssl is None:
        _wait("openssl_missing", "OpenSSL is required on the external verification runner")
    try:
        signature = base64.b64decode(_text(signature_base64, "signature_base64"), validate=True)
    except (binascii.Error, ValueError) as exc:
        raise ExternalGateError("invalid_signature", "signature_base64 is not valid Base64") from exc
    der = _p1363_to_der(signature)
    with tempfile.TemporaryDirectory(prefix="dps-external-gate-") as directory:
        public_key_path = Path(directory) / "public-key.pem"
        signature_path = Path(directory) / "signature.der"
        public_key_path.write_bytes(public_key_bytes)
        signature_path.write_bytes(der)
        try:
            key_check = subprocess.run(
                [openssl, "pkey", "-pubin", "-in", str(public_key_path), "-text", "-noout"],
                capture_output=True,
                timeout=10,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise ExternalPrerequisiteMissing("openssl_unavailable", "OpenSSL could not inspect the public key") from exc
        key_text = (key_check.stdout + key_check.stderr).decode("utf-8", errors="replace")
        if key_check.returncode != 0:
            _fail("invalid_public_key", "the injected public key is not a valid PEM public key")
        if "prime256v1" not in key_text and "P-256" not in key_text:
            _fail("invalid_public_key", "the injected public key is not ECDSA P-256")
        try:
            result = subprocess.run(
                [
                    openssl,
                    "dgst",
                    "-sha256",
                    "-verify",
                    str(public_key_path),
                    "-signature",
                    str(signature_path),
                ],
                input=signing_payload,
                capture_output=True,
                timeout=10,
                check=False,
            )
        except (OSError, subprocess.TimeoutExpired) as exc:
            raise ExternalPrerequisiteMissing("openssl_unavailable", "OpenSSL could not verify the signature") from exc
    if result.returncode != 0 or b"Verified OK" not in result.stdout:
        _fail("invalid_signature", "signature verification failed")


def _find_unique(items: Sequence[Any], predicate: Callable[[Mapping[str, Any]], bool], label: str) -> Mapping[str, Any]:
    matches = [item for item in items if isinstance(item, Mapping) and predicate(item)]
    if len(matches) != 1:
        _fail("trust_policy_mismatch", f"{label} must resolve to exactly one trust-policy entry")
    return matches[0]


def _secure_trust_policy_bytes(path: Path) -> bytes:
    """Read the trust root from one no-follow descriptor chain and fail closed."""

    if not path.is_absolute():
        _fail("unsafe_trust_policy", "trust policy path must be absolute and externally injected")
    if any(component in {"", ".", ".."} for component in path.parts[1:]):
        _fail("unsafe_trust_policy", "trust policy path contains an unsafe component")
    required_flags = ("O_NOFOLLOW", "O_DIRECTORY")
    if os.name == "nt" or any(not hasattr(os, name) for name in required_flags) or os.open not in os.supports_dir_fd:
        _wait(
            "secure_trust_reader_unavailable",
            "this runner lacks the no-follow directory-descriptor primitives required for trust policy input",
        )
    directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    current_fd: int | None = None
    policy_fd: int | None = None
    try:
        current_fd = os.open(path.anchor, directory_flags)
        for component in path.parts[1:-1]:
            next_fd = os.open(component, directory_flags, dir_fd=current_fd)
            if not stat.S_ISDIR(os.fstat(next_fd).st_mode):
                os.close(next_fd)
                _fail("unsafe_trust_policy", "trust policy parent must be a real directory")
            os.close(current_fd)
            current_fd = next_fd
        policy_fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=current_fd)
        policy_stat = os.fstat(policy_fd)
        if not stat.S_ISREG(policy_stat.st_mode):
            _fail("unsafe_trust_policy", "trust policy must be a regular file")
        if policy_stat.st_mode & 0o022:
            _fail("unsafe_trust_policy", "trust policy must not be group- or world-writable")
        if hasattr(os, "geteuid") and policy_stat.st_uid not in {0, os.geteuid()}:
            _fail("unsafe_trust_policy", "trust policy must be owned by root or the external runner identity")
        chunks: list[bytes] = []
        total = 0
        while True:
            chunk = os.read(policy_fd, 65536)
            if not chunk:
                break
            total += len(chunk)
            if total > 1024 * 1024:
                _fail("unsafe_trust_policy", "trust policy exceeds the one-megabyte safety limit")
            chunks.append(chunk)
        if total == 0:
            _fail("invalid_json", "external trust policy is empty")
        return b"".join(chunks)
    except FileNotFoundError as exc:
        raise ExternalPrerequisiteMissing("trust_policy_missing", "external trust policy is not mounted") from exc
    except ExternalGateError:
        raise
    except ExternalPrerequisiteMissing:
        raise
    except OSError as exc:
        raise ExternalGateError(
            "unsafe_trust_policy",
            "trust policy path contains a symlink, reparse-like component, or unreadable directory",
        ) from exc
    finally:
        if policy_fd is not None:
            os.close(policy_fd)
        if current_fd is not None:
            os.close(current_fd)


def _load_and_validate_trust_policy(path: Path) -> Mapping[str, Any]:
    policy = _decode_json_object(_secure_trust_policy_bytes(path), "external trust policy")
    required_keys = {
        "schema_version",
        "policy_id",
        "trusted_issuers",
        "trusted_bom_signers",
        "environment_policies",
    }
    actual_keys = set(policy)
    missing = sorted(required_keys - actual_keys)
    unknown = sorted(actual_keys - required_keys - {"prerequisite_receipt_policy"})
    if missing or unknown:
        _fail("invalid_shape", f"external trust policy keys mismatch; missing={missing}, unknown={unknown}")
    if policy["schema_version"] != "dps.external-verification-trust-policy/v1":
        _fail("unknown_trust_policy", "external trust policy schema version is unsupported")
    _external_id(policy["policy_id"], "external trust policy.policy_id")
    for key in ("trusted_issuers", "trusted_bom_signers", "environment_policies"):
        if len(_array(policy[key], f"external trust policy.{key}")) == 0:
            _fail("empty_trust_policy", f"external trust policy.{key} cannot be empty")
    if "prerequisite_receipt_policy" in policy:
        prerequisite = _object(policy["prerequisite_receipt_policy"], "prerequisite_receipt_policy")
        _exact_keys(
            prerequisite,
            {
                "repository_id",
                "maximum_age_seconds",
                "maximum_clock_skew_seconds",
                "revoked_receipt_ids",
                "required_source_evidence",
            },
            "prerequisite_receipt_policy",
        )
        repository_id = _text(prerequisite["repository_id"], "prerequisite_receipt_policy.repository_id")
        if REPOSITORY_ID_RE.fullmatch(repository_id) is None:
            _fail("invalid_repository_id", "prerequisite receipt repository_id is not canonical")
        maximum_age = _integer(
            prerequisite["maximum_age_seconds"], "prerequisite_receipt_policy.maximum_age_seconds", 1
        )
        if maximum_age > 31 * 24 * 3600:
            _fail("unsafe_prerequisite_policy", "prerequisite receipt age cannot exceed 31 days")
        maximum_skew = _integer(
            prerequisite["maximum_clock_skew_seconds"],
            "prerequisite_receipt_policy.maximum_clock_skew_seconds",
            0,
        )
        if maximum_skew > 300:
            _fail("unsafe_prerequisite_policy", "prerequisite receipt clock skew cannot exceed five minutes")
        revoked = _array(prerequisite["revoked_receipt_ids"], "prerequisite_receipt_policy.revoked_receipt_ids")
        revoked_ids = [
            _external_id(value, f"prerequisite_receipt_policy.revoked_receipt_ids[{index}]")
            for index, value in enumerate(revoked)
        ]
        if len(revoked_ids) != len(set(revoked_ids)):
            _fail("duplicate_revocation", "prerequisite receipt revocation ids must be unique")
        required_source = _object(
            prerequisite["required_source_evidence"],
            "prerequisite_receipt_policy.required_source_evidence",
        )
        _exact_keys(
            required_source,
            {
                "evidence_id",
                "evidence_sha256",
                "environment_id",
                "environment_sha256",
                "measurement_started_at",
                "measurement_finished_at",
                "edge_installation_id",
                "zenno_installation_id",
            },
            "prerequisite_receipt_policy.required_source_evidence",
        )
        _external_id(required_source["evidence_id"], "required_source_evidence.evidence_id")
        _sha256(required_source["evidence_sha256"], "required_source_evidence.evidence_sha256")
        if ENVIRONMENT_ID_RE.fullmatch(_text(required_source["environment_id"], "required_source_evidence.environment_id")) is None:
            _fail("prerequisite_environment_mismatch", "required F6 source environment id is not canonical")
        _sha256(required_source["environment_sha256"], "required_source_evidence.environment_sha256")
        _window(
            {
                "started_at": required_source["measurement_started_at"],
                "finished_at": required_source["measurement_finished_at"],
            },
            "required_source_evidence.measurement_window",
        )
        if (
            EDGE_INSTALLATION_ID_RE.fullmatch(_text(required_source["edge_installation_id"], "required_source_evidence.edge_installation_id")) is None
            or ZENNO_INSTALLATION_ID_RE.fullmatch(_text(required_source["zenno_installation_id"], "required_source_evidence.zenno_installation_id")) is None
        ):
            _fail("prerequisite_environment_mismatch", "required F6 Edge/Zenno installation ids are not canonical")
    return policy


def _trusted_key(
    entry: Mapping[str, Any],
    expected_keys: set[str],
    expected_algorithm: str,
    label: str,
) -> bytes:
    _exact_keys(entry, expected_keys, label)
    for id_field in ("runner_key_id", "key_id"):
        if id_field in entry:
            _external_id(entry[id_field], f"{label}.{id_field}")
    if entry["algorithm"] != expected_algorithm:
        _fail("unknown_signature_algorithm", f"{label} algorithm is unsupported")
    path, key_bytes = _regular_external_file(entry["public_key_pem_path"], f"{label}.public_key_pem_path")
    if sha256_bytes(key_bytes) != _sha256(entry["public_key_sha256"], f"{label}.public_key_sha256"):
        _fail("public_key_digest_mismatch", f"{label} public key digest is not trusted")
    del path
    return key_bytes


def _verify_release_bom(
    binding_value: Any,
    trust_policy: Mapping[str, Any],
    signature_verifier: Callable[[bytes, bytes, Any], None],
) -> Mapping[str, Any]:
    binding = _object(binding_value, "release_bom")
    _exact_keys(binding, {"bom_id", "status", "path", "sha256", "artifact_sha256"}, "release_bom")
    _external_id(binding["bom_id"], "release_bom.bom_id")
    if binding["status"] not in {"SIGNED", "DEPLOYING", "STABLE"}:
        _fail("unsigned_bom", "release_bom.status must be SIGNED, DEPLOYING, or STABLE")
    _, bom_bytes = _verify_file_binding(binding["path"], binding["sha256"], None, "release_bom.path")
    bom = _decode_json_object(bom_bytes, "Release BOM document")
    required = {
        "schema_version",
        "bom_id",
        "status",
        "modules",
        "rollout",
        "signature",
    }
    if not required.issubset(bom):
        _fail("invalid_bom", f"Release BOM is missing fields {sorted(required - set(bom))}")
    if bom["schema_version"] != "dps.release-bom/v1":
        _fail("unknown_bom_version", "Release BOM schema version is unsupported")
    if bom["bom_id"] != binding["bom_id"] or bom["status"] != binding["status"]:
        _fail("bom_binding_mismatch", "Release BOM id or status does not match the evidence envelope")
    artifact_sha256 = _sha256(binding["artifact_sha256"], "release_bom.artifact_sha256")
    rollout = _object(bom["rollout"], "Release BOM rollout")
    if rollout.get("shadow_artifact_sha256") != artifact_sha256:
        _fail("bom_artifact_mismatch", "Release BOM shadow artifact does not match the observed candidate")
    modules = _array(bom["modules"], "Release BOM modules")
    if not any(isinstance(module, Mapping) and module.get("sha256") == artifact_sha256 for module in modules):
        _fail("bom_artifact_mismatch", "candidate digest is not present in the Release BOM module set")
    signature = _object(bom["signature"], "Release BOM signature")
    _exact_keys(signature, {"algorithm", "key_id", "value"}, "Release BOM signature")
    _external_id(signature["key_id"], "Release BOM signature.key_id")
    if signature["algorithm"] != BOM_ALGORITHM:
        _fail("unknown_signature_algorithm", "Release BOM signature algorithm is unsupported")
    signer = _find_unique(
        _array(trust_policy["trusted_bom_signers"], "trusted_bom_signers"),
        lambda value: value.get("key_id") == signature["key_id"],
        "Release BOM signer",
    )
    signer_key_bytes = _trusted_key(
        signer,
        {"key_id", "algorithm", "public_key_pem_path", "public_key_sha256"},
        BOM_ALGORITHM,
        "Release BOM signer",
    )
    unsigned_bom = dict(bom)
    unsigned_bom.pop("signature")
    signature_verifier(
        signer_key_bytes,
        b"dps-release-bom/v1\n" + canonical_bytes(unsigned_bom),
        signature["value"],
    )
    return bom


def _validate_common(
    stage: str,
    evidence: Mapping[str, Any],
    trust_policy: Mapping[str, Any],
    signature_verifier: Callable[[bytes, bytes, Any], None],
) -> tuple[tuple[datetime, datetime], dict[str, dict[str, Any]], Mapping[str, Any]]:
    spec = STAGE_SPECS[stage]
    _exact_keys(evidence, _COMMON_KEYS, "evidence envelope")
    if "verification_level" in evidence:
        _fail("self_asserted_level", "external input must not self-assert a verification level")
    if evidence["schema_version"] != spec["schema_version"]:
        _fail("stage_schema_mismatch", "evidence schema does not match the requested stage")
    _external_id(evidence["evidence_id"], "evidence_id")
    if evidence["evidence_kind"] != "REAL_EXTERNAL":
        _fail("non_real_evidence", "mock, hosted, and simulated evidence cannot satisfy an external gate")
    if evidence["required"] is not True:
        _fail("optional_evidence", "external gate evidence must be required")
    _pass(evidence["status"], "evidence.status")
    if not isinstance(evidence["baseline_commit"], str) or GIT_OBJECT_RE.fullmatch(evidence["baseline_commit"]) is None:
        _fail("invalid_baseline", "baseline_commit must be a full lowercase Git object id")

    started, finished = _window(evidence["measurement_window"], "measurement_window")
    environment = _object(evidence["environment"], "environment")
    if "environment_id" not in environment or "os_family" not in environment:
        _fail("invalid_environment", "environment must include environment_id and os_family")
    environment_policy = _find_unique(
        _array(trust_policy["environment_policies"], "environment_policies"),
        lambda value: value.get("verification_level") == spec["verification_level"],
        "environment policy",
    )
    _exact_keys(environment_policy, {"verification_level", "required_claims"}, "environment policy")
    required_claims = _object(environment_policy["required_claims"], "environment policy.required_claims")
    expected_environment_keys = STAGE_ENVIRONMENT_KEYS[stage]
    _reject_sensitive_environment_claims(required_claims, "environment policy.required_claims")
    _reject_sensitive_environment_claims(environment, "environment")
    if set(required_claims) != expected_environment_keys:
        unknown = sorted(set(required_claims) - expected_environment_keys)
        missing = sorted(expected_environment_keys - set(required_claims))
        _fail(
            "unsafe_environment_policy",
            f"environment policy keys must match the stage allowlist; missing={missing}, unknown={unknown}",
        )
    if set(environment) != expected_environment_keys:
        unknown = sorted(set(environment) - expected_environment_keys)
        missing = sorted(expected_environment_keys - set(environment))
        _fail(
            "environment_claim_not_allowlisted",
            f"environment keys must exactly match the stage allowlist; missing={missing}, unknown={unknown}",
        )
    for key, expected in required_claims.items():
        _validate_environment_claim_grammar(key, expected, "environment policy.required_claims")
        _validate_environment_claim_grammar(key, environment[key], "environment")
        observed = environment.get(key)
        if type(observed) is not type(expected) or observed != expected:
            _fail("platform_mismatch", f"environment claim {key!r} does not match the trusted policy")
    if environment["os_family"] not in STAGE_OS_FAMILIES[stage]:
        _fail("platform_mismatch", f"environment os_family is not valid for stage {stage}")

    artifacts = _array(evidence["raw_artifacts"], "raw_artifacts")
    if not artifacts:
        _wait("raw_evidence_missing", "at least one raw external artifact is required")
    artifact_ids: set[str] = set()
    artifact_paths: set[Path] = set()
    artifact_bindings: dict[str, dict[str, Any]] = {}
    for index, item in enumerate(artifacts):
        artifact = _object(item, f"raw_artifacts[{index}]")
        _exact_keys(artifact, {"artifact_id", "path", "sha256", "size_bytes", "media_type"}, f"raw_artifacts[{index}]")
        artifact_id = _external_id(artifact["artifact_id"], f"raw_artifacts[{index}].artifact_id")
        if artifact_id in artifact_ids:
            _fail("duplicate_artifact", "raw artifact ids must be unique")
        artifact_ids.add(artifact_id)
        artifact_size = _integer(artifact["size_bytes"], f"raw_artifacts[{index}].size_bytes", 1)
        observed_path, observed_bytes = _verify_file_binding(
            artifact["path"], artifact["sha256"], artifact_size, f"raw_artifacts[{index}]"
        )
        if observed_path in artifact_paths:
            _fail("duplicate_artifact", "raw artifact paths must be unique")
        artifact_paths.add(observed_path)
        media_type = _text(artifact["media_type"], f"raw_artifacts[{index}].media_type")
        artifact_bindings[artifact_id] = {
            "sha256": artifact["sha256"],
            "media_type": media_type,
            "bytes": observed_bytes,
        }

    binding = _object(evidence["factory_binding"], "factory_binding")
    _exact_keys(
        binding,
        {
            "upgrade_stream_id",
            "instruction_receipt_id",
            "source_event_sha256",
            "implementer_identity",
            "evidence_issuer_identity",
            "release_approver_identity",
        },
        "factory_binding",
    )
    for key in ("upgrade_stream_id", "instruction_receipt_id"):
        _external_id(binding[key], f"factory_binding.{key}")
    _sha256(binding["source_event_sha256"], "factory_binding.source_event_sha256")
    roles = [
        _text(binding["implementer_identity"], "factory_binding.implementer_identity", 2),
        _text(binding["evidence_issuer_identity"], "factory_binding.evidence_issuer_identity", 2),
        _text(binding["release_approver_identity"], "factory_binding.release_approver_identity", 2),
    ]
    if len(set(roles)) != 3:
        _fail("role_separation_failed", "implementer, evidence issuer, and release approver must be distinct")

    release_bom = _verify_release_bom(evidence["release_bom"], trust_policy, signature_verifier)

    attestation = _object(evidence["attestation"], "attestation")
    _exact_keys(attestation, {"facts", "signature_base64"}, "attestation")
    facts = _object(attestation["facts"], "attestation.facts")
    expected_fact_keys = {
        "schema_version",
        "runner_key_id",
        "algorithm",
        "issued_at",
        "payload_sha256",
        "evidence_issuer_identity",
        "raw_artifacts_observed",
        "role_separation_verified",
        "real_environment_observed",
    }
    _exact_keys(facts, expected_fact_keys, "attestation.facts")
    if "verification_level" in facts:
        _fail("self_asserted_level", "attestation facts must not self-assert a verification level")
    if facts["schema_version"] != "1.0.0" or facts["algorithm"] != P1363_ALGORITHM:
        _fail("unknown_attestation", "runner attestation version or algorithm is unsupported")
    _external_id(facts["runner_key_id"], "attestation.facts.runner_key_id")
    _true(facts["raw_artifacts_observed"], "attestation.raw_artifacts_observed")
    _true(facts["role_separation_verified"], "attestation.role_separation_verified")
    _true(facts["real_environment_observed"], "attestation.real_environment_observed")
    if facts["evidence_issuer_identity"] != binding["evidence_issuer_identity"]:
        _fail("issuer_mismatch", "attestation issuer does not match the Factory evidence binding")
    evidence_without_attestation = dict(evidence)
    evidence_without_attestation.pop("attestation")
    expected_payload_sha256 = sha256_bytes(canonical_bytes(evidence_without_attestation))
    if facts["payload_sha256"] != expected_payload_sha256:
        _fail("attestation_binding_mismatch", "runner attestation does not bind the exact evidence payload")
    issued_at = _parse_utc(facts["issued_at"], "attestation.issued_at")
    if issued_at < finished or issued_at > finished + timedelta(minutes=5):
        _fail("stale_attestation", "runner attestation must be issued within five minutes after measurement completion")

    issuer = _find_unique(
        _array(trust_policy["trusted_issuers"], "trusted_issuers"),
        lambda value: value.get("runner_key_id") == facts["runner_key_id"]
        and value.get("issuer_identity") == facts["evidence_issuer_identity"],
        "external evidence issuer",
    )
    issuer_key_bytes = _trusted_key(
        issuer,
        {
            "issuer_identity",
            "runner_key_id",
            "algorithm",
            "public_key_pem_path",
            "public_key_sha256",
            "allowed_verification_levels",
        },
        P1363_ALGORITHM,
        "external evidence issuer",
    )
    allowed_levels = _array(issuer["allowed_verification_levels"], "allowed_verification_levels")
    if spec["verification_level"] not in allowed_levels:
        _fail("issuer_scope_mismatch", "trusted issuer is not authorized for the runner-computed target level")
    signing_payload = b"dps-external-runner-attestation/v1\n" + canonical_bytes(facts)
    signature_verifier(issuer_key_bytes, signing_payload, attestation["signature_base64"])
    return (started, finished), artifact_bindings, release_bom


def _validate_f6(
    payload_value: Any,
    outer_window: tuple[datetime, datetime],
    trusted_environment: Mapping[str, Any] | None,
) -> None:
    payload = _object(payload_value, "payload")
    _exact_keys(
        payload,
        {"capability_probe", "zenno_process", "ab_cycles", "observation_hours", "recovery_checks", "rollback"},
        "F6 payload",
    )
    capability = _object(payload["capability_probe"], "capability_probe")
    _exact_keys(
        capability,
        {
            "status",
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
        "capability_probe",
    )
    _pass(capability["status"], "capability_probe.status")
    windows_version = _text(capability["windows_version"], "capability_probe.windows_version")
    zenno_version = _text(capability["zennodroid_version"], "capability_probe.zennodroid_version")
    if ZENNO_VERSION_RE.fullmatch(zenno_version) is None:
        _fail("invalid_capability_probe", "capability_probe.zennodroid_version must be an exact bounded version")
    if trusted_environment is None or zenno_version != trusted_environment.get("zennodroid_version"):
        _fail("zennodroid_version_mismatch", "probed ZennoDroid version must match the trusted environment")
    for key in ("dotnet_framework_version", "csharp_language_version"):
        version = _text(capability[key], f"capability_probe.{key}")
        if re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,3}", version) is None:
            _fail("invalid_capability_probe", f"capability_probe.{key} must be an exact numeric version")
    bridge_abi = _text(capability["bridge_abi"], "capability_probe.bridge_abi")
    if re.fullmatch(r"dps\.zenno-bridge/v[1-9][0-9]*", bridge_abi) is None:
        _fail("invalid_capability_probe", "capability_probe.bridge_abi must be a versioned DPS bridge ABI")
    for key in (
        "codedom_compile",
        "gac_resolution",
        "dll_load",
        "zenno_project_load",
        "adb_authorization",
        "connection_continuity",
    ):
        _pass(capability[key], f"capability_probe.{key}")
    _integer(capability["adb_authorized_device_count"], "capability_probe.adb_authorized_device_count", 1)
    if capability["loopback_host"] != "127.0.0.1":
        _fail("unsafe_bridge_endpoint", "capability_probe.loopback_host must be 127.0.0.1")
    port = _integer(capability["loopback_port"], "capability_probe.loopback_port", 1024)
    if port > 65535:
        _fail("invalid_capability_probe", "capability_probe.loopback_port must be at most 65535")
    _true(capability["loopback_port_fixed"], "capability_probe.loopback_port_fixed")
    _true(capability["loopback_only"], "capability_probe.loopback_only")
    timeout = _number(capability["command_timeout_seconds"], "capability_probe.command_timeout_seconds", 0.001)
    if timeout > 300:
        _fail("invalid_capability_probe", "capability_probe.command_timeout_seconds must be at most 300")
    if capability["timeout_semantics"] != "FAIL_CLOSED":
        _fail("unsafe_timeout_semantics", "capability_probe.timeout_semantics must be FAIL_CLOSED")
    if capability["error_semantics"] != "NATIVE_ERROR_PRESERVED":
        _fail("unsafe_error_semantics", "capability_probe.error_semantics must preserve native errors")
    if trusted_environment is None:
        _fail("capability_environment_missing", "F6 capability probe requires the trust-pinned environment")
    trusted_capability_values = {
        "windows_version": windows_version,
        "zennodroid_version": zenno_version,
        "dotnet_framework_version": capability["dotnet_framework_version"],
        "csharp_language_version": capability["csharp_language_version"],
        "codedom_compile": capability["codedom_compile"],
        "gac_resolution": capability["gac_resolution"],
        "dll_load": capability["dll_load"],
        "zenno_project_load": capability["zenno_project_load"],
        "bridge_abi": bridge_abi,
        "adb_authorized_device_count": capability["adb_authorized_device_count"],
        "adb_authorization": capability["adb_authorization"],
        "loopback_host": capability["loopback_host"],
        "loopback_port": port,
        "loopback_port_fixed": capability["loopback_port_fixed"],
        "loopback_only": capability["loopback_only"],
        "command_timeout_seconds": capability["command_timeout_seconds"],
        "timeout_semantics": capability["timeout_semantics"],
        "error_semantics": capability["error_semantics"],
        "connection_continuity": capability["connection_continuity"],
    }
    for key, observed in trusted_capability_values.items():
        if type(observed) is not type(trusted_environment.get(key)) or observed != trusted_environment.get(key):
            _fail("capability_environment_mismatch", f"capability_probe.{key} must match the trusted environment")

    process = _object(payload["zenno_process"], "zenno_process")
    _exact_keys(
        process,
        {
            "pid_before",
            "pid_after",
            "started_at_before",
            "started_at_after",
            "observed_at_before",
            "observed_at_after",
        },
        "zenno_process",
    )
    pid_before = _integer(process["pid_before"], "zenno_process.pid_before", 1)
    pid_after = _integer(process["pid_after"], "zenno_process.pid_after", 1)
    started_before = _parse_utc(process["started_at_before"], "zenno_process.started_at_before")
    started_after = _parse_utc(process["started_at_after"], "zenno_process.started_at_after")
    if pid_before != pid_after or started_before != started_after:
        _fail("zenno_restarted", "ZennoDroid PID and process start time must remain unchanged")
    if started_before > outer_window[0]:
        _fail("zenno_process_time_invalid", "ZennoDroid process must have started no later than the measurement window")
    observed_before = _parse_utc(process["observed_at_before"], "zenno_process.observed_at_before")
    observed_after = _parse_utc(process["observed_at_after"], "zenno_process.observed_at_after")
    if observed_before != outer_window[0] or observed_after != outer_window[1]:
        _fail(
            "zenno_measurement_window_mismatch",
            "ZennoDroid before/after process observations must exactly bound the signed measurement window",
        )

    cycles = _array(payload["ab_cycles"], "ab_cycles")
    if len(cycles) < 100:
        _fail("threshold_not_met", "F6 requires at least 100 A/B switch and rollback cycles")
    expected_directions = ("A_TO_B", "B_TO_A")
    for index, item in enumerate(cycles, start=1):
        cycle = _object(item, f"ab_cycles[{index - 1}]")
        _exact_keys(
            cycle,
            {
                "sequence",
                "direction",
                "installed_digest_verified",
                "signature_verified",
                "self_test",
                "shadow_side_effect_count",
                "drain",
                "route_switch",
                "rollback_check",
            },
            f"ab_cycles[{index - 1}]",
        )
        if cycle["sequence"] != index:
            _fail("wave_sequence_invalid", "A/B cycle sequence must be contiguous and one-based")
        if cycle["direction"] != expected_directions[(index - 1) % 2]:
            _fail("wave_sequence_invalid", "A/B cycle directions must alternate A_TO_B then B_TO_A")
        _true(cycle["installed_digest_verified"], f"ab_cycles[{index - 1}].installed_digest_verified")
        _true(cycle["signature_verified"], f"ab_cycles[{index - 1}].signature_verified")
        _pass(cycle["self_test"], f"ab_cycles[{index - 1}].self_test")
        _zero(cycle["shadow_side_effect_count"], f"ab_cycles[{index - 1}].shadow_side_effect_count")
        for key in ("drain", "route_switch", "rollback_check"):
            _pass(cycle[key], f"ab_cycles[{index - 1}].{key}")

    observed_hours = _number(payload["observation_hours"], "observation_hours", 24)
    window_hours = (outer_window[1] - outer_window[0]).total_seconds() / 3600
    if window_hours < 24 or observed_hours > window_hours + 1e-9:
        _fail("time_threshold_not_met", "F6 raw measurement window must cover the declared 24-hour observation")
    recovery = _object(payload["recovery_checks"], "recovery_checks")
    recovery_keys = {
        "crash_window",
        "duplicate_delivery",
        "offline_recovery",
        "unknown_contract_rejected",
        "unknown_step_rejected",
    }
    _exact_keys(recovery, recovery_keys, "recovery_checks")
    for key in recovery_keys:
        _pass(recovery[key], f"recovery_checks.{key}")
    rollback = _object(payload["rollback"], "rollback")
    _exact_keys(rollback, {"status", "maximum_minutes", "old_bom_restored"}, "rollback")
    _pass(rollback["status"], "rollback.status")
    if _number(rollback["maximum_minutes"], "rollback.maximum_minutes", 0) > 5:
        _fail("rollback_too_slow", "F6 rollback must complete within five minutes")
    _true(rollback["old_bom_restored"], "rollback.old_bom_restored")


def _f7_raw_json_artifact(
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    artifact_id_value: Any,
    artifact_sha256_value: Any,
    label: str,
) -> tuple[str, Mapping[str, Any], bytes, str]:
    artifact_id, artifact = _raw_json_artifact(
        raw_artifacts,
        artifact_id_value,
        artifact_sha256_value,
        label,
    )
    if raw_artifacts is None:
        _fail("raw_artifact_index_missing", f"{label} requires the signed raw artifact index")
    binding = _object(raw_artifacts[artifact_id], f"raw artifact {artifact_id}")
    raw = binding["bytes"]
    if not isinstance(raw, bytes):
        _fail("invalid_raw_artifact", f"{label} raw artifact bytes are unavailable")
    if canonical_bytes(artifact) != raw:
        _fail("noncanonical_f7_artifact", f"{label} must be canonical UTF-8 JSON")
    return artifact_id, artifact, raw, str(binding["sha256"])


def _validate_f7_artifact_envelope(
    artifact_id: str,
    artifact: Mapping[str, Any],
    expected_kind: str,
    expected_scope: Mapping[str, str],
    outer_window: tuple[datetime, datetime],
    trusted_environment: Mapping[str, Any],
    evidence: Mapping[str, Any],
) -> tuple[Mapping[str, Any], tuple[datetime, datetime]]:
    _exact_keys(
        artifact,
        {
            "schema_version",
            "artifact_id",
            "artifact_kind",
            "producer",
            "environment",
            "captured_at",
            "scope",
            "content_summary",
            "content_sha256",
            "content",
        },
        f"F7 artifact {artifact_id}",
    )
    if artifact["schema_version"] != "dps.f7-raw-evidence-artifact/v1":
        _fail("unknown_f7_artifact_major", "F7 raw artifact schema major is unsupported")
    if artifact["artifact_id"] != artifact_id or artifact["artifact_kind"] != expected_kind:
        _fail("f7_artifact_binding_mismatch", "F7 raw artifact id or kind does not match its signed binding")

    producer = _object(artifact["producer"], f"F7 artifact {artifact_id}.producer")
    _exact_keys(producer, {"identity", "component", "version"}, f"F7 artifact {artifact_id}.producer")
    issuer = _object(evidence["factory_binding"], "factory_binding")["evidence_issuer_identity"]
    if (
        producer["identity"] != issuer
        or producer["component"] != trusted_environment.get("runner_component")
    ):
        _fail("f7_artifact_producer_mismatch", "F7 artifact producer must be the bound external evidence issuer")
    version = _text(producer["version"], f"F7 artifact {artifact_id}.producer.version")
    if SEMVER_RE.fullmatch(version) is None or version != trusted_environment.get("runner_version"):
        _fail("f7_artifact_producer_mismatch", "F7 artifact producer version must be SemVer")

    artifact_environment = _object(artifact["environment"], f"F7 artifact {artifact_id}.environment")
    _exact_keys(artifact_environment, set(trusted_environment), f"F7 artifact {artifact_id}.environment")
    if artifact_environment != trusted_environment or artifact_environment.get("os_family") != "Windows+Android":
        _fail("f7_artifact_environment_mismatch", "F7 artifact environment must match the signed Windows+Android environment")

    captured = _object(artifact["captured_at"], f"F7 artifact {artifact_id}.captured_at")
    started, finished = _window(captured, f"F7 artifact {artifact_id}.captured_at")
    if started < outer_window[0] or finished > outer_window[1]:
        _fail("f7_artifact_time_mismatch", "F7 artifact capture window lies outside the signed measurement window")

    scope = _object(artifact["scope"], f"F7 artifact {artifact_id}.scope")
    _exact_keys(
        scope,
        {"soul_id", "device_binding_id", "platform_account_id", "logical_source_id", "external_source_alias"},
        f"F7 artifact {artifact_id}.scope",
    )
    if dict(scope) != dict(expected_scope):
        _fail("f7_artifact_scope_mismatch", "F7 artifact scope does not match its declared Soul/device/account/Source tuple")
    if artifact["content_summary"] != F7_ARTIFACT_SUMMARIES[expected_kind]:
        _fail("f7_artifact_summary_mismatch", "F7 artifact content summary is not the fixed redacted summary for its kind")
    content = _object(artifact["content"], f"F7 artifact {artifact_id}.content")
    content_sha256 = _sha256(artifact["content_sha256"], f"F7 artifact {artifact_id}.content_sha256")
    if sha256_bytes(canonical_bytes(content)) != content_sha256:
        _fail("f7_artifact_content_digest_mismatch", "F7 artifact content digest is not independently reproducible")
    return content, (started, finished)


def _validate_f7_projection_artifact_content(
    content: Mapping[str, Any],
    scope: Mapping[str, str],
    source_binding_nonce: int,
    artifact_id: str,
    run_context: Mapping[str, Any],
    capture_window: tuple[datetime, datetime],
) -> tuple[str, str, str, str]:
    required = {
        "projection_revision",
        "written_checksum",
        "read_checksum",
        "source_binding_sha256",
        "source_binding_base64",
        "written_projection_base64",
        "readback_projection_base64",
    }
    missing = sorted((required | F7_OBSERVATION_COMMON_KEYS) - set(content))
    unknown = sorted(set(content) - required - F7_OBSERVATION_COMMON_KEYS - {"external_revision"})
    if missing or unknown:
        _fail("invalid_projection_artifact", f"projection artifact content keys mismatch; missing={missing}, unknown={unknown}")
    _validate_f7_observation_common(
        content,
        F7_PROJECTION_KIND,
        scope,
        run_context,
        capture_window,
        f"F7 artifact {artifact_id}.content",
    )
    revision = _sha256(content["projection_revision"], f"F7 artifact {artifact_id}.projection_revision")
    if "external_revision" in content:
        _external_revision(content["external_revision"], f"F7 artifact {artifact_id}.external_revision")
    written = _sha256(content["written_checksum"], f"F7 artifact {artifact_id}.written_checksum")
    read = _sha256(content["read_checksum"], f"F7 artifact {artifact_id}.read_checksum")
    if written != read:
        _fail("gbrain_checksum_mismatch", "GBrain exact readback checksum does not match the written projection")
    written_projection = _base64_content(
        content["written_projection_base64"], f"F7 artifact {artifact_id}.written_projection_base64"
    )
    readback_projection = _base64_content(
        content["readback_projection_base64"], f"F7 artifact {artifact_id}.readback_projection_base64"
    )
    if sha256_bytes(written_projection) != written or sha256_bytes(readback_projection) != read:
        _fail("projection_content_digest_mismatch", "projection checksums must be recomputable from the bound bytes")
    if written_projection != readback_projection:
        _fail("gbrain_exact_readback_mismatch", "bound GBrain write and readback bytes are not exact")
    source_binding_sha256 = _sha256(
        content["source_binding_sha256"],
        f"F7 artifact {artifact_id}.source_binding_sha256",
    )
    source_binding_bytes = _base64_content(
        content["source_binding_base64"],
        f"F7 artifact {artifact_id}.source_binding_base64",
    )
    if sha256_bytes(source_binding_bytes) != source_binding_sha256:
        _fail("source_binding_content_digest_mismatch", "Source binding digest is not reproducible from bound bytes")
    verified_binding = _validate_gbrain_source_binding_content(
        source_binding_bytes,
        scope,
        source_binding_nonce,
        f"F7 artifact {artifact_id}.source_binding",
    )
    projection_checksum = _validate_gbrain_projection_content(
        written_projection,
        {
            "soul_id": scope["soul_id"],
            "device_binding_id": scope["device_binding_id"],
            "platform_account_id": scope["platform_account_id"],
            "logical_source_id": scope["logical_source_id"],
            "trace_id": run_context["trace_id"],
            "projection_revision": revision,
        },
        source_binding_nonce,
        verified_binding,
        f"F7 artifact {artifact_id}.projection",
    )
    return revision, projection_checksum, verified_binding[0], verified_binding[1]


def _validate_f7_search_artifact_content(
    content: Mapping[str, Any],
    scope: Mapping[str, str],
    artifact_id: str,
    outer_window: tuple[datetime, datetime],
    verified_projection: tuple[str, str],
    run_context: Mapping[str, Any],
    capture_window: tuple[datetime, datetime],
) -> None:
    keys = {
        "result_schema_version",
        "provenance",
        "observed_at",
        "freshness_seconds",
        "query_sha256",
        "response_sha256",
        "query_base64",
        "response_base64",
        "matched_result_count",
    }
    _exact_keys(content, keys | F7_OBSERVATION_COMMON_KEYS, f"F7 artifact {artifact_id}.content")
    _validate_f7_observation_common(
        content,
        F7_SEARCH_KIND,
        scope,
        run_context,
        capture_window,
        f"F7 artifact {artifact_id}.content",
    )
    if content["result_schema_version"] != "gbrain.search-result/v1":
        _fail("search_schema_mismatch", "search readback result schema major is not supported")
    if content["provenance"] != "SOURCE_SCOPED_EXTERNAL_READBACK":
        _fail("search_provenance_mismatch", "search readback provenance is not source-scoped external evidence")
    observed_at = _parse_utc(content["observed_at"], f"F7 artifact {artifact_id}.observed_at")
    freshness = _integer(content["freshness_seconds"], f"F7 artifact {artifact_id}.freshness_seconds", 0)
    if (
        observed_at < outer_window[0]
        or observed_at > outer_window[1]
        or freshness > 300
        or outer_window[1] - observed_at != timedelta(seconds=freshness)
    ):
        _fail("search_result_stale", "search readback must be observed within 300 seconds of measurement completion")
    query_sha = _sha256(content["query_sha256"], f"F7 artifact {artifact_id}.query_sha256")
    response_sha = _sha256(content["response_sha256"], f"F7 artifact {artifact_id}.response_sha256")
    query_bytes = _base64_content(content["query_base64"], f"F7 artifact {artifact_id}.query_base64")
    response_bytes = _base64_content(content["response_base64"], f"F7 artifact {artifact_id}.response_base64")
    if sha256_bytes(query_bytes) != query_sha or sha256_bytes(response_bytes) != response_sha:
        _fail("search_content_digest_mismatch", "Search query/response digests must be recomputable from bound bytes")
    query = _decode_json_object(query_bytes, f"F7 artifact {artifact_id}.query")
    expected_query = {
        "schema_version": "dps.gbrain-source-search-query/v1",
        "soul_id": scope["soul_id"],
        "logical_source_id": scope["logical_source_id"],
        "external_source_alias": scope["external_source_alias"],
        "result_schema_version": content["result_schema_version"],
    }
    if query != expected_query or canonical_bytes(query) != query_bytes:
        _fail("search_query_scope_mismatch", "bound Search query is noncanonical or does not match the Source/Soul tuple")
    response = _decode_json_object(response_bytes, f"F7 artifact {artifact_id}.response")
    _exact_keys(
        response,
        {"schema_version", "soul_id", "logical_source_id", "external_source_alias", "provenance", "observed_at", "results"},
        f"F7 artifact {artifact_id}.response",
    )
    expected_response = {
        "schema_version": content["result_schema_version"],
        "soul_id": scope["soul_id"],
        "logical_source_id": scope["logical_source_id"],
        "external_source_alias": scope["external_source_alias"],
        "provenance": content["provenance"],
        "observed_at": content["observed_at"],
    }
    for key, expected in expected_response.items():
        if response.get(key) != expected:
            _fail("search_response_scope_mismatch", f"bound Search response {key} does not match")
    results = _array(response["results"], f"F7 artifact {artifact_id}.response.results")
    matched_count = _integer(content["matched_result_count"], f"F7 artifact {artifact_id}.matched_result_count", 1)
    if len(results) != matched_count:
        _fail("search_result_count_mismatch", "matched_result_count must equal the bound response result count")
    revision, checksum = verified_projection
    for index, value in enumerate(results):
        result = _object(value, f"F7 artifact {artifact_id}.results[{index}]")
        _exact_keys(
            result,
            {"soul_id", "logical_source_id", "external_source_alias", "projection_revision", "projection_checksum"},
            f"F7 artifact {artifact_id}.results[{index}]",
        )
        if (
            result["soul_id"] != scope["soul_id"]
            or result["logical_source_id"] != scope["logical_source_id"]
            or result["external_source_alias"] != scope["external_source_alias"]
        ):
            _fail("search_response_scope_mismatch", "Search response contains a cross-Soul or cross-Source result")
        if result["projection_revision"] != revision or result["projection_checksum"] != checksum:
            _fail("search_result_projection_mismatch", "Search result does not match the current verified projection")
    if canonical_bytes(response) != response_bytes:
        _fail("noncanonical_search_content", "bound Search response must be canonical UTF-8 JSON")


def _f7_observation_schema_version(kind: str) -> str:
    slug = kind.lower().replace("_", "-")
    return f"dps.f7-observation/{slug}/v1"


def _unwrap_f7_semantic_observation(
    content: Mapping[str, Any],
    kind: str,
    artifact_id: str,
) -> Mapping[str, Any]:
    label = f"F7 artifact {artifact_id}.content"
    _exact_keys(
        content,
        {"observation_schema_version", "observation_sha256", "observation_base64"},
        label,
    )
    if content["observation_schema_version"] != _f7_observation_schema_version(kind):
        _fail("f7_observation_schema_mismatch", "semantic observation schema does not match artifact kind")
    expected_digest = _sha256(content["observation_sha256"], f"{label}.observation_sha256")
    raw = _base64_content(content["observation_base64"], f"{label}.observation_base64", 1024 * 1024)
    if sha256_bytes(raw) != expected_digest:
        _fail("f7_observation_digest_mismatch", "semantic observation digest is not reproducible from raw bytes")
    observation = _decode_json_object(raw, f"F7 artifact {artifact_id}.observation")
    if canonical_bytes(observation) != raw:
        _fail("noncanonical_f7_observation", "semantic observation must be canonical UTF-8 JSON")
    return observation


def _validate_f7_observation_common(
    observation: Mapping[str, Any],
    kind: str,
    scope: Mapping[str, str],
    run_context: Mapping[str, Any],
    capture_window: tuple[datetime, datetime],
    label: str,
) -> None:
    for key in F7_OBSERVATION_COMMON_KEYS:
        if key not in observation:
            _fail("f7_observation_chain_missing", f"{label} is missing common chain field {key}")
    expected_phase = F7_PHASE_BY_ARTIFACT_KIND[kind]
    if (
        observation["f7_run_id"] != run_context["f7_run_id"]
        or observation["trace_id"] != run_context["trace_id"]
        or observation["release_bom_id"] != run_context["release_bom_id"]
        or observation["release_bom_sha256"] != run_context["release_bom_sha256"]
        or observation["phase"] != expected_phase
    ):
        _fail("f7_observation_chain_mismatch", "raw observation does not bind the signed run, trace, BOM, and phase")
    _external_id(observation["f7_run_id"], f"{label}.f7_run_id")
    _canonical_identifier(observation["trace_id"], TRACE_ID_RE, f"{label}.trace_id")
    _external_id(observation["release_bom_id"], f"{label}.release_bom_id")
    _sha256(observation["release_bom_sha256"], f"{label}.release_bom_sha256")
    expected_scope_digest = sha256_bytes(canonical_bytes(scope))
    if observation["scope_sha256"] != expected_scope_digest:
        _fail("f7_observation_scope_digest_mismatch", "raw observation scope digest is not reproducible")
    phase_window = run_context["phases"][expected_phase]
    observed_at = _parse_utc(observation["observed_at"], f"{label}.observed_at")
    if (
        capture_window[0] < phase_window[0]
        or capture_window[1] > phase_window[1]
        or observed_at < capture_window[0]
        or observed_at > capture_window[1]
    ):
        _fail("f7_observation_time_mismatch", "raw observation capture is outside its strictly ordered phase")


def _validate_f7_semantic_exchange(
    observation: Mapping[str, Any],
    kind: str,
    scope: Mapping[str, str],
    label: str,
) -> tuple[Mapping[str, Any], Mapping[str, Any], Mapping[str, Any]]:
    decoded: list[Mapping[str, Any]] = []
    request_id: str | None = None
    scope_sha256 = sha256_bytes(canonical_bytes(scope))
    expected_response_outcome = (
        "DENIED"
        if kind in F7_ATTACK_ARTIFACT_KINDS
        else "UNKNOWN_OUTCOME"
        if kind == "UNKNOWN_OUTCOME_RECONCILIATION"
        else "OBSERVED"
    )
    for exchange_kind in ("request", "response", "postcondition"):
        raw = _base64_content(
            observation[f"{exchange_kind}_base64"],
            f"{label}.{exchange_kind}_base64",
            1024 * 1024,
        )
        expected_digest = _sha256(
            observation[f"{exchange_kind}_sha256"],
            f"{label}.{exchange_kind}_sha256",
        )
        if sha256_bytes(raw) != expected_digest:
            _fail(
                "f7_exchange_digest_mismatch",
                f"{exchange_kind} digest is not reproducible from the bound raw bytes",
            )
        document = _decode_json_object(raw, f"{label}.{exchange_kind}")
        if canonical_bytes(document) != raw:
            _fail("noncanonical_f7_exchange", "F7 request/response/postcondition bytes must be canonical JSON")
        expected_keys = {
            "schema_version",
            "request_id",
            "artifact_kind",
            "f7_run_id",
            "trace_id",
            "scope_sha256",
            "payload",
        }
        if exchange_kind != "request":
            expected_keys.add("outcome")
        _exact_keys(document, expected_keys, f"{label}.{exchange_kind}")
        if document["schema_version"] != f"dps.f7-{exchange_kind}-record/v1":
            _fail("f7_exchange_schema_mismatch", "F7 raw exchange record schema is unsupported")
        current_request_id = _external_id(document["request_id"], f"{label}.{exchange_kind}.request_id")
        if request_id is None:
            request_id = current_request_id
        if (
            current_request_id != request_id
            or document["artifact_kind"] != kind
            or document["f7_run_id"] != observation["f7_run_id"]
            or document["trace_id"] != observation["trace_id"]
            or document["scope_sha256"] != scope_sha256
        ):
            _fail(
                "f7_exchange_chain_mismatch",
                "raw request, response, and postcondition must bind one run, trace, scope, kind, and request",
            )
        if exchange_kind != "request" and document["outcome"] not in {
            "OBSERVED",
            "VERIFIED",
            "DENIED",
            "UNKNOWN_OUTCOME",
        }:
            _fail("f7_exchange_outcome_mismatch", "raw exchange outcome is unsupported")
        if exchange_kind == "response" and document["outcome"] != expected_response_outcome:
            _fail(
                "f7_exchange_outcome_mismatch",
                "raw response outcome is inconsistent with the evidence kind",
            )
        if exchange_kind == "postcondition" and document["outcome"] != "VERIFIED":
            _fail(
                "f7_exchange_outcome_mismatch",
                "raw postcondition must be independently VERIFIED",
            )
        decoded.append(_object(document["payload"], f"{label}.{exchange_kind}.payload"))
    return decoded[0], decoded[1], decoded[2]


def _validate_f7_semantic_content(
    kind: str,
    content: Mapping[str, Any],
    scope: Mapping[str, str],
    all_scopes: Mapping[str, Mapping[str, str]],
    artifact_id: str,
    run_context: Mapping[str, Any],
    capture_window: tuple[datetime, datetime],
) -> Mapping[str, Any]:
    content = _unwrap_f7_semantic_observation(content, kind, artifact_id)
    label = f"F7 artifact {artifact_id}.observation"
    _validate_f7_observation_common(content, kind, scope, run_context, capture_window, label)
    request_payload, response_payload, postcondition_payload = _validate_f7_semantic_exchange(
        content,
        kind,
        scope,
        label,
    )

    def exact_kind_keys(keys: set[str]) -> None:
        _exact_keys(
            content,
            keys | F7_OBSERVATION_COMMON_KEYS | F7_SEMANTIC_RAW_EXCHANGE_KEYS,
            label,
        )

    if kind == "SOUL_DEVICE_SOURCE_OAUTH_BINDING":
        keys = {
            "device_transport",
            "adb_serial_hmac_sha256",
            "device_attestation_sha256",
            "ownership_authorization_sha256",
            "inventory_class",
            "oauth_client_id_sha256",
            "oauth_credential_lease_id",
            "oauth_token_fingerprint_sha256",
            "expected_full_soul_metadata_sha256",
            "observed_full_soul_metadata_sha256",
            "oauth_write_source_id",
            "oauth_read_source_ids",
            "oauth_whoami_source_id",
            "oauth_write_source_alias",
            "oauth_read_source_aliases",
            "oauth_whoami_source_alias",
            "oauth_readable_source_count",
            "oauth_write_source_count",
            "source_binding_nonce",
            "source_binding_revision",
            "source_binding_checksum",
        }
        exact_kind_keys(keys)
        for key in (
            "adb_serial_hmac_sha256",
            "device_attestation_sha256",
            "ownership_authorization_sha256",
            "oauth_client_id_sha256",
            "oauth_token_fingerprint_sha256",
            "expected_full_soul_metadata_sha256",
            "observed_full_soul_metadata_sha256",
            "source_binding_revision",
            "source_binding_checksum",
        ):
            _sha256(content[key], f"{label}.{key}")
        _external_id(content["oauth_credential_lease_id"], f"{label}.oauth_credential_lease_id")
        source_binding_nonce = _integer(content["source_binding_nonce"], f"{label}.source_binding_nonce", 0)
        if source_binding_nonce > 1023:
            _fail("source_binding_nonce_invalid", "OAuth Source binding nonce exceeds 1023")
        source_ids = _array(content["oauth_read_source_ids"], f"{label}.oauth_read_source_ids")
        aliases = _array(content["oauth_read_source_aliases"], f"{label}.oauth_read_source_aliases")
        _exact_keys(
            request_payload,
            {
                "operation",
                "device_transport",
                "oauth_credential_lease_id",
                "requested_source_id",
                "requested_source_alias",
            },
            f"{label}.request.payload",
        )
        _exact_keys(
            response_payload,
            {
                "adb_probe",
                "oauth_whoami",
                "readable_source_ids",
                "writable_source_ids",
                "readable_source_aliases",
                "writable_source_aliases",
                "observed_full_soul_metadata_sha256",
                "source_binding_revision",
                "source_binding_checksum",
            },
            f"{label}.response.payload",
        )
        adb_probe = _object(response_payload["adb_probe"], f"{label}.response.payload.adb_probe")
        _exact_keys(
            adb_probe,
            {"serial_hmac_sha256", "attestation_sha256", "ownership_authorization_sha256", "inventory_class"},
            f"{label}.response.payload.adb_probe",
        )
        oauth_whoami = _object(response_payload["oauth_whoami"], f"{label}.response.payload.oauth_whoami")
        _exact_keys(
            oauth_whoami,
            {
                "client_id_sha256",
                "credential_lease_id",
                "token_fingerprint_sha256",
                "source_id",
                "source_alias",
                "source_binding_nonce",
                "source_binding_revision",
                "source_binding_checksum",
            },
            f"{label}.response.payload.oauth_whoami",
        )
        _exact_keys(
            postcondition_payload,
            {"binding_verified", "expected_full_soul_metadata_sha256", "observed_full_soul_metadata_sha256"},
            f"{label}.postcondition.payload",
        )
        if (
            content["device_transport"] != "PHYSICAL_ADB"
            or content["inventory_class"] != "NON_PRODUCTION"
            or content["expected_full_soul_metadata_sha256"] != content["observed_full_soul_metadata_sha256"]
            or content["oauth_write_source_id"] != scope["logical_source_id"]
            or source_ids != [scope["logical_source_id"]]
            or content["oauth_whoami_source_id"] != scope["logical_source_id"]
            or content["oauth_write_source_alias"] != scope["external_source_alias"]
            or aliases != [scope["external_source_alias"]]
            or content["oauth_whoami_source_alias"] != scope["external_source_alias"]
            or content["oauth_readable_source_count"] != 1
            or content["oauth_write_source_count"] != 1
            or request_payload
            != {
                "operation": "VERIFY_DEVICE_SOURCE_OAUTH_BINDING",
                "device_transport": content["device_transport"],
                "oauth_credential_lease_id": content["oauth_credential_lease_id"],
                "requested_source_id": scope["logical_source_id"],
                "requested_source_alias": scope["external_source_alias"],
            }
            or adb_probe
            != {
                "serial_hmac_sha256": content["adb_serial_hmac_sha256"],
                "attestation_sha256": content["device_attestation_sha256"],
                "ownership_authorization_sha256": content["ownership_authorization_sha256"],
                "inventory_class": content["inventory_class"],
            }
            or oauth_whoami
            != {
                "client_id_sha256": content["oauth_client_id_sha256"],
                "credential_lease_id": content["oauth_credential_lease_id"],
                "token_fingerprint_sha256": content["oauth_token_fingerprint_sha256"],
                "source_id": content["oauth_whoami_source_id"],
                "source_alias": content["oauth_whoami_source_alias"],
                "source_binding_nonce": content["source_binding_nonce"],
                "source_binding_revision": content["source_binding_revision"],
                "source_binding_checksum": content["source_binding_checksum"],
            }
            or response_payload["readable_source_ids"] != source_ids
            or response_payload["writable_source_ids"] != [content["oauth_write_source_id"]]
            or response_payload["readable_source_aliases"] != aliases
            or response_payload["writable_source_aliases"] != [content["oauth_write_source_alias"]]
            or response_payload["observed_full_soul_metadata_sha256"]
            != content["observed_full_soul_metadata_sha256"]
            or response_payload["source_binding_revision"] != content["source_binding_revision"]
            or response_payload["source_binding_checksum"] != content["source_binding_checksum"]
            or postcondition_payload
            != {
                "binding_verified": True,
                "expected_full_soul_metadata_sha256": content["expected_full_soul_metadata_sha256"],
                "observed_full_soul_metadata_sha256": content["observed_full_soul_metadata_sha256"],
            }
        ):
            _fail("source_oauth_binding_mismatch", "raw device/Source/OAuth observations do not prove one-Soul isolation")
        return dict(content)
    if kind == "PERSONA_EXACT_CURRENT_READBACK":
        keys = {
            "read_mode",
            "fixed_slug",
            "semantic_search_invocation_count",
            "persona_schema_version",
            "expected_revision",
            "read_revision",
            "expected_checksum",
            "read_checksum",
            "expected_content_sha256",
            "read_content_sha256",
        }
        exact_kind_keys(keys)
        for key in (
            "expected_revision",
            "read_revision",
            "expected_checksum",
            "read_checksum",
            "expected_content_sha256",
            "read_content_sha256",
        ):
            _sha256(content[key], f"{label}.{key}")
        _exact_keys(
            request_payload,
            {"read_mode", "fixed_slug", "semantic_search_invocations"},
            f"{label}.request.payload",
        )
        _exact_keys(
            response_payload,
            {"persona_schema_version", "revision", "checksum", "persona_base64"},
            f"{label}.response.payload",
        )
        _exact_keys(
            postcondition_payload,
            {"expected_persona_base64", "read_persona_base64"},
            f"{label}.postcondition.payload",
        )
        response_persona = _base64_content(response_payload["persona_base64"], f"{label}.response.persona_base64")
        expected_persona = _base64_content(
            postcondition_payload["expected_persona_base64"],
            f"{label}.postcondition.expected_persona_base64",
        )
        read_persona = _base64_content(
            postcondition_payload["read_persona_base64"],
            f"{label}.postcondition.read_persona_base64",
        )
        persona_document = _decode_json_object(response_persona, f"{label}.response.persona")
        _exact_keys(
            persona_document,
            {"schema_version", "soul_id", "persona_revision", "traits"},
            f"{label}.response.persona",
        )
        if (
            content["read_mode"] != "EXACT_FIXED_SLUG"
            or content["fixed_slug"] != "persona-current"
            or content["semantic_search_invocation_count"] != 0
            or content["persona_schema_version"] != "dps.persona-current/v1"
            or content["expected_revision"] != content["read_revision"]
            or content["expected_checksum"] != content["read_checksum"]
            or content["expected_content_sha256"] != content["read_content_sha256"]
            or request_payload
            != {
                "read_mode": content["read_mode"],
                "fixed_slug": content["fixed_slug"],
                "semantic_search_invocations": [],
            }
            or response_payload["persona_schema_version"] != content["persona_schema_version"]
            or response_payload["revision"] != content["read_revision"]
            or response_payload["checksum"] != content["read_checksum"]
            or response_persona != expected_persona
            or response_persona != read_persona
            or canonical_bytes(persona_document) != response_persona
            or persona_document["schema_version"] != content["persona_schema_version"]
            or persona_document["soul_id"] != scope["soul_id"]
            or persona_document["persona_revision"] != content["read_revision"]
            or sha256_bytes(response_persona) != content["read_checksum"]
            or sha256_bytes(response_persona) != content["read_content_sha256"]
        ):
            _fail("persona_exact_readback_mismatch", "Persona current must use deterministic exact readback and match DPS truth")
        return dict(content)
    if kind == "DELETE_REBUILD_PURGE":
        keys = {
            "delete_request_id",
            "pre_delete_projection_checksum",
            "delete_observed_revision",
            "page_count_after_delete",
            "chunk_count_after_delete",
            "embedding_count_after_delete",
            "cache_entry_count_after_delete",
            "backup_reference_count_after_delete",
            "backup_policy_id",
            "rebuild_request_id",
            "expected_rebuild_revision",
            "readback_rebuild_revision",
            "expected_rebuild_checksum",
            "readback_rebuild_checksum",
            "rebuild_page_count",
        }
        exact_kind_keys(keys)
        for key in ("delete_request_id", "backup_policy_id", "rebuild_request_id"):
            _external_id(content[key], f"{label}.{key}")
        for key in (
            "pre_delete_projection_checksum",
            "delete_observed_revision",
            "expected_rebuild_revision",
            "readback_rebuild_revision",
            "expected_rebuild_checksum",
            "readback_rebuild_checksum",
        ):
            _sha256(content[key], f"{label}.{key}")
        for key in (
            "page_count_after_delete",
            "chunk_count_after_delete",
            "embedding_count_after_delete",
            "cache_entry_count_after_delete",
            "backup_reference_count_after_delete",
        ):
            _zero(content[key], f"{label}.{key}")
        _exact_keys(
            request_payload,
            {"delete_request_id", "rebuild_request_id", "pre_delete_projection_checksum"},
            f"{label}.request.payload",
        )
        _exact_keys(
            response_payload,
            {"delete_observed_revision", "backup_policy_id", "remaining"},
            f"{label}.response.payload",
        )
        remaining = _object(response_payload["remaining"], f"{label}.response.payload.remaining")
        remaining_keys = {"pages", "chunks", "embeddings", "cache_entries", "backup_references"}
        _exact_keys(remaining, remaining_keys, f"{label}.response.payload.remaining")
        _exact_keys(
            postcondition_payload,
            {"expected_rebuild_revision", "readback_rebuild_revision", "expected_rebuild_checksum", "readback_rebuild_checksum", "rebuilt_pages"},
            f"{label}.postcondition.payload",
        )
        rebuilt_pages = _array(postcondition_payload["rebuilt_pages"], f"{label}.postcondition.rebuilt_pages")
        if (
            content["expected_rebuild_revision"] != content["readback_rebuild_revision"]
            or content["expected_rebuild_checksum"] != content["readback_rebuild_checksum"]
            or content["rebuild_page_count"] != 1
            or request_payload
            != {
                "delete_request_id": content["delete_request_id"],
                "rebuild_request_id": content["rebuild_request_id"],
                "pre_delete_projection_checksum": content["pre_delete_projection_checksum"],
            }
            or response_payload["delete_observed_revision"] != content["delete_observed_revision"]
            or response_payload["backup_policy_id"] != content["backup_policy_id"]
            or any(_array(remaining[key], f"{label}.remaining.{key}") for key in remaining_keys)
            or postcondition_payload["expected_rebuild_revision"] != content["expected_rebuild_revision"]
            or postcondition_payload["readback_rebuild_revision"] != content["readback_rebuild_revision"]
            or postcondition_payload["expected_rebuild_checksum"] != content["expected_rebuild_checksum"]
            or postcondition_payload["readback_rebuild_checksum"] != content["readback_rebuild_checksum"]
            or len(rebuilt_pages) != content["rebuild_page_count"]
        ):
            _fail("delete_rebuild_mismatch", "delete/rebuild observations do not prove purge and exact reconstruction")
        rebuilt_page = _object(rebuilt_pages[0], f"{label}.postcondition.rebuilt_pages[0]")
        _exact_keys(
            rebuilt_page,
            {"external_source_alias", "projection_revision", "projection_checksum"},
            f"{label}.postcondition.rebuilt_pages[0]",
        )
        if rebuilt_page != {
            "external_source_alias": scope["external_source_alias"],
            "projection_revision": content["readback_rebuild_revision"],
            "projection_checksum": content["readback_rebuild_checksum"],
        }:
            _fail("delete_rebuild_mismatch", "rebuilt page does not match the verified Soul Source projection")
        return dict(content)
    if kind == "DATA_SUBJECT_EXPORT":
        exact_kind_keys({"request_id", "expected_scope_sha256", "exported_scope_sha256", "exported_record_count", "foreign_scope_record_count"})
        _external_id(content["request_id"], f"{label}.request_id")
        expected = _sha256(content["expected_scope_sha256"], f"{label}.expected_scope_sha256")
        observed = _sha256(content["exported_scope_sha256"], f"{label}.exported_scope_sha256")
        _integer(content["exported_record_count"], f"{label}.exported_record_count", 1)
        _zero(content["foreign_scope_record_count"], f"{label}.foreign_scope_record_count")
        _exact_keys(request_payload, {"target_scope_sha256"}, f"{label}.request.payload")
        _exact_keys(response_payload, {"records"}, f"{label}.response.payload")
        _exact_keys(postcondition_payload, {"foreign_scope_records"}, f"{label}.postcondition.payload")
        records = _array(response_payload["records"], f"{label}.response.payload.records")
        foreign_records = _array(
            postcondition_payload["foreign_scope_records"],
            f"{label}.postcondition.payload.foreign_scope_records",
        )
        scope_digest = sha256_bytes(canonical_bytes(scope))
        for index, record_value in enumerate(records):
            record = _object(record_value, f"{label}.response.payload.records[{index}]")
            _exact_keys(record, {"record_id", "scope_sha256"}, f"{label}.response.payload.records[{index}]")
            _external_id(record["record_id"], f"{label}.response.payload.records[{index}].record_id")
            if record["scope_sha256"] != scope_digest:
                _fail("data_subject_export_mismatch", "export response contains a foreign-scope record")
        if (
            expected != observed
            or expected != scope_digest
            or request_payload["target_scope_sha256"] != expected
            or len(records) != content["exported_record_count"]
            or len(foreign_records) != content["foreign_scope_record_count"]
        ):
            _fail("data_subject_export_mismatch", "exported data does not match the requested Soul scope")
        return dict(content)
    if kind == "DATA_SUBJECT_CORRECTION":
        exact_kind_keys({"request_id", "correction_event_id", "before_revision", "expected_after_revision", "observed_after_revision", "stale_live_record_count", "foreign_scope_write_count"})
        _external_id(content["request_id"], f"{label}.request_id")
        _external_id(content["correction_event_id"], f"{label}.correction_event_id")
        before = _sha256(content["before_revision"], f"{label}.before_revision")
        expected = _sha256(content["expected_after_revision"], f"{label}.expected_after_revision")
        observed = _sha256(content["observed_after_revision"], f"{label}.observed_after_revision")
        _zero(content["stale_live_record_count"], f"{label}.stale_live_record_count")
        _zero(content["foreign_scope_write_count"], f"{label}.foreign_scope_write_count")
        _exact_keys(
            request_payload,
            {"correction_event_id", "before_revision"},
            f"{label}.request.payload",
        )
        _exact_keys(response_payload, {"live_records"}, f"{label}.response.payload")
        _exact_keys(
            postcondition_payload,
            {"foreign_scope_writes", "stale_live_records"},
            f"{label}.postcondition.payload",
        )
        live_records = _array(response_payload["live_records"], f"{label}.response.payload.live_records")
        if len(live_records) != 1:
            _fail("data_subject_correction_mismatch", "correction must yield exactly one current live record")
        live_record = _object(live_records[0], f"{label}.response.payload.live_records[0]")
        _exact_keys(live_record, {"scope_sha256", "revision"}, f"{label}.response.payload.live_records[0]")
        if (
            before == expected
            or expected != observed
            or request_payload
            != {"correction_event_id": content["correction_event_id"], "before_revision": before}
            or live_record
            != {"scope_sha256": sha256_bytes(canonical_bytes(scope)), "revision": observed}
            or _array(postcondition_payload["foreign_scope_writes"], f"{label}.foreign_scope_writes")
            or _array(postcondition_payload["stale_live_records"], f"{label}.stale_live_records")
        ):
            _fail("data_subject_correction_mismatch", "correction did not produce the exact new live revision")
        return dict(content)
    if kind == "DATA_SUBJECT_DELETION":
        keys = {"request_id", "target_scope_sha256", "deleted_scope_sha256", "live_primary_count_after", "page_count_after", "chunk_count_after", "embedding_count_after", "cache_count_after", "backup_reference_count_after", "foreign_scope_delete_count"}
        exact_kind_keys(keys)
        _external_id(content["request_id"], f"{label}.request_id")
        expected = _sha256(content["target_scope_sha256"], f"{label}.target_scope_sha256")
        observed = _sha256(content["deleted_scope_sha256"], f"{label}.deleted_scope_sha256")
        for key in keys - {"request_id", "target_scope_sha256", "deleted_scope_sha256"}:
            _zero(content[key], f"{label}.{key}")
        _exact_keys(request_payload, {"target_scope_sha256"}, f"{label}.request.payload")
        _exact_keys(response_payload, {"remaining"}, f"{label}.response.payload")
        remaining = _object(response_payload["remaining"], f"{label}.response.payload.remaining")
        remaining_keys = {
            "live_primary",
            "pages",
            "chunks",
            "embeddings",
            "cache_entries",
            "backup_references",
        }
        _exact_keys(remaining, remaining_keys, f"{label}.response.payload.remaining")
        _exact_keys(postcondition_payload, {"foreign_scope_deletes"}, f"{label}.postcondition.payload")
        if (
            expected != observed
            or expected != sha256_bytes(canonical_bytes(scope))
            or request_payload["target_scope_sha256"] != expected
            or any(_array(remaining[key], f"{label}.remaining.{key}") for key in remaining_keys)
            or _array(postcondition_payload["foreign_scope_deletes"], f"{label}.foreign_scope_deletes")
        ):
            _fail("data_subject_deletion_mismatch", "deletion scope does not match the requested Soul scope")
        return dict(content)
    if kind == "FIXTURE_COMMAND_POSTCONDITION":
        keys = {"command_id", "trace_id", "approval_id", "lease_id", "idempotency_key", "native_result", "postcondition_result", "side_effect_count", "duplicate_side_effect_count", "postcondition_verified_at", "spoken_recorded_at", "owned_fixture_package_sha256", "platform_authorization_id"}
        exact_kind_keys(keys)
        for key in ("command_id", "approval_id", "lease_id", "platform_authorization_id"):
            _external_id(content[key], f"{label}.{key}")
        _canonical_identifier(content["trace_id"], TRACE_ID_RE, f"{label}.trace_id")
        _canonical_identifier(content["idempotency_key"], IDEMPOTENCY_KEY_RE, f"{label}.idempotency_key")
        _sha256(content["owned_fixture_package_sha256"], f"{label}.owned_fixture_package_sha256")
        postcondition_at = _parse_utc(content["postcondition_verified_at"], f"{label}.postcondition_verified_at")
        spoken_at = _parse_utc(content["spoken_recorded_at"], f"{label}.spoken_recorded_at")
        _exact_keys(
            request_payload,
            {"command_id", "approval_id", "lease_id", "idempotency_key", "platform_authorization_id", "owned_fixture_package_sha256"},
            f"{label}.request.payload",
        )
        _exact_keys(response_payload, {"native_receipts"}, f"{label}.response.payload")
        native_receipts = _array(response_payload["native_receipts"], f"{label}.response.payload.native_receipts")
        _exact_keys(
            postcondition_payload,
            {"side_effect_receipts", "duplicate_side_effect_receipts", "verified_postconditions", "spoken_recorded_at"},
            f"{label}.postcondition.payload",
        )
        verified_postconditions = _array(
            postcondition_payload["verified_postconditions"],
            f"{label}.postcondition.payload.verified_postconditions",
        )
        if (
            content["native_result"] != "SUCCEEDED"
            or content["postcondition_result"] != "VERIFIED"
            or content["side_effect_count"] != 1
            or content["duplicate_side_effect_count"] != 0
            or spoken_at < postcondition_at
            or request_payload
            != {
                "command_id": content["command_id"],
                "approval_id": content["approval_id"],
                "lease_id": content["lease_id"],
                "idempotency_key": content["idempotency_key"],
                "platform_authorization_id": content["platform_authorization_id"],
                "owned_fixture_package_sha256": content["owned_fixture_package_sha256"],
            }
            or len(native_receipts) != 1
            or len(_array(postcondition_payload["side_effect_receipts"], f"{label}.side_effect_receipts"))
            != content["side_effect_count"]
            or len(_array(postcondition_payload["duplicate_side_effect_receipts"], f"{label}.duplicate_side_effect_receipts"))
            != content["duplicate_side_effect_count"]
            or len(verified_postconditions) != 1
            or postcondition_payload["spoken_recorded_at"] != content["spoken_recorded_at"]
        ):
            _fail("fixture_postcondition_mismatch", "fixture side effect lacks native success and verified postcondition ordering")
        native_receipt = _object(native_receipts[0], f"{label}.response.payload.native_receipts[0]")
        _exact_keys(
            native_receipt,
            {"receipt_id", "command_id", "lease_id", "trace_id", "scope_sha256", "result"},
            f"{label}.response.payload.native_receipts[0]",
        )
        _external_id(native_receipt["receipt_id"], f"{label}.response.payload.native_receipts[0].receipt_id")
        scope_digest = sha256_bytes(canonical_bytes(scope))
        side_effect_receipts = _array(
            postcondition_payload["side_effect_receipts"],
            f"{label}.postcondition.payload.side_effect_receipts",
        )
        side_effect_receipt = _object(
            side_effect_receipts[0],
            f"{label}.postcondition.payload.side_effect_receipts[0]",
        )
        _exact_keys(
            side_effect_receipt,
            {"receipt_id", "command_id", "idempotency_key", "scope_sha256"},
            f"{label}.postcondition.payload.side_effect_receipts[0]",
        )
        _external_id(
            side_effect_receipt["receipt_id"],
            f"{label}.postcondition.payload.side_effect_receipts[0].receipt_id",
        )
        verified_postcondition = _object(
            verified_postconditions[0],
            f"{label}.postcondition.payload.verified_postconditions[0]",
        )
        _exact_keys(
            verified_postcondition,
            {"command_id", "scope_sha256", "result", "verified_at"},
            f"{label}.postcondition.payload.verified_postconditions[0]",
        )
        if (
            native_receipt
            != {
                "receipt_id": native_receipt["receipt_id"],
                "command_id": content["command_id"],
                "lease_id": content["lease_id"],
                "trace_id": content["trace_id"],
                "scope_sha256": scope_digest,
                "result": content["native_result"],
            }
            or side_effect_receipt
            != {
                "receipt_id": side_effect_receipt["receipt_id"],
                "command_id": content["command_id"],
                "idempotency_key": content["idempotency_key"],
                "scope_sha256": scope_digest,
            }
            or verified_postcondition
            != {
                "command_id": content["command_id"],
                "scope_sha256": scope_digest,
                "result": content["postcondition_result"],
                "verified_at": content["postcondition_verified_at"],
            }
        ):
            _fail("fixture_postcondition_mismatch", "raw native receipt or exact postcondition differs from the signed summary")
        return dict(content)
    if kind in F7_ATTACK_ARTIFACT_KINDS:
        keys = {"attack_id", "axis", "target_scope", "request_count", "authorization_decision", "native_execution_count", "returned_record_count", "side_effect_count", "audit_event_count"}
        exact_kind_keys(keys)
        _external_id(content["attack_id"], f"{label}.attack_id")
        if content["axis"] != kind.removeprefix("CROSS_").removesuffix("_ATTACK"):
            _fail("cross_scope_attack_axis_mismatch", "cross-scope attack artifact axis does not match its kind")
        target = _object(content["target_scope"], f"{label}.target_scope")
        _exact_keys(target, set(scope), f"{label}.target_scope")
        other_scopes = [candidate for soul, candidate in all_scopes.items() if soul != scope["soul_id"]]
        axis = content["axis"]
        axis_key = {"SOUL": "soul_id", "DEVICE": "device_binding_id", "ACCOUNT": "platform_account_id"}[axis]
        expected_target = dict(scope)
        if len(other_scopes) != 1:
            _fail("cross_scope_attack_target_mismatch", "attack requires exactly one other declared scope")
        expected_target[axis_key] = other_scopes[0][axis_key]
        if dict(target) != expected_target:
            _fail(
                "cross_scope_attack_target_mismatch",
                "attack must mutate exactly its named axis while every other scope axis remains constant",
            )
        request_count = _integer(content["request_count"], f"{label}.request_count", 1)
        audit_count = _integer(content["audit_event_count"], f"{label}.audit_event_count", 1)
        for key in ("native_execution_count", "returned_record_count", "side_effect_count"):
            _zero(content[key], f"{label}.{key}")
        _exact_keys(
            request_payload,
            {"axis", "actor_scope", "target_scope", "oauth_credential_lease_id", "oauth_token_fingerprint_sha256", "requested_source_alias"},
            f"{label}.request.payload",
        )
        actor_scope = _object(request_payload["actor_scope"], f"{label}.request.payload.actor_scope")
        request_target = _object(request_payload["target_scope"], f"{label}.request.payload.target_scope")
        _exact_keys(actor_scope, set(scope), f"{label}.request.payload.actor_scope")
        _exact_keys(request_target, set(scope), f"{label}.request.payload.target_scope")
        _external_id(
            request_payload["oauth_credential_lease_id"],
            f"{label}.request.payload.oauth_credential_lease_id",
        )
        _sha256(
            request_payload["oauth_token_fingerprint_sha256"],
            f"{label}.request.payload.oauth_token_fingerprint_sha256",
        )
        _exact_keys(
            response_payload,
            {"authorization_decision", "native_execution_receipts", "returned_records", "side_effect_receipts", "audit_events"},
            f"{label}.response.payload",
        )
        native_receipts = _array(
            response_payload["native_execution_receipts"],
            f"{label}.response.payload.native_execution_receipts",
        )
        returned_records = _array(response_payload["returned_records"], f"{label}.response.payload.returned_records")
        side_effect_receipts = _array(
            response_payload["side_effect_receipts"],
            f"{label}.response.payload.side_effect_receipts",
        )
        audit_events = _array(response_payload["audit_events"], f"{label}.response.payload.audit_events")
        audit_event_ids: set[str] = set()
        actor_scope_digest = sha256_bytes(canonical_bytes(scope))
        target_scope_digest = sha256_bytes(canonical_bytes(expected_target))
        for index, value in enumerate(audit_events):
            audit_event = _object(value, f"{label}.response.payload.audit_events[{index}]")
            _exact_keys(
                audit_event,
                {
                    "audit_event_id",
                    "attack_id",
                    "axis",
                    "actor_scope_sha256",
                    "target_scope_sha256",
                    "decision",
                },
                f"{label}.response.payload.audit_events[{index}]",
            )
            audit_event_id = _external_id(
                audit_event["audit_event_id"],
                f"{label}.response.payload.audit_events[{index}].audit_event_id",
            )
            if audit_event_id in audit_event_ids:
                _fail("cross_scope_attack_audit_mismatch", "denial audit event ids must be unique")
            audit_event_ids.add(audit_event_id)
            if audit_event != {
                "audit_event_id": audit_event_id,
                "attack_id": content["attack_id"],
                "axis": axis,
                "actor_scope_sha256": actor_scope_digest,
                "target_scope_sha256": target_scope_digest,
                "decision": "DENY",
            }:
                _fail(
                    "cross_scope_attack_audit_mismatch",
                    "denial audit event must bind the attack, exact actor/target scopes, axis, and decision",
                )
        _exact_keys(
            postcondition_payload,
            {"actor_scope_unchanged", "target_scope_unchanged"},
            f"{label}.postcondition.payload",
        )
        if (
            content["authorization_decision"] != "DENY"
            or request_count != 1
            or request_payload["axis"] != axis
            or dict(actor_scope) != dict(scope)
            or dict(request_target) != expected_target
            or request_payload["requested_source_alias"] != expected_target["external_source_alias"]
            or response_payload["authorization_decision"] != "DENY"
            or len(native_receipts) != content["native_execution_count"]
            or len(returned_records) != content["returned_record_count"]
            or len(side_effect_receipts) != content["side_effect_count"]
            or len(audit_events) != audit_count
            or postcondition_payload
            != {"actor_scope_unchanged": True, "target_scope_unchanged": True}
        ):
            _fail("cross_scope_attack_not_denied", "cross-scope attack must be denied before execution or data return")
        return dict(content)
    if kind == "DUPLICATE_DELIVERY":
        keys = {"command_id", "idempotency_key", "delivery_count", "native_execution_count", "side_effect_count", "verified_receipt_count", "distinct_result_count", "duplicate_side_effect_count"}
        exact_kind_keys(keys)
        _external_id(content["command_id"], f"{label}.command_id")
        _canonical_identifier(content["idempotency_key"], IDEMPOTENCY_KEY_RE, f"{label}.idempotency_key")
        delivery_count = _integer(content["delivery_count"], f"{label}.delivery_count", 2)
        _exact_keys(
            request_payload,
            {"command_id", "idempotency_key", "deliveries"},
            f"{label}.request.payload",
        )
        _exact_keys(
            response_payload,
            {"native_execution_receipts", "distinct_results"},
            f"{label}.response.payload",
        )
        _exact_keys(
            postcondition_payload,
            {"side_effect_receipts", "verified_receipts", "duplicate_side_effect_receipts"},
            f"{label}.postcondition.payload",
        )
        scope_digest = sha256_bytes(canonical_bytes(scope))
        deliveries = _array(request_payload["deliveries"], f"{label}.request.payload.deliveries")
        delivery_ids: set[str] = set()
        for index, value in enumerate(deliveries):
            delivery = _object(value, f"{label}.request.payload.deliveries[{index}]")
            _exact_keys(
                delivery,
                {"delivery_id", "ordinal", "command_id", "idempotency_key", "scope_sha256"},
                f"{label}.request.payload.deliveries[{index}]",
            )
            delivery_id = _external_id(
                delivery["delivery_id"],
                f"{label}.request.payload.deliveries[{index}].delivery_id",
            )
            if delivery_id in delivery_ids:
                _fail("duplicate_delivery_record_mismatch", "duplicate delivery records require unique delivery ids")
            delivery_ids.add(delivery_id)
            if delivery != {
                "delivery_id": delivery_id,
                "ordinal": index + 1,
                "command_id": content["command_id"],
                "idempotency_key": content["idempotency_key"],
                "scope_sha256": scope_digest,
            }:
                _fail(
                    "duplicate_delivery_record_mismatch",
                    "each raw delivery must bind its ordinal, command, idempotency key, and exact scope",
                )
        native_execution_receipts = _array(
            response_payload["native_execution_receipts"],
            f"{label}.response.payload.native_execution_receipts",
        )
        distinct_results = _array(response_payload["distinct_results"], f"{label}.response.payload.distinct_results")
        side_effect_receipts = _array(
            postcondition_payload["side_effect_receipts"],
            f"{label}.postcondition.payload.side_effect_receipts",
        )
        verified_receipts = _array(
            postcondition_payload["verified_receipts"],
            f"{label}.postcondition.payload.verified_receipts",
        )
        if (
            content["native_execution_count"] != 1
            or content["side_effect_count"] != 1
            or content["verified_receipt_count"] != 1
            or content["distinct_result_count"] != 1
            or content["duplicate_side_effect_count"] != 0
            or request_payload["command_id"] != content["command_id"]
            or request_payload["idempotency_key"] != content["idempotency_key"]
            or len(deliveries) != delivery_count
            or len(native_execution_receipts) != content["native_execution_count"]
            or len(distinct_results) != content["distinct_result_count"]
            or len(side_effect_receipts) != content["side_effect_count"]
            or len(verified_receipts) != content["verified_receipt_count"]
            or len(_array(postcondition_payload["duplicate_side_effect_receipts"], f"{label}.duplicate_side_effect_receipts"))
            != content["duplicate_side_effect_count"]
        ):
            _fail("duplicate_delivery_side_effect", "duplicate delivery was not collapsed to one verified side effect")
        native_execution_receipt = _object(
            native_execution_receipts[0],
            f"{label}.response.payload.native_execution_receipts[0]",
        )
        _exact_keys(
            native_execution_receipt,
            {"receipt_id", "command_id", "idempotency_key", "scope_sha256", "execution_ordinal"},
            f"{label}.response.payload.native_execution_receipts[0]",
        )
        distinct_result = _object(distinct_results[0], f"{label}.response.payload.distinct_results[0]")
        _exact_keys(
            distinct_result,
            {"result_id", "command_id", "scope_sha256"},
            f"{label}.response.payload.distinct_results[0]",
        )
        side_effect_receipt = _object(
            side_effect_receipts[0],
            f"{label}.postcondition.payload.side_effect_receipts[0]",
        )
        _exact_keys(
            side_effect_receipt,
            {"receipt_id", "command_id", "idempotency_key", "scope_sha256"},
            f"{label}.postcondition.payload.side_effect_receipts[0]",
        )
        verified_receipt = _object(
            verified_receipts[0],
            f"{label}.postcondition.payload.verified_receipts[0]",
        )
        _exact_keys(
            verified_receipt,
            {"receipt_id", "command_id", "scope_sha256"},
            f"{label}.postcondition.payload.verified_receipts[0]",
        )
        for record, id_key, record_label in (
            (native_execution_receipt, "receipt_id", "native execution receipt"),
            (distinct_result, "result_id", "distinct result"),
            (side_effect_receipt, "receipt_id", "side-effect receipt"),
            (verified_receipt, "receipt_id", "verified receipt"),
        ):
            _external_id(record[id_key], f"{label}.{record_label}.{id_key}")
        if (
            native_execution_receipt
            != {
                "receipt_id": native_execution_receipt["receipt_id"],
                "command_id": content["command_id"],
                "idempotency_key": content["idempotency_key"],
                "scope_sha256": scope_digest,
                "execution_ordinal": 1,
            }
            or distinct_result
            != {
                "result_id": distinct_result["result_id"],
                "command_id": content["command_id"],
                "scope_sha256": scope_digest,
            }
            or side_effect_receipt
            != {
                "receipt_id": side_effect_receipt["receipt_id"],
                "command_id": content["command_id"],
                "idempotency_key": content["idempotency_key"],
                "scope_sha256": scope_digest,
            }
            or verified_receipt
            != {
                "receipt_id": verified_receipt["receipt_id"],
                "command_id": content["command_id"],
                "scope_sha256": scope_digest,
            }
        ):
            _fail(
                "duplicate_delivery_record_mismatch",
                "duplicate-delivery receipts must bind one exact command, scope, idempotency key, and execution",
            )
        return dict(content)
    if kind == "UNKNOWN_OUTCOME_RECONCILIATION":
        keys = {"command_id", "idempotency_key", "unknown_outcome_code", "automatic_retry_count", "reconciliation_read_count", "reconciliation_mode", "final_verified_outcome", "native_execution_upper_bound", "duplicate_side_effect_count"}
        exact_kind_keys(keys)
        _external_id(content["command_id"], f"{label}.command_id")
        _canonical_identifier(content["idempotency_key"], IDEMPOTENCY_KEY_RE, f"{label}.idempotency_key")
        reconciliation_count = _integer(
            content["reconciliation_read_count"],
            f"{label}.reconciliation_read_count",
            1,
        )
        _exact_keys(
            request_payload,
            {"command_id", "idempotency_key", "automatic_retries"},
            f"{label}.request.payload",
        )
        _exact_keys(response_payload, {"native_receipts"}, f"{label}.response.payload")
        _exact_keys(
            postcondition_payload,
            {"mode", "reads", "final_verified_outcome", "duplicate_side_effect_receipts"},
            f"{label}.postcondition.payload",
        )
        native_receipts = _array(response_payload["native_receipts"], f"{label}.response.payload.native_receipts")
        if (
            content["unknown_outcome_code"] != "UNKNOWN_OUTCOME"
            or content["automatic_retry_count"] != 0
            or content["reconciliation_mode"] != "EXACT_POSTCONDITION_READBACK"
            or content["final_verified_outcome"] not in {"VERIFIED_SUCCEEDED", "VERIFIED_FAILED"}
            or content["native_execution_upper_bound"] != 1
            or content["duplicate_side_effect_count"] != 0
            or request_payload["command_id"] != content["command_id"]
            or request_payload["idempotency_key"] != content["idempotency_key"]
            or len(_array(request_payload["automatic_retries"], f"{label}.automatic_retries"))
            != content["automatic_retry_count"]
            or len(native_receipts) != content["native_execution_upper_bound"]
            or postcondition_payload["mode"] != content["reconciliation_mode"]
            or len(_array(postcondition_payload["reads"], f"{label}.reads")) != reconciliation_count
            or postcondition_payload["final_verified_outcome"] != content["final_verified_outcome"]
            or len(_array(postcondition_payload["duplicate_side_effect_receipts"], f"{label}.duplicate_side_effect_receipts"))
            != content["duplicate_side_effect_count"]
        ):
            _fail("unknown_outcome_unsafe_retry", "UNKNOWN_OUTCOME must reconcile by exact readback without blind retry")
        native_receipt = _object(native_receipts[0], f"{label}.response.payload.native_receipts[0]")
        _exact_keys(
            native_receipt,
            {"command_id", "idempotency_key", "scope_sha256", "outcome", "execution_ordinal"},
            f"{label}.response.payload.native_receipts[0]",
        )
        scope_digest = sha256_bytes(canonical_bytes(scope))
        if native_receipt != {
            "command_id": content["command_id"],
            "idempotency_key": content["idempotency_key"],
            "scope_sha256": scope_digest,
            "outcome": "UNKNOWN_OUTCOME",
            "execution_ordinal": 1,
        }:
            _fail("unknown_outcome_unsafe_retry", "raw UNKNOWN_OUTCOME receipt is missing or inconsistent")
        reads = _array(postcondition_payload["reads"], f"{label}.postcondition.payload.reads")
        read_ids: set[str] = set()
        for index, value in enumerate(reads):
            read = _object(value, f"{label}.postcondition.payload.reads[{index}]")
            _exact_keys(
                read,
                {
                    "read_id",
                    "ordinal",
                    "command_id",
                    "idempotency_key",
                    "scope_sha256",
                    "verified_outcome",
                },
                f"{label}.postcondition.payload.reads[{index}]",
            )
            read_id = _external_id(read["read_id"], f"{label}.postcondition.payload.reads[{index}].read_id")
            if read_id in read_ids:
                _fail("unknown_outcome_readback_mismatch", "reconciliation read ids must be unique")
            read_ids.add(read_id)
            if read != {
                "read_id": read_id,
                "ordinal": index + 1,
                "command_id": content["command_id"],
                "idempotency_key": content["idempotency_key"],
                "scope_sha256": scope_digest,
                "verified_outcome": content["final_verified_outcome"],
            }:
                _fail(
                    "unknown_outcome_readback_mismatch",
                    "each reconciliation read must bind the exact command, scope, order, and verified outcome",
                )
        return dict(content)
    _fail("unknown_f7_artifact_kind", f"unsupported F7 raw artifact kind {kind!r}")


def _validate_f7_windows_prerequisite(
    binding_value: Any,
    repository_id: str,
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    release_bom: Mapping[str, Any],
    evidence: Mapping[str, Any],
    trust_policy: Mapping[str, Any],
    signature_verifier: Callable[[bytes, bytes, Any], None],
    outer_window: tuple[datetime, datetime],
    evaluated_at: datetime,
) -> str:
    binding = _object(binding_value, "f6_prerequisite")
    _exact_keys(binding, {"receipt_id", "raw_artifact_id", "raw_artifact_sha256"}, "f6_prerequisite")
    receipt_id = _external_id(binding["receipt_id"], "f6_prerequisite.receipt_id")
    artifact_id, receipt, _raw, _digest = _f7_raw_json_artifact(
        raw_artifacts,
        binding["raw_artifact_id"],
        binding["raw_artifact_sha256"],
        "f6_prerequisite",
    )
    receipt_keys = {
        "schema_version",
        "receipt_id",
        "repository_id",
        "source_stage",
        "verification_level",
        "status",
        "required",
        "evidence_kind",
        "evidence_id",
        "source_evidence_sha256",
        "source_environment_id",
        "source_environment_sha256",
        "source_measurement_started_at",
        "source_measurement_finished_at",
        "edge_installation_id",
        "zenno_installation_id",
        "baseline_commit",
        "release_bom_id",
        "release_bom_sha256",
        "candidate_artifact_sha256",
        "trust_policy_id",
        "trust_policy_sha256",
        "issued_at",
        "expires_at",
        "evidence_issuer_identity",
        "signature",
    }
    _exact_keys(receipt, receipt_keys, "F7 Windows prerequisite receipt")
    if receipt["schema_version"] != "dps.f7-windows-prerequisite-receipt/v1":
        _fail("unknown_prerequisite_receipt", "F7 requires the dedicated Windows prerequisite receipt schema")
    if receipt["receipt_id"] != receipt_id or receipt["source_stage"] != "f6":
        _fail("prerequisite_binding_mismatch", "F7 prerequisite must bind a signed F6 receipt")
    if receipt["verification_level"] != "WINDOWS_VERIFIED" or receipt["status"] != PASS:
        _fail("prerequisite_level_mismatch", "F7 requires a PASS WINDOWS_VERIFIED prerequisite receipt")
    _true(receipt["required"], "F7 Windows prerequisite receipt.required")
    if receipt["evidence_kind"] != "REAL_EXTERNAL":
        _fail("non_real_prerequisite", "F7 Windows prerequisite must represent real external evidence")
    _external_id(receipt["evidence_id"], "F7 Windows prerequisite receipt.evidence_id")
    _sha256(receipt["source_evidence_sha256"], "F7 Windows prerequisite receipt.source_evidence_sha256")
    source_environment_id = _text(
        receipt["source_environment_id"],
        "F7 Windows prerequisite receipt.source_environment_id",
    )
    if ENVIRONMENT_ID_RE.fullmatch(source_environment_id) is None:
        _fail("prerequisite_environment_mismatch", "F6 prerequisite source environment id is not canonical")
    _sha256(
        receipt["source_environment_sha256"],
        "F7 Windows prerequisite receipt.source_environment_sha256",
    )
    edge_installation_id = _text(
        receipt["edge_installation_id"],
        "F7 Windows prerequisite receipt.edge_installation_id",
    )
    zenno_installation_id = _text(
        receipt["zenno_installation_id"],
        "F7 Windows prerequisite receipt.zenno_installation_id",
    )
    if (
        EDGE_INSTALLATION_ID_RE.fullmatch(edge_installation_id) is None
        or ZENNO_INSTALLATION_ID_RE.fullmatch(zenno_installation_id) is None
    ):
        _fail("prerequisite_environment_mismatch", "F6 prerequisite installation ids are not canonical")
    f7_environment = _object(evidence["environment"], "environment")
    if (
        source_environment_id != f7_environment.get("parent_windows_environment_id")
        or edge_installation_id != f7_environment.get("edge_installation_id")
        or zenno_installation_id != f7_environment.get("zenno_installation_id")
    ):
        _fail(
            "prerequisite_environment_mismatch",
            "F6 prerequisite must bind the parent Windows, Edge, and Zenno installations used by F7",
        )
    prerequisite_policy = trust_policy.get("prerequisite_receipt_policy")
    if not isinstance(prerequisite_policy, Mapping):
        _fail("prerequisite_policy_missing", "F7 requires an externally managed prerequisite receipt policy")
    required_source = _object(
        prerequisite_policy.get("required_source_evidence"),
        "prerequisite_receipt_policy.required_source_evidence",
    )
    if (
        receipt["evidence_id"] != required_source.get("evidence_id")
        or receipt["source_evidence_sha256"] != required_source.get("evidence_sha256")
        or receipt["source_environment_id"] != required_source.get("environment_id")
        or receipt["source_environment_sha256"] != required_source.get("environment_sha256")
        or receipt["source_measurement_started_at"] != required_source.get("measurement_started_at")
        or receipt["source_measurement_finished_at"] != required_source.get("measurement_finished_at")
        or receipt["edge_installation_id"] != required_source.get("edge_installation_id")
        or receipt["zenno_installation_id"] != required_source.get("zenno_installation_id")
    ):
        _fail(
            "prerequisite_source_evidence_mismatch",
            "F6 receipt must bind the exact externally trusted source evidence, environment, window, Edge, and Zenno instances",
        )
    policy_repository_id = _text(prerequisite_policy.get("repository_id"), "prerequisite_receipt_policy.repository_id")
    current_policy_sha = sha256_bytes(canonical_bytes(trust_policy))
    bom_binding = _object(evidence["release_bom"], "release_bom")
    if (
        repository_id != policy_repository_id
        or receipt["repository_id"] != repository_id
        or receipt["baseline_commit"] != evidence["baseline_commit"]
        or receipt["release_bom_id"] != release_bom.get("bom_id")
        or receipt["release_bom_sha256"] != bom_binding.get("sha256")
        or receipt["candidate_artifact_sha256"] != bom_binding.get("artifact_sha256")
        or receipt["trust_policy_id"] != trust_policy.get("policy_id")
        or receipt["trust_policy_sha256"] != current_policy_sha
    ):
        _fail("prerequisite_context_mismatch", "F6 prerequisite must bind the exact repository, commit, BOM, candidate, and trust policy")
    for key in ("release_bom_sha256", "candidate_artifact_sha256", "trust_policy_sha256"):
        _sha256(receipt[key], f"F7 Windows prerequisite receipt.{key}")
    issued_at = _parse_utc(receipt["issued_at"], "F7 Windows prerequisite receipt.issued_at")
    expires_at = _parse_utc(receipt["expires_at"], "F7 Windows prerequisite receipt.expires_at")
    source_window = _window(
        {
            "started_at": receipt["source_measurement_started_at"],
            "finished_at": receipt["source_measurement_finished_at"],
        },
        "F7 Windows prerequisite receipt.source_measurement_window",
    )
    maximum_age = _integer(prerequisite_policy.get("maximum_age_seconds"), "prerequisite_receipt_policy.maximum_age_seconds", 1)
    maximum_skew = _integer(prerequisite_policy.get("maximum_clock_skew_seconds"), "prerequisite_receipt_policy.maximum_clock_skew_seconds", 0)
    if (
        source_window[1] > issued_at
        or source_window[1] > outer_window[0]
        or issued_at > outer_window[0]
        or outer_window[0] - issued_at > timedelta(seconds=maximum_age)
        or expires_at <= issued_at
        or expires_at < outer_window[1]
        or expires_at < evaluated_at
        or issued_at > evaluated_at + timedelta(seconds=maximum_skew)
        or evaluated_at - issued_at > timedelta(seconds=maximum_age + maximum_skew)
        or expires_at - issued_at > timedelta(seconds=maximum_age + maximum_skew)
    ):
        _fail("stale_prerequisite_receipt", "F6 prerequisite receipt is not current for the complete F7 measurement")
    revoked = _array(prerequisite_policy.get("revoked_receipt_ids"), "prerequisite_receipt_policy.revoked_receipt_ids")
    if receipt_id in revoked:
        _fail("revoked_prerequisite_receipt", "F6 prerequisite receipt has been revoked by the current trust policy")
    signature = _object(receipt["signature"], "F7 Windows prerequisite receipt.signature")
    _exact_keys(signature, {"algorithm", "runner_key_id", "value"}, "F7 Windows prerequisite receipt.signature")
    if signature["algorithm"] != P1363_ALGORITHM:
        _fail("unknown_signature_algorithm", "F7 Windows prerequisite signature algorithm is unsupported")
    runner_key_id = _external_id(signature["runner_key_id"], "F7 Windows prerequisite receipt.signature.runner_key_id")
    issuer_identity = _text(receipt["evidence_issuer_identity"], "F7 Windows prerequisite receipt.evidence_issuer_identity", 2)
    issuer = _find_unique(
        _array(trust_policy["trusted_issuers"], "trusted_issuers"),
        lambda value: value.get("runner_key_id") == runner_key_id and value.get("issuer_identity") == issuer_identity,
        "F6 prerequisite evidence issuer",
    )
    key_bytes = _trusted_key(
        issuer,
        {"issuer_identity", "runner_key_id", "algorithm", "public_key_pem_path", "public_key_sha256", "allowed_verification_levels"},
        P1363_ALGORITHM,
        "F6 prerequisite evidence issuer",
    )
    windows_levels = _array(issuer["allowed_verification_levels"], "allowed_verification_levels")
    if windows_levels != ["WINDOWS_VERIFIED"]:
        _fail("issuer_scope_mismatch", "F6 prerequisite issuer is not trusted for WINDOWS_VERIFIED")
    attestation_facts = _object(
        _object(evidence["attestation"], "attestation")["facts"],
        "attestation.facts",
    )
    device_issuer = _find_unique(
        _array(trust_policy["trusted_issuers"], "trusted_issuers"),
        lambda value: value.get("runner_key_id") == attestation_facts.get("runner_key_id")
        and value.get("issuer_identity") == attestation_facts.get("evidence_issuer_identity"),
        "F7 device evidence issuer",
    )
    device_levels = _array(device_issuer["allowed_verification_levels"], "allowed_verification_levels")
    if device_levels != ["DEVICE_VERIFIED"]:
        _fail("issuer_scope_mismatch", "F7 issuer key must be dedicated to DEVICE_VERIFIED")
    bom_signature = _object(release_bom["signature"], "release BOM signature")
    bom_signer = _find_unique(
        _array(trust_policy["trusted_bom_signers"], "trusted_bom_signers"),
        lambda value: value.get("key_id") == bom_signature.get("key_id"),
        "F7 Release BOM signer",
    )
    role_key_digests = {
        _sha256(issuer["public_key_sha256"], "F6 issuer public_key_sha256"),
        _sha256(device_issuer["public_key_sha256"], "F7 issuer public_key_sha256"),
        _sha256(bom_signer["public_key_sha256"], "F7 BOM signer public_key_sha256"),
    }
    if len(role_key_digests) != 3:
        _fail(
            "cryptographic_role_separation_failed",
            "F6 receipt, F7 attestation, and Release BOM must use three distinct public keys",
        )
    unsigned_receipt = dict(receipt)
    unsigned_receipt.pop("signature")
    signature_verifier(
        key_bytes,
        b"dps-f7-windows-prerequisite-receipt/v1\n" + canonical_bytes(unsigned_receipt),
        signature["value"],
    )
    return artifact_id


def _validate_f7(
    payload_value: Any,
    outer_window: tuple[datetime, datetime],
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    trusted_environment: Mapping[str, Any] | None,
    release_bom: Mapping[str, Any] | None,
    evidence: Mapping[str, Any] | None,
    trust_policy: Mapping[str, Any] | None,
    signature_verifier: Callable[[bytes, bytes, Any], None] | None,
    evaluated_at: datetime | None,
) -> None:
    payload = _object(payload_value, "payload")
    _exact_keys(
        payload,
        {
            "repository_id",
            "f7_run_id",
            "trace_id",
            "release_bom_id",
            "release_bom_sha256",
            "devices",
            "source_mappings",
            "operation_sequence",
            "operation_timeline",
            "f6_prerequisite",
            "projection_checks",
            "search_readback_checks",
            "semantic_artifacts",
        },
        "F7 payload",
    )
    repository_id = _text(payload["repository_id"], "F7 payload.repository_id")
    if REPOSITORY_ID_RE.fullmatch(repository_id) is None:
        _fail("invalid_repository_id", "F7 repository_id is not canonical")
    if release_bom is None or evidence is None or trust_policy is None or signature_verifier is None or evaluated_at is None:
        _fail("prerequisite_context_missing", "F7 requires signed envelope, BOM, trust-policy, and verifier context")
    f7_run_id = _external_id(payload["f7_run_id"], "F7 payload.f7_run_id")
    trace_id = _canonical_identifier(payload["trace_id"], TRACE_ID_RE, "F7 payload.trace_id")
    if (
        payload["release_bom_id"] != release_bom.get("bom_id")
        or payload["release_bom_sha256"] != evidence["release_bom"]["sha256"]
    ):
        _fail("f7_bom_chain_mismatch", "F7 payload must bind the exact verified Release BOM")
    _external_id(payload["release_bom_id"], "F7 payload.release_bom_id")
    _sha256(payload["release_bom_sha256"], "F7 payload.release_bom_sha256")
    if trusted_environment is None or trusted_environment.get("os_family") != "Windows+Android":
        _fail("windows_prerequisite_missing", "F7 requires a trust-pinned Windows+Android environment")
    if raw_artifacts is None:
        _fail("raw_artifact_index_missing", "F7 requires the signed raw artifact index")
    runner_module = _find_unique(
        _array(release_bom.get("modules"), "release BOM.modules"),
        lambda value: value.get("module_id") == "f7-external-runner",
        "F7 runner BOM module",
    )
    if (
        runner_module.get("version") != trusted_environment.get("runner_version")
        or runner_module.get("sha256") != trusted_environment.get("runner_binary_sha256")
    ):
        _fail(
            "f7_runner_bom_mismatch",
            "F7 runner version and binary digest must be pinned by the signed Release BOM and trust policy",
        )
    if runner_module.get("sbom_sha256") != trusted_environment.get("runner_sbom_sha256"):
        _fail(
            "f7_runner_sbom_bom_mismatch",
            "F7 runner SBOM digest must be pinned on the same signed Release BOM module as the runner binary",
        )
    prerequisite_policy = trust_policy.get("prerequisite_receipt_policy")
    if not isinstance(prerequisite_policy, Mapping):
        _fail("prerequisite_policy_missing", "F7 requires an externally managed prerequisite receipt policy")
    maximum_age = _integer(
        prerequisite_policy.get("maximum_age_seconds"),
        "prerequisite_receipt_policy.maximum_age_seconds",
        1,
    )
    maximum_skew = _integer(
        prerequisite_policy.get("maximum_clock_skew_seconds"),
        "prerequisite_receipt_policy.maximum_clock_skew_seconds",
        0,
    )
    if (
        outer_window[1] > evaluated_at + timedelta(seconds=maximum_skew)
        or evaluated_at - outer_window[1] > timedelta(seconds=maximum_age)
    ):
        _fail(
            "stale_f7_evidence",
            "F7 measurement is future-dated or too old for the trusted gate evaluation clock",
        )
    expected_sequence = ["OBSERVE", "VERIFY", "MEMORY_EVENT", "INTEREST", "GBRAIN_PROJECTION", "EXACT_READBACK", "DELETE_REBUILD"]
    if payload["operation_sequence"] != expected_sequence:
        _fail("operation_sequence_invalid", "F7 operations must follow the declared no-shortcut sequence")
    timeline = _array(payload["operation_timeline"], "F7 payload.operation_timeline")
    if len(timeline) != len(expected_sequence):
        _fail("operation_timeline_invalid", "F7 operation timeline must contain every required phase exactly once")
    phases: dict[str, tuple[datetime, datetime]] = {}
    previous_finished: datetime | None = None
    for index, expected_phase in enumerate(expected_sequence):
        phase = _object(timeline[index], f"operation_timeline[{index}]")
        _exact_keys(phase, {"phase", "started_at", "finished_at"}, f"operation_timeline[{index}]")
        if phase["phase"] != expected_phase:
            _fail("operation_timeline_invalid", "F7 operation timeline order does not match operation_sequence")
        phase_window = _window(
            {"started_at": phase["started_at"], "finished_at": phase["finished_at"]},
            f"operation_timeline[{index}]",
        )
        if index == 0 and phase_window[0] != outer_window[0]:
            _fail("operation_timeline_invalid", "F7 first phase must start at measurement start")
        if previous_finished is not None and phase_window[0] != previous_finished:
            _fail("operation_timeline_invalid", "F7 operation phases must be contiguous and non-overlapping")
        previous_finished = phase_window[1]
        phases[expected_phase] = phase_window
    if previous_finished != outer_window[1]:
        _fail("operation_timeline_invalid", "F7 final phase must finish at measurement completion")
    run_context = {
        "f7_run_id": f7_run_id,
        "trace_id": trace_id,
        "release_bom_id": release_bom["bom_id"],
        "release_bom_sha256": evidence["release_bom"]["sha256"],
        "phases": phases,
    }

    devices = _array(payload["devices"], "devices")
    if len(devices) != 2:
        _fail("device_count_mismatch", "F7 requires exactly two devices")
    device_by_soul: dict[str, Mapping[str, Any]] = {}
    identity_fields = ("soul_id", "device_binding_id", "platform_account_id")
    for index, value in enumerate(devices):
        device = _object(value, f"devices[{index}]")
        _exact_keys(device, set(identity_fields), f"devices[{index}]")
        soul_id = _canonical_identifier(device["soul_id"], SOUL_ID_RE, f"devices[{index}].soul_id")
        _canonical_identifier(device["device_binding_id"], DEVICE_BINDING_ID_RE, f"devices[{index}].device_binding_id")
        _canonical_identifier(device["platform_account_id"], PLATFORM_ACCOUNT_ID_RE, f"devices[{index}].platform_account_id")
        if soul_id in device_by_soul:
            _fail("cross_scope_identity_collision", "F7 Soul ids must be unique")
        device_by_soul[soul_id] = device
    for key in identity_fields:
        if len({row[key] for row in device_by_soul.values()}) != 2:
            _fail("cross_scope_identity_collision", f"F7 device {key} values must be unique")

    mappings = _array(payload["source_mappings"], "source_mappings")
    if len(mappings) != 2:
        _fail("source_mapping_count_mismatch", "F7 requires one Source mapping per Soul")
    mapping_by_soul: dict[str, Mapping[str, Any]] = {}
    logical_sources: set[str] = set()
    aliases: set[str] = set()
    for index, value in enumerate(mappings):
        mapping = _object(value, f"source_mappings[{index}]")
        _exact_keys(mapping, {"soul_id", "logical_source_id", "external_source_alias", "source_binding_nonce"}, f"source_mappings[{index}]")
        soul_id = _canonical_identifier(mapping["soul_id"], SOUL_ID_RE, f"source_mappings[{index}].soul_id")
        logical = _canonical_identifier(mapping["logical_source_id"], LOGICAL_GBRAIN_SOURCE_RE, f"source_mappings[{index}].logical_source_id")
        alias = _canonical_identifier(mapping["external_source_alias"], EXTERNAL_GBRAIN_SOURCE_ALIAS_RE, f"source_mappings[{index}].external_source_alias")
        source_binding_nonce = _integer(
            mapping["source_binding_nonce"],
            f"source_mappings[{index}].source_binding_nonce",
            0,
        )
        if source_binding_nonce > 1023:
            _fail("source_binding_nonce_invalid", "F7 Source binding nonce exceeds 1023")
        if logical != _gbrain_source_for_soul(soul_id, source_binding_nonce) or alias != _expected_external_source_alias(logical):
            _fail("source_mapping_mismatch", "F7 Source mapping is not the deterministic non-PII mapping for the full Soul")
        if soul_id in mapping_by_soul or logical in logical_sources or alias in aliases:
            _fail("source_mapping_collision", "F7 Source mappings must be one-to-one")
        mapping_by_soul[soul_id] = mapping
        logical_sources.add(logical)
        aliases.add(alias)
    if set(mapping_by_soul) != set(device_by_soul):
        _fail("source_mapping_mismatch", "every device Soul must have exactly one Source mapping")
    scopes = {
        soul_id: {
            "soul_id": soul_id,
            "device_binding_id": str(device["device_binding_id"]),
            "platform_account_id": str(device["platform_account_id"]),
            "logical_source_id": str(mapping_by_soul[soul_id]["logical_source_id"]),
            "external_source_alias": str(mapping_by_soul[soul_id]["external_source_alias"]),
        }
        for soul_id, device in device_by_soul.items()
    }

    used_artifact_ids: set[str] = set()
    used_artifact_digests: set[str] = set()
    prerequisite_id = _validate_f7_windows_prerequisite(
        payload["f6_prerequisite"],
        repository_id,
        raw_artifacts,
        release_bom,
        evidence,
        trust_policy,
        signature_verifier,
        outer_window,
        evaluated_at,
    )
    used_artifact_ids.add(prerequisite_id)
    used_artifact_digests.add(str(raw_artifacts[prerequisite_id]["sha256"]))

    def consume_reference(reference: Mapping[str, Any], expected_kind: str, scope: Mapping[str, str], label: str) -> tuple[str, Mapping[str, Any], tuple[datetime, datetime]]:
        artifact_id, artifact, _raw, digest = _f7_raw_json_artifact(
            raw_artifacts,
            reference["raw_artifact_id"],
            reference["raw_artifact_sha256"],
            label,
        )
        if artifact_id in used_artifact_ids or digest in used_artifact_digests:
            _fail("duplicate_f7_artifact", "F7 artifacts must have unique ids and unique file digests")
        used_artifact_ids.add(artifact_id)
        used_artifact_digests.add(digest)
        content, capture_window = _validate_f7_artifact_envelope(
            artifact_id,
            artifact,
            expected_kind,
            scope,
            outer_window,
            trusted_environment,
            evidence,
        )
        return artifact_id, content, capture_window

    verified_projection_by_soul: dict[str, tuple[str, str, str, str]] = {}
    projection_checks = _array(payload["projection_checks"], "projection_checks")
    if len(projection_checks) != 2:
        _fail("projection_count_mismatch", "F7 requires one projection artifact per Soul")
    for index, value in enumerate(projection_checks):
        reference = _object(value, f"projection_checks[{index}]")
        _exact_keys(reference, {"soul_id", "raw_artifact_id", "raw_artifact_sha256"}, f"projection_checks[{index}]")
        soul_id = _canonical_identifier(reference["soul_id"], SOUL_ID_RE, f"projection_checks[{index}].soul_id")
        if soul_id not in scopes or soul_id in verified_projection_by_soul:
            _fail("projection_scope_mismatch", "projection artifacts must map one-to-one to declared Souls")
        artifact_id, content, capture_window = consume_reference(reference, F7_PROJECTION_KIND, scopes[soul_id], f"projection_checks[{index}]")
        verified_projection_by_soul[soul_id] = _validate_f7_projection_artifact_content(
            content,
            scopes[soul_id],
            int(mapping_by_soul[soul_id]["source_binding_nonce"]),
            artifact_id,
            run_context,
            capture_window,
        )
    if set(verified_projection_by_soul) != set(scopes):
        _fail("projection_scope_mismatch", "not every declared Soul has an exact projection artifact")

    search_souls: set[str] = set()
    search_checks = _array(payload["search_readback_checks"], "search_readback_checks")
    if len(search_checks) != 2:
        _fail("search_readback_count_mismatch", "F7 requires one search artifact per Soul")
    for index, value in enumerate(search_checks):
        reference = _object(value, f"search_readback_checks[{index}]")
        _exact_keys(reference, {"soul_id", "raw_artifact_id", "raw_artifact_sha256"}, f"search_readback_checks[{index}]")
        soul_id = _canonical_identifier(reference["soul_id"], SOUL_ID_RE, f"search_readback_checks[{index}].soul_id")
        if soul_id not in scopes or soul_id in search_souls:
            _fail("search_scope_mismatch", "search artifacts must map one-to-one to declared Souls")
        search_souls.add(soul_id)
        artifact_id, content, capture_window = consume_reference(reference, F7_SEARCH_KIND, scopes[soul_id], f"search_readback_checks[{index}]")
        _validate_f7_search_artifact_content(
            content,
            scopes[soul_id],
            artifact_id,
            outer_window,
            verified_projection_by_soul[soul_id][:2],
            run_context,
            capture_window,
        )
    if search_souls != set(scopes):
        _fail("search_scope_mismatch", "not every declared Soul has a verified search artifact")

    semantic = _array(payload["semantic_artifacts"], "semantic_artifacts")
    expected_semantic_count = (
        len(F7_PER_SOUL_ARTIFACT_KINDS) + len(F7_ATTACK_ARTIFACT_KINDS)
    ) * 2
    if len(semantic) != expected_semantic_count:
        _fail("f7_semantic_artifact_count_mismatch", f"F7 requires exactly {expected_semantic_count} semantic raw artifacts")
    observed_pairs: set[tuple[str, str]] = set()
    counts: dict[str, int] = {}
    semantic_facts: dict[tuple[str, str], Mapping[str, Any]] = {}
    for index, value in enumerate(semantic):
        reference = _object(value, f"semantic_artifacts[{index}]")
        _exact_keys(reference, {"artifact_kind", "soul_id", "raw_artifact_id", "raw_artifact_sha256"}, f"semantic_artifacts[{index}]")
        kind = _text(reference["artifact_kind"], f"semantic_artifacts[{index}].artifact_kind")
        if kind not in F7_PER_SOUL_ARTIFACT_KINDS | F7_ATTACK_ARTIFACT_KINDS:
            _fail("unknown_f7_artifact_kind", f"unsupported F7 semantic artifact kind {kind!r}")
        soul_id = _canonical_identifier(reference["soul_id"], SOUL_ID_RE, f"semantic_artifacts[{index}].soul_id")
        if soul_id not in scopes or (kind, soul_id) in observed_pairs:
            _fail("duplicate_f7_semantic_scope", "F7 semantic artifact kind/Soul bindings must be unique")
        observed_pairs.add((kind, soul_id))
        counts[kind] = counts.get(kind, 0) + 1
        artifact_id, content, capture_window = consume_reference(reference, kind, scopes[soul_id], f"semantic_artifacts[{index}]")
        semantic_facts[(kind, soul_id)] = _validate_f7_semantic_content(
            kind,
            content,
            scopes[soul_id],
            scopes,
            artifact_id,
            run_context,
            capture_window,
        )
    for kind in F7_PER_SOUL_ARTIFACT_KINDS:
        if counts.get(kind) != 2 or {soul for observed_kind, soul in observed_pairs if observed_kind == kind} != set(scopes):
            _fail("f7_semantic_scope_incomplete", f"F7 requires one {kind} artifact for each Soul")
    for kind in F7_ATTACK_ARTIFACT_KINDS:
        if counts.get(kind) != 2 or {
            soul for observed_kind, soul in observed_pairs if observed_kind == kind
        } != set(scopes):
            _fail(
                "f7_semantic_scope_incomplete",
                f"F7 requires one {kind} artifact in each Soul-to-other-Soul direction",
            )

    oauth_unique_fields = (
        "adb_serial_hmac_sha256",
        "device_attestation_sha256",
        "oauth_client_id_sha256",
        "oauth_credential_lease_id",
        "oauth_token_fingerprint_sha256",
    )
    oauth_by_soul = {
        soul_id: semantic_facts[("SOUL_DEVICE_SOURCE_OAUTH_BINDING", soul_id)]
        for soul_id in scopes
    }
    for field in oauth_unique_fields:
        if len({facts[field] for facts in oauth_by_soul.values()}) != 2:
            _fail(
                "f7_credential_or_attestation_collision",
                f"two-device F7 proof requires unique {field} values per Soul",
            )

    def exchange_payload(facts: Mapping[str, Any], exchange_kind: str) -> Mapping[str, Any]:
        raw = _base64_content(
            facts[f"{exchange_kind}_base64"],
            f"semantic facts.{exchange_kind}_base64",
            1024 * 1024,
        )
        return _object(
            _decode_json_object(raw, f"semantic facts.{exchange_kind}")["payload"],
            f"semantic facts.{exchange_kind}.payload",
        )

    for soul_id in scopes:
        oauth = oauth_by_soul[soul_id]
        projection_binding_revision, projection_binding_checksum = verified_projection_by_soul[soul_id][2:]
        if (
            oauth["source_binding_nonce"] != mapping_by_soul[soul_id]["source_binding_nonce"]
            or oauth["source_binding_revision"] != projection_binding_revision
            or oauth["source_binding_checksum"] != projection_binding_checksum
        ):
            _fail(
                "f7_oauth_source_binding_mismatch",
                "OAuth whoami Source must bind the same nonce, revision, and checksum as projection v2",
            )
        for attack_kind in F7_ATTACK_ARTIFACT_KINDS:
            attack = semantic_facts[(attack_kind, soul_id)]
            attack_request = exchange_payload(attack, "request")
            if (
                attack_request["oauth_credential_lease_id"] != oauth["oauth_credential_lease_id"]
                or attack_request["oauth_token_fingerprint_sha256"]
                != oauth["oauth_token_fingerprint_sha256"]
            ):
                _fail(
                    "f7_cross_credential_binding_mismatch",
                    "cross-scope attack must use the actor Soul's exact verified OAuth credential lease and token",
                )
        projection_revision, projection_checksum = verified_projection_by_soul[soul_id][:2]
        lifecycle = semantic_facts[("DELETE_REBUILD_PURGE", soul_id)]
        if (
            lifecycle["pre_delete_projection_checksum"] != projection_checksum
            or lifecycle["expected_rebuild_revision"] != projection_revision
            or lifecycle["readback_rebuild_revision"] != projection_revision
            or lifecycle["expected_rebuild_checksum"] != projection_checksum
            or lifecycle["readback_rebuild_checksum"] != projection_checksum
        ):
            _fail(
                "f7_lifecycle_projection_chain_mismatch",
                "delete and rebuild proof must bind the same exact verified projection revision and checksum",
            )
        fixture = semantic_facts[("FIXTURE_COMMAND_POSTCONDITION", soul_id)]
        duplicate = semantic_facts[("DUPLICATE_DELIVERY", soul_id)]
        unknown = semantic_facts[("UNKNOWN_OUTCOME_RECONCILIATION", soul_id)]
        if (
            duplicate["command_id"] != fixture["command_id"]
            or duplicate["idempotency_key"] != fixture["idempotency_key"]
        ):
            _fail(
                "f7_duplicate_fixture_chain_mismatch",
                "duplicate delivery proof must exercise the exact fixture command and idempotency key",
            )
        if (
            unknown["command_id"] in {fixture["command_id"], duplicate["command_id"]}
            or unknown["idempotency_key"] in {fixture["idempotency_key"], duplicate["idempotency_key"]}
        ):
            _fail(
                "f7_unknown_outcome_chain_mismatch",
                "UNKNOWN_OUTCOME reconciliation must be a distinct command while remaining in the same run and trace",
            )
    if set(raw_artifacts) != used_artifact_ids:
        missing = sorted(set(raw_artifacts) - used_artifact_ids)
        unknown = sorted(used_artifact_ids - set(raw_artifacts))
        _fail("f7_artifact_set_mismatch", f"F7 raw artifact set must be exact; extra={missing}, missing={unknown}")


def _validate_ordered_waves(
    waves_value: Any,
    expected_names: Sequence[str],
    outer_window: tuple[datetime, datetime],
) -> list[tuple[Mapping[str, Any], datetime, datetime]]:
    waves = _array(waves_value, "waves")
    if len(waves) != len(expected_names):
        _fail("wave_count_mismatch", f"waves must contain exactly {len(expected_names)} entries")
    result: list[tuple[Mapping[str, Any], datetime, datetime]] = []
    previous_finished: datetime | None = None
    for index, (item, expected_name) in enumerate(zip(waves, expected_names, strict=True)):
        wave = _object(item, f"waves[{index}]")
        if wave.get("name") != expected_name:
            _fail("wave_sequence_invalid", f"waves[{index}] must be {expected_name!r}")
        started = _parse_utc(wave.get("started_at"), f"waves[{index}].started_at")
        finished = _parse_utc(wave.get("finished_at"), f"waves[{index}].finished_at")
        if finished <= started:
            _fail("invalid_time_window", f"waves[{index}] must have positive duration")
        if started < outer_window[0] or finished > outer_window[1]:
            _fail("wave_outside_measurement", f"waves[{index}] lies outside the signed measurement window")
        if previous_finished is not None and started < previous_finished:
            _fail("wave_overlap", "rollout waves must be sequential and non-overlapping")
        previous_finished = finished
        result.append((wave, started, finished))
    return result


def _validate_f8(payload_value: Any, outer_window: tuple[datetime, datetime]) -> None:
    payload = _object(payload_value, "payload")
    _exact_keys(
        payload,
        {
            "waves",
            "parallel_module_count",
            "parallel_modules_independent",
            "zero_tolerance",
            "technical_measurements",
            "rollback_drill",
            "traceable_device_count",
            "queryable_bom_device_count",
        },
        "F8 payload",
    )
    names = ["simulator", "shadow", "test_soul", "1", "3", "8", "15", "30"]
    waves = _validate_ordered_waves(payload["waves"], names, outer_window)
    wave_keys = {"name", "device_count", "environment_kind", "started_at", "finished_at", "commands", "status", "real_side_effect_count"}
    expected_kinds = {
        "simulator": "SIMULATED",
        "shadow": "SHADOW",
        "test_soul": "TEST",
        "1": "PRODUCTION",
        "3": "PRODUCTION",
        "8": "PRODUCTION",
        "15": "PRODUCTION",
        "30": "PRODUCTION",
    }
    minimums = {
        "1": (2.0, 500),
        "3": (2.0, 500),
        "8": (2.0, 500),
        "15": (8.0, 1),
        "30": (24.0, 1),
    }
    for index, (wave, started, finished) in enumerate(waves):
        _exact_keys(wave, wave_keys, f"waves[{index}]")
        name = str(wave["name"])
        if wave["environment_kind"] != expected_kinds[name]:
            _fail("wave_environment_mismatch", f"wave {name} has the wrong environment kind")
        count = _integer(wave["device_count"], f"waves[{index}].device_count", 0)
        if name.isdigit() and count != int(name):
            _fail("device_count_mismatch", f"wave {name} must contain exactly {name} devices")
        _integer(wave["commands"], f"waves[{index}].commands", 0)
        _pass(wave["status"], f"waves[{index}].status")
        side_effects = _integer(wave["real_side_effect_count"], f"waves[{index}].real_side_effect_count", 0)
        if name in {"simulator", "shadow"} and side_effects != 0:
            _fail("shadow_side_effect", f"wave {name} must not cause real side effects")
        if name in minimums:
            minimum_hours, minimum_commands = minimums[name]
            actual_hours = (finished - started).total_seconds() / 3600
            if actual_hours < minimum_hours:
                _fail("time_threshold_not_met", f"wave {name} requires at least {minimum_hours:g} hours")
            if wave["commands"] < minimum_commands:
                _fail("command_threshold_not_met", f"wave {name} requires at least {minimum_commands} commands")

    module_count = _integer(payload["parallel_module_count"], "parallel_module_count", 1)
    if module_count > 2:
        _fail("parallel_scope_exceeded", "F8 permits at most two independent modules in canary")
    _true(payload["parallel_modules_independent"], "parallel_modules_independent")
    zero = _object(payload["zero_tolerance"], "zero_tolerance")
    zero_keys = {
        "cross_scope_leaks",
        "unauthorized_side_effects",
        "duplicate_side_effects",
        "false_successes",
        "unknown_contract_acceptances",
        "shadow_real_side_effects",
        "zenno_unexpected_restarts",
        "audit_chain_gaps",
    }
    _exact_keys(zero, zero_keys, "zero_tolerance")
    for key in zero_keys:
        _zero(zero[key], f"zero_tolerance.{key}")

    measurements = _object(payload["technical_measurements"], "technical_measurements")
    measurement_keys = {
        "max_consecutive_health_check_failures",
        "max_error_rate_delta_percentage_points_over_5m",
        "max_error_rate_ratio_over_5m",
        "max_p95_latency_ratio_over_10m",
        "max_oldest_growing_backlog_seconds",
        "max_gbrain_projection_lag_seconds",
    }
    _exact_keys(measurements, measurement_keys, "technical_measurements")
    if _integer(measurements["max_consecutive_health_check_failures"], "technical_measurements.max_consecutive_health_check_failures", 0) > 2:
        _fail("health_threshold_breached", "three consecutive health failures require rollback")
    if _number(measurements["max_error_rate_delta_percentage_points_over_5m"], "technical_measurements.max_error_rate_delta_percentage_points_over_5m", 0) > 2:
        _fail("error_rate_threshold_breached", "error-rate delta exceeded two percentage points")
    if _number(measurements["max_error_rate_ratio_over_5m"], "technical_measurements.max_error_rate_ratio_over_5m", 0) >= 2:
        _fail("error_rate_threshold_breached", "error rate reached twice the stable version")
    if _number(measurements["max_p95_latency_ratio_over_10m"], "technical_measurements.max_p95_latency_ratio_over_10m", 0) > 1.5:
        _fail("latency_threshold_breached", "p95 latency exceeded 1.5 times stable")
    if _number(measurements["max_oldest_growing_backlog_seconds"], "technical_measurements.max_oldest_growing_backlog_seconds", 0) > 120:
        _fail("backlog_threshold_breached", "growing Edge backlog exceeded two minutes")
    if _number(measurements["max_gbrain_projection_lag_seconds"], "technical_measurements.max_gbrain_projection_lag_seconds", 0) > 300:
        _fail("projection_lag_threshold_breached", "GBrain projection lag exceeded five minutes")

    rollback = _object(payload["rollback_drill"], "rollback_drill")
    _exact_keys(
        rollback,
        {"status", "duration_minutes", "previous_bom_restored", "event_loss_count", "duplicate_side_effect_count"},
        "rollback_drill",
    )
    _pass(rollback["status"], "rollback_drill.status")
    if _number(rollback["duration_minutes"], "rollback_drill.duration_minutes", 0) > 5:
        _fail("rollback_too_slow", "F8 rollback drill must finish within five minutes")
    _true(rollback["previous_bom_restored"], "rollback_drill.previous_bom_restored")
    _zero(rollback["event_loss_count"], "rollback_drill.event_loss_count")
    _zero(rollback["duplicate_side_effect_count"], "rollback_drill.duplicate_side_effect_count")
    _integer(payload["traceable_device_count"], "traceable_device_count", 30)
    _integer(payload["queryable_bom_device_count"], "queryable_bom_device_count", 30)


def _raw_json_artifact(
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    artifact_id_value: Any,
    artifact_sha256_value: Any,
    label: str,
) -> tuple[str, Mapping[str, Any]]:
    if raw_artifacts is None:
        _fail("raw_artifact_index_missing", f"{label} requires the signed raw artifact index")
    artifact_id = _external_id(artifact_id_value, f"{label}.raw_artifact_id")
    if artifact_id not in raw_artifacts:
        _fail("raw_artifact_binding_missing", f"{label} raw artifact is absent from the signed envelope")
    binding = _object(raw_artifacts[artifact_id], f"raw artifact {artifact_id}")
    _exact_keys(binding, {"sha256", "media_type", "bytes"}, f"raw artifact {artifact_id}")
    digest = _sha256(artifact_sha256_value, f"{label}.raw_artifact_sha256")
    if binding["sha256"] != digest:
        _fail("raw_artifact_digest_mismatch", f"{label} does not bind the envelope artifact digest")
    if binding["media_type"] != "application/json":
        _fail("raw_artifact_media_type", f"{label} raw artifact must use application/json")
    raw = binding["bytes"]
    if not isinstance(raw, bytes):
        _fail("invalid_raw_artifact", f"{label} raw artifact bytes are unavailable")
    return artifact_id, _decode_json_object(raw, f"raw artifact {artifact_id}")


def _dependency_waves(graph: Mapping[str, set[str]]) -> list[list[str]]:
    remaining = {module_id: set(dependencies) for module_id, dependencies in graph.items()}
    completed: set[str] = set()
    waves: list[list[str]] = []
    while remaining:
        ready = sorted(module_id for module_id, dependencies in remaining.items() if dependencies.issubset(completed))
        if not ready:
            _fail("dependency_cycle", "F9 signed manifests contain a dependency cycle")
        waves.append(ready)
        completed.update(ready)
        for module_id in ready:
            del remaining[module_id]
    return waves


def _f9_module_contract_mode(
    contracts_by_module: Mapping[str, Mapping[str, Sequence[Mapping[str, Any]]]],
    module_id: str,
    contract_major: tuple[str, int],
    *,
    prefer_provided: bool = False,
) -> tuple[str | None, str | None]:
    root = contracts_by_module.get(module_id)
    if root is None:
        return None, "external"
    modes: dict[str, str] = {}
    for kind in ("provided", "consumed"):
        for item in root[kind]:
            if (item["contractId"], item["major"]) == contract_major:
                modes[kind] = item["mode"]
    if len(set(modes.values())) > 1:
        _fail(
            "conflicting_contract_mode",
            f"{module_id} has conflicting provided/consumed modes for "
            f"{contract_major[0]}/v{contract_major[1]}",
        )
    if prefer_provided and "provided" in modes:
        return modes["provided"], "provided"
    if "consumed" in modes:
        return modes["consumed"], "consumed"
    if "provided" in modes:
        return modes["provided"], "provided"
    return None, None


def _f9_canonical_communication_edge(edge: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "peerModule": edge.get("peerModule"),
        "contractId": edge.get("contractId"),
        "major": edge.get("major"),
        "direction": edge.get("direction"),
        "transport": edge.get("transport"),
        "timeout": edge.get("timeoutMs"),
        "retryPolicy": edge.get("retryPolicy"),
        "idempotencyKey": edge.get("idempotencyKey"),
        "authScope": edge.get("authScope"),
        "failureMode": edge.get("failureMode"),
        "preserveProducer": edge.get("preserveProducer", False),
    }


def _f9_communication_pair_sha256(
    sender: str,
    receiver: str,
    contract_id: str,
    major: int,
    outbound: Mapping[str, Any],
    inbound: Mapping[str, Any],
) -> str:
    return hashlib.sha256(
        canonical_bytes(
            {
                "schemaVersion": "dps.communication-pair/v1",
                "contractId": contract_id,
                "major": major,
                "transportSenderModule": sender,
                "transportReceiverModule": receiver,
                "outbound": _f9_canonical_communication_edge(outbound),
                "inbound": _f9_canonical_communication_edge(inbound),
            }
        )
    ).hexdigest()


def _f9_route_details(
    contracts_by_module: Mapping[str, Mapping[str, Sequence[Mapping[str, Any]]]],
    owner_id: str,
    producer_id: str,
    sender_id: str,
    receiver_id: str,
    contract_major: tuple[str, int],
    resolution: str,
    reciprocal_resolved: bool,
    pair_sha256: str | None,
    preserve_producer: bool,
) -> dict[str, Any]:
    owner_mode, _ = _f9_module_contract_mode(
        contracts_by_module,
        owner_id,
        contract_major,
        prefer_provided=True,
    )
    producer_mode, producer_kind = _f9_module_contract_mode(
        contracts_by_module,
        producer_id,
        contract_major,
        prefer_provided=producer_id == owner_id,
    )
    consumer_mode, consumer_kind = _f9_module_contract_mode(
        contracts_by_module,
        receiver_id,
        contract_major,
        prefer_provided=receiver_id == owner_id,
    )
    if resolution == "schema-producer-preserved-by-relay":
        sender_mode, sender_kind = _f9_module_contract_mode(
            contracts_by_module,
            sender_id,
            contract_major,
        )
        if sender_kind != "consumed":
            sender_mode, sender_kind = None, None
    else:
        sender_mode, sender_kind = _f9_module_contract_mode(
            contracts_by_module,
            sender_id,
            contract_major,
            prefer_provided=sender_id == owner_id,
        )
    return {
        "owner_mode": owner_mode,
        "producer_module": producer_id,
        "producer_mode": producer_mode,
        "producer_declaration_kind": producer_kind,
        "transport_sender_module": sender_id,
        "transport_sender_mode": sender_mode,
        "transport_sender_declaration_kind": sender_kind,
        "transport_receiver_module": receiver_id,
        "consumer_module": receiver_id,
        "consumer_mode": consumer_mode,
        "consumer_declaration_kind": consumer_kind,
        "producer_resolution": resolution,
        "communication_pair_sha256": pair_sha256,
        "reciprocal_resolved": reciprocal_resolved,
        "transport_preserves_producer": preserve_producer,
    }


def _f9_runtime_routes_for_major(
    contracts_by_module: Mapping[str, Mapping[str, Sequence[Mapping[str, Any]]]],
    owner_id: str,
    contract_id: str,
    major: int,
    schema_producers: set[str],
    edge_index: Mapping[
        tuple[str, str, str, int, str], Mapping[str, Any]
    ],
) -> list[dict[str, Any]]:
    contract_major = (contract_id, major)
    routes: dict[tuple[str, str, str], dict[str, Any]] = {}
    resolved_producers: set[str] = set()
    for key, outbound in sorted(edge_index.items()):
        sender, receiver, edge_contract, edge_major, direction = key
        if (
            direction != "outbound"
            or edge_contract != contract_id
            or edge_major != major
            or receiver not in contracts_by_module
        ):
            continue
        preserve = outbound.get("preserveProducer", False) is True
        reciprocal = edge_index.get(
            (receiver, sender, contract_id, major, "inbound")
        )
        semantics_match = (
            reciprocal is not None
            and outbound.get("transport") == reciprocal.get("transport")
            and outbound.get("timeoutMs") == reciprocal.get("timeoutMs")
        )
        candidates: list[tuple[str, str]] = []
        sender_mode, sender_kind = _f9_module_contract_mode(
            contracts_by_module,
            sender,
            contract_major,
        )
        if sender in schema_producers and not preserve:
            candidates.append((sender, "schema-producer-is-transport-sender"))
        elif (
            preserve
            and sender_mode is not None
            and sender_kind == "consumed"
            and len(schema_producers) == 1
            and sender not in schema_producers
        ):
            candidates.append(
                (
                    next(iter(schema_producers)),
                    "schema-producer-preserved-by-relay",
                )
            )
        else:
            candidates.extend(
                (producer, "unresolved") for producer in schema_producers
            )
        for producer, resolution in candidates:
            resolved = semantics_match and resolution != "unresolved"
            pair_sha256 = (
                _f9_communication_pair_sha256(
                    sender,
                    receiver,
                    contract_id,
                    major,
                    outbound,
                    reciprocal,
                )
                if resolved and reciprocal is not None
                else None
            )
            route = _f9_route_details(
                contracts_by_module,
                owner_id,
                producer,
                sender,
                receiver,
                contract_major,
                resolution if resolved else "unresolved",
                resolved,
                pair_sha256,
                preserve,
            )
            routes[(producer, sender, receiver)] = route
            if resolved:
                resolved_producers.add(producer)

    family_consumers = {
        module_id
        for module_id, root in contracts_by_module.items()
        if any(
            item["contractId"] == contract_id for item in root["consumed"]
        )
    }
    for producer in sorted(schema_producers):
        producer_has_route = any(
            route_producer == producer
            for route_producer, _sender, _receiver in routes
        )
        if producer in resolved_producers or producer_has_route:
            continue
        candidate_receivers = set(family_consumers).difference(schema_producers)
        if owner_id != producer:
            candidate_receivers.add(owner_id)
        candidate_receivers.discard(producer)
        for receiver in sorted(candidate_receivers):
            identity = (producer, producer, receiver)
            if identity not in routes:
                routes[identity] = _f9_route_details(
                    contracts_by_module,
                    owner_id,
                    producer,
                    producer,
                    receiver,
                    contract_major,
                    "unresolved",
                    False,
                    None,
                    False,
                )
    return [routes[key] for key in sorted(routes)]


def _build_f9_compatibility_artifact(
    contracts_by_module: Mapping[str, Mapping[str, Sequence[Mapping[str, Any]]]],
    communications_by_module: Mapping[str, Sequence[Mapping[str, Any]]],
    contract_producers: Mapping[tuple[str, int], set[str]],
    policy_sha256: str,
) -> dict[str, Any]:
    """Rebuild the canonical static v2 matrix from signed Manifest facts.

    Owner, schema producer, transport sender, and runtime receiver are distinct
    roles. Static inventory never substitutes for candidate execution evidence.
    """

    policy_digest = _sha256(policy_sha256, "compatibility policy sha256")
    if not contracts_by_module:
        _fail(
            "compatibility_inventory_empty",
            "F9 compatibility inventory requires at least one signed module",
        )
    declarations: list[dict[str, Any]] = []
    provider_families: dict[str, tuple[str, dict[int, Mapping[str, Any]]]] = {}
    seen_declarations: set[tuple[str, str, str, int]] = set()
    for module_id in sorted(contracts_by_module):
        contract_root = contracts_by_module[module_id]
        for declaration_kind in ("provided", "consumed"):
            for item in contract_root[declaration_kind]:
                contract_id = str(item["contractId"])
                major = int(item["major"])
                declaration_key = (module_id, declaration_kind, contract_id, major)
                if declaration_key in seen_declarations:
                    _fail(
                        "duplicate_contract_major",
                        "F9 signed manifests duplicate a contract-major declaration",
                    )
                seen_declarations.add(declaration_key)
                producers = contract_producers.get((contract_id, major))
                if not producers:
                    _fail(
                        "contract_producer_unconstrained",
                        f"cannot resolve schema producer for {contract_id}/v{major}",
                    )
                declarations.append(
                    {
                        "moduleId": module_id,
                        "declarationKind": declaration_kind,
                        "contractId": contract_id,
                        "major": major,
                        "source": item["source"],
                        "status": item["status"],
                        "mode": item["mode"],
                        "ownerModule": item["ownerModule"],
                        "schemaProducers": sorted(producers),
                        "candidateGreenEligible": item["mode"]
                        == RUNNABLE_CONTRACT_MODE,
                    }
                )
                if declaration_kind != "provided":
                    continue
                if item["ownerModule"] != module_id:
                    _fail(
                        "contract_owner_mismatch",
                        "provided contract owner must match its signed manifest",
                    )
                existing = provider_families.get(contract_id)
                if existing is None:
                    provider_families[contract_id] = (module_id, {major: item})
                else:
                    if existing[0] != module_id:
                        _fail(
                            "multiple_contract_owners",
                            "F9 signed manifests declare multiple owners for one contract family",
                        )
                    if major in existing[1]:
                        _fail(
                            "duplicate_contract_major",
                            "F9 signed manifests duplicate a provided contract major",
                        )
                    existing[1][major] = item

    edge_index: dict[
        tuple[str, str, str, int, str], Mapping[str, Any]
    ] = {}
    for module_id in sorted(contracts_by_module):
        for edge in communications_by_module.get(module_id, []):
            if edge.get("moduleId") != module_id:
                _fail(
                    "communication_module_mismatch",
                    "normalized communication edge has the wrong module identity",
                )
            key = (
                module_id,
                str(edge["peerModule"]),
                str(edge["contractId"]),
                int(edge["major"]),
                str(edge["direction"]),
            )
            if key in edge_index:
                _fail(
                    "duplicate_communication_edge",
                    "F9 signed manifests duplicate an exact communication edge",
                )
            edge_index[key] = edge

    declaration_matrix: list[dict[str, Any]] = []
    for contract_id, (owner_id, owner_majors) in sorted(
        provider_families.items()
    ):
        owner_modes = {
            major: item["mode"] for major, item in owner_majors.items()
        }
        active_majors = sorted(
            major
            for major, mode in owner_modes.items()
            if mode == RUNNABLE_CONTRACT_MODE
        )
        if len(active_majors) > 2 or (
            len(active_majors) == 2
            and active_majors != [active_majors[-1] - 1, active_majors[-1]]
        ):
            _fail(
                "active_contract_window_invalid",
                f"{contract_id} active producer majors must remain within the N/N-1 window",
            )
        current = max(active_majors) if active_majors else None
        inventory_major = current if current is not None else max(owner_modes)
        previous = current - 1 if current is not None and current > 1 else None
        current_routes = _f9_runtime_routes_for_major(
            contracts_by_module,
            owner_id,
            contract_id,
            inventory_major,
            set(contract_producers[(contract_id, inventory_major)]),
            edge_index,
        )
        previous_routes: dict[tuple[str, str, str], dict[str, Any]] = {}
        if previous is not None and (contract_id, previous) in contract_producers:
            previous_routes = {
                (
                    route["producer_module"],
                    route["transport_sender_module"],
                    route["consumer_module"],
                ): route
                for route in _f9_runtime_routes_for_major(
                    contracts_by_module,
                    owner_id,
                    contract_id,
                    previous,
                    set(contract_producers[(contract_id, previous)]),
                    edge_index,
                )
            }

        def declaration(
            route: Mapping[str, Any], major: int, required: bool
        ) -> dict[str, Any]:
            modes = (
                route["owner_mode"],
                route["producer_mode"],
                route["transport_sender_mode"],
                route["consumer_mode"],
            )
            readable = (
                route["reciprocal_resolved"]
                and route["owner_mode"] == "active"
                and route["producer_mode"] == "active"
                and route["transport_sender_mode"] == "active"
                and route["consumer_mode"] == "compat-read"
            )
            runnable = route["reciprocal_resolved"] and all(
                mode == RUNNABLE_CONTRACT_MODE for mode in modes
            )
            if any(mode is None for mode in modes):
                execution_class = "missing-declaration"
            elif "retired" in modes:
                execution_class = "retired"
            elif "quarantine-only" in modes:
                execution_class = "quarantine-only"
            elif "compat-read" in modes:
                execution_class = "compat-read-only"
            elif not route["reciprocal_resolved"]:
                execution_class = "unresolved-communication"
            else:
                execution_class = "active-runtime"
            value = dict(route)
            value.update(
                {
                    "producer_major": major,
                    "readable": readable,
                    "runnable": runnable,
                    "execution_class": execution_class,
                    "result": (
                        "NOT_APPLICABLE"
                        if not required
                        else ("PASS" if runnable else "FAIL")
                    ),
                }
            )
            return value

        for current_route in current_routes:
            identity = (
                current_route["producer_module"],
                current_route["transport_sender_module"],
                current_route["consumer_module"],
            )
            current_required = current is not None
            current_declaration = declaration(
                current_route, inventory_major, current_required
            )
            if previous is None:
                previous_declaration = {
                    "producer_major": None,
                    "result": "NOT_APPLICABLE",
                    "runnable": False,
                    "readable": False,
                }
            else:
                previous_route = previous_routes.get(identity)
                if previous_route is None:
                    previous_route = _f9_route_details(
                        contracts_by_module,
                        owner_id,
                        identity[0],
                        identity[1],
                        identity[2],
                        (contract_id, previous),
                        "unresolved",
                        False,
                        None,
                        identity[1] != identity[0],
                    )
                previous_declaration = declaration(
                    previous_route, previous, current_required
                )
            independent_deployable = (
                current_required
                and current_declaration["result"] == "PASS"
                and previous_declaration["result"]
                in {"PASS", "NOT_APPLICABLE"}
            )
            compatibility_group_required = (
                current_required
                and current_declaration["result"] == "PASS"
                and previous_declaration["result"] == "FAIL"
            )
            for direction, resolved in (
                ("current-producer-to-current-consumer", current_declaration),
                ("previous-producer-to-current-consumer", previous_declaration),
            ):
                major = resolved["producer_major"]
                if major is None:
                    continue
                declaration_matrix.append(
                    {
                        "contractId": contract_id,
                        "major": major,
                        "ownerModule": owner_id,
                        "ownerMode": resolved["owner_mode"],
                        "producerModule": resolved["producer_module"],
                        "producerDeclarationKind": resolved[
                            "producer_declaration_kind"
                        ],
                        "producerMode": resolved["producer_mode"],
                        "transportSenderModule": resolved[
                            "transport_sender_module"
                        ],
                        "transportSenderMode": resolved[
                            "transport_sender_mode"
                        ],
                        "transportSenderDeclarationKind": resolved[
                            "transport_sender_declaration_kind"
                        ],
                        "transportReceiverModule": resolved[
                            "transport_receiver_module"
                        ],
                        "transportPreservesProducer": resolved[
                            "transport_preserves_producer"
                        ],
                        "consumerModule": resolved["consumer_module"],
                        "consumerDeclarationKind": resolved[
                            "consumer_declaration_kind"
                        ],
                        "consumerMode": resolved["consumer_mode"],
                        "producerResolution": resolved["producer_resolution"],
                        "communicationPairSha256": resolved[
                            "communication_pair_sha256"
                        ],
                        "reciprocalResolved": resolved["reciprocal_resolved"],
                        "direction": direction,
                        "executionClass": resolved["execution_class"],
                        "readCompatible": resolved["readable"],
                        "runnable": resolved["runnable"],
                        "deployable": resolved["runnable"],
                        "independentDeployable": independent_deployable,
                        "compatibilityGroupRequired": compatibility_group_required,
                        "activeProducerConsumer": resolved["runnable"],
                        "candidateGreenEligible": independent_deployable,
                    }
                )

    execution_combinations = [
        {
            "combinationId": combination_id,
            "producerAxisValue": producer_axis,
            "consumerAxisValue": consumer_axis,
            "required": True,
            "evidenceStatus": "NOT_RUN",
            "evidenceClass": "candidate-artifact-required",
            "candidateGreenEligible": False,
        }
        for combination_id, producer_axis, consumer_axis in (
            ("N/N", "N", "N"),
            ("N/N-1", "N", "N-1"),
            ("N-1/N", "N-1", "N"),
            ("N-1/N-1", "N-1", "N-1"),
        )
    ]
    declarations.sort(
        key=lambda value: (
            value["contractId"],
            value["major"],
            value["declarationKind"],
            value["moduleId"],
        )
    )
    declaration_matrix.sort(
        key=lambda value: (
            value["contractId"],
            value["major"],
            value["consumerModule"],
            value["direction"],
        )
    )
    return {
        "schemaVersion": "dps.compatibility-matrix/v2",
        "generatedFrom": "Modules/*/module.yaml",
        "policyRef": "governance/policies/compatibility-policy.yaml",
        "policySha256": policy_digest,
        "unknownMajorBehavior": "reject",
        "missingMajorBehavior": "reject",
        "unknownModeBehavior": "reject",
        "missingModeBehavior": "reject",
        "majorDeclarations": declarations,
        "declarationMatrix": declaration_matrix,
        "axisMeaning": {
            "producerAxis": "producer-module-version-from-signed-release-bom",
            "consumerAxis": "consumer-module-version-from-signed-release-bom",
            "N": "candidate-module-version",
            "NMinus1": "previous-stable-module-version",
        },
        "executionCombinations": execution_combinations,
        "independentDeployable": bool(declaration_matrix)
        and all(row["independentDeployable"] for row in declaration_matrix),
        "compatibilityGroupRequired": any(
            row["compatibilityGroupRequired"] for row in declaration_matrix
        ),
        "candidateGreenEligible": bool(declaration_matrix)
        and all(row["candidateGreenEligible"] for row in declaration_matrix),
    }


def _bom_signature_sha256(bom: Mapping[str, Any], label: str) -> str:
    signature = _object(bom.get("signature"), label + ".signature")
    _exact_keys(signature, {"algorithm", "key_id", "value"}, label + ".signature")
    return sha256_bytes(canonical_bytes(signature))


def _verify_raw_release_bom(
    bom: Mapping[str, Any],
    expected_status: str,
    trust_policy: Mapping[str, Any],
    signature_verifier: Callable[[bytes, bytes, Any], None],
    label: str,
) -> None:
    if bom.get("schema_version") != "dps.release-bom/v1":
        _fail("unknown_bom_version", label + " schema version is unsupported")
    if bom.get("status") != expected_status:
        _fail("previous_stable_bom_status", label + " must have status " + expected_status)
    _external_id(bom.get("bom_id"), label + ".bom_id")
    _integer(bom.get("release_bom_generation"), label + ".release_bom_generation", 1)
    _sha256(bom.get("activation_token_sha256"), label + ".activation_token_sha256")
    _bom_module_inventory(bom, label)
    signature = _object(bom.get("signature"), label + ".signature")
    _exact_keys(signature, {"algorithm", "key_id", "value"}, label + ".signature")
    if signature.get("algorithm") != BOM_ALGORITHM:
        _fail("unknown_signature_algorithm", label + " signature algorithm is unsupported")
    key_id = _external_id(signature.get("key_id"), label + ".signature.key_id")
    signer = _find_unique(
        _array(trust_policy["trusted_bom_signers"], "trusted_bom_signers"),
        lambda value: value.get("key_id") == key_id,
        label + " signer",
    )
    signer_key = _trusted_key(
        signer,
        {"key_id", "algorithm", "public_key_pem_path", "public_key_sha256"},
        BOM_ALGORITHM,
        label + " signer",
    )
    unsigned = dict(bom)
    unsigned.pop("signature")
    signature_verifier(
        signer_key,
        b"dps-release-bom/v1\n" + canonical_bytes(unsigned),
        signature["value"],
    )


def _bom_module_inventory(
    bom: Mapping[str, Any], label: str
) -> dict[str, dict[str, str]]:
    inventory: dict[str, dict[str, str]] = {}
    for index, value in enumerate(_array(bom.get("modules"), label + ".modules")):
        module = _object(value, f"{label}.modules[{index}]")
        module_id = _text(module.get("module_id"), f"{label}.modules[{index}].module_id")
        version = _text(module.get("version"), f"{label}.modules[{index}].version")
        artifact_sha256 = _sha256(
            module.get("sha256"), f"{label}.modules[{index}].sha256"
        )
        if MODULE_ID_RE.fullmatch(module_id) is None or SEMVER_RE.fullmatch(version) is None:
            _fail("invalid_bom_module_selection", label + " module id/version is invalid")
        if module_id in inventory:
            _fail("invalid_bom_module_selection", label + " module ids must be unique")
        inventory[module_id] = {
            "module_id": module_id,
            "version": version,
            "artifact_sha256": artifact_sha256,
        }
    if not inventory:
        _fail("invalid_bom_module_selection", label + " module inventory is empty")
    return inventory


def _matrix_row_identity(row: Mapping[str, Any]) -> tuple[str, str]:
    digest = sha256_bytes(canonical_bytes(row))
    return "compatibility-row-" + digest[:32], digest


def _combination_selection(
    combination_id: str,
    producer_id: str,
    consumer_id: str,
    candidate_modules: Mapping[str, Mapping[str, str]],
    previous_modules: Mapping[str, Mapping[str, str]],
) -> tuple[Mapping[str, str], Mapping[str, str]]:
    axes = {
        "N/N": (candidate_modules, candidate_modules),
        "N/N-1": (candidate_modules, previous_modules),
        "N-1/N": (previous_modules, candidate_modules),
        "N-1/N-1": (previous_modules, previous_modules),
    }
    producer_axis, consumer_axis = axes[combination_id]
    if producer_id not in producer_axis or consumer_id not in consumer_axis:
        _fail(
            "compatibility_module_selection_missing",
            "candidate and previous stable BOMs must contain every compatibility module",
        )
    return producer_axis[producer_id], consumer_axis[consumer_id]


def _expected_compatibility_group(
    matrix: Mapping[str, Any],
    graph: Mapping[str, set[str]],
    candidate_modules: Mapping[str, Mapping[str, str]],
    matrix_sha256: str,
    candidate_bom_sha256: str,
) -> dict[str, Any]:
    group_rows = [
        row
        for row in _array(matrix.get("declarationMatrix"), "compatibility declarationMatrix")
        if isinstance(row, Mapping) and row.get("compatibilityGroupRequired") is True
    ]
    if not group_rows:
        return {
            "required": False,
            "group_id": None,
            "group_version": None,
            "members": [],
            "edges": [],
            "blocked_matrix_row_ids": [],
            "release_order": [],
            "rollback_unit": None,
            "status": "NOT_APPLICABLE",
        }
    members: set[str] = set()
    edges: list[dict[str, Any]] = []
    blocked: list[str] = []
    for row in group_rows:
        row_id, _row_sha = _matrix_row_identity(row)
        producer = row.get("runtimeProducerModule", row.get("producerModule"))
        sender = row.get("transportSenderModule", producer)
        consumer = row.get("consumerModule")
        for module_id in (producer, sender, consumer):
            if isinstance(module_id, str) and module_id in graph:
                members.add(module_id)
        edges.append(
            {
                "matrix_row_id": row_id,
                "producer_module": producer,
                "transport_sender_module": sender,
                "consumer_module": consumer,
                "contract_id": row.get("contractId"),
                "major": row.get("major"),
            }
        )
        if row.get("runnable") is not True:
            blocked.append(row_id)
    pending = list(members)
    while pending:
        module_id = pending.pop()
        for dependency in graph.get(module_id, set()):
            if dependency not in members:
                members.add(dependency)
                pending.append(dependency)
    if not blocked:
        _fail(
            "compatibility_group_shape_invalid",
            "a compatibility group must contain at least one fail-closed declaration row",
        )
    if not members.issubset(candidate_modules):
        _fail(
            "compatibility_group_module_missing",
            "compatibility-group closure is not fully selected by the candidate BOM",
        )
    restricted_graph = {
        module_id: set(graph.get(module_id, set())).intersection(members)
        for module_id in members
    }
    release_order = [
        module_id
        for wave in _dependency_waves(restricted_graph)
        for module_id in wave
    ]
    row_ids = sorted(edge["matrix_row_id"] for edge in edges)
    group_digest = sha256_bytes(
        canonical_bytes(
            {
                "candidate_bom_sha256": candidate_bom_sha256,
                "matrix_sha256": matrix_sha256,
                "members": sorted(members),
                "row_ids": row_ids,
            }
        )
    )
    group_id = "compatibility-group-" + group_digest[:32]
    return {
        "required": True,
        "group_id": group_id,
        "group_version": "1.0.0",
        "members": [candidate_modules[module_id] for module_id in sorted(members)],
        "edges": sorted(edges, key=lambda value: value["matrix_row_id"]),
        "blocked_matrix_row_ids": sorted(blocked),
        "release_order": release_order,
        "rollback_unit": "compatibility-group:" + group_id,
        "status": "PASS",
    }


def _validate_combination_observation(
    observation: Mapping[str, Any],
    expected: Mapping[str, Any],
    candidate_bom_sha256: str,
    previous_bom_sha256: str,
    matrix_sha256: str,
    outer_window: tuple[datetime, datetime],
) -> None:
    keys = {
        "schema_version",
        "artifact_id",
        "evidence_kind",
        "matrix_row_id",
        "combination_id",
        "expectation",
        "environment_id",
        "candidate_bom_sha256",
        "previous_stable_bom_sha256",
        "compatibility_snapshot_sha256",
        "producer",
        "consumer",
        "started_at",
        "finished_at",
        "executed_test_count",
        "skip_count",
        "partial_count",
        "not_run_count",
        "observed_outcome",
        "side_effect_count",
        "status",
    }
    _exact_keys(observation, keys, "compatibility combination observation")
    if observation.get("schema_version") != "dps.compatibility-combination-observation/v1":
        _fail("unknown_compatibility_observation_version", "combination observation version is unsupported")
    for key in (
        "matrix_row_id",
        "combination_id",
        "expectation",
        "environment_id",
        "producer",
        "consumer",
    ):
        if observation.get(key) != expected.get(key):
            _fail("compatibility_observation_mismatch", "raw combination observation differs from signed execution row")
    if (
        observation.get("candidate_bom_sha256") != candidate_bom_sha256
        or observation.get("previous_stable_bom_sha256") != previous_bom_sha256
        or observation.get("compatibility_snapshot_sha256") != matrix_sha256
    ):
        _fail("compatibility_observation_binding_mismatch", "raw combination observation has stale BOM or matrix binding")
    if observation.get("evidence_kind") != "REAL_EXTERNAL" or observation.get("status") != PASS:
        _fail("compatibility_observation_not_pass", "raw compatibility evidence must be real external PASS")
    _integer(observation.get("executed_test_count"), "executed_test_count", 1)
    for key in ("skip_count", "partial_count", "not_run_count", "side_effect_count"):
        _zero(observation.get(key), "combination observation." + key)
    expected_outcome = "RUNNABLE" if expected["expectation"] == "RUNNABLE" else "FAIL_CLOSED"
    if observation.get("observed_outcome") != expected_outcome:
        _fail("compatibility_observation_outcome_mismatch", "combination observation did not meet its fixed expectation")
    started = _parse_utc(
        observation["started_at"],
        "compatibility combination observation.started_at",
    )
    finished = _parse_utc(
        observation["finished_at"],
        "compatibility combination observation.finished_at",
    )
    if finished <= started:
        _fail(
            "invalid_time_window",
            "compatibility combination observation must have positive duration",
        )
    if started < outer_window[0] or finished > outer_window[1]:
        _fail("compatibility_observation_window_mismatch", "combination observation is outside the signed F9 window")


def _validate_f9_compatibility_execution(
    binding: Mapping[str, Any],
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    release_bom: Mapping[str, Any],
    previous_bom: Mapping[str, Any],
    matrix: Mapping[str, Any],
    graph: Mapping[str, set[str]],
    policy_sha256: str,
    evidence: Mapping[str, Any],
    trust_policy: Mapping[str, Any],
    signature_verifier: Callable[[bytes, bytes, Any], None],
    outer_window: tuple[datetime, datetime],
) -> None:
    execution_sha256 = _sha256(
        binding["compatibility_execution_sha256"],
        "module_rollout_lines.compatibility_execution_sha256",
    )
    _artifact_id, artifact = _raw_json_artifact(
        raw_artifacts,
        binding["compatibility_execution_artifact_id"],
        execution_sha256,
        "compatibility execution evidence",
    )
    artifact_keys = {
        "schema_version",
        "artifact_id",
        "evidence_kind",
        "required",
        "status",
        "integration_commit",
        "candidate_release_bom",
        "previous_stable_release_bom",
        "compatibility_snapshot",
        "environment_id",
        "issued_at",
        "expires_at",
        "row_set_sha256",
        "row_results",
        "compatibility_group",
        "candidate_green_eligible",
        "attestation",
    }
    _exact_keys(artifact, artifact_keys, "compatibility execution evidence")
    if artifact.get("schema_version") != "dps.compatibility-execution-evidence/v1":
        _fail("unknown_compatibility_execution_version", "compatibility execution evidence version is unsupported")
    if artifact.get("evidence_kind") != "REAL_EXTERNAL" or artifact.get("required") is not True:
        _fail("compatibility_execution_not_external", "compatibility execution evidence must be required real external evidence")
    _pass(artifact.get("status"), "compatibility execution evidence.status")
    if artifact.get("candidate_green_eligible") is not True:
        _fail("compatibility_execution_not_green", "candidate execution evidence must be explicitly PASS after recomputation")
    environment_id = _text(evidence["environment"]["environment_id"], "environment.environment_id")
    if artifact.get("environment_id") != environment_id:
        _fail("compatibility_execution_environment_mismatch", "compatibility execution evidence environment is stale")
    if artifact.get("integration_commit") != release_bom.get("integration_commit"):
        _fail("compatibility_execution_commit_mismatch", "compatibility execution evidence is not for the signed integration commit")

    candidate_bom_sha256 = _sha256(evidence["release_bom"]["sha256"], "release_bom.sha256")
    previous_bom_sha256 = _sha256(
        binding["previous_stable_bom_sha256"],
        "module_rollout_lines.previous_stable_bom_sha256",
    )
    candidate_binding = _object(artifact["candidate_release_bom"], "candidate_release_bom")
    previous_binding = _object(artifact["previous_stable_release_bom"], "previous_stable_release_bom")
    bom_binding_keys = {"bom_id", "sha256", "generation", "activation_token_sha256", "signature_sha256"}
    _exact_keys(candidate_binding, bom_binding_keys, "candidate_release_bom")
    _exact_keys(previous_binding, bom_binding_keys, "previous_stable_release_bom")
    expected_candidate_binding = {
        "bom_id": release_bom.get("bom_id"),
        "sha256": candidate_bom_sha256,
        "generation": release_bom.get("release_bom_generation"),
        "activation_token_sha256": release_bom.get("activation_token_sha256"),
        "signature_sha256": _bom_signature_sha256(release_bom, "candidate Release BOM"),
    }
    expected_previous_binding = {
        "bom_id": previous_bom.get("bom_id"),
        "sha256": previous_bom_sha256,
        "generation": previous_bom.get("release_bom_generation"),
        "activation_token_sha256": previous_bom.get("activation_token_sha256"),
        "signature_sha256": _bom_signature_sha256(previous_bom, "previous stable Release BOM"),
    }
    if candidate_binding != expected_candidate_binding or previous_binding != expected_previous_binding:
        _fail("compatibility_execution_bom_mismatch", "execution evidence does not bind the exact current and previous signed BOMs")

    matrix_sha256 = _sha256(
        binding["compatibility_matrix_sha256"],
        "module_rollout_lines.compatibility_matrix_sha256",
    )
    snapshot_binding = _object(artifact["compatibility_snapshot"], "compatibility_snapshot")
    _exact_keys(snapshot_binding, {"schema_version", "sha256", "policy_sha256"}, "compatibility_snapshot")
    if snapshot_binding != {
        "schema_version": "dps.compatibility-matrix/v2",
        "sha256": matrix_sha256,
        "policy_sha256": policy_sha256,
    }:
        _fail("compatibility_execution_snapshot_mismatch", "execution evidence binds a stale matrix or policy")

    issued_at = _parse_utc(artifact["issued_at"], "compatibility execution issued_at")
    expires_at = _parse_utc(artifact["expires_at"], "compatibility execution expires_at")
    if issued_at > outer_window[0] or expires_at < outer_window[1] or issued_at >= expires_at:
        _fail("compatibility_execution_stale", "compatibility execution evidence must predate and cover the full F9 window")

    rows = [
        row
        for row in _array(matrix.get("declarationMatrix"), "compatibility declarationMatrix")
        if isinstance(row, Mapping)
    ]
    if matrix.get("independentDeployable") is True:
        required_rows = [row for row in rows if row.get("candidateGreenEligible") is True]
    elif matrix.get("compatibilityGroupRequired") is True:
        required_rows = [row for row in rows if row.get("runnable") is True]
    else:
        _fail("compatibility_declarations_not_deployable", "static declaration matrix is neither independent nor group deployable")
    if not required_rows:
        _fail("compatibility_execution_rows_missing", "candidate green requires at least one active runtime compatibility row")
    row_identities = {_matrix_row_identity(row)[0]: row for row in required_rows}
    if len(row_identities) != len(required_rows):
        _fail("duplicate_compatibility_matrix_row", "required matrix rows must be unique")
    expected_row_set_sha256 = sha256_bytes(canonical_bytes(sorted(row_identities)))
    if artifact.get("row_set_sha256") != expected_row_set_sha256:
        _fail("compatibility_execution_row_set_mismatch", "execution evidence row-set digest is incomplete or stale")

    row_results = _array(artifact["row_results"], "compatibility execution row_results")
    observed_row_ids: set[str] = set()
    candidate_modules = _bom_module_inventory(release_bom, "candidate Release BOM")
    previous_modules = _bom_module_inventory(previous_bom, "previous stable Release BOM")
    for index, value in enumerate(row_results):
        result = _object(value, f"row_results[{index}]")
        result_keys = {
            "matrix_row_id",
            "matrix_row_sha256",
            "contract_id",
            "major",
            "owner_module",
            "runtime_producer_module",
            "transport_sender_module",
            "consumer_module",
            "producer_mode",
            "consumer_mode",
            "communication_pair_sha256",
            "combination_results",
            "row_status",
        }
        _exact_keys(result, result_keys, f"row_results[{index}]")
        row_id = _external_id(result["matrix_row_id"], f"row_results[{index}].matrix_row_id")
        if row_id in observed_row_ids or row_id not in row_identities:
            _fail("compatibility_execution_row_inventory", "execution rows must exactly cover each required matrix row once")
        observed_row_ids.add(row_id)
        row = row_identities[row_id]
        expected_row_sha = _matrix_row_identity(row)[1]
        runtime_producer = row.get("runtimeProducerModule", row.get("producerModule"))
        transport_sender = row.get("transportSenderModule", runtime_producer)
        communication_sha = row.get("communicationPairSha256")
        if not isinstance(communication_sha, str):
            communication_sha = sha256_bytes(canonical_bytes(row))
        expected_identity = {
            "matrix_row_id": row_id,
            "matrix_row_sha256": expected_row_sha,
            "contract_id": row.get("contractId"),
            "major": row.get("major"),
            "owner_module": row.get("ownerModule"),
            "runtime_producer_module": runtime_producer,
            "transport_sender_module": transport_sender,
            "consumer_module": row.get("consumerModule"),
            "producer_mode": "active",
            "consumer_mode": "active",
            "communication_pair_sha256": communication_sha,
        }
        for key, expected_value in expected_identity.items():
            if result.get(key) != expected_value:
                _fail("compatibility_execution_row_mismatch", "execution row differs from the canonical runtime matrix row")
        _pass(result.get("row_status"), f"row_results[{index}].row_status")
        combinations = _array(result["combination_results"], f"row_results[{index}].combination_results")
        if len(combinations) != 4:
            _fail("compatibility_combination_inventory", "each execution row requires exactly four compatibility combinations")
        seen_combinations: set[str] = set()
        for combination_value in combinations:
            combination = _object(combination_value, "combination result")
            combination_keys = {
                "combination_id",
                "expectation",
                "producer",
                "consumer",
                "raw_evidence_artifact_id",
                "raw_evidence_sha256",
                "evidence_status",
                "evidence_class",
                "environment_id",
                "executed_test_count",
                "skip_count",
                "partial_count",
                "not_run_count",
            }
            _exact_keys(combination, combination_keys, "combination result")
            combination_id = combination.get("combination_id")
            if combination_id not in {"N/N", "N/N-1", "N-1/N", "N-1/N-1"} or combination_id in seen_combinations:
                _fail("compatibility_combination_inventory", "combination ids must be exact, complete, and unique")
            seen_combinations.add(str(combination_id))
            expectation = "RUNNABLE"
            if matrix.get("compatibilityGroupRequired") is True and combination_id in {"N/N-1", "N-1/N"}:
                expectation = "FAIL_CLOSED_BY_GROUP"
            producer_selection, consumer_selection = _combination_selection(
                str(combination_id),
                str(runtime_producer),
                str(row.get("consumerModule")),
                candidate_modules,
                previous_modules,
            )
            expected_combination = {
                "matrix_row_id": row_id,
                "combination_id": combination_id,
                "expectation": expectation,
                "producer": producer_selection,
                "consumer": consumer_selection,
                "environment_id": environment_id,
            }
            for key in ("expectation", "producer", "consumer", "environment_id"):
                if combination.get(key) != expected_combination[key]:
                    _fail("compatibility_combination_mismatch", "combination module versions or expectation differ from signed BOM axes")
            if combination.get("evidence_status") != PASS or combination.get("evidence_class") != "REAL_CANDIDATE_ARTIFACT":
                _fail("compatibility_combination_not_pass", "every combination requires real candidate PASS evidence")
            _integer(combination.get("executed_test_count"), "combination.executed_test_count", 1)
            for key in ("skip_count", "partial_count", "not_run_count"):
                _zero(combination.get(key), "combination." + key)
            raw_sha = _sha256(combination.get("raw_evidence_sha256"), "combination.raw_evidence_sha256")
            _raw_id, observation = _raw_json_artifact(
                raw_artifacts,
                combination.get("raw_evidence_artifact_id"),
                raw_sha,
                "compatibility combination observation",
            )
            if observation.get("artifact_id") != _raw_id:
                _fail("compatibility_observation_id_mismatch", "raw observation artifact id is not self-bound")
            _validate_combination_observation(
                observation,
                expected_combination,
                candidate_bom_sha256,
                previous_bom_sha256,
                matrix_sha256,
                outer_window,
            )
        if seen_combinations != {"N/N", "N/N-1", "N-1/N", "N-1/N-1"}:
            _fail("compatibility_combination_inventory", "required compatibility combination is missing")
    if observed_row_ids != set(row_identities):
        _fail("compatibility_execution_row_inventory", "execution evidence omits or adds a required matrix row")

    expected_group = _expected_compatibility_group(
        matrix,
        graph,
        candidate_modules,
        matrix_sha256,
        candidate_bom_sha256,
    )
    if artifact.get("compatibility_group") != expected_group:
        _fail("compatibility_group_evidence_mismatch", "compatibility-group members, edges, order, or rollback unit are incomplete")

    attestation = _object(artifact["attestation"], "compatibility execution attestation")
    attestation_keys = {"evidence_issuer_identity", "runner_key_id", "algorithm", "signature_base64"}
    _exact_keys(attestation, attestation_keys, "compatibility execution attestation")
    if attestation.get("algorithm") != P1363_ALGORITHM:
        _fail("unknown_signature_algorithm", "compatibility execution attestation algorithm is unsupported")
    factory_binding = _object(evidence["factory_binding"], "factory_binding")
    if attestation.get("evidence_issuer_identity") != factory_binding.get("evidence_issuer_identity"):
        _fail("issuer_mismatch", "compatibility execution issuer differs from the separated evidence authority")
    issuer = _find_unique(
        _array(trust_policy["trusted_issuers"], "trusted_issuers"),
        lambda value: value.get("runner_key_id") == attestation.get("runner_key_id")
        and value.get("issuer_identity") == attestation.get("evidence_issuer_identity"),
        "compatibility execution evidence issuer",
    )
    issuer_key = _trusted_key(
        issuer,
        {
            "issuer_identity",
            "runner_key_id",
            "algorithm",
            "public_key_pem_path",
            "public_key_sha256",
            "allowed_verification_levels",
        },
        P1363_ALGORITHM,
        "compatibility execution evidence issuer",
    )
    if "SCALE_VERIFIED" not in _array(issuer["allowed_verification_levels"], "allowed_verification_levels"):
        _fail("issuer_scope_mismatch", "compatibility execution issuer is not authorized for SCALE_VERIFIED")
    unsigned_artifact = dict(artifact)
    unsigned_artifact.pop("attestation")
    signature_verifier(
        issuer_key,
        b"dps-compatibility-execution-evidence/v1\n" + canonical_bytes(unsigned_artifact),
        attestation["signature_base64"],
    )


def _validate_f9_rollout_lines(
    binding_value: Any,
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    release_bom: Mapping[str, Any] | None,
    evidence: Mapping[str, Any] | None,
    trust_policy: Mapping[str, Any] | None,
    signature_verifier: Callable[[bytes, bytes, Any], None] | None,
    outer_window: tuple[datetime, datetime],
) -> None:
    binding = _object(binding_value, "module_rollout_lines")
    binding_keys = {
        "dependency_graph_artifact_id",
        "dependency_graph_sha256",
        "compatibility_matrix_artifact_id",
        "compatibility_matrix_sha256",
        "compatibility_policy_artifact_id",
        "compatibility_policy_sha256",
        "previous_stable_bom_artifact_id",
        "previous_stable_bom_sha256",
        "compatibility_execution_artifact_id",
        "compatibility_execution_sha256",
        "manifest_artifacts",
        "contract_schema_artifacts",
        "lines",
    }
    _exact_keys(binding, binding_keys, "module_rollout_lines")
    if (
        release_bom is None
        or evidence is None
        or trust_policy is None
        or signature_verifier is None
    ):
        _fail("release_bom_context_missing", "F9 rollout lines require the verified Release BOM and evidence")
    integration_commit = release_bom.get("integration_commit")
    if (
        not isinstance(integration_commit, str)
        or GIT_OBJECT_RE.fullmatch(integration_commit) is None
        or integration_commit != evidence.get("baseline_commit")
    ):
        _fail("bom_integration_commit_mismatch", "F9 Release BOM must bind the exact signed evidence baseline")

    graph_digest = _sha256(binding["dependency_graph_sha256"], "module_rollout_lines.dependency_graph_sha256")
    compatibility_digest = _sha256(
        binding["compatibility_matrix_sha256"],
        "module_rollout_lines.compatibility_matrix_sha256",
    )
    if release_bom.get("dependency_dag_sha256") != graph_digest:
        _fail("dependency_graph_binding_mismatch", "Release BOM dependency DAG digest does not match F9 evidence")
    if release_bom.get("compatibility_matrix_sha256") != compatibility_digest:
        _fail("compatibility_matrix_binding_mismatch", "Release BOM compatibility digest does not match F9 evidence")

    policy_digest = _sha256(
        binding["compatibility_policy_sha256"],
        "module_rollout_lines.compatibility_policy_sha256",
    )
    _policy_artifact_id, policy = _raw_json_artifact(
        raw_artifacts,
        binding["compatibility_policy_artifact_id"],
        policy_digest,
        "compatibility policy",
    )
    if (
        policy.get("schemaVersion") != "dps.compatibility-policy/v1"
        or policy.get("contractMajorModes", {}).get("resolution", {}).get("unknownMajorBehavior") != "reject"
        or policy.get("contractMajorModes", {}).get("resolution", {}).get("missingMajorBehavior") != "reject"
        or policy.get("contractMajorModes", {}).get("resolution", {}).get("unknownModeBehavior") != "reject"
        or policy.get("contractMajorModes", {}).get("resolution", {}).get("missingModeBehavior") != "reject"
        or policy.get("combinations")
        != {
            "N/N": "required",
            "N/N-1": "required",
            "N-1/N": "required",
            "N-1/N-1": "required",
            "unknown-N+1": "reject",
        }
    ):
        _fail("compatibility_policy_not_fail_closed", "signed compatibility policy is missing mandatory fail-closed semantics")
    instruction_hashes = _array(
        release_bom.get("instruction_hashes"),
        "Release BOM instruction_hashes",
    )
    policy_bindings = [
        item
        for item in instruction_hashes
        if isinstance(item, Mapping)
        and item.get("path") == "governance/policies/compatibility-policy.yaml"
    ]
    if len(policy_bindings) != 1 or policy_bindings[0].get("sha256") != policy_digest:
        _fail("compatibility_policy_bom_mismatch", "Release BOM must bind the exact compatibility policy digest")

    previous_bom_digest = _sha256(
        binding["previous_stable_bom_sha256"],
        "module_rollout_lines.previous_stable_bom_sha256",
    )
    _previous_artifact_id, previous_bom = _raw_json_artifact(
        raw_artifacts,
        binding["previous_stable_bom_artifact_id"],
        previous_bom_digest,
        "previous stable Release BOM",
    )
    if (
        release_bom.get("previous_stable_bom") != previous_bom.get("bom_id")
        or release_bom.get("previous_stable_bom_sha256") != previous_bom_digest
    ):
        _fail("previous_stable_bom_binding_mismatch", "candidate BOM does not bind the exact previous stable BOM")
    _verify_raw_release_bom(
        previous_bom,
        "STABLE",
        trust_policy,
        signature_verifier,
        "previous stable Release BOM",
    )

    _candidate_module_inventory = _bom_module_inventory(
        release_bom, "candidate Release BOM"
    )
    bom_modules: dict[str, str] = {}
    for index, item in enumerate(_array(release_bom.get("modules"), "Release BOM modules")):
        module = _object(item, f"Release BOM modules[{index}]")
        module_id = _text(module.get("module_id"), f"Release BOM modules[{index}].module_id")
        manifest_sha256 = _sha256(module.get("manifest_sha256"), f"Release BOM modules[{index}].manifest_sha256")
        if MODULE_ID_RE.fullmatch(module_id) is None or module_id in bom_modules:
            _fail("invalid_bom_module_graph", "Release BOM module ids must be unique kebab-case identifiers")
        bom_modules[module_id] = manifest_sha256
    if not bom_modules or len(bom_modules) > 4096:
        _fail("invalid_bom_module_graph", "F9 Release BOM must contain 1..4096 complete module entries")

    manifest_bindings: dict[str, tuple[str, str]] = {}
    manifest_artifact_ids: set[str] = set()
    for index, item in enumerate(_array(binding["manifest_artifacts"], "module_rollout_lines.manifest_artifacts")):
        manifest_binding = _object(item, f"module_rollout_lines.manifest_artifacts[{index}]")
        _exact_keys(
            manifest_binding,
            {"module_id", "raw_artifact_id", "manifest_sha256"},
            f"module_rollout_lines.manifest_artifacts[{index}]",
        )
        module_id = _text(manifest_binding["module_id"], f"manifest_artifacts[{index}].module_id")
        artifact_id = _external_id(manifest_binding["raw_artifact_id"], f"manifest_artifacts[{index}].raw_artifact_id")
        manifest_sha256 = _sha256(manifest_binding["manifest_sha256"], f"manifest_artifacts[{index}].manifest_sha256")
        if module_id in manifest_bindings or artifact_id in manifest_artifact_ids:
            _fail("duplicate_manifest_binding", "F9 manifest module and raw artifact bindings must be unique")
        manifest_bindings[module_id] = (artifact_id, manifest_sha256)
        manifest_artifact_ids.add(artifact_id)
    if set(manifest_bindings) != set(bom_modules):
        _fail("manifest_inventory_incomplete", "F9 raw manifest bindings must cover exactly every signed BOM module")

    bom_contracts: dict[tuple[str, int], tuple[str, str]] = {}
    for index, value in enumerate(_array(release_bom.get("contracts"), "Release BOM contracts")):
        contract = _object(value, f"Release BOM contracts[{index}]")
        contract_id = _text(contract.get("contract_id"), f"Release BOM contracts[{index}].contract_id")
        major = _integer(contract.get("major"), f"Release BOM contracts[{index}].major", 1)
        owner = _text(contract.get("owner_module"), f"Release BOM contracts[{index}].owner_module")
        schema_sha256 = _sha256(contract.get("schema_sha256"), f"Release BOM contracts[{index}].schema_sha256")
        key = (contract_id, major)
        if key in bom_contracts:
            _fail("duplicate_contract_major", "Release BOM contract-major rows must be unique")
        bom_contracts[key] = (owner, schema_sha256)
    schema_bindings: dict[tuple[str, int], tuple[str, str, str]] = {}
    for index, value in enumerate(
        _array(binding["contract_schema_artifacts"], "module_rollout_lines.contract_schema_artifacts")
    ):
        schema_binding = _object(value, f"contract_schema_artifacts[{index}]")
        _exact_keys(
            schema_binding,
            {"contract_id", "major", "owner_module", "raw_artifact_id", "schema_sha256"},
            f"contract_schema_artifacts[{index}]",
        )
        contract_id = _text(schema_binding["contract_id"], f"contract_schema_artifacts[{index}].contract_id")
        major = _integer(schema_binding["major"], f"contract_schema_artifacts[{index}].major", 1)
        owner = _text(schema_binding["owner_module"], f"contract_schema_artifacts[{index}].owner_module")
        artifact_id = _external_id(schema_binding["raw_artifact_id"], f"contract_schema_artifacts[{index}].raw_artifact_id")
        schema_sha256 = _sha256(schema_binding["schema_sha256"], f"contract_schema_artifacts[{index}].schema_sha256")
        key = (contract_id, major)
        if key in schema_bindings:
            _fail("duplicate_contract_schema_binding", "contract schema bindings must be unique by major")
        schema_bindings[key] = (owner, artifact_id, schema_sha256)
    if set(schema_bindings) != set(bom_contracts):
        _fail("contract_schema_inventory_incomplete", "raw contract schemas must exactly cover the signed BOM contract inventory")
    contract_producers: dict[tuple[str, int], set[str]] = {}
    for key, (owner, artifact_id, schema_sha256) in sorted(schema_bindings.items()):
        if bom_contracts[key] != (owner, schema_sha256):
            _fail("contract_schema_bom_mismatch", "raw contract schema differs from the signed BOM")
        _schema_artifact_id, contract_schema = _raw_json_artifact(
            raw_artifacts,
            artifact_id,
            schema_sha256,
            f"contract schema {key[0]}/v{key[1]}",
        )
        properties = _object(contract_schema.get("properties"), "contract schema properties")
        producer_schema = _object(properties.get("producer_module"), "contract producer_module")
        producers: set[str] = set()
        if isinstance(producer_schema.get("const"), str):
            producers.add(producer_schema["const"])
        values = producer_schema.get("enum")
        if isinstance(values, list):
            producers.update(value for value in values if isinstance(value, str))
        if not producers:
            _fail("contract_producer_unconstrained", "signed contract schema must constrain producer_module")
        contract_producers[key] = producers

    graph: dict[str, set[str]] = {}
    edge_reasons: dict[tuple[str, str], str] = {}
    contracts_by_module: dict[str, dict[str, list[dict[str, Any]]]] = {}
    communications_by_module: dict[str, list[dict[str, Any]]] = {}
    for module_id in sorted(bom_modules):
        artifact_id, binding_sha256 = manifest_bindings[module_id]
        if binding_sha256 != bom_modules[module_id]:
            _fail("manifest_bom_mismatch", f"F9 manifest binding for {module_id} differs from the signed BOM")
        _observed_artifact_id, manifest = _raw_json_artifact(
            raw_artifacts,
            artifact_id,
            binding_sha256,
            f"module manifest {module_id}",
        )
        if manifest.get("schemaVersion") != "dps.module/v1":
            _fail("unknown_manifest_version", f"F9 manifest for {module_id} has an unsupported schema version")
        module = _object(manifest.get("module"), f"module manifest {module_id}.module")
        if module.get("id") != module_id:
            _fail("manifest_identity_mismatch", f"F9 raw manifest identity does not match {module_id}")
        dependencies: set[str] = set()
        for dependency_index, dependency_value in enumerate(
            _array(manifest.get("dependencies"), f"module manifest {module_id}.dependencies")
        ):
            dependency = _object(dependency_value, f"module manifest {module_id}.dependencies[{dependency_index}]")
            _exact_keys(
                dependency,
                {"moduleId", "versionRange", "required", "reason"},
                f"module manifest {module_id}.dependencies[{dependency_index}]",
            )
            provider = _text(dependency["moduleId"], f"module manifest {module_id}.dependencies[].moduleId")
            _text(dependency["versionRange"], f"module manifest {module_id}.dependencies[].versionRange")
            if type(dependency["required"]) is not bool:
                _fail("invalid_manifest_dependency", f"module manifest {module_id} dependency.required must be boolean")
            reason = _text(dependency["reason"], f"module manifest {module_id}.dependencies[].reason")
            if provider == module_id or provider in dependencies or provider not in bom_modules:
                _fail("invalid_manifest_dependency", "F9 manifest dependencies must be unique known non-self modules")
            dependencies.add(provider)
            edge_reasons[(module_id, provider)] = reason
        graph[module_id] = dependencies

        communication = _object(
            manifest.get("communication"),
            f"module manifest {module_id}.communication",
        )
        _exact_keys(
            communication,
            {"inbound", "outbound"},
            f"module manifest {module_id}.communication",
        )
        normalized_edges: list[dict[str, Any]] = []
        for direction in ("inbound", "outbound"):
            for edge_index, edge_value in enumerate(
                _array(
                    communication[direction],
                    f"module manifest {module_id}.communication.{direction}",
                )
            ):
                edge = _object(
                    edge_value,
                    f"module manifest {module_id}.communication.{direction}[{edge_index}]",
                )
                required_edge_keys = {
                    "peerModule",
                    "contractId",
                    "major",
                    "direction",
                    "transport",
                    "timeoutMs",
                    "retryPolicy",
                    "idempotencyKey",
                    "authScope",
                    "failureMode",
                }
                allowed_edge_keys = required_edge_keys | {"preserveProducer"}
                if set(edge) != required_edge_keys and set(edge) != allowed_edge_keys:
                    _fail(
                        "invalid_communication_shape",
                        "signed Manifest communication edge has missing or unknown fields",
                    )
                if edge.get("direction") != direction:
                    _fail("invalid_communication_direction", "communication container and direction disagree")
                peer = _text(edge.get("peerModule"), "communication peerModule")
                contract_id = _text(edge.get("contractId"), "communication contractId")
                major = _integer(edge.get("major"), "communication major", 1)
                external_peer = (
                    peer in EXTERNAL_COMMUNICATION_PEERS
                    or peer.startswith("external:")
                )
                if peer not in bom_modules and not external_peer:
                    _fail("communication_peer_not_in_bom", "F9 module communication peer must be selected by the signed BOM")
                normalized = dict(edge)
                normalized["moduleId"] = module_id
                normalized["contractId"] = contract_id
                normalized["major"] = major
                normalized_edges.append(normalized)
        communications_by_module[module_id] = normalized_edges

        contracts = _object(manifest.get("contracts"), f"module manifest {module_id}.contracts")
        _exact_keys(contracts, {"provided", "consumed"}, f"module manifest {module_id}.contracts")
        compatibility = _object(
            manifest.get("compatibility"),
            f"module manifest {module_id}.compatibility",
        )
        for behavior in (
            "unknownMajorBehavior",
            "missingMajorBehavior",
            "unknownModeBehavior",
            "missingModeBehavior",
        ):
            if compatibility.get(behavior) != "reject":
                _fail(
                    "manifest_compatibility_not_fail_closed",
                    f"module manifest {module_id} must reject unknown and missing major/mode values",
                )
        normalized_contracts: dict[str, list[dict[str, Any]]] = {
            "provided": [],
            "consumed": [],
        }
        for direction in ("provided", "consumed"):
            for contract_index, contract_value in enumerate(
                _array(contracts[direction], f"module manifest {module_id}.contracts.{direction}")
            ):
                contract = _object(
                    contract_value,
                    f"module manifest {module_id}.contracts.{direction}[{contract_index}]",
                )
                _exact_keys(
                    contract,
                    {"contractId", "major", "source", "status", "mode", "ownerModule"},
                    f"module manifest {module_id}.contracts.{direction}[{contract_index}]",
                )
                contract_id = _text(contract["contractId"], f"module manifest {module_id} contractId")
                major = _integer(contract["major"], f"module manifest {module_id} contract major", 1)
                source = _text(contract["source"], f"module manifest {module_id} contract source")
                status = _text(contract["status"], f"module manifest {module_id} contract status")
                mode = _text(contract["mode"], f"module manifest {module_id} contract mode")
                owner = _text(contract["ownerModule"], f"module manifest {module_id} contract owner")
                if mode not in CONTRACT_COMPATIBILITY_MODES:
                    _fail(
                        "unknown_contract_mode",
                        f"module manifest {module_id} has an unsupported contract compatibility mode",
                    )
                if direction == "provided" and mode == "compat-read":
                    _fail(
                        "invalid_provider_contract_mode",
                        "compat-read is a consumer-only compatibility mode",
                    )
                if (status == "retired") != (mode == "retired"):
                    _fail(
                        "retired_contract_mode_mismatch",
                        "retired contract status and mode must be declared together",
                    )
                normalized_contracts[direction].append(
                    {
                        "contractId": contract_id,
                        "major": major,
                        "source": source,
                        "status": status,
                        "mode": mode,
                        "ownerModule": owner,
                    }
                )
        contracts_by_module[module_id] = normalized_contracts

    waves = _dependency_waves(graph)
    expected_edges = [
        {"consumer": consumer, "provider": provider, "reason": edge_reasons[(consumer, provider)]}
        for consumer in sorted(graph)
        for provider in sorted(graph[consumer])
    ]
    expected_dependency_artifact = {
        "schemaVersion": "dps.dependency-graph/v1",
        "generatedFrom": "Modules/*/module.yaml",
        "failOnCycle": True,
        "nodes": sorted(graph),
        "edges": expected_edges,
        "parallelWaves": waves,
    }
    _artifact_id, dependency_artifact = _raw_json_artifact(
        raw_artifacts,
        binding["dependency_graph_artifact_id"],
        graph_digest,
        "module_rollout_lines dependency graph",
    )
    if dependency_artifact != expected_dependency_artifact:
        _fail(
            "dependency_graph_manifest_mismatch",
            "F9 canonical dependency graph must equal the graph rebuilt from all signed BOM manifests",
        )

    expected_compatibility_artifact = _build_f9_compatibility_artifact(
        contracts_by_module,
        communications_by_module,
        contract_producers,
        policy_digest,
    )
    for consumer, contract_root in contracts_by_module.items():
        for contract in contract_root["consumed"]:
            owner = contract["ownerModule"]
            if owner != consumer and owner not in graph[consumer]:
                _fail(
                    "hidden_contract_dependency",
                    "consumed contract owner must be an explicit manifest dependency",
                )
    _compatibility_artifact_id, compatibility_artifact = _raw_json_artifact(
        raw_artifacts,
        binding["compatibility_matrix_artifact_id"],
        compatibility_digest,
        "module_rollout_lines compatibility matrix",
    )
    if compatibility_artifact != expected_compatibility_artifact:
        _fail(
            "compatibility_matrix_manifest_mismatch",
            "F9 compatibility matrix must equal the matrix rebuilt from all signed BOM manifests",
        )
    lines = _array(binding["lines"], "module_rollout_lines.lines")
    if not 1 <= len(lines) <= 4:
        _fail("parallel_scope_exceeded", "F9 permits one to four independent module rollout lines")
    module_to_line: dict[str, str] = {}
    line_ids: set[str] = set()
    for index, item in enumerate(lines):
        line = _object(item, f"module_rollout_lines.lines[{index}]")
        _exact_keys(line, {"line_id", "module_ids", "status"}, f"module_rollout_lines.lines[{index}]")
        line_id = _external_id(line["line_id"], f"module_rollout_lines.lines[{index}].line_id")
        if line_id in line_ids:
            _fail("duplicate_rollout_line", "F9 rollout line ids must be unique")
        line_ids.add(line_id)
        _pass(line["status"], f"module_rollout_lines.lines[{index}].status")
        module_ids = _array(line["module_ids"], f"module_rollout_lines.lines[{index}].module_ids")
        if not module_ids:
            _fail("empty_rollout_line", "every F9 rollout line must contain at least one module")
        for module_value in module_ids:
            module_id = _text(module_value, f"module_rollout_lines.lines[{index}].module_ids[]")
            if module_id not in graph or module_id in module_to_line:
                _fail("rollout_module_invalid", "F9 rollout modules must be unique members of the verified BOM graph")
            module_to_line[module_id] = line_id

    for source, source_line in module_to_line.items():
        pending = list(graph[source])
        observed: set[str] = set()
        while pending:
            dependency = pending.pop()
            if dependency in observed:
                continue
            observed.add(dependency)
            dependency_line = module_to_line.get(dependency)
            if dependency_line is not None and dependency_line != source_line:
                _fail("rollout_lines_not_independent", "F9 rollout lines contain a cross-line dependency")
            pending.extend(graph[dependency])

    _validate_f9_compatibility_execution(
        binding,
        raw_artifacts,
        release_bom,
        previous_bom,
        expected_compatibility_artifact,
        graph,
        policy_digest,
        evidence,
        trust_policy,
        signature_verifier,
        outer_window,
    )


def _validate_f9_canary_prerequisite(
    binding_value: Any,
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    release_bom: Mapping[str, Any] | None,
    evidence: Mapping[str, Any] | None,
    trust_policy: Mapping[str, Any] | None,
    signature_verifier: Callable[[bytes, bytes, Any], None] | None,
    f9_started: datetime,
) -> None:
    binding = _object(binding_value, "canary_prerequisite")
    _exact_keys(binding, {"receipt_id", "raw_artifact_id", "raw_artifact_sha256"}, "canary_prerequisite")
    receipt_id = _external_id(binding["receipt_id"], "canary_prerequisite.receipt_id")
    _artifact_id, receipt = _raw_json_artifact(
        raw_artifacts,
        binding["raw_artifact_id"],
        binding["raw_artifact_sha256"],
        "canary_prerequisite",
    )
    receipt_keys = {
        "schema_version",
        "receipt_id",
        "source_stage",
        "verification_level",
        "status",
        "required",
        "evidence_kind",
        "evidence_id",
        "baseline_commit",
        "release_bom_id",
        "release_bom_sha256",
        "candidate_artifact_sha256",
        "issued_at",
        "evidence_issuer_identity",
        "signature",
    }
    _exact_keys(receipt, receipt_keys, "F8 canary prerequisite receipt")
    if receipt["schema_version"] != "dps.external-verification-receipt/v1":
        _fail("unknown_prerequisite_receipt", "F8 prerequisite receipt schema version is unsupported")
    if receipt["receipt_id"] != receipt_id or receipt["source_stage"] != "f8":
        _fail("prerequisite_binding_mismatch", "F9 prerequisite must bind an F8 receipt")
    if receipt["verification_level"] != "CANARY_VERIFIED" or receipt["status"] != PASS:
        _fail("prerequisite_level_mismatch", "F9 requires a PASS CANARY_VERIFIED prerequisite receipt")
    _true(receipt["required"], "F8 prerequisite receipt.required")
    if receipt["evidence_kind"] != "REAL_EXTERNAL":
        _fail("non_real_prerequisite", "F8 prerequisite receipt must represent real external evidence")
    _external_id(receipt["evidence_id"], "F8 prerequisite receipt.evidence_id")
    if release_bom is None or evidence is None or trust_policy is None or signature_verifier is None:
        _fail("prerequisite_context_missing", "F9 prerequisite validation requires signed envelope context")
    bom_binding = _object(evidence["release_bom"], "release_bom")
    if (
        receipt["baseline_commit"] != evidence["baseline_commit"]
        or receipt["release_bom_id"] != release_bom.get("bom_id")
        or receipt["release_bom_sha256"] != bom_binding.get("sha256")
        or receipt["candidate_artifact_sha256"] != bom_binding.get("artifact_sha256")
    ):
        _fail("prerequisite_bom_mismatch", "F8 prerequisite receipt must bind the exact F9 commit and signed BOM")
    if not isinstance(receipt["baseline_commit"], str) or GIT_OBJECT_RE.fullmatch(receipt["baseline_commit"]) is None:
        _fail("invalid_prerequisite_receipt", "F8 prerequisite baseline must be a full Git object id")
    for key in ("release_bom_sha256", "candidate_artifact_sha256"):
        _sha256(receipt[key], f"F8 prerequisite receipt.{key}")
    issued_at = _parse_utc(receipt["issued_at"], "F8 prerequisite receipt.issued_at")
    if issued_at > f9_started:
        _fail("prerequisite_issued_after_scale", "F8 prerequisite receipt must predate F9 measurement")

    signature = _object(receipt["signature"], "F8 prerequisite receipt.signature")
    _exact_keys(signature, {"algorithm", "runner_key_id", "value"}, "F8 prerequisite receipt.signature")
    if signature["algorithm"] != P1363_ALGORITHM:
        _fail("unknown_signature_algorithm", "F8 prerequisite receipt signature algorithm is unsupported")
    runner_key_id = _external_id(signature["runner_key_id"], "F8 prerequisite receipt.signature.runner_key_id")
    issuer_identity = _text(receipt["evidence_issuer_identity"], "F8 prerequisite receipt.evidence_issuer_identity", 2)
    issuer = _find_unique(
        _array(trust_policy["trusted_issuers"], "trusted_issuers"),
        lambda value: value.get("runner_key_id") == runner_key_id
        and value.get("issuer_identity") == issuer_identity,
        "F8 prerequisite evidence issuer",
    )
    key_bytes = _trusted_key(
        issuer,
        {
            "issuer_identity",
            "runner_key_id",
            "algorithm",
            "public_key_pem_path",
            "public_key_sha256",
            "allowed_verification_levels",
        },
        P1363_ALGORITHM,
        "F8 prerequisite evidence issuer",
    )
    if "CANARY_VERIFIED" not in _array(issuer["allowed_verification_levels"], "allowed_verification_levels"):
        _fail("issuer_scope_mismatch", "F8 prerequisite issuer is not trusted for CANARY_VERIFIED")
    unsigned_receipt = dict(receipt)
    unsigned_receipt.pop("signature")
    signature_verifier(
        key_bytes,
        b"dps-external-verification-receipt/v1\n" + canonical_bytes(unsigned_receipt),
        signature["value"],
    )


def _validate_backlog_sample(depth_value: Any, age_value: Any, label: str) -> tuple[int, float]:
    depth = _integer(depth_value, f"{label}.backlog_depth", 0)
    age = _number(age_value, f"{label}.oldest_backlog_age_seconds", 0)
    if (depth == 0) != (age == 0):
        _fail("backlog_sample_inconsistent", f"{label} backlog depth and oldest age must become zero together")
    return depth, float(age)


def _validate_f9_load_artifact(
    name: str,
    run: Mapping[str, Any],
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    environment_id: str,
    outer_window: tuple[datetime, datetime],
) -> Mapping[str, Any]:
    artifact_id, artifact = _raw_json_artifact(
        raw_artifacts,
        run["artifact_id"],
        run["artifact_sha256"],
        f"load_runs.{name}",
    )
    artifact_keys = {
        "schema_version",
        "artifact_id",
        "run_id",
        "profile",
        "evidence_kind",
        "environment_id",
        "actor_kind",
        "actor_scope_id",
        "actor_digest_algorithm",
        "actor_sets",
        "windows",
        "recovery_samples",
    }
    _exact_keys(artifact, artifact_keys, f"load_runs.{name} raw artifact")
    if artifact["schema_version"] != "dps.f9-load-run-artifact/v1":
        _fail("unknown_load_artifact", f"load_runs.{name} raw artifact schema version is unsupported")
    expected = {
        "sustained": ("REAL_SUSTAINED", "REAL_EXTERNAL", "REAL_DEVICE_BINDING"),
        "burst": ("REAL_BURST", "REAL_EXTERNAL", "REAL_DEVICE_BINDING"),
        "simulated": ("SIMULATED_CAPACITY", "SIMULATED", "SIMULATED_DEVICE"),
    }[name]
    if (
        artifact["artifact_id"] != artifact_id
        or artifact["run_id"] != run["run_id"]
        or artifact["profile"] != expected[0]
        or artifact["evidence_kind"] != expected[1]
        or artifact["actor_kind"] != expected[2]
        or artifact["environment_id"] != environment_id
    ):
        _fail("load_artifact_binding_mismatch", f"load_runs.{name} raw artifact does not bind the signed run tuple")
    if artifact["actor_digest_algorithm"] != "HMAC_SHA256_SCOPE_V1":
        _fail("load_actor_digest_algorithm", "F9 raw actor identities must use scoped HMAC-SHA256 digests")
    actor_scope_id = _external_id(artifact["actor_scope_id"], f"load_runs.{name}.actor_scope_id")

    actor_sets: dict[str, frozenset[str]] = {}
    for index, item in enumerate(_array(artifact["actor_sets"], f"load_runs.{name}.actor_sets")):
        actor_set = _object(item, f"load_runs.{name}.actor_sets[{index}]")
        _exact_keys(actor_set, {"actor_set_id", "actor_digests"}, f"load_runs.{name}.actor_sets[{index}]")
        actor_set_id = _external_id(
            actor_set["actor_set_id"],
            f"load_runs.{name}.actor_sets[{index}].actor_set_id",
        )
        if actor_set_id in actor_sets:
            _fail("duplicate_actor_set", f"load_runs.{name} actor-set ids must be unique")
        digests: set[str] = set()
        for digest_value in _array(
            actor_set["actor_digests"],
            f"load_runs.{name}.actor_sets[{index}].actor_digests",
        ):
            digest = _sha256(digest_value, f"load_runs.{name}.actor_sets[{index}].actor_digests[]")
            if digest in digests:
                _fail("duplicate_actor_digest", f"load_runs.{name} actor digests must be unique within a set")
            digests.add(digest)
        if not digests:
            _fail("empty_actor_set", f"load_runs.{name} actor sets cannot be empty")
        actor_sets[actor_set_id] = frozenset(digests)
    if not actor_sets or len(actor_sets) > 4096:
        _fail("invalid_actor_sets", f"load_runs.{name} must contain 1..4096 actor sets")

    windows = _array(artifact["windows"], f"load_runs.{name}.windows")
    if not windows or len(windows) > 100000:
        _fail("invalid_load_windows", f"load_runs.{name} must contain 1..100000 raw windows")
    signed_started = _parse_utc(run["started_at"], f"load_runs.{name}.started_at")
    signed_finished = _parse_utc(run["finished_at"], f"load_runs.{name}.finished_at")
    previous_finished: datetime | None = None
    total_duration = 0.0
    concurrency_values: list[int] = []
    observed_actors: set[str] = set()
    backlog_was_nonzero = False
    previous_finish_backlog: tuple[int, float] | None = None
    for index, item in enumerate(windows):
        window = _object(item, f"load_runs.{name}.windows[{index}]")
        _exact_keys(
            window,
            {
                "sequence",
                "started_at",
                "finished_at",
                "actor_set_id",
                "maximum_backlog_depth",
                "maximum_oldest_backlog_age_seconds",
                "backlog_depth_at_finish",
                "oldest_backlog_age_seconds_at_finish",
            },
            f"load_runs.{name}.windows[{index}]",
        )
        if window["sequence"] != index:
            _fail("load_window_sequence_invalid", f"load_runs.{name} windows must be contiguous and zero-based")
        started = _parse_utc(window["started_at"], f"load_runs.{name}.windows[{index}].started_at")
        finished = _parse_utc(window["finished_at"], f"load_runs.{name}.windows[{index}].finished_at")
        seconds = (finished - started).total_seconds()
        if seconds <= 0 or seconds > 300:
            _fail("load_window_duration_invalid", f"load_runs.{name} raw windows must cover 1..300 seconds")
        if previous_finished is not None and started != previous_finished:
            _fail("load_window_discontinuity", f"load_runs.{name} raw windows cannot contain gaps or overlaps")
        actor_set_id = _external_id(window["actor_set_id"], f"load_runs.{name}.windows[{index}].actor_set_id")
        if actor_set_id not in actor_sets:
            _fail("unknown_actor_set", f"load_runs.{name} raw window references an unknown actor set")
        actors = actor_sets[actor_set_id]
        concurrency_values.append(len(actors))
        observed_actors.update(actors)
        maximum_backlog_depth, maximum_backlog_age = _validate_backlog_sample(
            window["maximum_backlog_depth"],
            window["maximum_oldest_backlog_age_seconds"],
            f"load_runs.{name}.windows[{index}].maximum",
        )
        backlog_depth, backlog_age = _validate_backlog_sample(
            window["backlog_depth_at_finish"],
            window["oldest_backlog_age_seconds_at_finish"],
            f"load_runs.{name}.windows[{index}]",
        )
        if maximum_backlog_depth < backlog_depth or maximum_backlog_age < backlog_age:
            _fail(
                "backlog_window_inconsistent",
                f"load_runs.{name} window maxima cannot be lower than the finish observation",
            )
        if maximum_backlog_age > 120:
            _fail("long_term_backlog", f"load_runs.{name} raw window observed backlog older than 120 seconds")
        if (
            previous_finish_backlog is not None
            and previous_finish_backlog[0] > 0
            and backlog_depth > 0
            and (
                backlog_depth > previous_finish_backlog[0]
                or backlog_age > previous_finish_backlog[1]
            )
        ):
            _fail(
                "growing_backlog",
                f"load_runs.{name} raw windows show monotonically growing unresolved backlog",
            )
        backlog_was_nonzero = backlog_was_nonzero or maximum_backlog_depth > 0
        previous_finish_backlog = (backlog_depth, backlog_age)
        total_duration += seconds
        previous_finished = finished
        if index == 0 and started != signed_started:
            _fail("load_artifact_time_mismatch", f"load_runs.{name} raw windows must start at the signed run start")
    if previous_finished != signed_finished:
        _fail("load_artifact_time_mismatch", f"load_runs.{name} raw windows must finish at the signed run finish")
    signed_duration = _number(run["duration_seconds"], f"load_runs.{name}.duration_seconds", 0.000001)
    recomputed_duration = (signed_finished - signed_started).total_seconds()
    if (
        signed_started < outer_window[0]
        or signed_finished > outer_window[1]
        or abs(total_duration - recomputed_duration) > 1e-9
        or abs(float(signed_duration) - recomputed_duration) > 1e-9
    ):
        _fail("load_duration_mismatch", f"load_runs.{name} signed and raw UTC durations must match exactly")

    recovery_samples = _array(artifact["recovery_samples"], f"load_runs.{name}.recovery_samples")
    if len(recovery_samples) < 7 or len(recovery_samples) > 10000:
        _fail("recovery_sample_count_invalid", f"load_runs.{name} needs 7..10000 recovery samples")
    previous_observed: datetime | None = None
    first_zero_at: datetime | None = None
    for index, item in enumerate(recovery_samples):
        sample = _object(item, f"load_runs.{name}.recovery_samples[{index}]")
        _exact_keys(
            sample,
            {"sequence", "observed_at", "backlog_depth", "oldest_backlog_age_seconds"},
            f"load_runs.{name}.recovery_samples[{index}]",
        )
        if sample["sequence"] != index:
            _fail("recovery_sequence_invalid", f"load_runs.{name} recovery samples must be contiguous and zero-based")
        observed_at = _parse_utc(sample["observed_at"], f"load_runs.{name}.recovery_samples[{index}].observed_at")
        if index == 0 and observed_at != signed_finished:
            _fail("recovery_window_mismatch", f"load_runs.{name} recovery sampling must begin at run finish")
        if previous_observed is not None:
            gap = (observed_at - previous_observed).total_seconds()
            if gap <= 0 or gap > 60:
                _fail("recovery_sample_gap", f"load_runs.{name} recovery samples must be at most 60 seconds apart")
        if observed_at > outer_window[1]:
            _fail("recovery_outside_measurement", f"load_runs.{name} recovery sample lies outside signed measurement")
        depth, age = _validate_backlog_sample(
            sample["backlog_depth"],
            sample["oldest_backlog_age_seconds"],
            f"load_runs.{name}.recovery_samples[{index}]",
        )
        if index == 0 and previous_finish_backlog != (depth, age):
            _fail(
                "recovery_state_discontinuity",
                f"load_runs.{name} first recovery sample must equal the final raw-window backlog tuple",
            )
        if age > 120:
            _fail("long_term_backlog", f"load_runs.{name} recovery observed backlog older than 120 seconds")
        backlog_was_nonzero = backlog_was_nonzero or depth > 0
        if depth == 0 and first_zero_at is None:
            first_zero_at = observed_at
        elif first_zero_at is not None and depth != 0:
            _fail("backlog_rebounded", f"load_runs.{name} backlog cannot rebound after recovery")
        previous_observed = observed_at
    if not backlog_was_nonzero:
        _fail("recovery_not_observed", f"load_runs.{name} raw observations must include a non-zero backlog before recovery")
    if first_zero_at is None or (first_zero_at - signed_finished).total_seconds() > 120:
        _fail("backlog_recovery_too_slow", f"load_runs.{name} backlog must clear within 120 seconds")
    if previous_observed is None or (previous_observed - first_zero_at).total_seconds() < 300:
        _fail("backlog_recovery_not_stable", f"load_runs.{name} backlog recovery must remain stable for five minutes")
    _true(run["recovered_without_long_term_backlog"], f"load_runs.{name}.recovered_without_long_term_backlog")

    recomputed_concurrency = min(concurrency_values) if name == "sustained" else max(concurrency_values)
    declared_concurrency = _integer(run["concurrency"], f"load_runs.{name}.concurrency", 1)
    if declared_concurrency != recomputed_concurrency:
        _fail("load_concurrency_mismatch", f"load_runs.{name} concurrency must equal the value recomputed from raw windows")
    threshold = {"sustained": 100, "burst": 200, "simulated": 400}[name]
    if recomputed_concurrency < threshold:
        _fail("load_threshold_not_met", f"load_runs.{name} raw concurrency does not meet {threshold}")
    if name == "sustained" and recomputed_duration < 72 * 3600:
        _fail("time_threshold_not_met", "100-device sustained raw run must cover at least 72 hours")
    return {
        "actor_scope_id": actor_scope_id,
        "actor_digests": frozenset(observed_actors),
        "concurrency": recomputed_concurrency,
        "duration_seconds": recomputed_duration,
    }


def _validate_f9(
    payload_value: Any,
    outer_window: tuple[datetime, datetime],
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None,
    release_bom: Mapping[str, Any] | None,
    evidence: Mapping[str, Any] | None,
    trust_policy: Mapping[str, Any] | None,
    signature_verifier: Callable[[bytes, bytes, Any], None] | None,
) -> None:
    payload = _object(payload_value, "payload")
    _exact_keys(
        payload,
        {
            "waves",
            "canary_prerequisite",
            "module_rollout_lines",
            "managed_devices",
            "load_runs",
            "control_plane_instances",
            "postgres_restore",
            "gbrain_capacity",
            "crash_recovery",
            "rollback_drills",
            "soak",
            "previous_stable_bom",
            "legacy_runtime_adapter",
        },
        "F9 payload",
    )
    names = ["2", "10", "20", "50", "100", "200"]
    waves = _validate_ordered_waves(payload["waves"], names, outer_window)
    for index, (wave, _started, _finished) in enumerate(waves):
        _exact_keys(wave, {"name", "device_count", "started_at", "finished_at", "status"}, f"waves[{index}]")
        if wave["device_count"] != int(wave["name"]):
            _fail("device_count_mismatch", f"wave {wave['name']} must have the matching device count")
        _pass(wave["status"], f"waves[{index}].status")
    _integer(payload["managed_devices"], "managed_devices", 200)
    _validate_f9_canary_prerequisite(
        payload["canary_prerequisite"],
        raw_artifacts,
        release_bom,
        evidence,
        trust_policy,
        signature_verifier,
        outer_window[0],
    )
    _validate_f9_rollout_lines(
        payload["module_rollout_lines"],
        raw_artifacts,
        release_bom,
        evidence,
        trust_policy,
        signature_verifier,
        outer_window,
    )

    load_runs = _object(payload["load_runs"], "load_runs")
    _exact_keys(load_runs, {"sustained", "burst", "simulated"}, "load_runs")
    run_ids: set[str] = set()
    load_artifact_ids: set[str] = set()
    artifact_digests: set[str] = set()
    load_keys = {
        "run_id",
        "artifact_id",
        "evidence_kind",
        "concurrency",
        "duration_seconds",
        "started_at",
        "finished_at",
        "status",
        "recovered_without_long_term_backlog",
        "artifact_sha256",
    }
    raw_load_results: dict[str, Mapping[str, Any]] = {}
    for name in ("sustained", "burst", "simulated"):
        run = _object(load_runs[name], f"load_runs.{name}")
        _exact_keys(run, load_keys, f"load_runs.{name}")
        run_id = _text(run["run_id"], f"load_runs.{name}.run_id", 8)
        artifact_id = _external_id(run["artifact_id"], f"load_runs.{name}.artifact_id")
        digest = _sha256(run["artifact_sha256"], f"load_runs.{name}.artifact_sha256")
        if run_id in run_ids or artifact_id in load_artifact_ids or digest in artifact_digests:
            _fail("load_evidence_not_independent", "100 sustained, 200 burst, and 400 simulated runs need distinct ids and artifacts")
        run_ids.add(run_id)
        load_artifact_ids.add(artifact_id)
        artifact_digests.add(digest)
        kind = "SIMULATED" if name == "simulated" else "REAL_EXTERNAL"
        if run["evidence_kind"] != kind:
            _fail("load_evidence_kind_mismatch", f"load_runs.{name} has the wrong evidence kind")
        _pass(run["status"], f"load_runs.{name}.status")
        raw_load_results[name] = _validate_f9_load_artifact(
            name,
            run,
            raw_artifacts,
            _text(evidence["environment"]["environment_id"], "environment.environment_id") if evidence else "",
            outer_window,
        )
    if raw_load_results["sustained"]["actor_scope_id"] != raw_load_results["burst"]["actor_scope_id"]:
        _fail("real_load_scope_mismatch", "F9 sustained and burst actor digests must share one trusted scope")
    if raw_load_results["simulated"]["actor_scope_id"] == raw_load_results["sustained"]["actor_scope_id"]:
        _fail("simulation_scope_collision", "F9 simulated actor digests must use a separate scope")
    real_actors = set(raw_load_results["sustained"]["actor_digests"]) | set(
        raw_load_results["burst"]["actor_digests"]
    )
    simulated_actors = set(raw_load_results["simulated"]["actor_digests"])
    if real_actors.intersection(simulated_actors):
        _fail("simulation_actor_collision", "F9 real and simulated actor digests must be disjoint")
    managed_devices = _integer(payload["managed_devices"], "managed_devices", 200)
    if len(real_actors) < managed_devices:
        _fail("managed_device_cardinality_mismatch", "F9 managed device count exceeds raw real-device observations")
    _integer(payload["control_plane_instances"], "control_plane_instances", 2)

    restore = _object(payload["postgres_restore"], "postgres_restore")
    restore_keys = {"status", "declared_rpo_minutes", "measured_rpo_minutes", "declared_rto_minutes", "measured_rto_minutes"}
    _exact_keys(restore, restore_keys, "postgres_restore")
    _pass(restore["status"], "postgres_restore.status")
    declared_rpo = _number(restore["declared_rpo_minutes"], "postgres_restore.declared_rpo_minutes", 0.000001)
    measured_rpo = _number(restore["measured_rpo_minutes"], "postgres_restore.measured_rpo_minutes", 0)
    declared_rto = _number(restore["declared_rto_minutes"], "postgres_restore.declared_rto_minutes", 0.000001)
    measured_rto = _number(restore["measured_rto_minutes"], "postgres_restore.measured_rto_minutes", 0)
    if measured_rpo > declared_rpo or measured_rto > declared_rto:
        _fail("recovery_objective_breached", "measured PostgreSQL RPO/RTO exceeds the declared objective")

    capacity = _object(payload["gbrain_capacity"], "gbrain_capacity")
    capacity_keys = {"status", "modeled_sources", "oauth_clients", "connection_budget", "projection_capacity_devices"}
    _exact_keys(capacity, capacity_keys, "gbrain_capacity")
    _pass(capacity["status"], "gbrain_capacity.status")
    _integer(capacity["modeled_sources"], "gbrain_capacity.modeled_sources", 200)
    _integer(capacity["oauth_clients"], "gbrain_capacity.oauth_clients", 1)
    _integer(capacity["connection_budget"], "gbrain_capacity.connection_budget", 1)
    _integer(capacity["projection_capacity_devices"], "gbrain_capacity.projection_capacity_devices", 200)

    crash = _object(payload["crash_recovery"], "crash_recovery")
    crash_keys = {"factory", "control_plane", "edge_worker"}
    _exact_keys(crash, crash_keys, "crash_recovery")
    for key in crash_keys:
        _pass(crash[key], f"crash_recovery.{key}")
    rollback_drills = _array(payload["rollback_drills"], "rollback_drills")
    required_scopes = {"site", "database", "edge", "gbrain", "module"}
    observed_scopes: set[str] = set()
    for index, item in enumerate(rollback_drills):
        drill = _object(item, f"rollback_drills[{index}]")
        _exact_keys(drill, {"scope", "status", "duration_minutes"}, f"rollback_drills[{index}]")
        scope = _text(drill["scope"], f"rollback_drills[{index}].scope")
        if scope not in required_scopes or scope in observed_scopes:
            _fail("rollback_scope_invalid", "rollback scopes must contain each required scope exactly once")
        observed_scopes.add(scope)
        _pass(drill["status"], f"rollback_drills[{index}].status")
        duration = _number(drill["duration_minutes"], f"rollback_drills[{index}].duration_minutes", 0)
        if scope == "module" and duration > 5:
            _fail("rollback_too_slow", "F9 ordinary module rollback must complete within five minutes")
    if observed_scopes != required_scopes:
        _fail("rollback_scope_missing", "site, database, edge, GBrain, and module rollback drills are all required")

    soak = _object(payload["soak"], "soak")
    soak_keys = {"status", "duration_hours", "cross_scope_leaks", "unauthorized_side_effects", "duplicate_side_effects", "false_successes", "long_term_backlog"}
    _exact_keys(soak, soak_keys, "soak")
    _pass(soak["status"], "soak.status")
    _number(soak["duration_hours"], "soak.duration_hours", 72)
    for key in ("cross_scope_leaks", "unauthorized_side_effects", "duplicate_side_effects", "false_successes"):
        _zero(soak[key], f"soak.{key}")
    if soak["long_term_backlog"] is not False:
        _fail("long_term_backlog", "72-hour soak must finish without long-term backlog")
    previous = _object(payload["previous_stable_bom"], "previous_stable_bom")
    previous_keys = {"available", "artifacts_available", "compatible_schema_available"}
    _exact_keys(previous, previous_keys, "previous_stable_bom")
    for key in previous_keys:
        _true(previous[key], f"previous_stable_bom.{key}")
    legacy = _object(payload["legacy_runtime_adapter"], "legacy_runtime_adapter")
    _exact_keys(legacy, {"status", "remaining_entries_documented"}, "legacy_runtime_adapter")
    if legacy["status"] not in {"RETIRED", "COMPATIBILITY_ONLY"}:
        _fail("legacy_adapter_not_reduced", "legacy runtime adapter must be retired or compatibility-only")
    _true(legacy["remaining_entries_documented"], "legacy_runtime_adapter.remaining_entries_documented")


_STAGE_VALIDATORS: dict[str, Callable[[Any, tuple[datetime, datetime]], None]] = {
    "f8": _validate_f8,
}


def validate_stage_payload(
    stage: str,
    payload: Any,
    outer_window: tuple[datetime, datetime],
    raw_artifacts: Mapping[str, Mapping[str, Any]] | None = None,
    trusted_environment: Mapping[str, Any] | None = None,
    release_bom: Mapping[str, Any] | None = None,
    evidence: Mapping[str, Any] | None = None,
    trust_policy: Mapping[str, Any] | None = None,
    signature_verifier: Callable[[bytes, bytes, Any], None] | None = None,
    evaluated_at: datetime | None = None,
) -> None:
    """Validate stage facts only; this helper never creates an evidence decision."""

    if stage == "f7":
        _validate_f7(
            payload,
            outer_window,
            raw_artifacts,
            trusted_environment,
            release_bom,
            evidence,
            trust_policy,
            signature_verifier,
            evaluated_at,
        )
        return
    if stage == "f6":
        _validate_f6(payload, outer_window, trusted_environment)
        return
    if stage == "f9":
        _validate_f9(
            payload,
            outer_window,
            raw_artifacts,
            release_bom,
            evidence,
            trust_policy,
            signature_verifier,
        )
        return
    if stage not in _STAGE_VALIDATORS:
        _fail("unknown_stage", f"unknown external gate stage {stage!r}")
    _STAGE_VALIDATORS[stage](payload, outer_window)


def evaluate(
    stage: str,
    evidence: Mapping[str, Any],
    trust_policy: Mapping[str, Any],
    signature_verifier: Callable[[bytes, bytes, Any], None] = _openssl_verify_p1363,
    *,
    evaluated_at: datetime | None = None,
) -> GateDecision:
    """Return eligibility only; never sign or persist an evidence receipt."""

    if stage not in STAGE_SPECS:
        _fail("unknown_stage", f"unknown external gate stage {stage!r}")
    if stage == "f7" and F7_GBRAIN_CONTRACT_BINDING_STATUS != "FROZEN":
        _wait(
            "f7_gbrain_contract_binding_stale",
            "F7 GBrain projection/source-binding candidate is stale and must be independently re-frozen",
        )
    if evaluated_at is None:
        evaluated_at = datetime.now(timezone.utc)
    if evaluated_at.tzinfo is None or evaluated_at.utcoffset() != timedelta(0):
        _fail("invalid_evaluation_clock", "external gate evaluation clock must be timezone-aware UTC")
    evaluated_at = evaluated_at.astimezone(timezone.utc)
    window, raw_artifacts, release_bom = _validate_common(stage, evidence, trust_policy, signature_verifier)
    validate_stage_payload(
        stage,
        evidence["payload"],
        window,
        raw_artifacts,
        evidence["environment"],
        release_bom,
        evidence,
        trust_policy,
        signature_verifier,
        evaluated_at,
    )
    digest = sha256_bytes(canonical_bytes(evidence))
    return GateDecision(
        stage=stage,
        status=PASS,
        exit_code=0,
        decision=ELIGIBLE,
        target_verification_level=STAGE_SPECS[stage]["verification_level"],
        reason_code="all_signed_external_facts_and_thresholds_verified",
        message="Eligible for review by a separate external evidence issuer; no receipt was issued.",
        evidence_id=str(evidence["evidence_id"]),
        evidence_sha256=digest,
    )


def run_gate(
    stage: str,
    evidence_path: Path | None,
    trust_policy_path: Path | None,
    signature_verifier: Callable[[bytes, bytes, Any], None] = _openssl_verify_p1363,
    *,
    clock: Callable[[], datetime] = lambda: datetime.now(timezone.utc),
) -> GateDecision:
    target = STAGE_SPECS.get(stage, {}).get("verification_level", "UNRESOLVED")
    try:
        if stage not in STAGE_SPECS:
            _fail("unknown_stage", f"unknown external gate stage {stage!r}")
        if evidence_path is None:
            _wait("external_input_missing", "no external evidence input was supplied")
        if trust_policy_path is None:
            _wait("trust_policy_missing", "no externally managed trust policy was supplied")
        evidence = _read_json(evidence_path, "external evidence input", missing_is_waiting=True)
        trust_policy = _load_and_validate_trust_policy(trust_policy_path)
        evaluated_at = clock()
        if not isinstance(evaluated_at, datetime):
            _fail("invalid_evaluation_clock", "external gate clock did not return a datetime")
        return evaluate(
            stage,
            evidence,
            trust_policy,
            signature_verifier,
            evaluated_at=evaluated_at,
        )
    except ExternalPrerequisiteMissing as exc:
        return GateDecision(
            stage=stage,
            status=WAITING_EXTERNAL,
            exit_code=3,
            decision="NOT_ELIGIBLE",
            target_verification_level=target,
            reason_code=exc.code,
            message=str(exc),
        )
    except ExternalGateError as exc:
        return GateDecision(
            stage=stage,
            status=FAIL,
            exit_code=1,
            decision="NOT_ELIGIBLE",
            target_verification_level=target,
            reason_code=exc.code,
            message=str(exc),
        )
    except Exception:
        return GateDecision(
            stage=stage,
            status=FAIL,
            exit_code=1,
            decision="NOT_ELIGIBLE",
            target_verification_level=target,
            reason_code="unexpected_validator_error",
            message="external gate failed closed due to an unexpected validator error",
        )
