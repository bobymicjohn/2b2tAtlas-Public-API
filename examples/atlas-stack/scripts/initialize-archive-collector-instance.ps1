[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_-]{1,48}$')]
    [string]$InstanceName,
    [string]$TemplateRoot = 'C:\AtlasExample\Ingest\archive-sync\collector',
    [string]$InstancesRoot = 'C:\AtlasExample\Ingest\archive-sync\collectors'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Copy-SeedItem([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source)) { return }
    if (Test-Path -LiteralPath $Source -PathType Container) {
        if (-not (Test-Path -LiteralPath $Destination)) {
            New-Item -ItemType Directory -Path $Destination -Force | Out-Null
        }
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force
        return
    }
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

$template = [IO.Path]::GetFullPath($TemplateRoot)
$instance = [IO.Path]::GetFullPath((Join-Path $InstancesRoot $InstanceName))
if (-not (Test-Path -LiteralPath $template -PathType Container)) {
    throw "Collector template was not found: $template"
}

foreach ($directory in @(
        $instance,
        (Join-Path $instance 'HeadlessMC\auth'),
        (Join-Path $instance 'game\saves'),
        (Join-Path $instance 'game\logs'),
        (Join-Path $instance 'game\baritone')
    )) {
    if (-not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
}

# Seed only version/runtime files. Saves, Baritone cache, logs, and auth are
# intentionally excluded because every concurrent client must own those paths.
Copy-SeedItem (Join-Path $template 'headlessmc-launcher-2.10.0.jar') `
    (Join-Path $instance 'headlessmc-launcher-2.10.0.jar')
Copy-SeedItem (Join-Path $template 'minecraft') (Join-Path $instance 'minecraft')
foreach ($name in @('.fabric', 'config', 'data', 'downloads', 'mods', 'resourcepacks')) {
    Copy-SeedItem (Join-Path $template "game\$name") (Join-Path $instance "game\$name")
}
foreach ($name in @('options.txt', 'servers.dat', 'servers.dat_old')) {
    Copy-SeedItem (Join-Path $template "game\$name") (Join-Path $instance "game\$name")
}
foreach ($name in @('configs', 'HeadlessMC', 'java', 'servers', 'specifics')) {
    Copy-SeedItem (Join-Path $template "HeadlessMC\$name") (Join-Path $instance "HeadlessMC\$name")
}
Copy-SeedItem (Join-Path $template 'HeadlessMC\config.properties') `
    (Join-Path $instance 'HeadlessMC\config.properties')

$accountFile = Join-Path $instance 'HeadlessMC\auth\.accounts.json'
$authenticatedProfile = $null
if (Test-Path -LiteralPath $accountFile -PathType Leaf) {
    try {
        $accounts = [IO.File]::ReadAllText($accountFile, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
        $authenticatedProfile = @($accounts.accounts | ForEach-Object {
            if ($null -ne $_.session -and $null -ne $_.session.mcProfile) {
                [string]$_.session.mcProfile.name
            }
        }) | Select-Object -First 1
    } catch {
        throw "Existing auth profile is unreadable for ${InstanceName}: $($_.Exception.Message)"
    }
}

[pscustomobject][ordered]@{
    instanceName = $InstanceName
    installRoot = $instance
    authState = if ([string]::IsNullOrWhiteSpace($authenticatedProfile)) { 'needs-device-login' } else { 'authenticated' }
    profile = $authenticatedProfile
    launcher = Join-Path $instance 'headlessmc-launcher-2.10.0.jar'
    # HeadlessMC resolves its auth directory beneath the process working
    # directory; the collector itself deliberately runs from InstallRoot.
    workingDirectory = $instance
    gameDirectory = Join-Path $instance 'game'
    minecraftDirectory = Join-Path $instance 'minecraft'
}
