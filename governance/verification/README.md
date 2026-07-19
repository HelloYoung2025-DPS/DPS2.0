# F6-F9 external verification contracts

Status: executable preparation only; no external level has been issued.

The four stage input schemas are:

- `f6-windows-zenno-input.v1.schema.json`
- `f7-device-gbrain-input.v3.schema.json`
- `f8-canary-input.v1.schema.json`
- `f9-scale-input.v1.schema.json`

F7 also consumes:

- `f7-windows-prerequisite-receipt.v1.schema.json`
- `f7-raw-evidence-artifact.v1.schema.json`

F9 also consumes six raw-artifact schemas:

- `f8-canary-prerequisite-receipt.v1.schema.json`
- `f9-module-dependency-dag.v1.schema.json`
- `f9-compatibility-matrix.v2.schema.json`
- `f9-compatibility-execution-evidence.v1.schema.json`
- `f9-compatibility-combination-observation.v1.schema.json`
- `f9-load-run-artifact.v1.schema.json`

`f9-compatibility-matrix.v1.schema.json` is retained only for historical
verification. The executable F9 gate rebuilds v2 from the exact signed module
Manifests, requires every contract-major declaration to carry an explicit
mode, and rejects missing or unknown major/mode behavior. The v2 matrix is a
static declaration inventory: its N/N execution combinations remain
`NOT_RUN`, and it cannot by itself prove candidate execution or authorize a
compatibility-group release. F9 therefore requires a separately signed
`dps.compatibility-execution-evidence/v1` artifact plus one exact raw
observation for every N/N, N/N-1, N-1/N, and N-1/N-1 combination of every
eligible runtime row. Missing, duplicated, skipped, partial, not-run, stale,
or independently unverifiable observations fail closed.

`dps.scale-verification-input/v1` was tightened in place in R0-B to require
`manifest_schema_artifacts`, so F9 can hold every signed module manifest to the
exact schema of its declared major instead of trusting the declared version
string. Tightening a released major rather than publishing a v2 was permissible
only because no external producer had ever issued an F9 envelope; the affected
population was zero. Once a real envelope is signed, this file is frozen: any
further shape change goes to `dps.scale-verification-input/v2`. A retained v1
reader is not an option — a reader that accepts envelopes without
`manifest_schema_artifacts` has no schema to validate manifests against and is
byte-for-byte the pre-R0-B path that could return ELIGIBLE for a manifest
violating its own major.

`f7-device-gbrain-input.v1.schema.json` and v2 are historical only and are
rejected by the executable gate. F7 v3 binds one current signed F6 prerequisite
receipt, two projection artifacts, two Search artifacts, and exactly 24
semantic artifacts through unique raw artifact IDs and SHA-256 digests. All F7
evidence artifacts use `f7-raw-evidence-artifact.v1.schema.json`; the older
projection/Search artifact schemas are historical compatibility records.

They compose `external-evidence-envelope.v1.schema.json`. Runtime trust is
defined by `external-trust-policy.v1.schema.json` and must be injected outside
Git. `external-gate-policy.v1.json` records the non-reducible wave orders and
minimum thresholds mirrored by the executable validator.

Its F7 contract binding is currently `STALE`: an independent F2 audit
invalidated the 2026-07-15 candidate projection/source-binding hash set. Those
hashes remain only as drift detectors while the repaired v2 contracts are
prepared. They are not a freeze or DEVICE_VERIFIED claim.

These contracts are separate from repository/static test evidence. They accept
raw observations only and do not contain a self-declared `verification_level`.
The runner derives the target level from the selected F-stage, then verifies
issuer scope, signatures, exact artifacts, environment claims, sequence, and
thresholds. Environment claims use a stage-fixed exact allowlist: F6 pins the
full Windows/Zenno/.NET/C#/CodeDom/GAC/DLL/project-load/ADB/Bridge/loopback/
timeout/error/continuity tuple; F7 pins Windows+Android, GBrain deployment,
parent Windows environment, Edge and Zenno installation IDs, and the external
runner component/version/binary/SBOM digests; F8-F9 accept only
`environment_id`, `os_family`. The
trust policy must pin exactly those keys and cannot expand them. Each value
follows a field-specific grammar; free text and
secret/key/token/password material fail closed. A separate evidence service and authorized approver remain
responsible for any eventual signed evidence receipt.

Governance changes cannot approve their own evidence. Updating these schemas,
the runner, the trusted issuer list, and a production release approval must be
independently reviewed actions.

