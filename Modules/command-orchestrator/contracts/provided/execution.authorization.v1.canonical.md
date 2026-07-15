# `execution.authorization/v1` canonical signature

This proposed contract is owned by Command Orchestrator and consumed by Executor Gateway. It is not a Release BOM and does not prove that the referenced BOM is active; Executor Gateway must separately read cryptographically authenticated current activation truth before every native call. The signed payload binds the exact BOM digest, its monotonic active generation, and the SHA-256 of an opaque 256-bit execution token. The raw token is supplied only by the trusted active-BOM reader to the native transport.

The signed payload excludes only `signature_base64`. Its first token is the exact `signature_domain`. Every following field is encoded in the order declared by `ExecutionAuthorizationProtocolV1.CanonicalAuthorizationBytes`: UTF-8 field name followed by UTF-8 field value, each preceded by an unsigned 32-bit big-endian byte length. GUIDs use lowercase `N` format, UTC timestamps use invariant round-trip `O` format, booleans use lowercase `true` or `false`, and integers use invariant decimal text.

`command_sha256` is lowercase SHA-256 over `ExecutionAuthorizationProtocolV1.CanonicalCommandBytes`. That byte stream uses domain `dps.command-orchestrator.command-dispatch/v1`, binds every `command.dispatch/v1` field including authoritative `approval_sha256`, preserves step ordinal, and sorts only argument-map keys using ordinal comparison. It never sorts steps by `step_id`.

Sign the raw canonical authorization bytes with NIST P-256 ECDSA and SHA-256. Encode the signature as the fixed 64-byte IEEE P1363 `r || s` representation, then Base64. DER ECDSA signatures, other curves, other hashes, other domains, and unknown encoding labels fail closed.

`execution.authorization.v1.canonical.json` is the machine-readable specification. Its exact SHA-256 is bound from the JSON Schema and it contains a language-neutral command byte vector, authorization byte vector, digests, P-256 public key, and valid P1363 signature. A change to the machine specification, field order, token binding, or vector invalidates consumer instruction receipts.
