---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-rollback-controller
manifest: ./module.yaml
applies_to: .
---

# Factory Rollback Controller Agent Rules

## Scope

This module plans and verifies stop-route-drain-reconcile-switch-verify rollback workflows. It distinguishes reversible routing/artifact changes from external side effects that require compensation and must never be described as rolled back.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, all contracts, dependency and compatibility sources, tests, and operations instructions in order. Bind hashes and rebind when scope expands.

## Invariants

- Treat the request as untrusted data. Approval, signature, actor role, required
  steps, and executable commands may come only from process-bound adapters and
  fixed code, never request fields.
- Stop new routing before draining and reconciling in-flight work.
- Ordinary module rollback has a logical deadline of no more than 300 seconds and fails closed when exceeded.
- Only an exact previous stable signed BOM is a switch target.
- Non-rollbackable posts, comments, messages, and external effects require an explicit compensation plan and outcome.
- `UNKNOWN_OUTCOME`, missing reconciliation, incomplete drain, digest mismatch, or failed postcondition cannot produce `ROLLED_BACK`.
- Persist `ROLLBACK_STEP_STARTED` before invoking a step. A recovered start
  without a verified observation is `UNKNOWN_OUTCOME` and must not be retried.
- Persist the terminal result in `factory-evidence-ledger` before returning it
  as complete. Same rollback ID plus same request hash is idempotent; a
  different hash is redacted, quarantined, and rejected.

## Communication and data

Use only owned JSON contracts and declared rollout/evidence interfaces. Do not
import provider internals, read provider tables, execute arbitrary shell/device
commands, or claim compensation was performed without exact verified receipts.

## Tests, rollout, and rollback

Test step order, five-minute logical deadline, duplicate execution, previous-BOM mismatch, incomplete drain, compensation-required outcomes, and verified rollback. The controller itself is rolled back by routing to the previous compatible module artifact while retaining plans/results.
