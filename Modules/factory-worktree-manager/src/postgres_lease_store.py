"""PostgreSQL 18.4 production adapter for Factory lease and fencing truth."""

from __future__ import annotations

import datetime as dt
import json
import re
from pathlib import Path
from typing import Any, Dict, Mapping, Sequence

import psycopg
from psycopg import sql
from psycopg.rows import dict_row

from worktree_manager import (
    ExternalSqliteLeaseStore,
    LeaseConflict,
    StaleFence,
    WorktreeError,
    _canonical_bytes,
    _sha256,
)


PINNED_PSYCOPG_VERSION = "3.3.4"
PINNED_POSTGRES_SERVER_VERSION = 180004
_SCHEMA_ID = re.compile(r"^[a-z][a-z0-9_]{0,62}$")
_PLAN_ID = re.compile(r"^worktree:[0-9a-f]{32}$")
_LEASE_ID = re.compile(r"^lease:[0-9a-f]{32}$")
_MIGRATION = Path(__file__).parents[1] / "migrations" / "001_external_lease_store.sql"


def _utc(value: dt.datetime) -> str:
    if value.tzinfo is None:
        raise WorktreeError("database returned a timestamp without timezone")
    return value.astimezone(dt.timezone.utc).isoformat().replace("+00:00", "Z")


