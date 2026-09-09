[CmdletBinding()]
param([ValidateRange(1024,65535)][int]$Port=5297, [switch]$InitializeOnly, [switch]$SkipBuild)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$local=Join-Path $root '.local'
New-Item -ItemType Directory -Path $local -Force | Out-Null
$secretPath=Join-Path $local 'secrets.json'
if (-not (Test-Path -LiteralPath $secretPath)) {
    $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $keyBytes=New-Object byte[] 48
        $passwordBytes=New-Object byte[] 24
        $workerBytes=New-Object byte[] 48
        $rng.GetBytes($keyBytes); $rng.GetBytes($passwordBytes); $rng.GetBytes($workerBytes)
        $secrets=[ordered]@{
            JwtKey=[Convert]::ToBase64String($keyBytes)
            OwnerPassword=[Convert]::ToBase64String($passwordBytes)
            WorkerKey=[Convert]::ToBase64String($workerBytes)
        }
        $json=$secrets | ConvertTo-Json
        [IO.File]::WriteAllText($secretPath,$json,(New-Object Text.UTF8Encoding($false)))
        # A private local file, never placed under a static content directory.
        $acl=Get-Acl -LiteralPath $secretPath
        $acl.SetAccessRuleProtection($true,$false)
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent().User
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','Allow')))
        Set-Acl -LiteralPath $secretPath -AclObject $acl
    } finally { $rng.Dispose() }
}
$secrets=Get-Content -LiteralPath $secretPath -Raw | ConvertFrom-Json
if ($secrets.JwtKey.Length -lt 32 -or $secrets.OwnerPassword.Length -lt 16 -or $secrets.WorkerKey.Length -lt 32) {
    throw 'Invalid local secrets. Repair the private secrets.json file; do not use sample passwords.'
}
Write-Output "Local owner: atlas-owner. Password is in $secretPath (not printed)."
if ($InitializeOnly) { return }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is already in use." }
$app=Join-Path $local 'app'
if (-not $SkipBuild) {
    & dotnet publish (Join-Path $root '2b2tAtlas.Server\2b2tAtlas.Server.csproj') -c Release --nologo -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
}
$dll=Join-Path $app '2b2tAtlas.Server.dll'
if (-not (Test-Path -LiteralPath $dll)) { throw 'Publish the application first.' }
$sha=[Security.Cryptography.SHA256]::Create()
try { $workerHash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($secrets.WorkerKey)))).Replace('-','').ToLowerInvariant() }
finally { $sha.Dispose() }
$settings=@{
    ASPNETCORE_URLS="http://127.0.0.1:$Port"; ASPNETCORE_ENVIRONMENT='Production'; HostStaticClient='true'
    JwtSettings__SecretKey=$secrets.JwtKey; Bootstrap__OwnerPassword=$secrets.OwnerPassword
    Bootstrap__SeedHistoricalCatalog='false'; Database__Path=(Join-Path $local 'data\atlas.db')
    Recovery__Root=(Join-Path $local 'recovery'); IngestionWorker__ApiKeySha256=$workerHash
    IngestionWorker__IntakeRoot=(Join-Path $local 'intake'); WdlArchive__Root=(Join-Path $local 'archive')
    BlueMap__OutputRoot=(Join-Path $local 'bluemap'); BlueMap__StatusPath=(Join-Path $local 'bluemap-status.json')
    ArchiveCollectorStatus__RunRoot=(Join-Path $local 'collector\run')
    ArchiveCollectorStatus__PrimaryProfileRoot=(Join-Path $local 'collector\profile-1')
    ArchiveCollectorStatus__ProfilesRoot=(Join-Path $local 'collector\profiles')
    AiEnrichment__Enabled='false'
}
$previous=@{}
try {
    foreach($name in $settings.Keys) {
        $previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,$settings[$name],'Process')
    }
    Write-Output "Atlas example: http://127.0.0.1:$Port/ (Ctrl+C to stop)"
    & dotnet $dll --contentRoot $app
} finally {
    foreach($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') }
}
