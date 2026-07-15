---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-artifact-builder
manifest: ./module.yaml
applies_to: .
---

# Factory Artifact Builder Agent Rules

## Scope

This module converts an approved merge head and existing module payload into immutable digest-addressed descriptors, SPDX JSON SBOMs, and build provenance. It never invents, simulates, or applies a signature; signing belongs to an external controlled signer.

## Required reading before the first write

Read the root `AGENTS.md`, this file, `module.yaml`, all contracts, the dependency graph, compatibility matrix, tests, and `operations/README.md`, in order. Bind exact hashes and rebind on scope expansion.

## Invariants

- Artifact bytes are hashed from disk and re-read before descriptor publication.
- Repository and output paths are pinned with descriptor-relative, no-follow opens; path-namespace drift fails the build.
- Artifact, manifest, Git listing, source-file count, per-file bytes, and total source bytes remain bounded by reviewed hard limits.
- Artifact identity is the exact SHA-256 digest, never `latest`, a mutable tag, or an unsigned alias.
- The signed merge decision must pass its complete v1 runtime shape and match the build request's Soul, device, account, trace, integration commit, and temporal order.
- The requested module version must equal the exact version in the Manifest whose bytes match the signed integration commit.
- Every `build_id` is durably and atomically bound to the exact validated request, trusted decision, artifact, source tree, module version, and integration commit before any output is published. Exact retries are idempotent; divergent reuse fails closed.
- Production uses the PostgreSQL claim function through a distinct least-privilege runtime identity. The in-memory registry is unit-test-only and is never a production fallback.
- Every production claim re-attests exact PostgreSQL 18.4, `NOINHERIT`, zero role memberships, zero elevated role flags, zero direct table/schema-create privilege, and sole function execution privilege. A migration/admin connection is rejected before claiming.
- SBOM and provenance describe the exact payload and merge commit.
- Descriptor signature status is always `UNSIGNED_AWAITING_EXTERNAL_SIGNER` in this module.
- A build failure, digest drift, missing source, or unapproved merge decision fails closed.

## Communication and data

Exchange only declared JSON contracts. Do not import provider internals, modify source inputs, retain production secrets, or execute model text as a build/deploy command.

## Tests, rollout, and rollback

Test byte-level digest accuracy, deterministic metadata, digest drift, path and output-directory swaps, crash windows, duplicate Git paths, resource limits, mutable identifiers, unsigned status, SBOM/provenance linkage, rejected merge decisions, exact build-ID replay, conflicting build-ID reuse, concurrent claims, restart recovery, and database ACL/append-only enforcement. Rollout selects exact descriptors through a signed BOM; rollback retains the immutable artifact and routes back to the previous BOM. Local non-writable files are content-addressed publication evidence, not a WORM guarantee; production retention requires an external immutable store policy.
