# Operations

## Production append boundary

Production composition is fail closed. `ExternalAppendAuthority` reads the
append HMAC key and revocation epoch only from:

- `DPS_FACTORY_EVIDENCE_APPEND_HMAC_KEY_B64`
- `DPS_FACTORY_EVIDENCE_APPEND_REVOCATION_EPOCH`

The key must decode to at least 32 bytes. Missing or invalid values are
`WAITING_EXTERNAL`; no repository is created and no event is appended. The
authorization service signs the exact canonical `upgrade.event.append/v1`
bytes for issuer `dps-factory-auth-service`, audience
`factory-evidence-ledger`, scope `factory:evidence:append`, the exact producer,
and the current revocation epoch. Capabilities expire within five minutes and
are revalidated by the storage repository at append time. They cannot be
copied, serialized, or inherited across a process fork, and reuse of one
authorization nonce is rejected atomically across concurrent verifier threads.

Python database access is pinned as `psycopg[binary]==3.3.4` in the root
`requirements-ci.in` and hash-locked `requirements-ci.txt`. Production code
uses a fixed DSN string and verifies that both PostgreSQL `current_user` and
`session_user` are exactly `dps_factory_evidence_runtime`; connection factories
and caller-selected roles are rejected.

## Migration and roles

Apply migrations in order with a dedicated migration identity:

1. `001_upgrade_event_ledger.sql`
2. `002_authenticated_append_acl.sql`

`002` refuses to invent raw authenticated command bytes for existing legacy
rows. If such rows exist, stop and perform a separately reviewed export,
verification, and explicit migration. The migration creates three fixed roles:

- `dps_factory_evidence_owner`: NOLOGIN internal table/function owner.
- `dps_factory_evidence_runtime`: protected append/read functions only.
- `dps_factory_evidence_admin`: key-install and protected read functions only.

The migration resets all security-sensitive role attributes, rejects any role
membership chain involving these identities, clears stale schema/table/
sequence/function grants, and then grants only the listed protected functions.
Runtime and admin have no direct table access. Event, quarantine, key history,
and stream state have mutation/truncate triggers; deferred constraints verify
that the stored head equals the final ordered event. Install the external HMAC
key through `factory_evidence.install_append_auth_key(bytea, bigint)` while
connected as the exact admin role. Installing the same epoch with the same key
is an idempotent no-op; key drift at the same epoch or an older epoch fails.

## Verification truth

The required PostgreSQL suite needs all of:

- `DPS_TEST_POSTGRES_MIGRATION_URI`
- `DPS_TEST_POSTGRES_ADMIN_URI`
- `DPS_TEST_POSTGRES_RUNTIME_URI`
- `DPS_FACTORY_EVIDENCE_APPEND_HMAC_KEY_B64`
- `DPS_FACTORY_EVIDENCE_APPEND_REVOCATION_EPOCH`
- `DPS_PSQL` or `psql` on `PATH`
- the repository hash-locked `psycopg` driver

Any missing input is a hard `INFRA_ERROR`; it is never skipped or replaced by
a mock. The JSONL repository is explicitly development-only evidence for local
locking and crash recovery. It rejects symlinks, hard links, non-regular files,
path replacement while locked, partial or short writes, oversized files,
duplicate members, tampering, and concurrent sequence races; it is never a
PostgreSQL or production fallback.

Rollback stops new capability issuance and writes, drains, verifies the full
command/event replay and database head, and routes to the previous compatible
writer without deleting history or key records.
