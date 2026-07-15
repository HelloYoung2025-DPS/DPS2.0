"""Fail-closed merge-head decisions bound to a trusted runner attestation."""

from __future__ import annotations

import base64
import binascii
import hashlib
import hmac
import json
import re
from collections.abc import Mapping, Sequence
from typing import Any


_COMMIT = re.compile(r"^[0-9a-f]{40}$")
_SHA256 = re.compile(r"^[0-9a-f]{64}$")
_OPAQUE_IDS = {
    "soul_id": re.compile(r"^soul_[a-f0-9]{64}\Z"),
    "device_binding_id": re.compile(r"^db_[a-f0-9]{32}\Z"),
    "platform_account_id": re.compile(r"^pa_[a-f0-9]{32}\Z"),
    "trace_id": re.compile(r"^trace_[a-f0-9]{32}\Z"),
    "idempotency_key": re.compile(r"^idem_[a-f0-9]{64}\Z"),
}
_PASS = "PASS"
_REQUEST_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class",
    "merge_request_id", "integration_commit", "branch_heads",
    "changed_paths", "evidence", "instruction_receipts",
    "current_diff_fingerprint", "conflicts", "trusted_policy_sha256",
    "runner_attestation",
}
_POLICY_FIELDS = {
    "schema_version", "policy_id", "required_checks", "implementers",
    "evidence_issuers", "merge_decider", "release_approvers",
    "trusted_runner_policy_sha256",
}
_ATTESTATION_FIELDS = {
    "algorithm", "key_id", "signer_identity", "payload_sha256",
    "signature_value",
}
_TEST_RESULT_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class", "result_id",
    "request_id", "worktree_plan_id", "module_id", "check_id", "suite_id",
    "evidence_level", "template_id", "tested_commit", "required", "status",
    "release_allowed", "runner_identity", "auth_context_id",
    "instruction_receipt_id", "manifest_sha256", "workspace_sha256",
    "required_checks_sha256", "trusted_policy_sha256", "lease_id",
    "fencing_token", "command_argv", "timeout_seconds", "started_at",
    "finished_at", "exit_code", "stdout_sha256", "stderr_sha256",
    "log_sha256", "raw_artifact_sha256", "runner_attestation",
}


class InvalidMergeRequest(ValueError):
    """The request cannot be interpreted as a trusted merge.request/v1."""


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def sha256(value: Any) -> str:
    data = value if isinstance(value, bytes) else canonical_bytes(value)
    return hashlib.sha256(data).hexdigest()


def _opaque_envelope_is_valid(value: Mapping[str, Any]) -> bool:
    for field, pattern in _OPAQUE_IDS.items():
        item = value.get(field)
        if field in {"soul_id", "device_binding_id", "platform_account_id"} and item is None:
            continue
        if not isinstance(item, str) or pattern.fullmatch(item) is None:
            return False
    return True


