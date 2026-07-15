"""Fail-closed v2 impact planning for the DPS AI Factory.

The analyzer accepts only process-bound capabilities issued by the three fixed
authorities in this module.  Canonical JSON is an audit projection, never an
authority.  The repository policy shipped beside this source is deliberately a
non-production template, so every plan remains ``WAITING_EXTERNAL`` until a
portable cross-process trust provider exists.
"""

from __future__ import annotations

import copy
import datetime as dt
import fnmatch
import hashlib
import hmac
import json
import os
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from types import MappingProxyType
from typing import Any, Dict, Iterable, Mapping, Optional, Sequence, Set, Tuple

from jsonschema import Draft202012Validator, FormatChecker


class ImpactError(ValueError):
    """An input capability or impact plan cannot be trusted."""


_MODULE_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*\Z")
_CONTRACT_ID = re.compile(r"^[a-z][a-z0-9]*(?:\.[a-z0-9]+)+\Z")
_COMMIT = re.compile(r"^[0-9a-f]{40}\Z")
_SHA256 = re.compile(r"^[0-9a-f]{64}\Z")
_NONCE = re.compile(r"^nonce_[0-9a-f]{32}\Z")
_AUTHORITY_RECEIPT_ID = re.compile(
    r"^[a-z][a-z0-9-]*:[a-z0-9][a-z0-9._:-]{7,127}\Z"
)
_REQUEST_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{7,127}\Z")
_CANONICAL_UTC = re.compile(
    r"^[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
    r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z\Z"
)
_RISK = {"R0": 0, "R1": 1, "R2": 2, "R3": 3}
_STAGES = {"development", "shadow", "canary", "rolling", "soaking"}
_ROLES = {
    "impact_planner",
    "contract_architect",
    "module_implementer",
    "independent_test_agent",
    "security_privacy_adversary",
    "reliability_reviewer",
    "windows_zenno_reviewer",
    "evidence_auditor",
    "release_approver",
}
_CHANGE_KINDS = {
    "add-major",
    "additive-schema",
    "mode-transition",
    "introduce-quarantined-major",
}
_ROUTABLE_MODES = {"active", "compat-read"}
_PROVIDER_MODES = {"active", "quarantine-only", "retired"}
_CONTRACT_MODES = _ROUTABLE_MODES | {"quarantine-only", "retired"}
_CONTRACT_STATUSES = {"proposed", "active", "deprecated", "retired"}
_COMMUNICATION_FIELDS = {
    "peerModule", "contractId", "major", "direction", "transport", "timeoutMs",
    "retryPolicy", "idempotencyKey", "authScope", "failureMode",
}
_COMMUNICATION_TRANSPORTS = {
    "in-process-api", "http-api", "event", "command", "receipt",
    "read-only-query", "soul-memory-adapter",
}
_GLOBAL_ENGINEERING_EXACT = {
    ".editorconfig", ".gitattributes", ".gitignore", ".node-version",
    ".powershell-version", ".python-version", "AGENTS.md", "CHANGELOG.md",
    "Directory.Build.props", "Directory.Build.targets",
    "Directory.Packages.props", "Dps.slnx", "NuGet.Config", "README.md",
    "global.json", "package-lock.json", "package.json", "requirements-ci.in",
    "requirements-ci.txt", "toolchain.lock.json",
}
_GLOBAL_ENGINEERING_PREFIXES = (
    ".github/", "Docs/", "Tests/ci/", "Tools/ci/", "Tools/verification/",
    "governance/", "scripts/",
)
_LEGACY_TOMBSTONE_EXACT = {".omo.conf"}
_LEGACY_TOMBSTONE_PREFIXES = (".omo/", "Tools/omo_guard/")
_GIT = Path("/usr/bin/git")
_MAX_WIRE_BYTES = 16 * 1024 * 1024
_MAX_ACTIVE_CAPABILITIES = 4096

_MODULE_ROOT = Path(__file__).resolve().parents[1]
_INTENT_SCHEMA = (
    _MODULE_ROOT.parent
    / "factory-upgrade-intake/contracts/provided/upgrade.intent.v2.schema.json"
)
_RECEIPT_SCHEMA = (
    _MODULE_ROOT.parent
    / "factory-instruction-resolver/contracts/provided/instruction.receipt.v2.schema.json"
)
_PLAN_SCHEMA = (
    _MODULE_ROOT / "contracts/provided/module.change.plan.v2.schema.json"
)

_INTENT_ATTESTATION_FIELDS = {
    "raw_sha256", "upgrade_intent_sha256", "source_peer_module",
    "source_producer_module", "source_contract_id", "source_major",
    "source_upgrade_intent_sha256", "source_audience",
    "source_trust_receipt_id", "source_trust_nonce",
    "source_issued_at", "source_verified_at", "source_expires_at",
    "requester_auth_expires_at", "manifest_ownership_expires_at",
    "approval_expires_at", "producer_module", "contract_id", "major",
    "peer_module", "audience", "trust_receipt_id", "trust_nonce",
    "issued_at", "expires_at", "verification_mac",
}
_RECEIPT_ATTESTATION_FIELDS = {
    "raw_sha256", "receipt_sha256", "receipt_id",
    "source_upgrade_intent_sha256", "source_producer_module",
    "source_contract_id", "source_major", "source_issuer", "source_audience",
    "source_issued_at", "source_expires_at", "source_nonce",
    "source_generation", "source_status", "producer_module", "contract_id",
    "major", "peer_module", "audience", "trust_receipt_id", "trust_nonce",
    "issued_at", "expires_at", "verification_mac",
}
_POLICY_ATTESTATION_FIELDS = {
    "raw_sha256", "policy_id", "producer_module", "contract_id", "major",
    "peer_module", "audience", "trust_receipt_id", "trust_nonce",
    "issued_at", "expires_at", "verification_mac",
}


def _canonical_bytes(value: Any) -> bytes:
    try:
        return json.dumps(
            value,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
            allow_nan=False,
        ).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise ImpactError("value is not strict canonical JSON") from exc


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _domain_sha256(domain: str, value: Any) -> str:
    return _sha256(
        b"DPS\x00" + domain.encode("ascii") + b"\x00" + _canonical_bytes(value)
    )


def _contract_change_sort_key(item: Mapping[str, Any]) -> Tuple[Any, ...]:
    """The frozen Intake/Resolver canonical ordering for multi-change wires."""

    return (
        item["contract_id"], item["major"], item["baseline_commit"],
        item["expected_mode"], item["expected_status"],
        item["expected_baseline_state"], item["change_kind"],
        item["expected_owner_module"], item["expected_source"],
        item["expected_source_sha256"], item["expected_previous_mode"] or "",
        item["expected_previous_source_sha256"] or "",
        item["quarantine_reason"] or "", item["quarantine_evidence_sha256"] or "",
    )


def _unique_object(pairs: Sequence[Tuple[str, Any]]) -> Dict[str, Any]:
    value: Dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ImpactError("duplicate JSON member: " + key)
        value[key] = item
    return value


def _reject_float(_value: str) -> Any:
    raise ImpactError("JSON floating point numbers are forbidden")


def _reject_constant(_value: str) -> Any:
    raise ImpactError("non-finite JSON numbers are forbidden")


def _decode_canonical(raw: bytes, label: str) -> Dict[str, Any]:
    if (
        type(raw) is not bytes
        or not raw
        or len(raw) > _MAX_WIRE_BYTES
        or raw.startswith(b"\xef\xbb\xbf")
    ):
        raise ImpactError(label + " is empty, oversized, non-bytes, or has a BOM")
    try:
        value = json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_float=_reject_float,
            parse_constant=_reject_constant,
        )
    except ImpactError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, TypeError) as exc:
        raise ImpactError(label + " is invalid JSON") from exc
    if type(value) is not dict or _canonical_bytes(value) != raw:
        raise ImpactError(label + " must be one canonical JSON object")
    return value


