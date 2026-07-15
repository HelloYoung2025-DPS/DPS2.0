# Changelog

## 0.4.0 - 2026-07-14

- Moved unique ownership and production of `platform.account.authorization.evidence/v1` to the standalone `platform-authorization-authority` module. The account registry is now an explicit fail-closed consumer of that contract and its public canonicalizer/trust metadata.
- Added an executable shared boundary corpus for exact UTC, Int64, canonical identifiers, and absolute platform/alias/evidence identifier termination; aligned reservation timestamps/revisions to the same boundary, and malformed trailing data is rejected by both schema and strict runtime parsing.
- Normalized alias-key and authorization-evidence identifiers to lowercase across the authority input, account output, strict parser, and additive PostgreSQL migration; mixed-case pre-release rows fail migration instead of being silently rewritten.
- Corrected the authority-to-account inbound authorization scope and made the Integration harness reject the persistent GBrain Company target and non-18.4 servers before any schema mutation.

## 0.3.0 - 2026-07-14

- Replaced caller-forgeable approval references with the strict `platform.account.authorization.evidence/v1` P-256 signed envelope, a compiled public-root pin, bounded validity, exact scope/revision/BOM binding, and no repository-held production private key.
- Required the initial canonical `pa_<32 lowercase hex>` identifier before authorization so the signature binds the exact future account identity; added alias HMAC key epochs and persisted public signed-evidence documents plus hashes.
- Tightened all public v1 JSON contracts to exact `1.0.0`, required/unmapped/duplicate rejection, canonical `db_`/`pa_`/`trace_`/`idem_` identifiers, strict UTC and UTF-8 text, and newline-safe schema/database anchoring.
- Changed reservation receipt idempotency to the deterministic `idem_<SHA-256>` account-provider domain shared with the binding consumer, while preserving held/active/released reservation behavior.

## 0.2.0 - 2026-07-14

- Added the versioned `platform.account.binding.reservation/v1` contract and provider-owned held/active/released reservation state.
- Serialized reservation and authorization-status mutations with the same account advisory lock so suspend/revoke/refresh cannot race an effective binding.
- Added deterministic reservation lifecycle, mutation-blocking, and contract tests; real PostgreSQL proof remains a required Integration gate.

## Unreleased

- Added a real PostgreSQL 18.4 adapter with module-owned migrations, digest-only aliases, revisioned authorize/suspend/revoke lifecycle, transactionally coupled idempotency receipts and outbox rows, and fail-closed replay conflicts.
- Added a required disposable-schema Integration suite for concurrency uniqueness, crash rollback, restart recovery, cross-Soul/account isolation, and persistence privacy checks. Missing PostgreSQL is a hard failure rather than a skip.
- Bound signed authorization evidence to the configured active Release BOM and monotonic persisted generation, added per-migration SHA-256 provenance, and fail closed if evidence expires during a database mutation before commit.
- Aligned the C#, JSON Schema, and PostgreSQL platform identifier limit at 64 characters and added old-empty convergence, old-nonempty refusal, migration-drift, and generation-replay integration coverage.

## 0.1.0 - 2026-07-14

- Added hashed platform-account aliases, authorization revision and revocation, fail-closed v1 contract, and deterministic tests.
- Added canonical non-null Soul/binding/account scope, separated contract and implementation artifacts, and enforced scoped reads and mutations without propagating raw aliases.
