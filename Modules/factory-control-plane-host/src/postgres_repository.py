"""PostgreSQL 18.4 append-only repository for Factory orchestration truth."""

from __future__ import annotations

import copy
import json
import os
import re
import stat
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping, Sequence

from factory_control_plane_host import (
    ZERO_HASH, CorruptWorkflow, FactoryHostError, IdempotencyConflict,
    IllegalTransition, StaleFence, canonical_bytes, opaque_idempotency, sha256,
    validate_event_stream, validate_native_stop_trust_durable_fact,
)


_MIGRATION_FILENAME = re.compile(r"^(?P<version>[0-9]{3})_(?P<slug>[a-z0-9]+(?:_[a-z0-9]+)*)\.sql$")
_SHA256 = re.compile(r"^[a-f0-9]{64}$")
_INTENT_ID = re.compile(r"^[a-z0-9][a-z0-9._:-]{7,127}$")
_IDEMPOTENCY_KEY = re.compile(r"^idem_[a-f0-9]{64}$")
_NONCE = re.compile(r"^nonce_[a-f0-9]{32}$")
_INTAKE_CLAIM_DOMAINS = {
    "INTENT_ID": "dps.factory-control-plane-host/intake-replay/v1/intent-id",
    "IDEMPOTENCY_KEY": "dps.factory-control-plane-host/intake-replay/v1/idempotency-key",
    "REQUESTER_AUTH_NONCE": "dps.factory-control-plane-host/intake-replay/v1/requester-auth-nonce",
    "APPROVAL_NONCE": "dps.factory-control-plane-host/intake-replay/v1/approval-nonce",
}


@dataclass(frozen=True)
class MigrationFile:
    version: int
    name: str
    path: Path
    sha256: str
    sql: str


@dataclass(frozen=True)
class IntakeReplayClaim:
    kind: str
    key_sha256: str


@dataclass(frozen=True)
class IntakeReplayGuard:
    upgrade_intent_sha256: str
    claims: tuple[IntakeReplayClaim, ...]


def _domain_sha256(domain: str, value: Any) -> str:
    return sha256(b"DPS\x00" + domain.encode("ascii") + b"\x00" + canonical_bytes(value))


def intake_upgrade_intent_sha256(intent: Mapping[str, Any]) -> str:
    return _domain_sha256(
        "dps.upgrade-intent/v2/full-intent",
        {key: value for key, value in intent.items() if key != "upgrade_intent_sha256"},
    )


def intake_replay_claim_key_sha256(kind: str, value: str) -> str:
    domain = _INTAKE_CLAIM_DOMAINS.get(kind)
    if domain is None:
        raise FactoryHostError("unknown intake replay claim kind")
    return _domain_sha256(domain, {"value": value})


def _required_replay_value(payload: Mapping[str, Any], field: str, pattern: re.Pattern[str]) -> str:
    value = payload.get(field)
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise FactoryHostError("upgrade.intent/v2 replay field is invalid: " + field)
    return value


def intake_replay_guard_from_receipt(receipt: Mapping[str, Any]) -> IntakeReplayGuard | None:
    """Extract a future v2 Intake replay index without changing the active v1 wire boundary."""

    outputs = receipt.get("outputs")
    if not isinstance(outputs, Sequence) or isinstance(outputs, (str, bytes, bytearray)):
        raise FactoryHostError("module receipt outputs are invalid")
    matching = [
        item for item in outputs
        if isinstance(item, Mapping) and item.get("contract_id") == "upgrade.intent/v2"
    ]
    if not matching:
        return None
    if len(matching) != 1:
        raise FactoryHostError("module receipt contains multiple upgrade.intent/v2 outputs")
    if (
        receipt.get("target_module") != "factory-upgrade-intake"
        or receipt.get("operation") != "validate-intent"
        or matching[0].get("producer_module") != "factory-upgrade-intake"
    ):
        raise FactoryHostError("upgrade.intent/v2 replay output is outside the Intake boundary")
    payload = matching[0].get("payload")
    if not isinstance(payload, Mapping):
        raise FactoryHostError("upgrade.intent/v2 replay payload is invalid")
    if (
        payload.get("schema_version") != "dps.upgrade-intent/v2"
        or payload.get("contract_id") != "upgrade.intent/v2"
        or payload.get("producer_module") != "factory-upgrade-intake"
        or matching[0].get("payload_sha256") != sha256(dict(payload))
    ):
        raise FactoryHostError("upgrade.intent/v2 replay identity or payload digest is invalid")

    intent_id = _required_replay_value(payload, "intent_id", _INTENT_ID)
    idempotency_key = _required_replay_value(payload, "idempotency_key", _IDEMPOTENCY_KEY)
    requester_nonce = _required_replay_value(payload, "requester_auth_nonce", _NONCE)
    full_digest = _required_replay_value(payload, "upgrade_intent_sha256", _SHA256)
    if full_digest != intake_upgrade_intent_sha256(payload):
        raise FactoryHostError("upgrade.intent/v2 full intent digest is invalid")
    authorization = payload.get("authorization")
    if not isinstance(authorization, Mapping) or "approval_nonce" not in authorization:
        raise FactoryHostError("upgrade.intent/v2 authorization replay field is invalid")
    approval_nonce = authorization.get("approval_nonce")
    if approval_nonce is not None and (
        not isinstance(approval_nonce, str) or _NONCE.fullmatch(approval_nonce) is None
    ):
        raise FactoryHostError("upgrade.intent/v2 approval nonce is invalid")

    values = [
        ("INTENT_ID", intent_id),
        ("IDEMPOTENCY_KEY", idempotency_key),
        ("REQUESTER_AUTH_NONCE", requester_nonce),
    ]
    if approval_nonce is not None:
        values.append(("APPROVAL_NONCE", approval_nonce))
    return IntakeReplayGuard(
        upgrade_intent_sha256=full_digest,
        claims=tuple(
            IntakeReplayClaim(kind, intake_replay_claim_key_sha256(kind, value))
            for kind, value in values
        ),
    )


def discover_migrations(directory: Path) -> tuple[MigrationFile, ...]:
    root = Path(directory)
    directory_flags = (
        os.O_RDONLY
        | getattr(os, "O_CLOEXEC", 0)
        | getattr(os, "O_DIRECTORY", 0)
        | getattr(os, "O_NOFOLLOW", 0)
    )
    try:
        directory_descriptor = os.open(root, directory_flags)
    except (NotImplementedError, OSError) as exc:
        raise FactoryHostError("migration directory cannot be opened safely") from exc
    migrations: list[MigrationFile] = []
    versions: set[int] = set()
    try:
        if not stat.S_ISDIR(os.fstat(directory_descriptor).st_mode):
            raise FactoryHostError("migration directory source is not a directory")
        try:
            names = sorted(os.listdir(directory_descriptor))
        except (NotImplementedError, OSError) as exc:
            raise FactoryHostError("migration directory cannot be listed safely") from exc
        for name in names:
            if not name.endswith(".sql"):
                continue
            match = _MIGRATION_FILENAME.fullmatch(name)
            if match is None:
                raise FactoryHostError("migration filename is invalid: " + name)
            version = int(match.group("version"))
            if version <= 0 or version in versions:
                raise FactoryHostError(
                    "migration version is duplicate or invalid: " + str(version)
                )
            try:
                path_metadata = os.stat(
                    name, dir_fd=directory_descriptor, follow_symlinks=False,
                )
            except (NotImplementedError, OSError) as exc:
                raise FactoryHostError(
                    "migration file cannot be inspected safely: " + name
                ) from exc
            if stat.S_ISLNK(path_metadata.st_mode):
                raise FactoryHostError("migration file must not be a symlink: " + name)
            if not stat.S_ISREG(path_metadata.st_mode):
                raise FactoryHostError("migration source is not a regular file: " + name)
            flags = (
                os.O_RDONLY
                | getattr(os, "O_CLOEXEC", 0)
                | getattr(os, "O_NOFOLLOW", 0)
            )
            try:
                descriptor = os.open(name, flags, dir_fd=directory_descriptor)
            except (NotImplementedError, OSError) as exc:
                raise FactoryHostError(
                    "migration file cannot be opened safely: " + name
                ) from exc
            try:
                metadata = os.fstat(descriptor)
                if not stat.S_ISREG(metadata.st_mode):
                    raise FactoryHostError(
                        "migration source is not a regular file: " + name
                    )
                chunks = []
                while True:
                    chunk = os.read(descriptor, 1024 * 1024)
                    if not chunk:
                        break
                    chunks.append(chunk)
                raw = b"".join(chunks)
            finally:
                os.close(descriptor)
            try:
                sql = raw.decode("utf-8")
            except UnicodeDecodeError as exc:
                raise FactoryHostError(
                    "migration source is not UTF-8: " + name
                ) from exc
            migrations.append(
                MigrationFile(version, name, root / name, sha256(raw), sql)
            )
            versions.add(version)
    finally:
        os.close(directory_descriptor)
    if not migrations:
        raise FactoryHostError("no migration files were discovered")
    ordered = sorted(migrations, key=lambda item: item.version)
    expected = list(range(1, len(ordered) + 1))
    actual = [item.version for item in ordered]
    if actual != expected:
        raise FactoryHostError("migration versions must be contiguous from 001")
    return tuple(ordered)


