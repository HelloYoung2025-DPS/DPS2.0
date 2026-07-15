# Consumed contracts

The host consumes the exact public JSON outputs declared in `module.yaml` from
the ten Factory modules. It loads no provider implementation or database type.
Provider output is accepted only inside an attested
`factory.module.receipt/v1`, after producer, contract major, payload digest,
workflow, request, role, fencing, prior-output reference, and domain invariant
checks. Each command carries immutable full prior public outputs as well as
their receipt digests, so a provider cannot invent an unrelated intent,
instruction receipt, worktree plan, evidence result, merge decision, artifact,
BOM, or rollback result.

Opaque boundary identifiers use only `db_` plus 32 lowercase hex,
`pa_` plus 32 lowercase hex, `trace_` plus 32 lowercase hex, and `idem_`
plus 64 lowercase hex. Trailing whitespace and line breaks fail closed.

`factory.module.command/v1` is an adapter envelope, not permission to execute
arbitrary code. A deployment adapter maps the fixed `(target_module,
operation)` pair to a process-bound public API or fixed argv profile. Requests
cannot provide or override argv, cwd, environment, credentials, policy, role,
or expected output contracts.

## Release native-stop authority trust

`release.bom.native.stop.authority.trust/v1` is a separate direct inbound
receipt from `factory-release-controller`; it is not wrapped in a module
receipt and it is not interchangeable with `rollout.event/v1`. The reciprocal
edge is exactly `receipt`, 5000 ms,
`same-receipt-id-and-payload-sha256`,
`receipt_id:release_bom_sha256`,
`factory:host:native.stop.authority.trust`, and
`host-must-not-activate-bom-without-verified-trust-receipt`.

The Host accepts only the exact active proposed major 1. Missing or unknown
majors and the unpublished
`release.bom.native-stop-authority-trust/v1` draft are rejected without an
alias. Receipt bytes must be strict canonical UTF-8 JSON with no duplicate
members or floating-point values and must validate against the independently
pinned Release Schema SHA-256.

The receipt is verified through a fixed composition-root authority, not a
Mapping, lambda, caller verifier, or directly constructed capability. The
authority binds the exact BOM ID/SHA/generation/activation-token digest,
integration commit, three authority-set digests, Release signature, provider
identity, issuer, audience, issued/expiry window, nonce, generation, and
revocation. It recomputes every authority digest and treats
`now == expires_at` as expired. Authority A capabilities cannot be consumed by
Authority B, and swapping the provider object or canonical receipt bytes after
composition fails closed.

Durable truth has two append-only records. The global
`native_stop_authority_trust_binding` index binds the receipt ID to its full
canonical SHA and stable BOM tuple across workflows; the workflow-local
`EXTERNAL_FACT_BOUND` event contains the canonical public receipt string and
the current public provider attestation. Restart reconstructs and revalidates
the full receipt and cross-checks the global index; it trusts neither a naked
database row nor a prior boolean. The same receipt ID with a different full SHA
appends a quarantine record and cannot advance.
Only public key identifiers/hashes and signatures are accepted. Raw private
keys, activation tokens, service secrets, passwords, credentials, and API keys
are forbidden.
