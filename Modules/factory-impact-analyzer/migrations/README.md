# Migrations

Version 0.2.0 introduces no data-store migration. It is a contract and trust-boundary migration:

1. Keep v1 Intent, Receipt, and plan bytes frozen and quarantine-only.
2. Deploy Intake v2 and Resolver v2 before Impact v2.
3. Accept only sealed v2 capabilities and emit only non-authorizing plan v2.
4. Freeze reciprocal Worktree Manager/Host consumers and portable trust before any runtime cutover.
5. Do not reinterpret or rewrite stored v1 payloads as v2.

If cutover fails, discard unexecuted v2 plans and roll back the whole compatibility group. Never reactivate v1 as an operational fallback.
