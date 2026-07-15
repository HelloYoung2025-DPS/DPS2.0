# Changelog

## Unreleased

- Aligned all provided JSON integer ranges with the C# `long` boundary and constrained UTC timestamps to the exact 0-7 fractional-second representation accepted by the strict consumer parser.
- Added a shared executable `identity.binding/v1` boundary corpus covering exact version termination, canonical IDs, zero-offset UTC, Int64 overflow, and quoted-number rejection.
- Updated the real-provider Integration composition to consume authority-owned signed evidence through the account API. It now requires an external pinned-root PKCS#8 test signer and fails closed when signer infrastructure is absent; no production or fixture private key is stored in Git.
- Redacted inaccessible external-signer file failures without retaining path-bearing inner exceptions and guaranteed private-key byte cleanup even when algorithm creation fails.

## 0.4.0 - 2026-07-14

- Added fixed-root signed Release BOM composition attestation over binding/provider implementation and contract-pack digests, the composition host, non-secret instance-configuration digests, and trust epochs; caller-signed or caller-replaceable trust is rejected before production construction.
- Persisted the highest accepted composition generation, rejected rollback and same-generation equivocation, and made the production async factory apply ordered migrations before recording the generation and recovering pending work.
- Hardened every contract, schema, and database boundary to the exact repository-wide `db_`/`pa_`/`trace_` 32-hex and `idem_` 64-hex formats, with strict JSON unknown/missing/duplicate rejection.
- Made every provider reservation receipt fail closed on wrong scope, reservation, revision, state, trace, operation time, idempotency identity, or expired held lease.
- Added `identity.binding.mutation.fence/v1`, a contracts-only client whose real PostgreSQL lease holds the authoritative binding lock through a consumer commit.
- Made mutation-fence release non-pooled, idempotent, non-throwing after consumer commit, and hard-bounded to five seconds.
- Replaced cross-module friend access and source references with provider-owned public reservation clients and sealed implementations returned by module factories.
- Added ordered 002/003 migrations, autonomous pending-attempt startup recovery, provider command deadlines, pending-conflict quarantine before provider reads, and truthful provider compatibility floors.

## 0.3.0 - 2026-07-14

- Replaced the remaining read/check race with provider-owned exact-revision reservations in device-registry and platform-account-registry.
- Added a durable pending-attempt saga that resumes after forced termination, compensates partial provider reservation failures, and activates the binding only after both reservations are confirmed.
- Scoped quarantine reads, removed raw idempotency keys from quarantine output, preserved canonical JSON bytes for checksums, rejected historical bind receipts after revocation, and added revoke crash/retry coverage.

## 0.2.0 - 2026-07-14

- Removed caller-constructible device/account proof records from the binding mutation API and added trusted current-state provider adapters for the full provider-owned contracts.
- Added PostgreSQL current bindings, append-only revisions, receipts, conflict quarantine, transactional outbox, active device/account uniqueness, and crash-window fault injection.
- Added deterministic Unit/Contract coverage plus a required real PostgreSQL 18.4 Integration suite that uses the actual device and account providers through independent schemas and public APIs.

## 0.1.0 - 2026-07-14

- Added the proposed canonical identity binding contract, fail-closed lifecycle implementation boundary, and deterministic tests.
- Removed the stale `identity.binding/v1` outbound declaration to `soul-memory-adapter`; the adapter now consumes only `gbrain.projection/v1` and binding retains no fabricated coupling to it.