For F7, canonical `soul_`, `db_`, and `pa_` identifiers are mandatory. The
current trust policy pins the exact original F6 evidence/environment/window and
Edge/Zenno instances accepted as the prerequisite. F6 receipt, F7 attestation,
and Release BOM signing keys are cryptographically distinct. The signed BOM
also pins the F7 runner version, binary, and SBOM on one runner module entry;
every artifact's producer and complete
Windows+Android environment must match it exactly.

The F7 payload fixes one contiguous causal timeline:
Observe → Verify → MemoryEvent → Interest → GBrain projection → exact readback
→ delete/rebuild. Every raw observation binds the same run, trace, BOM,
scope digest, phase, and capture window. Projection and Search artifacts bind
canonical bytes and recomputed checksums. Each semantic artifact additionally
binds canonical request, response, and postcondition byte records. Counts and
summary statuses are checked against concrete arrays of records, native
receipts, audit events, side-effect receipts, purge-layer rows, and exact
postcondition reads. Exchange outcomes are fixed per artifact kind, and the
concrete command, idempotency, scope, attack, and audit identifiers are
recomputed rather than inferred from array length.

After the repaired F2 set is independently re-frozen, F7 accepts only
`gbrain.projection/v2` projection bytes and separately
bound `gbrain.source.binding/v1` bytes. The Source candidate is `dps-` plus the
first 28 lowercase hex characters of
`SHA-256(ASCII("dps.gbrain-source-binding/source-id/v1\\0") || complete ASCII
soul_id || NUL || signed int64 big-endian nonce)`, with nonce 0..1023. The gate
recomputes Source ID, full Soul hash, binding revision, binding checksum,
projection checksum, and exact canonical bytes. The same binding
nonce/revision/checksum and native GBrain Source ID must appear in projection
v2 and OAuth whoami; an adapter alias alone is insufficient.
Projection v1 and the legacy first-28-Soul-prefix mapping are quarantine-only.

Each of CROSS_SOUL, CROSS_DEVICE, and CROSS_ACCOUNT is required in both
directions and may alter only its named axis. The attack request uses the actor
Soul's exact verified OAuth credential lease and token fingerprint. The two
Souls' physical device attestations, OAuth client IDs, credential leases, token
fingerprints, devices, accounts, and Sources must all be different. Persona
current uses exact fixed-slug bytes; export/deletion scope hashes are recomputed;
delete/rebuild must bind the current projection revision/checksum; duplicate
delivery must reuse the fixture command and idempotency key; UNKNOWN_OUTCOME
must be a distinct command reconciled through exact reads without retries.
Cached, unscoped, wrong-Soul, unknown-major, stale, digest-only, mixed-run, or
extra evidence fails closed. General IDs must start with an ASCII letter, so
numeric phone-shaped values are rejected.

F6 requires a complete capability tuple: exact ZennoDroid, .NET Framework and
C# versions; successful CodeDom, GAC, DLL and Zenno project loading probes; ADB
authorization; versioned Bridge ABI; fixed `127.0.0.1` port; bounded command
timeout with fail-closed semantics; native-error preservation; and connection
continuity. Every capability value must equal the trust-pinned environment
tuple. Process observations exactly bound the signed measurement window while
the unchanged process start must be no later than that window.

F9 requires a cryptographically verified F8 receipt, not a Boolean prerequisite.
The raw receipt is signed with the same P-256/P1363 runner-attestation algorithm
by an issuer explicitly trusted for `CANARY_VERIFIED`, and binds the exact F9
commit, Release BOM id and bytes, and candidate artifact digest. The verified
Release BOM in turn binds the integration commit, every raw module Manifest,
the canonical dependency DAG, compatibility policy, compatibility matrix,
previous stable BOM, contract schemas, and signed compatibility-execution
artifact. The runner rebuilds edges, waves, contract owners, exact schema
producers, transport senders, reciprocal receivers, and communication-pair
hashes from those signed bytes; ownership is never treated as proof of runtime
production. Missing or forged edges and hidden contract dependencies fail
closed. A compatibility group additionally binds its complete member set,
contract edges, release order, and atomic rollback unit. It
accepts at most four rollout lines and rejects direct or transitive dependencies
across lines. Each sustained, burst, and simulated run binds a different
`dps.f9-load-run-artifact/v1` JSON document. Actor cardinality, concurrency,
timestamps, duration, 72-hour coverage and backlog recovery are recomputed from
raw windows and recovery samples. Every window's peak oldest backlog age is
capped at 120 seconds, adjacent windows cannot show monotonically growing
unresolved backlog, and final backlog must clear within 120 seconds and remain
stable for five minutes. The first recovery tuple must exactly equal the final
window tuple at the shared timestamp, and all recovery ages retain the same
120-second cap; marker bytes or a summary alone are invalid.
The module rollback drill is capped at five minutes.
