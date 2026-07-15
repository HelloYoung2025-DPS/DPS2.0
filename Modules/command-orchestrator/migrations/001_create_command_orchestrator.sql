CREATE TABLE __SCHEMA__.migration_ledger (
    schema_version text COLLATE "C" PRIMARY KEY,
    migration_sha256 text COLLATE "C" NOT NULL,
    runtime_capability_sha256 text COLLATE "C" NOT NULL,
    migrator_role text COLLATE "C" NOT NULL,
    server_version_num integer NOT NULL,
    applied_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT migration_schema_version_exact CHECK (schema_version = '1'),
    CONSTRAINT migration_sha256_exact CHECK (migration_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(migration_sha256) = 64),
    CONSTRAINT migration_runtime_capability_sha256_exact CHECK (runtime_capability_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(runtime_capability_sha256) = 64),
    CONSTRAINT migration_postgres_exact CHECK (server_version_num = 180004)
);

CREATE TABLE __SCHEMA__.catalog_attestations (
    catalog_sha256 text COLLATE "C" PRIMARY KEY,
    migration_sha256 text COLLATE "C" NOT NULL,
    schema_version text COLLATE "C" NOT NULL,
    server_version_num integer NOT NULL,
    recorded_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT catalog_sha256_exact CHECK (catalog_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(catalog_sha256) = 64),
    CONSTRAINT catalog_migration_sha256_exact CHECK (migration_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(migration_sha256) = 64),
    CONSTRAINT catalog_schema_version_exact CHECK (schema_version = '1'),
    CONSTRAINT catalog_postgres_exact CHECK (server_version_num = 180004)
);

CREATE TABLE __SCHEMA__.commands (
    command_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL,
    idempotency_scope_sha256 text COLLATE "C" NOT NULL UNIQUE,
    operation_sha256 text COLLATE "C" NOT NULL,
    soul_id text COLLATE "C" NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    platform_account_id text COLLATE "C" NOT NULL,
    trace_id text COLLATE "C" NOT NULL,
    idempotency_key text COLLATE "C" NOT NULL,
    operation_json jsonb NOT NULL,
    retry_safe boolean NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT commands_operation_sha256_exact CHECK (operation_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(operation_sha256) = 64),
    CONSTRAINT commands_scope_sha256_exact CHECK (idempotency_scope_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(idempotency_scope_sha256) = 64),
    CONSTRAINT commands_soul_exact CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT commands_binding_exact CHECK (device_binding_id ~ '^db_[a-f0-9]{32}$' AND octet_length(device_binding_id) = 35),
    CONSTRAINT commands_account_exact CHECK (platform_account_id ~ '^pa_[a-f0-9]{32}$' AND octet_length(platform_account_id) = 35),
    CONSTRAINT commands_trace_exact CHECK (trace_id ~ '^trace_[a-f0-9]{32}$' AND octet_length(trace_id) = 38),
    CONSTRAINT commands_idempotency_exact CHECK (idempotency_key ~ '^idem_[a-f0-9]{64}$' AND octet_length(idempotency_key) = 69),
    CONSTRAINT commands_operation_size CHECK (octet_length(operation_json::text) BETWEEN 2 AND 262144)
);

CREATE TABLE __SCHEMA__.leases (
    lease_id uuid PRIMARY KEY,
    command_id uuid NOT NULL REFERENCES __SCHEMA__.commands(command_id),
    attempt integer NOT NULL,
    lease_owner text COLLATE "C" NOT NULL,
    acquired_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT leases_command_attempt_unique UNIQUE (command_id, attempt),
    CONSTRAINT leases_attempt_range CHECK (attempt BETWEEN 1 AND 3),
    CONSTRAINT leases_owner_exact CHECK (octet_length(lease_owner) BETWEEN 1 AND 128),
    CONSTRAINT leases_window CHECK (expires_at > acquired_at AND expires_at <= acquired_at + interval '5 minutes')
);

CREATE TABLE __SCHEMA__.attempt_events (
    event_seq bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    command_id uuid NOT NULL REFERENCES __SCHEMA__.commands(command_id),
    lease_id uuid NOT NULL REFERENCES __SCHEMA__.leases(lease_id),
    attempt integer NOT NULL,
    event_kind text COLLATE "C" NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    command_sha256 text COLLATE "C",
    authorization_sha256 text COLLATE "C",
    release_bom_sha256 text COLLATE "C",
    active_release_bom_generation bigint,
    active_release_bom_token_sha256 text COLLATE "C",
    payload_json jsonb,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT attempt_events_once UNIQUE (command_id, attempt, event_kind),
    CONSTRAINT attempt_events_attempt_range CHECK (attempt BETWEEN 1 AND 3),
    CONSTRAINT attempt_events_kind_exact CHECK (event_kind IN (
        'LEASE_RESERVED', 'LEASE_BOUND', 'DISPATCHED', 'LEASE_EXPIRED_PRE_DISPATCH',
        'LEASE_EXPIRED_POST_DISPATCH', 'RECEIPT_SUCCESS', 'RECEIPT_FAILED_RETRYABLE',
        'RECEIPT_FAILED_FINAL', 'RECEIPT_UNKNOWN_OUTCOME')),
    CONSTRAINT attempt_events_command_sha256_exact CHECK (command_sha256 IS NULL OR (command_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(command_sha256) = 64)),
    CONSTRAINT attempt_events_authorization_sha256_exact CHECK (authorization_sha256 IS NULL OR (authorization_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(authorization_sha256) = 64)),
    CONSTRAINT attempt_events_bom_sha256_exact CHECK (release_bom_sha256 IS NULL OR (release_bom_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(release_bom_sha256) = 64)),
    CONSTRAINT attempt_events_token_sha256_exact CHECK (active_release_bom_token_sha256 IS NULL OR (active_release_bom_token_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(active_release_bom_token_sha256) = 64)),
    CONSTRAINT attempt_events_generation_positive CHECK (active_release_bom_generation IS NULL OR active_release_bom_generation > 0),
    CONSTRAINT attempt_events_payload_size CHECK (payload_json IS NULL OR octet_length(payload_json::text) BETWEEN 2 AND 262144)
);

