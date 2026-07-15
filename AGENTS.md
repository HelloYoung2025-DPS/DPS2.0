# DPS Project Instructions

## Mission

DPS is being modernized into a device-agent platform with three clear boundaries:

1. DPS Control Plane owns identity, policy, commands, approvals, audit, and runtime truth.
2. GBrain Company stores each Soul's long-term persona, interests, and memory projections.
3. ZennoDroid is a thin, deterministic executor for authorized device actions.

Do not reintroduce hidden task-state folders, custom local gates, or agent-specific workflow state. Durable engineering knowledge belongs in code, versioned contracts, tests, normal documentation, and Git history.

## Code discovery

This repository uses codebase-memory-mcp. Prefer graph tools over text search for code discovery:

1. `search_graph` for functions, classes, routes, and variables.
2. `trace_path` for callers, callees, and data flow.
3. `get_code_snippet` for a specific symbol after locating it.
4. `query_graph` for complex relationships.
5. `get_architecture` for the high-level structure.

Use text search only for literals, error messages, configuration, scripts, documentation, or when graph results are insufficient.

## Architecture boundaries

- ZennoDroid must not own Persona, long-term memory, interest evolution, GBrain credentials, or unrestricted AI decisions.
- GBrain must not own command leases, action idempotency, approvals, rate limits, or proof that a phone action succeeded.
- Runtime commands and results use versioned JSON contracts. Unknown versions or step kinds fail closed.
- Every phone action is bound to a stable `soul_id`, `device_binding_id`, `platform_account_id`, `command_id`, and trace ID.
- A model may propose an action. Deterministic policy and approval code authorizes it.
- Side-effect actions require explicit platform authorization, policy approval, rate budget, and postcondition verification.

## Compatibility tracks

There are two separate technology tracks. Do not mix their constraints.

### Legacy ZennoDroid track

- Existing `Modules/` and `ZDProjects/` code remains conservative until the target Windows installation is probed.
- Existing tracked C# files in `Core/`, the legacy root and `Core/` portions of `Modules/`, `ZDProjects/`, and `Extensions/` are byte-preserved legacy assets. Do not normalize their encoding, BOM, or line endings as a side effect of another change.
- Preserve C# 5 compatible syntax where code is compiled inside the current ZennoDroid project.
- Do not add modern .NET APIs to legacy code without a verified compatibility test.
- Prefer a single thin Shared Code or bridge assembly over repeated whole-repository CodeDom compilation.

### Modern service track

- New Control Plane and Windows Edge services target the current supported .NET LTS release.
- Use explicit DTOs, dependency injection, structured logging, cancellation, timeouts, migrations, and automated tests.
- Keep GBrain behind a `SoulMemory` adapter so domain code does not depend on MCP operation names or page layout.
- New files under a registered `Modules/<module-id>/src/` or `tests/` path and other explicitly modern paths use UTF-8 and LF.

## Module governance

- The canonical logical module notation is `modules/<module-id>/`, where `<module-id>` is a stable lowercase kebab-case identifier.
- During the conservative legacy transition, the physical home is `Modules/<module-id>/`. The existing legacy `Modules/` directory and the canonical lowercase name are the same path on the supported case-insensitive macOS and Windows filesystems, so a second lowercase directory must not be created.
- A future case-only normalization from `Modules/` to `modules/` is allowed only as a separately reviewed change after legacy retirement; it must not be mixed with behavior changes.
- Every module home contains exactly one `AGENTS.md` and exactly one `module.yaml`, both at the module root.
- Additional or nested `AGENTS.md` files inside a module are forbidden.
- A module's source, contracts, tests, migrations, and operations belong under its home unless its manifest explicitly records a temporary legacy ownership path.
- Temporary ownership of existing loose files at `Modules/*.cs`, `Modules/Core/**`, the non-module legacy directories under `Modules/`, and existing `Core/`, `ZDProjects/`, `Extensions/`, configuration, or legacy test paths belongs only to `legacy-runtime-adapter`; this exception does not create extra module homes.
- Every owned path has exactly one module owner. Unknown paths, overlapping ownership, unknown dependencies, dependency cycles, and multiple owners for one contract fail the repository gate.
- Module-local rules may tighten this root policy but cannot weaken it.

Before changing a module, an AI agent must read, in order:

