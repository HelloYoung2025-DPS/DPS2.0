-- Upgrade path for device-registry releases whose 001 migration predates
-- provider-owned binding reservations. Safe to re-run after the current 001.
CREATE TABLE IF NOT EXISTS __SCHEMA__.binding_reservations
(
    reservation_id text PRIMARY KEY CHECK (length(reservation_id) = 69 AND reservation_id ~ '^bres_[a-f0-9]{64}$'),
    device_id text NOT NULL REFERENCES __SCHEMA__.devices(device_id) ON DELETE RESTRICT,
    soul_id text NOT NULL CHECK (length(soul_id) = 69 AND soul_id ~ '^soul_[a-f0-9]{64}$'),
    device_binding_id text NOT NULL CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    platform_account_id text NOT NULL CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    device_registration_revision bigint NOT NULL CHECK (device_registration_revision >= 1),
    state text NOT NULL CHECK (state IN ('held', 'active', 'released')),
    lease_expires_at timestamptz NULL,
    trace_id text NOT NULL CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    occurred_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    CHECK ((state = 'held') = (lease_expires_at IS NOT NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_device_effective_binding_reservation
    ON __SCHEMA__.binding_reservations(device_id)
    WHERE state IN ('held', 'active');

CREATE INDEX IF NOT EXISTS ix_device_binding_reservation_scope
    ON __SCHEMA__.binding_reservations(soul_id, device_binding_id, platform_account_id, reservation_id);
