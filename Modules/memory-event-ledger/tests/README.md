# memory-event-ledger tests

This directory separates unit, contract, integration, resilience, Windows, and device evidence. The exact required suites and commands are declared in ../module.yaml.

A required check releases only on PASS. FAIL, SKIP, PARTIAL, NOT_RUN, INFRA_ERROR, NOT_APPLICABLE, an empty test set, or missing raw evidence is a failure. Mock evidence must be labelled and cannot satisfy integration, Windows, ZennoDroid, ADB, device, canary, or scale gates.

The first implementation slice is side-effect free. PostgreSQL integration must use a real PostgreSQL 18.4 instance; an in-memory replacement cannot satisfy the required integration suite.

v2 unit/contract coverage includes forged v1 booleans and Soul DTOs, wrong/cross scope, raw byte swaps, wrong keys, stale/equal-expiry/revoked authorities, signed-receipt replay under another event ID, unknown/acted outcomes, capability reconstruction/JSON round-trip, canonical JSON limits, and signal bounds. Integration requires distinct bootstrap/admin/runtime roles and covers atomic event/outbox crash windows, duplicate no-op, same-id conflict quarantine, concurrency, chain replay, runtime role bypass, direct DML, UPDATE/DELETE/TRUNCATE, cross-Soul privacy references, and privacy-table grants. Missing `DPS_TEST_POSTGRES` is `INFRA_ERROR/NOT_RUN`, never a skip or pass.
