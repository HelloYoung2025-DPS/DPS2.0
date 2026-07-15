BEGIN;

SET LOCAL search_path = pg_catalog, public, factory_evidence;

DO $$
BEGIN
    IF current_setting('server_version_num')::integer < 180000 THEN
        RAISE EXCEPTION 'factory-evidence-ledger requires PostgreSQL 18 or newer';
    END IF;
END;
$$;

CREATE EXTENSION IF NOT EXISTS pgcrypto WITH SCHEMA public;

DO $$
DECLARE
    v_schema text;
BEGIN
    SELECT n.nspname INTO v_schema
      FROM pg_extension e
      JOIN pg_namespace n ON n.oid = e.extnamespace
     WHERE e.extname = 'pgcrypto';
    IF v_schema <> 'public' THEN
        RAISE EXCEPTION 'pgcrypto must be pinned to the public schema for fixed function resolution';
    END IF;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dps_factory_evidence_owner') THEN
        CREATE ROLE dps_factory_evidence_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dps_factory_evidence_runtime') THEN
        CREATE ROLE dps_factory_evidence_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'dps_factory_evidence_admin') THEN
        CREATE ROLE dps_factory_evidence_admin LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT;
    END IF;
END;
$$;

ALTER ROLE dps_factory_evidence_owner
    NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
ALTER ROLE dps_factory_evidence_runtime
    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
ALTER ROLE dps_factory_evidence_admin
    LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
          FROM pg_auth_members membership
          JOIN pg_roles granted_role ON granted_role.oid = membership.roleid
          JOIN pg_roles member_role ON member_role.oid = membership.member
         WHERE granted_role.rolname IN (
                   'dps_factory_evidence_owner',
                   'dps_factory_evidence_runtime',
                   'dps_factory_evidence_admin'
               )
            OR member_role.rolname IN (
                   'dps_factory_evidence_owner',
                   'dps_factory_evidence_runtime',
                   'dps_factory_evidence_admin'
               )
    ) THEN
        RAISE EXCEPTION 'factory evidence roles must not participate in role membership chains';
    END IF;
END;
$$;

ALTER TABLE factory_evidence.upgrade_stream
    ADD COLUMN IF NOT EXISTS head_event_id text;

ALTER TABLE factory_evidence.upgrade_event
    ADD COLUMN IF NOT EXISTS command_wire bytea,
    ADD COLUMN IF NOT EXISTS event_type text,
    ADD COLUMN IF NOT EXISTS source_module text,
    ADD COLUMN IF NOT EXISTS privacy_class text,
    ADD COLUMN IF NOT EXISTS occurred_at_text text,
    ADD COLUMN IF NOT EXISTS auth_wire bytea,
    ADD COLUMN IF NOT EXISTS auth_issuer text,
    ADD COLUMN IF NOT EXISTS auth_audience text,
    ADD COLUMN IF NOT EXISTS auth_scope text,
    ADD COLUMN IF NOT EXISTS auth_producer_module text,
    ADD COLUMN IF NOT EXISTS auth_issued_at bigint,
    ADD COLUMN IF NOT EXISTS auth_expires_at bigint,
    ADD COLUMN IF NOT EXISTS auth_revocation_epoch bigint,
    ADD COLUMN IF NOT EXISTS auth_nonce text,
    ADD COLUMN IF NOT EXISTS auth_key_id text,
    ADD COLUMN IF NOT EXISTS auth_signature char(64);

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM factory_evidence.upgrade_event
        WHERE command_wire IS NULL
           OR event_type IS NULL
           OR source_module IS NULL
           OR privacy_class IS NULL
           OR occurred_at_text IS NULL
           OR auth_wire IS NULL
           OR auth_issuer IS NULL
           OR auth_audience IS NULL
           OR auth_scope IS NULL
           OR auth_producer_module IS NULL
           OR auth_issued_at IS NULL
           OR auth_expires_at IS NULL
           OR auth_revocation_epoch IS NULL
           OR auth_nonce IS NULL
           OR auth_key_id IS NULL
           OR auth_signature IS NULL
    ) THEN
        RAISE EXCEPTION '002 cannot infer authenticated raw bytes for legacy rows; export, verify, and migrate them explicitly';
    END IF;
END;
$$;

ALTER TABLE factory_evidence.upgrade_event
    ALTER COLUMN command_wire SET NOT NULL,
    ALTER COLUMN event_type SET NOT NULL,
    ALTER COLUMN source_module SET NOT NULL,
    ALTER COLUMN privacy_class SET NOT NULL,
    ALTER COLUMN occurred_at_text SET NOT NULL,
    ALTER COLUMN auth_wire SET NOT NULL,
    ALTER COLUMN auth_issuer SET NOT NULL,
    ALTER COLUMN auth_audience SET NOT NULL,
    ALTER COLUMN auth_scope SET NOT NULL,
    ALTER COLUMN auth_producer_module SET NOT NULL,
    ALTER COLUMN auth_issued_at SET NOT NULL,
    ALTER COLUMN auth_expires_at SET NOT NULL,
    ALTER COLUMN auth_revocation_epoch SET NOT NULL,
    ALTER COLUMN auth_nonce SET NOT NULL,
    ALTER COLUMN auth_key_id SET NOT NULL,
    ALTER COLUMN auth_signature SET NOT NULL;

