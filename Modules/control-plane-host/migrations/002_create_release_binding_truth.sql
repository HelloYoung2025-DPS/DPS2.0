DO $bootstrap_preflight$
DECLARE
    schema_oid oid;
    schema_marker text;
    expected_marker constant text :=
        'dps.control-plane-host.release-binding-baseline/v1';
BEGIN
    SELECT n.oid,
           pg_catalog.obj_description(n.oid, 'pg_namespace')
    INTO schema_oid, schema_marker
    FROM pg_catalog.pg_namespace n
    WHERE n.nspname = '__SCHEMA__';

    IF schema_oid IS NULL THEN
        EXECUTE pg_catalog.format('CREATE SCHEMA %I', '__SCHEMA__');
        EXECUTE pg_catalog.format(
            'COMMENT ON SCHEMA %I IS %L',
            '__SCHEMA__',
            expected_marker);
    ELSIF schema_marker IS NULL THEN
        RAISE EXCEPTION
            'release-binding migration refuses an unmarked pre-existing schema';
    ELSIF schema_marker <> expected_marker THEN
        RAISE EXCEPTION 'release-binding schema baseline marker is unknown';
    END IF;
END;
$bootstrap_preflight$;
ALTER SCHEMA __SCHEMA__ OWNER TO CURRENT_USER;

