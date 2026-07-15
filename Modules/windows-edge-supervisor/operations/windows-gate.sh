#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
: "${DPS_EDGE_WINDOWS_GATE_CONFIG:?DPS_EDGE_WINDOWS_GATE_CONFIG must name the declarative Windows gate configuration}"
: "${DPS_EDGE_WINDOWS_GATE_CONFIG_SHA256:?process-bound configuration digest is required}"
: "${DPS_EDGE_RELEASE_BOM_SHA256:?process-bound Release BOM digest is required}"
: "${DPS_EDGE_PROTECTED_POLICY_SHA256:?process-bound protected policy digest is required}"
: "${DPS_EDGE_WINDOWS_EVIDENCE_TRUST_STORE_FINGERPRINT:?process-bound trust-store fingerprint is required}"
: "${DPS_EDGE_HOST_ID:?process-bound host identity is required}"
: "${DPS_EDGE_SERVER_KEY_ID:?process-bound bridge server key identity is required}"
bash scripts/dotnet-pinned.sh restore Modules/windows-edge-supervisor/src/Dps.WindowsEdgeSupervisor.csproj --locked-mode
bash scripts/dotnet-pinned.sh run --project Modules/windows-edge-supervisor/src/Dps.WindowsEdgeSupervisor.csproj --configuration Release --no-restore -- \
  --windows-gate --config "$DPS_EDGE_WINDOWS_GATE_CONFIG"
