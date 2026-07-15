DO $bootstrap_preflight$
DECLARE
    schema_oid oid;
    schema_marker text;
    expected_marker constant text :=
        'dps.control-plane-host.constraint-definition-baseline/v1';
BEGIN
    SELECT n.oid,
           pg_catalog.obj_description(n.oid, 'pg_namespace')
    INTO schema_oid, schema_marker
    FROM pg_catalog.pg_namespace n
    WHERE n.nspname = '__SCHEMA__';

    IF schema_oid IS NULL THEN
        EXECUTE pg_catalog.format('CREATE SCHEMA %I', '__SCHEMA__');
        PERFORM pg_catalog.set_config(
            'dps.control_plane.bootstrap_new_schema',
            'true',
            true);
    ELSIF schema_marker IS NULL THEN
        RAISE EXCEPTION
            'control-plane migration refuses an unmarked pre-existing schema';
    ELSIF schema_marker <> expected_marker THEN
        RAISE EXCEPTION 'control-plane schema baseline marker is unknown';
    ELSE
        PERFORM pg_catalog.set_config(
            'dps.control_plane.bootstrap_new_schema',
            'false',
            true);
    END IF;
END;
$bootstrap_preflight$;
ALTER SCHEMA __SCHEMA__ OWNER TO CURRENT_USER;

DO $database_acl$
BEGIN
    EXECUTE format(
        'REVOKE TEMPORARY, CREATE ON DATABASE %I FROM PUBLIC',
        current_database());
    EXECUTE format(
        'REVOKE TEMPORARY, CREATE ON DATABASE %I FROM %I',
        current_database(),
        '__RUNTIME_ROLE__');
END;
$database_acl$;

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM __RUNTIME_ROLE__;

