function Install-PendingArchiveCoverage([string]$InstanceRoot) {
    $pending = Join-Path $InstanceRoot 'pending-coverage-mod.json'
    if (-not (Test-Path -LiteralPath $pending -PathType Leaf)) { return }
    $manifest = Get-Content -LiteralPath $pending -Raw | ConvertFrom-Json
    $source = [IO.Path]::GetFullPath([string]$manifest.source)
    if (-not $source.StartsWith('C:\AtlasExample\Ingest\archive-sync\tool-cache\', [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $source) -notmatch '^atlas-archive-coverage-[0-9.]+\.jar$' -or $manifest.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid staged coverage artifact.' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Staged coverage hash mismatch.' }
    $game = [IO.Path]::GetFullPath((Join-Path $InstanceRoot 'game'))
    $live = @(Get-CimInstance Win32_Process -Filter "Name='java.exe'" | Where-Object {
        $_.CommandLine -and $_.CommandLine.IndexOf($game, [StringComparison]::OrdinalIgnoreCase) -ge 0
    })
    if ($live.Count -gt 0) { throw 'Coverage installation requires the old instance to have exited.' }
    $mods = Join-Path $game 'mods'
    $destination = Join-Path $mods (Split-Path -Leaf $source)
    $temporary = $destination + '.pending-copy'
    Copy-Item -LiteralPath $source -Destination $temporary -Force
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Coverage copy hash mismatch.' }
    $backups = @()
    try {
        foreach ($prior in Get-ChildItem -LiteralPath $mods -Filter 'atlas-archive-coverage-*.jar' -File) {
            $backup = $prior.FullName + '.disabled-' + [datetime]::UtcNow.ToString('yyyyMMddHHmmss')
            Move-Item -LiteralPath $prior.FullName -Destination $backup
            $backups += @{Original=$prior.FullName;Backup=$backup}
        }
        Move-Item -LiteralPath $temporary -Destination $destination
    } catch {
        foreach ($backup in $backups) { if (-not (Test-Path -LiteralPath $backup.Original)) { Move-Item -LiteralPath $backup.Backup -Destination $backup.Original } }
        throw
    }
    Move-Item -LiteralPath $pending -Destination (Join-Path $InstanceRoot 'installed-coverage-mod.json') -Force
    Write-Output "COVERAGE-UPGRADED $(Split-Path -Leaf $destination) sha256=$($manifest.sha256) at safe startup boundary"
}
