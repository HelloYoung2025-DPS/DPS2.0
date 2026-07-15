#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/windows-edge-worker/src/Dps.WindowsEdgeWorker.csproj --locked-mode
bash scripts/dotnet-pinned.sh build Modules/windows-edge-worker/src/Dps.WindowsEdgeWorker.csproj --configuration Release --no-restore
