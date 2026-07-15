# Consumed contracts

`upgrade.intent/v2` is the only runtime input consumed from `factory-upgrade-intake`. The resolver independently checks its exact shape, canonical domain-separated hashes, routable authorization disposition, authority proof identifiers, exact baseline, target modules, requested paths, contract-major expectations, and full-intent digest. Every expected contract source must be in the requested write scope.

`upgrade.intent/v1` is declared only to identify and reject historical wire data as `quarantine-only`; it has no inbound communication edge and never becomes a processable domain object.
