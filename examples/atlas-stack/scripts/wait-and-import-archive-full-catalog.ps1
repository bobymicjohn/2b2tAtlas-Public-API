[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$ApiBase = 'http://127.0.0.1:5297',
    [string]$WorkerConfigPath = 'C:\AtlasExample\Ingest\config\worker.json',
    [ValidateRange(10, 3600)]
    [int]$PollSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Save-JsonUtf8([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 12
}

function Set-HandoffStatus([string]$Stage, [hashtable]$Details) {
    $status = [ordered]@{
        stage = $Stage
        updatedUtc = [DateTime]::UtcNow.ToString('o')
    }
    foreach ($key in $Details.Keys) { $status[$key] = $Details[$key] }
    Save-JsonUtf8 $status $handoffStatusPath
}

$collectorPidPath = Join-Path $RunRoot 'continuation.pid'
$continuationStatusPath = Join-Path $RunRoot 'continuation-status.json'
$promotionManifestPath = Join-Path $RunRoot 'promotion-manifest.json'
$readyRoot = Join-Path $RunRoot 'ready'
$importStatePath = Join-Path $RunRoot 'production-import-state.json'
$handoffStatusPath = Join-Path $RunRoot 'production-handoff-status.json'
$handoffLockPath = Join-Path $RunRoot 'production-handoff.lock'
$importerScript = Join-Path $RepositoryRoot 'scripts\import-archive-inbox.ps1'

foreach ($required in @($collectorPidPath, $continuationStatusPath, $WorkerConfigPath, $importerScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required production handoff input was not found: $required"
    }
}

$lock = $null
try {
    $lock = [IO.File]::Open($handoffLockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
} catch [IO.IOException] {
    throw "Another production handoff watcher is already registered: $handoffLockPath"
}

try {
    $collectorProcessId = [int]([IO.File]::ReadAllText($collectorPidPath).Trim())
    Set-HandoffStatus 'waiting-for-collector' @{ collectorPid = $collectorProcessId; apiBase = $ApiBase }
    Write-Output "Waiting for full-catalog collector PID $collectorProcessId."

    while ($null -ne (Get-Process -Id $collectorProcessId -ErrorAction SilentlyContinue)) {
        Start-Sleep -Seconds $PollSeconds
    }

    # The continuation writes this state only after capture retries are exhausted and the
    # high-confidence promotion manifest has been committed atomically.
    $continuation = Read-JsonUtf8 $continuationStatusPath
    if ([string]$continuation.stage -ne 'ready-for-ingestion') {
        Set-HandoffStatus 'blocked' @{
            collectorPid = $collectorProcessId
            continuationStage = [string]$continuation.stage
            reason = 'Collector did not complete and promote a validated batch.'
        }
        throw "Collector exited at stage '$($continuation.stage)'; production import will not start."
    }

    if (-not (Test-Path -LiteralPath $promotionManifestPath -PathType Leaf)) {
        Set-HandoffStatus 'blocked' @{ reason = 'Promotion manifest is missing.' }
        throw "Promotion manifest was not found: $promotionManifestPath"
    }
    $promotion = Read-JsonUtf8 $promotionManifestPath
    if ([bool]$promotion.dryRun) {
        Set-HandoffStatus 'blocked' @{ reason = 'Promotion manifest is a dry run.' }
        throw 'Production import will not accept a dry-run promotion manifest.'
    }
    $promoted = @($promotion.promoted)
    if ([int]$promotion.promotedCount -ne $promoted.Count) {
        Set-HandoffStatus 'blocked' @{ reason = 'Promotion manifest count does not match its entries.' }
        throw 'Promotion manifest count does not match its entries.'
    }
    foreach ($entry in $promoted) {
        $destination = [IO.Path]::GetFullPath([string]$entry.destination)
        $readyPrefix = [IO.Path]::GetFullPath($readyRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $destination.StartsWith($readyPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $destination -PathType Leaf) -or
            -not (Test-Path -LiteralPath "$destination.metadata.json" -PathType Leaf)) {
            Set-HandoffStatus 'blocked' @{ reason = "Promoted artifact is missing or outside ready root: $destination" }
            throw "Promoted artifact is missing or outside ready root: $destination"
        }
    }

    if ($promoted.Count -eq 0) {
        Set-HandoffStatus 'complete' @{ accepted = 0; failed = 0; quarantined = [int]$promotion.quarantinedCount }
        Write-Output 'Promotion completed with no eligible artifacts; nothing was submitted to production.'
        exit 0
    }

    # Verify that the loopback production API is responding before crossing the intake boundary.
    $probe = Invoke-WebRequest -UseBasicParsing -Uri ($ApiBase.TrimEnd('/') + '/api/locations') -TimeoutSec 30
    if ($probe.StatusCode -ne 200) { throw "Production Atlas API probe returned HTTP $($probe.StatusCode)." }

    Set-HandoffStatus 'importing-to-production' @{
        promoted = $promoted.Count
        quarantined = [int]$promotion.quarantinedCount
        apiBase = $ApiBase
    }
    Write-Output "Submitting $($promoted.Count) promoted Archive WDLs to the production Atlas pipeline."
    & $importerScript -ReadyRoot $readyRoot -ApiBase $ApiBase -WorkerConfigPath $WorkerConfigPath `
        -StatePath $importStatePath -MinimumStableSeconds 0 -DelayBetweenJobsSeconds 2

    $acceptedCount = 0
    $failedCount = 0
    if (Test-Path -LiteralPath $importStatePath -PathType Leaf) {
        $importState = Read-JsonUtf8 $importStatePath
        foreach ($property in @($importState.PSObject.Properties)) {
            if ([string]$property.Value.status -eq 'accepted') { $acceptedCount++ }
            elseif ([string]$property.Value.status -eq 'failed') { $failedCount++ }
        }
    }

    $finalStage = if ($failedCount -eq 0) { 'submitted-to-production' } else { 'submitted-with-failures' }
    Set-HandoffStatus $finalStage @{
        promoted = $promoted.Count
        accepted = $acceptedCount
        failed = $failedCount
        quarantined = [int]$promotion.quarantinedCount
        importStatePath = $importStatePath
    }
    Write-Output "Production handoff finished: accepted=$acceptedCount failed=$failedCount quarantined=$($promotion.quarantinedCount)."
    if ($failedCount -gt 0) { exit 2 }
} catch {
    if (-not (Test-Path -LiteralPath $handoffStatusPath -PathType Leaf) -or
        [string](Read-JsonUtf8 $handoffStatusPath).stage -notin @('blocked', 'submitted-with-failures')) {
        Set-HandoffStatus 'failed' @{ error = $_.Exception.Message }
    }
    throw
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $handoffLockPath -PathType Leaf) {
        Remove-Item -LiteralPath $handoffLockPath -Force
    }
}
