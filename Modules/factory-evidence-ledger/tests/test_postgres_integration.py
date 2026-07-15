import base64
import hashlib
import hmac
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import time
import unittest
import uuid
from pathlib import Path

try:
    import psycopg
except ImportError as exc:  # pragma: no cover - required infrastructure gate
    psycopg = None
    PSYCOPG_IMPORT_ERROR = exc
else:
    PSYCOPG_IMPORT_ERROR = None


MODULE_ROOT = Path(__file__).resolve(strict=True).parents[1]
SOURCE_ROOT = MODULE_ROOT / "src"
SOURCE_PATH = SOURCE_ROOT / "evidence_ledger.py"
SUBJECT_NAME = "_dps_factory_evidence_ledger_postgres_subject"


def load_subject():
    if SOURCE_ROOT.is_symlink() or SOURCE_PATH.is_symlink():
        raise ImportError("PostgreSQL integration subject path must not contain a symbolic link")
    source_root = SOURCE_ROOT.resolve(strict=True)
    source_path = SOURCE_PATH.resolve(strict=True)
    if source_root.parent != MODULE_ROOT or source_path.parent != source_root:
        raise ImportError("PostgreSQL integration subject escaped the module-owned src directory")
    existing = sys.modules.get(SUBJECT_NAME)
    if existing is not None:
        if Path(getattr(existing, "__file__", "")).resolve(strict=True) != source_path:
            raise ImportError("PostgreSQL integration subject module name is already bound elsewhere")
        return existing
    spec = importlib.util.spec_from_file_location(SUBJECT_NAME, source_path)
    if spec is None or spec.loader is None:
        raise ImportError("unable to create the PostgreSQL integration subject module spec")
    subject = importlib.util.module_from_spec(spec)
    sys.modules[SUBJECT_NAME] = subject
    try:
        spec.loader.exec_module(subject)
    except BaseException:
        sys.modules.pop(SUBJECT_NAME, None)
        raise
    return subject


SUBJECT = load_subject()
EvidenceLedger = SUBJECT.EvidenceLedger
ExternalAppendAuthority = SUBJECT.ExternalAppendAuthority
IdempotencyConflict = SUBJECT.IdempotencyConflict
PostgresEvidenceRepository = SUBJECT.PostgresEvidenceRepository
canonical_bytes = SUBJECT.canonical_bytes
sha256 = SUBJECT.sha256

MIGRATION_URI = os.environ.get("DPS_TEST_POSTGRES_MIGRATION_URI")
ADMIN_URI = os.environ.get("DPS_TEST_POSTGRES_ADMIN_URI")
RUNTIME_URI = os.environ.get("DPS_TEST_POSTGRES_RUNTIME_URI")
PSQL = os.environ.get("DPS_PSQL") or shutil.which("psql")
AUTH_KEY_B64 = os.environ.get("DPS_FACTORY_EVIDENCE_APPEND_HMAC_KEY_B64")
AUTH_EPOCH = os.environ.get("DPS_FACTORY_EVIDENCE_APPEND_REVOCATION_EPOCH")


def command(stream_id, payload, expected_sequence, key):
    return {
        "schema_version": "1.0.0",
        "contract_id": "upgrade.event.append/v1",
        "producer_module": "factory-release-controller",
        "soul_id": None,
        "device_binding_id": None,
        "platform_account_id": None,
        "trace_id": "trace_" + "1" * 32,
        "idempotency_key": "idem_" + sha256({"fixture_key": key}),
        "occurred_at": "2026-07-14T00:00:00Z",
        "privacy_class": "internal",
        "stream_id": stream_id,
        "expected_sequence": expected_sequence,
        "event_type": "STATE_TRANSITIONED",
        "payload": payload,
        "payload_sha256": sha256(payload),
    }


def authorization(raw, key, epoch):
    now = int(time.time())
    producer = json.loads(raw)["producer_module"]
    unsigned = {
        "schema_version": "dps.factory-evidence-append-auth/v1",
        "issuer": "dps-factory-auth-service",
        "audience": "factory-evidence-ledger",
        "scope": "factory:evidence:append",
        "producer_module": producer,
        "command_sha256": hashlib.sha256(raw).hexdigest(),
        "issued_at": now,
        "expires_at": now + 60,
        "revocation_epoch": epoch,
        "nonce": "auth_" + uuid.uuid4().hex,
        "key_id": "factory-evidence-append-v1",
    }
    order = (
        "schema_version", "issuer", "audience", "scope", "producer_module",
        "command_sha256", "issued_at", "expires_at", "revocation_epoch", "nonce", "key_id",
    )
    material = "|".join(str(unsigned[field]) for field in order).encode("utf-8")
    value = dict(unsigned)
    value["signature"] = hmac.new(key, material, hashlib.sha256).hexdigest()
    return canonical_bytes(value)


