"""Digest-addressed artifact builder with trusted merge and Git-tree inputs."""

from __future__ import annotations

import base64
import binascii
import errno
import fcntl
import fnmatch
import hashlib
import hmac
import json
import os
import re
import selectors
import shutil
import stat
import subprocess
import threading
import time
from collections.abc import Callable, Mapping
from contextlib import contextmanager
from datetime import datetime
from pathlib import Path, PurePosixPath
from typing import Any


_COMMIT = re.compile(r"\A[0-9a-f]{40}\Z")
_GIT_OBJECT = re.compile(r"\A[0-9a-f]{40,64}\Z")
_MODULE = re.compile(r"\A[a-z0-9]+(?:-[a-z0-9]+)*\Z")
_SEMVER = re.compile(r"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?\Z")
_SHA256 = re.compile(r"\A[0-9a-f]{64}\Z")
_SOUL_ID = re.compile(r"\Asoul_[a-f0-9]{64}\Z")
_DEVICE_BINDING_ID = re.compile(r"\Adb_[a-f0-9]{32}\Z")
_PLATFORM_ACCOUNT_ID = re.compile(r"\Apa_[a-f0-9]{32}\Z")
_TRACE_ID = re.compile(r"\Atrace_[a-f0-9]{32}\Z")
_IDEMPOTENCY_KEY = re.compile(r"\Aidem_[a-f0-9]{64}\Z")
_BUILD_ID = re.compile(r"\A[a-z0-9][a-z0-9._:-]{7,127}\Z")
_MERGE_ID = re.compile(r"\Amerge-[0-9a-f]{32}\Z")
_ACTOR_ID = re.compile(r"\A[a-z0-9][a-z0-9._:-]{0,127}\Z")
_RFC3339_TIMESTAMP = re.compile(
    r"\A(?P<date>[0-9]{4}-[0-9]{2}-[0-9]{2})T"
    r"(?P<time>[0-9]{2}:[0-9]{2}:[0-9]{2})(?P<fraction>\.[0-9]+)?"
    r"(?P<offset>Z|[+-][0-9]{2}:[0-9]{2})\Z"
)
_ARTIFACT_SUFFIX = re.compile(r"\A\.[A-Za-z0-9]{1,16}\Z")
_REQUEST_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class", "build_id",
    "module_id", "module_version", "integration_commit", "artifact_path",
    "expected_sha256", "merge_decision_id",
}
_ATTESTATION_FIELDS = {
    "algorithm", "key_id", "signer_identity", "payload_sha256",
    "signature_value",
}
_DECISION_FIELDS = {
    "schema_version", "contract_id", "producer_module", "soul_id",
    "device_binding_id", "platform_account_id", "trace_id",
    "idempotency_key", "occurred_at", "privacy_class", "decision_id",
    "merge_request_id", "integration_commit", "outcome", "reasons",
    "evidence_ids", "decided_by", "verification_scope",
    "trusted_policy_sha256", "runner_attestation_sha256",
}
_BUILD_IDENTITY_FIELDS = {
    "schema_version", "build_id", "request_sha256", "decision_sha256",
    "artifact_sha256", "source_tree_sha256", "module_id", "module_version",
    "integration_commit",
}
_SCHEMA_ATTESTATION = (
    "dps.factory-artifact-schema/v1;"
    "sha256=41392cabeca90e2a959dd5aed06d9a7430ed0ef7b50bc158e1d262a6cba25642"
)
_CLAIM_FUNCTION_PROSRC_SHA256 = (
    "fc56bd6435c1b96b5085e8a5cc55f4346ce0706a4b807ff16fcdc13ea9545763"
)
_MUTATION_FUNCTION_PROSRC_SHA256 = (
    "48876504ecd3989db12012190f9c45600a94c1e102500933d20a0c42c298699b"
)
_EXPECTED_BUILD_IDENTITY_COLUMNS = (
    ("build_id", "text", True, ""),
    ("claim_sha256", "character(64)", True, ""),
    ("request_sha256", "character(64)", True, ""),
    ("decision_sha256", "character(64)", True, ""),
    ("artifact_sha256", "character(64)", True, ""),
    ("source_tree_sha256", "character(64)", True, ""),
    ("module_id", "text", True, ""),
    ("module_version", "text", True, ""),
    ("integration_commit", "character(40)", True, ""),
    ("claimed_at", "timestamp with time zone", True, "clock_timestamp()"),
)

# These are security limits, not tuning hints.  They bound every allocation fed
# by an untrusted worktree or Git object database before publication metadata is
# assembled.  Increasing them requires a separately reviewed resource model.
_MAX_ARTIFACT_BYTES = 256 * 1024 * 1024
_MAX_MANIFEST_BYTES = 1024 * 1024
_MAX_SOURCE_FILES = 10_000
_MAX_SOURCE_FILE_BYTES = 256 * 1024 * 1024
_MAX_SOURCE_TOTAL_BYTES = 1024 * 1024 * 1024
_MAX_GIT_LISTING_BYTES = 32 * 1024 * 1024
_MAX_GIT_STDERR_BYTES = 64 * 1024
_MAX_METADATA_BYTES = 64 * 1024 * 1024
_MAX_REPOSITORY_PATH_BYTES = 4096
_MAX_OWNED_PATTERNS = 128
_READ_CHUNK_BYTES = 1024 * 1024

_REQUIRED_OPEN_FLAGS = ("O_CLOEXEC", "O_DIRECTORY", "O_NOFOLLOW")
_MISSING_OPEN_FLAGS = tuple(name for name in _REQUIRED_OPEN_FLAGS if not hasattr(os, name))


class ArtifactBuildError(RuntimeError):
    """A build input cannot produce a trustworthy immutable descriptor."""


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def _strict_json_loads(value: bytes) -> Any:
    def object_from_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, item in pairs:
            if key in result:
                raise ArtifactBuildError("module manifest contains a duplicate JSON key")
            result[key] = item
        return result

    return json.loads(value, object_pairs_hook=object_from_pairs)


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _validate_build_identity_claim(claim: Mapping[str, Any]) -> None:
    if set(claim) != _BUILD_IDENTITY_FIELDS:
        raise ArtifactBuildError("build identity claim has unknown or missing fields")
    if claim.get("schema_version") != "dps.artifact-build-identity-claim/v1":
        raise ArtifactBuildError("unknown build identity claim version")
    if not isinstance(claim.get("build_id"), str) or not _BUILD_ID.fullmatch(claim["build_id"]):
        raise ArtifactBuildError("invalid build identity claim build_id")
    for field in (
        "request_sha256", "decision_sha256", "artifact_sha256", "source_tree_sha256",
    ):
        if not isinstance(claim.get(field), str) or not _SHA256.fullmatch(claim[field]):
            raise ArtifactBuildError(f"invalid build identity claim {field}")
    if not isinstance(claim.get("module_id"), str) or not _MODULE.fullmatch(claim["module_id"]):
        raise ArtifactBuildError("invalid build identity claim module_id")
    if (
        not isinstance(claim.get("module_version"), str)
        or not _SEMVER.fullmatch(claim["module_version"])
    ):
        raise ArtifactBuildError("invalid build identity claim module_version")
    if (
        not isinstance(claim.get("integration_commit"), str)
        or not _COMMIT.fullmatch(claim["integration_commit"])
    ):
        raise ArtifactBuildError("invalid build identity claim integration_commit")


class BuildIdentityRegistry:
    """Linearizable durable ownership of a build ID and its exact validated inputs."""

    def claim(self, claim: Mapping[str, Any]) -> None:
        raise NotImplementedError


