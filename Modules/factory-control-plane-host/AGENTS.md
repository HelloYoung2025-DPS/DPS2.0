---
agents_spec: dps.agents/v1
policy_version: 1.1.0
module_id: factory-control-plane-host
manifest: ./module.yaml
applies_to: .
---

# Factory Control Plane Host Agent Rules

## Scope

This module is the sole composition root for the DPS AI Factory workflow. It
owns orchestration state, append-only orchestration events, role-binding
receipts, module-call receipts, a transactional outbox, recovery fencing, and
workflow status. It does not own any provider module's domain data, execute
model-authored commands, sign a BOM, approve a release, or read another
module's database.

The Host is the sole consumer-side composition root for
`release.bom.native.stop.authority.trust/v1`. The public JSON receipt is not
authority by itself. Only the fixed, typed deployment provider, pinned Release
Schema digest, separate public-key verification ports, trusted clock, and
process-sealed capability together authorize the Host to bind that fact.

## Required reading before the first write

Read the repository root `AGENTS.md`, this file, `module.yaml`, every provided
and consumed contract, the generated dependency graph, compatibility matrix,
risk policy, tests, migrations, and `operations/README.md`, in that order.
When a provider contract or affected scope changes, rebind the provider and
all declared consumers before another write.

## Invariants

- Workflow truth is rebuilt from an append-only hash chain. Receipts, outbox
  messages, delivery observations, fences, and quarantine records are
  append-only.
- A newly acquired fencing token is strictly greater than every prior token.
  A superseded worker cannot append an event, receipt, or delivery fact.
- The same workflow and idempotency key with identical canonical bytes is a
  no-op. Conflicting bytes are quarantined and cannot advance the workflow.
- Provider calls cross only `factory.module.command/v1` and
  `factory.module.receipt/v1` through an injected adapter. The host never
  imports provider source modules or accepts argv, shell, SQL, device commands,
  environment overrides, or production secrets from a request.
- A provider request has one immutable `logical_request_sha256`; delivery time
  and the monotonically increasing orchestration fence belong to a separate
  attempt envelope. Reuse of a request ID with different logical content is a
  quarantine condition.
- Downstream commands bind explicit stage-scoped causal heads. Never infer the
  newest instruction, evidence, rollout, or rollback fact from a hash-sorted
  history.
- All nine Factory roles are present and pairwise distinct. Role bindings come
  from a process-bound trusted directory, not the workflow request.
- A module implementer cannot author governance, contracts, tests, evidence,
  signatures, release approval, or rollback approval in the same workflow.
- R2 initial production canary and every R3 production transition require a
  separately verified human approval whose identity is distinct from all nine
  Factory roles. R4 is rejected.
- Simulation is labelled `SIMULATION`, has zero real side effects, and has an
  `INTEGRATION_VERIFIED` ceiling. A simulated `COMPLETED` workflow is not a
  production release, canary, device, Windows, or scale claim.
- The process-bound feature flag and kill switch must authorize intake, every
  fence acquisition, each orchestration continuation, and every provider call
  immediately before and after invocation. Every repository mutation must be
  inside the control authority's atomic guard; a separate check followed by a
  write is forbidden. Status and export remain read-only when denied.
- Production evidence kind and verification level are an exact pair. Every
  fixed operation has a minimum level, provider payload metadata must agree
  with its receipt, and status is derived from accepted raw receipts rather
  than the workflow state name.
- Human canary approval binds the exact request, risk, BOM, artifact, BOM
  signature, distinct approver, one-workflow nonce, signature key, and a
  maximum fifteen-minute validity window.
- A production workflow cannot schedule `verify-signed-bom`, enter
  `BOM_SIGNED`, or execute a rollout phase until the exact active major-1
  `release.bom.native.stop.authority.trust/v1` receipt is verified. Missing
  deployment provider or expired currentness produces `WAITING_EXTERNAL` with
  no rollout phase transition. The unpublished hyphenated draft, a family
  without `/v1`, and every unknown major fail closed.
- Native-stop trust binds the exact Release BOM ID, full BOM SHA, integration
  commit, BOM generation, activation-token digest, and all three authority
  sets. Recompute every individual and set digest with the Release-owned wire
  profile, verify the Release signature and the independent issuer/audience
  provider attestation, and require `issued_at <= now < expires_at`; equality
  at expiry is expired. Any revoked authority is rejected.
- Plain mappings, lambdas, public constructors, capabilities from another
  authority instance, provider swaps, raw-byte swaps, and same receipt ID with
  a different full SHA never confer authority. Persist canonical public bytes,
  full receipt SHA, ID, generation, and attestation in the append-only event
  stream. Bind each receipt ID to its full SHA and stable BOM tuple in the
  global append-only `native_stop_authority_trust_binding` index before
  appending the workflow fact. Fully revalidate both records after restart and
  before every production rollout transition; cross-workflow ID/SHA conflicts
  quarantine the attempted workflow.
- Native-stop trust accepts public key identifiers/hashes and signatures only.
  Raw private keys, activation tokens, API keys, passwords, credentials, and
  service secrets are forbidden in requests, receipts, durable events, logs,
  screenshots, or tests.
- Rollback requires a separately verified, short-lived external authorization
  fact bound to the exact request, causal reason, candidate BOM, previous
  stable BOM, and stable-BOM verification record. The result must echo that
  fact's exact ID; a merely well-formed authorization string is forbidden.
- Provider schemas and fixed argv deployments are external trust inputs. The
  complete schema digest set, schema signature, executable, argv, deployment
  tree, external files, environment, timeout, and profile digest are bound
  before runtime use and rechecked immediately before invocation. Direct POSIX
  execution additionally requires a non-root service identity and root-owned,
  service-nonwritable paths. Output is streamed under a hard cap and timeout;
  overflow terminates and reaps the entire provider process group.

## Communication and data

Use only declared JSON contracts, injected public adapter protocols, and fixed
process-bound argv profiles with `shell=False`. Never trust a non-empty
signature or approval string by itself. Store only opaque references and
bounded hashes; prompts, source content, personal data, device credentials,
GBrain credentials, and production private keys are forbidden.

## Tests, rollout, and rollback

Required tests cover the full local simulation, all nine roles, role overlap,
request-authored authority, path-class separation, stale instructions,
conflicting receipts, crash after provider success, process restart, monotonic
fencing, illegal transitions, R2/R3 approval, R4 rejection, and PostgreSQL 18.4
transactions. Missing PostgreSQL infrastructure is `INFRA_ERROR`, never PASS.
They also use the frozen Release fixture to attack unknown/missing majors,
the unpublished hyphenated draft, non-canonical JSON, signatures, BOM and
generation drift, three-authority swaps, issuer/audience/revocation,
cross-authority capabilities, provider/raw swaps, expiry equality, restart
replay, missing provider, and same-ID/different-SHA quarantine.
Rollout begins with simulation and shadow orchestration only. The kill switch
stops new intake and fence acquisition. Rollback routes to the previous signed
host while retaining every orchestration fact.
