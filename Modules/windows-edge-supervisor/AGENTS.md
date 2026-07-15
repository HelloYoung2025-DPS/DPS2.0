---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: windows-edge-supervisor
manifest: ./module.yaml
applies_to: .
---

# Windows Edge Supervisor Agent Rules

## Scope

This module owns deterministic A/B worker staging, capability validation, device-binding routing, drain, atomic cutover, and atomic route rollback. It never executes phone actions, stores Soul memory, or grants production approval.

## Required reading before the first write

Read the root AGENTS.md, this file, module.yaml, every provided and consumed contract, the dependency graph, compatibility matrix, tests/README.md, and operations/README.md in order. Bind exact hashes and rebind when the diff or affected contract set expands.

## Invariants

- EDGE-NORESTART-001: a Windows release requires unchanged ZennoDroid PID and start time plus continuous bridge connectivity.
- Route only by stable device_binding_id to an exact validated worker artifact and version directory.
- A candidate cannot receive routed commands before digest, capability, health, and shadow checks pass.
- Stop new routing to the old worker, drain and reconcile in-flight commands, then switch atomically.
- A drain starts only from the single active-drain-only `edge.worker.drain.directive/v1` wire signed through the least-privilege broker, verified against the deployment-pinned Supervisor public key, and durably recorded as PREPARED before transport. Retry and restart reuse those exact bytes; they never create another PSS signature for the same drain.
- Cutover accepts a strict Worker-only raw completion receipt plus a separately fetched rich Journal owner attestation. Supervisor, Worker, and Journal key sets are pairwise disjoint. The bounded Journal call, deterministic request ID, raw wire digests, scope, BOM, policy, Worker and Journal artifacts, routing epoch, canonical Journal payload, owner receipt, durability, and fresh Journal proof must correlate exactly before the two proof digests are persisted with the route transition.
- Bootstrap is an internal provisioning seam, not live-process evidence. Runtime restart must hold the exclusive writer lease and load checksum-chained durable Supervisor state coordinated with one host/path-bound, linearizable, crash-durable external monotonic anchor through prepare, local replace, and commit. Each preparation has an authority-generated unpredictable one-use fencing token; stale commit/abort calls fail. Resume may finish only the exact tokenized prepared new head or abort only that exact prepared head while the committed file remains; every third state fails closed. This is recoverable two-resource coordination, not a claim of live Worker recovery, cross-resource atomicity, or proven filesystem metadata durability.
- Roll back only to the exact previously validated slot; never silently select latest or overwrite the rollback slot before an authorized soak release.
- Bind only the fixed `127.0.0.1:28741` Zenno HTTP ABI on Windows. Require Negotiate plus an exact client SID allowlist and one `LocalMachine/My` certificate whose thumbprint and SPKI key ID match protected configuration. Until Worker IPC is stable, an authenticated POLL may receive only a signed WAIT; native results fail closed without an ACK.
- A future Worker launch must consume one unforgeable one-use Supervisor authorization, lock every file in the signed runtime manifest against write/delete, verify every directory security descriptor, start only the exact suspended executable, confirm its image path, and assign it to a `KILL_ON_JOB_CLOSE` Job Object before resume. Never invent zero arguments or accept caller/model arguments when the Worker's fixed launch/runtime ABI is unknown. The current implementation must reject launch before `CreateProcess` and report `worker-launch-runtime-abi-unavailable`.
- Artifact, capability-attestation, bridge-server, Supervisor-drain, Worker-drain and Journal key roles must be pairwise disjoint where they authorize different powers. Capability expectation must bind Zenno PID/start time, bridge key, evidence-log root, continuity, switch count and soak duration in addition to artifact/BOM/policy/freshness.
- Keep Supervisor evidence in the declared append-only hash-chain log. A real Windows statement binds an independent entry count, head, and open-file identity; self-hashing the statement is not enough. Until signed segment rotation plus an externally committed previous-segment head exist, 24-hour/scale evidence remains blocked.
- Missing Windows, PowerShell, ZennoDroid, ADB, ABI, continuity, protected configuration digest, trust-store fingerprint, BOM, policy, host, worker, or validity evidence is WAITING_EXTERNAL or FAIL and never PASS.
- Do not mark the module release-eligible until bootstrap/live-process binding, in-flight restart reconciliation, Worker restart/reattach, signed drain `NOT_SEEN`, start-or-resume retry, soak finalize, atomic process-liveness fencing, and evidence-log segmentation are implemented and independently verified.

## Communication and boundaries

Communication uses only declared versioned JSON contracts and authenticated loopback edges. Do not import another module's internal types, read its stores, expose secrets, access GBrain, make AI decisions, or execute arbitrary shell supplied by a model.

## Tests and evidence

Mac tests are unit, contract, or simulation evidence only. Test one hundred A/B switches, stable device routing, invalid digests and capabilities, drain refusal, crash windows, rollback, and missing external prerequisites. Only the real Windows gate may issue WINDOWS_VERIFIED evidence.

## Rollout and rollback

Use signed Release BOM inputs, staged version directories, shadow, bounded device cohorts, an armed kill switch, and a five-minute route rollback objective. Preserve the previous slot until post-cutover soak succeeds.
