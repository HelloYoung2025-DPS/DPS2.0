# Migrations

`NOT_APPLICABLE` to the current repository slice: the module defines a durable exact-envelope receipt-store interface but does not yet own or ship a database implementation. No empty or fabricated SQL migration is used to imply persistence.

Before production eligibility, a separately reviewed persistence implementation must add append-only migration files, migration-digest drift protection, exact key/payload uniqueness constraints, conflict quarantine, crash-window recovery, retention/deletion policy, and PostgreSQL integration tests. That future change must update `module.yaml` from `releaseEligible: false` only after the required executable evidence is `PASS`.
