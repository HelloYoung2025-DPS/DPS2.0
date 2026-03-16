param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Preflight", "Advance", "Postflight")]
    [string]$Phase,

    [string]$FilePath = "",

    [switch]$ExecuteCommands,

    [switch]$NoStateWrite
)

Set-StrictMode -Version 2
$ErrorActionPreference = "Stop"

function Write-GateInfo($message) {
    Write-Host "[OMO-GATE] " -NoNewline -ForegroundColor Cyan
    Write-Host $message
}

function Fail-Gate($message) {
    throw "[OMO-GATE] " + $message
}

function Strip-Wrappers($value) {
    if ($null -eq $value) {
        return ""
    }

    return $value.Trim().Trim(@([char]96, [char]34))
}

function Get-RepoRoot() {
    $root = Join-Path $PSScriptRoot "..\.."
    return [System.IO.Path]::GetFullPath($root)
}

function Read-FileText($path) {
    if (-not (Test-Path -LiteralPath $path)) {
        Fail-Gate ("文件不存在: " + $path)
    }

    return [System.IO.File]::ReadAllText($path)
}

function Get-SectionLines($path, $heading) {
    $lines = [System.IO.File]::ReadAllLines($path)
    $startIndex = -1
    $endIndex = $lines.Length
    $i = 0

    while ($i -lt $lines.Length) {
        if ($lines[$i].Trim() -eq $heading) {
            $startIndex = $i + 1
            break
        }
        $i++
    }

    if ($startIndex -lt 0) {
        Fail-Gate ("缺少节: " + $heading + " @ " + $path)
    }

    $i = $startIndex
    while ($i -lt $lines.Length) {
        if ($lines[$i] -match "^##\s+" -or $lines[$i] -match "^#\s+") {
            $endIndex = $i
            break
        }
        $i++
    }

    if ($endIndex -le $startIndex) {
        return @()
    }

    return $lines[$startIndex..($endIndex - 1)]
}

function Get-ListItems($lines) {
    $items = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed -match "^(?:[-*]|\d+\.)\s+(.+?)\s*$") {
            $item = $matches[1].Trim()
            if (-not [string]::IsNullOrEmpty($item)) {
                $items.Add((Strip-Wrappers $item))
            }
        }
    }

    return $items
}

function Get-BoldFieldValue($path, $fieldName) {
    $text = Read-FileText $path
    $pattern = "- \*\*" + [System.Text.RegularExpressions.Regex]::Escape($fieldName) + "\*\*:\s*(.+)"
    $match = [System.Text.RegularExpressions.Regex]::Match($text, $pattern)
    if (-not $match.Success) {
        return ""
    }

    return Strip-Wrappers $match.Groups[1].Value
}