CREATE TABLE IF NOT EXISTS factory_evidence.append_auth_key_history (
    issuer text NOT NULL CHECK (issuer = 'dps-factory-auth-service'),
    key_id text NOT NULL CHECK (key_id = 'factory-evidence-append-v1'),
    revocation_epoch bigint NOT NULL CHECK (revocation_epoch >= 0),
    secret_key bytea NOT NULL CHECK (octet_length(secret_key) >= 32),
    installed_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    installed_by text NOT NULL,
    PRIMARY KEY (issuer, key_id, revocation_epoch)
);

CREATE OR REPLACE FUNCTION factory_evidence.jsonb_object_key_count(p_value jsonb)
RETURNS integer
LANGUAGE sql
IMMUTABLE
STRICT
AS $$
    SELECT count(*)::integer FROM jsonb_object_keys(p_value);
$$;

ALTER TABLE factory_evidence.upgrade_stream
    DROP CONSTRAINT IF EXISTS upgrade_stream_shape_ck;
ALTER TABLE factory_evidence.upgrade_event
    DROP CONSTRAINT IF EXISTS upgrade_event_projected_json_ck,
    DROP CONSTRAINT IF EXISTS upgrade_event_command_auth_ck;
ALTER TABLE factory_evidence.upgrade_event_quarantine
    DROP CONSTRAINT IF EXISTS upgrade_event_quarantine_json_ck;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'upgrade_stream_shape_ck'
           AND conrelid = 'factory_evidence.upgrade_stream'::regclass
    ) THEN
        ALTER TABLE factory_evidence.upgrade_stream ADD CONSTRAINT upgrade_stream_shape_ck CHECK (
            stream_id ~ '^[a-z0-9][a-z0-9._:-]{7,127}$'
            AND (
                (last_sequence = 0 AND last_event_sha256 = repeat('0', 64) AND head_event_id IS NULL)
                OR
                (last_sequence > 0 AND head_event_id ~ '^event-[0-9a-f]{32}$')
            )
        );
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'upgrade_event_projected_json_ck'
           AND conrelid = 'factory_evidence.upgrade_event'::regclass
    ) THEN
        ALTER TABLE factory_evidence.upgrade_event ADD CONSTRAINT upgrade_event_projected_json_ck CHECK (
            jsonb_typeof(event_json) = 'object'
            AND factory_evidence.jsonb_object_key_count(event_json) = 21
            AND event_json ->> 'schema_version' = '1.0.0'
            AND event_json ->> 'contract_id' = 'upgrade.event/v1'
            AND event_json ->> 'producer_module' = 'factory-evidence-ledger'
            AND event_json ->> 'event_id' = event_id
            AND event_json ->> 'stream_id' = stream_id
            AND jsonb_typeof(event_json -> 'sequence') = 'number'
            AND event_json ->> 'sequence' = sequence::text
            AND event_json ->> 'idempotency_key' = idempotency_key
            AND event_json ->> 'payload_sha256' = payload_sha256
            AND event_json ->> 'previous_event_sha256' = previous_event_sha256
            AND event_json ->> 'event_sha256' = event_sha256
            AND event_json ->> 'event_type' = event_type
            AND event_json ->> 'source_module' = source_module
            AND event_json ->> 'privacy_class' = privacy_class
            AND event_json ->> 'occurred_at' = occurred_at_text
            AND event_json ->> 'append_status' = 'APPENDED'
            AND event_type ~ '^[A-Z][A-Z0-9_]{1,63}$'
            AND source_module IN ('factory-release-controller', 'factory-rollback-controller')
            AND privacy_class = 'internal'
            AND event_id ~ '^event-[0-9a-f]{32}$'
            AND occurred_at = occurred_at_text::timestamptz
        );
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'upgrade_event_command_auth_ck'
           AND conrelid = 'factory_evidence.upgrade_event'::regclass
    ) THEN
        ALTER TABLE factory_evidence.upgrade_event ADD CONSTRAINT upgrade_event_command_auth_ck CHECK (
            command_sha256 = encode(digest(command_wire, 'sha256'), 'hex')
            AND jsonb_typeof(convert_from(command_wire, 'UTF8')::jsonb) = 'object'
            AND factory_evidence.jsonb_object_key_count(convert_from(command_wire, 'UTF8')::jsonb) = 15
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'contract_id' = 'upgrade.event.append/v1'
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'producer_module' = source_module
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'stream_id' = stream_id
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'idempotency_key' = idempotency_key
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'payload_sha256' = payload_sha256
            AND jsonb_typeof(convert_from(command_wire, 'UTF8')::jsonb -> 'expected_sequence') = 'number'
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'expected_sequence' = (sequence - 1)::text
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'event_type' = event_type
            AND convert_from(command_wire, 'UTF8')::jsonb ->> 'occurred_at' = occurred_at_text
            AND jsonb_typeof(convert_from(auth_wire, 'UTF8')::jsonb) = 'object'
            AND factory_evidence.jsonb_object_key_count(convert_from(auth_wire, 'UTF8')::jsonb) = 12
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'issuer' = auth_issuer
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'audience' = auth_audience
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'scope' = auth_scope
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'producer_module' = auth_producer_module
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'command_sha256' = command_sha256
            AND jsonb_typeof(convert_from(auth_wire, 'UTF8')::jsonb -> 'issued_at') = 'number'
            AND jsonb_typeof(convert_from(auth_wire, 'UTF8')::jsonb -> 'expires_at') = 'number'
            AND jsonb_typeof(convert_from(auth_wire, 'UTF8')::jsonb -> 'revocation_epoch') = 'number'
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'issued_at' = auth_issued_at::text
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'expires_at' = auth_expires_at::text
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'revocation_epoch' = auth_revocation_epoch::text
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'nonce' = auth_nonce
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'key_id' = auth_key_id
            AND convert_from(auth_wire, 'UTF8')::jsonb ->> 'signature' = auth_signature
            AND auth_issuer = 'dps-factory-auth-service'
            AND auth_audience = 'factory-evidence-ledger'
            AND auth_scope = 'factory:evidence:append'
            AND auth_producer_module = source_module
            AND auth_key_id = 'factory-evidence-append-v1'
            AND auth_nonce ~ '^auth_[0-9a-f]{32}$'
            AND auth_signature ~ '^[0-9a-f]{64}$'
            AND auth_expires_at > auth_issued_at
            AND auth_expires_at - auth_issued_at <= 300
        );
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
         WHERE conname = 'upgrade_event_quarantine_json_ck'
           AND conrelid = 'factory_evidence.upgrade_event_quarantine'::regclass
    ) THEN
        ALTER TABLE factory_evidence.upgrade_event_quarantine ADD CONSTRAINT upgrade_event_quarantine_json_ck CHECK (
            jsonb_typeof(record_json) = 'object'
            AND factory_evidence.jsonb_object_key_count(record_json) = 7
            AND record_json ->> 'quarantine_id' = quarantine_id
            AND record_json ->> 'stream_id' = stream_id
            AND record_json ->> 'idempotency_key' = idempotency_key
            AND record_json ->> 'existing_command_sha256' = existing_command_sha256
            AND record_json ->> 'conflicting_command_sha256' = conflicting_command_sha256
            AND record_json ->> 'reason' = reason
            AND (record_json ->> 'occurred_at')::timestamptz = occurred_at
            AND quarantine_id ~ '^quarantine-[0-9a-f]{32}$'
        );
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION factory_evidence.reject_immutable_write()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'INSERT' AND current_user = 'dps_factory_evidence_owner' THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION '% is append-only and accepts inserts only from its protected owner function', TG_TABLE_NAME;
END;
$$;

