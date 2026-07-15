---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: platform-authorization-authority
manifest: ./module.yaml
applies_to: .
---

# Platform Authorization Authority Agent Rules

## Scope

This module is the sole DPS owner and producer of `platform.account.authorization.evidence/v1`. It validates and normalizes untrusted raw external-platform proof, then wraps the resulting DPS decision in an internally signed evidence envelope. It never claims that an external platform issued the DPS envelope and does not own platform accounts, account lifecycle state, bindings, policy approval, device actions, or GBrain data.

## Required reading

Before the first write, read the root `AGENTS.md`, this file, `module.yaml`, every provided and consumed contract, the generated dependency graph and compatibility matrix, the required tests, and rollout/rollback instructions. Bind exact instruction hashes. Rebind when the diff expands or a consumer changes.

## Invariants

- `SOUL-ISO-001`: Soul, device binding, and platform account identifiers are canonical, non-null, exact-scope values; raw email, phone, login, cookie, token, or platform identifier data never enters the public evidence contract.
- `CMD-IDEMP-001`: the same exact scope and idempotency key must replay the byte-identical signed envelope; a different payload under that key fails closed and is quarantined by the durable receipt implementation.
- `RESULT-VERIFY-001`: raw external proof is untrusted input. Parsing or receiving it is not authorization; an explicit platform-proof verifier must validate it before DPS evidence may be issued.
- `GBRAIN-READBACK-001`: this module has no GBrain access.
- `EDGE-NORESTART-001`: this module makes no Windows, ZennoDroid, or device verification claim.
- The production P-256 private key is external to this repository and process. The compiled issuer, key ID, root SPKI, and SHA-256 pin cannot be replaced by a caller-provided key.
- Evidence binds the exact canonical scope, alias digest/key epoch, target status/revision, command trace/idempotency/time, active Release BOM/generation, and a validity window no longer than fifteen minutes.
- Unknown versions, issuers, keys, proof formats, statuses, revisions, runtime generations, malformed identifiers, control characters, newline suffixes, stale proof, stale runtime, or invalid signatures fail closed.
- Production issuance additionally requires an injected durable exact-envelope receipt store. An in-memory dictionary is test-only and cannot satisfy production readiness. This repository currently defines the durable store contract but intentionally ships no production persistence implementation.

## Communication and boundaries

Only the versioned `platform.account.authorization.evidence/v1` contract may cross to `platform-account-registry`. Do not import account-registry internal types, read its tables, share mutable static state, store raw platform proof in the evidence envelope, or accept a caller-selected verifier, trust root, or production signer. The account registry remains the consumer and lifecycle authority; this module is only the evidence issuer.

## Tests and rollout

Required checks release only on `PASS`. Contract tests must cover strict JSON, duplicate/unknown/missing fields, canonical identifiers and newline rejection, exact UTC lexical parity including year-zero and leap-second rejection, delimiter-collision-resistant canonicalization, fixed root metadata, wrong-key signatures, expiry, cross-scope replay, exact-envelope idempotency, and conflicting-payload rejection. Production remains disabled until durable receipt storage, real external-signer composition, integration evidence, bounded canary, kill switch, and signed-BOM rollback are all present.
