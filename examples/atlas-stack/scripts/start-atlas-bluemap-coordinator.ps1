[CmdletBinding()]
param(
    [string]$DatabasePath = 'C:\AtlasExample\Api\data\atlas.db',
    [string]$OutputRoot = 'F:\AtlasExample\AtlasBlueMap\location-renders',
    [string]$StateRoot = 'C:\AtlasExample\Ingest\bluemap',
    [ValidateRange(1,3)][int]$WorkerCount = 3,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$renderer = Join-Path $PSScriptRoot 'invoke-atlas-bluemap-render.ps1'
$started = [datetime]::UtcNow
$lock = $null
$slots = @{}
$failures = @{}
$jobs = @()
$lastDiscovery = [datetime]::MinValue
$dispatchCount = 0
$cooldownPath = Join-Path $StateRoot 'retry-cooldowns.json'
$statusPath = Join-Path $StateRoot 'location-render-status.json'

function Write-State($Value, [string]$Path) {
    $temp = "$Path.tmp-$PID"
    ConvertTo-Json -InputObject $Value -Depth 12 | Set-Content -LiteralPath $temp -Encoding UTF8
    Move-Item -LiteralPath $temp -Destination $Path -Force
}
function Read-State([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    for ($attempt=0; $attempt -lt 3; $attempt++) {
        try { return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json }
        catch { Start-Sleep -Milliseconds 30 }
    }
    return $null
}
function Discover-Jobs {
    $sql = @'
WITH ranked AS (
 SELECT r.Id AS RenderId, l.Rowid AS LocationId, l.Name AS LocationName,
 l.X AS X,l.Z AS Z,j.Dimension,j.ArchiveSha256,
 COALESCE(j.CompletedUtc,j.UpdatedUtc,j.RequestedUtc) AS CompletedUtc,
 row_number() OVER(PARTITION BY r.Id ORDER BY COALESCE(j.CompletedUtc,j.UpdatedUtc,j.RequestedUtc) DESC,j.Id DESC) AS rn
 FROM IngestionJobs j JOIN Renders r ON r.Id=j.RenderId JOIN Locations l ON l.Rowid=r.LocationRowid
 WHERE lower(j.Status)='completed' AND j.ArchiveSha256 IS NOT NULL
)
SELECT * FROM ranked WHERE rn=1 ORDER BY RenderId;
'@
    $json = & sqlite3 -readonly -json $DatabasePath $sql
    if ($LASTEXITCODE -ne 0) { throw 'Could not read eligible BlueMap jobs.' }
    if (-not $json) { return @() }
    return @((($json | Out-String) | ConvertFrom-Json))
}
function Test-Complete($Job) {
    $generation = "render-$($Job.RenderId)-$($Job.ArchiveSha256)-v5.23-p7"
    $root = Join-Path $OutputRoot $generation
    $m = Read-State (Join-Path $root 'manifest.json')
    if ($null -eq $m) { return $false }
    try {
        if ($m.Status -ne 'complete' -or $m.SourceSha256 -ne $Job.ArchiveSha256 -or
            $m.RendererProfileVersion -ne 7 -or -not $m.QualityGate.Passed -or
            -not $m.QualityGate.LocationStartExact -or -not $m.RenderingProfile.Relight.FootprintAuditExact) { return $false }
        $settings = Read-State (Join-Path $root 'web\maps\atlas\settings.json')
        return $null -ne $settings -and $settings.startPos[0] -eq $Job.X -and $settings.startPos[1] -eq $Job.Z
    } catch { return $false }
}
function Next-Job($Pending, [int]$Turn) {
    # Three FIFO backfill assignments for each newest-first assignment. New
    # arrivals get timely service without starving the historical catalog.
    if ($Turn % 4 -eq 0) { return $Pending | Sort-Object CompletedUtc -Descending | Select-Object -First 1 }
    return $Pending | Sort-Object RenderId | Select-Object -First 1
}
function Test-RetryDue($Failure, [datetime]$NowUtc) {
    # PowerShell 5.1 casts an ISO-Z string to LOCAL DateTime. Keep comparison
    # explicitly UTC so Mountain time cannot turn 30 minutes into an immediate retry.
    return [datetimeoffset]::Parse([string]$Failure.RetryAfterUtc).UtcDateTime -le $NowUtc
}

if ($PlanOnly) {
    $plan = @(Discover-Jobs | Where-Object { -not (Test-Complete $_) })
    [pscustomobject]@{ WorkerCount=$WorkerCount; Pending=$plan.Count; NextNewest=(Next-Job $plan 0); NextBackfill=(Next-Job $plan 1) } | ConvertTo-Json -Depth 5
    return
}
if ([IO.Path]::GetFullPath($StateRoot).TrimEnd('\') -ne 'C:\AtlasExample\Ingest\bluemap') {
    throw 'Custom coordinator state roots are supported only for read-only planning.'
}
New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
try {
    # Same exclusive lock as the legacy batch: transition cannot overlap it.
    $lock = [IO.File]::Open((Join-Path $StateRoot 'location-render.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $saved = Read-State $cooldownPath
    if ($null -ne $saved) { foreach ($item in @($saved)) { $failures[[int]$item.RenderId] = $item } }
    while ($true) {
        $now = [datetime]::UtcNow
        if (($now-$lastDiscovery).TotalSeconds -ge 60) {
            try { $jobs = @(Discover-Jobs) }
            catch { Write-Warning "Discovery failed; retaining the last safe job list: $($_.Exception.Message)" }
            $lastDiscovery = $now
        }
        foreach ($id in @($slots.Keys)) {
            $slot = $slots[$id]
            $slot.Process.Refresh()
            if ($slot.Process.HasExited) {
                if (Test-Complete $slot.Job) { $failures.Remove([int]$slot.Job.RenderId) }
                else {
                    $failures[[int]$slot.Job.RenderId] = [pscustomobject]@{
                        RenderId=[int]$slot.Job.RenderId; State='failed'; RetryAfterUtc=$now.AddMinutes(30).ToString('o')
                    }
                }
                $slot.Process.Dispose()
                $slots.Remove($id)
                Write-State @($failures.Values) $cooldownPath
            }
        }
        $complete = @{}
        foreach ($job in $jobs) { if (Test-Complete $job) { $complete[[int]$job.RenderId]=$true } }
        $draining = Test-Path -LiteralPath (Join-Path $StateRoot 'pause-coordinator')
        if (-not $draining) {
            foreach ($id in 1..$WorkerCount) {
                if ($slots.ContainsKey($id)) { continue }
                $runningIds = @($slots.Values | ForEach-Object { [int]$_.Job.RenderId })
                $pending = @($jobs | Where-Object {
                    $rid=[int]$_.RenderId
                    -not $complete.ContainsKey($rid) -and $rid -notin $runningIds -and
                    (-not $failures.ContainsKey($rid) -or (Test-RetryDue $failures[$rid] $now))
                })
                $job = Next-Job $pending $dispatchCount
                if ($null -eq $job) { continue }
                $dispatchCount++
                $workerRoot = Join-Path $StateRoot "workers\$id"
                New-Item -ItemType Directory -Path $workerRoot -Force | Out-Null
                $logKey = "render-$($job.RenderId)-$($now.ToString('yyyyMMdd-HHmmss'))"
                $stdout = Join-Path $workerRoot "$logKey.out.log"
                $stderr = Join-Path $workerRoot "$logKey.err.log"
                $args = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',('"'+$renderer+'"'),
                    '-RenderId',[string]$job.RenderId,'-WorkerId',[string]$id,'-CoordinatorPid',[string]$PID,
                    '-DatabasePath',('"'+$DatabasePath+'"'),'-OutputRoot',('"'+$OutputRoot+'"'))
                $process = Start-Process powershell.exe -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
                $slots[$id] = [pscustomobject]@{Process=$process;Job=$job;StartedUtc=$now.ToString('o');Root=$workerRoot;Stdout=$stdout;Stderr=$stderr}
            }
        }
        $workers = @(foreach ($id in 1..$WorkerCount) {
            if (-not $slots.ContainsKey($id)) {
                [pscustomobject]@{WorkerId=$id;State='idle';Stage='waiting';Current=$null;StartedUtc=$null;UpdatedUtc=$now.ToString('o')}
                continue
            }
            $slot=$slots[$id]
            $checkpoint=Read-State (Join-Path $slot.Root 'location-render-status.json')
            $stage='starting'
            $activity=[datetimeoffset]::Parse([string]$slot.StartedUtc).UtcDateTime
            if ($null -ne $checkpoint -and $null -ne $checkpoint.Current -and $checkpoint.Current.RenderId -eq $slot.Job.RenderId) {
                $stage=[string]$checkpoint.Stage
                $activity=[datetimeoffset]::Parse([string]$checkpoint.UpdatedUtc).UtcDateTime
            }
            foreach($logPath in @($slot.Stdout,$slot.Stderr)) {
                $log=Get-Item -LiteralPath $logPath -ErrorAction SilentlyContinue
                if ($null -ne $log -and $log.LastWriteTimeUtc -gt $activity) { $activity=$log.LastWriteTimeUtc }
            }
            [pscustomobject]@{WorkerId=$id;State=$(if(($now-$activity).TotalMinutes -gt 30){'stale'}else{'running'});
                Stage=$stage;Current=$slot.Job;StartedUtc=$slot.StartedUtc;UpdatedUtc=$activity.ToUniversalTime().ToString('o')}
        })
        Write-State ([ordered]@{
            SchemaVersion=2;Coordinated=$true;State=$(if($draining){'draining'}else{'running'});
            StartedUtc=$started.ToString('o');UpdatedUtc=$now.ToString('o');Completed=$complete.Count;Total=$jobs.Count;
            Percent=$(if($jobs.Count){[math]::Round(100*$complete.Count/$jobs.Count,1)}else{0});
            Current=$null;Workers=$workers;WorkerCount=$WorkerCount;Results=@($failures.Values);
            OutputRoot=$OutputRoot;OutputQuotaBytes=500GB;StageBudgetBytes=32GB;Message='Three isolated worker slots; 32 GiB stage admission budget, bounded retries, and coordinated publication.'
        }) $statusPath
        if ($draining -and $slots.Count -eq 0) { break }
        Start-Sleep -Seconds 5
    }
} finally {
    # Do not leave an orphan able to publish after another coordinator acquires
    # the global lock. Preserve scratch; watchdog cleans only owned containers.
    foreach ($slot in @($slots.Values)) {
        if (-not $slot.Process.HasExited) {
            Stop-Process -Id $slot.Process.Id -ErrorAction SilentlyContinue
            Wait-Process -Id $slot.Process.Id -Timeout 15 -ErrorAction SilentlyContinue
        }
    }
    if ($lock) { $lock.Dispose() }
}
