# DPS Git Workflow (Simple)

This guide is for daily use with minimal Git knowledge.

## 1) First-time setup (already done once per project)

```bash
git init
```

Set your identity once on this machine:

```bash
git config --global user.name "Your Name"
git config --global user.email "you@example.com"
```

## 2) API key safety rule

- Do not commit `Config/AIConfig.json`.
- Use `Config/AIConfig.template.json` as the shared format.
- Keep real keys in local-only `Config/AIConfig.json`.

Create your local file from template:

```bash
cp Config/AIConfig.template.json Config/AIConfig.json
```

PowerShell equivalent:

```powershell
Copy-Item Config\AIConfig.template.json Config\AIConfig.json
```

## 3) Daily workflow

Check status:

```bash
git status
```

Review the diff, then stage only the files that belong to the change:

```bash
git diff --check
git add path/to/file1 path/to/file2
git diff --cached
git commit -m "feat: short description"
```

Do not use `git add .` in release automation. It can silently include generated files, local secrets, unrelated edits, or large deletion sets.

## 4) Versioning strategy

Use semantic versioning:

- `vX.Y.Z`
- `X` (major): architecture/breaking changes
- `Y` (minor): major feature upgrades, backward compatible
- `Z` (patch): bug fixes and small improvements

Examples:

- `v4.5.1` -> bug fix
- `v4.6.0` -> major feature upgrade
- `v5.0.0` -> breaking architecture change

## 5) Release steps for major upgrades

1. Update `CHANGELOG.md`.
2. Update `Docs/Architecture/`, `Docs/TechManual_技术手册.md`, and external contracts if architecture changed.
3. Commit all changes.
4. Confirm Hosted CI and the required Windows ZennoDroid gate passed.
5. Create tag.

Commands:

```bash
git add CHANGELOG.md Docs/ path/to/changed/source
git diff --cached
git commit -m "release: v5.0.0 major upgrade"
git tag -a v5.0.0 -m "Major upgrade v5.0.0"
```

If using remote:

```bash
git push origin main --tags
```

## 5.1) Release helper

The helper is a validation-only preflight. It requires a completely clean checkout whose `HEAD` exactly matches the signed candidate BOM, then runs the unique Phase 0 gate and the candidate BOM validator. It does not create commits, tags, signatures, deployments, approvals, or production routing changes.

```bash
./scripts/release.sh \
  --bundle-root /absolute/path/to/candidate-bundle \
  --bom release-bom.json \
  --previous-bom previous-stable-bom.json \
  --native-stop-trust-receipt /absolute/path/to/native-stop-trust-receipt.json \
  --schema-sha256 <lowercase-sha256>
```

The script will:

- Refuse any tracked, staged, or untracked worktree difference
- Require the BOM `integration_commit` to equal the current `HEAD`
- Run `Tools/ci/run_phase0_gate.py`
- Validate the exact artifacts, SBOM, provenance, compatibility data, previous stable BOM, and fixed deployed trust anchor
- Exit without changing Git or any deployment state

An authorized human or separately deployed release controller performs any later commit, tag, signature, publication, or rollout step under its own approval and credentials. Passing this helper is necessary preflight evidence, not release authorization.

## 6) Useful commands

View history:

```bash
git log --oneline --decorate --graph -n 20
```

View tags:

```bash
git tag
```

Show one release:

```bash
git show v5.0.0
```
