[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\AtlasExample\Ingest\archive-sync\collector',
    [string]$QueuePath = 'C:\AtlasExample\Ingest\archive-sync\collector-queue.json',
    [string]$StatePath = 'C:\AtlasExample\Ingest\archive-sync\collector-state.json',
    [string]$CapturedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [string]$ReadyRoot = 'C:\AtlasExample\Ingest\archive-sync\ready',
    [string]$Server = 'thearchive.world',
    [string]$MinecraftVersion = 'fabric-loader-0.19.5-1.21.11',
    [string[]]$Warp = @(),
    [ValidateRange(1, 10000)]
    [int]$MaxWarps = 1,
    [ValidateRange(10, 600)]
    [int]$CaptureSeconds = 35,
    [int[]]$CoverageBounds = @(),
    [ValidateSet('Overworld', 'Nether', 'End')]
    [string]$CoverageDimension = 'Overworld',
    [ValidateRange(2, 32)]
    [int]$CoverageTerrainRadiusChunks = 10,
    [ValidateRange(1, 64)]
    [int]$CoverageStepChunks = 20,
    [ValidateRange(60, 7200)]
    [int]$CoverageTimeoutSeconds = 1800,
    [switch]$AdaptiveScan,
    [switch]$PreflightOnly,
    [ValidateRange(256, 4096)]
    [int]$AdaptiveCoreRadiusBlocks = 512,
    [ValidateRange(128, 2048)]
    [int]$AdaptiveExpansionBlocks = 256,
    [ValidateRange(0, 2000000000)]
    [int]$AdaptiveMaxRadiusBlocks = 0,
    [ValidateRange(2, 32)]
    [int]$AdaptiveTerrainRadiusChunks = 10,
    [ValidateRange(1, 64)]
    [int]$AdaptiveStepChunks = 20,
    [ValidateSet('Overworld', 'Nether', 'End')]
    [string]$AdaptiveDimension = 'Overworld',
    [ValidateRange(60, 14400)]
    [int]$AdaptiveTimeoutSeconds = 7200,
    [ValidateRange(0, 86400)]
    [int]$AdaptiveMaximumRuntimeSeconds = 43200,
    [ValidateSet('Review', 'Retryable')]
    [string]$AdaptiveRuntimeLimitDisposition = 'Review',
    [ValidateRange(0, 2000000000)]
    [int]$AdaptiveBackgroundRadiusBlocks = 0,
    [ValidateRange(0, 300)]
    [int]$DelayBetweenWarpsSeconds = 5,
    [ValidateRange(10, 10000)]
    [int]$MinimumFreeGiB = 100,
    [switch]$MoveToReady,
    [switch]$RecheckMissing,
    [switch]$RecheckRetryable,
    [string]$ExitAfterCurrentWarpSignalPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')
. (Join-Path $PSScriptRoot 'archive-adaptive-policy.ps1')
. (Join-Path $PSScriptRoot 'archive-survey-handoff.ps1')
. (Join-Path $PSScriptRoot 'archive-interrupted-captures.ps1')
. (Join-Path $PSScriptRoot 'archive-capture-recovery.ps1')
. (Join-Path $PSScriptRoot 'install-pending-archive-coverage.ps1')

$java = 'C:\Program Files\Java\jdk-21\bin\java.exe'
$launcher = Join-Path $InstallRoot 'headlessmc-launcher-2.10.0.jar'
$gameRoot = Join-Path $InstallRoot 'game'
$minecraftRoot = Join-Path $InstallRoot 'minecraft'
$savesRoot = Join-Path $gameRoot 'saves'
$adaptiveNonVoidManifestPath = Join-Path $gameRoot 'config\atlas-archive-coverage\adaptive-nonvoid.csv'
$version = $MinecraftVersion
$script:process = $null
$script:stdoutTask = $null
$script:stderrTask = $null
$script:lines = New-Object 'System.Collections.Generic.List[string]'
$script:logWriter = $null
$script:downloadActive = $false
$script:resumeCapture = $null
$script:captureStartIndex = 0
$script:lastDisconnectScanIndex = 0
$collectorStandardVersion = 2
$coverageEnabled = $CoverageBounds.Count -gt 0
$adaptiveEnabled = [bool]$AdaptiveScan
if ($coverageEnabled -and $adaptiveEnabled) {
    throw 'CoverageBounds and AdaptiveScan are mutually exclusive capture modes.'
}
if ($PreflightOnly -and -not $adaptiveEnabled) {
    throw 'PreflightOnly requires AdaptiveScan so landing coordinates can be audited.'
}
if ($adaptiveEnabled -and $MoveToReady) {
    throw 'Adaptive captures are canary-only and cannot use MoveToReady until their footprint has been reviewed.'
}
if ($coverageEnabled -and $CoverageBounds.Count -ne 4) {
    throw 'CoverageBounds must contain exactly four integers: minX, minZ, maxXExclusive, maxZExclusive.'
}
if ($coverageEnabled) {
    if ($CoverageBounds[2] -le $CoverageBounds[0] -or $CoverageBounds[3] -le $CoverageBounds[1]) {
        throw 'CoverageBounds must have positive width and height.'
    }
    if ($CoverageStepChunks -gt $CoverageTerrainRadiusChunks * 2) {
        throw 'CoverageStepChunks cannot exceed twice CoverageTerrainRadiusChunks or the route would contain gaps.'
    }
}
if ($adaptiveEnabled) {
    if ($AdaptiveMaxRadiusBlocks -ne 0 -and $AdaptiveMaxRadiusBlocks -lt $AdaptiveCoreRadiusBlocks) {
        throw 'AdaptiveMaxRadiusBlocks cannot be smaller than AdaptiveCoreRadiusBlocks.'
    }
    if ($AdaptiveStepChunks -gt $AdaptiveTerrainRadiusChunks * 2) {
        throw 'AdaptiveStepChunks cannot exceed twice AdaptiveTerrainRadiusChunks or the teleport grid would contain gaps.'
    }
    $wdlConfig = Join-Path $gameRoot 'config\wdl.properties'
    if (-not (Test-Path -LiteralPath $wdlConfig -PathType Leaf)) {
        throw "Archive WDL configuration was not found: $wdlConfig"
    }
    $wdlConfigText = [IO.File]::ReadAllText($wdlConfig, (New-Object Text.UTF8Encoding($false)))
    if ($wdlConfigText -notmatch '(?m)^skipVoidChunks=true\s*$') {
        throw 'Adaptive scanning requires skipVoidChunks=true so pure-void museum chunks are omitted from the saved artifact.'
    }
}

function ConvertTo-PlainLine([string]$Value) {
    if ($null -eq $Value) { return '' }
    $escape = [regex]::Escape([string][char]27)
    return [regex]::Replace($Value, "$escape\[[0-9;?]*[ -/]*[@-~]", '')
}

function Save-JsonAtomically([object]$Value, [string]$Path) {
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 12
}

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Add-CollectorLine([string]$Line, [string]$Stream) {
    if ($null -eq $Line) { return }
    $plain = ConvertTo-PlainLine $Line
    $script:lines.Add($plain)
    if ($null -ne $script:logWriter) {
        $script:logWriter.WriteLine(("[{0}] {1}" -f $Stream, $plain))
        $script:logWriter.Flush()
    }
}

function Pump-CollectorOutput {
    do {
        $readAny = $false
        if ($null -ne $script:stdoutTask -and $script:stdoutTask.IsCompleted) {
            $line = $script:stdoutTask.GetAwaiter().GetResult()
            if ($null -ne $line) {
                Add-CollectorLine $line 'out'
                $script:stdoutTask = $script:process.StandardOutput.ReadLineAsync()
            } else {
                $script:stdoutTask = $null
            }
            $readAny = $true
        }
        if ($null -ne $script:stderrTask -and $script:stderrTask.IsCompleted) {
            $line = $script:stderrTask.GetAwaiter().GetResult()
            if ($null -ne $line) {
                Add-CollectorLine $line 'err'
                $script:stderrTask = $script:process.StandardError.ReadLineAsync()
            } else {
                $script:stderrTask = $null
            }
            $readAny = $true
        }
    } while ($readAny)
}

function Send-CollectorCommand([string]$Command) {
    if ($null -eq $script:process -or $script:process.HasExited) {
        throw "Collector process is not running; cannot send '$Command'."
    }
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Command + "`n")
    $script:process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $script:process.StandardInput.BaseStream.Flush()
}

function Get-CollectorProcessTreeIds([int]$RootProcessId) {
    if ($RootProcessId -le 0) { return @() }

    $snapshot = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Select-Object ProcessId, ParentProcessId)
    $owned = New-Object 'System.Collections.Generic.HashSet[int]'
    [void]$owned.Add($RootProcessId)

    do {
        $added = $false
        foreach ($candidate in $snapshot) {
            $candidateId = [int]$candidate.ProcessId
            if (-not $owned.Contains($candidateId) -and $owned.Contains([int]$candidate.ParentProcessId)) {
                [void]$owned.Add($candidateId)
                $added = $true
            }
        }
    } while ($added)

    return @($owned)
}

