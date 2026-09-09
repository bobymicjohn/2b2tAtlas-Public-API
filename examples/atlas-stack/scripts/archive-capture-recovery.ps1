# Durable per-worker capture journal. Partial worlds never enter public intake.
function Get-CaptureJournalPath { Join-Path (Split-Path -Parent $StatePath) 'active-capture.json' }

function Set-CaptureJournalSeed($Seed) {
    $path = Get-CaptureJournalPath
    if (-not (Test-Path -LiteralPath $path)) { return }
    $journal = Read-JsonUtf8 $path
    $journal.seeds = @(@{path=$Seed.ZipPath;sha256=$Seed.Sha256})
    Save-JsonAtomically $journal $path
}

function Start-TrackedWdlCapture([string]$Name, $Seed = $null) {
    $path = Get-CaptureJournalPath
    $checkpointId = $Name
    if ($null -ne $Seed -and (Test-Path -LiteralPath $path)) {
        $checkpointId = [string](Read-JsonUtf8 $path).checkpointCaptureId
    } elseif (Test-Path -LiteralPath $path) {
        throw 'Resume checkpoint held: previous capture journal has not been recovered.'
    }
    $config = Join-Path $gameRoot 'config\atlas-archive-coverage'
    $prior = $stateByWarp[$identity]
    $retryNumber = if ($null -ne $prior -and $null -ne $prior.PSObject.Properties['interruptionCount']) { [int]$prior.interruptionCount+1 } else { 1 }
    $journal = [ordered]@{schemaVersion=1;server=$Server;warp=$warpName;normalizedWarp=$identity;retryNumber=$retryNumber;
        captureName=$Name;checkpointCaptureId=$checkpointId;dimension=$adaptiveLiveDimensionId;
        atlasDimension=$adaptiveCaptureDimension;x=$adaptiveWarpX;z=$adaptiveWarpZ;
        core=$AdaptiveCoreRadiusBlocks;expansion=$AdaptiveExpansionBlocks;radius=$AdaptiveTerrainRadiusChunks;
        step=$AdaptiveStepChunks;createdUtc=[datetime]::UtcNow.ToString('o');seeds=@()}
    # Bind the previous durable checkpoint before the new downloader can receive
    # anything. A crash during asynchronous NBT restore must not lose its parent.
    if ($null -eq $Seed) {
        $latest = Join-Path (Get-SurveyHandoffDirectory $Server $warpName) 'latest.json'
        if (Test-Path -LiteralPath $latest) {
            $receipt = Read-JsonUtf8 $latest
            if (-not (Test-SurveyHandoffIdentity $receipt $Server $warpName $adaptiveLiveDimensionId $adaptiveWarpX $adaptiveWarpZ ([datetimeoffset]::UtcNow)) -or
                $receipt.directory -notmatch '^\d{8}-\d{6}-[a-f0-9]{32}$') { throw 'Resume checkpoint held: saved capture identity mismatch.' }
            $Seed = @{ZipPath=(Join-Path (Split-Path -Parent $latest) ($receipt.directory+'\partial-wdl.zip'));Sha256=$receipt.zipSha256}
        }
    }
    if ($null -ne $Seed) {
        if ((Get-FileHash -LiteralPath $Seed.ZipPath -Algorithm SHA256).Hash -ne $Seed.Sha256) { throw 'Resume checkpoint held: parent capture hash mismatch.' }
        $journal.seeds = @(@{path=$Seed.ZipPath;sha256=$Seed.Sha256})
    }
    Save-JsonAtomically $journal $path
    Save-JsonAtomically @{captureId=$Name} (Join-Path $config 'capture-context.json')
    $script:captureStartIndex = $script:lines.Count
    $script:downloadActive = $true
    Invoke-CollectorCommand "msg /wdl start $Name" @('Downloading\s+') 30 | Out-Null
}

function Clear-CaptureJournal {
    $path = Get-CaptureJournalPath
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
}

