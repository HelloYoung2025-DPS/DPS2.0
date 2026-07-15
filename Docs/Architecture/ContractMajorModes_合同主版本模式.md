# Contract major modes 合同主版本模式

状态: Central compatibility policy adopted by registered module Manifests, the Phase 0 generator, and the executable F9 signed-Manifest validator. Product communication reciprocity and candidate execution evidence remain separately reviewable.

## Purpose

Every item in `contracts.provided[]` and `contracts.consumed[]` identifies one exact `(contractId, major)` and must declare one machine-readable `mode`:

- `active`
- `compat-read`
- `quarantine-only`
- `retired`

`compat-read` is valid only in `contracts.consumed[]`. A provided declaration cannot encode or publish a read-compatibility major, so Schema validation rejects `contracts.provided[].mode: compat-read`.

There is no default. A missing or unknown contract ID, major, or mode fails closed. A later major is never inferred to be compatible from SemVer, file names, DTO shape, successful deserialization, or the presence of an older major.

`status` and `mode` answer different questions:

- `status` is the lifecycle of the contract asset: proposed, active, deprecated, or retired.
- `mode` is the runtime treatment of that exact major in this exact module version.

A retired status and retired mode must agree. Other lifecycle states do not silently determine a runtime mode.

## Mode semantics

| Mode | Runtime behavior | Encoding | Eligibility contribution |
|---|---|---|---|
| `active` | Decode, validate, and use the exact major on the declared runtime path | Allowed only when the reciprocal communication direction and constrained producer permit it | May contribute to runnable, deployability, an active producer-consumer pair, and candidate green after every other gate passes |
| `compat-read` | Decode and validate the previous major for read-only compatibility evidence; do not execute its runtime domain path | Forbidden | Must not contribute to runnable, deployability, active producer-consumer inventory, or candidate green |
| `quarantine-only` | Inspect only bounded routing metadata needed to quarantine and audit | Forbidden | Must not contribute to runnable, deployability, active producer-consumer inventory, or candidate green |
| `retired` | Reject at runtime | Forbidden | Must not contribute to any runtime or release result |

A `true` eligibility value in the policy means only “may contribute”. It is never sufficient evidence by itself. Tests, reciprocal communication, exact artifacts, compatibility combinations, signed Release BOM selection, rollout controls, and all higher-risk approvals still apply.

Passing a negative quarantine test proves that fail-closed handling works. It does not turn the quarantined major into a runnable path or positive candidate evidence, and it must not increase any green count.

## Compatibility matrix v2

`governance/verification/f9-compatibility-matrix.v2.schema.json` replaces the ambiguous v1 snapshot shape for new evidence while retaining the v1 Schema for historical verification. V2 requires:

- `majorDeclarations`: the exact module, declaration kind, contract ID, major, source, lifecycle status, mode, owner, and static eligibility for every declaration;
- `policySha256`: the SHA-256 of the exact validated compatibility policy used to generate the snapshot;
- `majorDeclarations.schemaProducers`: the exact `producer_module` const/enum read from the contract Schema rather than inferred from contract ownership;
- `declarationMatrix`: the contract owner, Schema producer, transport sender, transport receiver/runtime consumer, reciprocal resolution, canonical communication-pair digest, directional class, mode class, `readCompatible`, independent/group requirements, and separate runtime/release accounting booleans;
- `axisMeaning`: N and N-1 explicitly mean candidate and previous-stable module versions selected by signed Release BOMs, not contract majors;
- `executionCombinations`: the fixed N/N, N/N-1, N-1/N, and N-1/N-1 rows; and
- top-level `candidateGreenEligible`: only the static prerequisite that all required declaration rows are eligible.

Every execution-combination row is fixed to `evidenceStatus: NOT_RUN`, `evidenceClass: candidate-artifact-required`, and `candidateGreenEligible: false`. The static compatibility snapshot therefore cannot self-issue test evidence. A separate candidate artifact must prove each required execution combination.

For `active -> compat-read`, `readCompatible` is true while `runnable`, `deployable`, `activeProducerConsumer`, and `candidateGreenEligible` are all false. Any quarantine-only or retired row also fixes all four accounting booleans to false.

An empty declaration matrix is never green. `all([])` is not an acceptable
proof: v2 requires at least one exact runtime row, and the Phase 0 aggregate
uses an explicit non-empty check before evaluating `all(...)`.

V2 also separates ordinary and grouped deployment:

- `independentDeployable` is true only when the current active pair and every required previous-major pair are runnable without coordinated release.
- `compatibilityGroupRequired` is true when the current active pair is runnable but a required previous major is intentionally unavailable because it is missing, `compat-read`, `quarantine-only`, or `retired`.

