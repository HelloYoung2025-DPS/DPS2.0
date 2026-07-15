# Consumed contracts

The module consumes `platform.account.authorization.evidence/v1` from the unique contract owner and producer, `platform-authorization-authority`, over the declared in-process API boundary. The authority verifies and normalizes untrusted external-platform proof before issuing the internal DPS envelope; an external platform is never represented as the contract producer. Production verifies the compiled P-256 public root and exact issuer key ID. The private signer is not present in this repository, application configuration, or account-registry tests.

The envelope contains only canonical scope, alias HMAC digest/key metadata, decision status/revision, Release BOM identity, bounded validity, and a signature. It never contains a raw email address, phone number, platform login identifier, credential, bearer token, or raw external proof. The consumer does not issue, rewrite, or re-sign the envelope.
