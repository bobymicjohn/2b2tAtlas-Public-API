[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'build\ingestion-worker'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory"
}

$project = Join-Path $repoRoot '2b2tAtlas.Ingestor\2b2tAtlas.Ingestor.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }

Copy-Item (Join-Path $repoRoot '2b2tAtlas.Ingestor\examples\worker.example.json') $OutputDirectory
Copy-Item (Join-Path $repoRoot '2b2tAtlas.Ingestor\examples\renderer-profile.example.json') $OutputDirectory
Write-Host "Worker package: $OutputDirectory"
Write-Host 'Configure worker.example.json, then run: .\2b2tAtlas.Ingestor.exe worker --config .\worker.example.json'