[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [string]$GlobalQueuePath = 'C:\AtlasExample\Ingest\archive-sync\collector-queue.json',
    [string]$CatalogPath = 'C:\AtlasExample\Ingest\archive-sync\archive-warp-catalog.json',
    [ValidateRange(1, 100)]
    [int]$TargetStandardVersion = 2
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$utf8 = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')
$windows1252 = [Text.Encoding]::GetEncoding(1252)
$markers = @([char]0x00c3, [char]0x00c2, [char]0x00e2, [char]0x00f0)

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 24
}

function Get-MarkerCount([string]$Value) {
    if ([string]::IsNullOrEmpty($Value)) { return 0 }
    $count = 0
    foreach ($marker in $markers) { $count += @($Value.ToCharArray() | Where-Object { $_ -eq $marker }).Count }
    return $count
}

function Repair-Mojibake([string]$Value) {
    if ([string]::IsNullOrEmpty($Value)) { return $Value }
    $repaired = $Value
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $before = Get-MarkerCount $repaired
        if ($before -eq 0) { break }
        $candidate = [Text.Encoding]::UTF8.GetString($windows1252.GetBytes($repaired))
        if ((Get-MarkerCount $candidate) -ge $before -or $candidate.Contains([char]0xfffd)) { break }
        $repaired = $candidate
    }
    return $repaired
}

