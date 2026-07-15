# memory-event-ledger source

Production source belongs below this directory. Other modules may depend only on versioned provided contracts, never internal implementation types.

The v2 runtime separates public data contracts from authority. Public `PrepareAsync` accepts only `memory.append.request/v2`, treats every field as untrusted, then obtains sealed Soul and signed-observation capabilities from fixed non-public authorities, builds the event itself, and registers a reference-identity seal. `AppendAsync` revalidates both authorities and the event hash before calling the capability-gated PostgreSQL function. `ReadSoulEventsAsync` uses only the capability-gated Soul query and rejects sequence gaps, cross-Soul rows, non-canonical payloads, payload-hash mismatches, and broken chain hashes. `CreateProduction` intentionally returns `WAITING_EXTERNAL` until real upstream authorities are pinned; test roots are internal only.
