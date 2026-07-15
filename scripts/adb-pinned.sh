#!/usr/bin/env bash
set -euo pipefail

required_version="37.0.0-14910828"
adb_executable="${DPS_ADB:-}"
if [[ -z "$adb_executable" ]] && command -v adb >/dev/null 2>&1; then
  adb_executable="$(command -v adb)"
fi
if [[ -z "$adb_executable" ]] && [[ -x "$HOME/.local/bin/adb" ]]; then
  adb_executable="$HOME/.local/bin/adb"
fi
if [[ -z "$adb_executable" ]] || [[ ! -x "$adb_executable" ]]; then
  echo "Pinned Android Platform Tools $required_version were not found." >&2
  exit 127
fi

version_output="$($adb_executable version)"
if [[ "$version_output" != *"Version $required_version"* ]]; then
  echo "Expected adb $required_version." >&2
  echo "$version_output" >&2
  exit 2
fi

exec "$adb_executable" "$@"
