#!/usr/bin/env python3
"""Run the unique unsigned whole-repository Contract/Integration candidate gate.

This runner discovers required suites from registered ``module.yaml`` files,
executes only hardened fixed argv, and emits diagnostic candidate evidence.  It
never signs evidence and never issues a formal DPS verification level.
"""

from __future__ import annotations

import sys

# Direct script execution normally prepends Tools/ci to sys.path before this
# file imports anything. Remove that untrusted import root before importing the
# standard library or locked third-party packages. Local DPS modules are loaded
# in one bounded window below and the directory is removed again immediately.
if __name__ == "__main__" and sys.path:
    del sys.path[0]

import argparse
import importlib
import datetime as dt
import hashlib
import importlib.metadata
import importlib.util
import json
import os
import platform
import re
import shlex
import stat
import subprocess
import time
import urllib.parse
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Dict, Iterable, List, Mapping, Optional, Sequence, Tuple


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = Path(__file__).resolve().parent


def _preload_locked_dependency(name: str) -> Any:
    """Import third-party code without allowing repository-local shadow modules."""

    original_path = list(sys.path)
    interpreter_root = Path(sys.prefix).resolve()
    clean_path: List[str] = []
    for raw in original_path:
        try:
            resolved = Path(raw or os.getcwd()).resolve()
        except (OSError, RuntimeError):
            continue
        try:
            resolved.relative_to(ROOT)
            inside_repository = True
        except ValueError:
            inside_repository = False
        try:
            resolved.relative_to(interpreter_root)
            inside_interpreter = True
        except ValueError:
            inside_interpreter = False
        if not inside_repository or inside_interpreter:
            clean_path.append(raw)
    existing = sys.modules.get(name)
    if existing is not None:
        origin = getattr(existing, "__file__", None)
        if origin is None:
            raise RuntimeError("locked dependency has no import origin: " + name)
        try:
            Path(origin).resolve().relative_to(interpreter_root)
        except (OSError, RuntimeError, ValueError) as exc:
            raise RuntimeError(
                "repository-local or foreign dependency was preloaded: " + name
            ) from exc
        return existing
    try:
        sys.path[:] = clean_path
        module = importlib.import_module(name)
    finally:
        sys.path[:] = original_path
    origin = getattr(module, "__file__", None)
    if origin is None:
        raise RuntimeError("locked dependency has no import origin: " + name)
    try:
        Path(origin).resolve().relative_to(interpreter_root)
    except (OSError, RuntimeError, ValueError) as exc:
        raise RuntimeError(
            "locked dependency did not load from the active interpreter: " + name
        ) from exc
    return module


_LOCKED_DEPENDENCIES = {
    name: _preload_locked_dependency(name)
    for name in ("attrs", "jsonschema", "psycopg", "referencing", "rpds", "yaml")
}
_LOCKED_DEPENDENCY_ORIGINS = {
    name: str(Path(module.__file__).resolve())
    for name, module in _LOCKED_DEPENDENCIES.items()
}
if str(CI_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(CI_DIRECTORY))

from phase0 import (  # noqa: E402
    CONTRACT_COMPATIBILITY_MODES,
    REQUIRED_DOTNET_SDK,
    REQUIRED_PYTHON,
    RUNNABLE_CONTRACT_MODE,
    Phase0Error,
    _changed_paths,
    discover_registered_module_dirs,
    load_json_compatible_yaml,
    manifest_module_id,
    resolve_instruction_receipt,
    run_command,
    sha256_file,
    sha256_text,
    stable_json,
    validate_governance,
    validate_instruction_receipt,
    validate_json_schema,
    workspace_digest,
)
from run_phase0_gate import (  # noqa: E402
    EvidencePublication,
    PUBLICATION_MARKER_SUFFIX,
    TrustedInvocation,
    TrustedSuitePlan,
    _new_publication_run_id,
    _load_committed_json_object_with_sha,
    _publication_claim_path,
    _publication_marker_path,
    _trusted_test_environment_scope,
    execute_manifest_suite,
    parse_manifest_suite_command,
    write_evidence,
)
while str(CI_DIRECTORY) in sys.path:
    sys.path.remove(str(CI_DIRECTORY))


GATE_NAME = "DPS_CONTRACT_INTEGRATION_CANDIDATE"
SCHEMA_VERSION = "dps.candidate-gate-evidence/v1"
POLICY_PATH = Path("governance/policies/candidate-test-policy.yaml")
POLICY_SCHEMA_PATH = Path("governance/schemas/candidate-test-policy.schema.json")
EVIDENCE_SCHEMA_PATH = Path("governance/schemas/candidate-gate-evidence.schema.json")
CANDIDATE_TRUST_PATHS = (
    POLICY_PATH.as_posix(),
    ".editorconfig",
    ".gitattributes",
    ".github/CODEOWNERS",
    ".github/workflows/static-ci.yml",
    ".gitignore",
    ".node-version",
    ".powershell-version",
    ".python-version",
    "AGENTS.md",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Packages.props",
    "Dps.slnx",
    "NuGet.Config",
    POLICY_SCHEMA_PATH.as_posix(),
    EVIDENCE_SCHEMA_PATH.as_posix(),
    "governance/schemas/agents-frontmatter.schema.json",
    "governance/schemas/module-manifest.schema.json",
    "governance/schemas/module-manifest.v1.schema.json",
    "governance/schemas/phase0-instruction-receipt.schema.json",
    "governance/schemas/phase0-test-evidence.schema.json",
    "governance/schemas/release-bom.schema.json",
    "governance/schemas/release-bom.v1.auth.json",
    "governance/modules/module-catalog.yaml",
    "governance/modules/dependency-graph.yaml",
    "governance/modules/compatibility.yaml",
    "governance/policies/risk-policy.yaml",
    "governance/policies/compatibility-policy.yaml",
    "governance/policies/deployed-release-trust-policy.v1.json",
    "governance/verification/release-bom.canonical-number.v1.corpus.json",
    "governance/verification/release-bom.canonical-string.v1.corpus.json",
    "governance/verification/release-bom.signature.v1.corpus.json",
    "governance/verification/f9-scale-input.v1.schema.json",
    "governance/verification/f9-scale-input.v2.schema.json",
    "Modules/control-plane-host/tests/Dps.ControlPlaneHost.Tests/ActiveReleaseBindingAuthorityTests.cs",
    "Modules/control-plane-host/tests/Dps.ControlPlaneHost.Tests/Dps.ControlPlaneHost.Tests.csproj",
    "Tests/ci/test_candidate_bom_validator.py",
    "Tests/ci/test_candidate_bom_validator_e2e.py",
    "Tests/ci/test_candidate_gate.py",
    "Tests/ci/test_candidate_policy.py",
    "Tests/ci/test_manifest_schema_subset_evaluator.py",
    "Tests/ci/test_module_impact.py",
    "Tests/ci/test_r0b_receipt_migration_dual_run.py",
    "Tests/ci/test_release_bom_signer_contract.py",
    "Tests/ci/test_release_bom_field_set_dual_pin.py",
    "Tests/ci/test_phase0_gate.py",
    "Tests/ci/fixtures/r0c_release_binding_compat/corpus.json",
    "Tests/ci/fixtures/r0c_release_binding_compat/trust-policy.json",
    "Tests/ci/fixtures/r0c_release_binding_compat/token-preimages.json",
    "Tests/ci/fixtures/r0c_release_binding_compat/bundle/candidate-bom.json",
    "Tests/ci/fixtures/r0c_release_binding_compat/bundle/previous-signed-bom.json",
    "Tests/ci/fixtures/r0c_release_binding_compat/bundle/previous-stable-bom.json",
    "Tests/ci/fixtures/r0c_native_stop_trust_e2e/generate_unsigned_receipt.py",
    "Tools/ci/candidate_bom_validator.py",
    "Tools/ci/run_candidate_gate.py",
    "Tools/ci/run_phase0_gate.py",
    "Tools/ci/phase0.py",
    "Tools/ci/validate_repo.py",
    "Tools/verification/external_gate.py",
    "Tools/verification/tests/test_external_gate.py",
    "global.json",
    "package-lock.json",
    "package.json",
    "requirements-ci.in",
    "scripts/adb-pinned.sh",
    "scripts/bootstrap-ci-python.sh",
    "scripts/dotnet-pinned.sh",
    "scripts/pwsh-pinned.sh",
    "scripts/release.sh",
    "scripts/start-test-postgres.sh",
    "scripts/stop-test-postgres.sh",
    "requirements-ci.txt",
    "toolchain.lock.json",
)
DEFAULT_CANDIDATE_RUNS_ROOT = Path("Reports/ci/candidate-runs")
ALLOWED_RUNTIME_ENVIRONMENT = (
    "DPS_TEST_POSTGRES",
    "DPS_TEST_POSTGRES_ADMIN_URI",
    "DPS_TEST_POSTGRES_RUNTIME_URI",
    "DPS_TEST_POSTGRES_URI",
    "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE",
    "DPS_PSQL",
)
PHASE0_PREREQUISITE_RUNTIME_ENVIRONMENT = (
    "DPS_LEGACY_BASELINE_ANCHOR",
)
POSTGRES_CONNECTION_ENVIRONMENT = (
    "DPS_TEST_POSTGRES",
    "DPS_TEST_POSTGRES_ADMIN_URI",
    "DPS_TEST_POSTGRES_RUNTIME_URI",
    "DPS_TEST_POSTGRES_URI",
)
EXPECTED_LEVEL = {
    "contract": "CONTRACT_VERIFIED",
    "integration": "INTEGRATION_VERIFIED",
}
LEVEL_RANK = {
    "REPOSITORY_STATIC_VERIFIED": 1,
    "CONTRACT_VERIFIED": 2,
    "INTEGRATION_VERIFIED": 3,
    "WINDOWS_VERIFIED": 4,
    "DEVICE_VERIFIED": 5,
    "CANARY_VERIFIED": 6,
    "SCALE_VERIFIED": 7,
}
EXPECTED_POSTGRES_VERSION_NUM = "180004"
MAX_EMBEDDED_LOG_CHARS = 120000
GIT_EXECUTABLE = Path("/usr/bin/git")
LOCKED_IMPORT_NAMES = tuple(sorted(_LOCKED_DEPENDENCIES))


@dataclass(frozen=True)
class ContractPolicy:
    module_id: str
    suite_id: str
    test_target: str
    test_category: str
    minimum_executed_tests: int


@dataclass(frozen=True)
class IntegrationPolicy:
    module_id: str
    suite_id: str
    evidence_kind: str
    test_target: str
    test_category: str
    required_environment: Tuple[str, ...]
    minimum_executed_tests: int


@dataclass(frozen=True)
class CandidatePolicy:
    contract: Mapping[Tuple[str, str], ContractPolicy]
    integration: Mapping[Tuple[str, str], IntegrationPolicy]
    sha256: str


@dataclass(frozen=True)
class CandidateSuite:
    module_id: str
    module_root: Path
    manifest_path: Path
    agents_path: Path
    manifest_sha256: str
    agents_sha256: str
    suite: Mapping[str, Any]
    evidence_kind: str
    integration_policy: Optional[IntegrationPolicy]


@dataclass(frozen=True)
class Inventory:
    module_ids: Tuple[str, ...]
    suites: Tuple[CandidateSuite, ...]
    errors: Tuple[str, ...]
    modules_without_contract: Tuple[str, ...]
    modules_without_integration: Tuple[str, ...]
    public_contract_owner_count: int
    public_contract_count: int
    public_contract_inventory: Tuple[Mapping[str, Any], ...]
    public_contract_inventory_sha256: str
    manifest_inventory_sha256: str


def _utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def _within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _safe_repo_target(root: Path, raw: str, label: str) -> Path:
    if (
        not isinstance(raw, str)
        or not raw
        or Path(raw).is_absolute()
        or "\\" in raw
        or any(ord(value) < 32 for value in raw)
        or any(part in ("", ".", "..") for part in Path(raw).parts)
    ):
        raise Phase0Error(label + " must be a safe repository-relative path")
    candidate = root / raw
    current = root.resolve()
    for part in Path(raw).parts:
        current = current / part
        if current.is_symlink():
            raise Phase0Error(label + " may not traverse a symlink: " + raw)
    try:
        resolved = candidate.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error(label + " is missing: {0}: {1}".format(raw, exc))
    if not _within(resolved, root.resolve()):
        raise Phase0Error(label + " escapes the repository: " + raw)
    if not resolved.is_file() and not resolved.is_dir():
        raise Phase0Error(label + " is not a file or directory: " + raw)
    return resolved


def _load_json_object_with_sha(path: Path, label: str) -> Tuple[Dict[str, Any], str]:
    descriptor: Optional[int] = None
    try:
        before = os.lstat(path)
        if not stat.S_ISREG(before.st_mode):
            raise Phase0Error(label + " is missing or unsafe")
        flags = os.O_RDONLY
        if hasattr(os, "O_NOFOLLOW"):
            flags |= os.O_NOFOLLOW
        descriptor = os.open(path, flags)
        opened = os.fstat(descriptor)
        if not stat.S_ISREG(opened.st_mode) or (
            opened.st_dev,
            opened.st_ino,
        ) != (before.st_dev, before.st_ino):
            raise Phase0Error(label + " changed during safe open")
        chunks: List[bytes] = []
        total = 0
        while True:
            chunk = os.read(descriptor, 1024 * 1024)
            if not chunk:
                break
            total += len(chunk)
            if total > 64 * 1024 * 1024:
                raise Phase0Error(label + " exceeds the 64 MiB evidence limit")
            chunks.append(chunk)
        final = os.fstat(descriptor)
        if (final.st_dev, final.st_ino, final.st_size) != (
            opened.st_dev,
            opened.st_ino,
            total,
        ):
            raise Phase0Error(label + " changed while it was being read")
        payload = b"".join(chunks)
        value = json.loads(payload.decode("utf-8-sig"))
    except Phase0Error:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise Phase0Error("invalid {0}: {1}".format(label, exc))
    finally:
        if descriptor is not None:
            os.close(descriptor)
    if not isinstance(value, dict):
        raise Phase0Error(label + " must contain an object")
    return value, hashlib.sha256(payload).hexdigest()


def _load_json_object(path: Path, label: str) -> Dict[str, Any]:
    value, _ = _load_json_object_with_sha(path, label)
    return value


