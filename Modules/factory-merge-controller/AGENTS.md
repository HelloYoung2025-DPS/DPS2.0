---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-merge-controller
manifest: ./module.yaml
applies_to: .
---

# Factory Merge Controller Agent Rules

## Scope

This module decides whether an already-created integration merge head is eligible to move forward. It never merges branches, signs evidence, builds artifacts, approves a release, or accepts a collection of branch-local green results as proof for the merge head.

## Required reading before the first write

Read the root `AGENTS.md`, this file, `module.yaml`, every provided and consumed contract, `governance/modules/dependency-graph.yaml`, `governance/modules/compatibility.yaml`, the relevant tests, and `operations/README.md`, in that order. Bind exact hashes and rebind whenever the affected scope expands.

## Invariants

- Only evidence whose tested commit exactly equals the requested merge head may satisfy a required gate.
- Every required check is `PASS`; `SKIP`, `PARTIAL`, `NOT_RUN`, `INFRA_ERROR`, and missing evidence fail closed.
- Implementer, evidence issuer, merge decision issuer, and release approver are distinct identities.
- Contract conflicts, path ownership conflicts, stale instructions, unresolved conflicts, and an untested merge head are rejection reasons.
- A deterministic decision is not release approval and cannot promote verification above its raw evidence.

## Communication and data

Use only the versioned JSON contracts owned by this module. Do not import another module's internal types, read its database, use shared mutable state, or execute model-authored shell, SQL, device, or deployment commands.

## Tests, rollout, and rollback

Test merge-head mismatch, branch-green substitution, missing and non-PASS checks, conflicting paths/contracts, stale receipts, duplicate requests, and identity self-approval. Rollout remains disabled until a signed Release BOM enables the feature flag; rollback disables routing to this version and retains every decision as evidence.
