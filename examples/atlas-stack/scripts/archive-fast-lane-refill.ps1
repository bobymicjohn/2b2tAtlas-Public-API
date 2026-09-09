Set-StrictMode -Version 2.0

function Test-ArchiveRefillWorkerRunning([object]$Worker) {
    if ($null -ne $Worker.PSObject.Properties['processId']) {
        return [int]$Worker.processId -gt 0 -and $null -ne (Get-Process -Id ([int]$Worker.processId) -ErrorAction SilentlyContinue)
    }
    if ($null -ne $Worker.PSObject.Properties['process'] -and $null -ne $Worker.process) {
        $Worker.process.Refresh()
        return -not $Worker.process.HasExited
    }
    return $false
}

function Invoke-ArchiveFastLaneRefill {
    param(
        [object[]]$Workers,
        [int[]]$FastLaneWorkerIds,
        [string]$LedgerPath,
        [object]$DeferralLedger,
        [ValidateRange(1, 100)][int]$BatchSize = 24,
        [switch]$AllLong
    )
    # Called only by the exclusive supervisor, before it restarts any worker.
    # Fence the donor's next dispatch first. Deferred/attempted work waits for
    # process exit; a verified current-launch donor may release an untouched tail
    # while retaining its active and next candidates (see Test-ArchiveLiveTailRelease).
    $ledger = if (Test-Path -LiteralPath $LedgerPath -PathType Leaf) {
        Read-AtlasJsonWithRetry -Path $LedgerPath
    } else { [pscustomobject]@{ schemaVersion = 1; updatedUtc = ''; batches = @() } }
    $byId = @{}
    $queues = @{}
    foreach ($worker in $Workers) {
        $byId[[int]$worker.id] = $worker
        $queues[[int]$worker.id] = Get-ArchiveWorkerQueue $worker
    }
    $excluded = @{}
    foreach ($route in @($DeferralLedger.entries)) { $excluded[[string]$route.normalizedWarp] = $true }
    foreach ($worker in $Workers) {
        foreach ($entry in @((Read-AtlasJsonWithRetry -Path $worker.statePath).entries)) {
            # Never send timed-out, completed, missing or review-held work around again.
            # Preflight/background skips have never started a capture and remain
            # eligible in the current zero-background-radius collector queues.
            if ([string]$entry.status -notin @('preflight-eligible', 'skipped-inside-background')) {
                $excluded[[string]$entry.normalizedWarp] = $true
            }
        }
    }

    foreach ($batch in @($ledger.batches | Where-Object { $_.status -in @('waiting-for-boundary', 'committing') })) {
        $source = $byId[[int]$batch.sourceWorkerId]
        $target = $byId[[int]$batch.targetWorkerId]
        if ($null -eq $source -or $null -eq $target) { throw 'Refill ledger references an unavailable worker.' }
        $targetIsTemporaryLong = $null -ne $target.PSObject.Properties['lane'] -and [string]$target.lane -eq 'temporary-long'
        $sourceRunning = Test-ArchiveRefillWorkerRunning $source
        $liveTailRelease = $sourceRunning -and $batch.mode -eq 'throughput' -and
            (Test-ArchiveLiveTailRelease $source)
        if (($sourceRunning -and -not $liveTailRelease) -or
            ((Test-ArchiveRefillWorkerRunning $target) -and -not ($targetIsTemporaryLong -and $batch.mode -eq 'throughput'))) { continue }
        if ($batch.status -eq 'waiting-for-boundary') {
            if ([string]$batch.mode -eq 'temporary-long') {
                $deferredIds = @{}
                foreach ($route in @($DeferralLedger.entries | Where-Object { [int]$_.targetWorkerId -eq [int]$source.id })) {
                    $deferredIds[[string]$route.normalizedWarp] = $true
                }
                $sourceState = @{}
                foreach ($entry in @((Read-AtlasJsonWithRetry -Path $source.statePath).entries)) { $sourceState[[string]$entry.normalizedWarp] = $entry }
                $batch.entries = @($queues[[int]$source.id].entries | Where-Object {
                    $prior = $sourceState[[string]$_.normalizedWarp]
                    $deferredIds.ContainsKey([string]$_.normalizedWarp) -and $null -eq $prior
                } | Select-Object -Last 1)
            } else {
                $untouched = @($queues[[int]$source.id].entries | Where-Object {
                    -not $excluded.ContainsKey([string]$_.normalizedWarp)
                })
                if ($liveTailRelease) {
                    # The current candidate and the one that could have passed the
                    # boundary test just before our signal remain with the donor.
                    # Deferral reconciliation only prepends known, excluded work;
                    # the unattempted subsequence retains its loaded queue order.
                    $untouched = @($untouched | Select-Object -Skip 2)
                }
                if ($liveTailRelease -and $untouched.Count -eq 0) { continue }
                $batch.entries = @($untouched | Select-Object -Last $BatchSize)
                $batch | Add-Member -NotePropertyName releaseMethod -NotePropertyValue $(if ($liveTailRelease) { 'signaled-untouched-tail' } else { 'stopped-worker' }) -Force
            }
            $batch.status = 'committing'
            $ledger.updatedUtc = [DateTime]::UtcNow.ToString('o')
            Write-AtlasJsonAtomically -Value $ledger -Path $LedgerPath -Depth 24
        }
        if ([string]$batch.mode -eq 'temporary-long') {
            foreach ($entry in @($batch.entries)) {
                $route = @($DeferralLedger.entries | Where-Object { [string]$_.normalizedWarp -eq [string]$entry.normalizedWarp })
                if ($route.Count -ne 1) { throw 'Temporary long job has no unique durable owner.' }
                $route[0].targetWorkerId = [int]$target.id
            }
            # The deferral ledger is authoritative during every reconciliation.
            Save-ArchiveFastLaneLedger $DeferralLedger (Join-Path (Split-Path -Parent $LedgerPath) 'fast-lane-deferred.json')
        }
        $sourceQueue = $queues[[int]$source.id]
        $targetQueue = $queues[[int]$target.id]
        foreach ($entry in @($batch.entries)) {
            $identity = [string]$entry.normalizedWarp
            foreach ($other in $Workers) {
                if ([int]$other.id -in @([int]$source.id, [int]$target.id)) { continue }
                if (@($queues[[int]$other.id].entries | Where-Object { [string]$_.normalizedWarp -eq $identity }).Count -gt 0) {
                    throw "Refill candidate already belongs to another worker: $identity"
                }
            }
            $sourceQueue.entries = @($sourceQueue.entries | Where-Object { [string]$_.normalizedWarp -ne $identity })
            if (@($targetQueue.entries | Where-Object { [string]$_.normalizedWarp -eq $identity }).Count -eq 0) {
                $targetQueue.entries = @($targetQueue.entries) + @($entry)
            }
        }
        # Persist intent first above; a crash between these writes is replayed
        # before either worker can restart. Existing entries and states survive.
        $sourceQueue.queueCount = @($sourceQueue.entries).Count
        $targetQueue.queueCount = @($targetQueue.entries).Count
        Write-AtlasJsonAtomically -Value $sourceQueue -Path $source.queuePath -Depth 24
        Write-AtlasJsonAtomically -Value $targetQueue -Path $target.queuePath -Depth 24
        Set-ArchiveWorkerOwnership $source @($sourceQueue.entries)
        Set-ArchiveWorkerOwnership $target @($targetQueue.entries)
        $batch.status = 'applied'
        $batch.appliedUtc = [DateTime]::UtcNow.ToString('o')
        $ledger.updatedUtc = $batch.appliedUtc
        Write-AtlasJsonAtomically -Value $ledger -Path $LedgerPath -Depth 24
        $target.restartAfterUtc = [DateTime]::UtcNow
        Write-Verbose "FAST-LANE-REFILLED source=$($source.id) target=$($target.id) entries=$(@($batch.entries).Count)"
    }

    Set-ArchiveFastLaneModes -Workers $Workers -FastLaneWorkerIds $FastLaneWorkerIds -RefillLedger $ledger
    $pending = @($ledger.batches | Where-Object { $_.status -in @('waiting-for-boundary', 'committing') })
    foreach ($fast in @($Workers | Where-Object { [int]$_.id -in $FastLaneWorkerIds } | Sort-Object id)) {
        $temporaryActive = (Test-ArchiveRefillWorkerRunning $fast) -and
            $null -ne $fast.PSObject.Properties['lane'] -and [string]$fast.lane -eq 'temporary-long'
        if ((Test-ArchiveRefillWorkerRunning $fast) -and -not $temporaryActive) { continue }
        # assigned/completed status can lag by a poll: inspect actual queue/state.
        $ownState = @{}
        foreach ($entry in @((Read-AtlasJsonWithRetry -Path $fast.statePath).entries)) { $ownState[[string]$entry.normalizedWarp] = $entry }
        $hasWork = @($queues[[int]$fast.id].entries | Where-Object {
            $prior = $ownState[[string]$_.normalizedWarp]
            if ($null -eq $prior) { return $true }
            if ([string]$prior.status -in @('captured', 'ready')) { return -not (Test-FinalAdaptiveCapture $prior) }
            return [string]$prior.status -notin @('missing', 'retryable', 'needs-footprint-review')
        }).Count -gt 0
        if ($hasWork -or (-not $temporaryActive -and -not [string]::IsNullOrWhiteSpace([string]$fast.fallbackWarp))) { continue }
        # "Completed" also includes retryable disconnects. Recover owned transient
        # failures as one-shot long attempts before waiting on another queue owner.
        $retried = @{}
        foreach ($priorBatch in @($ledger.batches | Where-Object {
            $_.mode -eq 'temporary-long' -and [int]$_.sourceWorkerId -eq [int]$fast.id -and [int]$_.targetWorkerId -eq [int]$fast.id
        })) { foreach ($entry in @($priorBatch.entries)) { $retried[[string]$entry.normalizedWarp] = $true } }
        $retry = @($queues[[int]$fast.id].entries | Where-Object {
            $identity = [string]$_.normalizedWarp
            $prior = $ownState[$identity]
            $null -ne $prior -and [string]$prior.status -eq 'retryable' -and
                [string]$prior.error -match 'cancelled reason=left-world|Collector process exited|Disconnected|Connection reset' -and
                -not $retried.ContainsKey($identity)
        } | Select-Object -First 1)
        if ($retry.Count -gt 0 -and -not $temporaryActive) {
            $prior = $ownState[[string]$retry[0].normalizedWarp]
            $ledger.batches = @($ledger.batches) + @([pscustomobject]@{
                sourceWorkerId = [int]$fast.id; targetWorkerId = [int]$fast.id
                mode = 'temporary-long'; status = 'applied'; requestedUtc = [DateTime]::UtcNow.ToString('o')
                appliedUtc = [DateTime]::UtcNow.ToString('o'); retryFailedUtc = [string]$prior.failedUtc; entries = @($retry[0])
            })
            $ledger.updatedUtc = [DateTime]::UtcNow.ToString('o')
            Write-AtlasJsonAtomically -Value $ledger -Path $LedgerPath -Depth 24
            $fast.restartAfterUtc = [DateTime]::UtcNow
            continue
        }
        if (@($pending | Where-Object { [int]$_.targetWorkerId -eq [int]$fast.id }).Count -gt 0) { continue }
        $donors = @()
        foreach ($donor in @($Workers | Where-Object { [int]$_.id -ne [int]$fast.id -and ($AllLong -or [int]$_.id -notin $FastLaneWorkerIds) })) {
            if (@($pending | Where-Object { [int]$_.sourceWorkerId -eq [int]$donor.id }).Count -gt 0) { continue }
            $available = @($queues[[int]$donor.id].entries | Where-Object { -not $excluded.ContainsKey([string]$_.normalizedWarp) }).Count
            if ($available -gt 1) { $donors += [pscustomobject]@{ worker = $donor; available = $available } }
        }
        $mode = 'throughput'
        if ($donors.Count -eq 0 -or $AllLong) {
            $freshDonors = @($donors)
            $donors = @()
            foreach ($donor in @($Workers | Where-Object { [int]$_.id -ne [int]$fast.id -and ($AllLong -or [int]$_.id -notin $FastLaneWorkerIds) })) {
                if (@($pending | Where-Object { [int]$_.sourceWorkerId -eq [int]$donor.id }).Count -gt 0) { continue }
                $done = @{}
                foreach ($entry in @((Read-AtlasJsonWithRetry -Path $donor.statePath).entries)) { $done[[string]$entry.normalizedWarp] = $true }
                $available = @($DeferralLedger.entries | Where-Object {
                    [int]$_.targetWorkerId -eq [int]$donor.id -and -not $done.ContainsKey([string]$_.normalizedWarp)
                }).Count
                if ($available -gt 0) { $donors += [pscustomobject]@{ worker = $donor; available = $available } }
            }
            if ($donors.Count -gt 0) { $mode = 'temporary-long' } else { $donors = $freshDonors }
        }
        if ($donors.Count -eq 0) { continue }
        $source = @($donors | Sort-Object @{ Expression = 'available'; Descending = $true }, @{ Expression = { $_.worker.id } } | Select-Object -First 1)[0].worker
        $batch = [pscustomobject]@{
            sourceWorkerId = [int]$source.id; targetWorkerId = [int]$fast.id
            mode = $mode; status = 'waiting-for-boundary'; requestedUtc = [DateTime]::UtcNow.ToString('o')
            appliedUtc = $null; entries = @()
        }
        $ledger.batches = @($ledger.batches) + @($batch)
        $pending += $batch
        $ledger.updatedUtc = [DateTime]::UtcNow.ToString('o')
        Write-AtlasJsonAtomically -Value $ledger -Path $LedgerPath -Depth 24
    }
    # Reissue an unapplied boundary request after a supervisor crash. Never kill
    # the client or alter its current capture, survey checkpoints or WDL files.
    foreach ($batch in @($ledger.batches | Where-Object status -eq 'waiting-for-boundary')) {
        $source = $byId[[int]$batch.sourceWorkerId]
        if (Test-ArchiveRefillWorkerRunning $source) {
            [IO.File]::WriteAllText([string]$source.exitSignalPath, 'fast-lane-refill', (New-Object Text.UTF8Encoding($false)))
        }
    }
    Set-ArchiveFastLaneModes -Workers $Workers -FastLaneWorkerIds $FastLaneWorkerIds -RefillLedger $ledger
    return $ledger
}

