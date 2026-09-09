Set-StrictMode -Version 2.0

function Get-ArchiveFastLaneLedger([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject][ordered]@{
            schemaVersion = 1
            updatedUtc = [DateTime]::UtcNow.ToString('o')
            entries = @()
        }
    }
    return Read-AtlasJsonWithRetry -Path $Path
}

function Save-ArchiveFastLaneLedger([object]$Ledger, [string]$Path) {
    $Ledger.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Write-AtlasJsonAtomically -Value $Ledger -Path $Path -Depth 24
}

function Get-ArchiveWorkerQueue([object]$Worker) {
    return Read-AtlasJsonWithRetry -Path ([string]$Worker.queuePath)
}

function Set-ArchiveWorkerOwnership([object]$Worker, [object[]]$Entries) {
    $identities = @{}
    foreach ($entry in @($Entries)) {
        $identity = [string]$entry.normalizedWarp
        if (-not [string]::IsNullOrWhiteSpace($identity)) { $identities[$identity] = $true }
    }
    $Worker.identities = $identities
    $Worker.assigned = @($Entries).Count
}

function Sync-ArchiveFastLaneRouting([object[]]$Workers, [string]$LedgerPath) {
    $ledger = Get-ArchiveFastLaneLedger $LedgerPath
    $queueByWorker = @{}
    $changedWorkers = @{}
    foreach ($worker in $Workers) {
        $queueByWorker[[int]$worker.id] = Get-ArchiveWorkerQueue $worker
    }

    foreach ($route in @($ledger.entries)) {
        $identity = [string]$route.normalizedWarp
        $targetId = [int]$route.targetWorkerId
        if ([string]::IsNullOrWhiteSpace($identity) -or -not $queueByWorker.ContainsKey($targetId)) { continue }
        $targetQueue = $queueByWorker[$targetId]
        $storedEntry = $route.queueEntry
        if ($null -eq $storedEntry) { continue }

        $targetEntries = @($targetQueue.entries | Where-Object { [string]$_.normalizedWarp -ne $identity })
        # Deferred large captures are deliberately placed first. The target worker
        # exits at its next safe WDL boundary and reloads this queue.
        $targetQueue.entries = @($storedEntry) + $targetEntries
        $changedWorkers[$targetId] = $true

        foreach ($worker in $Workers) {
            if ([int]$worker.id -eq $targetId) { continue }
            $queue = $queueByWorker[[int]$worker.id]
            $before = @($queue.entries).Count
            $queue.entries = @($queue.entries | Where-Object { [string]$_.normalizedWarp -ne $identity })
            if (@($queue.entries).Count -ne $before) { $changedWorkers[[int]$worker.id] = $true }
        }
    }

    foreach ($worker in $Workers) {
        $workerId = [int]$worker.id
        $queue = $queueByWorker[$workerId]
        if ($changedWorkers.ContainsKey($workerId)) {
            $queue.queueCount = @($queue.entries).Count
            Write-AtlasJsonAtomically -Value $queue -Path ([string]$worker.queuePath) -Depth 24
        }
        Set-ArchiveWorkerOwnership $worker @($queue.entries)
    }
    return $ledger
}

function Move-ArchiveFastLaneTimeouts(
    [object[]]$Workers,
    [int[]]$FastLaneWorkerIds,
    [string]$LedgerPath
) {
    $ledger = Sync-ArchiveFastLaneRouting $Workers $LedgerPath
    $knownRoutes = @{}
    foreach ($route in @($ledger.entries)) { $knownRoutes[[string]$route.normalizedWarp] = $true }
    $longWorkers = @($Workers | Where-Object { [int]$_.id -notin $FastLaneWorkerIds } | Sort-Object id)
    if ($longWorkers.Count -eq 0) { return $ledger }

    $newRoutes = New-Object Collections.ArrayList
    foreach ($fastWorker in @($Workers | Where-Object { [int]$_.id -in $FastLaneWorkerIds })) {
        $state = Read-AtlasJsonWithRetry -Path ([string]$fastWorker.statePath)
        $queue = Get-ArchiveWorkerQueue $fastWorker
        $queueByIdentity = @{}
        foreach ($entry in @($queue.entries)) { $queueByIdentity[[string]$entry.normalizedWarp] = $entry }
        foreach ($entry in @($state.entries)) {
            $identity = [string]$entry.normalizedWarp
            if (-not $fastWorker.identities.ContainsKey($identity) -or $knownRoutes.ContainsKey($identity)) { continue }
            $errorText = if ($null -ne $entry.PSObject.Properties['error']) { [string]$entry.error } else { '' }
            if ([string]$entry.status -ne 'retryable' -or
                $errorText -notmatch '^Adaptive maximum runtime of \d+ second\(s\) reached') { continue }
            if (-not $queueByIdentity.ContainsKey($identity)) { continue }

            $target = @($longWorkers | Sort-Object `
                @{ Expression = { [int]$_.assigned - [int]$_.completed }; Ascending = $true }, `
                @{ Expression = { [int]$_.assigned }; Ascending = $true }, `
                @{ Expression = { [int]$_.id }; Ascending = $true } | Select-Object -First 1)[0]
            [void]$newRoutes.Add([pscustomobject][ordered]@{
                normalizedWarp = $identity
                warp = [string]$entry.warp
                sourceWorkerId = [int]$fastWorker.id
                targetWorkerId = [int]$target.id
                deferredUtc = [DateTime]::UtcNow.ToString('o')
                reason = 'fast-lane-30-minute-limit'
                queueEntry = $queueByIdentity[$identity]
            })
            $knownRoutes[$identity] = $true
            # Account for the route while selecting the next least-loaded target.
            $target.assigned = [int]$target.assigned + 1
        }
    }

    if ($newRoutes.Count -gt 0) {
        $ledger.entries = @($ledger.entries) + @($newRoutes)
        # Persist intent first. A reboot at any later point is repaired by Sync.
        Save-ArchiveFastLaneLedger $ledger $LedgerPath
        $ledger = Sync-ArchiveFastLaneRouting $Workers $LedgerPath
        foreach ($route in @($newRoutes)) {
            $target = @($Workers | Where-Object { [int]$_.id -eq [int]$route.targetWorkerId } | Select-Object -First 1)
            if ($target.Count -eq 1) {
                $signalPath = [string]$target[0].exitSignalPath
                if (-not [string]::IsNullOrWhiteSpace($signalPath)) {
                    [IO.File]::WriteAllText($signalPath, [string]$route.normalizedWarp, (New-Object Text.UTF8Encoding($false)))
                }
            }
            Write-Verbose "FAST-LANE-DEFERRED worker=$($route.sourceWorkerId) target=$($route.targetWorkerId) warp=$($route.warp)"
        }
    }
    return $ledger
}
