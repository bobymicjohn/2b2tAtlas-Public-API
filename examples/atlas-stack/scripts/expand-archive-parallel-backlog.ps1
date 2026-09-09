[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot,
    [Parameter(Mandatory = $true)]
    [string]$ParallelRunRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return Read-AtlasJsonWithRetry -Path $Path
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 24
}

function Get-GroupKey([object]$Entry) {
    $locationProperty = $Entry.PSObject.Properties['locationUuid']
    if ($null -ne $locationProperty -and -not [string]::IsNullOrWhiteSpace([string]$locationProperty.Value)) {
        return 'location:' + ([string]$locationProperty.Value).ToLowerInvariant()
    }
    $identity = [string]$Entry.normalizedWarp
    $identity = $identity -replace '(?i)@(overworld|nether|end)$', ''
    $identity = $identity -replace '(?i)(?:[_\- ](?:19|20)\d{2}(?:[_\- ]\d{1,2}){0,2})$', ''
    return 'warp:' + $identity
}

$run = [IO.Path]::GetFullPath($RunRoot)
$parallelRun = [IO.Path]::GetFullPath($ParallelRunRoot)
$runPrefix = $run.TrimEnd('\') + '\'
if (-not $parallelRun.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Parallel run must be contained by the canonical run root: $parallelRun"
}
$statePath = Join-Path $run 'collector-state.json'
$queuePath = Join-Path $run 'capture-queue.json'
$supervisorLockPath = "$statePath.parallel.lock"
if (Test-Path -LiteralPath $supervisorLockPath -PathType Leaf) {
    throw "Stop the parallel supervisor before expanding queues: $supervisorLockPath"
}
foreach ($required in @($statePath, $queuePath, $parallelRun)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required path was not found: $required" }
}

$workerRoots = @(Get-ChildItem -LiteralPath $parallelRun -Directory | Where-Object Name -like 'worker-*' | Sort-Object Name)
if ($workerRoots.Count -eq 0) { throw "No worker queues were found under $parallelRun" }
$queue = Read-JsonUtf8 $queuePath
$state = Read-JsonUtf8 $statePath
$stateByWarp = @{}
foreach ($entry in @($state.entries)) {
    if ($null -ne $entry -and -not [string]::IsNullOrWhiteSpace([string]$entry.normalizedWarp)) {
        $stateByWarp[[string]$entry.normalizedWarp] = $entry
    }
}

$workerQueues = @()
$assigned = @{}
foreach ($workerRoot in $workerRoots) {
    $path = Join-Path $workerRoot.FullName 'queue.json'
    $workerQueue = Read-JsonUtf8 $path
    foreach ($entry in @($workerQueue.entries)) { $assigned[[string]$entry.normalizedWarp] = $true }
    $workerQueues += [pscustomobject]@{ Path = $path; Queue = $workerQueue; Entries = [Collections.ArrayList]@($workerQueue.entries) }
}

$eligible = @($queue.entries | Where-Object {
    $identity = [string]$_.normalizedWarp
    if ($assigned.ContainsKey($identity)) { return $false }
    $prior = $stateByWarp[$identity]
    if ($null -eq $prior) { return $true }
    return [string]$prior.status -in @('skipped-inside-background', 'preflight-eligible')
})

$groups = @{}
foreach ($entry in $eligible) {
    $key = Get-GroupKey $entry
    if (-not $groups.ContainsKey($key)) { $groups[$key] = New-Object Collections.ArrayList }
    [void]$groups[$key].Add($entry)
}
foreach ($group in @($groups.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending)) {
    $target = $workerQueues | Sort-Object { $_.Entries.Count } | Select-Object -First 1
    foreach ($entry in @($group.Value)) { [void]$target.Entries.Add($entry) }
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$backupRoot = Join-Path $parallelRun "queue-backups\$stamp"
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
foreach ($worker in $workerQueues) {
    Copy-Item -LiteralPath $worker.Path -Destination (Join-Path $backupRoot ([IO.Path]::GetFileName((Split-Path $worker.Path -Parent)) + '-queue.json'))
    $worker.Queue.entries = @($worker.Entries)
    Save-JsonAtomically $worker.Queue $worker.Path
}

[pscustomobject][ordered]@{
    added = $eligible.Count
    previouslyAssigned = $assigned.Count
    nowAssigned = ($workerQueues | ForEach-Object { $_.Entries.Count } | Measure-Object -Sum).Sum
    backupRoot = $backupRoot
    workers = @($workerQueues | ForEach-Object { [pscustomobject]@{ queue = $_.Path; assigned = $_.Entries.Count } })
} | ConvertTo-Json -Depth 8
