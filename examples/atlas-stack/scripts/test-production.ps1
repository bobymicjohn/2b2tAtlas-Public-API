[CmdletBinding()]
param(
    [uri]$SiteBaseUrl = 'https://atlas.example',
    [uri]$ApiBaseUrl = 'http://127.0.0.1:5297',
    [uri]$AtlasTileBaseUrl = 'https://tiles.atlas.example/AtlasTiles',
    [int]$MinimumLocations = 1200,
    [int]$MinimumGroups = 20,
    [int]$MinimumHighways = 80,
    [int]$MaximumApiMilliseconds = 2500,
    [int]$MaximumCachedTileMilliseconds = 1500,
    [switch]$RequirePublicApiEdgeCache,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$siteBase = $SiteBaseUrl.AbsoluteUri.TrimEnd('/')
$apiBase = $ApiBaseUrl.AbsoluteUri.TrimEnd('/')
$atlasTileBase = $AtlasTileBaseUrl.AbsoluteUri.TrimEnd('/')
$results = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Details,
        [string]$Category
    )

    $results.Add([pscustomobject]@{
        Category = $Category
        Name = $Name
        Passed = $Passed
        Details = $Details
    })
}

function Invoke-AtlasRequest {
    param(
        [string]$Url,
        [string]$Method = 'GET',
        [hashtable]$Headers = @{}
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Uri $Url -Method $Method -Headers $Headers -UseBasicParsing -TimeoutSec 45
        $stopwatch.Stop()
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            Milliseconds = $stopwatch.ElapsedMilliseconds
            Headers = $response.Headers
            Content = $response.Content
            Bytes = $response.RawContentStream.ToArray()
            Error = $null
        }
    } catch {
        $stopwatch.Stop()
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        return [pscustomobject]@{
            Status = $status
            Milliseconds = $stopwatch.ElapsedMilliseconds
            Headers = @{}
            Content = ''
            Bytes = [byte[]]@()
            Error = $_.Exception.Message
        }
    }
}

function Header-Value {
    param($Response, [string]$Name)
    $value = $Response.Headers[$Name]
    if ($null -eq $value) { return '' }
    return [string]$value
}

function Test-Magic {
    param([byte[]]$Bytes, [byte[]]$Expected)
    if ($Bytes.Length -lt $Expected.Length) { return $false }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Bytes[$index] -ne $Expected[$index]) { return $false }
    }
    return $true
}

Write-Host '2b2t Atlas production acceptance'
Write-Host "Site: $siteBase"
Write-Host "API:  $apiBase"

$publicRoutes = @(
    '/',
    '/map?dimension=overworld&x=0&z=0&zoom=5',
    '/map?dimension=nether&x=0&z=0&zoom=5',
    '/map?dimension=end&x=0&z=0&zoom=5',
    '/groups',
    '/api',
    '/about',
    '/extras',
    '/login',
    '/location/1'
)

