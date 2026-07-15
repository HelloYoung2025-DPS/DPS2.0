CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.souls (
    soul_id text PRIMARY KEY,
    tenant_id text NOT NULL,
    created_at timestamptz NOT NULL,
    tombstoned_at timestamptz NULL,
    CONSTRAINT soul_id_format CHECK (char_length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT uq_soul_tenant_identity UNIQUE (tenant_id, soul_id)
);

CREATE INDEX IF NOT EXISTS ix_souls_tenant
    ON __SCHEMA__.souls (tenant_id, soul_id);

CREATE OR REPLACE FUNCTION __SCHEMA__.protect_soul_identity()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'souls are append-only; use an audited tombstone';
    END IF;

    IF NEW.soul_id IS DISTINCT FROM OLD.soul_id
       OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR (OLD.tombstoned_at IS NOT NULL AND NEW.tombstoned_at IS DISTINCT FROM OLD.tombstoned_at) THEN
        RAISE EXCEPTION 'immutable Soul identity fields cannot be changed';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_protect_soul_identity ON __SCHEMA__.souls;
CREATE TRIGGER trg_protect_soul_identity
BEFORE UPDATE OR DELETE ON __SCHEMA__.souls
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.protect_soul_identity();

CREATE TABLE IF NOT EXISTS __SCHEMA__.identity_aliases (
    alias_id uuid PRIMARY KEY,
    tenant_id text NOT NULL,
    alias_kind text NOT NULL,
    alias_digest text NOT NULL,
    alias_key_id text NOT NULL,
    soul_id text NOT NULL,
    verification_evidence_sha256 text NOT NULL,
    verified_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    revoked_at timestamptz NULL,
    revocation_reason_sha256 text NULL,
    CONSTRAINT alias_kind_known CHECK (alias_kind IN ('email', 'phone', 'platform_id')),
    CONSTRAINT alias_digest_format CHECK (char_length(alias_digest) = 64 AND alias_digest ~ '^[a-f0-9]{64}$'),
    CONSTRAINT verification_digest_format CHECK (char_length(verification_evidence_sha256) = 64 AND verification_evidence_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT revocation_digest_format CHECK (
        revocation_reason_sha256 IS NULL OR (char_length(revocation_reason_sha256) = 64 AND revocation_reason_sha256 ~ '^[a-f0-9]{64}$')),
    CONSTRAINT uq_alias_digest UNIQUE (tenant_id, alias_kind, alias_key_id, alias_digest),
    CONSTRAINT fk_alias_soul_tenant FOREIGN KEY (tenant_id, soul_id)
        REFERENCES __SCHEMA__.souls (tenant_id, soul_id)
);

CREATE INDEX IF NOT EXISTS ix_identity_aliases_soul
    ON __SCHEMA__.identity_aliases (tenant_id, soul_id, revoked_at);

CREATE OR REPLACE FUNCTION __SCHEMA__.protect_identity_alias()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'identity aliases are append-only';
    END IF;

    IF NEW.alias_id IS DISTINCT FROM OLD.alias_id
       OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
       OR NEW.alias_kind IS DISTINCT FROM OLD.alias_kind
       OR NEW.alias_digest IS DISTINCT FROM OLD.alias_digest
       OR NEW.alias_key_id IS DISTINCT FROM OLD.alias_key_id
       OR NEW.soul_id IS DISTINCT FROM OLD.soul_id
       OR NEW.verification_evidence_sha256 IS DISTINCT FROM OLD.verification_evidence_sha256
       OR NEW.verified_at IS DISTINCT FROM OLD.verified_at
       OR NEW.created_at IS DISTINCT FROM OLD.created_at
       OR (OLD.revoked_at IS NOT NULL AND NEW.revoked_at IS DISTINCT FROM OLD.revoked_at)
       OR (OLD.revocation_reason_sha256 IS NOT NULL AND NEW.revocation_reason_sha256 IS DISTINCT FROM OLD.revocation_reason_sha256)
       OR ((NEW.revoked_at IS NULL) <> (NEW.revocation_reason_sha256 IS NULL)) THEN
        RAISE EXCEPTION 'immutable alias fields cannot be changed';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_protect_identity_alias ON __SCHEMA__.identity_aliases;
CREATE TRIGGER trg_protect_identity_alias
BEFORE UPDATE OR DELETE ON __SCHEMA__.identity_aliases
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.protect_identity_alias();

CREATE TABLE IF NOT EXISTS __SCHEMA__.resolution_receipts (
    tenant_id text NOT NULL,
    idempotency_key text NOT NULL,
    operation text NOT NULL,
    request_sha256 text NOT NULL,
    soul_id text NOT NULL,
    device_binding_id text NOT NULL,
    platform_account_id text NOT NULL,
    trace_id text NOT NULL,
    occurred_at timestamptz NOT NULL,
    alias_kind text NOT NULL,
    alias_digest text NOT NULL,
    alias_key_id text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (tenant_id, idempotency_key),
    CONSTRAINT receipt_idempotency_format CHECK (char_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT receipt_soul_format CHECK (char_length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT receipt_device_format CHECK (char_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    CONSTRAINT receipt_account_format CHECK (char_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    CONSTRAINT receipt_trace_format CHECK (char_length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    CONSTRAINT receipt_request_digest_format CHECK (char_length(request_sha256) = 64 AND request_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT receipt_alias_digest_format CHECK (char_length(alias_digest) = 64 AND alias_digest ~ '^[a-f0-9]{64}$'),
    CONSTRAINT fk_receipt_soul_tenant FOREIGN KEY (tenant_id, soul_id)
        REFERENCES __SCHEMA__.souls (tenant_id, soul_id)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.mutation_receipts (
    tenant_id text NOT NULL,
    idempotency_key text NOT NULL,
    operation text NOT NULL,
    request_sha256 text NOT NULL,
    entity_id text NOT NULL,
    trace_id text NOT NULL,
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (tenant_id, idempotency_key),
    CONSTRAINT mutation_idempotency_format CHECK (char_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    CONSTRAINT mutation_entity_format CHECK (char_length(entity_id) = 69 AND entity_id ~ '^soul_[a-f0-9]{64}$'),
    CONSTRAINT mutation_trace_format CHECK (char_length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    CONSTRAINT mutation_request_digest_format CHECK (char_length(request_sha256) = 64 AND request_sha256 ~ '^[a-f0-9]{64}$'),
    CONSTRAINT fk_mutation_soul_tenant FOREIGN KEY (tenant_id, entity_id)
        REFERENCES __SCHEMA__.souls (tenant_id, soul_id)
);

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_receipt_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'identity receipts are append-only';
END;
$$;

DROP TRIGGER IF EXISTS trg_protect_resolution_receipt ON __SCHEMA__.resolution_receipts;
CREATE TRIGGER trg_protect_resolution_receipt
BEFORE UPDATE OR DELETE ON __SCHEMA__.resolution_receipts
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_receipt_mutation();

DROP TRIGGER IF EXISTS trg_protect_mutation_receipt ON __SCHEMA__.mutation_receipts;
CREATE TRIGGER trg_protect_mutation_receipt
BEFORE UPDATE OR DELETE ON __SCHEMA__.mutation_receipts
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_receipt_mutation();

REVOKE UPDATE, DELETE ON __SCHEMA__.resolution_receipts FROM PUBLIC;
REVOKE UPDATE, DELETE ON __SCHEMA__.mutation_receipts FROM PUBLIC;