CREATE INDEX attempt_events_latest_idx ON __SCHEMA__.attempt_events(command_id, event_seq DESC);

CREATE TABLE __SCHEMA__.signed_receipts (
    receipt_id uuid PRIMARY KEY,
    command_id uuid NOT NULL REFERENCES __SCHEMA__.commands(command_id),
    lease_id uuid NOT NULL REFERENCES __SCHEMA__.leases(lease_id),
    attempt integer NOT NULL,
    signed_receipt_sha256 text COLLATE "C" NOT NULL,
    receipt_sha256 text COLLATE "C" NOT NULL,
    command_sha256 text COLLATE "C" NOT NULL,
    authorization_sha256 text COLLATE "C" NOT NULL,
    release_bom_sha256 text COLLATE "C" NOT NULL,
    active_release_bom_generation bigint NOT NULL,
    active_release_bom_token_sha256 text COLLATE "C" NOT NULL,
    outcome text COLLATE "C" NOT NULL,
    signed_receipt_json jsonb NOT NULL,
    receipt_json jsonb NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT signed_receipts_attempt_range CHECK (attempt BETWEEN 1 AND 3),
    CONSTRAINT signed_receipts_signed_sha_exact CHECK (signed_receipt_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(signed_receipt_sha256) = 64),
    CONSTRAINT signed_receipts_receipt_sha_exact CHECK (receipt_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(receipt_sha256) = 64),
    CONSTRAINT signed_receipts_command_sha_exact CHECK (command_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(command_sha256) = 64),
    CONSTRAINT signed_receipts_authorization_sha_exact CHECK (authorization_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(authorization_sha256) = 64),
    CONSTRAINT signed_receipts_bom_sha_exact CHECK (release_bom_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(release_bom_sha256) = 64),
    CONSTRAINT signed_receipts_token_sha_exact CHECK (active_release_bom_token_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(active_release_bom_token_sha256) = 64),
    CONSTRAINT signed_receipts_generation_positive CHECK (active_release_bom_generation > 0),
    CONSTRAINT signed_receipts_outcome_exact CHECK (outcome IN ('SUCCESS', 'FAILED', 'UNKNOWN_OUTCOME')),
    CONSTRAINT signed_receipts_payload_size CHECK (octet_length(signed_receipt_json::text) BETWEEN 2 AND 524288 AND octet_length(receipt_json::text) BETWEEN 2 AND 262144)
);

CREATE TABLE __SCHEMA__.outbox (
    outbox_seq bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    receipt_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.signed_receipts(receipt_id),
    command_id uuid NOT NULL,
    soul_id text COLLATE "C" NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    platform_account_id text COLLATE "C" NOT NULL,
    topic text COLLATE "C" NOT NULL,
    payload_sha256 text COLLATE "C" NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at timestamp with time zone NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT outbox_topic_exact CHECK (topic = 'command.receipt/v1'),
    CONSTRAINT outbox_payload_sha_exact CHECK (payload_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(payload_sha256) = 64),
    CONSTRAINT outbox_soul_exact CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT outbox_binding_exact CHECK (device_binding_id ~ '^db_[a-f0-9]{32}$' AND octet_length(device_binding_id) = 35),
    CONSTRAINT outbox_account_exact CHECK (platform_account_id ~ '^pa_[a-f0-9]{32}$' AND octet_length(platform_account_id) = 35),
    CONSTRAINT outbox_payload_size CHECK (octet_length(payload_json::text) BETWEEN 2 AND 262144)
);

CREATE TABLE __SCHEMA__.quarantine (
    quarantine_seq bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    conflict_kind text COLLATE "C" NOT NULL,
    identity_id text COLLATE "C" NOT NULL,
    soul_id text COLLATE "C" NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    platform_account_id text COLLATE "C" NOT NULL,
    existing_sha256 text COLLATE "C" NOT NULL,
    incoming_sha256 text COLLATE "C" NOT NULL,
    reason text COLLATE "C" NOT NULL,
    created_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT quarantine_conflict_unique UNIQUE (conflict_kind, identity_id, existing_sha256, incoming_sha256, reason),
    CONSTRAINT quarantine_kind_exact CHECK (conflict_kind IN ('IDEMPOTENCY_CONFLICT', 'COMMAND_ID_COLLISION', 'RECEIPT_ID_CONFLICT')),
    CONSTRAINT quarantine_identity_bounded CHECK (octet_length(identity_id) BETWEEN 1 AND 128),
    CONSTRAINT quarantine_soul_exact CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT quarantine_binding_exact CHECK (device_binding_id ~ '^db_[a-f0-9]{32}$' AND octet_length(device_binding_id) = 35),
    CONSTRAINT quarantine_account_exact CHECK (platform_account_id ~ '^pa_[a-f0-9]{32}$' AND octet_length(platform_account_id) = 35),
    CONSTRAINT quarantine_existing_sha_exact CHECK (existing_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(existing_sha256) = 64),
    CONSTRAINT quarantine_incoming_sha_exact CHECK (incoming_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(incoming_sha256) = 64),
    CONSTRAINT quarantine_reason_exact CHECK (reason IN ('same scoped idempotency key with different operation digest', 'same command id with different immutable command', 'same receipt id with different signed receipt digest'))
);

