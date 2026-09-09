[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\AtlasExample\Ingest\archive-sync\collector',
    [string]$CatalogPath = 'C:\AtlasExample\Ingest\archive-sync\archive-warp-catalog.json',
    [string]$Server = 'thearchive.world',
    [ValidateRange(1, 10000)]
    [int]$MaxNewWarps = 25,
    [ValidateRange(1, 5000)]
    [int]$MaxPages = 500,
    [ValidateRange(1, 20)]
    [int]$MaxDepth = 10,
    [int[]]$RootSlots = @(),
    [string[]]$RootLabels = @(),
    [switch]$RevalidateKnown,
    [string[]]$ExcludeRootLabels = @(
        'The Archive Lobby',
        'Survival',
        'Nether Spawn',
        'Overworld Spawn',
        'The End Spawn',
        'Constantiam Server',
        '3b3t server'
    )
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

if ($RootSlots.Count -gt 0 -and $RootLabels.Count -gt 0) {
    throw 'Use either -RootSlots or -RootLabels, not both.'
}

$java = 'C:\Program Files\Java\jdk-21\bin\java.exe'
$launcher = Join-Path $InstallRoot 'headlessmc-launcher-2.10.0.jar'
$gameRoot = Join-Path $InstallRoot 'game'
$minecraftRoot = Join-Path $InstallRoot 'minecraft'
$version = 'fabric-loader-0.19.5-1.21.11'
$script:process = $null
$script:stdoutTask = $null
$script:stderrTask = $null
$script:lines = New-Object 'System.Collections.Generic.List[string]'
$script:logWriter = $null
$script:catalog = $null
$script:byWarp = @{}
$script:warpByBreadcrumb = @{}
$script:visitedPages = @{}
$script:newWarps = 0
$script:pages = 0
$script:stopRequested = $false
$script:lastDisconnectScanIndex = 0

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

function Save-Catalog {
    $script:catalog.entries = @($script:byWarp.Values | Sort-Object normalizedWarp)
    $script:catalog.updatedUtc = [DateTime]::UtcNow.ToString('o')
    Save-JsonAtomically $script:catalog $CatalogPath
}

function Add-CollectorLine([string]$Line, [string]$Stream) {
    if ($null -eq $Line) { return }
    $plain = ConvertTo-PlainLine $Line
    if ($plain -notmatch 'Clicking at|Screen:|You were teleported|joined the game|HMC-Specifics|\bSlot\b' -and
        $plain -match 'Failed to verify signature on property|Profile contained invalid signature for textures property|java\.security\.SignatureException|sun\.security\.rsa\.RSASignature|java\.security\.Signature(?:\$Delegate)?\.|net\.minecraft\.class_1071|YggdrasilServicesKeyInfo|YggdrasilMinecraftSessionService|java\.util\.stream\.(MatchOps|ReferencePipeline|AbstractPipeline)|AbstractList\$RandomAccessSpliterator|CompletableFuture\$AsyncSupply|ForkJoin(Pool|Task|WorkerThread)') {
        return
    }
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
            } else { $script:stdoutTask = $null }
            $readAny = $true
        }
        if ($null -ne $script:stderrTask -and $script:stderrTask.IsCompleted) {
            $line = $script:stderrTask.GetAwaiter().GetResult()
            if ($null -ne $line) {
                Add-CollectorLine $line 'err'
                $script:stderrTask = $script:process.StandardError.ReadLineAsync()
            } else { $script:stderrTask = $null }
            $readAny = $true
        }
    } while ($readAny)
}

function Send-CollectorCommand([string]$Command) {
    if ($null -eq $script:process -or $script:process.HasExited) { throw "Collector process is not running; cannot send '$Command'." }
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($Command + "`n")
    $script:process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $script:process.StandardInput.BaseStream.Flush()
}

