# Migrations

This module owns no database and therefore has no module-local data migration.
Rollback progress is reconstructed from append-only `upgrade.event/v1`
evidence owned by `factory-evidence-ledger`.

Any future persistence change requires an additive, versioned migration in the
owning storage module, N/N-1 compatibility evidence, a read-back test, and a
separate rollback procedure. This directory must not become a hidden task-state
or approval store.
