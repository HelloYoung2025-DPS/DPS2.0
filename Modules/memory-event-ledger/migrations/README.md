# memory-event-ledger migrations

Use expand, migrate, contract across separate releases. The current and previous compatible versions must both run after the expand step. Destructive contraction requires a later signed BOM, backup evidence, and an explicit rollback or forward-fix decision.

Migration 002 is additive. It creates v2 event, head, outbox, delivery, quarantine, correction-link, and privacy-tombstone tables; a capability-gated `SECURITY DEFINER` append/read API; exact JSON-column checks including signed-command event identity and authority role/time columns; per-event advisory serialization; per-Soul sequence/hash chaining; cross-Soul-safe privacy foreign keys; immutable mutation/TRUNCATE triggers; and explicit admin/runtime grants. Runtime roles receive no table DML. Event plus outbox commit in the same function/transaction. Migration 001 remains byte-frozen for v1 quarantine/read history.

Correction and deletion are append-only authority records. Migration 002 intentionally grants no runtime write route to those tables; a later independent privacy authority and signed BOM must add one. Physical deletion, UPDATE, and TRUNCATE are not privacy workflows.
