---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: executor-gateway
manifest: ./module.yaml
applies_to: .
---

# Executor Gateway Agent Rules

## Scope

This module is the only modern boundary that may ask an injected native executor to perform an already authorized command. It validates dispatch authority, protocol, lease, scope, native result, and business postcondition. It owns no policy, retry, lease, selector fallback, Persona, memory, or GBrain access.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, all provided/consumed contracts, dependency graph, compatibility matrix, `tests/README.md`, and `operations/README.md` in order. Bind exact hashes and rebind on scope or consumer changes.

## Communication and boundaries

Accept only `command.dispatch/v1` plus `execution.authorization/v1` from authenticated Command Orchestrator scope. Consume `approval.execution.fence/v1` and the policy-owned `approval.submission.intent/v1`, `approval.submission.acknowledgement/v1`, and `approval.submission.state/v1` lifecycle only through the composition-fixed Policy Approval provider; callers cannot provide revisions, requests, lifecycle state, authorizations, or leases. Use only the injected trusted clock; callers never provide execution time. Before any native call, require a cryptographically authenticated, anti-rollback active-BOM generation and opaque 256-bit execution token whose digest is bound by the signed authorization. The native transport consumes that exact token and returns the executor-owned `native.submission.ack/v1`; its schema, privacy class, durability, full scope, BOM, lifecycle, submitted-request, and canonical acknowledgement digest must match exactly. Wrap independently reported native scope in `native.result/v1`; return only `command.receipt.signed/v1`. Never read another module's tables, import internal types, share mutable static state, access GBrain, or translate unknown text/steps into native operations.

## Invariants

- `SOUL-ISO-001`: dispatch authorization, native result, postcondition, and receipt scope match exactly.
- `CMD-IDEMP-001`: execute only the leased command ID; retry policy remains with Command Orchestrator.
- `RESULT-VERIFY-001`: success requires native success and verified business postcondition.
- `GBRAIN-READBACK-001`: no GBrain credentials or network.
- `EDGE-NORESTART-001`: no Windows/device verification claims from mocks.
- Unknown action/step/major, expired lease, shadow mode, wrong caller/scope/BOM, native mismatch, timeout, and missing evidence fail closed.
- Authorization uses the owner-controlled `execution.authorization/v1` domain, canonical encoding, algorithm, and signature format. It is accepted only through an injected verifier; the signed BOM digest is then compared with independently read active-BOM truth and caller-provided strings are never trusted directly.
- The exact approval execution fence is acquired, scope-checked, and revalidated immediately before the durable PENDING transition. Executor creates a requestless inert native capability before PENDING; it cannot receive a command or cross the device boundary. Only `SubmitFirstByteAsync` on that inert capability may cross the native boundary after Policy commits the exact PENDING state and retains its cross-commit session guard. A returned or cold Task is not evidence of submission.
- Native timeout, cancellation, exception, null, or invalid acknowledgement never authorizes `UNKNOWN_SUBMISSION`, guard release, retry, or success. The attempt remains durable PENDING, the exact lease/attempt/task is transferred to a process-rooted guardian, and the only receipt result is non-retryable `UNKNOWN_OUTCOME` with `WAITING_EXTERNAL`.
- Existing PENDING and historical UNKNOWN states are read-only blocking evidence. They invoke no native callback, remain non-retryable, and return `WAITING_EXTERNAL` after exact scope/state validation. A provider cannot inject a new UNKNOWN state through the callback path.
- `native.stop.proof/v1` is deprecated and byte-frozen as `quarantine-only`. Runtime code must not emit it, request it, verify it for domain state, accept it as a receipt, release a guard from it, permit retry from it, or treat it as action/business success. The owner corpus and freeze manifest permit bounded identification and quarantine metadata only; they are not authority evidence.
- Provider substitution, guarded-provider failure after possible PENDING, guardian registration failure, or invalid retention evidence invokes the constructor-fixed host fail-stop authority; it must never produce a signed receipt.
- The constructor-fixed production adapter must verify Policy Approval signatures and canonical digests, compute the fence-request digest from its own fixed lease request, exact-read authoritative proposal state, and keep the owner-coordinated guard through durable ACKNOWLEDGED or process death. A pending wrapper is valid only for owner disposition `Inserted` with `MaySubmit=true`; exact existing PENDING invokes no callback. No production PostgreSQL/Windows composition or Policy-owned replacement recovery authority has been verified, so this module remains `releaseEligible=false`.
- `SUBMISSION_PENDING` and `SUBMISSION_ACKNOWLEDGED` both forbid same-attempt native resubmission. A confirmed-not-submitted recovery requires the policy-owned reconciliation/recovery lifecycle, a new lease, and the immediately following bounded attempt; Executor Gateway never invents or self-authorizes recovery.
- The gateway rechecks trusted time after authorization, after the active-BOM read, after native execution, and before the final receipt. Expiry after a possible side effect is `UNKNOWN_OUTCOME`, never success or blind retry.
- `native.result/v1` must independently report the exact command, lease, attempt, Soul/device/account, trace, idempotency key, active-BOM digest, generation, token digest, and ordered step sent to native. Gateway-populated scope is forbidden.
- The active generation is reread after native execution and immediately before a successful receipt. Any absence or generation/BOM change after a possible side effect is non-retryable `UNKNOWN_OUTCOME`.
- `command.dispatch/v1` and `native.result/v1` each contain exactly one ordered step in v1. Multi-step or reordered input fails closed.
- Native `UNKNOWN` and timeouts return `UNKNOWN_OUTCOME`; no local retry.

## Tests and rollout

Required checks release only on `PASS`. Use labelled fakes for false-success, timeout, unknown native outcome, unknown step, shadow side effect, role/BOM/scope attack, exact success, concurrent existing-state replay, and restart blocking. Contract tests must freeze the legacy v1 bytes and attack duplicate fields, unknown fields, invalid UTF-8, oversize input, identity leakage, unknown major, non-canonical versions, replay, and runtime reflection surfaces. The machine inventory `tests/required-security-tests.v2.json` is mandatory; deletion of any listed ID or moving it to a Category other than its declared Category fails the Contract suite. Fakes cannot satisfy Windows or device gates. Production is R3 and human-approved.

## Rollout and rollback

Rollout requires the exact signed BOM, a bounded human-approved device cohort, and `kill_executor_gateway`. Rollback stops new dispatch and restores previous routing; it never claims that an external side effect was undone. Every `UNKNOWN_OUTCOME` must be reconciled before any later retry.
