# gbrain-projector changelog

## Unreleased

- Tightened `gbrain.source.binding/v1` and `gbrain.projection/v2` to exact schema versions, bounded candidate computation to nonce `0..1023`, and made both the DTO and Source authority recompute the fixed `soul_id + nonce` derivation before trusting a binding.
- Removed injectable Source generation from the production authority/store path; collision tests now preoccupy real fixed-algorithm candidates without minting false bindings.
- Split PostgreSQL migration and runtime credentials. The migrator now rejects shared/impersonated/elevated runtime identities, weak precreated schemas, catalog drift, mixed owners, unexpected constraints/indexes/triggers, and non-exact ACL before adoption; runtime initialization performs attestation but no DDL.
- Added UPDATE/DELETE and TRUNCATE protection for all ledgers, canonical-text/JSONB equality checks, exact runtime SELECT/INSERT-only grants, and read-back verification across relational columns, canonical bytes, JSONB, checksum, and referenced binding.
- Added adversarial unit, contract, and real-PostgreSQL tests for exact versions, nonce bounds, forged bindings, strict adoption, role separation, ACL, TRUNCATE, and relational/canonical/JSONB splits. Real PostgreSQL evidence remains `INFRA_ERROR/NOT_RUN` until both external credentials exist; release eligibility remains false and consumer v2 reciprocity remains red.

- Byte-froze `gbrain.projection/v1`, its v1 DTO, and the legacy collision corpus; moved v1 to deprecated quarantine-only with no runtime communication edge.
- Added active proposed `gbrain.source.binding/v1` and `gbrain.projection/v2`, binding full Soul hash, domain-separated SHA-256 nonce allocation, allocation time, binding revision/checksum, and projection revision/checksum.
- Added the fixed process-bound Source binding authority and non-directly-constructible renderer capability; raw caller-selected Source IDs are no longer accepted by the v2 renderer.
- Added PostgreSQL unique full-Soul Source allocation with same-Soul idempotency, bounded collision retry, nonce-exhaustion quarantine, restart verification, and append-only binding/revision tables.
- Added tests for the retained v1 collision pair, v2 distinct mapping, injected collisions, concurrent replay, restart, tamper, cross-Soul capability misuse, nonce exhaustion, revision rollback, and real PostgreSQL persistence. Real PostgreSQL and GBrain evidence remain external blockers; the module remains release-ineligible.

- Bound each proposed GBrain Source to `dps-` plus the first 112 bits of the Soul digest, published the collision/read-back corpus, and required the full Soul/revision/checksum tuple to remain authoritative when a truncated Source collides.
- Tightened the projection envelope, nested evidence hashes, and UTC timestamps to exact canonical v1 forms.
- Registered the module governance boundary. This entry does not claim runtime implementation or production verification.
- Added the F2 deterministic GBrain projection DTO renderer with Soul-owned source IDs, exact event/evidence matching, revision/checksum guards, and no network write path.
- Added unit, contract, cross-scope, duplicate/conflict, and real PostgreSQL-backed vertical-slice tests.
- Froze `gbrain.projection/v1` as the sole Source binding, projection revision, and projection checksum authority and declared `soul-memory-adapter` as an exact consumer without adding a network path.
- The module remains proposed and is not release eligible; these changes do not claim a GBrain write/read-back, Windows, device, canary, scale, or production verification.
