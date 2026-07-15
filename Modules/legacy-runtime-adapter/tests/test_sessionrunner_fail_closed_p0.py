#!/usr/bin/env python3
"""Static regressions for the F5 P0 SessionRunner containment boundary.

These tests execute no C#, Windows, ZennoDroid, ADB, GBrain, or device action.
They prove only that the reviewed source shape remains fail-closed.
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
SOURCE_PATH = REPOSITORY_ROOT / "Modules" / "SessionRunner.cs"
TECH_MANUAL_PATH = REPOSITORY_ROOT / "Docs" / "TechManual_技术手册.md"

ENTRY_DECLARATIONS = {
    "Run": "public static string Run(object projectObj, object instanceObj)",
    "InitSession": "public static string InitSession(object projectObj, object instanceObj)",
    "DecideNextAction": "public static string DecideNextAction(object projectObj, object instanceObj)",
    "EvaluateActionResult": "public static string EvaluateActionResult(object projectObj, object instanceObj)",
    "FinalizeSession": "public static string FinalizeSession(object projectObj, object instanceObj)",
}


class SessionRunnerFailClosedP0Tests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source_bytes = SOURCE_PATH.read_bytes()
        cls.source = cls.source_bytes.decode("utf-8")

    def _method_prefix(self, declaration: str, length: int = 900) -> str:
        start = self.source.index(declaration)
        return self.source[start : start + length]

    def test_legacy_encoding_profile_and_frozen_declarations(self) -> None:
        self.assertFalse(self.source_bytes.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\r\n", self.source_bytes)
        self.assertGreater(self.source_bytes.count(b"\n"), 0)
        for declaration in ENTRY_DECLARATIONS.values():
            with self.subTest(declaration=declaration):
                self.assertEqual(1, self.source.count(declaration))

    def test_all_production_entrypoints_gate_before_legacy_logic(self) -> None:
        for method, declaration in ENTRY_DECLARATIONS.items():
            with self.subTest(method=method):
                prefix = self._method_prefix(declaration)
                init = prefix.index("CoreHelper.Init(projectObj, instanceObj);")
                gate = prefix.index("if (!HasVerifiedModernExecutionBridge())")
                stop = prefix.index("return StopUnverifiedLegacyExecution(")
                self.assertLess(init, gate)
                self.assertLess(gate, stop)

    def test_bridge_is_explicitly_unavailable_and_stop_state_is_unknown(self) -> None:
        bridge = re.search(
            r"private static bool HasVerifiedModernExecutionBridge\(\)\s*"
            r"\{\s*return false;\s*\}",
            self.source,
        )
        self.assertIsNotNone(bridge)
        stop_start = self.source.index("private static string StopUnverifiedLegacyExecution")
        stop_end = self.source.index("private static bool ContainsLegacyStepDsl", stop_start)
        stop_body = self.source[stop_start:stop_end]
        self.assertIn(
            'private const string LEGACY_FAILURE_STEP = "ERROR_BRIDGE_REQUIRED";',
            self.source,
        )
        required = (
            'CoreHelper.SetVar(VK.ZdStepPlan, "");',
            "CoreHelper.SetVar(VK.ZdStepType, LEGACY_FAILURE_STEP);",
            'CoreHelper.SetVar(VK.ZdSelectorKey, "");',
            'CoreHelper.SetVar(VK.ZdSwipeDuration, "0");',
            'CoreHelper.SetVar(VK.ZdFound, "false");',
            'CoreHelper.SetVar(VK.ZdActionResult, "UNKNOWN");',
            'CoreHelper.SetVar(VK.ActionCount, "0");',
            'CoreHelper.SetVar(VK.SuccessCount, "0");',
            'CoreHelper.SetVar(VK.LegacyRunCompleted, "false");',
            'CoreHelper.SetVar(VK.LegacyRunResult, "ERROR: " + MODERN_BRIDGE_REQUIRED);',
            'CoreHelper.SetVar("action_count", "0");',
            'CoreHelper.SetVar("action_attempt_count", "0");',
            'CoreHelper.SetVar("session_successful_actions", "0");',
            'CoreHelper.SetVar("session_success_rate", "0.0000");',
            'CoreHelper.SetVar("action_result", "ERROR");',
            'CoreHelper.SetVar("execution_proposal", "");',
            'CoreHelper.SetVar("recovery_proposal", "");',
            'CoreHelper.SetVar("ai_direct_execution", "false");',
            'CoreHelper.SetVar("run_result", "ERROR");',
            'CoreHelper.SetVar("session_result", "ERROR");',
        )
        for statement in required:
            self.assertIn(statement, stop_body)
        self.assertNotIn('CoreHelper.SetVar(VK.ZdStepType, "DONE");', stop_body)
        failure_write = "CoreHelper.SetVar(VK.ZdStepType, LEGACY_FAILURE_STEP);"
        self.assertEqual(2, stop_body.count(failure_write))
        first_failure = stop_body.index(failure_write)
        log_call = stop_body.index("CoreHelper.LogErr(TAG", first_failure)
        final_failure = stop_body.rindex(failure_write)
        return_value = stop_body.index("return returnValue;", final_failure)
        self.assertLess(first_failure, log_call)
        self.assertLess(log_call, final_failure)
        self.assertLess(final_failure, return_value)
        self.assertRegex(
            stop_body[first_failure:final_failure],
            r"try\s*\{\s*CoreHelper\.LogErr\(TAG,.*?\);\s*\}\s*"
            r"catch\s*\(Exception\)\s*\{[^}]*\}",
        )

    def test_failure_token_routes_to_wrapper_default_bad_end_not_good_end(self) -> None:
        manual = TECH_MANUAL_PATH.read_text(encoding="utf-8")
        switch_start = manual.index("DPS_DecideAction_OwnCode")
        switch_end = manual.index("DPS_Finalize_OwnCode", switch_start)
        switch_contract = manual[switch_start:switch_end]
        self.assertIn("DONE   -> [GoodEnd", switch_contract)
        self.assertIn("Default-> [BadEnd", switch_contract)
        self.assertNotIn("ERROR_BRIDGE_REQUIRED", switch_contract)
        self.assertIn("ERROR_EMPTY_PLAN -> Default -> BadEnd", manual)

        decide_prefix = self._method_prefix(ENTRY_DECLARATIONS["DecideNextAction"])
        evaluate_prefix = self._method_prefix(ENTRY_DECLARATIONS["EvaluateActionResult"])
        self.assertIn(
            'StopUnverifiedLegacyExecution("DecideNextAction", LEGACY_FAILURE_STEP)',
            decide_prefix,
        )
        self.assertIn(
            'StopUnverifiedLegacyExecution("EvaluateActionResult", LEGACY_FAILURE_STEP)',
            evaluate_prefix,
        )

    def test_writable_step_dsl_cannot_execute_or_auto_pass(self) -> None:
        dsl_start = self.source.index("private static bool ContainsLegacyStepDsl")
        dsl_end = self.source.index("// ==================== SessionState", dsl_start)
        dsl_body = self.source[dsl_start:dsl_end]
        self.assertIn("for (int i = 0; i < steps.Length; i++)", dsl_body)
        for token in ("L", "T", "S", "W", "B", "V"):
            self.assertIn('stepType == "{0}"'.format(token), dsl_body)
        self.assertIn("DecideNextAction legacy DSL rejected", self.source)
        self.assertIn("EvaluateActionResult legacy DSL rejected", self.source)
        self.assertIn("FinalizeSession legacy DSL rejected", self.source)
        forbidden = (
            "DSL 模式自动通过",
            "isDslTestMode",
            "requiredSuccessRate = 0.5",
            "minSuccessfulActions = 1",
        )
        for value in forbidden:
            self.assertNotIn(value, self.source)

    def test_missing_or_unknown_native_result_fails_closed(self) -> None:
        self.assertIn(
            'if (string.IsNullOrEmpty(actionResult) || actionResult == "UNKNOWN")',
            self.source,
        )
        self.assertIn("EvaluateActionResult missing native result", self.source)
        normalize_start = self.source.index(
            "private static string NormalizeActionResult(string raw)"
        )
        normalize_end = self.source.index(
            "private static int ParseActionDuration", normalize_start
        )
        normalize_body = self.source[normalize_start:normalize_end]
        self.assertIn("return raw;", normalize_body)
        self.assertNotIn(".Trim()", normalize_body)
        self.assertNotIn("IndexOf(':')", normalize_body)
        self.assertNotIn("Substring(", normalize_body)
        evaluate_start = self.source.index(
            ENTRY_DECLARATIONS["EvaluateActionResult"]
        )
        evaluate_end = self.source.index(
            "private static string NormalizeActionResult", evaluate_start
        )
        evaluate_body = self.source[evaluate_start:evaluate_end]
        exception_body = evaluate_body[evaluate_body.rindex("catch (Exception ex)") :]
        self.assertNotIn('return "CONTINUE";', exception_body)
        self.assertIn("EvaluateActionResult exception", exception_body)

    def test_vision_recovery_and_direct_side_effects_are_absent(self) -> None:
        forbidden = (
            "ActionExecutor.Execute(",
            "VisionCorrector.VerifyError(",
            "VisionCorrector.CorrectWithRetry(",
            "SendKeyCode(",
            ".Shell(",
            ".Tap(",
            ".Swipe(",
            "System.Diagnostics.Process.Start(",
            'actionResult = "SUCCESS";',
        )
        for value in forbidden:
            with self.subTest(value=value):
                self.assertNotIn(value, self.source)
        self.assertIn('"error_preserved"', self.source)
        self.assertIn("recovery proposal blocked", self.source)

    def test_legacy_executor_is_proposal_only(self) -> None:
        start = self.source.index("private static string ExecuteWithUnifiedEngine")
        end = self.source.index("private static void LoadUserStrategy", start)
        body = self.source[start:end]
        self.assertIn("Execution proposal blocked", body)
        self.assertIn('CoreHelper.SetVar("action_result", "ERROR");', body)
        self.assertIn('return "ERROR:authorized_execution_bridge_required";', body)

    def test_unverified_legacy_results_cannot_write_memory_or_spoken_facts(self) -> None:
        forbidden = (
            "MemoryManager.RecordInteraction(",
            "AppendMemoryEntry(",
            "CoreHelper.WriteFileAtomic(",
            "CoreHelper.WriteFile(",
            'CoreHelper.SetVar("spoken"',
        )
        for value in forbidden:
            self.assertNotIn(value, self.source)
        self.assertIn("verified receipt required", self.source)


if __name__ == "__main__":
    unittest.main()
