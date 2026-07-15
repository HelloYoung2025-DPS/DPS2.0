# Command Orchestrator changelog

## Unreleased

- Make PostgreSQL the sole clock authority for lease reservation, binding, dispatch validity and recovery, with the timestamp sampled after the command lock.
- Replace broad privilege checks with exact role, object, function, sequence and default-ACL inventories; reject third-party grants and unknown schema objects.
- Bind catalog attestation to all identity-sequence parameters and ownership metadata, and project command state by explicit transition order so descending sequence drift cannot regress truth.
- Require every counted Integration test to create and destroy an unpredictable dedicated PostgreSQL 18.4 database with restricted roles, a nonce marker, exact owner proof and a live session guard.
- Add a versioned, key-bound Policy Approval signer port while preserving Command Orchestrator as the `execution.authorization/v1` owner and producer; reject signer metadata, envelope or P-256 signature drift locally.

- Add a PostgreSQL 18.4 durable orchestrator with immutable command/lease/attempt/receipt/outbox/quarantine history, advisory-lock concurrency, pre/post-dispatch crash recovery, and exact signed-receipt replay semantics.
- Add migration SHA-256 and live catalog attestation, separate migrator/runtime login identities, `SECURITY DEFINER` APIs, and a runtime role with no direct table, column, sequence, DDL, ownership, or inheritance access.
- Verify every execution authorization against an independent NIST P-256 Policy Approval trust root before dispatch and reject forged signatures without changing durable state.
- Bind every runtime `api_*` invocation to a separate 256-bit process capability whose SHA-256 is attested in the immutable migration ledger, so the runtime database credential alone cannot forge dispatch or receipt transitions.
- Make the shape-only in-memory state model internal and test-only, eliminating it as a product entry point that could bypass the durable path's Policy Approval signature and database capability boundaries.
- Add the required 21-test `command-orchestrator.postgresql18` real-infrastructure suite; missing `DPS_TEST_POSTGRES`, non-18.4 servers, and any failure to provision a disposable proof-bound database fail closed and cannot fall back to mocks.
- Make the required Python contract suite use the repository-pinned `python3.12` command surface instead of depending on a developer-specific `.venv` directory.
- Replace the raw receipt state-transition entry point with trusted P-256 `command.receipt.signed/v1` verification and bind every attempt to the exact command, issued authorization, active BOM generation/token digest, and native/postcondition evidence.
- Require the signed receipt envelope to repeat and cryptographically bind all public identity, trace, idempotency, time, and privacy fields; forged raw success can no longer advance command state.
- Replace delimiter-joined operation, receipt, idempotency-scope, command-ID, and lease-ID material with distinct domain-separated, length-prefixed canonical encodings.
- Bind operation and receipt digests to every contract field, explicit nullable presence, ordered steps, and canonicalized argument maps; quarantine conflicting duplicate payloads that previously collided.
- Snapshot compiled step arguments before storing a command and require a first receipt to match the command trace and idempotency identity.
- Rebind the consumer to the latest `operation.compiled/v1`, retain its validated deep-frozen snapshot, propagate mandatory `approval_sha256` into dispatch, and bind it into command and authorization digests.
- Bind execution authorization to active-BOM generation plus execution-token SHA-256 and add a Schema-bound machine canonical specification with cross-language bytes, digests, public key, and P1363 signature.
- Raise the Unit/Contract test floors to the current 19/9 baseline and bind required security IDs through a machine-readable inventory.
- Require fixed lowercase digest identifiers for device, platform account, trace, and idempotency scope; JSON Schema uses an ECMAScript-safe absolute end assertion and runtime tests reject trailing newlines.
- Make the signed-receipt Schema assert the exact canonical Base64 shape of a 64-byte P-256 P1363 signature, with a real Draft 2020-12 negative suite instead of relying on the annotation-only `contentEncoding` keyword.
- Make all command, authorization, receipt, and signed-receipt schema versions, SHA-256 values, P1363 signatures, and zero-offset timestamps reject terminal whitespace and extra bytes; mirror the version boundary in C# with an absolute `\\z` anchor.

## 0.1.0 - Proposed

- Add versioned dispatch and receipt protocol contracts.
- Add scoped duplicate/conflict handling, leases, pre-dispatch recovery, post-dispatch reconciliation, and safe bounded retry state.
- Add the proposed `execution.authorization/v1` owner contract with fixed canonical domain/encoding and explicit ECDSA P-256/SHA-256 IEEE P1363 Base64 signature semantics.
- Align the unreleased `command.dispatch/v1` JSON Schema with the existing exactly-one-step C# invariant; multi-step dispatch remains unsupported in v1.
- Preserve dispatch step ordinal in authorization command digests instead of sorting by `step_id`.
- Make all command canonical writers use strict UTF-8 so isolated surrogate input fails closed instead of collapsing to the same replacement bytes.
