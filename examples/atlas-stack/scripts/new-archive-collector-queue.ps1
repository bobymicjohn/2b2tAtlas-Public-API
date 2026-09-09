[CmdletBinding()]
param(
    [string]$ApiBase = 'http://127.0.0.1:5297',
    [string]$OutputPath = 'C:\AtlasExample\Ingest\archive-sync\collector-queue.json',
    [string]$CollectorStatePath = 'C:\AtlasExample\Ingest\archive-sync\collector-state.json',
    [string]$ArchiveCatalogPath = 'C:\AtlasExample\Ingest\archive-sync\archive-warp-catalog.json',
    [ValidateRange(0, 2000000000)]
    [int]$MinimumKnownRadiusBlocks = 0,
    [switch]$ArchiveCatalogOnly,
    [switch]$IncludeCompleted
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Get-NormalizedWarp([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value.Trim() -replace '^/warp\s+', '' -replace '\s+', '_').ToLowerInvariant()
}

function Test-ForeignServer([string]$Warp, [object[]]$CategoryPath = @()) {
    if ($Warp -match '(?i)@(?:Constantiam|3b3t)(?:[_\s-]*server)?$') { return $true }
    return @($CategoryPath | Where-Object {
        [string]$_ -match '(?i)\b(?:Constantiam|3b3t)\b'
    }).Count -gt 0
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 12
}

$apiBase = $ApiBase.TrimEnd('/')
$locationResponse = Invoke-RestMethod -Method Get -Uri "$apiBase/api/locations" -UseBasicParsing
# Windows PowerShell 5.1 preserves a top-level JSON array as one pipeline object.
# GetEnumerator keeps each location distinct on both 5.1 and newer PowerShell.
$locations = @($locationResponse.GetEnumerator())
$completed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

if (Test-Path -LiteralPath $CollectorStatePath -PathType Leaf) {
    $savedState = Read-JsonUtf8 $CollectorStatePath
    foreach ($entry in @($savedState.entries)) {
        if ($null -eq $entry) { continue }
        $status = [string]$entry.status
        if ($status -in @('accepted', 'complete', 'captured', 'ready', 'imported') -or
            ($status -eq 'skipped-inside-background' -and $MinimumKnownRadiusBlocks -gt 0)) {
            $identity = Get-NormalizedWarp ([string]$entry.warp)
            if (-not [string]::IsNullOrWhiteSpace($identity)) { $completed.Add($identity) | Out-Null }
        }
    }
}

$byWarp = @{}
$conflicts = New-Object System.Collections.Generic.List[object]
foreach ($location in $locations) {
    foreach ($warp in @($location.warps)) {
        if ($null -eq $warp) { continue }
        if (Test-ForeignServer ([string]$warp.name)) { continue }
        $identity = Get-NormalizedWarp ([string]$warp.name)
        if ([string]::IsNullOrWhiteSpace($identity)) { continue }

        $candidate = [ordered]@{
            warp = [string]$warp.name
            normalizedWarp = $identity
            locationUuid = [string]$location.locationUuid
            locationName = [string]$location.name
            dimension = [string]$location.dimensionName
            x = [int]($location.x)
            y = [int]($location.y)
            z = [int]($location.z)
            atlasWarpId = [int]($warp.id)
            atlasWarpUuid = [string]$warp.warpUuid
            source = 'atlas'
            archiveDisplayName = ''
            archiveCategoryPath = @()
            archiveDiscoveredUtc = ''
            archiveLastSeenUtc = ''
        }

        if ($byWarp.ContainsKey($identity)) {
            $prior = $byWarp[$identity]
            if ($prior.locationUuid -eq $candidate.locationUuid) {
                # Historical Atlas imports can contain duplicate warp rows on the
                # same location. They represent one Archive capture identity.
                continue
            }
            $conflicts.Add([ordered]@{
                normalizedWarp = $identity
                firstLocationUuid = $prior.locationUuid
                firstLocationName = $prior.locationName
                secondLocationUuid = $candidate.locationUuid
                secondLocationName = $candidate.locationName
            })
            continue
        }
        $byWarp[$identity] = $candidate
    }
}

$archiveCatalogCount = 0
$archiveOnlyCount = 0
if (Test-Path -LiteralPath $ArchiveCatalogPath -PathType Leaf) {
    $archiveCatalog = Read-JsonUtf8 $ArchiveCatalogPath
    foreach ($catalogEntry in @($archiveCatalog.entries)) {
        if ($null -eq $catalogEntry) { continue }
        if (Test-ForeignServer ([string]$catalogEntry.warp) @($catalogEntry.categoryPath)) { continue }
        $identity = Get-NormalizedWarp ([string]$catalogEntry.warp)
        if ([string]::IsNullOrWhiteSpace($identity)) { continue }
        $archiveCatalogCount++
        if ($byWarp.ContainsKey($identity)) {
            $existing = $byWarp[$identity]
            $existing['source'] = 'atlas+archive-catalog'
            $existing['archiveDisplayName'] = [string]$catalogEntry.displayName
            $existing['archiveCategoryPath'] = @($catalogEntry.categoryPath)
            $existing['archiveDiscoveredUtc'] = [string]$catalogEntry.discoveredUtc
            $existing['archiveLastSeenUtc'] = [string]$catalogEntry.lastSeenUtc
            continue
        }
        $archiveOnlyCount++
        $byWarp[$identity] = [ordered]@{
            warp = [string]$catalogEntry.warp
            normalizedWarp = $identity
            locationUuid = ''
            locationName = [string]$catalogEntry.displayName
            dimension = ''
            x = 0
            y = 0
            z = 0
            atlasWarpId = 0
            atlasWarpUuid = ''
            source = 'archive-catalog'
            archiveDisplayName = [string]$catalogEntry.displayName
            archiveCategoryPath = @($catalogEntry.categoryPath)
            archiveDiscoveredUtc = [string]$catalogEntry.discoveredUtc
            archiveLastSeenUtc = [string]$catalogEntry.lastSeenUtc
        }
    }
}

$catalogEligible = @($byWarp.Values | Where-Object {
    -not $ArchiveCatalogOnly -or $_.source -ne 'atlas'
})
$eligibleRadius = @($catalogEligible | Where-Object {
    if ($MinimumKnownRadiusBlocks -eq 0) { return $true }
    $x = [double]$_.x
    $z = [double]$_.z
    return [Math]::Sqrt($x * $x + $z * $z) -ge $MinimumKnownRadiusBlocks
})
$excludedCatalogSourceCount = $byWarp.Count - $catalogEligible.Count
$excludedRadiusCount = $catalogEligible.Count - $eligibleRadius.Count
$entries = @($eligibleRadius |
    Where-Object { $IncludeCompleted -or -not $completed.Contains($_.normalizedWarp) } |
    Sort-Object normalizedWarp)
$excludedCompletedCount = if ($IncludeCompleted) { 0 } else {
    @($eligibleRadius | Where-Object { $completed.Contains($_.normalizedWarp) }).Count
}
$conflictEntries = $conflicts.ToArray()

$manifest = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    sourceApi = $apiBase
    semantics = 'One Archive warp identifies one WDL; one Atlas location may own multiple warps/WDLs.'
    totalAtlasLocations = $locations.Count
    archiveCatalogWarps = $archiveCatalogCount
    archiveOnlyWarps = $archiveOnlyCount
    totalUniqueWarps = $byWarp.Count
    archiveCatalogOnly = [bool]$ArchiveCatalogOnly
    excludedAtlasOnlyWarps = $excludedCatalogSourceCount
    minimumKnownRadiusBlocks = $MinimumKnownRadiusBlocks
    excludedInsideOrUnknownRadius = $excludedRadiusCount
    excludedCompleted = $excludedCompletedCount
    queueCount = $entries.Count
    conflicts = $conflictEntries
    entries = $entries
}

Save-JsonAtomically $manifest $OutputPath
Write-Output "Wrote $($entries.Count) Archive warp(s) to $OutputPath."
if ($conflicts.Count -gt 0) {
    Write-Warning "$($conflicts.Count) normalized warp conflict(s) require review before automated capture."
}
