[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][uri]$ApiBaseUrl,
    [uri]$SiteBaseUrl='https://atlas.example',
    [string]$OutputDirectory,
    [string]$SeoDataApiBaseUrl,
    [string]$GroupEvidenceIndexPath,
    [string]$SeoFingerprint
)
$ErrorActionPreference='Stop'
if (-not $ApiBaseUrl.IsAbsoluteUri -or ($ApiBaseUrl.Scheme -ne 'https' -and -not $ApiBaseUrl.IsLoopback)) {
    throw 'API URL must be HTTPS, or loopback for local development.'
}
$root=Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $root ('build\static-'+(Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; existing releases are retained.' }
$publish=Join-Path $OutputDirectory 'publish'
& dotnet publish (Join-Path $root '2b2tAtlas.Client\2b2tAtlas.Client.csproj') -c Release --nologo -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }
$wwwroot=Join-Path $publish 'wwwroot'
$public=Join-Path $OutputDirectory 'public_html'
New-Item -ItemType Directory -Path $public -Force | Out-Null
Copy-Item -Path (Join-Path $wwwroot '*') -Destination $public -Recurse
@{ApiBaseUrl=$ApiBaseUrl.AbsoluteUri.TrimEnd('/')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $public 'appsettings.json') -Encoding UTF8
@'
Options -Indexes
RewriteEngine On
RewriteCond %{REQUEST_FILENAME} !-f
RewriteRule \.(wasm|js|css|json|png|webp|svg|woff2?)$ - [R=404,L]
RewriteCond %{REQUEST_FILENAME} !-f
RewriteCond %{REQUEST_FILENAME} !-d
RewriteRule ^ index.html [L]
AddType application/wasm .wasm
'@ | Set-Content -LiteralPath (Join-Path $public '.htaccess') -Encoding ASCII
# This directory contains only published browser assets. Private .local state is outside it.
Compress-Archive -Path (Join-Path $public '*') -DestinationPath ($OutputDirectory+'.zip')
Write-Output "Static client: $public"
Write-Output "ZIP: $OutputDirectory.zip"
Write-Output 'Configure canonical URLs/map origins before public indexing. See the SEO reference recipe for catalog exports.'
