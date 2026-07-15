-- The proposed pre-release schema previously stored an unkeyed fingerprint_sha256.
-- There is no trustworthy way to derive an HMAC or its key provenance from that digest.
-- Empty old schemas may converge; any populated old schema fails closed and requires an
-- explicitly reviewed re-attestation/import using the original authorized device source.
DO $migration$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = '__SCHEMA__'
          AND table_name = 'devices'
          AND column_name = 'fingerprint_sha256')
    THEN
        IF EXISTS (SELECT 1 FROM __SCHEMA__.devices)
           OR EXISTS (SELECT 1 FROM __SCHEMA__.capability_revisions)
           OR EXISTS (SELECT 1 FROM __SCHEMA__.idempotency_receipts)
           OR EXISTS (SELECT 1 FROM __SCHEMA__.outbox)
        THEN
            RAISE EXCEPTION USING
                ERRCODE = '55000',
                MESSAGE = 'device-registry migration refused: populated fingerprint_sha256 rows cannot be converted to keyed fingerprint HMAC truth';
        END IF;

        ALTER TABLE __SCHEMA__.devices DROP COLUMN fingerprint_sha256 CASCADE;
    END IF;
END;
$migration$;

ALTER TABLE __SCHEMA__.devices
    ADD COLUMN IF NOT EXISTS fingerprint_hmac_sha256 text,
    ADD COLUMN IF NOT EXISTS fingerprint_key_id text,
    ADD COLUMN IF NOT EXISTS fingerprint_key_epoch bigint;

ALTER TABLE __SCHEMA__.devices
    ALTER COLUMN fingerprint_hmac_sha256 SET NOT NULL,
    ALTER COLUMN fingerprint_key_id SET NOT NULL,
    ALTER COLUMN fingerprint_key_epoch SET NOT NULL;

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

