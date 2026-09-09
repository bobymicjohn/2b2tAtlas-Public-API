[CmdletBinding()]
param(
    [string]$CatalogPath = 'C:\AtlasExample\Ingest\archive-sync\archive-warp-catalog.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

$utf8NoBom = New-Object Text.UTF8Encoding($false)
$windows1252 = [Text.Encoding]::GetEncoding(1252)
$mojibakeMarkers = @([char]0x00c3, [char]0x00c2, [char]0x00e2, [char]0x00f0)
$replacementCharacter = [char]0xfffd

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 12
}

function Get-MojibakeMarkerCount([string]$Value) {
    if ([string]::IsNullOrEmpty($Value)) { return 0 }
    $count = 0
    foreach ($marker in $mojibakeMarkers) {
        $count += $Value.ToCharArray().Where({ $_ -eq $marker }).Count
    }
    return $count
}

function Get-ReplacementCharacterCount([string]$Value) {
    if ([string]::IsNullOrEmpty($Value)) { return 0 }
    return $Value.ToCharArray().Where({ $_ -eq $replacementCharacter }).Count
}

function Repair-Mojibake([string]$Value) {
    if ([string]::IsNullOrEmpty($Value)) { return $Value }

    $repaired = $Value
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $beforeMarkers = Get-MojibakeMarkerCount $repaired
        if ($beforeMarkers -eq 0) { break }

        $beforeReplacements = Get-ReplacementCharacterCount $repaired
        $candidate = [Text.Encoding]::UTF8.GetString($windows1252.GetBytes($repaired))
        $afterMarkers = Get-MojibakeMarkerCount $candidate
        $afterReplacements = Get-ReplacementCharacterCount $candidate

        if ($afterMarkers -ge $beforeMarkers -or $afterReplacements -gt $beforeReplacements) { break }
        $repaired = $candidate
    }

    return $repaired
}

function ConvertTo-NormalizedWarp([string]$Warp) {
    $value = (Repair-Mojibake $Warp).Trim()
    $value = $value -replace '^\s*/warp\s+', ''
    $value = $value -replace '\s+', '_'
    return $value.ToLowerInvariant()
}

function Get-EarliestIsoValue([object[]]$Values) {
    return @($Values | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object | Select-Object -First 1)[0]
}

function Get-LatestIsoValue([object[]]$Values) {
    return @($Values | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object -Descending | Select-Object -First 1)[0]
}

if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
    throw "Archive catalog does not exist: $CatalogPath"
}

$timestamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$directory = Split-Path -Parent $CatalogPath
$baseName = [IO.Path]::GetFileNameWithoutExtension($CatalogPath)
$backupPath = Join-Path $directory ("{0}.pre-utf8-repair-{1}.json" -f $baseName, $timestamp)
$auditPath = Join-Path $directory ("{0}.utf8-repair-{1}.audit.json" -f $baseName, $timestamp)
Copy-Item -LiteralPath $CatalogPath -Destination $backupPath

$catalog = [IO.File]::ReadAllText($CatalogPath, $utf8NoBom) | ConvertFrom-Json
$originalEntries = @($catalog.entries)
$changedValues = New-Object 'System.Collections.Generic.List[object]'
$repairedEntries = New-Object 'System.Collections.Generic.List[object]'
$filteredEntries = New-Object 'System.Collections.Generic.List[object]'

foreach ($entry in $originalEntries) {
    $warpBefore = [string]$entry.warp
    $displayBefore = [string]$entry.displayName
    $categoryBefore = @($entry.categoryPath | ForEach-Object { [string]$_ })
    $warpAfter = Repair-Mojibake $warpBefore
    $displayAfter = Repair-Mojibake $displayBefore
    $categoryAfter = @($categoryBefore | ForEach-Object { Repair-Mojibake $_ })

    if ($warpAfter -ne $warpBefore -or $displayAfter -ne $displayBefore -or
        (($categoryAfter -join "`n") -ne ($categoryBefore -join "`n"))) {
        $changedValues.Add([pscustomobject]@{
            warpBefore = $warpBefore
            warpAfter = $warpAfter
            displayNameBefore = $displayBefore
            displayNameAfter = $displayAfter
            categoryPathBefore = $categoryBefore
            categoryPathAfter = $categoryAfter
        })
    }

    $sourceServer = if ($entry.PSObject.Properties.Name -contains 'sourceServer') { [string]$entry.sourceServer } else { [string]$catalog.server }
    $repairedEntries.Add([pscustomobject]@{
        warp = $warpAfter
        normalizedWarp = ConvertTo-NormalizedWarp $warpAfter
        displayName = $displayAfter
        categoryPath = $categoryAfter
        sourceServer = $sourceServer
        discoveredUtc = [string]$entry.discoveredUtc
        lastSeenUtc = [string]$entry.lastSeenUtc
    })
}