def _require_mapping(value: Any, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise InvalidMergeRequest(f"{name} must be an object")
    return value


def _require_sequence(value: Any, name: str) -> Sequence[Any]:
    if not isinstance(value, Sequence) or isinstance(value, (str, bytes, bytearray)):
        raise InvalidMergeRequest(f"{name} must be an array")
    return value


def _mgf1(seed: bytes, length: int) -> bytes:
    output = bytearray()
    counter = 0
    while len(output) < length:
        output.extend(hashlib.sha256(seed + counter.to_bytes(4, "big")).digest())
        counter += 1
    return bytes(output[:length])


def _verify_rsa_pss(message: bytes, signature: bytes, modulus: int, exponent: int) -> bool:
    """Verify RSA-PSS/SHA-256 with a 32-byte salt (RFC 8017)."""
    if modulus.bit_length() < 1024 or exponent < 3 or exponent % 2 == 0:
        return False
    em_bits = modulus.bit_length() - 1
    em_length = (em_bits + 7) // 8
    if len(signature) != (modulus.bit_length() + 7) // 8:
        return False
    encoded = pow(int.from_bytes(signature, "big"), exponent, modulus).to_bytes(em_length, "big")
    digest_length = hashlib.sha256().digest_size
    salt_length = digest_length
    if em_length < digest_length + salt_length + 2 or encoded[-1] != 0xBC:
        return False
    masked_db = encoded[: em_length - digest_length - 1]
    encoded_hash = encoded[em_length - digest_length - 1 : -1]
    unused_bits = 8 * em_length - em_bits
    if unused_bits and masked_db[0] >> (8 - unused_bits):
        return False
    mask = _mgf1(encoded_hash, len(masked_db))
    data_block = bytearray(left ^ right for left, right in zip(masked_db, mask))
    if unused_bits:
        data_block[0] &= 0xFF >> unused_bits
    padding_length = em_length - digest_length - salt_length - 2
    if data_block[:padding_length] != b"\x00" * padding_length or data_block[padding_length] != 0x01:
        return False
    salt = bytes(data_block[-salt_length:])
    expected = hashlib.sha256(b"\x00" * 8 + hashlib.sha256(message).digest() + salt).digest()
    return hmac.compare_digest(encoded_hash, expected)


class RsaPssTrustStore:
    """Immutable public-key facts loaded by the process trust boundary, not a request."""

    def __init__(self, keys: Mapping[str, Mapping[str, Any]]) -> None:
        if not keys:
            raise ValueError("at least one trusted runner key is required")
        normalized: dict[str, tuple[str, int, int]] = {}
        for key_id, record in keys.items():
            if set(record) != {"identity", "algorithm", "modulus_hex", "exponent"}:
                raise ValueError(f"invalid trust-store record for {key_id}")
            if record["algorithm"] != "rsa-pss-sha256":
                raise ValueError("only rsa-pss-sha256 is supported")
            try:
                modulus = int(str(record["modulus_hex"]), 16)
                exponent = int(record["exponent"])
            except (TypeError, ValueError) as exc:
                raise ValueError("invalid RSA public key") from exc
            identity = record["identity"]
            if not isinstance(key_id, str) or not key_id or not isinstance(identity, str) or not identity:
                raise ValueError("key_id and identity are required")
            if modulus.bit_length() < 1024:
                raise ValueError("trusted RSA modulus must be at least 1024 bits")
            normalized[key_id] = (identity, modulus, exponent)
        self._keys = normalized

    def verify(self, attestation: Mapping[str, Any], payload_bytes: bytes) -> str:
        if set(attestation) != _ATTESTATION_FIELDS:
            raise InvalidMergeRequest("runner attestation has unknown or missing fields")
        if attestation.get("algorithm") != "rsa-pss-sha256":
            raise InvalidMergeRequest("unsupported runner attestation algorithm")
        key_id = attestation.get("key_id")
        trusted = self._keys.get(key_id) if isinstance(key_id, str) else None
        if trusted is None:
            raise InvalidMergeRequest("runner attestation key is not trusted")
        identity, modulus, exponent = trusted
        if attestation.get("signer_identity") != identity:
            raise InvalidMergeRequest("runner attestation identity does not match trust store")
        if attestation.get("payload_sha256") != hashlib.sha256(payload_bytes).hexdigest():
            raise InvalidMergeRequest("runner attestation payload digest mismatch")
        try:
            signature = base64.b64decode(str(attestation.get("signature_value")), validate=True)
        except (binascii.Error, ValueError) as exc:
            raise InvalidMergeRequest("runner attestation signature is not valid base64") from exc
        if not _verify_rsa_pss(payload_bytes, signature, modulus, exponent):
            raise InvalidMergeRequest("runner attestation signature verification failed")
        return identity


class MergeController:
    """Evaluate the synthesized merge commit using process-bound trusted policy."""

    def __init__(
        self,
        decider_identity: str,
        trusted_policy: Mapping[str, Any],
        trust_store: RsaPssTrustStore,
    ) -> None:
        if not decider_identity:
            raise ValueError("decider_identity is required")
        if set(trusted_policy) != _POLICY_FIELDS or trusted_policy.get("schema_version") != "dps.merge-policy/v1":
            raise ValueError("trusted merge policy has unknown, missing, or invalid fields")
        required_checks = tuple(_require_sequence(trusted_policy.get("required_checks"), "policy.required_checks"))
        implementers = frozenset(_require_sequence(trusted_policy.get("implementers"), "policy.implementers"))
        issuers = frozenset(_require_sequence(trusted_policy.get("evidence_issuers"), "policy.evidence_issuers"))
        approvers = frozenset(_require_sequence(trusted_policy.get("release_approvers"), "policy.release_approvers"))
        if not required_checks or len(set(required_checks)) != len(required_checks):
            raise ValueError("trusted policy required checks are missing or duplicated")
        if not implementers or not issuers or not approvers:
            raise ValueError("trusted policy must assign every separated role")
        if trusted_policy.get("merge_decider") != decider_identity:
            raise ValueError("trusted policy merge decider does not match process identity")
        role_groups = [implementers, issuers, {decider_identity}, approvers]
        if any(left & right for index, left in enumerate(role_groups) for right in role_groups[index + 1 :]):
            raise ValueError("trusted policy violates separation of duties")
        self._decider_identity = decider_identity
        self._trusted_policy = dict(trusted_policy)
        self._policy_sha256 = sha256(self._trusted_policy)
        runner_policy_sha = trusted_policy.get("trusted_runner_policy_sha256")
        if not isinstance(runner_policy_sha, str) or not _SHA256.fullmatch(runner_policy_sha):
            raise ValueError("trusted runner policy digest is invalid")
        self._runner_policy_sha256 = runner_policy_sha
        self._required_checks = required_checks
        self._required_checks_sha256 = sha256(sorted(required_checks))
        self._implementers = implementers
        self._issuers = issuers
        self._approvers = approvers
        self._trust_store = trust_store

    @property
    def trusted_policy_sha256(self) -> str:
        return self._policy_sha256

    def evaluate(self, request: Mapping[str, Any]) -> dict[str, Any]:
        request = _require_mapping(request, "request")
        if set(request) != _REQUEST_FIELDS:
            raise InvalidMergeRequest("merge request has unknown or missing fields")
        if request.get("schema_version") != "1.0.0" or request.get("contract_id") != "merge.request/v1":
            raise InvalidMergeRequest("unknown merge request contract version")
        if request.get("producer_module") != "factory-trusted-runner":
            raise InvalidMergeRequest("untrusted request producer")
        if request.get("trusted_policy_sha256") != self._policy_sha256:
            raise InvalidMergeRequest("merge request is not bound to the process trusted policy")
        if not _opaque_envelope_is_valid(request):
            raise InvalidMergeRequest("merge request opaque identifier envelope is invalid")

        request_id = request.get("merge_request_id")
        merge_commit = request.get("integration_commit")
        if not isinstance(request_id, str) or len(request_id) < 8:
            raise InvalidMergeRequest("merge_request_id is required")
        if not isinstance(merge_commit, str) or not _COMMIT.fullmatch(merge_commit):
            raise InvalidMergeRequest("integration_commit must be a lowercase 40-character Git object")

        evidence = list(_require_sequence(request.get("evidence"), "evidence"))
        receipts = list(_require_sequence(request.get("instruction_receipts"), "instruction_receipts"))
        changed_paths = list(_require_sequence(request.get("changed_paths"), "changed_paths"))
        conflicts = _require_mapping(request.get("conflicts"), "conflicts")
        if set(conflicts) != {"merge", "path_ownership", "contract_ownership"}:
            raise InvalidMergeRequest("conflicts has unknown or missing fields")
        current_diff = request.get("current_diff_fingerprint")
        if not isinstance(current_diff, str) or not _SHA256.fullmatch(current_diff):
            raise InvalidMergeRequest("current_diff_fingerprint must be SHA-256")
        attested_payload = {
            "contract_id": "merge.request-attestation/v1",
            "merge_request_id": request_id,
            "integration_commit": merge_commit,
            "changed_paths_sha256": sha256(changed_paths),
            "evidence_sha256": sha256(evidence),
            "instruction_receipts_sha256": sha256(receipts),
            "conflicts_sha256": sha256(dict(conflicts)),
            "current_diff_fingerprint": current_diff,
            "trusted_policy_sha256": self._policy_sha256,
        }
        attested_bytes = b"dps-merge-request-attestation/v1\n" + canonical_bytes(attested_payload)
        signer_identity = self._trust_store.verify(
            _require_mapping(request.get("runner_attestation"), "runner_attestation"),
            attested_bytes,
        )
        if signer_identity not in self._issuers:
            raise InvalidMergeRequest("trusted runner signer is not a policy evidence issuer")

        reasons: set[str] = set()
        for conflict_kind in ("merge", "path_ownership", "contract_ownership"):
            values = _require_sequence(conflicts.get(conflict_kind), f"conflicts.{conflict_kind}")
            if values:
                reasons.add(f"{conflict_kind} conflict present")

        if not receipts:
            reasons.add("instruction receipts missing")
        for raw_receipt in receipts:
            receipt = _require_mapping(raw_receipt, "instruction_receipt")
            if set(receipt) != {"receipt_id", "status", "diff_fingerprint", "receipt_sha256"}:
                raise InvalidMergeRequest("instruction receipt has unknown or missing fields")
            if receipt.get("status") != "BOUND" or receipt.get("diff_fingerprint") != current_diff:
                reasons.add("stale instruction receipt")
            if not isinstance(receipt.get("receipt_sha256"), str) or not _SHA256.fullmatch(receipt["receipt_sha256"]):
                raise InvalidMergeRequest("instruction receipt digest is invalid")

        verified_evidence: list[tuple[str, Mapping[str, Any]]] = []
        for raw_wrapper in evidence:
            wrapper = _require_mapping(raw_wrapper, "evidence wrapper")
            if set(wrapper) != {"basis", "result"} or wrapper.get("basis") not in {"merge-head", "branch-head"}:
                raise InvalidMergeRequest("evidence wrapper has unknown, missing, or invalid fields")
            item = _require_mapping(wrapper.get("result"), "trusted test result")
            if set(item) != _TEST_RESULT_FIELDS:
                raise InvalidMergeRequest("trusted test result has unknown or missing fields")
            if (
                item.get("schema_version") != "1.0.0"
                or item.get("contract_id") != "trusted.test.result/v1"
                or item.get("producer_module") != "factory-trusted-runner"
                or item.get("privacy_class") != "internal"
                or item.get("trusted_policy_sha256") != self._runner_policy_sha256
                or item.get("required_checks_sha256") != self._required_checks_sha256
                or item.get("suite_id") != item.get("check_id")
            ):
                raise InvalidMergeRequest("trusted test result is not bound to runner policy and manifest suite")
            if not _opaque_envelope_is_valid(item):
                raise InvalidMergeRequest("trusted test result opaque identifier envelope is invalid")
            unsigned = {key: value for key, value in item.items() if key != "runner_attestation"}
            pre_artifact = {key: value for key, value in unsigned.items() if key != "raw_artifact_sha256"}
            if item.get("raw_artifact_sha256") != sha256(pre_artifact):
                raise InvalidMergeRequest("trusted test raw artifact digest is invalid")
            evidence_signer = self._trust_store.verify(
                _require_mapping(item.get("runner_attestation"), "test result runner attestation"),
                canonical_bytes(unsigned),
            )
            if evidence_signer != item.get("runner_identity") or evidence_signer not in self._issuers:
                raise InvalidMergeRequest("trusted test result signer is not an evidence issuer")
            verified_evidence.append((wrapper["basis"], item))

        accepted_evidence_ids: set[str] = set()
        for check_id in self._required_checks:
            matching = [
                (basis, item) for basis, item in verified_evidence
                if item.get("check_id") == check_id
            ]
            merge_head_items = [
                item for basis, item in matching
                if basis == "merge-head" and item.get("tested_commit") == merge_commit
            ]
            if not merge_head_items:
                reasons.add(f"required check {check_id} lacks merge-head evidence")
                continue
            if any(
                item.get("status") != _PASS
                or item.get("required") is not True
                or item.get("release_allowed") is not True
                for item in merge_head_items
            ):
                reasons.add(f"required check {check_id} is not trusted PASS")
                continue
            for item in merge_head_items:
                result_id = item.get("result_id")
                if isinstance(result_id, str):
                    accepted_evidence_ids.add(result_id)

        outcome = "REJECTED" if reasons else "APPROVED"
        identity_material = {"request_id": request_id, "merge_commit": merge_commit, "policy": self._policy_sha256}
        decision_id = "merge-" + hashlib.sha256(canonical_bytes(identity_material)).hexdigest()[:32]
        attestation_sha = sha256(dict(_require_mapping(request["runner_attestation"], "runner_attestation")))
        return {
            "schema_version": "1.0.0",
            "contract_id": "merge.decision/v1",
            "producer_module": "factory-merge-controller",
            "soul_id": request.get("soul_id"),
            "device_binding_id": request.get("device_binding_id"),
            "platform_account_id": request.get("platform_account_id"),
            "trace_id": request.get("trace_id"),
            "idempotency_key": "idem_" + sha256({"decision_id": decision_id, "purpose": "merge-decision"}),
            "occurred_at": request.get("occurred_at"),
            "privacy_class": "internal",
            "decision_id": decision_id,
            "merge_request_id": request_id,
            "integration_commit": merge_commit,
            "outcome": outcome,
            "reasons": sorted(reasons),
            "evidence_ids": sorted(accepted_evidence_ids),
            "decided_by": self._decider_identity,
            "verification_scope": "MERGE_HEAD_ONLY",
            "trusted_policy_sha256": self._policy_sha256,
            "runner_attestation_sha256": attestation_sha,
        }
