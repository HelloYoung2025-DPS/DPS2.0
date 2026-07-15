# gbrain-projector migrations

Use expand, migrate, contract across separate releases. The current and previous compatible versions must both run after the expand step. Destructive contraction requires a later signed BOM, backup evidence, and an explicit rollback or forward-fix decision.

`001_create_gbrain_projector.sql` is the proposed PostgreSQL 18.4 expand migration. It is intentionally plain `CREATE`, not `IF NOT EXISTS`: `GBrainProjectorPostgresMigrator` first locks the module schema, creates it only when absent, and otherwise verifies the complete existing catalog without repairing or adopting a weak table. It creates `source_bindings` keyed by the complete Soul, `source_binding_quarantine` for bounded nonce exhaustion, and append-only `rendered_revisions` constrained to the exact Source binding tuple.

The migration login owns the schema, tables, indexes, constraints, triggers, and rejection function. The separately authenticated runtime login receives only schema `USAGE` and table `SELECT, INSERT`; it receives no DDL, UPDATE, DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN, function execution, ownership, inherited role, or grant option. PUBLIC receives no module-schema privilege. UPDATE/DELETE row triggers and TRUNCATE statement triggers protect all three ledgers even when the owner operates them accidentally.

Canonical text and JSONB must be semantically equal at insert time. Runtime read-back independently revalidates those forms and all relational proof columns. Runtime rollback does not erase ledger rows; correction creates a new compatible revision, while deletion requires a separately approved retention workflow.
