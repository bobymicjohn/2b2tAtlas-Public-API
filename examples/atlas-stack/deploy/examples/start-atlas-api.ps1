[CmdletBinding()]
param(
    [string]$AppRoot = 'C:\AtlasExample\Api\app',
    [string]$DataRoot = 'C:\AtlasExample\Api\data',
    [string]$EnvironmentFile = 'C:\AtlasExample\Api\config\secrets.ps1'
)
$ErrorActionPreference = 'Stop'
$binary = Join-Path $AppRoot '2b2tAtlas.Server.exe'
foreach ($file in @($binary,$EnvironmentFile)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing host file: $file" }
}
if (-not (Test-Path -LiteralPath $DataRoot -PathType Container)) { throw 'Create the private API data directory first.' }
. $EnvironmentFile
if ([string]::IsNullOrWhiteSpace($env:JwtSettings__SecretKey)) { throw 'API signing key is not configured.' }
if ([string]::IsNullOrWhiteSpace($env:ASPNETCORE_URLS)) { $env:ASPNETCORE_URLS = 'http://127.0.0.1:5297' }
Set-Location -LiteralPath $DataRoot
& $binary --contentRoot $AppRoot
exit $LASTEXITCODE
