param(
    [string]$StatePath = 'C:\AtlasExample\Ingest\bluemap\location-render-status.json',
    [string]$OutputRoot = 'F:\AtlasExample\AtlasBlueMap\location-renders',
    [string]$DatabasePath = 'C:\AtlasExample\Api\data\atlas.db'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
    throw "Atlas BlueMap status was not found: $StatePath"
}

$status = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
$manifests = @()
$validated = @()
if (Test-Path -LiteralPath $OutputRoot -PathType Container) {
    # Generation manifests are direct children of OutputRoot. Do not recurse
    # through millions of BlueMap model files just to compute status.
    foreach ($generation in Get-ChildItem -LiteralPath $OutputRoot -Directory -ErrorAction SilentlyContinue) {
        $file = Get-Item -LiteralPath (Join-Path $generation.FullName 'manifest.json') -ErrorAction SilentlyContinue
        if ($null -eq $file) { continue }
        try {
            $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($manifest.Status -eq 'complete') {
                $manifests += $manifest
                $generationName = Split-Path -Leaf (Split-Path -Parent $file.FullName)
                $profileVersion = if ($null -eq $manifest.PSObject.Properties['RendererProfileVersion']) { 1 } else { [int]$manifest.RendererProfileVersion }
                $qualityPassed = $null -ne $manifest.PSObject.Properties['QualityGate'] -and
                    $null -ne $manifest.QualityGate.PSObject.Properties['Passed'] -and
                    [bool]$manifest.QualityGate.Passed
                $footprintExact = $null -ne $manifest.PSObject.Properties['RenderingProfile'] -and
                    $null -ne $manifest.RenderingProfile.PSObject.Properties['Relight'] -and
                    $null -ne $manifest.RenderingProfile.Relight.PSObject.Properties['FootprintAuditExact'] -and
                    [bool]$manifest.RenderingProfile.Relight.FootprintAuditExact
                $locationStartExact = $null -ne $manifest.PSObject.Properties['QualityGate'] -and
                    $null -ne $manifest.QualityGate.PSObject.Properties['LocationStartExact'] -and
                    [bool]$manifest.QualityGate.LocationStartExact
                if ($generationName -notlike '*.superseded-*' -and $profileVersion -ge 7 -and
                    $qualityPassed -and $footprintExact -and $locationStartExact) {
                    $validated += [pscustomobject]@{
                        RenderId = [int]$manifest.RenderId
                        ProfileVersion = $profileVersion
                        GeneratedUtc = [DateTime]$manifest.GeneratedUtc
                    }
                }
            }
        }
        catch {
            Write-Warning "Unreadable BlueMap manifest $($file.FullName): $($_.Exception.Message)"
        }
    }
}

$outputBytes = [long](($manifests | Measure-Object -Property OutputBytes -Sum).Sum)
$driveName = [IO.Path]::GetPathRoot($OutputRoot).Substring(0, 1)
$drive = Get-PSDrive -Name $driveName
$current = $status.Current
$validatedRenderCount = @($validated | Group-Object RenderId).Count
$sourceBackedRenderCount = $null
if ((Test-Path -LiteralPath $DatabasePath -PathType Leaf) -and (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    # Match the renderer's selectable catalog exactly. A completed historical
    # job whose render/location row was later removed is provenance, but it is
    # not a renderable public entity and must not inflate the remaining count.
    $countText = (& sqlite3 $DatabasePath "SELECT COUNT(DISTINCT j.RenderId) FROM IngestionJobs j JOIN Renders r ON r.Id=j.RenderId JOIN Locations l ON l.Rowid=r.LocationRowid WHERE lower(j.Status)='completed' AND j.ArchiveSha256 IS NOT NULL;" | Out-String).Trim()
    if ($LASTEXITCODE -eq 0 -and $countText -match '^\d+$') {
        $sourceBackedRenderCount = [int]$countText
    }
}

[pscustomobject]@{
    State = [string]$status.State
    Coordinated = $null -ne $status.PSObject.Properties['Coordinated'] -and [bool]$status.Coordinated
    Workers = if ($null -ne $status.PSObject.Properties['Workers']) { @($status.Workers) } else { @() }
    Progress = "$($status.Completed)/$($status.Total)"
    Percent = [double]$status.Percent
    CurrentRenderId = if ($null -eq $current) { $null } else { $current.RenderId }
    CurrentLocation = if ($null -eq $current) { $null } else { $current.LocationName }
    UpdatedUtc = [datetime]$status.UpdatedUtc
    ValidatedRenderCount = $validatedRenderCount
    SourceBackedRenderCount = $sourceBackedRenderCount
    RemainingRenderCount = if ($null -eq $sourceBackedRenderCount) { $null } else { [Math]::Max(0, $sourceBackedRenderCount - $validatedRenderCount) }
    DiagnosticGenerationCount = $manifests.Count
    OutputGiB = [math]::Round($outputBytes / 1GB, 3)
    QuotaGiB = [math]::Round([long]$status.OutputQuotaBytes / 1GB, 1)
    OutputDriveFreeGiB = [math]::Round([long]$drive.Free / 1GB, 1)
    Message = [string]$status.Message
    OutputRoot = $OutputRoot
}

