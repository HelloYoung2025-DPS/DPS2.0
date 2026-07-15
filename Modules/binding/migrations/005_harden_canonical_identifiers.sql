-- Fail-closed convergence from the earlier proposed identifier shapes. No
-- release-eligible binding database used those shapes, so invalid pre-release
-- rows must be exported and recreated instead of silently rewritten.

ALTER TABLE __SCHEMA__.bindings
    DROP CONSTRAINT IF EXISTS ck_bindings_device_binding_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_bindings_platform_account_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_bindings_trace_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_bindings_idempotency_key_canonical,
    ADD CONSTRAINT ck_bindings_device_binding_id_canonical CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_bindings_platform_account_id_canonical CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_bindings_trace_id_canonical CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_bindings_idempotency_key_canonical CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$');

ALTER TABLE __SCHEMA__.binding_attempts
    DROP CONSTRAINT IF EXISTS ck_binding_attempts_idempotency_key_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_attempts_device_binding_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_attempts_platform_account_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_attempts_trace_id_canonical,
    ADD CONSTRAINT ck_binding_attempts_idempotency_key_canonical CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    ADD CONSTRAINT ck_binding_attempts_device_binding_id_canonical CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_attempts_platform_account_id_canonical CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_attempts_trace_id_canonical CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$');

ALTER TABLE __SCHEMA__.binding_revisions
    DROP CONSTRAINT IF EXISTS ck_binding_revisions_device_binding_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_revisions_platform_account_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_revisions_trace_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_revisions_idempotency_key_canonical,
    ADD CONSTRAINT ck_binding_revisions_device_binding_id_canonical CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_revisions_platform_account_id_canonical CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_revisions_trace_id_canonical CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_revisions_idempotency_key_canonical CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$');

ALTER TABLE __SCHEMA__.idempotency_receipts
    DROP CONSTRAINT IF EXISTS ck_binding_receipts_idempotency_key_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_receipts_device_binding_id_canonical,
    ADD CONSTRAINT ck_binding_receipts_idempotency_key_canonical CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    ADD CONSTRAINT ck_binding_receipts_device_binding_id_canonical CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$');

ALTER TABLE __SCHEMA__.idempotency_quarantine
    DROP CONSTRAINT IF EXISTS ck_binding_quarantine_device_binding_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_quarantine_platform_account_id_canonical,
    ADD CONSTRAINT ck_binding_quarantine_device_binding_id_canonical CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_quarantine_platform_account_id_canonical CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$');

ALTER TABLE __SCHEMA__.outbox
    DROP CONSTRAINT IF EXISTS ck_binding_outbox_idempotency_key_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_outbox_device_binding_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_outbox_platform_account_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_outbox_trace_id_canonical,
    ADD CONSTRAINT ck_binding_outbox_idempotency_key_canonical CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$'),
    ADD CONSTRAINT ck_binding_outbox_device_binding_id_canonical CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_outbox_platform_account_id_canonical CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_outbox_trace_id_canonical CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$');

ALTER TABLE __SCHEMA__.binding_mutation_fences
    DROP CONSTRAINT IF EXISTS ck_binding_fences_device_binding_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_fences_platform_account_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_fences_trace_id_canonical,
    DROP CONSTRAINT IF EXISTS ck_binding_fences_idempotency_key_canonical,
    ADD CONSTRAINT ck_binding_fences_device_binding_id_canonical CHECK (length(device_binding_id) = 35 AND device_binding_id ~ '^db_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_fences_platform_account_id_canonical CHECK (length(platform_account_id) = 35 AND platform_account_id ~ '^pa_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_fences_trace_id_canonical CHECK (length(trace_id) = 38 AND trace_id ~ '^trace_[a-f0-9]{32}$'),
    ADD CONSTRAINT ck_binding_fences_idempotency_key_canonical CHECK (length(idempotency_key) = 69 AND idempotency_key ~ '^idem_[a-f0-9]{64}$');
