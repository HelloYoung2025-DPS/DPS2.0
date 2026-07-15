---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: command-orchestrator
manifest: ./module.yaml
applies_to: .
---

# Command Orchestrator Agent Rules

## Scope

This module owns command idempotency, leases, bounded retry state, timeout recovery, and the versioned dispatch/receipt protocol. It does not approve actions, compile unknown steps, execute device actions, or decide that a native action succeeded.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, every provided/consumed contract, dependency graph, compatibility matrix, `tests/README.md`, and `operations/README.md` in order. Bind exact hashes before writing and rebind on affected-scope expansion.

## Communication and boundaries

Use only the declared `operation.compiled/v1`, `command.dispatch/v1`, `execution.authorization/v1`, and `command.receipt/v1` boundaries. The orchestrator uniquely owns the command and execution-authorization protocol so the executor can verify and emit protocol messages without creating a contract dependency cycle. Never read another module's tables, import internal types, share mutable static state, or turn model text into a command.

## Invariants

- `SOUL-ISO-001`: every idempotency key, lease, command, and receipt is scoped to one opaque Soul/device/account tuple.
- `CMD-IDEMP-001`: identical scoped delivery is a no-op; conflicting delivery is quarantined.
- `RESULT-VERIFY-001`: success requires a valid receipt with native and business-postcondition verification.
- `GBRAIN-READBACK-001`: no GBrain access.
- `EDGE-NORESTART-001`: no Windows/device evidence claims.
- A pre-dispatch lease timeout may requeue; a post-dispatch crash window becomes `RECONCILIATION_REQUIRED`.
- `UNKNOWN_OUTCOME` is never blindly retried.
- Out-of-order, forged, cross-scope, unknown-major, unknown-step, expired-lease, and over-attempt inputs fail closed.
- `execution.authorization/v1` uses its fixed domain, canonical byte encoding, P-256/SHA-256 algorithm, IEEE P1363 signature format, and exact Release BOM digest. Unknown algorithms or formats are not negotiated.
- Command Orchestrator constructs and remains the wire producer/contract owner of `execution.authorization/v1`; only the exact versioned `policy-approval` signer port may add its signature. The signer port protocol, module, key ID, returned canonical bytes, and signature are verified locally before relay. Never change the v1 producer to disguise the signer.
- Lease acquisition, binding, dispatch validity, and expiry recovery use PostgreSQL `clock_timestamp()` only after their command lock is held. Caller time is never an authority input.
- Every dispatch preserves the authoritative `approval_sha256`; the command digest and signed execution authorization bind it. Empty, substituted, or legacy approval digests fail closed.
- Execution authorization also binds the exact active-BOM generation and SHA-256 of its supervisor-issued opaque execution token. It never carries the raw token.

## Tests and rollout

Required checks release only on `PASS`. Cover duplicates, conflicts, timeout before dispatch, crash after dispatch, out-of-order receipts, safe bounded retries, unknown outcomes, and cross-scope attacks. The machine inventory `tests/required-security-tests.v1.json` is mandatory and deletion of any listed ID fails the Contract suite. Production is R3, exact-BOM-only, human-approved, canaried, kill-switch protected, and rollback routes only future commands.
