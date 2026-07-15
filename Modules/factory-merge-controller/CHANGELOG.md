# Changelog

## 0.1.0 - Proposed

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms in runtime validation, Schema, and adversarial fixtures.
- Registered the versioned `merge.decision/v1` receipt path to `factory-control-plane-host`.
- Added merge-head-only evidence evaluation.
- Added role separation, stale-instruction, conflict, and required-PASS gates.
- Added versioned merge request and decision contracts plus unit tests.
- Hardened the Contract suite to load the exact module-owned production source under isolated Python without relying on `PYTHONPATH` or unit-test fixtures.
- Hardened the required Unit suite with the same exact-path isolated loader instead of an ambient `PYTHONPATH` import.
