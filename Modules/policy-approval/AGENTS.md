---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: policy-approval
manifest: ./module.yaml
applies_to: .
---

# Policy Approval Agent Rules

## Scope

This module is the deterministic authorization boundary for proposed actions. It owns policy evaluation, platform authorization checks, rate budget checks, and the kill switch decision. Models may propose but can never approve. This module does not compile steps, lease commands, execute actions, or access GBrain.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, all provided and consumed contracts, the dependency graph, compatibility matrix, `tests/README.md`, and `operations/README.md` in order. Bind exact hashes before writing and rebind on scope or consumer changes.

## Invariants

- `SOUL-ISO-001`: proposal and evaluation scope must match exactly.
- `CMD-IDEMP-001`: an approval ID is deterministic for the same scoped policy evaluation.
- `RESULT-VERIFY-001`: approval does not prove execution success.
- `GBRAIN-READBACK-001`: no GBrain access.
- `EDGE-NORESTART-001`: no Windows or device verification claims.
- Unknown actions, policies, majors, callers, and authorization states fail closed.
- Side effects require explicit platform authorization, positive rate budget, enabled policies, and a disabled kill switch.
- Authorization, platform status, rate budget, and kill-switch state come only from an injected trusted provider that verifies an authenticated Control Plane envelope; callers cannot self-report these values.
- `action.proposal/v1` is always shadow-only and has no execution authority by itself.
- A non-shadow approval requires an exact-scope, independently signed `action.execution.promotion/v1`. This module uniquely owns that API input contract while its wire `producer_module` remains exactly `control-plane-host`; promotion must bind the immutable proposal commitment, policy runtime revision, database-time validity window, Release BOM, Soul, device binding, platform account, trace, and idempotency key.
- Evaluation, promotion, and revocation signing authorities are separate production inputs. The module never owns a production private key and never lets a proposal signer promote its own proposal.
- A valid approval is still not permission to dispatch later: consumers must hold `approval.execution.fence/v1` through native dispatch so expiry, revocation, or runtime change cannot race the side effect. A fence lifetime is positive and never exceeds two seconds.
- Fence revalidation is read-only and never grants native-submission authority. Public native dispatch is disabled before PENDING; internal segmented lifecycle methods exist only for persistence and recovery tests.
- Production callers may reach only the no-callback `PolicyApprovalExecutionFenceLease.SubmitNativeOnceAsync` boundary, which remains fail-closed `WAITING_EXTERNAL` and creates no PENDING fact. Segmented begin, acknowledgement, and quarantine methods are internal/test-only and cannot grant production execution authority.
- Do not define, copy, compile, embed, or package `native.stop.*` DTOs, schemas, or corpora in this module. Executor Gateway is the contract owner; Policy Approval may consume only an exact signed owner artifact after a future declared compatibility edge and activation review.
- The dormant native-stop challenge ledger grants no table or function access to runtime, executor, reconciler, or recovery roles. A future activation migration must be separately reviewed and backed by real PostgreSQL 18.4 catalog, ACL, mutation, crash, duplicate, and race evidence.
- Internal segmented lifecycle tests may append PENDING and exercise ACK/UNKNOWN persistence, but they cannot be composed into a production native call. PENDING commit uncertainty never grants retry authority.
- `SUBMISSION_PENDING` is a durable uncertainty boundary, not a retry token. The exact pending attempt blocks acquire, revalidation, pre-submit, and automatic redelivery across process restarts until an exact signed acknowledgement or separately authorized reconciliation is committed.
- An exact duplicate begin may return the original pending state for recovery inspection, but its disposition is `ExistingUnknownSubmission` and `MaySubmit` is false. No caller may reinterpret a duplicate, timeout, cancellation, or missing acknowledgement as permission to call the native executor again.
- Only an independently signed `CONFIRMED_NOT_SUBMITTED` reconciliation followed by a distinct human recovery signature may authorize a fresh lease, fresh submission-attempt ID, next bounded attempt, and newly bound native request. The old attempt is immutable and never reopens; acknowledged or confirmed-submitted attempts can never be recovered for redelivery.
- Executor intent, native acknowledgement, reconciliation, human recovery, and policy submission-state signing authorities are distinct production inputs. Policy state receipts bind the exact command, lease, attempt, Soul/device/account/trace/idempotency scope, approval/proposal commitments, status/runtime revisions, Release BOM generation, and native-request commitments.
- Persistence authority is split too: submission begin/ack/quarantine, independent reconciliation, and human recovery use three pairwise-distinct, non-member PostgreSQL login roles with disjoint function grants. The generic policy runtime role can execute none of the lifecycle RPCs. Production must not collapse their credentials into one untrusted host.
- Acquire and revalidation hold the shared scope, approval, command, and attempt locks before checking lifecycle state. All transition validity is rechecked against PostgreSQL time after lock acquisition; a wait cannot extend a fence or signed receipt.
- Recovery authorization commits linearize against control-plane-host release binding transitions: the recovery commit transaction takes the shared per-device `release-binding:<device_binding_id>` advisory lock (the exact `hashtextextended(..., 0)` key derivation from the control-plane-host release-binding migration) after the release-binding baseline schema marker proves one shared database, and before the final active-binding comparison; missing coordination proof, lock-wait failure, or any comparison failure persists nothing. The per-device database advisory lock is acquired before any in-process authority gate on every path that touches both (global lock order); the comparison reader holds the gate only briefly and only while already holding the advisory lock.
- Public decision and fence JSON may be emitted or accepted only through their bounded strict codecs. Unknown, duplicate, missing, non-canonical, or schema/DTO-divergent payloads fail closed; PostgreSQL JSONB readback uses the codec's exact semantic mode rather than permissive default deserialization.
- Production still requires an Executor-owned, signed stop proof bound to attempt, native binding, worker incarnation, Release BOM and stop evidence, plus an explicitly injected process-root guardian or immediate host fail-fast. Policy Approval must not invent or publish that proof contract; shared mutable static registries are forbidden.

## Communication and boundaries

Communication is limited to the versioned inbound and outbound contracts declared in `module.yaml`. Do not read another module's tables or internal types, and reject unknown peers, callers, policies, or contract majors before producing a decision.

## Tests and rollout

Required checks release only on `PASS`. Test unknown policy, model and role attacks, kill switch, rate exhaustion, missing platform authorization, shadow denial, forged or stale promotion, signer and database-role separation, execution-fence races, concurrent acquire, idempotency, cross-scope isolation, all native-submission crash windows, exact duplicate begin, restart blocking, acknowledgement binding, forged reconciliation, recovery-scope reuse, lock-wait expiry, legacy overload removal, malformed direct RPCs, and recovery-authority separation. Production release, execution promotion, reconciliation, and recovery are R3 and require the declared authority, bounded canary, kill switch, and previous signed BOM rollback. A missing real PostgreSQL 18.4 target is a failed/infra Integration result, never a Mock substitute.
