# Provided contracts

The Release Controller uniquely owns only the provided versions declared in `module.yaml`.

- `rollout.command/v1` and `rollout.event/v1` are byte-frozen, deprecated, quarantine-only historical shapes. They have no runtime communication edge.
- `rollout.command/v2` is the strict Host-authored transition request accepted by the Release Controller.
- `rollout.event/v2` is the durable active rollout state event emitted to the Host and Rollback Controller.
- `release.bom.native.stop.authority.trust/v1` is the Release-authored, externally signed public trust receipt delivered to `factory-control-plane-host`. It binds the exact Release BOM ID, digest, generation and activation-token digest to Worker native-stop, Supervisor route-assignment and Policy challenge authority sets.

The native-stop trust receipt carries public key digests, set digests, validity/revocation metadata and signature proof only. It must never contain a raw private key, service credential, secret or raw activation token. Its producer is exactly `factory-release-controller`; unknown majors, extra fields, trailing newline normalization, mismatched set digests/signatures, revoked authorities and unregistered consumers fail closed. Host activation is not authorized until its reciprocal inbound Manifest edge is present and matches every communication attribute.
