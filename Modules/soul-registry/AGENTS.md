---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: soul-registry
manifest: ./module.yaml
applies_to: .
---

# Soul Registry Agent Rules

## Scope

This module owns stable Soul identity resolution and verified aliases. It does not own device commands, platform side effects, long-term memory, interests, or GBrain transport.

## Required reading before the first write

Read the repository root AGENTS.md, this file, module.yaml, all provided and consumed contracts, governance/modules/dependency-graph.yaml, governance/modules/compatibility.yaml, tests/README.md, and operations/README.md in that order. Bind their hashes in an instruction receipt before writing.

If a diff or contract change adds a consumer, stop and bind the consumer's AGENTS.md and module.yaml. A reading receipt is not test or approval evidence.

## Stable policies

- SOUL-ISO-001: soul_id is immutable; verified email, phone, and platform identifiers are aliases, never the Soul itself.
- CMD-IDEMP-001: repeated resolution requests with the same idempotency key must be deterministic.
- RESULT-VERIFY-001: identity verification must finish before an alias is accepted.
- GBRAIN-READBACK-001: this module never accesses GBrain directly.
- EDGE-NORESTART-001: this module cannot claim Windows or ZennoDroid verification.

## Identity and privacy

- Do not expose raw email addresses or phone numbers in public contracts.
- Reject ambiguous, unverified, revoked, cross-tenant, or unknown aliases.
- Cross-Soul, cross-device, and cross-account leakage must remain zero.
- Keep correction, export, deletion, and alias revocation deterministic and auditable.

## Contracts and communication

Use only the declared versioned API and event edges. Do not read another module's tables, import another module's internal types, or share mutable state. Unknown major versions fail closed.

## Tests and evidence

Required checks release only on PASS. Include deterministic resolution, alias collision, revocation, duplicate delivery, concurrency, and cross-Soul adversarial tests. Mock evidence cannot satisfy integration, Windows, or device gates.

## Rollout and rollback

The module is proposed and not release eligible. Future rollout requires a feature flag, synthetic and shadow verification, bounded canary, kill switch, additive migration, and rollback to the previous signed Release BOM.
