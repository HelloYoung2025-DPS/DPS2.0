"""Declarative worktree planning and externally persisted fenced leases."""

from __future__ import annotations

import copy
import datetime as dt
import fnmatch
import hashlib
import json
import os
import re
import sqlite3
import time
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Callable, Dict, Mapping, Sequence, Set, Tuple


class WorktreeError(ValueError):
    """A declarative plan or lease is unsafe."""


class LeaseConflict(WorktreeError):
    """A requested module, path, or contract is already leased."""


class StaleFence(WorktreeError):
    """A writer no longer owns the current fencing token."""


_MODULE_ID = re.compile(r"^[a-z0-9]+(?:-[a-z0-9]+)*$")
_CONTRACT_ID = re.compile(r"^[a-z][a-z0-9]*(?:\.[a-z0-9]+)+$")
_COMMIT = re.compile(r"^[0-9a-f]{40}$")
_IDENTITIES = {
    "soul_id": re.compile(r"^soul_[a-f0-9]{64}\Z"),
    "device_binding_id": re.compile(r"^db_[a-f0-9]{32}\Z"),
    "platform_account_id": re.compile(r"^pa_[a-f0-9]{32}\Z"),
}
_TRACE_ID = re.compile(r"^trace_[a-f0-9]{32}\Z")
_IDEMPOTENCY_KEY = re.compile(r"^idem_[a-f0-9]{64}\Z")
_ROLE_KEYS = {
    "impact_planner", "contract_architect", "module_implementer",
    "independent_test_agent", "evidence_auditor", "release_approver",
}
_SCHEMA = """
CREATE TABLE IF NOT EXISTS fencing_counters (
    lock_key TEXT PRIMARY KEY,
    last_token INTEGER NOT NULL CHECK (last_token >= 0)
);
CREATE TABLE IF NOT EXISTS active_locks (
    lock_key TEXT PRIMARY KEY,
    lease_id TEXT NOT NULL,
    holder_identity TEXT NOT NULL,
    fencing_token INTEGER NOT NULL CHECK (fencing_token >= 1),
    acquired_at REAL NOT NULL,
    expires_at REAL NOT NULL,
    revoked INTEGER NOT NULL DEFAULT 0 CHECK (revoked IN (0, 1))
);
CREATE INDEX IF NOT EXISTS ix_active_locks_lease_id ON active_locks(lease_id);
CREATE TABLE IF NOT EXISTS lease_records (
    lease_id TEXT PRIMARY KEY,
    plan_id TEXT NOT NULL,
    holder_identity TEXT NOT NULL,
    idempotency_key TEXT NOT NULL UNIQUE CHECK (
        length(idempotency_key) = 69
        AND substr(idempotency_key, 1, 5) = 'idem_'
        AND substr(idempotency_key, 6) NOT GLOB '*[^0-9a-f]*'
    ),
    lock_keys_json TEXT NOT NULL,
    lock_tokens_json TEXT NOT NULL,
    acquired_at REAL NOT NULL,
    expires_at REAL NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ACTIVE', 'REVOKED', 'EXPIRED'))
);
"""


def _canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _utc(epoch: float) -> str:
    return dt.datetime.fromtimestamp(epoch, tz=dt.timezone.utc).isoformat().replace("+00:00", "Z")


def _safe_relative(value: Any) -> str:
    if not isinstance(value, str) or not value or value.startswith("/") or "\\" in value:
        raise WorktreeError("path must be repository-relative POSIX form")
    pure = PurePosixPath(value)
    if pure.as_posix() != value or any(
        part in {"", ".", "..", ".git", ".omo"} or part.startswith(".")
        for part in pure.parts
    ):
        raise WorktreeError("path traversal or hidden state is forbidden")
    return value


def _contained_no_symlink(root: Path, relative_path: str) -> Path:
    normalized = _safe_relative(relative_path)
    candidate = root
    for part in PurePosixPath(normalized).parts:
        candidate = candidate / part
        if candidate.is_symlink():
            raise WorktreeError("symlinked repository path is forbidden: " + normalized)
    resolved_root = root.resolve(strict=True)
    resolved = candidate.resolve(strict=False)
    if resolved != resolved_root and resolved_root not in resolved.parents:
        raise WorktreeError("repository path escapes root: " + normalized)
    return candidate


@dataclass(frozen=True)
class ManifestRecord:
    module_id: str
    actual_root: str
    owned: Tuple[str, ...]
    dependencies: Tuple[str, ...]
    provided_sources: Mapping[str, str]


