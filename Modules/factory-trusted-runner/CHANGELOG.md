# Changelog

## 0.1.0 - 2026-07-14

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms in runtime validation, Schema, and adversarial fixtures.
- Registered the versioned `trusted.test.result/v1` receipt path to `factory-control-plane-host`.
- Added exact policy-owned argv execution with no shell, active fencing, role separation, workspace/commit binding, hashed fail-closed evidence, and externally issued RSA-PSS attestations.
- Added a required Draft 2020-12 `trusted.test.result/v1` contract suite that validates real `TrustedRunner` output and adversarial instances; the Manifest candidate minimum is now `CONTRACT_VERIFIED`, without claiming formal verification.
