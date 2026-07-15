---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: factory-trusted-runner
manifest: ./module.yaml
applies_to: .
---

# Factory Trusted Runner Agent Rules

## Scope

This module runs exact, policy-owned argv templates with `shell=False`, verifies active fencing authority, and emits hashed `trusted.test.result/v1` evidence plus `merge.request/v1`. It cannot accept request-authored commands, required flags, role lists, or release decisions.

## Required reading before the first write

Read root and module AGENTS, this Manifest, all contracts, dependency and compatibility data, risk policy, `operations/trusted-runner-policy.v1.json`, tests, and rollout/rollback instructions. Rebind after any policy, instruction, Manifest, contract, worktree, or diff change.

## Invariants

- Command argv, working directory, timeout, required status, success marker, forbidden markers, environment keys, and role separation come only from a trusted policy whose SHA-256 is bound.
- Candidate code is executed by the previous stable signed Runner artifact. Production policy digests, runtime-fact authentication material, RSA keys, and release trust-store public keys come from external deployment configuration; a candidate repository may only propose a policy and can never authorize its own copy.
- Shell interpreters, command strings, untrusted environment expansion, traversal, symlinked repository paths, and unknown templates fail closed.
- Required evidence releases only on `PASS` with exit code zero and all trusted success conditions; `SKIP`, `PARTIAL`, `NOT_RUN`, and infrastructure errors never pass.
- The runner identity is distinct from implementers and release approvers.
- A stale instruction receipt or fencing token prevents execution.
- Output content is not returned as evidence; only bounded hashes and deterministic metadata are emitted. Every result carries a process-bound RSA-PSS attestation over its canonical payload; an identifier without a verifiable signature is never evidence.

## Stable policies

- `SOUL-ISO-001`, `CMD-IDEMP-001`, `RESULT-VERIFY-001`, `GBRAIN-READBACK-001`, and `EDGE-NORESTART-001` remain mandatory.

## Communication, tests, and rollout

Consume `instruction.receipt/v1`, `worktree.plan/v1`, `worktree.lease/v1`, and `merge.request/v1`; provide `trusted.test.result/v1`. Test injection, forged roles/checks, stale fences, timeouts, output limits, skip/partial output, symlinks, and exact argv. Shadow runs synthetic fixtures only; rollback routes to the prior signed runner and `factory_disable_trusted_execution` stops new processes.