ALTER DEFAULT PRIVILEGES IN SCHEMA __SCHEMA__ REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_append_only_mutation()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
BEGIN
    RAISE EXCEPTION 'command-orchestrator durable records are append-only' USING ERRCODE = '55000';
END
$function$;

CREATE TRIGGER migration_ledger_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.migration_ledger FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER migration_ledger_no_truncate BEFORE TRUNCATE ON __SCHEMA__.migration_ledger FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER catalog_attestations_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.catalog_attestations FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER catalog_attestations_no_truncate BEFORE TRUNCATE ON __SCHEMA__.catalog_attestations FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER commands_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.commands FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER commands_no_truncate BEFORE TRUNCATE ON __SCHEMA__.commands FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER leases_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.leases FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER leases_no_truncate BEFORE TRUNCATE ON __SCHEMA__.leases FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER attempt_events_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.attempt_events FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER attempt_events_no_truncate BEFORE TRUNCATE ON __SCHEMA__.attempt_events FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER signed_receipts_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.signed_receipts FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER signed_receipts_no_truncate BEFORE TRUNCATE ON __SCHEMA__.signed_receipts FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER outbox_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.outbox FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER outbox_no_truncate BEFORE TRUNCATE ON __SCHEMA__.outbox FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER quarantine_no_row_mutation BEFORE UPDATE OR DELETE ON __SCHEMA__.quarantine FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();
CREATE TRIGGER quarantine_no_truncate BEFORE TRUNCATE ON __SCHEMA__.quarantine FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_mutation();

INSERT INTO __SCHEMA__.migration_ledger(
    schema_version, migration_sha256, runtime_capability_sha256, migrator_role,
    server_version_num)
VALUES (
    '1', '__MIGRATION_SHA256__', '__RUNTIME_CAPABILITY_SHA256__', current_user,
    current_setting('server_version_num')::integer);

CREATE OR REPLACE FUNCTION __SCHEMA__.assert_runtime_capability(p_runtime_capability bytea)
RETURNS void
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
BEGIN
    IF p_runtime_capability IS NULL
       OR pg_catalog.octet_length(p_runtime_capability) <> 32 THEN
        RAISE EXCEPTION 'runtime database capability is missing or invalid'
            USING ERRCODE = '42501';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.migration_ledger AS ledger
        WHERE ledger.schema_version = '1'
          AND ledger.runtime_capability_sha256 =
              pg_catalog.encode(pg_catalog.sha256(p_runtime_capability), 'hex')) THEN
        RAISE EXCEPTION 'runtime database capability is missing or invalid'
            USING ERRCODE = '42501';
    END IF;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.project_command_state(p_command_id uuid)
RETURNS TABLE(state text, attempt integer, lease_id uuid, lease_expires_at timestamp with time zone, event_kind text)
LANGUAGE sql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
    SELECT
        CASE latest.event_kind
            WHEN 'LEASE_RESERVED' THEN 'Leased'
            WHEN 'LEASE_BOUND' THEN 'Leased'
            WHEN 'DISPATCHED' THEN 'Dispatched'
            WHEN 'LEASE_EXPIRED_PRE_DISPATCH' THEN 'Pending'
            WHEN 'LEASE_EXPIRED_POST_DISPATCH' THEN 'ReconciliationRequired'
            WHEN 'RECEIPT_SUCCESS' THEN 'Succeeded'
            WHEN 'RECEIPT_FAILED_RETRYABLE' THEN 'Pending'
            WHEN 'RECEIPT_FAILED_FINAL' THEN 'Failed'
            WHEN 'RECEIPT_UNKNOWN_OUTCOME' THEN 'ReconciliationRequired'
            ELSE 'Pending'
        END,
        COALESCE(latest.attempt, 0),
        latest.lease_id,
        lease.expires_at,
        latest.event_kind
    FROM __SCHEMA__.commands AS command
    LEFT JOIN LATERAL (
        SELECT event.attempt, event.lease_id, event.event_kind
        FROM __SCHEMA__.attempt_events AS event
        WHERE event.command_id = command.command_id
        ORDER BY event.attempt DESC,
                 CASE event.event_kind
                     WHEN 'LEASE_RESERVED' THEN 10
                     WHEN 'LEASE_BOUND' THEN 20
                     WHEN 'DISPATCHED' THEN 30
                     WHEN 'LEASE_EXPIRED_PRE_DISPATCH' THEN 40
                     WHEN 'LEASE_EXPIRED_POST_DISPATCH' THEN 40
                     WHEN 'RECEIPT_SUCCESS' THEN 50
                     WHEN 'RECEIPT_FAILED_RETRYABLE' THEN 50
                     WHEN 'RECEIPT_FAILED_FINAL' THEN 50
                     WHEN 'RECEIPT_UNKNOWN_OUTCOME' THEN 50
                     ELSE 0
                 END DESC,
                 event.event_seq DESC
        LIMIT 1
    ) AS latest ON true
    LEFT JOIN __SCHEMA__.leases AS lease ON lease.lease_id = latest.lease_id
    WHERE command.command_id = p_command_id
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_enqueue_command(
    p_command_id uuid,
    p_operation_id uuid,
    p_idempotency_scope_sha256 text,
    p_operation_sha256 text,
    p_soul_id text,
    p_device_binding_id text,
    p_platform_account_id text,
    p_trace_id text,
    p_idempotency_key text,
    p_operation_json jsonb,
    p_retry_safe boolean,
    p_occurred_at timestamp with time zone,
    p_runtime_capability bytea)
