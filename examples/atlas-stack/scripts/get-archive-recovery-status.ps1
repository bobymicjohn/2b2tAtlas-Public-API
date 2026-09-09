[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',

    [string]$PrimaryInstallRoot = 'C:\AtlasExample\Ingest\archive-sync\collector',

    [string]$AdditionalInstallRoot = 'C:\AtlasExample\Ingest\archive-sync\collectors',

    [string]$CompatibilityInstallRoot = 'D:\AtlasExample\Ingest\archive-compat\collector-1.21.10',

    [ValidateRange(100, 20000)]
    [int]$LogTailLines = 3000,

    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$statusPath = Join-Path ([IO.Path]::GetFullPath($RunRoot)) 'parallel-collector-status.json'
if (-not (Test-Path -LiteralPath $statusPath -PathType Leaf)) {
    throw "Parallel collector status was not found: $statusPath"
}

function Read-JsonWithRetry([string]$Path, [int]$Attempts = 20, [int]$DelayMilliseconds = 100) {
    $lastError = $null
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            # The collector rewrites this small status document frequently. A
            # writer may hold an exclusive handle for a few milliseconds, or
            # a reader may catch an incomplete in-place write. Retry either
            # condition instead of terminating the watchdog's recovery probe.
            return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json)
        } catch {
            $lastError = $_
            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds $DelayMilliseconds
            }
        }
    }

    throw "Unable to read a stable collector status after $Attempts attempts: $($lastError.Exception.Message)"
}

$status = Read-JsonWithRetry -Path $statusPath
$workerResults = @()

foreach ($worker in @($status.workers)) {
    # Wrap the complete conditional in @(). PowerShell otherwise unwraps a
    # one-item branch to a string, and += would concatenate worker 5's
    # compatibility root instead of appending a second path.
    $installRoots = @(if ([int]$worker.id -eq 1) {
        [IO.Path]::GetFullPath($PrimaryInstallRoot)
    } else {
        Join-Path ([IO.Path]::GetFullPath($AdditionalInstallRoot)) ([string]$worker.profile)
    })
    if ([int]$worker.id -eq 5 -and
        -not [string]::IsNullOrWhiteSpace($CompatibilityInstallRoot) -and
        (Test-Path -LiteralPath $CompatibilityInstallRoot -PathType Container)) {
        $installRoots += [IO.Path]::GetFullPath($CompatibilityInstallRoot)
    }

    $wrapperCreatedUtc = [datetime]::MinValue
    foreach ($wrapperLog in @([string]$worker.stdout, [string]$worker.stderr)) {
        if (-not [string]::IsNullOrWhiteSpace($wrapperLog) -and
            (Test-Path -LiteralPath $wrapperLog -PathType Leaf)) {
            $created = (Get-Item -LiteralPath $wrapperLog).CreationTimeUtc
            if ($created -gt $wrapperCreatedUtc) { $wrapperCreatedUtc = $created }
        }
    }

    $latestLog = $null
    foreach ($installRoot in $installRoots) {
        $logRoot = Join-Path $installRoot 'logs'
        if (-not (Test-Path -LiteralPath $logRoot -PathType Container)) { continue }
        $candidate = Get-ChildItem -LiteralPath $logRoot -File -Filter 'collector-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($candidate -and ($null -eq $latestLog -or $candidate.LastWriteTimeUtc -gt $latestLog.LastWriteTimeUtc)) {
            $latestLog = $candidate
        }
    }

    $currentLaunch = $false
    $initialized = $false
    $operationalTelemetry = $false
    $backendRejected = $false
    $serverDisconnected = $false
    $lastEvent = $null
    if ($latestLog) {
        # Minecraft creates its collector log shortly after the wrapper redirects
        # stdout/stderr. The tolerance covers filesystem timestamp granularity.
        $currentLaunch = $wrapperCreatedUtc -eq [datetime]::MinValue -or
            $latestLog.CreationTimeUtc -ge $wrapperCreatedUtc.AddSeconds(-10)
        $tail = (Get-Content -LiteralPath $latestLog.FullName -Tail $LogTailLines) -join "`n"
        $initialized = [bool]($tail -match 'ATLAS_COVER initialized\b')
        $backendRejected = [bool]($tail -match 'Unable to connect to archive')
        $serverDisconnected = [bool]($tail -match 'Client disconnected with reason:\s*Disconnected\b')
        $events = @([regex]::Matches($tail, 'ATLAS_COVER (?<event>[a-z][a-z0-9-]*)'))
        $operationalEvents = @($events | Where-Object { $_.Groups['event'].Value -ne 'initialized' })
        if ($operationalEvents.Count -gt 0) {
            $operationalTelemetry = $true
            $lastEvent = $operationalEvents[-1].Groups['event'].Value
        }
    }

    # A historical successful log must never make a backoff/restart state look
    # recovered. All three predicates are required for current live coverage.
    $liveCoverage = [bool]$worker.running -and $currentLaunch -and $operationalTelemetry
    $outcome = if ($liveCoverage) {
        'live'
    } elseif ($currentLaunch -and $backendRejected) {
        'archive-backend-rejected'
    } elseif ($currentLaunch -and $serverDisconnected) {
        'server-disconnected'
    } elseif ([bool]$worker.running) {
        'connecting'
    } elseif ([bool]$worker.restarting) {
        'backoff'
    } else {
        'idle'
    }
    $workerResults += [pscustomobject][ordered]@{
        id = [int]$worker.id
        profile = [string]$worker.profile
        minecraftVersion = [string]$worker.minecraftVersion
        running = [bool]$worker.running
        restarting = [bool]$worker.restarting
        restartCount = [int]$worker.restartCount
        completed = [int]$worker.completed
        logName = if ($latestLog) { $latestLog.Name } else { $null }
        logUpdatedUtc = if ($latestLog) { $latestLog.LastWriteTimeUtc.ToString('o') } else { $null }
        currentLaunch = $currentLaunch
        initialized = $initialized
        backendRejected = $backendRejected
        serverDisconnected = $serverDisconnected
        operationalTelemetry = $operationalTelemetry
        lastOperationalEvent = $lastEvent
        liveCoverage = $liveCoverage
        outcome = $outcome
    }
}

$liveCount = @($workerResults | Where-Object { $_.liveCoverage }).Count
$result = [pscustomobject][ordered]@{
    schemaVersion = 1
    checkedUtc = [DateTime]::UtcNow.ToString('o')
    statusUpdatedUtc = [string]$status.updatedUtc
    workerCount = $workerResults.Count
    liveCoverageWorkers = $liveCount
    allWorkersOperational = $workerResults.Count -gt 0 -and $liveCount -eq $workerResults.Count
    # Retained for older consumers; true whenever at least the original five
    # lanes are operational.
    allFiveOperational = $workerResults.Count -ge 5 -and $liveCount -ge 5
    workers = $workerResults
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 5
} else {
    $result
}
