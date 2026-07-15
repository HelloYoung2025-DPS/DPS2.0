# Changelog

## 0.5.0 - Proposed

- Added the real Windows-only Supervisor host composition for the fixed Zenno `127.0.0.1:28741` HTTP exchange: Negotiate plus exact SID authentication, strict request size/timeout/contract decoding, LocalMachine certificate thumbprint and SPKI pinning, and signed `WAIT` handshake/health responses. Native results remain 503 until live Worker IPC exists; no success or ACK is fabricated.
- Added direct suspended Windows Worker creation with an exact application path, zero caller arguments, a minimal environment, pre-resume `KILL_ON_JOB_CLOSE` Job Object assignment, PID/start/executable validation, and fail-stop cleanup. The A/B coordinator invokes the existing staging, sticky route, signed drain, cutover, and rollback state machine; its production runtime channel intentionally remains unavailable.
- Added component-by-component path proofs that reject symlinks/reparse points and revalidate native open-handle identities at artifact validation, startup, route acquisition, cutover, and rollback. Directory identity ignores legitimate child-write timestamps while file content metadata remains bound.
- Added a single-writer append-only evidence JSONL with synchronous flush, sequence/hash chaining, replacement checks, and independent file identity. `edge.capability.evidence/v1` now binds the log entry count, head, and file identity; a real claim requires at least 100 entries.
- Expiry equality now fails for capability evidence, drain directives, and Worker drain receipts. Added exact Zenno decoder, host configuration, evidence tamper, parent replacement, unavailable-runtime, and expiry-boundary tests.
- Bound artifacts to a complete signed runtime manifest and immutable version-directory security digest, and added launch-time read handles that deny write/delete for the entire process lifetime. Same-size content rewrites, missing/extra DLLs or configuration, case collisions, links, hardlinks, and directory ACL mutation fail closed.
- Replaced raw process launch with a one-use Supervisor-issued candidate authorization and separated artifact, capability-attestation, bridge-server, Supervisor-drain, Worker-drain, and Journal key roles. External capability expectations now bind Zenno PID/start time, bridge server key, evidence-log count/head/file identity, continuity, switch count, and soak duration.
- The actual Worker launch/runtime ABI is not frozen, so capability assessment deliberately adds `worker-launch-runtime-abi-unavailable`, process launch stops before `CreateProcess`, and no production candidate or route transition can occur. Worker health/shadow and drain calls now have a protected 30-second deadline, but crash recovery, drain `NOT_SEEN`, soak finalize, atomic process-liveness fencing, and evidence-log segmentation remain explicit release blockers.
- Expanded the Unit suite with signed-runtime-closure rewrite/extra-file rejection; module-local floors are now 12 Unit, 13 Contract, and 2 Integration.
- Removed the stale statement that this module produces `edge.journal.append/v1`; no such manifest or communication edge exists. The module remains proposed and release-ineligible. Windows, ZennoDroid, ADB, device, canary, and scale evidence is `NOT_RUN`.

## 0.4.0 - Proposed

- Removed free-text `edge.worker.exchange/v1` DRAIN and introduced the Supervisor-owned, RSA-PSS-signed `edge.worker.drain.directive/v1` contract with strict expectation and golden vectors.
- Added an active-drain-only signing-broker path that verifies the returned signature against the deployment-pinned public key and durably records one exact PREPARED raw wire before it can be sent. Concurrent and restart retries reuse those bytes and never re-sign the same drain.
- Added a signed durable-continuation verifier for already PREPARED/COMMITTED Worker state. It preserves canonical wire, exact expectation, key-ID and signature verification while intentionally skipping only wall-clock freshness; it cannot authorize new drain intake.
- Split completion truth into a Worker-only raw receipt and a separately fetched rich Journal owner attestation. A stale but exact Worker receipt is usable only as durable continuation and only with a newly issued, fresh Journal proof. Cutover and rollback correlate the deterministic Journal request ID, exact Worker wire digest, Journal payload and owner receipt, then persist both proof-wire digests with the route transition.
- Required pairwise-disjoint Supervisor, Worker and Journal trust key sets and bounded the direct Journal provider call to five seconds; added missing-proof, wrong request/scope/BOM/key, overlapping-key, hung-provider, outer-signature, concurrent preparation, restart reuse and half-proof-pair negative tests.
- Replaced the unrecoverable file-then-anchor crash gap with a linearizable external-anchor prepare, local replace, and anchor commit protocol. Every prepared head carries an authority-generated unpredictable one-use token, so stale commit/abort ABA calls fail. Resume deterministically commits when the local file equals the exact tokenized prepared head, aborts when it still equals the committed head, and rejects every other combination. Tests cover same-store Resume/write concurrency, ABA re-prepare, prepare/replace windows, commit/abort rejection, applied-then-throw ambiguity, and valid third-state rejection without a permanent wedge.
- An expired directive that the Worker never durably observed still requires a future signed, fenced `NOT_SEEN` reconciliation protocol before the Supervisor can safely abandon it and issue a new drain ID; release eligibility remains false until that cross-module race is closed.
- The module remains proposed and release-ineligible. No Windows, ZennoDroid, ADB, device, canary, or scale verification is claimed.

