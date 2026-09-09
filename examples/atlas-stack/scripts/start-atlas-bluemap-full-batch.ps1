param(
    [switch]$Foreground
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# The renderer discovers every distinct completed, source-backed render. The
# selection is intentionally render-centric rather than location- or warp-centric:
# if one location has both an Overworld and Nether render, both render IDs enter
# the batch and keep their own dimension config and immutable output.
$renderer = Join-Path $PSScriptRoot 'start-atlas-bluemap-coordinator.ps1'
$stateRoot = 'C:\AtlasExample\Ingest\bluemap'
$stdout = Join-Path $stateRoot 'full-batch-stdout.log'
$stderr = Join-Path $stateRoot 'full-batch-stderr.log'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null

if ($Foreground) {
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $renderer
    exit $LASTEXITCODE
}

$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', $renderer
)

$process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

[pscustomobject]@{
    ProcessId = $process.Id
    Selection = 'all completed source-backed render IDs (all dimensions)'
    StatusPath = Join-Path $stateRoot 'location-render-status.json'
    StandardOutput = $stdout
    StandardError = $stderr
}
