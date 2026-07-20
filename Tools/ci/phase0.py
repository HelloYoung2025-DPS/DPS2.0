#!/usr/bin/env python3
"""Deterministic Phase 0 governance and evidence primitives.

This module deliberately uses only the Python standard library.  CI and local
verification therefore validate the repository that is already checked out;
they never download a parser or silently skip governance checks.
"""

from __future__ import annotations

import ast
import errno
import fnmatch
import hashlib
import json
import os
import platform
import re
import shlex
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterable, List, Mapping, Optional, Sequence, Set, Tuple


EVIDENCE_STATUSES: Tuple[str, ...] = (
    "PASS",
    "FAIL",
    "SKIP",
    "PARTIAL",
    "NOT_RUN",
    "INFRA_ERROR",
    "NOT_APPLICABLE",
)
VERIFICATION_LEVEL = "REPOSITORY_STATIC_VERIFIED"
REQUIRED_PYTHON = (3, 12, 13)
REQUIRED_NODE_VERSION = "v24.18.0"
REQUIRED_DOTNET_SDK = "10.0.301"
PINNED_GITHUB_ACTIONS: Mapping[str, Tuple[str, str]] = {
    "actions/checkout": (
        "08c6903cd8c0fde910a37f88322edcfb5dd907a8",
        "v5.0.0",
    ),
    "actions/setup-python": (
        "e797f83bcb11b83ae66e0230d6156d7c80228e7c",
        "v6.0.0",
    ),
    "actions/setup-node": (
        "2028fbc5c25fe9cf00d9f06a71cc4710d4507903",
        "v6.0.0",
    ),
    "actions/setup-dotnet": (
        "d4c94342e560b34958eacfc5d055d21461ed1c5d",
        "v5.0.0",
    ),
    "actions/upload-artifact": (
        "ea165f8d65b6e75b540449e92b4886f43607fa02",
        "v4.6.2",
    ),
}
MODULES_DIRECTORY = "Modules"
MODULE_AGENTS_FRONT_MATTER = {
    "agents_spec": "dps.agents/v1",
    "manifest": "./module.yaml",
    "applies_to": ".",
}
REQUIRED_COMMUNICATION_FIELDS = {
    "peer_module",
    "contract_id",
    "direction",
    "transport",
    "timeout",
    "retry_policy",
    "idempotency_key",
    "auth_scope",
    "failure_mode",
}
EXTERNAL_PEERS = {
    "gbrain-company",
    "postgresql",
    "windows-edge",
    "zennodroid",
    "external",
}
KNOWN_RUNTIME_ROOTS = (
    "Core",
    "Modules/Core",
    "Modules/Decision",
    "Modules/Persona",
    "Modules/Report",
    "ZDProjects",
    "Extensions",
    "Config",
    "Configs/Manifests",
    "Data",
    "Tools/app_onboarder",
)
KNOWN_RUNTIME_FILES = ("Tests/playwright_dps_test.js",)
LEGACY_UNREGISTERED_MODULE_DIRECTORIES = {"Core", "Decision", "Persona", "Report"}
COMMON_CONTRACT_FIELDS = {
    "schema_version",
    "contract_id",
    "producer_module",
    "soul_id",
    "device_binding_id",
    "platform_account_id",
    "trace_id",
    "idempotency_key",
    "occurred_at",
    "privacy_class",
}
IDENTITY_FIELD_PATTERNS = {
    "soul_id": r"^soul_[a-f0-9]{64}$(?![\s\S])",
    "device_binding_id": r"^db_[a-f0-9]{32}$(?![\s\S])",
    "platform_account_id": r"^pa_[a-f0-9]{32}$(?![\s\S])",
    "trace_id": r"^trace_[a-f0-9]{32}$(?![\s\S])",
    "idempotency_key": r"^idem_[a-f0-9]{64}$(?![\s\S])",
}
REQUIRED_MODULE_DIRECTORIES = (
    "src",
    "contracts/provided",
    "contracts/consumed",
    "tests",
    "migrations",
    "operations",
)
REQUIRED_MODULE_FILES = ("CHANGELOG.md",)
CONTRACT_COMPATIBILITY_MODES = frozenset(
    {"active", "compat-read", "quarantine-only", "retired"}
)
RUNNABLE_CONTRACT_MODE = "active"
READABLE_CONSUMER_MODES = frozenset({"active", "compat-read"})
COMPATIBILITY_POLICY_RELATIVE_PATH = (
    "governance/policies/compatibility-policy.yaml"
)
COMPATIBILITY_MATRIX_SCHEMA_RELATIVE_PATH = (
    "governance/verification/f9-compatibility-matrix.v2.schema.json"
)


class Phase0Error(Exception):
    """A deterministic policy or validation failure."""


@dataclass(frozen=True)
class CommandResult:
    command: List[str]
    exit_code: int
    duration_ms: int
    output: str


@dataclass
class ModuleRecord:
    module_id: str
    root: Path
    manifest_path: Path
    agents_path: Path
    manifest: Dict[str, Any]
    ownership_patterns: List[str]
    dependencies: Set[str]
    provides: Dict[str, Set[int]]
    consumes: Dict[str, Set[int]]
    provided_modes: Dict[Tuple[str, int], str]
    consumed_modes: Dict[Tuple[str, int], str]


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_text(value: str) -> str:
    return sha256_bytes(value.encode("utf-8"))


def sha256_file(path: Path) -> str:
    return sha256_bytes(path.read_bytes())


def stable_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def relative(root: Path, path: Path) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


def run_command(
    command: Sequence[str],
    cwd: Path,
    timeout_seconds: int = 300,
    env: Optional[Mapping[str, str]] = None,
) -> CommandResult:
    started = time.monotonic()
    try:
        process = subprocess.Popen(
            list(command),
            cwd=str(cwd),
            env=dict(env) if env is not None else None,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            start_new_session=(os.name == "posix"),
        )
        try:
            output, _ = process.communicate(timeout=timeout_seconds)
            output = output or ""
            exit_code = process.returncode
        except subprocess.TimeoutExpired as exc:
            captured = exc.output or ""
            if isinstance(captured, bytes):
                captured = captured.decode("utf-8", errors="replace")
            try:
                if os.name == "posix":
                    os.killpg(process.pid, signal.SIGKILL)
                else:
                    process.kill()
            except (OSError, ProcessLookupError):
                try:
                    process.kill()
                except OSError:
                    pass
            final_output, _ = process.communicate()
            if isinstance(final_output, bytes):
                final_output = final_output.decode("utf-8", errors="replace")
            output = (final_output or captured) + "\nERROR: command timed out"
            exit_code = 124
    except OSError as exc:
        output = "ERROR: {0}".format(exc)
        exit_code = 127
    return CommandResult(
        command=list(command),
        exit_code=exit_code,
        duration_ms=int((time.monotonic() - started) * 1000),
        output=output,
    )


def git_output(root: Path, arguments: Sequence[str], allow_failure: bool = False) -> str:
    result = run_command(["git"] + list(arguments), root, timeout_seconds=60)
    if result.exit_code != 0 and not allow_failure:
        raise Phase0Error(
            "git {0} failed ({1}): {2}".format(
                " ".join(arguments), result.exit_code, result.output.strip()
            )
        )
    return result.output.strip()


def repository_files(root: Path) -> List[str]:
    output = git_output(
        root,
        ["ls-files", "--cached", "--others", "--exclude-standard"],
    )
    return sorted(
        value.replace("\\", "/")
        for value in output.splitlines()
        if value and (root / value).is_file()
    )


def load_json_compatible_yaml(path: Path) -> Dict[str, Any]:
    """Load manifests without an implicit network-installed YAML dependency.

    DPS manifests are intentionally JSON-compatible YAML.  A very small,
    fail-closed mapping parser is retained for AGENTS front matter only.  Full
    manifests must parse as JSON so duplicate/implicit YAML semantics cannot
    alter machine truth between runners.
    """

    raw = path.read_text(encoding="utf-8-sig")
    try:
        value = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise Phase0Error(
            "{0} must be JSON-compatible YAML: {1}".format(path.name, exc)
        )
    if not isinstance(value, dict):
        raise Phase0Error("{0} must contain an object".format(path.name))
    return value


def _compatibility_policy_errors(policy: Mapping[str, Any]) -> List[str]:
    """Validate the fail-closed policy subset consumed by Phase0.

    The policy is executable governance, not explanatory prose.  Phase0 binds
    the exact fields it uses so weakening a mode, role source, or rejection
    behavior cannot silently change the generated matrix.
    """

    errors: List[str] = []

    def require(path: Sequence[str], expected: Any) -> None:
        current: Any = policy
        for part in path:
            if not isinstance(current, Mapping) or part not in current:
                errors.append("compatibility policy missing " + ".".join(path))
                return
            current = current[part]
        if current != expected:
            errors.append(
                "compatibility policy {0} must be {1}".format(
                    ".".join(path), stable_json(expected)
                )
            )

    require(("schemaVersion",), "dps.compatibility-policy/v1")
    require(
        ("compatibilitySnapshot", "schemaVersion"),
        "dps.compatibility-matrix/v2",
    )
    require(
        ("compatibilitySnapshot", "schemaRef"),
        COMPATIBILITY_MATRIX_SCHEMA_RELATIVE_PATH,
    )
    require(("supportWindow",), ["N", "N-1"])
    require(
        ("contractMajorModes", "allowedModes"),
        ["active", "compat-read", "quarantine-only", "retired"],
    )
    for field in (
        "unknownContractBehavior",
        "missingContractBehavior",
        "unknownMajorBehavior",
        "missingMajorBehavior",
        "unknownModeBehavior",
        "missingModeBehavior",
    ):
        require(("contractMajorModes", "resolution", field), "reject")
    require(
        ("contractMajorModes", "resolution", "exactIdentityRequired"), True
    )
    require(
        ("contractMajorModes", "resolution", "duplicateIdentityBehavior"),
        "reject",
    )
    require(
        ("contractMajorModes", "resolution", "implicitFallback"), "forbidden"
    )
    require(
        ("contractMajorModes", "countingRule", "modeAloneIsSufficient"), False
    )
    require(
        ("contractMajorModes", "modes", "active", "allowedDeclarations"),
        ["provided", "consumed"],
    )
    require(
        ("contractMajorModes", "modes", "compat-read", "allowedDeclarations"),
        ["consumed"],
    )
    require(
        (
            "contractMajorModes",
            "modes",
            "quarantine-only",
            "allowedDeclarations",
        ),
        ["provided", "consumed"],
    )
    require(
        ("contractMajorModes", "modes", "retired", "allowedDeclarations"),
        ["provided", "consumed"],
    )
    mode_semantics = {
        "active": {
            "wireAction": "decode-validate-and-use-exact-major",
            "decoder": "required",
            "encoder": "allowed-only-when-communication-direction-and-contract-producer-permit",
            "domainPath": "allowed-after-all-policy-and-approval-gates",
        },
        "compat-read": {
            "wireAction": "decode-validate-and-read-previous-major-without-runtime-execution",
            "decoder": "required",
            "encoder": "forbidden-for-this-major",
            "domainPath": "forbidden",
        },
        "quarantine-only": {
            "wireAction": "bounded-identify-quarantine-and-audit",
            "decoder": "routing-metadata-and-exact-quarantine-proof-only",
            "encoder": "forbidden",
            "domainPath": "forbidden",
            "businessSuccess": "forbidden",
        },
        "retired": {
            "wireAction": "reject",
            "decoder": "forbidden-in-runtime",
            "encoder": "forbidden",
            "domainPath": "forbidden",
            "businessSuccess": "forbidden",
        },
    }
    for mode, semantics in mode_semantics.items():
        for field, expected_value in semantics.items():
            require(
                ("contractMajorModes", "modes", mode, field), expected_value
            )
    for mode in ("active", "compat-read", "quarantine-only", "retired"):
        expected = mode == "active"
        for result_name in (
            "runnable",
            "deployability",
            "activeProducerConsumer",
            "candidateGreen",
        ):
            require(
                (
                    "contractMajorModes",
                    "modes",
                    mode,
                    "countsToward",
                    result_name,
                ),
                expected,
            )
    require(
        (
            "contractMajorModes",
            "producerConsumerModePairs",
            "activeRuntime",
        ),
        [["active", "active"]],
    )
    require(
        (
            "contractMajorModes",
            "producerConsumerModePairs",
            "mixedReadWindow",
        ),
        [["active", "compat-read"]],
    )
    require(
        (
            "contractMajorModes",
            "producerConsumerModePairs",
            "roleResolutionSource",
        ),
        "exact-contract-schema-producer_module-plus-reciprocal-communication-outbound-inbound",
    )
    require(
        (
            "contractMajorModes",
            "producerConsumerModePairs",
            "mixedReadWindowAccounting",
        ),
        "read-compatible-only-not-runnable-not-deployable-not-active-not-candidate-green",
    )
    require(
        (
            "contractMajorModes",
            "producerConsumerModePairs",
            "anyPairContainingQuarantineOnly",
        ),
        "not-runnable-not-deployable-not-active-not-candidate-green",
    )
    require(
        (
            "contractMajorModes",
            "producerConsumerModePairs",
            "anyPairContainingRetired",
        ),
        "reject",
    )
    require(
        ("runtimeRoleResolution", "contractOwnerSource"),
        "provided-manifest-ownerModule",
    )
    require(
        ("runtimeRoleResolution", "runtimeProducerSource"),
        "exact-contract-schema-producer_module-const-or-enum",
    )
    require(
        ("runtimeRoleResolution", "runtimeConsumerSource"),
        "reciprocal-communication-outbound-inbound-receiver",
    )
    require(("runtimeRoleResolution", "reciprocalRequired"), True)
    require(
        ("runtimeRoleResolution", "relayProducerRule"),
        "outbound-preserveProducer-retains-exact-schema-producer",
    )
    require(
        ("runtimeRoleResolution", "communicationPairDigest"),
        "sha256-stable-canonical-reciprocal-pair",
    )
    require(("runtimeRoleResolution", "ownerIsRuntimeProducerByDefault"), False)
    require(
        ("runtimeRoleResolution", "unresolvedRoleBehavior"),
        "not-runnable-not-deployable-not-active-not-candidate-green",
    )
    require(
        ("compatibilityGroups", "candidateGreenWithoutEvidence"), False
    )
    require(
        ("compatibilityGroups", "phase0Authority"),
        "identify-only-never-authorize",
    )
    require(
        ("compatibilityGroups", "releaseAuthority"),
        "exact-signed-release-bom-plus-complete-group-execution-evidence",
    )
    require(
        ("candidateSelection", "activePathRequiresExactModePair"), True
    )
    require(
        ("candidateSelection", "unknownOrMissingContractMajor"), "fail"
    )
    require(("candidateSelection", "unknownOrMissingMode"), "fail")
    require(
        ("candidateSelection", "quarantineOnlyAsPositiveEvidence"),
        "forbidden",
    )
    require(
        ("candidateSelection", "mixedReadPathRequiresExactModePair"), True
    )
    return errors


def load_compatibility_policy(root: Path) -> Tuple[Dict[str, Any], str]:
    path = root / COMPATIBILITY_POLICY_RELATIVE_PATH
    if not path.is_file():
        raise Phase0Error(
            "compatibility policy is required: "
            + COMPATIBILITY_POLICY_RELATIVE_PATH
        )
    policy = load_json_compatible_yaml(path)
    errors = _compatibility_policy_errors(policy)
    if errors:
        raise Phase0Error("\n".join(sorted(set(errors))))
    return policy, sha256_file(path)


def _parse_front_matter_scalar(raw: str) -> Any:
    value = raw.strip()
    if not value:
        return ""
    if value.startswith(("!", "&", "*", "|", ">")):
        raise Phase0Error("unsafe or multiline YAML is forbidden in AGENTS front matter")
    if value.startswith(('"', "'")):
        try:
            parsed = ast.literal_eval(value)
        except (SyntaxError, ValueError) as exc:
            raise Phase0Error("invalid quoted front matter value: {0}".format(exc))
        if not isinstance(parsed, str):
            raise Phase0Error("front matter quoted values must be strings")
        return parsed
    lowered = value.lower()
    if lowered in ("true", "false"):
        return lowered == "true"
    if lowered in ("null", "~"):
        return None
    if re.fullmatch(r"-?[0-9]+", value):
        return int(value)
    if re.fullmatch(r"-?[0-9]+\.[0-9]+", value):
        return float(value)
    return value


def parse_agents_front_matter(path: Path) -> Dict[str, Any]:
    lines = path.read_text(encoding="utf-8-sig").splitlines()
    if not lines or lines[0].strip() != "---":
        raise Phase0Error("{0} is missing required front matter".format(path))
    result: Dict[str, Any] = {}
    closing_index: Optional[int] = None
    for index, line in enumerate(lines[1:], start=1):
        if line.strip() == "---":
            closing_index = index
            break
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        if line[:1].isspace() or ":" not in line:
            raise Phase0Error("nested or invalid AGENTS front matter is forbidden")
        key, raw_value = line.split(":", 1)
        key = key.strip()
        if not re.fullmatch(r"[a-z][a-z0-9_]*", key):
            raise Phase0Error("invalid AGENTS front matter key: " + key)
        if key in result:
            raise Phase0Error("duplicate AGENTS front matter key: " + key)
        result[key] = _parse_front_matter_scalar(raw_value)
    if closing_index is None:
        raise Phase0Error("{0} has unterminated front matter".format(path))
    return result


def find_agents_schema(root: Path) -> Optional[Path]:
    for candidate in (
        root / "governance" / "schemas" / "agents-frontmatter.schema.json",
        root / "Governance" / "schemas" / "agents-frontmatter.schema.json",
    ):
        if candidate.is_file():
            return candidate
    return None


def _schema_type_matches(value: Any, type_name: str) -> bool:
    if type_name == "object":
        return isinstance(value, dict)
    if type_name == "array":
        return isinstance(value, list)
    if type_name == "string":
        return isinstance(value, str)
    if type_name == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if type_name == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if type_name == "boolean":
        return isinstance(value, bool)
    if type_name == "null":
        return value is None
    raise Phase0Error("unsupported JSON Schema type: " + type_name)


