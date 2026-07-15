REVOKE ALL ON SCHEMA __SCHEMA__ FROM PUBLIC;
GRANT USAGE ON SCHEMA __SCHEMA__ TO __RUNTIME_ROLE__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.schema_migrations
(
    migration_version integer PRIMARY KEY,
    migration_sha256 text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT schema_migrations_version_positive CHECK (migration_version >= 1),
    CONSTRAINT schema_migrations_hash CHECK (migration_sha256 ~ '^[a-f0-9]{64}$')
);

INSERT INTO __SCHEMA__.schema_migrations(migration_version, migration_sha256)
VALUES (1, '__MIGRATION_SHA256__')
ON CONFLICT (migration_version) DO NOTHING;

CREATE TABLE IF NOT EXISTS __SCHEMA__.schema_attestations
(
    migration_version integer PRIMARY KEY,
    catalog_sha256 text NOT NULL,
    attested_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT schema_attestations_version_positive CHECK (migration_version >= 1),
    CONSTRAINT schema_attestations_hash CHECK (catalog_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT schema_attestations_migration_fk
        FOREIGN KEY (migration_version)
        REFERENCES __SCHEMA__.schema_migrations(migration_version) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_composition_state
(
    composition_generation bigint PRIMARY KEY,
    attestation_sha256 text NOT NULL,
    release_bom_sha256 text NOT NULL,
    binding_instance_trust_epoch bigint NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT binding_composition_generation_positive CHECK (composition_generation >= 1),
    CONSTRAINT binding_composition_attestation_hash CHECK (attestation_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(attestation_sha256) = 64),
    CONSTRAINT binding_composition_release_bom_hash CHECK (release_bom_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(release_bom_sha256) = 64),
    CONSTRAINT binding_composition_trust_epoch_positive CHECK (binding_instance_trust_epoch >= 1),
    UNIQUE (attestation_sha256)
);

CREATE OR REPLACE FUNCTION __SCHEMA__.all_sha256(values_to_check text[])
RETURNS boolean
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $function$
    SELECT COALESCE(bool_and(value ~ '^[a-f0-9]{64}$' AND octet_length(value) = 64), false)
    FROM unnest(values_to_check) AS item(value);
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.is_strict_ordinal_array(values_to_check text[])
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
    SELECT array_position(values_to_check, NULL) IS NULL
       AND NOT EXISTS (
            SELECT 1
            FROM generate_subscripts(values_to_check, 1) AS position(index_value)
            WHERE index_value > array_lower(values_to_check, 1)
              AND values_to_check[index_value - 1] COLLATE "C" >= values_to_check[index_value] COLLATE "C");
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.is_persona_utc_timestamp_text(value_to_check text)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
    SELECT octet_length(value_to_check) BETWEEN 20 AND 33
       AND substring(
               value_to_check
               FROM '^(?:20[2-9][0-9]|21[0-9]{2})-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12][0-9]|3[01])T(?:[01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](?:\.[0-9]{1,7})?(?:Z|\+00:00)$') = value_to_check
       AND value_to_check::timestamptz IS NOT NULL;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.jsonb_has_exact_keys(document jsonb, expected_keys text[])
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
    SELECT jsonb_typeof(document) = 'object'
       AND (SELECT array_agg(key_value ORDER BY key_value COLLATE "C")
            FROM jsonb_object_keys(document) AS keys(key_value)) = expected_keys;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.jsonb_text_array_equals(document jsonb, member_name text, expected_values text[])
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
    SELECT jsonb_typeof(document -> member_name) = 'array'
       AND ARRAY(SELECT jsonb_array_elements_text(document -> member_name)) = expected_values;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.persona_traits_are_valid(document jsonb)
RETURNS boolean
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
    SELECT jsonb_typeof(document) = 'object'
       AND (SELECT count(*) FROM pg_catalog.jsonb_object_keys(document)) BETWEEN 1 AND 5
       AND NOT EXISTS (
            SELECT 1
            FROM jsonb_each(document) AS trait(key_value, json_value)
            WHERE jsonb_typeof(json_value) <> 'string'
               OR CASE key_value
                    WHEN 'curiosity' THEN json_value #>> '{}' NOT IN ('low', 'medium', 'high')
                    WHEN 'humor' THEN json_value #>> '{}' NOT IN ('low', 'subtle', 'playful')
                    WHEN 'pace' THEN json_value #>> '{}' NOT IN ('slow', 'balanced', 'fast')
                    WHEN 'sociality' THEN json_value #>> '{}' NOT IN ('reserved', 'balanced', 'outgoing')
                    WHEN 'tone' THEN json_value #>> '{}' NOT IN ('calm', 'warm', 'direct', 'playful', 'formal')
                    ELSE true
                  END);
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.hmac_sha256_exact(key_material bytea, payload bytea)
RETURNS bytea
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
DECLARE
    inner_pad bytea := decode(repeat('36', 64), 'hex');
    outer_pad bytea := decode(repeat('5c', 64), 'hex');
    index_value integer;
BEGIN
    IF octet_length(key_material) <> 32 THEN
        RAISE EXCEPTION 'Persona HMAC key must contain exactly 32 bytes';
    END IF;
    FOR index_value IN 0..31 LOOP
        inner_pad := set_byte(inner_pad, index_value, get_byte(inner_pad, index_value) # get_byte(key_material, index_value));
        outer_pad := set_byte(outer_pad, index_value, get_byte(outer_pad, index_value) # get_byte(key_material, index_value));
    END LOOP;
    RETURN sha256(outer_pad || sha256(inner_pad || payload));
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.canonical_text_v1(value_to_encode text)
RETURNS bytea
LANGUAGE sql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
    SELECT int4send(octet_length(convert_to(value_to_encode, 'UTF8'))) || convert_to(value_to_encode, 'UTF8');
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.dotnet_roundtrip_utc_text_v1(value_to_normalize text)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
DECLARE
    fractional text;
BEGIN
    IF NOT __SCHEMA__.is_persona_utc_timestamp_text(value_to_normalize) THEN
        RAISE EXCEPTION 'Persona timestamp is not canonical UTC text';
    END IF;
    fractional := substring(value_to_normalize FROM '\.([0-9]{1,7})(?:Z|\+00:00)$');
    RETURN substring(value_to_normalize FROM 1 FOR 19) || '.' ||
           rpad(COALESCE(fractional, ''), 7, '0') || '+00:00';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.system_text_json_utc_text_v1(value_to_normalize text)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
DECLARE
    fractional text;
BEGIN
    IF NOT __SCHEMA__.is_persona_utc_timestamp_text(value_to_normalize) THEN
        RAISE EXCEPTION 'Persona timestamp is not canonical UTC text';
    END IF;
    fractional := rtrim(COALESCE(substring(value_to_normalize FROM '\.([0-9]{1,7})(?:Z|\+00:00)$'), ''), '0');
    RETURN substring(value_to_normalize FROM 1 FOR 19) ||
           CASE WHEN fractional = '' THEN '' ELSE '.' || fractional END || '+00:00';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.persona_history_item_canonical_json_v1(
    item_document jsonb,
    dotnet_roundtrip_timestamp boolean)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
DECLARE
    revision_document jsonb := item_document -> 'revision';
    trait_keys_json text;
    evidence_json text;
    traits_json text;
BEGIN
    SELECT COALESCE(string_agg(to_jsonb(value)::text, ',' ORDER BY position), '')
      INTO trait_keys_json
      FROM jsonb_array_elements_text(revision_document -> 'trait_keys') WITH ORDINALITY AS values_to_encode(value, position);
    SELECT COALESCE(string_agg(to_jsonb(value)::text, ',' ORDER BY position), '')
      INTO evidence_json
      FROM jsonb_array_elements_text(revision_document -> 'evidence_sha256') WITH ORDINALITY AS values_to_encode(value, position);

    IF item_document -> 'traits' = 'null'::jsonb THEN
        traits_json := 'null';
    ELSE
        SELECT '{' || string_agg(to_jsonb(key_value)::text || ':' || to_jsonb(value_value)::text, ',' ORDER BY key_value COLLATE "C") || '}'
          INTO traits_json
          FROM jsonb_each_text(item_document -> 'traits') AS traits_to_encode(key_value, value_value);
    END IF;

    RETURN '{"revision":{' ||
           '"schema_version":' || to_jsonb(revision_document ->> 'schema_version')::text || ',' ||
           '"contract_id":' || to_jsonb(revision_document ->> 'contract_id')::text || ',' ||
           '"producer_module":' || to_jsonb(revision_document ->> 'producer_module')::text || ',' ||
           '"soul_id":' || to_jsonb(revision_document ->> 'soul_id')::text || ',' ||
           '"device_binding_id":' || to_jsonb(revision_document ->> 'device_binding_id')::text || ',' ||
           '"platform_account_id":' || to_jsonb(revision_document ->> 'platform_account_id')::text || ',' ||
           '"trace_id":' || to_jsonb(revision_document ->> 'trace_id')::text || ',' ||
           '"idempotency_key":' || to_jsonb(revision_document ->> 'idempotency_key')::text || ',' ||
           '"occurred_at":' || to_jsonb(
               CASE WHEN dotnet_roundtrip_timestamp
                    THEN __SCHEMA__.dotnet_roundtrip_utc_text_v1(revision_document ->> 'occurred_at')
                    ELSE __SCHEMA__.system_text_json_utc_text_v1(revision_document ->> 'occurred_at')
               END)::text || ',' ||
           '"privacy_class":' || to_jsonb(revision_document ->> 'privacy_class')::text || ',' ||
           '"persona_revision":' || (revision_document ->> 'persona_revision')::bigint::text || ',' ||
           '"traits_sha256":' || to_jsonb(revision_document ->> 'traits_sha256')::text || ',' ||
           '"trait_keys":[' || trait_keys_json || '],' ||
           '"evidence_sha256":[' || evidence_json || '],' ||
           '"status":' || to_jsonb(revision_document ->> 'status')::text || '},' ||
           '"live_primary_payload_state":' || to_jsonb(item_document ->> 'live_primary_payload_state')::text || ',' ||
           '"traits":' || traits_json || '}';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.persona_traits_commitment_v1(key_material bytea, traits_document jsonb)
RETURNS text
LANGUAGE plpgsql
IMMUTABLE
STRICT
PARALLEL SAFE
AS $function$
DECLARE
    domain_value constant text := 'dps.persona-store.traits-hmac-sha256/v1';
    canonical bytea;
    trait record;
    key_bytes bytea;
    value_bytes bytea;
BEGIN
    IF NOT __SCHEMA__.persona_traits_are_valid(traits_document) THEN
        RAISE EXCEPTION 'Persona traits violate the closed v1 vocabulary';
    END IF;
    canonical := int4send(octet_length(convert_to(domain_value, 'UTF8'))) || convert_to(domain_value, 'UTF8');
    canonical := canonical || int4send((SELECT count(*)::integer FROM pg_catalog.jsonb_object_keys(traits_document)));
    FOR trait IN
        SELECT key_value, value_value
        FROM jsonb_each_text(traits_document) AS values_to_hash(key_value, value_value)
        ORDER BY key_value COLLATE "C"
    LOOP
        key_bytes := convert_to(trait.key_value, 'UTF8');
        value_bytes := convert_to(trait.value_value, 'UTF8');
        canonical := canonical || int4send(octet_length(key_bytes)) || key_bytes;
        canonical := canonical || int4send(octet_length(value_bytes)) || value_bytes;
    END LOOP;
    RETURN encode(__SCHEMA__.hmac_sha256_exact(key_material, canonical), 'hex');
END;
$function$;

CREATE TABLE IF NOT EXISTS __SCHEMA__.persona_request_hmac_key_attestations
(
    key_name text PRIMARY KEY,
    key_sha256 text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT persona_request_hmac_key_name_exact CHECK (key_name = 'history-export-v1'),
    CONSTRAINT persona_request_hmac_key_hash CHECK (key_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(key_sha256) = 64)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.persona_hmac_keys
(
    soul_id text PRIMARY KEY,
    key_material bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT persona_hmac_keys_soul_format CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT persona_hmac_keys_exact_length CHECK (octet_length(key_material) = 32)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.persona_revisions
(
    soul_id text NOT NULL,
    persona_revision bigint NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    binding_revision bigint NOT NULL,
    status text NOT NULL,
    traits_sha256 text NOT NULL,
    trait_keys text[] NOT NULL,
    evidence_sha256 text[] NOT NULL,
    trace_id text NOT NULL,
    idempotency_key text NOT NULL,
    idempotency_key_sha256 text NOT NULL,
    occurred_at timestamptz NOT NULL,
    request_sha256 text NOT NULL,
    result_sha256 text NOT NULL,
    result_json jsonb NOT NULL,
    binding_fence_id text NOT NULL,
    binding_fence_sequence bigint NOT NULL,
    binding_fence_receipt_sha256 text NOT NULL,
    binding_fence_receipt_json jsonb NOT NULL,
    binding_composition_attestation_sha256 text NOT NULL,
    binding_release_bom_sha256 text NOT NULL,
    binding_composition_generation bigint NOT NULL,
    binding_instance_trust_epoch bigint NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (soul_id, persona_revision),
    CONSTRAINT persona_revisions_payload_scope_unique
        UNIQUE (soul_id, persona_revision, device_binding_id, platform_account_id, traits_sha256),
    CONSTRAINT persona_revisions_soul_format CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT persona_revisions_revision_positive CHECK (persona_revision >= 1),
    CONSTRAINT persona_revisions_binding_format CHECK (device_binding_id ~ '^db_[a-f0-9]{32}$' AND octet_length(device_binding_id) = 35),
    CONSTRAINT persona_revisions_account_format CHECK (platform_account_id ~ '^pa_[a-f0-9]{32}$' AND octet_length(platform_account_id) = 35),
    CONSTRAINT persona_revisions_binding_revision_positive CHECK (binding_revision >= 1),
    CONSTRAINT persona_revisions_status_known CHECK (status IN ('active', 'deleted')),
    CONSTRAINT persona_revisions_traits_hash CHECK (traits_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(traits_sha256) = 64),
    CONSTRAINT persona_revisions_trait_keys_known CHECK (trait_keys <@ ARRAY['curiosity', 'humor', 'pace', 'sociality', 'tone']::text[]),
    CONSTRAINT persona_revisions_trait_keys_canonical CHECK (
        __SCHEMA__.is_strict_ordinal_array(trait_keys) AND
        ((status = 'active' AND cardinality(trait_keys) BETWEEN 1 AND 5) OR
         (status = 'deleted' AND cardinality(trait_keys) = 0))),
    CONSTRAINT persona_revisions_evidence_present CHECK (cardinality(evidence_sha256) BETWEEN 1 AND 64),
    CONSTRAINT persona_revisions_evidence_canonical CHECK (__SCHEMA__.all_sha256(evidence_sha256)),
    CONSTRAINT persona_revisions_evidence_order CHECK (__SCHEMA__.is_strict_ordinal_array(evidence_sha256)),
    CONSTRAINT persona_revisions_trace_format CHECK (trace_id ~ '^trace_[a-f0-9]{32}$' AND octet_length(trace_id) = 38),
    CONSTRAINT persona_revisions_idempotency_format CHECK (idempotency_key ~ '^idem_[a-f0-9]{64}$' AND octet_length(idempotency_key) = 69),
    CONSTRAINT persona_revisions_idempotency_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(idempotency_key_sha256) = 64),
    CONSTRAINT persona_revisions_request_hash CHECK (request_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(request_sha256) = 64),
    CONSTRAINT persona_revisions_result_hash CHECK (
        result_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(result_sha256) = 64 AND
        result_sha256 = encode(sha256(convert_to(result_json::text, 'UTF8')), 'hex')),
    CONSTRAINT persona_revisions_result_object CHECK (jsonb_typeof(result_json) = 'object'),
    CONSTRAINT persona_revisions_result_keys CHECK (__SCHEMA__.jsonb_has_exact_keys(
        result_json,
        ARRAY['contract_id','device_binding_id','evidence_sha256','idempotency_key','occurred_at','persona_revision','platform_account_id','privacy_class','producer_module','schema_version','soul_id','status','trace_id','trait_keys','traits_sha256']::text[])),
    CONSTRAINT persona_revisions_result_envelope CHECK (
        result_json ->> 'schema_version' = '1.0.0' AND
        result_json ->> 'contract_id' = 'persona.revision/v1' AND
        result_json ->> 'producer_module' = 'persona-store' AND
        result_json ->> 'privacy_class' = 'personal'),
    CONSTRAINT persona_revisions_result_scope CHECK (
        result_json ->> 'soul_id' = soul_id AND
        result_json ->> 'device_binding_id' = device_binding_id AND
        result_json ->> 'platform_account_id' = platform_account_id),
    CONSTRAINT persona_revisions_result_revision CHECK ((result_json ->> 'persona_revision')::bigint = persona_revision),
    CONSTRAINT persona_revisions_result_status CHECK (result_json ->> 'status' = status),
    CONSTRAINT persona_revisions_result_exact_fields CHECK (
        result_json ->> 'trace_id' = trace_id AND
        result_json ->> 'idempotency_key' = idempotency_key AND
        (result_json ->> 'occurred_at')::timestamptz = occurred_at AND
        result_json ->> 'traits_sha256' = traits_sha256 AND
        __SCHEMA__.is_persona_utc_timestamp_text(result_json ->> 'occurred_at') AND
        __SCHEMA__.jsonb_text_array_equals(result_json, 'trait_keys', trait_keys) AND
        __SCHEMA__.jsonb_text_array_equals(result_json, 'evidence_sha256', evidence_sha256)),
    CONSTRAINT persona_revisions_binding_fence_id CHECK (binding_fence_id ~ '^bfence_[a-f0-9]{64}$' AND octet_length(binding_fence_id) = 71),
    CONSTRAINT persona_revisions_binding_fence_sequence CHECK (binding_fence_sequence >= 1),
    CONSTRAINT persona_revisions_binding_fence_receipt_hash CHECK (
        binding_fence_receipt_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(binding_fence_receipt_sha256) = 64 AND
        binding_fence_receipt_sha256 = encode(sha256(convert_to(binding_fence_receipt_json::text, 'UTF8')), 'hex')),
    CONSTRAINT persona_revisions_binding_fence_receipt_keys CHECK (__SCHEMA__.jsonb_has_exact_keys(
        binding_fence_receipt_json,
        ARRAY['binding_revision','contract_id','device_binding_id','fence_id','fence_sequence','idempotency_key','occurred_at','platform_account_id','privacy_class','producer_module','schema_version','soul_id','state','trace_id']::text[])),
    CONSTRAINT persona_revisions_binding_fence_receipt_exact CHECK (
        binding_fence_receipt_json ->> 'schema_version' = '1.0.0' AND
        binding_fence_receipt_json ->> 'contract_id' = 'identity.binding.mutation.fence/v1' AND
        binding_fence_receipt_json ->> 'producer_module' = 'binding' AND
        binding_fence_receipt_json ->> 'privacy_class' = 'sensitive' AND
        binding_fence_receipt_json ->> 'state' = 'held' AND
        binding_fence_receipt_json ->> 'soul_id' = soul_id AND
        binding_fence_receipt_json ->> 'device_binding_id' = device_binding_id AND
        binding_fence_receipt_json ->> 'platform_account_id' = platform_account_id AND
        binding_fence_receipt_json ->> 'trace_id' = trace_id AND
        binding_fence_receipt_json ->> 'idempotency_key' = idempotency_key AND
        __SCHEMA__.is_persona_utc_timestamp_text(binding_fence_receipt_json ->> 'occurred_at') AND
        (binding_fence_receipt_json ->> 'occurred_at')::timestamptz = occurred_at AND
        (binding_fence_receipt_json ->> 'binding_revision')::bigint = binding_revision AND
        binding_fence_receipt_json ->> 'fence_id' = binding_fence_id AND
        (binding_fence_receipt_json ->> 'fence_sequence')::bigint = binding_fence_sequence),
    CONSTRAINT persona_revisions_composition_attestation_hash CHECK (binding_composition_attestation_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(binding_composition_attestation_sha256) = 64),
    CONSTRAINT persona_revisions_release_bom_hash CHECK (binding_release_bom_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(binding_release_bom_sha256) = 64),
    CONSTRAINT persona_revisions_composition_generation CHECK (binding_composition_generation >= 1),
    CONSTRAINT persona_revisions_binding_trust_epoch CHECK (binding_instance_trust_epoch >= 1)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.trait_payloads
(
    soul_id text NOT NULL,
    persona_revision bigint NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    traits_sha256 text NOT NULL,
    traits_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (soul_id, persona_revision),
    CONSTRAINT trait_payloads_revision_scope_fk
        FOREIGN KEY (soul_id, persona_revision, device_binding_id, platform_account_id, traits_sha256)
        REFERENCES __SCHEMA__.persona_revisions
            (soul_id, persona_revision, device_binding_id, platform_account_id, traits_sha256)
        ON DELETE RESTRICT,
    CONSTRAINT trait_payloads_traits_hash CHECK (traits_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(traits_sha256) = 64),
    CONSTRAINT trait_payloads_object CHECK (__SCHEMA__.persona_traits_are_valid(traits_json))
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.persona_current
(
    soul_id text PRIMARY KEY,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    persona_revision bigint NOT NULL,
    status text NOT NULL,
    result_sha256 text NOT NULL,
    result_json jsonb NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT persona_current_revision_fk
        FOREIGN KEY (soul_id, persona_revision)
        REFERENCES __SCHEMA__.persona_revisions(soul_id, persona_revision) ON DELETE RESTRICT,
    CONSTRAINT persona_current_status_known CHECK (status IN ('active', 'deleted')),
    CONSTRAINT persona_current_result_object CHECK (jsonb_typeof(result_json) = 'object'),
    CONSTRAINT persona_current_result_hash CHECK (
        result_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(result_sha256) = 64 AND
        result_sha256 = encode(sha256(convert_to(result_json::text, 'UTF8')), 'hex')),
    CONSTRAINT persona_current_result_scope CHECK (
        result_json ->> 'soul_id' = soul_id AND
        result_json ->> 'device_binding_id' = device_binding_id AND
        result_json ->> 'platform_account_id' = platform_account_id),
    CONSTRAINT persona_current_result_revision CHECK ((result_json ->> 'persona_revision')::bigint = persona_revision),
    CONSTRAINT persona_current_result_status CHECK (result_json ->> 'status' = status)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_receipts
(
    idempotency_key_sha256 text PRIMARY KEY,
    operation text NOT NULL,
    request_sha256 text NOT NULL,
    result_sha256 text NOT NULL,
    soul_id text NOT NULL,
    persona_revision bigint NOT NULL,
    result_json jsonb NOT NULL,
    outbox_id uuid NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT idempotency_receipts_revision_fk
        FOREIGN KEY (soul_id, persona_revision)
        REFERENCES __SCHEMA__.persona_revisions(soul_id, persona_revision) ON DELETE RESTRICT,
    CONSTRAINT idempotency_receipts_key_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(idempotency_key_sha256) = 64),
    CONSTRAINT idempotency_receipts_operation_known CHECK (operation IN ('put', 'delete')),
    CONSTRAINT idempotency_receipts_request_hash CHECK (request_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(request_sha256) = 64),
    CONSTRAINT idempotency_receipts_result_hash CHECK (
        result_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(result_sha256) = 64 AND
        result_sha256 = encode(sha256(convert_to(result_json::text, 'UTF8')), 'hex')),
    CONSTRAINT idempotency_receipts_result_object CHECK (jsonb_typeof(result_json) = 'object'),
    CONSTRAINT idempotency_receipts_result_scope CHECK (result_json ->> 'soul_id' = soul_id),
    CONSTRAINT idempotency_receipts_result_revision CHECK ((result_json ->> 'persona_revision')::bigint = persona_revision)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.persona_export_receipts
(
    idempotency_key_sha256 text PRIMARY KEY,
    soul_id text NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    export_request_hmac_sha256 text NOT NULL,
    snapshot_persona_revision bigint NOT NULL,
    snapshot_cursor_hmac_sha256 text NOT NULL,
    export_payload_sha256 text NOT NULL,
    export_receipt_hmac_sha256 text NOT NULL,
    export_receipt_id text NOT NULL UNIQUE,
    result_wire_bytes integer NOT NULL,
    result_sha256 text NOT NULL,
    result_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT persona_export_receipts_revision_fk
        FOREIGN KEY (soul_id, snapshot_persona_revision)
        REFERENCES __SCHEMA__.persona_revisions(soul_id, persona_revision) ON DELETE RESTRICT,
    CONSTRAINT persona_export_receipts_soul_format CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT persona_export_receipts_binding_format CHECK (device_binding_id ~ '^db_[a-f0-9]{32}$' AND octet_length(device_binding_id) = 35),
    CONSTRAINT persona_export_receipts_account_format CHECK (platform_account_id ~ '^pa_[a-f0-9]{32}$' AND octet_length(platform_account_id) = 35),
    CONSTRAINT persona_export_receipts_idempotency_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(idempotency_key_sha256) = 64),
    CONSTRAINT persona_export_receipts_request_hmac CHECK (export_request_hmac_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(export_request_hmac_sha256) = 64),
    CONSTRAINT persona_export_receipts_snapshot_positive CHECK (snapshot_persona_revision >= 1),
    CONSTRAINT persona_export_receipts_cursor_hmac CHECK (snapshot_cursor_hmac_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(snapshot_cursor_hmac_sha256) = 64),
    CONSTRAINT persona_export_receipts_payload_hash CHECK (export_payload_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(export_payload_sha256) = 64),
    CONSTRAINT persona_export_receipts_receipt_hmac CHECK (export_receipt_hmac_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(export_receipt_hmac_sha256) = 64),
    CONSTRAINT persona_export_receipts_receipt_id CHECK (
        export_receipt_id = 'pexport_' || export_receipt_hmac_sha256 AND octet_length(export_receipt_id) = 72),
    CONSTRAINT persona_export_receipts_wire_limit CHECK (result_wire_bytes BETWEEN 1 AND 16777216),
    CONSTRAINT persona_export_receipts_result_hash CHECK (
        result_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(result_sha256) = 64 AND
        result_sha256 = encode(sha256(convert_to(result_json::text, 'UTF8')), 'hex')),
    CONSTRAINT persona_export_receipts_result_object CHECK (jsonb_typeof(result_json) = 'object'),
    CONSTRAINT persona_export_receipts_result_keys CHECK (__SCHEMA__.jsonb_has_exact_keys(
        result_json,
        ARRAY['contract_id','device_binding_id','export_payload_sha256','export_receipt_hmac_sha256','export_receipt_id','export_request_hmac_sha256','idempotency_key','live_primary_payload_state','occurred_at','platform_account_id','privacy_class','producer_module','revisions','schema_version','snapshot_cursor_hmac_sha256','snapshot_persona_revision','soul_id','trace_id']::text[])),
    CONSTRAINT persona_export_receipts_result_envelope CHECK (
        result_json ->> 'schema_version' IS NOT DISTINCT FROM '1.0.0' AND
        result_json ->> 'contract_id' IS NOT DISTINCT FROM 'persona.history.export/v1' AND
        result_json ->> 'producer_module' IS NOT DISTINCT FROM 'persona-store' AND
        result_json ->> 'privacy_class' IS NOT DISTINCT FROM 'sensitive' AND
        result_json ->> 'soul_id' IS NOT DISTINCT FROM soul_id AND
        result_json ->> 'device_binding_id' IS NOT DISTINCT FROM device_binding_id AND
        result_json ->> 'platform_account_id' IS NOT DISTINCT FROM platform_account_id AND
        (result_json ->> 'trace_id' ~ '^trace_[a-f0-9]{32}$') IS NOT DISTINCT FROM true AND
        octet_length(result_json ->> 'trace_id') IS NOT DISTINCT FROM 38 AND
        (result_json ->> 'idempotency_key' ~ '^idem_[a-f0-9]{64}$') IS NOT DISTINCT FROM true AND
        octet_length(result_json ->> 'idempotency_key') IS NOT DISTINCT FROM 69 AND
        __SCHEMA__.is_persona_utc_timestamp_text(result_json ->> 'occurred_at') IS NOT DISTINCT FROM true AND
        (result_json ->> 'live_primary_payload_state' IN ('retained', 'live-primary-logically-deleted')) IS NOT DISTINCT FROM true AND
        encode(sha256(convert_to(result_json ->> 'idempotency_key', 'UTF8')), 'hex') IS NOT DISTINCT FROM idempotency_key_sha256),
    CONSTRAINT persona_export_receipts_result_proof CHECK (
        (result_json ->> 'snapshot_persona_revision')::bigint IS NOT DISTINCT FROM snapshot_persona_revision AND
        result_json ->> 'snapshot_cursor_hmac_sha256' IS NOT DISTINCT FROM snapshot_cursor_hmac_sha256 AND
        result_json ->> 'export_request_hmac_sha256' IS NOT DISTINCT FROM export_request_hmac_sha256 AND
        result_json ->> 'export_payload_sha256' IS NOT DISTINCT FROM export_payload_sha256 AND
        result_json ->> 'export_receipt_hmac_sha256' IS NOT DISTINCT FROM export_receipt_hmac_sha256 AND
        result_json ->> 'export_receipt_id' IS NOT DISTINCT FROM export_receipt_id),
    CONSTRAINT persona_export_receipts_result_tail CHECK (
        CASE WHEN jsonb_typeof(result_json -> 'revisions') = 'array' THEN
            jsonb_array_length(result_json -> 'revisions') BETWEEN 1 AND 10000 AND
            (result_json -> 'revisions' -> -1 -> 'revision' ->> 'persona_revision')::bigint IS NOT DISTINCT FROM snapshot_persona_revision
        ELSE false END)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.persona_export_receipt_quarantine
(
    quarantine_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    soul_id text NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    idempotency_key_sha256 text NOT NULL,
    existing_request_hmac_sha256 text NOT NULL,
    incoming_request_hmac_sha256 text NOT NULL,
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (idempotency_key_sha256, incoming_request_hmac_sha256),
    CONSTRAINT persona_export_quarantine_soul_format CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT persona_export_quarantine_binding_format CHECK (device_binding_id ~ '^db_[a-f0-9]{32}$' AND octet_length(device_binding_id) = 35),
    CONSTRAINT persona_export_quarantine_account_format CHECK (platform_account_id ~ '^pa_[a-f0-9]{32}$' AND octet_length(platform_account_id) = 35),
    CONSTRAINT persona_export_quarantine_idempotency_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(idempotency_key_sha256) = 64),
    CONSTRAINT persona_export_quarantine_existing_hmac CHECK (existing_request_hmac_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(existing_request_hmac_sha256) = 64),
    CONSTRAINT persona_export_quarantine_incoming_hmac CHECK (incoming_request_hmac_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(incoming_request_hmac_sha256) = 64),
    CONSTRAINT persona_export_quarantine_reason_exact CHECK (reason = 'same-export-idempotency-key-different-request-hmac-or-scope')
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox
(
    outbox_id uuid PRIMARY KEY,
    soul_id text NOT NULL,
    persona_revision bigint NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    trace_id text NOT NULL,
    topic text NOT NULL,
    payload_sha256 text NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (soul_id, persona_revision),
    UNIQUE (outbox_id, soul_id, persona_revision),
    CONSTRAINT outbox_revision_fk
        FOREIGN KEY (soul_id, persona_revision)
        REFERENCES __SCHEMA__.persona_revisions(soul_id, persona_revision) ON DELETE RESTRICT,
    CONSTRAINT outbox_topic_exact CHECK (topic = 'persona.revision/v1'),
    CONSTRAINT outbox_payload_hash CHECK (
        payload_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(payload_sha256) = 64 AND
        payload_sha256 = encode(sha256(convert_to(payload_json::text, 'UTF8')), 'hex')),
    CONSTRAINT outbox_payload_object CHECK (jsonb_typeof(payload_json) = 'object'),
    CONSTRAINT outbox_payload_scope CHECK (
        payload_json ->> 'soul_id' = soul_id AND
        payload_json ->> 'device_binding_id' = device_binding_id AND
        payload_json ->> 'platform_account_id' = platform_account_id),
    CONSTRAINT outbox_payload_revision CHECK ((payload_json ->> 'persona_revision')::bigint = persona_revision),
    CONSTRAINT outbox_payload_exact CHECK (
        payload_json ->> 'trace_id' = trace_id AND
        (payload_json ->> 'occurred_at')::timestamptz = occurred_at)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox_dispatch_receipts
(
    outbox_id uuid PRIMARY KEY,
    dispatcher_receipt_sha256 text NOT NULL,
    dispatched_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CONSTRAINT outbox_dispatch_receipts_outbox_fk
        FOREIGN KEY (outbox_id) REFERENCES __SCHEMA__.outbox(outbox_id) ON DELETE RESTRICT,
    CONSTRAINT outbox_dispatch_receipts_hash CHECK (dispatcher_receipt_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(dispatcher_receipt_sha256) = 64)
);

ALTER TABLE __SCHEMA__.idempotency_receipts
    DROP CONSTRAINT IF EXISTS idempotency_receipts_exact_outbox_fk;
ALTER TABLE __SCHEMA__.idempotency_receipts
    ADD CONSTRAINT idempotency_receipts_exact_outbox_fk
    FOREIGN KEY (outbox_id, soul_id, persona_revision)
    REFERENCES __SCHEMA__.outbox(outbox_id, soul_id, persona_revision)
    ON DELETE RESTRICT
    DEFERRABLE INITIALLY DEFERRED;

CREATE TABLE IF NOT EXISTS __SCHEMA__.idempotency_quarantine
(
    quarantine_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    soul_id text NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    idempotency_key_sha256 text NOT NULL,
    incoming_operation text NOT NULL,
    existing_request_sha256 text NOT NULL,
    incoming_request_sha256 text NOT NULL,
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (idempotency_key_sha256, incoming_request_sha256),
    CONSTRAINT idempotency_quarantine_soul_format CHECK (soul_id ~ '^soul_[a-f0-9]{64}$' AND octet_length(soul_id) = 69),
    CONSTRAINT idempotency_quarantine_binding_format CHECK (device_binding_id ~ '^db_[a-f0-9]{32}$' AND octet_length(device_binding_id) = 35),
    CONSTRAINT idempotency_quarantine_account_format CHECK (platform_account_id ~ '^pa_[a-f0-9]{32}$' AND octet_length(platform_account_id) = 35),
    CONSTRAINT idempotency_quarantine_key_hash CHECK (idempotency_key_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(idempotency_key_sha256) = 64),
    CONSTRAINT idempotency_quarantine_operation_known CHECK (incoming_operation IN ('put', 'delete')),
    CONSTRAINT idempotency_quarantine_existing_hash CHECK (existing_request_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(existing_request_sha256) = 64),
    CONSTRAINT idempotency_quarantine_incoming_hash CHECK (incoming_request_sha256 ~ '^[a-f0-9]{64}$' AND octet_length(incoming_request_sha256) = 64)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.erasure_audit
(
    soul_id text NOT NULL,
    persona_revision bigint NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    evidence_sha256 text[] NOT NULL,
    erased_payload_count bigint NOT NULL,
    hmac_key_erased boolean NOT NULL,
    policy_action text NOT NULL,
    deletion_scope text NOT NULL,
    external_destruction_receipt_sha256 text NULL,
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (soul_id, persona_revision),
    CONSTRAINT erasure_audit_revision_fk
        FOREIGN KEY (soul_id, persona_revision)
        REFERENCES __SCHEMA__.persona_revisions(soul_id, persona_revision) ON DELETE RESTRICT,
    CONSTRAINT erasure_audit_evidence_present CHECK (cardinality(evidence_sha256) BETWEEN 1 AND 64),
    CONSTRAINT erasure_audit_evidence_canonical CHECK (__SCHEMA__.all_sha256(evidence_sha256)),
    CONSTRAINT erasure_audit_evidence_order CHECK (__SCHEMA__.is_strict_ordinal_array(evidence_sha256)),
    CONSTRAINT erasure_audit_count_nonnegative CHECK (erased_payload_count >= 0),
    CONSTRAINT erasure_audit_key_erased CHECK (hmac_key_erased),
    CONSTRAINT erasure_audit_policy_exact CHECK (policy_action = 'audited-live-store-logical-deletion'),
    CONSTRAINT erasure_audit_scope_exact CHECK (deletion_scope = 'live-postgresql-primary-only'),
    CONSTRAINT erasure_audit_external_receipt_absent CHECK (external_destruction_receipt_sha256 IS NULL)
);

CREATE INDEX IF NOT EXISTS persona_revisions_exact_scope_idx
    ON __SCHEMA__.persona_revisions(soul_id, device_binding_id, platform_account_id, persona_revision);

CREATE INDEX IF NOT EXISTS outbox_exact_pending_scope_idx
    ON __SCHEMA__.outbox(soul_id, device_binding_id, platform_account_id, created_at, outbox_id);

CREATE OR REPLACE FUNCTION __SCHEMA__.read_persona_hmac_key_v1(
    target_soul_id text,
    target_device_binding_id text,
    target_platform_account_id text,
    target_persona_revision bigint)
RETURNS bytea
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    resolved_key bytea;
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.persona_current
        WHERE soul_id = target_soul_id
          AND device_binding_id = target_device_binding_id
          AND platform_account_id = target_platform_account_id
          AND persona_revision = target_persona_revision
          AND status = 'active'
    ) THEN
        RAISE EXCEPTION 'Persona Soul HMAC key requires exact active current scope and revision';
    END IF;
    SELECT key_material INTO STRICT resolved_key
      FROM __SCHEMA__.persona_hmac_keys
     WHERE soul_id = target_soul_id;
    RETURN resolved_key;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.erase_persona_material(target_soul_id text, tombstone_revision bigint)
RETURNS bigint
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    erased_count bigint;
    actual_erased_count bigint;
    erased_key_count bigint;
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.persona_revisions
        JOIN __SCHEMA__.persona_current
          USING (soul_id, persona_revision)
        WHERE persona_revisions.soul_id = target_soul_id
          AND persona_revisions.persona_revision = tombstone_revision
          AND persona_revisions.status = 'deleted'
          AND persona_current.status = 'deleted'
    ) THEN
        RAISE EXCEPTION 'live-primary logical deletion requires the exact current tombstone revision';
    END IF;

    SELECT count(*) INTO erased_count
    FROM __SCHEMA__.trait_payloads
    WHERE soul_id = target_soul_id;
    IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.persona_hmac_keys WHERE soul_id = target_soul_id) THEN
        RAISE EXCEPTION 'live-primary logical deletion requires an existing per-Soul HMAC key';
    END IF;

    INSERT INTO __SCHEMA__.erasure_audit
        (soul_id, persona_revision, device_binding_id, platform_account_id, evidence_sha256,
         erased_payload_count, hmac_key_erased, policy_action, deletion_scope,
         external_destruction_receipt_sha256, occurred_at)
    SELECT soul_id, persona_revision, device_binding_id, platform_account_id, evidence_sha256,
           erased_count, true, 'audited-live-store-logical-deletion', 'live-postgresql-primary-only',
           NULL, occurred_at
    FROM __SCHEMA__.persona_revisions
    WHERE soul_id = target_soul_id
      AND persona_revision = tombstone_revision;

    DELETE FROM __SCHEMA__.trait_payloads WHERE soul_id = target_soul_id;
    GET DIAGNOSTICS actual_erased_count = ROW_COUNT;
    IF actual_erased_count <> erased_count THEN
        RAISE EXCEPTION 'live-primary payload deletion count changed inside the tombstone transaction';
    END IF;

    DELETE FROM __SCHEMA__.persona_hmac_keys WHERE soul_id = target_soul_id;
    GET DIAGNOSTICS erased_key_count = ROW_COUNT;
    IF erased_key_count <> 1 THEN
        RAISE EXCEPTION 'live-primary Persona key-row deletion was not exact';
    END IF;

    RETURN erased_count;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.resolve_persona_receipt_v1(
    target_soul_id text,
    target_device_binding_id text,
    target_platform_account_id text,
    target_idempotency_key_sha256 text,
    incoming_operation text,
    incoming_request_sha256 text)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    existing_request_sha256 text;
    existing_result_json jsonb;
BEGIN
    IF target_soul_id !~ '^soul_[a-f0-9]{64}$' OR octet_length(target_soul_id) <> 69 OR
       target_device_binding_id !~ '^db_[a-f0-9]{32}$' OR octet_length(target_device_binding_id) <> 35 OR
       target_platform_account_id !~ '^pa_[a-f0-9]{32}$' OR octet_length(target_platform_account_id) <> 35 OR
       target_idempotency_key_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(target_idempotency_key_sha256) <> 64 OR
       incoming_request_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(incoming_request_sha256) <> 64 OR
       incoming_operation NOT IN ('put', 'delete') THEN
        RAISE EXCEPTION 'invalid Persona receipt-resolution request';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(target_idempotency_key_sha256, 730201));
    PERFORM pg_advisory_xact_lock(hashtextextended(target_soul_id, 730202));
    SELECT request_sha256, result_json
      INTO existing_request_sha256, existing_result_json
      FROM __SCHEMA__.idempotency_receipts
     WHERE idempotency_key_sha256 = target_idempotency_key_sha256
     FOR UPDATE;
    IF NOT FOUND THEN
        RETURN jsonb_build_object('outcome', 'missing');
    END IF;
    IF existing_request_sha256 = incoming_request_sha256 THEN
        RETURN jsonb_build_object('outcome', 'replay', 'result', existing_result_json);
    END IF;

    INSERT INTO __SCHEMA__.idempotency_quarantine
        (soul_id, device_binding_id, platform_account_id, idempotency_key_sha256,
         incoming_operation, existing_request_sha256, incoming_request_sha256, reason)
    VALUES
        (target_soul_id, target_device_binding_id, target_platform_account_id,
         target_idempotency_key_sha256, incoming_operation, existing_request_sha256,
         incoming_request_sha256, 'same-idempotency-key-different-request-hmac')
    ON CONFLICT (idempotency_key_sha256, incoming_request_sha256) DO NOTHING;
    RETURN jsonb_build_object('outcome', 'conflict');
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.resolve_persona_export_receipt_v1(
    target_soul_id text,
    target_device_binding_id text,
    target_platform_account_id text,
    target_idempotency_key_sha256 text,
    incoming_request_hmac_sha256 text)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    existing_soul_id text;
    existing_device_binding_id text;
    existing_platform_account_id text;
    existing_request_hmac_sha256 text;
    existing_result_json jsonb;
BEGIN
    IF target_soul_id !~ '^soul_[a-f0-9]{64}$' OR octet_length(target_soul_id) <> 69 OR
       target_device_binding_id !~ '^db_[a-f0-9]{32}$' OR octet_length(target_device_binding_id) <> 35 OR
       target_platform_account_id !~ '^pa_[a-f0-9]{32}$' OR octet_length(target_platform_account_id) <> 35 OR
       target_idempotency_key_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(target_idempotency_key_sha256) <> 64 OR
       incoming_request_hmac_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(incoming_request_hmac_sha256) <> 64 THEN
        RAISE EXCEPTION 'invalid Persona history export receipt-resolution request';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(target_idempotency_key_sha256, 730203));
    PERFORM pg_advisory_xact_lock(hashtextextended(target_soul_id, 730202));
    SELECT soul_id, device_binding_id, platform_account_id, export_request_hmac_sha256, result_json
      INTO existing_soul_id, existing_device_binding_id, existing_platform_account_id,
           existing_request_hmac_sha256, existing_result_json
      FROM __SCHEMA__.persona_export_receipts
     WHERE idempotency_key_sha256 = target_idempotency_key_sha256
     FOR UPDATE;
    IF NOT FOUND THEN
        RETURN jsonb_build_object('outcome', 'missing');
    END IF;
    IF existing_soul_id = target_soul_id AND
       existing_device_binding_id = target_device_binding_id AND
       existing_platform_account_id = target_platform_account_id AND
       existing_request_hmac_sha256 = incoming_request_hmac_sha256 THEN
        RETURN jsonb_build_object('outcome', 'replay', 'result', existing_result_json);
    END IF;

    INSERT INTO __SCHEMA__.persona_export_receipt_quarantine
        (soul_id, device_binding_id, platform_account_id, idempotency_key_sha256,
         existing_request_hmac_sha256, incoming_request_hmac_sha256, reason)
    VALUES
        (target_soul_id, target_device_binding_id, target_platform_account_id,
         target_idempotency_key_sha256, existing_request_hmac_sha256,
         incoming_request_hmac_sha256, 'same-export-idempotency-key-different-request-hmac-or-scope')
    ON CONFLICT (idempotency_key_sha256, incoming_request_hmac_sha256) DO NOTHING;
    RETURN jsonb_build_object('outcome', 'conflict');
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.record_persona_export_receipt_v1(
    target_soul_id text,
    target_device_binding_id text,
    target_platform_account_id text,
    target_idempotency_key_sha256 text,
    target_export_request_hmac_sha256 text,
    target_snapshot_persona_revision bigint,
    target_snapshot_cursor_hmac_sha256 text,
    target_export_payload_sha256 text,
    target_export_receipt_hmac_sha256 text,
    target_export_receipt_id text,
    target_result_document jsonb,
    target_request_hmac_key bytea)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    existing_request_hmac_sha256 text;
    existing_result_json jsonb;
    result_sha256_value text;
    current_status_value text;
    current_revision_value bigint;
    canonical_value bytea;
    canonical_occurred_at_value text;
    canonical_items_json text;
    canonical_wire_items_json text;
    canonical_payload_json text;
    canonical_result_json text;
    expected_payload_sha256 text;
    expected_request_hmac_sha256 text;
    expected_cursor_hmac_sha256 text;
    expected_receipt_hmac_sha256 text;
    result_wire_bytes_value integer;
BEGIN
    IF target_soul_id !~ '^soul_[a-f0-9]{64}$' OR octet_length(target_soul_id) <> 69 OR
       target_device_binding_id !~ '^db_[a-f0-9]{32}$' OR octet_length(target_device_binding_id) <> 35 OR
       target_platform_account_id !~ '^pa_[a-f0-9]{32}$' OR octet_length(target_platform_account_id) <> 35 OR
       target_idempotency_key_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(target_idempotency_key_sha256) <> 64 OR
       target_export_request_hmac_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(target_export_request_hmac_sha256) <> 64 OR
       target_snapshot_persona_revision < 1 OR
       target_snapshot_cursor_hmac_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(target_snapshot_cursor_hmac_sha256) <> 64 OR
       target_export_payload_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(target_export_payload_sha256) <> 64 OR
       target_export_receipt_hmac_sha256 !~ '^[a-f0-9]{64}$' OR octet_length(target_export_receipt_hmac_sha256) <> 64 OR
       target_export_receipt_id <> 'pexport_' || target_export_receipt_hmac_sha256 OR
       octet_length(target_export_receipt_id) <> 72 OR
       target_request_hmac_key IS NULL OR octet_length(target_request_hmac_key) <> 32 THEN
        RAISE EXCEPTION 'invalid Persona history export receipt';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended(target_idempotency_key_sha256, 730203));
    PERFORM pg_advisory_xact_lock(hashtextextended(target_soul_id, 730202));
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.persona_request_hmac_key_attestations
        WHERE key_name = 'history-export-v1'
          AND key_sha256 = encode(sha256(target_request_hmac_key), 'hex')
    ) THEN
        RAISE EXCEPTION 'Persona history export HMAC key is not deployment-attested';
    END IF;

    IF __SCHEMA__.jsonb_has_exact_keys(
        target_result_document,
        ARRAY['contract_id','device_binding_id','export_payload_sha256','export_receipt_hmac_sha256','export_receipt_id','export_request_hmac_sha256','idempotency_key','live_primary_payload_state','occurred_at','platform_account_id','privacy_class','producer_module','revisions','schema_version','snapshot_cursor_hmac_sha256','snapshot_persona_revision','soul_id','trace_id']::text[]) IS DISTINCT FROM true OR
       target_result_document ->> 'schema_version' IS DISTINCT FROM '1.0.0' OR
       target_result_document ->> 'contract_id' IS DISTINCT FROM 'persona.history.export/v1' OR
       target_result_document ->> 'producer_module' IS DISTINCT FROM 'persona-store' OR
       target_result_document ->> 'privacy_class' IS DISTINCT FROM 'sensitive' OR
       target_result_document ->> 'soul_id' IS DISTINCT FROM target_soul_id OR
       target_result_document ->> 'device_binding_id' IS DISTINCT FROM target_device_binding_id OR
       target_result_document ->> 'platform_account_id' IS DISTINCT FROM target_platform_account_id OR
       (target_result_document ->> 'trace_id' ~ '^trace_[a-f0-9]{32}$') IS DISTINCT FROM true OR
       octet_length(target_result_document ->> 'trace_id') IS DISTINCT FROM 38 OR
       (target_result_document ->> 'idempotency_key' ~ '^idem_[a-f0-9]{64}$') IS DISTINCT FROM true OR
       octet_length(target_result_document ->> 'idempotency_key') IS DISTINCT FROM 69 OR
       __SCHEMA__.is_persona_utc_timestamp_text(target_result_document ->> 'occurred_at') IS DISTINCT FROM true OR
       encode(sha256(convert_to(target_result_document ->> 'idempotency_key', 'UTF8')), 'hex') IS DISTINCT FROM target_idempotency_key_sha256 OR
       (target_result_document ->> 'snapshot_persona_revision')::bigint IS DISTINCT FROM target_snapshot_persona_revision OR
       target_result_document ->> 'snapshot_cursor_hmac_sha256' IS DISTINCT FROM target_snapshot_cursor_hmac_sha256 OR
       target_result_document ->> 'export_request_hmac_sha256' IS DISTINCT FROM target_export_request_hmac_sha256 OR
       target_result_document ->> 'export_payload_sha256' IS DISTINCT FROM target_export_payload_sha256 OR
       target_result_document ->> 'export_receipt_hmac_sha256' IS DISTINCT FROM target_export_receipt_hmac_sha256 OR
       target_result_document ->> 'export_receipt_id' IS DISTINCT FROM target_export_receipt_id OR
       jsonb_typeof(target_result_document -> 'revisions') IS DISTINCT FROM 'array' THEN
        RAISE EXCEPTION 'Persona history export receipt does not bind one exact contract snapshot';
    END IF;

    IF jsonb_array_length(target_result_document -> 'revisions') NOT BETWEEN 1 AND 10000 OR
       jsonb_array_length(target_result_document -> 'revisions')::bigint IS DISTINCT FROM target_snapshot_persona_revision OR
       (target_result_document -> 'revisions' -> -1 -> 'revision' ->> 'persona_revision')::bigint IS DISTINCT FROM target_snapshot_persona_revision THEN
        RAISE EXCEPTION 'Persona history export revision array is not the complete contiguous snapshot';
    END IF;

    SELECT status, persona_revision
      INTO current_status_value, current_revision_value
      FROM __SCHEMA__.persona_current
     WHERE soul_id = target_soul_id
       AND device_binding_id = target_device_binding_id
       AND platform_account_id = target_platform_account_id
     FOR SHARE;
    IF NOT FOUND OR current_revision_value IS DISTINCT FROM target_snapshot_persona_revision THEN
        RAISE EXCEPTION 'Persona history export snapshot no longer matches current truth';
    END IF;

    IF target_result_document ->> 'live_primary_payload_state' IS DISTINCT FROM
       CASE current_status_value
           WHEN 'active' THEN 'retained'
           WHEN 'deleted' THEN 'live-primary-logically-deleted'
           ELSE NULL
       END THEN
        RAISE EXCEPTION 'Persona history export payload state does not match current truth';
    END IF;

    IF (SELECT count(*)
        FROM __SCHEMA__.persona_revisions
        WHERE soul_id = target_soul_id
          AND device_binding_id = target_device_binding_id
          AND platform_account_id = target_platform_account_id
          AND persona_revision BETWEEN 1 AND target_snapshot_persona_revision) IS DISTINCT FROM target_snapshot_persona_revision OR
       EXISTS (
           SELECT 1
           FROM jsonb_array_elements(target_result_document -> 'revisions') WITH ORDINALITY AS item(document, revision_number)
           LEFT JOIN __SCHEMA__.persona_revisions revision
             ON revision.soul_id = target_soul_id
            AND revision.device_binding_id = target_device_binding_id
            AND revision.platform_account_id = target_platform_account_id
            AND revision.persona_revision = item.revision_number
           LEFT JOIN __SCHEMA__.trait_payloads payload
             ON payload.soul_id = revision.soul_id
            AND payload.persona_revision = revision.persona_revision
            AND payload.device_binding_id = revision.device_binding_id
            AND payload.platform_account_id = revision.platform_account_id
            AND payload.traits_sha256 = revision.traits_sha256
           WHERE revision.soul_id IS NULL
              OR __SCHEMA__.jsonb_has_exact_keys(
                     item.document,
                     ARRAY['live_primary_payload_state','revision','traits']::text[]) IS DISTINCT FROM true
              OR item.document ->> 'live_primary_payload_state' IS DISTINCT FROM target_result_document ->> 'live_primary_payload_state'
              OR item.document -> 'revision' IS DISTINCT FROM revision.result_json
              OR CASE current_status_value
                     WHEN 'active' THEN
                         payload.soul_id IS NULL OR item.document -> 'traits' IS DISTINCT FROM payload.traits_json
                     WHEN 'deleted' THEN
                         payload.soul_id IS NOT NULL OR item.document -> 'traits' IS DISTINCT FROM 'null'::jsonb
                     ELSE true
                 END
       ) THEN
        RAISE EXCEPTION 'Persona history export nested history differs from authoritative relational truth';
    END IF;

    SELECT string_agg(__SCHEMA__.persona_history_item_canonical_json_v1(item.document, true), ',' ORDER BY item.revision_number)
      INTO canonical_items_json
      FROM jsonb_array_elements(target_result_document -> 'revisions') WITH ORDINALITY AS item(document, revision_number);
    canonical_occurred_at_value := __SCHEMA__.dotnet_roundtrip_utc_text_v1(target_result_document ->> 'occurred_at');
    canonical_payload_json := '{"domain":"dps.persona-store.history-export-payload-sha256/v1",' ||
        '"schema_version":' || to_jsonb(target_result_document ->> 'schema_version')::text || ',' ||
        '"contract_id":' || to_jsonb(target_result_document ->> 'contract_id')::text || ',' ||
        '"producer_module":' || to_jsonb(target_result_document ->> 'producer_module')::text || ',' ||
        '"soul_id":' || to_jsonb(target_soul_id)::text || ',' ||
        '"device_binding_id":' || to_jsonb(target_device_binding_id)::text || ',' ||
        '"platform_account_id":' || to_jsonb(target_platform_account_id)::text || ',' ||
        '"trace_id":' || to_jsonb(target_result_document ->> 'trace_id')::text || ',' ||
        '"idempotency_key":' || to_jsonb(target_result_document ->> 'idempotency_key')::text || ',' ||
        '"occurred_at":' || to_jsonb(canonical_occurred_at_value)::text || ',' ||
        '"privacy_class":' || to_jsonb(target_result_document ->> 'privacy_class')::text || ',' ||
        '"live_primary_payload_state":' || to_jsonb(target_result_document ->> 'live_primary_payload_state')::text || ',' ||
        '"snapshot_persona_revision":' || target_snapshot_persona_revision::text || ',' ||
        '"revisions":[' || canonical_items_json || ']}';
    expected_payload_sha256 := encode(sha256(convert_to(canonical_payload_json, 'UTF8')), 'hex');
    IF target_export_payload_sha256 IS DISTINCT FROM expected_payload_sha256 THEN
        RAISE EXCEPTION 'Persona history export payload digest differs from the authoritative canonical snapshot';
    END IF;

    canonical_value := __SCHEMA__.canonical_text_v1('dps.persona-store.history-export-request-hmac-sha256/v1') ||
                       __SCHEMA__.canonical_text_v1(target_soul_id) ||
                       __SCHEMA__.canonical_text_v1(target_device_binding_id) ||
                       __SCHEMA__.canonical_text_v1(target_platform_account_id) ||
                       __SCHEMA__.canonical_text_v1(target_result_document ->> 'trace_id') ||
                       __SCHEMA__.canonical_text_v1(target_result_document ->> 'idempotency_key') ||
                       __SCHEMA__.canonical_text_v1(canonical_occurred_at_value);
    expected_request_hmac_sha256 := encode(__SCHEMA__.hmac_sha256_exact(target_request_hmac_key, canonical_value), 'hex');

    canonical_value := __SCHEMA__.canonical_text_v1('dps.persona-store.history-export-cursor-hmac-sha256/v1') ||
                       __SCHEMA__.canonical_text_v1(target_soul_id) ||
                       __SCHEMA__.canonical_text_v1(target_device_binding_id) ||
                       __SCHEMA__.canonical_text_v1(target_platform_account_id) ||
                       int8send(target_snapshot_persona_revision);
    expected_cursor_hmac_sha256 := encode(__SCHEMA__.hmac_sha256_exact(target_request_hmac_key, canonical_value), 'hex');

    canonical_value := __SCHEMA__.canonical_text_v1('dps.persona-store.history-export-receipt-hmac-sha256/v1') ||
                       __SCHEMA__.canonical_text_v1(expected_request_hmac_sha256) ||
                       int8send(target_snapshot_persona_revision) ||
                       __SCHEMA__.canonical_text_v1(expected_cursor_hmac_sha256) ||
                       __SCHEMA__.canonical_text_v1(expected_payload_sha256);
    expected_receipt_hmac_sha256 := encode(__SCHEMA__.hmac_sha256_exact(target_request_hmac_key, canonical_value), 'hex');
    IF target_export_request_hmac_sha256 IS DISTINCT FROM expected_request_hmac_sha256 OR
       target_snapshot_cursor_hmac_sha256 IS DISTINCT FROM expected_cursor_hmac_sha256 OR
       target_export_receipt_hmac_sha256 IS DISTINCT FROM expected_receipt_hmac_sha256 OR
       target_export_receipt_id IS DISTINCT FROM 'pexport_' || expected_receipt_hmac_sha256 THEN
        RAISE EXCEPTION 'Persona history export cryptographic proof does not match the deployment-attested key';
    END IF;

    SELECT string_agg(__SCHEMA__.persona_history_item_canonical_json_v1(item.document, false), ',' ORDER BY item.revision_number)
      INTO canonical_wire_items_json
      FROM jsonb_array_elements(target_result_document -> 'revisions') WITH ORDINALITY AS item(document, revision_number);
    canonical_result_json := '{"schema_version":' || to_jsonb(target_result_document ->> 'schema_version')::text || ',' ||
        '"contract_id":' || to_jsonb(target_result_document ->> 'contract_id')::text || ',' ||
        '"producer_module":' || to_jsonb(target_result_document ->> 'producer_module')::text || ',' ||
        '"soul_id":' || to_jsonb(target_soul_id)::text || ',' ||
        '"device_binding_id":' || to_jsonb(target_device_binding_id)::text || ',' ||
        '"platform_account_id":' || to_jsonb(target_platform_account_id)::text || ',' ||
        '"trace_id":' || to_jsonb(target_result_document ->> 'trace_id')::text || ',' ||
        '"idempotency_key":' || to_jsonb(target_result_document ->> 'idempotency_key')::text || ',' ||
        '"occurred_at":' || to_jsonb(__SCHEMA__.system_text_json_utc_text_v1(target_result_document ->> 'occurred_at'))::text || ',' ||
        '"privacy_class":' || to_jsonb(target_result_document ->> 'privacy_class')::text || ',' ||
        '"live_primary_payload_state":' || to_jsonb(target_result_document ->> 'live_primary_payload_state')::text || ',' ||
        '"snapshot_persona_revision":' || target_snapshot_persona_revision::text || ',' ||
        '"snapshot_cursor_hmac_sha256":' || to_jsonb(target_snapshot_cursor_hmac_sha256)::text || ',' ||
        '"export_request_hmac_sha256":' || to_jsonb(target_export_request_hmac_sha256)::text || ',' ||
        '"export_payload_sha256":' || to_jsonb(expected_payload_sha256)::text || ',' ||
        '"export_receipt_hmac_sha256":' || to_jsonb(target_export_receipt_hmac_sha256)::text || ',' ||
        '"export_receipt_id":' || to_jsonb(target_export_receipt_id)::text || ',' ||
        '"revisions":[' || canonical_wire_items_json || ']}';
    result_wire_bytes_value := octet_length(convert_to(canonical_result_json, 'UTF8'));
    IF result_wire_bytes_value > 16777216 THEN
        RAISE EXCEPTION 'Persona history export exceeds the v1 16-MiB complete wire ceiling';
    END IF;

    SELECT export_request_hmac_sha256, result_json
      INTO existing_request_hmac_sha256, existing_result_json
      FROM __SCHEMA__.persona_export_receipts
     WHERE idempotency_key_sha256 = target_idempotency_key_sha256
     FOR UPDATE;
    IF FOUND THEN
        IF existing_request_hmac_sha256 = target_export_request_hmac_sha256 AND
           existing_result_json = target_result_document THEN
            RETURN jsonb_build_object('outcome', 'replay', 'result', existing_result_json);
        END IF;
        RAISE EXCEPTION 'Persona history export receipt changed after idempotency binding';
    END IF;

    result_sha256_value := encode(sha256(convert_to(target_result_document::text, 'UTF8')), 'hex');
    INSERT INTO __SCHEMA__.persona_export_receipts
        (idempotency_key_sha256, soul_id, device_binding_id, platform_account_id,
         export_request_hmac_sha256, snapshot_persona_revision,
         snapshot_cursor_hmac_sha256, export_payload_sha256,
         export_receipt_hmac_sha256, export_receipt_id, result_wire_bytes, result_sha256, result_json)
    VALUES
        (target_idempotency_key_sha256, target_soul_id, target_device_binding_id,
         target_platform_account_id, target_export_request_hmac_sha256,
         target_snapshot_persona_revision, target_snapshot_cursor_hmac_sha256,
         target_export_payload_sha256, target_export_receipt_hmac_sha256,
         target_export_receipt_id, result_wire_bytes_value, result_sha256_value, target_result_document);
    RETURN jsonb_build_object('outcome', 'committed', 'result', target_result_document);
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.mutate_persona_v1(
    operation_value text,
    target_soul_id text,
    target_device_binding_id text,
    target_platform_account_id text,
    expected_persona_revision bigint,
    traits_document jsonb,
    evidence_values text[],
    trace_value text,
    idempotency_key_value text,
    idempotency_key_sha256_value text,
    request_sha256_value text,
    occurred_at_value timestamptz,
    outbox_id_value uuid,
    fence_receipt_document jsonb,
    composition_attestation_sha256_value text,
    release_bom_sha256_value text,
    composition_generation_value bigint,
    binding_instance_trust_epoch_value bigint)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    existing_request_sha256 text;
    existing_result_json jsonb;
    current_revision bigint;
    current_device_binding_id text;
    current_platform_account_id text;
    current_status text;
    next_revision bigint;
    result_status text;
    trait_keys_value text[];
    trait_commitment_sha256 text;
    soul_hmac_key bytea;
    result_document jsonb;
    result_sha256_value text;
    fence_receipt_sha256_value text;
BEGIN
    IF operation_value NOT IN ('put', 'delete') OR expected_persona_revision < 0 OR
       target_soul_id !~ '^soul_[a-f0-9]{64}$' OR octet_length(target_soul_id) <> 69 OR
       target_device_binding_id !~ '^db_[a-f0-9]{32}$' OR octet_length(target_device_binding_id) <> 35 OR
       target_platform_account_id !~ '^pa_[a-f0-9]{32}$' OR octet_length(target_platform_account_id) <> 35 OR
       trace_value !~ '^trace_[a-f0-9]{32}$' OR octet_length(trace_value) <> 38 OR
       idempotency_key_value !~ '^idem_[a-f0-9]{64}$' OR octet_length(idempotency_key_value) <> 69 OR
       idempotency_key_sha256_value !~ '^[a-f0-9]{64}$' OR octet_length(idempotency_key_sha256_value) <> 64 OR
       request_sha256_value !~ '^[a-f0-9]{64}$' OR octet_length(request_sha256_value) <> 64 OR
       composition_attestation_sha256_value !~ '^[a-f0-9]{64}$' OR octet_length(composition_attestation_sha256_value) <> 64 OR
       release_bom_sha256_value !~ '^[a-f0-9]{64}$' OR octet_length(release_bom_sha256_value) <> 64 OR
       composition_generation_value < 1 OR binding_instance_trust_epoch_value < 1 OR
       extract(year FROM occurred_at_value) NOT BETWEEN 2020 AND 2199 OR
       cardinality(evidence_values) NOT BETWEEN 1 AND 64 OR
       NOT __SCHEMA__.all_sha256(evidence_values) OR
       NOT __SCHEMA__.is_strict_ordinal_array(evidence_values) THEN
        RAISE EXCEPTION 'invalid Persona atomic mutation input';
    END IF;
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.binding_composition_state composition
        WHERE composition.composition_generation = composition_generation_value
          AND composition.composition_generation = (SELECT max(latest.composition_generation) FROM __SCHEMA__.binding_composition_state latest)
          AND composition.attestation_sha256 = composition_attestation_sha256_value
          AND composition.release_bom_sha256 = release_bom_sha256_value
          AND composition.binding_instance_trust_epoch = binding_instance_trust_epoch_value
    ) THEN
        RAISE EXCEPTION 'Persona mutation is not bound to the latest migrator-recorded Binding composition';
    END IF;
    IF NOT __SCHEMA__.jsonb_has_exact_keys(
        fence_receipt_document,
        ARRAY['binding_revision','contract_id','device_binding_id','fence_id','fence_sequence','idempotency_key','occurred_at','platform_account_id','privacy_class','producer_module','schema_version','soul_id','state','trace_id']::text[]) OR
       fence_receipt_document ->> 'schema_version' <> '1.0.0' OR
       fence_receipt_document ->> 'contract_id' <> 'identity.binding.mutation.fence/v1' OR
       fence_receipt_document ->> 'producer_module' <> 'binding' OR
       fence_receipt_document ->> 'privacy_class' <> 'sensitive' OR
       fence_receipt_document ->> 'state' <> 'held' OR
       fence_receipt_document ->> 'soul_id' <> target_soul_id OR
       fence_receipt_document ->> 'device_binding_id' <> target_device_binding_id OR
       fence_receipt_document ->> 'platform_account_id' <> target_platform_account_id OR
       fence_receipt_document ->> 'trace_id' <> trace_value OR
       fence_receipt_document ->> 'idempotency_key' <> idempotency_key_value OR
       NOT __SCHEMA__.is_persona_utc_timestamp_text(fence_receipt_document ->> 'occurred_at') OR
       (fence_receipt_document ->> 'occurred_at')::timestamptz <> occurred_at_value OR
       (fence_receipt_document ->> 'binding_revision')::bigint < 1 OR
       fence_receipt_document ->> 'fence_id' !~ '^bfence_[a-f0-9]{64}$' OR
       octet_length(fence_receipt_document ->> 'fence_id') <> 71 OR
       (fence_receipt_document ->> 'fence_sequence')::bigint < 1 THEN
        RAISE EXCEPTION 'Persona mutation requires one exact held Binding fence receipt';
    END IF;
    fence_receipt_sha256_value := encode(sha256(convert_to(fence_receipt_document::text, 'UTF8')), 'hex');

    PERFORM pg_advisory_xact_lock(hashtextextended(idempotency_key_sha256_value, 730201));
    PERFORM pg_advisory_xact_lock(hashtextextended(target_soul_id, 730202));
    SELECT request_sha256, result_json
      INTO existing_request_sha256, existing_result_json
      FROM __SCHEMA__.idempotency_receipts
     WHERE idempotency_key_sha256 = idempotency_key_sha256_value
     FOR UPDATE;
    IF FOUND THEN
        IF existing_request_sha256 = request_sha256_value THEN
            RETURN jsonb_build_object('outcome', 'replay', 'result', existing_result_json);
        END IF;
        INSERT INTO __SCHEMA__.idempotency_quarantine
            (soul_id, device_binding_id, platform_account_id, idempotency_key_sha256,
             incoming_operation, existing_request_sha256, incoming_request_sha256, reason)
        VALUES
            (target_soul_id, target_device_binding_id, target_platform_account_id,
             idempotency_key_sha256_value, operation_value, existing_request_sha256,
             request_sha256_value, 'same-idempotency-key-different-request-hmac')
        ON CONFLICT (idempotency_key_sha256, incoming_request_sha256) DO NOTHING;
        RETURN jsonb_build_object('outcome', 'conflict');
    END IF;

    SELECT persona_revision, device_binding_id, platform_account_id, status
      INTO current_revision, current_device_binding_id, current_platform_account_id, current_status
      FROM __SCHEMA__.persona_current
     WHERE soul_id = target_soul_id
     FOR UPDATE;
    IF NOT FOUND THEN
        current_revision := 0;
        current_device_binding_id := NULL;
        current_platform_account_id := NULL;
        current_status := NULL;
    END IF;
    IF current_revision <> expected_persona_revision THEN
        RETURN jsonb_build_object('outcome', 'revision_conflict', 'actual_revision', current_revision);
    END IF;
    IF current_revision > 0 AND
       (current_device_binding_id <> target_device_binding_id OR current_platform_account_id <> target_platform_account_id) THEN
        RAISE EXCEPTION 'Persona mutation scope does not match current truth';
    END IF;
    IF operation_value = 'delete' AND current_revision = 0 THEN
        RETURN jsonb_build_object('outcome', 'unknown_persona');
    END IF;
    IF current_status = 'deleted' THEN
        RETURN jsonb_build_object('outcome', 'already_deleted');
    END IF;

    next_revision := current_revision + 1;
    IF operation_value = 'put' THEN
        IF traits_document IS NULL OR NOT __SCHEMA__.persona_traits_are_valid(traits_document) THEN
            RAISE EXCEPTION 'Persona put requires the closed v1 trait vocabulary';
        END IF;
        INSERT INTO __SCHEMA__.persona_hmac_keys(soul_id, key_material)
        VALUES (
            target_soul_id,
            uuid_send(gen_random_uuid()) || uuid_send(gen_random_uuid()))
        ON CONFLICT (soul_id) DO NOTHING;
        SELECT key_material INTO STRICT soul_hmac_key
          FROM __SCHEMA__.persona_hmac_keys
         WHERE soul_id = target_soul_id
         FOR SHARE;
        trait_commitment_sha256 := __SCHEMA__.persona_traits_commitment_v1(soul_hmac_key, traits_document);
        SELECT array_agg(key_value ORDER BY key_value COLLATE "C")
          INTO trait_keys_value
          FROM jsonb_object_keys(traits_document) AS keys(key_value);
        result_status := 'active';
    ELSE
        IF traits_document IS NOT NULL THEN
            RAISE EXCEPTION 'Persona delete cannot carry trait payload';
        END IF;
        trait_commitment_sha256 := 'cf337ecd78b410737dc34bfdb15770a3d8e57a6c9b04b7fc693d54a34ecff583';
        trait_keys_value := ARRAY[]::text[];
        result_status := 'deleted';
    END IF;

    result_document := jsonb_build_object(
        'schema_version', '1.0.0',
        'contract_id', 'persona.revision/v1',
        'producer_module', 'persona-store',
        'soul_id', target_soul_id,
        'device_binding_id', target_device_binding_id,
        'platform_account_id', target_platform_account_id,
        'trace_id', trace_value,
        'idempotency_key', idempotency_key_value,
        'occurred_at', occurred_at_value,
        'privacy_class', 'personal',
        'persona_revision', next_revision,
        'traits_sha256', trait_commitment_sha256,
        'trait_keys', to_jsonb(trait_keys_value),
        'evidence_sha256', to_jsonb(evidence_values),
        'status', result_status);
    result_sha256_value := encode(sha256(convert_to(result_document::text, 'UTF8')), 'hex');

    INSERT INTO __SCHEMA__.persona_revisions
        (soul_id, persona_revision, device_binding_id, platform_account_id, binding_revision,
         status, traits_sha256, trait_keys, evidence_sha256, trace_id, idempotency_key,
         idempotency_key_sha256, occurred_at, request_sha256, result_sha256, result_json,
         binding_fence_id, binding_fence_sequence, binding_fence_receipt_sha256,
         binding_fence_receipt_json, binding_composition_attestation_sha256,
         binding_release_bom_sha256, binding_composition_generation, binding_instance_trust_epoch)
    VALUES
        (target_soul_id, next_revision, target_device_binding_id, target_platform_account_id,
         (fence_receipt_document ->> 'binding_revision')::bigint,
         result_status, trait_commitment_sha256, trait_keys_value, evidence_values, trace_value,
         idempotency_key_value, idempotency_key_sha256_value, occurred_at_value,
         request_sha256_value, result_sha256_value, result_document,
         fence_receipt_document ->> 'fence_id',
         (fence_receipt_document ->> 'fence_sequence')::bigint,
         fence_receipt_sha256_value, fence_receipt_document,
         composition_attestation_sha256_value, release_bom_sha256_value,
         composition_generation_value, binding_instance_trust_epoch_value);

    IF operation_value = 'put' THEN
        INSERT INTO __SCHEMA__.trait_payloads
            (soul_id, persona_revision, device_binding_id, platform_account_id, traits_sha256, traits_json)
        VALUES
            (target_soul_id, next_revision, target_device_binding_id, target_platform_account_id,
             trait_commitment_sha256, traits_document);
    END IF;

    INSERT INTO __SCHEMA__.persona_current
        (soul_id, device_binding_id, platform_account_id, persona_revision, status,
         result_sha256, result_json, updated_at)
    VALUES
        (target_soul_id, target_device_binding_id, target_platform_account_id, next_revision,
         result_status, result_sha256_value, result_document, clock_timestamp())
    ON CONFLICT (soul_id) DO UPDATE SET
        device_binding_id = EXCLUDED.device_binding_id,
        platform_account_id = EXCLUDED.platform_account_id,
        persona_revision = EXCLUDED.persona_revision,
        status = EXCLUDED.status,
        result_sha256 = EXCLUDED.result_sha256,
        result_json = EXCLUDED.result_json,
        updated_at = EXCLUDED.updated_at;

    IF operation_value = 'delete' THEN
        PERFORM __SCHEMA__.erase_persona_material(target_soul_id, next_revision);
    END IF;

    INSERT INTO __SCHEMA__.idempotency_receipts
        (idempotency_key_sha256, operation, request_sha256, result_sha256,
         soul_id, persona_revision, result_json, outbox_id)
    VALUES
        (idempotency_key_sha256_value, operation_value, request_sha256_value,
         result_sha256_value, target_soul_id, next_revision, result_document, outbox_id_value);
    INSERT INTO __SCHEMA__.outbox
        (outbox_id, soul_id, persona_revision, device_binding_id, platform_account_id,
         trace_id, topic, payload_sha256, payload_json, occurred_at)
    VALUES
        (outbox_id_value, target_soul_id, next_revision, target_device_binding_id,
         target_platform_account_id, trace_value, 'persona.revision/v1',
         result_sha256_value, result_document, occurred_at_value);
    RETURN jsonb_build_object('outcome', 'committed', 'result', result_document);
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.record_persona_outbox_dispatch_v1(
    target_outbox_id uuid,
    target_dispatcher_receipt_sha256 text,
    target_dispatched_at timestamptz)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    existing_receipt_sha256 text;
    existing_dispatched_at timestamptz;
BEGIN
    IF target_dispatcher_receipt_sha256 !~ '^[a-f0-9]{64}$' OR
       octet_length(target_dispatcher_receipt_sha256) <> 64 OR
       extract(year FROM target_dispatched_at) NOT BETWEEN 2020 AND 2199 THEN
        RAISE EXCEPTION 'invalid Persona outbox dispatch receipt';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM __SCHEMA__.outbox WHERE outbox_id = target_outbox_id) THEN
        RAISE EXCEPTION 'unknown Persona outbox row';
    END IF;
    INSERT INTO __SCHEMA__.outbox_dispatch_receipts(outbox_id, dispatcher_receipt_sha256, dispatched_at)
    VALUES (target_outbox_id, target_dispatcher_receipt_sha256, target_dispatched_at)
    ON CONFLICT (outbox_id) DO NOTHING;
    SELECT dispatcher_receipt_sha256, dispatched_at
      INTO STRICT existing_receipt_sha256, existing_dispatched_at
      FROM __SCHEMA__.outbox_dispatch_receipts
     WHERE outbox_id = target_outbox_id;
    IF existing_receipt_sha256 <> target_dispatcher_receipt_sha256 OR
       existing_dispatched_at <> target_dispatched_at THEN
        RAISE EXCEPTION 'Persona outbox dispatch idempotency conflict';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_append_only_row_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'persona-store append-only rows cannot be updated or deleted';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.verify_revision_bundle()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.idempotency_receipts receipt
        JOIN __SCHEMA__.outbox event
          ON event.outbox_id = receipt.outbox_id
         AND event.soul_id = receipt.soul_id
         AND event.persona_revision = receipt.persona_revision
        JOIN __SCHEMA__.persona_current current_value
          ON current_value.soul_id = receipt.soul_id
         AND current_value.persona_revision = receipt.persona_revision
        WHERE receipt.soul_id = NEW.soul_id
          AND receipt.persona_revision = NEW.persona_revision
          AND receipt.idempotency_key_sha256 = NEW.idempotency_key_sha256
          AND receipt.request_sha256 = NEW.request_sha256
          AND receipt.operation = CASE WHEN NEW.status = 'active' THEN 'put' ELSE 'delete' END
          AND receipt.result_sha256 = NEW.result_sha256
          AND receipt.result_json = NEW.result_json
          AND event.device_binding_id = NEW.device_binding_id
          AND event.platform_account_id = NEW.platform_account_id
          AND event.payload_sha256 = NEW.result_sha256
          AND event.payload_json = NEW.result_json
          AND event.trace_id = NEW.trace_id
          AND event.occurred_at = NEW.occurred_at
          AND current_value.device_binding_id = NEW.device_binding_id
          AND current_value.platform_account_id = NEW.platform_account_id
          AND current_value.status = NEW.status
          AND current_value.result_sha256 = NEW.result_sha256
          AND current_value.result_json = NEW.result_json
    ) THEN
        RAISE EXCEPTION 'persona revision requires one exact atomic receipt and outbox bundle';
    END IF;

    IF NEW.status = 'active' AND NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.trait_payloads payload
        JOIN __SCHEMA__.persona_hmac_keys key_value USING (soul_id)
        WHERE payload.soul_id = NEW.soul_id
          AND payload.persona_revision = NEW.persona_revision
          AND payload.device_binding_id = NEW.device_binding_id
          AND payload.platform_account_id = NEW.platform_account_id
          AND payload.traits_sha256 = NEW.traits_sha256
          AND __SCHEMA__.persona_traits_commitment_v1(key_value.key_material, payload.traits_json) = NEW.traits_sha256
    ) THEN
        RAISE EXCEPTION 'active persona revision requires one exact keyed trait payload';
    END IF;

    IF NEW.status = 'deleted' AND NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.erasure_audit
        WHERE soul_id = NEW.soul_id AND persona_revision = NEW.persona_revision
    ) THEN
        RAISE EXCEPTION 'deleted persona revision requires an atomic live-primary logical-deletion audit';
    END IF;

    IF NEW.status = 'deleted' AND (
        EXISTS (SELECT 1 FROM __SCHEMA__.trait_payloads WHERE soul_id = NEW.soul_id) OR
        EXISTS (SELECT 1 FROM __SCHEMA__.persona_hmac_keys WHERE soul_id = NEW.soul_id)
    ) THEN
        RAISE EXCEPTION 'deleted persona revision requires live-store payload and key deletion';
    END IF;

    RETURN NEW;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.verify_current_truth()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.persona_revisions revision
        WHERE revision.soul_id = NEW.soul_id
          AND revision.persona_revision = NEW.persona_revision
          AND revision.device_binding_id = NEW.device_binding_id
          AND revision.platform_account_id = NEW.platform_account_id
          AND revision.status = NEW.status
          AND revision.result_sha256 = NEW.result_sha256
          AND revision.result_json = NEW.result_json
          AND revision.persona_revision = (
              SELECT max(latest.persona_revision)
              FROM __SCHEMA__.persona_revisions latest
              WHERE latest.soul_id = NEW.soul_id)
    ) THEN
        RAISE EXCEPTION 'persona current truth must exactly match the latest revision';
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS persona_revision_bundle_complete ON __SCHEMA__.persona_revisions;
CREATE CONSTRAINT TRIGGER persona_revision_bundle_complete
AFTER INSERT ON __SCHEMA__.persona_revisions
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.verify_revision_bundle();

