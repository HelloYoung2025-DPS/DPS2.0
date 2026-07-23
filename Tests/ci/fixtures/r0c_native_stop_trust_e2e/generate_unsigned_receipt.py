#!/usr/bin/env python3
"""Rebuild/check the unsigned owner-signing fixture from production code."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import sys
from pathlib import Path


FIXTURE_ROOT = Path(__file__).resolve(strict=True).parent
REPOSITORY_ROOT = FIXTURE_ROOT.parents[3]
VALIDATOR_PATH = REPOSITORY_ROOT / "Tools" / "ci" / "candidate_bom_validator.py"
POLICY_PATH = (
    REPOSITORY_ROOT
    / "governance"
    / "policies"
    / "deployed-release-trust-policy.v1.json"
)
CONTRACT_ID = "release.bom.native.stop.authority.trust/v1"
RECEIPT_ID_DOMAIN = "dps-r0c-native-stop-trust-e2e-fixture-receipt-id/v1"
TRACE_ID = "trace_" + "8" * 32
OCCURRED_AT = "2026-07-14T00:00:00.0000001Z"
SIGNATURE_KEY_ID = "native-stop-trust-owner-key-1"


def _load_validator():
    name = "_dps_r0c_unsigned_receipt_generator"
    spec = importlib.util.spec_from_file_location(name, VALIDATOR_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"cannot import {VALIDATOR_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def build_expected():
    validator = _load_validator()
    bom_bytes = (FIXTURE_ROOT / "bom.json").read_bytes()
    bom = validator._strict_json_loads(bom_bytes, "fixture Release BOM")
    policy = validator._strict_json_loads(
        POLICY_PATH.read_bytes(), "deployed release trust policy"
    )
    bom_sha256 = validator.sha256_bytes(bom_bytes)
    identity_material = {
        "domain": RECEIPT_ID_DOMAIN,
        "contract_id": CONTRACT_ID,
        "occurred_at": OCCURRED_AT,
        "release_bom_sha256": bom_sha256,
        "trace_id": TRACE_ID,
        "trust_policy_id": policy["policy_id"],
    }
    identity_sha256 = validator.sha256_bytes(
        validator.canonical_bytes(identity_material)
    )
    receipt_id = "native-stop-trust-" + identity_sha256[:32]
    payload = validator.build_native_stop_trust_receipt_payload(
        bom,
        bom_bytes,
        policy["policy_id"],
        receipt_id,
        TRACE_ID,
        OCCURRED_AT,
    )
    payload_bytes = validator.canonical_bytes(payload) + b"\n"
    signing_bytes = validator.native_stop_trust_signing_bytes(payload)
    return validator, payload, payload_bytes, signing_bytes, identity_sha256


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(
        description="Check or rebuild the unsigned R0-C owner-signing fixture"
    )
    parser.add_argument(
        "--write-unsigned",
        action="store_true",
        help="write the rebuilt payload and reset its detached signature placeholder",
    )
    parser.add_argument(
        "--signing-bytes-out",
        type=Path,
        help="write the exact public signing message to this explicit path",
    )
    args = parser.parse_args(argv)

    validator, payload, payload_bytes, signing_bytes, identity_sha256 = build_expected()
    payload_path = FIXTURE_ROOT / "receipt-payload.json"
    signature_path = FIXTURE_ROOT / "receipt-signature.json"

    if args.write_unsigned:
        payload_path.write_bytes(payload_bytes)
        signature_path.write_bytes(
            validator.canonical_bytes(
                {
                    "algorithm": "rsa-pss-sha256",
                    "key_id": SIGNATURE_KEY_ID,
                    "value": "",
                }
            )
            + b"\n"
        )
    elif payload_path.read_bytes() != payload_bytes:
        raise SystemExit(
            "receipt-payload.json is not the production-builder output for bom.json"
        )

    if args.signing_bytes_out is not None:
        args.signing_bytes_out.parent.mkdir(parents=True, exist_ok=True)
        args.signing_bytes_out.write_bytes(signing_bytes)

    print(
        json.dumps(
            {
                "identity_sha256": identity_sha256,
                "receipt_id": payload["receipt_id"],
                "idempotency_key": payload["idempotency_key"],
                "payload_file_sha256": hashlib.sha256(payload_bytes).hexdigest(),
                "signing_bytes_length": len(signing_bytes),
                "signing_bytes_sha256": hashlib.sha256(signing_bytes).hexdigest(),
            },
            sort_keys=True,
            separators=(",", ":"),
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
