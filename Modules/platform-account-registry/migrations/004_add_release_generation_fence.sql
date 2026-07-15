CREATE TABLE IF NOT EXISTS __SCHEMA__.release_generation_state (
    scope text PRIMARY KEY CHECK (scope = 'platform-account-production'),
    highest_generation bigint NOT NULL CHECK (highest_generation >= 1),
    release_bom_sha256 char(64) NOT NULL CHECK (
        length(release_bom_sha256) = 64 AND release_bom_sha256 !~ '[^a-f0-9]'),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

REVOKE ALL ON TABLE __SCHEMA__.release_generation_state FROM PUBLIC;
