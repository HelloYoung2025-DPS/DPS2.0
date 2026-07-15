#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "Usage: $0 <Unit|Contract|Integration>" >&2
  exit 64
fi

case "$1" in
  Unit|Contract|Integration)
    suite_category="$1"
    ;;
  *)
    echo "Unknown suite category: $1" >&2
    exit 64
    ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
bash scripts/dotnet-pinned.sh restore Modules/windows-edge-worker/tests/Dps.WindowsEdgeWorker.Tests.csproj --locked-mode
bash scripts/dotnet-pinned.sh test Modules/windows-edge-worker/tests/Dps.WindowsEdgeWorker.Tests.csproj --configuration Release --no-restore -- \
  --filter-trait "Category=$suite_category" \
  --minimum-expected-tests 1 \
  --fail-skips on