$excludedNavigationLabels = @(
    'The Archive Lobby',
    'Survival',
    'Nether Spawn',
    'Overworld Spawn',
    'The End Spawn',
    'Constantiam Server',
    '3b3t server'
)

foreach ($entry in $repairedEntries) {
    $categoryLabels = @($entry.categoryPath | ForEach-Object { [string]$_ })
    $isExcluded = $entry.warp -match '(?i)@(?:constantiam|3b3t)(?:_server)?$'
    if (-not $isExcluded) {
        foreach ($label in $categoryLabels) {
            if ($label -match '(?i)\b(?:constantiam|3b3t)\b' -or $excludedNavigationLabels -contains $label) {
                $isExcluded = $true
                break
            }
        }
    }

    if (-not $isExcluded) { $filteredEntries.Add($entry) }
}

$deduplicatedEntries = New-Object 'System.Collections.Generic.List[object]'
$duplicateGroups = New-Object 'System.Collections.Generic.List[object]'
foreach ($group in @($filteredEntries | Group-Object normalizedWarp | Sort-Object Name)) {
    $members = @($group.Group)
    $preferred = @($members | Sort-Object @{ Expression = { Get-MojibakeMarkerCount $_.warp } },
        @{ Expression = { @($_.categoryPath).Count }; Descending = $true },
        @{ Expression = { ([string]$_.displayName).Length }; Descending = $true } | Select-Object -First 1)[0]

    if ($members.Count -gt 1) {
        $duplicateGroups.Add([pscustomobject]@{
            normalizedWarp = $group.Name
            sourceCount = $members.Count
            sourceWarps = @($members | ForEach-Object { $_.warp })
        })
    }

    $deduplicatedEntries.Add([pscustomobject]@{
        warp = [string]$preferred.warp
        normalizedWarp = [string]$group.Name
        displayName = [string]$preferred.displayName
        categoryPath = @($preferred.categoryPath)
        sourceServer = [string]$preferred.sourceServer
        discoveredUtc = Get-EarliestIsoValue @($members | ForEach-Object { $_.discoveredUtc })
        lastSeenUtc = Get-LatestIsoValue @($members | ForEach-Object { $_.lastSeenUtc })
    })
}

$catalog.entries = @($deduplicatedEntries | Sort-Object normalizedWarp)
$catalog.updatedUtc = [DateTime]::UtcNow.ToString('o')
Save-JsonAtomically $catalog $CatalogPath

$audit = [pscustomobject]@{
    repairedUtc = [DateTime]::UtcNow.ToString('o')
    catalogPath = $CatalogPath
    backupPath = $backupPath
    sourceEntryCount = $originalEntries.Count
    repairedValueCount = $changedValues.Count
    filteredEntryCount = $originalEntries.Count - $filteredEntries.Count
    duplicateEntryCount = $filteredEntries.Count - $deduplicatedEntries.Count
    finalEntryCount = $deduplicatedEntries.Count
    changedValues = @($changedValues | ForEach-Object { $_ })
    duplicateGroups = @($duplicateGroups | ForEach-Object { $_ })
}
Save-JsonAtomically $audit $auditPath

Write-Output ("Archive catalog repaired: {0} -> {1} entries; {2} changed values; {3} filtered; {4} duplicates removed." -f
    $originalEntries.Count, $deduplicatedEntries.Count, $changedValues.Count,
    ($originalEntries.Count - $filteredEntries.Count), ($filteredEntries.Count - $deduplicatedEntries.Count))
Write-Output "Backup: $backupPath"
Write-Output "Audit: $auditPath"
