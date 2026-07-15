# Consumed contracts

No public runtime contract is consumed by this module. Requester authentication and Manifest ownership are injected through trusted process-composition ports, not imported as another module's internal type or accepted as model-authored claims.

Intake does not consume or produce `rollout.command` and does not communicate directly with the Release Controller.
