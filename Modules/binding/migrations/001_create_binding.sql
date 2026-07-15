CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.bindings (
    device_binding_id text PRIMARY KEY CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    device_id text NOT NULL CHECK (length(device_id) = 39 AND device_id ~ '^device_[a-f0-9]{32}$'),
    reservation_id text NOT NULL CHECK (length(reservation_id) = 69 AND reservation_id ~ '^bres_[a-f0-9]{64}$'),
    binding_revision bigint NOT NULL CHECK (binding_revision >= 1),
    status text NOT NULL CHECK (status IN ('active', 'revoked')),
    device_registration_revision bigint NOT NULL CHECK (device_registration_revision >= 1),
    account_authorization_revision bigint NOT NULL CHECK (account_authorization_revision >= 1),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    occurred_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

ALTER TABLE __SCHEMA__.bindings
    ADD COLUMN IF NOT EXISTS reservation_id text CHECK (length(reservation_id) = 69 AND reservation_id ~ '^bres_[a-f0-9]{64}$');

CREATE UNIQUE INDEX IF NOT EXISTS ux_binding_active_device
    ON __SCHEMA__.bindings (device_id)
    WHERE status = 'active';

CREATE UNIQUE INDEX IF NOT EXISTS ux_binding_active_account
    ON __SCHEMA__.bindings (platform_account_id)
    WHERE status = 'active';

CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_attempts (
    idempotency_key text PRIMARY KEY CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[a-f0-9]{64}$'),
    reservation_id text NOT NULL UNIQUE CHECK (length(reservation_id) = 69 AND reservation_id ~ '^bres_[a-f0-9]{64}$'),
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    device_id text NOT NULL CHECK (length(device_id) = 39 AND device_id ~ '^device_[a-f0-9]{32}$'),
    device_registration_revision bigint NOT NULL CHECK (device_registration_revision >= 1),
    account_authorization_revision bigint NOT NULL CHECK (account_authorization_revision >= 1),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    occurred_at timestamptz NOT NULL,
    state text NOT NULL CHECK (state IN ('pending', 'committed', 'compensated')),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS ix_binding_attempt_scope
    ON __SCHEMA__.binding_attempts(soul_id, device_binding_id, platform_account_id, state);

CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_revisions (
    device_binding_id text NOT NULL,
    binding_revision bigint NOT NULL CHECK (binding_revision >= 1),
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    device_id text NOT NULL CHECK (length(device_id) = 39 AND device_id ~ '^device_[a-f0-9]{32}$'),
    status text NOT NULL CHECK (status IN ('active', 'revoked')),
    device_registration_revision bigint NOT NULL CHECK (device_registration_revision >= 1),
    account_authorization_revision bigint NOT NULL CHECK (account_authorization_revision >= 1),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    occurred_at timestamptz NOT NULL,
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[a-f0-9]{64}$'),
    payload_canonical text NOT NULL,
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (device_binding_id, binding_revision),
    FOREIGN KEY (device_binding_id) REFERENCES __SCHEMA__.bindings (device_binding_id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_receipts (
    idempotency_key text PRIMARY KEY CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    operation text NOT NULL CHECK (operation IN ('bind', 'revoke')),
    request_sha256 text NOT NULL CHECK (request_sha256 ~ '^[a-f0-9]{64}$'),
    device_binding_id text NOT NULL,
    binding_revision bigint NOT NULL CHECK (binding_revision >= 1),
    result_canonical text NOT NULL,
    result_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (device_binding_id, binding_revision)
        REFERENCES __SCHEMA__.binding_revisions (device_binding_id, binding_revision) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_quarantine (
    quarantine_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    idempotency_key_sha256 text NOT NULL CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$'),
    incoming_operation text NOT NULL CHECK (incoming_operation IN ('bind', 'revoke')),
    existing_request_sha256 text NOT NULL CHECK (existing_request_sha256 ~ '^[a-f0-9]{64}$'),
    incoming_request_sha256 text NOT NULL CHECK (incoming_request_sha256 ~ '^[a-f0-9]{64}$'),
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox (
    outbox_id uuid PRIMARY KEY,
    idempotency_key text NOT NULL,
    device_binding_id text NOT NULL,
    binding_revision bigint NOT NULL CHECK (binding_revision >= 1),
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    topic text NOT NULL CHECK (topic = 'identity.binding/v1'),
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[a-f0-9]{64}$'),
    payload_canonical text NOT NULL,
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    dispatched_at timestamptz NULL,
    UNIQUE (idempotency_key),
    FOREIGN KEY (device_binding_id, binding_revision)
        REFERENCES __SCHEMA__.binding_revisions (device_binding_id, binding_revision) ON DELETE RESTRICT
);

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_binding_revision_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'binding revisions are append-only';
END;
$function$;

DROP TRIGGER IF EXISTS binding_revisions_append_only ON __SCHEMA__.binding_revisions;
CREATE TRIGGER binding_revisions_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.binding_revisions
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_binding_revision_mutation();
