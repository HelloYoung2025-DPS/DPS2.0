-- Existing non-empty pre-release databases cannot be upgraded by fabricating
-- authorization evidence. Export/re-authorize through an externally signed
-- evidence flow, or retire the pre-release schema before applying this gate.
ALTER TABLE __SCHEMA__.accounts
    ADD COLUMN IF NOT EXISTS alias_key_epoch bigint,
    ADD COLUMN IF NOT EXISTS authorization_evidence_sha256 text,
    ADD COLUMN IF NOT EXISTS authorization_evidence_json text;

ALTER TABLE __SCHEMA__.authorization_revisions
    ADD COLUMN IF NOT EXISTS authorization_evidence_sha256 text,
    ADD COLUMN IF NOT EXISTS authorization_evidence_json text;

DO $block$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM __SCHEMA__.accounts
        WHERE alias_key_epoch IS NULL
           OR authorization_evidence_sha256 IS NULL
           OR authorization_evidence_json IS NULL
    ) OR EXISTS (
        SELECT 1
        FROM __SCHEMA__.authorization_revisions
        WHERE authorization_evidence_sha256 IS NULL
           OR authorization_evidence_json IS NULL
    ) THEN
        RAISE EXCEPTION 'pre-release platform-account rows require externally signed authorization evidence; automatic backfill is forbidden';
    END IF;
END;
$block$;

ALTER TABLE __SCHEMA__.accounts
    ALTER COLUMN alias_key_epoch SET NOT NULL,
    ALTER COLUMN authorization_evidence_sha256 SET NOT NULL,
    ALTER COLUMN authorization_evidence_json SET NOT NULL;

ALTER TABLE __SCHEMA__.authorization_revisions
    ALTER COLUMN authorization_evidence_sha256 SET NOT NULL,
    ALTER COLUMN authorization_evidence_json SET NOT NULL;

ALTER TABLE __SCHEMA__.accounts
    DROP CONSTRAINT IF EXISTS accounts_platform_account_id_check,
    DROP CONSTRAINT IF EXISTS accounts_soul_id_check,
    DROP CONSTRAINT IF EXISTS accounts_device_binding_id_check,
    DROP CONSTRAINT IF EXISTS accounts_trace_id_check,
    DROP CONSTRAINT IF EXISTS accounts_idempotency_key_check,
    DROP CONSTRAINT IF EXISTS accounts_platform_alias_key_id_alias_digest_key,
    DROP CONSTRAINT IF EXISTS dps_accounts_platform_account_id_v1,
    DROP CONSTRAINT IF EXISTS dps_accounts_soul_id_v1,
    DROP CONSTRAINT IF EXISTS dps_accounts_device_binding_id_v1,
    DROP CONSTRAINT IF EXISTS dps_accounts_trace_id_v1,
    DROP CONSTRAINT IF EXISTS dps_accounts_idempotency_key_v1,
    DROP CONSTRAINT IF EXISTS dps_accounts_alias_key_epoch_v1,
    DROP CONSTRAINT IF EXISTS dps_accounts_authorization_evidence_v1,
    ADD CONSTRAINT dps_accounts_platform_account_id_v1 CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[0-9a-f]{32}$'),
    ADD CONSTRAINT dps_accounts_soul_id_v1 CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    ADD CONSTRAINT dps_accounts_device_binding_id_v1 CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    ADD CONSTRAINT dps_accounts_trace_id_v1 CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    ADD CONSTRAINT dps_accounts_idempotency_key_v1 CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    ADD CONSTRAINT dps_accounts_alias_key_epoch_v1 CHECK (alias_key_epoch >= 1),
    ADD CONSTRAINT dps_accounts_authorization_evidence_v1 CHECK (
        length(authorization_evidence_sha256) = 64
        AND authorization_evidence_sha256 ~ '^[0-9a-f]{64}$'
        AND jsonb_typeof(authorization_evidence_json::jsonb) = 'object'
        AND authorization_evidence_sha256 = encode(sha256(convert_to(authorization_evidence_json, 'UTF8')), 'hex')
        AND authorization_evidence_json::jsonb ->> 'contract_id' = 'platform.account.authorization.evidence/v1'
        AND authorization_evidence_json::jsonb ->> 'authorization_evidence_id' = authorization_evidence_id
        AND authorization_evidence_json::jsonb ->> 'soul_id' = soul_id
        AND authorization_evidence_json::jsonb ->> 'device_binding_id' = device_binding_id
        AND authorization_evidence_json::jsonb ->> 'platform_account_id' = platform_account_id
        AND authorization_evidence_json::jsonb ->> 'platform' = platform
        AND authorization_evidence_json::jsonb ->> 'alias_digest' = alias_digest
        AND authorization_evidence_json::jsonb ->> 'alias_key_id' = alias_key_id
        AND (authorization_evidence_json::jsonb ->> 'alias_key_epoch')::bigint = alias_key_epoch
        AND authorization_evidence_json::jsonb ->> 'target_status' = status
        AND (authorization_evidence_json::jsonb ->> 'authorization_revision')::bigint = authorization_revision
        AND authorization_evidence_json::jsonb ->> 'trace_id' = trace_id
        AND authorization_evidence_json::jsonb ->> 'idempotency_key' = idempotency_key
    );

