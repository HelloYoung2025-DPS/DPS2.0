---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: device-registry
manifest: ./module.yaml
applies_to: .
---

# Device Registry Agent Rules

## Scope

This module owns stable device identity, key-id-and-epoch-bound fingerprint HMAC digests, bounded capability revisions, device lifecycle state, and provider-side reservations that freeze an exact revision for binding. It does not own the fingerprint HMAC secret, Souls, platform accounts, the authoritative binding relationship, commands, selectors, actions, or GBrain access.

## Required reading

Before writing, read the root `AGENTS.md`, this file, `module.yaml`, provided and consumed contracts, the generated dependency graph and compatibility matrix, `tests/README.md`, and `operations/README.md`. Bind exact hashes and rebind if the diff expands. A receipt is not test or approval evidence.

## Invariants

- `SOUL-ISO-001`: every public result preserves a canonical Soul, binding, and account operation scope, but device identity never establishes that relationship; `binding` remains authoritative.
- `CMD-IDEMP-001`: an idempotency key cannot identify two different registration payloads.
- `RESULT-VERIFY-001`: a device is usable only after its identity and capability revision validate.
- `GBRAIN-READBACK-001`: this module never accesses GBrain.
- `EDGE-NORESTART-001`: this module cannot claim Windows, ZennoDroid, or device verification.
- Store only `fingerprint_hmac_sha256` with its canonical `fingerprint_key_id` and positive epoch; never store raw hardware identifiers, an unkeyed fingerprint digest, or the HMAC secret.
- Binding consumers may trust only the provider instance whose secret-free configuration SHA-256 and positive `trust_epoch` are pinned by the signed composition attestation; the digest binds the PostgreSQL target, schema, fingerprint key ID/epoch, and trust epoch.
- Accept only exact `1.0.0` schemas and strict JSON DTOs; unknown and missing properties fail closed.
- `trace_id`, `device_binding_id`, and `platform_account_id` are respectively `trace_`, `db_`, and `pa_` plus exactly 32 lowercase hexadecimal characters; the repo-wide `idempotency_key` is `idem_` plus exactly 64 lowercase hexadecimal characters.
- Capability input is bounded before enumeration to 64 canonical ASCII identifiers, each at most 64 bytes and at most 4096 bytes in total.
- Unknown contract majors and malformed identity context fail closed.
- Capability changes and retirement use the same device lock as reservation transitions and fail closed while a held-unexpired or active reservation exists.

## Communication and boundaries

Communicate only through the versioned API, `device.registered/v1`, `device.binding.reservation/v1`, and declared queries, commands, receipts, or events. All three common identity fields are canonical and non-null; UUID and `not_applicable` placeholders are forbidden. Do not read another module's tables, import internal types, share mutable static state, or add undeclared peers. Public contract changes require all declared consumers to bind their own instructions before another write.

## Tests, rollout, and rollback

Required checks release only on `PASS`. Test stable keyed identity, key-version mismatch, provider-configuration digest and trust-epoch binding, populated unkeyed-schema rejection, conflicting idempotency keys, strict JSON, capability bounds, capability revision concurrency, reservation expiry/confirmation/release, mutation freeze, unknown versions, and cross-device isolation. The module remains proposed and cannot be released until a signed BOM, shadow verification, bounded canary, kill switch, and rollback are independently approved.