def validate_json_schema(
    instance: Any,
    schema: Mapping[str, Any],
    root_schema: Optional[Mapping[str, Any]] = None,
    location: str = "$",
) -> List[str]:
    """Validate the deterministic JSON Schema subset used by governance files."""

    root_schema = root_schema or schema
    errors: List[str] = []
    if not isinstance(schema, Mapping):
        return [location + ": schema must be an object"]

    ref = schema.get("$ref")
    if ref is not None:
        if not isinstance(ref, str) or not ref.startswith("#/"):
            return [location + ": only local JSON Schema references are supported"]
        target: Any = root_schema
        try:
            for part in ref[2:].split("/"):
                key = part.replace("~1", "/").replace("~0", "~")
                target = target[key]
        except (KeyError, TypeError):
            return [location + ": unresolved schema reference " + ref]
        return validate_json_schema(instance, target, root_schema, location)

    for child in schema.get("allOf", []):
        errors.extend(validate_json_schema(instance, child, root_schema, location))
    if "if" in schema:
        condition_errors = validate_json_schema(
            instance, schema["if"], root_schema, location
        )
        selected = schema.get("then") if not condition_errors else schema.get("else")
        if isinstance(selected, Mapping):
            errors.extend(validate_json_schema(instance, selected, root_schema, location))
    if "anyOf" in schema:
        candidates = [
            validate_json_schema(instance, child, root_schema, location)
            for child in schema["anyOf"]
        ]
        if candidates and all(candidate for candidate in candidates):
            errors.append(location + ": does not match anyOf")
    if "oneOf" in schema:
        matches = sum(
            not validate_json_schema(instance, child, root_schema, location)
            for child in schema["oneOf"]
        )
        if matches != 1:
            errors.append(location + ": must match exactly one oneOf branch")

    if "const" in schema and instance != schema["const"]:
        errors.append(location + ": value does not match const")
    if "enum" in schema and instance not in schema["enum"]:
        errors.append(location + ": value is not in enum")

    declared_type = schema.get("type")
    if declared_type is not None:
        allowed_types = [declared_type] if isinstance(declared_type, str) else declared_type
        if not isinstance(allowed_types, list) or not all(
            isinstance(value, str) for value in allowed_types
        ):
            errors.append(location + ": invalid schema type declaration")
            return errors
        if not any(_schema_type_matches(instance, value) for value in allowed_types):
            errors.append(
                "{0}: expected type {1}, got {2}".format(
                    location, allowed_types, type(instance).__name__
                )
            )
            return errors

    if isinstance(instance, dict):
        required = schema.get("required", [])
        for key in required:
            if key not in instance:
                errors.append("{0}: missing required property {1}".format(location, key))
        properties = schema.get("properties", {})
        pattern_properties = schema.get("patternProperties", {})
        for key, value in instance.items():
            matched = False
            if key in properties:
                matched = True
                errors.extend(
                    validate_json_schema(
                        value, properties[key], root_schema, location + "." + key
                    )
                )
            for pattern, child_schema in pattern_properties.items():
                if re.search(pattern, key):
                    matched = True
                    errors.extend(
                        validate_json_schema(
                            value, child_schema, root_schema, location + "." + key
                        )
                    )
            additional = schema.get("additionalProperties", True)
            if not matched and additional is False:
                errors.append(location + ": unexpected property " + key)
            elif not matched and isinstance(additional, Mapping):
                errors.extend(
                    validate_json_schema(
                        value, additional, root_schema, location + "." + key
                    )
                )
        if "minProperties" in schema and len(instance) < schema["minProperties"]:
            errors.append(location + ": too few properties")

    if isinstance(instance, list):
        if "minItems" in schema and len(instance) < schema["minItems"]:
            errors.append(location + ": too few items")
        if "maxItems" in schema and len(instance) > schema["maxItems"]:
            errors.append(location + ": too many items")
        if schema.get("uniqueItems"):
            rendered = [stable_json(value) for value in instance]
            if len(rendered) != len(set(rendered)):
                errors.append(location + ": array items must be unique")
        items_schema = schema.get("items")
        if isinstance(items_schema, Mapping):
            for index, value in enumerate(instance):
                errors.extend(
                    validate_json_schema(
                        value,
                        items_schema,
                        root_schema,
                        "{0}[{1}]".format(location, index),
                    )
                )

    if isinstance(instance, str):
        if "minLength" in schema and len(instance) < schema["minLength"]:
            errors.append(location + ": string is too short")
        if "maxLength" in schema and len(instance) > schema["maxLength"]:
            errors.append(location + ": string is too long")
        if "pattern" in schema and re.search(schema["pattern"], instance) is None:
            errors.append(location + ": string does not match pattern")

    if isinstance(instance, (int, float)) and not isinstance(instance, bool):
        if "minimum" in schema and instance < schema["minimum"]:
            errors.append(location + ": value is below minimum")
        if "maximum" in schema and instance > schema["maximum"]:
            errors.append(location + ": value is above maximum")

    return errors


def _first_mapping_value(value: Mapping[str, Any], paths: Iterable[Sequence[str]]) -> Any:
    for path in paths:
        cursor: Any = value
        for key in path:
            if not isinstance(cursor, Mapping) or key not in cursor:
                break
            cursor = cursor[key]
        else:
            return cursor
    return None


def manifest_module_id(manifest: Mapping[str, Any]) -> Optional[str]:
    value = _first_mapping_value(
        manifest,
        (
            ("metadata", "id"),
            ("module", "id"),
            ("module_id",),
            ("moduleId",),
            ("id",),
        ),
    )
    return value if isinstance(value, str) else None


def _as_string_list(value: Any) -> List[str]:
    if value is None:
        return []
    if isinstance(value, str):
        return [value]
    if isinstance(value, list):
        return [item for item in value if isinstance(item, str)]
    return []


def manifest_ownership_patterns(manifest: Mapping[str, Any]) -> List[str]:
    paths: List[str] = []
    candidates = (
        ("ownership", "paths"),
        ("ownership", "sourcePaths"),
        ("ownership", "source_paths"),
        ("spec", "ownership", "paths"),
        ("spec", "ownership", "sourcePaths"),
        ("spec", "ownership", "source_paths"),
        ("source", "roots"),
        ("paths", "owned"),
        ("owned_paths",),
        ("ownedPaths",),
    )
    for candidate in candidates:
        paths.extend(_as_string_list(_first_mapping_value(manifest, (candidate,))))
    return sorted(set(normalize_repo_pattern(path) for path in paths))


def _walk_dependency_values(value: Any, category: Optional[str] = None) -> Iterable[str]:
    ignored_categories = {
        "version",
        "versions",
        "range",
        "compatibility",
        "policy",
        "timeout",
        "retry",
    }
    if isinstance(value, str):
        if category not in ignored_categories and re.fullmatch(r"[a-z0-9][a-z0-9-]*", value):
            yield value
    elif isinstance(value, list):
        for item in value:
            yield from _walk_dependency_values(item, category)
    elif isinstance(value, Mapping):
        for key in ("module", "module_id", "moduleId", "id"):
            module_value = value.get(key)
            if isinstance(module_value, str) and re.fullmatch(
                r"[a-z0-9][a-z0-9-]*", module_value
            ):
                yield module_value
                break
        else:
            for key, item in value.items():
                yield from _walk_dependency_values(item, key)


def manifest_dependencies(manifest: Mapping[str, Any]) -> Set[str]:
    containers = [
        _first_mapping_value(manifest, (("dependencies",),)),
        _first_mapping_value(manifest, (("spec", "dependencies"),)),
    ]
    dependencies: Set[str] = set()
    for container in containers:
        dependencies.update(_walk_dependency_values(container))
    return dependencies


def _contract_major_versions(item: Any) -> Set[int]:
    versions: Any = None
    if isinstance(item, Mapping):
        versions = item.get(
            "versions",
            item.get("supported_versions", item.get("version", item.get("major"))),
        )
    if versions is None:
        return set()
    if not isinstance(versions, list):
        versions = [versions]
    result: Set[int] = set()
    for value in versions:
        if isinstance(value, int):
            result.add(value)
        elif isinstance(value, str):
            match = re.search(r"(?:^|/|v)([0-9]+)(?:$|\.)", value)
            if match:
                result.add(int(match.group(1)))
    return result


def _contracts_from_container(container: Any) -> Dict[str, Set[int]]:
    result: Dict[str, Set[int]] = {}
    if container is None:
        return result
    values = container if isinstance(container, list) else [container]
    for item in values:
        if isinstance(item, str):
            contract_id = item
        elif isinstance(item, Mapping):
            raw_id = item.get("id", item.get("contract_id", item.get("contractId")))
            if not isinstance(raw_id, str):
                continue
            contract_id = raw_id
        else:
            continue
        result.setdefault(contract_id, set()).update(_contract_major_versions(item))
    return result


def manifest_contracts(
    manifest: Mapping[str, Any], direction: str
) -> Dict[str, Set[int]]:
    aliases = (direction, "provided" if direction == "provides" else "consumed")
    result: Dict[str, Set[int]] = {}
    for prefix in (("contracts",), ("spec", "contracts")):
        contract_root = _first_mapping_value(manifest, (prefix,))
        if not isinstance(contract_root, Mapping):
            continue
        for alias in aliases:
            for contract_id, versions in _contracts_from_container(
                contract_root.get(alias)
            ).items():
                result.setdefault(contract_id, set()).update(versions)
    return result


def manifest_contract_modes(
    manifest: Mapping[str, Any], direction: str
) -> Dict[Tuple[str, int], str]:
    """Return the exact compatibility mode for every declared contract major.

    A mode is deliberately bound to one declaration and one major.  Accepting a
    missing or unknown mode here would let legacy declarations silently become
    runnable, so this parser fails closed even when JSON Schema validation is
    disabled for an adversarial fixture.
    """

    if direction not in {"provides", "consumes"}:
        raise Phase0Error("unknown contract declaration direction: " + direction)
    aliases = (direction, "provided" if direction == "provides" else "consumed")
    result: Dict[Tuple[str, int], str] = {}
    for prefix in (("contracts",), ("spec", "contracts")):
        contract_root = _first_mapping_value(manifest, (prefix,))
        if not isinstance(contract_root, Mapping):
            continue
        for alias in aliases:
            container = contract_root.get(alias)
            if container is None:
                continue
            values = container if isinstance(container, list) else [container]
            for index, item in enumerate(values):
                if not isinstance(item, Mapping):
                    raise Phase0Error(
                        "contract {0}[{1}] must be an object with an explicit mode".format(
                            alias, index
                        )
                    )
                raw_id = item.get("id", item.get("contract_id", item.get("contractId")))
                if not isinstance(raw_id, str) or not raw_id:
                    raise Phase0Error(
                        "contract {0}[{1}] is missing a contract id".format(alias, index)
                    )
                majors = _contract_major_versions(item)
                if len(majors) != 1:
                    raise Phase0Error(
                        "contract {0}[{1}] {2} must declare exactly one major for a per-major mode".format(
                            alias, index, raw_id
                        )
                    )
                mode = item.get("mode")
                if not isinstance(mode, str) or mode not in CONTRACT_COMPATIBILITY_MODES:
                    rendered = "missing" if mode is None else repr(mode)
                    raise Phase0Error(
                        "contract {0}/v{1} has unknown or missing compatibility mode: {2}".format(
                            raw_id, next(iter(majors)), rendered
                        )
                    )
                if direction == "provides" and mode == "compat-read":
                    raise Phase0Error(
                        "provided contract {0}/v{1} cannot use compat-read".format(
                            raw_id, next(iter(majors))
                        )
                    )
                key = (raw_id, next(iter(majors)))
                if key in result:
                    raise Phase0Error(
                        "duplicate {0} contract-major declaration: {1}/v{2}".format(
                            direction, key[0], key[1]
                        )
                    )
                result[key] = mode
    return result


def _communication_edges(manifest: Mapping[str, Any]) -> List[Mapping[str, Any]]:
    root = _first_mapping_value(
        manifest,
        (
            ("communication",),
            ("communications",),
            ("spec", "communication"),
            ("spec", "communications"),
        ),
    )
    if root is None:
        return []
    if isinstance(root, list):
        return [item for item in root if isinstance(item, Mapping)]
    if isinstance(root, Mapping):
        edges: List[Mapping[str, Any]] = []
        for value in root.values():
            if isinstance(value, list):
                edges.extend(item for item in value if isinstance(item, Mapping))
            elif isinstance(value, Mapping) and (
                "peer_module" in value or "peerModule" in value
            ):
                edges.append(value)
        return edges
    return []


def normalize_repo_pattern(pattern: str) -> str:
    normalized = pattern.strip().replace("\\", "/")
    while normalized.startswith("./"):
        normalized = normalized[2:]
    normalized = re.sub(r"/+", "/", normalized).rstrip("/")
    if not normalized or normalized.startswith("/") or ".." in normalized.split("/"):
        raise Phase0Error("invalid ownership path: " + pattern)
    return normalized


def path_matches_pattern(path: str, pattern: str) -> bool:
    normalized_path = path.replace("\\", "/").casefold()
    normalized_pattern = normalize_repo_pattern(pattern).casefold()
    if any(character in normalized_pattern for character in "*?["):
        return fnmatch.fnmatchcase(normalized_path, normalized_pattern)
    return normalized_path == normalized_pattern or normalized_path.startswith(
        normalized_pattern + "/"
    )


def ownership_patterns_obviously_overlap(first: str, second: str) -> bool:
    """Reject exact and prefix overlaps even before a file occupies the path."""

    left = normalize_repo_pattern(first).casefold()
    right = normalize_repo_pattern(second).casefold()
    if left == right:
        return True

    def static_prefix(pattern: str) -> str:
        wildcard_positions = [
            index for index in (pattern.find("*"), pattern.find("?"), pattern.find("[")) if index >= 0
        ]
        end = min(wildcard_positions) if wildcard_positions else len(pattern)
        return pattern[:end].rstrip("/")

    left_prefix = static_prefix(left)
    right_prefix = static_prefix(right)
    left_recursive = left.endswith("/**") or not any(value in left for value in "*?[")
    right_recursive = right.endswith("/**") or not any(value in right for value in "*?[")
    if left_recursive and right_prefix and (
        right_prefix == left_prefix or right_prefix.startswith(left_prefix + "/")
    ):
        return True
    if right_recursive and left_prefix and (
        left_prefix == right_prefix or left_prefix.startswith(right_prefix + "/")
    ):
        return True
    return False


def discover_registered_module_dirs(root: Path) -> List[Path]:
    module_roots = [
        child
        for child in root.iterdir()
        if child.is_dir() and child.name.casefold() == "modules"
    ]
    if len(module_roots) != 1 or module_roots[0].name != MODULES_DIRECTORY:
        raise Phase0Error(
            "registered modules must use the exact physical directory Modules/<module-id>"
        )
    modules_root = root / MODULES_DIRECTORY
    if not modules_root.is_dir():
        raise Phase0Error("canonical Modules directory does not exist")
    registered: List[Path] = []
    for child in sorted(modules_root.iterdir(), key=lambda value: value.name.casefold()):
        if not child.is_dir():
            continue
        if child.name in LEGACY_UNREGISTERED_MODULE_DIRECTORIES:
            continue
        if not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", child.name):
            raise Phase0Error(
                "unexpected Modules directory outside the legacy allowlist: "
                + relative(root, child)
            )
        registered.append(child)
    if not registered:
        raise Phase0Error("no registered modules found under Modules/<module-id>")
    return registered


# The exact set of module manifest majors this gate implements, and the file each
# one lives in.  Discovering majors by globbing module-manifest*.schema.json and
# believing whatever schemaVersion.const each file declared made the supported set
# candidate-defined: dropping in a permissive module-manifest.v999.schema.json and
# switching a manifest to dps.module/v999 passed validation with the whole agents
# block removed.  A major is supported because it is named here -- in a file bound
# into the candidate trust anchor -- not because a schema file exists.
SUPPORTED_MANIFEST_SCHEMA_FILES: Dict[str, str] = {
    "dps.module/v1": "module-manifest.v1.schema.json",
    "dps.module/v2": "module-manifest.schema.json",
}


_MANIFEST_MAJOR_BY_FILE: Dict[str, str] = {
    name: major for major, name in SUPPORTED_MANIFEST_SCHEMA_FILES.items()
}


def _manifest_schema_directory(root: Path) -> Optional[Path]:
    for base_name in ("governance", "Governance"):
        base = root / base_name / "schemas"
        if base.is_dir():
            return base
    return None


def _manifest_schema_paths(root: Path) -> List[Path]:
    """The schema file of every supported major, in registry order.

    R0-B publishes ``dps.module/v2`` (resolver removed) beside the retained
    ``dps.module/v1`` (resolver-bearing), so the gate loads both and dispatches
    per manifest rather than pinning one "current" schema -- but only these two.
    """
    base = _manifest_schema_directory(root)
    if base is None:
        fallbacks = (
            root / "governance" / "module-manifest.schema.json",
            root / "Governance" / "schemas" / "module.schema.json",
        )
        return [path for path in fallbacks if path.is_file()]
    return [base / name for name in SUPPORTED_MANIFEST_SCHEMA_FILES.values()]


def load_manifest_schemas(root: Path) -> Dict[str, Mapping[str, Any]]:
    """Load module-manifest schemas keyed by the major each one pins.

    Each schema self-declares its major via ``schemaVersion.const``; a manifest
    is validated against the schema whose const matches its own declared
    ``schemaVersion``.  Unknown or missing majors fail closed in
    ``_load_module_record`` rather than silently reusing another major's rules.
    """
    schemas: Dict[str, Mapping[str, Any]] = {}
    base = _manifest_schema_directory(root)
    if base is not None:
        # An unregistered module-manifest*.schema.json is refused rather than
        # ignored: leaving it on disk is how a candidate would try to introduce a
        # major of its own, and a silently skipped file looks identical to one the
        # gate understood.
        expected = set(SUPPORTED_MANIFEST_SCHEMA_FILES.values())
        unexpected = sorted(
            path.name for path in base.glob("module-manifest*.schema.json") if path.name not in expected
        )
        if unexpected:
            raise Phase0Error(
                "unregistered module manifest schema files: " + ", ".join(unexpected)
            )
    for path in _manifest_schema_paths(root):
        try:
            value = json.loads(path.read_text(encoding="utf-8-sig"))
        except Exception as exc:
            raise Phase0Error(
                "invalid module manifest schema {0}: {1}".format(relative(root, path), exc)
            )
        if not isinstance(value, dict):
            raise Phase0Error(
                "module manifest schema {0} root is not an object".format(relative(root, path))
            )
        properties = value.get("properties")
        version_schema = properties.get("schemaVersion") if isinstance(properties, dict) else None
        const = version_schema.get("const") if isinstance(version_schema, dict) else None
        if not isinstance(const, str):
            raise Phase0Error(
                "module manifest schema {0} does not pin a schemaVersion const".format(
                    relative(root, path)
                )
            )
        if const in schemas:
            raise Phase0Error("duplicate module manifest schema major " + const)
        expected_major = _MANIFEST_MAJOR_BY_FILE.get(path.name)
        if expected_major is not None and const != expected_major:
            raise Phase0Error(
                "module manifest schema {0} declares major {1}, expected {2}".format(
                    relative(root, path), const, expected_major
                )
            )
        schemas[const] = value
    if base is not None and set(schemas) != set(SUPPORTED_MANIFEST_SCHEMA_FILES):
        raise Phase0Error(
            "module manifest majors on disk do not match the supported registry"
        )
    return schemas


