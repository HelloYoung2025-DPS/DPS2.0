# Consumed contracts

- `action.proposal/v1`, owned by `planner`.

`action.execution.promotion/v1` is not a consumed-owned contract entry: Policy Approval uniquely owns the API input schema and strict codec, while `producer_module` is fixed to `control-plane-host` and the communication edge remains inbound.
