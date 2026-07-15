-- Additive native-stop v2 challenge ledger. Runtime remains WAITING_EXTERNAL
-- until the sealed two-phase Executor Gateway protocol is composed.

CREATE TABLE IF NOT EXISTS __SCHEMA__.native_stop_challenge_issues
(
    challenge_id uuid PRIMARY KEY,
    submission_attempt_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.approval_submission_attempts(submission_attempt_id) ON DELETE RESTRICT,
    command_id uuid NOT NULL,
    lease_id uuid NOT NULL,
    attempt integer NOT NULL CHECK (attempt BETWEEN 1 AND 3),
    native_request_binding_sha256 text NOT NULL CHECK (native_request_binding_sha256 ~ '^[0-9a-f]{64}$'),
    pending_state_sha256 text NOT NULL CHECK (pending_state_sha256 ~ '^[0-9a-f]{64}$'),
    submitted_request_sha256 text NOT NULL CHECK (submitted_request_sha256 ~ '^[0-9a-f]{64}$'),
    challenge_nonce_sha256 text NOT NULL UNIQUE CHECK (challenge_nonce_sha256 ~ '^[0-9a-f]{64}$'),
    native_abort_challenge_sha256 text NOT NULL UNIQUE CHECK (native_abort_challenge_sha256 ~ '^[0-9a-f]{64}$'),
    challenge_wire_sha256 text NOT NULL UNIQUE CHECK (challenge_wire_sha256 ~ '^[0-9a-f]{64}$'),
    challenge_wire bytea NOT NULL CHECK (octet_length(challenge_wire) BETWEEN 1 AND 16384),
    challenge_json jsonb NOT NULL CHECK (jsonb_typeof(challenge_json) = 'object'),
    valid_until timestamptz NOT NULL,
    issued_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS __SCHEMA__.native_stop_challenge_consumptions
(
    consumption_id uuid PRIMARY KEY,
    challenge_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.native_stop_challenge_issues(challenge_id) ON DELETE RESTRICT,
    submission_attempt_id uuid NOT NULL UNIQUE REFERENCES __SCHEMA__.approval_submission_attempts(submission_attempt_id) ON DELETE RESTRICT,
    terminal_kind text NOT NULL CHECK (terminal_kind IN ('ACK', 'UNKNOWN')),
    terminal_evidence_sha256 text NOT NULL CHECK (terminal_evidence_sha256 ~ '^[0-9a-f]{64}$'),
    native_stop_proof_wire_sha256 text NULL CHECK (native_stop_proof_wire_sha256 IS NULL OR native_stop_proof_wire_sha256 ~ '^[0-9a-f]{64}$'),
    native_stop_proof_evidence_sha256 text NULL CHECK (native_stop_proof_evidence_sha256 IS NULL OR native_stop_proof_evidence_sha256 ~ '^[0-9a-f]{64}$'),
    native_stop_proof_wire bytea NULL CHECK (native_stop_proof_wire IS NULL OR octet_length(native_stop_proof_wire) BETWEEN 1 AND 32768),
    consumed_at timestamptz NOT NULL,
    CHECK ((terminal_kind = 'ACK' AND native_stop_proof_wire_sha256 IS NULL AND native_stop_proof_evidence_sha256 IS NULL AND native_stop_proof_wire IS NULL)
        OR (terminal_kind = 'UNKNOWN' AND native_stop_proof_wire_sha256 IS NOT NULL AND native_stop_proof_evidence_sha256 IS NOT NULL AND native_stop_proof_wire IS NOT NULL))
);

CREATE OR REPLACE FUNCTION __SCHEMA__.issue_native_stop_challenge(
    p_challenge_id uuid,
    p_submission_attempt_id uuid,
    p_challenge_wire bytea,
    p_challenge_wire_sha256 text,
    p_native_abort_challenge_sha256 text,
    p_submitted_request_sha256 text,
    p_challenge_nonce_sha256 text,
    p_valid_until timestamptz)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    v_pending __SCHEMA__.approval_submission_attempts%ROWTYPE;
    v_existing __SCHEMA__.native_stop_challenge_issues%ROWTYPE;
    v_challenge jsonb;
    v_now timestamptz;
BEGIN
    PERFORM __SCHEMA__.assert_submission_executor_role();
    IF p_challenge_id IS NULL OR p_submission_attempt_id IS NULL
       OR p_challenge_wire IS NULL OR octet_length(p_challenge_wire) NOT BETWEEN 1 AND 16384
       OR p_challenge_wire_sha256 !~ '^[0-9a-f]{64}$'
       OR p_native_abort_challenge_sha256 !~ '^[0-9a-f]{64}$'
       OR p_submitted_request_sha256 !~ '^[0-9a-f]{64}$'
       OR p_challenge_nonce_sha256 !~ '^[0-9a-f]{64}$'
       OR p_valid_until IS NULL THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'native stop challenge issue parameters are not canonical';
    END IF;
    BEGIN
        v_challenge := convert_from(p_challenge_wire, 'UTF8')::jsonb;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'native stop challenge wire is not one UTF-8 JSON object';
    END;
    PERFORM __SCHEMA__.assert_exact_submission_json(v_challenge, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'auth_scope', 'challenge_id',
        'submission_attempt_id', 'command_id', 'lease_id', 'attempt',
        'native_request_binding_sha256', 'pending_state_sha256', 'submitted_request_sha256',
        'soul_id', 'device_binding_id', 'platform_account_id', 'trace_id', 'idempotency_key',
        'active_release_bom_sha256', 'release_bom_generation', 'activation_token_sha256',
        'authority_id', 'policy_module_id', 'policy_artifact_id', 'policy_artifact_sha256',
        'policy_version', 'policy_instance_id', 'policy_generation', 'key_id', 'p256_spki_sha256',
        'signature_algorithm', 'signature_format', 'policy_id', 'rotation_epoch',
        'authority_valid_from', 'authority_valid_until', 'authority_revoked',
        'challenge_authority_sha256', 'worker_authority_sha256', 'routing_epoch',
        'route_assignment_sha256', 'submission_pending_occurred_at', 'occurred_at',
        'policy_abort_not_before_at', 'challenge_nonce_sha256', 'valid_until', 'privacy_class',
        'native_abort_challenge_sha256', 'signature_base64'], 'native stop challenge');
    IF v_challenge ->> 'schema_version' IS DISTINCT FROM '1.0.0'
       OR v_challenge ->> 'contract_id' IS DISTINCT FROM 'native.stop.challenge/v1'
       OR v_challenge ->> 'producer_module' IS DISTINCT FROM 'policy-approval'
       OR v_challenge ->> 'auth_scope' IS DISTINCT FROM 'policy-approval:native-stop-challenge:v1:issue'
       OR v_challenge ->> 'signature_algorithm' IS DISTINCT FROM 'ECDSA_P256_SHA256'
       OR v_challenge ->> 'signature_format' IS DISTINCT FROM 'IEEE_P1363_FIXED_FIELD_LOW_S'
       OR v_challenge ->> 'policy_id' IS DISTINCT FROM 'NATIVE-STOP-CHALLENGE-001'
       OR v_challenge ->> 'authority_revoked' IS DISTINCT FROM 'false'
       OR (v_challenge ->> 'challenge_id')::uuid IS DISTINCT FROM p_challenge_id
       OR (v_challenge ->> 'submission_attempt_id')::uuid IS DISTINCT FROM p_submission_attempt_id
       OR v_challenge ->> 'submitted_request_sha256' IS DISTINCT FROM p_submitted_request_sha256
       OR v_challenge ->> 'challenge_nonce_sha256' IS DISTINCT FROM p_challenge_nonce_sha256
       OR v_challenge ->> 'native_abort_challenge_sha256' IS DISTINCT FROM p_native_abort_challenge_sha256
       OR (v_challenge ->> 'valid_until')::timestamptz IS DISTINCT FROM p_valid_until THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'native stop challenge wire and issue commitments differ';
    END IF;

    SELECT pending.* INTO v_pending
      FROM __SCHEMA__.approval_submission_attempts AS pending
     WHERE pending.submission_attempt_id = p_submission_attempt_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'native stop challenge requires one durable pending submission';
    END IF;
    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_pending.soul_id || ':' || v_pending.device_binding_id || ':' || v_pending.platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_pending.approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_pending.command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(p_submission_attempt_id::text, '-', ''), 0));
    PERFORM 1 FROM __SCHEMA__.approval_submission_attempts WHERE submission_attempt_id = p_submission_attempt_id FOR UPDATE;

    SELECT issue.* INTO v_existing
      FROM __SCHEMA__.native_stop_challenge_issues AS issue
     WHERE issue.submission_attempt_id = p_submission_attempt_id;
    IF FOUND THEN
        IF v_existing.challenge_id = p_challenge_id
           AND v_existing.challenge_wire_sha256 = p_challenge_wire_sha256
           AND v_existing.native_abort_challenge_sha256 = p_native_abort_challenge_sha256
           AND v_existing.submitted_request_sha256 = p_submitted_request_sha256
           AND v_existing.challenge_nonce_sha256 = p_challenge_nonce_sha256
           AND v_existing.challenge_wire = p_challenge_wire THEN
            RETURN 'DUPLICATE_NO_OP';
        END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'submission attempt already has another native stop challenge';
    END IF;

    v_now := clock_timestamp();
    IF p_valid_until <= v_now
       OR (v_challenge ->> 'occurred_at')::timestamptz > v_now + interval '1 second'
       OR (v_challenge ->> 'policy_abort_not_before_at')::timestamptz < (v_challenge ->> 'occurred_at')::timestamptz
       OR (v_challenge ->> 'authority_valid_from')::timestamptz > v_now
       OR (v_challenge ->> 'authority_valid_until')::timestamptz <= v_now
       OR (v_challenge ->> 'authority_valid_until')::timestamptz < p_valid_until
       OR (v_challenge ->> 'command_id')::uuid IS DISTINCT FROM v_pending.command_id
       OR (v_challenge ->> 'lease_id')::uuid IS DISTINCT FROM v_pending.lease_id
       OR (v_challenge ->> 'attempt')::integer IS DISTINCT FROM v_pending.attempt
       OR v_challenge ->> 'native_request_binding_sha256' IS DISTINCT FROM v_pending.native_request_binding_sha256
       OR v_challenge ->> 'pending_state_sha256' IS DISTINCT FROM v_pending.pending_state_sha256
       OR v_challenge ->> 'soul_id' IS DISTINCT FROM v_pending.soul_id
       OR v_challenge ->> 'device_binding_id' IS DISTINCT FROM v_pending.device_binding_id
       OR v_challenge ->> 'platform_account_id' IS DISTINCT FROM v_pending.platform_account_id
       OR v_challenge ->> 'trace_id' IS DISTINCT FROM v_pending.trace_id
       OR v_challenge ->> 'idempotency_key' IS DISTINCT FROM v_pending.idempotency_key
       OR v_challenge ->> 'active_release_bom_sha256' IS DISTINCT FROM v_pending.release_bom_sha256
       OR (v_challenge ->> 'release_bom_generation')::bigint IS DISTINCT FROM v_pending.release_bom_generation
       OR (v_challenge ->> 'submission_pending_occurred_at')::timestamptz IS DISTINCT FROM v_pending.created_at
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_acknowledgements WHERE submission_attempt_id = p_submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_quarantines WHERE submission_attempt_id = p_submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_reconciliations WHERE submission_attempt_id = p_submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_recoveries WHERE submission_attempt_id = p_submission_attempt_id) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'native stop challenge is stale or not bound to the exact unresolved pending submission';
    END IF;

    INSERT INTO __SCHEMA__.native_stop_challenge_issues
    (challenge_id, submission_attempt_id, command_id, lease_id, attempt,
     native_request_binding_sha256, pending_state_sha256, submitted_request_sha256,
     challenge_nonce_sha256, native_abort_challenge_sha256, challenge_wire_sha256,
     challenge_wire, challenge_json, valid_until, issued_at)
    VALUES
    (p_challenge_id, p_submission_attempt_id, v_pending.command_id, v_pending.lease_id, v_pending.attempt,
     v_pending.native_request_binding_sha256, v_pending.pending_state_sha256, p_submitted_request_sha256,
     p_challenge_nonce_sha256, p_native_abort_challenge_sha256, p_challenge_wire_sha256,
     p_challenge_wire, v_challenge, p_valid_until, v_now);
    RETURN 'ISSUED';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.consume_native_stop_challenge_ack(
    p_consumption_id uuid,
    p_challenge_id uuid,
    p_challenge_wire_sha256 text,
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
    v_issue __SCHEMA__.native_stop_challenge_issues%ROWTYPE;
    v_pending __SCHEMA__.approval_submission_attempts%ROWTYPE;
    v_existing __SCHEMA__.native_stop_challenge_consumptions%ROWTYPE;
    v_result text;
BEGIN
    PERFORM __SCHEMA__.assert_submission_executor_role();
    SELECT issue.* INTO v_issue FROM __SCHEMA__.native_stop_challenge_issues AS issue WHERE issue.challenge_id = p_challenge_id;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'ACK has no issued native stop challenge'; END IF;
    SELECT pending.* INTO STRICT v_pending FROM __SCHEMA__.approval_submission_attempts AS pending WHERE pending.submission_attempt_id = v_issue.submission_attempt_id;
    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_pending.soul_id || ':' || v_pending.device_binding_id || ':' || v_pending.platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_pending.approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_pending.command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(v_issue.submission_attempt_id::text, '-', ''), 0));
    PERFORM 1 FROM __SCHEMA__.native_stop_challenge_issues WHERE challenge_id = p_challenge_id FOR UPDATE;
    SELECT consumption.* INTO v_existing FROM __SCHEMA__.native_stop_challenge_consumptions AS consumption WHERE consumption.challenge_id = p_challenge_id;
    IF FOUND THEN
        IF v_existing.consumption_id = p_consumption_id AND v_existing.terminal_kind = 'ACK'
           AND v_existing.terminal_evidence_sha256 = p_acknowledgement_sha256
           AND v_issue.challenge_wire_sha256 = p_challenge_wire_sha256 THEN RETURN 'DUPLICATE_NO_OP'; END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'native stop challenge is already consumed by another terminal';
    END IF;
    IF clock_timestamp() >= v_issue.valid_until
       OR v_issue.challenge_wire_sha256 IS DISTINCT FROM p_challenge_wire_sha256
       OR (p_acknowledgement ->> 'submission_attempt_id')::uuid IS DISTINCT FROM v_issue.submission_attempt_id
       OR p_acknowledgement ->> 'submitted_request_sha256' IS DISTINCT FROM v_issue.submitted_request_sha256
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_acknowledgements WHERE submission_attempt_id = v_issue.submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_quarantines WHERE submission_attempt_id = v_issue.submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_reconciliations WHERE submission_attempt_id = v_issue.submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_recoveries WHERE submission_attempt_id = v_issue.submission_attempt_id) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'ACK cannot consume this exact current native stop challenge';
    END IF;
    v_result := __SCHEMA__.acknowledge_approval_submission(p_acknowledgement, p_acknowledgement_sha256, p_state, p_state_sha256);
    IF v_result IS DISTINCT FROM 'INSERTED' THEN RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'atomic ACK expected one new lifecycle terminal'; END IF;
    INSERT INTO __SCHEMA__.native_stop_challenge_consumptions
    (consumption_id, challenge_id, submission_attempt_id, terminal_kind, terminal_evidence_sha256, consumed_at)
    VALUES (p_consumption_id, p_challenge_id, v_issue.submission_attempt_id, 'ACK', p_acknowledgement_sha256, clock_timestamp());
    RETURN 'ACK';
