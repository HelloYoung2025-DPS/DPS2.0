"""Test-only external trust-root fixture for the provider schema set."""

from __future__ import annotations

from pathlib import Path
from typing import Any, Mapping

from factory_control_plane_host import sha256
from schema_contract_verifier import PROVIDER_SCHEMA_PATHS, SchemaProviderContractVerifier


TEST_TRUST_ROOT_SHA256 = sha256("test-only-provider-schema-trust-root")
TEST_SIGNER_IDENTITY = "test:provider-schema-signer"


def build_test_provider_verifier(repository_root: Path) -> SchemaProviderContractVerifier:
    expected = {
        contract_id: sha256((repository_root / relative).read_bytes())
        for contract_id, relative in PROVIDER_SCHEMA_PATHS.items()
    }
    schema_set_sha256 = sha256(expected)
    signature_sha256 = sha256({
        "schema_set_sha256": schema_set_sha256,
        "trust_root_sha256": TEST_TRUST_ROOT_SHA256,
        "signer_identity": TEST_SIGNER_IDENTITY,
    })
    trust_record = {
        "schema_set_sha256": schema_set_sha256,
        "trust_root_sha256": TEST_TRUST_ROOT_SHA256,
        "signer_identity": TEST_SIGNER_IDENTITY,
        "signature_sha256": signature_sha256,
        "verified_at": "2026-07-14T00:00:00Z",
    }

    def verify_signature(
        actual: Mapping[str, str], trusted: Mapping[str, Any],
    ) -> bool:
        return bool(
            dict(actual) == expected
            and trusted.get("trust_root_sha256") == TEST_TRUST_ROOT_SHA256
            and trusted.get("signer_identity") == TEST_SIGNER_IDENTITY
            and trusted.get("signature_sha256") == sha256({
                "schema_set_sha256": sha256(dict(actual)),
                "trust_root_sha256": trusted.get("trust_root_sha256"),
                "signer_identity": trusted.get("signer_identity"),
            })
        )

    return SchemaProviderContractVerifier(
        repository_root,
        expected_schema_sha256s=expected,
        trust_record=trust_record,
        signature_verifier=verify_signature,
    )


__all__ = ["build_test_provider_verifier"]
