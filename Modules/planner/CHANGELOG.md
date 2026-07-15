# Planner changelog

## 0.2.0 - Proposed

- Add `action.proposal/v2` with exact typed SHA-256 selector, value, and evidence references; prompt tokens without spaces, Unicode/confusables, PII, wrong type prefixes, and non-canonical digests now fail structurally without keyword matching.
- Switch `ShadowActionPlanner` and the real-local-process production fixture path to v2 while retaining the original v1 DTO/schema/corpus as deprecated shadow/quarantine read compatibility only.
- Add a machine-readable major-mode inventory and gates proving Planner never produces v1, the current v2 boundary rejects v1 and unknown majors, neither proposal carries authority, and only v2 is eligible for separately authorized downstream promotion.
- Require child-process cleanup to prove exit within five seconds after timeout, forced kill, and disposal.

## 0.1.0 - Proposed

- Add a required seven-case `REAL_LOCAL_PROCESS` Integration suite over the production shadow planner and strict proposal codec, including strict wire rejection, scope and prompt attacks, forced kill/restart, byte-identical replay, and an exact machine-readable case inventory; every counted Integration case now starts a child process, while inventory classification is a separate Unit case.
- Publish crash-window readiness through flushed same-directory temporary-file rename, then use one stable parent read handle with bounded retry and exact child-PID/process-exit binding before replay; this remains local process evidence only and does not claim Policy, device, Windows, canary, or production verification.
- Add the shadow-only `action.proposal/v1` contract and deterministic planner boundary.
- Reject unknown actions, parameters, identity scope, and any request for execution authority.
- Tighten the unpublished v1 contract to fixed non-PII opaque identifiers, exact action/side-effect/parameter branches, bounded collections, and strict duplicate/extra/missing-member JSON decoding.
- Snapshot parameters and evidence into sorted read-only collections before publication.
- Expose snapshot-returning validation only, and classify `ProposerKind` as untrusted input rather than an authenticated role.
- Replace delimiter hashing with strict UTF-8, unsigned big-endian length-prefixed canonical commitments and deterministic RFC 9562 UUIDv8 proposal identifiers.
- Add one shared adversarial corpus exercised by the strict C# codec and a real Draft 2020-12 `jsonschema` validator; evidence remains synthetic and shadow-only.
