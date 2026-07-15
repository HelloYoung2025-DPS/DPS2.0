# Test evidence boundary

The executable project contains 15 `Unit` and 14 `Contract` tests. Unit tests use only a test-owned ephemeral P-256 key and a class named `TestOnlyInMemoryReceiptStore`; neither is a production composition. Contract tests execute the shared 2-valid/17-invalid corpus and the fixed public-root metadata without using a private production key.

Mock and diagnostic execution cannot prove durable-store, HSM/external-signer, PostgreSQL, Windows, device, canary, or scale behavior. Required restore, build, and filtered test commands are authoritative only when they exit zero without `SKIP`, `PARTIAL`, missing evidence, or infrastructure errors.
