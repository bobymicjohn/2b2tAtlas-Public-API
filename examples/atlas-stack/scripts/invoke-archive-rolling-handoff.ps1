[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$StatePath = '',
    [string]$StagedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [string]$ArchiveRoot = 'E:\AtlasExample\WorldDownloads\collector\example-catalog\captured',
    [string]$ReadyRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\ready',
    [string]$ApiBase = 'http://127.0.0.1:5297',
    [string]$WorkerConfigPath = 'C:\AtlasExample\Ingest\config\worker.json',
    [string]$ImportStatePath = '',
    [string]$RollingStatePath = '',
    [string]$StatusPath = '',
    [ValidateRange(0, 300)]
    [int]$MinimumStableSeconds = 0,
    [ValidateRange(0, 300)]
    [int]$DelayBetweenJobsSeconds = 2
)

# An already-running finalizer can still pass the pre-migration root.
# Resolve that exact legacy collector prefix locally until all callers recycle.
$legacyCollectorRoot = 'X:\AtlasExample\WorldDownloads\collector\'
if ($ArchiveRoot.StartsWith($legacyCollectorRoot, [StringComparison]::OrdinalIgnoreCase)) {
    $ArchiveRoot = 'E:\AtlasExample\WorldDownloads\collector\' + $ArchiveRoot.Substring($legacyCollectorRoot.Length)
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$utf8 = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return Read-AtlasJsonWithRetry -Path $Path -AllowMissing
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 24
}

function Test-FinalCapture([object]$Entry) {
    if ($null -eq $Entry -or [string]$Entry.status -notin @('captured', 'ready')) { return $false }
    $adaptiveProperty = $Entry.PSObject.Properties['adaptive']
    if ($null -eq $adaptiveProperty -or $null -eq $adaptiveProperty.Value) { return $false }
    $adaptive = $adaptiveProperty.Value
    return $null -ne $adaptive.PSObject.Properties['standardVersion'] -and
        [int]$adaptive.standardVersion -ge 2 -and
        $null -ne $adaptive.PSObject.Properties['componentSelection'] -and
        $null -ne $adaptive.PSObject.Properties['footprintArtifact']
}

function Test-ForeignServer([object]$Entry) {
    $warp = [string]$Entry.warp
    if ($warp -match '(?i)@(?:Constantiam|3b3t)(?:[_\s-]*server)?$') { return $true }
    return @($Entry.archiveCategoryPath | Where-Object {
        [string]$_ -match '(?i)\b(?:Constantiam|3b3t)\b'
    }).Count -gt 0
}

