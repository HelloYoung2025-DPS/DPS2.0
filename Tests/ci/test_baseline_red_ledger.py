#!/usr/bin/env python3
"""Adversarial fixtures for the baseline red ledger three-state policy.

Every MF finding from the adversarial review of the first ledger draft has at
least one interception test here: MF1 print forgery and truncated-name
collisions, MF2 diagnostic budget-pool moves, MF3 green-coverage shrinkage,
MF4 the problems bypass on already-red checks, MF5 self-protection of this
guard suite, MF6 ledger removal, MF7 the HEAD-fallback self-comparison, MF8
honest documentation, MF9 the release literal-PASS gate, and MF10 the
reseeded repository ledger.

The third-round counter-findings are intercepted here too: CF-P0 the
Tests/ci output-stream hijack (parent-process file-set pinning, see
CiTestFileSetPinTests), CF-P1 subTest failures collapsing into a permanent
parse_error, and CF-P2 an explicit --base that resolves to HEAD posing as
drift authority.
"""

from __future__ import annotations

import json
import os
import shutil
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
CI_DIRECTORY = ROOT / "Tools" / "ci"
_ORIGINAL_IMPORT_PATH = list(sys.path)
try:
    if str(CI_DIRECTORY) not in sys.path:
        sys.path.insert(0, str(CI_DIRECTORY))

    import phase0 as phase0_module  # noqa: E402
    import run_candidate_gate  # noqa: E402
    import run_phase0_gate  # noqa: E402
    from phase0 import Phase0Error, evaluate_checks, new_check  # noqa: E402
    from run_phase0_gate import (  # noqa: E402
        BASELINE_RED_DRIFT_CHECK_ID,
        BASELINE_RED_LEDGER_RELATIVE_PATH,
        BASELINE_RED_LEDGER_SCHEMA_VERSION,
        OVERALL_PASS_WITH_REGISTERED_BASELINE,
        PASSING_OVERALL_STATUSES,
        PHASE0_MINIMUM_ADVERSARIAL_TESTS,
        PHASE0_REQUIRED_UNITTEST_INVENTORY,
        _evaluate_ledger_drift,
        apply_baseline_red_policy,
        baseline_red_ledger_drift_check,
        compute_failure_fingerprint,
        dotnet_failure_fingerprint,
        error_set_fingerprint,
        evaluate_baseline_red_policy,
        fingerprint_relation,
        gate_exit_code,
        parse_baseline_red_ledger,
        resolve_baseline,
        unittest_failure_fingerprint,
    )
finally:
    sys.path[:] = _ORIGINAL_IMPORT_PATH
    del _ORIGINAL_IMPORT_PATH


COMMIT = "65c2f5fed6392e08868aec25911ab5c74ea12ab5"
SEPARATOR = "=" * 70
DASHES = "-" * 70


def unittest_log(
    headers: Sequence[tuple],
    executed: int = 5,
    prefix: str = "",
    summary: Optional[str] = None,
) -> str:
    """Compose a canonical python-unittest failure log."""

    failures = sum(1 for kind, _, _ in headers if kind == "FAIL")
    errors = sum(1 for kind, _, _ in headers if kind == "ERROR")
    if summary is None:
        tokens = []
        if failures:
            tokens.append("failures={0}".format(failures))
        if errors:
            tokens.append("errors={0}".format(errors))
        summary = "FAILED ({0})".format(", ".join(tokens))
    blocks = "".join(
        SEPARATOR
        + "\n{0}: {1} ({2})\n".format(kind, method, qualified)
        + DASHES
        + "\nTraceback (most recent call last):\n  boom\n\n"
        for kind, method, qualified in headers
    )
    return (
        prefix
        + blocks
        + DASHES
        + "\nRan {0} tests in 1.000s\n\n".format(executed)
        + summary
        + "\n"
    )


UNITTEST_HEADERS = (
    ("FAIL", "test_two", "test_mod.ExampleTests.test_two"),
    ("ERROR", "test_three", "test_mod.ExampleTests.test_three"),
)
UNITTEST_LOG = unittest_log(UNITTEST_HEADERS)
DOTNET_COMPILE_LOG = (
    "--- trusted segment 1: restore ---\n"
    "  All projects are up-to-date for restore.\n"
    "--- trusted segment 2: dotnet-test ---\n"
    "Modules/example/tests/ExampleTests.cs(10,4): error CS1503: bad argument "
    "[Modules/example/tests/Example.Tests.csproj]\n"
    "Modules/example/tests/ExampleTests.cs(20,4): error xUnit1051: token "
    "[Modules/example/tests/Example.Tests.csproj]\n"
    "Build failed with exit code: 1.\n"
)
DOTNET_TESTRUN_LOG = (
    "--- trusted segment 2: dotnet-test ---\n"
    "failed Dps.Example.Tests.ExampleTests.Case_one (1s 2ms)\n"
    "  System.InvalidOperationException : boom\n"
    "Test run summary: Failed!\n"
    "  total: 4\n"
    "  failed: 1\n"
    "  succeeded: 3\n"
    "  skipped: 0\n"
)
ERROR_SET_LOG = "ERROR: alpha broke\nbeta broke\n"


