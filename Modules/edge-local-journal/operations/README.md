# Operations

Run `bash Modules/edge-local-journal/operations/test.sh Unit`, `Contract`, or `Integration`. The argument is mandatory and exact; unknown or additional arguments are rejected. The repository-approved fixed wrapper performs locked restore and then the pinned `dotnet test` MTP path with exact minimum counts (6 Unit, 3 Contract, 7 Integration) and skip-failure policy. In a restricted sandbox, a NuGet audit or test-host IPC failure remains `INFRA_ERROR`; direct DLL execution is useful local diagnostic evidence but is not substituted for the formal wrapper. Integration uses local temporary files and real second processes; it is not Windows evidence until the same required suite actually runs on Windows.

The journal implementation is a single-writer component hosted by the trusted Edge side; it is not yet a production IPC host. A process-wide path lock and operating-system exclusive writer lease protect multi-instance use; every append first file-flushes a 0600 append-intent artifact, then reloads the committed head under that same writer lease. A separate intent-admission lease makes the final issuance check atomic with writer-lease release, and its path identity is rechecked immediately before and after that release. The process-kill tests prove a stale intent remains on the tested local filesystem; parent-directory fsync and power-loss persistence remain external verification and are not claimed. The strict UTF-8 JSON Lines file contains one durable checksum-chained record per line. Each record binds the append producer, command, idempotency key, complete Soul/device/account scope, entry metadata, payload checksum, sequence, previous checksum, checksum encoding, and entry checksum. Identity and entry checksums use the domain-separated, 32-bit big-endian length-prefixed layout in `contracts/provided/edge.journal.checksum.v1.json`; newline concatenation is forbidden. An `edge.journal.receipt/v1` response produced by the Journal is returned only after `Flush(true)`.

## Durable drain attestation authority

Open the store with `JournalStore.OpenWithAttestationAuthorityAsync` and an externally supplied RSA private key of at least 2048 bits. The key remains owned by the caller/secret provider and is never written by this module. `IssueDrainAttestationAsync(JournalDrainAttestationRequest)` accepts no caller-provided head, range, owner receipt, quarantine/recovery state, or Journal signature. Under the same OS writer lease as append, it reloads from disk, holds one journal file descriptor, recomputes and rechecks device/inode (Unix) or volume/file-index (Windows), length, complete bytes, checksum head, exact command range, entry-set digest, state artifacts, and the canonical owner receipt.

The result is owner `edge.journal.drain.attestation/v1`. Cross-module production consumers compile only against `contracts/provided/Dps.EdgeLocalJournal.Contracts/Dps.EdgeLocalJournal.Contracts.csproj`; composition injects separate least-authority interfaces rather than a public aggregate. Consumers use the pack's `CanonicalJson`, `JournalChecksumEncoding`, and non-secret `JournalDrainAttestationCodec` instead of copying payload hashing, canonicalization, or verification logic. They do not construct `JournalStore` or receive the signing authority. `JournalDrainAttestationCodec.Verify` verifies exactly one rich Journal signature and current Journal validity. Journal never parses or verifies `edge.worker.drain.receipt/v1`; it binds only `worker_receipt_wire_sha256` from the durable `WORKER_DRAINED` payload. Supervisor separately verifies the exact persisted Worker receipt UTF-8 bytes with its owner codec, hashes those exact bytes, verifies the independent Journal wire, correlates all shared scope/deployment/drain fields, and rejects equal normalized Worker and Journal SPKI identities.

Narrow interfaces alone are not a process security boundary: a Worker that receives a same-process `JournalStore` can cast it to attestation or quarantine authority. Direct store injection and the current local-process tests are simulation-only. Before F6 production or `WINDOWS_VERIFIED`, Worker must receive an independent authenticated IPC implementation of only append/readiness whose concrete type and dependency closure include no `JournalStore`, attestation provider, quarantine administrator, or signing authority. Supervisor alone requests rich drain attestations.

Issuance fails closed for a missing key, invalid worker statement/payload/deployment binding, quarantine, stale or active append intent, concurrent append, corruption, path replacement, symlink/reparse point, file identity/length/byte/head/state change, or expiry. A stale append intent after a killed process is deliberately not removed automatically:

1. Engage `kill_edge_command_intake` and prove no writer process or writer lease remains.
2. Preserve and hash the intent, journal, lock, and process-crash evidence outside this module.
3. Reopen and fully verify the journal under operator review.
4. Remove only the reviewed stale intent, then issue a new request ID. Never reuse an earlier signature or claim continuity across the kill window.

Recovery may isolate only a non-newline-terminated crash fragment. Recovery rejects files above 64 MiB, records above 4 MiB, canonical payloads above 1 MiB, unknown checksum encodings, checksum mismatches, sequence gaps, and malformed committed lines before unbounded allocation. Rollback never deletes committed records.

## Conflicting-duplicate quarantine and release

The first live `entry_id` conflict writes and disk-flushes `<journal>.quarantine.json` before returning `JournalConflictException`. Every store instance checks that marker before append; restart therefore remains fail-closed. The marker contains only hashes, the strict entry ID, UTC detection time, and the bound journal head. It does not contain the conflicting payload.

Recovery is an explicit operator action, never an automatic retry:

1. Keep `kill_edge_command_intake` engaged and preserve the journal plus quarantine marker.
2. Correct or disable the producer that emitted the conflicting identity.
3. Read `GetQuarantineStatusAsync`, review the exact marker SHA-256 and bound journal head, and record the human release decision outside this module.
4. Call `RecoverFromQuarantineAsync(expectedMarkerSha256)`. A digest mismatch, malformed marker, corrupt journal, or changed head fails closed.
5. The release atomically renames the marker to `<journal>.released-quarantine.<sha256>.json`; retain that evidence. Only then may intake resume.

## Checksum-format compatibility

Records missing `checksum_encoding=dps.length-prefixed-utf8/v1` and unknown encodings are deliberately rejected. Do not edit or reinterpret them in place. Keep routing on the old compatible binary, or use the separately reviewed offline export/replay migration described under `migrations/`; verify record count, order, identity, payload hashes, and both heads before switching routing.

This module remains `proposed` and `releaseEligible=false`. macOS local-process evidence can support only the repository's current static claim; Windows A/B, ZennoDroid continuity, device, canary, and scale gates remain external.