def _load_validator(path: Path, label: str) -> Draft202012Validator:
    try:
        schema = json.loads(path.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
    except Exception as exc:
        raise ImpactError(label + " Schema is unavailable or invalid") from exc
    return Draft202012Validator(schema, format_checker=FormatChecker())


_INTENT_VALIDATOR = _load_validator(_INTENT_SCHEMA, "upgrade.intent/v2")
_RECEIPT_VALIDATOR = _load_validator(_RECEIPT_SCHEMA, "instruction.receipt/v2")
_PLAN_VALIDATOR = _load_validator(_PLAN_SCHEMA, "module.change.plan/v2")


def _schema_validate(
    value: Mapping[str, Any], validator: Draft202012Validator, label: str
) -> None:
    errors = sorted(
        validator.iter_errors(value),
        key=lambda error: tuple(str(part) for part in error.absolute_path),
    )
    if errors:
        error = errors[0]
        location = "$" + "".join("[%r]" % part for part in error.absolute_path)
        raise ImpactError("%s Schema rejected %s: %s" % (label, location, error.message))


def _parse_utc(value: Any, label: str) -> dt.datetime:
    if type(value) is not str or _CANONICAL_UTC.fullmatch(value) is None:
        raise ImpactError(label + " must use canonical UTC second precision")
    try:
        parsed = dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError as exc:
        raise ImpactError(label + " is not a real UTC timestamp") from exc
    if parsed.strftime("%Y-%m-%dT%H:%M:%SZ") != value:
        raise ImpactError(label + " is not canonical")
    return parsed


def _safe_relative(value: Any, *, reject_hidden: bool) -> str:
    if type(value) is not str or not value or value.startswith("/") or "\\" in value:
        raise ImpactError("repository path must be relative POSIX form")
    if value[-1].isspace() or any(ord(char) < 32 or ord(char) == 127 for char in value):
        raise ImpactError("repository path contains control or trailing whitespace")
    pure = PurePosixPath(value)
    if pure.as_posix() != value or any(part in {"", ".", ".."} for part in pure.parts):
        raise ImpactError("repository path is not normalized")
    if any(part in {".git", ".omo"} for part in pure.parts):
        raise ImpactError("Git and hidden task state are forbidden")
    if reject_hidden and any(part.startswith(".") for part in pure.parts):
        raise ImpactError("hidden write paths are forbidden")
    return value


def _safe_file(root: Path, relative: str) -> Path:
    normalized = _safe_relative(relative, reject_hidden=False)
    candidate = root
    for part in PurePosixPath(normalized).parts:
        candidate /= part
        if candidate.is_symlink():
            raise ImpactError("symlinked repository path is forbidden: " + normalized)
    if not candidate.is_file():
        raise ImpactError("required repository file is missing: " + normalized)
    resolved = candidate.resolve(strict=True)
    if resolved != root and root not in resolved.parents:
        raise ImpactError("repository file escapes the worktree: " + normalized)
    return candidate


def _git_bytes(root: Path, args: Sequence[str], *, required: bool = True) -> bytes:
    if not _GIT.is_file() or _GIT.is_symlink() or not os.access(str(_GIT), os.X_OK):
        raise ImpactError("locked /usr/bin/git is missing or unsafe")
    completed = subprocess.run(
        [
            str(_GIT), "-c", "core.hooksPath=/dev/null", "-c",
            "core.fsmonitor=false", *args,
        ],
        cwd=str(root),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
        env={
            "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_SYSTEM": "/dev/null", "GIT_OPTIONAL_LOCKS": "0",
            "GIT_TERMINAL_PROMPT": "0", "HOME": "/var/empty", "LANG": "C",
            "LC_ALL": "C", "PATH": "/usr/bin:/bin", "TMPDIR": "/tmp",
        },
    )
    if required and completed.returncode != 0:
        raise ImpactError(
            "Git metadata lookup failed: "
            + completed.stderr.decode("utf-8", errors="replace").strip()
        )
    return completed.stdout if completed.returncode == 0 else b""


def _git(root: Path, args: Sequence[str], *, required: bool = True) -> str:
    return _git_bytes(root, args, required=required).decode(
        "utf-8", errors="strict"
    ).strip()


def _git_hash_bytes(root: Path, data: bytes) -> str:
    completed = subprocess.run(
        [str(_GIT), "-c", "core.hooksPath=/dev/null", "hash-object", "--stdin"],
        cwd=str(root), input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        check=False,
        env={
            "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_SYSTEM": "/dev/null", "GIT_OPTIONAL_LOCKS": "0",
            "GIT_TERMINAL_PROMPT": "0", "HOME": "/var/empty", "LANG": "C",
            "LC_ALL": "C", "PATH": "/usr/bin:/bin", "TMPDIR": "/tmp",
        },
    )
    value = completed.stdout.decode("ascii", errors="strict").strip()
    if completed.returncode != 0 or _COMMIT.fullmatch(value) is None:
        raise ImpactError("Git byte hashing failed")
    return value


class TrustedUtcClock:
    """Fixed composition-root clock.  Analyze callers cannot supply time."""

    def __init__(self, *, _fixed_for_tests: Optional[str] = None) -> None:
        self.__fixed = _fixed_for_tests
        if self.__fixed is not None:
            _parse_utc(self.__fixed, "trusted clock")

    @classmethod
    def fixed_for_tests(cls, value: str) -> "TrustedUtcClock":
        return cls(_fixed_for_tests=value)

    def now(self) -> str:
        if self.__fixed is not None:
            return self.__fixed
        return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        )

    def advance_for_tests(self, value: str) -> None:
        if self.__fixed is None:
            raise ImpactError("production trusted clocks cannot be advanced")
        if _parse_utc(value, "trusted clock") <= _parse_utc(
            self.__fixed, "trusted clock"
        ):
            raise ImpactError("test clock must advance monotonically")
        self.__fixed = value


def _attestation_mac(key: bytes, domain: str, material: Mapping[str, Any]) -> str:
    return hmac.new(
        key,
        b"DPS\x00" + domain.encode("ascii") + b"\x00" + _canonical_bytes(material),
        hashlib.sha256,
    ).hexdigest()


def _check_common_attestation(
    *,
    attestation: Mapping[str, Any],
    fields: Set[str],
    key: bytes,
    domain: str,
    clock: TrustedUtcClock,
) -> Dict[str, Any]:
    if type(attestation) is not dict or set(attestation) != fields:
        raise ImpactError("trust attestation has an invalid exact shape")
    material = {
        name: attestation[name] for name in sorted(fields - {"verification_mac"})
    }
    supplied = attestation.get("verification_mac")
    expected = _attestation_mac(key, domain, material)
    if (
        type(supplied) is not str
        or _SHA256.fullmatch(supplied) is None
        or not hmac.compare_digest(supplied, expected)
    ):
        raise ImpactError("trust attestation verification failed")
    receipt = material.get("trust_receipt_id")
    nonce = material.get("trust_nonce")
    if (
        type(receipt) is not str
        or _AUTHORITY_RECEIPT_ID.fullmatch(receipt) is None
        or type(nonce) is not str
        or _NONCE.fullmatch(nonce) is None
    ):
        raise ImpactError("trust attestation identity is invalid")
    now = _parse_utc(clock.now(), "trusted clock")
    issued = _parse_utc(material.get("issued_at"), "trust issued_at")
    expires = _parse_utc(material.get("expires_at"), "trust expires_at")
    if issued > now or expires <= now:
        raise ImpactError("trust attestation is not current")
    return material


class IntentVerifierPort:
    """Concrete local verifier for canonical Intake v2 plus source metadata."""

    def __init__(self, verification_key: bytes, clock: TrustedUtcClock) -> None:
        if type(clock) is not TrustedUtcClock:
            raise ImpactError("intent verifier requires the concrete trusted clock")
        if type(verification_key) is not bytes or len(verification_key) < 32:
            raise ImpactError("intent verifier key must be at least 256 bits")
        self.__key = bytes(verification_key)
        self.__clock = clock

    def trusted_now(self) -> str:
        return self.__clock.now()

    def create_process_bound_attestation(
        self,
        raw_bytes: bytes,
        *,
        source_capability: Mapping[str, Any],
        trust_receipt_id: str,
        trust_nonce: str,
        issued_at: str,
        expires_at: str,
    ) -> Dict[str, Any]:
        intent = _decode_intent(raw_bytes)
        expected_source = {
            "payload_sha256", "upgrade_intent_sha256", "peer_module", "audience",
            "producer_module", "contract_id", "major",
            "trust_receipt_id", "trust_nonce", "issued_at", "verified_at",
            "expires_at", "requester_auth_expires_at",
            "manifest_ownership_expires_at", "approval_expires_at",
        }
        if type(source_capability) is not dict or set(source_capability) != expected_source:
            raise ImpactError("source Intent capability metadata has invalid shape")
        material = {
            "raw_sha256": _sha256(raw_bytes),
            "upgrade_intent_sha256": intent["upgrade_intent_sha256"],
            "source_peer_module": source_capability["peer_module"],
            "source_producer_module": source_capability["producer_module"],
            "source_contract_id": source_capability["contract_id"],
            "source_major": source_capability["major"],
            "source_upgrade_intent_sha256": source_capability[
                "upgrade_intent_sha256"
            ],
            "source_audience": source_capability["audience"],
            "source_trust_receipt_id": source_capability["trust_receipt_id"],
            "source_trust_nonce": source_capability["trust_nonce"],
            "source_issued_at": source_capability["issued_at"],
            "source_verified_at": source_capability["verified_at"],
            "source_expires_at": source_capability["expires_at"],
            "requester_auth_expires_at": source_capability[
                "requester_auth_expires_at"
            ],
            "manifest_ownership_expires_at": source_capability[
                "manifest_ownership_expires_at"
            ],
            "approval_expires_at": source_capability["approval_expires_at"],
            "producer_module": "factory-upgrade-intake",
            "contract_id": "upgrade.intent",
            "major": 2,
            "peer_module": "factory-upgrade-intake",
            "audience": "dps.factory-impact-analyzer.intent",
            "trust_receipt_id": trust_receipt_id,
            "trust_nonce": trust_nonce,
            "issued_at": issued_at,
            "expires_at": expires_at,
        }
        if (
            source_capability["payload_sha256"] != material["raw_sha256"]
            or source_capability["upgrade_intent_sha256"]
            != intent["upgrade_intent_sha256"]
        ):
            raise ImpactError("source Intent payload or full digest mismatch")
        material["verification_mac"] = _attestation_mac(
            self.__key, "dps.factory-impact-analyzer.intent-trust/v2", material
        )
        return material

    def verify(self, raw_bytes: bytes, attestation: Mapping[str, Any]) -> Dict[str, Any]:
        intent = _decode_intent(raw_bytes)
        material = _check_common_attestation(
            attestation=attestation,
            fields=_INTENT_ATTESTATION_FIELDS,
            key=self.__key,
            domain="dps.factory-impact-analyzer.intent-trust/v2",
            clock=self.__clock,
        )
        if (
            material["raw_sha256"] != _sha256(raw_bytes)
            or material["upgrade_intent_sha256"] != intent["upgrade_intent_sha256"]
            or material["producer_module"] != "factory-upgrade-intake"
            or material["contract_id"] != "upgrade.intent"
            or material["major"] != 2
            or material["peer_module"] != "factory-upgrade-intake"
            or material["audience"] != "dps.factory-impact-analyzer.intent"
            or material["source_peer_module"] != "factory-upgrade-intake"
            or material["source_producer_module"] != "factory-upgrade-intake"
            or material["source_contract_id"] != "upgrade.intent"
            or material["source_major"] != 2
            or material["source_upgrade_intent_sha256"]
            != intent["upgrade_intent_sha256"]
            or material["source_audience"] != "dps.factory-instruction-resolver"
        ):
            raise ImpactError("Intent trust binding mismatch")
        now = _parse_utc(self.trusted_now(), "trusted clock")
        local_issued = _parse_utc(material["issued_at"], "trust issued_at")
        source_issued = _parse_utc(material["source_issued_at"], "source issued_at")
        source_verified = _parse_utc(
            material["source_verified_at"], "source verified_at"
        )
        expiry_names = (
            "source_expires_at", "requester_auth_expires_at",
            "manifest_ownership_expires_at",
        )
        if (
            source_issued > source_verified
            or source_verified > local_issued
            or local_issued > now
            or any(_parse_utc(material[name], name) <= now for name in expiry_names)
            or _parse_utc(material["expires_at"], "trust expires_at")
            > min(_parse_utc(material[name], name) for name in expiry_names)
        ):
            raise ImpactError("source Intent authority proof is expired or inconsistent")
        approval = material["approval_expires_at"]
        if approval is not None:
            approval_dt = _parse_utc(approval, "approval_expires_at")
            if approval_dt <= now or _parse_utc(material["expires_at"], "expires_at") > approval_dt:
                raise ImpactError("source approval proof is expired or inconsistent")
        return material