def check_for(
    check_id: str,
    status: str,
    log: str = "",
    required: bool = True,
    details: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    exit_code = 0 if status == "PASS" else 1
    return new_check(check_id, required, status, None, exit_code, 0, log, details)


def entry_for(
    check_id: str,
    fingerprint: Dict[str, Any],
    batch: str = "M2",
    root_cause: str = "registered baseline red",
) -> Dict[str, Any]:
    return {
        "check_id": check_id,
        "registered_batch": batch,
        "root_cause": root_cause,
        "observed_commit": COMMIT,
        "failure_fingerprint": fingerprint,
    }


def ledger_text(
    entries: Sequence[Dict[str, Any]], notes: Optional[List[str]] = None
) -> str:
    payload: Dict[str, Any] = {
        "schema_version": BASELINE_RED_LEDGER_SCHEMA_VERSION,
        "entries": list(entries),
    }
    if notes is not None:
        payload["notes"] = notes
    return json.dumps(
        payload, ensure_ascii=False, indent=2, sort_keys=True
    ) + "\n"


def state_for(
    entries: Optional[List[Dict[str, Any]]],
    baseline_entries: Optional[List[Dict[str, Any]]],
    errors: Optional[List[str]] = None,
    baseline_commit: Optional[str] = COMMIT,
) -> Dict[str, Any]:
    state: Dict[str, Any] = {
        "ledger_path": BASELINE_RED_LEDGER_RELATIVE_PATH,
        "present": entries is not None,
        "ledger_sha256": "0" * 64 if entries is not None else None,
        "entries": entries,
        "errors": list(errors or []),
        "baseline_commit": baseline_commit,
        "baseline_present": (
            (baseline_entries is not None) if baseline_commit is not None else None
        ),
        "baseline_ledger_sha256": "1" * 64 if baseline_entries is not None else None,
        "baseline_entries": baseline_entries,
        "drift_status": None,
        "drift_errors": [],
    }
    _evaluate_ledger_drift(state)
    return state


def run_pipeline(
    checks: Sequence[Dict[str, Any]], state: Dict[str, Any]
) -> tuple:
    """Mirror the _run_phase0_gate wiring: drift check, evaluate, then apply."""

    combined = list(checks) + [baseline_red_ledger_drift_check(state)]
    overall_status, _summary = evaluate_checks(combined)
    baseline_red = evaluate_baseline_red_policy(combined, state)
    return apply_baseline_red_policy(overall_status, baseline_red), baseline_red


class FingerprintParserTests(unittest.TestCase):
    def test_unittest_fingerprint_keeps_full_module_qualified_names(self) -> None:
        fingerprint = unittest_failure_fingerprint(UNITTEST_LOG)
        self.assertEqual(
            {
                "kind": "unittest",
                "failed_tests": [
                    "test_mod.ExampleTests.test_three",
                    "test_mod.ExampleTests.test_two",
                ],
                "count": 2,
                "executed": 5,
                "missing_inventory": [],
            },
            fingerprint,
        )

    def test_unittest_fingerprint_handles_setupclass_errors(self) -> None:
        log = unittest_log(
            [("ERROR", "setUpClass", "test_mod.ExampleTests")], executed=1
        )
        fingerprint = unittest_failure_fingerprint(log)
        self.assertEqual(
            ["test_mod.ExampleTests.setUpClass"], fingerprint["failed_tests"]
        )

    def test_subtest_failures_keep_distinct_identities(self) -> None:
        # CF-P1: subTest failures share one method name but each failed
        # subTest is its own detail block and its own unit in the FAILED
        # summary.  The parameter/message suffix therefore stays part of the
        # counted identity; collapsing them by method made every subTest
        # failure an UNPARSEABLE parse_error.
        base = "FAIL: test_matrix (test_mod.ExampleTests.test_matrix)"
        block_tail = "\n" + DASHES + "\nTraceback (most recent call last):\n  boom\n\n"
        log = (
            SEPARATOR + "\n" + base + " (a=1, b=2, c=3)" + block_tail
            + SEPARATOR + "\n" + base + " (a=2, b=2, c=3)" + block_tail
            + SEPARATOR + "\n" + base + " [boom] (a=3,  b=2,\tc=3)" + block_tail
            + DASHES + "\nRan 1 test in 0.001s\n\nFAILED (failures=3)\n"
        )
        fingerprint = unittest_failure_fingerprint(log)
        self.assertNotIn("parse_error", fingerprint)
        self.assertEqual(3, fingerprint["count"])
        self.assertEqual(
            [
                "test_mod.ExampleTests.test_matrix (a=1, b=2, c=3)",
                "test_mod.ExampleTests.test_matrix (a=2, b=2, c=3)",
                "test_mod.ExampleTests.test_matrix [boom] (a=3, b=2, c=3)",
            ],
            fingerprint["failed_tests"],
        )
        # One executed test carrying three subTest failures must be a
        # registrable fingerprint (executed is not bounded by failure count).
        self.assertEqual(1, fingerprint["executed"])
        self.assertEqual([], run_phase0_gate._fingerprint_errors(fingerprint))

    def test_unittest_fingerprint_records_executed_and_missing_inventory(self) -> None:
        fingerprint = unittest_failure_fingerprint(
            UNITTEST_LOG, required_inventory=["test_two", "test_absent_carrier"]
        )
        self.assertEqual(5, fingerprint["executed"])
        self.assertEqual(["test_absent_carrier"], fingerprint["missing_inventory"])

    def test_print_forged_failure_lines_outside_detail_blocks_are_ignored(self) -> None:
        forged = unittest_log(
            UNITTEST_HEADERS,
            prefix=(
                "test_a (m.C.test_a) ... ok\n"
                "FAIL: test_forged (evil_mod.ExampleTests.test_forged)\n"
                "ERROR: test_two (test_mod.ExampleTests.test_two)\n"
            ),
        )
        self.assertEqual(
            unittest_failure_fingerprint(UNITTEST_LOG),
            unittest_failure_fingerprint(forged),
        )

    def test_forged_detail_block_breaks_the_summary_cross_check(self) -> None:
        forged_block = (
            SEPARATOR
            + "\nFAIL: test_ghost (test_mod.ExampleTests.test_ghost)\n"
            + DASHES
            + "\nforged\n\n"
        )
        forged = unittest_log(UNITTEST_HEADERS, prefix=forged_block)
        fingerprint = unittest_failure_fingerprint(forged)
        self.assertIn("parse_error", fingerprint)
        self.assertIn("do not equal the FAILED summary", fingerprint["parse_error"])

    def test_duplicate_ran_summaries_are_a_parse_error(self) -> None:
        forged = "Ran 9 tests in 0.100s\n" + UNITTEST_LOG
        fingerprint = unittest_failure_fingerprint(forged)
        self.assertIn("parse_error", fingerprint)
        self.assertIn("exactly one canonical", fingerprint["parse_error"])

    def test_missing_failed_summary_is_a_parse_error(self) -> None:
        truncated = UNITTEST_LOG.rsplit("FAILED", 1)[0] + "PASSED (hooray)\n"
        fingerprint = unittest_failure_fingerprint(truncated)
        self.assertIn("parse_error", fingerprint)

    def test_dotnet_fingerprint_dedupes_repeated_diagnostics(self) -> None:
        duplicated = DOTNET_COMPILE_LOG + DOTNET_COMPILE_LOG
        self.assertEqual(
            dotnet_failure_fingerprint(DOTNET_COMPILE_LOG),
            dotnet_failure_fingerprint(duplicated),
        )
        fingerprint = dotnet_failure_fingerprint(DOTNET_COMPILE_LOG)
        self.assertEqual({"CS1503": 1, "xUnit1051": 1}, fingerprint["error_codes"])
        self.assertEqual(2, fingerprint["count"])
        self.assertTrue(fingerprint["build_failed"])
        self.assertIsNone(fingerprint["test_counts"])

    def test_dotnet_fingerprint_strips_the_given_root_prefix(self) -> None:
        remote = DOTNET_COMPILE_LOG.replace(
            "Modules/example", "/home/runner/work/DPS2.0/DPS2.0/Modules/example"
        )
        fingerprint = dotnet_failure_fingerprint(
            remote, root_prefixes=["/home/runner/work/DPS2.0/DPS2.0"]
        )
        self.assertEqual(
            dotnet_failure_fingerprint(DOTNET_COMPILE_LOG), fingerprint
        )

    def test_dotnet_line_number_drift_changes_the_fingerprint(self) -> None:
        drifted = DOTNET_COMPILE_LOG.replace(
            "ExampleTests.cs(10,4)", "ExampleTests.cs(11,4)"
        )
        registered = dotnet_failure_fingerprint(DOTNET_COMPILE_LOG)
        observed = dotnet_failure_fingerprint(drifted)
        self.assertEqual(registered["error_codes"], observed["error_codes"])
        self.assertEqual(registered["files"], observed["files"])
        self.assertNotEqual(
            registered["error_instances_sha256"], observed["error_instances_sha256"]
        )
        self.assertEqual("GREW", fingerprint_relation(observed, registered))

    def test_dotnet_cross_file_migration_changes_the_fingerprint(self) -> None:
        two_files = (
            DOTNET_COMPILE_LOG
            + "Modules/example/tests/OtherTests.cs(5,1): error CS1503: bad "
            "[Modules/example/tests/Example.Tests.csproj]\n"
        )
        migrated = (
            DOTNET_COMPILE_LOG.replace(
                "ExampleTests.cs(10,4): error CS1503",
                "ExampleTests.cs(10,4): error xUnit1051",
            )
            .replace(
                "ExampleTests.cs(20,4): error xUnit1051",
                "ExampleTests.cs(20,4): error CS1503",
            )
            + "Modules/example/tests/OtherTests.cs(5,1): error CS1503: bad "
            "[Modules/example/tests/Example.Tests.csproj]\n"
        )
        registered = dotnet_failure_fingerprint(two_files)
        observed = dotnet_failure_fingerprint(migrated)
        self.assertEqual(registered["error_codes"], observed["error_codes"])
        self.assertEqual(registered["files"], observed["files"])
        self.assertEqual(registered["count"], observed["count"])
        self.assertNotEqual(
            registered["error_instances_sha256"], observed["error_instances_sha256"]
        )
        self.assertEqual("GREW", fingerprint_relation(observed, registered))

    def test_dotnet_fingerprint_pins_test_counts_and_build_marker(self) -> None:
        fingerprint = dotnet_failure_fingerprint(DOTNET_TESTRUN_LOG)
        self.assertEqual(
            {"total": 4, "failed": 1, "succeeded": 3, "skipped": 0},
            fingerprint["test_counts"],
        )
        self.assertEqual(
            ["Dps.Example.Tests.ExampleTests.Case_one"], fingerprint["failed_tests"]
        )
        self.assertFalse(fingerprint["build_failed"])
        self.assertEqual(1, fingerprint["count"])

    def test_dotnet_ambiguous_test_summary_is_a_parse_error(self) -> None:
        doubled = DOTNET_TESTRUN_LOG + "  total: 4\n"
        fingerprint = dotnet_failure_fingerprint(doubled)
        self.assertIn("parse_error", fingerprint)

    def test_error_set_fingerprint_is_order_independent(self) -> None:
        reordered = "ERROR: beta broke\nalpha broke\n"
        self.assertEqual(
            error_set_fingerprint(ERROR_SET_LOG),
            error_set_fingerprint(reordered),
        )
        self.assertEqual(2, error_set_fingerprint(ERROR_SET_LOG)["count"])

    def test_unknown_fingerprint_kind_fails_closed(self) -> None:
        with self.assertRaises(Phase0Error):
            compute_failure_fingerprint("made-up", "log")


class LedgerParseTests(unittest.TestCase):
    def entry(self) -> Dict[str, Any]:
        return entry_for(
            "phase0-adversarial-unit-tests",
            unittest_failure_fingerprint(UNITTEST_LOG),
        )

    def test_committed_repository_ledger_stays_strictly_valid(self) -> None:
        # No entry-count pin here: the shrink-only lifecycle expects entries to
        # disappear as reds are fixed, and only strict validity is invariant.
        text = (ROOT / BASELINE_RED_LEDGER_RELATIVE_PATH).read_text(
            encoding="utf-8-sig"
        )
        entries = parse_baseline_red_ledger(text, "repository ledger")
        self.assertNotIn(
            BASELINE_RED_DRIFT_CHECK_ID,
            {entry["check_id"] for entry in entries},
        )

    def test_committed_repository_ledger_pins_the_reseeded_fingerprints(self) -> None:
        # MF10: the eight entries were reseeded from the archived main-tip CI
        # evidence under the per-instance fingerprint definitions.  Entries may
        # legitimately shrink away, but while an entry exists its fingerprint
        # must stay in the reseeded shape.
        text = (ROOT / BASELINE_RED_LEDGER_RELATIVE_PATH).read_text(
            encoding="utf-8-sig"
        )
        by_id = {
            entry["check_id"]: entry
            for entry in parse_baseline_red_ledger(text, "repository ledger")
        }
        adversarial = by_id.get("phase0-adversarial-unit-tests")
        if adversarial is not None:
            fingerprint = adversarial["failure_fingerprint"]
            self.assertEqual("unittest", fingerprint["kind"])
            self.assertEqual([], fingerprint["missing_inventory"])
            self.assertEqual(9, fingerprint["count"])
            for name in fingerprint["failed_tests"]:
                self.assertGreaterEqual(
                    name.count("."), 2, "full module-qualified name required"
                )
            self.assertIn(
                "test_legacy_sessionrunner_strangler."
                "SessionRunnerStranglerBaselineTests."
                "test_schema_weakening_fails_closed",
                fingerprint["failed_tests"],
            )
        supervisor = by_id.get("manifest:windows-edge-supervisor:windows-edge-supervisor.unit")
        if supervisor is not None:
            fingerprint = supervisor["failure_fingerprint"]
            self.assertEqual("M3", supervisor["registered_batch"])
            self.assertEqual(
                {"total": 12, "failed": 3, "succeeded": 9, "skipped": 0},
                fingerprint["test_counts"],
            )
            self.assertEqual(3, len(fingerprint["failed_tests"]))
        solution = by_id.get("solution-locked-restore-build")
        if solution is not None:
            fingerprint = solution["failure_fingerprint"]
            self.assertTrue(fingerprint["build_failed"])
            self.assertEqual(84, fingerprint["count"])
            self.assertEqual(
                {
                    "CS1503": 9,
                    "CS1729": 2,
                    "CS8632": 3,
                    "xUnit1031": 1,
                    "xUnit1051": 69,
                },
                fingerprint["error_codes"],
            )

    def test_valid_ledger_round_trips(self) -> None:
        entries = parse_baseline_red_ledger(ledger_text([self.entry()]), "fixture")
        self.assertEqual(1, len(entries))

    def test_optional_top_level_notes_are_validated(self) -> None:
        parsed = parse_baseline_red_ledger(
            ledger_text([self.entry()], notes=["Zebra note.", "alpha note."]),
            "fixture",
        )
        self.assertEqual(1, len(parsed))
        with self.assertRaisesRegex(Phase0Error, "notes must be"):
            parse_baseline_red_ledger(
                ledger_text([self.entry()], notes=[""]), "fixture"
            )

    def test_duplicate_check_id_is_rejected(self) -> None:
        with self.assertRaisesRegex(Phase0Error, "duplicate ledger entry"):
            parse_baseline_red_ledger(
                ledger_text([self.entry(), self.entry()]), "fixture"
            )

    def test_registering_the_drift_check_itself_is_rejected(self) -> None:
        entry = self.entry()
        entry["check_id"] = BASELINE_RED_DRIFT_CHECK_ID
        with self.assertRaisesRegex(Phase0Error, "drift check itself"):
            parse_baseline_red_ledger(ledger_text([entry]), "fixture")

    def test_count_mismatch_is_rejected(self) -> None:
        entry = self.entry()
        entry["failure_fingerprint"]["count"] = 5
        with self.assertRaisesRegex(Phase0Error, "count must equal"):
            parse_baseline_red_ledger(ledger_text([entry]), "fixture")

    def test_unsorted_failed_tests_are_rejected(self) -> None:
        entry = self.entry()
        entry["failure_fingerprint"]["failed_tests"] = list(
            reversed(entry["failure_fingerprint"]["failed_tests"])
        )
        with self.assertRaisesRegex(Phase0Error, "sorted"):
            parse_baseline_red_ledger(ledger_text([entry]), "fixture")

    def test_wrong_schema_version_is_rejected(self) -> None:
        text = ledger_text([self.entry()]).replace(
            BASELINE_RED_LEDGER_SCHEMA_VERSION, "dps.baseline-red-ledger/v2"
        )
        with self.assertRaisesRegex(Phase0Error, "schema_version"):
            parse_baseline_red_ledger(text, "fixture")

    def test_parse_error_fingerprint_is_rejected(self) -> None:
        entry = self.entry()
        entry["failure_fingerprint"] = unittest_failure_fingerprint("garbage")
        self.assertIn("parse_error", entry["failure_fingerprint"])
        with self.assertRaisesRegex(Phase0Error, "parse_error"):
            parse_baseline_red_ledger(ledger_text([entry]), "fixture")

    def test_dotnet_count_shape_invariants_are_rejected(self) -> None:
        entry = entry_for(
            "manifest:example:example.unit",
            dotnet_failure_fingerprint(DOTNET_TESTRUN_LOG),
        )
        entry["failure_fingerprint"]["test_counts"]["total"] = 9
        with self.assertRaisesRegex(Phase0Error, "total must equal"):
            parse_baseline_red_ledger(ledger_text([entry]), "fixture")
        entry = entry_for(
            "manifest:example:example.unit",
            dotnet_failure_fingerprint(DOTNET_TESTRUN_LOG),
        )
        entry["failure_fingerprint"]["failed_tests"] = []
        entry["failure_fingerprint"]["count"] = 0
        with self.assertRaisesRegex(Phase0Error, "failed must equal|at least one"):
            parse_baseline_red_ledger(ledger_text([entry]), "fixture")


class ThreeStateVerdictTests(unittest.TestCase):
    def matching_fixture(self) -> tuple:
        checks = [
            check_for("clean-checkout-evidence-boundary", "PASS"),
            check_for("phase0-adversarial-unit-tests", "FAIL", UNITTEST_LOG),
            check_for("manifest:example:example.unit", "FAIL", DOTNET_COMPILE_LOG),
            check_for("module-governance", "FAIL", ERROR_SET_LOG),
        ]
        entries = [
            entry_for(
                "phase0-adversarial-unit-tests",
                unittest_failure_fingerprint(UNITTEST_LOG),
                batch="R0-B",
            ),
            entry_for(
                "manifest:example:example.unit",
                dotnet_failure_fingerprint(DOTNET_COMPILE_LOG),
            ),
            entry_for(
                "module-governance",
                error_set_fingerprint(ERROR_SET_LOG),
                batch="R0-B",
            ),
        ]
        return checks, entries

    def test_all_green_with_no_ledger_stays_pass(self) -> None:
        checks = [check_for("clean-checkout-evidence-boundary", "PASS")]
        state = state_for(entries=None, baseline_entries=None)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("PASS", overall)
        self.assertEqual("LEDGER_ABSENT", state["drift_status"])
        self.assertEqual("LEDGER_INACTIVE", baseline_red["result"])

    def test_all_green_with_emptied_ledger_stays_pass(self) -> None:
        checks = [check_for("clean-checkout-evidence-boundary", "PASS")]
        _checks, entries = self.matching_fixture()
        state = state_for(entries=[], baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("PASS", overall)
        self.assertEqual("LEDGER_SHRUNK", state["drift_status"])
        self.assertEqual("LEDGER_INACTIVE", baseline_red["result"])

    def test_exact_fingerprint_match_passes_with_registered_baseline(self) -> None:
        checks, entries = self.matching_fixture()
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual(OVERALL_PASS_WITH_REGISTERED_BASELINE, overall)
        self.assertEqual("REGISTERED_BASELINE_ONLY", baseline_red["result"])
        self.assertEqual("LEDGER_UNCHANGED", baseline_red["drift_status"])
        self.assertEqual(state["ledger_sha256"], baseline_red["ledger_sha256"])
        self.assertEqual(
            ["MATCHED", "MATCHED", "MATCHED"],
            [entry["verdict"] for entry in baseline_red["entries"]],
        )
        self.assertEqual(
            [
                "manifest:example:example.unit",
                "module-governance",
                "phase0-adversarial-unit-tests",
            ],
            baseline_red["required_failures"],
        )
        self.assertEqual([], baseline_red["unregistered_required_failures"])
        self.assertTrue(
            any("registered baseline red" in note for note in baseline_red["notes"])
        )

    def test_overall_status_literal_is_never_pass(self) -> None:
        self.assertNotEqual("PASS", OVERALL_PASS_WITH_REGISTERED_BASELINE)
        self.assertIn(OVERALL_PASS_WITH_REGISTERED_BASELINE, PASSING_OVERALL_STATUSES)
        checks, entries = self.matching_fixture()
        state = state_for(entries=entries, baseline_entries=entries)
        overall, _baseline_red = run_pipeline(checks, state)
        self.assertNotEqual("PASS", overall)
        self.assertEqual("PASS_WITH_REGISTERED_BASELINE", overall)

    def test_unregistered_red_fails(self) -> None:
        checks, entries = self.matching_fixture()
        checks.append(check_for("repository-validator", "FAIL", "boom"))
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        self.assertEqual("BLOCKED", baseline_red["result"])
        self.assertEqual(
            ["repository-validator"],
            baseline_red["unregistered_required_failures"],
        )

    def test_extra_failure_inside_fingerprint_fails_as_mismatch(self) -> None:
        checks, entries = self.matching_fixture()
        grown = UNITTEST_HEADERS + (
            ("FAIL", "test_new", "test_mod.ExampleTests.test_new"),
        )
        checks[1] = check_for(
            "phase0-adversarial-unit-tests", "FAIL", unittest_log(grown)
        )
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual("MISMATCH", verdicts["phase0-adversarial-unit-tests"])
        self.assertTrue(
            any("unregistered red" in reason for reason in baseline_red["block_reasons"])
        )

    def test_same_class_name_in_another_module_is_a_mismatch(self) -> None:
        # MF1: the registered identity keeps every module segment, so the same
        # class.method reappearing from a different module never matches.
        checks, entries = self.matching_fixture()
        relabelled = (
            ("FAIL", "test_two", "evil_mod.ExampleTests.test_two"),
            ("ERROR", "test_three", "evil_mod.ExampleTests.test_three"),
        )
        checks[1] = check_for(
            "phase0-adversarial-unit-tests", "FAIL", unittest_log(relabelled)
        )
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual("MISMATCH", verdicts["phase0-adversarial-unit-tests"])

    def test_executed_count_shrink_is_a_mismatch(self) -> None:
        # MF3/MF4: deleting passing (non-inventory) tests shrinks the Ran
        # count; the failure set alone no longer decides a match.
        checks, entries = self.matching_fixture()
        checks[1] = check_for(
            "phase0-adversarial-unit-tests",
            "FAIL",
            unittest_log(UNITTEST_HEADERS, executed=3),
        )
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual("MISMATCH", verdicts["phase0-adversarial-unit-tests"])

    def test_missing_inventory_after_deleting_a_carrier_file_is_a_mismatch(self) -> None:
        # MF4: on an already-red check the executed/inventory problems used to
        # land only in details; they are now part of the fingerprint.
        checks, entries = self.matching_fixture()
        entries[0] = entry_for(
            "phase0-adversarial-unit-tests",
            unittest_failure_fingerprint(
                UNITTEST_LOG, required_inventory=["test_two"]
            ),
            batch="R0-B",
        )
        checks[1] = check_for(
            "phase0-adversarial-unit-tests",
            "FAIL",
            UNITTEST_LOG,
            details={"required_inventory": ["test_two", "test_deleted_carrier"]},
        )
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual("MISMATCH", verdicts["phase0-adversarial-unit-tests"])

    def test_dotnet_test_count_shrink_is_a_mismatch(self) -> None:
        # MF3: same failed tests, fewer green tests -> counts deviate -> FAIL.
        registered = dotnet_failure_fingerprint(DOTNET_TESTRUN_LOG)
        shrunk_log = DOTNET_TESTRUN_LOG.replace("total: 4", "total: 2").replace(
            "succeeded: 3", "succeeded: 1"
        )
        checks = [
            check_for("clean-checkout-evidence-boundary", "PASS"),
            check_for("manifest:example:example.unit", "FAIL", shrunk_log),
        ]
        entries = [entry_for("manifest:example:example.unit", registered)]
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        self.assertEqual(
            "MISMATCH", baseline_red["entries"][0]["verdict"]
        )

    def test_dotnet_partial_fix_is_a_mismatch_requiring_owner_reseed(self) -> None:
        # MF2 documented cost: fixing one of two registered compile errors is
        # still a fingerprint deviation; the ledger must be reseeded through
        # an Owner merge (or the entry removed once fully green).
        checks, entries = self.matching_fixture()
        partially_fixed = DOTNET_COMPILE_LOG.replace(
            "Modules/example/tests/ExampleTests.cs(10,4): error CS1503: bad "
            "argument [Modules/example/tests/Example.Tests.csproj]\n",
            "",
        )
        checks[2] = check_for(
            "manifest:example:example.unit", "FAIL", partially_fixed
        )
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual("MISMATCH", verdicts["manifest:example:example.unit"])

    def test_partially_green_check_is_stale_and_fails_with_shrink_hint(self) -> None:
        checks, entries = self.matching_fixture()
        checks[1] = check_for(
            "phase0-adversarial-unit-tests",
            "FAIL",
            unittest_log([("FAIL", "test_two", "test_mod.ExampleTests.test_two")]),
        )
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual(
            "STALE_PARTIALLY_GREEN", verdicts["phase0-adversarial-unit-tests"]
        )
        self.assertTrue(
            any(
                "shrink " + BASELINE_RED_LEDGER_RELATIVE_PATH + " in the same PR"
                in reason
                for reason in baseline_red["block_reasons"]
            )
        )

    def test_fully_green_check_with_ledger_entry_is_stale_and_fails(self) -> None:
        checks, entries = self.matching_fixture()
        checks[1] = check_for("phase0-adversarial-unit-tests", "PASS")
        checks[2] = check_for("manifest:example:example.unit", "PASS")
        checks[3] = check_for("module-governance", "PASS")
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        self.assertEqual("BLOCKED", baseline_red["result"])
        self.assertEqual(
            {"STALE_GREEN"},
            {entry["verdict"] for entry in baseline_red["entries"]},
        )
        self.assertTrue(
            any("shrink" in reason for reason in baseline_red["block_reasons"])
        )

    def test_infra_error_on_registered_check_fails(self) -> None:
        checks, entries = self.matching_fixture()
        checks[1] = check_for("phase0-adversarial-unit-tests", "INFRA_ERROR", "boom")
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual(
            "UNMATCHABLE_STATUS_INFRA_ERROR",
            verdicts["phase0-adversarial-unit-tests"],
        )

    def test_unparseable_failure_log_fails_closed(self) -> None:
        checks, entries = self.matching_fixture()
        checks[1] = check_for(
            "phase0-adversarial-unit-tests", "FAIL", "no recognizable failure lines"
        )
        state = state_for(entries=entries, baseline_entries=entries)
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        verdicts = {
            entry["check_id"]: entry["verdict"] for entry in baseline_red["entries"]
        }
        self.assertEqual(
            "UNPARSEABLE_FAILURE", verdicts["phase0-adversarial-unit-tests"]
        )

    def test_malformed_ledger_blocks_even_an_all_green_run(self) -> None:
        checks = [check_for("clean-checkout-evidence-boundary", "PASS")]
        state = state_for(
            entries=None,
            baseline_entries=None,
            errors=["working tree ledger: ledger is not valid JSON: boom"],
        )
        state["present"] = True
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        self.assertEqual("BLOCKED", baseline_red["result"])
        self.assertEqual("LEDGER_INVALID", state["drift_status"])


class LedgerDriftTests(unittest.TestCase):
    def entries(self) -> List[Dict[str, Any]]:
        return [
            entry_for(
                "phase0-adversarial-unit-tests",
                unittest_failure_fingerprint(UNITTEST_LOG),
            ),
            entry_for(
                "manifest:example:example.unit",
                dotnet_failure_fingerprint(DOTNET_COMPILE_LOG),
            ),
        ]

    def test_introducing_the_ledger_is_allowed(self) -> None:
        state = state_for(entries=self.entries(), baseline_entries=None)
        self.assertEqual("LEDGER_INTRODUCED", state["drift_status"])
        self.assertEqual([], state["drift_errors"])
        check = baseline_red_ledger_drift_check(state)
        self.assertEqual("PASS", check["status"])
        self.assertTrue(check["required"])
        self.assertEqual(BASELINE_RED_DRIFT_CHECK_ID, check["id"])

    def test_new_entry_is_expansion_and_fails_the_drift_check(self) -> None:
        entries = self.entries()
        state = state_for(entries=entries, baseline_entries=entries[:1])
        self.assertEqual("LEDGER_EXPANDED", state["drift_status"])
        check = baseline_red_ledger_drift_check(state)
        self.assertEqual("FAIL", check["status"])
        self.assertIn("expansion: new ledger entry", check["log"])

    def test_grown_fingerprint_is_expansion(self) -> None:
        baseline_entries = self.entries()
        entries = self.entries()
        grown = UNITTEST_HEADERS + (
            ("FAIL", "test_new", "test_mod.ExampleTests.test_new"),
        )
        entries[0]["failure_fingerprint"] = unittest_failure_fingerprint(
            unittest_log(grown)
        )
        state = state_for(entries=entries, baseline_entries=baseline_entries)
        self.assertEqual("LEDGER_EXPANDED", state["drift_status"])

    def test_error_set_hash_swap_is_expansion(self) -> None:
        baseline_entries = [
            entry_for("module-governance", error_set_fingerprint(ERROR_SET_LOG))
        ]
        entries = [
            entry_for(
                "module-governance",
                error_set_fingerprint("ERROR: different failure\n"),
            )
        ]
        state = state_for(entries=entries, baseline_entries=baseline_entries)
        self.assertEqual("LEDGER_EXPANDED", state["drift_status"])
        self.assertIn(
            "grew or changed", " ".join(state["drift_errors"])
        )

    def test_expansion_blocks_even_when_observed_failures_match(self) -> None:
        checks = [
            check_for("clean-checkout-evidence-boundary", "PASS"),
            check_for("phase0-adversarial-unit-tests", "FAIL", UNITTEST_LOG),
            check_for("manifest:example:example.unit", "FAIL", DOTNET_COMPILE_LOG),
        ]
        entries = self.entries()
        state = state_for(entries=entries, baseline_entries=entries[:1])
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        self.assertEqual("BLOCKED", baseline_red["result"])
        self.assertTrue(
            any("drift" in reason for reason in baseline_red["block_reasons"])
        )

    def test_entry_removal_is_a_shrink_and_matching_still_passes(self) -> None:
        checks = [
            check_for("clean-checkout-evidence-boundary", "PASS"),
            check_for("phase0-adversarial-unit-tests", "FAIL", UNITTEST_LOG),
        ]
        baseline_entries = self.entries()
        entries = baseline_entries[:1]
        state = state_for(entries=entries, baseline_entries=baseline_entries)
        self.assertEqual("LEDGER_SHRUNK", state["drift_status"])
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual(OVERALL_PASS_WITH_REGISTERED_BASELINE, overall)
        self.assertEqual("REGISTERED_BASELINE_ONLY", baseline_red["result"])

    def test_fingerprint_item_removal_is_a_shrink(self) -> None:
        baseline_entries = self.entries()
        entries = self.entries()
        entries[0]["failure_fingerprint"] = unittest_failure_fingerprint(
            unittest_log(
                [("FAIL", "test_two", "test_mod.ExampleTests.test_two")]
            )
        )
        state = state_for(entries=entries, baseline_entries=baseline_entries)
        self.assertEqual("LEDGER_SHRUNK", state["drift_status"])
        self.assertEqual([], state["drift_errors"])

    def test_removing_the_ledger_file_fails_the_drift_check(self) -> None:
        # MF6: removal used to be a silent pass, letting the next batch
        # reintroduce a fresh ledger and reset the shrink-only ratchet.
        state = state_for(entries=None, baseline_entries=self.entries())
        self.assertEqual("LEDGER_REMOVED", state["drift_status"])
        check = baseline_red_ledger_drift_check(state)
        self.assertEqual("FAIL", check["status"])
        self.assertIn("never a legal transition", check["log"])
        checks = [check_for("clean-checkout-evidence-boundary", "PASS")]
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        self.assertEqual("BLOCKED", baseline_red["result"])

    def test_missing_baseline_commit_with_load_bearing_ledger_fails(self) -> None:
        # MF7: without an authoritative --base the old draft compared the
        # ledger against HEAD, a vacuous self-comparison that always passed.
        checks = [
            check_for("clean-checkout-evidence-boundary", "PASS"),
            check_for("phase0-adversarial-unit-tests", "FAIL", UNITTEST_LOG),
            check_for("manifest:example:example.unit", "FAIL", DOTNET_COMPILE_LOG),
        ]
        state = state_for(
            entries=self.entries(), baseline_entries=None, baseline_commit=None
        )
        self.assertEqual("BASELINE_COMMIT_UNAVAILABLE", state["drift_status"])
        self.assertEqual(
            "FAIL", baseline_red_ledger_drift_check(state)["status"]
        )
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)
        self.assertEqual("BLOCKED", baseline_red["result"])

    def test_missing_baseline_commit_with_inert_ledger_stays_green(self) -> None:
        checks = [check_for("clean-checkout-evidence-boundary", "PASS")]
        state = state_for(entries=[], baseline_entries=None, baseline_commit=None)
        self.assertEqual("BASELINE_COMMIT_UNAVAILABLE", state["drift_status"])
        overall, baseline_red = run_pipeline(checks, state)
        self.assertEqual("PASS", overall)
        self.assertEqual("LEDGER_INACTIVE", baseline_red["result"])

    def test_missing_baseline_commit_with_absent_ledger_fails(self) -> None:
        checks = [check_for("clean-checkout-evidence-boundary", "PASS")]
        state = state_for(entries=None, baseline_entries=None, baseline_commit=None)
        self.assertEqual("BASELINE_COMMIT_UNAVAILABLE", state["drift_status"])
        overall, _baseline_red = run_pipeline(checks, state)
        self.assertEqual("FAIL", overall)


class FingerprintRelationTests(unittest.TestCase):
    def test_dotnet_relations_are_exact_match_or_growth_only(self) -> None:
        registered = dotnet_failure_fingerprint(DOTNET_COMPILE_LOG)
        self.assertEqual("MATCH", fingerprint_relation(registered, registered))
        shrunk = dotnet_failure_fingerprint(
            "Modules/example/tests/ExampleTests.cs(10,4): error CS1503: bad "
            "argument [Modules/example/tests/Example.Tests.csproj]\n"
            "Build failed with exit code: 1.\n"
        )
        self.assertEqual("GREW", fingerprint_relation(shrunk, registered))
        grown = dotnet_failure_fingerprint(
            DOTNET_COMPILE_LOG
            + "Modules/example/tests/Other.cs(1,1): error CS0246: missing "
            "[Modules/example/tests/Example.Tests.csproj]\n"
        )
        self.assertEqual("GREW", fingerprint_relation(grown, registered))

    def test_kind_change_is_growth(self) -> None:
        self.assertEqual(
            "GREW",
            fingerprint_relation(
                unittest_failure_fingerprint(UNITTEST_LOG),
                dotnet_failure_fingerprint(DOTNET_COMPILE_LOG),
            ),
        )

    def test_unittest_relation_shrinks_only_with_stable_executed_and_inventory(
        self,
    ) -> None:
        registered = unittest_failure_fingerprint(UNITTEST_LOG)
        clean_shrink = unittest_failure_fingerprint(
            unittest_log(
                [("FAIL", "test_two", "test_mod.ExampleTests.test_two")],
                executed=5,
            )
        )
        self.assertEqual("SHRUNK", fingerprint_relation(clean_shrink, registered))
        shrink_with_fewer_runs = unittest_failure_fingerprint(
            unittest_log(
                [("FAIL", "test_two", "test_mod.ExampleTests.test_two")],
                executed=4,
            )
        )
        self.assertEqual(
            "GREW", fingerprint_relation(shrink_with_fewer_runs, registered)
        )
        inventory_loss = dict(clean_shrink)
        inventory_loss["missing_inventory"] = ["test_deleted_carrier"]
        self.assertEqual("GREW", fingerprint_relation(inventory_loss, registered))


class CiTestFileSetPinTests(unittest.TestCase):
    """CF-P0: the parent gate process pins the Tests/ci discovery surface.

    Every unittest fingerprint input is read from the child processes' merged
    output stream, which a hostile test file inside Tests/ci can rewrite
    wholesale (sys.stderr swap plus an atexit rewrite).  These tests pin the
    interception that never trusts that stream: the parent's own directory
    enumeration must match the in-gate pinned file list before the suite is
    allowed to start.
    """

    def make_tree(self, names: Sequence[str]) -> Path:
        root = Path(tempfile.mkdtemp(prefix="ci-file-set-pin-"))
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        directory = root / "Tests" / "ci"
        directory.mkdir(parents=True)
        for name in names:
            (directory / name).write_text("", encoding="utf-8")
        return root

    def pinned(self) -> List[str]:
        return sorted(run_phase0_gate.PHASE0_PINNED_CI_TEST_FILES)

    def intercepted_check(self, root: Path) -> Dict[str, Any]:
        # The interception must happen in the parent before any subprocess:
        # run_command raising proves the suite never starts.
        with mock.patch.object(run_phase0_gate, "ROOT", root), mock.patch.object(
            run_phase0_gate,
            "run_command",
            side_effect=AssertionError("the unittest subprocess must not start"),
        ):
            return run_phase0_gate.run_phase0_unittests(COMMIT)

    def test_ci_test_file_set_pin_matches_the_working_tree(self) -> None:
        # The pinned constant and the real repository may never drift apart.
        self.assertEqual([], run_phase0_gate._ci_test_file_set_errors(ROOT))

    def test_ci_test_file_pin_equals_the_candidate_trust_path_set(self) -> None:
        # The gate's pinned list and the candidate trust roots are two copies
        # of the same authority; if they drift, one of them is lying.
        prefix = "Tests/ci/"
        trust_entries = {
            path
            for path in run_candidate_gate.CANDIDATE_TRUST_PATHS
            if path.startswith(prefix)
            and "/" not in path[len(prefix):]
            and path[len(prefix):].startswith("test_")
            and path.endswith(".py")
        }
        self.assertEqual(
            {prefix + name for name in run_phase0_gate.PHASE0_PINNED_CI_TEST_FILES},
            trust_entries,
        )

    def test_added_ci_test_file_is_intercepted_before_the_subprocess(self) -> None:
        added = self.intercepted_check(
            self.make_tree(self.pinned() + ["test_zz_evil.py"])
        )
        self.assertEqual("FAIL", added["status"])
        self.assertEqual(
            run_phase0_gate.TEST_FILE_SET_MISMATCH_REASON,
            added["details"]["reason"],
        )
        self.assertIn(
            "unpinned test file present: test_zz_evil.py",
            added["details"]["file_set_errors"],
        )
        removed = self.intercepted_check(self.make_tree(self.pinned()[:-1]))
        self.assertEqual("FAIL", removed["status"])
        self.assertEqual(
            run_phase0_gate.TEST_FILE_SET_MISMATCH_REASON,
            removed["details"]["reason"],
        )
        renamed = self.intercepted_check(
            self.make_tree(self.pinned()[:-1] + ["test_zz_renamed.py"])
        )
        self.assertEqual("FAIL", renamed["status"])

    def test_nested_discovery_surface_under_tests_ci_is_intercepted(self) -> None:
        # A package directory (or a top-level __init__.py) re-opens discovery
        # to files outside the pinned top-level set, so both are refused.
        root = self.make_tree(self.pinned())
        package = root / "Tests" / "ci" / "evil_pkg"
        package.mkdir()
        (package / "__init__.py").write_text("", encoding="utf-8")
        (package / "test_evil.py").write_text("", encoding="utf-8")
        errors = run_phase0_gate._ci_test_file_set_errors(root)
        self.assertIn("unpinned discovery surface: evil_pkg/__init__.py", errors)
        self.assertIn("unpinned discovery surface: evil_pkg/test_evil.py", errors)
        top_level = self.make_tree(self.pinned() + ["__init__.py"])
        self.assertIn(
            "unpinned discovery surface: __init__.py",
            run_phase0_gate._ci_test_file_set_errors(top_level),
        )
        # A symlinked directory lets discovery escape what the walk can see,
        # so its mere presence is refused without inspecting the target.
        linked = self.make_tree(self.pinned())
        outside = Path(tempfile.mkdtemp(prefix="ci-file-set-outside-"))
        self.addCleanup(shutil.rmtree, outside, ignore_errors=True)
        (outside / "__init__.py").write_text("", encoding="utf-8")
        (outside / "test_evil.py").write_text("", encoding="utf-8")
        os.symlink(outside, linked / "Tests" / "ci" / "evil_link")
        self.assertIn(
            "unpinned discovery surface: symlinked directory evil_link",
            run_phase0_gate._ci_test_file_set_errors(linked),
        )
        self.assertEqual("FAIL", self.intercepted_check(root)["status"])

    def test_file_set_mismatch_never_enters_fingerprint_matching(self) -> None:
        # Even a log crafted byte-exact against the registered fingerprint is
        # refused once the parent flagged the file set: the reason lives in
        # parent-authored details that no child output can influence.
        entry = entry_for(
            "phase0-adversarial-unit-tests",
            unittest_failure_fingerprint(UNITTEST_LOG),
            batch="R0-B",
        )
        forged = check_for(
            "phase0-adversarial-unit-tests",
            "FAIL",
            UNITTEST_LOG,
            details={"reason": run_phase0_gate.TEST_FILE_SET_MISMATCH_REASON},
        )
        state = state_for(entries=[entry], baseline_entries=[entry])
        overall, baseline_red = run_pipeline([forged], state)
        self.assertEqual("FAIL", overall)
        self.assertEqual("BLOCKED", baseline_red["result"])
        self.assertEqual(
            run_phase0_gate.TEST_FILE_SET_MISMATCH_REASON,
            baseline_red["entries"][0]["verdict"],
        )
        self.assertIsNone(baseline_red["entries"][0]["observed_fingerprint"])
        self.assertTrue(
            any(
                run_phase0_gate.TEST_FILE_SET_MISMATCH_REASON in reason
                for reason in baseline_red["block_reasons"]
            )
        )
        # The identical log without the parent-authored reason still matches:
        # the refusal is keyed on the parent's verdict, not on log content.
        honest = check_for("phase0-adversarial-unit-tests", "FAIL", UNITTEST_LOG)
        overall_honest, _ = run_pipeline([honest], state_for([entry], [entry]))
        self.assertEqual(OVERALL_PASS_WITH_REGISTERED_BASELINE, overall_honest)

    def test_symlinked_test_file_is_refused_whatever_its_name(self) -> None:
        # P0-A: enumerating with "is_file() and not is_symlink()" silently
        # dropped symlinks from the observed set, so a symlink carrying an
        # UNPINNED name was neither an extra top-level file nor a nested
        # discovery surface -- discovery still imported it.  The same
        # condition closed the pinned-name side, so the bug was one-sided.
        # Any top-level entry that is a symlink is now refused outright, the
        # way run_candidate_gate._test_tree_sha256 refuses symlinked entries.
        outside = Path(tempfile.mkdtemp(prefix="ci-file-set-payload-"))
        self.addCleanup(shutil.rmtree, outside, ignore_errors=True)
        payload = outside / "payload.py"
        payload.write_text("", encoding="utf-8")

        unpinned = self.make_tree(self.pinned())
        os.symlink(payload, unpinned / "Tests" / "ci" / "test_zz_symlink.py")
        self.assertIn(
            "unpinned discovery surface: symlinked file test_zz_symlink.py",
            run_phase0_gate._ci_test_file_set_errors(unpinned),
        )
        refused = self.intercepted_check(unpinned)
        self.assertEqual("FAIL", refused["status"])
        self.assertEqual(
            run_phase0_gate.TEST_FILE_SET_MISMATCH_REASON,
            refused["details"]["reason"],
        )

        # A pinned name replaced by a symlink names the symlink explicitly
        # instead of only reporting the file as missing.
        swapped = self.make_tree(self.pinned())
        pinned_name = self.pinned()[0]
        (swapped / "Tests" / "ci" / pinned_name).unlink()
        os.symlink(payload, swapped / "Tests" / "ci" / pinned_name)
        self.assertIn(
            "unpinned discovery surface: symlinked file " + pinned_name,
            run_phase0_gate._ci_test_file_set_errors(swapped),
        )
        self.assertEqual("FAIL", self.intercepted_check(swapped)["status"])

        # Tests/ci is sys.path[0] during discovery, so a symlink that is not
        # named test_*.py is an import surface too.
        helper = self.make_tree(self.pinned())
        os.symlink(payload, helper / "Tests" / "ci" / "sitecustomize.py")
        self.assertIn(
            "unpinned discovery surface: symlinked file sitecustomize.py",
            run_phase0_gate._ci_test_file_set_errors(helper),
        )
        self.assertEqual("FAIL", self.intercepted_check(helper)["status"])

    def test_unpinned_importable_top_level_file_is_refused(self) -> None:
        # Same class of hole as P0-A/P0-B: the pinned set is a set of NAMES,
        # while discovery's import surface is every importable suffix in
        # Tests/ci (sys.path[0]).  Tests/ci/json.py shadows the stdlib module
        # a pinned test imports without ever matching test_*.py.  Inert data
        # files (.json/.md) stay allowed: no import hook loads them.
        for name in ("json.py", "helper.so", "payload.pth", "bundle.zip"):
            tree = self.make_tree(self.pinned())
            (tree / "Tests" / "ci" / name).write_text("", encoding="utf-8")
            self.assertIn(
                "unpinned discovery surface: importable file " + name,
                run_phase0_gate._ci_test_file_set_errors(tree),
                name,
            )
            self.assertEqual("FAIL", self.intercepted_check(tree)["status"], name)
        allowed = self.make_tree(self.pinned())
        (allowed / "Tests" / "ci" / "README.md").write_text("", encoding="utf-8")
        (allowed / "Tests" / "ci" / "fixture.json").write_text("", encoding="utf-8")
        self.assertEqual([], run_phase0_gate._ci_test_file_set_errors(allowed))


class CiTestBytecodeShadowTests(unittest.TestCase):
    """P0-B: the pinned NAME set is not the executed BYTE set.

    A PEP 552 unchecked-hash .pyc records no usable source hash and is never
    revalidated, so Tests/ci/__pycache__/test_x.cpython-312.pyc replaces the
    whole import of test_x.py while the honest source stays byte-identical
    and the pinned name set still matches.  Two mechanical closures, both
    parent-side facts: stray sourceless bytecode is refused by enumeration,
    and the suite child is launched with -X pycache_prefix pointing at a
    fresh empty directory so no in-repository __pycache__ is ever consulted.
    """

    PAYLOAD_MARKER = "PYC-PAYLOAD-EXECUTED-INSTEAD-OF-SOURCE"
    HONEST_MARKER = "HONEST-SOURCE-EXECUTED"

    def make_tree(self, names: Sequence[str]) -> Path:
        root = Path(tempfile.mkdtemp(prefix="ci-pyc-shadow-"))
        self.addCleanup(shutil.rmtree, root, ignore_errors=True)
        directory = root / "Tests" / "ci"
        directory.mkdir(parents=True)
        for name in names:
            (directory / name).write_text("", encoding="utf-8")
        return root

    def pinned(self) -> List[str]:
        return sorted(run_phase0_gate.PHASE0_PINNED_CI_TEST_FILES)

    def suite_command(self) -> List[str]:
        """The exact argv run_phase0_unittests hands to the suite child."""

        captured: List[List[str]] = []

        class _Stop(Exception):
            pass

        def record(command, cwd, timeout_seconds=None, env=None):
            captured.append(list(command))
            raise _Stop()

        with mock.patch.object(run_phase0_gate, "run_command", side_effect=record):
            with self.assertRaises(_Stop):
                run_phase0_gate.run_phase0_unittests(COMMIT)
        self.assertEqual(1, len(captured))
        return captured[0]

    def write_unchecked_hash_pyc(self, source: Path, cache: Path) -> None:
        """Compile source into cache in PEP 552 UNCHECKED_HASH mode."""

        import py_compile

        cache.parent.mkdir(parents=True, exist_ok=True)
        py_compile.compile(
            str(source),
            cfile=str(cache),
            dfile=source.name,
            invalidation_mode=py_compile.PycInvalidationMode.UNCHECKED_HASH,
            doraise=True,
        )
        flags = int.from_bytes(cache.read_bytes()[4:8], "little")
        # bit0 = hash-based, bit1 = check_source.  Unchecked hash is 0b01.
        self.assertEqual(1, flags, "pyc must be hash-based and unchecked")

    def test_suite_child_never_reads_an_in_repository_pycache(self) -> None:
        command = self.suite_command()
        prefix = command[: command.index("-m")]
        flags = [value for value in prefix if value.startswith("pycache_prefix=")]
        self.assertEqual(1, len(flags), prefix)
        self.assertEqual("-X", prefix[prefix.index(flags[0]) - 1])
        cache_root = Path(flags[0].split("=", 1)[1])
        self.assertTrue(cache_root.is_absolute(), cache_root)
        self.assertTrue(cache_root.is_dir(), cache_root)
        self.assertEqual([], sorted(cache_root.iterdir()), cache_root)
        self.assertNotIn(
            run_phase0_gate.ROOT.resolve(), cache_root.resolve().parents
        )

        # End-to-end: with exactly this flag shape the shadow pyc loses.
        work = Path(tempfile.mkdtemp(prefix="ci-pyc-e2e-"))
        self.addCleanup(shutil.rmtree, work, ignore_errors=True)
        body = (
            "import unittest\nprint({marker!r})\n"
            "class T(unittest.TestCase):\n    def test_ok(self): pass\n"
        )
        honest = work / "test_shadow.py"
        honest.write_text(body.format(marker=self.HONEST_MARKER), encoding="utf-8")
        payload_source = work / "payload_source.py"
        payload_source.write_text(
            body.format(marker=self.PAYLOAD_MARKER), encoding="utf-8"
        )
        tag = sys.implementation.cache_tag
        self.write_unchecked_hash_pyc(
            payload_source, work / "__pycache__" / ("test_shadow.%s.pyc" % tag)
        )
        honest_bytes = honest.read_bytes()

        import subprocess

        def discover(extra: Sequence[str]) -> str:
            completed = subprocess.run(
                [
                    *prefix[:1],
                    *[value for value in prefix[1:] if not value.startswith("pycache_prefix=")
                      and value != "-X"],
                    *extra,
                    "-m",
                    "unittest",
                    "discover",
                    "-s",
                    str(work),
                    "-p",
                    "test_shadow.py",
                ],
                capture_output=True,
                text=True,
                timeout=120,
            )
            return completed.stdout + completed.stderr

        # Control: without the flag the unchecked-hash pyc wins outright.
        unguarded = discover([])
        self.assertIn(self.PAYLOAD_MARKER, unguarded)
        self.assertNotIn(self.HONEST_MARKER, unguarded)
        # Guarded: the honest source is compiled and executed instead.
        redirected = Path(tempfile.mkdtemp(prefix="ci-pyc-redirect-"))
        self.addCleanup(shutil.rmtree, redirected, ignore_errors=True)
        guarded = discover(["-X", "pycache_prefix=" + str(redirected)])
        self.assertIn(self.HONEST_MARKER, guarded)
        self.assertNotIn(self.PAYLOAD_MARKER, guarded)
        # The honest carrier was never touched: name pinning saw nothing.
        self.assertEqual(honest_bytes, honest.read_bytes())

    def test_sourceless_bytecode_under_tests_ci_is_refused(self) -> None:
        # __pycache__ entries are neutralised by pycache_prefix, but a .pyc
        # sitting directly in Tests/ci is a SourcelessFileLoader surface that
        # pycache_prefix does not govern: Tests/ci/json.pyc answers the
        # `import json` of a pinned test.  Refuse it by enumeration.
        for name in ("json.pyc", "json.pyo", "fixtures/nested.pyc"):
            tree = self.make_tree(self.pinned())
            target = tree / "Tests" / "ci" / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b"")
            self.assertIn(
                "unpinned discovery surface: sourceless bytecode " + name,
                run_phase0_gate._ci_test_file_set_errors(tree),
                name,
            )
        # Ordinary __pycache__ bytecode is NOT refused: static-ci.yml runs a
        # discover over Tests/ci before the gate, so refusing it would fail
        # the gate on its own workflow.  pycache_prefix is what closes it.
        cached = self.make_tree(self.pinned())
        cache_dir = cached / "Tests" / "ci" / "__pycache__"
        cache_dir.mkdir()
        (cache_dir / ("test_module_impact.%s.pyc" % sys.implementation.cache_tag)).write_bytes(b"")
        self.assertEqual([], run_phase0_gate._ci_test_file_set_errors(cached))


