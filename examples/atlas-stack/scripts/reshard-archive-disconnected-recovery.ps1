[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$CapturedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')
. (Join-Path $PSScriptRoot 'archive-fast-lane-routing.ps1')

function Get-DescendantProcessIds([int]$RootId, [object[]]$Processes) {
    $ids = New-Object 'Collections.Generic.HashSet[int]'
    [void]$ids.Add($RootId)
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $Processes) {
            if ($ids.Contains([int]$process.ParentProcessId) -and -not $ids.Contains([int]$process.ProcessId)) {
                [void]$ids.Add([int]$process.ProcessId); $changed = $true
            }
        }
    }
    return @($ids)
}

$run = [IO.Path]::GetFullPath($RunRoot)
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$statusPath = Join-Path $run 'parallel-collector-status.json'
$statePath = Join-Path $run 'collector-state.json'
$recoveryScript = Join-Path $repository 'scripts\get-archive-recovery-status.ps1'
$shardScript = Join-Path $repository 'scripts\invoke-archive-parallel-collector.ps1'
$resumeScript = Join-Path $repository 'scripts\resume-archive-parallel-supervisor.ps1'
foreach ($required in @($statusPath, $statePath, $recoveryScript, $shardScript, $resumeScript)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required recovery input was not found: $required" }
}

$recovery = & $recoveryScript -RunRoot $run
if ([int]$recovery.workerCount -lt 1 -or [int]$recovery.liveCoverageWorkers -ne 0) {
    throw 'Disconnected reshard requires an existing shard set with zero live ATLAS_COVER workers.'
}
$unsafe = @($recovery.workers | Where-Object { [string]$_.outcome -notin @(
    'archive-backend-rejected', 'server-disconnected', 'connecting', 'backoff', 'idle'
) })
if ($unsafe.Count -gt 0) { throw "Refusing to interrupt $($unsafe.Count) worker(s) outside a disconnected recovery state." }