def _load_module_record(
    root: Path,
    module_root: Path,
    schemas: Optional[Mapping[str, Mapping[str, Any]]],
) -> ModuleRecord:
    agents_path = module_root / "AGENTS.md"
    manifest_path = module_root / "module.yaml"
    if not agents_path.is_file():
        raise Phase0Error("registered module missing AGENTS.md: " + relative(root, module_root))
    if not manifest_path.is_file():
        raise Phase0Error("registered module missing module.yaml: " + relative(root, module_root))

    nested_agents = sorted(
        path for path in module_root.rglob("AGENTS.md") if path != agents_path
    )
    if nested_agents:
        raise Phase0Error(
            "nested AGENTS.md forbidden: "
            + ", ".join(relative(root, path) for path in nested_agents)
        )
    nested_manifests = sorted(
        path for path in module_root.rglob("module.yaml") if path != manifest_path
    )
    if nested_manifests:
        raise Phase0Error(
            "nested module.yaml forbidden: "
            + ", ".join(relative(root, path) for path in nested_manifests)
        )

    missing_layout: List[str] = []
    unsafe_layout: List[str] = []
    for required_directory in REQUIRED_MODULE_DIRECTORIES:
        path = module_root / required_directory
        if not path.is_dir():
            missing_layout.append(required_directory + "/")
            continue
        cursor = module_root
        traverses_symlink = False
        for part in Path(required_directory).parts:
            cursor = cursor / part
            if cursor.is_symlink():
                traverses_symlink = True
                break
        if traverses_symlink:
            unsafe_layout.append(required_directory + "/ (symlink)")
            continue
        # Git does not preserve an empty directory.  Requiring a real file under
        # each standard root makes the module layout reproducible from a clean
        # checkout instead of accepting a local-only empty folder.
        if not any(
            candidate.is_file()
            and not any(
                part in {"bin", "obj", "__pycache__", ".pytest_cache"}
                for part in candidate.relative_to(path).parts
            )
            for candidate in path.rglob("*")
        ):
            missing_layout.append(required_directory + "/ (empty)")
    for required_file in REQUIRED_MODULE_FILES:
        path = module_root / required_file
        if not path.is_file():
            missing_layout.append(required_file)
        elif path.is_symlink():
            unsafe_layout.append(required_file + " (symlink)")
    if missing_layout or unsafe_layout:
        messages: List[str] = []
        if missing_layout:
            messages.append("missing standard module layout: " + ", ".join(missing_layout))
        if unsafe_layout:
            messages.append("unsafe standard module layout: " + ", ".join(unsafe_layout))
        raise Phase0Error(
            "{0} {1}".format(relative(root, module_root), "; ".join(messages))
        )

    manifest = load_json_compatible_yaml(manifest_path)
    if schemas:
        declared = manifest.get("schemaVersion")
        if not isinstance(declared, str) or declared not in schemas:
            raise Phase0Error(
                "module manifest {0} declares unknown or missing schemaVersion {1!r}; "
                "supported majors: {2}".format(
                    relative(root, manifest_path),
                    declared,
                    ", ".join(sorted(schemas)) or "(none)",
                )
            )
        schema_errors = validate_json_schema(manifest, schemas[declared])
        if schema_errors:
            raise Phase0Error(
                "invalid module manifest {0}: {1}".format(
                    relative(root, manifest_path), "; ".join(schema_errors)
                )
            )

    module_id = manifest_module_id(manifest)
    expected_id = module_root.name
    if not module_id or module_id != expected_id:
        raise Phase0Error(
            "manifest module id must exactly match directory {0}: got {1}".format(
                expected_id, module_id
            )
        )
    if not re.fullmatch(r"[a-z0-9][a-z0-9-]*", module_id):
        raise Phase0Error("invalid module id: " + module_id)
    actual_root = _first_mapping_value(manifest, (("paths", "actualRoot"),))
    if actual_root is not None and actual_root != relative(root, module_root):
        raise Phase0Error(
            "manifest paths.actualRoot must match physical module directory: "
            + relative(root, module_root)
        )
    canonical_root = _first_mapping_value(manifest, (("paths", "canonicalRoot"),))
    expected_canonical = "modules/" + module_id
    if canonical_root is not None and canonical_root != expected_canonical:
        raise Phase0Error(
            "manifest paths.canonicalRoot must be " + expected_canonical
        )

    front_matter = parse_agents_front_matter(agents_path)
    agents_content = agents_path.read_text(encoding="utf-8-sig").casefold()
    required_instruction_topics = {
        "manifest": ("module.yaml", "manifest"),
        "contracts": ("contract",),
        "compatibility": ("compatib",),
        "tests": ("test",),
        "canary_or_rollout": ("canary", "rollout"),
        "rollback": ("rollback",),
        "communication": ("communication",),
    }
    missing_topics = sorted(
        topic
        for topic, alternatives in required_instruction_topics.items()
        if not any(alternative in agents_content for alternative in alternatives)
    )
    if missing_topics:
        raise Phase0Error(
            "{0} omits required upgrade topics: {1}".format(
                relative(root, agents_path), ", ".join(missing_topics)
            )
        )
    agents_schema_path = find_agents_schema(root)
    if agents_schema_path is not None:
        try:
            agents_schema = json.loads(
                agents_schema_path.read_text(encoding="utf-8-sig")
            )
        except Exception as exc:
            raise Phase0Error("invalid AGENTS front matter schema: {0}".format(exc))
        front_matter_errors = validate_json_schema(front_matter, agents_schema)
        if front_matter_errors:
            raise Phase0Error(
                "invalid AGENTS front matter {0}: {1}".format(
                    relative(root, agents_path), "; ".join(front_matter_errors)
                )
            )
    for key, expected in MODULE_AGENTS_FRONT_MATTER.items():
        if front_matter.get(key) != expected:
            raise Phase0Error(
                "{0} front matter {1} must be {2!r}".format(
                    relative(root, agents_path), key, expected
                )
            )
    if front_matter.get("module_id") != module_id:
        raise Phase0Error(
            "{0} front matter module_id mismatch".format(relative(root, agents_path))
        )
    policy_version = front_matter.get("policy_version")
    if not isinstance(policy_version, (str, int, float)) or str(policy_version).strip() == "":
        raise Phase0Error(
            "{0} front matter policy_version is required".format(
                relative(root, agents_path)
            )
        )

    patterns = manifest_ownership_patterns(manifest)
    implicit_root = relative(root, module_root) + "/**"
    if implicit_root not in patterns:
        patterns.append(implicit_root)
    dependencies = manifest_dependencies(manifest)

    provided_modes = manifest_contract_modes(manifest, "provides")
    consumed_modes = manifest_contract_modes(manifest, "consumes")
    return ModuleRecord(
        module_id=module_id,
        root=module_root,
        manifest_path=manifest_path,
        agents_path=agents_path,
        manifest=manifest,
        ownership_patterns=sorted(set(patterns)),
        dependencies=dependencies,
        provides={
            contract_id: {
                major
                for declared_contract, major in provided_modes
                if declared_contract == contract_id
            }
            for contract_id, _ in provided_modes
        },
        consumes={
            contract_id: {
                major
                for declared_contract, major in consumed_modes
                if declared_contract == contract_id
            }
            for contract_id, _ in consumed_modes
        },
        provided_modes=provided_modes,
        consumed_modes=consumed_modes,
    )


def _safe_declared_repo_path(root: Path, raw: str, label: str) -> Path:
    if not raw or "\\" in raw or "\x00" in raw:
        raise Phase0Error(label + " contains an invalid path: " + str(raw))
    value = Path(raw)
    if value.is_absolute() or ".." in value.parts:
        raise Phase0Error(label + " path escapes the repository: " + raw)
    cursor = root
    for part in value.parts:
        cursor = cursor / part
        if cursor.is_symlink():
            raise Phase0Error(label + " path traverses a symlink: " + raw)
    try:
        resolved = (root / value).resolve(strict=True)
        resolved.relative_to(root.resolve())
    except (OSError, RuntimeError, ValueError) as exc:
        raise Phase0Error(label + " path is missing or escapes the repository: {0}: {1}".format(raw, exc))
    return resolved


def _artifact_command_paths(build: str) -> List[str]:
    try:
        tokens = shlex.split(build, comments=False, posix=True)
    except ValueError as exc:
        raise Phase0Error("artifact build command cannot be parsed: " + str(exc))
    result: List[str] = []
    for token in tokens:
        normalized = token.replace("\\", "/")
        if normalized.startswith(("Modules/", "Core/", "ZDProjects/", "Extensions/", "scripts/")):
            result.append(normalized)
    return result


def _validate_runtime_and_artifact_paths(
    root: Path, records: Mapping[str, ModuleRecord]
) -> List[str]:
    errors: List[str] = []
    for module_id, record in sorted(records.items()):
        runtime = record.manifest.get("runtime")
        runtime = runtime if isinstance(runtime, Mapping) else {}
        entrypoints = runtime.get("entrypoints", [])
        if not isinstance(entrypoints, list):
            entrypoints = []
        for entrypoint in entrypoints:
            if not isinstance(entrypoint, str):
                continue
            try:
                path = _safe_declared_repo_path(
                    root, entrypoint, module_id + " runtime.entrypoints"
                )
                if not path.is_file():
                    errors.append(
                        module_id + " runtime entrypoint is not a file: " + entrypoint
                    )
                if not any(
                    path_matches_pattern(entrypoint, pattern)
                    for pattern in record.ownership_patterns
                ):
                    errors.append(
                        module_id + " runtime entrypoint is outside module ownership: " + entrypoint
                    )
            except Phase0Error as exc:
                errors.append(str(exc))

        # A modern module must contain an actual implementation/project, not
        # only a placeholder that makes an empty src directory appear present.
        # legacy-runtime-adapter is the explicit transition owner for loose
        # legacy sources and may keep only a declarative local src placeholder.
        if module_id != "legacy-runtime-adapter":
            substantive = [
                path
                for path in (record.root / "src").rglob("*")
                if path.is_file()
                and not path.is_symlink()
                and path.suffix.casefold() in {".cs", ".csproj", ".py"}
                and "bin" not in path.parts
                and "obj" not in path.parts
            ]
            if not substantive:
                errors.append(module_id + " src has no substantive implementation")

        artifacts = record.manifest.get("artifacts")
        if not isinstance(artifacts, list):
            continue
        artifact_ids: Set[str] = set()
        for index, artifact in enumerate(artifacts):
            if not isinstance(artifact, Mapping):
                continue
            artifact_id = artifact.get("id")
            if isinstance(artifact_id, str):
                if artifact_id in artifact_ids:
                    errors.append(module_id + " repeats artifact id " + artifact_id)
                artifact_ids.add(artifact_id)
            kind = artifact.get("kind")
            build = artifact.get("build")
            if kind in {"assembly", "service"} and not isinstance(build, str):
                errors.append(
                    "{0} artifact[{1}] {2} requires a build command".format(
                        module_id, index, artifact_id
                    )
                )
                continue
            if not isinstance(build, str):
                continue
            try:
                declared_paths = _artifact_command_paths(build)
            except Phase0Error as exc:
                errors.append("{0} artifact[{1}] {2}".format(module_id, index, exc))
                continue
            module_paths = [
                path
                for path in declared_paths
                if path.casefold().startswith(
                    ("Modules/" + module_id + "/").casefold()
                )
            ]
            if not module_paths:
                errors.append(
                    "{0} artifact[{1}] build names no module-owned source/contract/operation path".format(
                        module_id, index
                    )
                )
            for declared_path in declared_paths:
                try:
                    _safe_declared_repo_path(
                        root,
                        declared_path,
                        "{0} artifact[{1}]".format(module_id, index),
                    )
                except Phase0Error as exc:
                    errors.append(str(exc))
    return errors


def _validate_cross_module_project_references(
    root: Path, records: Mapping[str, ModuleRecord]
) -> List[str]:
    errors: List[str] = []
    known_modules = set(records)
    assembly_owners: Dict[str, Set[str]] = {}
    allowed_contract_projects: Set[Path] = set()

    def display_path(path: Path) -> str:
        try:
            return path.relative_to(root).as_posix()
        except ValueError:
            return str(path)

    def decode_csharp_escape(match: re.Match[str]) -> str:
        value = int(match.group(1) or match.group(2), 16)
        return chr(value) if value <= 0x10FFFF else "\N{REPLACEMENT CHARACTER}"
    for owner_id, owner_record in sorted(records.items()):
        for project in sorted(owner_record.root.rglob("*.csproj")):
            if (
                not project.is_file()
                or project.is_symlink()
                or any(part in {"bin", "obj"} for part in project.parts)
            ):
                continue
            try:
                document = ET.parse(str(project))
            except (ET.ParseError, OSError):
                continue
            assembly_name = project.stem
            for element in document.getroot().iter():
                if element.tag.rsplit("}", 1)[-1] != "AssemblyName":
                    continue
                if element.text and element.text.strip():
                    assembly_name = element.text.strip()
                    break
            assembly_owners.setdefault(assembly_name, set()).add(owner_id)
        for artifact in owner_record.manifest.get("artifacts", []):
            if not isinstance(artifact, dict) or artifact.get("kind") != "contract-pack":
                continue
            build = artifact.get("build")
            if not isinstance(build, str) or not build.strip():
                continue
            try:
                tokens = shlex.split(build, comments=False, posix=True)
            except ValueError:
                continue
            for token in tokens:
                if not token.startswith(
                    MODULES_DIRECTORY + "/" + owner_id + "/contracts"
                ):
                    continue
                candidate = root / token
                if candidate.is_file() and candidate.suffix.casefold() == ".csproj":
                    allowed_contract_projects.add(candidate.resolve())
                elif candidate.is_dir() and not candidate.is_symlink():
                    allowed_contract_projects.update(
                        project.resolve()
                        for project in candidate.rglob("*.csproj")
                        if project.is_file()
                        and not project.is_symlink()
                        and "consumed" not in project.relative_to(candidate).parts
                        and not any(part in {"bin", "obj"} for part in project.parts)
                    )

    def validate_friend(module_id: str, source: Path, raw_friend: str) -> None:
        if any(token in raw_friend for token in ("$(", "@(", "%(")):
            errors.append(
                "InternalsVisibleTo must be a literal same-module assembly: {0}".format(
                    relative(root, source)
                )
            )
            return
        friend = raw_friend.split(",", 1)[0].strip()
        owners = assembly_owners.get(friend, set())
        if owners != {module_id}:
            errors.append(
                "cross-module or unknown InternalsVisibleTo is forbidden: "
                "{0} -> {1}; expose a versioned public contract instead".format(
                    relative(root, source), friend or "<empty>"
                )
            )

    def resolve_msbuild_path(source: Path, raw: str, label: str) -> Optional[Path]:
        if (
            not raw
            or "\x00" in raw
            or any(token in raw for token in ("$(", "@(", "%("))
            or any(token in raw for token in ("*", "?", ";"))
        ):
            errors.append(
                "{0} must be one literal path: {1}".format(
                    label, relative(root, source)
                )
            )
            return None
        normalized = raw.replace("\\", "/")
        if Path(normalized).is_absolute() or re.match(r"^[A-Za-z]:/", normalized):
            errors.append(
                "{0} must be repository-relative: {1}".format(
                    label, relative(root, source)
                )
            )
            return None
        cursor = source.parent
        for part in Path(normalized).parts:
            cursor = cursor.parent if part == ".." else cursor / part
            if cursor.is_symlink():
                errors.append(
                    "{0} may not traverse a symlink: {1}".format(
                        label, relative(root, source)
                    )
                )
                return None
        try:
            return (source.parent / normalized).resolve(strict=True)
        except (OSError, RuntimeError) as exc:
            errors.append(
                "{0} path is missing: {1}: {2}".format(
                    label, relative(root, source), exc
                )
            )
            return None

    for module_id, record in sorted(records.items()):
        production_xml: List[Path] = []
        production_xml.extend(record.root.glob("*.csproj"))
        production_xml.extend(record.root.glob("*.props"))
        production_xml.extend(record.root.glob("*.targets"))
        for area in (record.root / "src", record.root / "contracts"):
            if not area.is_dir() or area.is_symlink():
                continue
            production_xml.extend(area.rglob("*.csproj"))
            production_xml.extend(area.rglob("*.props"))
            production_xml.extend(area.rglob("*.targets"))
        for project in sorted(set(production_xml)):
            if any(part in {"bin", "obj"} for part in project.parts):
                continue
            if not project.is_file() or project.is_symlink():
                errors.append(
                    module_id
                    + " has an unsafe production project path: "
                    + display_path(project)
                )
                continue
            try:
                document = ET.parse(str(project))
            except (ET.ParseError, OSError) as exc:
                errors.append(
                    "{0} production project cannot be parsed: {1}: {2}".format(
                        module_id, relative(root, project), exc
                    )
                )
                continue
            for element in document.getroot().iter():
                tag = element.tag.rsplit("}", 1)[-1]
                if tag == "ProjectReference":
                    include = element.get("Include")
                    if not include:
                        errors.append(
                            relative(root, project)
                            + " has ProjectReference without Include"
                        )
                        continue
                    target = resolve_msbuild_path(
                        project, include, "production ProjectReference"
                    )
                    if target is None:
                        continue
                    try:
                        target.relative_to(root.resolve())
                    except ValueError:
                        errors.append(
                            "production ProjectReference escapes the repository: "
                            + relative(root, project)
                        )
                        continue
                    try:
                        target.relative_to(record.root.resolve())
                        continue
                    except ValueError:
                        pass
                    target_relative = relative(root, target)
                    parts = Path(target_relative).parts
                    allowed_contract = (
                        len(parts) >= 4
                        and parts[0] == MODULES_DIRECTORY
                        and parts[1] in known_modules
                        and parts[1] != module_id
                        and target in allowed_contract_projects
                    )
                    if not allowed_contract:
                        errors.append(
                            "cross-module production ProjectReference is forbidden: "
                            "{0} -> {1}; reference only the provider contract pack".format(
                                relative(root, project), target_relative
                            )
                        )
                elif tag == "Compile" and element.get("Include"):
                    include = str(element.get("Include"))
                    target = resolve_msbuild_path(
                        project, include, "production Compile Include"
                    )
                    if target is None:
                        continue
                    try:
                        target.relative_to(record.root.resolve())
                    except ValueError:
                        errors.append(
                            "production Compile Include escapes its module: {0} -> {1}".format(
                                relative(root, project), include
                            )
                        )
                elif tag == "Import" and element.get("Project"):
                    include = str(element.get("Project"))
                    target = resolve_msbuild_path(
                        project, include, "production Import"
                    )
                    if target is None:
                        continue
                    try:
                        target.relative_to(record.root.resolve())
                    except ValueError:
                        errors.append(
                            "production Import escapes its module: {0} -> {1}".format(
                                relative(root, project), include
                            )
                        )
                elif tag == "InternalsVisibleTo":
                    if (
                        set(element.attrib) != {"Include"}
                        or len(element) != 0
                        or not element.get("Include")
                    ):
                        errors.append(
                            "InternalsVisibleTo must be one literal Include item: "
                            + relative(root, project)
                        )
                        continue
                    validate_friend(module_id, project, str(element.get("Include")))
                elif tag == "AssemblyAttribute":
                    include = str(element.get("Include", ""))
                    if any(token in include for token in ("$(", "@(", "%(")):
                        errors.append(
                            "AssemblyAttribute type must be a literal: "
                            + relative(root, project)
                        )
                    elif "InternalsVisibleTo" in include:
                        errors.append(
                            "InternalsVisibleTo AssemblyAttribute indirection is forbidden: "
                            + relative(root, project)
                        )
        for area in (record.root / "src", record.root / "contracts"):
            if not area.is_dir() or area.is_symlink():
                continue
            for candidate in sorted(area.rglob("*")):
                if any(part in {"bin", "obj"} for part in candidate.parts):
                    continue
                if candidate.is_symlink():
                    errors.append(
                        "production source trees may not contain symlinks: "
                        + display_path(candidate)
                    )
            for source in sorted(area.rglob("*.cs")):
                if any(part in {"bin", "obj"} for part in source.parts):
                    continue
                if not source.is_file() or source.is_symlink():
                    continue
                try:
                    text = source.read_text(encoding="utf-8-sig")
                except (OSError, UnicodeError) as exc:
                    errors.append(
                        "{0} cannot be read while validating friend assemblies: {1}".format(
                            relative(root, source), exc
                        )
                    )
                    continue
                normalized_text = re.sub(
                    r"\\(?:u([0-9A-Fa-f]{4})|U([0-9A-Fa-f]{8}))",
                    decode_csharp_escape,
                    text,
                )
                if "InternalsVisibleTo" in normalized_text:
                    errors.append(
                        "source InternalsVisibleTo is forbidden: {0}; use one exact "
                        "same-module csproj item".format(relative(root, source))
                    )
    return errors


def _find_cycle(graph: Mapping[str, Set[str]]) -> Optional[List[str]]:
    visiting: Set[str] = set()
    visited: Set[str] = set()
    stack: List[str] = []

    def visit(node: str) -> Optional[List[str]]:
        if node in visiting:
            index = stack.index(node)
            return stack[index:] + [node]
        if node in visited:
            return None
        visiting.add(node)
        stack.append(node)
        for dependency in sorted(graph.get(node, set())):
            cycle = visit(dependency)
            if cycle:
                return cycle
        stack.pop()
        visiting.remove(node)
        visited.add(node)
        return None

    for node in sorted(graph):
        cycle = visit(node)
        if cycle:
            return cycle
    return None


def _schema_allowed_producers(
    root: Path, item: Mapping[str, Any]
) -> Set[str]:
    source = item.get("source")
    if not isinstance(source, str) or not (root / source).is_file():
        return set()
    try:
        schema = json.loads((root / source).read_text(encoding="utf-8-sig"))
    except Exception:
        return set()
    if not isinstance(schema, Mapping):
        return set()
    properties = schema.get("properties")
    if not isinstance(properties, Mapping):
        return set()
    producer = properties.get("producer_module")
    if not isinstance(producer, Mapping):
        return set()
    producer_const = producer.get("const")
    if isinstance(producer_const, str) and producer_const:
        return {producer_const}
    producer_enum = producer.get("enum")
    if isinstance(producer_enum, list):
        return {
            value for value in producer_enum if isinstance(value, str) and value
        }
    return set()


