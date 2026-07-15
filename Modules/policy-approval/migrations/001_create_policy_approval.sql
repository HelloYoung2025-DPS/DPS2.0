CREATE SCHEMA IF NOT EXISTS __SCHEMA__;

CREATE TABLE IF NOT EXISTS __SCHEMA__.policy_runtime_revisions
(
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    revision bigint NOT NULL CHECK (revision > 0),
    state_status text NOT NULL CHECK (state_status IN ('ACTIVE', 'REVOKED')),
    policy_version text NOT NULL CHECK (policy_version ~ '^[0-9]+\.[0-9]+\.[0-9]+$'),
    enabled_policy_ids text[] NOT NULL CHECK (cardinality(enabled_policy_ids) BETWEEN 1 AND 32),
    kill_switch_enabled boolean NOT NULL,
    remaining_rate_budget integer NOT NULL CHECK (remaining_rate_budget >= 0),
    platform_authorized boolean NOT NULL,
    platform_authorization_id text NULL CHECK (platform_authorization_id IS NULL OR length(platform_authorization_id) BETWEEN 1 AND 256),
    execution_enabled boolean NOT NULL,
    release_bom_sha256 text NOT NULL CHECK (release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    valid_until timestamptz NOT NULL,
    state_sha256 text NOT NULL CHECK (state_sha256 ~ '^[a-f0-9]{64}$'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (soul_id, device_binding_id, platform_account_id, revision),
    UNIQUE (soul_id, device_binding_id, platform_account_id, revision, state_sha256),
    CHECK ((platform_authorized AND platform_authorization_id IS NOT NULL) OR (NOT platform_authorized AND platform_authorization_id IS NULL))
);

CREATE INDEX IF NOT EXISTS ix_policy_runtime_current_scope
    ON __SCHEMA__.policy_runtime_revisions
       (soul_id, device_binding_id, platform_account_id, revision DESC);

CREATE OR REPLACE FUNCTION __SCHEMA__.serialize_policy_runtime_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    expected_revision bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(
        'policy-runtime:' || NEW.soul_id || ':' || NEW.device_binding_id || ':' || NEW.platform_account_id,
        0));
    SELECT COALESCE(max(existing.revision), 0) + 1
      INTO expected_revision
      FROM __SCHEMA__.policy_runtime_revisions AS existing
     WHERE existing.soul_id = NEW.soul_id
       AND existing.device_binding_id = NEW.device_binding_id
       AND existing.platform_account_id = NEW.platform_account_id;
    IF NEW.revision <> expected_revision THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'policy runtime revisions must be contiguous for the exact scope';
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS policy_runtime_revisions_serialize_insert ON __SCHEMA__.policy_runtime_revisions;
CREATE TRIGGER policy_runtime_revisions_serialize_insert
BEFORE INSERT ON __SCHEMA__.policy_runtime_revisions
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.serialize_policy_runtime_revision();

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_decisions
(
    approval_id uuid PRIMARY KEY,
    proposal_id uuid NOT NULL,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    occurred_at timestamptz NOT NULL,
    decision text NOT NULL CHECK (decision IN ('APPROVED', 'DENIED')),
    proposal_sha256 text NOT NULL CHECK (proposal_sha256 ~ '^[a-f0-9]{64}$'),
    decision_sha256 text NOT NULL CHECK (decision_sha256 ~ '^[a-f0-9]{64}$'),
    command_sha256 text NOT NULL CHECK (command_sha256 ~ '^[a-f0-9]{64}$'),
    trust_evidence_sha256 text NOT NULL CHECK (trust_evidence_sha256 ~ '^[a-f0-9]{64}$'),
    runtime_revision bigint NOT NULL CHECK (runtime_revision > 0),
    runtime_state_sha256 text NOT NULL CHECK (runtime_state_sha256 ~ '^[a-f0-9]{64}$'),
    release_bom_sha256 text NOT NULL CHECK (release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    valid_until timestamptz NOT NULL,
    decision_json jsonb NOT NULL CHECK (jsonb_typeof(decision_json) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (soul_id, device_binding_id, platform_account_id, idempotency_key),
    UNIQUE (approval_id, soul_id, device_binding_id, platform_account_id),
    FOREIGN KEY (soul_id, device_binding_id, platform_account_id, runtime_revision, runtime_state_sha256)
        REFERENCES __SCHEMA__.policy_runtime_revisions(soul_id, device_binding_id, platform_account_id, revision, state_sha256) ON DELETE RESTRICT,
    CHECK (decision_json ->> 'contract_id' = 'approval.decision/v1'),
    CHECK (decision_json ->> 'producer_module' = 'policy-approval'),
    CHECK ((decision_json ->> 'approval_id')::uuid = approval_id),
    CHECK ((decision_json ->> 'proposal_id')::uuid = proposal_id),
    CHECK (decision_json ->> 'soul_id' = soul_id),
    CHECK (decision_json ->> 'device_binding_id' = device_binding_id),
    CHECK (decision_json ->> 'platform_account_id' = platform_account_id),
    CHECK (decision_json ->> 'trace_id' = trace_id),
    CHECK (decision_json ->> 'idempotency_key' = idempotency_key),
    CHECK ((decision_json ->> 'occurred_at')::timestamptz = occurred_at),
    CHECK (decision_json ->> 'decision' = decision),
    CHECK (occurred_at < valid_until)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_status_revisions
(
    approval_id uuid NOT NULL REFERENCES __SCHEMA__.approval_decisions(approval_id) ON DELETE RESTRICT,
    revision bigint NOT NULL CHECK (revision > 0),
    status text NOT NULL CHECK (status IN ('ACTIVE', 'REVOKED')),
    reason_code text NOT NULL CHECK (reason_code IN ('ISSUED', 'CONTROL_PLANE_REVOKED')),
    reason_sha256 text NOT NULL CHECK (reason_sha256 ~ '^[a-f0-9]{64}$'),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (approval_id, revision),
    CHECK ((revision = 1 AND status = 'ACTIVE' AND reason_code = 'ISSUED') OR
           (revision > 1 AND status = 'REVOKED' AND reason_code = 'CONTROL_PLANE_REVOKED'))
);

CREATE OR REPLACE FUNCTION __SCHEMA__.serialize_approval_status_revision()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    expected_revision bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtextextended(
        'approval:' || replace(NEW.approval_id::text, '-', ''),
        0));
    SELECT COALESCE(max(existing.revision), 0) + 1
      INTO expected_revision
      FROM __SCHEMA__.approval_status_revisions AS existing
     WHERE existing.approval_id = NEW.approval_id;
    IF NEW.revision <> expected_revision THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'approval status revisions must be contiguous';
    END IF;
    RETURN NEW;
END;
$function$;

DROP TRIGGER IF EXISTS approval_status_revisions_serialize_insert ON __SCHEMA__.approval_status_revisions;
CREATE TRIGGER approval_status_revisions_serialize_insert
BEFORE INSERT ON __SCHEMA__.approval_status_revisions
FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.serialize_approval_status_revision();

CREATE TABLE IF NOT EXISTS __SCHEMA__.policy_rate_consumptions
(
    approval_id uuid PRIMARY KEY,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    runtime_revision bigint NOT NULL CHECK (runtime_revision > 0),
    runtime_state_sha256 text NOT NULL CHECK (runtime_state_sha256 ~ '^[a-f0-9]{64}$'),
    units integer NOT NULL CHECK (units = 1),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    FOREIGN KEY (soul_id, device_binding_id, platform_account_id, runtime_revision, runtime_state_sha256)
        REFERENCES __SCHEMA__.policy_runtime_revisions(soul_id, device_binding_id, platform_account_id, revision, state_sha256) ON DELETE RESTRICT,
    FOREIGN KEY (approval_id, soul_id, device_binding_id, platform_account_id)
        REFERENCES __SCHEMA__.approval_decisions(approval_id, soul_id, device_binding_id, platform_account_id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_policy_rate_consumptions_scope
    ON __SCHEMA__.policy_rate_consumptions
       (soul_id, device_binding_id, platform_account_id, approval_id);

CREATE INDEX IF NOT EXISTS ix_approval_status_current
    ON __SCHEMA__.approval_status_revisions (approval_id, revision DESC);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_idempotency_receipts
(
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    mutation_kind text NOT NULL CHECK (mutation_kind IN ('decision', 'revoke')),
    command_sha256 text NOT NULL CHECK (command_sha256 ~ '^[a-f0-9]{64}$'),
    approval_id uuid NOT NULL REFERENCES __SCHEMA__.approval_decisions(approval_id) ON DELETE RESTRICT,
    decision_sha256 text NOT NULL CHECK (decision_sha256 ~ '^[a-f0-9]{64}$'),
    status_revision bigint NOT NULL CHECK (status_revision > 0),
    result_json jsonb NOT NULL CHECK (jsonb_typeof(result_json) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (soul_id, device_binding_id, platform_account_id, idempotency_key),
    UNIQUE (approval_id, status_revision, soul_id, device_binding_id, platform_account_id, idempotency_key),
    FOREIGN KEY (approval_id, soul_id, device_binding_id, platform_account_id)
        REFERENCES __SCHEMA__.approval_decisions(approval_id, soul_id, device_binding_id, platform_account_id) ON DELETE RESTRICT,
    FOREIGN KEY (approval_id, status_revision)
        REFERENCES __SCHEMA__.approval_status_revisions(approval_id, revision) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_outbox
(
    outbox_id uuid PRIMARY KEY,
    approval_id uuid NOT NULL,
    status_revision bigint NOT NULL,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    topic text NOT NULL CHECK (topic IN ('approval.decision/v1', 'policy-approval.status/internal-v1')),
    payload_sha256 text NOT NULL CHECK (payload_sha256 ~ '^[a-f0-9]{64}$'),
    payload_json jsonb NOT NULL CHECK (jsonb_typeof(payload_json) = 'object'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (approval_id, status_revision),
    FOREIGN KEY (approval_id, soul_id, device_binding_id, platform_account_id)
        REFERENCES __SCHEMA__.approval_decisions(approval_id, soul_id, device_binding_id, platform_account_id) ON DELETE RESTRICT,
    FOREIGN KEY (approval_id, status_revision)
        REFERENCES __SCHEMA__.approval_status_revisions(approval_id, revision) ON DELETE RESTRICT,
    FOREIGN KEY (approval_id, status_revision, soul_id, device_binding_id, platform_account_id, idempotency_key)
        REFERENCES __SCHEMA__.approval_idempotency_receipts(approval_id, status_revision, soul_id, device_binding_id, platform_account_id, idempotency_key) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_idempotency_quarantine
(
    quarantine_id uuid PRIMARY KEY,
    scope_sha256 text NOT NULL CHECK (scope_sha256 ~ '^[a-f0-9]{64}$'),
    idempotency_sha256 text NOT NULL CHECK (idempotency_sha256 ~ '^[a-f0-9]{64}$'),
    mutation_kind text NOT NULL CHECK (mutation_kind IN ('decision', 'revoke')),
    existing_command_sha256 text NOT NULL CHECK (existing_command_sha256 ~ '^[a-f0-9]{64}$'),
    incoming_command_sha256 text NOT NULL CHECK (incoming_command_sha256 ~ '^[a-f0-9]{64}$'),
    reason text NOT NULL CHECK (reason = 'scoped_idempotency_digest_conflict'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (scope_sha256, idempotency_sha256, incoming_command_sha256)
);

CREATE OR REPLACE FUNCTION __SCHEMA__.reject_policy_approval_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    RAISE EXCEPTION 'policy-approval rows are append-only';
END;
$function$;

DROP TRIGGER IF EXISTS policy_runtime_revisions_append_only ON __SCHEMA__.policy_runtime_revisions;
CREATE TRIGGER policy_runtime_revisions_append_only BEFORE UPDATE OR DELETE ON __SCHEMA__.policy_runtime_revisions FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
DROP TRIGGER IF EXISTS policy_runtime_revisions_no_truncate ON __SCHEMA__.policy_runtime_revisions;
CREATE TRIGGER policy_runtime_revisions_no_truncate BEFORE TRUNCATE ON __SCHEMA__.policy_runtime_revisions FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();

DROP TRIGGER IF EXISTS approval_decisions_append_only ON __SCHEMA__.approval_decisions;
CREATE TRIGGER approval_decisions_append_only BEFORE UPDATE OR DELETE ON __SCHEMA__.approval_decisions FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
DROP TRIGGER IF EXISTS approval_decisions_no_truncate ON __SCHEMA__.approval_decisions;
CREATE TRIGGER approval_decisions_no_truncate BEFORE TRUNCATE ON __SCHEMA__.approval_decisions FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();

DROP TRIGGER IF EXISTS approval_status_revisions_append_only ON __SCHEMA__.approval_status_revisions;
CREATE TRIGGER approval_status_revisions_append_only BEFORE UPDATE OR DELETE ON __SCHEMA__.approval_status_revisions FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
DROP TRIGGER IF EXISTS approval_status_revisions_no_truncate ON __SCHEMA__.approval_status_revisions;
CREATE TRIGGER approval_status_revisions_no_truncate BEFORE TRUNCATE ON __SCHEMA__.approval_status_revisions FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();

DROP TRIGGER IF EXISTS policy_rate_consumptions_append_only ON __SCHEMA__.policy_rate_consumptions;
CREATE TRIGGER policy_rate_consumptions_append_only BEFORE UPDATE OR DELETE ON __SCHEMA__.policy_rate_consumptions FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
DROP TRIGGER IF EXISTS policy_rate_consumptions_no_truncate ON __SCHEMA__.policy_rate_consumptions;
CREATE TRIGGER policy_rate_consumptions_no_truncate BEFORE TRUNCATE ON __SCHEMA__.policy_rate_consumptions FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();

DROP TRIGGER IF EXISTS approval_idempotency_receipts_append_only ON __SCHEMA__.approval_idempotency_receipts;
CREATE TRIGGER approval_idempotency_receipts_append_only BEFORE UPDATE OR DELETE ON __SCHEMA__.approval_idempotency_receipts FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
DROP TRIGGER IF EXISTS approval_idempotency_receipts_no_truncate ON __SCHEMA__.approval_idempotency_receipts;
CREATE TRIGGER approval_idempotency_receipts_no_truncate BEFORE TRUNCATE ON __SCHEMA__.approval_idempotency_receipts FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();

DROP TRIGGER IF EXISTS approval_outbox_append_only ON __SCHEMA__.approval_outbox;
CREATE TRIGGER approval_outbox_append_only BEFORE UPDATE OR DELETE ON __SCHEMA__.approval_outbox FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
DROP TRIGGER IF EXISTS approval_outbox_no_truncate ON __SCHEMA__.approval_outbox;
CREATE TRIGGER approval_outbox_no_truncate BEFORE TRUNCATE ON __SCHEMA__.approval_outbox FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();

DROP TRIGGER IF EXISTS approval_idempotency_quarantine_append_only ON __SCHEMA__.approval_idempotency_quarantine;
CREATE TRIGGER approval_idempotency_quarantine_append_only BEFORE UPDATE OR DELETE ON __SCHEMA__.approval_idempotency_quarantine FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
DROP TRIGGER IF EXISTS approval_idempotency_quarantine_no_truncate ON __SCHEMA__.approval_idempotency_quarantine;
CREATE TRIGGER approval_idempotency_quarantine_no_truncate BEFORE TRUNCATE ON __SCHEMA__.approval_idempotency_quarantine FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation();