class ReceiptVerifierPort:
    """Concrete local verifier for one Resolver-issued receipt capability."""

    def __init__(self, verification_key: bytes, clock: TrustedUtcClock) -> None:
        if type(clock) is not TrustedUtcClock:
            raise ImpactError("receipt verifier requires the concrete trusted clock")
        if type(verification_key) is not bytes or len(verification_key) < 32:
            raise ImpactError("receipt verifier key must be at least 256 bits")
        self.__key = bytes(verification_key)
        self.__clock = clock

    def trusted_now(self) -> str:
        return self.__clock.now()

    def create_process_bound_attestation(
        self,
        raw_bytes: bytes,
        *,
        source_capability: Mapping[str, Any],
        trust_receipt_id: str,
        trust_nonce: str,
        issued_at: str,
        expires_at: str,
    ) -> Dict[str, Any]:
        receipt = _decode_receipt(raw_bytes)
        expected_source = {
            "receipt_sha256", "receipt_id", "producer_module", "contract_id",
            "major", "issuer", "audience", "issued_at", "expires_at", "nonce",
            "generation", "status",
        }
        if type(source_capability) is not dict or set(source_capability) != expected_source:
            raise ImpactError("source Receipt capability metadata has invalid shape")
        if source_capability["receipt_sha256"] != _sha256(raw_bytes):
            raise ImpactError("source Receipt full digest mismatch")
        material = {
            "raw_sha256": _sha256(raw_bytes),
            "receipt_sha256": source_capability["receipt_sha256"],
            "receipt_id": source_capability["receipt_id"],
            "source_upgrade_intent_sha256": receipt[
                "source_upgrade_intent_sha256"
            ],
            "source_producer_module": source_capability["producer_module"],
            "source_contract_id": source_capability["contract_id"],
            "source_major": source_capability["major"],
            "source_issuer": source_capability["issuer"],
            "source_audience": source_capability["audience"],
            "source_issued_at": source_capability["issued_at"],
            "source_expires_at": source_capability["expires_at"],
            "source_nonce": source_capability["nonce"],
            "source_generation": source_capability["generation"],
            "source_status": source_capability["status"],
            "producer_module": "factory-instruction-resolver",
            "contract_id": "instruction.receipt",
            "major": 2,
            "peer_module": "factory-instruction-resolver",
            "audience": "dps.factory-impact-analyzer.receipt",
            "trust_receipt_id": trust_receipt_id,
            "trust_nonce": trust_nonce,
            "issued_at": issued_at,
            "expires_at": expires_at,
        }
        material["verification_mac"] = _attestation_mac(
            self.__key, "dps.factory-impact-analyzer.receipt-trust/v2", material
        )
        return material

    def verify(self, raw_bytes: bytes, attestation: Mapping[str, Any]) -> Dict[str, Any]:
        receipt = _decode_receipt(raw_bytes)
        material = _check_common_attestation(
            attestation=attestation,
            fields=_RECEIPT_ATTESTATION_FIELDS,
            key=self.__key,
            domain="dps.factory-impact-analyzer.receipt-trust/v2",
            clock=self.__clock,
        )
        if (
            material["raw_sha256"] != _sha256(raw_bytes)
            or material["receipt_sha256"] != _sha256(raw_bytes)
            or material["receipt_id"] != receipt["receipt_id"]
            or material["source_upgrade_intent_sha256"]
            != receipt["source_upgrade_intent_sha256"]
            or material["source_issuer"] != "factory-instruction-resolver"
            or material["source_producer_module"] != "factory-instruction-resolver"
            or material["source_contract_id"] != "instruction.receipt"
            or material["source_major"] != 2
            or material["source_audience"]
            != "dps.factory-instruction-resolver.currentness"
            or material["source_status"] != "BOUND"
            or type(material["source_generation"]) is not int
            or material["source_generation"] < 1
            or type(material["source_nonce"]) is not str
            or _NONCE.fullmatch(material["source_nonce"]) is None
            or material["producer_module"] != "factory-instruction-resolver"
            or material["contract_id"] != "instruction.receipt"
            or material["major"] != 2
            or material["peer_module"] != "factory-instruction-resolver"
            or material["audience"] != "dps.factory-impact-analyzer.receipt"
        ):
            raise ImpactError("Receipt trust binding mismatch")
        now = _parse_utc(self.trusted_now(), "trusted clock")
        local_issued = _parse_utc(material["issued_at"], "trust issued_at")
        source_issued = _parse_utc(material["source_issued_at"], "source issued_at")
        source_expires = _parse_utc(material["source_expires_at"], "source expires_at")
        expected_source_expiry = min(
            _parse_utc(receipt[field], field)
            for field in (
                "source_intake_trust_expires_at",
                "source_requester_auth_expires_at",
                "source_manifest_ownership_expires_at",
            )
        )
        if receipt["source_approval_expires_at"] is not None:
            expected_source_expiry = min(
                expected_source_expiry,
                _parse_utc(
                    receipt["source_approval_expires_at"],
                    "source_approval_expires_at",
                ),
            )
        if (
            source_issued > now
            or source_issued != _parse_utc(receipt["resolved_at"], "resolved_at")
            or source_issued > local_issued
            or source_expires <= now
            or source_expires != expected_source_expiry
            or _parse_utc(material["expires_at"], "expires_at") > source_expires
        ):
            raise ImpactError("source Receipt capability is expired or inconsistent")
        return material


class ImpactPolicyVerifierPort:
    """Concrete local verifier for a stable, signed policy document."""

    def __init__(self, verification_key: bytes, clock: TrustedUtcClock) -> None:
        if type(clock) is not TrustedUtcClock:
            raise ImpactError("policy verifier requires the concrete trusted clock")
        if type(verification_key) is not bytes or len(verification_key) < 32:
            raise ImpactError("policy verifier key must be at least 256 bits")
        self.__key = bytes(verification_key)
        self.__clock = clock

    def trusted_now(self) -> str:
        return self.__clock.now()

    def create_process_bound_attestation(
        self,
        raw_bytes: bytes,
        *,
        trust_receipt_id: str,
        trust_nonce: str,
        issued_at: str,
        expires_at: str,
    ) -> Dict[str, Any]:
        policy = _decode_policy(raw_bytes)
        material = {
            "raw_sha256": _sha256(raw_bytes),
            "policy_id": policy["policy_id"],
            "producer_module": "factory-impact-policy-store",
            "contract_id": "factory.impact.policy",
            "major": 2,
            "peer_module": "factory-impact-policy-store",
            "audience": "dps.factory-impact-analyzer.policy",
            "trust_receipt_id": trust_receipt_id,
            "trust_nonce": trust_nonce,
            "issued_at": issued_at,
            "expires_at": expires_at,
        }
        material["verification_mac"] = _attestation_mac(
            self.__key, "dps.factory-impact-analyzer.policy-trust/v2", material
        )
        return material

    def verify(self, raw_bytes: bytes, attestation: Mapping[str, Any]) -> Dict[str, Any]:
        policy = _decode_policy(raw_bytes)
        material = _check_common_attestation(
            attestation=attestation,
            fields=_POLICY_ATTESTATION_FIELDS,
            key=self.__key,
            domain="dps.factory-impact-analyzer.policy-trust/v2",
            clock=self.__clock,
        )
        if (
            material["raw_sha256"] != _sha256(raw_bytes)
            or material["policy_id"] != policy["policy_id"]
            or material["producer_module"] != "factory-impact-policy-store"
            or material["contract_id"] != "factory.impact.policy"
            or material["major"] != 2
            or material["peer_module"] != "factory-impact-policy-store"
            or material["audience"] != "dps.factory-impact-analyzer.policy"
        ):
            raise ImpactError("Impact policy trust binding mismatch")
        return material


@dataclass(frozen=True, init=False)
class VerifiedIntentV2:
    raw_bytes: bytes
    raw_sha256: str
    upgrade_intent_sha256: str
    source_metadata: Mapping[str, Any]
    trust_metadata: Mapping[str, Any]
    expires_at: str
    trust_receipt_id: str
    _issuer: Any
    _issuer_token: object

    def __init__(self, *, _issuer: Any, _issuer_token: object, values: Mapping[str, Any]) -> None:
        if _issuer is None or _issuer_token is None or type(values) is not dict:
            raise ImpactError("VerifiedIntentV2 requires the fixed authority")
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])

    def canonical_intent(self) -> Dict[str, Any]:
        return _decode_intent(self.raw_bytes)


@dataclass(frozen=True, init=False)
class VerifiedReceiptV2:
    raw_bytes: bytes
    raw_sha256: str
    receipt_sha256: str
    receipt_id: str
    source_upgrade_intent_sha256: str
    source_metadata: Mapping[str, Any]
    trust_metadata: Mapping[str, Any]
    expires_at: str
    trust_receipt_id: str
    _issuer: Any
    _issuer_token: object

    def __init__(self, *, _issuer: Any, _issuer_token: object, values: Mapping[str, Any]) -> None:
        if _issuer is None or _issuer_token is None or type(values) is not dict:
            raise ImpactError("VerifiedReceiptV2 requires the fixed authority")
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])

    def canonical_receipt(self) -> Dict[str, Any]:
        return _decode_receipt(self.raw_bytes)


@dataclass(frozen=True, init=False)
class VerifiedImpactPolicyV2:
    raw_bytes: bytes
    raw_sha256: str
    policy_id: str
    status: str
    roles: Mapping[str, Tuple[str, ...]]
    risk_required_checks: Mapping[str, Tuple[str, ...]]
    stage_required_checks: Mapping[str, Tuple[str, ...]]
    change_kind_risk_floor: Mapping[str, str]
    risk_stage_allowlist: Mapping[str, Tuple[str, ...]]
    allowed_stages: Tuple[str, ...]
    trust_metadata: Mapping[str, Any]
    expires_at: str
    trust_receipt_id: str
    _issuer: Any
    _issuer_token: object

    def __init__(self, *, _issuer: Any, _issuer_token: object, values: Mapping[str, Any]) -> None:
        if _issuer is None or _issuer_token is None or type(values) is not dict:
            raise ImpactError("VerifiedImpactPolicyV2 requires the fixed authority")
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])


