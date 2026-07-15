# Provided contracts

Only this module may change schemas owned here. Additive changes stay within a major; breaking changes create a new major and follow the compatibility rollout sequence.

v1 files are frozen quarantine identifiers and must not be edited or emitted. v2 is a breaking major with a bounded untrusted append request, bounded canonical bytes, bounded/unique interest signals, signed-command event identity (`event_id == command_id`), authority audit bindings, per-Soul sequence and chain fields, and exact outbox payload identity. Constructing an append request is never equivalent to holding a capability.