def _validate_communications(
    root: Path, records: Mapping[str, ModuleRecord]
) -> List[str]:
    errors: List[str] = []
    aliases = {
        "peer_module": ("peer_module", "peerModule"),
        "contract_id": ("contract_id", "contractId"),
        "direction": ("direction",),
        "transport": ("transport",),
        "timeout": ("timeout", "timeoutMs"),
        "retry_policy": ("retry_policy", "retryPolicy"),
        "idempotency_key": ("idempotency_key", "idempotencyKey"),
        "auth_scope": ("auth_scope", "authScope"),
        "failure_mode": ("failure_mode", "failureMode"),
    }
    contract_producers: Dict[Tuple[str, int], Set[str]] = {}
    for owner_id, record in records.items():
        contract_root = _first_mapping_value(
            record.manifest, (("contracts",), ("spec", "contracts"))
        )
        provided_items = []
        if isinstance(contract_root, Mapping):
            provided_items = contract_root.get(
                "provided", contract_root.get("provides", [])
            )
        for item in provided_items if isinstance(provided_items, list) else []:
            if not isinstance(item, Mapping):
                continue
            contract_id = item.get(
                "contractId", item.get("contract_id", item.get("id"))
            )
            major = item.get("major")
            if not isinstance(contract_id, str) or not isinstance(major, int):
                continue
            allowed = _schema_allowed_producers(root, item)
            if allowed:
                contract_producers[(contract_id, major)] = allowed
                for producer in sorted(allowed):
                    external = producer in EXTERNAL_PEERS or producer.startswith(
                        "external:"
                    )
                    if producer not in records and not external:
                        errors.append(
                            "contract {0}/v{1} owned by {2} allows unknown producer {3}".format(
                                contract_id, major, owner_id, producer
                            )
                        )

    edge_index: Dict[
        Tuple[str, str, str, Optional[int], str], Mapping[str, Any]
    ] = {}
    for module_id, record in records.items():
        for edge in _communication_edges(record.manifest):
            peer = edge.get("peer_module", edge.get("peerModule"))
            contract_id = edge.get("contract_id", edge.get("contractId"))
            direction = edge.get("direction")
            major = edge.get("major")
            if (
                isinstance(peer, str)
                and isinstance(contract_id, str)
                and direction in ("inbound", "outbound")
            ):
                edge_key = (module_id, peer, contract_id, major, direction)
                if edge_key in edge_index:
                    errors.append(
                        "{0} has duplicate exact communication edge for {1}/v{2} {3} {4}".format(
                            module_id, contract_id, major, direction, peer
                        )
                    )
                else:
                    edge_index[edge_key] = edge

    for module_id, record in records.items():
        for index, edge in enumerate(_communication_edges(record.manifest)):
            missing = sorted(
                field
                for field in REQUIRED_COMMUNICATION_FIELDS
                if not any(alias in edge for alias in aliases[field])
            )
            if missing:
                errors.append(
                    "{0} communication[{1}] missing {2}".format(
                        module_id, index, ", ".join(missing)
                    )
                )
            peer = edge.get("peer_module", edge.get("peerModule"))
            if isinstance(peer, str):
                external = peer in EXTERNAL_PEERS or peer.startswith("external:")
                if peer not in records and not external:
                    errors.append(
                        "{0} communication[{1}] has unknown peer {2}".format(
                            module_id, index, peer
                        )
                    )
                auth_scope = edge.get("auth_scope", edge.get("authScope"))
                if (
                    isinstance(auth_scope, str)
                    and auth_scope.startswith("module:")
                    and auth_scope != "module:" + peer
                ):
                    errors.append(
                        "{0} communication[{1}] module auth scope must target peer {2}".format(
                            module_id, index, peer
                        )
                    )
            else:
                errors.append(
                    "{0} communication[{1}] peer_module must be a string".format(
                        module_id, index
                    )
                )
            contract_id = edge.get("contract_id", edge.get("contractId"))
            direction = edge.get("direction")
            major = edge.get("major")
            preserve_producer = edge.get("preserveProducer", False)
            if preserve_producer is not False and preserve_producer is not True:
                errors.append(
                    "{0} communication[{1}] preserveProducer must be boolean".format(
                        module_id, index
                    )
                )
            if preserve_producer is True and direction != "outbound":
                errors.append(
                    "{0} communication[{1}] preserveProducer is valid only on outbound relay edges".format(
                        module_id, index
                    )
                )
            declared: Dict[str, Set[int]] = {}
            for declared_id, declared_majors in record.provides.items():
                declared.setdefault(declared_id, set()).update(declared_majors)
            for declared_id, declared_majors in record.consumes.items():
                declared.setdefault(declared_id, set()).update(declared_majors)
            if direction not in ("inbound", "outbound"):
                errors.append(
                    "{0} communication[{1}] has invalid direction".format(
                        module_id, index
                    )
                )
            elif not isinstance(contract_id, str) or contract_id not in declared:
                errors.append(
                    "{0} communication[{1}] uses undeclared {2} contract {3}".format(
                        module_id, index, direction, contract_id
                    )
                )
            elif isinstance(major, int) and declared[contract_id] and major not in declared[contract_id]:
                errors.append(
                    "{0} communication[{1}] major is not declared for {2}".format(
                        module_id, index, contract_id
                    )
                )
            if (
                isinstance(peer, str)
                and peer in records
                and isinstance(contract_id, str)
                and direction in ("inbound", "outbound")
            ):
                reciprocal_direction = (
                    "inbound" if direction == "outbound" else "outbound"
                )
                reciprocal = (
                    peer,
                    module_id,
                    contract_id,
                    major,
                    reciprocal_direction,
                )
                reciprocal_edge = edge_index.get(reciprocal)
                if reciprocal_edge is None:
                    errors.append(
                        "{0} communication[{1}] lacks reciprocal {2} edge in {3} for {4}/v{5}".format(
                            module_id,
                            index,
                            reciprocal_direction,
                            peer,
                            contract_id,
                            major,
                        )
                    )
                else:
                    for semantic_field, field_aliases in (
                        ("transport", ("transport",)),
                        ("timeout", ("timeout", "timeoutMs")),
                    ):
                        local_value = next(
                            (edge[name] for name in field_aliases if name in edge),
                            None,
                        )
                        peer_value = next(
                            (
                                reciprocal_edge[name]
                                for name in field_aliases
                                if name in reciprocal_edge
                            ),
                            None,
                        )
                        if local_value != peer_value:
                            errors.append(
                                "{0} communication[{1}] {2} conflicts with reciprocal edge in {3}".format(
                                    module_id, index, semantic_field, peer
                                )
                            )

                allowed_producers = contract_producers.get((contract_id, major))
                if allowed_producers:
                    if (
                        direction == "outbound"
                        and preserve_producer is True
                        and module_id in allowed_producers
                    ):
                        errors.append(
                            "{0} communication[{1}] preserveProducer is relay-only; "
                            "the transport sender is already an exact schema producer".format(
                                module_id, index
                            )
                        )
                    if (
                        direction == "outbound"
                        and preserve_producer is True
                        and len(allowed_producers) != 1
                    ):
                        errors.append(
                            "{0} communication[{1}] preserveProducer cannot resolve "
                            "an exact producer from a multi-value schema enum".format(
                                module_id, index
                            )
                        )
                    expected_producer = module_id if direction == "outbound" else peer
                    relay_allowed = False
                    if direction == "outbound":
                        relay_allowed = (
                            preserve_producer is True
                            and contract_id in record.consumes
                        )
                    elif reciprocal_edge is not None:
                        peer_record = records[peer]
                        relay_allowed = (
                            reciprocal_edge.get("preserveProducer") is True
                            and contract_id in peer_record.consumes
                        )
                    if expected_producer not in allowed_producers and not relay_allowed:
                        errors.append(
                            "{0} communication[{1}] direction conflicts with {2}/v{3} producers {4}".format(
                                module_id,
                                index,
                                contract_id,
                                major,
                                ", ".join(sorted(allowed_producers)),
                            )
                        )
    return errors


def _runtime_scope_files(root: Path, files: Sequence[str]) -> Set[str]:
    scoped: Set[str] = set()
    for value in files:
        folded = value.casefold()
        if any(
            folded == runtime.casefold()
            or folded.startswith(runtime.casefold().rstrip("/") + "/")
            for runtime in KNOWN_RUNTIME_ROOTS
        ):
            scoped.add(value)
        if any(folded == runtime.casefold() for runtime in KNOWN_RUNTIME_FILES):
            scoped.add(value)
    return scoped


def _records_repository_root(records: Mapping[str, ModuleRecord]) -> Path:
    roots = {record.root.parent.parent.resolve() for record in records.values()}
    if len(roots) != 1:
        raise Phase0Error(
            "compatibility matrix requires records from exactly one repository root"
        )
    return next(iter(roots))


def _manifest_contract_item_map(
    record: ModuleRecord, direction: str
) -> Dict[Tuple[str, int], Mapping[str, Any]]:
    aliases = (
        ("provided", "provides")
        if direction == "provided"
        else ("consumed", "consumes")
    )
    root = _first_mapping_value(
        record.manifest, (("contracts",), ("spec", "contracts"))
    )
    result: Dict[Tuple[str, int], Mapping[str, Any]] = {}
    if not isinstance(root, Mapping):
        return result
    for alias in aliases:
        values = root.get(alias, [])
        if not isinstance(values, list):
            continue
        for item in values:
            if not isinstance(item, Mapping):
                continue
            contract_id = item.get(
                "contractId", item.get("contract_id", item.get("id"))
            )
            majors = _contract_major_versions(item)
            if isinstance(contract_id, str) and len(majors) == 1:
                result[(contract_id, next(iter(majors)))] = item
    return result


def _module_contract_mode(
    record: Optional[ModuleRecord],
    contract_major: Tuple[str, int],
    prefer_provided: bool = False,
) -> Tuple[Optional[str], Optional[str]]:
    if record is None:
        return None, "external"
    provided = record.provided_modes.get(contract_major)
    consumed = record.consumed_modes.get(contract_major)
    if provided is not None and consumed is not None and provided != consumed:
        raise Phase0Error(
            "module {0} has conflicting provided/consumed modes for {1}/v{2}".format(
                record.module_id, contract_major[0], contract_major[1]
            )
        )
    if prefer_provided and provided is not None:
        return provided, "provided"
    if consumed is not None:
        return consumed, "consumed"
    if provided is not None:
        return provided, "provided"
    return None, None


def _edge_value(edge: Mapping[str, Any], *names: str) -> Any:
    return next((edge[name] for name in names if name in edge), None)


def _canonical_communication_edge(edge: Mapping[str, Any]) -> Dict[str, Any]:
    return {
        "peerModule": _edge_value(edge, "peer_module", "peerModule"),
        "contractId": _edge_value(edge, "contract_id", "contractId"),
        "major": edge.get("major"),
        "direction": edge.get("direction"),
        "transport": edge.get("transport"),
        "timeout": _edge_value(edge, "timeout", "timeoutMs"),
        "retryPolicy": _edge_value(edge, "retry_policy", "retryPolicy"),
        "idempotencyKey": _edge_value(
            edge, "idempotency_key", "idempotencyKey"
        ),
        "authScope": _edge_value(edge, "auth_scope", "authScope"),
        "failureMode": _edge_value(edge, "failure_mode", "failureMode"),
        "preserveProducer": edge.get("preserveProducer", False),
    }


def _communication_pair_sha256(
    sender: str,
    receiver: str,
    contract_id: str,
    major: int,
    outbound: Mapping[str, Any],
    inbound: Mapping[str, Any],
) -> str:
    return sha256_text(
        stable_json(
            {
                "schemaVersion": "dps.communication-pair/v1",
                "contractId": contract_id,
                "major": major,
                "transportSenderModule": sender,
                "transportReceiverModule": receiver,
                "outbound": _canonical_communication_edge(outbound),
                "inbound": _canonical_communication_edge(inbound),
            }
        )
    )


def _communication_index(
    records: Mapping[str, ModuleRecord],
) -> Dict[Tuple[str, str, str, int, str], Mapping[str, Any]]:
    result: Dict[Tuple[str, str, str, int, str], Mapping[str, Any]] = {}
    for module_id, record in sorted(records.items()):
        for edge in _communication_edges(record.manifest):
            peer = _edge_value(edge, "peer_module", "peerModule")
            contract_id = _edge_value(edge, "contract_id", "contractId")
            major = edge.get("major")
            direction = edge.get("direction")
            if not (
                isinstance(peer, str)
                and isinstance(contract_id, str)
                and isinstance(major, int)
                and direction in {"inbound", "outbound"}
            ):
                continue
            key = (module_id, peer, contract_id, major, direction)
            if key in result:
                raise Phase0Error(
                    "duplicate exact communication edge: {0} {1} {2}/v{3} {4}".format(
                        module_id, peer, contract_id, major, direction
                    )
                )
            result[key] = edge
    return result


def _contract_runtime_inventory(
    root: Path, records: Mapping[str, ModuleRecord]
) -> Tuple[
    Dict[str, str],
    Dict[Tuple[str, int], str],
    Dict[Tuple[str, int], Set[str]],
]:
    family_owners: Dict[str, str] = {}
    major_owners: Dict[Tuple[str, int], str] = {}
    schema_producers: Dict[Tuple[str, int], Set[str]] = {}
    for module_id, record in sorted(records.items()):
        item_map = _manifest_contract_item_map(record, "provided")
        for contract_major in sorted(record.provided_modes):
            contract_id, major = contract_major
            previous_family = family_owners.get(contract_id)
            if previous_family is not None and previous_family != module_id:
                raise Phase0Error(
                    "compatibility matrix requires one contract-family owner: "
                    + contract_id
                )
            family_owners[contract_id] = module_id
            previous_major = major_owners.get(contract_major)
            if previous_major is not None and previous_major != module_id:
                raise Phase0Error(
                    "compatibility matrix requires one owner for {0}/v{1}".format(
                        contract_id, major
                    )
                )
            major_owners[contract_major] = module_id
            item = item_map.get(contract_major)
            allowed = _schema_allowed_producers(root, item or {})
            if not allowed:
                raise Phase0Error(
                    "compatibility matrix cannot resolve exact schema producer for {0}/v{1}".format(
                        contract_id, major
                    )
                )
            schema_producers[contract_major] = allowed
    return family_owners, major_owners, schema_producers


def _route_details(
    records: Mapping[str, ModuleRecord],
    owner_id: str,
    producer_id: str,
    sender_id: str,
    receiver_id: str,
    contract_major: Tuple[str, int],
    resolution: str,
    reciprocal_resolved: bool,
    pair_sha256: Optional[str],
    preserve_producer: bool,
) -> Dict[str, Any]:
    owner_mode, _ = _module_contract_mode(
        records.get(owner_id), contract_major, prefer_provided=True
    )
    producer_mode, producer_kind = _module_contract_mode(
        records.get(producer_id),
        contract_major,
        prefer_provided=producer_id == owner_id,
    )
    consumer_mode, consumer_kind = _module_contract_mode(
        records.get(receiver_id),
        contract_major,
        prefer_provided=receiver_id == owner_id,
    )
    if resolution == "schema-producer-preserved-by-relay":
        sender_record = records.get(sender_id)
        sender_mode = (
            sender_record.consumed_modes.get(contract_major)
            if sender_record is not None
            else None
        )
        sender_kind: Optional[str] = "consumed" if sender_mode is not None else None
    else:
        sender_mode, sender_kind = _module_contract_mode(
            records.get(sender_id),
            contract_major,
            prefer_provided=sender_id == owner_id,
        )
    return {
        "owner_module": owner_id,
        "owner_mode": owner_mode,
        "producer_module": producer_id,
        "producer_mode": producer_mode,
        "producer_declaration_kind": producer_kind,
        "transport_sender_module": sender_id,
        "transport_sender_mode": sender_mode,
        "transport_sender_declaration_kind": sender_kind,
        "transport_receiver_module": receiver_id,
        "consumer_module": receiver_id,
        "consumer_mode": consumer_mode,
        "consumer_declaration_kind": consumer_kind,
        "producer_resolution": resolution,
        "communication_pair_sha256": pair_sha256,
        "reciprocal_resolved": reciprocal_resolved,
        "transport_preserves_producer": preserve_producer,
    }


def _runtime_routes_for_major(
    root: Path,
    records: Mapping[str, ModuleRecord],
    owner_id: str,
    contract_id: str,
    major: int,
    schema_producers: Set[str],
    edge_index: Mapping[Tuple[str, str, str, int, str], Mapping[str, Any]],
) -> List[Dict[str, Any]]:
    contract_major = (contract_id, major)
    routes: Dict[Tuple[str, str, str], Dict[str, Any]] = {}
    resolved_producers: Set[str] = set()
    for key, outbound in sorted(edge_index.items()):
        sender, receiver, edge_contract, edge_major, direction = key
        if (
            direction != "outbound"
            or edge_contract != contract_id
            or edge_major != major
            or receiver not in records
        ):
            continue
        preserve = outbound.get("preserveProducer", False) is True
        reciprocal = edge_index.get(
            (receiver, sender, contract_id, major, "inbound")
        )
        semantics_match = reciprocal is not None and all(
            _edge_value(outbound, *aliases) == _edge_value(reciprocal, *aliases)
            for aliases in (
                ("transport",),
                ("timeout", "timeoutMs"),
            )
        )
        candidates: List[Tuple[str, str]] = []
        if sender in schema_producers and not preserve:
            candidates.append((sender, "schema-producer-is-transport-sender"))
        elif (
            preserve
            and contract_major in records[sender].consumed_modes
            and len(schema_producers) == 1
            and sender not in schema_producers
        ):
            candidates.append(
                (
                    next(iter(schema_producers)),
                    "schema-producer-preserved-by-relay",
                )
            )
        else:
            candidates.extend((producer, "unresolved") for producer in schema_producers)
        for producer, resolution in candidates:
            resolved = semantics_match and resolution != "unresolved"
            pair_sha256 = (
                _communication_pair_sha256(
                    sender,
                    receiver,
                    contract_id,
                    major,
                    outbound,
                    reciprocal,
                )
                if resolved and reciprocal is not None
                else None
            )
            route = _route_details(
                records,
                owner_id,
                producer,
                sender,
                receiver,
                contract_major,
                resolution if resolved else "unresolved",
                resolved,
                pair_sha256,
                preserve,
            )
            routes[(producer, sender, receiver)] = route
            if resolved:
                resolved_producers.add(producer)

    family_consumers = {
        module_id
        for module_id, record in records.items()
        if contract_id in record.consumes
    }
    for producer in sorted(schema_producers):
        producer_has_route = any(
            route_producer == producer
            for route_producer, _sender, _receiver in routes
        )
        # Runtime consumers come from reciprocal inbound edges.  A consumed
        # declaration alone cannot create extra runtime receivers: non-owner
        # schema producers often declare the contract as consumed because the
        # owner holds the provided Schema.  Only synthesize one fail-closed
        # expectation when the schema producer has no transport route at all.
        if producer in resolved_producers or producer_has_route:
            continue
        candidate_receivers = set(family_consumers).difference(schema_producers)
        if owner_id != producer:
            candidate_receivers.add(owner_id)
        candidate_receivers.discard(producer)
        for receiver in sorted(candidate_receivers):
            identity = (producer, producer, receiver)
            if identity not in routes:
                routes[identity] = _route_details(
                    records,
                    owner_id,
                    producer,
                    producer,
                    receiver,
                    contract_major,
                    "unresolved",
                    False,
                    None,
                    False,
                )
    return [routes[key] for key in sorted(routes)]


def _unresolved_route_for_identity(
    records: Mapping[str, ModuleRecord],
    owner_id: str,
    contract_id: str,
    major: int,
    producer_id: str,
    sender_id: str,
    receiver_id: str,
) -> Dict[str, Any]:
    return _route_details(
        records,
        owner_id,
        producer_id,
        sender_id,
        receiver_id,
        (contract_id, major),
        "unresolved",
        False,
        None,
        sender_id != producer_id,
    )