DROP TRIGGER IF EXISTS persona_current_exact_latest ON __SCHEMA__.persona_current;
CREATE CONSTRAINT TRIGGER persona_current_exact_latest
AFTER INSERT OR UPDATE ON __SCHEMA__.persona_current
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.verify_current_truth();

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_append_only_truncate()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'persona-store append-only tables cannot be truncated';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_delete_or_truncate()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'persona-store current truth cannot be deleted or truncated';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_unaudited_persona_material_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM __SCHEMA__.erasure_audit audit
        JOIN __SCHEMA__.persona_current current
          ON current.soul_id = audit.soul_id
         AND current.persona_revision = audit.persona_revision
        WHERE audit.soul_id = OLD.soul_id
          AND audit.hmac_key_erased
          AND current.status = 'deleted'
    ) THEN
        RAISE EXCEPTION 'Persona material can be deleted only behind the exact audited current tombstone';
    END IF;
    RETURN OLD;
END;
$function$;

DO $block$
DECLARE
    table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'schema_migrations',
        'schema_attestations',
        'binding_composition_state',
        'persona_request_hmac_key_attestations',
        'persona_revisions',
        'idempotency_receipts',
        'persona_export_receipts',
        'persona_export_receipt_quarantine',
        'outbox',
        'outbox_dispatch_receipts',
        'idempotency_quarantine',
        'erasure_audit'
    ]
    LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I', table_name || '_append_only_rows', table_name);
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON __SCHEMA__.%I FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_row_mutation()',
            table_name || '_append_only_rows',
            table_name);
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I', table_name || '_append_only_truncate', table_name);
        EXECUTE format(
            'CREATE TRIGGER %I BEFORE TRUNCATE ON __SCHEMA__.%I FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_truncate()',
            table_name || '_append_only_truncate',
            table_name);
    END LOOP;
