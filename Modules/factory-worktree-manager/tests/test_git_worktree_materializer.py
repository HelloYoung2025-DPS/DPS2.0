import concurrent.futures
import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "src"
sys.path.insert(0, str(SOURCE))

from git_worktree_materializer import GitWorktreeMaterializer, MaterializationError
from worktree_manager import StaleFence


class GitWorktreeMaterializerIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.outer = tempfile.TemporaryDirectory()
        outer = Path(self.outer.name)
        self.repo = outer / "repository"
        self.worktrees = outer / "worktrees"
        self.repo.mkdir(); self.worktrees.mkdir()
        for module in ("alpha", "beta", "consumer"):
            root = self.repo / "Modules" / module / "src"
            root.mkdir(parents=True)
            (root / "value.txt").write_text("base\n", encoding="utf-8")
        (self.repo / "run_checks.py").write_text(
            "import pathlib, sys, time\n"
            "time.sleep(0.6)\n"
            "for module in sys.argv[1:]:\n"
            "    value=pathlib.Path('Modules',module,'src','value.txt').read_text().strip()\n"
            "    if value != 'upgraded': raise SystemExit(2)\n"
            "print('OK')\n",
            encoding="utf-8",
        )
        self._git(self.repo, "init", "-q")
        self._git(self.repo, "add", ".")
        self._git(
            self.repo, "-c", "user.name=DPS Test", "-c", "user.email=dps@example.invalid",
            "commit", "-qm", "baseline",
        )
        self.baseline = self._git(self.repo, "rev-parse", "HEAD").stdout.strip()
        self.policy_hash = hashlib.sha256(b"stable-materializer-policy").hexdigest()
        self.plan = self._plan()
        self.leases = {module: self._lease(module, index + 1) for index, module in enumerate(("alpha", "beta", "consumer"))}
        argv = {
            module: [sys.executable, "run_checks.py", module]
            for module in ("alpha", "beta", "consumer")
        }
        self.materializer = GitWorktreeMaterializer(
            self.repo, self.worktrees, self.plan,
            trusted_policy_sha256=self.policy_hash,
            module_test_argv=argv,
            merge_test_argv=[sys.executable, "run_checks.py", "alpha", "beta", "consumer"],
            plan_verifier=self._verify_plan,
            fence_verifier=self._verify_fence,
        )

    def tearDown(self):
        if hasattr(self, "materializer"):
            self.materializer.cleanup()
        self.outer.cleanup()

    @staticmethod
    def _git(cwd, *args, check=True):
        return subprocess.run(
            ["git", *args], cwd=cwd, stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            shell=False, check=check,
        )

    def _plan(self):
        entries = []
        for module, writer, dependencies in (
            ("alpha", "writer-alpha", []),
            ("beta", "writer-beta", []),
            ("consumer", "writer-consumer", ["alpha"]),
        ):
            path = f"Modules/{module}/src/value.txt"
            entries.append({
                "module_id": module, "writer_identity": writer,
                "owned_paths": [path], "depends_on": dependencies,
                "worktree_ref": f"factory-worktree:{module}:1234567890abcdef",
                "lease_keys": [f"module:{module}", f"path:{path}"],
            })
        return {
            "schema_version": "dps.worktree-plan/v1",
            "contract_id": "worktree.plan/v1",
            "producer_module": "factory-worktree-manager",
            "soul_id": None, "device_binding_id": None, "platform_account_id": None,
            "trace_id": "trace_" + "1" * 32,
            "idempotency_key": "idem_" + "2" * 64,
            "occurred_at": "2026-07-14T00:00:00Z", "privacy_class": "internal",
            "plan_id": "worktree:" + "a" * 32,
            "change_plan_id": "change:" + "b" * 32,
            "instruction_receipt_id": "instruction:" + "c" * 32,
            "baseline_commit": self.baseline,
            "trusted_policy_sha256": self.policy_hash,
            "entries": entries, "contract_worktree": None,
        }

    def _lease(self, module, token):
        entry = next(item for item in self.plan["entries"] if item["module_id"] == module)
        tokens = {key: token for key in entry["lease_keys"]}
        return {
            "schema_version": "dps.worktree-lease/v1",
            "contract_id": "worktree.lease/v1", "producer_module": "factory-worktree-manager",
            "soul_id": None, "device_binding_id": None, "platform_account_id": None,
            "trace_id": "trace_" + "1" * 32,
            "idempotency_key": "idem_" + hashlib.sha256(("lease-materializer-" + module).encode("utf-8")).hexdigest(),
            "occurred_at": "2026-07-14T00:00:00Z", "privacy_class": "internal",
            "lease_id": "lease:" + format(token, "x") * 32,
            "plan_id": self.plan["plan_id"], "status": "ACTIVE",
            "holder_identity": entry["writer_identity"], "lock_keys": list(entry["lease_keys"]),
            "lock_tokens": tokens, "fencing_token": token,
            "acquired_at": "2026-07-14T00:00:00Z", "expires_at": "2026-07-14T01:00:00Z",
        }

    def _verify_fence(self, lease):
        current = next(
            (item for item in self.leases.values() if item["lease_id"] == lease["lease_id"]),
            None,
        )
        if current is None:
            return {"verified": False}
        return {
            "verified": True, "fact_id": current["lease_id"],
            "fact_sha256": hashlib.sha256(json.dumps(current, sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
            "plan_id": current["plan_id"], "lock_tokens": current["lock_tokens"],
            "fencing_token": current["fencing_token"],
        }

    def _verify_plan(self, plan):
        return {
            "verified": True, "fact_id": plan["plan_id"],
            "fact_sha256": hashlib.sha256(json.dumps(plan, sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
            "baseline_commit": plan["baseline_commit"],
            "trusted_policy_sha256": plan["trusted_policy_sha256"],
            "instruction_receipt_id": plan["instruction_receipt_id"],
            "instruction_receipt_status": "BOUND",
        }

    def _write_and_test(self, paths, module):
        (paths[module] / "Modules" / module / "src" / "value.txt").write_text(
            "upgraded\n", encoding="utf-8"
        )
        return self.materializer.commit_and_test(module, self.leases[module])

    def test_two_independent_writers_test_in_parallel_then_dependency_and_merge_head_retest(self):
        paths = self.materializer.materialize()
        with self.assertRaisesRegex(MaterializationError, "before providers"):
            self._write_and_test(paths, "consumer")
        start = time.monotonic()
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(self._write_and_test, paths, module) for module in ("alpha", "beta")]
            evidence = [future.result(timeout=10) for future in futures]
        elapsed = time.monotonic() - start
        self.assertLess(elapsed, 1.1, "two 0.6s checks did not execute concurrently")
        self.assertEqual(["PASS", "PASS"], [item["status"] for item in evidence])
        consumer = self.materializer.commit_and_test("consumer", self.leases["consumer"])
        self.assertEqual("PASS", consumer["status"])
        merged = self.materializer.merge_and_retest()
        self.assertEqual("PASS", merged["status"])
        self.assertEqual(["alpha", "beta", "consumer"], merged["merged_entries"])
        self.assertNotEqual(self.baseline, merged["commit"])

    def test_overlapping_paths_and_forged_policy_are_rejected(self):
        plan = self._plan()
        plan["entries"][1]["owned_paths"] = list(plan["entries"][0]["owned_paths"])
        plan["entries"][1]["lease_keys"] = list(plan["entries"][0]["lease_keys"])
        with tempfile.TemporaryDirectory(dir=Path(self.outer.name), prefix="overlap-") as root:
            with self.assertRaisesRegex(MaterializationError, "escapes|overlapping"):
                GitWorktreeMaterializer(
                    self.repo, root, plan, trusted_policy_sha256=self.policy_hash,
                    module_test_argv={m: [sys.executable, "run_checks.py", m] for m in ("alpha", "beta", "consumer")},
                    merge_test_argv=[sys.executable, "run_checks.py", "alpha", "beta", "consumer"],
                    plan_verifier=self._verify_plan,
                    fence_verifier=self._verify_fence,
                )
        with tempfile.TemporaryDirectory(dir=Path(self.outer.name), prefix="policy-") as root:
            with self.assertRaisesRegex(MaterializationError, "trusted process facts"):
                GitWorktreeMaterializer(
                    self.repo, root, self.plan, trusted_policy_sha256="f" * 64,
                    module_test_argv={m: [sys.executable, "run_checks.py", m] for m in ("alpha", "beta", "consumer")},
                    merge_test_argv=[sys.executable, "run_checks.py", "alpha", "beta", "consumer"],
                    plan_verifier=self._verify_plan,
                    fence_verifier=self._verify_fence,
                )
        with tempfile.TemporaryDirectory(dir=Path(self.outer.name), prefix="fact-") as root:
            with self.assertRaisesRegex(MaterializationError, "immutable fresh-receipt"):
                GitWorktreeMaterializer(
                    self.repo, root, self.plan, trusted_policy_sha256=self.policy_hash,
                    module_test_argv={m: [sys.executable, "run_checks.py", m] for m in ("alpha", "beta", "consumer")},
                    merge_test_argv=[sys.executable, "run_checks.py", "alpha", "beta", "consumer"],
                    plan_verifier=lambda _plan: {"verified": False},
                    fence_verifier=self._verify_fence,
                )

    def test_stale_baseline_symlink_and_fence_stop_before_commit(self):
        paths = self.materializer.materialize()
        source = paths["alpha"] / "Modules" / "alpha" / "src" / "value.txt"
        source.unlink(); source.symlink_to("/etc/hosts")
        with self.assertRaisesRegex(MaterializationError, "symlink"):
            self.materializer.commit_and_test("alpha", self.leases["alpha"])
        source.unlink(); source.write_text("upgraded\n", encoding="utf-8")
        forged = dict(self.leases["alpha"]); forged["fencing_token"] = 999
        with self.assertRaises(StaleFence):
            self.materializer.commit_and_test("alpha", forged)

        self.materializer.cleanup()
        (self.repo / "new.txt").write_text("new\n", encoding="utf-8")
        self._git(self.repo, "add", "new.txt")
        self._git(
            self.repo, "-c", "user.name=DPS Test", "-c", "user.email=dps@example.invalid",
            "commit", "-qm", "advance",
        )
        with tempfile.TemporaryDirectory(dir=Path(self.outer.name), prefix="stale-") as root:
            with self.assertRaisesRegex(MaterializationError, "stale"):
                GitWorktreeMaterializer(
                    self.repo, root, self.plan, trusted_policy_sha256=self.policy_hash,
                    module_test_argv={m: [sys.executable, "run_checks.py", m] for m in ("alpha", "beta", "consumer")},
                    merge_test_argv=[sys.executable, "run_checks.py", "alpha", "beta", "consumer"],
                    plan_verifier=self._verify_plan,
                    fence_verifier=self._verify_fence,
                )

    def test_untrusted_shell_test_argv_is_rejected(self):
        with tempfile.TemporaryDirectory(dir=Path(self.outer.name), prefix="shell-") as root:
            with self.assertRaisesRegex(MaterializationError, "shell"):
                GitWorktreeMaterializer(
                    self.repo, root, self.plan, trusted_policy_sha256=self.policy_hash,
                    module_test_argv={
                        "alpha": ["sh", "-c", "true"],
                        "beta": [sys.executable, "run_checks.py", "beta"],
                        "consumer": [sys.executable, "run_checks.py", "consumer"],
                    },
                    merge_test_argv=[sys.executable, "run_checks.py", "alpha", "beta", "consumer"],
                    plan_verifier=self._verify_plan,
                    fence_verifier=self._verify_fence,
                )


if __name__ == "__main__":
    unittest.main()
