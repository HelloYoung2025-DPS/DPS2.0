ALTER TABLE __SCHEMA__.accounts
    DROP CONSTRAINT IF EXISTS accounts_alias_key_id_check,
    DROP CONSTRAINT IF EXISTS accounts_authorization_evidence_id_check,
    DROP CONSTRAINT IF EXISTS dps_accounts_alias_key_id_v1,
    DROP CONSTRAINT IF EXISTS dps_accounts_authorization_evidence_id_v1,
    ADD CONSTRAINT dps_accounts_alias_key_id_v1 CHECK (
        length(alias_key_id) BETWEEN 1 AND 64
        AND alias_key_id ~ '^[a-z0-9][a-z0-9._-]{0,63}$'
    ),
    ADD CONSTRAINT dps_accounts_authorization_evidence_id_v1 CHECK (
        length(authorization_evidence_id) BETWEEN 10 AND 128
        AND authorization_evidence_id ~ '^approval_[a-z0-9_-]{1,119}$'
    );

ALTER TABLE __SCHEMA__.authorization_revisions
    DROP CONSTRAINT IF EXISTS authorization_revisions_authorization_evidence_id_check,
    DROP CONSTRAINT IF EXISTS dps_authorization_revisions_authorization_evidence_id_v1,
    ADD CONSTRAINT dps_authorization_revisions_authorization_evidence_id_v1 CHECK (
        length(authorization_evidence_id) BETWEEN 10 AND 128
        AND authorization_evidence_id ~ '^approval_[a-z0-9_-]{1,119}$'
    );
