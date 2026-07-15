"""Process-bound verifier for the Factory providers' public JSON contracts."""

from __future__ import annotations

import json
import re
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Mapping

from jsonschema import Draft202012Validator, FormatChecker

from factory_control_plane_host import canonical_bytes, sha256


PROVIDER_SCHEMA_PATHS = {
    "upgrade.intent/v1": "Modules/factory-upgrade-intake/contracts/provided/upgrade.intent.v1.schema.json",
    "instruction.receipt/v1": "Modules/factory-instruction-resolver/contracts/provided/instruction.receipt.v1.schema.json",
    "module.change.plan/v1": "Modules/factory-impact-analyzer/contracts/provided/module.change.plan.v1.schema.json",
    "worktree.plan/v1": "Modules/factory-worktree-manager/contracts/provided/worktree.plan.v1.schema.json",
    "worktree.lease/v1": "Modules/factory-worktree-manager/contracts/provided/worktree.lease.v1.schema.json",
    "trusted.test.result/v1": "Modules/factory-trusted-runner/contracts/provided/trusted.test.result.v1.schema.json",
    "merge.decision/v1": "Modules/factory-merge-controller/contracts/provided/merge.decision.v1.schema.json",
    "artifact.descriptor/v1": "Modules/factory-artifact-builder/contracts/provided/artifact.descriptor.v1.schema.json",
    "upgrade.event/v1": "Modules/factory-evidence-ledger/contracts/provided/upgrade.event.v1.schema.json",
    "rollout.event/v1": "Modules/factory-release-controller/contracts/provided/rollout.event.v1.schema.json",
    "rollback.plan/v1": "Modules/factory-rollback-controller/contracts/provided/rollback.plan.v1.schema.json",
    "rollback.result/v1": "Modules/factory-rollback-controller/contracts/provided/rollback.result.v1.schema.json",
}


class SchemaProviderContractVerifier:
    """Compile an externally authenticated, immutable provider schema set.

    The repository files are only deployment inputs.  Their exact byte digests
    and the signature over the complete digest set must be supplied by an
    independent trust root before any validator is constructed.
    """

    _TRUST_FIELDS = {
        "schema_set_sha256", "trust_root_sha256", "signer_identity",
        "signature_sha256", "verified_at",
    }

    def __init__(
        self,
        repository_root: Path,
        *,
        expected_schema_sha256s: Mapping[str, str],
        trust_record: Mapping[str, Any],
        signature_verifier: Callable[[Mapping[str, str], Mapping[str, Any]], bool],
        maximum_payload_bytes: int = 1_048_576,
    ) -> None:
        if repository_root.is_symlink():
            raise ValueError("repository root must not be a symlink")
        root = repository_root.resolve(strict=True)
        if not root.is_dir():
            raise ValueError("repository root must be an existing non-symlink directory")
        if maximum_payload_bytes < 1024 or maximum_payload_bytes > 16_777_216:
            raise ValueError("provider contract payload limit is invalid")
        if not isinstance(expected_schema_sha256s, Mapping) or set(expected_schema_sha256s) != set(PROVIDER_SCHEMA_PATHS):
            raise ValueError("provider schema digest set is incomplete or contains unknown contracts")
        expected = dict(expected_schema_sha256s)
        if any(
            not isinstance(contract_id, str)
            or not isinstance(digest, str)
            or re.fullmatch(r"[a-f0-9]{64}", digest) is None
            for contract_id, digest in expected.items()
        ):
            raise ValueError("provider schema digest set contains an invalid digest")
        if not isinstance(trust_record, Mapping) or set(trust_record) != self._TRUST_FIELDS:
            raise ValueError("provider schema trust record is not exact")
        trusted = dict(trust_record)
        schema_set_sha256 = sha256(expected)
        if trusted.get("schema_set_sha256") != schema_set_sha256:
            raise ValueError("provider schema trust record does not bind the exact digest set")
        for name in ("trust_root_sha256", "signature_sha256"):
            if not isinstance(trusted.get(name), str) or re.fullmatch(r"[a-f0-9]{64}", trusted[name]) is None:
                raise ValueError("provider schema trust record has an invalid " + name)
        signer = trusted.get("signer_identity")
        if not isinstance(signer, str) or re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{7,127}", signer) is None:
            raise ValueError("provider schema trust signer identity is invalid")
        verified_at = trusted.get("verified_at")
        if not isinstance(verified_at, str):
            raise ValueError("provider schema trust timestamp is invalid")
        try:
            parsed = datetime.fromisoformat(verified_at.replace("Z", "+00:00"))
        except ValueError as exc:
            raise ValueError("provider schema trust timestamp is invalid") from exc
        if parsed.tzinfo is None:
            raise ValueError("provider schema trust timestamp must have a timezone")
        if signature_verifier(dict(expected), dict(trusted)) is not True:
            raise ValueError("provider schema digest set lacks an external trust-root signature")
        self._maximum_payload_bytes = maximum_payload_bytes
        self._schema_set_sha256 = schema_set_sha256
        self._validators: dict[str, Draft202012Validator] = {}
        for contract_id, relative in PROVIDER_SCHEMA_PATHS.items():
            candidate = root / relative
            if candidate.is_symlink():
                raise ValueError("provider contract schema must not be a symlink: " + relative)
            resolved = candidate.resolve(strict=True)
            try:
                resolved.relative_to(root)
            except ValueError as exc:
                raise ValueError("provider contract schema escapes the repository") from exc
            schema_bytes = resolved.read_bytes()
            if sha256(schema_bytes) != expected[contract_id]:
                raise ValueError("provider contract schema digest drift: " + relative)
            try:
                schema = json.loads(schema_bytes.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                raise ValueError("provider contract schema is not canonical UTF-8 JSON: " + relative) from exc
            Draft202012Validator.check_schema(schema)
            self._validators[contract_id] = Draft202012Validator(
                schema, format_checker=FormatChecker(),
            )

    @property
    def schema_set_sha256(self) -> str:
        return self._schema_set_sha256

    def verify(self, contract_id: str, payload: Mapping[str, Any]) -> bool:
        validator = self._validators.get(contract_id)
        if validator is None or not isinstance(payload, Mapping):
            return False
        try:
            if len(canonical_bytes(dict(payload))) > self._maximum_payload_bytes:
                return False
            return next(validator.iter_errors(dict(payload)), None) is None
        except (TypeError, ValueError, UnicodeError):
            return False


__all__ = ["PROVIDER_SCHEMA_PATHS", "SchemaProviderContractVerifier"]
