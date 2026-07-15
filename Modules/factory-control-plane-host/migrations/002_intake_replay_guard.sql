CREATE TABLE IF NOT EXISTS factory_control_plane_host.schema_migration (
  migration_version integer PRIMARY KEY CHECK (migration_version > 0),
  migration_name text UNIQUE NOT NULL CHECK (migration_name ~ '^[0-9]{3}_[a-z0-9]+(_[a-z0-9]+)*[.]sql$'),
  migration_sha256 char(64) NOT NULL CHECK (migration_sha256 ~ '^[a-f0-9]{64}$'),
  applied_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS factory_control_plane_host.intake_replay_binding (
  claim_kind text NOT NULL CHECK (claim_kind IN (
    'INTENT_ID', 'IDEMPOTENCY_KEY', 'REQUESTER_AUTH_NONCE', 'APPROVAL_NONCE'
  )),
  claim_key_sha256 char(64) NOT NULL CHECK (claim_key_sha256 ~ '^[a-f0-9]{64}$'),
  upgrade_intent_sha256 char(64) NOT NULL CHECK (upgrade_intent_sha256 ~ '^[a-f0-9]{64}$'),
  first_workflow_id text NOT NULL,
  first_request_id text NOT NULL,
  first_receipt_sha256 char(64) NOT NULL CHECK (first_receipt_sha256 ~ '^[a-f0-9]{64}$'),
  occurred_at timestamptz NOT NULL,
  PRIMARY KEY (claim_kind, claim_key_sha256),
  FOREIGN KEY (first_workflow_id, first_request_id)
    REFERENCES factory_control_plane_host.outbox_message(workflow_id, request_id)
);

CREATE TABLE IF NOT EXISTS factory_control_plane_host.intake_replay_conflict (
  conflict_sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  conflict_sha256 char(64) UNIQUE NOT NULL CHECK (conflict_sha256 ~ '^[a-f0-9]{64}$'),
  workflow_id text NOT NULL,
  request_id text NOT NULL,
  claim_kind text NOT NULL CHECK (claim_kind IN (
    'INTENT_ID', 'IDEMPOTENCY_KEY', 'REQUESTER_AUTH_NONCE', 'APPROVAL_NONCE'
  )),
  claim_key_sha256 char(64) NOT NULL CHECK (claim_key_sha256 ~ '^[a-f0-9]{64}$'),
  bound_upgrade_intent_sha256 char(64) NOT NULL CHECK (bound_upgrade_intent_sha256 ~ '^[a-f0-9]{64}$'),
  conflicting_upgrade_intent_sha256 char(64) NOT NULL CHECK (conflicting_upgrade_intent_sha256 ~ '^[a-f0-9]{64}$'),
  attempted_receipt_sha256 char(64) NOT NULL CHECK (attempted_receipt_sha256 ~ '^[a-f0-9]{64}$'),
  occurred_at timestamptz NOT NULL,
  CHECK (bound_upgrade_intent_sha256 <> conflicting_upgrade_intent_sha256),
  FOREIGN KEY (workflow_id, request_id)
    REFERENCES factory_control_plane_host.outbox_message(workflow_id, request_id)
);

DO $$
DECLARE table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY[
    'schema_migration', 'intake_replay_binding', 'intake_replay_conflict'
  ]
  LOOP
    EXECUTE format('DROP TRIGGER IF EXISTS reject_mutation ON factory_control_plane_host.%I', table_name);
    EXECUTE format('CREATE TRIGGER reject_mutation BEFORE UPDATE OR DELETE ON factory_control_plane_host.%I FOR EACH ROW EXECUTE FUNCTION factory_control_plane_host.reject_mutation()', table_name);
    EXECUTE format('DROP TRIGGER IF EXISTS reject_truncate ON factory_control_plane_host.%I', table_name);
    EXECUTE format('CREATE TRIGGER reject_truncate BEFORE TRUNCATE ON factory_control_plane_host.%I FOR EACH STATEMENT EXECUTE FUNCTION factory_control_plane_host.reject_mutation()', table_name);
    EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON factory_control_plane_host.%I FROM PUBLIC', table_name);
  END LOOP;
END $$;

REVOKE ALL ON ALL TABLES IN SCHEMA factory_control_plane_host FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA factory_control_plane_host FROM PUBLIC;