RETURNS TABLE(disposition text, result_command_id uuid, payload_sha256 text, state text)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    existing __SCHEMA__.commands%ROWTYPE;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_idempotency_scope_sha256, 730301));
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_command_id::text, 730302));
    SELECT * INTO existing FROM __SCHEMA__.commands WHERE idempotency_scope_sha256 = p_idempotency_scope_sha256;
    IF FOUND THEN
        IF existing.operation_sha256 = p_operation_sha256 AND existing.command_id = p_command_id THEN
            RETURN QUERY SELECT 'DuplicateNoOp'::text, existing.command_id, existing.operation_sha256, 'Pending'::text;
            RETURN;
        END IF;
        INSERT INTO __SCHEMA__.quarantine(
            conflict_kind, identity_id, soul_id, device_binding_id, platform_account_id,
            existing_sha256, incoming_sha256, reason)
        VALUES (
            'IDEMPOTENCY_CONFLICT', p_idempotency_scope_sha256, p_soul_id, p_device_binding_id,
            p_platform_account_id, existing.operation_sha256, p_operation_sha256,
            'same scoped idempotency key with different operation digest')
        ON CONFLICT DO NOTHING;
        RETURN QUERY SELECT 'Quarantined'::text, NULL::uuid, p_operation_sha256, 'Pending'::text;
        RETURN;
    END IF;

    SELECT * INTO existing FROM __SCHEMA__.commands WHERE command_id = p_command_id;
    IF FOUND THEN
        INSERT INTO __SCHEMA__.quarantine(
            conflict_kind, identity_id, soul_id, device_binding_id, platform_account_id,
            existing_sha256, incoming_sha256, reason)
        VALUES (
            'COMMAND_ID_COLLISION', p_command_id::text, p_soul_id, p_device_binding_id,
            p_platform_account_id, existing.operation_sha256, p_operation_sha256,
            'same command id with different immutable command')
        ON CONFLICT DO NOTHING;
        RETURN QUERY SELECT 'Quarantined'::text, NULL::uuid, p_operation_sha256, 'Pending'::text;
        RETURN;
    END IF;

    INSERT INTO __SCHEMA__.commands(
        command_id, operation_id, idempotency_scope_sha256, operation_sha256, soul_id,
        device_binding_id, platform_account_id, trace_id, idempotency_key, operation_json,
        retry_safe, occurred_at)
    VALUES (
        p_command_id, p_operation_id, p_idempotency_scope_sha256, p_operation_sha256, p_soul_id,
        p_device_binding_id, p_platform_account_id, p_trace_id, p_idempotency_key, p_operation_json,
        p_retry_safe, p_occurred_at);
    RETURN QUERY SELECT 'Inserted'::text, p_command_id, p_operation_sha256, 'Pending'::text;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_reserve_lease(
    p_command_id uuid,
    p_soul_id text,
    p_device_binding_id text,
    p_platform_account_id text,
    p_lease_id uuid,
    p_worker_id text,
    p_duration_seconds integer,
    p_runtime_capability bytea)
