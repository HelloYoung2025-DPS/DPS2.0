---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: binding
manifest: ./module.yaml
applies_to: .
---

# Binding Agent Rules

## Scope

This module is the sole owner of the authoritative relationship between one canonical Soul, one registered device, and one authorized platform account. It owns `device_binding_id`, binding revision, and binding lifecycle; it does not own the three referenced identities.

## Required reading

Before writing, read the root `AGENTS.md`, this file, `module.yaml`, every provided and consumed contract, the dependency graph, compatibility matrix, tests, and operations guidance. Rebind instruction hashes when the diff or a public-contract consumer changes.

## Invariants

- `SOUL-ISO-001`: `soul_id` is exactly `soul_` plus 64 lowercase hexadecimal characters; `device_binding_id`, `platform_account_id`, and `trace_id` are their canonical prefix plus 32 lowercase hexadecimal characters; `idempotency_key` is `idem_` plus 64 lowercase hexadecimal characters.
- `CMD-IDEMP-001`: identical redelivery is a no-op and one idempotency key cannot bind a different payload.
- `RESULT-VERIFY-001`: the write path never accepts caller-built proofs; production composition supplies provider-owned public reservation clients whose exact implementation assembly digests are bound by a current fixed-root `binding.composition.attestation/v1`, and only exact registered plus authorized scope with both provider-owned reservations confirmed may activate a binding.
- `GBRAIN-READBACK-001`: this module has no GBrain network or credential access.
- `EDGE-NORESTART-001`: this module cannot issue Windows, ZennoDroid, or device-verification evidence.
- A revoked binding cannot be silently reactivated or reassigned to another Soul.
- A bind attempt is durable before provider mutations, resumes with the same request and reservation ID after a crash, and cannot become active until both exact provider revisions are frozen.
- Every provider Reserve, Confirm, and Release receipt is validated and must prove the exact scope, reservation ID, revision, state, trace, operation time, and idempotency identity; an expired held lease or mismatched receipt is a hard failure.
- A consumer mutation that depends on an active binding holds `identity.binding.mutation.fence/v1` from authoritative revision resolution through its own commit. The fence and revoke serialize on the same binding-owned advisory-lock key.

## Communication and boundaries

Use only declared versioned contracts, scoped queries, reservation clients, and mutation-fence clients. Binding source references provider contract packs, never another module's source assembly or internal types. Production composition uses `PostgresBindingRegistry.CreateForCompositionAsync` with clients created by the registered provider modules plus a short-lived, fixed-root signed Release BOM attestation over the exact binding/provider implementation and contract-pack digests, composition-host digest, non-secret instance configuration digests, and trust epochs. The highest accepted composition generation is persisted before use; a caller-replaceable verifier, caller key, generation rollback, or same-generation equivocation is forbidden. Do not read other modules' tables, accept raw email or phone values, or share mutable static state. Unknown versions, producers, identities, statuses, reservation states, stale revisions, signatures, generations, or artifact/configuration digests fail closed.

## Tests and rollout

Required checks release only on `PASS`. Real Integration coverage uses independent PostgreSQL schemas and provider public APIs; it must cover scope mismatch, duplicate delivery, conflicting idempotency, concurrent attempts, pending/reserved/confirmed crash windows, partial compensation, restart, outbox atomicity, cross-Soul reads, provider mutation freeze, revocation release, and revoke crash recovery. Production remains human-approved behind the feature flag and kill switch, with signed-BOM rollback.
