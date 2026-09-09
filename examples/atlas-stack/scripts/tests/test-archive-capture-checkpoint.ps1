$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$scripts=Split-Path -Parent $PSScriptRoot
. (Join-Path $scripts 'archive-json-io.ps1')
. (Join-Path $scripts 'archive-survey-handoff.ps1')
function Assert($Value,$Message) { if (-not $Value) { throw $Message } }
$fixture=Join-Path $env:TEMP ('atlas-checkpoint-'+[guid]::NewGuid().ToString('N'))
$gameRoot=Join-Path $fixture 'game'
$privateRoot=Join-Path $fixture 'private'
$Server='thearchive.world'
$adaptiveLiveDimensionId='minecraft:overworld';$adaptiveWarpX=10.0;$adaptiveWarpZ=-20.0
$AdaptiveMaximumRuntimeSeconds=43200;$AdaptiveMaxRadiusBlocks=0
$AdaptiveCoreRadiusBlocks=512;$AdaptiveExpansionBlocks=256;$AdaptiveTerrainRadiusChunks=8;$AdaptiveStepChunks=8
$script:resumeCapture=$null;$script:failCopy=$false;$script:cancelled=$false
function Get-SurveyHandoffDirectory($ArchiveServer,$WarpName) { return $privateRoot }
function Get-PSDrive($Name) { return [pscustomobject]@{Free=200GB} }
function Save-JsonAtomically($Value,$Path) { Write-AtlasJsonAtomically -Value $Value -Path $Path }
function Test-CaptureZip($Path,$Name) { Assert (Test-Path $Path) 'Missing test ZIP'; return $true }
function Complete-WdlCapture($Name) { return [pscustomobject]@{ZipPath=(Join-Path $fixture 'partial.zip');SavedChunks=42} }
function Copy-VerifiedAtomically($Source,$Destination,$Hash) {
    if ($script:failCopy) { throw 'Simulated copy interruption' }
    Copy-Item -LiteralPath $Source -Destination $Destination
    Assert ((Get-FileHash $Destination).Hash -eq $Hash) 'Copy hash mismatch'
}
function Invoke-CollectorCommand($Command,$Patterns,$Timeout) {
    if ($Command -eq 'msg /atlascover cancel') { $script:cancelled=$true }
    return [pscustomobject]@{Line='ATLAS_COVER resume-start planningOnly=false restored=42 waypoints=1 received=42'}
}
New-Item -ItemType Directory -Path (Join-Path $gameRoot 'config\atlas-archive-coverage') -Force | Out-Null
try {
    [IO.File]::WriteAllBytes((Join-Path $fixture 'partial.zip'),[byte[]]@(1,2,3))
    $hint=Join-Path $gameRoot 'config\atlas-archive-coverage\survey-hint.json'
    Save-JsonAtomically @{schema=2;core=512;expansion=256;radius=8;step=8;voidChunks=@(@{x=10;z=20})} $hint
    Save-SurveyHandoff 'Base_2022-07-20' 'archive-base'
    $latest=Join-Path $privateRoot 'latest.json'
    $receipt=Read-AtlasJsonWithRetry $latest
    Assert ($receipt.schemaVersion -eq 2 -and -not $receipt.planningOnly) 'Full checkpoint not committed'
    Assert (Restore-SurveyHandoff 'Base_2022-07-20' 'archive-resumed') 'Valid checkpoint not resumed'
    Assert ($script:resumeCapture.RestoredChunks -eq 42) 'Restored coverage not recorded'
    Assert ($script:resumeCapture.CaptureName -eq 'archive-resumed') 'Merge linked to wrong capture'
    $AdaptiveMaximumRuntimeSeconds=1800
    Assert (Restore-SurveyHandoff 'Base_2022-07-20' 'archive-resumed') 'Fast-lane restart discarded saved coverage'
    $savedHint=Read-AtlasJsonWithRetry $hint
    Assert ($savedHint.voidChunks.Count -eq 1 -and $savedHint.terrainZip -eq $script:resumeCapture.ZipPath) 'Void/terrain linkage lost'
    $before=[IO.File]::ReadAllBytes($latest)
    $script:failCopy=$true;$failed=$false
    try { Save-SurveyHandoff 'Base_2022-07-20' 'archive-next' } catch { $failed=$true }
    Assert $failed 'Interrupted handoff succeeded'
    Assert ([Convert]::ToBase64String($before) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($latest))) 'Interrupted handoff replaced last-good receipt'
    [IO.File]::AppendAllText($script:resumeCapture.ZipPath,'corruption')
    $failed=$false
    try { Restore-SurveyHandoff 'Base_2022-07-20' 'archive-next' } catch { $failed=$true }
    Assert ($failed -and $script:cancelled) 'Corrupt checkpoint silently restarted from zero'
    # A repair timeout transfers the original discovery ledger, not an empty
    # sparse-route checkpoint. It remains eligible for a normal lane handoff.
    $script:failCopy=$false
    function Get-CaptureJournalPath { Join-Path $fixture 'active-capture.json' }
    Save-JsonAtomically @{checkpointCaptureId='archive-discovery'} (Get-CaptureJournalPath)
    $discoveryHint=Join-Path $gameRoot 'config\atlas-archive-coverage\checkpoints\archive-discovery.json'
    Save-JsonAtomically @{schema=3;core=512;expansion=256;radius=8;step=8;captureId='archive-discovery';voidChunks=@(@{x=11;z=22})} $discoveryHint
    Save-SurveyHandoff 'Base_2022-07-20' 'archive-repair-1'
    $repairReceipt=Read-AtlasJsonWithRetry $latest
    Assert ($repairReceipt.schemaVersion -eq 3 -and $repairReceipt.hintSha256 -eq (Get-FileHash $discoveryHint).Hash) 'Repair handoff lost the discovery ledger'
    function python {
        $global:LASTEXITCODE=0
        '{"retainedChunks":40,"newChunks":2,"replacedChunks":0,"totalChunks":42}'
    }
    $script:resumeCapture=@{CaptureName='archive-unique-a';ZipPath=(Join-Path $fixture 'partial.zip');Sha256=(Get-FileHash (Join-Path $fixture 'partial.zip')).Hash;Merge=$null}
    $merged=Merge-ResumedWdlCapture ([pscustomobject]@{CaptureName='archive-unique-a';ZipPath='fixture-current.zip';SavedChunks=2;Chunks=2})
    Assert ((Split-Path -Leaf $merged.ZipPath) -eq 'archive-unique-a.zip') 'Merged captures collide on a common output basename'
    'PASS: durable receipt, fast restart, void restore, failed-copy/hash holds, repair-timeout handoff, unique merged filenames.'

} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    $prefix=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
    if (-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped TEMP' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
