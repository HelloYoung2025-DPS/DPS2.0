---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-worktree-manager
manifest: ./module.yaml
applies_to: .
---

# Factory Worktree Manager Agent Rules

## Scope

This module creates declarative `worktree.plan/v1` records and externally persisted `worktree.lease/v1` records. Its separately reviewable `GitWorktreeMaterializer` host component may execute only fixed Git argv and externally bound test argv with `shell=False`; it never accepts arbitrary Git, test, or shell commands.

## Required reading before the first write

Read root and module AGENTS, this Manifest, every contract, dependency and compatibility data, risk policy, tests, and operations in order. Bind hashes before writes and rebind after scope, ownership, contract, or instruction changes.

## Invariants

- One module has one worktree and one trusted writer per plan.
- Public contract sources have one contract worktree and one contract architect.
- Every planned path is owned by its Manifest owner; traversal, hidden state, symlinked repository paths, and ownership overlaps fail closed.
- Lease, path lock, contract lock, idempotency, and fencing truth lives in the isolated external PostgreSQL 18.4 Factory database. `ExternalSqliteLeaseStore` is a dev-only substitute and cannot issue production evidence.
- Fencing tokens increase monotonically; an expired or superseded writer can never regain authority with an old token.
- Plan creation is declarative and never executes request-provided commands.
- Materialization uses the previous stable Factory process, an exact baseline, Manifest-owned paths, active PostgreSQL fencing facts, one writer per worktree, dependency waves, and a merge-head retest. Conflicts, stale baselines, path expansion, test failure, and stale fencing stop the merge.

## Stable policies

- `SOUL-ISO-001`, `CMD-IDEMP-001`, `RESULT-VERIFY-001`, `GBRAIN-READBACK-001`, and `EDGE-NORESTART-001` remain mandatory.

## Communication, tests, and rollout

Consume `instruction.receipt/v1` and `module.change.plan/v1`; provide `worktree.plan/v1` and `worktree.lease/v1`. Test transactional contention, lease expiry, stale-writer fencing, path overlaps, traversal, symlinks, role origin, deterministic planning, two genuinely parallel worktrees, dependency ordering, merge-head retest, real PostgreSQL transactions, and process restart. The required PostgreSQL suite fails with `INFRA_ERROR` when `DPS_TEST_POSTGRES_URI` or the locked driver is absent; it is never skipped. Rollback routes to the previous signed manager and revokes active leases; `factory_revoke_all_worktree_leases` is the kill switch.
