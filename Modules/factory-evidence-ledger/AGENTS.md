---
agents_spec: dps.agents/v1
policy_version: 1.1.0
module_id: factory-evidence-ledger
manifest: ./module.yaml
applies_to: .
---

# Factory Evidence Ledger Agent Rules

## Scope

This module owns the append-only upgrade-event ledger. It provides authenticated exact-byte append, optimistic sequence checks, idempotency, redacted conflict quarantine, full-field hash-chain replay, and fixed storage compositions. It does not alter historical events, approve releases, or label simulated evidence as real deployment evidence.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, all contracts, dependency and compatibility sources, migrations, tests, and operations instructions in order. Bind exact hashes; a stale receipt requires rebinding before another write.

## Invariants

- Stream sequence is monotonic and enforced with optimistic concurrency.
- An append requires an opaque, process-bound capability for the exact canonical command bytes. A Mapping, lambda, caller-selected trust root, copied capability, fork-inherited capability, stale capability, wrong issuer/audience/scope/producer, or changed raw bytes fails before storage. Nonce binding is atomic across threads.
- The repository revalidates the capability at append time. Production PostgreSQL composition requires the external authority; missing external authentication is `WAITING_EXTERNAL` with zero appends.
- The same stream/idempotency key and exact authenticated command returns the original event; different command bytes are redacted, durably quarantined, and rejected.
- Every replay recomputes command, payload, deterministic event ID, all projected fields, sequence, prior hash, event hash, status, source, privacy, and timestamp. Corruption fails closed.
- PostgreSQL writes use one protected security-definer transaction, a fixed runtime identity, exact ACLs, a unique idempotency constraint, a protected stream head, and append-only mutation/truncate triggers. Runtime and admin identities never receive direct table writes.
- The file repository is development-only. It uses an existing non-symlink parent, no-follow regular single-link files, pre/post-lock path identity checks, OS locks, bounded write-all plus fsync, strict JSONL, and redacted corruption quarantine; it is never a production fallback.
- File-backed crash tests are labelled local integration; they do not satisfy PostgreSQL, Windows, device, canary, or scale gates.

## Communication and data

Use only owned JSON contracts and protected repository functions. The detached authentication envelope under `contracts/internal/` is internal composition data and must never be declared as a public product event. Never accept a caller-provided connection factory or database role, read another module's tables, or allow callers to supply SQL. Keep prompts, secrets, raw personal content, and device credentials out of ledger metadata.

## Tests, rollout, and rollback

Test valid and forged capabilities, copy/JSON/raw-swap/fork/parallel-nonce attacks, duplicate JSON members, noncanonical time and number types, size/depth/count limits, duplicate append, conflicting append, expected-sequence conflict, concurrent file writers, crash after durable append, short-write/path-swap/symlink/hardlink/partial-line attacks, every replayed field, database ACL bypass, truncate, head drift, and PostgreSQL transactional behavior. Missing real PostgreSQL roles, key material, `psql`, or the locked driver is `INFRA_ERROR`, never skip. Ledger migrations are additive; rollback routes writers to the prior compatible version and never deletes history.
