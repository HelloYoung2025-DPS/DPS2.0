# Changelog

## 0.1.0 - 2026-07-14

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms and enforced the idempotency format in both SQLite development truth and PostgreSQL production truth.
- Registered the versioned `worktree.plan/v1` and `worktree.lease/v1` receipt paths to `factory-control-plane-host`.
- Proposed declarative one-writer worktree plans and external transactional leases with path locks and monotonic fencing tokens.
- Added the PostgreSQL 18.4 production adapter, advisory-lock concurrency, durable monotonic fencing, process-reopen recovery, and an independent hash-locked psycopg runtime.
- Added the fixed-argv Git worktree materializer with parallel independent worktrees, dependency ordering, owned-path commits, conflict/stale-stop behavior, and exact merge-head retesting.
- Renamed the psycopg integration binding to `DPS_TEST_POSTGRES_URI` so it cannot collide with the .NET/Npgsql `DPS_TEST_POSTGRES` format.
- Added required Draft 2020-12 suites for both `worktree.plan/v1` and `worktree.lease/v1`, validating production planner and lease-store output plus fail-closed negative instances.
- Raised the Manifest candidate minimum to `INTEGRATION_VERIFIED` because the existing required temporary-Git and PostgreSQL 18.4 suites are real integration boundaries; this does not claim formal repository evidence.
