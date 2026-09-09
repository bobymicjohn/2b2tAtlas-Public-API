[CmdletBinding()]
param(
    [string]$InitialRunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$WeeklyRoot = 'C:\AtlasExample\Ingest\archive-sync\weekly',
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$CatalogPath = 'C:\AtlasExample\Ingest\archive-sync\archive-warp-catalog.json',
    [string]$CapturedRoot = 'D:\AtlasExample\Ingest\archive-captures\weekly\captured',
    [string]$ApiBase = 'http://127.0.0.1:5297',
    [string]$WorkerConfigPath = 'C:\AtlasExample\Ingest\config\worker.json',
    [ValidateRange(1, 10)]
    [int]$CaptureAttempts = 3,
    [switch]$GateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Save-JsonUtf8([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 15
}

function Normalize-Warp([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value.Trim() -replace '^/warp\s+', '' -replace '\s+', '_').ToLowerInvariant()
}

function Set-WeeklyStatus([string]$Stage, [hashtable]$Details = @{}) {
    $value = [ordered]@{
        schemaVersion = 1
        stage = $Stage
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        runId = $runId
    }
    foreach ($key in $Details.Keys) { $value[$key] = $Details[$key] }
    Save-JsonUtf8 $value $statusPath
}

function Get-TerminalIdentitySet([object]$State) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    if ($null -eq $State) { return $set }
    foreach ($entry in @($State.entries)) {
        if ([string]$entry.status -in @(
            'accepted', 'complete', 'captured', 'ready', 'imported',
            'missing', 'skipped-inside-background')) {
            $identity = Normalize-Warp ([string]$entry.warp)
            if ($identity) { [void]$set.Add($identity) }
        }
    }
    return $set
}

function Save-WeeklyState([object]$State) {
    $State.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Save-JsonUtf8 $State $weeklyStatePath
}

$catalogScript = Join-Path $RepositoryRoot 'scripts\get-archive-warp-catalog.ps1'
$queueScript = Join-Path $RepositoryRoot 'scripts\new-archive-collector-queue.ps1'
$collectorScript = Join-Path $RepositoryRoot 'scripts\invoke-archive-collector.ps1'
$promoterScript = Join-Path $RepositoryRoot 'scripts\promote-archive-adaptive-batch.ps1'
$archiverScript = Join-Path $RepositoryRoot 'scripts\archive-collector-captures.ps1'
$importerScript = Join-Path $RepositoryRoot 'scripts\import-archive-inbox.ps1'
foreach ($required in @($catalogScript, $queueScript, $collectorScript, $promoterScript, $archiverScript, $importerScript, $WorkerConfigPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Weekly Archive sync dependency was not found: $required" }
}

if (-not (Test-Path -LiteralPath $WeeklyRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $WeeklyRoot -Force | Out-Null
}
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$runRoot = Join-Path $WeeklyRoot "runs\$runId"
$logsRoot = Join-Path $WeeklyRoot 'logs'
$capturedRoot = [IO.Path]::GetFullPath($CapturedRoot)
$readyRoot = Join-Path $WeeklyRoot 'ready'
$statusPath = Join-Path $WeeklyRoot 'weekly-status.json'
$weeklyStatePath = Join-Path $WeeklyRoot 'collector-state.json'
$importStatePath = Join-Path $WeeklyRoot 'production-import-state.json'
$lockPath = Join-Path $WeeklyRoot 'weekly-sync.lock'
foreach ($directory in @($runRoot, $logsRoot, $capturedRoot, $readyRoot)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

$lock = $null
try {
    try {
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    } catch [IO.IOException] {
        $existingLock = Read-JsonUtf8 $lockPath
        $existingPid = if ($null -ne $existingLock) { [int]$existingLock.pid } else { 0 }
        if ($existingPid -gt 0 -and $null -ne (Get-Process -Id $existingPid -ErrorAction SilentlyContinue)) {
            Set-WeeklyStatus 'deferred-already-running' @{ activePid = $existingPid }
            exit 0
        }
        $stale = "$lockPath.stale-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
        Move-Item -LiteralPath $lockPath -Destination $stale
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    }
    $lockBytes = [Text.Encoding]::UTF8.GetBytes((@{ pid = $PID; startedUtc = [DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Compress))
    $lock.Write($lockBytes, 0, $lockBytes.Length)
    $lock.Flush()

    $otherCollector = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -ne $PID -and [string]$_.CommandLine -match
            '(?i)(invoke-archive-collector|get-archive-warp-catalog|continue-archive-full-catalog)\.ps1'
    } | Select-Object -First 1)
    if ($otherCollector.Count -gt 0) {
        Set-WeeklyStatus 'deferred-active-collector' @{ activePid = [int]$otherCollector[0].ProcessId }
        exit 0
    }

    $initialContinuation = Read-JsonUtf8 (Join-Path $InitialRunRoot 'continuation-status.json')
    $initialHandoff = Read-JsonUtf8 (Join-Path $InitialRunRoot 'production-handoff-status.json')
    $continuationStage = if ($null -ne $initialContinuation) { [string]$initialContinuation.stage } else { 'missing' }
    $handoffStage = if ($null -ne $initialHandoff) { [string]$initialHandoff.stage } else { 'missing' }
    if ($continuationStage -ne 'ready-for-ingestion' -or
        $handoffStage -notin @('complete', 'submitted-to-production')) {
        Set-WeeklyStatus 'deferred-initial-pass' @{
            continuationStage = $continuationStage
            handoffStage = $handoffStage
            reason = 'The initial capture and production handoff must finish before weekly discovery starts.'
        }
        exit 0
    }

    if ($GateOnly) {
        Set-WeeklyStatus 'gate-passed' @{ continuationStage = $continuationStage; handoffStage = $handoffStage }
        exit 0
    }

    if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) { throw "Archive catalog was not found: $CatalogPath" }
    $initialState = Read-JsonUtf8 (Join-Path $InitialRunRoot 'collector-state.json')
    if ($null -eq $initialState) { throw 'Initial collector state is missing or unreadable.' }
    if (-not (Test-Path -LiteralPath $weeklyStatePath -PathType Leaf)) {
        Save-JsonUtf8 ([ordered]@{ schemaVersion = 1; updatedUtc = [DateTime]::UtcNow.ToString('o'); entries = @() }) $weeklyStatePath
    }

    $catalogBefore = Read-JsonUtf8 $CatalogPath
    $knownBefore = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($catalogBefore.entries)) { [void]$knownBefore.Add((Normalize-Warp ([string]$entry.warp))) }

    Set-WeeklyStatus 'refreshing-catalog' @{ knownWarps = $knownBefore.Count }
    & $catalogScript -CatalogPath $CatalogPath -MaxNewWarps 10000 -MaxPages 500 -MaxDepth 10

    $catalogAfter = Read-JsonUtf8 $CatalogPath
    $newIdentities = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($catalogAfter.entries)) {
        $identity = Normalize-Warp ([string]$entry.warp)
        if (-not $knownBefore.Contains($identity)) { [void]$newIdentities.Add($identity) }
    }

    $candidateQueuePath = Join-Path $runRoot 'candidate-queue.json'
    $weeklyQueuePath = Join-Path $runRoot 'weekly-queue.json'
    & $queueScript -ApiBase $ApiBase -OutputPath $candidateQueuePath -CollectorStatePath $weeklyStatePath `
        -ArchiveCatalogPath $CatalogPath -ArchiveCatalogOnly
    $candidateQueue = Read-JsonUtf8 $candidateQueuePath
    $weeklyState = Read-JsonUtf8 $weeklyStatePath
    $initialTerminal = Get-TerminalIdentitySet $initialState
    $weeklyTerminal = Get-TerminalIdentitySet $weeklyState

    $selected = @($candidateQueue.entries | Where-Object {
        $identity = Normalize-Warp ([string]$_.warp)
        -not $initialTerminal.Contains($identity) -and -not $weeklyTerminal.Contains($identity)
    })
    $weeklyQueue = [ordered]@{
        schemaVersion = 1
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        runId = $runId
        semantics = 'New or unresolved Archive warps absent from the completed initial and weekly states.'
        newlyDiscovered = $newIdentities.Count
        queueCount = $selected.Count
        conflicts = @($candidateQueue.conflicts)
        entries = $selected
    }
    Save-JsonUtf8 $weeklyQueue $weeklyQueuePath

    if ($selected.Count -gt 0) {
        Set-WeeklyStatus 'preflighting-new-warps' @{ newlyDiscovered = $newIdentities.Count; queued = $selected.Count }
        & $collectorScript -QueuePath $weeklyQueuePath -StatePath $weeklyStatePath -CapturedRoot $capturedRoot `
            -MaxWarps 10000 -AdaptiveScan -PreflightOnly -AdaptiveBackgroundRadiusBlocks 0 `
            -DelayBetweenWarpsSeconds 2 -MinimumFreeGiB 100

        for ($attempt = 1; $attempt -le $CaptureAttempts; $attempt++) {
            $weeklyState = Read-JsonUtf8 $weeklyStatePath
            $stateMap = @{}
            foreach ($entry in @($weeklyState.entries)) { $stateMap[(Normalize-Warp ([string]$entry.warp))] = $entry }
            $remaining = @($selected | Where-Object {
                $identity = Normalize-Warp ([string]$_.warp)
                $prior = $stateMap[$identity]
                $null -eq $prior -or [string]$prior.status -in @('preflight-eligible', 'retryable')
            })
            if ($remaining.Count -eq 0) { break }
            Set-WeeklyStatus 'capturing-new-warps' @{
                newlyDiscovered = $newIdentities.Count
                queued = $selected.Count
                remaining = $remaining.Count
                attempt = $attempt
            }
            & $collectorScript -QueuePath $weeklyQueuePath -StatePath $weeklyStatePath -CapturedRoot $capturedRoot `
                -MaxWarps 10000 -AdaptiveScan -AdaptiveBackgroundRadiusBlocks 0 `
                -DelayBetweenWarpsSeconds 2 -MinimumFreeGiB 100
            if ($attempt -lt $CaptureAttempts) { Start-Sleep -Seconds 30 }
        }
    }

    $archiveRoot = 'E:\AtlasExample\WorldDownloads\collector\weekly\captured'
    Set-WeeklyStatus 'archiving-validated-captures' @{ newlyDiscovered = $newIdentities.Count; queued = $selected.Count }
    $archiveResult = & $archiverScript -StatePath $weeklyStatePath -StagedRoot $capturedRoot -ArchiveRoot $archiveRoot

    $manifestPath = Join-Path $runRoot 'promotion-manifest.json'
    Set-WeeklyStatus 'promoting-validated-captures' @{ newlyDiscovered = $newIdentities.Count; queued = $selected.Count }
    $promotion = & $promoterScript -StatePath $weeklyStatePath -CapturedRoot $archiveRoot `
        -ReadyRoot $readyRoot -ManifestPath $manifestPath

    $failedImports = 0
    $acceptedImports = 0
    if ([int]$promotion.promotedCount -gt 0) {
        Set-WeeklyStatus 'submitting-to-production' @{
            promoted = [int]$promotion.promotedCount
            quarantined = [int]$promotion.quarantinedCount
        }
        & $importerScript -ReadyRoot $readyRoot -ApiBase $ApiBase -WorkerConfigPath $WorkerConfigPath `
            -StatePath $importStatePath -MinimumStableSeconds 0 -DelayBetweenJobsSeconds 2

        $importState = Read-JsonUtf8 $importStatePath
        $weeklyState = Read-JsonUtf8 $weeklyStatePath
        $byIdentity = @{}
        foreach ($entry in @($weeklyState.entries)) { $byIdentity[(Normalize-Warp ([string]$entry.warp))] = $entry }
        foreach ($promoted in @($promotion.promoted)) {
            $digest = ([string]$promoted.sha256).ToLowerInvariant()
            $receipt = if ($null -ne $importState) { $importState.PSObject.Properties[$digest] } else { $null }
            if ($null -ne $receipt -and [string]$receipt.Value.status -eq 'accepted') {
                $acceptedImports++
                $identity = Normalize-Warp ([string]$promoted.warp)
                if ($byIdentity.ContainsKey($identity)) {
                    $byIdentity[$identity].status = 'imported'
                    $byIdentity[$identity] | Add-Member -NotePropertyName importedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o')) -Force
                }
            } else {
                $failedImports++
            }
        }
        $weeklyState.entries = @($byIdentity.Values | Sort-Object normalizedWarp)
        Save-WeeklyState $weeklyState
    }

    $finalStage = if ($failedImports -gt 0) { 'completed-with-import-failures' } else { 'complete' }
    Set-WeeklyStatus $finalStage @{
        catalogWarps = @($catalogAfter.entries).Count
        newlyDiscovered = $newIdentities.Count
        queued = $selected.Count
        promoted = [int]$promotion.promotedCount
        quarantined = [int]$promotion.quarantinedCount
        accepted = $acceptedImports
        failed = $failedImports
        nextAction = if ($failedImports -gt 0) { 'Retry failed production submissions on the next weekly run.' } else { 'Wait for the next scheduled catalog check.' }
    }
    if ($failedImports -gt 0) { throw "$failedImports promoted Archive WDL(s) were not accepted by production." }
} catch {
    if ($null -eq (Read-JsonUtf8 $statusPath) -or [string](Read-JsonUtf8 $statusPath).stage -notmatch '^completed-with-') {
        Set-WeeklyStatus 'failed' @{ error = $_.Exception.Message }
    }
    throw
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force }
}
