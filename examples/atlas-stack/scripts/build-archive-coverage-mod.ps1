[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$Java = 'C:\Program Files\Java\jdk-21\bin\java.exe',
    [string]$WrapperCache = 'C:\AtlasExample\Ingest\archive-sync\tool-cache',
    [string]$InstallRoot = 'C:\AtlasExample\Ingest\archive-sync\collector',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\AtlasArchiveCoverage'
}

$wrapperUri = 'https://raw.githubusercontent.com/thearchive-world/archive-world-downloader/dce52d48f229efc0336d5ebbb9f0aab2dc550674/gradle/wrapper/gradle-wrapper.jar'
$wrapperSha256 = '7a9ce74cff467ca1bf60a4fcd9f05185acceda4d0f382434d393e17864262c5d'
$wrapperPath = Join-Path $WrapperCache 'gradle-wrapper-9.7.0.jar'
$wrapperPropertiesPath = [IO.Path]::ChangeExtension($wrapperPath, '.properties')

if (-not (Test-Path -LiteralPath $Java -PathType Leaf)) { throw "Java 21 was not found: $Java" }
if (-not (Test-Path -LiteralPath $ProjectRoot -PathType Container)) { throw "Coverage project was not found: $ProjectRoot" }
if (-not (Test-Path -LiteralPath $WrapperCache)) { New-Item -ItemType Directory -Path $WrapperCache -Force | Out-Null }

if (Test-Path -LiteralPath $wrapperPath -PathType Leaf) {
    $existing = (Get-FileHash -LiteralPath $wrapperPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($existing -ne $wrapperSha256) { throw "Cached Gradle wrapper hash mismatch: $wrapperPath" }
} else {
    $temporary = "$wrapperPath.$([guid]::NewGuid().ToString('N')).download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $wrapperUri -OutFile $temporary
        $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $wrapperSha256) { throw "Gradle wrapper hash mismatch: expected $wrapperSha256, got $actual" }
        Move-Item -LiteralPath $temporary -Destination $wrapperPath
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

$projectWrapperProperties = Join-Path $ProjectRoot 'gradle\wrapper\gradle-wrapper.properties'
if (-not (Test-Path -LiteralPath $projectWrapperProperties -PathType Leaf)) {
    throw "Gradle wrapper properties were not found: $projectWrapperProperties"
}
Copy-Item -LiteralPath $projectWrapperProperties -Destination $wrapperPropertiesPath -Force

$arguments = @(
    '-classpath', $wrapperPath,
    'org.gradle.wrapper.GradleWrapperMain',
    '--project-dir', $ProjectRoot,
    'clean', 'build', '--no-daemon'
)
& $Java @arguments
if ($LASTEXITCODE -ne 0) { throw "Coverage mod build failed with exit code $LASTEXITCODE." }

$jar = Get-ChildItem -LiteralPath (Join-Path $ProjectRoot 'build\libs') -Filter 'atlas-archive-coverage-*.jar' -File |
    Where-Object Name -NotMatch '-sources\.jar$' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $jar) { throw 'Coverage mod build produced no runtime JAR.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($jar.FullName)
try {
    if ($null -eq $archive.GetEntry('fabric.mod.json')) { throw 'Coverage mod JAR has no fabric.mod.json.' }
    if ($null -eq $archive.GetEntry('com/b2btatlas/archive/coverage/AtlasArchiveCoverageClient.class')) {
        throw 'Coverage mod JAR has no client entrypoint class.'
    }
} finally {
    $archive.Dispose()
}

$digest = (Get-FileHash -LiteralPath $jar.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "BUILT $($jar.FullName) [$digest]"

if ($Install) {
    $mods = Join-Path $InstallRoot 'game\mods'
    if (-not (Test-Path -LiteralPath $mods -PathType Container)) { throw "Collector mods directory was not found: $mods" }
    $destination = Join-Path $mods $jar.Name
    foreach ($prior in @(Get-ChildItem -LiteralPath $mods -Filter 'atlas-archive-coverage-*.jar' -File)) {
        if ($prior.FullName -ne $destination) {
            $disabled = "$($prior.FullName).disabled-$([DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))"
            Move-Item -LiteralPath $prior.FullName -Destination $disabled
            Write-Output "DISABLED prior coverage mod -> $disabled"
        }
    }
    $partial = "$destination.$([guid]::NewGuid().ToString('N')).partial"
    try {
        Copy-Item -LiteralPath $jar.FullName -Destination $partial
        $copied = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($copied -ne $digest) { throw 'Installed coverage mod failed SHA-256 verification.' }
        Move-Item -LiteralPath $partial -Destination $destination -Force
    } finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
    }
    Write-Output "INSTALLED $destination [$digest]"
}
