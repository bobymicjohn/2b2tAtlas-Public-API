$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '..\atlas-bluemap-camera.ps1')
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Row($x, $z, $wx, $wz) {
    [pscustomobject]@{LocationX=$x;LocationZ=$z;ArchiveX=$wx;ArchiveZ=$wz;
        MinX=-8112;MinZ=6304;MaxXExclusive=-7472;MaxZExclusive=6960}
}
$oldDate = Get-AtlasBlueMapCamera (Row 14196 -4177 -7714.89 6556.27)
Assert ($oldDate.X -eq -7715 -and $oldDate.Z -eq 6556 -and $oldDate.Source -eq 'render-archive-warp') 'Older dated render followed the newer location marker'
$normal = Get-AtlasBlueMapCamera (Row -7700 6500 -7714.89 6556.27)
Assert ($normal.X -eq -7700 -and $normal.Z -eq 6500 -and $normal.Source -eq 'catalog-location') 'Valid catalog anchor changed'
foreach ($pair in @(@($null,$null),@(0,0),@([double]::NaN,6500),@(-7700,[double]::PositiveInfinity),@(-7472,6500))) {
    $result = Get-AtlasBlueMapCamera (Row 14196 -4177 $pair[0] $pair[1])
    Assert ($result.X -eq 14196 -and $result.Z -eq -4177) 'Invalid/outside warp was used'
}
$edge = Get-AtlasBlueMapCamera (Row 0 0 -7472.01 6959.99)
Assert ($edge.X -eq -7473 -and $edge.Z -eq 6959) 'Rounding escaped exclusive bounds'
$missing = Row 0 0 -7700 6500; $missing.MinX = $null
Assert ((Get-AtlasBlueMapCamera $missing).Source -eq 'catalog-location') 'Missing bounds accepted a warp'
$reversed = Row 0 0 -7700 6500; $reversed.MinX = 100
Assert ((Get-AtlasBlueMapCamera $reversed).Source -eq 'catalog-location') 'Reversed bounds accepted a warp'
$nether = [pscustomobject]@{LocationX=23999512;LocationZ=0;ArchiveX=2999939.0;ArchiveZ=0;
    MinX=2999856;MinZ=-64;MaxXExclusive=3000128;MaxZExclusive=80}
Assert ((Get-AtlasBlueMapCamera $nether).X -eq 2999939) 'Nether viewer used eight-times-scaled Atlas coordinates'
'PASS: dated render anchors, valid catalog anchors, negative rounding, exclusive edges, invalid coordinates and missing bounds.'