@dataclass(frozen=True)
class TrustedWriterPolicy:
    digest: str
    roles: Mapping[str, Tuple[str, ...]]

    @classmethod
    def from_verified_document(
        cls,
        document: bytes,
        *,
        expected_sha256: str,
        verifier: Callable[[Mapping[str, Any], str], bool],
    ) -> "TrustedWriterPolicy":
        digest = _sha256(document)
        if digest != expected_sha256:
            raise WorktreeError("trusted writer policy digest mismatch")
        try:
            value = json.loads(document.decode("utf-8"))
        except Exception as exc:
            raise WorktreeError("trusted writer policy is invalid JSON") from exc
        if not isinstance(value, Mapping) or not callable(verifier) or verifier(value, digest) is not True:
            raise WorktreeError("trusted writer policy was not externally verified")
        if value.get("schema_version") != "dps.factory-impact-policy/v1":
            raise WorktreeError("unknown trusted writer policy")
        roles = value.get("roles")
        if not isinstance(roles, Mapping) or set(roles) != _ROLE_KEYS:
            raise WorktreeError("trusted writer roles are invalid")
        normalized: Dict[str, Tuple[str, ...]] = {}
        identities: Set[str] = set()
        for role in sorted(_ROLE_KEYS):
            values = roles.get(role)
            if (
                not isinstance(values, list) or not values
                or any(not isinstance(item, str) or not item for item in values)
                or len(set(values)) != len(values)
                or identities.intersection(values)
            ):
                raise WorktreeError("trusted writer role identities overlap or are invalid")
            identities.update(values)
            normalized[role] = tuple(sorted(values))
        return cls(digest, normalized)


def _load_manifests(root: Path) -> Dict[str, ManifestRecord]:
    records: Dict[str, ManifestRecord] = {}
    contract_owners: Dict[str, str] = {}
    modules_root = root / "Modules"
    if not modules_root.is_dir() or modules_root.is_symlink():
        raise WorktreeError("Modules root is unavailable")
    for module_root in sorted(modules_root.iterdir(), key=lambda item: item.name):
        manifest_path = module_root / "module.yaml"
        if not manifest_path.is_file():
            continue
        if module_root.is_symlink() or manifest_path.is_symlink():
            raise WorktreeError("symlinked module governance is forbidden")
        try:
            manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        except Exception as exc:
            raise WorktreeError("invalid module Manifest") from exc
        module = manifest.get("module") if isinstance(manifest, Mapping) else None
        module_id = module.get("id") if isinstance(module, Mapping) else None
        paths = manifest.get("paths") if isinstance(manifest, Mapping) else None
        actual_root = paths.get("actualRoot") if isinstance(paths, Mapping) else None
        owned = paths.get("owned") if isinstance(paths, Mapping) else None
        if (
            not isinstance(module_id, str) or not _MODULE_ID.fullmatch(module_id)
            or module_id != module_root.name or module_id in records
            or not isinstance(actual_root, str)
            or not isinstance(owned, list) or not owned
            or any(not isinstance(item, str) for item in owned)
        ):
            raise WorktreeError("invalid module identity or ownership")
        _contained_no_symlink(root, actual_root)
        dependencies = []
        raw_dependencies = manifest.get("dependencies")
        if not isinstance(raw_dependencies, list):
            raise WorktreeError("invalid module dependencies")
        for item in raw_dependencies:
            dependency = item.get("moduleId") if isinstance(item, Mapping) else None
            if not isinstance(dependency, str) or not _MODULE_ID.fullmatch(dependency):
                raise WorktreeError("invalid module dependency")
            dependencies.append(dependency)
        contracts = manifest.get("contracts")
        provided = contracts.get("provided") if isinstance(contracts, Mapping) else None
        if not isinstance(provided, list):
            raise WorktreeError("invalid provided contracts")
        provided_sources: Dict[str, str] = {}
        for item in provided:
            contract_id = item.get("contractId") if isinstance(item, Mapping) else None
            source = item.get("source") if isinstance(item, Mapping) else None
            if (
                not isinstance(contract_id, str) or not _CONTRACT_ID.fullmatch(contract_id)
                or not isinstance(source, str)
            ):
                raise WorktreeError("invalid provided contract")
            _contained_no_symlink(root, source)
            previous = contract_owners.get(contract_id)
            if previous is not None and previous != module_id:
                raise WorktreeError("contract has multiple owners")
            contract_owners[contract_id] = module_id
            provided_sources[contract_id] = source
        records[module_id] = ManifestRecord(
            module_id, actual_root, tuple(owned), tuple(sorted(set(dependencies))),
            provided_sources,
        )
    for record in records.values():
        if set(record.dependencies).difference(records):
            raise WorktreeError("unknown module dependency")
    return records


