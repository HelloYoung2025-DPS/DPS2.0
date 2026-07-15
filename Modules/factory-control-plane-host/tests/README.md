# Factory Control Plane Host tests

The suites are intentionally separated by evidence kind:

- `test_factory_control_plane_host.py` proves local policy, role, fencing,
  idempotency, atomic kill-switch/write ordering, rollback-authorization
  continuity, immutable fixed-argv deployment, bounded process output, and
  capability invariants. It also attacks pre-exec file-identity replacement,
  exact per-module trusted-result binding, duplicate evidence, and crash-safe
  implementation-ready ordering, including trusted-clock lease expiry at the
  instant a ready or independent result is consumed. Migration discovery uses
  one held no-follow directory descriptor and rejects gaps, duplicate numbers,
  malformed names, file/directory symlinks, path replacement, and invalid
  UTF-8. The admin connection's failure bounds are tested. Intake replay
  extraction independently recomputes the v2 full-intent digest and emits only
  domain-separated claim hashes.
  The same suite consumes a fixture produced with the frozen Release
  `release.bom.native.stop.authority.trust/v1` wire functions and attacks
  unknown/missing majors, the unpublished hyphenated draft, non-canonical
  bytes, Release/provider signatures, BOM/generation/three-authority drift,
  issuer/audience/revocation, direct constructors, Mapping/lambda providers,
  Authority A to B replay, raw/provider swaps, expiry equality, missing
  provider, durable restart revalidation, and same-ID/different-SHA
  quarantine, including conflicts across workflow IDs through the global
  append-only binding index. These are process-bound boundary tests, not
  production key or device evidence; fixtures contain no private key or raw
  activation token.
- `test_contracts.py` validates the six host contracts and every synthetic
  provider output against its owning module's Draft 2020-12 schema. It also
  proves the Host's direct native-stop trust declaration and inbound edge are
  the exact reciprocal of the frozen Release declaration/outbound edge.
- `test_end_to_end_simulation.py` runs the complete deterministic, zero-side-
  effect workflow, rollback, crash window, and restart recovery path. Its
  ceiling is `INTEGRATION_VERIFIED`; it is not production or device evidence.
- `test_postgres_integration.py` requires a real PostgreSQL 18.4 instance with
  distinct `DPS_TEST_POSTGRES_ADMIN_URI` and
  `DPS_TEST_POSTGRES_RUNTIME_URI` identities. It proves that the runtime role
  receives only the exact schema/table/sequence privileges needed to
  append/read and cannot mutate, alter, drop, disable triggers, inherit another
  role, own objects, create database objects, or execute migration functions.
  Missing, same-role, elevated-role, or wrong-version infrastructure is
  `INFRA_ERROR`, never a skip or pass. It also proves ordered migration replay
  and hash-drift rejection plus simultaneous two-connection Intake races for
  exact full-digest reuse, identical claims/different digests, and partially
  overlapping claims. Conflicts must be append-only and all-or-nothing, with no
  deadlock, losing receipt, acknowledgement, or partial claim binding. It also
  covers migration `003`, global native-stop receipt ID/full-SHA binding,
  restart readback, cross-workflow conflicts, and append-only/least-privilege
  attacks against the binding table.
