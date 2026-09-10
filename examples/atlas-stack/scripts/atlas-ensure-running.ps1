<#
    2b2t Atlas watchdog — self-heals the example host API and WDL worker.

    The API/worker scheduled tasks are fire-and-forget (their VBS launcher returns
    immediately), so Task Scheduler's restart-on-failure never sees the detached
    process die. This watchdog runs every few minutes and relaunches whichever
    process is down. It is idempotent: it only launches a service when its process
    is absent, so it never spawns duplicates (which would collide on the API's
    loopback port or double-claim worker jobs).

    Registered by register-atlas-watchdog.ps1 as the "Atlas Example Watchdog" task.
#>
[CmdletBinding()]
param()

$logDir = 'C:\AtlasExample\Ops\logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
$log = Join-Path $logDir ('atlas-watchdog-{0}.log' -f (Get-Date -Format 'yyyyMMdd'))

function Write-Log([string]$msg) {
    $entry = ('{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg)
    for ($i = 0; $i -lt 5; $i++) {
        try { Add-Content -Path $log -Value $entry -ErrorAction Stop; break }
        catch { Start-Sleep -Milliseconds (50 * ($i + 1)) }
    }
}

function Test-ApiHealthy {
    if (-not (Get-Process '2b2tAtlas.Server' -ErrorAction SilentlyContinue)) { return $false }
    try {
        $r = Invoke-WebRequest 'http://127.0.0.1:5297/api/locations' -TimeoutSec 8 -UseBasicParsing
        return $r.StatusCode -eq 200
    } catch { return $false }
}

# --- API: relaunch if the process is absent. (A running-but-unresponsive API is
#     logged but NOT force-killed here, to avoid killing a slow-starting instance.) ---
$apiProc = Get-Process '2b2tAtlas.Server' -ErrorAction SilentlyContinue
if (-not $apiProc) {
    Write-Log 'API process absent -> launching start-atlas-api.vbs'
    Start-Process 'wscript.exe' -ArgumentList '"C:\AtlasExample\Api\start-atlas-api.vbs"'
} elseif (-not (Test-ApiHealthy)) {
    Write-Log ('API process alive (PID {0}) but loopback probe failed (may be starting or hung)' -f $apiProc.Id)
}

# --- Worker: relaunch if the process is absent. ---
if (-not (Test-Path -LiteralPath 'C:\AtlasExample\Ingest\pause-worker') -and
    -not (Get-Process '2b2tAtlas.Ingestor' -ErrorAction SilentlyContinue)) {
    Write-Log 'Worker process absent -> launching start-atlas-worker.ps1 hidden'
    Start-Process 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass',
        '-File', 'C:\AtlasExample\Ingest\start-atlas-worker.ps1')
}