CREATE OR REPLACE FUNCTION __SCHEMA__.receipt_json_has_exact_keys(document jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
    SELECT jsonb_typeof(document) = 'object'
       AND document ?& ARRAY[
           'schema_version', 'contract_id', 'producer_module', 'soul_id',
           'device_binding_id', 'platform_account_id', 'trace_id',
           'idempotency_key', 'occurred_at', 'privacy_class', 'receipt_id',
           'source_contract_id', 'source_producer_module',
           'source_payload_sha256', 'decision'
       ]::text[]
       AND NOT EXISTS (
           SELECT 1
           FROM jsonb_object_keys(document) AS actual(key)
           WHERE NOT (actual.key = ANY(ARRAY[
               'schema_version', 'contract_id', 'producer_module', 'soul_id',
               'device_binding_id', 'platform_account_id', 'trace_id',
               'idempotency_key', 'occurred_at', 'privacy_class', 'receipt_id',
               'source_contract_id', 'source_producer_module',
               'source_payload_sha256', 'decision'
           ]::text[]))
       )
       AND NOT EXISTS (
           SELECT 1
           FROM jsonb_each(document) AS field(key, value)
           WHERE jsonb_typeof(field.value) <> 'string'
              OR length(field.value #>> '{}') = 0
       );
$function$;

CREATE TABLE IF NOT EXISTS __SCHEMA__.provider_trust_states
(
    source_contract_id text COLLATE "C" NOT NULL,
    revision bigint NOT NULL,
    source_producer_module text COLLATE "C" NOT NULL,
    active_release_bom_sha256 text COLLATE "C" NOT NULL,
    provider_key_id text COLLATE "C" NOT NULL,
    provider_public_key_spki_base64 text COLLATE "C" NOT NULL,
    provider_public_key_sha256 text COLLATE "C" NOT NULL,
    status text COLLATE "C" NOT NULL,
    valid_from timestamptz NOT NULL,
    valid_until timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT provider_trust_states_pkey PRIMARY KEY (source_contract_id, revision),
    CONSTRAINT provider_trust_revision_positive CHECK (revision > 0),
    CONSTRAINT provider_trust_bom_hash CHECK (active_release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT provider_trust_key_id CHECK (provider_key_id ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$'),
    CONSTRAINT provider_trust_public_key_base64 CHECK (provider_public_key_spki_base64 ~ '^[A-Za-z0-9+/]+={0,2}$'),
    CONSTRAINT provider_trust_public_key_hash CHECK (provider_public_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT provider_trust_status_exact CHECK (status = 'ACTIVE' OR status = 'REVOKED'),
    CONSTRAINT provider_trust_window CHECK (valid_until > valid_from),
    CONSTRAINT provider_trust_owner_pair CHECK (
        (source_contract_id = 'device.registered/v1' AND source_producer_module = 'device-registry')
        OR (source_contract_id = 'platform.account.authorized/v1' AND source_producer_module = 'platform-account-registry')
        OR (source_contract_id = 'identity.binding/v1' AND source_producer_module = 'binding')
        OR (source_contract_id = 'persona.revision/v1' AND source_producer_module = 'persona-store')
        OR (source_contract_id = 'soul.memory.readback/v1' AND source_producer_module = 'soul-memory-adapter')
    )
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.runtime_truth
(
    truth_id uuid NOT NULL,
    business_key_sha256 text COLLATE "C" NOT NULL,
    scope_sha256 text COLLATE "C" NOT NULL,
    idempotency_key_sha256 text COLLATE "C" NOT NULL,
    record_sha256 text COLLATE "C" NOT NULL,
    schema_version text COLLATE "C" NOT NULL,
    source_contract_id text COLLATE "C" NOT NULL,
    source_producer_module text COLLATE "C" NOT NULL,
    soul_id text COLLATE "C" NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    platform_account_id text COLLATE "C" NOT NULL,
    trace_id text COLLATE "C" NOT NULL,
    idempotency_key text COLLATE "C" NOT NULL,
    occurred_at timestamptz NOT NULL,
    source_payload_sha256 text COLLATE "C" NOT NULL,
    source_payload_bytes bytea NOT NULL,
    result_status text COLLATE "C" NOT NULL,
    active_release_bom_sha256 text COLLATE "C" NOT NULL,
    provider_key_id text COLLATE "C" NOT NULL,
    provider_trust_revision bigint NOT NULL,
    provider_public_key_sha256 text COLLATE "C" NOT NULL,
    provider_signature_base64 text COLLATE "C" NOT NULL,
    provider_authorization_sha256 text COLLATE "C" NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT runtime_truth_pkey PRIMARY KEY (truth_id),
    CONSTRAINT runtime_truth_business_key_unique UNIQUE (business_key_sha256),
    CONSTRAINT runtime_truth_business_hash CHECK (business_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_scope_hash CHECK (scope_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_idempotency_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_record_hash CHECK (record_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_schema_major CHECK (schema_version ~ '^1(\.[0-9]+){0,2}$'),
    CONSTRAINT runtime_truth_soul_format CHECK (octet_length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_binding_format CHECK (octet_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT runtime_truth_account_format CHECK (octet_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT runtime_truth_trace_length CHECK (octet_length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    CONSTRAINT runtime_truth_idempotency_length CHECK (octet_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_payload_hash CHECK (source_payload_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_payload_bytes CHECK (
        octet_length(source_payload_bytes) >= 1
        AND octet_length(source_payload_bytes) <= 1048576
        AND encode(sha256(source_payload_bytes), 'hex') = source_payload_sha256),
    CONSTRAINT runtime_truth_bom_hash CHECK (active_release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_provider_key CHECK (provider_key_id ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$'),
    CONSTRAINT runtime_truth_provider_trust_revision CHECK (provider_trust_revision > 0),
    CONSTRAINT runtime_truth_provider_public_key_hash CHECK (provider_public_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_provider_signature CHECK (provider_signature_base64 ~ '^[A-Za-z0-9+/]{86}==$'),
    CONSTRAINT runtime_truth_authorization_hash CHECK (provider_authorization_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT runtime_truth_allowlisted_result CHECK (
        (source_contract_id = 'device.registered/v1'
            AND source_producer_module = 'device-registry'
            AND (result_status = 'registered' OR result_status = 'retired'))
        OR (source_contract_id = 'platform.account.authorized/v1'
            AND source_producer_module = 'platform-account-registry'
            AND (result_status = 'authorized' OR result_status = 'suspended' OR result_status = 'revoked'))
        OR (source_contract_id = 'identity.binding/v1'
            AND source_producer_module = 'binding'
            AND (result_status = 'active' OR result_status = 'revoked'))
        OR (source_contract_id = 'persona.revision/v1'
            AND source_producer_module = 'persona-store'
            AND (result_status = 'active' OR result_status = 'deleted'))
        OR (source_contract_id = 'soul.memory.readback/v1'
            AND source_producer_module = 'soul-memory-adapter'
            AND result_status = 'verified')
    )
);

CREATE INDEX IF NOT EXISTS runtime_truth_exact_scope_idx
    ON __SCHEMA__.runtime_truth
        (soul_id, device_binding_id, platform_account_id, source_contract_id, occurred_at, truth_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_receipts
(
    receipt_id text COLLATE "C" NOT NULL,
    truth_id uuid NOT NULL,
    business_key_sha256 text COLLATE "C" NOT NULL,
    scope_sha256 text COLLATE "C" NOT NULL,
    idempotency_key_sha256 text COLLATE "C" NOT NULL,
    record_sha256 text COLLATE "C" NOT NULL,
    soul_id text COLLATE "C" NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    platform_account_id text COLLATE "C" NOT NULL,
    source_contract_id text COLLATE "C" NOT NULL,
    source_producer_module text COLLATE "C" NOT NULL,
    trace_id text COLLATE "C" NOT NULL,
    idempotency_key text COLLATE "C" NOT NULL,
    occurred_at timestamptz NOT NULL,
    source_payload_sha256 text COLLATE "C" NOT NULL,
    receipt_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT idempotency_receipts_pkey PRIMARY KEY (receipt_id),
    CONSTRAINT idempotency_receipts_truth_unique UNIQUE (truth_id),
    CONSTRAINT idempotency_receipts_truth_fk FOREIGN KEY (truth_id)
        REFERENCES __SCHEMA__.runtime_truth(truth_id) ON DELETE RESTRICT,
    CONSTRAINT idempotency_receipts_business_key_unique UNIQUE (business_key_sha256),
    CONSTRAINT idempotency_receipts_id_format CHECK (receipt_id ~ '^receipt_[a-f0-9]{32}$'),
    CONSTRAINT idempotency_receipts_business_hash CHECK (business_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_scope_hash CHECK (scope_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_key_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_record_hash CHECK (record_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_soul_format CHECK (octet_length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_binding_format CHECK (octet_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT idempotency_receipts_account_format CHECK (octet_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT idempotency_receipts_trace_length CHECK (octet_length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    CONSTRAINT idempotency_receipts_idempotency_length CHECK (octet_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_payload_hash CHECK (source_payload_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_receipts_json_keys CHECK ((__SCHEMA__.receipt_json_has_exact_keys(receipt_json)) IS TRUE),
    CONSTRAINT idempotency_receipts_json_contract CHECK ((receipt_json ->> 'contract_id' = 'control.plane.receipt/v1') IS TRUE),
    CONSTRAINT idempotency_receipts_json_producer CHECK ((receipt_json ->> 'producer_module' = 'control-plane-host') IS TRUE),
    CONSTRAINT idempotency_receipts_json_scope CHECK ((
        receipt_json ->> 'soul_id' = soul_id
        AND receipt_json ->> 'device_binding_id' = device_binding_id
        AND receipt_json ->> 'platform_account_id' = platform_account_id) IS TRUE),
    CONSTRAINT idempotency_receipts_json_source CHECK ((
        receipt_json ->> 'source_contract_id' = source_contract_id
        AND receipt_json ->> 'source_producer_module' = source_producer_module
        AND receipt_json ->> 'source_payload_sha256' = source_payload_sha256) IS TRUE),
    CONSTRAINT idempotency_receipts_json_request CHECK ((
        receipt_json ->> 'trace_id' = trace_id
        AND receipt_json ->> 'idempotency_key' = idempotency_key
        AND (receipt_json ->> 'occurred_at')::timestamptz = occurred_at) IS TRUE),
    CONSTRAINT idempotency_receipts_json_id CHECK ((receipt_json ->> 'receipt_id' = receipt_id) IS TRUE),
    CONSTRAINT idempotency_receipts_json_decision CHECK ((receipt_json ->> 'decision' = 'accepted') IS TRUE)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_quarantine
(
    quarantine_id uuid NOT NULL,
    business_key_sha256 text COLLATE "C" NOT NULL,
    soul_id text COLLATE "C" NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    platform_account_id text COLLATE "C" NOT NULL,
    source_contract_id text COLLATE "C" NOT NULL,
    scope_sha256 text COLLATE "C" NOT NULL,
    idempotency_key_sha256 text COLLATE "C" NOT NULL,
    existing_record_sha256 text COLLATE "C" NOT NULL,
    incoming_record_sha256 text COLLATE "C" NOT NULL,
    reason text COLLATE "C" NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT idempotency_quarantine_pkey PRIMARY KEY (quarantine_id),
    CONSTRAINT idempotency_quarantine_conflict_unique UNIQUE
        (business_key_sha256, existing_record_sha256, incoming_record_sha256),
    CONSTRAINT idempotency_quarantine_business_hash CHECK (business_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_soul_format CHECK (octet_length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_binding_format CHECK (octet_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT idempotency_quarantine_account_format CHECK (octet_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT idempotency_quarantine_contract_known CHECK (
        source_contract_id = 'device.registered/v1'
        OR source_contract_id = 'platform.account.authorized/v1'
        OR source_contract_id = 'identity.binding/v1'
        OR source_contract_id = 'persona.revision/v1'
        OR source_contract_id = 'soul.memory.readback/v1'),
    CONSTRAINT idempotency_quarantine_scope_hash CHECK (scope_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_key_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_existing_hash CHECK (existing_record_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_incoming_hash CHECK (incoming_record_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT idempotency_quarantine_reason_exact CHECK (reason = 'scoped_idempotency_digest_conflict')
);

CREATE INDEX IF NOT EXISTS idempotency_quarantine_exact_scope_idx
    ON __SCHEMA__.idempotency_quarantine
        (soul_id, device_binding_id, platform_account_id, created_at, quarantine_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox
(
    outbox_id uuid NOT NULL,
    receipt_id text COLLATE "C" NOT NULL,
    business_key_sha256 text COLLATE "C" NOT NULL,
    soul_id text COLLATE "C" NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    platform_account_id text COLLATE "C" NOT NULL,
    scope_sha256 text COLLATE "C" NOT NULL,
    idempotency_key_sha256 text COLLATE "C" NOT NULL,
    record_sha256 text COLLATE "C" NOT NULL,
    source_contract_id text COLLATE "C" NOT NULL,
    source_producer_module text COLLATE "C" NOT NULL,
    trace_id text COLLATE "C" NOT NULL,
    idempotency_key text COLLATE "C" NOT NULL,
    source_payload_sha256 text COLLATE "C" NOT NULL,
    topic text COLLATE "C" NOT NULL,
    payload_sha256 text COLLATE "C" NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT outbox_pkey PRIMARY KEY (outbox_id),
    CONSTRAINT outbox_receipt_unique UNIQUE (receipt_id),
    CONSTRAINT outbox_receipt_fk FOREIGN KEY (receipt_id)
        REFERENCES __SCHEMA__.idempotency_receipts(receipt_id) ON DELETE RESTRICT,
    CONSTRAINT outbox_business_key_unique UNIQUE (business_key_sha256),
    CONSTRAINT outbox_business_hash CHECK (business_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT outbox_soul_format CHECK (octet_length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT outbox_binding_format CHECK (octet_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT outbox_account_format CHECK (octet_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT outbox_scope_hash CHECK (scope_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT outbox_idempotency_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT outbox_record_hash CHECK (record_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT outbox_trace_length CHECK (octet_length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    CONSTRAINT outbox_idempotency_length CHECK (octet_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT outbox_source_payload_hash CHECK (source_payload_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT outbox_topic_exact CHECK (topic = 'control.plane.receipt/v1'),
    CONSTRAINT outbox_payload_hash CHECK (payload_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT outbox_payload_keys CHECK ((__SCHEMA__.receipt_json_has_exact_keys(payload_json)) IS TRUE),
    CONSTRAINT outbox_payload_id CHECK ((payload_json ->> 'receipt_id' = receipt_id) IS TRUE),
    CONSTRAINT outbox_payload_scope CHECK ((
        payload_json ->> 'soul_id' = soul_id
        AND payload_json ->> 'device_binding_id' = device_binding_id
        AND payload_json ->> 'platform_account_id' = platform_account_id) IS TRUE),
    CONSTRAINT outbox_payload_source CHECK ((
        payload_json ->> 'source_contract_id' = source_contract_id
        AND payload_json ->> 'source_producer_module' = source_producer_module
        AND payload_json ->> 'source_payload_sha256' = source_payload_sha256) IS TRUE),
    CONSTRAINT outbox_payload_request CHECK ((
        payload_json ->> 'trace_id' = trace_id
        AND payload_json ->> 'idempotency_key' = idempotency_key
        AND (payload_json ->> 'occurred_at')::timestamptz = occurred_at) IS TRUE),
    CONSTRAINT outbox_payload_contract CHECK ((payload_json ->> 'contract_id' = topic) IS TRUE),
    CONSTRAINT outbox_payload_producer CHECK ((payload_json ->> 'producer_module' = 'control-plane-host') IS TRUE),
    CONSTRAINT outbox_payload_decision CHECK ((payload_json ->> 'decision' = 'accepted') IS TRUE)
);

CREATE INDEX IF NOT EXISTS outbox_exact_scope_idx
    ON __SCHEMA__.outbox
        (soul_id, device_binding_id, platform_account_id, created_at, outbox_id);

DO $constraint_baseline$
DECLARE
    baseline_marker constant text :=
        'dps.control-plane-host.constraint-definition-baseline/v1';
    current_marker text;
    bootstrap_new_schema boolean;
    constraint_value record;
    expected_comment text;
    observed_count integer := 0;
BEGIN
    bootstrap_new_schema := COALESCE(
        pg_catalog.current_setting(
            'dps.control_plane.bootstrap_new_schema',
            true)::boolean,
        false);
    SELECT pg_catalog.obj_description(n.oid, 'pg_namespace')
    INTO current_marker
    FROM pg_catalog.pg_namespace n
    WHERE n.nspname = '__SCHEMA__';

    IF current_marker IS NULL AND NOT bootstrap_new_schema THEN
        RAISE EXCEPTION 'control-plane constraint baseline cannot adopt an existing schema';
    ELSIF current_marker IS NOT NULL AND bootstrap_new_schema THEN
        RAISE EXCEPTION 'control-plane new-schema bootstrap marker is inconsistent';
    ELSIF current_marker IS NOT NULL AND current_marker <> baseline_marker THEN
        RAISE EXCEPTION 'control-plane schema baseline marker is unknown';
    END IF;

    FOR constraint_value IN
        SELECT constraint_state.oid,
               constraint_state.conname,
               table_value.relname AS table_name,
               pg_catalog.pg_get_constraintdef(constraint_state.oid, false) AS definition,
               pg_catalog.obj_description(
                   constraint_state.oid,
                   'pg_constraint') AS stored_comment
        FROM pg_catalog.pg_constraint constraint_state
        JOIN pg_catalog.pg_class table_value
          ON table_value.oid = constraint_state.conrelid
        JOIN pg_catalog.pg_namespace namespace_value
          ON namespace_value.oid = table_value.relnamespace
        WHERE namespace_value.nspname = '__SCHEMA__'
          AND table_value.relname = ANY(ARRAY[
              'provider_trust_states',
              'runtime_truth',
              'idempotency_receipts',
              'idempotency_quarantine',
              'outbox'
          ]::text[])
          AND constraint_state.contype <> 'n'
        ORDER BY table_value.relname, constraint_state.conname
    LOOP
        observed_count := observed_count + 1;
        expected_comment := baseline_marker || E'\n' || constraint_value.definition;
        IF current_marker IS NULL THEN
            EXECUTE pg_catalog.format(
                'COMMENT ON CONSTRAINT %I ON %I.%I IS %L',
                constraint_value.conname,
                '__SCHEMA__',
                constraint_value.table_name,
                expected_comment);
        ELSIF constraint_value.stored_comment IS DISTINCT FROM expected_comment THEN
            RAISE EXCEPTION 'control-plane constraint definition baseline drift: %',
                constraint_value.conname;
        END IF;
    END LOOP;

    IF observed_count <> 89 THEN
        RAISE EXCEPTION 'control-plane constraint definition baseline count mismatch: %',
            observed_count;
    END IF;

    IF current_marker IS NULL THEN
        EXECUTE pg_catalog.format(
            'COMMENT ON SCHEMA %I IS %L',
            '__SCHEMA__',
            baseline_marker);
    END IF;
END;
$constraint_baseline$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_control_plane_row_mutation()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
BEGIN
    RAISE EXCEPTION 'control-plane-host runtime truth is append-only';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_control_plane_truncate()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
BEGIN
    RAISE EXCEPTION 'control-plane-host runtime truth cannot be truncated';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.enforce_runtime_truth_provider_trust()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.provider_trust_states trust
        WHERE trust.source_contract_id = NEW.source_contract_id
          AND trust.revision = (
              SELECT max(latest.revision)
              FROM __SCHEMA__.provider_trust_states latest
              WHERE latest.source_contract_id = NEW.source_contract_id)
          AND trust.source_producer_module = NEW.source_producer_module
          AND trust.revision = NEW.provider_trust_revision
          AND trust.active_release_bom_sha256 = NEW.active_release_bom_sha256
          AND trust.provider_key_id = NEW.provider_key_id
          AND trust.provider_public_key_sha256 = NEW.provider_public_key_sha256
          AND trust.status = 'ACTIVE'
          AND clock_timestamp() >= trust.valid_from
          AND clock_timestamp() <= trust.valid_until
          AND NEW.occurred_at >= trust.valid_from
          AND NEW.occurred_at <= trust.valid_until)
    THEN
        RAISE EXCEPTION 'runtime truth lacks current provider trust';
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS runtime_truth_provider_trust ON __SCHEMA__.runtime_truth;
CREATE TRIGGER runtime_truth_provider_trust
BEFORE INSERT ON __SCHEMA__.runtime_truth
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.enforce_runtime_truth_provider_trust();

CREATE OR REPLACE FUNCTION __SCHEMA__.enforce_receipt_truth_link()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.runtime_truth truth
        WHERE truth.truth_id = NEW.truth_id
          AND truth.business_key_sha256 = NEW.business_key_sha256
          AND truth.scope_sha256 = NEW.scope_sha256
          AND truth.idempotency_key_sha256 = NEW.idempotency_key_sha256
          AND truth.record_sha256 = NEW.record_sha256
          AND truth.soul_id = NEW.soul_id
          AND truth.device_binding_id = NEW.device_binding_id
          AND truth.platform_account_id = NEW.platform_account_id
          AND truth.source_contract_id = NEW.source_contract_id
          AND truth.source_producer_module = NEW.source_producer_module
          AND truth.trace_id = NEW.trace_id
          AND truth.idempotency_key = NEW.idempotency_key
          AND truth.occurred_at = NEW.occurred_at
          AND truth.source_payload_sha256 = NEW.source_payload_sha256)
    THEN
        RAISE EXCEPTION 'receipt does not exactly bind its runtime truth';
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS idempotency_receipts_truth_link ON __SCHEMA__.idempotency_receipts;
CREATE TRIGGER idempotency_receipts_truth_link
BEFORE INSERT ON __SCHEMA__.idempotency_receipts
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.enforce_receipt_truth_link();

CREATE OR REPLACE FUNCTION __SCHEMA__.enforce_outbox_receipt_link()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.idempotency_receipts receipt
        WHERE receipt.receipt_id = NEW.receipt_id
          AND receipt.business_key_sha256 = NEW.business_key_sha256
          AND receipt.scope_sha256 = NEW.scope_sha256
          AND receipt.idempotency_key_sha256 = NEW.idempotency_key_sha256
          AND receipt.record_sha256 = NEW.record_sha256
          AND receipt.soul_id = NEW.soul_id
          AND receipt.device_binding_id = NEW.device_binding_id
          AND receipt.platform_account_id = NEW.platform_account_id
          AND receipt.source_contract_id = NEW.source_contract_id
          AND receipt.source_producer_module = NEW.source_producer_module
          AND receipt.trace_id = NEW.trace_id
          AND receipt.idempotency_key = NEW.idempotency_key
          AND receipt.occurred_at = NEW.occurred_at
          AND receipt.source_payload_sha256 = NEW.source_payload_sha256
          AND receipt.receipt_json = NEW.payload_json)
    THEN
        RAISE EXCEPTION 'outbox does not exactly bind its receipt';
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS outbox_receipt_link ON __SCHEMA__.outbox;
CREATE TRIGGER outbox_receipt_link
BEFORE INSERT ON __SCHEMA__.outbox
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.enforce_outbox_receipt_link();

CREATE OR REPLACE FUNCTION __SCHEMA__.commit_control_plane_atom(
    p_truth_id uuid,
    p_business_key_sha256 text,
    p_scope_sha256 text,
    p_idempotency_key_sha256 text,
    p_record_sha256 text,
    p_schema_version text,
    p_source_contract_id text,
    p_source_producer_module text,
    p_soul_id text,
    p_device_binding_id text,
    p_platform_account_id text,
    p_trace_id text,
    p_idempotency_key text,
    p_occurred_at timestamptz,
    p_source_payload_sha256 text,
    p_source_payload_bytes bytea,
    p_result_status text,
    p_active_release_bom_sha256 text,
    p_provider_key_id text,
    p_provider_trust_revision bigint,
    p_provider_public_key_sha256 text,
    p_provider_signature_base64 text,
    p_provider_authorization_sha256 text,
    p_receipt_id text,
    p_receipt_json jsonb,
    p_outbox_id uuid,
    p_outbox_payload_sha256 text,
    p_abort_after_stage text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $function$
BEGIN
    IF session_user <> '__RUNTIME_ROLE__'
       OR current_setting('session_replication_role') <> 'origin'
    THEN
        RAISE EXCEPTION 'control-plane atom caller is not the declared runtime role';
    END IF;
    IF p_abort_after_stage IS NOT NULL
       AND p_abort_after_stage NOT IN (
           'TruthWritten', 'ReceiptWritten', 'OutboxWritten', 'BeforeCommit')
    THEN
        RAISE EXCEPTION 'unknown control-plane integration abort stage';
    END IF;

    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended(p_source_contract_id, 0));
    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended(p_business_key_sha256, 0));

    INSERT INTO __SCHEMA__.runtime_truth
        (truth_id, business_key_sha256, scope_sha256, idempotency_key_sha256,
         record_sha256, schema_version, source_contract_id,
         source_producer_module, soul_id, device_binding_id,
         platform_account_id, trace_id, idempotency_key, occurred_at,
         source_payload_sha256, source_payload_bytes, result_status,
         active_release_bom_sha256, provider_key_id,
         provider_trust_revision, provider_public_key_sha256,
         provider_signature_base64, provider_authorization_sha256)
    VALUES
        (p_truth_id, p_business_key_sha256, p_scope_sha256,
         p_idempotency_key_sha256, p_record_sha256, p_schema_version,
         p_source_contract_id, p_source_producer_module, p_soul_id,
         p_device_binding_id, p_platform_account_id, p_trace_id,
         p_idempotency_key, p_occurred_at, p_source_payload_sha256,
         p_source_payload_bytes, p_result_status,
         p_active_release_bom_sha256, p_provider_key_id,
         p_provider_trust_revision, p_provider_public_key_sha256,
         p_provider_signature_base64, p_provider_authorization_sha256);
    IF p_abort_after_stage = 'TruthWritten' THEN
        RAISE EXCEPTION 'injected control-plane crash after truth';
    END IF;

    INSERT INTO __SCHEMA__.idempotency_receipts
        (receipt_id, truth_id, business_key_sha256, scope_sha256,
         idempotency_key_sha256, record_sha256, soul_id,
         device_binding_id, platform_account_id, source_contract_id,
         source_producer_module, trace_id, idempotency_key, occurred_at,
         source_payload_sha256, receipt_json)
    VALUES
        (p_receipt_id, p_truth_id, p_business_key_sha256, p_scope_sha256,
         p_idempotency_key_sha256, p_record_sha256, p_soul_id,
         p_device_binding_id, p_platform_account_id, p_source_contract_id,
         p_source_producer_module, p_trace_id, p_idempotency_key,
         p_occurred_at, p_source_payload_sha256, p_receipt_json);
    IF p_abort_after_stage = 'ReceiptWritten' THEN
        RAISE EXCEPTION 'injected control-plane crash after receipt';
    END IF;

    INSERT INTO __SCHEMA__.outbox
        (outbox_id, receipt_id, business_key_sha256, soul_id,
         device_binding_id, platform_account_id, scope_sha256,
         idempotency_key_sha256, record_sha256, source_contract_id,
         source_producer_module, trace_id, idempotency_key,
         source_payload_sha256, topic, payload_sha256, payload_json,
         occurred_at)
    VALUES
        (p_outbox_id, p_receipt_id, p_business_key_sha256, p_soul_id,
         p_device_binding_id, p_platform_account_id, p_scope_sha256,
         p_idempotency_key_sha256, p_record_sha256, p_source_contract_id,
         p_source_producer_module, p_trace_id, p_idempotency_key,
         p_source_payload_sha256, 'control.plane.receipt/v1',
         p_outbox_payload_sha256, p_receipt_json, p_occurred_at);
    IF p_abort_after_stage = 'OutboxWritten' THEN
        RAISE EXCEPTION 'injected control-plane crash after outbox';
    END IF;
    IF p_abort_after_stage = 'BeforeCommit' THEN
        RAISE EXCEPTION 'injected control-plane crash before commit';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.append_control_plane_quarantine(
    p_quarantine_id uuid,
    p_business_key_sha256 text,
    p_soul_id text,
    p_device_binding_id text,
    p_platform_account_id text,
    p_source_contract_id text,
    p_scope_sha256 text,
    p_idempotency_key_sha256 text,
    p_existing_record_sha256 text,
    p_incoming_record_sha256 text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $function$
BEGIN
    IF session_user <> '__RUNTIME_ROLE__'
       OR current_setting('session_replication_role') <> 'origin'
    THEN
        RAISE EXCEPTION 'control-plane quarantine caller is not the declared runtime role';
    END IF;

    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended(p_business_key_sha256, 0));
    INSERT INTO __SCHEMA__.idempotency_quarantine
        (quarantine_id, business_key_sha256, soul_id, device_binding_id,
         platform_account_id, source_contract_id, scope_sha256,
         idempotency_key_sha256, existing_record_sha256,
         incoming_record_sha256, reason)
    VALUES
        (p_quarantine_id, p_business_key_sha256, p_soul_id,
         p_device_binding_id, p_platform_account_id, p_source_contract_id,
         p_scope_sha256, p_idempotency_key_sha256,
         p_existing_record_sha256, p_incoming_record_sha256,
         'scoped_idempotency_digest_conflict')
    ON CONFLICT (business_key_sha256, existing_record_sha256,
                 incoming_record_sha256)
    DO NOTHING;
END;
$function$;

DO $block$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'provider_trust_states',
        'runtime_truth',
        'idempotency_receipts',
        'idempotency_quarantine',
        'outbox'
    ]
    LOOP
        EXECUTE format(
            'DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I',
            table_name || '_append_only_rows',
            table_name);
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON __SCHEMA__.%I FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_control_plane_row_mutation()',
            table_name || '_append_only_rows',
            table_name);
        EXECUTE format(
            'DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I',
            table_name || '_no_truncate',
            table_name);
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE TRUNCATE ON __SCHEMA__.%I FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_control_plane_truncate()',
            table_name || '_no_truncate',
            table_name);
    END LOOP;
END;
$block$;

REVOKE ALL ON SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM PUBLIC;

REVOKE ALL ON SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;
REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;

GRANT USAGE ON SCHEMA __SCHEMA__ TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.provider_trust_states TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.runtime_truth TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.idempotency_receipts TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.idempotency_quarantine TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.outbox TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.receipt_json_has_exact_keys(jsonb) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.commit_control_plane_atom(
    uuid, text, text, text, text, text, text, text, text, text, text,
    text, text, timestamptz, text, bytea, text, text, text, bigint,
    text, text, text, text, jsonb, uuid, text, text) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.append_control_plane_quarantine(
    uuid, text, text, text, text, text, text, text, text, text)
    TO __RUNTIME_ROLE__;

ALTER DEFAULT PRIVILEGES IN SCHEMA __SCHEMA__ REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA __SCHEMA__ REVOKE ALL ON FUNCTIONS FROM PUBLIC;
