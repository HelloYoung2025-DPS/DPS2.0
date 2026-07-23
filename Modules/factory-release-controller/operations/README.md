# Operations

## Candidate gate

R0-C moved the authoritative root release validation entrypoint to the
ordinary, stateless CI tool below. The module-local copy remains only as a
byte-compared rollback/migration-fidelity fixture until the module is removed
in R0-D; release orchestration must not invoke it.

```text
python3.12 Tools/ci/candidate_bom_validator.py \
  --repo-root <clean-checkout> \
  --bundle-root <immutable-bundle> \
  --bom <candidate-bom.json> \
  --previous-bom <previous-stable-bom.json> \
  --native-stop-trust-receipt <external-owner-receipt.json> \
  --minimum-remaining-lifetime-seconds 86400 \
  --schema-sha256 <exact-release-bom-schema-sha256>
```

The CLI validates only. It always loads the migrated
`governance/policies/deployed-release-trust-policy.v1.json`; its identity and
canonical SHA-256 are fixed in the validator code. A caller cannot provide or
override a trust policy, its identities, or its required gates. Each required
gate binds an evidence ID to an exact evidence kind and a minimum verification
level. A signed optional `FAIL`, a wrong kind, or a lower level cannot satisfy
a required gate. The reported candidate ceiling is derived from the required
gates that actually passed and is capped at `INTEGRATION_VERIFIED`. Changing
that anchor is a separately reviewed deployment-governance change and cannot
approve a product candidate in the same run.

The repository stores public verification material only. The corresponding production private keys are intentionally not present, and the initial anchor keys were generated without retaining their private halves; therefore the baseline fails closed until a separately authorized key-provisioning change replaces the anchor. The CLI never commits, tags, deploys, starts a shell, or executes text from a model, BOM, manifest, or artifact. Exit `0` means the candidate bundle passed static signed-BOM validation; it does not mean canary or scale verification.

All JSON crossing the gate uses duplicate-key-rejecting strict parsing and exact accepted shapes. Candidate, previous-BOM, policy, Manifest, descriptor, SBOM, provenance, evidence, and approval inputs are byte-, depth-, node-, and collection-bounded; artifacts are streamed through a fixed maximum-size digest check. The dependency DAG and compatibility matrix are rebuilt from the exact integration-commit Manifests, including dependency version ranges and exact consumed/provider contract sources. A matching hash alone is never sufficient. The previous stable BOM must be in the candidate's Git ancestry and its governed module combination, artifacts, metadata, DAG, and compatibility matrix must still be independently verifiable from the immutable bundle.

## External Release BOM signer contract

The authoritative R0-C issuance and verification profile is [release-bom.v1.auth.json](../../../governance/schemas/release-bom.v1.auth.json). The Release BOM is issued only by a repository-external Owner/KMS release signer. Its RSA private key must never enter this repository, any model, candidate code, the `Tools/ci` process, or a Control Plane runtime process. Repository code contains only schemas, public verification facts, and conformance vectors; neither this controller nor the candidate validator signs, deploys, or persists runtime activation state.

Each accepted external issuance produces and persists two exact canonical wires: a `SIGNED` candidate and its independently RSA-PSS-signed `STABLE` lifecycle twin. Every top-level value other than `status` and `signature` is deep-equal. The new candidate references the exact previously persisted STABLE wire by BOM ID and SHA-256 digest; for non-bootstrap activation, Control Plane Host receives that same exact previous STABLE wire beside the new SIGNED candidate and refuses reconstruction or normalization. The newly issued candidate's STABLE twin remains with the external issuance record for the next candidate's chain.

Each final canonical SIGNED or STABLE wire is bounded to 4 MiB. Both signatures cover the status-specific canonical payload with the fixed `dps-release-bom/v1\n` domain, and the activation token digest is committed by the signer before either wire is issued. Identical issuance retries return the already persisted byte-identical pair rather than generating fresh randomized RSA-PSS signatures.

## Rollout

Only `factory-control-plane-host` may produce active `rollout.command/v2`. Commands contain no risk claim and carry one to 64 unique references in the canonical `receipt:<kind>:<id>` form. `kind` must be one of the code/schema allowlisted enum values, and the lowercase ID cannot have leading, trailing, repeated, or ambiguous separators. The controller never repairs a reference with replacement or normalization. Raw `upgrade.intent/v1` or `/v2` payloads are rejected whether top-level, nested, or disguised as a receipt.

The active wire boundary uses strict UTF-8 JSON with duplicate-member and non-finite-number rejection. It accepts only real UTC calendar seconds formatted exactly as `YYYY-MM-DDTHH:MM:SSZ`, strict lowercase upgrade and opaque identity grammars, exact booleans, and exact nonnegative integer side-effect counts. Schema format plugins are not a runtime dependency; the runtime parser and calendar check are mandatory.

The original `rollout.command/v1` and `rollout.event/v1` schema bytes are fixed by `contracts/provided/rollout.v1.frozen-sha256.json`. Both majors are `deprecated` and `quarantine-only` in the Manifest. They have no communication edge, cannot be a fallback, and cannot advance or replay release state even when a payload fully matches the frozen legacy shape.

The process-bound trusted adapter resolves actor, risk, approval, result, digest, side-effect count, kill-switch state, the exact ordered receipt references, and a non-zero `receipt_set_sha256`. A reference mismatch or invalid digest stops the transition before the evidence ledger is called. The evidence ledger must durably acknowledge the exact `rollout.event/v2`, including that receipt-set binding, before in-memory state advances.

Restart recovery may accept only semantically valid v2 events and a stream-head anchor authenticated independently by `factory-evidence-ledger`. The future fixed adapter must bind that anchor to the requested upgrade, exact event count, final event digest, current ledger generation, and its own authenticated transport/currentness boundary. The internal head DTO and its deterministic `anchor_id` are only inputs to the post-authentication semantic verifier; constructing the DTO or recomputing that ID does not create trust. The local SHA-256 chain detects accidental corruption and discontinuity but is not an authenticity proof: an attacker who changes an event can recompute every unanchored local hash.

No authenticated production provider exists in this baseline. The public `ReleaseController.recover` API therefore has no resolver/callback/DTO injection point and always fails with `WAITING_EXTERNAL` before examining local replay bytes. Unit tests call the private post-authentication verifier with test-only fixtures, including adversarial replay inputs, to prove the later semantic stage; none of those fixtures is production evidence or can be supplied through the public entrypoint. A separately reviewed provider/currentness implementation must authenticate the external head and anti-rollback generation before wiring that verifier. Until then this proposed module remains `releaseEligible=false`, and production recovery stops rather than self-validating.

Shadow uses the signed candidate's exact BOM and artifact-set digests, keeps the kill switch armed, and requires zero side effects. Simulation output is always `SIMULATION`, capped at `INTEGRATION_VERIFIED`, and cannot authorize `CANARY`, `ROLLING`, `SOAKING`, or `COMPLETED`.

On a rollback condition, transition to `ROLLBACK_REQUIRED`, stop routing, and hand the signed previous-BOM reference to `factory-rollback-controller`. Do not run ad-hoc recovery commands.
