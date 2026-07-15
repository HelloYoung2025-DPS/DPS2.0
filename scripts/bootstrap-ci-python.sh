#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PYTHON="${DPS_PYTHON:-python3}"
EXPECTED_VERSION="$(tr -d '[:space:]' < "$ROOT/.python-version")"

actual_version="$($PYTHON -c 'import platform; print(platform.python_version())')"
if [[ "$actual_version" != "$EXPECTED_VERSION" ]]; then
  echo "Expected Python $EXPECTED_VERSION, found $actual_version at $PYTHON" >&2
  exit 2
fi

"$PYTHON" -m venv "$ROOT/.venv"
"$ROOT/.venv/bin/python" -m pip install \
  --disable-pip-version-check \
  --require-hashes \
  --requirement "$ROOT/requirements-ci.txt"

"$ROOT/.venv/bin/python" -c \
  'import jsonschema, yaml; print("Python CI dependencies are ready")'