function Wait-InterruptedCaptureFlush {
    if (-not $script:downloadActive -or $null -eq $script:process) { return }
    # A timeout is not evidence that saving stopped. Do not kill the JVM holding
    # unwritten chunks; hold this lane until the save terminates or the JVM exits.
    try { Send-CollectorCommand 'msg /wdl stop' } catch { }
    $lastNotice = [datetime]::MinValue
    $terminalPattern = 'Downloaded\s+.+:\s+chunks\s+\d+|Save failed:'
    $journalPath = Get-CaptureJournalPath
    if (Test-Path -LiteralPath $journalPath) {
        $currentName = [regex]::Escape([string](Read-JsonUtf8 $journalPath).captureName)
        $terminalPattern = 'Downloaded\s+'+$currentName+':\s+chunks\s+\d+|Save failed:'
    }
    $index=$script:captureStartIndex
    $fatalOom=$false
    while ($true) {
        if ($script:process.HasExited) {
            $orphans = @(Get-CimInstance Win32_Process -Filter "Name = 'java.exe' OR Name = 'javaw.exe'" | Where-Object {
                $_.CommandLine -and $_.CommandLine.IndexOf($gameRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0
            })
            if ($orphans.Count -eq 0) { return }
            # Launcher death does not prove its child finished writing. Without
            # a terminal save acknowledgement, keep that child alive for recovery.
        }
        Pump-CollectorOutput
        # ExitOnOutOfMemoryError ends the game JVM but HeadlessMC can remain at
        # its prompt. Require fatal-OOM evidence AND absence of the exact game's
        # Minecraft process before allowing ordinary disk recovery.
        if ($fatalOom) {
            $games = @(Get-CimInstance Win32_Process -Filter "Name = 'java.exe' OR Name = 'javaw.exe'" | Where-Object {
                $_.CommandLine -and $_.CommandLine.IndexOf($gameRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $_.CommandLine -match 'net\.fabricmc\.loader\.|net\.minecraft\.client\.main\.Main'
            })
            if ($games.Count -eq 0) {
                Write-Warning 'WDL-OOM-RECOVERY game JVM exited; retaining disk capture and parent checkpoints.'
                return
            }
        }
        for (; $index -lt $script:lines.Count; $index++) {
            if ($script:lines[$index] -match 'java\.lang\.OutOfMemoryError') { $fatalOom=$true }
            if ($script:lines[$index] -match $terminalPattern) {
                $script:downloadActive=$false
                return
            }
        }
        if (([datetime]::UtcNow-$lastNotice).TotalSeconds -ge 60) {
            Write-Warning 'WDL-FLUSH-WAIT retaining the running downloader until its save finishes; no reconnect or forced restart.'
            $lastNotice=[datetime]::UtcNow
            try { Send-CollectorCommand 'msg /wdl status' } catch { }
        }
        Start-Sleep -Seconds 1
    }
}

function Recover-CaptureJournal {
    $path = Get-CaptureJournalPath
    if (-not (Test-Path -LiteralPath $path)) { return }
    $journal = Read-JsonUtf8 $path
    if ($journal.schemaVersion -ne 1 -or $journal.server -cne $Server -or
        $journal.captureName -notmatch '^archive-[A-Za-z0-9._-]+$' -or $journal.captureName.Contains('..') -or
        $journal.checkpointCaptureId -notmatch '^archive-[A-Za-z0-9._-]+$' -or $journal.checkpointCaptureId.Contains('..') -or
        (Get-NormalizedWarp $journal.warp) -cne $journal.normalizedWarp) { throw 'Resume checkpoint held: invalid capture journal.' }
    $owned = @($queue.entries | Where-Object { $_.normalizedWarp -ceq $journal.normalizedWarp })
    if ($owned.Count -ne 1) { throw 'Resume checkpoint held: interrupted capture is not owned by this queue.' }
    $prior = $stateByWarp[$journal.normalizedWarp]
    if ($null -ne $prior -and $prior.status -in @('captured','ready') -and (Test-FinalAdaptiveCapture $prior)) {
        Clear-CaptureJournal
        return
    }
    $writers = @(Get-CimInstance Win32_Process -Filter "Name = 'java.exe' OR Name = 'javaw.exe'" | Where-Object {
        $_.CommandLine -and $_.CommandLine.IndexOf($gameRoot,[StringComparison]::OrdinalIgnoreCase) -ge 0
    })
    if ($writers.Count -gt 0) { throw 'Resume checkpoint held: previous game JVM still owns the working save.' }
    $root = Get-SurveyHandoffDirectory $Server $journal.warp
    $latest = Join-Path $root 'latest.json'
    $receipt = if (Test-Path -LiteralPath $latest) { Read-JsonUtf8 $latest } else { $null }
    # Replay after a crash between receipt publication and state update is cheap.
    $alreadyRecovered = $null -ne $receipt -and $null -ne $receipt.PSObject.Properties['recoveredCaptureId'] -and
        $receipt.recoveredCaptureId -ceq $journal.captureName
    if (-not $alreadyRecovered) {
        Write-Output "WDL-DISK-RECOVERY $($journal.warp): recovering saved terrain before reconnecting."
        if ($null -ne $journal.PSObject.Properties['diskRecovery']) {
            # A later checkpoint/receipt write can fail after the costly disk
            # union succeeds. Reuse that verified result instead of copying and
            # recompressing the same world on every supervisor retry.
            $recovery = $journal.diskRecovery
            if ($recovery.directory -notmatch '^\d{8}-\d{6}-[a-f0-9]{32}$') { throw 'Resume checkpoint held: invalid disk recovery directory.' }
            $attempt = Join-Path $root $recovery.directory
            $metrics = $recovery.metrics
            if ($metrics.savedChunks -le 0 -or $metrics.zipSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
                (Get-FileHash -LiteralPath (Join-Path $attempt 'partial-wdl.zip') -Algorithm SHA256).Hash -ne $metrics.zipSha256) {
                throw 'Resume checkpoint held: cached disk recovery hash mismatch.'
            }
            Write-Output "WDL-DISK-RECOVERY-REUSED $($journal.warp) persistedChunks=$($metrics.savedChunks)"
        } else {
            if ((Get-PSDrive D).Free -lt 100GB) { throw 'Resume checkpoint held: recovery requires 100 GiB free on D.' }
            if ($null -ne $journal.PSObject.Properties['preservedCapturePath']) {
                $preserved=[string]$journal.preservedCapturePath
                Assert-InterruptedCaptureSnapshot -Path $preserved -SavesRoot $savesRoot -CaptureName $journal.captureName
                Write-Output "WDL-PRESERVATION-REUSED $($journal.warp)"
            } else {
                $preserved = Save-InterruptedCaptureFiles -SavesRoot $savesRoot -CaptureNames @($journal.captureName) -WarpName $journal.warp
                if ($preserved) {
                    # Keep this transaction boundary before packing/union: even
                    # a failed disk recovery must not recopy the same snapshot.
                    $journal | Add-Member preservedCapturePath $preserved -Force
                    Save-JsonAtomically $journal $path
                }
            }
            if (-not $preserved) { $preserved = $savesRoot }
            $attempt = Join-Path $root ([datetime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
            $requestPath = Join-Path (Split-Path -Parent $path) 'capture-recovery-request.json'
            Save-JsonAtomically @{preservedRoot=$preserved;destination=$attempt;captureName=$journal.captureName;
                dimension=$journal.atlasDimension;seeds=@($journal.seeds)} $requestPath
            $output = & python (Join-Path $PSScriptRoot 'archive_capture_recovery.py') recover $requestPath
            if ($LASTEXITCODE -ne 0) { throw 'Resume checkpoint held: disk recovery failed; journal and originals retained.' }
            $metrics = $output | ConvertFrom-Json
            $journal | Add-Member -NotePropertyName diskRecovery -NotePropertyValue ([pscustomobject]@{
                directory=(Split-Path -Leaf $attempt);metrics=$metrics
            }) -Force
            Save-JsonAtomically $journal $path
        }
        $hintPath = Join-Path $gameRoot ('config\atlas-archive-coverage\checkpoints\'+$journal.checkpointCaptureId+'.json')
        $hint = if (Test-Path -LiteralPath $hintPath) { Read-JsonUtf8 $hintPath } else { $null }
        $cx=[int][Math]::Floor($journal.x/16);$cz=[int][Math]::Floor($journal.z/16)
        if ($null -eq $hint -or $hint.schema -ne 3 -or $hint.captureId -cne $journal.checkpointCaptureId -or
            $hint.dimension -cne $journal.dimension -or $hint.centerX -ne $cx -or $hint.centerZ -ne $cz) {
            # No trustworthy void observations survived. Rebuild terrain from
            # actual NBT and leave the unobserved area pending, never fabricated.
            $radius=[int][Math]::Ceiling($journal.core/16)
            $hint=[pscustomobject]@{schema=3;dimension=$journal.dimension;centerX=$cx;centerZ=$cz;
                core=$journal.core;expansion=$journal.expansion;maxRadius=0;radius=$journal.radius;step=$journal.step;
                minX=$cx-$radius;minZ=$cz-$radius;maxX=$cx+$radius-1;maxZ=$cz+$radius-1;
                anchor=$null;voidChunks=@();iteration=0;completedWaypoints=0;terrainZip=$null;captureId=$journal.checkpointCaptureId}
        }
        $hint.minX=[Math]::Min($hint.minX,$metrics.minChunkX);$hint.minZ=[Math]::Min($hint.minZ,$metrics.minChunkZ)
        $hint.maxX=[Math]::Max($hint.maxX,$metrics.maxChunkX);$hint.maxZ=[Math]::Max($hint.maxZ,$metrics.maxChunkZ)
        # Gson omits this field when null; preserve the valid checkpoint and
        # its void ledger while adding the wrapper-owned optional field.
        $hint | Add-Member -NotePropertyName terrainZip -NotePropertyValue $null -Force
        Save-JsonAtomically $hint (Join-Path $attempt 'survey-hint.json')
        $receipt=[pscustomobject]@{schemaVersion=3;planningOnly=$false;server=$journal.server;warp=$journal.warp;
            dimension=$journal.dimension;x=$journal.x;z=$journal.z;createdUtc=[datetime]::UtcNow.ToString('o');
            directory=(Split-Path -Leaf $attempt);hintSha256=(Get-FileHash (Join-Path $attempt 'survey-hint.json')).Hash.ToLowerInvariant();
            zipSha256=$metrics.zipSha256;savedChunks=$metrics.savedChunks;recoveredCaptureId=$journal.captureName}
        Save-JsonAtomically $receipt (Join-Path $attempt 'receipt.json')
        Save-JsonAtomically $receipt $latest
    }
    $attempt = Join-Path $root $receipt.directory
    if ((Get-FileHash (Join-Path $attempt 'partial-wdl.zip')).Hash -ne $receipt.zipSha256 -or
        (Get-FileHash (Join-Path $attempt 'survey-hint.json')).Hash -ne $receipt.hintSha256) { throw 'Resume checkpoint held: recovered receipt hash mismatch.' }
    if ($null -eq $prior) { $prior=[pscustomobject]@{warp=$journal.warp;normalizedWarp=$journal.normalizedWarp;status='retryable'} }
    $previous = if ($null -ne $prior.PSObject.Properties['interruptionCount']) { [int]$prior.interruptionCount } else { 0 }
    $prior | Add-Member interruptionCount ([Math]::Max($previous,[int]$journal.retryNumber)) -Force
    if ($null -eq $prior.PSObject.Properties['recoveredCaptureId'] -or $prior.recoveredCaptureId -cne $journal.captureName) {
        $prior | Add-Member automaticRetryAfterUtc ([datetime]::UtcNow.AddMinutes(5).ToString('o')) -Force
    }
    $prior | Add-Member recoveredCaptureId $journal.captureName -Force
    $prior | Add-Member partialPreservation 'verified-recovery-on-D' -Force
    $prior | Add-Member partialRecoveryPath $attempt -Force
    $stateByWarp[$journal.normalizedWarp]=$prior
    $state.entries=@($stateByWarp.Values | Sort-Object normalizedWarp);$state.updatedUtc=[datetime]::UtcNow.ToString('o')
    Save-JsonAtomically $state $StatePath
    Clear-CaptureJournal
    Write-Host "WDL-DISK-RECOVERED $($journal.warp) persistedChunks=$($receipt.savedChunks); next attempt resumes verified terrain."
}
