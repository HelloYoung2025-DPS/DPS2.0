CREATE TABLE IF NOT EXISTS __SCHEMA__.composition_generation_state (
    scope text PRIMARY KEY CHECK (scope = 'binding-production'),
    highest_generation bigint NOT NULL CHECK (highest_generation >= 1),
    release_bom_sha256 char(64) NOT NULL CHECK (release_bom_sha256 ~ '^[a-f0-9]{64}$'),
    composition_descriptor_sha256 char(64) NOT NULL CHECK (composition_descriptor_sha256 ~ '^[a-f0-9]{64}$'),
    attestation_expires_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

REVOKE ALL ON TABLE __SCHEMA__.composition_generation_state FROM PUBLIC;
