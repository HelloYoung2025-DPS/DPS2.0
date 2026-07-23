# Changelog

## 0.2.0 - Proposed

- Restored the original `rollout.command/v1` and `rollout.event/v1` schema bytes, pinned both SHA-256 values, and declared both majors deprecated quarantine-only with no runtime communication edge.
- Added active `rollout.command/v2` and `rollout.event/v2` for the Host boundary; runtime command processing, durable output, and recovery accept only v2.
- Replaced direct Intake/`upgrade.intent` consumption with host-authored `rollout.command/v2`; removed caller-supplied risk and reject raw intent v1/v2 at every command boundary.
- Required v2 to carry one to 64 unique canonical receipt references and bound them to trusted resolved references plus a non-zero `receipt_set_sha256` before any ledger append.
- Added adversarial proof that a complete frozen v1 command writes no event and a complete frozen v1 event cannot rebuild release state.
- Replaced permissive receipt parsing with an explicit kind allowlist and strict non-normalizing identifier grammar; raw-intent masquerades, repeated separators, mixed case, and extra components now fail closed.
- Added dependency-free strict v2 wire decoding, real canonical UTC validation, strict upgrade identity grammar, and exact boolean/nonnegative-integer runtime checks with schema/runtime attack tests.
- Added the full post-authentication v2 semantic replay verifier, but disabled the public recovery entrypoint with `WAITING_EXTERNAL` until a fixed authenticated Evidence Ledger head/currentness provider exists; locally recomputed payload/event hashes, a forged matching DTO, and an arbitrary resolver callback cannot authorize a tampered CANARY replay.
- Tightened all 19 terminal patterns in `release.bom.native.stop.authority.trust/v1` to absolute ECMAScript end semantics and added per-pattern LF/CR attacks.
- Registered `release.bom.native.stop.authority.trust/v1` as the Release Controller's unique active proposed public contract with an exact receipt route to the Host, fail-closed unknown-major policy, and an explicit no-raw-secret boundary. The unpublished hyphenated draft identity is rejected and has no compatibility alias.
- Required the candidate and previous signed Release BOM bytes to equal their unique sorted compact canonical wire before any digest, signature, or external trust-receipt use. The unsigned external-signer payload builder and .NET trust verifier now enforce the same mapping/byte identity, Unicode-scalar key ordering, Python-compatible finite-number rendering, 4300-digit budget, and shared adversarial corpus; aliases, re-encoding, invalid Unicode, and non-finite values fail closed. The Owner fixture is reproducibly rebuilt as a new receipt issuance whenever its exact BOM bytes change.
- Enforced the repository-external BOM signer key profile in both the authoritative `Tools/ci` parser and this byte-faithful migration copy: every `bom` key is single-purpose RSA-PSS/SHA-256, uses canonical lowercase unsigned modulus hex without a leading zero nibble, has at least 256 modulus octets, and uses public exponent 65537. Full validator fixtures now separate artifact signing from a fixed synthetic 2048-bit test-only BOM key; production policy, signer contract, and compatibility corpus remain public-verification-only.

## 0.1.0 - Proposed

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms in runtime validation, Schema, simulation, and candidate fixtures.
- Registered the initial `rollout.event/v1` proposal for Host review; 0.2.0 supersedes that proposed route, and no v1 runtime edge remains.
- Added the fail-closed main and exceptional release state graph.
- Added process-bound trusted facts, role separation, signed candidate BOM validation, exact digest continuity, kill-switch and shadow side-effect gates.
- Added append-before-advance event persistence and external hash-chain recovery.
- Added a validation-only Release BOM CLI with a code-bound, caller-non-overridable RSA-PSS public trust anchor.
- Bound every required gate to required/PASS status, exact evidence kind, minimum verification level, tested commit and signature; derive the reported ceiling from gates that truly passed.
- Rejected duplicate and unknown JSON members across BOM, trust policy, Manifest, descriptor, SBOM, provenance, evidence, and approval boundaries, with deterministic resource ceilings for control files, nested collections, and streamed artifacts.
- Rebuilt the dependency DAG and compatibility matrix from exact integration-commit Manifests, enforcing dependency version ranges plus exact N/N-1 provider schema bindings rather than trusting matching snapshot hashes alone.
- Required the signed previous stable BOM to be in the candidate Git lineage and revalidated its governed repository bindings, artifact set, descriptor, SBOM, and provenance before accepting it as a rollback anchor.
- Enforced the trust-policy separation between the module implementer/requester, evidence issuer, release controller, and human release approver.
- Added deterministic 200 registered / 100 sustained / 200 burst / 400 equivalent fleet simulation, explicitly capped at `INTEGRATION_VERIFIED`.
- Hardened the Contract suite to load the exact module-owned production source under isolated Python without relying on `PYTHONPATH` or unit-test fixtures.
- Hardened the required Unit and candidate-BOM suites to load their exact module-owned production sources under isolated Python.
- Hardened the deterministic fleet Integration suite with the same fail-closed exact-path loader so it runs under isolated Python without `PYTHONPATH`.
- No Windows, device, canary, or scale verification is claimed.