def build_compatibility_matrix(
    records: Mapping[str, ModuleRecord], root: Optional[Path] = None
) -> List[Dict[str, Any]]:
    """Resolve runtime roles from schema producers and reciprocal transports.

    Contract ownership remains an authorship concept.  A runtime row becomes
    runnable only when the exact schema producer, the exact receiver, both
    declaration modes, any relay mode, and a reciprocal communication pair all
    resolve as active.  Execution evidence remains NOT_RUN at Phase0.
    """

    if not records:
        return []
    repository_root = root.resolve() if root is not None else _records_repository_root(records)
    family_owners, _major_owners, schema_producer_map = _contract_runtime_inventory(
        repository_root, records
    )
    edge_index = _communication_index(records)
    matrix: List[Dict[str, Any]] = []
    for contract_id, owner_id in sorted(family_owners.items()):
        owner_record = records[owner_id]
        owner_modes = {
            major: mode
            for (declared_contract, major), mode in owner_record.provided_modes.items()
            if declared_contract == contract_id
        }
        active_majors = sorted(
            major for major, mode in owner_modes.items() if mode == RUNNABLE_CONTRACT_MODE
        )
        current = max(active_majors) if active_majors else None
        inventory_major = current if current is not None else max(owner_modes)
        previous = current - 1 if current is not None and current > 1 else None
        current_routes = _runtime_routes_for_major(
            repository_root,
            records,
            owner_id,
            contract_id,
            inventory_major,
            schema_producer_map[(contract_id, inventory_major)],
            edge_index,
        )
        previous_routes: Dict[Tuple[str, str, str], Dict[str, Any]] = {}
        if previous is not None and (contract_id, previous) in schema_producer_map:
            previous_routes = {
                (
                    route["producer_module"],
                    route["transport_sender_module"],
                    route["consumer_module"],
                ): route
                for route in _runtime_routes_for_major(
                    repository_root,
                    records,
                    owner_id,
                    contract_id,
                    previous,
                    schema_producer_map[(contract_id, previous)],
                    edge_index,
                )
            }

        def declaration(
            route: Dict[str, Any], major: int, required: bool
        ) -> Dict[str, Any]:
            modes = (
                route["owner_mode"],
                route["producer_mode"],
                route["transport_sender_mode"],
                route["consumer_mode"],
            )
            readable = (
                route["reciprocal_resolved"]
                and route["owner_mode"] == "active"
                and route["producer_mode"] == "active"
                and route["transport_sender_mode"] == "active"
                and route["consumer_mode"] == "compat-read"
            )
            runnable = route["reciprocal_resolved"] and all(
                mode == RUNNABLE_CONTRACT_MODE for mode in modes
            )
            if any(mode is None for mode in modes):
                execution_class = "missing-declaration"
            elif "retired" in modes:
                execution_class = "retired"
            elif "quarantine-only" in modes:
                execution_class = "quarantine-only"
            elif "compat-read" in modes:
                execution_class = "compat-read-only"
            elif not route["reciprocal_resolved"]:
                execution_class = "unresolved-communication"
            else:
                execution_class = "active-runtime"
            value = dict(route)
            value.update(
                {
                    "producer_major": major,
                    "readable": readable,
                    "runnable": runnable,
                    "execution_class": execution_class,
                    "result": (
                        "NOT_APPLICABLE"
                        if not required
                        else ("PASS" if runnable else "FAIL")
                    ),
                }
            )
            return value

        for current_route in current_routes:
            identity = (
                current_route["producer_module"],
                current_route["transport_sender_module"],
                current_route["consumer_module"],
            )
            current_required = current is not None
            current_declaration = declaration(
                current_route, inventory_major, current_required
            )
            if previous is None:
                previous_declaration = {
                    "producer_major": None,
                    "result": "NOT_APPLICABLE",
                    "runnable": False,
                    "readable": False,
                }
            else:
                previous_route = previous_routes.get(identity)
                if previous_route is None:
                    previous_route = _unresolved_route_for_identity(
                        records,
                        owner_id,
                        contract_id,
                        previous,
                        identity[0],
                        identity[1],
                        identity[2],
                    )
                previous_declaration = declaration(
                    previous_route, previous, current_required
                )
            independent_deployable = (
                current_required
                and current_declaration["result"] == "PASS"
                and previous_declaration["result"]
                in {"PASS", "NOT_APPLICABLE"}
            )
            compatibility_group_required = (
                current_required
                and current_declaration["result"] == "PASS"
                and previous_declaration["result"] == "FAIL"
            )
            consumer_record = records.get(current_route["consumer_module"])
            consumer_modes = {
                major: mode
                for (declared_contract, major), mode in (
                    consumer_record.consumed_modes.items()
                    if consumer_record is not None
                    else []
                )
                if declared_contract == contract_id
            }
            matrix.append(
                {
                    "contract_id": contract_id,
                    "owner_module": owner_id,
                    "provider_module": owner_id,
                    "producer_module": current_route["producer_module"],
                    "transport_sender_module": current_route[
                        "transport_sender_module"
                    ],
                    "transport_receiver_module": current_route[
                        "transport_receiver_module"
                    ],
                    "consumer_module": current_route["consumer_module"],
                    "producer_resolution": current_route["producer_resolution"],
                    "communication_pair_sha256": current_route[
                        "communication_pair_sha256"
                    ],
                    "reciprocal_resolved": current_route["reciprocal_resolved"],
                    "transport_preserves_producer": current_route[
                        "transport_preserves_producer"
                    ],
                    "current_active_major": current,
                    "previous_major": previous,
                    "producer_major_modes": [
                        {"major": major, "mode": owner_modes[major]}
                        for major in sorted(owner_modes)
                    ],
                    "consumer_major_modes": [
                        {"major": major, "mode": consumer_modes[major]}
                        for major in sorted(consumer_modes)
                    ],
                    "declaration_matrix": {
                        "current_producer_to_current_consumer": current_declaration,
                        "previous_producer_to_current_consumer": previous_declaration,
                    },
                    "declaration_deployable": independent_deployable,
                    "independent_deployable": independent_deployable,
                    "compatibility_group_required": compatibility_group_required,
                    "runnable_compatibility_required": current_required,
                    "candidate_green_eligible": independent_deployable,
                    "candidate_green": "NOT_RUN",
                    "execution_combinations": {
                        "N/N": "NOT_RUN",
                        "N/N-1": "NOT_RUN",
                        "N-1/N": "NOT_RUN",
                        "N-1/N-1": "NOT_RUN",
                    },
                    "unknown_N_plus_1": "REJECT",
                }
            )
    return sorted(
        matrix,
        key=lambda value: (
            value["contract_id"],
            value["producer_module"],
            value["transport_sender_module"],
            value["consumer_module"],
        ),
    )


def build_module_catalog_snapshot(
    records: Mapping[str, ModuleRecord],
) -> Dict[str, Any]:
    modules: List[Dict[str, Any]] = []
    for module_id, record in sorted(records.items()):
        metadata = _first_mapping_value(
            record.manifest, (("module",), ("metadata",))
        )
        paths = _first_mapping_value(record.manifest, (("paths",), ("ownership",)))
        metadata = metadata if isinstance(metadata, Mapping) else {}
        paths = paths if isinstance(paths, Mapping) else {}
        modules.append(
            {
                "id": module_id,
                "actualRoot": paths.get(
                    "actualRoot", "Modules/{0}".format(module_id)
                ),
                "canonicalRoot": paths.get(
                    "canonicalRoot", "modules/{0}".format(module_id)
                ),
                "manifest": "Modules/{0}/module.yaml".format(module_id),
                "agents": "Modules/{0}/AGENTS.md".format(module_id),
                "lifecycle": metadata.get("lifecycle", "proposed"),
                "runtimeState": metadata.get("runtimeState", "proposed"),
                "riskTier": metadata.get("riskTier", "R3"),
            }
        )
    return {
        "schemaVersion": "dps.module-catalog/v1",
        "physicalRoot": "Modules",
        "canonicalRoot": "modules",
        "caseNormalization": "deferred-until-legacy-retirement",
        "modules": modules,
    }


def _dependency_reason(record: ModuleRecord, dependency_id: str) -> str:
    dependencies = _first_mapping_value(
        record.manifest, (("dependencies",), ("spec", "dependencies"))
    )
    if isinstance(dependencies, list):
        for item in dependencies:
            if not isinstance(item, Mapping):
                continue
            item_id = item.get("moduleId", item.get("module_id", item.get("id")))
            if item_id == dependency_id:
                reason = item.get("reason")
                if isinstance(reason, str) and reason:
                    return reason
    return "declared module dependency"


def build_dependency_graph_snapshot(
    records: Mapping[str, ModuleRecord],
) -> Dict[str, Any]:
    graph = {
        module_id: set(record.dependencies)
        for module_id, record in sorted(records.items())
    }
    known_modules = set(graph)
    unknown_dependencies = {
        module_id: sorted(dependencies.difference(known_modules))
        for module_id, dependencies in graph.items()
        if dependencies.difference(known_modules)
    }
    if unknown_dependencies:
        rendered = "; ".join(
            "{0}: {1}".format(module_id, ", ".join(values))
            for module_id, values in sorted(unknown_dependencies.items())
        )
        raise Phase0Error(
            "cannot generate dependency waves with unknown dependencies: " + rendered
        )
    remaining = {module_id: set(values) for module_id, values in graph.items()}
    waves: List[List[str]] = []
    completed: Set[str] = set()
    while remaining:
        ready = sorted(
            module_id
            for module_id, dependencies in remaining.items()
            if dependencies.issubset(completed)
        )
        if not ready:
            raise Phase0Error("cannot generate dependency waves for a cyclic graph")
        waves.append(ready)
        completed.update(ready)
        for module_id in ready:
            del remaining[module_id]
    edges = [
        {
            "consumer": consumer_id,
            "provider": provider_id,
            "reason": _dependency_reason(records[consumer_id], provider_id),
        }
        for consumer_id, record in sorted(records.items())
        for provider_id in sorted(record.dependencies)
    ]
    return {
        "schemaVersion": "dps.dependency-graph/v1",
        "generatedFrom": "Modules/*/module.yaml",
        "failOnCycle": True,
        "nodes": sorted(records),
        "edges": edges,
        "parallelWaves": waves,
    }


def build_compatibility_snapshot(
    records: Mapping[str, ModuleRecord],
    root: Optional[Path] = None,
) -> Dict[str, Any]:
    if not records:
        raise Phase0Error(
            "compatibility snapshot requires at least one registered module"
        )
    repository_root = root.resolve() if root is not None else _records_repository_root(records)
    _policy, policy_sha256 = load_compatibility_policy(repository_root)
    _family_owners, _major_owners, schema_producer_map = (
        _contract_runtime_inventory(repository_root, records)
    )
    declarations: List[Dict[str, Any]] = []
    for module_id, record in sorted(records.items()):
        contract_root = _first_mapping_value(
            record.manifest, (("contracts",), ("spec", "contracts"))
        )
        if not isinstance(contract_root, Mapping):
            continue
        for declaration_kind, aliases, modes in (
            ("provided", ("provided", "provides"), record.provided_modes),
            ("consumed", ("consumed", "consumes"), record.consumed_modes),
        ):
            for alias in aliases:
                values = contract_root.get(alias, [])
                if not isinstance(values, list):
                    continue
                for item in values:
                    if not isinstance(item, Mapping):
                        continue
                    contract_id = item.get(
                        "contractId", item.get("contract_id", item.get("id"))
                    )
                    majors = _contract_major_versions(item)
                    if not isinstance(contract_id, str) or len(majors) != 1:
                        continue
                    major = next(iter(majors))
                    mode = modes[(contract_id, major)]
                    declarations.append(
                        {
                            "moduleId": module_id,
                            "declarationKind": declaration_kind,
                            "contractId": contract_id,
                            "major": major,
                            "source": item.get("source"),
                            "status": item.get("status"),
                            "mode": mode,
                            "ownerModule": item.get("ownerModule", item.get("owner_module")),
                            "schemaProducers": sorted(
                                schema_producer_map.get((contract_id, major), set())
                            ),
                            "candidateGreenEligible": mode == RUNNABLE_CONTRACT_MODE,
                        }
                    )

    declaration_matrix: List[Dict[str, Any]] = []
    for row in build_compatibility_matrix(records, repository_root):
        for execution_class, declaration_name in (
            ("current-producer-to-current-consumer", "current_producer_to_current_consumer"),
            ("previous-producer-to-current-consumer", "previous_producer_to_current_consumer"),
        ):
            declaration = row["declaration_matrix"][declaration_name]
            major = declaration["producer_major"]
            if major is None:
                continue
            declaration_matrix.append(
                {
                    "contractId": row["contract_id"],
                    "major": major,
                    "ownerModule": row["owner_module"],
                    "ownerMode": declaration["owner_mode"],
                    "producerModule": declaration["producer_module"],
                    "producerDeclarationKind": declaration[
                        "producer_declaration_kind"
                    ],
                    "producerMode": declaration["producer_mode"],
                    "transportSenderModule": declaration[
                        "transport_sender_module"
                    ],
                    "transportSenderMode": declaration[
                        "transport_sender_mode"
                    ],
                    "transportSenderDeclarationKind": declaration[
                        "transport_sender_declaration_kind"
                    ],
                    "transportReceiverModule": declaration[
                        "transport_receiver_module"
                    ],
                    "transportPreservesProducer": declaration[
                        "transport_preserves_producer"
                    ],
                    "consumerModule": declaration["consumer_module"],
                    "consumerDeclarationKind": declaration[
                        "consumer_declaration_kind"
                    ],
                    "consumerMode": declaration["consumer_mode"],
                    "producerResolution": declaration["producer_resolution"],
                    "communicationPairSha256": declaration[
                        "communication_pair_sha256"
                    ],
                    "reciprocalResolved": declaration["reciprocal_resolved"],
                    "direction": execution_class,
                    "executionClass": declaration["execution_class"],
                    "readCompatible": declaration["readable"],
                    "runnable": declaration["runnable"],
                    "deployable": declaration["runnable"],
                    "independentDeployable": row["independent_deployable"],
                    "compatibilityGroupRequired": row[
                        "compatibility_group_required"
                    ],
                    "activeProducerConsumer": declaration["runnable"],
                    "candidateGreenEligible": row[
                        "candidate_green_eligible"
                    ],
                }
            )

    execution_combinations = [
        {
            "combinationId": combination_id,
            "producerAxisValue": producer_axis,
            "consumerAxisValue": consumer_axis,
            "required": True,
            "evidenceStatus": "NOT_RUN",
            "evidenceClass": "candidate-artifact-required",
            "candidateGreenEligible": False,
        }
        for combination_id, producer_axis, consumer_axis in (
            ("N/N", "N", "N"),
            ("N/N-1", "N", "N-1"),
            ("N-1/N", "N-1", "N"),
            ("N-1/N-1", "N-1", "N-1"),
        )
    ]
    return {
        "schemaVersion": "dps.compatibility-matrix/v2",
        "generatedFrom": "Modules/*/module.yaml",
        "policyRef": COMPATIBILITY_POLICY_RELATIVE_PATH,
        "policySha256": policy_sha256,
        "unknownMajorBehavior": "reject",
        "missingMajorBehavior": "reject",
        "unknownModeBehavior": "reject",
        "missingModeBehavior": "reject",
        "majorDeclarations": sorted(
            declarations,
            key=lambda value: (
                value["contractId"],
                value["major"],
                value["declarationKind"],
                value["moduleId"],
            ),
        ),
        "declarationMatrix": sorted(
            declaration_matrix,
            key=lambda value: (
                value["contractId"],
                value["major"],
                value["consumerModule"],
                value["direction"],
            ),
        ),
        "axisMeaning": {
            "producerAxis": "producer-module-version-from-signed-release-bom",
            "consumerAxis": "consumer-module-version-from-signed-release-bom",
            "N": "candidate-module-version",
            "NMinus1": "previous-stable-module-version",
        },
        "executionCombinations": execution_combinations,
        "independentDeployable": bool(declaration_matrix)
        and all(value["independentDeployable"] for value in declaration_matrix),
        "compatibilityGroupRequired": any(
            value["compatibilityGroupRequired"] for value in declaration_matrix
        ),
        "candidateGreenEligible": bool(declaration_matrix)
        and all(value["candidateGreenEligible"] for value in declaration_matrix),
    }


def governance_snapshots(records: Mapping[str, ModuleRecord]) -> Dict[str, Dict[str, Any]]:
    return {
        "governance/modules/module-catalog.yaml": build_module_catalog_snapshot(records),
        "governance/modules/dependency-graph.yaml": build_dependency_graph_snapshot(records),
        "governance/modules/compatibility.yaml": build_compatibility_snapshot(records),
    }


def validate_governance_snapshots(
    root: Path, records: Mapping[str, ModuleRecord]
) -> Dict[str, Any]:
    expected = governance_snapshots(records)
    errors: List[str] = []
    hashes: Dict[str, str] = {}
    for relative_path, expected_value in sorted(expected.items()):
        path = root / relative_path
        if not path.is_file():
            errors.append("generated governance snapshot is missing: " + relative_path)
            continue
        expected_bytes = (
            json.dumps(expected_value, ensure_ascii=False, indent=2) + "\n"
        ).encode("utf-8")
        actual_bytes = path.read_bytes()
        if actual_bytes != expected_bytes:
            errors.append(
                "generated governance snapshot is stale or non-canonical: {0}; run "
                "python3 Tools/ci/generate_governance.py --write".format(relative_path)
            )
        hashes[relative_path] = sha256_file(path)
    if errors:
        raise Phase0Error("\n".join(errors))
    return {"files": sorted(expected), "sha256": hashes}