def load_candidate_policy(root: Path) -> CandidatePolicy:
    policy_path = root / POLICY_PATH
    schema_path = root / POLICY_SCHEMA_PATH
    policy = _load_json_object(policy_path, "candidate test policy")
    schema = _load_json_object(schema_path, "candidate test policy schema")
    errors = validate_json_schema(policy, schema)
    if errors:
        raise Phase0Error("candidate test policy violates schema: " + "; ".join(errors))

    contract: Dict[Tuple[str, str], ContractPolicy] = {}
    integration: Dict[Tuple[str, str], IntegrationPolicy] = {}
    targets: Dict[Tuple[str, str], Tuple[str, str]] = {}
    for item in policy["contractSuites"]:
        key = (item["moduleId"], item["suiteId"])
        if key in contract:
            raise Phase0Error("duplicate Contract policy key: " + ":".join(key))
        target = _safe_repo_target(root, item["testTarget"], "policy testTarget")
        if not target.is_file():
            raise Phase0Error("Contract policy target must be a file")
        normalized = target.relative_to(root.resolve()).as_posix()
        target_key = (normalized, item["testCategory"])
        if target_key in targets:
            raise Phase0Error("candidate policy repeats testTarget/category: " + normalized)
        targets[target_key] = key
        contract[key] = ContractPolicy(
            key[0],
            key[1],
            normalized,
            item["testCategory"],
            item["minimumExecutedTests"],
        )

    for item in policy["integrationSuites"]:
        key = (item["moduleId"], item["suiteId"])
        if key in integration:
            raise Phase0Error("duplicate Integration policy key: " + ":".join(key))
        target = _safe_repo_target(root, item["testTarget"], "policy testTarget")
        normalized = target.relative_to(root.resolve()).as_posix()
        target_key = (normalized, item["testCategory"])
        if target_key in targets:
            raise Phase0Error("candidate policy repeats testTarget/category: " + normalized)
        targets[target_key] = key
        integration[key] = IntegrationPolicy(
            module_id=key[0],
            suite_id=key[1],
            evidence_kind=item["evidenceKind"],
            test_target=normalized,
            test_category=item["testCategory"],
            required_environment=tuple(sorted(item["requiredEnvironment"])),
            minimum_executed_tests=item["minimumExecutedTests"],
        )
    return CandidatePolicy(contract, integration, sha256_file(policy_path))


def _provided_public_contracts(manifest: Mapping[str, Any]) -> List[Mapping[str, Any]]:
    contracts = manifest.get("contracts")
    provided = contracts.get("provided") if isinstance(contracts, Mapping) else None
    if not isinstance(provided, list):
        return []
    active: List[Mapping[str, Any]] = []
    for index, item in enumerate(provided):
        if not isinstance(item, Mapping):
            raise Phase0Error(
                "contracts.provided[{0}] must be an object with an explicit mode".format(
                    index
                )
            )
        mode = item.get("mode")
        if not isinstance(mode, str) or mode not in CONTRACT_COMPATIBILITY_MODES:
            rendered = "missing" if mode is None else repr(mode)
            raise Phase0Error(
                "contracts.provided[{0}] has unknown or missing compatibility mode: {1}".format(
                    index, rendered
                )
            )
        if mode == RUNNABLE_CONTRACT_MODE:
            active.append(item)

    module = manifest.get("module")
    if isinstance(module, Mapping) and module.get("lifecycle") == "retired":
        return []
    return active


def _minimum_requires_integration(manifest: Mapping[str, Any]) -> bool:
    module = manifest.get("module")
    if isinstance(module, Mapping) and module.get("lifecycle") == "retired":
        return False
    gates = manifest.get("deviceGates")
    level = gates.get("minimumVerification") if isinstance(gates, Mapping) else None
    return LEVEL_RANK.get(str(level), 0) >= LEVEL_RANK["INTEGRATION_VERIFIED"]


def discover_candidate_inventory(
    root: Path, requested_level: str, policy: CandidatePolicy
) -> Inventory:
    selected_types = (
        ("contract",)
        if requested_level == "CONTRACT_VERIFIED"
        else ("contract", "integration")
    )
    errors: List[str] = []
    suites: List[CandidateSuite] = []
    suite_ids: Dict[str, str] = {}
    module_ids: List[str] = []
    missing_contract: List[str] = []
    missing_integration: List[str] = []
    public_owner_count = 0
    inventory_digest: List[Dict[str, Any]] = []
    selected_integration_keys: set[Tuple[str, str]] = set()
    selected_contract_keys: set[Tuple[str, str]] = set()
    public_contract_inventory: List[Dict[str, Any]] = []

    module_roots = discover_registered_module_dirs(root)
    for module_root in module_roots:
        module_id = module_root.name
        manifest_path = module_root / "module.yaml"
        agents_path = module_root / "AGENTS.md"
        if (
            not manifest_path.is_file()
            or manifest_path.is_symlink()
            or not agents_path.is_file()
            or agents_path.is_symlink()
        ):
            errors.append(module_id + ": Manifest or AGENTS is missing/unsafe")
            continue
        try:
            manifest = load_json_compatible_yaml(manifest_path)
        except Phase0Error as exc:
            errors.append(module_id + ": " + str(exc))
            continue
        if manifest_module_id(manifest) != module_id:
            errors.append(module_id + ": manifest module id does not match directory")
            continue
        module_ids.append(module_id)
        manifest_hash = sha256_file(manifest_path)
        agents_hash = sha256_file(agents_path)
        tests = manifest.get("tests")
        declared = tests.get("suites") if isinstance(tests, Mapping) else None
        if not isinstance(declared, list) or not declared:
            errors.append(module_id + ": tests.suites is missing or empty")
            declared = []

        local_ids: set[str] = set()
        exact_contract: List[Mapping[str, Any]] = []
        exact_integration: List[Mapping[str, Any]] = []
        digest_suites: List[Dict[str, Any]] = []
        for suite in declared:
            if not isinstance(suite, Mapping):
                errors.append(module_id + ": suite entry must be an object")
                continue
            suite_id = suite.get("id")
            if not isinstance(suite_id, str) or not suite_id:
                errors.append(module_id + ": suite id is missing")
                continue
            if suite_id in local_ids:
                errors.append(module_id + ": duplicate suite id " + suite_id)
            local_ids.add(suite_id)
            previous = suite_ids.get(suite_id)
            if previous is not None and previous != module_id:
                errors.append(
                    "global duplicate suite id {0}: {1}, {2}".format(
                        suite_id, previous, module_id
                    )
                )
            suite_ids[suite_id] = module_id
            digest_suites.append(
                {
                    "id": suite_id,
                    "type": suite.get("type"),
                    "required": suite.get("required"),
                    "evidenceLevel": suite.get("evidenceLevel"),
                    "command_sha256": sha256_text(str(suite.get("command"))),
                }
            )

            test_type = suite.get("type")
            evidence_level = suite.get("evidenceLevel")
            required = suite.get("required") is True
            relevant_type = test_type in selected_types
            relevant_level = evidence_level in {
                EXPECTED_LEVEL[value] for value in selected_types
            }
            if relevant_type or relevant_level:
                if test_type not in EXPECTED_LEVEL:
                    errors.append(suite_id + ": target evidence level has wrong test type")
                    continue
                expected = EXPECTED_LEVEL[test_type]
                if evidence_level != expected:
                    errors.append(
                        suite_id + ": evidenceLevel must be exactly " + expected
                    )
                    continue
                if not required:
                    errors.append(suite_id + ": candidate suite must be required=true")
                    continue
                if test_type not in selected_types:
                    continue

                integration_policy = None
                evidence_kind = "CONTRACT"
                key = (module_id, suite_id)
                if test_type == "contract":
                    exact_contract.append(suite)
                    selected_contract_keys.add(key)
                    if key not in policy.contract:
                        errors.append(
                            suite_id + ": Contract suite lacks trusted policy binding"
                        )
                else:
                    exact_integration.append(suite)
                    selected_integration_keys.add(key)
                    integration_policy = policy.integration.get(key)
                    if integration_policy is None:
                        errors.append(
                            suite_id + ": Integration suite lacks trusted policy binding"
                        )
                        evidence_kind = "INVENTORY"
                    else:
                        evidence_kind = integration_policy.evidence_kind
                suites.append(
                    CandidateSuite(
                        module_id=module_id,
                        module_root=module_root,
                        manifest_path=manifest_path,
                        agents_path=agents_path,
                        manifest_sha256=manifest_hash,
                        agents_sha256=agents_hash,
                        suite=suite,
                        evidence_kind=evidence_kind,
                        integration_policy=integration_policy,
                    )
                )

        try:
            provided_public = _provided_public_contracts(manifest)
        except Phase0Error as exc:
            errors.append(module_id + ": " + str(exc))
            provided_public = []
        if provided_public:
            public_owner_count += 1
            if not exact_contract:
                missing_contract.append(module_id)
            for declaration in provided_public:
                source = declaration.get("source")
                contract_id = declaration.get("contractId")
                major = declaration.get("major")
                if not isinstance(source, str) or not isinstance(contract_id, str):
                    errors.append(module_id + ": public contract declaration is incomplete")
                    continue
                try:
                    source_path = _safe_repo_target(root, source, "public contract source")
                except Phase0Error as exc:
                    errors.append(module_id + ": " + str(exc))
                    continue
                if not source_path.is_file():
                    errors.append(module_id + ": public contract source is not a file")
                    continue
                public_contract_inventory.append(
                    {
                        "owner_module": module_id,
                        "contract_id": contract_id,
                        "major": major,
                        "source": source_path.relative_to(root.resolve()).as_posix(),
                        "source_sha256": sha256_file(source_path),
                    }
                )
        if requested_level == "INTEGRATION_VERIFIED" and _minimum_requires_integration(manifest):
            if not exact_integration:
                missing_integration.append(module_id)
        inventory_digest.append(
            {
                "module_id": module_id,
                "manifest_sha256": manifest_hash,
                "agents_sha256": agents_hash,
                "suites": sorted(digest_suites, key=lambda value: str(value["id"])),
            }
        )

    if not suites:
        errors.append("candidate inventory selected zero required suites")
    if requested_level == "INTEGRATION_VERIFIED":
        orphaned = sorted(set(policy.integration).difference(selected_integration_keys))
        if orphaned:
            errors.append(
                "Integration policy contains suites absent from manifest inventory: "
                + ", ".join("{0}:{1}".format(*key) for key in orphaned)
            )
    orphaned_contract = sorted(set(policy.contract).difference(selected_contract_keys))
    if orphaned_contract:
        errors.append(
            "Contract policy contains suites absent from manifest inventory: "
            + ", ".join("{0}:{1}".format(*key) for key in orphaned_contract)
        )
    for module_id in missing_contract:
        errors.append(module_id + ": public contract owner lacks required Contract suite")
    for module_id in missing_integration:
        errors.append(
            module_id
            + ": minimumVerification requires a required Integration suite"
        )
    contract_inventory = tuple(
        sorted(
            public_contract_inventory,
            key=lambda value: (
                str(value["owner_module"]),
                str(value["contract_id"]),
                int(value["major"]),
            ),
        )
    )
    return Inventory(
        module_ids=tuple(sorted(module_ids)),
        suites=tuple(sorted(suites, key=lambda value: (value.module_id, str(value.suite.get("id"))))),
        errors=tuple(errors),
        modules_without_contract=tuple(sorted(missing_contract)),
        modules_without_integration=tuple(sorted(missing_integration)),
        public_contract_owner_count=public_owner_count,
        public_contract_count=len(contract_inventory),
        public_contract_inventory=contract_inventory,
        public_contract_inventory_sha256=sha256_text(stable_json(contract_inventory)),
        manifest_inventory_sha256=sha256_text(stable_json(sorted(inventory_digest, key=lambda value: value["module_id"]))),
    )


def _check(
    check_id: str,
    status: str,
    log: str,
    details: Optional[Mapping[str, Any]] = None,
    exit_code: Optional[int] = None,
) -> Dict[str, Any]:
    if exit_code is None:
        exit_code = 0 if status == "PASS" else 1
    value = {
        "id": check_id,
        "required": True,
        "status": status,
        "exit_code": exit_code,
        "log": log,
        "log_sha256": sha256_text(log),
        "details": dict(details or {}),
    }
    return value


def _candidate_git_environment() -> Dict[str, str]:
    return {
        "GIT_CONFIG_GLOBAL": "/dev/null",
        "GIT_CONFIG_NOSYSTEM": "1",
        "GIT_CONFIG_SYSTEM": "/dev/null",
        "GIT_OPTIONAL_LOCKS": "0",
        "GIT_TERMINAL_PROMPT": "0",
        "HOME": "/var/empty",
        "LANG": "C",
        "LC_ALL": "C",
        "PATH": "/usr/bin:/bin",
        "TMPDIR": "/tmp",
    }


def _candidate_git_invocation(
    root: Path, args: Sequence[str]
) -> Tuple[Path, List[str]]:
    try:
        canonical_root = root.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error("locked Git root cannot be resolved: " + str(exc))
    if not canonical_root.is_dir():
        raise Phase0Error(
            "locked Git root is not a directory: " + str(canonical_root)
        )
    return canonical_root, [
        str(GIT_EXECUTABLE),
        "-c",
        "safe.directory=" + str(canonical_root),
        "-c",
        "core.hooksPath=/dev/null",
        "-c",
        "core.fsmonitor=false",
        *args,
    ]


def _candidate_git(
    root: Path, args: Sequence[str], *, timeout_seconds: int = 30
) -> Any:
    canonical_root, command = _candidate_git_invocation(root, args)
    return run_command(
        command,
        canonical_root,
        timeout_seconds=timeout_seconds,
        env=_candidate_git_environment(),
    )


def _candidate_git_output(root: Path, args: Sequence[str]) -> str:
    result = _candidate_git(root, args)
    if result.exit_code != 0:
        raise Phase0Error(
            "locked Git command failed: " + " ".join(args) + "\n" + result.output
        )
    return result.output.strip()


def _candidate_git_blob(root: Path, revision_and_path: str) -> Optional[bytes]:
    canonical_root, command = _candidate_git_invocation(
        root,
        ["show", revision_and_path],
    )
    completed = subprocess.run(
        command,
        cwd=str(canonical_root),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
        env=_candidate_git_environment(),
    )
    return completed.stdout if completed.returncode == 0 else None


def _workspace_snapshot(root: Path, baseline: str) -> Dict[str, Any]:
    status = _candidate_git(
        root,
        ["status", "--porcelain=v1", "--untracked-files=all", "-z"],
    )
    if status.exit_code != 0:
        raise Phase0Error("cannot determine workspace state: " + status.output.strip())
    dirty = [value for value in status.output.split("\0") if value]
    return {
        "head": _candidate_git_output(root, ["rev-parse", "HEAD^{commit}"]),
        "clean": not dirty,
        "dirty_entry_count": len(dirty),
        "digest": workspace_digest(root, baseline),
    }


