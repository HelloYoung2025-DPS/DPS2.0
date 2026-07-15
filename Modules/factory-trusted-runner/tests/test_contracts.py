import copy
import datetime as dt
import importlib.util
import json
import sys
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from jsonschema.exceptions import ValidationError


MODULE_ROOT = Path(__file__).resolve().parents[1]
FIXTURE_SPEC = importlib.util.spec_from_file_location(
    "factory_trusted_runner_contract_fixture",
    MODULE_ROOT / "tests" / "test_trusted_runner.py",
)
FIXTURE_MODULE = importlib.util.module_from_spec(FIXTURE_SPEC)
assert FIXTURE_SPEC.loader is not None
sys.modules[FIXTURE_SPEC.name] = FIXTURE_MODULE
FIXTURE_SPEC.loader.exec_module(FIXTURE_MODULE)


def strict_format_checker():
    checker = FormatChecker()

    @checker.checks("date-time")
    def is_datetime(value):
        try:
            parsed = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
        except (AttributeError, ValueError):
            return False
        return parsed.tzinfo is not None

    return checker


def valid_trusted_result():
    return {
        "schema_version": "1.0.0",
        "contract_id": "trusted.test.result/v1",
        "producer_module": "factory-trusted-runner",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + "2" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "result_id": "result:contract-0001",
        "request_id": "request:contract-0001",
        "worktree_plan_id": "worktree:" + "a" * 32,
        "module_id": "factory-trusted-runner",
        "check_id": "factory-trusted-runner.contract",
        "suite_id": "factory-trusted-runner.contract",
        "evidence_level": "CONTRACT_VERIFIED",
        "template_id": "python.unit",
        "tested_commit": "b" * 40,
        "required": True,
        "status": "PASS",
        "release_allowed": True,
        "runner_identity": "runner-service-1",
        "auth_context_id": "auth-context-0001",
        "instruction_receipt_id": "instruction:" + "c" * 32,
        "manifest_sha256": "d" * 64,
        "workspace_sha256": "e" * 64,
        "required_checks_sha256": "f" * 64,
        "trusted_policy_sha256": "1" * 64,
        "lease_id": "lease:" + "2" * 32,
        "fencing_token": 7,
        "command_argv": ["python3.12", "-m", "unittest", "test_contracts.py"],
        "timeout_seconds": 300,
        "started_at": "2026-07-14T00:00:00Z",
        "finished_at": "2026-07-14T00:00:01Z",
        "exit_code": 0,
        "stdout_sha256": "3" * 64,
        "stderr_sha256": "4" * 64,
        "log_sha256": "5" * 64,
        "raw_artifact_sha256": "6" * 64,
        "runner_attestation": {
            "algorithm": "rsa-pss-sha256",
            "key_id": "runner-key-1",
            "signer_identity": "runner-service-1",
            "payload_sha256": "7" * 64,
            "signature_value": "A" * 128,
        },
    }


class TrustedTestResultContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = (
            MODULE_ROOT
            / "contracts"
            / "provided"
            / "trusted.test.result.v1.schema.json"
        )
        cls.schema = json.loads(path.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(cls.schema)
        cls.validator = Draft202012Validator(
            cls.schema, format_checker=strict_format_checker()
        )

    def test_complete_signed_pass_result_validates(self):
        self.validator.validate(valid_trusted_result())

    def test_production_runner_output_validates(self):
        fixture = FIXTURE_MODULE.RunnerFixture()
        try:
            self.validator.validate(fixture.run())
        finally:
            fixture.close()

    def test_unknown_major_producer_and_field_fail_closed(self):
        invalid = valid_trusted_result()
        invalid["contract_id"] = "trusted.test.result/v2"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_trusted_result()
        invalid["producer_module"] = "candidate-runner"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_trusted_result()
        invalid["raw_stdout"] = "must-not-cross-contract-boundary"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

    def test_release_and_attestation_invariants_fail_closed(self):
        invalid = valid_trusted_result()
        invalid["status"] = "FAIL"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = copy.deepcopy(valid_trusted_result())
        invalid["runner_attestation"]["algorithm"] = "none"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)

        invalid = valid_trusted_result()
        invalid["device_binding_id"] = "device-without-db-prefix"
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid)


if __name__ == "__main__":
    unittest.main()