CREATE UNIQUE INDEX IF NOT EXISTS ux_platform_account_alias_epoch
    ON __SCHEMA__.accounts (platform, alias_key_id, alias_key_epoch, alias_digest);

ALTER TABLE __SCHEMA__.authorization_revisions
    DROP CONSTRAINT IF EXISTS authorization_revisions_device_binding_id_check,
    DROP CONSTRAINT IF EXISTS authorization_revisions_soul_id_check,
    DROP CONSTRAINT IF EXISTS authorization_revisions_trace_id_check,
    DROP CONSTRAINT IF EXISTS authorization_revisions_idempotency_key_check,
    DROP CONSTRAINT IF EXISTS dps_authorization_revisions_device_binding_id_v1,
    DROP CONSTRAINT IF EXISTS dps_authorization_revisions_soul_id_v1,
    DROP CONSTRAINT IF EXISTS dps_authorization_revisions_trace_id_v1,
    DROP CONSTRAINT IF EXISTS dps_authorization_revisions_idempotency_key_v1,
    DROP CONSTRAINT IF EXISTS dps_authorization_revisions_evidence_v1,
    ADD CONSTRAINT dps_authorization_revisions_device_binding_id_v1 CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    ADD CONSTRAINT dps_authorization_revisions_soul_id_v1 CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    ADD CONSTRAINT dps_authorization_revisions_trace_id_v1 CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    ADD CONSTRAINT dps_authorization_revisions_idempotency_key_v1 CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    ADD CONSTRAINT dps_authorization_revisions_evidence_v1 CHECK (
        length(authorization_evidence_sha256) = 64
        AND authorization_evidence_sha256 ~ '^[0-9a-f]{64}$'
        AND jsonb_typeof(authorization_evidence_json::jsonb) = 'object'
        AND authorization_evidence_sha256 = encode(sha256(convert_to(authorization_evidence_json, 'UTF8')), 'hex')
        AND authorization_evidence_json::jsonb ->> 'contract_id' = 'platform.account.authorization.evidence/v1'
        AND authorization_evidence_json::jsonb ->> 'authorization_evidence_id' = authorization_evidence_id
        AND authorization_evidence_json::jsonb ->> 'soul_id' = soul_id
        AND authorization_evidence_json::jsonb ->> 'device_binding_id' = device_binding_id
        AND authorization_evidence_json::jsonb ->> 'platform_account_id' = platform_account_id
        AND authorization_evidence_json::jsonb ->> 'target_status' = status
        AND (authorization_evidence_json::jsonb ->> 'authorization_revision')::bigint = authorization_revision
        AND authorization_evidence_json::jsonb ->> 'trace_id' = trace_id
        AND authorization_evidence_json::jsonb ->> 'idempotency_key' = idempotency_key
    );

ALTER TABLE __SCHEMA__.binding_reservations
    DROP CONSTRAINT IF EXISTS binding_reservations_device_binding_id_check,
    DROP CONSTRAINT IF EXISTS binding_reservations_soul_id_check,
    DROP CONSTRAINT IF EXISTS binding_reservations_trace_id_check,
    DROP CONSTRAINT IF EXISTS dps_binding_reservations_device_binding_id_v1,
    DROP CONSTRAINT IF EXISTS dps_binding_reservations_soul_id_v1,
    DROP CONSTRAINT IF EXISTS dps_binding_reservations_trace_id_v1,
    ADD CONSTRAINT dps_binding_reservations_device_binding_id_v1 CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    ADD CONSTRAINT dps_binding_reservations_soul_id_v1 CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    ADD CONSTRAINT dps_binding_reservations_trace_id_v1 CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$');

ALTER TABLE __SCHEMA__.mutation_receipts
    DROP CONSTRAINT IF EXISTS mutation_receipts_idempotency_key_check,
    DROP CONSTRAINT IF EXISTS dps_mutation_receipts_idempotency_key_v1,
    ADD CONSTRAINT dps_mutation_receipts_idempotency_key_v1 CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$');

ALTER TABLE __SCHEMA__.outbox
    DROP CONSTRAINT IF EXISTS outbox_device_binding_id_check,
    DROP CONSTRAINT IF EXISTS outbox_soul_id_check,
    DROP CONSTRAINT IF EXISTS outbox_trace_id_check,
    DROP CONSTRAINT IF EXISTS dps_outbox_device_binding_id_v1,
    DROP CONSTRAINT IF EXISTS dps_outbox_soul_id_v1,
    DROP CONSTRAINT IF EXISTS dps_outbox_trace_id_v1,
    ADD CONSTRAINT dps_outbox_device_binding_id_v1 CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    ADD CONSTRAINT dps_outbox_soul_id_v1 CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    ADD CONSTRAINT dps_outbox_trace_id_v1 CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$');