CREATE OR REPLACE FUNCTION factory_evidence.reject_stream_write()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP IN ('INSERT', 'UPDATE') AND current_user = 'dps_factory_evidence_owner' THEN
        RETURN NEW;
    END IF;
    RAISE EXCEPTION 'upgrade_stream is writable only by its protected owner function';
END;
$$;

CREATE OR REPLACE FUNCTION factory_evidence.reject_truncate()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION '% cannot be truncated', TG_TABLE_NAME;
END;
$$;

DROP TRIGGER IF EXISTS upgrade_event_append_only ON factory_evidence.upgrade_event;
CREATE TRIGGER upgrade_event_append_only
BEFORE INSERT OR UPDATE OR DELETE ON factory_evidence.upgrade_event
FOR EACH ROW EXECUTE FUNCTION factory_evidence.reject_immutable_write();

DROP TRIGGER IF EXISTS upgrade_event_no_truncate ON factory_evidence.upgrade_event;
CREATE TRIGGER upgrade_event_no_truncate
BEFORE TRUNCATE ON factory_evidence.upgrade_event
FOR EACH STATEMENT EXECUTE FUNCTION factory_evidence.reject_truncate();

DROP TRIGGER IF EXISTS upgrade_event_quarantine_append_only ON factory_evidence.upgrade_event_quarantine;
DROP FUNCTION IF EXISTS factory_evidence.reject_upgrade_event_mutation();
CREATE TRIGGER upgrade_event_quarantine_append_only
BEFORE INSERT OR UPDATE OR DELETE ON factory_evidence.upgrade_event_quarantine
FOR EACH ROW EXECUTE FUNCTION factory_evidence.reject_immutable_write();

DROP TRIGGER IF EXISTS upgrade_event_quarantine_no_truncate ON factory_evidence.upgrade_event_quarantine;
CREATE TRIGGER upgrade_event_quarantine_no_truncate
BEFORE TRUNCATE ON factory_evidence.upgrade_event_quarantine
FOR EACH STATEMENT EXECUTE FUNCTION factory_evidence.reject_truncate();

DROP TRIGGER IF EXISTS upgrade_stream_protected_write ON factory_evidence.upgrade_stream;
CREATE TRIGGER upgrade_stream_protected_write
BEFORE INSERT OR UPDATE OR DELETE ON factory_evidence.upgrade_stream
FOR EACH ROW EXECUTE FUNCTION factory_evidence.reject_stream_write();