# --- BlueMap derivatives: this is a downstream, polling consumer of completed
#     source-backed renders. It never owns collector or uNmINeD ingestion work.
#     One coordinator manages three memory-gated children. Exclusive locks and
#     immutable manifests prevent duplicate work/publication during recovery.
$blueMapRepo = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack'
$blueMapRenderer = Join-Path $blueMapRepo 'scripts\invoke-atlas-bluemap-render.ps1'
$blueMapCoordinator = Join-Path $blueMapRepo 'scripts\start-atlas-bluemap-coordinator.ps1'
$blueMapStatusPath = 'C:\AtlasExample\Ingest\bluemap\location-render-status.json'
try {
    if (Test-Path -LiteralPath $blueMapRenderer -PathType Leaf) {
        # A killed parent shell does not necessarily stop an already-created
        # Docker container. Reap only Atlas BlueMap containers whose PID-coded
        # owner no longer exists as a matching batch/renderer process.
        $blueMapContainers = @(& docker ps --format '{{.Names}}' 2>$null | Where-Object {
            $_ -match '^atlas-bluemap-(?:relight|render)-\d+-(\d+)-[a-f0-9]+$'
        })
        foreach ($containerName in $blueMapContainers) {
            if ($containerName -notmatch '^atlas-bluemap-(?:relight|render)-\d+-(\d+)-[a-f0-9]+$') { continue }
            $ownerPid = [int]$Matches[1]
            $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$ownerPid" -ErrorAction SilentlyContinue
            $ownerMatches = $null -ne $owner -and
                -not [string]::IsNullOrWhiteSpace([string]$owner.CommandLine) -and
                [string]$owner.CommandLine -match '(?i)(invoke-atlas-bluemap-render|start-atlas-bluemap-full-batch)\.ps1'
            if (-not $ownerMatches) {
                Write-Log ("Stopping orphaned BlueMap container {0}; encoded owner PID {1} is absent or unrelated" -f $containerName, $ownerPid)
                & docker stop --timeout 60 $containerName *> $null
                & docker rm -f $containerName *> $null
            }
        }

        # Coordinated children may outlive an abruptly killed coordinator. Stop
        # only explicitly identified orphan children before replacing the owner.
        $children = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
            $_.CommandLine -match 'invoke-atlas-bluemap-render\.ps1' -and $_.CommandLine -match '-CoordinatorPid\s+\d+'
        })
        foreach ($child in $children) {
            if ($child.CommandLine -match '-CoordinatorPid\s+(\d+)') {
                $coordinator = Get-CimInstance Win32_Process -Filter "ProcessId=$($Matches[1])" -ErrorAction SilentlyContinue
                if ($null -eq $coordinator -or $coordinator.CommandLine -notmatch 'start-atlas-bluemap-coordinator\.ps1') {
                    Stop-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                    $ownedContainers = @(& docker ps --format '{{.Names}}' | Where-Object {
                        $_ -match ('^atlas-bluemap-(?:relight|render)-\d+-' + $child.ProcessId + '-[a-f0-9]+$')
                    })
                    foreach ($ownedContainer in $ownedContainers) { & docker rm -f $ownedContainer *> $null }
                    Write-Log "Stopped orphan BlueMap worker $($child.ProcessId)."
                }
            }
        }
        # Reclaim only unmounted per-job web volumes with a dead/unrelated
        # encoded owner. Never prune shared caches or an active job's volume.
        $orphanVolumes = @(& docker volume ls --filter dangling=true --filter name=atlas-bluemap-web- --format '{{.Name}}')
        foreach ($volume in $orphanVolumes) {
            if ($volume -notmatch '^atlas-bluemap-web-\d+-(\d+)-[a-f0-9]+$') { continue }
            $volumeOwner = Get-CimInstance Win32_Process -Filter "ProcessId=$($Matches[1])" -ErrorAction SilentlyContinue
            if ($null -eq $volumeOwner -or $volumeOwner.CommandLine -notmatch '(invoke-atlas-bluemap-render|start-atlas-bluemap-full-batch)\.ps1') {
                & docker volume rm $volume *> $null
                if ($LASTEXITCODE -eq 0) { Write-Log "Reclaimed orphan BlueMap temporary web volume $volume." }
            }
        }
        $blueMapProcesses = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
            -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
            [string]$_.CommandLine -match '(?i)(invoke-atlas-bluemap-render|start-atlas-bluemap-full-batch|start-atlas-bluemap-coordinator)\.ps1'
        })
        $blueMapDue = $true
        if (Test-Path -LiteralPath $blueMapStatusPath -PathType Leaf) {
            $blueMapStatus = Get-Content -LiteralPath $blueMapStatusPath -Raw | ConvertFrom-Json
            if ($null -ne $blueMapStatus.PSObject.Properties['UpdatedUtc']) {
                $lastBlueMapUpdate = [DateTime]$blueMapStatus.UpdatedUtc
                $blueMapDue = ([DateTime]::UtcNow - $lastBlueMapUpdate.ToUniversalTime()).TotalMinutes -ge 15
                if ($blueMapStatus.SchemaVersion -ge 2) { $blueMapDue = $true }
            }
        }
        if ($blueMapProcesses.Count -eq 0 -and $blueMapDue -and
            -not (Test-Path -LiteralPath 'C:\AtlasExample\Ingest\bluemap\pause-coordinator')) {
            & docker info --format '{{.ServerVersion}}' *> $null
            if ($LASTEXITCODE -eq 0) {
                $blueMapStateRoot = Split-Path -Parent $blueMapStatusPath
                New-Item -ItemType Directory -Path $blueMapStateRoot -Force | Out-Null
                $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
                $stdout = Join-Path $blueMapStateRoot "full-batch-watchdog-$stamp.out.log"
                $stderr = Join-Path $blueMapStateRoot "full-batch-watchdog-$stamp.err.log"
                Write-Log 'BlueMap coordinator absent and due -> resuming three memory-gated workers for missing source-backed profiles'
                Start-Process 'powershell.exe' -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -ArgumentList @(
                    '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
                    '-File', $blueMapCoordinator) | Out-Null
            } else {
                Write-Log 'BlueMap derivative batch is due, but Docker is unavailable'
            }
        }
    }
} catch {
    Write-Log ("BlueMap derivative watchdog check failed: {0}" -f $_.Exception.Message)
}