$status = Read-AtlasJsonWithRetry $statusPath
$oldParallelRoot = [IO.Path]::GetFullPath([string]$status.runRoot)
$expectedParent = [IO.Path]::GetFullPath((Join-Path $run 'parallel-runs')).TrimEnd('\') + '\'
if (-not $oldParallelRoot.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Prior parallel root escaped the expected tree: $oldParallelRoot"
}
$historicalRoutes = @{}
foreach ($ledgerFile in @(Get-ChildItem -LiteralPath $expectedParent -Filter 'fast-lane-deferred.json' -File -Recurse -ErrorAction SilentlyContinue)) {
    $priorLedger = Read-AtlasJsonWithRetry $ledgerFile.FullName
    foreach ($route in @($priorLedger.entries)) {
        $identity = [string]$route.normalizedWarp
        if (-not [string]::IsNullOrWhiteSpace($identity) -and -not $historicalRoutes.ContainsKey($identity)) {
            $historicalRoutes[$identity] = $route
        }
    }
}

$processes = @(Get-CimInstance Win32_Process)
$supervisors = @($processes | Where-Object {
    $_.Name -match '^powershell(?:\.exe)?$' -and
    [string]$_.CommandLine -match '(?i)(?:resume-archive-parallel-supervisor|invoke-archive-parallel-collector)\.ps1' -and
    ([string]$_.CommandLine).IndexOf($run, [StringComparison]::OrdinalIgnoreCase) -ge 0
})
if ($supervisors.Count -ne 1) { throw "Expected exactly one collector supervisor; found $($supervisors.Count)." }
$workerWrappers = @($processes | Where-Object {
    $_.Name -match '^powershell(?:\.exe)?$' -and
    [string]$_.CommandLine -match '(?i)invoke-archive-collector\.ps1' -and
    ([string]$_.CommandLine).IndexOf($oldParallelRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
})

# Stop the verified supervisor first so it cannot replace a worker during the
# bounded teardown. No worker has live coverage by the required gate above.
Stop-Process -Id ([int]$supervisors[0].ProcessId) -Force
Start-Sleep -Milliseconds 500
foreach ($wrapper in $workerWrappers) {
    foreach ($processId in @(Get-DescendantProcessIds ([int]$wrapper.ProcessId) $processes | Sort-Object -Descending)) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Seconds 1

$backupRoot = Join-Path $oldParallelRoot 'queue-backups'
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $statePath -Destination (Join-Path $backupRoot "canonical-before-six-$stamp.json")

$mainState = Read-AtlasJsonWithRetry $statePath
$mainByWarp = @{}
foreach ($entry in @($mainState.entries)) { $mainByWarp[[string]$entry.normalizedWarp] = $entry }
$oldWorkers = @(Get-ChildItem -LiteralPath $oldParallelRoot -Directory | Where-Object Name -match '^worker-\d+-')
foreach ($workerRoot in $oldWorkers) {
    $queuePath = Join-Path $workerRoot.FullName 'queue.json'
    $workerStatePath = Join-Path $workerRoot.FullName 'state.json'
    $queue = Read-AtlasJsonWithRetry $queuePath
    $owned = @{}
    foreach ($entry in @($queue.entries)) { $owned[[string]$entry.normalizedWarp] = $true }
    $workerState = Read-AtlasJsonWithRetry $workerStatePath
    foreach ($entry in @($workerState.entries)) {
        $identity = [string]$entry.normalizedWarp
        if ($owned.ContainsKey($identity)) { $mainByWarp[$identity] = $entry }
    }
    $workerLock = "$workerStatePath.lock"
    if (Test-Path -LiteralPath $workerLock -PathType Leaf) {
        Move-Item -LiteralPath $workerLock -Destination "$workerLock.stale-$stamp.disabled"
    }
}
$mainState.entries = @($mainByWarp.Values | Sort-Object normalizedWarp)
$mainState.updatedUtc = [DateTime]::UtcNow.ToString('o')
Write-AtlasJsonAtomically $mainState $statePath 24
foreach ($lockPath in @("$statePath.parallel.lock", "$statePath.lock")) {
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        Move-Item -LiteralPath $lockPath -Destination "$lockPath.stale-$stamp.disabled"
    }
}

& $shardScript -RunRoot $run -RepositoryRoot $repository -CapturedRoot $CapturedRoot `
    -MaxWorkers 6 -FastLaneWorkerId 5 -SecondFastLaneWorkerId 6 -SetupOnly
$newStatus = Read-AtlasJsonWithRetry $statusPath
$newParallelRoot = [IO.Path]::GetFullPath([string]$newStatus.runRoot)
if (@($newStatus.workers).Count -ne 6 -or $newParallelRoot -eq $oldParallelRoot) {
    throw 'Six-worker shard creation did not produce a distinct validated run.'
}

# Carry unresolved fast-lane handoffs across the reshard. Their queue entries
# remain prioritized on one of workers 1-4 rather than falling back into a fast
# lane and consuming another 30-minute discovery attempt.
if ($historicalRoutes.Count -gt 0) {
    $newWorkers = @()
    foreach ($workerRoot in @(Get-ChildItem -LiteralPath $newParallelRoot -Directory | Where-Object Name -match '^worker-(\d+)-')) {
        $id = [int][regex]::Match($workerRoot.Name, '^worker-(\d+)-').Groups[1].Value
        $queuePath = Join-Path $workerRoot.FullName 'queue.json'
        $queue = Read-AtlasJsonWithRetry $queuePath
        $identities = @{}
        foreach ($entry in @($queue.entries)) { $identities[[string]$entry.normalizedWarp] = $true }
        $newWorkers += [pscustomobject]@{
            id = $id; queuePath = $queuePath; statePath = (Join-Path $workerRoot.FullName 'state.json')
            exitSignalPath = (Join-Path $workerRoot.FullName 'reload-queue-after-current-wdl.signal')
            identities = $identities; assigned = @($queue.entries).Count; completed = 0
        }
    }
    $carryRoutes = New-Object Collections.ArrayList
    foreach ($identity in @($historicalRoutes.Keys | Sort-Object)) {
        $owner = @($newWorkers | Where-Object { $_.identities.ContainsKey($identity) } | Select-Object -First 1)
        if ($owner.Count -ne 1) { continue }
        $ownerQueue = Read-AtlasJsonWithRetry $owner[0].queuePath
        $queueEntry = @($ownerQueue.entries | Where-Object { [string]$_.normalizedWarp -eq $identity } | Select-Object -First 1)
        if ($queueEntry.Count -ne 1) { continue }
        $target = @($newWorkers | Where-Object { [int]$_.id -le 4 } |
            Sort-Object assigned, id | Select-Object -First 1)[0]
        [void]$carryRoutes.Add([pscustomobject][ordered]@{
            normalizedWarp = $identity; warp = [string]$queueEntry[0].warp
            sourceWorkerId = [int]$historicalRoutes[$identity].sourceWorkerId; targetWorkerId = [int]$target.id
            deferredUtc = [string]$historicalRoutes[$identity].deferredUtc
            reason = 'fast-lane-30-minute-limit'; queueEntry = $queueEntry[0]
        })
        $target.assigned = [int]$target.assigned + 1
    }
    if ($carryRoutes.Count -gt 0) {
        $newLedgerPath = Join-Path $newParallelRoot 'fast-lane-deferred.json'
        $newLedger = [pscustomobject][ordered]@{
            schemaVersion = 1; updatedUtc = [DateTime]::UtcNow.ToString('o'); entries = @($carryRoutes)
        }
        Save-ArchiveFastLaneLedger $newLedger $newLedgerPath
        Sync-ArchiveFastLaneRouting $newWorkers $newLedgerPath | Out-Null
        Write-Output "FAST-LANE-ROUTES-CARRIED count=$($carryRoutes.Count)"
    }
}

$stdout = Join-Path $newParallelRoot "supervisor-six-$stamp.out.log"
$stderr = Join-Path $newParallelRoot "supervisor-six-$stamp.err.log"
$arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $resumeScript,
    '-RunRoot', $run, '-ParallelRunRoot', $newParallelRoot,
    '-MaxWorkerRestarts', '10000', '-WorkerRestartDelaySeconds', '300',
    '-WorkerStartDelaySeconds', '15', '-AdaptiveBackgroundRadiusBlocks', '0',
    '-FastLaneWorkerId', '5', '-SecondFastLaneWorkerId', '6',
    '-FastLaneAdaptiveMaximumRuntimeSeconds', '1800'
)
# Keep every production lane on its own authenticated install. The preserved
# 1.21.10 canary root uses the worker-1 identity and is diagnostic-only.
$newSupervisor = Start-Process 'powershell.exe' -WindowStyle Hidden -ArgumentList $arguments `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
Write-Output "SIX-WORKER-RECOVERY-LAUNCHED pid=$($newSupervisor.Id) run=$newParallelRoot"