def _repository_import_shadow_errors(directory: Path) -> List[str]:
    errors: List[str] = []
    for package in LOCKED_IMPORT_NAMES:
        suspicious = [
            directory / (package + ".py"),
            directory / (package + ".pyc"),
            directory / package,
        ]
        suspicious.extend(directory.glob(package + ".*"))
        suspicious.extend((directory / "__pycache__").glob(package + ".*.pyc"))
        if any(path.exists() or path.is_symlink() for path in suspicious):
            errors.append(
                "repository-local third-party import shadow is forbidden: " + package
            )
    return errors


def _toolchain_check(root: Path) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    errors: List[str] = []
    if tuple(sys.version_info[:3]) != REQUIRED_PYTHON:
        errors.append(
            "Python {0} is required, got {1}".format(
                ".".join(str(value) for value in REQUIRED_PYTHON),
                platform.python_version(),
            )
        )
    expected_python = root / ".venv" / "bin" / "python"
    try:
        expected_python_real = expected_python.resolve(strict=True)
        actual_python_real = Path(sys.executable).resolve(strict=True)
        if actual_python_real != expected_python_real:
            errors.append("candidate gate must run from the repository locked .venv")
    except (OSError, RuntimeError) as exc:
        expected_python_real = None
        actual_python_real = Path(sys.executable).resolve()
        errors.append("repository locked .venv is unavailable: " + str(exc))

    requirements = root / "requirements-ci.txt"
    toolchain_lock = root / "toolchain.lock.json"
    package_versions: Dict[str, Optional[str]] = {}
    expected_packages: Dict[str, str] = {}
    if not requirements.is_file() or requirements.is_symlink():
        errors.append("requirements-ci.txt is missing or unsafe")
    else:
        for line in requirements.read_text(encoding="utf-8").splitlines():
            match = re.match(
                r"^([A-Za-z0-9_.-]+)(?:\[[A-Za-z0-9_,.-]+\])?==([^ \\]+)",
                line,
            )
            if match:
                expected_packages[match.group(1).casefold()] = match.group(2)
        for package in (
            "jsonschema",
            "attrs",
            "referencing",
            "rpds-py",
            "psycopg",
            "PyYAML",
        ):
            try:
                actual = importlib.metadata.version(package)
            except importlib.metadata.PackageNotFoundError:
                actual = None
            package_versions[package] = actual
            expected = expected_packages.get(package.casefold())
            if expected is None or actual != expected:
                errors.append(
                    "locked Python package {0} must be {1}, got {2}".format(
                        package, expected or "declared", actual or "missing"
                    )
                )
    if not toolchain_lock.is_file() or toolchain_lock.is_symlink():
        errors.append("toolchain.lock.json is missing or unsafe")

    expected_environment_root = (root / ".venv").resolve()
    for package, origin in sorted(_LOCKED_DEPENDENCY_ORIGINS.items()):
        try:
            Path(origin).resolve().relative_to(expected_environment_root)
        except (OSError, RuntimeError, ValueError):
            errors.append(
                "locked Python dependency did not import from repository .venv: "
                + package
            )
    errors.extend(_repository_import_shadow_errors(CI_DIRECTORY))

    git_version: Optional[str] = None
    if (
        not GIT_EXECUTABLE.is_file()
        or GIT_EXECUTABLE.is_symlink()
        or not os.access(str(GIT_EXECUTABLE), os.X_OK)
    ):
        errors.append("locked /usr/bin/git is missing or unsafe")
    else:
        git_probe = _candidate_git(root, ["--version"])
        if git_probe.exit_code != 0 or not git_probe.output.startswith("git version "):
            errors.append("locked Git probe failed")
        else:
            git_version = git_probe.output.strip()

    wrapper = root / "scripts" / "dotnet-pinned.sh"
    dotnet_version: Optional[str] = None
    if not wrapper.is_file() or wrapper.is_symlink():
        errors.append("scripts/dotnet-pinned.sh is missing or unsafe")
    else:
        with _trusted_test_environment_scope({}) as environment:
            result = run_command(
                ["/bin/bash", str(wrapper), "--version"],
                root,
                timeout_seconds=30,
                env=environment,
            )
        if result.exit_code != 0:
            errors.append("pinned .NET SDK probe failed")
        elif result.output.strip() != REQUIRED_DOTNET_SDK:
            errors.append(
                "NET SDK {0} is required, got {1}".format(
                    REQUIRED_DOTNET_SDK, result.output.strip() or "empty output"
                )
            )
        else:
            dotnet_version = result.output.strip()
    details = {
        "python": platform.python_version(),
        "python_executable": str(Path(sys.executable)),
        "python_executable_realpath": str(actual_python_real),
        "requirements_ci_sha256": sha256_file(requirements) if requirements.is_file() else None,
        "toolchain_lock_sha256": sha256_file(toolchain_lock) if toolchain_lock.is_file() else None,
        "python_packages": package_versions,
        "python_package_origins": dict(_LOCKED_DEPENDENCY_ORIGINS),
        "dotnet_sdk": dotnet_version,
        "git": git_version,
    }
    status = "PASS" if not errors else "INFRA_ERROR"
    return details, _check(
        "pinned-candidate-toolchain",
        status,
        "candidate toolchain accepted" if not errors else "\n".join(errors),
        details,
        0 if not errors else 127,
    )


def _parse_npgsql_dsn(value: str) -> Dict[str, str]:
    result: Dict[str, str] = {}
    for item in value.split(";"):
        if not item.strip():
            continue
        if "=" not in item:
            raise Phase0Error("DPS_TEST_POSTGRES contains an invalid segment")
        key, raw = item.split("=", 1)
        result[key.strip().casefold()] = raw.strip()
    return result


def _parse_libpq_dsn(value: str) -> Dict[str, str]:
    result: Dict[str, str] = {}
    try:
        tokens = shlex.split(value, comments=False, posix=True)
    except ValueError as exc:
        raise Phase0Error("DPS_TEST_POSTGRES_URI is invalid: " + str(exc))
    for token in tokens:
        if "=" not in token:
            raise Phase0Error("DPS_TEST_POSTGRES_URI contains an invalid segment")
        key, raw = token.split("=", 1)
        result[key.strip().casefold()] = raw.strip()
    return result


def postgres_preflight(
    root: Path,
    runtime_environment: Mapping[str, str],
    required_environment: Sequence[str],
) -> Tuple[Optional[str], Dict[str, Any]]:
    required_connections = {
        key for key in required_environment if key in POSTGRES_CONNECTION_ENVIRONMENT
    }
    if "DPS_TEST_POSTGRES" in required_connections:
        required_connections.add("DPS_TEST_POSTGRES_URI")
    required_preflight_environment = set(required_connections)
    if "DPS_PSQL" in required_environment:
        required_preflight_environment.add("DPS_PSQL")
    missing = sorted(
        key
        for key in required_preflight_environment
        if not runtime_environment.get(key)
    )
    if missing:
        return None, _check(
            "postgresql18-preflight",
            "INFRA_ERROR",
            "missing required PostgreSQL candidate environment: " + ", ".join(missing),
            {"missing_environment_keys": missing},
            127,
        )
    try:
        if "DPS_PSQL" in required_preflight_environment:
            _safe_repo_or_external_executable(runtime_environment["DPS_PSQL"])
        if "DPS_TEST_POSTGRES" in required_connections:
            npgsql = _parse_npgsql_dsn(runtime_environment["DPS_TEST_POSTGRES"])
            libpq = _parse_libpq_dsn(runtime_environment["DPS_TEST_POSTGRES_URI"])
            comparisons = (
                ("host", "host"),
                ("port", "port"),
                ("database", "dbname"),
                ("username", "user"),
            )
            mismatches = [
                left
                for left, right in comparisons
                if npgsql.get(left) != libpq.get(right)
            ]
            if mismatches:
                raise Phase0Error(
                    "PostgreSQL connection forms do not identify the same target: "
                    + ", ".join(mismatches)
                )
        connection_targets = [
            (key, runtime_environment[key])
            for key in sorted(required_connections)
            if key != "DPS_TEST_POSTGRES"
        ]
        if not connection_targets:
            raise Phase0Error("no PostgreSQL connection target was declared")
        observed_versions = {
            _query_postgres_server_version(connection_target)
            for _, connection_target in connection_targets
        }
        if observed_versions != {EXPECTED_POSTGRES_VERSION_NUM}:
            raise Phase0Error(
                "every PostgreSQL server_version_num must be {0}, got {1}".format(
                    EXPECTED_POSTGRES_VERSION_NUM,
                    ", ".join(sorted(value or "empty" for value in observed_versions)),
                )
            )
        return EXPECTED_POSTGRES_VERSION_NUM, _check(
            "postgresql18-preflight",
            "PASS",
            "locked psycopg queried every declared PostgreSQL server; all are exactly 18.4",
            {
                "server_version_num": EXPECTED_POSTGRES_VERSION_NUM,
                "connection_target_count": len(connection_targets),
                "probe_driver": "psycopg==3.3.4",
            },
            0,
        )
    except Phase0Error as exc:
        return None, _check(
            "postgresql18-preflight",
            "INFRA_ERROR",
            str(exc),
            {},
            127,
        )


def _query_postgres_server_version(connection_target: str) -> str:
    """Query a server directly through the locked driver, never through stdout shims."""

    try:
        psycopg = _LOCKED_DEPENDENCIES["psycopg"]
        with psycopg.connect(
            connection_target,
            autocommit=True,
            connect_timeout=5,
            options="-c statement_timeout=5000",
        ) as connection:
            with connection.cursor() as cursor:
                cursor.execute("SHOW server_version_num")
                row = cursor.fetchone()
    except Exception as exc:
        raise Phase0Error(
            "PostgreSQL server probe failed through the locked driver"
        ) from exc
    if (
        not isinstance(row, (tuple, list))
        or len(row) != 1
        or not isinstance(row[0], (str, int))
    ):
        raise Phase0Error("PostgreSQL server_version_num probe returned an invalid row")
    return str(row[0]).strip()


def _safe_repo_or_external_executable(raw: str) -> Path:
    if not raw or "\x00" in raw:
        raise Phase0Error("DPS_PSQL must name an executable file")
    path = Path(raw).expanduser()
    if not path.is_absolute():
        raise Phase0Error("DPS_PSQL must be an absolute path")
    current = Path(path.anchor)
    for part in path.parts[1:]:
        current = current / part
        if current.is_symlink():
            raise Phase0Error("DPS_PSQL may not traverse a symlink")
    try:
        resolved = path.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error("DPS_PSQL is unavailable: " + str(exc))
    if not resolved.is_file() or not os.access(str(resolved), os.X_OK):
        raise Phase0Error("DPS_PSQL must be executable")
    return resolved


def _zenno_simulation_plan(root: Path, candidate: CandidateSuite) -> TrustedSuitePlan:
    suite = candidate.suite
    binding = candidate.integration_policy
    if (
        binding is None
        or binding.evidence_kind != "SIMULATION"
        or binding.test_category != "SecuritySimulation"
        or binding.required_environment
    ):
        raise Phase0Error(
            "Zenno auth simulation requires an exact SIMULATION/"
            "SecuritySimulation policy binding with no environment"
        )
    declared = suite.get("command")
    expected_command = "bash Modules/zenno-bridge/operations/test-auth-simulation.sh"
    if declared != expected_command:
        raise Phase0Error("Zenno simulation must use its single audited fixed entry point")
    script = root / "Modules/zenno-bridge/operations/test-auth-simulation.sh"
    expected = """#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/zenno-bridge/tests/Dps.ZennoBridge.AuthSimulation.Tests.csproj --locked-mode
bash scripts/dotnet-pinned.sh test Modules/zenno-bridge/tests/Dps.ZennoBridge.AuthSimulation.Tests.csproj --configuration Release --no-restore -- \\
  --filter-trait Category=SecuritySimulation \\
  --minimum-expected-tests 4 \\
  --fail-skips on
"""
    if not script.is_file() or script.is_symlink() or script.read_text(encoding="utf-8") != expected:
        raise Phase0Error("Zenno simulation script differs from the audited fixed template")
    wrapper = root / "scripts/dotnet-pinned.sh"
    if not wrapper.is_file() or wrapper.is_symlink():
        raise Phase0Error("trusted dotnet wrapper is missing or unsafe")
    target = "Modules/zenno-bridge/tests/Dps.ZennoBridge.AuthSimulation.Tests.csproj"
    return TrustedSuitePlan(
        module_id=candidate.module_id,
        suite_id=str(suite["id"]),
        test_type="integration",
        evidence_level="INTEGRATION_VERIFIED",
        declared_command=expected_command,
        environment={},
        invocations=(
            TrustedInvocation(
                ["/bin/bash", str(wrapper), "restore", target, "--locked-mode"],
                "restore",
                0,
            ),
            TrustedInvocation(
                [
                    "/bin/bash",
                    str(wrapper),
                    "test",
                    target,
                    "--configuration",
                    "Release",
                    "--no-restore",
                    "--",
                    "--filter-trait",
                    "Category=SecuritySimulation",
                    "--minimum-expected-tests",
                    "4",
                    "--fail-skips",
                    "on",
                ],
                "dotnet-test",
                4,
            ),
        ),
    )


def parse_candidate_suite(root: Path, candidate: CandidateSuite) -> TrustedSuitePlan:
    suite_id = str(candidate.suite.get("id"))
    if suite_id == "zenno-bridge.auth-simulation":
        return _zenno_simulation_plan(root, candidate)
    expected_category = (
        candidate.integration_policy.test_category
        if candidate.integration_policy is not None
        else str(candidate.suite.get("type", "")).title()
    )
    return parse_manifest_suite_command(
        root,
        candidate.module_root,
        candidate.module_id,
        candidate.suite,
        allowed_test_types=(str(candidate.suite.get("type")),),
        expected_evidence_level=EXPECTED_LEVEL[str(candidate.suite.get("type"))],
        expected_test_category=expected_category,
    )


