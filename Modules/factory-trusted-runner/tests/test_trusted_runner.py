import base64
import copy
import hashlib
import hmac
import importlib.util
import json
import os
import shutil
import shlex
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


SOURCE = Path(__file__).parents[1] / "src" / "trusted_runner.py"
SPEC = importlib.util.spec_from_file_location("factory_trusted_runner", SOURCE)
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)
sys.modules["trusted_runner"] = MODULE

CLI_SOURCE = Path(__file__).parents[1] / "src" / "cli.py"
CLI_SPEC = importlib.util.spec_from_file_location("factory_trusted_runner_cli", CLI_SOURCE)
CLI = importlib.util.module_from_spec(CLI_SPEC)
sys.modules[CLI_SPEC.name] = CLI
CLI_SPEC.loader.exec_module(CLI)

TrustedRunner = MODULE.TrustedRunner
TrustedRunnerError = MODULE.TrustedRunnerError
TrustedRunnerPolicy = MODULE.TrustedRunnerPolicy
canonical_bytes = MODULE.canonical_bytes
sha256_bytes = MODULE.sha256_bytes
workspace_sha256 = MODULE.workspace_sha256


ZERO = "0" * 64
COMMIT_ZERO = "0" * 40


class RunnerFixture:
    def __init__(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.module_root = self.root / "Modules" / "demo"
        self.tests = self.module_root / "tests"
        self.tests.mkdir(parents=True)
        self.test_file = self.tests / "test_fixture.py"
        self.test_file.write_text(
            "import unittest\nclass T(unittest.TestCase):\n    def test_ok(self): self.assertTrue(True)\n",
            encoding="utf-8",
        )
        self.argv = [
            sys.executable, "-m", "unittest", "discover", "-s",
            "Modules/demo/tests", "-p", "test_*.py",
        ]
        self.manifest = {
            "tests": {
                "suites": [{
                    "id": "demo.unit", "type": "unit", "required": True,
                    "command": shlex.join(self.argv),
                    "environment": "synthetic",
                    "evidenceLevel": "REPOSITORY_STATIC_VERIFIED",
                }]
            }
        }
        self.manifest_path = self.module_root / "module.yaml"
        self._write_manifest()
        self._git("init", "-q")
        self._git("add", ".")
        self._git("-c", "user.name=DPS Test", "-c", "user.email=dps@example.invalid", "commit", "-qm", "fixture")
        self.commit = self._git("rev-parse", "HEAD").strip()
        self.policy_document = self._policy_document()
        self.policy_digest = sha256_bytes(self.policy_document)
        self.policy = TrustedRunnerPolicy.from_verified_document(
            self.policy_document,
            self.policy_digest,
            lambda _document, digest: self.fact(digest, digest),
        )
        self.request = {
            "request_id": "request:12345678", "check_id": "demo.unit",
            "module_id": "demo", "worktree_plan_id": "worktree:" + "1" * 32,
            "instruction_receipt_id": "instruction:" + "2" * 32,
            "auth_context_id": "auth-context-12345678", "soul_id": None,
            "device_binding_id": None, "platform_account_id": None,
            "trace_id": "trace_" + "1" * 32, "idempotency_key": "idem_" + "2" * 64,
            "occurred_at": "2026-07-14T00:00:00Z",
        }
        self.plan = {
            "contract_id": "worktree.plan/v1", "producer_module": "factory-worktree-manager",
            "plan_id": self.request["worktree_plan_id"],
            "instruction_receipt_id": self.request["instruction_receipt_id"],
            "trusted_policy_sha256": self.policy_digest,
            "soul_id": None, "device_binding_id": None, "platform_account_id": None,
            "entries": [{
                "module_id": "demo", "writer_identity": "implementer-1",
                "lease_keys": ["module:demo", "path:Modules/demo"],
            }],
        }
        self.receipt = self._receipt()
        self.lease = {
            "contract_id": "worktree.lease/v1", "producer_module": "factory-worktree-manager",
            "lease_id": "lease:" + "3" * 32, "plan_id": self.plan["plan_id"],
            "status": "ACTIVE", "lock_tokens": {"module:demo": 4, "path:Modules/demo": 9},
            "fencing_token": 9,
        }
        self.runner = self._runner()

    def close(self):
        self.temp.cleanup()

    def _git(self, *args):
        completed = subprocess.run(
            ["git", "-C", str(self.root), *args], check=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
        )
        return completed.stdout

    def _write_manifest(self):
        self.manifest_path.write_text(json.dumps(self.manifest, sort_keys=True), encoding="utf-8")

    def _policy_document(self, **overrides):
        policy = {
            "schema_version": "dps.trusted-runner-policy/v1", "policy_id": "policy-1",
            "status": "non-production-template", "runner_identity": "runner-service",
            "allowed_executables": [Path(sys.executable).name],
            "allowed_environment": ["HOME", "PATH", "PYTHONUTF8", "TMPDIR"],
            "maximum_output_bytes": 200000,
            "roles": {
                "implementers": ["implementer-1"],
                "evidence_issuers": ["runner-service"],
                "release_approvers": ["human-approver"],
            },
            "checks": [{
                "check_id": "demo.unit", "module_id": "demo", "argv": self.argv,
                "cwd": ".", "timeout_seconds": 10, "required": True,
                "success_marker": "OK",
                "forbidden_output_markers": ["FAILED", "SKIP", "skipped=", "PARTIAL", "NOT_RUN", "INFRA_ERROR"],
            }],
        }
        policy.update(overrides)
        return canonical_bytes(policy)

    def _receipt(self):
        return {
            "contract_id": "instruction.receipt/v1",
            "producer_module": "factory-instruction-resolver",
            "receipt_id": self.request["instruction_receipt_id"],
            "auth_context_id": self.request["auth_context_id"],
            "status": "BOUND", "invalidated_reason": None, "scope": ["demo"],
            "soul_id": None, "device_binding_id": None, "platform_account_id": None,
            "manifests": [{
                "path": "Modules/demo/module.yaml",
                "sha256": sha256_bytes(self.manifest_path.read_bytes()),
            }],
        }

    @staticmethod
    def fact(fact_id, digest, **values):
        return {"verified": True, "fact_id": fact_id, "fact_sha256": digest, **values}

    def _runner(self, **overrides):
        callbacks = {
            "auth_context_verifier": lambda auth_id, _runner: self.fact(auth_id, "a" * 64),
            "receipt_verifier": lambda receipt: self.fact(
                receipt["receipt_id"], sha256_bytes(canonical_bytes(receipt))
            ),
            "commit_workspace_verifier": lambda _root, commit, workspace: self.fact(
                self.plan["plan_id"], "b" * 64, commit=commit, workspace_sha256=workspace
            ),
            "fence_verifier": lambda lease_id, tokens: self.fact(
                lease_id, sha256_bytes(canonical_bytes(self.lease)),
                plan_id=self.plan["plan_id"], lock_tokens=tokens, fencing_token=9,
            ),
            "attestation_issuer": lambda payload, runner, _policy: {
                "algorithm": "rsa-pss-sha256", "key_id": "runner-key-1",
                "signer_identity": runner, "payload_sha256": sha256_bytes(payload),
                "signature_value": "A" * 128,
            },
        }
        callbacks.update(overrides)
        return TrustedRunner(self.root, self.root, "runner-service", **callbacks)

    def run(self):
        return self.runner.run(self.request, self.plan, self.receipt, self.lease, self.policy)


class TrustedRunnerTests(unittest.TestCase):
    def setUp(self):
        self.fx = RunnerFixture()

    def tearDown(self):
        self.fx.close()

    def test_exact_policy_check_passes_and_emits_only_hashes(self):
        result = self.fx.run()
        self.assertEqual("PASS", result["status"])
        self.assertTrue(result["release_allowed"])
        self.assertEqual(self.fx.commit, result["tested_commit"])
        self.assertEqual("demo.unit", result["suite_id"])
        self.assertEqual("REPOSITORY_STATIC_VERIFIED", result["evidence_level"])
        self.assertEqual(self.fx.policy_digest, result["trusted_policy_sha256"])
        self.assertEqual(9, result["fencing_token"])
        self.assertNotIn("stdout", result)
        self.assertEqual(result["runner_attestation"]["payload_sha256"], sha256_bytes(canonical_bytes({k: v for k, v in result.items() if k != "runner_attestation"})))

    def test_request_cannot_supply_command_roles_required_or_commit(self):
        for key, value in (
            ("command_argv", ["sh", "-c", "id"]), ("roles", ["approver"]),
            ("required", False), ("tested_commit", COMMIT_ZERO),
        ):
            request = dict(self.fx.request); request[key] = value
            with self.assertRaises(TrustedRunnerError):
                self.fx.runner.run(request, self.fx.plan, self.fx.receipt, self.fx.lease, self.fx.policy)

    def test_policy_digest_external_fact_and_role_separation_are_required(self):
        with self.assertRaises(TrustedRunnerError):
            TrustedRunnerPolicy.from_verified_document(
                self.fx.policy_document, "f" * 64,
                lambda _doc, digest: self.fx.fact(digest, digest),
            )
        with self.assertRaises(TrustedRunnerError):
            TrustedRunnerPolicy.from_verified_document(
                self.fx.policy_document, self.fx.policy_digest,
                lambda _doc, digest: {"verified": True, "fact_id": digest, "fact_sha256": "0" * 64},
            )
        document = self.fx._policy_document(roles={
            "implementers": ["same"], "evidence_issuers": ["same"],
            "release_approvers": ["human"],
        })
        digest = sha256_bytes(document)
        with self.assertRaises(TrustedRunnerError):
            TrustedRunnerPolicy.from_verified_document(
                document, digest, lambda _doc, value: self.fx.fact(value, value)
            )

    def test_candidate_policy_replacement_cannot_change_process_bound_digest(self):
        candidate = json.loads(self.fx.policy_document)
        candidate["checks"][0]["required"] = False
        replacement = canonical_bytes(candidate)
        with self.assertRaises(TrustedRunnerError):
            TrustedRunnerPolicy.from_verified_document(
                replacement, self.fx.policy_digest,
                lambda _doc, value: self.fx.fact(value, value),
            )

    def test_shell_interpreter_and_shell_like_tokens_rejected_even_in_signed_policy(self):
        raw = json.loads(self.fx.policy_document)
        raw["allowed_executables"] = ["sh"]
        raw["checks"][0]["argv"] = ["sh", "-c", "echo OK"]
        document = canonical_bytes(raw); digest = sha256_bytes(document)
        with self.assertRaises(TrustedRunnerError):
            TrustedRunnerPolicy.from_verified_document(
                document, digest, lambda _doc, value: self.fx.fact(value, value)
            )

    def test_stale_receipt_plan_writer_and_fence_fail_before_execution(self):
        receipt = copy.deepcopy(self.fx.receipt); receipt["status"] = "STALE"
        with self.assertRaises(TrustedRunnerError):
            self.fx.runner.run(self.fx.request, self.fx.plan, receipt, self.fx.lease, self.fx.policy)
        plan = copy.deepcopy(self.fx.plan); plan["entries"][0]["writer_identity"] = "request-self-appointed"
        with self.assertRaises(TrustedRunnerError):
            self.fx.runner.run(self.fx.request, plan, self.fx.receipt, self.fx.lease, self.fx.policy)
        runner = self.fx._runner(fence_verifier=lambda _lease, _tokens: self.fx.fact(self.fx.lease["lease_id"], sha256_bytes(canonical_bytes(self.fx.lease)), plan_id=self.fx.plan["plan_id"], lock_tokens=self.fx.lease["lock_tokens"], fencing_token=10))
        with self.assertRaises(TrustedRunnerError):
            runner.run(self.fx.request, self.fx.plan, self.fx.receipt, self.fx.lease, self.fx.policy)

    def test_workspace_symlink_and_manifest_drift_fail_closed(self):
        (self.fx.module_root / "escape").symlink_to("/etc/hosts")
        with self.assertRaises(TrustedRunnerError):
            self.fx.run()
        (self.fx.module_root / "escape").unlink()
        self.fx.manifest["tests"]["suites"][0]["required"] = False
        self.fx._write_manifest()
        with self.assertRaises(TrustedRunnerError):
            self.fx.run()

    def test_skip_output_never_releases(self):
        self.fx.test_file.write_text(
            "import unittest\nclass T(unittest.TestCase):\n    @unittest.skip('no')\n    def test_skip(self): pass\n",
            encoding="utf-8",
        )
        result = self.fx.run()
        self.assertEqual("SKIP", result["status"])
        self.assertFalse(result["release_allowed"])

    def test_timeout_and_output_limit_are_infrastructure_errors(self):
        self.fx.test_file.write_text(
            "import time, unittest\nclass T(unittest.TestCase):\n    def test_slow(self): time.sleep(2)\n",
            encoding="utf-8",
        )
        raw = json.loads(self.fx.policy_document); raw["checks"][0]["timeout_seconds"] = 1
        document = canonical_bytes(raw); digest = sha256_bytes(document)
        policy = TrustedRunnerPolicy.from_verified_document(document, digest, lambda _doc, value: self.fx.fact(value, value))
        plan = dict(self.fx.plan); plan["trusted_policy_sha256"] = digest
        result = self.fx.runner.run(self.fx.request, plan, self.fx.receipt, self.fx.lease, policy)
        self.assertEqual("INFRA_ERROR", result["status"])
        self.assertFalse(result["release_allowed"])

        self.fx.test_file.write_text(
            "import unittest\nclass T(unittest.TestCase):\n    def test_loud(self): print('X' * 20000)\n",
            encoding="utf-8",
        )
        raw["checks"][0]["timeout_seconds"] = 10; raw["maximum_output_bytes"] = 200
        document = canonical_bytes(raw); digest = sha256_bytes(document)
        policy = TrustedRunnerPolicy.from_verified_document(document, digest, lambda _doc, value: self.fx.fact(value, value))
        plan["trusted_policy_sha256"] = digest
        result = self.fx.runner.run(self.fx.request, plan, self.fx.receipt, self.fx.lease, policy)
        self.assertEqual("INFRA_ERROR", result["status"])

    def test_malformed_identity_and_attestation_are_rejected(self):
        request = dict(self.fx.request); request["soul_id"] = "soul_bad"
        with self.assertRaises(TrustedRunnerError):
            self.fx.runner.run(request, self.fx.plan, self.fx.receipt, self.fx.lease, self.fx.policy)
        runner = self.fx._runner(attestation_issuer=lambda _payload, runner, _policy: {
            "algorithm": "rsa-pss-sha256", "key_id": "key", "signer_identity": runner,
            "payload_sha256": "0" * 64, "signature_value": "A" * 128,
        })
        with self.assertRaises(TrustedRunnerError):
            runner.run(self.fx.request, self.fx.plan, self.fx.receipt, self.fx.lease, self.fx.policy)

    def test_restricted_cli_uses_hmac_facts_and_emits_verifiable_rsa_pss(self):
        openssl = shutil.which("openssl")
        self.assertIsNotNone(openssl)
        with tempfile.TemporaryDirectory() as external:
            external_root = Path(external)
            key = external_root / "runner-key.pem"
            public = external_root / "runner-public.pem"
            subprocess.run([openssl, "genrsa", "-out", str(key), "2048"], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            key.chmod(0o600)
            subprocess.run([openssl, "rsa", "-in", str(key), "-pubout", "-out", str(public)], check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            facts_payload = {
                "policy": self.fx.fact(self.fx.policy_digest, self.fx.policy_digest),
                "auth_context": self.fx.fact(self.fx.request["auth_context_id"], "a" * 64),
                "instruction_receipt": self.fx.fact(
                    self.fx.receipt["receipt_id"], sha256_bytes(canonical_bytes(self.fx.receipt))
                ),
                "commit_workspace": self.fx.fact(
                    self.fx.plan["plan_id"], "b" * 64, commit=self.fx.commit,
                    workspace_sha256=workspace_sha256(self.fx.root),
                ),
                "lease": self.fx.fact(
                    self.fx.lease["lease_id"], sha256_bytes(canonical_bytes(self.fx.lease)),
                    plan_id=self.fx.plan["plan_id"], lock_tokens=self.fx.lease["lock_tokens"],
                    fencing_token=9,
                ),
            }
            hmac_key = bytes(range(32))
            documents = {
                "request.json": self.fx.request, "worktree-plan.json": self.fx.plan,
                "instruction-receipt.json": self.fx.receipt, "worktree-lease.json": self.fx.lease,
                "trusted-policy.json": json.loads(self.fx.policy_document),
                "runtime-facts.json": {
                    "payload": facts_payload,
                    "hmac_sha256": hmac.new(hmac_key, canonical_bytes(facts_payload), hashlib.sha256).hexdigest(),
                },
            }
            for name, value in documents.items():
                (external_root / name).write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
            environment = {
                "DPS_RUNNER_REPOSITORY_ROOT": str(self.fx.root),
                "DPS_RUNNER_EXECUTION_ROOT": str(self.fx.root),
                "DPS_RUNNER_INPUT_DIR": str(external_root),
                "DPS_RUNNER_POLICY_SHA256": self.fx.policy_digest,
                "DPS_RUNNER_FACT_HMAC_KEY_HEX": hmac_key.hex(),
                "DPS_RUNNER_RSA_PRIVATE_KEY": str(key),
                "DPS_RUNNER_RSA_KEY_ID": "runner-key-1",
                "DPS_RUNNER_OPENSSL": openssl,
                "DPS_RUNNER_IDENTITY": "runner-service",
            }
            with mock.patch.dict(os.environ, environment, clear=False):
                result = CLI.run_from_environment()
            self.assertEqual("PASS", result["status"])
            unsigned = {name: value for name, value in result.items() if name != "runner_attestation"}
            signature = external_root / "signature.bin"
            signature.write_bytes(base64.b64decode(result["runner_attestation"]["signature_value"]))
            verified = subprocess.run(
                [openssl, "dgst", "-sha256", "-verify", str(public), "-signature", str(signature),
                 "-sigopt", "rsa_padding_mode:pss"],
                input=canonical_bytes(unsigned), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                check=False,
            )
            self.assertEqual(0, verified.returncode, verified.stderr.decode("utf-8", errors="replace"))


if __name__ == "__main__":
    unittest.main()
