-- Additive v2 trust boundary. __SCHEMA__, __ADMIN_ROLE__, and __RUNTIME_ROLE__
-- are validated PostgreSQL identifiers before substitution. The capability hash
-- is a bound Npgsql parameter and is never interpolated into SQL text.

REVOKE ALL ON SCHEMA __SCHEMA__ FROM PUBLIC;
GRANT USAGE ON SCHEMA __SCHEMA__ TO __ADMIN_ROLE__, __RUNTIME_ROLE__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.runtime_authority_v2 (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    capability_sha256 text NOT NULL CHECK (capability_sha256 ~ '^[0-9a-f]{64}$'),
    installed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
INSERT INTO __SCHEMA__.runtime_authority_v2(singleton, capability_sha256)
VALUES (true, @runtime_capability_sha256)
ON CONFLICT (singleton) DO UPDATE
SET capability_sha256 = EXCLUDED.capability_sha256,
    installed_at = clock_timestamp();

CREATE TABLE IF NOT EXISTS __SCHEMA__.soul_heads_v2 (
    soul_id text PRIMARY KEY CHECK (soul_id ~ '^soul_[0-9a-f]{64}$'),
    last_sequence bigint NOT NULL CHECK (last_sequence >= 0),
    last_chain_sha256 text NOT NULL CHECK (last_chain_sha256 ~ '^[0-9a-f]{64}$'),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.memory_events_v2 (
    event_id uuid PRIMARY KEY,
    soul_id text NOT NULL CHECK (soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (device_binding_id ~ '^db_[0-9a-f]{32}$'),
    platform_account_id text NOT NULL CHECK (platform_account_id ~ '^pa_[0-9a-f]{32}$'),
    trace_id text NOT NULL CHECK (trace_id ~ '^trace_[0-9a-f]{32}$'),
    idempotency_key text NOT NULL CHECK (idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    occurred_at timestamptz NOT NULL,
    receipt_id uuid NOT NULL,
    command_id uuid NOT NULL,
    signed_receipt_sha256 text NOT NULL CHECK (signed_receipt_sha256 ~ '^[0-9a-f]{64}$'),
    content_digest text NOT NULL CHECK (content_digest ~ '^[0-9a-f]{64}$'),
    signals_digest text NOT NULL CHECK (signals_digest ~ '^[0-9a-f]{64}$'),
    identity_resolution_sha256 text NOT NULL CHECK (identity_resolution_sha256 ~ '^[0-9a-f]{64}$'),
    identity_resolution_revision bigint NOT NULL CHECK (identity_resolution_revision >= 1),
    identity_issuer text NOT NULL CHECK (identity_issuer = 'soul-registry'),
    identity_audience text NOT NULL CHECK (identity_audience = 'memory-event-ledger'),
    identity_key_role text NOT NULL CHECK (identity_key_role = 'soul-resolution-current'),
    identity_key_id text NOT NULL CHECK (identity_key_id ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
    identity_trust_epoch bigint NOT NULL CHECK (identity_trust_epoch >= 1),
    identity_revocation_epoch bigint NOT NULL CHECK (identity_revocation_epoch >= 0),
    identity_issued_at timestamptz NOT NULL,
    identity_expires_at timestamptz NOT NULL,
    result_issuer text NOT NULL CHECK (result_issuer = 'executor-gateway'),
    result_audience text NOT NULL CHECK (result_audience = 'memory-event-ledger'),
    result_key_role text NOT NULL CHECK (result_key_role = 'verified-observation-receipt'),
    result_key_id text NOT NULL CHECK (result_key_id ~ '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$'),
    result_trust_epoch bigint NOT NULL CHECK (result_trust_epoch >= 1),
    result_revocation_epoch bigint NOT NULL CHECK (result_revocation_epoch >= 0),
    result_issued_at timestamptz NOT NULL,
    result_expires_at timestamptz NOT NULL,
    soul_sequence bigint NOT NULL CHECK (soul_sequence >= 1),
    previous_chain_sha256 text NOT NULL CHECK (previous_chain_sha256 ~ '^[0-9a-f]{64}$'),
    chain_sha256 text NOT NULL CHECK (chain_sha256 ~ '^[0-9a-f]{64}$'),
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_json text NOT NULL CHECK (octet_length(canonical_json) BETWEEN 1 AND 65536),
    event_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (soul_id, soul_sequence),
    UNIQUE (event_id, soul_id),
    UNIQUE (event_id, soul_id, device_binding_id, platform_account_id),
    UNIQUE (receipt_id),
    CHECK (event_id = command_id),
    CHECK (identity_expires_at > identity_issued_at AND identity_expires_at - identity_issued_at <= interval '5 minutes'),
    CHECK (result_issued_at = occurred_at AND result_expires_at = result_issued_at + interval '5 minutes'),
    CHECK (event_json = canonical_json::jsonb),
    CHECK (payload_sha256 = encode(sha256(convert_to(canonical_json, 'UTF8')), 'hex')),
    CHECK (event_json ->> 'schema_version' = '2.0.0'),
    CHECK (event_json ->> 'contract_id' = 'memory.event/v2'),
    CHECK (event_json ->> 'producer_module' = 'memory-event-ledger'),
    CHECK (event_json ->> 'event_id' = event_id::text),
    CHECK (event_json ->> 'soul_id' = soul_id),
    CHECK (event_json ->> 'device_binding_id' = device_binding_id),
    CHECK (event_json ->> 'platform_account_id' = platform_account_id),
    CHECK (event_json ->> 'trace_id' = trace_id),
    CHECK (event_json ->> 'idempotency_key' = idempotency_key),
    CHECK ((event_json ->> 'occurred_at')::timestamptz = occurred_at),
    CHECK (event_json ->> 'privacy_class' = 'personal'),
    CHECK (event_json ->> 'event_type' = 'content.observed'),
    CHECK ((event_json #>> '{observation,receipt_id}')::uuid = receipt_id),
    CHECK ((event_json #>> '{observation,command_id}')::uuid = command_id),
    CHECK (event_json #>> '{observation,signed_receipt_sha256}' = signed_receipt_sha256),
    CHECK (event_json #>> '{observation,content_digest}' = content_digest),
    CHECK (event_json #>> '{observation,signals_digest}' = signals_digest),
    CHECK (event_json #>> '{identity_authority,resolution_sha256}' = identity_resolution_sha256),
    CHECK ((event_json #>> '{identity_authority,resolution_revision}')::bigint = identity_resolution_revision),
    CHECK (event_json #>> '{identity_authority,issuer}' = identity_issuer),
    CHECK (event_json #>> '{identity_authority,audience}' = identity_audience),
    CHECK (event_json #>> '{identity_authority,key_role}' = identity_key_role),
    CHECK (event_json #>> '{identity_authority,key_id}' = identity_key_id),
    CHECK ((event_json #>> '{identity_authority,trust_epoch}')::bigint = identity_trust_epoch),
    CHECK ((event_json #>> '{identity_authority,revocation_epoch}')::bigint = identity_revocation_epoch),
    CHECK ((event_json #>> '{identity_authority,issued_at}')::timestamptz = identity_issued_at),
    CHECK ((event_json #>> '{identity_authority,expires_at}')::timestamptz = identity_expires_at),
    CHECK (event_json #>> '{result_authority,issuer}' = result_issuer),
    CHECK (event_json #>> '{result_authority,audience}' = result_audience),
    CHECK (event_json #>> '{result_authority,key_role}' = result_key_role),
    CHECK (event_json #>> '{result_authority,key_id}' = result_key_id),
    CHECK ((event_json #>> '{result_authority,trust_epoch}')::bigint = result_trust_epoch),
    CHECK ((event_json #>> '{result_authority,revocation_epoch}')::bigint = result_revocation_epoch),
    CHECK ((event_json #>> '{result_authority,issued_at}')::timestamptz = result_issued_at),
    CHECK ((event_json #>> '{result_authority,expires_at}')::timestamptz = result_expires_at),
    CHECK (jsonb_typeof(event_json #> '{observation,interest_signals}') = 'array'),
    CHECK (jsonb_array_length(event_json #> '{observation,interest_signals}') <= 32)
);
CREATE INDEX IF NOT EXISTS ix_memory_events_v2_soul_order ON __SCHEMA__.memory_events_v2(soul_id, soul_sequence);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox_v2 (
    outbox_id uuid PRIMARY KEY,
    event_id uuid NOT NULL UNIQUE,
    soul_id text NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    trace_id text NOT NULL,
    idempotency_key text NOT NULL,
    occurred_at timestamptz NOT NULL,
    topic text NOT NULL CHECK (topic = 'memory.event/v2'),
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
    soul_sequence bigint NOT NULL CHECK (soul_sequence >= 1),
    previous_chain_sha256 text NOT NULL CHECK (previous_chain_sha256 ~ '^[0-9a-f]{64}$'),
    chain_sha256 text NOT NULL CHECK (chain_sha256 ~ '^[0-9a-f]{64}$'),
    canonical_payload_json text NOT NULL CHECK (octet_length(canonical_payload_json) BETWEEN 1 AND 65536),
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (event_id, soul_id, device_binding_id, platform_account_id)
        REFERENCES __SCHEMA__.memory_events_v2(event_id, soul_id, device_binding_id, platform_account_id),
    CHECK (payload_json = canonical_payload_json::jsonb),
    CHECK (payload_sha256 = encode(sha256(convert_to(canonical_payload_json, 'UTF8')), 'hex')),
    CHECK (payload_json ->> 'event_id' = event_id::text),
    CHECK (payload_json ->> 'soul_id' = soul_id),
    CHECK (payload_json ->> 'device_binding_id' = device_binding_id),
    CHECK (payload_json ->> 'platform_account_id' = platform_account_id),
    CHECK (payload_json ->> 'trace_id' = trace_id),
    CHECK (payload_json ->> 'idempotency_key' = idempotency_key),
    CHECK ((payload_json ->> 'occurred_at')::timestamptz = occurred_at)
);
CREATE INDEX IF NOT EXISTS ix_outbox_v2_order ON __SCHEMA__.outbox_v2(created_at, outbox_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.outbox_delivery_v2 (
    outbox_id uuid PRIMARY KEY REFERENCES __SCHEMA__.outbox_v2(outbox_id),
    dispatched_at timestamptz NOT NULL,
    dispatch_receipt_sha256 text NOT NULL CHECK (dispatch_receipt_sha256 ~ '^[0-9a-f]{64}$')
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.quarantine_v2 (
    quarantine_id uuid PRIMARY KEY,
    event_id uuid NOT NULL,
    incoming_soul_id text NOT NULL CHECK (incoming_soul_id ~ '^soul_[0-9a-f]{64}$'),
    existing_sha256 text NOT NULL CHECK (existing_sha256 ~ '^[0-9a-f]{64}$'),
    incoming_sha256 text NOT NULL CHECK (incoming_sha256 ~ '^[0-9a-f]{64}$'),
    incoming_json jsonb NOT NULL,
    reason text NOT NULL CHECK (reason = 'event_id_payload_hash_conflict'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (event_id, incoming_sha256)
);

-- Correction and erasure never rewrite an event. A future independently
-- approved privacy authority may append these records and rebuild projections;
-- no runtime grant is made in this migration.
CREATE TABLE IF NOT EXISTS __SCHEMA__.privacy_tombstones_v2 (
    tombstone_id uuid PRIMARY KEY,
    soul_id text NOT NULL CHECK (soul_id ~ '^soul_[0-9a-f]{64}$'),
    target_event_id uuid NULL,
    authority_receipt_sha256 text NOT NULL CHECK (authority_receipt_sha256 ~ '^[0-9a-f]{64}$'),
    reason_sha256 text NOT NULL CHECK (reason_sha256 ~ '^[0-9a-f]{64}$'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (target_event_id, soul_id)
        REFERENCES __SCHEMA__.memory_events_v2(event_id, soul_id)
);
CREATE TABLE IF NOT EXISTS __SCHEMA__.correction_links_v2 (
    correction_id uuid PRIMARY KEY,
    soul_id text NOT NULL CHECK (soul_id ~ '^soul_[0-9a-f]{64}$'),
    target_event_id uuid NOT NULL,
    replacement_event_id uuid NOT NULL,
    authority_receipt_sha256 text NOT NULL CHECK (authority_receipt_sha256 ~ '^[0-9a-f]{64}$'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK (target_event_id <> replacement_event_id),
    UNIQUE(target_event_id, replacement_event_id),
    FOREIGN KEY (target_event_id, soul_id)
        REFERENCES __SCHEMA__.memory_events_v2(event_id, soul_id),
    FOREIGN KEY (replacement_event_id, soul_id)
        REFERENCES __SCHEMA__.memory_events_v2(event_id, soul_id)
);

CREATE OR REPLACE FUNCTION __SCHEMA__.require_runtime_capability_v2(p_capability text)
RETURNS void LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE expected text;
BEGIN
    IF p_capability IS NULL OR p_capability !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION 'invalid runtime capability shape' USING ERRCODE = '28000';
    END IF;
    SELECT capability_sha256 INTO expected FROM __SCHEMA__.runtime_authority_v2 WHERE singleton;
    IF expected IS NULL OR encode(sha256(convert_to(p_capability, 'UTF8')), 'hex') <> expected THEN
        RAISE EXCEPTION 'runtime capability verification failed' USING ERRCODE = '28000';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.append_memory_event_v2(
    p_capability text, p_event_id uuid, p_outbox_id uuid, p_canonical_json text, p_payload_sha256 text)
RETURNS TABLE(disposition text, soul_sequence bigint, outbox_id uuid)
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    body jsonb; existing_hash text; event_soul text; event_device text; event_account text;
    event_trace text; event_idem text; event_occurred timestamptz; prior_sequence bigint;
    prior_chain text; next_sequence bigint; next_chain text;
BEGIN
    PERFORM __SCHEMA__.require_runtime_capability_v2(p_capability);
    PERFORM pg_advisory_xact_lock(hashtextextended(p_event_id::text, 0));
    IF p_canonical_json IS NULL OR octet_length(p_canonical_json) NOT BETWEEN 1 AND 65536 OR
       p_payload_sha256 !~ '^[0-9a-f]{64}$' OR
       encode(sha256(convert_to(p_canonical_json, 'UTF8')), 'hex') <> p_payload_sha256 THEN
        RAISE EXCEPTION 'canonical payload or digest is invalid' USING ERRCODE = '23514';
    END IF;
    BEGIN body := p_canonical_json::jsonb; EXCEPTION WHEN others THEN RAISE EXCEPTION 'canonical payload is not JSON' USING ERRCODE = '22023'; END;
    IF body ->> 'schema_version' <> '2.0.0' OR body ->> 'contract_id' <> 'memory.event/v2' OR
       body ->> 'producer_module' <> 'memory-event-ledger' OR body ->> 'event_type' <> 'content.observed' OR
       body ->> 'privacy_class' <> 'personal' OR (body ->> 'event_id')::uuid <> p_event_id OR
       (body #>> '{observation,command_id}')::uuid <> p_event_id THEN
        RAISE EXCEPTION 'v2 contract metadata or event id mismatch' USING ERRCODE = '23514';
    END IF;
    event_soul := body ->> 'soul_id'; event_device := body ->> 'device_binding_id'; event_account := body ->> 'platform_account_id';
    event_trace := body ->> 'trace_id'; event_idem := body ->> 'idempotency_key'; event_occurred := (body ->> 'occurred_at')::timestamptz;

    SELECT e.payload_sha256 INTO existing_hash FROM __SCHEMA__.memory_events_v2 e WHERE e.event_id = p_event_id FOR UPDATE;
    IF existing_hash IS NOT NULL THEN
        IF existing_hash = p_payload_sha256 THEN RETURN QUERY SELECT 'DUPLICATE_NO_OP'::text, NULL::bigint, NULL::uuid; RETURN; END IF;
        INSERT INTO __SCHEMA__.quarantine_v2(quarantine_id, event_id, incoming_soul_id, existing_sha256, incoming_sha256, incoming_json, reason)
        VALUES (gen_random_uuid(), p_event_id, event_soul, existing_hash, p_payload_sha256, body, 'event_id_payload_hash_conflict')
        ON CONFLICT (event_id, incoming_sha256) DO NOTHING;
        RETURN QUERY SELECT 'QUARANTINED'::text, NULL::bigint, NULL::uuid; RETURN;
    END IF;

    INSERT INTO __SCHEMA__.soul_heads_v2(soul_id, last_sequence, last_chain_sha256)
    VALUES (event_soul, 0, repeat('0', 64)) ON CONFLICT (soul_id) DO NOTHING;
    SELECT h.last_sequence, h.last_chain_sha256 INTO prior_sequence, prior_chain
    FROM __SCHEMA__.soul_heads_v2 h WHERE h.soul_id = event_soul FOR UPDATE;
    next_sequence := prior_sequence + 1;
    next_chain := encode(sha256(convert_to(prior_chain || ':' || next_sequence::text || ':' || p_payload_sha256, 'UTF8')), 'hex');

    INSERT INTO __SCHEMA__.memory_events_v2(
        event_id, soul_id, device_binding_id, platform_account_id, trace_id, idempotency_key, occurred_at,
        receipt_id, command_id, signed_receipt_sha256, content_digest, signals_digest,
        identity_resolution_sha256, identity_resolution_revision, identity_issuer, identity_audience, identity_key_role,
        identity_key_id, identity_trust_epoch, identity_revocation_epoch, identity_issued_at, identity_expires_at,
        result_issuer, result_audience, result_key_role, result_key_id, result_trust_epoch, result_revocation_epoch,
        result_issued_at, result_expires_at,
        soul_sequence, previous_chain_sha256, chain_sha256, payload_sha256, canonical_json, event_json)
    VALUES (p_event_id, event_soul, event_device, event_account, event_trace, event_idem, event_occurred,
        (body #>> '{observation,receipt_id}')::uuid, (body #>> '{observation,command_id}')::uuid,
        body #>> '{observation,signed_receipt_sha256}', body #>> '{observation,content_digest}', body #>> '{observation,signals_digest}',
        body #>> '{identity_authority,resolution_sha256}', (body #>> '{identity_authority,resolution_revision}')::bigint,
        body #>> '{identity_authority,issuer}', body #>> '{identity_authority,audience}', body #>> '{identity_authority,key_role}',
        body #>> '{identity_authority,key_id}', (body #>> '{identity_authority,trust_epoch}')::bigint,
        (body #>> '{identity_authority,revocation_epoch}')::bigint,
        (body #>> '{identity_authority,issued_at}')::timestamptz, (body #>> '{identity_authority,expires_at}')::timestamptz,
        body #>> '{result_authority,issuer}', body #>> '{result_authority,audience}', body #>> '{result_authority,key_role}',
        body #>> '{result_authority,key_id}',
        (body #>> '{result_authority,trust_epoch}')::bigint, (body #>> '{result_authority,revocation_epoch}')::bigint,
        (body #>> '{result_authority,issued_at}')::timestamptz, (body #>> '{result_authority,expires_at}')::timestamptz,
        next_sequence, prior_chain, next_chain, p_payload_sha256, p_canonical_json, body);
    INSERT INTO __SCHEMA__.outbox_v2(
        outbox_id, event_id, soul_id, device_binding_id, platform_account_id, trace_id, idempotency_key, occurred_at,
        topic, payload_sha256, soul_sequence, previous_chain_sha256, chain_sha256, canonical_payload_json, payload_json)
    VALUES (p_outbox_id, p_event_id, event_soul, event_device, event_account, event_trace, event_idem, event_occurred,
        'memory.event/v2', p_payload_sha256, next_sequence, prior_chain, next_chain, p_canonical_json, body);
    UPDATE __SCHEMA__.soul_heads_v2 SET last_sequence = next_sequence, last_chain_sha256 = next_chain, updated_at = clock_timestamp()
    WHERE soul_id = event_soul;
    RETURN QUERY SELECT 'INSERTED'::text, next_sequence, p_outbox_id;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.read_soul_events_v2(p_capability text, p_soul_id text)
RETURNS TABLE(soul_sequence bigint, canonical_json text, payload_sha256 text, previous_chain_sha256 text, chain_sha256 text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, __SCHEMA__
AS $function$
BEGIN
    PERFORM __SCHEMA__.require_runtime_capability_v2(p_capability);
    IF p_soul_id !~ '^soul_[0-9a-f]{64}$' THEN RAISE EXCEPTION 'invalid soul id' USING ERRCODE = '22023'; END IF;
    RETURN QUERY SELECT e.soul_sequence, e.canonical_json, e.payload_sha256, e.previous_chain_sha256, e.chain_sha256
    FROM __SCHEMA__.memory_events_v2 e WHERE e.soul_id = p_soul_id ORDER BY e.soul_sequence;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_v2_mutation()
RETURNS trigger LANGUAGE plpgsql AS $function$ BEGIN RAISE EXCEPTION 'v2 ledger records are append-only'; END; $function$;

DO $block$
DECLARE table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY['memory_events_v2','outbox_v2','outbox_delivery_v2','quarantine_v2','privacy_tombstones_v2','correction_links_v2']
  LOOP
    EXECUTE format('DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I', table_name || '_immutable', table_name);
    EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE OR TRUNCATE ON __SCHEMA__.%I FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_v2_mutation()', table_name || '_immutable', table_name);
  END LOOP;
END;
$block$;

REVOKE ALL ON ALL TABLES IN SCHEMA __SCHEMA__ FROM PUBLIC, __RUNTIME_ROLE__;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA __SCHEMA__ FROM PUBLIC, __RUNTIME_ROLE__;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA __SCHEMA__ FROM PUBLIC, __RUNTIME_ROLE__;
GRANT SELECT, INSERT ON ALL TABLES IN SCHEMA __SCHEMA__ TO __ADMIN_ROLE__;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA __SCHEMA__ TO __ADMIN_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.append_memory_event_v2(text, uuid, uuid, text, text) TO __RUNTIME_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.read_soul_events_v2(text, text) TO __RUNTIME_ROLE__;
ALTER DEFAULT PRIVILEGES IN SCHEMA __SCHEMA__ REVOKE ALL ON TABLES FROM PUBLIC, __RUNTIME_ROLE__;
ALTER DEFAULT PRIVILEGES IN SCHEMA __SCHEMA__ REVOKE ALL ON FUNCTIONS FROM PUBLIC, __RUNTIME_ROLE__;

-- v1 is retained solely for quarantine/read compatibility. No runtime path or
-- runtime grant is created for memory.event/v1 or memory.outbox/v1.
REVOKE ALL ON __SCHEMA__.memory_events, __SCHEMA__.outbox, __SCHEMA__.quarantine FROM PUBLIC, __RUNTIME_ROLE__;