class TrustRootPinTests(unittest.TestCase):
    def guard_test_names(self) -> List[str]:
        names: set = set()
        for value in globals().values():
            if (
                isinstance(value, type)
                and issubclass(value, unittest.TestCase)
                and value.__module__ == __name__
            ):
                names.update(
                    name for name in vars(value) if name.startswith("test_")
                )
        return sorted(names)

    def test_guard_suite_methods_are_pinned_into_the_gate_inventory(self) -> None:
        # MF5: the guard suite guards the ledger, so deleting or renaming any
        # of its tests must fail the phase0-adversarial-unit-tests inventory.
        names = self.guard_test_names()
        self.assertGreaterEqual(len(names), 60)
        missing = [
            name
            for name in names
            if name not in PHASE0_REQUIRED_UNITTEST_INVENTORY
        ]
        self.assertEqual([], missing)
        self.assertEqual(167 + len(names), PHASE0_MINIMUM_ADVERSARIAL_TESTS)

    def test_ledger_and_guard_suite_are_candidate_trust_paths(self) -> None:
        self.assertIn(
            "Tests/ci/test_baseline_red_ledger.py",
            run_candidate_gate.CANDIDATE_TRUST_PATHS,
        )
        self.assertIn(
            "governance/baseline-red-ledger.json",
            run_candidate_gate.CANDIDATE_TRUST_PATHS,
        )

    def test_workflow_baseline_expression_covers_merge_group(self) -> None:
        # MF7: merge_group runs previously evaluated the baseline expression
        # to empty, collapsing into the HEAD fallback inside the gate.
        workflow = (ROOT / ".github" / "workflows" / "static-ci.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn(
            "DPS_BASELINE_COMMIT: ${{ github.event.pull_request.base.sha "
            "|| github.event.merge_group.base_sha || github.event.before }}",
            workflow,
        )

    @staticmethod
    def _fake_git_output(head_commit: str):
        def fake(root: Path, args: Sequence[str]) -> str:
            joined = " ".join(args)
            if joined == "rev-parse HEAD":
                return head_commit
            if joined == "rev-parse HEAD^{commit}":
                return head_commit
            if joined == "rev-parse " + head_commit + "^{commit}":
                return head_commit
            if joined == "rev-parse " + COMMIT + "^{commit}":
                return COMMIT
            raise AssertionError("unexpected git call: " + joined)

        return fake

    def test_resolve_baseline_marks_the_head_fallback_non_authoritative(self) -> None:
        environment = {
            key: value
            for key, value in os.environ.items()
            if key not in ("DPS_BASELINE_COMMIT", "GITHUB_BASE_SHA")
        }
        head_commit = "b" * 40
        with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(
            run_phase0_gate, "git_output", self._fake_git_output(head_commit)
        ):
            explicit_commit, explicit_authoritative = resolve_baseline(COMMIT)
            self.assertEqual(COMMIT, explicit_commit)
            self.assertTrue(explicit_authoritative)
            for fallback_input in (None, "", "0" * 40):
                commit, authoritative = resolve_baseline(fallback_input)
                self.assertFalse(authoritative)
                self.assertEqual(head_commit, commit)

    def test_explicit_base_equal_to_head_is_not_authoritative(self) -> None:
        # CF-P2: an explicit --base (or environment base) that resolves to the
        # current HEAD commit is the HEAD self-comparison wearing an
        # authoritative costume; it must never count as drift authority.
        environment = {
            key: value
            for key, value in os.environ.items()
            if key not in ("DPS_BASELINE_COMMIT", "GITHUB_BASE_SHA")
        }
        head_commit = "c" * 40
        with mock.patch.dict(os.environ, environment, clear=True), mock.patch.object(
            run_phase0_gate, "git_output", self._fake_git_output(head_commit)
        ):
            for explicit_base in ("HEAD", head_commit):
                commit, authoritative = resolve_baseline(explicit_base)
                self.assertEqual(head_commit, commit)
                self.assertFalse(authoritative)
            environment_head = dict(os.environ)
            environment_head["DPS_BASELINE_COMMIT"] = head_commit
            with mock.patch.dict(os.environ, environment_head, clear=True):
                _commit, env_authoritative = resolve_baseline(None)
                self.assertFalse(env_authoritative)
        with mock.patch.dict(os.environ, environment, clear=True):
            # Real repository smoke check, no mocks: --base HEAD never grants
            # authority regardless of the commit it lands on.
            _real_commit, real_authoritative = resolve_baseline("HEAD")
            self.assertFalse(real_authoritative)
        # scripts/release.sh self-compares by design (--base "$head_commit");
        # it stays safe because --require-literal-pass refuses everything but
        # the literal PASS, so losing drift authority changes nothing there.
        release = (ROOT / "scripts" / "release.sh").read_text(encoding="utf-8")
        self.assertIn('phase0_arguments=(--base "$head_commit")', release)
        self.assertIn("phase0_arguments+=(--require-literal-pass)", release)

    def test_module_docstring_states_the_machine_protection_boundaries(self) -> None:
        # MF8: the docstring must not overclaim.  It names the literal-PASS
        # boundary, the unprotected self-edit surface, and Owner-only reseeds.
        docstring = run_phase0_gate.__doc__ or ""
        self.assertIn("PASS_WITH_REGISTERED_BASELINE", docstring)
        self.assertIn("--require-literal-pass", docstring)
        self.assertIn("not on a machine\n  guarantee", docstring)
        self.assertIn("Owner-merged", docstring)

    def test_release_script_pins_the_literal_pass_flag(self) -> None:
        release = (ROOT / "scripts" / "release.sh").read_text(encoding="utf-8")
        self.assertIn("phase0_arguments+=(--require-literal-pass)", release)
        self.assertEqual(
            [], phase0_module._release_validation_allowlist_errors(release)
        )

    def test_dropping_the_literal_pass_flag_breaks_the_release_allowlist(self) -> None:
        # MF9: scripts/release.sh used to trust the gate exit code alone, so a
        # registered red sailed through to "validation passed".
        release = (ROOT / "scripts" / "release.sh").read_text(encoding="utf-8")
        stripped = release.replace("phase0_arguments+=(--require-literal-pass)\n", "")
        self.assertNotEqual(release, stripped)
        errors = phase0_module._release_validation_allowlist_errors(stripped)
        self.assertTrue(
            any("--require-literal-pass" in error for error in errors), errors
        )

    def test_gate_exit_requires_literal_pass_when_flagged(self) -> None:
        self.assertEqual(0, gate_exit_code("PASS", False))
        self.assertEqual(0, gate_exit_code("PASS", True))
        self.assertEqual(
            0, gate_exit_code(OVERALL_PASS_WITH_REGISTERED_BASELINE, False)
        )
        self.assertEqual(
            1, gate_exit_code(OVERALL_PASS_WITH_REGISTERED_BASELINE, True)
        )
        self.assertEqual(1, gate_exit_code("FAIL", False))
        self.assertEqual(1, gate_exit_code("FAIL", True))


if __name__ == "__main__":
    unittest.main()