def _owners(path: str, records: Mapping[str, ManifestRecord]) -> Set[str]:
    return {
        module_id
        for module_id, record in records.items()
        if any(fnmatch.fnmatchcase(path, pattern) for pattern in record.owned)
    }


def _expected_waves(nodes: Set[str], edges: Set[Tuple[str, str]]) -> list[list[str]]:
    remaining = set(nodes)
    waves = []
    while remaining:
        wave = sorted(
            node for node in remaining
            if not {provider for consumer, provider in edges if consumer == node}.intersection(remaining)
        )
        if not wave:
            raise WorktreeError("dependency cycle in change plan")
        waves.append(wave)
        remaining.difference_update(wave)
    return waves


class WorktreePlanner:
    """Create a plan only; a separate host adapter may materialize it."""

    def __init__(self, repository_root: str | os.PathLike[str]) -> None:
        self.root = Path(repository_root).resolve(strict=True)

    def create_plan(
        self,
        change_plan: Mapping[str, Any],
        instruction_receipt: Mapping[str, Any],
        trusted_policy: TrustedWriterPolicy,
    ) -> Dict[str, Any]:
        if not isinstance(trusted_policy, TrustedWriterPolicy):
            raise WorktreeError("externally verified trusted writer policy is required")
        if change_plan.get("contract_id") != "module.change.plan/v1" or change_plan.get("producer_module") != "factory-impact-analyzer":
            raise WorktreeError("unknown change plan producer or contract")
        if instruction_receipt.get("contract_id") != "instruction.receipt/v1" or instruction_receipt.get("status") != "BOUND":
            raise WorktreeError("fresh instruction receipt is required")
        if (
            instruction_receipt.get("receipt_id") != change_plan.get("instruction_receipt_id")
            or instruction_receipt.get("baseline_commit") != change_plan.get("baseline_commit")
        ):
            raise WorktreeError("instruction receipt does not match change plan")
        if change_plan.get("trusted_policy_sha256") != trusted_policy.digest:
            raise WorktreeError("change plan trusted policy digest mismatch")
        expected_roles = {key: list(trusted_policy.roles[key]) for key in sorted(_ROLE_KEYS)}
        if change_plan.get("role_assignments") != expected_roles:
            raise WorktreeError("change plan role assignments are not trusted")
        baseline = change_plan.get("baseline_commit")
        if not isinstance(baseline, str) or not _COMMIT.fullmatch(baseline):
            raise WorktreeError("invalid baseline commit")
        for field, pattern in _IDENTITIES.items():
            value = change_plan.get(field)
            if value is not None and (not isinstance(value, str) or not pattern.fullmatch(value)):
                raise WorktreeError("invalid normalized identity: " + field)
        for field, pattern in (("trace_id", _TRACE_ID), ("idempotency_key", _IDEMPOTENCY_KEY)):
            value = change_plan.get(field)
            if not isinstance(value, str) or pattern.fullmatch(value) is None:
                raise WorktreeError("invalid opaque identifier: " + field)

        records = _load_manifests(self.root)
        affected = change_plan.get("affected_modules")
        requested = change_plan.get("requested_paths")
        contract_ids = change_plan.get("public_contract_changes")
        if (
            not isinstance(affected, list) or not affected or len(set(affected)) != len(affected)
            or any(module_id not in records for module_id in affected)
            or not isinstance(requested, list) or not requested or len(set(requested)) != len(requested)
            or not isinstance(contract_ids, list) or len(set(contract_ids)) != len(contract_ids)
        ):
            raise WorktreeError("invalid change-plan scope")
        affected_set = set(affected)
        paths_by_owner: Dict[str, list[str]] = {module_id: [] for module_id in affected}
        normalized_requested = []
        for raw_path in requested:
            path = _safe_relative(raw_path)
            _contained_no_symlink(self.root, path)
            owners = _owners(path, records)
            if len(owners) != 1 or not owners.issubset(affected_set):
                raise WorktreeError("requested path lacks one affected Manifest owner")
            owner = next(iter(owners))
            paths_by_owner[owner].append(path)
            normalized_requested.append(path)

        source_owner: Dict[str, str] = {}
        source_paths: Dict[str, str] = {}
        for module_id, record in records.items():
            for contract_id, source in record.provided_sources.items():
                if contract_id in source_owner:
                    raise WorktreeError("contract source has multiple owners")
                source_owner[contract_id] = module_id
                source_paths[contract_id] = source
        for contract_id in contract_ids:
            if not isinstance(contract_id, str) or contract_id not in source_owner:
                raise WorktreeError("unknown public contract change")
            if source_owner[contract_id] not in affected_set:
                raise WorktreeError("contract owner is absent from affected modules")
            if source_paths[contract_id] not in normalized_requested:
                raise WorktreeError("contract source path is absent from requested paths")

        expected_edges = {
            (consumer, provider)
            for consumer in affected_set
            for provider in records[consumer].dependencies
            if provider in affected_set
        }
        raw_edges = change_plan.get("dependency_edges")
        if not isinstance(raw_edges, list):
            raise WorktreeError("change plan dependency edges are invalid")
        supplied_edges = set()
        for edge in raw_edges:
            if not isinstance(edge, Mapping):
                raise WorktreeError("dependency edge is invalid")
            supplied_edges.add((edge.get("consumer"), edge.get("provider")))
        if supplied_edges != expected_edges:
            raise WorktreeError("change plan dependency graph does not match Manifests")
        waves = _expected_waves(affected_set, expected_edges)
        if change_plan.get("parallel_waves") != waves:
            raise WorktreeError("change plan waves do not match dependency graph")
        implementers = trusted_policy.roles["module_implementer"]
        if any(len(wave) > len(implementers) for wave in waves):
            raise WorktreeError("trusted policy has insufficient independent writers")

        contract_path_set = {source_paths[item] for item in contract_ids}
        entries = []
        for index, module_id in enumerate(sorted(affected_set)):
            module_paths = sorted(
                path for path in paths_by_owner[module_id] if path not in contract_path_set
            )
            if not module_paths:
                actual_root = records[module_id].actual_root
                module_paths = [
                    actual_root + "/src",
                    actual_root + "/tests",
                ]
                for path in module_paths:
                    _contained_no_symlink(self.root, path)
                    if not any(fnmatch.fnmatchcase(path + "/placeholder", pattern) for pattern in records[module_id].owned):
                        raise WorktreeError("fallback module path is outside Manifest ownership")
            writer = implementers[index % len(implementers)]
            material = {
                "module_id": module_id,
                "writer_identity": writer,
                "owned_paths": module_paths,
                "depends_on": sorted(provider for consumer, provider in expected_edges if consumer == module_id),
            }
            entry = dict(material)
            entry["worktree_ref"] = "factory-worktree:%s:%s" % (
                module_id, _sha256(_canonical_bytes(material))[:16]
            )
            entry["lease_keys"] = ["module:" + module_id] + [
                "path:" + path for path in module_paths
            ]
            entries.append(entry)

        contract_worktree = None
        if contract_ids:
            material = {
                "writer_identity": trusted_policy.roles["contract_architect"][0],
                "contract_ids": sorted(contract_ids),
                "owned_paths": sorted(contract_path_set),
            }
            contract_worktree = dict(material)
            contract_worktree["worktree_ref"] = "factory-contract-worktree:" + _sha256(
                _canonical_bytes(material)
            )[:16]
            contract_worktree["lease_keys"] = [
                *["contract:" + item for item in sorted(contract_ids)],
                *["path:" + path for path in sorted(contract_path_set)],
            ]

        body: Dict[str, Any] = {
            "schema_version": "dps.worktree-plan/v1",
            "contract_id": "worktree.plan/v1",
            "producer_module": "factory-worktree-manager",
            "soul_id": change_plan.get("soul_id"),
            "device_binding_id": change_plan.get("device_binding_id"),
            "platform_account_id": change_plan.get("platform_account_id"),
            "trace_id": change_plan.get("trace_id"),
            "idempotency_key": change_plan.get("idempotency_key"),
            "occurred_at": change_plan.get("occurred_at"),
            "privacy_class": "internal",
            "change_plan_id": change_plan.get("plan_id"),
            "instruction_receipt_id": instruction_receipt.get("receipt_id"),
            "baseline_commit": baseline,
            "entries": entries,
            "contract_worktree": contract_worktree,
            "trusted_policy_sha256": trusted_policy.digest,
        }
        plan = copy.deepcopy(body)
        plan["plan_id"] = "worktree:" + _sha256(_canonical_bytes(body))[:32]
        return plan


