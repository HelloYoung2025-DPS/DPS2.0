#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/zenno-bridge/src/Dps.ZennoBridge.csproj --locked-mode
bash scripts/dotnet-pinned.sh build Modules/zenno-bridge/src/Dps.ZennoBridge.csproj --configuration Release --no-restore