RETURNS TABLE(
    operation_json jsonb, operation_sha256 text, attempt integer, lease_id uuid,
    lease_expires_at timestamp with time zone, acquired_at timestamp with time zone,
    disposition text)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    command_row __SCHEMA__.commands%ROWTYPE;
    projected record;
    next_attempt integer;
    authoritative_now timestamp with time zone;
    proposed_expires timestamp with time zone;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    IF p_duration_seconds < 1 OR p_duration_seconds > 300 THEN
        RAISE EXCEPTION 'lease duration must be between one and 300 seconds' USING ERRCODE = '22023';
    END IF;
    IF octet_length(p_worker_id) < 1 OR octet_length(p_worker_id) > 128 THEN
        RAISE EXCEPTION 'lease owner is outside the allowed boundary' USING ERRCODE = '22023';
    END IF;
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_command_id::text, 730302));
    authoritative_now := pg_catalog.clock_timestamp();
    SELECT * INTO command_row FROM __SCHEMA__.commands WHERE command_id = p_command_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'unknown command' USING ERRCODE = 'P0002'; END IF;
    IF command_row.soul_id <> p_soul_id OR command_row.device_binding_id <> p_device_binding_id OR command_row.platform_account_id <> p_platform_account_id THEN
        RAISE EXCEPTION 'SOUL-ISO-001: command scope mismatch' USING ERRCODE = '42501';
    END IF;

    SELECT * INTO projected FROM __SCHEMA__.project_command_state(p_command_id);
    IF projected.state IN ('Leased', 'Dispatched') AND projected.lease_expires_at <= authoritative_now THEN
        INSERT INTO __SCHEMA__.attempt_events(command_id, lease_id, attempt, event_kind, occurred_at)
        VALUES (
            p_command_id, projected.lease_id, projected.attempt,
            CASE WHEN projected.state = 'Leased' THEN 'LEASE_EXPIRED_PRE_DISPATCH' ELSE 'LEASE_EXPIRED_POST_DISPATCH' END,
            authoritative_now)
        ON CONFLICT DO NOTHING;
        SELECT * INTO projected FROM __SCHEMA__.project_command_state(p_command_id);
    END IF;
    IF projected.state <> 'Pending' THEN
        RAISE EXCEPTION 'command is not leaseable from state %', projected.state USING ERRCODE = '55000';
    END IF;
    SELECT COALESCE(max(lease.attempt), 0) + 1 INTO next_attempt FROM __SCHEMA__.leases AS lease WHERE lease.command_id = p_command_id;
    IF next_attempt > 3 THEN RAISE EXCEPTION 'maximum attempts reached' USING ERRCODE = '54000'; END IF;
    proposed_expires := authoritative_now + pg_catalog.make_interval(secs => p_duration_seconds);
    INSERT INTO __SCHEMA__.leases(lease_id, command_id, attempt, lease_owner, acquired_at, expires_at)
    VALUES (p_lease_id, p_command_id, next_attempt, p_worker_id, authoritative_now, proposed_expires);
    INSERT INTO __SCHEMA__.attempt_events(command_id, lease_id, attempt, event_kind, occurred_at)
    VALUES (p_command_id, p_lease_id, next_attempt, 'LEASE_RESERVED', authoritative_now);
    RETURN QUERY SELECT command_row.operation_json, command_row.operation_sha256, next_attempt,
                        p_lease_id, proposed_expires, authoritative_now, 'Reserved'::text;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_bind_lease(
    p_command_id uuid,
    p_lease_id uuid,
    p_attempt integer,
    p_command_sha256 text,
    p_dispatch_json jsonb,
    p_runtime_capability bytea)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    projected record;
    existing __SCHEMA__.attempt_events%ROWTYPE;
    authoritative_now timestamp with time zone;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_command_id::text, 730302));
    authoritative_now := pg_catalog.clock_timestamp();
    SELECT * INTO existing FROM __SCHEMA__.attempt_events
    WHERE command_id = p_command_id AND attempt = p_attempt AND event_kind = 'LEASE_BOUND';
    IF FOUND THEN
        IF existing.lease_id = p_lease_id AND existing.command_sha256 = p_command_sha256 AND existing.payload_json = p_dispatch_json THEN RETURN 'DuplicateNoOp'; END IF;
        RAISE EXCEPTION 'lease binding conflict' USING ERRCODE = '23505';
    END IF;
    SELECT * INTO projected FROM __SCHEMA__.project_command_state(p_command_id);
    IF projected.state <> 'Leased' OR projected.event_kind <> 'LEASE_RESERVED' OR projected.lease_id <> p_lease_id OR projected.attempt <> p_attempt OR projected.lease_expires_at <= authoritative_now THEN
        RAISE EXCEPTION 'lease is missing, expired, forged, or out of order' USING ERRCODE = '42501';
    END IF;
    INSERT INTO __SCHEMA__.attempt_events(command_id, lease_id, attempt, event_kind, occurred_at, command_sha256, payload_json)
    VALUES (p_command_id, p_lease_id, p_attempt, 'LEASE_BOUND', authoritative_now, p_command_sha256, p_dispatch_json);
    RETURN 'Bound';
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_get_lease_context(
    p_command_id uuid,
    p_lease_id uuid,
    p_runtime_capability bytea)