class _AuthorityBase:
    capability_type: Any

    def __init__(self) -> None:
        self._token = object()
        self._issued: Dict[int, Any] = {}
        self._fingerprints: Dict[int, str] = {}
        self._by_receipt: Dict[str, Any] = {}
        self._by_nonce: Dict[str, Any] = {}

    def _register(self, capability: Any, receipt_id: str, fingerprint: str) -> Any:
        nonce = capability.trust_metadata["trust_nonce"]
        existing = self._by_receipt.get(receipt_id)
        if existing is not None:
            if self._fingerprints[id(existing)] != fingerprint:
                raise ImpactError("trust receipt replayed with different bytes or bindings")
            self.assert_issued(existing)
            return existing
        existing = self._by_nonce.get(nonce)
        if existing is not None:
            if self._fingerprints[id(existing)] != fingerprint:
                raise ImpactError("trust nonce replayed with different bytes or bindings")
            self.assert_issued(existing)
            return existing
        if len(self._issued) >= _MAX_ACTIVE_CAPABILITIES:
            raise ImpactError("trust authority capability quota exceeded")
        self._issued[id(capability)] = capability
        self._fingerprints[id(capability)] = fingerprint
        self._by_receipt[receipt_id] = capability
        self._by_nonce[nonce] = capability
        return capability

    def _prune_expired(self, trusted_now: str) -> None:
        now = _parse_utc(trusted_now, "trusted clock")
        for capability_id, capability in tuple(self._issued.items()):
            if _parse_utc(capability.expires_at, "capability expiry") <= now:
                self._issued.pop(capability_id, None)
                self._fingerprints.pop(capability_id, None)
                if self._by_receipt.get(capability.trust_receipt_id) is capability:
                    self._by_receipt.pop(capability.trust_receipt_id, None)
                nonce = capability.trust_metadata["trust_nonce"]
                if self._by_nonce.get(nonce) is capability:
                    self._by_nonce.pop(nonce, None)

    def assert_issued(self, capability: Any) -> None:
        if (
            type(capability) is not self.capability_type
            or capability._issuer is not self
            or capability._issuer_token is not self._token
            or self._issued.get(id(capability)) is not capability
            or self._fingerprints.get(id(capability)) != self._fingerprint(capability)
        ):
            raise ImpactError("capability was not issued by this fixed trust authority")


class IntentTrustAuthority(_AuthorityBase):
    capability_type = VerifiedIntentV2

    def __init__(self, verifier_port: IntentVerifierPort) -> None:
        if type(verifier_port) is not IntentVerifierPort:
            raise ImpactError("Intent authority requires the concrete verifier port")
        super().__init__()
        self.__port = verifier_port
        self.__source_receipts: Dict[str, str] = {}
        self.__source_nonces: Dict[str, str] = {}

    @staticmethod
    def _fingerprint(capability: VerifiedIntentV2) -> str:
        return _sha256(_canonical_bytes({
            "raw_sha256": _sha256(capability.raw_bytes),
            "upgrade_intent_sha256": capability.upgrade_intent_sha256,
            "source_metadata": dict(capability.source_metadata),
            "trust_metadata": dict(capability.trust_metadata),
            "expires_at": capability.expires_at,
            "trust_receipt_id": capability.trust_receipt_id,
        }))

    def verify_and_seal(
        self, raw_bytes: bytes, attestation: Mapping[str, Any]
    ) -> VerifiedIntentV2:
        verified = self.__port.verify(raw_bytes, attestation)
        self._prune_expired(self.__port.trusted_now())
        source_keys = {
            key for key in verified if key.startswith("source_")
        } | {
            "requester_auth_expires_at", "manifest_ownership_expires_at",
            "approval_expires_at",
        }
        source_fingerprint = _sha256(_canonical_bytes({
            "raw_sha256": verified["raw_sha256"],
            "source_metadata": {
                key: verified[key] for key in sorted(source_keys)
            },
        }))
        for index, key in (
            (self.__source_receipts, verified["source_trust_receipt_id"]),
            (self.__source_nonces, verified["source_trust_nonce"]),
        ):
            prior = index.get(key)
            if prior is not None and prior != source_fingerprint:
                raise ImpactError("source Intent receipt or nonce replayed with different bytes")
        values = {
            "raw_bytes": bytes(raw_bytes),
            "raw_sha256": verified["raw_sha256"],
            "upgrade_intent_sha256": verified["upgrade_intent_sha256"],
            "source_metadata": MappingProxyType(
                {key: verified[key] for key in sorted(source_keys)}
            ),
            "trust_metadata": MappingProxyType({
                key: verified[key] for key in (
                    "producer_module", "contract_id", "major", "peer_module",
                    "audience", "trust_receipt_id", "trust_nonce", "issued_at",
                    "expires_at",
                )
            }),
            "expires_at": verified["expires_at"],
            "trust_receipt_id": verified["trust_receipt_id"],
        }
        capability = VerifiedIntentV2(
            _issuer=self, _issuer_token=self._token, values=values
        )
        fingerprint = self._fingerprint(capability)
        result = self._register(capability, capability.trust_receipt_id, fingerprint)
        self.__source_receipts[
            verified["source_trust_receipt_id"]
        ] = source_fingerprint
        self.__source_nonces[verified["source_trust_nonce"]] = source_fingerprint
        self.assert_issued(result)
        return result

    def assert_issued(self, capability: Any) -> None:
        super().assert_issued(capability)
        _decode_intent(capability.raw_bytes)
        if (
            capability.raw_sha256 != _sha256(capability.raw_bytes)
            or capability.upgrade_intent_sha256
            != capability.canonical_intent()["upgrade_intent_sha256"]
            or _parse_utc(capability.expires_at, "Intent capability expiry")
            <= _parse_utc(self.__port.trusted_now(), "trusted clock")
        ):
            raise ImpactError("sealed Intent capability was mutated or expired")


class ReceiptTrustAuthority(_AuthorityBase):
    capability_type = VerifiedReceiptV2

    def __init__(self, verifier_port: ReceiptVerifierPort) -> None:
        if type(verifier_port) is not ReceiptVerifierPort:
            raise ImpactError("Receipt authority requires the concrete verifier port")
        super().__init__()
        self.__port = verifier_port
        self.__source_receipts: Dict[str, str] = {}
        self.__source_nonces: Dict[str, str] = {}

    @staticmethod
    def _fingerprint(capability: VerifiedReceiptV2) -> str:
        return _sha256(_canonical_bytes({
            "raw_sha256": _sha256(capability.raw_bytes),
            "receipt_sha256": capability.receipt_sha256,
            "receipt_id": capability.receipt_id,
            "source_upgrade_intent_sha256": capability.source_upgrade_intent_sha256,
            "source_metadata": dict(capability.source_metadata),
            "trust_metadata": dict(capability.trust_metadata),
            "expires_at": capability.expires_at,
            "trust_receipt_id": capability.trust_receipt_id,
        }))

    def verify_and_seal(
        self, raw_bytes: bytes, attestation: Mapping[str, Any]
    ) -> VerifiedReceiptV2:
        verified = self.__port.verify(raw_bytes, attestation)
        self._prune_expired(self.__port.trusted_now())
        source_keys = {key for key in verified if key.startswith("source_")}
        source_fingerprint = _sha256(_canonical_bytes({
            "raw_sha256": verified["raw_sha256"],
            "receipt_id": verified["receipt_id"],
            "source_metadata": {
                key: verified[key] for key in sorted(source_keys)
            },
        }))
        for index, key in (
            (self.__source_receipts, verified["receipt_id"]),
            (self.__source_nonces, verified["source_nonce"]),
        ):
            prior = index.get(key)
            if prior is not None and prior != source_fingerprint:
                raise ImpactError("source Receipt identity replayed with different bytes")
        values = {
            "raw_bytes": bytes(raw_bytes),
            "raw_sha256": verified["raw_sha256"],
            "receipt_sha256": verified["receipt_sha256"],
            "receipt_id": verified["receipt_id"],
            "source_upgrade_intent_sha256": verified[
                "source_upgrade_intent_sha256"
            ],
            "source_metadata": MappingProxyType(
                {key: verified[key] for key in sorted(source_keys)}
            ),
            "trust_metadata": MappingProxyType({
                key: verified[key] for key in (
                    "producer_module", "contract_id", "major", "peer_module",
                    "audience", "trust_receipt_id", "trust_nonce", "issued_at",
                    "expires_at",
                )
            }),
            "expires_at": verified["expires_at"],
            "trust_receipt_id": verified["trust_receipt_id"],
        }
        capability = VerifiedReceiptV2(
            _issuer=self, _issuer_token=self._token, values=values
        )
        result = self._register(
            capability, capability.trust_receipt_id, self._fingerprint(capability)
        )
        self.__source_receipts[verified["receipt_id"]] = source_fingerprint
        self.__source_nonces[verified["source_nonce"]] = source_fingerprint
        self.assert_issued(result)
        return result

    def assert_issued(self, capability: Any) -> None:
        super().assert_issued(capability)
        receipt = _decode_receipt(capability.raw_bytes)
        if (
            capability.raw_sha256 != _sha256(capability.raw_bytes)
            or capability.receipt_sha256 != _sha256(capability.raw_bytes)
            or capability.receipt_id != receipt["receipt_id"]
            or capability.source_upgrade_intent_sha256
            != receipt["source_upgrade_intent_sha256"]
            or _parse_utc(capability.expires_at, "Receipt capability expiry")
            <= _parse_utc(self.__port.trusted_now(), "trusted clock")
        ):
            raise ImpactError("sealed Receipt capability was mutated or expired")


class ImpactPolicyTrustAuthority(_AuthorityBase):
    capability_type = VerifiedImpactPolicyV2

    def __init__(self, verifier_port: ImpactPolicyVerifierPort) -> None:
        if type(verifier_port) is not ImpactPolicyVerifierPort:
            raise ImpactError("Policy authority requires the concrete verifier port")
        super().__init__()
        self.__port = verifier_port

    @staticmethod
    def _fingerprint(capability: VerifiedImpactPolicyV2) -> str:
        return _sha256(_canonical_bytes({
            "raw_sha256": _sha256(capability.raw_bytes),
            "policy_id": capability.policy_id,
            "status": capability.status,
            "roles": {key: list(value) for key, value in capability.roles.items()},
            "risk_required_checks": {
                key: list(value) for key, value in capability.risk_required_checks.items()
            },
            "stage_required_checks": {
                key: list(value) for key, value in capability.stage_required_checks.items()
            },
            "change_kind_risk_floor": dict(capability.change_kind_risk_floor),
            "risk_stage_allowlist": {
                key: list(value) for key, value in capability.risk_stage_allowlist.items()
            },
            "allowed_stages": list(capability.allowed_stages),
            "trust_metadata": dict(capability.trust_metadata),
            "expires_at": capability.expires_at,
            "trust_receipt_id": capability.trust_receipt_id,
        }))

    def verify_and_seal(
        self, raw_bytes: bytes, attestation: Mapping[str, Any]
    ) -> VerifiedImpactPolicyV2:
        verified = self.__port.verify(raw_bytes, attestation)
        self._prune_expired(self.__port.trusted_now())
        policy = _decode_policy(raw_bytes)
        values = {
            "raw_bytes": bytes(raw_bytes),
            "raw_sha256": verified["raw_sha256"],
            "policy_id": policy["policy_id"],
            "status": policy["status"],
            "roles": MappingProxyType({
                key: tuple(policy["roles"][key]) for key in sorted(policy["roles"])
            }),
            "risk_required_checks": MappingProxyType({
                key: tuple(policy["risk_required_checks"][key])
                for key in sorted(policy["risk_required_checks"])
            }),
            "stage_required_checks": MappingProxyType({
                key: tuple(policy["stage_required_checks"][key])
                for key in sorted(policy["stage_required_checks"])
            }),
            "change_kind_risk_floor": MappingProxyType(
                dict(policy["change_kind_risk_floor"])
            ),
            "risk_stage_allowlist": MappingProxyType({
                key: tuple(policy["risk_stage_allowlist"][key])
                for key in sorted(policy["risk_stage_allowlist"])
            }),
            "allowed_stages": tuple(policy["allowed_stages"]),
            "trust_metadata": MappingProxyType({
                key: verified[key] for key in (
                    "producer_module", "contract_id", "major", "peer_module",
                    "audience", "trust_receipt_id", "trust_nonce", "issued_at",
                    "expires_at",
                )
            }),
            "expires_at": verified["expires_at"],
            "trust_receipt_id": verified["trust_receipt_id"],
        }
        capability = VerifiedImpactPolicyV2(
            _issuer=self, _issuer_token=self._token, values=values
        )
        result = self._register(
            capability, capability.trust_receipt_id, self._fingerprint(capability)
        )
        self.assert_issued(result)
        return result

    def assert_issued(self, capability: Any) -> None:
        super().assert_issued(capability)
        _decode_policy(capability.raw_bytes)
        if (
            capability.raw_sha256 != _sha256(capability.raw_bytes)
            or _parse_utc(capability.expires_at, "Policy capability expiry")
            <= _parse_utc(self.__port.trusted_now(), "trusted clock")
        ):
            raise ImpactError("sealed Policy capability was mutated or expired")


