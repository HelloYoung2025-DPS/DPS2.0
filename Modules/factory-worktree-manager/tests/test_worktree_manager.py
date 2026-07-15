import hashlib
import importlib.util
import json
import pathlib
import sys
import tempfile
import unittest


MODULE_ROOT = pathlib.Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "worktree_manager", MODULE_ROOT / "src" / "worktree_manager.py"
)
WORKTREE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = WORKTREE
SPEC.loader.exec_module(WORKTREE)


class WorktreeManagerTests(unittest.TestCase):
    def setUp(self):
        self.outer = tempfile.TemporaryDirectory()
        outer = pathlib.Path(self.outer.name)
        self.root = outer / "repo"
        self.root.mkdir()
        self._module("alpha", provides=["alpha.event"])
        self._module("consumer", consumes=["alpha.event"], dependencies=["alpha"])
        roles = {
            "impact_planner": ["planner"],
            "contract_architect": ["architect"],
            "module_implementer": ["implementer-1", "implementer-2"],
            "independent_test_agent": ["tester"],
            "evidence_auditor": ["auditor"],
            "release_approver": ["human-approver"],
        }
        self.policy_value = {
            "schema_version": "dps.factory-impact-policy/v1",
            "policy_id": "fixture-policy",
            "status": "non-production-template",
            "roles": roles,
            "check_catalog": ["module.unit"],
            "risk_required_checks": {key: ["module.unit"] for key in ("R0", "R1", "R2", "R3")},
        }
        self.policy_document = json.dumps(
            self.policy_value, sort_keys=True, separators=(",", ":")
        ).encode()
        digest = hashlib.sha256(self.policy_document).hexdigest()
        self.policy = WORKTREE.TrustedWriterPolicy.from_verified_document(
            self.policy_document,
            expected_sha256=digest,
            verifier=lambda value, actual: value.get("policy_id") == "fixture-policy" and actual == digest,
        )
        self.planner = WORKTREE.WorktreePlanner(self.root)
        self.clock_value = [100.0]
        self.store = WORKTREE.ExternalSqliteLeaseStore(
            self.root, outer / "external" / "leases.sqlite3",
            clock=lambda: self.clock_value[0],
        )

    def tearDown(self):
        self.outer.cleanup()

    def _module(self, module_id, provides=None, consumes=None, dependencies=None):
        provides = provides or []
        consumes = consumes or []
        dependencies = dependencies or []
        root = self.root / "Modules" / module_id
        for directory in ("src", "tests", "contracts/provided"):
            (root / directory).mkdir(parents=True, exist_ok=True)
        (root / "src" / "domain.py").write_text("value = 1\n", encoding="utf-8")
        provided_items = []
        for contract_id in provides:
            source = f"Modules/{module_id}/contracts/provided/{contract_id}.v1.schema.json"
            (self.root / source).write_text("{}", encoding="utf-8")
            provided_items.append({"contractId": contract_id, "source": source})
        manifest = {
            "module": {"id": module_id},
            "paths": {"actualRoot": f"Modules/{module_id}", "owned": [f"Modules/{module_id}/**"]},
            "contracts": {
                "provided": provided_items,
                "consumed": [{"contractId": item} for item in consumes],
            },
            "dependencies": [{"moduleId": item} for item in dependencies],
        }
        (root / "module.yaml").write_text(json.dumps(manifest), encoding="utf-8")

    def _change_plan(self):
        roles = {key: list(self.policy.roles[key]) for key in sorted(self.policy.roles)}
        return {
            "contract_id": "module.change.plan/v1",
            "producer_module": "factory-impact-analyzer",
            "soul_id": None,
            "device_binding_id": None,
            "platform_account_id": None,
            "trace_id": "trace_" + "1" * 32,
            "idempotency_key": "idem_" + "2" * 64,
            "occurred_at": "2026-07-14T01:00:00Z",
            "plan_id": "change:" + "a" * 32,
            "instruction_receipt_id": "instruction:" + "b" * 32,
            "baseline_commit": "c" * 40,
            "affected_modules": ["alpha", "consumer"],
            "requested_paths": [
                "Modules/alpha/contracts/provided/alpha.event.v1.schema.json",
                "Modules/alpha/src/domain.py",
                "Modules/consumer/src/domain.py",
            ],
            "public_contract_changes": ["alpha.event"],
            "dependency_edges": [{"consumer": "consumer", "provider": "alpha"}],
            "parallel_waves": [["alpha"], ["consumer"]],
            "role_assignments": roles,
            "trusted_policy_sha256": self.policy.digest,
        }

    @staticmethod
    def _receipt():
        return {
            "contract_id": "instruction.receipt/v1",
            "status": "BOUND",
            "receipt_id": "instruction:" + "b" * 32,
            "baseline_commit": "c" * 40,
        }

    @staticmethod
    def _envelope(key="lease-key-0001"):
        return {
            "soul_id": None,
            "device_binding_id": None,
            "platform_account_id": None,
            "trace_id": "trace_" + "1" * 32,
            "idempotency_key": "idem_" + hashlib.sha256(key.encode("utf-8")).hexdigest(),
        }

    def test_plan_has_one_writer_per_module_and_one_contract_worktree(self):
        plan = self.planner.create_plan(self._change_plan(), self._receipt(), self.policy)
        self.assertEqual(2, len(plan["entries"]))
        self.assertEqual(2, len({item["writer_identity"] for item in plan["entries"]}))
        self.assertEqual(["alpha.event"], plan["contract_worktree"]["contract_ids"])
        self.assertNotIn(
            "Modules/alpha/contracts/provided/alpha.event.v1.schema.json",
            plan["entries"][0]["owned_paths"],
        )

    def test_forged_roles_policy_digest_and_graph_are_rejected(self):
        change = self._change_plan()
        change["role_assignments"]["module_implementer"] = ["attacker"]
        with self.assertRaisesRegex(WORKTREE.WorktreeError, "not trusted"):
            self.planner.create_plan(change, self._receipt(), self.policy)
        change = self._change_plan()
        change["dependency_edges"] = []
        with self.assertRaisesRegex(WORKTREE.WorktreeError, "graph"):
            self.planner.create_plan(change, self._receipt(), self.policy)

    def test_traversal_and_symlink_are_rejected(self):
        change = self._change_plan()
        change["requested_paths"][1] = "Modules/alpha/../consumer/src/domain.py"
        with self.assertRaises(WORKTREE.WorktreeError):
            self.planner.create_plan(change, self._receipt(), self.policy)
        outside = self.root.parent / "outside.py"
        outside.write_text("outside", encoding="utf-8")
        link = self.root / "Modules/alpha/src/link.py"
        link.symlink_to(outside)
        change = self._change_plan()
        change["requested_paths"][1] = "Modules/alpha/src/link.py"
        with self.assertRaisesRegex(WORKTREE.WorktreeError, "symlink"):
            self.planner.create_plan(change, self._receipt(), self.policy)

    def test_external_store_rejects_repository_database(self):
        with self.assertRaisesRegex(WORKTREE.WorktreeError, "external"):
            WORKTREE.ExternalSqliteLeaseStore(
                self.root, self.root / "state" / "leases.sqlite3"
            )

    def test_idempotent_lease_and_conflicting_writer(self):
        plan_id = "worktree:" + "a" * 32
        first = self.store.acquire(
            plan_id=plan_id, holder_identity="writer-1",
            lock_keys=["module:alpha", "path:Modules/alpha/src/domain.py"],
            ttl_seconds=10, envelope=self._envelope(),
        )
        replay = self.store.acquire(
            plan_id=plan_id, holder_identity="writer-1",
            lock_keys=["path:Modules/alpha/src/domain.py", "module:alpha"],
            ttl_seconds=10, envelope=self._envelope(),
        )
        self.assertEqual(first["lock_tokens"], replay["lock_tokens"])
        with self.assertRaises(WORKTREE.LeaseConflict):
            self.store.acquire(
                plan_id=plan_id, holder_identity="writer-2",
                lock_keys=["module:alpha"], ttl_seconds=10,
                envelope=self._envelope("lease-key-0002"),
            )

    def test_expired_writer_cannot_revive_after_higher_fence(self):
        plan_id = "worktree:" + "a" * 32
        old = self.store.acquire(
            plan_id=plan_id, holder_identity="writer-1", lock_keys=["module:alpha"],
            ttl_seconds=10, envelope=self._envelope(),
        )
        self.clock_value[0] = 111.0
        new = self.store.acquire(
            plan_id=plan_id, holder_identity="writer-2", lock_keys=["module:alpha"],
            ttl_seconds=10, envelope=self._envelope("lease-key-0002"),
        )
        self.assertGreater(new["lock_tokens"]["module:alpha"], old["lock_tokens"]["module:alpha"])
        with self.assertRaises(WORKTREE.StaleFence):
            self.store.assert_fence(old["lease_id"], old["lock_tokens"])
        self.store.assert_fence(new["lease_id"], new["lock_tokens"])

    def test_revoked_fence_is_rejected(self):
        lease = self.store.acquire(
            plan_id="worktree:" + "a" * 32, holder_identity="writer-1",
            lock_keys=["module:alpha"], ttl_seconds=10,
            envelope=self._envelope(),
        )
        self.store.revoke(lease["lease_id"])
        with self.assertRaises(WORKTREE.StaleFence):
            self.store.assert_fence(lease["lease_id"], lease["lock_tokens"])

    def test_lease_identity_must_be_canonical(self):
        envelope = self._envelope()
        envelope["soul_id"] = "soul_readable"
        with self.assertRaisesRegex(WORKTREE.WorktreeError, "identity"):
            self.store.acquire(
                plan_id="worktree:" + "a" * 32, holder_identity="writer-1",
                lock_keys=["module:alpha"], ttl_seconds=10, envelope=envelope,
            )

    def test_sqlite_truth_rejects_noncanonical_idempotency_even_when_api_is_bypassed(self):
        invalid_values = (
            "idem_" + "a" * 63,
            "idem_" + "A" * 64,
            "idem_" + "a" * 64 + "\n",
            "legacy-idempotency-key",
        )
        connection = self.store._connect()
        try:
            for index, invalid in enumerate(invalid_values):
                with self.subTest(invalid=repr(invalid)):
                    with self.assertRaises(WORKTREE.sqlite3.IntegrityError):
                        connection.execute(
                            "INSERT INTO lease_records(lease_id, plan_id, holder_identity, idempotency_key, "
                            "lock_keys_json, lock_tokens_json, acquired_at, expires_at, status) "
                            "VALUES (?, ?, ?, ?, '[]', '{}', 100.0, 110.0, 'ACTIVE')",
                            (f"lease:invalid-{index}", "worktree:" + "a" * 32, "writer", invalid),
                        )
        finally:
            connection.close()


if __name__ == "__main__":
    unittest.main()