RETURNS TABLE(
    state text, attempt integer, lease_expires_at timestamp with time zone,
    command_sha256 text, dispatch_json jsonb)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    RETURN QUERY
    SELECT projected.state, projected.attempt, projected.lease_expires_at,
           bound.command_sha256, bound.payload_json
    FROM __SCHEMA__.project_command_state(p_command_id) AS projected
    JOIN __SCHEMA__.attempt_events AS bound
      ON bound.command_id = p_command_id
     AND bound.lease_id = p_lease_id
     AND bound.attempt = projected.attempt
     AND bound.event_kind = 'LEASE_BOUND'
    WHERE projected.lease_id = p_lease_id;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_mark_dispatched(
    p_command_id uuid,
    p_lease_id uuid,
    p_attempt integer,
    p_command_sha256 text,
    p_authorization_sha256 text,
    p_release_bom_sha256 text,
    p_active_release_bom_generation bigint,
    p_active_release_bom_token_sha256 text,
    p_authorization_json jsonb,
    p_authorization_occurred_at timestamp with time zone,
    p_authorization_valid_until timestamp with time zone,
    p_runtime_capability bytea)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    projected record;
    bound __SCHEMA__.attempt_events%ROWTYPE;
    existing __SCHEMA__.attempt_events%ROWTYPE;
    authoritative_now timestamp with time zone;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_command_id::text, 730302));
    authoritative_now := pg_catalog.clock_timestamp();
    SELECT * INTO existing FROM __SCHEMA__.attempt_events
    WHERE command_id = p_command_id AND attempt = p_attempt AND event_kind = 'DISPATCHED';
    IF FOUND THEN
        IF existing.lease_id = p_lease_id AND existing.command_sha256 = p_command_sha256
           AND existing.authorization_sha256 = p_authorization_sha256
           AND existing.release_bom_sha256 = p_release_bom_sha256
           AND existing.active_release_bom_generation = p_active_release_bom_generation
           AND existing.active_release_bom_token_sha256 = p_active_release_bom_token_sha256
           AND existing.payload_json = p_authorization_json THEN RETURN 'DuplicateNoOp'; END IF;
        RAISE EXCEPTION 'dispatch binding conflict' USING ERRCODE = '23505';
    END IF;
    SELECT * INTO projected FROM __SCHEMA__.project_command_state(p_command_id);
    IF projected.state <> 'Leased' OR projected.event_kind <> 'LEASE_BOUND' OR projected.lease_id <> p_lease_id OR projected.attempt <> p_attempt OR projected.lease_expires_at <= authoritative_now THEN
        RAISE EXCEPTION 'lease is missing, expired, forged, or out of order' USING ERRCODE = '42501';
    END IF;
    IF p_authorization_occurred_at > authoritative_now
       OR p_authorization_valid_until <= authoritative_now
       OR p_authorization_valid_until > projected.lease_expires_at THEN
        RAISE EXCEPTION 'execution authorization is outside the database-clock validity window' USING ERRCODE = '42501';
    END IF;
    SELECT * INTO bound FROM __SCHEMA__.attempt_events
    WHERE command_id = p_command_id AND attempt = p_attempt AND event_kind = 'LEASE_BOUND';
    IF bound.command_sha256 <> p_command_sha256 THEN RAISE EXCEPTION 'authorization command digest mismatch' USING ERRCODE = '42501'; END IF;
    INSERT INTO __SCHEMA__.attempt_events(
        command_id, lease_id, attempt, event_kind, occurred_at, command_sha256,
        authorization_sha256, release_bom_sha256, active_release_bom_generation,
        active_release_bom_token_sha256, payload_json)
    VALUES (
        p_command_id, p_lease_id, p_attempt, 'DISPATCHED', authoritative_now, p_command_sha256,
        p_authorization_sha256, p_release_bom_sha256, p_active_release_bom_generation,
        p_active_release_bom_token_sha256, p_authorization_json);
    RETURN 'Dispatched';
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_record_receipt(
    p_receipt_id uuid,
    p_command_id uuid,
    p_lease_id uuid,
    p_attempt integer,
    p_soul_id text,
    p_device_binding_id text,
    p_platform_account_id text,
    p_trace_id text,
    p_idempotency_key text,
    p_signed_receipt_sha256 text,
    p_receipt_sha256 text,
    p_command_sha256 text,
    p_authorization_sha256 text,
    p_release_bom_sha256 text,
    p_active_release_bom_generation bigint,
    p_active_release_bom_token_sha256 text,
    p_outcome text,
    p_retry_allowed boolean,
    p_signed_receipt_json jsonb,
    p_receipt_json jsonb,
    p_occurred_at timestamp with time zone,
    p_runtime_capability bytea)