def verify_migration_history(
    migrations: Sequence[MigrationFile], rows: Sequence[Sequence[Any]],
) -> int:
    seen_versions: set[int] = set()
    seen_names: set[str] = set()
    normalized: list[tuple[int, str, str]] = []
    for row in rows:
        if len(row) < 3:
            raise FactoryHostError("schema migration history row is malformed")
        version, name, digest = int(row[0]), str(row[1]), str(row[2])
        if version in seen_versions or name in seen_names:
            raise FactoryHostError("schema migration history contains duplicates")
        seen_versions.add(version)
        seen_names.add(name)
        normalized.append((version, name, digest))
    if [item[0] for item in normalized] != list(range(1, len(normalized) + 1)):
        raise FactoryHostError("schema migration history contains a gap or reordering")
    if len(normalized) > len(migrations):
        raise FactoryHostError("schema migration history contains an unknown future migration")
    for version, name, digest in normalized:
        source = migrations[version - 1]
        if source.version != version or source.name != name or source.sha256 != digest:
            raise FactoryHostError("schema migration history name or hash drift detected at %03d" % version)
    return len(normalized)


class PostgresSchemaMigrator:
    """Admin-only migrator; the runtime repository never receives this DSN."""

    CONNECT_TIMEOUT_SECONDS = 5
    STATEMENT_TIMEOUT_MS = 120_000
    LOCK_TIMEOUT_MS = 5_000
    IDLE_TRANSACTION_TIMEOUT_MS = 120_000
    TCP_USER_TIMEOUT_MS = 10_000

    def __init__(self, admin_dsn: str, runtime_role: str, *, schema: str = "factory_control_plane_host") -> None:
        if not isinstance(admin_dsn, str) or not admin_dsn:
            raise FactoryHostError("PostgreSQL admin DSN is required")
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,62}", runtime_role):
            raise FactoryHostError("PostgreSQL runtime role is invalid")
        if not re.fullmatch(r"[a-z_][a-z0-9_]{0,62}", schema):
            raise FactoryHostError("PostgreSQL schema identifier is invalid")
        self.admin_dsn = admin_dsn
        self.runtime_role = runtime_role
        self.schema = schema

    def _connect(self, psycopg):
        options = (
            "-c statement_timeout=%d -c lock_timeout=%d "
            "-c idle_in_transaction_session_timeout=%d"
        ) % (
            self.STATEMENT_TIMEOUT_MS,
            self.LOCK_TIMEOUT_MS,
            self.IDLE_TRANSACTION_TIMEOUT_MS,
        )
        return psycopg.connect(
            self.admin_dsn,
            autocommit=False,
            connect_timeout=self.CONNECT_TIMEOUT_SECONDS,
            options=options,
            keepalives=1,
            keepalives_idle=2,
            keepalives_interval=1,
            keepalives_count=2,
            tcp_user_timeout=self.TCP_USER_TIMEOUT_MS,
        )

    def _validate_runtime_role(self, connection) -> None:
        role = connection.execute(
            "SELECT oid, rolcanlogin, rolsuper, rolcreaterole, rolcreatedb, rolreplication, "
            "rolbypassrls FROM pg_roles WHERE rolname=%s",
            (self.runtime_role,),
        ).fetchone()
        if role is None:
            raise FactoryHostError("INFRA_ERROR: PostgreSQL runtime role does not exist")
        runtime_oid = int(role[0])
        if not bool(role[1]):
            raise FactoryHostError("INFRA_ERROR: PostgreSQL runtime role cannot login")
        if any(bool(flag) for flag in role[2:]):
            raise FactoryHostError(
                "INFRA_ERROR: PostgreSQL runtime role has elevated role attributes"
            )
        database_facts = connection.execute(
            "SELECT datdba=%s, has_database_privilege(%s, current_database(), 'CREATE') "
            "FROM pg_database WHERE datname=current_database()",
            (runtime_oid, self.runtime_role),
        ).fetchone()
        if database_facts is None or any(bool(value) for value in database_facts):
            raise FactoryHostError(
                "INFRA_ERROR: PostgreSQL runtime role owns or can create in the database"
            )
        memberships = connection.execute(
            "SELECT r.rolname FROM pg_roles r WHERE r.oid<>%s "
            "AND pg_has_role(%s, r.oid, 'MEMBER') ORDER BY r.rolname",
            (runtime_oid, runtime_oid),
        ).fetchall()
        if memberships:
            raise FactoryHostError(
                "INFRA_ERROR: PostgreSQL runtime role has inherited role memberships"
            )
        owns_schema_objects = bool(connection.execute(
            "SELECT "
            "EXISTS(SELECT 1 FROM pg_namespace n WHERE n.nspname=%s AND n.nspowner=%s) "
            "OR EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
            "WHERE n.nspname=%s AND c.relowner=%s) "
            "OR EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace "
            "WHERE n.nspname=%s AND p.proowner=%s)",
            (
                self.schema, runtime_oid, self.schema, runtime_oid,
                self.schema, runtime_oid,
            ),
        ).fetchone()[0])
        if owns_schema_objects:
            raise FactoryHostError(
                "INFRA_ERROR: PostgreSQL runtime role owns migration schema objects"
            )

    def _set_exact_runtime_privileges(self, connection, psycopg) -> None:
        schema_identifier = psycopg.sql.Identifier(self.schema)
        runtime_identifier = psycopg.sql.Identifier(self.runtime_role)
        public_principal = psycopg.sql.SQL("PUBLIC")
        column_rows = connection.execute(
            "SELECT c.relname, array_agg(a.attname ORDER BY a.attnum) "
            "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
            "JOIN pg_attribute a ON a.attrelid=c.oid "
            "WHERE n.nspname=%s AND c.relkind IN ('r','p') "
            "AND a.attnum>0 AND NOT a.attisdropped GROUP BY c.relname ORDER BY c.relname",
            (self.schema,),
        ).fetchall()
        for target in (public_principal, runtime_identifier):
            connection.execute(
                psycopg.sql.SQL("REVOKE ALL PRIVILEGES ON SCHEMA {} FROM {}").format(
                    schema_identifier, target,
                )
            )
            connection.execute(
                psycopg.sql.SQL(
                    "REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA {} FROM {}"
                ).format(schema_identifier, target)
            )
            connection.execute(
                psycopg.sql.SQL(
                    "REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA {} FROM {}"
                ).format(schema_identifier, target)
            )
            connection.execute(
                psycopg.sql.SQL(
                    "REVOKE ALL PRIVILEGES ON ALL FUNCTIONS IN SCHEMA {} FROM {}"
                ).format(schema_identifier, target)
            )
            for table_name, column_names in column_rows:
                columns = psycopg.sql.SQL(", ").join(
                    psycopg.sql.Identifier(str(name)) for name in column_names
                )
                connection.execute(
                    psycopg.sql.SQL(
                        "REVOKE ALL PRIVILEGES ({}) ON {}.{} FROM {}"
                    ).format(
                        columns,
                        schema_identifier,
                        psycopg.sql.Identifier(str(table_name)),
                        target,
                    )
                )
        connection.execute(
            psycopg.sql.SQL("GRANT USAGE ON SCHEMA {} TO {}").format(
                schema_identifier, runtime_identifier,
            )
        )
        connection.execute(
            psycopg.sql.SQL(
                "GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA {} TO {}"
            ).format(schema_identifier, runtime_identifier)
        )
        connection.execute(
            psycopg.sql.SQL("GRANT USAGE ON ALL SEQUENCES IN SCHEMA {} TO {}").format(
                schema_identifier, runtime_identifier,
            )
        )
        connection.execute(
            psycopg.sql.SQL("REVOKE INSERT ON {}.{} FROM {}").format(
                schema_identifier,
                psycopg.sql.Identifier("schema_migration"),
                runtime_identifier,
            )
        )

    def _verify_exact_runtime_privileges(self, connection) -> None:
        schema_privileges = connection.execute(
            "SELECT has_schema_privilege(%s, %s, 'USAGE'), "
            "has_schema_privilege(%s, %s, 'CREATE')",
            (self.runtime_role, self.schema, self.runtime_role, self.schema),
        ).fetchone()
        if schema_privileges != (True, False):
            raise FactoryHostError(
                "INFRA_ERROR: PostgreSQL runtime schema privileges are not exact"
            )
        table_rows = connection.execute(
            "SELECT c.relname, "
            "has_table_privilege(%s, c.oid, 'SELECT'), "
            "has_table_privilege(%s, c.oid, 'INSERT'), "
            "has_table_privilege(%s, c.oid, 'UPDATE'), "
            "has_table_privilege(%s, c.oid, 'DELETE'), "
            "has_table_privilege(%s, c.oid, 'TRUNCATE'), "
            "has_table_privilege(%s, c.oid, 'REFERENCES'), "
            "has_table_privilege(%s, c.oid, 'TRIGGER'), "
            "has_table_privilege(%s, c.oid, 'MAINTAIN') "
            "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
            "WHERE n.nspname=%s AND c.relkind IN ('r','p') ORDER BY c.relname",
            (
                self.runtime_role, self.runtime_role, self.runtime_role,
                self.runtime_role, self.runtime_role, self.runtime_role,
                self.runtime_role, self.runtime_role, self.schema,
            ),
        ).fetchall()
        if not table_rows:
            raise FactoryHostError("INFRA_ERROR: PostgreSQL migration created no tables")
        for row in table_rows:
            expected_insert = row[0] != "schema_migration"
            if tuple(bool(value) for value in row[1:]) != (
                True, expected_insert, False, False, False, False, False, False,
            ):
                raise FactoryHostError(
                    "INFRA_ERROR: PostgreSQL runtime table privileges are not exact: "
                    + str(row[0])
                )
        column_rows = connection.execute(
            "SELECT c.relname, a.attname, "
            "has_column_privilege(%s, c.oid, a.attnum, 'SELECT'), "
            "has_column_privilege(%s, c.oid, a.attnum, 'INSERT'), "
            "has_column_privilege(%s, c.oid, a.attnum, 'UPDATE'), "
            "has_column_privilege(%s, c.oid, a.attnum, 'REFERENCES') "
            "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
            "JOIN pg_attribute a ON a.attrelid=c.oid "
            "WHERE n.nspname=%s AND c.relkind IN ('r','p') "
            "AND a.attnum>0 AND NOT a.attisdropped ORDER BY c.relname, a.attnum",
            (
                self.runtime_role, self.runtime_role, self.runtime_role,
                self.runtime_role, self.schema,
            ),
        ).fetchall()
        for row in column_rows:
            expected_insert = row[0] != "schema_migration"
            if tuple(bool(value) for value in row[2:]) != (
                True, expected_insert, False, False,
            ):
                raise FactoryHostError(
                    "INFRA_ERROR: PostgreSQL runtime column privileges are not exact: "
                    + str(row[0]) + "." + str(row[1])
                )
        sequence_rows = connection.execute(
            "SELECT c.relname, "
            "has_sequence_privilege(%s, c.oid, 'USAGE'), "
            "has_sequence_privilege(%s, c.oid, 'SELECT'), "
            "has_sequence_privilege(%s, c.oid, 'UPDATE') "
            "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace "
            "WHERE n.nspname=%s AND c.relkind='S' ORDER BY c.relname",
            (self.runtime_role, self.runtime_role, self.runtime_role, self.schema),
        ).fetchall()
        for row in sequence_rows:
            if tuple(bool(value) for value in row[1:]) != (True, False, False):
                raise FactoryHostError(
                    "INFRA_ERROR: PostgreSQL runtime sequence privileges are not exact: "
                    + str(row[0])
                )
        function_rows = connection.execute(
            "SELECT p.proname, has_function_privilege(%s, p.oid, 'EXECUTE') "
            "FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace "
            "WHERE n.nspname=%s ORDER BY p.proname",
            (self.runtime_role, self.schema),
        ).fetchall()
        if any(bool(row[1]) for row in function_rows):
            raise FactoryHostError(
                "INFRA_ERROR: PostgreSQL runtime can execute migration functions"
            )
        acl_facts = connection.execute(
            "WITH grants AS ("
            "SELECT acl.grantee, acl.is_grantable FROM pg_namespace n "
            "CROSS JOIN LATERAL aclexplode(n.nspacl) acl WHERE n.nspname=%s "
            "UNION ALL "
            "SELECT acl.grantee, acl.is_grantable FROM pg_class c "
            "JOIN pg_namespace n ON n.oid=c.relnamespace "
            "CROSS JOIN LATERAL aclexplode(c.relacl) acl WHERE n.nspname=%s "
            "UNION ALL "
            "SELECT acl.grantee, acl.is_grantable FROM pg_attribute a "
            "JOIN pg_class c ON c.oid=a.attrelid "
            "JOIN pg_namespace n ON n.oid=c.relnamespace "
            "CROSS JOIN LATERAL aclexplode(a.attacl) acl "
            "WHERE n.nspname=%s AND a.attnum>0 AND NOT a.attisdropped "
            "UNION ALL "
            "SELECT acl.grantee, acl.is_grantable FROM pg_proc p "
            "JOIN pg_namespace n ON n.oid=p.pronamespace "
            "CROSS JOIN LATERAL aclexplode(p.proacl) acl WHERE n.nspname=%s"
            ") SELECT "
            "EXISTS(SELECT 1 FROM grants WHERE grants.grantee=0), "
            "EXISTS(SELECT 1 FROM grants WHERE grants.grantee="
            "(SELECT oid FROM pg_roles WHERE rolname=%s) AND grants.is_grantable)",
            (
                self.schema, self.schema, self.schema, self.schema,
                self.runtime_role,
            ),
        ).fetchone()
        if acl_facts is None or bool(acl_facts[0]):
            raise FactoryHostError(
                "INFRA_ERROR: PUBLIC retains a PostgreSQL migration-schema privilege"
            )
        if bool(acl_facts[1]):
            raise FactoryHostError(
                "INFRA_ERROR: PostgreSQL runtime retains a grant option"
            )

    def migrate(self) -> None:
        psycopg = PostgresWorkflowRepository._driver()
        migrations = discover_migrations(Path(__file__).resolve().parents[1] / "migrations")
        with self._connect(psycopg) as connection:
            admin_role = str(connection.execute("SELECT current_user").fetchone()[0])
            if admin_role == self.runtime_role:
                raise FactoryHostError("migration and runtime PostgreSQL roles must be distinct")
            self._validate_runtime_role(connection)
            connection.execute(
                "SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))",
                ("dps-schema-migrator:" + self.schema,),
            )
            schema_exists = bool(connection.execute(
                "SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=%s)",
                (self.schema,),
            ).fetchone()[0])
            history_exists = bool(connection.execute(
                "SELECT EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n "
                "ON n.oid=c.relnamespace WHERE n.nspname=%s AND c.relname='schema_migration' "
                "AND c.relkind='r')",
                (self.schema,),
            ).fetchone()[0])
            if schema_exists and not history_exists:
                raise FactoryHostError(
                    "untracked pre-ledger schema is not eligible for automatic migration"
                )
            if history_exists:
                rows = connection.execute(
                    "SELECT migration_version, migration_name, migration_sha256 FROM %s.schema_migration "
                    "ORDER BY migration_version" % self.schema
                ).fetchall()
                applied = verify_migration_history(migrations, rows)
                for migration in migrations[applied:]:
                    sql = migration.sql.replace("factory_control_plane_host", self.schema)
                    connection.execute(sql)
                    connection.execute(
                        "INSERT INTO %s.schema_migration"
                        "(migration_version, migration_name, migration_sha256, applied_at) "
                        "VALUES (%%s, %%s, %%s, clock_timestamp())" % self.schema,
                        (migration.version, migration.name, migration.sha256),
                    )
            else:
                for migration in migrations:
                    sql = migration.sql.replace("factory_control_plane_host", self.schema)
                    connection.execute(sql)
                for migration in migrations:
                    connection.execute(
                        "INSERT INTO %s.schema_migration"
                        "(migration_version, migration_name, migration_sha256, applied_at) "
                        "VALUES (%%s, %%s, %%s, clock_timestamp())" % self.schema,
                        (migration.version, migration.name, migration.sha256),
                    )
            final_rows = connection.execute(
                "SELECT migration_version, migration_name, migration_sha256 FROM %s.schema_migration "
                "ORDER BY migration_version" % self.schema
            ).fetchall()
            if verify_migration_history(migrations, final_rows) != len(migrations):
                raise FactoryHostError("schema migration history is incomplete")
            self._validate_runtime_role(connection)
            self._set_exact_runtime_privileges(connection, psycopg)
            self._verify_exact_runtime_privileges(connection)
            connection.commit()


