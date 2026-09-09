[CmdletBinding()]
param(
    [string]$WorkerRoot = 'C:\AtlasExample\Ingest\worker',
    [string]$ConfigPath = 'C:\AtlasExample\Ingest\config\worker.json',
    [string]$EnvironmentFile = 'C:\AtlasExample\Ingest\config\secrets.ps1',
    [string]$PausePath = 'C:\AtlasExample\Ingest\pause-worker'
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $PausePath) { return }
$binary = Join-Path $WorkerRoot '2b2tAtlas.Ingestor.exe'
foreach ($file in @($binary,$ConfigPath,$EnvironmentFile)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing host file: $file" }
}
. $EnvironmentFile
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($config.apiKeyEnvironment))) {
    throw 'Worker credential is not configured for this process.'
}
Set-Location -LiteralPath $WorkerRoot
& $binary worker --config $ConfigPath
exit $LASTEXITCODE
