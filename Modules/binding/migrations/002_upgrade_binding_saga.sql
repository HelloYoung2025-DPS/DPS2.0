-- Pre-release schema convergence for the provider-reservation saga.
-- No 0.1/0.2 binding database was release-eligible. Existing rows that cannot
-- be given an authentic provider reservation or scope are rejected rather than
-- silently fabricated. Empty pre-release schemas and the current 001 are safe.

ALTER TABLE __SCHEMA__.bindings
    ADD COLUMN IF NOT EXISTS reservation_id text;

DO $migration$
BEGIN
    IF EXISTS (SELECT 1 FROM __SCHEMA__.bindings WHERE reservation_id IS NULL) THEN
        RAISE EXCEPTION
            'pre-release binding schema contains rows without provider reservations; revoke/export under the old build and recreate the proposed schema';
    END IF;
END;
$migration$;

ALTER TABLE __SCHEMA__.bindings
    ALTER COLUMN reservation_id SET NOT NULL;

ALTER TABLE __SCHEMA__.binding_attempts
    ADD COLUMN IF NOT EXISTS trace_id text,
    ADD COLUMN IF NOT EXISTS occurred_at timestamptz;

DO $migration$
BEGIN
    IF EXISTS (
        SELECT 1 FROM __SCHEMA__.binding_attempts
        WHERE trace_id IS NULL OR occurred_at IS NULL
    ) THEN
        RAISE EXCEPTION
            'pre-release binding attempts lack a recoverable trace/timestamp; recreate the proposed schema';
    END IF;
END;
$migration$;

ALTER TABLE __SCHEMA__.binding_attempts
    ALTER COLUMN trace_id SET NOT NULL,
    ALTER COLUMN occurred_at SET NOT NULL;

DROP TRIGGER IF EXISTS binding_revisions_append_only ON __SCHEMA__.binding_revisions;
ALTER TABLE __SCHEMA__.binding_revisions
    ADD COLUMN IF NOT EXISTS payload_canonical text;
UPDATE __SCHEMA__.binding_revisions
SET payload_canonical = payload_json::text,
    payload_sha256 = encode(sha256(convert_to(payload_json::text, 'UTF8')), 'hex')
WHERE payload_canonical IS NULL;
ALTER TABLE __SCHEMA__.binding_revisions
    ALTER COLUMN payload_canonical SET NOT NULL;

ALTER TABLE __SCHEMA__.idempotency_receipts
    ADD COLUMN IF NOT EXISTS result_canonical text;
UPDATE __SCHEMA__.idempotency_receipts
SET result_canonical = result_json::text
WHERE result_canonical IS NULL;
ALTER TABLE __SCHEMA__.idempotency_receipts
    ALTER COLUMN result_canonical SET NOT NULL;

ALTER TABLE __SCHEMA__.outbox
    ADD COLUMN IF NOT EXISTS payload_canonical text;
UPDATE __SCHEMA__.outbox
SET payload_canonical = payload_json::text,
    payload_sha256 = encode(sha256(convert_to(payload_json::text, 'UTF8')), 'hex')
WHERE payload_canonical IS NULL;
ALTER TABLE __SCHEMA__.outbox
    ALTER COLUMN payload_canonical SET NOT NULL;

ALTER TABLE __SCHEMA__.idempotency_quarantine
    ADD COLUMN IF NOT EXISTS soul_id text,
    ADD COLUMN IF NOT EXISTS device_binding_id text,
    ADD COLUMN IF NOT EXISTS platform_account_id text,
    ADD COLUMN IF NOT EXISTS idempotency_key_sha256 text;

DO $migration$
BEGIN
    IF EXISTS (
        SELECT 1 FROM __SCHEMA__.idempotency_quarantine
        WHERE soul_id IS NULL
           OR device_binding_id IS NULL
           OR platform_account_id IS NULL
           OR idempotency_key_sha256 IS NULL
    ) THEN
        RAISE EXCEPTION
            'pre-release quarantine rows lack an authoritative Soul/binding/account scope; export and recreate the proposed schema';
    END IF;
END;
$migration$;

ALTER TABLE __SCHEMA__.idempotency_quarantine
    ALTER COLUMN soul_id SET NOT NULL,
    ALTER COLUMN device_binding_id SET NOT NULL,
    ALTER COLUMN platform_account_id SET NOT NULL,
    ALTER COLUMN idempotency_key_sha256 SET NOT NULL;

ALTER TABLE __SCHEMA__.idempotency_quarantine
    DROP COLUMN IF EXISTS idempotency_key;

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_binding_revision_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'binding revisions are append-only';
END;
$function$;

CREATE TRIGGER binding_revisions_append_only
BEFORE UPDATE OR DELETE ON __SCHEMA__.binding_revisions
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_binding_revision_mutation();
