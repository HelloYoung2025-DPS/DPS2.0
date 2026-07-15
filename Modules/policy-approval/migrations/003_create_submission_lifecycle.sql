-- Upgrade hardening: the pre-fence-expiry prototype used a five-argument begin
-- overload and one generic runtime-role helper. Different PostgreSQL signatures
-- are distinct objects, so CREATE OR REPLACE cannot remove them.
DROP FUNCTION IF EXISTS __SCHEMA__.begin_approval_submission(uuid, jsonb, text, jsonb, text);
DROP FUNCTION IF EXISTS __SCHEMA__.assert_submission_runtime_role();

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_submission_attempts
(
    submission_attempt_id uuid PRIMARY KEY,
    fence_id uuid NOT NULL,
    approval_id uuid NOT NULL REFERENCES __SCHEMA__.approval_decisions(approval_id) ON DELETE RESTRICT,
    proposal_id uuid NOT NULL,
    command_id uuid NOT NULL,
    lease_id uuid NOT NULL,
    attempt integer NOT NULL CHECK (attempt BETWEEN 1 AND 3),
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    idempotency_key text NOT NULL CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    approval_sha256 text NOT NULL CHECK (approval_sha256 ~ '^[a-f0-9]{64}$'),
    proposal_sha256 text NOT NULL CHECK (proposal_sha256 ~ '^[a-f0-9]{64}$'),
    status_revision bigint NOT NULL CHECK (status_revision > 0),
    runtime_revision bigint NOT NULL CHECK (runtime_revision > 0),
    runtime_state_sha256 text NOT NULL CHECK (runtime_state_sha256 ~ '^[a-f0-9]{64}$'),
    release_bom_sha256 text NOT NULL CHECK (release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    release_bom_generation bigint NOT NULL CHECK (release_bom_generation > 0),
    execution_authorization_sha256 text NOT NULL CHECK (execution_authorization_sha256 ~ '^[a-f0-9]{64}$'),
    native_request_binding_sha256 text NOT NULL CHECK (native_request_binding_sha256 ~ '^[a-f0-9]{64}$'),
    fence_request_sha256 text NOT NULL CHECK (fence_request_sha256 ~ '^[a-f0-9]{64}$'),
    intent_sha256 text NOT NULL CHECK (intent_sha256 ~ '^[a-f0-9]{64}$'),
    intent_json jsonb NOT NULL CHECK (jsonb_typeof(intent_json) = 'object'),
    pending_state_event_id uuid NOT NULL UNIQUE,
    pending_state_sha256 text NOT NULL UNIQUE CHECK (pending_state_sha256 ~ '^[a-f0-9]{64}$'),
    pending_state_json jsonb NOT NULL CHECK (jsonb_typeof(pending_state_json) = 'object'),
    created_at timestamptz NOT NULL,
    UNIQUE (command_id, attempt),
    UNIQUE (native_request_binding_sha256),
    UNIQUE (approval_id, submission_attempt_id),
    FOREIGN KEY (approval_id, soul_id, device_binding_id, platform_account_id)
        REFERENCES __SCHEMA__.approval_decisions(approval_id, soul_id, device_binding_id, platform_account_id) ON DELETE RESTRICT,
    FOREIGN KEY (approval_id, status_revision)
        REFERENCES __SCHEMA__.approval_status_revisions(approval_id, revision) ON DELETE RESTRICT,
    FOREIGN KEY (soul_id, device_binding_id, platform_account_id, runtime_revision, runtime_state_sha256)
        REFERENCES __SCHEMA__.policy_runtime_revisions(soul_id, device_binding_id, platform_account_id, revision, state_sha256) ON DELETE RESTRICT,
    CHECK (intent_json ->> 'contract_id' = 'approval.submission.intent/v1'),
    CHECK (intent_json ->> 'producer_module' = 'executor-gateway'),
    CHECK ((intent_json ->> 'submission_attempt_id')::uuid = submission_attempt_id),
    CHECK ((intent_json ->> 'approval_id')::uuid = approval_id),
    CHECK ((intent_json ->> 'proposal_id')::uuid = proposal_id),
    CHECK ((intent_json ->> 'command_id')::uuid = command_id),
    CHECK ((intent_json ->> 'lease_id')::uuid = lease_id),
    CHECK ((intent_json ->> 'attempt')::integer = attempt),
    CHECK (intent_json ->> 'soul_id' = soul_id),
    CHECK (intent_json ->> 'device_binding_id' = device_binding_id),
    CHECK (intent_json ->> 'platform_account_id' = platform_account_id),
    CHECK (intent_json ->> 'trace_id' = trace_id),
    CHECK (intent_json ->> 'idempotency_key' = idempotency_key),
    CHECK (intent_json ->> 'native_request_binding_sha256' = native_request_binding_sha256),
    CHECK (pending_state_json ->> 'contract_id' = 'approval.submission.state/v1'),
    CHECK (pending_state_json ->> 'producer_module' = 'policy-approval'),
    CHECK (pending_state_json ->> 'state' = 'SUBMISSION_PENDING'),
    CHECK ((pending_state_json ->> 'state_event_id')::uuid = pending_state_event_id),
    CHECK ((pending_state_json ->> 'submission_attempt_id')::uuid = submission_attempt_id),
    CHECK ((pending_state_json ->> 'approval_id')::uuid = approval_id),
    CHECK ((pending_state_json ->> 'proposal_id')::uuid = proposal_id),
    CHECK ((pending_state_json ->> 'command_id')::uuid = command_id),
    CHECK ((pending_state_json ->> 'lease_id')::uuid = lease_id),
    CHECK ((pending_state_json ->> 'attempt')::integer = attempt),
    CHECK (pending_state_json ->> 'soul_id' = soul_id),
    CHECK (pending_state_json ->> 'device_binding_id' = device_binding_id),
    CHECK (pending_state_json ->> 'platform_account_id' = platform_account_id),
    CHECK (pending_state_json ->> 'trace_id' = trace_id),
    CHECK (pending_state_json ->> 'idempotency_key' = idempotency_key),
    CHECK (pending_state_json ->> 'release_bom_sha256' = release_bom_sha256),
    CHECK ((pending_state_json ->> 'release_bom_generation')::bigint = release_bom_generation),
    CHECK (pending_state_json ->> 'native_request_binding_sha256' = native_request_binding_sha256),
    CHECK (pending_state_json ->> 'submission_intent_sha256' = intent_sha256),
    CHECK (pending_state_json ->> 'state_sha256' = pending_state_sha256),
    CHECK (pending_state_json -> 'predecessor_state_sha256' = 'null'::jsonb),
    CHECK (pending_state_json ->> 'evidence_sha256' = intent_sha256)
);

CREATE INDEX IF NOT EXISTS ix_approval_submission_attempts_approval
    ON __SCHEMA__.approval_submission_attempts (approval_id, attempt DESC);
CREATE INDEX IF NOT EXISTS ix_approval_submission_attempts_scope
    ON __SCHEMA__.approval_submission_attempts (soul_id, device_binding_id, platform_account_id, command_id);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_submission_acknowledgements
(
    acknowledgement_id uuid PRIMARY KEY,
    submission_attempt_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.approval_submission_attempts(submission_attempt_id) ON DELETE RESTRICT,
    acknowledgement_sha256 text NOT NULL UNIQUE CHECK (acknowledgement_sha256 ~ '^[a-f0-9]{64}$'),
    submitted_request_sha256 text NOT NULL CHECK (submitted_request_sha256 ~ '^[a-f0-9]{64}$'),
    native_submission_id uuid NOT NULL,
    completion_handle_id uuid NOT NULL,
    native_acknowledgement_sha256 text NOT NULL CHECK (native_acknowledgement_sha256 ~ '^[a-f0-9]{64}$'),
    acknowledgement_json jsonb NOT NULL CHECK (jsonb_typeof(acknowledgement_json) = 'object'),
    state_event_id uuid NOT NULL UNIQUE,
    state_sha256 text NOT NULL UNIQUE CHECK (state_sha256 ~ '^[a-f0-9]{64}$'),
    state_json jsonb NOT NULL CHECK (jsonb_typeof(state_json) = 'object'),
    created_at timestamptz NOT NULL,
    CHECK (acknowledgement_json ->> 'contract_id' = 'approval.submission.acknowledgement/v1'),
    CHECK ((acknowledgement_json ->> 'acknowledgement_id')::uuid = acknowledgement_id),
    CHECK ((acknowledgement_json ->> 'submission_attempt_id')::uuid = submission_attempt_id),
    CHECK (acknowledgement_json ->> 'submitted_request_sha256' = submitted_request_sha256),
    CHECK ((acknowledgement_json ->> 'native_submission_id')::uuid = native_submission_id),
    CHECK ((acknowledgement_json ->> 'completion_handle_id')::uuid = completion_handle_id),
    CHECK (acknowledgement_json ->> 'native_acknowledgement_sha256' = native_acknowledgement_sha256),
    CHECK (state_json ->> 'state' = 'SUBMISSION_ACKNOWLEDGED'),
    CHECK ((state_json ->> 'state_event_id')::uuid = state_event_id),
    CHECK (state_json ->> 'state_sha256' = state_sha256),
    CHECK (state_json ->> 'evidence_sha256' = acknowledgement_sha256)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_submission_quarantines
(
    quarantine_id uuid PRIMARY KEY,
    submission_attempt_id uuid NOT NULL REFERENCES __SCHEMA__.approval_submission_attempts(submission_attempt_id) ON DELETE RESTRICT,
    reason_code text NOT NULL CHECK (reason_code IN ('NATIVE_SUBMISSION_UNCERTAIN', 'NATIVE_SUBMISSION_TIMEOUT', 'NATIVE_SUBMISSION_CANCELLED', 'NATIVE_SUBMISSION_NULL', 'NATIVE_SUBMISSION_ACK_INVALID', 'PROCESS_CRASH', 'AUTHORITY_TRANSITION_UNCERTAIN')),
    evidence_sha256 text NOT NULL CHECK (evidence_sha256 ~ '^[a-f0-9]{64}$'),
    state_event_id uuid NOT NULL UNIQUE,
    state_sha256 text NOT NULL UNIQUE CHECK (state_sha256 ~ '^[a-f0-9]{64}$'),
    state_json jsonb NOT NULL CHECK (jsonb_typeof(state_json) = 'object'),
    created_at timestamptz NOT NULL,
    UNIQUE (submission_attempt_id),
    CHECK (state_json ->> 'state' = 'UNKNOWN_SUBMISSION'),
    CHECK ((state_json ->> 'state_event_id')::uuid = state_event_id),
    CHECK ((state_json ->> 'submission_attempt_id')::uuid = submission_attempt_id),
    CHECK (state_json ->> 'state_sha256' = state_sha256),
    CHECK (state_json ->> 'evidence_sha256' = evidence_sha256)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_submission_reconciliations
(
    reconciliation_id uuid PRIMARY KEY,
    submission_attempt_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.approval_submission_attempts(submission_attempt_id) ON DELETE RESTRICT,
    finding text NOT NULL CHECK (finding IN ('CONFIRMED_NOT_SUBMITTED', 'CONFIRMED_SUBMITTED')),
    evidence_sha256 text NOT NULL CHECK (evidence_sha256 ~ '^[a-f0-9]{64}$'),
    reconciliation_sha256 text NOT NULL UNIQUE CHECK (reconciliation_sha256 ~ '^[a-f0-9]{64}$'),
    reconciliation_json jsonb NOT NULL CHECK (jsonb_typeof(reconciliation_json) = 'object'),
    state_event_id uuid NOT NULL UNIQUE,
    state_sha256 text NOT NULL UNIQUE CHECK (state_sha256 ~ '^[a-f0-9]{64}$'),
    state_json jsonb NOT NULL CHECK (jsonb_typeof(state_json) = 'object'),
    created_at timestamptz NOT NULL,
    CHECK (reconciliation_json ->> 'contract_id' = 'approval.submission.reconciliation/v1'),
    CHECK ((reconciliation_json ->> 'reconciliation_id')::uuid = reconciliation_id),
    CHECK ((reconciliation_json ->> 'submission_attempt_id')::uuid = submission_attempt_id),
    CHECK (reconciliation_json ->> 'finding' = finding),
    CHECK (reconciliation_json ->> 'evidence_sha256' = evidence_sha256),
    CHECK ((finding = 'CONFIRMED_NOT_SUBMITTED' AND state_json ->> 'state' = 'RECONCILED_NOT_SUBMITTED') OR
           (finding = 'CONFIRMED_SUBMITTED' AND state_json ->> 'state' = 'RECONCILED_SUBMITTED')),
    CHECK ((state_json ->> 'state_event_id')::uuid = state_event_id),
    CHECK (state_json ->> 'state_sha256' = state_sha256),
    CHECK (state_json ->> 'evidence_sha256' = reconciliation_sha256)
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.approval_submission_recoveries
(
    recovery_id uuid PRIMARY KEY,
    submission_attempt_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.approval_submission_attempts(submission_attempt_id) ON DELETE RESTRICT,
    reconciliation_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.approval_submission_reconciliations(reconciliation_id) ON DELETE RESTRICT,
    reconciliation_sha256 text NOT NULL CHECK (reconciliation_sha256 ~ '^[a-f0-9]{64}$'),
    next_submission_attempt_id uuid NOT NULL UNIQUE,
    next_lease_id uuid NOT NULL,
    next_attempt integer NOT NULL CHECK (next_attempt BETWEEN 2 AND 3),
    next_release_bom_sha256 text NOT NULL CHECK (next_release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    next_release_bom_generation bigint NOT NULL CHECK (next_release_bom_generation > 0),
    next_execution_authorization_sha256 text NOT NULL CHECK (next_execution_authorization_sha256 ~ '^[a-f0-9]{64}$'),
    next_native_request_binding_sha256 text NOT NULL CHECK (next_native_request_binding_sha256 ~ '^[a-f0-9]{64}$'),
    human_approval_id text NOT NULL CHECK (human_approval_id ~ '^human_[a-f0-9]{64}$'),
    recovery_sha256 text NOT NULL UNIQUE CHECK (recovery_sha256 ~ '^[a-f0-9]{64}$'),
    recovery_json jsonb NOT NULL CHECK (jsonb_typeof(recovery_json) = 'object'),
    state_event_id uuid NOT NULL UNIQUE,
    state_sha256 text NOT NULL UNIQUE CHECK (state_sha256 ~ '^[a-f0-9]{64}$'),
    state_json jsonb NOT NULL CHECK (jsonb_typeof(state_json) = 'object'),
    created_at timestamptz NOT NULL,
    CHECK (recovery_json ->> 'contract_id' = 'approval.submission.recovery/v1'),
    CHECK ((recovery_json ->> 'recovery_id')::uuid = recovery_id),
    CHECK ((recovery_json ->> 'submission_attempt_id')::uuid = submission_attempt_id),
    CHECK ((recovery_json ->> 'reconciliation_id')::uuid = reconciliation_id),
    CHECK (recovery_json ->> 'reconciliation_sha256' = reconciliation_sha256),
    CHECK ((recovery_json ->> 'next_submission_attempt_id')::uuid = next_submission_attempt_id),
    CHECK ((recovery_json ->> 'next_lease_id')::uuid = next_lease_id),
    CHECK ((recovery_json ->> 'next_attempt')::integer = next_attempt),
    CHECK (recovery_json ->> 'next_native_request_binding_sha256' = next_native_request_binding_sha256),
    CHECK (recovery_json ->> 'human_approval_id' = human_approval_id),
    CHECK (state_json ->> 'state' = 'RECOVERY_AUTHORIZED'),
    CHECK ((state_json ->> 'state_event_id')::uuid = state_event_id),
    CHECK (state_json ->> 'state_sha256' = state_sha256),
    CHECK (state_json ->> 'evidence_sha256' = recovery_sha256)
);

CREATE OR REPLACE FUNCTION __SCHEMA__.assert_exact_submission_json(
    p_value jsonb,
    p_expected_keys text[],
    p_name text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
BEGIN
    IF p_value IS NULL OR jsonb_typeof(p_value) IS DISTINCT FROM 'object' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = p_name || ' must be one exact JSON object';
    END IF;
    IF NOT (p_value ?& p_expected_keys)
       OR (SELECT count(*) FROM jsonb_object_keys(p_value)) <> cardinality(p_expected_keys) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = p_name || ' has missing or unknown fields';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.assert_submission_executor_role()
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
BEGIN
    IF session_user <> '__SUBMISSION_EXECUTOR_ROLE__' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'submission begin, acknowledgement, and quarantine require the exact executor login role';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.assert_submission_reconciliation_role()
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
BEGIN
    IF session_user <> '__SUBMISSION_RECONCILIATION_ROLE__' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'submission reconciliation requires the exact independent reconciler login role';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.assert_submission_recovery_role()
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
BEGIN
    IF session_user <> '__SUBMISSION_RECOVERY_ROLE__' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'submission recovery requires the exact human-recovery login role';
    END IF;
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.begin_approval_submission(
    p_fence_id uuid,
    p_fence_valid_until timestamptz,
    p_intent jsonb,
    p_intent_sha256 text,
    p_pending_state jsonb,
    p_pending_state_sha256 text)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    v_submission_attempt_id uuid := (p_intent ->> 'submission_attempt_id')::uuid;
    v_approval_id uuid := (p_intent ->> 'approval_id')::uuid;
    v_proposal_id uuid := (p_intent ->> 'proposal_id')::uuid;
    v_command_id uuid := (p_intent ->> 'command_id')::uuid;
    v_lease_id uuid := (p_intent ->> 'lease_id')::uuid;
    v_attempt integer := (p_intent ->> 'attempt')::integer;
    v_soul_id text := p_intent ->> 'soul_id';
    v_device_binding_id text := p_intent ->> 'device_binding_id';
    v_platform_account_id text := p_intent ->> 'platform_account_id';
    v_now timestamptz;
    v_existing_sha text;
BEGIN
    PERFORM __SCHEMA__.assert_submission_executor_role();
    PERFORM __SCHEMA__.assert_exact_submission_json(p_intent, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'auth_scope',
        'submission_attempt_id', 'fence_request_sha256', 'approval_id', 'proposal_id',
        'command_id', 'lease_id', 'attempt', 'soul_id', 'device_binding_id',
        'platform_account_id', 'trace_id', 'idempotency_key', 'approval_sha256',
        'proposal_sha256', 'status_revision', 'runtime_revision', 'runtime_state_sha256',
        'release_bom_sha256', 'release_bom_generation', 'execution_authorization_sha256',
        'native_request_binding_sha256', 'occurred_at', 'valid_until', 'privacy_class',
        'signature_base64'], 'submission intent');
    PERFORM __SCHEMA__.assert_exact_submission_json(p_pending_state, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'state_event_id',
        'submission_attempt_id', 'approval_id', 'proposal_id', 'command_id', 'lease_id',
        'attempt', 'soul_id', 'device_binding_id', 'platform_account_id', 'trace_id',
        'idempotency_key', 'release_bom_sha256', 'release_bom_generation',
        'native_request_binding_sha256', 'submission_intent_sha256', 'state',
        'predecessor_state_sha256', 'evidence_sha256', 'occurred_at', 'privacy_class',
        'state_sha256', 'signature_base64'], 'pending submission state');
    IF p_intent ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_intent ->> 'contract_id' IS DISTINCT FROM 'approval.submission.intent/v1'
       OR p_intent ->> 'producer_module' IS DISTINCT FROM 'executor-gateway'
       OR p_intent ->> 'auth_scope' IS DISTINCT FROM 'approval:submission:begin'
       OR p_pending_state ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_pending_state ->> 'contract_id' IS DISTINCT FROM 'approval.submission.state/v1'
       OR p_pending_state ->> 'producer_module' IS DISTINCT FROM 'policy-approval'
       OR p_pending_state ->> 'state' IS DISTINCT FROM 'SUBMISSION_PENDING'
       OR (p_pending_state ->> 'submission_attempt_id')::uuid IS DISTINCT FROM v_submission_attempt_id
       OR p_pending_state ->> 'submission_intent_sha256' IS DISTINCT FROM p_intent_sha256
       OR p_pending_state ->> 'evidence_sha256' IS DISTINCT FROM p_intent_sha256
       OR p_pending_state ->> 'state_sha256' IS DISTINCT FROM p_pending_state_sha256
       OR p_pending_state -> 'predecessor_state_sha256' IS DISTINCT FROM 'null'::jsonb THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'submission intent or pending state is not exact and policy-owned';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_soul_id || ':' || v_device_binding_id || ':' || v_platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(v_submission_attempt_id::text, '-', ''), 0));

    SELECT existing.intent_sha256 INTO v_existing_sha
      FROM __SCHEMA__.approval_submission_attempts AS existing
     WHERE existing.submission_attempt_id = v_submission_attempt_id;
    IF FOUND THEN
        IF v_existing_sha = p_intent_sha256 THEN RETURN 'EXISTING_UNKNOWN_SUBMISSION'; END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'submission_attempt_id is already bound to another intent';
    END IF;

    v_now := clock_timestamp();
    IF p_fence_valid_until IS NULL
       OR p_intent ->> 'valid_until' IS NULL
       OR p_intent ->> 'occurred_at' IS NULL
       OR p_pending_state ->> 'occurred_at' IS NULL
       OR p_fence_valid_until <= v_now
       OR (p_intent ->> 'valid_until')::timestamptz <= v_now
       OR (p_intent ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_intent ->> 'occurred_at')::timestamptz < v_now - interval '2 minutes'
       OR (p_pending_state ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_pending_state ->> 'occurred_at')::timestamptz < v_now - interval '5 seconds' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'fence, submission intent, or pending state expired while waiting for serialization';
    END IF;

    IF NOT EXISTS (
        SELECT 1
          FROM __SCHEMA__.approval_decisions AS decision
          JOIN __SCHEMA__.approval_status_revisions AS status ON status.approval_id = decision.approval_id
          JOIN __SCHEMA__.policy_runtime_revisions AS runtime
            ON runtime.soul_id = decision.soul_id
           AND runtime.device_binding_id = decision.device_binding_id
           AND runtime.platform_account_id = decision.platform_account_id
           AND runtime.revision = (p_intent ->> 'runtime_revision')::bigint
           AND runtime.state_sha256 = p_intent ->> 'runtime_state_sha256'
         WHERE decision.approval_id = v_approval_id
           AND decision.proposal_id = v_proposal_id
           AND decision.soul_id = v_soul_id
           AND decision.device_binding_id = v_device_binding_id
           AND decision.platform_account_id = v_platform_account_id
           AND decision.trace_id = p_intent ->> 'trace_id'
           AND decision.idempotency_key = p_intent ->> 'idempotency_key'
           AND decision.decision = 'APPROVED'
           AND decision.decision_sha256 = p_intent ->> 'approval_sha256'
           AND decision.proposal_sha256 = p_intent ->> 'proposal_sha256'
           AND decision.release_bom_sha256 = p_intent ->> 'release_bom_sha256'
           AND status.revision = (p_intent ->> 'status_revision')::bigint
           AND status.status = 'ACTIVE'
           AND status.revision = (SELECT max(latest.revision) FROM __SCHEMA__.approval_status_revisions AS latest WHERE latest.approval_id = decision.approval_id)
           AND runtime.state_status = 'ACTIVE'
           AND NOT runtime.kill_switch_enabled
           AND runtime.execution_enabled
           AND runtime.release_bom_sha256 = p_intent ->> 'release_bom_sha256'
           AND runtime.valid_until > v_now
           AND runtime.revision = (SELECT max(latest.revision) FROM __SCHEMA__.policy_runtime_revisions AS latest WHERE latest.soul_id = v_soul_id AND latest.device_binding_id = v_device_binding_id AND latest.platform_account_id = v_platform_account_id)
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'submission intent does not match the current authoritative approval/runtime/BOM scope';
    END IF;

    IF EXISTS (
        SELECT 1 FROM __SCHEMA__.approval_submission_attempts AS prior
        JOIN __SCHEMA__.approval_submission_acknowledgements AS acknowledged USING (submission_attempt_id)
        WHERE prior.approval_id = v_approval_id OR prior.command_id = v_command_id
    ) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'an acknowledged submission can never be retried';
    END IF;

    IF v_attempt = 1 THEN
        IF EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_attempts AS prior WHERE prior.approval_id = v_approval_id OR prior.command_id = v_command_id) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'an existing submission is UNKNOWN_SUBMISSION and cannot be redelivered';
        END IF;
    ELSE
        IF NOT EXISTS (
            SELECT 1
              FROM __SCHEMA__.approval_submission_attempts AS prior
              JOIN __SCHEMA__.approval_submission_recoveries AS recovery ON recovery.submission_attempt_id = prior.submission_attempt_id
             WHERE prior.approval_id = v_approval_id
               AND prior.proposal_id = v_proposal_id
               AND prior.command_id = v_command_id
               AND prior.soul_id = v_soul_id
               AND prior.device_binding_id = v_device_binding_id
               AND prior.platform_account_id = v_platform_account_id
               AND prior.trace_id = p_intent ->> 'trace_id'
               AND prior.idempotency_key = p_intent ->> 'idempotency_key'
               AND recovery.next_submission_attempt_id = v_submission_attempt_id
               AND recovery.next_lease_id = v_lease_id
               AND recovery.next_attempt = v_attempt
               AND recovery.next_release_bom_sha256 = p_intent ->> 'release_bom_sha256'
               AND recovery.next_release_bom_generation = (p_intent ->> 'release_bom_generation')::bigint
               AND recovery.next_execution_authorization_sha256 = p_intent ->> 'execution_authorization_sha256'
               AND recovery.next_native_request_binding_sha256 = p_intent ->> 'native_request_binding_sha256'
        ) THEN
            RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'a fresh attempt requires exact independent reconciliation and human recovery authorization';
        END IF;
    END IF;

    INSERT INTO __SCHEMA__.approval_submission_attempts
    (submission_attempt_id, fence_id, approval_id, proposal_id, command_id, lease_id, attempt,
     soul_id, device_binding_id, platform_account_id, trace_id, idempotency_key,
     approval_sha256, proposal_sha256, status_revision, runtime_revision, runtime_state_sha256,
     release_bom_sha256, release_bom_generation, execution_authorization_sha256,
     native_request_binding_sha256, fence_request_sha256, intent_sha256, intent_json,
     pending_state_event_id, pending_state_sha256, pending_state_json, created_at)
    VALUES
    (v_submission_attempt_id, p_fence_id, v_approval_id, v_proposal_id, v_command_id, v_lease_id, v_attempt,
     v_soul_id, v_device_binding_id, v_platform_account_id, p_intent ->> 'trace_id', p_intent ->> 'idempotency_key',
     p_intent ->> 'approval_sha256', p_intent ->> 'proposal_sha256', (p_intent ->> 'status_revision')::bigint,
     (p_intent ->> 'runtime_revision')::bigint, p_intent ->> 'runtime_state_sha256',
     p_intent ->> 'release_bom_sha256', (p_intent ->> 'release_bom_generation')::bigint,
     p_intent ->> 'execution_authorization_sha256', p_intent ->> 'native_request_binding_sha256',
     p_intent ->> 'fence_request_sha256', p_intent_sha256, p_intent,
     (p_pending_state ->> 'state_event_id')::uuid, p_pending_state_sha256, p_pending_state,
     (p_pending_state ->> 'occurred_at')::timestamptz);
    RETURN 'INSERTED';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.acknowledge_approval_submission(
    p_acknowledgement jsonb,
    p_acknowledgement_sha256 text,
    p_state jsonb,
    p_state_sha256 text)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    v_attempt_id uuid := (p_acknowledgement ->> 'submission_attempt_id')::uuid;
    v_now timestamptz;
    v_existing_sha text;
    v_pending __SCHEMA__.approval_submission_attempts%ROWTYPE;
BEGIN
    PERFORM __SCHEMA__.assert_submission_executor_role();
    PERFORM __SCHEMA__.assert_exact_submission_json(p_acknowledgement, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'auth_scope',
        'acknowledgement_id', 'submission_attempt_id', 'approval_id', 'proposal_id',
        'command_id', 'lease_id', 'attempt', 'soul_id', 'device_binding_id',
        'platform_account_id', 'trace_id', 'idempotency_key', 'release_bom_sha256',
        'release_bom_generation', 'native_request_binding_sha256',
        'submission_intent_sha256', 'pending_state_sha256', 'submitted_request_sha256',
        'native_submission_id', 'completion_handle_id', 'native_acknowledgement_sha256',
        'occurred_at', 'valid_until', 'privacy_class', 'signature_base64'],
        'submission acknowledgement');
    PERFORM __SCHEMA__.assert_exact_submission_json(p_state, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'state_event_id',
        'submission_attempt_id', 'approval_id', 'proposal_id', 'command_id', 'lease_id',
        'attempt', 'soul_id', 'device_binding_id', 'platform_account_id', 'trace_id',
        'idempotency_key', 'release_bom_sha256', 'release_bom_generation',
        'native_request_binding_sha256', 'submission_intent_sha256', 'state',
        'predecessor_state_sha256', 'evidence_sha256', 'occurred_at', 'privacy_class',
        'state_sha256', 'signature_base64'], 'acknowledged submission state');
    IF p_acknowledgement ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_acknowledgement ->> 'contract_id' IS DISTINCT FROM 'approval.submission.acknowledgement/v1'
       OR p_acknowledgement ->> 'producer_module' IS DISTINCT FROM 'executor-gateway'
       OR p_acknowledgement ->> 'auth_scope' IS DISTINCT FROM 'approval:submission:acknowledge'
       OR p_state ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_state ->> 'contract_id' IS DISTINCT FROM 'approval.submission.state/v1'
       OR p_state ->> 'producer_module' IS DISTINCT FROM 'policy-approval'
       OR p_state ->> 'state' IS DISTINCT FROM 'SUBMISSION_ACKNOWLEDGED'
       OR p_state ->> 'evidence_sha256' IS DISTINCT FROM p_acknowledgement_sha256
       OR p_state ->> 'state_sha256' IS DISTINCT FROM p_state_sha256 THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'acknowledgement or acknowledged state is invalid';
    END IF;

    SELECT pending.* INTO v_pending
      FROM __SCHEMA__.approval_submission_attempts AS pending
     WHERE pending.submission_attempt_id = v_attempt_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'acknowledgement has no durable pending submission';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_pending.soul_id || ':' || v_pending.device_binding_id || ':' || v_pending.platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_pending.approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_pending.command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(v_attempt_id::text, '-', ''), 0));
    PERFORM 1 FROM __SCHEMA__.approval_submission_attempts WHERE submission_attempt_id = v_attempt_id FOR UPDATE;

    SELECT acknowledgement_sha256 INTO v_existing_sha FROM __SCHEMA__.approval_submission_acknowledgements WHERE submission_attempt_id = v_attempt_id;
    IF FOUND THEN
        IF v_existing_sha = p_acknowledgement_sha256 THEN RETURN 'DUPLICATE_NO_OP'; END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'submission attempt already has another acknowledgement';
    END IF;
    v_now := clock_timestamp();
    IF p_acknowledgement ->> 'valid_until' IS NULL
       OR p_acknowledgement ->> 'occurred_at' IS NULL
       OR p_state ->> 'occurred_at' IS NULL
       OR (p_acknowledgement ->> 'valid_until')::timestamptz <= v_now
       OR (p_acknowledgement ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_acknowledgement ->> 'occurred_at')::timestamptz < v_now - interval '2 minutes'
       OR (p_state ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_state ->> 'occurred_at')::timestamptz < v_now - interval '5 seconds' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'acknowledgement expired while waiting for serialization';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM __SCHEMA__.approval_submission_attempts AS pending
         WHERE pending.submission_attempt_id = v_attempt_id
           AND pending.approval_id = (p_acknowledgement ->> 'approval_id')::uuid
           AND pending.proposal_id = (p_acknowledgement ->> 'proposal_id')::uuid
           AND pending.command_id = (p_acknowledgement ->> 'command_id')::uuid
           AND pending.lease_id = (p_acknowledgement ->> 'lease_id')::uuid
           AND pending.attempt = (p_acknowledgement ->> 'attempt')::integer
           AND pending.soul_id = p_acknowledgement ->> 'soul_id'
           AND pending.device_binding_id = p_acknowledgement ->> 'device_binding_id'
           AND pending.platform_account_id = p_acknowledgement ->> 'platform_account_id'
           AND pending.trace_id = p_acknowledgement ->> 'trace_id'
           AND pending.idempotency_key = p_acknowledgement ->> 'idempotency_key'
           AND pending.release_bom_sha256 = p_acknowledgement ->> 'release_bom_sha256'
           AND pending.release_bom_generation = (p_acknowledgement ->> 'release_bom_generation')::bigint
           AND pending.native_request_binding_sha256 = p_acknowledgement ->> 'native_request_binding_sha256'
           AND pending.intent_sha256 = p_acknowledgement ->> 'submission_intent_sha256'
           AND pending.pending_state_sha256 = p_acknowledgement ->> 'pending_state_sha256'
           AND p_state ->> 'predecessor_state_sha256' = COALESCE(
                (SELECT quarantine.state_sha256 FROM __SCHEMA__.approval_submission_quarantines AS quarantine WHERE quarantine.submission_attempt_id = pending.submission_attempt_id),
                pending.pending_state_sha256)
           AND p_state ->> 'submission_intent_sha256' = pending.intent_sha256
    ) OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_reconciliations WHERE submission_attempt_id = v_attempt_id)
      OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_recoveries WHERE submission_attempt_id = v_attempt_id) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'acknowledgement is not bound to the exact unrecovered pending submission';
    END IF;
    INSERT INTO __SCHEMA__.approval_submission_acknowledgements
    (acknowledgement_id, submission_attempt_id, acknowledgement_sha256, submitted_request_sha256,
     native_submission_id, completion_handle_id, native_acknowledgement_sha256, acknowledgement_json,
     state_event_id, state_sha256, state_json, created_at)
    VALUES
    ((p_acknowledgement ->> 'acknowledgement_id')::uuid, v_attempt_id, p_acknowledgement_sha256,
     p_acknowledgement ->> 'submitted_request_sha256', (p_acknowledgement ->> 'native_submission_id')::uuid,
     (p_acknowledgement ->> 'completion_handle_id')::uuid, p_acknowledgement ->> 'native_acknowledgement_sha256',
     p_acknowledgement, (p_state ->> 'state_event_id')::uuid, p_state_sha256, p_state,
     (p_state ->> 'occurred_at')::timestamptz);
    RETURN 'INSERTED';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.quarantine_approval_submission(
    p_quarantine_id uuid,
    p_submission_attempt_id uuid,
    p_reason_code text,
    p_evidence_sha256 text,
    p_state jsonb,
    p_state_sha256 text)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    v_pending __SCHEMA__.approval_submission_attempts%ROWTYPE;
    v_existing __SCHEMA__.approval_submission_quarantines%ROWTYPE;
