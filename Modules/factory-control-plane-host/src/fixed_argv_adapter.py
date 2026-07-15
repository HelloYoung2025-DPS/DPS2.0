"""Fixed process-bound adapter for provider JSON CLIs.

Profiles are injected by the running deployment.  A workflow request can never
provide argv, cwd, environment, timeout, or an executable.
"""

from __future__ import annotations

import json
import hashlib
import os
import re
import signal
import stat
import subprocess
import threading
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Mapping, Sequence

from factory_control_plane_host import FactoryHostError, canonical_bytes, sha256


_SHELL_EXECUTABLES = frozenset({"sh", "bash", "zsh", "fish", "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh"})
_FORBIDDEN_TOKENS = frozenset({
    "-c", "/c", "--command", "-command", "-encodedcommand",
    "-e", "--eval", "-m", "--module", "--require",
})
_SECRET_ENVIRONMENT_NAME = re.compile(r"(?:KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL)")
_ALLOWED_ENVIRONMENT_NAMES = frozenset({
    "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "LANG", "LC_ALL",
    "NO_COLOR", "PYTHONIOENCODING", "PYTHONUTF8", "TZ",
})
_FILE_ARGUMENT_SUFFIXES = frozenset({
    ".cfg", ".conf", ".ini", ".json", ".py", ".toml", ".yaml", ".yml",
})
MAX_DEPLOYMENT_NODES = 8_192
MAX_DEPLOYMENT_FILES = 4_096
MAX_SINGLE_DEPLOYMENT_FILE_BYTES = 67_108_864
MAX_TOTAL_DEPLOYMENT_BYTES = 268_435_456
MAX_EXECUTABLE_BYTES = 134_217_728


FileIdentity = tuple[int, int, int, int, int, int, int]


def _stream_file_sha256(path: Path, maximum_bytes: int) -> tuple[str, int]:
    digest = hashlib.sha256()
    total = 0
    try:
        with Path(path).open("rb") as stream:
            while True:
                chunk = stream.read(1_048_576)
                if not chunk:
                    break
                total += len(chunk)
                if total > maximum_bytes:
                    raise FactoryHostError("fixed argv deployment file exceeds its byte limit")
                digest.update(chunk)
    except OSError as exc:
        raise FactoryHostError("fixed argv deployment file is unreadable") from exc
    return digest.hexdigest(), total


def _strict_json_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for name, item in pairs:
        if name in value:
            raise ValueError("duplicate JSON property")
        value[name] = item
    return value


def _reject_json_constant(value: str) -> None:
    raise ValueError("non-finite JSON number: " + value)


@dataclass(frozen=True)
class FixedArgvProfile:
    target_module: str
    operation: str
    argv: tuple[str, ...]
    cwd: Path
    timeout_seconds: int
    executable_sha256: str
    cwd_tree_sha256: str
    profile_sha256: str
    environment: tuple[tuple[str, str], ...] = ()
    external_file_sha256s: tuple[tuple[Path, str], ...] = ()
    maximum_output_bytes: int = 1_048_576


def cwd_tree_sha256(cwd: Path) -> str:
    """Hash every immutable deployment file below cwd without following links."""
    root = Path(cwd)
    if not root.is_absolute() or root.is_symlink() or not root.is_dir() or root.resolve(strict=True) != root:
        raise FactoryHostError("fixed argv cwd must be one canonical absolute non-symlink directory")
    files: list[Path] = []
    node_count = 0
    for candidate in root.rglob("*"):
        node_count += 1
        if node_count > MAX_DEPLOYMENT_NODES:
            raise FactoryHostError("fixed argv deployment tree exceeds its node limit")
        if candidate.is_symlink():
            raise FactoryHostError("fixed argv deployment tree must not contain symlinks")
        if candidate.is_dir():
            continue
        if not candidate.is_file():
            raise FactoryHostError("fixed argv deployment tree contains a non-regular file")
        files.append(candidate)
        if len(files) > MAX_DEPLOYMENT_FILES:
            raise FactoryHostError("fixed argv deployment tree exceeds its file-count limit")
    entries: list[dict[str, Any]] = []
    total_bytes = 0
    for candidate in sorted(files, key=lambda item: item.relative_to(root).as_posix()):
        digest, size = _stream_file_sha256(candidate, MAX_SINGLE_DEPLOYMENT_FILE_BYTES)
        total_bytes += size
        if total_bytes > MAX_TOTAL_DEPLOYMENT_BYTES:
            raise FactoryHostError("fixed argv deployment tree exceeds its total-byte limit")
        entries.append({
            "path": candidate.relative_to(root).as_posix(),
            "sha256": digest,
        })
    return sha256(entries)