function Normalize-RepoPath($repoRoot, $path) {
    if ([string]::IsNullOrEmpty($path)) {
        return ""
    }

    $clean = (Strip-Wrappers $path).Replace("/", "\")
    if ([System.IO.Path]::IsPathRooted($clean)) {
        $full = [System.IO.Path]::GetFullPath($clean)
    } else {
        $full = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $clean))
    }

    $root = [System.IO.Path]::GetFullPath($repoRoot)
    if ($full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $full.Substring($root.Length).TrimStart('\')
        return $relative.Replace("/", "\")
    }

    return $clean
}

function Assert-RequiredSections($path, $sections) {
    foreach ($section in $sections) {
        [void](Get-SectionLines $path $section)
    }
}

function Get-ModuleTrackerPath($repoRoot, $moduleName) {
    if ([string]::IsNullOrEmpty($moduleName)) {
        return ""
    }

    return Join-Path $repoRoot (".omo\modules\" + $moduleName + ".md")
}

function Load-PlanContext($repoRoot) {
    $planPath = Join-Path $repoRoot ".omo\current-task\plan.md"
    Assert-RequiredSections $planPath @(
        "## 目标层级",
        "## 本次必须修改的文件",
        "## 强制修改顺序",
        "## 强制验证顺序",
        "## 强制运行命令"
    )

    $primaryLayer = Get-BoldFieldValue $planPath "主层级"
    $affectedLayers = Get-BoldFieldValue $planPath "受影响层级"
    $moduleName = Get-BoldFieldValue $planPath "主模块"
    $editOrder = Get-ListItems (Get-SectionLines $planPath "## 强制修改顺序")
    $validationOrder = Get-ListItems (Get-SectionLines $planPath "## 强制验证顺序")
    $runCommands = Get-ListItems (Get-SectionLines $planPath "## 强制运行命令")

    if ([string]::IsNullOrEmpty($primaryLayer)) {
        Fail-Gate "plan.md 缺少主层级"
    }

    if ($editOrder.Count -eq 0) {
        Fail-Gate "plan.md 缺少强制修改顺序列表"
    }

    if ($validationOrder.Count -eq 0) {
        Fail-Gate "plan.md 缺少强制验证顺序列表"
    }

    if ($runCommands.Count -eq 0) {
        Fail-Gate "plan.md 缺少强制运行命令列表"
    }

    if ((Normalize-RepoPath $repoRoot $editOrder[0]) -ne ".omo\current-task\plan.md") {
        Fail-Gate "强制修改顺序的第一项必须是 .omo/current-task/plan.md"
    }

    if ((Normalize-RepoPath $repoRoot $editOrder[$editOrder.Count - 1]) -ne "CHANGELOG.md") {
        Fail-Gate "强制修改顺序的最后一项必须是 CHANGELOG.md"
    }

    $trackerPath = ""
    if ($primaryLayer -eq "L2" -or $primaryLayer -eq "L3" -or $primaryLayer -eq "L4") {
        if ([string]::IsNullOrEmpty($moduleName)) {
            Fail-Gate "L2/L3/L4 任务必须在 plan.md 中声明主模块"
        }

        $trackerPath = Get-ModuleTrackerPath $repoRoot $moduleName
        Assert-RequiredSections $trackerPath @(
            "## 任务头",
            "## 强制文件顺序",
            "## 强制验证顺序",
            "## 强制运行命令"
        )
    }

    return @{
        PlanPath = $planPath
        PrimaryLayer = $primaryLayer
        AffectedLayers = $affectedLayers
        ModuleName = $moduleName
        TrackerPath = $trackerPath
        EditOrder = $editOrder
        ValidationOrder = $validationOrder
        RunCommands = $runCommands
    }
}

function Assert-ProtocolReferences($repoRoot) {
    $targets = @(
        (Join-Path $repoRoot "AGENTS.md"),
        (Join-Path $repoRoot ".omo\layers\EXECUTION_PROTOCOL.md"),
        (Join-Path $repoRoot ".omo\modules\WORKFLOW.md"),
        (Join-Path $repoRoot ".omo.conf")
    )

    $found = $false
    foreach ($target in $targets) {
        $text = Read-FileText $target
        if ($text.IndexOf("Tools/omo_guard/Invoke-OmoGate.ps1", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $text.IndexOf("Tools\\omo_guard\\Invoke-OmoGate.ps1", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $found = $true
        }
    }

    if (-not $found) {
        Fail-Gate "协议文件尚未引用 Tools/omo_guard/Invoke-OmoGate.ps1"
    }
}

function Save-GateState($statePath, $state, $noStateWrite) {
    if ($noStateWrite) {
        Write-GateInfo "NoStateWrite 已启用，跳过 state 写入"
        return
    }

    $json = $state | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($statePath, $json, [System.Text.Encoding]::UTF8)
}

function Load-GateState($statePath, $noStateWrite) {
    if ($noStateWrite) {
        return $null
    }

    if (-not (Test-Path -LiteralPath $statePath)) {
        Fail-Gate "缺少 Gate state，请先运行 Preflight"
    }

    return ConvertFrom-Json (Read-FileText $statePath)
}

function New-StateObject($repoRoot, $context) {
    $files = New-Object System.Collections.Generic.List[object]

    foreach ($item in $context.EditOrder) {
        $normalized = Normalize-RepoPath $repoRoot $item
        $absolute = Join-Path $repoRoot $normalized
        $exists = Test-Path -LiteralPath $absolute
        $lastWrite = ""
        if ($exists) {
            $lastWrite = (Get-Item -LiteralPath $absolute).LastWriteTimeUtc.ToString("o")
        }

        $files.Add(@{
            path = $normalized
            existed_at_preflight = $exists
            last_write_utc = $lastWrite
            advanced = $false
            advanced_at_utc = ""
        })
    }

    return @{
        created_at_utc = (Get-Date).ToUniversalTime().ToString("o")
        primary_layer = $context.PrimaryLayer
        affected_layers = $context.AffectedLayers
        module_name = $context.ModuleName
        current_index = 0
        files = $files
        validation_order = $context.ValidationOrder
        run_commands = $context.RunCommands
        completed_at_utc = ""
    }
}

function Invoke-Preflight($repoRoot, $statePath, $noStateWrite) {
    Assert-ProtocolReferences $repoRoot
    $context = Load-PlanContext $repoRoot
    $state = New-StateObject $repoRoot $context
    Save-GateState $statePath $state $noStateWrite
    Write-GateInfo ("Preflight 通过。主层级: " + $context.PrimaryLayer)
    Write-GateInfo ("下一项应先修改: " + (Normalize-RepoPath $repoRoot $context.EditOrder[0]))
}

function Invoke-Advance($repoRoot, $statePath, $filePath, $noStateWrite) {
    if ($noStateWrite) {
        Fail-Gate "Advance 不支持 NoStateWrite；它必须写入顺序状态"
    }

    if ([string]::IsNullOrEmpty($filePath)) {
        Fail-Gate "Advance 阶段必须提供 -FilePath"
    }

    $state = Load-GateState $statePath $false
    $currentIndex = [int]$state.current_index
    $total = $state.files.Count

    if ($currentIndex -ge $total) {
        Fail-Gate "所有计划文件都已完成，无需再 Advance"
    }

    $expected = Normalize-RepoPath $repoRoot $state.files[$currentIndex].path
    $actual = Normalize-RepoPath $repoRoot $filePath

    if ($actual -ne $expected) {
        Fail-Gate ("顺序错误。当前应先修改: " + $expected + "，但收到: " + $actual)
    }

    $state.files[$currentIndex].advanced = $true
    $state.files[$currentIndex].advanced_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    $state.current_index = $currentIndex + 1
    Save-GateState $statePath $state $false

    if ($state.current_index -lt $total) {
        Write-GateInfo ("Advance 通过。下一项: " + (Normalize-RepoPath $repoRoot $state.files[$state.current_index].path))
    } else {
        Write-GateInfo "Advance 通过。所有计划文件已按顺序打卡完成。"
    }
}

function Invoke-PlannedCommands($commands, $repoRoot) {
    foreach ($command in $commands) {
        if ($command.IndexOf("Invoke-OmoGate.ps1", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $command.IndexOf("-Phase Postflight", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            Fail-Gate "强制运行命令禁止再次调用 Postflight，以免递归。请把 Gate 命令与验证命令分开。"
        }

        Write-GateInfo ("执行命令: " + $command)
        Push-Location $repoRoot
        try {
            Invoke-Expression $command | Out-Host
        } finally {
            Pop-Location
        }
    }
}

function Invoke-Postflight($repoRoot, $statePath, $executeCommands, $noStateWrite) {
    $context = Load-PlanContext $repoRoot

    if ($noStateWrite) {
        Write-GateInfo "NoStateWrite 模式下执行无状态 Postflight 检查"
        if ($executeCommands) {
            Invoke-PlannedCommands $context.RunCommands $repoRoot
        }
        Write-GateInfo "Postflight 无状态检查通过。"
        return
    }

    $state = Load-GateState $statePath $false
    $currentIndex = [int]$state.current_index
    if ($currentIndex -lt $state.files.Count) {
        $nextExpected = Normalize-RepoPath $repoRoot $state.files[$currentIndex].path
        Fail-Gate ("仍有文件未按顺序打卡，下一项应为: " + $nextExpected)
    }

    if ($executeCommands) {
        Invoke-PlannedCommands $state.run_commands $repoRoot
    } else {
        Write-GateInfo "未执行命令，仅校验命令列表存在。"
    }

    $state.completed_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    Save-GateState $statePath $state $false
    Write-GateInfo "Postflight 通过。"
}

$repoRoot = Get-RepoRoot
$statePath = Join-Path $repoRoot ".omo\current-task\.gate-state.json"

switch ($Phase) {
    "Preflight" { Invoke-Preflight $repoRoot $statePath $NoStateWrite }
    "Advance" { Invoke-Advance $repoRoot $statePath $FilePath $NoStateWrite }
    "Postflight" { Invoke-Postflight $repoRoot $statePath $ExecuteCommands $NoStateWrite }
    default { Fail-Gate ("不支持的 Phase: " + $Phase) }
}
