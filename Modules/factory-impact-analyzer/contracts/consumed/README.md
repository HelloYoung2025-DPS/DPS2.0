# Consumed contracts

## Active v2 inputs

- `upgrade.intent/v2` supplies the canonical request, full domain-separated digest, exact target modules, exact authorized write paths, requested risk/stage, identity envelope, approval binding, and contract-change expectations.
- `instruction.receipt/v2` supplies Resolver provenance, the full Receipt identity and digest, exact read-impact scope, exact-major declaration index, verified Git baseline facts, bound instruction files, and Git diff fingerprint.

The analyzer requires its own process-bound sealed capability for both inputs. Bare JSON cannot be reconstructed into authority. Intent and Receipt are cross-bound across every copied identity, digest, source authority, Manifest authority, approval status/expiry, risk, stage, target, path, expectation, and source trust field.

`receipt.scope` is the instruction/read-impact boundary. `requested_target_modules` plus `authorized_write_paths` are the write boundary. The former never grants writes, and the latter never silently expands to a parent path, glob, consumer, or module root. Plan v2 hashes the instruction scope, path list, and combined module/path write scope separately.

`bound_contract_change_expectations` remain `UNVERIFIED_EXPECTATIONS`. `verified_baseline_contract_facts` describe only the exact Git baseline. A future `changeset.contract-verification/v1` proof is still required after changeset freeze.

## Historical v1 inputs

`upgrade.intent/v1` and `instruction.receipt/v1` bytes are frozen for quarantine parsing only. Their declarations are `deprecated/quarantine-only`; they have no runtime edge and are rejected by `analyze`.

## External trust boundary

The current authorities are process-bound diagnostics. Portable verification across the out-of-process boundary is `WAITING_EXTERNAL`; canonical JSON, receipt IDs, SHA-256, or a caller-provided verifier cannot substitute for it.
