---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-upgrade-intake
manifest: ./module.yaml
applies_to: .
---

# Factory Upgrade Intake Agent Rules

## Scope

This module owns `upgrade.intent`. Major 2 is the only active validation and encoding path. A validated v2 object is still a `REQUESTED` optimistic claim; it does not prove baseline contract facts, candidate source bytes, test results, approval to release, or a production transition. Major 1 has frozen Schema bytes and may be inspected only for bounded routing identity before fixed quarantine metadata is produced. It has no validator, encoder, approval, implementation, or outbound domain path.

The module may validate and normalize intake syntax. It cannot edit product code, execute tests, create worktrees, issue evidence, approve a release, access the durable idempotency store, or run production commands. Release transitions and rollout commands belong to the Release Controller boundary, not Intake.

## Required reading before the first write

Read, in order: the repository root `AGENTS.md`; this file; `module.yaml`; every file in `contracts/provided/` and `contracts/consumed/`; `governance/modules/dependency-graph.yaml`; `governance/modules/compatibility.yaml`; `governance/policies/risk-policy.yaml`; `tests/README.md`; and `operations/README.md`. Bind exact hashes in an instruction receipt before writing. Rebind after any affected path, contract consumer, manifest, instruction, or diff-scope change.

## Invariants

- Reject R4 requests unconditionally. `requested_risk_tier` is a requester claim that the Impact Analyzer must independently derive and review.
- Accept only concrete canonical repository-relative paths with exactly one owner in the externally verified Manifest snapshot. Globs, aliases, hidden segments, traversal, control characters, and unknown paths fail closed.
- Bind the exact baseline commit, Manifest ownership snapshot SHA-256, and verification receipt into the wire object. Every expected contract source must also be in `requested_paths`.
- Contract changes are `UNVERIFIED_EXPECTATIONS`. Intake never renames them as verified facts. The Instruction Resolver verifies exact baseline facts at `INSTRUCTIONS_BOUND`; an independent candidate gate verifies candidate bytes at `CHANGESET_FROZEN`.
- Every public-contract change contains all fourteen v2 fields. `(contract_id, major)` is unique, and canonical sorting is deterministic.
- The only change kinds are `add-major`, `additive-schema`, `mode-transition`, and `introduce-quarantined-major`. The fourth kind may introduce an exact baseline-absent historical wire only as deprecated `quarantine-only`, with a domain-separated evidence digest and no runtime communication.
- A provided contract never uses `compat-read`. Allowed one-way transitions are active to quarantine-only or retired, and quarantine-only to retired. Retired and quarantined majors cannot be reactivated.
- `public_contract_changes_sha256`, `approval_subject_sha256`, and `upgrade_intent_sha256` use distinct hash domains. Production idempotency binds the full intent digest, not the component digest alone.
- The requester authentication digest binds requester identity, role, audience, canonical validity window, nonce, and external verification receipt. It intentionally does not claim to bind the approval set. The selected approval is separately bound by its exact receipt, nonce, approver, scope, intent, baseline, requested risk/stage, issuance/expiry, approval-subject digest, and full-intent digest.
- Authority wrappers and registries are process-composition/type constraints, not a sandbox against hostile in-process Python. Verification ports, authority instances, credentials, and durable replay state must be held by the trusted composition root.
- Exact duplicate intent plus full digest may be evaluated deterministically again. Same nonce or idempotency key with a different full digest must be rejected by the Factory Control Plane in the same PostgreSQL transaction; Intake has no durable ledger and cannot claim that proof.
- R2/R3 production-stage authorization must come from a distinct human release approver. Pending, rejected, expired, mismatched, or self-approved requests are not routable.
- Governance changes require a separately registered owner and run. This module cannot let governance approve itself.
- Never turn model text into shell, SQL, device, or deployment commands.

## Communication and boundaries

Current v2 intent may go only to the Instruction Resolver, Impact Analyzer, and Control Plane Host through declared authenticated edges. The Host may use it for orchestration and durable idempotency only; it must not treat raw expected claims as scope truth. Raw intent must never go directly to the Release Controller. Major 1 has no outbound communication.

Intake does not consume or produce `rollout.command` and has no direct Release Controller communication. Unknown peers, majors, modes, change kinds, identities, paths, receipts, and authorization claims fail closed. Do not import another module's internal types, read another module's store, or share mutable state.

## Tests, rollout, and evidence

Required tests release only on `PASS`. Unit tests cover authority-instance binding, expiration, approval replay, strict JSON bounds, concrete path ownership, all four change kinds, digest domains, v1 quarantine, and the external durable-idempotency boundary. Contract tests validate production output and assert Schema failure and production failure independently for the owned corpus.

The module is proposed, `releaseEligible=false`, and supports only `REPOSITORY_STATIC_VERIFIED` at this baseline. `CONTRACT_VERIFIED` requires separately issued raw evidence; durable replay/idempotency requires PostgreSQL integration evidence. Rollback routes to the previous signed Factory BOM, never reactivates v1, and the kill switch rejects new intake.
