[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\AtlasExample\Ingest\archive-sync\collector',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$artifacts = @(
    [ordered]@{
        Name = 'HeadlessMc 2.10.0'
        FileName = 'headlessmc-launcher-2.10.0.jar'
        Uri = 'https://github.com/headlesshq/headlessmc/releases/download/2.10.0/headlessmc-launcher-2.10.0.jar'
        Sha256 = '52bd5006f478377b3893011d458562977d38c65ead6d2b31089beb4d614f13cd'
        Destination = 'headlessmc-launcher-2.10.0.jar'
    },
    [ordered]@{
        Name = 'Archive World Downloader 1.2.0 for Fabric 1.21.11'
        FileName = 'archive-wdl-fabric-1.2.0+1.21.11.jar'
        Uri = 'https://github.com/thearchive-world/archive-world-downloader/releases/download/1.2.0/archive-wdl-fabric-1.2.0%2B1.21.11.jar'
        Sha256 = 'a837d4075f56a0da4248dde6d50cdec0070ea05629a2294bc63e04587e48d57e'
        Destination = 'game\mods\archive-wdl-fabric-1.2.0+1.21.11.jar'
    },
    [ordered]@{
        Name = 'Fabric API 0.141.6 for 1.21.11'
        FileName = 'fabric-api-0.141.6+1.21.11.jar'
        Uri = 'https://cdn.modrinth.com/data/P7dR8mSH/versions/6qAuTtLR/fabric-api-0.141.6%2B1.21.11.jar'
        Sha256 = 'bdff7fd7e220085cfad2ff9b1f40dde6534ae0b96cf378f97a374bc54cb9ed0f'
        Destination = 'game\mods\fabric-api-0.141.6+1.21.11.jar'
    },
    [ordered]@{
        Name = 'HMC-Specifics 2.4.0 for Fabric 1.21.11'
        FileName = 'hmc-specifics-1.21.11-fabric-latest.jar'
        Uri = 'https://github.com/headlesshq/hmc-specifics/releases/download/1.21.11-latest/hmc-specifics-1.21.11-fabric-latest.jar'
        Sha256 = '931979a82c567021b064442700f8066d7e8acf25da0d30738341ad17686e2808'
        Destination = 'game\mods\hmc-specifics-1.21.11-2.4.0-fabric-release.jar'
    },
    [ordered]@{
        Name = 'Baritone API 1.17.0 for Fabric 1.21.11'
        FileName = 'baritone-api-fabric-1.17.0.jar'
        Uri = 'https://github.com/cabaletta/baritone/releases/download/v1.17.0/baritone-api-fabric-1.17.0.jar'
        Sha256 = 'a1646893c2a6338ea6bf1a3bdecf2cd743ede61e31a4324bd79a82f3d03360f9'
        Destination = 'game\mods\baritone-api-fabric-1.17.0.jar'
    }
)

# Fabric rejects a profile when mods compiled for the previous Minecraft release
# remain beside the replacement jars. Preserve those known 1.21.10 artifacts for
# rollback, but keep them out of the active mods set before installing 1.21.11.
$incompatibleModNames = @(
    'archive-wdl-fabric-1.1.0+1.21.10.jar',
    'fabric-api-0.138.4+1.21.10.jar',
    'hmc-specifics-1.21.10-2.4.0-fabric-release.jar',
    'baritone-api-fabric-1.16.0.jar'
)

function Get-VerifiedArtifact([object]$Artifact, [string]$Root, [switch]$Replace) {
    $destination = Join-Path $Root ([string]$Artifact.Destination)
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }

    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $existing = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        $allowedExisting = @([string]$Artifact.Sha256)
        if ($Artifact.Contains('AllowedExistingSha256')) {
            $allowedExisting += @($Artifact.AllowedExistingSha256 | ForEach-Object { [string]$_ })
        }
        if ($allowedExisting -contains $existing) {
            Write-Output "VERIFIED $($Artifact.Name)"
            return
        }
        if (-not $Replace) { throw "Hash mismatch for $destination. Use -Force to replace it." }
    }

    $temporary = "$destination.$([guid]::NewGuid().ToString('N')).download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Artifact.Uri -OutFile $temporary
        $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Artifact.Sha256) {
            throw "SHA-256 mismatch for $($Artifact.Name): expected $($Artifact.Sha256), got $actual"
        }
        Move-Item -LiteralPath $temporary -Destination $destination -Force
        Write-Output "INSTALLED $($Artifact.Name) -> $destination"
    } finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

$root = [System.IO.Path]::GetFullPath($InstallRoot)
New-Item -ItemType Directory -Path $root -Force | Out-Null
$modsRoot = Join-Path $root 'game\mods'
if (Test-Path -LiteralPath $modsRoot -PathType Container) {
    $disabledAt = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
    foreach ($modName in $incompatibleModNames) {
        $priorPath = Join-Path $modsRoot $modName
        if (Test-Path -LiteralPath $priorPath -PathType Leaf) {
            $disabledPath = "$priorPath.disabled-$disabledAt"
            Move-Item -LiteralPath $priorPath -Destination $disabledPath
            Write-Output "DISABLED incompatible 1.21.10 mod -> $disabledPath"
        }
    }
}
foreach ($artifact in $artifacts) { Get-VerifiedArtifact $artifact $root -Replace:$Force }

$readmePath = Join-Path $root 'NEXT-STEPS.txt'
$readme = @'
The hash-pinned collector binaries are installed.

One-time, interactive prerequisites (do not place tokens in scripts):
1. Install Java 21 and ensure java.exe is available.
2. Start HeadlessMc with isolated paths:
   java -Dhmc.mcdir=<root>\minecraft -Dhmc.gamedir=<root>\game -jar <root>\headlessmc-launcher-2.10.0.jar
3. In HeadlessMc, run:
   fabric 1.21.11
   specifics fabric-loader-0.19.5-1.21.11 hmc-specifics
   login
4. Complete Microsoft's device-code flow with a dedicated permitted Minecraft account.
5. Confirm The Archive permits scheduled automated catalog/capture access.

After login, launch the verified client with:
   launch fabric-loader-0.19.5-1.21.11 -lwjgl --jvm -Xmx3G

Do not schedule collection until a supervised canary proves the server address,
/warp behavior, inventory GUI contract, WDL completion signals, and output path.
'@
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding($false)))
Write-Output "Toolchain files are ready under $root. Read $readmePath before authentication."
