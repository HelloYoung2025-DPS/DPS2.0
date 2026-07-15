---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: operation-compiler
manifest: ./module.yaml
applies_to: .
---

# Operation Compiler Agent Rules

## Scope

This module compiles an already approved action into an allowlisted, versioned `operation.compiled/v1`. It has no approval authority and cannot lease or execute a command. Unknown actions and steps fail closed; there is never a coordinate-click fallback.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, provided and consumed contracts, dependency graph, compatibility matrix, `tests/README.md`, and `operations/README.md` in order. Bind exact hashes and rebind when scope expands.

## Invariants

- `SOUL-ISO-001`: approval and compiled operation preserve one exact Soul/device/account scope.
- `CMD-IDEMP-001`: compilation is deterministic for a scoped approval.
- `RESULT-VERIFY-001`: compiled steps define a required postcondition but do not prove it.
- `GBRAIN-READBACK-001`: no direct GBrain access.
- `EDGE-NORESTART-001`: no Windows/device verification claims.
- Only deterministic-policy-engine approvals with `APPROVED` status compile.
- The public compiler accepts only an exact lookup request and must asynchronously obtain an `ACTIVE` immutable approval snapshot from `IAuthoritativeApprovalReader`; a caller-supplied `ApprovalDecisionV1` is never a production compile input.
- The module-owned boundary enforces one 2000ms deadline across authoritative read, compilation, and command handoff. Both ports receive cancellation, and every result that terminates after the deadline must enter the required quarantine sink rather than become caller-visible success.
- `duration_ms` is canonical decimal `1..600000`; `selector_ref` and `value_ref` use the Planner-aligned 128-character opaque machine-reference syntax. Prompt text and coordinate-shaped references fail closed.
- Approval SHA-256, OperationId, and StepId are deterministic commitments, not authentication. Production remains ineligible until the authoritative reader is wired to an authenticated policy-approval receipt source.
- Reject shadow, unknown action/step/parameter/major, missing platform authorization, coordinate keys, and mutable fallback logic.

## Communication and boundaries

Communication is limited to the versioned contracts and peers declared in `module.yaml`. The compiler consumes an approval decision and emits an allowlisted operation only; it must not read another module's tables or internal types, and unknown peers or contract majors fail closed.

## Tests and rollout

Required checks release only on `PASS`. Cover unknown action/step, denied/forged/shadow approval, side-effect authorization, deterministic compilation, no-coordinate fallback, and scope preservation. Production rollout is R3 with human approval, shadow comparison, bounded canary, kill switch, and previous BOM rollback.