function Wait-CollectorMatch([string[]]$Patterns, [int]$TimeoutSeconds, [int]$StartIndex = -1) {
    if ($StartIndex -lt 0) { $StartIndex = $script:lines.Count }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $next = $StartIndex
    while ([DateTime]::UtcNow -lt $deadline) {
        Pump-CollectorOutput
        while ($next -lt $script:lines.Count) {
            $line = $script:lines[$next]
            $next++
            foreach ($pattern in $Patterns) {
                $match = [regex]::Match($line, $pattern, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
                if ($match.Success) { return [pscustomobject]@{ Line = $line; Match = $match; NextIndex = $next } }
            }
        }
        if ($script:process.HasExited) { throw "Collector process exited with code $($script:process.ExitCode)." }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out after $TimeoutSeconds second(s) waiting for: $($Patterns -join ' | ')"
}

function Invoke-CollectorCommand([string]$Command, [string[]]$Patterns, [int]$TimeoutSeconds) {
    Pump-CollectorOutput
    $start = $script:lines.Count
    Send-CollectorCommand $Command
    return Wait-CollectorMatch $Patterns $TimeoutSeconds $start
}

function Connect-ArchiveServer {
    $result = Invoke-CollectorCommand "connect $Server" @(
        'joined the game',
        'Failed to connect',
        'Invalid session',
        'not authenticated'
    ) 120
    if ($result.Line -notmatch 'joined the game') {
        throw "Archive connection failed: $($result.Line)"
    }
    $script:lastDisconnectScanIndex = $script:lines.Count
}

function Repair-DisconnectedArchiveSession {
    Pump-CollectorOutput
    $disconnected = $false
    for ($index = $script:lastDisconnectScanIndex; $index -lt $script:lines.Count; $index++) {
        if ($script:lines[$index] -match '(?i)Client disconnected|Connection reset|Disconnected from') {
            $disconnected = $true
        }
    }
    $script:lastDisconnectScanIndex = $script:lines.Count
    if (-not $disconnected) { return $false }

    Start-Sleep -Seconds 3
    Connect-ArchiveServer
    # A reconnect can emit the join event before the lobby plugin is ready to
    # serve /warps. Give the world and command bridge a bounded settle period.
    Start-Sleep -Seconds 8
    return $true
}

function Get-NormalizedWarp([string]$Value) {
    return ($Value.Trim() -replace '^/warp\s+', '' -replace '\s+', '_').ToLowerInvariant()
}

function Get-BreadcrumbKey([string[]]$Breadcrumbs) {
    return (@($Breadcrumbs | ForEach-Object { ([string]$_).Trim().ToLowerInvariant() }) -join [char]31)
}

function Get-GuiItems {
    Pump-CollectorOutput
    $start = $script:lines.Count
    Send-CollectorCommand 'gui'
    $last = Wait-CollectorMatch @(
        # Only slots 0-53 can contain clickable inventory entries. Waiting for
        # the first row after that range proves the useful portion is complete
        # without depending on slot 89, whose console row is occasionally
        # mangled by asynchronous client logging.
        '^\s*5[4-9]\s+.*\bSlot\s*$',
        'Minecraft is currently not displaying a Gui'
    ) 25 $start
    if ($last.Line -match 'not displaying a Gui') { throw 'Minecraft is currently not displaying the Archive GUI.' }
    $items = @()
    for ($index = $start; $index -lt $last.NextIndex; $index++) {
        $line = $script:lines[$index]
        $match = [regex]::Match($line, '^\s*(?<id>\d+)\s+(?<text>.*?)\s+(?<x>-?\d+)\s+(?<y>-?\d+)\s+(?<w>\d+)\s+(?<h>\d+)\s+Slot\s*$')
        if (-not $match.Success) { continue }
        $id = [int]$match.Groups['id'].Value
        $text = $match.Groups['text'].Value.Trim()
        if ($id -lt 54 -and -not [string]::IsNullOrWhiteSpace($text)) {
            $items += [pscustomobject]@{ slot = $id; label = $text.Trim('[', ']') }
        }
    }
    return @($items)
}

function Open-CatalogPath([int[]]$Path) {
    Pump-CollectorOutput
    if ($script:lines.Count -gt 20000) {
        $removeCount = $script:lines.Count - 2000
        $script:lines.RemoveRange(0, $removeCount)
        $script:lastDisconnectScanIndex = [math]::Max(0, $script:lastDisconnectScanIndex - $removeCount)
    }
    $lastError = $null
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        try {
            Send-CollectorCommand 'msg /warps'
            Start-Sleep -Seconds ([math]::Min(8, 1 + $attempt))
            # Wait for the complete inventory model, not merely the command.
            # Player-head texture validation can make large Archive menus take
            # several seconds to settle even when the screen technically opened.
            $currentItems = @(Get-GuiItems)
            $currentFingerprint = Get-PageFingerprint $currentItems
            if ([string]::IsNullOrWhiteSpace($currentFingerprint)) {
                throw 'Archive root catalog page is empty.'
            }
            foreach ($slot in @($Path)) {
                Invoke-CollectorCommand "click $slot" @('Clicking at') 20 | Out-Null
                Start-Sleep -Milliseconds 750
                $nextItems = @(Get-GuiItems)
                $nextFingerprint = Get-PageFingerprint $nextItems
                if ([string]::IsNullOrWhiteSpace($nextFingerprint)) {
                    throw "Archive GUI path slot $slot opened an empty page."
                }
                if ($nextFingerprint -eq $currentFingerprint) {
                    throw "Archive GUI path slot $slot did not change the menu."
                }
                $currentItems = $nextItems
                $currentFingerprint = $nextFingerprint
            }
            return
        } catch {
            $lastError = $_
            if ($attempt -lt 4) {
                try {
                    if (Repair-DisconnectedArchiveSession) {
                        Write-Warning "RECONNECTED Archive client after a dropped server session."
                    }
                } catch {
                    $lastError = $_
                }
                Start-Sleep -Seconds ([math]::Min(15, $attempt * 3))
            }
        }
    }
    throw "Archive GUI path could not be reopened after 4 attempts: $($lastError.Exception.Message)"
}

function Get-PageFingerprint([object[]]$Items) {
    return (@($Items | ForEach-Object { "$($_.slot):$($_.label)" }) -join '|').ToLowerInvariant()
}

function Test-RootExcluded([string]$Label) {
    foreach ($excluded in $ExcludeRootLabels) {
        if ($Label.Equals($excluded, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    # The root has a Constantiam branch, and "newly added" can expose a
    # second nested Constantiam submenu. Neither belongs in the 2b2t corpus.
    if ($Label -match '(?i)\b(?:Constantiam|3b3t)\b') { return $true }
    return $false
}

function Test-WarpExcluded([string]$Warp) {
    return $Warp -match '(?i)@(?:Constantiam|3b3t)(?:[_\s-]*server)?$'
}

function Probe-CatalogSlot([int[]]$Path, [int]$Slot) {
    Open-CatalogPath $Path
    Pump-CollectorOutput
    $start = $script:lines.Count
    Send-CollectorCommand "click $Slot"
    Wait-CollectorMatch @('Clicking at') 15 $start | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $next = $start
    while ([DateTime]::UtcNow -lt $deadline) {
        Pump-CollectorOutput
        while ($next -lt $script:lines.Count) {
            $line = $script:lines[$next]
            $next++
            $teleport = [regex]::Match($line, "You were teleported to\s+'([^']+)'", [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($teleport.Success) {
                return [pscustomobject]@{ kind = 'warp'; warp = $teleport.Groups[1].Value; items = @() }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    try {
        $items = @(Get-GuiItems)
        return [pscustomobject]@{ kind = 'page'; warp = ''; items = $items }
    } catch {
        # A real warp can close the menu immediately but not emit its confirmation
        # until the destination finishes loading. Do not misclassify that bounded
        # transition as a broken leaf merely because four seconds elapsed.
        try {
            $late = Wait-CollectorMatch @(
                "You were teleported to\s+'([^']+)'",
                'warp does not exist',
                'teleport.*cooldown',
                'permission'
            ) 25 $next
            $lateTeleport = [regex]::Match($late.Line, "You were teleported to\s+'([^']+)'", [Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($lateTeleport.Success) {
                return [pscustomobject]@{ kind = 'warp'; warp = $lateTeleport.Groups[1].Value; items = @() }
            }
            throw "Archive rejected catalog slot ${Slot}: $($late.Line)"
        } catch {
            throw "Slot $Slot neither teleported nor opened a readable submenu: $($_.Exception.Message)"
        }
    }
}

function Add-CatalogWarp([string]$Warp, [string]$DisplayName, [string[]]$Breadcrumbs) {
    if ((Test-WarpExcluded $Warp) -or @($Breadcrumbs | Where-Object { Test-RootExcluded ([string]$_) }).Count -gt 0) {
        Write-Warning "EXCLUDED non-2b2t catalog leaf $Warp [$($Breadcrumbs -join ' > ')]"
        return
    }
    $normalized = Get-NormalizedWarp $Warp
    $breadcrumbKey = Get-BreadcrumbKey $Breadcrumbs
    $existing = $script:byWarp[$normalized]
    if ($null -eq $existing) {
        $record = [ordered]@{
            warp = $Warp
            normalizedWarp = $normalized
            displayName = $DisplayName
            categoryPath = @($Breadcrumbs)
            sourceServer = $Server
            discoveredUtc = [DateTime]::UtcNow.ToString('o')
            lastSeenUtc = [DateTime]::UtcNow.ToString('o')
        }
        $script:byWarp[$normalized] = [pscustomobject]$record
        $script:newWarps++
        Write-Output "DISCOVERED $Warp [$($Breadcrumbs -join ' > ')]"
    } else {
        $existing.lastSeenUtc = [DateTime]::UtcNow.ToString('o')
        if ([string]::IsNullOrWhiteSpace([string]$existing.displayName)) { $existing.displayName = $DisplayName }
        if (@($existing.categoryPath).Count -gt 0 -and
            [string]$existing.categoryPath[0] -match '^root-slot-\d+$' -and
            $Breadcrumbs.Count -gt 0 -and $Breadcrumbs[0] -eq 'Warps') {
            $existing.displayName = $DisplayName
            $existing.categoryPath = @($Breadcrumbs)
        }
    }
    if ($script:warpByBreadcrumb.ContainsKey($breadcrumbKey) -and
        [string]$script:warpByBreadcrumb[$breadcrumbKey] -ne $normalized) {
        $script:warpByBreadcrumb[$breadcrumbKey] = ''
    } else {
        $script:warpByBreadcrumb[$breadcrumbKey] = $normalized
    }
    Save-Catalog
    if ($script:newWarps -ge $MaxNewWarps) { $script:stopRequested = $true }
}

function Explore-CatalogPage([int[]]$Path, [string[]]$Breadcrumbs, [object[]]$KnownItems = @()) {
    if ($script:stopRequested) { return }
    if ($Path.Count -gt $MaxDepth) { throw "Catalog depth exceeds $MaxDepth at $($Breadcrumbs -join ' > ')." }
    if ($KnownItems.Count -eq 0) {
        Open-CatalogPath $Path
        $items = @(Get-GuiItems)
    } else { $items = @($KnownItems) }
    $fingerprint = Get-PageFingerprint $items
    if ([string]::IsNullOrWhiteSpace($fingerprint)) { throw "Archive catalog page is empty at $($Breadcrumbs -join ' > ')." }
    if ($script:visitedPages.ContainsKey($fingerprint)) { return }
    $script:visitedPages[$fingerprint] = $true
    $script:pages++
    if ($script:pages -gt $MaxPages) { throw "Catalog page limit $MaxPages was exceeded." }

    foreach ($item in $items) {
        if ($script:stopRequested) { return }
        if ($item.label -match '^(Back|Previous)(?:\s|\(|$)') { continue }
        if (Test-RootExcluded $item.label) { continue }
        $itemBreadcrumbs = @($Breadcrumbs) + @($item.label)
        $itemKey = Get-BreadcrumbKey $itemBreadcrumbs
        if (-not $RevalidateKnown -and $script:warpByBreadcrumb.ContainsKey($itemKey)) {
            $knownIdentity = [string]$script:warpByBreadcrumb[$itemKey]
            if (-not [string]::IsNullOrWhiteSpace($knownIdentity) -and $script:byWarp.ContainsKey($knownIdentity)) {
                $script:byWarp[$knownIdentity].lastSeenUtc = [DateTime]::UtcNow.ToString('o')
                continue
            }
        }
        $result = $null
        $probeError = $null
        for ($probeAttempt = 1; $probeAttempt -le 3; $probeAttempt++) {
            try {
                $result = Probe-CatalogSlot $Path ([int]$item.slot)
                break
            } catch {
                $probeError = $_
                if ($probeAttempt -lt 3) {
                    Write-Warning "RETRY catalog slot $($item.slot) '$($item.label)' attempt $($probeAttempt + 1)/3: $($_.Exception.Message)"
                    Start-Sleep -Seconds ([math]::Min(10, $probeAttempt * 3))
                }
            }
        }
        if ($null -eq $result) {
            throw "Catalog slot $($item.slot) '$($item.label)' failed after 3 attempts: $($probeError.Exception.Message)"
        }
        if ($result.kind -eq 'warp') {
            Add-CatalogWarp $result.warp $item.label $itemBreadcrumbs
            continue
        }
        $childItems = @($result.items)
        $childFingerprint = Get-PageFingerprint $childItems
        if ($childFingerprint -eq $fingerprint) {
            Write-Warning "IGNORED no-op catalog slot $($item.slot) '$($item.label)' at $($Breadcrumbs -join ' > ')"
            continue
        }
        Explore-CatalogPage (@($Path) + @([int]$item.slot)) $itemBreadcrumbs $childItems
    }
}

foreach ($required in @($java, $launcher)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required file was not found: $required" }
}
$logRoot = Join-Path $InstallRoot 'logs'
if (-not (Test-Path -LiteralPath $logRoot)) { New-Item -ItemType Directory -Path $logRoot -Force | Out-Null }

$script:catalog = [pscustomobject][ordered]@{ schemaVersion = 1; updatedUtc = [DateTime]::UtcNow.ToString('o'); server = $Server; entries = @() }
$script:prunedEntries = 0
if (Test-Path -LiteralPath $CatalogPath -PathType Leaf) { $script:catalog = Read-JsonUtf8 $CatalogPath }
foreach ($entry in @($script:catalog.entries)) {
    if ($null -eq $entry -or [string]::IsNullOrWhiteSpace([string]$entry.warp)) { continue }
    if ((Test-WarpExcluded ([string]$entry.warp)) -or
        @($entry.categoryPath | Where-Object { Test-RootExcluded ([string]$_) }).Count -gt 0) {
        $script:prunedEntries++
        continue
    }
    $normalized = Get-NormalizedWarp ([string]$entry.warp)
    $script:byWarp[$normalized] = $entry
    $breadcrumbKey = Get-BreadcrumbKey @($entry.categoryPath)
    if ([string]::IsNullOrWhiteSpace($breadcrumbKey)) { continue }
    if ($script:warpByBreadcrumb.ContainsKey($breadcrumbKey) -and
        [string]$script:warpByBreadcrumb[$breadcrumbKey] -ne $normalized) {
        $script:warpByBreadcrumb[$breadcrumbKey] = ''
    } else {
        $script:warpByBreadcrumb[$breadcrumbKey] = $normalized
    }
}
if ($script:prunedEntries -gt 0) {
    Save-Catalog
    Write-Warning "PRUNED $($script:prunedEntries) non-2b2t catalog entr$(if($script:prunedEntries -eq 1){'y'}else{'ies'})."
}

$lockPath = 'C:\AtlasExample\Ingest\archive-sync\collector-state.json.lock'
$lock = $null
try { $lock = [IO.File]::Open($lockPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) }
catch [IO.IOException] { throw 'Another Archive collector or catalog crawl is already running.' }

$logPath = Join-Path $logRoot ("catalog-{0}.log" -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$script:logWriter = New-Object IO.StreamWriter($logPath, $false, (New-Object Text.UTF8Encoding($false)))

try {
    $arguments = @('-Dhmc.jline.enabled=false', "-Dhmc.mcdir=$minecraftRoot", "-Dhmc.gamedir=$gameRoot", '-jar', $launcher) |
        ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
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
    Send-CollectorCommand ''
    Start-Sleep -Seconds 2
    Send-CollectorCommand "launch $version -lwjgl --jvm -Xmx3G"
    Wait-CollectorMatch @('HMC-Specifics initialized!', 'Invalid credentials', 'Failed to log in') 360 | Out-Null
    Connect-ArchiveServer

    if ($RootLabels.Count -gt 0) {
        Open-CatalogPath @()
        $rootItems = @(Get-GuiItems)
        foreach ($requestedLabel in $RootLabels) {
            $matches = @($rootItems | Where-Object { $_.label.Equals($requestedLabel, [StringComparison]::OrdinalIgnoreCase) })
            if ($matches.Count -ne 1) {
                throw "Archive root label '$requestedLabel' resolved to $($matches.Count) entries; expected exactly one."
            }
            $rootItem = $matches[0]
            Explore-CatalogPage @([int]$rootItem.slot) @('Warps', [string]$rootItem.label)
        }
    } elseif ($RootSlots.Count -eq 0) {
        Explore-CatalogPage @() @('Warps')
    } else {
        foreach ($rootSlot in $RootSlots) {
            if ($script:stopRequested) { break }
            Explore-CatalogPage @($rootSlot) @("root-slot-$rootSlot")
        }
    }
    Save-Catalog
} finally {
    if ($null -ne $script:process -and -not $script:process.HasExited) {
        try { Send-CollectorCommand 'quit'; if (-not $script:process.WaitForExit(30000)) { $script:process.Kill() } } catch { try { $script:process.Kill() } catch { } }
    }
    try { Pump-CollectorOutput } catch { }
    if ($null -ne $script:logWriter) { $script:logWriter.Dispose() }
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force }
}

Write-Output "Catalog: $CatalogPath [$($script:byWarp.Count) total; $($script:newWarps) new; $($script:pages) pages]"
Write-Output "Catalog log: $logPath"
