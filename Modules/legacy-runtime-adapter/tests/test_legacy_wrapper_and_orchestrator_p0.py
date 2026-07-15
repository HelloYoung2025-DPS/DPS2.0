#!/usr/bin/env python3
"""Static P0 regressions for legacy wrappers and SmartOrchestrator.

No C#, Windows, ZennoDroid, ADB, GBrain, or device action is executed here.
"""

from __future__ import annotations

import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[3]
WRAPPERS = (
    "ZDProjects/SessionRunner_OwnCode.cs",
    "ZDProjects/DPS_Init_OwnCode.cs",
    "ZDProjects/DPS_DecideAction_OwnCode.cs",
    "ZDProjects/DPS_CheckResult_OwnCode.cs",
    "ZDProjects/DPS_Finalize_OwnCode.cs",
    "ZDProjects/Initializer_OwnCode.cs",
    "ZDProjects/Main_OwnCode.cs",
)
ORCHESTRATOR = REPOSITORY_ROOT / "Modules" / "Core" / "SmartOrchestrator.cs"


class LegacyWrapperFailClosedTests(unittest.TestCase):
    def test_compile_time_gate_returns_before_any_dynamic_loader_input(self) -> None:
        dangerous_tokens = (
            'project.Variables["project_root"]',
            "Func<string, string, object[], object> RunModule",
            "System.IO.File.Exists(",
            "System.IO.File.ReadAllText(",
            "System.IO.Directory.GetFiles(",
            ".GetExportedTypes()",
        )
        for relative in WRAPPERS:
            with self.subTest(path=relative):
                source = (REPOSITORY_ROOT / relative).read_text(encoding="utf-8")
                declaration = source.index(
                    "const bool LEGACY_DISABLE_NEW_COMMANDS = true;"
                )
                gate = source.index("if (LEGACY_DISABLE_NEW_COMMANDS)", declaration)
                failure_token = source.index(
                    'const string LEGACY_BAD_END_TOKEN = "ERROR_BRIDGE_REQUIRED";'
                )
                stop = source.index("return LEGACY_BAD_END_TOKEN;", gate)
                self.assertLess(declaration, gate)
                self.assertLess(failure_token, gate)
                self.assertLess(gate, stop)
                for token in dangerous_tokens:
                    if token in source:
                        self.assertLess(stop, source.index(token), token)

    def test_gate_cannot_be_disabled_by_project_state_and_clears_success(self) -> None:
        required = (
            'SetGateVariable("zd_step_plan", "");',
            'SetGateVariable("zd_step_type", LEGACY_BAD_END_TOKEN);',
            'SetGateVariable("zd_selector_key", "");',
            'SetGateVariable("zd_swipe_duration", "0");',
            'SetGateVariable("zd_found", "false");',
            'SetGateVariable("zd_action_result", "UNKNOWN");',
            'SetGateVariable("sr_action_count", "0");',
            'SetGateVariable("sr_success_count", "0");',
            'SetGateVariable("sr_use_legacy_run", "false");',
            'SetGateVariable("action_count", "0");',
            'SetGateVariable("action_attempt_count", "0");',
            'SetGateVariable("session_successful_actions", "0");',
            'SetGateVariable("session_failed_actions", "0");',
            'SetGateVariable("session_skipped_actions", "0");',
            'SetGateVariable("session_success_rate", "0.0000");',
            'SetGateVariable("action_result", "ERROR");',
            'SetGateVariable("execution_proposal", "");',
            'SetGateVariable("recovery_proposal", "");',
            'SetGateVariable("ai_direct_execution", "false");',
            'SetGateVariable("sr_legacy_run_completed", "false");',
            'SetGateVariable("run_result", "ERROR");',
            'SetGateVariable("session_result", "ERROR");',
        )
        for relative in WRAPPERS:
            with self.subTest(path=relative):
                source = (REPOSITORY_ROOT / relative).read_text(encoding="utf-8")
                stop = source.index("return LEGACY_BAD_END_TOKEN;")
                prefix = source[:stop]
                self.assertEqual(
                    1, source.count("const bool LEGACY_DISABLE_NEW_COMMANDS = true;")
                )
                self.assertNotIn('Variables["legacy_disable_new_commands"]', source)
                self.assertNotIn("LEGACY_DISABLE_NEW_COMMANDS = false", source)
                self.assertNotIn('SetGateVariable("zd_step_type", "DONE")', prefix)
                for statement in required:
                    self.assertIn(statement, prefix)

    def test_missing_variable_cannot_skip_later_cleanup_or_failure_token(self) -> None:
        for relative in WRAPPERS:
            with self.subTest(path=relative):
                source = (REPOSITORY_ROOT / relative).read_text(encoding="utf-8")
                stop = source.index("return LEGACY_BAD_END_TOKEN;")
                prefix = source[:stop]
                self.assertIn(
                    "System.Action<string, string> SetGateVariable", prefix
                )
                self.assertIn("catch (System.Exception)", prefix)
                self.assertIn("project.Variables[name].Value = value;", prefix)
                self.assertEqual(
                    2,
                    prefix.count(
                        'SetGateVariable("zd_step_type", LEGACY_BAD_END_TOKEN);'
                    ),
                )
                first_token = prefix.index(
                    'SetGateVariable("zd_step_type", LEGACY_BAD_END_TOKEN);'
                )
                middle = prefix.index('SetGateVariable("zd_selector_key", "");')
                later_success = prefix.index(
                    'SetGateVariable("session_successful_actions", "0");'
                )
                final_token = prefix.rindex(
                    'SetGateVariable("zd_step_type", LEGACY_BAD_END_TOKEN);'
                )
                self.assertLess(first_token, middle)
                self.assertLess(middle, later_success)
                self.assertLess(later_success, final_token)

    def test_legacy_encoding_profiles_are_preserved(self) -> None:
        for relative in WRAPPERS:
            with self.subTest(path=relative):
                value = (REPOSITORY_ROOT / relative).read_bytes()
                self.assertFalse(value.startswith(b"\xef\xbb\xbf"))
                if relative in {
                    "ZDProjects/SessionRunner_OwnCode.cs",
                    "ZDProjects/Initializer_OwnCode.cs",
                    "ZDProjects/Main_OwnCode.cs",
                }:
                    self.assertGreater(value.count(b"\r\n"), 0)
                    self.assertEqual(0, value.count(b"\n") - value.count(b"\r\n"))
                else:
                    self.assertNotIn(b"\r\n", value)
                    self.assertGreater(value.count(b"\n"), 0)


class SmartOrchestratorFailClosedTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source_bytes = ORCHESTRATOR.read_bytes()
        cls.source = cls.source_bytes.decode("utf-8")
        start = cls.source.index("public OperationVerdict EvaluateResult(")
        end = cls.source.index("public RecoveryLevel DecideRecovery()", start)
        cls.body = cls.source[start:end]

    def test_only_exact_success_reaches_business_postcondition(self) -> None:
        exact_guard = self.body.index('if (executionResult != "SUCCESS")')
        failure_return = self.body.index(
            "return OperationVerdict.ExecutionFailed;", exact_guard
        )
        postcondition = self.body.index("// 第 2 层: 业务成功判定")
        success_return = self.body.index("return OperationVerdict.Success;")
        self.assertLess(exact_guard, failure_return)
        self.assertLess(failure_return, postcondition)
        self.assertLess(postcondition, success_return)
        self.assertEqual(1, self.body.count("return OperationVerdict.Success;"))
        self.assertIn("if (string.IsNullOrEmpty(expectedPage))", self.body)
        self.assertIn(
            'if (string.IsNullOrEmpty(actualPage) || actualPage == "unknown" || actualPage != expectedPage)',
            self.body,
        )
        self.assertEqual(
            2, self.body.count("return OperationVerdict.BusinessFailed;")
        )
        self.assertLess(
            self.body.rindex("return OperationVerdict.BusinessFailed;"), success_return
        )
        for unsafe_shape in (
            'Contains("SUCCESS")',
            'StartsWith("SUCCESS")',
            'EndsWith("SUCCESS")',
            'IndexOf("SUCCESS")',
        ):
            self.assertNotIn(unsafe_shape, self.body)

    def test_failure_tokens_return_before_false_success_counter(self) -> None:
        exact_guard = self.body.index('if (executionResult != "SUCCESS")')
        failure_return = self.body.index(
            "return OperationVerdict.ExecutionFailed;", exact_guard
        )
        counter = self.body.index("_totalFalseSuccesses++;")
        self.assertLess(failure_return, counter)
        for token in ("FAILED", "TIMEOUT", "UNKNOWN", "SUCCESS ", "SUCCESS:detail"):
            with self.subTest(token=token):
                self.assertNotEqual("SUCCESS", token)

    def test_legacy_encoding_profile_is_preserved(self) -> None:
        self.assertFalse(self.source_bytes.startswith(b"\xef\xbb\xbf"))
        self.assertNotIn(b"\r\n", self.source_bytes)
        self.assertGreater(self.source_bytes.count(b"\n"), 0)


if __name__ == "__main__":
    unittest.main()
