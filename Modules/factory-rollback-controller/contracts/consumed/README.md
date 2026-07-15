# Consumed contracts

This module consumes contract sources owned by other modules; it does not copy
or redefine them here.

- `rollout.event/v1` is owned by `factory-release-controller` at
  `Modules/factory-release-controller/contracts/provided/rollout.event.v1.schema.json`.
- `upgrade.event.append/v1` and `upgrade.event/v1` are owned by
  `factory-evidence-ledger` at
  `Modules/factory-evidence-ledger/contracts/provided/`.

The composition root adapts the evidence ledger's append and read-model
operations to `EvidenceLedgerPort`. The rollback module imports no internal
type from either provider. Unknown majors, unknown event types, unexpected
producers, broken sequences, and broken event hashes fail closed.
