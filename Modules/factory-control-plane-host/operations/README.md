# Factory Control Plane Host operations

## Current status

The module is proposed and not release eligible. The local deterministic path
is simulation only. It may exercise every workflow state while keeping
`simulation_only=true`, `side_effect_count=0`, and an
`INTEGRATION_VERIFIED` ceiling; that does not establish a real canary or scale
result.

## Production composition

The process must receive separately controlled adapters for:

1. A trusted role directory that returns exactly nine pairwise-distinct role
   identities and a verified policy digest.
2. A provider adapter registry whose fixed profiles map only the declared
   module and operation pairs to versioned JSON APIs or fixed argv with
   `shell=False`. Each profile binds the canonical executable digest, complete
   argv, cwd deployment-tree digest, external file digests, fixed non-secret
   environment, timeout, and full profile digest. It inherits no process
   environment and rechecks every binding immediately before invocation. The
   POSIX launcher additionally requires a non-root Factory identity and a
   root-owned deployment tree that the Factory identity cannot modify. Other
   platforms fail closed until an equivalent signed-artifact/ACL launcher is
   installed. Stdout and stderr are drained concurrently under hard byte
   limits shared across stdout and stderr; overflow, inherited descendant
   pipes, or timeout terminates and reaps the complete process group. Every
   argv/environment file must be one canonical immutable non-symlink path.
   The launcher snapshots executable, cwd, argv/environment file, and external
   file identities through no-follow descriptors and rechecks device, inode,
   mode, owner, size, and timestamps immediately before process creation. The
   aggregate output limit is part of each signed profile digest.
3. A receipt trust verifier whose public keys and trust roots are process
   bound. Repository content cannot replace it in a running instance.
4. A process-bound provider-contract verifier that loads only the twelve
   module-receipt Draft 2020-12 schemas, rejects symlink/path escape, and
   fails closed on unknown fields, formats, majors, or oversized payloads. The
   exact twelve-schema byte-digest map and its schema-set signature come from
   an external trust root; mutable repository content cannot authenticate
   itself.
5. A separately composed native-stop authority trust boundary. It accepts
   only a typed deployed provider, typed public-key verification ports, a
   trusted clock, the exact Release-owned Schema path, and its independently
   configured SHA-256. Mapping/callback providers, lambdas, directly
   constructed capabilities, and caller time are forbidden. The provider
   returns canonical public receipt bytes plus a separately signed attestation
   containing exact provider identity, issuer, audience, receipt SHA, BOM SHA,
   generation, nonce, issued time, strict expiry, and revocation state.
   `now == expires_at` is expired.

   Before `verify-signed-bom` is scheduled, the Host recomputes all three
   authority sets with the Release-owned length-framed wire profile, verifies
   every individual digest, exact BOM ID/SHA/generation/activation-token
   digest, Release signature, provider attestation, and authority validity.
   Missing provider or expired attestation remains `WAITING_EXTERNAL`; no
   rollout phase completes. Only public key IDs/hashes and signatures cross
   this boundary. Raw private keys, activation tokens, credentials, API keys,
   passwords, and service secrets are rejected.
6. An external BOM verifier, human release authority, and rollback authority.
   R2 production remains `WAITING_EXTERNAL` before `SHADOW` to `CANARY` until
   its independently verified transition fact exists. R3 requires a separate
   one-time fact before each transition from `BOM_SIGNED` through
   `COMPLETED`; one approval cannot authorize a later transition. Human
   approval is valid only for the exact source and target states, request,
   risk, BOM, artifact, BOM signature, distinct approver, nonce, signature
   key, and a maximum fifteen-minute time window.
   Rollback also remains `WAITING_EXTERNAL` until a short-lived authorization
   fact binds the exact request, causal reason, candidate BOM, previous-stable
   BOM, and previous-stable verification record. Its authorizer is distinct
   from every Factory role.