foreach ($route in $publicRoutes) {
    $response = Invoke-AtlasRequest "$siteBase$route"
    $isHtml = (Header-Value $response 'Content-Type') -like 'text/html*'
    Add-Check "Route $route" ($response.Status -eq 200 -and $isHtml) `
        "HTTP $($response.Status), $($response.Milliseconds) ms, $(Header-Value $response 'Content-Type')" 'Routes'
}

$originHeaders = @{ Origin = $siteBase }
$locationsResponse = Invoke-AtlasRequest "$apiBase/api/locations" -Headers $originHeaders
$groupsResponse = Invoke-AtlasRequest "$apiBase/api/groups" -Headers $originHeaders
$highwaysResponse = Invoke-AtlasRequest "$apiBase/api/highways" -Headers $originHeaders
$rendersResponse = Invoke-AtlasRequest "$apiBase/api/maprenders" -Headers $originHeaders
$legacyLocationsResponse = Invoke-AtlasRequest "$apiBase/api/locations.php?rows=4&dimension=1" -Headers @{ Origin = 'https://community.example' }
$legacyCountResponse = Invoke-AtlasRequest "$apiBase/api/locationCount.php" -Headers @{ Origin = 'https://community.example' }

$apiResponses = @(
    @{ Name = 'Locations'; Path = 'locations'; Response = $locationsResponse; Minimum = $MinimumLocations },
    @{ Name = 'Groups'; Path = 'groups'; Response = $groupsResponse; Minimum = $MinimumGroups },
    @{ Name = 'Highways'; Path = 'highways'; Response = $highwaysResponse; Minimum = $MinimumHighways },
    @{ Name = 'Published renders'; Path = 'maprenders'; Response = $rendersResponse; Minimum = 0 }
)

foreach ($item in $apiResponses) {
    $response = $item.Response
    $rowCount = 0
    $jsonValid = $false
    try {
        $parsed = ConvertFrom-Json -InputObject $response.Content
        $rowCount = if ($null -eq $parsed) { 0 } elseif ($parsed -is [array]) { $parsed.Length } else { 1 }
        $jsonValid = $true
    } catch {
        $jsonValid = $false
    }
    $cors = Header-Value $response 'Access-Control-Allow-Origin'
    # Public read endpoints deliberately allow third-party Atlas consumers and may
    # return wildcard CORS; authenticated/write surfaces retain the restricted policy.
    $corsValid = $cors -eq '*' -or $cors -eq $siteBase
    $passed = $response.Status -eq 200 -and $jsonValid -and $rowCount -ge $item.Minimum -and $corsValid
    Add-Check "$($item.Name) public API" $passed `
        "HTTP $($response.Status), $rowCount rows, $($response.Milliseconds) ms, CORS '$cors'" 'API'
    Add-Check "$($item.Name) API latency" ($response.Milliseconds -le $MaximumApiMilliseconds) `
        "$($response.Milliseconds) ms (limit $MaximumApiMilliseconds ms)" 'Performance'
}

try {
    $legacyLocations = @(ConvertFrom-Json -InputObject $legacyLocationsResponse.Content | ForEach-Object { $_ })
    $legacyFirst = $legacyLocations | Select-Object -First 1
    $legacyShape = $legacyFirst -and
        $legacyFirst.PSObject.Properties.Name -contains 'location_uuid' -and
        $legacyFirst.PSObject.Properties.Name -contains 'time_added' -and
        $legacyFirst.PSObject.Properties.Name -contains 'video_url' -and
        $legacyFirst.PSObject.Properties.Name -contains 'end_dimension'
    $legacyAllEnd = @($legacyLocations | Where-Object { $_.end_dimension -ne 1 }).Count -eq 0
    Add-Check 'Legacy locations API compatibility' `
        ($legacyLocationsResponse.Status -eq 200 -and $legacyLocations.Count -eq 4 -and $legacyShape -and $legacyAllEnd) `
        "HTTP $($legacyLocationsResponse.Status), $($legacyLocations.Count) rows, CORS '$(Header-Value $legacyLocationsResponse 'Access-Control-Allow-Origin')'" 'API'
} catch {
    Add-Check 'Legacy locations API compatibility' $false $_.Exception.Message 'API'
}

try {
    $legacyCount = ConvertFrom-Json -InputObject $legacyCountResponse.Content
    Add-Check 'Legacy location count compatibility' `
        ($legacyCountResponse.Status -eq 200 -and [int]$legacyCount.locationCount -ge $MinimumLocations) `
        "HTTP $($legacyCountResponse.Status), count $($legacyCount.locationCount)" 'API'
} catch {
    Add-Check 'Legacy location count compatibility' $false $_.Exception.Message 'API'
}

$legacyWriteResponse = Invoke-AtlasRequest "$apiBase/api/newWarp.php?location_uuid_fk=test&name=test"
Add-Check 'Legacy anonymous warp write remains retired' ($legacyWriteResponse.Status -eq 410) `
    "HTTP $($legacyWriteResponse.Status)" 'Security'

$privateRoutes = @(
    '/api/admin/users',
    '/api/audit',
    '/api/highways/pending',
    '/api/revisions',
    '/api/roles'
)

foreach ($route in $privateRoutes) {
    $response = Invoke-AtlasRequest "$apiBase$route"
    Add-Check "Anonymous blocked from $route" ($response.Status -eq 401) `
        "HTTP $($response.Status)" 'Security'
}

$indexResponse = Invoke-AtlasRequest "$siteBase/"
$configResponse = Invoke-AtlasRequest "$siteBase/appsettings.json"
$dotnetResponse = Invoke-AtlasRequest "$siteBase/_framework/dotnet.js"
$mapScriptResponse = Invoke-AtlasRequest "$siteBase/js/atlas-map.js"

foreach ($item in @(
    @{ Name = 'HTML shell'; Response = $indexResponse },
    @{ Name = 'Runtime config'; Response = $configResponse },
    @{ Name = 'dotnet.js bootstrap'; Response = $dotnetResponse },
    @{ Name = 'Atlas map module'; Response = $mapScriptResponse }
)) {
    $cacheControl = Header-Value $item.Response 'Cache-Control'
    $passed = $cacheControl -match 'no-cache|no-store'
    Add-Check "$($item.Name) remains mutable" $passed `
        "Cache-Control '$cacheControl', CF $(Header-Value $item.Response 'CF-Cache-Status')" 'Caching'
}

Add-Check 'Map module configures API tile proxy' ($mapScriptResponse.Content -match 'export function setApiBase') `
    'atlas-map.js exports setApiBase' 'Caching'

$clientWasmMatch = [regex]::Match($dotnetResponse.Content, '"name"\s*:\s*"(2b2tAtlas\.Client\.[a-z0-9]{10}\.wasm)"')
if ($clientWasmMatch.Success) {
    $clientWasmUrl = "$siteBase/_framework/$($clientWasmMatch.Groups[1].Value)"
    $wasmFirst = Invoke-AtlasRequest $clientWasmUrl
    $wasmSecond = Invoke-AtlasRequest $clientWasmUrl
    $cacheControl = Header-Value $wasmSecond 'Cache-Control'
    $cacheStatus = Header-Value $wasmSecond 'CF-Cache-Status'
    Add-Check 'Fingerprint client WASM is immutable' ($cacheControl -match 'immutable' -and $cacheControl -match '31536000') `
        "Cache-Control '$cacheControl'" 'Caching'
    Add-Check 'Fingerprint client WASM reaches Cloudflare cache' ($cacheStatus -in @('HIT', 'REVALIDATED')) `
        "CF $cacheStatus after warm request" 'Caching'
} else {
    Add-Check 'Locate fingerprint client WASM' $false 'dotnet.js did not contain the expected client WASM name' 'Caching'
}

$proxyTiles = @(
    "$apiBase/tiles/place/base/4/0/0/0/t.0.0.webp",
    "$apiBase/tiles/place/overlay/4/0/0/0/t.0.0.webp"
)

foreach ($url in $proxyTiles) {
    $null = Invoke-AtlasRequest $url
    $response = Invoke-AtlasRequest $url
    $isWebP = (Header-Value $response 'Content-Type') -eq 'image/webp' -and
        (Test-Magic $response.Bytes ([byte[]](0x52, 0x49, 0x46, 0x46))) -and
        $response.Bytes.Length -ge 12 -and
        [Text.Encoding]::ASCII.GetString($response.Bytes, 8, 4) -eq 'WEBP'
    $cacheStatus = Header-Value $response 'CF-Cache-Status'
    $cacheControl = Header-Value $response 'Cache-Control'
    Add-Check "2b2t.place tile $(Split-Path $url -Leaf) is WebP" $isWebP `
        "HTTP $($response.Status), $($response.Bytes.Length) bytes" 'Tiles'
    Add-Check "2b2t.place tile is edge cached" ($cacheStatus -in @('HIT', 'REVALIDATED')) `
        "CF $cacheStatus, Cache-Control '$cacheControl', $($response.Milliseconds) ms" 'Caching'
    Add-Check "2b2t.place cached tile latency" ($response.Milliseconds -le $MaximumCachedTileMilliseconds) `
        "$($response.Milliseconds) ms (limit $MaximumCachedTileMilliseconds ms)" 'Performance'
}

