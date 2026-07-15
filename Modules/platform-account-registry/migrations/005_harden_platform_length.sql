ALTER TABLE __SCHEMA__.accounts
    DROP CONSTRAINT IF EXISTS dps_accounts_platform_v1,
    ADD CONSTRAINT dps_accounts_platform_v1 CHECK (
        length(platform) BETWEEN 1 AND 64
        AND platform ~ '^[a-z0-9]+([._-][a-z0-9]+)*$'
    );
