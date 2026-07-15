# evidence-service tests

This directory separates unit, contract, integration, resilience, Windows, and device evidence. The exact required suites and commands are declared in ../module.yaml.

A required check releases only on PASS. FAIL, SKIP, PARTIAL, NOT_RUN, INFRA_ERROR, NOT_APPLICABLE, an empty test set, or missing raw evidence is a failure. Mock evidence must be labelled and cannot satisfy integration, Windows, ZennoDroid, ADB, device, canary, or scale gates.

The first implementation slice is side-effect free. PostgreSQL integration must use a real PostgreSQL 18.4 instance; an in-memory replacement cannot satisfy the required integration suite.
