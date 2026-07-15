# Changelog

## 0.1.0 - proposed

- Established `platform-authorization-authority` as the sole owner and producer of `platform.account.authorization.evidence/v1`.
- Added the strict .NET 10 contract DTO, JSON codec, canonicalizer, schema, and public fixed P-256 issuer/root pin metadata.
- Added the evidence issuer with a composition-bound untrusted-proof verifier, trusted Release BOM/generation provider, externally backed P-256 signer adapter, and durable exact-envelope receipt-store contract.
- Added byte-identical replay and conflicting-payload quarantine semantics without persisting raw platform proof; the receipt payload digest now also binds the runtime BOM, generation, trust epoch, and context digest.
- Kept production composition internal and unavailable to arbitrary callers until an attested trusted host and concrete durable store exist.
- Tightened all three UTC fields, platform, alias key ID, evidence ID, Int64 bounds, and canonical P1363 signatures with a shared 2-valid/17-invalid corpus.
- Added 15 unit and 14 contract tests, including wrong-root, stale-proof, runtime-generation race, stale-BOM/expired replay, defensive-copy, conflict, recursive duplicate, year-zero, leap-second, and noncanonical-envelope attacks.
- Kept production private keys, raw platform credentials, account lifecycle state, and device behavior outside the module boundary.
- Production issuance remains disabled until a production durable-store implementation, real external signer/HSM composition, integration evidence, canary, and rollback evidence are implemented.
