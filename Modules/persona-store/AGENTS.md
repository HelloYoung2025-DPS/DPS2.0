---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: persona-store
manifest: ./module.yaml
applies_to: .
---

# Persona Store Agent Rules

## Scope

This module owns Soul-scoped persona revisions and their evidence references. It does not own identity resolution, device or account bindings, interests, long-term event history, GBrain transport, planning, or device actions.

## Required reading

Before writing, read the root rules, this file, `module.yaml`, every contract, dependency and compatibility snapshots, tests, and operations guidance. Rebind instruction hashes whenever the diff or a public-contract consumer expands.

## Invariants

- `SOUL-ISO-001`: every revision is scoped by canonical `soul_id`, `device_binding_id`, and `platform_account_id`; cross-Soul reads fail closed.
- `CMD-IDEMP-001`: one idempotency key cannot represent two persona mutations.
- `RESULT-VERIFY-001`: a revision requires a binding-owned active mutation fence held through transaction completion, optimistic revision match, and at least one evidence digest.
- `GBRAIN-READBACK-001`: this module stores no GBrain credential and performs no GBrain network call.
- `EDGE-NORESTART-001`: no Windows, ZennoDroid, or device evidence is issued here.
- Public contracts contain a deterministic per-Soul keyed commitment and allowed trait keys, never raw email, phone, platform identifiers, or credentials. Status `deleted` means `LIVE_PRIMARY_LOGICAL_DELETED`: the live PostgreSQL primary payload/key rows were removed in the tombstone transaction. It is not final cryptographic erasure.
- `KMS_CRYPTO_ERASURE_NOT_IMPLEMENTED` is a hard release blocker for every final-erasure claim. Only a future external KMS destruction receipt plus verified deletion/expiry evidence for WAL, backups, replicas, caches, exports, and downstream projections may establish final erasure.
- Persona trait values are a closed, versioned vocabulary. Current persona is read only by the exact Soul/binding/account tuple; semantic search, similarity search, and embeddings are forbidden here.
- PostgreSQL revision, receipt, outbox, quarantine, and legacy-named `erasure_audit` rows are append-only. Trait payload deletion is permitted only as part of the audited live-primary logical-deletion transaction.
- A signed history-export receipt is an immutable, sensitive export copy with its own retention/deletion policy. Replaying its exact idempotency key returns the original sealed snapshot, not current live state; logical deletion does not claim to erase previously issued export receipts.

## Communication and boundaries

Consume the public `binding.composition.attestation/v1` and `identity.binding.mutation.fence/v1` contracts. Before any database access, production composition must verify the fixed pinned root, signed BOM/artifact/configuration/trust epoch, current generation, and Binding's exact non-public sealed fence client. Callers cannot submit a binding revision or proof: Binding atomically resolves the exact active revision while acquiring its session fence, and Persona must retain that lease until its transaction commits or rolls back. Production source must not reference Binding implementation source, another module's internal types, or another module's tables. Provide only `persona.revision/v1` and exact Soul-scoped queries. Unknown keys, versions, producers, scopes, stale revisions, and an unavailable or invalid fence are rejected.

## Tests and rollout

Required checks release only on `PASS`. Cover deterministic hashing, duplicate delivery, stale updates, cross-Soul/device/account isolation, concurrent optimistic revision, crash windows, correction, audited live-primary logical deletion, immutable history, runtime-role denial, and unknown major rejection. PostgreSQL operations and binding reads have a maximum five-second timeout. Production remains human-approved with a feature flag, bounded canary, kill switch, and signed-BOM rollback.