def _canonical_test_target(root: Path, plan: TrustedSuitePlan) -> str:
    final = [value for value in plan.invocations if value.kind != "restore"]
    if len(final) != 1:
        raise Phase0Error("suite must contain exactly one executable test phase")
    invocation = final[0]
    argv = list(invocation.argv)
    target: Optional[Path] = None
    if invocation.kind == "python-unittest":
        python_args = argv[1:]
        if python_args[:1] == ["-I"]:
            python_args = python_args[1:]
        if python_args[:3] == ["-m", "unittest", "discover"]:
            directory = Path(python_args[python_args.index("-s") + 1]).resolve()
            pattern = python_args[python_args.index("-p") + 1]
            matches = [
                value.resolve()
                for value in directory.glob(pattern)
                if value.is_file() and not value.is_symlink()
            ]
            if len(matches) != 1:
                raise Phase0Error("trusted Python candidate suite must resolve to one test file")
            target = matches[0]
        elif python_args[:2] == ["-m", "unittest"] and len(python_args) == 3:
            target = Path(python_args[2]).resolve()
    elif invocation.kind == "dotnet-test":
        try:
            test_index = argv.index("test")
        except ValueError:
            test_index = -1
        if test_index >= 0:
            index = test_index + 1
            if argv[index:index + 1] == ["--project"]:
                index += 1
            target = Path(argv[index]).resolve()
            if target.is_dir():
                projects = [
                    value.resolve()
                    for value in target.rglob("*.csproj")
                    if value.is_file()
                    and not value.is_symlink()
                    and "obj" not in value.parts
                    and "bin" not in value.parts
                ]
                if len(projects) != 1:
                    raise Phase0Error("dotnet candidate directory must contain one project")
                target = projects[0]
    if target is None or not target.is_file() or not _within(target, root.resolve()):
        raise Phase0Error("candidate test target could not be proven")
    return target.relative_to(root.resolve()).as_posix()


def _policy_for_candidate(
    candidate: CandidateSuite, policy: CandidatePolicy
) -> ContractPolicy | IntegrationPolicy:
    key = (candidate.module_id, str(candidate.suite["id"]))
    binding = (
        policy.contract.get(key)
        if candidate.suite.get("type") == "contract"
        else policy.integration.get(key)
    )
    if binding is None:
        raise Phase0Error("candidate suite has no exact trusted policy binding")
    return binding


def _apply_policy_floor(
    plan: TrustedSuitePlan, minimum_executed_tests: int
) -> TrustedSuitePlan:
    invocations: List[TrustedInvocation] = []
    for invocation in plan.invocations:
        if invocation.kind == "restore":
            invocations.append(invocation)
            continue
        if minimum_executed_tests < invocation.minimum_tests:
            raise Phase0Error(
                "trusted candidate policy test-count floor cannot weaken the "
                "module manifest floor"
            )
        argv = list(invocation.argv)
        if invocation.kind == "dotnet-test":
            try:
                index = argv.index("--minimum-expected-tests") + 1
            except ValueError as exc:
                raise Phase0Error("dotnet candidate plan lacks a test-count floor") from exc
            argv[index] = str(minimum_executed_tests)
        invocations.append(
            TrustedInvocation(argv, invocation.kind, minimum_executed_tests)
        )
    return replace(plan, invocations=tuple(invocations))


def _test_tree_sha256(root: Path, candidate: CandidateSuite) -> str:
    test_root = candidate.module_root / "tests"
    if not test_root.is_dir() or test_root.is_symlink():
        raise Phase0Error("candidate module tests directory is missing or unsafe")
    files: List[Dict[str, str]] = []
    for path in test_root.rglob("*"):
        if (
            any(part in {"bin", "obj", "TestResults", "artifacts", "__pycache__"} for part in path.parts)
            or path.suffix in {".pyc", ".pyo"}
        ):
            continue
        if path.is_symlink():
            raise Phase0Error("candidate test tree contains a symlink")
        if path.is_file():
            files.append(
                {
                    "path": path.relative_to(root).as_posix(),
                    "sha256": sha256_file(path),
                }
            )
    if not files:
        raise Phase0Error("candidate test tree is empty")
    return sha256_text(stable_json(sorted(files, key=lambda value: value["path"])))


def validate_candidate_plan(
    root: Path,
    candidate: CandidateSuite,
    plan: TrustedSuitePlan,
    policy: CandidatePolicy,
) -> Tuple[TrustedSuitePlan, str, str, str, str]:
    target = _canonical_test_target(root, plan)
    binding = _policy_for_candidate(candidate, policy)
    if target != binding.test_target:
        raise Phase0Error("candidate suite target is not policy-bound")
    plan = _apply_policy_floor(plan, binding.minimum_executed_tests)
    effective = [list(value.argv) for value in plan.invocations]
    target_path = root / target
    return (
        plan,
        target,
        sha256_file(target_path),
        _test_tree_sha256(root, candidate),
        sha256_text(stable_json(effective)),
    )


def _secret_values(runtime_environment: Mapping[str, str]) -> List[Tuple[str, str]]:
    secrets: Dict[str, str] = {}
    for environment_key in POSTGRES_CONNECTION_ENVIRONMENT:
        raw = runtime_environment.get(environment_key)
        if not raw:
            continue
        try:
            if environment_key == "DPS_TEST_POSTGRES":
                parsed = _parse_npgsql_dsn(raw)
            elif "://" in raw:
                parsed_url = urllib.parse.urlsplit(raw)
                parsed = {"password": parsed_url.password or ""}
            else:
                parsed = _parse_libpq_dsn(raw)
        except Phase0Error:
            parsed = {}
        for key, value in parsed.items():
            if key in {
                "password",
                "pwd",
                "pass",
                "token",
                "access_token",
                "accesstoken",
                "secret",
                "client_secret",
            } and value:
                secrets["{0}:{1}".format(environment_key, key)] = value
    return sorted(secrets.items(), key=lambda item: len(item[1]), reverse=True)


def _redact_log(log: str, runtime_environment: Mapping[str, str]) -> str:
    redacted = log
    values = sorted(
        (
            (key, value)
            for key, value in runtime_environment.items()
            if key in ALLOWED_RUNTIME_ENVIRONMENT and value
        ),
        key=lambda item: len(item[1]),
        reverse=True,
    )
    for key, value in values:
        redacted = redacted.replace(value, "[REDACTED:{0}]".format(key))
    for label, value in _secret_values(runtime_environment):
        redacted = redacted.replace(value, "[REDACTED:{0}]".format(label))
    redacted = re.sub(
        r"(?i)(password|pwd|token|access_token|client_secret)\s*=\s*[^;\s]+",
        r"\1=[REDACTED]",
        redacted,
    )
    redacted = re.sub(
        r"-----BEGIN [^-\r\n]*PRIVATE KEY-----.*?"
        r"-----END [^-\r\n]*PRIVATE KEY-----",
        "[REDACTED:PRIVATE_KEY_PEM]",
        redacted,
        flags=re.DOTALL,
    )
    if len(redacted) > MAX_EMBEDDED_LOG_CHARS:
        omitted = len(redacted) - MAX_EMBEDDED_LOG_CHARS
        redacted = redacted[:MAX_EMBEDDED_LOG_CHARS] + "\n[TRUNCATED {0} CHARS]\n".format(omitted)
    return redacted


def _redact_evidence_logs(
    checks: Sequence[Dict[str, Any]],
    suites: Sequence[Dict[str, Any]],
    runtime_environment: Mapping[str, str],
) -> None:
    for record in list(checks) + list(suites):
        if not isinstance(record, dict):
            continue
        log = _redact_log(str(record.get("log", "")), runtime_environment)
        record["log"] = log
        record["log_sha256"] = sha256_text(log)


def _withheld_suite_output_log(
    raw_output: str,
    *,
    status: str,
    exit_code: Optional[int],
    executed_tests: int,
    minimum_tests: int,
) -> str:
    """Keep arbitrary test stdout out of portable candidate evidence."""

    return "\n".join(
        (
            "raw suite output withheld from unsigned portable evidence",
            "raw_output_sha256=" + sha256_text(raw_output),
            "reported_status=" + status,
            "reported_exit_code=" + str(exit_code),
            "reported_executed_tests=" + str(executed_tests),
            "required_minimum_tests=" + str(minimum_tests),
        )
    )


def _is_canonical_withheld_suite_log(record: Mapping[str, Any]) -> bool:
    log = record.get("log")
    if not isinstance(log, str):
        return False
    lines = log.splitlines()
    if len(lines) != 6 or lines[0] != (
        "raw suite output withheld from unsigned portable evidence"
    ):
        return False
    return (
        re.fullmatch(r"raw_output_sha256=[0-9a-f]{64}", lines[1]) is not None
        and lines[2] == "reported_status=" + str(record.get("status"))
        and lines[3] == "reported_exit_code=" + str(record.get("exit_code"))
        and lines[4] == "reported_executed_tests=" + str(record.get("executed_tests"))
        and lines[5] == "required_minimum_tests=" + str(record.get("minimum_tests"))
    )


def _suite_evidence_id(
    candidate: CandidateSuite,
    head: str,
    effective_argv_sha256: Optional[str],
    outcome: Mapping[str, Any],
) -> str:
    payload = {
        "module": candidate.module_id,
        "suite": candidate.suite.get("id"),
        "type": candidate.suite.get("type"),
        "manifest": candidate.manifest_sha256,
        "agents": candidate.agents_sha256,
        "head": head,
        "argv": effective_argv_sha256,
        "outcome": dict(outcome),
    }
    return "candidate:" + sha256_text(stable_json(payload))[:32]


def _suite_result(
    candidate: CandidateSuite,
    head: str,
    dirty: bool,
    receipt_id: Optional[str],
    started_at: str,
    finished_at: str,
    status: str,
    exit_code: Optional[int],
    executed_tests: int,
    minimum_tests: Optional[int],
    effective_argv_sha256: Optional[str],
    test_target: Optional[str],
    test_target_sha256: Optional[str],
    test_tree_sha256: Optional[str],
    forwarded_keys: Sequence[str],
    log: str,
    reason: Optional[str],
) -> Dict[str, Any]:
    declared = candidate.suite.get("command")
    declared_text = declared if isinstance(declared, str) else ""
    redacted = log
    repository_root = candidate.module_root.parents[1]
    outcome = {
        "status": status,
        "exit_code": exit_code,
        "executed_tests": executed_tests,
        "minimum_tests": minimum_tests,
        "log_sha256": sha256_text(redacted),
        "reason": reason,
    }
    return {
        "evidence_id": _suite_evidence_id(
            candidate, head, effective_argv_sha256, outcome
        ),
        "module_id": candidate.module_id,
        "suite_id": str(candidate.suite.get("id")),
        "test_type": str(candidate.suite.get("type")),
        "evidence_level": EXPECTED_LEVEL[str(candidate.suite.get("type"))],
        "evidence_kind": candidate.evidence_kind,
        "required": True,
        "status": status,
        "declared_command_sha256": sha256_text(declared_text),
        "effective_argv_sha256": effective_argv_sha256,
        "test_target": test_target,
        "test_target_sha256": test_target_sha256,
        "test_tree_sha256": test_tree_sha256,
        "manifest_path": candidate.manifest_path.relative_to(repository_root).as_posix(),
        "manifest_sha256": candidate.manifest_sha256,
        "agents_path": candidate.agents_path.relative_to(repository_root).as_posix(),
        "agents_sha256": candidate.agents_sha256,
        "instruction_receipt_id": receipt_id,
        "tested_commit": None if dirty else head,
        "workspace_dirty": dirty,
        "started_at": started_at,
        "finished_at": finished_at,
        "duration_ms": max(
            0,
            int(
                (
                    dt.datetime.fromisoformat(finished_at)
                    - dt.datetime.fromisoformat(started_at)
                ).total_seconds()
                * 1000
            ),
        ),
        "exit_code": exit_code,
        "executed_tests": executed_tests,
        "minimum_tests": minimum_tests,
        "forwarded_environment_keys": sorted(forwarded_keys),
        "log": redacted,
        "log_sha256": sha256_text(redacted),
        "reason": reason,
    }


def _coverage_suite_result(
    root: Path,
    module_id: str,
    test_type: str,
    head: str,
    dirty: bool,
    receipt_id: Optional[str],
    message: str,
) -> Dict[str, Any]:
    module_root = root / "Modules" / module_id
    manifest = module_root / "module.yaml"
    agents = module_root / "AGENTS.md"
    now = _utc_now()
    synthetic = CandidateSuite(
        module_id=module_id,
        module_root=module_root,
        manifest_path=manifest,
        agents_path=agents,
        manifest_sha256=sha256_file(manifest),
        agents_sha256=sha256_file(agents),
        suite={
            "id": module_id + ".missing-" + test_type + "-coverage",
            "type": test_type,
            "command": None,
        },
        evidence_kind="COVERAGE",
        integration_policy=None,
    )
    return _suite_result(
        synthetic,
        head,
        dirty,
        receipt_id,
        now,
        now,
        "FAIL",
        1,
        0,
        None,
        None,
        None,
        None,
        None,
        (),
        message,
        message,
    )