function Stop-CollectorProcessTree([Diagnostics.Process]$RootProcess) {
    if ($null -eq $RootProcess) { return }

    # HeadlessMC launches Minecraft as a child JVM. Stopping only the launcher
    # can orphan the much larger game JVM, leaving duplicate sessions for the
    # same account on every retry. Snapshot the complete owned tree before a
    # graceful quit, then guarantee that every surviving descendant is gone.
    $rootProcessId = [int]$RootProcess.Id
    $ownedProcessIds = @(Get-CollectorProcessTreeIds $rootProcessId)

    try {
        if (-not $RootProcess.HasExited) {
            Send-CollectorCommand 'quit'
            [void]$RootProcess.WaitForExit(10000)
        }
    } catch { }

    $ownedProcessIds = @($ownedProcessIds + @(Get-CollectorProcessTreeIds $rootProcessId) | Select-Object -Unique)
    [array]::Reverse($ownedProcessIds)
    foreach ($processId in $ownedProcessIds) {
        if ($processId -le 0 -or $processId -eq $PID) { continue }
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}

function Wait-CollectorMatch(
    [string[]]$Patterns,
    [int]$TimeoutSeconds,
    [int]$StartIndex = -1,
    [string[]]$ProgressPatterns = @(),
    [int]$MaximumSeconds = 0,
    [scriptblock]$ProgressGuard = $null
) {
    if ($StartIndex -lt 0) { $StartIndex = $script:lines.Count }
    $startedUtc = [DateTime]::UtcNow
    $idleDeadline = $startedUtc.AddSeconds($TimeoutSeconds)
    $maximumDeadline = if ($MaximumSeconds -gt 0) { $startedUtc.AddSeconds($MaximumSeconds) } else { [DateTime]::MaxValue }
    $next = $StartIndex
    while ([DateTime]::UtcNow -lt $idleDeadline -and [DateTime]::UtcNow -lt $maximumDeadline) {
        Pump-CollectorOutput
        while ($next -lt $script:lines.Count) {
            $line = $script:lines[$next]
            $next++
            foreach ($pattern in $Patterns) {
                $match = [regex]::Match($line, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($match.Success) {
                    return [pscustomobject]@{ Line = $line; Match = $match; NextIndex = $next }
                }
            }
            foreach ($progressPattern in $ProgressPatterns) {
                if ([regex]::IsMatch($line, $progressPattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
                    if ($null -ne $ProgressGuard) {
                        $guardReason = & $ProgressGuard $line
                        if (-not [string]::IsNullOrWhiteSpace([string]$guardReason)) {
                            throw "Adaptive runaway guard: $guardReason"
                        }
                    }
                    # Large Archive WDLs can legitimately need hours. Coverage progress renews
                    # the inactivity lease; MaximumSeconds remains a separate fail-closed fuse.
                    $idleDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
                    break
                }
            }
        }
        if ($script:process.HasExited) {
            throw "Collector process exited with code $($script:process.ExitCode) while waiting for: $($Patterns -join ' | ')"
        }
        Start-Sleep -Milliseconds 100
    }
    if ([DateTime]::UtcNow -ge $maximumDeadline) {
        throw "Adaptive maximum runtime of $MaximumSeconds second(s) reached before completion; quarantine for footprint review."
    }
    throw "Timed out after $TimeoutSeconds second(s) without matching progress while waiting for: $($Patterns -join ' | ')"
}

function Invoke-CollectorCommand(
    [string]$Command,
    [string[]]$Patterns,
    [int]$TimeoutSeconds
) {
    Pump-CollectorOutput
    $start = $script:lines.Count
    Send-CollectorCommand $Command
    return Wait-CollectorMatch $Patterns $TimeoutSeconds $start
}

function Connect-ArchiveServer {
    $result = Invoke-CollectorCommand "connect $Server" @(
        'joined the game',
        'Client disconnected with reason:',
        'Failed to connect',
        'Unable to connect to archive',
        'Invalid session',
        'not authenticated'
    ) 120
    if ($result.Line -notmatch 'joined the game') {
        throw "Archive connection failed: $($result.Line)"
    }
    $script:lastDisconnectScanIndex = $script:lines.Count
}

function Test-NewArchiveDisconnect {
    Pump-CollectorOutput
    $disconnected = $false
    for ($index = $script:lastDisconnectScanIndex; $index -lt $script:lines.Count; $index++) {
        if ($script:lines[$index] -match '(?i)Client disconnected|Connection reset|Disconnected from') {
            $disconnected = $true
        }
    }
    $script:lastDisconnectScanIndex = $script:lines.Count
    return $disconnected
}

function Repair-DisconnectedArchiveSession {
    if (-not (Test-NewArchiveDisconnect)) { return $false }
    if ($script:downloadActive) {
        throw 'Archive disconnected during an active WDL; the capture must remain retryable.'
    }
    Start-Sleep -Seconds 3
    Connect-ArchiveServer
    Start-Sleep -Seconds 8
    return $true
}

function Get-NormalizedWarp([string]$Value) {
    return ($Value.Trim() -replace '^/warp\s+', '' -replace '\s+', '_').ToLowerInvariant()
}

function Get-QueueDimension([object]$Entry, [string]$Fallback = 'Overworld') {
    $entryWarp = if ($null -ne $Entry.PSObject.Properties['warp']) { [string]$Entry.warp } else { '' }
    if ($entryWarp -match '(?i)@end$') { return 'End' }
    if ($entryWarp -match '(?i)@nether$') { return 'Nether' }
    $dimensionProperty = $Entry.PSObject.Properties['dimension']
    if ($null -ne $dimensionProperty -and [string]$dimensionProperty.Value -in @('Overworld', 'Nether', 'End')) {
        return [string]$dimensionProperty.Value
    }
    return $Fallback
}

function Get-DateInsensitiveWarpIdentity([string]$Value) {
    $normalized = Get-NormalizedWarp $Value
    $scope = ''
    $scopeIndex = $normalized.IndexOf('@')
    if ($scopeIndex -ge 0) {
        $scope = $normalized.Substring($scopeIndex)
        $normalized = $normalized.Substring(0, $scopeIndex)
    }
    $normalized = $normalized -replace '[_\-.](?:19|20)\d{2}(?:[_\-.]\d{1,2}){0,2}$', ''
    return $normalized.TrimEnd('_', '-', '.') + $scope
}

function Test-FinalAdaptiveCapture([object]$Record) {
    if ($null -eq $Record -or [string]$Record.status -notin @('captured', 'ready')) { return $false }
    $adaptiveProperty = $Record.PSObject.Properties['adaptive']
    if ($null -eq $adaptiveProperty -or $null -eq $adaptiveProperty.Value) { return $false }
    $adaptive = $adaptiveProperty.Value
    $versionProperty = $adaptive.PSObject.Properties['standardVersion']
    $selectionProperty = $adaptive.PSObject.Properties['componentSelection']
    return $null -ne $versionProperty -and [int]$versionProperty.Value -ge $collectorStandardVersion -and
        $null -ne $selectionProperty -and $null -ne $selectionProperty.Value
}

function ConvertTo-AtlasDimension([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $normalized = $Value.Trim().ToLowerInvariant()
    if ($normalized -eq 'minecraft:overworld') { return 'Overworld' }
    if ($normalized -eq 'minecraft:the_nether' -or
        $normalized -match '(?:^|[/_.:-])(?:the_)?nether(?:$|[/_.:-])') { return 'Nether' }
    if ($normalized -eq 'minecraft:the_end' -or
        $normalized -match '(?:^|[/_.:-])(?:the_)?end(?:$|[/_.:-])') { return 'End' }
    return ''
}

function Get-SafeCaptureName([string]$WarpName) {
    $safe = ($WarpName -replace '[^A-Za-z0-9._-]+', '_').Trim(' ', '.', '_')
    if ([string]::IsNullOrWhiteSpace($safe)) { $safe = 'archive-wdl' }
    if ($safe.Length -gt 90) { $safe = $safe.Substring(0, 90).TrimEnd('.', '_', '-') }
    $bytes = [Text.Encoding]::UTF8.GetBytes((Get-NormalizedWarp $WarpName))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $suffix = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').Substring(0, 10).ToLowerInvariant() }
    finally { $sha.Dispose() }
    return "archive-$safe-$suffix"
}

function Get-FreeGiB([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $drive = New-Object IO.DriveInfo($root)
    return [math]::Round($drive.AvailableFreeSpace / 1GB, 1)
}

function Remove-TransientWdlCapture([string]$CaptureName, [switch]$DurableCapture) {
    if (-not $DurableCapture) { throw 'Refusing cleanup without a verified durable capture.' }
    if ([string]::IsNullOrWhiteSpace($CaptureName)) { return }
    $root = [IO.Path]::GetFullPath($savesRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($candidate in @(
        (Join-Path $savesRoot $CaptureName),
        (Join-Path $savesRoot "$CaptureName.zip")
    )) {
        $full = [IO.Path]::GetFullPath($candidate)
        if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Transient WDL cleanup escaped the saves root: $full"
        }
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            Remove-Item -LiteralPath $full -Force
        } elseif (Test-Path -LiteralPath $full -PathType Container) {
            Remove-Item -LiteralPath $full -Recurse -Force
        }
    }
}

function Wait-StableFile([string]$Path, [int]$TimeoutSeconds = 180) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastLength = -1L
    $stable = 0
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            $length = (Get-Item -LiteralPath $Path).Length
            if ($length -gt 0 -and $length -eq $lastLength) { $stable++ } else { $stable = 0 }
            if ($stable -ge 3) { return }
            $lastLength = $length
        }
        Start-Sleep -Seconds 1
    }
    throw "Output ZIP did not stabilize: $Path"
}

function Test-CaptureZip([string]$Path, [string]$CaptureName) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $prefix = "$CaptureName/"
        $names = @($archive.Entries | ForEach-Object FullName)
        if (-not ($names -contains "${prefix}level.dat")) { throw 'Capture ZIP has no level.dat.' }
        if (-not ($names -contains "${prefix}wdl/download.jsonl")) { throw 'Capture ZIP has no WDL JSON report.' }
        if (-not ($names | Where-Object { $_ -match "^$([regex]::Escape($prefix))(?:DIM-1/|DIM1/)?region/r\.-?\d+\.-?\d+\.mca$" })) {
            throw 'Capture ZIP has no Anvil region data.'
        }
        $reportEntry = $archive.GetEntry("${prefix}wdl/download.jsonl")
        $reader = New-Object IO.StreamReader($reportEntry.Open())
        try { $report = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($report -notmatch '"status"\s*:\s*"complete"') { throw 'WDL report is not complete.' }
        $completedReport = $null
        foreach ($line in @($report -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })) {
            try { $parsed = $line | ConvertFrom-Json } catch { continue }
            if ([string]$parsed.status -eq 'complete') { $completedReport = $parsed }
        }
        if ($null -eq $completedReport) { throw 'WDL report has no parseable completion record.' }
        $downloadDimensionName = [string]$completedReport.dimensionName
        $downloadDimension = ConvertTo-AtlasDimension $downloadDimensionName
        if ([string]::IsNullOrWhiteSpace($downloadDimension)) {
            throw "WDL report has an unrecognized completed dimension '$downloadDimensionName'."
        }
        return [pscustomobject]@{
            Entries = $archive.Entries.Count
            RegionFiles = @($archive.Entries | Where-Object FullName -match '/region/r\.-?\d+\.-?\d+\.mca$').Count
            Dimension = $downloadDimension
            DimensionName = $downloadDimensionName
        }
    } finally {
        $archive.Dispose()
    }
}

function Complete-WdlCapture([string]$CaptureName) {
    $status = Invoke-CollectorCommand 'msg /wdl status' @('Downloading:\s+chunks\s+(\d+)') 30
    $chunks = [int]$status.Match.Groups[1].Value
    $finished = Invoke-CollectorCommand 'msg /wdl stop' @(
        'Downloaded\s+.+:\s+chunks\s+(\d+)',
        'Save failed:\s*(.+)'
    ) 300
    if ($finished.Line -match 'Save failed:') { throw "WDL save failed: $($finished.Line)" }
    $script:downloadActive = $false
    $savedChunks = [int]$finished.Match.Groups[1].Value
    $hasResumeSeed = $null -ne $script:resumeCapture -and $script:resumeCapture.CaptureName -eq $CaptureName
    if (($savedChunks -lt 1 -or $chunks -lt 1) -and -not $hasResumeSeed) { throw 'WDL completed without chunks.' }
    # A fully restored survey may need no new terrain. Its completed empty
    # continuation can still merge with the verified parent and face the full audit.

    $zipPath = Join-Path $savesRoot "$CaptureName.zip"
    Wait-StableFile $zipPath 300
    return Merge-ResumedWdlCapture ([pscustomobject]@{
        CaptureName = $CaptureName
        Chunks = $chunks
        SavedChunks = $savedChunks
        ZipPath = $zipPath
    })
}

function Copy-VerifiedAtomically([string]$Source, [string]$Destination, [string]$ExpectedSha256) {
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Reuse source is missing: $Source" }
    $expected = $ExpectedSha256.ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "Reuse source failed SHA-256 verification: $Source" }
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existing = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing -ne $expected) { throw "Reuse destination conflicts with a different artifact: $Destination" }
        return
    }
    $partial = "$Destination.$([guid]::NewGuid().ToString('N')).partial"
    try {
        Copy-Item -LiteralPath $Source -Destination $partial
        $copied = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copied -ne $expected) { throw "Reused artifact copy failed SHA-256 verification: $Source" }
        Move-Item -LiteralPath $partial -Destination $Destination
    } finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
    }
}

