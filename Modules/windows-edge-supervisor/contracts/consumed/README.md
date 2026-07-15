# Consumed contracts

- `edge.bridge.exchange/v1` is owned by `zenno-bridge` and is the only accepted ZennoDroid loopback envelope.
- `edge.journal.receipt/v1` is owned and produced by `edge-local-journal` after durable flush.
- `edge.journal.drain.attestation/v1` is owned and produced by `edge-local-journal`; the Supervisor verifies its independently pinned signature and its exact Worker-wire binding.

The Supervisor is not a producer of `edge.journal.append/v1` and has no append communication edge. Durable drain truth enters only through the declared receipt and attestation boundaries; route state and the Supervisor evidence chain remain Supervisor-owned stores.

The supervisor returns commands, acknowledgements, and waits through its owned `edge.bridge.directive/v1` contract. Request and response provenance are never conflated.

The module must not copy or fork either schema. Unknown majors fail closed.
