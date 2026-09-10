# Interrupted working saves are private evidence, never ready/public WDLs.
function Assert-InterruptedCaptureSnapshot {
    param([string]$Path,[string]$SavesRoot,[string]$CaptureName,
        [string]$RecoveryRoot='D:\AtlasExample\Ingest\DeferredCaptures\interrupted')
    $root=[IO.Path]::GetFullPath($RecoveryRoot).TrimEnd('\')+'\'
    $snapshot=[IO.Path]::GetFullPath($Path)
    if (-not $snapshot.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or
        $snapshot.Substring($root.Length) -notmatch '^\d{8}-\d{6}-[a-f0-9]{32}$') { throw 'Invalid interrupted snapshot path.' }
    $receipt=Read-AtlasJsonWithRetry (Join-Path $snapshot 'receipt.json')
    if ($receipt.schemaVersion -ne 1 -or $receipt.state -ne 'preserved-partial' -or $receipt.completeWorld -ne $false -or
        [IO.Path]::GetFullPath($receipt.sourceRoot).TrimEnd('\') -ne [IO.Path]::GetFullPath($SavesRoot).TrimEnd('\') -or
        @($receipt.files).Count -eq 0) { throw 'Interrupted snapshot identity mismatch.' }
    foreach ($record in $receipt.files) {
        if ($record.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $record.path.Contains('..') -or
            ($record.path -cne ($CaptureName+'.zip') -and -not $record.path.StartsWith($CaptureName+'\',[StringComparison]::Ordinal))) {
            throw 'Interrupted snapshot contains an unexpected capture path.'
        }
        $file=[IO.Path]::GetFullPath((Join-Path $snapshot $record.path))
        if (-not $file.StartsWith($snapshot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Interrupted snapshot path escaped.' }
        $ancestor=$file
        while ($ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Interrupted snapshot contains a reparse point.' }
            $ancestor=Split-Path -Parent $ancestor
        }
        if ((Get-Item -LiteralPath $file).Length -ne $record.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $record.sha256) { throw 'Interrupted snapshot hash mismatch.' }
    }
}

function Save-InterruptedCaptureFiles {
    param(
        [Parameter(Mandatory=$true)][string]$SavesRoot,
        [Parameter(Mandatory=$true)][string[]]$CaptureNames,
        [Parameter(Mandatory=$true)][string]$WarpName,
        [string]$RecoveryRoot = 'D:\AtlasExample\Ingest\DeferredCaptures\interrupted'
    )
    $root = [IO.Path]::GetFullPath($SavesRoot).TrimEnd('\') + '\'
    $files = @()
    foreach ($name in @($CaptureNames | Select-Object -Unique)) {
        if ($name -notmatch '^archive-[A-Za-z0-9._-]+$' -or $name.Contains('..')) {
            throw 'Interrupted capture name must be a safe archive leaf name.'
        }
        foreach ($candidate in @((Join-Path $root $name), (Join-Path $root "$name.zip"))) {
            if (-not (Test-Path -LiteralPath $candidate)) { continue }
            $item = Get-Item -LiteralPath $candidate -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Capture root is a reparse point.' }
            if ($item.PSIsContainer) {
                # Inspect each level before descending; never traverse a junction.
                $pending = New-Object 'Collections.Generic.Queue[string]'
                $pending.Enqueue($item.FullName)
                while ($pending.Count -gt 0) {
                    foreach ($child in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                        if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Capture contains a reparse point.' }
                        if ($child.PSIsContainer) { $pending.Enqueue($child.FullName) }
                        else { $files += $child }
                    }
                }
            } else { $files += $item }
        }
    }
    if ($files.Count -eq 0) { return $null }
    $destination = Join-Path $RecoveryRoot ([datetime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
    $records = @()
    foreach ($file in $files) {
        $source = $file.FullName
        if (-not $source.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Capture escaped its saves root.' }
        $relative = $source.Substring($root.Length)
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        # Called only after the previous collector exits. Deny writes/deletes during
        # each copy as an additional guard against an unexpected surviving writer.
        $inputFile = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $outputFile = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputFile.CopyTo($outputFile); $outputFile.Flush($true) }
            finally { $outputFile.Dispose() }
            $inputFile.Position = 0
            $sourceHash = (Get-FileHash -InputStream $inputFile -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($sourceHash -ne $targetHash) { throw 'Interrupted capture copy hash mismatch.' }
            $records += [pscustomobject]@{path=$relative;bytes=$inputFile.Length;sha256=$targetHash}
        } finally { $inputFile.Dispose() }
    }
    Write-AtlasJsonAtomically -Path (Join-Path $destination 'receipt.json') -Value ([ordered]@{
        schemaVersion=1;state='preserved-partial';warp=$WarpName;createdUtc=[datetime]::UtcNow.ToString('o')
        sourceRoot=$root;completeWorld=$false;files=$records
    })
    # Originals remain intact even after a verified copy. No failed save is deleted.
    return $destination
}

function Preserve-PendingInterruptedCaptures {
    param($StateByWarp, $State, [string]$StatePath, [string]$SavesRoot)
    $changed = $false
    foreach ($record in @($StateByWarp.Values)) {
        # Review holds already retain the stopped save and its journal. Repeated
        # recovery attempts must not multiply that same oversized world on D.
        if ($null -ne $record.PSObject.Properties['requiresFootprintReviewBeforeRecovery'] -and
            [bool]$record.requiresFootprintReviewBeforeRecovery) { continue }
        if ($null -eq $record.PSObject.Properties['workingCaptureNames'] -or
            $null -eq $record.PSObject.Properties['partialPreservation'] -or
            [string]$record.partialPreservation -ne 'retained-in-working-saves') { continue }
        if ((Get-PSDrive D).Free -lt 100GB) { throw 'Interrupted capture preservation needs 100 GiB free on D.' }
        $saved = Save-InterruptedCaptureFiles -SavesRoot $SavesRoot -CaptureNames @($record.workingCaptureNames) -WarpName ([string]$record.warp)
        $record.partialPreservation = if ($saved) { 'verified-copy-on-D' } else { 'no-working-files-found' }
        $record | Add-Member -NotePropertyName partialRecoveryPath -NotePropertyValue $saved -Force
        $changed = $true
    }
    if ($changed) {
        $State.entries = @($StateByWarp.Values | Sort-Object normalizedWarp)
        $State.updatedUtc = [datetime]::UtcNow.ToString('o')
        Write-AtlasJsonAtomically -Path $StatePath -Value $State
    }
}

function Get-RequestedCollectorRetries {
    param([string]$RequestPath, $Queue, $StateByWarp)
    $names = @()
    if (Test-Path -LiteralPath $RequestPath -PathType Leaf) {
        $request = Read-AtlasJsonWithRetry -Path $RequestPath
        if ($request.schemaVersion -ne 1 -or @($request.warps).Count -gt 100) { throw 'Invalid collector retry request.' }
        $requestedUtc = if ($null -ne $request.PSObject.Properties['updatedUtc']) {
            [datetimeoffset]::Parse([string]$request.updatedUtc)
        } else {
            # Legacy request files did not include an explicit timestamp.
            [datetimeoffset](Get-Item -LiteralPath $RequestPath).LastWriteTimeUtc
        }
        foreach ($requestedWarp in @($request.warps)) {
            $prior = $StateByWarp[(Get-NormalizedWarp ([string]$requestedWarp))]
            $failedUtc = if ($null -ne $prior -and $null -ne $prior.PSObject.Properties['failedUtc']) {
                [string]$prior.failedUtc
            } else { '' }
            # A request applies to the failure it was written for. Once another
            # attempt fails, normal bounded automatic retry/backoff takes over.
            # Keeping retry-warps.json on disk must not create an infinite retry.
            if (-not [string]::IsNullOrWhiteSpace($failedUtc)) {
                if ([datetimeoffset]::Parse($failedUtc) -le $requestedUtc) { $names += [string]$requestedWarp }
            } elseif ($null -eq $prior -or $null -eq $prior.PSObject.Properties['automaticRetryAfterUtc']) {
                $names += [string]$requestedWarp
            }
        }
    }
    foreach ($entry in @($Queue.entries)) {
        $prior = $StateByWarp[[string]$entry.normalizedWarp]
        if ($null -ne $prior -and [string]$prior.status -eq 'retryable' -and
            $null -ne $prior.PSObject.Properties['automaticRetryAfterUtc'] -and
            $null -ne $prior.PSObject.Properties['interruptionCount'] -and
            [int]$prior.interruptionCount -le 3 -and
            [datetimeoffset]::Parse([string]$prior.automaticRetryAfterUtc) -le [datetimeoffset]::UtcNow) {
            $names += [string]$entry.warp
        }
    }
    foreach ($warpName in @($names | Select-Object -Unique)) {
        $identity = Get-NormalizedWarp ([string]$warpName)
        $matches = @($Queue.entries | Where-Object { [string]$_.normalizedWarp -ceq $identity })
        if ($matches.Count -ne 1) { throw "Retry request must have one owner in this worker queue: $warpName" }
        $prior = $StateByWarp[$identity]
        # Completed and review-held captures are never overridden by a retry request.
        if ($null -eq $prior -or [string]$prior.status -eq 'retryable') { [string]$warpName }
    }
}
