# Changelog

## Unreleased - 2026-07-27

- Migrated the consumed projection contract: as of this batch the adapter consumes `gbrain.projection/v2` (nonce-witnessed Source binding). Earlier entries below describe the `gbrain.projection/v1` consumption that was current at their dates and are unchanged.

## 0.2.0 - 2026-07-15 (proposed)

- Added a strict GBrain Company HTTP/OAuth/MCP adapter with HTTPS-only production endpoints, exact issuer/resource/token authority, disabled redirects/proxies/cookies/decompression, bounded requests/responses/concurrency/timeouts, and no caller-selected URL or tool name.
- Added RFC 9728 protected-resource discovery, RFC 8414/OIDC authorization-server discovery, explicit resource-audience binding, per-Soul external credential leases, exact `read write` scope enforcement, token/secret clearing, and `whoami` verification; dynamic client registration and admin/federation scope are denied.
- Added MCP `2025-11-25` preference with an explicit three-version allowlist, initialized notification, session propagation/close, paginated `tools/list`, and exact capability-profile rejection before domain calls.
- Added native per-Soul `dps-<28 hex>` Source binding verification, exact projection write/read-back, duplicate no-op, commit-then-disconnect reconciliation without blind mutation retry, exact search revalidation, deterministic Persona-current reads, soft-delete verification, and explicit rebuild.
- Replaced tuple-only idempotency with a mandatory durable per-Soul mutation journal that reserves complete versioned intents, records `dispatched-unresolved` before remote side effects, blocks later mutations while unknown, and transitions only after exact reconciliation.
- Added versioned delete and rebuild operation intents. Delete now requires its own trace/idempotency/time and exact expected revision/checksum, so an old delete replay cannot remove a newer or rebuilt current projection.
- Strengthened the mutation lease port with a monotonic fence token and explicit loss/expiry signal; a lost fence leaves the journal quarantined and prevents a second mutation.
- Closed UNKNOWN_OUTCOME gaps for cancellation, malformed mutation responses, and reconciliation failures; active-only reads now reject soft-deleted pages, and search candidates must carry the exact native Source before exact re-read.
- Added the pinned GBrain `0.42.42.0` capability/provenance fixture and a 23-case in-process `SecuritySimulation` suite covering isolation, stale-delete replay, unresolved-mutation quarantine, lease loss, and adversarial failure paths without sockets, public network, real credentials, or side effects.
- Restored the real PostgreSQL suite to the central exact `Integration` category while keeping it `REAL_POSTGRESQL`; the protocol suite is separately labelled `SecuritySimulation`/`SIMULATION` so neither can be confused with future real-GBrain/device evidence.
- The module remains proposed and release-ineligible. No real GBrain, Windows, ZennoDroid, phone, canary, scale, or F7 verification is claimed; hard erasure beyond GBrain soft-delete remains unverified.

## 0.1.0 - 2026-07-14

- Added the shared 25-case read-back corpus and bounded duplicate/unknown-field-rejecting codec; prepared records cannot verify until exact full Soul, Source, revision, checksum, device, and account read-back succeeds.
- Aligned the Source mapping to the GBrain-compatible 112-bit `dps-<28 lowercase hex>` alias while explicitly failing closed on truncated-Source collisions.
- Added the proposed network-free SoulMemory DTO and exact read-back verification contract.
- Replaced the parallel `source_<digest>` and integer revision model with the exact `gbrain.projection/v1` Source ID, SHA-256 revision, and canonical checksum tuple.
- Declared `gbrain-projector` as the sole projection provider and added exact offline verification for Soul, device, account, Source, schema, contract, revision, and checksum.
- Added a required real-PostgreSQL offline Integration suite covering the production memory event/outbox path, deterministic reduction and projection, canonical UTF-8 persistence/read-back, two-Soul Source isolation, duplicate/conflict handling, mismatch rejection, restart replay, and transaction-failure recovery.
- The Integration fixture is test-only, uses a randomized schema, and enforces `live_gbrain=false`; it adds no runtime PostgreSQL permission, owned store, GBrain credential, or production dependency.
- The adapter remains proposed and network-free; this does not claim a live GBrain write/read-back or F7 verification.
