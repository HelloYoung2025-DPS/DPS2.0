$ErrorActionPreference = "Stop"

function Wait-External([string]$Reason) {
    @{ status = "WAITING_EXTERNAL"; verification_claim = $null; reason = $Reason } | ConvertTo-Json -Compress
    exit 2
}

if (-not $IsWindows) { Wait-External "Windows host is required" }
if (-not (Get-Command pwsh -ErrorAction SilentlyContinue)) { Wait-External "PowerShell is required" }
if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { Wait-External "ADB is required" }
if ($PSVersionTable.PSVersion.ToString() -ne "7.6.2") { Wait-External "Pinned PowerShell 7.6.2 is required" }
$adbVersion = (& adb version 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $adbVersion -notmatch "(?m)^Version 37\.0\.0-14910828\r?$") {
    Wait-External "Pinned Android Platform Tools 37.0.0-14910828 are required"
}

$zenno = Get-Process | Where-Object { $_.ProcessName -like "*ZennoDroid*" } | Select-Object -First 1
if ($null -eq $zenno) { Wait-External "ZennoDroid process is required" }

$evidencePath = $env:DPS_WINDOWS_EDGE_EVIDENCE
if ([string]::IsNullOrWhiteSpace($evidencePath) -or -not (Test-Path -LiteralPath $evidencePath)) {
    Wait-External "Signed Windows Edge evidence file is required"
}

$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
$required = @(
    "is_windows", "powershell_version", "zennodroid_version", "zennodroid_pid_before", "zennodroid_pid_after",
    "zennodroid_started_at_before", "zennodroid_started_at_after", "dotnet_framework_version", "csharp_version",
    "codedom_supported", "gac_supported", "dll_load_supported", "adb_version", "authorized_device_count",
    "bridge_abi", "loopback_port", "timeout_ms", "error_semantics", "peer_auth_mode", "peer_auth_key_id", "connection_continuity_seconds",
    "connection_drops", "ab_switch_count", "soak_seconds"
)
foreach ($name in $required) {
    if ($null -eq $evidence.$name) { Wait-External "Missing capability: $name" }
}

if ($evidence.is_windows -ne $true) { throw "Evidence is not from Windows" }
if ($evidence.powershell_version -ne "7.6.2") { throw "PowerShell evidence version mismatch" }
if ($evidence.adb_version -ne "37.0.0-14910828") { throw "ADB evidence version mismatch" }
if ($evidence.zennodroid_pid_before -ne $zenno.Id -or $evidence.zennodroid_pid_after -ne $zenno.Id) { throw "ZennoDroid PID changed" }
if ($evidence.zennodroid_started_at_before -ne $evidence.zennodroid_started_at_after) { throw "ZennoDroid start time changed" }
try {
    $evidenceStart = [DateTimeOffset]::Parse([string]$evidence.zennodroid_started_at_after).ToUniversalTime()
    $actualStart = [DateTimeOffset]($zenno.StartTime.ToUniversalTime())
} catch {
    throw "ZennoDroid start time evidence is invalid"
}
if ($evidenceStart.UtcDateTime.Ticks -ne $actualStart.UtcDateTime.Ticks) { throw "ZennoDroid start time does not match the running process" }
try {
    $actualZennoVersion = $zenno.MainModule.FileVersionInfo.FileVersion
} catch {
    Wait-External "ZennoDroid version could not be read from the running process"
}
if ([string]::IsNullOrWhiteSpace($actualZennoVersion) -or $evidence.zennodroid_version -ne $actualZennoVersion) {
    throw "ZennoDroid version evidence mismatch"
}
if ($evidence.connection_drops -ne 0) { throw "Bridge connection continuity failed" }
if ($evidence.codedom_supported -ne $true -or $evidence.gac_supported -ne $true -or $evidence.dll_load_supported -ne $true) {
    Wait-External "CodeDom, GAC, and DLL-load probes must pass"
}
$adbDevices = (& adb devices 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { Wait-External "ADB device enumeration failed" }
$authorizedDevices = @($adbDevices -split "`r?`n" | Where-Object { $_ -match "`tdevice$" }).Count
if ($authorizedDevices -lt 1 -or $evidence.authorized_device_count -ne $authorizedDevices) {
    Wait-External "Authorized ADB device evidence does not match the host"
}
if ($evidence.loopback_port -ne 28741) { throw "Fixed bridge loopback port mismatch" }
if ($evidence.peer_auth_mode -ne "WINDOWS_IDENTITY_AND_PINNED_RSA" -or $evidence.peer_auth_key_id -notmatch "^sha256_[a-f0-9]{64}$") {
    Wait-External "Pinned peer authentication evidence is required"
}
if ($evidence.ab_switch_count -lt 100) { Wait-External "One hundred real A/B switches are required" }
if ($evidence.soak_seconds -lt 86400 -or $evidence.connection_continuity_seconds -lt 86400) { Wait-External "Twenty-four-hour soak and continuity are required" }

Wait-External "Cryptographic evidence attestation must be verified by the supervisor against the trusted Release BOM key"
