# Internal append authorization

`append.authorization.v1.schema.json` documents the detached envelope consumed
by `ExternalAppendAuthority`. It authenticates the exact canonical bytes of an
existing `upgrade.event.append/v1` command; it does not change that public v1
wire contract and is not emitted as a public event.

Production key material is loaded only from the fixed process environment.
The resulting capability is process-bound, non-copyable, non-serializable,
short-lived, audience/scope/producer-bound, and revalidated by the repository
at append time. The local issuer exists only for unit and file-fixture tests.