def fixed_profile_sha256(profile: FixedArgvProfile) -> str:
    material = {
        "target_module": profile.target_module,
        "operation": profile.operation,
        "argv": list(profile.argv),
        "cwd": str(profile.cwd),
        "timeout_seconds": profile.timeout_seconds,
        "executable_sha256": profile.executable_sha256,
        "cwd_tree_sha256": profile.cwd_tree_sha256,
        "environment": [[name, value] for name, value in profile.environment],
        "external_file_sha256s": [[str(path), digest] for path, digest in profile.external_file_sha256s],
        "maximum_output_bytes": profile.maximum_output_bytes,
    }
    return sha256(material)


def _assert_service_immutable(path: Path, *, recursive: bool = False) -> None:
    """Reject any deployment path writable by the Factory service identity."""
    if os.name != "posix" or not hasattr(os, "geteuid") or os.geteuid() == 0:
        raise FactoryHostError(
            "fixed argv requires a non-root POSIX service identity and immutable deployment paths",
        )
    target = Path(path)
    if not target.is_absolute():
        raise FactoryHostError("fixed argv immutable path must be absolute")
    current = Path(target.anchor)
    candidates = [current]
    for part in target.parts[1:]:
        current = current / part
        candidates.append(current)
    if recursive and target.is_dir():
        candidates.extend(sorted(target.rglob("*"), key=lambda item: item.as_posix()))
    seen: set[Path] = set()
    for candidate in candidates:
        if candidate in seen:
            continue
        seen.add(candidate)
        try:
            metadata = candidate.lstat()
        except OSError as exc:
            raise FactoryHostError("fixed argv immutable deployment path is unreadable") from exc
        if stat.S_ISLNK(metadata.st_mode):
            raise FactoryHostError("fixed argv immutable deployment path contains a symlink")
        if metadata.st_uid != 0:
            raise FactoryHostError("fixed argv deployment is not owned by the trusted system identity")
        if metadata.st_mode & (stat.S_IWGRP | stat.S_IWOTH):
            raise FactoryHostError("fixed argv deployment is group/world writable")
        if os.access(candidate, os.W_OK):
            raise FactoryHostError("fixed argv deployment is writable by the Factory service identity")


def _canonical_argument_file(path: Path) -> Path:
    """Return one immutable lexical file path and reject every symlink alias."""
    candidate = Path(path)
    if candidate.is_symlink() or not candidate.is_file():
        raise FactoryHostError(
            "fixed argv path-like argument must be a canonical non-symlink file",
        )
    lexical = Path(os.path.abspath(os.fspath(candidate)))
    try:
        resolved = candidate.resolve(strict=True)
    except OSError as exc:
        raise FactoryHostError("fixed argv path-like argument is unreadable") from exc
    if lexical != resolved:
        raise FactoryHostError(
            "fixed argv path-like argument must be a canonical non-symlink file",
        )
    _assert_service_immutable(resolved)
    return resolved


