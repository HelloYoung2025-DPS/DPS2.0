# Operation Compiler changelog

## 0.3.0 - Proposed

- Replace synchronous authoritative-read and command-consumer ports with cancellation-aware asynchronous APIs.
- Enforce one fixed 2000ms deadline across authoritative read, compilation, and command handoff; a late terminal result cannot become caller-visible success.
- Require an explicit late-outcome quarantine port, track approval/command phase and canonical commitments, and expose graceful-shutdown draining with quarantine-write failures surfaced.
- Align `duration_ms` with Planner's canonical decimal `1..600000` rule and align `selector_ref`/`value_ref` with its 128-character opaque machine-reference grammar while rejecting prompt and coordinate-shaped input.
- Add focused Unit coverage for malformed duration/reference semantics and two Integration cases proving cancellation propagation plus late read/late command isolation.

## 0.2.0 - Proposed

- Add `OperationCompilationBoundary` so an authoritative approval lookup and allowlisted compile must complete before a strict `operation.compiled/v1` wire can cross the command-consumer port.
- Add eight required `LocalCryptographicStorageSimulation` Integration cases with real P-256 P1363 signing, pinned key/state/revision/BOM verification, strict provider and consumer wires, exact identity scope, denial/shadow/forgery/stale-state attacks, unknown versions/actions/steps, coordinate rejection, deterministic replay, conflict quarantine, disk-flushed pending/atomic-rename crash-window recovery, restart reconstruction, and immutable result checks.
- Keep the evidence boundary explicit: the disposable signed file authority is not the policy-owned production adapter and does not provide PostgreSQL, Windows, ZennoDroid, device, canary, or scale verification.
- Align older unit assertions with the provider-owned `approval.decision/v1` exact `1.0.0` and non-empty optional authorization validation now present in the consumed contract pack.

## 0.1.0 - Proposed

- Tighten the compiled operation envelope to exact opaque identities and regenerate the canonical approval/operation/step vector from those stable wire values.
- Add `operation.compiled/v1` and a fixed action-to-step compiler.
- Reject forged, denied, shadow, unknown, and coordinate-fallback inputs.
- Bind operation and step IDs to the complete approved security context with domain-separated, length-prefixed canonical encoding.
- Reject null, empty, and oversized step argument values consistently with the v1 schema.
- Reject ill-formed UTF-16 instead of allowing replacement fallback to collapse distinct inputs into one digest.
- Align the proposed v1 schema and DTO on exactly one step and explicit snake_case JSON, with missing and unknown JSON members rejected.
- Share step-ID canonicalization between producer and contract validation, publish a machine-readable canonical specification and golden vector, and reject tampered step IDs.
- Snapshot and fully enforce consumed approval schema collection, pattern, uniqueness, and length constraints at the compiler trust boundary.
- Require `command-orchestrator` instruction rebinding when the proposed v1 schema, JSON mapping, or canonical ID specification changes.
- Bind the canonical-spec digest from the Manifest-registered schema so canonical changes enter the public-contract impact path.
- Add a strict duplicate-property-rejecting JSON codec and deep-frozen validated snapshots to reduce parser ambiguity and mutable-collection TOCTOU.
- Bound approval collection sizes, identifier lengths, and argument enumeration before allocation-heavy processing, using non-backtracking regular expressions.
- Document that canonical IDs are not approval provenance; authenticated approval evidence remains an R3 release blocker.
- Remove the public naked-`ApprovalDecisionV1` compilation path and require an injected authoritative reader that returns an ACTIVE, exact-scope immutable approval snapshot with matching canonical digest.
- Add required `approval_sha256` to the proposed v1 operation and use the shared contract canonicalizer to recompute OperationId from the complete output envelope plus approval commitment before recomputing StepId.
- Keep production release disabled until a real authenticated approval-reader adapter and consumer rebinding are complete.
- Raise the required diagnostic floor to the observed 13 Unit and 11 Contract cases so deleting the new provenance, canonicalization, JSON, or denial tests cannot leave the module gate green.
