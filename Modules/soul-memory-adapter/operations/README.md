# Soul Memory Adapter operations

The GBrain Company adapter is implemented but remains `proposed` and production-disabled. The local in-process simulator is protocol evidence only: it does not call GBrain Company, a public endpoint, Windows, ZennoDroid, or a phone, and it uses no user credential.

## Deployment pin

The current diagnostic baseline is GBrain `0.42.42.0`, package source `github:garrytan/gbrain#4ee530f3c545b880cecc47c4f877e0ed014896b4`, package integrity `sha512-3Jg1uokovaZpatjEp5xUFh7OKeWHsYf4jJV4V/LzNDnhw3sT+1xk7ZEHBxphHAosZt60NU08UJaDKvXUQS1e6g==`, and Bun `1.3.14`. This is a local diagnostic fact, not a signed Release BOM. A release must pin and independently verify its exact server artifact, runtime, digest, schema pack, certificates, and previous stable BOM.

The client offers MCP `2025-11-25` and accepts only `2025-11-25`, `2025-06-18`, or `2025-03-26`; the diagnostic GBrain build negotiated `2025-03-26`. The negotiated version is sent on every subsequent MCP request. Unknown versions fail closed.

## Identity and Source provisioning

- `gbrain-projector` owns the native Source: `dps-<28 lowercase hex>` derived from the immutable `soul_id`.
- `gs_<16 hex>` is only an external F7 evidence alias. It must never be configured as the GBrain Source.
- Each Soul is provisioned as a distinct OAuth Source/context and has exactly one pre-created `projection/source-binding` page.
- Provision the binding page with `GBrainCompanyProvisioning.CreateSourceBindingMarkdown(scope)`, place it only in that Soul's Source, and verify it with exact `get_page` before any read or mutation.
- Email, phone, platform alias, OAuth secret, and GBrain admin credentials are never written to the page.

## OAuth and transport boundary

Production configuration requires HTTPS, an issuer root with no path, an MCP path exactly `/mcp`, and identical issuer/MCP authority. Redirects, cookies, proxy discovery, transparent decompression, user info, query strings, fragments, alternate authorities, and plaintext production traffic are rejected.

The adapter performs the following fixed flow:

1. Send an unauthenticated MCP initialize probe and require HTTP 401 Bearer challenge.
2. Resolve RFC 9728 protected-resource metadata from the challenge. GBrain `0.42.42.0` alone may use the exact same-authority path-aware/root metadata fallback when the challenge omits `resource_metadata`.
3. Require the protected resource audience and its sole authorization server to equal the deployment pin.
4. Discover RFC 8414 authorization-server metadata, with OIDC discovery as a same-authority fallback.
5. Require `client_credentials`, `client_secret_post`, `read`, and `write`; reject dynamic client registration and unexpected grants/scopes.
6. Request a token with an explicit `resource` audience and then verify the resulting OAuth context using fixed `whoami`.

The composition root supplies a per-Soul credential lease through `IGBrainOAuthClientCredentialSource`. The lease must be bound to the exact `soul_id`, native Source, one readable Source, and exactly `read write`. This module never reads credential files or environment variables. Secrets and access-token byte arrays are cleared when disposed.

Every write, rebuild, and soft-delete requires an `IGBrainProjectionMutationJournal`. Its production implementation must atomically bind a globally unique per-Soul idempotency key to the complete versioned intent, reject stale ordering, persist `dispatched-unresolved` before the remote call, block every later mutation for that Soul while unresolved, and mark `verified` only after exact proof. Every mutation also requires an `IGBrainSoulMutationLeaseProvider`; its production implementation must issue a durable exclusive lease with a monotonic fence token and a loss/expiry signal. The in-memory implementations in tests are not production implementations. These two ports compensate for GBrain mutation tools having no CAS/If-Match or native fence primitive.

`SoftDeleteProjectionAsync` accepts only a `gbrain.projection.delete-intent/v2` envelope containing the exact Source/Soul/device/account, trace, operation idempotency key, mutation time, expected revision, expected checksum, and Source-binding nonce witness. The journaled tuple is `gbrain.projection.mutation-intent/v2`. Both moved to major 2 with the `gbrain.projection/v2` Source binding: the required nonce witness makes every major-1 record invalid, so a major-1 delete or journal record is refused by contract major rather than silently reinterpreted. `RebuildProjectionAsync` likewise requires a separate `gbrain.projection.rebuild-intent/v1` operation envelope, which stays at major 1 because it carries no Source, Soul, or nonce. A scope-only delete, an equal/older operation time, a target mismatch, a reused key, a lost fence, or an unresolved prior dispatch fails closed. If an old delete is replayed after a rebuild or newer write, the active page is retained.

Requests have bounded body size, response size, concurrency, timeout, clock skew, pagination, and tool count. Capability schemas are compared to the checked-in fixture before any domain tool call. MCP sessions are initialized, used with the returned session ID, and closed best-effort.

## Fixed domain operations

The remote slugs and tools are not caller-selectable:

- Projection binding: exact `get_page projection/source-binding`.
- Projection current: `put_page` and exact `get_page projection/current`.
- Persona current: exact `get_page profile/persona/current`; semantic search is forbidden for current Persona truth.
- Projection search: fixed `search`, followed by exact `get_page` and complete scope/schema/revision/checksum/provenance/freshness validation for every candidate.
- Delete: exact target check, durable dispatch reservation, `delete_page projection/current`, then exact include-deleted target read-back. This proves soft-delete only.
- Rebuild: versioned operation intent, exact deleted read, durable dispatch reservation, fixed `restore_page` when appropriate, fixed write, and exact final read-back.

An identical existing projection returns as a no-op without a second write. A different projection may replace it only while holding the current per-Soul fence, after durable journal reservation, and only when its `occurred_at` is strictly newer. Reused keys, reused revisions with different checksums, stale versions, and equal-time ambiguous versions fail before mutation. The journal moves to `dispatched-unresolved` before any remote side effect. If a mutating exchange may have committed before connection loss, cancellation, lease loss, malformed response, or read failure, the Soul remains quarantined; an attempted newer mutation is rejected. Only replay of the exact intent with an exact safe read-back, or separately authorized operator repair, may clear the state. The adapter never blindly retries a mutation.

## Rollout and rollback

The feature flag is `soul_memory_gbrain_company_v1`; the kill switch is `kill_soul_memory_gbrain_company`. Rollout begins shadow/read-only, then uses two isolated non-production Souls with operator-provisioned credentials. Each write, search, Persona read, soft-delete, and rebuild must retain raw redacted evidence and be approved as R3 before production.

Rollback disables new writes first, retains exact reads long enough to reconcile unknown outcomes, restores the previous signed BOM, and records the last verified revision per Soul. A soft-delete response is never represented as legal or physical erasure. Hard purge, chunk, embedding, cache, and backup deletion remain external operator work with separate proof.

F7 remains `WAITING_EXTERNAL` until a real GBrain deployment and two authorized non-production phones/Souls are available. Simulator success cannot raise the repository to `DEVICE_VERIFIED`.
