# Repository protection and Factory authority

> **Document status**: Proposed
>
> **Evidence status**: `NONE`

Repository files can describe and test governance, but they cannot prove that the same writer is unable to change the policy, validator, workflow, tests, and implementation together. DPS therefore treats GitHub rules and the deployed Factory trust root as external control-plane evidence.

## Local Candidate Runner authority

The local Candidate Runner is a non-issuer. Whether it runs against a dirty workspace diagnostic or a clean committed candidate, its evidence must keep `candidate_verification_level=null`, `verification_level=null`, `signed=false`, and `formal_evidence_eligible=false`. It can only prepare candidate material for an external trusted reviewer; it cannot authorize itself.

## Required repository rules

The default branch must use a GitHub ruleset or equivalent controls with all of the following settings:

- Changes enter through pull requests; direct pushes and force pushes are disabled.
- At least two approvals from distinct human identities are required for governance, Factory, CI, trust-policy, evidence, and release paths.
- Code-owner review is required, stale approvals are dismissed, and the last pusher cannot satisfy the final approval alone.
- Administrators and automation identities cannot bypass the protected-path rules during ordinary releases.
- The required verification workflow is owned by the organization or loaded from the protected default branch. A candidate branch must not be able to replace the workflow that judges itself.
- Required checks include the unique Phase 0 gate and, when applicable, Contract and Integration candidate results. Formal Contract or Integration levels must be issued by an independent trusted authority outside the candidate-controlled workflow; a local Candidate PASS is not separately issued evidence.
- Merge commits or the selected merge strategy are tested at the exact final commit; green results from separate branches cannot be concatenated.
- Branch deletion, tag creation, release publication, and deployment use identities separate from the implementation writer.

The sensitive paths are at least:

```text
AGENTS.md
.github/**
governance/**
Tools/ci/**
Tools/verification/**
scripts/release.sh
Modules/factory-*/**
```

`.github/CODEOWNERS` establishes the current repository owner as a baseline reviewer. A personal repository with only that identity does not yet satisfy the two-person rule; add a distinct trusted reviewer before treating role separation as verified.

## Deployed trust root

- The running Trusted Runner and Release Controller use the previous stable Factory artifact, not code from the candidate being judged.
- Public keys, policy digests, database credentials, and signing authority are supplied by the deployed environment or an external evidence issuer.
- Candidate code may propose a new policy or key but cannot activate it in the same release.
- Private signing keys never enter this repository, CI artifacts, logs, prompts, or GBrain.
- Revocation and key rotation require an independently approved operation and preserve an immutable audit record.

## Evidence required to leave Proposed

Capture the repository/ruleset identifier, protected branch, rule revision, required checks, approval identities, bypass list, default-branch workflow digest, Candidate Policy digest, complete Instruction Receipt digest, exact candidate commit, previous stable Trusted Runner/Factory artifact digest, trust-policy digest, independent issuance event, and independent release-approval event. Evidence must be collected without tokens or secrets and signed by an issuer that did not implement the candidate.

Until those controls are configured and independently verified, Factory role separation remains `proposed`/`WAITING_EXTERNAL`; local tests, CODEOWNERS, or a passing candidate workflow cannot upgrade it by themselves.
