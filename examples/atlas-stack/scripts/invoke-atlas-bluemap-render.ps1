param(
    [int[]]$RenderId = @(),
    [switch]$All,
    [string]$DatabasePath = 'C:\AtlasExample\Api\data\atlas.db',
    [string]$SourceRoot = 'E:\AtlasExample\WorldDownloads\objects',
    [string]$ScratchRoot = 'D:\AtlasExample\Ingest\bluemap-work',
    [string]$OutputRoot = 'F:\AtlasExample\AtlasBlueMap\location-renders',
    [string]$BlueMapImage = 'ghcr.io/bluemap-minecraft/bluemap:v5.23',
    [int]$CpuCount = 8,
    [string]$MemoryLimit = '8g',
    [string]$RelightToolRoot = 'D:\AtlasExample\Ingest\bluemap-tools',
    [string]$RelightImage = 'itzg/minecraft-server@sha256:0cfb1fd185b90351bbc1d9ae9bb28299c8efbe1efeeacf208b466206ec9d9806',
    [int]$RelightCpuCount = 8,
    [string]$RelightMemoryLimit = '12g',
    [int]$RelightTimeoutMinutes = 180,
    [long]$OutputQuotaBytes = 500GB,
    [long]$MinimumFreeBytes = 256GB,
    [switch]$Force,
    [switch]$KeepScratch,
    [ValidateRange(0, 3)][int]$WorkerId = 0,
    [int]$CoordinatorPid = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'atlas-bluemap-resources.ps1')
if ($MemoryLimit -ne '8g' -or $RelightMemoryLimit -ne '12g') {
    throw 'Stage admission requires the validated 8g render and 12g relight container limits.'
}

$blueMapVersion = '5.23'
$rendererProfileVersion = 7
$sharedCacheVolume = 'atlas-bluemap-cache-v5-23'
$runStartedUtc = [DateTime]::UtcNow
$stateRoot = 'C:\AtlasExample\Ingest\bluemap'
$coordinationRoot = $stateRoot
if ($WorkerId -gt 0) {
    if ($All -or $RenderId.Count -ne 1 -or $Force) { throw 'Coordinated workers require one render ID and cannot use All/Force.' }
    $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$CoordinatorPid" -ErrorAction Stop
    if ($null -eq $owner -or $owner.CommandLine -notmatch 'start-atlas-bluemap-coordinator\.ps1') {
        throw 'Coordinated worker has no valid coordinator owner.'
    }
    $stateRoot = Join-Path $stateRoot "workers\$WorkerId"
    $sharedCacheVolume = "atlas-bluemap-cache-v5-23-worker-$WorkerId"
}
$statusPath = Join-Path $stateRoot 'location-render-status.json'
$lockPath = Join-Path $stateRoot 'location-render.lock'
$batchLogPath = Join-Path $stateRoot 'location-render.log'
$currentStage = 'waiting'
$jobStartedUtc = $null

function Set-WorkerStage([string]$Stage) {
    $script:currentStage = $Stage
    if ($WorkerId -gt 0) { Update-RunStatus -State 'running' -Completed $completedCount -Total $jobs.Count -Current $current -Results $results }
}

function Remove-OwnedDockerResource {
    param([ValidatePattern('^atlas-bluemap-[a-z0-9-]+$')][string]$Name, [switch]$Volume)
    try {
        if ($Volume) { & docker volume rm -f $Name *> $null }
        else { & docker rm -f $Name *> $null }
    } catch {
        # --rm already removes completed containers. PowerShell 5.1 can turn
        # Docker's harmless "No such container" stderr into a terminating error.
        # Cleanup must never discard an otherwise successful generation.
        if ($_.Exception.Message -notmatch '(?i)no such (container|volume)') {
            Write-Warning "BlueMap cleanup could not remove $Name`: $($_.Exception.Message)"
        }
    }
}

function Enter-PublicationLock {
    $path = Join-Path $coordinationRoot 'publication.lock'
    $deadline = [datetime]::UtcNow.AddMinutes(5)
    do {
        try { return [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch [IO.IOException] { Start-Sleep -Milliseconds 200 }
    } while ([datetime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for BlueMap publication lock.'
}

function Reserve-Output([long]$Bytes) {
    $gate = Enter-PublicationLock
    try {
        $reservationsRoot = Join-Path $coordinationRoot 'reservations'
        New-Item -ItemType Directory -Path $reservationsRoot -Force | Out-Null
        $reserved = 0L
        foreach ($file in Get-ChildItem -LiteralPath $reservationsRoot -Filter '*.json' -File) {
            $reservation = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json
            $owner = Get-Process -Id ([int]$reservation.OwnerPid) -ErrorAction SilentlyContinue
            if ($null -ne $owner -and $owner.StartTime.ToUniversalTime().Ticks -eq [long]$reservation.OwnerStartTicks) {
                $reserved += [long]$reservation.Bytes
            } else { Remove-Item -LiteralPath $file.FullName -Force }
        }
        $used = Get-ManifestUsage -Root $OutputRoot
        $free = [long](Get-PSDrive -Name ([IO.Path]::GetPathRoot($OutputRoot).Substring(0,1))).Free
        if ($used + $reserved + $Bytes -gt $OutputQuotaBytes -or $free - $reserved - $Bytes -lt $MinimumFreeBytes) {
            throw 'Insufficient unreserved BlueMap output capacity.'
        }
        $path = Join-Path $reservationsRoot "$PID.json"
        Write-AtomicJson -Path $path -Value @{
            OwnerPid=$PID;OwnerStartTicks=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks;Bytes=$Bytes
        }
        return $path
    } finally { $gate.Dispose() }
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = "$Path.tmp-$PID"
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Get-ManifestUsage {
    param([string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return 0L
    }

    $total = 0L
    # Generations are direct children. A recursive search would traverse every
    # BlueMap model file and get slower with every completed location.
    foreach ($generation in Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue) {
        $manifestFile = Get-Item -LiteralPath (Join-Path $generation.FullName 'manifest.json') -ErrorAction SilentlyContinue
        if ($null -eq $manifestFile) { continue }
        try {
            $manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
            if ($manifest.Status -eq 'complete' -and $null -ne $manifest.OutputBytes) {
                $total += [long]$manifest.OutputBytes
            }
        }
        catch {
            Write-Warning "Ignoring unreadable BlueMap manifest $($manifestFile.FullName): $($_.Exception.Message)"
        }
    }
    return $total
}

function Get-DirectoryMeasurement {
    param([string]$Root)

    $measurement = Get-ChildItem -LiteralPath $Root -File -Recurse | Measure-Object -Property Length -Sum
    [pscustomobject]@{
        Files = [long]$measurement.Count
        Bytes = if ($null -eq $measurement.Sum) { 0L } else { [long]$measurement.Sum }
    }
}

function ConvertTo-HoconString {
    param([AllowEmptyString()][string]$Value)
    return ($Value | ConvertTo-Json -Compress)
}

function Test-BlueMapStaticGeneration {
    param(
        [Parameter(Mandatory = $true)][string]$WebRoot,
        [Parameter(Mandatory = $true)][double]$ExpectedSkyLight,
        [Parameter(Mandatory = $true)][double]$ExpectedAmbientLight,
        [Parameter(Mandatory = $true)][int64]$ExpectedStartX,
        [Parameter(Mandatory = $true)][int64]$ExpectedStartZ
    )

    $settingsPath = Join-Path $WebRoot 'maps\atlas\settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        throw "BlueMap web settings are missing: $settingsPath"
    }
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $epsilon = 0.000001
    if ([Math]::Abs(([double]$settings.skyLight) - $ExpectedSkyLight) -gt $epsilon -or
        [Math]::Abs(([double]$settings.ambientLight) - $ExpectedAmbientLight) -gt $epsilon) {
        throw "BlueMap lighting settings failed validation (sky=$($settings.skyLight), ambient=$($settings.ambientLight))."
    }
    if ([int]$settings.lowres.tileSize[0] -ne 500 -or
        [int]$settings.lowres.tileSize[1] -ne 500 -or
        [int]$settings.lowres.lodFactor -ne 5 -or
        [int]$settings.lowres.lodCount -ne 3) {
        throw 'BlueMap LOD settings do not match the validated Atlas profile.'
    }
    if ($null -eq $settings.startPos -or @($settings.startPos).Count -ne 2 -or
        [int64]$settings.startPos[0] -ne $ExpectedStartX -or
        [int64]$settings.startPos[1] -ne $ExpectedStartZ) {
        throw "BlueMap start position is not anchored to the canonical Atlas location ($ExpectedStartX,$ExpectedStartZ)."
    }

    $mapRoot = Join-Path $WebRoot 'maps\atlas'
    $lowresTileCount = @(Get-ChildItem -LiteralPath $mapRoot -File -Filter '*.png' -Recurse).Count
    $modelFileCount = @(Get-ChildItem -LiteralPath $mapRoot -File -Filter '*.gz' -Recurse).Count
    if ($lowresTileCount -lt 1 -or $modelFileCount -lt 1) {
        throw "BlueMap generation has no usable map payload (png=$lowresTileCount, models=$modelFileCount)."
    }

    return [pscustomobject]@{
        Passed = $true
        SettingsSchema = 'BlueMap maps/atlas/settings.json'
        SkyLight = [double]$settings.skyLight
        AmbientLight = [double]$settings.ambientLight
        StartPosition = @([int64]$settings.startPos[0], [int64]$settings.startPos[1])
        LocationStartExact = $true
        LowresTileCount = $lowresTileCount
        ModelFileCount = $modelFileCount
        StockClientShader = $true
    }
}

function Get-OrDownloadPinnedArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Sha256
    )

    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $temporary = "$Path.download-$PID"
        $previousProgressPreference = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest -Uri $Uri -OutFile $temporary -UseBasicParsing -TimeoutSec 300
        }
        finally {
            $ProgressPreference = $previousProgressPreference
        }
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }

    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant()) {
        throw "Pinned relight artifact hash mismatch: $Path"
    }
    return $Path
}

