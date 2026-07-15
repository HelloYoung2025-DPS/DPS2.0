CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE OR REPLACE FUNCTION __SCHEMA__.jsonb_has_exact_keys(
    document jsonb,
    expected_keys text[])
RETURNS boolean
LANGUAGE sql
IMMUTABLE
AS $function$
    SELECT jsonb_typeof(document) = 'object'
       AND document ?& expected_keys
       AND NOT EXISTS (
           SELECT 1
           FROM jsonb_object_keys(document) AS actual(key)
           WHERE NOT (actual.key = ANY(expected_keys))
       );
$function$;

CREATE TABLE IF NOT EXISTS __SCHEMA__.audit_events (
    audit_event_id uuid PRIMARY KEY,
    subject_id uuid NOT NULL,
    source_receipt_id uuid NOT NULL UNIQUE,
    soul_id text NOT NULL CHECK (char_length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (char_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    platform_account_id text NOT NULL CHECK (char_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[0-9a-f]{32}$'),
    trace_id text NOT NULL CHECK (char_length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    idempotency_key text NOT NULL CHECK (char_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    occurred_at timestamptz NOT NULL,
    outcome text NOT NULL CHECK (outcome IN ('SUCCESS', 'FAILED', 'UNKNOWN_OUTCOME')),
    result_code text NOT NULL CHECK (result_code ~ '^[A-Za-z0-9._:-]{1,128}$'),
    verification_class text NOT NULL CHECK (verification_class IN ('verified', 'failed', 'unknown')),
    evidence_digest text NOT NULL CHECK (evidence_digest ~ '^[0-9a-f]{64}$'),
    source_receipt_sha256 text NOT NULL CHECK (source_receipt_sha256 ~ '^[0-9a-f]{64}$'),
    release_bom_sha256 text NOT NULL CHECK (release_bom_sha256 ~ '^[0-9a-f]{64}$'),
    event_integrity_sha256 text NOT NULL CHECK (event_integrity_sha256 ~ '^[0-9a-f]{64}$'),
    record_sha256 text NOT NULL CHECK (record_sha256 ~ '^[0-9a-f]{64}$'),
    event_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (soul_id, device_binding_id, platform_account_id, idempotency_key),
    CHECK (__SCHEMA__.jsonb_has_exact_keys(
        event_json,
        ARRAY[
            'schema_version', 'contract_id', 'producer_module', 'audit_event_id',
            'subject_id', 'soul_id', 'device_binding_id', 'platform_account_id',
            'trace_id', 'idempotency_key', 'occurred_at', 'privacy_class',
            'event_type', 'outcome', 'source_contract_id', 'evidence_digest', 'labels'
        ]::text[])),
    CHECK (__SCHEMA__.jsonb_has_exact_keys(
        event_json -> 'labels',
        ARRAY['result_code', 'verification_class']::text[])),
    CHECK (event_json ->> 'schema_version' IS NOT DISTINCT FROM '1.0.0'),
    CHECK (event_json ->> 'contract_id' IS NOT DISTINCT FROM 'audit.event/v1'),
    CHECK (event_json ->> 'producer_module' IS NOT DISTINCT FROM 'audit-metrics'),
    CHECK ((event_json ->> 'audit_event_id')::uuid IS NOT DISTINCT FROM audit_event_id),
    CHECK ((event_json ->> 'subject_id')::uuid IS NOT DISTINCT FROM subject_id),
    CHECK (event_json ->> 'soul_id' IS NOT DISTINCT FROM soul_id),
    CHECK (event_json ->> 'device_binding_id' IS NOT DISTINCT FROM device_binding_id),
    CHECK (event_json ->> 'platform_account_id' IS NOT DISTINCT FROM platform_account_id),
    CHECK (event_json ->> 'trace_id' IS NOT DISTINCT FROM trace_id),
    CHECK (event_json ->> 'idempotency_key' IS NOT DISTINCT FROM idempotency_key),
    CHECK ((event_json ->> 'occurred_at')::timestamptz IS NOT DISTINCT FROM occurred_at),
    CHECK (event_json ->> 'privacy_class' IS NOT DISTINCT FROM 'internal'),
    CHECK (event_json ->> 'event_type' IS NOT DISTINCT FROM 'command.completed'),
    CHECK (event_json ->> 'outcome' IS NOT DISTINCT FROM outcome),
    CHECK (event_json ->> 'source_contract_id' IS NOT DISTINCT FROM 'command.receipt/v1'),
    CHECK (event_json ->> 'evidence_digest' IS NOT DISTINCT FROM evidence_digest),
    CHECK (event_json -> 'labels' ->> 'result_code' IS NOT DISTINCT FROM result_code),
    CHECK (event_json -> 'labels' ->> 'verification_class' IS NOT DISTINCT FROM verification_class),
    CHECK (
        (outcome = 'SUCCESS' AND verification_class = 'verified')
        OR (outcome = 'FAILED' AND verification_class = 'failed')
        OR (outcome = 'UNKNOWN_OUTCOME' AND verification_class = 'unknown')
    )
);

CREATE INDEX IF NOT EXISTS ix_audit_events_exact_scope_order
    ON __SCHEMA__.audit_events
        (soul_id, device_binding_id, platform_account_id, occurred_at, audit_event_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.audit_quarantine (
    quarantine_id uuid PRIMARY KEY,
    incoming_audit_event_id uuid NOT NULL,
    existing_audit_event_id uuid NOT NULL REFERENCES __SCHEMA__.audit_events(audit_event_id),
    conflict_key_sha256 text NOT NULL CHECK (conflict_key_sha256 ~ '^[0-9a-f]{64}$'),
    existing_record_sha256 text NOT NULL CHECK (existing_record_sha256 ~ '^[0-9a-f]{64}$'),
    incoming_record_sha256 text NOT NULL CHECK (incoming_record_sha256 ~ '^[0-9a-f]{64}$'),
    scope_sha256 text NOT NULL CHECK (scope_sha256 ~ '^[0-9a-f]{64}$'),
    idempotency_sha256 text NOT NULL CHECK (idempotency_sha256 ~ '^[0-9a-f]{64}$'),
    reason text NOT NULL CHECK (reason IN (
        'event_id_and_scoped_idempotency_digest_conflict',
        'event_id_digest_conflict',
        'scoped_idempotency_digest_conflict'
    )),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (conflict_key_sha256, existing_record_sha256, incoming_record_sha256, reason)
);

CREATE INDEX IF NOT EXISTS ix_audit_quarantine_scope_created
    ON __SCHEMA__.audit_quarantine (scope_sha256, created_at, quarantine_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.audit_relay_trust_states (
    revision bigint PRIMARY KEY CHECK (revision > 0),
    state_id uuid NOT NULL UNIQUE,
    schema_version text NOT NULL CHECK (schema_version = '1.0.0'),
    contract_id text NOT NULL CHECK (contract_id = 'audit.relay-trust-state/v1'),
    active_release_bom_sha256 text NOT NULL CHECK (active_release_bom_sha256 ~ '^[0-9a-f]{64}$'),
    relay_key_id text NOT NULL CHECK (relay_key_id ~ '^[A-Za-z0-9._:-]{1,128}$'),
    relay_public_key_sha256 text NOT NULL CHECK (relay_public_key_sha256 ~ '^[0-9a-f]{64}$'),
    relay_key_status text NOT NULL CHECK (relay_key_status IN ('ACTIVE', 'REVOKED')),
    valid_from timestamptz NOT NULL,
    valid_until timestamptz NOT NULL CHECK (valid_until > valid_from),
    signature_base64 text NOT NULL CHECK (signature_base64 ~ '^[A-Za-z0-9+/]{86}==$'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION __SCHEMA__.serialize_audit_relay_trust_state_append()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    PERFORM pg_advisory_xact_lock(
        hashtextextended('dps.audit.relay-trust-state/v1', 0));
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS audit_relay_trust_states_serialize_append ON __SCHEMA__.audit_relay_trust_states;
CREATE TRIGGER audit_relay_trust_states_serialize_append
BEFORE INSERT ON __SCHEMA__.audit_relay_trust_states
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.serialize_audit_relay_trust_state_append();

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_audit_event_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'audit_events is append-only';
END;
$function$;

DROP TRIGGER IF EXISTS audit_events_append_only ON __SCHEMA__.audit_events;
CREATE TRIGGER audit_events_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.audit_events
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_audit_event_mutation();

DROP TRIGGER IF EXISTS audit_events_no_truncate ON __SCHEMA__.audit_events;
CREATE TRIGGER audit_events_no_truncate
BEFORE TRUNCATE ON __SCHEMA__.audit_events
FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_audit_event_mutation();

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_audit_quarantine_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'audit_quarantine is append-only';
END;
$function$;

DROP TRIGGER IF EXISTS audit_quarantine_append_only ON __SCHEMA__.audit_quarantine;
CREATE TRIGGER audit_quarantine_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.audit_quarantine
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_audit_quarantine_mutation();

DROP TRIGGER IF EXISTS audit_quarantine_no_truncate ON __SCHEMA__.audit_quarantine;
CREATE TRIGGER audit_quarantine_no_truncate
BEFORE TRUNCATE ON __SCHEMA__.audit_quarantine
FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_audit_quarantine_mutation();

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_audit_relay_trust_state_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'audit_relay_trust_states is append-only';
END;
$function$;

DROP TRIGGER IF EXISTS audit_relay_trust_states_append_only ON __SCHEMA__.audit_relay_trust_states;
CREATE TRIGGER audit_relay_trust_states_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.audit_relay_trust_states
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_audit_relay_trust_state_mutation();

DROP TRIGGER IF EXISTS audit_relay_trust_states_no_truncate ON __SCHEMA__.audit_relay_trust_states;
CREATE TRIGGER audit_relay_trust_states_no_truncate
BEFORE TRUNCATE ON __SCHEMA__.audit_relay_trust_states
FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_audit_relay_trust_state_mutation();
