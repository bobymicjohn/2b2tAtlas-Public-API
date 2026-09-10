$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../archive-adaptive-policy.ps1')
function Policy([string]$Warp) { Get-ArchiveAdaptiveCapturePolicy ([pscustomobject]@{warp=$Warp}) }
function Expand([int]$Iteration, [int]$MaxX, [int]$MaxZ, [long]$Chunks) {
    "ATLAS_COVER adaptive-expand iteration=$Iteration bounds=0,0..$MaxX,$MaxZ componentBounds=0,0..10,10 componentBuild=12 expand=true,false,false,false newWaypoints=40 targetChunks=$Chunks"
}
function Complete([long]$Chunks, [int]$MaxX, [int]$MaxZ, [int]$Build=1000, [int]$Orphan=0) {
    "ATLAS_COVER adaptive-complete confidence=high bounds=0,0..$MaxX,$MaxZ surveyBounds=0,0..$MaxX,$MaxZ target=$Chunks missing=0 received=$Chunks nonvoid=$Chunks built=$Build strong=100 componentBuild=$Build componentStrong=100 orphanBuild=$Orphan iterations=1"
}
function Check([string]$Name, [bool]$Actual, [bool]$Expected) {
    if ($Actual -ne $Expected) { throw "FAILED $Name expected=$Expected actual=$Actual" }
    Write-Output "PASS $Name"
}
$lodge = Policy 'l19w38_Boat_Lodge_2019-09-21@Spawnmason_lodge'
$base = Policy 'Boyland_2022-12-18_rebuild'
$bridge = Policy 'Interdimensional_Bridge'
Check 'underscore lodge classification' ($lodge.name -eq 'individual-lodge') $true
Check 'small lodge completes' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 2500 800 800) $lodge)) $false
Check 'legitimate small bridge completes' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 4096 4096 256 108) $bridge)) $false
Check 'ongoing small base expands' ([bool](Get-ArchiveAdaptiveRunawayReason (Expand 3 100 100 10201) $base)) $false
Check 'lodge starts consuming a region' ([bool](Get-ArchiveAdaptiveRunawayReason (Expand 4 128 128 16641) $lodge)) $true
Check 'base survey budget' ([bool](Get-ArchiveAdaptiveRunawayReason (Expand 4 256 256 66049) $base)) $true
Check 'runaway despite small area' ([bool](Get-ArchiveAdaptiveRunawayReason (Expand 12 100 100 10201) $base)) $true
Check 'Boat Lodge completed regression' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 315186 12576 6416 36988 36193) $lodge)) $true
Check 'Boyland completed regression' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 336144 4512 19072 48249 42241) $base)) $true
Check 'Poker Room completed regression' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 116332 8128 3664 17858 11209) $lodge)) $true
Check 'substantial unrelated builds' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 20000 3200 1600 2000 1600) $base)) $true
Check 'small incidental construction allowed' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 10000 1600 1600 1000 100) $base)) $false
Check 'coordinate sign does not change size' ([bool](Get-ArchiveAdaptiveRunawayReason 'ATLAS_COVER adaptive-complete confidence=high bounds=-40000,-40000..-39200,-39200 surveyBounds=-40000,-40000..-39200,-39200 target=2500' $lodge)) $false
Check 'all-axis entries retain fuse without trusting location coordinates' ([bool](Get-ArchiveAdaptiveRunawayReason (Expand 5 100 100 10201) (Policy 'Along_the_Axes'))) $true
Check 'writer wait is never a footprint violation' ([bool](Get-ArchiveAdaptiveRunawayReason 'ATLAS_COVER writer-wait pending=2000' $lodge)) $false
Check 'area uses 64-bit arithmetic' ([bool](Get-ArchiveAdaptiveRunawayReason (Complete 4000000000 32000000 32000000) $base)) $true

