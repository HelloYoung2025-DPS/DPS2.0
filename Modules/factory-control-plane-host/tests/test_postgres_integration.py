from __future__ import annotations

import concurrent.futures
import json
import os
import secrets
import sys
import threading
import unittest
from pathlib import Path

MODULE_ROOT = Path(__file__).resolve().parents[1]
SOURCE_ROOT = (MODULE_ROOT / "src").resolve(strict=True)
if SOURCE_ROOT.parent != MODULE_ROOT:
    raise RuntimeError("test source root escaped its module")
sys.path.insert(0, str(SOURCE_ROOT))

import psycopg

from factory_control_plane_host import (
    FactoryControlPlaneHost,
    FactoryHostError,
    IdempotencyConflict,
    IllegalTransition,
    SimulationReceiptVerifier,
    StaticRuntimeControlAuthority,
    StaleFence,
    sha256,
    utc_now,
    validate_event_stream,
)
from postgres_repository import (
    PostgresSchemaMigrator, PostgresWorkflowRepository, discover_migrations,
    intake_upgrade_intent_sha256,
)
from provider_verifier_fixture import build_test_provider_verifier
from simulation_adapter import (
    CrashAfterProviderSuccessAdapter,
    DeterministicSimulationAdapter,
    SimulationExternalAuthority,
    SimulationRoleDirectory,
)


PROVIDER_VERIFIER = build_test_provider_verifier(Path(__file__).resolve().parents[3])


def request(workflow_id):
    return {
        "schema_version": "1.0.0", "contract_id": "factory.workflow.request/v1",
        "producer_module": "factory-control-plane-host", "soul_id": None,
        "device_binding_id": None, "platform_account_id": None,
        "trace_id": "trace_" + "7" * 32, "idempotency_key": "idem_" + "8" * 64,
        "occurred_at": "2026-07-14T00:00:00Z", "privacy_class": "internal",
        "workflow_id": workflow_id, "mode": "SIMULATION", "risk_tier": "R1",
        "baseline_commit": "e" * 40, "target_modules": ["factory-control-plane-host"],
        "requested_paths": ["Modules/factory-control-plane-host/src/factory_control_plane_host.py"],
        "public_contract_changes": [], "external_context_ref": None,
    }


def host(repository, adapter):
    return FactoryControlPlaneHost(
        repository, SimulationRoleDirectory(), adapter,
        SimulationReceiptVerifier(), PROVIDER_VERIFIER, SimulationExternalAuthority(),
        StaticRuntimeControlAuthority(),
    )


def intake_replay_payload(seed):
    intent_token = sha256({"seed": seed, "claim": "intent"})[:32]
    requester_token = sha256({"seed": seed, "claim": "requester"})[:32]
    approval_token = sha256({"seed": seed, "claim": "approval"})[:32]
    value = {
        "schema_version": "dps.upgrade-intent/v2",
        "contract_id": "upgrade.intent/v2",
        "producer_module": "factory-upgrade-intake",
        "intent_id": "intent:" + intent_token,
        "idempotency_key": "idem_" + sha256({"seed": seed, "claim": "idempotency"}),
        "requester_auth_nonce": "nonce_" + requester_token,
        "authorization": {"approval_nonce": "nonce_" + approval_token},
        "upgrade_intent_sha256": "0" * 64,
    }
    value["upgrade_intent_sha256"] = intake_upgrade_intent_sha256(value)
    return value


def intake_replay_receipt(workflow_id, request_id, payload):
    return {
        "workflow_id": workflow_id,
        "request_id": request_id,
        "target_module": "factory-upgrade-intake",
        "operation": "validate-intent",
        "outputs": [{
            "contract_id": "upgrade.intent/v2",
            "producer_module": "factory-upgrade-intake",
            "payload_sha256": sha256(payload),
            "payload": payload,
        }],
    }


