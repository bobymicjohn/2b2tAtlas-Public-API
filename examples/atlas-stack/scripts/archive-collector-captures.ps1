[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StatePath,
    [string]$StagedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [string]$ArchiveRoot = 'E:\AtlasExample\WorldDownloads\collector\example-catalog\captured',
    [ValidateRange(1, 100)]
    [int]$MinimumStandardVersion = 2,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$utf8 = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 24
}

function Test-PathInside([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Copy-VerifiedAtomically([string]$Source, [string]$Destination, [string]$ExpectedSha256 = '') {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Staged artifact is missing: $Source" }
    $actual = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and $actual -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "Staged artifact failed SHA-256 verification: $Source"
    }
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existing = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing -ne $actual) { throw "Archive destination collision: $Destination" }
        return $actual
    }
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = "$Destination.$([guid]::NewGuid().ToString('N')).partial"
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        $copied = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copied -ne $actual) { throw "Archive copy failed SHA-256 verification: $Source" }
        Move-Item -LiteralPath $temporary -Destination $Destination
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
    return $actual
}

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Collector state was not found: $StatePath" }
$staged = [IO.Path]::GetFullPath($StagedRoot)
$archive = [IO.Path]::GetFullPath($ArchiveRoot)
if (-not (Test-Path -LiteralPath $staged -PathType Container)) { throw "Staged capture root was not found: $staged" }
if (-not $DryRun -and -not (Test-Path -LiteralPath $archive -PathType Container)) {
    New-Item -ItemType Directory -Path $archive -Force | Out-Null
}

$state = Read-JsonUtf8 $StatePath
$archived = New-Object Collections.Generic.List[object]
$skipped = New-Object Collections.Generic.List[object]
foreach ($entry in @($state.entries | Sort-Object normalizedWarp)) {
    if ([string]$entry.status -notin @('captured', 'ready')) { continue }
    $adaptiveProperty = $entry.PSObject.Properties['adaptive']
    if ($null -eq $adaptiveProperty -or $null -eq $adaptiveProperty.Value) { continue }
    $adaptive = $adaptiveProperty.Value
    if ($null -eq $adaptive.PSObject.Properties['standardVersion'] -or
        [int]$adaptive.standardVersion -lt $MinimumStandardVersion -or
        $null -eq $adaptive.PSObject.Properties['componentSelection']) {
        $skipped.Add([ordered]@{ warp = [string]$entry.warp; reason = 'prestandard' })
        continue
    }
    $rawSource = [IO.Path]::GetFullPath([string]$entry.path)
    if (-not (Test-PathInside $rawSource $staged)) {
        if (Test-PathInside $rawSource $archive) {
            $skipped.Add([ordered]@{ warp = [string]$entry.warp; reason = 'already-archived' })
            continue
        }
        throw "Capture path is outside both approved roots: $rawSource"
    }
    $manifestSource = [IO.Path]::GetFullPath([string]$adaptive.nonVoidManifest.path)
    $footprintSource = [IO.Path]::GetFullPath([string]$adaptive.footprintArtifact.path)
    foreach ($source in @($manifestSource, $footprintSource)) {
        if (-not (Test-PathInside $source $staged)) { throw "Related artifact escaped staged root: $source" }
    }
    $rawDestination = Join-Path $archive ([IO.Path]::GetFileName($rawSource))
    $manifestDestination = Join-Path $archive ([IO.Path]::GetFileName($manifestSource))
    $footprintDestination = Join-Path $archive ([IO.Path]::GetFileName($footprintSource))
    if (-not $DryRun) {
        [void](Copy-VerifiedAtomically $rawSource $rawDestination ([string]$entry.sha256))
        [void](Copy-VerifiedAtomically $manifestSource $manifestDestination ([string]$adaptive.nonVoidManifest.sha256))
        [void](Copy-VerifiedAtomically $footprintSource $footprintDestination ([string]$adaptive.footprintArtifact.sha256))
        foreach ($metadataSource in @("$rawSource.metadata.json", "$footprintSource.metadata.json")) {
            if (Test-Path -LiteralPath $metadataSource -PathType Leaf) {
                [void](Copy-VerifiedAtomically $metadataSource (Join-Path $archive ([IO.Path]::GetFileName($metadataSource))))
            }
        }
        $entry.path = $rawDestination
        $adaptive.nonVoidManifest.path = $manifestDestination
        $adaptive.footprintArtifact.path = $footprintDestination
        $adaptive.footprintArtifact.sourceCapturePath = $rawDestination
        $storage = [pscustomobject][ordered]@{
            tier = 'local-archive'
            stagedRoot = $staged
            archiveRoot = $archive
            archivedUtc = [DateTime]::UtcNow.ToString('o')
            hashesVerified = $true
        }
        if ($entry.PSObject.Properties['storage']) { $entry.storage = $storage }
        else { $entry | Add-Member -NotePropertyName storage -NotePropertyValue $storage }
    }
    $archived.Add([ordered]@{
        warp = [string]$entry.warp
        raw = $rawDestination
        footprint = $footprintDestination
        bytes = [long]$entry.archiveBytes + [long]$adaptive.footprintArtifact.bytes
    })
}

$backupPath = ''
if (-not $DryRun -and $archived.Count -gt 0) {
    $backupPath = "$StatePath.pre-archive-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')).bak"
    Copy-Item -LiteralPath $StatePath -Destination $backupPath
    $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Save-JsonAtomically $state $StatePath
}

[pscustomobject][ordered]@{
    dryRun = [bool]$DryRun
    stagedRoot = $staged
    archiveRoot = $archive
    archivedCount = $archived.Count
    skippedCount = $skipped.Count
    backupPath = $backupPath
    archived = $archived.ToArray()
    skipped = $skipped.ToArray()
}