END;
$block$;

DROP TRIGGER IF EXISTS trait_payloads_no_update ON __SCHEMA__.trait_payloads;
CREATE TRIGGER trait_payloads_no_update
BEFORE UPDATE ON __SCHEMA__.trait_payloads
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_row_mutation();

DROP TRIGGER IF EXISTS trait_payloads_audited_delete ON __SCHEMA__.trait_payloads;
CREATE TRIGGER trait_payloads_audited_delete
BEFORE DELETE ON __SCHEMA__.trait_payloads
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_unaudited_persona_material_delete();

DROP TRIGGER IF EXISTS trait_payloads_no_truncate ON __SCHEMA__.trait_payloads;
CREATE TRIGGER trait_payloads_no_truncate
BEFORE TRUNCATE ON __SCHEMA__.trait_payloads
FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_truncate();

DROP TRIGGER IF EXISTS persona_hmac_keys_no_update ON __SCHEMA__.persona_hmac_keys;
CREATE TRIGGER persona_hmac_keys_no_update
BEFORE UPDATE ON __SCHEMA__.persona_hmac_keys
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_append_only_row_mutation();

DROP TRIGGER IF EXISTS persona_hmac_keys_audited_delete ON __SCHEMA__.persona_hmac_keys;
CREATE TRIGGER persona_hmac_keys_audited_delete
BEFORE DELETE ON __SCHEMA__.persona_hmac_keys
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_unaudited_persona_material_delete();

