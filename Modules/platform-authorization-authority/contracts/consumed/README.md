# Consumed inputs

This module declares no consumed DPS module contract. Raw external-platform proof is untrusted input to the authority verifier boundary, not a DPS authorization fact and not a statement authored by the external platform merely because DPS received it.

Source validates one composition-bound proof format through an explicitly trusted verifier before normalization or signing. The issue request cannot supply a Release BOM, generation, signer, key, verifier, or receipt store. Raw email addresses, phone numbers, platform login identifiers, cookies, bearer tokens, credentials, and raw proof bytes must never enter `platform.account.authorization.evidence/v1`, logs, screenshots, prompts, or durable exact-envelope receipts.
