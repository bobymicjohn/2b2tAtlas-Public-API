[CmdletBinding()]
param(
    [string]$ServerPublishRoot = '',
    [string]$StagingRoot = '',
    [string]$ProductionDatabase = 'C:\AtlasExample\Api\data\atlas.db',
    [string]$RendererProfile = 'C:\AtlasExample\Research\unmined-1m-tuning\renderer.1m-candidate.json',
    [string]$SampleWorldsRoot = 'C:\AtlasExample\Ingest\sample-worlds',
    [string]$GroupEvidenceIndexPath = 'C:\AtlasExample\Api\data\enrichment\2b2t-wiki-group-audit.json',
    [int]$Port = 8766,
    [switch]$RefreshDatabase,
    [switch]$EnableAiEnrichment
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# PSScriptRoot is not initialized while parameter default expressions are evaluated in every
# PowerShell host. Resolve repository-relative defaults after parameter binding instead.
if ([string]::IsNullOrWhiteSpace($ServerPublishRoot)) {
    $ServerPublishRoot = Join-Path $PSScriptRoot '..\build\staging-api'
}
if ([string]::IsNullOrWhiteSpace($StagingRoot)) {
    $StagingRoot = Join-Path $PSScriptRoot '..\build\staging-runtime'
}

if ($Port -eq 5197) { throw 'Port 5197 is reserved for the production Atlas API.' }
$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($null -ne $listener) { throw "Port $Port is already in use; stop the prior preview first." }

$serverRoot = [System.IO.Path]::GetFullPath($ServerPublishRoot)
$stageRoot = [System.IO.Path]::GetFullPath($StagingRoot)
$serverAssembly = Join-Path $serverRoot '2b2tAtlas.Server.dll'
if (-not (Test-Path -LiteralPath $serverAssembly -PathType Leaf)) {
    throw "Staging server publish was not found: $serverAssembly"
}
$dotnet = Get-Command dotnet -ErrorAction Stop
if (-not (Test-Path -LiteralPath $ProductionDatabase -PathType Leaf)) {
    throw "Production database was not found: $ProductionDatabase"
}
if (-not (Test-Path -LiteralPath $RendererProfile -PathType Leaf)) {
    throw "Candidate renderer profile was not found: $RendererProfile"
}
if (-not (Test-Path -LiteralPath $GroupEvidenceIndexPath -PathType Leaf)) {
    throw "Group evidence index was not found: $GroupEvidenceIndexPath"
}

New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
foreach ($name in @('intake', 'archive', 'logs')) {
    New-Item -ItemType Directory -Path (Join-Path $stageRoot $name) -Force | Out-Null
}

$stageDatabase = Join-Path $stageRoot 'atlas.db'
if ($RefreshDatabase -or -not (Test-Path -LiteralPath $stageDatabase -PathType Leaf)) {
    $sqlite = Get-Command sqlite3 -ErrorAction Stop
    if (Test-Path -LiteralPath $stageDatabase) { Remove-Item -LiteralPath $stageDatabase -Force }
    $escaped = $stageDatabase.Replace("'", "''")
    & $sqlite.Source $ProductionDatabase ".backup '$escaped'"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $stageDatabase -PathType Leaf)) {
        throw 'SQLite online backup for staging failed.'
    }
}

$secretPath = Join-Path $stageRoot 'jwt-secret.txt'
if (-not (Test-Path -LiteralPath $secretPath -PathType Leaf)) {
    $secretBytes = New-Object byte[] 64
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($secretBytes) } finally { $random.Dispose() }
    [System.IO.File]::WriteAllText($secretPath, [Convert]::ToBase64String($secretBytes), [Text.Encoding]::UTF8)
}
$secret = [System.IO.File]::ReadAllText($secretPath, [Text.Encoding]::UTF8).Trim()

# Keep local-intake authentication isolated from production while allowing the
# staging API to accept jobs from the same trusted example host worker process.
# The raw key is never persisted in the staging runtime; ASP.NET receives only
# the SHA-256 digest, matching the production API's trust boundary.
$workerKey = [Environment]::GetEnvironmentVariable('ATLAS_INGEST_WORKER_KEY')
$workerKeyHash = $null
if (-not [string]::IsNullOrWhiteSpace($workerKey)) {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($workerKey))
        $workerKeyHash = ([BitConverter]::ToString($hashBytes)).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha256.Dispose()
    }
}

$settings = [ordered]@{
    'ASPNETCORE_URLS' = "http://127.0.0.1:$Port"
    'ASPNETCORE_ENVIRONMENT' = 'Staging'
    'ASPNETCORE_CONTENTROOT' = $serverRoot
    'JwtSettings__SecretKey' = $secret
    'HostStaticClient' = 'true'
    'IngestionWorker__IntakeRoot' = (Join-Path $stageRoot 'intake')
    'WdlArchive__Root' = (Join-Path $stageRoot 'archive')
    'RenderSettings__RendererJsonPath' = [System.IO.Path]::GetFullPath($RendererProfile)
    'RenderSettings__SampleWorldsRoot' = [System.IO.Path]::GetFullPath($SampleWorldsRoot)
    'AiEnrichment__Enabled' = $(if ($EnableAiEnrichment) { 'true' } else { 'false' })
    'AiEnrichment__GroupEvidenceIndexPath' = [System.IO.Path]::GetFullPath($GroupEvidenceIndexPath)
}
if ($null -ne $workerKeyHash) {
    $settings['IngestionWorker__ApiKeySha256'] = $workerKeyHash
}
$prior = @{}
try {
    foreach ($pair in $settings.GetEnumerator()) {
        $prior[$pair.Key] = [Environment]::GetEnvironmentVariable($pair.Key, 'Process')
        [Environment]::SetEnvironmentVariable($pair.Key, [string]$pair.Value, 'Process')
    }
    $stdout = Join-Path $stageRoot 'logs\stdout.log'
    $stderr = Join-Path $stageRoot 'logs\stderr.log'
    # Launch through the registered x64 host. A framework-dependent Windows apphost can
    # incorrectly probe its publish directory as DOTNET_ROOT on some machines.
    $process = Start-Process -FilePath $dotnet.Source -ArgumentList @($serverAssembly) -WorkingDirectory $stageRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    [System.IO.File]::WriteAllText((Join-Path $stageRoot 'server.pid'), [string]$process.Id, [Text.Encoding]::ASCII)
} finally {
    foreach ($pair in $settings.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($pair.Key, $prior[$pair.Key], 'Process')
    }
}

$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 250
    try {
        $probe = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$Port/api/locations" -TimeoutSec 3
        if ($probe.StatusCode -eq 200) {
            Write-Output "Atlas staging is ready at http://127.0.0.1:$Port/ (PID $($process.Id))."
            Write-Output "Database, intake, archive, JWT key, and renderer settings are isolated under $stageRoot."
            return
        }
    } catch { }
} while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)

throw "Staging did not become ready. Inspect $(Join-Path $stageRoot 'logs\stderr.log')."
