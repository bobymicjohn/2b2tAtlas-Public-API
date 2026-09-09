$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
. (Join-Path $scriptsRoot 'archive-json-io.ps1')
. (Join-Path $scriptsRoot 'archive-fast-lane-routing.ps1')

$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('atlas-fast-lane-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $fastRoot = Join-Path $testRoot 'worker-5-fast'
    $longRoot = Join-Path $testRoot 'worker-1-long'
    New-Item -ItemType Directory -Path $fastRoot, $longRoot -Force | Out-Null
    $deferred = [pscustomobject]@{ warp = 'Huge_Base'; normalizedWarp = 'huge_base'; source = 'fixture' }
    $ordinary = [pscustomobject]@{ warp = 'Ordinary_Base'; normalizedWarp = 'ordinary_base'; source = 'fixture' }
    $queueTemplate = [ordered]@{ schemaVersion = 1; queueCount = 0; conflicts = @(); entries = @() }
    $fastQueue = [pscustomobject](($queueTemplate | ConvertTo-Json -Depth 5) | ConvertFrom-Json)
    $fastQueue.entries = @($deferred); $fastQueue.queueCount = 1
    $longQueue = [pscustomobject](($queueTemplate | ConvertTo-Json -Depth 5) | ConvertFrom-Json)
    $longQueue.entries = @($ordinary); $longQueue.queueCount = 1
    Write-AtlasJsonAtomically $fastQueue (Join-Path $fastRoot 'queue.json') 10
    Write-AtlasJsonAtomically $longQueue (Join-Path $longRoot 'queue.json') 10
    Write-AtlasJsonAtomically ([ordered]@{ entries = @([ordered]@{
        warp = 'Huge_Base'; normalizedWarp = 'huge_base'; status = 'retryable'
        error = 'Adaptive maximum runtime of 1800 second(s) reached while discovering Huge_Base.'
    }) }) (Join-Path $fastRoot 'state.json') 10
    Write-AtlasJsonAtomically ([ordered]@{ entries = @() }) (Join-Path $longRoot 'state.json') 10

    $long = [pscustomobject]@{
        id = 1; queuePath = (Join-Path $longRoot 'queue.json'); statePath = (Join-Path $longRoot 'state.json')
        exitSignalPath = (Join-Path $longRoot 'reload.signal'); identities = @{ ordinary_base = $true }
        assigned = 1; completed = 0
    }
    $fast = [pscustomobject]@{
        id = 5; queuePath = (Join-Path $fastRoot 'queue.json'); statePath = (Join-Path $fastRoot 'state.json')
        exitSignalPath = (Join-Path $fastRoot 'reload.signal'); identities = @{ huge_base = $true }
        assigned = 1; completed = 0
    }
    $workers = @($long, $fast)
    $ledgerPath = Join-Path $testRoot 'fast-lane-deferred.json'
    $ledger = Move-ArchiveFastLaneTimeouts $workers @(5) $ledgerPath
    if (@($ledger.entries).Count -ne 1) { throw 'Expected exactly one durable route.' }
    $fastAfter = Read-AtlasJsonWithRetry $fast.queuePath
    $longAfter = Read-AtlasJsonWithRetry $long.queuePath
    if (@($fastAfter.entries).Count -ne 0) { throw 'Fast worker retained deferred ownership.' }
    if (@($longAfter.entries).Count -ne 2 -or [string]$longAfter.entries[0].normalizedWarp -ne 'huge_base') {
        throw 'Long worker did not receive the deferred WDL at queue priority.'
    }
    if (-not (Test-Path -LiteralPath $long.exitSignalPath -PathType Leaf)) { throw 'Safe queue-reload signal was not created.' }

    $ledger = Move-ArchiveFastLaneTimeouts $workers @(5) $ledgerPath
    $longAfter = Read-AtlasJsonWithRetry $long.queuePath
    if (@($ledger.entries).Count -ne 1 -or @($longAfter.entries | Where-Object normalizedWarp -eq 'huge_base').Count -ne 1) {
        throw 'Repeated routing was not idempotent.'
    }

    # Rehearse a crash after intent persistence but before source removal.
    $fastAfter.entries = @($deferred)
    Write-AtlasJsonAtomically $fastAfter $fast.queuePath 10
    Sync-ArchiveFastLaneRouting $workers $ledgerPath | Out-Null
    $fastAfter = Read-AtlasJsonWithRetry $fast.queuePath
    if (@($fastAfter.entries).Count -ne 0) { throw 'Ledger reconciliation failed to repair duplicate ownership.' }

    Write-Output 'PASS archive fast-lane durable routing and reconciliation'
} finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
