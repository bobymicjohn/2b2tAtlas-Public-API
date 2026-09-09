[CmdletBinding()]
param(
    [string]$RunRoot = 'D:\AtlasExample\Ingest\manual-primary\spawn-november-2022-y254',
    [string]$ParallelStatusPath = 'C:\AtlasExample\Ingest\archive-sync\example-catalog\parallel-collector-status.json',
    [string]$CollectorQueuePath = 'C:\AtlasExample\Ingest\archive-sync\example-catalog\capture-queue.json',
    [string]$Warp = 'xcc2_and_TheSpire_spawn_base_2022-11-15',
    [string]$InstallRootOverride = '',
    [ValidateRange(10, 300)]
    [int]$PollSeconds = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$collectorScript = Join-Path $PSScriptRoot 'invoke-archive-collector.ps1'
$pyramidScript = Join-Path $PSScriptRoot 'generate-historic-spawn-pyramid.py'
$unmined = 'C:\AtlasExample\Ingest\tools\unmined-cli_0.19.48-dev_win-64bit\unmined-cli.exe'
$sqlite = 'C:\Users\atlas-operator\AppData\Local\Microsoft\WinGet\Packages\SQLite.SQLite_Microsoft.Winget.Source_8wekyb3d8bbwe\sqlite3.exe'
$database = 'C:\AtlasExample\Api\data\atlas.db'
$tileRoot = 'F:\AtlasExample\AtlasTiles'
$archiveRoot = 'E:\AtlasExample\WorldDownloads\manual-primary\historic-spawn'
$backupRoot = 'B:\AtlasExample\Backups'
$statusPath = Join-Path $RunRoot 'status.json'
$logPath = Join-Path $RunRoot 'run.log'
$captureRoot = Join-Path $RunRoot 'captured'
$readyRoot = Join-Path $RunRoot 'ready'
$captureStatePath = Join-Path $RunRoot 'capture-state.json'
$bounds = @(-5104, -5104, 5104, 5104)
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-Status([string]$Stage, [string]$Message, [hashtable]$Details = @{}) {
    $record = [ordered]@{
        schemaVersion = 1
        stage = $Stage
        message = $Message
        updatedUtc = [DateTime]::UtcNow.ToString('o')
        details = $Details
    }
    $temporary = "$statusPath.$([guid]::NewGuid().ToString('N')).partial"
    [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 12), $utf8)
    Move-Item -LiteralPath $temporary -Destination $statusPath -Force
}

function Add-Log([string]$Message) {
    $line = '[{0}] {1}' -f [DateTime]::UtcNow.ToString('o'), $Message
    [IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8)
}

function Get-InstallRoot([string]$Profile) {
    if ($Profile -eq 'atlas-owner') { return 'C:\AtlasExample\Ingest\archive-sync\collector' }
    return Join-Path 'C:\AtlasExample\Ingest\archive-sync\collectors' $Profile
}

function Test-InstallRootIdle([string]$InstallRoot) {
    $matching = @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @('powershell.exe', 'java.exe') -and
        -not [string]::IsNullOrWhiteSpace([string]$_.CommandLine) -and
        [string]$_.CommandLine -like "*$InstallRoot*"
    })
    return $matching.Count -eq 0
}

function Invoke-LoggedCommand([scriptblock]$Command, [string]$FailureMessage) {
    & $Command *>&1 | ForEach-Object {
        $text = [string]$_
        [IO.File]::AppendAllText($logPath, $text + [Environment]::NewLine, $utf8)
    }
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage (exit $LASTEXITCODE)." }
}

foreach ($required in @($ParallelStatusPath, $CollectorQueuePath, $collectorScript, $pyramidScript, $unmined, $sqlite, $database)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required input was not found: $required" }
}
foreach ($directory in @($RunRoot, $captureRoot, $readyRoot)) {
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
}

