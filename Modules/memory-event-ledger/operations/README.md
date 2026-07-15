# memory-event-ledger operations

The module is proposed and not production release eligible. Runtime routing may change only through an exact signed Release BOM.

Rollout order is synthetic test, shadow, bounded canary, rolling waves, and soak. Shadow is forbidden from producing real-world side effects. The feature flag, kill switch, canary rule, rollback target, minimum evidence, and human approval boundary are machine truth in ../module.yaml.

Rollback restores routing and the previous compatible signed BOM. It must not pretend that external side effects or destructive data changes were reversed; those require stop, reconcile, compensate, or restore procedures.

## v2 rollout hold

Production routing is disabled until both blockers are resolved:

1. Soul Registry exposes an exact current-resolution provider with revision, currentness, expiry, trust epoch, and revocation epoch through a fixed non-public composition.
2. The signed Release BOM pins the Executor Gateway receipt P-256 root, key role, key id, trust epoch, and revocation source.

Do not substitute a public `SoulResolved`, a caller mapping, a lambda, an environment-selected public key, or test authority. v1 append is permanently disabled and is not a rollback target.

Rollback disables `memory_event_ledger_v2`, stops new append routing, retains v2 rows and pending outbox records, and restores the previous signed BOM. Quarantines and chain heads remain for reconciliation. Privacy export is ordered by `soul_sequence`; correction appends a link plus replacement event; deletion appends a tombstone and rebuilds downstream projections. Until an independent privacy authority exists, correction/deletion writes remain fail-closed.
