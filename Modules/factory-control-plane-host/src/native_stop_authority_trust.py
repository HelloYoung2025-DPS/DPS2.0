"""Strict Release native-stop authority trust boundary for the Factory Host.

The public Release receipt is data, not authority.  A fixed composition root
pins the receipt schema, the deployed provider identity, the two verifier
ports, and the trusted clock.  Only that authority can seal a verified
capability.  No private key, activation token, secret, or generic callback is
accepted by this module.
"""

from __future__ import annotations

import abc
import copy
import datetime as dt
import hashlib
import json
import re
import weakref
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Sequence

from jsonschema import Draft202012Validator, FormatChecker


CONTRACT_ID = "release.bom.native.stop.authority.trust/v1"
CONTRACT_FAMILY = "release.bom.native.stop.authority.trust"
PRODUCER_MODULE = "factory-release-controller"
SCHEMA_RELATIVE_PATH = (
    "Modules/factory-release-controller/contracts/provided/"
    "release.bom.native.stop.authority.trust.v1.schema.json"
)
PROVIDER_ATTESTATION_ISSUER = "dps.release-native-stop-trust-provider"
PROVIDER_ATTESTATION_AUDIENCE = "dps.factory-control-plane-host"
MAX_RECEIPT_BYTES = 4_194_304
MAX_JSON_DEPTH = 64
MAX_JSON_NODES = 65_536
MAX_PROVIDER_ATTESTATION_WINDOW = dt.timedelta(minutes=15)

_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_OPAQUE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._:-]{7,127}$")
_RECEIPT_ID = re.compile(r"^native-stop-trust-[0-9a-f]{32}$")
_WORKFLOW_ID = re.compile(r"^upgrade:[A-Za-z0-9][A-Za-z0-9._-]{7,119}$")
_CANONICAL_RUNTIME_UTC = re.compile(
    r"^(?:20(?:2[0-9]|[3-9][0-9])|2[1-9][0-9]{2}|[3-9][0-9]{3})-"
    r"(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T"
    r"(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]\.[0-9]{7}Z$"
)

_NATIVE_STOP_AUTHORITY_HASH_DOMAIN = "dps.native-stop-worker-authority-sha256/v2"
_NATIVE_STOP_AUTHORITIES_HASH_DOMAIN = "dps.native-stop-authorities-sha256/v1"
_DEVICE_ROUTE_AUTHORITY_HASH_DOMAIN = "dps.device-route-assignment-authority-sha256/v1"
_DEVICE_ROUTE_AUTHORITIES_HASH_DOMAIN = "dps.device-route-assignment-authorities-sha256/v1"
_NATIVE_STOP_CHALLENGE_AUTHORITY_HASH_DOMAIN = "dps.native-stop-challenge-authority-sha256/v1"
_NATIVE_STOP_CHALLENGE_AUTHORITIES_HASH_DOMAIN = "dps.native-stop-challenge-authorities-sha256/v1"
_AUTHORITY_SETS_HASH_DOMAIN = "dps.release-bom-authority-sets-sha256/v1"
_RELEASE_SIGNATURE_DOMAIN = "dps-release-bom-native.stop.authority.trust/v1"
_PROVIDER_ATTESTATION_DOMAIN = (
    "dps.factory-control-plane-host/native-stop-trust-provider-attestation/v1"
)

_CAPABILITY_SENTINEL = object()
_AUTHORITY_SENTINEL = object()


class NativeStopAuthorityTrustError(RuntimeError):
    """The trust receipt, provider attestation, or sealed capability failed."""


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        allow_nan=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _length_framed(values: Sequence[str]) -> bytes:
    wire = bytearray()
    for value in values:
        encoded = value.encode("utf-8", errors="strict")
        if len(encoded) > 0xFFFFFFFF:
            raise NativeStopAuthorityTrustError("trust field exceeds the wire limit")
        wire.extend(len(encoded).to_bytes(4, "big"))
        wire.extend(encoded)
    return bytes(wire)


def _strict_json_loads(raw: bytes) -> Mapping[str, Any]:
    if type(raw) is not bytes or not raw or len(raw) > MAX_RECEIPT_BYTES:
        raise NativeStopAuthorityTrustError("trust receipt byte boundary is invalid")
    try:
        text = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise NativeStopAuthorityTrustError("trust receipt is not UTF-8") from exc

    def reject_constant(value: str) -> None:
        raise NativeStopAuthorityTrustError("trust receipt contains a non-finite number: " + value)

    def reject_float(value: str) -> None:
        raise NativeStopAuthorityTrustError("trust receipt contains a floating-point number: " + value)

    def exact_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise NativeStopAuthorityTrustError("trust receipt contains a duplicate JSON member")
            result[key] = value
        return result

    try:
        value = json.loads(
            text,
            object_pairs_hook=exact_object,
            parse_constant=reject_constant,
            parse_float=reject_float,
        )
    except (json.JSONDecodeError, UnicodeError, ValueError) as exc:
        if isinstance(exc, NativeStopAuthorityTrustError):
            raise
        raise NativeStopAuthorityTrustError("trust receipt is not strict JSON") from exc
    if not isinstance(value, Mapping):
        raise NativeStopAuthorityTrustError("trust receipt root must be an object")
    if _canonical_bytes(value) != raw:
        raise NativeStopAuthorityTrustError("trust receipt bytes are not canonical JSON")
    _validate_json_resource_bounds(value)
    _reject_secret_material(value)
    return copy.deepcopy(dict(value))