try {
    Write-Status 'waiting-for-client' 'Waiting for the first collector profile to finish its assigned shard.' @{
        warp = $Warp
        bounds = $bounds
        requestedTopY = 254
    }
    Add-Log 'Historic November 2022 Y254 replacement queued.'

    $installRoot = if ([string]::IsNullOrWhiteSpace($InstallRootOverride)) { $null } else { [IO.Path]::GetFullPath($InstallRootOverride) }
    $profile = if ($null -eq $installRoot) { $null } else { Split-Path -Leaf $installRoot }
    while ($null -eq $installRoot) {
        $parallel = [IO.File]::ReadAllText($ParallelStatusPath, $utf8) | ConvertFrom-Json
        foreach ($worker in @($parallel.workers | Sort-Object { [int]$_.assigned - [int]$_.completed })) {
            if ([int]$worker.completed -lt [int]$worker.assigned -or [bool]$worker.running) { continue }
            $candidateRoot = Get-InstallRoot ([string]$worker.profile)
            if (Test-InstallRootIdle $candidateRoot) {
                $installRoot = $candidateRoot
                $profile = [string]$worker.profile
                break
            }
        }
        if ($null -eq $installRoot) { Start-Sleep -Seconds $PollSeconds }
    }
    if (-not (Test-Path -LiteralPath $installRoot -PathType Container)) {
        throw "Collector install root was not found: $installRoot"
    }
    if (-not (Test-InstallRootIdle $installRoot)) {
        throw "Collector install root is still in use: $installRoot"
    }

    Write-Status 'capturing' "Collector profile $profile is scanning the exact 10,208-block square." @{
        profile = $profile
        installRoot = $installRoot
        warp = $Warp
        bounds = $bounds
        requestedTopY = 254
    }
    Add-Log "Using completed collector profile $profile at $installRoot."

    & $collectorScript `
        -InstallRoot $installRoot `
        -QueuePath $CollectorQueuePath `
        -StatePath $captureStatePath `
        -CapturedRoot $captureRoot `
        -ReadyRoot $readyRoot `
        -Warp $Warp `
        -MaxWarps 1 `
        -CoverageBounds $bounds `
        -CoverageDimension Overworld `
        -CoverageTerrainRadiusChunks 10 `
        -CoverageStepChunks 20 `
        -CoverageTimeoutSeconds 7200 `
        -MinimumFreeGiB 100 *>&1 | ForEach-Object {
            [IO.File]::AppendAllText($logPath, ([string]$_) + [Environment]::NewLine, $utf8)
        }

    $captureState = [IO.File]::ReadAllText($captureStatePath, $utf8) | ConvertFrom-Json
    $normalizedWarp = $Warp.Trim().ToLowerInvariant()
    $capture = @($captureState.entries | Where-Object {
        [string]$_.normalizedWarp -eq $normalizedWarp -and [string]$_.status -eq 'captured'
    } | Select-Object -Last 1)
    if ($capture.Count -ne 1) { throw 'The collector exited without a durable captured record.' }
    $capture = $capture[0]
    if (-not [bool]$capture.coverage.exactWithinBounds -or [int]$capture.coverage.diskAudit.targetMissing -ne 0) {
        throw 'The captured ZIP failed its exact on-disk coverage audit.'
    }
    $capturePath = [string]$capture.path
    $captureHash = [string]$capture.sha256
    if (-not (Test-Path -LiteralPath $capturePath -PathType Leaf)) { throw "Captured ZIP is missing: $capturePath" }
    if ((Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $captureHash) {
        throw 'Captured ZIP hash no longer matches its durable state record.'
    }

    Write-Status 'archiving' 'Copying the verified source WDL from D staging to the X archive.' @{
        profile = $profile
        capturePath = $capturePath
        sha256 = $captureHash
        chunks = [int]$capture.chunks
    }
    if (-not (Test-Path -LiteralPath 'E:\')) { throw 'The E landing volume is unavailable.' }
    New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
    $archivedCapture = Join-Path $archiveRoot ([IO.Path]::GetFileName($capturePath))
    if (-not (Test-Path -LiteralPath $archivedCapture -PathType Leaf)) {
        $archivePartial = "$archivedCapture.$([guid]::NewGuid().ToString('N')).partial"
        Copy-Item -LiteralPath $capturePath -Destination $archivePartial
        if ((Get-FileHash -LiteralPath $archivePartial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $captureHash) {
            Remove-Item -LiteralPath $archivePartial -Force
            throw 'The E archive copy failed SHA-256 verification.'
        }
        Move-Item -LiteralPath $archivePartial -Destination $archivedCapture
    } elseif ((Get-FileHash -LiteralPath $archivedCapture -Algorithm SHA256).Hash.ToLowerInvariant() -ne $captureHash) {
        throw "Archive destination collision: $archivedCapture"
    }

    $runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $extractRoot = Join-Path $RunRoot "extracted-$runId"
    New-Item -ItemType Directory -Path $extractRoot | Out-Null
    Write-Status 'extracting' 'Extracting the verified WDL on D for rendering.' @{
        archivedCapture = $archivedCapture
        extractRoot = $extractRoot
        sha256 = $captureHash
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($capturePath, $extractRoot)
    $worldCandidates = @(Get-ChildItem -LiteralPath $extractRoot -Filter 'level.dat' -File -Recurse | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.DirectoryName 'region') -PathType Container
    } | Sort-Object { $_.DirectoryName.Length })
    if ($worldCandidates.Count -eq 0) { throw 'No extracted Overworld root with level.dat and region data was found.' }
    $worldRoot = $worldCandidates[0].DirectoryName

    $renderPath = Join-Path $RunRoot "spawn-10k-november-2022-y254-$runId.png"
    Write-Status 'rendering-y254' 'Rendering the exact square with the obsidian ceiling excluded.' @{
        worldRoot = $worldRoot
        output = $renderPath
        topY = 254
        bounds = $bounds
    }
    $renderArguments = @(
        'image', 'render',
        "--world=$worldRoot",
        "--output=$renderPath",
        '--area=b(-5104,-5104,10208,10208)',
        '--dimension=0',
        '--topY=254',
        '--shadows=3d',
        '--chunkprocessors=24',
        '-c'
    )
    & $unmined $renderArguments *>&1 | ForEach-Object {
        [IO.File]::AppendAllText($logPath, ([string]$_) + [Environment]::NewLine, $utf8)
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $renderPath -PathType Leaf)) {
        throw "uNmINeD Y254 render failed (exit $LASTEXITCODE)."
    }
    Add-Type -AssemblyName System.Drawing
    $image = [Drawing.Image]::FromFile($renderPath)
    try {
        if ($image.Width -ne 10208 -or $image.Height -ne 10208) {
            throw "Unexpected render dimensions: $($image.Width)x$($image.Height)."
        }
    } finally {
        $image.Dispose()
    }
    $renderHash = (Get-FileHash -LiteralPath $renderPath -Algorithm SHA256).Hash.ToLowerInvariant()

    $tileDirectoryName = "spawn-november-2022-y254-overworld-$($renderHash.Substring(0, 10))"
    $tileDirectory = Join-Path $tileRoot $tileDirectoryName
    Write-Status 'building-pyramid' 'Building and verifying the immutable Atlas XYZ pyramid.' @{
        source = $renderPath
        sourceSha256 = $renderHash
        destination = $tileDirectory
    }
    & python $pyramidScript `
        --source $renderPath `
        --output $tileDirectory `
        --min-x -5104 `
        --min-z -5104 `
        --max-zoom 10 `
        --origin-offset 500 *>&1 | ForEach-Object {
            [IO.File]::AppendAllText($logPath, ([string]$_) + [Environment]::NewLine, $utf8)
        }
    if ($LASTEXITCODE -ne 0) { throw "Historic spawn pyramid generation failed (exit $LASTEXITCODE)." }
    $pyramidReportPath = "$tileDirectory.pyramid-report.json"
    if (-not (Test-Path -LiteralPath $pyramidReportPath -PathType Leaf)) { throw 'Pyramid report is missing.' }
    $pyramid = [IO.File]::ReadAllText($pyramidReportPath, $utf8) | ConvertFrom-Json
    if ([string]$pyramid.sourceSha256 -ne $renderHash -or [int]$pyramid.maxZoom -ne 10 -or
        [int]$pyramid.minX -ne -5104 -or [int]$pyramid.maxXExclusive -ne 5104 -or
        [int]$pyramid.minZ -ne -5104 -or [int]$pyramid.maxZExclusive -ne 5104 -or
        [int]$pyramid.tileCount -lt 2000) {
        throw 'Pyramid report did not match the verified Y254 source and expected georeference.'
    }
    $centerTile = Join-Path $tileDirectory '10\500\500.png'
    if (-not (Test-Path -LiteralPath $centerTile -PathType Leaf)) { throw 'The maximum-detail spawn-center tile is missing.' }

    Write-Status 'publishing' 'Backing up Atlas metadata and switching the existing layer to the verified Y254 pyramid.' @{
        tileDirectory = $tileDirectory
        tileCount = [int]$pyramid.tileCount
        sourceSha256 = $renderHash
    }
    New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
    $databaseBackup = Join-Path $backupRoot "atlas-$([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-before-spawn-y254.db"
    & $sqlite $database ".backup '$databaseBackup'"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $databaseBackup -PathType Leaf)) {
        throw 'SQLite backup failed; the layer was not changed.'
    }
    $urlTemplate = "https://tiles.atlas.example/AtlasTiles/$tileDirectoryName/{z}/{y}/{x}.png"
    $escapedUrl = $urlTemplate.Replace("'", "''")
    $sql = @"
BEGIN IMMEDIATE;
UPDATE MapRenders
SET Name = '10k Spawn (November 2022, Y254)',
    Scale = '10k',
    UrlTemplate = '$escapedUrl',
    HasDayNight = 0,
    MaxNativeZoom = 10,
    WorldDownloadDate = '2022-11-10',
    Source = 'The Archive historical 2b2t spawn world (2022-11-10); rendered at Y254',
    IsPublished = 1
WHERE Slug = 'spawn-10k-november-2022-overworld';
SELECT changes();
COMMIT;
"@
    $changed = (& $sqlite $database $sql | Select-Object -Last 1).Trim()
    if ($LASTEXITCODE -ne 0 -or $changed -ne '1') { throw "MapRender update affected '$changed' rows instead of one." }

    $cacheBuster = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $registered = @(Invoke-RestMethod -Uri "http://127.0.0.1:5297/api/maprenders?cb=$cacheBuster" -TimeoutSec 30 | Where-Object {
        [string]$_.slug -eq 'spawn-10k-november-2022-overworld'
    })
    if ($registered.Count -ne 1 -or [string]$registered[0].urlTemplate -ne $urlTemplate -or
        [int]$registered[0].maxNativeZoom -ne 10) {
        throw 'The live API did not return the newly published Y254 layer.'
    }

    Write-Status 'complete' 'The verified Y254 layer is live; the prior immutable ceiling pyramid remains available for rollback.' @{
        profile = $profile
        warp = $Warp
        bounds = $bounds
        topY = 254
        capturePath = $capturePath
        archivedCapture = $archivedCapture
        captureSha256 = $captureHash
        renderPath = $renderPath
        renderSha256 = $renderHash
        tileDirectory = $tileDirectory
        tileCount = [int]$pyramid.tileCount
        urlTemplate = $urlTemplate
        databaseBackup = $databaseBackup
    }
    Add-Log "COMPLETE $urlTemplate"
} catch {
    $failure = $_.Exception.Message
    Add-Log "FAILED $failure"
    Write-Status 'failed' $failure @{
        warp = $Warp
        bounds = $bounds
        requestedTopY = 254
    }
    throw
}