function Normalize-Warp([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ((Repair-Mojibake $Value).Trim() -replace '^/warp\s+', '' -replace '\s+', '_').ToLowerInvariant()
}

function Test-Prestandard([object]$Entry) {
    if ([string]$Entry.status -eq 'skipped-inside-background') { return $true }
    if ([string]$Entry.status -notin @('captured', 'ready')) { return $false }
    $adaptiveProperty = $Entry.PSObject.Properties['adaptive']
    if ($null -eq $adaptiveProperty -or $null -eq $adaptiveProperty.Value) { return $true }
    $adaptive = $adaptiveProperty.Value
    $versionProperty = $adaptive.PSObject.Properties['standardVersion']
    $selectionProperty = $adaptive.PSObject.Properties['componentSelection']
    return $null -eq $versionProperty -or [int]$versionProperty.Value -lt $TargetStandardVersion -or
        $null -eq $selectionProperty -or $null -eq $selectionProperty.Value
}

function Test-ForeignServer([object]$Entry) {
    if ([string]$Entry.warp -match '(?i)@(?:Constantiam|3b3t)(?:[_\s-]*server)?$') { return $true }
    $category = if ($null -ne $Entry.PSObject.Properties['archiveCategoryPath']) {
        @($Entry.archiveCategoryPath)
    } else { @() }
    return @($category | Where-Object {
        [string]$_ -match '(?i)\b(?:Constantiam|3b3t)\b'
    }).Count -gt 0
}

$queuePath = Join-Path $RunRoot 'capture-queue.json'
$statePath = Join-Path $RunRoot 'collector-state.json'
foreach ($required in @($queuePath, $statePath, $GlobalQueuePath, $CatalogPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required input was not found: $required" }
}

$queue = Read-JsonUtf8 $queuePath
$state = Read-JsonUtf8 $statePath
$global = Read-JsonUtf8 $GlobalQueuePath
$catalog = Read-JsonUtf8 $CatalogPath
$existing = @{}
foreach ($entry in @($queue.entries)) {
    $identity = Normalize-Warp ([string]$entry.warp)
    $existing[$identity] = $entry
}
$globalByIdentity = @{}
foreach ($entry in @($global.entries)) {
    $identity = Normalize-Warp ([string]$entry.warp)
    $globalByIdentity[$identity] = $entry
}
$catalogByIdentity = @{}
foreach ($entry in @($catalog.entries)) {
    $identity = Normalize-Warp ([string]$entry.warp)
    $catalogByIdentity[$identity] = $entry
}

$added = New-Object Collections.Generic.List[object]
foreach ($record in @($state.entries | Where-Object { Test-Prestandard $_ })) {
    if (Test-ForeignServer $record) { continue }
    $identity = Normalize-Warp ([string]$record.warp)
    if ([string]::IsNullOrWhiteSpace($identity) -or $existing.ContainsKey($identity)) { continue }
    if ($globalByIdentity.ContainsKey($identity)) {
        $candidate = $globalByIdentity[$identity]
    } elseif ($catalogByIdentity.ContainsKey($identity)) {
        $catalogEntry = $catalogByIdentity[$identity]
        $candidate = [pscustomobject][ordered]@{
            warp = [string]$catalogEntry.warp
            normalizedWarp = $identity
            locationName = if ($record.PSObject.Properties['locationName']) { [string]$record.locationName } else { [string]$catalogEntry.displayName }
            locationUuid = if ($record.PSObject.Properties['locationUuid']) { [string]$record.locationUuid } else { '' }
            x = if ($record.PSObject.Properties['atlasX']) { [int]$record.atlasX } else { 0 }
            y = if ($record.PSObject.Properties['atlasY']) { [int]$record.atlasY } else { 0 }
            z = if ($record.PSObject.Properties['atlasZ']) { [int]$record.atlasZ } else { 0 }
            source = 'archive-final-standard-recapture'
            archiveDisplayName = [string]$catalogEntry.displayName
            archiveCategoryPath = @($catalogEntry.categoryPath)
            archiveDiscoveredUtc = [string]$catalogEntry.discoveredUtc
            archiveLastSeenUtc = [string]$catalogEntry.lastSeenUtc
        }
    } else {
        $candidate = [pscustomobject][ordered]@{
            warp = Repair-Mojibake ([string]$record.warp)
            normalizedWarp = $identity
            locationName = if ($record.PSObject.Properties['locationName']) { Repair-Mojibake ([string]$record.locationName) } else { '' }
            locationUuid = if ($record.PSObject.Properties['locationUuid']) { [string]$record.locationUuid } else { '' }
            x = if ($record.PSObject.Properties['atlasX']) { [int]$record.atlasX } else { 0 }
            y = if ($record.PSObject.Properties['atlasY']) { [int]$record.atlasY } else { 0 }
            z = if ($record.PSObject.Properties['atlasZ']) { [int]$record.atlasZ } else { 0 }
            source = 'collector-state-final-standard-recapture'
            archiveDisplayName = if ($record.PSObject.Properties['archiveDisplayName']) { Repair-Mojibake ([string]$record.archiveDisplayName) } else { '' }
            archiveCategoryPath = if ($record.PSObject.Properties['archiveCategoryPath']) { @($record.archiveCategoryPath) } else { @() }
            archiveDiscoveredUtc = if ($record.PSObject.Properties['archiveDiscoveredUtc']) { [string]$record.archiveDiscoveredUtc } else { '' }
            archiveLastSeenUtc = if ($record.PSObject.Properties['archiveLastSeenUtc']) { [string]$record.archiveLastSeenUtc } else { '' }
        }
    }
    $existing[$identity] = $candidate
    $added.Add($candidate)
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$backupPath = "$queuePath.pre-standard-v$TargetStandardVersion-$stamp.bak"
Copy-Item -LiteralPath $queuePath -Destination $backupPath
$queue.entries = @($existing.Values | Sort-Object normalizedWarp)
if ($queue.PSObject.Properties['queueCount']) { $queue.queueCount = @($queue.entries).Count }
if (-not $queue.PSObject.Properties['targetCollectorStandardVersion']) {
    $queue | Add-Member -NotePropertyName targetCollectorStandardVersion -NotePropertyValue $TargetStandardVersion
} else { $queue.targetCollectorStandardVersion = $TargetStandardVersion }
if (-not $queue.PSObject.Properties['prestandardRequeueUtc']) {
    $queue | Add-Member -NotePropertyName prestandardRequeueUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o'))
} else { $queue.prestandardRequeueUtc = [DateTime]::UtcNow.ToString('o') }
Save-JsonAtomically $queue $queuePath

[pscustomobject][ordered]@{
    queuePath = $queuePath
    backupPath = $backupPath
    targetStandardVersion = $TargetStandardVersion
    added = $added.Count
    total = @($queue.entries).Count
    addedWarps = @($added | ForEach-Object { [string]$_.warp })
}