def _validate_json_resource_bounds(value: Any) -> None:
    nodes = 0
    stack: list[tuple[Any, int]] = [(value, 1)]
    while stack:
        current, depth = stack.pop()
        nodes += 1
        if nodes > MAX_JSON_NODES or depth > MAX_JSON_DEPTH:
            raise NativeStopAuthorityTrustError("trust receipt exceeds JSON resource bounds")
        if isinstance(current, Mapping):
            stack.extend((item, depth + 1) for item in current.values())
        elif isinstance(current, list):
            stack.extend((item, depth + 1) for item in current)


def _reject_secret_material(value: Any) -> None:
    forbidden_names = {
        "activation_token", "raw_activation_token", "private_key", "private_key_pem",
        "secret", "client_secret", "password", "credential", "api_key",
    }
    stack = [value]
    while stack:
        current = stack.pop()
        if isinstance(current, Mapping):
            for key, item in current.items():
                if not isinstance(key, str) or key.casefold() in forbidden_names:
                    raise NativeStopAuthorityTrustError("trust receipt contains forbidden secret material")
                stack.append(item)
        elif isinstance(current, list):
            stack.extend(current)
        elif isinstance(current, str) and "PRIVATE KEY" in current.upper():
            raise NativeStopAuthorityTrustError("trust receipt contains private key material")


def _runtime_ticks(value: str, name: str) -> int:
    if not isinstance(value, str) or _CANONICAL_RUNTIME_UTC.fullmatch(value) is None:
        raise NativeStopAuthorityTrustError(name + " is not canonical 100ns UTC")
    try:
        second = dt.datetime.strptime(value[:19], "%Y-%m-%dT%H:%M:%S").replace(
            tzinfo=dt.timezone.utc,
        )
    except ValueError as exc:
        raise NativeStopAuthorityTrustError(name + " is not a real UTC instant") from exc
    epoch = dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc)
    delta = second - epoch
    seconds = delta.days * 86_400 + delta.seconds
    return seconds * 10_000_000 + int(value[20:27])


def _clock_ticks(value: dt.datetime) -> int:
    if not isinstance(value, dt.datetime) or value.tzinfo is None:
        raise NativeStopAuthorityTrustError("trusted clock returned a naive or invalid datetime")
    current = value.astimezone(dt.timezone.utc)
    epoch = dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc)
    delta = current - epoch
    seconds = delta.days * 86_400 + delta.seconds
    return seconds * 10_000_000 + current.microsecond * 10


def _individual_native_hash(authority: Mapping[str, Any]) -> str:
    return _sha256_bytes(_length_framed((
        _NATIVE_STOP_AUTHORITY_HASH_DOMAIN,
        str(authority["authority_id"]), str(authority["producer_module"]),
        str(authority["worker_module_id"]), str(authority["worker_artifact_id"]),
        str(authority["worker_artifact_sha256"]), str(authority["worker_version"]),
        str(authority["worker_slot"]), str(authority["worker_instance_id"]),
        str(authority["worker_generation"]), str(authority["key_id"]),
        str(authority["p256_spki_sha256"]), str(authority["signature_algorithm"]),
        str(authority["signature_format"]), str(authority["auth_scope"]),
        str(authority["native_stop_contract_id"]), str(authority["policy_id"]),
        str(authority["release_bom_generation"]), str(authority["activation_token_sha256"]),
        str(authority["rotation_epoch"]), str(authority["valid_from"]),
        str(authority["valid_until"]), "true" if authority["revoked"] else "false",
    )))


def _native_set_hash(authorities: Sequence[Mapping[str, Any]]) -> str:
    return _sha256_bytes(_length_framed((
        _NATIVE_STOP_AUTHORITIES_HASH_DOMAIN,
        str(len(authorities)),
        *(str(item["worker_authority_sha256"]) for item in authorities),
    )))


def _individual_route_hash(authority: Mapping[str, Any]) -> str:
    return _sha256_bytes(_length_framed((
        _DEVICE_ROUTE_AUTHORITY_HASH_DOMAIN,
        str(authority["route_authority_id"]), str(authority["producer_module"]),
        str(authority["supervisor_module_id"]), str(authority["supervisor_artifact_id"]),
        str(authority["supervisor_artifact_sha256"]), str(authority["supervisor_version"]),
        str(authority["supervisor_instance_id"]), str(authority["supervisor_generation"]),
        str(authority["route_signer_key_id"]), str(authority["route_signer_p256_spki_sha256"]),
        str(authority["signature_algorithm"]), str(authority["signature_format"]),
        str(authority["auth_scope"]), str(authority["policy_id"]),
        str(authority["release_bom_generation"]), str(authority["activation_token_sha256"]),
        str(authority["rotation_epoch"]), str(authority["valid_from"]),
        str(authority["valid_until"]), "true" if authority["revoked"] else "false",
    )))


def _route_set_hash(authorities: Sequence[Mapping[str, Any]]) -> str:
    return _sha256_bytes(_length_framed((
        _DEVICE_ROUTE_AUTHORITIES_HASH_DOMAIN,
        str(len(authorities)),
        *(str(item["route_authority_sha256"]) for item in authorities),
    )))


