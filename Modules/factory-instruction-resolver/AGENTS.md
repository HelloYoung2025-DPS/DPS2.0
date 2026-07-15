---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-instruction-resolver
manifest: ./module.yaml
applies_to: .
---

# Factory Instruction Resolver Agent Rules

## Scope

This module binds root and module instructions, exact contract majors, Manifests, compatibility data, tests, operations, a process-bound verified Intake capability, verified baseline facts, and optimistic future expectations to `instruction.receipt/v2`. `resolve` returns an unforgeable process-bound `VerifiedInstructionReceiptV2`; its `canonical_receipt()` is the public JSON projection, but bare JSON is never currentness authority. The module reads Git metadata but never edits product code, approves evidence, verifies a completed changeset, or releases artifacts. `instruction.receipt/v1` and `upgrade.intent/v1` are historical quarantine-only wire identities and have no runtime route.

## Required reading before the first write

Read the repository root `AGENTS.md`; this file; `module.yaml`; every provided and consumed contract; `governance/modules/dependency-graph.yaml`; `governance/modules/compatibility.yaml`; `governance/policies/risk-policy.yaml`; all tests; and `operations/README.md`. Bind exact hashes before another write and rebind after any affected instruction, Manifest, contract, or diff-scope change.

## Invariants

- Root instructions bind first, followed by every impacted module instruction in stable module order.
- Public-contract impact is indexed by exact `(contractId, major)`; a v1 consumer is never inferred to consume v2.
- Future contract changes remain `UNVERIFIED_EXPECTATIONS`; only facts read from the exact baseline commit may appear under `verified_baseline_contract_facts`.
- A historical major absent from the baseline may only use `introduce-quarantined-major`, with the canonical quarantine proof, `quarantine-only/deprecated`, no active declaration, and no exact-major runtime communication.
- Every communication edge must name an exact locally declared contract major and have a reciprocal peer edge; hidden, one-sided, duplicate, quarantine-only, or retired routes fail closed before impact resolution.
- The full `upgrade_intent_sha256`, requester authority proof, Manifest ownership proof, requested risk, stage, and authorization disposition are bound into every v2 receipt.
- Production `resolve` accepts only `VerifiedUpgradeIntentV2` issued by the Resolver's fixed `UpgradeIntentTrustAuthority`; a plain Mapping, direct constructor, callback verifier, capability from another authority, changed raw bytes, non-canonical JSON, stale trust record, or caller-supplied time fails closed.
- Every trust receipt cache lookup occurs only after the complete attestation has been reverified and every verified authority field has been compared with the cached capability; a receipt ID alone can never retrieve a capability.
- Receipt currentness accepts only `VerifiedInstructionReceiptV2` issued by the same Resolver's private fixed authority. Canonical bytes, full SHA-256, receipt ID, producer, major, issuer, audience, issued/expiry time, nonce, generation, and status are inseparable; plain JSON, direct construction, cross-authority use, raw swapping, or field mutation returns no derived receipt.
- `requested_target_modules` and `authorized_write_paths` are the exact caller-requested, Manifest-owned write boundary. `scope` is the wider instruction/read/test impact set and never grants write access to affected consumers.
- Communication direction follows the exact Schema `producer_module`; relay is allowed only when `preserveProducer=true` and the relay has the exact consumed declaration.
- Git blob identity, SHA-256, baseline commit, source state, order, worktree bytes/mode, exact index entries, porcelain-v2 status, and diff fingerprint are recorded.
- Git Blob and SHA-256 must be computed from the same bytes. A changed bound file, expanded or racing diff, changed consumer set, symlink, path traversal, glob, or unknown major makes the prior receipt `STALE` or aborts binding.
- Validation accepts only a strict JSON, Schema-valid, canonical-ID `BOUND` v2 receipt inside that issued capability. Every receipt timestamp is parsed with deterministic real-calendar UTC logic independent of optional format packages; impossible dates and expiry equality fail closed. A schema-invalid, unknown-major, already-STALE, expired, or unrecomputable input returns no derived receipt; a valid prior receipt may become `STALE` only by preserving its original receipt ID and source authority bindings in a newly issued capability.
- Replay, capability, and fingerprint registries have a hard maximum of 4096 active records, prune by the earliest trusted authority expiry, and reject new work at quota. Expired capabilities are removed and cannot be replayed.
- A receipt proves what was read; it never proves implementation correctness.

## Stable policies

- `SOUL-ISO-001`: identity fields are opaque routing identifiers, not data access.
- `CMD-IDEMP-001`: identical inputs produce the same bound-file and diff hashes.
- `RESULT-VERIFY-001`: instruction binding is not test evidence.
- `GBRAIN-READBACK-001`: this module has no GBrain credentials.
- `EDGE-NORESTART-001`: this module cannot issue Windows or device evidence.

## Communication, tests, and rollout

Consume runtime work only through sealed canonical `upgrade.intent/v2`; provide the canonical JSON projection of issued `instruction.receipt/v2` only to declared exact-major peers. v1 declarations remain quarantine-only and must have no communication edge. Unknown peers, majors, modes, paths, producers, pending/rejected approvals, impossible dates, expired auth/Manifest/approval facts, unsigned receipt JSON, or mismatched full-intent digests fail closed. Required tests cover the real Intake encoder, exact 30/8/14 surfaces, both capability authorities, exact write scope, exact-major consumers, four contract-change kinds and hash domains, calendar and expiry equality, bounded state, baseline truth, content drift, controlled races, and path attacks. Rollback routes to the previous signed Factory BOM; `factory_disable_instruction_binding` rejects new work. Both local capabilities are process-bound diagnostics, not portable production trust: cross-process signature or mTLS plus receipt lookup remains `WAITING_EXTERNAL`.
