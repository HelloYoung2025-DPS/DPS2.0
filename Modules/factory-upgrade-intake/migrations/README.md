# Migrations

No database or migration is owned by this module. Intent revisions are immutable. Durable idempotency, requester/approval nonce conflict detection, and event recovery belong to the Factory Control Plane's externally managed PostgreSQL transaction boundary. Until that dependency and its integration evidence exist, runtime persistence is `WAITING_EXTERNAL` and this module remains not release eligible.
