[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot,
    [Parameter(Mandatory = $true)]
    [string]$ParallelRunRoot,
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$CapturedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [ValidateRange(5, 300)]
    [int]$PollSeconds = 15,
    [ValidateRange(0, 100000)]
    [int]$MaxWorkerRestarts = 10000,
    [ValidateRange(0, 600)]
    [int]$WorkerRestartDelaySeconds = 300,
    [ValidateRange(0, 600)]
    [int]$InitialWorkerStartDelaySeconds = 0,
    [ValidateRange(0, 600)]
    [int]$WorkerStartDelaySeconds = 15,
    [ValidateRange(0, 30000000)]
    [int]$AdaptiveBackgroundRadiusBlocks = 0,
    [ValidateRange(0, 6)]
    [int]$FastLaneWorkerId = 5,
    [ValidateRange(0, 6)]
    [int]$SecondFastLaneWorkerId = 6,
    [ValidateRange(300, 14400)]
    [int]$FastLaneAdaptiveMaximumRuntimeSeconds = 1800,
    [string]$PrimaryMinecraftVersion = 'fabric-loader-0.19.5-1.21.11',
    [string]$CompatibilityInstallRoot = '',
    [string]$CompatibilityMinecraftVersion = 'fabric-loader-0.19.3-1.21.10',
    [ValidateRange(0, 6)]
    [int]$CompatibilityWorkerId = 0,
    [ValidateRange(1, 100)]
    [int]$CompatibilityEveryRestarts = 3,
    [switch]$RecheckRetryable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$utf8 = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')
. (Join-Path $PSScriptRoot 'archive-fast-lane-routing.ps1')
. (Join-Path $PSScriptRoot 'archive-fast-lane-refill.ps1')
. (Join-Path $PSScriptRoot 'archive-collector-lane-policy.ps1')

function Read-JsonUtf8([string]$Path) {
    return Read-AtlasJsonWithRetry -Path $Path
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 24
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

function Start-CollectorWorker([object]$Worker) {
    $useCompatibility = $CompatibilityWorkerId -gt 0 -and
        $Worker.id -eq $CompatibilityWorkerId -and
        -not [string]::IsNullOrWhiteSpace($CompatibilityInstallRoot) -and
        $Worker.restarts % $CompatibilityEveryRestarts -eq 0
    $attemptArguments = if ($useCompatibility) { $Worker.compatibilityArguments } else { $Worker.primaryArguments }
    $attemptArguments = @(Get-ArchiveRefillLaunchArguments $Worker $attemptArguments $refillLaneIds $FastLaneAdaptiveMaximumRuntimeSeconds -AllLong:$lanePolicy.allLong)
    $Worker.activeMinecraftVersion = if ($useCompatibility) { $CompatibilityMinecraftVersion } else { $PrimaryMinecraftVersion }
    $suffix = ".reattach-restart-$($Worker.restarts)"
    $Worker.stdout = Join-Path $Worker.workerRoot "collector$suffix.out.log"
    $Worker.stderr = Join-Path $Worker.workerRoot "collector$suffix.err.log"
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $attemptArguments -WindowStyle Hidden `
        -RedirectStandardOutput $Worker.stdout -RedirectStandardError $Worker.stderr -PassThru
    $Worker.processId = [int]$process.Id
    $Worker.restartAfterUtc = $null
    Write-Output ("WORKER-REATTACHED id=$($Worker.id) profile=$($Worker.profile) " +
        "pid=$($Worker.processId) restart=$($Worker.restarts) minecraft=$($Worker.activeMinecraftVersion)")
}

$run = [IO.Path]::GetFullPath($RunRoot)
$lanePolicy = Get-ArchiveCollectorLanePolicy $run
$parallelRun = [IO.Path]::GetFullPath($ParallelRunRoot)
$runPrefix = $run.TrimEnd('\') + '\'
if (-not $parallelRun.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Parallel run must be contained by the canonical run root: $parallelRun"
}
$statePath = Join-Path $run 'collector-state.json'
$statusPath = Join-Path $run 'parallel-collector-status.json'
$lockPath = "$statePath.parallel.lock"
$collectorScript = Join-Path $RepositoryRoot 'scripts\invoke-archive-collector.ps1'
$capturedRoot = [IO.Path]::GetFullPath($CapturedRoot)
$readyRoot = Join-Path $run 'ready'
$fastLaneIds = @($FastLaneWorkerId, $SecondFastLaneWorkerId |
    Where-Object { $_ -gt 0 } | Select-Object -Unique)
# Preserve the old routing IDs for final in-flight short captures. New starts
# and public status use the policy; refill remains available to every long lane.
$routingLaneIds = @($fastLaneIds)
$refillLaneIds = if ($lanePolicy.allLong) { @(1..6) } else { @($fastLaneIds) }
if ($lanePolicy.allLong) { $fastLaneIds = @() }
foreach ($path in @($statePath, $parallelRun, $collectorScript)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required path was not found: $path" }
}
if (-not [string]::IsNullOrWhiteSpace($CompatibilityInstallRoot)) {
    $CompatibilityInstallRoot = [IO.Path]::GetFullPath($CompatibilityInstallRoot)
    if (-not (Test-Path -LiteralPath $CompatibilityInstallRoot -PathType Container)) {
        throw "Compatibility install root was not found: $CompatibilityInstallRoot"
    }
}

$priorRestartCounts = @{}
if (Test-Path -LiteralPath $statusPath -PathType Leaf) {
    try {
        $priorStatus = Read-JsonUtf8 $statusPath
        foreach ($priorWorker in @($priorStatus.workers)) {
            $priorRestartCounts["$([int]$priorWorker.id)|$([string]$priorWorker.profile)"] =
                if ($null -ne $priorWorker.PSObject.Properties['restartCount']) { [int]$priorWorker.restartCount } else { 0 }
        }
    } catch {
        Write-Warning "Prior parallel status was unreadable; restart counters begin at zero: $($_.Exception.Message)"
    }
}

$workers = @()
foreach ($workerRoot in @(Get-ChildItem -LiteralPath $parallelRun -Directory | Sort-Object Name)) {
    $queuePath = Join-Path $workerRoot.FullName 'queue.json'
    $workerStatePath = Join-Path $workerRoot.FullName 'state.json'
    $exitSignalPath = Join-Path $workerRoot.FullName 'reload-queue-after-current-wdl.signal'
    if (-not (Test-Path -LiteralPath $queuePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $workerStatePath -PathType Leaf)) { continue }
    $queue = Read-JsonUtf8 $queuePath
    $identities = @{}
    foreach ($entry in @($queue.entries)) { $identities[[string]$entry.normalizedWarp] = $true }
    $matching = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
        [string]$_.CommandLine -match '(?i)invoke-archive-collector\.ps1' -and
        [string]$_.CommandLine -like "*$workerStatePath*"
    })
    if ($matching.Count -gt 1) { throw "Multiple collector processes own $workerStatePath" }
    $idMatch = [regex]::Match($workerRoot.Name, '^worker-([0-9]+)-(.+)$')
    if (-not $idMatch.Success) { throw "Unexpected worker directory name: $($workerRoot.Name)" }
    $workerId = [int]$idMatch.Groups[1].Value
    $profile = $idMatch.Groups[2].Value
    $installRoot = if ($profile -eq 'atlas-owner') {
        'C:\AtlasExample\Ingest\archive-sync\collector'
    } else {
        Join-Path 'C:\AtlasExample\Ingest\archive-sync\collectors' $profile
    }
    $arguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $collectorScript,
        '-InstallRoot', $installRoot,
        '-MinecraftVersion', $PrimaryMinecraftVersion,
        '-QueuePath', $queuePath,
        '-StatePath', $workerStatePath,
        '-CapturedRoot', $capturedRoot,
        '-ReadyRoot', $readyRoot,
        '-MaxWarps', '10000',
        '-AdaptiveScan',
        '-AdaptiveBackgroundRadiusBlocks', [string]$AdaptiveBackgroundRadiusBlocks,
        '-DelayBetweenWarpsSeconds', '2',
        '-MinimumFreeGiB', '100',
        '-ExitAfterCurrentWarpSignalPath', $exitSignalPath
    )
    if ($RecheckRetryable) { $arguments += '-RecheckRetryable' }
    $lane = 'large-wdl'
    if ($workerId -in $fastLaneIds) {
        $arguments += @(
            '-AdaptiveMaximumRuntimeSeconds', [string]$FastLaneAdaptiveMaximumRuntimeSeconds,
            '-AdaptiveRuntimeLimitDisposition', 'Retryable'
        )
        $lane = 'throughput'
    }
    $compatibilityArguments = @($arguments)
    if ($workerId -eq $CompatibilityWorkerId -and
        -not [string]::IsNullOrWhiteSpace($CompatibilityInstallRoot)) {
        for ($argumentIndex = 0; $argumentIndex -lt $compatibilityArguments.Count; $argumentIndex++) {
            if ($compatibilityArguments[$argumentIndex] -eq '-InstallRoot') {
                $compatibilityArguments[$argumentIndex + 1] = $CompatibilityInstallRoot
            } elseif ($compatibilityArguments[$argumentIndex] -eq '-MinecraftVersion') {
                $compatibilityArguments[$argumentIndex + 1] = $CompatibilityMinecraftVersion
            }
        }
    }
    $activeTimeout = if ($lane -eq 'throughput') { $FastLaneAdaptiveMaximumRuntimeSeconds } else { 43200 }
    $activeStdout = Join-Path $workerRoot.FullName 'collector.out.log'
    $activeStderr = Join-Path $workerRoot.FullName 'collector.err.log'
    if ($matching.Count -eq 1) {
        # Reattaching changes future launches, never the budget of a running
        # PowerShell capture. Report its actual arguments until its safe exit.
        $runtimeMatch = [regex]::Match([string]$matching[0].CommandLine, '(?i)-AdaptiveMaximumRuntimeSeconds\s+"?(\d+)')
        $activeTimeout = if ($runtimeMatch.Success) { [int]$runtimeMatch.Groups[1].Value } else { 43200 }
        $lane = if ($activeTimeout -lt 43200) { 'throughput' } else { 'large-wdl' }
        if ($null -ne (Get-Variable priorStatus -ErrorAction SilentlyContinue)) {
            $previousWorker = @($priorStatus.workers | Where-Object {
                [int]$_.id -eq $workerId -and [int]$_.processId -eq [int]$matching[0].ProcessId
            })
            if ($previousWorker.Count -eq 1) {
                if ([string]$previousWorker[0].lane -eq 'temporary-long') { $lane = 'temporary-long' }
                $activeStdout = [string]$previousWorker[0].stdout
                $activeStderr = [string]$previousWorker[0].stderr
            }
        }
    }
    $restartKey = "$workerId|$profile"
    $workers += [pscustomobject]@{
        id = $workerId
        profile = $profile
        processId = if ($matching.Count -eq 1) { [int]$matching[0].ProcessId } else { 0 }
        statePath = $workerStatePath
        queuePath = $queuePath
        exitSignalPath = $exitSignalPath
        identities = $identities
        assigned = @($queue.entries).Count
        workerRoot = $workerRoot.FullName
        installRoot = $installRoot
        primaryArguments = $arguments
        compatibilityArguments = $compatibilityArguments
        activeMinecraftVersion = $PrimaryMinecraftVersion
        lane = $lane
        fallbackWarp = ''
        adaptiveMaximumRuntimeSeconds = $activeTimeout
        restarts = if ($priorRestartCounts.ContainsKey($restartKey)) { [int]$priorRestartCounts[$restartKey] } else { 0 }
        restartAfterUtc = if ($matching.Count -eq 1) {
            $null
        } else {
            [DateTime]::UtcNow.AddSeconds($InitialWorkerStartDelaySeconds)
        }
        stdout = $activeStdout
        stderr = $activeStderr
        completed = 0
    }
}
if ($workers.Count -eq 0) { throw "No resumable workers were found under $parallelRun" }

$lock = $null
if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
    $lockOwner = @()
    try {
        $lockOwner = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
            [int]$_.ProcessId -ne $PID -and
            -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
            [string]$_.CommandLine -match '(?i)resume-archive-parallel-supervisor\.ps1' -and
            ([string]$_.CommandLine).IndexOf($run, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
    } catch {
        throw "Could not verify the owner of parallel supervisor lock $lockPath`: $($_.Exception.Message)"
    }
    if ($lockOwner.Count -eq 0) {
        $stalePath = "$lockPath.stale-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$PID"
        Move-Item -LiteralPath $lockPath -Destination $stalePath
        Write-Warning "Recovered orphaned parallel supervisor lock: $stalePath"
    }
}
try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
} catch [IO.IOException] {
    throw "Another parallel supervisor already owns $lockPath"
}

