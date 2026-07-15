---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: zenno-bridge
manifest: ./module.yaml
applies_to: .
---

# Zenno Bridge Agent Rules

## Scope

This module is a frozen, thin C# 5 compatible bridge between ZennoDroid and a fixed loopback JSON protocol. It validates envelopes, polls for authorized work, and returns native results. It owns no Persona, interests, long-term memory, policy, planning, or production credentials.

## Required reading before the first write

Read the root AGENTS.md, this file, module.yaml, every provided and consumed contract, dependency and compatibility sources, tests/README.md, and operations/README.md in order. Bind exact hashes and rebind when scope expands.

## Invariants

- Preserve C# 5 compatibility and do not assume unprobed .NET Framework, CodeDom, GAC, or DLL behavior.
- EDGE-NORESTART-001: normal Edge Worker upgrades must not replace this bridge or restart ZennoDroid.
- Accept only the exact loopback endpoint and edge.bridge.exchange/v1 envelope.
- Default to no trusted peer: require a deployment-pinned public key, a request nonce, a fresh timestamp, a full directive-body digest, a valid proof, and one-time nonce use before returning a directive.
- Unknown contract versions, exchange kinds, actions, steps, identities, or result states fail closed.
- Never reinterpret an unknown selector or action as a coordinate click.
- Never store or transmit GBrain credentials, Persona, interests, AI prompts, or arbitrary executable text.

## Communication and boundaries

Communication uses only the declared `edge.bridge.exchange/v1` JSON contract at the fixed loopback endpoint. The request presents the Windows process identity; the response requires a pinned public-key proof. Windows must still verify the host-side ACL/auth mode before release. Do not import other module internals, access their stores, expose credentials, or accept undeclared transports.

## Tests and evidence

Mac static and contract tests can check source compatibility and schemas only. A real Windows ZennoDroid gate must prove exact version, PID, start time, bridge ABI, connectivity continuity, one hundred A/B switches, rollback, and twenty-four-hour soak. Missing prerequisites are WAITING_EXTERNAL, not PASS.

## Rollout and rollback

Daily upgrades occur in Edge workers and signed declarative packs. Replacing the bridge requires a separately approved maintenance window unless the target installation has proven safe replacement; do not promise bridge hot replacement.
