[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,
    [Parameter(Mandatory = $true)]
    [string]$DestinationPath,
    [Parameter(Mandatory = $true)]
    [int[]]$Bounds,
    [string]$ExpectedChunksPath = '',
    [ValidateSet('Overworld', 'Nether', 'End')]
    [string]$Dimension = 'Overworld'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ($Bounds.Count -ne 4 -or $Bounds[2] -le $Bounds[0] -or $Bounds[3] -le $Bounds[1]) {
    throw 'Bounds must be minX,minZ,maxXExclusive,maxZExclusive with positive width and height.'
}

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$destination = [IO.Path]::GetFullPath($DestinationPath)
if ($source -eq $destination) { throw 'SourcePath and DestinationPath must differ.' }
if (Test-Path -LiteralPath $destination) { throw "Destination already exists: $destination" }
$parent = Split-Path -Parent $destination
if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$minChunkX = [int][Math]::Floor($Bounds[0] / 16.0)
$minChunkZ = [int][Math]::Floor($Bounds[1] / 16.0)
$maxChunkX = [int][Math]::Floor(($Bounds[2] - 1) / 16.0)
$maxChunkZ = [int][Math]::Floor(($Bounds[3] - 1) / 16.0)
$temporary = "$destination.$([guid]::NewGuid().ToString('N')).partial"
$prunedHeaderSlots = 0
$removedExternalChunks = 0
$rewrittenRegionFiles = 0
$compactedBytesRemoved = 0L

function Test-TargetDimensionPath([string]$Name) {
    $normalized = $Name.Replace('\', '/')
    switch ($Dimension) {
        'Overworld' {
            return $normalized -notmatch '/DIM-?1/' -and
                $normalized -notmatch '/dimensions/minecraft/(the_nether|the_end)/'
        }
        'Nether' {
            return $normalized -match '(^|/)DIM-1/' -or
                $normalized -match '(^|/)dimensions/minecraft/the_nether/'
        }
        'End' {
            return $normalized -match '(^|/)DIM1/' -or
                $normalized -match '(^|/)dimensions/minecraft/the_end/'
        }
    }
}

function Test-ChunkInside([int]$ChunkX, [int]$ChunkZ) {
    return $ChunkX -ge $minChunkX -and $ChunkX -le $maxChunkX -and
        $ChunkZ -ge $minChunkZ -and $ChunkZ -le $maxChunkZ
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceArchive = $null
$destinationArchive = $null
$destinationStream = $null
try {
    $sourceArchive = [IO.Compression.ZipFile]::OpenRead($source)
    $destinationStream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $destinationArchive = New-Object IO.Compression.ZipArchive(
        $destinationStream,
        [IO.Compression.ZipArchiveMode]::Create,
        $false,
        (New-Object Text.UTF8Encoding($false)))

    foreach ($entry in $sourceArchive.Entries) {
        $normalized = $entry.FullName.Replace('\', '/')
        $isTargetDimension = Test-TargetDimensionPath $normalized
        $isRegionContainer = $normalized -match '(^|/)(region|entities|poi)/'

        if ($isTargetDimension -and $isRegionContainer -and
            $normalized -match '/c\.(-?\d+)\.(-?\d+)\.mcc$') {
            if (-not (Test-ChunkInside ([int]$Matches[1]) ([int]$Matches[2]))) {
                $removedExternalChunks++
                continue
            }
        }

        $newEntry = $destinationArchive.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
        $newEntry.LastWriteTime = $entry.LastWriteTime
        $input = $entry.Open()
        $output = $newEntry.Open()
        try {
            if ($isTargetDimension -and $isRegionContainer -and
                $normalized -match '/r\.(-?\d+)\.(-?\d+)\.mca$') {
                if ($entry.Length -gt 512MB) { throw "Region entry exceeds the 512 MiB crop limit: $normalized" }
                $regionX = [int]$Matches[1]
                $regionZ = [int]$Matches[2]
                $memory = New-Object IO.MemoryStream
                try {
                    $input.CopyTo($memory)
                    $bytes = $memory.ToArray()
                } finally {
                    $memory.Dispose()
                }
                if ($bytes.Length -lt 8192) { throw "Region entry has a short header: $normalized" }
                $compacted = New-Object IO.MemoryStream
                $compacted.SetLength(8192)
                $nextSector = 2
                for ($index = 0; $index -lt 1024; $index++) {
                    $headerOffset = $index * 4
                    $occupied = $bytes[$headerOffset] -ne 0 -or $bytes[$headerOffset + 1] -ne 0 -or
                        $bytes[$headerOffset + 2] -ne 0 -or $bytes[$headerOffset + 3] -ne 0
                    if (-not $occupied) { continue }
                    $chunkX = $regionX * 32 + ($index % 32)
                    $chunkZ = $regionZ * 32 + [int][Math]::Floor($index / 32)
                    if (-not (Test-ChunkInside $chunkX $chunkZ)) {
                        $prunedHeaderSlots++
                        continue
                    }
                    $sourceSector = (([int]$bytes[$headerOffset]) -shl 16) -bor
                        (([int]$bytes[$headerOffset + 1]) -shl 8) -bor ([int]$bytes[$headerOffset + 2])
                    $sectorCount = [int]$bytes[$headerOffset + 3]
                    if ($sourceSector -lt 2 -or $sectorCount -lt 1 -or
                        (($sourceSector + $sectorCount) * 4096L) -gt $bytes.Length) {
                        throw "Region entry has an invalid occupied slot $index`: $normalized"
                    }
                    if ($nextSector -gt 0xFFFFFF) { throw "Compacted region offset overflow: $normalized" }
                    $newLocation = ($nextSector -shl 8) -bor $sectorCount
                    $compacted.Position = $headerOffset
                    $compacted.WriteByte([byte](($newLocation -shr 24) -band 0xFF))
                    $compacted.WriteByte([byte](($newLocation -shr 16) -band 0xFF))
                    $compacted.WriteByte([byte](($newLocation -shr 8) -band 0xFF))
                    $compacted.WriteByte([byte]($newLocation -band 0xFF))
                    $compacted.Position = 4096 + $headerOffset
                    $compacted.Write($bytes, 4096 + $headerOffset, 4)
                    $compacted.Position = $nextSector * 4096L
                    $compacted.Write($bytes, $sourceSector * 4096, $sectorCount * 4096)
                    $nextSector += $sectorCount
                }
                $compacted.SetLength($nextSector * 4096L)
                $compacted.Position = 0
                $compacted.CopyTo($output)
                $compactedBytesRemoved += [Math]::Max(0L, $bytes.Length - $compacted.Length)
                $compacted.Dispose()
                $rewrittenRegionFiles++
            } else {
                $input.CopyTo($output)
            }
        } finally {
            $output.Dispose()
            $input.Dispose()
        }
    }
    $destinationArchive.Dispose()
    $destinationArchive = $null
    $destinationStream.Dispose()
    $destinationStream = $null
    $sourceArchive.Dispose()
    $sourceArchive = $null
    Move-Item -LiteralPath $temporary -Destination $destination
} catch {
    if ($null -ne $destinationArchive) { $destinationArchive.Dispose() }
    if ($null -ne $destinationStream) { $destinationStream.Dispose() }
    if ($null -ne $sourceArchive) { $sourceArchive.Dispose() }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    throw
}

$auditScript = Join-Path $PSScriptRoot 'test-wdl-chunk-coverage.ps1'
$audit = & $auditScript -Path $destination -Dimension $Dimension -Bounds $Bounds `
    -ExpectedChunksPath $ExpectedChunksPath
if ($null -eq $audit -or -not [bool]$audit.ExactBoundsOnly) {
    throw "Footprint artifact audit failed: missing=$($audit.TargetMissing) outside=$($audit.OutsideTargetChunks)"
}

[pscustomobject][ordered]@{
    SourcePath = $source
    DestinationPath = $destination
    Dimension = $Dimension
    Bounds = @($Bounds)
    SourceSha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    DestinationSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    Bytes = (Get-Item -LiteralPath $destination).Length
    TargetChunks = [int]$audit.TargetChunks
    HeaderChunks = [int]$audit.HeaderChunks
    PrunedHeaderSlots = $prunedHeaderSlots
    RemovedExternalChunks = $removedExternalChunks
    RewrittenRegionFiles = $rewrittenRegionFiles
    CompactedBytesRemoved = $compactedBytesRemoved
    ExactBoundsOnly = [bool]$audit.ExactBoundsOnly
}
