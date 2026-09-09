[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StatePath,
    [Parameter(Mandatory = $true)]
    [string]$CapturedRoot,
    [Parameter(Mandatory = $true)]
    [string]$ReadyRoot,
    [string]$ManifestPath = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Test-PathInside([string]$Path, [string]$Root) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $fullPath.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)
}

function Copy-VerifiedAtomically([string]$Source, [string]$Destination, [string]$ExpectedSha256) {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existing = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing -ne $ExpectedSha256) { throw "Ready artifact conflicts with a different digest: $Destination" }
        return
    }
    $temporary = "$Destination.$([guid]::NewGuid().ToString('N')).partial"
    try {
        Copy-Item -LiteralPath $Source -Destination $temporary
        $copied = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copied -ne $ExpectedSha256) { throw "Copied artifact failed SHA-256 verification: $Source" }
        Move-Item -LiteralPath $temporary -Destination $Destination
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) { throw "Collector state was not found: $StatePath" }
if (-not (Test-Path -LiteralPath $CapturedRoot -PathType Container)) { throw "Captured root was not found: $CapturedRoot" }
if (-not (Test-Path -LiteralPath $ReadyRoot -PathType Container) -and -not $DryRun) {
    New-Item -ItemType Directory -Path $ReadyRoot -Force | Out-Null
}

$capturedRootFull = [IO.Path]::GetFullPath($CapturedRoot)
$state = Read-JsonUtf8 $StatePath
$promoted = New-Object System.Collections.Generic.List[object]
$quarantined = New-Object System.Collections.Generic.List[object]

foreach ($entry in @($state.entries | Where-Object { [string]$_.status -eq 'captured' } | Sort-Object normalizedWarp)) {
    $adaptiveProperty = $entry.PSObject.Properties['adaptive']
    $adaptive = if ($null -eq $adaptiveProperty) { $null } else { $adaptiveProperty.Value }
    # A run state can be seeded with prior non-adaptive or separately quarantined captures.
    # Only artifacts physically rooted in this named batch are candidates for its promotion.
    if ($null -eq $adaptive -or $null -eq $adaptive.PSObject.Properties['footprintArtifact']) { continue }
    $source = [IO.Path]::GetFullPath([string]$adaptive.footprintArtifact.path)
    if (-not (Test-PathInside $source $capturedRootFull)) { continue }

    $reasons = New-Object System.Collections.Generic.List[string]
    if ($null -ne $adaptive) {
        if ($null -eq $adaptive.PSObject.Properties['standardVersion'] -or
            [int]$adaptive.standardVersion -lt 2) { $reasons.Add('collector standard is older than v2') }
        if ($null -eq $adaptive.PSObject.Properties['componentSelection']) {
            $reasons.Add('component-selection evidence is missing')
        }
        if ([string]$adaptive.confidence -ne 'high') { $reasons.Add("confidence=$($adaptive.confidence)") }
        if ([int]$adaptive.missingChunks -ne 0) { $reasons.Add("missingChunks=$($adaptive.missingChunks)") }
        if ([int]$adaptive.surveyMissingChunks -ne 0) { $reasons.Add("surveyMissingChunks=$($adaptive.surveyMissingChunks)") }
        if ([bool]$adaptive.maxRadiusReached) { $reasons.Add('coordinate/radius limit reached') }
        if (@($adaptive.otherKnownWarpsInsideBounds).Count -ne 0) { $reasons.Add('another known warp is inside bounds') }
        if (-not [bool]$adaptive.footprintArtifact.exactBoundsOnly) { $reasons.Add('footprint is not exact-bounds-only') }
        if ([int]$adaptive.footprintArtifact.headerChunks -le 0) { $reasons.Add('footprint has no saved chunks') }
    }

    if ($reasons.Count -gt 0) {
        $quarantined.Add([ordered]@{ warp = [string]$entry.warp; reasons = $reasons.ToArray() })
        continue
    }

    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Footprint artifact is missing: $source" }
    $metadataSource = "$source.metadata.json"
    if (-not (Test-Path -LiteralPath $metadataSource -PathType Leaf)) { throw "Footprint metadata is missing: $metadataSource" }

    $expected = ([string]$adaptive.footprintArtifact.sha256).ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "Footprint SHA-256 mismatch: $source" }
    $metadata = Read-JsonUtf8 $metadataSource
    if (([string]$metadata.sha256).ToLowerInvariant() -ne $expected) { throw "Metadata SHA-256 mismatch: $metadataSource" }

    $destination = Join-Path $ReadyRoot ([IO.Path]::GetFileName($source))
    $metadataDestination = "$destination.metadata.json"
    if (-not $DryRun) {
        Copy-VerifiedAtomically $source $destination $expected
        $metadataExpected = (Get-FileHash -LiteralPath $metadataSource -Algorithm SHA256).Hash.ToLowerInvariant()
        Copy-VerifiedAtomically $metadataSource $metadataDestination $metadataExpected
    }
    $promoted.Add([ordered]@{
        warp = [string]$entry.warp
        sha256 = $expected
        chunks = [int]$adaptive.footprintArtifact.headerChunks
        dimension = [string]$adaptive.dimension
        destination = $destination
    })
}

$manifest = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    dryRun = [bool]$DryRun
    statePath = [IO.Path]::GetFullPath($StatePath)
    capturedRoot = $capturedRootFull
    readyRoot = [IO.Path]::GetFullPath($ReadyRoot)
    promotedCount = $promoted.Count
    quarantinedCount = $quarantined.Count
    promoted = $promoted.ToArray()
    quarantined = $quarantined.ToArray()
}
if (-not [string]::IsNullOrWhiteSpace($ManifestPath)) {
    $manifestParent = Split-Path -Parent $ManifestPath
    if (-not [string]::IsNullOrWhiteSpace($manifestParent) -and -not (Test-Path -LiteralPath $manifestParent)) {
        New-Item -ItemType Directory -Path $manifestParent -Force | Out-Null
    }
    $temporary = "$ManifestPath.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, ($manifest | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $ManifestPath -Force
}
$manifest
