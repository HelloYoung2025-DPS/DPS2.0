-- PostgreSQL 18.4 production lease truth. The deployment role sets an isolated
-- Factory schema as search_path before applying this idempotent migration.
CREATE TABLE IF NOT EXISTS fencing_counters (
    lock_key TEXT PRIMARY KEY,
    last_token BIGINT NOT NULL CHECK (last_token >= 0)
);

CREATE TABLE IF NOT EXISTS active_locks (
    lock_key TEXT PRIMARY KEY,
    lease_id TEXT NOT NULL,
    holder_identity TEXT NOT NULL,
    fencing_token BIGINT NOT NULL CHECK (fencing_token >= 1),
    acquired_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE INDEX IF NOT EXISTS ix_active_locks_lease_id ON active_locks(lease_id);

CREATE TABLE IF NOT EXISTS lease_records (
    lease_id TEXT PRIMARY KEY,
    plan_id TEXT NOT NULL,
    holder_identity TEXT NOT NULL,
    idempotency_key TEXT NOT NULL UNIQUE CHECK (
        char_length(idempotency_key) = 69
        AND idempotency_key ~ '^idem_[0-9a-f]{64}$'
    ),
    lock_keys_json JSONB NOT NULL CHECK (jsonb_typeof(lock_keys_json) = 'array'),
    lock_tokens_json JSONB NOT NULL CHECK (jsonb_typeof(lock_tokens_json) = 'object'),
    envelope_json JSONB NOT NULL CHECK (jsonb_typeof(envelope_json) = 'object'),
    acquired_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ACTIVE', 'REVOKED', 'EXPIRED'))
);

CREATE INDEX IF NOT EXISTS ix_lease_records_plan_id ON lease_records(plan_id);