class ExternalSqliteLeaseStore:
    """Dev-only single-host substitute; never production Factory truth."""

    def __init__(
        self,
        repository_root: str | os.PathLike[str],
        database_path: str | os.PathLike[str],
        *,
        clock: Callable[[], float] = time.time,
    ) -> None:
        self.repository_root = Path(repository_root).resolve(strict=True)
        database = Path(database_path).expanduser()
        if database.is_symlink():
            raise WorktreeError("lease database cannot be a symlink")
        database.parent.mkdir(parents=True, exist_ok=True)
        self.database_path = database.resolve(strict=False)
        if (
            self.database_path == self.repository_root
            or self.repository_root in self.database_path.parents
        ):
            raise WorktreeError("lease database must be external to the repository")
        self.clock = clock
        connection = self._connect()
        try:
            connection.executescript(_SCHEMA)
        finally:
            connection.close()

    def _connect(self) -> sqlite3.Connection:
        connection = sqlite3.connect(str(self.database_path), timeout=5.0, isolation_level=None)
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA foreign_keys = ON")
        connection.execute("PRAGMA journal_mode = WAL")
        return connection

    @staticmethod
    def _validate_lock_keys(lock_keys: Sequence[str]) -> Tuple[str, ...]:
        if (
            not isinstance(lock_keys, Sequence) or isinstance(lock_keys, (str, bytes))
            or not lock_keys or len(set(lock_keys)) != len(lock_keys)
            or any(
                not isinstance(item, str) or len(item) < 3 or len(item) > 512
                or "\x00" in item or "\n" in item or "\r" in item
                for item in lock_keys
            )
        ):
            raise WorktreeError("lock keys are invalid")
        return tuple(sorted(lock_keys))

    @staticmethod
    def _validate_envelope(envelope: Mapping[str, Any]) -> None:
        if not isinstance(envelope, Mapping):
            raise WorktreeError("lease envelope is required")
        for field, pattern in _IDENTITIES.items():
            value = envelope.get(field)
            if value is not None and (not isinstance(value, str) or not pattern.fullmatch(value)):
                raise WorktreeError("invalid normalized identity: " + field)
        for field, pattern in (("trace_id", _TRACE_ID), ("idempotency_key", _IDEMPOTENCY_KEY)):
            if not isinstance(envelope.get(field), str) or pattern.fullmatch(envelope[field]) is None:
                raise WorktreeError("invalid lease envelope field: " + field)

    def _lease_contract(
        self,
        row: Mapping[str, Any],
        envelope: Mapping[str, Any],
    ) -> Dict[str, Any]:
        tokens = json.loads(row["lock_tokens_json"])
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
            "lock_keys": json.loads(row["lock_keys_json"]),
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
        if not isinstance(plan_id, str) or not re.fullmatch(r"worktree:[0-9a-f]{32}", plan_id):
            raise WorktreeError("invalid worktree plan id")
        if not isinstance(holder_identity, str) or not holder_identity:
            raise WorktreeError("holder identity is required")
        if not isinstance(ttl_seconds, int) or not 1 <= ttl_seconds <= 3600:
            raise WorktreeError("lease TTL is outside policy")
        keys = self._validate_lock_keys(lock_keys)
        now = float(self.clock())
        expires = now + ttl_seconds
        idempotency_key = envelope["idempotency_key"]
        lease_id = "lease:" + _sha256(_canonical_bytes({
            "plan_id": plan_id,
            "holder_identity": holder_identity,
            "lock_keys": keys,
            "idempotency_key": idempotency_key,
        }))[:32]
        connection = self._connect()
        try:
            connection.execute("BEGIN IMMEDIATE")
            existing = connection.execute(
                "SELECT * FROM lease_records WHERE idempotency_key = ?",
                (idempotency_key,),
            ).fetchone()
            if existing is not None:
                same = (
                    existing["plan_id"] == plan_id
                    and existing["holder_identity"] == holder_identity
                    and tuple(json.loads(existing["lock_keys_json"])) == keys
                )
                if not same:
                    raise LeaseConflict("idempotency key payload conflict")
                if existing["status"] != "ACTIVE" or existing["expires_at"] <= now:
                    raise StaleFence("idempotent lease is no longer active; use a new key")
                connection.execute("COMMIT")
                return self._lease_contract(existing, envelope)

            for key in keys:
                lock = connection.execute(
                    "SELECT * FROM active_locks WHERE lock_key = ?", (key,)
                ).fetchone()
                if lock is not None and lock["revoked"] == 0 and lock["expires_at"] > now:
                    raise LeaseConflict("active lock conflict: " + key)
                if lock is not None and lock["expires_at"] <= now:
                    connection.execute(
                        "UPDATE lease_records SET status = 'EXPIRED' WHERE lease_id = ? AND status = 'ACTIVE'",
                        (lock["lease_id"],),
                    )

            tokens: Dict[str, int] = {}
            for key in keys:
                counter = connection.execute(
                    "SELECT last_token FROM fencing_counters WHERE lock_key = ?", (key,)
                ).fetchone()
                token = (counter["last_token"] if counter is not None else 0) + 1
                connection.execute(
                    "INSERT INTO fencing_counters(lock_key, last_token) VALUES (?, ?) "
                    "ON CONFLICT(lock_key) DO UPDATE SET last_token = excluded.last_token",
                    (key, token),
                )
                connection.execute(
                    "INSERT INTO active_locks(lock_key, lease_id, holder_identity, fencing_token, acquired_at, expires_at, revoked) "
                    "VALUES (?, ?, ?, ?, ?, ?, 0) "
                    "ON CONFLICT(lock_key) DO UPDATE SET lease_id=excluded.lease_id, holder_identity=excluded.holder_identity, "
                    "fencing_token=excluded.fencing_token, acquired_at=excluded.acquired_at, expires_at=excluded.expires_at, revoked=0",
                    (key, lease_id, holder_identity, token, now, expires),
                )
                tokens[key] = token
            connection.execute(
                "INSERT INTO lease_records(lease_id, plan_id, holder_identity, idempotency_key, lock_keys_json, lock_tokens_json, acquired_at, expires_at, status) "
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'ACTIVE')",
                (
                    lease_id, plan_id, holder_identity, idempotency_key,
                    json.dumps(list(keys), separators=(",", ":")),
                    json.dumps(tokens, sort_keys=True, separators=(",", ":")),
                    now, expires,
                ),
            )
            row = connection.execute(
                "SELECT * FROM lease_records WHERE lease_id = ?", (lease_id,)
            ).fetchone()
            connection.execute("COMMIT")
            assert row is not None
            return self._lease_contract(row, envelope)
        except Exception:
            if connection.in_transaction:
                connection.execute("ROLLBACK")
            raise
        finally:
            connection.close()

    def assert_fence(
        self,
        lease_id: str,
        lock_tokens: Mapping[str, int],
    ) -> None:
        if not isinstance(lock_tokens, Mapping) or not lock_tokens:
            raise StaleFence("fencing token set is missing")
        now = float(self.clock())
        connection = self._connect()
        try:
            record = connection.execute(
                "SELECT * FROM lease_records WHERE lease_id = ?", (lease_id,)
            ).fetchone()
            if record is None or record["status"] != "ACTIVE" or record["expires_at"] <= now:
                raise StaleFence("lease is absent, expired, or revoked")
            expected_keys = tuple(json.loads(record["lock_keys_json"]))
            if set(expected_keys) != set(lock_tokens):
                raise StaleFence("fencing token set does not cover the lease")
            for key in expected_keys:
                lock = connection.execute(
                    "SELECT * FROM active_locks WHERE lock_key = ?", (key,)
                ).fetchone()
                if (
                    lock is None or lock["lease_id"] != lease_id or lock["revoked"] != 0
                    or lock["expires_at"] <= now or lock["fencing_token"] != lock_tokens[key]
                ):
                    raise StaleFence("writer fencing token is stale: " + key)
        finally:
            connection.close()

    def revoke(self, lease_id: str) -> None:
        connection = self._connect()
        try:
            connection.execute("BEGIN IMMEDIATE")
            updated = connection.execute(
                "UPDATE lease_records SET status = 'REVOKED' WHERE lease_id = ? AND status = 'ACTIVE'",
                (lease_id,),
            ).rowcount
            if updated != 1:
                raise StaleFence("active lease was not found")
            connection.execute(
                "UPDATE active_locks SET revoked = 1 WHERE lease_id = ?", (lease_id,)
            )
            connection.execute("COMMIT")
        except Exception:
            if connection.in_transaction:
                connection.execute("ROLLBACK")
            raise
        finally:
            connection.close()
