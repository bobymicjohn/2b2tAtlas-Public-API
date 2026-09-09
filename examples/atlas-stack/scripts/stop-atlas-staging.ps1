[CmdletBinding()]
param([string]$StagingRoot = "$PSScriptRoot\..\build\staging-runtime")

$ErrorActionPreference = 'Stop'
$stageRoot = [System.IO.Path]::GetFullPath($StagingRoot)
$pidPath = Join-Path $stageRoot 'server.pid'
if (-not (Test-Path -LiteralPath $pidPath -PathType Leaf)) {
    Write-Output 'No Atlas staging PID file exists.'
    return
}
$processId = 0
if (-not [int]::TryParse((Get-Content -LiteralPath $pidPath -Raw).Trim(), [ref]$processId)) {
    throw 'Atlas staging PID file is invalid.'
}
$process = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction SilentlyContinue
if ($null -ne $process) {
    $defaultAssembly = [System.IO.Path]::GetFullPath((Join-Path $stageRoot '..\staging-api\2b2tAtlas.Server.dll'))
    $isStagingAppHost = $process.ExecutablePath -like '*\build\staging-api\2b2tAtlas.Server.exe'
    $isStagingDotnet = [System.IO.Path]::GetFileName($process.ExecutablePath) -ieq 'dotnet.exe' -and
        ([string]$process.CommandLine).IndexOf($defaultAssembly, [StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $isStagingAppHost -and -not $isStagingDotnet) {
        throw "PID $processId is not the isolated Atlas staging host; refusing to stop it."
    }
}
if ($null -ne $process) { Stop-Process -Id $processId -Force }
Remove-Item -LiteralPath $pidPath -Force
Write-Output "Stopped Atlas staging PID $processId."
