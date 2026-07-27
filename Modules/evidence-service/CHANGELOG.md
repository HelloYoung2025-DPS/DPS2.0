# evidence-service changelog

## Unreleased

- 2026-07-27: Migrated the consumed projection contract: as of this batch the vertical-slice composer consumes `gbrain.projection/v2`.
- Tightened evidence identities and idempotency to exact opaque forms in DTO, Schema, persistence columns, and the cross-module vertical composer.
- Registered the module governance boundary. This entry does not claim runtime implementation or production verification.
- Added the F2 cryptographically attested evidence pipeline with complete event-set binding, role-separated runner trust policy, exact identity-scope reads, and fail-closed release evaluation.
- Added transactional PostgreSQL persistence for canonical receipts and immutable source artifacts, restart-time digest/signature verification, conflict quarantine, and real PostgreSQL 18.4 vertical-slice tests.
- The module remains proposed and is not release eligible; these changes do not claim Windows, device, canary, scale, or production verification.
