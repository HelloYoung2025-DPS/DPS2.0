# Consumed contracts

Schemas consumed from another owner are referenced through ../module.yaml. Do not fork or locally weaken an upstream schema.

The v2 adapter consumes `soul.resolved/v1` only as exact raw material returned by a fixed, current Soul authority; the DTO alone is never authorization. It also consumes exact canonical bytes for `command.receipt.signed/v1`, independently verifies its P-256 signature, and requires native/postcondition digests plus `OBSERVATION_VERIFIED`. Current upstream modules do not yet declare the reciprocal memory-specific authority path, so compatibility remains intentionally red and production composition fails closed.