def execute_candidate_suites(
    root: Path,
    inventory: Inventory,
    policy: CandidatePolicy,
    head: str,
    dirty: bool,
    receipt_id: Optional[str],
    runtime_environment: Mapping[str, str],
    postgres_ready: bool,
    timeout_seconds: int,
) -> List[Dict[str, Any]]:
    parsed: Dict[
        Tuple[str, str], Tuple[TrustedSuitePlan, str, str, str, str]
    ] = {}
    failures: Dict[Tuple[str, str], str] = {}
    argv_owners: Dict[str, List[Tuple[str, str]]] = {}
    for candidate in inventory.suites:
        key = (candidate.module_id, str(candidate.suite.get("id")))
        try:
            plan = parse_candidate_suite(root, candidate)
            plan, target, target_sha, tree_sha, argv_sha = validate_candidate_plan(
                root, candidate, plan, policy
            )
            parsed[key] = (plan, target, target_sha, tree_sha, argv_sha)
            argv_owners.setdefault(argv_sha, []).append(key)
        except (Phase0Error, KeyError, ValueError) as exc:
            failures[key] = str(exc)
    for argv_sha, owners in argv_owners.items():
        if len(owners) > 1:
            message = "duplicate effective test argv: " + ", ".join(
                "{0}:{1}".format(*value) for value in sorted(owners)
            )
            for key in owners:
                failures[key] = message

    results: List[Dict[str, Any]] = []
    for candidate in inventory.suites:
        key = (candidate.module_id, str(candidate.suite.get("id")))
        started_at = _utc_now()
        plan_and_hash = parsed.get(key)
        required_keys = (
            candidate.integration_policy.required_environment
            if candidate.integration_policy is not None
            else ()
        )
        available_required_keys = tuple(
            key_name
            for key_name in required_keys
            if runtime_environment.get(key_name)
        )
        if key in failures or plan_and_hash is None:
            finished_at = _utc_now()
            message = failures.get(key, "suite plan is unavailable")
            results.append(
                _suite_result(
                    candidate,
                    head,
                    dirty,
                    receipt_id,
                    started_at,
                    finished_at,
                    "FAIL",
                    1,
                    0,
                    None,
                    plan_and_hash[4] if plan_and_hash else None,
                    plan_and_hash[1] if plan_and_hash else None,
                    plan_and_hash[2] if plan_and_hash else None,
                    plan_and_hash[3] if plan_and_hash else None,
                    available_required_keys,
                    message,
                    message,
                )
            )
            continue
        plan, target, target_sha, tree_sha, argv_sha = plan_and_hash
        missing = [key_name for key_name in required_keys if not runtime_environment.get(key_name)]
        if missing:
            missing_message = "missing required environment: " + ", ".join(missing)
            finished_at = _utc_now()
            results.append(
                _suite_result(
                    candidate,
                    head,
                    dirty,
                    receipt_id,
                    started_at,
                    finished_at,
                    "INFRA_ERROR",
                    127,
                    0,
                    plan.invocations[-1].minimum_tests,
                    argv_sha,
                    target,
                    target_sha,
                    tree_sha,
                    available_required_keys,
                    missing_message,
                    missing_message,
                )
            )
            continue
        if candidate.evidence_kind == "REAL_POSTGRESQL" and not postgres_ready:
            missing_message = "PostgreSQL 18.4 preflight failed"
            finished_at = _utc_now()
            results.append(
                _suite_result(
                    candidate,
                    head,
                    dirty,
                    receipt_id,
                    started_at,
                    finished_at,
                    "INFRA_ERROR",
                    127,
                    0,
                    plan.invocations[-1].minimum_tests,
                    argv_sha,
                    target,
                    target_sha,
                    tree_sha,
                    available_required_keys,
                    missing_message,
                    missing_message,
                )
            )
            continue
        forwarded = {
            key_name: runtime_environment[key_name]
            for key_name in required_keys
            if runtime_environment.get(key_name)
        }
        plan = replace(plan, environment={**dict(plan.environment), **forwarded})
        try:
            check = execute_manifest_suite(
                root, plan, timeout_seconds=timeout_seconds
            )
        except Exception as exc:
            finished_at = _utc_now()
            raw_error = "{0}: {1}".format(type(exc).__name__, exc)
            message = _withheld_suite_output_log(
                raw_error,
                status="INFRA_ERROR",
                exit_code=127,
                executed_tests=0,
                minimum_tests=plan.invocations[-1].minimum_tests,
            )
            results.append(
                _suite_result(
                    candidate,
                    head,
                    dirty,
                    receipt_id,
                    started_at,
                    finished_at,
                    "INFRA_ERROR",
                    127,
                    0,
                    plan.invocations[-1].minimum_tests,
                    argv_sha,
                    target,
                    target_sha,
                    tree_sha,
                    tuple(sorted(forwarded)),
                    message,
                    "trusted suite execution infrastructure error",
                )
            )
            continue
        raw_log = str(check.get("log", ""))
        status = str(check.get("status"))
        if status == "FAIL" and re.search(r"(?i)\bINFRA_ERROR\b", raw_log):
            status = "INFRA_ERROR"
        details = check.get("details")
        details = details if isinstance(details, Mapping) else {}
        executed = details.get("executed_tests", 0)
        minimum = details.get("minimum_tests")
        if not isinstance(executed, int) or isinstance(executed, bool):
            executed = 0
        if not isinstance(minimum, int) or isinstance(minimum, bool):
            minimum = plan.invocations[-1].minimum_tests
        log = _withheld_suite_output_log(
            raw_log,
            status=status,
            exit_code=check.get("exit_code"),
            executed_tests=executed,
            minimum_tests=minimum,
        )
        reason = None if status == "PASS" else "required candidate suite did not pass"
        finished_at = _utc_now()
        results.append(
            _suite_result(
                candidate,
                head,
                dirty,
                receipt_id,
                started_at,
                finished_at,
                status,
                check.get("exit_code"),
                executed,
                minimum,
                argv_sha,
                target,
                target_sha,
                tree_sha,
                tuple(sorted(forwarded)),
                log,
                reason,
            )
        )
    return results


def _changed_path_inventory(
    root: Path, baseline: str
) -> Tuple[Tuple[str, ...], List[Dict[str, Any]]]:
    try:
        changed_paths = tuple(_changed_paths(root, baseline))
    except Exception as exc:
        raise Phase0Error("candidate changed-path resolution failed: " + str(exc))
    records: List[Dict[str, Any]] = []
    for relative in changed_paths:
        path = root / relative
        if path.is_symlink():
            raise Phase0Error("changed path is a symlink: " + relative)
        state = "file" if path.is_file() else "deleted"
        records.append(
            {
                "path": relative,
                "state": state,
                "sha256": sha256_file(path) if state == "file" else None,
            }
        )
    return changed_paths, records


def _build_upgrade_intent(
    root: Path,
    baseline: str,
    head: str,
    started_at: str,
    inventory: Inventory,
    changed_paths: Sequence[str],
) -> Dict[str, Any]:
    token = sha256_text(
        stable_json(
            {
                "baseline": baseline,
                "head": head,
                "started_at": started_at,
                "changed_paths": list(changed_paths),
                "modules": list(inventory.module_ids),
            }
        )
    )
    intent: Dict[str, Any] = {
        "schema_version": "dps.upgrade-intent/v1",
        "contract_id": "upgrade.intent/v1",
        "producer_module": "candidate-gate",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + token[:32],
        "idempotency_key": "idem_" + token,
        "occurred_at": started_at,
        "privacy_class": "internal",
        "intent_id": "candidate:intent:" + token[:24],
        "auth_context_id": "candidate:auth:" + token[:24],
        "baseline_commit": baseline,
        "target_modules": list(inventory.module_ids),
        "requested_paths": [
            "Modules/{0}/module.yaml".format(module_id)
            for module_id in inventory.module_ids
        ],
        "public_contract_changes": sorted(
            {str(value["contract_id"]) for value in inventory.public_contract_inventory}
        ),
        "risk_tier": "R3",
        "requested_stage": "development",
        "requester": {
            "identity": "dps-candidate-gate",
            "role": "impact-planner",
        },
        "authorization": {
            "status": "pending",
            "approved_by": None,
            "approver_role": "not-applicable",
            "approval_scope": None,
        },
    }
    return intent


def _validate_upgrade_intent_binding(
    root: Path,
    intent: Mapping[str, Any],
    *,
    baseline: str,
    head: str,
    started_at: str,
    inventory: Inventory,
    changed_paths: Sequence[str],
) -> None:
    expected = _build_upgrade_intent(
        root,
        baseline,
        head,
        started_at,
        inventory,
        changed_paths,
    )
    if stable_json(intent) != stable_json(expected):
        raise Phase0Error(
            "candidate UpgradeIntent does not exactly match current candidate truth"
        )


def _resolve_candidate_receipt(
    root: Path,
    baseline: str,
    required_scope: Sequence[str],
    resolved_at: str,
) -> Dict[str, Any]:
    try:
        receipt = resolve_instruction_receipt(
            root,
            baseline,
            agent_identity="dps-candidate-gate",
            agent_role="evidence-auditor",
            resolved_at=resolved_at,
            required_scope=required_scope,
        )
        valid, message, current = validate_instruction_receipt(
            root,
            receipt,
            required_scope=required_scope,
        )
    except Exception as exc:
        raise Phase0Error("candidate instruction binding failed: " + str(exc))
    if not valid:
        raise Phase0Error("candidate instruction receipt is stale: " + message)
    schema = _load_json_object(
        root / "governance/schemas/phase0-instruction-receipt.schema.json",
        "InstructionReceipt schema",
    )
    errors = validate_json_schema(current, schema)
    if errors:
        raise Phase0Error(
            "candidate InstructionReceipt violates owned schema: " + "; ".join(errors)
        )
    return current


def _safe_evidence_path(
    root: Path, raw: Path, *, allow_reserved_companion: bool = False
) -> Path:
    root_path = root.resolve()
    path = raw if raw.is_absolute() else root_path / raw
    reports_root = root_path / "Reports" / "ci"
    try:
        lexical = Path(os.path.abspath(os.fspath(path)))
        relative_to_root = lexical.relative_to(root_path)
        relative_to_reports = lexical.relative_to(reports_root)
    except (OSError, RuntimeError, ValueError) as exc:
        raise Phase0Error(
            "candidate evidence must be written under ignored Reports/ci: "
            + str(exc)
        )
    if not relative_to_reports.parts:
        raise Phase0Error("candidate evidence must name a JSON file under Reports/ci")
    for part in relative_to_reports.parts:
        if re.fullmatch(r"[a-z0-9][a-z0-9._-]*", part) is None:
            raise Phase0Error(
                "candidate evidence path components must use lowercase ASCII safe names"
            )
    if (
        "phase0-prerequisites" in relative_to_reports.parts
        and not allow_reserved_companion
    ):
        raise Phase0Error(
            "candidate evidence may not use the reserved phase0-prerequisites directory"
        )
    current = root_path
    for part in relative_to_root.parts:
        current = current / part
        if current.is_symlink():
            raise Phase0Error("candidate evidence path may not traverse a symlink")
    try:
        candidate = lexical.resolve(strict=False)
        allowed = reports_root.resolve(strict=False)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error("candidate evidence path cannot be resolved: " + str(exc))
    if not _within(candidate, allowed) or candidate == allowed:
        raise Phase0Error("candidate evidence must be written under ignored Reports/ci")
    if candidate.suffix != ".json":
        raise Phase0Error("candidate evidence must use a lowercase .json filename")
    if candidate.name.endswith(PUBLICATION_MARKER_SUFFIX):
        raise Phase0Error("candidate evidence may not occupy a publication marker path")
    artifacts = (
        candidate,
        _publication_marker_path(candidate),
        _publication_claim_path(candidate),
    )
    for artifact in artifacts:
        if artifact.is_symlink():
            raise Phase0Error("candidate publication paths may not be symlinks")
        if artifact.exists() and not artifact.is_file():
            raise Phase0Error("candidate publication paths must be regular files")
        relative = artifact.relative_to(root_path).as_posix()
        tracked = _candidate_git(
            root,
            ["ls-files", "--error-unmatch", "--", relative],
        )
        if tracked.exit_code == 0:
            raise Phase0Error(
                "candidate evidence may not overwrite a tracked publication file"
            )
        ignored = _candidate_git(
            root,
            ["check-ignore", "--quiet", "--no-index", "--", relative],
        )
        if ignored.exit_code != 0:
            raise Phase0Error("candidate publication paths must be Git-ignored")
    return candidate


def _trust_anchor_inventory(
    root: Path, baseline: str
) -> Tuple[List[Dict[str, Any]], bool]:
    records: List[Dict[str, Any]] = []
    all_match = True
    for relative in CANDIDATE_TRUST_PATHS:
        current_path = root / relative
        current_sha = (
            sha256_file(current_path)
            if current_path.is_file() and not current_path.is_symlink()
            else None
        )
        blob = _candidate_git_blob(root, baseline + ":" + relative)
        baseline_sha = (
            hashlib.sha256(blob).hexdigest()
            if blob is not None
            else None
        )
        matches = current_sha is not None and current_sha == baseline_sha
        all_match = all_match and matches
        records.append(
            {
                "path": relative,
                "baseline_sha256": baseline_sha,
                "current_sha256": current_sha,
                "matches_baseline": matches,
            }
        )
    return records, all_match


def _phase0_companion_evidence_path(
    root: Path,
    candidate_evidence_path: Path,
    publication_run_id: Optional[str] = None,
) -> Path:
    if publication_run_id is not None and re.fullmatch(
        r"[0-9a-f]{32}", publication_run_id
    ) is None:
        raise Phase0Error("candidate publication run id is invalid")
    companion_name = candidate_evidence_path.stem
    if publication_run_id is not None:
        companion_name += "-" + publication_run_id
    companion = (
        candidate_evidence_path.parent
        / "phase0-prerequisites"
        / (companion_name + ".json")
    )
    return _safe_evidence_path(root, companion, allow_reserved_companion=True)


def _phase0_prerequisite_runtime_environment(
    ambient: Optional[Mapping[str, str]] = None,
) -> Dict[str, str]:
    source = os.environ if ambient is None else ambient
    return {
        key: source[key]
        for key in PHASE0_PREREQUISITE_RUNTIME_ENVIRONMENT
        if source.get(key)
    }


def _phase0_prerequisite(
    root: Path,
    candidate_evidence_path: Path,
    baseline: str,
    head: str,
    start_digest: str,
    diagnostic: bool,
    runtime_environment: Mapping[str, str],
    publication_run_id: str,
) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    evidence_path = _phase0_companion_evidence_path(
        root, candidate_evidence_path, publication_run_id
    )
    command = [
        sys.executable,
        "-I",
        str(root / "Tools/ci/run_phase0_gate.py"),
        "--base",
        baseline,
        "--evidence",
        str(evidence_path),
    ]
    if diagnostic:
        command.append("--diagnostic-workspace")
    phase0_environment = {}
    legacy_anchor = runtime_environment.get("DPS_LEGACY_BASELINE_ANCHOR")
    if legacy_anchor:
        phase0_environment["DPS_LEGACY_BASELINE_ANCHOR"] = legacy_anchor
    with _trusted_test_environment_scope(phase0_environment) as environment:
        result = run_command(
            command,
            root,
            timeout_seconds=3600,
            env=environment,
        )
    try:
        payload, phase0_file_sha = _load_committed_json_object_with_sha(
            evidence_path, "Phase0 prerequisite evidence"
        )
    except Phase0Error as exc:
        return {}, _check(
            "phase0-cumulative-prerequisite",
            "INFRA_ERROR" if result.exit_code in (124, 127) else "FAIL",
            _redact_log(result.output + "\n" + str(exc), runtime_environment),
            {},
            result.exit_code or 1,
        )
    supplied_hash = payload.get("evidence_sha256")
    accepted = result.exit_code == 0 and _phase0_payload_valid(
        payload,
        root=root,
        baseline=baseline,
        head=head,
        workspace_digest_value=start_digest,
        diagnostic=diagnostic,
    )
    details = {
        "phase0_evidence_sha256": phase0_file_sha,
        "phase0_payload_sha256": supplied_hash,
        "head": payload.get("head_commit_observed"),
        "baseline": payload.get("baseline_commit"),
        "workspace_sha256": payload.get("workspace_sha256"),
        "diagnostic": diagnostic,
    }
    return payload, _check(
        "phase0-cumulative-prerequisite",
        "PASS" if accepted else "FAIL",
        "same-baseline Phase0 prerequisite passed"
        if accepted
        else _redact_log(result.output or "Phase0 prerequisite evidence mismatch", runtime_environment),
        details,
        0 if accepted else (result.exit_code or 1),
    )


