---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-impact-analyzer
manifest: ./module.yaml
applies_to: .
---

# Factory Impact Analyzer Agent Rules

## Scope

This module converts only process-bound `VerifiedIntentV2`, `VerifiedReceiptV2`, and `VerifiedImpactPolicyV2` capabilities into deterministic `module.change.plan/v2`. It is read-only: it cannot modify a worktree, execute tests, issue evidence, assign itself a role, approve a release, or authorize a product side effect.

`upgrade.intent/v1`, `instruction.receipt/v1`, and `module.change.plan/v1` are byte-frozen historical contracts. They are `deprecated/quarantine-only`, have no runtime communication edge, and must never enter the v2 analyzer.

## Required reading before the first write

Read, in order:

1. The root `AGENTS.md`.
2. This file and `module.yaml`.
3. Both consumed v2 Schemas, the provided v2 Schema, and the frozen v1 Schemas.
4. The exact-major dependency graph and compatibility matrix.
5. `operations/trusted-impact-policy.v2.json`, tests, current evidence, rollout, kill-switch, and rollback instructions.

When an Intent, Receipt, plan, authority wire, policy shape, exact-major declaration, or consumer changes, rebind all affected instructions before another write.

## Trust invariants

- Public JSON, a self-consistent ID, a recomputed SHA, and an arbitrary callback are not authority.
- `analyze` accepts only exact capabilities issued by this analyzer instance's fixed authorities. Plain Mapping, lambda verifier, direct constructor, Authority-A capability presented to Authority-B, raw-byte swap, same receipt ID with different bytes, wrong producer/audience/major, and `now == expires_at` all fail closed.
- The local HMAC path proves only process composition and attack behavior. A portable signature, mTLS plus durable receipt lookup, or equivalent cross-process authority is still missing. Every plan therefore says `portable_trust_status=WAITING_EXTERNAL`, `release_eligible=false`, and `side_effects_authorized=false`.
- The stable policy owns roles, check catalog, change-kind risk floors, risk/stage matrix, and stage checks. Candidate input cannot lower them.
- The repository policy is `non-production-template`; it may authorize only deterministic `development` or zero-side-effect `shadow` analysis.
- Policy mappings and sequences remain deeply immutable after sealing. Implementer, tester, auditor, and release approver identities remain disjoint.

## Scope, contract, and currentness invariants

- `requested_target_modules` and `authorized_write_paths` are the exact write permission. They never expand from `scope`.
- `scope` is the instruction/read-impact boundary. Independently recomputed impact must equal `receipt.scope` exactly; a subset or superset is stale.
- Each path has exactly one current Manifest owner. Each contract identity is exact `(contract_id, major, mode, status, source, owner)`; family-name matching is forbidden.
- Active/compat-read declarations may participate in runtime edges. Quarantine-only and retired declarations are non-runnable and cannot gain authority through an expectation.
- `UNVERIFIED_EXPECTATIONS` remain separate from verified Git baseline facts. They cannot authorize current mode, source bytes, tests, roles, risk, stage, writes, release, or side effects.
- Plan v2 binds the full Intent SHA, full Receipt SHA, baseline, diff fingerprint, instruction scope and digest, write modules, exact authorized paths and digest, combined write-scope digest, exact-major expectations, baseline-facts digest, policy, risk, stage, checks, and dependency waves.
- Verify bound files, Git HEAD/index/status/diff, Manifest declarations, exact consumers, scope, and policy before use and again before returning. A race produces no plan.
- Dependency waves are topological: providers complete before consumers; only dependency-independent modules share a wave.

## Tests and rollout

Required tests use isolated temporary Git repositories and the real Intake→Resolver→Impact path for `add-major`, `additive-schema`, `mode-transition`, and `introduce-quarantined-major`. They must also cover Mapping/lambda/constructor/authority attacks, replay, expiry equality, wrong audience/producer/major, raw swap, scope too many/too few, exact-major separation, index-only drift, TOCTOU, deterministic output, policy immutability, and shadow zero side effects.

Mock or process-local tests are repository diagnostics only. They do not prove portable trust or production readiness. Rollout is shadow-only behind `factory_impact_analyzer_v2`; `factory_disable_impact_analysis` stops new plans. Rollback discards unexecuted v2 plans and returns the whole compatible Factory group to the previous signed BOM; it never reactivates v1 runtime paths.
