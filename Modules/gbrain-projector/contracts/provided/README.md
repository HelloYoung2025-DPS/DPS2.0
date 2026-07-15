# Provided contracts

Only this module may change schemas owned here. Additive changes stay within a major; breaking changes create a new major and follow the compatibility rollout sequence.

`gbrain.projection/v1` and `gbrain.source-id/v1` are byte-frozen quarantine artifacts with no runtime authority. `gbrain.source.binding/v1` owns the persistent full-Soul mapping proof and accepts only exact schema `1.0.0`, fixed derivation, and nonce `0..1023`. `gbrain.projection/v2` accepts only exact schema `2.0.0` and carries that proof plus the logical projection revision/checksum for exact downstream read-back.