BEGIN
    PERFORM __SCHEMA__.assert_submission_executor_role();
    PERFORM __SCHEMA__.assert_exact_submission_json(p_state, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'state_event_id',
        'submission_attempt_id', 'approval_id', 'proposal_id', 'command_id', 'lease_id',
        'attempt', 'soul_id', 'device_binding_id', 'platform_account_id', 'trace_id',
        'idempotency_key', 'release_bom_sha256', 'release_bom_generation',
        'native_request_binding_sha256', 'submission_intent_sha256', 'state',
        'predecessor_state_sha256', 'evidence_sha256', 'occurred_at', 'privacy_class',
        'state_sha256', 'signature_base64'], 'unknown submission state');
    IF p_state ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_state ->> 'contract_id' IS DISTINCT FROM 'approval.submission.state/v1'
       OR p_state ->> 'producer_module' IS DISTINCT FROM 'policy-approval'
       OR p_state ->> 'state' IS DISTINCT FROM 'UNKNOWN_SUBMISSION'
       OR (p_state ->> 'submission_attempt_id')::uuid IS DISTINCT FROM p_submission_attempt_id
       OR p_state ->> 'evidence_sha256' IS DISTINCT FROM p_evidence_sha256
       OR p_state ->> 'state_sha256' IS DISTINCT FROM p_state_sha256 THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'UNKNOWN_SUBMISSION state is not bound to the pending attempt';
    END IF;

    SELECT pending.* INTO v_pending
      FROM __SCHEMA__.approval_submission_attempts AS pending
     WHERE pending.submission_attempt_id = p_submission_attempt_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'only a durable pending submission can be quarantined';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_pending.soul_id || ':' || v_pending.device_binding_id || ':' || v_pending.platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_pending.approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_pending.command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(p_submission_attempt_id::text, '-', ''), 0));
    PERFORM 1 FROM __SCHEMA__.approval_submission_attempts WHERE submission_attempt_id = p_submission_attempt_id FOR UPDATE;

    SELECT quarantine.* INTO v_existing
      FROM __SCHEMA__.approval_submission_quarantines AS quarantine
     WHERE quarantine.submission_attempt_id = p_submission_attempt_id;
    IF FOUND THEN
        IF v_existing.quarantine_id = p_quarantine_id
           AND v_existing.reason_code = p_reason_code
           AND v_existing.evidence_sha256 = p_evidence_sha256
           AND v_existing.state_sha256 = p_state_sha256 THEN
            RETURN 'DUPLICATE_NO_OP';
        END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'submission attempt already has another uncertainty quarantine';
    END IF;
    IF EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_acknowledgements WHERE submission_attempt_id = p_submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_reconciliations WHERE submission_attempt_id = p_submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_recoveries WHERE submission_attempt_id = p_submission_attempt_id)
       OR p_state ->> 'predecessor_state_sha256' IS DISTINCT FROM v_pending.pending_state_sha256 THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'only an unresolved durable pending submission can be quarantined';
    END IF;
    INSERT INTO __SCHEMA__.approval_submission_quarantines
    (quarantine_id, submission_attempt_id, reason_code, evidence_sha256, state_event_id, state_sha256, state_json, created_at)
    VALUES (p_quarantine_id, p_submission_attempt_id, p_reason_code, p_evidence_sha256,
            (p_state ->> 'state_event_id')::uuid, p_state_sha256, p_state, (p_state ->> 'occurred_at')::timestamptz);
    RETURN 'UNKNOWN_SUBMISSION';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.reconcile_approval_submission(
    p_reconciliation jsonb,
    p_reconciliation_sha256 text,
    p_state jsonb,
    p_state_sha256 text)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    v_attempt_id uuid := (p_reconciliation ->> 'submission_attempt_id')::uuid;
    v_existing_sha text;
    v_pending __SCHEMA__.approval_submission_attempts%ROWTYPE;
    v_now timestamptz;
