# evidence-service operations

The module is proposed and not production release eligible. Runtime routing may change only through an exact signed Release BOM.

Rollout order is synthetic test, shadow, bounded canary, rolling waves, and soak. Shadow is forbidden from producing real-world side effects. The feature flag, kill switch, canary rule, rollback target, minimum evidence, and human approval boundary are machine truth in ../module.yaml.

Rollback restores routing and the previous compatible signed BOM. It must not pretend that external side effects or destructive data changes were reversed; those require stop, reconcile, compensate, or restore procedures.

The F2 evidence store writes the canonical test receipt and its declared source-receipt bytes in the same PostgreSQL transaction as the digest record. Artifact rows are immutable, are read only with exact Soul/device/account scope, and are rehashed before a runner signature is accepted after restart. Raw identity aliases, credentials, and service secrets are forbidden evidence content.

This remains a proposed integration baseline. A production release must add the approved retention and privacy-erasure mechanism for personal evidence without rewriting the retained non-identifying audit proof.
