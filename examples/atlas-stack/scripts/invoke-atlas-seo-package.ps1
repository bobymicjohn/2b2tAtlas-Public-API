[CmdletBinding()]
param(
    [ValidatePattern('^https://')]
    [string]$ApiBaseUrl = 'http://127.0.0.1:5297',

    [ValidatePattern('^https://')]
    [string]$SiteBaseUrl = 'https://atlas.example',

    [string]$StateRoot = 'C:\AtlasExample\Seo',

    [string]$PushoverEnvFile = 'C:\AtlasExample\Ops\blackbrain\.env',

    [string]$GroupEvidenceIndexPath = 'C:\AtlasExample\Api\data\enrichment\2b2t-wiki-group-audit.json',

    [ValidateRange(2, 100)]
    [int]$RetentionCount = 14,

    [switch]$Force,

    [switch]$DryRun,

    [switch]$SkipNotification
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')
$buildScript = Join-Path $PSScriptRoot 'build-namecheap-package.ps1'
if ([string]::IsNullOrWhiteSpace($GroupEvidenceIndexPath) -or
    -not (Test-Path -LiteralPath $GroupEvidenceIndexPath -PathType Leaf)) {
    throw "The revision-pinned group evidence index is required for SEO packaging: $GroupEvidenceIndexPath"
}
$statePath = Join-Path $StateRoot 'state.json'
$pendingPath = Join-Path $StateRoot 'pending-upload.json'
$logPath = Join-Path $StateRoot 'logs\seo-package.log'
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$mutex = New-Object Threading.Mutex($false, 'Local\2b2tAtlasSeoPackage')
$ownsMutex = $false

function Write-Log {
    param([string]$Message, [ValidateSet('INFO', 'WARN', 'ERROR')][string]$Level = 'INFO')

    $line = '{0} [{1}] {2}' -f [DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm:ss zzz'), $Level, $Message
    Write-Host $line
    if (-not $DryRun) {
        $parent = Split-Path -Parent $logPath
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        [IO.File]::AppendAllText($logPath, $line + [Environment]::NewLine, $utf8NoBom)
    }
}

function Write-JsonAtomic {
    param([string]$Path, [object]$Value)
    Write-AtlasJsonAtomically -Value $Value -Path $Path -Depth 8
}

function Get-Sha256Text {
    param([string]$Text)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($utf8NoBom.GetBytes($Text)))).Replace('-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

function Get-CurrentFingerprint {
    $locationsUri = $ApiBaseUrl.TrimEnd('/') + '/api/locations'
    $response = Invoke-WebRequest -UseBasicParsing -Uri $locationsUri -Headers @{ Accept = 'application/json' } -TimeoutSec 90
    if ($response.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($response.Content)) {
        throw "Atlas locations API did not return a usable catalog: HTTP $($response.StatusCode)."
    }
    # Windows PowerShell 5.1 boxes ConvertFrom-Json's top-level array when it is
    # wrapped directly in @(...), producing a misleading count of one.
    $locations = $response.Content | ConvertFrom-Json
    $locationCount = [int]$locations.Count
    if ($locationCount -lt 1) { throw 'Atlas locations API returned an empty catalog.' }
    $warpCount = [int](@($locations | ForEach-Object { @($_.warps).Count } | Measure-Object -Sum).Sum)
    $worldDownloadCount = @($locations | ForEach-Object { @($_.warps) } | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    }).Count + @($locations | ForEach-Object { @($_.renders) } | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    }).Count
    $renderCount = [int](@($locations | ForEach-Object { @($_.renders).Count } | Measure-Object -Sum).Sum)
    $attachmentCount = [int](@($locations | ForEach-Object { @($_.attachments).Count } | Measure-Object -Sum).Sum)
    $groupRelationshipCount = [int](@($locations | ForEach-Object { @($_.groups).Count } | Measure-Object -Sum).Sum)

    $groupsUri = $ApiBaseUrl.TrimEnd('/') + '/api/groups'
    $groupsResponse = Invoke-WebRequest -UseBasicParsing -Uri $groupsUri -Headers @{ Accept = 'application/json' } -TimeoutSec 90
    if ($groupsResponse.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($groupsResponse.Content)) {
        throw "Atlas groups API did not return a usable catalog: HTTP $($groupsResponse.StatusCode)."
    }
    $groups = $groupsResponse.Content | ConvertFrom-Json
    $groupCount = [int]$groups.Count
    if ($groupCount -lt 1) { throw 'Atlas groups API returned an empty catalog.' }
    $groupDetailBodies = New-Object Collections.Generic.List[string]
    foreach ($group in @($groups | Sort-Object id)) {
        $detailUri = $ApiBaseUrl.TrimEnd('/') + '/api/groups/' + [int]$group.id
        $detailResponse = Invoke-WebRequest -UseBasicParsing -Uri $detailUri -Headers @{ Accept = 'application/json' } -TimeoutSec 60
        if ($detailResponse.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($detailResponse.Content)) {
            throw "Atlas group detail API did not return group $($group.id): HTTP $($detailResponse.StatusCode)."
        }
        $groupDetailBodies.Add([string]$detailResponse.Content)
    }

    $sourceRoots = @(
        (Join-Path $repoRoot '2b2tAtlas.Client'),
        (Join-Path $repoRoot '2b2tAtlas.Shared')
    )
    $sourceFiles = @(
        foreach ($root in $sourceRoots) {
            Get-ChildItem -LiteralPath $root -Recurse -File |
                Where-Object {
                    $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
                    $_.Extension -in '.cs', '.razor', '.css', '.js', '.json', '.csproj', '.props', '.targets'
                }
        }
        Get-Item -LiteralPath $buildScript
        Get-Item -LiteralPath (Join-Path $PSScriptRoot 'generate-location-entities.ps1')
        Get-Item -LiteralPath (Join-Path $PSScriptRoot 'generate-nocom-page.ps1')
        Get-Item -LiteralPath (Join-Path $PSScriptRoot 'generate-group-entities.ps1')
    ) | Sort-Object FullName -Unique

    $sourceLines = foreach ($file in $sourceFiles) {
        $relative = $file.FullName.Substring($repoRoot.Length).TrimStart('\').Replace('\', '/')
        '{0}={1}' -f $relative, (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $catalogHash = Get-Sha256Text $response.Content
    $groupCatalogHash = Get-Sha256Text (($groupsResponse.Content + "`n" + ($groupDetailBodies -join "`n")))
    $evidenceHash = ''
    if (-not [string]::IsNullOrWhiteSpace($GroupEvidenceIndexPath) -and
        (Test-Path -LiteralPath $GroupEvidenceIndexPath -PathType Leaf)) {
        $evidenceHash = (Get-FileHash -LiteralPath $GroupEvidenceIndexPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $material = @(
        'schema=5'
        'api=' + $ApiBaseUrl.TrimEnd('/').ToLowerInvariant()
        'site=' + $SiteBaseUrl.TrimEnd('/').ToLowerInvariant()
        'catalog=' + $catalogHash
        'groups=' + $groupCatalogHash
        'groupEvidence=' + $evidenceHash
        $sourceLines
    ) -join "`n"

    [pscustomobject]@{
        Fingerprint = Get-Sha256Text $material
        CatalogHash = $catalogHash
        LocationCount = $locationCount
        WarpCount = $warpCount
        WorldDownloadCount = $worldDownloadCount
        RenderCount = $renderCount
        AttachmentCount = $attachmentCount
        GroupRelationshipCount = $groupRelationshipCount
        GroupCatalogHash = $groupCatalogHash
        GroupEvidenceHash = $evidenceHash
        GroupCount = $groupCount
        SourceFileCount = $sourceFiles.Count
    }
}

function Get-PublicFingerprint {
    try {
        $uri = $SiteBaseUrl.TrimEnd('/') + '/seo-release.json?atlasSeoProbe=' + [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $release = Invoke-RestMethod -Uri $uri -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 20
        $value = [string]$release.seoFingerprint
        if ($value -match '^[a-fA-F0-9]{64}$') { return $value.ToLowerInvariant() }
    } catch {
        Write-Log "Public SEO release marker is not available yet: $($_.Exception.Message)" 'WARN'
    }
    return $null
}

function Get-PushoverCredentials {
    $credentials = @{ Token = $null; User = $null }
    if (-not (Test-Path -LiteralPath $PushoverEnvFile -PathType Leaf)) { return $credentials }
    foreach ($line in Get-Content -LiteralPath $PushoverEnvFile) {
        if ($line -match '^\s*PUSHOVER_APP_TOKEN\s*=\s*(.+?)\s*$') {
            $credentials.Token = $Matches[1].Trim().Trim('"').Trim("'")
        } elseif ($line -match '^\s*PUSHOVER_USER_KEY\s*=\s*(.+?)\s*$') {
            $credentials.User = $Matches[1].Trim().Trim('"').Trim("'")
        }
    }
    return $credentials
}

function Send-PendingNotification {
    param([object]$Pending)

    if ($SkipNotification) { return $false }
    $credentials = Get-PushoverCredentials
    if (-not $credentials.Token -or -not $credentials.User) {
        Write-Log 'Pushover credentials are unavailable; the pending package remains eligible for a later notification.' 'WARN'
        return $false
    }
    $message = @(
        'A fresh 2b2t Atlas SEO/Namecheap package is waiting for cPanel upload.'
        "Locations: $($Pending.locationCount)"
        "Groups: $($Pending.groupCount)"
        "Relationships: $($Pending.groupRelationshipCount) group/build, $($Pending.groupHighwayRelationshipCount) group/highway"
        "Resources: $($Pending.warpCount) warps, $($Pending.worldDownloadCount) public WDLs, $($Pending.renderCount) renders, $($Pending.attachmentCount) attachments"
        "File: $([IO.Path]::GetFileName([string]$Pending.packagePath))"
        "SHA-256: $([string]$Pending.packageSha256)"
        'Extract the ZIP contents into public_html.'
    ) -join "`n"
    $body = @{
        token = $credentials.Token
        user = $credentials.User
        title = '2b2t Atlas SEO upload ready'
        message = $message
        priority = 0
        sound = 'pushover'
        url = $SiteBaseUrl.TrimEnd('/') + '/admin'
        url_title = 'Open Atlas admin'
    }
    try {
        $null = Invoke-RestMethod -Uri 'https://api.pushover.net/1/messages.json' -Method Post -Body $body -TimeoutSec 20
        Write-Log "Pushover notification sent for $([IO.Path]::GetFileName([string]$Pending.packagePath))."
        return $true
    } catch {
        Write-Log "Pushover notification failed; it will be retried on the next daily run: $($_.Exception.Message)" 'WARN'
        return $false
    }
}

function Remove-ExpiredAutoPackages {
    param([string]$PreservePackage)

    $buildRoot = Join-Path $repoRoot 'build'
    $resolvedBuildRoot = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\') + '\'
    $zipFiles = @(Get-ChildItem -LiteralPath $buildRoot -File -Filter 'namecheap-auto-*.zip' |
        Sort-Object LastWriteTimeUtc -Descending)
    foreach ($zip in @($zipFiles | Select-Object -Skip $RetentionCount)) {
        if ($zip.FullName -eq $PreservePackage) { continue }
        $resolved = [IO.Path]::GetFullPath($zip.FullName)
        if (-not $resolved.StartsWith($resolvedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove an auto package outside the repository build directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Force
        $directory = $resolved.Substring(0, $resolved.Length - 4)
        if (Test-Path -LiteralPath $directory -PathType Container) {
            $resolvedDirectory = [IO.Path]::GetFullPath($directory)
            if (-not $resolvedDirectory.StartsWith($resolvedBuildRoot, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Refusing to remove an auto package directory outside the repository build directory: $resolvedDirectory"
            }
            Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
        }
    }
}

try {
    $ownsMutex = $mutex.WaitOne(0)
    if (-not $ownsMutex) {
        Write-Log 'Another Atlas SEO packaging run is active; exiting without overlap.' 'WARN'
        return
    }
    if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) { throw "Build script was not found: $buildScript" }
    if (-not $DryRun -and -not (Test-Path -LiteralPath $StateRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
    }

    $state = if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{
            schemaVersion = 1
            lastBuiltFingerprint = $null
            lastDeployedFingerprint = $null
            lastCheckedUtc = $null
            pending = $null
        }
    }

    $publicFingerprint = Get-PublicFingerprint
    if ($state.pending -and $publicFingerprint -eq [string]$state.pending.fingerprint) {
        Write-Log "Public site now serves pending fingerprint $publicFingerprint; marking the upload deployed."
        $state.lastDeployedFingerprint = $publicFingerprint
        $state.pending = $null
        if (-not $DryRun) {
            if (Test-Path -LiteralPath $pendingPath -PathType Leaf) { Remove-Item -LiteralPath $pendingPath -Force }
            Write-JsonAtomic $statePath $state
        }
    }

    $current = Get-CurrentFingerprint
    Write-Log "Observed $($current.LocationCount) public locations, $($current.GroupCount) groups, $($current.GroupRelationshipCount) group/build relationships, $($current.WarpCount) warps, $($current.RenderCount) renders, and $($current.AttachmentCount) attachments; fingerprint $($current.Fingerprint)."
    if ($DryRun) {
        [pscustomobject]@{
            Status = 'dry-run'
            Fingerprint = $current.Fingerprint
            LocationCount = $current.LocationCount
            GroupCount = $current.GroupCount
            GroupRelationshipCount = $current.GroupRelationshipCount
            WarpCount = $current.WarpCount
            WorldDownloadCount = $current.WorldDownloadCount
            RenderCount = $current.RenderCount
            AttachmentCount = $current.AttachmentCount
            PublicFingerprint = $publicFingerprint
            PendingPackage = if ($state.pending) { $state.pending.packagePath } else { $null }
            WouldBuild = $Force -or ([string]$state.lastBuiltFingerprint -ne $current.Fingerprint)
        }
        return
    }

    $state.lastCheckedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    if (-not $Force -and [string]$state.lastBuiltFingerprint -eq $current.Fingerprint) {
        if ($state.pending) {
            if ([string]$state.pending.notifiedUtc -eq '' -and (Send-PendingNotification $state.pending)) {
                $state.pending.notifiedUtc = [DateTimeOffset]::UtcNow.ToString('o')
                Write-JsonAtomic $pendingPath $state.pending
            }
            Write-Log "SEO package remains pending upload: $($state.pending.packagePath)."
        } else {
            Write-Log 'SEO assets are current; no package build is needed.'
        }
        Write-JsonAtomic $statePath $state
        return
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outputDirectory = Join-Path (Join-Path $repoRoot 'build') "namecheap-auto-$stamp"
    Write-Log "SEO inputs changed; building $outputDirectory.zip."
    & $buildScript -ApiBaseUrl $ApiBaseUrl -SiteBaseUrl $SiteBaseUrl `
        -OutputDirectory $outputDirectory -SeoFingerprint $current.Fingerprint `
        -GroupEvidenceIndexPath $GroupEvidenceIndexPath
    if ($LASTEXITCODE -ne 0) { throw "Namecheap package build exited with code $LASTEXITCODE." }

    $packagePath = "$outputDirectory.zip"
    $releasePath = Join-Path $outputDirectory 'public_html\seo-release.json'
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $releasePath -PathType Leaf)) {
        throw 'The package or its SEO release marker was not produced.'
    }
    $release = Get-Content -LiteralPath $releasePath -Raw | ConvertFrom-Json
    if ([string]$release.seoFingerprint -ne $current.Fingerprint -or
        [int]$release.locationCount -ne $current.LocationCount -or
        [int]$release.groupCount -ne $current.GroupCount -or
        [int]$release.groupRelationshipCount -ne $current.GroupRelationshipCount -or
        [int]$release.warpCount -ne $current.WarpCount -or
        [int]$release.worldDownloadRecordCount -ne $current.WorldDownloadCount -or
        [int]$release.renderCount -ne $current.RenderCount -or
        [int]$release.attachmentCount -ne $current.AttachmentCount) {
        throw 'The package SEO release marker does not match the observed input fingerprint/catalog count.'
    }
    $package = Get-Item -LiteralPath $packagePath
    $pending = [pscustomobject]@{
        schemaVersion = 1
        fingerprint = $current.Fingerprint
        catalogSha256 = $current.CatalogHash
        groupCatalogSha256 = $current.GroupCatalogHash
        groupEvidenceSha256 = $current.GroupEvidenceHash
        packagePath = $package.FullName
        packageSha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
        packageBytes = $package.Length
        locationCount = $current.LocationCount
        groupCount = $current.GroupCount
        groupRelationshipCount = $current.GroupRelationshipCount
        groupHighwayRelationshipCount = [int]$release.groupHighwayRelationshipCount
        groupEvidenceCitationCount = [int]$release.groupEvidenceCitationCount
        groupEvidenceLocationCount = [int]$release.groupEvidenceLocationCount
        warpCount = $current.WarpCount
        worldDownloadCount = $current.WorldDownloadCount
        renderCount = $current.RenderCount
        attachmentCount = $current.AttachmentCount
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        notifiedUtc = $null
        uploadTarget = 'Namecheap cPanel public_html'
    }
    $state.lastBuiltFingerprint = $current.Fingerprint
    $state.pending = $pending
    Write-JsonAtomic $pendingPath $pending

    if (Send-PendingNotification $pending) {
        $pending.notifiedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $state.pending = $pending
        Write-JsonAtomic $pendingPath $pending
    }
    Write-JsonAtomic $statePath $state
    Remove-ExpiredAutoPackages -PreservePackage $package.FullName
    Write-Log "New SEO upload is ready: $($package.FullName) ($($package.Length) bytes)."
    $pending
} catch {
    Write-Log $_.Exception.Message 'ERROR'
    throw
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