DO $validation$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM __SCHEMA__.devices
        WHERE length(device_id) <> 39
           OR device_id !~ '^device_[a-f0-9]{32}$'
           OR length(fingerprint_hmac_sha256) <> 64
           OR fingerprint_hmac_sha256 !~ '^[a-f0-9]{64}$'
           OR length(fingerprint_key_id) <> 38
           OR fingerprint_key_id !~ '^fpkey_[a-f0-9]{32}$'
           OR fingerprint_key_epoch < 1
           OR length(registration_soul_id) <> 69
           OR registration_soul_id !~ '^soul_[a-f0-9]{64}$'
           OR length(registration_device_binding_id) <> 35
           OR registration_device_binding_id !~ '^db_[a-f0-9]{32}$'
           OR length(registration_platform_account_id) <> 35
           OR registration_platform_account_id !~ '^pa_[a-f0-9]{32}$')
       OR EXISTS (
        SELECT 1
        FROM __SCHEMA__.capability_revisions
        WHERE length(device_id) <> 39
           OR device_id !~ '^device_[a-f0-9]{32}$'
           OR length(soul_id) <> 69
           OR soul_id !~ '^soul_[a-f0-9]{64}$'
           OR length(device_binding_id) <> 35
           OR device_binding_id !~ '^db_[a-f0-9]{32}$'
           OR length(platform_account_id) <> 35
           OR platform_account_id !~ '^pa_[a-f0-9]{32}$'
           OR length(trace_id) <> 38
           OR trace_id !~ '^trace_[a-f0-9]{32}$'
           OR length(idempotency_key) <> 69
           OR idempotency_key !~ '^idem_[a-f0-9]{64}$'
           OR length(payload_sha256) <> 64
           OR payload_sha256 !~ '^[a-f0-9]{64}$'
           OR NOT __SCHEMA__.device_capabilities_are_canonical(capabilities)
           OR result_json ->> 'schema_version' IS DISTINCT FROM '1.0.0'
           OR NOT (result_json ?& ARRAY[
                'fingerprint_hmac_sha256',
                'fingerprint_key_id',
                'fingerprint_key_epoch'])
           OR length(result_json ->> 'fingerprint_hmac_sha256') IS DISTINCT FROM 64
           OR result_json ->> 'fingerprint_hmac_sha256' !~ '^[a-f0-9]{64}$'
           OR length(result_json ->> 'fingerprint_key_id') IS DISTINCT FROM 38
           OR result_json ->> 'fingerprint_key_id' !~ '^fpkey_[a-f0-9]{32}$'
           OR result_json ? 'fingerprint_sha256')
       OR EXISTS (
        SELECT 1
        FROM __SCHEMA__.binding_reservations
        WHERE length(reservation_id) <> 69
           OR reservation_id !~ '^bres_[a-f0-9]{64}$'
           OR length(device_id) <> 39
           OR device_id !~ '^device_[a-f0-9]{32}$'
           OR length(soul_id) <> 69
           OR soul_id !~ '^soul_[a-f0-9]{64}$'
           OR length(device_binding_id) <> 35
           OR device_binding_id !~ '^db_[a-f0-9]{32}$'
           OR length(platform_account_id) <> 35
           OR platform_account_id !~ '^pa_[a-f0-9]{32}$'
           OR length(trace_id) <> 38
           OR trace_id !~ '^trace_[a-f0-9]{32}$')
       OR EXISTS (
        SELECT 1
        FROM __SCHEMA__.idempotency_receipts
        WHERE length(idempotency_key) <> 69
           OR idempotency_key !~ '^idem_[a-f0-9]{64}$'
           OR length(command_sha256) <> 64
           OR command_sha256 !~ '^[a-f0-9]{64}$'
           OR length(device_id) <> 39
           OR device_id !~ '^device_[a-f0-9]{32}$'
           OR result_json ->> 'schema_version' IS DISTINCT FROM '1.0.0'
           OR NOT (result_json ?& ARRAY[
                'fingerprint_hmac_sha256',
                'fingerprint_key_id',
                'fingerprint_key_epoch'])
           OR length(result_json ->> 'fingerprint_hmac_sha256') IS DISTINCT FROM 64
           OR result_json ->> 'fingerprint_hmac_sha256' !~ '^[a-f0-9]{64}$'
           OR length(result_json ->> 'fingerprint_key_id') IS DISTINCT FROM 38
           OR result_json ->> 'fingerprint_key_id' !~ '^fpkey_[a-f0-9]{32}$'
           OR result_json ? 'fingerprint_sha256')
       OR EXISTS (
        SELECT 1
        FROM __SCHEMA__.outbox
        WHERE length(device_id) <> 39
           OR device_id !~ '^device_[a-f0-9]{32}$'
           OR length(soul_id) <> 69
           OR soul_id !~ '^soul_[a-f0-9]{64}$'
           OR length(device_binding_id) <> 35
           OR device_binding_id !~ '^db_[a-f0-9]{32}$'
           OR length(platform_account_id) <> 35
           OR platform_account_id !~ '^pa_[a-f0-9]{32}$'
           OR length(trace_id) <> 38
           OR trace_id !~ '^trace_[a-f0-9]{32}$'
           OR length(idempotency_key) <> 69
           OR idempotency_key !~ '^idem_[a-f0-9]{64}$'
           OR length(payload_sha256) <> 64
           OR payload_sha256 !~ '^[a-f0-9]{64}$'
           OR payload_json ->> 'schema_version' IS DISTINCT FROM '1.0.0'
           OR NOT (payload_json ?& ARRAY[
                'fingerprint_hmac_sha256',
                'fingerprint_key_id',
                'fingerprint_key_epoch'])
           OR length(payload_json ->> 'fingerprint_hmac_sha256') IS DISTINCT FROM 64
           OR payload_json ->> 'fingerprint_hmac_sha256' !~ '^[a-f0-9]{64}$'
           OR length(payload_json ->> 'fingerprint_key_id') IS DISTINCT FROM 38
           OR payload_json ->> 'fingerprint_key_id' !~ '^fpkey_[a-f0-9]{32}$'
           OR payload_json ? 'fingerprint_sha256')
       OR EXISTS (
        SELECT 1
        FROM __SCHEMA__.idempotency_quarantine
        WHERE length(idempotency_key) <> 69
           OR idempotency_key !~ '^idem_[a-f0-9]{64}$'
           OR length(existing_command_sha256) <> 64
           OR existing_command_sha256 !~ '^[a-f0-9]{64}$'
           OR length(incoming_command_sha256) <> 64
           OR incoming_command_sha256 !~ '^[a-f0-9]{64}$')
    THEN
        RAISE EXCEPTION USING
            ERRCODE = '55000',
            MESSAGE = 'device-registry migration refused: rows do not satisfy the keyed fingerprint and canonical v1 contract';
    END IF;