RETURNS TABLE(disposition text, state text)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    existing __SCHEMA__.signed_receipts%ROWTYPE;
    command_row __SCHEMA__.commands%ROWTYPE;
    projected record;
    dispatched __SCHEMA__.attempt_events%ROWTYPE;
    next_event text;
    next_state text;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_receipt_id::text, 730303));
    SELECT * INTO existing FROM __SCHEMA__.signed_receipts WHERE receipt_id = p_receipt_id;
    IF FOUND THEN
        IF existing.signed_receipt_sha256 = p_signed_receipt_sha256 THEN
            SELECT projected_state.state INTO next_state FROM __SCHEMA__.project_command_state(existing.command_id) AS projected_state;
            RETURN QUERY SELECT 'DuplicateNoOp'::text, next_state;
            RETURN;
        END IF;
        INSERT INTO __SCHEMA__.quarantine(
            conflict_kind, identity_id, soul_id, device_binding_id, platform_account_id,
            existing_sha256, incoming_sha256, reason)
        VALUES (
            'RECEIPT_ID_CONFLICT', p_receipt_id::text, p_soul_id, p_device_binding_id,
            p_platform_account_id, existing.signed_receipt_sha256, p_signed_receipt_sha256,
            'same receipt id with different signed receipt digest')
        ON CONFLICT DO NOTHING;
        SELECT projected_state.state INTO next_state FROM __SCHEMA__.project_command_state(existing.command_id) AS projected_state;
        RETURN QUERY SELECT 'Quarantined'::text, next_state;
        RETURN;
    END IF;

    PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(p_command_id::text, 730302));
    SELECT * INTO command_row FROM __SCHEMA__.commands WHERE command_id = p_command_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'unknown command' USING ERRCODE = 'P0002'; END IF;
    IF command_row.soul_id <> p_soul_id OR command_row.device_binding_id <> p_device_binding_id OR command_row.platform_account_id <> p_platform_account_id
       OR command_row.trace_id <> p_trace_id OR command_row.idempotency_key <> p_idempotency_key THEN
        RAISE EXCEPTION 'receipt scope, trace, or idempotency identity mismatch' USING ERRCODE = '42501';
    END IF;
    SELECT * INTO projected FROM __SCHEMA__.project_command_state(p_command_id);
    IF projected.state = 'Dispatched' THEN
        NULL;
    ELSIF projected.state = 'ReconciliationRequired' AND projected.event_kind = 'LEASE_EXPIRED_POST_DISPATCH' THEN
        NULL;
    ELSE
        RAISE EXCEPTION 'receipt is out of order for state %', projected.state USING ERRCODE = '55000';
    END IF;
    IF projected.lease_id <> p_lease_id OR projected.attempt <> p_attempt THEN
        RAISE EXCEPTION 'receipt belongs to a stale or forged lease attempt' USING ERRCODE = '42501';
    END IF;
    SELECT * INTO dispatched FROM __SCHEMA__.attempt_events
    WHERE command_id = p_command_id AND lease_id = p_lease_id AND attempt = p_attempt AND event_kind = 'DISPATCHED';
    IF NOT FOUND OR dispatched.command_sha256 <> p_command_sha256
       OR dispatched.authorization_sha256 <> p_authorization_sha256
       OR dispatched.release_bom_sha256 <> p_release_bom_sha256
       OR dispatched.active_release_bom_generation <> p_active_release_bom_generation
       OR dispatched.active_release_bom_token_sha256 <> p_active_release_bom_token_sha256 THEN
        RAISE EXCEPTION 'receipt is not bound to the exact dispatched authorization and BOM fence' USING ERRCODE = '42501';
    END IF;

    IF p_outcome = 'SUCCESS' THEN
        next_event := 'RECEIPT_SUCCESS'; next_state := 'Succeeded';
    ELSIF p_outcome = 'UNKNOWN_OUTCOME' THEN
        next_event := 'RECEIPT_UNKNOWN_OUTCOME'; next_state := 'ReconciliationRequired';
    ELSIF p_outcome = 'FAILED' AND p_retry_allowed AND command_row.retry_safe AND p_attempt < 3 THEN
        next_event := 'RECEIPT_FAILED_RETRYABLE'; next_state := 'Pending';
    ELSIF p_outcome = 'FAILED' THEN
        next_event := 'RECEIPT_FAILED_FINAL'; next_state := 'Failed';
    ELSE
        RAISE EXCEPTION 'unknown receipt outcome' USING ERRCODE = '22023';
    END IF;

    INSERT INTO __SCHEMA__.signed_receipts(
        receipt_id, command_id, lease_id, attempt, signed_receipt_sha256, receipt_sha256,
        command_sha256, authorization_sha256, release_bom_sha256, active_release_bom_generation,
        active_release_bom_token_sha256, outcome, signed_receipt_json, receipt_json, occurred_at)
    VALUES (
        p_receipt_id, p_command_id, p_lease_id, p_attempt, p_signed_receipt_sha256, p_receipt_sha256,
        p_command_sha256, p_authorization_sha256, p_release_bom_sha256, p_active_release_bom_generation,
        p_active_release_bom_token_sha256, p_outcome, p_signed_receipt_json, p_receipt_json, p_occurred_at);
    INSERT INTO __SCHEMA__.attempt_events(
        command_id, lease_id, attempt, event_kind, occurred_at, command_sha256,
        authorization_sha256, release_bom_sha256, active_release_bom_generation,
        active_release_bom_token_sha256, payload_json)
    VALUES (
        p_command_id, p_lease_id, p_attempt, next_event, p_occurred_at, p_command_sha256,
        p_authorization_sha256, p_release_bom_sha256, p_active_release_bom_generation,
        p_active_release_bom_token_sha256, p_receipt_json);
    INSERT INTO __SCHEMA__.outbox(
        receipt_id, command_id, soul_id, device_binding_id, platform_account_id,
        topic, payload_sha256, payload_json, occurred_at)
    VALUES (
        p_receipt_id, p_command_id, p_soul_id, p_device_binding_id, p_platform_account_id,
        'command.receipt/v1', p_receipt_sha256, p_receipt_json, p_occurred_at);
    RETURN QUERY SELECT 'Applied'::text, next_state;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_recover_expired_leases(
    p_runtime_capability bytea)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    candidate record;
    projected record;
    changed integer := 0;
    scan_now timestamp with time zone;
    authoritative_now timestamp with time zone;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    scan_now := pg_catalog.clock_timestamp();
    FOR candidate IN
        SELECT command.command_id
        FROM __SCHEMA__.commands AS command
        JOIN LATERAL __SCHEMA__.project_command_state(command.command_id) AS state ON true
        WHERE state.state IN ('Leased', 'Dispatched') AND state.lease_expires_at <= scan_now
        ORDER BY command.command_id
    LOOP
        PERFORM pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(candidate.command_id::text, 730302));
        authoritative_now := pg_catalog.clock_timestamp();
        SELECT * INTO projected FROM __SCHEMA__.project_command_state(candidate.command_id);
        IF projected.state IN ('Leased', 'Dispatched') AND projected.lease_expires_at <= authoritative_now THEN
            INSERT INTO __SCHEMA__.attempt_events(command_id, lease_id, attempt, event_kind, occurred_at)
            VALUES (
                candidate.command_id, projected.lease_id, projected.attempt,
                CASE WHEN projected.state = 'Leased' THEN 'LEASE_EXPIRED_PRE_DISPATCH' ELSE 'LEASE_EXPIRED_POST_DISPATCH' END,
                authoritative_now)
            ON CONFLICT DO NOTHING;
            changed := changed + 1;
        END IF;
    END LOOP;
    RETURN changed;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_get_snapshot(
    p_command_id uuid,
    p_soul_id text,
    p_device_binding_id text,
    p_platform_account_id text,
    p_runtime_capability bytea)
