# Audit Metrics changelog

## 0.1.0 - Proposed

- Tighten all five audit identity fields to fixed-length opaque forms in the contract, canonical encoding, PostgreSQL truth, and test fixtures.
- Add `audit.event/v1`, append-only duplicate/conflict behavior, scope-isolated reads, and low-cardinality outcome counts.
- Reject raw PII, secret-like labels, outcome escalation, and unauthorized ingestion.
- Bind command receipts, relay envelopes, and audit-event integrity digests with domain-separated, length-prefixed canonical bytes instead of delimiter-joined text.
- Bind duplicate detection to the complete source receipt digest and cover delimiter-collision, signature-reuse, Golden Vector, and idempotency-conflict regressions.
- Add the Npgsql PostgreSQL 18.4 append path, additive migration, exact-scope readback, unique scoped idempotency, digest-only conflict quarantine, and database-enforced update/delete rejection.
- Require the concrete ECDSA relay verifier before the database transaction so an invalid signature produces zero audit or quarantine writes; add real-PostgreSQL concurrency, restart, ordering, isolation, and append-only Integration tests without treating fake verification or simulation as Integration evidence.
- Restrict trace and idempotency metadata to opaque safe characters and reject obvious PII/secret-shaped values before an audit event can be persisted.
- Bind PostgreSQL ingestion to the configured active Release BOM, require NIST P-256 with an explicit signature format, enforce five-second database timeouts, scope quarantine reads, and reject `TRUNCATE` alongside update/delete.
- Verify a root-signed active BOM/relay-key trust state before opening a business connection; split privileged migration from the runtime writer; and exercise real runtime-role grants, DDL denial, crash windows, cancellation rollback, and retry in the required PostgreSQL Integration suite.
- Replace owner-session `SET ROLE` with an independent unprivileged runtime login that is attested on every connection; persist signed trust-state revisions append-only; read the highest revision on every append; reject revision rollback; and use an internal `TimeProvider` so callers cannot replay expired authorization with a stale clock argument.
- Attest exact schema/table/function ownership and ACLs, reject runtime object ownership, and serialize trust-state publication against audit commit with exclusive/shared transaction advisory locks plus a same-transaction highest-revision read.