def _individual_challenge_hash(authority: Mapping[str, Any]) -> str:
    return _sha256_bytes(_length_framed((
        _NATIVE_STOP_CHALLENGE_AUTHORITY_HASH_DOMAIN,
        str(authority["authority_id"]), str(authority["producer_module"]),
        str(authority["policy_module_id"]), str(authority["policy_artifact_id"]),
        str(authority["policy_artifact_sha256"]), str(authority["policy_version"]),
        str(authority["policy_instance_id"]), str(authority["policy_generation"]),
        str(authority["key_id"]), str(authority["p256_spki_sha256"]),
        str(authority["signature_algorithm"]), str(authority["signature_format"]),
        str(authority["auth_scope"]), str(authority["native_stop_challenge_contract_id"]),
        str(authority["policy_id"]), str(authority["release_bom_generation"]),
        str(authority["activation_token_sha256"]), str(authority["rotation_epoch"]),
        str(authority["valid_from"]), str(authority["valid_until"]),
        "true" if authority["revoked"] else "false",
    )))


def _challenge_set_hash(authorities: Sequence[Mapping[str, Any]]) -> str:
    return _sha256_bytes(_length_framed((
        _NATIVE_STOP_CHALLENGE_AUTHORITIES_HASH_DOMAIN,
        str(len(authorities)),
        *(str(item["challenge_authority_sha256"]) for item in authorities),
    )))


def _authority_sets_hash(native_hash: str, route_hash: str, challenge_hash: str) -> str:
    return _sha256_bytes(_length_framed((
        _AUTHORITY_SETS_HASH_DOMAIN, native_hash, route_hash, challenge_hash,
    )))


def release_receipt_signing_bytes(payload: Mapping[str, Any]) -> bytes:
    """Return the byte-exact signing profile frozen by the Release owner."""
    expected = {
        "schema_version", "contract_id", "producer_module", "soul_id",
        "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
        "occurred_at", "privacy_class", "receipt_id", "release_bom_id",
        "release_bom_sha256", "integration_commit", "release_bom_generation",
        "activation_token_sha256", "trust_policy_id", "native_stop_authorities_sha256",
        "device_route_assignment_authorities_sha256", "native_stop_challenge_authorities_sha256",
        "authority_sets_sha256", "native_stop_authorities",
        "device_route_assignment_authorities", "native_stop_challenge_authorities",
    }
    if not isinstance(payload, Mapping) or set(payload) != expected:
        raise NativeStopAuthorityTrustError("Release trust signing payload is not exact")
    values = (
        _RELEASE_SIGNATURE_DOMAIN,
        str(payload["schema_version"]), str(payload["contract_id"]),
        str(payload["producer_module"]), "" if payload["soul_id"] is None else str(payload["soul_id"]),
        "" if payload["device_binding_id"] is None else str(payload["device_binding_id"]),
        "" if payload["platform_account_id"] is None else str(payload["platform_account_id"]),
        str(payload["trace_id"]), str(payload["idempotency_key"]),
        str(payload["occurred_at"]), str(payload["privacy_class"]),
        str(payload["receipt_id"]), str(payload["release_bom_id"]),
        str(payload["release_bom_sha256"]), str(payload["integration_commit"]),
        str(payload["release_bom_generation"]), str(payload["activation_token_sha256"]),
        str(payload["trust_policy_id"]), str(payload["native_stop_authorities_sha256"]),
        str(payload["device_route_assignment_authorities_sha256"]),
        str(payload["native_stop_challenge_authorities_sha256"]),
        str(payload["authority_sets_sha256"]),
    )
    return _length_framed(values)


@dataclass(frozen=True, slots=True)
class NativeStopTrustProviderAttestation:
    attestation_id: str
    provider_identity: str
    issuer: str
    audience: str
    workflow_id: str
    request_sha256: str
    external_context_ref: str | None
    receipt_id: str
    receipt_sha256: str
    release_bom_sha256: str
    release_bom_generation: int
    issued_at: str
    expires_at: str
    revoked: bool
    nonce: str
    algorithm: str
    key_id: str
    signature: str

    def public_record(self) -> dict[str, Any]:
        return {
            "attestation_id": self.attestation_id,
            "provider_identity": self.provider_identity,
            "issuer": self.issuer,
            "audience": self.audience,
            "workflow_id": self.workflow_id,
            "request_sha256": self.request_sha256,
            "external_context_ref": self.external_context_ref,
            "receipt_id": self.receipt_id,
            "receipt_sha256": self.receipt_sha256,
            "release_bom_sha256": self.release_bom_sha256,
            "release_bom_generation": self.release_bom_generation,
            "issued_at": self.issued_at,
            "expires_at": self.expires_at,
            "revoked": self.revoked,
            "nonce": self.nonce,
            "algorithm": self.algorithm,
            "key_id": self.key_id,
            "signature": self.signature,
        }

    @classmethod
    def from_public_record(cls, value: Mapping[str, Any]) -> "NativeStopTrustProviderAttestation":
        expected = {
            "attestation_id", "provider_identity", "issuer", "audience", "workflow_id",
            "request_sha256", "external_context_ref", "receipt_id",
            "receipt_sha256", "release_bom_sha256", "release_bom_generation",
            "issued_at", "expires_at", "revoked", "nonce", "algorithm", "key_id",
            "signature",
        }
        if not isinstance(value, Mapping) or set(value) != expected:
            raise NativeStopAuthorityTrustError("provider attestation record is not exact")
        if isinstance(value.get("release_bom_generation"), bool):
            raise NativeStopAuthorityTrustError("provider attestation generation is invalid")
        return cls(**copy.deepcopy(dict(value)))


