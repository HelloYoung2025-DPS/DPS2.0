#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/zenno-bridge/tests/Dps.ZennoBridge.AuthSimulation.Tests.csproj --locked-mode
bash scripts/dotnet-pinned.sh test Modules/zenno-bridge/tests/Dps.ZennoBridge.AuthSimulation.Tests.csproj --configuration Release --no-restore -- \
  --filter-trait Category=SecuritySimulation \
  --minimum-expected-tests 4 \
  --fail-skips on