function ConvertTo-ArchiveProcessArgument([string]$Value) {
    # Windows argv quoting, not executable PowerShell text. In particular, quotes
    # and trailing backslashes must survive Start-Process's flattened ArgumentList.
    return '"' + ([regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"'
}

function Set-ArchiveFastLaneModes([object[]]$Workers, [int[]]$FastLaneWorkerIds, [object]$RefillLedger) {
    foreach ($worker in @($Workers | Where-Object { [int]$_.id -in $FastLaneWorkerIds })) {
        if (Test-ArchiveRefillWorkerRunning $worker) { continue }
        $fallback = @{}
        foreach ($batch in @($RefillLedger.batches | Where-Object {
            $_.status -eq 'applied' -and $_.mode -eq 'temporary-long' -and [int]$_.targetWorkerId -eq [int]$worker.id
        })) {
            foreach ($entry in @($batch.entries)) { $fallback[[string]$entry.normalizedWarp] = $batch }
        }
        $state = @{}
        foreach ($entry in @((Read-AtlasJsonWithRetry -Path $worker.statePath).entries)) { $state[[string]$entry.normalizedWarp] = $entry }
        $fresh = @(); $large = @()
        foreach ($entry in @((Get-ArchiveWorkerQueue $worker).entries)) {
            $identity = [string]$entry.normalizedWarp
            $prior = $state[$identity]
            if ($fallback.ContainsKey($identity)) {
                $batch = $fallback[$identity]
                $sameOwnedFailure = $null -ne $batch.PSObject.Properties['retryFailedUtc'] -and $null -ne $prior -and
                    [string]$prior.status -eq 'retryable' -and [string]$prior.failedUtc -eq [string]$batch.retryFailedUtc
                if ($null -eq $prior -or $sameOwnedFailure -or ([string]$prior.status -eq 'retryable' -and
                    [string]$prior.error -match '^Adaptive maximum runtime of 1800 second')) { $large += $entry }
            } elseif ($null -eq $prior -or [string]$prior.status -in @('preflight-eligible', 'skipped-inside-background')) {
                $fresh += $entry
            }
        }
        $warp = if ($fresh.Count -eq 0 -and $large.Count -gt 0) { [string]$large[0].warp } else { '' }
        $worker | Add-Member -NotePropertyName fallbackWarp -NotePropertyValue $warp -Force
    }
}

function Get-ArchiveRefillLaunchArguments([object]$Worker, [string[]]$Arguments, [int[]]$FastLaneWorkerIds, [int]$FastTimeoutSeconds, [switch]$AllLong) {
    $result = @($Arguments)
    if ([int]$Worker.id -notin $FastLaneWorkerIds -and -not $AllLong) { return $result }
    $temporaryLong = $null -ne $Worker.PSObject.Properties['fallbackWarp'] -and -not [string]::IsNullOrWhiteSpace([string]$Worker.fallbackWarp)
    $timeout = if ($temporaryLong -or $AllLong) { 43200 } else { $FastTimeoutSeconds }
    $found = $false
    for ($i = 0; $i -lt $result.Count - 1; $i++) {
        if ($result[$i] -eq '-AdaptiveMaximumRuntimeSeconds') { $result[$i + 1] = [string]$timeout; $found = $true }
    }
    if (-not $found) { $result += @('-AdaptiveMaximumRuntimeSeconds', [string]$timeout) }
    if ($AllLong) {
        $foundDisposition = $false
        for ($i = 0; $i -lt $result.Count - 1; $i++) {
            if ($result[$i] -eq '-AdaptiveRuntimeLimitDisposition') {
                $result[$i + 1] = 'Review'; $foundDisposition = $true
            }
        }
        if (-not $foundDisposition) { $result += @('-AdaptiveRuntimeLimitDisposition', 'Review') }
    }
    if ($temporaryLong) { $result += @('-Warp', (ConvertTo-ArchiveProcessArgument ([string]$Worker.fallbackWarp))) }
    $Worker.lane = if ($AllLong) { 'large-wdl' } elseif ($temporaryLong) { 'temporary-long' } else { 'throughput' }
    $Worker.adaptiveMaximumRuntimeSeconds = $timeout
    return $result
}

function Test-ArchiveLiveTailRelease([object]$Worker) {
    # Compatibility with already-running collectors: their candidate arrays are
    # fixed at launch, in queue order. A current-launch WARP line proves startup
    # (including its stale-signal cleanup) is past. A still-present boundary signal
    # fences further dispatch. Only a future untouched tail may be released;
    # active/deferred work always waits for full process exit.
    if (-not (Test-Path -LiteralPath $Worker.exitSignalPath -PathType Leaf)) { return $false }
    $workerPid = if ($null -ne $Worker.PSObject.Properties['processId']) { [int]$Worker.processId } else { [int]$Worker.process.Id }
    $process = Get-Process -Id $workerPid -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $false }
    $start = $process.StartTime.ToUniversalTime()
    $paths = @()
    foreach ($name in @('stdout','stdoutPath')) {
        if ($null -ne $Worker.PSObject.Properties[$name]) { $paths += [string]$Worker.$name }
    }
    if ($null -ne $Worker.PSObject.Properties['workerRoot'] -and $null -ne $Worker.PSObject.Properties['restarts']) {
        $paths += Join-Path $Worker.workerRoot "collector.reattach-restart-$($Worker.restarts).out.log"
        $paths += Join-Path $Worker.workerRoot "collector.restart-$($Worker.restarts).out.log"
    }
    foreach ($path in @($paths | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $info = Get-Item -LiteralPath $path
        if ($info.CreationTimeUtc -lt $start.AddSeconds(-10) -or $info.CreationTimeUtc -gt $start.AddSeconds(30)) { continue }
        $reader = $null
        try {
            $reader = [IO.StreamReader]::new([IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite))
            for ($lineNumber = 0; $lineNumber -lt 10000 -and -not $reader.EndOfStream; $lineNumber++) {
                if ($reader.ReadLine() -match '^WARP .+') {
                    return Test-Path -LiteralPath $Worker.exitSignalPath -PathType Leaf
                }
            }
        } finally { if ($null -ne $reader) { $reader.Dispose() } }
    }
    return $false
}