def provider_attestation_signing_bytes(attestation: NativeStopTrustProviderAttestation) -> bytes:
    if type(attestation) is not NativeStopTrustProviderAttestation:
        raise NativeStopAuthorityTrustError("provider attestation type is not fixed")
    return _length_framed((
        _PROVIDER_ATTESTATION_DOMAIN,
        attestation.attestation_id,
        attestation.provider_identity,
        attestation.issuer,
        attestation.audience,
        attestation.workflow_id,
        attestation.request_sha256,
        "" if attestation.external_context_ref is None else attestation.external_context_ref,
        attestation.receipt_id,
        attestation.receipt_sha256,
        attestation.release_bom_sha256,
        str(attestation.release_bom_generation),
        attestation.issued_at,
        attestation.expires_at,
        "true" if attestation.revoked else "false",
        attestation.nonce,
        attestation.algorithm,
        attestation.key_id,
    ))


@dataclass(frozen=True, slots=True)
class NativeStopTrustEnvelope:
    canonical_receipt_bytes: bytes
    provider_attestation: NativeStopTrustProviderAttestation


@dataclass(frozen=True, slots=True)
class NativeStopTrustRequest:
    workflow_id: str
    request_sha256: str
    external_context_ref: str | None
    release_bom_sha256: str


class NativeStopAuthorityTrustProvider(abc.ABC):
    """Deployed provider port selected only by the process composition root."""

    @property
    @abc.abstractmethod
    def provider_identity(self) -> str:
        raise NotImplementedError

    @abc.abstractmethod
    def fetch(self, request: NativeStopTrustRequest) -> NativeStopTrustEnvelope | None:
        raise NotImplementedError


class NativeStopTrustCryptographicVerifier(abc.ABC):
    """Public-key verification port; it never receives private key material."""

    @abc.abstractmethod
    def verify(
        self,
        *,
        purpose: str,
        algorithm: str,
        key_id: str,
        signing_bytes: bytes,
        signature: str,
    ) -> bool:
        raise NotImplementedError


class NativeStopTrustClock(abc.ABC):
    @abc.abstractmethod
    def now(self) -> dt.datetime:
        raise NotImplementedError


class SystemNativeStopTrustClock(NativeStopTrustClock):
    def now(self) -> dt.datetime:
        return dt.datetime.now(dt.timezone.utc)


class VerifiedNativeStopAuthorityTrust:
    """Process-bound capability.  Its constructor is intentionally sealed."""

    __slots__ = (
        "_authority_nonce", "_receipt", "_raw", "_attestation", "_fact",
        "_fingerprint", "__weakref__",
    )

    def __init__(
        self,
        authority_nonce: object,
        receipt: Mapping[str, Any],
        raw: bytes,
        attestation: NativeStopTrustProviderAttestation,
        fact: Mapping[str, Any],
        fingerprint: str,
        sentinel: object | None = None,
    ) -> None:
        if sentinel is not _CAPABILITY_SENTINEL:
            raise TypeError("verified native-stop trust capabilities are composition-root sealed")
        self._authority_nonce = authority_nonce
        self._receipt = copy.deepcopy(dict(receipt))
        self._raw = bytes(raw)
        self._attestation = attestation
        self._fact = copy.deepcopy(dict(fact))
        self._fingerprint = fingerprint

    @property
    def receipt_id(self) -> str:
        return str(self._receipt["receipt_id"])

    @property
    def receipt_sha256(self) -> str:
        return str(self._fact["receipt_sha256"])

    @property
    def release_bom_generation(self) -> int:
        return int(self._receipt["release_bom_generation"])