class PostgreSQL18IntegrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        missing = []
        for name, value in (
            ("DPS_TEST_POSTGRES_MIGRATION_URI", MIGRATION_URI),
            ("DPS_TEST_POSTGRES_ADMIN_URI", ADMIN_URI),
            ("DPS_TEST_POSTGRES_RUNTIME_URI", RUNTIME_URI),
            ("DPS_FACTORY_EVIDENCE_APPEND_HMAC_KEY_B64", AUTH_KEY_B64),
            ("DPS_FACTORY_EVIDENCE_APPEND_REVOCATION_EPOCH", AUTH_EPOCH),
        ):
            if not value:
                missing.append(name)
        if not PSQL:
            missing.append("DPS_PSQL or psql on PATH")
        if psycopg is None:
            missing.append(f"locked psycopg driver ({PSYCOPG_IMPORT_ERROR})")
        try:
            cls.auth_key = base64.b64decode(AUTH_KEY_B64 or "", validate=True)
            cls.auth_epoch = int(AUTH_EPOCH or "-1", 10)
            if len(cls.auth_key) < 32 or cls.auth_epoch < 0:
                raise ValueError
        except (ValueError, TypeError):
            missing.append("valid external append key and revocation epoch")
        if missing:
            raise RuntimeError(
                "INFRA_ERROR: required PostgreSQL 18 and external-auth inputs missing: " + ", ".join(missing)
            )
        cls._apply_migrations()
        cls._install_auth_key()
        cls.authority = ExternalAppendAuthority.from_environment()

    @classmethod
    def _psql(cls, uri, *arguments, expect_success=True):
        result = subprocess.run(
            [PSQL, "-X", "--no-psqlrc", "-v", "ON_ERROR_STOP=1", "-d", uri, *arguments],
            check=False,
            shell=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=30,
            env={"PATH": str(Path(PSQL).parent), "LC_ALL": "C", "LANG": "C"},
        )
        if expect_success and result.returncode != 0:
            raise AssertionError(f"INFRA_ERROR: psql failed: {result.stderr}")
        if not expect_success and result.returncode == 0:
            raise AssertionError("expected PostgreSQL role/append-only guard to reject operation")
        return result

    @classmethod
    def _apply_migrations(cls):
        version = cls._psql(MIGRATION_URI, "-Atc", "SHOW server_version_num").stdout.strip()
        if int(version) < 180000:
            raise RuntimeError("INFRA_ERROR: PostgreSQL 18 is required")
        first = MODULE_ROOT / "migrations" / "001_upgrade_event_ledger.sql"
        second = MODULE_ROOT / "migrations" / "002_authenticated_append_acl.sql"
        for _ in range(2):
            cls._psql(MIGRATION_URI, "-f", str(first))
            cls._psql(MIGRATION_URI, "-f", str(second))

    @classmethod
    def _install_auth_key(cls):
        with psycopg.connect(ADMIN_URI) as connection:
            with connection.cursor() as cursor:
                cursor.execute("SELECT current_user, session_user")
                if cursor.fetchone() != ("dps_factory_evidence_admin", "dps_factory_evidence_admin"):
                    raise RuntimeError("INFRA_ERROR: admin URI is not the exact admin identity")
                cursor.execute(
                    "SELECT factory_evidence.install_append_auth_key(%s, %s)",
                    (cls.auth_key, cls.auth_epoch),
                )

    def repository(self):
        return PostgresEvidenceRepository.production(RUNTIME_URI, self.authority)

    def append(self, ledger, value):
        raw = canonical_bytes(value)
        capability = self.authority.verify_and_bind(raw, authorization(raw, self.auth_key, self.auth_epoch))
        return ledger.append(capability)

    def test_real_protected_append_replay_conflict_and_hash_chain(self):
        stream_id = f"integration-{uuid.uuid4().hex}"
        first_payload = {"from_state": "REQUESTED", "to_state": "SCOPE_RESOLVED"}
        second_payload = {"from_state": "SCOPE_RESOLVED", "to_state": "INSTRUCTIONS_BOUND"}
        first_process = EvidenceLedger(self.repository(), self.authority)
        first = self.append(first_process, command(stream_id, first_payload, 0, "append-001"))
        replay = self.append(first_process, command(stream_id, first_payload, 0, "append-001"))
        self.assertEqual("APPENDED", first["append_status"])
        self.assertEqual("IDEMPOTENT_REPLAY", replay["append_status"])
        with self.assertRaises(IdempotencyConflict):
            self.append(first_process, command(stream_id, {"different": True}, 0, "append-001"))
        quarantine = first_process.read_quarantine(stream_id)
        self.assertEqual(1, len(quarantine))
        self.assertNotIn("payload", quarantine[0])
        second_process = EvidenceLedger(self.repository(), self.authority)
        second = self.append(second_process, command(stream_id, second_payload, 1, "append-002"))
        self.assertEqual(first["event_sha256"], second["previous_event_sha256"])
        recovered = EvidenceLedger(self.repository(), self.authority).read_stream(stream_id)
        self.assertEqual(2, len(recovered))

    def test_runtime_and_admin_have_no_direct_table_writes_or_reads(self):
        attacks = (
            (RUNTIME_URI, "SELECT count(*) FROM factory_evidence.upgrade_event"),
            (RUNTIME_URI, "TRUNCATE factory_evidence.upgrade_event"),
            (RUNTIME_URI, "DELETE FROM factory_evidence.upgrade_stream"),
            (ADMIN_URI, "UPDATE factory_evidence.upgrade_stream SET last_sequence=0"),
            (ADMIN_URI, "TRUNCATE factory_evidence.upgrade_event_quarantine"),
            (ADMIN_URI, "SELECT factory_evidence.append_upgrade_event('x'::bytea, '{}'::jsonb, '{}'::jsonb)"),
            (RUNTIME_URI, "SELECT factory_evidence.install_append_auth_key(decode(repeat('00',32),'hex'), 999999)"),
        )
        for uri, sql in attacks:
            with self.subTest(sql=sql):
                self._psql(uri, "-c", sql, expect_success=False)

    def test_database_rejects_json_string_for_numeric_sequence_even_with_valid_hmac(self):
        seed_stream = f"type-seed-{uuid.uuid4().hex}"
        seed_ledger = EvidenceLedger(self.repository(), self.authority)
        valid_event = self.append(
            seed_ledger,
            command(
                seed_stream,
                {"from_state": "REQUESTED", "to_state": "SCOPE_RESOLVED"},
                0,
                "type-seed-001",
            ),
        )
        attacked_stream = f"type-attack-{uuid.uuid4().hex}"
        attacked = command(
            attacked_stream,
            {"from_state": "REQUESTED", "to_state": "SCOPE_RESOLVED"},
            0,
            "type-attack-001",
        )
        attacked["expected_sequence"] = "0"
        raw = canonical_bytes(attacked)
        auth = authorization(raw, self.auth_key, self.auth_epoch).decode("utf-8")
        connection = psycopg.connect(RUNTIME_URI)
        cursor = connection.cursor()
        try:
            with self.assertRaises(psycopg.Error):
                cursor.execute(
                    "SELECT append_status, event_json "
                    "FROM factory_evidence.append_upgrade_event(%s, %s::jsonb, %s::jsonb)",
                    (raw, json.dumps(valid_event), auth),
                )
            connection.rollback()
        finally:
            cursor.close()
            connection.close()
        self.assertEqual([], EvidenceLedger(self.repository(), self.authority).read_stream(attacked_stream))

    def test_migration_identity_cannot_silently_mutate_or_truncate_ledger_data(self):
        stream_id = f"migration-guard-{uuid.uuid4().hex}"
        ledger = EvidenceLedger(self.repository(), self.authority)
        self.append(
            ledger,
            command(
                stream_id,
                {"from_state": "REQUESTED", "to_state": "SCOPE_RESOLVED"},
                0,
                "migration-guard-001",
            ),
        )
        attacks = (
            f"UPDATE factory_evidence.upgrade_event SET event_json = '{{}}'::jsonb WHERE stream_id = '{stream_id}'",
            f"DELETE FROM factory_evidence.upgrade_event WHERE stream_id = '{stream_id}'",
            f"UPDATE factory_evidence.upgrade_stream SET last_sequence = 0 WHERE stream_id = '{stream_id}'",
            "TRUNCATE factory_evidence.upgrade_event",
            "TRUNCATE factory_evidence.upgrade_stream",
            "TRUNCATE factory_evidence.append_auth_key_history",
        )
        for sql in attacks:
            with self.subTest(sql=sql):
                self._psql(MIGRATION_URI, "-c", sql, expect_success=False)
        recovered = EvidenceLedger(self.repository(), self.authority).read_stream(stream_id)
        self.assertEqual(1, len(recovered))

    def test_role_and_function_acl_are_exact(self):
        rows = self._psql(
            MIGRATION_URI,
            "-Atc",
            "SELECT rolname || ':' || rolcanlogin || ':' || rolinherit || ':' || rolsuper || ':' || rolcreaterole || ':' || rolcreatedb || ':' || rolreplication || ':' || rolbypassrls "
            "FROM pg_roles WHERE rolname LIKE 'dps_factory_evidence_%' ORDER BY rolname",
        ).stdout.splitlines()
        self.assertEqual(
            [
                "dps_factory_evidence_admin:t:f:f:f:f:f:f",
                "dps_factory_evidence_owner:f:f:f:f:f:f:f",
                "dps_factory_evidence_runtime:t:f:f:f:f:f:f",
            ],
            rows,
        )
        memberships = self._psql(
            MIGRATION_URI,
            "-Atc",
            "SELECT granted_role.rolname || '->' || member_role.rolname "
            "FROM pg_auth_members membership "
            "JOIN pg_roles granted_role ON granted_role.oid = membership.roleid "
            "JOIN pg_roles member_role ON member_role.oid = membership.member "
            "WHERE granted_role.rolname LIKE 'dps_factory_evidence_%' "
            "OR member_role.rolname LIKE 'dps_factory_evidence_%'",
        ).stdout.splitlines()
        self.assertEqual([], memberships)


if __name__ == "__main__":
    unittest.main()