def validate_governance(root: Path, require_schema: bool = True) -> Dict[str, Any]:
    errors: List[str] = []
    policy_sha256: Optional[str] = None
    if not (root / "AGENTS.md").is_file():
        errors.append("root AGENTS.md is required")
    try:
        _policy, policy_sha256 = load_compatibility_policy(root)
    except Phase0Error as exc:
        errors.append(str(exc))

    schemas: Dict[str, Mapping[str, Any]] = {}
    try:
        schemas = dict(load_manifest_schemas(root))
    except Phase0Error as exc:
        errors.append(str(exc))
    if not schemas and require_schema:
        errors.append("module manifest JSON Schema is required")

    try:
        registered_dirs = discover_registered_module_dirs(root)
    except Phase0Error as exc:
        registered_dirs = []
        errors.append(str(exc))

    records: Dict[str, ModuleRecord] = {}
    for module_root in registered_dirs:
        try:
            record = _load_module_record(root, module_root, schemas or None)
            if record.module_id in records:
                errors.append("duplicate module id: " + record.module_id)
            else:
                records[record.module_id] = record
        except Phase0Error as exc:
            errors.append(str(exc))

    errors.extend(_validate_runtime_and_artifact_paths(root, records))
    errors.extend(_validate_cross_module_project_references(root, records))

    allowed_agents = {root / "AGENTS.md"}
    allowed_agents.update(module_root / "AGENTS.md" for module_root in registered_dirs)
    allowed_manifests: Set[Path] = {
        module_root / "module.yaml" for module_root in registered_dirs
    }
    unexpected_agents = sorted(
        path
        for path in root.rglob("AGENTS.md")
        if ".git" not in path.parts and path not in allowed_agents
    )
    if unexpected_agents:
        errors.append(
            "unexpected AGENTS.md outside root/module boundary: "
            + ", ".join(relative(root, path) for path in unexpected_agents)
        )
    unexpected_manifests = sorted(
        path
        for path in root.rglob("module.yaml")
        if ".git" not in path.parts and path not in allowed_manifests
    )
    if unexpected_manifests:
        errors.append(
            "module.yaml outside registered Modules/<module-id>: "
            + ", ".join(relative(root, path) for path in unexpected_manifests)
        )

    all_ids = set(records)
    for module_id, record in records.items():
        unknown = sorted(record.dependencies.difference(all_ids))
        if unknown:
            errors.append(
                "{0} has unknown dependencies: {1}".format(
                    module_id, ", ".join(unknown)
                )
            )
    graph = {module_id: set(record.dependencies) for module_id, record in records.items()}
    cycle = _find_cycle(graph)
    if cycle:
        errors.append("dependency cycle: " + " -> ".join(cycle))

    contract_owners: Dict[str, str] = {}
    contract_major_owners: Dict[Tuple[str, int], str] = {}
    for module_id, record in records.items():
        contract_root = _first_mapping_value(
            record.manifest, (("contracts",), ("spec", "contracts"))
        )
        provided_items = []
        consumed_items = []
        if isinstance(contract_root, Mapping):
            provided_items = contract_root.get(
                "provided", contract_root.get("provides", [])
            )
            consumed_items = contract_root.get(
                "consumed", contract_root.get("consumes", [])
            )
        for item in provided_items if isinstance(provided_items, list) else []:
            if not isinstance(item, Mapping):
                continue
            contract_id = item.get("contractId", item.get("contract_id", item.get("id")))
            declared_owner = item.get("ownerModule", item.get("owner_module"))
            if declared_owner is not None and declared_owner != module_id:
                errors.append(
                    "{0} declares another owner for provided contract {1}".format(
                        module_id, contract_id
                    )
                )
            source = item.get("source")
            if isinstance(source, str) and not (root / source).is_file():
                errors.append(
                    "{0} provided contract source is missing: {1}".format(
                        module_id, source
                    )
                )
            errors.extend(_validate_provided_contract_schema(root, module_id, item))
        for contract_id in record.provides:
            previous = contract_owners.get(contract_id)
            if previous is not None and previous != module_id:
                errors.append(
                    "contract {0} has multiple owners: {1}, {2}".format(
                        contract_id, previous, module_id
                    )
                )
            else:
                contract_owners[contract_id] = module_id
        for contract_major in record.provided_modes:
            previous_major_owner = contract_major_owners.get(contract_major)
            if previous_major_owner is not None and previous_major_owner != module_id:
                errors.append(
                    "contract {0}/v{1} has multiple owners: {2}, {3}".format(
                        contract_major[0],
                        contract_major[1],
                        previous_major_owner,
                        module_id,
                    )
                )
            else:
                contract_major_owners[contract_major] = module_id

    active_contract_majors: Dict[str, List[int]] = {}
    for contract_id, owner_id in sorted(contract_owners.items()):
        active_majors = sorted(
            major
            for (declared_contract, major), mode in records[owner_id].provided_modes.items()
            if declared_contract == contract_id and mode == RUNNABLE_CONTRACT_MODE
        )
        active_contract_majors[contract_id] = active_majors
        if active_majors:
            current_major = max(active_majors)
            allowed_window = {current_major}
            if current_major > 1:
                allowed_window.add(current_major - 1)
            outside_window = sorted(set(active_majors).difference(allowed_window))
            if outside_window:
                errors.append(
                    "contract {0} active producer majors exceed explicit N/N-1 window: {1}".format(
                        contract_id,
                        ", ".join(str(value) for value in active_majors),
                    )
                )
    for module_id, record in records.items():
        contract_root = _first_mapping_value(
            record.manifest, (("contracts",), ("spec", "contracts"))
        )
        consumed_items = []
        if isinstance(contract_root, Mapping):
            consumed_items = contract_root.get(
                "consumed", contract_root.get("consumes", [])
            )
        for item in consumed_items if isinstance(consumed_items, list) else []:
            if not isinstance(item, Mapping):
                continue
            contract_id = item.get("contractId", item.get("contract_id", item.get("id")))
            declared_owner = item.get("ownerModule", item.get("owner_module"))
            major = item.get("major")
            actual_owner = (
                contract_major_owners.get((contract_id, major))
                if isinstance(contract_id, str) and isinstance(major, int)
                else None
            )
            if actual_owner is not None and declared_owner is not None and declared_owner != actual_owner:
                errors.append(
                    "{0} declares wrong owner {1} for consumed contract {2}; expected {3}".format(
                        module_id, declared_owner, contract_id, actual_owner
                    )
                )
            source = item.get("source")
            if isinstance(source, str) and not (root / source).is_file():
                errors.append(
                    "{0} consumed contract source is missing: {1}".format(
                        module_id, source
                    )
                )
        for contract_id, major in record.consumed_modes:
            if (
                (contract_id, major) not in contract_major_owners
                and not contract_id.startswith("external.")
            ):
                errors.append(
                    "{0} consumes contract major without owner: {1}/v{2}".format(
                        module_id, contract_id, major
                    )
                )

        compatibility = _first_mapping_value(
            record.manifest,
            (("compatibility",), ("spec", "compatibility")),
        )
        if isinstance(compatibility, Mapping):
            rejection_fields = {
                "unknownMajorBehavior": "unknown contract majors",
                "missingMajorBehavior": "missing contract majors",
                "unknownModeBehavior": "unknown compatibility modes",
                "missingModeBehavior": "missing compatibility modes",
            }
            for field_name, label in rejection_fields.items():
                if compatibility.get(field_name) != "reject":
                    errors.append(module_id + " must reject " + label)
            supported = compatibility.get(
                "supportedContractMajors",
                compatibility.get("supported_contract_majors", {}),
            )
            if isinstance(supported, Mapping):
                for contract_id, majors in {
                    **record.provides,
                    **record.consumes,
                }.items():
                    declared_majors = supported.get(contract_id, [])
                    if not isinstance(declared_majors, list) or not majors.issubset(
                        set(value for value in declared_majors if isinstance(value, int))
                    ):
                        errors.append(
                            "{0} compatibility omits declared majors for {1}".format(
                                module_id, contract_id
                            )
                        )

    errors.extend(_validate_communications(root, records))

    files: List[str] = []
    try:
        files = repository_files(root)
    except Phase0Error as exc:
        errors.append(str(exc))
    file_set = set(files)
    for module_id, record in records.items():
        module_prefix = relative(root, record.root) + "/"
        for required_directory in REQUIRED_MODULE_DIRECTORIES:
            prefix = module_prefix + required_directory.rstrip("/") + "/"
            if not any(value.startswith(prefix) for value in files):
                errors.append(
                    "{0} standard directory is not reproducible from repository files: {1}".format(
                        module_id, required_directory + "/"
                    )
                )
        for required_file in REQUIRED_MODULE_FILES:
            expected = module_prefix + required_file
            if expected not in file_set:
                errors.append(
                    "{0} standard file is not reproducible from repository files: {1}".format(
                        module_id, required_file
                    )
                )
    ownership: Dict[str, List[str]] = {}
    record_values = list(sorted(records.items()))
    for left_index, (left_id, left_record) in enumerate(record_values):
        for right_id, right_record in record_values[left_index + 1 :]:
            for left_pattern in left_record.ownership_patterns:
                for right_pattern in right_record.ownership_patterns:
                    if ownership_patterns_obviously_overlap(left_pattern, right_pattern):
                        errors.append(
                            "ownership patterns overlap: {0}:{1} <=> {2}:{3}".format(
                                left_id, left_pattern, right_id, right_pattern
                            )
                        )
    for value in files:
        owners = sorted(
            module_id
            for module_id, record in records.items()
            if any(path_matches_pattern(value, pattern) for pattern in record.ownership_patterns)
        )
        if owners:
            ownership[value] = owners
        if len(owners) > 1:
            errors.append(
                "path has multiple module owners: {0} => {1}".format(
                    value, ", ".join(owners)
                )
            )
    runtime_scope = _runtime_scope_files(root, files)
    for value in sorted(runtime_scope):
        if value not in ownership:
            errors.append("runtime path has no module owner: " + value)

    try:
        matrix = build_compatibility_matrix(records, root)
    except Phase0Error as exc:
        matrix = []
        errors.append(str(exc))
    for row in matrix:
        if not row["runnable_compatibility_required"]:
            continue
        current_result = row["declaration_matrix"][
            "current_producer_to_current_consumer"
        ]["result"]
        previous_result = row["declaration_matrix"][
            "previous_producer_to_current_consumer"
        ]["result"]
        if current_result == "FAIL":
            errors.append(
                "runtime contract path is not active and reciprocal: owner={0}, "
                "producer={1}, sender={2}, receiver={3}, contract={4}".format(
                    row["owner_module"],
                    row["producer_module"],
                    row["transport_sender_module"],
                    row["consumer_module"],
                    row["contract_id"],
                )
            )
        if previous_result == "FAIL" and not row["compatibility_group_required"]:
            errors.append(
                "previous runtime contract path is not runnable: owner={0}, "
                "producer={1}, sender={2}, receiver={3}, contract={4}".format(
                    row["owner_module"],
                    row["producer_module"],
                    row["transport_sender_module"],
                    row["consumer_module"],
                    row["contract_id"],
                )
            )

    if errors:
        raise Phase0Error("\n".join(sorted(set(errors))))

    snapshot_details: Optional[Dict[str, Any]] = None
    if require_schema:
        snapshot_details = validate_governance_snapshots(root, records)

    return {
        "modules_directory": MODULES_DIRECTORY,
        "module_count": len(records),
        "modules": sorted(records),
        "manifest_schemas": sorted(relative(root, path) for path in _manifest_schema_paths(root)),
        "dependency_dag": {
            module_id: sorted(record.dependencies)
            for module_id, record in sorted(records.items())
        },
        "contract_owners": dict(sorted(contract_owners.items())),
        "contract_major_owners": {
            "{0}/v{1}".format(contract_id, major): owner
            for (contract_id, major), owner in sorted(contract_major_owners.items())
        },
        "active_contract_majors": dict(sorted(active_contract_majors.items())),
        "compatibility_policy_sha256": policy_sha256,
        "compatibility_matrix": matrix,
        "owned_runtime_paths": len(runtime_scope),
        "governance_snapshots": snapshot_details,
    }


def load_module_records_without_schema(root: Path) -> Dict[str, ModuleRecord]:
    records: Dict[str, ModuleRecord] = {}
    for module_root in discover_registered_module_dirs(root):
        record = _load_module_record(root, module_root, None)
        records[record.module_id] = record
    return records


def _changed_paths(root: Path, baseline_commit: str) -> List[str]:
    tracked = git_output(
        root,
        ["diff", "--name-only", "--diff-filter=ACDMRTUXB", baseline_commit, "--"],
    ).splitlines()
    untracked = git_output(
        root,
        ["ls-files", "--others", "--exclude-standard"],
    ).splitlines()
    return sorted(set(value.replace("\\", "/") for value in tracked + untracked if value))


def _git_blob_at(root: Path, commit: str, path: str) -> Optional[str]:
    output = git_output(root, ["rev-parse", "{0}:{1}".format(commit, path)], allow_failure=True)
    return output if re.fullmatch(r"[0-9a-f]{40,64}", output) else None


def _git_blob_current(root: Path, path: str) -> Optional[str]:
    file_path = root / path
    if not file_path.is_file():
        return None
    output = git_output(root, ["hash-object", "--", path], allow_failure=True)
    return output if re.fullmatch(r"[0-9a-f]{40,64}", output) else None


def _contract_consumers(records: Mapping[str, ModuleRecord]) -> Dict[str, Set[str]]:
    result: Dict[str, Set[str]] = {}
    for module_id, record in records.items():
        for contract_id in record.consumes:
            result.setdefault(contract_id, set()).add(module_id)
    return result


def _manifest_contract_items(manifest: Mapping[str, Any]) -> List[Mapping[str, Any]]:
    root = _first_mapping_value(manifest, (("contracts",), ("spec", "contracts")))
    if not isinstance(root, Mapping):
        return []
    result: List[Mapping[str, Any]] = []
    for key in ("provided", "provides", "consumed", "consumes"):
        values = root.get(key, [])
        if isinstance(values, list):
            result.extend(value for value in values if isinstance(value, Mapping))
    return result


def _identity_schema_is_constrained(
    field_schema: Any, expected_pattern: str
) -> bool:
    if not isinstance(field_schema, Mapping):
        return False
    if "const" in field_schema and field_schema.get("const") is None:
        return True
    for union_key in ("oneOf", "anyOf"):
        branches = field_schema.get(union_key)
        if isinstance(branches, list) and branches:
            return all(
                _identity_schema_is_constrained(branch, expected_pattern)
                for branch in branches
            )
    declared_type = field_schema.get("type")
    types = set(declared_type) if isinstance(declared_type, list) else {declared_type}
    if not types or not types.issubset({"string", "null"}):
        return False
    if "string" not in types:
        return types == {"null"}
    return field_schema.get("pattern") == expected_pattern


def _validate_provided_contract_schema(
    root: Path,
    module_id: str,
    item: Mapping[str, Any],
) -> List[str]:
    contract_id = item.get("contractId", item.get("contract_id", item.get("id")))
    major = item.get("major")
    source = item.get("source")
    if not isinstance(source, str) or not (root / source).is_file():
        return []
    try:
        schema = json.loads((root / source).read_text(encoding="utf-8-sig"))
    except Exception as exc:
        return ["invalid contract JSON Schema {0}: {1}".format(source, exc)]
    if not isinstance(schema, Mapping):
        return ["contract schema must be an object: " + source]
    properties = schema.get("properties")
    required = schema.get("required")
    errors: List[str] = []
    if not isinstance(properties, Mapping) or not isinstance(required, list):
        errors.append("contract schema lacks properties/required arrays: " + source)
        return errors
    missing_properties = sorted(COMMON_CONTRACT_FIELDS.difference(properties))
    missing_required = sorted(COMMON_CONTRACT_FIELDS.difference(required))
    if missing_properties:
        errors.append(
            "contract schema {0} lacks common properties: {1}".format(
                source, ", ".join(missing_properties)
            )
        )
    if missing_required:
        errors.append(
            "contract schema {0} does not require common fields: {1}".format(
                source, ", ".join(missing_required)
            )
        )
    if schema.get("additionalProperties") is not False:
        errors.append("contract schema must fail closed on unknown fields: " + source)
    for field_name, expected_pattern in IDENTITY_FIELD_PATTERNS.items():
        if not _identity_schema_is_constrained(
            properties.get(field_name), expected_pattern
        ):
            errors.append(
                "contract schema {0} must constrain {1} to {2} or null".format(
                    source, field_name, expected_pattern
                )
            )
    expected_contract = (
        "{0}/v{1}".format(contract_id, major)
        if isinstance(contract_id, str) and isinstance(major, int)
        else None
    )
    contract_property = properties.get("contract_id")
    producer_property = properties.get("producer_module")
    if not isinstance(contract_property, Mapping) or contract_property.get("const") != expected_contract:
        errors.append(
            "contract schema {0} must bind contract_id to {1}".format(
                source, expected_contract
            )
        )
    allowed_producers: List[str] = []
    if isinstance(producer_property, Mapping):
        producer_const = producer_property.get("const")
        producer_enum = producer_property.get("enum")
        if isinstance(producer_const, str) and producer_const:
            allowed_producers = [producer_const]
        elif (
            isinstance(producer_enum, list)
            and producer_enum
            and all(isinstance(value, str) and value for value in producer_enum)
        ):
            allowed_producers = list(producer_enum)
    if not allowed_producers:
        errors.append(
            "contract schema {0} must fail closed with producer_module const or enum".format(
                source
            )
        )
    return errors


def _bound_file(root: Path, baseline_commit: str, path: str, order: int) -> Dict[str, Any]:
    file_path = root / path
    if not file_path.is_file():
        raise Phase0Error("bound file is missing: " + path)
    baseline_blob = _git_blob_at(root, baseline_commit, path)
    current_blob = _git_blob_current(root, path)
    if baseline_blob is None:
        source_state = "untracked"
    elif baseline_blob == current_blob:
        source_state = "tracked"
    else:
        source_state = "modified"
    return {
        "path": path,
        "order": order,
        "source_state": source_state,
        "git_blob": current_blob,
        "sha256": sha256_file(file_path),
    }


def _instruction_schema(root: Path) -> Optional[Mapping[str, Any]]:
    candidates = (
        root / "governance" / "schemas" / "phase0-instruction-receipt.schema.json",
        root / "Governance" / "schemas" / "phase0-instruction-receipt.schema.json",
    )
    for path in candidates:
        if path.is_file():
            try:
                value = json.loads(path.read_text(encoding="utf-8-sig"))
            except Exception as exc:
                raise Phase0Error("invalid instruction receipt schema: {0}".format(exc))
            if not isinstance(value, Mapping):
                raise Phase0Error("instruction receipt schema must be an object")
            return value
    return None


def resolve_instruction_receipt(
    root: Path,
    baseline_commit: str,
    agent_identity: str = "phase0-gate",
    agent_role: str = "instruction-resolver",
    resolved_at: Optional[str] = None,
    required_scope: Optional[Iterable[str]] = None,
) -> Dict[str, Any]:
    git_output(root, ["rev-parse", "--verify", baseline_commit + "^{commit}"])
    records = load_module_records_without_schema(root)
    changed_paths = _changed_paths(root, baseline_commit)
    impacted: Set[str] = set()

    governance_change = any(
        value == "AGENTS.md"
        or value.startswith(("governance/", "Governance/", ".github/", "Tools/ci/"))
        for value in changed_paths
    )
    if governance_change:
        impacted.update(records)

    if required_scope is not None:
        requested = set(required_scope)
        unknown = sorted(requested.difference(records))
        if unknown:
            raise Phase0Error(
                "instruction receipt requested unknown modules: "
                + ", ".join(unknown)
            )
        impacted.update(requested)

    changed_contracts: Set[str] = set()
    for value in changed_paths:
        for module_id, record in records.items():
            if any(path_matches_pattern(value, pattern) for pattern in record.ownership_patterns):
                impacted.add(module_id)
                module_relative = value.casefold()
                if (
                    "/contracts/provided/" in module_relative
                    or module_relative == relative(root, record.manifest_path).casefold()
                ):
                    changed_contracts.update(record.provides)
    consumers = _contract_consumers(records)
    for contract_id in changed_contracts:
        impacted.update(consumers.get(contract_id, set()))

    # A repository gate with no changed module still binds a complete, useful
    # instruction scope.  Empty receipts are never evidence.
    if not impacted:
        impacted.update(records)

    instruction_paths = ["AGENTS.md"] + [
        relative(root, records[module_id].agents_path) for module_id in sorted(impacted)
    ]
    instructions: List[Dict[str, Any]] = []
    for order, path in enumerate(instruction_paths):
        instructions.append(_bound_file(root, baseline_commit, path, order))

    manifests = [
        _bound_file(
            root,
            baseline_commit,
            relative(root, records[module_id].manifest_path),
            order,
        )
        for order, module_id in enumerate(sorted(impacted))
    ]
    contract_paths: Set[str] = set()
    for module_id in sorted(impacted):
        for item in _manifest_contract_items(records[module_id].manifest):
            source = item.get("source")
            if isinstance(source, str):
                contract_paths.add(normalize_repo_pattern(source))
    contracts = [
        _bound_file(root, baseline_commit, path, order)
        for order, path in enumerate(sorted(contract_paths))
    ]

    diff_fingerprint: List[Dict[str, Any]] = []
    for value in changed_paths:
        path = root / value
        diff_fingerprint.append(
            {
                "path": value,
                "sha256": sha256_file(path) if path.is_file() else None,
            }
        )
    if resolved_at is None:
        resolved_at = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    receipt_without_id = {
        "schema_version": "dps.phase0-instruction-receipt/v1",
        "agent_identity": agent_identity,
        "agent_role": agent_role,
        "baseline_commit": git_output(root, ["rev-parse", baseline_commit]),
        "resolved_at": resolved_at,
        "scope": sorted(impacted),
        "instructions": instructions,
        "manifests": manifests,
        "contracts": contracts,
        "diff_fingerprint": sha256_text(stable_json(diff_fingerprint)),
        "status": "BOUND",
        "invalidated_reason": None,
    }
    receipt = dict(receipt_without_id)
    receipt["receipt_id"] = "instruction:" + sha256_text(
        stable_json(receipt_without_id)
    )[:32]
    # Preserve schema field order only for readability; canonical hashes sort.
    receipt = {
        "schema_version": receipt["schema_version"],
        "receipt_id": receipt["receipt_id"],
        "agent_identity": receipt["agent_identity"],
        "agent_role": receipt["agent_role"],
        "baseline_commit": receipt["baseline_commit"],
        "resolved_at": receipt["resolved_at"],
        "scope": receipt["scope"],
        "instructions": receipt["instructions"],
        "manifests": receipt["manifests"],
        "contracts": receipt["contracts"],
        "diff_fingerprint": receipt["diff_fingerprint"],
        "status": receipt["status"],
        "invalidated_reason": receipt["invalidated_reason"],
    }
    schema = _instruction_schema(root)
    if schema is not None:
        schema_errors = validate_json_schema(receipt, schema)
        if schema_errors:
            raise Phase0Error(
                "instruction receipt violates schema: " + "; ".join(schema_errors)
            )
    return receipt


