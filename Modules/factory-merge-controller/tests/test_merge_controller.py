import base64
import copy
import hashlib
import importlib.util
import sys
import unittest
from pathlib import Path


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "merge_controller.py"
SUBJECT_NAME = "_dps_factory_merge_controller_unit_subject"


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("unit subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != MODULE_ROOT or source_path.parent != source_root:
        raise ImportError("unit subject escaped the module-owned src directory")

    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        existing_path = Path(getattr(existing, "__file__", "")).resolve(strict=True)
        if existing_path != source_path:
            raise ImportError("unit subject module name is already bound elsewhere")
        return existing

    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the unit subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


SUBJECT = load_subject()
InvalidMergeRequest = SUBJECT.InvalidMergeRequest
MergeController = SUBJECT.MergeController
RsaPssTrustStore = SUBJECT.RsaPssTrustStore
canonical_bytes = SUBJECT.canonical_bytes
sha256 = SUBJECT.sha256


MERGE = "a" * 40
BRANCH = "b" * 40
SHA = "c" * 64
RUNNER_POLICY_SHA = "d" * 64
RSA_N = int(
    "d558ac64db3f45412cda262c2d9bc4d28aa5cc0b3f0e839fdf6689809133f0a7"
    "e73fcda74f1222650189276dc5043cedb3e3026227dd366abcad140d562da829b8"
    "3cf4578a4e7070100874a151552fc41e295d435e19df44b76a6704cb6df1c071f"
    "d7baf2834b28e02d6be84c3a6528d8bd501bdb8f7cbba32e7ac63c2102aa1",
    16,
)
RSA_D = int(
    "7f39d4048922a0000fe93f9e54cc718144a13e9eee498f80c54e766d2f2a1437"
    "6c9605e3e229644d6baf08ce531105ec92bbab6e316b9fc9e31e2bb9104d45dc"
    "14763250168949a3f3516fa690baebf60aa1ddf82cce3e0dc99483fc00b8e8fd"
    "60492b2432dc8e3b62ef0a7979b4bd46f63e632e7a83ca9f5d0b803c31bab3a9",
    16,
)
RSA_E = 65537


def _mgf1(seed, length):
    output = bytearray()
    counter = 0
    while len(output) < length:
        output.extend(hashlib.sha256(seed + counter.to_bytes(4, "big")).digest())
        counter += 1
    return bytes(output[:length])


def _sign(message):
    em_bits = RSA_N.bit_length() - 1
    em_length = (em_bits + 7) // 8
    salt = hashlib.sha256(b"unit-test-salt" + message).digest()
    message_hash = hashlib.sha256(message).digest()
    encoded_hash = hashlib.sha256(b"\x00" * 8 + message_hash + salt).digest()
    padding_length = em_length - len(encoded_hash) - len(salt) - 2
    data_block = b"\x00" * padding_length + b"\x01" + salt
    mask = _mgf1(encoded_hash, len(data_block))
    masked = bytearray(left ^ right for left, right in zip(data_block, mask))
    unused_bits = 8 * em_length - em_bits
    if unused_bits:
        masked[0] &= 0xFF >> unused_bits
    encoded = bytes(masked) + encoded_hash + b"\xbc"
    signature = pow(int.from_bytes(encoded, "big"), RSA_D, RSA_N)
    return signature.to_bytes((RSA_N.bit_length() + 7) // 8, "big")


def trusted_policy():
    return {
        "schema_version": "dps.merge-policy/v1",
        "policy_id": "merge-policy-001",
        "required_checks": ["module.unit", "module.contract"],
        "implementers": ["builder"],
        "evidence_issuers": ["trusted-runner"],
        "merge_decider": "decider",
        "release_approvers": ["human-approver"],
        "trusted_runner_policy_sha256": RUNNER_POLICY_SHA,
    }


def controller():
    store = RsaPssTrustStore({
        "runner-key-001": {
            "identity": "trusted-runner",
            "algorithm": "rsa-pss-sha256",
            "modulus_hex": format(RSA_N, "x"),
            "exponent": RSA_E,
        }
    })
    return MergeController("decider", trusted_policy(), store)


def valid_request(subject):
    request = {
        "schema_version": "1.0.0",
        "contract_id": "merge.request/v1",
        "producer_module": "factory-trusted-runner",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + "2" * 64,
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "merge_request_id": "request-001",
        "integration_commit": MERGE,
        "branch_heads": [{"module_id": "module-one", "commit": BRANCH}],
        "changed_paths": ["Modules/module-one/src/a.py"],
        "evidence": [trusted_result("module.unit"), trusted_result("module.contract")],
        "instruction_receipts": [
            {"receipt_id": "receipt-001", "status": "BOUND", "diff_fingerprint": SHA, "receipt_sha256": SHA}
        ],
        "current_diff_fingerprint": SHA,
        "conflicts": {"merge": [], "path_ownership": [], "contract_ownership": []},
        "trusted_policy_sha256": subject.trusted_policy_sha256,
        "runner_attestation": {},
    }
    return resign(request)


def trusted_result(check_id, basis="merge-head"):
    result = {
        "schema_version": "1.0.0",
        "contract_id": "trusted.test.result/v1",
        "producer_module": "factory-trusted-runner",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + hashlib.sha256(("result:" + check_id).encode("utf-8")).hexdigest(),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "result_id": f"result:{sha256(check_id)[:32]}",
        "request_id": "test-request-001",
        "worktree_plan_id": "worktree-plan-001",
        "module_id": "module-one",
        "check_id": check_id,
        "suite_id": check_id,
        "evidence_level": "CONTRACT_VERIFIED" if check_id.endswith("contract") else "REPOSITORY_STATIC_VERIFIED",
        "template_id": "python.unit",
        "tested_commit": MERGE,
        "required": True,
        "status": "PASS",
        "release_allowed": True,
        "runner_identity": "trusted-runner",
        "auth_context_id": "auth-context-001",
        "instruction_receipt_id": "instruction-receipt-001",
        "manifest_sha256": SHA,
        "workspace_sha256": SHA,
        "required_checks_sha256": sha256(sorted(["module.unit", "module.contract"])),
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
    wrapper = {"basis": basis, "result": result}
    return resign_result(wrapper)


def resign_result(wrapper):
    result = wrapper["result"]
    pre_artifact = {
        key: value for key, value in result.items()
        if key not in {"raw_artifact_sha256", "runner_attestation"}
    }
    result["raw_artifact_sha256"] = sha256(pre_artifact)
    unsigned = {key: value for key, value in result.items() if key != "runner_attestation"}
    message = canonical_bytes(unsigned)
    result["runner_attestation"] = {
        "algorithm": "rsa-pss-sha256",
        "key_id": "runner-key-001",
        "signer_identity": "trusted-runner",
        "payload_sha256": hashlib.sha256(message).hexdigest(),
        "signature_value": base64.b64encode(_sign(message)).decode("ascii"),
    }
    return wrapper


def resign(request):
    payload = {
        "contract_id": "merge.request-attestation/v1",
        "merge_request_id": request["merge_request_id"],
        "integration_commit": request["integration_commit"],
        "changed_paths_sha256": sha256(request["changed_paths"]),
        "evidence_sha256": sha256(request["evidence"]),
        "instruction_receipts_sha256": sha256(request["instruction_receipts"]),
        "conflicts_sha256": sha256(request["conflicts"]),
        "current_diff_fingerprint": request["current_diff_fingerprint"],
        "trusted_policy_sha256": request["trusted_policy_sha256"],
    }
    message = b"dps-merge-request-attestation/v1\n" + canonical_bytes(payload)
    request["runner_attestation"] = {
        "algorithm": "rsa-pss-sha256",
        "key_id": "runner-key-001",
        "signer_identity": "trusted-runner",
        "payload_sha256": hashlib.sha256(message).hexdigest(),
        "signature_value": base64.b64encode(_sign(message)).decode("ascii"),
    }
    return request


class MergeControllerTests(unittest.TestCase):
    def setUp(self):
        self.controller = controller()

    def test_valid_merge_head_is_approved_and_idempotent(self):
        request = valid_request(self.controller)
        first = self.controller.evaluate(request)
        second = self.controller.evaluate(copy.deepcopy(request))
        self.assertEqual("APPROVED", first["outcome"])
        self.assertEqual(first, second)
        self.assertEqual(self.controller.trusted_policy_sha256, first["trusted_policy_sha256"])

    def test_branch_green_cannot_replace_merge_head_retest(self):
        request = valid_request(self.controller)
        for evidence in request["evidence"]:
            evidence["basis"] = "branch-head"
            evidence["result"]["tested_commit"] = BRANCH
            resign_result(evidence)
        decision = self.controller.evaluate(resign(request))
        self.assertEqual("REJECTED", decision["outcome"])
        self.assertTrue(any("lacks merge-head evidence" in reason for reason in decision["reasons"]))

    def test_non_pass_conflict_and_stale_receipt_reject(self):
        request = valid_request(self.controller)
        request["evidence"][0]["result"]["status"] = "PARTIAL"
        request["evidence"][0]["result"]["release_allowed"] = False
        request["evidence"][0]["result"]["exit_code"] = 1
        resign_result(request["evidence"][0])
        request["conflicts"]["contract_ownership"] = ["contract.x"]
        request["instruction_receipts"][0]["status"] = "STALE"
        decision = self.controller.evaluate(resign(request))
        self.assertEqual("REJECTED", decision["outcome"])
        self.assertIn("contract_ownership conflict present", decision["reasons"])
        self.assertIn("required check module.unit is not trusted PASS", decision["reasons"])
        self.assertIn("stale instruction receipt", decision["reasons"])

    def test_request_cannot_self_declare_roles_or_required_checks(self):
        for field, value in (("roles", {"merge_decider": "attacker"}), ("required_checks", [])):
            request = valid_request(self.controller)
            request[field] = value
            with self.assertRaisesRegex(InvalidMergeRequest, "unknown or missing"):
                self.controller.evaluate(request)

    def test_forged_producer_bound_receipt_and_pass_evidence_are_rejected(self):
        mutations = [
            lambda request: request.__setitem__("producer_module", "factory-untrusted-runner"),
            lambda request: request["instruction_receipts"][0].__setitem__("receipt_sha256", "0" * 64),
            lambda request: request["evidence"][0]["result"].__setitem__("status", "PASS"),
        ]
        request = valid_request(self.controller)
        request["evidence"][0]["result"]["status"] = "FAIL"
        request["evidence"][0]["result"]["release_allowed"] = False
        request["evidence"][0]["result"]["exit_code"] = 1
        resign_result(request["evidence"][0])
        request = resign(request)
        for mutate in mutations:
            forged = copy.deepcopy(request)
            mutate(forged)
            with self.assertRaises(InvalidMergeRequest):
                self.controller.evaluate(forged)

    def test_forged_attestation_and_unknown_contract_fail_closed(self):
        request = valid_request(self.controller)
        request["runner_attestation"]["signature_value"] = base64.b64encode(b"x" * 128).decode("ascii")
        with self.assertRaisesRegex(InvalidMergeRequest, "signature verification"):
            self.controller.evaluate(request)
        request = valid_request(self.controller)
        request["contract_id"] = "merge.request/v2"
        with self.assertRaises(InvalidMergeRequest):
            self.controller.evaluate(request)

    def test_trusted_policy_cannot_overlap_roles(self):
        policy = trusted_policy()
        policy["release_approvers"] = ["builder"]
        store = RsaPssTrustStore({
            "runner-key-001": {
                "identity": "trusted-runner", "algorithm": "rsa-pss-sha256",
                "modulus_hex": format(RSA_N, "x"), "exponent": RSA_E,
            }
        })
        with self.assertRaisesRegex(ValueError, "separation"):
            MergeController("decider", policy, store)


if __name__ == "__main__":
    unittest.main()
