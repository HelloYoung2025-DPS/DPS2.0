# Changelog

## Unreleased

- Added the reciprocal active major-1 consumer and exact inbound edge for
  `release.bom.native.stop.authority.trust/v1`; unknown/missing majors and the
  unpublished hyphenated draft fail closed.
- Added a fixed composition-root native-stop trust authority. It verifies
  canonical JSON against the independently pinned Release Schema digest,
  recomputes all three authority sets, verifies separate Release and provider
  signatures, binds exact BOM ID/SHA/generation/activation-token digest, and
  rejects Mapping/lambda/direct-constructor/cross-authority/provider/raw-swap
  paths. Only public key hashes/IDs and signatures are accepted.
- Production now remains `WAITING_EXTERNAL` before `verify-signed-bom` and all
  rollout transitions when the deployed trust provider or current receipt is
  absent. Canonical receipt bytes, full SHA, ID, generation, and attestation
  are persisted in the existing append-only event stream and fully revalidated
  after restart.
- Added PostgreSQL migration `003` and an in-memory equivalent for a global,
  append-only native-stop trust receipt index. Same receipt ID with different
  bytes is quarantined across workflows; identical full-SHA replay remains
  idempotent across the index-bind/event-append crash window.
- Added Release-wire, signature, BOM/generation/authority-swap, currentness,
  expiry-equality, restart, replay, and missing-provider attack coverage while
  preserving the PostgreSQL `002` Intake replay guard.

- Added ordered, contiguous PostgreSQL migrations with an append-only
  admin-owned migration hash ledger and fail-closed gap, duplicate, symlink,
  untracked-schema, and hash-drift handling.
- Added the PostgreSQL `002` future Intake replay guard. Intent IDs,
  idempotency keys, requester nonces, and approval nonces receive distinct
  domain-separated binding hashes; a conflicting full-intent digest appends a
  conflict proof and writes no receipt, acknowledgement, or partial bindings.
- Added unit, contract, and real PostgreSQL attack coverage. The active Host
  contract/communication declarations remain v1; v2 orchestration wiring is a
  separate compatibility change.
- Hardened migration discovery with a held no-follow directory descriptor,
  bounded admin connection settings, exact least-privilege runtime-role ACL
  reset/verification, and simultaneous two-connection replay race coverage.

## 0.1.0 - 2026-07-14

- Added the proposed DPS AI Factory composition root.
- Added append-only workflow, receipt, outbox, delivery, fencing, and
  quarantine persistence with in-memory and PostgreSQL repositories.
- Added recoverable orchestration across the ten existing Factory modules,
  strict nine-role separation, fixed adapter boundaries, local simulation,
  adversarial contract tests, and a real PostgreSQL integration suite.
- Added immutable prior-output chaining, cross-stage semantic validation,
  conflict-triggered quarantine, canonical opaque IDs, and trailing-newline
  rejection at the public contract boundary.
- Separated immutable logical provider requests from fenced delivery attempts,
  added explicit stage causal heads, and bound rollback to the previous stable
  signed BOM tuple and full plan/result digest chain.
- Added conditional management fencing, transition-conflict quarantine, an
  executable process-bound kill switch, and distinct PostgreSQL migration and
  runtime identities with negative DDL/trigger privilege tests.
- Added a process-bound Draft 2020-12 verifier for every consumed provider
  payload so runtime behavior cannot rely on test-only schema validation.
- Bound the complete provider schema digest set to an externally verified
  trust-root signature before validator construction.
- Bound fixed argv execution to the executable, complete argv, immutable cwd
  tree, external files, fixed non-secret environment, timeout, and full
  profile digest with no inherited environment.
- Made evidence advancement operation-scoped and receipt-derived, bound human
  canary approval to the exact signed BOM tuple and a short nonce-bearing
  validity window, and extended the kill switch across held-fence work and
  provider invocation boundaries.
- Linearized every repository mutation with kill-switch revocation so a stale
  allow result cannot race a receipt, external fact, phase, or transition
  write.
- Added an external rollback authorization fact bound to the causal reason and
  signed previous-stable BOM tuple; rollback results must preserve its exact
  authorization ID.
- Restricted direct POSIX provider execution to root-owned deployment paths
  that the non-root Factory identity cannot modify, and replaced unbounded
  pipe buffering with concurrent capped streaming and process-group teardown.
- Split worktree planning from lease acquisition and require exact, disjoint
  implementation, test, contract/governance, and operations writer leases;
  provider-success retries preserve provider-domain fencing truth.
- Replaced the reusable canary approval with transition-scoped approvals: R2
  requires a distinct `SHADOW` to `CANARY` fact and R3 requires a new
  short-lived fact for each of the five rollout transitions.
- Bound PostgreSQL JSON readback to its stored digest and identity columns and
  added fixed connect, statement, lock, idle-transaction, keepalive, and TCP
  timeouts so runtime-control revocation cannot wait on an unbounded DB call.
- Rejected cross-platform path aliases, case/Unicode collisions, Windows
  reserved names, symlinked argv/environment file aliases, inherited process
  pipes, and aggregate provider output beyond the digest-bound profile limit.
- Added no-follow descriptor identity snapshots and an immediate pre-spawn
  recheck for every filesystem object that authorizes a fixed provider exec.
- Added a durable per-module implementation-ready barrier before independent
  verification, exact request/module/check/runner/attestation result binding,
  and duplicate result/evidence rejection before merge approval.
- Revalidate the subject lease and its execution time window against the
  trusted Host clock whenever a trusted result consumes it; expired leases
  now produce an auditable `STALE` transition instead of accepted evidence.
