[CmdletBinding()]
param(
    [string]$SourceRoot = 'C:\AtlasExample\Ingest\archive-sync\tool-cache\baritone-1.21.10',
    [string]$JavaHome = 'C:\Program Files\Java\jdk-21',
    [string]$InstallRoot = 'C:\AtlasExample\Ingest\archive-sync\collector',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repository = 'https://github.com/cabaletta/baritone.git'
$commit = '0a374ada491f32a1d864c806b6823bbde2fa5a13'
$expectedVersion = '1.16.0'

$java = Join-Path $JavaHome 'bin\java.exe'
if (-not (Test-Path -LiteralPath $java -PathType Leaf)) { throw "Java 21 was not found: $java" }

if (-not (Test-Path -LiteralPath (Join-Path $SourceRoot '.git') -PathType Container)) {
    $parent = Split-Path -Parent $SourceRoot
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    & git clone --filter=blob:none --no-checkout $repository $SourceRoot
    if ($LASTEXITCODE -ne 0) { throw 'Could not clone the official Baritone repository.' }
}

& git -C $SourceRoot fetch --depth=1 origin $commit
if ($LASTEXITCODE -ne 0) { throw "Could not fetch pinned Baritone commit $commit." }
& git -C $SourceRoot checkout --detach --force $commit
if ($LASTEXITCODE -ne 0) { throw "Could not check out pinned Baritone commit $commit." }
$actualCommit = (& git -C $SourceRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($actualCommit -ne $commit) { throw "Baritone commit mismatch: expected $commit, got $actualCommit" }

$previousJavaHome = $env:JAVA_HOME
try {
    $env:JAVA_HOME = $JavaHome
    Push-Location -LiteralPath $SourceRoot
    try {
        & (Join-Path $SourceRoot 'gradlew.bat') ':fabric:build' '-Pavailable_loaders=fabric' '--no-daemon'
        if ($LASTEXITCODE -ne 0) { throw "Pinned Baritone build failed with exit code $LASTEXITCODE." }
    } finally {
        Pop-Location
    }
} finally {
    $env:JAVA_HOME = $previousJavaHome
}

$jar = Join-Path $SourceRoot "dist\baritone-api-fabric-$expectedVersion.jar"
if (-not (Test-Path -LiteralPath $jar -PathType Leaf)) { throw "Baritone build produced no API-preserving Fabric JAR: $jar" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($jar)
try {
    if ($null -eq $archive.GetEntry('fabric.mod.json')) { throw 'Baritone JAR has no fabric.mod.json.' }
    if ($null -eq $archive.GetEntry('baritone/api/BaritoneAPI.class')) { throw 'Baritone JAR has no public API.' }
} finally {
    $archive.Dispose()
}
$digest = (Get-FileHash -LiteralPath $jar -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "BUILT $jar [$digest] from $commit"

if ($Install) {
    $mods = Join-Path $InstallRoot 'game\mods'
    if (-not (Test-Path -LiteralPath $mods -PathType Container)) { throw "Collector mods directory was not found: $mods" }
    $destination = Join-Path $mods "baritone-api-fabric-$expectedVersion.jar"
    $partial = "$destination.$([guid]::NewGuid().ToString('N')).partial"
    try {
        Copy-Item -LiteralPath $jar -Destination $partial
        $copied = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copied -ne $digest) { throw 'Installed Baritone failed SHA-256 verification.' }
        Move-Item -LiteralPath $partial -Destination $destination -Force
    } finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
    }
    Write-Output "INSTALLED $destination [$digest]"
}
