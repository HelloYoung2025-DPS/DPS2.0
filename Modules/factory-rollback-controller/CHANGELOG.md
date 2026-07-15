# Changelog

## 0.2.0 - 2026-07-14

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms in runtime validation, Schema, and recovery fixtures.
- Registered the versioned `rollback.plan/v1` and `rollback.result/v1` receipt paths to `factory-control-plane-host`.
- Added the pure-Python declarative rollback controller with process-bound BOM
  verification and human R3 authorization ports.
- Added exact rollbackable and compensation-only five-step workflows.
- Added append-before-effect step markers, crash-window recovery, request-hash
  idempotency, redacted conflict quarantine, logical deadline enforcement, and
  durable terminal-result evidence.
- Hardened the unreleased proposed v1 schema scaffold before activation so
  plans/results require request, plan, authorization, and stable-BOM proof
  references; no active consumer compatibility promise existed.
- Added repository-static unit and contract tests.
- Hardened the required Unit suite to load the exact module-owned production
  source under isolated Python without relying on ambient `PYTHONPATH`.

The module remains proposed and is not production or canary verified.
