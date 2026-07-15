# Tests

Run `python3.12 -m unittest Modules/factory-upgrade-intake/tests/test_upgrade_intake.py` for deterministic intake behavior. The suite uses process-composed verification authorities and covers exact authority-instance issuance, trusted-clock expiry, human-approval anti-replay, four contract-change kinds, provider-mode/status transitions, Manifest snapshot binding, concrete path ownership, strict bounded JSON, domain-separated digests, v1 fixed quarantine metadata, and the explicit external durable-idempotency boundary.

Run `python3.12 -m unittest Modules/factory-upgrade-intake/tests/test_contracts.py` for Draft 2020-12 contract checks. Production-normalized output must validate against `upgrade.intent/v2`; the owned corpus records exact positive component digests and a `schema_rejects` expectation for each negative. Every negative must independently satisfy its Schema expectation and be rejected by production validation. The suite also pins the exact v1 Schema SHA-256 and the v2 Schema source digest in the corpus.

Required outcomes release only on `PASS`; `SKIP`, `PARTIAL`, missing, or infrastructure-only evidence fails the gate. These local results support repository-static review only. They are not a signed contract-evidence envelope, PostgreSQL replay proof, integration evidence, or production release approval.