Check 'promotion refuses missing footprint evidence' ([bool](Get-ArchiveAdaptiveCompletedReviewReason ([pscustomobject]@{warp='base'}) ([pscustomobject]@{}))) $true
Check 'promotion checks old high-confidence oversized captures' ([bool](Get-ArchiveAdaptiveCompletedReviewReason ([pscustomobject]@{warp='Boat_lodge'}) ([pscustomobject]@{bounds=@(12912,-8544,25488,-2128);targetChunks=315186;primaryComponentBuildChunks=36988;orphanBuildChunksInsideBounds=36193}))) $true

Check 'oversized resume stopped before reconstruction' ([bool](Get-ArchiveAdaptiveCheckpointReviewReason ([pscustomobject]@{minX=-512;minZ=-512;maxX=511;maxZ=511;iteration=2}) $base)) $true
Check 'ordinary small checkpoint still resumes' ([bool](Get-ArchiveAdaptiveCheckpointReviewReason ([pscustomobject]@{minX=-20;minZ=-20;maxX=20;maxZ=20;iteration=2}) $base)) $false
Check 'different dated exhibits never reuse automatically' (Test-ArchiveSameSnapshotDate 'Poker_2021-04-17' 'Chunk_Haven_2021-05-22') $false
Check 'same dated snapshot may pass remaining reuse checks' (Test-ArchiveSameSnapshotDate 'First_2021-05-22' 'Second_2021-05-22') $true
Check 'undated shared world is not snapshot proof' (Test-ArchiveSameSnapshotDate 'First' 'Second') $false

. (Join-Path $PSScriptRoot '../archive-survey-handoff.ps1')
$fixtureRoot = Join-Path $env:TEMP ('atlas-footprint-preflight-' + [guid]::NewGuid().ToString('N'))
$attemptName = '20260101-000000-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$attemptPath = Join-Path $fixtureRoot $attemptName
$hintPath = Join-Path $attemptPath 'survey-hint.json'
$receiptPath = Join-Path $fixtureRoot 'latest.json'
function Get-SurveyHandoffDirectory { return $fixtureRoot }
$AdaptiveMaxRadiusBlocks=0; $Server='fixture.example'; $adaptiveLiveDimensionId='minecraft:overworld'; $adaptiveWarpX=0; $adaptiveWarpZ=0
try {
    New-Item -ItemType Directory -Path $attemptPath -Force | Out-Null
    @{minX=-512;minZ=-512;maxX=511;maxZ=511;iteration=2} | ConvertTo-Json | Set-Content $hintPath
    $receipt=@{schemaVersion=3;server=$Server;warp='Base_2022-07-20';dimension=$adaptiveLiveDimensionId;x=0;z=0;createdUtc=[datetime]::UtcNow.ToString('o');directory=$attemptName;hintSha256=(Get-FileHash $hintPath).Hash}
    $receipt | ConvertTo-Json | Set-Content $receiptPath
    $originalReceipt=(Get-FileHash $receiptPath).Hash; $originalHint=(Get-FileHash $hintPath).Hash
    $held=$false
    try { Assert-SurveyHandoffFootprint 'Base_2022-07-20' $base } catch { $held=$_.Exception.Message -like 'Resume checkpoint held: policy=*' }
    Check 'preflight rejects oversized saved parent' $held $true
    Check 'preflight leaves parent receipt and terrain pointer unchanged' (((Get-FileHash $receiptPath).Hash -eq $originalReceipt) -and ((Get-FileHash $hintPath).Hash -eq $originalHint) -and @(Get-ChildItem $fixtureRoot -File -Recurse).Count -eq 2) $true
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../invoke-archive-collector.ps1'),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $preflight=$ast.Find({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Assert-SurveyHandoffFootprint'},$true)
    $start=$ast.Find({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Start-TrackedWdlCapture'},$true)
    Check 'collector preflight precedes first tracked download' ($preflight.Extent.StartOffset -lt $start.Extent.StartOffset) $true
} finally {
    # Explicit fixture files and empty directories only; never touch a live store.
    foreach ($path in @($receiptPath,$hintPath)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    if (Test-Path -LiteralPath $attemptPath) { Remove-Item -LiteralPath $attemptPath }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot }
}
