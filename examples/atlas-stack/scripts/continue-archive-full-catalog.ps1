[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [ValidateRange(1, 10)]
    [int]$CaptureAttempts = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Save-JsonUtf8([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 15
}

$queuePath = Join-Path $RunRoot 'live-catalog-all.json'
$statePath = Join-Path $RunRoot 'collector-state.json'
$preflightPidPath = Join-Path $RunRoot 'preflight.pid'
$capturedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured'
$readyRoot = Join-Path $RunRoot 'ready'
$captureQueuePath = Join-Path $RunRoot 'capture-queue.json'
$promotionManifestPath = Join-Path $RunRoot 'promotion-manifest.json'
$statusPath = Join-Path $RunRoot 'continuation-status.json'
$queueScript = Join-Path $RepositoryRoot 'scripts\new-archive-collector-queue.ps1'
$collectorScript = Join-Path $RepositoryRoot 'scripts\invoke-archive-collector.ps1'
$buildModScript = Join-Path $RepositoryRoot 'scripts\build-archive-coverage-mod.ps1'
$promoterScript = Join-Path $RepositoryRoot 'scripts\promote-archive-adaptive-batch.ps1'
$archiverScript = Join-Path $RepositoryRoot 'scripts\archive-collector-captures.ps1'

foreach ($path in @($queuePath, $statePath, $preflightPidPath, $queueScript, $collectorScript, $buildModScript, $promoterScript, $archiverScript)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required full-catalog input was not found: $path" }
}

Save-JsonUtf8 ([ordered]@{ stage = 'waiting-preflight'; updatedUtc = [DateTime]::UtcNow.ToString('o') }) $statusPath
$preflightPid = [int]([IO.File]::ReadAllText($preflightPidPath).Trim())
$preflightProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $preflightPid" -ErrorAction SilentlyContinue
$preflightCommandLine = if ($null -ne $preflightProcess) { [string]$preflightProcess.CommandLine } else { '' }
$isExpectedPreflight = $preflightCommandLine -match 'invoke-archive-collector\.ps1' -and
    $preflightCommandLine -match [regex]::Escape($statePath)
if ($isExpectedPreflight) {
    while ($null -ne (Get-Process -Id $preflightPid -ErrorAction SilentlyContinue)) { Start-Sleep -Seconds 30 }
} elseif ($null -ne $preflightProcess) {
    Write-Warning "Ignoring stale preflight PID $preflightPid; it now belongs to another process."
}
while (Test-Path -LiteralPath "$statePath.lock") { Start-Sleep -Seconds 5 }

$queue = Read-JsonUtf8 $queuePath
$unresolvedPreflight = @()
for ($preflightAttempt = 1; $preflightAttempt -le 3; $preflightAttempt++) {
    $state = Read-JsonUtf8 $statePath
    $byWarp = @{}
    foreach ($entry in @($state.entries)) { $byWarp[[string]$entry.normalizedWarp] = $entry }
    $unresolvedPreflight = @($queue.entries | Where-Object {
        $prior = $byWarp[[string]$_.normalizedWarp]
        $null -eq $prior -or [string]$prior.status -eq 'retryable'
    })
    if ($unresolvedPreflight.Count -eq 0) { break }
    if ($preflightAttempt -lt 3) {
        Save-JsonUtf8 ([ordered]@{
            stage = 'retrying-preflight'
            attempt = $preflightAttempt + 1
            unresolvedCount = $unresolvedPreflight.Count
            updatedUtc = [DateTime]::UtcNow.ToString('o')
        }) $statusPath
        try {
            & $collectorScript -QueuePath $queuePath -StatePath $statePath -CapturedRoot $capturedRoot `
                -MaxWarps 10000 -AdaptiveScan -PreflightOnly -AdaptiveBackgroundRadiusBlocks 0 `
                -DelayBetweenWarpsSeconds 2 -MinimumFreeGiB 100
        } catch {
            Write-Warning "Preflight retry $($preflightAttempt + 1) stopped early: $($_.Exception.Message)"
            Start-Sleep -Seconds 30
        }
    }
}
if ($unresolvedPreflight.Count -gt 0) {
    Save-JsonUtf8 ([ordered]@{
        stage = 'preflight-failed'
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        unresolvedCount = $unresolvedPreflight.Count
        sample = @($unresolvedPreflight | Select-Object -First 25 warp, normalizedWarp)
    }) $statusPath
    throw "Preflight left $($unresolvedPreflight.Count) unresolved catalog entries; capture will not start."
}

Save-JsonUtf8 ([ordered]@{ stage = 'building-collector'; updatedUtc = [DateTime]::UtcNow.ToString('o') }) $statusPath
& $buildModScript -Install

& $queueScript -ApiBase 'http://127.0.0.1:5297' -OutputPath $captureQueuePath `
    -CollectorStatePath $statePath -ArchiveCatalogPath 'C:\AtlasExample\Ingest\archive-sync\archive-warp-catalog.json' `
    -ArchiveCatalogOnly
$captureQueue = Read-JsonUtf8 $captureQueuePath
if (-not (Test-Path -LiteralPath $capturedRoot)) { New-Item -ItemType Directory -Path $capturedRoot -Force | Out-Null }

# Prove the two non-default vanilla dimension layouts before committing the collector to
# the full multi-day queue. Successful canaries remain in state and are skipped by the
# bulk pass, so this gate adds no duplicate capture work.
$state = Read-JsonUtf8 $statePath
$preflightByWarp = @{}
foreach ($entry in @($state.entries)) { $preflightByWarp[[string]$entry.normalizedWarp] = $entry }
$dimensionCanaries = @(
    $captureQueue.entries | Where-Object {
        $prior = $preflightByWarp[[string]$_.normalizedWarp]
        $position = if ($null -ne $prior -and $null -ne $prior.PSObject.Properties['archiveWarpPosition']) {
            $prior.archiveWarpPosition
        } else {
            $null
        }
        $null -ne $prior -and [string]$prior.status -eq 'preflight-eligible' -and
            [string]$_.warp -match '(?i)@(nether|end)$' -and
            $null -ne $position -and
            [Math]::Abs([double]$position.x) -le 29000000 -and
            [Math]::Abs([double]$position.z) -le 29000000
    } | Group-Object { if ([string]$_.warp -match '(?i)@nether$') { 'Nether' } else { 'End' } } |
        ForEach-Object {
            # Canary warps only need to prove dimension handling. Pick the candidate
            # nearest the origin so its adaptive probe cannot be rejected by the
            # Archive's /tppos +/-30M command guard. This is deliberately not a
            # catalog capture limit: the bulk pass remains unclamped and can retain
            # historical or exploited terrain beyond Minecraft's normal border.
            $_.Group | Sort-Object {
                $position = $preflightByWarp[[string]$_.normalizedWarp].archiveWarpPosition
                [Math]::Max([Math]::Abs([double]$position.x), [Math]::Abs([double]$position.z))
            } | Select-Object -First 1
        }
)
foreach ($canary in $dimensionCanaries) {
    $canarySucceeded = $false
    $resultStatus = 'missing-state'
    for ($canaryAttempt = 1; $canaryAttempt -le 3; $canaryAttempt++) {
        Save-JsonUtf8 ([ordered]@{
            stage = 'capturing-dimension-canary'
            warp = [string]$canary.warp
            attempt = $canaryAttempt
            updatedUtc = [DateTime]::UtcNow.ToString('o')
        }) $statusPath
        try {
            & $collectorScript -QueuePath $captureQueuePath -StatePath $statePath -CapturedRoot $capturedRoot `
                -Warp ([string]$canary.warp) -MaxWarps 1 -AdaptiveScan -AdaptiveBackgroundRadiusBlocks 0 `
                -DelayBetweenWarpsSeconds 2 -MinimumFreeGiB 100
        } catch {
            Write-Warning "Dimension canary attempt $canaryAttempt stopped early: $($_.Exception.Message)"
        }
        $state = Read-JsonUtf8 $statePath
        $result = @($state.entries | Where-Object { [string]$_.normalizedWarp -eq [string]$canary.normalizedWarp } | Select-Object -First 1)
        $resultStatus = if ($result.Count -eq 1) { [string]$result[0].status } else { 'missing-state' }
        if ($resultStatus -eq 'captured') { $canarySucceeded = $true; break }
        if ($canaryAttempt -lt 3) { Start-Sleep -Seconds 30 }
    }
    if (-not $canarySucceeded) {
        Save-JsonUtf8 ([ordered]@{
            stage = 'dimension-canary-failed'
            warp = [string]$canary.warp
            resultStatus = $resultStatus
            updatedUtc = [DateTime]::UtcNow.ToString('o')
        }) $statusPath
        throw "Dimension canary '$($canary.warp)' finished as '$resultStatus'; bulk capture will not start."
    }
}

for ($attempt = 1; $attempt -le $CaptureAttempts; $attempt++) {
    Save-JsonUtf8 ([ordered]@{
        stage = 'capturing'
        attempt = $attempt
        queueCount = [int]$captureQueue.queueCount
        updatedUtc = [DateTime]::UtcNow.ToString('o')
    }) $statusPath
    $capturePassError = ''
    try {
        & $collectorScript -QueuePath $captureQueuePath -StatePath $statePath -CapturedRoot $capturedRoot `
            -MaxWarps 10000 -AdaptiveScan -AdaptiveBackgroundRadiusBlocks 0 `
            -DelayBetweenWarpsSeconds 2 -MinimumFreeGiB 100
    } catch {
        $capturePassError = $_.Exception.Message
        Write-Warning "Capture pass $attempt stopped early: $capturePassError"
    }

    $state = Read-JsonUtf8 $statePath
    $retryable = @($state.entries | Where-Object { [string]$_.status -eq 'retryable' })
    $remaining = @($captureQueue.entries | Where-Object {
        $identity = [string]$_.normalizedWarp
        $entry = @($state.entries | Where-Object { [string]$_.normalizedWarp -eq $identity } | Select-Object -First 1)
        $entry.Count -eq 0 -or [string]$entry[0].status -in @('preflight-eligible', 'retryable')
    })
    if ($retryable.Count -eq 0 -and $remaining.Count -eq 0 -and
        [string]::IsNullOrWhiteSpace($capturePassError)) { break }
    if ($attempt -eq $CaptureAttempts) {
        Save-JsonUtf8 ([ordered]@{
            stage = 'capture-retries-exhausted'
            attempts = $CaptureAttempts
            retryableCount = $retryable.Count
            remainingCount = $remaining.Count
            capturePassError = $capturePassError
            sample = @($retryable | Select-Object -First 25 warp, error)
            updatedUtc = [DateTime]::UtcNow.ToString('o')
        }) $statusPath
        throw "Capture left $($retryable.Count) retryable and $($remaining.Count) remaining entries after $CaptureAttempts attempts."
    }
    Save-JsonUtf8 ([ordered]@{
        stage = 'retrying-capture'
        nextAttempt = $attempt + 1
        retryableCount = $retryable.Count
        remainingCount = $remaining.Count
        capturePassError = $capturePassError
        updatedUtc = [DateTime]::UtcNow.ToString('o')
    }) $statusPath
    Start-Sleep -Seconds 30
}

if (-not (Test-Path -LiteralPath $readyRoot)) { New-Item -ItemType Directory -Path $readyRoot -Force | Out-Null }
$archiveRoot = 'E:\AtlasExample\WorldDownloads\collector\example-catalog\captured'
Save-JsonUtf8 ([ordered]@{ stage = 'archiving'; updatedUtc = [DateTime]::UtcNow.ToString('o') }) $statusPath
$archiveResult = & $archiverScript -StatePath $statePath -StagedRoot $capturedRoot -ArchiveRoot $archiveRoot
Save-JsonUtf8 ([ordered]@{ stage = 'promoting'; updatedUtc = [DateTime]::UtcNow.ToString('o') }) $statusPath
$promotion = & $promoterScript -StatePath $statePath -CapturedRoot $archiveRoot `
    -ReadyRoot $readyRoot -ManifestPath $promotionManifestPath

Save-JsonUtf8 ([ordered]@{
    stage = 'ready-for-ingestion'
    promotedCount = [int]$promotion.promotedCount
    quarantinedCount = [int]$promotion.quarantinedCount
    updatedUtc = [DateTime]::UtcNow.ToString('o')
}) $statusPath
Write-Output "FULL-CATALOG READY promoted=$($promotion.promotedCount) quarantined=$($promotion.quarantinedCount)"
