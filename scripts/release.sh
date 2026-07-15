#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage:
  ./scripts/release.sh \
    --bundle-root PATH \
    --bom PATH \
    --previous-bom PATH \
    --schema-sha256 HEX \
    [--phase0-evidence PATH]

This command validates a release candidate only. It never commits, tags,
pushes, signs, deploys, approves, or changes production routing.
When --phase0-evidence is omitted, the Phase 0 runner creates a unique,
fail-closed run-id directory under Reports/ci/phase0-runs.
EOF
}

bundle_root=""
bom_path=""
previous_bom_path=""
schema_sha256=""
phase0_evidence=""

while (($#)); do
  case "$1" in
    --bundle-root)
      bundle_root="${2:-}"
      shift 2
      ;;
    --bom)
      bom_path="${2:-}"
      shift 2
      ;;
    --previous-bom)
      previous_bom_path="${2:-}"
      shift 2
      ;;
    --schema-sha256)
      schema_sha256="${2:-}"
      shift 2
      ;;
    --phase0-evidence)
      phase0_evidence="${2:-}"
      shift 2
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown release validation argument: $1" >&2
      usage >&2
      exit 64
      ;;
  esac
done

for value in \
  "$bundle_root" \
  "$bom_path" \
  "$previous_bom_path" \
  "$schema_sha256"; do
  if [[ -z "$value" ]]; then
    echo "All required release validation arguments must be supplied." >&2
    usage >&2
    exit 64
  fi
done

if [[ ! "$schema_sha256" =~ ^[0-9a-f]{64}$ ]]; then
  echo "--schema-sha256 must be a lowercase SHA-256 digest." >&2
  exit 64
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if ! git rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  echo "Not inside a Git repository." >&2
  exit 1
fi
if [[ -n "$(git status --porcelain=v1 --untracked-files=all)" ]]; then
  echo "Release validation requires a clean worktree, index, and untracked-file set." >&2
  exit 1
fi

python_executable=""
if [[ -x "$repo_root/.venv/bin/python" ]]; then
  python_executable="$repo_root/.venv/bin/python"
elif command -v python3.12 >/dev/null 2>&1; then
  python_executable="$(command -v python3.12)"
else
  echo "Pinned Python 3.12.13 is required." >&2
  exit 127
fi

actual_python="$($python_executable -c 'import platform; print(platform.python_version())')"
if [[ "$actual_python" != "3.12.13" ]]; then
  echo "Expected Python 3.12.13, found $actual_python." >&2
  exit 2
fi

head_commit="$(git rev-parse HEAD^{commit})"
bom_commit="$($python_executable - "$bom_path" <<'PY'
import json
import re
import sys
from pathlib import Path

try:
    value = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
except Exception as exc:
    raise SystemExit("Cannot read candidate Release BOM: {0}".format(exc))
commit = value.get("integration_commit") if isinstance(value, dict) else None
if not isinstance(commit, str) or re.fullmatch(r"[0-9a-f]{40}", commit) is None:
    raise SystemExit("Candidate Release BOM has no valid integration_commit")
print(commit)
PY
)"
if [[ "$bom_commit" != "$head_commit" ]]; then
  echo "Candidate Release BOM integration_commit does not match current HEAD." >&2
  exit 1
fi

phase0_arguments=(--base "$head_commit")
if [[ -n "$phase0_evidence" ]]; then
  phase0_arguments+=(--evidence "$phase0_evidence")
fi
"$python_executable" Tools/ci/run_phase0_gate.py "${phase0_arguments[@]}"

PYTHONPATH="$repo_root/Modules/factory-release-controller/src" \
  "$python_executable" \
  Modules/factory-release-controller/src/candidate_bom_validator.py \
  --repo-root "$repo_root" \
  --bundle-root "$bundle_root" \
  --bom "$bom_path" \
  --previous-bom "$previous_bom_path" \
  --schema-sha256 "$schema_sha256"

echo "Release candidate validation passed for $head_commit."
echo "No commit, tag, signature, deployment, approval, or production change was performed."