def _path_identity_no_follow(path: Path, *, directory: bool = False) -> FileIdentity:
    """Read one path identity through a no-follow descriptor and bind its name."""
    candidate = Path(path)
    if not candidate.is_absolute() or not hasattr(os, "O_NOFOLLOW"):
        raise FactoryHostError("fixed argv execution identity requires POSIX no-follow paths")
    flags = os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW
    if directory:
        if not hasattr(os, "O_DIRECTORY"):
            raise FactoryHostError("fixed argv execution identity requires directory descriptors")
        flags |= os.O_DIRECTORY
    try:
        descriptor = os.open(candidate, flags)
    except OSError as exc:
        raise FactoryHostError("fixed argv execution path cannot be opened without following links") from exc
    try:
        descriptor_stat = os.fstat(descriptor)
        name_stat = os.lstat(candidate)
    finally:
        os.close(descriptor)
    expected_kind = stat.S_ISDIR if directory else stat.S_ISREG
    if (
        not expected_kind(descriptor_stat.st_mode)
        or not expected_kind(name_stat.st_mode)
        or stat.S_ISLNK(name_stat.st_mode)
        or descriptor_stat.st_dev != name_stat.st_dev
        or descriptor_stat.st_ino != name_stat.st_ino
    ):
        raise FactoryHostError("fixed argv execution path identity changed while it was opened")
    return (
        descriptor_stat.st_dev,
        descriptor_stat.st_ino,
        descriptor_stat.st_mode,
        descriptor_stat.st_uid,
        descriptor_stat.st_size,
        descriptor_stat.st_mtime_ns,
        descriptor_stat.st_ctime_ns,
    )


def _execution_paths(profile: FixedArgvProfile) -> tuple[tuple[Path, bool], ...]:
    """Return every filesystem object whose identity authorizes this exec."""
    paths: dict[Path, bool] = {
        Path(profile.argv[0]): False,
        Path(profile.cwd): True,
    }
    node_count = 0
    for candidate in profile.cwd.rglob("*"):
        node_count += 1
        if node_count > MAX_DEPLOYMENT_NODES:
            raise FactoryHostError("fixed argv deployment tree exceeds its node limit")
        if candidate.is_symlink():
            raise FactoryHostError("fixed argv deployment tree must not contain symlinks")
        if candidate.is_dir():
            paths[candidate] = True
        elif candidate.is_file():
            paths[candidate] = False
        else:
            raise FactoryHostError("fixed argv deployment tree contains a non-regular file")
    for path, _digest in profile.external_file_sha256s:
        paths[Path(path)] = False
    for token in profile.argv[1:]:
        raw_path = token.split("=", 1)[1] if "=" in token and token.startswith("-") else token
        candidate = Path(raw_path)
        if not candidate.is_absolute():
            candidate = profile.cwd / candidate
        if candidate.exists() and candidate.is_file():
            paths[_canonical_argument_file(candidate)] = False
    for name, value in profile.environment:
        if name == "TZ":
            continue
        candidate = Path(value)
        if not candidate.is_absolute():
            candidate = profile.cwd / candidate
        if candidate.exists() and candidate.is_file():
            paths[_canonical_argument_file(candidate)] = False
    return tuple(sorted(paths.items(), key=lambda item: str(item[0])))


def _snapshot_execution_identities(
    profile: FixedArgvProfile,
) -> tuple[tuple[Path, bool, FileIdentity], ...]:
    return tuple(
        (path, directory, _path_identity_no_follow(path, directory=directory))
        for path, directory in _execution_paths(profile)
    )


def _recheck_execution_identities(
    expected: Sequence[tuple[Path, bool, FileIdentity]],
) -> None:
    for path, directory, identity in expected:
        if _path_identity_no_follow(path, directory=directory) != identity:
            raise FactoryHostError(
                "fixed argv execution path identity changed before process creation",
            )


def _terminate_process_group(process: subprocess.Popen[bytes]) -> None:
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        return
    except OSError:
        process.kill()