def _decode_intent(raw: bytes) -> Dict[str, Any]:
    value = _decode_canonical(raw, "upgrade.intent/v2 wire")
    if value.get("contract_id") == "upgrade.intent/v1":
        raise ImpactError("upgrade.intent/v1 is deprecated and quarantine-only")
    _schema_validate(value, _INTENT_VALIDATOR, "upgrade.intent/v2")
    changes_material = {
        "baseline_commit": value["baseline_commit"],
        "manifest_ownership_sha256": value["manifest_ownership_sha256"],
        "public_contract_changes": value["public_contract_changes"],
    }
    if value["public_contract_changes_sha256"] != _domain_sha256(
        "dps.upgrade-intent/v2/public-contract-changes", changes_material
    ):
        raise ImpactError("public contract expectation digest mismatch")
    subject = {
        key: item for key, item in value.items()
        if key not in {"authorization", "approval_subject_sha256", "upgrade_intent_sha256"}
    }
    if value["approval_subject_sha256"] != _domain_sha256(
        "dps.upgrade-intent/v2/approval-subject", subject
    ):
        raise ImpactError("approval subject digest mismatch")
    full = {key: item for key, item in value.items() if key != "upgrade_intent_sha256"}
    if value["upgrade_intent_sha256"] != _domain_sha256(
        "dps.upgrade-intent/v2/full-intent", full
    ):
        raise ImpactError("full upgrade intent digest mismatch")
    if value["target_modules"] != sorted(value["target_modules"]):
        raise ImpactError("target_modules must be canonical and sorted")
    if value["requested_paths"] != sorted(value["requested_paths"]):
        raise ImpactError("requested_paths must be canonical and sorted")
    changes = value["public_contract_changes"]
    canonical_changes = sorted(changes, key=_contract_change_sort_key)
    if changes != canonical_changes:
        raise ImpactError("contract expectations must be canonical and sorted")
    return value


def _decode_receipt(raw: bytes) -> Dict[str, Any]:
    value = _decode_canonical(raw, "instruction.receipt/v2 wire")
    if value.get("contract_id") == "instruction.receipt/v1":
        raise ImpactError("instruction.receipt/v1 is deprecated and quarantine-only")
    _schema_validate(value, _RECEIPT_VALIDATOR, "instruction.receipt/v2")
    if value["status"] != "BOUND" or value["invalidated_reason"] is not None:
        raise ImpactError("only a BOUND v2 receipt is routable")
    resolved = _parse_utc(value["resolved_at"], "resolved_at")
    occurred = _parse_utc(value["occurred_at"], "occurred_at")
    verified_at = _parse_utc(
        value["source_intake_verified_at"], "source_intake_verified_at"
    )
    source_issued = _parse_utc(
        value["source_intake_trust_issued_at"],
        "source_intake_trust_issued_at",
    )
    authority_expiries = [
        _parse_utc(value[field], field)
        for field in (
            "source_intake_trust_expires_at",
            "source_requester_auth_expires_at",
            "source_manifest_ownership_expires_at",
        )
    ]
    if value["source_approval_expires_at"] is not None:
        authority_expiries.append(
            _parse_utc(
                value["source_approval_expires_at"],
                "source_approval_expires_at",
            )
        )
    if (
        occurred != resolved
        or verified_at != resolved
        or source_issued > resolved
        or any(expires <= resolved for expires in authority_expiries)
    ):
        raise ImpactError("Receipt time and source authority bindings are inconsistent")
    material = dict(value)
    receipt_id = material.pop("receipt_id")
    expected = "instruction:" + _sha256(_canonical_bytes(material))[:32]
    if receipt_id != expected:
        raise ImpactError("receipt_id does not bind the full canonical receipt")
    facts_material = {
        "baseline_commit": value["baseline_commit"],
        "verified_baseline_contract_facts": value[
            "verified_baseline_contract_facts"
        ],
    }
    if value["verified_baseline_contract_facts_sha256"] != _sha256(
        _canonical_bytes(facts_material)
    ):
        raise ImpactError("verified baseline facts digest mismatch")
    return value


def _decode_policy(raw: bytes) -> Dict[str, Any]:
    if type(raw) is not bytes or not raw or len(raw) > _MAX_WIRE_BYTES:
        raise ImpactError("factory impact policy/v2 is empty, oversized, or non-bytes")
    try:
        value = json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_float=_reject_float,
            parse_constant=_reject_constant,
        )
    except ImpactError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, TypeError) as exc:
        raise ImpactError("factory impact policy/v2 is invalid JSON") from exc
    if type(value) is not dict:
        raise ImpactError("factory impact policy/v2 must contain one JSON object")
    expected_fields = {
        "schema_version", "policy_id", "status", "allowed_stages", "roles",
        "check_catalog", "risk_required_checks", "stage_required_checks",
        "change_kind_risk_floor", "risk_stage_allowlist",
    }
    if set(value) != expected_fields or value.get("schema_version") != "dps.factory-impact-policy/v2":
        raise ImpactError("Impact policy must use the exact v2 shape")
    if value.get("status") != "non-production-template":
        raise ImpactError("repository Impact policy cannot authorize production")
    if (
        type(value.get("policy_id")) is not str
        or _REQUEST_ID.fullmatch(value["policy_id"]) is None
    ):
        raise ImpactError("Impact policy id cannot satisfy plan v2")
    if (
        type(value.get("allowed_stages")) is not list
        or not value["allowed_stages"]
        or value["allowed_stages"] != sorted(value["allowed_stages"])
        or len(set(value["allowed_stages"])) != len(value["allowed_stages"])
        or any(stage not in {"development", "shadow"} for stage in value["allowed_stages"])
    ):
        raise ImpactError("non-production policy stage boundary is invalid")
    roles = value.get("roles")
    if type(roles) is not dict or set(roles) != _ROLES:
        raise ImpactError("Impact policy role set is invalid")
    identities: Set[str] = set()
    for role in sorted(_ROLES):
        assigned = roles[role]
        if (
            type(assigned) is not list or not assigned or assigned != sorted(assigned)
            or len(set(assigned)) != len(assigned)
            or any(type(item) is not str or not item for item in assigned)
            or identities.intersection(assigned)
        ):
            raise ImpactError("Impact policy role identities are invalid or overlap")
        identities.update(assigned)
    catalog = value.get("check_catalog")
    risk_checks = value.get("risk_required_checks")
    stage_checks = value.get("stage_required_checks")
    floors = value.get("change_kind_risk_floor")
    allowlist = value.get("risk_stage_allowlist")
    if (
        type(catalog) is not list or not catalog or len(set(catalog)) != len(catalog)
        or any(type(item) is not str or not item for item in catalog)
        or type(risk_checks) is not dict or set(risk_checks) != set(_RISK)
        or type(stage_checks) is not dict or set(stage_checks) != _STAGES
        or type(floors) is not dict or set(floors) != _CHANGE_KINDS
        or any(value not in _RISK for value in floors.values())
        or type(allowlist) is not dict or set(allowlist) != set(_RISK)
    ):
        raise ImpactError("Impact policy checks, floors, or stage matrix are invalid")
    for collection in tuple(risk_checks.values()) + tuple(stage_checks.values()):
        if (
            type(collection) is not list or collection != sorted(collection)
            or len(set(collection)) != len(collection)
            or any(item not in catalog for item in collection)
        ):
            raise ImpactError("Impact policy check selection is invalid")
    for risk, stages in allowlist.items():
        if (
            type(stages) is not list or not stages or stages != sorted(stages)
            or len(set(stages)) != len(stages) or any(stage not in _STAGES for stage in stages)
        ):
            raise ImpactError("Impact policy risk/stage allowlist is invalid")
    return value


@dataclass(frozen=True)
class ContractDeclaration:
    contract_id: str
    major: int
    mode: str
    source: str
    status: str
    owner_module: str
    declaring_module: str
    declaration_kind: str

    @property
    def key(self) -> Tuple[str, int]:
        return (self.contract_id, self.major)

    def value(self) -> Dict[str, Any]:
        return {
            "contract_id": self.contract_id, "major": self.major,
            "mode": self.mode, "source": self.source, "status": self.status,
            "owner_module": self.owner_module,
            "declaring_module": self.declaring_module,
            "declaration_kind": self.declaration_kind,
        }


@dataclass(frozen=True)
class ModuleRecord:
    module_id: str
    manifest_path: Path
    manifest: Mapping[str, Any]
    owned: Tuple[str, ...]
    dependencies: Tuple[str, ...]
    risk_tier: str
    provided: Mapping[Tuple[str, int], ContractDeclaration]
    consumed: Mapping[Tuple[str, int], ContractDeclaration]


