# Changelog

## 0.1.1 - Proposed

- Reclassified the withdrawn Executor-owned `native.stop.proof/v1` consumer as `quarantine-only` and removed its malformed runtime communication edge.
- Replaced the legacy issuer with an internal zero-authority tombstone that cannot read runtime identity, stop native work, sign, verify, or persist a proof.
- Reduced the bounded legacy store to internal owner-codec inspection that returns metadata only; it has no proof creation or raw-wire API.
- Added unit, contract, and simulation coverage for repeated, concurrent, cancelled, conflicting, restart, path, permission, and malformed-artifact cases with zero native-stop calls and zero proof emission.
- Recorded the frozen Supervisor route/live-IPC and Policy v2/challenge Worker APIs as `WAITING_EXTERNAL`; no local contract fork is introduced.
- Added negative launch-compatibility evidence: the real Worker entry point rejects the Supervisor's current zero-argument launch before state creation or intake, so a shared fixed Release-BOM-protected launch ABI remains a hard prerequisite for stage or cutover.
- Windows, ZennoDroid, and ADB verification remain `NOT_RUN`; release eligibility remains false.

## 0.1.0 - Proposed

- Added lease-bound idempotent command processing and native-result/postcondition truth semantics.
- Added deterministic duplicate, crash-window, timeout, offline, and unknown-outcome simulations.
- Replaced newline-delimited command hashing with a domain-separated, length-prefixed canonical encoding that distinguishes field boundaries, nulls, and empty strings.
- Added regression coverage proving newline partition collisions are quarantined as conflicting duplicates without a second dispatch.
- Bound the worker implementation to the supervisor-owned request-hash specification and golden vector, and fail closed on invalid Unicode instead of replacement encoding.
- Added strict full-envelope JSON decoding with required-field, duplicate/unknown-property, size, identity, action/step, result-nullability, and request-hash validation.
- Replaced the ambiguous pending state with fenced `Reserved`, `Accepted`, `TransportAttempted`, `DispatchAcknowledged`, and `Completed` phases; restart evidence now reports dispatch acknowledgement as false, unknown, or true without inventing certainty.
- Added restart simulations for the reservation, accepted, transport-attempted, and dispatch-acknowledged crash windows, plus bounded pre-dispatch retry enforcement.
- Added production `RECEIPT` and `HEALTH` encoding plus strict `DRAIN` decoding against supervisor-owned golden wires; duplicate and retry truth are now explicit protocol fields.
- Persisted the dispatch-attempt count with each command state so a restart cannot reset the two-attempt budget; attempted or acknowledged same-epoch duplicates now return non-retryable reconciliation truth instead of retryable `IN_PROGRESS`.
- Restored the exact audited 25-line .NET test wrapper; higher Contract and Integration count floors are injected only by the central candidate policy, and manual reflection counts are not formal evidence.
- Added a required consumer-differential gate over the exact supervisor and Journal provider corpora; runtime decoding now rejects nonzero-offset command and lease timestamps.
- Synchronized the consumer request hasher with the supervisor-owned 19-field specification so `occurred_at` and `privacy_class` are inside the idempotency boundary, and standalone hashing rejects noncanonical time offsets.
- Added recoverable two-phase terminal completion: receipt and deterministic terminal audit are prepared together, the Journal append is idempotently replayable, and `Completed` requires a matching durable append receipt.
- Made drain truth include persisted unfinished, uncertain, and terminal-audit-pending state in addition to in-process work; drain cannot be reported before intake stops.
- Added explicit durable production adapter interfaces and a production-mode composition guard. No durable adapter or production host exists yet, so direct startup remains disabled and the module remains ineligible for release.
- Added simulations for terminal Journal failure, crash after durable append, duplicate reconciliation, persistent drain blockers, and production rejection of in-memory adapters.
- Replaced the partial Journal test boundary with the complete owner request and receipt DTOs, exact canonical-payload hashing, full receipt-scope verification, ASCII token fencing, and a real `edge-local-journal` cross-module golden/negative simulation.
- Added an executable reconcile-only production host, private runtime-directory and file enforcement, an exclusive writer fence, checksum-chained durable command state, crash-tail isolation, and a public-contract-only adapter over the real `edge-local-journal` store.
- Added real local-process crash/restart tests proving exact crash-after-Journal reconciliation, persistent uncertain-outcome blocking, duplicate/conflict truth after restart, writer-lock fencing, tamper rejection, path-replacement detection, resource ceilings, and private artifact permissions.
- Bound the Supervisor-owned dual-attested `edge.worker.drain.receipt/v1` Schema, corpus, and auth profile. Full production issuance remains `WAITING_EXTERNAL` for the separately owned Journal attestation API; the Worker never holds or simulates the Journal private key.
- Replaced pre-seeded crash fixtures with real child-process `CommandProcessor` execution at transport, native-side-effect, durable-acknowledgement, and post-Journal/pre-finalize windows; restart remains reconcile-only and never increments the simulated native side effect.
- Bound open state and writer-lock handles to the current Unix device/inode identity, rejected hard-link aliases, capped state crash tails at 128, bounded existing-tail reads, and made Journal quarantine a startup failure instead of a false drained report.
- Bound Unix runtime directories and files to the process effective user and reject FIFOs or other non-regular paths before opening them; added a bounded child-process regression so a malicious FIFO cannot hang startup verification.
- Replaced the earlier free-text exchange `DRAIN` draft with the Supervisor-owned signed `edge.worker.drain.directive/v1`; unknown exchange kinds now fail closed and no unstructured detail may stop intake.
- Replaced the earlier dual-attested receipt draft with two independent public wires: this module emits only the Worker RSA-PSS receipt, while Supervisor directly requests and correlates Journal rich attestation by exact Worker wire digest.
- Added a private checksum-chained PREPARED/COMMITTED drain-receipt store, exact raw-wire restart reuse, writer fencing, crash-tail isolation, immutable Journal locator, changed-input quarantine, and bounded durable-append recovery.
- Added `WorkerDrainReceiptIssuer`: it verifies the exact signed directive and active expectation before stopping intake, derives drain truth from `CommandProcessor`, rejects clock rollback/non-UTC time, signs after completion, appends `WORKER_DRAINED` through append/readiness-only IPC, and returns only after an exact durable Journal receipt.
- Removed the Worker-to-Journal file and attestation coupling. Production source references only the Journal contract pack, and a client exposing rich attestation or quarantine administration is rejected.
- Documented `native.stop.proof/v1` as a release blocker until the Executor owner exports its DTO/canonical codec and a real BOM-authorized P-256 no-later-write Worker implementation exists; no manifest-only proof is claimed.
- Superseded the contract-pack portion of that blocker after `executor-gateway` exported its owner DTO, evidence digest, canonical signing bytes, and strict JSON codec; the Worker now references that pack directly and declares the proposed outbound edge without owning a local fork.
- Added `WorkerNativeStopProofIssuer` and a private durable proof store: exact active BOM/token/key and Worker-incarnation rechecks, constructor-fixed enumerated no-later-write authority, P-256 P1363 sign/verify, owner-canonical exact-wire persistence, cross-issuer serialization, restart idempotency, changed-input quarantine, writer fencing, private paths, and bounded artifacts.
- Added owner-Schema negative vectors and simulation tests for signature/evidence correctness, clock failure, stop failure, runtime identity changes, cancellation after confirmed stop, concurrent issuers, restart, and quarantine. Production Windows stop control, active-BOM attestation, external key service, IPC, and process-rooted composition remain blockers, so release eligibility and evidence level are unchanged.
- No Windows or device verification is claimed.
