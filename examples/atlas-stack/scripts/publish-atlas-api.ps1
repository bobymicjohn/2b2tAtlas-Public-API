[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repoRoot "build\atlas-api-$stamp"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory"
}

$project = Join-Path $repoRoot '2b2tAtlas.Server\2b2tAtlas.Server.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Atlas API publish failed.' }

Write-Host "Atlas API package: $OutputDirectory"
Write-Host 'Keep persistent data outside the app package. Set Database__Path to the database file before startup.'
Write-Host 'Set HostStaticClient=false when a separate host serves the frontend.'
Write-Host 'Set production secrets through the task/service environment; do not edit them into appsettings.json.'
