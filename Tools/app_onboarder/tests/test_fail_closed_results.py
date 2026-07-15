# -*- coding: utf-8 -*-
"""Fail-closed regression tests for App Onboarder result propagation."""

import os
import tempfile
import unittest
from types import SimpleNamespace
from unittest import mock

from Tools.app_onboarder import main as onboarder_main
from Tools.app_onboarder.test_runner import TestRunner


class _FakeADB(object):
    def screenshot(self, name):
        return "/tmp/{0}.png".format(name)


class _FakeProcess(object):
    def __init__(self, stdout, stderr=b"", returncode=0):
        self._stdout = stdout
        self._stderr = stderr
        self.returncode = returncode

    def communicate(self, timeout=None):
        return self._stdout, self._stderr

    def kill(self):
        return None


class TestRunnerFailClosedTests(unittest.TestCase):
    def setUp(self):
        handle, self.script_path = tempfile.mkstemp(suffix=".ps1")
        os.close(handle)

    def tearDown(self):
        if os.path.exists(self.script_path):
            os.unlink(self.script_path)

    def _runner(self, evidence_class="device_e2e", execution_mode="real"):
        return TestRunner(
            adb=_FakeADB(),
            test_script_path=self.script_path,
            config_path="unused.json",
            operations_path="unused.json",
            platform_key="fixture",
            max_fix_attempts=1,
            evidence_class=evidence_class,
            execution_mode=execution_mode,
        )

    @staticmethod
    def _pass_output():
        return (
            b"Phase 1 - Launch: [PASS]\n"
            b"[Phase 1] [PASS] native postcondition verified\n"
            b"Total: 1/1 passed\n"
        )

    def test_nonzero_powershell_exit_cannot_be_overridden_by_pass_text(self):
        runner = self._runner()
        process = _FakeProcess(self._pass_output(), returncode=9)
        with mock.patch(
            "Tools.app_onboarder.test_runner.subprocess.Popen",
            return_value=process,
        ):
            report = runner.run_and_fix()

        self.assertFalse(report["success"])
        self.assertEqual(
            report["final_results"]["execution_status"], "INFRA_ERROR"
        )
        self.assertIn("process_exit_code_9", report["failure_reasons"])

    def test_required_non_pass_statuses_all_block(self):
        for status in (
            "FAIL", "SKIP", "PARTIAL", "NOT_RUN", "INFRA_ERROR",
        ):
            with self.subTest(status=status):
                output = (
                    "[{0}] Phase 1: required result\n"
                    "Total: 0/1 passed\n"
                ).format(status).encode("utf-8")
                runner = self._runner()
                with mock.patch(
                    "Tools.app_onboarder.test_runner.subprocess.Popen",
                    return_value=_FakeProcess(output, returncode=0),
                ):
                    results = runner.run_test()

                self.assertEqual(results["phases"][1]["status"], status)
                self.assertFalse(runner.is_result_successful(results))

    def test_summary_without_phase_evidence_is_not_success(self):
        runner = self._runner()
        with mock.patch(
            "Tools.app_onboarder.test_runner.subprocess.Popen",
            return_value=_FakeProcess(b"Total: 1/1 passed\n", returncode=0),
        ):
            results = runner.run_test()

        self.assertFalse(results["evidence_complete"])
        self.assertFalse(runner.is_result_successful(results))

    def test_mock_cannot_satisfy_integration_evidence(self):
        runner = self._runner(
            evidence_class="integration", execution_mode="mock"
        )
        output = b"[EVIDENCE] execution_mode=mock\n" + self._pass_output()
        with mock.patch(
            "Tools.app_onboarder.test_runner.subprocess.Popen",
            return_value=_FakeProcess(output, returncode=0),
        ):
            results = runner.run_test()

        self.assertTrue(results["mock"])
        self.assertFalse(runner.is_result_successful(results))
        self.assertTrue(any(
            "cannot_satisfy_integration" in reason
            for reason in runner._result_failure_reasons(results)
        ))

    def test_complete_real_pass_evidence_succeeds(self):
        runner = self._runner()
        with mock.patch(
            "Tools.app_onboarder.test_runner.subprocess.Popen",
            return_value=_FakeProcess(self._pass_output(), returncode=0),
        ):
            results = runner.run_test()

        self.assertTrue(results["evidence_complete"])
        self.assertTrue(runner.is_result_successful(results))


class MainExitCodeTests(unittest.TestCase):
    def _args(self):
        return SimpleNamespace(
            yes=True,
            device=None,
            skip_explore=False,
            enable_vision=False,
            vision_model="unused",
            ai_config=None,
            vision_max_pages=1,
            output=None,
            skip_test=False,
        )

    def _run_main_with_report(self, report):
        app_map = {
            "platform_key": "fixture",
            "package_name": "example.fixture",
        }
        with mock.patch.object(onboarder_main, "print_banner"), \
                mock.patch.object(onboarder_main, "parse_args", return_value=self._args()), \
                mock.patch.object(onboarder_main, "step_check_adb", return_value=_FakeADB()), \
                mock.patch.object(onboarder_main, "step_get_info", return_value=("example.fixture", "fixture")), \
                mock.patch.object(onboarder_main, "step_detect_state", return_value={}), \
                mock.patch.object(onboarder_main, "step_explore", return_value=app_map), \
                mock.patch.object(onboarder_main, "step_generate", return_value={}), \
                mock.patch.object(onboarder_main, "step_test", return_value=report), \
                mock.patch.object(onboarder_main, "step_summary"):
            return onboarder_main.main()

    def test_failed_or_missing_report_returns_nonzero(self):
        self.assertEqual(
            self._run_main_with_report({"success": False}),
            onboarder_main.EXIT_REQUIRED_TEST_FAILED,
        )
        self.assertEqual(
            self._run_main_with_report(None),
            onboarder_main.EXIT_REQUIRED_TEST_FAILED,
        )

    def test_success_report_returns_zero(self):
        self.assertEqual(
            self._run_main_with_report({"success": True}),
            onboarder_main.EXIT_SUCCESS,
        )

    def test_explicit_skip_is_a_failed_required_report(self):
        report = onboarder_main.step_test(
            adb=_FakeADB(),
            platform_key="fixture",
            generated_paths={},
            skip=True,
        )
        self.assertFalse(report["success"])
        self.assertEqual(
            report["final_results"]["execution_status"], "SKIP"
        )


if __name__ == "__main__":
    unittest.main()
