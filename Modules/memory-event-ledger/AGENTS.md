---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: memory-event-ledger
manifest: ./module.yaml
applies_to: .
---

# Memory Event Ledger Agent Rules

## Scope

This module owns the append-only MemoryEvent ledger, event content hashes, quarantine decisions, and its transactional outbox. It does not own Persona current state, interest scoring, GBrain pages, or device action truth.

## Required reading before the first write

Read the root AGENTS.md, this file, module.yaml, every contract in contracts/provided and contracts/consumed, the dependency graph, compatibility source, tests/README.md, and operations/README.md in order. Bind exact hashes before writing.

If affected paths or consumers expand, invalidate the receipt and bind all newly affected module instructions. Reading is not correctness evidence.

## Stable policies

- SOUL-ISO-001: every event and query is scoped by soul_id and cannot cross Soul boundaries.
- CMD-IDEMP-001: same event_id plus same canonical hash is a no-op; same event_id plus a different hash is quarantined.
- RESULT-VERIFY-001: v2 accepts only an exact signed `OBSERVATION_VERIFIED` SUCCESS receipt with native and postcondition evidence. Spoken, acted, failed, and `UNKNOWN_OUTCOME` claims are rejected; they cannot be relabelled as observations.
- GBRAIN-READBACK-001: this ledger does not treat a projection attempt as a successful GBrain write.
- EDGE-NORESTART-001: this module cannot issue Windows or device verification.

## Ledger invariants

- Events are append-only; corrections and deletions are new auditable records or approved privacy erasure workflows.
- Business event and outbox record commit in one transaction.
- Replay of identical ordered input is deterministic.
- Timeouts, duplicate delivery, crash windows, out-of-order events, and recovery are required test cases.
- `memory.event/v1` and `memory.outbox/v1` are byte-frozen quarantine-only contracts. No active append or outbound runtime path may use them.
- v2 append accepts only a `PreparedMemoryEventV2` issued by this exact composition. Soul and observation capabilities use reference-identity seals and must be revalidated for current revision, key role, audience, trust epoch, revocation epoch, and strict expiry before append.
- Public DTO construction, JSON round-tripping, reflection-free copying, caller-supplied mappings, delegates, clocks, or trust roots never authorize an append.

## Contracts and communication

Only declared versioned events, APIs, receipts, and owned read models may cross the boundary. No cross-module table reads, internal type references, shared mutable static state, or raw model text execution. Unknown major versions fail closed.

The current repository does not yet contain a production Soul current-resolution capability provider or a Release-BOM-pinned Executor receipt root. `CreateProduction` must therefore remain fail-closed with `WAITING_EXTERNAL`; test-only fixed authorities are not production evidence.

## Tests and evidence

Required checks release only on PASS. Tests must cover duplicate no-op, conflicting hash quarantine, transaction rollback, outbox recovery, deterministic replay, and zero cross-Soul leakage. Mock evidence is labelled and cannot satisfy integration or device gates.

## Rollout and rollback

The module is proposed and not release eligible. Use additive migrations, dual-read compatibility when required, shadow replay, a feature flag, bounded canary, kill switch, and the previous signed BOM as the rollback target.
