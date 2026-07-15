# Changelog

## 0.4.0 - 2026-07-15

- Replaced bare-JSON receipt currentness input with Resolver-private, process-bound `VerifiedInstructionReceiptV2`; it binds canonical bytes, full digest, receipt ID, issuer, audience, issue/expiry time, nonce, generation, producer/major, and status.
- Made `resolve` issue the capability and made `validate` reject plain Mapping, direct construction, cross-authority capability, raw-byte substitution, same-ID/different-byte replay, and every post-issuance capability-field mutation.
- Added deterministic real-calendar parsing and strict time relationships for every receipt timestamp, independent of optional JSON Schema format packages; impossible February dates and expiry equality fail closed.
- Added hard 4096-entry quotas plus earliest-expiry pruning to Intake nonce, Intake capability, and receipt capability state; expired records are removed and cannot be replayed.
- Preserved public `instruction.receipt/v2` as the canonical JSON projection and kept portable cross-process receipt trust `WAITING_EXTERNAL`, proposed, and release-ineligible.

## 0.3.0 - 2026-07-15

- Replaced the plain Mapping production entrypoint with process-bound `VerifiedUpgradeIntentV2`, a fixed verifier port and authority, strict canonical JSON re-decoding, a trusted clock, replay binding, and strict auth/Manifest/approval expiry.
- Bound Intake trust provenance, sorted target modules, and exact Manifest-owned authorized write paths into receipt v2; the wider impact scope no longer implies write access.
- Added exact Schema-producer communication direction, explicit relay, reciprocal, duplicate, and quarantine route checks.
- Coupled `BOUND` to a null invalidation reason and `STALE` to a non-empty reason in the receipt Schema.
- Added a real Intake runtime/Schema to sealed Resolver to Receipt Schema test plus provenance, cross-authority, raw substitution, expiry equality, canonical wire, write-scope, and producer-reversal attacks.
- Added exact Git index-entry and porcelain-v2 status identities to diff material so staging or index-only mutations invalidate an otherwise byte-identical receipt.
- Closed cache-hit attestation bypass by re-verifying every reuse and comparing the complete verified authority material before returning an existing capability.
- Made receipt currentness validation consume the locked public v2 Schema and canonical receipt-ID hash; invalid, non-BOUND, or unrecomputable inputs no longer fabricate Schema-invalid STALE objects, while valid stale records preserve the prior receipt identity and source bindings.
- Completed real Intake-derived positive and negative coverage for all four domain-separated digests.
- Kept portable cross-process signature or mTLS/DB trust `WAITING_EXTERNAL`; the module remains proposed and release-ineligible.

## 0.2.0 - 2026-07-15

- Added `instruction.receipt/v2` and moved v1 receipt and Intake declarations to `quarantine-only` with no v1 runtime communication.
- Replaced contract-family truncation with exact `(contractId, major)` owner and consumer indexes that retain source, status, mode, owner, and declaration kind.
- Separated optimistic future contract expectations from verified baseline facts and added `introduce-quarantined-major` for historical wire majors absent from the baseline.
- Bound the domain-separated full Intake digest, requester authority proof, Manifest ownership proof, risk, stage, and authorization disposition into every receipt.
- Removed caller-supplied diff overrides, used identical bytes for Git Blob and SHA-256, and added double final snapshot checks plus controlled race attacks.
- Added v2 runtime/Schema parity tests, four-kind production receipts, canonical order and path attacks, exact-major consumer attacks, stale/tamper attacks, and a frozen v1 Schema SHA-256.
- Rejected active consumers of introduced quarantine-only majors and rejected hidden or non-reciprocal exact-major communication edges before they can suppress consumer instruction scope.
- Kept the module proposed and release-ineligible; local PASS does not raise the formal evidence level.

## 0.1.0 - 2026-07-14

- Tightened nullable identity envelopes plus trace/idempotency to exact canonical opaque forms in runtime validation, Schema, and adversarial fixtures.
- Registered the versioned `instruction.receipt/v1` receipt path to `factory-control-plane-host`.
- Proposed exact instruction, Manifest, contract, Git blob, and diff binding with stale detection.
- Added a required Draft 2020-12 `instruction.receipt/v1` contract suite with production `InstructionResolver` output plus fail-closed negative instances; the Manifest candidate minimum is now `CONTRACT_VERIFIED`, without claiming formal verification.
- Bound candidate-gate policy, schemas, runners, locked toolchain files, and adversarial tests; whole-repository receipts now admit only explicitly allowlisted global engineering paths, record removed legacy `.omo` files as tombstones, and exclude generated test artifacts from instruction hashes.
- Expanded the candidate trust closure to bind the root instructions, core generated governance and compatibility policies, solution and NuGet inputs, shared .NET build properties, locked SDK selection, repository validator, and release syntax entry point so those controls cannot be weakened and approved by the same clean candidate run.
- Bound `Directory.Build.targets` into candidate trust receipts and fixed Resolver Git discovery to `/usr/bin/git` with system/global configuration disabled and dangerous ambient `GIT_*` state excluded.
