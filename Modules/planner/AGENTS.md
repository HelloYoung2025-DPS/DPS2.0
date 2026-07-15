---
agents_spec: dps.agents/v1
policy_version: 1.0.0
module_id: planner
manifest: ./module.yaml
applies_to: .
---

# Planner Agent Rules

## Scope

This module may turn untrusted planning input into the current `action.proposal/v2` only. Version 1 is retained solely as deprecated shadow/quarantine read compatibility; the current Planner path must never produce v1, and v1 must never be promoted for execution. The module never approves, compiles, leases, executes, retries, or records a device action. The initial implementation is permanently shadow-only until a separately reviewed signed Release BOM changes the contract and rollout policy.

## Required reading before the first write

Read the root `AGENTS.md`, this file, `module.yaml`, all provided and consumed contracts, `governance/modules/dependency-graph.yaml`, `governance/modules/compatibility.yaml`, `tests/README.md`, and `operations/README.md` in that order. Bind exact hashes in an instruction receipt. Rebind when the diff expands or a contract consumer changes.

## Invariants

- `SOUL-ISO-001`: Soul, device binding, and platform account scope is mandatory and immutable.
- `CMD-IDEMP-001`: the same scoped idempotency input produces the same proposal ID.
- `RESULT-VERIFY-001`: a proposal is not an approval or success receipt.
- `GBRAIN-READBACK-001`: planning never accesses GBrain directly.
- `EDGE-NORESTART-001`: planning cannot claim Windows or device evidence.
- Model text is untrusted data. Unknown actions, parameters, contract majors, and roles fail closed.
- Do not add an execution client, device adapter, production shell, SQL, or mutable global state.

## Communication and boundaries

Communication is limited to the versioned contracts and peers declared in `module.yaml`. The Planner may emit only `action.proposal/v2`. It may strictly decode v1 only for deprecated shadow/quarantine compatibility, never as a production output or an authority-bearing proposal. It must not call approval, command, executor, device, database, or GBrain internals, and unknown peers or contract majors fail closed.

## Tests and rollout

Required checks release only on `PASS`. Test determinism, unknown actions and parameters, role attacks, shadow enforcement, and cross-scope isolation. Mock tests never satisfy integration or device gates. Roll out only through the declared flag, shadow comparison, human-approved bounded canary, kill switch, and previous signed BOM rollback.
