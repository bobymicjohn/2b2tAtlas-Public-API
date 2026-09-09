param(
    [string]$OutputRoot = 'F:\AtlasExample\AtlasBlueMap\location-renders',
    [int]$Port = 8770,
    [string]$StateRoot = 'C:\AtlasExample\Ingest\bluemap',
    [int]$MinimumProfileVersion = 7
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $OutputRoot -PathType Container)) {
    throw "Atlas BlueMap output was not found: $OutputRoot"
}

$python = Get-Command python.exe -ErrorAction Stop
$previewRoot = Join-Path $StateRoot "preview-public-p$MinimumProfileVersion"
New-Item -ItemType Directory -Path $previewRoot -Force | Out-Null
$entries = @()
foreach ($generation in Get-ChildItem -LiteralPath $OutputRoot -Directory -ErrorAction SilentlyContinue) {
    $file = Get-Item -LiteralPath (Join-Path $generation.FullName 'manifest.json') -ErrorAction SilentlyContinue
    if ($null -eq $file) { continue }
    try {
        $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($manifest.Status -ne 'complete') {
            continue
        }

        $generationRoot = Split-Path -Parent $file.FullName
        $generationName = Split-Path -Leaf $generationRoot
        if ($generationName -like '*.superseded-*') {
            continue
        }
        $webIndex = Join-Path $generationRoot 'web\index.html'
        if (-not (Test-Path -LiteralPath $webIndex -PathType Leaf)) {
            continue
        }

        $profileVersion = if ($null -eq $manifest.PSObject.Properties['RendererProfileVersion']) { 1 } else { [int]$manifest.RendererProfileVersion }
        if ($profileVersion -lt $MinimumProfileVersion) {
            continue
        }
        if ($profileVersion -ge 7) {
            $hasQualityGate = $null -ne $manifest.PSObject.Properties['QualityGate'] -and
                $null -ne $manifest.QualityGate.PSObject.Properties['Passed'] -and
                [bool]$manifest.QualityGate.Passed
            $hasExactFootprint = $null -ne $manifest.PSObject.Properties['RenderingProfile'] -and
                $null -ne $manifest.RenderingProfile.PSObject.Properties['Relight'] -and
                $null -ne $manifest.RenderingProfile.Relight.PSObject.Properties['FootprintAuditExact'] -and
                [bool]$manifest.RenderingProfile.Relight.FootprintAuditExact
            $hasLocationStart = $null -ne $manifest.PSObject.Properties['QualityGate'] -and
                $null -ne $manifest.QualityGate.PSObject.Properties['LocationStartExact'] -and
                [bool]$manifest.QualityGate.LocationStartExact
            if (-not $hasQualityGate -or -not $hasExactFootprint -or -not $hasLocationStart) {
                continue
            }
        }

        $entries += [pscustomobject]@{
            Manifest = $manifest
            GenerationName = $generationName
            ProfileVersion = $profileVersion
            GeneratedUtc = if ($null -eq $manifest.PSObject.Properties['GeneratedUtc']) { [DateTime]::MinValue } else { [DateTime]$manifest.GeneratedUtc }
        }
    }
    catch {
        Write-Warning "Ignoring unreadable BlueMap manifest $($file.FullName): $($_.Exception.Message)"
    }
}

if ($entries.Count -eq 0) {
    throw 'No completed Atlas BlueMap generations are available to preview.'
}

# A renderer-profile correction intentionally creates a new immutable generation
# beside the old one. Only advertise the highest/newest complete generation for
# each source render so stale lighting/mesh profiles cannot be opened by mistake.
$entries = @($entries |
    Group-Object { [int]$_.Manifest.RenderId } |
    ForEach-Object { $_.Group | Sort-Object ProfileVersion, GeneratedUtc -Descending | Select-Object -First 1 })

$cards = foreach ($entry in $entries | Sort-Object { [int]$_.Manifest.RenderId }) {
    $manifest = $entry.Manifest
    $name = [Net.WebUtility]::HtmlEncode([string]$manifest.LocationName)
    $dimension = [Net.WebUtility]::HtmlEncode([string]$manifest.Dimension)
    $date = [Net.WebUtility]::HtmlEncode([string]$manifest.WorldDownloadDate)
    $generation = [Uri]::EscapeDataString([string]$entry.GenerationName)
    $chunks = if ($null -eq $manifest.KnownChunkCount) { 'unknown' } else { '{0:N0}' -f [long]$manifest.KnownChunkCount }
    $size = '{0:N1} MiB' -f ([long]$manifest.OutputBytes / 1MB)
    @"
      <a class="card" href="./$generation/web/">
        <strong>$name</strong>
        <span>Render $($manifest.RenderId) &middot; profile $($entry.ProfileVersion) &middot; $dimension &middot; $date</span>
        <span>$chunks known chunks &middot; $size static webroot</span>
      </a>
"@
}

