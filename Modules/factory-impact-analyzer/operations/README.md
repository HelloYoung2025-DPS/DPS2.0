# Operations

`trusted-impact-policy.v2.json` is the stable-owned, non-production policy template. It fixes roles, checks, change-kind risk floors, risk/stage combinations, and permits only `development` and zero-side-effect `shadow`. The repository file cannot authorize canary, rolling, soaking, a release, or any product/repository side effect.

The fixed composition root must create concrete Intent, Receipt, and Policy verifier ports and authorities. The local HMAC route demonstrates process binding only. The production route remains `WAITING_EXTERNAL` until an independently verifiable signature, mTLS plus durable receipt lookup, or equivalent portable trust provider binds exact canonical bytes, issuer, audience, receipt/nonce/generation, expiry, and revocation/currentness.

Before and after analysis, verify the Receipt's bound files, baseline, Git HEAD, index, status, diff material, exact-major declarations, scope, write boundary, and policy. Any mismatch or race stops without returning a plan. `shadow_side_effect_count` must remain zero.

## Kill switch and rollback

- `factory_disable_impact_analysis` stops accepting new Intent/Receipt pairs.
- Discard all unexecuted candidate plans after a trust, currentness, policy, or compatibility failure.
- Roll back the Intake v2 → Resolver v2 → Impact v2 compatibility group to the previous signed BOM within five minutes.
- Never roll back by reactivating v1 runtime contracts; v1 remains quarantine-only.
- No downstream Worktree Manager or Host may consume plan v2 until reciprocal exact-major declarations and portable trust are frozen and independently audited.