class NativeStopAuthorityTrustAuthority:
    """Pinned authority that obtains, verifies, seals, and revalidates receipts."""

    __slots__ = (
        "_provider", "_provider_object_id", "_provider_identity", "_release_verifier",
        "_provider_verifier", "_clock", "_validator", "_schema_sha256",
        "_release_key_ids", "_provider_key_ids", "_nonce", "_registry",
    )

    def __init__(
        self,
        *,
        provider: NativeStopAuthorityTrustProvider,
        release_signature_verifier: NativeStopTrustCryptographicVerifier,
        provider_attestation_verifier: NativeStopTrustCryptographicVerifier,
        clock: NativeStopTrustClock,
        validator: Draft202012Validator,
        schema_sha256: str,
        release_key_ids: frozenset[str],
        provider_key_ids: frozenset[str],
        provider_identity: str,
        sentinel: object | None = None,
    ) -> None:
        if sentinel is not _AUTHORITY_SENTINEL:
            raise TypeError("native-stop trust authority must be created by the composition root")
        self._provider = provider
        self._provider_object_id = id(provider)
        self._provider_identity = provider_identity
        self._release_verifier = release_signature_verifier
        self._provider_verifier = provider_attestation_verifier
        self._clock = clock
        self._validator = validator
        self._schema_sha256 = schema_sha256
        self._release_key_ids = release_key_ids
        self._provider_key_ids = provider_key_ids
        self._nonce = object()
        self._registry: weakref.WeakKeyDictionary[VerifiedNativeStopAuthorityTrust, str] = (
            weakref.WeakKeyDictionary()
        )

    @property
    def schema_sha256(self) -> str:
        return self._schema_sha256

    def obtain(
        self,
        *,
        workflow_id: str,
        request_sha256: str,
        external_context_ref: str | None,
        release_bom_sha256: str,
    ) -> VerifiedNativeStopAuthorityTrust | None:
        request = self._validate_request(
            workflow_id, request_sha256, external_context_ref, release_bom_sha256,
        )
        self._assert_provider_unchanged()
        envelope = self._provider.fetch(request)
        self._assert_provider_unchanged()
        if envelope is None:
            return None
        return self._seal_verified(envelope, request)

    def revalidate_fact(
        self,
        fact: Mapping[str, Any],
        *,
        workflow_id: str,
        request_sha256: str,
        external_context_ref: str | None,
        release_bom_sha256: str,
    ) -> VerifiedNativeStopAuthorityTrust:
        request = self._validate_request(
            workflow_id, request_sha256, external_context_ref, release_bom_sha256,
        )
        expected = {
            "verified", "fact_id", "fact_kind", "contract_id", "receipt_id",
            "receipt_sha256", "canonical_receipt_utf8", "release_bom_id",
            "release_bom_sha256", "integration_commit", "release_bom_generation",
            "activation_token_sha256", "trust_policy_id",
            "native_stop_authorities_sha256", "device_route_assignment_authorities_sha256",
            "native_stop_challenge_authorities_sha256", "authority_sets_sha256",
            "provider_attestation",
        }
        if not isinstance(fact, Mapping) or set(fact) != expected or fact.get("verified") is not True:
            raise NativeStopAuthorityTrustError("durable native-stop trust fact is not exact")
        if fact.get("fact_kind") != "NATIVE_STOP_AUTHORITY_TRUST":
            raise NativeStopAuthorityTrustError("durable native-stop trust fact kind drifted")
        raw_text = fact.get("canonical_receipt_utf8")
        if not isinstance(raw_text, str):
            raise NativeStopAuthorityTrustError("durable native-stop trust bytes are missing")
        try:
            raw = raw_text.encode("utf-8", errors="strict")
        except UnicodeEncodeError as exc:
            raise NativeStopAuthorityTrustError("durable native-stop trust bytes are invalid") from exc
        attestation = NativeStopTrustProviderAttestation.from_public_record(
            fact.get("provider_attestation"),
        )
        capability = self._seal_verified(NativeStopTrustEnvelope(raw, attestation), request)
        rebuilt = self.to_durable_fact(capability)
        if rebuilt != dict(fact):
            raise NativeStopAuthorityTrustError("durable native-stop trust fact drifted")
        return capability

    def validate_capability(
        self,
        capability: VerifiedNativeStopAuthorityTrust,
        *,
        workflow_id: str,
        request_sha256: str,
        external_context_ref: str | None,
        release_bom_sha256: str,
    ) -> bool:
        if type(capability) is not VerifiedNativeStopAuthorityTrust:
            return False
        if capability._authority_nonce is not self._nonce:
            return False
        registered = self._registry.get(capability)
        if registered is None or registered != capability._fingerprint:
            return False
        try:
            request = self._validate_request(
                workflow_id, request_sha256, external_context_ref, release_bom_sha256,
            )
            material = self._verify_envelope(
                NativeStopTrustEnvelope(capability._raw, capability._attestation), request,
            )
        except NativeStopAuthorityTrustError:
            return False
        return material[2] == capability._fingerprint and material[1] == capability._fact

    def to_durable_fact(
        self, capability: VerifiedNativeStopAuthorityTrust,
    ) -> dict[str, Any]:
        if type(capability) is not VerifiedNativeStopAuthorityTrust:
            raise NativeStopAuthorityTrustError("native-stop trust capability type is invalid")
        registered = self._registry.get(capability)
        if (
            capability._authority_nonce is not self._nonce
            or registered is None
            or registered != capability._fingerprint
        ):
            raise NativeStopAuthorityTrustError("native-stop trust capability is foreign or unsealed")
        return copy.deepcopy(capability._fact)

    def _seal_verified(
        self, envelope: NativeStopTrustEnvelope, request: NativeStopTrustRequest,
    ) -> VerifiedNativeStopAuthorityTrust:
        receipt, fact, fingerprint = self._verify_envelope(envelope, request)
        capability = VerifiedNativeStopAuthorityTrust(
            self._nonce, receipt, envelope.canonical_receipt_bytes,
            envelope.provider_attestation, fact, fingerprint, _CAPABILITY_SENTINEL,
        )
        self._registry[capability] = fingerprint
        return capability

    def _verify_envelope(
        self, envelope: NativeStopTrustEnvelope, request: NativeStopTrustRequest,
    ) -> tuple[dict[str, Any], dict[str, Any], str]:
        self._assert_provider_unchanged()
        if type(envelope) is not NativeStopTrustEnvelope:
            raise NativeStopAuthorityTrustError("provider returned a non-fixed trust envelope")
        if type(envelope.provider_attestation) is not NativeStopTrustProviderAttestation:
            raise NativeStopAuthorityTrustError("provider returned a non-fixed attestation")
        raw = envelope.canonical_receipt_bytes
        receipt = dict(_strict_json_loads(raw))
        schema_error = next(self._validator.iter_errors(receipt), None)
        if schema_error is not None:
            raise NativeStopAuthorityTrustError("trust receipt fails the pinned Release JSON Schema")
        if (
            receipt.get("contract_id") != CONTRACT_ID
            or receipt.get("producer_module") != PRODUCER_MODULE
            or receipt.get("schema_version") != "1.0.0"
        ):
            raise NativeStopAuthorityTrustError("unknown, missing, or legacy trust contract major")
        receipt_sha = _sha256_bytes(raw)
        if receipt.get("release_bom_sha256") != request.release_bom_sha256:
            raise NativeStopAuthorityTrustError("trust receipt is not bound to the exact signed BOM")
        expected_idempotency = "idem_" + _sha256_bytes(_canonical_bytes({
            "contract_id": CONTRACT_ID,
            "receipt_id": receipt["receipt_id"],
            "release_bom_sha256": receipt["release_bom_sha256"],
        }))
        if receipt.get("idempotency_key") != expected_idempotency:
            raise NativeStopAuthorityTrustError("trust receipt idempotency identity drifted")
        self._validate_authority_sets(receipt)
        signature = receipt.get("signature")
        if not isinstance(signature, Mapping) or set(signature) != {"algorithm", "key_id", "value"}:
            raise NativeStopAuthorityTrustError("Release trust signature is not exact")
        if (
            signature.get("algorithm") != "rsa-pss-sha256"
            or signature.get("key_id") not in self._release_key_ids
            or self._release_verifier.verify(
                purpose="release-native-stop-trust-receipt",
                algorithm=str(signature.get("algorithm")),
                key_id=str(signature.get("key_id")),
                signing_bytes=release_receipt_signing_bytes({
                    key: value for key, value in receipt.items() if key != "signature"
                }),
                signature=str(signature.get("value")),
            ) is not True
        ):
            raise NativeStopAuthorityTrustError("Release trust receipt signature is untrusted")
        self._validate_provider_attestation(
            envelope.provider_attestation, receipt, receipt_sha, request,
        )
        fact = {
            "verified": True,
            "fact_id": str(receipt["receipt_id"]),
            "fact_kind": "NATIVE_STOP_AUTHORITY_TRUST",
            "contract_id": CONTRACT_ID,
            "receipt_id": str(receipt["receipt_id"]),
            "receipt_sha256": receipt_sha,
            "canonical_receipt_utf8": raw.decode("utf-8", errors="strict"),
            "release_bom_id": str(receipt["release_bom_id"]),
            "release_bom_sha256": str(receipt["release_bom_sha256"]),
            "integration_commit": str(receipt["integration_commit"]),
            "release_bom_generation": int(receipt["release_bom_generation"]),
            "activation_token_sha256": str(receipt["activation_token_sha256"]),
            "trust_policy_id": str(receipt["trust_policy_id"]),
            "native_stop_authorities_sha256": str(receipt["native_stop_authorities_sha256"]),
            "device_route_assignment_authorities_sha256": str(
                receipt["device_route_assignment_authorities_sha256"]
            ),
            "native_stop_challenge_authorities_sha256": str(
                receipt["native_stop_challenge_authorities_sha256"]
            ),
            "authority_sets_sha256": str(receipt["authority_sets_sha256"]),
            "provider_attestation": envelope.provider_attestation.public_record(),
        }
        fingerprint = _sha256_bytes(_canonical_bytes({
            "authority_schema_sha256": self._schema_sha256,
            "provider_identity": self._provider_identity,
            "receipt_id": fact["receipt_id"],
            "receipt_sha256": fact["receipt_sha256"],
            "release_bom_sha256": fact["release_bom_sha256"],
            "release_bom_generation": fact["release_bom_generation"],
            "activation_token_sha256": fact["activation_token_sha256"],
            "authority_sets_sha256": fact["authority_sets_sha256"],
            "attestation_id": envelope.provider_attestation.attestation_id,
            "attestation_nonce": envelope.provider_attestation.nonce,
            "attestation_expires_at": envelope.provider_attestation.expires_at,
        }))
        return receipt, fact, fingerprint

    def _validate_provider_attestation(
        self,
        attestation: NativeStopTrustProviderAttestation,
        receipt: Mapping[str, Any],
        receipt_sha256: str,
        request: NativeStopTrustRequest,
    ) -> None:
        if (
            attestation.provider_identity != self._provider_identity
            or attestation.issuer != PROVIDER_ATTESTATION_ISSUER
            or attestation.audience != PROVIDER_ATTESTATION_AUDIENCE
            or attestation.workflow_id != request.workflow_id
            or attestation.request_sha256 != request.request_sha256
            or attestation.external_context_ref != request.external_context_ref
            or attestation.receipt_id != receipt.get("receipt_id")
            or attestation.receipt_sha256 != receipt_sha256
            or attestation.release_bom_sha256 != receipt.get("release_bom_sha256")
            or attestation.release_bom_generation != receipt.get("release_bom_generation")
            or attestation.revoked is not False
        ):
            raise NativeStopAuthorityTrustError("provider attestation binding or revocation drifted")
        for value, name in (
            (attestation.attestation_id, "attestation_id"),
            (attestation.provider_identity, "provider_identity"),
            (attestation.nonce, "nonce"),
            (attestation.key_id, "key_id"),
        ):
            if not isinstance(value, str) or _OPAQUE_ID.fullmatch(value) is None:
                raise NativeStopAuthorityTrustError("provider attestation identity is invalid: " + name)
        issued = _runtime_ticks(attestation.issued_at, "provider attestation issued_at")
        expires = _runtime_ticks(attestation.expires_at, "provider attestation expires_at")
        now = _clock_ticks(self._clock.now())
        max_window = int(MAX_PROVIDER_ATTESTATION_WINDOW.total_seconds() * 10_000_000)
        if issued > now or now >= expires or issued >= expires or expires - issued > max_window:
            raise NativeStopAuthorityTrustError("provider attestation is stale or outside its window")
        receipt_issued = _runtime_ticks(str(receipt.get("occurred_at")), "trust receipt occurred_at")
        if receipt_issued > issued:
            raise NativeStopAuthorityTrustError(
                "trust receipt occurs after its provider attestation",
            )
        if (
            attestation.algorithm != "rsa-pss-sha256"
            or attestation.key_id not in self._provider_key_ids
            or not isinstance(attestation.signature, str)
            or not attestation.signature
            or self._provider_verifier.verify(
                purpose="native-stop-trust-provider-attestation",
                algorithm=attestation.algorithm,
                key_id=attestation.key_id,
                signing_bytes=provider_attestation_signing_bytes(attestation),
                signature=attestation.signature,
            ) is not True
        ):
            raise NativeStopAuthorityTrustError("provider attestation signature is untrusted")

    def _validate_authority_sets(self, receipt: Mapping[str, Any]) -> None:
        generation = receipt["release_bom_generation"]
        activation = receipt["activation_token_sha256"]
        now = _clock_ticks(self._clock.now())
        native = receipt["native_stop_authorities"]
        routes = receipt["device_route_assignment_authorities"]
        challenges = receipt["native_stop_challenge_authorities"]
        identifier_sets: list[list[str]] = [[], [], []]
        public_keys: set[str] = set()
        for index, item in enumerate(native):
            if (
                item["worker_authority_sha256"] != _individual_native_hash(item)
                or item["release_bom_generation"] != generation
                or item["activation_token_sha256"] != activation
                or item["revoked"] is not False
            ):
                raise NativeStopAuthorityTrustError("native-stop authority digest or BOM binding drifted")
            self._validate_authority_window(item, now)
            identifier_sets[0].append(str(item["authority_id"]))
            if item["p256_spki_sha256"] in public_keys:
                raise NativeStopAuthorityTrustError("one public key is reused across authority roles")
            public_keys.add(str(item["p256_spki_sha256"]))
        for item in routes:
            if (
                item["route_authority_sha256"] != _individual_route_hash(item)
                or item["release_bom_generation"] != generation
                or item["activation_token_sha256"] != activation
                or item["revoked"] is not False
                or item["route_signer_key_id"] != "p256_spki_" + item["route_signer_p256_spki_sha256"]
            ):
                raise NativeStopAuthorityTrustError("route authority digest or BOM binding drifted")
            self._validate_authority_window(item, now)
            identifier_sets[1].append(str(item["route_authority_id"]))
            if item["route_signer_p256_spki_sha256"] in public_keys:
                raise NativeStopAuthorityTrustError("one public key is reused across authority roles")
            public_keys.add(str(item["route_signer_p256_spki_sha256"]))
        for item in challenges:
            if (
                item["challenge_authority_sha256"] != _individual_challenge_hash(item)
                or item["release_bom_generation"] != generation
                or item["activation_token_sha256"] != activation
                or item["revoked"] is not False
            ):
                raise NativeStopAuthorityTrustError("challenge authority digest or BOM binding drifted")
            self._validate_authority_window(item, now)
            identifier_sets[2].append(str(item["authority_id"]))
            if item["p256_spki_sha256"] in public_keys:
                raise NativeStopAuthorityTrustError("one public key is reused across authority roles")
            public_keys.add(str(item["p256_spki_sha256"]))
        if any(len(values) != len(set(values)) for values in identifier_sets):
            raise NativeStopAuthorityTrustError("authority identifiers are not unique")
        native_hash = _native_set_hash(native)
        route_hash = _route_set_hash(routes)
        challenge_hash = _challenge_set_hash(challenges)
        if (
            receipt["native_stop_authorities_sha256"] != native_hash
            or receipt["device_route_assignment_authorities_sha256"] != route_hash
            or receipt["native_stop_challenge_authorities_sha256"] != challenge_hash
            or receipt["authority_sets_sha256"]
            != _authority_sets_hash(native_hash, route_hash, challenge_hash)
        ):
            raise NativeStopAuthorityTrustError("authority-set digest drifted")

    @staticmethod
    def _validate_authority_window(authority: Mapping[str, Any], now: int) -> None:
        valid_from = _runtime_ticks(str(authority["valid_from"]), "authority valid_from")
        valid_until = _runtime_ticks(str(authority["valid_until"]), "authority valid_until")
        if valid_from > now or now >= valid_until or valid_from >= valid_until:
            raise NativeStopAuthorityTrustError("authority is stale or outside its validity window")

    def _assert_provider_unchanged(self) -> None:
        if (
            id(self._provider) != self._provider_object_id
            or self._provider.provider_identity != self._provider_identity
        ):
            raise NativeStopAuthorityTrustError("deployed trust provider was swapped after composition")

    @staticmethod
    def _validate_request(
        workflow_id: str,
        request_sha256: str,
        external_context_ref: str | None,
        release_bom_sha256: str,
    ) -> NativeStopTrustRequest:
        if not isinstance(workflow_id, str) or _WORKFLOW_ID.fullmatch(workflow_id) is None:
            raise NativeStopAuthorityTrustError("native-stop trust workflow identity is invalid")
        if not isinstance(request_sha256, str) or _SHA256.fullmatch(request_sha256) is None:
            raise NativeStopAuthorityTrustError("native-stop trust request digest is invalid")
        if not isinstance(release_bom_sha256, str) or _SHA256.fullmatch(release_bom_sha256) is None:
            raise NativeStopAuthorityTrustError("native-stop trust BOM digest is invalid")
        if external_context_ref is not None and (
            not isinstance(external_context_ref, str)
            or _OPAQUE_ID.fullmatch(external_context_ref) is None
        ):
            raise NativeStopAuthorityTrustError("native-stop trust external context is invalid")
        return NativeStopTrustRequest(
            workflow_id, request_sha256, external_context_ref, release_bom_sha256,
        )


