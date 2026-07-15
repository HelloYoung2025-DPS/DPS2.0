# Tests

Run:

```text
python3.12 -m unittest Modules/factory-impact-analyzer/tests/test_impact_analyzer.py
python3.12 -m unittest Modules/factory-impact-analyzer/tests/test_contracts.py
```

The behavior suite builds isolated temporary Git worktrees and runs the real `factory-upgrade-intake` v2 and `factory-instruction-resolver` v2 implementations before the Impact Analyzer. It covers all four change kinds, upstream multi-change tuple ordering, exact-major consumer selection, deterministic plans, scope equality, write/read separation, authority separation, receipt/nonce and source-metadata replay, raw swap, causal timestamps, expiry equality, wrong issuer/audience/major, index-only drift, TOCTOU, stable-policy immutability, and shadow zero-side-effect behavior.

The contract suite validates real analyzer outputs with Draft 2020-12, independently recomputes plan ID/full SHA, covers all four expectation shapes and fail-closed release/side-effect/schema attacks, and freezes the v1 Schema digest.

These tests use process-local trust and synthetic temporary repositories. They are repository diagnostics, not portable cross-process, deployment, Windows, device, canary, or scale evidence. A required `SKIP`, `PARTIAL`, `NOT_RUN`, `INFRA_ERROR`, or missing raw result is failure.
