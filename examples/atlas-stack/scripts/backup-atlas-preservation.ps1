[CmdletBinding()]
param([ValidateSet('Metadata','Assets','Preservation')][string]$Tier = 'Metadata')
$ErrorActionPreference = 'Stop'
$restic = 'C:\AtlasExample\Ops\tools\restic\restic_0.19.1_windows_amd64.exe'
$key = 'C:\AtlasExample\Api\config\backup-password.txt'
$repoRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack'
$stateRoot = 'C:\AtlasExample\Recovery'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$lockPath = Join-Path $stateRoot "$Tier.lock"
try { $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
catch { throw "An Atlas $Tier backup is already running." }
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$logPath = Join-Path $stateRoot "$Tier-$stamp.log"
$statusPath = Join-Path $stateRoot "$Tier-status.json"
$status = [ordered]@{ tier=$Tier; startedUtc=(Get-Date).ToUniversalTime().ToString('o'); state='running'; log=$logPath }
function Save-Status { $status | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statusPath -Encoding UTF8 }
try {
    Save-Status
    if ($Tier -eq 'Metadata') {
        $repository = 'B:\AtlasExample\Backups\restic-metadata'
        if ((Get-PSDrive B).Free -lt 100GB) { throw 'Metadata backup drive has less than 100 GiB free.' }
        & "$repoRoot\scripts\backup-atlas-db.ps1" -RetentionCount 365 | Out-File -LiteralPath $logPath -Append -Encoding utf8
        $latest = Get-ChildItem 'B:\AtlasExample\Backups' -File -Filter 'atlas-*.db' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        $paths = @($latest.FullName, "$($latest.FullName).json", 'C:\AtlasExample\Api\config', 'C:\AtlasExample\Api\app', 'C:\AtlasExample\Api\data',
            'C:\AtlasExample\Ingest\config', 'C:\AtlasExample\Ingest\worker', 'C:\AtlasExample\Ingest\tools', 'C:\AtlasExample\Ingest\bluemap', 'C:\AtlasExample\Ingest\archive-sync',
            'C:\AtlasExample\Ingest\external-downloads', 'C:\AtlasExample\Seo', "$repoRoot\scripts", "$repoRoot\docs",
            "$repoRoot\2b2tAtlas.Server", "$repoRoot\2b2tAtlas.Shared", "$repoRoot\2b2tAtlas.Client",
            "$repoRoot\2b2tAtlas.Ingestor", 'C:\AtlasExample\Ops\SERVICE_REFERENCE.md',
            'B:\AtlasExample\Backups\human-edits', 'C:\NGNIX\docker-compose.yml', 'C:\NGNIX\nginx.conf', 'C:\NGNIX\ssl',
            'F:\AtlasExample\AtlasTiles')
        $excludes = @('**/bin/**','**/obj/**','**/node_modules/**','**/game/**','**/libraries/**','**/assets/**',
            '**/logs/**','**/*.log','**/tool-cache/**','**/captured/**','**/failed/**','**/ready/**',
            '**/seed-credentials.txt','**/*.lock','**/*.lck', '**/AtlasTiles/Overworld', '**/AtlasTiles/Nether',
            '**/AtlasTiles/End', '**/AtlasTiles/Nocom','**/atlas.db','**/atlas.db-wal','**/atlas.db-shm')
        $paths += @(Get-ChildItem -LiteralPath 'C:\AtlasExample\Api','C:\AtlasExample\Ingest' -File |
            Where-Object {$_.Extension -in @('.ps1','.vbs','.bat','.json')} | Select-Object -ExpandProperty FullName)
    } else {
        $repository = '\\192.0.2.10\public\AtlasBackups\restic-preservation'
        if ((Get-PSDrive X).Free -lt 1TB) { throw 'Preservation backup NAS has less than 1 TiB free.' }
        $mapping = Get-SmbMapping -LocalPath 'X:' -ErrorAction Stop
        if ($mapping.RemotePath -ne '\\192.0.2.10\public') { throw 'X: does not point at the reviewed Atlas NAS.' }
        # Historical immutable assets and source archives. In-progress torrent payloads
        # and regenerable ingestion scratch are deliberately excluded.
        $paths = @('B:\AtlasExample\Backups', 'E:\AtlasExample\HistoricalMedia',
            'E:\AtlasExample\WorldDownloads\objects',
            'E:\AtlasExample\WorldDownloads\collector',
            'X:\AtlasExample\WorldDownloads\DeferredCaptures',
            'D:\AtlasExample\Ingest\DeferredCaptures',
            'E:\AtlasExample\WorldDownloads\manual-primary',
            'E:\2b2t\Exploits', 'E:\2b2t\WorldDownloads', 'E:\2b2t\Personal World Downloads',
            'E:\2b2t\RawRenders', 'E:\2b2t\MiscRenders', 'E:\2b2t\256k Journey Map Data',
            'E:\2b2t\AtlasTiles', 'F:\AtlasExample\AtlasTiles', 'F:\AtlasExample\AtlasBlueMap')
        $excludes = @('**/restic-metadata/**', '**/*.lock', '**/*.part', '**/*.partial', '**/*.tmp')
        if ($Tier -eq 'Assets') {
            $paths = @('B:\AtlasExample\Backups', 'E:\AtlasExample\HistoricalMedia',
                'E:\AtlasExample\WorldDownloads\objects',
                'E:\AtlasExample\WorldDownloads\collector',
                'X:\AtlasExample\WorldDownloads\DeferredCaptures',
                'D:\AtlasExample\Ingest\DeferredCaptures',
                'E:\AtlasExample\WorldDownloads\manual-primary')
        }
    }
    $status.repository = $repository
    Save-Status
    # Missing roots fail visibly instead of silently producing a partial "success".
    foreach ($path in $paths) { if (-not (Test-Path -LiteralPath $path)) { throw "Required backup source unavailable: $path" } }
    $common = @('--repo', $repository, '--password-file', $key, '--cache-dir', "$stateRoot\cache")
    if (-not (Test-Path -LiteralPath "$repository\config")) {
        & $restic @common init 2>&1 | Out-File -LiteralPath $logPath -Append -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw "Repository initialization failed ($LASTEXITCODE)." }
    }
    # A reboot/interrupted initial transfer can leave a dead-process lock.
    # Default unlock removes only stale locks; never use --remove-all.
    & $restic @common unlock 2>&1 | Out-File -LiteralPath $logPath -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Stale-lock recovery failed.' }
    # Snapshot DB filenames change each hour; grouping by paths would force a
    # fresh file scan/hash baseline every run. Stable host/tag grouping reuses the
    # preceding tier snapshot while still discovering additions/deletions.
    $argsBackup = @('backup','--tag',"atlas-$Tier",'--host','example-host','--group-by','host,tags','--json','--limit-upload','81920')
    foreach ($pattern in $excludes) { $argsBackup += @('--exclude', $pattern) }
    $ErrorActionPreference = 'Continue' # Native stderr is data; restic's exit code determines success.
    & $restic @common @argsBackup @paths 2>&1 | Out-File -LiteralPath $logPath -Append -Encoding utf8
    $backupExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($backupExit -ne 0) { throw "Restic backup failed or was incomplete ($backupExit); see $logPath" }
    & $restic @common check 2>&1 | Out-File -LiteralPath $logPath -Append -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Repository integrity check failed ($LASTEXITCODE)." }
    if ($Tier -eq 'Metadata') {
        $nasRepository = '\\192.0.2.10\public\AtlasBackups\restic-metadata'
        $nasArgs = @('--repo', $nasRepository, '--password-file', $key, '--cache-dir', "$stateRoot\cache")
        if (-not (Test-Path -LiteralPath "$nasRepository\config")) {
            & $restic @nasArgs init --from-repo $repository --from-password-file $key --copy-chunker-params 2>&1 |
                Out-File -LiteralPath $logPath -Append -Encoding utf8
            if ($LASTEXITCODE -ne 0) { throw 'NAS metadata repository initialization failed.' }
        }
        & $restic @nasArgs copy --from-repo $repository --from-password-file $key latest 2>&1 |
            Out-File -LiteralPath $logPath -Append -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw 'NAS metadata copy failed.' }
        & $restic @nasArgs check 2>&1 | Out-File -LiteralPath $logPath -Append -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw 'NAS metadata repository check failed.' }
        $status.nasRepository = $nasRepository
    }
    # Keep every snapshot for now. No automatic forget/prune and no deletion mirroring.
    $status.state = 'verified'; $status.repository = $repository
    $status.completedUtc = (Get-Date).ToUniversalTime().ToString('o')
    Save-Status
} catch {
    $status.state = 'failed'; $status.error = $_.Exception.Message; Save-Status
    throw
} finally { $lock.Dispose() }
