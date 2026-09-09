param(
    [switch]$Foreground
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Representative modern-format sources: sparse, ordinary, dense, maximum,
# cutaway, Nether, End, and multiple data-version generations. Legacy 1.12.2
# remains an explicitly separate conversion lane and is not silently upgraded.
$renderIds = @(220, 449, 624, 785, 824, 871, 927, 968, 983, 1006)
$renderer = Join-Path $PSScriptRoot 'invoke-atlas-bluemap-render.ps1'
$stateRoot = 'C:\AtlasExample\Ingest\bluemap'
$stdout = Join-Path $stateRoot 'canary-stdout.log'
$stderr = Join-Path $stateRoot 'canary-stderr.log'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null

if ($Foreground) {
    & $renderer -RenderId $renderIds
    $renderExitCode = $LASTEXITCODE
    if ($renderExitCode -eq 0) {
        # Rebuild the localhost catalog after immutable generations are promoted;
        # otherwise the static review page keeps linking the prior profile.
        & (Join-Path $PSScriptRoot 'start-atlas-bluemap-preview.ps1') | Out-Null
    }
    exit $renderExitCode
}

$arguments = @(
    '-NoProfile',
    '-NonInteractive',
    '-ExecutionPolicy', 'Bypass',
    '-File', $PSCommandPath,
    '-Foreground'
)

$process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru

[pscustomobject]@{
    ProcessId = $process.Id
    RenderIds = $renderIds -join ','
    StatusPath = Join-Path $stateRoot 'location-render-status.json'
    StandardOutput = $stdout
    StandardError = $stderr
}