7. A PostgreSQL 18.4 Factory database with separate migration and runtime
   identities. The runtime identity must be a non-owner login with no elevated
   role attributes, inherited memberships, or database `CREATE`; otherwise
   migration fails as `INFRA_ERROR`. The migrator revokes prior direct and
   `PUBLIC` privileges, then grants only schema `USAGE`, table `SELECT/INSERT`
   (`schema_migration` is `SELECT` only), and sequence `USAGE`, and independently
   verifies that no mutation, DDL, trigger, reference, sequence-read, or
   function-execute privilege remains. Append-only tables reject UPDATE,
   DELETE, and TRUNCATE. Every JSON read is checked against its stored SHA-256
   and relational identity columns. Runtime and admin-migration connections
   have separate fixed connect, statement, advisory-lock, idle-transaction,
   keepalive, and TCP failure bounds. Migrations are discovered by contiguous
   three-digit version through a no-follow directory descriptor, read relative
   to that held descriptor from non-symlink regular files, and bound to an
   admin-only append-only `schema_migration` ledger before runtime grants are
   finalized.
   A name/hash drift, duplicate, gap, malformed filename, symlink, or unknown
   future history fails closed before another migration is applied. A schema
   created by the former untracked migrator is not auto-adopted because its
   applied bytes cannot be proven; it requires a separately reviewed rebuild
   or evidence-backed operator migration.
8. A process-bound runtime control authority. A disabled feature flag or armed
   kill switch rejects new intake, every new fence, continued work under an
   already-held fence, and provider calls. The host checks immediately before
   and after a provider invocation. Every repository mutation is executed
   inside the authority's atomic guard, so kill-switch activation and a write
   have one linear order; a stale positive check cannot cross that guard.
   Production must implement the same guarantee with the durable control
   generation and repository transaction, not as two remote calls. Read-only
   status and export remain available.

No production private key, device credential, GBrain credential, prompt, or
source content belongs in this database or module.

## Intake replay transaction

Migration `002` adds an append-only replay index for future
`upgrade.intent/v2` Host wiring. The active Host contract and communication
declarations remain v1 in this change; therefore this is persistence readiness,
not a claim that v2 orchestration is active.

For one trusted Intake receipt, the repository independently recomputes
`upgrade_intent_sha256`, derives separate domain hashes for `INTENT_ID`,
`IDEMPOTENCY_KEY`, `REQUESTER_AUTH_NONCE`, and a non-null `APPROVAL_NONCE`,
then takes every claim lock in deterministic order. It reads all bindings before
inserting any. Exact full-digest reuse is idempotent, including across workflow
IDs. If any claim is already bound to another full digest, every conflict proof
is appended and the workflow is quarantined, while no new claim binding,
module receipt, or `ACKNOWLEDGED` delivery fact is written. The replay index and
conflict tables store only domain-separated claim hashes; the already-required
versioned receipt remains the orchestration audit record.

The PostgreSQL integration gate runs simultaneous two-connection attacks for
exact full-digest reuse, identical claims with different full digests, and
partially overlapping claim sets. Each conflicting race must have one atomic
winner, durable conflict evidence, no deadlock, and no losing receipt, ACK, or
partial claim binding.

Migration `003` adds the global append-only
`native_stop_authority_trust_binding` index. Before a workflow appends its
native-stop trust fact, the repository takes a receipt-scoped advisory lock and
atomically binds the receipt ID to the full canonical receipt SHA and stable BOM
tuple. Identical ID/SHA reuse is a no-op, including after a crash between index
binding and event append. Same ID with a different SHA appends quarantine truth
to the attempted workflow and fails; the loser cannot overwrite the first
binding. The index contains public receipt material only and has no raw token,
private-key, password, credential, or service-secret column.

## Recovery

Each worker first appends a monotonically increasing fencing fact. Scheduling
an orchestration phase and inserting its outbox messages occur in one
transaction. Provider invocation happens outside the transaction. The
provider request ID and `logical_request_sha256` are stable, so a crash after
provider success causes the same logical request to be delivered again. The
attempt envelope may carry a higher orchestration fence and a later delivery
time. Providers bind idempotency to the logical digest and return the same
native output; conflicting logical content or output is quarantined.

