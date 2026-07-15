#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
required_version="$(tr -d '[:space:]' < "$repo_root/.powershell-version")"

pwsh_executable="${DPS_PWSH:-}"
if [[ -z "$pwsh_executable" ]] && command -v pwsh >/dev/null 2>&1; then
  pwsh_executable="$(command -v pwsh)"
fi
if [[ -z "$pwsh_executable" ]] && [[ -x "$HOME/.dotnet/tools/pwsh" ]]; then
  pwsh_executable="$HOME/.dotnet/tools/pwsh"
fi
if [[ -z "$pwsh_executable" ]] || [[ ! -x "$pwsh_executable" ]]; then
  echo "Pinned PowerShell $required_version was not found." >&2
  exit 127
fi

if [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
  export PATH="$HOME/.dotnet:$PATH"
fi

actual_version="$($pwsh_executable -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString()')"
if [[ "$actual_version" != "$required_version" ]]; then
  echo "Expected PowerShell $required_version, found $actual_version." >&2
  exit 2
fi

cd "$repo_root"
exec "$pwsh_executable" "$@"
