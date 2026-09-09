[CmdletBinding()]
param(
    [string]$RunRoot = 'C:\AtlasExample\Ingest\archive-sync\example-catalog',
    [Parameter(Mandatory = $true)]
    [string]$ParallelRunRoot,
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack',
    [string]$CapturedRoot = 'D:\AtlasExample\Ingest\archive-captures\example-catalog\captured',
    [switch]$ExpandBacklog,
    [ValidateRange(0, 6)]
    [int]$FastLaneWorkerId = 5,
    [ValidateRange(0, 6)]
    [int]$SecondFastLaneWorkerId = 6,
    [ValidateRange(300, 14400)]
    [int]$FastLaneAdaptiveMaximumRuntimeSeconds = 1800
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')
. (Join-Path $PSScriptRoot 'archive-collector-lane-policy.ps1')
$run = [IO.Path]::GetFullPath($RunRoot)
$lanePolicy = Get-ArchiveCollectorLanePolicy $run
$parallelRun = [IO.Path]::GetFullPath($ParallelRunRoot)
$runPrefix = $run.TrimEnd('\') + '\'
if (-not $parallelRun.StartsWith($runPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Parallel run must be contained by the canonical run root: $parallelRun"
}
foreach ($path in @($run, $parallelRun, $RepositoryRoot, $CapturedRoot)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required path was not found: $path" }
}

$resumeScript = Join-Path $RepositoryRoot 'scripts\resume-archive-parallel-supervisor.ps1'
$expandScript = Join-Path $RepositoryRoot 'scripts\expand-archive-parallel-backlog.ps1'
$supervisors = @(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" | Where-Object {
    [string]$_.CommandLine -match '(?i)resume-archive-parallel-supervisor\.ps1' -and
    [string]$_.CommandLine -like "*$parallelRun*"
})
if ($supervisors.Count -gt 1) { throw "Multiple supervisors target $parallelRun" }
if ($supervisors.Count -eq 1) {
    $supervisorId = [int]$supervisors[0].ProcessId
    Stop-Process -Id $supervisorId -Force
    for ($attempt = 0; $attempt -lt 40 -and (Get-Process -Id $supervisorId -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Id $supervisorId -ErrorAction SilentlyContinue) {
        throw "Supervisor PID $supervisorId did not stop."
    }
}

$lockPath = Join-Path $run 'collector-state.json.parallel.lock'
if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
    Remove-Item -LiteralPath $lockPath -Force
}
if ($ExpandBacklog) {
    & $expandScript -RunRoot $run -ParallelRunRoot $parallelRun
}

$logRoot = Join-Path $run 'logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$stdout = Join-Path $logRoot "parallel-supervisor-$stamp.out.log"
$stderr = Join-Path $logRoot "parallel-supervisor-$stamp.err.log"
$arguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $resumeScript,
    '-RunRoot', $run,
    '-ParallelRunRoot', $parallelRun,
    '-RepositoryRoot', $RepositoryRoot,
    '-CapturedRoot', $CapturedRoot,
    '-PollSeconds', '15',
    '-MaxWorkerRestarts', '10000',
    '-WorkerRestartDelaySeconds', '300',
    '-WorkerStartDelaySeconds', '15',
    '-AdaptiveBackgroundRadiusBlocks', '0',
    '-FastLaneWorkerId', [string]$FastLaneWorkerId,
    '-SecondFastLaneWorkerId', [string]$SecondFastLaneWorkerId,
    '-FastLaneAdaptiveMaximumRuntimeSeconds', [string]$FastLaneAdaptiveMaximumRuntimeSeconds
)
$process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
Set-Content -LiteralPath (Join-Path $run 'parallel-supervisor.pid') -Value ([string]$process.Id) -Encoding Ascii
Start-Sleep -Seconds 2
if ($process.HasExited) {
    $errorText = if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Raw } else { '' }
    throw "Replacement supervisor exited with code $($process.ExitCode): $errorText"
}

[pscustomobject][ordered]@{
    processId = $process.Id
    expanded = [bool]$ExpandBacklog
    lanePolicy = $lanePolicy.mode
    fastLaneWorkerIds = @(@($FastLaneWorkerId, $SecondFastLaneWorkerId) | Where-Object { $_ -gt 0 -and -not $lanePolicy.allLong } | Select-Object -Unique)
    fastLaneAdaptiveMaximumRuntimeSeconds = $FastLaneAdaptiveMaximumRuntimeSeconds
    stdout = $stdout
    stderr = $stderr
} | ConvertTo-Json