$atlasTileUrl = "$atlasTileBase/Overworld/256k/day/5/16/16.png"
$null = Invoke-AtlasRequest $atlasTileUrl
$atlasTile = Invoke-AtlasRequest $atlasTileUrl
$isPng = (Header-Value $atlasTile 'Content-Type') -eq 'image/png' -and
    (Test-Magic $atlasTile.Bytes ([byte[]](0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)))
Add-Check 'Atlas tile is valid PNG' $isPng `
    "HTTP $($atlasTile.Status), $($atlasTile.Bytes.Length) bytes" 'Tiles'
Add-Check 'Atlas tile is edge cached' ((Header-Value $atlasTile 'CF-Cache-Status') -in @('HIT', 'REVALIDATED')) `
    "CF $(Header-Value $atlasTile 'CF-Cache-Status'), Cache-Control '$(Header-Value $atlasTile 'Cache-Control')'" 'Caching'

foreach ($item in $apiResponses) {
    $response = Invoke-AtlasRequest "$apiBase/api/$($item.Path)" -Headers $originHeaders
    $cacheStatus = Header-Value $response 'CF-Cache-Status'
    $isCached = $cacheStatus -in @('HIT', 'REVALIDATED')
    $required = $RequirePublicApiEdgeCache.IsPresent
    Add-Check "$($item.Name) API edge cache" ($isCached -or -not $required) `
        "CF $cacheStatus$(if (-not $required) { ' (informational)' })" 'Caching'
}

$failed = @($results | Where-Object { -not $_.Passed })
$passed = @($results | Where-Object { $_.Passed })
$summary = [pscustomobject]@{
    TimestampUtc = [DateTime]::UtcNow.ToString('o')
    SiteBaseUrl = $siteBase
    ApiBaseUrl = $apiBase
    Passed = $passed.Count
    Failed = $failed.Count
    Checks = $results
}

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ReportPath = Join-Path (Split-Path -Parent $PSScriptRoot) ".artifacts\production-acceptance-$stamp.json"
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
$reportDirectory = Split-Path -Parent $ReportPath
if (-not (Test-Path $reportDirectory)) {
    New-Item -ItemType Directory -Path $reportDirectory | Out-Null
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $ReportPath -Encoding UTF8

$results | Select-Object Category, Name, Passed, Details | Format-Table -AutoSize -Wrap
Write-Host "Passed: $($passed.Count)  Failed: $($failed.Count)"
Write-Host "Report: $ReportPath"

if ($failed.Count -gt 0) { exit 1 }
