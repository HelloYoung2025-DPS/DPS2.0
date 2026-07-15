import copy
import importlib.util
import json
import sys
import unittest
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

ROOT = Path(__file__).resolve(strict=True).parents[1]
REPOSITORY = ROOT.parents[1]
SOURCE_ROOT = ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "merge_controller.py"
SUBJECT_NAME = "_dps_factory_merge_controller_contract_subject"
MERGE_COMMIT = "a" * 40
BRANCH_COMMIT = "b" * 40
SHA = "c" * 64
RUNNER_POLICY_SHA = "d" * 64


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("contract subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != ROOT or source_path.parent != source_root:
        raise ImportError("contract subject escaped the module-owned src directory")

    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        existing_path = Path(getattr(existing, "__file__", "")).resolve(strict=True)
        if existing_path != source_path:
            raise ImportError("contract subject module name is already bound elsewhere")
        return existing

    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the contract subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


SUBJECT = load_subject()


def attestation(payload_bytes):
    return {
        "algorithm": "rsa-pss-sha256",
        "key_id": "runner-key-001",
        "signer_identity": "trusted-runner",
        "payload_sha256": SUBJECT.sha256(payload_bytes),
        "signature_value": "A" * 128,
    }


class ContractTrustStore:
    """Process-bound verifier stub; RSA behavior belongs to the unit suite."""

    def verify(self, record, payload_bytes):
        if record.get("signer_identity") != "trusted-runner":
            raise SUBJECT.InvalidMergeRequest("fixture signer is not trusted")
        if record.get("payload_sha256") != SUBJECT.sha256(payload_bytes):
            raise SUBJECT.InvalidMergeRequest("fixture payload digest mismatch")
        return "trusted-runner"


def controller():
    policy = {
        "schema_version": "dps.merge-policy/v1",
        "policy_id": "merge-policy-contract-001",
        "required_checks": ["module.contract"],
        "implementers": ["builder"],
        "evidence_issuers": ["trusted-runner"],
        "merge_decider": "decider",
        "release_approvers": ["human-approver"],
        "trusted_runner_policy_sha256": RUNNER_POLICY_SHA,
    }
    return SUBJECT.MergeController("decider", policy, ContractTrustStore())


def trusted_result():
    result = {
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
        "result_id": "result:" + SUBJECT.sha256("module.contract")[:32],
        "request_id": "test-request-001",
        "worktree_plan_id": "worktree-plan-001",
        "module_id": "module-one",
        "check_id": "module.contract",
        "suite_id": "module.contract",
        "evidence_level": "CONTRACT_VERIFIED",
        "template_id": "python.unit",
        "tested_commit": MERGE_COMMIT,
        "required": True,
        "status": "PASS",
        "release_allowed": True,
        "runner_identity": "trusted-runner",
        "auth_context_id": "auth-context-001",
        "instruction_receipt_id": "instruction-receipt-001",
        "manifest_sha256": SHA,
        "workspace_sha256": SHA,
        "required_checks_sha256": SUBJECT.sha256(["module.contract"]),
        "trusted_policy_sha256": RUNNER_POLICY_SHA,
        "lease_id": "lease:" + "e" * 32,
        "fencing_token": 1,
        "command_argv": ["python3.12", "-m", "unittest"],
        "timeout_seconds": 30,
        "started_at": "2026-07-14T00:00:00Z",
        "finished_at": "2026-07-14T00:00:01Z",
        "exit_code": 0,
        "stdout_sha256": SHA,
        "stderr_sha256": SHA,
        "log_sha256": SHA,
        "raw_artifact_sha256": "",
        "runner_attestation": {},
    }
    pre_artifact = {
        key: value for key, value in result.items()
        if key not in {"raw_artifact_sha256", "runner_attestation"}
    }
    result["raw_artifact_sha256"] = SUBJECT.sha256(pre_artifact)
    unsigned = {key: value for key, value in result.items() if key != "runner_attestation"}
    result["runner_attestation"] = attestation(SUBJECT.canonical_bytes(unsigned))
    return {"basis": "merge-head", "result": result}


def valid_request(subject):
    request = {
        "schema_version": "1.0.0",
        "contract_id": "merge.request/v1",
        "producer_module": "factory-trusted-runner",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + "3" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "merge_request_id": "request-001",
        "integration_commit": MERGE_COMMIT,
        "branch_heads": [{"module_id": "module-one", "commit": BRANCH_COMMIT}],
        "changed_paths": ["Modules/module-one/src/a.py"],
        "evidence": [trusted_result()],
        "instruction_receipts": [
            {
                "receipt_id": "receipt-001",
                "status": "BOUND",
                "diff_fingerprint": SHA,
                "receipt_sha256": SHA,
            }
        ],
        "current_diff_fingerprint": SHA,
        "conflicts": {"merge": [], "path_ownership": [], "contract_ownership": []},
        "trusted_policy_sha256": subject.trusted_policy_sha256,
        "runner_attestation": {},
    }
    payload = {
        "contract_id": "merge.request-attestation/v1",
        "merge_request_id": request["merge_request_id"],
        "integration_commit": request["integration_commit"],
        "changed_paths_sha256": SUBJECT.sha256(request["changed_paths"]),
        "evidence_sha256": SUBJECT.sha256(request["evidence"]),
        "instruction_receipts_sha256": SUBJECT.sha256(request["instruction_receipts"]),
        "conflicts_sha256": SUBJECT.sha256(request["conflicts"]),
        "current_diff_fingerprint": request["current_diff_fingerprint"],
        "trusted_policy_sha256": request["trusted_policy_sha256"],
    }
    request["runner_attestation"] = attestation(
        b"dps-merge-request-attestation/v1\n" + SUBJECT.canonical_bytes(payload)
    )
    return request


def load(path):
    return json.loads(path.read_text(encoding="utf-8"))


class MergeContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        provided = ROOT / "contracts" / "provided"
        cls.request_schema = load(provided / "merge.request.v1.schema.json")
        cls.decision_schema = load(provided / "merge.decision.v1.schema.json")
        trusted_path = (
            REPOSITORY / "Modules" / "factory-trusted-runner" / "contracts" /
            "provided" / "trusted.test.result.v1.schema.json"
        )
        cls.trusted_schema = load(trusted_path)
        for schema in (cls.request_schema, cls.decision_schema, cls.trusted_schema):
            Draft202012Validator.check_schema(schema)
        cls.registry = Registry().with_resource(
            cls.trusted_schema["$id"], Resource.from_contents(cls.trusted_schema)
        )

    def validate_request(self, value):
        Draft202012Validator(
            self.request_schema, registry=self.registry, format_checker=FormatChecker()
        ).validate(value)

    def validate_decision(self, value):
        Draft202012Validator(
            self.decision_schema, format_checker=FormatChecker()
        ).validate(value)

    def test_positive_request_and_decision_validate(self):
        subject = controller()
        request = valid_request(subject)
        self.validate_request(request)
        decision = subject.evaluate(request)
        self.assertEqual("APPROVED", decision["outcome"])
        self.validate_decision(decision)

    def test_unknown_field_version_and_producer_fail(self):
        subject = controller()
        request = valid_request(subject)
        request["roles"] = {"decider": "attacker"}
        self.assertRaises(Exception, self.validate_request, request)
        request = valid_request(subject)
        request["contract_id"] = "merge.request/v2"
        self.assertRaises(Exception, self.validate_request, request)
        request = valid_request(subject)
        request["producer_module"] = "factory-untrusted-runner"
        self.assertRaises(Exception, self.validate_request, request)
        request = valid_request(subject)
        del request["trace_id"]
        self.assertRaises(Exception, self.validate_request, request)

    def test_nested_result_unknown_field_and_bad_identity_fail(self):
        subject = controller()
        request = valid_request(subject)
        request["evidence"][0]["result"]["claimed_pass"] = True
        self.assertRaises(Exception, self.validate_request, request)
        request = valid_request(subject)
        request["device_binding_id"] = "device-without-db-prefix"
        self.assertRaises(Exception, self.validate_request, request)

    def test_approved_decision_cannot_have_reasons_or_empty_evidence(self):
        subject = controller()
        decision = subject.evaluate(valid_request(subject))
        invalid = copy.deepcopy(decision)
        invalid["reasons"] = ["hidden conflict"]
        self.assertRaises(Exception, self.validate_decision, invalid)
        invalid = copy.deepcopy(decision)
        invalid["evidence_ids"] = []
        self.assertRaises(Exception, self.validate_decision, invalid)


if __name__ == "__main__":
    unittest.main()