DROP TRIGGER IF EXISTS persona_hmac_keys_no_truncate ON __SCHEMA__.persona_hmac_keys;
CREATE TRIGGER persona_hmac_keys_no_truncate
BEFORE TRUNCATE ON __SCHEMA__.persona_hmac_keys
FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_append_only_truncate();

DROP TRIGGER IF EXISTS persona_current_no_delete ON __SCHEMA__.persona_current;
CREATE TRIGGER persona_current_no_delete
BEFORE DELETE ON __SCHEMA__.persona_current
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_delete_or_truncate();

DROP TRIGGER IF EXISTS persona_current_no_truncate ON __SCHEMA__.persona_current;
CREATE TRIGGER persona_current_no_truncate
BEFORE TRUNCATE ON __SCHEMA__.persona_current
FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_delete_or_truncate();

REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM PUBLIC;
REVOKE CREATE ON SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;
DO $block$
BEGIN
    EXECUTE format('REVOKE CREATE ON DATABASE %I FROM __RUNTIME_ROLE__', current_database());
END;
$block$;
REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM __RUNTIME_ROLE__;

DO $block$
DECLARE
    column_value record;
BEGIN
    FOR column_value IN
        SELECT relation.relname AS table_name, attribute.attname AS column_name
        FROM pg_catalog.pg_attribute attribute
        JOIN pg_catalog.pg_class relation ON relation.oid = attribute.attrelid
        JOIN pg_catalog.pg_namespace namespace ON namespace.oid = relation.relnamespace
        WHERE namespace.oid = pg_catalog.to_regnamespace('__SCHEMA__')
          AND relation.relkind IN ('r', 'p')
          AND attribute.attnum > 0
          AND NOT attribute.attisdropped
        ORDER BY relation.relname COLLATE "C", attribute.attnum
    LOOP
        EXECUTE format('REVOKE SELECT (%I) ON TABLE __SCHEMA__.%I FROM PUBLIC', column_value.column_name, column_value.table_name);
        EXECUTE format('REVOKE INSERT (%I) ON TABLE __SCHEMA__.%I FROM PUBLIC', column_value.column_name, column_value.table_name);
        EXECUTE format('REVOKE UPDATE (%I) ON TABLE __SCHEMA__.%I FROM PUBLIC', column_value.column_name, column_value.table_name);
        EXECUTE format('REVOKE REFERENCES (%I) ON TABLE __SCHEMA__.%I FROM PUBLIC', column_value.column_name, column_value.table_name);
        EXECUTE format('REVOKE SELECT (%I) ON TABLE __SCHEMA__.%I FROM __RUNTIME_ROLE__', column_value.column_name, column_value.table_name);
        EXECUTE format('REVOKE INSERT (%I) ON TABLE __SCHEMA__.%I FROM __RUNTIME_ROLE__', column_value.column_name, column_value.table_name);
        EXECUTE format('REVOKE UPDATE (%I) ON TABLE __SCHEMA__.%I FROM __RUNTIME_ROLE__', column_value.column_name, column_value.table_name);
        EXECUTE format('REVOKE REFERENCES (%I) ON TABLE __SCHEMA__.%I FROM __RUNTIME_ROLE__', column_value.column_name, column_value.table_name);
    END LOOP;
