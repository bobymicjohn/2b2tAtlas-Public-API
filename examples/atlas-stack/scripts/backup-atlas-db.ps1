[CmdletBinding()]
param(
    [string]$DatabasePath = 'C:\AtlasExample\Api\data\atlas.db',
    [string]$BackupRoot = 'B:\AtlasExample\Backups',
    [ValidateRange(2, 365)]
    [int]$RetentionCount = 30
)

$ErrorActionPreference = 'Stop'
$database = [IO.Path]::GetFullPath($DatabasePath)
$backupRootPath = [IO.Path]::GetFullPath($BackupRoot)
if (-not (Test-Path $database -PathType Leaf)) { throw "Atlas database not found: $database" }
New-Item -ItemType Directory -Path $backupRootPath -Force | Out-Null

$sqlite = (Get-Command sqlite3 -ErrorAction Stop).Source
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$backupPath = Join-Path $backupRootPath "atlas-$stamp.db"
$manifestPath = "$backupPath.json"
$escapedBackup = $backupPath.Replace("'", "''")

& $sqlite $database ".backup '$escapedBackup'"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $backupPath -PathType Leaf)) {
    throw 'SQLite online backup failed.'
}

$integrity = (& $sqlite $backupPath 'PRAGMA integrity_check;').Trim()
if ($LASTEXITCODE -ne 0 -or $integrity -ne 'ok') {
    Remove-Item $backupPath -Force -ErrorAction SilentlyContinue
    throw "Backup integrity check failed: $integrity"
}

$item = Get-Item $backupPath
$hash = (Get-FileHash $backupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    schemaVersion = 1
    createdUtc = (Get-Date).ToUniversalTime().ToString('o')
    sourceDatabase = $database
    backupPath = $backupPath
    bytes = $item.Length
    sha256 = $hash
    integrityCheck = $integrity
}
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), $utf8NoBom)

$verified = @(Get-ChildItem $backupRootPath -File -Filter 'atlas-*.db' | Sort-Object LastWriteTimeUtc -Descending)
foreach ($old in $verified | Select-Object -Skip $RetentionCount) {
    Remove-Item $old.FullName -Force
    Remove-Item "$($old.FullName).json" -Force -ErrorAction SilentlyContinue
}

$manifest | Format-List
Write-Host "Verified backup: $backupPath"
