# Changelog

## 0.2.0 - 2026-07-15

- Added the breaking active `upgrade.intent/v2` REQUESTED-claim contract. It binds requester authentication receipt/nonce, exact baseline, verified Manifest ownership snapshot/receipt, requested scope, unverified expected contract changes, requested risk/stage, authorization, and the full intent digest.
- Added four typed contract-change kinds: `add-major`, `additive-schema`, one-way `mode-transition`, and exact baseline-absent `introduce-quarantined-major`. Provider `compat-read`, reactivation, invalid retired status, source outside requested scope, and duplicate/conflicting contract majors fail closed.
- Added process-composed authentication and Manifest authorities with exact-instance issuance registries and trusted-clock expiry revalidation. These are type/composition controls; external verification receipts remain the trust root.
- Added domain-separated SHA-256 commitments for the Manifest-bound contract-change component, quarantine import proof, approval subject, requester context, and full intent.
- Bound selected human approval to exact approver, scope, intent, baseline, requested risk/stage, receipt, nonce, issuance/expiry, approval subject, and full intent. Pending, rejected, expired, mismatched, and self-approved requests are not routable.
- Added a bounded duplicate-key-rejecting UTF-8 JSON codec, canonical UTC timestamps, integer/depth/size bounds, concrete path rules, and wrong-type fail-closed handling.
- Added an owned v2 positive/negative corpus whose Schema and production expectations are asserted independently. Local unit and contract suites do not claim integration evidence.
- Preserved the exact v1 Schema bytes while moving v1 to fixed routing metadata and `quarantine-only`; v1 has no current validation, encoding, approval, domain, or outbound path.
- Limited current raw v2 communication to the Instruction Resolver, Impact Analyzer, and Host orchestration boundary. Direct raw-intent delivery to the Release Controller is forbidden.
- Removed the unimplemented and architecturally misplaced `rollout.command/v1` declaration; Intake has no direct Release Controller path. Durable PostgreSQL nonce/idempotency handling remains `WAITING_EXTERNAL`, so `releaseEligible` stays false.

## 0.1.0 - 2026-07-14

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms in runtime validation, Schema, and adversarial fixtures.
- Registered the versioned `upgrade.intent/v1` receipt path to `factory-control-plane-host`.
- Added deterministic `upgrade.intent/v1` validation.
- Added R4 rejection, Manifest ownership scoping, verified authentication, and human production approval separation.
- Allowed nullable identity-envelope values only in normalized `soul_`, `db_`, and `pa_` form.
- Added standard-library unit and adversarial tests.
- Added a required Draft 2020-12 `upgrade.intent/v1` contract suite using production-normalized output and fail-closed negative instances; the Manifest candidate minimum is now `CONTRACT_VERIFIED`, without claiming formal verification.
