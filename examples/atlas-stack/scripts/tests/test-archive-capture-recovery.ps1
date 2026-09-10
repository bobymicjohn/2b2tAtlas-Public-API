$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$scripts=Split-Path -Parent $PSScriptRoot
. (Join-Path $scripts 'archive-json-io.ps1')
. (Join-Path $scripts 'archive-survey-handoff.ps1')
. (Join-Path $scripts 'archive-interrupted-captures.ps1')
. (Join-Path $scripts 'archive-capture-recovery.ps1')
$copyFunction=(Get-Command Save-InterruptedCaptureFiles).ScriptBlock
$verifySnapshotFunction=(Get-Command Assert-InterruptedCaptureSnapshot).ScriptBlock
$fixture=Join-Path $env:TEMP ('atlas-recovery-'+[guid]::NewGuid().ToString('N'))
$gameRoot=Join-Path $fixture 'game';$savesRoot=Join-Path $gameRoot 'saves'
$StatePath=Join-Path $fixture 'worker\state.json';$privateRoot=Join-Path $fixture 'private'
$Server='archive.example';$warpName='Fixture_2024-01-01';$identity=$warpName.ToLowerInvariant()
$adaptiveCaptureDimension='Overworld';$adaptiveLiveDimensionId='minecraft:worlds/2b2t/2b2t_1';$adaptiveWarpX=-500.0;$adaptiveWarpZ=1.0
$AdaptiveCoreRadiusBlocks=512;$AdaptiveExpansionBlocks=256;$AdaptiveTerrainRadiusChunks=8;$AdaptiveStepChunks=8
$state=[ordered]@{entries=@();updatedUtc=''};$stateByWarp=@{}
$queue=[pscustomobject]@{entries=@([pscustomobject]@{warp=$warpName;normalizedWarp=$identity})}
$script:lines=New-Object 'Collections.Generic.List[string]';$script:process=$null
$script:copies=0;$script:commands=0;$script:writers=@()
$script:failHintWrite=$false
$script:failRecoveryRequest=$false
function Assert($Value,$Message) { if (-not $Value) { throw $Message } }
function Save-JsonAtomically($Value,$Path) {
    if ($script:failRecoveryRequest -and (Split-Path -Leaf $Path) -eq 'capture-recovery-request.json') {
        $script:failRecoveryRequest=$false
        throw 'Injected failure before packing'
    }
    if ($script:failHintWrite -and (Split-Path -Leaf $Path) -eq 'survey-hint.json') {
        $script:failHintWrite=$false
        throw 'Injected checkpoint write failure'
    }
    Write-AtlasJsonAtomically -Value $Value -Path $Path
}
function Read-JsonUtf8($Path) { Read-AtlasJsonWithRetry $Path }
function Get-NormalizedWarp($Value) { $Value.ToLowerInvariant() }
function ConvertTo-AtlasDimension($Value) { 'Overworld' }
function Get-PSDrive($Name) { [pscustomobject]@{Free=200GB} }
function Get-CimInstance { $script:writers }
function Get-SurveyHandoffDirectory($ArchiveServer,$WarpName) { $privateRoot }
function Test-FinalAdaptiveCapture($Record) { $false }
function Invoke-CollectorCommand($Command,$Patterns,$Timeout) {
    Assert (Test-Path (Get-CaptureJournalPath)) 'Download started without durable journal'
    $context=Read-JsonUtf8 (Join-Path $gameRoot 'config\atlas-archive-coverage\capture-context.json')
    Assert ($Command -like "*$($context.captureId)") 'Context does not identify new capture'
    $script:commands++
}
function Save-InterruptedCaptureFiles($SavesRoot,$CaptureNames,$WarpName) {
    $script:copies++
    & $copyFunction -SavesRoot $SavesRoot -CaptureNames $CaptureNames -WarpName $WarpName -RecoveryRoot (Join-Path $fixture 'preserved')
}
function Assert-InterruptedCaptureSnapshot($Path,$SavesRoot,$CaptureName) {
    & $verifySnapshotFunction -Path $Path -SavesRoot $SavesRoot -CaptureName $CaptureName -RecoveryRoot (Join-Path $fixture 'preserved')
}
New-Item -ItemType Directory -Path $savesRoot -Force | Out-Null
try {
    Start-TrackedWdlCapture 'archive-test'
    $journal=Read-JsonUtf8 (Get-CaptureJournalPath)
    Assert ($journal.checkpointCaptureId -eq 'archive-test' -and $script:commands -eq 1) 'Missing capture identity'
    $env:ATLAS_RECOVERY_FIXTURE=$savesRoot
    $code=@'
import os,sys
from pathlib import Path
sys.path.insert(0,sys.argv[1])
from test_archive_capture_resume import archive,chunk
archive(Path(os.environ['ATLAS_RECOVERY_FIXTURE'])/'archive-test.zip',{0:chunk('saved'),1:chunk('also saved')},root='archive-test')
'@
    $code | python - $scripts
    if ($LASTEXITCODE -ne 0) { throw 'Could not create recovery fixture' }
    # A foreign checkpoint must not credit its void ledger to this capture.
    Save-JsonAtomically @{schema=3;captureId='archive-other';dimension=$adaptiveLiveDimensionId;centerX=-32;centerZ=0;voidChunks=@(@{x=999;z=999})} (Join-Path $gameRoot 'config\atlas-archive-coverage\checkpoints\archive-test.json')
    $script:writers=@([pscustomobject]@{CommandLine="java --gameDir $gameRoot"})
    $held=$false
    try { Recover-CaptureJournal } catch { $held=$true }
    Assert ($held -and $script:copies -eq 0) 'Copied from a live game writer'
    $script:writers=@()
    Recover-CaptureJournal
    Assert (-not (Test-Path (Get-CaptureJournalPath))) 'Committed journal not cleared'
    $receipt=Read-JsonUtf8 (Join-Path $privateRoot 'latest.json')
    $hint=Read-JsonUtf8 (Join-Path $privateRoot ($receipt.directory+'\survey-hint.json'))
    Assert ($receipt.schemaVersion -eq 3 -and $receipt.savedChunks -eq 2) 'Terrain recovery receipt missing'
    Assert (@($hint.voidChunks).Count -eq 0) 'Foreign voids credited to recovery'
    Assert ($stateByWarp[$identity].partialPreservation -eq 'verified-recovery-on-D') 'Worker state not made durable'
    Assert ($stateByWarp[$identity].interruptionCount -eq 1) 'Crash retry not bounded'
    # Replay at the receipt/state transaction boundary must not recopy the save.
    Save-JsonAtomically $journal (Get-CaptureJournalPath)
    Recover-CaptureJournal
    Assert ($script:copies -eq 1 -and $stateByWarp[$identity].interruptionCount -eq 1) 'Journal replay duplicated work/retry count'
    $stateByWarp[$identity] | Add-Member requiresFootprintReviewBeforeRecovery $true -Force
    Save-JsonAtomically $journal (Get-CaptureJournalPath)
    $held=$false
    try { Recover-CaptureJournal } catch { $held=$_.Exception.Message -like 'Resume checkpoint held: review the saved footprint*' }
    Assert ($held -and $script:copies -eq 1 -and (Test-Path (Get-CaptureJournalPath))) 'Review hold copied the world or discarded its journal'
    $stateByWarp[$identity].requiresFootprintReviewBeforeRecovery=$false
    $stateByWarp[$identity].status='needs-footprint-review'
    Save-JsonAtomically $journal (Get-CaptureJournalPath)
    Recover-CaptureJournal
    Assert ($stateByWarp[$identity].status -eq 'needs-footprint-review') 'Disk recovery released a runtime/footprint review hold'
    $stateByWarp[$identity].status='retryable'
    Start-TrackedWdlCapture 'archive-next'
    $next=Read-JsonUtf8 (Get-CaptureJournalPath)
    Assert (@($next.seeds).Count -eq 1 -and $next.seeds[0].sha256 -eq $receipt.zipSha256) 'Restart omitted its recovered parent'
    # A stale terminal save line from a previous capture cannot end this flush.
    $script:lines.Clear();$script:lines.Add('Downloaded old: chunks 50');$script:captureStartIndex=1
    $script:downloadActive=$true;$script:process=[pscustomobject]@{HasExited=$false}
    function Send-CollectorCommand($Command) { }
    function Pump-CollectorOutput { $script:lines.Add('Downloaded archive-next: chunks 2') }
    Wait-InterruptedCaptureFlush
    Assert (-not $script:downloadActive) 'Completed flush did not release worker'
    # Gson omits null fields in the real companion checkpoint. A valid hint
    # therefore has no terrainZip property until the wrapper attaches its seed.
    $hint.captureId='archive-next'
    $hint.PSObject.Properties.Remove('terrainZip')
    $hint.voidChunks=@([pscustomobject]@{x=-32;z=0})
    Save-JsonAtomically $hint (Join-Path $gameRoot 'config\atlas-archive-coverage\checkpoints\archive-next.json')
    $script:failHintWrite=$true
    $held=$false
    try { Recover-CaptureJournal } catch { $held=$_.Exception.Message -eq 'Injected checkpoint write failure' }
    Assert ($held -and $script:copies -eq 2) 'Checkpoint failure did not retain completed disk work'
    Assert ($null -ne (Read-JsonUtf8 (Get-CaptureJournalPath)).PSObject.Properties['diskRecovery']) 'Completed recovery was not journaled'
    $cachedJournal=Read-JsonUtf8 (Get-CaptureJournalPath)
    $cachedZip=Join-Path $privateRoot ($cachedJournal.diskRecovery.directory+'\partial-wdl.zip')
    $cachedBytes=[IO.File]::ReadAllBytes($cachedZip)
    [IO.File]::WriteAllText($cachedZip,'corrupt')
    $held=$false
    try { Recover-CaptureJournal } catch { $held=$_.Exception.Message -like '*cached disk recovery hash mismatch*' }
    Assert ($held -and $script:copies -eq 2 -and (Test-Path (Get-CaptureJournalPath))) 'Corrupt cached recovery was reused or silently discarded'
    [IO.File]::WriteAllBytes($cachedZip,$cachedBytes)
    Recover-CaptureJournal
    Assert ($script:copies -eq 2) 'Checkpoint retry duplicated preservation and disk union'
    $nativeReceipt=Read-JsonUtf8 (Join-Path $privateRoot 'latest.json')
    $nativeHint=Read-JsonUtf8 (Join-Path $privateRoot ($nativeReceipt.directory+'\survey-hint.json'))
    Assert ($nativeReceipt.savedChunks -eq 2) 'Native checkpoint recovery lost its parent terrain'
    Assert (@($nativeHint.voidChunks).Count -eq 1) 'Valid checkpoint void ledger was discarded'
    Assert ($null -ne $nativeHint.PSObject.Properties['terrainZip'] -and $null -eq $nativeHint.terrainZip) 'Optional terrainZip was not normalized'
    Start-TrackedWdlCapture 'archive-third'
    Copy-Item (Join-Path $savesRoot 'archive-test.zip') (Join-Path $savesRoot 'archive-third.zip')
    $script:failRecoveryRequest=$true
    $held=$false
    try { Recover-CaptureJournal } catch { $held=$_.Exception.Message -eq 'Injected failure before packing' }
    Assert ($held -and $script:copies -eq 3) 'Preservation failure fixture did not run'
    $savedJournal=Read-JsonUtf8 (Get-CaptureJournalPath)
    $savedFile=Join-Path $savedJournal.preservedCapturePath 'archive-third.zip'
    $savedBytes=[IO.File]::ReadAllBytes($savedFile)
    [IO.File]::WriteAllText($savedFile,'corrupt')
    $held=$false
    try { Recover-CaptureJournal } catch { $held=$_.Exception.Message -eq 'Interrupted snapshot hash mismatch.' }
    Assert ($held -and $script:copies -eq 3) 'Invalid preserved snapshot accepted or copied again'
    [IO.File]::WriteAllBytes($savedFile,$savedBytes)
    Recover-CaptureJournal
    Assert ($script:copies -eq 3) 'Failure before packing caused another preservation copy'
    'PASS: journal-before-download, live writer hold, native/foreign checkpoints, failure replay without duplicate copies, corrupt cache hold, parent linkage, flush boundary.'
} finally {
    Remove-Item Env:\ATLAS_RECOVERY_FIXTURE -ErrorAction SilentlyContinue
    $resolved=[IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped TEMP' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
