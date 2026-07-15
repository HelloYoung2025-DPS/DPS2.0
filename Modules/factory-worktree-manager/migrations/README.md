# Migrations

`001_external_lease_store.sql` is the idempotent PostgreSQL 18.4 migration applied by the production `PostgresLeaseStore` adapter inside an isolated Factory schema. Runtime dependencies are independently pinned with hashes in `requirements.lock`. Rollback preserves counters and marks active records revoked; it never resets tokens. The embedded SQLite schema exists only for deterministic unit tests and local development.
