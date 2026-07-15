# legacy-runtime-adapter migrations

Use expand, migrate, contract across separate releases. The current and previous compatible versions must both run after the expand step. Destructive contraction requires a later signed BOM, backup evidence, and an explicit rollback or forward-fix decision.
