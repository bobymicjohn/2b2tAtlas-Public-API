[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$WorkerProfile = 'collector-four',
    [ValidateRange(5, 180)]
    [int]$CheckpointTimeoutMinutes = 120
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$statusPath = Join-Path $RunRoot 'parallel-collector-status.json'
$priorityRoot = 'D:\AtlasExample\Ingest\manual-primary\spawn-november-2022-y254'
$priorityStatus = Join-Path $priorityRoot 'priority-status.json'
$priorityLog = Join-Path $priorityRoot 'priority.log'
$historicScript = Join-Path $PSScriptRoot 'invoke-historic-spawn-y254.ps1'
$resumeScript = Join-Path $PSScriptRoot 'resume-archive-parallel-supervisor.ps1'
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-PriorityStatus([string]$Stage, [string]$Message, [hashtable]$Details = @{}) {
    $record = [ordered]@{
        schemaVersion = 1
        stage = $Stage
        message = $Message
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        details = $Details
    }
    $partial = "$priorityStatus.$([guid]::NewGuid().ToString('N')).partial"
    [IO.File]::WriteAllText($partial, ($record | ConvertTo-Json -Depth 12), $utf8)
    Move-Item -LiteralPath $partial -Destination $priorityStatus -Force
}

function Add-PriorityLog([string]$Message) {
    [IO.File]::AppendAllText($priorityLog,
        ('[{0}] {1}' -f [DateTime]::UtcNow.ToString('o'), $Message) + [Environment]::NewLine, $utf8)
}

function Get-Descendants([int]$RootProcessId) {
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

function Stop-ValidatedProcessTree([int]$RootProcessId, [string]$RequiredCommandPattern) {
    $root = Get-CimInstance Win32_Process -Filter "ProcessId=$RootProcessId" -ErrorAction SilentlyContinue
    if ($null -eq $root -or [string]$root.CommandLine -notmatch $RequiredCommandPattern) {
        throw "PID $RootProcessId is not the expected process."
    }
    $ids = @(Get-Descendants $RootProcessId)
    Stop-Process -Id $RootProcessId -Force
    Start-Sleep -Milliseconds 250
    foreach ($id in $ids) {
        if ($id -ne $RootProcessId) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    }
    return $ids.Count
}

foreach ($required in @($statusPath, $historicScript, $resumeScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file was not found: $required" }
}
if (-not (Test-Path -LiteralPath $priorityRoot)) { New-Item -ItemType Directory -Path $priorityRoot -Force | Out-Null }

$resumeStarted = $false
$parallelRunRoot = ''
try {
    $parallel = [IO.File]::ReadAllText($statusPath, $utf8) | ConvertFrom-Json
    $parallelRunRoot = [string]$parallel.runRoot
    $worker = @($parallel.workers | Where-Object { [string]$_.profile -eq $WorkerProfile })
    if ($worker.Count -ne 1 -or -not [bool]$worker[0].running) {
        throw "Running worker profile '$WorkerProfile' was not found."
    }
    $worker = $worker[0]
    $workerPid = [int]$worker.processId
    $workerStatePath = Join-Path ([IO.Path]::GetDirectoryName([string]$worker.stdout)) 'state.json'
    $workerLog = [string]$worker.stdout
    $installRoot = if ($WorkerProfile -eq 'atlas-owner') {
        'C:\AtlasExample\Ingest\archive-sync\collector'
    } else {
        Join-Path 'C:\AtlasExample\Ingest\archive-sync\collectors' $WorkerProfile
    }

    $workerProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$workerPid"
    if ($null -eq $workerProcess -or [string]$workerProcess.CommandLine -notmatch 'invoke-archive-collector\.ps1') {
        throw "Worker PID $workerPid no longer owns an Archive collector."
    }
    $logLines = @(Get-Content -LiteralPath $workerLog)
    $warpLines = @($logLines | Where-Object { $_ -match '^WARP ' })
    if ($warpLines.Count -eq 0) { throw 'The worker log does not identify its current warp.' }
    $currentWarp = ([string]$warpLines[-1]).Substring(5)

    $watchers = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
        [string]$_.CommandLine -match 'invoke-historic-spawn-y254\.ps1' -and [int]$_.ProcessId -ne $PID
    })
    foreach ($watcher in $watchers) { Stop-Process -Id ([int]$watcher.ProcessId) -Force -ErrorAction SilentlyContinue }

    $supervisors = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
        [string]$_.CommandLine -match 'resume-archive-parallel-supervisor\.ps1'
    })
    if ($supervisors.Count -ne 1) { throw "Expected one parallel supervisor, found $($supervisors.Count)." }
    $supervisorPid = [int]$supervisors[0].ProcessId
    [void](Stop-ValidatedProcessTree $supervisorPid 'resume-archive-parallel-supervisor\.ps1')
    $parallelLock = Join-Path $RunRoot 'collector-state.json.parallel.lock'
    if (Test-Path -LiteralPath $parallelLock -PathType Leaf) {
        $stale = "$parallelLock.stale-priority-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).disabled"
        Move-Item -LiteralPath $parallelLock -Destination $stale
    }

    Write-PriorityStatus 'waiting-for-checkpoint' "Waiting for $WorkerProfile to finish $currentWarp." @{
        workerPid = $workerPid
        workerProfile = $WorkerProfile
        currentWarp = $currentWarp
    }
    Add-PriorityLog "Draining worker $WorkerProfile after $currentWarp; four other workers remain active."
    $escaped = [regex]::Escape($currentWarp)
    $checkpointPattern = "^(?:CAPTURED|MISSING|WARNING: RETRYABLE) $escaped(?:\s|:|$)"
    $deadline = (Get-Date).AddMinutes($CheckpointTimeoutMinutes)
    do {
        if ($null -eq (Get-Process -Id $workerPid -ErrorAction SilentlyContinue)) {
            throw "Worker $WorkerProfile exited before its checkpoint was observed."
        }
        $tail = @(Get-Content -LiteralPath $workerLog -Tail 30 -ErrorAction SilentlyContinue)
        $checkpoint = @($tail | Where-Object { $_ -match $checkpointPattern })
        if ($checkpoint.Count -gt 0) { break }
        Start-Sleep -Milliseconds 100
    } while ((Get-Date) -lt $deadline)
    if ($checkpoint.Count -eq 0) { throw "Timed out waiting for $currentWarp to checkpoint." }

    [void](Stop-ValidatedProcessTree $workerPid 'invoke-archive-collector\.ps1')
    Start-Sleep -Seconds 1
    $workerLock = "$workerStatePath.lock"
    if (Test-Path -LiteralPath $workerLock -PathType Leaf) {
        $stale = "$workerLock.stale-priority-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss')).disabled"
        Move-Item -LiteralPath $workerLock -Destination $stale
    }

    Write-PriorityStatus 'running-y254' 'The dedicated collector has started the exact Y254 spawn capture.' @{
        workerProfile = $WorkerProfile
        installRoot = $installRoot
    }
    & $historicScript -InstallRootOverride $installRoot
    if ($LASTEXITCODE -ne 0) { throw "Historic Y254 task failed with exit code $LASTEXITCODE." }
    Write-PriorityStatus 'resuming-collector' 'Y254 completed; restoring the five-worker collector supervisor.'
} catch {
    Add-PriorityLog "FAILED $($_.Exception.Message)"
    Write-PriorityStatus 'failed' $_.Exception.Message
    throw
} finally {
    if (-not [string]::IsNullOrWhiteSpace($parallelRunRoot)) {
        $resumeOut = Join-Path $RunRoot "parallel-resume-after-y254-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).out.log"
        $resumeErr = $resumeOut.Replace('.out.log', '.err.log')
        $arguments = @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $resumeScript,
            '-RunRoot', $RunRoot,
            '-ParallelRunRoot', $parallelRunRoot,
            '-AdaptiveBackgroundRadiusBlocks', '0'
        )
        $resume = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden `
            -RedirectStandardOutput $resumeOut -RedirectStandardError $resumeErr -PassThru
        $resumeStarted = $true
        Add-PriorityLog "Started replacement supervisor PID $($resume.Id)."
    }
}

if ($resumeStarted) {
    Write-PriorityStatus 'complete' 'Y254 completed and the five-worker collector supervisor was restarted.'
}
