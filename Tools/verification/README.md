# DPS F6-F9 external gate runner

This directory contains a fail-closed validator. It does not sign, persist, or
issue `WINDOWS_VERIFIED`, `DEVICE_VERIFIED`, `CANARY_VERIFIED`, or
`SCALE_VERIFIED` evidence.

## Invocation

Run the validator in the environment that owns the raw evidence:

```text
python Tools/verification/run_external_gate.py \
  --stage f6 \
  --input <absolute-or-runner-local-evidence.json> \
  --trust-policy <absolute-read-only-trust-policy.json>
```

Stages are `f6`, `f7`, `f8`, and `f9`. The stage selects the target verification
level and thresholds. Input documents deliberately contain no
`verification_level`; an input cannot promote itself.

Exit codes and decisions:

| Exit | Status | Meaning |
|---:|---|---|
| `0` | `PASS` | Facts are `ELIGIBLE_FOR_EXTERNAL_ISSUANCE`; no receipt was issued. |
| `1` | `FAIL` | Evidence was unsafe, malformed, untrusted, inconsistent, or below threshold. |
| `3` | `WAITING_EXTERNAL` | Evidence, trust material, OpenSSL, or a raw artifact is not available. |

Mock, hosted, and simulated envelopes cannot satisfy an external gate. The
400-device simulation is a required, explicitly simulated sub-run inside real
F9 scale evidence; it does not replace the 100-device sustained or 200-device
burst observations.

## Signature boundaries

The trust policy is injected at runtime from an absolute, non-symlink,
deployment-owned path. On Unix it must not be group- or world-writable. It
contains only trusted issuer and BOM signer identities, allowed levels, public
key paths, public-key SHA-256 digests, and exact environment claims. Private
keys never enter this repository or runner.

On POSIX runners the policy is opened through a no-follow directory-descriptor
chain and read from the same verified file descriptor; parent symlinks and
post-check path replacement cannot redirect the trust root. A platform without
equivalent safe-open primitives returns `WAITING_EXTERNAL` rather than using a
path-check/read race. A Windows reparse-point/ACL implementation is therefore
still required before this validator can run directly on Windows.

Both signature types use ECDSA P-256 with SHA-256 and fixed 64-byte P1363
signatures. The external runner attestation signs:

```text
dps-external-runner-attestation/v1\n + canonical-json(attestation.facts)
```

The Release BOM signature uses the existing `ecdsa-p256-sha256` spelling and
signs:

```text
dps-release-bom/v1\n + canonical-json(release-bom-without-signature)
```

Canonical JSON is UTF-8, keys sorted, no insignificant whitespace, and Unicode
preserved. The attestation `payload_sha256` binds canonical JSON for the whole
external evidence envelope with `attestation` removed. The runner also reads
and hashes every raw artifact and the exact Release BOM bytes.

The external `environment` object is not a free-form metadata bag. F6 requires
the exact Windows/Zenno/.NET/C# tuple plus CodeDom, GAC, DLL, Zenno-project,
ADB, Bridge ABI, loopback host/port/fixedness, timeout/error semantics and
connection-continuity claims. F7 accepts the exact Windows+Android/GBrain/
parent-Windows/Edge/Zenno tuple plus the external runner component, SemVer,
binary SHA-256, and SBOM SHA-256; F8-F9 accept only `environment_id`,
`os_family`. A
trust policy must pin exactly the applicable set and cannot expand it. Values
must match both the policy and the field's fixed grammar. Nested values, free
text, scope/credential-shaped strings,
secret/key/token/password/credential field names, Bearer/JWT-like values, and
common secret prefixes are rejected.

F7 accepts only `dps.device-gbrain-verification-input/v3`; v1 and v2 are
historical and fail closed. V3 first verifies a separately signed, current,
non-revoked F6 `WINDOWS_VERIFIED` receipt. The current trust policy independently
pins the original F6 evidence SHA-256, Windows environment SHA-256, measurement
window, Edge installation, and Zenno installation. The F6 receipt issuer, F7
device issuer, and Release BOM signer must use three distinct public keys.

The 2026-07-15 `gbrain.projection/v2` / `gbrain.source.binding/v1` hash set is
currently **STALE** after an independent F2 audit. The hashes remain in the
candidate tests only to detect drift; they are not a freeze claim and cannot
support a DEVICE_VERIFIED conclusion until F2 publishes and independently
audits a repaired set, then F7 rebinds and reruns.

