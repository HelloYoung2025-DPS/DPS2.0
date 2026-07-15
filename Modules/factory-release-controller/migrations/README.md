# Migrations

This module owns no database tables. Release state may be rebuilt from the append-only `factory-evidence-ledger` contract only when its full v2 semantics and an independently authenticated ledger head agree. A local/recomputed hash chain is not a recovery authority. The provider is currently `WAITING_EXTERNAL`, so public recovery is disabled. Any future owned persistence requires an additive migration and a manifest ownership update before code may use it.