END;
$validation$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_devices_keyed_fingerprint_v1
    ON __SCHEMA__.devices(fingerprint_key_id, fingerprint_key_epoch, fingerprint_hmac_sha256);

DO $constraints$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'devices_keyed_fingerprint_contract_v1'
          AND conrelid = '__SCHEMA__.devices'::regclass)
    THEN
        ALTER TABLE __SCHEMA__.devices
            ADD CONSTRAINT devices_keyed_fingerprint_contract_v1 CHECK (
                length(fingerprint_hmac_sha256) = 64
                AND fingerprint_hmac_sha256 ~ '^[a-f0-9]{64}$'
                AND length(fingerprint_key_id) = 38
                AND fingerprint_key_id ~ '^fpkey_[a-f0-9]{32}$'
                AND fingerprint_key_epoch >= 1);
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'devices_scope_contract_v1'
          AND conrelid = '__SCHEMA__.devices'::regclass)
    THEN
        ALTER TABLE __SCHEMA__.devices
            ADD CONSTRAINT devices_scope_contract_v1 CHECK (
                length(device_id) = 39
                AND device_id ~ '^device_[a-f0-9]{32}$'
                AND length(registration_soul_id) = 69
                AND registration_soul_id ~ '^soul_[a-f0-9]{64}$'
                AND length(registration_device_binding_id) = 35
                AND registration_device_binding_id ~ '^db_[a-f0-9]{32}$'
                AND length(registration_platform_account_id) = 35
                AND registration_platform_account_id ~ '^pa_[a-f0-9]{32}$');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'capability_revisions_contract_v1'
          AND conrelid = '__SCHEMA__.capability_revisions'::regclass)
    THEN
        ALTER TABLE __SCHEMA__.capability_revisions
            ADD CONSTRAINT capability_revisions_contract_v1 CHECK (
                length(device_id) = 39
                AND device_id ~ '^device_[a-f0-9]{32}$'
                AND length(soul_id) = 69
                AND soul_id ~ '^soul_[a-f0-9]{64}$'
                AND length(device_binding_id) = 35
                AND device_binding_id ~ '^db_[a-f0-9]{32}$'
                AND length(platform_account_id) = 35
                AND platform_account_id ~ '^pa_[a-f0-9]{32}$'
                AND length(trace_id) = 38
                AND trace_id ~ '^trace_[a-f0-9]{32}$'
                AND length(idempotency_key) = 69
                AND idempotency_key ~ '^idem_[a-f0-9]{64}$'
                AND length(payload_sha256) = 64
                AND payload_sha256 ~ '^[a-f0-9]{64}$'
                AND __SCHEMA__.device_capabilities_are_canonical(capabilities)
                AND result_json ->> 'schema_version' = '1.0.0'
                AND result_json ?& ARRAY[
                    'fingerprint_hmac_sha256',
                    'fingerprint_key_id',
                    'fingerprint_key_epoch']
                AND length(result_json ->> 'fingerprint_hmac_sha256') IS NOT DISTINCT FROM 64
                AND result_json ->> 'fingerprint_hmac_sha256' ~ '^[a-f0-9]{64}$'
                AND length(result_json ->> 'fingerprint_key_id') IS NOT DISTINCT FROM 38
                AND result_json ->> 'fingerprint_key_id' ~ '^fpkey_[a-f0-9]{32}$'
                AND NOT (result_json ? 'fingerprint_sha256'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'binding_reservations_scope_contract_v1'
          AND conrelid = '__SCHEMA__.binding_reservations'::regclass)
    THEN
        ALTER TABLE __SCHEMA__.binding_reservations
            ADD CONSTRAINT binding_reservations_scope_contract_v1 CHECK (
                length(reservation_id) = 69
                AND reservation_id ~ '^bres_[a-f0-9]{64}$'
                AND length(device_id) = 39
                AND device_id ~ '^device_[a-f0-9]{32}$'
                AND length(soul_id) = 69
                AND soul_id ~ '^soul_[a-f0-9]{64}$'
                AND length(device_binding_id) = 35
                AND device_binding_id ~ '^db_[a-f0-9]{32}$'
                AND length(platform_account_id) = 35
                AND platform_account_id ~ '^pa_[a-f0-9]{32}$'
                AND length(trace_id) = 38
                AND trace_id ~ '^trace_[a-f0-9]{32}$');
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'idempotency_receipts_contract_v1'
          AND conrelid = '__SCHEMA__.idempotency_receipts'::regclass)
    THEN
        ALTER TABLE __SCHEMA__.idempotency_receipts
            ADD CONSTRAINT idempotency_receipts_contract_v1 CHECK (
                length(idempotency_key) = 69
                AND idempotency_key ~ '^idem_[a-f0-9]{64}$'
                AND length(command_sha256) = 64
                AND command_sha256 ~ '^[a-f0-9]{64}$'
                AND length(device_id) = 39
                AND device_id ~ '^device_[a-f0-9]{32}$'
                AND result_json ->> 'schema_version' = '1.0.0'
                AND result_json ?& ARRAY[
                    'fingerprint_hmac_sha256',
                    'fingerprint_key_id',
                    'fingerprint_key_epoch']
                AND length(result_json ->> 'fingerprint_hmac_sha256') IS NOT DISTINCT FROM 64
                AND result_json ->> 'fingerprint_hmac_sha256' ~ '^[a-f0-9]{64}$'
                AND length(result_json ->> 'fingerprint_key_id') IS NOT DISTINCT FROM 38
                AND result_json ->> 'fingerprint_key_id' ~ '^fpkey_[a-f0-9]{32}$'
                AND NOT (result_json ? 'fingerprint_sha256'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'outbox_contract_v1'
          AND conrelid = '__SCHEMA__.outbox'::regclass)
    THEN
        ALTER TABLE __SCHEMA__.outbox
            ADD CONSTRAINT outbox_contract_v1 CHECK (
                length(device_id) = 39
                AND device_id ~ '^device_[a-f0-9]{32}$'
                AND length(soul_id) = 69
                AND soul_id ~ '^soul_[a-f0-9]{64}$'
                AND length(device_binding_id) = 35
                AND device_binding_id ~ '^db_[a-f0-9]{32}$'
                AND length(platform_account_id) = 35
                AND platform_account_id ~ '^pa_[a-f0-9]{32}$'
                AND length(trace_id) = 38
                AND trace_id ~ '^trace_[a-f0-9]{32}$'
                AND length(idempotency_key) = 69
                AND idempotency_key ~ '^idem_[a-f0-9]{64}$'
                AND length(payload_sha256) = 64
                AND payload_sha256 ~ '^[a-f0-9]{64}$'
                AND payload_json ->> 'schema_version' = '1.0.0'
                AND payload_json ?& ARRAY[
                    'fingerprint_hmac_sha256',
                    'fingerprint_key_id',
                    'fingerprint_key_epoch']
                AND length(payload_json ->> 'fingerprint_hmac_sha256') IS NOT DISTINCT FROM 64
                AND payload_json ->> 'fingerprint_hmac_sha256' ~ '^[a-f0-9]{64}$'
                AND length(payload_json ->> 'fingerprint_key_id') IS NOT DISTINCT FROM 38
                AND payload_json ->> 'fingerprint_key_id' ~ '^fpkey_[a-f0-9]{32}$'
                AND NOT (payload_json ? 'fingerprint_sha256'));
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'idempotency_quarantine_contract_v1'
          AND conrelid = '__SCHEMA__.idempotency_quarantine'::regclass)
    THEN
        ALTER TABLE __SCHEMA__.idempotency_quarantine
            ADD CONSTRAINT idempotency_quarantine_contract_v1 CHECK (
                length(idempotency_key) = 69
                AND idempotency_key ~ '^idem_[a-f0-9]{64}$'
                AND length(existing_command_sha256) = 64
                AND existing_command_sha256 ~ '^[a-f0-9]{64}$'
                AND length(incoming_command_sha256) = 64
                AND incoming_command_sha256 ~ '^[a-f0-9]{64}$');
    END IF;
END;
$constraints$;
