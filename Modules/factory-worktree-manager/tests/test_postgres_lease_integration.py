import hashlib
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import uuid
from pathlib import Path


SOURCE = Path(__file__).parents[1] / "src"
sys.path.insert(0, str(SOURCE))

try:
    import psycopg
    from psycopg import sql
except ImportError as exc:
    psycopg = None
    PSYCOPG_ERROR = exc
else:
    PSYCOPG_ERROR = None

if psycopg is not None:
    from postgres_lease_store import (
        PINNED_POSTGRES_SERVER_VERSION,
        PINNED_PSYCOPG_VERSION,
        PostgresLeaseStore,
    )
    from worktree_manager import LeaseConflict, StaleFence


DSN = os.environ.get("DPS_TEST_POSTGRES_URI")


class PostgreSQLLeaseIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        missing = []
        if not DSN:
            missing.append("DPS_TEST_POSTGRES_URI")
        if psycopg is None:
            missing.append(f"locked psycopg driver ({PSYCOPG_ERROR})")
        if missing:
            raise RuntimeError(
                "INFRA_ERROR: required PostgreSQL lease integration inputs missing: "
                + ", ".join(missing)
            )
        cls.schema = "factory_wtm_" + uuid.uuid4().hex[:20]
        cls.store = PostgresLeaseStore(DSN, schema=cls.schema)
        cls.store.apply_migrations()
        cls.store.apply_migrations()

    @classmethod
    def tearDownClass(cls):
        if DSN and psycopg is not None and hasattr(cls, "schema"):
            with psycopg.connect(DSN, autocommit=True) as connection:
                connection.execute(
                    sql.SQL("DROP SCHEMA IF EXISTS {} CASCADE").format(
                        sql.Identifier(cls.schema)
                    )
                )

    def setUp(self):
        with self.store._connect() as connection:
            connection.execute("TRUNCATE active_locks, lease_records, fencing_counters")

    @staticmethod
    def envelope(key):
        return {
            "soul_id": None, "device_binding_id": None, "platform_account_id": None,
            "trace_id": "trace_" + "1" * 32,
            "idempotency_key": "idem_" + hashlib.sha256(key.encode("utf-8")).hexdigest(),
        }

    def acquire(self, store, writer, key, ttl=10):
        return store.acquire(
            plan_id="worktree:" + "a" * 32, holder_identity=writer,
            lock_keys=["module:alpha", "path:Modules/alpha/src/domain.py"],
            ttl_seconds=ttl, envelope=self.envelope(key),
        )

    def test_locked_driver_postgresql_18_4_and_idempotency(self):
        self.assertEqual(PINNED_PSYCOPG_VERSION, psycopg.__version__)
        self.assertEqual(PINNED_POSTGRES_SERVER_VERSION, 180004)
        first = self.acquire(self.store, "writer-1", "postgres-key-1")
        replay = self.acquire(self.store, "writer-1", "postgres-key-1")
        self.assertEqual(first, replay)
        with self.assertRaises(LeaseConflict):
            self.acquire(self.store, "writer-2", "postgres-key-1")

    def test_concurrent_conflict_has_one_transactional_winner(self):
        barrier = threading.Barrier(2)
        results = []
        lock = threading.Lock()

        def contender(writer, key):
            store = PostgresLeaseStore(DSN, schema=self.schema)
            barrier.wait(timeout=5)
            try:
                lease = self.acquire(store, writer, key)
                outcome = ("PASS", lease["lease_id"])
            except LeaseConflict:
                outcome = ("CONFLICT", writer)
            with lock:
                results.append(outcome)

        threads = [
            threading.Thread(target=contender, args=("writer-1", "postgres-race-1")),
            threading.Thread(target=contender, args=("writer-2", "postgres-race-2")),
        ]
        for thread in threads: thread.start()
        for thread in threads: thread.join(timeout=10)
        self.assertFalse(any(thread.is_alive() for thread in threads))
        self.assertEqual(["CONFLICT", "PASS"], sorted(item[0] for item in results))

    def test_expired_takeover_increments_fence_and_old_writer_cannot_revive(self):
        old = self.acquire(self.store, "writer-old", "postgres-expiry-1", ttl=1)
        time.sleep(1.1)
        reopened = PostgresLeaseStore(DSN, schema=self.schema)
        new = self.acquire(reopened, "writer-new", "postgres-expiry-2", ttl=10)
        for key in old["lock_tokens"]:
            self.assertGreater(new["lock_tokens"][key], old["lock_tokens"][key])
        with self.assertRaises(StaleFence):
            self.store.assert_fence(old["lease_id"], old["lock_tokens"])
        with self.assertRaises(StaleFence):
            self.acquire(self.store, "writer-old", "postgres-expiry-1", ttl=10)
        reopened.assert_fence(new["lease_id"], new["lock_tokens"])

    def test_new_process_recovers_active_fence_and_immutable_fact(self):
        lease = self.acquire(self.store, "writer-1", "postgres-reopen-1")
        with tempfile.TemporaryDirectory() as directory:
            lease_path = Path(directory) / "lease.json"
            lease_path.write_text(json.dumps(lease, sort_keys=True), encoding="utf-8")
            environment = {
                "DPS_TEST_POSTGRES_URI": DSN,
                "DPS_TEST_SCHEMA": self.schema,
                "DPS_TEST_LEASE_JSON": str(lease_path),
                "PATH": os.environ.get("PATH", ""),
                "HOME": os.environ.get("HOME", ""),
                "PYTHONPATH": str(SOURCE),
                "PYTHONDONTWRITEBYTECODE": "1",
            }
            completed = subprocess.run(
                [sys.executable, str(Path(__file__).parent / "postgres_reopen_probe.py")],
                env=environment, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                stderr=subprocess.PIPE, text=True, timeout=20, check=False, shell=False,
            )
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual("OK", completed.stdout.strip())

    def test_revoke_and_tampered_contract_are_rejected(self):
        lease = self.acquire(self.store, "writer-1", "postgres-revoke-1")
        fact = self.store.verify_fence_fact(lease)
        self.assertEqual(lease["lease_id"], fact["fact_id"])
        tampered = json.loads(json.dumps(lease)); tampered["holder_identity"] = "attacker"
        with self.assertRaises(StaleFence):
            self.store.verify_fence_fact(tampered)
        self.store.revoke(lease["lease_id"])
        with self.assertRaises(StaleFence):
            self.store.assert_fence(lease["lease_id"], lease["lock_tokens"])


if __name__ == "__main__":
    unittest.main()
