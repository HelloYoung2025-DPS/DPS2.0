# Changelog

## Unreleased

- Removed the unimplemented direct Supervisor append/receipt communication edges. The current graph routes Worker append/receipt and exposes only the independently verified Journal drain attestation to Supervisor; this governance correction does not claim a production IPC host or Windows verification.
- Added owner `edge.journal.drain.attestation/v1` with fixed strict-UTF-8 byte-length framing, RSA-PSS-SHA256, SPKI-derived key IDs, provider Schema/auth profile, and a 25-case corpus.
- Added the independently buildable `Dps.EdgeLocalJournal.Contracts` assembly so production consumers bind versioned DTOs, separate least-authority interfaces, and the non-secret strict codec/verifier instead of the Journal implementation project or copied canonicalization logic. No public aggregate interface is exposed.
- Added externally injected Journal attestation authority and `IssueDrainAttestationAsync`; callers cannot state the durable head, range, owner receipt, quarantine/recovery state, or Journal signatures.
- Added one rich Journal owner signature binding journal file identity, bytes, head, exact entry set, state artifacts, Worker artifact/version/slot, Journal artifact, BOM, protected policy, route epoch, request, validity, and the opaque SHA-256 of the exact persisted Worker receipt wire. Worker and Journal proofs remain independently owned; the Journal does not import or reconstruct the Worker contract, and no second compatibility signature or port exists.
- Documented that narrow same-process interfaces are not security isolation. F6 production requires an independent authenticated Worker append/readiness IPC proxy with no `JournalStore`, attestation, quarantine, or signing-authority dependency; same-process composition remains simulation-only.
- Append and attestation now share the same OS writer lease. File-flushed append intents and an intent-admission lease close cross-process issuance races; the gate inode is checked before and after writer release, and stale intents from a process killed inside real `AppendAsync` fail closed pending reviewed recovery. Parent-directory fsync and power-loss persistence are not yet claimed.
- Attestation holds and re-hashes one journal descriptor and checks Unix device/inode or Windows volume/file-index, length, path binding, bytes, head, quarantine/recovery state, and exact drain range before and after signing. Symlinks, reparse points, identical-content replacement, concurrent append, corruption, state change, and expiry are rejected.
- Added restart and real second-process append/replacement/symlink/gate-rebind/kill-window tests without Windows silent-pass branches. Exact executable floors are now 6 Unit, 3 Contract, and 7 Integration methods; the Python suite covers 48 provider cases.

- Recovery now rejects strict-UTF-8 violations, blank records, unknown or duplicate journal fields, and missing envelope fields before checksum validation.
- Canonical payload JSON now rejects duplicate object properties at every nesting level.
- `command_id` and `entry_id` now use a shared strict ASCII token contract that rejects line breaks, whitespace, path separators, and ambiguous Unicode.
- Identity and entry checksums now use domain-separated, 32-bit big-endian length-prefixed strict-UTF-8 fields; each record carries the required checksum-encoding discriminator and the provider publishes a machine-readable profile.
- A conflicting live duplicate now flushes a persistent quarantine marker and stops every writer across restart; resumption requires an exact reviewed marker digest and preserves release evidence.
- Recovery and append now enforce hard journal, record, canonical-payload, and quarantine-marker byte limits before allocation or durable write.
- Missing or unknown checksum encodings fail closed; the only retained-data path is a separately reviewed offline export/replay into a new journal, never an in-place reinterpretation.

## 0.1.0 - Proposed

- Added append-only checksum chain, durable receipts, duplicate quarantine, replay, and crash-tail recovery.
- Split caller-produced `edge.journal.append/v1` from Journal-produced `edge.journal.receipt/v1` and declared both reciprocal communication directions.
- Bound producer, command, idempotency, Soul/device/account scope, trace metadata, and payload to the entry identity and checksum chain.
- Aligned runtime and Schemas on canonical zero-offset UTC, strict three-to-64-character entry types, bounded command/payload fields, exact lowercase SHA-256 values, and signed-64-bit sequence limits; provider corpora preserve raw Int64 boundaries.
- No Windows or device verification is claimed.
