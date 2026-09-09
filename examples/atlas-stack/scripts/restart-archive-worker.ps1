[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [Parameter(Mandatory = $true)]
    [ValidateSet('atlas-owner', 'collector-two', 'collector-three', 'collector-four', 'collector-five', 'collector-six')]
    [string]$Profile
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Get-DescendantProcessIds([int]$RootProcessId) {
    $all = @(Get-CimInstance Win32_Process)
    $ids = New-Object 'Collections.Generic.HashSet[int]'
    [void]$ids.Add($RootProcessId)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $all) {
            if ($ids.Contains([int]$process.ParentProcessId) -and -not $ids.Contains([int]$process.ProcessId)) {
                [void]$ids.Add([int]$process.ProcessId)
                $changed = $true
            }
        }
    }
    return @($ids)
}

$run = [IO.Path]::GetFullPath($RunRoot)
$statusPath = Join-Path $run 'parallel-collector-status.json'
if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) { throw "Status file not found: $statusPath" }
$status = Read-AtlasJsonWithRetry -Path $statusPath
$matches = @($status.workers | Where-Object { [string]$_.profile -eq $Profile })
if ($matches.Count -ne 1 -or -not [bool]$matches[0].running) {
    throw "Exactly one running worker is required for profile $Profile."
}
$worker = $matches[0]
$workerId = [int]$worker.processId
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$workerId"
if ($null -eq $process -or [string]$process.CommandLine -notmatch '(?i)invoke-archive-collector\.ps1') {
    throw "PID $workerId is not the expected Archive collector worker."
}
$stateMatch = [regex]::Match([string]$process.CommandLine, '(?i)-StatePath\s+(?:"([^"]+)"|(\S+))')
if (-not $stateMatch.Success) { throw 'Worker command line does not expose a state path.' }
$statePath = if ($stateMatch.Groups[1].Success) { $stateMatch.Groups[1].Value } else { $stateMatch.Groups[2].Value }
$parallelRoot = [IO.Path]::GetFullPath([string]$status.runRoot).TrimEnd('\') + '\'
if (-not ([IO.Path]::GetFullPath($statePath)).StartsWith($parallelRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Worker state path is outside the active parallel run.'
}

$ids = @(Get-DescendantProcessIds $workerId)
Stop-Process -Id $workerId -Force
Start-Sleep -Milliseconds 250
foreach ($id in $ids) {
    if ($id -ne $workerId) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
}
$workerLock = "$statePath.lock"
if (Test-Path -LiteralPath $workerLock -PathType Leaf) {
    $stale = "$workerLock.stale-restart-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).disabled"
    Move-Item -LiteralPath $workerLock -Destination $stale
}

[pscustomobject][ordered]@{
    profile = $Profile
    stoppedProcessId = $workerId
    stoppedProcessCount = $ids.Count
    supervisorWillRestart = $true
} | ConvertTo-Json
