[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [ValidateSet('Overworld', 'Nether', 'End')]
    [string]$Dimension = 'Overworld',
    [int[]]$Bounds = @(),
    [string]$ExpectedChunksPath = '',
    [switch]$RequireExact,
    [switch]$RequireOnlyBounds
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($Bounds.Count -ne 0 -and $Bounds.Count -ne 4) {
    throw 'Bounds must be empty or minX,minZ,maxXExclusive,maxZExclusive in block coordinates.'
}
if ($Bounds.Count -eq 4 -and ($Bounds[2] -le $Bounds[0] -or $Bounds[3] -le $Bounds[1])) {
    throw 'Bounds must have positive width and height.'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedChunksPath) -and $Bounds.Count -ne 4) {
    throw 'ExpectedChunksPath requires Bounds so manifest coordinates can be range-checked.'
}

$resolved = (Resolve-Path -LiteralPath $Path).Path
$chunks = New-Object 'System.Collections.Generic.HashSet[string]'
$regionFiles = 0

function Test-RegionEntryPath([string]$name) {
    $normalized = $name.Replace('\', '/')
    switch ($Dimension) {
        'Overworld' { return $normalized -match '(^|/)region/r\.-?\d+\.-?\d+\.mca$' -and $normalized -notmatch '/DIM-?1/' }
        'Nether' { return $normalized -match '(^|/)DIM-1/region/r\.-?\d+\.-?\d+\.mca$' }
        'End' { return $normalized -match '(^|/)DIM1/region/r\.-?\d+\.-?\d+\.mca$' }
    }
}

function Add-RegionHeader([string]$name, [IO.Stream]$stream) {
    $fileName = [IO.Path]::GetFileName($name.Replace('/', '\'))
    if ($fileName -notmatch '^r\.(-?\d+)\.(-?\d+)\.mca$') { return }
    $regionX = [int]$Matches[1]
    $regionZ = [int]$Matches[2]
    $header = New-Object byte[] 4096
    $offset = 0
    while ($offset -lt $header.Length) {
        $read = $stream.Read($header, $offset, $header.Length - $offset)
        if ($read -le 0) { throw "Short Anvil location header: $name" }
        $offset += $read
    }
    for ($index = 0; $index -lt 1024; $index++) {
        $headerOffset = $index * 4
        if ($header[$headerOffset] -eq 0 -and $header[$headerOffset + 1] -eq 0 -and
            $header[$headerOffset + 2] -eq 0 -and $header[$headerOffset + 3] -eq 0) { continue }
        $chunkX = $regionX * 32 + ($index % 32)
        $chunkZ = $regionZ * 32 + [int][Math]::Floor($index / 32)
        [void]$chunks.Add("$chunkX,$chunkZ")
    }
    $script:regionFiles++
}

if (Test-Path -LiteralPath $resolved -PathType Container) {
    $relativeRegion = switch ($Dimension) {
        'Overworld' { 'region' }
        'Nether' { 'DIM-1\region' }
        'End' { 'DIM1\region' }
    }
    $regionRoot = Join-Path $resolved $relativeRegion
    if (-not (Test-Path -LiteralPath $regionRoot -PathType Container)) {
        throw "$Dimension region directory was not found: $regionRoot"
    }
    foreach ($file in @(Get-ChildItem -LiteralPath $regionRoot -Filter 'r.*.*.mca' -File)) {
        $stream = [IO.File]::OpenRead($file.FullName)
        try { Add-RegionHeader $file.Name $stream } finally { $stream.Dispose() }
    }
} elseif (Test-Path -LiteralPath $resolved -PathType Leaf) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($resolved)
    try {
        foreach ($entry in $archive.Entries) {
            if (-not (Test-RegionEntryPath $entry.FullName)) { continue }
            $stream = $entry.Open()
            try { Add-RegionHeader $entry.FullName $stream } finally { $stream.Dispose() }
        }
    } finally {
        $archive.Dispose()
    }
} else {
    throw "WDL path is neither a directory nor a file: $resolved"
}

if ($regionFiles -lt 1 -or $chunks.Count -lt 1) {
    throw "No occupied $Dimension Anvil chunks were found in $resolved"
}

$chunkXs = @($chunks | ForEach-Object { [int]($_.Split(',')[0]) })
$chunkZs = @($chunks | ForEach-Object { [int]($_.Split(',')[1]) })
$minChunkX = ($chunkXs | Measure-Object -Minimum).Minimum
$maxChunkX = ($chunkXs | Measure-Object -Maximum).Maximum
$minChunkZ = ($chunkZs | Measure-Object -Minimum).Minimum
$maxChunkZ = ($chunkZs | Measure-Object -Maximum).Maximum

$result = [ordered]@{
    Path = $resolved
    Dimension = $Dimension
    RegionFiles = $regionFiles
    HeaderChunks = $chunks.Count
    MinChunkX = $minChunkX
    MaxChunkX = $maxChunkX
    MinChunkZ = $minChunkZ
    MaxChunkZ = $maxChunkZ
    MinBlockX = $minChunkX * 16
    MaxBlockXExclusive = ($maxChunkX + 1) * 16
    MinBlockZ = $minChunkZ * 16
    MaxBlockZExclusive = ($maxChunkZ + 1) * 16
}

if ($Bounds.Count -eq 4) {
    $targetMinChunkX = [Math]::Floor($Bounds[0] / 16.0)
    $targetMinChunkZ = [Math]::Floor($Bounds[1] / 16.0)
    $targetMaxChunkX = [Math]::Floor(($Bounds[2] - 1) / 16.0)
    $targetMaxChunkZ = [Math]::Floor(($Bounds[3] - 1) / 16.0)
    $expected = New-Object 'System.Collections.Generic.HashSet[string]'
    if (-not [string]::IsNullOrWhiteSpace($ExpectedChunksPath)) {
        $expectedPath = (Resolve-Path -LiteralPath $ExpectedChunksPath).Path
        foreach ($line in [IO.File]::ReadAllLines($expectedPath, (New-Object Text.UTF8Encoding($false)))) {
            $value = $line.Trim()
            if ([string]::IsNullOrWhiteSpace($value) -or $value.StartsWith('#')) { continue }
            if ($value -notmatch '^(-?\d+),(-?\d+)$') {
                throw "Invalid expected-chunk manifest line '$value' in $expectedPath"
            }
            $chunkX = [int]$Matches[1]
            $chunkZ = [int]$Matches[2]
            if ($chunkX -lt $targetMinChunkX -or $chunkX -gt $targetMaxChunkX -or
                $chunkZ -lt $targetMinChunkZ -or $chunkZ -gt $targetMaxChunkZ) {
                throw "Expected chunk $chunkX,$chunkZ is outside Bounds in $expectedPath"
            }
            if (-not $expected.Add("$chunkX,$chunkZ")) {
                throw "Duplicate expected chunk $chunkX,$chunkZ in $expectedPath"
            }
        }
        if ($expected.Count -lt 1) { throw "Expected-chunk manifest is empty: $expectedPath" }
    } else {
        for ($chunkZ = $targetMinChunkZ; $chunkZ -le $targetMaxChunkZ; $chunkZ++) {
            for ($chunkX = $targetMinChunkX; $chunkX -le $targetMaxChunkX; $chunkX++) {
                [void]$expected.Add("$chunkX,$chunkZ")
            }
        }
    }

    $missing = New-Object 'System.Collections.Generic.List[string]'
    $outside = New-Object 'System.Collections.Generic.List[string]'
    foreach ($chunk in $expected) {
        if (-not $chunks.Contains($chunk)) { $missing.Add($chunk) }
    }
    foreach ($chunk in $chunks) {
        $parts = $chunk.Split(',')
        $chunkX = [int]$parts[0]
        $chunkZ = [int]$parts[1]
        if ($chunkX -lt $targetMinChunkX -or $chunkX -gt $targetMaxChunkX -or
            $chunkZ -lt $targetMinChunkZ -or $chunkZ -gt $targetMaxChunkZ) {
            $outside.Add($chunk)
        }
    }
    $result['TargetBounds'] = @($Bounds)
    $result['BoundsAreaChunks'] =
        ($targetMaxChunkX - $targetMinChunkX + 1) * ($targetMaxChunkZ - $targetMinChunkZ + 1)
    $result['ExpectedChunksPath'] = if ([string]::IsNullOrWhiteSpace($ExpectedChunksPath)) { '' } else { $expectedPath }
    $result['TargetChunks'] = $expected.Count
    $result['TargetPresent'] = $expected.Count - $missing.Count
    $result['TargetMissing'] = $missing.Count
    $result['ExactWithinBounds'] = $missing.Count -eq 0
    $result['MissingSample'] = @($missing | Select-Object -First 50)
    $result['OutsideTargetChunks'] = $outside.Count
    $result['OutsideSample'] = @($outside | Select-Object -First 50)
    $result['ExactBoundsOnly'] = $missing.Count -eq 0 -and $outside.Count -eq 0
}

$object = [pscustomobject]$result
$object
if ($RequireExact -and $Bounds.Count -eq 4 -and -not $object.ExactWithinBounds) { exit 2 }
if ($RequireOnlyBounds -and $Bounds.Count -eq 4 -and -not $object.ExactBoundsOnly) { exit 3 }