DROP TRIGGER IF EXISTS upgrade_stream_no_truncate ON factory_evidence.upgrade_stream;
CREATE TRIGGER upgrade_stream_no_truncate
BEFORE TRUNCATE ON factory_evidence.upgrade_stream
FOR EACH STATEMENT EXECUTE FUNCTION factory_evidence.reject_truncate();

DROP TRIGGER IF EXISTS append_auth_key_append_only ON factory_evidence.append_auth_key_history;
CREATE TRIGGER append_auth_key_append_only
BEFORE INSERT OR UPDATE OR DELETE ON factory_evidence.append_auth_key_history
FOR EACH ROW EXECUTE FUNCTION factory_evidence.reject_immutable_write();

DROP TRIGGER IF EXISTS append_auth_key_no_truncate ON factory_evidence.append_auth_key_history;
CREATE TRIGGER append_auth_key_no_truncate
BEFORE TRUNCATE ON factory_evidence.append_auth_key_history
FOR EACH STATEMENT EXECUTE FUNCTION factory_evidence.reject_truncate();

CREATE OR REPLACE FUNCTION factory_evidence.assert_stream_head_consistency()
RETURNS trigger
LANGUAGE plpgsql
AS $$
DECLARE
    v_stream_id text;
    v_head factory_evidence.upgrade_stream%ROWTYPE;
    v_event_id text;
    v_sequence bigint;
    v_sha char(64);
BEGIN
    v_stream_id := CASE WHEN TG_TABLE_NAME = 'upgrade_event' THEN NEW.stream_id ELSE NEW.stream_id END;
    SELECT * INTO v_head FROM factory_evidence.upgrade_stream WHERE stream_id = v_stream_id;
    SELECT event_id, sequence, event_sha256
      INTO v_event_id, v_sequence, v_sha
      FROM factory_evidence.upgrade_event
     WHERE stream_id = v_stream_id
     ORDER BY sequence DESC
     LIMIT 1;
    IF v_head.last_sequence = 0 THEN
        IF v_event_id IS NOT NULL OR v_head.last_event_sha256 <> repeat('0', 64) OR v_head.head_event_id IS NOT NULL THEN
            RAISE EXCEPTION 'empty stream head is inconsistent';
        END IF;
    ELSIF v_event_id IS NULL
       OR v_head.last_sequence <> v_sequence
       OR v_head.last_event_sha256 <> v_sha
       OR v_head.head_event_id <> v_event_id THEN
        RAISE EXCEPTION 'stream head is inconsistent with ordered events';
    END IF;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS upgrade_event_head_consistency ON factory_evidence.upgrade_event;
CREATE CONSTRAINT TRIGGER upgrade_event_head_consistency
AFTER INSERT ON factory_evidence.upgrade_event
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION factory_evidence.assert_stream_head_consistency();

DROP TRIGGER IF EXISTS upgrade_stream_head_consistency ON factory_evidence.upgrade_stream;
CREATE CONSTRAINT TRIGGER upgrade_stream_head_consistency
AFTER INSERT OR UPDATE ON factory_evidence.upgrade_stream
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION factory_evidence.assert_stream_head_consistency();

CREATE OR REPLACE FUNCTION factory_evidence.install_append_auth_key(
    p_secret_key bytea,
    p_revocation_epoch bigint
)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, factory_evidence
AS $$
DECLARE
    v_latest bigint;
    v_existing_key bytea;
BEGIN
    IF session_user <> 'dps_factory_evidence_admin' THEN
        RAISE EXCEPTION 'append auth key installation requires the exact admin identity';
    END IF;
    IF p_secret_key IS NULL OR octet_length(p_secret_key) < 32 OR p_revocation_epoch < 0 THEN
        RAISE EXCEPTION 'append auth key material or epoch is invalid';
    END IF;
    PERFORM pg_advisory_xact_lock(73031, 20260715);
    SELECT max(revocation_epoch) INTO v_latest
      FROM factory_evidence.append_auth_key_history
     WHERE issuer = 'dps-factory-auth-service'
       AND key_id = 'factory-evidence-append-v1';
    IF v_latest = p_revocation_epoch THEN
        SELECT secret_key INTO v_existing_key
          FROM factory_evidence.append_auth_key_history
         WHERE issuer = 'dps-factory-auth-service'
           AND key_id = 'factory-evidence-append-v1'
           AND revocation_epoch = p_revocation_epoch;
        IF public.digest(v_existing_key, 'sha256') <> public.digest(p_secret_key, 'sha256') THEN
            RAISE EXCEPTION 'same append auth epoch has different key material';
        END IF;
        RETURN;
    END IF;
    IF v_latest IS NOT NULL AND p_revocation_epoch < v_latest THEN
        RAISE EXCEPTION 'append auth revocation epoch must increase';
    END IF;
    INSERT INTO factory_evidence.append_auth_key_history(
        issuer, key_id, revocation_epoch, secret_key, installed_by
    ) VALUES (
        'dps-factory-auth-service', 'factory-evidence-append-v1',
        p_revocation_epoch, p_secret_key, session_user
    );
END;
$$;