function Test-ManifestNearPosition([string]$Path, [double]$BlockX, [double]$BlockZ, [int]$RadiusChunks = 2) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $targetChunkX = [Math]::Floor($BlockX / 16.0)
    $targetChunkZ = [Math]::Floor($BlockZ / 16.0)
    foreach ($line in [IO.File]::ReadAllLines($Path, (New-Object Text.UTF8Encoding($false)))) {
        if ($line -notmatch '^(-?\d+),(-?\d+)$') { continue }
        if ([Math]::Abs([int]$Matches[1] - $targetChunkX) -le $RadiusChunks -and
            [Math]::Abs([int]$Matches[2] - $targetChunkZ) -le $RadiusChunks) { return $true }
    }
    return $false
}

function Find-ReusableAdaptiveCapture(
    [hashtable]$StateByWarp,
    [string]$Identity,
    [string]$LiveDimensionId,
    [double]$BlockX,
    [double]$BlockZ
) {
    if ([string]::IsNullOrWhiteSpace($LiveDimensionId)) { return $null }
    foreach ($prior in @($StateByWarp.Values)) {
        $requiredRecordProperties = @(
            'warp', 'normalizedWarp', 'status', 'locationName', 'path', 'sha256',
            'chunks', 'archiveBytes', 'zipEntries', 'regionFiles'
        )
        if (@($requiredRecordProperties | Where-Object { $null -eq $prior.PSObject.Properties[$_] }).Count -gt 0) { continue }
        if ([string]$prior.normalizedWarp -eq $Identity -or
            [string]$prior.status -notin @('captured', 'ready')) { continue }
        $adaptiveProperty = $prior.PSObject.Properties['adaptive']
        if ($null -eq $adaptiveProperty) { continue }
        $adaptive = $adaptiveProperty.Value
        $requiredProperties = @(
            'liveDimensionId', 'missingChunks', 'surveyMissingChunks', 'maxRadiusReached',
            'bounds', 'otherKnownWarpsInsideBounds', 'archiveWarpPosition',
            'initialIngestionPolicy', 'nonVoidManifest', 'footprintArtifact', 'dimension',
            'standardVersion', 'componentSelection'
        )
        $missingRequired = @($requiredProperties | Where-Object { $null -eq $adaptive.PSObject.Properties[$_] })
        if ($missingRequired.Count -gt 0) { continue }
        if ($null -eq $adaptive.archiveWarpPosition -or $null -eq $adaptive.initialIngestionPolicy -or
            $null -eq $adaptive.nonVoidManifest -or $null -eq $adaptive.footprintArtifact) { continue }
        if (@('x', 'y', 'z') | Where-Object {
                $null -eq $adaptive.archiveWarpPosition.PSObject.Properties[$_]
            }) { continue }
        if ($null -eq $adaptive.initialIngestionPolicy.PSObject.Properties['eligible']) { continue }
        if (@('path', 'sha256') | Where-Object {
                $null -eq $adaptive.nonVoidManifest.PSObject.Properties[$_]
            }) { continue }
        if (@('path', 'sha256', 'bytes', 'headerChunks', 'exactBoundsOnly') | Where-Object {
                $null -eq $adaptive.footprintArtifact.PSObject.Properties[$_]
            }) { continue }
        if ([int]$adaptive.standardVersion -lt $collectorStandardVersion -or
            [string]$adaptive.liveDimensionId -ne $LiveDimensionId -or
            [int]$adaptive.missingChunks -ne 0 -or
            [int]$adaptive.surveyMissingChunks -ne 0 -or
            [bool]$adaptive.maxRadiusReached -or
            -not [bool]$adaptive.footprintArtifact.exactBoundsOnly) { continue }
        $bounds = @($adaptive.bounds)
        if ($bounds.Count -ne 4 -or $BlockX -lt [double]$bounds[0] -or $BlockX -ge [double]$bounds[2] -or
            $BlockZ -lt [double]$bounds[1] -or $BlockZ -ge [double]$bounds[3]) { continue }
        $listedNeighbor = @($adaptive.otherKnownWarpsInsideBounds | Where-Object {
            [string]$_.normalizedWarp -eq $Identity
        }).Count -gt 0
        if (-not $listedNeighbor) { continue }
        $manifestPath = [string]$adaptive.nonVoidManifest.path
        if (-not (Test-ManifestNearPosition $manifestPath $BlockX $BlockZ 2)) { continue }
        return $prior
    }
    return $null
}

foreach ($required in @($java, $launcher, $QueuePath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file was not found: $required" }
}
foreach ($directory in @($CapturedRoot, $ReadyRoot, $savesRoot, (Join-Path $InstallRoot 'logs'))) {
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
}

$state = [ordered]@{ schemaVersion = 1; updatedUtc = [DateTime]::UtcNow.ToString('o'); entries = @() }
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $state = Read-JsonUtf8 $StatePath
}
$stateByWarp = @{}
foreach ($entry in @($state.entries)) {
    if ($null -ne $entry -and -not [string]::IsNullOrWhiteSpace([string]$entry.warp)) {
        $stateByWarp[(Get-NormalizedWarp ([string]$entry.warp))] = $entry
    }
}

$queue = Read-JsonUtf8 $QueuePath
if (@($queue.conflicts).Count -gt 0) { throw 'Collector queue contains normalized-warp conflicts.' }
Recover-CaptureJournal
$requestedWarps = @($Warp | ForEach-Object { ([string]$_).Trim() } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($requestedWarps.Count -eq 0) {
    $requestedWarps = @(Get-RequestedCollectorRetries -RequestPath (Join-Path (Split-Path -Parent $StatePath) 'retry-warps.json') -Queue $queue -StateByWarp $stateByWarp)
}
$candidates = @(if ($requestedWarps.Count -gt 0) {
    foreach ($requestedWarp in $requestedWarps) {
        $requestedIdentity = Get-NormalizedWarp $requestedWarp
        $prior = $stateByWarp[$requestedIdentity]
        if ($null -ne $prior) {
            $priorStatus = [string]$prior.status
            if (($priorStatus -eq 'skipped-inside-background' -and $AdaptiveBackgroundRadiusBlocks -gt 0) -or
                ($priorStatus -in @('captured', 'ready') -and (Test-FinalAdaptiveCapture $prior))) {
                throw "Warp '$requestedWarp' is already $priorStatus; refusing to recapture it."
            }
            if ($priorStatus -eq 'missing' -and -not $RecheckMissing) {
                throw "Warp '$requestedWarp' is recorded missing; use -RecheckMissing to probe it again."
            }
        }

        $queueMatches = @($queue.entries | Where-Object { [string]$_.normalizedWarp -eq $requestedIdentity })
        if ($queueMatches.Count -gt 1) { throw "Warp '$requestedWarp' appears more than once in the collector queue." }
        if ($queueMatches.Count -eq 1) {
            $queueMatches[0]
        } else {
            [pscustomobject]@{
                warp = $requestedWarp
                normalizedWarp = $requestedIdentity
                locationName = ''
                locationUuid = ''
                x = 0
                y = 0
                z = 0
                source = 'operator'
                archiveDisplayName = ''
                archiveCategoryPath = @()
                archiveDiscoveredUtc = ''
                archiveLastSeenUtc = ''
            }
        }
    }
} else {
    @($queue.entries | Where-Object {
        $prior = $stateByWarp[[string]$_.normalizedWarp]
        if ($null -eq $prior) {
            $true
        } else {
            $priorStatus = [string]$prior.status
            ($priorStatus -ne 'skipped-inside-background' -or $AdaptiveBackgroundRadiusBlocks -eq 0) -and
                ($priorStatus -notin @('captured', 'ready') -or -not (Test-FinalAdaptiveCapture $prior)) -and
                $priorStatus -ne 'needs-footprint-review' -and
                ($RecheckRetryable -or $priorStatus -ne 'retryable') -and
                (-not $PreflightOnly -or $priorStatus -ne 'preflight-eligible') -and
                ($RecheckMissing -or $priorStatus -ne 'missing')
        }
    } | Select-Object -First $MaxWarps)
})
if ($candidates.Count -eq 0) {
    Write-Output 'No eligible Archive warps remain in the selected queue.'
    exit 0
}
if (-not [string]::IsNullOrWhiteSpace($ExitAfterCurrentWarpSignalPath) -and
    (Test-Path -LiteralPath $ExitAfterCurrentWarpSignalPath -PathType Leaf)) {
    # The queue was loaded after the signal was created, so this launch already
    # sees the deferred work and does not need an immediate recycle.
    Remove-Item -LiteralPath $ExitAfterCurrentWarpSignalPath -Force
}

$lockPath = "$StatePath.lock"
$lockParent = Split-Path -Parent $lockPath
if (-not (Test-Path -LiteralPath $lockParent)) { New-Item -ItemType Directory -Path $lockParent -Force | Out-Null }
$lockOwner = @()
if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
    try {
        $lockOwner = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
            [int]$_.ProcessId -ne $PID -and
            -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
            [string]$_.CommandLine -match '(?i)invoke-archive-collector\.ps1' -and
            ([string]$_.CommandLine).IndexOf($StatePath, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
    } catch {
        throw "Could not verify the owner of collector lock $lockPath`: $($_.Exception.Message)"
    }
    if ($lockOwner.Count -eq 0) {
        $stalePath = "$lockPath.stale-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$PID"
        Move-Item -LiteralPath $lockPath -Destination $stalePath
        Write-Warning "Recovered orphaned collector lock after an interrupted host/process: $stalePath"
    }
}
$lock = $null
try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) }
catch [IO.IOException] { throw 'Another Archive collector is already running.' }

