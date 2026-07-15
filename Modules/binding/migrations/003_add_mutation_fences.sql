CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_mutation_fences (
    fence_id text PRIMARY KEY CHECK (length(fence_id) = 71 AND fence_id ~ '^bfence_[a-f0-9]{64}$'),
    fence_sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    binding_revision bigint NOT NULL CHECK (binding_revision >= 1),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    occurred_at timestamptz NOT NULL,
    acquired_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    released_at timestamptz NULL,
    FOREIGN KEY (device_binding_id, binding_revision)
        REFERENCES __SCHEMA__.binding_revisions(device_binding_id, binding_revision) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_binding_mutation_fence_scope
    ON __SCHEMA__.binding_mutation_fences(soul_id, device_binding_id, platform_account_id, fence_sequence);
