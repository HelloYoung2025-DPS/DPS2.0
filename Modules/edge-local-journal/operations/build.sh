#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/edge-local-journal/src/Dps.EdgeLocalJournal.csproj --locked-mode
bash scripts/dotnet-pinned.sh build Modules/edge-local-journal/src/Dps.EdgeLocalJournal.csproj --configuration Release --no-restore