def validate_instruction_receipt(
    root: Path,
    receipt: Mapping[str, Any],
    required_scope: Optional[Iterable[str]] = None,
) -> Tuple[bool, str, Dict[str, Any]]:
    baseline = receipt.get("baseline_commit")
    if not isinstance(baseline, str):
        return False, "receipt baseline_commit is missing", {}
    try:
        current = resolve_instruction_receipt(
            root,
            baseline,
            agent_identity=str(receipt.get("agent_identity", "phase0-gate")),
            agent_role=str(receipt.get("agent_role", "instruction-resolver")),
            resolved_at=str(receipt.get("resolved_at", "")),
            required_scope=required_scope,
        )
    except Phase0Error as exc:
        return False, str(exc), {}
    comparable_keys = (
        "schema_version",
        "receipt_id",
        "agent_identity",
        "agent_role",
        "baseline_commit",
        "resolved_at",
        "scope",
        "instructions",
        "manifests",
        "contracts",
        "diff_fingerprint",
        "status",
        "invalidated_reason",
    )
    mismatches = [key for key in comparable_keys if receipt.get(key) != current.get(key)]
    if mismatches:
        return (
            False,
            "instruction receipt is stale: " + ", ".join(mismatches),
            current,
        )
    return True, "instruction receipt is current", current


def _shell_executable_surface(text: str) -> str:
    """Return shell source lines while excluding literal heredoc/help bodies."""

    result: List[str] = []
    heredoc_end: Optional[str] = None
    for line in text.splitlines():
        if heredoc_end is not None:
            if line.strip() == heredoc_end:
                heredoc_end = None
            continue
        result.append(line)
        match = re.search(r"<<-?\s*['\"]?([A-Za-z_][A-Za-z0-9_]*)['\"]?", line)
        if match:
            heredoc_end = match.group(1)
    return "\n".join(result)


def _shell_logical_lines(text: str) -> List[str]:
    surface = _shell_executable_surface(text)
    result: List[str] = []
    buffer = ""
    for source_line in surface.splitlines():
        stripped = source_line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        continued = source_line.rstrip().endswith("\\")
        piece = source_line.rstrip()
        if continued:
            piece = piece[:-1]
        buffer = (buffer + " " + piece.strip()).strip()
        if not continued:
            if buffer:
                result.append(buffer)
            buffer = ""
    if buffer:
        result.append(buffer)
    return result


def _shell_tokens(line: str) -> List[str]:
    try:
        return shlex.split(line, comments=True, posix=True)
    except ValueError:
        return []


def _command_tokens_without_environment(tokens: Sequence[str]) -> List[str]:
    values = list(tokens)
    while values and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", values[0]):
        values.pop(0)
    if values[:1] == ["env"]:
        values.pop(0)
        while values and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", values[0]):
            values.pop(0)
    if values[:1] == ["command"]:
        values.pop(0)
    return values


def _is_python_command(token: str) -> bool:
    if Path(token).name in {"python", "python3", "python3.12"}:
        return True
    normalized = token.strip('"\'')
    return normalized.startswith("$") and "python" in normalized.casefold()


def _python_script_invocations(shell_text: str, script_path: str) -> List[List[str]]:
    invocations: List[List[str]] = []
    for line in _shell_logical_lines(shell_text):
        tokens = _command_tokens_without_environment(_shell_tokens(line))
        if len(tokens) >= 2 and _is_python_command(tokens[0]) and tokens[1] == script_path:
            invocations.append(tokens)
    return invocations


def _load_workflow_steps(
    workflow_text: str,
) -> Tuple[List[Mapping[str, Any]], List[str]]:
    try:
        import yaml  # type: ignore
    except Exception as exc:
        raise Phase0Error("pinned PyYAML is required to validate workflow structure: {0}".format(exc))

    class UniqueBaseLoader(yaml.BaseLoader):
        pass

    def construct_unique_mapping(loader: Any, node: Any, deep: bool = False) -> Dict[Any, Any]:
        mapping: Dict[Any, Any] = {}
        for key_node, value_node in node.value:
            key = loader.construct_object(key_node, deep=deep)
            if key in mapping:
                raise Phase0Error("workflow YAML contains duplicate key: " + str(key))
            mapping[key] = loader.construct_object(value_node, deep=deep)
        return mapping

    UniqueBaseLoader.add_constructor(
        yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, construct_unique_mapping
    )
    try:
        document = yaml.load(workflow_text, Loader=UniqueBaseLoader)
    except Phase0Error:
        raise
    except Exception as exc:
        raise Phase0Error("Static CI workflow YAML is invalid: {0}".format(exc))
    if not isinstance(document, Mapping):
        raise Phase0Error("Static CI workflow must be a YAML mapping")
    jobs = document.get("jobs")
    if not isinstance(jobs, Mapping) or not jobs:
        raise Phase0Error("Static CI workflow must declare jobs")
    steps: List[Mapping[str, Any]] = []
    runners: List[str] = []
    for job_id, job in jobs.items():
        if not isinstance(job, Mapping):
            raise Phase0Error("workflow job must be a mapping: " + str(job_id))
        runner = job.get("runs-on")
        if not isinstance(runner, str) or not runner:
            raise Phase0Error("workflow job must pin runs-on: " + str(job_id))
        runners.append(runner)
        job_steps = job.get("steps")
        if not isinstance(job_steps, list) or not job_steps:
            raise Phase0Error("workflow job has no steps: " + str(job_id))
        for step in job_steps:
            if not isinstance(step, Mapping):
                raise Phase0Error("workflow step must be a mapping")
            steps.append(step)
    return steps, runners


