---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: windows-edge-worker
manifest: ./module.yaml
applies_to: .
---

# Windows Edge Worker Agent Rules

## Scope

This module processes already-authorized, versioned commands with leases, idempotency, bounded dispatch, native-result truth, and recovery. It cannot invent actions, approve side effects, or access GBrain.

## Required reading before the first write

Read the root AGENTS.md, this file, module.yaml, all provided and consumed contracts, dependency and compatibility sources, tests/README.md, and operations/README.md in order. Bind hashes and rebind on scope expansion.

## Invariants

- CMD-IDEMP-001: a duplicate idempotency key with the same request hash returns the recorded receipt; a conflicting hash is quarantined.
- RESULT-VERIFY-001: success requires a native success result and a verified business postcondition.
- UNKNOWN_OUTCOME is persisted for reconciliation and is never blindly retried.
- Unknown contract majors, actions, steps, identities, leases, or result states fail closed.
- A lease is bound to command_id, soul_id, device_binding_id, platform_account_id, and trace_id.
- Shadow mode cannot dispatch a real side effect.
- A production process starts with command intake stopped and must reconcile every prepared completion before any later intake decision.
- Only the Supervisor-owned, RSA-PSS signed `edge.worker.drain.directive/v1` with an exact active scope/deployment/epoch expectation may stop intake for cutover. Free-text `DRAIN` is not an authorization path.
- The Worker signs one Worker-only drain receipt, persists its exact raw UTF-8 wire as PREPARED, appends `WORKER_DRAINED` with the exact wire SHA through the narrow external Journal IPC, and returns it only after a matching durable Journal receipt is COMMITTED.
- A PREPARED or COMMITTED restart re-verifies canonical directive wire, exact expectation, pinned Supervisor key, and RSA-PSS signature through the owner continuation codec; it reuses the exact Worker wire and never creates a second randomized PSS signature.
- The Worker never requests, receives, persists, parses, verifies, or transfers the Journal rich drain attestation, and never holds a Journal signing or quarantine-administration key. Supervisor obtains and correlates that independent proof directly.
- Missing signed directive, durable Journal receipt, Worker signing authority, Windows ABI evidence, Supervisor cutover authorization, frozen route assignment, authenticated live Worker IPC, or one shared fixed Release-BOM-protected launch ABI keeps intake, stage, and cutover fail closed.
- The Supervisor currently starts the exact Worker executable with zero caller arguments, while this Worker accepts only `--production-reconcile --state-dir <absolute-private-directory>` and still rejects Windows. Preserve this incompatibility as `WAITING_EXTERNAL` until both module owners freeze the same launch ABI; do not weaken either boundary or count a negative launch test as stage, health, cutover, or route-transition evidence.
- `native.stop.proof/v1` remains byte-owned by `executor-gateway` and is deprecated `quarantine-only` in this Worker. The internal tombstone must fail before runtime-identity read, native stop, signing, verification, or persistence. The bounded internal store may decode an already-existing artifact only through the owner codec and return digest/size/disposition metadata; it must not create, lease, emit, replay, or expose raw wire bytes.
- A v1 artifact can never authorize `UNKNOWN_SUBMISSION`, become business success, or provide positive compatibility evidence. Caller booleans, a local Schema/DTO/digest/codec fork, and any v1 runtime communication edge are forbidden.
- The proposed Policy-owned v2 challenge/proof, Release-BOM authority receipt, Supervisor route assignment, and live Worker IPC remain `WAITING_EXTERNAL` until their owners freeze exact Worker-facing APIs. Do not bind temporary files or infer missing contracts.

## Communication and boundaries

Communication uses declared versioned contracts and injected transports only. The Journal client may expose only append plus read-only readiness; a client that also exposes attestation or quarantine administration is rejected. Do not read another module's tables or files, import its internal types, hold GBrain credentials, issue arbitrary shell, or downgrade an unknown selector to coordinate clicking.

## Tests and evidence

Test duplicate delivery, conflicting duplicates, expired leases, crash windows, timeouts before and after dispatch acknowledgement, offline recovery, UNKNOWN_OUTCOME reconciliation, postcondition failure, shadow suppression, process fencing, open-handle/path identity, effective-user ownership, symbolic and hard links, non-regular files such as FIFOs, permissions, resource limits, Journal quarantine, and real local-process restart through the production decision path. Mac process tests remain simulation evidence and never prove Windows or device behavior.

## Rollout and rollback

Workers run in immutable A/B version directories selected by the supervisor. Stop intake, drain, reconcile, and route back to the previous exact artifact; retain the local journal for recovery.