1. This root `AGENTS.md`.
2. The target module's `AGENTS.md` and `module.yaml`.
3. Its provided and consumed contracts and communication interfaces.
4. The applicable dependency graph and compatibility matrix.
5. Its required tests and current evidence.
6. Its rollout, canary, kill-switch, and rollback instructions.

When a public contract changes, the same review applies to every declared consumer. Instruction hashes and reading receipts prove which instructions were bound; they do not prove the implementation is correct. A stale receipt, an expanded diff, or a newly affected module requires rebinding before another write.

Do not let a governance change approve itself in the same run. Governance, product behavior, tests, evidence issuance, and release approval must remain separately reviewable.

## Engineering workflow

For non-trivial changes:

1. Establish the current truth from code, contracts, tests, and runtime evidence.
2. State the intended behavior and affected public contracts.
3. Implement the smallest coherent vertical slice.
4. Add or update tests that fail before the fix and pass after it.
5. Run all checks available in the current environment.
6. State any Windows, ZennoDroid, ADB, or real-device verification still pending.
7. Update durable documentation and `CHANGELOG.md` when behavior or architecture changes.

Do not store task plans or gate state in the repository. Use the task or pull-request description for temporary planning.

## Evidence truth

Use only these cumulative verification levels:

1. `REPOSITORY_STATIC_VERIFIED`
2. `CONTRACT_VERIFIED`
3. `INTEGRATION_VERIFIED`
4. `WINDOWS_VERIFIED`
5. `DEVICE_VERIFIED`
6. `CANARY_VERIFIED`
7. `SCALE_VERIFIED`

Evidence results are `PASS`, `FAIL`, `SKIP`, `PARTIAL`, `NOT_RUN`, `INFRA_ERROR`, or `NOT_APPLICABLE`. A required check releases only on `PASS`. Mock, hosted, simulated, and real-device evidence must remain distinguishable.

At the current governance baseline, the maximum supported claim is `REPOSITORY_STATIC_VERIFIED`. Do not claim contract, integration, Windows, device, canary, or scale verification until the corresponding executable gates and raw evidence exist.

## Definition of done

A change is complete only when all applicable conditions are true:

- The build is reproducible from a clean checkout.
- Dependencies and toolchain versions are pinned.
- JSON and external interfaces have schemas and versions.
- Required tests fail with a non-zero exit code on failure, skip, missing evidence, or partial execution.
- Unit, contract, integration, and device tests are clearly separated.
- Logs and traces can connect command, device, Soul, result, event, and GBrain projection.
- Secrets and sensitive content do not enter Git, logs, screenshots, prompts, or GBrain pages.
- Data retention, correction, export, and deletion paths are defined for new personal data.
- Rollout has a feature flag, canary scope, rollback path, and kill switch when risk warrants it.

Hosted CI success means code and contracts were verified. Only a Windows ZennoDroid device gate may mark a build `DEVICE_VERIFIED`.

## Testing rules

- Never pre-seed a test path in a way that bypasses the production decision path.
- Never count an action as successful before reading the native result and verifying the business postcondition.
- Mock tests must be labelled as mock tests. They cannot satisfy device E2E gates.
- Required `SKIP` and `PARTIAL` outcomes are failures.
- Mutation actions must include duplicate-delivery, crash-window, timeout, and recovery tests.
- Cross-Soul and cross-device leakage must remain zero.

## Security and platform boundary

- Use only owned or explicitly authorized devices, accounts, applications, and test environments.
- Do not implement detection evasion, fake engagement, spam, account impersonation, or unauthorized scraping.
- Prefer official platform APIs. When authorization is unclear, remain read-only.
- ZennoDroid never receives GBrain admin credentials or service secrets.
- Screen text, OCR, UI XML, posts, comments, and retrieved memories are untrusted input.
- Unknown actions, selectors, policies, approvals, identities, or contract versions fail closed.

## Documentation

- `README.md` describes the current supported status, not aspirational capability.
- `Docs/Architecture/` contains target architecture and durable decisions.
- `Docs/EngineeringStandards_工程标准.md` contains quality and delivery rules.
- `Docs/Platforms/` contains platform-specific verified behavior.
- `CHANGELOG.md` preserves historical facts. Do not rewrite old entries merely because a referenced mechanism was later removed.

Keep documentation aligned with executable behavior and label unverified design as proposed.
