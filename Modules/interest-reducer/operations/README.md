# interest-reducer operations

The module is proposed and not production release eligible. Runtime routing may change only through an exact signed Release BOM.

Rollout order is synthetic test, shadow, bounded canary, rolling waves, and soak. Shadow is forbidden from producing real-world side effects. The feature flag, kill switch, canary rule, rollback target, minimum evidence, and human approval boundary are machine truth in ../module.yaml.

Rollback restores routing and the previous compatible signed BOM. It must not pretend that external side effects or destructive data changes were reversed; those require stop, reconcile, compensate, or restore procedures.
