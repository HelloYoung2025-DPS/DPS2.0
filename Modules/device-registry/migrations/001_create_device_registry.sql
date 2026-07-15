CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE OR REPLACE FUNCTION __SCHEMA__.device_capabilities_are_canonical(values_to_check text[])
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
AS $capabilities$
    SELECT cardinality(values_to_check) <= 64
       AND octet_length(array_to_string(values_to_check, '')) <= 4096
       AND COALESCE(
            (SELECT bool_and(
                value ~ '^[a-z0-9]+([._-][a-z0-9]+)*$'
                AND value !~ '[^a-z0-9._-]'
                AND length(value) BETWEEN 1 AND 64)
             FROM unnest(values_to_check) AS capabilities(value)),
            true)
       AND values_to_check = ARRAY(
            SELECT DISTINCT value
            FROM unnest(values_to_check) AS capabilities(value)
            ORDER BY value);
$capabilities$;

CREATE TABLE IF NOT EXISTS __SCHEMA__.devices
(
    device_id text PRIMARY KEY,
    fingerprint_hmac_sha256 text NOT NULL,
    fingerprint_key_id text NOT NULL,
    fingerprint_key_epoch bigint NOT NULL,
    registration_soul_id text NOT NULL,
    registration_device_binding_id text NOT NULL,
    registration_platform_account_id text NOT NULL,
    current_revision bigint NOT NULL,
    status text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (fingerprint_key_id, fingerprint_key_epoch, fingerprint_hmac_sha256),
    CONSTRAINT devices_device_id_format CHECK (length(device_id) = 39 AND device_id ~ '^device_[a-f0-9]{32}$'),
    CONSTRAINT devices_fingerprint_hmac_format CHECK (length(fingerprint_hmac_sha256) = 64 AND fingerprint_hmac_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT devices_fingerprint_key_id_format CHECK (length(fingerprint_key_id) = 38 AND fingerprint_key_id ~ '^fpkey_[a-f0-9]{32}$'),
    CONSTRAINT devices_fingerprint_key_epoch_positive CHECK (fingerprint_key_epoch >= 1),
    CONSTRAINT devices_soul_id_format CHECK (length(registration_soul_id) = 69 AND registration_soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT devices_binding_id_format CHECK (length(registration_device_binding_id) = 35 AND registration_device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT devices_account_id_format CHECK (length(registration_platform_account_id) = 35 AND registration_platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT devices_revision_positive CHECK (current_revision >= 1),
    CONSTRAINT devices_status_known CHECK (status IN ('registered', 'retired'))
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.capability_revisions
(
    device_id text NOT NULL,
    capability_revision bigint NOT NULL,
    soul_id text NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    trace_id text NOT NULL,
    idempotency_key text NOT NULL,
    occurred_at timestamptz NOT NULL,
    capabilities text[] NOT NULL,
    status text NOT NULL,
    payload_sha256 text NOT NULL,
    result_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (device_id, capability_revision),
    CONSTRAINT capability_revisions_device_fk
        FOREIGN KEY (device_id) REFERENCES __SCHEMA__.devices(device_id) ON DELETE RESTRICT,
    CONSTRAINT capability_revisions_revision_positive CHECK (capability_revision >= 1),
    CONSTRAINT capability_revisions_soul_id_format CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT capability_revisions_binding_id_format CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT capability_revisions_account_id_format CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT capability_revisions_trace_id_format CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    CONSTRAINT capability_revisions_idempotency_format CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT capability_revisions_capability_count CHECK (cardinality(capabilities) BETWEEN 0 AND 64),
    CONSTRAINT capability_revisions_capability_bytes CHECK (octet_length(array_to_string(capabilities, '')) <= 4096),
    CONSTRAINT capability_revisions_capabilities_canonical CHECK (__SCHEMA__.device_capabilities_are_canonical(capabilities)),
    CONSTRAINT capability_revisions_status_known CHECK (status IN ('registered', 'retired')),
    CONSTRAINT capability_revisions_payload_hash CHECK (length(payload_sha256) = 64 AND payload_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT capability_revisions_result_object CHECK (jsonb_typeof(result_json) = 'object'),
    CONSTRAINT capability_revisions_result_device CHECK (result_json ->> 'device_id' = device_id),
    CONSTRAINT capability_revisions_result_revision CHECK ((result_json ->> 'capability_revision')::bigint = capability_revision),
    CONSTRAINT capability_revisions_result_soul CHECK (result_json ->> 'soul_id' = soul_id),
    CONSTRAINT capability_revisions_result_binding CHECK (result_json ->> 'device_binding_id' = device_binding_id),
    CONSTRAINT capability_revisions_result_account CHECK (result_json ->> 'platform_account_id' = platform_account_id),
    CONSTRAINT capability_revisions_result_fingerprint_hmac CHECK (length(result_json ->> 'fingerprint_hmac_sha256') = 64 AND result_json ->> 'fingerprint_hmac_sha256' ~ '^[a-f0-9]{64}$'),
    CONSTRAINT capability_revisions_result_fingerprint_key CHECK (length(result_json ->> 'fingerprint_key_id') = 38 AND result_json ->> 'fingerprint_key_id' ~ '^fpkey_[a-f0-9]{32}$'),
    CONSTRAINT capability_revisions_result_fingerprint_epoch CHECK ((result_json ->> 'fingerprint_key_epoch')::bigint >= 1),
    CONSTRAINT capability_revisions_result_no_unkeyed_fingerprint CHECK (NOT (result_json ? 'fingerprint_sha256')),
    CONSTRAINT capability_revisions_result_status CHECK (result_json ->> 'status' = status)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_reservations
(
    reservation_id text PRIMARY KEY CHECK (length(reservation_id) = 69 AND reservation_id ~ '^bres_[a-f0-9]{64}$'),
    device_id text NOT NULL REFERENCES __SCHEMA__.devices(device_id) ON DELETE RESTRICT,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    device_registration_revision bigint NOT NULL CHECK (device_registration_revision >= 1),
    state text NOT NULL CHECK (state IN ('held', 'active', 'released')),
    lease_expires_at timestamptz NULL,
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    occurred_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK ((state = 'held') = (lease_expires_at IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_device_effective_binding_reservation
    ON __SCHEMA__.binding_reservations(device_id)
    WHERE state IN ('held', 'active');

CREATE INDEX IF NOT EXISTS ix_device_binding_reservation_scope
    ON __SCHEMA__.binding_reservations(soul_id, device_binding_id, platform_account_id, reservation_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_receipts
(
    idempotency_key text PRIMARY KEY,
    command_sha256 text NOT NULL,
    mutation_kind text NOT NULL,
    device_id text NOT NULL,
    capability_revision bigint NOT NULL,
    outbox_id uuid NOT NULL UNIQUE,
    result_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT idempotency_receipts_revision_fk
        FOREIGN KEY (device_id, capability_revision)
        REFERENCES __SCHEMA__.capability_revisions(device_id, capability_revision) ON DELETE RESTRICT,
    CONSTRAINT idempotency_receipts_key_format CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_command_hash CHECK (length(command_sha256) = 64 AND command_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_kind_known CHECK (mutation_kind IN ('register', 'capability-revision', 'retire')),
    CONSTRAINT idempotency_receipts_result_object CHECK (jsonb_typeof(result_json) = 'object'),
    CONSTRAINT idempotency_receipts_result_device CHECK (result_json ->> 'device_id' = device_id),
    CONSTRAINT idempotency_receipts_result_revision CHECK ((result_json ->> 'capability_revision')::bigint = capability_revision)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox
(
    outbox_id uuid PRIMARY KEY,
    device_id text NOT NULL,
    capability_revision bigint NOT NULL,
    soul_id text NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    trace_id text NOT NULL,
    idempotency_key text NOT NULL UNIQUE,
    occurred_at timestamptz NOT NULL,
    topic text NOT NULL,
    payload_sha256 text NOT NULL,
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    dispatched_at timestamptz NULL,
    UNIQUE (device_id, capability_revision),
    CONSTRAINT outbox_revision_fk
        FOREIGN KEY (device_id, capability_revision)
        REFERENCES __SCHEMA__.capability_revisions(device_id, capability_revision) ON DELETE RESTRICT,
    CONSTRAINT outbox_receipt_fk
        FOREIGN KEY (idempotency_key)
        REFERENCES __SCHEMA__.idempotency_receipts(idempotency_key) ON DELETE RESTRICT,
    CONSTRAINT outbox_soul_id_format CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT outbox_binding_id_format CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT outbox_account_id_format CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT outbox_trace_id_format CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    CONSTRAINT outbox_idempotency_key_format CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT outbox_topic_exact CHECK (topic = 'device.registered/v1'),
    CONSTRAINT outbox_payload_hash CHECK (length(payload_sha256) = 64 AND payload_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT outbox_payload_object CHECK (jsonb_typeof(payload_json) = 'object'),
    CONSTRAINT outbox_payload_device CHECK (payload_json ->> 'device_id' = device_id),
    CONSTRAINT outbox_payload_revision CHECK ((payload_json ->> 'capability_revision')::bigint = capability_revision),
    CONSTRAINT outbox_payload_soul CHECK (payload_json ->> 'soul_id' = soul_id),
    CONSTRAINT outbox_payload_binding CHECK (payload_json ->> 'device_binding_id' = device_binding_id),
    CONSTRAINT outbox_payload_account CHECK (payload_json ->> 'platform_account_id' = platform_account_id)
    ,CONSTRAINT outbox_payload_fingerprint_hmac CHECK (length(payload_json ->> 'fingerprint_hmac_sha256') = 64 AND payload_json ->> 'fingerprint_hmac_sha256' ~ '^[a-f0-9]{64}$')
    ,CONSTRAINT outbox_payload_fingerprint_key CHECK (length(payload_json ->> 'fingerprint_key_id') = 38 AND payload_json ->> 'fingerprint_key_id' ~ '^fpkey_[a-f0-9]{32}$')
    ,CONSTRAINT outbox_payload_fingerprint_epoch CHECK ((payload_json ->> 'fingerprint_key_epoch')::bigint >= 1)
    ,CONSTRAINT outbox_payload_no_unkeyed_fingerprint CHECK (NOT (payload_json ? 'fingerprint_sha256'))
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_quarantine
(
    quarantine_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    idempotency_key text NOT NULL,
    mutation_kind text NOT NULL,
    existing_command_sha256 text NOT NULL,
    incoming_command_sha256 text NOT NULL,
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (idempotency_key, incoming_command_sha256),
    CONSTRAINT idempotency_quarantine_key_format CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_kind_known CHECK (mutation_kind IN ('register', 'capability-revision', 'retire')),
    CONSTRAINT idempotency_quarantine_existing_hash CHECK (length(existing_command_sha256) = 64 AND existing_command_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_incoming_hash CHECK (length(incoming_command_sha256) = 64 AND incoming_command_sha256 ~ '^[a-f0-9]{64}$')
);

CREATE INDEX IF NOT EXISTS outbox_pending_scope_idx
    ON __SCHEMA__.outbox(soul_id, device_binding_id, platform_account_id, created_at, outbox_id)
    WHERE dispatched_at IS NULL;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_append_only_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'append-only device-registry rows cannot be updated or deleted';
END;
$function$;

DROP TRIGGER IF EXISTS capability_revisions_append_only ON __SCHEMA__.capability_revisions;
CREATE TRIGGER capability_revisions_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.capability_revisions
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();

DROP TRIGGER IF EXISTS idempotency_receipts_append_only ON __SCHEMA__.idempotency_receipts;
CREATE TRIGGER idempotency_receipts_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.idempotency_receipts
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();

DROP TRIGGER IF EXISTS idempotency_quarantine_append_only ON __SCHEMA__.idempotency_quarantine;
CREATE TRIGGER idempotency_quarantine_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.idempotency_quarantine
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
