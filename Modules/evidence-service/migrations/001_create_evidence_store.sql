CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.test_evidence (
    evidence_id uuid PRIMARY KEY,
    soul_id text NOT NULL CHECK (char_length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (char_length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    platform_account_id text NOT NULL CHECK (char_length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[0-9a-f]{32}$'),
    trace_id text NOT NULL CHECK (char_length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    idempotency_key text NOT NULL CHECK (char_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[0-9a-f]{64}$'),
    module_id text NOT NULL,
    status text NOT NULL,
    verification_level text NOT NULL,
    baseline_commit text NOT NULL CHECK (length(baseline_commit) = 40),
    instruction_receipt_sha256 text NOT NULL CHECK (length(instruction_receipt_sha256) = 64),
    receipt_sha256 text NOT NULL CHECK (length(receipt_sha256) = 64),
    artifact_set_sha256 text NOT NULL CHECK (length(artifact_set_sha256) = 64),
    source_receipt_set_sha256 text NOT NULL CHECK (length(source_receipt_set_sha256) = 64),
    runner_key_id text NOT NULL,
    attestation_algorithm text NOT NULL,
    attestation_issued_at timestamptz NOT NULL,
    attestation_signature text NOT NULL CHECK (length(attestation_signature) BETWEEN 1 AND 512),
    attestation_sha256 text NOT NULL CHECK (length(attestation_sha256) = 64),
    bundle_checksum text NOT NULL CHECK (length(bundle_checksum) = 64),
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS ix_test_evidence_soul_trace
    ON __SCHEMA__.test_evidence (soul_id, trace_id, created_at);

CREATE TABLE IF NOT EXISTS __SCHEMA__.evidence_artifacts (
    evidence_id uuid NOT NULL REFERENCES __SCHEMA__.test_evidence(evidence_id),
    artifact_id text NOT NULL,
    artifact_role text NOT NULL CHECK (artifact_role IN ('receipt', 'source')),
    sha256 text NOT NULL CHECK (length(sha256) = 64),
    size_bytes bigint NOT NULL CHECK (size_bytes >= 0),
    media_type text NOT NULL,
    content_bytes bytea NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (evidence_id, artifact_id),
    CHECK (octet_length(content_bytes) = size_bytes)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.evidence_quarantine (
    quarantine_id uuid PRIMARY KEY,
    evidence_id uuid NOT NULL,
    incoming_soul_id text NOT NULL CHECK (char_length(incoming_soul_id) = 69 AND incoming_soul_id ~ '^soul_[0-9a-f]{64}$'),
    existing_checksum text NOT NULL CHECK (length(existing_checksum) = 64),
    incoming_checksum text NOT NULL CHECK (length(incoming_checksum) = 64),
    incoming_artifact_set_sha256 text NOT NULL CHECK (length(incoming_artifact_set_sha256) = 64),
    reason_code text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (evidence_id, incoming_checksum)
);

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_evidence_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'evidence records are immutable';
END;
$function$;

DROP TRIGGER IF EXISTS test_evidence_immutable ON __SCHEMA__.test_evidence;
CREATE TRIGGER test_evidence_immutable
BEFORE UPDATE OR DELETE ON __SCHEMA__.test_evidence
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_evidence_mutation();

DROP TRIGGER IF EXISTS evidence_quarantine_immutable ON __SCHEMA__.evidence_quarantine;
CREATE TRIGGER evidence_quarantine_immutable
BEFORE UPDATE OR DELETE ON __SCHEMA__.evidence_quarantine
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_evidence_mutation();

DROP TRIGGER IF EXISTS evidence_artifacts_immutable ON __SCHEMA__.evidence_artifacts;
CREATE TRIGGER evidence_artifacts_immutable
BEFORE UPDATE OR DELETE ON __SCHEMA__.evidence_artifacts
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_evidence_mutation();
