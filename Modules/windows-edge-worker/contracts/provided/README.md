# Provided contracts

This module does not own a public contract in v0.1. It produces `RECEIPT` and `HEALTH` through a strict encoder using the Supervisor-owned `edge.worker.exchange/v1` schema and produces the independent Worker-only `edge.worker.drain.receipt/v1` wire through the Supervisor owner contract pack. The drain receipt is returned only after its exact wire digest is durably appended through the Journal owner's append/receipt contracts; Supervisor obtains Journal rich attestation directly. These outbound instances are declared in `module.yaml`; ownership, Schemas, auth profiles, corpora, DTOs, and canonical codecs remain with their contract owners.

The withdrawn Executor-owned `native.stop.proof/v1` is not an outbound runtime instance. It is declared only as a deprecated `quarantine-only` consumed major so already-existing artifacts can be bounded and identified with the owner codec. The Worker exposes no public issuer, signing path, native-stop call, raw-wire replay, or communication edge for v1. A local Schema, DTO, digest, or codec fork remains forbidden.

No Worker-facing Policy v2/challenge, Release-BOM authority, route-assignment, or live Supervisor IPC contract is declared until each owner freezes the exact reciprocal API. Their absence is a release blocker, not permission to invent a local contract.
