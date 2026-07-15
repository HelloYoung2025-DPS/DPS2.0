# Provided contracts

`control.plane.receipt/v1` is this module's deterministic receipt for accepted provider truth. Its contract pack includes the authoritative schema, strict bounded snake_case codec, and shared schema/codec adversarial corpus; missing, duplicate, additional, loose-ID, non-Zulu, malformed, or non-canonical external wire input fails closed.

`action.execution.promotion/v1` is owned by `policy-approval`; Control Plane Host consumes that public contract pack and is its only allowed wire producer. The contract does not itself grant execution and does not replace an independent release approval, signature verification, policy evaluation, or human production approval.

The same ownership pattern applies to `approval.submission.reconciliation/v1` and `approval.submission.recovery/v1`: they remain Policy-owned API contracts while Control Plane Host is their only allowed wire producer. They are therefore registered as consumed owner contracts, not duplicated as Control-provided schemas.