CREATE OR REPLACE FUNCTION factory_evidence.append_upgrade_event(
    p_command_wire bytea,
    p_event_json jsonb,
    p_auth_json jsonb
)
RETURNS TABLE(append_status text, event_json jsonb)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, factory_evidence
AS $$
DECLARE
    v_command jsonb;
    v_stream factory_evidence.upgrade_stream%ROWTYPE;
    v_existing factory_evidence.upgrade_event%ROWTYPE;
    v_command_sha text;
    v_expected_sequence bigint;
    v_key bytea;
    v_current_epoch bigint;
    v_auth_material text;
    v_signature text;
    v_record jsonb;
BEGIN
    IF session_user <> 'dps_factory_evidence_runtime' THEN
        RAISE EXCEPTION 'append requires the exact factory evidence runtime identity';
    END IF;
    IF p_command_wire IS NULL OR octet_length(p_command_wire) = 0 OR octet_length(p_command_wire) > 65536 THEN
        RAISE EXCEPTION 'command wire byte length is invalid';
    END IF;
    BEGIN
        v_command := convert_from(p_command_wire, 'UTF8')::jsonb;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION 'command wire is not valid UTF-8 JSON';
    END;
    IF jsonb_typeof(v_command) <> 'object'
       OR (SELECT count(*) FROM jsonb_object_keys(v_command)) <> 15
       OR NOT (v_command ?& ARRAY[
            'schema_version','contract_id','producer_module','soul_id','device_binding_id',
            'platform_account_id','trace_id','idempotency_key','occurred_at','privacy_class',
            'stream_id','expected_sequence','event_type','payload','payload_sha256'
       ])
       OR jsonb_typeof(p_event_json) <> 'object'
       OR (SELECT count(*) FROM jsonb_object_keys(p_event_json)) <> 21
       OR NOT (p_event_json ?& ARRAY[
            'schema_version','contract_id','producer_module','soul_id','device_binding_id',
            'platform_account_id','trace_id','idempotency_key','occurred_at','privacy_class',
            'event_id','stream_id','sequence','event_type','source_module','payload',
            'payload_sha256','previous_event_sha256','event_sha256','append_status'
       ])
       OR jsonb_typeof(p_auth_json) <> 'object'
       OR (SELECT count(*) FROM jsonb_object_keys(p_auth_json)) <> 12
       OR NOT (p_auth_json ?& ARRAY[
            'schema_version','issuer','audience','scope','producer_module','command_sha256',
            'issued_at','expires_at','revocation_epoch','nonce','key_id','signature'
       ]) THEN
        RAISE EXCEPTION 'command event or auth object shape is invalid';
    END IF;
    v_command_sha := encode(public.digest(p_command_wire, 'sha256'), 'hex');
    IF v_command ->> 'schema_version' <> '1.0.0'
       OR v_command ->> 'contract_id' <> 'upgrade.event.append/v1'
       OR v_command ->> 'producer_module' NOT IN ('factory-release-controller', 'factory-rollback-controller')
       OR v_command ->> 'privacy_class' <> 'internal'
       OR v_command ->> 'stream_id' !~ '^[a-z0-9][a-z0-9._:-]{7,127}$'
       OR v_command ->> 'trace_id' !~ '^trace_[0-9a-f]{32}$'
       OR v_command ->> 'idempotency_key' !~ '^idem_[0-9a-f]{64}$'
       OR v_command ->> 'payload_sha256' !~ '^[0-9a-f]{64}$'
       OR v_command ->> 'event_type' !~ '^[A-Z][A-Z0-9_]{1,63}$'
       OR v_command ->> 'occurred_at' !~ '^[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,6})?Z$'
       OR jsonb_typeof(v_command -> 'expected_sequence') <> 'number'
       OR jsonb_typeof(v_command -> 'payload') <> 'object'
       OR NOT (
            jsonb_typeof(v_command -> 'soul_id') = 'null'
            OR (jsonb_typeof(v_command -> 'soul_id') = 'string' AND v_command ->> 'soul_id' ~ '^soul_[0-9a-f]{64}$')
       )
       OR NOT (
            jsonb_typeof(v_command -> 'device_binding_id') = 'null'
            OR (jsonb_typeof(v_command -> 'device_binding_id') = 'string' AND v_command ->> 'device_binding_id' ~ '^db_[0-9a-f]{32}$')
       )
       OR NOT (
            jsonb_typeof(v_command -> 'platform_account_id') = 'null'
            OR (jsonb_typeof(v_command -> 'platform_account_id') = 'string' AND v_command ->> 'platform_account_id' ~ '^pa_[0-9a-f]{32}$')
       )
       OR v_command ->> 'expected_sequence' !~ '^(0|[1-9][0-9]*)$' THEN
        RAISE EXCEPTION 'command fields are invalid';
    END IF;
    PERFORM (v_command ->> 'occurred_at')::timestamptz;
    v_expected_sequence := (v_command ->> 'expected_sequence')::bigint;
    IF p_auth_json ->> 'schema_version' <> 'dps.factory-evidence-append-auth/v1'
       OR p_auth_json ->> 'issuer' <> 'dps-factory-auth-service'
       OR p_auth_json ->> 'audience' <> 'factory-evidence-ledger'
       OR p_auth_json ->> 'scope' <> 'factory:evidence:append'
       OR p_auth_json ->> 'producer_module' <> v_command ->> 'producer_module'
       OR p_auth_json ->> 'command_sha256' <> v_command_sha
       OR p_auth_json ->> 'key_id' <> 'factory-evidence-append-v1'
       OR p_auth_json ->> 'nonce' !~ '^auth_[0-9a-f]{32}$'
       OR p_auth_json ->> 'signature' !~ '^[0-9a-f]{64}$'
       OR jsonb_typeof(p_auth_json -> 'issued_at') <> 'number'
       OR jsonb_typeof(p_auth_json -> 'expires_at') <> 'number'
       OR jsonb_typeof(p_auth_json -> 'revocation_epoch') <> 'number'
       OR p_auth_json ->> 'issued_at' !~ '^(0|[1-9][0-9]*)$'
       OR p_auth_json ->> 'expires_at' !~ '^(0|[1-9][0-9]*)$'
       OR p_auth_json ->> 'revocation_epoch' !~ '^(0|[1-9][0-9]*)$' THEN
        RAISE EXCEPTION 'authorization fields are invalid';
    END IF;
    SELECT secret_key, revocation_epoch INTO v_key, v_current_epoch
      FROM factory_evidence.append_auth_key_history
     WHERE issuer = 'dps-factory-auth-service'
       AND key_id = 'factory-evidence-append-v1'
     ORDER BY revocation_epoch DESC
     LIMIT 1;
    IF v_key IS NULL OR v_current_epoch <> (p_auth_json ->> 'revocation_epoch')::bigint THEN
        RAISE EXCEPTION 'authorization key is missing or revoked';
    END IF;
    IF (p_auth_json ->> 'expires_at')::bigint <= (p_auth_json ->> 'issued_at')::bigint
       OR (p_auth_json ->> 'expires_at')::bigint - (p_auth_json ->> 'issued_at')::bigint > 300
       OR (p_auth_json ->> 'issued_at')::bigint > extract(epoch FROM clock_timestamp())::bigint + 5
       OR (p_auth_json ->> 'expires_at')::bigint < extract(epoch FROM clock_timestamp())::bigint THEN
        RAISE EXCEPTION 'authorization is expired or not current';
    END IF;
    v_auth_material := concat_ws('|',
        p_auth_json ->> 'schema_version', p_auth_json ->> 'issuer',
        p_auth_json ->> 'audience', p_auth_json ->> 'scope',
        p_auth_json ->> 'producer_module', p_auth_json ->> 'command_sha256',
        p_auth_json ->> 'issued_at', p_auth_json ->> 'expires_at',
        p_auth_json ->> 'revocation_epoch', p_auth_json ->> 'nonce',
        p_auth_json ->> 'key_id'
    );
    v_signature := encode(public.hmac(convert_to(v_auth_material, 'UTF8'), v_key, 'sha256'), 'hex');
    IF v_signature <> p_auth_json ->> 'signature' THEN
        RAISE EXCEPTION 'authorization signature is invalid';
    END IF;

    INSERT INTO factory_evidence.upgrade_stream(stream_id, last_sequence, last_event_sha256, head_event_id)
    VALUES (v_command ->> 'stream_id', 0, repeat('0', 64), NULL)
    ON CONFLICT (stream_id) DO NOTHING;
    SELECT * INTO v_stream
      FROM factory_evidence.upgrade_stream
     WHERE stream_id = v_command ->> 'stream_id'
     FOR UPDATE;
    SELECT * INTO v_existing
      FROM factory_evidence.upgrade_event
     WHERE stream_id = v_command ->> 'stream_id'
       AND idempotency_key = v_command ->> 'idempotency_key';
    IF FOUND THEN
        IF v_existing.command_sha256 = v_command_sha THEN
            RETURN QUERY SELECT 'IDEMPOTENT_REPLAY'::text, v_existing.event_json;
            RETURN;
        END IF;
        v_record := jsonb_build_object(
            'quarantine_id', 'quarantine-' || substr(encode(public.digest(convert_to(
                '{"conflicting_command_sha256":"' || v_command_sha ||
                '","existing_command_sha256":"' || v_existing.command_sha256 ||
                '","idempotency_key":"' || (v_command ->> 'idempotency_key') ||
                '","stream_id":"' || (v_command ->> 'stream_id') || '"}',
                'UTF8'
            ), 'sha256'), 'hex'), 1, 32),
            'stream_id', v_command ->> 'stream_id',
            'idempotency_key', v_command ->> 'idempotency_key',
            'existing_command_sha256', v_existing.command_sha256,
            'conflicting_command_sha256', v_command_sha,
            'reason', 'IDEMPOTENCY_KEY_CONTENT_CONFLICT',
            'occurred_at', v_command ->> 'occurred_at'
        );
        INSERT INTO factory_evidence.upgrade_event_quarantine(
            quarantine_id, stream_id, idempotency_key, existing_command_sha256,
            conflicting_command_sha256, reason, record_json, occurred_at
        ) VALUES (
            v_record ->> 'quarantine_id', v_record ->> 'stream_id',
            v_record ->> 'idempotency_key', v_record ->> 'existing_command_sha256',
            v_record ->> 'conflicting_command_sha256', v_record ->> 'reason',
            v_record, (v_record ->> 'occurred_at')::timestamptz
        ) ON CONFLICT (quarantine_id) DO NOTHING;
        RETURN QUERY SELECT 'IDEMPOTENCY_CONFLICT'::text, NULL::jsonb;
        RETURN;
    END IF;
    IF v_stream.last_sequence <> v_expected_sequence THEN
        RAISE EXCEPTION 'expected sequence %, actual %', v_expected_sequence, v_stream.last_sequence;
    END IF;
    IF p_event_json ->> 'previous_event_sha256' <> v_stream.last_event_sha256
       OR jsonb_typeof(p_event_json -> 'sequence') <> 'number'
       OR p_event_json ->> 'sequence' <> (v_expected_sequence + 1)::text
       OR p_event_json ->> 'schema_version' <> '1.0.0'
       OR p_event_json ->> 'contract_id' <> 'upgrade.event/v1'
       OR p_event_json ->> 'producer_module' <> 'factory-evidence-ledger'
       OR p_event_json ->> 'stream_id' <> v_command ->> 'stream_id'
       OR p_event_json ->> 'idempotency_key' <> v_command ->> 'idempotency_key'
       OR p_event_json ->> 'source_module' <> v_command ->> 'producer_module'
       OR (p_event_json -> 'soul_id') IS DISTINCT FROM (v_command -> 'soul_id')
       OR (p_event_json -> 'device_binding_id') IS DISTINCT FROM (v_command -> 'device_binding_id')
       OR (p_event_json -> 'platform_account_id') IS DISTINCT FROM (v_command -> 'platform_account_id')
       OR p_event_json ->> 'trace_id' <> v_command ->> 'trace_id'
       OR p_event_json ->> 'privacy_class' <> v_command ->> 'privacy_class'
       OR p_event_json -> 'payload' <> v_command -> 'payload'
       OR p_event_json ->> 'payload_sha256' <> v_command ->> 'payload_sha256'
       OR p_event_json ->> 'event_type' <> v_command ->> 'event_type'
       OR p_event_json ->> 'occurred_at' <> v_command ->> 'occurred_at'
       OR p_event_json ->> 'event_id' <> 'event-' || substr(encode(public.digest(convert_to(
            '{"idempotency_key":"' || (v_command ->> 'idempotency_key') ||
            '","stream_id":"' || (v_command ->> 'stream_id') || '"}',
            'UTF8'
       ), 'sha256'), 'hex'), 1, 32)
       OR p_event_json ->> 'append_status' <> 'APPENDED' THEN
        RAISE EXCEPTION 'event does not match authenticated command or locked stream head';
    END IF;
    INSERT INTO factory_evidence.upgrade_event(
        event_id, stream_id, sequence, idempotency_key, command_sha256,
        payload_sha256, previous_event_sha256, event_sha256, event_json,
        occurred_at, command_wire, event_type, source_module, privacy_class,
        occurred_at_text, auth_wire, auth_issuer, auth_audience, auth_scope,
        auth_producer_module, auth_issued_at, auth_expires_at,
        auth_revocation_epoch, auth_nonce, auth_key_id, auth_signature
    ) VALUES (
        p_event_json ->> 'event_id', p_event_json ->> 'stream_id',
        (p_event_json ->> 'sequence')::bigint, p_event_json ->> 'idempotency_key',
        v_command_sha, p_event_json ->> 'payload_sha256',
        p_event_json ->> 'previous_event_sha256', p_event_json ->> 'event_sha256',
        p_event_json, (p_event_json ->> 'occurred_at')::timestamptz,
        p_command_wire, p_event_json ->> 'event_type', p_event_json ->> 'source_module',
        p_event_json ->> 'privacy_class', p_event_json ->> 'occurred_at',
        convert_to(p_auth_json::text, 'UTF8'), p_auth_json ->> 'issuer',
        p_auth_json ->> 'audience', p_auth_json ->> 'scope',
        p_auth_json ->> 'producer_module', (p_auth_json ->> 'issued_at')::bigint,
        (p_auth_json ->> 'expires_at')::bigint,
        (p_auth_json ->> 'revocation_epoch')::bigint, p_auth_json ->> 'nonce',
        p_auth_json ->> 'key_id', p_auth_json ->> 'signature'
    );
    UPDATE factory_evidence.upgrade_stream
       SET last_sequence = (p_event_json ->> 'sequence')::bigint,
           last_event_sha256 = p_event_json ->> 'event_sha256',
           head_event_id = p_event_json ->> 'event_id'
     WHERE stream_id = p_event_json ->> 'stream_id';
    RETURN QUERY SELECT 'APPENDED'::text, p_event_json;
