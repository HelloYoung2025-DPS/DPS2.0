#!/usr/bin/env python3
"""Fail-closed static verifier for the first SessionRunner strangler baseline.

This tool reads repository files only.  Passing it proves that the committed
characterization artifacts still match the byte-preserved legacy source.  It
does not execute C#, Windows, ZennoDroid, ADB, GBrain, or a device.
"""

from __future__ import annotations

import argparse
import ast
import hashlib
import json
import os
import re
import stat
import subprocess
import sys
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Sequence, Set, Tuple


SOURCE_RELATIVE = Path("Modules/SessionRunner.cs")
ARTIFACT_RELATIVE = Path(
    "Modules/legacy-runtime-adapter/operations/strangler"
)
SNAPSHOT_FILE = "sessionrunner-signatures.v1.json"
SIGNATURE_SCHEMA_FILE = "sessionrunner-signature-snapshot.schema.json"
TRACE_SCHEMA_FILE = "golden-trace.schema.json"
LEGACY_BYTES_FILE = "legacy-csharp-bytes.v1.json"
LEGACY_BYTES_SCHEMA_FILE = "legacy-csharp-bytes.schema.json"
LEGACY_BYTES_SCHEMA_VERSION = "dps.legacy-csharp-bytes/v1"
TRUSTED_ANCHOR_SCHEMA_VERSION = "dps.legacy-baseline-trusted-anchor/v1"
TRUSTED_ANCHOR_ENV = "DPS_LEGACY_BASELINE_ANCHOR"
TRUSTED_PROVIDER_ID = "dps-independent-release-authority/v1"
TRUSTED_ISSUER = "DPS_INDEPENDENT_RELEASE_AUTHORITY"
TRUSTED_AUDIENCE = "dps/legacy-runtime-adapter/static-baseline"
EXPECTED_LEGACY_CSHARP_COUNT = 79
EXPECTED_APPROVED_REPAIR_COUNT = 4
EXPECTED_CONTAINMENT_REPAIR_COUNT = 8
TRUSTED_ANCHOR_KEYS = {
    "schema_version",
    "anchor_id",
    "provider_id",
    "audience",
    "issued_at",
    "baseline_commit",
    "baseline_tree",
    "baseline_parents",
    "inventory_scope",
    "inventory_count",
    "inventory_digest_algorithm",
    "inventory_sha256",
    "approved_repair_count",
    "approved_repairs",
    "containment_repair_count",
    "containment_repairs",
    "protected_files",
    "test_requirements",
    "issued_by",
    "limitations",
}
TRUSTED_PROTECTED_FILE_KEYS = {"path", "sha256", "byte_length"}
TRUSTED_TEST_REQUIREMENT_KEYS = {"path", "minimum_test_methods"}
TRUSTED_APPROVED_REPAIR_KEYS = {
    "path",
    "baseline_git_blob",
    "baseline_sha256",
    "baseline_byte_length",
    "working_sha256",
    "working_byte_length",
    "disposition",
    "approval_ref",
}
TRUSTED_PROTECTED_PATHS = {
    "Modules/legacy-runtime-adapter/AGENTS.md",
    "Modules/legacy-runtime-adapter/module.yaml",
    "Modules/legacy-runtime-adapter/operations/strangler/verify_sessionrunner_baseline.py",
    "Modules/legacy-runtime-adapter/operations/strangler/approved-legacy-repairs.policy.v1.json",
    "Modules/legacy-runtime-adapter/operations/strangler/legacy-csharp-bytes.schema.json",
    "Modules/legacy-runtime-adapter/operations/strangler/legacy-csharp-bytes.v1.json",
    "Modules/legacy-runtime-adapter/operations/strangler/sessionrunner-signature-snapshot.schema.json",
    "Modules/legacy-runtime-adapter/operations/strangler/sessionrunner-signatures.v1.json",
    "Modules/legacy-runtime-adapter/operations/strangler/golden-trace.schema.json",
    "Modules/legacy-runtime-adapter/operations/strangler/golden-traces/device-unavailable.synthetic.v1.json",
    "Modules/legacy-runtime-adapter/operations/strangler/golden-traces/split-session-nominal.synthetic.v1.json",
    "Modules/legacy-runtime-adapter/tests/test_legacy_byte_baseline.py",
    "Modules/legacy-runtime-adapter/tests/test_sessionrunner_fail_closed_p0.py",
    "Modules/legacy-runtime-adapter/tests/test_legacy_wrapper_and_orchestrator_p0.py",
}
EXPECTED_TEST_MINIMUMS = {
    "Modules/legacy-runtime-adapter/tests/test_legacy_byte_baseline.py": 21,
    "Modules/legacy-runtime-adapter/tests/test_sessionrunner_fail_closed_p0.py": 9,
    "Modules/legacy-runtime-adapter/tests/test_legacy_wrapper_and_orchestrator_p0.py": 7,
}
EXPECTED_APPROVAL_BINDINGS = {
    "Modules/SessionRunner.cs": (
        "APPROVED_F5_P0_FAIL_CLOSED_REPAIR",
        "F5_P0_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/RuntimeTestRunner.cs": (
        "APPROVED_F1_FALSE_GREEN_REPAIR",
        "F1_FALSE_GREEN_REPAIR",
    ),
    "ZDProjects/TestRunner.cs": (
        "APPROVED_F1_FALSE_GREEN_REPAIR",
        "F1_FALSE_GREEN_REPAIR",
    ),
    "ZDProjects/Tests/Reddit_IntegrationTest.cs": (
        "APPROVED_F1_FALSE_GREEN_REPAIR",
        "F1_FALSE_GREEN_REPAIR",
    ),
}
EXPECTED_CONTAINMENT_BINDINGS = {
    "Modules/Core/SmartOrchestrator.cs": (
        "APPROVED_F5_ORCHESTRATOR_FAIL_CLOSED_REPAIR",
        "F5_ORCHESTRATOR_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/SessionRunner_OwnCode.cs": (
        "APPROVED_F5_WRAPPER_FAIL_CLOSED_REPAIR",
        "F5_WRAPPER_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/DPS_Init_OwnCode.cs": (
        "APPROVED_F5_WRAPPER_FAIL_CLOSED_REPAIR",
        "F5_WRAPPER_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/DPS_DecideAction_OwnCode.cs": (
        "APPROVED_F5_WRAPPER_FAIL_CLOSED_REPAIR",
        "F5_WRAPPER_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/DPS_CheckResult_OwnCode.cs": (
        "APPROVED_F5_WRAPPER_FAIL_CLOSED_REPAIR",
        "F5_WRAPPER_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/DPS_Finalize_OwnCode.cs": (
        "APPROVED_F5_WRAPPER_FAIL_CLOSED_REPAIR",
        "F5_WRAPPER_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/Initializer_OwnCode.cs": (
        "APPROVED_F5_WRAPPER_FAIL_CLOSED_REPAIR",
        "F5_WRAPPER_FAIL_CLOSED_REPAIR",
    ),
    "ZDProjects/Main_OwnCode.cs": (
        "APPROVED_F5_WRAPPER_FAIL_CLOSED_REPAIR",
        "F5_WRAPPER_FAIL_CLOSED_REPAIR",
    ),
}
LEGACY_BYTES_KEYS = {
    "schema_version",
    "baseline_commit",
    "inventory_scope",
    "entry_count",
    "entries",
    "limitations",
}
LEGACY_BYTE_ENTRY_KEYS = {
    "path",
    "baseline_git_blob",
    "baseline_sha256",
    "baseline_byte_length",
    "working_sha256",
    "working_byte_length",
    "disposition",
    "approval_ref",
}
EXPECTED_METHODS = (
    "Run",
    "InitSession",
    "DecideNextAction",
    "EvaluateActionResult",
    "FinalizeSession",
)
SNAPSHOT_KEYS = {
    "schema_version",
    "snapshot_id",
    "baseline_commit",
    "captured_from",
    "canonicalization",
    "evidence_scope",
    "methods",
    "limitations",
}
SOURCE_KEYS = {"path", "git_blob", "sha256", "byte_length"}
METHOD_KEYS = {
    "name",
    "qualified_name",
    "declaration",
    "start_line",
    "signature_sha256",
}
TRACE_KEYS = {
    "schema_version",
    "trace_id",
    "fixture_kind",
    "runtime_observed",
    "evidence_eligibility",
    "source_snapshot_id",
    "scenario",
    "identity_scope",
    "steps",
    "limitations",
}
IDENTITY_KEYS = {"soul_id", "device_binding_id", "platform_account_id"}
STEP_KEYS = {
    "sequence",
    "method",
    "input_fixture",
    "expected_return",
    "expected_state",
}


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def git_blob_sha1(value: bytes) -> str:
    header = b"blob " + str(len(value)).encode("ascii") + b"\0"
    return hashlib.sha1(header + value).hexdigest()


def canonical_declaration(value: str) -> str:
    return re.sub(r"\s+", " ", value).strip()


def trusted_anchor_id(value: Mapping[str, Any]) -> str:
    """Bind every external-provider field except the derived identifier itself."""

    payload = {key: item for key, item in value.items() if key != "anchor_id"}
    canonical = json.dumps(
        payload, ensure_ascii=True, separators=(",", ":"), sort_keys=True
    ).encode("ascii")
    return "legacy_anchor_" + sha256_bytes(canonical)


def count_test_methods(path: Path, errors: List[str]) -> int:
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    except (OSError, UnicodeError, SyntaxError) as exc:
        errors.append("protected test cannot be parsed {0}: {1}".format(path, exc))
        return 0
    return sum(
        1
        for node in ast.walk(tree)
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
        and node.name.startswith("test_")
    )


def load_json(path: Path, label: str, errors: List[str]) -> Any:
    if path.is_symlink() or not path.is_file():
        errors.append("{0} is missing: {1}".format(label, path))
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        errors.append("{0} is unreadable: {1}".format(label, exc))
        return None


def _is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _absolute_has_symlink_component(path: Path) -> bool:
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current = current / part
        if current.is_symlink():
            return True
    return False


def load_trusted_anchor(
    root: Path, anchor_path: Path, errors: List[str]
) -> Optional[Mapping[str, Any]]:
    """Load an externally mounted, read-only anchor outside the candidate tree."""

    if not anchor_path.is_absolute():
        errors.append("trusted anchor path must be absolute")
        return None
    if _absolute_has_symlink_component(anchor_path):
        errors.append("trusted anchor path and parents must not contain symlinks")
        return None
    try:
        resolved = anchor_path.resolve(strict=True)
    except OSError as exc:
        errors.append("trusted anchor is unavailable: {0}".format(exc))
        return None
    if _is_within(resolved, root):
        errors.append("trusted anchor must be outside the repository candidate tree")
        return None
    flags = os.O_RDONLY
    if hasattr(os, "O_NOFOLLOW"):
        flags |= os.O_NOFOLLOW
    try:
        descriptor = os.open(str(resolved), flags)
    except OSError as exc:
        errors.append("trusted anchor cannot be opened safely: {0}".format(exc))
        return None
    try:
        anchor_stat = os.fstat(descriptor)
        parent_stat = resolved.parent.stat()
        if not stat.S_ISREG(anchor_stat.st_mode):
            errors.append("trusted anchor must be a regular file")
            return None
        if anchor_stat.st_mode & 0o222:
            errors.append("trusted anchor must be mounted read-only")
            return None
        verifier_uid = os.geteuid() if hasattr(os, "geteuid") else None
        if verifier_uid is None:
            errors.append("trusted anchor ownership cannot be verified on this platform")
            return None
        if anchor_stat.st_uid == verifier_uid or parent_stat.st_uid == verifier_uid:
            errors.append(
                "trusted anchor and its parent must be controlled by an identity "
                "different from the verifier"
            )
            return None
        if os.access(str(resolved.parent), os.W_OK):
            errors.append(
                "trusted anchor parent must not be writable by the verifier identity"
            )
            return None
        chunks: List[bytes] = []
        while True:
            chunk = os.read(descriptor, 65536)
            if not chunk:
                break
            chunks.append(chunk)
        raw = b"".join(chunks)
    except OSError as exc:
        errors.append("trusted anchor cannot be inspected or read: {0}".format(exc))
        return None
    finally:
        os.close(descriptor)
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        errors.append("trusted anchor is invalid JSON: {0}".format(exc))
        return None
    if not isinstance(value, Mapping):
        errors.append("trusted anchor must be an object")
        return None
    return value


def require_exact_keys(
    value: Any, expected: Set[str], label: str, errors: List[str]
) -> bool:
    if not isinstance(value, Mapping):
        errors.append("{0} must be an object".format(label))
        return False
    actual = set(value)
    missing = sorted(expected - actual)
    extra = sorted(actual - expected)
    if missing:
        errors.append("{0} is missing keys: {1}".format(label, ", ".join(missing)))
    if extra:
        errors.append("{0} has unknown keys: {1}".format(label, ", ".join(extra)))
    return not missing and not extra


def validate_schema_document(
    value: Any, label: str, expected_const: str, errors: List[str]
) -> None:
    if not isinstance(value, Mapping):
        errors.append("{0} must be an object".format(label))
        return
    if value.get("$schema") != "https://json-schema.org/draft/2020-12/schema":
        errors.append("{0} must use JSON Schema draft 2020-12".format(label))
    if value.get("type") != "object" or value.get("additionalProperties") is not False:
        errors.append("{0} must reject unknown top-level fields".format(label))
    properties = value.get("properties")
    if not isinstance(properties, Mapping):
        errors.append("{0} properties are missing".format(label))
        return
    schema_version = properties.get("schema_version")
    if not isinstance(schema_version, Mapping) or schema_version.get("const") != expected_const:
        errors.append("{0} has the wrong schema_version const".format(label))


def _is_legacy_csharp_path(value: str) -> bool:
    path = Path(value)
    if path.is_absolute() or ".." in path.parts or path.suffix.casefold() != ".cs":
        return False
    parts = path.parts
    if not parts:
        return False
    if parts[0] in {"Core", "Extensions", "ZDProjects"}:
        return True
    if parts[0] != "Modules" or len(parts) < 2:
        return False
    if len(parts) == 2:
        return True
    return parts[1] in {"Core", "Decision", "Persona", "Report"}


def _legacy_scope_case_alias(value: str) -> Optional[str]:
    parts = Path(value).parts
    if not parts:
        return None
    root_names = {
        "core": "Core",
        "extensions": "Extensions",
        "modules": "Modules",
        "zdprojects": "ZDProjects",
    }
    expected_root = root_names.get(parts[0].casefold())
    if expected_root is not None and parts[0] != expected_root:
        return parts[0] + " (expected " + expected_root + ")"
    if parts[0] != "Modules" or len(parts) < 2:
        return None
    module_scope_names = {
        "core": "Core",
        "decision": "Decision",
        "persona": "Persona",
        "report": "Report",
    }
    expected_child = module_scope_names.get(parts[1].casefold())
    if expected_child is not None and parts[1] != expected_child:
        return "Modules/" + parts[1] + " (expected Modules/" + expected_child + ")"
    return None


def _register_casefold_components(
    value: str,
    seen: Dict[str, str],
    label: str,
    errors: List[str],
) -> bool:
    collision = False
    parts = Path(value).parts
    for length in range(1, len(parts) + 1):
        prefix = "/".join(parts[:length])
        casefold_prefix = prefix.casefold()
        previous = seen.get(casefold_prefix)
        if previous is not None and previous != prefix:
            errors.append(
                "{0} has a case-insensitive path collision: {1}, {2}".format(
                    label, previous, prefix
                )
            )
            collision = True
        else:
            seen[casefold_prefix] = prefix
    return not collision


def _git_bytes(root: Path, args: Sequence[str], label: str, errors: List[str]) -> bytes:
    try:
        completed = subprocess.run(
            ["git", "-C", str(root), *args],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
    except OSError as exc:
        errors.append("{0} could not run Git: {1}".format(label, exc))
        return b""
    if completed.returncode != 0:
        message = completed.stderr.decode("utf-8", errors="replace").strip()
        errors.append("{0} failed: {1}".format(label, message or "Git exit failure"))
        return b""
    return completed.stdout


def _baseline_git_inventory(
    root: Path, commit: str, errors: List[str]
) -> Dict[str, Tuple[str, bytes]]:
    output = _git_bytes(
        root,
        ["ls-tree", "-r", "-z", commit],
        "legacy baseline inventory",
        errors,
    )
    result: Dict[str, Tuple[str, bytes]] = {}
    casefold_paths: Dict[str, str] = {}
    reported_aliases: Set[str] = set()
    for raw in output.split(b"\0"):
        if not raw:
            continue
        try:
            header, raw_path = raw.split(b"\t", 1)
            mode, object_type, object_id = header.decode("ascii").split(" ")
            path = raw_path.decode("utf-8")
        except (ValueError, UnicodeError):
            errors.append("legacy baseline inventory contains an invalid Git tree entry")
            continue
        alias = _legacy_scope_case_alias(path)
        if alias is not None:
            if alias not in reported_aliases:
                errors.append("legacy baseline has a case-aliased scope path: " + alias)
                reported_aliases.add(alias)
            continue
        if not _is_legacy_csharp_path(path):
            continue
        if not _register_casefold_components(
            path, casefold_paths, "legacy baseline", errors
        ):
            continue
        if mode not in {"100644", "100755"} or object_type != "blob" or re.fullmatch(
            r"[0-9a-f]{40}", object_id
        ) is None:
            errors.append("legacy baseline has a non-regular C# entry: " + path)
            continue
        blob = _git_bytes(root, ["cat-file", "blob", object_id], "legacy blob " + path, errors)
        result[path] = (object_id, blob)
    return result


def _working_legacy_inventory(root: Path, errors: List[str]) -> Set[str]:
    """Enumerate legacy C# paths without relying on case-sensitive globs.

    Directories, symlinks, and other non-regular entries with a C# suffix are
    intentionally included.  The byte validator rejects them later instead of
    silently omitting them from the inventory.
    """

    result: Set[str] = set()
    casefold_paths: Dict[str, str] = {}
    root_names = {
        "core": "Core",
        "extensions": "Extensions",
        "modules": "Modules",
        "zdprojects": "ZDProjects",
    }
    if root.is_dir():
        for path in root.iterdir():
            expected = root_names.get(path.name.casefold())
            if expected is not None and path.name != expected:
                errors.append(
                    "legacy working tree has a case-aliased scope root: {0} "
                    "(expected {1})".format(path.name, expected)
                )

    def register(path: Path) -> None:
        if path.suffix.casefold() != ".cs":
            return
        relative = path.relative_to(root).as_posix()
        if not _register_casefold_components(
            relative, casefold_paths, "legacy working tree", errors
        ):
            return
        result.add(relative)

    def visit(path: Path) -> None:
        if path.is_symlink():
            errors.append(
                "legacy working scope contains a symlink or reparse-like entry: "
                + path.relative_to(root).as_posix()
            )
        register(path)

    for base in ("Core", "Extensions", "ZDProjects"):
        base_path = root / base
        if base_path.is_dir():
            for path in base_path.rglob("*"):
                visit(path)
    modules = root / "Modules"
    if modules.is_dir():
        module_scope_names = {
            "core": "Core",
            "decision": "Decision",
            "persona": "Persona",
            "report": "Report",
        }
        for path in modules.iterdir():
            expected = module_scope_names.get(path.name.casefold())
            if expected is not None and path.name != expected:
                errors.append(
                    "legacy working tree has a case-aliased Modules scope: {0} "
                    "(expected {1})".format(path.name, expected)
                )
            if path.suffix.casefold() == ".cs":
                visit(path)
        for base in ("Core", "Decision", "Persona", "Report"):
            base_path = modules / base
            if base_path.is_dir():
                for path in base_path.rglob("*"):
                    visit(path)
    return result


def _has_symlink_component(root: Path, relative: str) -> bool:
    current = root
    for part in Path(relative).parts:
        current = current / part
        if current.is_symlink():
            return True
    return False


def legacy_inventory_sha256(
    inventory: Mapping[str, Tuple[str, bytes]]
) -> str:
    records = []
    for path in sorted(inventory):
        object_id, value = inventory[path]
        records.append(
            {
                "path": path,
                "baseline_git_blob": object_id,
                "baseline_sha256": sha256_bytes(value),
                "baseline_byte_length": len(value),
            }
        )
    canonical = json.dumps(
        records, ensure_ascii=True, separators=(",", ":"), sort_keys=True
    ).encode("ascii")
    return sha256_bytes(canonical)


def validate_trusted_anchor(
    root: Path,
    anchor: Mapping[str, Any],
    errors: List[str],
) -> Tuple[Mapping[str, Mapping[str, Any]], Dict[str, Tuple[str, bytes]]]:
    if not require_exact_keys(anchor, TRUSTED_ANCHOR_KEYS, "trusted anchor", errors):
        return {}, {}
    if anchor.get("schema_version") != TRUSTED_ANCHOR_SCHEMA_VERSION:
        errors.append("trusted anchor schema_version is unsupported")
    if anchor.get("provider_id") != TRUSTED_PROVIDER_ID:
        errors.append("trusted anchor provider_id is not the fixed release authority")
    if anchor.get("issued_by") != TRUSTED_ISSUER:
        errors.append("trusted anchor issued_by is not the fixed release authority")
    if anchor.get("audience") != TRUSTED_AUDIENCE:
        errors.append("trusted anchor audience is not this verification boundary")
    issued_at = anchor.get("issued_at")
    if not isinstance(issued_at, str) or re.fullmatch(
        r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z",
        issued_at,
    ) is None:
        errors.append("trusted anchor issued_at must be an RFC3339 UTC timestamp")
    anchor_id = anchor.get("anchor_id")
    if not isinstance(anchor_id, str) or re.fullmatch(
        r"legacy_anchor_[0-9a-f]{64}", anchor_id
    ) is None:
        errors.append("trusted anchor_id is invalid")
    elif anchor_id != trusted_anchor_id(anchor):
        errors.append("trusted anchor_id does not bind the complete provider record")
    if anchor.get("inventory_scope") != "TRACKED_LEGACY_CSHARP_PLUS_APPROVED_REPAIRS":
        errors.append("trusted anchor inventory_scope is unsupported")
    if anchor.get("inventory_count") != EXPECTED_LEGACY_CSHARP_COUNT:
        errors.append(
            "trusted anchor must bind exactly {0} legacy C# paths".format(
                EXPECTED_LEGACY_CSHARP_COUNT
            )
        )
    if anchor.get("inventory_digest_algorithm") != "sha256-canonical-json-v1":
        errors.append("trusted anchor inventory digest algorithm is unsupported")
    inventory_sha = anchor.get("inventory_sha256")
    if not isinstance(inventory_sha, str) or re.fullmatch(
        r"[0-9a-f]{64}", inventory_sha
    ) is None:
        errors.append("trusted anchor inventory_sha256 is invalid")

    commit = anchor.get("baseline_commit")
    if not isinstance(commit, str) or re.fullmatch(r"[0-9a-f]{40}", commit) is None:
        errors.append("trusted anchor baseline_commit is invalid")
        return {}, {}
    object_type = _git_bytes(
        root, ["cat-file", "-t", commit], "trusted baseline commit object", errors
    ).decode("ascii", errors="replace").strip()
    if object_type != "commit":
        errors.append("trusted baseline_commit is not a real Git commit object")
    actual_tree = _git_bytes(
        root, ["rev-parse", commit + "^{tree}"], "trusted baseline tree", errors
    ).decode("ascii", errors="replace").strip()
    if anchor.get("baseline_tree") != actual_tree:
        errors.append("trusted anchor baseline_tree does not match Git")
    parent_line = _git_bytes(
        root, ["rev-list", "--parents", "-n", "1", commit], "trusted baseline parents", errors
    ).decode("ascii", errors="replace").strip()
    actual_parents = parent_line.split()[1:] if parent_line else []
    anchor_parents = anchor.get("baseline_parents")
    if not isinstance(anchor_parents, list) or anchor_parents != actual_parents:
        errors.append("trusted anchor baseline parent chain does not match Git")
    try:
        ancestor = subprocess.run(
            ["git", "-C", str(root), "merge-base", "--is-ancestor", commit, "HEAD"],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
        )
        if ancestor.returncode != 0:
            errors.append("trusted baseline_commit is not an ancestor of candidate HEAD")
    except OSError as exc:
        errors.append("trusted baseline ancestry could not run Git: {0}".format(exc))

    inventory = _baseline_git_inventory(root, commit, errors)
    if len(inventory) != EXPECTED_LEGACY_CSHARP_COUNT:
        errors.append(
            "trusted Git baseline inventory must contain exactly {0} paths; found {1}".format(
                EXPECTED_LEGACY_CSHARP_COUNT, len(inventory)
            )
        )
    actual_inventory_sha = legacy_inventory_sha256(inventory)
    if inventory_sha != actual_inventory_sha:
        errors.append("trusted anchor inventory_sha256 does not match Git inventory")

    protected_files = anchor.get("protected_files")
    protected_paths: Set[str] = set()
    if not isinstance(protected_files, list):
        errors.append("trusted anchor protected_files must be an array")
    else:
        for index, record in enumerate(protected_files):
            label = "trusted anchor protected_files[{0}]".format(index)
            if not require_exact_keys(
                record, TRUSTED_PROTECTED_FILE_KEYS, label, errors
            ):
                continue
            assert isinstance(record, Mapping)
            path = record.get("path")
            if not isinstance(path, str) or path not in TRUSTED_PROTECTED_PATHS:
                errors.append(label + " has an unknown protected path")
                continue
            if path in protected_paths:
                errors.append("trusted anchor repeats protected path: " + path)
                continue
            protected_paths.add(path)
            target = root / path
            if _has_symlink_component(root, path) or not target.is_file():
                errors.append("protected path must be a regular non-symlink file: " + path)
                continue
            try:
                target_bytes = target.read_bytes()
            except OSError as exc:
                errors.append("protected path is unreadable {0}: {1}".format(path, exc))
                continue
            if record.get("sha256") != sha256_bytes(target_bytes):
                errors.append("protected path hash differs from external anchor: " + path)
            if record.get("byte_length") != len(target_bytes):
                errors.append("protected path length differs from external anchor: " + path)
    if protected_paths != TRUSTED_PROTECTED_PATHS:
        errors.append(
            "trusted anchor protected path set is incomplete; missing={0}; extra={1}".format(
                ",".join(sorted(TRUSTED_PROTECTED_PATHS - protected_paths)),
                ",".join(sorted(protected_paths - TRUSTED_PROTECTED_PATHS)),
            )
        )

    test_requirements = anchor.get("test_requirements")
    requirement_paths: Set[str] = set()
    if not isinstance(test_requirements, list):
        errors.append("trusted anchor test_requirements must be an array")
    else:
        for index, record in enumerate(test_requirements):
            label = "trusted anchor test_requirements[{0}]".format(index)
            if not require_exact_keys(
                record, TRUSTED_TEST_REQUIREMENT_KEYS, label, errors
            ):
                continue
            assert isinstance(record, Mapping)
            path = record.get("path")
            if not isinstance(path, str) or path not in EXPECTED_TEST_MINIMUMS:
                errors.append(label + " has an unknown required test path")
                continue
            if path in requirement_paths:
                errors.append("trusted anchor repeats test requirement path: " + path)
                continue
            requirement_paths.add(path)
            expected_minimum = EXPECTED_TEST_MINIMUMS[path]
            if record.get("minimum_test_methods") != expected_minimum:
                errors.append(label + " minimum is not the protected expected value")
            actual_count = count_test_methods(root / path, errors)
            if actual_count < expected_minimum:
                errors.append(
                    "protected test method count is below minimum for {0}: {1} < {2}".format(
                        path, actual_count, expected_minimum
                    )
                )
    if requirement_paths != set(EXPECTED_TEST_MINIMUMS):
        errors.append("trusted anchor required test path set is not exact")

    approved = anchor.get("approved_repairs")
    overrides: Dict[str, Mapping[str, Any]] = {}
    if anchor.get("approved_repair_count") != EXPECTED_APPROVED_REPAIR_COUNT:
        errors.append("trusted anchor must bind exactly four approved repairs")
    if not isinstance(approved, list) or len(approved) != EXPECTED_APPROVED_REPAIR_COUNT:
        errors.append("trusted anchor approved_repairs must contain exactly four records")
    else:
        for index, record in enumerate(approved):
            label = "trusted anchor approved_repairs[{0}]".format(index)
            if not require_exact_keys(
                record, TRUSTED_APPROVED_REPAIR_KEYS, label, errors
            ):
                continue
            assert isinstance(record, Mapping)
            path = record.get("path")
            if not isinstance(path, str) or path not in EXPECTED_APPROVAL_BINDINGS:
                errors.append(label + " has an unknown approved repair path")
                continue
            if path in overrides:
                errors.append("trusted anchor repeats approved repair path: " + path)
                continue
            expected_disposition, expected_ref = EXPECTED_APPROVAL_BINDINGS[path]
            if record.get("disposition") != expected_disposition:
                errors.append(label + " has the wrong disposition")
            if record.get("approval_ref") != expected_ref:
                errors.append(label + " has the wrong approval_ref")
            baseline_record = inventory.get(path)
            if baseline_record is None:
                errors.append(label + " path is absent from trusted Git inventory")
            else:
                object_id, baseline_bytes = baseline_record
                expected_old = {
                    "baseline_git_blob": object_id,
                    "baseline_sha256": sha256_bytes(baseline_bytes),
                    "baseline_byte_length": len(baseline_bytes),
                }
                for key, expected in expected_old.items():
                    if record.get(key) != expected:
                        errors.append(label + "." + key + " does not match trusted Git")
            for key in ("working_sha256", "baseline_sha256"):
                value = record.get(key)
                if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None:
                    errors.append(label + "." + key + " is invalid")
            for key in ("working_byte_length", "baseline_byte_length"):
                value = record.get(key)
                if not isinstance(value, int) or isinstance(value, bool) or value < 1:
                    errors.append(label + "." + key + " is invalid")
            overrides[path] = record
    if set(overrides) != set(EXPECTED_APPROVAL_BINDINGS):
        errors.append("trusted anchor approved repair path set is not exact")

    containment = anchor.get("containment_repairs")
    containment_paths: Set[str] = set()
    if anchor.get("containment_repair_count") != EXPECTED_CONTAINMENT_REPAIR_COUNT:
        errors.append("trusted anchor must bind exactly eight containment repairs")
    if (
        not isinstance(containment, list)
        or len(containment) != EXPECTED_CONTAINMENT_REPAIR_COUNT
    ):
        errors.append(
            "trusted anchor containment_repairs must contain exactly eight records"
        )
    else:
        for index, record in enumerate(containment):
            label = "trusted anchor containment_repairs[{0}]".format(index)
            if not require_exact_keys(
                record, TRUSTED_APPROVED_REPAIR_KEYS, label, errors
            ):
                continue
            assert isinstance(record, Mapping)
            path = record.get("path")
            if not isinstance(path, str) or path not in EXPECTED_CONTAINMENT_BINDINGS:
                errors.append(label + " has an unknown containment repair path")
                continue
            if path in containment_paths or path in overrides:
                errors.append("trusted anchor repeats containment repair path: " + path)
                continue
            containment_paths.add(path)
            expected_disposition, expected_ref = EXPECTED_CONTAINMENT_BINDINGS[path]
            if record.get("disposition") != expected_disposition:
                errors.append(label + " has the wrong disposition")
            if record.get("approval_ref") != expected_ref:
                errors.append(label + " has the wrong approval_ref")
            baseline_record = inventory.get(path)
            if baseline_record is None:
                errors.append(label + " path is absent from trusted Git inventory")
            else:
                object_id, baseline_bytes = baseline_record
                expected_old = {
                    "baseline_git_blob": object_id,
                    "baseline_sha256": sha256_bytes(baseline_bytes),
                    "baseline_byte_length": len(baseline_bytes),
                }
                for key, expected in expected_old.items():
                    if record.get(key) != expected:
                        errors.append(label + "." + key + " does not match trusted Git")
            for key in ("working_sha256", "baseline_sha256"):
                value = record.get(key)
                if not isinstance(value, str) or re.fullmatch(r"[0-9a-f]{64}", value) is None:
                    errors.append(label + "." + key + " is invalid")
            for key in ("working_byte_length", "baseline_byte_length"):
                value = record.get(key)
                if not isinstance(value, int) or isinstance(value, bool) or value < 1:
                    errors.append(label + "." + key + " is invalid")
            overrides[path] = record
    if containment_paths != set(EXPECTED_CONTAINMENT_BINDINGS):
        errors.append("trusted anchor containment repair path set is not exact")
    expected_override_paths = set(EXPECTED_APPROVAL_BINDINGS) | set(
        EXPECTED_CONTAINMENT_BINDINGS
    )
    if set(overrides) != expected_override_paths:
        errors.append("trusted anchor full repair path set is not exact")

    limitations = anchor.get("limitations")
    if not isinstance(limitations, list) or len(limitations) < 2:
        errors.append("trusted anchor must state at least two limitations")
    return overrides, inventory


def validate_legacy_byte_baseline(
    root: Path,
    artifact_root: Path,
    anchor: Mapping[str, Any],
    git_inventory: Mapping[str, Tuple[str, bytes]],
    overrides: Mapping[str, Mapping[str, Any]],
    errors: List[str],
) -> Tuple[int, int]:
    schema = load_json(
        artifact_root / LEGACY_BYTES_SCHEMA_FILE, "legacy C# byte schema", errors
    )
    validate_schema_document(
        schema,
        "legacy C# byte schema",
        LEGACY_BYTES_SCHEMA_VERSION,
        errors,
    )
    baseline = load_json(
        artifact_root / LEGACY_BYTES_FILE, "legacy C# byte baseline", errors
    )
    if not require_exact_keys(
        baseline, LEGACY_BYTES_KEYS, "legacy C# byte baseline", errors
    ):
        return 0, 0
    assert isinstance(baseline, Mapping)
    if baseline.get("schema_version") != LEGACY_BYTES_SCHEMA_VERSION:
        errors.append("legacy C# byte baseline schema_version is unsupported")
    if baseline.get("inventory_scope") != "TRACKED_LEGACY_CSHARP_PLUS_APPROVED_REPAIRS":
        errors.append("legacy C# byte baseline inventory_scope is unsupported")
    commit = baseline.get("baseline_commit")
    if not isinstance(commit, str) or re.fullmatch(r"[0-9a-f]{40}", commit) is None:
        errors.append("legacy C# byte baseline commit is invalid")
        return 0, 0
    if commit != anchor.get("baseline_commit"):
        errors.append("legacy C# byte baseline commit differs from external trusted anchor")

    working_inventory = _working_legacy_inventory(root, errors)
    entries = baseline.get("entries")
    if not isinstance(entries, list):
        errors.append("legacy C# byte baseline entries must be an array")
        return len(git_inventory), 0
    if baseline.get("entry_count") != EXPECTED_LEGACY_CSHARP_COUNT:
        errors.append(
            "legacy C# byte baseline entry_count must be exactly {0}".format(
                EXPECTED_LEGACY_CSHARP_COUNT
            )
        )
    if baseline.get("entry_count") != anchor.get("inventory_count"):
        errors.append("legacy C# byte baseline entry_count differs from trusted anchor")
    if baseline.get("entry_count") != len(entries):
        errors.append("legacy C# byte baseline entry_count is incorrect")

    stored_paths: Set[str] = set()
    observed_changed_paths: Set[str] = set()
    changed_count = 0
    for index, entry in enumerate(entries):
        label = "legacy C# byte baseline entries[{0}]".format(index)
        if not require_exact_keys(entry, LEGACY_BYTE_ENTRY_KEYS, label, errors):
            continue
        assert isinstance(entry, Mapping)
        path = entry.get("path")
        if not isinstance(path, str) or not _is_legacy_csharp_path(path):
            errors.append(label + " has an invalid legacy C# path")
            continue
        if path in stored_paths:
            errors.append("legacy C# byte baseline repeats path: " + path)
            continue
        stored_paths.add(path)
        baseline_record = git_inventory.get(path)
        if baseline_record is None:
            errors.append("legacy C# byte baseline path is absent from baseline Git tree: " + path)
            continue
        object_id, baseline_bytes = baseline_record
        expected_baseline = {
            "baseline_git_blob": object_id,
            "baseline_sha256": sha256_bytes(baseline_bytes),
            "baseline_byte_length": len(baseline_bytes),
        }
        for key, expected in expected_baseline.items():
            if entry.get(key) != expected:
                errors.append("{0}.{1} does not match baseline Git bytes".format(label, key))

        working_path = root / path
        if _has_symlink_component(root, path) or not working_path.is_file():
            errors.append("legacy C# working path must be a regular non-symlink file: " + path)
            continue
        try:
            working_bytes = working_path.read_bytes()
        except OSError as exc:
            errors.append("legacy C# working path is unreadable {0}: {1}".format(path, exc))
            continue
        if entry.get("working_sha256") != sha256_bytes(working_bytes):
            errors.append(label + ".working_sha256 does not match working bytes")
        if entry.get("working_byte_length") != len(working_bytes):
            errors.append(label + ".working_byte_length does not match working bytes")

        approved = overrides.get(path)
        if working_bytes == baseline_bytes:
            if entry.get("disposition") != "BYTE_IDENTICAL" or entry.get("approval_ref") is not None:
                errors.append(label + " must be BYTE_IDENTICAL without an approval_ref")
        else:
            changed_count += 1
            observed_changed_paths.add(path)
            if approved is None:
                errors.append("unapproved legacy C# byte change: " + path)
            else:
                expected_working = {
                    "working_sha256": sha256_bytes(working_bytes),
                    "working_byte_length": len(working_bytes),
                }
                for key, actual in expected_working.items():
                    if approved.get(key) != actual:
                        errors.append(
                            "legacy C# working bytes do not match external exact approved repair: "
                            + path
                        )
                if entry.get("disposition") != approved.get("disposition"):
                    errors.append(label + " has the wrong changed-file disposition")
                if entry.get("approval_ref") != approved.get("approval_ref"):
                    errors.append(label + " has the wrong approval_ref")

    baseline_paths = set(git_inventory)
    if stored_paths != baseline_paths:
        errors.append(
            "legacy C# byte baseline path set differs from baseline Git inventory; missing={0}; extra={1}".format(
                ",".join(sorted(baseline_paths - stored_paths)),
                ",".join(sorted(stored_paths - baseline_paths)),
            )
        )
    if working_inventory != baseline_paths:
        errors.append(
            "legacy C# working path set changed; missing={0}; extra={1}".format(
                ",".join(sorted(baseline_paths - working_inventory)),
                ",".join(sorted(working_inventory - baseline_paths)),
            )
        )
    if observed_changed_paths != set(overrides):
        errors.append(
            "legacy C# changed path set is not the exact approved repair set; actual="
            + ",".join(sorted(observed_changed_paths))
        )
    limitations = baseline.get("limitations")
    if not isinstance(limitations, list) or len(limitations) < 2:
        errors.append("legacy C# byte baseline must state at least two limitations")
    return len(git_inventory), changed_count


def extract_declaration(source: str, method: str, errors: List[str]) -> Dict[str, Any]:
    pattern = re.compile(
        r"^\s*(public\s+static\s+string\s+"
        + re.escape(method)
        + r"\s*\(\s*object\s+projectObj\s*,\s*object\s+instanceObj\s*\))\s*$",
        re.MULTILINE,
    )
    matches = list(pattern.finditer(source))
    if len(matches) != 1:
        errors.append(
            "{0} declaration must occur exactly once; found {1}".format(
                method, len(matches)
            )
        )
        return {}
    match = matches[0]
    declaration = canonical_declaration(match.group(1))
    return {
        "name": method,
        "qualified_name": "SessionRunner." + method,
        "declaration": declaration,
        "start_line": source.count("\n", 0, match.start()) + 1,
        "signature_sha256": sha256_bytes(declaration.encode("utf-8")),
    }


def validate_snapshot(
    snapshot: Any,
    source_bytes: bytes,
    anchor: Mapping[str, Any],
    errors: List[str],
) -> str:
    if not require_exact_keys(snapshot, SNAPSHOT_KEYS, "signature snapshot", errors):
        return ""
    assert isinstance(snapshot, Mapping)
    if snapshot.get("schema_version") != "dps.legacy-sessionrunner-signatures/v1":
        errors.append("signature snapshot schema_version is unsupported")
    if snapshot.get("canonicalization") != "collapse-ascii-whitespace-v1":
        errors.append("signature snapshot canonicalization is unsupported")
    if snapshot.get("evidence_scope") != "REPOSITORY_STATIC_ONLY":
        errors.append("signature snapshot must remain static-only evidence")

    captured = snapshot.get("captured_from")
    if require_exact_keys(captured, SOURCE_KEYS, "captured_from", errors):
        assert isinstance(captured, Mapping)
        expected_source = {
            "path": SOURCE_RELATIVE.as_posix(),
            "git_blob": git_blob_sha1(source_bytes),
            "sha256": sha256_bytes(source_bytes),
            "byte_length": len(source_bytes),
        }
        for key, expected in expected_source.items():
            if captured.get(key) != expected:
                errors.append(
                    "captured_from.{0} mismatch: expected {1!r}".format(key, expected)
                )

    source_hash = sha256_bytes(source_bytes)
    expected_snapshot_id = "legacy-sessionrunner:" + source_hash
    if snapshot.get("snapshot_id") != expected_snapshot_id:
        errors.append("signature snapshot_id does not bind the current source bytes")
    baseline = snapshot.get("baseline_commit")
    if not isinstance(baseline, str) or re.fullmatch(r"[0-9a-f]{40}", baseline) is None:
        errors.append("signature snapshot baseline_commit is invalid")
    elif baseline != anchor.get("baseline_commit"):
        errors.append("signature snapshot baseline_commit differs from trusted anchor")

    try:
        source = source_bytes.decode("utf-8-sig")
    except UnicodeError as exc:
        errors.append("legacy SessionRunner source cannot be decoded without mutation: {0}".format(exc))
        return expected_snapshot_id

    actual_methods = {
        name: extract_declaration(source, name, errors) for name in EXPECTED_METHODS
    }
    stored_methods = snapshot.get("methods")
    if not isinstance(stored_methods, list):
        errors.append("signature snapshot methods must be an array")
        return expected_snapshot_id
    if len(stored_methods) != len(EXPECTED_METHODS):
        errors.append("signature snapshot must contain exactly five methods")
    seen: Set[str] = set()
    for index, stored in enumerate(stored_methods):
        label = "signature snapshot methods[{0}]".format(index)
        if not require_exact_keys(stored, METHOD_KEYS, label, errors):
            continue
        assert isinstance(stored, Mapping)
        name = stored.get("name")
        if not isinstance(name, str) or name not in EXPECTED_METHODS:
            errors.append("{0} has an unknown method".format(label))
            continue
        if name in seen:
            errors.append("signature snapshot repeats method {0}".format(name))
        seen.add(name)
        expected = actual_methods.get(name, {})
        for key in METHOD_KEYS:
            if stored.get(key) != expected.get(key):
                errors.append("{0}.{1} does not match source".format(label, key))
    if seen != set(EXPECTED_METHODS):
        errors.append("signature snapshot method set is incomplete")

    limitations = snapshot.get("limitations")
    if not isinstance(limitations, list) or len(limitations) < 2:
        errors.append("signature snapshot must state at least two limitations")
    return expected_snapshot_id


def scalar_map(value: Any) -> bool:
    if not isinstance(value, Mapping):
        return False
    return all(
        item is None or isinstance(item, (str, int, float, bool))
        for item in value.values()
    )


def validate_trace(
    trace: Any,
    path: Path,
    snapshot_id: str,
    trace_ids: Set[str],
    errors: List[str],
) -> None:
    label = "golden trace {0}".format(path.name)
    if not require_exact_keys(trace, TRACE_KEYS, label, errors):
        return
    assert isinstance(trace, Mapping)
    if trace.get("schema_version") != "dps.legacy-sessionrunner-golden-trace/v1":
        errors.append("{0} schema_version is unsupported".format(label))
    trace_id = trace.get("trace_id")
    if not isinstance(trace_id, str) or re.fullmatch(
        r"trace_[a-f0-9]{32}\Z", trace_id
    ) is None:
        errors.append("{0} trace_id is invalid".format(label))
    elif trace_id in trace_ids:
        errors.append("duplicate golden trace id: {0}".format(trace_id))
    else:
        trace_ids.add(trace_id)
    if trace.get("fixture_kind") != "SYNTHETIC_FORMAT_EXAMPLE":
        errors.append("{0} must remain a synthetic format example".format(label))
    if trace.get("runtime_observed") is not False:
        errors.append("{0} cannot claim runtime observation".format(label))
    if trace.get("evidence_eligibility") != []:
        errors.append("{0} cannot satisfy a verification level".format(label))
    if trace.get("source_snapshot_id") != snapshot_id:
        errors.append("{0} references a stale signature snapshot".format(label))
    if not isinstance(trace.get("scenario"), str) or not trace.get("scenario"):
        errors.append("{0} scenario is missing".format(label))

    identity = trace.get("identity_scope")
    if require_exact_keys(identity, IDENTITY_KEYS, label + " identity_scope", errors):
        assert isinstance(identity, Mapping)
        identity_patterns = {
            "soul_id": r"soul_[a-f0-9]{64}\Z",
            "device_binding_id": r"db_[a-f0-9]{32}\Z",
            "platform_account_id": r"pa_[a-f0-9]{32}\Z",
        }
        for key, pattern in identity_patterns.items():
            value = identity.get(key)
            if not isinstance(value, str) or re.fullmatch(
                pattern, value
            ) is None:
                errors.append("{0} {1} is not a canonical opaque identifier".format(label, key))

    steps = trace.get("steps")
    if not isinstance(steps, list) or not steps:
        errors.append("{0} must contain at least one step".format(label))
    else:
        for index, step in enumerate(steps):
            step_label = "{0} steps[{1}]".format(label, index)
            if not require_exact_keys(step, STEP_KEYS, step_label, errors):
                continue
            assert isinstance(step, Mapping)
            if step.get("sequence") != index + 1:
                errors.append("{0} sequence is not contiguous".format(step_label))
            if step.get("method") not in EXPECTED_METHODS:
                errors.append("{0} method is not frozen".format(step_label))
            if not scalar_map(step.get("input_fixture")):
                errors.append("{0} input_fixture must contain scalars only".format(step_label))
            expected_return = step.get("expected_return")
            if not isinstance(expected_return, str) or not expected_return:
                errors.append("{0} expected_return is missing".format(step_label))
            if not scalar_map(step.get("expected_state")):
                errors.append("{0} expected_state must contain scalars only".format(step_label))

    limitations = trace.get("limitations")
    if not isinstance(limitations, list):
        errors.append("{0} limitations must be an array".format(label))
    else:
        joined = " ".join(str(value) for value in limitations).casefold()
        if len(limitations) < 2 or "no windows" not in joined or "device evidence" not in joined:
            errors.append("{0} must deny Windows and device evidence claims".format(label))


def _verification_result(
    status: str,
    errors: List[str],
    anchor_path: Optional[Path],
    snapshot_id: Optional[str] = None,
    legacy_csharp_count: int = 0,
    approved_legacy_change_count: int = 0,
    synthetic_trace_count: int = 0,
) -> Dict[str, Any]:
    return {
        "schema_version": "dps.strangler-baseline-verification/v1",
        "status": status,
        "ok": status == "PASS" and not errors,
        "scope": "STATIC_CHARACTERIZATION_ONLY",
        "source": SOURCE_RELATIVE.as_posix(),
        "trusted_anchor": str(anchor_path) if anchor_path is not None else None,
        "snapshot_id": snapshot_id,
        "method_count": len(EXPECTED_METHODS),
        "synthetic_trace_count": synthetic_trace_count,
        "legacy_csharp_count": legacy_csharp_count,
        "approved_legacy_change_count": approved_legacy_change_count,
        "errors": errors,
        "limitations": [
            "No C# or ZennoDroid runtime was executed.",
            "No Windows, ADB, GBrain, integration, or device evidence is claimed.",
            "PASS requires an independently issued read-only anchor outside the candidate tree.",
        ],
    }


def verify_repository(
    root: Path, trusted_anchor_path: Optional[Path] = None
) -> Dict[str, Any]:
    root = root.resolve()
    if trusted_anchor_path is None:
        return _verification_result(
            "WAITING_EXTERNAL",
            [
                "external trusted anchor is required; set {0} or pass --trusted-anchor".format(
                    TRUSTED_ANCHOR_ENV
                )
            ],
            None,
        )
    if not trusted_anchor_path.exists():
        return _verification_result(
            "WAITING_EXTERNAL",
            ["external trusted anchor is unavailable: " + str(trusted_anchor_path)],
            trusted_anchor_path,
        )

    errors: List[str] = []
    anchor = load_trusted_anchor(root, trusted_anchor_path, errors)
    if anchor is None:
        return _verification_result("FAIL", errors, trusted_anchor_path)
    overrides, trusted_inventory = validate_trusted_anchor(root, anchor, errors)
    if errors:
        return _verification_result("FAIL", errors, trusted_anchor_path)

    artifact_root = root / ARTIFACT_RELATIVE
    signature_schema = load_json(
        artifact_root / SIGNATURE_SCHEMA_FILE, "signature schema", errors
    )
    trace_schema = load_json(artifact_root / TRACE_SCHEMA_FILE, "trace schema", errors)
    validate_schema_document(
        signature_schema,
        "signature schema",
        "dps.legacy-sessionrunner-signatures/v1",
        errors,
    )
    legacy_csharp_count, approved_legacy_change_count = validate_legacy_byte_baseline(
        root,
        artifact_root,
        anchor,
        trusted_inventory,
        overrides,
        errors,
    )
    validate_schema_document(
        trace_schema,
        "trace schema",
        "dps.legacy-sessionrunner-golden-trace/v1",
        errors,
    )

    source_path = root / SOURCE_RELATIVE
    if not source_path.is_file():
        errors.append("legacy SessionRunner source is missing")
        source_bytes = b""
    else:
        try:
            source_bytes = source_path.read_bytes()
        except OSError as exc:
            errors.append("legacy SessionRunner source is unreadable: {0}".format(exc))
            source_bytes = b""

    snapshot = load_json(artifact_root / SNAPSHOT_FILE, "signature snapshot", errors)
    snapshot_id = validate_snapshot(snapshot, source_bytes, anchor, errors)

    trace_directory = artifact_root / "golden-traces"
    trace_paths = sorted(trace_directory.glob("*.json")) if trace_directory.is_dir() else []
    if len(trace_paths) < 2:
        errors.append("at least two synthetic Golden Trace fixtures are required")
    trace_ids: Set[str] = set()
    for trace_path in trace_paths:
        trace = load_json(trace_path, "golden trace", errors)
        validate_trace(trace, trace_path, snapshot_id, trace_ids, errors)

    return _verification_result(
        "PASS" if not errors else "FAIL",
        errors,
        trusted_anchor_path,
        snapshot_id=snapshot_id or None,
        legacy_csharp_count=legacy_csharp_count,
        approved_legacy_change_count=approved_legacy_change_count,
        synthetic_trace_count=len(trace_paths),
    )


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Verify the static SessionRunner strangler baseline."
    )
    parser.add_argument(
        "--root",
        default=str(Path(__file__).resolve().parents[4]),
        help="DPS repository root",
    )
    parser.add_argument(
        "--trusted-anchor",
        default=os.environ.get(TRUSTED_ANCHOR_ENV),
        help=(
            "Absolute path to an independently issued read-only anchor outside the "
            "candidate tree. Defaults to {0}.".format(TRUSTED_ANCHOR_ENV)
        ),
    )
    args = parser.parse_args(argv)
    anchor_path = Path(args.trusted_anchor) if args.trusted_anchor else None
    result = verify_repository(Path(args.root), anchor_path)
    print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
    if result["status"] == "PASS":
        return 0
    if result["status"] == "WAITING_EXTERNAL":
        return 3
    return 1


if __name__ == "__main__":
    sys.exit(main())