function Get-DockerLogsText {
    param(
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [int]$Tail = 350
    )

    # Docker forwards application stderr as native stderr. Windows PowerShell
    # turns every such line into an ErrorRecord and, under Stop, mistakes
    # harmless Paper warnings for terminating script failures. Capture both
    # streams while temporarily using Continue, then restore the caller's
    # strict error policy. The container state/exit code is checked separately.
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        return (& docker logs --tail $Tail $ContainerName 2>&1 | Out-String)
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

function Get-DimensionRegionRoot {
    param(
        [Parameter(Mandatory = $true)][string]$WorldRoot,
        [Parameter(Mandatory = $true)][string]$Dimension
    )

    $relativeCandidates = switch ($Dimension) {
        'overworld' { @('region') }
        'nether' { @('DIM-1\region', 'dimensions\minecraft\the_nether\region') }
        'end' { @('DIM1\region', 'dimensions\minecraft\the_end\region') }
        default { throw "Unsupported Anvil dimension '$Dimension'." }
    }
    foreach ($relative in $relativeCandidates) {
        $candidate = Join-Path $WorldRoot $relative
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            return $candidate
        }
    }
    throw "Could not find the $Dimension region directory below $WorldRoot."
}

function Get-ContainedRegionRoot {
    param([Parameter(Mandatory = $true)][string]$DimensionRoot)

    foreach ($relative in @(
        'region',
        'DIM-1\region',
        'DIM1\region',
        'dimensions\minecraft\the_nether\region',
        'dimensions\minecraft\the_end\region')) {
        $candidate = Join-Path $DimensionRoot $relative
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw "Could not find a contained Anvil region directory below $DimensionRoot."
}

function Get-AnvilChunkInventory {
    param([Parameter(Mandatory = $true)][string]$RegionRoot)

    $chunks = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($regionFile in @(Get-ChildItem -LiteralPath $RegionRoot -File -Filter 'r.*.*.mca')) {
        if ($regionFile.Name -notmatch '^r\.(-?\d+)\.(-?\d+)\.mca$') { continue }
        $regionX = [int]$Matches[1]
        $regionZ = [int]$Matches[2]
        $stream = [IO.File]::Open($regionFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            if ($stream.Length -lt 8192) { throw "Region file has a short header: $($regionFile.FullName)" }
            $header = New-Object byte[] 4096
            if ($stream.Read($header, 0, $header.Length) -ne $header.Length) {
                throw "Could not read the region header: $($regionFile.FullName)"
            }
            for ($index = 0; $index -lt 1024; $index++) {
                $offset = $index * 4
                if ($header[$offset] -eq 0 -and $header[$offset + 1] -eq 0 -and
                    $header[$offset + 2] -eq 0 -and $header[$offset + 3] -eq 0) { continue }
                $chunkX = $regionX * 32 + ($index % 32)
                $chunkZ = $regionZ * 32 + [int][Math]::Floor($index / 32)
                [void]$chunks.Add("$chunkX,$chunkZ")
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    return ,$chunks
}

function Remove-GeneratedAnvilChunks {
    param(
        [Parameter(Mandatory = $true)][string]$DimensionRoot,
        [Parameter(Mandatory = $true)]$OriginalChunks
    )

    $removedChunks = 0
    $rewrittenFiles = 0
    $removedFiles = 0
    foreach ($containerName in @('region', 'entities', 'poi')) {
        $baseRegionRoot = Get-ContainedRegionRoot -DimensionRoot $DimensionRoot
        $containerRoot = if ($containerName -eq 'region') {
            $baseRegionRoot
        } else {
            Join-Path (Split-Path -Parent $baseRegionRoot) $containerName
        }
        if (-not (Test-Path -LiteralPath $containerRoot -PathType Container)) { continue }

        foreach ($externalChunk in @(Get-ChildItem -LiteralPath $containerRoot -File -Filter 'c.*.*.mcc')) {
            if ($externalChunk.Name -match '^c\.(-?\d+)\.(-?\d+)\.mcc$') {
                $key = "$([int]$Matches[1]),$([int]$Matches[2])"
                if (-not $OriginalChunks.Contains($key)) {
                    Remove-Item -LiteralPath $externalChunk.FullName -Force
                    $removedFiles++
                }
            }
        }

        foreach ($regionFile in @(Get-ChildItem -LiteralPath $containerRoot -File -Filter 'r.*.*.mca')) {
            if ($regionFile.Name -notmatch '^r\.(-?\d+)\.(-?\d+)\.mca$') { continue }
            $regionX = [int]$Matches[1]
            $regionZ = [int]$Matches[2]
            $bytes = [IO.File]::ReadAllBytes($regionFile.FullName)
            if ($bytes.Length -lt 8192) {
                if ($containerName -eq 'region') {
                    throw "Region file has a short header: $($regionFile.FullName)"
                }

                # Entity/POI sidecars are irrelevant to BlueMap's block model.
                # Paper can leave an empty sidecar when it rejects malformed
                # historical entity data. Keep the block-region lane strict,
                # but remove malformed auxiliary data from this disposable
                # derivative rather than failing a valid terrain render.
                Remove-Item -LiteralPath $regionFile.FullName -Force
                $removedFiles++
                continue
            }
            $compacted = New-Object IO.MemoryStream
            try {
                $compacted.SetLength(8192)
                $nextSector = 2
                $keptChunks = 0
                for ($index = 0; $index -lt 1024; $index++) {
                    $headerOffset = $index * 4
                    $occupied = $bytes[$headerOffset] -ne 0 -or $bytes[$headerOffset + 1] -ne 0 -or
                        $bytes[$headerOffset + 2] -ne 0 -or $bytes[$headerOffset + 3] -ne 0
                    if (-not $occupied) { continue }

                    $chunkX = $regionX * 32 + ($index % 32)
                    $chunkZ = $regionZ * 32 + [int][Math]::Floor($index / 32)
                    if (-not $OriginalChunks.Contains("$chunkX,$chunkZ")) {
                        $removedChunks++
                        continue
                    }

                    $sourceSector = (([int]$bytes[$headerOffset]) -shl 16) -bor
                        (([int]$bytes[$headerOffset + 1]) -shl 8) -bor ([int]$bytes[$headerOffset + 2])
                    $sectorCount = [int]$bytes[$headerOffset + 3]
                    if ($sourceSector -lt 2 -or $sectorCount -lt 1 -or
                        (($sourceSector + $sectorCount) * 4096L) -gt $bytes.Length) {
                        if ($containerName -eq 'region') {
                            throw "Region file has an invalid occupied slot $index`: $($regionFile.FullName)"
                        }

                        # The preserved source remains the authoritative WDL;
                        # omit only the malformed auxiliary record from the
                        # render-only derivative.
                        $removedChunks++
                        continue
                    }
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
                    $keptChunks++
                }

                if ($keptChunks -eq 0) {
                    $compacted.Dispose()
                    $compacted = $null
                    Remove-Item -LiteralPath $regionFile.FullName -Force
                    $removedFiles++
                    continue
                }
                $compacted.SetLength($nextSector * 4096L)
                $temporary = "$($regionFile.FullName).pruned-$PID"
                $output = [IO.File]::Open($temporary, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    $compacted.Position = 0
                    $compacted.CopyTo($output)
                    $output.Flush($true)
                }
                finally {
                    $output.Dispose()
                }
                Move-Item -LiteralPath $temporary -Destination $regionFile.FullName -Force
                $rewrittenFiles++
            }
            finally {
                if ($null -ne $compacted) { $compacted.Dispose() }
            }
        }
    }

    return [pscustomobject]@{
        RemovedChunkRecords = $removedChunks
        RewrittenRegionFiles = $rewrittenFiles
        RemovedFiles = $removedFiles
    }
}

function Invoke-AtlasWorldRelight {
    param(
        [Parameter(Mandatory = $true)][string]$ScratchJob,
        [Parameter(Mandatory = $true)][string]$ExtractedWorldRoot,
        [Parameter(Mandatory = $true)][string]$Dimension,
        # Older level.dat files can predate Version.Name. The existing fallback
        # below handles them; do not reject the source before selecting it.
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$SourceVersionName,
        [Parameter(Mandatory = $true)][int]$RenderId
    )

    $paperVersion = if ($SourceVersionName -match '^1\.21\.11(?:\.|$)') { '1.21.11' } else { '1.21.10' }
    $paperArtifact = switch ($paperVersion) {
        '1.21.11' {
            @{
                FileName = 'paper-1.21.11-132.jar'
                Uri = 'https://fill-data.papermc.io/v1/objects/5ffef465eeeb5f2a3c23a24419d97c51afd7dbb4923ff42df9a3f58bba1ccfba/paper-1.21.11-132.jar'
                Sha256 = '5ffef465eeeb5f2a3c23a24419d97c51afd7dbb4923ff42df9a3f58bba1ccfba'
            }
        }
        default {
            @{
                FileName = 'paper-1.21.10-130.jar'
                Uri = 'https://fill-data.papermc.io/v1/objects/158703f75a26f842ea656b3dc6d75bf3d1ec176b97a2c36384d0b80b3871af53/paper-1.21.10-130.jar'
                Sha256 = '158703f75a26f842ea656b3dc6d75bf3d1ec176b97a2c36384d0b80b3871af53'
            }
        }
    }

    $paperPath = Get-OrDownloadPinnedArtifact `
        -Path (Join-Path $RelightToolRoot $paperArtifact.FileName) `
        -Uri $paperArtifact.Uri `
        -Sha256 $paperArtifact.Sha256
    $lightCleanerPath = Get-OrDownloadPinnedArtifact `
        -Path (Join-Path $RelightToolRoot 'plugins\LightCleaner-2.0.0-154.jar') `
        -Uri 'https://ci.mg-dev.eu/job/Light-Cleaner/154/artifact/target/LightCleaner-2.0.0-154.jar' `
        -Sha256 '6a91ec9b0d71e6d86b7d4739975e8ffffe78bcba1a038359ae2f406d73e43087'
    $bkCommonLibPath = Get-OrDownloadPinnedArtifact `
        -Path (Join-Path $RelightToolRoot 'plugins\BKCommonLib-2.0.3-SNAPSHOT-2043.jar') `
        -Uri 'https://ci.mg-dev.eu/job/BKCommonLib/2043/artifact/build/BKCommonLib-2.0.3-SNAPSHOT-2043.jar' `
        -Sha256 'b8b06b4643da79b2b10333247f2a4f5b06f663528fdeba41921ba29b3ac27de2'

    $sourceDimensionRegionRoot = Get-DimensionRegionRoot -WorldRoot $ExtractedWorldRoot -Dimension $Dimension
    $originalChunks = Get-AnvilChunkInventory -RegionRoot $sourceDimensionRegionRoot
    if ($originalChunks.Count -lt 1) {
        throw "No original $Dimension chunks were found to relight for render $RenderId."
    }

    $serverRoot = Join-Path $ScratchJob 'relight-server'
    $serverWorldRoot = Join-Path $serverRoot 'world'
    New-Item -ItemType Directory -Path $serverRoot -Force | Out-Null
    Assert-ChildPath -Parent $ScratchJob -Child $serverRoot
    Assert-ChildPath -Parent $ScratchJob -Child $ExtractedWorldRoot
    if (Test-Path -LiteralPath $serverWorldRoot) {
        throw "Relight world target already exists: $serverWorldRoot"
    }
    Move-Item -LiteralPath $ExtractedWorldRoot -Destination $serverWorldRoot

    Copy-Item -LiteralPath $paperPath -Destination (Join-Path $serverRoot 'paper.jar') -Force
    $pluginRoot = Join-Path $serverRoot 'plugins'
    New-Item -ItemType Directory -Path $pluginRoot -Force | Out-Null
    Copy-Item -LiteralPath $lightCleanerPath -Destination $pluginRoot -Force
    Copy-Item -LiteralPath $bkCommonLibPath -Destination $pluginRoot -Force

    $lightCleanerConfigRoot = Join-Path $pluginRoot 'LightCleaner'
    New-Item -ItemType Directory -Path $lightCleanerConfigRoot -Force | Out-Null
    @"
minFreeMemory: 800
skipWorldEdge: false
autoCleanEnabled: false
autoCleanWorldEditEnabled: false
asyncLoadConcurrency: 75
unsavedWorldNames: []
"@ | Set-Content -LiteralPath (Join-Path $lightCleanerConfigRoot 'config.yml') -Encoding UTF8

    $worldName = switch ($Dimension) {
        'overworld' { 'world' }
        'nether' { 'world_nether' }
        'end' { 'world_the_end' }
        default { throw "Unsupported relight dimension '$Dimension'." }
    }
    # Paper's bundler otherwise downloads the same Mojang server, applies the
    # same patches, and expands the same libraries into every disposable job.
    # These version-keyed runtime caches contain no world data and are safe to
    # reuse within a worker because its slot is exclusive and every Paper JAR
    # is SHA-pinned above. Coordinated slots never share these mutable folders.
    $paperCacheRoot = Join-Path $RelightToolRoot ("server-cache\{0}" -f $paperVersion)
    if ($WorkerId -gt 0) { $paperCacheRoot = Join-Path $RelightToolRoot ("server-cache-worker-{0}\{1}" -f $WorkerId, $paperVersion) }
    $paperRuntimeMounts = [ordered]@{}
    foreach ($runtimeDirectory in @('cache', 'libraries', 'versions', '.cache')) {
        $hostDirectory = Join-Path $paperCacheRoot $runtimeDirectory
        New-Item -ItemType Directory -Path $hostDirectory -Force | Out-Null
        $paperRuntimeMounts[$runtimeDirectory] = "type=bind,source=$hostDirectory,target=/data/$runtimeDirectory"
    }
    $containerName = "atlas-bluemap-relight-$RenderId-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8))".ToLowerInvariant()
    $rconPassword = [guid]::NewGuid().ToString('N')
    $serverMount = "type=bind,source=$serverRoot,target=/data"
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $containerStarted = $false
    $stageLease = $null
    try {
        $stageLease = Enter-BlueMapStage 'relighting' 12GB $containerName
        $stopwatch.Restart()
        & docker run -d --name $containerName --cpus $RelightCpuCount --memory $RelightMemoryLimit `
            --mount $serverMount `
            --mount $paperRuntimeMounts['cache'] `
            --mount $paperRuntimeMounts['libraries'] `
            --mount $paperRuntimeMounts['versions'] `
            --mount $paperRuntimeMounts['.cache'] `
            -e EULA=TRUE -e TYPE=CUSTOM -e CUSTOM_SERVER=/data/paper.jar `
            -e ONLINE_MODE=FALSE -e MEMORY=10G -e INIT_MEMORY=2G -e MAX_MEMORY=10G `
            -e ENABLE_RCON=TRUE -e RCON_PASSWORD=$rconPassword -e OVERRIDE_SERVER_PROPERTIES=TRUE `
            -e VIEW_DISTANCE=2 -e SIMULATION_DISTANCE=2 -e SPAWN_PROTECTION=0 -e MAX_TICK_TIME=-1 `
            -e GENERATE_STRUCTURES=FALSE -e SPAWN_ANIMALS=FALSE -e SPAWN_MONSTERS=FALSE -e SPAWN_NPCS=FALSE `
            -e ENABLE_AUTOPAUSE=FALSE -e STOP_SERVER_ANNOUNCE_DELAY=0 `
            $RelightImage | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not start relight container $containerName." }
        $containerStarted = $true

        $startupDeadline = (Get-Date).AddMinutes(6)
        do {
            Start-Sleep -Seconds 3
            $running = (& docker inspect $containerName --format '{{.State.Running}}' 2>$null | Out-String).Trim()
            $logs = Get-DockerLogsText -ContainerName $containerName
            if ($running -ne 'true') {
                throw "Relight server exited before startup completed. Tail:`n$logs"
            }
            $ready = $logs -match 'LightCleaner version .* enabled' -and $logs -match 'Done \('
        } while (-not $ready -and (Get-Date) -lt $startupDeadline)
        if (-not $ready) { throw "Relight server did not become ready within six minutes. Tail:`n$logs" }

        $startOutput = (& docker exec $containerName rcon-cli cleanlight world $worldName 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $startOutput -notmatch 'is now being fixed') {
            throw "Light Cleaner did not accept ${worldName}: $startOutput"
        }
        Write-Host "Relighting render $RenderId $Dimension with Paper $paperVersion ..."

        $relightDeadline = (Get-Date).AddMinutes($RelightTimeoutMinutes)
        $lastReport = [DateTime]::MinValue
        do {
            Start-Sleep -Seconds 5
            $running = (& docker inspect $containerName --format '{{.State.Running}}' 2>$null | Out-String).Trim()
            if ($running -ne 'true') {
                $logs = Get-DockerLogsText -ContainerName $containerName
                throw "Relight server exited while processing $worldName. Tail:`n$logs"
            }
            $statusOutput = (& docker exec $containerName rcon-cli cleanlight status 2>&1 | Out-String)
            if ((Get-Date) -ge $lastReport.AddSeconds(30)) {
                $oneLine = ($statusOutput -replace '\x1b\[[0-9;]*m', '' -replace '\s+', ' ').Trim()
                Write-Host "Relight render $RenderId`: $oneLine"
                $lastReport = Get-Date
            }
            $complete = $statusOutput -match 'No lighting is being processed at this time'
        } while (-not $complete -and (Get-Date) -lt $relightDeadline)
        if (-not $complete) { throw "Relighting render $RenderId exceeded $RelightTimeoutMinutes minutes." }

        & docker exec $containerName rcon-cli save-all flush | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not flush relit world for render $RenderId." }
        & docker exec $containerName rcon-cli stop | Out-Null
        $shutdownDeadline = (Get-Date).AddMinutes(4)
        do {
            Start-Sleep -Seconds 2
            $running = (& docker inspect $containerName --format '{{.State.Running}}' 2>$null | Out-String).Trim()
        } while ($running -eq 'true' -and (Get-Date) -lt $shutdownDeadline)
        if ($running -eq 'true') { throw "Relight server did not stop cleanly for render $RenderId." }

        $stopwatch.Stop()
        $relitWorldRoot = Join-Path $serverRoot $worldName
        if (-not (Test-Path -LiteralPath (Join-Path $relitWorldRoot 'level.dat') -PathType Leaf)) {
            throw "Relit $Dimension world root is missing level.dat: $relitWorldRoot"
        }
        $pruned = Remove-GeneratedAnvilChunks -DimensionRoot $relitWorldRoot -OriginalChunks $originalChunks
        $relitRegionRoot = Get-ContainedRegionRoot -DimensionRoot $relitWorldRoot
        $finalChunks = Get-AnvilChunkInventory -RegionRoot $relitRegionRoot
        if (-not $finalChunks.SetEquals($originalChunks)) {
            throw "Relight footprint audit failed for render $RenderId ($($originalChunks.Count) original, $($finalChunks.Count) final chunks)."
        }
        return [pscustomobject]@{
            WorldRoot = $relitWorldRoot
            PaperVersion = $paperVersion
            PaperSha256 = $paperArtifact.Sha256
            LightCleanerVersion = '2.0.0-154'
            LightCleanerSha256 = '6a91ec9b0d71e6d86b7d4739975e8ffffe78bcba1a038359ae2f406d73e43087'
            BKCommonLibVersion = '2.0.3-SNAPSHOT-2043'
            BKCommonLibSha256 = 'b8b06b4643da79b2b10333247f2a4f5b06f663528fdeba41921ba29b3ac27de2'
            Seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
            EdgeChunksSkipped = $false
            OriginalChunkCount = $originalChunks.Count
            GeneratedChunkRecordsPruned = $pruned.RemovedChunkRecords
            RewrittenRegionFiles = $pruned.RewrittenRegionFiles
            RemovedGeneratedFiles = $pruned.RemovedFiles
            FootprintAuditExact = $true
            PersistentPaperRuntimeCache = $true
        }
    }
    finally {
        if ($containerStarted) {
            Remove-OwnedDockerResource -Name $containerName
        }
        Exit-BlueMapStage $stageLease
    }
}

function Assert-ChildPath {
    param(
        [string]$Parent,
        [string]$Child
    )

    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing operation outside $Parent`: $Child"
    }
}

function Update-RunStatus {
    param(
        [string]$State,
        [int]$Completed,
        [int]$Total,
        $Current,
        $Results,
        [string]$Message = $null
    )

    $status = [ordered]@{
        SchemaVersion = 1
        State = $State
        StartedUtc = $runStartedUtc.ToString('o')
        UpdatedUtc = [DateTime]::UtcNow.ToString('o')
        Completed = $Completed
        Total = $Total
        Percent = if ($Total -le 0) { 100 } else { [math]::Round(($Completed * 100.0) / $Total, 1) }
        Current = $Current
        Results = @($Results)
        Message = $Message
        OutputRoot = $OutputRoot
        OutputQuotaBytes = $OutputQuotaBytes
        WorkerId = $WorkerId
        Stage = $currentStage
        JobStartedUtc = $jobStartedUtc
    }
    Write-AtomicJson -Value $status -Path $statusPath
}

if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) {
    throw "Atlas database was not found: $DatabasePath"
}
if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
    throw "Atlas WDL object store was not found: $SourceRoot"
}
if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    throw 'sqlite3 is required for the BlueMap render queue.'
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required for the pinned BlueMap renderer.'
}

New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
New-Item -ItemType Directory -Path $ScratchRoot -Force | Out-Null
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

$lockStream = $null
$generationLock = $null
try {
    $lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    if ($WorkerId -gt 0) {
        $claimsRoot = Join-Path $coordinationRoot 'claims'
        New-Item -ItemType Directory -Path $claimsRoot -Force | Out-Null
        $generationLock = [IO.File]::Open((Join-Path $claimsRoot ("render-{0}.lock" -f $RenderId[0])), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
}
catch {
    throw "Another Atlas BlueMap batch owns $lockPath"
}

$results = [Collections.Generic.List[object]]::new()

try {
    if ($All -and $RenderId.Count -gt 0) {
        throw 'Choose either -All or explicit -RenderId values, not both.'
    }
    if (-not $All -and $RenderId.Count -eq 0) {
        throw 'Supply at least one -RenderId or use -All.'
    }

    $renderIds = @($RenderId | Sort-Object -Unique)
    $idList = ($renderIds | ForEach-Object { [int]$_ }) -join ','
    $selectionClause = if ($All) { '' } else { "AND j.RenderId IN ($idList)" }
    $sql = @"
WITH ranked AS (
    SELECT
        j.Id AS JobId,
        j.RenderId,
        j.Name AS JobName,
        l.Rowid AS LocationId,
        l.Name AS LocationName,
        l.X AS LocationX,
        l.Y AS LocationY,
        l.Z AS LocationZ,
        j.Dimension,
        j.RenderTopY,
        j.ArchiveSha256,
        j.OriginalFileName,
        json_extract(j.InspectionJson, '$.VersionName') AS VersionName,
        json_extract(j.InspectionJson, '$.DataVersion') AS DataVersion,
        json_extract(j.InspectionJson, '$.Dimensions[0].ChunkCount') AS ChunkCount,
        r.WorldDownloadDate,
        r.MinX,
        r.MinZ,
        r.MaxXExclusive,
        r.MaxZExclusive,
        ROW_NUMBER() OVER (PARTITION BY j.RenderId ORDER BY COALESCE(j.CompletedUtc, j.UpdatedUtc, j.RequestedUtc) DESC, j.Id DESC) AS rn
    FROM IngestionJobs j
    JOIN Renders r ON r.Id = j.RenderId
    JOIN Locations l ON l.Rowid = r.LocationRowid
    WHERE lower(j.Status) = 'completed'
      AND j.ArchiveSha256 IS NOT NULL
      $selectionClause
)
SELECT * FROM ranked WHERE rn = 1 ORDER BY RenderId;
"@

    $json = & sqlite3 -json $DatabasePath $sql
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not query Atlas render provenance.'
    }
    $parsedJobs = (($json -join "`n") | ConvertFrom-Json)
    $jobs = [Collections.Generic.List[object]]::new()
    if ($parsedJobs -is [Array]) {
        foreach ($parsedJob in $parsedJobs) {
            $jobs.Add($parsedJob)
        }
    }
    elseif ($null -ne $parsedJobs) {
        $jobs.Add($parsedJobs)
    }

    $availableRenderIds = @()
    foreach ($availableJob in $jobs) {
        $availableRenderIds += [int]$availableJob.RenderId
    }
    $missingIds = @($renderIds | Where-Object { $_ -notin $availableRenderIds })
    if (-not $All -and $missingIds.Count -gt 0) {
        throw "No completed source-backed ingestion job was found for render IDs: $($missingIds -join ', ')"
    }

    Update-RunStatus -State 'running' -Completed 0 -Total $jobs.Count -Current $null -Results $results

    $completedCount = 0
    foreach ($job in $jobs) {
        $jobStartedUtc = [datetime]::UtcNow.ToString('o')
        $sha = ([string]$job.ArchiveSha256).ToLowerInvariant()
        $renderIdValue = [int]$job.RenderId
        $generationName = "render-$renderIdValue-$sha-v$blueMapVersion-p$rendererProfileVersion"
        $finalRoot = Join-Path $OutputRoot $generationName
        $manifestPath = Join-Path $finalRoot 'manifest.json'
        $current = [ordered]@{
            RenderId = $renderIdValue
            LocationId = [int]$job.LocationId
            LocationName = [string]$job.LocationName
            Dimension = [string]$job.Dimension
            SourceSha256 = $sha
        }
        Update-RunStatus -State 'running' -Completed $completedCount -Total $jobs.Count -Current $current -Results $results

        $jobWebVolume = $null
        $reservationPath = $null
        $renderStageLease = $null
        try {
            if (-not $Force -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
                $existing = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
                $hasQualityGate = $null -ne $existing.PSObject.Properties['QualityGate'] -and
                    $null -ne $existing.QualityGate.PSObject.Properties['Passed'] -and
                    [bool]$existing.QualityGate.Passed
                $hasExactFootprint = $null -ne $existing.PSObject.Properties['RenderingProfile'] -and
                    $null -ne $existing.RenderingProfile.PSObject.Properties['Relight'] -and
                    $null -ne $existing.RenderingProfile.Relight.PSObject.Properties['FootprintAuditExact'] -and
                    [bool]$existing.RenderingProfile.Relight.FootprintAuditExact
                $existingSettingsPath = Join-Path $finalRoot 'web\maps\atlas\settings.json'
                $hasLocationStart = $false
                if ($null -ne $existing.PSObject.Properties['QualityGate'] -and
                    $null -ne $existing.QualityGate.PSObject.Properties['LocationStartExact'] -and
                    [bool]$existing.QualityGate.LocationStartExact -and
                    (Test-Path -LiteralPath $existingSettingsPath -PathType Leaf)) {
                    $existingSettings = Get-Content -LiteralPath $existingSettingsPath -Raw | ConvertFrom-Json
                    $hasLocationStart = $null -ne $existingSettings.startPos -and
                        @($existingSettings.startPos).Count -eq 2 -and
                        [int64]$existingSettings.startPos[0] -eq [int64]$job.LocationX -and
                        [int64]$existingSettings.startPos[1] -eq [int64]$job.LocationZ
                }
                if ($existing.Status -eq 'complete' -and $existing.SourceSha256 -eq $sha -and
                    $existing.BlueMapVersion -eq $blueMapVersion -and
                    [int]$existing.RendererProfileVersion -eq $rendererProfileVersion -and
                    $hasQualityGate -and $hasExactFootprint -and $hasLocationStart) {
                    $results.Add([pscustomobject]@{
                        RenderId = $renderIdValue
                        LocationName = [string]$job.LocationName
                        State = 'already-complete'
                        OutputRoot = $finalRoot
                        OutputBytes = [long]$existing.OutputBytes
                    })
                    $completedCount++
                    continue
                }
            }

            $versionName = [string]$job.VersionName

            $usedBytes = Get-ManifestUsage -Root $OutputRoot
            if ($usedBytes -ge $OutputQuotaBytes) {
                throw "BlueMap output quota of $OutputQuotaBytes bytes is already exhausted."
            }

            $outputDrive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($OutputRoot).Substring(0, 1))
            if ([long]$outputDrive.Free -lt $MinimumFreeBytes) {
                throw "BlueMap output drive has less than $MinimumFreeBytes bytes free."
            }

            $sourceZip = Join-Path (Join-Path $SourceRoot $sha.Substring(0, 2)) "$sha.zip"
            $scratchDrive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($ScratchRoot).Substring(0, 1))
            if ([long]$scratchDrive.Free -lt 100GB) {
                throw 'BlueMap scratch drive requires at least 100 GiB free before admitting a new job.'
            }
            if (-not (Test-Path -LiteralPath $sourceZip -PathType Leaf)) {
                throw "Immutable source WDL is missing: $sourceZip"
            }

            $scratchJob = Join-Path $ScratchRoot "$generationName-$([guid]::NewGuid().ToString('N'))"
            Assert-ChildPath -Parent $ScratchRoot -Child $scratchJob
            New-Item -ItemType Directory -Path $scratchJob -Force | Out-Null
            Set-WorkerStage 'staging source'
            $localSourceZip = Join-Path $scratchJob 'source.zip'
            Copy-Item -LiteralPath $sourceZip -Destination $localSourceZip -ErrorAction Stop
            Set-WorkerStage 'verifying source'
            $actualHash = (Get-FileHash -LiteralPath $localSourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $sha) {
                throw "Source WDL hash mismatch for render $renderIdValue."
            }

            $extractRoot = Join-Path $scratchJob 'extract'
            Set-WorkerStage 'extracting'
            New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

            & tar.exe -xf $localSourceZip -C $extractRoot
            if ($LASTEXITCODE -ne 0) {
                throw "Could not extract source WDL for render $renderIdValue."
            }

            $levelFiles = @(Get-ChildItem -LiteralPath $extractRoot -Filter 'level.dat' -File -Recurse)
            if ($levelFiles.Count -ne 1) {
                throw "Expected exactly one level.dat in render $renderIdValue source; found $($levelFiles.Count)."
            }

            $worldRoot = $levelFiles[0].Directory.FullName
            Assert-ChildPath -Parent $scratchJob -Child $worldRoot

            $dimension = ([string]$job.Dimension).ToLowerInvariant()
            Set-WorkerStage 'relighting'
            $relight = Invoke-AtlasWorldRelight `
                -ScratchJob $scratchJob `
                -ExtractedWorldRoot $worldRoot `
                -Dimension $dimension `
                -SourceVersionName $versionName `
                -RenderId $renderIdValue
            $worldRoot = $relight.WorldRoot
            $renderStageLease = Enter-BlueMapStage 'rendering' 8GB
            Set-WorkerStage 'configuring'
            $containerWorld = '/world'

            # Keep the image's /app working directory visible because its entrypoint
            # is `java -jar cli.jar`. Mounting over /app hides the renderer itself.
            $mount = "type=bind,source=$scratchJob,target=/work"
            & docker run --rm --cpus $CpuCount --memory $MemoryLimit --mount $mount $BlueMapImage -c /work/config
            # The CLI currently exits 1 after printing help when invoked only to
            # generate defaults, even though the configuration was written.
            if ($LASTEXITCODE -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $scratchJob 'config\core.conf') -PathType Leaf)) {
                throw "BlueMap could not generate configuration for render $renderIdValue."
            }

            $configRoot = Join-Path $scratchJob 'config'
            $corePath = Join-Path $configRoot 'core.conf'
            $webappPath = Join-Path $configRoot 'webapp.conf'
            $webserverPath = Join-Path $configRoot 'webserver.conf'
            $fileStoragePath = Join-Path $configRoot 'storages\file.conf'
            $mapsRoot = Join-Path $configRoot 'maps'
            New-Item -ItemType Directory -Path $mapsRoot -Force | Out-Null

            $core = Get-Content -LiteralPath $corePath -Raw
            $core = [regex]::Replace($core, 'accept-download:\s*false', 'accept-download: true')
            $core = [regex]::Replace($core, 'data:\s*"[^"]+"', 'data: "/work/data"')
            $core = [regex]::Replace($core, 'render-thread-count:\s*[-0-9]+', "render-thread-count: $CpuCount")
            $core = [regex]::Replace($core, 'metrics:\s*true', 'metrics: false')
            Set-Content -LiteralPath $corePath -Value $core -Encoding UTF8

            $webapp = Get-Content -LiteralPath $webappPath -Raw
            $webapp = [regex]::Replace($webapp, 'webroot:\s*"[^"]+"', 'webroot: "/work/web"')
            $webapp = [regex]::Replace($webapp, '(?m)^#?client-decompression:\s*(true|false)\s*$', 'client-decompression: true')
            $webapp = [regex]::Replace($webapp, 'default-to-flat-view:\s*true', 'default-to-flat-view: false')
            # Mirror the proven 2b2t.info BlueMap viewer distances. The default
            # remains conservative while users can opt into a much wider 3D
            # radius for large bases.
            $webapp = [regex]::Replace($webapp, 'hires-slider-max:\s*\d+', 'hires-slider-max: 10000')
            $webapp = [regex]::Replace($webapp, 'hires-slider-default:\s*\d+', 'hires-slider-default: 500')
            $webapp = [regex]::Replace($webapp, 'lowres-slider-max:\s*\d+', 'lowres-slider-max: 100000')
            $webapp = [regex]::Replace($webapp, 'lowres-slider-default:\s*\d+', 'lowres-slider-default: 2000')
            Set-Content -LiteralPath $webappPath -Value $webapp -Encoding UTF8

            $webserver = Get-Content -LiteralPath $webserverPath -Raw
            $webserver = [regex]::Replace($webserver, '(?m)^enabled:\s*true\s*$', 'enabled: false')
            $webserver = [regex]::Replace($webserver, 'webroot:\s*"[^"]+"', 'webroot: "/work/web"')
            Set-Content -LiteralPath $webserverPath -Value $webserver -Encoding UTF8

            $storage = Get-Content -LiteralPath $fileStoragePath -Raw
            $storage = [regex]::Replace($storage, 'root:\s*"[^"]+"', 'root: "/work/web/maps"')
            $storage = [regex]::Replace($storage, 'compression:\s*\w+', 'compression: gzip')
            Set-Content -LiteralPath $fileStoragePath -Value $storage -Encoding UTF8

            Get-ChildItem -LiteralPath $mapsRoot -Filter '*.conf' -File -ErrorAction SilentlyContinue | Remove-Item -Force

            $minX = [int64]$job.MinX
            $minZ = [int64]$job.MinZ
            $maxX = [int64]$job.MaxXExclusive - 1
            $maxZ = [int64]$job.MaxZExclusive - 1
            if ($maxX -lt $minX -or $maxZ -lt $minZ) {
                throw "Render $renderIdValue has invalid bounds."
            }
            # WDLs can be sparse and may contain multiple preserved clusters. A
            # bounds midpoint can therefore land in empty terrain, or halfway
            # between an Overworld base and an old 8:1-scaled companion cluster.
            # The Atlas location coordinate is the canonical camera anchor.
            $centerX = [int64]$job.LocationX
            $centerZ = [int64]$job.LocationZ
            $dimensionKey = switch ($dimension) {
                'overworld' { 'minecraft:overworld' }
                'nether' { 'minecraft:the_nether' }
                'end' { 'minecraft:the_end' }
                default { throw "Unsupported Atlas dimension '$dimension'." }
            }
            # BlueMap 5.x exposes sky-light as a normalized display strength:
            # 0 is dark and 1 is fully lit. It is not Minecraft's 0-15 block
            # light value. Using 15 clips the client shader and produces the
            # camera-dependent white/neon output seen in profile 2.
            # Match BlueMap's normal dimension profiles (and the good-looking
            # 2b2t.info Overworld map). The isolated derivative was relit above;
            # do not hide damaged light data with extreme display brightness.
            $ambientLight = if ($dimension -eq 'overworld') { '0.1' } else { '0.6' }
            $skyLight = if ($dimension -eq 'overworld') { '1' } else { '0' }
            $skyLightValue = [double]::Parse($skyLight, [Globalization.CultureInfo]::InvariantCulture)
            if ($skyLightValue -lt 0 -or $skyLightValue -gt 1) {
                throw "BlueMap sky-light must be in the normalized range 0..1; received $skyLight."
            }
            $removeCaves = if ($dimension -eq 'overworld') { '55' } else { '-10000' }
            $mapName = ConvertTo-HoconString -Value "$($job.LocationName) - $($job.WorldDownloadDate)"
            $worldValue = ConvertTo-HoconString -Value $containerWorld

            $maskLines = @(
                '  {',
                '    type: box',
                "    min-x: $minX",
                "    max-x: $maxX",
                "    min-z: $minZ",
                "    max-z: $maxZ"
            )
            if ($null -ne $job.RenderTopY -and -not [string]::IsNullOrWhiteSpace([string]$job.RenderTopY)) {
                $maskLines += "    max-y: $([int]$job.RenderTopY)"
            }
            $maskLines += '  }'
            if ($dimension -eq 'nether') {
                $maskLines += @(
                    '  {',
                    '    type: box',
                    '    subtract: true',
                    '    min-y: 90',
                    '    max-y: 127',
                    '  }'
                )
            }
            $renderMask = $maskLines -join "`n"

            $mapConfig = @"
world: $worldValue
dimension: "$dimensionKey"
name: $mapName
sorting: 0
start-pos: { x: $centerX, z: $centerZ }
sky-light: $skyLight
ambient-light: $ambientLight
remove-caves-below-y: $removeCaves
cave-detection-ocean-floor: -5
cave-detection-uses-block-light: false
min-inhabited-time: 0
render-mask: [
$renderMask
]
render-edges: true
edge-light-strength: 8
enable-perspective-view: true
enable-flat-view: true
enable-free-flight-view: true
enable-hires: true
storage: "file"
# Atlas WDLs are intentionally sparse. BlueMap's strict mode rejects an entire
# tile when a neighboring chunk is absent, even after every stored chunk is
# correctly relit. Permit sparse tile edges; the stored chunks were repaired
# immediately before this render, so its full-bright fallback is exceptional.
ignore-missing-light-data: true
lowres-tile-size: 500
lod-count: 3
lod-factor: 5
marker-sets: {}
"@
            Set-Content -LiteralPath (Join-Path $mapsRoot 'atlas.conf') -Value $mapConfig -Encoding UTF8

            $renderLog = Join-Path $scratchJob 'bluemap-render.log'
            # BlueMap produces a very large number of tiny model files. Rendering
            # those through Docker Desktop's Windows bind mount is slow and can
            # intermittently fail writes. Keep the active webroot and reusable
            # renderer cache on Docker's native Linux filesystem, then export the
            # completed immutable webroot to F: in one operation.
            # Creation is idempotent and preserves existing data. On PowerShell
            # 5.1 a missing-volume inspect can throw on stderr before LASTEXITCODE
            # is checked, so fresh worker caches must not use inspect-then-create.
            & docker volume create $sharedCacheVolume | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not create worker BlueMap cache volume $sharedCacheVolume." }
            $jobWebVolume = "atlas-bluemap-web-$renderIdValue-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 12))".ToLowerInvariant()
            & docker volume create $jobWebVolume | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Could not create BlueMap web volume $jobWebVolume." }

            $worldMount = "type=bind,source=$worldRoot,target=/world,readonly"
            $configMount = "type=bind,source=$configRoot,target=/work/config,readonly"
            $webMount = "type=volume,source=$jobWebVolume,target=/work/web"
            $cacheMount = "type=volume,source=$sharedCacheVolume,target=/work/data"
            $renderContainerName = "atlas-bluemap-render-$renderIdValue-$PID-$([guid]::NewGuid().ToString('N').Substring(0, 8))".ToLowerInvariant()
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            Set-WorkerStage 'rendering'
            try {
                & docker run --rm --name $renderContainerName --cpus $CpuCount --memory $MemoryLimit `
                    --mount $worldMount --mount $configMount --mount $webMount --mount $cacheMount `
                    $BlueMapImage -c /work/config -r -g -s --markers -m atlas 2>&1 |
                    Tee-Object -FilePath $renderLog
                $renderExitCode = $LASTEXITCODE
            }
            finally {
                $stopwatch.Stop()
                # A normally completed --rm container no longer exists. This
                # fallback handles interrupted shells and failed Docker clients.
                Remove-OwnedDockerResource -Name $renderContainerName
            }
            if ($renderExitCode -ne 0) {
                throw "BlueMap exited with code $renderExitCode for render $renderIdValue."
            }

            $webRoot = Join-Path $scratchJob 'web'
            Set-WorkerStage 'exporting'
            New-Item -ItemType Directory -Path $webRoot -Force | Out-Null
            $exportMount = "type=bind,source=$webRoot,target=/export"
            $exportSourceMount = "type=volume,source=$jobWebVolume,target=/source,readonly"
            & docker run --rm --entrypoint sh --mount $exportSourceMount --mount $exportMount $BlueMapImage `
                -c 'cp -a /source/. /export/'
            if ($LASTEXITCODE -ne 0) {
                throw "Could not export the completed BlueMap webroot for render $renderIdValue."
            }
            $mapRoot = Join-Path $webRoot 'maps\atlas'
            if (-not (Test-Path -LiteralPath (Join-Path $webRoot 'index.html') -PathType Leaf) -or
                -not (Test-Path -LiteralPath $mapRoot -PathType Container)) {
                throw "BlueMap did not generate a complete static webroot for render $renderIdValue."
            }

            $quality = Test-BlueMapStaticGeneration `
                -WebRoot $webRoot `
                -ExpectedSkyLight $skyLightValue `
                -ExpectedAmbientLight ([double]$ambientLight) `
                -ExpectedStartX $centerX `
                -ExpectedStartZ $centerZ

            $webMeasurement = Get-DirectoryMeasurement -Root $webRoot
            # Reserve final disk space before copying. Small allowance covers
            # configuration, logs and metadata; final accounting is rechecked.
            $reservationPath = Reserve-Output -Bytes ($webMeasurement.Bytes + 16MB)

            $stagingRoot = "$finalRoot.staging-$PID"
            Assert-ChildPath -Parent $OutputRoot -Child $stagingRoot
            New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

            # Renderer cache (including the downloaded Minecraft client JAR) is
            # scratch-only. It is not needed by the generated static site and
            # retaining one copy per render would waste tens of GiB at catalog
            # scale.
            foreach ($folder in @('web', 'config')) {
                $sourceFolder = Join-Path $scratchJob $folder
                if (Test-Path -LiteralPath $sourceFolder -PathType Container) {
                    $destinationFolder = Join-Path $stagingRoot $folder
                    New-Item -ItemType Directory -Path $destinationFolder -Force | Out-Null
                    & robocopy $sourceFolder $destinationFolder /E /COPY:DAT /DCOPY:DAT /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
                    if ($LASTEXITCODE -gt 7) {
                        throw "Could not promote BlueMap $folder for render $renderIdValue (robocopy $LASTEXITCODE)."
                    }
                }
            }
            Copy-Item -LiteralPath $renderLog -Destination (Join-Path $stagingRoot 'bluemap-render.log') -Force

            # OutputBytes intentionally measures the immutable served/config/log
            # payload and excludes manifest.json itself, whose length necessarily
            # changes when its own measurements are serialized.
            $generationMeasurement = Get-DirectoryMeasurement -Root $stagingRoot
            Set-WorkerStage 'publishing'
            $publicationLock = Enter-PublicationLock
            try {
            # Recheck global usage under the lock; another worker may have
            # published while this job was rendering/copying to private staging.
            $usedBytes = Get-ManifestUsage -Root $OutputRoot
            $outputDrive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($OutputRoot).Substring(0, 1))
            if ([long]$outputDrive.Free -lt $MinimumFreeBytes) { throw 'BlueMap output volume is below the free-space floor.' }
            if (($usedBytes + $generationMeasurement.Bytes) -gt $OutputQuotaBytes) {
                Assert-ChildPath -Parent $OutputRoot -Child $stagingRoot
                Remove-Item -LiteralPath $stagingRoot -Recurse -Force
                throw 'Promoting this generation would exceed the 500 GiB BlueMap output quota.'
            }

            $manifest = [ordered]@{
                SchemaVersion = 2
                Status = 'complete'
                RenderId = $renderIdValue
                LocationId = [int]$job.LocationId
                LocationName = [string]$job.LocationName
                LocationCoordinates = [ordered]@{
                    X = [int64]$job.LocationX
                    Y = if ($null -eq $job.LocationY) { $null } else { [int64]$job.LocationY }
                    Z = [int64]$job.LocationZ
                }
                WorldDownloadDate = [string]$job.WorldDownloadDate
                Dimension = $dimension
                RenderTopY = if ($null -eq $job.RenderTopY) { $null } else { [int]$job.RenderTopY }
                SourceSha256 = $sha
                SourceFileName = [string]$job.OriginalFileName
                SourceVersionName = $versionName
                SourceDataVersion = if ($null -eq $job.DataVersion) { $null } else { [int]$job.DataVersion }
                KnownChunkCount = if ($null -eq $job.ChunkCount) { $null } else { [long]$job.ChunkCount }
                Bounds = [ordered]@{
                    MinX = $minX
                    MinZ = $minZ
                    MaxXExclusive = [int64]$job.MaxXExclusive
                    MaxZExclusive = [int64]$job.MaxZExclusive
                }
                BlueMapImage = $BlueMapImage
                BlueMapVersion = $blueMapVersion
                RendererProfileVersion = $rendererProfileVersion
                RenderingProfile = [ordered]@{
                    SkyLight = $skyLightValue
                    AmbientLight = [double]$ambientLight
                    IgnoreMissingLightData = $true
                    MissingLightPolicy = 'repair-existing-chunks-then-render-sparse-edges'
                    Relight = [ordered]@{
                        PaperVersion = $relight.PaperVersion
                        PaperSha256 = $relight.PaperSha256
                        LightCleanerVersion = $relight.LightCleanerVersion
                        LightCleanerSha256 = $relight.LightCleanerSha256
                        BKCommonLibVersion = $relight.BKCommonLibVersion
                        BKCommonLibSha256 = $relight.BKCommonLibSha256
                        Seconds = $relight.Seconds
                        EdgeChunksSkipped = $relight.EdgeChunksSkipped
                        OriginalChunkCount = $relight.OriginalChunkCount
                        GeneratedChunkRecordsPruned = $relight.GeneratedChunkRecordsPruned
                        RewrittenRegionFiles = $relight.RewrittenRegionFiles
                        RemovedGeneratedFiles = $relight.RemovedGeneratedFiles
                        FootprintAuditExact = $relight.FootprintAuditExact
                        PersistentPaperRuntimeCache = $relight.PersistentPaperRuntimeCache
                        SourceMutation = 'isolated-derivative-only'
                    }
                    StockBlueMapWebapp = $true
                    RenderEdges = $true
                    EdgeLightStrength = 8
                    HiresDefaultBlocks = 500
                    HiresMaximumBlocks = 10000
                    LowresDefaultBlocks = 2000
                    LowresMaximumBlocks = 100000
                    LowresTileSize = 500
                    LodCount = 3
                    LodFactor = 5
                    ActiveOutputFilesystem = 'docker-volume'
                }
                QualityGate = [ordered]@{
                    Passed = $quality.Passed
                    SettingsSchema = $quality.SettingsSchema
                    SkyLight = $quality.SkyLight
                    AmbientLight = $quality.AmbientLight
                    StartPosition = @($quality.StartPosition)
                    LocationStartExact = $quality.LocationStartExact
                    LowresTileCount = $quality.LowresTileCount
                    ModelFileCount = $quality.ModelFileCount
                    StockClientShader = $quality.StockClientShader
                }
                CpuCount = $CpuCount
                MemoryLimit = $MemoryLimit
                RenderSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
                OutputBytes = [long]$generationMeasurement.Bytes
                OutputFiles = [long]$generationMeasurement.Files
                WebOutputBytes = [long]$webMeasurement.Bytes
                WebOutputFiles = [long]$webMeasurement.Files
                GeneratedUtc = [DateTime]::UtcNow.ToString('o')
                WebRelativePath = 'web/index.html'
                ProvenanceNotice = 'Static 3D derivative generated from the exact preserved Atlas source WDL; the original ZIP remains authoritative.'
            }
            Write-AtomicJson -Value $manifest -Path (Join-Path $stagingRoot 'manifest.json')

            if (Test-Path -LiteralPath $finalRoot) {
                $supersededRoot = "$finalRoot.superseded-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
                Assert-ChildPath -Parent $OutputRoot -Child $supersededRoot
                Move-Item -LiteralPath $finalRoot -Destination $supersededRoot
            }
            Move-Item -LiteralPath $stagingRoot -Destination $finalRoot
            if ($reservationPath) { Remove-Item -LiteralPath $reservationPath -Force; $reservationPath = $null }
            } finally { $publicationLock.Dispose() }

            $results.Add([pscustomobject]@{
                RenderId = $renderIdValue
                LocationName = [string]$job.LocationName
                State = 'complete'
                OutputRoot = $finalRoot
                OutputBytes = [long]$generationMeasurement.Bytes
                OutputFiles = [long]$generationMeasurement.Files
                RenderSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
            })

            if (-not $KeepScratch) {
                Set-WorkerStage 'cleanup'
                Assert-ChildPath -Parent $ScratchRoot -Child $scratchJob
                Remove-Item -LiteralPath $scratchJob -Recurse -Force
            }
        }
        catch {
            $results.Add([pscustomobject]@{
                RenderId = $renderIdValue
                LocationName = [string]$job.LocationName
                State = 'failed'
                Message = $_.Exception.Message
            })
            Add-Content -LiteralPath $batchLogPath -Value "$([DateTime]::UtcNow.ToString('o')) render=$renderIdValue failed=$($_.Exception.Message)"
        }
        finally {
            Exit-BlueMapStage $renderStageLease
            if ($reservationPath) {
                $gate = Enter-PublicationLock
                try { Remove-Item -LiteralPath $reservationPath -Force -ErrorAction SilentlyContinue }
                finally { $gate.Dispose() }
            }
            if (-not [string]::IsNullOrWhiteSpace($jobWebVolume)) {
                Remove-OwnedDockerResource -Name $jobWebVolume -Volume
            }
        }

        $completedCount++
        Update-RunStatus -State 'running' -Completed $completedCount -Total $jobs.Count -Current $null -Results $results
    }

    $failed = @($results | Where-Object { $_.State -eq 'failed' }).Count
    $terminalState = if ($failed -gt 0) { 'completed-with-errors' } else { 'complete' }
    Update-RunStatus -State $terminalState -Completed $jobs.Count -Total $jobs.Count -Current $null -Results $results -Message $(if ($failed -gt 0) { "$failed render(s) failed." } else { 'All requested renders reached a terminal state.' })
    $results
}
finally {
    if ($generationLock) { $generationLock.Dispose() }
    if ($lockStream) {
        $lockStream.Dispose()
    }
}