def _parse_declarations(
    root: Path, manifest: Mapping[str, Any], module_id: str, kind: str
) -> Dict[Tuple[str, int], ContractDeclaration]:
    contracts = manifest.get("contracts")
    items = contracts.get(kind) if type(contracts) is dict else None
    if type(items) is not list:
        raise ImpactError("Manifest contracts.%s must be a list" % kind)
    expected = {"contractId", "major", "source", "status", "mode", "ownerModule"}
    result: Dict[Tuple[str, int], ContractDeclaration] = {}
    for item in items:
        if type(item) is not dict or set(item) != expected:
            raise ImpactError("contract declaration must use the frozen exact shape")
        contract_id = item["contractId"]
        major = item["major"]
        mode = item["mode"]
        source = item["source"]
        status = item["status"]
        owner = item["ownerModule"]
        if (
            type(contract_id) is not str or _CONTRACT_ID.fullmatch(contract_id) is None
            or type(major) is not int or major < 1
            or type(source) is not str or type(owner) is not str
            or _MODULE_ID.fullmatch(owner) is None
            or mode not in _CONTRACT_MODES or status not in _CONTRACT_STATUSES
        ):
            raise ImpactError("contract declaration identity is invalid")
        _safe_file(root, source)
        if kind == "provided" and (mode not in _PROVIDER_MODES or owner != module_id):
            raise ImpactError("provider declaration owner or mode is invalid")
        if (status == "retired") != (mode == "retired"):
            raise ImpactError("retired contract status and mode must agree")
        key = (contract_id, major)
        if key in result:
            raise ImpactError("duplicate exact contract declaration")
        result[key] = ContractDeclaration(
            contract_id, major, mode, source, status, owner, module_id, kind
        )
    return result


def _load_records(root: Path) -> Dict[str, ModuleRecord]:
    modules_root = root / "Modules"
    if not modules_root.is_dir() or modules_root.is_symlink():
        raise ImpactError("Modules root is missing or symlinked")
    records: Dict[str, ModuleRecord] = {}
    for module_root in sorted(modules_root.iterdir(), key=lambda item: item.name):
        manifest_path = module_root / "module.yaml"
        agents_path = module_root / "AGENTS.md"
        if not manifest_path.is_file() and not agents_path.is_file():
            continue
        if (
            module_root.is_symlink() or manifest_path.is_symlink()
            or agents_path.is_symlink() or not manifest_path.is_file()
            or not agents_path.is_file()
        ):
            raise ImpactError("registered module requires safe AGENTS.md and module.yaml")
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        except Exception as exc:
            raise ImpactError("module Manifest is invalid JSON") from exc
        module = manifest.get("module") if type(manifest) is dict else None
        module_id = module.get("id") if type(module) is dict else None
        risk = module.get("riskTier") if type(module) is dict else None
        paths = manifest.get("paths") if type(manifest) is dict else None
        owned = paths.get("owned") if type(paths) is dict else None
        if (
            type(module_id) is not str or _MODULE_ID.fullmatch(module_id) is None
            or module_id != module_root.name or module_id in records or risk not in _RISK
            or type(owned) is not list or not owned
            or any(type(item) is not str for item in owned)
        ):
            raise ImpactError("module identity, risk, or ownership is invalid")
        dependencies_raw = manifest.get("dependencies")
        if type(dependencies_raw) is not list:
            raise ImpactError("Manifest dependencies must be a list")
        dependencies = []
        for item in dependencies_raw:
            dependency = item.get("moduleId") if type(item) is dict else None
            if type(dependency) is not str or _MODULE_ID.fullmatch(dependency) is None:
                raise ImpactError("Manifest dependency is invalid")
            dependencies.append(dependency)
        records[module_id] = ModuleRecord(
            module_id=module_id,
            manifest_path=manifest_path,
            manifest=manifest,
            owned=tuple(owned),
            dependencies=tuple(sorted(set(dependencies))),
            risk_tier=risk,
            provided=MappingProxyType(
                _parse_declarations(root, manifest, module_id, "provided")
            ),
            consumed=MappingProxyType(
                _parse_declarations(root, manifest, module_id, "consumed")
            ),
        )
    if not records:
        raise ImpactError("no registered modules")
    exact_owners: Dict[Tuple[str, int], ContractDeclaration] = {}
    family_owners: Dict[str, str] = {}
    for record in records.values():
        if set(record.dependencies).difference(records):
            raise ImpactError("Manifest has an unknown dependency")
        for key, declaration in record.provided.items():
            if key in exact_owners:
                raise ImpactError("multiple exact owners for %s/v%d" % key)
            prior = family_owners.get(key[0])
            if prior is not None and prior != record.module_id:
                raise ImpactError("contract family has multiple owners: " + key[0])
            family_owners[key[0]] = record.module_id
            exact_owners[key] = declaration
    for record in records.values():
        for key, declaration in record.consumed.items():
            owner = exact_owners.get(key)
            if (
                owner is None or declaration.owner_module != owner.declaring_module
                or declaration.source != owner.source
            ):
                raise ImpactError("consumer lacks matching exact owner/source")
    _validate_runtime_edges(records, exact_owners)
    return records


def _validate_runtime_edges(
    records: Mapping[str, ModuleRecord],
    owners: Mapping[Tuple[str, int], ContractDeclaration],
) -> None:
    index: Dict[Tuple[str, str, str, int, str], Mapping[str, Any]] = {}
    for module_id, record in records.items():
        communication = record.manifest.get("communication")
        if type(communication) is not dict or set(communication) != {"inbound", "outbound"}:
            raise ImpactError("Manifest communication must use the frozen shape")
        for direction in ("inbound", "outbound"):
            edges = communication[direction]
            if type(edges) is not list or any(type(edge) is not dict for edge in edges):
                raise ImpactError("Manifest communication entries are invalid")
            for edge in edges:
                if set(edge) not in {
                    frozenset(_COMMUNICATION_FIELDS),
                    frozenset(_COMMUNICATION_FIELDS | {"preserveProducer"}),
                }:
                    raise ImpactError("runtime communication edge shape is invalid")
                peer = edge.get("peerModule")
                contract_id = edge.get("contractId")
                major = edge.get("major")
                key = (contract_id, major)
                if (
                    type(peer) is not str or peer not in records
                    or type(contract_id) is not str or type(major) is not int
                    or edge.get("direction") != direction
                    or edge.get("transport") not in _COMMUNICATION_TRANSPORTS
                    or type(edge.get("timeoutMs")) is not int
                    or not 1 <= edge["timeoutMs"] <= 300000
                    or any(
                        type(edge.get(field)) is not str or not edge[field]
                        for field in (
                            "retryPolicy", "idempotencyKey", "authScope", "failureMode"
                        )
                    )
                    or (
                        "preserveProducer" in edge
                        and type(edge["preserveProducer"]) is not bool
                    )
                    or (
                        edge.get("preserveProducer") is True and direction != "outbound"
                    )
                ):
                    raise ImpactError("runtime communication identity is invalid")
                declaration = record.provided.get(key) or record.consumed.get(key)
                owner = owners.get(key)
                if (
                    declaration is None or owner is None or owner.mode != "active"
                    or declaration.mode not in _ROUTABLE_MODES
                ):
                    raise ImpactError("quarantine/retired contract cannot be routed")
                edge_key = (module_id, peer, contract_id, major, direction)
                if edge_key in index:
                    raise ImpactError("duplicate exact-major communication edge")
                index[edge_key] = edge
    for (module_id, peer, contract_id, major, direction), edge in index.items():
        reciprocal_direction = "inbound" if direction == "outbound" else "outbound"
        reciprocal = index.get(
            (peer, module_id, contract_id, major, reciprocal_direction)
        )
        if reciprocal is None:
            raise ImpactError("runtime communication lacks reciprocal exact-major edge")
        if (
            reciprocal.get("transport") != edge.get("transport")
            or reciprocal.get("timeoutMs") != edge.get("timeoutMs")
        ):
            raise ImpactError("reciprocal communication semantics mismatch")


def _indexes(records: Mapping[str, ModuleRecord]) -> Tuple[
    Dict[Tuple[str, int], ContractDeclaration], Dict[Tuple[str, int], Set[str]]
]:
    owners: Dict[Tuple[str, int], ContractDeclaration] = {}
    consumers: Dict[Tuple[str, int], Set[str]] = {}
    for module_id, record in records.items():
        owners.update(record.provided)
        for key in record.consumed:
            # Quarantined/retired declarations remain non-runnable, but they are
            # still exact-major declarations whose owners may need review.
            consumers.setdefault(key, set()).add(module_id)
    return owners, consumers


def _manifest_snapshot(value: Mapping[str, Any], kind: str) -> Dict[
    Tuple[str, int], Dict[str, Any]
]:
    contracts = value.get("contracts")
    items = contracts.get(kind) if type(contracts) is dict else None
    if type(items) is not list:
        raise ImpactError("baseline Manifest contract list is invalid")
    expected = {"contractId", "major", "source", "status", "mode", "ownerModule"}
    result: Dict[Tuple[str, int], Dict[str, Any]] = {}
    for item in items:
        if type(item) is not dict or set(item) != expected:
            raise ImpactError("baseline contract declaration shape is invalid")
        key = (item["contractId"], item["major"])
        if (
            type(key[0]) is not str or _CONTRACT_ID.fullmatch(key[0]) is None
            or type(key[1]) is not int or key[1] < 1 or key in result
        ):
            raise ImpactError("baseline contract identity is invalid")
        result[key] = dict(item)
    return result


def _baseline_indexes(
    root: Path, baseline: str, records: Mapping[str, ModuleRecord]
) -> Tuple[
    Dict[Tuple[str, int], Tuple[str, Mapping[str, Any]]],
    Dict[Tuple[str, int], Set[str]],
    Dict[str, str],
    Dict[str, Dict[Tuple[str, int], Mapping[str, Any]]],
]:
    owners: Dict[Tuple[str, int], Tuple[str, Mapping[str, Any]]] = {}
    consumers: Dict[Tuple[str, int], Set[str]] = {}
    family: Dict[str, str] = {}
    provided_by_module: Dict[str, Dict[Tuple[str, int], Mapping[str, Any]]] = {}
    consumed_by_module: Dict[str, Dict[Tuple[str, int], Mapping[str, Any]]] = {}
    for module_id, record in records.items():
        path = record.manifest_path.relative_to(root).as_posix()
        raw = _git_bytes(root, ["show", baseline + ":" + path], required=False)
        if not raw:
            provided_by_module[module_id] = {}
            consumed_by_module[module_id] = {}
            continue
        try:
            manifest = json.loads(raw.decode("utf-8-sig"))
        except Exception as exc:
            raise ImpactError("baseline Manifest is invalid JSON") from exc
        provided = _manifest_snapshot(manifest, "provided")
        consumed = _manifest_snapshot(manifest, "consumed")
        provided_by_module[module_id] = provided
        consumed_by_module[module_id] = consumed
        for key, item in provided.items():
            if key in owners or item.get("ownerModule") != module_id:
                raise ImpactError("baseline exact owner is invalid or duplicated")
            prior = family.get(key[0])
            if prior is not None and prior != module_id:
                raise ImpactError("baseline contract family owner changed")
            family[key[0]] = module_id
            owners[key] = (module_id, item)
    for module_id, declarations in consumed_by_module.items():
        for key, item in declarations.items():
            owner = owners.get(key)
            if (
                owner is None or item.get("ownerModule") != owner[0]
                or item.get("source") != owner[1].get("source")
            ):
                raise ImpactError("baseline consumer lacks matching exact owner")
            # Baseline consumers are impact readers even when their old mode is
            # quarantine-only or retired.  Runtime routing is checked separately.
            consumers.setdefault(key, set()).add(module_id)
    return owners, consumers, family, provided_by_module


