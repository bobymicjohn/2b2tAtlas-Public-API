<#
.SYNOPSIS
  Prune old 2b2t Atlas blue-green deploy backups, keeping the newest few as rollback points.

.DESCRIPTION
  Each API/worker deploy renames the previous binaries aside (app-prev-<stamp> / worker-prev-<stamp>)
  as a rollback copy. Those accumulate forever. This keeps the newest -Keep in each location and
  deletes the rest. Registered as the "2b2t Atlas Backup Prune" scheduled task (daily).
#>
[CmdletBinding()]
param(
    [int]$Keep = 3
)
$ErrorActionPreference = 'Stop'

$logDir = 'C:\AtlasExample\Ops\logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$log = Join-Path $logDir ('atlas-prune-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))
function Write-Log([string]$msg) {
    $entry = ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg)
    for ($i = 0; $i -lt 5; $i++) {
        try { Add-Content -Path $log -Value $entry -ErrorAction Stop; break }
        catch { Start-Sleep -Milliseconds (50 * ($i + 1)) }
    }
}

$targets = @(
    @{ Path = 'C:\AtlasExample\Api';    Filter = 'app-prev-*' },
    @{ Path = 'C:\AtlasExample\Ingest'; Filter = 'worker-prev-*' }
)

foreach ($t in $targets) {
    if (-not (Test-Path $t.Path)) { continue }
    $dirs = Get-ChildItem $t.Path -Directory -Filter $t.Filter -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending
    $stale = @($dirs | Select-Object -Skip $Keep)
    if ($stale.Count -eq 0) {
        Write-Log ("{0}\{1}: {2} kept, 0 pruned" -f $t.Path, $t.Filter, $dirs.Count)
        continue
    }
    $freed = 0L
    foreach ($d in $stale) {
        $freed += (Get-ChildItem $d.FullName -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum
        Remove-Item $d.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
    Write-Log ("{0}\{1}: kept newest {2}, pruned {3} ({4} MB freed)" -f `
        $t.Path, $t.Filter, $Keep, $stale.Count, [math]::Round($freed / 1MB))
}