def _inventory_payload(
    inventory: Inventory, policy: CandidatePolicy
) -> Dict[str, Any]:
    contract_count = sum(
        value.suite.get("type") == "contract" for value in inventory.suites
    )
    integration_count = sum(
        value.suite.get("type") == "integration" for value in inventory.suites
    )
    return {
        "module_count": len(inventory.module_ids),
        "public_contract_owner_count": inventory.public_contract_owner_count,
        "public_contract_count": inventory.public_contract_count,
        "public_contract_inventory": [
            dict(value) for value in inventory.public_contract_inventory
        ],
        "public_contract_inventory_sha256": inventory.public_contract_inventory_sha256,
        "selected_suite_count": len(inventory.suites),
        "contract_suite_count": contract_count,
        "integration_suite_count": integration_count,
        "modules_without_contract_coverage": list(inventory.modules_without_contract),
        "modules_without_integration_coverage": list(inventory.modules_without_integration),
        "manifest_inventory_sha256": inventory.manifest_inventory_sha256,
        "policy_sha256": policy.sha256,
    }


def _expected_phase0_check_ids(root: Path) -> set[str]:
    ids = {
        "clean-checkout-evidence-boundary",
        "pinned-toolchain",
        "repository-validator",
        "release-shell-syntax",
        "module-governance",
        "ci-fail-closed-policy",
        "instruction-resolution-and-staleness",
        "phase0-adversarial-unit-tests",
        "external-gate-adversarial-unit-tests",
        "solution-locked-restore-build",
        "playwright-static-config",
        "instruction-receipt-final-staleness",
        "test-evidence-schema",
    }
    for module_root in discover_registered_module_dirs(root):
        manifest = load_json_compatible_yaml(module_root / "module.yaml")
        tests = manifest.get("tests")
        suites = tests.get("suites") if isinstance(tests, Mapping) else []
        for suite in suites if isinstance(suites, list) else []:
            if (
                isinstance(suite, Mapping)
                and suite.get("required") is True
                and suite.get("evidenceLevel") == "REPOSITORY_STATIC_VERIFIED"
            ):
                ids.add(
                    "manifest:{0}:{1}".format(module_root.name, suite.get("id"))
                )
    return ids


def _phase0_payload_valid(
    payload: Mapping[str, Any],
    *,
    root: Path = ROOT,
    baseline: str,
    head: str,
    workspace_digest_value: str,
    diagnostic: bool,
) -> bool:
    supplied_hash = payload.get("evidence_sha256")
    unhashed = dict(payload)
    unhashed.pop("evidence_sha256", None)
    checks = payload.get("checks")
    if not isinstance(checks, list) or not checks:
        return False
    check_ids: List[str] = []
    statuses: List[str] = []
    for value in checks:
        if not isinstance(value, Mapping) or not isinstance(value.get("id"), str):
            return False
        if value.get("required") is not True:
            return False
        check_ids.append(str(value["id"]))
        statuses.append(str(value.get("status")))
        if value.get("log_sha256") != sha256_text(str(value.get("log", ""))):
            return False
        if value.get("status") == "PASS" and value.get("exit_code") != 0:
            return False
    if len(check_ids) != len(set(check_ids)) or set(check_ids) != _expected_phase0_check_ids(root):
        return False
    required_pass = all(value.get("status") == "PASS" for value in checks)
    expected_summary = {
        "total": len(statuses),
        "required": len(checks),
        "passed": sum(value == "PASS" for value in statuses),
        "failed": sum(value == "FAIL" for value in statuses),
        "skipped": sum(value == "SKIP" for value in statuses),
        "partial": sum(value == "PARTIAL" for value in statuses),
        "not_run": sum(value == "NOT_RUN" for value in statuses),
        "infra_error": sum(value == "INFRA_ERROR" for value in statuses),
        "not_applicable": sum(value == "NOT_APPLICABLE" for value in statuses),
    }
    if payload.get("summary") != expected_summary:
        return False
    environment = payload.get("environment")
    if (
        payload.get("schema_version") != "dps.phase0-evidence-bundle/v1"
        or not isinstance(environment, Mapping)
    ):
        return False
    clean_check = next(
        (
            value
            for value in checks
            if value.get("id") == "clean-checkout-evidence-boundary"
        ),
        None,
    )
    if not isinstance(clean_check, Mapping):
        return False
    clean_details = clean_check.get("details")
    if not isinstance(clean_details, Mapping):
        return False
    if diagnostic:
        if (
            payload.get("gate") != "WORKSPACE_DIAGNOSTIC_ONLY"
            or environment.get("evidence_mode") != "WORKSPACE_DIAGNOSTIC_ONLY"
            or payload.get("test_evidence") != []
        ):
            return False
    elif (
        payload.get("gate") != "REPOSITORY_STATIC_VERIFIED"
        or environment.get("evidence_mode") != "REPOSITORY_STATIC_VERIFIED"
        or environment.get("workspace_clean") is not True
        or clean_details.get("clean") is not True
        or clean_details.get("diagnostic_workspace") is not False
        or clean_details.get("formal_evidence_eligible") is not True
    ):
        return False
    phase0_receipt = payload.get("instruction_receipt")
    if not isinstance(phase0_receipt, Mapping):
        return False
    receipt_schema = _load_json_object(
        root / "governance/schemas/phase0-instruction-receipt.schema.json",
        "Phase0 InstructionReceipt schema",
    )
    if validate_json_schema(phase0_receipt, receipt_schema):
        return False
    if (
        phase0_receipt.get("status") != "BOUND"
        or phase0_receipt.get("baseline_commit") != baseline
    ):
        return False
    try:
        receipt_valid, _, current_receipt = validate_instruction_receipt(
            root, phase0_receipt
        )
    except (Phase0Error, OSError, ValueError, KeyError, TypeError):
        return False
    if (
        not receipt_valid
        or stable_json(current_receipt) != stable_json(phase0_receipt)
    ):
        return False
    if not diagnostic:
        test_evidence = payload.get("test_evidence")
        if not isinstance(test_evidence, list) or len(test_evidence) != len(checks):
            return False
        test_schema = _load_json_object(
            root / "governance/schemas/phase0-test-evidence.schema.json",
            "Phase0 test evidence schema",
        )
        evidence_by_id: Dict[str, Mapping[str, Any]] = {}
        for record in test_evidence:
            if not isinstance(record, Mapping) or validate_json_schema(
                record, test_schema
            ):
                return False
            test_id = record.get("test_id")
            if not isinstance(test_id, str) or test_id in evidence_by_id:
                return False
            evidence_by_id[test_id] = record
        if set(evidence_by_id) != set(check_ids):
            return False
        receipt_id = phase0_receipt.get("receipt_id")
        for check in checks:
            check_id = str(check["id"])
            record = evidence_by_id[check_id]
            artifacts = record.get("artifacts")
            if (
                record.get("evidence_id")
                != "phase0:{0}:{1}".format(
                    sha256_text(check_id + baseline)[:16], check_id
                )
                or record.get("required") is not True
                or record.get("status") != "PASS"
                or record.get("exit_code") != 0
                or record.get("verification_level")
                != "REPOSITORY_STATIC_VERIFIED"
                or record.get("baseline_commit") != baseline
                or record.get("instruction_receipt_id") != receipt_id
                or record.get("runner_identity") != "dps-phase0-gate"
                or not isinstance(artifacts, list)
                or len(artifacts) != 1
                or not isinstance(artifacts[0], Mapping)
                or artifacts[0].get("path")
                != "embedded:checks/{0}/log".format(check_id)
                or artifacts[0].get("sha256") != check.get("log_sha256")
            ):
                return False
    common = (
        supplied_hash == sha256_text(stable_json(unhashed))
        and payload.get("overall_status") == "PASS"
        and payload.get("baseline_commit") == baseline
        and payload.get("head_commit_observed") == head
        and payload.get("workspace_sha256") == workspace_digest_value
        and required_pass
    )
    if diagnostic:
        return common and payload.get("verification_level") is None and payload.get("commit_sha") is None
    return (
        common
        and payload.get("verification_level") == "REPOSITORY_STATIC_VERIFIED"
        and payload.get("commit_sha") == head
    )