def _owners_for_path(path: str, records: Mapping[str, ModuleRecord]) -> Set[str]:
    return {
        module_id for module_id, record in records.items()
        if any(fnmatch.fnmatchcase(path, pattern) for pattern in record.owned)
    }


def _is_global(path: str) -> bool:
    return path in _GLOBAL_ENGINEERING_EXACT or path.startswith(
        _GLOBAL_ENGINEERING_PREFIXES
    )


def _is_legacy_tombstone(root: Path, baseline: str, path: str) -> bool:
    if not (
        path in _LEGACY_TOMBSTONE_EXACT
        or path.startswith(_LEGACY_TOMBSTONE_PREFIXES)
    ):
        return False
    candidate = root / path
    return not candidate.exists() and bool(
        _git(root, ["rev-parse", baseline + ":" + path], required=False)
    )


def _changed_paths(root: Path, baseline: str) -> Tuple[str, ...]:
    tracked = _git(
        root,
        ["-c", "core.quotepath=false", "diff", "--name-only", "-z",
         "--diff-filter=ACDMRTUXB", baseline, "--"],
    )
    untracked = _git(
        root,
        ["-c", "core.quotepath=false", "ls-files", "-z", "--others",
         "--exclude-standard"],
    )
    values = {item for text in (tracked, untracked) for item in text.split("\0") if item}
    result = []
    for path in values:
        if _is_legacy_tombstone(root, baseline, path):
            result.append(path)
        else:
            result.append(_safe_relative(path, reject_hidden=False))
    return tuple(sorted(result))


def _diff_material(root: Path, paths: Sequence[str]) -> Dict[str, Any]:
    entries = []
    for path in paths:
        candidate = root / path
        if candidate.is_symlink():
            raise ImpactError("changed symlink is forbidden: " + path)
        entries.append({
            "path": path,
            "sha256": _sha256(candidate.read_bytes()) if candidate.is_file() else None,
            "worktree_kind": "file" if candidate.is_file() else "missing",
            "worktree_executable": bool(candidate.stat().st_mode & 0o111)
            if candidate.is_file() else None,
        })
    return {
        "paths": entries,
        "index_entries_sha256": _sha256(_git_bytes(root, ["ls-files", "--stage", "-z"])),
        "git_status_sha256": _sha256(_git_bytes(
            root, ["status", "--porcelain=v2", "-z", "--untracked-files=all"]
        )),
    }


def _verify_bound_files(root: Path, receipt: Mapping[str, Any]) -> None:
    for collection_name in (
        "instructions", "manifests", "contracts", "governance", "tests", "operations"
    ):
        for bound in receipt[collection_name]:
            path = bound["path"]
            data = _safe_file(root, path).read_bytes()
            if (
                bound["sha256"] != _sha256(data)
                or bound["git_blob"] != _git_hash_bytes(root, data)
            ):
                raise ImpactError("bound instruction file changed: " + path)


def _state_snapshot(
    root: Path, baseline: str, receipt: Mapping[str, Any]
) -> Tuple[Tuple[str, ...], Dict[str, Any], str]:
    changed = _changed_paths(root, baseline)
    material = _diff_material(root, changed)
    if receipt["diff_fingerprint"] != _sha256(_canonical_bytes(material)):
        raise ImpactError("instruction receipt diff fingerprint is stale")
    _verify_bound_files(root, receipt)
    snapshot = {
        "head": _git(root, ["rev-parse", "HEAD"]),
        "diff_material": material,
        "bound": {
            name: [
                {"path": item["path"], "sha256": item["sha256"], "git_blob": item["git_blob"]}
                for item in receipt[name]
            ]
            for name in (
                "instructions", "manifests", "contracts", "governance", "tests", "operations"
            )
        },
    }
    return changed, material, _sha256(_canonical_bytes(snapshot))


def _topological_waves(
    nodes: Set[str], edges: Sequence[Tuple[str, str]]
) -> list[list[str]]:
    dependencies = {node: set() for node in nodes}
    for consumer, provider in edges:
        if consumer == provider:
            raise ImpactError("dependency graph has a self cycle")
        dependencies[consumer].add(provider)
    remaining = set(nodes)
    waves = []
    while remaining:
        wave = sorted(
            node for node in remaining
            if not dependencies[node].intersection(remaining)
        )
        if not wave:
            raise ImpactError("dependency cycle prevents a parallel plan")
        waves.append(wave)
        remaining.difference_update(wave)
    return waves


def _cross_bind(intent_cap: VerifiedIntentV2, receipt_cap: VerifiedReceiptV2) -> Tuple[
    Dict[str, Any], Dict[str, Any]
]:
    intent = intent_cap.canonical_intent()
    receipt = receipt_cap.canonical_receipt()
    pairs = {
        "soul_id": "soul_id", "device_binding_id": "device_binding_id",
        "platform_account_id": "platform_account_id", "trace_id": "trace_id",
        "idempotency_key": "idempotency_key", "intent_id": "intent_id",
        "auth_context_id": "auth_context_id", "baseline_commit": "baseline_commit",
        "requester_auth_context_sha256": "source_requester_auth_context_sha256",
        "requester_auth_receipt_id": "source_requester_auth_receipt_id",
        "requester_auth_nonce": "source_requester_auth_nonce",
        "manifest_ownership_sha256": "source_manifest_ownership_sha256",
        "manifest_ownership_receipt_id": "source_manifest_ownership_receipt_id",
        "target_modules": "requested_target_modules",
        "requested_paths": "authorized_write_paths",
        "public_contract_changes": "bound_contract_change_expectations",
        "public_contract_changes_sha256": "source_contract_change_claims_sha256",
        "contract_change_claims_status": "source_contract_change_claims_status",
        "baseline_verification_required": "baseline_verification_required",
        "approval_subject_sha256": "source_approval_subject_sha256",
        "upgrade_intent_sha256": "source_upgrade_intent_sha256",
        "requested_risk_tier": "requested_risk_tier",
        "requested_stage": "requested_stage",
    }
    for left, right in pairs.items():
        if intent[left] != receipt[right]:
            raise ImpactError("Intent/Receipt cross-binding mismatch: " + left)
    if intent["authorization"]["status"] != receipt["source_authorization_status"]:
        raise ImpactError("Intent/Receipt authorization status mismatch")
    if receipt["source_intent_contract"] != {
        "contract_id": "upgrade.intent", "major": 2, "mode": "active"
    }:
        raise ImpactError("Receipt source Intent contract identity is invalid")
    source = dict(intent_cap.source_metadata)
    capability_pairs = {
        "source_intake_peer_module": "source_peer_module",
        "source_intake_audience": "source_audience",
        "source_intake_trust_receipt_id": "source_trust_receipt_id",
        "source_intake_trust_nonce": "source_trust_nonce",
        "source_intake_trust_issued_at": "source_issued_at",
        "source_intake_verified_at": "source_verified_at",
        "source_intake_trust_expires_at": "source_expires_at",
        "source_requester_auth_expires_at": "requester_auth_expires_at",
        "source_manifest_ownership_expires_at": "manifest_ownership_expires_at",
        "source_approval_expires_at": "approval_expires_at",
    }
    if receipt["source_intake_payload_sha256"] != intent_cap.raw_sha256:
        raise ImpactError("Receipt source Intake payload digest mismatch")
    for receipt_name, capability_name in capability_pairs.items():
        if receipt[receipt_name] != source[capability_name]:
            raise ImpactError("Receipt source capability mismatch: " + receipt_name)
    if (
        receipt_cap.source_upgrade_intent_sha256 != intent_cap.upgrade_intent_sha256
        or receipt_cap.source_metadata["source_upgrade_intent_sha256"]
        != intent_cap.upgrade_intent_sha256
    ):
        raise ImpactError("Receipt capability is bound to another Intent")
    expectations = receipt["bound_contract_change_expectations"]
    facts = receipt["verified_baseline_contract_facts"]
    if len(expectations) != len(facts):
        raise ImpactError("each expectation requires one separate baseline fact")
    facts_by_key = {(item["contract_id"], item["major"]): item for item in facts}
    if len(facts_by_key) != len(facts):
        raise ImpactError("baseline facts have duplicate exact identities")
    for expectation in expectations:
        key = (expectation["contract_id"], expectation["major"])
        fact = facts_by_key.get(key)
        if (
            fact is None or fact["baseline_commit"] != intent["baseline_commit"]
            or expectation["baseline_commit"] != intent["baseline_commit"]
            or expectation["expected_baseline_state"] != fact["presence"]
        ):
            raise ImpactError("expectation and verified baseline fact are conflated")
    return intent, receipt


