---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: platform-account-registry
manifest: ./module.yaml
applies_to: .
---

# Platform Account Registry Agent Rules

## Scope

This module owns stable platform-account identity, hashed authorized aliases, authorization revision, lifecycle status, and provider-side reservations that freeze an exact revision for binding. It does not own Souls, devices, the authoritative binding relationship, credentials, login automation, platform actions, or GBrain data.

## Required reading

Read the root rules, this file, `module.yaml`, every contract, the dependency graph, compatibility matrix, test rules, and rollout/rollback instructions before the first write. Bind exact hashes; an expanded diff or new consumer requires rebinding.

## Invariants

- `SOUL-ISO-001`: every public result preserves a canonical Soul, binding, and account operation scope, but account identity does not establish the binding; raw email/phone/platform identifiers never cross the public contract.
- `CMD-IDEMP-001`: duplicate authorization requests are deterministic; a conflicting idempotency key is rejected.
- `RESULT-VERIFY-001`: only explicitly authorized status is bindable.
- `GBRAIN-READBACK-001`: no GBrain access or credential storage.
- `EDGE-NORESTART-001`: no Windows or ZennoDroid claim.
- Authorization mutations require a signed `platform.account.authorization.evidence/v1` envelope uniquely owned and produced by `platform-authorization-authority` under the compiled P-256 trust root. That authority verifies and normalizes raw external proof; this consumer never treats an external platform as the DPS contract producer. The envelope binds the exact canonical account ID, scope, alias HMAC key epoch, target status, revision, Release BOM, generation, and command envelope; caller-provided approval strings never authorize a mutation.
- The authorization signer and its private key are external to this repository. Missing, untrusted, invalid, expired, replayed-across-scope, or mismatched evidence fails closed.
- Startup must verify every embedded migration against the persisted source SHA-256 ledger and persist the active Release BOM under a monotonic generation fence before any operation. Evidence must still be fresh immediately before commit; expiration during a database wait or crash window rolls back the whole mutation.
- Alias collisions, revoked accounts, unknown versions, and stale revisions fail closed.
- Authorization refresh, suspension, and revocation use the same account lock as reservation transitions and fail closed while a held-unexpired or active reservation exists.

## Communication and boundaries

Communicate only through the versioned API, `platform.account.authorization.evidence/v1`, `platform.account.authorized/v1`, `platform.account.binding.reservation/v1`, and declared queries, commands, receipts, or events. All three common identity fields are canonical and non-null; UUID and `not_applicable` placeholders are forbidden. Never pass raw aliases or credentials. Do not read another module's tables, import internal types, share mutable static state, or add undeclared peers. A contract change requires consumer instruction rebinding.

## Tests and rollout

Required checks release only on `PASS`. Cover alias collision, revocation, optimistic concurrency, duplicate delivery, reservation expiry/confirmation/release, mutation freeze, unknown versions, and cross-account isolation. Production use remains human-approved behind a feature flag, bounded canary, kill switch, and signed-BOM rollback.