BEGIN
    PERFORM __SCHEMA__.assert_submission_reconciliation_role();
    PERFORM __SCHEMA__.assert_exact_submission_json(p_reconciliation, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'auth_scope', 'authority_role',
        'reconciliation_id', 'submission_attempt_id', 'approval_id', 'proposal_id',
        'command_id', 'lease_id', 'attempt', 'soul_id', 'device_binding_id',
        'platform_account_id', 'trace_id', 'idempotency_key', 'submission_intent_sha256',
        'pending_state_sha256', 'finding', 'evidence_sha256', 'occurred_at', 'valid_until',
        'privacy_class', 'signature_base64'], 'submission reconciliation');
    PERFORM __SCHEMA__.assert_exact_submission_json(p_state, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'state_event_id',
        'submission_attempt_id', 'approval_id', 'proposal_id', 'command_id', 'lease_id',
        'attempt', 'soul_id', 'device_binding_id', 'platform_account_id', 'trace_id',
        'idempotency_key', 'release_bom_sha256', 'release_bom_generation',
        'native_request_binding_sha256', 'submission_intent_sha256', 'state',
        'predecessor_state_sha256', 'evidence_sha256', 'occurred_at', 'privacy_class',
        'state_sha256', 'signature_base64'], 'reconciled submission state');
    IF p_reconciliation ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_reconciliation ->> 'contract_id' IS DISTINCT FROM 'approval.submission.reconciliation/v1'
       OR p_reconciliation ->> 'producer_module' IS DISTINCT FROM 'control-plane-host'
       OR p_reconciliation ->> 'auth_scope' IS DISTINCT FROM 'approval:submission:reconcile'
       OR p_reconciliation ->> 'authority_role' IS DISTINCT FROM 'independent-reconciler'
       OR p_state ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_state ->> 'contract_id' IS DISTINCT FROM 'approval.submission.state/v1'
       OR p_state ->> 'producer_module' IS DISTINCT FROM 'policy-approval'
       OR p_state ->> 'evidence_sha256' IS DISTINCT FROM p_reconciliation_sha256
       OR p_state ->> 'state_sha256' IS DISTINCT FROM p_state_sha256
       OR (p_reconciliation ->> 'finding' IS DISTINCT FROM 'CONFIRMED_NOT_SUBMITTED'
           AND p_reconciliation ->> 'finding' IS DISTINCT FROM 'CONFIRMED_SUBMITTED')
       OR (p_reconciliation ->> 'finding' = 'CONFIRMED_NOT_SUBMITTED'
           AND p_state ->> 'state' IS DISTINCT FROM 'RECONCILED_NOT_SUBMITTED')
       OR (p_reconciliation ->> 'finding' = 'CONFIRMED_SUBMITTED'
           AND p_state ->> 'state' IS DISTINCT FROM 'RECONCILED_SUBMITTED') THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'reconciliation receipt or state is invalid or expired';
    END IF;

    SELECT pending.* INTO v_pending
      FROM __SCHEMA__.approval_submission_attempts AS pending
     WHERE pending.submission_attempt_id = v_attempt_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'reconciliation has no durable pending submission';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_pending.soul_id || ':' || v_pending.device_binding_id || ':' || v_pending.platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_pending.approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_pending.command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(v_attempt_id::text, '-', ''), 0));
    PERFORM 1 FROM __SCHEMA__.approval_submission_attempts WHERE submission_attempt_id = v_attempt_id FOR UPDATE;

    SELECT reconciliation_sha256 INTO v_existing_sha FROM __SCHEMA__.approval_submission_reconciliations WHERE submission_attempt_id = v_attempt_id;
    IF FOUND THEN
        IF v_existing_sha = p_reconciliation_sha256 THEN RETURN 'DUPLICATE_NO_OP'; END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'submission attempt already has another reconciliation';
    END IF;
    v_now := clock_timestamp();
    IF p_reconciliation ->> 'valid_until' IS NULL
       OR p_reconciliation ->> 'occurred_at' IS NULL
       OR p_state ->> 'occurred_at' IS NULL
       OR (p_reconciliation ->> 'valid_until')::timestamptz <= v_now
       OR (p_reconciliation ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_reconciliation ->> 'occurred_at')::timestamptz < v_now - interval '5 minutes'
       OR (p_state ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_state ->> 'occurred_at')::timestamptz < v_now - interval '5 seconds' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'reconciliation expired while waiting for serialization';
    END IF;
    IF EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_acknowledgements WHERE submission_attempt_id = v_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_recoveries WHERE submission_attempt_id = v_attempt_id)
       OR NOT EXISTS (
           SELECT 1 FROM __SCHEMA__.approval_submission_attempts AS pending
            WHERE pending.submission_attempt_id = v_attempt_id
              AND pending.approval_id = (p_reconciliation ->> 'approval_id')::uuid
              AND pending.proposal_id = (p_reconciliation ->> 'proposal_id')::uuid
              AND pending.command_id = (p_reconciliation ->> 'command_id')::uuid
              AND pending.lease_id = (p_reconciliation ->> 'lease_id')::uuid
              AND pending.attempt = (p_reconciliation ->> 'attempt')::integer
              AND pending.soul_id = p_reconciliation ->> 'soul_id'
              AND pending.device_binding_id = p_reconciliation ->> 'device_binding_id'
              AND pending.platform_account_id = p_reconciliation ->> 'platform_account_id'
              AND pending.trace_id = p_reconciliation ->> 'trace_id'
              AND pending.idempotency_key = p_reconciliation ->> 'idempotency_key'
              AND pending.intent_sha256 = p_reconciliation ->> 'submission_intent_sha256'
              AND pending.pending_state_sha256 = p_reconciliation ->> 'pending_state_sha256'
              AND p_state ->> 'predecessor_state_sha256' = COALESCE(
                    (SELECT quarantine.state_sha256 FROM __SCHEMA__.approval_submission_quarantines AS quarantine WHERE quarantine.submission_attempt_id = pending.submission_attempt_id),
                    pending.pending_state_sha256))
    THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'reconciliation is not bound to the exact pending submission';
    END IF;
    INSERT INTO __SCHEMA__.approval_submission_reconciliations
    (reconciliation_id, submission_attempt_id, finding, evidence_sha256, reconciliation_sha256,
     reconciliation_json, state_event_id, state_sha256, state_json, created_at)
    VALUES
    ((p_reconciliation ->> 'reconciliation_id')::uuid, v_attempt_id, p_reconciliation ->> 'finding',
     p_reconciliation ->> 'evidence_sha256', p_reconciliation_sha256, p_reconciliation,
     (p_state ->> 'state_event_id')::uuid, p_state_sha256, p_state, (p_state ->> 'occurred_at')::timestamptz);
    RETURN 'INSERTED';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.recover_approval_submission(
    p_recovery jsonb,
    p_recovery_sha256 text,
    p_state jsonb,
    p_state_sha256 text)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    v_attempt_id uuid := (p_recovery ->> 'submission_attempt_id')::uuid;
    v_existing_sha text;
    v_pending __SCHEMA__.approval_submission_attempts%ROWTYPE;
    v_now timestamptz;
