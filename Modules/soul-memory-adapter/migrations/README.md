# Soul Memory Adapter migrations

This module owns no database in version `0.2.0`, so there is no runtime data migration. Source binding pages are operator-provisioned external GBrain records and are verified exactly before use; they are not created through a hidden repository migration.

A future durable receipt store must use expand-migrate-contract and retain only scoped identifiers, checksums, revisions, redacted evidence references, and verification timestamps. It must never persist OAuth client secrets, access tokens, raw email or phone aliases, or unrestricted GBrain page bodies. A future GBrain page-schema migration must be additive, Soul-scoped, reversible through the prior signed BOM, and must retain old/new exact-read compatibility until the compatibility matrix authorizes retirement.