class PostgresWorkflowRepository:
    CONNECT_TIMEOUT_SECONDS = 5
    STATEMENT_TIMEOUT_MS = 5_000
    LOCK_TIMEOUT_MS = 5_000
    IDLE_TRANSACTION_TIMEOUT_MS = 5_000

    def __init__(self, dsn: str, *, schema: str = "factory_control_plane_host") -> None:
        if not isinstance(dsn, str) or not dsn:
            raise FactoryHostError("PostgreSQL DSN is required")
        if not re.fullmatch(r"[a-z_][a-z0-9_]{0,62}", schema):
            raise FactoryHostError("PostgreSQL schema identifier is invalid")
        self.dsn = dsn
        self.schema = schema

    @staticmethod
    def _driver():
        try:
            import psycopg
        except ImportError as exc:
            raise FactoryHostError("INFRA_ERROR: hash-locked psycopg 3.3.4 is unavailable") from exc
        return psycopg

    def _connect(self):
        options = (
            "-c statement_timeout=%d -c lock_timeout=%d "
            "-c idle_in_transaction_session_timeout=%d"
        ) % (
            self.STATEMENT_TIMEOUT_MS,
            self.LOCK_TIMEOUT_MS,
            self.IDLE_TRANSACTION_TIMEOUT_MS,
        )
        return self._driver().connect(
            self.dsn,
            connect_timeout=self.CONNECT_TIMEOUT_SECONDS,
            options=options,
            keepalives=1,
            keepalives_idle=2,
            keepalives_interval=1,
            keepalives_count=2,
            tcp_user_timeout=5_000,
        )

    @staticmethod
    def _loads(value: Any) -> Any:
        return json.loads(value) if isinstance(value, str) else value

    @classmethod
    def _trusted_json(cls, raw: Any, digest: Any, *, label: str) -> dict[str, Any]:
        value = cls._loads(raw)
        if not isinstance(value, Mapping) or digest != sha256(value):
            raise CorruptWorkflow(label + " JSON does not match its stored digest")
        return copy.deepcopy(dict(value))

    @classmethod
    def _trusted_request_row(
        cls,
        row: Sequence[Any],
        workflow_id: str,
    ) -> dict[str, Any]:
        value = cls._trusted_json(row[0], row[1], label="workflow request")
        if (
            row[2] != workflow_id
            or value.get("workflow_id") != workflow_id
            or row[3] != value.get("idempotency_key")
        ):
            raise CorruptWorkflow("workflow request identity columns drifted")
        return value

    @classmethod
    def _trusted_binding_row(
        cls,
        row: Sequence[Any],
        workflow_id: str,
    ) -> dict[str, Any]:
        value = cls._trusted_json(row[0], row[1], label="role binding")
        if (
            row[2] != workflow_id
            or value.get("workflow_id") != workflow_id
            or row[3] != value.get("binding_id")
        ):
            raise CorruptWorkflow("role binding identity columns drifted")
        return value

    @classmethod
    def _trusted_message_row(
        cls,
        row: Sequence[Any],
        workflow_id: str,
    ) -> dict[str, Any]:
        value = cls._trusted_json(row[0], row[1], label="outbox message")
        if (
            row[2] != workflow_id
            or value.get("stage_id") != row[4]
            or value.get("request_id") != row[3]
        ):
            raise CorruptWorkflow("outbox message identity columns drifted")
        return value

    @classmethod
    def _trusted_receipt_row(
        cls,
        row: Sequence[Any],
        workflow_id: str,
    ) -> dict[str, Any]:
        value = cls._loads(row[0])
        if not isinstance(value, Mapping):
            raise CorruptWorkflow("module receipt JSON is malformed")
        stored = copy.deepcopy(dict(value))
        receipt_id = stored.pop("receipt_id", None)
        if (
            row[1] != sha256(stored)
            or row[2] != workflow_id
            or stored.get("workflow_id") != workflow_id
            or stored.get("request_id") != row[3]
            or receipt_id != row[4]
            or receipt_id != "module-receipt:" + str(row[1])[:32]
        ):
            raise CorruptWorkflow("module receipt identity or digest columns drifted")
        return copy.deepcopy(dict(value))

    @classmethod
    def _trusted_native_stop_trust_row(
        cls,
        row: Sequence[Any],
    ) -> dict[str, Any]:
        if len(row) != 11:
            raise CorruptWorkflow("native-stop trust index row shape drifted")
        value = cls._trusted_json(
            row[0], row[1], label="native-stop authority trust binding",
        )
        value = validate_native_stop_trust_durable_fact(value)
        expected_columns = (
            value["receipt_id"],
            value["receipt_sha256"],
            value["release_bom_id"],
            value["release_bom_sha256"],
            value["integration_commit"],
            value["release_bom_generation"],
            value["activation_token_sha256"],
            value["authority_sets_sha256"],
        )
        try:
            stored_generation = int(row[7])
        except (TypeError, ValueError) as exc:
            raise CorruptWorkflow(
                "native-stop trust index generation column drifted",
            ) from exc
        actual_columns = (
            row[2], row[3], row[4], row[5], row[6], stored_generation, row[8], row[9],
        )
        first_workflow_id = row[10]
        if actual_columns != expected_columns or (
            not isinstance(first_workflow_id, str)
            or re.fullmatch(
                r"upgrade:[A-Za-z0-9][A-Za-z0-9._-]{7,119}",
                first_workflow_id,
            ) is None
        ):
            raise CorruptWorkflow("native-stop trust index columns drifted")
        return value

    @staticmethod
    def _lock(cursor, workflow_id: str) -> None:
        cursor.execute("SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))", (workflow_id,))

    @staticmethod
    def _lock_intake_claims(cursor, claims: Sequence[IntakeReplayClaim]) -> None:
        for claim in sorted(claims, key=lambda item: (item.kind, item.key_sha256)):
            cursor.execute(
                "SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))",
                (
                    "dps-intake-replay:%s:%s" % (claim.kind, claim.key_sha256),
                ),
            )

    def _table(self, name: str) -> str:
        return self.schema + "." + name

    def _insert_quarantine_once_cursor(
        self, cursor, workflow_id: str, reason: str, digest: str, occurred_at: str,
    ) -> None:
        cursor.execute(
            "INSERT INTO %s(workflow_id, reason, conflicting_sha256, occurred_at) "
            "SELECT %%s, %%s, %%s, %%s WHERE NOT EXISTS "
            "(SELECT 1 FROM %s WHERE workflow_id=%%s AND reason=%%s AND conflicting_sha256=%%s)"
            % (self._table("quarantine"), self._table("quarantine")),
            (workflow_id, reason, digest, occurred_at, workflow_id, reason, digest),
        )

    def _preflight_intake_replay_cursor(
        self,
        cursor,
        workflow_id: str,
        request_id: str,
        receipt_sha256: str,
        guard: IntakeReplayGuard,
        occurred_at: str,
    ) -> bool:
        """Return False after persisting conflicts; never partially bind a failed set."""

        self._lock_intake_claims(cursor, guard.claims)
        existing: dict[tuple[str, str], str] = {}
        for claim in guard.claims:
            cursor.execute(
                "SELECT upgrade_intent_sha256 FROM %s WHERE claim_kind=%%s AND claim_key_sha256=%%s"
                % self._table("intake_replay_binding"),
                (claim.kind, claim.key_sha256),
            )
            row = cursor.fetchone()
            if row is not None:
                existing[(claim.kind, claim.key_sha256)] = str(row[0])

        conflicts = [
            (claim, existing[(claim.kind, claim.key_sha256)])
            for claim in guard.claims
            if (
                (claim.kind, claim.key_sha256) in existing
                and existing[(claim.kind, claim.key_sha256)] != guard.upgrade_intent_sha256
            )
        ]
        if conflicts:
            for claim, bound_digest in conflicts:
                conflict_body = {
                    "workflow_id": workflow_id,
                    "request_id": request_id,
                    "claim_kind": claim.kind,
                    "claim_key_sha256": claim.key_sha256,
                    "bound_upgrade_intent_sha256": bound_digest,
                    "conflicting_upgrade_intent_sha256": guard.upgrade_intent_sha256,
                    "attempted_receipt_sha256": receipt_sha256,
                }
                conflict_digest = _domain_sha256(
                    "dps.factory-control-plane-host/intake-replay-conflict/v1",
                    conflict_body,
                )
                cursor.execute(
                    "INSERT INTO %s(conflict_sha256, workflow_id, request_id, claim_kind, "
                    "claim_key_sha256, bound_upgrade_intent_sha256, "
                    "conflicting_upgrade_intent_sha256, attempted_receipt_sha256, occurred_at) "
                    "VALUES (%%s, %%s, %%s, %%s, %%s, %%s, %%s, %%s, %%s) "
                    "ON CONFLICT (conflict_sha256) DO NOTHING"
                    % self._table("intake_replay_conflict"),
                    (
                        conflict_digest, workflow_id, request_id, claim.kind,
                        claim.key_sha256, bound_digest, guard.upgrade_intent_sha256,
                        receipt_sha256, occurred_at,
                    ),
                )
                self._insert_quarantine_once_cursor(
                    cursor, workflow_id, "INTAKE_REPLAY_CONFLICT",
                    conflict_digest, occurred_at,
                )
            return False

        for claim in guard.claims:
            if (claim.kind, claim.key_sha256) in existing:
                continue
            cursor.execute(
                "INSERT INTO %s(claim_kind, claim_key_sha256, upgrade_intent_sha256, "
                "first_workflow_id, first_request_id, first_receipt_sha256, occurred_at) "
                "VALUES (%%s, %%s, %%s, %%s, %%s, %%s, %%s)"
                % self._table("intake_replay_binding"),
                (
                    claim.kind, claim.key_sha256, guard.upgrade_intent_sha256,
                    workflow_id, request_id, receipt_sha256, occurred_at,
                ),
            )
        return True

    def register(self, request: Mapping[str, Any], request_sha256: str, role_binding: Mapping[str, Any]) -> bool:
        workflow_id = str(request["workflow_id"])
        conflict = False
        created = False
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id)
                cursor.execute(
                    "SELECT request_sha256, idempotency_key, request_json, workflow_id "
                    "FROM %s WHERE workflow_id=%%s" % self._table("workflow_request"),
                    (workflow_id,),
                )
                row = cursor.fetchone()
                if row is not None:
                    self._trusted_request_row(
                        (row[2], row[0], row[3], row[1]), workflow_id,
                    )
                    if row[0] != request_sha256 or row[1] != request["idempotency_key"]:
                        cursor.execute("INSERT INTO %s(workflow_id, reason, conflicting_sha256, occurred_at) VALUES (%%s, %%s, %%s, %%s)" % self._table("quarantine"), (workflow_id, "WORKFLOW_ID_HASH_CONFLICT", request_sha256, request["occurred_at"]))
                        conflict = True
                else:
                    cursor.execute("INSERT INTO %s(workflow_id, idempotency_key, request_sha256, request_json, occurred_at) VALUES (%%s, %%s, %%s, %%s::jsonb, %%s)" % self._table("workflow_request"), (workflow_id, request["idempotency_key"], request_sha256, json.dumps(request), request["occurred_at"]))
                    cursor.execute("INSERT INTO %s(workflow_id, binding_id, binding_sha256, binding_json, occurred_at) VALUES (%%s, %%s, %%s, %%s::jsonb, %%s)" % self._table("role_binding_receipt"), (workflow_id, role_binding["binding_id"], sha256(role_binding), json.dumps(role_binding), role_binding["verified_at"]))
                    self._append_event_cursor(cursor, workflow_id, "WORKFLOW_REQUESTED", "REQUESTED", {"request_sha256": request_sha256, "role_binding_id": role_binding["binding_id"]}, "requested:" + request["idempotency_key"], 0, request["occurred_at"])
                    created = True
            connection.commit()
        if conflict:
            raise IdempotencyConflict("workflow identity is already bound to different content")
        return created

    def request(self, workflow_id: str) -> dict[str, Any]:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT request_json, request_sha256, workflow_id, idempotency_key "
                "FROM %s WHERE workflow_id=%%s" % self._table("workflow_request"),
                (workflow_id,),
            ).fetchone()
        if row is None:
            raise FactoryHostError("unknown workflow")
        return self._trusted_request_row(row, workflow_id)

    def role_binding(self, workflow_id: str) -> dict[str, Any]:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT binding_json, binding_sha256, workflow_id, binding_id "
                "FROM %s WHERE workflow_id=%%s" % self._table("role_binding_receipt"),
                (workflow_id,),
            ).fetchone()
        if row is None:
            raise CorruptWorkflow("workflow role binding is missing")
        return self._trusted_binding_row(row, workflow_id)

    def acquire_fence(self, workflow_id: str, worker_identity: str, occurred_at: str) -> int:
        if not worker_identity or len(worker_identity) > 128:
            raise FactoryHostError("worker identity is invalid")
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id)
                cursor.execute("SELECT COALESCE(MAX(fencing_token),0)+1 FROM %s WHERE workflow_id=%%s" % self._table("fence_event"), (workflow_id,))
                token = int(cursor.fetchone()[0])
                cursor.execute("INSERT INTO %s(workflow_id, fencing_token, worker_identity, occurred_at) VALUES (%%s, %%s, %%s, %%s)" % self._table("fence_event"), (workflow_id, token, worker_identity, occurred_at))
            connection.commit()
        return token

    def acquire_fence_if_state(self, workflow_id: str, worker_identity: str, occurred_at: str, allowed_states: Sequence[str]) -> int:
        if not worker_identity or len(worker_identity) > 128:
            raise FactoryHostError("worker identity is invalid")
        allowed = frozenset(allowed_states)
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id)
                cursor.execute(
                    "SELECT state FROM %s WHERE workflow_id=%%s ORDER BY sequence DESC LIMIT 1"
                    % self._table("workflow_event"),
                    (workflow_id,),
                )
                row = cursor.fetchone()
                if row is None:
                    raise FactoryHostError("unknown workflow")
                if str(row[0]) not in allowed:
                    raise IllegalTransition("workflow state does not allow this management operation: " + str(row[0]))
                cursor.execute("SELECT COALESCE(MAX(fencing_token),0)+1 FROM %s WHERE workflow_id=%%s" % self._table("fence_event"), (workflow_id,))
                token = int(cursor.fetchone()[0])
                cursor.execute("INSERT INTO %s(workflow_id, fencing_token, worker_identity, occurred_at) VALUES (%%s, %%s, %%s, %%s)" % self._table("fence_event"), (workflow_id, token, worker_identity, occurred_at))
            connection.commit()
        return token

    def latest_fence(self, workflow_id: str) -> int:
        with self._connect() as connection:
            row = connection.execute("SELECT COALESCE(MAX(fencing_token),0) FROM %s WHERE workflow_id=%%s" % self._table("fence_event"), (workflow_id,)).fetchone()
        return int(row[0])

    def _assert_fence_cursor(self, cursor, workflow_id: str, fence: int) -> None:
        cursor.execute("SELECT COALESCE(MAX(fencing_token),0) FROM %s WHERE workflow_id=%%s" % self._table("fence_event"), (workflow_id,))
        if fence <= 0 or int(cursor.fetchone()[0]) != fence:
            raise StaleFence("worker fencing token is stale")

    def events(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT event_json, workflow_id, sequence, event_id, event_type, state, "
                "fencing_token, idempotency_key, payload_sha256, payload_json, "
                "previous_event_sha256, event_sha256 FROM %s "
                "WHERE workflow_id=%%s ORDER BY sequence" % self._table("workflow_event"),
                (workflow_id,),
            ).fetchall()
        result = []
        for row in rows:
            event = self._loads(row[0])
            if not isinstance(event, Mapping):
                raise CorruptWorkflow("workflow event JSON is malformed")
            expected = (
                workflow_id, event.get("sequence"), event.get("event_id"),
                event.get("event_type"), event.get("state"),
                event.get("fencing_token"), event.get("idempotency_key"),
                event.get("payload_sha256"), event.get("payload"),
                event.get("previous_event_sha256"), event.get("event_sha256"),
            )
            actual = (
                row[1], int(row[2]), row[3], row[4], row[5], int(row[6]),
                row[7], row[8], self._loads(row[9]), row[10], row[11],
            )
            if actual != expected:
                raise CorruptWorkflow("workflow event columns drifted from event JSON")
            result.append(copy.deepcopy(dict(event)))
        if not result:
            raise CorruptWorkflow("workflow event stream is missing")
        validate_event_stream(result)
        return result

    def receipts(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT r.receipt_json, r.receipt_sha256, r.workflow_id, r.request_id, "
                "r.receipt_id, d.receipt_sha256 FROM %s r JOIN %s d ON "
                "d.workflow_id=r.workflow_id AND d.request_id=r.request_id "
                "AND d.status='ACKNOWLEDGED' WHERE r.workflow_id=%%s ORDER BY d.delivery_sequence"
                % (self._table("module_receipt"), self._table("outbox_delivery_event")),
                (workflow_id,),
            ).fetchall()
        result = []
        for row in rows:
            if row[5] != row[1]:
                raise CorruptWorkflow("acknowledged delivery receipt digest drifted")
            result.append(self._trusted_receipt_row(row[:5], workflow_id))
        return result

    def pending_messages(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._connect() as connection:
            message_rows = connection.execute(
                "SELECT message_json, message_sha256, workflow_id, request_id, stage_id "
                "FROM %s WHERE workflow_id=%%s ORDER BY request_id"
                % self._table("outbox_message"), (workflow_id,),
            ).fetchall()
            receipt_rows = connection.execute(
                "SELECT receipt_json, receipt_sha256, workflow_id, request_id, receipt_id "
                "FROM %s WHERE workflow_id=%%s" % self._table("module_receipt"),
                (workflow_id,),
            ).fetchall()
        acknowledged = {
            self._trusted_receipt_row(row, workflow_id)["request_id"]
            for row in receipt_rows
        }
        messages = [self._trusted_message_row(row, workflow_id) for row in message_rows]
        return [message for message in messages if message["request_id"] not in acknowledged]

    def stage_for_phase(self, workflow_id: str, state: str, activation_sequence: int, phase: str) -> str | None:
        for event in self.events(workflow_id):
            payload = event.get("payload")
            if (
                event.get("event_type") == "STAGE_SCHEDULED"
                and event.get("state") == state
                and isinstance(payload, Mapping)
                and payload.get("activation_sequence") == activation_sequence
                and payload.get("phase") == phase
            ):
                return str(payload.get("stage_id"))
        return None

    def schedule_phase(self, workflow_id: str, state: str, activation_sequence: int, phase: str, messages: Sequence[Mapping[str, Any]], fence: int, occurred_at: str) -> str:
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id)
                self._assert_fence_cursor(cursor, workflow_id, fence)
                cursor.execute("SELECT payload_json->>'stage_id' FROM %s WHERE workflow_id=%%s AND event_type='STAGE_SCHEDULED' AND state=%%s AND (payload_json->>'activation_sequence')::bigint=%%s AND payload_json->>'phase'=%%s LIMIT 1" % self._table("workflow_event"), (workflow_id, state, activation_sequence, phase))
                existing = cursor.fetchone()
                if existing:
                    return str(existing[0])
                stage_id = "stage:" + sha256({"workflow_id": workflow_id, "state": state, "activation_sequence": activation_sequence, "phase": phase})[:32]
                prepared = []
                for raw in messages:
                    message = copy.deepcopy(dict(raw)); message["stage_id"] = stage_id
                    message["request_id"] = "call:" + sha256({
                        "stage_id": stage_id, "target": message["target_module"],
                        "operation": message["operation"], "role": message["actor_role"],
                        "subject_module": message.get("context", {}).get("subject_module"),
                    })[:32]
                    prepared.append(message)
                self._append_event_cursor(cursor, workflow_id, "STAGE_SCHEDULED", state, {"activation_sequence": activation_sequence, "phase": phase, "stage_id": stage_id, "request_ids": [item["request_id"] for item in prepared]}, "schedule:" + stage_id, fence, occurred_at)
                for message in prepared:
                    cursor.execute("INSERT INTO %s(workflow_id, request_id, stage_id, message_sha256, message_json, occurred_at) VALUES (%%s, %%s, %%s, %%s, %%s::jsonb, %%s)" % self._table("outbox_message"), (workflow_id, message["request_id"], stage_id, sha256(message), json.dumps(message), occurred_at))
            connection.commit()
        return stage_id

    def stage_receipts(self, workflow_id: str, stage_id: str) -> list[dict[str, Any]]:
        sql = (
            "SELECT r.receipt_json, r.receipt_sha256, r.workflow_id, r.request_id, "
            "r.receipt_id, m.message_json, m.message_sha256, m.workflow_id, "
            "m.request_id, m.stage_id FROM %s r JOIN %s m ON "
            "m.workflow_id=r.workflow_id AND m.request_id=r.request_id "
            "WHERE r.workflow_id=%%s AND m.stage_id=%%s ORDER BY r.request_id"
            % (self._table("module_receipt"), self._table("outbox_message"))
        )
        with self._connect() as connection:
            rows = connection.execute(sql, (workflow_id, stage_id)).fetchall()
        result = []
        for row in rows:
            receipt = self._trusted_receipt_row(row[:5], workflow_id)
            message = self._trusted_message_row(row[5:10], workflow_id)
            if receipt["request_id"] != message["request_id"] or message["stage_id"] != stage_id:
                raise CorruptWorkflow("stage receipt is not bound to its outbox message")
            result.append(receipt)
        return result

    def record_attempt(self, workflow_id: str, request_id: str, command_sha256: str, fence: int, occurred_at: str) -> None:
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id); self._assert_fence_cursor(cursor, workflow_id, fence)
                cursor.execute(
                    "SELECT message_json, message_sha256, workflow_id, request_id, stage_id "
                    "FROM %s WHERE workflow_id=%%s AND request_id=%%s"
                    % self._table("outbox_message"), (workflow_id, request_id),
                )
                message_row = cursor.fetchone()
                if message_row is None:
                    raise FactoryHostError("unknown outbox request")
                self._trusted_message_row(message_row, workflow_id)
                cursor.execute("INSERT INTO %s(workflow_id, request_id, status, command_sha256, fencing_token, occurred_at) VALUES (%%s, %%s, 'ATTEMPTED', %%s, %%s, %%s)" % self._table("outbox_delivery_event"), (workflow_id, request_id, command_sha256, fence, occurred_at))
            connection.commit()

    def record_receipt(self, workflow_id: str, request_id: str, receipt: Mapping[str, Any], fence: int, occurred_at: str) -> bool:
        digest = sha256(dict(receipt))
        replay_guard = intake_replay_guard_from_receipt(receipt)
        conflict = False
        conflict_message = "provider returned conflicting content for one request"
        created = False
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id); self._assert_fence_cursor(cursor, workflow_id, fence)
                cursor.execute(
                    "SELECT message_json, message_sha256, workflow_id, request_id, stage_id "
                    "FROM %s WHERE workflow_id=%%s AND request_id=%%s"
                    % self._table("outbox_message"),
                    (workflow_id, request_id),
                )
                message_row = cursor.fetchone()
                if message_row is None:
                    raise FactoryHostError("unknown outbox request")
                self._trusted_message_row(message_row, workflow_id)
                cursor.execute(
                    "SELECT receipt_sha256, receipt_json, workflow_id, request_id, "
                    "receipt_id FROM %s WHERE workflow_id=%%s AND request_id=%%s"
                    % self._table("module_receipt"), (workflow_id, request_id),
                )
                row = cursor.fetchone()
                if row:
                    self._trusted_receipt_row(
                        (row[1], row[0], row[2], row[3], row[4]), workflow_id,
                    )
                    if row[0] != digest:
                        self._insert_quarantine_once_cursor(
                            cursor, workflow_id, "RECEIPT_HASH_CONFLICT", digest, occurred_at,
                        )
                        conflict = True
                if not conflict and replay_guard is not None:
                    if not self._preflight_intake_replay_cursor(
                        cursor, workflow_id, request_id, digest, replay_guard, occurred_at,
                    ):
                        conflict = True
                        conflict_message = (
                            "intake replay claim is already bound to a different full intent digest"
                        )
                if not conflict and row is None:
                    stored = copy.deepcopy(dict(receipt)); stored["receipt_id"] = "module-receipt:" + digest[:32]
                    cursor.execute("INSERT INTO %s(workflow_id, request_id, receipt_id, receipt_sha256, receipt_json, fencing_token, occurred_at) VALUES (%%s, %%s, %%s, %%s, %%s::jsonb, %%s, %%s)" % self._table("module_receipt"), (workflow_id, request_id, stored["receipt_id"], digest, json.dumps(stored), fence, occurred_at))
                    cursor.execute("INSERT INTO %s(workflow_id, request_id, status, receipt_sha256, fencing_token, occurred_at) VALUES (%%s, %%s, 'ACKNOWLEDGED', %%s, %%s, %%s)" % self._table("outbox_delivery_event"), (workflow_id, request_id, digest, fence, occurred_at)); created = True
            connection.commit()
        if conflict:
            raise IdempotencyConflict(conflict_message)
        return created

    def register_native_stop_authority_trust(
        self,
        workflow_id: str,
        fact: Mapping[str, Any],
        fence: int,
        occurred_at: str,
    ) -> bool:
        value = validate_native_stop_trust_durable_fact(fact)
        receipt_id = str(value["receipt_id"])
        fact_sha256 = sha256(value)
        conflict = False
        conflict_digest = ""
        created = False
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id)
                self._assert_fence_cursor(cursor, workflow_id, fence)
                cursor.execute(
                    "SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))",
                    ("dps-native-stop-trust:" + receipt_id,),
                )
                cursor.execute(
                    "SELECT fact_json, fact_sha256, receipt_id, receipt_sha256, "
                    "release_bom_id, release_bom_sha256, integration_commit, "
                    "release_bom_generation, activation_token_sha256, "
                    "authority_sets_sha256, first_workflow_id FROM %s "
                    "WHERE receipt_id=%%s"
                    % self._table("native_stop_authority_trust_binding"),
                    (receipt_id,),
                )
                row = cursor.fetchone()
                if row is not None:
                    existing = self._trusted_native_stop_trust_row(row)
                    if existing["receipt_sha256"] != value["receipt_sha256"]:
                        conflict_digest = sha256({
                            "receipt_id": receipt_id,
                            "bound_receipt_sha256": existing["receipt_sha256"],
                            "conflicting_receipt_sha256": value["receipt_sha256"],
                        })
                        self._insert_quarantine_once_cursor(
                            cursor,
                            workflow_id,
                            "NATIVE_STOP_TRUST_RECEIPT_HASH_CONFLICT",
                            conflict_digest,
                            occurred_at,
                        )
                        conflict = True
                else:
                    cursor.execute(
                        "INSERT INTO %s(receipt_id, receipt_sha256, release_bom_id, "
                        "release_bom_sha256, integration_commit, release_bom_generation, "
                        "activation_token_sha256, authority_sets_sha256, fact_sha256, "
                        "fact_json, first_workflow_id, occurred_at) VALUES "
                        "(%%s, %%s, %%s, %%s, %%s, %%s, %%s, %%s, %%s, %%s::jsonb, %%s, %%s)"
                        % self._table("native_stop_authority_trust_binding"),
                        (
                            receipt_id,
                            value["receipt_sha256"],
                            value["release_bom_id"],
                            value["release_bom_sha256"],
                            value["integration_commit"],
                            value["release_bom_generation"],
                            value["activation_token_sha256"],
                            value["authority_sets_sha256"],
                            fact_sha256,
                            json.dumps(value),
                            workflow_id,
                            occurred_at,
                        ),
                    )
                    created = True
            connection.commit()
        if conflict:
            raise IdempotencyConflict(
                "native-stop trust receipt id is globally bound to different bytes: "
                + conflict_digest,
            )
        return created

    def native_stop_authority_trust(
        self,
        receipt_id: str,
    ) -> dict[str, Any] | None:
        with self._connect() as connection:
            row = connection.execute(
                "SELECT fact_json, fact_sha256, receipt_id, receipt_sha256, "
                "release_bom_id, release_bom_sha256, integration_commit, "
                "release_bom_generation, activation_token_sha256, "
                "authority_sets_sha256, first_workflow_id FROM %s WHERE receipt_id=%%s"
                % self._table("native_stop_authority_trust_binding"),
                (receipt_id,),
            ).fetchone()
        return None if row is None else self._trusted_native_stop_trust_row(row)

    def append_phase_completed(self, workflow_id: str, state: str, activation_sequence: int, phase: str, fence: int, occurred_at: str) -> None:
        key = "phase:%s:%s:%s" % (state, activation_sequence, phase)
        self.transition(workflow_id, state, "PHASE_COMPLETED", {"activation_sequence": activation_sequence, "phase": phase}, key, fence, occurred_at)

    def transition(self, workflow_id: str, state: str, event_type: str, payload: Mapping[str, Any], idempotency_key: str, fence: int, occurred_at: str) -> dict[str, Any]:
        idempotency_key = opaque_idempotency(idempotency_key)
        conflict = False
        event: dict[str, Any] | None = None
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id); self._assert_fence_cursor(cursor, workflow_id, fence)
                cursor.execute(
                    "SELECT state, payload_sha256, event_json, event_sha256, "
                    "workflow_id, idempotency_key FROM %s WHERE workflow_id=%%s "
                    "AND idempotency_key=%%s" % self._table("workflow_event"),
                    (workflow_id, idempotency_key),
                )
                row = cursor.fetchone()
                if row:
                    stored_event = self._loads(row[2])
                    if (
                        not isinstance(stored_event, Mapping)
                        or row[3] != sha256({
                            key: value for key, value in stored_event.items()
                            if key != "event_sha256"
                        })
                        or stored_event.get("event_sha256") != row[3]
                        or row[4] != workflow_id
                        or row[5] != stored_event.get("idempotency_key")
                    ):
                        raise CorruptWorkflow("idempotent workflow event row is corrupt")
                    if row[0] != state or row[1] != sha256(dict(payload)):
                        cursor.execute(
                            "INSERT INTO %s(workflow_id, reason, conflicting_sha256, occurred_at) VALUES (%%s, 'TRANSITION_IDEMPOTENCY_CONFLICT', %%s, %%s)"
                            % self._table("quarantine"),
                            (workflow_id, sha256({"state": state, "payload": dict(payload)}), occurred_at),
                        )
                        conflict = True
                    else:
                        event = copy.deepcopy(dict(stored_event))
                else:
                    event = self._append_event_cursor(cursor, workflow_id, event_type, state, payload, idempotency_key, fence, occurred_at)
            connection.commit()
        if conflict:
            raise IdempotencyConflict("transition idempotency key is bound to different content")
        if event is None:
            raise CorruptWorkflow("transition did not produce an event")
        return event

    def quarantine(self, workflow_id: str, reason: str, digest: str, fence: int, occurred_at: str) -> None:
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, workflow_id); self._assert_fence_cursor(cursor, workflow_id, fence)
                cursor.execute("INSERT INTO %s(workflow_id, reason, conflicting_sha256, occurred_at) VALUES (%%s, %%s, %%s, %%s)" % self._table("quarantine"), (workflow_id, reason, digest, occurred_at))
            connection.commit()

    def quarantine_records(self, workflow_id: str) -> list[dict[str, Any]]:
        with self._connect() as connection:
            rows = connection.execute(
                "SELECT quarantine_sequence, reason, conflicting_sha256, occurred_at FROM %s WHERE workflow_id=%%s ORDER BY quarantine_sequence"
                % self._table("quarantine"),
                (workflow_id,),
            ).fetchall()
        return [
            {
                "sequence": int(row[0]), "reason": str(row[1]),
                "digest": str(row[2]), "occurred_at": row[3].isoformat(),
            }
            for row in rows
        ]

    def _append_event_cursor(self, cursor, workflow_id: str, event_type: str, state: str, payload: Mapping[str, Any], idempotency_key: str, fence: int, occurred_at: str) -> dict[str, Any]:
        idempotency_key = opaque_idempotency(idempotency_key)
        cursor.execute(
            "SELECT request_json, request_sha256, workflow_id, idempotency_key "
            "FROM %s WHERE workflow_id=%%s" % self._table("workflow_request"),
            (workflow_id,),
        )
        request_row = cursor.fetchone()
        if request_row is None:
            raise CorruptWorkflow("workflow request is missing while appending an event")
        request = self._trusted_request_row(request_row, workflow_id)
        cursor.execute("SELECT sequence, event_sha256 FROM %s WHERE workflow_id=%%s ORDER BY sequence DESC LIMIT 1" % self._table("workflow_event"), (workflow_id,))
        row = cursor.fetchone(); sequence = int(row[0]) + 1 if row else 1; previous = row[1] if row else ZERO_HASH
        body = {
            "schema_version": "1.0.0", "contract_id": "factory.workflow.event/v1", "producer_module": "factory-control-plane-host",
            "soul_id": request["soul_id"], "device_binding_id": request["device_binding_id"],
            "platform_account_id": request["platform_account_id"], "trace_id": request["trace_id"],
            "privacy_class": "internal", "workflow_id": workflow_id, "sequence": sequence,
            "event_id": "workflow-event:" + sha256({"workflow_id": workflow_id, "sequence": sequence, "idempotency_key": idempotency_key})[:32],
            "event_type": event_type, "state": state, "fencing_token": fence, "idempotency_key": idempotency_key,
            "payload": copy.deepcopy(dict(payload)), "payload_sha256": sha256(dict(payload)), "previous_event_sha256": previous,
            "occurred_at": occurred_at,
        }
        body["event_sha256"] = sha256(body)
        cursor.execute("INSERT INTO %s(workflow_id, sequence, event_id, event_type, state, fencing_token, idempotency_key, payload_sha256, payload_json, previous_event_sha256, event_sha256, event_json, occurred_at) VALUES (%%s, %%s, %%s, %%s, %%s, %%s, %%s, %%s, %%s::jsonb, %%s, %%s, %%s::jsonb, %%s)" % self._table("workflow_event"), (workflow_id, sequence, body["event_id"], event_type, state, fence, idempotency_key, body["payload_sha256"], json.dumps(payload), previous, body["event_sha256"], json.dumps(body), occurred_at))
        return body


__all__ = [
    "IntakeReplayClaim", "IntakeReplayGuard", "MigrationFile",
    "PostgresSchemaMigrator", "PostgresWorkflowRepository",
    "discover_migrations", "intake_replay_claim_key_sha256",
    "intake_replay_guard_from_receipt", "intake_upgrade_intent_sha256",
    "verify_migration_history",
]