def validate_candidate_evidence(evidence: Mapping[str, Any], root: Path = ROOT) -> None:
    schema = _load_json_object(root / EVIDENCE_SCHEMA_PATH, "candidate evidence schema")
    errors = validate_json_schema(evidence, schema)
    if errors:
        raise Phase0Error("candidate evidence violates schema: " + "; ".join(errors))
    if (
        evidence.get("candidate_verification_level") is not None
        or evidence.get("verification_level") is not None
        or evidence.get("signed") is not False
        or evidence.get("formal_evidence_eligible") is not False
    ):
        raise Phase0Error("local candidate evidence cannot issue or sign a verification level")

    supplied_hash = evidence.get("evidence_sha256")
    unhashed = dict(evidence)
    unhashed.pop("evidence_sha256", None)
    if supplied_hash != sha256_text(stable_json(unhashed)):
        raise Phase0Error("candidate evidence_sha256 does not match canonical payload")

    requested_level = evidence.get("requested_verification_level")
    if requested_level not in ("CONTRACT_VERIFIED", "INTEGRATION_VERIFIED"):
        raise Phase0Error("candidate evidence requested level is invalid")
    head = _candidate_git_output(root, ["rev-parse", "HEAD^{commit}"])
    baseline = str(evidence.get("baseline_commit"))
    if evidence.get("head_commit_observed") != head:
        raise Phase0Error("candidate evidence HEAD is not current repository HEAD")
    try:
        resolved_baseline = _candidate_git_output(root, ["rev-parse", baseline + "^{commit}"])
    except Phase0Error as exc:
        raise Phase0Error("candidate baseline is not a commit: " + str(exc))
    if resolved_baseline != baseline:
        raise Phase0Error("candidate baseline is not canonical")

    policy = load_candidate_policy(root)
    inventory = discover_candidate_inventory(root, str(requested_level), policy)
    if evidence.get("inventory") != _inventory_payload(inventory, policy):
        raise Phase0Error("candidate inventory does not match current module/contract truth")

    changed_paths, changed_inventory = _changed_path_inventory(root, baseline)
    if evidence.get("changed_paths") != changed_inventory:
        raise Phase0Error("candidate changed-path inventory is stale or incomplete")

    intent = evidence.get("upgrade_intent")
    if not isinstance(intent, Mapping):
        raise Phase0Error("candidate evidence lacks a full UpgradeIntent")
    started_at = evidence.get("started_at")
    if not isinstance(started_at, str):
        raise Phase0Error("candidate evidence started_at is invalid")
    _validate_upgrade_intent_binding(
        root,
        intent,
        baseline=baseline,
        head=head,
        started_at=started_at,
        inventory=inventory,
        changed_paths=changed_paths,
    )

    receipt = evidence.get("instruction_receipt")
    if not isinstance(receipt, Mapping):
        raise Phase0Error("candidate evidence lacks an InstructionReceipt")
    receipt_schema = _load_json_object(
        root / "governance/schemas/phase0-instruction-receipt.schema.json",
        "InstructionReceipt schema",
    )
    receipt_errors = validate_json_schema(receipt, receipt_schema)
    if receipt_errors:
        raise Phase0Error("candidate InstructionReceipt violates owned schema: " + "; ".join(receipt_errors))
    current_receipt = _resolve_candidate_receipt(
        root,
        baseline,
        inventory.module_ids,
        str(receipt.get("resolved_at")),
    )
    if stable_json(current_receipt) != stable_json(receipt):
        raise Phase0Error("candidate InstructionReceipt is stale")
    if receipt.get("scope") != list(inventory.module_ids):
        raise Phase0Error("candidate receipt does not bind the whole module scope")

    trust_anchors, trust_match = _trust_anchor_inventory(root, baseline)
    if evidence.get("trust_anchors") != trust_anchors:
        raise Phase0Error("candidate trust-anchor inventory is stale")

    workspace = evidence.get("workspace")
    if not isinstance(workspace, Mapping):
        raise Phase0Error("candidate workspace boundary is missing")
    current_workspace = _workspace_snapshot(root, baseline)
    if (
        current_workspace["head"] != head
        or workspace.get("digest_post_write") != current_workspace["digest"]
        or workspace.get("clean_post_write") != current_workspace["clean"]
        or workspace.get("dirty_entry_count_post_write")
        != current_workspace["dirty_entry_count"]
        or workspace.get("digest_start") != workspace.get("digest_end")
        or workspace.get("digest_end") != workspace.get("digest_post_write")
    ):
        raise Phase0Error("candidate workspace did not remain stable through evidence write")

    mode = evidence.get("mode")
    diagnostic_requested = evidence.get("diagnostic_requested") is True
    if mode == "WORKSPACE_DIAGNOSTIC_ONLY":
        if evidence.get("commit_sha") is not None:
            raise Phase0Error("diagnostic candidate cannot attribute a tested commit")
    elif mode == "CLEAN_CANDIDATE":
        ancestor = _candidate_git(
            root, ["merge-base", "--is-ancestor", baseline, head]
        ).exit_code == 0
        stable_clean = (
            not diagnostic_requested
            and ancestor
            and workspace.get("clean_start") is True
            and workspace.get("clean_end") is True
            and workspace.get("clean_post_write") is True
        )
        trust_eligible = stable_clean and baseline != head and trust_match
        if evidence.get("commit_sha") != (head if trust_eligible else None):
            raise Phase0Error("clean candidate commit attribution is not recomputable")
        if evidence.get("overall_status") == "PASS" and not trust_eligible:
            raise Phase0Error("clean PASS candidate lacks a stable predecessor trust anchor")
    else:
        raise Phase0Error("candidate evidence mode is invalid")

    phase0 = evidence.get("phase0_prerequisite")
    if not isinstance(phase0, Mapping) or not _phase0_payload_valid(
        phase0,
        root=root,
        baseline=baseline,
        head=head,
        workspace_digest_value=str(workspace.get("digest_start")),
        diagnostic=mode == "WORKSPACE_DIAGNOSTIC_ONLY",
    ):
        raise Phase0Error("same-baseline cumulative Phase0 evidence is invalid")

    checks = evidence.get("checks")
    suites = evidence.get("suites")
    if not isinstance(checks, list) or not isinstance(suites, list):
        raise Phase0Error("candidate evidence checks and suites must be arrays")
    expected_check_ids = {
        "workspace-start-boundary",
        "pinned-candidate-toolchain",
        "module-governance-prerequisite",
        "candidate-policy-and-inventory",
        "candidate-trust-anchor",
        "phase0-cumulative-prerequisite",
        "instruction-receipt-start",
        "postgresql18-preflight",
        "instruction-receipt-final-staleness",
        "workspace-end-stability",
    }
    check_map: Dict[str, Mapping[str, Any]] = {}
    for record in checks:
        if not isinstance(record, Mapping) or not isinstance(record.get("id"), str):
            raise Phase0Error("candidate check record is malformed")
        check_id = str(record["id"])
        if check_id in check_map:
            raise Phase0Error("candidate evidence contains duplicate check ids")
        if record.get("log_sha256") != sha256_text(str(record.get("log", ""))):
            raise Phase0Error("candidate check log hash mismatch: " + check_id)
        if record.get("status") == "PASS" and record.get("exit_code") != 0:
            raise Phase0Error("PASS candidate check has nonzero exit: " + check_id)
        check_map[check_id] = record
    if set(check_map) != expected_check_ids:
        raise Phase0Error("candidate evidence check inventory is incomplete or expanded")

    fresh_environment, fresh_toolchain = _toolchain_check(root)
    environment = evidence.get("environment")
    if not isinstance(environment, Mapping):
        raise Phase0Error("candidate environment is malformed")
    for key in (
        "python",
        "python_executable",
        "python_executable_realpath",
        "requirements_ci_sha256",
        "toolchain_lock_sha256",
        "python_packages",
        "dotnet_sdk",
    ):
        if environment.get(key) != fresh_environment.get(key):
            raise Phase0Error("candidate toolchain environment is stale: " + key)
    forwarded_environment_keys = environment.get("forwarded_environment_keys")
    if not isinstance(forwarded_environment_keys, list):
        raise Phase0Error("candidate forwarded environment keys are malformed")

    try:
        validate_governance(root, require_schema=True)
        governance_status = "PASS"
    except (Phase0Error, OSError, ValueError):
        governance_status = "FAIL"
    expected_check_status = {
        "workspace-start-boundary": (
            "PASS"
            if workspace.get("clean_start") is True or diagnostic_requested
            else "FAIL"
        ),
        "pinned-candidate-toolchain": fresh_toolchain["status"],
        "module-governance-prerequisite": governance_status,
        "candidate-policy-and-inventory": "PASS" if not inventory.errors else "FAIL",
        "candidate-trust-anchor": (
            "PASS"
            if mode == "WORKSPACE_DIAGNOSTIC_ONLY"
            else "PASS" if trust_match and baseline != head else "FAIL"
        ),
        "phase0-cumulative-prerequisite": "PASS",
        "instruction-receipt-start": "PASS",
        "instruction-receipt-final-staleness": "PASS",
        "workspace-end-stability": (
            "PASS"
            if workspace.get("digest_start") == workspace.get("digest_end")
            and (workspace.get("clean_end") is True or diagnostic_requested)
            else "FAIL"
        ),
    }
    runtime_environment = {
        key: os.environ[key]
        for key in ALLOWED_RUNTIME_ENVIRONMENT
        if os.environ.get(key)
    }
    postgres_needed = (
        requested_level == "INTEGRATION_VERIFIED"
        and any(value.evidence_kind == "REAL_POSTGRESQL" for value in inventory.suites)
    )
    if postgres_needed:
        required_postgres_environment = sorted(
            {
                key
                for candidate in inventory.suites
                if candidate.evidence_kind == "REAL_POSTGRESQL"
                and candidate.integration_policy is not None
                for key in candidate.integration_policy.required_environment
            }
        )
        postgres_version, postgres_check = postgres_preflight(
            root,
            runtime_environment,
            required_postgres_environment,
        )
        expected_check_status["postgresql18-preflight"] = postgres_check["status"]
        if environment.get("postgres_server_version_num") != postgres_version:
            raise Phase0Error("candidate PostgreSQL server version evidence is stale")
    else:
        expected_check_status["postgresql18-preflight"] = "PASS"
        if environment.get("postgres_server_version_num") is not None:
            raise Phase0Error("Contract-only evidence cannot claim PostgreSQL execution")
    for check_id, expected_status in expected_check_status.items():
        if check_map[check_id].get("status") != expected_status:
            raise Phase0Error("candidate check status is not recomputable: " + check_id)

    suite_map: Dict[Tuple[str, str], Mapping[str, Any]] = {}
    evidence_ids: set[str] = set()
    for record in suites:
        if not isinstance(record, Mapping):
            raise Phase0Error("candidate suite evidence must be an object")
        key = (str(record.get("module_id")), str(record.get("suite_id")))
        if key in suite_map:
            raise Phase0Error("candidate evidence contains duplicate module/suite ids")
        evidence_id = str(record.get("evidence_id"))
        if evidence_id in evidence_ids:
            raise Phase0Error("candidate evidence contains duplicate evidence ids")
        evidence_ids.add(evidence_id)
        if record.get("log_sha256") != sha256_text(str(record.get("log", ""))):
            raise Phase0Error("candidate suite log hash mismatch: " + ":".join(key))
        for _, secret in _secret_values(runtime_environment):
            if secret and secret in str(record.get("log", "")):
                raise Phase0Error("candidate evidence contains a standalone secret value")
        suite_map[key] = record

    actual_candidates = {
        (value.module_id, str(value.suite.get("id"))): value
        for value in inventory.suites
    }
    coverage_keys = {
        (module_id, module_id + ".missing-contract-coverage"): "contract"
        for module_id in inventory.modules_without_contract
    }
    coverage_keys.update(
        {
            (module_id, module_id + ".missing-integration-coverage"): "integration"
            for module_id in inventory.modules_without_integration
        }
    )
    expected_suite_keys = set(actual_candidates).union(coverage_keys)
    if set(suite_map) != expected_suite_keys:
        raise Phase0Error("candidate suite evidence inventory is incomplete or expanded")

    receipt_test_paths = {
        value.get("path")
        for value in receipt.get("tests", [])
        if isinstance(value, Mapping)
    }
    expected_tested_commit = head if mode == "CLEAN_CANDIDATE" else None
    suite_forwarded_environment: set[str] = set()
    for key, candidate in actual_candidates.items():
        record = suite_map[key]
        raw_plan = parse_candidate_suite(root, candidate)
        plan, target, target_sha, tree_sha, argv_sha = validate_candidate_plan(
            root, candidate, raw_plan, policy
        )
        binding = _policy_for_candidate(candidate, policy)
        outcome = {
            "status": record.get("status"),
            "exit_code": record.get("exit_code"),
            "executed_tests": record.get("executed_tests"),
            "minimum_tests": record.get("minimum_tests"),
            "log_sha256": record.get("log_sha256"),
            "reason": record.get("reason"),
        }
        expected_fields = {
            "evidence_id": _suite_evidence_id(candidate, head, argv_sha, outcome),
            "module_id": candidate.module_id,
            "suite_id": str(candidate.suite["id"]),
            "test_type": str(candidate.suite["type"]),
            "evidence_level": EXPECTED_LEVEL[str(candidate.suite["type"])],
            "evidence_kind": candidate.evidence_kind,
            "required": True,
            "declared_command_sha256": sha256_text(str(candidate.suite["command"])),
            "effective_argv_sha256": argv_sha,
            "test_target": target,
            "test_target_sha256": target_sha,
            "test_tree_sha256": tree_sha,
            "manifest_path": candidate.manifest_path.relative_to(root).as_posix(),
            "manifest_sha256": sha256_file(candidate.manifest_path),
            "agents_path": candidate.agents_path.relative_to(root).as_posix(),
            "agents_sha256": sha256_file(candidate.agents_path),
            "instruction_receipt_id": receipt.get("receipt_id"),
            "tested_commit": expected_tested_commit,
            "minimum_tests": binding.minimum_executed_tests,
        }
        for field, expected in expected_fields.items():
            if record.get(field) != expected:
                raise Phase0Error(
                    "candidate suite field is stale: {0}:{1}:{2}".format(
                        key[0], key[1], field
                    )
                )
        if target not in receipt_test_paths:
            raise Phase0Error("candidate receipt does not bind executed test target: " + target)
        required_environment = (
            binding.required_environment
            if isinstance(binding, IntegrationPolicy)
            else ()
        )
        forwarded = [
            value for value in required_environment if os.environ.get(value)
        ]
        if record.get("forwarded_environment_keys") != sorted(forwarded):
            raise Phase0Error("candidate suite environment binding is incorrect")
        suite_forwarded_environment.update(forwarded)
        status = record.get("status")
        if status == "PASS":
            executed = record.get("executed_tests")
            if (
                len(forwarded) != len(required_environment)
                or record.get("exit_code") != 0
                or not isinstance(executed, int)
                or isinstance(executed, bool)
                or executed < binding.minimum_executed_tests
                or record.get("reason") is not None
                or not _is_canonical_withheld_suite_log(record)
            ):
                raise Phase0Error("PASS suite lacks policy-floor execution proof")
        elif record.get("reason") is None or record.get("exit_code") in (None, 0):
            raise Phase0Error("non-PASS suite lacks a failure reason/exit code")

    if sorted(suite_forwarded_environment) != environment.get(
        "forwarded_environment_keys"
    ):
        raise Phase0Error(
            "candidate top-level forwarded environment does not equal suite union"
        )

    for key, test_type in coverage_keys.items():
        record = suite_map[key]
        module_root = root / "Modules" / key[0]
        synthetic = CandidateSuite(
            module_id=key[0],
            module_root=module_root,
            manifest_path=module_root / "module.yaml",
            agents_path=module_root / "AGENTS.md",
            manifest_sha256=sha256_file(module_root / "module.yaml"),
            agents_sha256=sha256_file(module_root / "AGENTS.md"),
            suite={"id": key[1], "type": test_type, "command": None},
            evidence_kind="COVERAGE",
            integration_policy=None,
        )
        expected_message = (
            "public contract owner lacks required Contract/CONTRACT_VERIFIED suite"
            if test_type == "contract"
            else "minimumVerification requires required Integration/INTEGRATION_VERIFIED suite"
        )
        expected_outcome = {
            "status": "FAIL",
            "exit_code": 1,
            "executed_tests": 0,
            "minimum_tests": None,
            "log_sha256": sha256_text(expected_message),
            "reason": expected_message,
        }
        if (
            record.get("evidence_id")
            != _suite_evidence_id(synthetic, head, None, expected_outcome)
            or record.get("status") != "FAIL"
            or record.get("evidence_kind") != "COVERAGE"
            or record.get("exit_code") != 1
            or record.get("executed_tests") != 0
            or record.get("effective_argv_sha256") is not None
            or record.get("test_target") is not None
            or record.get("instruction_receipt_id") != receipt.get("receipt_id")
            or record.get("log") != expected_message
            or record.get("reason") != expected_message
        ):
            raise Phase0Error("coverage blocker evidence is not canonical: " + ":".join(key))

    statuses = [str(value.get("status")) for value in checks] + [
        str(value.get("status")) for value in suites
    ]
    expected_summary = {
        "required": len(statuses),
        "pass": sum(value == "PASS" for value in statuses),
        "fail": sum(value == "FAIL" for value in statuses),
        "infra_error": sum(value == "INFRA_ERROR" for value in statuses),
        "other_non_pass": sum(
            value not in ("PASS", "FAIL", "INFRA_ERROR") for value in statuses
        ),
    }
    expected_overall = (
        "PASS" if statuses and all(value == "PASS" for value in statuses) else "FAIL"
    )
    if evidence.get("summary") != expected_summary or evidence.get("overall_status") != expected_overall:
        raise Phase0Error("candidate summary/overall status is not recomputable")


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--level",
        required=True,
        choices=("contract", "integration"),
        help="candidate layer; integration cumulatively runs Contract and Integration suites",
    )
    parser.add_argument("--base", default=None, help="instruction receipt baseline commit")
    parser.add_argument(
        "--evidence",
        default=None,
        help=(
            "structured evidence JSON path; when omitted, a unique run-id "
            "directory under Reports/ci/candidate-runs is used"
        ),
    )
    parser.add_argument(
        "--diagnostic-workspace",
        action="store_true",
        help="allow an explicitly non-releasable dirty-workspace diagnostic run",
    )
    parser.add_argument(
        "--timeout-seconds",
        type=int,
        default=600,
        help="per-suite timeout (default: 600)",
    )
    arguments = parser.parse_args(argv)
    if arguments.timeout_seconds < 1 or arguments.timeout_seconds > 3600:
        parser.error("--timeout-seconds must be between 1 and 3600")
    return arguments


def _default_candidate_evidence_path(level: str, run_id: str) -> Path:
    if level not in ("contract", "integration"):
        raise Phase0Error("candidate level is invalid")
    if re.fullmatch(r"[0-9a-f]{32}", run_id) is None:
        raise Phase0Error("candidate publication run id is invalid")
    return (
        DEFAULT_CANDIDATE_RUNS_ROOT
        / level
        / run_id
        / "candidate-evidence.json"
    )


