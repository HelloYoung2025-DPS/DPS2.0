# evidence-service source

Production source belongs below this directory. Other modules may depend only on versioned provided contracts, never internal implementation types.

Evidence submission is fail-closed: an untrusted receipt and raw artifacts become storable only after a configured runner public key verifies an external ECDSA attestation. The production assembly contains no signing/private-key path. PostgreSQL persists the canonical receipt and source-receipt bytes atomically, then requires exact Soul/device/account scope and digest/signature replay verification when reading them after restart.
