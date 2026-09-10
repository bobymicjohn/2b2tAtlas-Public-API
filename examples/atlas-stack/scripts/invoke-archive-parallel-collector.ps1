[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$CapturedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [string[]]$InstallRoots = @(
        'C:\AtlasExample\Ingest\archive-sync\collector',
        'C:\AtlasExample\Ingest\archive-sync\collectors\collector-two',
        'C:\AtlasExample\Ingest\archive-sync\collectors\collector-three',
        'C:\AtlasExample\Ingest\archive-sync\collectors\collector-four',
        'C:\AtlasExample\Ingest\archive-sync\collectors\collector-five',
        'C:\AtlasExample\Ingest\archive-sync\collectors\collector-six'
    ),
    [ValidateRange(1, 6)]
    [int]$MaxWorkers = 2,
    [ValidateRange(1, 10000)]
    [int]$MaxWarpsPerWorker = 10000,
    [ValidateRange(5, 300)]
    [int]$PollSeconds = 15,
    [ValidateRange(0, 180)]
    [int]$WorkerStartDelaySeconds = 45,
    [ValidateRange(0, 100000)]
    [int]$MaxWorkerRestarts = 10000,
    [ValidateRange(5, 600)]
    [int]$WorkerRestartDelaySeconds = 300,
    [ValidateRange(0, 6)]
    [int]$FastLaneWorkerId = 5,
    [ValidateRange(0, 6)]
    [int]$SecondFastLaneWorkerId = 6,
    [ValidateRange(300, 14400)]
    [int]$FastLaneAdaptiveMaximumRuntimeSeconds = 1800,
    [switch]$RecheckMissing,
    [switch]$RecheckRetryable,
    [switch]$SetupOnly
)

$ErrorActionPreference = 'Stop'
$collectorPauseSignal = 'C:\AtlasExample\Ingest\pause-collector'
if (Test-Path -LiteralPath $collectorPauseSignal) { Write-Warning 'Collector paused by operator.'; return }
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')
. (Join-Path $PSScriptRoot 'archive-fast-lane-routing.ps1')
. (Join-Path $PSScriptRoot 'archive-fast-lane-refill.ps1')
. (Join-Path $PSScriptRoot 'archive-collector-lane-policy.ps1')

function Read-JsonUtf8([string]$Path) {
    return Read-AtlasJsonWithRetry -Path $Path
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 20
}