def compose_native_stop_authority_trust_authority(
    repository_root: Path,
    *,
    expected_schema_sha256: str,
    provider: NativeStopAuthorityTrustProvider,
    release_signature_verifier: NativeStopTrustCryptographicVerifier,
    provider_attestation_verifier: NativeStopTrustCryptographicVerifier,
    release_signer_key_ids: Sequence[str],
    provider_attestation_key_ids: Sequence[str],
    clock: NativeStopTrustClock | None = None,
) -> NativeStopAuthorityTrustAuthority:
    """Create the sole production authority from fixed, typed deployment ports."""
    if not isinstance(repository_root, Path):
        raise TypeError("repository_root must be a pathlib.Path")
    if (
        not isinstance(provider, NativeStopAuthorityTrustProvider)
        or isinstance(provider, Mapping)
        or callable(provider)
    ):
        raise TypeError("native-stop trust provider must be a fixed provider port")
    if not isinstance(release_signature_verifier, NativeStopTrustCryptographicVerifier):
        raise TypeError("Release signature verifier must be a fixed verifier port")
    if not isinstance(provider_attestation_verifier, NativeStopTrustCryptographicVerifier):
        raise TypeError("provider attestation verifier must be a fixed verifier port")
    selected_clock = clock or SystemNativeStopTrustClock()
    if not isinstance(selected_clock, NativeStopTrustClock) or callable(selected_clock):
        raise TypeError("native-stop trust clock must be a fixed clock port")
    if not isinstance(expected_schema_sha256, str) or _SHA256.fullmatch(expected_schema_sha256) is None:
        raise ValueError("native-stop trust schema digest is invalid")
    root = repository_root.resolve(strict=True)
    if repository_root.is_symlink() or not root.is_dir():
        raise ValueError("repository root must be a real non-symlink directory")
    candidate = root / SCHEMA_RELATIVE_PATH
    if candidate.is_symlink():
        raise ValueError("native-stop trust schema must not be a symlink")
    resolved = candidate.resolve(strict=True)
    try:
        resolved.relative_to(root)
    except ValueError as exc:
        raise ValueError("native-stop trust schema escapes the repository") from exc
    raw_schema = resolved.read_bytes()
    if _sha256_bytes(raw_schema) != expected_schema_sha256:
        raise ValueError("native-stop trust schema digest drifted")
    try:
        schema = json.loads(raw_schema.decode("utf-8", errors="strict"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ValueError("native-stop trust schema is not UTF-8 JSON") from exc
    Draft202012Validator.check_schema(schema)
    release_keys = frozenset(release_signer_key_ids)
    provider_keys = frozenset(provider_attestation_key_ids)
    if (
        not release_keys or not provider_keys
        or any(_OPAQUE_ID.fullmatch(value) is None for value in release_keys | provider_keys)
    ):
        raise ValueError("native-stop trust signer key policy is invalid")
    provider_identity = provider.provider_identity
    if not isinstance(provider_identity, str) or _OPAQUE_ID.fullmatch(provider_identity) is None:
        raise ValueError("native-stop trust provider identity is invalid")
    return NativeStopAuthorityTrustAuthority(
        provider=provider,
        release_signature_verifier=release_signature_verifier,
        provider_attestation_verifier=provider_attestation_verifier,
        clock=selected_clock,
        validator=Draft202012Validator(schema, format_checker=FormatChecker()),
        schema_sha256=expected_schema_sha256,
        release_key_ids=release_keys,
        provider_key_ids=provider_keys,
        provider_identity=provider_identity,
        sentinel=_AUTHORITY_SENTINEL,
    )


__all__ = [
    "CONTRACT_FAMILY", "CONTRACT_ID", "MAX_PROVIDER_ATTESTATION_WINDOW",
    "NativeStopAuthorityTrustAuthority", "NativeStopAuthorityTrustError",
    "NativeStopAuthorityTrustProvider", "NativeStopTrustClock",
    "NativeStopTrustCryptographicVerifier", "NativeStopTrustEnvelope",
    "NativeStopTrustProviderAttestation", "NativeStopTrustRequest",
    "PROVIDER_ATTESTATION_AUDIENCE", "PROVIDER_ATTESTATION_ISSUER",
    "SCHEMA_RELATIVE_PATH", "SystemNativeStopTrustClock",
    "VerifiedNativeStopAuthorityTrust", "compose_native_stop_authority_trust_authority",
    "provider_attestation_signing_bytes", "release_receipt_signing_bytes",
]
