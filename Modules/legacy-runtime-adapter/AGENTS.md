---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: legacy-runtime-adapter
manifest: ./module.yaml
applies_to: .
---

# Legacy Runtime Adapter Agent Rules

## Scope

This module is the temporary owner of the existing ZennoDroid runtime paths listed in module.yaml. It is a transition boundary, not permission to rewrite or modernize the legacy runtime wholesale.

## Required reading before the first write

Read, in this order:

1. The repository root AGENTS.md.
2. This file and module.yaml.
3. Every provided and consumed contract named by module.yaml.
4. governance/modules/dependency-graph.yaml and governance/modules/compatibility.yaml.
5. The relevant test definitions and current raw evidence.
6. operations/README.md, including canary, kill-switch, and rollback limits.

If the diff expands to another module or a public contract, stop and bind that module's instructions before writing again. Record the ordered hashes in a valid instruction receipt. A receipt proves reading only.

## Stable policies

- SOUL-ISO-001: never mix Soul, device, or platform-account state.
- CMD-IDEMP-001: mutations require stable idempotency and duplicate-delivery behavior.
- RESULT-VERIFY-001: native execution and the business postcondition must both be verified before success.
- GBRAIN-READBACK-001: no GBrain success claim without exact scoped read-back and checksum.
- EDGE-NORESTART-001: no claim that ZennoDroid stayed running without Windows PID and start-time evidence.

## Legacy constraints

- Preserve the bytes, BOM, encoding, and line endings of existing tracked C# files unless the approved change targets that exact file.
- Keep code compiled by ZennoDroid compatible with C# 5 until a real target installation proves otherwise.
- Do not add modern .NET APIs to the legacy compile path.
- Do not move existing legacy paths into this folder as a side effect.
- Unknown contract versions, commands, actions, steps, selectors, or result states fail closed.
- Mock, screenshot, stdout PASS text, and process exit alone do not prove a device action succeeded.

## Contracts and communication

Only versioned APIs, events, commands, receipts, module-owned read models, or the SoulMemory adapter may cross module boundaries. Do not read another module's tables, reference its internal types, share mutable static state, or send model text directly to Shell, SQL, a device, or release machinery.

## Tests and evidence

Required checks release only on PASS. FAIL, SKIP, PARTIAL, NOT_RUN, INFRA_ERROR, and NOT_APPLICABLE are failures for a required check. Label all mock tests as mock, and never use them for Windows or device evidence.

## Rollout and rollback

This module is not independently release eligible. Any behavior change is R3 until Windows and ZennoDroid compatibility are measured, requires human release approval, a bounded canary, a kill switch, and a documented rollback or compensation path.

## Prohibited

Do not store GBrain credentials, Persona, Interest evolution, or unrestricted AI decisions in ZennoDroid. Do not implement evasion, fake engagement, spam, impersonation, or unauthorized data access.