try {
    $nextWorkerStartUtc = [DateTime]::UtcNow
    $routingLedgerPath = Join-Path $parallelRun 'fast-lane-deferred.json'
    do {
        $routingLedger = Move-ArchiveFastLaneTimeouts -Workers $workers -FastLaneWorkerIds $routingLaneIds `
            -LedgerPath $routingLedgerPath
        $refillLedger = Invoke-ArchiveFastLaneRefill -Workers $workers -FastLaneWorkerIds $refillLaneIds -AllLong:$lanePolicy.allLong `
            -LedgerPath (Join-Path $parallelRun 'fast-lane-refill.json') -DeferralLedger $routingLedger
        $mainState = Read-JsonUtf8 $statePath
        $mainByWarp = @{}
        foreach ($entry in @($mainState.entries)) {
            if ($null -ne $entry -and -not [string]::IsNullOrWhiteSpace([string]$entry.normalizedWarp)) {
                $mainByWarp[[string]$entry.normalizedWarp] = $entry
            }
        }
        $anyRunning = $false
        $workerStatus = @()
        foreach ($worker in $workers) {
            $processRunning = $worker.processId -gt 0 -and
                $null -ne (Get-Process -Id $worker.processId -ErrorAction SilentlyContinue)
            $completed = 0
            $lastWarp = ''
            $lastStarted = [datetime]::MinValue
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
                    if ($null -ne $startedProperty -and
                        -not [string]::IsNullOrWhiteSpace([string]$startedProperty.Value)) {
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
            $worker.completed = $completed
            $restartPending = $false
            $refillPending = @($refillLedger.batches | Where-Object {
                $_.status -eq 'waiting-for-boundary' -and
                ([int]$_.sourceWorkerId -eq [int]$worker.id -or [int]$_.targetWorkerId -eq [int]$worker.id)
            }).Count -gt 0
            $refillDonorHeld = -not $processRunning -and @($refillLedger.batches | Where-Object {
                $_.status -eq 'waiting-for-boundary' -and [int]$_.sourceWorkerId -eq [int]$worker.id
            }).Count -gt 0
            if (-not $processRunning -and -not $refillDonorHeld -and ($completed -lt $worker.assigned -or
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
                processId = $worker.processId
                running = $processRunning
                restarting = $restartPending
                refillPending = $refillPending
                restartCount = $worker.restarts
                exitCode = $null
                assigned = $worker.assigned
                completed = $completed
                currentOrLastWarp = $lastWarp
                lane = $worker.lane
                adaptiveMaximumRuntimeSeconds = $worker.adaptiveMaximumRuntimeSeconds
                minecraftVersion = $worker.activeMinecraftVersion
                deferredIn = @($routingLedger.entries | Where-Object { [int]$_.targetWorkerId -eq [int]$worker.id }).Count
                deferredOut = @($routingLedger.entries | Where-Object { [int]$_.sourceWorkerId -eq [int]$worker.id }).Count
                stdout = $worker.stdout
                stderr = $worker.stderr
            }
        }
        $mainState.entries = @($mainByWarp.Values | Sort-Object normalizedWarp)
        $mainState.updatedUtc = [DateTime]::UtcNow.ToString('o')
        Save-JsonAtomically $mainState $statePath
        Save-JsonAtomically ([ordered]@{
            stage = if ($anyRunning) { 'capturing-parallel' } else { 'parallel-finished' }
            runRoot = $parallelRun
            queued = ($workers | Measure-Object assigned -Sum).Sum
            recoveredSupervisor = $true
            adaptiveBackgroundRadiusBlocks = $AdaptiveBackgroundRadiusBlocks
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
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        Remove-Item -LiteralPath $lockPath -Force
    }
}