A compatibility group prevents a security-breaking upgrade from being permanently rejected merely because the old major must no longer execute. It is not an exception to evidence. Static Phase 0 output can only identify the required group; only an exact signed Release BOM plus complete candidate execution evidence for the group may authorize rollout. While that evidence is absent, `candidateGreenEligible` remains false and all execution combinations remain `NOT_RUN`.

The executable F9 validator independently rebuilds the same v2 bytes from the
raw Manifests bound by the signed BOM. It rejects v1 artifacts, missing or
unknown modes, non-fail-closed Manifest behavior, and any v2 matrix that differs
from those signed facts. Until a separately versioned full-group execution
artifact is present and bound to the exact BOM, a required compatibility group
fails with `compatibility_group_evidence_missing`; the static v2 matrix cannot
authorize it.

## Ownership is not producer identity

`contracts.provided[]` records the module that owns the public contract definition. It does not prove that the owner emits every message. A module may own an input contract whose authenticated peer is the wire producer.

The producer and consumer roles for a deployment are resolved only from:

1. the exact signed module manifests selected by the Release BOM;
2. reciprocal `communication.inbound[]` and `communication.outbound[]` entries for the same contract ID and major;
3. the contract Schema's constrained `producer_module`; and
4. the exact mode declared by each affected module for that major.

The resulting fields deliberately keep four identities separate:

- `ownerModule` owns and versions the contract Schema;
- `producerModule` is constrained by the exact Schema's `producer_module`;
- `transportSenderModule` owns the outbound communication edge; and
- `transportReceiverModule`/`consumerModule` owns the reciprocal inbound edge.

Ordinarily the Schema producer is the transport sender. A relay may differ only
when its exact outbound edge declares `preserveProducer: true`, the relay has an
exact consumed declaration for that contract major, the Schema resolves one
unambiguous original producer, and the receiver declares the reciprocal inbound
edge. The snapshot then records
`producerResolution: schema-producer-preserved-by-relay`. It never rewrites the
producer to the relay module.

`communicationPairSha256` binds a stable canonical representation of both
communication edges, including sender, receiver, contract major, transport,
timeouts, retry/idempotency/auth/failure semantics, and `preserveProducer`.
Missing, duplicate, conflicting, or one-sided edges set
`producerResolution: unresolved`, leave the digest null, and cannot be
runnable, deployable, active, independently deployable, or candidate green.

An active runtime pair is `active -> active` only after the owner, Schema producer, any relay sender, and runtime consumer all resolve to `active` and the reciprocal pair is bound. An `active -> compat-read` pair proves only bounded read compatibility; it is not a runnable or deployable execution path and supplies no candidate-green credit. Any required pair containing `quarantine-only` is unavailable for runtime and deployment accounting. Any pair containing `retired` rejects the message.

## Fail-closed resolution

For every inbound or outbound contract message, gate, matrix generator, and release evaluator:

1. Parse a bounded contract identity and explicit positive major.
2. Resolve an exact `(contractId, major)` declaration; do not fall forward or backward.
3. Reject a missing or unknown contract or major.
4. Read the explicit mode; reject a missing or unknown mode.
5. Resolve the reciprocal peer and communication direction.
6. Apply only an allowed exact mode pair.
7. Require the exact module versions, contract sources, matrix, and artifacts selected by the signed Release BOM.

`supportedContractMajors` must cover the same declared majors, but it does not supply a default mode. The per-major contract item remains the source of mode truth.

## Upgrade sequence

For a V1 to V2 breaking change:

1. Add V2 without changing the meaning of V1.
2. While V1 traffic can still execute, ship consumers that declare both V1 and V2 `active` and pass all four N/N-1 execution combinations.
3. Apply additive storage expansion and keep writes on the selected current major.
4. Enable the V2 producer only through an exact signed BOM and disabled-by-default feature flag.
5. Run shadow, canary, and the N/N-1 observation window.
6. Stop V1 production in a separate release.
7. After no V1 execution path remains, change the consumed V1 declaration to `compat-read` only for bounded historical/readback compatibility checks; this contributes no runtime or candidate-green credit.
8. Change V1 to `quarantine-only` when delayed V1 traffic must be isolated and audited; this is not positive compatibility evidence.
9. Change V1 to `retired` and remove its runtime decoder only in a later release after the retention and rollback window closes.

Any gate or matrix implementation that ignores `mode`, supplies a default for an absent mode, treats `quarantine-only` as supported runtime traffic, or counts a quarantine result as candidate green is incompatible with this policy and must fail closed.
