# evidence-service contracts

Provided contract schemas have one owner: this module. Consumed schemas are referenced from their owner and are not copied or modified here. Every cross-module payload rejects unknown major versions and includes the identity, trace, idempotency, time, and privacy fields required by the root policy where applicable.
