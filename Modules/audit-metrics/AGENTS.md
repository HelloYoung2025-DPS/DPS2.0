---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: audit-metrics
manifest: ./module.yaml
applies_to: .
---

# Audit Metrics Agent Rules

## Scope

This module owns immutable, scoped audit events and low-cardinality operational counters. It never approves, compiles, leases, executes, retries, or alters an outcome. It stores no secret, credential, raw email, raw phone, model prompt, OCR/UI payload, post body, or other raw personal content.

## Required reading before the first write

Read root `AGENTS.md`, this file, `module.yaml`, all provided/consumed contracts, dependency graph, compatibility matrix, `tests/README.md`, and `operations/README.md` in order. Bind exact hashes and rebind on scope or consumer changes.

## Communication and boundaries

Accept only relayed `command.receipt/v1` from Command Orchestrator scope and emit `audit.event/v1`. Never read another module's table, import internal types, share mutable static state, accept stdout as proof, or expose identifiers as metric labels.

## Invariants

- `SOUL-ISO-001`: audit queries require the exact opaque Soul/device/account scope.
- `CMD-IDEMP-001`: identical event ID/hash is a no-op; conflicting reuse is quarantined.
- `RESULT-VERIFY-001`: preserve receipt verification flags; never upgrade an outcome.
- `GBRAIN-READBACK-001`: no GBrain access or content.
- `EDGE-NORESTART-001`: no Windows/device evidence claim without raw evidence owned elsewhere.
- Events are append-only. Out-of-order arrival is sorted for reads, never used to overwrite history.
- Raw PII, secrets, high-cardinality identity labels, unknown contracts, roles, outcomes, and majors fail closed.
- The only append path accepts a command receipt plus a signed relay envelope verified by an injected verifier; direct public audit-event append is forbidden.

## Tests and rollout

Required checks release only on `PASS`. Cover duplicate/conflict, out-of-order arrival, raw PII/secret rejection, outcome preservation, role attack, and cross-scope isolation. The in-memory store is unit/contract evidence only; production needs append-only PostgreSQL integration.

## Rollout and rollback

Rollout requires the exact signed BOM, shadow count comparison, a bounded human-approved canary, and `kill_audit_metrics_ingest`. Rollback restores prior routing without deleting or rewriting immutable audit history; a metric or log line can never replace the source receipt evidence.