class ImpactAnalyzer:
    """Read-only v2 analyzer with fixed composition-root authorities."""

    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        *,
        intent_authority: IntentTrustAuthority,
        receipt_authority: ReceiptTrustAuthority,
        policy_authority: ImpactPolicyTrustAuthority,
    ) -> None:
        if (
            type(intent_authority) is not IntentTrustAuthority
            or type(receipt_authority) is not ReceiptTrustAuthority
            or type(policy_authority) is not ImpactPolicyTrustAuthority
        ):
            raise ImpactError("ImpactAnalyzer requires its three fixed trust authorities")
        self.root = Path(repository_root).resolve(strict=True)
        if not (self.root / ".git").exists():
            raise ImpactError("ImpactAnalyzer requires a Git worktree")
        self.__intent_authority = intent_authority
        self.__receipt_authority = receipt_authority
        self.__policy_authority = policy_authority

    def analyze(
        self,
        verified_intent: VerifiedIntentV2,
        verified_receipt: VerifiedReceiptV2,
        verified_policy: VerifiedImpactPolicyV2,
    ) -> Dict[str, Any]:
        if (
            type(verified_intent) is not VerifiedIntentV2
            or type(verified_receipt) is not VerifiedReceiptV2
            or type(verified_policy) is not VerifiedImpactPolicyV2
        ):
            raise ImpactError("analyze requires three exact process-bound v2 capabilities")
        self.__intent_authority.assert_issued(verified_intent)
        self.__receipt_authority.assert_issued(verified_receipt)
        self.__policy_authority.assert_issued(verified_policy)
        intent, receipt = _cross_bind(verified_intent, verified_receipt)
        baseline = intent["baseline_commit"]
        resolved = _git(self.root, ["rev-parse", "--verify", baseline + "^{commit}"])
        if resolved != baseline:
            raise ImpactError("Intent baseline is not the exact Git commit")
        if _git(self.root, ["rev-parse", "HEAD"]) != baseline:
            raise ImpactError("repository HEAD moved beyond the resolved baseline")

        changed_before, _material_before, state_before = _state_snapshot(
            self.root, baseline, receipt
        )
        records = _load_records(self.root)
        _topological_waves(
            set(records),
            sorted(
                (consumer, provider)
                for consumer, record in records.items()
                for provider in record.dependencies
            ),
        )
        owners, exact_consumers = _indexes(records)
        baseline_owners, baseline_consumers, baseline_family, baseline_provided = (
            _baseline_indexes(self.root, baseline, records)
        )

        targets = set(intent["target_modules"])
        write_owners: Set[str] = set()
        for path in intent["requested_paths"]:
            _safe_relative(path, reject_hidden=True)
            path_owners = _owners_for_path(path, records)
            if len(path_owners) != 1:
                raise ImpactError("authorized write path must have exactly one owner")
            write_owners.update(path_owners)
        if write_owners != targets:
            raise ImpactError("write modules must exactly equal requested target modules")
        expectations = {
            (item["contract_id"], item["major"]): item
            for item in intent["public_contract_changes"]
        }
        if len(expectations) != len(intent["public_contract_changes"]):
            raise ImpactError("duplicate exact-major contract expectation")
        for key, expectation in expectations.items():
            if expectation["expected_owner_module"] not in targets:
                raise ImpactError("expected contract owner is outside write modules")
            if expectation["expected_source"] not in intent["requested_paths"]:
                raise ImpactError("expected contract source is not an exact authorized path")
            if _owners_for_path(expectation["expected_source"], records) != {
                expectation["expected_owner_module"]
            }:
                raise ImpactError("expected contract source has the wrong exact owner")

        impacted = set(targets)
        if any(_is_global(path) for path in changed_before):
            impacted.update(records)
        for path in changed_before:
            if _is_legacy_tombstone(self.root, baseline, path) or _is_global(path):
                continue
            path_owners = _owners_for_path(path, records)
            if len(path_owners) != 1:
                raise ImpactError("changed path must have exactly one current owner")
            impacted.update(path_owners)

        changed_contracts: Set[Tuple[str, int]] = set()
        for key, declaration in owners.items():
            current_value = {
                "contractId": declaration.contract_id, "major": declaration.major,
                "source": declaration.source, "status": declaration.status,
                "mode": declaration.mode, "ownerModule": declaration.owner_module,
            }
            baseline_value = baseline_provided.get(
                declaration.declaring_module, {}
            ).get(key)
            manifest_path = records[
                declaration.declaring_module
            ].manifest_path.relative_to(self.root).as_posix()
            if declaration.source in changed_before or (
                manifest_path in changed_before and baseline_value != current_value
            ):
                changed_contracts.add(key)
        for module_id, baseline_declarations in baseline_provided.items():
            removed = set(baseline_declarations).difference(records[module_id].provided)
            if removed:
                raise ImpactError("provided majors must be declared retired, not removed")
        undeclared = changed_contracts.difference(expectations)
        if undeclared:
            raise ImpactError("changed public contract lacks an exact-major expectation")

        fact_by_key = {
            (item["contract_id"], item["major"]): item
            for item in receipt["verified_baseline_contract_facts"]
        }
        for key, expectation in expectations.items():
            impacted.add(expectation["expected_owner_module"])
            current_owner = owners.get(key)
            if current_owner is not None:
                impacted.add(current_owner.declaring_module)
            impacted.update(exact_consumers.get(key, set()))
            impacted.update(baseline_consumers.get(key, set()))
            fact = fact_by_key[key]
            baseline_owner = baseline_owners.get(key)
            baseline_item = baseline_owner[1] if baseline_owner is not None else None
            baseline_source = baseline_item.get("source") if baseline_item else None
            baseline_sha = (
                _sha256(_git_bytes(self.root, ["show", baseline + ":" + baseline_source]))
                if baseline_source else None
            )
            expected_fact = {
                "contract_id": key[0], "major": key[1], "baseline_commit": baseline,
                "presence": "present" if baseline_item else "absent",
                "owner_module": baseline_owner[0] if baseline_owner else None,
                "source": baseline_source, "source_sha256": baseline_sha,
                "mode": baseline_item.get("mode") if baseline_item else None,
                "status": baseline_item.get("status") if baseline_item else None,
                "family_owner_module": baseline_family.get(key[0]),
                "consumer_modules": sorted(baseline_consumers.get(key, set())),
            }
            if fact != expected_fact:
                raise ImpactError("receipt baseline fact is not current Git baseline truth")

        if sorted(impacted) != receipt["scope"]:
            raise ImpactError("independently recomputed impact scope must exactly equal receipt.scope")
        receipt_declarations = sorted(
            receipt["contract_declarations"],
            key=lambda item: (
                item["contract_id"], item["major"], item["declaring_module"],
                item["declaration_kind"],
            ),
        )
        current_declarations = sorted(
            (
                declaration.value() for module_id in sorted(impacted)
                for declaration in (
                    *records[module_id].provided.values(),
                    *records[module_id].consumed.values(),
                )
            ),
            key=lambda item: (
                item["contract_id"], item["major"], item["declaring_module"],
                item["declaration_kind"],
            ),
        )
        if receipt_declarations != current_declarations:
            raise ImpactError("receipt exact contract declaration index is stale")

        requested_risk = intent["requested_risk_tier"]
        effective_index = _RISK[requested_risk]
        for module_id in impacted:
            effective_index = max(effective_index, _RISK[records[module_id].risk_tier])
        for expectation in expectations.values():
            floor = verified_policy.change_kind_risk_floor[expectation["change_kind"]]
            effective_index = max(effective_index, _RISK[floor])
        effective_risk = "R%d" % effective_index
        stage = intent["requested_stage"]
        if stage not in verified_policy.risk_stage_allowlist[effective_risk]:
            raise ImpactError("stable policy forbids the effective risk/stage combination")
        if stage not in verified_policy.allowed_stages:
            raise ImpactError("non-production policy cannot authorize this stage")
        checks = sorted(set(
            verified_policy.risk_required_checks[effective_risk]
        ).union(verified_policy.stage_required_checks[stage]))
        edges = sorted({
            (consumer, provider) for consumer in impacted
            for provider in records[consumer].dependencies if provider in impacted
        })
        waves = _topological_waves(set(impacted), edges)

        changed_after, _material_after, state_after = _state_snapshot(
            self.root, baseline, receipt
        )
        if changed_before != changed_after or state_before != state_after:
            raise ImpactError("repository truth changed during impact analysis")

        instruction_scope = sorted(impacted)
        authorized_paths = list(intent["requested_paths"])
        expectations_list = copy.deepcopy(intent["public_contract_changes"])
        roles = {
            key: list(verified_policy.roles[key]) for key in sorted(_ROLES)
        }
        body: Dict[str, Any] = {
            "schema_version": "dps.module-change-plan/v2",
            "contract_id": "module.change.plan/v2",
            "producer_module": "factory-impact-analyzer",
            "soul_id": intent["soul_id"],
            "device_binding_id": intent["device_binding_id"],
            "platform_account_id": intent["platform_account_id"],
            "trace_id": intent["trace_id"],
            "idempotency_key": intent["idempotency_key"],
            "occurred_at": receipt["resolved_at"],
            "privacy_class": "internal",
            "intent_id": intent["intent_id"],
            "instruction_receipt_id": receipt["receipt_id"],
            "instruction_receipt_sha256": verified_receipt.receipt_sha256,
            "source_upgrade_intent_sha256": intent["upgrade_intent_sha256"],
            "source_intake_payload_sha256": verified_intent.raw_sha256,
            "baseline_commit": baseline,
            "diff_fingerprint": receipt["diff_fingerprint"],
            "instruction_scope": instruction_scope,
            "instruction_scope_sha256": _sha256(_canonical_bytes(instruction_scope)),
            "write_modules": list(intent["target_modules"]),
            "authorized_write_paths": authorized_paths,
            "authorized_write_paths_sha256": _sha256(
                _canonical_bytes(authorized_paths)
            ),
            "write_scope_sha256": _sha256(_canonical_bytes({
                "write_modules": list(intent["target_modules"]),
                "authorized_write_paths": authorized_paths,
            })),
            "source_contract_change_claims_status": "UNVERIFIED_EXPECTATIONS",
            "source_contract_change_claims_sha256": intent[
                "public_contract_changes_sha256"
            ],
            "bound_contract_change_expectations": expectations_list,
            "verified_baseline_contract_facts_sha256": receipt[
                "verified_baseline_contract_facts_sha256"
            ],
            "changeset_contract_verification_required": True,
            "dependency_edges": [
                {"consumer": consumer, "provider": provider}
                for consumer, provider in edges
            ],
            "parallel_waves": waves,
            "requested_risk_tier": requested_risk,
            "effective_risk_tier": effective_risk,
            "requested_stage": stage,
            "planned_stage": stage,
            "required_checks": checks,
            "role_assignments": roles,
            "trusted_policy_id": verified_policy.policy_id,
            "trusted_policy_sha256": verified_policy.raw_sha256,
            "trusted_policy_status": verified_policy.status,
            "portable_trust_status": "WAITING_EXTERNAL",
            "release_eligible": False,
            "side_effects_authorized": False,
            "shadow_side_effect_count": 0,
        }
        plan_id = "change:" + _sha256(_canonical_bytes(body))[:32]
        with_id = dict(body)
        with_id["plan_id"] = plan_id
        plan_sha = _sha256(_canonical_bytes(with_id))
        plan = dict(with_id)
        plan["plan_sha256"] = plan_sha
        changed_final, _material_final, state_final = _state_snapshot(
            self.root, baseline, receipt
        )
        if changed_before != changed_final or state_before != state_final:
            raise ImpactError("repository truth changed before plan return")
        _schema_validate(plan, _PLAN_VALIDATOR, "module.change.plan/v2 output")
        self.__intent_authority.assert_issued(verified_intent)
        self.__receipt_authority.assert_issued(verified_receipt)
        self.__policy_authority.assert_issued(verified_policy)
        return plan


__all__ = [
    "ImpactAnalyzer", "ImpactError", "TrustedUtcClock", "IntentVerifierPort",
    "ReceiptVerifierPort", "ImpactPolicyVerifierPort", "IntentTrustAuthority",
    "ReceiptTrustAuthority", "ImpactPolicyTrustAuthority", "VerifiedIntentV2",
    "VerifiedReceiptV2", "VerifiedImpactPolicyV2",
]
