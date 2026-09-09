[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 2147483647)]
    [int]$RootProcessId,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedWarp,
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$CheckpointLogPath = '',
    [ValidateRange(1, 60)]
    [int]$TimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$run = [IO.Path]::GetFullPath($RunRoot)
$statePath = Join-Path $run 'collector-state.json'
$outputLogPath = $CheckpointLogPath
if ([string]::IsNullOrWhiteSpace($outputLogPath)) {
    $outputLog = Get-ChildItem -LiteralPath $run -Filter 'continuation-*.out.log' -File |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($null -eq $outputLog) { throw "Continuation output log was not found under $run" }
    $outputLogPath = $outputLog.FullName
}
if (-not (Test-Path -LiteralPath $outputLogPath -PathType Leaf)) {
    throw "Checkpoint output log was not found: $outputLogPath"
}

$escapedWarp = [regex]::Escape($ExpectedWarp)
$checkpointPattern = "(?m)^(?:CAPTURED|WARNING: RETRYABLE) $escapedWarp(?:\s|:|$)"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    $tail = (Get-Content -LiteralPath $outputLogPath -Tail 40 -ErrorAction SilentlyContinue) -join "`n"
    if ($tail -match $checkpointPattern) { break }
    Start-Sleep -Milliseconds 500
}
if ((Get-Date) -ge $deadline) { throw "Timed out waiting for a durable checkpoint for $ExpectedWarp" }

$allProcesses = @(Get-CimInstance Win32_Process)
$root = $allProcesses | Where-Object { [int]$_.ProcessId -eq $RootProcessId } | Select-Object -First 1
if ($null -eq $root -or [string]$root.CommandLine -notmatch '(?:continue-archive-full-catalog|invoke-archive-parallel-collector)\.ps1') {
    throw "PID $RootProcessId is no longer the expected Archive collector orchestrator."
}

# A worker emits CAPTURED after writing its private state. Give the parallel
# supervisor one polling interval to merge that entry into canonical state.
$stateDeadline = (Get-Date).AddSeconds(45)
do {
    try {
        $canonicalState = [IO.File]::ReadAllText($statePath, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
        $checkpointEntry = @($canonicalState.entries | Where-Object {
            [string]$_.warp -eq $ExpectedWarp -and [string]$_.status -in @('captured', 'ready', 'retryable', 'missing')
        } | Select-Object -First 1)
    } catch {
        $checkpointEntry = @()
    }
    if ($checkpointEntry.Count -eq 1) { break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $stateDeadline)
if ($checkpointEntry.Count -ne 1) {
    throw "Checkpoint for $ExpectedWarp was not merged into canonical state."
}
$descendantIds = New-Object 'Collections.Generic.HashSet[int]'
[void]$descendantIds.Add($RootProcessId)
$changed = $true
while ($changed) {
    $changed = $false
    foreach ($process in $allProcesses) {
        if ($descendantIds.Contains([int]$process.ParentProcessId) -and
            -not $descendantIds.Contains([int]$process.ProcessId)) {
            [void]$descendantIds.Add([int]$process.ProcessId)
            $changed = $true
        }
    }
}

# Stop the orchestrator first so it cannot dispatch the next warp, then stop its
# already-resolved child tree. The checkpoint line is emitted only after the
# ZIP, metadata, and canonical state entry have been atomically persisted.
Stop-Process -Id $RootProcessId -Force
Start-Sleep -Milliseconds 500
foreach ($processId in @($descendantIds | Where-Object { $_ -ne $RootProcessId })) {
    Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2
$survivors = @(Get-Process -Id @($descendantIds) -ErrorAction SilentlyContinue)
if ($survivors.Count -gt 0) { throw "Collector descendants survived: $($survivors.Id -join ',')" }

$lockPath = "$statePath.lock"
$quarantinedLock = ''
if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
    $quarantinedLock = "$lockPath.stale-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).disabled"
    Move-Item -LiteralPath $lockPath -Destination $quarantinedLock
}

[pscustomobject][ordered]@{
    checkpointDetected = $true
    warp = $ExpectedWarp
    stoppedRootProcessId = $RootProcessId
    stoppedProcessCount = $descendantIds.Count
    quarantinedLock = $quarantinedLock
    stoppedUtc = [DateTime]::UtcNow.ToString('o')
}
