# Dated WDLs can have different coordinates. Keep an in-bounds catalog anchor;
# otherwise use this render's own Archive arrival, never another date's warp.
function Get-AtlasBlueMapCamera($Row) {
    $x = [int64]$Row.LocationX
    $z = [int64]$Row.LocationZ
    $validBounds = $null -ne $Row.MinX -and $null -ne $Row.MinZ -and
        $null -ne $Row.MaxXExclusive -and $null -ne $Row.MaxZExclusive -and
        $Row.MaxXExclusive -gt $Row.MinX -and $Row.MaxZExclusive -gt $Row.MinZ
    $catalogInside = $validBounds -and $x -ge $Row.MinX -and $x -lt $Row.MaxXExclusive -and
        $z -ge $Row.MinZ -and $z -lt $Row.MaxZExclusive
    $source = 'catalog-location'
    if (-not $catalogInside -and $validBounds -and $null -ne $Row.ArchiveX -and $null -ne $Row.ArchiveZ) {
        $warpX = [double]$Row.ArchiveX
        $warpZ = [double]$Row.ArchiveZ
        if (-not [double]::IsNaN($warpX) -and -not [double]::IsInfinity($warpX) -and
            -not [double]::IsNaN($warpZ) -and -not [double]::IsInfinity($warpZ) -and
            $warpX -ge $Row.MinX -and $warpX -lt $Row.MaxXExclusive -and
            $warpZ -ge $Row.MinZ -and $warpZ -lt $Row.MaxZExclusive) {
            $x = [int64][Math]::Min($Row.MaxXExclusive - 1, [Math]::Round($warpX, [MidpointRounding]::AwayFromZero))
            $z = [int64][Math]::Min($Row.MaxZExclusive - 1, [Math]::Round($warpZ, [MidpointRounding]::AwayFromZero))
            $source = 'render-archive-warp'
        }
    }
    [pscustomobject]@{ X=$x; Z=$z; Source=$source }
}
