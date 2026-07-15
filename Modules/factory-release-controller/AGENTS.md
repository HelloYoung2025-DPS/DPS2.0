---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-release-controller
manifest: ./module.yaml
applies_to: .
---

# Factory Release Controller Agent Rules

## Scope

This module enforces the upgrade and rollout state machine. Its active runtime accepts only `rollout.command/v2` authored by `factory-control-plane-host`, resolves receipt and risk authority outside the command, and emits `rollout.event/v2`. It also uniquely owns `release.bom.native.stop.authority.trust/v1` and emits that signed, BOM-bound public trust receipt to the Host; it never exports raw private keys, service secrets, or activation tokens. The original rollout v1 command and event schemas are byte-frozen, deprecated, quarantine-only classifiers with no runtime communication edge. The module cannot consume raw upgrade intents, sign its own BOM, or approve its own work.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, all provided and consumed contracts, the dependency graph, compatibility matrix, tests, and operations instructions in order. Bind hashes and rebind on any scope or public-contract change.

## Invariants

- Unknown or illegal state transitions fail closed.
- `rollout.command/v1` and `rollout.event/v1` retain their frozen original bytes and semantics; they may only be identified for quarantine and can never advance or rebuild runtime state.
- The frozen hashes in `contracts/provided/rollout.v1.frozen-sha256.json` must match before any Release Controller test can pass.
- Raw `upgrade.intent/v1` or `/v2`, including nested or receipt-shaped variants, is never a Release Controller input.
- Active v2 rollout commands contain no caller-supplied risk tier and carry one to 64 unique canonical receipt references.
- Receipt references use exactly `receipt:<kind>:<id>`, where `kind` is one declared allowlisted enum value and the lowercase ID has no leading, trailing, repeated, or mixed-normalization separator. Never normalize or rewrite an untrusted reference into acceptance.
- Active v2 wire JSON is decoded with duplicate-member rejection. `upgrade_id` and all opaque IDs use their exact lowercase grammar, `occurred_at` is a real calendar second in canonical `Z` UTC form, and side-effect counts are exact nonnegative integers (never booleans or floats).
- The trusted resolver must return the exact ordered receipt references plus a non-zero `receipt_set_sha256`; drift fails before any ledger append.
- Recovery requires a stream-head anchor authenticated independently from the replay bytes. The local event hash chain proves internal consistency only; it does not prove authenticity, and a recomputed anchor from the candidate replay stream is forbidden outside tests. Until a fixed authenticated Evidence Ledger provider is wired, the public recovery entrypoint must always fail with `WAITING_EXTERNAL`; an arbitrary resolver, callable, DTO, or locally recomputed commitment is never an acceptable substitute.
- `BOM_SIGNED` requires an external trust verifier; non-empty signature text is not verification.
- Shadow and rollout use the exact artifact/BOM digest; drift stops the release.
- Shadow has zero real side effects. Any reported side effect quarantines the release.
- R2 initial production canary and all R3 changes require a distinct human release approver; R4 is rejected.
- A kill switch is armed before shadow/canary/rolling.
- Simulation evidence is labelled `SIMULATION` with an `INTEGRATION_VERIFIED` ceiling; it cannot claim canary or scale verification.
- The native-stop trust receipt producer is exactly `factory-release-controller`, its contract major is exactly v1, and all 19 string patterns use absolute ECMAScript termination so trailing LF/CR cannot be normalized into acceptance.
- Native-stop, route-assignment, and challenge authority sets bind the exact Release BOM generation, activation-token digest, public-key digest, validity window, and set digests. Only signatures and public digests cross the Host boundary; raw private key or raw activation-token material is forbidden.
- Unknown native-stop trust majors, unknown properties, mismatched digests/signatures, revoked authorities, or a missing reciprocal Host consumer fail closed.

## Communication and data

Use only versioned JSON contracts and injected deterministic verifiers. Only v2 has a runtime route; never add v1 as a communication fallback. Do not execute arbitrary production shell, import another module's internals, read its tables, or expose production credentials.

## Tests, rollout, and rollback

Test the v1 byte freeze and zero-authority quarantine path, all v2 main and exceptional states, illegal jumps, self-approval, unsigned/invalid BOM, digest drift, strict wire parsing, receipt masquerades and separator ambiguity, invalid time/identity/count values, externally anchored tamper rejection after local hash recomputation, shadow side effects, kill switch absence, and deterministic 200-device/100-sustained/200-burst/400-equivalent simulation with failures. Rollback is requested through a v2 rollout event and performed by the rollback controller.
