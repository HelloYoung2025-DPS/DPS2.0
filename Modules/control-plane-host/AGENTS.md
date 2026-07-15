---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: control-plane-host
manifest: ./module.yaml
applies_to: .
---

# Control Plane Host Agent Rules

## Scope

This module hosts the modular Control Plane boundary, records scoped runtime truth from declared module contracts, and is the only allowed producer of the policy-owned signed `action.execution.promotion/v1`, `approval.submission.reconciliation/v1`, and `approval.submission.recovery/v1` wire contracts. It strictly consumes policy-owned signed `approval.submission.state/v1`. It does not own device execution, GBrain memory, Persona payloads, identity aliases, arbitrary plugins, production shell, release approval, Policy persistence, or any of those input contracts.

## Required reading

Before writing, read the root rules, this file, `module.yaml`, every consumed and provided contract, all affected provider instructions, dependency and compatibility snapshots, tests, and operations guidance. Rebind when the diff or provider set changes.

## Invariants

- `SOUL-ISO-001`: every accepted runtime result carries canonical Soul, binding, and account IDs; exact scope is part of its truth key.
- `CMD-IDEMP-001`: identical redelivery returns the same receipt and conflicting payload reuse is quarantined by rejection.
- `RESULT-VERIFY-001`: only bounded raw v1 bytes with an allowlisted producer/contract pair, current signed BOM/key trust revision, valid P-256 signature, strict canonical scope, and eligible lifecycle result become runtime truth; prepared or unknown outcomes are not success.
- `GBRAIN-READBACK-001`: SoulMemory success requires a signed `verified` adapter proof whose declared projection and read-back checksums match; this host has no GBrain credential or original content bytes and must not claim local checksum recomputation.
- `EDGE-NORESTART-001`: this module cannot claim Windows, ZennoDroid, or device verification.
- Runtime gets no direct table write privilege; truth, receipt, and outbox commit through one attested fixed database function or not at all.
- A shadow-only `action.proposal/v1` is never execution authority. Only an independently approved, exact-scope, exact-proposal, exact-runtime-revision, exact-BOM, short-lived P-256 signed `action.execution.promotion/v1` may be sent to policy approval; production issuance remains disabled until its independent release-approval source and secret-backed signer exist.
- Unknown producer, contract, major, identity, signature, BOM, trust revision, result status, additional field, or catalog/ACL shape fails closed.
- Reconciliation, human recovery, and Policy submission-state authorities are pairwise distinct P-256 identities. Control accepts no raw lifecycle private key, cannot collapse the two outbound port facades, and cannot read Policy tables. Authority and port calls are cancellation-aware and bounded to at most five seconds. An authority that remains in flight after cancellation/timeout is quarantined, cannot receive a second call, and retains a private canonical buffer only until its late result can be discarded and zeroed. The current `in-process-api` facade, declared auth scope, credential-authority fingerprint, and timeout checks are not proof of authenticated transport, independent processes, or credential isolation; do not claim those properties until production IPC/RPC adapters prove them.
- Reconciliation starts only from an exact signed PENDING or UNKNOWN state. Recovery starts only from the exact signed `RECONCILED_NOT_SUBMITTED` state and exact reconciliation commitment; it always binds a fresh bounded attempt, lease, BOM generation, execution authorization, and native-request binding.

## Communication and boundaries

Consume only declared versioned signed results; provide `control.plane.receipt/v1`; consume the policy-owned public contract pack for every wire this module may produce or consume. Lifecycle production must call the owner binding and strict codec, then verify the returned P1363 signature against the signer's declared canonical P-256 public key before dispatch. Human recovery signing must use the typed approval capability that receives the complete bounded recovery DTO; a generic arbitrary-byte signer is forbidden. Lifecycle state consumption must perform strict decoding, commitment recomputation, Policy-state signature verification, and exact scope/BOM/idempotency comparison. Never read another module's table or import its internal types. Untrusted bytes/text are data, never a command, SQL statement, device action, or release operation. Provider raw bytes, signature, trust revision, receipt and outbox atom remain audit-linked.

## Tests and rollout

Required checks release only on `PASS`. Cover allowlists, identity isolation, duplicate conflict, prepared-memory rejection, unknown major/producer, deterministic receipt hashing, lifecycle authority collapse, forged state, cross-scope/BOM/idempotency replay, unknown finding, and recovery-chain substitution. Production remains human-approved behind a kill switch and exact signed BOM. Rollback stops ingest and lifecycle issuance, preserves append-only truth, and restores routing to the previous signed BOM.