BEGIN
    PERFORM __SCHEMA__.assert_submission_recovery_role();
    PERFORM __SCHEMA__.assert_exact_submission_json(p_recovery, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'auth_scope', 'authority_role',
        'recovery_id', 'submission_attempt_id', 'reconciliation_id', 'reconciliation_sha256',
        'approval_id', 'proposal_id', 'command_id', 'previous_lease_id', 'previous_attempt',
        'next_submission_attempt_id', 'next_lease_id', 'next_attempt', 'soul_id',
        'device_binding_id', 'platform_account_id', 'trace_id', 'idempotency_key',
        'next_release_bom_sha256', 'next_release_bom_generation',
        'next_execution_authorization_sha256', 'next_native_request_binding_sha256',
        'human_approval_id', 'occurred_at', 'valid_until', 'privacy_class', 'signature_base64'],
        'submission recovery authorization');
    PERFORM __SCHEMA__.assert_exact_submission_json(p_state, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'state_event_id',
        'submission_attempt_id', 'approval_id', 'proposal_id', 'command_id', 'lease_id',
        'attempt', 'soul_id', 'device_binding_id', 'platform_account_id', 'trace_id',
        'idempotency_key', 'release_bom_sha256', 'release_bom_generation',
        'native_request_binding_sha256', 'submission_intent_sha256', 'state',
        'predecessor_state_sha256', 'evidence_sha256', 'occurred_at', 'privacy_class',
        'state_sha256', 'signature_base64'], 'recovery-authorized submission state');
    IF p_recovery ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_recovery ->> 'contract_id' IS DISTINCT FROM 'approval.submission.recovery/v1'
       OR p_recovery ->> 'producer_module' IS DISTINCT FROM 'control-plane-host'
       OR p_recovery ->> 'auth_scope' IS DISTINCT FROM 'approval:submission:recover'
       OR p_recovery ->> 'authority_role' IS DISTINCT FROM 'human-release-approver'
       OR p_state ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR p_state ->> 'contract_id' IS DISTINCT FROM 'approval.submission.state/v1'
       OR p_state ->> 'producer_module' IS DISTINCT FROM 'policy-approval'
       OR p_state ->> 'state' IS DISTINCT FROM 'RECOVERY_AUTHORIZED'
       OR p_state ->> 'evidence_sha256' IS DISTINCT FROM p_recovery_sha256
       OR p_state ->> 'state_sha256' IS DISTINCT FROM p_state_sha256 THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'recovery authorization or state is invalid or expired';
    END IF;

    SELECT pending.* INTO v_pending
      FROM __SCHEMA__.approval_submission_attempts AS pending
     WHERE pending.submission_attempt_id = v_attempt_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'recovery has no durable pending submission';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_pending.soul_id || ':' || v_pending.device_binding_id || ':' || v_pending.platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_pending.approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_pending.command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(v_attempt_id::text, '-', ''), 0));
    PERFORM 1 FROM __SCHEMA__.approval_submission_attempts WHERE submission_attempt_id = v_attempt_id FOR UPDATE;

    SELECT recovery_sha256 INTO v_existing_sha FROM __SCHEMA__.approval_submission_recoveries WHERE submission_attempt_id = v_attempt_id;
    IF FOUND THEN
        IF v_existing_sha = p_recovery_sha256 THEN RETURN 'DUPLICATE_NO_OP'; END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'submission attempt already has another recovery authorization';
    END IF;
    v_now := clock_timestamp();
    IF p_recovery ->> 'valid_until' IS NULL
       OR p_recovery ->> 'occurred_at' IS NULL
       OR p_state ->> 'occurred_at' IS NULL
       OR (p_recovery ->> 'valid_until')::timestamptz <= v_now
       OR (p_recovery ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_recovery ->> 'occurred_at')::timestamptz < v_now - interval '5 minutes'
       OR (p_state ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (p_state ->> 'occurred_at')::timestamptz < v_now - interval '5 seconds' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'recovery authorization expired while waiting for serialization';
    END IF;
    IF EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_acknowledgements WHERE submission_attempt_id = v_attempt_id)
       OR NOT EXISTS (
           SELECT 1
             FROM __SCHEMA__.approval_submission_attempts AS pending
             JOIN __SCHEMA__.approval_submission_reconciliations AS reconciliation ON reconciliation.submission_attempt_id = pending.submission_attempt_id
            WHERE pending.submission_attempt_id = v_attempt_id
              AND reconciliation.reconciliation_id = (p_recovery ->> 'reconciliation_id')::uuid
              AND reconciliation.reconciliation_sha256 = p_recovery ->> 'reconciliation_sha256'
              AND reconciliation.finding = 'CONFIRMED_NOT_SUBMITTED'
              AND pending.approval_id = (p_recovery ->> 'approval_id')::uuid
              AND pending.proposal_id = (p_recovery ->> 'proposal_id')::uuid
              AND pending.command_id = (p_recovery ->> 'command_id')::uuid
              AND pending.lease_id = (p_recovery ->> 'previous_lease_id')::uuid
              AND pending.attempt = (p_recovery ->> 'previous_attempt')::integer
              AND pending.soul_id = p_recovery ->> 'soul_id'
              AND pending.device_binding_id = p_recovery ->> 'device_binding_id'
              AND pending.platform_account_id = p_recovery ->> 'platform_account_id'
              AND pending.trace_id = p_recovery ->> 'trace_id'
              AND pending.idempotency_key = p_recovery ->> 'idempotency_key'
              AND (p_recovery ->> 'next_submission_attempt_id')::uuid <> pending.submission_attempt_id
              AND (p_recovery ->> 'next_lease_id')::uuid <> pending.lease_id
              AND (p_recovery ->> 'next_attempt')::integer = pending.attempt + 1
              AND p_state ->> 'predecessor_state_sha256' = reconciliation.state_sha256)
    THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'recovery requires exact CONFIRMED_NOT_SUBMITTED evidence for the old attempt';
    END IF;
    INSERT INTO __SCHEMA__.approval_submission_recoveries
    (recovery_id, submission_attempt_id, reconciliation_id, reconciliation_sha256,
     next_submission_attempt_id, next_lease_id, next_attempt, next_release_bom_sha256,
     next_release_bom_generation, next_execution_authorization_sha256,
     next_native_request_binding_sha256, human_approval_id, recovery_sha256, recovery_json,
     state_event_id, state_sha256, state_json, created_at)
    VALUES
    ((p_recovery ->> 'recovery_id')::uuid, v_attempt_id, (p_recovery ->> 'reconciliation_id')::uuid,
     p_recovery ->> 'reconciliation_sha256', (p_recovery ->> 'next_submission_attempt_id')::uuid,
     (p_recovery ->> 'next_lease_id')::uuid, (p_recovery ->> 'next_attempt')::integer,
     p_recovery ->> 'next_release_bom_sha256', (p_recovery ->> 'next_release_bom_generation')::bigint,
     p_recovery ->> 'next_execution_authorization_sha256', p_recovery ->> 'next_native_request_binding_sha256',
     p_recovery ->> 'human_approval_id', p_recovery_sha256, p_recovery,
     (p_state ->> 'state_event_id')::uuid, p_state_sha256, p_state, (p_state ->> 'occurred_at')::timestamptz);
    RETURN 'INSERTED';
