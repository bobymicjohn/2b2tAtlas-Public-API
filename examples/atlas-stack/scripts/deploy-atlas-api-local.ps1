[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StagedApp,
    [string]$AtlasRoot = 'C:\AtlasExample\Api',
    [string]$RollbackLabel = 'collector-backlog',
    [string]$ApiTaskName = 'Atlas Example API (.NET)'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$root = [IO.Path]::GetFullPath($AtlasRoot).TrimEnd('\')
$live = [IO.Path]::GetFullPath((Join-Path $root 'app'))
$staged = [IO.Path]::GetFullPath($StagedApp)
$rootPrefix = $root + '\'
if (-not $live.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $staged.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Live and staged app directories must both be contained by AtlasRoot.'
}
if ([IO.Path]::GetFileName($staged) -notlike 'app-next-*') {
    throw "Staged directory must use an app-next-* name: $staged"
}
foreach ($path in @($live, $staged, (Join-Path $staged '2b2tAtlas.Server.exe'), (Join-Path $root 'start-atlas-api.vbs'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required deployment path was not found: $path" }
}

# Launch through Task Scheduler, outside the deploying terminal's process job.
# A detached-looking Start-Process child can still be killed by session cleanup.
# Validate the task before stopping anything, including alternate AtlasRoot use.
$apiTask = Get-ScheduledTask -TaskName $ApiTaskName -ErrorAction Stop
$actions = @($apiTask.Actions)
$expectedLauncher = Join-Path $root 'start-atlas-api.vbs'
if ($apiTask.State -eq 'Disabled' -or $actions.Count -ne 1 -or
    [IO.Path]::GetFileName([string]$actions[0].Execute) -ine 'wscript.exe' -or
    ([string]$actions[0].Arguments).Trim().Trim('"') -ine $expectedLauncher -or
    ([string]$actions[0].WorkingDirectory).TrimEnd('\') -ine (Join-Path $root 'data')) {
    throw 'The API scheduled task must be enabled and launch the reviewed AtlasRoot VBS from its data directory.'
}

$listeners = @(Get-NetTCPConnection -LocalPort 5197 -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count -ne 1) { throw "Expected exactly one Atlas API listener on 5197; found $($listeners.Count)." }
$oldPid = [int]$listeners[0].OwningProcess
$oldProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$oldPid"
if ($null -eq $oldProcess -or
    -not ([IO.Path]::GetFullPath([string]$oldProcess.ExecutablePath)).StartsWith($live + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Port 5197 is not owned by the expected Atlas app directory."
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$rollback = [IO.Path]::GetFullPath((Join-Path $root "app-prev-$stamp-$RollbackLabel"))
$failed = [IO.Path]::GetFullPath((Join-Path $root "app-failed-$stamp-$RollbackLabel"))
if ((Test-Path -LiteralPath $rollback) -or (Test-Path -LiteralPath $failed)) {
    throw 'Generated rollback target already exists.'
}

Stop-Process -Id $oldPid -Force
for ($attempt = 0; $attempt -lt 40 -and (Get-Process -Id $oldPid -ErrorAction SilentlyContinue); $attempt++) {
    Start-Sleep -Milliseconds 250
}
if (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) { throw "Atlas API PID $oldPid did not stop." }

Move-Item -LiteralPath $live -Destination $rollback
Move-Item -LiteralPath $staged -Destination $live
try {
    Start-ScheduledTask -TaskName $ApiTaskName
    $healthy = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        try {
            $response = Invoke-WebRequest -Uri 'http://127.0.0.1:5297/api/locations' -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
    }
    if (-not $healthy) { throw 'Replacement Atlas API did not pass its local locations probe.' }
} catch {
    $replacement = @(Get-NetTCPConnection -LocalPort 5197 -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
    foreach ($replacementPid in $replacement) { Stop-Process -Id $replacementPid -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $live) { Move-Item -LiteralPath $live -Destination $failed }
    Move-Item -LiteralPath $rollback -Destination $live
    Start-ScheduledTask -TaskName $ApiTaskName
    throw
}

$newListener = Get-NetTCPConnection -LocalPort 5197 -State Listen | Select-Object -First 1
[pscustomobject][ordered]@{
    processId = [int]$newListener.OwningProcess
    rollback = $rollback
    locationsProbe = 200
} | ConvertTo-Json
