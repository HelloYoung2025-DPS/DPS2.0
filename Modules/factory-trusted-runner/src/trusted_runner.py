"""Fail-closed execution of policy-owned test commands.

The public runner API has no command-string or shell option. Authentication,
instruction freshness, commit/workspace truth, fencing and RSA signing are
process-bound collaborators. Untrusted input can only select a registered check.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shlex
import shutil
import signal
import subprocess
import tempfile
import time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path, PurePosixPath
from typing import Any, Callable, Mapping, Sequence


class TrustedRunnerError(ValueError):
    """A trust, containment, or execution invariant failed."""


_MODULE_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
_CHECK_ID = re.compile(r"^[a-z0-9]+(?:[.-][a-z0-9]+)+$")
_COMMIT = re.compile(r"^[0-9a-f]{40}$")
_HASH = re.compile(r"^[0-9a-f]{64}$")
_LEASE_ID = re.compile(r"^lease:[0-9a-f]{32}$")
_IDENTITIES = {
    "soul_id": re.compile(r"^soul_[a-f0-9]{64}\Z"),
    "device_binding_id": re.compile(r"^db_[a-f0-9]{32}\Z"),
    "platform_account_id": re.compile(r"^pa_[a-f0-9]{32}\Z"),
}
_TRACE_ID = re.compile(r"^trace_[a-f0-9]{32}\Z")
_IDEMPOTENCY_KEY = re.compile(r"^idem_[a-f0-9]{64}\Z")
_SHELLS = {
    "ash", "bash", "cmd", "cmd.exe", "csh", "dash", "fish", "ksh",
    "powershell", "powershell.exe", "pwsh", "pwsh.exe", "sh", "tcsh", "zsh",
}
_SHELL_TOKENS = (";", "&&", "||", "`", "$(", "${", "\n", "\r", "\x00")
_EVIDENCE_LEVELS = {
    "REPOSITORY_STATIC_VERIFIED", "CONTRACT_VERIFIED", "INTEGRATION_VERIFIED",
    "WINDOWS_VERIFIED", "DEVICE_VERIFIED", "CANARY_VERIFIED", "SCALE_VERIFIED",
}
_REQUEST_KEYS = {
    "request_id", "check_id", "module_id", "worktree_plan_id",
    "instruction_receipt_id", "auth_context_id", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id", "idempotency_key",
    "occurred_at",
}
_POLICY_KEYS = {
    "schema_version", "policy_id", "status", "runner_identity",
    "allowed_executables", "allowed_environment", "maximum_output_bytes",
    "roles", "checks",
}
_ROLE_KEYS = {"implementers", "evidence_issuers", "release_approvers"}
_CHECK_KEYS = {
    "check_id", "module_id", "argv", "cwd", "timeout_seconds", "required",
    "success_marker", "forbidden_output_markers",
}
_ATTESTATION_KEYS = {
    "algorithm", "key_id", "signer_identity", "payload_sha256", "signature_value",
}


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def _exact_keys(value: Mapping[str, Any], expected: set[str], label: str) -> None:
    actual = set(value)
    if actual != expected:
        raise TrustedRunnerError(
            f"{label} keys invalid: missing={sorted(expected - actual)}, "
            f"extra={sorted(actual - expected)}"
        )


def _timestamp(value: Any, label: str) -> None:
    if not isinstance(value, str):
        raise TrustedRunnerError(f"{label} must be a timezone-aware timestamp")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise TrustedRunnerError(f"{label} must be a timezone-aware timestamp") from exc
    if parsed.tzinfo is None:
        raise TrustedRunnerError(f"{label} must be a timezone-aware timestamp")


def _identity_envelope(value: Mapping[str, Any]) -> None:
    for name, pattern in _IDENTITIES.items():
        item = value.get(name)
        if item is not None and (not isinstance(item, str) or pattern.fullmatch(item) is None):
            raise TrustedRunnerError(f"invalid {name}")
    for name, pattern in (("trace_id", _TRACE_ID), ("idempotency_key", _IDEMPOTENCY_KEY)):
        item = value.get(name)
        if not isinstance(item, str) or pattern.fullmatch(item) is None:
            raise TrustedRunnerError(f"invalid {name}")
    for name, minimum, maximum in (("auth_context_id", 8, 128), ("request_id", 8, 128)):
        item = value.get(name)
        if not isinstance(item, str) or not minimum <= len(item) <= maximum:
            raise TrustedRunnerError(f"invalid {name}")
    _timestamp(value.get("occurred_at"), "occurred_at")


def _relative(value: Any, label: str, *, allow_dot: bool = False) -> PurePosixPath:
    if not isinstance(value, str) or not value or "\\" in value:
        raise TrustedRunnerError(f"invalid {label}")
    if value == "." and allow_dot:
        return PurePosixPath(".")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in path.parts):
        raise TrustedRunnerError(f"unsafe {label}")
    if any(part.startswith(".") for part in path.parts):
        raise TrustedRunnerError(f"hidden {label} is forbidden")
    return path


def _existing(root: Path, relative: PurePosixPath, label: str, *, directory: bool) -> Path:
    candidate = root.joinpath(*relative.parts)
    current = root
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            raise TrustedRunnerError(f"symlinked {label} is forbidden")
    if not candidate.exists():
        raise TrustedRunnerError(f"missing {label}")
    resolved = candidate.resolve(strict=True)
    try:
        resolved.relative_to(root)
    except ValueError as exc:
        raise TrustedRunnerError(f"{label} escapes execution root") from exc
    if directory != resolved.is_dir():
        kind = "directory" if directory else "file"
        raise TrustedRunnerError(f"{label} must be a {kind}")
    return resolved


def workspace_sha256(root: Path) -> str:
    """Hash exact non-Git workspace bytes and reject every symlink."""
    records: list[dict[str, str]] = []
    for path in sorted(root.rglob("*"), key=lambda item: item.relative_to(root).as_posix()):
        relative = path.relative_to(root)
        if relative.parts and relative.parts[0] == ".git":
            continue
        if path.is_symlink():
            raise TrustedRunnerError(f"symlinked workspace path: {relative.as_posix()}")
        if path.is_file():
            records.append({
                "path": relative.as_posix(),
                "sha256": sha256_bytes(path.read_bytes()),
            })
    return sha256_bytes(canonical_bytes(records))


@dataclass(frozen=True)
class TrustedCheck:
    check_id: str
    module_id: str
    argv: tuple[str, ...]
    cwd: PurePosixPath
    timeout_seconds: int
    required: bool
    success_marker: str
    forbidden_output_markers: tuple[str, ...]


@dataclass(frozen=True)
class TrustedRunnerPolicy:
    digest: str
    policy_id: str
    runner_identity: str
    allowed_executables: frozenset[str]
    allowed_environment: tuple[str, ...]
    maximum_output_bytes: int
    implementers: frozenset[str]
    evidence_issuers: frozenset[str]
    release_approvers: frozenset[str]
    checks: Mapping[str, TrustedCheck]

    @classmethod
    def from_verified_document(
        cls,
        document: bytes,
        expected_sha256: str,
        verifier: Callable[[bytes, str], Mapping[str, Any]],
    ) -> "TrustedRunnerPolicy":
        digest = sha256_bytes(document)
        if not _HASH.fullmatch(expected_sha256) or digest != expected_sha256:
            raise TrustedRunnerError("trusted policy digest mismatch")
        fact = verifier(document, digest)
        _verified_fact(fact, "trusted policy", digest, digest)
        try:
            raw = json.loads(document)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise TrustedRunnerError("trusted policy is not valid JSON") from exc
        if not isinstance(raw, dict):
            raise TrustedRunnerError("trusted policy must be an object")
        _exact_keys(raw, _POLICY_KEYS, "trusted policy")
        if raw["schema_version"] != "dps.trusted-runner-policy/v1":
            raise TrustedRunnerError("unknown trusted policy version")
        if not isinstance(raw["policy_id"], str) or not raw["policy_id"]:
            raise TrustedRunnerError("invalid policy_id")
        runner = raw["runner_identity"]
        if not isinstance(runner, str) or not runner:
            raise TrustedRunnerError("invalid runner identity")
        executables = raw["allowed_executables"]
        if (
            not isinstance(executables, list) or not executables
            or len(set(executables)) != len(executables)
            or any(not isinstance(item, str) or not item or "/" in item or "\\" in item for item in executables)
            or any(item.casefold() in _SHELLS for item in executables)
        ):
            raise TrustedRunnerError("invalid executable allowlist")
        environment = raw["allowed_environment"]
        if (
            not isinstance(environment, list) or len(set(environment)) != len(environment)
            or any(not isinstance(item, str) or re.fullmatch(r"[A-Z][A-Z0-9_]*", item) is None for item in environment)
        ):
            raise TrustedRunnerError("invalid environment allowlist")
        maximum_output = raw["maximum_output_bytes"]
        if not isinstance(maximum_output, int) or not 1 <= maximum_output <= 16 * 1024 * 1024:
            raise TrustedRunnerError("invalid maximum_output_bytes")
        roles = raw["roles"]
        if not isinstance(roles, dict):
            raise TrustedRunnerError("roles must be an object")
        _exact_keys(roles, _ROLE_KEYS, "roles")
        role_sets: dict[str, frozenset[str]] = {}
        for name, identities in roles.items():
            if (
                not isinstance(identities, list) or not identities
                or len(set(identities)) != len(identities)
                or any(not isinstance(item, str) or not item for item in identities)
            ):
                raise TrustedRunnerError(f"invalid identities for {name}")
            role_sets[name] = frozenset(identities)
        if (
            role_sets["implementers"] & role_sets["evidence_issuers"]
            or role_sets["implementers"] & role_sets["release_approvers"]
            or role_sets["evidence_issuers"] & role_sets["release_approvers"]
        ):
            raise TrustedRunnerError("trusted role identities must be disjoint")
        if runner not in role_sets["evidence_issuers"]:
            raise TrustedRunnerError("runner must be an evidence issuer")
        raw_checks = raw["checks"]
        if not isinstance(raw_checks, list) or not raw_checks:
            raise TrustedRunnerError("trusted policy has no checks")
        checks: dict[str, TrustedCheck] = {}
        for item in raw_checks:
            if not isinstance(item, dict):
                raise TrustedRunnerError("check must be an object")
            _exact_keys(item, _CHECK_KEYS, "check")
            check_id, module_id, argv = item["check_id"], item["module_id"], item["argv"]
            if not isinstance(check_id, str) or _CHECK_ID.fullmatch(check_id) is None or check_id in checks:
                raise TrustedRunnerError("invalid or duplicate check_id")
            if not isinstance(module_id, str) or _MODULE_ID.fullmatch(module_id) is None:
                raise TrustedRunnerError("invalid check module_id")
            if not isinstance(argv, list) or len(argv) < 3 or any(not isinstance(arg, str) or not arg for arg in argv):
                raise TrustedRunnerError("invalid check argv")
            executable = Path(argv[0]).name
            if executable not in executables or executable.casefold() in _SHELLS:
                raise TrustedRunnerError("check executable is not allowed")
            if any(any(token in argument for token in _SHELL_TOKENS) for argument in argv):
                raise TrustedRunnerError("shell-like argv token is forbidden")
            cwd = _relative(item["cwd"], "check cwd", allow_dot=True)
            timeout = item["timeout_seconds"]
            if not isinstance(timeout, int) or not 1 <= timeout <= 1800:
                raise TrustedRunnerError("invalid check timeout")
            if not isinstance(item["required"], bool):
                raise TrustedRunnerError("invalid required flag")
            success, forbidden = item["success_marker"], item["forbidden_output_markers"]
            if not isinstance(success, str) or not success:
                raise TrustedRunnerError("invalid success marker")
            if not isinstance(forbidden, list) or not forbidden or any(not isinstance(marker, str) or not marker for marker in forbidden):
                raise TrustedRunnerError("invalid forbidden markers")
            checks[check_id] = TrustedCheck(
                check_id, module_id, tuple(argv), cwd, timeout, item["required"],
                success, tuple(forbidden),
            )
        return cls(
            digest, raw["policy_id"], runner, frozenset(executables), tuple(environment),
            maximum_output, role_sets["implementers"], role_sets["evidence_issuers"],
            role_sets["release_approvers"], checks,
        )


def _verified_fact(
    fact: Mapping[str, Any], label: str, expected_id: str, expected_hash: str | None = None
) -> None:
    if not isinstance(fact, Mapping):
        raise TrustedRunnerError(f"{label} verifier returned no immutable fact")
    if fact.get("verified") is not True or fact.get("fact_id") != expected_id:
        raise TrustedRunnerError(f"{label} external verification failed")
    digest = fact.get("fact_sha256")
    if not isinstance(digest, str) or _HASH.fullmatch(digest) is None:
        raise TrustedRunnerError(f"{label} immutable fact hash missing")
    if expected_hash is not None and digest != expected_hash:
        raise TrustedRunnerError(f"{label} immutable fact hash mismatch")


class TrustedRunner:
    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        execution_root: str | os.PathLike[str],
        runner_identity: str,
        *,
        auth_context_verifier: Callable[[str, str], Mapping[str, Any]],
        receipt_verifier: Callable[[Mapping[str, Any]], Mapping[str, Any]],
        commit_workspace_verifier: Callable[[Path, str, str], Mapping[str, Any]],
        fence_verifier: Callable[[str, Mapping[str, int]], Mapping[str, Any]],
        attestation_issuer: Callable[[bytes, str, str], Mapping[str, Any]],
    ) -> None:
        self.repository_root = self._root(repository_root, "repository root")
        self.execution_root = self._root(execution_root, "execution root")
        if not isinstance(runner_identity, str) or not runner_identity:
            raise TrustedRunnerError("invalid process-bound runner identity")
        self.runner_identity = runner_identity
        self.auth_context_verifier = auth_context_verifier
        self.receipt_verifier = receipt_verifier
        self.commit_workspace_verifier = commit_workspace_verifier
        self.fence_verifier = fence_verifier
        self.attestation_issuer = attestation_issuer

    @staticmethod
    def _root(value: str | os.PathLike[str], label: str) -> Path:
        path = Path(value)
        if path.is_symlink() or not path.exists() or not path.is_dir():
            raise TrustedRunnerError(f"invalid {label}")
        return path.resolve(strict=True)

    def run(
        self,
        request: Mapping[str, Any],
        worktree_plan: Mapping[str, Any],
        instruction_receipt: Mapping[str, Any],
        lease: Mapping[str, Any],
        policy: TrustedRunnerPolicy,
    ) -> dict[str, Any]:
        if not isinstance(request, Mapping):
            raise TrustedRunnerError("request must be an object")
        _exact_keys(request, _REQUEST_KEYS, "run request")
        _identity_envelope(request)
        if self.runner_identity != policy.runner_identity:
            raise TrustedRunnerError("process runner identity does not match trusted policy")
        check_id, module_id = request["check_id"], request["module_id"]
        if not isinstance(check_id, str) or check_id not in policy.checks:
            raise TrustedRunnerError("unknown trusted check")
        check = policy.checks[check_id]
        if module_id != check.module_id or not isinstance(module_id, str) or _MODULE_ID.fullmatch(module_id) is None:
            raise TrustedRunnerError("request module does not match trusted check")

        auth_fact = self.auth_context_verifier(request["auth_context_id"], self.runner_identity)
        _verified_fact(auth_fact, "auth context", request["auth_context_id"])
        receipt_fact = self.receipt_verifier(instruction_receipt)
        _verified_fact(
            receipt_fact, "instruction receipt", request["instruction_receipt_id"],
            sha256_bytes(canonical_bytes(instruction_receipt)),
        )
        self._validate_receipt(request, module_id, instruction_receipt)
        module_entry = self._validate_plan(request, module_id, worktree_plan, policy)
        tested_commit, workspace_hash = self._verified_commit_workspace(worktree_plan)
        lease_id, fencing_token = self._verified_lease(worktree_plan, lease, module_entry)
        manifest_hash, suite_id, evidence_level = self._verified_manifest_suite(
            module_id, check, instruction_receipt
        )

        cwd = self.execution_root if check.cwd == PurePosixPath(".") else _existing(
            self.execution_root, check.cwd, "check cwd", directory=True
        )
        argv = (self._resolve_executable(check.argv[0], policy), *check.argv[1:])
        started_at = _utc_now()
        exit_code, stdout, stderr, timed_out, limited = self._execute(
            argv, cwd, self._environment(policy), check.timeout_seconds,
            policy.maximum_output_bytes,
        )
        finished_at = _utc_now()
        status = self._status(exit_code, stdout + stderr, check, timed_out, limited)
        stdout_hash, stderr_hash = sha256_bytes(stdout), sha256_bytes(stderr)
        log_hash = sha256_bytes(canonical_bytes({
            "stdout_sha256": stdout_hash, "stderr_sha256": stderr_hash,
            "timed_out": timed_out, "output_limited": limited,
        }))
        required_hash = sha256_bytes(canonical_bytes(sorted(
            item.check_id for item in policy.checks.values() if item.required
        )))
        seed = canonical_bytes({
            "request_id": request["request_id"], "check_id": check_id,
            "tested_commit": tested_commit, "workspace_sha256": workspace_hash,
            "policy": policy.digest,
        })
        result: dict[str, Any] = {
            "schema_version": "1.0.0", "contract_id": "trusted.test.result/v1",
            "producer_module": "factory-trusted-runner",
            "soul_id": request["soul_id"], "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"], "trace_id": request["trace_id"],
            "idempotency_key": request["idempotency_key"], "occurred_at": finished_at,
            "privacy_class": "internal", "result_id": "result:" + sha256_bytes(seed)[:32],
            "request_id": request["request_id"], "worktree_plan_id": request["worktree_plan_id"],
            "module_id": module_id, "check_id": check_id, "suite_id": suite_id,
            "evidence_level": evidence_level, "template_id": self._template_id(check.argv),
            "tested_commit": tested_commit, "required": check.required, "status": status,
            "release_allowed": status == "PASS", "runner_identity": self.runner_identity,
            "auth_context_id": request["auth_context_id"],
            "instruction_receipt_id": request["instruction_receipt_id"],
            "manifest_sha256": manifest_hash, "workspace_sha256": workspace_hash,
            "required_checks_sha256": required_hash, "trusted_policy_sha256": policy.digest,
            "lease_id": lease_id, "fencing_token": fencing_token,
            "command_argv": list(check.argv), "timeout_seconds": check.timeout_seconds,
            "started_at": started_at, "finished_at": finished_at, "exit_code": exit_code,
            "stdout_sha256": stdout_hash, "stderr_sha256": stderr_hash, "log_sha256": log_hash,
        }
        result["raw_artifact_sha256"] = sha256_bytes(canonical_bytes(result))
        payload = canonical_bytes(result)
        result["runner_attestation"] = self._attestation(
            self.attestation_issuer(payload, self.runner_identity, policy.digest),
            sha256_bytes(payload),
        )
        return result

    @staticmethod
    def _validate_receipt(request: Mapping[str, Any], module_id: str, receipt: Mapping[str, Any]) -> None:
        if (
            receipt.get("contract_id") != "instruction.receipt/v1"
            or receipt.get("producer_module") != "factory-instruction-resolver"
            or receipt.get("receipt_id") != request["instruction_receipt_id"]
            or receipt.get("auth_context_id") != request["auth_context_id"]
            or receipt.get("status") != "BOUND" or receipt.get("invalidated_reason") is not None
            or module_id not in receipt.get("scope", [])
        ):
            raise TrustedRunnerError("instruction receipt is stale or outside scope")
        if any(receipt.get(name) != request[name] for name in _IDENTITIES):
            raise TrustedRunnerError("instruction receipt identity scope mismatch")

    @staticmethod
    def _validate_plan(
        request: Mapping[str, Any], module_id: str, plan: Mapping[str, Any], policy: TrustedRunnerPolicy
    ) -> Mapping[str, Any]:
        if (
            plan.get("contract_id") != "worktree.plan/v1"
            or plan.get("producer_module") != "factory-worktree-manager"
            or plan.get("plan_id") != request["worktree_plan_id"]
            or plan.get("instruction_receipt_id") != request["instruction_receipt_id"]
            or plan.get("trusted_policy_sha256") != policy.digest
            or any(plan.get(name) != request[name] for name in _IDENTITIES)
        ):
            raise TrustedRunnerError("worktree plan does not match trusted facts")
        entries = plan.get("entries")
        if not isinstance(entries, list):
            raise TrustedRunnerError("worktree plan entries are invalid")
        matching = [item for item in entries if isinstance(item, dict) and item.get("module_id") == module_id]
        if len(matching) != 1 or matching[0].get("writer_identity") not in policy.implementers:
            raise TrustedRunnerError("worktree plan must contain one trusted module writer")
        return matching[0]

    def _verified_commit_workspace(self, plan: Mapping[str, Any]) -> tuple[str, str]:
        actual_commit = self._git_head()
        actual_workspace = workspace_sha256(self.execution_root)
        fact = self.commit_workspace_verifier(
            self.execution_root, actual_commit, actual_workspace
        )
        _verified_fact(fact, "commit workspace", plan["plan_id"])
        if fact.get("commit") != actual_commit or fact.get("workspace_sha256") != actual_workspace:
            raise TrustedRunnerError("commit/workspace immutable facts do not match")
        return actual_commit, actual_workspace

    def _git_head(self) -> str:
        git = shutil.which("git", path=os.environ.get("PATH"))
        if git is None:
            raise TrustedRunnerError("git is unavailable for commit verification")
        process = subprocess.run(
            [str(Path(git).resolve(strict=True)), "-C", str(self.execution_root),
             "rev-parse", "--verify", "HEAD^{commit}"],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            shell=False, timeout=10, check=False, env=self._git_environment(),
        )
        commit = process.stdout.decode("ascii", errors="ignore").strip()
        if process.returncode != 0 or _COMMIT.fullmatch(commit) is None:
            raise TrustedRunnerError("execution workspace has no exact Git commit")
        return commit

    @staticmethod
    def _git_environment() -> dict[str, str]:
        return {key: value for key, value in os.environ.items() if key in {"HOME", "PATH", "TMPDIR"}}

    def _verified_lease(
        self, plan: Mapping[str, Any], lease: Mapping[str, Any], entry: Mapping[str, Any]
    ) -> tuple[str, int]:
        lease_id, tokens = lease.get("lease_id"), lease.get("lock_tokens")
        required_locks = set(entry.get("lease_keys", []))
        if (
            lease.get("contract_id") != "worktree.lease/v1"
            or lease.get("producer_module") != "factory-worktree-manager"
            or lease.get("status") != "ACTIVE" or lease.get("plan_id") != plan.get("plan_id")
            or not isinstance(lease_id, str) or _LEASE_ID.fullmatch(lease_id) is None
            or not isinstance(tokens, dict) or not tokens or not required_locks.issubset(tokens)
            or any(not isinstance(key, str) or not isinstance(value, int) or value < 1 for key, value in tokens.items())
        ):
            raise TrustedRunnerError("invalid active worktree lease")
        fact = self.fence_verifier(lease_id, tokens)
        _verified_fact(fact, "worktree lease", lease_id, sha256_bytes(canonical_bytes(lease)))
        fence = fact.get("fencing_token")
        if (
            fact.get("plan_id") != plan.get("plan_id") or fact.get("lock_tokens") != tokens
            or not isinstance(fence, int) or fence < 1 or lease.get("fencing_token") != fence
        ):
            raise TrustedRunnerError("stale fencing token")
        return lease_id, fence

    def _verified_manifest_suite(
        self, module_id: str, check: TrustedCheck, receipt: Mapping[str, Any]
    ) -> tuple[str, str, str]:
        relative = PurePosixPath("Modules") / module_id / "module.yaml"
        path = _existing(self.execution_root, relative, "module manifest", directory=False)
        content, digest = path.read_bytes(), sha256_bytes(path.read_bytes())
        bound = [item for item in receipt.get("manifests", []) if isinstance(item, dict) and item.get("path") == relative.as_posix()]
        if len(bound) != 1 or bound[0].get("sha256") != digest:
            raise TrustedRunnerError("module manifest is not bound by the fresh receipt")
        try:
            manifest = json.loads(content)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise TrustedRunnerError("module manifest is not JSON-compatible YAML") from exc
        suites = manifest.get("tests", {}).get("suites", [])
        matching = [item for item in suites if isinstance(item, dict) and item.get("id") == check.check_id]
        if len(matching) != 1:
            raise TrustedRunnerError("trusted check is not a unique Manifest suite")
        suite = matching[0]
        evidence_level = suite.get("evidenceLevel")
        if evidence_level not in _EVIDENCE_LEVELS or suite.get("required") != check.required:
            raise TrustedRunnerError("Manifest suite evidence metadata mismatches policy")
        try:
            manifest_argv = shlex.split(suite.get("command", ""), posix=True)
        except ValueError as exc:
            raise TrustedRunnerError("Manifest suite command is malformed") from exc
        if manifest_argv != list(check.argv):
            raise TrustedRunnerError("Manifest suite argv mismatches trusted policy")
        return digest, suite["id"], evidence_level

    @staticmethod
    def _resolve_executable(argv0: str, policy: TrustedRunnerPolicy) -> str:
        if Path(argv0).name not in policy.allowed_executables:
            raise TrustedRunnerError("executable not allowed by trusted policy")
        resolved = shutil.which(argv0, path=os.environ.get("PATH"))
        if resolved is None or not Path(resolved).is_file() or Path(resolved).name.casefold() in _SHELLS:
            raise TrustedRunnerError("trusted executable is unavailable or forbidden")
        return str(Path(resolved).resolve(strict=True))

    @staticmethod
    def _environment(policy: TrustedRunnerPolicy) -> dict[str, str]:
        environment = {key: os.environ[key] for key in policy.allowed_environment if key in os.environ}
        environment["PYTHONDONTWRITEBYTECODE"] = "1"
        if "PYTHONUTF8" in policy.allowed_environment:
            environment["PYTHONUTF8"] = "1"
        return environment

    @staticmethod
    def _terminate(process: subprocess.Popen[bytes]) -> None:
        if process.poll() is not None:
            return
        try:
            if os.name == "posix":
                os.killpg(process.pid, signal.SIGKILL)
            else:
                process.kill()
        except (OSError, ProcessLookupError):
            process.kill()

    @classmethod
    def _execute(
        cls, argv: Sequence[str], cwd: Path, environment: Mapping[str, str],
        timeout_seconds: int, maximum_output_bytes: int,
    ) -> tuple[int | None, bytes, bytes, bool, bool]:
        timed_out = limited = False
        with tempfile.TemporaryFile() as out, tempfile.TemporaryFile() as err:
            process = subprocess.Popen(
                list(argv), cwd=cwd, env=dict(environment), stdin=subprocess.DEVNULL,
                stdout=out, stderr=err, shell=False, close_fds=True,
                start_new_session=(os.name == "posix"),
            )
            deadline = time.monotonic() + timeout_seconds
            while process.poll() is None:
                if time.monotonic() >= deadline:
                    timed_out = True
                    cls._terminate(process)
                    break
                if os.fstat(out.fileno()).st_size + os.fstat(err.fileno()).st_size > maximum_output_bytes:
                    limited = True
                    cls._terminate(process)
                    break
                time.sleep(0.01)
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                cls._terminate(process)
                process.wait(timeout=5)
            out.seek(0); err.seek(0)
            stdout = out.read(maximum_output_bytes + 1)
            stderr = err.read(max(0, maximum_output_bytes + 1 - len(stdout)))
            if len(stdout) + len(stderr) > maximum_output_bytes:
                limited = True
                stdout = stdout[:maximum_output_bytes]
                stderr = stderr[:max(0, maximum_output_bytes - len(stdout))]
            return process.returncode, stdout, stderr, timed_out, limited

    @staticmethod
    def _status(
        exit_code: int | None, output: bytes, check: TrustedCheck,
        timed_out: bool, limited: bool,
    ) -> str:
        if timed_out or limited:
            return "INFRA_ERROR"
        text, upper = output.decode("utf-8", errors="replace"), output.decode("utf-8", errors="replace").upper()
        for marker, status in (
            ("PARTIAL", "PARTIAL"), ("NOT_RUN", "NOT_RUN"),
            ("NOT_APPLICABLE", "NOT_APPLICABLE"), ("INFRA_ERROR", "INFRA_ERROR"),
            ("SKIP", "SKIP"),
        ):
            if marker in upper:
                return status
        if exit_code != 0 or any(marker in text for marker in check.forbidden_output_markers):
            return "FAIL"
        return "PASS" if check.success_marker in text else "FAIL"

    @staticmethod
    def _template_id(argv: Sequence[str]) -> str:
        return "python.unit" if "unittest" in argv else "python.compile"

    def _attestation(self, value: Mapping[str, Any], payload_hash: str) -> dict[str, Any]:
        if not isinstance(value, Mapping):
            raise TrustedRunnerError("attestation issuer returned no signature")
        _exact_keys(value, _ATTESTATION_KEYS, "runner attestation")
        if (
            value.get("algorithm") != "rsa-pss-sha256"
            or value.get("signer_identity") != self.runner_identity
            or value.get("payload_sha256") != payload_hash
            or not isinstance(value.get("key_id"), str) or not value["key_id"]
            or not isinstance(value.get("signature_value"), str) or len(value["signature_value"]) < 64
        ):
            raise TrustedRunnerError("runner attestation does not bind the result")
        return dict(value)