## 0.3.0 - Proposed

- Canonicalized the Worker drain Journal payload using the owner-compatible ordinal property order, tightened drain timestamps and versions to the rich Journal contract, and raised local suite floors to the complete test counts.
- Added a held cross-process state writer lease, null-element rejection, and rollback-slot protection; an external Windows anti-replay/configuration trust anchor is still required before release eligibility.
- Replaced caller-constructed drain truth with strict raw `edge.worker.drain.receipt/v1` decoding and separate deployment-pinned Worker and Journal RSA-PSS attestations. The receipt now binds the exact scope, active drain, worker and journal artifacts, Release BOM, protected policy, routing epoch, owner journal receipt, payload and entry checksums, durability, and freshness.
- Added checksum-chained atomic Supervisor state with explicit `Bootstrap` and mandatory `Resume`. Missing, corrupt, deployment-mismatched, concurrently replaced, or in-flight restart state fails closed; routing, drain, stage, cutover, rollback, and lease counts are persisted before successful return.
- Bound capability attestations to host ID, Release BOM, protected policy, worker artifact/version/slot, issued time, not-before time, expiry, maximum age, and clock skew. Windows-gate configuration bytes and the trust-store fingerprint must match externally protected process values.
- Removed `InternalsVisibleTo`; candidate staging accepts only the sealed verification result returned after real signature verification, and rechecks its trust-store fingerprint and exact candidate/deployment fields.
- Replaced runtime timestamp regex end anchors with absolute `\z` anchors and made null required arrays fail with structured contract errors.
- Added malicious signature, Journal checksum, BOM/policy, stale configuration, state tampering, missing state, and restart-with-inflight negative tests. No Windows, ZennoDroid, ADB, device, canary, or scale verification is claimed.

## 0.2.0 - Proposed

- Fixed supervisor-to-Zenno authentication interoperability: the shared contract now fixes `sha256_<SPKI DER SHA-256>` key IDs and `RSA_PKCS1_SHA256` directive proofs, while worker artifacts and Windows capability evidence remain RSA-PSS SHA-256.
- Added a provider-owned directive authentication specification plus a public-SPKI signed golden wire that both modern .NET and the C# 5 Zenno consumer can verify without a private fixture key.
- Replaced serialized `attestation_verified` claims with real capability signature fields, a strict DTO/codec, a versioned attestation statement, and verification against deployment-pinned trust roots.
- Made the Windows entrypoint require a declarative configuration containing an absolute trust root, exact allowed key IDs, and a signed capability-evidence path; missing configuration fails closed as `WAITING_EXTERNAL`.
- Candidate staging now requires a passing `CapabilityAssessment` plus signed artifact, health, and shadow evidence. Cutover and rollback now require an exact, durable worker-drain receipt bound to slot, version, artifact digest, routing epoch, worker receipt, and journal receipt.
- Tightened route identity to exactly `db_` plus 32 lowercase hexadecimal characters.
- Extended command request hashing from 17 to 19 fields by binding `occurred_at` and `privacy_class`; reissued the owner spec and command/receipt golden hash as `d7a8f4901c7d56f833b2ff24ea169bff565984a6417f165a4f25a5ff233d8d1e`.
- Corrected module communication scopes for worker and journal peers and retained distinct, explicit RSA padding permissions.
- No Windows, ZennoDroid, ADB, device, canary, or scale verification is claimed.

## 0.1.0 - Proposed

- Added deterministic A/B slot validation, device-binding routing, drain, rollback, capability evidence, and simulation gates.
- Added immutable fingerprint-pinned RSA trust stores for worker artifacts and Windows capability evidence; per-call arbitrary public keys are not accepted.
- Rejects trust-root symlink escapes and signatures not made by a deployment-pinned Release BOM key.
- Added the machine-readable `edge.worker.exchange/v1` request-hash canonicalization owned by this module, including framing, scalar rules, field order, and a self-verifying golden vector.
- Defined fail-closed `COMMAND`, `RECEIPT`, `HEALTH`, and `DRAIN` producer/nullability semantics; only a command computes the canonical request hash, while a receipt carries that original command hash.
- Added the production command DTO/encoder and a full wire golden fixture shared with the worker consumer tests.
- Added production `DRAIN` encoding plus strict `RECEIPT` and `HEALTH` decoding, with four owner-controlled wire fixtures.
- Added versioned `duplicate` and `retry_allowed` receipt fields and a Draft 2020-12 cross-field truth table that rejects false success, ambiguous failure, and unsafe in-progress claims.
- Restored the exact audited 25-line .NET test wrapper and registered Draft 2020-12 validation as a separate required `windows-edge-supervisor.contract-schema` suite; Python diagnostics remain distinct from the currently unavailable formal .NET gate.
- Hardened all owned wire Schemas to canonical zero-offset UTC, .NET-compatible year/second ranges, absolute-end security strings, exact digest/key lengths, canonical Base64, and CLR integer bounds; owner corpora are consumed directly by Edge and Zenno tests.
- Reissued the request-hash and four wire golden fixtures with strict opaque identifiers and zero-offset wire timestamps; the canonical request vector remains byte-for-byte self-verifying.
- No Windows, ZennoDroid, ADB, or device verification is claimed.
