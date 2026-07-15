"""Restricted trusted-runner entrypoint.

All mutable inputs live under one process-bound input directory using fixed file
names. Runtime facts are authenticated with a supervisor-held HMAC key. RSA-PSS
signing uses a supervisor-bound private key and fixed OpenSSL argv; neither
secret is accepted in request JSON or printed.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
import subprocess
import sys
from pathlib import Path
from typing import Any, Mapping

from trusted_runner import (
    TrustedRunner,
    TrustedRunnerError,
    TrustedRunnerPolicy,
    canonical_bytes,
    sha256_bytes,
)


_INPUT_FILES = {
    "request": "request.json",
    "plan": "worktree-plan.json",
    "receipt": "instruction-receipt.json",
    "lease": "worktree-lease.json",
    "policy": "trusted-policy.json",
    "facts": "runtime-facts.json",
}
_FACT_KEYS = {"policy", "auth_context", "instruction_receipt", "commit_workspace", "lease"}


def _bound_directory(name: str) -> Path:
    value = os.environ.get(name)
    if not value:
        raise TrustedRunnerError(f"missing process binding {name}")
    path = Path(value)
    if not path.is_absolute() or path.is_symlink() or not path.is_dir():
        raise TrustedRunnerError(f"invalid process binding {name}")
    return path.resolve(strict=True)


def _read_fixed_json(root: Path, filename: str) -> Mapping[str, Any]:
    path = root / filename
    if path.is_symlink() or not path.is_file() or path.parent.resolve(strict=True) != root:
        raise TrustedRunnerError(f"invalid fixed input {filename}")
    try:
        value = json.loads(path.read_bytes())
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise TrustedRunnerError(f"invalid JSON input {filename}") from exc
    if not isinstance(value, dict):
        raise TrustedRunnerError(f"input {filename} must be an object")
    return value


def _runtime_facts(document: Mapping[str, Any]) -> Mapping[str, Mapping[str, Any]]:
    if set(document) != {"payload", "hmac_sha256"} or not isinstance(document["payload"], dict):
        raise TrustedRunnerError("invalid runtime facts envelope")
    payload = document["payload"]
    if set(payload) != _FACT_KEYS or any(not isinstance(value, dict) for value in payload.values()):
        raise TrustedRunnerError("invalid runtime facts payload")
    key_hex = os.environ.get("DPS_RUNNER_FACT_HMAC_KEY_HEX", "")
    try:
        key = bytes.fromhex(key_hex)
    except ValueError as exc:
        raise TrustedRunnerError("invalid runtime fact HMAC key") from exc
    if len(key) < 32:
        raise TrustedRunnerError("runtime fact HMAC key must be at least 256 bits")
    expected = hmac.new(key, canonical_bytes(payload), hashlib.sha256).hexdigest()
    supplied = document["hmac_sha256"]
    if not isinstance(supplied, str) or not hmac.compare_digest(expected, supplied):
        raise TrustedRunnerError("runtime fact HMAC verification failed")
    return payload


def _rsa_pss_issuer(payload: bytes, runner_identity: str, _policy_digest: str) -> dict[str, str]:
    key_value = os.environ.get("DPS_RUNNER_RSA_PRIVATE_KEY")
    key_id = os.environ.get("DPS_RUNNER_RSA_KEY_ID")
    openssl_value = os.environ.get("DPS_RUNNER_OPENSSL", "/usr/bin/openssl")
    if not key_value or not key_id:
        raise TrustedRunnerError("missing process-bound RSA signer configuration")
    key, openssl = Path(key_value), Path(openssl_value)
    if (
        not key.is_absolute() or key.is_symlink() or not key.is_file()
        or not openssl.is_absolute() or openssl.is_symlink() or not openssl.is_file()
    ):
        raise TrustedRunnerError("invalid process-bound RSA signer path")
    if os.name == "posix" and key.stat().st_mode & 0o077:
        raise TrustedRunnerError("RSA private key permissions are too broad")
    completed = subprocess.run(
        [str(openssl.resolve(strict=True)), "dgst", "-sha256", "-sign",
         str(key.resolve(strict=True)), "-sigopt", "rsa_padding_mode:pss"],
        input=payload, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, shell=False, timeout=15, check=False,
        env={key: value for key, value in os.environ.items() if key in {"HOME", "PATH", "TMPDIR"}},
    )
    if completed.returncode != 0 or len(completed.stdout) < 64:
        raise TrustedRunnerError("process-bound RSA-PSS signer failed")
    return {
        "algorithm": "rsa-pss-sha256", "key_id": key_id,
        "signer_identity": runner_identity,
        "payload_sha256": sha256_bytes(payload),
        "signature_value": base64.b64encode(completed.stdout).decode("ascii"),
    }


def run_from_environment() -> dict[str, Any]:
    repository = _bound_directory("DPS_RUNNER_REPOSITORY_ROOT")
    workspace = _bound_directory("DPS_RUNNER_EXECUTION_ROOT")
    input_root = _bound_directory("DPS_RUNNER_INPUT_DIR")
    if input_root == repository or input_root == workspace or repository in input_root.parents or workspace in input_root.parents:
        raise TrustedRunnerError("runtime inputs must be outside repository and execution workspace")
    inputs = {name: _read_fixed_json(input_root, filename) for name, filename in _INPUT_FILES.items()}
    facts = _runtime_facts(inputs["facts"])
    expected_policy = os.environ.get("DPS_RUNNER_POLICY_SHA256", "")
    policy_bytes = canonical_bytes(inputs["policy"])
    policy = TrustedRunnerPolicy.from_verified_document(
        policy_bytes, expected_policy,
        lambda _document, _digest: facts["policy"],
    )
    runner_identity = os.environ.get("DPS_RUNNER_IDENTITY", "")
    runner = TrustedRunner(
        repository, workspace, runner_identity,
        auth_context_verifier=lambda _auth_id, _runner: facts["auth_context"],
        receipt_verifier=lambda _receipt: facts["instruction_receipt"],
        commit_workspace_verifier=lambda _root, _commit, _workspace: facts["commit_workspace"],
        fence_verifier=lambda _lease_id, _tokens: facts["lease"],
        attestation_issuer=_rsa_pss_issuer,
    )
    return runner.run(
        inputs["request"], inputs["plan"], inputs["receipt"],
        inputs["lease"], policy,
    )


def main() -> int:
    try:
        result = run_from_environment()
    except (TrustedRunnerError, OSError, subprocess.SubprocessError) as exc:
        print(json.dumps({"status": "FAIL", "error": str(exc)}, sort_keys=True), file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
