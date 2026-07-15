BEGIN;

CREATE SCHEMA IF NOT EXISTS factory_evidence;

CREATE TABLE IF NOT EXISTS factory_evidence.upgrade_stream (
    stream_id text PRIMARY KEY,
    last_sequence bigint NOT NULL CHECK (last_sequence >= 0),
    last_event_sha256 char(64) NOT NULL CHECK (last_event_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE TABLE IF NOT EXISTS factory_evidence.upgrade_event (
    event_id text PRIMARY KEY,
    stream_id text NOT NULL REFERENCES factory_evidence.upgrade_stream(stream_id),
    sequence bigint NOT NULL CHECK (sequence > 0),
    idempotency_key text NOT NULL CHECK (
        char_length(idempotency_key) = 69
        AND idempotency_key ~ '^idem_[0-9a-f]{64}$'
    ),
    command_sha256 char(64) NOT NULL CHECK (command_sha256 ~ '^[0-9a-f]{64}$'),
    payload_sha256 char(64) NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    previous_event_sha256 char(64) NOT NULL CHECK (previous_event_sha256 ~ '^[0-9a-f]{64}$'),
    event_sha256 char(64) NOT NULL CHECK (event_sha256 ~ '^[0-9a-f]{64}$'),
    event_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    inserted_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (stream_id, sequence),
    UNIQUE (stream_id, idempotency_key)
);

CREATE TABLE IF NOT EXISTS factory_evidence.upgrade_event_quarantine (
    quarantine_id text PRIMARY KEY,
    stream_id text NOT NULL,
    idempotency_key text NOT NULL CHECK (
        char_length(idempotency_key) = 69
        AND idempotency_key ~ '^idem_[0-9a-f]{64}$'
    ),
    existing_command_sha256 char(64) NOT NULL CHECK (existing_command_sha256 ~ '^[0-9a-f]{64}$'),
    conflicting_command_sha256 char(64) NOT NULL CHECK (conflicting_command_sha256 ~ '^[0-9a-f]{64}$'),
    reason text NOT NULL CHECK (reason = 'IDEMPOTENCY_KEY_CONTENT_CONFLICT'),
    record_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    inserted_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION factory_evidence.reject_upgrade_event_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'factory_evidence.upgrade_event is append-only';
END;
$$;

DROP TRIGGER IF EXISTS upgrade_event_append_only ON factory_evidence.upgrade_event;
CREATE TRIGGER upgrade_event_append_only
BEFORE UPDATE OR DELETE ON factory_evidence.upgrade_event
FOR EACH ROW EXECUTE FUNCTION factory_evidence.reject_upgrade_event_mutation();

DROP TRIGGER IF EXISTS upgrade_event_quarantine_append_only ON factory_evidence.upgrade_event_quarantine;
CREATE TRIGGER upgrade_event_quarantine_append_only
BEFORE UPDATE OR DELETE ON factory_evidence.upgrade_event_quarantine
FOR EACH ROW EXECUTE FUNCTION factory_evidence.reject_upgrade_event_mutation();

COMMIT;