RETURNS TABLE(
    command_id uuid, soul_id text, device_binding_id text, platform_account_id text,
    state text, attempt integer, lease_id uuid, lease_expires_at timestamp with time zone)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    command_row __SCHEMA__.commands%ROWTYPE;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    SELECT * INTO command_row FROM __SCHEMA__.commands AS stored_command WHERE stored_command.command_id = p_command_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'unknown command' USING ERRCODE = 'P0002'; END IF;
    IF command_row.soul_id <> p_soul_id OR command_row.device_binding_id <> p_device_binding_id OR command_row.platform_account_id <> p_platform_account_id THEN
        RAISE EXCEPTION 'SOUL-ISO-001: command scope mismatch' USING ERRCODE = '42501';
    END IF;
    RETURN QUERY
    SELECT command_row.command_id, command_row.soul_id, command_row.device_binding_id,
           command_row.platform_account_id, projected.state, projected.attempt,
           projected.lease_id, projected.lease_expires_at
    FROM __SCHEMA__.project_command_state(p_command_id) AS projected;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_quarantine_count(p_runtime_capability bytea)
RETURNS bigint
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
DECLARE
    result_count bigint;
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    SELECT count(*) INTO result_count FROM __SCHEMA__.quarantine;
    RETURN result_count;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_read_outbox(
    p_after_seq bigint,
    p_limit integer,
    p_runtime_capability bytea)
RETURNS TABLE(
    outbox_seq bigint, receipt_id uuid, command_id uuid, soul_id text,
    device_binding_id text, platform_account_id text, topic text,
    payload_sha256 text, payload_json jsonb, occurred_at timestamp with time zone)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    IF p_after_seq < 0 OR p_limit < 1 OR p_limit > 100 THEN
        RAISE EXCEPTION 'outbox cursor or limit is outside the allowed boundary' USING ERRCODE = '22023';
    END IF;
    RETURN QUERY
    SELECT item.outbox_seq, item.receipt_id, item.command_id, item.soul_id,
           item.device_binding_id, item.platform_account_id, item.topic,
           item.payload_sha256, item.payload_json, item.occurred_at
    FROM __SCHEMA__.outbox AS item
    WHERE item.outbox_seq > p_after_seq
    ORDER BY item.outbox_seq
    LIMIT p_limit;
END
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.api_runtime_attestation(p_runtime_capability bytea)
RETURNS TABLE(
    schema_version text, migration_sha256 text, catalog_sha256 text,
    server_version_num integer, migrator_role text, runtime_role text)
LANGUAGE plpgsql
STABLE
SECURITY DEFINER
SET search_path = pg_catalog
SET row_security = off
AS $function$
BEGIN
    PERFORM __SCHEMA__.assert_runtime_capability(p_runtime_capability);
    RETURN QUERY
    SELECT ledger.schema_version, ledger.migration_sha256, attestation.catalog_sha256,
           current_setting('server_version_num')::integer, ledger.migrator_role, session_user
    FROM __SCHEMA__.migration_ledger AS ledger
    JOIN LATERAL (
        SELECT value.catalog_sha256
        FROM __SCHEMA__.catalog_attestations AS value
        WHERE value.migration_sha256 = ledger.migration_sha256
          AND value.schema_version = ledger.schema_version
        ORDER BY value.recorded_at DESC
        LIMIT 1
    ) AS attestation ON true
    WHERE ledger.schema_version = '1';
END
$function$;

REVOKE ALL ON SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM PUBLIC, __RUNTIME_ROLE__;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA __SCHEMA__ FROM PUBLIC, __RUNTIME_ROLE__;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM PUBLIC, __RUNTIME_ROLE__;
GRANT USAGE ON SCHEMA __SCHEMA__ TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_enqueue_command(uuid, uuid, text, text, text, text, text, text, text, jsonb, boolean, timestamp with time zone, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_reserve_lease(uuid, text, text, text, uuid, text, integer, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_bind_lease(uuid, uuid, integer, text, jsonb, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_get_lease_context(uuid, uuid, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_mark_dispatched(uuid, uuid, integer, text, text, text, bigint, text, jsonb, timestamp with time zone, timestamp with time zone, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_record_receipt(uuid, uuid, uuid, integer, text, text, text, text, text, text, text, text, text, text, bigint, text, text, boolean, jsonb, jsonb, timestamp with time zone, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_recover_expired_leases(bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_get_snapshot(uuid, text, text, text, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_quarantine_count(bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_read_outbox(bigint, integer, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.api_runtime_attestation(bytea) TO __RUNTIME_ROLE__;