END;
$block$;

GRANT SELECT ON __SCHEMA__.schema_migrations TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.schema_attestations TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.binding_composition_state TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.persona_revisions TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.trait_payloads TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.persona_current TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.idempotency_receipts TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.outbox TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.outbox_dispatch_receipts TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.idempotency_quarantine TO __RUNTIME_ROLE__;
GRANT SELECT ON __SCHEMA__.erasure_audit TO __RUNTIME_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.read_persona_hmac_key_v1(text, text, text, bigint) FROM PUBLIC;
REVOKE ALL ON FUNCTION __SCHEMA__.erase_persona_material(text, bigint) FROM PUBLIC;
REVOKE ALL ON FUNCTION __SCHEMA__.resolve_persona_receipt_v1(text, text, text, text, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION __SCHEMA__.resolve_persona_export_receipt_v1(text, text, text, text, text) FROM PUBLIC;
REVOKE ALL ON FUNCTION __SCHEMA__.record_persona_export_receipt_v1(text, text, text, text, text, bigint, text, text, text, text, jsonb, bytea) FROM PUBLIC;
REVOKE ALL ON FUNCTION __SCHEMA__.mutate_persona_v1(text, text, text, text, bigint, jsonb, text[], text, text, text, text, timestamptz, uuid, jsonb, text, text, bigint, bigint) FROM PUBLIC;
REVOKE ALL ON FUNCTION __SCHEMA__.record_persona_outbox_dispatch_v1(uuid, text, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION __SCHEMA__.read_persona_hmac_key_v1(text, text, text, bigint) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.resolve_persona_receipt_v1(text, text, text, text, text, text) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.resolve_persona_export_receipt_v1(text, text, text, text, text) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.record_persona_export_receipt_v1(text, text, text, text, text, bigint, text, text, text, text, jsonb, bytea) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.mutate_persona_v1(text, text, text, text, bigint, jsonb, text[], text, text, text, text, timestamptz, uuid, jsonb, text, text, bigint, bigint) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.record_persona_outbox_dispatch_v1(uuid, text, timestamptz) TO __RUNTIME_ROLE__;
