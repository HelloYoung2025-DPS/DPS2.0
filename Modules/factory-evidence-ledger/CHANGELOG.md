# Changelog

## 0.2.0 - Proposed

- Preserved the public `upgrade.event.append/v1` and `upgrade.event/v1` schema bytes while adding a documented internal detached-auth envelope.
- Replaced Mapping-based append with a non-copyable, non-serializable, PID-bound capability for exact canonical command bytes; issuer, audience, scope, producer, TTL, revocation epoch, and signature are revalidated at repository append time, and nonce binding is atomic across threads.
- Removed arbitrary PostgreSQL connection factories and caller-selected roles. Production now requires the fixed external authority and exact runtime database identity; missing authority is `WAITING_EXTERNAL` with zero appends.
- Added strict duplicate-member, canonical UTC, privacy, identifier, integer-not-boolean, finite-number, payload byte/depth/node/item, source, and event-type validation.
- Expanded replay to cross-check raw command bytes, every derived event field, deterministic ID, source, timestamp, privacy, append status, all hashes, projected database columns, and stream head.
- Hardened the development-only JSONL fixture with no-follow regular single-link files, ownership checks, pre/post-lock path identity checks, OS locks, bounded read/write-all, fsync, strict complete lines, redacted corruption quarantine, and concurrent-writer serialization.
- Added additive PostgreSQL migration `002` with corrected fixed owner/runtime/admin attributes, forbidden role-membership chains, append-only key history, protected key/install/append/read functions, stale-grant removal and exact ACLs, projected JSON constraints, mutation/truncate guards, and deferred head consistency. Conflict outcomes are fully replayed and their exact quarantine record is verified before commit.
- Added capability forgery/copy/raw-swap/fork/expiry/revocation/parallel-nonce, strict JSON, resource limit, all-field replay, concurrent file, short-write/path-swap/symlink/hardlink/partial-line, ACL, and protected PostgreSQL adversarial tests. Real PostgreSQL and external auth remain required infrastructure and cannot be replaced by a mock.

## 0.1.0 - Proposed

- Tightened nullable identity envelopes plus trace/idempotency to the canonical opaque forms and added PostgreSQL checks so storage cannot accept a bypassed non-canonical idempotency key.
- Registered the ordered `upgrade.event/v1` replay path to `factory-control-plane-host`.
- Added append-only upgrade event contracts and PostgreSQL 18 migration.
- Added optimistic sequence, idempotency conflict, hash-chain replay, and replaceable repositories.
- Added a real subprocess-kill local crash-recovery integration test; PostgreSQL evidence remains explicitly pending.
- Hardened the Contract suite to load the exact module-owned production source under isolated Python without relying on `PYTHONPATH` or another test module.
- Hardened the required Unit suite with the same exact-path isolated loader instead of an ambient `PYTHONPATH` import.
- Hardened the crash-recovery and real PostgreSQL 18 Integration suites to resolve the fixed module-owned source, reject symlink or boundary escapes, and run under isolated Python without `PYTHONPATH`.
