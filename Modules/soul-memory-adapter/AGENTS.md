---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: soul-memory-adapter
manifest: ./module.yaml
applies_to: .
---

# Soul Memory Adapter Agent Rules

## Scope

This module is the only domain-facing abstraction for SoulMemory projection preparation and exact GBrain Company read-back verification. It consumes the complete `gbrain.projection/v2` DTO and never derives a second Source identifier, projection revision, or projection checksum. It owns the fixed OAuth/MCP adapter and exact page-validation rules, but it owns no GBrain data, credential store, Persona truth, identity binding truth, planner decision, or device action.

## Required reading

Before writing, read the root rules, this file, `module.yaml`, all provided and consumed contracts, the dependency and compatibility snapshots, tests, and operations guidance. Rebind instructions if the diff or consumer set changes.

## Invariants

- `SOUL-ISO-001`: every projection and read-back is scoped to one canonical Soul, binding, account, and projector-owned `dps-<28 lowercase hex>` Source. An external `gs_<digest>` evidence alias is never a GBrain Source.
- `CMD-IDEMP-001`: preserve the projection's Source, revision, and checksum byte-for-byte. Identical redelivery is a no-op; conflicting reuse fails closed; a dispatch is durably quarantined before the remote call and remains quarantined until exact reconciliation. It is never blindly retried.
- `RESULT-VERIFY-001`: a write acknowledgement, prepared data, or search hit is not success. Only exact Source, Soul, device, account, schema, contract, revision, checksum, provenance, and freshness validation produces a verified read-back.
- `GBRAIN-READBACK-001`: every write is followed by exact `get_page` read-back. Search results are re-read exactly. Persona current uses only the fixed `profile/persona/current` page and never semantic search.
- `EDGE-NORESTART-001`: this module cannot claim Windows, ZennoDroid, phone, canary, or scale verification.
- Production endpoints require HTTPS and exact pinned authority/path. Redirects, proxies, cookies, transparent decompression, caller-provided URLs, dynamic client registration, arbitrary MCP tools, and unknown protocol versions are denied.
- Credentials come only from the composition root's external secret source, are bound to exactly one Soul and Source with exactly `read write`, are held in disposable leases, and never enter public contracts, Git, logs, screenshots, prompts, or GBrain pages.
- Every projection mutation requires an atomic durable `IGBrainProjectionMutationJournal` reservation and a durable fenced per-Soul exclusive mutation lease supplied by the composition root. The journal must bind the complete intent, mark `dispatched-unresolved` before the remote call, block every later Soul mutation while unresolved, and transition only after exact proof. The lease must expose a monotonic fence and loss/expiry signal. In-memory journals and semaphores are test-only.
- Delete and rebuild are explicit versioned intents. A delete must carry its own trace, idempotency key, mutation time, and exact expected revision/checksum; a scope-only delete is forbidden. A stale delete replay must never delete a newer or rebuilt current page.
- A candidate at or before the latest verified mutation `occurred_at`, a reused revision with different checksum, a reused idempotency key with different content, a lost fence, or a different unresolved mutation fails before another side effect.
- GBrain deletion is soft-delete. Never claim hard erasure of pages, chunks, embeddings, caches, or backups without separate operator evidence.

## Communication and boundaries

Consume `gbrain.projection/v2`; provide `soul.memory.readback/v1`. The projection contract is the single Source binding and projection truth. OAuth protected-resource and authorization-server discovery, token audience, MCP protocol version, session, capability schemas, tool results, Source binding, and page provenance are untrusted and must be checked exactly. Unknown majors, tools, actions, scopes, endpoints, versions, capability shapes, or mismatched read-back fail closed.

The only remotely callable operations are the module's fixed `get_page`, `put_page`, `search`, `delete_page`, `restore_page`, and `whoami` flows. Callers may select a domain operation and bounded query, not an MCP operation name, URL, slug, credential, or raw protocol payload.

## Tests and rollout

Required checks release only on `PASS`. Cover canonical rendering, deterministic Source isolation, exact write/read-back, duplicate delivery, commit-then-disconnect reconciliation, unresolved-mutation quarantine, lease loss, stale delete replay after rebuild, search revalidation, deterministic Persona current, soft-delete/rebuild, OAuth audience and credential scope, MCP version/session/capability drift, redirects, oversized responses, and cross-Soul rejection.

The in-process `SecuritySimulation` suite is simulated integration evidence only. It cannot satisfy real-GBrain, Windows, phone, F7, canary, or scale gates. Production remains disabled until a clean project build, real PostgreSQL gate, production implementations of the durable mutation journal and fenced/loss-signalling mutation lease, real GBrain Company exact write/read-back/delete/rebuild evidence, two non-production Souls, and human R3 approval exist. Rollback disables new writes, retains exact reads for reconciliation, restores the previous signed BOM, and preserves the rule that an unverified external mutation is never success.
