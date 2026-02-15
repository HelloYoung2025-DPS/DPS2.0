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

## 3) Daily workflow

Check status:

```bash
git status
```

Stage and commit:

```bash
git add .
git commit -m "feat: short description"
```

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
2. Update `SYSTEM_BIBLE.md` if architecture changed.
3. Commit all changes.
4. Create tag.

Commands:

```bash
git add .
git commit -m "release: v5.0.0 major upgrade"
git tag -a v5.0.0 -m "Major upgrade v5.0.0"
```

If using remote:

```bash
git push origin main --tags
```

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
