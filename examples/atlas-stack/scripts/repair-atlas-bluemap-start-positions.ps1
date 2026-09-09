param(
    [string]$DatabasePath = 'C:\AtlasExample\Api\data\atlas.db',
    [string]$OutputRoot = 'F:\AtlasExample\AtlasBlueMap\location-renders',
    [int]$MinimumProfileVersion = 7
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $DatabasePath -PathType Leaf)) {
    throw "Atlas database was not found: $DatabasePath"
}
if (-not (Test-Path -LiteralPath $OutputRoot -PathType Container)) {
    throw "BlueMap output root was not found: $OutputRoot"
}
if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    throw 'sqlite3 is required to repair BlueMap start positions.'
}

function Write-AtomicJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Compress
    )

    $json = if ($Compress) {
        $Value | ConvertTo-Json -Depth 30 -Compress
    } else {
        $Value | ConvertTo-Json -Depth 30
    }
    $temporary = "$Path.tmp-$PID"
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

$rowsJson = & sqlite3 -json $DatabasePath @"
SELECT r.Id AS RenderId,
       l.Rowid AS LocationId,
       l.X AS LocationX,
       l.Y AS LocationY,
       l.Z AS LocationZ
FROM Renders r
JOIN Locations l ON l.Rowid = r.LocationRowid;
"@
if ($LASTEXITCODE -ne 0) { throw 'Could not query Atlas render locations.' }
$parsedRows = (($rowsJson -join "`n") | ConvertFrom-Json)
$rows = if ($parsedRows -is [Array]) {
    @($parsedRows)
} elseif ($null -ne $parsedRows) {
    @($parsedRows)
} else {
    @()
}
$byRender = @{}
foreach ($row in $rows) { $byRender[[int]$row.RenderId] = $row }

$examined = 0
$repaired = 0
$alreadyCorrect = 0
$skipped = 0
foreach ($generation in @(Get-ChildItem -LiteralPath $OutputRoot -Directory)) {
    if ($generation.Name -like '*.superseded-*') { continue }
    $manifestPath = Join-Path $generation.FullName 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { continue }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifestProperties = @($manifest.PSObject.Properties.Name)
    if ('Status' -notin $manifestProperties -or
        'RendererProfileVersion' -notin $manifestProperties -or
        'RenderId' -notin $manifestProperties -or
        'QualityGate' -notin $manifestProperties) { continue }
    if ([string]$manifest.Status -ne 'complete' -or
        [int]$manifest.RendererProfileVersion -lt $MinimumProfileVersion) { continue }
    $examined++

    $renderId = [int]$manifest.RenderId
    if (-not $byRender.ContainsKey($renderId)) {
        Write-Warning "No Atlas location row exists for BlueMap render $renderId."
        $skipped++
        continue
    }
    $row = $byRender[$renderId]
    $x = [int64]$row.LocationX
    $z = [int64]$row.LocationZ
    $settingsPath = Join-Path $generation.FullName 'web\maps\atlas\settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath -PathType Leaf)) {
        Write-Warning "BlueMap settings are missing for render $renderId."
        $skipped++
        continue
    }

    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $settingsCorrect = $null -ne $settings.startPos -and
        @($settings.startPos).Count -eq 2 -and
        [int64]$settings.startPos[0] -eq $x -and
        [int64]$settings.startPos[1] -eq $z
    $manifestCorrect = $null -ne $manifest.PSObject.Properties['QualityGate'] -and
        $null -ne $manifest.QualityGate.PSObject.Properties['LocationStartExact'] -and
        [bool]$manifest.QualityGate.LocationStartExact
    if ($settingsCorrect -and $manifestCorrect) {
        $alreadyCorrect++
        continue
    }

    $settings.startPos = @($x, $z)
    $coordinates = [pscustomobject]@{
        X = $x
        Y = if ($null -eq $row.LocationY) { $null } else { [int64]$row.LocationY }
        Z = $z
    }
    if ($null -eq $manifest.PSObject.Properties['LocationCoordinates']) {
        $manifest | Add-Member -NotePropertyName LocationCoordinates -NotePropertyValue $coordinates
    } else {
        $manifest.LocationCoordinates = $coordinates
    }
    if ($null -eq $manifest.QualityGate.PSObject.Properties['StartPosition']) {
        $manifest.QualityGate | Add-Member -NotePropertyName StartPosition -NotePropertyValue @($x, $z)
    } else {
        $manifest.QualityGate.StartPosition = @($x, $z)
    }
    if ($null -eq $manifest.QualityGate.PSObject.Properties['LocationStartExact']) {
        $manifest.QualityGate | Add-Member -NotePropertyName LocationStartExact -NotePropertyValue $true
    } else {
        $manifest.QualityGate.LocationStartExact = $true
    }
    if ($null -eq $manifest.PSObject.Properties['PresentationPatchedUtc']) {
        $manifest | Add-Member -NotePropertyName PresentationPatchedUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('o'))
    } else {
        $manifest.PresentationPatchedUtc = [DateTime]::UtcNow.ToString('o')
    }

    Write-AtomicJson -Value $settings -Path $settingsPath -Compress
    Write-AtomicJson -Value $manifest -Path $manifestPath
    $repaired++
}

[pscustomobject]@{
    Examined = $examined
    Repaired = $repaired
    AlreadyCorrect = $alreadyCorrect
    Skipped = $skipped
    OutputRoot = $OutputRoot
}
