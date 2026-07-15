# gbrain-projector source

Production source belongs below this directory. Other modules may depend only on versioned provided contracts, never internal implementation types.

`GBrainProjectorPostgresMigrator` is the only DDL entrypoint. It receives separate migration and runtime connection strings, proves both are distinct direct login identities, refuses elevated or inherited runtime roles, creates only an absent schema, and otherwise adopts an existing schema only after exact catalog, owner, constraint, index, trigger, and ACL attestation.

`GBrainProjectorPostgresRuntime` receives only the runtime connection string. `InitializeAsync` performs attestation and never executes migration SQL; resolution and append fail closed until initialization succeeds. The runtime fixes the Source derivation, microsecond-normalized system clock, PostgreSQL store, Source binding authority, and renderer into one process boundary. Every read cross-checks relational proof columns, canonical text, JSONB, checksum, and referenced binding before issuing a capability or accepting a revision.

`GBrainProjectionRenderer` accepts only `VerifiedGBrainSourceBinding`; callers cannot provide a raw Source identifier or a custom binding store through the public API. Rendering remains a DTO-only operation with no GBrain client or credentials.
