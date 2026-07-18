#!/usr/bin/env python3
"""The only supported DPS Phase 0 gate entry point.

Every required check is represented in one evidence bundle.  A required check
passes only with the literal status ``PASS``; skip, partial, empty, timeout,
missing evidence, and infrastructure errors all make this process exit nonzero.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import errno
import functools
import hashlib
import json
import os
import re
import shlex
import shutil
import stat
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Sequence


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = Path(__file__).resolve().parent
if str(CI_DIRECTORY) not in sys.path:
    sys.path.insert(0, str(CI_DIRECTORY))

from phase0 import (  # noqa: E402
    REQUIRED_DOTNET_SDK,
    REQUIRED_NODE_VERSION,
    VERIFICATION_LEVEL,
    CommandResult,
    Phase0Error,
    check_from_command,
    evaluate_checks,
    discover_registered_module_dirs,
    git_output,
    load_json_compatible_yaml,
    manifest_module_id,
    new_check,
    resolve_instruction_receipt,
    run_command,
    sha256_text,
    sha256_file,
    stable_json,
    toolchain_details,
    validate_ci_integrity,
    validate_governance,
    validate_instruction_receipt,
    validate_json_schema,
    workspace_digest,
    _open_directory_no_follow,
    _write_all,
)


ALLOWED_MANIFEST_ENVIRONMENT = {"PYTHONPATH"}
ALLOWED_TRUSTED_EXECUTOR_ENVIRONMENT = ALLOWED_MANIFEST_ENVIRONMENT | {
    "DPS_PSQL",
    "DPS_TEST_POSTGRES",
    "DPS_TEST_POSTGRES_ADMIN_URI",
    "DPS_TEST_POSTGRES_RUNTIME_URI",
    "DPS_TEST_POSTGRES_URI",
    "DPS_TEST_PLATFORM_AUTHORITY_PKCS8_FILE",
}
PYTHON_NAMES = {"python", "python3", "python3.12"}
FORBIDDEN_COMMAND_FRAGMENTS = ("\n", "\r", "\x00", "`", "$(`", "${", ";", "|", ">", "<")
PUBLICATION_SCHEMA_VERSION = "dps.evidence-publication/v1"
PUBLICATION_MARKER_SUFFIX = ".publication.json"
PUBLICATION_CLAIM_SUFFIX = ".publication.lock"
MAX_PUBLICATION_FILE_BYTES = 64 * 1024 * 1024
DEFAULT_PHASE0_RUNS_ROOT = Path("Reports/ci/phase0-runs")


@dataclass(frozen=True)
class TrustedInvocation:
    argv: Sequence[str]
    kind: str
    minimum_tests: int = 1


@dataclass(frozen=True)
class TrustedSuitePlan:
    module_id: str
    suite_id: str
    test_type: str
    evidence_level: str
    declared_command: str
    environment: Mapping[str, str]
    invocations: Sequence[TrustedInvocation]


def parse_arguments(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run the trusted DPS Phase 0 gate")
    parser.add_argument(
        "--evidence",
        default=None,
        help=(
            "Evidence JSON path. When omitted, a unique run-id directory under "
            "Reports/ci/phase0-runs is used."
        ),
    )
    parser.add_argument(
        "--node",
        default=None,
        help="Node 24 executable. Known local bundled runtimes are auto-detected.",
    )
    parser.add_argument(
        "--base",
        default=None,
        help="Baseline commit used for changed scope and instruction receipt",
    )
    parser.add_argument(
        "--receipt-in",
        default=None,
        help="Validate a previously issued instruction receipt instead of issuing one",
    )
    parser.add_argument(
        "--receipt-out",
        default=None,
        help="Optional path for the freshly validated instruction receipt",
    )
    parser.add_argument(
        "--diagnostic-workspace",
        action="store_true",
        help=(
            "Run all checks on a dirty workspace without issuing formal "
            "REPOSITORY_STATIC_VERIFIED evidence"
        ),
    )
    return parser.parse_args(argv)


def _new_publication_run_id() -> str:
    return os.urandom(16).hex()


def _default_phase0_evidence_path(run_id: str) -> Path:
    if re.fullmatch(r"[0-9a-f]{32}", run_id) is None:
        raise Phase0Error("Phase0 publication run id is invalid")
    return DEFAULT_PHASE0_RUNS_ROOT / run_id / "phase0-evidence.json"


def resolve_baseline(explicit: Optional[str]) -> str:
    candidates = (
        explicit,
        os.environ.get("DPS_BASELINE_COMMIT"),
        os.environ.get("GITHUB_BASE_SHA"),
    )
    for candidate in candidates:
        if candidate and candidate.strip("0"):
            return git_output(ROOT, ["rev-parse", candidate + "^{commit}"])
    return git_output(ROOT, ["rev-parse", "HEAD"])


def workspace_cleanliness_check(root: Path, diagnostic: bool) -> Dict[str, Any]:
    started = time.monotonic()
    result = run_command(
        [
            "git",
            "status",
            "--porcelain=v1",
            "--untracked-files=all",
            "-z",
        ],
        root,
        timeout_seconds=30,
    )
    dirty_entries = [value for value in result.output.split("\0") if value]
    clean = result.exit_code == 0 and not dirty_entries
    formal_eligible = clean and not diagnostic
    details = {
        "clean": clean,
        "diagnostic_workspace": diagnostic,
        "formal_evidence_eligible": formal_eligible,
        "dirty_entry_count": len(dirty_entries),
        "status_sha256": sha256_text(result.output),
    }
    if result.exit_code != 0:
        status = "INFRA_ERROR"
        exit_code = result.exit_code
        log = "ERROR: cannot determine repository workspace status\n" + result.output
    elif clean:
        status = "PASS"
        exit_code = 0
        log = (
            "clean checkout eligible for formal evidence"
            if not diagnostic
            else "clean checkout intentionally limited to diagnostic evidence"
        )
    elif diagnostic:
        status = "PASS"
        exit_code = 0
        log = (
            "dirty workspace accepted only for WORKSPACE_DIAGNOSTIC_ONLY; "
            "formal evidence and commit attribution are suppressed"
        )
    else:
        status = "FAIL"
        exit_code = 1
        log = (
            "dirty workspace cannot issue REPOSITORY_STATIC_VERIFIED evidence; "
            "rerun with --diagnostic-workspace for non-releasable diagnostics"
        )
    return new_check(
        "clean-checkout-evidence-boundary",
        True,
        status,
        result.command,
        exit_code,
        int((time.monotonic() - started) * 1000),
        log,
        details,
    )


def evidence_classification(
    overall_status: str, formal_evidence_eligible: bool, diagnostic: bool
) -> tuple[str, Optional[str]]:
    if diagnostic:
        return "WORKSPACE_DIAGNOSTIC_ONLY", None
    level = (
        VERIFICATION_LEVEL
        if overall_status == "PASS" and formal_evidence_eligible
        else None
    )
    return VERIFICATION_LEVEL, level


def in_process_check(check_id: str, operation: Any) -> Dict[str, Any]:
    started = time.monotonic()
    try:
        details = operation()
        if details is None:
            details = {}
        log = json.dumps(details, ensure_ascii=False, sort_keys=True, indent=2)
        return new_check(
            check_id,
            True,
            "PASS",
            None,
            0,
            int((time.monotonic() - started) * 1000),
            log,
            details if isinstance(details, Mapping) else {},
        )
    except Phase0Error as exc:
        return new_check(
            check_id,
            True,
            "FAIL",
            None,
            1,
            int((time.monotonic() - started) * 1000),
            "ERROR: " + str(exc),
        )
    except Exception as exc:  # pragma: no cover - last-resort evidence boundary
        return new_check(
            check_id,
            True,
            "INFRA_ERROR",
            None,
            1,
            int((time.monotonic() - started) * 1000),
            "UNEXPECTED ERROR: {0}: {1}".format(type(exc).__name__, exc),
        )


def run_phase0_unittests() -> Dict[str, Any]:
    minimum_adversarial_tests = 137
    required_inventory = {
        "test_missing_standard_module_layout_is_rejected",
        "test_placeholder_only_src_is_rejected",
        "test_standard_layout_symlink_is_rejected",
        "test_ignored_only_standard_directory_is_not_reproducible",
        "test_missing_runtime_entrypoint_is_rejected",
        "test_missing_artifact_build_path_is_rejected",
        "test_cross_module_src_project_reference_is_rejected",
        "test_shell_metacharacter_is_rejected",
        "test_unknown_environment_prefix_is_rejected",
        "test_manifest_cannot_declare_trusted_executor_postgres_environment",
        "test_path_traversal_is_rejected",
        "test_symlink_escape_is_rejected",
        "test_zero_unittests_fails_even_with_exit_zero",
        "test_skipped_unittest_fails_even_with_exit_zero",
        "test_plain_stdout_pass_is_not_test_evidence",
        "test_bash_suite_cannot_pass_from_a_different_test_category",
        "test_bash_suite_preserves_strict_category_specific_floor",
        "test_bash_suite_rejects_non_literal_category_floor",
        "test_bash_category_must_match_declared_suite_type",
        "test_bash_suite_rejects_extra_argument",
        "test_json_tool_is_not_semantic_contract_evidence",
        "test_timeout_is_infrastructure_error",
        "test_missing_required_static_suite_generates_failure",
        "test_required_suite_missing_command_generates_failure",
        "test_solution_omitting_project_fails_before_build",
        "test_missing_dotnet_pin_is_rejected",
        "test_mutable_action_tag_is_rejected",
        "test_latest_runner_image_is_rejected",
        "test_unapproved_action_is_rejected",
        "test_node_patch_version_mismatch_is_rejected",
        "test_trusted_environment_ignores_path_and_dps_node_injection",
        "test_unlocked_fixed_node_candidate_fails_closed",
        "test_trusted_environment_rejects_path_override",
        "test_trusted_environment_forwards_candidate_postgres_keys",
        "test_trusted_dotnet_ignores_ambient_path_and_home",
        "test_trusted_environment_uses_private_non_ambient_state",
        "test_manifest_cannot_disable_the_executor_network_sandbox",
        "test_world_accessible_trusted_state_is_rejected",
        "test_restore_platform_failure_is_infrastructure_error",
        "test_command_timeout_kills_the_posix_process_group",
        "test_phase0_output_is_restricted_to_lowercase_ignored_reports_ci",
        "test_clean_checkout_is_formal_evidence_eligible",
        "test_modified_tracked_test_file_blocks_formal_evidence",
        "test_untracked_production_file_blocks_formal_evidence",
        "test_dirty_diagnostic_workspace_never_issues_formal_level",
        "test_global_required_unittest_skip_is_not_pass",
        "test_global_required_unittest_expected_failure_is_not_pass",
        "test_release_git_commit_is_rejected",
        "test_release_git_push_in_command_substitution_is_rejected",
        "test_release_missing_candidate_bom_validator_is_rejected",
        "test_release_comment_only_invocations_are_rejected",
        "test_release_network_command_is_rejected",
        "test_release_deployment_command_is_rejected",
        "test_release_file_deletion_is_rejected",
        "test_release_arbitrary_python_is_rejected",
        "test_runner_comment_only_markers_are_rejected",
        "test_runner_fixed_success_is_rejected",
        "test_runner_if_false_operations_are_not_reachable",
        "test_runner_calls_after_return_are_not_reachable",
        "test_workflow_echo_gate_is_rejected",
        "test_workflow_always_false_gate_step_is_rejected",
        "test_workflow_pip_without_require_hashes_is_rejected",
        "test_workflow_diagnostic_mode_is_rejected",
        "test_candidate_level_is_schema_locked_to_null",
        "test_all_candidate_trust_paths_are_bound_by_production_resolver",
        "test_audit_contract_cannot_be_retargeted_to_production_source",
        "test_policy_floor_is_injected_into_every_effective_plan",
        "test_duplicate_unittest_summaries_cannot_raise_the_count_floor",
        "test_isolated_python_ignores_shadow_unittest_module",
        "test_postgres_password_value_is_redacted_even_without_key_name",
        "test_evidence_output_is_restricted_to_ignored_reports_ci",
        "test_json_evidence_read_rejects_symlink_and_binds_one_file_sha",
        "test_parallel_companion_writes_remain_distinct",
        "test_empty_phase0_check_inventory_is_never_accepted",
        "test_weak_receipt_empty_suites_forgery_is_rejected",
        "test_contract_policy_is_exactly_the_required_contract_inventory",
        "test_integration_policy_is_exactly_the_required_integration_inventory",
        # RebuildPlan 4.2.3 old/new dual-run for the R0-B receipt migration.  Naming
        # these here makes the frozen migration corpus load-bearing: deleting or
        # renaming it fails this gate instead of silently shrinking coverage.
        "test_every_frozen_file_matches_its_recorded_digest",
        "test_old_schema_rejects_all_34_current_manifests",
        "test_new_schema_rejects_all_34_baseline_manifests",
        "test_reintroducing_the_factory_resolver_breaks_the_new_gate",
        "test_stale_receipt_is_rejected_after_a_manifest_edit",
        # The attack corpus is only evidence while these hold: the corpus file
        # cannot be neutered into no-op mutations or weakened verdicts, because the
        # expectations are pinned in test code rather than read from the corpus.
        "test_corpus_declares_exactly_the_pinned_attack_classes",
        "test_declared_expectations_match_the_pinned_verdicts",
        "test_every_rejecting_sample_actually_mutates_its_base",
        "test_pinned_attack_verdicts_hold_against_both_schemas",
    }
    command = [
        sys.executable,
        "-I",
        "-m",
        "unittest",
        "discover",
        "-v",
        "-s",
        "Tests/ci",
        "-p",
        "test_*.py",
    ]
    result = run_command(command, ROOT, timeout_seconds=180)
    check = check_from_command("phase0-adversarial-unit-tests", True, result)
    executed, summary_reason = _executed_test_count(
        TrustedInvocation(command, "python-unittest", minimum_adversarial_tests),
        result.output,
    )
    missing_inventory = sorted(
        test_name for test_name in required_inventory if test_name not in result.output
    )
    if summary_reason:
        missing_inventory.append("invalid unittest summary: " + summary_reason)
    check["details"]["required_inventory"] = sorted(required_inventory)
    return enforce_unittest_evidence(
        check,
        result,
        executed,
        minimum_adversarial_tests,
        missing_inventory,
        "Phase0 adversarial tests",
    )


def run_external_gate_unittests() -> Dict[str, Any]:
    minimum_tests = 25
    command = [
        sys.executable,
        "-I",
        "-m",
        "unittest",
        "discover",
        "-s",
        "Tools/verification/tests",
        "-p",
        "test_*.py",
    ]
    with _trusted_test_environment_scope({}) as environment:
        result = run_command(
            command,
            ROOT,
            timeout_seconds=180,
            env=environment,
        )
    check = check_from_command(
        "external-gate-adversarial-unit-tests",
        True,
        result,
        {
            "module_id": "evidence-service",
            "test_type": "unit",
            "minimum_tests": minimum_tests,
            "external_gates_executed": False,
        },
    )
    executed, summary_reason = _executed_test_count(
        TrustedInvocation(command, "python-unittest", minimum_tests),
        result.output,
    )
    return enforce_unittest_evidence(
        check,
        result,
        executed,
        minimum_tests,
        (() if summary_reason is None else ("invalid unittest summary: " + summary_reason,)),
        "external gate adversarial tests",
    )


def _within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def _safe_phase0_output_path(root: Path, raw: Path, label: str) -> Path:
    root_path = root.resolve()
    path = raw if raw.is_absolute() else root_path / raw
    reports_root = root_path / "Reports" / "ci"
    try:
        lexical = Path(os.path.abspath(os.fspath(path)))
        relative_to_root = lexical.relative_to(root_path)
        relative_to_reports = lexical.relative_to(reports_root)
    except (OSError, RuntimeError, ValueError) as exc:
        raise Phase0Error(
            label + " must be written under ignored Reports/ci: " + str(exc)
        )
    if not relative_to_reports.parts:
        raise Phase0Error(label + " must name a JSON file under Reports/ci")
    for part in relative_to_reports.parts:
        if re.fullmatch(r"[a-z0-9][a-z0-9._-]*", part) is None:
            raise Phase0Error(
                label + " path components must use lowercase ASCII safe names"
            )
    current = root_path
    for part in relative_to_root.parts:
        current = current / part
        if current.is_symlink():
            raise Phase0Error(label + " path may not traverse a symlink")
    try:
        candidate = lexical.resolve(strict=False)
        allowed = reports_root.resolve(strict=False)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error(label + " path cannot be resolved: " + str(exc))
    if not _within(candidate, allowed) or candidate == allowed:
        raise Phase0Error(label + " must be written under ignored Reports/ci")
    if candidate.suffix != ".json":
        raise Phase0Error(label + " must use a lowercase .json filename")
    if candidate.name.endswith(PUBLICATION_MARKER_SUFFIX):
        raise Phase0Error(label + " may not occupy a publication marker path")
    artifacts = (
        candidate,
        _publication_marker_path(candidate),
        _publication_claim_path(candidate),
    )
    for artifact in artifacts:
        if artifact.is_symlink():
            raise Phase0Error(label + " publication paths may not be symlinks")
        if artifact.exists() and not artifact.is_file():
            raise Phase0Error(label + " publication paths must be regular files")
        relative = artifact.relative_to(root_path).as_posix()
        tracked = run_command(
            ["git", "ls-files", "--error-unmatch", "--", relative],
            root,
            timeout_seconds=30,
        )
        if tracked.exit_code == 0:
            raise Phase0Error(label + " may not overwrite a tracked publication file")
        ignored = run_command(
            ["git", "check-ignore", "--quiet", "--no-index", "--", relative],
            root,
            timeout_seconds=30,
        )
        if ignored.exit_code != 0:
            raise Phase0Error(label + " publication paths must be Git-ignored")
    return candidate


def _publication_marker_path(path: Path) -> Path:
    return path.with_name(path.name + PUBLICATION_MARKER_SUFFIX)


def _publication_claim_path(path: Path) -> Path:
    return path.with_name(path.name + PUBLICATION_CLAIM_SUFFIX)


def _publication_record(
    status: str,
    run_id: str,
    payload_name: str,
    payload_sha256: Optional[str],
    payload_size: Optional[int],
) -> Dict[str, Any]:
    record: Dict[str, Any] = {
        "schema_version": PUBLICATION_SCHEMA_VERSION,
        "status": status,
        "run_id": run_id,
        "payload_name": payload_name,
        "payload_sha256": payload_sha256,
        "payload_size": payload_size,
    }
    record["record_sha256"] = sha256_text(stable_json(record))
    return record


def _publication_json_bytes(value: Mapping[str, Any]) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, sort_keys=True, indent=2) + "\n"
    ).encode("utf-8")


def _decode_publication_record(
    payload: bytes,
    label: str,
    payload_name: str,
    allowed_statuses: set[str],
) -> Dict[str, Any]:
    try:
        marker = json.loads(payload.decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise Phase0Error(label + " is invalid: " + str(exc))
    if not isinstance(marker, dict):
        raise Phase0Error(label + " must contain an object")
    expected_keys = {
        "schema_version",
        "status",
        "run_id",
        "payload_name",
        "payload_sha256",
        "payload_size",
        "record_sha256",
    }
    if set(marker) != expected_keys:
        raise Phase0Error(label + " fields are not canonical")
    supplied_record_sha = marker.get("record_sha256")
    marker_without_sha = dict(marker)
    marker_without_sha.pop("record_sha256", None)
    status = marker.get("status")
    digest = marker.get("payload_sha256")
    size = marker.get("payload_size")
    payload_binding_valid = (
        isinstance(digest, str)
        and re.fullmatch(r"[0-9a-f]{64}", digest) is not None
        and isinstance(size, int)
        and size >= 2
    )
    payload_binding_empty = digest is None and size is None
    if (
        marker.get("schema_version") != PUBLICATION_SCHEMA_VERSION
        or status not in allowed_statuses
        or not isinstance(marker.get("run_id"), str)
        or re.fullmatch(r"[0-9a-f]{32}", str(marker.get("run_id"))) is None
        or marker.get("payload_name") != payload_name
        or supplied_record_sha != sha256_text(stable_json(marker_without_sha))
        or (status == "COMMITTED" and not payload_binding_valid)
        or (
            status != "COMMITTED"
            and not payload_binding_valid
            and not payload_binding_empty
        )
    ):
        binding_kind = (
            "COMMITTED" if allowed_statuses == {"COMMITTED"} else "publication"
        )
        raise Phase0Error(
            label + " is not a valid " + binding_kind + " binding"
        )
    return marker


def _read_regular_file_at(
    directory_descriptor: int,
    name: str,
    label: str,
    maximum_bytes: int = MAX_PUBLICATION_FILE_BYTES,
) -> tuple[bytes, tuple[int, int, int]]:
    descriptor: Optional[int] = None
    try:
        flags = os.O_RDONLY
        if not hasattr(os, "O_NOFOLLOW"):
            raise OSError(
                errno.ENOTSUP,
                "committed evidence reads require O_NOFOLLOW",
            )
        flags |= os.O_NOFOLLOW
        descriptor = os.open(name, flags, dir_fd=directory_descriptor)
        opened = os.fstat(descriptor)
        if not stat.S_ISREG(opened.st_mode):
            raise Phase0Error(label + " is not a regular file")
        chunks: List[bytes] = []
        total = 0
        while True:
            chunk = os.read(descriptor, 1024 * 1024)
            if not chunk:
                break
            total += len(chunk)
            if total > maximum_bytes:
                raise Phase0Error(label + " exceeds the evidence size limit")
            chunks.append(chunk)
        final = os.fstat(descriptor)
        if (
            final.st_dev,
            final.st_ino,
            final.st_size,
        ) != (opened.st_dev, opened.st_ino, total):
            raise Phase0Error(label + " changed while it was being read")
        return b"".join(chunks), (final.st_dev, final.st_ino, final.st_size)
    except Phase0Error:
        raise
    except OSError as exc:
        raise Phase0Error("cannot safely read {0}: {1}".format(label, exc))
    finally:
        if descriptor is not None:
            os.close(descriptor)


def _open_existing_directory_no_follow(path: Path) -> int:
    if (
        not hasattr(os, "O_DIRECTORY")
        or not hasattr(os, "O_NOFOLLOW")
        or os.open not in os.supports_dir_fd
    ):
        raise OSError(
            errno.ENOTSUP,
            "committed evidence reads require directory descriptors and O_NOFOLLOW",
        )
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    descriptor = os.open(path.anchor, flags)
    try:
        for part in path.parts[1:]:
            child = os.open(part, flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def _path_identity_at(
    directory_descriptor: int, name: str, label: str
) -> tuple[int, int, int]:
    try:
        current = os.stat(name, dir_fd=directory_descriptor, follow_symlinks=False)
    except OSError as exc:
        raise Phase0Error("cannot revalidate {0}: {1}".format(label, exc))
    if not stat.S_ISREG(current.st_mode):
        raise Phase0Error(label + " is no longer a regular file")
    return current.st_dev, current.st_ino, current.st_size


def _claim_absent_at(directory_descriptor: int, claim_name: str) -> None:
    try:
        os.stat(claim_name, dir_fd=directory_descriptor, follow_symlinks=False)
    except FileNotFoundError:
        return
    except OSError as exc:
        raise Phase0Error("cannot inspect evidence publication claim: " + str(exc))
    raise Phase0Error(
        "evidence publication is in progress, failed, or requires manual recovery"
    )


def _load_committed_json_object_with_sha(
    path: Path, label: str
) -> tuple[Dict[str, Any], str]:
    """Read one explicitly COMMITTED payload and its integrity marker.

    A lingering exclusive claim is always a denial, including after a crashed
    or failed writer.  That intentionally requires a new output path or manual
    review rather than treating an uncertain payload as evidence.
    """

    absolute = Path(os.path.abspath(os.fspath(path)))
    directory_descriptor: Optional[int] = None
    try:
        directory_descriptor = _open_existing_directory_no_follow(absolute.parent)
        claim_name = _publication_claim_path(absolute).name
        marker_name = _publication_marker_path(absolute).name
        _claim_absent_at(directory_descriptor, claim_name)
        marker_payload, marker_identity = _read_regular_file_at(
            directory_descriptor,
            marker_name,
            label + " publication marker",
            maximum_bytes=64 * 1024,
        )
        marker = _decode_publication_record(
            marker_payload,
            label + " publication marker",
            absolute.name,
            {"COMMITTED"},
        )

        payload, payload_identity = _read_regular_file_at(
            directory_descriptor, absolute.name, label
        )
        payload_sha = hashlib.sha256(payload).hexdigest()
        if (
            len(payload) != marker["payload_size"]
            or payload_sha != marker["payload_sha256"]
        ):
            raise Phase0Error(label + " does not match its COMMITTED integrity binding")
        try:
            value = json.loads(payload.decode("utf-8-sig"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            raise Phase0Error("invalid {0}: {1}".format(label, exc))
        if not isinstance(value, dict):
            raise Phase0Error(label + " must contain an object")

        _claim_absent_at(directory_descriptor, claim_name)
        if _path_identity_at(
            directory_descriptor, marker_name, label + " publication marker"
        ) != marker_identity:
            raise Phase0Error(label + " publication marker changed during read")
        if _path_identity_at(directory_descriptor, absolute.name, label) != payload_identity:
            raise Phase0Error(label + " changed during committed read")
        return value, payload_sha
    except Phase0Error:
        raise
    except OSError as exc:
        raise Phase0Error("cannot safely read {0}: {1}".format(label, exc))
    finally:
        if directory_descriptor is not None:
            os.close(directory_descriptor)


class EvidencePublication:
    """Fail-closed, single-writer publication for one logical evidence path."""

    def __init__(self, path: Path, *, run_id: Optional[str] = None):
        self.path = Path(os.path.abspath(os.fspath(path)))
        self.run_id = run_id or _new_publication_run_id()
        if re.fullmatch(r"[0-9a-f]{32}", self.run_id) is None:
            raise ValueError("evidence publication run id is invalid")
        self._directory_descriptor: Optional[int] = None
        self._claim_descriptor: Optional[int] = None
        self._staged_sha256: Optional[str] = None
        self._staged_size: Optional[int] = None
        self._committed = False
        self._aborted = False
        self._claim_released = False
        self._durability_uncertain = False

    @property
    def claim_path(self) -> Path:
        return _publication_claim_path(self.path)

    @property
    def marker_path(self) -> Path:
        return _publication_marker_path(self.path)

    def _atomic_write(self, name: str, payload: bytes) -> None:
        if self._directory_descriptor is None:
            raise RuntimeError("evidence publication has not been acquired")
        temporary_name = "publication-{0}-{1}.tmp".format(
            self.run_id, os.urandom(8).hex()
        )
        descriptor: Optional[int] = None
        try:
            descriptor = os.open(
                temporary_name,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                0o600,
                dir_fd=self._directory_descriptor,
            )
            _write_all(descriptor, payload)
            os.fsync(descriptor)
            completed = descriptor
            descriptor = None
            os.close(completed)
            os.rename(
                temporary_name,
                name,
                src_dir_fd=self._directory_descriptor,
                dst_dir_fd=self._directory_descriptor,
            )
            os.fsync(self._directory_descriptor)
        finally:
            if descriptor is not None:
                os.close(descriptor)
            if self._directory_descriptor is not None:
                try:
                    os.unlink(temporary_name, dir_fd=self._directory_descriptor)
                except FileNotFoundError:
                    pass

    def _release_claim(self) -> None:
        if self._directory_descriptor is None or self._claim_released:
            return
        os.unlink(self.claim_path.name, dir_fd=self._directory_descriptor)
        try:
            os.fsync(self._directory_descriptor)
        except BaseException:
            # The unlink is visible in this process but its durability is now
            # uncertain. Re-create the quarantine claim before propagating the
            # failure so local readers and a complete evidence-directory
            # artifact both continue to fail closed.
            replacement_descriptor: Optional[int] = None
            try:
                replacement_descriptor = os.open(
                    self.claim_path.name,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=self._directory_descriptor,
                )
                claim = _publication_record(
                    "PREPARING", self.run_id, self.path.name, None, None
                )
                _write_all(
                    replacement_descriptor, _publication_json_bytes(claim)
                )
                os.fsync(replacement_descriptor)
                os.fsync(self._directory_descriptor)
            except FileExistsError:
                # A claim at this name is already a denial for every reader.
                pass
            except BaseException:
                # Preserve the original durability failure. The caller marks
                # the publication uncertain; no success may be reported.
                pass
            finally:
                if replacement_descriptor is not None:
                    try:
                        os.close(replacement_descriptor)
                    except OSError:
                        pass
            raise
        self._claim_released = True

    def _existing_marker_status(self) -> Optional[str]:
        if self._directory_descriptor is None:
            raise RuntimeError("evidence publication has not been acquired")
        try:
            marker_stat = os.stat(
                self.marker_path.name,
                dir_fd=self._directory_descriptor,
                follow_symlinks=False,
            )
        except FileNotFoundError:
            return None
        if not stat.S_ISREG(marker_stat.st_mode):
            raise Phase0Error("existing evidence publication marker is unsafe")
        payload, _ = _read_regular_file_at(
            self._directory_descriptor,
            self.marker_path.name,
            "existing evidence publication marker",
            maximum_bytes=64 * 1024,
        )
        marker = _decode_publication_record(
            payload,
            "existing evidence publication marker",
            self.path.name,
            {"PREPARING", "ABORTED", "COMMITTED"},
        )
        return str(marker["status"])

    def __enter__(self) -> "EvidencePublication":
        claim_created = False
        try:
            if not hasattr(os, "O_NOFOLLOW"):
                raise OSError(
                    errno.ENOTSUP,
                    "secure evidence publication requires O_NOFOLLOW",
                )
            self._directory_descriptor = _open_directory_no_follow(self.path.parent)
            try:
                self._claim_descriptor = os.open(
                    self.claim_path.name,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW,
                    0o600,
                    dir_fd=self._directory_descriptor,
                )
            except FileExistsError as exc:
                raise BlockingIOError(
                    errno.EBUSY,
                    "another run owns this evidence path or a failed run requires recovery",
                    str(self.claim_path),
                ) from exc
            claim_created = True
            claim = _publication_record(
                "PREPARING", self.run_id, self.path.name, None, None
            )
            _write_all(self._claim_descriptor, _publication_json_bytes(claim))
            os.fsync(self._claim_descriptor)
            os.fsync(self._directory_descriptor)
            existing_status = self._existing_marker_status()
            if existing_status == "COMMITTED":
                self._release_claim()
                raise FileExistsError(
                    errno.EEXIST,
                    "committed evidence is immutable; choose a new --evidence path",
                    str(self.path),
                )
            if existing_status == "PREPARING":
                self._durability_uncertain = True
                raise Phase0Error(
                    "existing PREPARING publication requires manual recovery"
                )
            preparing = _publication_record(
                "PREPARING", self.run_id, self.path.name, None, None
            )
            self._atomic_write(
                self.marker_path.name, _publication_json_bytes(preparing)
            )
            return self
        except BaseException:
            if self._claim_descriptor is not None:
                os.close(self._claim_descriptor)
                self._claim_descriptor = None
            if self._directory_descriptor is not None:
                os.close(self._directory_descriptor)
                self._directory_descriptor = None
            # Once the exclusive claim exists, retain it on every failed
            # acquisition/publication path.  Readers therefore fail closed
            # even if a later fsync reported an uncertain outcome.
            if not claim_created:
                self._committed = False
            raise

    def stage(self, evidence: Mapping[str, Any]) -> None:
        if self._directory_descriptor is None or self._committed:
            raise RuntimeError("evidence publication is not active")
        payload = _publication_json_bytes(evidence)
        if len(payload) > MAX_PUBLICATION_FILE_BYTES:
            raise Phase0Error("evidence payload exceeds the 64 MiB limit")
        try:
            self._atomic_write(self.path.name, payload)
        except BaseException:
            self._durability_uncertain = True
            raise
        self._staged_sha256 = hashlib.sha256(payload).hexdigest()
        self._staged_size = len(payload)

    def commit(self) -> None:
        if (
            self._directory_descriptor is None
            or self._claim_descriptor is None
            or self._staged_sha256 is None
            or self._staged_size is None
            or self._committed
        ):
            raise RuntimeError("evidence publication has no staged payload")
        payload, _ = _read_regular_file_at(
            self._directory_descriptor, self.path.name, "staged evidence"
        )
        if (
            len(payload) != self._staged_size
            or hashlib.sha256(payload).hexdigest() != self._staged_sha256
        ):
            raise Phase0Error("staged evidence changed before COMMITTED publication")
        committed = _publication_record(
            "COMMITTED",
            self.run_id,
            self.path.name,
            self._staged_sha256,
            self._staged_size,
        )
        committed_payload = _publication_json_bytes(committed)
        try:
            self._atomic_write(
                self.marker_path.name, committed_payload
            )
            published_marker, _ = _read_regular_file_at(
                self._directory_descriptor,
                self.marker_path.name,
                "COMMITTED evidence marker",
                maximum_bytes=64 * 1024,
            )
            if published_marker != committed_payload:
                raise Phase0Error(
                    "COMMITTED evidence marker changed before claim release"
                )
            self._release_claim()
        except BaseException:
            self._durability_uncertain = True
            raise
        self._committed = True

    def abort(self) -> None:
        if (
            self._directory_descriptor is None
            or self._claim_descriptor is None
            or self._committed
            or self._aborted
            or self._claim_released
        ):
            return
        if self._durability_uncertain:
            raise Phase0Error(
                "uncertain publication remains quarantined for manual recovery"
            )
        aborted = _publication_record(
            "ABORTED",
            self.run_id,
            self.path.name,
            self._staged_sha256,
            self._staged_size,
        )
        aborted_payload = _publication_json_bytes(aborted)
        try:
            self._atomic_write(self.marker_path.name, aborted_payload)
            published_marker, _ = _read_regular_file_at(
                self._directory_descriptor,
                self.marker_path.name,
                "ABORTED evidence marker",
                maximum_bytes=64 * 1024,
            )
            if published_marker != aborted_payload:
                raise Phase0Error(
                    "ABORTED evidence marker changed before claim release"
                )
            self._release_claim()
        except BaseException:
            self._durability_uncertain = True
            raise
        self._aborted = True

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        abort_failure: Optional[BaseException] = None
        try:
            if (
                not self._committed
                and not self._aborted
                and not self._claim_released
                and not self._durability_uncertain
            ):
                self.abort()
        except BaseException as failure:
            abort_failure = failure
        finally:
            if self._claim_descriptor is not None:
                try:
                    os.close(self._claim_descriptor)
                except OSError:
                    pass
                self._claim_descriptor = None
            if self._directory_descriptor is not None:
                try:
                    os.close(self._directory_descriptor)
                except OSError:
                    pass
                self._directory_descriptor = None
        if abort_failure is not None and exc_type is None:
            raise abort_failure


def write_evidence(
    path: Path,
    evidence: Mapping[str, Any],
    *,
    publication: Optional[EvidencePublication] = None,
    commit: bool = True,
) -> None:
    """Publish one JSON payload, preserving the historical two-argument API."""

    if publication is not None:
        if Path(os.path.abspath(os.fspath(path))) != publication.path:
            raise Phase0Error("evidence publication path does not match its claim")
        publication.stage(evidence)
        if commit:
            publication.commit()
        return
    with EvidencePublication(path) as owned_publication:
        owned_publication.stage(evidence)
        owned_publication.commit()


def _safe_existing_path(
    root: Path,
    module_root: Path,
    raw: str,
    *,
    require_file: bool = False,
    require_directory: bool = False,
) -> Path:
    if not isinstance(raw, str) or not raw or "\\" in raw or "\x00" in raw:
        raise Phase0Error("test command contains an invalid path")
    relative_path = Path(raw)
    if relative_path.is_absolute() or any(part in ("", "..") for part in relative_path.parts):
        raise Phase0Error("test command path must be a contained repository-relative path: " + raw)
    candidate = root / relative_path
    cursor = root
    for part in relative_path.parts:
        cursor = cursor / part
        if cursor.is_symlink():
            raise Phase0Error("test command path may not traverse a symlink: " + raw)
    try:
        resolved = candidate.resolve(strict=True)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error("test command path is missing: {0}: {1}".format(raw, exc))
    resolved_root = root.resolve()
    resolved_module = module_root.resolve()
    if not _within(resolved, resolved_root) or not _within(resolved, resolved_module):
        raise Phase0Error("test command path escapes its module: " + raw)
    if require_file and not resolved.is_file():
        raise Phase0Error("test command requires a file: " + raw)
    if require_directory and not resolved.is_dir():
        raise Phase0Error("test command requires a directory: " + raw)
    return resolved


def _split_manifest_command(command: str) -> tuple[Dict[str, str], List[List[str]]]:
    if not isinstance(command, str) or not command.strip():
        raise Phase0Error("required suite command is missing")
    if len(command) > 8192:
        raise Phase0Error("required suite command is too long")
    without_separators = command.replace(" && ", " ")
    if "&" in without_separators or any(
        fragment in command for fragment in FORBIDDEN_COMMAND_FRAGMENTS
    ):
        raise Phase0Error("shell metacharacters are forbidden in required suite commands")
    try:
        tokens = shlex.split(command, comments=False, posix=True)
    except ValueError as exc:
        raise Phase0Error("required suite command cannot be parsed: " + str(exc))
    if not tokens:
        raise Phase0Error("required suite command is empty")
    if any(token.startswith("#") for token in tokens):
        raise Phase0Error("shell comments are forbidden in required suite commands")

    environment: Dict[str, str] = {}
    while tokens and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", tokens[0]):
        key, value = tokens.pop(0).split("=", 1)
        if key not in ALLOWED_MANIFEST_ENVIRONMENT:
            raise Phase0Error("unknown manifest test environment variable: " + key)
        if key in environment:
            raise Phase0Error("duplicate manifest test environment variable: " + key)
        environment[key] = value

    segments: List[List[str]] = []
    current: List[str] = []
    for token in tokens:
        if token == "&&":
            if not current:
                raise Phase0Error("empty command segment is forbidden")
            segments.append(current)
            current = []
            continue
        if "&" in token:
            raise Phase0Error("background or shell execution is forbidden")
        current.append(token)
    if not current:
        raise Phase0Error("required suite command ends with an empty segment")
    segments.append(current)
    if len(segments) > 2:
        raise Phase0Error("required suite command may contain at most restore then test")
    return environment, segments


def _dotnet_target(
    root: Path, module_root: Path, arguments: Sequence[str], operation: str
) -> tuple[Path, List[str]]:
    values = list(arguments)
    if operation == "test" and values[:1] == ["--project"]:
        values = values[1:]
    if not values:
        raise Phase0Error("dotnet {0} target is missing".format(operation))
    target_raw = values.pop(0)
    target = _safe_existing_path(root, module_root, target_raw)
    if target.is_dir():
        projects = [
            path
            for path in target.rglob("*.csproj")
            if path.is_file() and not path.is_symlink() and "obj" not in path.parts
        ]
        if not projects:
            raise Phase0Error("dotnet test target contains no project: " + target_raw)
    elif target.suffix.casefold() != ".csproj":
        raise Phase0Error("dotnet target must be a project or project directory: " + target_raw)
    return target, values


def _parse_dotnet_sequence(
    root: Path,
    module_root: Path,
    segments: Sequence[Sequence[str]],
    test_type: str,
    expected_test_category: Optional[str] = None,
) -> Sequence[TrustedInvocation]:
    if len(segments) != 2:
        raise Phase0Error("dotnet required suite must contain locked restore then test")
    expected_executable = "scripts/dotnet-pinned.sh"
    restore = list(segments[0])
    test = list(segments[1])
    if restore[:2] != [expected_executable, "restore"] or test[:2] != [expected_executable, "test"]:
        raise Phase0Error("only the pinned dotnet restore/test sequence is allowed")
    wrapper = root / expected_executable
    if not wrapper.is_file() or wrapper.is_symlink():
        raise Phase0Error("trusted dotnet wrapper is missing or is a symlink")
    restore_target, restore_tail = _dotnet_target(root, module_root, restore[2:], "restore")
    if restore_tail != ["--locked-mode"]:
        raise Phase0Error("dotnet restore must use only --locked-mode")
    test_target, test_tail = _dotnet_target(root, module_root, test[2:], "test")
    if restore_target != test_target:
        raise Phase0Error("dotnet restore and test targets must be identical")
    if test_tail[:3] != ["--configuration", "Release", "--no-restore"]:
        raise Phase0Error("dotnet test must use Release and --no-restore")
    runner_tail = test_tail[3:]
    if runner_tail[:1] == ["--"]:
        runner_tail = runner_tail[1:]
    if len(runner_tail) != 6:
        raise Phase0Error("dotnet test runner arguments are not the trusted fixed shape")
    category = expected_test_category or test_type.title()
    if runner_tail[0] != "--filter-trait" or runner_tail[1] != "Category=" + category:
        raise Phase0Error("dotnet test must select the declared suite category")
    if runner_tail[2] != "--minimum-expected-tests" or not runner_tail[3].isdigit():
        raise Phase0Error("dotnet test must declare a numeric minimum test count")
    minimum_tests = int(runner_tail[3])
    if minimum_tests < 1:
        raise Phase0Error("dotnet minimum expected tests must be positive")
    if runner_tail[4:] != ["--fail-skips", "on"]:
        raise Phase0Error("dotnet test must fail skipped tests")
    shell = "/bin/bash"
    return (
        TrustedInvocation(
            [
                shell,
                str(wrapper),
                "restore",
                str(restore_target),
                *_trusted_restore_flags(root),
            ],
            "restore",
            0,
        ),
        TrustedInvocation([shell, str(wrapper), *test[1:]], "dotnet-test", minimum_tests),
    )


def _audit_dotnet_test_script(
    root: Path, module_root: Path, script: Path, selected_category: str
) -> tuple[str, int]:
    lines = script.read_text(encoding="utf-8-sig").splitlines()
    expected_common_prefix = [
        "#!/usr/bin/env bash",
        "set -euo pipefail",
        "",
        'if [[ "$#" -ne 1 ]]; then',
        '  echo "Usage: $0 <Unit|Contract|Integration>" >&2',
        "  exit 64",
        "fi",
        "",
        'case "$1" in',
    ]
    expected_legacy_tail = [
        "  Unit|Contract|Integration)",
        '    suite_category="$1"',
        "    ;;",
        "  *)",
        '    echo "Unknown suite category: $1" >&2',
        "    exit 64",
        "    ;;",
        "esac",
        "",
        'repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"',
        'cd "$repo_root"',
    ]
    category_floor = 1
    if lines[:9] != expected_common_prefix:
        raise Phase0Error("module operations/test.sh is outside the trusted fixed template")
    if len(lines) == 25 and lines[9:20] == expected_legacy_tail:
        restore_line = lines[20]
        test_lines = lines[21:25]
        expected_floor_token = "1"
    elif len(lines) == 34:
        floors: Dict[str, int] = {}
        for category, offset in (("Unit", 9), ("Contract", 13), ("Integration", 17)):
            if (
                lines[offset] != "  " + category + ")"
                or lines[offset + 1] != '    suite_category="$1"'
                or lines[offset + 3] != "    ;;"
            ):
                raise Phase0Error(
                    "module operations/test.sh is outside the trusted fixed template"
                )
            match = re.fullmatch(
                r"    minimum_expected_tests=([1-9][0-9]{0,5})",
                lines[offset + 2],
            )
            if match is None:
                raise Phase0Error(
                    "module operations/test.sh has an invalid category test floor"
                )
            floors[category] = int(match.group(1))
        expected_hardened_tail = [
            "  *)",
            '    echo "Unknown suite category: $1" >&2',
            "    exit 64",
            "    ;;",
            "esac",
            "",
            'repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"',
            'cd "$repo_root"',
        ]
        if lines[21:29] != expected_hardened_tail:
            raise Phase0Error(
                "module operations/test.sh is outside the trusted fixed template"
            )
        if selected_category not in floors:
            raise Phase0Error("module test script selected an unknown category")
        category_floor = floors[selected_category]
        restore_line = lines[29]
        test_lines = lines[30:34]
        expected_floor_token = "$minimum_expected_tests"
    else:
        raise Phase0Error("module operations/test.sh is outside the trusted fixed template")
    try:
        restore = shlex.split(restore_line, comments=False, posix=True)
        test_command = " ".join(
            line.rstrip()[:-1].rstrip()
            if line.rstrip().endswith("\\")
            else line.strip()
            for line in test_lines
        )
        test = shlex.split(test_command, comments=False, posix=True)
    except ValueError as exc:
        raise Phase0Error("module operations/test.sh cannot be parsed: " + str(exc))
    if restore[:3] != ["bash", "scripts/dotnet-pinned.sh", "restore"]:
        raise Phase0Error("module operations/test.sh must call the pinned restore wrapper")
    if test[:3] != ["bash", "scripts/dotnet-pinned.sh", "test"]:
        raise Phase0Error("module operations/test.sh must call the pinned test wrapper")
    restore_target, restore_tail = _dotnet_target(root, module_root, restore[3:], "restore")
    test_target, test_tail = _dotnet_target(root, module_root, test[3:], "test")
    if restore_target != test_target or restore_tail != ["--locked-mode"]:
        raise Phase0Error("module test script restore/test targets or lock mode differ")
    expected_test_tail = [
        "--configuration",
        "Release",
        "--no-restore",
        "--",
        "--filter-trait",
        "Category=$suite_category",
        "--minimum-expected-tests",
        expected_floor_token,
        "--fail-skips",
        "on",
    ]
    if test_tail != expected_test_tail:
        raise Phase0Error(
            "module test script must use the fixed category-isolated Release test arguments"
        )
    return restore[3], category_floor


def _parse_python_invocation(
    root: Path,
    module_root: Path,
    segment: Sequence[str],
    test_type: str,
) -> TrustedInvocation:
    python_prefix = [sys.executable, "-I"]
    values = list(segment)
    if not values or values.pop(0) not in PYTHON_NAMES:
        raise Phase0Error("only the pinned Phase0 Python interpreter is allowed")
    if values[:3] == ["-m", "unittest", "discover"]:
        if len(values) != 7 or values[3] != "-s" or values[5] != "-p":
            raise Phase0Error("unittest discovery must use the fixed -s/-p shape")
        test_directory = _safe_existing_path(
            root, module_root, values[4], require_directory=True
        )
        pattern = values[6]
        if (
            "/" in pattern
            or "\\" in pattern
            or re.fullmatch(r"test_[A-Za-z0-9_.\-*]+\.py", pattern) is None
        ):
            raise Phase0Error("unittest discovery pattern is unsafe")
        discovered = [
            path
            for path in test_directory.glob(pattern)
            if path.is_file() and not path.is_symlink()
        ]
        if not discovered:
            raise Phase0Error("unittest discovery currently matches zero test files")
        return TrustedInvocation(
            [*python_prefix, "-m", "unittest", "discover", "-s", str(test_directory), "-p", pattern],
            "python-unittest",
            1,
        )
    if values[:2] == ["-m", "unittest"]:
        if len(values) != 3:
            raise Phase0Error("direct unittest invocation must name exactly one test file")
        test_file = _safe_existing_path(root, module_root, values[2], require_file=True)
        if test_file.suffix.casefold() != ".py":
            raise Phase0Error("direct unittest target must be a Python test file")
        return TrustedInvocation(
            [
                *python_prefix,
                "-m",
                "unittest",
                "discover",
                "-s",
                str(test_file.parent),
                "-p",
                test_file.name,
            ],
            "python-unittest",
            1,
        )
    if values[:2] == ["-m", "json.tool"]:
        raise Phase0Error(
            "json.tool checks syntax only and is not semantic contract test evidence"
        )
    if test_type != "static" or not values:
        raise Phase0Error("direct Python scripts are allowed only for static suites")
    script_raw = values.pop(0)
    if script_raw.replace("\\", "/") == "Tools/ci/run_phase0_gate.py":
        raise Phase0Error("recursive Phase0 suite command is forbidden")
    script = _safe_existing_path(root, module_root, script_raw, require_file=True)
    if script.suffix.casefold() != ".py":
        raise Phase0Error("static Python suite target must be a .py file")
    if values not in ([], ["--root", "."]):
        raise Phase0Error("static Python suite arguments are not allowlisted")
    return TrustedInvocation(
        [sys.executable, str(script), *values], "static-json", 1
    )


def parse_manifest_suite_command(
    root: Path,
    module_root: Path,
    module_id: str,
    suite: Mapping[str, Any],
    allowed_test_types: Sequence[str] = ("static", "unit"),
    expected_evidence_level: str = VERIFICATION_LEVEL,
    expected_test_category: Optional[str] = None,
) -> TrustedSuitePlan:
    suite_id = suite.get("id")
    test_type = suite.get("type")
    evidence_level = suite.get("evidenceLevel")
    declared = suite.get("command")
    if not isinstance(suite_id, str) or not suite_id:
        raise Phase0Error("required suite id is missing")
    allowed = tuple(allowed_test_types)
    if test_type not in allowed:
        raise Phase0Error(
            "required suite type {0!r} is not one of: {1}".format(
                test_type, ", ".join(allowed)
            )
        )
    if evidence_level != expected_evidence_level:
        raise Phase0Error(
            "required suite evidenceLevel must be exactly {0}".format(
                expected_evidence_level
            )
        )
    environment, segments = _split_manifest_command(declared)

    resolved_environment: Dict[str, str] = {}
    for key, value in environment.items():
        if key == "PYTHONPATH":
            resolved_environment[key] = str(
                _safe_existing_path(root, module_root, value, require_directory=True)
            )

    executable = segments[0][0] if segments and segments[0] else ""
    if executable == "scripts/dotnet-pinned.sh":
        invocations = _parse_dotnet_sequence(
            root,
            module_root,
            segments,
            test_type,
            expected_test_category=expected_test_category,
        )
    elif executable in PYTHON_NAMES:
        if len(segments) != 1:
            raise Phase0Error("Python suites cannot use compound shell commands")
        invocations = (
            _parse_python_invocation(root, module_root, segments[0], test_type),
        )
    elif executable == "bash":
        if len(segments) != 1 or len(segments[0]) != 3:
            raise Phase0Error(
                "bash suite must name its audited operations/test.sh and exactly one category"
            )
        script = _safe_existing_path(
            root, module_root, segments[0][1], require_file=True
        )
        expected = module_root / "operations" / "test.sh"
        if script != expected.resolve() or script.is_symlink():
            raise Phase0Error("bash suite may only run its own operations/test.sh")
        category = segments[0][2]
        expected_category = expected_test_category or test_type.title()
        if category != expected_category or category not in {
            "Unit",
            "Contract",
            "Integration",
        }:
            raise Phase0Error(
                "bash suite category must exactly match the declared suite type"
            )
        test_target, script_minimum_tests = _audit_dotnet_test_script(
            root, module_root, script, category
        )
        wrapper = root / "scripts" / "dotnet-pinned.sh"
        if not wrapper.is_file() or wrapper.is_symlink():
            raise Phase0Error("trusted dotnet wrapper is missing or is a symlink")
        # The manifest may retain a conservative operations/test.sh entry, but
        # Phase0 does not execute that shell file.  It extracts the audited
        # project target and runs the hardened argv directly so the declared
        # category cannot be satisfied by a different remaining category.
        invocations = (
            TrustedInvocation(
                [
                    "/bin/bash",
                    str(wrapper),
                    "restore",
                    str(test_target),
                    *_trusted_restore_flags(root),
                ],
                "restore",
                0,
            ),
            TrustedInvocation(
                [
                    "/bin/bash",
                    str(wrapper),
                    "test",
                    test_target,
                    "--configuration",
                    "Release",
                    "--no-restore",
                    "--",
                    "--filter-trait",
                    "Category=" + expected_category,
                    "--minimum-expected-tests",
                    str(script_minimum_tests),
                    "--fail-skips",
                    "on",
                ],
                "dotnet-test",
                script_minimum_tests,
            ),
        )
    else:
        raise Phase0Error("unknown or untrusted required suite executable: " + executable)

    return TrustedSuitePlan(
        module_id=module_id,
        suite_id=suite_id,
        test_type=test_type,
        evidence_level=evidence_level,
        declared_command=declared,
        environment=resolved_environment,
        invocations=invocations,
    )


@functools.lru_cache(maxsize=1)
def _trusted_dotnet_executable() -> Optional[Path]:
    probe_environment = {
        "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
    }
    for candidate in _trusted_dotnet_candidates():
        try:
            resolved = candidate.resolve(strict=True)
        except (OSError, RuntimeError):
            continue
        if not resolved.is_file() or not os.access(str(resolved), os.X_OK):
            continue
        probe = run_command(
            [str(resolved), "--version"],
            ROOT,
            timeout_seconds=30,
            env=probe_environment,
        )
        if probe.exit_code == 0 and probe.output.strip() == REQUIRED_DOTNET_SDK:
            return resolved
    return None


def _trusted_account_home() -> Optional[Path]:
    """Resolve the OS account home without consulting injectable env values."""

    if os.name != "posix":
        return None
    try:
        import pwd

        value = pwd.getpwuid(os.getuid()).pw_dir
        home = Path(value).resolve(strict=True)
    except (ImportError, KeyError, OSError, RuntimeError):
        return None
    return home if home.is_dir() else None


def _trusted_dotnet_candidates() -> Sequence[Path]:
    """Return fixed candidates; never select the .NET host from ambient PATH/HOME."""

    candidates: List[Path] = []
    home = _trusted_account_home()
    if home is not None:
        candidates.append(home / ".dotnet" / "dotnet")
    candidates.extend(
        [
            Path("/usr/local/share/dotnet/dotnet"),
            Path("/usr/share/dotnet/dotnet"),
            Path("/opt/homebrew/share/dotnet/dotnet"),
            Path("/opt/hostedtoolcache/dotnet")
            / REQUIRED_DOTNET_SDK
            / "x64"
            / "dotnet",
            Path("/opt/hostedtoolcache/dotnet")
            / REQUIRED_DOTNET_SDK
            / "arm64"
            / "dotnet",
        ]
    )
    return tuple(candidates)


def _locked_node_version() -> str:
    lock_path = ROOT / "toolchain.lock.json"
    if not lock_path.is_file() or lock_path.is_symlink():
        raise Phase0Error("toolchain.lock.json is missing or unsafe")
    try:
        payload = json.loads(lock_path.read_text(encoding="utf-8"))
        version = payload["node"]["version"]
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError) as exc:
        raise Phase0Error("toolchain.lock.json has no valid Node pin") from exc
    if not isinstance(version, str) or re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", version) is None:
        raise Phase0Error("toolchain.lock.json has no valid Node pin")
    locked_version = "v" + version
    if locked_version != REQUIRED_NODE_VERSION:
        raise Phase0Error(
            "toolchain.lock.json Node pin disagrees with the Phase 0 policy"
        )
    return locked_version


def _trusted_node_candidates(locked_version: str) -> Sequence[Path]:
    """Return auditable candidates; never derive an executable from PATH."""

    version = locked_version.removeprefix("v")
    candidates: List[Path] = []
    home = _trusted_account_home()
    if home is not None:
        candidates.extend(
            [
                home / ".local" / "bin" / "node",
                home / ".nvm" / "versions" / "node" / ("v" + version) / "bin" / "node",
                home
                / ".cache"
                / "codex-runtimes"
                / "codex-primary-runtime"
                / "dependencies"
                / "node"
                / "bin"
                / "node",
            ]
        )
    candidates.extend(
        [
            Path("/Applications/ChatGPT.app/Contents/Resources/cua_node/bin/node"),
            Path("/opt/hostedtoolcache/node") / version / "x64" / "bin" / "node",
            Path("/opt/hostedtoolcache/node") / version / "arm64" / "bin" / "node",
        ]
    )
    return tuple(candidates)


@functools.lru_cache(maxsize=1)
def _trusted_node_executable() -> Optional[Path]:
    locked_version = _locked_node_version()
    probe_environment = {
        "PATH": "/usr/bin:/bin:/usr/sbin:/sbin",
        "PYTHONDONTWRITEBYTECODE": "1",
    }
    for candidate in _trusted_node_candidates(locked_version):
        try:
            resolved = candidate.resolve(strict=True)
        except (OSError, RuntimeError):
            continue
        if not resolved.is_file() or not os.access(str(resolved), os.X_OK):
            continue
        probe = run_command(
            [str(resolved), "--version"],
            ROOT,
            timeout_seconds=30,
            env=probe_environment,
        )
        if probe.exit_code == 0 and probe.output.strip() == locked_version:
            return resolved
    return None


def _trusted_restore_flags(root: Path) -> Sequence[str]:
    config = root / "NuGet.Config"
    if not config.is_file() or config.is_symlink():
        raise Phase0Error("trusted NuGet.Config is missing or unsafe")
    return (
        "--locked-mode",
        "--configfile",
        str(config.resolve()),
        "-p:RestoreUseStaticGraphEvaluation=true",
        "-p:NuGetAudit=true",
        "-p:NuGetAuditMode=all",
        "-p:TreatWarningsAsErrors=true",
        "-p:MSBuildEnableWorkloadResolver=false",
    )


def _restore_failure_is_infrastructure(exit_code: int, output: str) -> bool:
    if exit_code in (124, 127):
        return True
    normalized = output.casefold()
    infrastructure_markers = (
        "cssm_moduleload()",
        "unable to load the service index",
        "temporary failure in name resolution",
        "name or service not known",
        "network is unreachable",
        "connection refused",
        "no such host is known",
        "the ssl connection could not be established",
        "no space left on device",
    )
    return any(marker in normalized for marker in infrastructure_markers)


def _trusted_state_parent() -> Path:
    candidates = [Path("/private/tmp"), Path("/tmp")]
    if os.name != "posix":
        candidates = [Path(tempfile.gettempdir())]
    for candidate in candidates:
        try:
            if candidate.is_symlink():
                continue
            resolved = candidate.resolve(strict=True)
        except (OSError, RuntimeError):
            continue
        if resolved.is_dir():
            return resolved
    raise Phase0Error("no trusted temporary-state parent is available")


def _trusted_test_environment(
    extra: Mapping[str, str], state_root: Path
) -> Dict[str, str]:
    try:
        resolved_state = state_root.resolve(strict=True)
        state_stat = state_root.stat(follow_symlinks=False)
    except (OSError, RuntimeError) as exc:
        raise Phase0Error("trusted test state root is unavailable") from exc
    if state_root.is_symlink() or not resolved_state.is_dir():
        raise Phase0Error("trusted test state root is unsafe")
    if os.name == "posix" and (
        state_stat.st_uid != os.getuid() or state_stat.st_mode & 0o077
    ):
        raise Phase0Error("trusted test state root ownership or permissions are unsafe")

    path_entries = [str(Path(sys.executable).resolve().parent), "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
    dotnet_executable = _trusted_dotnet_executable()
    if dotnet_executable is not None:
        path_entries.insert(0, str(dotnet_executable.parent))
    node_executable = _trusted_node_executable()
    if node_executable is None:
        raise Phase0Error(
            "trusted Node {0} executable is unavailable at an approved fixed path".format(
                _locked_node_version().removeprefix("v")
            )
        )
    path_entries.insert(0, str(node_executable.parent))
    environment: Dict[str, str] = {
        "PATH": os.pathsep.join(dict.fromkeys(path_entries)),
        "PYTHONDONTWRITEBYTECODE": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH": "0",
        "DOTNET_GENERATE_ASPNET_CERTIFICATE": "false",
        "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE": "true",
        "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER": "1",
        "DOTNET_CLI_UI_LANGUAGE": "en-US",
        "DOTNET_NOLOGO": "1",
        "MSBUILDDISABLENODEREUSE": "1",
        "MSBUILDUSESERVER": "0",
        "MSBuildEnableWorkloadResolver": "false",
        "TESTINGPLATFORM_TELEMETRY_OPTOUT": "1",
        "DPS_NODE": str(node_executable),
        "HOME": str(resolved_state / "home"),
        "DOTNET_CLI_HOME": str(resolved_state / "dotnet-home"),
        "NUGET_HTTP_CACHE_PATH": str(resolved_state / "nuget-http-cache"),
        "NUGET_SCRATCH": str(resolved_state / "nuget-scratch"),
        "TMPDIR": str(resolved_state / "tmp"),
        "LANG": "C",
        "LC_ALL": "C",
    }
    account_home = _trusted_account_home()
    if account_home is not None:
        package_cache = account_home / ".nuget" / "packages"
        if package_cache.is_symlink():
            raise Phase0Error("trusted NuGet package cache cannot be a symlink")
        environment["NUGET_PACKAGES"] = str(package_cache)
    if dotnet_executable is not None:
        environment["DPS_DOTNET"] = str(dotnet_executable)
    if sys.platform == "darwin":
        environment["DOTNET_NUGET_SIGNATURE_VERIFICATION"] = "false"
    for sandbox_key in ("CODEX_SANDBOX", "CODEX_SANDBOX_NETWORK_DISABLED"):
        sandbox_value = os.environ.get(sandbox_key)
        if sandbox_value:
            environment[sandbox_key] = sandbox_value
    unknown = sorted(set(extra).difference(ALLOWED_TRUSTED_EXECUTOR_ENVIRONMENT))
    if unknown:
        raise Phase0Error(
            "unknown trusted test environment override: " + ", ".join(unknown)
        )
    environment.update(extra)
    return environment


@contextlib.contextmanager
def _trusted_test_environment_scope(
    extra: Mapping[str, str],
):
    state_root = Path(
        tempfile.mkdtemp(prefix="dps-phase0-executor-", dir=str(_trusted_state_parent()))
    )
    try:
        os.chmod(state_root, 0o700)
        for child in (
            "home",
            "dotnet-home",
            "nuget-http-cache",
            "nuget-scratch",
            "tmp",
        ):
            path = state_root / child
            path.mkdir(mode=0o700)
        yield _trusted_test_environment(extra, state_root)
    finally:
        try:
            if state_root.is_symlink():
                state_root.unlink(missing_ok=True)
            else:
                shutil.rmtree(state_root, ignore_errors=True)
        except OSError:
            pass


def run_locked_solution_build(root: Path, timeout_seconds: int = 600) -> Dict[str, Any]:
    started = time.monotonic()
    solution = root / "Dps.slnx"
    wrapper = root / "scripts" / "dotnet-pinned.sh"
    details: Dict[str, Any] = {
        "module_id": "evidence-service",
        "test_id": "solution-locked-restore-build",
        "test_type": "static",
        "evidence_level": VERIFICATION_LEVEL,
    }
    if not solution.is_file() or solution.is_symlink():
        return new_check(
            "solution-locked-restore-build",
            True,
            "FAIL",
            None,
            1,
            0,
            "ERROR: Dps.slnx is missing or unsafe",
            details,
        )
    if not wrapper.is_file() or wrapper.is_symlink():
        return new_check(
            "solution-locked-restore-build",
            True,
            "INFRA_ERROR",
            None,
            127,
            0,
            "ERROR: scripts/dotnet-pinned.sh is missing or unsafe",
            details,
        )
    try:
        import xml.etree.ElementTree as element_tree

        document = element_tree.parse(str(solution))
        declared = {
            element.get("Path")
            for element in document.findall(".//Project")
            if isinstance(element.get("Path"), str)
        }
    except Exception as exc:
        return new_check(
            "solution-locked-restore-build",
            True,
            "FAIL",
            None,
            1,
            0,
            "ERROR: Dps.slnx cannot be parsed: " + str(exc),
            details,
        )
    actual = {
        path.relative_to(root).as_posix()
        for module_root in discover_registered_module_dirs(root)
        for path in module_root.rglob("*.csproj")
        if path.is_file()
        and not path.is_symlink()
        and "bin" not in path.parts
        and "obj" not in path.parts
    }
    missing = sorted(actual.difference(declared))
    unknown = sorted(declared.difference(actual))
    if missing or unknown:
        message = []
        if missing:
            message.append("projects omitted from solution: " + ", ".join(missing))
        if unknown:
            message.append("solution declares missing/unknown projects: " + ", ".join(unknown))
        return new_check(
            "solution-locked-restore-build",
            True,
            "FAIL",
            None,
            1,
            0,
            "ERROR: " + "; ".join(message),
            details,
        )
    commands = (
        [
            "/bin/bash",
            str(wrapper),
            "restore",
            "Dps.slnx",
            *_trusted_restore_flags(root),
            "--disable-parallel",
        ],
        [
            "/bin/bash",
            str(wrapper),
            "build",
            "Dps.slnx",
            "--configuration",
            "Release",
            "--no-restore",
            "--maxcpucount:1",
        ],
    )
    output: List[str] = []
    effective: List[str] = []
    exit_code = 0
    failed_restore = False
    with _trusted_test_environment_scope({}) as environment:
        for index, command in enumerate(commands):
            effective.extend((["&&"] if effective else []) + command)
            result = run_command(command, root, timeout_seconds=timeout_seconds, env=environment)
            output.append(
                "--- locked solution segment {0} ---\n{1}".format(index + 1, result.output)
            )
            exit_code = result.exit_code
            failed_restore = index == 0 and exit_code != 0
            if exit_code != 0:
                break
    status = "PASS"
    if failed_restore and _restore_failure_is_infrastructure(exit_code, "\n".join(output)):
        status = "INFRA_ERROR"
    elif exit_code in (124, 127):
        status = "INFRA_ERROR"
    elif exit_code != 0:
        status = "FAIL"
    details.update(
        {
            "solution": "Dps.slnx",
            "solution_sha256": sha256_file(solution),
            "project_count": len(actual),
        }
    )
    return new_check(
        "solution-locked-restore-build",
        True,
        status,
        effective,
        exit_code,
        int((time.monotonic() - started) * 1000),
        "\n".join(output),
        details,
    )


def _forbidden_output_reason(output: str) -> Optional[str]:
    for match in re.finditer(
        r"(?im)(?:^|[\[{,]\s*)[\"']?(?:status|outcome)[\"']?\s*[:=]\s*[\"']?(SKIP|PARTIAL|NOT_RUN|INFRA_ERROR|NOT_APPLICABLE|FAIL)",
        output,
    ):
        return "required test reported forbidden outcome " + match.group(1).upper()
    for match in re.finditer(r"(?im)^\s*(?:skipped|skip)\s*[:=]\s*([0-9]+)\b", output):
        if int(match.group(1)) > 0:
            return "required test reported skipped tests"
    for match in re.finditer(r"(?i)\bskipped\s*=\s*([0-9]+)\b", output):
        if int(match.group(1)) > 0:
            return "required test reported skipped tests"
    for label, pattern in (
        ("expected failures", r"(?i)\bexpected\s+failures\s*=\s*([0-9]+)\b"),
        ("unexpected successes", r"(?i)\bunexpected\s+successes\s*=\s*([0-9]+)\b"),
    ):
        for match in re.finditer(pattern, output):
            if int(match.group(1)) > 0:
                return "required test reported " + label
    for match in re.finditer(r"(?im)^\s*failed\s*[:=]\s*([0-9]+)\b", output):
        if int(match.group(1)) > 0:
            return "required test reported failures despite exit zero"
    if re.search(r"(?im)^\s*(?:\[(?:SKIP|PARTIAL|NOT_RUN)\]|SKIP|PARTIAL|NOT_RUN)\s*$", output):
        return "required test emitted a non-PASS outcome"
    return None


def enforce_unittest_evidence(
    check: Dict[str, Any],
    result: CommandResult,
    executed: int,
    minimum_tests: int,
    missing_inventory: Sequence[str],
    label: str,
) -> Dict[str, Any]:
    check["details"]["executed_tests"] = executed
    check["details"]["minimum_tests"] = minimum_tests
    check["details"]["missing_inventory"] = list(missing_inventory)
    problems: List[str] = []
    if executed < minimum_tests:
        problems.append(
            "required at least {0}, ran {1}".format(minimum_tests, executed)
        )
    if missing_inventory:
        problems.append("missing inventory: " + ", ".join(missing_inventory))
    forbidden = _forbidden_output_reason(result.output)
    if forbidden:
        problems.append(forbidden)
    if result.exit_code == 0 and problems:
        check["status"] = "FAIL"
        check["exit_code"] = 1
        check["log"] += "ERROR: {0} are not eligible evidence: {1}\n".format(
            label, "; ".join(problems)
        )
        check["log_sha256"] = sha256_text(check["log"])
    return check


def _executed_test_count(invocation: TrustedInvocation, output: str) -> tuple[int, Optional[str]]:
    forbidden = _forbidden_output_reason(output)
    if forbidden:
        return 0, forbidden
    if invocation.kind == "python-unittest":
        matches = re.findall(r"(?m)^Ran ([0-9]+) tests?\b", output)
        if len(matches) != 1:
            return 0, "unittest must emit exactly one canonical Ran summary"
        canonical_tail = re.search(
            r"(?ms)(?:^|\r?\n)Ran [0-9]+ tests? in [^\r\n]+\r?\n\r?\nOK(?: \([^\r\n]+\))?\s*\Z",
            output,
        )
        if canonical_tail is None:
            return 0, "unittest output does not end with one canonical success summary"
        count = int(matches[0])
        return count, None if count >= invocation.minimum_tests else "unittest executed zero or fewer than the required tests"
    if invocation.kind == "dotnet-test":
        matches = re.findall(r"(?im)^\s*total:\s*([0-9]+)\s*$", output)
        if len(matches) != 1:
            return 0, "dotnet test must emit exactly one total summary"
        count = int(matches[0])
        return count, None if count >= invocation.minimum_tests else "dotnet test executed zero or fewer than the required tests"
    if invocation.kind == "static-json":
        try:
            payload = json.loads(output.strip())
        except (TypeError, json.JSONDecodeError):
            return 0, "static suite did not emit one structured JSON result"
        if not isinstance(payload, Mapping):
            return 0, "static suite result must be a JSON object"
        status_shape = (
            payload.get("status") == "PASS"
            and payload.get("test_type") == "static"
            and payload.get("verification_level") == VERIFICATION_LEVEL
        )
        baseline_shape = (
            payload.get("ok") is True
            and isinstance(payload.get("schema_version"), str)
            and str(payload.get("schema_version")).startswith("dps.")
            and "STATIC" in str(payload.get("scope", "")).upper()
            and payload.get("errors") == []
            and any(
                isinstance(value, int) and not isinstance(value, bool) and value > 0
                for key, value in payload.items()
                if key.endswith("_count")
            )
        )
        if not (status_shape or baseline_shape):
            return 0, "static suite did not emit an eligible structured PASS result"
        return 1, None
    return 0, "trusted suite plan has no executable test phase"


def execute_manifest_suite(root: Path, plan: TrustedSuitePlan, timeout_seconds: int = 300) -> Dict[str, Any]:
    started = time.monotonic()
    combined_output: List[str] = []
    effective: List[str] = []
    final_exit = 0
    final_kind: Optional[TrustedInvocation] = None
    final_test_output = ""
    failed_restore = False
    with _trusted_test_environment_scope(plan.environment) as environment:
        for index, invocation in enumerate(plan.invocations):
            effective.extend((["&&"] if effective else []) + list(invocation.argv))
            result = run_command(
                invocation.argv,
                root,
                timeout_seconds=timeout_seconds,
                env=environment,
            )
            combined_output.append(
                "--- trusted segment {0}: {1} ---\n{2}".format(
                    index + 1, invocation.kind, result.output
                )
            )
            final_exit = result.exit_code
            failed_restore = invocation.kind == "restore" and result.exit_code != 0
            if result.exit_code != 0:
                break
            if invocation.kind != "restore":
                final_kind = invocation
                final_test_output = result.output
    output = "\n".join(combined_output)
    details: Dict[str, Any] = {
        "module_id": plan.module_id,
        "test_id": plan.suite_id,
        "test_type": plan.test_type,
        "evidence_level": plan.evidence_level,
        "declared_command": plan.declared_command,
        "declared_command_sha256": sha256_text(plan.declared_command),
        "effective_command": effective,
        "executed_tests": 0,
    }
    status = "PASS"
    if failed_restore and _restore_failure_is_infrastructure(final_exit, output):
        status = "INFRA_ERROR"
    elif final_exit == 124 or final_exit == 127:
        status = "INFRA_ERROR"
    elif final_exit != 0:
        status = "FAIL"
    elif final_kind is None:
        status = "FAIL"
        final_exit = 1
        output += "\nERROR: no actual test phase was executed\n"
        details["executed_tests"] = 0
    else:
        count, reason = _executed_test_count(final_kind, final_test_output)
        details["executed_tests"] = count
        details["minimum_tests"] = final_kind.minimum_tests
        if reason:
            status = "FAIL"
            final_exit = 1
            output += "\nERROR: " + reason + "\n"
    return new_check(
        "manifest:{0}:{1}".format(plan.module_id, plan.suite_id),
        True,
        status,
        effective,
        final_exit,
        int((time.monotonic() - started) * 1000),
        output,
        details,
    )


def _module_test_failure(module_id: str, suffix: str, message: str) -> Dict[str, Any]:
    return new_check(
        "manifest:{0}:{1}".format(module_id, suffix),
        True,
        "FAIL",
        None,
        1,
        0,
        "ERROR: " + message,
        {
            "module_id": module_id,
            "test_id": suffix,
            "test_type": "static",
            "evidence_level": VERIFICATION_LEVEL,
            "executed_tests": 0,
        },
    )


def run_required_module_static_tests(root: Path, timeout_seconds: int = 300) -> List[Dict[str, Any]]:
    checks: List[Dict[str, Any]] = []
    try:
        module_roots = discover_registered_module_dirs(root)
    except Phase0Error as exc:
        return [_module_test_failure("evidence-service", "module-discovery", str(exc))]
    for module_root in module_roots:
        module_id = module_root.name
        manifest_path = module_root / "module.yaml"
        if not manifest_path.is_file() or manifest_path.is_symlink():
            checks.append(
                _module_test_failure(module_id, "required-static", "module manifest is missing or unsafe")
            )
            continue
        try:
            manifest = load_json_compatible_yaml(manifest_path)
        except Phase0Error as exc:
            checks.append(_module_test_failure(module_id, "required-static", str(exc)))
            continue
        declared_id = manifest_module_id(manifest)
        if declared_id != module_id:
            checks.append(
                _module_test_failure(module_id, "required-static", "manifest module id does not match its directory")
            )
            continue
        tests = manifest.get("tests")
        suites = tests.get("suites") if isinstance(tests, Mapping) else None
        if not isinstance(suites, list):
            checks.append(
                _module_test_failure(module_id, "required-static", "tests.suites is missing")
            )
            continue
        suite_ids: set[str] = set()
        duplicates: set[str] = set()
        for suite in suites:
            if isinstance(suite, Mapping) and isinstance(suite.get("id"), str):
                if suite["id"] in suite_ids:
                    duplicates.add(suite["id"])
                suite_ids.add(suite["id"])
        if duplicates:
            checks.append(
                _module_test_failure(
                    module_id,
                    "required-static",
                    "duplicate suite ids: " + ", ".join(sorted(duplicates)),
                )
            )
            continue
        required = [
            suite
            for suite in suites
            if isinstance(suite, Mapping)
            and suite.get("required") is True
            and suite.get("evidenceLevel") == VERIFICATION_LEVEL
        ]
        if not required:
            checks.append(
                _module_test_failure(
                    module_id,
                    "required-static",
                    "module declares no required REPOSITORY_STATIC_VERIFIED suite",
                )
            )
            continue
        for suite in required:
            suite_id = str(suite.get("id", "invalid-suite"))
            try:
                plan = parse_manifest_suite_command(root, module_root, module_id, suite)
            except Phase0Error as exc:
                checks.append(_module_test_failure(module_id, suite_id, str(exc)))
                continue
            check = execute_manifest_suite(root, plan, timeout_seconds=timeout_seconds)
            check["details"]["manifest"] = str(manifest_path.relative_to(root))
            check["details"]["manifest_sha256"] = sha256_file(manifest_path)
            checks.append(check)
    return checks


def load_or_issue_receipt(
    baseline: str, receipt_path: Optional[str]
) -> Dict[str, Any]:
    if receipt_path:
        path = Path(receipt_path)
        if not path.is_absolute():
            path = ROOT / path
        try:
            value, _ = _load_committed_json_object_with_sha(
                path, "instruction receipt"
            )
        except Exception as exc:
            raise Phase0Error("cannot read instruction receipt: {0}".format(exc))
        if value.get("baseline_commit") != baseline:
            raise Phase0Error(
                "instruction receipt baseline does not match the authorized gate baseline"
            )
        valid, message, current = validate_instruction_receipt(ROOT, value)
        if not valid:
            raise Phase0Error(message)
        return current

    receipt = resolve_instruction_receipt(ROOT, baseline)
    valid, message, current = validate_instruction_receipt(ROOT, receipt)
    if not valid:
        raise Phase0Error(message)
    return current


def build_test_evidence_records(
    checks: Sequence[Mapping[str, Any]],
    baseline: str,
    receipt_id: str,
    environment: Mapping[str, Any],
    started_at: str,
    finished_at: str,
) -> List[Dict[str, Any]]:
    scalar_environment = {
        key: value
        for key, value in environment.items()
        if value is None or isinstance(value, (str, int, float, bool))
    }
    records: List[Dict[str, Any]] = []
    for check in checks:
        check_id = str(check["id"])
        details = check.get("details")
        details = details if isinstance(details, Mapping) else {}
        command = check.get("command")
        rendered_command = (
            " ".join(str(value) for value in command)
            if isinstance(command, list) and command
            else "internal:" + check_id
        )
        declared_type = details.get("test_type")
        test_type = (
            str(declared_type)
            if declared_type
            in ("static", "unit", "contract", "integration", "windows", "device", "canary", "scale")
            else "unit" if "unit-test" in check_id else "static"
        )
        module_id = details.get("module_id")
        if not isinstance(module_id, str) or re.fullmatch(
            r"[a-z0-9]+(?:-[a-z0-9]+)*", module_id
        ) is None:
            module_id = "evidence-service"
        status = str(check["status"])
        records.append(
            {
                "schema_version": "dps.phase0-test-evidence/v1",
                "evidence_id": "phase0:{0}:{1}".format(
                    sha256_text(check_id + baseline)[:16], check_id
                ),
                "test_id": check_id,
                "module_id": module_id,
                "test_type": test_type,
                "required": check.get("required") is True,
                "status": status,
                "verification_level": VERIFICATION_LEVEL,
                "baseline_commit": baseline,
                "instruction_receipt_id": receipt_id,
                "runner_identity": "dps-phase0-gate",
                "command": rendered_command,
                "started_at": started_at,
                "finished_at": finished_at,
                "exit_code": check.get("exit_code"),
                "environment": scalar_environment,
                "artifacts": [
                    {
                        "path": "embedded:checks/{0}/log".format(check_id),
                        "sha256": check["log_sha256"],
                        "media_type": "text/plain",
                    }
                ],
                "reason": None if status == "PASS" else "required check did not pass",
            }
        )
    return records


def validate_test_evidence_records(records: Sequence[Mapping[str, Any]]) -> Dict[str, Any]:
    schema_path = (
        ROOT / "governance" / "schemas" / "phase0-test-evidence.schema.json"
    )
    if not schema_path.is_file():
        raise Phase0Error("test evidence JSON Schema is required")
    try:
        schema = json.loads(schema_path.read_text(encoding="utf-8-sig"))
    except Exception as exc:
        raise Phase0Error("invalid test evidence JSON Schema: {0}".format(exc))
    errors: List[str] = []
    for index, record in enumerate(records):
        errors.extend(
            "record[{0}] {1}".format(index, error)
            for error in validate_json_schema(record, schema)
        )
    if errors:
        raise Phase0Error("test evidence violates schema: " + "; ".join(errors))
    return {"record_count": len(records), "schema": str(schema_path.relative_to(ROOT))}


def _run_phase0_gate(
    arguments: argparse.Namespace,
    evidence_path: Path,
    receipt_path: Optional[Path],
    evidence_publication: EvidencePublication,
    receipt_publication: Optional[EvidencePublication],
) -> int:
    gate_started_at = dt.datetime.now(dt.timezone.utc).isoformat()
    checks: List[Dict[str, Any]] = []
    baseline: Optional[str] = None
    receipt: Dict[str, Any] = {}
    environment: Dict[str, Any] = {}
    workspace_check = workspace_cleanliness_check(
        ROOT, bool(arguments.diagnostic_workspace)
    )
    checks.append(workspace_check)
    formal_evidence_eligible = (
        workspace_check.get("details", {}).get("formal_evidence_eligible") is True
    )

    try:
        baseline = resolve_baseline(arguments.base)
    except Phase0Error as exc:
        checks.append(
            new_check(
                "baseline-resolution",
                True,
                "INFRA_ERROR",
                None,
                1,
                0,
                "ERROR: " + str(exc),
            )
        )

    environment, toolchain_errors = toolchain_details(ROOT, arguments.node)
    environment["workspace_clean"] = (
        workspace_check.get("details", {}).get("clean") is True
    )
    environment["evidence_mode"] = (
        "WORKSPACE_DIAGNOSTIC_ONLY"
        if arguments.diagnostic_workspace
        else VERIFICATION_LEVEL
    )
    checks.append(
        new_check(
            "pinned-toolchain",
            True,
            "PASS" if not toolchain_errors else "INFRA_ERROR",
            None,
            0 if not toolchain_errors else 1,
            0,
            "toolchain accepted"
            if not toolchain_errors
            else "\n".join("ERROR: " + value for value in toolchain_errors),
            environment,
        )
    )

    validator_result = run_command(
        [sys.executable, "Tools/ci/validate_repo.py"], ROOT, timeout_seconds=180
    )
    checks.append(check_from_command("repository-validator", True, validator_result))
    shell_result = run_command(
        ["/bin/bash", "-n", "scripts/release.sh"], ROOT, timeout_seconds=30
    )
    checks.append(check_from_command("release-shell-syntax", True, shell_result))

    checks.append(
        in_process_check(
            "module-governance",
            lambda: validate_governance(ROOT, require_schema=True),
        )
    )
    checks.append(
        in_process_check("ci-fail-closed-policy", lambda: validate_ci_integrity(ROOT))
    )

    if baseline is not None:
        instruction_check = in_process_check(
            "instruction-resolution-and-staleness",
            lambda: load_or_issue_receipt(baseline, arguments.receipt_in),
        )
        checks.append(instruction_check)
        if instruction_check["status"] == "PASS":
            receipt = dict(instruction_check["details"])
    else:
        checks.append(
            new_check(
                "instruction-resolution-and-staleness",
                True,
                "NOT_RUN",
                None,
                None,
                0,
                "baseline resolution failed",
            )
        )

    checks.append(run_phase0_unittests())
    checks.append(run_external_gate_unittests())
    checks.append(run_locked_solution_build(ROOT))
    checks.extend(run_required_module_static_tests(ROOT))

    node_executable = environment.get("node_executable")
    if isinstance(node_executable, str):
        playwright_report = ROOT / "Reports" / "ci" / "playwright-config.json"
        playwright_result = run_command(
            [
                node_executable,
                "Tests/playwright_dps_test.js",
                "--mode",
                "config",
                "--report",
                str(playwright_report),
            ],
            ROOT,
            timeout_seconds=120,
        )
        checks.append(
            check_from_command("playwright-static-config", True, playwright_result)
        )
    else:
        checks.append(
            new_check(
                "playwright-static-config",
                True,
                "INFRA_ERROR",
                None,
                127,
                0,
                "Node 24 executable is unavailable",
            )
        )

    if receipt:
        checks.append(
            in_process_check(
                "instruction-receipt-final-staleness",
                lambda: (
                    {"receipt_id": receipt["receipt_id"]}
                    if validate_instruction_receipt(ROOT, receipt)[0]
                    else (_ for _ in ()).throw(
                        Phase0Error(validate_instruction_receipt(ROOT, receipt)[1])
                    )
                ),
            )
        )
    else:
        checks.append(
            new_check(
                "instruction-receipt-final-staleness",
                True,
                "NOT_RUN",
                None,
                None,
                0,
                "instruction receipt is unavailable",
            )
        )

    try:
        head_commit = git_output(ROOT, ["rev-parse", "HEAD"])
    except Phase0Error:
        head_commit = None
    digest = None
    if baseline is not None:
        try:
            digest = workspace_digest(ROOT, baseline)
        except Phase0Error:
            digest = None
    environment["head_commit_observed"] = head_commit

    pre_schema_finished_at = dt.datetime.now(dt.timezone.utc).isoformat()
    if (
        formal_evidence_eligible
        and baseline is not None
        and receipt.get("receipt_id")
    ):
        preview_records = build_test_evidence_records(
            checks,
            baseline,
            str(receipt["receipt_id"]),
            environment,
            gate_started_at,
            pre_schema_finished_at,
        )
        checks.append(
            in_process_check(
                "test-evidence-schema",
                lambda: validate_test_evidence_records(preview_records),
            )
        )
    elif not formal_evidence_eligible:
        checks.append(
            new_check(
                "test-evidence-schema",
                True,
                "PASS",
                None,
                0,
                0,
                "formal per-test evidence suppressed for a non-releasable workspace",
                {
                    "record_count": 0,
                    "formal_evidence_eligible": False,
                },
            )
        )
    else:
        checks.append(
            new_check(
                "test-evidence-schema",
                True,
                "NOT_RUN",
                None,
                None,
                0,
                "instruction receipt is unavailable",
            )
        )

    overall_status, summary = evaluate_checks(checks)
    finished_at = dt.datetime.now(dt.timezone.utc).isoformat()
    test_evidence = (
        build_test_evidence_records(
            checks,
            baseline,
            str(receipt["receipt_id"]),
            environment,
            gate_started_at,
            finished_at,
        )
        if (
            formal_evidence_eligible
            and baseline is not None
            and receipt.get("receipt_id")
        )
        else []
    )
    gate_name, verification_level = evidence_classification(
        overall_status,
        formal_evidence_eligible,
        bool(arguments.diagnostic_workspace),
    )

    evidence: Dict[str, Any] = {
        "schema_version": "dps.phase0-evidence-bundle/v1",
        "gate": gate_name,
        "verification_level": verification_level,
        "overall_status": overall_status,
        "commit_sha": head_commit if formal_evidence_eligible else None,
        "head_commit_observed": head_commit,
        "baseline_commit": baseline,
        "workspace_sha256": digest,
        "started_at": gate_started_at,
        "finished_at": finished_at,
        "environment": environment,
        "instruction_receipt": receipt or None,
        "checks": checks,
        "test_evidence": test_evidence,
        "summary": summary,
        "limitations": [
            "No Windows, ZennoDroid, ADB, GBrain, or real-device verification is claimed.",
            "Hosted CI cannot issue WINDOWS_VERIFIED or DEVICE_VERIFIED evidence.",
            (
                "COMMITTED publication markers detect concurrent, torn, or mismatched "
                "local writes, but do not replace separate OS identities for an "
                "untrusted runner and evidence issuer."
            ),
            (
                "This is non-releasable workspace diagnostic output; no formal "
                "verification level or commit attribution is issued."
                if arguments.diagnostic_workspace
                else "Formal evidence requires a completely clean checkout."
            ),
        ],
    }
    evidence["evidence_sha256"] = sha256_text(stable_json(evidence))
    publication_revoked = False
    try:
        write_evidence(
            evidence_path,
            evidence,
            publication=evidence_publication,
            commit=False,
        )
        if formal_evidence_eligible:
            post_write_head = git_output(ROOT, ["rev-parse", "HEAD"])
            post_write_digest = (
                workspace_digest(ROOT, baseline) if baseline is not None else None
            )
            post_write_clean = workspace_cleanliness_check(ROOT, diagnostic=False)
            if (
                post_write_head != head_commit
                or post_write_digest != digest
                or post_write_clean.get("status") != "PASS"
            ):
                evidence["gate"] = VERIFICATION_LEVEL
                evidence["verification_level"] = None
                evidence["overall_status"] = "FAIL"
                evidence["commit_sha"] = None
                evidence["limitations"].append(
                    "Formal evidence was revoked because repository state changed during evidence publication."
                )
                evidence.pop("evidence_sha256", None)
                evidence["evidence_sha256"] = sha256_text(stable_json(evidence))
                write_evidence(
                    evidence_path,
                    evidence,
                    publication=evidence_publication,
                    commit=False,
                )
                publication_revoked = True
        if (
            not publication_revoked
            and receipt_path is not None
            and receipt
            and receipt_publication is not None
        ):
            write_evidence(
                receipt_path,
                receipt,
                publication=receipt_publication,
                commit=False,
            )
            receipt_publication.commit()
        if formal_evidence_eligible and not publication_revoked:
            final_publish_head = git_output(ROOT, ["rev-parse", "HEAD"])
            final_publish_digest = (
                workspace_digest(ROOT, baseline) if baseline is not None else None
            )
            final_publish_clean = workspace_cleanliness_check(
                ROOT, diagnostic=False
            )
            if (
                final_publish_head != head_commit
                or final_publish_digest != digest
                or final_publish_clean.get("status") != "PASS"
            ):
                evidence["gate"] = VERIFICATION_LEVEL
                evidence["verification_level"] = None
                evidence["overall_status"] = "FAIL"
                evidence["commit_sha"] = None
                evidence["limitations"].append(
                    "Formal evidence was revoked by the final pre-COMMITTED repository check."
                )
                evidence.pop("evidence_sha256", None)
                evidence["evidence_sha256"] = sha256_text(stable_json(evidence))
                write_evidence(
                    evidence_path,
                    evidence,
                    publication=evidence_publication,
                    commit=False,
                )
                publication_revoked = True
        evidence_publication.commit()
    except Exception as exc:
        print("ERROR: failed to write evidence: {0}".format(exc), file=sys.stderr)
        return 1

    if publication_revoked:
        print(
            "ERROR: repository state changed during evidence publication",
            file=sys.stderr,
        )
        return 1

    print("Phase 0 gate: " + overall_status)
    print("Verification level: " + (evidence["verification_level"] or "NONE"))
    print("Commit: " + str(head_commit))
    print("Evidence: " + str(evidence_path))
    print("COMMITTED marker: " + str(evidence_publication.marker_path))
    for check in checks:
        print("[{0}] {1}".format(check["status"], check["id"]))
    return 0 if overall_status == "PASS" else 1


def main(argv: Optional[Sequence[str]] = None) -> int:
    arguments = parse_arguments(argv)
    publication_run_id = _new_publication_run_id()
    try:
        raw_evidence_path = (
            Path(arguments.evidence)
            if arguments.evidence is not None
            else _default_phase0_evidence_path(publication_run_id)
        )
        evidence_path = _safe_phase0_output_path(
            ROOT, raw_evidence_path, "Phase0 evidence"
        )
        receipt_path = (
            _safe_phase0_output_path(
                ROOT, Path(arguments.receipt_out), "instruction receipt"
            )
            if arguments.receipt_out
            else None
        )
        if receipt_path is not None:
            same_existing_file = False
            if receipt_path.exists() and evidence_path.exists():
                try:
                    same_existing_file = os.path.samefile(receipt_path, evidence_path)
                except OSError:
                    same_existing_file = False
            if receipt_path == evidence_path or same_existing_file:
                raise Phase0Error(
                    "Phase0 evidence and instruction receipt must use different paths"
                )
    except Phase0Error as exc:
        print("ERROR: unsafe Phase0 output path: " + str(exc), file=sys.stderr)
        return 2

    try:
        with contextlib.ExitStack() as stack:
            evidence_publication = stack.enter_context(
                EvidencePublication(evidence_path, run_id=publication_run_id)
            )
            receipt_publication = (
                stack.enter_context(EvidencePublication(receipt_path))
                if receipt_path is not None
                else None
            )
            run_exit_code = _run_phase0_gate(
                arguments,
                evidence_path,
                receipt_path,
                evidence_publication,
                receipt_publication,
            )
    except (OSError, Phase0Error, RuntimeError) as exc:
        print("ERROR: evidence publication could not start: {0}".format(exc), file=sys.stderr)
        run_exit_code = 1

    overall_status, _ = evaluate_checks(
        [
            new_check(
                "phase0-runner-outcome",
                True,
                "PASS" if run_exit_code == 0 else "FAIL",
                None,
                run_exit_code,
                0,
                "Phase 0 runner completed"
                if run_exit_code == 0
                else "Phase 0 runner failed closed",
            )
        ]
    )
    return 0 if overall_status == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
