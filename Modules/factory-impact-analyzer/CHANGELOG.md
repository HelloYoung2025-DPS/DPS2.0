# Changelog

## 0.2.0 - 2026-07-15

- Froze `upgrade.intent/v1`, `instruction.receipt/v1`, and `module.change.plan/v1` as deprecated quarantine-only wires with no runtime path.
- Added fixed process-bound Intent, Receipt, and stable-policy authorities with full raw-byte/source-metadata fingerprints, receipt/nonce replay protection, and causal issue/verify/expiry ordering; plain Mapping, lambda verifier, direct constructor, cross-authority capability, raw swap, and wrong audience/producer/major fail closed.
- Added `module.change.plan/v2` with full Intent/Receipt digests, separate instruction/path/combined-write scope digests, exact-major expectations, baseline-facts digest, dependency waves, stable policy, risk/stage checks, deterministic plan ID/full SHA, and mandatory changeset verification.
- Independently recompute exact scope and Git/Manifest/contract currentness before and after analysis. Quarantine-only and retired contracts cannot become runnable.
- Added real isolated Intake→Resolver→Impact coverage for all four change kinds plus upstream multi-change ordering, source/local replay, timestamp causality, scope, exact-major, TOCTOU, index-only, policy, determinism, and shadow no-side-effect attacks.
- Kept runtime status `WAITING_EXTERNAL` and `releaseEligible=false`; portable cross-process trust and downstream v2 reciprocal consumers remain external blockers.

## 0.1.0 - 2026-07-14

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms in runtime validation, Schema, and adversarial fixtures.
- Registered the versioned `module.change.plan/v1` receipt path to `factory-control-plane-host`.
- Proposed Manifest-owned impact analysis, trusted-policy checks and roles, contract consumer expansion, and dependency-safe parallel waves.
- Added a required Draft 2020-12 `module.change.plan/v1` contract suite with production `ImpactAnalyzer` output plus fail-closed negative instances; the Manifest candidate minimum is now `CONTRACT_VERIFIED`, without claiming formal verification.
