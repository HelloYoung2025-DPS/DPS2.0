# Changelog

## Unreleased

- Aligned the legacy bridge key identifier with the modern SPKI SHA-256 fingerprint while retaining the explicitly declared RSA PKCS#1 SHA-256 directive-proof algorithm.
- Bound directive idempotency and privacy scope to the originating poll; ACK/WAIT directives now reject every command field.
- Bounded loopback response bytes before JSON deserialization and peer authentication.

## 0.1.0 - Proposed

- Added a C# 5 compatible fixed-loopback JSON bridge contract and fail-closed validation design.
- Added a default-deny peer-proof verifier binding pinned public-key ID, request nonce, canonical timestamp, full directive-body digest, and RSA signature; replay, rogue-key, body-tamper, and undeclared-field paths fail closed.
- Windows, ZennoDroid, .NET Framework, CodeDom, GAC, DLL, ADB, and bridge ABI capabilities remain `WAITING_EXTERNAL` until probed.
- Added required Draft 2020-12 schema cases and a linked exact-source C# wire suite that serializes the production `BridgeExchange` DTO against the owned envelope; neither suite is Windows or device evidence.
- Hardened the authentication simulation to select exactly `Category=SecuritySimulation`, require two tests, and fail on skips while retaining `EvidenceKind=SIMULATION`.
- Added canonical UTC, strict privacy, exact authentication field, and canonical Base64 runtime checks while retaining C# 5 compatibility; owned and consumed provider corpora now exercise terminal-newline, year-zero, leap-second, offset, and identity attacks.
