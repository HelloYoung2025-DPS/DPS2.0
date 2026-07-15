# Consumed contracts

The release controller consumes only the versions declared in `module.yaml`:

- `merge.decision/v1` for the exact merge-head decision.
- `artifact.build.request/v1` as the contract owned by the artifact builder and emitted by this controller.
- `artifact.descriptor/v1` for unsigned build output awaiting the external signing boundary.
- `upgrade.event.append/v1` and `upgrade.event/v1` for durable state and proposed replay. Replay additionally requires an independently authenticated ledger stream-head anchor; the replay bytes and their recomputable local hashes cannot supply that authority. Because that provider is not yet wired, the public recovery entrypoint currently fails `WAITING_EXTERNAL` for every stream.
- `rollback.request/v1`, owned by the rollback controller, for the fixed declaration emitted only after `ROLLBACK_REQUIRED`.

Unknown majors and unregistered producers fail closed. Runtime code does not import another module's internal types.

`upgrade.intent/v1` and `/v2` are deliberately absent. The Control Plane Host resolves intake output into a host-authored `rollout.command/v2`; the Release Controller rejects raw intents at the top level, nested in transition evidence, or disguised as receipt references. Frozen `rollout.command/v1` is owner-declared quarantine-only and is not a consumed runtime contract.