$logPath = Join-Path $InstallRoot ("logs\collector-{0}.log" -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$script:logWriter = New-Object IO.StreamWriter($logPath, $false, (New-Object Text.UTF8Encoding($false)))

try {
    $freeGiB = Get-FreeGiB $savesRoot
    if ($freeGiB -lt $MinimumFreeGiB) { throw "Collector drive has only $freeGiB GiB free; minimum is $MinimumFreeGiB GiB." }
    Preserve-PendingInterruptedCaptures -StateByWarp $stateByWarp -State $state -StatePath $StatePath -SavesRoot $savesRoot
    Write-Output 'COLLECTOR-RECOVERY disk-resume-v1: journaled saves, flush-safe restart, missing-only repair'
    Write-Output 'COLLECTOR-SAFETY interrupted-retention-v1: failed saves retained; verified private copies on D; durable-success-only cleanup.'
    Install-PendingArchiveCoverage $InstallRoot
    Write-Output 'COLLECTOR-RESUME saved-terrain-v1: private terrain and void checkpoints; chunk-slot union before final audit.'

    $arguments = @(
        '-Dhmc.jline.enabled=false',
        "-Dhmc.mcdir=$minecraftRoot",
        "-Dhmc.gamedir=$gameRoot",
        '-jar',
        $launcher
    ) | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $java
    $startInfo.Arguments = $arguments -join ' '
    $startInfo.WorkingDirectory = $InstallRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8

    $script:process = New-Object Diagnostics.Process
    $script:process.StartInfo = $startInfo
    if (-not $script:process.Start()) { throw 'HeadlessMc failed to start.' }
    $script:stdoutTask = $script:process.StandardOutput.ReadLineAsync()
    $script:stderrTask = $script:process.StandardError.ReadLineAsync()

    # Windows PowerShell 5.1 constructs redirected StandardInput with a UTF-8 BOM.
    # Consume that preamble as an empty launcher command before sending real input.
    Send-CollectorCommand ''
    Start-Sleep -Seconds 2
    Send-CollectorCommand "launch $version -lwjgl --jvm -Xmx3G"
    $initialized = Wait-CollectorMatch @('HMC-Specifics initialized!', 'Invalid credentials', 'Failed to log in') 360

    # On 1.21.11 HMC-Specifics initializes before Minecraft's first resource
    # reload has finished. A connect command sent in that window opens a login
    # socket, but the subsequent screen/resource transition silently cancels it
    # without a disconnect message. Wait for the final vanilla texture atlas
    # stitch instead of using a fixed delay (the reload time varies sharply when
    # five clients start together), then give the title screen a short settle.
    Wait-CollectorMatch @('Created: .+minecraft:textures/atlas/shulker_boxes\.png-atlas') 180 $initialized.NextIndex | Out-Null
    Start-Sleep -Seconds 3
    Connect-ArchiveServer

    $candidateOrdinal = 0
    foreach ($candidate in $candidates) {
        if ($candidateOrdinal -gt 0 -and
            -not [string]::IsNullOrWhiteSpace($ExitAfterCurrentWarpSignalPath) -and
            (Test-Path -LiteralPath $ExitAfterCurrentWarpSignalPath -PathType Leaf)) {
            Remove-Item -LiteralPath $ExitAfterCurrentWarpSignalPath -Force
            Write-Output 'SAFE-QUEUE-RELOAD requested after completed WDL boundary.'
            break
        }
        $candidateOrdinal++
        if (Repair-DisconnectedArchiveSession) {
            Write-Warning 'RECONNECTED Archive client before the next selected capture.'
        }
        $warpName = [string]$candidate.warp
        $script:resumeCapture = $null
        $identity = Get-NormalizedWarp $warpName
        $dateInsensitiveIdentity = Get-DateInsensitiveWarpIdentity $warpName
        $adaptiveCaptureDimension = Get-QueueDimension $candidate $AdaptiveDimension
        $adaptiveCapturePolicy = Get-ArchiveAdaptiveCapturePolicy $candidate
        $captureName = Get-SafeCaptureName $warpName
        if ($coverageEnabled) {
            $captureName += '-coverage-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
        } elseif ($adaptiveEnabled) {
            $captureName += '-adaptive-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
        }
        $startedUtc = [DateTime]::UtcNow
        Write-Output "WARP $warpName"
        try {
            if ($coverageEnabled -or $adaptiveEnabled) {
                Invoke-CollectorCommand 'msg /atlascover reset' @('ATLAS_COVER reset dimension=') 30 | Out-Null
            }
            $warpResult = Invoke-CollectorCommand "msg /warp $warpName" @(
                "You were teleported to\s+'([^']+)'",
                'warp does not exist',
                'Bad command',
                'teleport.*cooldown',
                'permission'
            ) 45
            if ($warpResult.Line -match 'warp does not exist') {
                $record = [ordered]@{ warp = $warpName; normalizedWarp = $identity; status = 'missing'; checkedUtc = [DateTime]::UtcNow.ToString('o') }
                $stateByWarp[$identity] = [pscustomobject]$record
                $state.entries = @($stateByWarp.Values | Sort-Object normalizedWarp)
                $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
                Save-JsonAtomically $state $StatePath
                Write-Output "MISSING $warpName"
                continue
            }
            if ($warpResult.Line -notmatch 'You were teleported to') { throw "Warp failed: $($warpResult.Line)" }

            Start-Sleep -Seconds 5
            $coverageProbe = $null
            if ($coverageEnabled -or $adaptiveEnabled) {
                $coverageProbe = Invoke-CollectorCommand 'msg /atlascover status' @('ATLAS_COVER status ingame=true') 30
                Write-Output $coverageProbe.Line
            }
            $coverageResult = $null
            $adaptiveDiscoveryResult = $null
            $adaptiveCaptureResult = $null
            $adaptiveCaptureStrategy = ''
            $adaptiveFallbackReason = ''
            $adaptiveCaptureMissing = 0
            $adaptiveCaptureReceived = 0
            $adaptiveCaptureWaypoints = 0
            $adaptiveCaptureRepairPasses = 0
            $adaptiveCaptureBounds = @()
            $adaptiveNeighborWarps = @()
            $adaptiveManifestDigest = ''
            $adaptiveManifestDestination = ''
            $adaptiveEffectiveConfidence = ''
            $adaptiveBoundarySkipped = 0
            $adaptiveWarpX = 0.0
            $adaptiveWarpY = 0.0
            $adaptiveWarpZ = 0.0
            $adaptiveWarpDistanceBlocks = 0.0
            $adaptiveOutsideBackgroundRadius = $false
            $adaptiveInitialIngestionEligible = $false
            $adaptiveLiveDimensionId = ''
            $activeCaptureName = $captureName
            if ($adaptiveEnabled) {
                $dimensionMatch = [regex]::Match([string]$coverageProbe.Line, '\sdimension=([^\s]+)\s')
                if ($dimensionMatch.Success) {
                    $adaptiveLiveDimensionId = $dimensionMatch.Groups[1].Value
                    $liveDimension = ConvertTo-AtlasDimension $adaptiveLiveDimensionId
                    if (-not [string]::IsNullOrWhiteSpace($liveDimension) -and
                        $liveDimension -ne $adaptiveCaptureDimension) {
                        Write-Output "DIMENSION-RESOLVED $warpName catalog=$adaptiveCaptureDimension live=$liveDimension raw=$adaptiveLiveDimensionId"
                        $adaptiveCaptureDimension = $liveDimension
                    }
                }
                $positionMatch = [regex]::Match(
                    [string]$coverageProbe.Line,
                    '\sx=(-?\d+(?:\.\d+)?)\sy=(-?\d+(?:\.\d+)?)\sz=(-?\d+(?:\.\d+)?)\s')
                if (-not $positionMatch.Success) {
                    throw "Adaptive warp probe did not expose parseable coordinates: $($coverageProbe.Line)"
                }
                $adaptiveWarpX = [double]::Parse($positionMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
                $adaptiveWarpY = [double]::Parse($positionMatch.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
                $adaptiveWarpZ = [double]::Parse($positionMatch.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
                $adaptiveWarpDistanceBlocks = [Math]::Sqrt(
                    $adaptiveWarpX * $adaptiveWarpX + $adaptiveWarpZ * $adaptiveWarpZ)
                $adaptiveOutsideBackgroundRadius =
                    $AdaptiveBackgroundRadiusBlocks -eq 0 -or
                    $adaptiveWarpDistanceBlocks -ge $AdaptiveBackgroundRadiusBlocks

                if ($PreflightOnly -or -not $adaptiveOutsideBackgroundRadius) {
                    $preflightStatus = if ($adaptiveOutsideBackgroundRadius) {
                        'preflight-eligible'
                    } else {
                        'skipped-inside-background'
                    }
                    $record = [ordered]@{
                        warp = $warpName
                        normalizedWarp = $identity
                        status = $preflightStatus
                        sourceServer = $Server
                        queueSource = [string]$candidate.source
                        archiveDisplayName = [string]$candidate.archiveDisplayName
                        archiveCategoryPath = @($candidate.archiveCategoryPath)
                        archiveDiscoveredUtc = [string]$candidate.archiveDiscoveredUtc
                        archiveLastSeenUtc = [string]$candidate.archiveLastSeenUtc
                        checkedUtc = [DateTime]::UtcNow.ToString('o')
                        archiveWarpPosition = [ordered]@{
                            x = $adaptiveWarpX
                            y = $adaptiveWarpY
                            z = $adaptiveWarpZ
                            distanceFromOriginBlocks = [Math]::Round($adaptiveWarpDistanceBlocks, 2)
                        }
                        backgroundRadiusBlocks = $AdaptiveBackgroundRadiusBlocks
                    }
                    $stateByWarp[$identity] = [pscustomobject]$record
                    $state.entries = @($stateByWarp.Values | Sort-Object normalizedWarp)
                    $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
                    Save-JsonAtomically $state $StatePath
                    if ($adaptiveOutsideBackgroundRadius) {
                        Write-Output ("PREFLIGHT-ELIGIBLE {0} distance={1:N0}" -f $warpName, $adaptiveWarpDistanceBlocks)
                    } else {
                        Write-Output ("SKIP-INSIDE-BACKGROUND {0} distance={1:N0} radius={2:N0}" -f
                            $warpName, $adaptiveWarpDistanceBlocks, $AdaptiveBackgroundRadiusBlocks)
                    }
                    continue
                }

                # A few Archive custom dimensions expose several catalog warps inside the
                # same immutable backing WDL. Reuse is deliberately strict: the prior
                # capture must have named this exact warp as a neighbor, share the live
                # dimension id, cover the actual landing coordinate, contain nearby
                # non-void chunks, and pass every stored SHA/exactness invariant.
                $reuseSource = Find-ReusableAdaptiveCapture $stateByWarp $identity `
                    $adaptiveLiveDimensionId $adaptiveWarpX $adaptiveWarpZ
                if ($null -ne $reuseSource) {
                    $reuseBaseName = "$captureName-reused"
                    $reuseRawPath = Join-Path $CapturedRoot "$reuseBaseName.zip"
                    $reuseManifestPath = "$reuseRawPath.nonvoid.csv"
                    $reuseFootprintPath = Join-Path $CapturedRoot "$reuseBaseName.footprint.zip"
                    $sourceAdaptive = $reuseSource.adaptive
                    Copy-VerifiedAtomically ([string]$reuseSource.path) $reuseRawPath ([string]$reuseSource.sha256)
                    Copy-VerifiedAtomically ([string]$sourceAdaptive.nonVoidManifest.path) $reuseManifestPath `
                        ([string]$sourceAdaptive.nonVoidManifest.sha256)
                    Copy-VerifiedAtomically ([string]$sourceAdaptive.footprintArtifact.path) $reuseFootprintPath `
                        ([string]$sourceAdaptive.footprintArtifact.sha256)

                    $reusedAdaptive = ($sourceAdaptive | ConvertTo-Json -Depth 12) | ConvertFrom-Json
                    $reusedAdaptive.nonVoidManifest.path = $reuseManifestPath
                    $reusedAdaptive.footprintArtifact.path = $reuseFootprintPath
                    $reusedAdaptive.footprintArtifact.sourceCapturePath = $reuseRawPath
                    $reusedAdaptive.footprintArtifact.sourceCaptureSha256 = ([string]$reuseSource.sha256).ToLowerInvariant()
                    $reusedAdaptive.archiveWarpPosition = [pscustomobject][ordered]@{
                        x = $adaptiveWarpX
                        y = $adaptiveWarpY
                        z = $adaptiveWarpZ
                        distanceFromOriginBlocks = [Math]::Round($adaptiveWarpDistanceBlocks, 2)
                    }
                    $reusedAdaptive.probe = [string]$coverageProbe.Line
                    $reusedAdaptive.confidence = 'low'
                    $reusedAdaptive.initialIngestionPolicy.eligible = $false
                    $reusedNeighbors = @($reusedAdaptive.otherKnownWarpsInsideBounds | Where-Object {
                        [string]$_.normalizedWarp -ne $identity
                    })
                    $reusedNeighbors += [pscustomobject][ordered]@{
                        warp = [string]$reuseSource.warp
                        normalizedWarp = [string]$reuseSource.normalizedWarp
                        locationName = [string]$reuseSource.locationName
                        dimension = [string]$sourceAdaptive.dimension
                        x = [double]$sourceAdaptive.archiveWarpPosition.x
                        y = [double]$sourceAdaptive.archiveWarpPosition.y
                        z = [double]$sourceAdaptive.archiveWarpPosition.z
                    }
                    $reusedAdaptive.otherKnownWarpsInsideBounds = @($reusedNeighbors)
                    $reusedAdaptive | Add-Member -NotePropertyName captureStrategy `
                        -NotePropertyValue 'reused-covered-artifact' -Force
                    $reusedAdaptive | Add-Member -NotePropertyName reusedFromWarp `
                        -NotePropertyValue ([string]$reuseSource.warp) -Force
                    $reusedAdaptive | Add-Member -NotePropertyName reusedFromSha256 `
                        -NotePropertyValue ([string]$sourceAdaptive.footprintArtifact.sha256) -Force

                    $reuseCompletedUtc = [DateTime]::UtcNow.ToString('o')
                    $record = [ordered]@{
                        warp = $warpName
                        normalizedWarp = $identity
                        status = 'captured'
                        captureMode = 'adaptive-footprint'
                        sourceServer = $Server
                        queueSource = [string]$candidate.source
                        archiveDisplayName = [string]$candidate.archiveDisplayName
                        archiveCategoryPath = @($candidate.archiveCategoryPath)
                        archiveDiscoveredUtc = [string]$candidate.archiveDiscoveredUtc
                        archiveLastSeenUtc = [string]$candidate.archiveLastSeenUtc
                        locationUuid = [string]$candidate.locationUuid
                        locationName = [string]$candidate.locationName
                        atlasX = [int]$candidate.x
                        atlasY = [int]$candidate.y
                        atlasZ = [int]$candidate.z
                        startedUtc = $startedUtc.ToString('o')
                        completedUtc = $reuseCompletedUtc
                        chunks = [int]$reuseSource.chunks
                        archiveBytes = (Get-Item -LiteralPath $reuseRawPath).Length
                        sha256 = ([string]$reuseSource.sha256).ToLowerInvariant()
                        path = $reuseRawPath
                        zipEntries = [int]$reuseSource.zipEntries
                        regionFiles = [int]$reuseSource.regionFiles
                        adaptive = $reusedAdaptive
                    }
                    $rawMetadata = [ordered]@{
                        schemaVersion = 1
                        warp = $warpName
                        normalizedWarp = $identity
                        sourceServer = $Server
                        queueSource = [string]$candidate.source
                        displayName = [string]$candidate.archiveDisplayName
                        categoryPath = @($candidate.archiveCategoryPath)
                        catalogDiscoveredUtc = [string]$candidate.archiveDiscoveredUtc
                        catalogLastSeenUtc = [string]$candidate.archiveLastSeenUtc
                        captureMode = 'adaptive-footprint'
                        capturedUtc = $reuseCompletedUtc
                        sha256 = [string]$record.sha256
                        archiveBytes = [long]$record.archiveBytes
                        chunks = [int]$record.chunks
                        adaptive = $reusedAdaptive
                    }
                    Save-JsonAtomically $rawMetadata "$reuseRawPath.metadata.json"
                    $footprintMetadata = [ordered]@{
                        schemaVersion = 1
                        warp = $warpName
                        normalizedWarp = $identity
                        sourceServer = $Server
                        queueSource = [string]$candidate.source
                        displayName = [string]$candidate.archiveDisplayName
                        categoryPath = @($candidate.archiveCategoryPath)
                        catalogDiscoveredUtc = [string]$candidate.archiveDiscoveredUtc
                        catalogLastSeenUtc = [string]$candidate.archiveLastSeenUtc
                        captureMode = 'adaptive-footprint'
                        capturedUtc = $reuseCompletedUtc
                        sha256 = [string]$reusedAdaptive.footprintArtifact.sha256
                        archiveBytes = [long]$reusedAdaptive.footprintArtifact.bytes
                        chunks = [int]$reusedAdaptive.footprintArtifact.headerChunks
                        sourceCapturePath = $reuseRawPath
                        sourceCaptureSha256 = [string]$record.sha256
                        adaptive = $reusedAdaptive
                    }
                    Save-JsonAtomically $footprintMetadata "$reuseFootprintPath.metadata.json"
                    $stateByWarp[$identity] = [pscustomobject]$record
                    $state.entries = @($stateByWarp.Values | Sort-Object normalizedWarp)
                    $state.updatedUtc = $reuseCompletedUtc
                    Save-JsonAtomically $state $StatePath
                    Write-Output "REUSED-COVERED-WDL $warpName <- $($reuseSource.warp) [$($record.sha256.Substring(0, 12))]"
                    if ($DelayBetweenWarpsSeconds -gt 0) { Start-Sleep -Seconds $DelayBetweenWarpsSeconds }
                    continue
                }

                $spectator = Invoke-CollectorCommand 'msg /gmsp' @(
                    'Set game mode\s+spectator',
                    'Bad command',
                    'permission'
                ) 30
                if ($spectator.Line -notmatch 'Set game mode\s+spectator') {
                    throw "Archive spectator mode failed: $($spectator.Line)"
                }
            }
            if ($coverageEnabled) {
                Invoke-CollectorCommand "msg /wdl start $captureName" @('Downloading\s+') 30 | Out-Null
                $script:downloadActive = $true
                $routeCommand = 'msg /atlascover route {0} {1} {2} {3} {4} {5}' -f
                    $CoverageBounds[0], $CoverageBounds[1], $CoverageBounds[2], $CoverageBounds[3],
                    $CoverageTerrainRadiusChunks, $CoverageStepChunks
                Invoke-CollectorCommand $routeCommand @('ATLAS_COVER route-start ') 30 | Out-Null
                $coverageResult = Wait-CollectorMatch @(
                    'ATLAS_COVER complete exact=(true|false) target=(\d+) missing=(\d+) received=(\d+) waypoints=(\d+)(?: repairPasses=(\d+))?',
                    'ATLAS_COVER cancelled reason=([^\s]+)'
                ) $CoverageTimeoutSeconds
                if ($coverageResult.Line -match 'ATLAS_COVER cancelled') {
                    throw "Coverage route failed: $($coverageResult.Line)"
                }
                if ($coverageResult.Match.Groups[1].Value -ne 'true') {
                    throw "Coverage route did not receive every target chunk: $($coverageResult.Line)"
                }
                Write-Output $coverageResult.Line
            } elseif ($adaptiveEnabled) {
                # Capture once, then repair only persisted gaps if the independent
                # full non-void audit finds any. Journal before starting the WDL.
                $activeCaptureName = "$captureName-discovery"
                Start-TrackedWdlCapture $activeCaptureName
                $adaptiveCommand = 'msg /atlascover adaptive {0} {1} {2} {3} {4}' -f
                    $AdaptiveCoreRadiusBlocks, $AdaptiveExpansionBlocks, $AdaptiveMaxRadiusBlocks,
                    $AdaptiveTerrainRadiusChunks, $AdaptiveStepChunks
                if (-not (Restore-SurveyHandoff $warpName $activeCaptureName)) {
                    Invoke-CollectorCommand $adaptiveCommand @('ATLAS_COVER adaptive-start ') 30 | Out-Null
                }
                $adaptiveProgressGuard = {
                    param([string]$ProgressLine)
                    Get-ArchiveAdaptiveRunawayReason -Line $ProgressLine -Policy $adaptiveCapturePolicy
                }.GetNewClosure()
                $adaptiveDiscoveryResult = Wait-CollectorMatch -Patterns @(
                    'ATLAS_COVER adaptive-complete confidence=(high|medium|low) bounds=(-?\d+),(-?\d+)\.\.(-?\d+),(-?\d+) surveyBounds=(-?\d+),(-?\d+)\.\.(-?\d+),(-?\d+) target=(\d+) missing=(\d+) received=(\d+) nonvoid=(\d+) built=(\d+) strong=(\d+) componentBuild=(\d+) componentStrong=(\d+) orphanBuild=(\d+) artificialBlocks=(\d+) blockEntities=(\d+) iterations=(\d+) waypoints=(\d+) surveyTarget=(\d+) surveyMissing=(\d+) gapChunks=(\d+) marginChunks=(\d+) maxRadiusReached=(true|false) selection=(landing|dominant-fallback|none) components=(\d+) landingBuild=(\d+) landingStrong=(\d+) dominantBuild=(\d+) dominantStrong=(\d+) boundarySkipped=(\d+)',
                    'ATLAS_COVER cancelled reason=([^\s]+)'
                ) -TimeoutSeconds $AdaptiveTimeoutSeconds -ProgressPatterns @(
                    'ATLAS_COVER (?:settled waypoint=|coverage-skip waypoint=|adaptive-expand |adaptive-repair-start |teleporting waypoint=|boundary-skip waypoint=)'
                ) -MaximumSeconds $AdaptiveMaximumRuntimeSeconds -ProgressGuard $adaptiveProgressGuard
                if ($adaptiveDiscoveryResult.Line -match 'ATLAS_COVER cancelled') {
                    throw "Adaptive discovery failed: $($adaptiveDiscoveryResult.Line)"
                }
                $adaptiveBoundarySkipped = [int]$adaptiveDiscoveryResult.Match.Groups[34].Value
                if (([int]$adaptiveDiscoveryResult.Match.Groups[11].Value -ne 0 -or
                    [int]$adaptiveDiscoveryResult.Match.Groups[24].Value -ne 0) -and
                    $adaptiveBoundarySkipped -eq 0) {
                    throw "Adaptive discovery did not receive every capture/survey chunk: $($adaptiveDiscoveryResult.Line)"
                }
                if ($adaptiveBoundarySkipped -gt 0) {
                    Write-Warning (("Adaptive discovery was limited by {0} Archive /tppos coordinate-boundary waypoint(s). " +
                        "The capture will be preserved at low confidence for manual review and will not be auto-promoted.") -f
                        $adaptiveBoundarySkipped)
                }
                Write-Output $adaptiveDiscoveryResult.Line

                $adaptiveCaptureBounds = @(
                    [int]$adaptiveDiscoveryResult.Match.Groups[2].Value,
                    [int]$adaptiveDiscoveryResult.Match.Groups[3].Value,
                    [int]$adaptiveDiscoveryResult.Match.Groups[4].Value,
                    [int]$adaptiveDiscoveryResult.Match.Groups[5].Value
                )
                if (-not (Test-Path -LiteralPath $adaptiveNonVoidManifestPath -PathType Leaf)) {
                    throw "Adaptive discovery did not persist its non-void chunk manifest: $adaptiveNonVoidManifestPath"
                }
                $manifestCoordinates = @([IO.File]::ReadAllLines(
                    $adaptiveNonVoidManifestPath,
                    (New-Object Text.UTF8Encoding($false))) | Where-Object {
                        -not [string]::IsNullOrWhiteSpace($_) -and -not $_.Trim().StartsWith('#')
                    })
                $expectedNonVoid = [int]$adaptiveDiscoveryResult.Match.Groups[13].Value
                if ($manifestCoordinates.Count -ne $expectedNonVoid) {
                    throw "Adaptive non-void manifest count mismatch: report=$expectedNonVoid manifest=$($manifestCoordinates.Count)"
                }
                foreach ($coordinate in $manifestCoordinates) {
                    if ($coordinate.Trim() -notmatch '^(-?\d+),(-?\d+)$') {
                        throw "Adaptive non-void manifest contains an invalid coordinate: $coordinate"
                    }
                    $manifestChunkX = [int]$Matches[1]
                    $manifestChunkZ = [int]$Matches[2]
                    if ($manifestChunkX * 16 -lt $adaptiveCaptureBounds[0] -or
                        $manifestChunkX * 16 -ge $adaptiveCaptureBounds[2] -or
                        $manifestChunkZ * 16 -lt $adaptiveCaptureBounds[1] -or
                        $manifestChunkZ * 16 -ge $adaptiveCaptureBounds[3]) {
                        throw "Adaptive non-void manifest coordinate is outside inferred bounds: $coordinate"
                    }
                }
                $adaptiveManifestDigest = (Get-FileHash -LiteralPath $adaptiveNonVoidManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
                $adaptiveNeighborWarps = @($queue.entries | Where-Object {
                    [string]$_.normalizedWarp -ne $identity -and
                    (Get-DateInsensitiveWarpIdentity ([string]$_.warp)) -ne $dateInsensitiveIdentity -and
                    ([string]::IsNullOrWhiteSpace([string]$candidate.locationUuid) -or
                        [string]$_.locationUuid -ne [string]$candidate.locationUuid) -and
                    (Get-QueueDimension $_ $AdaptiveDimension) -eq $adaptiveCaptureDimension -and
                    ([int]$_.x -ne 0 -or [int]$_.z -ne 0) -and
                    [int]$_.x -ge $adaptiveCaptureBounds[0] -and [int]$_.x -lt $adaptiveCaptureBounds[2] -and
                    [int]$_.z -ge $adaptiveCaptureBounds[1] -and [int]$_.z -lt $adaptiveCaptureBounds[3]
                } | ForEach-Object {
                    [pscustomobject][ordered]@{
                        warp = [string]$_.warp
                        normalizedWarp = [string]$_.normalizedWarp
                        locationName = [string]$_.locationName
                        dimension = Get-QueueDimension $_ $AdaptiveDimension
                        x = [int]$_.x
                        y = [int]$_.y
                        z = [int]$_.z
                    }
                })
                $adaptiveEffectiveConfidence = [string]$adaptiveDiscoveryResult.Match.Groups[1].Value
                if ($adaptiveNeighborWarps.Count -gt 0) {
                    $adaptiveEffectiveConfidence = 'low'
                    Write-Warning "Adaptive footprint contains $($adaptiveNeighborWarps.Count) other known Atlas warp(s); confidence downgraded to low."
                }
                $adaptiveInitialIngestionEligible =
                    $adaptiveEffectiveConfidence -eq 'high' -and $adaptiveOutsideBackgroundRadius
                if (-not $adaptiveOutsideBackgroundRadius) {
                    Write-Warning ("Adaptive warp is {0:N0} blocks from origin, inside the provisional {1:N0}-block Archive background radius; capture will remain ineligible for initial ingestion." -f
                        $adaptiveWarpDistanceBlocks, $AdaptiveBackgroundRadiusBlocks)
                }

                $adaptiveCaptureStrategy = 'discovery-pass'
                $adaptiveCaptureReceived = [int]$adaptiveDiscoveryResult.Match.Groups[12].Value
            } else {
                Invoke-CollectorCommand "msg /wdl start $captureName" @('Downloading\s+') 30 | Out-Null
                $script:downloadActive = $true
                Start-Sleep -Seconds $CaptureSeconds
            }
            $completedCapture = Complete-WdlCapture $activeCaptureName
            $chunks = [int]$completedCapture.Chunks
            $savedChunks = [int]$completedCapture.SavedChunks
            $zipPath = [string]$completedCapture.ZipPath
            $zipInfo = $null
            $coverageDiskAudit = $null
            $adaptiveDiskAudit = $null
            if ($coverageEnabled) {
                $zipInfo = Test-CaptureZip $zipPath $activeCaptureName
                $auditScript = Join-Path $PSScriptRoot 'test-wdl-chunk-coverage.ps1'
                if (-not (Test-Path -LiteralPath $auditScript -PathType Leaf)) {
                    throw "Coverage audit script was not found: $auditScript"
                }
                $coverageDiskAudit = & $auditScript -Path $zipPath -Dimension $CoverageDimension -Bounds $CoverageBounds
                if ($null -eq $coverageDiskAudit -or -not $coverageDiskAudit.ExactWithinBounds) {
                    $diskMissing = if ($null -eq $coverageDiskAudit) { 'unknown' } else { $coverageDiskAudit.TargetMissing }
                    throw "Saved ZIP failed exact region-header coverage audit; missing target chunks: $diskMissing"
                }
            } elseif ($adaptiveEnabled) {
                $auditScript = Join-Path $PSScriptRoot 'test-wdl-chunk-coverage.ps1'
                if (-not (Test-Path -LiteralPath $auditScript -PathType Leaf)) {
                    throw "Coverage audit script was not found: $auditScript"
                }
                try {
                    $zipInfo = Test-CaptureZip $zipPath $activeCaptureName
                    if ([string]$zipInfo.Dimension -ne $adaptiveCaptureDimension) {
                        Write-Warning ("Downloader dimension '{0}' overrides inferred dimension '{1}' for {2}." -f
                            $zipInfo.Dimension, $adaptiveCaptureDimension, $warpName)
                        $adaptiveCaptureDimension = [string]$zipInfo.Dimension
                    }
                    $adaptiveDiskAudit = & $auditScript -Path $zipPath -Dimension $adaptiveCaptureDimension `
                        -Bounds $adaptiveCaptureBounds -ExpectedChunksPath $adaptiveNonVoidManifestPath
                    if ($null -eq $adaptiveDiskAudit -or -not [bool]$adaptiveDiskAudit.ExactWithinBounds) {
                        $targetMissing = if ($null -eq $adaptiveDiskAudit) { 'unknown' } else { $adaptiveDiskAudit.TargetMissing }
                        $adaptiveFallbackReason = "discovery audit missing=$targetMissing"
                    }
                } catch {
                    $adaptiveFallbackReason = "discovery validation failed: $($_.Exception.Message)"
                }

                if (-not [string]::IsNullOrWhiteSpace($adaptiveFallbackReason)) {
                    Write-Warning "Adaptive discovery capture requires missing-chunk repair for ${warpName}: $adaptiveFallbackReason"
                    # Corrupt archives and invalid reports require review. A missing
                    # header alone is repairable; no full rectangle is redownloaded.
                    $zipInfo = Test-CaptureZip $zipPath $activeCaptureName
                    $adaptiveCaptureStrategy = 'missing-chunk-repair'
                    $adaptiveCaptureWaypoints = 0
                    $adaptiveCaptureRepairPasses = 0
                    for ($repairAttempt=1; $repairAttempt -le 3; $repairAttempt++) {
                        $planPath = Join-Path $gameRoot 'config\atlas-archive-coverage\repair-plan.json'
                        $requestPath = Join-Path (Split-Path -Parent $StatePath) 'capture-repair-request.json'
                        Save-JsonAtomically @{sourcePath=$zipPath;dimension=$adaptiveCaptureDimension;liveDimension=$adaptiveLiveDimensionId;
                            bounds=$adaptiveCaptureBounds;expectedChunksPath=$adaptiveNonVoidManifestPath;
                            radius=$AdaptiveTerrainRadiusChunks;planPath=$planPath} $requestPath
                        $planJson = & python (Join-Path $PSScriptRoot 'archive_capture_recovery.py') repair $requestPath
                        if ($LASTEXITCODE -ne 0) { throw 'Resume checkpoint held: invalid saved terrain or expected footprint; repair requires review.' }
                        $plan = $planJson | ConvertFrom-Json
                        if ($plan.missing -lt 1) { throw 'Resume checkpoint held: failed discovery validation has no repairable missing chunks.' }
                        # Keep the complete discovery data before starting a new save.
                        $seedDirectory = Join-Path 'D:\AtlasExample\Ingest\DeferredCaptures\repairs' ([guid]::NewGuid().ToString('N'))
                        New-Item -ItemType Directory -Path $seedDirectory -Force | Out-Null
                        $seedPath = Join-Path $seedDirectory 'partial-wdl.zip'
                        Copy-VerifiedAtomically $zipPath $seedPath $plan.sourceSha256
                        $activeCaptureName = "$captureName-repair-$repairAttempt"
                        $seed = @{CaptureName=$activeCaptureName;ZipPath=$seedPath;Sha256=$plan.sourceSha256;RestoredChunks=$plan.present;Merge=$null}
                        Start-TrackedWdlCapture $activeCaptureName $seed
                        $script:resumeCapture = $seed
                        Write-Output "WDL-TARGETED-REPAIR $warpName missing=$($plan.missing) retained=$($plan.present) expected=$($plan.expected) attempt=$repairAttempt"
                        Invoke-CollectorCommand 'msg /atlascover repair' @('ATLAS_COVER targeted-repair-start ') 30 | Out-Null
                        $adaptiveCaptureResult = Wait-CollectorMatch -Patterns @(
                            'ATLAS_COVER teleport-complete exact=(true|false) target=(\d+) missing=(\d+) received=(\d+) waypoints=(\d+) repairPasses=(\d+)',
                            'ATLAS_COVER cancelled reason=([^\s]+)'
                        ) -TimeoutSeconds $AdaptiveTimeoutSeconds -ProgressPatterns @(
                            'ATLAS_COVER (?:settled waypoint=|teleport-repair-start |teleporting waypoint=|boundary-skip waypoint=)'
                        ) -MaximumSeconds $AdaptiveMaximumRuntimeSeconds
                        if ($adaptiveCaptureResult.Line -match 'ATLAS_COVER cancelled') { throw "Targeted repair failed: $($adaptiveCaptureResult.Line)" }
                        Write-Output $adaptiveCaptureResult.Line
                        $completedCapture = Complete-WdlCapture $activeCaptureName
                        $chunks = [int]$completedCapture.Chunks
                        $savedChunks = [int]$completedCapture.SavedChunks
                        $zipPath = [string]$completedCapture.ZipPath
                        $zipInfo = Test-CaptureZip $zipPath $activeCaptureName
                        if ([string]$zipInfo.Dimension -ne $adaptiveCaptureDimension) { throw 'Resume checkpoint held: repair changed dimension.' }
                        $adaptiveDiskAudit = & $auditScript -Path $zipPath -Dimension $adaptiveCaptureDimension `
                            -Bounds $adaptiveCaptureBounds -ExpectedChunksPath $adaptiveNonVoidManifestPath
                        $adaptiveCaptureWaypoints += [int]$adaptiveCaptureResult.Match.Groups[5].Value
                        $adaptiveCaptureRepairPasses += 1 + [int]$adaptiveCaptureResult.Match.Groups[6].Value
                        if ($null -ne $adaptiveDiskAudit -and [bool]$adaptiveDiskAudit.ExactWithinBounds) { break }
                    }
                    if ($null -eq $adaptiveDiskAudit -or -not [bool]$adaptiveDiskAudit.ExactWithinBounds) {
                        throw 'Resume checkpoint held: saved ZIP remains incomplete after three missing-only repairs.'
                    }
                    $adaptiveCaptureMissing = [int]$adaptiveDiskAudit.TargetMissing
                    $adaptiveCaptureReceived = [int]$adaptiveDiskAudit.HeaderChunks
                } else {
                    $adaptiveCaptureMissing = [int]$adaptiveDiskAudit.TargetMissing
                    $adaptiveCaptureWaypoints = 0
                    $adaptiveCaptureRepairPasses = 0
                    Write-Output "DISCOVERY-CAPTURE-ACCEPTED $warpName headers=$($adaptiveDiskAudit.HeaderChunks) target=$($adaptiveDiskAudit.TargetPresent)"
                }
                if ([int]$adaptiveDiskAudit.HeaderChunks -ne $savedChunks) {
                    Write-Warning ("WDL completion count differs from the independently audited Anvil headers " +
                        "for ${warpName}: report=$savedChunks headers=$($adaptiveDiskAudit.HeaderChunks). " +
                        'All discovered non-void chunks are present; the exact-bounds derivative remains authoritative.')
                }
            } else {
                $zipInfo = Test-CaptureZip $zipPath $activeCaptureName
            }
            $digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $destinationRoot = if ($MoveToReady) { $ReadyRoot } else { $CapturedRoot }
            $destination = Join-Path $destinationRoot ([IO.Path]::GetFileName($zipPath))
            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                $existing = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($existing -ne $digest) { throw "Capture destination collision: $destination" }
            } else {
                $partial = "$destination.$([guid]::NewGuid().ToString('N')).partial"
                Copy-Item -LiteralPath $zipPath -Destination $partial
                $copied = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($copied -ne $digest) { Remove-Item -LiteralPath $partial -Force; throw 'Copied capture failed SHA-256 verification.' }
                Move-Item -LiteralPath $partial -Destination $destination
            }

            $adaptiveFootprintArtifact = $null
            if ($adaptiveEnabled) {
                $adaptiveManifestDestination = "$destination.nonvoid.csv"
                if (Test-Path -LiteralPath $adaptiveManifestDestination -PathType Leaf) {
                    $existingManifestDigest = (Get-FileHash -LiteralPath $adaptiveManifestDestination -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($existingManifestDigest -ne $adaptiveManifestDigest) {
                        throw "Adaptive manifest destination collision: $adaptiveManifestDestination"
                    }
                } else {
                    $manifestPartial = "$adaptiveManifestDestination.$([guid]::NewGuid().ToString('N')).partial"
                    Copy-Item -LiteralPath $adaptiveNonVoidManifestPath -Destination $manifestPartial
                    $copiedManifestDigest = (Get-FileHash -LiteralPath $manifestPartial -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($copiedManifestDigest -ne $adaptiveManifestDigest) {
                        Remove-Item -LiteralPath $manifestPartial -Force
                        throw 'Copied adaptive non-void manifest failed SHA-256 verification.'
                    }
                    Move-Item -LiteralPath $manifestPartial -Destination $adaptiveManifestDestination
                }
                $footprintScript = Join-Path $PSScriptRoot 'new-wdl-footprint-artifact.ps1'
                if (-not (Test-Path -LiteralPath $footprintScript -PathType Leaf)) {
                    throw "Footprint artifact script was not found: $footprintScript"
                }
                $footprintPath = Join-Path $destinationRoot (
                    [IO.Path]::GetFileNameWithoutExtension($destination) + '.footprint.zip')
                $adaptiveFootprintArtifact = & $footprintScript `
                    -SourcePath $destination `
                    -DestinationPath $footprintPath `
                    -Bounds $adaptiveCaptureBounds `
                    -ExpectedChunksPath $adaptiveManifestDestination `
                    -Dimension $adaptiveCaptureDimension
                if ($null -eq $adaptiveFootprintArtifact -or -not [bool]$adaptiveFootprintArtifact.ExactBoundsOnly) {
                    throw 'Derived adaptive footprint artifact failed its exact-bounds audit.'
                }
            }

            $captureMode = if ($coverageEnabled) { 'bounded-route' } elseif ($adaptiveEnabled) { 'adaptive-footprint' } else { 'landing-view' }
            $record = [ordered]@{
                warp = $warpName
                normalizedWarp = $identity
                status = if ($MoveToReady) { 'ready' } else { 'captured' }
                captureMode = $captureMode
                sourceServer = $Server
                queueSource = [string]$candidate.source
                archiveDisplayName = [string]$candidate.archiveDisplayName
                archiveCategoryPath = @($candidate.archiveCategoryPath)
                archiveDiscoveredUtc = [string]$candidate.archiveDiscoveredUtc
                archiveLastSeenUtc = [string]$candidate.archiveLastSeenUtc
                locationUuid = [string]$candidate.locationUuid
                locationName = [string]$candidate.locationName
                atlasX = [int]$candidate.x
                atlasY = [int]$candidate.y
                atlasZ = [int]$candidate.z
                startedUtc = $startedUtc.ToString('o')
                completedUtc = [DateTime]::UtcNow.ToString('o')
                chunks = $savedChunks
                archiveBytes = (Get-Item -LiteralPath $destination).Length
                sha256 = $digest
                path = $destination
                zipEntries = $zipInfo.Entries
                regionFiles = $zipInfo.RegionFiles
            }
            if ($coverageEnabled) {
                $record['coverage'] = [ordered]@{
                    bounds = @($CoverageBounds)
                    boundsSource = 'operator'
                    dimension = $CoverageDimension
                    terrainRadiusChunks = $CoverageTerrainRadiusChunks
                    stepChunks = $CoverageStepChunks
                    exactWithinBounds = $true
                    targetChunks = [int]$coverageResult.Match.Groups[2].Value
                    missingChunks = [int]$coverageResult.Match.Groups[3].Value
                    receivedChunks = [int]$coverageResult.Match.Groups[4].Value
                    waypoints = [int]$coverageResult.Match.Groups[5].Value
                    repairPasses = if ($coverageResult.Match.Groups.Count -gt 6 -and $coverageResult.Match.Groups[6].Success) {
                        [int]$coverageResult.Match.Groups[6].Value
                    } else { 0 }
                    diskAudit = [ordered]@{
                        dimension = [string]$coverageDiskAudit.Dimension
                        regionFiles = [int]$coverageDiskAudit.RegionFiles
                        headerChunks = [int]$coverageDiskAudit.HeaderChunks
                        minBlockX = [int]$coverageDiskAudit.MinBlockX
                        maxBlockXExclusive = [int]$coverageDiskAudit.MaxBlockXExclusive
                        minBlockZ = [int]$coverageDiskAudit.MinBlockZ
                        maxBlockZExclusive = [int]$coverageDiskAudit.MaxBlockZExclusive
                        targetPresent = [int]$coverageDiskAudit.TargetPresent
                        targetMissing = [int]$coverageDiskAudit.TargetMissing
                        exactWithinBounds = [bool]$coverageDiskAudit.ExactWithinBounds
                    }
                    probe = [string]$coverageProbe.Line
                }
            } elseif ($adaptiveEnabled) {
                $record['adaptive'] = [ordered]@{
                    standardVersion = $collectorStandardVersion
                    bounds = @($adaptiveCaptureBounds)
                    surveyBounds = @(
                        [int]$adaptiveDiscoveryResult.Match.Groups[6].Value,
                        [int]$adaptiveDiscoveryResult.Match.Groups[7].Value,
                        [int]$adaptiveDiscoveryResult.Match.Groups[8].Value,
                        [int]$adaptiveDiscoveryResult.Match.Groups[9].Value
                    )
                    boundsSource = 'adaptive-selected-build-component-plus-margin'
                    dimension = $adaptiveCaptureDimension
                    liveDimensionId = $adaptiveLiveDimensionId
                    downloadDimensionName = [string]$zipInfo.DimensionName
                    confidence = $adaptiveEffectiveConfidence
                    classifierConfidence = [string]$adaptiveDiscoveryResult.Match.Groups[1].Value
                    coreRadiusBlocks = $AdaptiveCoreRadiusBlocks
                    expansionBlocks = $AdaptiveExpansionBlocks
                    maxRadiusBlocks = $AdaptiveMaxRadiusBlocks
                    terrainRadiusChunks = $AdaptiveTerrainRadiusChunks
                    stepChunks = $AdaptiveStepChunks
                    targetChunks = [int]$adaptiveDiscoveryResult.Match.Groups[10].Value
                    missingChunks = $adaptiveCaptureMissing
                    receivedChunks = $adaptiveCaptureReceived
                    discoveryReceivedChunks = [int]$adaptiveDiscoveryResult.Match.Groups[12].Value
                    nonVoidChunks = [int]$adaptiveDiscoveryResult.Match.Groups[13].Value
                    probableBuildChunks = [int]$adaptiveDiscoveryResult.Match.Groups[14].Value
                    strongBuildChunks = [int]$adaptiveDiscoveryResult.Match.Groups[15].Value
                    primaryComponentBuildChunks = [int]$adaptiveDiscoveryResult.Match.Groups[16].Value
                    primaryComponentStrongChunks = [int]$adaptiveDiscoveryResult.Match.Groups[17].Value
                    orphanBuildChunksInsideBounds = [int]$adaptiveDiscoveryResult.Match.Groups[18].Value
                    artificialBlocks = [long]$adaptiveDiscoveryResult.Match.Groups[19].Value
                    blockEntities = [long]$adaptiveDiscoveryResult.Match.Groups[20].Value
                    iterations = [int]$adaptiveDiscoveryResult.Match.Groups[21].Value
                    discoveryWaypoints = [int]$adaptiveDiscoveryResult.Match.Groups[22].Value
                    surveyTargetChunks = [int]$adaptiveDiscoveryResult.Match.Groups[23].Value
                    surveyMissingChunks = [int]$adaptiveDiscoveryResult.Match.Groups[24].Value
                    boundarySkippedWaypoints = [int]$adaptiveDiscoveryResult.Match.Groups[34].Value
                    componentGapChunks = [int]$adaptiveDiscoveryResult.Match.Groups[25].Value
                    contextMarginChunks = [int]$adaptiveDiscoveryResult.Match.Groups[26].Value
                    maxRadiusReached = [bool]::Parse($adaptiveDiscoveryResult.Match.Groups[27].Value)
                    componentSelection = [ordered]@{
                        strategy = [string]$adaptiveDiscoveryResult.Match.Groups[28].Value
                        componentCount = [int]$adaptiveDiscoveryResult.Match.Groups[29].Value
                        landingBuildChunks = [int]$adaptiveDiscoveryResult.Match.Groups[30].Value
                        landingStrongChunks = [int]$adaptiveDiscoveryResult.Match.Groups[31].Value
                        dominantBuildChunks = [int]$adaptiveDiscoveryResult.Match.Groups[32].Value
                        dominantStrongChunks = [int]$adaptiveDiscoveryResult.Match.Groups[33].Value
                    }
                    captureStrategy = $adaptiveCaptureStrategy
                    resumedProgress = if ($null -ne $script:resumeCapture) {
                        [ordered]@{sourceSha256=$script:resumeCapture.Sha256;restoredChunks=$script:resumeCapture.RestoredChunks;merge=$script:resumeCapture.Merge}
                    } else { $null }
                    fallbackReason = $adaptiveFallbackReason
                    captureWaypoints = $adaptiveCaptureWaypoints
                    captureRepairPasses = $adaptiveCaptureRepairPasses
                    otherKnownWarpsInsideBounds = @($adaptiveNeighborWarps)
                    archiveWarpPosition = [ordered]@{
                        x = $adaptiveWarpX
                        y = $adaptiveWarpY
                        z = $adaptiveWarpZ
                        distanceFromOriginBlocks = [Math]::Round($adaptiveWarpDistanceBlocks, 2)
                    }
                    initialIngestionPolicy = [ordered]@{
                        backgroundRadiusBlocks = $AdaptiveBackgroundRadiusBlocks
                        outsideBackgroundRadius = $adaptiveOutsideBackgroundRadius
                        eligible = $adaptiveInitialIngestionEligible
                        rationale = if ($AdaptiveBackgroundRadiusBlocks -gt 0) {
                            'Adaptive ingestion requires high confidence and a warp outside the configured Archive background radius.'
                        } else {
                            'Adaptive ingestion requires high confidence; no origin-distance exclusion is configured.'
                        }
                    }
                    nonVoidManifest = [ordered]@{
                        path = $adaptiveManifestDestination
                        sha256 = $adaptiveManifestDigest
                        chunks = [int]$adaptiveDiscoveryResult.Match.Groups[13].Value
                    }
                    footprintArtifact = [ordered]@{
                        path = [string]$adaptiveFootprintArtifact.DestinationPath
                        sha256 = [string]$adaptiveFootprintArtifact.DestinationSha256
                        bytes = [long]$adaptiveFootprintArtifact.Bytes
                        headerChunks = [int]$adaptiveFootprintArtifact.HeaderChunks
                        prunedHeaderSlots = [int]$adaptiveFootprintArtifact.PrunedHeaderSlots
                        removedExternalChunks = [int]$adaptiveFootprintArtifact.RemovedExternalChunks
                        rewrittenRegionFiles = [int]$adaptiveFootprintArtifact.RewrittenRegionFiles
                        compactedBytesRemoved = [long]$adaptiveFootprintArtifact.CompactedBytesRemoved
                        exactBoundsOnly = [bool]$adaptiveFootprintArtifact.ExactBoundsOnly
                        sourceCapturePath = [string]$adaptiveFootprintArtifact.SourcePath
                        sourceCaptureSha256 = [string]$adaptiveFootprintArtifact.SourceSha256
                    }
                    probe = [string]$coverageProbe.Line
                    diskAudit = [ordered]@{
                        dimension = [string]$adaptiveDiskAudit.Dimension
                        regionFiles = [int]$adaptiveDiskAudit.RegionFiles
                        headerChunks = [int]$adaptiveDiskAudit.HeaderChunks
                        minBlockX = [int]$adaptiveDiskAudit.MinBlockX
                        maxBlockXExclusive = [int]$adaptiveDiskAudit.MaxBlockXExclusive
                        minBlockZ = [int]$adaptiveDiskAudit.MinBlockZ
                        maxBlockZExclusive = [int]$adaptiveDiskAudit.MaxBlockZExclusive
                        targetPresent = [int]$adaptiveDiskAudit.TargetPresent
                        targetMissing = [int]$adaptiveDiskAudit.TargetMissing
                        exactWithinBounds = [bool]$adaptiveDiskAudit.ExactWithinBounds
                    }
                }
            }
            $captureMetadata = [ordered]@{
                schemaVersion = 1
                warp = $warpName
                normalizedWarp = $identity
                sourceServer = $Server
                queueSource = [string]$candidate.source
                displayName = [string]$candidate.archiveDisplayName
                categoryPath = @($candidate.archiveCategoryPath)
                catalogDiscoveredUtc = [string]$candidate.archiveDiscoveredUtc
                catalogLastSeenUtc = [string]$candidate.archiveLastSeenUtc
                captureMode = $captureMode
                capturedUtc = [string]$record.completedUtc
                sha256 = $digest
                archiveBytes = [long]$record.archiveBytes
                chunks = $savedChunks
            }
            if ($coverageEnabled) { $captureMetadata['coverage'] = $record.coverage }
            if ($adaptiveEnabled) { $captureMetadata['adaptive'] = $record.adaptive }
            Save-JsonAtomically $captureMetadata "$destination.metadata.json"
            if ($adaptiveEnabled) {
                $footprintMetadata = [ordered]@{
                    schemaVersion = 1
                    warp = $warpName
                    normalizedWarp = $identity
                    sourceServer = $Server
                    queueSource = [string]$candidate.source
                    displayName = [string]$candidate.archiveDisplayName
                    categoryPath = @($candidate.archiveCategoryPath)
                    catalogDiscoveredUtc = [string]$candidate.archiveDiscoveredUtc
                    catalogLastSeenUtc = [string]$candidate.archiveLastSeenUtc
                    captureMode = 'adaptive-footprint'
                    capturedUtc = [string]$record.completedUtc
                    sha256 = [string]$adaptiveFootprintArtifact.DestinationSha256
                    archiveBytes = [long]$adaptiveFootprintArtifact.Bytes
                    chunks = [int]$adaptiveFootprintArtifact.HeaderChunks
                    sourceCapturePath = $destination
                    sourceCaptureSha256 = $digest
                    adaptive = $record.adaptive
                }
                Save-JsonAtomically $footprintMetadata "$($adaptiveFootprintArtifact.DestinationPath).metadata.json"
            }
            $stateByWarp[$identity] = [pscustomobject]$record
            $state.entries = @($stateByWarp.Values | Sort-Object normalizedWarp)
            $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
            Save-JsonAtomically $state $StatePath
            Clear-CaptureJournal
            Write-Output "CAPTURED $warpName -> $destination [$savedChunks chunks; $($digest.Substring(0, 12))]"
            try {
                foreach ($transientName in @($captureName, "$captureName-discovery", "$captureName-exact", "$captureName-repair-1", "$captureName-repair-2", "$captureName-repair-3") | Select-Object -Unique) {
                    Remove-TransientWdlCapture $transientName -DurableCapture
                }
            } catch {
                Write-Warning "Capture is durable, but transient C-drive cleanup failed for ${warpName}: $($_.Exception.Message)"
            }
        } catch {
            $adaptiveRuntimeLimitReached = $adaptiveEnabled -and
                $_.Exception.Message -like 'Adaptive maximum runtime of *'
            $captureFailureMessage = $_.Exception.Message
            $adaptiveNeedsFootprintReview = $adaptiveEnabled -and (
                ($adaptiveRuntimeLimitReached -and $AdaptiveRuntimeLimitDisposition -eq 'Review') -or
                $_.Exception.Message -like 'Resume checkpoint held:*' -or
                $_.Exception.Message -like 'Adaptive runaway guard: *' -or
                $adaptiveBoundarySkipped -gt 0)
            $disconnected = (Test-NewArchiveDisconnect) -or $captureFailureMessage -match '(?i)disconnected during an active WDL|Collector process exited'
            $handoffPreserved = $false
            if ($adaptiveRuntimeLimitReached -and $AdaptiveRuntimeLimitDisposition -eq 'Retryable' -and $script:downloadActive -and -not $disconnected) {
                try { Save-SurveyHandoff $warpName $activeCaptureName; $handoffPreserved = $true; Clear-CaptureJournal }
                catch {
                    # The existing supervisor transfers only the runtime-limit error.
                    # Hold ownership if the verified handoff did not finish; never
                    # give the long lane an empty checkpoint and silently start over.
                    $captureFailureMessage = "Handoff preservation pending: $($_.Exception.Message)"
                    $adaptiveNeedsFootprintReview = $true
                    Write-Warning $captureFailureMessage
                }
            }
            if ($adaptiveNeedsFootprintReview -and $script:downloadActive -and -not $disconnected) {
                try {
                    Invoke-CollectorCommand 'msg /atlascover cancel' @('ATLAS_COVER cancelled reason=') 30 | Out-Null
                } catch { }
            }
            if ($script:downloadActive -and -not $disconnected) {
                try {
                    $stopped = Invoke-CollectorCommand 'msg /wdl stop' @('Downloaded\s+', 'Save failed:', 'Idle\.') 180
                    if ($stopped.Line -match 'Downloaded\s+|Save failed:') { $script:downloadActive = $false }
                } catch { }
            }
            $record = [ordered]@{
                warp = $warpName
                normalizedWarp = $identity
                status = if ($adaptiveNeedsFootprintReview) { 'needs-footprint-review' } else { 'retryable' }
                failedUtc = [DateTime]::UtcNow.ToString('o')
                error = $captureFailureMessage
                adaptivePolicy = [string]$adaptiveCapturePolicy.name
                workingCaptureNames = @($captureName, "$captureName-discovery", "$captureName-exact", "$captureName-repair-1", "$captureName-repair-2", "$captureName-repair-3")
                partialPreservation = if ($handoffPreserved) { 'verified-handoff-on-D' } else { 'retained-in-working-saves' }
            }
            if ($disconnected -or (Test-Path -LiteralPath (Get-CaptureJournalPath))) {
                $priorInterrupted = $stateByWarp[$identity]
                $previousInterruptions = if ($null -ne $priorInterrupted -and $null -ne $priorInterrupted.PSObject.Properties['interruptionCount']) { [int]$priorInterrupted.interruptionCount } else { 0 }
                $record.interruptionCount = $previousInterruptions + 1
                $record.automaticRetryAfterUtc = [datetime]::UtcNow.AddMinutes(5).ToString('o')
            }
            $stateByWarp[$identity] = [pscustomobject]$record
            $state.entries = @($stateByWarp.Values | Sort-Object normalizedWarp)
            $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
            Save-JsonAtomically $state $StatePath
            if ($adaptiveNeedsFootprintReview) {
                Write-Warning "NEEDS-FOOTPRINT-REVIEW ${warpName}: $captureFailureMessage The stopped working save is preserved for diagnosis."
            } else {
                Write-Warning "RETRYABLE ${warpName}: $captureFailureMessage"
                Write-Warning "Interrupted working saves retained for ${warpName}; they are not complete/public captures."
            }
            if ($disconnected -and -not [string]::IsNullOrWhiteSpace($ExitAfterCurrentWarpSignalPath)) {
                # Finish flushing this attempt, exit at the existing boundary, then
                # let the supervisor apply its backoff and retry on the owning lane.
                [IO.File]::WriteAllText($ExitAfterCurrentWarpSignalPath, 'preserve interrupted attempt before reconnect retry')
            }
            if ($null -eq $script:process -or $script:process.HasExited) { throw }
            # End this wrapper at the capture boundary. Its finally block waits
            # for WDL flush, stops the game, and recovers the journal before restart.
            if ($disconnected -or (Test-Path -LiteralPath (Get-CaptureJournalPath))) { break }
        }
        if ($DelayBetweenWarpsSeconds -gt 0) { Start-Sleep -Seconds $DelayBetweenWarpsSeconds }
    }
} finally {
    Wait-InterruptedCaptureFlush
    Stop-CollectorProcessTree $script:process
    try { Recover-CaptureJournal }
    catch { Write-Warning "Disk resume pending; next startup is held until recovery succeeds: $($_.Exception.Message)" }
    try { Preserve-PendingInterruptedCaptures -StateByWarp $stateByWarp -State $state -StatePath $StatePath -SavesRoot $savesRoot }
    catch { Write-Warning "Interrupted capture copy is pending; originals retained: $($_.Exception.Message)" }
    try { Pump-CollectorOutput } catch { }
    if ($null -ne $script:logWriter) { $script:logWriter.Dispose() }
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force }
}

Write-Output "Collector log: $logPath"
