# gbrain-projector operations

The module is proposed and not production release eligible. Runtime routing may change only through an exact signed Release BOM.

Rollout order is synthetic test, shadow, bounded canary, rolling waves, and soak. Shadow is forbidden from producing real-world side effects. The feature flag, kill switch, canary rule, rollback target, minimum evidence, and human approval boundary are machine truth in ../module.yaml.

Rollback restores routing and the previous compatible signed BOM. It must not pretend that external side effects or destructive data changes were reversed; those require stop, reconcile, compensate, or restore procedures.

The v2 rollout flag is `gbrain_projector_v2`; the kill switch is `kill_gbrain_projector`. Canary prerequisites include real PostgreSQL migration evidence, exact v2 consumer compatibility, Source binding collision/concurrency/restart tests, and append-only revision evidence. Frozen v1 is quarantine-only and is not a rollback runtime target.

Database rollout uses two independently managed direct-login credentials. Set `DPS_TEST_POSTGRES_ADMIN_URI` only for migration/integration infrastructure and `DPS_TEST_POSTGRES_RUNTIME_URI` only for the application runtime. Never copy the migration connection into runtime configuration, use `SET ROLE`, or log either value. Run the migrator first, retain its created-versus-verified-existing result, then start the runtime and require its attestation before routing traffic.

An existing schema is accepted only when every expected table, column/default, PK, UNIQUE, CHECK, FK, index, trigger, owner, function, and effective ACL matches. A weak or partly created schema is an operator-visible stop condition, not something the migrator repairs. Schema or ACL drift after initialization requires immediate kill-switch activation, routing stop, forensic export, and restoration from the last signed database/BOM procedure.

Nonce exhaustion, binding checksum drift, cross-Soul Source reuse, or persisted revision ambiguity stops allocation/rendering and records quarantine where possible. Operators must not bypass the unique constraint or rewrite binding/revision rows.

The provider currently advertises v2 while repository consumers still retain v1 declarations. This reciprocal compatibility failure remains a release blocker; do not turn the matrix green by weakening the provider or silently editing consumers.
