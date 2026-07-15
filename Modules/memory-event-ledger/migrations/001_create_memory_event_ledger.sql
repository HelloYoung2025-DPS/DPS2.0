CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.memory_events (
    event_id uuid PRIMARY KEY,
    soul_id text NOT NULL CHECK (char_length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (char_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    platform_account_id text NOT NULL CHECK (char_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[0-9a-f]{32}$'),
    soul_sequence bigint NOT NULL,
    occurred_at timestamptz NOT NULL,
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_json text NOT NULL,
    event_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (soul_id, soul_sequence),
    UNIQUE (event_id, soul_id, device_binding_id, platform_account_id),
    CHECK (jsonb_typeof(event_json) = 'object'),
    CHECK (event_json = canonical_json::jsonb),
    CHECK (payload_sha256 = encode(sha256(convert_to(canonical_json, 'UTF8')), 'hex')),
    CHECK (event_json ->> 'schema_version' = '1.0.0'),
    CHECK (event_json ->> 'contract_id' = 'memory.event/v1'),
    CHECK (event_json ->> 'producer_module' = 'memory-event-ledger'),
    CHECK (event_json ->> 'event_id' = event_id::text),
    CHECK (event_json ->> 'soul_id' = soul_id),
    CHECK (event_json ->> 'device_binding_id' = device_binding_id),
    CHECK (event_json ->> 'platform_account_id' = platform_account_id),
    CHECK ((event_json ->> 'occurred_at')::timestamptz = occurred_at),
    CHECK (event_json ->> 'privacy_class' = 'personal')
);

CREATE INDEX IF NOT EXISTS ix_memory_events_soul_order
    ON __SCHEMA__.memory_events (soul_id, soul_sequence);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox (
    outbox_id uuid PRIMARY KEY,
    event_id uuid NOT NULL UNIQUE,
    soul_id text NOT NULL CHECK (char_length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (char_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    platform_account_id text NOT NULL CHECK (char_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[0-9a-f]{32}$'),
    trace_id text NOT NULL CHECK (char_length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    idempotency_key text NOT NULL CHECK (char_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    occurred_at timestamptz NOT NULL,
    topic text NOT NULL CHECK (topic = 'memory.event/v1'),
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_payload_json text NOT NULL,
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    dispatched_at timestamptz NULL,
    FOREIGN KEY (event_id, soul_id, device_binding_id, platform_account_id)
        REFERENCES __SCHEMA__.memory_events
            (event_id, soul_id, device_binding_id, platform_account_id),
    CHECK (jsonb_typeof(payload_json) = 'object'),
    CHECK (payload_json = canonical_payload_json::jsonb),
    CHECK (payload_sha256 = encode(sha256(convert_to(canonical_payload_json, 'UTF8')), 'hex')),
    CHECK (payload_json ->> 'event_id' = event_id::text),
    CHECK (payload_json ->> 'soul_id' = soul_id),
    CHECK (payload_json ->> 'device_binding_id' = device_binding_id),
    CHECK (payload_json ->> 'platform_account_id' = platform_account_id),
    CHECK (payload_json ->> 'trace_id' = trace_id),
    CHECK (payload_json ->> 'idempotency_key' = idempotency_key),
    CHECK ((payload_json ->> 'occurred_at')::timestamptz = occurred_at)
);

CREATE INDEX IF NOT EXISTS ix_outbox_pending
    ON __SCHEMA__.outbox (created_at, outbox_id)
    WHERE dispatched_at IS NULL;

CREATE OR REPLACE FUNCTION __SCHEMA__.verify_outbox_event_match()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    source_event __SCHEMA__.memory_events%ROWTYPE;
BEGIN
    SELECT *
    INTO source_event
    FROM __SCHEMA__.memory_events
    WHERE event_id = NEW.event_id
      AND soul_id = NEW.soul_id
      AND device_binding_id = NEW.device_binding_id
      AND platform_account_id = NEW.platform_account_id;

    -- The composite foreign key below owns scope rejection. Returning here lets
    -- PostgreSQL report an FK violation instead of replacing it with a trigger error.
    IF NOT FOUND THEN
        RETURN NEW;
    END IF;

    IF NEW.occurred_at IS DISTINCT FROM source_event.occurred_at
       OR NEW.payload_sha256 IS DISTINCT FROM source_event.payload_sha256
       OR NEW.canonical_payload_json IS DISTINCT FROM source_event.canonical_json
       OR NEW.payload_json IS DISTINCT FROM source_event.event_json
       OR NEW.trace_id IS DISTINCT FROM source_event.event_json ->> 'trace_id'
       OR NEW.idempotency_key IS DISTINCT FROM source_event.event_json ->> 'idempotency_key' THEN
        RAISE EXCEPTION 'outbox scope and payload must exactly match its source memory event';
    END IF;

    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS outbox_event_match ON __SCHEMA__.outbox;
CREATE TRIGGER outbox_event_match
BEFORE INSERT ON __SCHEMA__.outbox
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.verify_outbox_event_match();

CREATE OR REPLACE FUNCTION __SCHEMA__.protect_outbox_record()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'outbox records cannot be deleted';
    END IF;

    IF NEW.outbox_id IS DISTINCT FROM OLD.outbox_id
       OR NEW.event_id IS DISTINCT FROM OLD.event_id
       OR NEW.soul_id IS DISTINCT FROM OLD.soul_id
       OR NEW.device_binding_id IS DISTINCT FROM OLD.device_binding_id
       OR NEW.platform_account_id IS DISTINCT FROM OLD.platform_account_id
       OR NEW.trace_id IS DISTINCT FROM OLD.trace_id
       OR NEW.idempotency_key IS DISTINCT FROM OLD.idempotency_key
       OR NEW.occurred_at IS DISTINCT FROM OLD.occurred_at
       OR NEW.topic IS DISTINCT FROM OLD.topic
       OR NEW.payload_sha256 IS DISTINCT FROM OLD.payload_sha256
       OR NEW.canonical_payload_json IS DISTINCT FROM OLD.canonical_payload_json
       OR NEW.payload_json IS DISTINCT FROM OLD.payload_json
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR OLD.dispatched_at IS NOT NULL
       OR NEW.dispatched_at IS NULL THEN
        RAISE EXCEPTION 'outbox payload is immutable and dispatch can be acknowledged once';
    END IF;

    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS outbox_protected ON __SCHEMA__.outbox;
CREATE TRIGGER outbox_protected
BEFORE UPDATE OR DELETE ON __SCHEMA__.outbox
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.protect_outbox_record();

CREATE TABLE IF NOT EXISTS __SCHEMA__.quarantine (
    quarantine_id uuid PRIMARY KEY,
    event_id uuid NOT NULL,
    incoming_soul_id text NOT NULL,
    existing_sha256 text NOT NULL CHECK (length(existing_sha256) = 64),
    incoming_sha256 text NOT NULL CHECK (length(incoming_sha256) = 64),
    incoming_json jsonb NOT NULL,
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (event_id, incoming_sha256)
);

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_quarantine_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'quarantine records are append-only';
END;
$function$;

DROP TRIGGER IF EXISTS quarantine_append_only ON __SCHEMA__.quarantine;
CREATE TRIGGER quarantine_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.quarantine
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_quarantine_mutation();

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_memory_event_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'memory_events is append-only';
END;
$function$;

DROP TRIGGER IF EXISTS memory_events_append_only ON __SCHEMA__.memory_events;
CREATE TRIGGER memory_events_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.memory_events
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_memory_event_mutation();
