# Tests

Run `python3.12 -m unittest Modules/factory-trusted-runner/tests/test_trusted_runner.py` for deterministic runner behavior. Run `python3.12 -m unittest Modules/factory-trusted-runner/tests/test_contracts.py` for the required Draft 2020-12 `trusted.test.result/v1` suite, including real `TrustedRunner` output and fail-closed negative instances. Tests use synthetic child processes and cannot produce integration, Windows, device, canary, or scale evidence.