CREATE TABLE IF NOT EXISTS __SCHEMA__.release_binding_journal
(
    device_binding_id text COLLATE "C" NOT NULL,
    sequence bigint NOT NULL,
    receipt_kind text COLLATE "C" NOT NULL,
    receipt_wire bytea NOT NULL,
    current_binding_wire bytea NOT NULL,
    previous_binding_wire bytea,
    last_activation_signer_generation bigint NOT NULL,
    request_sha256 text COLLATE "C" NOT NULL,
    signed_bom_wire bytea,
    previous_stable_bom_wire bytea,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT release_binding_journal_pkey PRIMARY KEY (device_binding_id, sequence),
    CONSTRAINT release_binding_journal_device CHECK (
        octet_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT release_binding_journal_sequence_positive CHECK (sequence > 0),
    CONSTRAINT release_binding_journal_kind CHECK (
        receipt_kind = 'activation'
        OR receipt_kind = 'revocation'
        OR receipt_kind = 'rollback'),
    CONSTRAINT release_binding_journal_receipt_bytes CHECK (
        octet_length(receipt_wire) >= 1 AND octet_length(receipt_wire) <= 16384),
    CONSTRAINT release_binding_journal_current_bytes CHECK (
        octet_length(current_binding_wire) >= 1
        AND octet_length(current_binding_wire) <= 16384),
    CONSTRAINT release_binding_journal_previous_bytes CHECK (
        previous_binding_wire IS NULL
        OR (octet_length(previous_binding_wire) >= 1
            AND octet_length(previous_binding_wire) <= 16384)),
    CONSTRAINT release_binding_journal_signer_generation CHECK (
        last_activation_signer_generation >= 0),
    CONSTRAINT release_binding_journal_request_hash CHECK (
        request_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT release_binding_journal_bom_only_off_revocation CHECK (
        (receipt_kind = 'revocation') = (signed_bom_wire IS NULL)),
    CONSTRAINT release_binding_journal_bom_bytes CHECK (
        signed_bom_wire IS NULL
        OR (octet_length(signed_bom_wire) >= 1
            AND octet_length(signed_bom_wire) <= 4194304))
);

-- This migration is deliberately re-runnable against an already marked
-- R0-C development schema. CREATE TABLE IF NOT EXISTS cannot add a later
-- column, so add it explicitly and replace both constraints with their exact
-- fail-closed definitions. Existing rows that cannot prove the new invariant
-- make the migration fail instead of being grandfathered.
ALTER TABLE __SCHEMA__.release_binding_journal
    ADD COLUMN IF NOT EXISTS previous_stable_bom_wire bytea;
ALTER TABLE __SCHEMA__.release_binding_journal
    DROP CONSTRAINT IF EXISTS release_binding_journal_bom_bytes;
ALTER TABLE __SCHEMA__.release_binding_journal
    ADD CONSTRAINT release_binding_journal_bom_bytes CHECK (
        signed_bom_wire IS NULL
        OR (octet_length(signed_bom_wire) >= 1
            AND octet_length(signed_bom_wire) <= 4194304));
ALTER TABLE __SCHEMA__.release_binding_journal
    DROP CONSTRAINT IF EXISTS release_binding_journal_previous_stable_bom_shape;
ALTER TABLE __SCHEMA__.release_binding_journal
    ADD CONSTRAINT release_binding_journal_previous_stable_bom_shape CHECK (
        (previous_stable_bom_wire IS NOT NULL)
        = (receipt_kind = 'activation' AND sequence > 1));
ALTER TABLE __SCHEMA__.release_binding_journal
    DROP CONSTRAINT IF EXISTS release_binding_journal_previous_stable_bom_bytes;
ALTER TABLE __SCHEMA__.release_binding_journal
    ADD CONSTRAINT release_binding_journal_previous_stable_bom_bytes CHECK (
        previous_stable_bom_wire IS NULL
        OR (octet_length(previous_stable_bom_wire) >= 1
            AND octet_length(previous_stable_bom_wire) <= 4194304));

CREATE TABLE IF NOT EXISTS __SCHEMA__.release_binding_recovery_fences
(
    recovery_id uuid NOT NULL,
    device_binding_id text COLLATE "C" NOT NULL,
    journal_sequence bigint NOT NULL,
    release_bom_sha256 text COLLATE "C" NOT NULL,
    release_bom_generation bigint NOT NULL,
    recovery_content_sha256 text COLLATE "C" NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT release_binding_recovery_fences_pkey PRIMARY KEY (recovery_id),
    CONSTRAINT release_binding_recovery_fences_device CHECK (
        octet_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT release_binding_recovery_fences_sequence CHECK (journal_sequence > 0),
    CONSTRAINT release_binding_recovery_fences_bom_hash CHECK (
        release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT release_binding_recovery_fences_generation CHECK (
        release_bom_generation > 0),
    CONSTRAINT release_binding_recovery_fences_content_hash CHECK (
        recovery_content_sha256 ~ '^[a-f0-9]{64}$')
);

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_release_binding_row_mutation()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
BEGIN
    RAISE EXCEPTION 'release binding truth is append-only';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_release_binding_truncate()
RETURNS trigger
LANGUAGE plpgsql
SECURITY INVOKER
SET search_path = pg_catalog
AS $function$
BEGIN
    RAISE EXCEPTION 'release binding truth cannot be truncated';
END;
$function$;

DO $append_only_triggers$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'release_binding_journal',
        'release_binding_recovery_fences'
    ]
    LOOP
        EXECUTE format(
            'DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I',
            table_name || '_append_only_rows',
            table_name);
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON __SCHEMA__.%I FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_release_binding_row_mutation()',
            table_name || '_append_only_rows',
            table_name);
        EXECUTE format(
            'DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I',
            table_name || '_no_truncate',
            table_name);
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE TRUNCATE ON __SCHEMA__.%I FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_release_binding_truncate()',
            table_name || '_no_truncate',
            table_name);
    END LOOP;
END;
$append_only_triggers$;

-- A changed input signature creates a PostgreSQL overload rather than
-- replacing the old SECURITY DEFINER function. Remove the exact legacy
-- overload so the runtime retains one unambiguous least-privilege append
-- surface.
DROP FUNCTION IF EXISTS __SCHEMA__.append_release_binding_record(
    text, bigint, text, bytea, bytea, bytea, bigint, text, bytea);

CREATE OR REPLACE FUNCTION __SCHEMA__.append_release_binding_record(
    p_device_binding_id text,
    p_sequence bigint,
    p_receipt_kind text,
    p_receipt_wire bytea,
    p_current_binding_wire bytea,
    p_previous_binding_wire bytea,
    p_last_activation_signer_generation bigint,
    p_request_sha256 text,
    p_signed_bom_wire bytea,
    p_previous_stable_bom_wire bytea)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $function$
DECLARE
    last_sequence bigint;
    receipt_document jsonb;
    current_document jsonb;
BEGIN
    IF session_user <> '__RUNTIME_ROLE__'
       OR current_setting('session_replication_role') <> 'origin'
    THEN
        RAISE EXCEPTION 'release binding append caller is not the declared runtime role';
    END IF;

    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended('release-binding:' || p_device_binding_id, 0));

    receipt_document := convert_from(p_receipt_wire, 'UTF8')::jsonb;
    current_document := convert_from(p_current_binding_wire, 'UTF8')::jsonb;
    IF (receipt_document ->> 'sequence')::bigint IS DISTINCT FROM p_sequence
       OR receipt_document ->> 'device_binding_id' IS DISTINCT FROM p_device_binding_id
       OR receipt_document ->> 'receipt_kind' IS DISTINCT FROM p_receipt_kind
       OR current_document ->> 'device_binding_id' IS DISTINCT FROM p_device_binding_id
    THEN
        RAISE EXCEPTION 'release binding append wires do not bind their declared identity';
    END IF;

    SELECT COALESCE(max(journal.sequence), 0)
    INTO last_sequence
    FROM __SCHEMA__.release_binding_journal journal
    WHERE journal.device_binding_id = p_device_binding_id;

    IF p_sequence <> last_sequence + 1 THEN
        RAISE EXCEPTION 'release binding journal sequence conflict';
    END IF;

    INSERT INTO __SCHEMA__.release_binding_journal
        (device_binding_id, sequence, receipt_kind, receipt_wire,
         current_binding_wire, previous_binding_wire,
         last_activation_signer_generation, request_sha256, signed_bom_wire,
         previous_stable_bom_wire)
    VALUES
        (p_device_binding_id, p_sequence, p_receipt_kind, p_receipt_wire,
         p_current_binding_wire, p_previous_binding_wire,
         p_last_activation_signer_generation, p_request_sha256,
         p_signed_bom_wire, p_previous_stable_bom_wire);
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.commit_release_binding_recovery_fence(
    p_device_binding_id text,
    p_journal_sequence bigint,
    p_release_bom_sha256 text,
    p_release_bom_generation bigint,
    p_recovery_id uuid,
    p_recovery_content_sha256 text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog
AS $function$
DECLARE
    existing_fence record;
    head record;
    head_binding jsonb;
BEGIN
    IF session_user <> '__RUNTIME_ROLE__'
       OR current_setting('session_replication_role') <> 'origin'
    THEN
        RAISE EXCEPTION 'release binding fence caller is not the declared runtime role';
    END IF;

    PERFORM pg_catalog.pg_advisory_xact_lock(
        pg_catalog.hashtextextended('release-binding:' || p_device_binding_id, 0));

    SELECT fence.device_binding_id,
           fence.journal_sequence,
           fence.release_bom_sha256,
           fence.release_bom_generation,
           fence.recovery_content_sha256
    INTO existing_fence
    FROM __SCHEMA__.release_binding_recovery_fences fence
    WHERE fence.recovery_id = p_recovery_id;

    IF FOUND THEN
        -- Idempotent redelivery: the exact same recovery content for the
        -- exact same fenced journal position replays successfully; anything
        -- else on the same recovery_id fails closed.
        IF existing_fence.device_binding_id = p_device_binding_id
           AND existing_fence.journal_sequence = p_journal_sequence
           AND existing_fence.release_bom_sha256 = p_release_bom_sha256
           AND existing_fence.release_bom_generation = p_release_bom_generation
           AND existing_fence.recovery_content_sha256 = p_recovery_content_sha256
        THEN
            RETURN;
        END IF;
        RAISE EXCEPTION 'release binding recovery fence conflict';
    END IF;

    SELECT journal.sequence, journal.current_binding_wire
    INTO head
    FROM __SCHEMA__.release_binding_journal journal
    WHERE journal.device_binding_id = p_device_binding_id
    ORDER BY journal.sequence DESC
    LIMIT 1;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'release binding recovery fence conflict';
    END IF;

    head_binding := convert_from(head.current_binding_wire, 'UTF8')::jsonb;
    IF head.sequence <> p_journal_sequence
       OR head_binding ->> 'status' IS DISTINCT FROM 'active'
       OR head_binding ->> 'release_bom_sha256' IS DISTINCT FROM p_release_bom_sha256
       OR (head_binding ->> 'generation')::bigint IS DISTINCT FROM p_release_bom_generation
    THEN
        RAISE EXCEPTION 'release binding recovery fence conflict';
    END IF;

    INSERT INTO __SCHEMA__.release_binding_recovery_fences
        (recovery_id, device_binding_id, journal_sequence,
         release_bom_sha256, release_bom_generation, recovery_content_sha256)
    VALUES
        (p_recovery_id, p_device_binding_id, p_journal_sequence,
         p_release_bom_sha256, p_release_bom_generation,
         p_recovery_content_sha256);
END;
$function$;

REVOKE ALL ON SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM PUBLIC;

REVOKE ALL ON SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;
REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;

GRANT USAGE ON SCHEMA __SCHEMA__ TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.release_binding_journal TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.release_binding_recovery_fences TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.append_release_binding_record(
    text, bigint, text, bytea, bytea, bytea, bigint, text, bytea, bytea)
    TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.commit_release_binding_recovery_fence(
    text, bigint, text, bigint, uuid, text) TO __RUNTIME_ROLE__;

ALTER DEFAULT PRIVILEGES IN SCHEMA __SCHEMA__ REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES IN SCHEMA __SCHEMA__ REVOKE ALL ON FUNCTIONS FROM PUBLIC;
