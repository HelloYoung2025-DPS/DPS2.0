# legacy-runtime-adapter tests

This directory separates unit, contract, integration, resilience, Windows, and device evidence. The exact required suites and commands are declared in ../module.yaml.

A required check releases only on PASS. FAIL, SKIP, PARTIAL, NOT_RUN, INFRA_ERROR, NOT_APPLICABLE, an empty test set, or missing raw evidence is a failure. Mock evidence must be labelled and cannot satisfy integration, Windows, ZennoDroid, ADB, device, canary, or scale gates.

The current legacy runtime has not passed Windows or device gates. Static checks do not upgrade that status.

`test_legacy_byte_baseline.py` builds disposable Git repositories with exactly 79 legacy C# paths and exactly 12 changed paths: the immutable original four approvals plus a separate eight-path containment set. Its 21 tests cover missing/same-identity providers, whole-record digest drift, arbitrary commit and manifest swaps, 79-to-5 shrink, stable deletion and mutation, uppercase `.CS` injection, component collisions, case-aliased Git roots, linked-directory hiding, verifier/policy/test/module-command weakening, approved-byte self-approval, Golden Trace identity validation, and the prohibition on extending the original four. It executes no legacy C#.

`test_sessionrunner_fail_closed_p0.py` proves the source-level F5 P0 containment shape: all five production entrypoints gate on an explicitly unavailable bridge; writable DSL, missing/UNKNOWN results, Vision success overrides, direct device recovery, external process launch, and unverified memory writes remain blocked. It is static evidence only and executes no legacy C# or device operation.

`test_legacy_wrapper_and_orchestrator_p0.py` proves that all seven production wrapper gates, including `Initializer_OwnCode.cs` and `Main_OwnCode.cs`, are compile-time ON and return `ERROR_BRIDGE_REQUIRED` before any dynamic root or source read, cannot be disabled by project state, isolate every optional-variable setter failure, clear action/success/legacy telemetry, write the error token first and last, and preserve their original line-ending profiles. It also proves only raw exact `SUCCESS` reaches the SmartOrchestrator postcondition path, missing or `unknown` page proof fails, and failure tokens return before the false-success counter. It contains 7 static tests and executes no C#.

The external provider anchor protects the exact hashes of all three test files, their commands in `module.yaml`, and minimum method counts of 21, 9, and 7. Those commands resolve to an independent trusted-runner-created CPython 3.12.13 `.venv`, not a PATH-selected `python3`; formal evidence must also retain the interpreter SHA-256, exact argv, environment, raw output, and exit status. Deleting tests, reducing them to zero, replacing discovery with fixed success, or weakening assertions invalidates the provider record before the candidate verifier can pass.