The signed Release BOM pins the F7 runner version, binary, and SBOM on the same
runner module entry. Every raw
artifact repeats the exact trusted environment and producer identity/version,
and binds one run ID, trace ID, BOM ID/digest, scope digest, strictly ordered
phase, and capture window. Projection and Search checks each require two unique
canonical artifacts. The 24 semantic artifacts cover nine per-Soul behaviors
and all three cross-Soul/device/account attacks in both directions. Semantic
content is not accepted as counters or a `DENY` string alone: each observation
contains separately hash-bound canonical request, response, and postcondition
bytes, and the response/postcondition outcomes must match the artifact kind.
The runner derives returned records, native executions, side effects,
postcondition results, purge-layer emptiness, duplicate delivery behavior, and
UNKNOWN_OUTCOME reconciliation from scoped, identifier-bound records in those
bytes.

Projection evidence is v2-only once F2 is re-frozen. The runner mirrors the candidate
`gbrain.projection/v2` canonical field order and checksum and separately binds
canonical `gbrain.source.binding/v1` bytes. Source IDs are derived from the
complete ASCII `soul_id` and signed 64-bit big-endian nonce 0..1023 under the
`dps.gbrain-source-binding/source-id/v1` domain. Binding revision/checksum,
nonce, complete Soul hash, projection v2, OAuth whoami native Source ID plus
adapter alias, Search readback, and delete/rebuild must agree.
`gbrain.projection/v1` and the old first-28-Soul
prefix mapping are quarantine-only and cannot satisfy F7.

Cross-scope attacks must mutate exactly one identity axis while every other
axis remains fixed. The request must use the actor Soul's verified OAuth lease
and token fingerprint. Both physical-device attestations, OAuth clients,
leases, tokens, Souls, devices, accounts, and Sources must be unique. Exact
Persona readback, projection/Search revision and checksum, delete/rebuild,
fixture command, duplicate delivery, and reconciliation artifacts must remain
on the same causal run/trace/BOM chain. Search freshness is capped at 300
seconds and every returned result is revalidated against the current exact
projection tuple. Digest-only summaries, stale replay, extra artifacts, mixed
environments, and unknown contract majors fail closed.
General external IDs are letter-prefixed opaque values, preventing numeric
phone-shaped values from entering decisions or logs. These external artifact
bytes are sensitive and must never enter Git or runner logs.

F6's capability probe is executable, not a narrative checklist. It binds the
exact ZennoDroid, .NET Framework, and C# versions and requires `PASS` for
CodeDom compilation, GAC resolution, DLL loading, Zenno project loading, ADB
authorization, and connection continuity. The Bridge ABI is versioned; its
endpoint must be fixed on `127.0.0.1`, its bounded port is explicit, timeout
semantics fail closed, and native errors cannot be coerced to success. These
facts are required in addition to 100 alternating A/B cycles, 24 hours, and an
unchanged Zenno PID/process start time. Signed before/after process observations
must exactly bound the evidence measurement window; a future process start or
an observation outside that window fails closed.

F9 cannot promote itself from a Boolean claim that F8 passed. It binds a raw
`dps.external-verification-receipt/v1` F8 receipt whose P-256 signature is
verified against an issuer trusted for `CANARY_VERIFIED`; the receipt must bind
the exact baseline commit, signed Release BOM bytes, BOM id, and candidate
artifact. F9 reads every raw module Manifest whose digest is carried by that
signed BOM, rebuilds the complete dependency DAG and compatibility matrix, and
requires the BOM-bound raw snapshots to match. An omitted, invented,
hidden-contract, direct or transitive cross-line dependency fails closed. One
to four rollout lines are allowed, and an ordinary module rollback must finish
in five minutes. The 100-device sustained, 200-device burst, and 400-device
simulated runs each bind a different versioned JSON artifact. The runner
recomputes scoped-HMAC actor cardinality, every five-minute-or-shorter window's
concurrency, exact timestamps/duration, 72-hour sustained coverage, and backlog
peak age no greater than 120 seconds, no monotonically growing unresolved
backlog across adjacent windows, clearance within 120 seconds, plus five-minute
recovery stability. The first recovery sample is at the same timestamp and must
exactly equal the final window's backlog tuple; recovery age retains the same
120-second cap. Marker bytes and self-reported
summaries cannot satisfy these facts, and simulation can never satisfy either
real-load observation.

OpenSSL is required only for public-key verification. If it is unavailable,
the gate returns `WAITING_EXTERNAL`; it never falls back to checking whether a
signature string is merely non-empty.

## Tests

```text
.venv/bin/python -m unittest discover -s Tools/verification/tests -v
```

The suite uses synthetic facts only. Most positive paths inject a validator
stub and assert eligibility without a receipt. The F7 end-to-end test generates
three ephemeral P-256 test keys and exercises real OpenSSL verification for the
F6 prerequisite receipt, complete F7 envelope attestation, and Release BOM;
tampering or key-role collapse is rejected.
No test output is external verification evidence.
