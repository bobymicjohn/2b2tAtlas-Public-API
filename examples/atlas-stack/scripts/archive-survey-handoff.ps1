# Private partial captures are never placed in intake, ready, or public objects.
function Get-SurveyHandoffDirectory([string]$ArchiveServer, [string]$WarpName) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($ArchiveServer + "`n" + $WarpName)))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return Join-Path 'D:\AtlasExample\Ingest\DeferredCaptures' $key
}

function Test-SurveyHandoffIdentity($Receipt, [string]$ArchiveServer, [string]$WarpName, [string]$Dimension, [double]$X, [double]$Z, [datetimeoffset]$Now) {
    try {
        $age = $Now - [datetimeoffset]::Parse([string]$Receipt.createdUtc)
        return $Receipt.schemaVersion -in @(1, 2, 3) -and
            $Receipt.server -ceq $ArchiveServer -and $Receipt.warp -ceq $WarpName -and
            $WarpName -match '\d{4}-\d{2}' -and $Receipt.dimension -ceq $Dimension -and
            $age.TotalHours -ge 0 -and
            [Math]::Abs([double]$Receipt.x - $X) -lt 1 -and [Math]::Abs([double]$Receipt.z - $Z) -lt 1
    } catch { return $false }
}

function Save-SurveyHandoff([string]$WarpName, [string]$CaptureName) {
    $hintSource = Join-Path $gameRoot 'config\atlas-archive-coverage\survey-hint.json'
    $repairCheckpoint = $null
    if (Get-Command Get-CaptureJournalPath -ErrorAction SilentlyContinue) {
        $journalPath = Get-CaptureJournalPath
        if (Test-Path -LiteralPath $journalPath) {
            $journal = Get-Content -LiteralPath $journalPath -Raw | ConvertFrom-Json
            if ($journal.checkpointCaptureId -cne $CaptureName) { $repairCheckpoint = [string]$journal.checkpointCaptureId }
        }
    }
    if ($repairCheckpoint) {
        # Sparse repair keeps the original discovery ledger. A fast-lane timeout
        # during repair must transfer that ledger plus the merged partial world.
        if ($repairCheckpoint -notmatch '^archive-[A-Za-z0-9._-]+$' -or $repairCheckpoint.Contains('..')) { throw 'Invalid repair checkpoint identity.' }
        $hintSource = Join-Path $gameRoot ('config\atlas-archive-coverage\checkpoints\'+$repairCheckpoint+'.json')
        if (-not (Test-Path -LiteralPath $hintSource)) { throw 'Repair discovery checkpoint is missing.' }
        Invoke-CollectorCommand 'msg /atlascover cancel' @('ATLAS_COVER cancelled') 30 | Out-Null
    } else {
        Invoke-CollectorCommand 'msg /atlascover checkpoint' @('ATLAS_COVER checkpoint-saved ') 30 | Out-Null
    }
    $partial = Complete-WdlCapture $CaptureName
    Test-CaptureZip $partial.ZipPath $CaptureName | Out-Null
    if ((Get-PSDrive D).Free -lt 100GB) { throw 'Deferred capture staging requires 100 GiB free on D.' }
    $root = Get-SurveyHandoffDirectory $Server $WarpName
    $attempt = Join-Path $root ([datetime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $attempt -Force | Out-Null
    $hintHash = (Get-FileHash -LiteralPath $hintSource -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipHash = (Get-FileHash -LiteralPath $partial.ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-VerifiedAtomically $hintSource (Join-Path $attempt 'survey-hint.json') $hintHash
    Copy-VerifiedAtomically $partial.ZipPath (Join-Path $attempt 'partial-wdl.zip') $zipHash
    $hintState = Get-Content -LiteralPath $hintSource -Raw | ConvertFrom-Json
    $receipt = [ordered]@{schemaVersion=[int]$hintState.schema;planningOnly=([int]$hintState.schema -lt 2);server=$Server;warp=$WarpName;
        dimension=$adaptiveLiveDimensionId;x=$adaptiveWarpX;z=$adaptiveWarpZ;createdUtc=[datetime]::UtcNow.ToString('o');
        directory=(Split-Path -Leaf $attempt);hintSha256=$hintHash;zipSha256=$zipHash;savedChunks=$partial.SavedChunks}
    Save-JsonAtomically $receipt (Join-Path $attempt 'receipt.json')
    Save-JsonAtomically $receipt (Join-Path $root 'latest.json')
    Write-Output "SURVEY-HANDOFF-SAVED $WarpName chunks=$($partial.SavedChunks) schema=$($hintState.schema) durableTerrain=true"
}

# Run before Start-TrackedWdlCapture: a held parent must not create a new empty
# capture/journal or replace the latest saved checkpoint during recovery.
function Assert-SurveyHandoffFootprint([string]$WarpName, [object]$Policy) {
    if ($AdaptiveMaxRadiusBlocks -ne 0) { return }
    $root = Get-SurveyHandoffDirectory $Server $WarpName
    $path = Join-Path $root 'latest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    try {
        $receipt = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if (-not (Test-SurveyHandoffIdentity $receipt $Server $WarpName $adaptiveLiveDimensionId $adaptiveWarpX $adaptiveWarpZ ([datetimeoffset]::UtcNow))) { throw 'Saved snapshot identity requires review.' }
        if ($receipt.directory -notmatch '^\d{8}-\d{6}-[a-f0-9]{32}$') { throw 'Invalid saved handoff directory.' }
        $hintPath = Join-Path (Join-Path $root $receipt.directory) 'survey-hint.json'
        if ((Get-FileHash -LiteralPath $hintPath -Algorithm SHA256).Hash -ne $receipt.hintSha256) { throw 'Checkpoint hash mismatch.' }
        $hint = Get-Content -LiteralPath $hintPath -Raw | ConvertFrom-Json
        $reason = Get-ArchiveAdaptiveCheckpointReviewReason $hint $Policy
        if ($reason) { throw $reason }
    } catch { throw "Resume checkpoint held: $($_.Exception.Message)" }
}

function Restore-SurveyHandoff([string]$WarpName, [string]$CaptureName) {
    if ($AdaptiveMaxRadiusBlocks -ne 0) { return $false }
    $root = Get-SurveyHandoffDirectory $Server $WarpName
    $path = Join-Path $root 'latest.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    try {
        $r = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if (-not (Test-SurveyHandoffIdentity $r $Server $WarpName $adaptiveLiveDimensionId $adaptiveWarpX $adaptiveWarpZ ([datetimeoffset]::UtcNow))) { throw 'Saved snapshot/server/dimension/landing identity requires review.' }
        if ($r.directory -notmatch '^\d{8}-\d{6}-[a-f0-9]{32}$') { throw 'Invalid saved handoff directory.' }
        $attempt = Join-Path $root $r.directory
        $hint = Join-Path $attempt 'survey-hint.json'
        $zip = Join-Path $attempt 'partial-wdl.zip'
        if ((Get-FileHash -LiteralPath $hint -Algorithm SHA256).Hash -ne $r.hintSha256 -or
            (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $r.zipSha256) { throw 'Saved handoff hash mismatch; retained for recovery.' }
        $hintState = Get-Content -LiteralPath $hint -Raw | ConvertFrom-Json
        if ($hintState.core -ne $AdaptiveCoreRadiusBlocks -or $hintState.expansion -ne $AdaptiveExpansionBlocks -or
            $hintState.radius -ne $AdaptiveTerrainRadiusChunks -or $hintState.step -ne $AdaptiveStepChunks) { throw 'Saved handoff policy differs; refusing to discard progress.' }
        $localHint = Join-Path $gameRoot 'config\atlas-archive-coverage\survey-hint.json'
        $hintState | Add-Member -NotePropertyName terrainZip -NotePropertyValue $zip -Force
        Save-JsonAtomically $hintState $localHint
        if (Get-Command Set-CaptureJournalSeed -ErrorAction SilentlyContinue) { Set-CaptureJournalSeed @{ZipPath=$zip;Sha256=$r.zipSha256} }
        # Re-reading a large saved world can take longer than the server's idle
        # window. Reassert spectator mode without moving from the verified landing.
        $result = Invoke-CollectorCommand 'msg /atlascover resume' @('ATLAS_COVER resume-start planningOnly=false', 'ATLAS_COVER resume-failed ') 900 -KeepAliveCommand 'msg /gamemode spectator'
        if ($result.Line -match 'resume-failed') { throw "Saved terrain could not be resumed: $($result.Line)" }
        $restored = [regex]::Match($result.Line, 'restored=(\d+)')
        if (-not $restored.Success -or [int]$restored.Groups[1].Value -lt 1) { throw 'Resume did not confirm restored coverage.' }
        $script:resumeCapture = @{CaptureName=$CaptureName;ZipPath=$zip;Sha256=[string]$r.zipSha256;RestoredChunks=[int]$restored.Groups[1].Value;Merge=$null}
        Write-Host "SURVEY-HANDOFF-RESUMED $WarpName planningOnly=false; $($result.Line)"
        return $true
    } catch {
        $failure=$_.Exception.Message
        Write-Warning "Survey handoff retained; resume blocked: $failure"
        if ($failure -like 'Archive disconnected while waiting*') {
            # Preserve the network failure as retryable; do not wait for an
            # acknowledgement from a server session that has already closed.
            throw "Archive disconnected during an active WDL resume: $failure"
        }
        try { Invoke-CollectorCommand 'msg /atlascover cancel' @('ATLAS_COVER cancelled') 30 | Out-Null }
        catch { Write-Warning 'Resume cancellation was not acknowledged; original failure retained.' }
        throw "Resume checkpoint held: $failure"
    }
}

function Merge-ResumedWdlCapture($Capture) {
    if ($null -eq $script:resumeCapture -or [string]$Capture.CaptureName -ne $script:resumeCapture.CaptureName) { return $Capture }
    $seed = $script:resumeCapture
    if ((Get-FileHash -LiteralPath $seed.ZipPath -Algorithm SHA256).Hash -ne $seed.Sha256) { throw 'Resume source changed after validation.' }
    if ((Get-PSDrive D).Free -lt 100GB) { throw 'Resumed capture merge needs 100 GiB free on D.' }
    $directory = Join-Path 'D:\AtlasExample\Ingest\DeferredCaptures\continuations' ([guid]::NewGuid().ToString('N'))
    $destination = Join-Path $directory ($Capture.CaptureName + '.zip')
    $metricsJson = & python (Join-Path $PSScriptRoot 'archive_capture_resume.py') --previous $seed.ZipPath --current $Capture.ZipPath --output $destination --root $Capture.CaptureName
    if ($LASTEXITCODE -ne 0) { throw 'Chunk-level continuation merge failed; both input captures remain intact.' }
    $metrics = $metricsJson | ConvertFrom-Json
    $script:resumeCapture.Merge = $metrics
    $Capture.ZipPath = $destination
    $Capture.SavedChunks = [int]$metrics.totalChunks
    $Capture.Chunks = [Math]::Max([int]$Capture.Chunks, [int]$metrics.totalChunks)
    Write-Host "WDL-RESUME-MERGED retained=$($metrics.retainedChunks) new=$($metrics.newChunks) refreshed=$($metrics.replacedChunks) total=$($metrics.totalChunks)"
    return $Capture
}
