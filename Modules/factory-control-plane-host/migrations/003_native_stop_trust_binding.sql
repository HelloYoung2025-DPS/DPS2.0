CREATE TABLE IF NOT EXISTS factory_control_plane_host.native_stop_authority_trust_binding (
  receipt_id text PRIMARY KEY
    CHECK (receipt_id ~ '^native-stop-trust-[a-f0-9]{32}$'),
  receipt_sha256 char(64) NOT NULL
    CHECK (receipt_sha256 ~ '^[a-f0-9]{64}$'),
  release_bom_id text NOT NULL,
  release_bom_sha256 char(64) NOT NULL
    CHECK (release_bom_sha256 ~ '^[a-f0-9]{64}$'),
  integration_commit char(40) NOT NULL
    CHECK (integration_commit ~ '^[a-f0-9]{40}$'),
  release_bom_generation bigint NOT NULL
    CHECK (release_bom_generation > 0),
  activation_token_sha256 char(64) NOT NULL
    CHECK (activation_token_sha256 ~ '^[a-f0-9]{64}$'),
  authority_sets_sha256 char(64) NOT NULL
    CHECK (authority_sets_sha256 ~ '^[a-f0-9]{64}$'),
  fact_sha256 char(64) NOT NULL
    CHECK (fact_sha256 ~ '^[a-f0-9]{64}$'),
  fact_json jsonb NOT NULL,
  first_workflow_id text NOT NULL
    REFERENCES factory_control_plane_host.workflow_request(workflow_id),
  occurred_at timestamptz NOT NULL,
  CHECK (octet_length(fact_json::text) <= 8388608)
);

DROP TRIGGER IF EXISTS reject_mutation
  ON factory_control_plane_host.native_stop_authority_trust_binding;
CREATE TRIGGER reject_mutation
  BEFORE UPDATE OR DELETE
  ON factory_control_plane_host.native_stop_authority_trust_binding
  FOR EACH ROW EXECUTE FUNCTION factory_control_plane_host.reject_mutation();

DROP TRIGGER IF EXISTS reject_truncate
  ON factory_control_plane_host.native_stop_authority_trust_binding;
CREATE TRIGGER reject_truncate
  BEFORE TRUNCATE
  ON factory_control_plane_host.native_stop_authority_trust_binding
  FOR EACH STATEMENT EXECUTE FUNCTION factory_control_plane_host.reject_mutation();

REVOKE UPDATE, DELETE, TRUNCATE
  ON factory_control_plane_host.native_stop_authority_trust_binding FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA factory_control_plane_host FROM PUBLIC;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA factory_control_plane_host FROM PUBLIC;
