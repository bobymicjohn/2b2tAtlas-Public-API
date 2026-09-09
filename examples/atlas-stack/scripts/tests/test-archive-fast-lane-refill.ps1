$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$scriptsRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
. (Join-Path $scriptsRoot 'archive-json-io.ps1')
. (Join-Path $scriptsRoot 'archive-fast-lane-routing.ps1')
. (Join-Path $scriptsRoot 'archive-fast-lane-refill.ps1')
. (Join-Path $scriptsRoot 'archive-collector-lane-policy.ps1')
function Test-FinalAdaptiveCapture($entry) { return $entry.status -eq 'captured' -and $entry.adaptive.standardVersion -ge 2 }
$testRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('atlas-refill-' + [guid]::NewGuid().ToString('N'))))
try {
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $workers = @()
    foreach ($id in @(1, 5)) {
        $dir = Join-Path $testRoot "worker-$id"
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $workers += [pscustomobject]@{
            id = $id; processId = if ($id -eq 1) { $PID } else { 0 }
            queuePath = Join-Path $dir 'queue.json'; statePath = Join-Path $dir 'state.json'
            exitSignalPath = Join-Path $dir 'reload.signal'; identities = @{}
            assigned = 0; completed = 0; restartAfterUtc = $null
        }
    }
    $source = $workers[0]; $target = $workers[1]
    $entries = @(1..8 | ForEach-Object { [pscustomobject]@{ warp = "Base_$_"; normalizedWarp = "base_$_" } })
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 8; entries = $entries }) $source.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 0; entries = @() }) $target.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @() }) $source.statePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @() }) $target.statePath 10
    $deferred = [pscustomobject]@{ entries = @([pscustomobject]@{ normalizedWarp = 'base_8'; targetWorkerId = 1 }) }
    $ledgerPath = Join-Path $testRoot 'refill.json'
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred -BatchSize 3
    if ($ledger.batches.Count -ne 1 -or $ledger.batches[0].status -ne 'waiting-for-boundary') { throw 'Expected durable boundary request.' }
    if (-not (Test-Path -LiteralPath $source.exitSignalPath)) { throw 'Missing safe-boundary signal.' }
    if (@((Get-ArchiveWorkerQueue $source).entries).Count -ne 8 -or @((Get-ArchiveWorkerQueue $target).entries).Count -ne 0) { throw 'Live donor queue was changed.' }
    # A second poll must not steal from the live in-memory queue or duplicate intent.
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred -BatchSize 3
    if ($ledger.batches.Count -ne 1) { throw 'Duplicate pending batch.' }

    # The current capture completes during the boundary wait: exclude it using the fresh state.
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @([pscustomobject]@{
        warp = 'Base_7'; normalizedWarp = 'base_7'; status = 'captured'; adaptive = @{ standardVersion = 2 }
    }) }) $source.statePath 10
    $source.processId = 0
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred -BatchSize 3
    if ($ledger.batches[0].status -ne 'applied') { throw 'Stopped donor was not refilled.' }
    $received = @((Get-ArchiveWorkerQueue $target).entries.normalizedWarp)
    if (($received -join ',') -ne 'base_4,base_5,base_6') { throw "Completed/deferred work was requeued: $received" }
    $all = @($workers | ForEach-Object { (Get-ArchiveWorkerQueue $_).entries.normalizedWarp })
    if ($all.Count -ne 8 -or @($all | Select-Object -Unique).Count -ne 8) { throw 'Ownership lost or duplicated.' }
    if ($target.assigned -ne 3 -or $source.assigned -ne 5) { throw 'In-memory supervisor ownership was stale.' }
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred -BatchSize 3
    if ($ledger.batches.Count -ne 1) { throw 'Nonempty fast lane received another refill.' }

    # Rehearse a crash between queue writes and completion of the durable intent.
    $ledger.batches[0].status = 'committing'
    Write-AtlasJsonAtomically $ledger $ledgerPath 24
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 8; entries = $entries }) $source.queuePath 10
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred -BatchSize 3
    $all = @($workers | ForEach-Object { (Get-ArchiveWorkerQueue $_).entries.normalizedWarp })
    if ($all.Count -ne 8 -or @($all | Select-Object -Unique).Count -ne 8 -or $ledger.batches[0].status -ne 'applied') { throw 'Crash reconciliation failed.' }
    # No fresh work remains. The idle fast worker borrows one known large job,
    # even if its own checkpoint records the original 30-minute timeout.
    $large = [pscustomobject]@{ warp = 'Large Base'; normalizedWarp = 'large_base' }
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 1; entries = @($large) }) $source.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 0; entries = @() }) $target.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @() }) $source.statePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @([pscustomobject]@{
        warp = $large.warp; normalizedWarp = $large.normalizedWarp; status = 'retryable'
        error = 'Adaptive maximum runtime of 1800 second(s) reached before completion'
    }) }) $target.statePath 10
    $deferred = [pscustomobject]@{ entries = @([pscustomobject]@{ normalizedWarp = $large.normalizedWarp; sourceWorkerId = 5; targetWorkerId = 1; queueEntry = $large }); updatedUtc = '' }
    $ledgerPath = Join-Path $testRoot 'fallback.json'
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred
    if ($ledger.batches[0].mode -ne 'temporary-long') { throw 'Empty fast lane did not request long work.' }
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred
    if ($target.fallbackWarp -ne 'Large Base' -or $deferred.entries[0].targetWorkerId -ne 5) { throw 'Fallback identity/durable owner missing.' }
    $target | Add-Member lane 'throughput' -Force
    $target | Add-Member adaptiveMaximumRuntimeSeconds 1800 -Force
    $launch = @(Get-ArchiveRefillLaunchArguments $target @('-File','fixture.ps1','-AdaptiveMaximumRuntimeSeconds','1800') @(5) 1800)
    if ($target.lane -ne 'temporary-long' -or $target.adaptiveMaximumRuntimeSeconds -ne 43200 -or $launch[-2] -ne '-Warp' -or $launch[-1] -ne '"Large Base"') { throw 'Fallback launch did not select exactly one 12-hour job.' }
    # Refill while the temporary long capture is active; the donor must not be
    # left idle waiting for it. Its current budget stays long until completion.
    $target.processId = $PID
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 3; entries = @(
        [pscustomobject]@{ warp = 'Fresh1'; normalizedWarp = 'fresh1' },
        [pscustomobject]@{ warp = 'Fresh2'; normalizedWarp = 'fresh2' },
        [pscustomobject]@{ warp = 'Fresh3'; normalizedWarp = 'fresh3' }
    ) }) $source.queuePath 10
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred
    if (@((Get-ArchiveWorkerQueue $target).entries).Count -ne 4 -or $target.lane -ne 'temporary-long') { throw 'Active long capture prevented refill or its budget changed mid-capture.' }
    $target.processId = 0
    Set-ArchiveFastLaneModes $workers @(5) $ledger
    $launch = @(Get-ArchiveRefillLaunchArguments $target @('-File','fixture.ps1','-AdaptiveMaximumRuntimeSeconds','1800') @(5) 1800)
    if ($target.fallbackWarp -ne '' -or $target.lane -ne 'throughput' -or $target.adaptiveMaximumRuntimeSeconds -ne 1800 -or '-Warp' -in $launch) { throw 'Fresh queue did not restore fast mode.' }
    # Recover an already-owned transient failure immediately, without transferring
    # ownership or requiring a donor. A newly failed attempt is not spun forever.
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 0; entries = @() }) $source.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 1; entries = @($large) }) $target.queuePath 10
    $failed = [pscustomobject]@{ warp = $large.warp; normalizedWarp = $large.normalizedWarp; status = 'retryable'; failedUtc = '2026-09-06T00:00:00Z'; error = 'Adaptive discovery failed: ATLAS_COVER cancelled reason=left-world' }
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @($failed) }) $target.statePath 10
    $ledgerPath = Join-Path $testRoot 'owned-retry.json'
    $deferred.entries = @()
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred
    if ($target.fallbackWarp -ne 'Large Base' -or $ledger.batches[0].sourceWorkerId -ne 5 -or $ledger.batches[0].status -ne 'applied') { throw 'Owned disconnect retry did not become immediately runnable.' }
    $failed.failedUtc = '2026-09-07T00:00:00Z'
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @($failed) }) $target.statePath 10
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred
    if ($target.fallbackWarp -ne '' -or $ledger.batches.Count -ne 1) { throw 'Repeated transient failure entered an unbounded retry loop.' }
    # Existing live collectors can release only an unattempted tail once startup
    # is proven past and their next-dispatch boundary has been fenced. Retain two
    # unattempted candidates to cover the active/next-candidate race.
    $source.processId = $PID
    $source | Add-Member stdout (Join-Path $testRoot 'current-launch.out.log') -Force
    [IO.File]::WriteAllText($source.stdout, "WARP Base_1`n")
    [IO.File]::SetCreationTimeUtc($source.stdout, (Get-Process -Id $PID).StartTime.ToUniversalTime())
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 8; entries = $entries }) $source.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 0; entries = @() }) $target.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @() }) $source.statePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @() }) $target.statePath 10
    $deferred.entries = @([pscustomobject]@{ normalizedWarp = 'base_8'; targetWorkerId = 1 })
    $ledgerPath = Join-Path $testRoot 'live-tail.json'
    # Historical background skips are preflight records, not capture attempts.
    # They comprise most of the real zero-radius backlog and must transfer.
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @($entries | ForEach-Object {
        [pscustomobject]@{ warp = $_.warp; normalizedWarp = $_.normalizedWarp; status = 'skipped-inside-background' }
    }) }) $source.statePath 10
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred -BatchSize 3
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(5) $ledgerPath $deferred -BatchSize 3
    if ($ledger.batches[0].releaseMethod -ne 'signaled-untouched-tail' -or $ledger.batches[0].status -ne 'applied') { throw 'Verified live queued tail was not released.' }
    if ((@((Get-ArchiveWorkerQueue $target).entries.normalizedWarp) -join ',') -ne 'base_5,base_6,base_7') { throw 'Preflight-only skipped backlog was not refilled.' }
    $retained = @((Get-ArchiveWorkerQueue $source).entries.normalizedWarp)
    if ('base_1' -notin $retained -or 'base_2' -notin $retained -or 'base_8' -notin $retained) { throw 'Active/next/deferred candidates were transferred.' }
    if (-not (Get-Process -Id $PID) -or -not (Test-Path -LiteralPath $source.exitSignalPath)) { throw 'Live capture was interrupted or dispatch fence lost.' }
    $collector = [IO.File]::ReadAllText((Join-Path $scriptsRoot 'invoke-archive-collector.ps1'))
    if ($collector -notmatch '\$candidateOrdinal -gt 0' -or $collector -notmatch 'SAFE-QUEUE-RELOAD requested after completed WDL boundary') { throw 'Legacy collector dispatch contract changed; live tail release must be re-reviewed.' }
    # Drain mode survives a supervisor/watchdog restart, gives every worker the
    # long budget and retains refill between peers, with deferred work first.
    if ((Get-ArchiveCollectorLanePolicy $testRoot).allLong) { throw 'Missing policy changed the balanced default.' }
    Write-AtlasJsonAtomically ([pscustomobject]@{ mode = 'all-long' }) (Join-Path $testRoot 'collector-lane-policy.json') 10
    if (-not (Get-ArchiveCollectorLanePolicy $testRoot).allLong) { throw 'Persistent all-long policy was ignored.' }
    $source.processId = 0; $target.processId = 0
    $source | Add-Member fallbackWarp '' -Force
    $target.fallbackWarp = ''
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 8; entries = $entries }) $source.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 0; entries = @() }) $target.queuePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @() }) $source.statePath 10
    Write-AtlasJsonAtomically ([pscustomobject]@{ entries = @() }) $target.statePath 10
    $deferred.entries = @([pscustomobject]@{ normalizedWarp = 'base_8'; sourceWorkerId = 5; targetWorkerId = 1; queueEntry = $entries[7] })
    $ledgerPath = Join-Path $testRoot 'all-long.json'
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(1,5) $ledgerPath $deferred -AllLong
    if ($ledger.batches.Count -ne 1 -or $ledger.batches[0].mode -ne 'temporary-long') { throw 'All-long mode failed to prefer deferred work over fresh work.' }
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(1,5) $ledgerPath $deferred -AllLong
    if ($target.fallbackWarp -ne 'Base_8' -or $deferred.entries[0].targetWorkerId -ne 5) { throw 'All-long peer refill lost deferred ownership.' }
    $launch = @(Get-ArchiveRefillLaunchArguments $target @('-AdaptiveMaximumRuntimeSeconds','1800','-AdaptiveRuntimeLimitDisposition','Retryable') @(1,5) 1800 -AllLong)
    if ($target.lane -ne 'large-wdl' -or $launch[1] -ne '43200' -or $launch[3] -ne 'Review' -or $launch[-1] -ne '"Base_8"') { throw 'All-long launch retained fast timeout/disposition or lost its resume target.' }
    $all = @($workers | ForEach-Object { (Get-ArchiveWorkerQueue $_).entries.normalizedWarp })
    if ($all.Count -ne 8 -or @($all | Select-Object -Unique).Count -ne 8) { throw 'All-long transfer duplicated or lost a job.' }
    $target.fallbackWarp = ''
    Write-AtlasJsonAtomically ([pscustomobject]@{ queueCount = 0; entries = @() }) $target.queuePath 10
    $deferred.entries = @()
    $ledgerPath = Join-Path $testRoot 'all-long-fresh.json'
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(1,5) $ledgerPath $deferred -AllLong -BatchSize 2
    $ledger = Invoke-ArchiveFastLaneRefill $workers @(1,5) $ledgerPath $deferred -AllLong -BatchSize 2
    if (@((Get-ArchiveWorkerQueue $target).entries).Count -ne 2) { throw 'Idle long worker could not borrow fresh queued work from a peer.' }
    Write-Output 'PASS safe handoff, live-tail/active protection, unique ownership, crash replay, bounded retry, fallback, persistent all-long budget and deferred-first peer refill'
} finally {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $testRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escapes temp root.' }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