END;
$function$;

CREATE OR REPLACE FUNCTION __SCHEMA__.consume_native_stop_challenge_unknown(
    p_consumption_id uuid,
    p_quarantine_id uuid,
    p_challenge_id uuid,
    p_challenge_wire_sha256 text,
    p_native_stop_proof_wire bytea,
    p_native_stop_proof_wire_sha256 text,
    p_native_stop_proof_evidence_sha256 text,
    p_state jsonb,
    p_state_sha256 text)
RETURNS text
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, __SCHEMA__
AS $function$
DECLARE
    v_issue __SCHEMA__.native_stop_challenge_issues%ROWTYPE;
    v_pending __SCHEMA__.approval_submission_attempts%ROWTYPE;
    v_existing __SCHEMA__.native_stop_challenge_consumptions%ROWTYPE;
    v_proof jsonb;
    v_result text;
BEGIN
    PERFORM __SCHEMA__.assert_submission_executor_role();
    IF p_native_stop_proof_wire IS NULL OR octet_length(p_native_stop_proof_wire) NOT BETWEEN 1 AND 32768
       OR p_native_stop_proof_wire_sha256 !~ '^[0-9a-f]{64}$'
       OR p_native_stop_proof_evidence_sha256 !~ '^[0-9a-f]{64}$' THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'native stop proof wire commitments are not canonical';
    END IF;
    BEGIN
        v_proof := convert_from(p_native_stop_proof_wire, 'UTF8')::jsonb;
    EXCEPTION WHEN OTHERS THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'native stop proof wire is not one UTF-8 JSON object';
    END;
    SELECT issue.* INTO v_issue FROM __SCHEMA__.native_stop_challenge_issues AS issue WHERE issue.challenge_id = p_challenge_id;
    IF NOT FOUND THEN RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'UNKNOWN has no issued native stop challenge'; END IF;
    SELECT pending.* INTO STRICT v_pending FROM __SCHEMA__.approval_submission_attempts AS pending WHERE pending.submission_attempt_id = v_issue.submission_attempt_id;
    PERFORM pg_advisory_xact_lock(hashtextextended('policy-runtime:' || v_pending.soul_id || ':' || v_pending.device_binding_id || ':' || v_pending.platform_account_id, 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('approval:' || replace(v_pending.approval_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-command:' || replace(v_pending.command_id::text, '-', ''), 0));
    PERFORM pg_advisory_xact_lock(hashtextextended('submission-attempt:' || replace(v_issue.submission_attempt_id::text, '-', ''), 0));
    PERFORM 1 FROM __SCHEMA__.native_stop_challenge_issues WHERE challenge_id = p_challenge_id FOR UPDATE;
    SELECT consumption.* INTO v_existing FROM __SCHEMA__.native_stop_challenge_consumptions AS consumption WHERE consumption.challenge_id = p_challenge_id;
    IF FOUND THEN
        IF v_existing.consumption_id = p_consumption_id AND v_existing.terminal_kind = 'UNKNOWN'
           AND v_existing.native_stop_proof_wire_sha256 = p_native_stop_proof_wire_sha256
           AND v_existing.native_stop_proof_evidence_sha256 = p_native_stop_proof_evidence_sha256
           AND v_existing.native_stop_proof_wire = p_native_stop_proof_wire
           AND v_issue.challenge_wire_sha256 = p_challenge_wire_sha256 THEN RETURN 'DUPLICATE_NO_OP'; END IF;
        RAISE EXCEPTION USING ERRCODE = '23505', MESSAGE = 'native stop challenge is already consumed by another terminal';
    END IF;
    PERFORM __SCHEMA__.assert_exact_submission_json(v_proof, ARRAY[
        'schema_version', 'contract_id', 'producer_module', 'stopped', 'submission_attempt_id',
        'command_id', 'lease_id', 'attempt', 'native_request_binding_sha256', 'pending_state_sha256',
        'submitted_request_sha256', 'soul_id', 'device_binding_id', 'platform_account_id',
        'trace_id', 'idempotency_key', 'active_release_bom_sha256', 'release_bom_generation',
        'activation_token_sha256', 'authority_id', 'worker_module_id', 'worker_artifact_id',
        'worker_artifact_sha256', 'worker_version', 'worker_slot', 'worker_instance_id',
        'worker_generation', 'key_id', 'p256_spki_sha256', 'signature_algorithm', 'signature_format',
        'auth_scope', 'policy_id', 'rotation_epoch', 'authority_valid_from', 'authority_valid_until',
        'authority_revoked', 'worker_authority_sha256', 'routing_epoch', 'route_assignment_sha256',
        'submission_pending_occurred_at', 'policy_abort_not_before_at', 'native_abort_started_at',
        'native_abort_challenge_sha256', 'stop_kind', 'evidence_sha256', 'occurred_at',
        'privacy_class', 'signature_base64'], 'native stop proof v2');
    IF clock_timestamp() >= v_issue.valid_until
       OR v_issue.challenge_wire_sha256 IS DISTINCT FROM p_challenge_wire_sha256
       OR v_proof ->> 'schema_version' IS DISTINCT FROM '2.0.0'
       OR v_proof ->> 'contract_id' IS DISTINCT FROM 'native.stop.proof/v2'
       OR v_proof ->> 'producer_module' IS DISTINCT FROM 'windows-edge-worker'
       OR v_proof ->> 'stopped' IS DISTINCT FROM 'true'
       OR v_proof ->> 'signature_algorithm' IS DISTINCT FROM 'ECDSA_P256_SHA256'
       OR v_proof ->> 'signature_format' IS DISTINCT FROM 'IEEE_P1363_FIXED_FIELD_LOW_S'
       OR v_proof ->> 'auth_scope' IS DISTINCT FROM 'policy-approval:native-stop-proof:v2:commit-unknown'
       OR v_proof ->> 'policy_id' IS DISTINCT FROM 'RESULT-VERIFY-001'
       OR v_proof ->> 'authority_revoked' IS DISTINCT FROM 'false'
       OR (v_proof ->> 'submission_attempt_id')::uuid IS DISTINCT FROM v_issue.submission_attempt_id
       OR (v_proof ->> 'command_id')::uuid IS DISTINCT FROM v_issue.command_id
       OR (v_proof ->> 'lease_id')::uuid IS DISTINCT FROM v_issue.lease_id
       OR (v_proof ->> 'attempt')::integer IS DISTINCT FROM v_issue.attempt
       OR v_proof ->> 'native_request_binding_sha256' IS DISTINCT FROM v_issue.native_request_binding_sha256
       OR v_proof ->> 'pending_state_sha256' IS DISTINCT FROM v_issue.pending_state_sha256
       OR v_proof ->> 'submitted_request_sha256' IS DISTINCT FROM v_issue.submitted_request_sha256
       OR v_proof ->> 'soul_id' IS DISTINCT FROM v_issue.challenge_json ->> 'soul_id'
       OR v_proof ->> 'device_binding_id' IS DISTINCT FROM v_issue.challenge_json ->> 'device_binding_id'
       OR v_proof ->> 'platform_account_id' IS DISTINCT FROM v_issue.challenge_json ->> 'platform_account_id'
       OR v_proof ->> 'trace_id' IS DISTINCT FROM v_issue.challenge_json ->> 'trace_id'
       OR v_proof ->> 'idempotency_key' IS DISTINCT FROM v_issue.challenge_json ->> 'idempotency_key'
       OR v_proof ->> 'active_release_bom_sha256' IS DISTINCT FROM v_issue.challenge_json ->> 'active_release_bom_sha256'
       OR (v_proof ->> 'release_bom_generation')::bigint IS DISTINCT FROM (v_issue.challenge_json ->> 'release_bom_generation')::bigint
       OR v_proof ->> 'activation_token_sha256' IS DISTINCT FROM v_issue.challenge_json ->> 'activation_token_sha256'
       OR v_proof ->> 'worker_authority_sha256' IS DISTINCT FROM v_issue.challenge_json ->> 'worker_authority_sha256'
       OR (v_proof ->> 'routing_epoch')::bigint IS DISTINCT FROM (v_issue.challenge_json ->> 'routing_epoch')::bigint
       OR v_proof ->> 'route_assignment_sha256' IS DISTINCT FROM v_issue.challenge_json ->> 'route_assignment_sha256'
       OR (v_proof ->> 'submission_pending_occurred_at')::timestamptz IS DISTINCT FROM (v_issue.challenge_json ->> 'submission_pending_occurred_at')::timestamptz
       OR (v_proof ->> 'policy_abort_not_before_at')::timestamptz IS DISTINCT FROM (v_issue.challenge_json ->> 'policy_abort_not_before_at')::timestamptz
       OR v_proof ->> 'native_abort_challenge_sha256' IS DISTINCT FROM v_issue.native_abort_challenge_sha256
       OR v_proof ->> 'stop_kind' IS DISTINCT FROM 'NATIVE_TRANSPORT_ABORTED'
       OR v_proof ->> 'evidence_sha256' IS DISTINCT FROM p_native_stop_proof_evidence_sha256
       OR (v_proof ->> 'native_abort_started_at')::timestamptz < (v_issue.challenge_json ->> 'policy_abort_not_before_at')::timestamptz
       OR (v_proof ->> 'occurred_at')::timestamptz < (v_proof ->> 'native_abort_started_at')::timestamptz
       OR (v_proof ->> 'occurred_at')::timestamptz >= v_issue.valid_until
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_acknowledgements WHERE submission_attempt_id = v_issue.submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_quarantines WHERE submission_attempt_id = v_issue.submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_reconciliations WHERE submission_attempt_id = v_issue.submission_attempt_id)
       OR EXISTS (SELECT 1 FROM __SCHEMA__.approval_submission_recoveries WHERE submission_attempt_id = v_issue.submission_attempt_id) THEN
        RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'UNKNOWN proof cannot consume this exact current native stop challenge';
    END IF;
    v_result := __SCHEMA__.quarantine_approval_submission(
        p_quarantine_id, v_issue.submission_attempt_id, 'NATIVE_SUBMISSION_UNCERTAIN',
        p_native_stop_proof_evidence_sha256, p_state, p_state_sha256);
    IF v_result IS DISTINCT FROM 'UNKNOWN_SUBMISSION' THEN RAISE EXCEPTION USING ERRCODE = '42501', MESSAGE = 'atomic UNKNOWN expected one new lifecycle terminal'; END IF;
    INSERT INTO __SCHEMA__.native_stop_challenge_consumptions
    (consumption_id, challenge_id, submission_attempt_id, terminal_kind, terminal_evidence_sha256,
     native_stop_proof_wire_sha256, native_stop_proof_evidence_sha256, native_stop_proof_wire, consumed_at)
    VALUES
    (p_consumption_id, p_challenge_id, v_issue.submission_attempt_id, 'UNKNOWN', p_native_stop_proof_evidence_sha256,
     p_native_stop_proof_wire_sha256, p_native_stop_proof_evidence_sha256, p_native_stop_proof_wire, clock_timestamp());
    RETURN 'UNKNOWN';
END;
$function$;

DO $triggers$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['native_stop_challenge_issues', 'native_stop_challenge_consumptions'] LOOP
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I', table_name || '_append_only', table_name);
        EXECUTE format('CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON __SCHEMA__.%I FOR EACH ROW EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation()', table_name || '_append_only', table_name);
        EXECUTE format('DROP TRIGGER IF EXISTS %I ON __SCHEMA__.%I', table_name || '_no_truncate', table_name);
        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON __SCHEMA__.%I FOR EACH STATEMENT EXECUTE FUNCTION __SCHEMA__.reject_policy_approval_mutation()', table_name || '_no_truncate', table_name);
    END LOOP;
END;
$triggers$;

REVOKE ALL ON __SCHEMA__.native_stop_challenge_issues FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON __SCHEMA__.native_stop_challenge_consumptions FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;

REVOKE ALL ON FUNCTION __SCHEMA__.issue_native_stop_challenge(uuid, uuid, bytea, text, text, text, text, timestamptz) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.consume_native_stop_challenge_ack(uuid, uuid, text, jsonb, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;
REVOKE ALL ON FUNCTION __SCHEMA__.consume_native_stop_challenge_unknown(uuid, uuid, uuid, text, bytea, text, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;

-- Deliberately no GRANT in 0.6.0. A separately reviewed activation migration
-- may grant these atomic functions only after the fixed external trust and
-- two-phase submitted-request protocol are installed and real-PG verified.