def native_stop_trust_fact(receipt_id, variant):
    release_bom_sha256 = "9" * 64
    activation_sha256 = "a" * 64
    native_sha256 = sha256({"variant": variant, "set": "native"})
    route_sha256 = sha256({"variant": variant, "set": "route"})
    challenge_sha256 = sha256({"variant": variant, "set": "challenge"})
    authority_sets_sha256 = sha256({
        "native": native_sha256,
        "route": route_sha256,
        "challenge": challenge_sha256,
    })
    receipt = {
        "schema_version": "1.0.0",
        "contract_id": "release.bom.native.stop.authority.trust/v1",
        "producer_module": "factory-release-controller",
        "receipt_id": receipt_id,
        "release_bom_id": "release-bom:" + sha256({"variant": variant})[:32],
        "release_bom_sha256": release_bom_sha256,
        "integration_commit": "b" * 40,
        "release_bom_generation": 1,
        "activation_token_sha256": activation_sha256,
        "trust_policy_id": "native-stop-trust-policy-" + variant,
        "native_stop_authorities_sha256": native_sha256,
        "device_route_assignment_authorities_sha256": route_sha256,
        "native_stop_challenge_authorities_sha256": challenge_sha256,
        "authority_sets_sha256": authority_sets_sha256,
    }
    canonical = json.dumps(
        receipt, sort_keys=True, separators=(",", ":"), ensure_ascii=False,
        allow_nan=False,
    )
    return {
        "verified": True,
        "fact_id": receipt_id,
        "fact_kind": "NATIVE_STOP_AUTHORITY_TRUST",
        "contract_id": "release.bom.native.stop.authority.trust/v1",
        "receipt_id": receipt_id,
        "receipt_sha256": sha256(canonical.encode("utf-8")),
        "canonical_receipt_utf8": canonical,
        "release_bom_id": receipt["release_bom_id"],
        "release_bom_sha256": release_bom_sha256,
        "integration_commit": receipt["integration_commit"],
        "release_bom_generation": 1,
        "activation_token_sha256": activation_sha256,
        "trust_policy_id": receipt["trust_policy_id"],
        "native_stop_authorities_sha256": native_sha256,
        "device_route_assignment_authorities_sha256": route_sha256,
        "native_stop_challenge_authorities_sha256": challenge_sha256,
        "authority_sets_sha256": authority_sets_sha256,
        "provider_attestation": {
            "attestation_id": "public-attestation-" + variant,
            "key_id": "public-provider-key-001",
            "signature": "public-test-signature-" + variant,
        },
    }


class PostgresIntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.admin_dsn = os.environ.get("DPS_TEST_POSTGRES_ADMIN_URI")
        cls.runtime_dsn = os.environ.get("DPS_TEST_POSTGRES_RUNTIME_URI")
        if not cls.admin_dsn or not cls.runtime_dsn:
            raise RuntimeError("INFRA_ERROR: distinct DPS_TEST_POSTGRES_ADMIN_URI and DPS_TEST_POSTGRES_RUNTIME_URI are required")
        identities = []
        for name, dsn in (("admin", cls.admin_dsn), ("runtime", cls.runtime_dsn)):
            try:
                with psycopg.connect(dsn) as connection:
                    version = str(connection.execute("SHOW server_version").fetchone()[0])
                    identities.append(str(connection.execute("SELECT current_user").fetchone()[0]))
            except Exception as exc:
                raise RuntimeError("INFRA_ERROR: real PostgreSQL %s identity is unreachable" % name) from exc
            if version != "18.4":
                raise RuntimeError("INFRA_ERROR: PostgreSQL 18.4 required for %s; got %s" % (name, version))
        if identities[0] == identities[1]:
            raise RuntimeError("INFRA_ERROR: migration/admin and runtime PostgreSQL roles must be distinct")
        cls.runtime_role = identities[1]

    def setUp(self):
        self.schema = "factory_host_test_" + secrets.token_hex(8)
        PostgresSchemaMigrator(
            self.admin_dsn, self.runtime_role, schema=self.schema,
        ).migrate()
        self.repository = PostgresWorkflowRepository(self.runtime_dsn, schema=self.schema)

    def tearDown(self):
        with psycopg.connect(self.admin_dsn, autocommit=True) as connection:
            connection.execute("DROP SCHEMA IF EXISTS %s CASCADE" % self.schema)

    def test_restart_recovery_idempotency_fencing_and_append_only_tables(self):
        adapter = DeterministicSimulationAdapter()
        first = host(self.repository, adapter)
        workflow_id = "upgrade:factory-host-pg-" + secrets.token_hex(6)
        raw = request(workflow_id)
        first.start(raw)
        self.assertEqual(first.start(raw)["state"], "REQUESTED")

        old_fence = self.repository.acquire_fence(workflow_id, "old-worker", utc_now())
        new_fence = self.repository.acquire_fence(workflow_id, "new-worker", utc_now())
        self.assertGreater(new_fence, old_fence)
        with self.assertRaises(StaleFence):
            self.repository.transition(
                workflow_id, "FAILED", "STATE_TRANSITIONED", {"reason": "STALE"},
                "stale-transition", old_fence, utc_now(),
            )

        restarted_repository = PostgresWorkflowRepository(self.runtime_dsn, schema=self.schema)
        restarted = host(restarted_repository, adapter)
        status = restarted.run_until_blocked(workflow_id, "restarted-worker", maximum_steps=200)
        self.assertEqual("COMPLETED", status["state"])
        validate_event_stream(restarted_repository.events(workflow_id))

        conflict = request(workflow_id)
        conflict["risk_tier"] = "R2"
        with self.assertRaises(IdempotencyConflict):
            restarted.start(conflict)

        for statement in (
            "UPDATE %s.workflow_request SET request_sha256='%s' WHERE workflow_id='%s'" % (self.schema, "0" * 64, workflow_id),
            "DELETE FROM %s.workflow_request WHERE workflow_id='%s'" % (self.schema, workflow_id),
            "TRUNCATE TABLE %s.workflow_request CASCADE" % self.schema,
        ):
            with self.subTest(statement=statement), self.assertRaises(psycopg.Error):
                with psycopg.connect(self.runtime_dsn) as connection:
                    connection.execute(statement)

        privileged_statements = (
            "ALTER TABLE %s.workflow_request ADD COLUMN forbidden integer" % self.schema,
            "ALTER TABLE %s.workflow_request DISABLE TRIGGER ALL" % self.schema,
            "DROP TABLE %s.workflow_request CASCADE" % self.schema,
        )
        for statement in privileged_statements:
            with self.subTest(statement=statement), self.assertRaises(psycopg.Error):
                with psycopg.connect(self.runtime_dsn) as connection:
                    connection.execute(statement)

    def test_crash_window_replays_same_request_after_process_restart(self):
        deterministic = DeterministicSimulationAdapter()
        crashing = CrashAfterProviderSuccessAdapter(deterministic, "verify-merge-head")
        workflow_id = "upgrade:factory-host-pg-crash-" + secrets.token_hex(5)
        first = host(self.repository, crashing)
        first.start(request(workflow_id))
        with self.assertRaisesRegex(RuntimeError, "SIMULATED_PROCESS_CRASH"):
            first.run_until_blocked(workflow_id, "before-crash", maximum_steps=100)
        before = [call for call in deterministic.calls if call["operation"] == "verify-merge-head"]
        self.assertEqual(1, len(before))

        restarted_repository = PostgresWorkflowRepository(self.runtime_dsn, schema=self.schema)
        restarted = host(restarted_repository, deterministic)
        self.assertEqual(
            "COMPLETED",
            restarted.run_until_blocked(workflow_id, "after-crash", maximum_steps=200)["state"],
        )
        replayed = [call for call in deterministic.calls if call["operation"] == "verify-merge-head"]
        self.assertEqual(2, len(replayed))
        self.assertEqual(replayed[0]["request_id"], replayed[1]["request_id"])
        self.assertGreater(replayed[1]["fencing_token"], replayed[0]["fencing_token"])
        self.assertEqual(replayed[0]["logical_request_sha256"], replayed[1]["logical_request_sha256"])

    def test_invalid_management_fence_and_transition_conflict_are_atomic(self):
        adapter = DeterministicSimulationAdapter()
        service = host(self.repository, adapter)
        workflow_id = "upgrade:factory-host-pg-atomic-" + secrets.token_hex(4)
        service.start(request(workflow_id))
        before = self.repository.latest_fence(workflow_id)
        with self.assertRaises(IllegalTransition):
            self.repository.acquire_fence_if_state(
                workflow_id, "invalid-manager", utc_now(), ("STALE",),
            )
        self.assertEqual(before, self.repository.latest_fence(workflow_id))

        fence = self.repository.acquire_fence(workflow_id, "conflict-worker", utc_now())
        self.repository.transition(
            workflow_id, "REQUESTED", "PHASE_COMPLETED", {"reason": "ONE"},
            "pg-transition-conflict", fence, utc_now(),
        )
        with self.assertRaises(IdempotencyConflict):
            self.repository.transition(
                workflow_id, "REQUESTED", "PHASE_COMPLETED", {"reason": "TWO"},
                "pg-transition-conflict", fence, utc_now(),
            )
        self.assertEqual(
            "TRANSITION_IDEMPOTENCY_CONFLICT",
            self.repository.quarantine_records(workflow_id)[-1]["reason"],
        )

    def test_rolling_back_waiting_external_recovers_after_restart(self):
        adapter = DeterministicSimulationAdapter({
            "run-canary": "FAIL", "execute-rollback": "WAITING_EXTERNAL",
        })
        workflow_id = "upgrade:factory-host-pg-rollback-wait-" + secrets.token_hex(4)
        first = host(self.repository, adapter)
        first.start(request(workflow_id))
        waiting = first.run_until_blocked(workflow_id, "before-wait", maximum_steps=240)
        self.assertEqual("WAITING_EXTERNAL", waiting["state"])
        adapter.failures.pop("execute-rollback")
        restarted = host(
            PostgresWorkflowRepository(self.runtime_dsn, schema=self.schema), adapter,
        )
        self.assertEqual(
            "ROLLED_BACK",
            restarted.resume_waiting(workflow_id, "after-wait-restart")["state"],
        )

    def _prepare_intake_message(self, label):
        adapter = DeterministicSimulationAdapter()
        service = host(self.repository, adapter)
        workflow_id = "upgrade:factory-host-pg-replay-%s-%s" % (
            label, secrets.token_hex(4),
        )
        service.start(request(workflow_id))
        fence = self.repository.acquire_fence(workflow_id, "replay-worker", utc_now())
        self.assertTrue(service._tick(workflow_id, fence))
        messages = self.repository.pending_messages(workflow_id)
        self.assertEqual(1, len(messages))
        self.assertEqual("factory-upgrade-intake", messages[0]["target_module"])
        self.assertEqual("validate-intent", messages[0]["operation"])
        return workflow_id, messages[0], fence

    def _record_intake_concurrently(self, attempts):
        barrier = threading.Barrier(len(attempts))

        def record(attempt):
            workflow_id, request_id, receipt, fence = attempt
            repository = PostgresWorkflowRepository(
                self.runtime_dsn, schema=self.schema,
            )
            barrier.wait(timeout=15)
            try:
                return (
                    "CREATED",
                    repository.record_receipt(
                        workflow_id, request_id, receipt, fence, utc_now(),
                    ),
                )
            except IdempotencyConflict as exc:
                return ("CONFLICT", str(exc))

        with concurrent.futures.ThreadPoolExecutor(
            max_workers=len(attempts), thread_name_prefix="intake-replay",
        ) as executor:
            futures = [executor.submit(record, attempt) for attempt in attempts]
            return [future.result(timeout=20) for future in futures]

    def test_ordered_migration_ledger_is_idempotent_and_detects_hash_drift(self):
        migrations = discover_migrations(MODULE_ROOT / "migrations")
        with psycopg.connect(self.runtime_dsn) as connection:
            rows = connection.execute(
                "SELECT migration_version, migration_name, migration_sha256 "
                "FROM %s.schema_migration ORDER BY migration_version" % self.schema
            ).fetchall()
        self.assertEqual(
            [(item.version, item.name, item.sha256) for item in migrations],
            [(int(row[0]), str(row[1]), str(row[2])) for row in rows],
        )
        PostgresSchemaMigrator(
            self.admin_dsn, self.runtime_role, schema=self.schema,
        ).migrate()
        with psycopg.connect(self.runtime_dsn) as connection:
            self.assertEqual(
                len(migrations),
                int(connection.execute(
                    "SELECT count(*) FROM %s.schema_migration" % self.schema
                ).fetchone()[0]),
            )
        with self.assertRaises(psycopg.Error):
            with psycopg.connect(self.runtime_dsn) as connection:
                connection.execute(
                    "INSERT INTO %s.schema_migration"
                    "(migration_version, migration_name, migration_sha256, applied_at) "
                    "VALUES (999, '999_attack.sql', '%s', clock_timestamp())"
                    % (self.schema, "f" * 64)
                )

        with psycopg.connect(self.admin_dsn, autocommit=True) as connection:
            connection.execute(
                "ALTER TABLE %s.schema_migration DISABLE TRIGGER reject_mutation"
                % self.schema
            )
            connection.execute(
                "UPDATE %s.schema_migration SET migration_sha256='%s' "
                "WHERE migration_version=1" % (self.schema, "0" * 64)
            )
            connection.execute(
                "ALTER TABLE %s.schema_migration ENABLE TRIGGER reject_mutation"
                % self.schema
            )
        with self.assertRaisesRegex(FactoryHostError, "hash drift"):
            PostgresSchemaMigrator(
                self.admin_dsn, self.runtime_role, schema=self.schema,
            ).migrate()

    def test_runtime_role_has_only_exact_migrator_grants(self):
        with psycopg.connect(self.runtime_dsn) as connection:
            role_flags = connection.execute(
                "SELECT rolcanlogin, rolsuper, rolcreaterole, rolcreatedb, rolreplication, "
                "rolbypassrls FROM pg_roles WHERE rolname=current_user"
            ).fetchone()
            self.assertEqual((True, False, False, False, False, False), role_flags)
            self.assertEqual(
                (True, False),
                connection.execute(
                    "SELECT has_schema_privilege(current_user, %s, 'USAGE'), "
                    "has_schema_privilege(current_user, %s, 'CREATE')",
                    (self.schema, self.schema),
                ).fetchone(),
            )
            memberships = connection.execute(
                "SELECT r.rolname FROM pg_roles r "
                "WHERE r.rolname<>current_user "
                "AND pg_has_role(current_user, r.oid, 'MEMBER')"
            ).fetchall()
            self.assertEqual([], memberships)
            table_rows = connection.execute(
                "SELECT c.relname, "
                "has_table_privilege(current_user, c.oid, 'SELECT'), "
                "has_table_privilege(current_user, c.oid, 'INSERT'), "
                "has_table_privilege(current_user, c.oid, 'UPDATE'), "
                "has_table_privilege(current_user, c.oid, 'DELETE'), "
                "has_table_privilege(current_user, c.oid, 'TRUNCATE'), "
                "has_table_privilege(current_user, c.oid, 'REFERENCES'), "
                "has_table_privilege(current_user, c.oid, 'TRIGGER'), "
                "has_table_privilege(current_user, c.oid, 'MAINTAIN') "
                "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
                "WHERE n.nspname=%s AND c.relkind IN ('r','p') ORDER BY c.relname",
                (self.schema,),
            ).fetchall()
            for row in table_rows:
                self.assertEqual(
                    (
                        True, row[0] != "schema_migration", False, False,
                        False, False, False, False,
                    ),
                    tuple(bool(value) for value in row[1:]),
                    row[0],
                )
            column_rows = connection.execute(
                "SELECT c.relname, a.attname, "
                "has_column_privilege(current_user, c.oid, a.attnum, 'SELECT'), "
                "has_column_privilege(current_user, c.oid, a.attnum, 'INSERT'), "
                "has_column_privilege(current_user, c.oid, a.attnum, 'UPDATE'), "
                "has_column_privilege(current_user, c.oid, a.attnum, 'REFERENCES') "
                "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
                "JOIN pg_attribute a ON a.attrelid=c.oid "
                "WHERE n.nspname=%s AND c.relkind IN ('r','p') "
                "AND a.attnum>0 AND NOT a.attisdropped ORDER BY c.relname, a.attnum",
                (self.schema,),
            ).fetchall()
            for row in column_rows:
                self.assertEqual(
                    (True, row[0] != "schema_migration", False, False),
                    tuple(bool(value) for value in row[2:]),
                    "%s.%s" % (row[0], row[1]),
                )
            sequence_rows = connection.execute(
                "SELECT c.relname, "
                "has_sequence_privilege(current_user, c.oid, 'USAGE'), "
                "has_sequence_privilege(current_user, c.oid, 'SELECT'), "
                "has_sequence_privilege(current_user, c.oid, 'UPDATE') "
                "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
                "WHERE n.nspname=%s AND c.relkind='S' ORDER BY c.relname",
                (self.schema,),
            ).fetchall()
            for row in sequence_rows:
                self.assertEqual((True, False, False), tuple(row[1:]), row[0])
            function_rows = connection.execute(
                "SELECT p.proname, has_function_privilege(current_user, p.oid, 'EXECUTE') "
                "FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace "
                "WHERE n.nspname=%s ORDER BY p.proname",
                (self.schema,),
            ).fetchall()
            self.assertTrue(function_rows)
            self.assertTrue(all(not bool(row[1]) for row in function_rows))
            grantable_privileges = connection.execute(
                "SELECT grants.object_kind, grants.object_name, grants.privilege_type "
                "FROM ("
                "SELECT 'schema' AS object_kind, n.nspname AS object_name, "
                "acl.grantee, acl.privilege_type, acl.is_grantable "
                "FROM pg_namespace n CROSS JOIN LATERAL aclexplode(n.nspacl) acl "
                "WHERE n.nspname=%s "
                "UNION ALL "
                "SELECT 'relation', c.relname, acl.grantee, acl.privilege_type, "
                "acl.is_grantable FROM pg_class c "
                "JOIN pg_namespace n ON n.oid=c.relnamespace "
                "CROSS JOIN LATERAL aclexplode(c.relacl) acl WHERE n.nspname=%s "
                "UNION ALL "
                "SELECT 'column', c.relname || '.' || a.attname, acl.grantee, "
                "acl.privilege_type, acl.is_grantable FROM pg_attribute a "
                "JOIN pg_class c ON c.oid=a.attrelid "
                "JOIN pg_namespace n ON n.oid=c.relnamespace "
                "CROSS JOIN LATERAL aclexplode(a.attacl) acl "
                "WHERE n.nspname=%s AND a.attnum>0 AND NOT a.attisdropped "
                "UNION ALL "
                "SELECT 'function', p.proname, acl.grantee, acl.privilege_type, "
                "acl.is_grantable FROM pg_proc p "
                "JOIN pg_namespace n ON n.oid=p.pronamespace "
                "CROSS JOIN LATERAL aclexplode(p.proacl) acl WHERE n.nspname=%s"
                ") grants WHERE grants.grantee=0 OR "
                "(grants.grantee=(SELECT oid FROM pg_roles WHERE rolname=current_user) "
                "AND grants.is_grantable)",
                (self.schema, self.schema, self.schema, self.schema),
            ).fetchall()
            self.assertEqual([], grantable_privileges)

    def test_migrator_resets_acl_drift_to_exact_whitelist(self):
        schema_identifier = psycopg.sql.Identifier(self.schema)
        runtime_identifier = psycopg.sql.Identifier(self.runtime_role)
        with psycopg.connect(self.admin_dsn, autocommit=True) as connection:
            connection.execute(
                psycopg.sql.SQL("GRANT CREATE ON SCHEMA {} TO {}").format(
                    schema_identifier, runtime_identifier,
                )
            )
            connection.execute(
                psycopg.sql.SQL(
                    "GRANT UPDATE ON {}.{} TO {} WITH GRANT OPTION"
                ).format(
                    schema_identifier,
                    psycopg.sql.Identifier("workflow_request"),
                    runtime_identifier,
                )
            )
            connection.execute(
                psycopg.sql.SQL(
                    "GRANT REFERENCES ({}) ON {}.{} TO {} WITH GRANT OPTION"
                ).format(
                    psycopg.sql.Identifier("workflow_id"),
                    schema_identifier,
                    psycopg.sql.Identifier("workflow_request"),
                    runtime_identifier,
                )
            )
            connection.execute(
                psycopg.sql.SQL(
                    "GRANT SELECT ON ALL SEQUENCES IN SCHEMA {} TO {}"
                ).format(schema_identifier, runtime_identifier)
            )
            connection.execute(
                psycopg.sql.SQL("GRANT EXECUTE ON FUNCTION {}.{}() TO PUBLIC").format(
                    schema_identifier, psycopg.sql.Identifier("reject_mutation"),
                )
            )

        PostgresSchemaMigrator(
            self.admin_dsn, self.runtime_role, schema=self.schema,
        ).migrate()

        with psycopg.connect(self.runtime_dsn) as connection:
            self.assertFalse(bool(connection.execute(
                "SELECT has_schema_privilege(current_user, %s, 'CREATE')",
                (self.schema,),
            ).fetchone()[0]))
            self.assertFalse(bool(connection.execute(
                "SELECT has_table_privilege(current_user, %s, 'UPDATE')",
                (self.schema + ".workflow_request",),
            ).fetchone()[0]))
            self.assertFalse(bool(connection.execute(
                "SELECT has_column_privilege(current_user, %s, %s, 'REFERENCES')",
                (self.schema + ".workflow_request", "workflow_id"),
            ).fetchone()[0]))
            sequence_privileges = connection.execute(
                "SELECT has_sequence_privilege(current_user, c.oid, 'SELECT') "
                "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
                "WHERE n.nspname=%s AND c.relkind='S'",
                (self.schema,),
            ).fetchall()
            self.assertTrue(sequence_privileges)
            self.assertTrue(all(not bool(row[0]) for row in sequence_privileges))
            self.assertFalse(bool(connection.execute(
                "SELECT has_function_privilege(current_user, p.oid, 'EXECUTE') "
                "FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace "
                "WHERE n.nspname=%s AND p.proname='reject_mutation'",
                (self.schema,),
            ).fetchone()[0]))

    def test_concurrent_exact_intent_replay_is_idempotent_across_workflows(self):
        payload = intake_replay_payload("concurrent-exact")
        attempts = []
        workflow_ids = []
        for label in ("exact-a", "exact-b"):
            workflow_id, message, fence = self._prepare_intake_message(label)
            workflow_ids.append(workflow_id)
            attempts.append((
                workflow_id,
                message["request_id"],
                intake_replay_receipt(workflow_id, message["request_id"], payload),
                fence,
            ))
        results = self._record_intake_concurrently(attempts)
        self.assertEqual(["CREATED", "CREATED"], sorted(item[0] for item in results))
        self.assertEqual([True, True], sorted(item[1] for item in results))
        with psycopg.connect(self.runtime_dsn) as connection:
            self.assertEqual(
                4,
                int(connection.execute(
                    "SELECT count(*) FROM %s.intake_replay_binding" % self.schema
                ).fetchone()[0]),
            )
            self.assertEqual(
                2,
                int(connection.execute(
                    "SELECT count(*) FROM %s.module_receipt "
                    "WHERE workflow_id IN (%%s, %%s)" % self.schema,
                    tuple(workflow_ids),
                ).fetchone()[0]),
            )
            self.assertEqual(
                2,
                int(connection.execute(
                    "SELECT count(*) FROM %s.outbox_delivery_event "
                    "WHERE workflow_id IN (%%s, %%s) AND status='ACKNOWLEDGED'"
                    % self.schema,
                    tuple(workflow_ids),
                ).fetchone()[0]),
            )
            self.assertEqual(
                0,
                int(connection.execute(
                    "SELECT count(*) FROM %s.intake_replay_conflict" % self.schema
                ).fetchone()[0]),
            )

    def test_concurrent_same_claims_different_digest_has_one_atomic_winner(self):
        first_payload = intake_replay_payload("concurrent-same-claims")
        second_payload = dict(first_payload)
        second_payload["authorization"] = dict(first_payload["authorization"])
        second_payload["requested_stage"] = "canary"
        second_payload["upgrade_intent_sha256"] = intake_upgrade_intent_sha256(
            second_payload,
        )
        attempts = []
        workflow_ids = []
        for label, payload in (
            ("same-claims-a", first_payload),
            ("same-claims-b", second_payload),
        ):
            workflow_id, message, fence = self._prepare_intake_message(label)
            workflow_ids.append(workflow_id)
            attempts.append((
                workflow_id,
                message["request_id"],
                intake_replay_receipt(workflow_id, message["request_id"], payload),
                fence,
            ))
        results = self._record_intake_concurrently(attempts)
        self.assertEqual(["CONFLICT", "CREATED"], sorted(item[0] for item in results))
        with psycopg.connect(self.runtime_dsn) as connection:
            self._assert_one_atomic_replay_winner(
                connection, workflow_ids,
                {
                    first_payload["upgrade_intent_sha256"],
                    second_payload["upgrade_intent_sha256"],
                },
                expected_conflicts=4,
            )

    def test_concurrent_partial_overlap_has_no_deadlock_or_claim_poisoning(self):
        first_payload = intake_replay_payload("concurrent-partial-a")
        second_payload = intake_replay_payload("concurrent-partial-b")
        second_payload["requester_auth_nonce"] = first_payload["requester_auth_nonce"]
        second_payload["upgrade_intent_sha256"] = intake_upgrade_intent_sha256(
            second_payload,
        )
        attempts = []
        workflow_ids = []
        for label, payload in (
            ("partial-a", first_payload), ("partial-b", second_payload),
        ):
            workflow_id, message, fence = self._prepare_intake_message(label)
            workflow_ids.append(workflow_id)
            attempts.append((
                workflow_id,
                message["request_id"],
                intake_replay_receipt(workflow_id, message["request_id"], payload),
                fence,
            ))
        results = self._record_intake_concurrently(attempts)
        self.assertEqual(["CONFLICT", "CREATED"], sorted(item[0] for item in results))
        with psycopg.connect(self.runtime_dsn) as connection:
            self._assert_one_atomic_replay_winner(
                connection, workflow_ids,
                {
                    first_payload["upgrade_intent_sha256"],
                    second_payload["upgrade_intent_sha256"],
                },
                expected_conflicts=1,
            )
            self.assertEqual(
                [("REQUESTER_AUTH_NONCE",)],
                connection.execute(
                    "SELECT claim_kind FROM %s.intake_replay_conflict"
                    % self.schema
                ).fetchall(),
            )

    def _assert_one_atomic_replay_winner(
        self, connection, workflow_ids, candidate_digests, *, expected_conflicts,
    ):
        bindings = connection.execute(
            "SELECT claim_kind, upgrade_intent_sha256 "
            "FROM %s.intake_replay_binding ORDER BY claim_kind" % self.schema
        ).fetchall()
        self.assertEqual(4, len(bindings))
        bound_digests = {str(row[1]) for row in bindings}
        self.assertEqual(1, len(bound_digests))
        self.assertTrue(bound_digests.issubset(candidate_digests))
        self.assertEqual(
            1,
            int(connection.execute(
                "SELECT count(*) FROM %s.module_receipt "
                "WHERE workflow_id IN (%%s, %%s)" % self.schema,
                tuple(workflow_ids),
            ).fetchone()[0]),
        )
        self.assertEqual(
            1,
            int(connection.execute(
                "SELECT count(*) FROM %s.outbox_delivery_event "
                "WHERE workflow_id IN (%%s, %%s) AND status='ACKNOWLEDGED'"
                % self.schema,
                tuple(workflow_ids),
            ).fetchone()[0]),
        )
        self.assertEqual(
            expected_conflicts,
            int(connection.execute(
                "SELECT count(*) FROM %s.intake_replay_conflict" % self.schema
            ).fetchone()[0]),
        )

    def test_intake_replay_guard_is_cross_workflow_atomic_and_append_only(self):
        baseline = intake_replay_payload("baseline")
        workflow_id, message, fence = self._prepare_intake_message("baseline")
        receipt = intake_replay_receipt(
            workflow_id, message["request_id"], baseline,
        )
        self.assertTrue(self.repository.record_receipt(
            workflow_id, message["request_id"], receipt, fence, utc_now(),
        ))
        self.assertFalse(self.repository.record_receipt(
            workflow_id, message["request_id"], receipt, fence, utc_now(),
        ))

        replay_workflow, replay_message, replay_fence = self._prepare_intake_message("exact")
        exact_replay = intake_replay_receipt(
            replay_workflow, replay_message["request_id"], baseline,
        )
        self.assertTrue(self.repository.record_receipt(
            replay_workflow, replay_message["request_id"],
            exact_replay, replay_fence, utc_now(),
        ))
        with psycopg.connect(self.runtime_dsn) as connection:
            self.assertEqual(
                4,
                int(connection.execute(
                    "SELECT count(*) FROM %s.intake_replay_binding" % self.schema
                ).fetchone()[0]),
            )

        field_by_kind = {
            "INTENT_ID": ("intent_id",),
            "IDEMPOTENCY_KEY": ("idempotency_key",),
            "REQUESTER_AUTH_NONCE": ("requester_auth_nonce",),
            "APPROVAL_NONCE": ("authorization", "approval_nonce"),
        }
        for kind, path in field_by_kind.items():
            candidate = intake_replay_payload("candidate-" + kind.lower())
            if len(path) == 1:
                candidate[path[0]] = baseline[path[0]]
            else:
                candidate[path[0]][path[1]] = baseline[path[0]][path[1]]
            candidate["upgrade_intent_sha256"] = intake_upgrade_intent_sha256(candidate)
            conflict_workflow, conflict_message, conflict_fence = (
                self._prepare_intake_message(kind.lower())
            )
            conflicting_receipt = intake_replay_receipt(
                conflict_workflow, conflict_message["request_id"], candidate,
            )
            with self.subTest(kind=kind), self.assertRaisesRegex(
                IdempotencyConflict, "different full intent digest",
            ):
                self.repository.record_receipt(
                    conflict_workflow, conflict_message["request_id"],
                    conflicting_receipt, conflict_fence, utc_now(),
                )
            with psycopg.connect(self.runtime_dsn) as connection:
                self.assertEqual(
                    4,
                    int(connection.execute(
                        "SELECT count(*) FROM %s.intake_replay_binding" % self.schema
                    ).fetchone()[0]),
                    "a conflict must not poison any unbound claim",
                )
                self.assertEqual(
                    [(kind,)],
                    connection.execute(
                        "SELECT claim_kind FROM %s.intake_replay_conflict "
                        "WHERE workflow_id=%%s ORDER BY conflict_sequence" % self.schema,
                        (conflict_workflow,),
                    ).fetchall(),
                )
                self.assertEqual(
                    0,
                    int(connection.execute(
                        "SELECT count(*) FROM %s.module_receipt "
                        "WHERE workflow_id=%%s AND request_id=%%s" % self.schema,
                        (conflict_workflow, conflict_message["request_id"]),
                    ).fetchone()[0]),
                )
                self.assertEqual(
                    0,
                    int(connection.execute(
                        "SELECT count(*) FROM %s.outbox_delivery_event "
                        "WHERE workflow_id=%%s AND request_id=%%s AND status='ACKNOWLEDGED'"
                        % self.schema,
                        (conflict_workflow, conflict_message["request_id"]),
                    ).fetchone()[0]),
                )
            self.assertEqual(
                "INTAKE_REPLAY_CONFLICT",
                self.repository.quarantine_records(conflict_workflow)[-1]["reason"],
            )

        for table in (
            "schema_migration", "intake_replay_binding", "intake_replay_conflict",
            "native_stop_authority_trust_binding",
        ):
            attacks = (
                "UPDATE %s.%s SET occurred_at=clock_timestamp()" % (self.schema, table),
                "DELETE FROM %s.%s" % (self.schema, table),
                "TRUNCATE TABLE %s.%s" % (self.schema, table),
                "ALTER TABLE %s.%s ADD COLUMN forbidden integer" % (self.schema, table),
                "ALTER TABLE %s.%s DISABLE TRIGGER ALL" % (self.schema, table),
                "DROP TABLE %s.%s CASCADE" % (self.schema, table),
            )
            for statement in attacks:
                with self.subTest(table=table, statement=statement), self.assertRaises(
                    psycopg.Error,
                ):
                    with psycopg.connect(self.runtime_dsn) as connection:
                        connection.execute(statement)

    def test_native_stop_trust_global_binding_has_one_atomic_race_winner(self):
        adapter = DeterministicSimulationAdapter()
        service = host(self.repository, adapter)
        workflow_ids = [
            "upgrade:factory-host-native-stop-race-a-" + secrets.token_hex(3),
            "upgrade:factory-host-native-stop-race-b-" + secrets.token_hex(3),
        ]
        fences = []
        for index, workflow_id in enumerate(workflow_ids):
            service.start(request(workflow_id))
            fences.append(self.repository.acquire_fence(
                workflow_id, "native-stop-race-%d" % index, utc_now(),
            ))
        receipt_id = "native-stop-trust-" + secrets.token_hex(16)
        facts = [
            native_stop_trust_fact(receipt_id, "race-a"),
            native_stop_trust_fact(receipt_id, "race-b"),
        ]
        self.assertNotEqual(facts[0]["receipt_sha256"], facts[1]["receipt_sha256"])

        def register(index):
            try:
                return self.repository.register_native_stop_authority_trust(
                    workflow_ids[index], facts[index], fences[index], utc_now(),
                )
            except IdempotencyConflict:
                return "CONFLICT"

        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(register, (0, 1)))
        self.assertEqual(1, results.count(True))
        self.assertEqual(1, results.count("CONFLICT"))
        winner = results.index(True)
        loser = 1 - winner

        restarted = PostgresWorkflowRepository(self.runtime_dsn, schema=self.schema)
        self.assertEqual(
            facts[winner], restarted.native_stop_authority_trust(receipt_id),
        )
        self.assertFalse(restarted.register_native_stop_authority_trust(
            workflow_ids[loser], facts[winner], fences[loser], utc_now(),
        ))
        self.assertEqual(
            "NATIVE_STOP_TRUST_RECEIPT_HASH_CONFLICT",
            restarted.quarantine_records(workflow_ids[loser])[-1]["reason"],
        )
        with psycopg.connect(self.runtime_dsn) as connection:
            self.assertEqual(
                1,
                int(connection.execute(
                    "SELECT count(*) FROM %s.native_stop_authority_trust_binding "
                    "WHERE receipt_id=%%s" % self.schema,
                    (receipt_id,),
                ).fetchone()[0]),
            )


if __name__ == "__main__":
    unittest.main()