def _runner_reachable_calls(runner_text: str) -> Tuple[Set[str], List[str]]:
    errors: List[str] = []
    try:
        tree = ast.parse(runner_text, filename="Tools/ci/run_phase0_gate.py")
    except SyntaxError as exc:
        return set(), ["Phase 0 runner Python AST is invalid: {0}".format(exc)]
    definitions: Dict[str, ast.FunctionDef | ast.AsyncFunctionDef] = {}
    duplicate_definitions: Set[str] = set()
    for node in tree.body:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            if node.name in definitions:
                duplicate_definitions.add(node.name)
            definitions[node.name] = node
    if duplicate_definitions:
        errors.append(
            "Phase 0 runner repeats function definitions: "
            + ", ".join(sorted(duplicate_definitions))
        )
    if "main" not in definitions:
        return set(), errors + ["Phase 0 runner must define main"]

    def call_name(call: ast.Call) -> Optional[str]:
        if isinstance(call.func, ast.Name):
            return call.func.id
        if isinstance(call.func, ast.Attribute):
            return call.func.attr
        return None

    def literal_truth(node: ast.AST) -> Optional[bool]:
        try:
            value = ast.literal_eval(node)
        except (ValueError, TypeError, SyntaxError):
            return None
        if isinstance(value, (bool, int, float, str, bytes, tuple, list, dict, set)):
            return bool(value)
        if value is None:
            return False
        return None

    class ReachableCollector(ast.NodeVisitor):
        def __init__(self) -> None:
            self.calls: List[ast.Call] = []
            self.returns: List[ast.Return] = []
            self.assignments: List[ast.Assign | ast.AnnAssign | ast.NamedExpr] = []

        def visit_Call(self, node: ast.Call) -> None:
            self.calls.append(node)
            name = call_name(node)
            self.visit(node.func)
            for argument in [*node.args, *[keyword.value for keyword in node.keywords]]:
                if isinstance(argument, ast.Lambda) and name == "in_process_check":
                    self.visit(argument.body)
                else:
                    self.visit(argument)

        def visit_Lambda(self, node: ast.Lambda) -> None:
            return

        def visit_FunctionDef(self, node: ast.FunctionDef) -> None:
            return

        def visit_AsyncFunctionDef(self, node: ast.AsyncFunctionDef) -> None:
            return

        def visit_ClassDef(self, node: ast.ClassDef) -> None:
            return

        def visit_Assign(self, node: ast.Assign) -> None:
            self.assignments.append(node)
            self.generic_visit(node)

        def visit_AnnAssign(self, node: ast.AnnAssign) -> None:
            self.assignments.append(node)
            self.generic_visit(node)

        def visit_NamedExpr(self, node: ast.NamedExpr) -> None:
            self.assignments.append(node)
            self.generic_visit(node)

        def visit_statements(self, statements: Sequence[ast.stmt]) -> bool:
            can_continue = True
            for statement in statements:
                if not can_continue:
                    break
                can_continue = self.visit_statement(statement)
            return can_continue

        def visit_statement(self, statement: ast.stmt) -> bool:
            if isinstance(statement, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                return True
            if isinstance(statement, ast.Return):
                self.returns.append(statement)
                if statement.value is not None:
                    self.visit(statement.value)
                return False
            if isinstance(statement, ast.Raise):
                if statement.exc is not None:
                    self.visit(statement.exc)
                if statement.cause is not None:
                    self.visit(statement.cause)
                return False
            if isinstance(statement, ast.If):
                self.visit(statement.test)
                truth = literal_truth(statement.test)
                if truth is True:
                    return self.visit_statements(statement.body)
                if truth is False:
                    return self.visit_statements(statement.orelse)
                body_continues = self.visit_statements(statement.body)
                else_continues = (
                    self.visit_statements(statement.orelse)
                    if statement.orelse
                    else True
                )
                return body_continues or else_continues
            if isinstance(statement, (ast.For, ast.AsyncFor)):
                self.visit(statement.target)
                self.visit(statement.iter)
                self.visit_statements(statement.body)
                self.visit_statements(statement.orelse)
                return True
            if isinstance(statement, ast.While):
                self.visit(statement.test)
                if literal_truth(statement.test) is False:
                    return self.visit_statements(statement.orelse)
                self.visit_statements(statement.body)
                self.visit_statements(statement.orelse)
                return True
            if isinstance(statement, (ast.With, ast.AsyncWith)):
                for item in statement.items:
                    self.visit(item.context_expr)
                    if item.optional_vars is not None:
                        self.visit(item.optional_vars)
                return self.visit_statements(statement.body)
            if isinstance(statement, (ast.Try, ast.TryStar)):
                body_continues = self.visit_statements(statement.body)
                handler_continuations = []
                for handler in statement.handlers:
                    if handler.type is not None:
                        self.visit(handler.type)
                    handler_continuations.append(
                        self.visit_statements(handler.body)
                    )
                if body_continues:
                    body_continues = self.visit_statements(statement.orelse)
                finally_continues = self.visit_statements(statement.finalbody)
                return finally_continues and (
                    body_continues
                    or any(handler_continuations)
                    or not statement.handlers
                )
            if isinstance(statement, ast.Match):
                self.visit(statement.subject)
                continuations = []
                for case in statement.cases:
                    if case.guard is not None:
                        self.visit(case.guard)
                    continuations.append(self.visit_statements(case.body))
                return any(continuations) if continuations else True
            self.visit(statement)
            return True

    def collect_function(function: ast.FunctionDef | ast.AsyncFunctionDef) -> ReachableCollector:
        collector = ReachableCollector()
        collector.visit_statements(function.body)
        return collector

    reachable: Set[str] = set()
    visited: Set[str] = set()
    queue = ["main"]
    main_collector: Optional[ReachableCollector] = None
    while queue:
        function_name = queue.pop()
        if function_name in visited:
            continue
        visited.add(function_name)
        function = definitions.get(function_name)
        if function is None:
            continue
        collector = collect_function(function)
        if function_name == "main":
            main_collector = collector
        for node in collector.calls:
            name = call_name(node)
            if name is None:
                continue
            reachable.add(name)
            if name in definitions and name not in visited:
                queue.append(name)

    def assigns_overall_from_evaluation(
        assignment: ast.Assign | ast.AnnAssign | ast.NamedExpr,
    ) -> bool:
        value = assignment.value
        if not isinstance(value, ast.Call) or call_name(value) != "evaluate_checks":
            return False
        targets: List[ast.AST]
        if isinstance(assignment, ast.Assign):
            targets = list(assignment.targets)
        else:
            targets = [assignment.target]
        return any(
            isinstance(child, ast.Name) and child.id == "overall_status"
            for target in targets
            for child in ast.walk(target)
        )

    status_from_evaluation = bool(main_collector) and any(
        assigns_overall_from_evaluation(assignment)
        for assignment in main_collector.assignments
    )

    def is_guarded_return(node: ast.Return) -> bool:
        value = node.value
        if not isinstance(value, ast.IfExp):
            return False
        test = value.test
        if not (
            isinstance(test, ast.Compare)
            and len(test.ops) == 1
            and isinstance(test.ops[0], (ast.Eq, ast.NotEq))
            and len(test.comparators) == 1
        ):
            return False
        operands = (test.left, test.comparators[0])
        has_status = any(
            isinstance(operand, ast.Name) and operand.id == "overall_status"
            for operand in operands
        )
        has_pass = any(
            isinstance(operand, ast.Constant) and operand.value == "PASS"
            for operand in operands
        )
        if not (has_status and has_pass):
            return False
        if isinstance(test.ops[0], ast.Eq):
            return (
                isinstance(value.body, ast.Constant)
                and value.body.value == 0
                and isinstance(value.orelse, ast.Constant)
                and value.orelse.value == 1
            )
        return (
            isinstance(value.body, ast.Constant)
            and value.body.value == 1
            and isinstance(value.orelse, ast.Constant)
            and value.orelse.value == 0
        )

    guarded_return = bool(main_collector) and any(
        is_guarded_return(node) for node in main_collector.returns
    )
    if not status_from_evaluation:
        errors.append("Phase 0 main must assign overall_status from evaluate_checks")
    if not guarded_return:
        errors.append("Phase 0 main must return nonzero unless overall_status is PASS")

    module_collector = ReachableCollector()
    module_collector.visit_statements(tree.body)
    has_main_exit = any(
        isinstance(node.func, ast.Attribute)
        and isinstance(node.func.value, ast.Name)
        and node.func.value.id == "sys"
        and node.func.attr == "exit"
        and len(node.args) == 1
        and isinstance(node.args[0], ast.Call)
        and isinstance(node.args[0].func, ast.Name)
        and node.args[0].func.id == "main"
        for node in module_collector.calls
    )
    if not has_main_exit:
        errors.append("Phase 0 runner must execute sys.exit(main())")
    return reachable, errors


RELEASE_BOM_COMMIT_READER = """import json
import re
import sys
from pathlib import Path

try:
    value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
except Exception as exc:
    raise SystemExit("Cannot read candidate Release BOM: {0}".format(exc))
commit = value.get("integration_commit") if isinstance(value, dict) else None
if not isinstance(commit, str) or re.fullmatch(r"[0-9a-f]{40}", commit) is None:
    raise SystemExit("Candidate Release BOM has no valid integration_commit")
print(commit)
"""


def _heredoc_bodies(shell_text: str, marker: str) -> List[str]:
    lines = shell_text.splitlines()
    bodies: List[str] = []
    index = 0
    start_pattern = re.compile(
        r"<<-?\s*['\"]?" + re.escape(marker) + r"['\"]?\s*$"
    )
    while index < len(lines):
        if start_pattern.search(lines[index]) is None:
            index += 1
            continue
        index += 1
        body: List[str] = []
        while index < len(lines) and lines[index].strip() != marker:
            body.append(lines[index])
            index += 1
        if index >= len(lines):
            bodies.append("\n".join(body) + "\n<UNTERMINATED>\n")
            break
        bodies.append("\n".join(body) + "\n")
        index += 1
    return bodies


def _release_validation_allowlist_errors(release_text: str) -> List[str]:
    errors: List[str] = []
    lines = _shell_logical_lines(release_text)

    phase0_control_lines = {
        'phase0_evidence=""',
        'phase0_evidence="${2:-}"',
        'phase0_arguments=(--base "$head_commit")',
        'if [[ -n "$phase0_evidence" ]]; then',
        'phase0_arguments+=(--evidence "$phase0_evidence")',
        '"$python_executable" Tools/ci/run_phase0_gate.py "${phase0_arguments[@]}"',
    }
    for required_line in phase0_control_lines:
        if lines.count(required_line) != 1:
            errors.append(
                "release validation must preserve the exact safe Phase 0 "
                "default/override control: " + required_line
            )
    unexpected_phase0_assignments = [
        line
        for line in lines
        if (
            line.startswith("phase0_evidence=")
            or line.startswith("phase0_arguments=")
            or line.startswith("phase0_arguments+=")
        )
        and line not in phase0_control_lines
    ]
    if unexpected_phase0_assignments:
        errors.append(
            "release validation may not construct an arbitrary Phase 0 evidence path"
        )

    python_bodies = _heredoc_bodies(release_text, "PY")
    if python_bodies != [RELEASE_BOM_COMMIT_READER]:
        errors.append(
            "release validation must use the fixed read-only Release BOM commit reader"
        )

    exact_lines = {
        "set -euo pipefail",
        "usage() {",
        "cat <<'EOF'",
        "}",
        "while (($#)); do",
        'case "$1" in',
        ";;",
        "*)",
        "esac",
        "done",
        "fi",
        "else",
        'repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"',
        'cd "$repo_root"',
        "if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then",
        'if [[ -n "$(git status --porcelain=v1 --untracked-files=all)" ]]; then',
        "elif command -v python3.12 >/dev/null 2>&1; then",
        'python_executable="$(command -v python3.12)"',
        "actual_python=\"$($python_executable -c 'import platform; print(platform.python_version())')\"",
        'head_commit="$(git rev-parse HEAD^{commit})"',
        'bom_commit="$($python_executable - "$bom_path" <<\'PY\'',
        ')"',
        'if [[ "$bom_commit" != "$head_commit" ]]; then',
        'phase0_arguments=(--base "$head_commit")',
        'if [[ -n "$phase0_evidence" ]]; then',
        'phase0_arguments+=(--evidence "$phase0_evidence")',
    }

    help_exit_indices = {
        index + 2
        for index in range(len(lines) - 3)
        if lines[index : index + 4] == ["--help|-h)", "usage", "exit 0", ";;"]
    }

    for index, line in enumerate(lines):
        if line in exact_lines:
            continue
        if re.fullmatch(r"--[a-z0-9-]+\)", line) or line == "--help|-h)":
            continue
        if re.fullmatch(r"(?:[A-Za-z_][A-Za-z0-9_]*)=", line):
            continue
        if re.fullmatch(
            r"[A-Za-z_][A-Za-z0-9_]*=(?:\"[^\x60]*\"|'[^\x60]*'|[^\s\x60;&|<>]+)",
            line,
        ):
            if "$(" not in line:
                continue
        if re.fullmatch(
            r"for\s+[A-Za-z_][A-Za-z0-9_]*\s+in\s+"
            r"\"\$[A-Za-z_][A-Za-z0-9_]*\""
            r"(?:\s+\"\$[A-Za-z_][A-Za-z0-9_]*\")*;\s*do",
            line,
        ):
            continue
        if re.fullmatch(r"(?:if|elif)\s+\[\[.*\]\];\s*then", line):
            if "$(" not in line and "\x60" not in line:
                continue
        if re.fullmatch(r"shift\s+2", line):
            continue
        if re.fullmatch(r"usage(?:\s+>&2)?", line):
            continue
        if re.fullmatch(r"exit\s+(?:1|2|64|127)", line):
            continue
        if line == "exit 0" and index in help_exit_indices:
            continue
        if line.startswith("echo "):
            body = line[5:]
            if body.endswith(" >&2"):
                body = body[:-4]
            if not any(
                token in body
                for token in ("$(", "\x60", ";", "&&", "||", "<", ">")
            ):
                continue

        phase0 = _python_script_invocations(line, "Tools/ci/run_phase0_gate.py")
        if phase0:
            expected = [
                phase0[0][0],
                "Tools/ci/run_phase0_gate.py",
                "${phase0_arguments[@]}",
            ]
            if len(phase0) == 1 and phase0[0] == expected:
                continue

        candidate = _python_script_invocations(
            line,
            "Modules/factory-release-controller/src/candidate_bom_validator.py",
        )
        if candidate:
            raw_tokens = _shell_tokens(line)
            expected_environment = (
                "PYTHONPATH=$repo_root/Modules/factory-release-controller/src"
            )
            expected = [
                candidate[0][0],
                "Modules/factory-release-controller/src/candidate_bom_validator.py",
                "--repo-root",
                "$repo_root",
                "--bundle-root",
                "$bundle_root",
                "--bom",
                "$bom_path",
                "--previous-bom",
                "$previous_bom_path",
                "--schema-sha256",
                "$schema_sha256",
            ]
            if (
                len(candidate) == 1
                and raw_tokens[:1] == [expected_environment]
                and candidate[0] == expected
            ):
                continue

        errors.append(
            "release validation command is outside the validation-only allowlist: "
            + line
        )
    return errors


def validate_ci_integrity(root: Path) -> Dict[str, Any]:
    workflow = root / ".github" / "workflows" / "static-ci.yml"
    runner = root / "Tools" / "ci" / "run_phase0_gate.py"
    release_script = root / "scripts" / "release.sh"
    if not workflow.is_file():
        raise Phase0Error(".github/workflows/static-ci.yml is required")
    if not runner.is_file():
        raise Phase0Error("Tools/ci/run_phase0_gate.py is required")
    if not release_script.is_file() or release_script.is_symlink():
        raise Phase0Error("scripts/release.sh validation entry point is required")
    workflow_text = workflow.read_text(encoding="utf-8-sig")
    runner_text = runner.read_text(encoding="utf-8-sig")
    release_text = release_script.read_text(encoding="utf-8-sig")
    errors: List[str] = []
    try:
        steps, runners = _load_workflow_steps(workflow_text)
    except Phase0Error as exc:
        steps = []
        runners = []
        errors.append(str(exc))

    if not runners or any(runner != "ubuntu-24.04" for runner in runners):
        errors.append("Static CI jobs must pin runs-on to ubuntu-24.04")

    action_counts = {action: 0 for action in PINNED_GITHUB_ACTIONS}
    for step in steps:
        uses = step.get("uses")
        if not isinstance(uses, str):
            continue
        if "@" not in uses:
            errors.append("CI action reference must contain an immutable commit SHA")
            continue
        action, revision = uses.rsplit("@", 1)
        expected = PINNED_GITHUB_ACTIONS.get(action)
        if expected is None:
            errors.append("CI uses an action outside the pinned allowlist: " + action)
            continue
        action_counts[action] += 1
        if revision != expected[0]:
            errors.append(
                "CI action {0} must pin official {1} commit {2}".format(
                    action, expected[1], expected[0]
                )
            )
        annotation_pattern = (
            r"(?m)^\s*(?:-\s*)?uses:\s*"
            + re.escape(action + "@" + expected[0])
            + r"\s+#\s*"
            + re.escape(expected[1])
            + r"\s*$"
        )
        if re.search(annotation_pattern, workflow_text) is None:
            errors.append(
                "CI action {0} pin must retain its {1} version annotation".format(
                    action, expected[1]
                )
            )
    for action, count in action_counts.items():
        if count != 1:
            errors.append(
                "Static CI must use pinned action {0} exactly once".format(action)
            )

    run_commands = [
        str(step["run"])
        for step in steps
        if isinstance(step.get("run"), str)
    ]
    for step in steps:
        condition = str(step.get("if", "")).strip().casefold().replace(" ", "")
        if condition in {"false", "0", "${{false}}", "${{0}}"}:
            errors.append("CI cannot hide a required operation behind an always-false step")
        if str(step.get("continue-on-error", "false")).casefold() == "true":
            errors.append("CI cannot ignore a failed required step")
    for command in run_commands:
        executable = "\n".join(_shell_logical_lines(command))
        if re.search(r"(?:^|[;&|]\s*)set\s+\+e\b", executable):
            errors.append("CI cannot disable fail-fast handling")
        if re.search(r"(?:^|[;&|]\s*)exit\s+0\b", executable):
            errors.append("CI cannot force a successful exit")
        if re.search(r"\|\|\s*true\b|;\s*true\b", executable):
            errors.append("CI cannot convert command failure to success")

    gate_invocations = [
        invocation
        for command in run_commands
        for invocation in _python_script_invocations(
            command, "Tools/ci/run_phase0_gate.py"
        )
    ]
    if len(gate_invocations) != 1:
        errors.append("Static CI must actually invoke the unique Phase 0 gate exactly once")
    elif (
        "--evidence" not in gate_invocations[0]
        or "Reports/ci/phase0-evidence/phase0-evidence.json"
        not in gate_invocations[0]
    ):
        errors.append("Static CI gate invocation must write the canonical Phase 0 evidence path")
    elif "--diagnostic-workspace" in gate_invocations[0]:
        errors.append("Static CI may not use non-releasable diagnostic workspace mode")
    direct_validator_invocations = [
        invocation
        for command in run_commands
        for invocation in _python_script_invocations(
            command, "Tools/ci/validate_repo.py"
        )
    ]
    if direct_validator_invocations:
        errors.append("Static CI must not bypass the unique Phase 0 gate")

    pip_install_count = 0
    for command in run_commands:
        for line in _shell_logical_lines(command):
            tokens = _command_tokens_without_environment(_shell_tokens(line))
            if len(tokens) < 4 or not _is_python_command(tokens[0]):
                continue
            if tokens[1:4] != ["-m", "pip", "install"]:
                continue
            pip_install_count += 1
            has_requirement = (
                "--requirement=requirements-ci.txt" in tokens
                or "-rrequirements-ci.txt" in tokens
                or any(
                    tokens[index] in ("--requirement", "-r")
                    and index + 1 < len(tokens)
                    and tokens[index + 1] == "requirements-ci.txt"
                    for index in range(len(tokens))
                )
            )
            if "--require-hashes" not in tokens or not has_requirement:
                errors.append(
                    "Static CI pip install must use --require-hashes with requirements-ci.txt"
                )
    if pip_install_count != 1:
        errors.append("Static CI must contain exactly one structured pinned pip install step")

    def pinned_setup(action_prefix: str, key: str, expected: str) -> bool:
        for step in steps:
            uses = step.get("uses")
            values = step.get("with")
            if (
                isinstance(uses, str)
                and uses.startswith(action_prefix + "@")
                and isinstance(values, Mapping)
                and str(values.get(key)) == expected
            ):
                return True
        return False

    if not pinned_setup("actions/setup-python", "python-version", "3.12.13"):
        errors.append("Static CI must structurally pin Python 3.12.13")
    if not pinned_setup("actions/setup-node", "node-version", "24.18.0"):
        errors.append("Static CI must structurally pin Node 24.18.0")
    if not pinned_setup("actions/setup-dotnet", "dotnet-version", "10.0.301"):
        errors.append("Static CI must structurally pin .NET SDK 10.0.301")
    if not any(
        isinstance(step.get("uses"), str)
        and str(step.get("uses")).startswith("actions/checkout@")
        and isinstance(step.get("with"), Mapping)
        and str(step.get("with", {}).get("fetch-depth")) == "0"
        for step in steps
    ):
        errors.append("Static CI must fetch baseline commits for instruction receipts")
    canonical_evidence_directory = "Reports/ci/phase0-evidence/"
    evidence_uploads = [
        (
            {
                line.strip()
                for line in str(step.get("with", {}).get("path", "")).splitlines()
                if line.strip()
            },
            str(step.get("with", {}).get("if-no-files-found", "")).strip(),
        )
        for step in steps
        if isinstance(step.get("uses"), str)
        and str(step.get("uses")).startswith("actions/upload-artifact@")
        and isinstance(step.get("with"), Mapping)
    ]
    if not any(
        canonical_evidence_directory in paths
        and not any(path.startswith("!") for path in paths)
        and missing_policy == "error"
        for paths, missing_policy in evidence_uploads
    ):
        errors.append(
            "Static CI must upload the complete canonical Phase 0 evidence "
            "directory without exclusions and fail on missing files so payload, "
            "marker, and any quarantine claim travel together"
        )

    required_runner_calls = {
        "run_phase0_unittests",
        "run_external_gate_unittests",
        "run_locked_solution_build",
        "run_required_module_static_tests",
        "validate_governance",
        "validate_ci_integrity",
        "load_or_issue_receipt",
        "resolve_instruction_receipt",
        "evaluate_checks",
        "build_test_evidence_records",
        "write_evidence",
    }
    reachable_calls, runner_errors = _runner_reachable_calls(runner_text)
    errors.extend(runner_errors)
    missing_calls = sorted(required_runner_calls.difference(reachable_calls))
    if missing_calls:
        errors.append(
            "Phase 0 runner main cannot reach required operations: "
            + ", ".join(missing_calls)
        )
    duplicate_runners = sorted(root.rglob("run_phase0_gate.py"))
    if duplicate_runners != [runner]:
        errors.append("there must be exactly one run_phase0_gate.py")
    errors.extend(_release_validation_allowlist_errors(release_text))
    release_lines = _shell_logical_lines(release_text)
    release_executable_text = "\n".join(release_lines)
    release_phase0 = _python_script_invocations(
        release_text, "Tools/ci/run_phase0_gate.py"
    )
    if len(release_phase0) != 1:
        errors.append("release validation must actually invoke the unique Phase 0 gate exactly once")
    candidate_validator = _python_script_invocations(
        release_text,
        "Modules/factory-release-controller/src/candidate_bom_validator.py",
    )
    if len(candidate_validator) != 1:
        errors.append("release validation must actually invoke candidate_bom_validator.py exactly once")
    if not any(_shell_tokens(line) == ["set", "-euo", "pipefail"] for line in release_lines):
        errors.append("release validation must use fail-fast shell settings")
    executable_git_mutation_patterns = (
        r"(?m)(?:^|[;&|]\s*)(?:(?:if|then|while|until|!|time|command|sudo|env)\s+)*(?:[A-Za-z_][A-Za-z0-9_]*=\S+\s+)*git\s+(?:-[^\s]+\s+)*(commit|tag|push)\b",
        r"\$\(\s*(?:command\s+)?git\s+(?:-[^\s]+\s+)*(commit|tag|push)\b",
        r"`\s*(?:command\s+)?git\s+(?:-[^\s]+\s+)*(commit|tag|push)\b",
    )
    for pattern in executable_git_mutation_patterns:
        match = re.search(pattern, release_executable_text)
        if match:
            errors.append(
                "release validation may not execute git {0}".format(match.group(1))
            )
    if re.search(r"(?:eval\s+|(?:ba)?sh\s+-c\b)", release_executable_text):
        errors.append("release validation may not use eval or an indirect shell command")
    if re.search(
        r"(?:^|\$\(|[;&|]\s*)git\s+status\s+--porcelain=v1\s+--untracked-files=all",
        release_executable_text,
    ) is None:
        errors.append("release validation must reject a dirty worktree")
    if errors:
        raise Phase0Error("\n".join(errors))
    return {
        "workflow": relative(root, workflow),
        "workflow_sha256": sha256_file(workflow),
        "runner": relative(root, runner),
        "runner_sha256": sha256_file(runner),
        "release_script": relative(root, release_script),
        "release_script_sha256": sha256_file(release_script),
    }


def new_check(
    check_id: str,
    required: bool,
    status: str,
    command: Optional[Sequence[str]],
    exit_code: Optional[int],
    duration_ms: int,
    log: str,
    details: Optional[Mapping[str, Any]] = None,
) -> Dict[str, Any]:
    if status not in EVIDENCE_STATUSES:
        raise ValueError("invalid evidence status: " + status)
    normalized_log = log.rstrip() + ("\n" if log else "")
    return {
        "id": check_id,
        "required": required,
        "status": status,
        "command": list(command) if command is not None else None,
        "exit_code": exit_code,
        "duration_ms": duration_ms,
        "log_sha256": sha256_text(normalized_log),
        "log": normalized_log,
        "details": dict(details or {}),
    }


def check_from_command(
    check_id: str,
    required: bool,
    result: CommandResult,
    details: Optional[Mapping[str, Any]] = None,
) -> Dict[str, Any]:
    if result.exit_code == 0:
        status = "PASS"
    elif result.exit_code in (124, 127):
        status = "INFRA_ERROR"
    else:
        status = "FAIL"
    return new_check(
        check_id,
        required,
        status,
        result.command,
        result.exit_code,
        result.duration_ms,
        result.output,
        details,
    )


def evaluate_checks(checks: Sequence[Mapping[str, Any]]) -> Tuple[str, Dict[str, int]]:
    required = [check for check in checks if check.get("required") is True]
    counts = {status.casefold(): 0 for status in EVIDENCE_STATUSES}
    for check in checks:
        status = check.get("status")
        if status not in EVIDENCE_STATUSES:
            counts["infra_error"] += 1
        else:
            counts[status.casefold()] += 1
    summary = {
        "total": len(checks),
        "required": len(required),
        "passed": counts["pass"],
        "failed": counts["fail"],
        "skipped": counts["skip"],
        "partial": counts["partial"],
        "not_run": counts["not_run"],
        "infra_error": counts["infra_error"],
        "not_applicable": counts["not_applicable"],
    }
    overall = (
        "PASS"
        if required and all(check.get("status") == "PASS" for check in required)
        else "FAIL"
    )
    return overall, summary


def detect_node(node_argument: Optional[str]) -> Optional[str]:
    candidates = [
        node_argument,
        os.environ.get("DPS_NODE"),
        "node",
        "/Applications/ChatGPT.app/Contents/Resources/cua_node/bin/node",
        str(
            Path.home()
            / ".cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node"
        ),
    ]
    for candidate in candidates:
        if not candidate:
            continue
        if "/" in candidate:
            path = Path(candidate).expanduser()
            if path.is_file() and os.access(str(path), os.X_OK):
                return str(path)
        else:
            from shutil import which

            resolved = which(candidate)
            if resolved:
                return resolved
    return None


def node_version(node: str, root: Path) -> Tuple[Optional[str], Optional[str]]:
    result = run_command([node, "--version"], root, timeout_seconds=30)
    if result.exit_code != 0:
        return None, result.output.strip()
    version = result.output.strip()
    if re.fullmatch(r"v([0-9]+)\.([0-9]+)\.([0-9]+)", version) is None:
        return None, "unrecognized Node version: " + version
    if version != REQUIRED_NODE_VERSION:
        return None, "Node {0} is required, got {1}".format(
            REQUIRED_NODE_VERSION.removeprefix("v"), version
        )
    return version, None


def toolchain_details(root: Path, node_argument: Optional[str]) -> Tuple[Dict[str, Any], List[str]]:
    errors: List[str] = []
    python_version = tuple(sys.version_info[:3])
    if python_version != REQUIRED_PYTHON:
        errors.append(
            "Python {0} is required, got {1}".format(
                ".".join(str(value) for value in REQUIRED_PYTHON),
                ".".join(str(value) for value in python_version),
            )
        )
    node = detect_node(node_argument)
    version: Optional[str] = None
    if node is None:
        errors.append("Node 24 executable was not found")
    else:
        version, error = node_version(node, root)
        if error:
            errors.append(error)
    dotnet_wrapper = root / "scripts" / "dotnet-pinned.sh"
    dotnet_version: Optional[str] = None
    if (
        not dotnet_wrapper.is_file()
        or dotnet_wrapper.is_symlink()
        or not os.access(str(dotnet_wrapper), os.X_OK)
    ):
        errors.append("executable scripts/dotnet-pinned.sh is required")
    else:
        dotnet_probe_environment = dict(os.environ)
        # The gate selects the SDK from the checked runner PATH (or the
        # wrapper's documented local fallback); an ambient DPS_DOTNET value
        # must not replace the executable being attested.
        dotnet_probe_environment.pop("DPS_DOTNET", None)
        dotnet_result = run_command(
            [str(dotnet_wrapper), "--version"],
            root,
            timeout_seconds=30,
            env=dotnet_probe_environment,
        )
        if dotnet_result.exit_code != 0:
            errors.append(
                "pinned .NET SDK probe failed ({0}): {1}".format(
                    dotnet_result.exit_code, dotnet_result.output.strip()
                )
            )
        else:
            candidate = dotnet_result.output.strip()
            if candidate != REQUIRED_DOTNET_SDK:
                errors.append(
                    "NET SDK {0} is required, got {1}".format(
                        REQUIRED_DOTNET_SDK, candidate or "empty output"
                    )
                )
            else:
                dotnet_version = candidate
    return (
        {
            "os": platform.platform(),
            "python": platform.python_version(),
            "python_executable": sys.executable,
            "node": version,
            "node_executable": node,
            "dotnet_sdk": dotnet_version,
            "dotnet_wrapper": relative(root, dotnet_wrapper),
            "runner_os": os.environ.get("RUNNER_OS"),
            "runner_arch": os.environ.get("RUNNER_ARCH"),
            "github_actions": os.environ.get("GITHUB_ACTIONS"),
            "runner_image_os": os.environ.get("ImageOS"),
            "runner_image_version": os.environ.get("ImageVersion"),
        },
        errors,
    )


def workspace_digest(root: Path, baseline_commit: str) -> str:
    paths = _changed_paths(root, baseline_commit)
    values = []
    for value in paths:
        path = root / value
        values.append(
            {"path": value, "sha256": sha256_file(path) if path.is_file() else None}
        )
    return sha256_text(stable_json(values))


def _write_all(file_descriptor: int, payload: bytes) -> None:
    offset = 0
    while offset < len(payload):
        written = os.write(file_descriptor, payload[offset:])
        if written <= 0:
            raise OSError("evidence write made no forward progress")
        offset += written


def _open_directory_no_follow(path: Path) -> int:
    supported = (
        hasattr(os, "O_DIRECTORY")
        and hasattr(os, "O_NOFOLLOW")
        and os.open in os.supports_dir_fd
        and os.mkdir in os.supports_dir_fd
        and os.rename in os.supports_dir_fd
        and os.unlink in os.supports_dir_fd
    )
    if not supported:
        raise OSError(
            errno.ENOTSUP,
            "secure evidence writes require directory descriptors, O_NOFOLLOW, and dirfd rename",
        )

    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    descriptor = os.open(path.anchor, flags)
    try:
        for part in path.parts[1:]:
            try:
                child = os.open(part, flags, dir_fd=descriptor)
            except FileNotFoundError:
                try:
                    os.mkdir(part, mode=0o700, dir_fd=descriptor)
                except FileExistsError:
                    # A concurrent trusted writer may have created the same
                    # directory. Re-open it with O_NOFOLLOW so a symlink or
                    # non-directory replacement still fails closed.
                    pass
                child = os.open(part, flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def write_evidence(path: Path, evidence: Mapping[str, Any]) -> None:
    path = Path(os.path.abspath(os.fspath(path)))
    payload = (
        json.dumps(evidence, ensure_ascii=False, sort_keys=True, indent=2) + "\n"
    ).encode("utf-8")
    temporary_name = ".{0}.{1}.{2}.tmp".format(
        path.name, os.getpid(), os.urandom(8).hex()
    )
    directory_descriptor: Optional[int] = None
    file_descriptor: Optional[int] = None
    try:
        directory_descriptor = _open_directory_no_follow(path.parent)
        file_descriptor = os.open(
            temporary_name,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
            0o600,
            dir_fd=directory_descriptor,
        )
        _write_all(file_descriptor, payload)
        os.fsync(file_descriptor)
        completed_descriptor = file_descriptor
        file_descriptor = None
        os.close(completed_descriptor)
        os.rename(
            temporary_name,
            path.name,
            src_dir_fd=directory_descriptor,
            dst_dir_fd=directory_descriptor,
        )
        os.fsync(directory_descriptor)
    finally:
        try:
            if file_descriptor is not None:
                os.close(file_descriptor)
            if directory_descriptor is not None:
                try:
                    os.unlink(temporary_name, dir_fd=directory_descriptor)
                except FileNotFoundError:
                    pass
        finally:
            if directory_descriptor is not None:
                os.close(directory_descriptor)


def shell_join(command: Sequence[str]) -> str:
    return " ".join(shlex.quote(value) for value in command)