class PostgresLeaseStore:
    """Production lease store using DB transactions and advisory path locks."""

    def __init__(
        self,
        connection_string: str,
        *,
        schema: str,
        required_server_version: int = PINNED_POSTGRES_SERVER_VERSION,
    ) -> None:
        if not isinstance(connection_string, str) or not connection_string.strip():
            raise WorktreeError("PostgreSQL connection string is required")
        if not isinstance(schema, str) or _SCHEMA_ID.fullmatch(schema) is None:
            raise WorktreeError("invalid isolated Factory schema")
        if psycopg.__version__ != PINNED_PSYCOPG_VERSION:
            raise WorktreeError(
                f"psycopg version drift: expected {PINNED_PSYCOPG_VERSION}, got {psycopg.__version__}"
            )
        self.connection_string = connection_string
        self.schema = schema
        self.required_server_version = required_server_version
        with self._connect(set_search_path=False) as connection:
            if connection.info.server_version != required_server_version:
                raise WorktreeError(
                    f"PostgreSQL version drift: expected {required_server_version}, "
                    f"got {connection.info.server_version}"
                )

    def _connect(self, *, set_search_path: bool = True) -> psycopg.Connection:
        connection = psycopg.connect(
            self.connection_string, autocommit=False, row_factory=dict_row,
            connect_timeout=10, application_name="dps-factory-worktree-manager",
        )
        if set_search_path:
            connection.execute(
                sql.SQL("SET search_path TO {}").format(sql.Identifier(self.schema))
            )
        return connection

    def apply_migrations(self) -> None:
        """Provision the isolated schema; production deployers call this explicitly."""
        migration = _MIGRATION.read_text(encoding="utf-8")
        with self._connect(set_search_path=False) as connection:
            connection.execute(
                sql.SQL("CREATE SCHEMA IF NOT EXISTS {}").format(sql.Identifier(self.schema))
            )
            connection.execute(
                sql.SQL("SET search_path TO {}").format(sql.Identifier(self.schema))
            )
            connection.execute(migration, prepare=False)

    @staticmethod
    def _validate_lock_keys(lock_keys: Sequence[str]) -> tuple[str, ...]:
        return ExternalSqliteLeaseStore._validate_lock_keys(lock_keys)

    @staticmethod
    def _validate_envelope(envelope: Mapping[str, Any]) -> None:
        ExternalSqliteLeaseStore._validate_envelope(envelope)

    @staticmethod
    def _lock(cursor: psycopg.Cursor, key: str) -> None:
        cursor.execute(
            "SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))", (key,)
        )

    @staticmethod
    def _lease_contract(row: Mapping[str, Any]) -> Dict[str, Any]:
        envelope = row["envelope_json"]
        keys = list(row["lock_keys_json"])
        tokens = {key: int(value) for key, value in row["lock_tokens_json"].items()}
        return {
            "schema_version": "dps.worktree-lease/v1",
            "contract_id": "worktree.lease/v1",
            "producer_module": "factory-worktree-manager",
            "soul_id": envelope.get("soul_id"),
            "device_binding_id": envelope.get("device_binding_id"),
            "platform_account_id": envelope.get("platform_account_id"),
            "trace_id": envelope["trace_id"],
            "idempotency_key": row["idempotency_key"],
            "occurred_at": _utc(row["acquired_at"]),
            "privacy_class": "internal",
            "lease_id": row["lease_id"],
            "plan_id": row["plan_id"],
            "holder_identity": row["holder_identity"],
            "lock_keys": keys,
            "lock_tokens": tokens,
            "fencing_token": max(tokens.values()),
            "acquired_at": _utc(row["acquired_at"]),
            "expires_at": _utc(row["expires_at"]),
            "status": row["status"],
        }

    def acquire(
        self,
        *,
        plan_id: str,
        holder_identity: str,
        lock_keys: Sequence[str],
        ttl_seconds: int,
        envelope: Mapping[str, Any],
    ) -> Dict[str, Any]:
        self._validate_envelope(envelope)
        if not isinstance(plan_id, str) or _PLAN_ID.fullmatch(plan_id) is None:
            raise WorktreeError("invalid worktree plan id")
        if not isinstance(holder_identity, str) or not holder_identity:
            raise WorktreeError("holder identity is required")
        if not isinstance(ttl_seconds, int) or not 1 <= ttl_seconds <= 3600:
            raise WorktreeError("lease TTL is outside policy")
        keys = self._validate_lock_keys(lock_keys)
        normalized_envelope = {
            "soul_id": envelope.get("soul_id"),
            "device_binding_id": envelope.get("device_binding_id"),
            "platform_account_id": envelope.get("platform_account_id"),
            "trace_id": envelope["trace_id"],
            "idempotency_key": envelope["idempotency_key"],
        }
        lease_id = "lease:" + _sha256(_canonical_bytes({
            "plan_id": plan_id, "holder_identity": holder_identity,
            "lock_keys": keys, "idempotency_key": envelope["idempotency_key"],
        }))[:32]
        with self._connect() as connection:
            with connection.cursor() as cursor:
                self._lock(cursor, "idempotency:" + envelope["idempotency_key"])
                for key in keys:
                    self._lock(cursor, "lock:" + key)
                cursor.execute("SELECT clock_timestamp() AS now")
                now = cursor.fetchone()["now"]
                expires = now + dt.timedelta(seconds=ttl_seconds)
                cursor.execute(
                    "SELECT * FROM lease_records WHERE idempotency_key = %s FOR UPDATE",
                    (envelope["idempotency_key"],),
                )
                existing = cursor.fetchone()
                if existing is not None:
                    same = (
                        existing["plan_id"] == plan_id
                        and existing["holder_identity"] == holder_identity
                        and tuple(existing["lock_keys_json"]) == keys
                        and existing["envelope_json"] == normalized_envelope
                    )
                    if not same:
                        raise LeaseConflict("idempotency key payload conflict")
                    if existing["status"] != "ACTIVE" or existing["expires_at"] <= now:
                        raise StaleFence("idempotent lease is no longer active; use a new key")
                    return self._lease_contract(existing)

                for key in keys:
                    cursor.execute(
                        "SELECT * FROM active_locks WHERE lock_key = %s FOR UPDATE", (key,)
                    )
                    active = cursor.fetchone()
                    if active is not None and not active["revoked"] and active["expires_at"] > now:
                        raise LeaseConflict("active lock conflict: " + key)
                    if active is not None and active["expires_at"] <= now:
                        cursor.execute(
                            "UPDATE lease_records SET status='EXPIRED' "
                            "WHERE lease_id=%s AND status='ACTIVE'",
                            (active["lease_id"],),
                        )

                tokens: Dict[str, int] = {}
                for key in keys:
                    cursor.execute(
                        "INSERT INTO fencing_counters(lock_key, last_token) VALUES (%s, 1) "
                        "ON CONFLICT(lock_key) DO UPDATE "
                        "SET last_token=fencing_counters.last_token + 1 RETURNING last_token",
                        (key,),
                    )
                    token = int(cursor.fetchone()["last_token"])
                    tokens[key] = token
                    cursor.execute(
                        "INSERT INTO active_locks(lock_key, lease_id, holder_identity, fencing_token, acquired_at, expires_at, revoked) "
                        "VALUES (%s,%s,%s,%s,%s,%s,FALSE) "
                        "ON CONFLICT(lock_key) DO UPDATE SET lease_id=EXCLUDED.lease_id, "
                        "holder_identity=EXCLUDED.holder_identity, fencing_token=EXCLUDED.fencing_token, "
                        "acquired_at=EXCLUDED.acquired_at, expires_at=EXCLUDED.expires_at, revoked=FALSE",
                        (key, lease_id, holder_identity, token, now, expires),
                    )
                cursor.execute(
                    "INSERT INTO lease_records(lease_id, plan_id, holder_identity, idempotency_key, "
                    "lock_keys_json, lock_tokens_json, envelope_json, acquired_at, expires_at, status) "
                    "VALUES (%s,%s,%s,%s,%s::jsonb,%s::jsonb,%s::jsonb,%s,%s,'ACTIVE') RETURNING *",
                    (
                        lease_id, plan_id, holder_identity, envelope["idempotency_key"],
                        json.dumps(list(keys), separators=(",", ":")),
                        json.dumps(tokens, sort_keys=True, separators=(",", ":")),
                        json.dumps(normalized_envelope, sort_keys=True, separators=(",", ":")),
                        now, expires,
                    ),
                )
                return self._lease_contract(cursor.fetchone())

    def assert_fence(self, lease_id: str, lock_tokens: Mapping[str, int]) -> None:
        if (
            not isinstance(lease_id, str) or _LEASE_ID.fullmatch(lease_id) is None
            or not isinstance(lock_tokens, Mapping) or not lock_tokens
        ):
            raise StaleFence("fencing token set is missing or invalid")
        with self._connect() as connection:
            with connection.cursor() as cursor:
                cursor.execute("SELECT * FROM lease_records WHERE lease_id=%s", (lease_id,))
                first = cursor.fetchone()
                if first is None:
                    raise StaleFence("lease is absent")
                expected_keys = tuple(first["lock_keys_json"])
                if set(expected_keys) != set(lock_tokens):
                    raise StaleFence("fencing token set does not cover the lease")
                for key in sorted(expected_keys):
                    self._lock(cursor, "lock:" + key)
                cursor.execute("SELECT clock_timestamp() AS now")
                now = cursor.fetchone()["now"]
                cursor.execute("SELECT * FROM lease_records WHERE lease_id=%s FOR UPDATE", (lease_id,))
                record = cursor.fetchone()
                if record is None or record["status"] != "ACTIVE" or record["expires_at"] <= now:
                    raise StaleFence("lease is absent, expired, or revoked")
                for key in expected_keys:
                    cursor.execute("SELECT * FROM active_locks WHERE lock_key=%s FOR UPDATE", (key,))
                    active = cursor.fetchone()
                    if (
                        active is None or active["lease_id"] != lease_id or active["revoked"]
                        or active["expires_at"] <= now
                        or int(active["fencing_token"]) != lock_tokens.get(key)
                    ):
                        raise StaleFence("writer fencing token is stale: " + key)

    def verify_fence_fact(self, lease_contract: Mapping[str, Any]) -> Dict[str, Any]:
        if not isinstance(lease_contract, Mapping):
            raise StaleFence("lease contract is required")
        lease_id, tokens = lease_contract.get("lease_id"), lease_contract.get("lock_tokens")
        self.assert_fence(lease_id, tokens)
        with self._connect() as connection:
            row = connection.execute(
                "SELECT * FROM lease_records WHERE lease_id=%s", (lease_id,)
            ).fetchone()
        current = self._lease_contract(row)
        if current != dict(lease_contract):
            raise StaleFence("lease contract differs from external PostgreSQL truth")
        return {
            "verified": True, "fact_id": lease_id,
            "fact_sha256": _sha256(_canonical_bytes(current)),
            "plan_id": current["plan_id"], "lock_tokens": current["lock_tokens"],
            "fencing_token": current["fencing_token"],
        }

    def revoke(self, lease_id: str) -> None:
        if not isinstance(lease_id, str) or _LEASE_ID.fullmatch(lease_id) is None:
            raise StaleFence("invalid lease id")
        with self._connect() as connection:
            with connection.cursor() as cursor:
                cursor.execute("SELECT * FROM lease_records WHERE lease_id=%s", (lease_id,))
                record = cursor.fetchone()
                if record is None:
                    raise StaleFence("active lease was not found")
                for key in sorted(record["lock_keys_json"]):
                    self._lock(cursor, "lock:" + key)
                cursor.execute(
                    "UPDATE lease_records SET status='REVOKED' "
                    "WHERE lease_id=%s AND status='ACTIVE' RETURNING lease_id",
                    (lease_id,),
                )
                if cursor.fetchone() is None:
                    raise StaleFence("active lease was not found")
                cursor.execute(
                    "UPDATE active_locks SET revoked=TRUE WHERE lease_id=%s", (lease_id,)
                )