$index = @"
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <title>2b2t Atlas BlueMap canary</title>
  <style>
    :root { color-scheme: dark; font: 16px/1.45 system-ui, sans-serif; background: #0f1217; color: #eef3f8; }
    body { max-width: 900px; margin: 0 auto; padding: 2rem 1rem 4rem; }
    h1 { margin-bottom: .4rem; }
    p { color: #aeb9c5; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit,minmax(260px,1fr)); gap: 1rem; margin-top: 1.5rem; }
    .card { display: grid; gap: .45rem; padding: 1rem; border: 1px solid #334150; border-radius: .75rem; background: #1a2028; color: inherit; text-decoration: none; }
    .card:hover { border-color: #79b8ff; background: #202a35; }
    .card span { color: #aeb9c5; font-size: .9rem; }
  </style>
</head>
<body>
  <h1>2b2t Atlas BlueMap canary</h1>
  <p>Local test-only views generated from exact preserved Atlas WDLs. Only lighting-safe renderer profiles at or above profile $MinimumProfileVersion are listed.</p>
  <div class="grid">
$($cards -join "`n")
  </div>
</body>
</html>
"@
Set-Content -LiteralPath (Join-Path $OutputRoot 'index.html') -Value $index -Encoding UTF8

# Serve a narrow public view containing only the generations accepted above.
# Pointing the generic HTTP server at OutputRoot made rejected experimental
# profiles reachable through a remembered direct URL even though the index no
# longer linked them. Junctions avoid copying immutable BlueMap payloads while
# making old p1-p6 URLs return 404 from localhost as they do in production.
Set-Content -LiteralPath (Join-Path $previewRoot 'index.html') -Value $index -Encoding UTF8
foreach ($entry in $entries) {
    $link = Join-Path $previewRoot $entry.GenerationName
    if (-not (Test-Path -LiteralPath $link)) {
        $target = Join-Path $OutputRoot $entry.GenerationName
        New-Item -ItemType Junction -Path $link -Target $target | Out-Null
    }
}

$listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -in @('127.0.0.1', '::1') } |
    Select-Object -First 1

New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
$stdout = Join-Path $StateRoot 'preview-stdout.log'
$stderr = Join-Path $StateRoot 'preview-stderr.log'

if ($listener) {
    $listenerProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
    $commandLine = if ($null -eq $listenerProcess) { '' } else { [string]$listenerProcess.CommandLine }
    $isAtlasPreview = $listenerProcess.Name -eq 'python.exe' -and
        $commandLine -like '*-m http.server*' -and
        ($commandLine -like "*$OutputRoot*" -or $commandLine -like "*$previewRoot*")
    $servesValidatedRoot = $commandLine -like "*$previewRoot*"
    if ($isAtlasPreview -and -not $servesValidatedRoot) {
        Stop-Process -Id $listener.OwningProcess -Force
        Start-Sleep -Milliseconds 500
        $listener = $null
    } elseif (-not $isAtlasPreview) {
        throw "Port $Port is already owned by a process that is not the Atlas BlueMap preview."
    }
}

if (-not $listener) {
    $arguments = @(
        '-m', 'http.server', [string]$Port,
        '--bind', '127.0.0.1',
        '--directory', $previewRoot
    )
    $process = Start-Process -FilePath $python.Source -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 300
        $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
            Where-Object { $_.OwningProcess -eq $process.Id } |
            Select-Object -First 1
    } while (-not $listener -and (Get-Date) -lt $deadline)
    if (-not $listener) {
        throw "The BlueMap preview server did not start. Review $stderr"
    }
}

$baseUrl = "http://127.0.0.1:$Port/"
$probe = Invoke-WebRequest -Uri $baseUrl -UseBasicParsing -TimeoutSec 10
if ($probe.StatusCode -ne 200) {
    throw "The BlueMap preview returned HTTP $($probe.StatusCode)."
}

[pscustomobject]@{
    State = 'ready'
    Url = $baseUrl
    CompletedGenerations = $entries.Count
    OutputRoot = $OutputRoot
    PreviewRoot = $previewRoot
    LocalhostOnly = $true
}

