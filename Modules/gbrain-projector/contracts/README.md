# gbrain-projector contracts

Provided contract schemas have one owner: this module. Consumed schemas are referenced from their owner and are not copied or modified here. Every cross-module payload rejects unknown major versions and includes the identity, trace, idempotency, time, and privacy fields required by the root policy where applicable.

`gbrain.projection/v1` and the v1 collision corpus are byte-frozen, deprecated, quarantine-only artifacts. Active proposed contracts are `gbrain.source.binding/v1` and `gbrain.projection/v2`.

Source candidate canonical bytes are ASCII `dps.gbrain-source-binding/source-id/v1` followed by NUL, the complete ASCII `soul_<64hex>`, another NUL, and nonce `0..1023` encoded as signed eight-byte big-endian. The lowercase SHA-256 digest contributes its first 28 hex characters after `dps-`; PostgreSQL uniqueness, not truncation, is the isolation authority. The DTO and authority both recompute this exact relation, and schema versions must equal `1.0.0` or `2.0.0` rather than merely sharing a major.
