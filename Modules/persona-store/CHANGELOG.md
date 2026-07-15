# Changelog

## 0.4.0 - 2026-07-15

- Sealed `persona.history.export/v1` with an exact snapshot tail, HMAC cursor/request/receipt, canonical payload SHA-256, immutable receipt ID, and stable replay across later correction, deletion, and process restart.
- Bound PostgreSQL export receipt writes to an immutable deployment-key digest, server-recomputed HMAC proofs, and the complete authoritative relational history so direct runtime SQL cannot poison an append-only idempotency key with malformed JSON.
- Recomputed every retained trait commitment before export using fixed-time digest comparisons; added exact 10,000-revision and 16-MiB boundaries.
- Added append-only PostgreSQL export receipt/quarantine truth in the same Soul-serialized transaction as snapshot reads, while documenting issued export copies as a separate retention/deletion surface.
- Removed schema-adoption TOCTOU with exact migrator identity/ownership and non-idempotent schema creation; added column ACL revocation, `pg_attribute.attacl` attestation, and real PostgreSQL fail-closed tests.
- Replaced the nonexistent PostgreSQL JSON object-length helper with PostgreSQL 18 `jsonb_object_keys` cardinality.

## 0.3.0 - 2026-07-15

- Added fixed-root verification of `binding.composition.attestation/v1`, exact Binding fence-client/artifact/BOM/configuration/trust-epoch binding before database access, and a migrator-recorded composition fence on every mutation.
- Revoked runtime raw DML, sequence, and internal-helper access; introduced fixed `SECURITY DEFINER` receipt, atomic mutation, exact key-read, and dispatch APIs with deferred bundle verification.
- Tightened wire/schema/database validation for absolute string endings, zero-offset UTC with seven-digit precision, signed Int64 bounds, strictly sorted unique trait/evidence arrays, closed stored-trait vocabulary, and database-canonical JSON checksums.
- Expanded pre-DDL catalog attestation to owners, ACLs, sequences, function security configuration, roles/memberships, default ACLs, and effective schema/database privileges.
- Added the provider-owned 27-case raw `persona.revision/v1` differential corpus for Schema/.NET consumers.
- Locked that corpus to its exact adversarial case IDs and added `persona.history.export/v1`, which exports every retained raw trait revision after closed-vocabulary, exact-key, and keyed-commitment verification.
- Corrected deletion claims: `deleted` now means `LIVE_PRIMARY_LOGICAL_DELETED`; `KMS_CRYPTO_ERASURE_NOT_IMPLEMENTED` blocks final-erasure claims until external KMS and backup/WAL/replica/cache/export/downstream deletion receipts exist.

## 0.2.0 - 2026-07-14

- Added a PostgreSQL 18.4 append-only persona revision ledger with atomic current pointer, idempotency receipt, checksummed outbox, conflict quarantine, and crash-window recovery.
- Replaced caller-supplied binding proofs and the racy pre-commit re-read with Binding's public `identity.binding.mutation.fence/v1` lease, held from authoritative active-revision resolution through Persona commit.
- Added deterministic closed-vocabulary persona traits, exact Soul/device/account reads, optimistic revision concurrency, correction as a new revision, and audited tombstone erasure of separate trait payloads.
- Added distinct migrator/runtime roles, five-second operation limits, immutable-table update/delete/truncate triggers, locked Npgsql dependencies, and a required real-PostgreSQL Integration suite.
- Kept persona current-state reads deterministic and explicitly excluded GBrain transport, embeddings, similarity, and semantic search.
- Replaced enumerable low-entropy trait digests with independent per-Soul HMAC commitments, added same-transaction key destruction, deployment-scoped request HMAC idempotency, strict JSON parsing, fixed opaque IDs, immutable catalog attestation, exact deferred bundle checks, and a real Binding/PostgreSQL fence composition test.

## 0.1.0 - 2026-07-14

- Added the proposed evidence-backed persona revision contract and deterministic fail-closed module boundary.
- Removed the stale `persona.revision/v1` outbound declaration to `soul-memory-adapter`; GBrain projection now reaches the adapter only through `gbrain-projector`.