END;
$function$;

DO $triggers$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'approval_submission_attempts', 'approval_submission_acknowledgements',
        'approval_submission_quarantines', 'approval_submission_reconciliations',
        'approval_submission_recoveries'
    ] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I', table_name || '_append_only', table_name);
        EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON __SCHEMA__.%I FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation()', table_name || '_append_only', table_name);
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I', table_name || '_no_truncate', table_name);
        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON __SCHEMA__.%I FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation()', table_name || '_no_truncate', table_name);
    END LOOP;
END;
$triggers$;

REVOKE ALL ON __SCHEMA__.approval_submission_attempts FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON __SCHEMA__.approval_submission_acknowledgements FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON __SCHEMA__.approval_submission_quarantines FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON __SCHEMA__.approval_submission_reconciliations FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON __SCHEMA__.approval_submission_recoveries FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;

GRANT USAGE ON SCHEMA __SCHEMA__ TO __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
GRANT SELECT ON __SCHEMA__.policy_runtime_revisions, __SCHEMA__.approval_decisions, __SCHEMA__.approval_status_revisions TO __SUBMISSION_EXECUTOR_ROLE__;
GRANT SELECT ON __SCHEMA__.approval_submission_attempts, __SCHEMA__.approval_submission_acknowledgements,
    __SCHEMA__.approval_submission_quarantines, __SCHEMA__.approval_submission_reconciliations,
    __SCHEMA__.approval_submission_recoveries
    TO __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;

REVOKE ALL ON FUNCTION __SCHEMA__.assert_submission_executor_role() FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.assert_submission_reconciliation_role() FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.assert_submission_recovery_role() FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.assert_exact_submission_json(jsonb, text[], text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.begin_approval_submission(uuid, timestamptz, jsonb, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.acknowledge_approval_submission(jsonb, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.quarantine_approval_submission(uuid, uuid, text, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.reconcile_approval_submission(jsonb, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.recover_approval_submission(jsonb, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.begin_approval_submission(uuid, timestamptz, jsonb, text, jsonb, text) TO __SUBMISSION_EXECUTOR_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.acknowledge_approval_submission(jsonb, text, jsonb, text) TO __SUBMISSION_EXECUTOR_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.quarantine_approval_submission(uuid, uuid, text, text, jsonb, text) TO __SUBMISSION_EXECUTOR_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.reconcile_approval_submission(jsonb, text, jsonb, text) TO __SUBMISSION_RECONCILIATION_ROLE__;
GRANT EXECUTE ON FUNCTION __SCHEMA__.recover_approval_submission(jsonb, text, jsonb, text) TO __SUBMISSION_RECOVERY_ROLE__;
