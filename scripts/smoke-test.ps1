$ErrorActionPreference = 'Stop'
$baseUrl = if ([string]::IsNullOrWhiteSpace($env:ATLAS_API_BASE_URL)) {
    'https://api.blackportal.cloud'
} else {
    $env:ATLAS_API_BASE_URL.TrimEnd('/')
}
$routes = @(
    '/api',
    '/openapi/v1.json',
    '/api/locations/5',
    '/api/groups/7',
    '/api/warps?locationId=5&limit=1',
    '/api/renders?locationId=5&limit=1',
    '/api/renders/38/world-download',
    '/api/attachments?limit=1',
    '/api/highways/1',
    '/api/maprenders/catalog'
    '/api/nocom'
    '/api/nocom/periods?dimension=nether'
    '/api/nocom/highways?dimension=nether&direction=northeast'
)

foreach ($route in $routes) {
    $response = Invoke-WebRequest -UseBasicParsing -Uri ($baseUrl + $route) -TimeoutSec 30 `
        -Headers @{ Accept = 'application/json'; 'User-Agent' = '2b2tAtlas-Public-API-Smoke/1.0' }
    if ($response.StatusCode -ne 200 -or $response.RawContentLength -lt 2) {
        throw "Unexpected response for ${route}: HTTP $($response.StatusCode), $($response.RawContentLength) bytes"
    }
    Write-Host "OK $route ($($response.RawContentLength) bytes)"
}
