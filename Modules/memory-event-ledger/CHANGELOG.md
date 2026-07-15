# memory-event-ledger changelog

## Unreleased

- Added bounded untrusted `memory.append.request/v2`, plus `memory.event/v2` and `memory.outbox/v2` with canonical serialization, exact authority audit bindings, signed observation evidence, signal-set digests, Soul sequence, and replay chain fields.
- Permanently disabled the forgeable v1 append path and declared v1 contracts quarantine-only without changing their bytes.
- Added reference-identity Soul and observation capability seals, fixed non-public authority sources, current/revocation/equality-expiry checks, exact P-256 signed-receipt parsing, and a production `WAITING_EXTERNAL` fail-closed boundary rather than caller-selected roots.
- Added additive migration 002 with atomic event/outbox append, same-hash no-op, different-hash quarantine, per-event concurrency lock, per-Soul chain, strict runtime ACL, capability-gated functions, immutable/TRUNCATE guards, column/JSON checks, and reserved append-only privacy correction/tombstone stores.
- Added v2 adversarial unit, contract, and real PostgreSQL 18.4 integration suites. No integration, Windows, device, canary, scale, or production verification is claimed in this change.
- Bound v2 `event_id` to the signed `command_id`, persisted every authority role/time field beside its JSON source, and added composite Soul/event privacy foreign keys so receipt replay and cross-Soul correction/deletion references fail closed.
- Added a capability-gated Soul replay API that revalidates canonical payload bytes, exact scope, ordered sequence, payload hash, and the complete per-Soul chain before returning records.

- Tightened event and Outbox envelopes to exact opaque identifiers, lowercase SHA-256 values, canonical UTC, and matching PostgreSQL constraints, including newline/case/length adversarial coverage.
- Registered the module governance boundary. This entry does not claim runtime implementation or production verification.
- Added the F2 append-only MemoryEvent ledger with transactional Outbox, deterministic duplicate handling, conflict quarantine, replay ordering, and injected crash-window recovery.
- Added database-enforced canonical JSON hashing, event/Outbox identity-scope matching, immutable records, and real PostgreSQL 18.4 adversarial integration tests.
- The module remains proposed and is not release eligible; these changes do not claim Windows, device, canary, scale, or production verification.
