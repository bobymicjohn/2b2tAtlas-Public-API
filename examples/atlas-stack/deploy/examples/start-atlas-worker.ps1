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
$logRoot = Join-Path (Split-Path -Parent $WorkerRoot) 'logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
# The worker handles individual job failures. Capture native stderr directly so
# Windows PowerShell cannot terminate the worker pipeline on an error message.
$process = Start-Process -FilePath $binary -WorkingDirectory $WorkerRoot -ArgumentList @('worker', '--config', ('"{0}"' -f $ConfigPath)) -WindowStyle Hidden -PassThru -Wait -RedirectStandardOutput (Join-Path $logRoot "worker-$stamp.out.log") -RedirectStandardError (Join-Path $logRoot "worker-$stamp.err.log")
exit $process.ExitCode
