[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$StagedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [string]$ArchiveRoot = 'E:\AtlasExample\WorldDownloads\collector\example-catalog\captured',
    [string]$ApiBase = 'http://127.0.0.1:5297',
    [string]$WorkerConfigPath = 'C:\AtlasExample\Ingest\config\worker.json',
    [ValidateRange(1, 10)]
    [int]$CapturePasses = 3,
    [ValidateRange(10, 600)]
    [int]$PollSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$utf8 = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return Read-AtlasJsonWithRetry -Path $Path
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 24
}

function Normalize-Warp([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value.Trim() -replace '^/warp\s+', '' -replace '\s+', '_').ToLowerInvariant()
}

function Test-FinalCapture([object]$Record) {
    if ($null -eq $Record -or [string]$Record.status -notin @('captured', 'ready')) { return $false }
    if ($null -eq $Record.PSObject.Properties['adaptive'] -or $null -eq $Record.adaptive) { return $false }
    return $null -ne $Record.adaptive.PSObject.Properties['standardVersion'] -and
        [int]$Record.adaptive.standardVersion -ge 2 -and
        $null -ne $Record.adaptive.PSObject.Properties['componentSelection']
}

function Set-Status([string]$Stage, [hashtable]$Details = @{}) {
    $value = [ordered]@{ stage = $Stage; updatedUtc = [DateTime]::UtcNow.ToString('o') }
    foreach ($key in $Details.Keys) { $value[$key] = $Details[$key] }
    Save-JsonAtomically $value $finalizerStatusPath
}

function Invoke-RollingHandoffSafe {
    try {
        return & $rollingScript -RunRoot $RunRoot -RepositoryRoot $RepositoryRoot `
            -StatePath $statePath -StagedRoot $StagedRoot -ArchiveRoot $ArchiveRoot `
            -ApiBase $ApiBase -WorkerConfigPath $WorkerConfigPath -ImportStatePath $importStatePath `
            -MinimumStableSeconds 0 -DelayBetweenJobsSeconds 2
    } catch {
        Write-Warning "Rolling production handoff will retry: $($_.Exception.Message)"
        return [pscustomobject]@{ stage = 'failed'; submitted = 0; error = $_.Exception.Message }
    }
}

function Wait-ParallelPass {
    do {
        if (-not (Test-Path -LiteralPath $parallelStatusPath -PathType Leaf)) {
            Start-Sleep -Seconds $PollSeconds
            continue
        }
        $parallel = Read-JsonUtf8 $parallelStatusPath
        if ([string]$parallel.stage -eq 'parallel-finished' -and
            -not (Test-Path -LiteralPath "$statePath.parallel.lock" -PathType Leaf)) { return }
        $rolling = Invoke-RollingHandoffSafe
        Set-Status 'waiting-for-collector' @{
            parallelStage = [string]$parallel.stage
            runningWorkers = @($parallel.workers | Where-Object {
                $null -ne $_.PSObject.Properties['running'] -and [bool]$_.running
            }).Count
            queuedThisPass = if ($null -ne $parallel.PSObject.Properties['queued']) { [int]$parallel.queued } else { 0 }
            rollingStage = [string]$rolling.stage
            rollingSubmitted = if ($null -ne $rolling.PSObject.Properties['submitted']) { [int]$rolling.submitted } else { 0 }
        }
        Start-Sleep -Seconds $PollSeconds
    } while ($true)
}

function Get-Unresolved {
    $queue = Read-JsonUtf8 $queuePath
    $state = Read-JsonUtf8 $statePath
    $byIdentity = @{}
    foreach ($entry in @($state.entries)) {
        $identity = Normalize-Warp ([string]$entry.warp)
        $byIdentity[$identity] = $entry
    }
    return @($queue.entries | Where-Object {
        $identity = Normalize-Warp ([string]$_.warp)
        $prior = $byIdentity[$identity]
        if ($null -eq $prior) { return $true }
        $status = [string]$prior.status
        if ($status -eq 'missing') { return $false }
        return -not (Test-FinalCapture $prior)
    })
}

$statePath = Join-Path $RunRoot 'collector-state.json'
$queuePath = Join-Path $RunRoot 'capture-queue.json'
$parallelStatusPath = Join-Path $RunRoot 'parallel-collector-status.json'
$importStatePath = Join-Path $RunRoot 'production-import-state.json'
$finalizerStatusPath = Join-Path $RunRoot 'final-standard-handoff-status.json'
$lockPath = Join-Path $RunRoot 'final-standard-handoff.lock'
$parallelScript = Join-Path $RepositoryRoot 'scripts\invoke-archive-parallel-collector.ps1'
$rollingScript = Join-Path $RepositoryRoot 'scripts\invoke-archive-rolling-handoff.ps1'
foreach ($required in @($statePath, $queuePath, $parallelScript, $rollingScript, $WorkerConfigPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required finalizer input was not found: $required" }
}

$lock = $null
if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
    $lockOwner = @()
    try {
        $lockOwner = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
            [int]$_.ProcessId -ne $PID -and
            -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
            [string]$_.CommandLine -match '(?i)wait-and-finalize-archive-parallel\.ps1' -and
            ([string]$_.CommandLine).IndexOf($RunRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
    } catch {
        throw "Could not verify the owner of final-standard handoff lock $lockPath`: $($_.Exception.Message)"
    }
    if ($lockOwner.Count -eq 0) {
        $stalePath = "$lockPath.stale-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$PID"
        Move-Item -LiteralPath $lockPath -Destination $stalePath
        Write-Warning "Recovered orphaned final-standard handoff lock: $stalePath"
    }
}
try {
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
} catch [IO.IOException] { throw "Another final-standard handoff already owns $lockPath" }

