-- Upgrade path for platform-account-registry releases whose 001 migration
-- predates provider-owned binding reservations. Safe to re-run after current 001.
CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_reservations (
    reservation_id text PRIMARY KEY CHECK (length(reservation_id) = 69 AND reservation_id ~ '^bres_[a-f0-9]{64}$'),
    platform_account_id text NOT NULL REFERENCES __SCHEMA__.accounts(platform_account_id) ON DELETE RESTRICT,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[0-9a-f]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[0-9a-f]{32}$'),
    account_authorization_revision bigint NOT NULL CHECK (account_authorization_revision >= 1),
    state text NOT NULL CHECK (state IN ('held', 'active', 'released')),
    lease_expires_at timestamptz NULL,
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[0-9a-f]{32}$'),
    occurred_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK ((state = 'held') = (lease_expires_at IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_platform_account_effective_binding_reservation
    ON __SCHEMA__.binding_reservations(platform_account_id)
    WHERE state IN ('held', 'active');

CREATE INDEX IF NOT EXISTS ix_platform_account_binding_reservation_scope
    ON __SCHEMA__.binding_reservations(soul_id, device_binding_id, platform_account_id, reservation_id);
