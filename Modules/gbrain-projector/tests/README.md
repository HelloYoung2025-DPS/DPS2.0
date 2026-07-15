# gbrain-projector tests

This directory separates unit, contract, integration, resilience, Windows, and device evidence. The exact required suites and commands are declared in ../module.yaml.

A required check releases only on PASS. FAIL, SKIP, PARTIAL, NOT_RUN, INFRA_ERROR, NOT_APPLICABLE, an empty test set, or missing raw evidence is a failure. Mock evidence must be labelled and cannot satisfy integration, Windows, ZennoDroid, ADB, device, canary, or scale gates.

Rendering is side-effect free; the module now persists Source bindings and rendered revision truth. PostgreSQL integration must use a real PostgreSQL 18.4 instance and two distinct direct-login credentials in `DPS_TEST_POSTGRES_ADMIN_URI` and `DPS_TEST_POSTGRES_RUNTIME_URI`; an in-memory replacement, one shared credential, missing environment, or `SET ROLE` cannot satisfy the required integration suite. Missing either variable is an explicit `INFRA_ERROR/NOT_RUN` failure.

Unit and contract suites cover the retained v1 collision pair, exact version matching, nonce `0..1023`, fixed derivation, preoccupied real-candidate retry, re-signed forged binding rejection, same-Soul concurrency, restart reads, capability scope, nonce exhaustion quarantine, v1 byte freeze, v2 canonical proof, and migration shape. They do not prove PostgreSQL or GBrain network write/read-back.

The real integration suite covers distinct identity enforcement before DDL, weak precreated table rejection without repair, exact-schema no-op rerun, administrator-runtime rejection, effective SELECT/INSERT-only ACL, owner-side UPDATE/DELETE/TRUNCATE triggers, restart/concurrency, and fault-injected relational/canonical/JSONB splits. Fault injection first initializes a valid runtime, then uses the migration owner to deliberately remove the relevant guard; this proves the read verifier independently fails closed rather than merely retesting a database CHECK.