def _run_candidate_gate(
    arguments: argparse.Namespace,
    evidence_path: Path,
    evidence_publication: EvidencePublication,
) -> int:
    requested_level = EXPECTED_LEVEL[arguments.level]
    started_at = _utc_now()
    checks: List[Dict[str, Any]] = []
    suite_results: List[Dict[str, Any]] = []
    try:
        head = _candidate_git_output(ROOT, ["rev-parse", "HEAD^{commit}"])
        baseline = _candidate_git_output(
            ROOT,
            ["rev-parse", (arguments.base or "HEAD") + "^{commit}"],
        )
        start_workspace = _workspace_snapshot(ROOT, baseline)
    except Phase0Error as exc:
        print("ERROR: " + str(exc), file=sys.stderr)
        return 1

    dirty = not start_workspace["clean"]
    diagnostic_run = bool(arguments.diagnostic_workspace or dirty)
    mode = (
        "WORKSPACE_DIAGNOSTIC_ONLY" if diagnostic_run else "CLEAN_CANDIDATE"
    )
    workspace_status = (
        "PASS" if not dirty or arguments.diagnostic_workspace else "FAIL"
    )
    checks.append(
        _check(
            "workspace-start-boundary",
            workspace_status,
            (
                "workspace is clean"
                if not dirty
                else "dirty workspace accepted only as explicit diagnostic"
                if arguments.diagnostic_workspace
                else "dirty workspace requires --diagnostic-workspace"
            ),
            {
                "clean": not dirty,
                "dirty_entry_count": start_workspace["dirty_entry_count"],
                "head": head,
            },
            0 if workspace_status == "PASS" else 1,
        )
    )

    environment, toolchain_check = _toolchain_check(ROOT)
    checks.append(toolchain_check)
    try:
        governance = validate_governance(ROOT, require_schema=True)
        checks.append(
            _check(
                "module-governance-prerequisite",
                "PASS",
                "registered module governance is structurally valid",
                {
                    "module_count": governance.get("module_count"),
                    "contract_count": governance.get("contract_count"),
                },
                0,
            )
        )
    except (Phase0Error, OSError, ValueError) as exc:
        checks.append(
            _check("module-governance-prerequisite", "FAIL", str(exc), {}, 1)
        )

    try:
        policy = load_candidate_policy(ROOT)
        inventory = discover_candidate_inventory(ROOT, requested_level, policy)
        checks.append(
            _check(
                "candidate-policy-and-inventory",
                "PASS" if not inventory.errors else "FAIL",
                "candidate inventory accepted"
                if not inventory.errors
                else "\n".join(inventory.errors),
                {
                    "module_count": len(inventory.module_ids),
                    "selected_suite_count": len(inventory.suites),
                    "error_count": len(inventory.errors),
                },
                0 if not inventory.errors else 1,
            )
        )
    except (Phase0Error, OSError, ValueError) as exc:
        print("ERROR: candidate inventory could not be built: " + str(exc), file=sys.stderr)
        return 1

    trust_anchors, trust_match = _trust_anchor_inventory(ROOT, baseline)
    ancestor = _candidate_git(
        ROOT, ["merge-base", "--is-ancestor", baseline, head]
    ).exit_code == 0
    trust_eligible = baseline != head and ancestor and trust_match
    trust_status = "PASS" if diagnostic_run or trust_eligible else "FAIL"
    checks.append(
        _check(
            "candidate-trust-anchor",
            trust_status,
            (
                "diagnostic run records but cannot authorize changed local trust anchors"
                if diagnostic_run
                else "all candidate trust roots match the explicit predecessor baseline"
                if trust_eligible
                else "clean candidate requires an unchanged explicit predecessor trust anchor"
            ),
            {
                "baseline_differs_from_head": baseline != head,
                "baseline_is_ancestor": ancestor,
                "all_trust_roots_match": trust_match,
                "trust_root_count": len(trust_anchors),
            },
            0 if trust_status == "PASS" else 1,
        )
    )

    runtime_environment = {
        key: os.environ[key]
        for key in ALLOWED_RUNTIME_ENVIRONMENT
        if os.environ.get(key)
    }
    phase0_payload, phase0_check = _phase0_prerequisite(
        ROOT,
        evidence_path,
        baseline,
        head,
        str(start_workspace["digest"]),
        diagnostic_run,
        _phase0_prerequisite_runtime_environment(),
        evidence_publication.run_id,
    )
    checks.append(phase0_check)
    if phase0_check["status"] != "PASS":
        print("ERROR: cumulative Phase0 prerequisite failed", file=sys.stderr)
        return 1

    try:
        changed_paths, changed_inventory = _changed_path_inventory(ROOT, baseline)
        intent = _build_upgrade_intent(
            ROOT,
            baseline,
            head,
            started_at,
            inventory,
            changed_paths,
        )
        receipt = _resolve_candidate_receipt(
            ROOT,
            baseline,
            inventory.module_ids,
            started_at,
        )
        checks.append(
            _check(
                "instruction-receipt-start",
                "PASS",
                "candidate InstructionReceipt is BOUND",
                {
                    "receipt_id": receipt["receipt_id"],
                    "scope_count": len(receipt["scope"]),
                    "contract_binding_count": len(receipt["contracts"]),
                    "manifest_binding_count": len(receipt["manifests"]),
                },
                0,
            )
        )
    except (Phase0Error, OSError, ValueError) as exc:
        print("ERROR: candidate instruction binding failed: " + str(exc), file=sys.stderr)
        return 1

    postgres_version: Optional[str] = None
    postgres_needed = bool(
        requested_level == "INTEGRATION_VERIFIED"
        and any(value.evidence_kind == "REAL_POSTGRESQL" for value in inventory.suites)
    )
    postgres_ready = False
    if postgres_needed:
        required_postgres_environment = sorted(
            {
                key
                for candidate in inventory.suites
                if candidate.evidence_kind == "REAL_POSTGRESQL"
                and candidate.integration_policy is not None
                for key in candidate.integration_policy.required_environment
            }
        )
        postgres_version, postgres_check = postgres_preflight(
            ROOT,
            runtime_environment,
            required_postgres_environment,
        )
        checks.append(postgres_check)
        postgres_ready = postgres_check["status"] == "PASS"
    else:
        checks.append(
            _check(
                "postgresql18-preflight",
                "PASS",
                "PostgreSQL preflight is not required for this candidate selection",
                {"required": False},
                0,
            )
        )

    receipt_id = str(receipt["receipt_id"])
    suite_results.extend(
        execute_candidate_suites(
            ROOT,
            inventory,
            policy,
            head,
            dirty,
            receipt_id,
            runtime_environment,
            postgres_ready,
            arguments.timeout_seconds,
        )
    )
    for module_id in inventory.modules_without_contract:
        suite_results.append(
            _coverage_suite_result(
                ROOT,
                module_id,
                "contract",
                head,
                dirty,
                receipt_id,
                "public contract owner lacks required Contract/CONTRACT_VERIFIED suite",
            )
        )
    for module_id in inventory.modules_without_integration:
        suite_results.append(
            _coverage_suite_result(
                ROOT,
                module_id,
                "integration",
                head,
                dirty,
                receipt_id,
                "minimumVerification requires required Integration/INTEGRATION_VERIFIED suite",
            )
        )

    try:
        current_receipt = _resolve_candidate_receipt(
            ROOT,
            baseline,
            inventory.module_ids,
            started_at,
        )
        receipt_valid = stable_json(current_receipt) == stable_json(receipt)
        checks.append(
            _check(
                "instruction-receipt-final-staleness",
                "PASS" if receipt_valid else "FAIL",
                "candidate InstructionReceipt remains BOUND"
                if receipt_valid
                else "candidate InstructionReceipt became stale",
                {"receipt_id": receipt.get("receipt_id")},
                0 if receipt_valid else 1,
            )
        )
    except (Phase0Error, OSError, ValueError) as exc:
        checks.append(
            _check(
                "instruction-receipt-final-staleness",
                "FAIL",
                str(exc),
                {},
                1,
            )
        )

    try:
        end_workspace = _workspace_snapshot(ROOT, baseline)
        stable = (
            end_workspace["head"] == head
            and end_workspace["digest"] == start_workspace["digest"]
        )
        clean_boundary = (
            start_workspace["clean"] and end_workspace["clean"]
        ) or arguments.diagnostic_workspace
        final_workspace_pass = stable and clean_boundary
        checks.append(
            _check(
                "workspace-end-stability",
                "PASS" if final_workspace_pass else "FAIL",
                "workspace and HEAD remained stable"
                if final_workspace_pass
                else "workspace or HEAD changed during candidate execution",
                {
                    "head_unchanged": end_workspace["head"] == head,
                    "digest_unchanged": end_workspace["digest"] == start_workspace["digest"],
                    "clean_end": end_workspace["clean"],
                    "dirty_entry_count": end_workspace["dirty_entry_count"],
                },
                0 if final_workspace_pass else 1,
            )
        )
    except Phase0Error as exc:
        print("ERROR: cannot establish final workspace boundary: " + str(exc), file=sys.stderr)
        return 1

    if mode == "WORKSPACE_DIAGNOSTIC_ONLY":
        for value in suite_results:
            value["tested_commit"] = None

    _redact_evidence_logs(checks, suite_results, runtime_environment)

    required_statuses = [str(value["status"]) for value in checks] + [
        str(value["status"]) for value in suite_results
    ]
    overall_status = "PASS" if required_statuses and all(value == "PASS" for value in required_statuses) else "FAIL"
    summary = {
        "required": len(required_statuses),
        "pass": sum(value == "PASS" for value in required_statuses),
        "fail": sum(value == "FAIL" for value in required_statuses),
        "infra_error": sum(value == "INFRA_ERROR" for value in required_statuses),
        "other_non_pass": sum(
            value not in ("PASS", "FAIL", "INFRA_ERROR") for value in required_statuses
        ),
    }
    clean_stable = (
        mode == "CLEAN_CANDIDATE"
        and start_workspace["clean"]
        and end_workspace["clean"]
        and start_workspace["digest"] == end_workspace["digest"]
        and head == end_workspace["head"]
    )
    commit_attribution = head if clean_stable and trust_eligible else None
    actual_forwarded_keys = sorted(
        {
            key
            for result in suite_results
            for key in result.get("forwarded_environment_keys", [])
        }
    )
    finished_at = _utc_now()
    evidence: Dict[str, Any] = {
        "schema_version": SCHEMA_VERSION,
        "gate": GATE_NAME,
        "mode": mode,
        "diagnostic_requested": bool(arguments.diagnostic_workspace),
        "requested_verification_level": requested_level,
        "candidate_verification_level": None,
        "verification_level": None,
        "signed": False,
        "formal_evidence_eligible": False,
        "overall_status": overall_status,
        "commit_sha": commit_attribution,
        "head_commit_observed": head,
        "baseline_commit": baseline,
        "workspace": {
            "clean_start": bool(start_workspace["clean"]),
            "clean_end": bool(end_workspace["clean"]),
            "clean_post_write": bool(end_workspace["clean"]),
            "digest_start": str(start_workspace["digest"]),
            "digest_end": str(end_workspace["digest"]),
            "digest_post_write": str(end_workspace["digest"]),
            "dirty_entry_count_start": int(start_workspace["dirty_entry_count"]),
            "dirty_entry_count_end": int(end_workspace["dirty_entry_count"]),
            "dirty_entry_count_post_write": int(end_workspace["dirty_entry_count"]),
        },
        "started_at": started_at,
        "finished_at": finished_at,
        "environment": {
            "python": environment["python"],
            "python_executable": environment["python_executable"],
            "python_executable_realpath": environment["python_executable_realpath"],
            "requirements_ci_sha256": environment["requirements_ci_sha256"],
            "toolchain_lock_sha256": environment["toolchain_lock_sha256"],
            "python_packages": environment["python_packages"],
            "dotnet_sdk": environment["dotnet_sdk"],
            "forwarded_environment_keys": actual_forwarded_keys,
            "postgres_server_version_num": postgres_version,
        },
        "upgrade_intent": intent,
        "changed_paths": changed_inventory,
        "trust_anchors": trust_anchors,
        "instruction_receipt": receipt,
        "phase0_prerequisite": phase0_payload,
        "inventory": _inventory_payload(inventory, policy),
        "checks": checks,
        "suites": suite_results,
        "formal_test_evidence": [],
        "summary": summary,
        "limitations": [
            "This runner emits unsigned candidate evidence only and never issues a formal verification level.",
            "Test code is untrusted code; local stdout and test-count parsing cannot prove resistance to an arbitrarily malicious test project.",
            "Portable candidate JSON withholds arbitrary suite stdout/stderr; a formal runner must place raw output in an isolated immutable evidence store and bind its digest.",
            "Candidate PASS and local validator acceptance are unsigned diagnostics, are not release-authorization inputs, and cannot replace an independent evidence signature.",
            "Formal issuance requires an externally anchored policy/test-tree hash, an isolated Trusted Runner, an independent evidence issuer, and a separate release approver.",
            "The local runner does not provide a process-level network or filesystem capability sandbox; only dedicated non-production test credentials are permitted, and formal execution must add that isolation.",
            "The local checkout, Git metadata, and virtual environment remain writable by the same UID; formal execution must verify and mount approved inputs read-only.",
            "COMMITTED publication markers detect concurrent, torn, or mismatched local writes, but a same-UID malicious process remains outside the repository-level trust boundary.",
            "SIMULATION evidence is labelled and cannot substitute for real PostgreSQL, Windows, ZennoDroid, GBrain, device, canary, or scale evidence.",
            "No Windows, ZennoDroid, ADB, GBrain, real-device, canary, or scale verification is claimed.",
        ],
    }
    evidence["evidence_sha256"] = sha256_text(stable_json(evidence))
    try:
        validate_candidate_evidence(evidence, ROOT)
        write_evidence(
            evidence_path,
            evidence,
            publication=evidence_publication,
            commit=False,
        )
        post_write = _workspace_snapshot(ROOT, baseline)
        evidence["workspace"]["clean_post_write"] = bool(post_write["clean"])
        evidence["workspace"]["digest_post_write"] = str(post_write["digest"])
        evidence["workspace"]["dirty_entry_count_post_write"] = int(
            post_write["dirty_entry_count"]
        )
        evidence["evidence_sha256"] = sha256_text(
            stable_json({key: value for key, value in evidence.items() if key != "evidence_sha256"})
        )
        validate_candidate_evidence(evidence, ROOT)
        write_evidence(
            evidence_path,
            evidence,
            publication=evidence_publication,
            commit=False,
        )
        final_snapshot = _workspace_snapshot(ROOT, baseline)
        if (
            final_snapshot["head"] != head
            or final_snapshot["digest"] != post_write["digest"]
            or final_snapshot["clean"] != post_write["clean"]
            or final_snapshot["dirty_entry_count"] != post_write["dirty_entry_count"]
        ):
            raise Phase0Error("workspace changed during the final atomic evidence write")
        evidence_publication.commit()
    except (Phase0Error, OSError, ValueError) as exc:
        print("ERROR: candidate evidence validation/write failed: " + str(exc), file=sys.stderr)
        return 1

    print("Candidate gate: " + overall_status)
    print("Requested level: " + requested_level)
    print("Candidate level: NONE")
    print("Formal verification level: NONE")
    print("Suites: {0}".format(len(suite_results)))
    print("Evidence: " + str(evidence_path))
    print("COMMITTED marker: " + str(evidence_publication.marker_path))
    return 0 if overall_status == "PASS" else 1


def main(argv: Optional[Sequence[str]] = None) -> int:
    arguments = parse_arguments(argv)
    publication_run_id = _new_publication_run_id()
    try:
        raw_evidence_path = (
            Path(arguments.evidence)
            if arguments.evidence is not None
            else _default_candidate_evidence_path(
                arguments.level, publication_run_id
            )
        )
        evidence_path = _safe_evidence_path(
            ROOT,
            raw_evidence_path,
        )
        with EvidencePublication(
            evidence_path, run_id=publication_run_id
        ) as evidence_publication:
            return _run_candidate_gate(
                arguments, evidence_path, evidence_publication
            )
    except (OSError, Phase0Error, RuntimeError) as exc:
        print("ERROR: candidate evidence publication could not start: " + str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
