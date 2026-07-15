# SessionRunner strangler and F5 P0 containment baseline

This directory freezes the five externally invoked declarations in `Modules/SessionRunner.cs`, records the exact reviewed F5 P0 fail-closed bytes, and defines a versioned Golden Trace format for later capture on the target Windows host. The P0 containment intentionally changes behavior: production SessionRunner commands remain disabled until a modern authorization/lease/receipt bridge is implemented and independently approved.

An ordinary candidate checkout intentionally cannot create its own passing proof. After an independent release authority has installed a read-only provider anchor owned by a different system identity, run the comparison from the repository root in isolated Python mode:

```text
DPS_LEGACY_BASELINE_ANCHOR=/protected/provider/legacy-baseline-anchor.json \
.venv/bin/python -I Modules/legacy-runtime-adapter/operations/strangler/verify_sessionrunner_baseline.py --root .
```

Without that provider, the command returns `WAITING_EXTERNAL` and exits 3. A repository-local anchor, a same-identity file made temporarily read-only with chmod, a writable provider directory, symlinked path, arbitrary issuer/audience, or record whose derived ID does not bind every field fails closed.

The provider record binds the real Git commit, tree and immediate parent list; the exact 79-path inventory and canonical digest; the original four approved repairs; the separate eight containment repairs covering all seven production wrappers plus `SmartOrchestrator`; the explicit approved-repair policy; the verifier, module rules, schemas, baseline data, snapshot and traces; all required adversarial/static test hashes; and protected minimum test-method counts of 21, 9, and 7. It therefore detects baseline/manifest/rule/test swaps, five-entry shrink attacks, `.CS` injection, component collisions, case-aliased legacy roots or `Modules` children, linked-directory hiding, deletion, stable-byte drift, changed approved bytes, and candidate-created commits. The command must run with the independent trusted-runner-created CPython 3.12.13 `.venv` shown above, and evidence must preserve its interpreter SHA-256, exact argv, environment, raw output, and exit status. The tool never rewrites source or generates a provider anchor.

The two files under `golden-traces/` are synthetic format examples. Their expected state contains `ERROR_BRIDGE_REQUIRED` and cannot route to `GoodEnd`. They were not captured from Windows, ZennoDroid, ADB, GBrain, or a phone and cannot satisfy integration, Windows, device, canary, or scale gates. Real Golden Traces must be captured later from an authorized non-production Windows/ZennoDroid environment and stored as separately reviewed evidence.

The current dynamic CodeDom/reflection loader remains only as unreachable migration history after the compile-time gate. It is not an acceptable future bridge. Re-enablement requires a separately reviewed fixed ABI and signed-BOM loader that rejects variable roots, source globs, `Extensions`, peer-file discovery, first-exported-type selection, reparse points, UNC paths, and unlisted files.
