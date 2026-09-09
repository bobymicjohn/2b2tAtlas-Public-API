[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSScriptRoot
$probeScript = Join-Path $scriptRoot 'get-archive-recovery-status.ps1'
$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
$fixtureRoot = Join-Path $tempBase ('atlas-recovery-probe-' + [guid]::NewGuid().ToString('N'))
$lockJob = $null

function Write-Utf8Json([object]$Value, [string]$Path) {
    $parent = Split-Path -Parent $Path
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $utf8 = New-Object Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), $utf8)
}

function New-Log([string]$Path, [string]$Text, [datetime]$CreatedUtc) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
    $item = Get-Item -LiteralPath $Path
    $item.CreationTimeUtc = $CreatedUtc
    $item.LastWriteTimeUtc = $CreatedUtc
}

try {
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $runRoot = Join-Path $fixtureRoot 'run'
    $primary = Join-Path $fixtureRoot 'primary'
    $additional = Join-Path $fixtureRoot 'additional'
    $compatibility = Join-Path $fixtureRoot 'compatibility'
    $profiles = @('one', 'two', 'three', 'four', 'five')
    $workers = @()
    $now = [DateTime]::UtcNow

    for ($id = 1; $id -le 5; $id++) {
        $wrapperRoot = Join-Path $runRoot "worker-$id"
        $stdout = Join-Path $wrapperRoot 'current.out.log'
        $stderr = Join-Path $wrapperRoot 'current.err.log'
        New-Log -Path $stdout -Text '' -CreatedUtc $now.AddSeconds(-30)
        New-Log -Path $stderr -Text '' -CreatedUtc $now.AddSeconds(-30)
        $workers += [pscustomobject][ordered]@{
            id = $id
            profile = $profiles[$id - 1]
            minecraftVersion = 'fixture-version'
            running = $true
            restarting = $false
            restartCount = 1
            completed = 0
            stdout = $stdout
            stderr = $stderr
        }
    }

    Write-Utf8Json -Value ([ordered]@{
        stage = 'capturing-parallel'
        updatedUtc = $now.ToString('o')
        workers = $workers
    }) -Path (Join-Path $runRoot 'parallel-collector-status.json')

    $primaryLogs = Join-Path $primary 'logs'
    $twoLogs = Join-Path (Join-Path $additional 'two') 'logs'
    $threeLogs = Join-Path (Join-Path $additional 'three') 'logs'
    $fourLogs = Join-Path (Join-Path $additional 'four') 'logs'
    $fiveLogs = Join-Path (Join-Path $additional 'five') 'logs'
    $compatLogs = Join-Path $compatibility 'logs'

    New-Log (Join-Path $primaryLogs 'collector-init.log') "ATLAS_COVER initialized version=fixture`nClient disconnected with reason: Unable to connect to archive:" $now
    New-Log (Join-Path $twoLogs 'collector-live.log') 'ATLAS_COVER arrived waypoint=1/1 received=10 missing=0' $now
    New-Log (Join-Path $threeLogs 'collector-stopped.log') "ATLAS_COVER settled waypoint=1/1 received=10 missing=0`nClient disconnected with reason: Disconnected" $now
    $workers[2].running = $false
    $workers[2].restarting = $true
    New-Log (Join-Path $fourLogs 'collector-stale.log') 'ATLAS_COVER teleport-start waypoint=1/1' $now.AddMinutes(-2)
    New-Log (Join-Path $fiveLogs 'collector-primary.log') 'ATLAS_COVER initialized version=fixture' $now.AddSeconds(-5)
    New-Log (Join-Path $compatLogs 'collector-compat.log') 'ATLAS_COVER arrived waypoint=1/1 received=20 missing=0' $now
    Write-Utf8Json -Value ([ordered]@{stage='capturing-parallel';updatedUtc=$now.ToString('o');workers=$workers}) `
        -Path (Join-Path $runRoot 'parallel-collector-status.json')

    $first = & $probeScript -RunRoot $runRoot -PrimaryInstallRoot $primary `
        -AdditionalInstallRoot $additional -CompatibilityInstallRoot $compatibility
    if ($first.liveCoverageWorkers -ne 2 -or $first.allFiveOperational) {
        throw "Expected two live workers and a closed gate; got $($first.liveCoverageWorkers)."
    }
    if (($first.workers | Where-Object id -eq 1).liveCoverage -or
        ($first.workers | Where-Object id -eq 3).liveCoverage -or
        ($first.workers | Where-Object id -eq 4).liveCoverage) {
        throw 'Initialization-only, stopped, or stale telemetry incorrectly passed the recovery gate.'
    }
    if (($first.workers | Where-Object id -eq 1).outcome -ne 'archive-backend-rejected') {
        throw 'The normalized Archive backend rejection outcome was not detected.'
    }
    if (($first.workers | Where-Object id -eq 3).outcome -ne 'server-disconnected') {
        throw 'The normalized generic server disconnect outcome was not detected.'
    }
    if (-not ($first.workers | Where-Object id -eq 5).liveCoverage) {
        throw 'Worker 5 compatibility telemetry was not discovered.'
    }

    $workers[2].running = $true
    $workers[2].restarting = $false
    foreach ($pair in @(
        @{ Root=$primaryLogs; Name='collector-live-2.log' },
        @{ Root=$threeLogs; Name='collector-live-2.log' },
        @{ Root=$fourLogs; Name='collector-live-2.log' }
    )) {
        New-Log (Join-Path $pair.Root $pair.Name) 'ATLAS_COVER settled waypoint=1/1 received=30 missing=0' $now.AddSeconds(10)
    }
    Write-Utf8Json -Value ([ordered]@{stage='capturing-parallel';updatedUtc=$now.AddSeconds(10).ToString('o');workers=$workers}) `
        -Path (Join-Path $runRoot 'parallel-collector-status.json')
    $second = & $probeScript -RunRoot $runRoot -PrimaryInstallRoot $primary `
        -AdditionalInstallRoot $additional -CompatibilityInstallRoot $compatibility
    if ($second.liveCoverageWorkers -ne 5 -or -not $second.allFiveOperational) {
        throw "Expected the five-worker recovery gate to open; got $($second.liveCoverageWorkers)."
    }

    $statusPath = Join-Path $runRoot 'parallel-collector-status.json'
    $lockMarker = Join-Path $fixtureRoot 'status-lock-ready'
    $lockJob = Start-Job -ScriptBlock {
        param([string]$LockedPath, [string]$MarkerPath)
        $stream = $null
        try {
            $stream = [IO.File]::Open($LockedPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            [IO.File]::WriteAllText($MarkerPath, 'ready')
            Start-Sleep -Milliseconds 600
        } finally {
            if ($stream) { $stream.Dispose() }
        }
    } -ArgumentList $statusPath, $lockMarker
    $lockDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $lockMarker -PathType Leaf) -and [DateTime]::UtcNow -lt $lockDeadline) {
        Start-Sleep -Milliseconds 25
    }
    if (-not (Test-Path -LiteralPath $lockMarker -PathType Leaf)) {
        throw 'The transient status-lock fixture did not become ready.'
    }
    $lockedRead = & $probeScript -RunRoot $runRoot -PrimaryInstallRoot $primary `
        -AdditionalInstallRoot $additional -CompatibilityInstallRoot $compatibility
    if ($lockedRead.liveCoverageWorkers -ne 5 -or -not $lockedRead.allFiveOperational) {
        throw 'A transient status writer lock prevented a stable recovery read.'
    }
    Wait-Job -Job $lockJob | Out-Null
    Receive-Job -Job $lockJob -ErrorAction Stop | Out-Null
    Remove-Job -Job $lockJob -Force
    $lockJob = $null

    'Archive recovery status fixture passed.'
} finally {
    if ($lockJob) {
        Stop-Job -Job $lockJob -ErrorAction SilentlyContinue
        Remove-Job -Job $lockJob -Force -ErrorAction SilentlyContinue
    }
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if ($resolvedFixture.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedFixture -PathType Container)) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