$run = [IO.Path]::GetFullPath($RunRoot)
if ([string]::IsNullOrWhiteSpace($StatePath)) { $StatePath = Join-Path $run 'collector-state.json' }
if ([string]::IsNullOrWhiteSpace($ImportStatePath)) { $ImportStatePath = Join-Path $run 'production-import-state.json' }
$rollingRoot = Join-Path $run 'rolling-handoff'
if ([string]::IsNullOrWhiteSpace($RollingStatePath)) { $RollingStatePath = Join-Path $rollingRoot 'state.json' }
if ([string]::IsNullOrWhiteSpace($StatusPath)) { $StatusPath = Join-Path $rollingRoot 'status.json' }
$lockPath = Join-Path $rollingRoot 'handoff.lock'
$archiverScript = Join-Path $RepositoryRoot 'scripts\archive-collector-captures.ps1'
$promoterScript = Join-Path $RepositoryRoot 'scripts\promote-archive-adaptive-batch.ps1'
$importerScript = Join-Path $RepositoryRoot 'scripts\import-archive-inbox.ps1'
foreach ($required in @($StatePath, $WorkerConfigPath, $archiverScript, $promoterScript, $importerScript)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required rolling-handoff input was not found: $required" }
}
foreach ($directory in @($rollingRoot, $ReadyRoot)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

$lock = $null
try {
    try {
        $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    } catch [IO.IOException] {
        $busy = [ordered]@{ stage = 'already-running'; updatedUtc = [DateTime]::UtcNow.ToString('o') }
        Save-JsonAtomically $busy $StatusPath
        return [pscustomobject]$busy
    }

    $collectorState = Read-JsonUtf8 $StatePath
    if ($null -eq $collectorState) { throw "Collector state is unreadable: $StatePath" }
    $rollingState = Read-JsonUtf8 $RollingStatePath
    if ($null -eq $rollingState) {
        $rollingState = [pscustomobject][ordered]@{ schemaVersion = 1; updatedUtc = [DateTime]::UtcNow.ToString('o'); receipts = [pscustomobject]@{} }
    }
    $importState = Read-JsonUtf8 $ImportStatePath

    $pending = New-Object Collections.Generic.List[object]
    $foreign = New-Object Collections.Generic.List[object]
    foreach ($entry in @($collectorState.entries | Where-Object { Test-FinalCapture $_ } | Sort-Object completedUtc)) {
        if (Test-ForeignServer $entry) {
            $foreign.Add([ordered]@{ warp = [string]$entry.warp; reason = 'foreign-server provenance' })
            continue
        }
        $digest = ([string]$entry.adaptive.footprintArtifact.sha256).ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($digest)) { continue }
        $importReceipt = if ($null -ne $importState) { $importState.PSObject.Properties[$digest] } else { $null }
        if ($null -ne $importReceipt -and [string]$importReceipt.Value.status -eq 'accepted') { continue }
        $rollingReceipt = $rollingState.receipts.PSObject.Properties[$digest]
        if ($null -ne $rollingReceipt -and [string]$rollingReceipt.Value.status -eq 'quarantined') { continue }
        $pending.Add($entry)
    }

    if ($pending.Count -eq 0) {
        $empty = [ordered]@{
            stage = 'idle'
            updatedUtc = [DateTime]::UtcNow.ToString('o')
            pending = 0
            archived = 0
            promoted = 0
            quarantined = 0
            submitted = 0
            failed = 0
            foreignRejected = $foreign.Count
        }
        Save-JsonAtomically $empty $StatusPath
        return [pscustomobject]$empty
    }

    $cycleId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $cycleRoot = Join-Path $rollingRoot "cycles\$cycleId"
    New-Item -ItemType Directory -Path $cycleRoot -Force | Out-Null
    $cycleStatePath = Join-Path $cycleRoot 'collector-state.json'
    $cycleManifestPath = Join-Path $cycleRoot 'promotion-manifest.json'
    $cycleState = [ordered]@{
        schemaVersion = 1
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        entries = $pending.ToArray()
    }
    Save-JsonAtomically $cycleState $cycleStatePath

    $active = [ordered]@{
        stage = 'archiving'
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        cycleId = $cycleId
        pending = $pending.Count
        foreignRejected = $foreign.Count
    }
    Save-JsonAtomically $active $StatusPath
    # Preserve to E using a separate receipt. Keep the promotion input on D;
    # archiving rewrites paths in its input state to the durable destination.
    $archiveStatePath = Join-Path $cycleRoot 'archive-state.json'
    Save-JsonAtomically $cycleState $archiveStatePath
    $archiveResult = & $archiverScript -StatePath $archiveStatePath -StagedRoot $StagedRoot -ArchiveRoot $ArchiveRoot

    $active.stage = 'promoting'
    $active.updatedUtc = [DateTime]::UtcNow.ToString('o')
    $active.archived = [int]$archiveResult.archivedCount
    Save-JsonAtomically $active $StatusPath
    $promotion = & $promoterScript -StatePath $cycleStatePath -CapturedRoot $StagedRoot `
        -ReadyRoot $ReadyRoot -ManifestPath $cycleManifestPath

    foreach ($quarantined in @($promotion.quarantined)) {
        $matching = @($pending | Where-Object { [string]$_.warp -eq [string]$quarantined.warp } | Select-Object -First 1)
        if ($matching.Count -ne 1) { continue }
        $digest = ([string]$matching[0].adaptive.footprintArtifact.sha256).ToLowerInvariant()
        $receipt = [pscustomobject][ordered]@{
            status = 'quarantined'
            warp = [string]$quarantined.warp
            reasons = @($quarantined.reasons)
            updatedUtc = [DateTime]::UtcNow.ToString('o')
        }
        $rollingState.receipts | Add-Member -NotePropertyName $digest -NotePropertyValue $receipt -Force
    }
    $rollingState.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Save-JsonAtomically $rollingState $RollingStatePath

    $active.stage = 'submitting-to-production'
    $active.updatedUtc = [DateTime]::UtcNow.ToString('o')
    $active.promoted = [int]$promotion.promotedCount
    $active.quarantined = [int]$promotion.quarantinedCount
    Save-JsonAtomically $active $StatusPath
    if ([int]$promotion.promotedCount -gt 0) {
        $importOutput = @(& $importerScript -ReadyRoot $ReadyRoot -ApiBase $ApiBase -WorkerConfigPath $WorkerConfigPath `
            -ReadyFiles @($promotion.promoted | ForEach-Object { [string]$_.destination }) `
            -StatePath $ImportStatePath -MinimumStableSeconds $MinimumStableSeconds `
            -DelayBetweenJobsSeconds $DelayBetweenJobsSeconds -AdaptiveBackgroundRadiusBlocks 0)
    }

    $importState = Read-JsonUtf8 $ImportStatePath
    $submitted = 0
    $failed = 0
    foreach ($promoted in @($promotion.promoted)) {
        $digest = ([string]$promoted.sha256).ToLowerInvariant()
        $receipt = if ($null -ne $importState) { $importState.PSObject.Properties[$digest] } else { $null }
        if ($null -ne $receipt -and [string]$receipt.Value.status -eq 'accepted') { $submitted++ }
        else { $failed++ }
    }
    $complete = [ordered]@{
        stage = if ($failed -gt 0) { 'completed-with-failures' } else { 'complete' }
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        cycleId = $cycleId
        pending = $pending.Count
        archived = [int]$archiveResult.archivedCount
        promoted = [int]$promotion.promotedCount
        quarantined = [int]$promotion.quarantinedCount
        submitted = $submitted
        failed = $failed
        foreignRejected = $foreign.Count
        manifest = $cycleManifestPath
    }
    Save-JsonAtomically $complete $StatusPath
    [pscustomobject]$complete
} catch {
    $failedStatus = [ordered]@{
        stage = 'failed'
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        error = $_.Exception.Message
    }
    Save-JsonAtomically $failedStatus $StatusPath
    throw
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force }
}
