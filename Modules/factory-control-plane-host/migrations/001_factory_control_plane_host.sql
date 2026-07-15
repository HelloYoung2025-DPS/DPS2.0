CREATE SCHEMA IF NOT EXISTS factory_control_plane_host;
REVOKE ALL ON SCHEMA factory_control_plane_host FROM PUBLIC;

CREATE OR REPLACE FUNCTION factory_control_plane_host.reject_mutation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'append-only Factory control-plane table cannot be mutated';
END $$;

CREATE TABLE IF NOT EXISTS factory_control_plane_host.workflow_request (
  workflow_id text PRIMARY KEY,
  idempotency_key text NOT NULL CHECK (char_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
  request_sha256 char(64) NOT NULL,
  request_json jsonb NOT NULL,
  occurred_at timestamptz NOT NULL,
  UNIQUE (workflow_id, idempotency_key)
);
CREATE TABLE IF NOT EXISTS factory_control_plane_host.role_binding_receipt (
  workflow_id text PRIMARY KEY REFERENCES factory_control_plane_host.workflow_request(workflow_id),
  binding_id text UNIQUE NOT NULL,
  binding_sha256 char(64) NOT NULL,
  binding_json jsonb NOT NULL,
  occurred_at timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS factory_control_plane_host.workflow_event (
  workflow_id text NOT NULL REFERENCES factory_control_plane_host.workflow_request(workflow_id),
  sequence bigint NOT NULL,
  event_id text UNIQUE NOT NULL,
  event_type text NOT NULL,
  state text NOT NULL,
  fencing_token bigint NOT NULL,
  idempotency_key text NOT NULL CHECK (char_length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
  payload_sha256 char(64) NOT NULL,
  payload_json jsonb NOT NULL,
  previous_event_sha256 char(64) NOT NULL,
  event_sha256 char(64) NOT NULL,
  event_json jsonb NOT NULL,
  occurred_at timestamptz NOT NULL,
  PRIMARY KEY (workflow_id, sequence),
  UNIQUE (workflow_id, idempotency_key)
);
CREATE TABLE IF NOT EXISTS factory_control_plane_host.fence_event (
  workflow_id text NOT NULL REFERENCES factory_control_plane_host.workflow_request(workflow_id),
  fencing_token bigint NOT NULL CHECK (fencing_token > 0),
  worker_identity text NOT NULL,
  occurred_at timestamptz NOT NULL,
  PRIMARY KEY (workflow_id, fencing_token)
);
CREATE TABLE IF NOT EXISTS factory_control_plane_host.outbox_message (
  workflow_id text NOT NULL REFERENCES factory_control_plane_host.workflow_request(workflow_id),
  request_id text NOT NULL,
  stage_id text NOT NULL,
  message_sha256 char(64) NOT NULL,
  message_json jsonb NOT NULL,
  occurred_at timestamptz NOT NULL,
  PRIMARY KEY (workflow_id, request_id)
);
CREATE TABLE IF NOT EXISTS factory_control_plane_host.outbox_delivery_event (
  delivery_sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  workflow_id text NOT NULL REFERENCES factory_control_plane_host.workflow_request(workflow_id),
  request_id text NOT NULL,
  status text NOT NULL CHECK (status IN ('ATTEMPTED','ACKNOWLEDGED')),
  command_sha256 char(64),
  receipt_sha256 char(64),
  fencing_token bigint NOT NULL,
  occurred_at timestamptz NOT NULL
);
CREATE TABLE IF NOT EXISTS factory_control_plane_host.module_receipt (
  workflow_id text NOT NULL REFERENCES factory_control_plane_host.workflow_request(workflow_id),
  request_id text NOT NULL,
  receipt_id text UNIQUE NOT NULL,
  receipt_sha256 char(64) NOT NULL,
  receipt_json jsonb NOT NULL,
  fencing_token bigint NOT NULL,
  occurred_at timestamptz NOT NULL,
  PRIMARY KEY (workflow_id, request_id),
  FOREIGN KEY (workflow_id, request_id) REFERENCES factory_control_plane_host.outbox_message(workflow_id, request_id)
);
CREATE TABLE IF NOT EXISTS factory_control_plane_host.quarantine (
  quarantine_sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  workflow_id text NOT NULL,
  reason text NOT NULL,
  conflicting_sha256 char(64) NOT NULL,
  occurred_at timestamptz NOT NULL
);

DO $$
DECLARE table_name text;
BEGIN
  FOREACH table_name IN ARRAY ARRAY['workflow_request','role_binding_receipt','workflow_event','fence_event','outbox_message','outbox_delivery_event','module_receipt','quarantine']
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