try {
    $initialRolling = Invoke-RollingHandoffSafe
    Set-Status 'rolling-backlog' @{
        rollingStage = [string]$initialRolling.stage
        submitted = if ($null -ne $initialRolling.PSObject.Properties['submitted']) { [int]$initialRolling.submitted } else { 0 }
    }
    for ($pass = 1; $pass -le $CapturePasses; $pass++) {
        Wait-ParallelPass
        $unresolved = @(Get-Unresolved)
        if ($unresolved.Count -eq 0) { break }
        if ($pass -ge $CapturePasses) {
            Set-Status 'blocked-unresolved' @{ pass = $pass; unresolved = $unresolved.Count; sample = @($unresolved | Select-Object -First 25 warp) }
            throw "Final-standard capture still has $($unresolved.Count) unresolved entries after $pass passes."
        }
        Set-Status 'retrying-unresolved' @{ completedPass = $pass; unresolved = $unresolved.Count }
        & $parallelScript -RunRoot $RunRoot -RepositoryRoot $RepositoryRoot -CapturedRoot $StagedRoot `
            -MaxWorkers 6 -MaxWarpsPerWorker 10000 -PollSeconds 15 -WorkerStartDelaySeconds 45 `
            -MaxWorkerRestarts 10000 -WorkerRestartDelaySeconds 300 -RecheckRetryable
    }

    $unresolved = @(Get-Unresolved)
    if ($unresolved.Count -ne 0) { throw "Unresolved audit changed unexpectedly: $($unresolved.Count)" }
    $finalRolling = Invoke-RollingHandoffSafe
    Set-Status 'submitted-to-production' @{
        rollingStage = [string]$finalRolling.stage
        submitted = if ($null -ne $finalRolling.PSObject.Properties['submitted']) { [int]$finalRolling.submitted } else { 0 }
        importState = $importStatePath
    }
} catch {
    if (-not (Test-Path -LiteralPath $finalizerStatusPath -PathType Leaf) -or
        [string](Read-JsonUtf8 $finalizerStatusPath).stage -notlike 'blocked-*') {
        Set-Status 'failed' @{ error = $_.Exception.Message }
    }
    throw
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force }
}
