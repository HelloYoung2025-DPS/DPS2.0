---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: edge-local-journal
manifest: ./module.yaml
applies_to: .
---

# Edge Local Journal Agent Rules

## Scope

This module owns the local append-only, checksum-chained recovery journal used by Edge components. It records durable command lifecycle facts but does not authorize, route, or execute actions.

## Required reading before the first write

Read the root AGENTS.md, this file, module.yaml, every provided and consumed contract, the dependency graph, compatibility matrix, tests/README.md, and operations/README.md in order. Bind hashes and rebind whenever scope changes.

## Invariants

- CMD-IDEMP-001: duplicate entry_id plus identical payload hash is a no-op; a conflicting hash is quarantined.
- Committed records are append-only and linked by sequence and checksums.
- Only an incomplete trailing crash fragment may be isolated during recovery; a corrupt committed record fails closed.
- A receipt is returned only after the record is flushed durably.
- A drain attestation is issued only from the exact durable `windows-edge-worker` `WORKER_DRAINED` entry while holding the same OS writer lease used by append. The Journal, never the caller, recomputes the complete head, exact command range, entry-set digest, file identity, quarantine state, recovery state, and owner receipt.
- Drain issuance requires the externally injected Journal RSA-PSS authority. It emits exactly one rich Journal signature; no Worker proof or second compatibility signature is accepted, reconstructed, or emitted by this module.
- Any append intent, writer race, path link/reparse point, device/inode or volume/file-index replacement, length/byte/head change, stale validity, payload/deployment mismatch, missing key, or quarantine fails closed without an attestation.
- Stored payloads must not contain production secrets, GBrain credentials, Persona, or unrestricted screen content.

## Communication and boundaries

The current product communication graph accepts `edge.journal.append/v1` from the declared Worker producer, returns `edge.journal.receipt/v1` to that Worker, and provides owner `edge.journal.drain.attestation/v1` only to Supervisor. The provider schemas retain explicit caller provenance, but no unimplemented direct Supervisor append/receipt route may be declared. Cross-module production code may reference only `contracts/provided/Dps.EdgeLocalJournal.Contracts/Dps.EdgeLocalJournal.Contracts.csproj` and its DTOs, the separate `IJournalAppendClient`, `IJournalReadiness`, `IJournalDrainAttestationProvider`, and `IJournalQuarantineAdministration` capabilities, `CanonicalJson`, `JournalChecksumEncoding`, and non-secret `JournalDrainAttestationCodec`; no public aggregate interface is allowed. It must never reference the Journal implementation project, copy the codec, payload canonicalization, checksum encoding, or owner DTOs, or receive signing authority. Worker receipt and Journal rich attestation are independent wires. Journal never imports, parses, reconstructs, or verifies the Worker wire; it re-derives the durable payload, scope, owner receipt, and deployment truth and binds only `worker_receipt_wire_sha256`, the lowercase SHA-256 of the exact persisted Worker receipt UTF-8 bytes. A second Supervisor-compatible Journal signature or compatibility port is forbidden. Supervisor independently verifies and hashes the exact raw Worker wire, verifies the Journal rich wire, correlates every shared scope/deployment/drain field, and rejects equal normalized Worker and Journal SPKI identities. Request, receipt, rich attestation, and caller provenance must never be conflated. The private key belongs only to the externally injected Journal authority; do not persist, export, log, or pass it to Worker or Supervisor. A same-process object that implements multiple narrow interfaces is simulation-only because a Worker can cast the runtime object; production F6 Worker wiring must use an independent authenticated IPC append/readiness proxy whose concrete type and dependency closure contain neither `JournalStore`, attestation capability, quarantine administration, nor signing authority. Do not claim `WINDOWS_VERIFIED` from same-process composition. Do not read other module stores, import internal types, decide retries, claim action success, or execute commands.

## Tests and evidence

Test duplicate and conflicting entries, checksum tampering, crash-tail recovery, monotonic sequence, restart replay, cancellation before commit, concurrent append serialization, restart attestation, exact single rich-statement bytes, independent opaque Worker-wire correlation, and second-process append/replacement/symlink/kill-window races. Mac integration evidence is not Windows evidence.

## Rollout and rollback

Journal format changes use additive schemas and N/N-1 readers. Rollback changes routing or binaries without deleting committed records; format downgrade that cannot preserve data fails closed.
