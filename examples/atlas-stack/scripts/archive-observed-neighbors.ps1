# Archive coordinates belong to a particular live world, not merely a dimension.
# Catalog/Atlas coordinates can be unknown or describe a different historical site.
function Get-ObservedArchiveNeighbors {
    param([object[]]$Entries, [string]$Server, [string]$LiveDimensionId,
          [string]$Warp, [int[]]$Bounds)
    if ($Bounds.Count -ne 4 -or $Bounds[0] -ge $Bounds[2] -or $Bounds[1] -ge $Bounds[3]) {
        throw 'Invalid neighbor-check bounds.'
    }
    if ([string]::IsNullOrWhiteSpace($LiveDimensionId)) {
        throw 'Cannot verify footprint neighbors without the live Archive world identity.'
    }
    $seen = @{}
    foreach ($entry in $Entries) {
        if ($null -eq $entry -or $null -eq $entry.PSObject.Properties['adaptive'] -or
            $null -eq $entry.PSObject.Properties['sourceServer'] -or $entry.sourceServer -ne $Server) { continue }
        $a = $entry.adaptive
        if ($null -eq $a -or $null -eq $a.PSObject.Properties['liveDimensionId'] -or
            $a.liveDimensionId -cne $LiveDimensionId -or
            $null -eq $a.PSObject.Properties['archiveWarpPosition']) { continue }
        $p = $a.archiveWarpPosition
        if ($null -eq $p -or $null -eq $p.PSObject.Properties['x'] -or
            $null -eq $p.PSObject.Properties['z'] -or $null -eq $p.x -or $null -eq $p.z) { continue }
        if ($null -eq $entry.PSObject.Properties['warp']) { continue }
        $name = [string]$entry.warp
        if ([string]::IsNullOrWhiteSpace($name) -or
            (Get-DateInsensitiveWarpIdentity $name) -eq (Get-DateInsensitiveWarpIdentity $Warp)) { continue }
        $x = [double]$p.x; $z = [double]$p.z
        if ([double]::IsNaN($x) -or [double]::IsNaN($z) -or
            $x -lt $Bounds[0] -or $x -ge $Bounds[2] -or $z -lt $Bounds[1] -or $z -ge $Bounds[3]) { continue }
        $key = Get-NormalizedWarp $name
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        [pscustomobject]@{ warp=$name; normalizedWarp=$key; x=$x; z=$z;
            liveDimensionId=$LiveDimensionId; source='observed-archive-landing' }
    }
}

function Read-ArchiveNeighborStates {
    param([string]$StatePath, [string]$KnownWarpStatePath, [string]$PeerStateRoot)
    $paths = @($StatePath)
    if ($KnownWarpStatePath) { $paths += $KnownWarpStatePath }
    if ($PeerStateRoot) {
        # Only this explicitly configured run's immediate worker directories.
        $paths += @(Get-ChildItem -LiteralPath $PeerStateRoot -Directory -ErrorAction Stop |
            Where-Object { $_.Name -like 'worker-*' } |
            ForEach-Object { Join-Path $_.FullName 'state.json' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    }
    foreach ($path in @($paths | Select-Object -Unique)) {
        # Fail closed on an unreadable configured state: absence of evidence is
        # not a high-confidence footprint. Readers tolerate atomic replacements.
        $snapshot = Read-AtlasJsonWithRetry -Path $path
        if ($null -eq $snapshot.PSObject.Properties['entries']) { throw 'Invalid neighbor state: entries missing.' }
        $snapshot.entries
        $journalPath = Join-Path (Split-Path -Parent $path) 'active-capture.json'
        $journal = Read-AtlasJsonWithRetry -Path $journalPath -AllowMissing
        if ($null -ne $journal) {
            # Include peers still capturing, before their final state is merged.
            [pscustomobject]@{ warp=$journal.warp; sourceServer=$journal.server;
                adaptive=[pscustomobject]@{liveDimensionId=$journal.dimension;
                    archiveWarpPosition=[pscustomobject]@{x=$journal.x;z=$journal.z}} }
        }
    }
}