Restart recovery validates the event hash chain, reacquires a higher fence,
replays receipts and delivery observations, and resumes the oldest unacknowledged
outbox message. An old worker is rejected on its next write.

Commands contain the complete digest-checked chain of prior public outputs
plus explicit causal heads grouped by producing stage, not source text or
prompts. The host verifies cross-stage identity continuity
from intent through instructions, worktrees, tests, merge, artifact, signed
BOM, rollout, and rollback before it persists a receipt. Public trace and
idempotency identifiers use the canonical opaque formats; internal event keys
are hashed into `idem_` identifiers before leaving the repository boundary.

Worktree creation is two-stage. The impact planner first receives one exact
plan. The host then requests only the writer leases needed by the declared
paths: implementation (`src`/`migrations`), tests, contract/governance, and
operations. Contract source paths exist only in the contract worktree;
case-folded or Unicode-normalized duplicate paths and overlapping lease keys
fail before implementation.

Implementation has a persisted two-stage evidence barrier. The trusted runner
must first return one exact `verify-implementation-ready` result per target
module. Only after that stage is durably completed may the host schedule the
four independent checks for each module. Every result is bound to its exact
host request ID, subject module, operation/check/suite, required-check digest,
independent runner identity and attestation, tested changeset commit, and
subject-scoped fenced lease. The host rechecks `ACTIVE`, plan, subject, fence,
and the acquired/start/finish/expiry window against its trusted clock every
time evidence consumes that lease; expiry transitions the workflow to
`STALE` before any independent work is scheduled. Duplicate result IDs or
duplicate merge evidence IDs fail before merge approval.

The externally verified signed-BOM fact is appended before release scheduling
and includes the exact previous stable BOM tuple. A separately verified,
short-lived rollback authorization fact is appended before rollback
scheduling. Rollback plan and result must preserve the BOM tuple, request
digest, causal reason, plan digest, rollback ID, exact authorization fact ID,
and postcondition proof.

The Release native-stop trust receipt is also durable append-only truth. The
global binding index owns receipt ID/full-SHA uniqueness across workflows; each
workflow's `EXTERNAL_FACT_BOUND` event stores canonical public receipt UTF-8,
the exact BOM/authority tuple, and the current public provider attestation. On
every restart and before each production rollout transition the Host
cross-checks the index, reconstructs the bytes, reruns strict JSON and Schema
validation, recomputes all digests, rechecks both signatures, and re-evaluates
currentness. It never restores an old boolean. A refreshed attestation may
reuse the same receipt ID only when the canonical receipt full SHA is unchanged.
The same ID with different canonical bytes is quarantined before any release
transition, including when the first binding belongs to another workflow.

Production evidence is cumulative and operation-scoped. An exact evidence
kind/verification-level pair must meet the fixed operation minimum, and any
evidence metadata inside a provider payload must match its receipt. Workflow
status reports the highest accepted raw receipt level capped by the current
stage; a state name alone never creates `DEVICE_VERIFIED` or
`CANARY_VERIFIED` evidence. Simulation remains explicitly capped at
`INTEGRATION_VERIFIED`.

## Rollout and rollback

- Feature flag: `factory_control_plane_host_v1`.
- Kill switch: `factory_disable_control_plane_host` blocks new workflows, new
  fences, held-fence continuation, and provider calls without deleting
  history.
- Canary begins with local simulation, then an isolated PostgreSQL shadow with
  fixed synthetic provider adapters.
- Production remains before the rollout boundary until the reciprocal
  `release.bom.native.stop.authority.trust/v1` route is deployed and current.
  The unpublished hyphenated draft and unknown/missing majors have no fallback.
- Rollback stops intake, drains in-flight adapter calls, acquires no new fence,
  validates the final hash chain, and routes new work to the previous signed
  host. Existing streams remain readable and recoverable.
