# Tests

Run the unit suite with `python3.12 -m unittest Modules/factory-worktree-manager/tests/test_worktree_manager.py`. It uses a temporary repository and a dev-only external SQLite substitute.

Run `python3.12 -m unittest Modules/factory-worktree-manager/tests/test_contracts.py` for the required Draft 2020-12 validation of real planner and lease-store output plus fail-closed negative instances for both `worktree.plan/v1` and `worktree.lease/v1`. Contract validation does not replace either integration suite.

The required production-adapter suite is `PYTHONPATH=Modules/factory-worktree-manager/src python3.12 -m unittest Modules/factory-worktree-manager/tests/test_postgres_lease_integration.py`. It requires `DPS_TEST_POSTGRES_URI`, PostgreSQL 18.4, and the hash-locked psycopg 3.3.4 runtime. Missing infrastructure raises `INFRA_ERROR` and exits non-zero; no test is skipped. It covers transactional conflict, expiry takeover, monotonic fencing, stale-writer rejection, revocation, tamper rejection, and recovery in a new Python process.

The real temporary-Git suite is `PYTHONPATH=Modules/factory-worktree-manager/src python3.12 -m unittest Modules/factory-worktree-manager/tests/test_git_worktree_materializer.py`. It proves two independent worktrees modify, commit, and test concurrently; a dependent worktree waits; the exact merge head is retested; forged policy/fencing, overlapping paths, symlinks, shell argv, and stale baselines stop before release.
