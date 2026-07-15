# GBrain Company compatibility probe

Status: local diagnostic capability probe only. This document is not `INTEGRATION_VERIFIED`, `DEVICE_VERIFIED`, or production approval.

F2 contract-binding status (2026-07-15): **STALE**. An independent audit
invalidated the current projection/source-binding candidate freeze. Its hashes
remain useful only for drift detection until the repaired v2 Schema, DTO,
canonicalization, PostgreSQL isolation, and reciprocal compatibility set are
independently re-audited and frozen.

Probe date: 2026-07-14

## Verified local facts

- The installed CLI reports `gbrain 0.42.42.0`; its Bun lock resolves `github:garrytan/gbrain#4ee530f3c545b880cecc47c4f877e0ed014896b4` with integrity `sha512-3Jg1uokovaZpatjEp5xUFh7OKeWHsYf4jJV4V/LzNDnhw3sT+1xk7ZEHBxphHAosZt60NU08UJaDKvXUQS1e6g==`. This is a local diagnostic baseline, not permission to track a moving branch in a Release BOM.
- A disposable PGLite brain initialized outside the Git worktree with embeddings disabled and applied schema migrations through version 116.
- Two isolated, non-federated Sources were registered successfully in the disposable brain.
- The same slug was written with different deterministic test content into both Sources. Exact `get_page` calls under each Source returned only that Source's row, Source ID, content hash, and content.
- Soft-deleting the slug in Source A left the identical slug in Source B readable and unchanged. Source A's deleted row was visible only through the explicit deleted-record read path. This demonstrates local engine isolation and recoverable deletion behavior, not final privacy erasure.
- Native GBrain Source identifiers are limited to 1–32 lowercase alphanumeric characters with optional interior hyphens. The native expression is <code>^[a-z0-9]&#40;?:[a-z0-9-]{0,30}[a-z0-9]&#41;?$</code>.
- GBrain OAuth clients support one bound write Source and an explicit federated-read allow-list. HTTP MCP dispatch obtains the write Source from authenticated client context rather than trusting a caller-supplied page field.
- The installed MCP surface includes exact page read/write/delete operations and Source-scoped query. Page deletion is initially recoverable; hard deletion is a separate administrative lifecycle.

The disposable probe used synthetic letters and digests only: no DPS Soul data, phone data, production credential, or embedding request. All known model and embedding key environment variables were removed from the write/read/delete processes. Its health check reported expected warnings because HTTP serving, embeddings, and the optional retrieval-reflex integration were intentionally disabled.

## DPS binding decision

`gbrain-projector` remains the sole authority for the Source identifier carried
by the candidate `gbrain.projection/v2` and `gbrain.source.binding/v1`. The
identifier must satisfy GBrain's native 32-character limit. The candidate
algorithm pending re-freeze is:

```text
dps- + first28hex(
  SHA256(
    ASCII("dps.gbrain-source-binding/source-id/v1\0")
    || complete ASCII soul_id
    || NUL
    || signed int64 big-endian nonce
  )
)
```

Nonce is restricted to 0..1023 and is allocated by the single Source-binding
authority. The binding retains the complete 256-bit Soul body, allocation time,
canonical revision, and checksum. A collision advances the nonce; exhaustion
quarantines the Soul. The old `dps- + first28(soul body)` mapping is historical
and collision-prone and must never be used by F7. A live adapter rejects any
Source whose canonical binding bytes, full Soul hash, OAuth write binding, or
read-back Soul differs. Prefix matching is never sufficient.

## Required live adapter behavior

1. Resolve and validate canonical `gbrain.source.binding/v1` bytes, then require
   the same Source/nonce/binding revision/checksum in exact
   `gbrain.projection/v2` bytes.
2. Obtain a short-lived OAuth token from a client bound to exactly that Source. Credentials are resolved outside Git and never logged.
   F7 independently compares the native GBrain Source ID returned by OAuth
   whoami with the binding; an adapter-local alias is bound separately and
   cannot replace the native Source proof.
3. Call only fixed, allow-listed MCP operations with bounded request sizes, cancellation, and timeouts.
4. Write a deterministic page containing projection v2, the complete Source
   binding proof, identity scope, revision, and checksum.
5. Read the exact slug back; semantic or keyword search cannot establish Persona current-state truth.
6. Compare Source, nonce, full Soul hash, binding proof, schema, contract,
   revision, content bytes, and checksum before issuing `verified`.
7. Treat timeout after write as `UNKNOWN_OUTCOME`; reconcile with exact read-back instead of blind retry.
8. For search, constrain the OAuth read scope and independently revalidate every returned Source/Soul/schema/provenance/freshness field.
9. For correction, export, rebuild, and deletion, record an auditable plan and verify the resulting page/chunk/cache lifecycle. Soft delete alone is not proof of final erasure.

## Secret boundary

DeepSeek, Voyage, GBrain OAuth, and database credentials must be supplied through the external secret provider or a runtime-only secret-file reference. The repository, module manifests, logs, evidence JSON, test fixtures, screenshots, and GBrain pages contain neither secret values nor user-specific secret-file paths.

The current local key files were not read, copied, moved, or printed during this probe.

## Unverified work

- No Source-bound OAuth client was minted or used by DPS code.
- No live HTTP MCP write/read/delete/rebuild test has run.
- Voyage embeddings and DeepSeek calls remain unconfigured in the disposable probe.
- No two-Soul live isolation attack, non-production phone, Windows, ZennoDroid, 30-device canary, or 200-device scale evidence exists.

These items remain mandatory F7–F9 gates and cannot be replaced by the disposable PGLite result.