def _bounded_process_run(
    profile: FixedArgvProfile,
    environment: Mapping[str, str],
    input_bytes: bytes,
    maximum_output_bytes: int,
    expected_identities: Sequence[tuple[Path, bool, FileIdentity]] | None = None,
) -> subprocess.CompletedProcess[bytes]:
    """Drain both pipes concurrently and kill the whole process group at the cap."""
    if len(input_bytes) > maximum_output_bytes:
        raise FactoryHostError("fixed provider input exceeded bounded size")
    if expected_identities is not None:
        _recheck_execution_identities(expected_identities)
    process = subprocess.Popen(
        list(profile.argv), cwd=profile.cwd, env=dict(environment),
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        shell=False, close_fds=True, start_new_session=True,
    )
    stdout = bytearray()
    stderr = bytearray()
    aggregate_output_bytes = 0
    output_lock = threading.Lock()
    exceeded = threading.Event()
    thread_errors: list[BaseException] = []

    def read_bounded(stream: Any, destination: bytearray) -> None:
        nonlocal aggregate_output_bytes
        try:
            while True:
                chunk = stream.read(65_536)
                if not chunk:
                    return
                with output_lock:
                    remaining = maximum_output_bytes - aggregate_output_bytes
                    if len(chunk) > remaining:
                        if remaining > 0:
                            destination.extend(chunk[:remaining])
                            aggregate_output_bytes += remaining
                        exceeded.set()
                    else:
                        destination.extend(chunk)
                        aggregate_output_bytes += len(chunk)
                if exceeded.is_set():
                    _terminate_process_group(process)
                    return
        except BaseException as exc:  # surfaced after the process is reaped
            thread_errors.append(exc)
            _terminate_process_group(process)
        finally:
            stream.close()

    def write_input() -> None:
        assert process.stdin is not None
        try:
            process.stdin.write(input_bytes)
            process.stdin.flush()
        except BrokenPipeError:
            pass
        except BaseException as exc:
            thread_errors.append(exc)
            _terminate_process_group(process)
        finally:
            try:
                process.stdin.close()
            except BrokenPipeError:
                pass
            except BaseException as exc:
                thread_errors.append(exc)
                _terminate_process_group(process)

    assert process.stdout is not None and process.stderr is not None
    readers = [
        threading.Thread(target=read_bounded, args=(process.stdout, stdout), daemon=True),
        threading.Thread(target=read_bounded, args=(process.stderr, stderr), daemon=True),
    ]
    writer = threading.Thread(target=write_input, daemon=True)
    for thread in readers:
        thread.start()
    writer.start()
    timed_out = False
    try:
        process.wait(timeout=profile.timeout_seconds)
    except subprocess.TimeoutExpired:
        timed_out = True
        _terminate_process_group(process)
        process.wait()
    writer.join(timeout=1)
    for thread in readers:
        thread.join(timeout=1)
    inherited_pipe = any(thread.is_alive() for thread in readers) or writer.is_alive()
    if process.poll() is None or inherited_pipe:
        _terminate_process_group(process)
        if process.poll() is None:
            process.wait()
        writer.join(timeout=1)
        for thread in readers:
            thread.join(timeout=1)
    if timed_out:
        raise FactoryHostError("fixed provider process timed out and was terminated")
    if exceeded.is_set():
        raise FactoryHostError("fixed provider process exceeded bounded output and was terminated")
    if inherited_pipe:
        raise FactoryHostError("fixed provider descendant retained process pipes and was terminated")
    if thread_errors or any(thread.is_alive() for thread in readers) or writer.is_alive():
        raise FactoryHostError("fixed provider process pipes did not close cleanly")
    return subprocess.CompletedProcess(
        list(profile.argv), int(process.returncode), bytes(stdout), bytes(stderr),
    )


