# Migrations

`001_build_identity_claims.sql` creates the append-only PostgreSQL build-ID registry used before artifact publication. The migration must run with a dedicated migration identity in a fresh database namespace; it deliberately rejects adoption of a pre-existing `factory_artifact` schema. Runtime receives only `USAGE` on the schema and `EXECUTE` on `claim_build_identity(...)`, never table or DDL privileges.

The table binds each `build_id` to the exact validated request, trusted merge decision, artifact, source tree, module version, and integration commit. Exact retries return true; a different claim returns false. UPDATE, DELETE, and TRUNCATE are rejected by both ACL and triggers. Artifact payload metadata remains immutable and digest-addressed. Store layout changes require additive readers and a separately signed Release BOM.