function Test-FinalAdaptiveCapture([object]$Record) {
    if ($null -eq $Record -or [string]$Record.status -notin @('captured', 'ready')) { return $false }
    $adaptiveProperty = $Record.PSObject.Properties['adaptive']
    if ($null -eq $adaptiveProperty -or $null -eq $adaptiveProperty.Value) { return $false }
    $adaptive = $adaptiveProperty.Value
    $versionProperty = $adaptive.PSObject.Properties['standardVersion']
    $selectionProperty = $adaptive.PSObject.Properties['componentSelection']
    return $null -ne $versionProperty -and [int]$versionProperty.Value -ge 2 -and
        $null -ne $selectionProperty -and $null -ne $selectionProperty.Value
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

function Get-AuthenticatedProfile([string]$InstallRoot) {
    $authPath = Join-Path $InstallRoot 'HeadlessMC\auth\.accounts.json'
    if (-not (Test-Path -LiteralPath $authPath -PathType Leaf)) {
        throw "Collector instance is not authenticated: $InstallRoot"
    }
    $auth = Read-JsonUtf8 $authPath
    $profiles = @($auth.accounts | ForEach-Object {
        if ($null -ne $_.session -and $null -ne $_.session.mcProfile -and
            -not [string]::IsNullOrWhiteSpace([string]$_.session.mcProfile.name)) {
            [string]$_.session.mcProfile.name
        }
    } | Select-Object -Unique)
    if ($profiles.Count -ne 1) {
        throw "Expected exactly one authenticated Minecraft profile under $InstallRoot; found $($profiles.Count)."
    }
    return $profiles[0]
}

function Start-CollectorWorker([object]$Worker) {
    $suffix = if ([int]$Worker.restarts -gt 0) { ".restart-$($Worker.restarts)" } else { '' }
    $Worker.stdoutPath = Join-Path $Worker.workerRoot "collector$suffix.out.log"
    $Worker.stderrPath = Join-Path $Worker.workerRoot "collector$suffix.err.log"
    if (Test-Path -LiteralPath $collectorPauseSignal) { throw 'Collector safety hold: operator pause is active.' }
    $Worker.process = Start-Process -FilePath 'powershell.exe' -ArgumentList @(Get-ArchiveRefillLaunchArguments $Worker $Worker.arguments $refillLaneIds $FastLaneAdaptiveMaximumRuntimeSeconds -AllLong:$lanePolicy.allLong) -WindowStyle Hidden `
        -RedirectStandardOutput $Worker.stdoutPath -RedirectStandardError $Worker.stderrPath -PassThru
    $Worker.finalExitCode = $null
    $Worker.restartAfterUtc = $null
    Write-Output ("WORKER-STARTED id=$($Worker.id) profile=$($Worker.profile) assigned=$($Worker.assigned) " +
        "pid=$($Worker.process.Id) restart=$($Worker.restarts)")
}

$run = [IO.Path]::GetFullPath($RunRoot)
$lanePolicy = Get-ArchiveCollectorLanePolicy $run
$collectorScript = Join-Path $RepositoryRoot 'scripts\invoke-archive-collector.ps1'
$queuePath = Join-Path $run 'capture-queue.json'
$statePath = Join-Path $run 'collector-state.json'
$capturedRoot = [IO.Path]::GetFullPath($CapturedRoot)
$readyRoot = Join-Path $run 'ready'
$statusPath = Join-Path $run 'parallel-collector-status.json'
$fastLaneIds = @($FastLaneWorkerId, $SecondFastLaneWorkerId |
    Where-Object { $_ -gt 0 -and $_ -le $MaxWorkers } | Select-Object -Unique)
# Preserve the old routing IDs for final in-flight short captures. New starts
# and public status use the policy; refill remains available to every long lane.
$routingLaneIds = @($fastLaneIds)
$refillLaneIds = if ($lanePolicy.allLong) { @(1..6) } else { @($fastLaneIds) }
if ($lanePolicy.allLong) { $fastLaneIds = @() }
foreach ($required in @($collectorScript, $queuePath, $statePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required input was not found: $required" }
}
if (Test-Path -LiteralPath "$statePath.lock") {
    throw "The canonical collector is active; wait for or stop it before sharding: $statePath.lock"
}

$selectedRoots = @($InstallRoots | Select-Object -First $MaxWorkers)
if ($selectedRoots.Count -ne $MaxWorkers) { throw 'InstallRoots contains fewer entries than MaxWorkers.' }
$profiles = @()
foreach ($installRoot in $selectedRoots) {
    $fullRoot = [IO.Path]::GetFullPath($installRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $fullRoot 'headlessmc-launcher-2.10.0.jar') -PathType Leaf)) {
        throw "HeadlessMC launcher is missing from $fullRoot"
    }
    $profiles += Get-AuthenticatedProfile $fullRoot
}
if (@($profiles | Select-Object -Unique).Count -ne $profiles.Count) {
    throw 'Every worker must use a different authenticated Minecraft profile.'
}

$queue = Read-JsonUtf8 $queuePath
if (@($queue.conflicts).Count -gt 0) { throw 'Collector queue contains normalized-warp conflicts.' }
$mainState = Read-JsonUtf8 $statePath
$mainByWarp = @{}
foreach ($entry in @($mainState.entries)) {
    if ($null -ne $entry -and -not [string]::IsNullOrWhiteSpace([string]$entry.normalizedWarp)) {
        $mainByWarp[[string]$entry.normalizedWarp] = $entry
    }
}
$remaining = @($queue.entries | Where-Object {
    $identity = [string]$_.normalizedWarp
    $prior = $mainByWarp[$identity]
    if ($null -eq $prior) { return $true }
    $status = [string]$prior.status
    return (($status -notin @('captured', 'ready') -or -not (Test-FinalAdaptiveCapture $prior)) -and
        $status -ne 'needs-footprint-review' -and
        ($RecheckRetryable -or $status -ne 'retryable') -and
        ($RecheckMissing -or $status -ne 'missing'))
})
if ($remaining.Count -eq 0) {
    Write-Output 'No eligible Archive warps remain in the selected queue.'
    exit 0
}

$supervisorLockPath = "$statePath.parallel.lock"
$supervisorLock = $null
try {
    $supervisorLock = [IO.File]::Open($supervisorLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
} catch {
    throw "Another parallel collector supervisor already owns $supervisorLockPath"
}

try {
    $parallelRoot = Join-Path $run ('parallel-runs\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $parallelRoot -Force | Out-Null

    # Keep date variants and known Atlas-location siblings on the same worker so
    # strict covered-artifact reuse can operate without cross-process state races.
    $groupMap = @{}
    foreach ($entry in $remaining) {
        $key = Get-GroupKey $entry
        if (-not $groupMap.ContainsKey($key)) { $groupMap[$key] = New-Object Collections.ArrayList }
        [void]$groupMap[$key].Add($entry)
    }
    $assignments = @()
    for ($index = 0; $index -lt $selectedRoots.Count; $index++) {
        $assignments += ,(New-Object Collections.ArrayList)
    }
    foreach ($group in @($groupMap.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending)) {
        $targetIndex = 0
        for ($index = 1; $index -lt $assignments.Count; $index++) {
            if ($assignments[$index].Count -lt $assignments[$targetIndex].Count) { $targetIndex = $index }
        }
        foreach ($entry in @($group.Value)) { [void]$assignments[$targetIndex].Add($entry) }
    }

    $workers = @()
    for ($index = 0; $index -lt $selectedRoots.Count; $index++) {
        $workerId = $index + 1
        $workerRoot = Join-Path $parallelRoot ("worker-$workerId-$($profiles[$index].ToLowerInvariant())")
        New-Item -ItemType Directory -Path $workerRoot -Force | Out-Null
        $workerQueuePath = Join-Path $workerRoot 'queue.json'
        $workerStatePath = Join-Path $workerRoot 'state.json'
        $exitSignalPath = Join-Path $workerRoot 'reload-queue-after-current-wdl.signal'
        $workerQueue = ($queue | ConvertTo-Json -Depth 20) | ConvertFrom-Json
        $workerQueue.entries = @($assignments[$index])
        Save-JsonAtomically $workerQueue $workerQueuePath
        Save-JsonAtomically $mainState $workerStatePath

        $arguments = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $collectorScript,
            '-InstallRoot', [IO.Path]::GetFullPath($selectedRoots[$index]),
            '-QueuePath', $workerQueuePath,
            '-StatePath', $workerStatePath,
            '-KnownWarpStatePath', $statePath,
            '-PeerStateRoot', $parallelRoot,
            '-CapturedRoot', $capturedRoot,
            '-ReadyRoot', $readyRoot,
            '-MaxWarps', [string]$MaxWarpsPerWorker,
            '-AdaptiveScan',
            '-AdaptiveBackgroundRadiusBlocks', '0',
            '-DelayBetweenWarpsSeconds', '2',
            '-MinimumFreeGiB', '100',
            '-ExitAfterCurrentWarpSignalPath', $exitSignalPath
        )
        if ($RecheckMissing) { $arguments += '-RecheckMissing' }
        if ($RecheckRetryable) { $arguments += '-RecheckRetryable' }
        $lane = 'large-wdl'
        if ($workerId -in $fastLaneIds) {
            $arguments += @(
                '-AdaptiveMaximumRuntimeSeconds', [string]$FastLaneAdaptiveMaximumRuntimeSeconds,
                '-AdaptiveRuntimeLimitDisposition', 'Retryable'
            )
            $lane = 'throughput'
        }
        $identitySet = @{}
        foreach ($entry in @($assignments[$index])) { $identitySet[[string]$entry.normalizedWarp] = $true }
        $worker = [pscustomobject][ordered]@{
            id = $workerId
            profile = $profiles[$index]
            installRoot = [IO.Path]::GetFullPath($selectedRoots[$index])
            assigned = $assignments[$index].Count
            identities = $identitySet
            statePath = $workerStatePath
            queuePath = $workerQueuePath
            exitSignalPath = $exitSignalPath
            workerRoot = $workerRoot
            arguments = $arguments
            lane = $lane
            fallbackWarp = ''
            adaptiveMaximumRuntimeSeconds = if ($lane -eq 'throughput') {
                $FastLaneAdaptiveMaximumRuntimeSeconds
            } else {
                43200
            }
            stdoutPath = ''
            stderrPath = ''
            process = $null
            finalExitCode = $null
            restarts = 0
            restartAfterUtc = $null
            completed = 0
        }
        if (-not $SetupOnly) { Start-CollectorWorker $worker }
        $workers += $worker
        if (-not $SetupOnly -and $index -lt $selectedRoots.Count - 1 -and $WorkerStartDelaySeconds -gt 0) {
            Start-Sleep -Seconds $WorkerStartDelaySeconds
        }
    }

    if ($SetupOnly) {
        $setupWorkers = @($workers | ForEach-Object {
            [pscustomobject][ordered]@{
                id = $_.id; profile = $_.profile; processId = 0; running = $false; restarting = $true
                restartCount = 0; exitCode = $null; assigned = $_.assigned; completed = 0
                currentOrLastWarp = ''; lane = $_.lane
                adaptiveMaximumRuntimeSeconds = $_.adaptiveMaximumRuntimeSeconds
                minecraftVersion = 'fabric-loader-0.19.5-1.21.11'
                deferredIn = 0; deferredOut = 0; stdout = ''; stderr = ''
            }
        })
        Save-JsonAtomically ([ordered]@{
            stage = 'sharded-awaiting-supervisor'; runRoot = $parallelRoot; queued = $remaining.Count
            fastLaneWorkerId = if ($fastLaneIds.Count -gt 0) { $fastLaneIds[0] } else { 0 }
            fastLaneWorkerIds = @($fastLaneIds)
            lanePolicy = $lanePolicy.mode
            fastLaneAdaptiveMaximumRuntimeSeconds = $FastLaneAdaptiveMaximumRuntimeSeconds
            deferredToLongRunning = 0; workers = $setupWorkers; updatedUtc = [DateTime]::UtcNow.ToString('o')
        }) $statusPath
        Write-Output "PARALLEL-SHARDS-CREATED workers=$($workers.Count) queued=$($remaining.Count) root=$parallelRoot"
        return
    }

    $nextWorkerStartUtc = [DateTime]::UtcNow
    $routingLedgerPath = Join-Path $parallelRoot 'fast-lane-deferred.json'
    do {
        $routingLedger = Move-ArchiveFastLaneTimeouts -Workers $workers -FastLaneWorkerIds $routingLaneIds `
            -LedgerPath $routingLedgerPath
        $refillLedger = Invoke-ArchiveFastLaneRefill -Workers $workers -FastLaneWorkerIds $refillLaneIds -AllLong:$lanePolicy.allLong `
            -LedgerPath (Join-Path $parallelRoot 'fast-lane-refill.json') -DeferralLedger $routingLedger
        $anyRunning = $false
        $workerStatus = @()
        foreach ($worker in $workers) {
            $worker.process.Refresh()
            $processRunning = -not $worker.process.HasExited
            if (-not $processRunning -and $null -eq $worker.finalExitCode) {
                # Start-Process may not materialize ExitCode until the process
                # handle has been explicitly waited, even after HasExited flips.
                $worker.process.WaitForExit()
                $worker.process.Refresh()
                $worker.finalExitCode = [int]$worker.process.ExitCode
            }
            $completed = 0
            $lastWarp = ''
            $lastStarted = [datetime]::MinValue
            if (Test-Path -LiteralPath $worker.statePath -PathType Leaf) {
                try {
                    $workerState = Read-JsonUtf8 $worker.statePath
                    foreach ($entry in @($workerState.entries)) {
                        $identity = [string]$entry.normalizedWarp
                        if (-not $worker.identities.ContainsKey($identity)) { continue }
                        $mainByWarp[$identity] = $entry
                        $entryStatus = [string]$entry.status
                        if (($entryStatus -in @('captured', 'ready') -and (Test-FinalAdaptiveCapture $entry)) -or
                            $entryStatus -in @('missing', 'needs-footprint-review', 'retryable')) { $completed++ }
                        $startedProperty = $entry.PSObject.Properties['startedUtc']
                        if ($null -ne $startedProperty -and -not [string]::IsNullOrWhiteSpace([string]$startedProperty.Value)) {
                            $started = [datetime]$startedProperty.Value
                            if ($started -gt $lastStarted) {
                                $lastStarted = $started
                                $lastWarp = [string]$entry.warp
                            }
                        }
                    }
                } catch {
                    Write-Warning "Worker $($worker.id) checkpoint was mid-write or unreadable: $($_.Exception.Message)"
                }
            }
            $worker.completed = $completed
            $restartPending = $false
            $refillPending = @($refillLedger.batches | Where-Object {
                $_.status -eq 'waiting-for-boundary' -and
                ([int]$_.sourceWorkerId -eq [int]$worker.id -or [int]$_.targetWorkerId -eq [int]$worker.id)
            }).Count -gt 0
            $refillDonorHeld = -not $processRunning -and @($refillLedger.batches | Where-Object {
                $_.status -eq 'waiting-for-boundary' -and [int]$_.sourceWorkerId -eq [int]$worker.id
            }).Count -gt 0
            if (-not $processRunning -and -not $refillDonorHeld -and $null -ne $worker.finalExitCode -and
                ($completed -lt $worker.assigned -or
                    ($null -ne $worker.PSObject.Properties['fallbackWarp'] -and -not [string]::IsNullOrWhiteSpace([string]$worker.fallbackWarp))) -and
                $worker.restarts -lt $MaxWorkerRestarts) {
                if ($null -eq $worker.restartAfterUtc) {
                    $worker.restartAfterUtc = [DateTime]::UtcNow.AddSeconds($WorkerRestartDelaySeconds)
                }
                if ([DateTime]::UtcNow -ge [datetime]$worker.restartAfterUtc -and
                    [DateTime]::UtcNow -ge $nextWorkerStartUtc) {
                    $worker.restarts++
                    Start-CollectorWorker $worker
                    $processRunning = $true
                    $nextWorkerStartUtc = [DateTime]::UtcNow.AddSeconds($WorkerStartDelaySeconds)
                } else {
                    $restartPending = $true
                }
            }
            if ($processRunning -or $restartPending -or $refillPending) { $anyRunning = $true }
            $workerStatus += [pscustomobject][ordered]@{
                id = $worker.id
                profile = $worker.profile
                processId = $worker.process.Id
                running = $processRunning
                restarting = $restartPending
                refillPending = $refillPending
                restartCount = $worker.restarts
                exitCode = $worker.finalExitCode
                assigned = $worker.assigned
                completed = $completed
                currentOrLastWarp = $lastWarp
                lane = $worker.lane
                adaptiveMaximumRuntimeSeconds = $worker.adaptiveMaximumRuntimeSeconds
                deferredIn = @($routingLedger.entries | Where-Object { [int]$_.targetWorkerId -eq [int]$worker.id }).Count
                deferredOut = @($routingLedger.entries | Where-Object { [int]$_.sourceWorkerId -eq [int]$worker.id }).Count
                stdout = $worker.stdoutPath
                stderr = $worker.stderrPath
            }
        }
        $mainState.entries = @($mainByWarp.Values | Sort-Object normalizedWarp)
        $mainState.updatedUtc = [DateTime]::UtcNow.ToString('o')
        Save-JsonAtomically $mainState $statePath
        Save-JsonAtomically ([ordered]@{
            stage = if ($anyRunning) { 'capturing-parallel' } else { 'parallel-finished' }
            runRoot = $parallelRoot
            queued = $remaining.Count
            fastLaneWorkerId = if ($fastLaneIds.Count -gt 0) { $fastLaneIds[0] } else { 0 }
            fastLaneWorkerIds = @($fastLaneIds)
            lanePolicy = $lanePolicy.mode
            fastLaneAdaptiveMaximumRuntimeSeconds = $FastLaneAdaptiveMaximumRuntimeSeconds
            deferredToLongRunning = @($routingLedger.entries).Count
            workers = $workerStatus
            updatedUtc = [DateTime]::UtcNow.ToString('o')
        }) $statusPath
        if ($anyRunning) { Start-Sleep -Seconds $PollSeconds }
    } while ($anyRunning)

    $failedWorkers = @($workers | Where-Object { $null -eq $_.finalExitCode -or $_.finalExitCode -ne 0 })
    if ($failedWorkers.Count -gt 0) {
        throw "$($failedWorkers.Count) parallel collector worker(s) exited nonzero; completed checkpoints were merged."
    }
    Write-Output "PARALLEL-COLLECTOR-FINISHED workers=$($workers.Count) queued=$($remaining.Count)"
} finally {
    if ($null -ne $supervisorLock) { $supervisorLock.Dispose() }
    if (Test-Path -LiteralPath $supervisorLockPath -PathType Leaf) {
        Remove-Item -LiteralPath $supervisorLockPath -Force
    }
}
