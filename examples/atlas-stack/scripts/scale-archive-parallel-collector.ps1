[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$RepositoryRoot = '',
    [string]$CapturedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [ValidateRange(2, 5)]
    [int]$MaxWorkers = 5,
    [ValidateRange(5, 720)]
    [int]$DrainTimeoutMinutes = 240,
    [ValidateRange(0, 300)]
    [int]$WorkerStartDelaySeconds = 45,
    [ValidateRange(0, 100000)]
    [int]$MaxWorkerRestarts = 10000,
    [ValidateRange(5, 600)]
    [int]$WorkerRestartDelaySeconds = 300
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 24
}

function Get-DescendantProcessIds([int]$RootId) {
    $all = @(Get-CimInstance Win32_Process)
    $ids = New-Object 'Collections.Generic.HashSet[int]'
    [void]$ids.Add($RootId)
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

function Stop-VerifiedWorkerTree([int]$WorkerProcessId) {
    $all = @(Get-CimInstance Win32_Process)
    $worker = $all | Where-Object { [int]$_.ProcessId -eq $WorkerProcessId } | Select-Object -First 1
    if ($null -eq $worker) { return 0 }
    if ($worker.Name -notmatch '^powershell(?:\.exe)?$' -or
        [string]$worker.CommandLine -notmatch 'invoke-archive-collector\.ps1') {
        throw "PID $WorkerProcessId is not a verified Archive collector worker."
    }
    $ids = @(Get-DescendantProcessIds $WorkerProcessId)
    Stop-Process -Id $WorkerProcessId -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    foreach ($id in @($ids | Where-Object { $_ -ne $WorkerProcessId })) {
        Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 1
    $survivors = @(Get-Process -Id $ids -ErrorAction SilentlyContinue)
    if ($survivors.Count -gt 0) { throw "Worker descendants survived: $($survivors.Id -join ',')" }
    return $ids.Count
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$run = [IO.Path]::GetFullPath($RunRoot)
$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$statusPath = Join-Path $run 'parallel-collector-status.json'
$statePath = Join-Path $run 'collector-state.json'
$parallelScript = Join-Path $repository 'scripts\invoke-archive-parallel-collector.ps1'
foreach ($required in @($statusPath, $statePath, $parallelScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required path was not found: $required" }
}

$status = Read-JsonUtf8 $statusPath
$parallelRunRoot = [IO.Path]::GetFullPath([string]$status.runRoot)
$parallelParent = [IO.Path]::GetFullPath((Join-Path $run 'parallel-runs'))
if (-not $parallelRunRoot.StartsWith($parallelParent, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Parallel run root escaped the expected run tree: $parallelRunRoot"
}

$workers = @($status.workers | Where-Object { [bool]$_.running })
if ($workers.Count -eq 0) { throw 'No live parallel collector workers were found.' }
$allProcesses = @(Get-CimInstance Win32_Process)
$parentIds = @($workers | ForEach-Object {
    $expectedWorkerId = [int]$_.processId
    $workerProcess = $allProcesses | Where-Object { [int]$_.ProcessId -eq $expectedWorkerId } | Select-Object -First 1
    if ($null -eq $workerProcess) { throw "Worker PID $($_.processId) disappeared before drain setup." }
    [int]$workerProcess.ParentProcessId
} | Select-Object -Unique)
if ($parentIds.Count -ne 1) { throw "Workers do not share exactly one supervisor: $($parentIds -join ',')" }
$supervisorId = [int]$parentIds[0]
$supervisor = $allProcesses | Where-Object { [int]$_.ProcessId -eq $supervisorId } | Select-Object -First 1
if ($null -eq $supervisor -or [string]$supervisor.CommandLine -notmatch 'invoke-archive-parallel-collector\.ps1') {
    throw "PID $supervisorId is not the expected parallel collector supervisor."
}

$drains = @()
foreach ($worker in $workers) {
    $stdout = [IO.Path]::GetFullPath([string]$worker.stdout)
    if (-not $stdout.StartsWith($parallelRunRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $stdout -PathType Leaf)) {
        throw "Worker output is outside the verified parallel run: $stdout"
    }
    $warpLines = @(Select-String -LiteralPath $stdout -Pattern '^WARP (.+)$')
    if ($warpLines.Count -eq 0) { throw "Worker $($worker.id) has no current WARP checkpoint in $stdout" }
    $lastWarp = $warpLines[-1]
    $workerDirectory = Split-Path -Parent $stdout
    $drains += [pscustomobject][ordered]@{
        id = [int]$worker.id
        profile = [string]$worker.profile
        processId = [int]$worker.processId
        stdout = $stdout
        statePath = Join-Path $workerDirectory 'state.json'
        queuePath = Join-Path $workerDirectory 'queue.json'
        warp = [string]$lastWarp.Matches[0].Groups[1].Value
        warpLine = [int]$lastWarp.LineNumber
        complete = $false
        durable = $false
        stoppedProcesses = 0
    }
}

foreach ($drain in $drains) {
    Write-Output "DRAIN-ARMED worker=$($drain.id) profile=$($drain.profile) warp=$($drain.warp)"
}

# The supervisor owns no capture. Stop it first so intentionally drained workers
# are not restarted while they finish their current WDL.
Stop-Process -Id $supervisorId -Force
Start-Sleep -Seconds 1
if ($null -ne (Get-Process -Id $supervisorId -ErrorAction SilentlyContinue)) {
    throw "Parallel supervisor PID $supervisorId survived the drain stop."
}
Write-Output "SUPERVISOR-STOPPED pid=$supervisorId; workers remain active until their armed checkpoints"

$deadline = (Get-Date).AddMinutes($DrainTimeoutMinutes)
while (@($drains | Where-Object { -not $_.complete }).Count -gt 0 -and (Get-Date) -lt $deadline) {
    foreach ($drain in @($drains | Where-Object { -not $_.complete })) {
        $escaped = [regex]::Escape([string]$drain.warp)
        $checkpoint = @(Select-String -LiteralPath $drain.stdout -Pattern "^(?:CAPTURED|WARNING: RETRYABLE) $escaped(?:\s|:|$)" |
            Where-Object { $_.LineNumber -gt $drain.warpLine } | Select-Object -First 1)
        try {
            $workerState = Read-JsonUtf8 $drain.statePath
            $stateEntry = @($workerState.entries | Where-Object {
                [string]$_.warp -eq [string]$drain.warp -and
                [string]$_.status -in @('captured', 'ready', 'retryable', 'missing')
            } | Select-Object -First 1)
        } catch { $stateEntry = @() }
        $workerAlive = $null -ne (Get-Process -Id $drain.processId -ErrorAction SilentlyContinue)
        if ($checkpoint.Count -eq 0 -and $stateEntry.Count -eq 0 -and $workerAlive) { continue }

        if ($stateEntry.Count -eq 1) {
            $drain.durable = $true
        } elseif ($checkpoint.Count -eq 1) {
            $stateDeadline = (Get-Date).AddSeconds(30)
            do {
                try {
                    $workerState = Read-JsonUtf8 $drain.statePath
                    $entry = @($workerState.entries | Where-Object {
                        [string]$_.warp -eq [string]$drain.warp -and
                        [string]$_.status -in @('captured', 'ready', 'retryable', 'missing')
                    } | Select-Object -First 1)
                } catch { $entry = @() }
                if ($entry.Count -eq 1) { break }
                Start-Sleep -Milliseconds 250
            } while ((Get-Date) -lt $stateDeadline)
            $drain.durable = $entry.Count -eq 1
        }

        if ($workerAlive) { $drain.stoppedProcesses = Stop-VerifiedWorkerTree $drain.processId }
        $drain.complete = $true
        Write-Output "WORKER-DRAINED id=$($drain.id) warp=$($drain.warp) durable=$($drain.durable) stopped=$($drain.stoppedProcesses)"
    }
    if (@($drains | Where-Object { -not $_.complete }).Count -gt 0) { Start-Sleep -Milliseconds 500 }
}

foreach ($drain in @($drains | Where-Object { -not $_.complete })) {
    if ($null -ne (Get-Process -Id $drain.processId -ErrorAction SilentlyContinue)) {
        $drain.stoppedProcesses = Stop-VerifiedWorkerTree $drain.processId
    }
    $drain.complete = $true
    Write-Warning "DRAIN-TIMEOUT worker=$($drain.id) warp=$($drain.warp); unfinished work will be retried"
}

# Merge only entries belonging to each worker's immutable shard. This is the
# same ownership rule used by the normal supervisor poll loop.
$mainState = Read-JsonUtf8 $statePath
$mainByWarp = @{}
foreach ($entry in @($mainState.entries)) { $mainByWarp[[string]$entry.normalizedWarp] = $entry }
foreach ($drain in $drains) {
    if (-not (Test-Path -LiteralPath $drain.queuePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $drain.statePath -PathType Leaf)) { continue }
    $workerQueue = Read-JsonUtf8 $drain.queuePath
    $owned = @{}
    foreach ($entry in @($workerQueue.entries)) { $owned[[string]$entry.normalizedWarp] = $true }
    $workerState = Read-JsonUtf8 $drain.statePath
    foreach ($entry in @($workerState.entries)) {
        $identity = [string]$entry.normalizedWarp
        if ($owned.ContainsKey($identity)) { $mainByWarp[$identity] = $entry }
    }
    $workerLock = "$($drain.statePath).lock"
    if (Test-Path -LiteralPath $workerLock -PathType Leaf) {
        Move-Item -LiteralPath $workerLock -Destination "$workerLock.stale-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).disabled"
    }
}
$mainState.entries = @($mainByWarp.Values | Sort-Object normalizedWarp)
$mainState.updatedUtc = [DateTime]::UtcNow.ToString('o')
Save-JsonAtomically $mainState $statePath

foreach ($lockPath in @("$statePath.parallel.lock", "$statePath.lock")) {
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        Move-Item -LiteralPath $lockPath -Destination "$lockPath.stale-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).disabled"
    }
}

Save-JsonAtomically ([ordered]@{
    stage = 'drained-for-scale'
    previousRunRoot = $parallelRunRoot
    workers = $drains
    updatedUtc = [DateTime]::UtcNow.ToString('o')
}) $statusPath

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdoutPath = Join-Path $run "parallel-$stamp.out.log"
$stderrPath = Join-Path $run "parallel-$stamp.err.log"
$arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $parallelScript,
    '-RunRoot', $run,
    '-RepositoryRoot', $repository,
    '-CapturedRoot', ([IO.Path]::GetFullPath($CapturedRoot)),
    '-MaxWorkers', [string]$MaxWorkers,
    '-MaxWarpsPerWorker', '10000',
    '-PollSeconds', '15',
    '-WorkerStartDelaySeconds', [string]$WorkerStartDelaySeconds,
    '-MaxWorkerRestarts', [string]$MaxWorkerRestarts,
    '-WorkerRestartDelaySeconds', [string]$WorkerRestartDelaySeconds
)
$newSupervisor = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
Write-Output "SCALE-LAUNCHED workers=$MaxWorkers pid=$($newSupervisor.Id) stdout=$stdoutPath stderr=$stderrPath"
