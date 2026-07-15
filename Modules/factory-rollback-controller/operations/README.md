# Factory Rollback Controller operations

## Current status

The Python controller and repository-static tests are buildable. The module is
still `proposed`, is not release eligible, and has no production, canary,
Windows, device, or scale evidence.

## Composition requirements

The host must inject four separately controlled ports:

1. A previous-stable Release BOM verifier that returns a
   `VerifiedStableBom` only after checking the exact BOM bytes, digest,
   signature, signer trust, and `STABLE` status.
2. A human R3 rollback authority that returns a `VerifiedRollbackGrant` bound
   to the exact rollback, upgrade, rollout event, request hash, plan hash,
   target digest, rollback unit, and fixed step list. The authority verifies the
   rollout event lineage externally; the approver identity must differ from the
   controller identity.
3. A declarative step executor accepting only `StepInstruction.step` enum
   values. The adapter must not accept command text, shell fragments, SQL,
   device instructions, or model-generated executable content.
4. The external append-only evidence ledger. A terminal result may be returned
   only after `ROLLBACK_RESULT_RECORDED` is durably appended.

## Execution paths

Rollbackable routing and artifact changes use exactly:

`STOP_ROUTING -> DRAIN -> RECONCILE -> SWITCH_BOM -> VERIFY`

Non-rollbackable external effects use exactly:

`STOP_ROUTING -> DRAIN -> RECONCILE -> COMPENSATE -> VERIFY`

The latter result is `COMPENSATED`, never `ROLLED_BACK`. Public posts,
comments, messages, or other external history remain external facts even after
a verified compensating action.

`external_effects` contains only typed opaque references and
`compensation_plan` contains only a stable plan ID. The executor resolves that
ID from a separately signed declarative registry; neither field may contain
natural-language instructions or executable content. Step and terminal reasons
are bounded reason codes, never copied native output, OCR, UI text, or model
text.

Every step writes a durable `ROLLBACK_STEP_STARTED` event before execution.
If recovery finds `STARTED` without `OBSERVED`, the outcome is unknown and the
step is not retried. The logical deadline is captured once and may never exceed
300 seconds; the absolute logical deadline is included in every declarative
step instruction so the adapter can enforce it while executing. A recovered
logical clock moving backwards fails closed.

## Canary and kill switch

- Feature flag: `factory_rollback_controller_v1`.
- Kill switch: `kill_factory_rollback_controller` stops admission of new
  rollback plans. It does not delete or rewrite in-progress evidence.
- First rollout is simulation-only failure injection. Simulation evidence does
  not establish `CANARY_VERIFIED` or any higher level.
- A production drill requires a separately verified human R3 approval, an
  exact signed previous-stable BOM, and a declared rollback unit.

## Failure and recovery runbook

- Request hash conflict: the controller appends only accepted/conflicting
  hashes and `ROLLBACK_ID_HASH_CONFLICT`, then quarantines the request.
- Ledger read/hash/sequence failure: stop immediately and repair the evidence
  service; never continue from a guessed sequence.
- Ledger append failure: do not report completion. Re-read the stream and
  resume only from exact durable events.
- Incomplete drain or reconciliation: result is failure; do not switch BOM.
- `UNKNOWN_OUTCOME`: do not retry. Escalate for deterministic reconciliation.
- Target or active digest mismatch: stop routing and retain the current state;
  do not label the operation rolled back.
- Controller rollback: route new requests to the previous compatible signed
  controller artifact and retain the external stream for recovery.
