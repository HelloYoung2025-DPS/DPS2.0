"""Fail-closed instruction binding for the DPS AI Factory.

The resolver is deliberately read-only.  It accepts a normalized upgrade intent,
derives impact from registered JSON-compatible module Manifests, and returns an
immutable receipt.  It never writes the receipt or executes repository content.
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
import weakref
from collections.abc import Mapping as MappingABC
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Dict, Iterable, Mapping, Optional, Sequence, Set, Tuple

from jsonschema import Draft202012Validator, FormatChecker


class ResolutionError(ValueError):
    """An instruction scope cannot be trusted."""


_MODULE_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
_CONTRACT_ID = re.compile(r"^[a-z][a-z0-9]*(?:\.[a-z0-9]+)+$")
_COMMIT = re.compile(r"^[0-9a-f]{40}$")
_CONTRACT_MODES = {"active", "compat-read", "quarantine-only", "retired"}
_CONTRACT_STATUSES = {"proposed", "active", "deprecated", "retired"}
_ROLES = {
    "impact-planner",
    "contract-architect",
    "module-implementer",
    "independent-test-agent",
    "security-privacy-adversary",
    "reliability-reviewer",
    "windows-zenno-reviewer",
    "evidence-auditor",
    "release-rollback-controller",
}
_IDENTITIES = {
    "soul_id": re.compile(r"^soul_[a-f0-9]{64}\Z"),
    "device_binding_id": re.compile(r"^db_[a-f0-9]{32}\Z"),
    "platform_account_id": re.compile(r"^pa_[a-f0-9]{32}\Z"),
}
_TRACE_ID = re.compile(r"^trace_[a-f0-9]{32}\Z")
_IDEMPOTENCY_KEY = re.compile(r"^idem_[a-f0-9]{64}\Z")
_SHA256 = re.compile(r"^[a-f0-9]{64}\Z")
_OPAQUE_REQUEST_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{7,127}\Z")
_ACTOR_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{0,127}\Z")
_RECEIPT_ID = re.compile(r"^[a-z][a-z0-9-]*:[a-z0-9][a-z0-9._:-]{7,127}\Z")
_NONCE = re.compile(r"^nonce_[a-f0-9]{32}\Z")
_CANONICAL_UTC = re.compile(
    r"^[0-9]{4}-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])"
    r"T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z\Z"
)
_PUBLIC_CHANGE_FIELDS = {
    "contract_id",
    "major",
    "baseline_commit",
    "expected_mode",
    "expected_status",
    "expected_baseline_state",
    "change_kind",
    "expected_owner_module",
    "expected_source",
    "expected_source_sha256",
    "expected_previous_mode",
    "expected_previous_source_sha256",
    "quarantine_reason",
    "quarantine_evidence_sha256",
}
_CHANGE_KINDS = {
    "add-major",
    "additive-schema",
    "mode-transition",
    "introduce-quarantined-major",
}
_QUARANTINE_IMPORT_REASON = "historical-wire-import-no-baseline-major"
_MODE_TRANSITIONS = {
    ("active", "quarantine-only"),
    ("active", "retired"),
    ("quarantine-only", "retired"),
}
_PROVIDED_CONTRACT_MODES = {"active", "quarantine-only", "retired"}
_UPGRADE_INTENT_V2_FIELDS = {
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
    "intent_id",
    "auth_context_id",
    "requester_auth_context_sha256",
    "requester_auth_receipt_id",
    "requester_auth_nonce",
    "baseline_commit",
    "manifest_ownership_sha256",
    "manifest_ownership_receipt_id",
    "target_modules",
    "requested_paths",
    "public_contract_changes",
    "public_contract_changes_sha256",
    "contract_change_claims_status",
    "baseline_verification_required",
    "approval_subject_sha256",
    "upgrade_intent_sha256",
    "requested_risk_tier",
    "requested_stage",
    "requester",
    "authorization",
}
_AUTHORIZATION_FIELDS = {
    "status",
    "approved_by",
    "approver_role",
    "approval_scope",
    "approval_receipt_id",
    "approval_nonce",
    "approved_at",
    "approval_expires_at",
}
_COMMUNICATION_FIELDS = {
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
_COMMUNICATION_TRANSPORTS = {
    "in-process-api",
    "http-api",
    "event",
    "command",
    "receipt",
    "read-only-query",
    "soul-memory-adapter",
}
_ROUTABLE_CONTRACT_MODES = {"active", "compat-read"}
_MAX_WIRE_BYTES = 256 * 1024
_MAX_JSON_DEPTH = 64
_MAX_ACTIVE_TRUST_RECORDS = 4096
_INTAKE_AUDIENCE = "dps.factory-instruction-resolver"
_INTAKE_PEER = "factory-upgrade-intake"
_RECEIPT_CAPABILITY_AUDIENCE = "dps.factory-instruction-resolver.currentness"
_RECEIPT_CAPABILITY_ISSUER = "factory-instruction-resolver"
_TRUST_RECORD_FIELDS = {
    "payload_sha256",
    "upgrade_intent_sha256",
    "peer_module",
    "producer_module",
    "contract_id",
    "major",
    "audience",
    "trust_receipt_id",
    "trust_nonce",
    "issued_at",
    "expires_at",
    "requester_auth_expires_at",
    "manifest_ownership_expires_at",
    "approval_expires_at",
    "verification_mac",
}
_CORE_GOVERNANCE_PATHS = (
    "governance/modules/module-catalog.yaml",
    "governance/modules/dependency-graph.yaml",
    "governance/modules/compatibility.yaml",
    "governance/policies/risk-policy.yaml",
    "governance/policies/compatibility-policy.yaml",
)
_CANDIDATE_TRUST_PATHS = (
    "governance/policies/candidate-test-policy.yaml",
    ".editorconfig",
    ".gitattributes",
    ".github/CODEOWNERS",
    ".github/workflows/static-ci.yml",
    ".gitignore",
    ".node-version",
    ".powershell-version",
    ".python-version",
    "AGENTS.md",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "Dps.slnx",
    "NuGet.Config",
    "governance/schemas/agents-frontmatter.schema.json",
    "governance/schemas/candidate-gate-evidence.schema.json",
    "governance/schemas/candidate-test-policy.schema.json",
    "governance/schemas/module-manifest.schema.json",
    "governance/schemas/module-manifest.v1.schema.json",
    "governance/schemas/phase0-instruction-receipt.schema.json",
    "governance/schemas/phase0-test-evidence.schema.json",
    "governance/modules/module-catalog.yaml",
    "governance/modules/dependency-graph.yaml",
    "governance/modules/compatibility.yaml",
    "governance/policies/risk-policy.yaml",
    "governance/policies/compatibility-policy.yaml",
    "governance/verification/f9-scale-input.v1.schema.json",
    "governance/verification/f9-scale-input.v2.schema.json",
    "Modules/factory-instruction-resolver/contracts/provided/instruction.receipt.v1.schema.json",
    "Modules/factory-instruction-resolver/contracts/provided/instruction.receipt.v2.schema.json",
    "Modules/factory-instruction-resolver/src/instruction_resolver.py",
    "Modules/factory-upgrade-intake/contracts/provided/upgrade.intent.v1.schema.json",
    "Modules/factory-upgrade-intake/contracts/provided/upgrade.intent.v2.schema.json",
    "Modules/factory-upgrade-intake/src/upgrade_intake.py",
    "Tests/ci/test_candidate_gate.py",
    "Tests/ci/test_candidate_policy.py",
    "Tests/ci/test_manifest_schema_subset_evaluator.py",
    "Tests/ci/test_r0b_receipt_migration_dual_run.py",
    "Tests/ci/test_phase0_gate.py",
    "Tools/ci/phase0.py",
    "Tools/ci/run_candidate_gate.py",
    "Tools/ci/run_phase0_gate.py",
    "Tools/ci/validate_repo.py",
    "Tools/verification/external_gate.py",
    "Tools/verification/tests/test_external_gate.py",
    "global.json",
    "package-lock.json",
    "package.json",
    "requirements-ci.in",
    "requirements-ci.txt",
    "scripts/adb-pinned.sh",
    "scripts/bootstrap-ci-python.sh",
    "scripts/dotnet-pinned.sh",
    "scripts/pwsh-pinned.sh",
    "scripts/release.sh",
    "scripts/start-test-postgres.sh",
    "scripts/stop-test-postgres.sh",
    "toolchain.lock.json",
)
_GLOBAL_ENGINEERING_EXACT = {
    ".editorconfig",
    ".gitattributes",
    ".gitignore",
    ".node-version",
    ".powershell-version",
    ".python-version",
    "AGENTS.md",
    "CHANGELOG.md",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "Dps.slnx",
    "NuGet.Config",
    "README.md",
    "global.json",
    "package-lock.json",
    "package.json",
    "requirements-ci.in",
    "requirements-ci.txt",
    "toolchain.lock.json",
}
_GLOBAL_ENGINEERING_PREFIXES = (
    ".github/",
    "Docs/",
    "Tests/ci/",
    "Tools/ci/",
    "Tools/verification/",
    "governance/",
    "scripts/",
)
_LEGACY_TOMBSTONE_EXACT = {".omo.conf"}
_LEGACY_TOMBSTONE_PREFIXES = (".omo/", "Tools/omo_guard/")
_GENERATED_DIRECTORY_NAMES = {
    "__pycache__",
    "TestResults",
    "artifacts",
    "bin",
    "obj",
}
_GIT_EXECUTABLE = Path("/usr/bin/git")
_RECEIPT_V2_SCHEMA_PATH = (
    Path(__file__).resolve().parents[1]
    / "contracts"
    / "provided"
    / "instruction.receipt.v2.schema.json"
)


def _load_receipt_v2_validator() -> Draft202012Validator:
    try:
        schema = json.loads(_RECEIPT_V2_SCHEMA_PATH.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
    except Exception as exc:
        raise ResolutionError(
            "instruction.receipt/v2 schema is unavailable or invalid: " + str(exc)
        ) from exc
    return Draft202012Validator(schema, format_checker=FormatChecker())


_RECEIPT_V2_VALIDATOR = _load_receipt_v2_validator()


@dataclass(frozen=True)
class ContractDeclaration:
    contract_id: str
    major: int
    source: str
    status: str
    mode: str
    owner_module: str
    declaring_module: str
    declaration_kind: str

    @property
    def key(self) -> Tuple[str, int]:
        return (self.contract_id, self.major)

    def receipt_value(self) -> Dict[str, Any]:
        return {
            "contract_id": self.contract_id,
            "major": self.major,
            "mode": self.mode,
            "source": self.source,
            "status": self.status,
            "owner_module": self.owner_module,
            "declaring_module": self.declaring_module,
            "declaration_kind": self.declaration_kind,
        }


@dataclass(frozen=True)
class ModuleRecord:
    module_id: str
    root: Path
    agents_path: Path
    manifest_path: Path
    manifest: Mapping[str, Any]
    owned: Tuple[str, ...]
    provided: Mapping[Tuple[str, int], ContractDeclaration]
    consumed: Mapping[Tuple[str, int], ContractDeclaration]
    dependencies: Tuple[str, ...]


@dataclass(frozen=True, init=False)
class VerifiedUpgradeIntentV2:
    """Process-bound capability for one authenticated canonical Intake v2 wire."""

    raw_bytes: bytes
    payload_sha256: str
    upgrade_intent_sha256: str
    producer_module: str
    contract_id: str
    major: int
    peer_module: str
    audience: str
    trust_receipt_id: str
    trust_nonce: str
    issued_at: str
    verified_at: str
    expires_at: str
    requester_auth_expires_at: str
    manifest_ownership_expires_at: str
    approval_expires_at: Optional[str]
    _issuer: Any
    _issuer_token: object

    def __init__(
        self, *, _issuer: Any, _issuer_token: object, **values: Any
    ) -> None:
        if _issuer is None or _issuer_token is None:
            raise ResolutionError(
                "VerifiedUpgradeIntentV2 must be issued by the fixed trust authority"
            )
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])

    def canonical_intent(self) -> Dict[str, Any]:
        """Return an independently decoded copy; never expose mutable authority state."""

        return _decode_canonical_upgrade_intent(self.raw_bytes)


class TrustedUtcClock:
    """Composition-root clock; resolve callers cannot supply or replace time."""

    def __init__(self, *, _fixed_for_tests: Optional[str] = None) -> None:
        self.__fixed = _fixed_for_tests
        if self.__fixed is not None:
            _canonical_utc(self.__fixed, "trusted clock")

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
        """Advance only a fixed test clock; production clocks are immutable."""

        if self.__fixed is None:
            raise ResolutionError("production trusted clocks cannot be advanced")
        current = _parse_canonical_utc(self.__fixed, "trusted clock")
        replacement = _parse_canonical_utc(value, "trusted clock")
        if replacement <= current:
            raise ResolutionError("test trusted clock must advance monotonically")
        self.__fixed = value


class UpgradeIntentVerifierPort:
    """Fixed process-bound verifier port; no callback or per-call verifier exists."""

    def __init__(
        self,
        verification_key: bytes,
        clock: TrustedUtcClock,
        *,
        max_active_records: int = _MAX_ACTIVE_TRUST_RECORDS,
    ) -> None:
        if type(clock) is not TrustedUtcClock:
            raise ResolutionError("verifier requires the concrete trusted clock")
        if not isinstance(verification_key, bytes) or len(verification_key) < 32:
            raise ResolutionError("verifier key must be at least 256 bits")
        if (
            isinstance(max_active_records, bool)
            or not isinstance(max_active_records, int)
            or not 1 <= max_active_records <= _MAX_ACTIVE_TRUST_RECORDS
        ):
            raise ResolutionError("verifier active-record quota is invalid")
        self.__key = bytes(verification_key)
        self.__clock = clock
        self.__max_active_records = max_active_records
        self.__seen: Dict[str, Tuple[str, dt.datetime]] = {}

    def _prune(self, now: dt.datetime) -> None:
        for nonce, (_payload, expires_at) in tuple(self.__seen.items()):
            if expires_at <= now:
                self.__seen.pop(nonce, None)

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
        requester_auth_expires_at: str,
        manifest_ownership_expires_at: str,
        approval_expires_at: Optional[str],
    ) -> Dict[str, Any]:
        """Composition-root helper for the local process-bound verification path."""

        intent = _decode_canonical_upgrade_intent(raw_bytes)
        material = {
            "payload_sha256": _sha256(raw_bytes),
            "upgrade_intent_sha256": intent.get("upgrade_intent_sha256"),
            "peer_module": _INTAKE_PEER,
            "producer_module": intent.get("producer_module"),
            "contract_id": "upgrade.intent",
            "major": 2,
            "audience": _INTAKE_AUDIENCE,
            "trust_receipt_id": trust_receipt_id,
            "trust_nonce": trust_nonce,
            "issued_at": issued_at,
            "expires_at": expires_at,
            "requester_auth_expires_at": requester_auth_expires_at,
            "manifest_ownership_expires_at": manifest_ownership_expires_at,
            "approval_expires_at": approval_expires_at,
        }
        material["verification_mac"] = hmac.new(
            self.__key,
            b"DPS\x00dps.upgrade-intent-trust/v1\x00"
            + _canonical_bytes(material),
            hashlib.sha256,
        ).hexdigest()
        return material

    def verify(
        self, raw_bytes: bytes, attestation: Mapping[str, Any]
    ) -> Dict[str, Any]:
        if not isinstance(attestation, Mapping) or set(attestation) != _TRUST_RECORD_FIELDS:
            raise ResolutionError("Intake trust attestation has an invalid shape")
        material = {
            key: attestation[key]
            for key in sorted(_TRUST_RECORD_FIELDS - {"verification_mac"})
        }
        expected_mac = hmac.new(
            self.__key,
            b"DPS\x00dps.upgrade-intent-trust/v1\x00"
            + _canonical_bytes(material),
            hashlib.sha256,
        ).hexdigest()
        supplied_mac = attestation.get("verification_mac")
        if (
            not isinstance(supplied_mac, str)
            or not _SHA256.fullmatch(supplied_mac)
            or not hmac.compare_digest(supplied_mac, expected_mac)
        ):
            raise ResolutionError("Intake trust attestation verification failed")
        intent = _decode_canonical_upgrade_intent(raw_bytes)
        if (
            material["payload_sha256"] != _sha256(raw_bytes)
            or material["upgrade_intent_sha256"]
            != intent.get("upgrade_intent_sha256")
            or material["peer_module"] != _INTAKE_PEER
            or material["producer_module"] != "factory-upgrade-intake"
            or material["producer_module"] != intent.get("producer_module")
            or material["contract_id"] != "upgrade.intent"
            or material["major"] != 2
            or material["audience"] != _INTAKE_AUDIENCE
        ):
            raise ResolutionError("Intake trust attestation binding mismatch")
        if (
            not isinstance(material["trust_receipt_id"], str)
            or not _RECEIPT_ID.fullmatch(material["trust_receipt_id"])
            or not isinstance(material["trust_nonce"], str)
            or not _NONCE.fullmatch(material["trust_nonce"])
        ):
            raise ResolutionError("Intake trust receipt identity is invalid")
        now = _parse_canonical_utc(self.trusted_now(), "trusted clock")
        issued = _parse_canonical_utc(material["issued_at"], "trust issued_at")
        expires = _parse_canonical_utc(material["expires_at"], "trust expires_at")
        auth_expires = _parse_canonical_utc(
            material["requester_auth_expires_at"], "requester auth expires_at"
        )
        manifest_expires = _parse_canonical_utc(
            material["manifest_ownership_expires_at"],
            "Manifest ownership expires_at",
        )
        approval_value = material["approval_expires_at"]
        approval_expires = (
            _parse_canonical_utc(approval_value, "approval expires_at")
            if approval_value is not None
            else None
        )
        if (
            issued > now
            or expires <= now
            or auth_expires <= now
            or manifest_expires <= now
            or (approval_expires is not None and approval_expires <= now)
        ):
            raise ResolutionError("Intake trust or authority proof is expired")
        self._prune(now)
        nonce = material["trust_nonce"]
        prior = self.__seen.get(nonce)
        if prior is not None and prior[0] != material["payload_sha256"]:
            raise ResolutionError("Intake trust nonce replayed with different bytes")
        if prior is None and len(self.__seen) >= self.__max_active_records:
            raise ResolutionError("Intake trust verifier active-record quota exceeded")
        retention_expiry = min(
            value
            for value in (expires, auth_expires, manifest_expires, approval_expires)
            if value is not None
        )
        self.__seen[nonce] = (material["payload_sha256"], retention_expiry)
        return dict(material)


class UpgradeIntentTrustAuthority:
    """Fixed composition-root authority that alone may seal verified Intake wire."""

    def __init__(
        self,
        verifier_port: UpgradeIntentVerifierPort,
        *,
        max_active_capabilities: int = _MAX_ACTIVE_TRUST_RECORDS,
    ) -> None:
        if type(verifier_port) is not UpgradeIntentVerifierPort:
            raise ResolutionError(
                "trust authority requires the concrete verifier port, not a callback"
            )
        if (
            isinstance(max_active_capabilities, bool)
            or not isinstance(max_active_capabilities, int)
            or not 1 <= max_active_capabilities <= _MAX_ACTIVE_TRUST_RECORDS
        ):
            raise ResolutionError("trust authority capability quota is invalid")
        self.__port = verifier_port
        self.__max_active_capabilities = max_active_capabilities
        self.__token = object()
        self.__issued: weakref.WeakValueDictionary[
            int, VerifiedUpgradeIntentV2
        ] = weakref.WeakValueDictionary()
        self.__by_receipt: weakref.WeakValueDictionary[
            str, VerifiedUpgradeIntentV2
        ] = weakref.WeakValueDictionary()
        self.__fingerprints: Dict[int, str] = {}
        self.__expires: Dict[int, dt.datetime] = {}

    def _prune(self) -> None:
        now = _parse_canonical_utc(self.trusted_now(), "trusted clock")
        for capability_id, expires_at in tuple(self.__expires.items()):
            capability = self.__issued.get(capability_id)
            if capability is None or expires_at <= now:
                if capability is not None and (
                    self.__by_receipt.get(capability.trust_receipt_id)
                    is capability
                ):
                    self.__by_receipt.pop(capability.trust_receipt_id, None)
                self.__issued.pop(capability_id, None)
                self.__fingerprints.pop(capability_id, None)
                self.__expires.pop(capability_id, None)

    @staticmethod
    def _fingerprint(capability: VerifiedUpgradeIntentV2) -> str:
        return _sha256(
            _canonical_bytes(
                {
                    "payload_sha256": _sha256(capability.raw_bytes),
                    "upgrade_intent_sha256": capability.upgrade_intent_sha256,
                    "producer_module": capability.producer_module,
                    "contract_id": capability.contract_id,
                    "major": capability.major,
                    "peer_module": capability.peer_module,
                    "audience": capability.audience,
                    "trust_receipt_id": capability.trust_receipt_id,
                    "trust_nonce": capability.trust_nonce,
                    "issued_at": capability.issued_at,
                    "verified_at": capability.verified_at,
                    "expires_at": capability.expires_at,
                    "requester_auth_expires_at": capability.requester_auth_expires_at,
                    "manifest_ownership_expires_at": capability.manifest_ownership_expires_at,
                    "approval_expires_at": capability.approval_expires_at,
                }
            )
        )

    def verify_and_seal(
        self, raw_bytes: bytes, attestation: Mapping[str, Any]
    ) -> VerifiedUpgradeIntentV2:
        if not isinstance(raw_bytes, bytes):
            raise ResolutionError("canonical Intake wire must be bytes")
        # Verification is never bypassed for a cache hit.  The cache is only
        # an identity-preserving optimization after the complete attestation
        # shape, MAC, expiry, nonce, audience, and payload binding have been
        # rechecked by the fixed verifier port.
        verified = self.__port.verify(raw_bytes, attestation)
        self._prune()
        receipt = verified["trust_receipt_id"]
        existing = self.__by_receipt.get(receipt)
        if existing is not None:
            if existing.raw_bytes != raw_bytes:
                raise ResolutionError("trust receipt replayed with different raw bytes")
            existing_material = {
                "payload_sha256": existing.payload_sha256,
                "upgrade_intent_sha256": existing.upgrade_intent_sha256,
                "peer_module": existing.peer_module,
                "producer_module": existing.producer_module,
                "contract_id": existing.contract_id,
                "major": existing.major,
                "audience": existing.audience,
                "trust_receipt_id": existing.trust_receipt_id,
                "trust_nonce": existing.trust_nonce,
                "issued_at": existing.issued_at,
                "expires_at": existing.expires_at,
                "requester_auth_expires_at": existing.requester_auth_expires_at,
                "manifest_ownership_expires_at": (
                    existing.manifest_ownership_expires_at
                ),
                "approval_expires_at": existing.approval_expires_at,
            }
            if verified != existing_material:
                raise ResolutionError(
                    "trust receipt replayed with different authority binding"
                )
            self.assert_issued(existing)
            return existing
        intent = _decode_canonical_upgrade_intent(raw_bytes)
        if len(self.__expires) >= self.__max_active_capabilities:
            raise ResolutionError("trust authority capability quota exceeded")
        verified_at = self.__port.trusted_now()
        capability = VerifiedUpgradeIntentV2(
            _issuer=self,
            _issuer_token=self.__token,
            raw_bytes=bytes(raw_bytes),
            payload_sha256=verified["payload_sha256"],
            upgrade_intent_sha256=verified["upgrade_intent_sha256"],
            producer_module=verified["producer_module"],
            contract_id=verified["contract_id"],
            major=verified["major"],
            peer_module=verified["peer_module"],
            audience=verified["audience"],
            trust_receipt_id=verified["trust_receipt_id"],
            trust_nonce=verified["trust_nonce"],
            issued_at=verified["issued_at"],
            verified_at=verified_at,
            expires_at=verified["expires_at"],
            requester_auth_expires_at=verified["requester_auth_expires_at"],
            manifest_ownership_expires_at=verified[
                "manifest_ownership_expires_at"
            ],
            approval_expires_at=verified["approval_expires_at"],
        )
        self.__issued[id(capability)] = capability
        self.__by_receipt[capability.trust_receipt_id] = capability
        self.__fingerprints[id(capability)] = self._fingerprint(capability)
        expiry_values = [
            _parse_canonical_utc(capability.expires_at, "trust expires_at"),
            _parse_canonical_utc(
                capability.requester_auth_expires_at,
                "requester auth expires_at",
            ),
            _parse_canonical_utc(
                capability.manifest_ownership_expires_at,
                "Manifest ownership expires_at",
            ),
        ]
        if capability.approval_expires_at is not None:
            expiry_values.append(
                _parse_canonical_utc(
                    capability.approval_expires_at, "approval expires_at"
                )
            )
        self.__expires[id(capability)] = min(expiry_values)
        return capability

    def assert_issued(self, capability: VerifiedUpgradeIntentV2) -> None:
        self._prune()
        if (
            type(capability) is not VerifiedUpgradeIntentV2
            or capability._issuer is not self
            or capability._issuer_token is not self.__token
            or self.__issued.get(id(capability)) is not capability
            or capability.payload_sha256 != _sha256(capability.raw_bytes)
            or self.__fingerprints.get(id(capability))
            != self._fingerprint(capability)
        ):
            raise ResolutionError(
                "upgrade intent capability was not issued by this trust authority"
            )
        intent = _decode_canonical_upgrade_intent(capability.raw_bytes)
        if (
            capability.upgrade_intent_sha256
            != intent.get("upgrade_intent_sha256")
            or capability.producer_module != intent.get("producer_module")
            or capability.contract_id != "upgrade.intent"
            or capability.major != 2
            or capability.peer_module != _INTAKE_PEER
            or capability.audience != _INTAKE_AUDIENCE
        ):
            raise ResolutionError("sealed upgrade intent capability was mutated")
        now = _parse_canonical_utc(self.trusted_now(), "trusted clock")
        if (
            _parse_canonical_utc(capability.expires_at, "trust expires_at") <= now
            or _parse_canonical_utc(
                capability.requester_auth_expires_at,
                "requester auth expires_at",
            )
            <= now
            or _parse_canonical_utc(
                capability.manifest_ownership_expires_at,
                "Manifest ownership expires_at",
            )
            <= now
            or (
                capability.approval_expires_at is not None
                and _parse_canonical_utc(
                    capability.approval_expires_at, "approval expires_at"
                )
                <= now
            )
        ):
            raise ResolutionError("sealed upgrade intent capability has expired")

    def trusted_now(self) -> str:
        return self.__port.trusted_now()


@dataclass(frozen=True, init=False)
class VerifiedInstructionReceiptV2(MappingABC):
    """Process-bound proof that this Resolver authority issued exact receipt bytes."""

    raw_bytes: bytes
    receipt_sha256: str
    receipt_id: str
    producer_module: str
    contract_id: str
    major: int
    issuer: str
    audience: str
    issued_at: str
    expires_at: str
    nonce: str
    generation: int
    status: str
    _issuer: Any
    _issuer_token: object

    def __init__(
        self, *, _issuer: Any, _issuer_token: object, **values: Any
    ) -> None:
        if _issuer is None or _issuer_token is None:
            raise ResolutionError(
                "VerifiedInstructionReceiptV2 must be issued by the fixed receipt authority"
            )
        for name in self.__dataclass_fields__:
            if name == "_issuer":
                object.__setattr__(self, name, _issuer)
            elif name == "_issuer_token":
                object.__setattr__(self, name, _issuer_token)
            else:
                object.__setattr__(self, name, values[name])

    def canonical_receipt(self) -> Dict[str, Any]:
        try:
            value = json.loads(
                self.raw_bytes.decode("utf-8", errors="strict"),
                object_pairs_hook=_unique_json_object,
                parse_float=_reject_json_float,
                parse_int=_parse_json_int,
                parse_constant=_reject_json_constant,
            )
        except ResolutionError:
            raise
        except (UnicodeDecodeError, json.JSONDecodeError, TypeError, ValueError) as exc:
            raise ResolutionError("issued receipt bytes are not canonical JSON") from exc
        if type(value) is not dict or _canonical_bytes(value) != self.raw_bytes:
            raise ResolutionError("issued receipt bytes are not canonical JSON")
        return _strict_receipt_v2_object(value, require_bound=self.status == "BOUND")

    def __getitem__(self, key: str) -> Any:
        return self.canonical_receipt()[key]

    def __iter__(self):
        return iter(self.canonical_receipt())

    def __len__(self) -> int:
        return len(self.canonical_receipt())


class InstructionReceiptTrustAuthority:
    """Resolver-private, bounded authority for receipt currentness capabilities."""

    def __init__(
        self,
        source_authority: UpgradeIntentTrustAuthority,
        *,
        max_active_capabilities: int = _MAX_ACTIVE_TRUST_RECORDS,
    ) -> None:
        if type(source_authority) is not UpgradeIntentTrustAuthority:
            raise ResolutionError(
                "receipt authority requires the fixed Intake trust authority"
            )
        if (
            isinstance(max_active_capabilities, bool)
            or not isinstance(max_active_capabilities, int)
            or not 1 <= max_active_capabilities <= _MAX_ACTIVE_TRUST_RECORDS
        ):
            raise ResolutionError("receipt authority capability quota is invalid")
        self.__source_authority = source_authority
        self.__max_active_capabilities = max_active_capabilities
        self.__token = object()
        self.__nonce_key = os.urandom(32)
        self.__generation = 0
        self.__issued: weakref.WeakValueDictionary[
            int, VerifiedInstructionReceiptV2
        ] = weakref.WeakValueDictionary()
        self.__by_content: weakref.WeakValueDictionary[
            str, VerifiedInstructionReceiptV2
        ] = weakref.WeakValueDictionary()
        self.__bound_by_receipt: weakref.WeakValueDictionary[
            str, VerifiedInstructionReceiptV2
        ] = weakref.WeakValueDictionary()
        self.__fingerprints: Dict[int, str] = {}
        self.__expires: Dict[int, dt.datetime] = {}

    def trusted_now(self) -> str:
        return self.__source_authority.trusted_now()

    @staticmethod
    def _receipt_expiry(receipt: Mapping[str, Any]) -> dt.datetime:
        values = [
            _parse_canonical_utc(
                receipt["source_intake_trust_expires_at"],
                "source_intake_trust_expires_at",
            ),
            _parse_canonical_utc(
                receipt["source_requester_auth_expires_at"],
                "source_requester_auth_expires_at",
            ),
            _parse_canonical_utc(
                receipt["source_manifest_ownership_expires_at"],
                "source_manifest_ownership_expires_at",
            ),
        ]
        if receipt["source_approval_expires_at"] is not None:
            values.append(
                _parse_canonical_utc(
                    receipt["source_approval_expires_at"],
                    "source_approval_expires_at",
                )
            )
        return min(values)

    @staticmethod
    def _fingerprint(capability: VerifiedInstructionReceiptV2) -> str:
        return _sha256(
            _canonical_bytes(
                {
                    "raw_sha256": _sha256(capability.raw_bytes),
                    "receipt_sha256": capability.receipt_sha256,
                    "receipt_id": capability.receipt_id,
                    "producer_module": capability.producer_module,
                    "contract_id": capability.contract_id,
                    "major": capability.major,
                    "issuer": capability.issuer,
                    "audience": capability.audience,
                    "issued_at": capability.issued_at,
                    "expires_at": capability.expires_at,
                    "nonce": capability.nonce,
                    "generation": capability.generation,
                    "status": capability.status,
                }
            )
        )

    def _prune(self) -> None:
        now = _parse_canonical_utc(self.trusted_now(), "trusted clock")
        for capability_id, expires_at in tuple(self.__expires.items()):
            capability = self.__issued.get(capability_id)
            if capability is None or expires_at <= now:
                if capability is not None:
                    if self.__by_content.get(capability.receipt_sha256) is capability:
                        self.__by_content.pop(capability.receipt_sha256, None)
                    if (
                        self.__bound_by_receipt.get(capability.receipt_id)
                        is capability
                    ):
                        self.__bound_by_receipt.pop(capability.receipt_id, None)
                self.__issued.pop(capability_id, None)
                self.__fingerprints.pop(capability_id, None)
                self.__expires.pop(capability_id, None)

    def _issue(self, receipt: Dict[str, Any]) -> VerifiedInstructionReceiptV2:
        parsed = _strict_receipt_v2_object(
            receipt, require_bound=receipt.get("status") == "BOUND"
        )
        raw_bytes = _canonical_bytes(parsed)
        receipt_sha256 = _sha256(raw_bytes)
        self._prune()
        existing = self.__by_content.get(receipt_sha256)
        if existing is not None:
            if existing.raw_bytes != raw_bytes:
                raise ResolutionError("receipt digest collision detected")
            self.assert_issued(existing)
            return existing
        if len(self.__expires) >= self.__max_active_capabilities:
            raise ResolutionError("receipt authority capability quota exceeded")
        issued_at = self.trusted_now()
        now = _parse_canonical_utc(issued_at, "receipt capability issued_at")
        expires_at_value = self._receipt_expiry(parsed)
        if expires_at_value <= now:
            raise ResolutionError("receipt capability source authority has expired")
        self.__generation += 1
        generation = self.__generation
        nonce = "nonce_" + hmac.new(
            self.__nonce_key,
            b"DPS\x00dps.instruction-receipt-capability/v1\x00"
            + receipt_sha256.encode("ascii")
            + b"\x00"
            + str(generation).encode("ascii")
            + b"\x00"
            + issued_at.encode("ascii"),
            hashlib.sha256,
        ).hexdigest()[:32]
        capability = VerifiedInstructionReceiptV2(
            _issuer=self,
            _issuer_token=self.__token,
            raw_bytes=raw_bytes,
            receipt_sha256=receipt_sha256,
            receipt_id=parsed["receipt_id"],
            producer_module=parsed["producer_module"],
            contract_id="instruction.receipt",
            major=2,
            issuer=_RECEIPT_CAPABILITY_ISSUER,
            audience=_RECEIPT_CAPABILITY_AUDIENCE,
            issued_at=issued_at,
            expires_at=expires_at_value.strftime("%Y-%m-%dT%H:%M:%SZ"),
            nonce=nonce,
            generation=generation,
            status=parsed["status"],
        )
        self.__issued[id(capability)] = capability
        self.__by_content[receipt_sha256] = capability
        self.__fingerprints[id(capability)] = self._fingerprint(capability)
        self.__expires[id(capability)] = expires_at_value
        return capability

    def issue_bound(
        self, receipt: Dict[str, Any]
    ) -> VerifiedInstructionReceiptV2:
        parsed = _strict_receipt_v2_object(receipt, require_bound=True)
        existing = self.__bound_by_receipt.get(parsed["receipt_id"])
        if existing is not None:
            if existing.raw_bytes != _canonical_bytes(parsed):
                raise ResolutionError(
                    "same instruction receipt ID was presented with different bytes"
                )
            self.assert_issued(existing)
            return existing
        capability = self._issue(parsed)
        self.__bound_by_receipt[parsed["receipt_id"]] = capability
        return capability

    def issue_stale(
        self, original: VerifiedInstructionReceiptV2, reason: str
    ) -> VerifiedInstructionReceiptV2:
        self.assert_issued(original)
        if original.status != "BOUND":
            raise ResolutionError("only an issued BOUND receipt can become STALE")
        if not isinstance(reason, str) or not 1 <= len(reason) <= 512:
            raise ResolutionError("STALE reason is invalid")
        stale = original.canonical_receipt()
        stale["status"] = "STALE"
        stale["invalidated_reason"] = reason
        return self._issue(stale)

    def assert_issued(self, capability: VerifiedInstructionReceiptV2) -> None:
        self._prune()
        if (
            type(capability) is not VerifiedInstructionReceiptV2
            or capability._issuer is not self
            or capability._issuer_token is not self.__token
            or self.__issued.get(id(capability)) is not capability
            or capability.receipt_sha256 != _sha256(capability.raw_bytes)
            or self.__fingerprints.get(id(capability))
            != self._fingerprint(capability)
        ):
            raise ResolutionError(
                "instruction receipt capability was not issued by this Resolver authority"
            )
        now = _parse_canonical_utc(self.trusted_now(), "trusted clock")
        issued_at = _parse_canonical_utc(
            capability.issued_at, "receipt capability issued_at"
        )
        expires_at = _parse_canonical_utc(
            capability.expires_at, "receipt capability expires_at"
        )
        if issued_at > now or expires_at <= now:
            raise ResolutionError("instruction receipt capability has expired")
        if (
            capability.producer_module != _RECEIPT_CAPABILITY_ISSUER
            or capability.contract_id != "instruction.receipt"
            or capability.major != 2
            or capability.issuer != _RECEIPT_CAPABILITY_ISSUER
            or capability.audience != _RECEIPT_CAPABILITY_AUDIENCE
            or not _NONCE.fullmatch(capability.nonce)
            or isinstance(capability.generation, bool)
            or not isinstance(capability.generation, int)
            or capability.generation < 1
            or capability.status not in {"BOUND", "STALE"}
        ):
            raise ResolutionError("instruction receipt capability binding is invalid")
        receipt = capability.canonical_receipt()
        if (
            receipt["receipt_id"] != capability.receipt_id
            or receipt["producer_module"] != capability.producer_module
            or receipt["contract_id"] != "instruction.receipt/v2"
            or receipt["status"] != capability.status
        ):
            raise ResolutionError("instruction receipt capability bytes were swapped")


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _reject_json_float(value: str) -> Any:
    raise ResolutionError("canonical Intake wire forbids floating point values")


def _reject_json_constant(value: str) -> Any:
    raise ResolutionError("canonical Intake wire forbids non-finite values")


def _parse_json_int(value: str) -> int:
    if len(value.lstrip("-")) > 10:
        raise ResolutionError("canonical Intake wire integer exceeds the bound")
    return int(value)


def _unique_json_object(pairs: Sequence[Tuple[str, Any]]) -> Dict[str, Any]:
    result: Dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ResolutionError("canonical Intake wire has a duplicate key: " + key)
        result[key] = value
    return result


def _json_depth(value: Any) -> int:
    if isinstance(value, Mapping):
        return 1 + max((_json_depth(item) for item in value.values()), default=0)
    if isinstance(value, list):
        return 1 + max((_json_depth(item) for item in value), default=0)
    return 1


def _is_strict_json_tree(value: Any) -> bool:
    if value is None or type(value) in {str, int, bool}:
        return True
    if type(value) is list:
        return all(_is_strict_json_tree(item) for item in value)
    if type(value) is dict:
        return all(
            type(key) is str and _is_strict_json_tree(item)
            for key, item in value.items()
        )
    return False


def _strict_receipt_v2_object(
    value: Any, *, require_bound: bool
) -> Dict[str, Any]:
    """Return a canonical JSON copy only when the public v2 Schema accepts it."""

    if type(value) is not dict or not _is_strict_json_tree(value):
        raise ResolutionError("instruction receipt must be a strict JSON object")
    if _json_depth(value) > _MAX_JSON_DEPTH:
        raise ResolutionError("instruction receipt exceeds the depth bound")
    try:
        encoded = json.dumps(
            value,
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=False,
            allow_nan=False,
        ).encode("utf-8")
    except (TypeError, ValueError) as exc:
        raise ResolutionError("instruction receipt is not canonical JSON") from exc
    if len(encoded) > 16 * 1024 * 1024:
        raise ResolutionError("instruction receipt exceeds the size bound")
    parsed = json.loads(encoded.decode("utf-8"))
    errors = sorted(
        _RECEIPT_V2_VALIDATOR.iter_errors(parsed),
        key=lambda error: tuple(str(item) for item in error.absolute_path),
    )
    if errors:
        error = errors[0]
        location = "$" + "".join("[%r]" % item for item in error.absolute_path)
        raise ResolutionError(
            "instruction.receipt/v2 schema rejected %s: %s"
            % (location, error.message)
        )
    time_fields = (
        "occurred_at",
        "resolved_at",
        "source_intake_trust_issued_at",
        "source_intake_verified_at",
        "source_intake_trust_expires_at",
        "source_requester_auth_expires_at",
        "source_manifest_ownership_expires_at",
    )
    parsed_times = {
        field: _parse_canonical_utc(parsed[field], field) for field in time_fields
    }
    approval_expiry = parsed["source_approval_expires_at"]
    if approval_expiry is not None:
        parsed_times["source_approval_expires_at"] = _parse_canonical_utc(
            approval_expiry, "source_approval_expires_at"
        )
    resolved_at = parsed_times["resolved_at"]
    if (
        parsed_times["occurred_at"] != resolved_at
        or parsed_times["source_intake_verified_at"] != resolved_at
        or parsed_times["source_intake_trust_issued_at"] > resolved_at
        or any(
            parsed_times[field] <= resolved_at
            for field in (
                "source_intake_trust_expires_at",
                "source_requester_auth_expires_at",
                "source_manifest_ownership_expires_at",
            )
        )
        or (
            approval_expiry is not None
            and parsed_times["source_approval_expires_at"] <= resolved_at
        )
    ):
        raise ResolutionError(
            "instruction receipt time bindings are inconsistent or expired"
        )
    if require_bound:
        if parsed["status"] != "BOUND" or parsed["invalidated_reason"] is not None:
            raise ResolutionError("only a BOUND v2 receipt can be validated")
        material = dict(parsed)
        supplied_receipt_id = material.pop("receipt_id")
        expected_receipt_id = "instruction:" + _sha256(
            _canonical_bytes(material)
        )[:32]
        if supplied_receipt_id != expected_receipt_id:
            raise ResolutionError(
                "BOUND receipt_id does not match its canonical bound content"
            )
    return parsed


def _decode_canonical_upgrade_intent(raw_bytes: bytes) -> Dict[str, Any]:
    if (
        not isinstance(raw_bytes, bytes)
        or not raw_bytes
        or len(raw_bytes) > _MAX_WIRE_BYTES
        or raw_bytes.startswith(b"\xef\xbb\xbf")
    ):
        raise ResolutionError("canonical Intake wire is empty, oversized, or has a BOM")
    try:
        text = raw_bytes.decode("utf-8", errors="strict")
        value = json.loads(
            text,
            object_pairs_hook=_unique_json_object,
            parse_float=_reject_json_float,
            parse_int=_parse_json_int,
            parse_constant=_reject_json_constant,
        )
    except ResolutionError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, TypeError) as exc:
        raise ResolutionError("canonical Intake wire is invalid JSON") from exc
    if not isinstance(value, dict):
        raise ResolutionError("canonical Intake wire must contain an object")
    if _json_depth(value) > _MAX_JSON_DEPTH:
        raise ResolutionError("canonical Intake wire exceeds the depth bound")
    if _canonical_bytes(value) != raw_bytes:
        raise ResolutionError("Intake wire must use exact canonical JSON bytes")
    return value


def _domain_sha256(domain: str, value: Any) -> str:
    return _sha256(
        b"DPS\x00"
        + domain.encode("ascii")
        + b"\x00"
        + _canonical_bytes(value)
    )


def _git(root: Path, args: Sequence[str], required: bool = True) -> str:
    if (
        not _GIT_EXECUTABLE.is_file()
        or _GIT_EXECUTABLE.is_symlink()
        or not os.access(str(_GIT_EXECUTABLE), os.X_OK)
    ):
        raise ResolutionError("locked /usr/bin/git is missing or unsafe")
    completed = subprocess.run(
        [
            str(_GIT_EXECUTABLE),
            "-c",
            "core.hooksPath=/dev/null",
            "-c",
            "core.fsmonitor=false",
            *args,
        ],
        cwd=str(root),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
        text=True,
        encoding="utf-8",
        env={
            "GIT_CONFIG_GLOBAL": "/dev/null",
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_SYSTEM": "/dev/null",
            "GIT_OPTIONAL_LOCKS": "0",
            "GIT_TERMINAL_PROMPT": "0",
            "HOME": "/var/empty",
            "LANG": "C",
            "LC_ALL": "C",
            "PATH": "/usr/bin:/bin",
            "TMPDIR": "/tmp",
        },
    )
    if required and completed.returncode != 0:
        raise ResolutionError(
            "Git metadata lookup failed: " + completed.stderr.strip()
        )
    return completed.stdout.strip() if completed.returncode == 0 else ""


def _git_bytes(root: Path, args: Sequence[str], required: bool = True) -> bytes:
    if (
        not _GIT_EXECUTABLE.is_file()
        or _GIT_EXECUTABLE.is_symlink()
        or not os.access(str(_GIT_EXECUTABLE), os.X_OK)
    ):
        raise ResolutionError("locked /usr/bin/git is missing or unsafe")
    completed = subprocess.run(
        [
            str(_GIT_EXECUTABLE),
            "-c",
            "core.hooksPath=/dev/null",
            "-c",
            "core.fsmonitor=false",
            *args,
        ],
        cwd=str(root),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
        env={
            "GIT_CONFIG_GLOBAL": "/dev/null",
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_SYSTEM": "/dev/null",
            "GIT_OPTIONAL_LOCKS": "0",
            "GIT_TERMINAL_PROMPT": "0",
            "HOME": "/var/empty",
            "LANG": "C",
            "LC_ALL": "C",
            "PATH": "/usr/bin:/bin",
            "TMPDIR": "/tmp",
        },
    )
    if required and completed.returncode != 0:
        raise ResolutionError(
            "Git metadata lookup failed: "
            + completed.stderr.decode("utf-8", errors="replace").strip()
        )
    return completed.stdout if completed.returncode == 0 else b""


def _git_hash_object_bytes(root: Path, data: bytes) -> str:
    """Hash the exact bytes already read, never a second path read."""

    if (
        not _GIT_EXECUTABLE.is_file()
        or _GIT_EXECUTABLE.is_symlink()
        or not os.access(str(_GIT_EXECUTABLE), os.X_OK)
    ):
        raise ResolutionError("locked /usr/bin/git is missing or unsafe")
    completed = subprocess.run(
        [
            str(_GIT_EXECUTABLE),
            "-c",
            "core.hooksPath=/dev/null",
            "-c",
            "core.fsmonitor=false",
            "hash-object",
            "--stdin",
        ],
        cwd=str(root),
        input=data,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
        env={
            "GIT_CONFIG_GLOBAL": "/dev/null",
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_SYSTEM": "/dev/null",
            "GIT_OPTIONAL_LOCKS": "0",
            "GIT_TERMINAL_PROMPT": "0",
            "HOME": "/var/empty",
            "LANG": "C",
            "LC_ALL": "C",
            "PATH": "/usr/bin:/bin",
            "TMPDIR": "/tmp",
        },
    )
    if completed.returncode != 0:
        raise ResolutionError(
            "Git byte hashing failed: "
            + completed.stderr.decode("utf-8", errors="replace").strip()
        )
    value = completed.stdout.decode("ascii", errors="strict").strip()
    if not _COMMIT.fullmatch(value):
        raise ResolutionError("Git byte hashing returned an invalid object id")
    return value


def _safe_relative(value: Any, *, reject_hidden: bool) -> str:
    if not isinstance(value, str) or not value or value.startswith("/") or "\\" in value:
        raise ResolutionError("repository path must be non-empty relative POSIX form")
    if value[-1].isspace() or any(ord(char) < 32 or ord(char) == 127 for char in value):
        raise ResolutionError("repository path contains control or trailing whitespace")
    pure = PurePosixPath(value)
    if pure.as_posix() != value or any(part in {"", ".", ".."} for part in pure.parts):
        raise ResolutionError("repository path contains traversal or normalization")
    if any(part in {".git", ".omo"} for part in pure.parts):
        raise ResolutionError("hidden task or Git state is forbidden")
    if reject_hidden and any(part.startswith(".") for part in pure.parts):
        raise ResolutionError("hidden requested paths are forbidden")
    return value


def _canonical_utc(value: Any, field: str) -> str:
    if not isinstance(value, str) or _CANONICAL_UTC.fullmatch(value) is None:
        raise ResolutionError(field + " must use canonical UTC second precision")
    try:
        parsed = dt.datetime.strptime(value, "%Y-%m-%dT%H:%M:%SZ")
    except ValueError as exc:
        raise ResolutionError(field + " is not a valid UTC timestamp") from exc
    if parsed.strftime("%Y-%m-%dT%H:%M:%SZ") != value:
        raise ResolutionError(field + " is not canonical")
    return value


def _parse_canonical_utc(value: Any, field: str) -> dt.datetime:
    return dt.datetime.strptime(_canonical_utc(value, field), "%Y-%m-%dT%H:%M:%SZ")


def _is_global_engineering_path(value: str) -> bool:
    return value in _GLOBAL_ENGINEERING_EXACT or value.startswith(
        _GLOBAL_ENGINEERING_PREFIXES
    )


def _is_legacy_tombstone(root: Path, baseline: str, value: str) -> bool:
    if not (
        value in _LEGACY_TOMBSTONE_EXACT
        or value.startswith(_LEGACY_TOMBSTONE_PREFIXES)
    ):
        return False
    candidate = root / value
    if candidate.exists() or candidate.is_symlink():
        return False
    return bool(_git(root, ["rev-parse", baseline + ":" + value], required=False))


def _normalize_changed_path(root: Path, baseline: str, value: Any) -> str:
    if isinstance(value, str) and _is_legacy_tombstone(root, baseline, value):
        pure = PurePosixPath(value)
        if value.startswith("/") or "\\" in value or pure.as_posix() != value:
            raise ResolutionError("legacy tombstone path is not normalized")
        return value
    return _safe_relative(value, reject_hidden=False)


def _safe_file(root: Path, relative_path: str) -> Path:
    normalized = _safe_relative(relative_path, reject_hidden=False)
    candidate = root
    for part in PurePosixPath(normalized).parts:
        candidate = candidate / part
        if candidate.is_symlink():
            raise ResolutionError("symlinked repository path is forbidden: " + normalized)
    if not candidate.is_file():
        raise ResolutionError("required bound file is missing: " + normalized)
    resolved_root = root.resolve(strict=True)
    resolved = candidate.resolve(strict=True)
    if resolved != resolved_root and resolved_root not in resolved.parents:
        raise ResolutionError("bound file escapes repository: " + normalized)
    return candidate


def _contract_items(manifest: Mapping[str, Any], kind: str) -> Sequence[Mapping[str, Any]]:
    contracts = manifest.get("contracts")
    if not isinstance(contracts, Mapping):
        raise ResolutionError("Manifest contracts must be an object")
    items = contracts.get(kind)
    if not isinstance(items, list):
        raise ResolutionError("Manifest contracts.%s must be a list" % kind)
    if any(not isinstance(item, Mapping) for item in items):
        raise ResolutionError("Manifest contract entries must be objects")
    return items


def _parse_contract_declarations(
    root: Path,
    manifest: Mapping[str, Any],
    module_id: str,
    kind: str,
) -> Dict[Tuple[str, int], ContractDeclaration]:
    """Parse the frozen Manifest per-major contract shape without fallback.

    This intentionally mirrors the central Phase0 `manifest_contract_modes`
    semantics: mode belongs to the exact `(contractId, major)` declaration,
    duplicate exact identities fail, and a provider can never use
    `compat-read`.  The additional fields are retained so receipt generation
    cannot silently discard source, owner, status, or mode facts.
    """

    if kind not in {"provided", "consumed"}:
        raise ResolutionError("unknown contract declaration kind: " + kind)
    declarations: Dict[Tuple[str, int], ContractDeclaration] = {}
    expected_fields = {
        "contractId",
        "major",
        "source",
        "status",
        "mode",
        "ownerModule",
    }
    for index, item in enumerate(_contract_items(manifest, kind)):
        if set(item) != expected_fields:
            missing = sorted(expected_fields.difference(item))
            unknown = sorted(set(item).difference(expected_fields))
            raise ResolutionError(
                "contract {0}[{1}] must use the frozen Manifest shape; missing={2}; unknown={3}".format(
                    kind, index, ",".join(missing), ",".join(unknown)
                )
            )
        contract_id = item.get("contractId")
        major = item.get("major")
        source = item.get("source")
        status = item.get("status")
        mode = item.get("mode")
        owner_module = item.get("ownerModule")
        if not isinstance(contract_id, str) or not _CONTRACT_ID.fullmatch(contract_id):
            raise ResolutionError("invalid contract id in " + module_id)
        if isinstance(major, bool) or not isinstance(major, int) or major < 1:
            raise ResolutionError("invalid contract major for " + contract_id)
        if not isinstance(source, str):
            raise ResolutionError("invalid contract source for %s/v%d" % (contract_id, major))
        _safe_file(root, source)
        if not isinstance(status, str) or status not in _CONTRACT_STATUSES:
            raise ResolutionError("unknown contract status for %s/v%d" % (contract_id, major))
        if not isinstance(mode, str) or mode not in _CONTRACT_MODES:
            raise ResolutionError(
                "unknown or missing compatibility mode for %s/v%d" % (contract_id, major)
            )
        if kind == "provided" and mode == "compat-read":
            raise ResolutionError(
                "provided contract %s/v%d cannot use compat-read" % (contract_id, major)
            )
        if (status == "retired") != (mode == "retired"):
            raise ResolutionError(
                "retired status and mode must agree for %s/v%d" % (contract_id, major)
            )
        if not isinstance(owner_module, str) or not _MODULE_ID.fullmatch(owner_module):
            raise ResolutionError("invalid contract owner for %s/v%d" % (contract_id, major))
        if kind == "provided" and owner_module != module_id:
            raise ResolutionError(
                "provided contract owner mismatch for %s/v%d" % (contract_id, major)
            )
        key = (contract_id, major)
        if key in declarations:
            raise ResolutionError(
                "duplicate %s contract-major declaration: %s/v%d"
                % (kind, contract_id, major)
            )
        declarations[key] = ContractDeclaration(
            contract_id=contract_id,
            major=major,
            source=source,
            status=status,
            mode=mode,
            owner_module=owner_module,
            declaring_module=module_id,
            declaration_kind=kind,
        )
    return declarations


def _manifest_contract_snapshot(
    manifest: Mapping[str, Any], kind: str
) -> Dict[Tuple[str, int], Mapping[str, Any]]:
    if kind not in {"provided", "consumed"}:
        raise ResolutionError("unknown baseline contract declaration kind: " + kind)
    expected_fields = {
        "contractId",
        "major",
        "source",
        "status",
        "mode",
        "ownerModule",
    }
    result: Dict[Tuple[str, int], Mapping[str, Any]] = {}
    for index, item in enumerate(_contract_items(manifest, kind)):
        if set(item) != expected_fields:
            raise ResolutionError(
                "baseline contract %s[%d] does not use the frozen Manifest shape"
                % (kind, index)
            )
        contract_id = item.get("contractId")
        major = item.get("major")
        source = item.get("source")
        status = item.get("status")
        mode = item.get("mode")
        owner_module = item.get("ownerModule")
        if (
            not isinstance(contract_id, str)
            or not _CONTRACT_ID.fullmatch(contract_id)
            or isinstance(major, bool)
            or not isinstance(major, int)
            or major < 1
            or not isinstance(source, str)
            or not isinstance(status, str)
            or status not in _CONTRACT_STATUSES
            or not isinstance(mode, str)
            or mode not in _CONTRACT_MODES
            or not isinstance(owner_module, str)
            or not _MODULE_ID.fullmatch(owner_module)
        ):
            raise ResolutionError("baseline contract declaration is invalid")
        _safe_relative(source, reject_hidden=False)
        if kind == "provided" and mode == "compat-read":
            raise ResolutionError("baseline provider cannot use compat-read")
        if (status == "retired") != (mode == "retired"):
            raise ResolutionError("baseline retired status and mode must agree")
        key = (contract_id, major)
        if key in result:
            raise ResolutionError(
                "baseline has duplicate %s contract-major: %s/v%d"
                % (kind, contract_id, major)
            )
        result[key] = dict(item)
    return result


def _baseline_contracts(
    root: Path, baseline: str, record: ModuleRecord, kind: str
) -> Dict[Tuple[str, int], Mapping[str, Any]]:
    path = record.manifest_path.relative_to(root).as_posix()
    blob = _git(root, ["rev-parse", "--verify", baseline + ":" + path], required=False)
    if not blob:
        return {}
    raw = _git_bytes(root, ["show", baseline + ":" + path])
    try:
        manifest = json.loads(raw.decode("utf-8-sig"))
    except Exception as exc:
        raise ResolutionError("baseline Manifest is invalid: " + path) from exc
    if not isinstance(manifest, Mapping):
        raise ResolutionError("baseline Manifest is not an object: " + path)
    return _manifest_contract_snapshot(manifest, kind)


def _baseline_file_sha256(root: Path, baseline: str, path: str) -> Optional[str]:
    blob = _git(root, ["rev-parse", "--verify", baseline + ":" + path], required=False)
    if not blob:
        return None
    return _sha256(_git_bytes(root, ["show", baseline + ":" + path]))


def _baseline_contract_indexes(
    root: Path,
    baseline: str,
    records: Mapping[str, ModuleRecord],
) -> Tuple[
    Dict[Tuple[str, int], Tuple[str, Mapping[str, Any]]],
    Dict[Tuple[str, int], Set[str]],
    Dict[str, str],
]:
    owners: Dict[Tuple[str, int], Tuple[str, Mapping[str, Any]]] = {}
    consumers: Dict[Tuple[str, int], Set[str]] = {}
    family_owners: Dict[str, str] = {}
    baseline_consumed: Dict[str, Dict[Tuple[str, int], Mapping[str, Any]]] = {}
    for module_id, record in records.items():
        provided = _baseline_contracts(root, baseline, record, "provided")
        baseline_consumed[module_id] = _baseline_contracts(
            root, baseline, record, "consumed"
        )
        for key, item in provided.items():
            if item.get("ownerModule") != module_id:
                raise ResolutionError(
                    "baseline provided owner mismatch for %s/v%d" % key
                )
            if key in owners:
                raise ResolutionError(
                    "baseline has multiple exact owners for %s/v%d" % key
                )
            family_owner = family_owners.get(key[0])
            if family_owner is not None and family_owner != module_id:
                raise ResolutionError(
                    "baseline contract family has multiple owners: " + key[0]
                )
            family_owners[key[0]] = module_id
            owners[key] = (module_id, item)
    for module_id, declarations in baseline_consumed.items():
        for key, item in declarations.items():
            owner = owners.get(key)
            if owner is None:
                raise ResolutionError(
                    "baseline consumer has no exact owner: %s/v%d" % key
                )
            if item.get("ownerModule") != owner[0] or item.get("source") != owner[1].get(
                "source"
            ):
                raise ResolutionError(
                    "baseline consumer owner/source mismatch for %s/v%d" % key
                )
            consumers.setdefault(key, set()).add(module_id)
    return owners, consumers, family_owners


def load_module_records(root: Path) -> Dict[str, ModuleRecord]:
    """Load registered modules while rejecting aliases, symlinks, and duplicates."""

    modules_root = root / "Modules"
    if modules_root.is_symlink() or not modules_root.is_dir():
        raise ResolutionError("Modules root is missing or symlinked")
    records: Dict[str, ModuleRecord] = {}
    contract_owners: Dict[Tuple[str, int], str] = {}
    for module_root in sorted(modules_root.iterdir(), key=lambda item: item.name):
        manifest_path = module_root / "module.yaml"
        agents_path = module_root / "AGENTS.md"
        if not manifest_path.is_file() and not agents_path.is_file():
            continue
        if module_root.is_symlink() or manifest_path.is_symlink() or agents_path.is_symlink():
            raise ResolutionError("registered module paths cannot be symlinks")
        if not manifest_path.is_file() or not agents_path.is_file():
            raise ResolutionError("registered module requires AGENTS.md and module.yaml")
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        except Exception as exc:
            raise ResolutionError("invalid module Manifest: %s" % manifest_path) from exc
        module = manifest.get("module") if isinstance(manifest, Mapping) else None
        module_id = module.get("id") if isinstance(module, Mapping) else None
        if not isinstance(module_id, str) or not _MODULE_ID.fullmatch(module_id):
            raise ResolutionError("invalid module id in %s" % manifest_path)
        if module_id != module_root.name or module_id in records:
            raise ResolutionError("module directory/id mismatch or duplicate: " + module_id)
        paths = manifest.get("paths")
        owned = paths.get("owned") if isinstance(paths, Mapping) else None
        if not isinstance(owned, list) or not owned or any(
            not isinstance(item, str) or item.startswith("/") or ".." in PurePosixPath(item).parts
            for item in owned
        ):
            raise ResolutionError("module has invalid ownership patterns: " + module_id)

        provided = _parse_contract_declarations(
            root, manifest, module_id, "provided"
        )
        consumed = _parse_contract_declarations(
            root, manifest, module_id, "consumed"
        )
        for key in provided:
            other = contract_owners.get(key)
            if other is not None:
                raise ResolutionError(
                    "multiple owners for %s/v%d" % (key[0], key[1])
                )
            contract_owners[key] = module_id
        raw_dependencies = manifest.get("dependencies")
        if not isinstance(raw_dependencies, list):
            raise ResolutionError("Manifest dependencies must be a list")
        dependencies = []
        for item in raw_dependencies:
            dependency = item.get("moduleId") if isinstance(item, Mapping) else None
            if not isinstance(dependency, str) or not _MODULE_ID.fullmatch(dependency):
                raise ResolutionError("invalid dependency in " + module_id)
            dependencies.append(dependency)
        records[module_id] = ModuleRecord(
            module_id=module_id,
            root=module_root,
            agents_path=agents_path,
            manifest_path=manifest_path,
            manifest=manifest,
            owned=tuple(owned),
            provided=provided,
            consumed=consumed,
            dependencies=tuple(sorted(set(dependencies))),
        )
    if not records:
        raise ResolutionError("no registered modules")
    for record in records.values():
        unknown = set(record.dependencies).difference(records)
        if unknown:
            raise ResolutionError("unknown dependencies for %s: %s" % (
                record.module_id, ", ".join(sorted(unknown))
            ))
    family_owners: Dict[str, str] = {}
    exact_owners: Dict[Tuple[str, int], ContractDeclaration] = {}
    for record in records.values():
        for key, declaration in record.provided.items():
            family_owner = family_owners.get(key[0])
            if family_owner is not None and family_owner != record.module_id:
                raise ResolutionError("contract family has multiple owners: " + key[0])
            family_owners[key[0]] = record.module_id
            exact_owners[key] = declaration
    for record in records.values():
        for key, declaration in record.consumed.items():
            owner = exact_owners.get(key)
            if owner is None:
                raise ResolutionError(
                    "consumed contract has no exact owner: %s/v%d" % (key[0], key[1])
                )
            if declaration.owner_module != owner.declaring_module:
                raise ResolutionError(
                    "consumer owner mismatch for %s/v%d" % (key[0], key[1])
                )
            if declaration.source != owner.source:
                raise ResolutionError(
                    "consumer source mismatch for %s/v%d" % (key[0], key[1])
                )
    _validate_communication_declarations(records)
    return records


def _validate_communication_declarations(
    records: Mapping[str, ModuleRecord],
) -> None:
    """Reject hidden, unroutable, duplicate, or one-sided exact-major edges."""

    exact_owners = {
        key: declaration
        for record in records.values()
        for key, declaration in record.provided.items()
    }
    edge_index: Dict[
        Tuple[str, str, str, int, str], Mapping[str, Any]
    ] = {}
    for module_id, record in records.items():
        communication = record.manifest.get("communication")
        if not isinstance(communication, Mapping) or set(communication) != {
            "inbound",
            "outbound",
        }:
            raise ResolutionError("Manifest communication must use the frozen shape")
        for direction in ("inbound", "outbound"):
            edges = communication.get(direction)
            if not isinstance(edges, list):
                raise ResolutionError(
                    "Manifest communication.%s must be a list" % direction
                )
            for index, edge in enumerate(edges):
                if not isinstance(edge, Mapping):
                    raise ResolutionError("communication edge must be an object")
                edge_fields = set(edge)
                if edge_fields not in {
                    frozenset(_COMMUNICATION_FIELDS),
                    frozenset(_COMMUNICATION_FIELDS | {"preserveProducer"}),
                }:
                    raise ResolutionError(
                        "communication edge must use the frozen Manifest shape"
                    )
                peer = edge.get("peerModule")
                contract_id = edge.get("contractId")
                major = edge.get("major")
                if (
                    not isinstance(peer, str)
                    or not _MODULE_ID.fullmatch(peer)
                    or peer not in records
                ):
                    raise ResolutionError("communication edge has unknown peer module")
                if (
                    not isinstance(contract_id, str)
                    or not _CONTRACT_ID.fullmatch(contract_id)
                    or isinstance(major, bool)
                    or not isinstance(major, int)
                    or major < 1
                ):
                    raise ResolutionError("communication edge has invalid contract identity")
                if edge.get("direction") != direction:
                    raise ResolutionError(
                        "communication edge direction disagrees with its collection"
                    )
                if edge.get("transport") not in _COMMUNICATION_TRANSPORTS:
                    raise ResolutionError("communication edge has unknown transport")
                timeout = edge.get("timeoutMs")
                if (
                    isinstance(timeout, bool)
                    or not isinstance(timeout, int)
                    or not 1 <= timeout <= 300000
                ):
                    raise ResolutionError("communication edge has invalid timeout")
                if any(
                    not isinstance(edge.get(field), str) or not edge[field]
                    for field in (
                        "retryPolicy",
                        "idempotencyKey",
                        "authScope",
                        "failureMode",
                    )
                ):
                    raise ResolutionError("communication edge has empty semantics")
                if "preserveProducer" in edge and not isinstance(
                    edge["preserveProducer"], bool
                ):
                    raise ResolutionError(
                        "communication preserveProducer must be boolean"
                    )
                if edge.get("preserveProducer") is True and direction != "outbound":
                    raise ResolutionError(
                        "preserveProducer is valid only on an outbound relay"
                    )
                key = (contract_id, major)
                declarations = tuple(
                    declaration
                    for declaration in (
                        record.provided.get(key),
                        record.consumed.get(key),
                    )
                    if declaration is not None
                )
                if not declarations:
                    raise ResolutionError(
                        "communication edge uses undeclared exact contract: %s/v%d"
                        % key
                    )
                owner = exact_owners.get(key)
                if owner is None or owner.mode != "active":
                    raise ResolutionError(
                        "runtime communication requires an active exact owner: %s/v%d"
                        % key
                    )
                if not any(
                    declaration.mode in _ROUTABLE_CONTRACT_MODES
                    for declaration in declarations
                ):
                    raise ResolutionError(
                        "runtime communication cannot route quarantine-only or retired contract: %s/v%d"
                        % key
                    )
                edge_key = (module_id, peer, contract_id, major, direction)
                if edge_key in edge_index:
                    raise ResolutionError(
                        "duplicate exact communication edge for %s/v%d" % key
                    )
                edge_index[edge_key] = edge

    for edge_key, edge in edge_index.items():
        module_id, peer, contract_id, major, direction = edge_key
        reciprocal_direction = "inbound" if direction == "outbound" else "outbound"
        reciprocal = edge_index.get(
            (peer, module_id, contract_id, major, reciprocal_direction)
        )
        if reciprocal is None:
            raise ResolutionError(
                "communication edge lacks reciprocal exact-major peer edge: %s/v%d"
                % (contract_id, major)
            )
        if (
            reciprocal.get("transport") != edge.get("transport")
            or reciprocal.get("timeoutMs") != edge.get("timeoutMs")
        ):
            raise ResolutionError(
                "reciprocal communication semantics mismatch for %s/v%d"
                % (contract_id, major)
            )
        key = (contract_id, major)
        owner = exact_owners[key]
        try:
            schema = json.loads(
                _safe_file(
                    owner_root := records[owner.declaring_module].root.parents[1],
                    owner.source,
                ).read_text(encoding="utf-8-sig")
            )
        except Exception as exc:
            raise ResolutionError(
                "communication contract Schema cannot be read: %s/v%d" % key
            ) from exc
        properties = schema.get("properties") if isinstance(schema, Mapping) else None
        producer = properties.get("producer_module") if isinstance(properties, Mapping) else None
        allowed_producers: Set[str] = set()
        if isinstance(producer, Mapping):
            if isinstance(producer.get("const"), str):
                allowed_producers.add(producer["const"])
            if isinstance(producer.get("enum"), list):
                allowed_producers.update(
                    value for value in producer["enum"] if isinstance(value, str)
                )
        if not allowed_producers or any(
            value not in records for value in allowed_producers
        ):
            raise ResolutionError(
                "communication contract lacks registered exact producers: %s/v%d"
                % key
            )
        if edge.get("preserveProducer") is True and (
            module_id in allowed_producers or len(allowed_producers) != 1
        ):
            raise ResolutionError(
                "preserveProducer is relay-only with one exact Schema producer: %s/v%d"
                % key
            )
        expected_producer = module_id if direction == "outbound" else peer
        relay_allowed = False
        if direction == "outbound":
            relay_allowed = (
                edge.get("preserveProducer") is True
                and key in records[module_id].consumed
            )
        else:
            relay_allowed = (
                reciprocal.get("preserveProducer") is True
                and key in records[peer].consumed
            )
        if expected_producer not in allowed_producers and not relay_allowed:
            raise ResolutionError(
                "communication direction conflicts with exact Schema producer: %s/v%d"
                % key
            )


def _contract_index(
    records: Mapping[str, ModuleRecord],
) -> Tuple[
    Dict[Tuple[str, int], ContractDeclaration],
    Dict[Tuple[str, int], Set[str]],
]:
    owners: Dict[Tuple[str, int], ContractDeclaration] = {}
    consumers: Dict[Tuple[str, int], Set[str]] = {}
    for module_id, record in records.items():
        for key, declaration in record.provided.items():
            if key in owners:
                raise ResolutionError(
                    "contract has multiple exact-major owners: %s/v%d" % key
                )
            owners[key] = declaration
        for key in record.consumed:
            consumers.setdefault(key, set()).add(module_id)
    for key in consumers:
        if key not in owners:
            raise ResolutionError(
                "consumed contract has no exact owner: %s/v%d" % key
            )
    return owners, consumers


def _runtime_communication_edges(
    records: Mapping[str, ModuleRecord], key: Tuple[str, int]
) -> Tuple[Tuple[str, str, str], ...]:
    """Return exact-major runtime edges while failing closed on malformed shape."""

    matches = []
    for module_id, record in records.items():
        communication = record.manifest.get("communication")
        if not isinstance(communication, Mapping):
            raise ResolutionError("Manifest communication must be an object")
        for direction in ("inbound", "outbound"):
            edges = communication.get(direction)
            if not isinstance(edges, list) or any(
                not isinstance(edge, Mapping) for edge in edges
            ):
                raise ResolutionError(
                    "Manifest communication.%s must be an object list" % direction
                )
            for edge in edges:
                if edge.get("contractId") == key[0] and edge.get("major") == key[1]:
                    peer = edge.get("peerModule")
                    if not isinstance(peer, str) or not _MODULE_ID.fullmatch(peer):
                        raise ResolutionError("communication edge has invalid peer module")
                    matches.append((module_id, direction, peer))
    return tuple(sorted(matches))


def _owners_for_path(path: str, records: Mapping[str, ModuleRecord]) -> Set[str]:
    return {
        module_id
        for module_id, record in records.items()
        if any(fnmatch.fnmatchcase(path, pattern) for pattern in record.owned)
    }


def _changed_paths(root: Path, baseline: str) -> Tuple[str, ...]:
    tracked = _git(
        root,
        ["-c", "core.quotepath=false", "diff", "--name-only", "-z", "--diff-filter=ACDMRTUXB", baseline, "--"],
    )
    untracked = _git(
        root,
        ["-c", "core.quotepath=false", "ls-files", "-z", "--others", "--exclude-standard"],
    )
    values = {line for text in (tracked, untracked) for line in text.split("\0") if line}
    return tuple(sorted(_normalize_changed_path(root, baseline, value) for value in values))


def _diff_material(root: Path, paths: Sequence[str]) -> Dict[str, Any]:
    index_entries = _git_bytes(root, ["ls-files", "--stage", "-z"])
    status_record = _git_bytes(
        root,
        ["status", "--porcelain=v2", "-z", "--untracked-files=all"],
    )
    path_material = []
    for value in paths:
        candidate = root / value
        if candidate.is_symlink():
            raise ResolutionError("changed symlink is forbidden: " + value)
        path_material.append(
            {
                "path": value,
                "sha256": _sha256(candidate.read_bytes())
                if candidate.is_file()
                else None,
                "worktree_kind": "file" if candidate.is_file() else "missing",
                "worktree_executable": bool(candidate.stat().st_mode & 0o111)
                if candidate.is_file()
                else None,
            }
        )
    return {
        "paths": path_material,
        "index_entries_sha256": _sha256(index_entries),
        "git_status_sha256": _sha256(status_record),
    }


def _validate_identity_envelope(intent: Mapping[str, Any]) -> None:
    for field, pattern in _IDENTITIES.items():
        value = intent.get(field)
        if value is not None and (not isinstance(value, str) or not pattern.fullmatch(value)):
            raise ResolutionError("invalid normalized identity: " + field)


def _public_contract_changes(
    intent: Mapping[str, Any],
) -> Dict[Tuple[str, int], Mapping[str, Any]]:
    raw = intent.get("public_contract_changes")
    if not isinstance(raw, list):
        raise ResolutionError("public_contract_changes must be a list")
    if len(raw) > 128:
        raise ResolutionError("public_contract_changes exceeds the bounded count")
    result: Dict[Tuple[str, int], Mapping[str, Any]] = {}
    canonical_values = []
    for index, item in enumerate(raw):
        if not isinstance(item, Mapping) or set(item) != _PUBLIC_CHANGE_FIELDS:
            raise ResolutionError(
                "public_contract_changes[%d] must use the exact v2 shape" % index
            )
        contract_id = item.get("contract_id")
        major = item.get("major")
        item_baseline = item.get("baseline_commit")
        mode = item.get("expected_mode")
        status = item.get("expected_status")
        baseline_state = item.get("expected_baseline_state")
        kind = item.get("change_kind")
        owner_module = item.get("expected_owner_module")
        source = item.get("expected_source")
        source_sha256 = item.get("expected_source_sha256")
        previous_mode = item.get("expected_previous_mode")
        previous_sha256 = item.get("expected_previous_source_sha256")
        quarantine_reason = item.get("quarantine_reason")
        quarantine_evidence_sha256 = item.get("quarantine_evidence_sha256")
        if not isinstance(contract_id, str) or not _CONTRACT_ID.fullmatch(contract_id):
            raise ResolutionError("public contract change has invalid contract_id")
        if (
            isinstance(major, bool)
            or not isinstance(major, int)
            or not 1 <= major <= 2147483647
        ):
            raise ResolutionError("public contract change has invalid major")
        if item_baseline != intent.get("baseline_commit"):
            raise ResolutionError(
                "public contract change baseline_commit must equal intent baseline"
            )
        if not isinstance(mode, str) or mode not in _PROVIDED_CONTRACT_MODES:
            raise ResolutionError("public contract change has invalid mode")
        if not isinstance(status, str) or status not in _CONTRACT_STATUSES:
            raise ResolutionError("public contract change has invalid status")
        if baseline_state not in {"present", "absent"}:
            raise ResolutionError("public contract change has invalid baseline state")
        if (status == "retired") != (mode == "retired"):
            raise ResolutionError(
                "public contract expected retired status and mode must agree"
            )
        if not isinstance(kind, str) or kind not in _CHANGE_KINDS:
            raise ResolutionError("public contract change has invalid change_kind")
        if not isinstance(owner_module, str) or not _MODULE_ID.fullmatch(owner_module):
            raise ResolutionError("public contract change has invalid owner_module")
        normalized_source = _safe_relative(source, reject_hidden=True)
        if len(normalized_source) > 512 or any(
            value in normalized_source for value in ("*", "?", "[", "]")
        ):
            raise ResolutionError("public contract expected source is too long")
        if not isinstance(source_sha256, str) or not _SHA256.fullmatch(source_sha256):
            raise ResolutionError("public contract change has invalid source_sha256")
        if previous_mode is not None and (
            not isinstance(previous_mode, str)
            or previous_mode not in _PROVIDED_CONTRACT_MODES
        ):
            raise ResolutionError("public contract change has invalid previous_mode")
        if previous_sha256 is not None and (
            not isinstance(previous_sha256, str) or not _SHA256.fullmatch(previous_sha256)
        ):
            raise ResolutionError(
                "public contract change has invalid previous_source_sha256"
            )
        if kind == "add-major":
            if (
                mode != "active"
                or status != "proposed"
                or baseline_state != "absent"
                or previous_mode is not None
                or previous_sha256 is not None
                or quarantine_reason is not None
                or quarantine_evidence_sha256 is not None
            ):
                raise ResolutionError("add-major must start active without previous facts")
        elif kind == "additive-schema":
            if (
                mode != "active"
                or status not in {"proposed", "active"}
                or baseline_state != "present"
                or previous_mode != "active"
                or previous_sha256 is None
                or previous_sha256 == source_sha256
                or quarantine_reason is not None
                or quarantine_evidence_sha256 is not None
            ):
                raise ResolutionError(
                    "additive-schema requires active mode and distinct previous digest"
                )
        elif kind == "mode-transition":
            if (
                baseline_state != "present"
                or previous_mode is None
                or previous_sha256 != source_sha256
                or (previous_mode, mode) not in _MODE_TRANSITIONS
                or (mode == "quarantine-only" and status != "deprecated")
                or (mode == "retired" and status != "retired")
                or quarantine_reason is not None
                or quarantine_evidence_sha256 is not None
            ):
                raise ResolutionError(
                    "mode-transition must be monotonic with unchanged source bytes"
                )
        else:
            quarantine_material = {
                "baseline_commit": item_baseline,
                "contract_id": contract_id,
                "major": major,
                "expected_source": normalized_source,
                "expected_source_sha256": source_sha256,
                "quarantine_reason": quarantine_reason,
            }
            if (
                baseline_state != "absent"
                or mode != "quarantine-only"
                or status != "deprecated"
                or previous_mode is not None
                or previous_sha256 is not None
                or quarantine_reason != _QUARANTINE_IMPORT_REASON
                or not isinstance(quarantine_evidence_sha256, str)
                or not _SHA256.fullmatch(quarantine_evidence_sha256)
                or quarantine_evidence_sha256
                != _domain_sha256(
                    "dps.upgrade-intent/v2/quarantine-import-evidence",
                    quarantine_material,
                )
            ):
                raise ResolutionError(
                    "introduce-quarantined-major requires an exact absent-baseline quarantine proof"
                )
        key = (contract_id, major)
        if key in result:
            raise ResolutionError(
                "duplicate public contract change: %s/v%d" % key
            )
        value = dict(item)
        result[key] = value
        canonical_values.append(value)
    canonical_values.sort(
        key=lambda value: (
            value["contract_id"],
            value["major"],
            value["baseline_commit"],
            value["expected_mode"],
            value["expected_status"],
            value["expected_baseline_state"],
            value["change_kind"],
            value["expected_owner_module"],
            value["expected_source"],
            value["expected_source_sha256"],
            value["expected_previous_mode"] or "",
            value["expected_previous_source_sha256"] or "",
            value["quarantine_reason"] or "",
            value["quarantine_evidence_sha256"] or "",
        )
    )
    expected_digest = intent.get("public_contract_changes_sha256")
    if (
        not isinstance(expected_digest, str)
        or not _SHA256.fullmatch(expected_digest)
        or expected_digest
        != _domain_sha256(
            "dps.upgrade-intent/v2/public-contract-changes",
            {
                "baseline_commit": intent.get("baseline_commit"),
                "manifest_ownership_sha256": intent.get(
                    "manifest_ownership_sha256"
                ),
                "public_contract_changes": canonical_values,
            },
        )
    ):
        raise ResolutionError("public_contract_changes_sha256 mismatch")
    if canonical_values != raw:
        raise ResolutionError("public_contract_changes must use canonical order")
    return result


def _requires_human_approval(risk_tier: str, stage: str) -> bool:
    return (risk_tier == "R3" and stage != "development") or (
        risk_tier == "R2" and stage in {"canary", "rolling", "soaking"}
    )


def _validate_intent(
    intent: Mapping[str, Any],
    *,
    resolved_at: str,
    capability: VerifiedUpgradeIntentV2,
) -> None:
    if not isinstance(intent, Mapping):
        raise ResolutionError("upgrade intent must be an object")
    if intent.get("schema_version") == "dps.upgrade-intent/v1" or intent.get(
        "contract_id"
    ) == "upgrade.intent/v1":
        raise ResolutionError("upgrade.intent/v1 is quarantine-only")
    if set(intent) != _UPGRADE_INTENT_V2_FIELDS:
        raise ResolutionError("upgrade intent must use the exact v2 shape")
    if intent.get("schema_version") != "dps.upgrade-intent/v2":
        raise ResolutionError("unknown upgrade intent schema")
    if intent.get("contract_id") != "upgrade.intent/v2":
        raise ResolutionError("unknown upgrade intent contract")
    if intent.get("producer_module") != "factory-upgrade-intake":
        raise ResolutionError("untrusted upgrade intent producer")
    _validate_identity_envelope(intent)
    for field, pattern in (("trace_id", _TRACE_ID), ("idempotency_key", _IDEMPOTENCY_KEY)):
        if not isinstance(intent.get(field), str) or pattern.fullmatch(intent[field]) is None:
            raise ResolutionError("invalid intent field: " + field)
    for field in ("intent_id", "auth_context_id"):
        if (
            not isinstance(intent.get(field), str)
            or not _OPAQUE_REQUEST_ID.fullmatch(intent[field])
        ):
            raise ResolutionError("invalid intent field: " + field)
    _canonical_utc(intent.get("occurred_at"), "occurred_at")
    if intent.get("privacy_class") != "internal":
        raise ResolutionError("Factory intent must be internal")
    for field in (
        "requester_auth_context_sha256",
        "manifest_ownership_sha256",
        "approval_subject_sha256",
        "upgrade_intent_sha256",
    ):
        if not isinstance(intent.get(field), str) or not _SHA256.fullmatch(
            intent[field]
        ):
            raise ResolutionError("invalid intent digest: " + field)
    if (
        not isinstance(intent.get("requester_auth_receipt_id"), str)
        or not _RECEIPT_ID.fullmatch(intent["requester_auth_receipt_id"])
        or not isinstance(intent.get("manifest_ownership_receipt_id"), str)
        or not _RECEIPT_ID.fullmatch(intent["manifest_ownership_receipt_id"])
        or not isinstance(intent.get("requester_auth_nonce"), str)
        or not _NONCE.fullmatch(intent["requester_auth_nonce"])
    ):
        raise ResolutionError("requester or Manifest authority proof is invalid")
    if intent.get("contract_change_claims_status") != "UNVERIFIED_EXPECTATIONS":
        raise ResolutionError("contract change claims must remain unverified expectations")
    if intent.get("baseline_verification_required") is not True:
        raise ResolutionError("baseline verification is required")
    risk_tier = intent.get("requested_risk_tier")
    if not isinstance(risk_tier, str) or risk_tier not in {"R0", "R1", "R2", "R3"}:
        raise ResolutionError("unknown or forbidden risk tier")
    stage = intent.get("requested_stage")
    if not isinstance(stage, str) or stage not in {
        "development",
        "shadow",
        "canary",
        "rolling",
        "soaking",
    }:
        raise ResolutionError("unknown requested stage")
    targets = intent.get("target_modules")
    if (
        not isinstance(targets, list)
        or not targets
        or len(targets) > 32
        or len(set(targets)) != len(targets)
        or targets != sorted(targets)
        or any(
            not isinstance(value, str) or not _MODULE_ID.fullmatch(value)
            for value in targets
        )
    ):
        raise ResolutionError("target_modules must be canonical and sorted")
    requested = intent.get("requested_paths")
    if not isinstance(requested, list) or not requested or len(requested) > 512:
        raise ResolutionError("requested_paths must be a bounded list")
    normalized_requested = [
        _safe_relative(value, reject_hidden=True) for value in requested
    ]
    if (
        len(set(normalized_requested)) != len(normalized_requested)
        or normalized_requested != sorted(normalized_requested)
        or any(
            token in value
            for value in normalized_requested
            for token in ("*", "?", "[", "]")
        )
    ):
        raise ResolutionError("requested_paths must be canonical and sorted")
    requester = intent.get("requester")
    if (
        not isinstance(requester, Mapping)
        or set(requester) != {"identity", "role"}
        or not isinstance(requester.get("identity"), str)
        or not _ACTOR_ID.fullmatch(requester["identity"])
        or requester.get("role")
        not in {
            "human-requester",
            "impact-planner",
            "contract-architect",
            "module-implementer",
        }
    ):
        raise ResolutionError("requester is invalid")
    authorization = intent.get("authorization")
    if not isinstance(authorization, Mapping) or set(authorization) != _AUTHORIZATION_FIELDS:
        raise ResolutionError("authorization is invalid")
    status = authorization.get("status")
    if status == "not-required":
        if _requires_human_approval(risk_tier, stage) or dict(authorization) != {
            "status": "not-required",
            "approved_by": None,
            "approver_role": "not-applicable",
            "approval_scope": [],
            "approval_receipt_id": None,
            "approval_nonce": None,
            "approved_at": None,
            "approval_expires_at": None,
        }:
            raise ResolutionError("not-required authorization is inconsistent")
        if capability.approval_expires_at is not None:
            raise ResolutionError(
                "not-required authorization cannot carry approval trust expiry"
            )
    elif status == "approved":
        scope = authorization.get("approval_scope")
        approved_by = authorization.get("approved_by")
        if (
            not isinstance(scope, list)
            or not scope
            or len(scope) > 5
            or len(set(scope)) != len(scope)
            or scope != sorted(scope)
            or stage not in scope
            or any(not isinstance(value, str) or value not in {
                "development", "shadow", "canary", "rolling", "soaking"
            } for value in scope)
            or not isinstance(approved_by, str)
            or not _ACTOR_ID.fullmatch(approved_by)
            or approved_by == requester["identity"]
            or authorization.get("approver_role") != "human-release-approver"
            or not isinstance(authorization.get("approval_receipt_id"), str)
            or not _RECEIPT_ID.fullmatch(authorization["approval_receipt_id"])
            or not isinstance(authorization.get("approval_nonce"), str)
            or not _NONCE.fullmatch(authorization["approval_nonce"])
        ):
            raise ResolutionError("approved authorization is invalid")
        approved_at = _canonical_utc(authorization.get("approved_at"), "approved_at")
        expires_at = _canonical_utc(
            authorization.get("approval_expires_at"), "approval_expires_at"
        )
        if expires_at <= approved_at:
            raise ResolutionError("approval expiry must follow approval time")
        if capability.approval_expires_at != authorization["approval_expires_at"]:
            raise ResolutionError("approval trust expiry does not match Intake wire")
    else:
        raise ResolutionError("pending or rejected authorization is not routable")

    trusted_time = _parse_canonical_utc(resolved_at, "trusted resolved_at")
    if (
        _parse_canonical_utc(capability.issued_at, "trust issued_at") > trusted_time
        or _parse_canonical_utc(capability.expires_at, "trust expires_at")
        <= trusted_time
        or _parse_canonical_utc(
            capability.requester_auth_expires_at,
            "requester auth expires_at",
        )
        <= trusted_time
        or _parse_canonical_utc(
            capability.manifest_ownership_expires_at,
            "Manifest ownership expires_at",
        )
        <= trusted_time
        or (
            status == "approved"
            and _parse_canonical_utc(
                authorization["approval_expires_at"], "approval expires_at"
            )
            <= trusted_time
        )
    ):
        raise ResolutionError(
            "Intake trust, auth, Manifest, or approval proof is expired"
        )

    changes = _public_contract_changes(intent)
    if any(
        change["expected_source"] not in normalized_requested
        for change in changes.values()
    ):
        raise ResolutionError(
            "every expected contract source must be in requested_paths"
        )
    approval_subject = {
        key: value
        for key, value in intent.items()
        if key
        not in {"authorization", "approval_subject_sha256", "upgrade_intent_sha256"}
    }
    if intent["approval_subject_sha256"] != _domain_sha256(
        "dps.upgrade-intent/v2/approval-subject", approval_subject
    ):
        raise ResolutionError("approval subject digest mismatch")
    full_intent = {
        key: value for key, value in intent.items() if key != "upgrade_intent_sha256"
    }
    if intent["upgrade_intent_sha256"] != _domain_sha256(
        "dps.upgrade-intent/v2/full-intent", full_intent
    ):
        raise ResolutionError("full upgrade intent digest mismatch")


def _bound_file(root: Path, baseline: str, relative_path: str, order: int) -> Dict[str, Any]:
    path = _safe_file(root, relative_path)
    data = path.read_bytes()
    current_blob = _git_hash_object_bytes(root, data)
    baseline_blob = _git(root, ["rev-parse", baseline + ":" + relative_path], required=False)
    if not baseline_blob:
        source_state = "untracked"
    else:
        source_state = "tracked" if baseline_blob == current_blob else "modified"
    return {
        "path": relative_path,
        "order": order,
        "source_state": source_state,
        "git_blob": current_blob if _COMMIT.fullmatch(current_blob) else None,
        "sha256": _sha256(data),
    }


def _verify_bound_files(
    root: Path, collections: Iterable[Sequence[Mapping[str, Any]]]
) -> None:
    for collection in collections:
        for bound in collection:
            path = bound.get("path")
            if not isinstance(path, str):
                raise ResolutionError("bound file path is invalid")
            data = _safe_file(root, path).read_bytes()
            if (
                bound.get("sha256") != _sha256(data)
                or bound.get("git_blob") != _git_hash_object_bytes(root, data)
            ):
                raise ResolutionError(
                    "repository file changed during instruction binding: " + path
                )


def _bind_many(root: Path, baseline: str, paths: Iterable[str]) -> list[Dict[str, Any]]:
    return [
        _bound_file(root, baseline, path, order)
        for order, path in enumerate(sorted(set(paths)))
    ]


def _module_files(root: Path, record: ModuleRecord, directory: str) -> Set[str]:
    target = record.root / directory
    if target.is_symlink() or not target.is_dir():
        raise ResolutionError("module %s lacks safe %s directory" % (record.module_id, directory))
    files: Set[str] = set()
    for path in target.rglob("*"):
        if (
            any(part in _GENERATED_DIRECTORY_NAMES for part in path.parts)
            or path.suffix in {".pyc", ".pyo"}
        ):
            continue
        if path.is_symlink():
            raise ResolutionError("symlink in module %s: %s" % (directory, path))
        if path.is_file():
            files.add(path.relative_to(root).as_posix())
    if not files:
        raise ResolutionError("module %s has no %s instructions" % (record.module_id, directory))
    return files


class InstructionResolver:
    """Resolve and revalidate immutable instruction receipts."""

    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        *,
        trust_authority: UpgradeIntentTrustAuthority,
        receipt_capability_quota: int = _MAX_ACTIVE_TRUST_RECORDS,
    ) -> None:
        if type(trust_authority) is not UpgradeIntentTrustAuthority:
            raise ResolutionError(
                "InstructionResolver requires the fixed composition-root trust authority"
            )
        self.root = Path(repository_root).resolve(strict=True)
        if not (self.root / ".git").exists():
            raise ResolutionError("repository root is not a Git worktree")
        self.__trust_authority = trust_authority
        self.__receipt_authority = InstructionReceiptTrustAuthority(
            trust_authority,
            max_active_capabilities=receipt_capability_quota,
        )

    def resolve(
        self,
        verified_intent: VerifiedUpgradeIntentV2,
        *,
        agent_identity: str,
        agent_role: str,
    ) -> VerifiedInstructionReceiptV2:
        self.__trust_authority.assert_issued(verified_intent)
        intent = _decode_canonical_upgrade_intent(verified_intent.raw_bytes)
        resolved_at = verified_intent.verified_at
        _validate_intent(
            intent, resolved_at=resolved_at, capability=verified_intent
        )
        if not isinstance(agent_identity, str) or not _ACTOR_ID.fullmatch(agent_identity):
            raise ResolutionError("agent identity is not canonical")
        if agent_role not in _ROLES:
            raise ResolutionError("agent role is not allowed")
        _canonical_utc(resolved_at, "resolved_at")
        baseline_input = intent.get("baseline_commit")
        if not isinstance(baseline_input, str) or not _COMMIT.fullmatch(baseline_input):
            raise ResolutionError("intent baseline_commit is invalid")
        baseline = _git(self.root, ["rev-parse", "--verify", baseline_input + "^{commit}"])
        if not _COMMIT.fullmatch(baseline):
            raise ResolutionError("baseline is not a commit")

        records = load_module_records(self.root)
        owners, consumers = _contract_index(records)
        intent_owner = owners.get(("upgrade.intent", 2))
        if (
            intent_owner is None
            or intent_owner.declaring_module != "factory-upgrade-intake"
            or intent_owner.mode != "active"
        ):
            raise ResolutionError("upgrade.intent/v2 has no active trusted owner")
        receipt_owner = owners.get(("instruction.receipt", 2))
        if (
            receipt_owner is None
            or receipt_owner.declaring_module != "factory-instruction-resolver"
            or receipt_owner.mode != "active"
        ):
            raise ResolutionError("instruction.receipt/v2 has no active trusted owner")
        resolver_record = records.get("factory-instruction-resolver")
        if (
            resolver_record is None
            or ("upgrade.intent", 2) not in resolver_record.consumed
        ):
            raise ResolutionError("resolver does not consume exact upgrade.intent/v2")
        targets = intent.get("target_modules")
        if not isinstance(targets, list) or not targets or any(
            not isinstance(value, str) or value not in records for value in targets
        ) or len(set(targets)) != len(targets):
            raise ResolutionError("target_modules must be unique registered modules")
        requested = intent.get("requested_paths")
        if not isinstance(requested, list) or not requested:
            raise ResolutionError("requested_paths must be non-empty")
        normalized_requested = [
            _safe_relative(value, reject_hidden=True) for value in requested
        ]
        if len(set(normalized_requested)) != len(normalized_requested):
            raise ResolutionError("requested_paths must be unique")
        requested_owners: Set[str] = set()
        for value in normalized_requested:
            path_owners = _owners_for_path(value, records)
            if len(path_owners) != 1:
                raise ResolutionError("requested path must have one owner: " + value)
            requested_owners.update(path_owners)
        if requested_owners != set(targets):
            raise ResolutionError("requested paths and target modules do not match")

        declared_contracts = _public_contract_changes(intent)
        baseline_provided = {
            module_id: _baseline_contracts(
                self.root, baseline, record, "provided"
            )
            for module_id, record in records.items()
        }
        baseline_owners, baseline_consumers, baseline_family_owners = (
            _baseline_contract_indexes(self.root, baseline, records)
        )
        verified_baseline_contract_facts = []
        for key, change in declared_contracts.items():
            expected_owner = change["expected_owner_module"]
            expected_source = change["expected_source"]
            if expected_owner not in targets:
                raise ResolutionError(
                    "public contract expected owner must be a target: %s/v%d" % key
                )
            source_owners = _owners_for_path(expected_source, records)
            if source_owners != {expected_owner}:
                raise ResolutionError(
                    "public contract expected source has wrong ownership: %s/v%d" % key
                )
            baseline_owner = baseline_owners.get(key)
            previous = baseline_owner[1] if baseline_owner is not None else None
            actual_baseline_state = "present" if previous is not None else "absent"
            if change["expected_baseline_state"] != actual_baseline_state:
                raise ResolutionError(
                    "public contract expected baseline state is false: %s/v%d" % key
                )
            previous_source = previous.get("source") if previous is not None else None
            previous_digest = None
            if isinstance(previous_source, str):
                previous_digest = _baseline_file_sha256(
                    self.root, baseline, previous_source
                )
                if previous_digest is None:
                    raise ResolutionError(
                        "baseline contract source is missing: %s/v%d" % key
                    )
            if change["change_kind"] in {
                "add-major",
                "introduce-quarantined-major",
            }:
                if previous is not None:
                    raise ResolutionError(
                        "new contract major already existed at baseline: %s/v%d" % key
                    )
                family_owner = baseline_family_owners.get(key[0])
                if family_owner is not None and family_owner != expected_owner:
                    raise ResolutionError(
                        "new major expected owner conflicts with baseline family owner: %s/v%d"
                        % key
                    )
                if change["change_kind"] == "introduce-quarantined-major":
                    current_owner = owners.get(key)
                    if current_owner is not None and (
                        current_owner.declaring_module != expected_owner
                        or current_owner.owner_module != expected_owner
                        or current_owner.source != expected_source
                        or current_owner.mode != "quarantine-only"
                        or current_owner.status != "deprecated"
                    ):
                        raise ResolutionError(
                            "introduced quarantined major became an active or mismatched producer: %s/v%d"
                            % key
                        )
                    unsafe_consumers = sorted(
                        module_id
                        for module_id, record in records.items()
                        for declaration in (record.consumed.get(key),)
                        if declaration is not None
                        and (
                            declaration.mode not in {"quarantine-only", "retired"}
                            or (
                                declaration.mode == "quarantine-only"
                                and declaration.status != "deprecated"
                            )
                        )
                    )
                    if unsafe_consumers:
                        raise ResolutionError(
                            "introduced quarantined major has active or mismatched consumers: %s/v%d: %s"
                            % (key[0], key[1], ", ".join(unsafe_consumers))
                        )
                    if _runtime_communication_edges(records, key):
                        raise ResolutionError(
                            "introduced quarantined major cannot have runtime communication: %s/v%d"
                            % key
                        )
            elif change["change_kind"] == "additive-schema":
                if (
                    previous is None
                    or baseline_owner is None
                    or baseline_owner[0] != expected_owner
                    or previous.get("mode") != "active"
                    or previous_source != expected_source
                    or change["expected_previous_mode"] != previous.get("mode")
                    or change["expected_previous_source_sha256"] != previous_digest
                ):
                    raise ResolutionError(
                        "additive-schema baseline facts mismatch: %s/v%d" % key
                    )
            else:
                if (
                    previous is None
                    or baseline_owner is None
                    or baseline_owner[0] != expected_owner
                    or previous_source != expected_source
                    or change["expected_previous_mode"] != previous.get("mode")
                    or change["expected_previous_source_sha256"] != previous_digest
                ):
                    raise ResolutionError(
                        "mode-transition baseline facts mismatch: %s/v%d" % key
                    )
            verified_baseline_contract_facts.append(
                {
                    "contract_id": key[0],
                    "major": key[1],
                    "baseline_commit": baseline,
                    "presence": "present" if previous is not None else "absent",
                    "owner_module": baseline_owner[0] if baseline_owner is not None else None,
                    "source": previous_source,
                    "source_sha256": previous_digest,
                    "mode": previous.get("mode") if previous is not None else None,
                    "status": previous.get("status") if previous is not None else None,
                    "family_owner_module": baseline_family_owners.get(key[0]),
                    "consumer_modules": sorted(baseline_consumers.get(key, set())),
                }
            )
        verified_baseline_contract_facts.sort(
            key=lambda value: (value["contract_id"], value["major"])
        )

        # The diff is a trusted Git-derived fact.  A caller-supplied subset
        # could omit a changed contract, suppress its consumers, or keep a
        # stale receipt looking BOUND, so the production API has no override.
        current_diff = _changed_paths(self.root, baseline)
        impacted = set(targets)
        governance_change = any(
            _is_global_engineering_path(value)
            for value in current_diff
        )
        if governance_change:
            impacted.update(records)
        for value in current_diff:
            if _is_legacy_tombstone(self.root, baseline, value):
                continue
            if _is_global_engineering_path(value):
                continue
            path = self.root / value
            if path.is_symlink():
                raise ResolutionError("changed symlink is forbidden: " + value)
            path_owners = _owners_for_path(value, records)
            if len(path_owners) != 1:
                raise ResolutionError("changed path must have exactly one owner: " + value)
            impacted.update(path_owners)

        changed_contracts: Set[Tuple[str, int]] = set()
        for key, declaration in owners.items():
            owner_manifest = records[declaration.declaring_module].manifest_path
            owner_manifest_path = owner_manifest.relative_to(self.root).as_posix()
            baseline_declaration = baseline_provided[
                declaration.declaring_module
            ].get(key)
            current_declaration = {
                "contractId": declaration.contract_id,
                "major": declaration.major,
                "source": declaration.source,
                "status": declaration.status,
                "mode": declaration.mode,
                "ownerModule": declaration.owner_module,
            }
            if declaration.source in current_diff or (
                owner_manifest_path in current_diff
                and baseline_declaration != current_declaration
            ):
                changed_contracts.add(key)
        for module_id, baseline_declarations in baseline_provided.items():
            current_keys = set(records[module_id].provided)
            removed = set(baseline_declarations).difference(current_keys)
            if removed:
                raise ResolutionError(
                    "provided contract majors cannot be removed; declare retired: "
                    + ", ".join("%s/v%d" % key for key in sorted(removed))
                )
        undeclared = changed_contracts.difference(declared_contracts)
        if undeclared:
            raise ResolutionError(
                "changed public contracts were not declared: "
                + ", ".join(
                    "%s/v%d" % key for key in sorted(undeclared)
                )
            )
        for key, change in declared_contracts.items():
            impacted.add(change["expected_owner_module"])
            current_owner = owners.get(key)
            if current_owner is not None:
                impacted.add(current_owner.declaring_module)
            impacted.update(consumers.get(key, set()))
            impacted.update(baseline_consumers.get(key, set()))

        instruction_paths = ["AGENTS.md"] + [
            records[module_id].agents_path.relative_to(self.root).as_posix()
            for module_id in sorted(impacted)
        ]
        manifest_paths = [
            records[module_id].manifest_path.relative_to(self.root).as_posix()
            for module_id in sorted(impacted)
        ]
        contract_paths: Set[str] = set()
        test_paths: Set[str] = set()
        operation_paths: Set[str] = set()
        for module_id in sorted(impacted):
            record = records[module_id]
            contract_paths.update(item.source for item in record.provided.values())
            contract_paths.update(item.source for item in record.consumed.values())
            test_paths.update(_module_files(self.root, record, "tests"))
            operation_paths.update(_module_files(self.root, record, "operations"))
        contract_declarations = sorted(
            (
                declaration.receipt_value()
                for module_id in sorted(impacted)
                for declaration in (
                    *records[module_id].provided.values(),
                    *records[module_id].consumed.values(),
                )
            ),
            key=lambda value: (
                value["contract_id"],
                value["major"],
                value["declaring_module"],
                value["declaration_kind"],
            ),
        )
        candidate_trust_present = (self.root / _CANDIDATE_TRUST_PATHS[0]).is_file()
        candidate_trust_paths = _CANDIDATE_TRUST_PATHS if candidate_trust_present else ()
        governance_paths = set(_CORE_GOVERNANCE_PATHS)
        governance_paths.update(candidate_trust_paths)
        governance_paths.update(
            value
            for value in current_diff
            if _is_global_engineering_path(value) and (self.root / value).is_file()
        )
        for path in governance_paths:
            _safe_file(self.root, path)

        diff_material = _diff_material(self.root, current_diff)
        bound_contract_change_expectations = [
            dict(declared_contracts[key]) for key in sorted(declared_contracts)
        ]
        baseline_facts_material = {
            "baseline_commit": baseline,
            "verified_baseline_contract_facts": verified_baseline_contract_facts,
        }
        receipt_without_id: Dict[str, Any] = {
            "schema_version": "dps.instruction-receipt/v2",
            "contract_id": "instruction.receipt/v2",
            "producer_module": "factory-instruction-resolver",
            "soul_id": intent.get("soul_id"),
            "device_binding_id": intent.get("device_binding_id"),
            "platform_account_id": intent.get("platform_account_id"),
            "trace_id": intent["trace_id"],
            "idempotency_key": intent["idempotency_key"],
            "occurred_at": resolved_at,
            "privacy_class": "internal",
            "intent_id": intent["intent_id"],
            "auth_context_id": intent["auth_context_id"],
            "agent_identity": agent_identity,
            "agent_role": agent_role,
            "baseline_commit": baseline,
            "resolved_at": resolved_at,
            "scope": sorted(impacted),
            "source_intent_contract": {
                "contract_id": "upgrade.intent",
                "major": 2,
                "mode": "active",
            },
            "source_intake_payload_sha256": verified_intent.payload_sha256,
            "source_intake_peer_module": verified_intent.peer_module,
            "source_intake_audience": verified_intent.audience,
            "source_intake_trust_receipt_id": verified_intent.trust_receipt_id,
            "source_intake_trust_nonce": verified_intent.trust_nonce,
            "source_intake_trust_issued_at": verified_intent.issued_at,
            "source_intake_verified_at": verified_intent.verified_at,
            "source_intake_trust_expires_at": verified_intent.expires_at,
            "source_requester_auth_expires_at": verified_intent.requester_auth_expires_at,
            "source_manifest_ownership_expires_at": verified_intent.manifest_ownership_expires_at,
            "source_approval_expires_at": verified_intent.approval_expires_at,
            "source_upgrade_intent_sha256": intent["upgrade_intent_sha256"],
            "source_approval_subject_sha256": intent[
                "approval_subject_sha256"
            ],
            "source_requester_auth_context_sha256": intent[
                "requester_auth_context_sha256"
            ],
            "source_requester_auth_receipt_id": intent[
                "requester_auth_receipt_id"
            ],
            "source_requester_auth_nonce": intent["requester_auth_nonce"],
            "source_manifest_ownership_sha256": intent[
                "manifest_ownership_sha256"
            ],
            "source_manifest_ownership_receipt_id": intent[
                "manifest_ownership_receipt_id"
            ],
            "requested_risk_tier": intent["requested_risk_tier"],
            "requested_stage": intent["requested_stage"],
            "requested_target_modules": list(intent["target_modules"]),
            "authorized_write_paths": list(intent["requested_paths"]),
            "source_authorization_status": intent["authorization"]["status"],
            "source_contract_change_claims_status": "UNVERIFIED_EXPECTATIONS",
            "source_contract_change_claims_sha256": intent[
                "public_contract_changes_sha256"
            ],
            "baseline_verification_required": True,
            "bound_contract_change_expectations": bound_contract_change_expectations,
            "verified_baseline_contract_facts": verified_baseline_contract_facts,
            "verified_baseline_contract_facts_sha256": _sha256(
                _canonical_bytes(baseline_facts_material)
            ),
            "changeset_contract_verification_required": True,
            "contract_declarations": contract_declarations,
            "instructions": [
                _bound_file(self.root, baseline, path, order)
                for order, path in enumerate(instruction_paths)
            ],
            "manifests": _bind_many(self.root, baseline, manifest_paths),
            "contracts": _bind_many(self.root, baseline, contract_paths),
            "governance": _bind_many(self.root, baseline, governance_paths),
            "tests": _bind_many(self.root, baseline, test_paths),
            "operations": _bind_many(self.root, baseline, operation_paths),
            "diff_fingerprint": _sha256(_canonical_bytes(diff_material)),
            "status": "BOUND",
            "invalidated_reason": None,
        }
        bound_collections = tuple(
            receipt_without_id[field]
            for field in (
                "instructions",
                "manifests",
                "contracts",
                "governance",
                "tests",
                "operations",
            )
        )
        _verify_bound_files(self.root, bound_collections)
        if (
            _changed_paths(self.root, baseline) != current_diff
            or _diff_material(self.root, current_diff) != diff_material
        ):
            raise ResolutionError("repository diff changed during instruction binding")
        # A second pass narrows the check/use window and catches writes that
        # began during the first verification.  The Worktree Manager remains
        # responsible for the single-writer freeze around this read-only call.
        _verify_bound_files(self.root, bound_collections)
        if (
            _changed_paths(self.root, baseline) != current_diff
            or _diff_material(self.root, current_diff) != diff_material
        ):
            raise ResolutionError("repository diff changed during instruction binding")
        receipt = dict(receipt_without_id)
        receipt["receipt_id"] = "instruction:" + _sha256(
            _canonical_bytes(receipt_without_id)
        )[:32]
        return self.__receipt_authority.issue_bound(receipt)

    def validate(
        self,
        receipt: VerifiedInstructionReceiptV2,
        verified_intent: VerifiedUpgradeIntentV2,
    ) -> Tuple[bool, str, Optional[VerifiedInstructionReceiptV2]]:
        if type(receipt) is not VerifiedInstructionReceiptV2:
            return False, "an issued instruction receipt capability is required", None
        try:
            self.__receipt_authority.assert_issued(receipt)
            if receipt.status != "BOUND":
                return False, "only an issued BOUND v2 receipt can be validated", None
            original = receipt.canonical_receipt()
        except ResolutionError as exc:
            return False, str(exc), None
        try:
            current_capability = self.resolve(
                verified_intent,
                agent_identity=original["agent_identity"],
                agent_role=original["agent_role"],
            )
        except ResolutionError as exc:
            # No replacement receipt exists when current truth cannot be
            # recomputed.  Returning a fabricated STALE object here would mix
            # trusted old bindings with an unverified failure path.
            return False, str(exc), None
        try:
            self.__receipt_authority.assert_issued(current_capability)
            canonical_current = current_capability.canonical_receipt()
        except ResolutionError as exc:
            return False, "current resolver output is invalid: " + str(exc), None
        if _canonical_bytes(canonical_current) != _canonical_bytes(original):
            try:
                stale = self.__receipt_authority.issue_stale(
                    receipt, "bound content or diff scope changed"
                )
            except ResolutionError as exc:
                return False, "cannot emit a valid STALE receipt: " + str(exc), None
            return False, "bound content or diff scope changed", stale
        return True, "BOUND", current_capability