class InMemoryBuildIdentityRegistry(BuildIdentityRegistry):
    """Thread-safe unit-test registry; production must use a durable registry."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._claims: dict[str, bytes] = {}

    def claim(self, claim: Mapping[str, Any]) -> None:
        _validate_build_identity_claim(claim)
        payload = canonical_bytes(dict(claim))
        build_id = str(claim["build_id"])
        with self._lock:
            existing = self._claims.get(build_id)
            if existing is None:
                self._claims[build_id] = payload
                return
            if not hmac.compare_digest(existing, payload):
                raise ArtifactBuildError("build_id is already claimed by different validated inputs")


class PostgresBuildIdentityRegistry(BuildIdentityRegistry):
    """PostgreSQL-backed production registry using the sole SECURITY DEFINER claim API."""

    def __init__(self, connection_factory: Callable[[], Any]) -> None:
        if not callable(connection_factory):
            raise ValueError("connection_factory is required")
        self._connection_factory = connection_factory

    def claim(self, claim: Mapping[str, Any]) -> None:
        _validate_build_identity_claim(claim)
        claim_sha256 = _sha256_bytes(
            b"dps-artifact-build-identity-claim/v1\n" + canonical_bytes(dict(claim))
        )
        connection: Any | None = None
        cursor: Any | None = None
        try:
            connection = self._connection_factory()
            if connection is None:
                raise ArtifactBuildError("build identity registry connection is unavailable")
            if getattr(connection, "autocommit", None) is not False:
                raise ArtifactBuildError(
                    "build identity registry requires an owned non-autocommit transaction"
                )
            # A caller cannot lend a transaction whose earlier SET ROLE or
            # uncommitted work changes this claim.  The registry owns one clean
            # transaction from attestation through the durable commit.
            connection.rollback()
            cursor = connection.cursor()
            cursor.execute("SET LOCAL synchronous_commit = on")
            cursor.execute(
                "SELECT current_setting('server_version_num'), current_user, session_user, "
                "current_setting('synchronous_commit'), current_setting('fsync'), "
                "current_setting('full_page_writes'), role.rolcanlogin, role.rolinherit, "
                "role.rolsuper, role.rolcreaterole, role.rolcreatedb, role.rolreplication, "
                "role.rolbypassrls, "
                "EXISTS (SELECT 1 FROM pg_auth_members membership "
                "WHERE membership.member=role.oid OR membership.roleid=role.oid), "
                "database.datdba=role.oid, "
                "has_database_privilege(current_user, current_database(), 'CREATE'), "
                "has_database_privilege(current_user, current_database(), 'TEMP'), "
                "EXISTS (SELECT 1 FROM pg_namespace namespace "
                "WHERE has_schema_privilege(current_user, namespace.oid, 'CREATE')) "
                "FROM pg_roles role JOIN pg_database database "
                "ON database.datname=current_database() WHERE role.rolname=current_user"
            )
            security = cursor.fetchone()
            if (
                security is None
                or len(security) != 18
                or not isinstance(security[1], str)
                or not security[1]
                or security[0] != "180004"
                or security[2] != security[1]
                or security[3:6] != ("on", "on", "on")
                or security[6:] != (
                    True, False, False, False, False, False, False,
                    False, False, False, False, False,
                )
            ):
                raise ArtifactBuildError(
                    "build identity registry connection is not an exact least-privilege PostgreSQL 18.4 runtime"
                )
            runtime_role = security[1]

            cursor.execute(
                "SELECT schema_owner.rolname, table_owner.rolname, claim_owner.rolname, "
                "mutation_owner.rolname, "
                "obj_description(namespace.oid, 'pg_namespace'), "
                "obj_description(relation.oid, 'pg_class'), "
                "obj_description(claim.oid, 'pg_proc'), "
                "obj_description(mutation.oid, 'pg_proc'), "
                "relation.relkind, relation.relpersistence, relation.relrowsecurity, "
                "relation.relforcerowsecurity, relation.relnatts, "
                "(SELECT count(*) FROM pg_proc candidate "
                " WHERE candidate.pronamespace=namespace.oid), "
                "claim.prokind, claim.prosecdef, claim_language.lanname, claim.proconfig, "
                "pg_get_function_result(claim.oid), claim.pronargs, claim.proisstrict, "
                "claim.provolatile, claim.proparallel, claim.proleakproof, claim.prosrc, "
                "mutation.prokind, mutation.prosecdef, mutation_language.lanname, "
                "mutation.proconfig, pg_get_function_result(mutation.oid), "
                "mutation.pronargs, mutation.proisstrict, mutation.provolatile, "
                "mutation.proparallel, mutation.proleakproof, mutation.prosrc "
                "FROM pg_namespace namespace "
                "JOIN pg_roles schema_owner ON schema_owner.oid=namespace.nspowner "
                "JOIN pg_class relation ON relation.relnamespace=namespace.oid "
                "JOIN pg_roles table_owner ON table_owner.oid=relation.relowner "
                "JOIN pg_proc claim ON claim.oid=to_regprocedure(" 
                "'factory_artifact.claim_build_identity(text,text,text,text,text,text,text,text,text)') "
                "JOIN pg_roles claim_owner ON claim_owner.oid=claim.proowner "
                "JOIN pg_language claim_language ON claim_language.oid=claim.prolang "
                "JOIN pg_proc mutation ON mutation.oid=to_regprocedure(" 
                "'factory_artifact.reject_build_identity_mutation()') "
                "JOIN pg_roles mutation_owner ON mutation_owner.oid=mutation.proowner "
                "JOIN pg_language mutation_language ON mutation_language.oid=mutation.prolang "
                "WHERE namespace.nspname='factory_artifact' "
                "AND relation.relname='build_identity_claim'"
            )
            objects = cursor.fetchone()
            expected_claim_shape = (
                "r", "p", False, False, 10, 2,
                "f", True, "plpgsql", ["search_path=pg_catalog"],
                "boolean", 9, False, "v", "u", False,
            )
            expected_mutation_shape = (
                "f", False, "plpgsql", None, "trigger", 0,
                False, "v", "u", False,
            )
            if (
                objects is None
                or len(objects) != 36
                or len(set(objects[0:4])) != 1
                or objects[0] == runtime_role
                or objects[4:8] != (_SCHEMA_ATTESTATION,) * 4
                or objects[8:24] != expected_claim_shape
                or objects[25:35] != expected_mutation_shape
                or not isinstance(objects[24], str)
                or _sha256_bytes(objects[24].strip().encode("utf-8"))
                != _CLAIM_FUNCTION_PROSRC_SHA256
                or not isinstance(objects[35], str)
                or _sha256_bytes(objects[35].strip().encode("utf-8"))
                != _MUTATION_FUNCTION_PROSRC_SHA256
            ):
                raise ArtifactBuildError(
                    "build identity registry catalog does not match the reviewed migration"
                )

            cursor.execute(
                "WITH namespace AS ("
                " SELECT * FROM pg_namespace WHERE nspname='factory_artifact'"
                "), relation AS ("
                " SELECT target.* FROM pg_class target JOIN namespace "
                " ON namespace.oid=target.relnamespace "
                " WHERE target.relname='build_identity_claim'"
                "), claim AS ("
                " SELECT * FROM pg_proc WHERE oid=to_regprocedure(" 
                " 'factory_artifact.claim_build_identity(text,text,text,text,text,text,text,text,text)')"
                "), mutation AS ("
                " SELECT * FROM pg_proc WHERE oid=to_regprocedure(" 
                " 'factory_artifact.reject_build_identity_mutation()')"
                "), runtime AS ("
                " SELECT oid FROM pg_roles WHERE rolname=current_user"
                ") SELECT "
                "EXISTS (SELECT 1 FROM claim, runtime, "
                " LATERAL aclexplode(COALESCE(claim.proacl, acldefault('f', claim.proowner))) acl "
                " WHERE acl.grantee NOT IN (claim.proowner, runtime.oid)), "
                "(SELECT count(*) FROM claim, runtime, "
                " LATERAL aclexplode(COALESCE(claim.proacl, acldefault('f', claim.proowner))) acl "
                " WHERE acl.grantee=runtime.oid AND acl.privilege_type='EXECUTE'), "
                "COALESCE((SELECT bool_and(NOT acl.is_grantable) FROM claim, runtime, "
                " LATERAL aclexplode(COALESCE(claim.proacl, acldefault('f', claim.proowner))) acl "
                " WHERE acl.grantee=runtime.oid AND acl.privilege_type='EXECUTE'), false), "
                "EXISTS (SELECT 1 FROM mutation, "
                " LATERAL aclexplode(COALESCE(mutation.proacl, acldefault('f', mutation.proowner))) acl "
                " WHERE acl.grantee<>mutation.proowner), "
                "EXISTS (SELECT 1 FROM namespace, runtime, "
                " LATERAL aclexplode(COALESCE(namespace.nspacl, acldefault('n', namespace.nspowner))) acl "
                " WHERE acl.grantee<>namespace.nspowner "
                " AND NOT (acl.grantee=runtime.oid AND acl.privilege_type='USAGE')), "
                "(SELECT count(*) FROM namespace, runtime, "
                " LATERAL aclexplode(COALESCE(namespace.nspacl, acldefault('n', namespace.nspowner))) acl "
                " WHERE acl.grantee=runtime.oid AND acl.privilege_type='USAGE'), "
                "COALESCE((SELECT bool_and(NOT acl.is_grantable) FROM namespace, runtime, "
                " LATERAL aclexplode(COALESCE(namespace.nspacl, acldefault('n', namespace.nspowner))) acl "
                " WHERE acl.grantee=runtime.oid AND acl.privilege_type='USAGE'), false), "
                "EXISTS (SELECT 1 FROM relation, "
                " LATERAL aclexplode(COALESCE(relation.relacl, acldefault('r', relation.relowner))) acl "
                " WHERE acl.grantee<>relation.relowner)"
            )
            if cursor.fetchone() != (False, 1, True, False, False, 1, True, False):
                raise ArtifactBuildError(
                    "build identity registry ACLs are not the exact owner/runtime split"
                )

            cursor.execute(
                "SELECT attribute.attname, "
                "format_type(attribute.atttypid, attribute.atttypmod), "
                "attribute.attnotnull, COALESCE(pg_get_expr(default_value.adbin, "
                "default_value.adrelid), '') FROM pg_attribute attribute "
                "LEFT JOIN pg_attrdef default_value ON default_value.adrelid=attribute.attrelid "
                "AND default_value.adnum=attribute.attnum "
                "WHERE attribute.attrelid='factory_artifact.build_identity_claim'::regclass "
                "AND attribute.attnum>0 AND NOT attribute.attisdropped "
                "ORDER BY attribute.attnum"
            )
            if tuple(cursor.fetchall()) != _EXPECTED_BUILD_IDENTITY_COLUMNS:
                raise ArtifactBuildError("build identity registry columns do not match migration v1")

            cursor.execute(
                "SELECT count(*), bool_and(constraint_row.convalidated), "
                "bool_and(NOT constraint_row.condeferrable AND NOT constraint_row.condeferred), "
                "count(*) FILTER (WHERE constraint_row.contype='p'), "
                "max(pg_get_constraintdef(constraint_row.oid, false)) "
                " FILTER (WHERE constraint_row.contype='p'), "
                "(SELECT index_row.indisvalid FROM pg_index index_row "
                " WHERE index_row.indexrelid=(SELECT primary_row.conindid "
                " FROM pg_constraint primary_row "
                " WHERE primary_row.conrelid='factory_artifact.build_identity_claim'::regclass "
                " AND primary_row.contype='p')), "
                "(SELECT index_row.indisready FROM pg_index index_row "
                " WHERE index_row.indexrelid=(SELECT primary_row.conindid "
                " FROM pg_constraint primary_row "
                " WHERE primary_row.conrelid='factory_artifact.build_identity_claim'::regclass "
                " AND primary_row.contype='p')) "
                "FROM pg_constraint constraint_row "
                "WHERE constraint_row.conrelid='factory_artifact.build_identity_claim'::regclass"
            )
            if cursor.fetchone() != (
                10, True, True, 1, "PRIMARY KEY (build_id)", True, True,
            ):
                raise ArtifactBuildError(
                    "build identity registry constraints or primary index are incomplete"
                )

            cursor.execute(
                "SELECT trigger.tgname, trigger.tgtype, trigger.tgenabled, "
                "format('%I.%I(%s)', function_namespace.nspname, procedure.proname, "
                "pg_get_function_identity_arguments(procedure.oid)) "
                "FROM pg_trigger trigger "
                "JOIN pg_class relation ON relation.oid=trigger.tgrelid "
                "JOIN pg_proc procedure ON procedure.oid=trigger.tgfoid "
                "JOIN pg_namespace function_namespace ON function_namespace.oid=procedure.pronamespace "
                "WHERE relation.oid='factory_artifact.build_identity_claim'::regclass "
                "AND NOT trigger.tgisinternal ORDER BY trigger.tgname"
            )
            if cursor.fetchall() != [
                (
                    "reject_build_identity_truncate", 34, "O",
                    "factory_artifact.reject_build_identity_mutation()",
                ),
                (
                    "reject_build_identity_update_delete", 27, "O",
                    "factory_artifact.reject_build_identity_mutation()",
                ),
            ]:
                raise ArtifactBuildError("build identity registry mutation triggers are incomplete")

            cursor.execute(
                "SELECT factory_artifact.claim_build_identity(%s,%s,%s,%s,%s,%s,%s,%s,%s)",
                (
                    claim["build_id"], claim_sha256, claim["request_sha256"],
                    claim["decision_sha256"], claim["artifact_sha256"],
                    claim["source_tree_sha256"], claim["module_id"],
                    claim["module_version"], claim["integration_commit"],
                ),
            )
            row = cursor.fetchone()
            if row is None or len(row) != 1 or row[0] is not True:
                raise ArtifactBuildError("build_id is already claimed by different validated inputs")
            # The claim must be committed before artifact publication.  A lost
            # acknowledgement fails this attempt closed; an exact retry can
            # safely observe and reuse the committed identity.
            connection.commit()
        except ArtifactBuildError:
            if connection is not None:
                try:
                    connection.rollback()
                except Exception:
                    pass
            raise
        except Exception as exc:
            if connection is not None:
                try:
                    connection.rollback()
                except Exception:
                    pass
            raise ArtifactBuildError("durable build identity claim failed") from exc
        finally:
            if cursor is not None:
                try:
                    cursor.close()
                except Exception:
                    pass
            if connection is not None:
                try:
                    connection.close()
                except Exception:
                    pass


def _parse_rfc3339(value: Any, *, label: str) -> datetime:
    if not isinstance(value, str):
        raise ArtifactBuildError(f"{label} must be an RFC 3339 timestamp")
    match = _RFC3339_TIMESTAMP.fullmatch(value)
    if match is None:
        raise ArtifactBuildError(f"{label} must be an RFC 3339 timestamp")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00" if value.endswith("Z") else value)
    except ValueError as exc:
        raise ArtifactBuildError(f"{label} is not a valid calendar timestamp") from exc
    if parsed.utcoffset() is None:
        raise ArtifactBuildError(f"{label} must include a UTC offset")
    return parsed


def _validate_optional_identity(value: Any, pattern: re.Pattern[str], *, label: str) -> None:
    if value is not None and (not isinstance(value, str) or pattern.fullmatch(value) is None):
        raise ArtifactBuildError(f"invalid {label}")


def _validate_string_array(
    value: Any,
    *,
    label: str,
    minimum_items: int,
    maximum_items: int,
    item_pattern: re.Pattern[str] | None = None,
) -> None:
    if (
        type(value) is not list
        or len(value) < minimum_items
        or len(value) > maximum_items
        or any(
            not isinstance(item, str)
            or not item
            or len(item.encode("utf-8")) > 512
            or "\x00" in item
            or (item_pattern is not None and item_pattern.fullmatch(item) is None)
            for item in value
        )
        or len(set(value)) != len(value)
    ):
        raise ArtifactBuildError(f"merge decision {label} is invalid")


def _validate_merge_decision(decision: Mapping[str, Any]) -> None:
    if set(decision) != _DECISION_FIELDS:
        raise ArtifactBuildError("merge decision has unknown or missing fields")
    if (
        decision.get("schema_version") != "1.0.0"
        or decision.get("contract_id") != "merge.decision/v1"
        or decision.get("producer_module") != "factory-merge-controller"
        or decision.get("privacy_class") != "internal"
        or decision.get("verification_scope") != "MERGE_HEAD_ONLY"
    ):
        raise ArtifactBuildError("merge decision contract identity is invalid")
    _validate_optional_identity(decision.get("soul_id"), _SOUL_ID, label="merge decision soul_id")
    _validate_optional_identity(
        decision.get("device_binding_id"),
        _DEVICE_BINDING_ID,
        label="merge decision device_binding_id",
    )
    _validate_optional_identity(
        decision.get("platform_account_id"),
        _PLATFORM_ACCOUNT_ID,
        label="merge decision platform_account_id",
    )
    if not isinstance(decision.get("trace_id"), str) or not _TRACE_ID.fullmatch(decision["trace_id"]):
        raise ArtifactBuildError("merge decision trace_id is invalid")
    if (
        not isinstance(decision.get("idempotency_key"), str)
        or not _IDEMPOTENCY_KEY.fullmatch(decision["idempotency_key"])
    ):
        raise ArtifactBuildError("merge decision idempotency_key is invalid")
    _parse_rfc3339(decision.get("occurred_at"), label="merge decision occurred_at")
    if not isinstance(decision.get("decision_id"), str) or not _MERGE_ID.fullmatch(decision["decision_id"]):
        raise ArtifactBuildError("merge decision decision_id is invalid")
    if not isinstance(decision.get("merge_request_id"), str) or not _BUILD_ID.fullmatch(
        decision["merge_request_id"]
    ):
        raise ArtifactBuildError("merge decision merge_request_id is invalid")
    if (
        not isinstance(decision.get("integration_commit"), str)
        or not _COMMIT.fullmatch(decision["integration_commit"])
    ):
        raise ArtifactBuildError("merge decision integration_commit is invalid")
    if decision.get("outcome") not in {"APPROVED", "REJECTED"}:
        raise ArtifactBuildError("merge decision outcome is invalid")
    _validate_string_array(decision.get("reasons"), label="reasons", minimum_items=0, maximum_items=128)
    _validate_string_array(
        decision.get("evidence_ids"),
        label="evidence_ids",
        minimum_items=1 if decision.get("outcome") == "APPROVED" else 0,
        maximum_items=1024,
        item_pattern=_BUILD_ID,
    )
    if decision.get("outcome") == "APPROVED" and decision.get("reasons"):
        raise ArtifactBuildError("approved merge decision must not contain rejection reasons")
    if not isinstance(decision.get("decided_by"), str) or not _ACTOR_ID.fullmatch(decision["decided_by"]):
        raise ArtifactBuildError("merge decision decided_by is invalid")
    for field in ("trusted_policy_sha256", "runner_attestation_sha256"):
        if not isinstance(decision.get(field), str) or not _SHA256.fullmatch(decision[field]):
            raise ArtifactBuildError(f"merge decision {field} is invalid")


def _require_secure_open_support() -> None:
    if _MISSING_OPEN_FLAGS or os.open not in os.supports_dir_fd:
        missing = ", ".join(_MISSING_OPEN_FLAGS) if _MISSING_OPEN_FLAGS else "dir_fd"
        raise ArtifactBuildError(f"secure descriptor-relative filesystem access is unavailable: {missing}")


def _directory_open_flags() -> int:
    _require_secure_open_support()
    return os.O_RDONLY | os.O_CLOEXEC | os.O_DIRECTORY | os.O_NOFOLLOW


def _file_open_flags(access: int) -> int:
    _require_secure_open_support()
    return access | os.O_CLOEXEC | os.O_NOFOLLOW


def _path_parts(path: str | os.PathLike[str], *, label: str) -> tuple[bool, tuple[str, ...]]:
    raw = os.fspath(path)
    if not isinstance(raw, str) or not raw or "\x00" in raw or "\\" in raw:
        raise ArtifactBuildError(f"{label} must be a canonical POSIX path")
    if len(os.fsencode(raw)) > _MAX_REPOSITORY_PATH_BYTES:
        raise ArtifactBuildError(f"{label} exceeds the path length limit")
    if raw in (".", "/"):
        return raw == "/", ()
    absolute = raw.startswith("/")
    components = raw.split("/")[1:] if absolute else raw.split("/")
    if any(not part or part in (".", "..") for part in components):
        raise ArtifactBuildError(f"{label} must not contain empty, dot, or parent components")
    return absolute, tuple(components)


def _relative_parts(path: PurePosixPath, *, label: str) -> tuple[str, ...]:
    raw = path.as_posix()
    absolute, parts = _path_parts(raw, label=label)
    if absolute or not parts:
        raise ArtifactBuildError(f"{label} must be a non-empty repository-relative path")
    return parts


def _fsync_directory(directory_fd: int) -> None:
    try:
        os.fsync(directory_fd)
    except OSError as exc:
        raise ArtifactBuildError("artifact output directory could not be made durable") from exc


def _walk_directory(anchor_fd: int, parts: tuple[str, ...], *, create: bool) -> int:
    current = os.dup(anchor_fd)
    try:
        for part in parts:
            created = False
            if create:
                try:
                    os.mkdir(part, 0o750, dir_fd=current)
                    created = True
                except FileExistsError:
                    pass
                except OSError as exc:
                    raise ArtifactBuildError("output directory could not be created securely") from exc
                if created:
                    _fsync_directory(current)
            try:
                following = os.open(part, _directory_open_flags(), dir_fd=current)
            except OSError as exc:
                raise ArtifactBuildError("directory path contains a missing or unsafe component") from exc
            os.close(current)
            current = following
        return current
    except BaseException:
        os.close(current)
        raise


class _SecureDirectory:
    """Pin a directory and retain an anchor for namespace-drift checks."""

    def __init__(self, path: str | os.PathLike[str], *, create: bool, label: str) -> None:
        absolute, self._parts = _path_parts(path, label=label)
        self._anchor_fd = os.open("/" if absolute else ".", _directory_open_flags())
        self._fd: int | None = None
        try:
            self._fd = _walk_directory(self._anchor_fd, self._parts, create=create)
            information = os.fstat(self._fd)
            if not stat.S_ISDIR(information.st_mode):
                raise ArtifactBuildError(f"{label} must be a directory")
            self._identity = (information.st_dev, information.st_ino)
        except BaseException:
            if self._fd is not None:
                os.close(self._fd)
            os.close(self._anchor_fd)
            raise
        self._closed = False

    @property
    def fd(self) -> int:
        if self._closed:
            raise ArtifactBuildError("secure directory handle is closed")
        assert self._fd is not None
        return self._fd

    def assert_path_identity(self) -> None:
        if self._closed:
            raise ArtifactBuildError("secure directory handle is closed")
        try:
            current = _walk_directory(self._anchor_fd, self._parts, create=False)
        except ArtifactBuildError as exc:
            raise ArtifactBuildError("directory namespace changed during artifact build") from exc
        try:
            information = os.fstat(current)
            if (information.st_dev, information.st_ino) != self._identity:
                raise ArtifactBuildError("directory namespace changed during artifact build")
        finally:
            os.close(current)

    def close(self) -> None:
        if not self._closed:
            assert self._fd is not None
            os.close(self._fd)
            os.close(self._anchor_fd)
            self._closed = True

    def __enter__(self) -> _SecureDirectory:
        return self

    def __exit__(self, exc_type: object, exc: object, traceback: object) -> None:
        self.close()


def _mgf1(seed: bytes, length: int) -> bytes:
    output = bytearray()
    counter = 0
    while len(output) < length:
        output.extend(hashlib.sha256(seed + counter.to_bytes(4, "big")).digest())
        counter += 1
    return bytes(output[:length])


def _verify_rsa_pss(message: bytes, signature: bytes, modulus: int, exponent: int) -> bool:
    if modulus.bit_length() < 1024 or exponent < 3 or exponent % 2 == 0:
        return False
    em_bits = modulus.bit_length() - 1
    em_length = (em_bits + 7) // 8
    if len(signature) != (modulus.bit_length() + 7) // 8:
        return False
    encoded = pow(int.from_bytes(signature, "big"), exponent, modulus).to_bytes(em_length, "big")
    digest_length = hashlib.sha256().digest_size
    salt_length = digest_length
    if em_length < digest_length + salt_length + 2 or encoded[-1] != 0xBC:
        return False
    masked_db = encoded[: em_length - digest_length - 1]
    encoded_hash = encoded[em_length - digest_length - 1 : -1]
    unused_bits = 8 * em_length - em_bits
    if unused_bits and masked_db[0] >> (8 - unused_bits):
        return False
    mask = _mgf1(encoded_hash, len(masked_db))
    data_block = bytearray(left ^ right for left, right in zip(masked_db, mask))
    if unused_bits:
        data_block[0] &= 0xFF >> unused_bits
    padding_length = em_length - digest_length - salt_length - 2
    if data_block[:padding_length] != b"\x00" * padding_length or data_block[padding_length] != 0x01:
        return False
    salt = bytes(data_block[-salt_length:])
    expected = hashlib.sha256(b"\x00" * 8 + hashlib.sha256(message).digest() + salt).digest()
    return hmac.compare_digest(encoded_hash, expected)


class MergeDecisionTrustStore:
    """Verify signed decisions loaded from the external immutable merge ledger."""

    def __init__(self, keys: Mapping[str, Mapping[str, Any]]) -> None:
        if not keys:
            raise ValueError("at least one merge decision key is required")
        normalized: dict[str, tuple[str, int, int]] = {}
        for key_id, record in keys.items():
            if set(record) != {"identity", "algorithm", "modulus_hex", "exponent"}:
                raise ValueError(f"invalid merge trust-store record for {key_id}")
            if record["algorithm"] != "rsa-pss-sha256":
                raise ValueError("only rsa-pss-sha256 is supported")
            try:
                modulus = int(str(record["modulus_hex"]), 16)
                exponent = int(record["exponent"])
            except (TypeError, ValueError) as exc:
                raise ValueError("invalid RSA public key") from exc
            identity = record["identity"]
            if not isinstance(key_id, str) or not key_id or not isinstance(identity, str) or not identity:
                raise ValueError("key identity is required")
            if modulus.bit_length() < 1024:
                raise ValueError("trusted RSA modulus must be at least 1024 bits")
            normalized[key_id] = (identity, modulus, exponent)
        self._keys = normalized

    def verify(self, envelope: Mapping[str, Any]) -> Mapping[str, Any]:
        if type(envelope) is not dict or set(envelope) != {"decision", "attestation"}:
            raise ArtifactBuildError("merge ledger record has unknown or missing fields")
        decision = envelope.get("decision")
        attestation = envelope.get("attestation")
        if type(decision) is not dict or type(attestation) is not dict:
            raise ArtifactBuildError("merge ledger record must contain decision and attestation objects")
        # Freeze loader-controlled objects before validation.  The signature,
        # authorization checks, and build claim must all observe one plain JSON
        # value rather than a mutable/custom Mapping with time-varying reads.
        try:
            decision = json.loads(canonical_bytes(decision))
            attestation = json.loads(canonical_bytes(attestation))
        except (TypeError, ValueError) as exc:
            raise ArtifactBuildError("merge ledger record is not canonical JSON") from exc
        _validate_merge_decision(decision)
        if set(attestation) != _ATTESTATION_FIELDS or attestation.get("algorithm") != "rsa-pss-sha256":
            raise ArtifactBuildError("merge decision attestation is invalid")
        if (
            not isinstance(attestation.get("key_id"), str)
            or not _ACTOR_ID.fullmatch(attestation["key_id"])
            or not isinstance(attestation.get("signer_identity"), str)
            or not _ACTOR_ID.fullmatch(attestation["signer_identity"])
            or not isinstance(attestation.get("payload_sha256"), str)
            or not _SHA256.fullmatch(attestation["payload_sha256"])
            or not isinstance(attestation.get("signature_value"), str)
        ):
            raise ArtifactBuildError("merge decision attestation is invalid")
        trusted = self._keys.get(attestation.get("key_id"))
        if trusted is None:
            raise ArtifactBuildError("merge decision key is not trusted")
        identity, modulus, exponent = trusted
        if attestation.get("signer_identity") != identity or decision.get("decided_by") != identity:
            raise ArtifactBuildError("merge decision identity does not match trust store")
        message = b"dps-merge-decision/v1\n" + canonical_bytes(dict(decision))
        if attestation.get("payload_sha256") != _sha256_bytes(message):
            raise ArtifactBuildError("merge decision attestation digest mismatch")
        try:
            signature = base64.b64decode(str(attestation.get("signature_value")), validate=True)
        except (binascii.Error, ValueError) as exc:
            raise ArtifactBuildError("merge decision signature is not valid base64") from exc
        if not _verify_rsa_pss(message, signature, modulus, exponent):
            raise ArtifactBuildError("merge decision signature verification failed")
        return decision


def _open_parent_at(directory_fd: int, parts: tuple[str, ...]) -> tuple[int, str]:
    if not parts:
        raise ArtifactBuildError("file path must include a file name")
    parent = os.dup(directory_fd)
    try:
        for part in parts[:-1]:
            following = os.open(part, _directory_open_flags(), dir_fd=parent)
            os.close(parent)
            parent = following
        return parent, parts[-1]
    except OSError as exc:
        os.close(parent)
        raise ArtifactBuildError("file path contains a missing or unsafe parent") from exc
    except BaseException:
        os.close(parent)
        raise


def _stat_identity(information: os.stat_result) -> tuple[int, int, int, int, int, int]:
    return (
        information.st_dev,
        information.st_ino,
        information.st_mode,
        information.st_size,
        information.st_mtime_ns,
        information.st_ctime_ns,
    )


def _read_open_regular(
    file_fd: int,
    *,
    max_bytes: int,
    label: str,
    require_immutable_output: bool = False,
) -> tuple[bytes, os.stat_result]:
    try:
        before = os.fstat(file_fd)
        if not stat.S_ISREG(before.st_mode):
            raise ArtifactBuildError(f"{label} must be a regular file")
        if before.st_size < 0 or before.st_size > max_bytes:
            raise ArtifactBuildError(f"{label} exceeds the byte limit")
        if require_immutable_output:
            if before.st_nlink != 1 or before.st_mode & 0o222:
                raise ArtifactBuildError("immutable output has unsafe link count or writable mode")
        chunks: list[bytes] = []
        total = 0
        while True:
            chunk = os.read(file_fd, min(_READ_CHUNK_BYTES, max_bytes - total + 1))
            if not chunk:
                break
            total += len(chunk)
            if total > max_bytes:
                raise ArtifactBuildError(f"{label} exceeds the byte limit")
            chunks.append(chunk)
        after = os.fstat(file_fd)
    except ArtifactBuildError:
        raise
    except OSError as exc:
        raise ArtifactBuildError(f"{label} cannot be read securely") from exc
    if _stat_identity(before) != _stat_identity(after) or total != before.st_size:
        raise ArtifactBuildError(f"{label} changed while it was being read")
    return b"".join(chunks), after


def _open_regular_at(directory_fd: int, parts: tuple[str, ...]) -> tuple[int, int]:
    parent_fd, name = _open_parent_at(directory_fd, parts)
    try:
        file_fd = os.open(name, _file_open_flags(os.O_RDONLY), dir_fd=parent_fd)
        return parent_fd, file_fd
    except OSError as exc:
        os.close(parent_fd)
        raise ArtifactBuildError("regular file cannot be opened without following links") from exc


def _read_regular_at(
    directory_fd: int,
    relative: PurePosixPath,
    *,
    max_bytes: int,
    label: str,
    require_immutable_output: bool = False,
) -> bytes:
    parts = _relative_parts(relative, label=label)
    parent_fd, file_fd = _open_regular_at(directory_fd, parts)
    try:
        data, information = _read_open_regular(
            file_fd,
            max_bytes=max_bytes,
            label=label,
            require_immutable_output=require_immutable_output,
        )
    finally:
        os.close(file_fd)
        os.close(parent_fd)

    # Reopen through the anchored namespace.  This detects parent replacement
    # and final-name substitution that happened after the descriptor was first
    # opened; the bytes themselves always came from the pinned file descriptor.
    verification_parent, verification_fd = _open_regular_at(directory_fd, parts)
    try:
        verified = os.fstat(verification_fd)
        if _stat_identity(verified) != _stat_identity(information):
            raise ArtifactBuildError(f"{label} namespace changed while it was being read")
    finally:
        os.close(verification_fd)
        os.close(verification_parent)
    return data


def _read_stable_regular(path: Path, *, max_bytes: int = _MAX_ARTIFACT_BYTES) -> bytes:
    """Compatibility wrapper using the same descriptor-relative trusted read."""

    if not path.name:
        raise ArtifactBuildError("regular file path must include a file name")
    with _SecureDirectory(path.parent, create=False, label="regular file parent") as parent:
        data = _read_regular_at(
            parent.fd,
            PurePosixPath(path.name),
            max_bytes=max_bytes,
            label="regular file",
        )
        parent.assert_path_identity()
        return data


def _validate_output_name(name: str) -> None:
    if (
        not isinstance(name, str)
        or not name
        or name in (".", "..")
        or "/" in name
        or "\\" in name
        or "\x00" in name
        or len(os.fsencode(name)) > 255
    ):
        raise ArtifactBuildError("immutable output name is invalid")


def _read_output_at(directory_fd: int, name: str, *, max_bytes: int) -> bytes:
    return _read_regular_at(
        directory_fd,
        PurePosixPath(name),
        max_bytes=max_bytes,
        label="immutable output",
        require_immutable_output=True,
    )


def _discard_temp(directory_fd: int, name: str) -> None:
    try:
        os.unlink(name, dir_fd=directory_fd)
    except FileNotFoundError:
        return
    except OSError as exc:
        raise ArtifactBuildError("temporary artifact output could not be removed") from exc


@contextmanager
def _output_lock(directory_fd: int):
    lock_name = ".dps-artifact-builder.lock"
    lock_fd: int | None = None
    created = False
    last_error: OSError | None = None
    for _ in range(8):
        try:
            lock_fd = os.open(
                lock_name,
                _file_open_flags(os.O_RDWR | os.O_CREAT | os.O_EXCL),
                0o600,
                dir_fd=directory_fd,
            )
            created = True
            break
        except FileExistsError:
            try:
                lock_fd = os.open(
                    lock_name,
                    _file_open_flags(os.O_RDWR),
                    dir_fd=directory_fd,
                )
                break
            except FileNotFoundError as exc:
                last_error = exc
                continue
            except OSError as exc:
                raise ArtifactBuildError("artifact output lock cannot be opened securely") from exc
        except OSError as exc:
            raise ArtifactBuildError("artifact output lock cannot be opened securely") from exc
    if lock_fd is None:
        raise ArtifactBuildError("artifact output lock namespace did not stabilize") from last_error
    try:
        information = os.fstat(lock_fd)
        if not stat.S_ISREG(information.st_mode) or information.st_nlink != 1:
            raise ArtifactBuildError("artifact output lock is not a private regular file")
        os.fchmod(lock_fd, 0o600)
        os.fsync(lock_fd)
        if created:
            _fsync_directory(directory_fd)
        fcntl.flock(lock_fd, fcntl.LOCK_EX)
        visible = os.stat(lock_name, dir_fd=directory_fd, follow_symlinks=False)
        identity = (information.st_dev, information.st_ino)
        if (visible.st_dev, visible.st_ino) != identity:
            raise ArtifactBuildError("artifact output lock namespace changed")
        yield
        visible = os.stat(lock_name, dir_fd=directory_fd, follow_symlinks=False)
        if (visible.st_dev, visible.st_ino) != identity:
            raise ArtifactBuildError("artifact output lock namespace changed")
    except ArtifactBuildError:
        raise
    except OSError as exc:
        raise ArtifactBuildError("artifact output lock failed") from exc
    finally:
        try:
            fcntl.flock(lock_fd, fcntl.LOCK_UN)
        finally:
            os.close(lock_fd)


def _recover_stale_temp(directory_fd: int, temporary_name: str, final_name: str) -> None:
    try:
        temporary = os.stat(temporary_name, dir_fd=directory_fd, follow_symlinks=False)
    except FileNotFoundError:
        return
    except OSError as exc:
        raise ArtifactBuildError("stale artifact temporary cannot be inspected") from exc
    try:
        final = os.stat(final_name, dir_fd=directory_fd, follow_symlinks=False)
    except FileNotFoundError:
        final = None
    except OSError as exc:
        raise ArtifactBuildError("artifact final name cannot be inspected during recovery") from exc
    # Removing this module-owned deterministic temporary name is safe even when
    # an attacker replaced it with a symlink or hardlink: unlink never follows
    # the directory entry.  If link() completed before a crash, this also drops
    # the second link so the final object returns to nlink == 1.
    if final is not None and (
        temporary.st_dev,
        temporary.st_ino,
    ) == (
        final.st_dev,
        final.st_ino,
    ) and not stat.S_ISREG(final.st_mode):
        raise ArtifactBuildError("crash-recovered artifact final is not a regular file")
    _discard_temp(directory_fd, temporary_name)
    _fsync_directory(directory_fd)


def _write_immutable_locked(directory_fd: int, name: str, payload: bytes, *, max_bytes: int) -> str:
    digest = _sha256_bytes(payload)
    temporary_name = f"._dps_tmp_{hashlib.sha256(name.encode('utf-8')).hexdigest()[:40]}"
    _recover_stale_temp(directory_fd, temporary_name, name)

    try:
        existing = _read_output_at(directory_fd, name, max_bytes=max_bytes)
    except ArtifactBuildError as exc:
        cause = exc.__cause__
        if not isinstance(cause, OSError) or cause.errno != errno.ENOENT:
            raise
    else:
        if existing != payload:
            raise ArtifactBuildError("immutable output already exists with different content")
        return digest

    temporary_fd: int | None = None
    linked = False
    try:
        temporary_fd = os.open(
            temporary_name,
            _file_open_flags(os.O_RDWR | os.O_CREAT | os.O_EXCL),
            0o600,
            dir_fd=directory_fd,
        )
        offset = 0
        payload_view = memoryview(payload)
        while offset < len(payload):
            written = os.write(temporary_fd, payload_view[offset:])
            if written <= 0:
                raise ArtifactBuildError("immutable output write made no progress")
            offset += written
        os.fsync(temporary_fd)
        os.lseek(temporary_fd, 0, os.SEEK_SET)
        reread, _ = _read_open_regular(
            temporary_fd,
            max_bytes=max_bytes,
            label="temporary immutable output",
        )
        if reread != payload:
            raise ArtifactBuildError("immutable output read-back digest drift")
        os.fchmod(temporary_fd, 0o440)
        os.fsync(temporary_fd)
        try:
            os.link(
                temporary_name,
                name,
                src_dir_fd=directory_fd,
                dst_dir_fd=directory_fd,
                follow_symlinks=False,
            )
            linked = True
        except FileExistsError:
            existing = _read_output_at(directory_fd, name, max_bytes=max_bytes)
            if existing != payload:
                raise ArtifactBuildError("immutable output was created concurrently with different content")
        finally:
            os.close(temporary_fd)
            temporary_fd = None
            _discard_temp(directory_fd, temporary_name)
        _fsync_directory(directory_fd)
        published = _read_output_at(directory_fd, name, max_bytes=max_bytes)
        if published != payload:
            raise ArtifactBuildError("published immutable output digest drift")
    except ArtifactBuildError:
        raise
    except OSError as exc:
        raise ArtifactBuildError("immutable output could not be published securely") from exc
    finally:
        if temporary_fd is not None:
            os.close(temporary_fd)
        try:
            _discard_temp(directory_fd, temporary_name)
        except ArtifactBuildError:
            if linked:
                raise
    return digest


def _write_immutable_at(directory_fd: int, name: str, payload: bytes, *, max_bytes: int) -> str:
    _validate_output_name(name)
    if len(payload) > max_bytes:
        raise ArtifactBuildError("immutable output exceeds the byte limit")
    with _output_lock(directory_fd):
        return _write_immutable_locked(directory_fd, name, payload, max_bytes=max_bytes)


def _write_json_at(directory_fd: int, name: str, value: Mapping[str, Any]) -> str:
    payload = canonical_bytes(value) + b"\n"
    return _write_immutable_at(directory_fd, name, payload, max_bytes=_MAX_METADATA_BYTES)


def _write_json_locked(directory_fd: int, name: str, value: Mapping[str, Any]) -> str:
    payload = canonical_bytes(value) + b"\n"
    _validate_output_name(name)
    if len(payload) > _MAX_METADATA_BYTES:
        raise ArtifactBuildError("immutable output exceeds the byte limit")
    return _write_immutable_locked(directory_fd, name, payload, max_bytes=_MAX_METADATA_BYTES)


class GitSourceTree:
    """Read exact source bytes from a commit with fixed argv and shell disabled."""

    def __init__(self, repository_root: Path) -> None:
        executable = shutil.which("git")
        if executable is None:
            raise ArtifactBuildError("git executable is required")
        self._git = executable
        self._root = repository_root

    def _run(self, arguments: list[str], *, max_stdout_bytes: int) -> bytes:
        if max_stdout_bytes < 0:
            raise ArtifactBuildError("trusted Git stdout limit is invalid")
        environment = {
            "PATH": os.path.dirname(self._git),
            "LC_ALL": "C",
            "LANG": "C",
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_CONFIG_SYSTEM": os.devnull,
            "GIT_ATTR_NOSYSTEM": "1",
            "GIT_OPTIONAL_LOCKS": "0",
            "GIT_TERMINAL_PROMPT": "0",
        }
        process = subprocess.Popen(
            [self._git, "-C", str(self._root), *arguments],
            shell=False,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=environment,
            start_new_session=True,
        )
        assert process.stdout is not None and process.stderr is not None
        stdout_fd = process.stdout.fileno()
        stderr_fd = process.stderr.fileno()
        selector = selectors.DefaultSelector()
        streams = {
            stdout_fd: ("stdout", bytearray(), max_stdout_bytes),
            stderr_fd: ("stderr", bytearray(), _MAX_GIT_STDERR_BYTES),
        }
        for descriptor in streams:
            os.set_blocking(descriptor, False)
            selector.register(descriptor, selectors.EVENT_READ)
        deadline = time.monotonic() + 30
        try:
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise ArtifactBuildError("trusted Git tree read timed out")
                events = selector.select(min(remaining, 0.25))
                if not events and process.poll() is not None:
                    # A closed pipe becomes readable; loop once more rather than
                    # trusting process completion as proof that buffers are empty.
                    events = selector.select(0)
                for key, _ in events:
                    name, buffer, limit = streams[key.fd]
                    try:
                        chunk = os.read(key.fd, min(_READ_CHUNK_BYTES, limit - len(buffer) + 1))
                    except BlockingIOError:
                        continue
                    if not chunk:
                        selector.unregister(key.fd)
                        continue
                    buffer.extend(chunk)
                    if len(buffer) > limit:
                        raise ArtifactBuildError(f"trusted Git {name} exceeded its byte limit")
            return_code = process.wait(timeout=max(0.1, deadline - time.monotonic()))
        except (ArtifactBuildError, subprocess.TimeoutExpired):
            if process.poll() is None:
                process.kill()
            process.wait(timeout=5)
            raise ArtifactBuildError("trusted Git tree read failed or exceeded a resource limit")
        finally:
            selector.close()
            process.stdout.close()
            process.stderr.close()
        if return_code != 0:
            raise ArtifactBuildError("trusted Git tree read failed")
        return bytes(streams[stdout_fd][1])

    def inventory(self, commit: str, owned_root: str) -> list[dict[str, Any]]:
        if not _COMMIT.fullmatch(commit):
            raise ArtifactBuildError("invalid integration commit")
        if not owned_root or PurePosixPath(owned_root).is_absolute() or ".." in PurePosixPath(owned_root).parts:
            raise ArtifactBuildError("invalid manifest owned root")
        raw = self._run(
            ["ls-tree", "-r", "-l", "-z", "--full-tree", commit, "--", owned_root],
            max_stdout_bytes=_MAX_GIT_LISTING_BYTES,
        )
        records = [record for record in raw.split(b"\x00") if record]
        if len(records) > _MAX_SOURCE_FILES:
            raise ArtifactBuildError("Git module tree exceeds the file-count limit")
        parsed: list[tuple[str, str, int, str]] = []
        seen_paths: set[str] = set()
        total_bytes = 0
        for record in records:
            if not record:
                continue
            try:
                metadata, path_bytes = record.split(b"\t", 1)
                mode, object_type, object_id, size_text = metadata.decode("ascii").split()
                path = path_bytes.decode("utf-8")
                size_bytes = int(size_text, 10)
            except (ValueError, UnicodeDecodeError) as exc:
                raise ArtifactBuildError("Git returned an invalid tree record") from exc
            if (
                object_type != "blob"
                or mode not in ("100644", "100755")
                or not _GIT_OBJECT.fullmatch(object_id)
                or size_bytes < 0
            ):
                raise ArtifactBuildError("module tree contains unsupported non-blob content")
            pure_path = PurePosixPath(path)
            if (
                len(path.encode("utf-8")) > _MAX_REPOSITORY_PATH_BYTES
                or "\\" in path
                or any(ord(character) < 32 or ord(character) == 127 for character in path)
                or pure_path.is_absolute()
                or "." in pure_path.parts
                or ".." in pure_path.parts
                or not (path == owned_root or path.startswith(owned_root + "/"))
            ):
                raise ArtifactBuildError("Git returned a non-canonical module path")
            if path in seen_paths:
                raise ArtifactBuildError("Git returned a duplicate module path")
            seen_paths.add(path)
            if size_bytes > _MAX_SOURCE_FILE_BYTES:
                raise ArtifactBuildError("Git module tree contains an oversized file")
            total_bytes += size_bytes
            if total_bytes > _MAX_SOURCE_TOTAL_BYTES:
                raise ArtifactBuildError("Git module tree exceeds the total-byte limit")
            parsed.append((mode, object_id, size_bytes, path))

        inventory: list[dict[str, Any]] = []
        for mode, object_id, size_bytes, path in parsed:
            blob = self._run(["cat-file", "blob", object_id], max_stdout_bytes=size_bytes)
            if len(blob) != size_bytes:
                raise ArtifactBuildError("Git blob size differs from the trusted tree listing")
            inventory.append({"path": path, "sha256": _sha256_bytes(blob), "size_bytes": len(blob), "mode": mode})
        if not inventory:
            raise ArtifactBuildError("manifest owned root is absent from integration commit")
        return sorted(inventory, key=lambda item: item["path"])


class _ArtifactBuilderCore:
    """Internal build engine; production callers use the strict ArtifactBuilder facade."""

    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        merge_decision_loader: Callable[[str], Mapping[str, Any]],
        merge_trust_store: MergeDecisionTrustStore,
        allowed_merge_policy_sha256: set[str] | frozenset[str],
        build_identity_registry: BuildIdentityRegistry,
    ) -> None:
        root = Path(repository_root)
        if root.is_symlink() or not root.is_dir():
            raise ValueError("repository_root must be a non-symlink directory")
        self._root = root.resolve(strict=True)
        try:
            with _SecureDirectory(self._root, create=False, label="repository_root") as secure_root:
                information = os.fstat(secure_root.fd)
                self._root_identity = (information.st_dev, information.st_ino)
                secure_root.assert_path_identity()
        except ArtifactBuildError as exc:
            raise ValueError("repository_root must have a stable non-symlink path") from exc
        self._decision_loader = merge_decision_loader
        self._merge_trust_store = merge_trust_store
        if not allowed_merge_policy_sha256 or any(not _SHA256.fullmatch(item) for item in allowed_merge_policy_sha256):
            raise ValueError("allowed merge policy digests are required")
        self._allowed_policy_hashes = frozenset(allowed_merge_policy_sha256)
        if not isinstance(build_identity_registry, BuildIdentityRegistry):
            raise ValueError("a durable build identity registry is required")
        self._build_identity_registry = build_identity_registry
        self._git = GitSourceTree(self._root)

    def _manifest(self, repository_fd: int, module_id: str) -> tuple[Mapping[str, Any], str, list[str], str]:
        manifest_relative = PurePosixPath("Modules") / module_id / "module.yaml"
        try:
            manifest_bytes = _read_regular_at(
                repository_fd,
                manifest_relative,
                max_bytes=_MAX_MANIFEST_BYTES,
                label="module manifest",
            )
            manifest = _strict_json_loads(manifest_bytes)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise ArtifactBuildError("module manifest must be JSON-compatible YAML") from exc
        if not isinstance(manifest, Mapping) or manifest.get("module", {}).get("id") != module_id:
            raise ArtifactBuildError("module manifest identity mismatch")
        paths = manifest.get("paths")
        if not isinstance(paths, Mapping):
            raise ArtifactBuildError("module manifest paths are missing")
        actual_root = paths.get("actualRoot")
        owned = paths.get("owned")
        expected_root = f"Modules/{module_id}"
        if actual_root != expected_root or not isinstance(owned, list) or not owned:
            raise ArtifactBuildError("module manifest ownership is invalid")
        if len(owned) > _MAX_OWNED_PATTERNS or any(
            not isinstance(item, str)
            or len(item.encode("utf-8")) > _MAX_REPOSITORY_PATH_BYTES
            or not (item == expected_root or item.startswith(expected_root + "/"))
            for item in owned
        ):
            raise ArtifactBuildError("module manifest ownership exceeds trusted bounds")
        owned_patterns = list(owned)
        if len(set(owned_patterns)) != len(owned_patterns):
            raise ArtifactBuildError("module manifest contains duplicate ownership patterns")
        return manifest, actual_root, owned_patterns, _sha256_bytes(manifest_bytes)

    def build(
        self,
        request: Mapping[str, Any],
        output_directory: str | os.PathLike[str],
    ) -> dict[str, Any]:
        if type(request) is not dict:
            raise ArtifactBuildError("build request must be a plain JSON object")
        try:
            request = json.loads(canonical_bytes(request))
        except (TypeError, ValueError) as exc:
            raise ArtifactBuildError("build request is not canonical JSON") from exc
        if set(request) != _REQUEST_FIELDS:
            raise ArtifactBuildError("build request has unknown or missing fields")
        if request.get("schema_version") != "1.0.0" or request.get("contract_id") != "artifact.build.request/v1":
            raise ArtifactBuildError("unknown build request contract version")
        if request.get("producer_module") != "factory-release-controller":
            raise ArtifactBuildError("untrusted build request producer")
        if request.get("privacy_class") != "internal":
            raise ArtifactBuildError("invalid privacy_class")
        request_occurred_at = _parse_rfc3339(request.get("occurred_at"), label="build request occurred_at")

        soul_id = request.get("soul_id")
        device_binding_id = request.get("device_binding_id")
        platform_account_id = request.get("platform_account_id")
        trace_id = request.get("trace_id")
        idempotency_key = request.get("idempotency_key")
        if soul_id is not None and (
            not isinstance(soul_id, str) or not _SOUL_ID.fullmatch(soul_id)
        ):
            raise ArtifactBuildError("invalid soul_id")
        if device_binding_id is not None and (
            not isinstance(device_binding_id, str) or not _DEVICE_BINDING_ID.fullmatch(device_binding_id)
        ):
            raise ArtifactBuildError("invalid device_binding_id")
        if platform_account_id is not None and (
            not isinstance(platform_account_id, str) or not _PLATFORM_ACCOUNT_ID.fullmatch(platform_account_id)
        ):
            raise ArtifactBuildError("invalid platform_account_id")
        if not isinstance(trace_id, str) or not _TRACE_ID.fullmatch(trace_id):
            raise ArtifactBuildError("invalid trace_id")
        if not isinstance(idempotency_key, str) or not _IDEMPOTENCY_KEY.fullmatch(idempotency_key):
            raise ArtifactBuildError("invalid idempotency_key")

        module_id = request.get("module_id")
        version = request.get("module_version")
        commit = request.get("integration_commit")
        expected_sha = request.get("expected_sha256")
        decision_id = request.get("merge_decision_id")
        build_id = request.get("build_id")
        if not isinstance(build_id, str) or not _BUILD_ID.fullmatch(build_id):
            raise ArtifactBuildError("invalid build_id")
        if not isinstance(module_id, str) or not _MODULE.fullmatch(module_id):
            raise ArtifactBuildError("invalid module_id")
        if not isinstance(version, str) or not _SEMVER.fullmatch(version):
            raise ArtifactBuildError("invalid module_version")
        if not isinstance(commit, str) or not _COMMIT.fullmatch(commit):
            raise ArtifactBuildError("invalid integration_commit")
        if not isinstance(expected_sha, str) or not _SHA256.fullmatch(expected_sha):
            raise ArtifactBuildError("expected_sha256 is mandatory")
        if not isinstance(decision_id, str) or not _MERGE_ID.fullmatch(decision_id):
            raise ArtifactBuildError("merge_decision_id is required")

        try:
            envelope = self._decision_loader(decision_id)
        except Exception as exc:
            raise ArtifactBuildError("merge decision cannot be loaded from trusted ledger") from exc
        decision = self._merge_trust_store.verify(envelope)
        if (
            decision.get("contract_id") != "merge.decision/v1"
            or decision.get("producer_module") != "factory-merge-controller"
            or decision.get("decision_id") != decision_id
            or decision.get("outcome") != "APPROVED"
            or decision.get("integration_commit") != commit
            or decision.get("trusted_policy_sha256") not in self._allowed_policy_hashes
        ):
            raise ArtifactBuildError("trusted merge ledger decision does not authorize this build")
        for field in ("soul_id", "device_binding_id", "platform_account_id", "trace_id"):
            if decision.get(field) != request.get(field):
                raise ArtifactBuildError(f"trusted merge decision {field} does not match build request")
        decision_occurred_at = _parse_rfc3339(
            decision.get("occurred_at"),
            label="merge decision occurred_at",
        )
        if request_occurred_at < decision_occurred_at:
            raise ArtifactBuildError("build request occurred before its trusted merge decision")

        return self._build_authorized(
            request=request,
            output_directory=output_directory,
            decision=decision,
            module_id=module_id,
            version=version,
            commit=commit,
            expected_sha=expected_sha,
            decision_id=decision_id,
            trace_id=trace_id,
        )

    def _build_authorized(
        self,
        *,
        request: Mapping[str, Any],
        output_directory: str | os.PathLike[str],
        decision: Mapping[str, Any],
        module_id: str,
        version: str,
        commit: str,
        expected_sha: str,
        decision_id: str,
        trace_id: str,
    ) -> dict[str, Any]:
        raw_path = request.get("artifact_path")
        if (
            not isinstance(raw_path, str)
            or not raw_path
            or "\\" in raw_path
            or len(raw_path.encode("utf-8")) > _MAX_REPOSITORY_PATH_BYTES
        ):
            raise ArtifactBuildError("artifact_path must be a canonical repository-relative path")
        relative = PurePosixPath(raw_path)
        if relative.is_absolute() or ".." in relative.parts or "." in relative.parts:
            raise ArtifactBuildError("absolute paths and traversal are forbidden")

        with _SecureDirectory(self._root, create=False, label="repository_root") as repository:
            repository_information = os.fstat(repository.fd)
            if (repository_information.st_dev, repository_information.st_ino) != self._root_identity:
                raise ArtifactBuildError("repository_root identity changed after builder initialization")
            manifest, actual_root, owned_patterns, workspace_manifest_sha = self._manifest(
                repository.fd,
                module_id,
            )
            manifest_module = manifest.get("module")
            if (
                not isinstance(manifest_module, Mapping)
                or manifest_module.get("id") != module_id
                or manifest_module.get("version") != version
            ):
                raise ArtifactBuildError("build request module version differs from module manifest")
            if not any(
                fnmatch.fnmatchcase(relative.as_posix(), pattern)
                or (pattern.endswith("/**") and relative.as_posix() == pattern[:-3])
                for pattern in owned_patterns
            ):
                raise ArtifactBuildError("artifact_path is not owned by the module manifest")
            data = _read_regular_at(
                repository.fd,
                relative,
                max_bytes=_MAX_ARTIFACT_BYTES,
                label="artifact_path",
            )
            artifact_sha = _sha256_bytes(data)
            if expected_sha != artifact_sha:
                raise ArtifactBuildError("artifact digest differs from expected_sha256")

            repository.assert_path_identity()
            source_files = self._git.inventory(commit, actual_root)
            repository.assert_path_identity()
            source_tree_sha = _sha256_bytes(canonical_bytes(source_files))
            by_path = {item["path"]: item for item in source_files}
            if len(by_path) != len(source_files):
                raise ArtifactBuildError("signed integration commit contains duplicate inventory paths")
            artifact_tree_entry = by_path.get(relative.as_posix())
            if artifact_tree_entry is None or artifact_tree_entry["sha256"] != artifact_sha:
                raise ArtifactBuildError("artifact bytes do not match the signed integration commit Git tree")
            manifest_entry = by_path.get(f"{actual_root}/module.yaml")
            agents_entry = by_path.get(f"{actual_root}/AGENTS.md")
            if manifest_entry is None or agents_entry is None:
                raise ArtifactBuildError("integration commit lacks module AGENTS.md or module.yaml")
            if manifest_entry["sha256"] != workspace_manifest_sha:
                raise ArtifactBuildError("workspace module manifest differs from the signed integration commit")

            suffix = relative.suffix if relative.suffix else ".bin"
            if not _ARTIFACT_SUFFIX.fullmatch(suffix):
                raise ArtifactBuildError("artifact file suffix is not safe for immutable publication")
            artifact_name = f"{module_id}-{version}-{artifact_sha}{suffix}"
            _validate_output_name(artifact_name)

            created_at = str(request.get("occurred_at"))
            build_id = str(request.get("build_id"))
            self._build_identity_registry.claim({
                "schema_version": "dps.artifact-build-identity-claim/v1",
                "build_id": build_id,
                "request_sha256": _sha256_bytes(canonical_bytes(dict(request))),
                "decision_sha256": _sha256_bytes(canonical_bytes(dict(decision))),
                "artifact_sha256": artifact_sha,
                "source_tree_sha256": source_tree_sha,
                "module_id": module_id,
                "module_version": version,
                "integration_commit": commit,
            })
            artifact_id = "artifact-" + artifact_sha[:32]
            sbom = {
                "spdxVersion": "SPDX-2.3",
                "dataLicense": "CC0-1.0",
                "SPDXID": "SPDXRef-DOCUMENT",
                "name": artifact_name,
                "documentNamespace": f"https://dps.local/spdx/{artifact_sha}",
                "creationInfo": {"created": created_at, "creators": ["Tool: dps-factory-artifact-builder-0.1.0"]},
                "packages": [{
                    "name": module_id, "SPDXID": "SPDXRef-Package", "versionInfo": version,
                    "downloadLocation": "NOASSERTION", "filesAnalyzed": True,
                    "checksums": [{"algorithm": "SHA256", "checksumValue": artifact_sha}],
                }],
                "files": [
                    {
                        "fileName": item["path"], "SPDXID": f"SPDXRef-File-{index}",
                        "checksums": [{"algorithm": "SHA256", "checksumValue": item["sha256"]}],
                    }
                    for index, item in enumerate(source_files, start=1)
                ],
                "relationships": [{
                    "spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES",
                    "relatedSpdxElement": "SPDXRef-Package",
                }],
            }
            provenance = {
                "_type": "https://in-toto.io/Statement/v1",
                "subject": [{"name": artifact_name, "digest": {"sha256": artifact_sha}}],
                "predicateType": "https://slsa.dev/provenance/v1",
                "predicate": {
                    "buildDefinition": {
                        "buildType": "https://dps.local/build/module-artifact/v1",
                        "externalParameters": {
                            "module_id": module_id, "module_version": version,
                            "integration_commit": commit, "source_tree_sha256": source_tree_sha,
                        },
                        "internalParameters": {"merge_decision_id": decision_id},
                        "resolvedDependencies": source_files,
                    },
                    "runDetails": {
                        "builder": {"id": "dps:factory-artifact-builder:0.1.0"},
                        "metadata": {"invocationId": build_id, "startedOn": created_at, "finishedOn": created_at},
                    },
                },
            }

            sbom_payload = canonical_bytes(sbom) + b"\n"
            provenance_payload = canonical_bytes(provenance) + b"\n"
            sbom_sha = _sha256_bytes(sbom_payload)
            provenance_sha = _sha256_bytes(provenance_payload)
            sbom_name = f"{sbom_sha}.spdx.json"
            provenance_name = f"{provenance_sha}.provenance.json"
            with _SecureDirectory(output_directory, create=True, label="output directory") as output:
                with _output_lock(output.fd):
                    _write_immutable_locked(
                        output.fd,
                        artifact_name,
                        data,
                        max_bytes=_MAX_ARTIFACT_BYTES,
                    )
                    if _write_json_locked(output.fd, sbom_name, sbom) != sbom_sha:
                        raise ArtifactBuildError("SBOM content address changed during publication")
                    if _write_json_locked(output.fd, provenance_name, provenance) != provenance_sha:
                        raise ArtifactBuildError("provenance content address changed during publication")
                    descriptor = {
                        "schema_version": "1.0.0",
                        "contract_id": "artifact.descriptor/v1",
                        "producer_module": "factory-artifact-builder",
                        "soul_id": request.get("soul_id"),
                        "device_binding_id": request.get("device_binding_id"),
                        "platform_account_id": request.get("platform_account_id"),
                        "trace_id": trace_id,
                        "idempotency_key": "idem_" + artifact_sha,
                        "occurred_at": created_at,
                        "privacy_class": "internal",
                        "artifact_id": artifact_id,
                        "build_id": build_id,
                        "module_id": module_id,
                        "module_version": version,
                        "integration_commit": commit,
                        "artifact_uri": f"sha256:{artifact_sha}",
                        "artifact_file": artifact_name,
                        "artifact_sha256": artifact_sha,
                        "size_bytes": len(data),
                        "merge_decision_id": decision_id,
                        "trusted_merge_policy_sha256": decision["trusted_policy_sha256"],
                        "source_tree_sha256": source_tree_sha,
                        "agents_sha256": agents_entry["sha256"],
                        "manifest_sha256": manifest_entry["sha256"],
                        "sbom": {"path": sbom_name, "sha256": sbom_sha, "media_type": "application/json"},
                        "provenance": {"path": provenance_name, "sha256": provenance_sha, "media_type": "application/json"},
                        "signature": {
                            "status": "UNSIGNED_AWAITING_EXTERNAL_SIGNER",
                            "signer_required": "external-controlled-signer",
                        },
                    }
                    descriptor_payload = canonical_bytes(descriptor) + b"\n"
                    descriptor_sha = _sha256_bytes(descriptor_payload)
                    descriptor_name = f"{descriptor_sha}.descriptor.json"
                    # Re-read the artifact after all pre-descriptor metadata was
                    # published.  A successful earlier write is not evidence that
                    # the directory entry still names those bytes now.
                    if _sha256_bytes(
                        _read_output_at(output.fd, artifact_name, max_bytes=_MAX_ARTIFACT_BYTES)
                    ) != artifact_sha:
                        raise ArtifactBuildError("artifact changed before descriptor publication")
                    if _write_json_locked(output.fd, descriptor_name, descriptor) != descriptor_sha:
                        raise ArtifactBuildError("descriptor content address changed during publication")
                    expected_outputs = {
                        artifact_name: data,
                        sbom_name: sbom_payload,
                        provenance_name: provenance_payload,
                        descriptor_name: descriptor_payload,
                    }
                    for output_name, expected_payload in expected_outputs.items():
                        maximum = _MAX_ARTIFACT_BYTES if output_name == artifact_name else _MAX_METADATA_BYTES
                        if _read_output_at(output.fd, output_name, max_bytes=maximum) != expected_payload:
                            raise ArtifactBuildError("immutable output bundle changed before build completion")
                    output.assert_path_identity()
            repository.assert_path_identity()
            return descriptor


class ArtifactBuilder(_ArtifactBuilderCore):
    """Production builder that cannot be composed with an in-memory identity registry."""

    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        merge_decision_loader: Callable[[str], Mapping[str, Any]],
        merge_trust_store: MergeDecisionTrustStore,
        allowed_merge_policy_sha256: set[str] | frozenset[str],
        build_identity_registry: PostgresBuildIdentityRegistry,
    ) -> None:
        if type(build_identity_registry) is not PostgresBuildIdentityRegistry:
            raise ValueError("production ArtifactBuilder requires PostgresBuildIdentityRegistry")
        super().__init__(
            repository_root,
            merge_decision_loader,
            merge_trust_store,
            allowed_merge_policy_sha256,
            build_identity_registry,
        )
