"""Fixed-argv Git worktree host adapter for trusted Factory plans.

This component never accepts a Git command or test command from a change
request. Repository/worktree roots, test argv and policy digest are bound by
the previous stable Factory process. All Git invocations use fixed argv with
``shell=False`` and validated commits, module IDs and Manifest-owned paths.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import subprocess
import threading
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Callable, Dict, Mapping, Sequence

from worktree_manager import StaleFence, WorktreeError, _canonical_bytes, _safe_relative


class MaterializationError(WorktreeError):
    """A worktree, commit, test, or merge invariant failed."""


_MODULE_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
_COMMIT = re.compile(r"^[0-9a-f]{40}$")
_HASH = re.compile(r"^[0-9a-f]{64}$")
_PLAN_ID = re.compile(r"^worktree:[0-9a-f]{32}$")
_SHELLS = {
    "bash", "cmd", "cmd.exe", "dash", "fish", "powershell", "powershell.exe",
    "pwsh", "pwsh.exe", "sh", "zsh",
}
_FORBIDDEN_OUTPUT = ("FAILED", "SKIP", "skipped=", "PARTIAL", "NOT_RUN", "INFRA_ERROR")


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _bound_root(value: str | os.PathLike[str], label: str, *, create: bool = False) -> Path:
    path = Path(value)
    if path.is_symlink():
        raise MaterializationError(f"symlinked {label} is forbidden")
    if create and not path.exists():
        parent = path.parent.resolve(strict=True)
        if parent.is_symlink():
            raise MaterializationError(f"symlinked {label} parent is forbidden")
        path.mkdir(mode=0o700)
    if not path.is_dir():
        raise MaterializationError(f"{label} must be a directory")
    return path.resolve(strict=True)


def _validate_argv(argv: Sequence[str]) -> tuple[str, ...]:
    if (
        not isinstance(argv, Sequence) or isinstance(argv, (str, bytes))
        or len(argv) < 1 or any(not isinstance(item, str) or not item for item in argv)
    ):
        raise MaterializationError("trusted test argv is invalid")
    executable = Path(argv[0]).name.casefold()
    if executable in _SHELLS:
        raise MaterializationError("shell interpreter is forbidden")
    if any(any(token in item for token in ("\x00", "\n", "\r", ";", "&&", "||", "`", "$(")) for item in argv):
        raise MaterializationError("shell-like test argv is forbidden")
    resolved = shutil.which(argv[0], path=os.environ.get("PATH"))
    if resolved is None or not Path(resolved).is_file():
        raise MaterializationError("trusted test executable is unavailable")
    return (str(Path(resolved).resolve(strict=True)), *argv[1:])


@dataclass(frozen=True)
class WorktreeEntry:
    entry_id: str
    module_id: str | None
    writer_identity: str
    owned_paths: tuple[str, ...]
    depends_on: tuple[str, ...]
    lease_keys: tuple[str, ...]
    path: Path


class GitWorktreeMaterializer:
    """Materialize, test and merge a trusted plan without request-authored argv."""

    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        worktree_root: str | os.PathLike[str],
        plan: Mapping[str, Any],
        *,
        trusted_policy_sha256: str,
        module_test_argv: Mapping[str, Sequence[str]],
        merge_test_argv: Sequence[str],
        plan_verifier: Callable[[Mapping[str, Any]], Mapping[str, Any]],
        fence_verifier: Callable[[Mapping[str, Any]], Mapping[str, Any]],
        maximum_output_bytes: int = 1_048_576,
    ) -> None:
        self.repository = _bound_root(repository_root, "repository root")
        self.worktree_root = _bound_root(worktree_root, "worktree root", create=True)
        if (
            self.repository == self.worktree_root
            or self.repository in self.worktree_root.parents
            or self.worktree_root in self.repository.parents
        ):
            raise MaterializationError("worktree root must be external to the repository")
        if any(self.worktree_root.iterdir()):
            raise MaterializationError("worktree root must start empty")
        if not isinstance(trusted_policy_sha256, str) or _HASH.fullmatch(trusted_policy_sha256) is None:
            raise MaterializationError("invalid process-bound policy digest")
        if not isinstance(maximum_output_bytes, int) or not 1024 <= maximum_output_bytes <= 16 * 1024 * 1024:
            raise MaterializationError("invalid output limit")
        self.git = self._resolve_git()
        self.plan = dict(plan)
        self.policy_digest = trusted_policy_sha256
        self.maximum_output_bytes = maximum_output_bytes
        self.plan_verifier = plan_verifier
        self.fence_verifier = fence_verifier
        self.module_test_argv = {
            key: _validate_argv(argv) for key, argv in module_test_argv.items()
        }
        self.merge_test_argv = _validate_argv(merge_test_argv)
        self.entries, self.contract_entry, self.waves = self._validate_plan()
        self._commits: Dict[str, str] = {}
        self._evidence: Dict[str, Dict[str, Any]] = {}
        self._leases: Dict[str, Dict[str, Any]] = {}
        self._lock = threading.Lock()
        self.integration_path = self.worktree_root / "integration"

    @staticmethod
    def _resolve_git() -> str:
        value = shutil.which("git", path=os.environ.get("PATH"))
        if value is None or not Path(value).is_file():
            raise MaterializationError("Git executable is unavailable")
        return str(Path(value).resolve(strict=True))

    @staticmethod
    def _environment() -> Dict[str, str]:
        environment = {
            key: os.environ[key] for key in ("HOME", "PATH", "TMPDIR") if key in os.environ
        }
        environment.update({
            "GIT_AUTHOR_NAME": "DPS Factory",
            "GIT_AUTHOR_EMAIL": "factory@dps.invalid",
            "GIT_COMMITTER_NAME": "DPS Factory",
            "GIT_COMMITTER_EMAIL": "factory@dps.invalid",
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_TERMINAL_PROMPT": "0",
            "PYTHONDONTWRITEBYTECODE": "1",
        })
        return environment

    def _git(
        self, cwd: Path, *arguments: str, check: bool = True
    ) -> subprocess.CompletedProcess[bytes]:
        completed = subprocess.run(
            [self.git, *arguments], cwd=cwd, env=self._environment(),
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            shell=False, timeout=60, check=False,
        )
        if len(completed.stdout) + len(completed.stderr) > self.maximum_output_bytes:
            raise MaterializationError("Git output exceeded the trusted limit")
        if check and completed.returncode != 0:
            raise MaterializationError(
                "fixed Git operation failed: "
                + completed.stderr.decode("utf-8", errors="replace")[:1000]
            )
        return completed

    def _head(self, cwd: Path) -> str:
        value = self._git(cwd, "rev-parse", "--verify", "HEAD^{commit}").stdout.decode("ascii").strip()
        if _COMMIT.fullmatch(value) is None:
            raise MaterializationError("Git returned an invalid commit")
        return value

    def _validate_plan(self) -> tuple[Dict[str, WorktreeEntry], WorktreeEntry | None, list[list[str]]]:
        plan = self.plan
        if (
            plan.get("contract_id") != "worktree.plan/v1"
            or plan.get("producer_module") != "factory-worktree-manager"
            or not isinstance(plan.get("plan_id"), str) or _PLAN_ID.fullmatch(plan["plan_id"]) is None
            or plan.get("trusted_policy_sha256") != self.policy_digest
            or not isinstance(plan.get("baseline_commit"), str) or _COMMIT.fullmatch(plan["baseline_commit"]) is None
        ):
            raise MaterializationError("worktree plan is not bound to trusted process facts")
        fact = self.plan_verifier(plan)
        if (
            not isinstance(fact, Mapping) or fact.get("verified") is not True
            or fact.get("fact_id") != plan["plan_id"]
            or fact.get("fact_sha256") != _sha256(_canonical_bytes(plan))
            or fact.get("baseline_commit") != plan["baseline_commit"]
            or fact.get("trusted_policy_sha256") != self.policy_digest
            or fact.get("instruction_receipt_id") != plan.get("instruction_receipt_id")
            or fact.get("instruction_receipt_status") != "BOUND"
        ):
            raise MaterializationError("worktree plan lacks an immutable fresh-receipt fact")
        if self._head(self.repository) != plan["baseline_commit"]:
            raise MaterializationError("plan baseline is stale")
        raw_entries = plan.get("entries")
        if not isinstance(raw_entries, list) or not raw_entries:
            raise MaterializationError("plan has no module entries")
        entries: Dict[str, WorktreeEntry] = {}
        path_owners: Dict[str, str] = {}
        for raw in raw_entries:
            if not isinstance(raw, Mapping):
                raise MaterializationError("invalid module worktree entry")
            module_id = raw.get("module_id")
            writer = raw.get("writer_identity")
            if (
                not isinstance(module_id, str) or _MODULE_ID.fullmatch(module_id) is None
                or module_id in entries or module_id not in self.module_test_argv
                or not isinstance(writer, str) or not writer
            ):
                raise MaterializationError("invalid module worktree identity or trusted check")
            paths = self._paths(raw.get("owned_paths"), module_id, path_owners)
            depends = raw.get("depends_on")
            lease_keys = raw.get("lease_keys")
            if (
                not isinstance(depends, list) or len(set(depends)) != len(depends)
                or any(not isinstance(item, str) or _MODULE_ID.fullmatch(item) is None for item in depends)
                or not isinstance(lease_keys, list) or not lease_keys
                or len(set(lease_keys)) != len(lease_keys)
            ):
                raise MaterializationError("invalid dependency or lease keys")
            entries[module_id] = WorktreeEntry(
                module_id, module_id, writer, paths, tuple(sorted(depends)),
                tuple(sorted(lease_keys)), self.worktree_root / module_id,
            )
        if any(set(entry.depends_on).difference(entries) for entry in entries.values()):
            raise MaterializationError("unknown materialization dependency")
        waves = self._waves(entries)
        for wave in waves:
            writers = [entries[item].writer_identity for item in wave]
            if len(set(writers)) != len(writers):
                raise MaterializationError("parallel wave reuses one writer identity")

        contract_entry = None
        raw_contract = plan.get("contract_worktree")
        if raw_contract is not None:
            if not isinstance(raw_contract, Mapping) or "contracts" not in self.module_test_argv:
                raise MaterializationError("contract worktree lacks a trusted check")
            writer = raw_contract.get("writer_identity")
            lease_keys = raw_contract.get("lease_keys")
            if not isinstance(writer, str) or not writer or not isinstance(lease_keys, list) or not lease_keys:
                raise MaterializationError("invalid contract worktree")
            paths = self._paths(raw_contract.get("owned_paths"), None, path_owners)
            contract_entry = WorktreeEntry(
                "contracts", None, writer, paths, (), tuple(sorted(lease_keys)),
                self.worktree_root / "contracts",
            )
        return entries, contract_entry, waves

    def _paths(
        self, raw_paths: Any, module_id: str | None, path_owners: Dict[str, str]
    ) -> tuple[str, ...]:
        if not isinstance(raw_paths, list) or not raw_paths or len(set(raw_paths)) != len(raw_paths):
            raise MaterializationError("owned path list is invalid")
        normalized = []
        for raw in raw_paths:
            path = _safe_relative(raw)
            if module_id is not None and not (
                path == f"Modules/{module_id}" or path.startswith(f"Modules/{module_id}/")
            ):
                raise MaterializationError("module worktree path escapes its module")
            for existing, owner in path_owners.items():
                if path == existing or path.startswith(existing + "/") or existing.startswith(path + "/"):
                    raise MaterializationError(
                        f"overlapping worktree paths: {owner} and {module_id or 'contracts'}"
                    )
            path_owners[path] = module_id or "contracts"
            normalized.append(path)
        return tuple(sorted(normalized))

    @staticmethod
    def _waves(entries: Mapping[str, WorktreeEntry]) -> list[list[str]]:
        remaining = set(entries)
        completed: set[str] = set()
        waves: list[list[str]] = []
        while remaining:
            wave = sorted(item for item in remaining if set(entries[item].depends_on).issubset(completed))
            if not wave:
                raise MaterializationError("dependency cycle blocks materialization")
            waves.append(wave)
            completed.update(wave)
            remaining.difference_update(wave)
        return waves

    def materialize(self) -> Mapping[str, Path]:
        if self._head(self.repository) != self.plan["baseline_commit"]:
            raise MaterializationError("plan became stale before materialization")
        supplied_waves = self.plan.get("parallel_waves")
        if supplied_waves is not None and supplied_waves != self.waves:
            raise MaterializationError("plan dependency waves are stale or forged")
        all_entries = list(self.entries.values())
        if self.contract_entry is not None:
            all_entries.append(self.contract_entry)
        for entry in all_entries:
            if entry.path.exists() or entry.path.is_symlink():
                raise MaterializationError("materialized worktree path already exists")
            self._git(
                self.repository, "worktree", "add", "--detach", str(entry.path),
                self.plan["baseline_commit"],
            )
        self._git(
            self.repository, "worktree", "add", "--detach", str(self.integration_path),
            self.plan["baseline_commit"],
        )
        return {entry.entry_id: entry.path for entry in all_entries}

    def _verify_fence(self, entry: WorktreeEntry, lease: Mapping[str, Any]) -> None:
        if (
            not isinstance(lease, Mapping) or lease.get("contract_id") != "worktree.lease/v1"
            or lease.get("plan_id") != self.plan["plan_id"] or lease.get("status") != "ACTIVE"
            or set(entry.lease_keys) - set(lease.get("lock_tokens", {}))
        ):
            raise StaleFence("lease does not cover materialized entry")
        fact = self.fence_verifier(lease)
        expected_hash = _sha256(_canonical_bytes(lease))
        if (
            not isinstance(fact, Mapping) or fact.get("verified") is not True
            or fact.get("fact_id") != lease.get("lease_id")
            or fact.get("fact_sha256") != expected_hash
            or fact.get("plan_id") != self.plan["plan_id"]
            or fact.get("lock_tokens") != lease.get("lock_tokens")
            or fact.get("fencing_token") != lease.get("fencing_token")
        ):
            raise StaleFence("external fencing fact is stale or untrusted")

    def _changed_paths(self, entry: WorktreeEntry) -> tuple[str, ...]:
        tracked = self._git(
            entry.path, "diff", "--name-only", "--no-renames", "-z", "HEAD"
        ).stdout.split(b"\x00")
        untracked = self._git(
            entry.path, "ls-files", "--others", "--exclude-standard", "-z"
        ).stdout.split(b"\x00")
        try:
            changed = sorted({item.decode("utf-8") for item in (*tracked, *untracked) if item})
        except UnicodeDecodeError as exc:
            raise MaterializationError("changed path is not UTF-8") from exc
        if not changed:
            raise MaterializationError("entry has no candidate changes")
        for path in changed:
            normalized = _safe_relative(path)
            if not any(normalized == owned or normalized.startswith(owned + "/") for owned in entry.owned_paths):
                raise MaterializationError("candidate changed a path outside its worktree ownership")
            candidate = entry.path / PurePosixPath(normalized)
            if candidate.is_symlink():
                raise MaterializationError("candidate introduced a symlink")
        return tuple(changed)

    def commit_and_test(self, entry_id: str, lease: Mapping[str, Any]) -> Dict[str, Any]:
        entry = self.contract_entry if entry_id == "contracts" else self.entries.get(entry_id)
        if entry is None:
            raise MaterializationError("unknown materialized entry")
        with self._lock:
            missing = set(entry.depends_on).difference(self._commits)
        if missing:
            raise MaterializationError("dependent entry cannot run before providers pass")
        self._verify_fence(entry, lease)
        changed = self._changed_paths(entry)
        self._git(entry.path, "add", "--", *entry.owned_paths)
        self._git(
            entry.path, "commit", "--no-gpg-sign", "-m",
            "DPS Factory candidate " + entry.entry_id,
        )
        commit = self._head(entry.path)
        self._verify_fence(entry, lease)
        argv = self.module_test_argv[entry.entry_id]
        evidence = self._run_test(entry.path, argv, entry.entry_id, commit, changed)
        self._verify_fence(entry, lease)
        if evidence["status"] != "PASS":
            raise MaterializationError("required worktree test did not PASS")
        with self._lock:
            self._commits[entry.entry_id] = commit
            self._evidence[entry.entry_id] = evidence
            self._leases[entry.entry_id] = dict(lease)
        return dict(evidence)

    def _run_test(
        self, cwd: Path, argv: Sequence[str], scope: str, commit: str,
        changed_paths: Sequence[str],
    ) -> Dict[str, Any]:
        completed = subprocess.run(
            list(argv), cwd=cwd, env=self._environment(), stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, shell=False,
            timeout=60, check=False,
        )
        if len(completed.stdout) + len(completed.stderr) > self.maximum_output_bytes:
            raise MaterializationError("test output exceeded the trusted limit")
        text = (completed.stdout + completed.stderr).decode("utf-8", errors="replace")
        status = "PASS"
        if completed.returncode != 0 or "OK" not in text or any(marker in text for marker in _FORBIDDEN_OUTPUT):
            status = "FAIL"
        material = {
            "scope": scope, "commit": commit, "changed_paths": list(changed_paths),
            "policy_sha256": self.policy_digest,
            "argv_sha256": _sha256(_canonical_bytes(list(argv))),
            "exit_code": completed.returncode,
            "stdout_sha256": _sha256(completed.stdout),
            "stderr_sha256": _sha256(completed.stderr), "status": status,
        }
        material["evidence_sha256"] = _sha256(_canonical_bytes(material))
        return material

    def merge_and_retest(self) -> Dict[str, Any]:
        expected = set(self.entries)
        if self.contract_entry is not None:
            expected.add("contracts")
        with self._lock:
            if set(self._commits) != expected or any(item["status"] != "PASS" for item in self._evidence.values()):
                raise MaterializationError("all entry tests must PASS before merge")
            commits = dict(self._commits)
            leases = dict(self._leases)
        for entry_id in sorted(expected):
            entry = self.contract_entry if entry_id == "contracts" else self.entries[entry_id]
            assert entry is not None
            self._verify_fence(entry, leases[entry_id])
        if self._head(self.integration_path) != self.plan["baseline_commit"]:
            raise MaterializationError("integration worktree is stale")
        ordered: list[str] = []
        if self.contract_entry is not None:
            ordered.append("contracts")
        for wave in self.waves:
            ordered.extend(wave)
        for entry_id in ordered:
            completed = self._git(
                self.integration_path, "merge", "--no-ff", "--no-edit", "--no-gpg-sign",
                commits[entry_id], check=False,
            )
            if completed.returncode != 0:
                self._git(self.integration_path, "merge", "--abort", check=False)
                raise MaterializationError("merge conflict or stale candidate stopped integration")
        merge_commit = self._head(self.integration_path)
        evidence = self._run_test(
            self.integration_path, self.merge_test_argv, "merge-head", merge_commit, (),
        )
        if evidence["status"] != "PASS":
            raise MaterializationError("merge-head required retest did not PASS")
        evidence["merged_entries"] = ordered
        evidence["entry_evidence_sha256"] = _sha256(_canonical_bytes(
            {key: self._evidence[key]["evidence_sha256"] for key in sorted(self._evidence)}
        ))
        return evidence

    def cleanup(self) -> None:
        paths = [entry.path for entry in self.entries.values()]
        if self.contract_entry is not None:
            paths.append(self.contract_entry.path)
        paths.append(self.integration_path)
        for path in paths:
            if path.exists() and path.resolve(strict=True).parent == self.worktree_root:
                self._git(self.repository, "worktree", "remove", "--force", str(path), check=False)
        self._git(self.repository, "worktree", "prune", check=False)
