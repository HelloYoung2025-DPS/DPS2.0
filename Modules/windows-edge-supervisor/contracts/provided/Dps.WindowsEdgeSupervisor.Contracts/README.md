# Dps.WindowsEdgeSupervisor.Contracts

This is the only code-level owner of the signed Supervisor drain directive,
the durable-drain Worker receipt, both signing statements, strict wire codecs,
and the canonical Journal payload.
Worker and Supervisor consumers must reference this pack instead of copying
field order or framing code.

The Worker receipt and the rich Journal attestation are independent raw wires.
This pack does not reference Journal contracts, runtime, signing authority,
state store, or private keys. Journal treats the exact persisted Worker-receipt
wire SHA-256 as opaque durable data. Supervisor verifies both signatures and
correlates that digest before cutover or rollback.

This package provides canonical bytes and strict contract serialization. Trust
roots, freshness policy, active-drain expectations, and release authorization
remain runtime responsibilities.

New intake must use `DrainDirectiveV1Codec.DecodeAndVerify`, including its
bounded wall-clock freshness check. `DecodeAndVerifyDurableContinuation` exists
only for an exact raw wire already recorded as PREPARED or COMMITTED: it still
requires canonical bytes, the complete active expectation, the pinned SPKI key
ID, and a valid RSA-PSS signature, but does not re-apply wall-clock freshness.
It must never authorize a new drain or justify re-signing an expired directive.

Fresh Worker completion intake must use
`WorkerDrainReceiptContractCodec.DecodeAndVerify`. The receipt
`DecodeAndVerifyDurableContinuation` overload is only for the exact signed raw
wire already present in durable Worker/Journal state. It intentionally skips
only wall-clock freshness; Supervisor must still obtain and verify a newly
issued, independently signed Journal owner attestation that binds that exact
wire digest before changing any route.
