#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
required_version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1], encoding="utf-8"))["sdk"]["version"])' "$repo_root/global.json")"

dotnet_executable="${DPS_DOTNET:-}"
if [[ -z "$dotnet_executable" ]] && command -v dotnet >/dev/null 2>&1; then
  dotnet_executable="$(command -v dotnet)"
fi
if [[ -z "$dotnet_executable" ]] && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  dotnet_executable="$HOME/.dotnet/dotnet"
fi
if [[ -z "$dotnet_executable" ]] || [[ ! -x "$dotnet_executable" ]]; then
  echo "Pinned .NET SDK $required_version was not found." >&2
  exit 127
fi

dotnet_root="$(cd "$(dirname "$dotnet_executable")" && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$dotnet_root}"
export PATH="$dotnet_root:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
export TESTINGPLATFORM_TELEMETRY_OPTOUT="${TESTINGPLATFORM_TELEMETRY_OPTOUT:-1}"
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE="${DOTNET_SKIP_FIRST_TIME_EXPERIENCE:-1}"
export DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="${DOTNET_ADD_GLOBAL_TOOLS_TO_PATH:-0}"
export DOTNET_GENERATE_ASPNET_CERTIFICATE="${DOTNET_GENERATE_ASPNET_CERTIFICATE:-false}"
export DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE="${DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE:-true}"
export DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER="${DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER:-1}"
export MSBUILDDISABLENODEREUSE="${MSBUILDDISABLENODEREUSE:-1}"
export MSBUILDUSESERVER="${MSBUILDUSESERVER:-0}"
export MSBuildEnableWorkloadResolver="${MSBuildEnableWorkloadResolver:-false}"
export RestoreUseStaticGraphEvaluation="${RestoreUseStaticGraphEvaluation:-true}"

if [[ "$(uname -s)" == "Darwin" ]]; then
  export DOTNET_NUGET_SIGNATURE_VERIFICATION="${DOTNET_NUGET_SIGNATURE_VERIFICATION:-false}"
fi

actual_version="$($dotnet_executable --version)"
if [[ "$actual_version" != "$required_version" ]]; then
  echo "Expected .NET SDK $required_version, found $actual_version." >&2
  exit 2
fi

cd "$repo_root"
exec "$dotnet_executable" "$@"