function Get-PushoverCredentials {
    $credentials = @{ Token = $null; User = $null }
    $envPath = 'C:\AtlasExample\Ops\blackbrain\.env'
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) { return $credentials }
    foreach ($line in Get-Content -LiteralPath $envPath) {
        if ($line -match '^\s*PUSHOVER_APP_TOKEN\s*=\s*(.+?)\s*$') {
            $credentials.Token = $Matches[1].Trim().Trim('"').Trim("'")
        } elseif ($line -match '^\s*PUSHOVER_USER_KEY\s*=\s*(.+?)\s*$') {
            $credentials.User = $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $credentials
}

function Send-ArchiveRecoveredPush([int]$CapturedCount) {
    $credentials = Get-PushoverCredentials
    if (-not $credentials.Token -or -not $credentials.User) {
        Write-Log 'Archive recovery reached all workers, but Pushover credentials are unavailable'
        return $false
    }
    try {
        $body = @{
            token = $credentials.Token
            user = $credentials.User
            title = '2b2t Atlas collector recovered'
            message = "All six Archive collectors joined and emitted current-launch ATLAS_COVER telemetry. Saved captures: $CapturedCount."
            priority = 0
            sound = 'pushover'
            url = 'https://atlas.example/admin'
            url_title = 'Open Atlas admin'
        }
        $null = Invoke-RestMethod -Uri 'https://api.pushover.net/1/messages.json' -Method Post -Body $body -TimeoutSec 20
        Write-Log 'Archive recovery reached verified 6/6 operational telemetry; Pushover notification sent'
        return $true
    } catch {
        Write-Log ("Archive recovery reached 6/6, but Pushover notification failed: {0}" -f $_.Exception.Message)
        return $false
    }
}

# --- Archive initial pass: resume the six-worker supervisor and rolling D -> E ->
#     production handoff after a host restart. A completed pass is left alone; the
#     separately scheduled weekly sync owns future catalog checks. ---
$archiveRunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog'
$archiveParallelRoot = Join-Path $archiveRunRoot 'parallel-runs\20260901-061410'
$archiveRepo = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack'
$archiveStatusPath = Join-Path $archiveRunRoot 'parallel-collector-status.json'

if (-not (Test-Path -LiteralPath 'C:\AtlasExample\Ingest\pause-collector') -and (Test-Path -LiteralPath $archiveStatusPath -PathType Leaf)) {
    try {
        $archiveStatus = Get-Content -LiteralPath $archiveStatusPath -Raw | ConvertFrom-Json
        if ($null -ne $archiveStatus.PSObject.Properties['runRoot'] -and
            -not [string]::IsNullOrWhiteSpace([string]$archiveStatus.runRoot)) {
            $candidateParallelRoot = [IO.Path]::GetFullPath([string]$archiveStatus.runRoot)
            if ($candidateParallelRoot.StartsWith($archiveRunRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
                $archiveParallelRoot = $candidateParallelRoot
            }
        }
        if ([string]$archiveStatus.stage -ne 'parallel-finished') {
            $powerShellProcesses = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'")
            $supervisor = @($powerShellProcesses | Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
                [string]$_.CommandLine -match '(?i)resume-archive-parallel-supervisor\.ps1' -and
                ([string]$_.CommandLine).IndexOf($archiveRunRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
            })
            if ($supervisor.Count -eq 0) {
                $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
                $stdout = Join-Path $archiveParallelRoot "supervisor-watchdog-$stamp.out.log"
                $stderr = Join-Path $archiveParallelRoot "supervisor-watchdog-$stamp.err.log"
                Write-Log 'Archive supervisor absent during active initial pass -> resuming six workers'
                $supervisorArguments = @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    (Join-Path $archiveRepo 'scripts\resume-archive-parallel-supervisor.ps1'),
                    '-RunRoot', $archiveRunRoot, '-ParallelRunRoot', $archiveParallelRoot,
                    '-MaxWorkerRestarts', '10000', '-WorkerRestartDelaySeconds', '300',
                    '-WorkerStartDelaySeconds', '15', '-AdaptiveBackgroundRadiusBlocks', '0',
                    '-FastLaneWorkerId', '5', '-SecondFastLaneWorkerId', '6',
                    '-FastLaneAdaptiveMaximumRuntimeSeconds', '1800')
                # All six workers use their own authenticated 1.21.11 install. The old
                # 1.21.10 canary root is authenticated as atlas-owner and must never be
                # alternated into worker 5, where it would evict worker 1's session.
                Start-Process 'powershell.exe' -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr `
                    -ArgumentList $supervisorArguments | Out-Null
            }

            $finalizer = @($powerShellProcesses | Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
                [string]$_.CommandLine -match '(?i)wait-and-finalize-archive-parallel\.ps1' -and
                ([string]$_.CommandLine).IndexOf($archiveRunRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
            })
            if ($finalizer.Count -eq 0) {
                $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
                $stdout = Join-Path $archiveRunRoot "finalizer-watchdog-$stamp.out.log"
                $stderr = Join-Path $archiveRunRoot "finalizer-watchdog-$stamp.err.log"
                Write-Log 'Archive production handoff absent during active initial pass -> resuming rolling handoff'
                Start-Process 'powershell.exe' -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -ArgumentList @(
                    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                    (Join-Path $archiveRepo 'scripts\wait-and-finalize-archive-parallel.ps1'),
                    '-RunRoot', $archiveRunRoot, '-RepositoryRoot', $archiveRepo, '-PollSeconds', '60') | Out-Null
            }

            # Notify once when all current launches have progressed beyond
            # mod initialization into real Archive coverage telemetry. The probe
            # is read-only and rejects stale logs, stopped workers, and the
            # ATLAS_COVER initialization line.
            $recoveryProbe = Join-Path $archiveRepo 'scripts\get-archive-recovery-status.ps1'
            $recoveryMarker = 'C:\AtlasExample\Ops\state\atlas-archive-recovery-notified.json'
            if ((Test-Path -LiteralPath $recoveryProbe -PathType Leaf) -and
                -not (Test-Path -LiteralPath $recoveryMarker -PathType Leaf)) {
                $recovery = & $recoveryProbe -RunRoot $archiveRunRoot
                if ([bool]$recovery.allWorkersOperational) {
                    $collectorState = Get-Content -LiteralPath (Join-Path $archiveRunRoot 'collector-state.json') -Raw | ConvertFrom-Json
                    $capturedCount = @($collectorState.entries | Where-Object status -eq 'captured').Count
                    if (Send-ArchiveRecoveredPush -CapturedCount $capturedCount) {
                        $markerRoot = Split-Path -Parent $recoveryMarker
                        if (-not (Test-Path -LiteralPath $markerRoot -PathType Container)) {
                            New-Item -ItemType Directory -Path $markerRoot -Force | Out-Null
                        }
                        $marker = [ordered]@{
                            schemaVersion = 1
                            recoveredUtc = [DateTime]::UtcNow.ToString('o')
                            liveCoverageWorkers = [int]$recovery.liveCoverageWorkers
                            capturedCount = $capturedCount
                        }
                        [IO.File]::WriteAllText($recoveryMarker, ($marker | ConvertTo-Json -Depth 3), (New-Object Text.UTF8Encoding($false)))
                    }
                }
            }
        }
    } catch {
        Write-Log ("Archive pipeline watchdog check failed: {0}" -f $_.Exception.Message)
    }
}
