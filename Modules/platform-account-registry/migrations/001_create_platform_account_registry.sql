CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.accounts (
    platform_account_id text PRIMARY KEY CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[0-9a-f]{32}$'),
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    platform text NOT NULL CHECK (length(platform) BETWEEN 1 AND 64 AND platform ~ '^[a-z0-9]+([._-][a-z0-9]+)*$'),
    alias_digest text NOT NULL CHECK (length(alias_digest) = 64 AND alias_digest ~ '^[0-9a-f]{64}$'),
    alias_key_id text NOT NULL CHECK (alias_key_id ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
    alias_key_epoch bigint NOT NULL CHECK (alias_key_epoch >= 1),
    authorization_evidence_id text NOT NULL CHECK (authorization_evidence_id ~ '^approval_[A-Za-z0-9_-]{1,119}$'),
    authorization_evidence_sha256 text NOT NULL CHECK (length(authorization_evidence_sha256) = 64 AND authorization_evidence_sha256 ~ '^[0-9a-f]{64}$'),
    authorization_evidence_json text NOT NULL CHECK (jsonb_typeof(authorization_evidence_json::jsonb) = 'object'),
    authorization_revision bigint NOT NULL CHECK (authorization_revision >= 1),
    status text NOT NULL CHECK (status IN ('authorized', 'suspended', 'revoked')),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (platform, alias_key_id, alias_key_epoch, alias_digest),
    UNIQUE (platform_account_id, soul_id, device_binding_id)
);

CREATE INDEX IF NOT EXISTS ix_platform_accounts_scope
    ON __SCHEMA__.accounts (soul_id, device_binding_id, platform_account_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_reservations (
    reservation_id text PRIMARY KEY CHECK (length(reservation_id) = 69 AND reservation_id ~ '^bres_[a-f0-9]{64}$'),
    platform_account_id text NOT NULL REFERENCES __SCHEMA__.accounts(platform_account_id) ON DELETE RESTRICT,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    account_authorization_revision bigint NOT NULL CHECK (account_authorization_revision >= 1),
    state text NOT NULL CHECK (state IN ('held', 'active', 'released')),
    lease_expires_at timestamptz NULL,
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    occurred_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK ((state = 'held') = (lease_expires_at IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_platform_account_effective_binding_reservation
    ON __SCHEMA__.binding_reservations(platform_account_id)
    WHERE state IN ('held', 'active');

CREATE INDEX IF NOT EXISTS ix_platform_account_binding_reservation_scope
    ON __SCHEMA__.binding_reservations(soul_id, device_binding_id, platform_account_id, reservation_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.authorization_revisions (
    platform_account_id text NOT NULL,
    authorization_revision bigint NOT NULL CHECK (authorization_revision >= 1),
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    status text NOT NULL CHECK (status IN ('authorized', 'suspended', 'revoked')),
    authorization_evidence_id text NOT NULL CHECK (authorization_evidence_id ~ '^approval_[A-Za-z0-9_-]{1,119}$'),
    authorization_evidence_sha256 text NOT NULL CHECK (length(authorization_evidence_sha256) = 64 AND authorization_evidence_sha256 ~ '^[0-9a-f]{64}$'),
    authorization_evidence_json text NOT NULL CHECK (jsonb_typeof(authorization_evidence_json::jsonb) = 'object'),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (platform_account_id, authorization_revision),
    UNIQUE (idempotency_key),
    FOREIGN KEY (platform_account_id, soul_id, device_binding_id)
        REFERENCES __SCHEMA__.accounts (platform_account_id, soul_id, device_binding_id)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.mutation_receipts (
    idempotency_key text PRIMARY KEY CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    operation text NOT NULL CHECK (operation IN ('authorize', 'status')),
    request_sha256 text NOT NULL CHECK (length(request_sha256) = 64 AND request_sha256 ~ '^[0-9a-f]{64}$'),
    platform_account_id text NOT NULL,
    authorization_revision bigint NOT NULL,
    result_json text NOT NULL CHECK (jsonb_typeof(result_json::jsonb) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (platform_account_id, authorization_revision)
        REFERENCES __SCHEMA__.authorization_revisions (platform_account_id, authorization_revision),
    CHECK (result_json::jsonb ->> 'platform_account_id' = platform_account_id),
    CHECK ((result_json::jsonb ->> 'authorization_revision')::bigint = authorization_revision),
    CHECK (result_json::jsonb ->> 'idempotency_key' = idempotency_key)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox (
    outbox_id uuid PRIMARY KEY,
    idempotency_key text NOT NULL UNIQUE REFERENCES __SCHEMA__.mutation_receipts (idempotency_key),
    platform_account_id text NOT NULL,
    authorization_revision bigint NOT NULL,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    topic text NOT NULL CHECK (topic = 'platform.account.authorized/v1'),
    payload_sha256 text NOT NULL CHECK (length(payload_sha256) = 64 AND payload_sha256 ~ '^[0-9a-f]{64}$'),
    payload_json text NOT NULL CHECK (jsonb_typeof(payload_json::jsonb) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    dispatched_at timestamptz NULL,
    FOREIGN KEY (platform_account_id, authorization_revision)
        REFERENCES __SCHEMA__.authorization_revisions (platform_account_id, authorization_revision),
    CHECK (payload_json::jsonb ->> 'platform_account_id' = platform_account_id),
    CHECK ((payload_json::jsonb ->> 'authorization_revision')::bigint = authorization_revision),
    CHECK (payload_json::jsonb ->> 'soul_id' = soul_id),
    CHECK (payload_json::jsonb ->> 'device_binding_id' = device_binding_id),
    CHECK (payload_json::jsonb ->> 'trace_id' = trace_id),
    CHECK (payload_json::jsonb ->> 'idempotency_key' = idempotency_key),
    CHECK (payload_json::jsonb ->> 'contract_id' = topic),
    CHECK (payload_sha256 = encode(sha256(convert_to(payload_json, 'UTF8')), 'hex'))
);

CREATE INDEX IF NOT EXISTS ix_platform_account_outbox_pending
    ON __SCHEMA__.outbox (created_at, outbox_id)
    WHERE dispatched_at IS NULL;

CREATE OR REPLACE FUNCTION __SCHEMA__.protect_platform_account_identity()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'platform accounts cannot be deleted by a runtime mutation';
    END IF;

    IF NEW.platform_account_id IS DISTINCT FROM OLD.platform_account_id
       OR NEW.soul_id IS DISTINCT FROM OLD.soul_id
       OR NEW.device_binding_id IS DISTINCT FROM OLD.device_binding_id
       OR NEW.platform IS DISTINCT FROM OLD.platform
       OR NEW.alias_digest IS DISTINCT FROM OLD.alias_digest
       OR NEW.alias_key_id IS DISTINCT FROM OLD.alias_key_id
       OR NEW.alias_key_epoch IS DISTINCT FROM OLD.alias_key_epoch
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR NEW.authorization_revision <> OLD.authorization_revision + 1 THEN
        RAISE EXCEPTION 'platform account identity is immutable and revisions must advance exactly once';
    END IF;

    IF OLD.status = 'revoked' THEN
        RAISE EXCEPTION 'a revoked platform account cannot be mutated';
    END IF;

    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS platform_account_identity_protected ON __SCHEMA__.accounts;
CREATE TRIGGER platform_account_identity_protected
BEFORE UPDATE OR DELETE ON __SCHEMA__.accounts
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.protect_platform_account_identity();

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_platform_account_append_only_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION '% is append-only', TG_TABLE_NAME;
END;
$function$;

DROP TRIGGER IF EXISTS authorization_revisions_append_only ON __SCHEMA__.authorization_revisions;
CREATE TRIGGER authorization_revisions_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.authorization_revisions
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_platform_account_append_only_mutation();

DROP TRIGGER IF EXISTS mutation_receipts_append_only ON __SCHEMA__.mutation_receipts;
CREATE TRIGGER mutation_receipts_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.mutation_receipts
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_platform_account_append_only_mutation();

CREATE OR REPLACE FUNCTION __SCHEMA__.protect_platform_account_outbox()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'platform account outbox rows cannot be deleted';
    END IF;

    IF NEW.outbox_id IS DISTINCT FROM OLD.outbox_id
       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
       OR NEW.platform_account_id IS DISTINCT FROM OLD.platform_account_id
       OR NEW.authorization_revision IS DISTINCT FROM OLD.authorization_revision
       OR NEW.soul_id IS DISTINCT FROM OLD.soul_id
       OR NEW.device_binding_id IS DISTINCT FROM OLD.device_binding_id
       OR NEW.trace_id IS DISTINCT FROM OLD.trace_id
       OR NEW.topic IS DISTINCT FROM OLD.topic
       OR NEW.payload_sha256 IS DISTINCT FROM OLD.payload_sha256
       OR NEW.payload_json IS DISTINCT FROM OLD.payload_json
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR OLD.dispatched_at IS NOT NULL
       OR NEW.dispatched_at IS NULL THEN
        RAISE EXCEPTION 'platform account outbox payload is immutable and dispatch can be acknowledged once';
    END IF;

    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS platform_account_outbox_protected ON __SCHEMA__.outbox;
CREATE TRIGGER platform_account_outbox_protected
BEFORE UPDATE OR DELETE ON __SCHEMA__.outbox
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.protect_platform_account_outbox();
