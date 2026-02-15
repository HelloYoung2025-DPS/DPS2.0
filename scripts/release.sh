#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-}"
COMMIT_MSG="${2:-}"

if [[ -z "$VERSION" ]]; then
  echo "Usage: ./scripts/release.sh vX.Y.Z [commit message]"
  exit 1
fi

if [[ ! "$VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Invalid version: $VERSION"
  echo "Expected format: vX.Y.Z (example: v4.6.0)"
  exit 1
fi

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Not inside a Git repository."
  exit 1
fi

if git rev-parse "$VERSION" >/dev/null 2>&1; then
  echo "Tag already exists: $VERSION"
  exit 1
fi

if git diff --quiet && git diff --cached --quiet; then
  echo "No changes detected. Nothing to release."
  exit 1
fi

if [[ -z "$COMMIT_MSG" ]]; then
  COMMIT_MSG="release: $VERSION"
fi

echo "Releasing $VERSION ..."
git add .
git commit -m "$COMMIT_MSG"
git tag -a "$VERSION" -m "Release $VERSION"

echo
echo "Release complete:"
echo "- Commit: $(git rev-parse --short HEAD)"
echo "- Tag:    $VERSION"
echo
echo "If you have a remote, push with:"
echo "git push origin main --tags"
