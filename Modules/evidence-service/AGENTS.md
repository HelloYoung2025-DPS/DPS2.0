---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: evidence-service
manifest: ./module.yaml
applies_to: .
---

# Evidence Service Agent Rules

## Scope

This module validates and records immutable test and verification evidence. It does not execute production actions, approve its own changes, or promote mock evidence to a higher verification level.

## Required reading before the first write

Read the root AGENTS.md, this file, module.yaml, every provided and consumed contract, the dependency graph, compatibility source, tests/README.md, and operations/README.md in order. Bind exact hashes before writing and rebind whenever the affected scope changes.

Evidence issuance, implementation, governance modification, and release approval must be separately reviewable. A receipt proves instruction binding only.

## Stable policies

- SOUL-ISO-001: evidence containing identity context is scoped and redacted; cross-Soul leakage is forbidden.
- CMD-IDEMP-001: evidence_id and artifact hashes make duplicate submission a no-op and conflicting submission a quarantine.
- RESULT-VERIFY-001: device success requires native result plus verified business postcondition.
- GBRAIN-READBACK-001: GBrain evidence requires exact scoped read-back and checksum.
- EDGE-NORESTART-001: Windows evidence requires ZennoDroid PID, start time, and connection continuity.

## Evidence invariants

- Allowed statuses are PASS, FAIL, SKIP, PARTIAL, NOT_RUN, INFRA_ERROR, and NOT_APPLICABLE.
- A required check releases only on PASS with exit code zero and required raw artifacts.
- Mock, hosted, simulated, Windows, device, canary, and scale evidence remain distinguishable.
- Missing, tampered, stale, partial, self-issued, or unverifiable evidence fails closed.

## Contracts and communication

Accept only declared versioned receipts through authenticated edges. Do not read another module's tables, import internal types, share mutable static state, or accept stdout PASS as proof.

## Tests and evidence

Test schema rejection, required non-PASS rejection, artifact hash mismatch, duplicate and conflict behavior, stale instruction receipts, role separation, and verification-level escalation attacks.

## Rollout and rollback

The module is proposed and not release eligible. Future rollout requires append-only storage, shadow validation, a bounded canary, kill switch, previous schema compatibility, and rollback of routing without deleting historical evidence.
