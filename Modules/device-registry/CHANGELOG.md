# Changelog

## Unreleased

- Added a shared executable `device.registered/v1` boundary corpus proving strict version termination, canonical identifiers, exact zero-offset UTC, Int64 overflow, and quoted-number rejection across owner and consumers; aligned reservation timestamps and revisions to the same UTC/Int64 boundary.
- Made the disposable PostgreSQL harness reject the persistent GBrain Company port and database before opening any connection.

## 0.4.0 - 2026-07-14

- Replaced the proposed unkeyed `fingerprint_sha256` field with `fingerprint_hmac_sha256`, a canonical non-secret key ID, and a positive key epoch; registration now rejects a key version other than the registry's active configuration.
- Made public DTO JSON reject unknown and missing properties and aligned exact `1.0.0`, UTC, canonical `trace_/idem_/db_/pa_` identifiers, and bounded canonical ASCII capabilities across C#, schemas, PostgreSQL constraints, and tests.
- Added a fail-closed migration that refuses to fabricate HMAC provenance for populated unkeyed rows; empty pre-release schemas may converge, while real re-attestation remains an explicit reviewed operation.
- Added binding-provider instance attestation metadata: a secret-free configuration SHA-256 binds the PostgreSQL target, schema, fingerprint key ID/epoch, and positive trust epoch; reservation clients expose both values for signed composition verification.

## 0.3.0 - 2026-07-14

- Added the versioned `device.binding.reservation/v1` contract and provider-owned held/active/released reservation state.
- Serialized reservation and device mutations with the same device advisory lock so an effective reservation freezes the exact registration revision until release.
- Added deterministic reservation lifecycle, mutation-blocking, and contract tests; real PostgreSQL proof remains a required Integration gate.

## 0.2.0 - 2026-07-14

- Added a PostgreSQL 18.4 repository and embedded initial migration for devices, append-only capability revisions, idempotency receipts, outbox delivery, and conflict quarantine.
- Made register, capability revision, and retirement mutations transactional across state, revision, receipt, and outbox writes, with injected crash-window recovery tests.
- Added required real-PostgreSQL integration coverage for same-hash replay, conflicting-hash rejection, concurrent uniqueness, restart read-back, cross-scope isolation, and the raw hardware/PII storage boundary.

## 0.1.0 - 2026-07-14

- Added stable hashed device identity, versioned capabilities, fail-closed v1 contract, and deterministic local tests.
- Replaced UUID and `not_applicable` identity placeholders with canonical scoped identifiers, split the contract pack from implementation, and added retirement and cross-scope rejection.