class FixedArgvAdapter:
    def __init__(
        self,
        profiles: Sequence[FixedArgvProfile],
        *,
        trusted_policy_sha256: str,
        policy_verifier: Callable[[Sequence[FixedArgvProfile], str], bool],
        maximum_output_bytes: int = 1_048_576,
    ) -> None:
        if not isinstance(trusted_policy_sha256, str) or re.fullmatch(r"[a-f0-9]{64}", trusted_policy_sha256) is None:
            raise FactoryHostError("fixed argv policy digest is invalid")
        if not profiles:
            raise FactoryHostError("fixed argv profiles are missing")
        if maximum_output_bytes < 1024 or maximum_output_bytes > 16_777_216:
            raise FactoryHostError("adapter output limit is invalid")
        self._profiles: dict[tuple[str, str], FixedArgvProfile] = {}
        for profile in profiles:
            key = (profile.target_module, profile.operation)
            if key in self._profiles:
                raise FactoryHostError("fixed argv profile is duplicated")
            self._validate_profile(profile)
            if profile.maximum_output_bytes != maximum_output_bytes:
                raise FactoryHostError(
                    "adapter output limit is not bound by every fixed profile",
                )
            self._profiles[key] = profile
        if policy_verifier(tuple(self._profiles.values()), trusted_policy_sha256) is not True:
            raise FactoryHostError("fixed argv profiles are not externally verified")
        self._policy_sha256 = trusted_policy_sha256
        self._maximum_output = maximum_output_bytes

    @staticmethod
    def _validate_profile(profile: FixedArgvProfile) -> None:
        if (
            not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", profile.target_module)
            or not re.fullmatch(r"[a-z][a-z0-9-]{2,63}", profile.operation)
            or not profile.argv
            or len(profile.argv) > 256
            or profile.timeout_seconds < 1
            or profile.timeout_seconds > 1800
            or profile.maximum_output_bytes < 1024
            or profile.maximum_output_bytes > 16_777_216
        ):
            raise FactoryHostError("fixed argv profile is incomplete")
        if any(not isinstance(token, str) or not token or len(token) > 4096 for token in profile.argv):
            raise FactoryHostError("fixed argv contains an invalid token")
        executable = Path(profile.argv[0])
        if (
            not executable.is_absolute()
            or executable.is_symlink()
            or not executable.is_file()
            or executable.resolve(strict=True) != executable
            or not os.access(executable, os.X_OK)
        ):
            raise FactoryHostError("fixed argv executable must be an absolute executable non-symlink file")
        if executable.name.lower() in _SHELL_EXECUTABLES:
            raise FactoryHostError("shell interpreters are forbidden")
        _assert_service_immutable(executable)
        _assert_service_immutable(profile.cwd, recursive=True)
        with executable.open("rb") as executable_stream:
            executable_prefix = executable_stream.read(2)
        if executable_prefix == b"#!":
            raise FactoryHostError("script executables are forbidden; bind the interpreter and script separately")
        executable_digest, _executable_size = _stream_file_sha256(
            executable, MAX_EXECUTABLE_BYTES,
        )
        if not isinstance(profile.executable_sha256, str) or profile.executable_sha256 != executable_digest:
            raise FactoryHostError("fixed argv executable digest is not externally bound")
        if any(token.lower() in _FORBIDDEN_TOKENS for token in profile.argv[1:]):
            raise FactoryHostError("shell command switches are forbidden")
        if any("\x00" in token or "\n" in token or "\r" in token for token in profile.argv):
            raise FactoryHostError("fixed argv contains control characters")
        if not isinstance(profile.cwd_tree_sha256, str) or profile.cwd_tree_sha256 != cwd_tree_sha256(profile.cwd):
            raise FactoryHostError("fixed argv deployment tree digest is not externally bound")
        if any(
            not isinstance(name, str)
            or re.fullmatch(r"[A-Z_][A-Z0-9_]{0,63}", name) is None
            or name not in _ALLOWED_ENVIRONMENT_NAMES
            or name == "DPS_FACTORY_ADAPTER_POLICY_SHA256"
            or _SECRET_ENVIRONMENT_NAME.search(name) is not None
            or not isinstance(value, str)
            or len(value) > 4096
            or any(control in value for control in ("\x00", "\n", "\r"))
            for name, value in profile.environment
        ):
            raise FactoryHostError("fixed argv environment is invalid or secret-bearing")
        environment_names = [name for name, _value in profile.environment]
        if len(environment_names) != len(set(environment_names)) or tuple(sorted(profile.environment)) != profile.environment:
            raise FactoryHostError("fixed argv environment must be unique and canonically ordered")
        external: dict[Path, str] = {}
        for path, digest in profile.external_file_sha256s:
            candidate = Path(path)
            if (
                not candidate.is_absolute()
                or candidate.is_symlink()
                or not candidate.is_file()
                or candidate.resolve(strict=True) != candidate
                or not isinstance(digest, str)
            ):
                raise FactoryHostError("fixed argv external file is not an exact digest-bound regular file")
            _assert_service_immutable(candidate)
            candidate_digest, _candidate_size = _stream_file_sha256(
                candidate, MAX_SINGLE_DEPLOYMENT_FILE_BYTES,
            )
            if digest != candidate_digest:
                raise FactoryHostError("fixed argv external file is not an exact digest-bound regular file")
            if candidate in external:
                raise FactoryHostError("fixed argv external file binding is duplicated")
            external[candidate] = digest
        if tuple(sorted(profile.external_file_sha256s, key=lambda item: str(item[0]))) != profile.external_file_sha256s:
            raise FactoryHostError("fixed argv external files must be canonically ordered")
        referenced_external: set[Path] = set()
        for token in profile.argv[1:]:
            raw_path = token.split("=", 1)[1] if "=" in token and token.startswith("-") else token
            candidate = Path(raw_path)
            if not candidate.is_absolute():
                candidate = profile.cwd / candidate
            looks_like_file = (
                "/" in raw_path
                or "\\" in raw_path
                or Path(raw_path).suffix.lower() in _FILE_ARGUMENT_SUFFIXES
            )
            if looks_like_file and not candidate.is_file():
                raise FactoryHostError("fixed argv path-like argument is not a bound regular file")
            if candidate.exists() and candidate.is_file():
                resolved = _canonical_argument_file(candidate)
                try:
                    resolved.relative_to(profile.cwd)
                except ValueError:
                    referenced_external.add(resolved)
        for _name, value in profile.environment:
            if _name == "TZ":
                continue
            looks_like_path = (
                "/" in value
                or "\\" in value
                or Path(value).suffix.lower() in _FILE_ARGUMENT_SUFFIXES
            )
            if not looks_like_path:
                continue
            candidate = Path(value)
            if not candidate.is_absolute():
                candidate = profile.cwd / candidate
            if candidate.is_dir() and candidate.resolve(strict=True) == profile.cwd:
                continue
            if not candidate.is_file():
                raise FactoryHostError("fixed argv environment path is not a bound deployment file")
            resolved = _canonical_argument_file(candidate)
            try:
                resolved.relative_to(profile.cwd)
            except ValueError:
                referenced_external.add(resolved)
        if referenced_external != set(external):
            raise FactoryHostError("fixed argv external file bindings do not exactly match referenced files")
        if not isinstance(profile.profile_sha256, str) or profile.profile_sha256 != fixed_profile_sha256(profile):
            raise FactoryHostError("fixed argv full profile digest is not externally bound")

    @property
    def trusted_policy_sha256(self) -> str:
        return self._policy_sha256

    def invoke(self, command: Mapping[str, Any]) -> Mapping[str, Any]:
        if any(name in command for name in ("argv", "command", "shell", "cwd", "environment")):
            raise FactoryHostError("workflow command attempted to override process authority")
        key = (str(command.get("target_module")), str(command.get("operation")))
        profile = self._profiles.get(key)
        if profile is None:
            raise FactoryHostError("no externally verified fixed adapter profile exists")
        expected_identities = _snapshot_execution_identities(profile)
        self._validate_profile(profile)
        _recheck_execution_identities(expected_identities)
        environment = dict(profile.environment)
        environment["DPS_FACTORY_ADAPTER_POLICY_SHA256"] = self._policy_sha256
        try:
            completed = _bounded_process_run(
                profile, environment, canonical_bytes(dict(command)),
                self._maximum_output, expected_identities,
            )
        except OSError as exc:
            raise FactoryHostError("fixed provider process failed before a receipt") from exc
        if completed.returncode != 0:
            raise FactoryHostError("fixed provider process returned non-zero; stderr_sha256=" + sha256(completed.stderr))
        try:
            receipt = json.loads(
                completed.stdout.decode("utf-8"),
                object_pairs_hook=_strict_json_object,
                parse_constant=_reject_json_constant,
            )
        except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as exc:
            raise FactoryHostError("fixed provider process did not return one JSON receipt") from exc
        if not isinstance(receipt, Mapping):
            raise FactoryHostError("fixed provider receipt is not an object")
        return dict(receipt)


__all__ = [
    "FixedArgvAdapter", "FixedArgvProfile", "cwd_tree_sha256",
    "fixed_profile_sha256",
]