END;
$$;

CREATE OR REPLACE FUNCTION factory_evidence.read_upgrade_stream(p_stream_id text)
RETURNS TABLE(
    event_id text, stream_id text, sequence bigint, idempotency_key text,
    command_sha256 text, payload_sha256 text, previous_event_sha256 text,
    event_sha256 text, event_type text, source_module text, privacy_class text,
    occurred_at_text text, command_wire bytea, event_json jsonb
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, factory_evidence
AS $$
BEGIN
    IF session_user NOT IN ('dps_factory_evidence_runtime', 'dps_factory_evidence_admin') THEN
        RAISE EXCEPTION 'stream read requires the exact runtime or admin identity';
    END IF;
    RETURN QUERY
    SELECT e.event_id, e.stream_id, e.sequence, e.idempotency_key,
           e.command_sha256::text, e.payload_sha256::text,
           e.previous_event_sha256::text, e.event_sha256::text,
           e.event_type, e.source_module, e.privacy_class, e.occurred_at_text,
           e.command_wire, e.event_json
      FROM factory_evidence.upgrade_event e
     WHERE e.stream_id = p_stream_id
     ORDER BY e.sequence;
END;
$$;

CREATE OR REPLACE FUNCTION factory_evidence.read_upgrade_stream_head(p_stream_id text)
RETURNS TABLE(last_sequence bigint, last_event_sha256 text, head_event_id text)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, factory_evidence
AS $$
BEGIN
    IF session_user NOT IN ('dps_factory_evidence_runtime', 'dps_factory_evidence_admin') THEN
        RAISE EXCEPTION 'stream-head read requires the exact runtime or admin identity';
    END IF;
    RETURN QUERY
    SELECT s.last_sequence, s.last_event_sha256::text, s.head_event_id
      FROM factory_evidence.upgrade_stream s
     WHERE s.stream_id = p_stream_id;
END;
$$;

CREATE OR REPLACE FUNCTION factory_evidence.read_upgrade_event_quarantine(p_stream_id text)
RETURNS TABLE(record_json jsonb)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, factory_evidence
AS $$
BEGIN
    IF session_user NOT IN ('dps_factory_evidence_runtime', 'dps_factory_evidence_admin') THEN
        RAISE EXCEPTION 'quarantine read requires the exact runtime or admin identity';
    END IF;
    RETURN QUERY
    SELECT q.record_json
      FROM factory_evidence.upgrade_event_quarantine q
     WHERE q.stream_id = p_stream_id
     ORDER BY q.inserted_at, q.quarantine_id;
END;
$$;

ALTER SCHEMA factory_evidence OWNER TO dps_factory_evidence_owner;
ALTER TABLE factory_evidence.upgrade_stream OWNER TO dps_factory_evidence_owner;
ALTER TABLE factory_evidence.upgrade_event OWNER TO dps_factory_evidence_owner;
ALTER TABLE factory_evidence.upgrade_event_quarantine OWNER TO dps_factory_evidence_owner;
ALTER TABLE factory_evidence.append_auth_key_history OWNER TO dps_factory_evidence_owner;

ALTER FUNCTION factory_evidence.reject_immutable_write() OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.jsonb_object_key_count(jsonb) OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.reject_stream_write() OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.reject_truncate() OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.assert_stream_head_consistency() OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.install_append_auth_key(bytea, bigint) OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.append_upgrade_event(bytea, jsonb, jsonb) OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.read_upgrade_stream(text) OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.read_upgrade_stream_head(text) OWNER TO dps_factory_evidence_owner;
ALTER FUNCTION factory_evidence.read_upgrade_event_quarantine(text) OWNER TO dps_factory_evidence_owner;

REVOKE ALL ON SCHEMA factory_evidence FROM PUBLIC;
REVOKE ALL ON SCHEMA factory_evidence FROM dps_factory_evidence_runtime, dps_factory_evidence_admin;
REVOKE ALL ON ALL TABLES IN SCHEMA factory_evidence FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA factory_evidence FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA factory_evidence FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA factory_evidence FROM dps_factory_evidence_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA factory_evidence FROM dps_factory_evidence_admin;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA factory_evidence FROM dps_factory_evidence_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA factory_evidence FROM dps_factory_evidence_admin;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA factory_evidence FROM dps_factory_evidence_runtime;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA factory_evidence FROM dps_factory_evidence_admin;

GRANT USAGE ON SCHEMA factory_evidence TO dps_factory_evidence_runtime, dps_factory_evidence_admin;
GRANT EXECUTE ON FUNCTION factory_evidence.append_upgrade_event(bytea, jsonb, jsonb)
    TO dps_factory_evidence_runtime;
GRANT EXECUTE ON FUNCTION factory_evidence.read_upgrade_stream(text),
                          factory_evidence.read_upgrade_stream_head(text),
                          factory_evidence.read_upgrade_event_quarantine(text)
    TO dps_factory_evidence_runtime, dps_factory_evidence_admin;
GRANT EXECUTE ON FUNCTION factory_evidence.install_append_auth_key(bytea, bigint)
    TO dps_factory_evidence_admin;

ALTER DEFAULT PRIVILEGES FOR ROLE dps_factory_evidence_owner IN SCHEMA factory_evidence
    REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE dps_factory_evidence_owner IN SCHEMA factory_evidence
    REVOKE ALL ON FUNCTIONS FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE dps_factory_evidence_owner IN SCHEMA factory_evidence
    REVOKE ALL ON SEQUENCES FROM PUBLIC;

COMMIT;
