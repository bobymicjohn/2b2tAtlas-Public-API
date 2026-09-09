[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://')]
    [string]$ApiBaseUrl,

    [ValidatePattern('^https://')]
    [string]$SiteBaseUrl = 'https://atlas.example',

    [string]$OutputDirectory,

    [string]$SeoDataApiBaseUrl,

    [string]$GroupEvidenceIndexPath = 'C:\AtlasExample\Api\data\enrichment\2b2t-wiki-group-audit.json',

    [ValidatePattern('^[a-fA-F0-9]{64}$')]
    [string]$SeoFingerprint
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SeoDataApiBaseUrl)) { $SeoDataApiBaseUrl = $ApiBaseUrl }
if (-not [string]::IsNullOrWhiteSpace($GroupEvidenceIndexPath) -and
    -not (Test-Path -LiteralPath $GroupEvidenceIndexPath -PathType Leaf)) {
    throw "Group evidence index was not found: $GroupEvidenceIndexPath"
}
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repoRoot "build\namecheap-$stamp"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory"
}

$directorySourcePath = Join-Path $repoRoot '2b2tAtlas.Client\Pages\Home.razor'
$directorySource = [IO.File]::ReadAllText($directorySourcePath)
if ($directorySource -cnotmatch 'src="/Images/journeymap\.png"' -or
    $directorySource -cnotmatch 'src="/Images/xaeroplus\.webp"' -or
    $directorySource -cmatch 'src="/images/(journeymap\.png|xaeroplus\.webp)"') {
    throw 'Directory export icons must use the canonical case-sensitive /Images paths.'
}
if ($directorySource -notmatch 'PreferredDirectoryWarp' -or
    $directorySource -notmatch 'CopyDirectoryWarp' -or
    $directorySource -notmatch 'PreferredDownloadRender' -or
    $directorySource -notmatch 'Property="GroupNames"' -or
    $directorySource -notmatch 'directory-group-pills' -or
    $directorySource -notmatch 'Property="BlueMapReadyRenderCount" Title="3D Ready"' -or
    $directorySource -notmatch 'BlueMapReadyRenderCount == 1' -or
    $directorySource -notmatch 'directory-action-slot' -or
    $directorySource -notmatch 'directory-wdl-button' -or
    $directorySource -notmatch 'RenderDownloadFileName' -or
    $directorySource -notmatch 'RenderDownloadUrl') {
    throw 'Directory fixed-slot desktop/mobile warp-copy and named bounded-WDL actions are missing.'
}
$locationDetailSource = [IO.File]::ReadAllText((Join-Path $repoRoot '2b2tAtlas.Client\Pages\LocationDetail.razor'))
if ($locationDetailSource -notmatch 'loc-wdl-download' -or
    $locationDetailSource -notmatch 'renderDownloadUrl' -or
    $locationDetailSource -notmatch 'loc-render-checkbox' -or
    $locationDetailSource -notmatch 'DownloadFileNames\.RenderWorldDownload' -or
    $locationDetailSource -notmatch 'CopyRenderWarp' -or
    $locationDetailSource -notmatch 'loc-3d-ctrl' -or
    $locationDetailSource -notmatch 'ThreeDControlDisabled' -or
    $locationDetailSource -notmatch 'has not finished BlueMap generation yet' -or
    $locationDetailSource -notmatch 'RefreshThreeDOptions' -or
    $locationDetailSource -notmatch 'getVisibleBaseRenderIds' -or
    $locationDetailSource -notmatch 'BlueMapPath' -or
    $locationDetailSource -notmatch 'atlas-controls=1') {
    throw 'Location pages are missing their render selection, source-WDL, warp-copy, or availability-gated viewport-aware native BlueMap 3D controls.'
}
if ($locationDetailSource -match 'loc-3d-navigation|SetThreeDNavigation|OnThreeDFrameLoaded|Control change was not confirmed') {
    throw 'Duplicate Atlas Orbit/Fly controls or their obsolete acknowledgement UI must not ship.'
}
$adminSource = [IO.File]::ReadAllText((Join-Path $repoRoot '2b2tAtlas.Client\Pages\Admin.razor'))
if ($adminSource -notmatch 'BlueMap 3D Generation' -or
    $adminSource -notmatch 'api/admin/bluemap' -or
    $adminSource -notmatch 'Validated source-backed 3D derivatives' -or
    $adminSource -notmatch 'PendingAfterSnapshot' -or
    $adminSource -notmatch 'OutputDriveFreeBytes' -or
    $adminSource -notmatch 'blueMapStatus.Workers' -or
    $adminSource -notmatch 'worker.Stage') {
    throw 'Admin BlueMap monitoring source gate failed: progress, current work, next-pass, or storage telemetry is missing.'
}
$groupDetailSource = [IO.File]::ReadAllText((Join-Path $repoRoot '2b2tAtlas.Client\Pages\GroupDetail.razor'))
$mapSource = [IO.File]::ReadAllText((Join-Path $repoRoot '2b2tAtlas.Client\wwwroot\js\atlas-map.js'))
if ($groupDetailSource -notmatch 'group-map-canvas' -or
    $groupDetailSource -notmatch 'LoadGroupMapLocationsAsync' -or
    $groupDetailSource -notmatch 'OnLocationSelected' -or
    $groupDetailSource -notmatch 'selectedBaseRenderIds' -or
    $groupDetailSource -notmatch 'ToggleOthers' -or
    $groupDetailSource -notmatch 'loadHighways' -or
    $groupDetailSource -notmatch 'loc-primary-render-panel' -or
    $groupDetailSource -notmatch '"fitLocations"' -or
    $mapSource -notmatch 'export function fitLocations') {
    throw 'Group detail pages are missing their full interactive location/render map.'
}
$extrasSource = [IO.File]::ReadAllText((Join-Path $repoRoot '2b2tAtlas.Client\Pages\Extras.razor'))
if ($extrasSource -notmatch 'https://github\.com/dekrom/xaerotools' -or
    $extrasSource -notmatch '>XaeroTools<' -or
    $extrasSource -notmatch '2b2tAtlas overlay') {
    throw 'Extras source gate failed: the XaeroTools Atlas integration card is missing.'
}
if ($extrasSource -notmatch 'https://2b2t\.info/' -or
    $extrasSource -notmatch '>2b2t\.info<' -or
    $extrasSource -notmatch 'historical performance graphs') {
    throw 'Extras source gate failed: the 2b2t.info community resource card is missing.'
}
if ($extrasSource -notmatch 'https://unmined\.net/' -or
    $extrasSource -notmatch '>uNmINeD<' -or
    $extrasSource -notmatch 'https://bluemap\.bluecolored\.de/' -or
    $extrasSource -notmatch '>BlueMap<') {
    throw 'Extras source gate failed: the official uNmINeD or BlueMap resource card is missing.'
}
$wikiCardPosition = $extrasSource.IndexOf('<h2 style="margin-left: 11px;">2b2t Wiki</h2>', [StringComparison]::Ordinal)
$nocomCardPosition = $extrasSource.IndexOf('<h2 style="margin-left: 11px;">Nocom</h2>', [StringComparison]::Ordinal)
$infoCardPosition = $extrasSource.IndexOf('<h2 style="margin-left: 11px;">2b2t.info</h2>', [StringComparison]::Ordinal)
if ($wikiCardPosition -lt 0 -or $nocomCardPosition -lt 0 -or $infoCardPosition -lt 0 -or
    $wikiCardPosition -ge $nocomCardPosition -or $nocomCardPosition -ge $infoCardPosition) {
    throw 'Extras source gate failed: the 2b2t Wiki and 2b2t.info card positions were not swapped as requested.'
}
$aboutSource = [IO.File]::ReadAllText((Join-Path $repoRoot '2b2tAtlas.Client\Pages\About.razor'))
$aboutPipeline = [regex]::Match($aboutSource, '(?s)<section[^>]*id="ingestion-pipeline"[^>]*>(.*?)</section>').Groups[1].Value
$aboutStepNames = @([regex]::Matches($aboutPipeline, '<h3>([^<]+)</h3>') | ForEach-Object { $_.Groups[1].Value })
if ($aboutStepNames.Count -ne 7 -or
    $aboutSource -notmatch 'How the Atlas ingestion pipeline works' -or
    $aboutPipeline -notmatch 'Archive World Downloader' -or
    $aboutPipeline -notmatch 'SHA-256' -or
    $aboutPipeline -notmatch 'Local AI' -or
    $aboutPipeline -notmatch 'review' -or
    $aboutPipeline -notmatch 'https://unmined\.net/' -or
    $aboutPipeline -notmatch 'https://bluemap\.bluecolored\.de/' -or
    $aboutPipeline -notmatch '<details') {
    throw 'About source gate failed: the source-preserving 2D/3D ingestion pipeline explanation is incomplete.'
}

$publishRoot = Join-Path $OutputDirectory '.publish'
$siteRoot = Join-Path $OutputDirectory 'public_html'
$project = Join-Path $repoRoot '2b2tAtlas.Client\2b2tAtlas.Client.csproj'
dotnet publish $project -c Release -o $publishRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Client publish failed.' }

$publishedWwwRoot = Join-Path $publishRoot 'wwwroot'
if (-not (Test-Path $publishedWwwRoot -PathType Container)) {
    throw "Published wwwroot was not found: $publishedWwwRoot"
}
New-Item -ItemType Directory -Path $siteRoot | Out-Null
Copy-Item (Join-Path $publishedWwwRoot '*') $siteRoot -Recurse -Force

# Scoped component CSS has a stable filename; change its URL when its contents change.
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$componentStylesHash = (Get-FileHash (Join-Path $siteRoot '2b2tAtlas.Client.styles.css') -Algorithm SHA256).Hash.ToLowerInvariant()
$indexPath = Join-Path $siteRoot 'index.html'
$indexHtml = [IO.File]::ReadAllText($indexPath)
$indexHtml = $indexHtml.Replace('href="2b2tAtlas.Client.styles.css"', ('href="2b2tAtlas.Client.styles.css?v=' + $componentStylesHash + '"'))
if (-not $indexHtml.Contains('2b2tAtlas.Client.styles.css?v=' + $componentStylesHash)) {
    throw 'Could not version the component stylesheet in index.html.'
}
[IO.File]::WriteAllText($indexPath, $indexHtml, $utf8NoBom)
# Apache compresses HTML on demand. Do not retain compressed copies of the old shell.
foreach ($suffix in @('.br', '.gz')) {
    $compressedIndexPath = $indexPath + $suffix
    if (Test-Path -LiteralPath $compressedIndexPath) { Remove-Item -LiteralPath $compressedIndexPath -Force }
}

$json = @{
    ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')
} | ConvertTo-Json
$utf8NoBom = New-Object Text.UTF8Encoding($false)
$runtimeConfigPath = Join-Path $siteRoot 'appsettings.json'
[IO.File]::WriteAllText($runtimeConfigPath, $json, $utf8NoBom)

$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $configHash = [Convert]::ToBase64String($sha256.ComputeHash([IO.File]::ReadAllBytes($runtimeConfigPath)))
} finally {
    $sha256.Dispose()
}
$assetManifestPath = Join-Path $siteRoot 'service-worker-assets.js'
$assetManifest = [IO.File]::ReadAllText($assetManifestPath)
$configAssetPattern = New-Object Text.RegularExpressions.Regex(
    '("hash"\s*:\s*")sha256-[^"]+("\s*,\s*"url"\s*:\s*"appsettings\.json")')
if ($configAssetPattern.Matches($assetManifest).Count -ne 1) {
    throw 'Could not identify exactly one appsettings.json service-worker asset entry.'
}
$assetManifest = $configAssetPattern.Replace($assetManifest, {
    param($match)
    $match.Groups[1].Value + 'sha256-' + $configHash + $match.Groups[2].Value
})
$indexHasher = [Security.Cryptography.SHA256]::Create()
try {
    $indexHash = [Convert]::ToBase64String($indexHasher.ComputeHash([IO.File]::ReadAllBytes($indexPath)))
} finally {
    $indexHasher.Dispose()
}
$indexAssetPattern = New-Object Text.RegularExpressions.Regex(
    '("hash"\s*:\s*")sha256-[^"]+("\s*,\s*"url"\s*:\s*"index\.html")')
if ($indexAssetPattern.Matches($assetManifest).Count -ne 1) {
    throw 'Could not identify exactly one index.html service-worker asset entry.'
}
$assetManifest = $indexAssetPattern.Replace($assetManifest, {
    param($match)
    $match.Groups[1].Value + 'sha256-' + $indexHash + $match.Groups[2].Value
})
[IO.File]::WriteAllText($assetManifestPath, $assetManifest, $utf8NoBom)
foreach ($changedPath in @($assetManifestPath, $runtimeConfigPath)) {
    foreach ($suffix in @('.br', '.gz')) {
        $compressedPath = $changedPath + $suffix
        if (Test-Path -LiteralPath $compressedPath) { Remove-Item -LiteralPath $compressedPath -Force }
    }
}

$entityGenerator = Join-Path $PSScriptRoot 'generate-location-entities.ps1'
$entityArguments = @{
    ApiBaseUrl = $ApiBaseUrl
    SiteBaseUrl = $SiteBaseUrl
    DataApiBaseUrl = $SeoDataApiBaseUrl
    OutputDirectory = $siteRoot
}
if (-not [string]::IsNullOrWhiteSpace($GroupEvidenceIndexPath)) {
    $entityArguments.GroupEvidenceIndexPath = $GroupEvidenceIndexPath
}
& $entityGenerator @entityArguments

$groupEntityGenerator = Join-Path $PSScriptRoot 'generate-group-entities.ps1'
& $groupEntityGenerator `
    -ApiBaseUrl $ApiBaseUrl `
    -SiteBaseUrl $SiteBaseUrl `
    -DataApiBaseUrl $SeoDataApiBaseUrl `
    -OutputDirectory $siteRoot

$legacyRedirectPath = Join-Path $siteRoot 'entities\legacy-uuid-redirects.inc'
if (-not (Test-Path $legacyRedirectPath -PathType Leaf)) {
    throw 'The entity generator did not produce legacy UUID redirects.'
}
$legacyUuidRedirects = [IO.File]::ReadAllText($legacyRedirectPath).Trim()
if ([string]::IsNullOrWhiteSpace($legacyUuidRedirects)) {
    throw 'The generated legacy UUID redirect set is empty.'
}
$legacyUuidRedirectCount = @($legacyUuidRedirects -split "`r?`n" | Where-Object { $_.Trim() }).Count
Remove-Item $legacyRedirectPath -Force

$htaccessPrefix = @'
RewriteEngine On

# Historical Vue location URLs used /<uuid>. Consolidate each known UUID into
# its canonical static entity with a one-hop permanent redirect. This block is
# intentionally before host canonicalization so legacy www URLs also go
# directly to the final apex entity URL.
'@
$uuidSkipGuard = @"
RewriteCond %{REQUEST_URI} !^/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/?$ [NC]
RewriteRule ^ - [S=$legacyUuidRedirectCount]
"@
$htaccessSuffix = @'

# UUID-shaped paths not present in the canonical corpus represent retired or
# unknown legacy records. Return Gone instead of indexing the generic SPA shell.
RewriteRule ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/?$ - [R=410,L]

RewriteCond %{HTTP_HOST} ^www\.atlas\.example$ [NC]
RewriteRule ^ https://atlas.example%{REQUEST_URI} [R=301,L,NE]

RewriteCond %{REQUEST_FILENAME} !-f
RewriteCond %{REQUEST_FILENAME} !-d
RewriteRule ^entities/locations/[0-9]+/?$ - [R=404,L]
RewriteCond %{REQUEST_FILENAME} !-f
RewriteCond %{REQUEST_FILENAME} !-d
RewriteRule ^entities/groups/[0-9]+/?$ - [R=404,L]

RewriteRule ^api/locations\.php$ http://127.0.0.1:5297/api/locations.php [R=307,L,NE]
RewriteRule ^api/locationCount\.php$ http://127.0.0.1:5297/api/locationCount.php [R=307,L,NE]
RewriteRule ^api/newWarp\.php$ http://127.0.0.1:5297/api/newWarp.php [R=307,L,NE]

# Compatibility for cached clients from the case-insensitive Windows build.
# Namecheap's Linux filesystem correctly distinguishes /images from /Images.
RewriteRule ^images/(journeymap\.png|xaeroplus\.webp)$ Images/$1 [L]

# A missing static asset must not receive the Blazor shell with HTTP 200.
RewriteCond %{REQUEST_FILENAME} !-f
RewriteRule \.(avif|bmp|css|gif|ico|jpe?g|js|json|jsonl|map|mjs|png|svg|txt|wasm|webp|woff2?)$ - [R=404,L,NC]
RewriteCond %{REQUEST_FILENAME} !-f
RewriteCond %{REQUEST_FILENAME} !-d
RewriteRule ^ index.html [L]

AddType application/wasm .wasm
AddType application/octet-stream .dll .dat
AddType application/json .json
AddType application/x-ndjson .jsonl
AddType application/xml .xml
AddType text/plain .txt

<IfModule mod_setenvif.c>
    # These routes are authenticated utilities or interactive duplicates of
    # the canonical server-rendered entity pages. Keep them usable and
    # crawlable for link discovery, but out of search indexes.
    SetEnvIf Request_URI "^/(admin|account|login)(/|$)" atlas_private_route=1
    SetEnvIf Request_URI "^/location/[0-9]+/?$" atlas_interactive_entity=1
    SetEnvIf Request_URI "^/group/[0-9]+/?$" atlas_interactive_entity=1
</IfModule>

<IfModule mod_headers.c>
    Header always set X-Robots-Tag "noindex, nofollow" env=atlas_private_route
    Header always set X-Robots-Tag "noindex, follow" env=atlas_interactive_entity
    <FilesMatch "\.[a-z0-9]+\.(js|wasm|dll|dat|blat|pdb)$">
        Header always set Cache-Control "public, max-age=31536000, immutable"
    </FilesMatch>
    <FilesMatch "^(index\.html|appsettings\.json|seo-release\.json|service-worker\.js|service-worker-assets\.js|blazor\.webassembly\.js|dotnet\.js|atlas-map\.js)$">
        Header always set Cache-Control "no-cache, no-store, must-revalidate"
        Header always set Pragma "no-cache"
        Header always set Expires "0"
    </FilesMatch>
</IfModule>

<IfModule mod_deflate.c>
    AddOutputFilterByType DEFLATE text/html text/plain text/css application/javascript application/json application/x-ndjson application/xml application/wasm
</IfModule>
'@
$htaccess = $htaccessPrefix + "`r`n" + $uuidSkipGuard + "`r`n" + $legacyUuidRedirects + "`r`n" + $htaccessSuffix
[IO.File]::WriteAllText((Join-Path $siteRoot '.htaccess'), $htaccess, [Text.Encoding]::ASCII)

$catalogPath = Join-Path $siteRoot 'entities\locations.jsonl'
$catalogLines = @([IO.File]::ReadAllLines($catalogPath) | Where-Object { $_.Trim() })
$catalogRecords = @($catalogLines | ForEach-Object { $_ | ConvertFrom-Json })
$groupCatalogPath = Join-Path $siteRoot 'entities\groups.jsonl'
$groupCatalogRecords = @([IO.File]::ReadAllLines($groupCatalogPath) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$mediaCatalogPath = Join-Path $siteRoot 'entities\media.jsonl'
$mediaCatalogRecords = @([IO.File]::ReadAllLines($mediaCatalogPath) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$worldDownloadCatalogPath = Join-Path $siteRoot 'entities\world-downloads.jsonl'
$worldDownloadCatalogRecords = @([IO.File]::ReadAllLines($worldDownloadCatalogPath) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$warpCount = [int](@($catalogRecords | ForEach-Object { @($_.warps).Count } | Measure-Object -Sum).Sum)
$renderCount = [int](@($catalogRecords | ForEach-Object { @($_.renders).Count } | Measure-Object -Sum).Sum)
$attachmentCount = [int](@($catalogRecords | ForEach-Object { @($_.attachments).Count } | Measure-Object -Sum).Sum)
$locationGroupRelationshipCount = [int](@($catalogRecords | ForEach-Object { @($_.groups).Count } | Measure-Object -Sum).Sum)
$groupLocationRelationshipCount = [int](@($groupCatalogRecords | ForEach-Object { @($_.locations).Count } | Measure-Object -Sum).Sum)
$groupHighwayRelationshipCount = [int](@($groupCatalogRecords | ForEach-Object { @($_.highways).Count } | Measure-Object -Sum).Sum)
$groupEvidenceCitationCount = [int](@($catalogRecords | ForEach-Object { @($_.groupEvidence).Count } | Measure-Object -Sum).Sum)
$groupEvidenceLocationCount = @($catalogRecords | Where-Object { @($_.groupEvidence).Count -gt 0 }).Count
$journeyIconPath = Join-Path $siteRoot 'Images\journeymap.png'
$xaeroIconPath = Join-Path $siteRoot 'Images\xaeroplus.webp'
if (-not (Test-Path $journeyIconPath -PathType Leaf) -or -not (Test-Path $xaeroIconPath -PathType Leaf)) {
    throw 'The JourneyMap or Xaero export icon is absent from the published package.'
}
$journeyBytes = [IO.File]::ReadAllBytes($journeyIconPath)
$xaeroBytes = [IO.File]::ReadAllBytes($xaeroIconPath)
if ($journeyBytes.Length -lt 8 -or [BitConverter]::ToString($journeyBytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') {
    throw 'The packaged JourneyMap export icon is not a valid PNG.'
}
if ($xaeroBytes.Length -lt 12 -or [Text.Encoding]::ASCII.GetString($xaeroBytes, 0, 4) -ne 'RIFF' -or
    [Text.Encoding]::ASCII.GetString($xaeroBytes, 8, 4) -ne 'WEBP') {
    throw 'The packaged Xaero export icon is not a valid WebP image.'
}
$releaseMetadata = [ordered]@{
    schemaVersion = 5
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    locationCount = $catalogRecords.Count
    groupCount = $groupCatalogRecords.Count
    warpCount = $warpCount
    renderCount = $renderCount
    attachmentCount = $attachmentCount
    mediaRecordCount = $mediaCatalogRecords.Count
    worldDownloadRecordCount = $worldDownloadCatalogRecords.Count
    groupRelationshipCount = $locationGroupRelationshipCount
    locationGroupRelationshipCount = $locationGroupRelationshipCount
    groupLocationRelationshipCount = $groupLocationRelationshipCount
    groupHighwayRelationshipCount = $groupHighwayRelationshipCount
    groupEvidenceCitationCount = $groupEvidenceCitationCount
    groupEvidenceLocationCount = $groupEvidenceLocationCount
    seoFingerprint = if ([string]::IsNullOrWhiteSpace($SeoFingerprint)) { $null } else { $SeoFingerprint.ToLowerInvariant() }
}
[IO.File]::WriteAllText(
    (Join-Path $siteRoot 'seo-release.json'),
    ($releaseMetadata | ConvertTo-Json -Depth 3),
    $utf8NoBom)
$redirectCount = [regex]::Matches($legacyUuidRedirects, '(?m)^RewriteRule \^[0-9a-f-]{36}/\?\$ ').Count
if ($catalogRecords.Count -eq 0 -or $redirectCount -ne $catalogRecords.Count) {
    throw "Entity count ($($catalogRecords.Count)) and UUID redirect count ($redirectCount) do not match."
}
if (@($catalogRecords | Where-Object { [string]$_.description -match '(?i)rnrn|\\[rn]' }).Count -gt 0) {
    throw 'The generated entity catalog contains a legacy newline artifact.'
}
if (-not [string]::IsNullOrWhiteSpace($GroupEvidenceIndexPath)) {
    $evidencePath = [IO.Path]::GetFullPath($GroupEvidenceIndexPath)
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
        throw "Group evidence index was not found: $evidencePath"
    }
    $evidenceIndex = Get-Content -LiteralPath $evidencePath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$evidenceIndex.schemaVersion -ne 1) {
        throw "Unsupported group evidence schema version: $($evidenceIndex.schemaVersion)"
    }

    $catalogById = @{}
    foreach ($record in $catalogRecords) { $catalogById[[int]$record.id] = $record }
    $expectedEvidenceKeys = @{}
    foreach ($candidate in @($evidenceIndex.exactBuildCandidates)) {
        $locationId = [int]$candidate.Rowid
        if (-not $catalogById.ContainsKey($locationId)) { continue }
        $reviewedGroupIds = @($catalogById[$locationId].groups | ForEach-Object { [int]$_.id })
        foreach ($groupId in @($candidate.groupIds | ForEach-Object { [int]$_ })) {
            if ($groupId -notin $reviewedGroupIds) { continue }
            $revisionId = [int]$candidate.revisionId
            if ($revisionId -le 0 -or [string]::IsNullOrWhiteSpace([string]$candidate.groupUrl)) { continue }
            $expectedEvidenceKeys['{0}:{1}:{2}' -f $locationId, $groupId, $revisionId] = $true
        }
    }

    $actualEvidenceKeys = @{}
    $validatedEvidenceLocationCount = 0
    foreach ($record in $catalogRecords) {
        $recordEvidence = @($record.groupEvidence)
        if ($recordEvidence.Count -gt 0) { $validatedEvidenceLocationCount++ }
        foreach ($evidence in $recordEvidence) {
            $key = '{0}:{1}:{2}' -f [int]$record.id, [int]$evidence.groupId, [int]$evidence.revisionId
            if ($actualEvidenceKeys.ContainsKey($key)) { throw "Duplicate group evidence citation: $key" }
            $actualEvidenceKeys[$key] = $true
            if (@($record.sources) -notcontains [string]$evidence.revisionUrl) {
                throw "Location $($record.id) omits group evidence revision $($evidence.revisionId) from sources."
            }
            $entityHtmlPath = Join-Path $siteRoot "entities\locations\$($record.id)\index.html"
            $entityHtml = [IO.File]::ReadAllText($entityHtmlPath)
            if ($entityHtml -notmatch ('oldid=' + [regex]::Escape([string]$evidence.revisionId)) -or
                $entityHtml -notmatch 'Revision-pinned') {
                throw "Location $($record.id) does not expose group evidence $($evidence.revisionId) in HTML and JSON-LD."
            }
        }
    }
    $missingEvidence = @($expectedEvidenceKeys.Keys | Where-Object { -not $actualEvidenceKeys.ContainsKey($_) })
    $extraEvidence = @($actualEvidenceKeys.Keys | Where-Object { -not $expectedEvidenceKeys.ContainsKey($_) })
    if ($missingEvidence.Count -gt 0 -or $extraEvidence.Count -gt 0) {
        throw "Group evidence intersection mismatch: $($missingEvidence.Count) missing, $($extraEvidence.Count) extra."
    }
    if ($actualEvidenceKeys.Count -ne $groupEvidenceCitationCount -or
        $validatedEvidenceLocationCount -ne $groupEvidenceLocationCount) {
        throw "Group evidence count mismatch: expected $groupEvidenceCitationCount/$groupEvidenceLocationCount, validated $($actualEvidenceKeys.Count)/$validatedEvidenceLocationCount."
    }
}
if (@($catalogRecords | Where-Object { [string]$_.uuid -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' }).Count -gt 0) {
    throw 'The generated entity catalog contains a missing or invalid UUID.'
}
$missingModified = @($catalogRecords | Where-Object { [string]$_.dateModified -notmatch '^20\d{2}-\d{2}-\d{2}$' })
if ($missingModified.Count -gt 0) {
    throw "The generated entity catalog contains $($missingModified.Count) missing or invalid modification dates."
}
function Get-JsonMemberCount {
    param([object]$Object, [string]$Name)

    if ($null -eq $Object) { return 0 }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return 0 }
    return @($property.Value).Count
}
$sitemapText = [IO.File]::ReadAllText((Join-Path $siteRoot 'sitemap.xml'))
[xml]$sitemapXml = $sitemapText
$sitemapByUrl = @{}
foreach ($urlNode in @($sitemapXml.urlset.url)) { $sitemapByUrl[[string]$urlNode.loc] = [string]$urlNode.lastmod }
$staticSitemapUrls = @(
    "$SiteBaseUrl/", "$SiteBaseUrl/about", "$SiteBaseUrl/api", "$SiteBaseUrl/extras",
    "$SiteBaseUrl/groups", "$SiteBaseUrl/map", "$SiteBaseUrl/entities/locations/",
    "$SiteBaseUrl/entities/groups/", "$SiteBaseUrl/entities/media/",
    "$SiteBaseUrl/entities/world-downloads/", "$SiteBaseUrl/mcp/", "$SiteBaseUrl/nocom/", "$SiteBaseUrl/locations/",
    "$SiteBaseUrl/locations/overworld/", "$SiteBaseUrl/locations/nether/", "$SiteBaseUrl/locations/end/"
)
if ($sitemapByUrl.Count -ne $catalogRecords.Count + $groupCatalogRecords.Count + $staticSitemapUrls.Count) {
    throw "Sitemap URL count ($($sitemapByUrl.Count)) does not equal location/group entities plus $($staticSitemapUrls.Count) static pages ($($catalogRecords.Count + $groupCatalogRecords.Count + $staticSitemapUrls.Count))."
}
foreach ($staticSitemapUrl in $staticSitemapUrls) {
    if (-not $sitemapByUrl.ContainsKey($staticSitemapUrl)) {
        throw "Sitemap is missing public static/interactive route $staticSitemapUrl."
    }
}
$imageAttachmentCount = @($catalogRecords | ForEach-Object { @($_.attachments) } | Where-Object {
    [string]$_.mediaType -match '(?i)^image$' -and [string]$_.url -match '^https://'
}).Count
$imageSitemapEntryCount = [regex]::Matches($sitemapText, '<image:image>').Count
if ($imageSitemapEntryCount -ne $imageAttachmentCount) {
    throw "Image sitemap entry count ($imageSitemapEntryCount) does not match indexable image attachments ($imageAttachmentCount)."
}
foreach ($record in $catalogRecords) {
    if (-not $sitemapByUrl.ContainsKey([string]$record.url) -or
        $sitemapByUrl[[string]$record.url] -ne [string]$record.dateModified) {
        throw "Sitemap lastmod mismatch for $($record.url)."
    }
    if ([int]$record.relationshipCounts.groups -ne @($record.groups).Count -or
        [int]$record.relationshipCounts.warps -ne @($record.warps).Count -or
        [int]$record.relationshipCounts.worldDownloads -ne (@($record.warps | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count +
            @($record.renders | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count) -or
        [int]$record.relationshipCounts.renders -ne @($record.renders).Count -or
        [int]$record.relationshipCounts.attachments -ne @($record.attachments).Count) {
        throw "Location $($record.id) relationship counts diverge from its JSONL arrays."
    }
    foreach ($resource in @($record.warps) + @($record.renders) + @($record.attachments)) {
        if ([string]::IsNullOrWhiteSpace([string]$resource.apiUrl)) {
            throw "Location $($record.id) contains a resource without an API URL."
        }
    }
    $locationPagePath = Join-Path $siteRoot "entities\locations\$($record.id)\index.html"
    if (-not (Test-Path $locationPagePath -PathType Leaf)) { throw "Missing location entity page for $($record.id)." }
    $locationPageHtml = [IO.File]::ReadAllText($locationPagePath)
    $locationJsonLdMatch = [regex]::Match($locationPageHtml, '<script type="application/ld\+json">(.*?)</script>', 'Singleline')
    if (-not $locationJsonLdMatch.Success) { throw "Location $($record.id) is missing JSON-LD." }
    $locationGraph = @(($locationJsonLdMatch.Groups[1].Value | ConvertFrom-Json).'@graph')
    $locationEntity = $locationGraph | Where-Object { [string]$_.'@id' -eq "$($record.url)#entity" } | Select-Object -First 1
    $downloadableWarpCount = @($record.warps | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    }).Count
    $downloadableRenderCount = @($record.renders | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    }).Count
    if ($null -eq $locationEntity -or
        (Get-JsonMemberCount $locationEntity 'contributor') -ne @($record.groups).Count -or
        (Get-JsonMemberCount $locationEntity 'associatedMedia') -ne @($record.attachments).Count -or
        (Get-JsonMemberCount $locationEntity 'subjectOf') -ne (1 + @($record.renders).Count + $downloadableWarpCount + $downloadableRenderCount) -or
        (Get-JsonMemberCount $locationEntity 'identifier') -ne (1 + @($record.warps).Count)) {
        throw "Location $($record.id) JSON-LD relationship counts diverge from the JSONL catalog."
    }
    foreach ($group in @($record.groups)) {
        if ($locationPageHtml -notmatch [regex]::Escape([string]$group.url)) {
            throw "Location $($record.id) does not link its attributed group $($group.id)."
        }
        if (-not @($locationEntity.contributor | Where-Object { [string]$_.'@id' -eq "$($group.url)#organization" }).Count) {
            throw "Location $($record.id) JSON-LD omits attributed group $($group.id)."
        }
    }
    foreach ($warp in @($record.warps)) {
        $warpNode = @($locationEntity.identifier | Where-Object { [string]$_.url -eq [string]$warp.apiUrl }) | Select-Object -First 1
        $warpConceptNode = @($warpNode.additionalProperty | Where-Object { [string]$_.name -eq 'Single-player concept' }) | Select-Object -First 1
        if ($null -eq $warpNode -or
            $locationPageHtml -notmatch [regex]::Escape([string]$warp.apiUrl)) {
            throw "Location $($record.id) does not expose warp $($warp.id) consistently in HTML and JSON-LD."
        }
        if ($null -eq $warpConceptNode -or [bool]$warpConceptNode.value -ne [bool]$warp.isSinglePlayerConcept) {
            throw "Location $($record.id) warp $($warp.id) has inconsistent single-player concept metadata."
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$warp.worldDownloadUrl)) {
            $downloadNode = @($locationEntity.subjectOf | Where-Object {
                [string]$_.'@type' -eq 'DataDownload' -and
                [string]$_.contentUrl -eq [string]$warp.worldDownloadUrl -and
                [string]$_.url -eq [string]$warp.worldDownloadMetadataUrl
            }) | Select-Object -First 1
            if ($null -eq $downloadNode -or
                $locationPageHtml -notmatch [regex]::Escape([string]$warp.worldDownloadUrl) -or
                $locationPageHtml -notmatch [regex]::Escape([string]$warp.worldDownloadMetadataUrl)) {
                throw "Location $($record.id) does not expose bounded world download $($warp.id) consistently in HTML and JSON-LD."
            }
        }
    }
    foreach ($render in @($record.renders)) {
        $renderNode = @($locationEntity.subjectOf | Where-Object { [string]$_.url -eq [string]$render.apiUrl }) | Select-Object -First 1
        $renderConceptNode = @($renderNode.additionalProperty | Where-Object { [string]$_.name -eq 'Single-player concept' }) | Select-Object -First 1
        if ($null -eq $renderNode -or
            $locationPageHtml -notmatch [regex]::Escape([string]$render.apiUrl)) {
            throw "Location $($record.id) does not expose render $($render.id) consistently in HTML and JSON-LD."
        }
        if ($null -eq $renderConceptNode -or [bool]$renderConceptNode.value -ne [bool]$render.isSinglePlayerConcept) {
            throw "Location $($record.id) render $($render.id) has inconsistent single-player concept metadata."
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$render.worldDownloadUrl)) {
            $downloadNode = @($locationEntity.subjectOf | Where-Object {
                [string]$_.'@type' -eq 'DataDownload' -and
                [string]$_.contentUrl -eq [string]$render.worldDownloadUrl -and
                [string]$_.url -eq [string]$render.worldDownloadMetadataUrl
            }) | Select-Object -First 1
            if ($null -eq $downloadNode -or
                $locationPageHtml -notmatch [regex]::Escape([string]$render.worldDownloadUrl) -or
                $locationPageHtml -notmatch [regex]::Escape([string]$render.worldDownloadMetadataUrl)) {
                throw "Location $($record.id) does not expose preserved render source $($render.id) consistently in HTML and JSON-LD."
            }
        }
    }
    foreach ($attachment in @($record.attachments)) {
        $mediaNode = @($locationEntity.associatedMedia | Where-Object { [string]$_.url -eq [string]$attachment.apiUrl }) | Select-Object -First 1
        if ($null -eq $mediaNode -or
            $locationPageHtml -notmatch [regex]::Escape([string]$attachment.apiUrl)) {
            throw "Location $($record.id) does not expose attachment $($attachment.id) consistently in HTML and JSON-LD."
        }
        if ([string]$mediaNode.contentUrl -ne [string]$attachment.url -or
            [string]::IsNullOrWhiteSpace([string]$mediaNode.encodingFormat)) {
            throw "Location $($record.id) attachment $($attachment.id) lacks a synchronized content URL or encoding format in JSON-LD."
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$attachment.sourceUrl) -and
            [string]$mediaNode.isBasedOn -ne [string]$attachment.sourceUrl) {
            throw "Location $($record.id) attachment $($attachment.id) lacks source provenance in JSON-LD."
        }
        if ([string]$attachment.mediaType -match '(?i)^image$' -and $locationPageHtml -notmatch '<img\s[^>]*src=') {
            throw "Location $($record.id) has image attachments but no crawlable semantic image element."
        }
    }
}
$worldDownloadsById = @{}
foreach ($worldDownloadRecord in $worldDownloadCatalogRecords) {
    $worldDownloadId = [int]$worldDownloadRecord.id
    $sourceType = [string]$worldDownloadRecord.sourceType
    $worldDownloadKey = "$sourceType-$worldDownloadId"
    if ([int]$worldDownloadRecord.schemaVersion -ne 2 -or
        $sourceType -notin @('archive-warp', 'render') -or
        $worldDownloadsById.ContainsKey($worldDownloadKey)) {
        throw "World-download catalog contains a duplicate identity or unsupported schema: $worldDownloadKey."
    }
    if ([string]$worldDownloadRecord.type -ne 'world-download' -or
        [bool]$worldDownloadRecord.isCompleteWorld -or
        [string]$worldDownloadRecord.playability -ne 'partial-java-save' -or
        [string]$worldDownloadRecord.encodingFormat -ne 'application/zip' -or
        [string]::IsNullOrWhiteSpace([string]$worldDownloadRecord.contentUrl) -or
        [string]::IsNullOrWhiteSpace([string]$worldDownloadRecord.metadataUrl) -or
        [string]$worldDownloadRecord.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "World-download record $worldDownloadId is missing bounded-world semantics, URLs, or checksum evidence."
    }
    $worldDownloadsById[$worldDownloadKey] = $worldDownloadRecord
}
$expectedWorldDownloadCount = @($catalogRecords | ForEach-Object { @($_.warps) } | Where-Object {
    -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
}).Count + @($catalogRecords | ForEach-Object { @($_.renders) } | Where-Object {
    -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
}).Count
if ($worldDownloadCatalogRecords.Count -ne $expectedWorldDownloadCount) {
    throw "World-download catalog count ($($worldDownloadCatalogRecords.Count)) does not match downloadable location warps and render sources ($expectedWorldDownloadCount)."
}
foreach ($locationRecord in $catalogRecords) {
    foreach ($warp in @($locationRecord.warps | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) })) {
        $worldDownloadRecord = $worldDownloadsById["archive-warp-$([int]$warp.id)"]
        if ($null -eq $worldDownloadRecord -or
            [int]$worldDownloadRecord.location.id -ne [int]$locationRecord.id -or
            [int]$worldDownloadRecord.warp.id -ne [int]$warp.id -or
            [bool]$worldDownloadRecord.isSinglePlayerConcept -ne [bool]$warp.isSinglePlayerConcept -or
            [string]$worldDownloadRecord.contentUrl -ne [string]$warp.worldDownloadUrl -or
            [string]$worldDownloadRecord.metadataUrl -ne [string]$warp.worldDownloadMetadataUrl -or
            [string]$worldDownloadRecord.scope -ne [string]$warp.worldDownloadScope -or
            [string]$worldDownloadRecord.sha256 -ne ([string]$warp.archiveSha256).ToLowerInvariant()) {
            throw "Location $($locationRecord.id) WDL $($warp.id) diverges from world-downloads.jsonl."
        }
        if ([bool]$worldDownloadRecord.isSinglePlayerConcept -and
            ([string]$worldDownloadRecord.fidelityNotice -notmatch 'not a live 2b2t snapshot' -or
             [string]$worldDownloadRecord.contentUrl -notmatch 'singleplayer-concept')) {
            throw "Single-player concept WDL $($warp.id) is missing its public warning or concept-marked filename."
        }
        $expectedRenderIds = @($locationRecord.renders | Where-Object { $null -ne $_.archiveWarpId -and [int]$_.archiveWarpId -eq [int]$warp.id } | ForEach-Object { [int]$_.id } | Sort-Object)
        $actualRenderIds = @($worldDownloadRecord.renderIds | ForEach-Object { [int]$_ } | Sort-Object)
        if ($expectedRenderIds.Count -ne $actualRenderIds.Count -or
            @($expectedRenderIds | Where-Object { $_ -notin $actualRenderIds }).Count -gt 0) {
            throw "World-download record $($warp.id) has unsynchronized render relationships."
        }
    }
    foreach ($render in @($locationRecord.renders | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) })) {
        $worldDownloadRecord = $worldDownloadsById["render-$([int]$render.id)"]
        if ($null -eq $worldDownloadRecord -or
            [string]$worldDownloadRecord.sourceType -ne 'render' -or
            [int]$worldDownloadRecord.location.id -ne [int]$locationRecord.id -or
            [int]$worldDownloadRecord.render.id -ne [int]$render.id -or
            [string]$worldDownloadRecord.contentUrl -ne [string]$render.worldDownloadUrl -or
            [string]$worldDownloadRecord.metadataUrl -ne [string]$render.worldDownloadMetadataUrl -or
            [string]$worldDownloadRecord.scope -ne [string]$render.worldDownloadScope -or
            [string]$worldDownloadRecord.sha256 -ne ([string]$render.worldDownloadSha256).ToLowerInvariant()) {
            throw "Location $($locationRecord.id) preserved render WDL $($render.id) diverges from world-downloads.jsonl."
        }
        $actualRenderIds = @($worldDownloadRecord.renderIds | ForEach-Object { [int]$_ })
        if ($actualRenderIds.Count -ne 1 -or $actualRenderIds[0] -ne [int]$render.id) {
            throw "World-download render record $($render.id) has unsynchronized render relationships."
        }
    }
}
$mediaById = @{}
foreach ($mediaRecord in $mediaCatalogRecords) {
    if ([int]$mediaRecord.schemaVersion -ne 1 -or $mediaById.ContainsKey([int]$mediaRecord.id)) {
        throw "Media catalog contains a duplicate ID or unsupported schema: $($mediaRecord.id)."
    }
    if ([string]::IsNullOrWhiteSpace([string]$mediaRecord.contentUrl) -or
        [string]::IsNullOrWhiteSpace([string]$mediaRecord.apiUrl) -or
        [string]::IsNullOrWhiteSpace([string]$mediaRecord.location.url) -or
        [string]::IsNullOrWhiteSpace([string]$mediaRecord.encodingFormat)) {
        throw "Media record $($mediaRecord.id) is missing content, API, or owning-location URLs."
    }
    $mediaById[[int]$mediaRecord.id] = $mediaRecord
}
if ($mediaCatalogRecords.Count -ne $attachmentCount) {
    throw "Media catalog count ($($mediaCatalogRecords.Count)) does not match location attachment count ($attachmentCount)."
}
foreach ($locationRecord in $catalogRecords) {
    foreach ($attachment in @($locationRecord.attachments)) {
        $mediaRecord = $mediaById[[int]$attachment.id]
        if ($null -eq $mediaRecord -or [int]$mediaRecord.location.id -ne [int]$locationRecord.id -or
            [string]$mediaRecord.contentUrl -ne [string]$attachment.url -or
            [string]$mediaRecord.sourceUrl -ne [string]$attachment.sourceUrl) {
            throw "Location $($locationRecord.id) attachment $($attachment.id) diverges from media.jsonl."
        }
    }
}
foreach ($record in $groupCatalogRecords) {
    if (-not $sitemapByUrl.ContainsKey([string]$record.url) -or
        $sitemapByUrl[[string]$record.url] -ne [string]$record.dateModified) {
        throw "Sitemap lastmod mismatch for $($record.url)."
    }
}
$directoryHtml = [IO.File]::ReadAllText((Join-Path $siteRoot 'entities\locations\index.html'))
if ($directoryHtml -notmatch '"@type":"Dataset"' -or $directoryHtml -notmatch '"@type":"DataDownload"') {
    throw 'The entity directory is missing Dataset/DataDownload JSON-LD.'
}
$groupDirectoryHtml = [IO.File]::ReadAllText((Join-Path $siteRoot 'entities\groups\index.html'))
if ($groupDirectoryHtml -notmatch '"@type":"Dataset"' -or $groupDirectoryHtml -notmatch '"@type":"DataDownload"' -or
    $groupDirectoryHtml -notmatch 'entities/groups\.jsonl') {
    throw 'The group directory is missing Dataset/DataDownload JSON-LD or its JSONL discovery link.'
}
$mediaDirectoryHtml = [IO.File]::ReadAllText((Join-Path $siteRoot 'entities\media\index.html'))
if ($mediaDirectoryHtml -notmatch '"@type":"Dataset"' -or $mediaDirectoryHtml -notmatch 'entities/media\.jsonl') {
    throw 'The media directory is missing Dataset JSON-LD or JSONL discovery.'
}
$worldDownloadDirectoryHtml = [IO.File]::ReadAllText((Join-Path $siteRoot 'entities\world-downloads\index.html'))
if ($worldDownloadDirectoryHtml -notmatch '"@type":"Dataset"' -or
    $worldDownloadDirectoryHtml -notmatch '"@type":"DataDownload"' -or
    $worldDownloadDirectoryHtml -notmatch 'entities/world-downloads\.jsonl' -or
    $worldDownloadDirectoryHtml -notmatch 'partial world') {
    throw 'The world-download directory is missing Dataset/DataDownload JSON-LD, JSONL discovery, or its partial-world fidelity notice.'
}
if (@($groupCatalogRecords | Where-Object { [int]$_.schemaVersion -ne 2 }).Count -gt 0) {
    throw 'The group entity catalog contains a record with an unsupported schema version.'
}
foreach ($groupRecord in $groupCatalogRecords) {
    $groupPagePath = Join-Path $siteRoot "entities\groups\$($groupRecord.id)\index.html"
    if (-not (Test-Path $groupPagePath -PathType Leaf)) { throw "Missing group entity page for $($groupRecord.id)." }
    $groupPageHtml = [IO.File]::ReadAllText($groupPagePath)
    $jsonLdMatch = [regex]::Match($groupPageHtml, '<script type="application/ld\+json">(.*?)</script>', 'Singleline')
    if (-not $jsonLdMatch.Success) { throw "Group $($groupRecord.id) is missing JSON-LD." }
    $groupJsonLd = $jsonLdMatch.Groups[1].Value | ConvertFrom-Json
    $graph = @($groupJsonLd.'@graph')
    $graphTypes = @($graph | ForEach-Object { $_.'@type' })
    if ($graphTypes -notcontains 'Organization' -or $graphTypes -notcontains 'WebPage' -or
        $graphTypes -notcontains 'BreadcrumbList' -or @($graphTypes | Where-Object { $_ -eq 'ItemList' }).Count -ne 2) {
        throw "Group $($groupRecord.id) JSON-LD does not expose the organization, page, breadcrumb, and both attribution lists."
    }
    $organization = $graph | Where-Object { [string]$_.'@type' -eq 'Organization' } | Select-Object -First 1
    $catalogAliases = @($groupRecord.aliases | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $jsonLdAliases = @($organization.alternateName | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($catalogAliases.Count -ne $jsonLdAliases.Count -or
        ($catalogAliases.Count -gt 0 -and @(Compare-Object -ReferenceObject $catalogAliases -DifferenceObject $jsonLdAliases -SyncWindow 0).Count -gt 0)) {
        throw "Group $($groupRecord.id) reviewed aliases diverge between JSONL and JSON-LD."
    }
    foreach ($alias in $catalogAliases) {
        if ($groupPageHtml -notmatch [regex]::Escape($alias)) {
            throw "Group $($groupRecord.id) alias '$alias' is absent from its human-readable entity page."
        }
    }
    $buildList = $graph | Where-Object { [string]$_.'@id' -eq "$($groupRecord.url)#attributed-builds" } | Select-Object -First 1
    $highwayList = $graph | Where-Object { [string]$_.'@id' -eq "$($groupRecord.url)#attributed-highways" } | Select-Object -First 1
    if ([int]$buildList.numberOfItems -ne @($groupRecord.locations).Count -or
        [int]$highwayList.numberOfItems -ne @($groupRecord.highways).Count) {
        throw "Group $($groupRecord.id) JSON-LD attribution counts diverge from the JSONL catalog."
    }
    if ([int]$groupRecord.relationshipCounts.locations -ne @($groupRecord.locations).Count -or
        [int]$groupRecord.relationshipCounts.highways -ne @($groupRecord.highways).Count) {
        throw "Group $($groupRecord.id) relationship counts diverge from its JSONL arrays."
    }
    foreach ($locationLink in @($groupRecord.locations)) {
        if ($groupPageHtml -notmatch [regex]::Escape([string]$locationLink.url) -or
            -not @($buildList.itemListElement | Where-Object { [string]$_.item.url -eq [string]$locationLink.url }).Count) {
            throw "Group $($groupRecord.id) does not expose location $($locationLink.id) consistently in HTML and JSON-LD."
        }
    }
    foreach ($highwayLink in @($groupRecord.highways)) {
        if ($groupPageHtml -notmatch [regex]::Escape([string]$highwayLink.apiUrl) -or
            $groupPageHtml -notmatch [regex]::Escape([string]$highwayLink.mapUrl) -or
            -not @($highwayList.itemListElement | Where-Object { [string]$_.item.url -eq [string]$highwayLink.apiUrl }).Count) {
            throw "Group $($groupRecord.id) does not expose highway $($highwayLink.id) consistently in HTML and JSON-LD."
        }
    }
}
$locationsById = @{}
foreach ($record in $catalogRecords) { $locationsById[[int]$record.id] = $record }
$groupsById = @{}
foreach ($groupRecord in $groupCatalogRecords) { $groupsById[[int]$groupRecord.id] = $groupRecord }
foreach ($groupRecord in $groupCatalogRecords) {
    foreach ($locationLink in @($groupRecord.locations)) {
        $locationRecord = $locationsById[[int]$locationLink.id]
        if ($null -eq $locationRecord -or -not @($locationRecord.groups | Where-Object { [int]$_.id -eq [int]$groupRecord.id }).Count) {
            throw "Group $($groupRecord.id) -> location $($locationLink.id) lacks its reciprocal location -> group edge."
        }
    }
}
foreach ($locationRecord in $catalogRecords) {
    foreach ($groupLink in @($locationRecord.groups)) {
        $groupRecord = $groupsById[[int]$groupLink.id]
        if ($null -eq $groupRecord -or -not @($groupRecord.locations | Where-Object { [int]$_.id -eq [int]$locationRecord.id }).Count) {
            throw "Location $($locationRecord.id) -> group $($groupLink.id) lacks its reciprocal group -> location edge."
        }
    }
}
if ($locationGroupRelationshipCount -ne $groupLocationRelationshipCount) {
    throw "Location/group edge counts diverge ($locationGroupRelationshipCount vs $groupLocationRelationshipCount)."
}
$rootDataset = [IO.File]::ReadAllText((Join-Path $siteRoot 'dataset.json'))
$legacyDataset = [IO.File]::ReadAllText((Join-Path $siteRoot 'entities\dataset.json'))
if ($rootDataset -ne $legacyDataset -or ($rootDataset | ConvertFrom-Json).recordCount -ne $catalogRecords.Count -or
    ($rootDataset | ConvertFrom-Json).groupCount -ne $groupCatalogRecords.Count -or
    ($rootDataset | ConvertFrom-Json).warpCount -ne $warpCount -or
    ($rootDataset | ConvertFrom-Json).renderCount -ne $renderCount -or
    ($rootDataset | ConvertFrom-Json).attachmentCount -ne $attachmentCount -or
    ($rootDataset | ConvertFrom-Json).worldDownloadCount -ne $worldDownloadCatalogRecords.Count -or
    ($rootDataset | ConvertFrom-Json).groupRelationshipCount -ne $locationGroupRelationshipCount -or
    ($rootDataset | ConvertFrom-Json).groupLocationRelationshipCount -ne $groupLocationRelationshipCount -or
    ($rootDataset | ConvertFrom-Json).groupHighwayRelationshipCount -ne $groupHighwayRelationshipCount -or
    ($rootDataset | ConvertFrom-Json).groupEvidenceCitationCount -ne $groupEvidenceCitationCount -or
    ($rootDataset | ConvertFrom-Json).groupEvidenceLocationCount -ne $groupEvidenceLocationCount -or
    ($rootDataset | ConvertFrom-Json).schemaVersion -ne 5 -or
    ($rootDataset | ConvertFrom-Json).groupCatalogUrl -ne "$SiteBaseUrl/entities/groups.jsonl" -or
    ($rootDataset | ConvertFrom-Json).mediaCatalogUrl -ne "$SiteBaseUrl/entities/media.jsonl" -or
    ($rootDataset | ConvertFrom-Json).worldDownloadCatalogUrl -ne "$SiteBaseUrl/entities/world-downloads.jsonl") {
    throw 'Root dataset discovery metadata is absent, divergent, or has the wrong record count.'
}
$datasetMetadata = $rootDataset | ConvertFrom-Json
if ([string]$datasetMetadata.license -notmatch 'reused for any purpose without permission' -or
    [string]$datasetMetadata.attribution -notmatch 'optional and appreciated') {
    throw 'Root dataset reuse metadata is inconsistent with the public Atlas reuse policy.'
}
if ([string]$datasetMetadata.ingestionPipeline.documentationUrl -ne "$SiteBaseUrl/about#ingestion-pipeline" -or
    @($datasetMetadata.ingestionPipeline.stages).Count -ne 7 -or
    @($datasetMetadata.ingestionPipeline.dimensions).Count -ne 3 -or
    [string]$datasetMetadata.ingestionPipeline.failureIsolation -notmatch 'does not roll back' -or
    [string]$datasetMetadata.ingestionPipeline.enrichment.execution -notmatch 'Local model' -or
    [string]$datasetMetadata.ingestionPipeline.enrichment.reviewPolicy -notmatch 'administrator review' -or
    -not @($datasetMetadata.ingestionPipeline.software | Where-Object { $_.name -eq 'Local AI enrichment' }).Count -or
    -not @($datasetMetadata.ingestionPipeline.software | Where-Object { $_.name -eq 'uNmINeD' -and $_.url -eq 'https://unmined.net/' }).Count -or
    -not @($datasetMetadata.ingestionPipeline.software | Where-Object { $_.name -eq 'BlueMap' -and $_.url -eq 'https://bluemap.bluecolored.de/' }).Count) {
    throw 'Root dataset metadata is missing the machine-readable Atlas ingestion and rendering pipeline.'
}
$datasetStages = @($datasetMetadata.ingestionPipeline.stages | Sort-Object position)
for ($step = 0; $step -lt $aboutStepNames.Count; $step++) {
    if ([int]$datasetStages[$step].position -ne ($step + 1) -or [string]$datasetStages[$step].name -cne $aboutStepNames[$step]) {
        throw 'Dataset ingestion stages differ from the visible About explanation.'
    }
}
$packagedHome = [IO.File]::ReadAllText((Join-Path $siteRoot 'index.html'))
if ($packagedHome -match '(?i)interactive Atlas could not start|unhandled error|locations could not be loaded') {
    throw 'The packaged homepage contains crawler-visible failure text.'
}
if ($packagedHome -notmatch '"@type": "WebSite"' -or $packagedHome -notmatch '"@type": "DataCatalog"' -or
    $packagedHome -notmatch '"@type": "HowTo"' -or $packagedHome -notmatch 'about#ingestion-pipeline' -or
    $packagedHome -notmatch 'Local AI enrichment with reviewed 2b2t sources' -or
    $packagedHome -notmatch 'entities/groups\.jsonl' -or $packagedHome -notmatch 'entities/media\.jsonl' -or
    $packagedHome -notmatch 'entities/world-downloads\.jsonl' -or $packagedHome -notmatch 'mcp/') {
    throw 'The packaged homepage is missing WebSite/DataCatalog JSON-LD or machine-readable catalog discovery.'
}
$homeSchema = [regex]::Match($packagedHome, '(?s)<script type="application/ld\+json">\s*(.*?)</script>').Groups[1].Value | ConvertFrom-Json
$homePipeline = @($homeSchema.'@graph' | Where-Object { $_.'@type' -eq 'HowTo' })
if ($homePipeline.Count -ne 1 -or @($homePipeline[0].step).Count -ne $aboutStepNames.Count) {
    throw 'Homepage ingestion steps are missing or duplicated.'
}
$homeSteps = @($homePipeline[0].step | Sort-Object position)
for ($step = 0; $step -lt $aboutStepNames.Count; $step++) {
    if ([int]$homeSteps[$step].position -ne ($step + 1) -or [string]$homeSteps[$step].name -cne $aboutStepNames[$step]) {
        throw 'Homepage ingestion steps differ from the visible About explanation.'
    }
}
$mcpGuidePath = Join-Path $siteRoot 'mcp\index.html'
if (-not (Test-Path -LiteralPath $mcpGuidePath -PathType Leaf)) { throw 'The MCP crawler guide is missing.' }
$mcpGuideHtml = [IO.File]::ReadAllText($mcpGuidePath)
if ($mcpGuideHtml -notmatch 'http://127\.0\.0\.1:5297/mcp' -or
    $mcpGuideHtml -notmatch '"@type":"WebAPI"' -or
    $mcpGuideHtml -notmatch 'search_locations' -or
    $mcpGuideHtml -notmatch '2b2tatlas://location/\{id\}') {
    throw 'The MCP crawler guide is missing its endpoint, schema, tool inventory, or resource templates.'
}
$llmsText = [IO.File]::ReadAllText((Join-Path $siteRoot 'llms.txt'))
$nocomDataset = [IO.File]::ReadAllText((Join-Path $siteRoot 'nocom\dataset.json')) | ConvertFrom-Json
$nocomPeriods = @([IO.File]::ReadAllLines((Join-Path $siteRoot 'nocom\periods.jsonl')) | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$nocomHighways = @([IO.File]::ReadAllLines((Join-Path $siteRoot 'nocom\highways.jsonl')) | Where-Object { $_.Trim() })
$nocomPage = [IO.File]::ReadAllText((Join-Path $siteRoot 'nocom\index.html'))
if ($nocomPeriods.Count -ne 39 -or $nocomHighways.Count -ne 272 -or
    [long]$nocomDataset.observations -ne [long](($nocomPeriods | Measure-Object observations -Sum).Sum) -or
    $nocomPage -notmatch 'application/ld\+json' -or $nocomPage -notmatch 'get_nocom_periods' -or
    $llmsText -notmatch '/nocom/dataset.json' -or $rootDataset -notmatch 'historicalObservationDatasets') {
    throw 'Nocom public data/crawl export verification failed.'
}
if ($llmsText -notmatch '(?m)^## Agent access\r?$' -or $llmsText -notmatch 'http://127\.0\.0\.1:5297/mcp' -or
    $llmsText -notmatch '(?m)^## Preservation and rendering pipeline\r?$' -or
    $llmsText -notmatch 'https://unmined\.net/' -or $llmsText -notmatch 'https://bluemap\.bluecolored\.de/') {
    throw 'llms.txt is missing MCP discovery or source-backed 2D/3D pipeline guidance.'
}
$packagedRobots = [IO.File]::ReadAllText((Join-Path $siteRoot 'robots.txt'))
if ($packagedRobots -notmatch '(?m)^Sitemap: https://atlas\.example/sitemap\.xml\r?$' -or
    $packagedRobots -notmatch '(?m)^Disallow: /jsonapi\r?$') {
    throw 'robots.txt is missing canonical sitemap or duplicate-machine-surface policy.'
}
if ($htaccess -notmatch 'X-Robots-Tag "noindex, follow" env=atlas_interactive_entity') {
    throw 'The package is missing the interactive-location noindex response header.'
}
foreach ($entityKind in 'locations', 'groups') {
    $guardPattern = '(?m)RewriteCond %\{REQUEST_FILENAME\} !-f\r?\nRewriteCond %\{REQUEST_FILENAME\} !-d\r?\nRewriteRule \^entities/' + $entityKind + '/\[0-9\]\+/\?\$ - \[R=404,L\]'
    if ($htaccess -notmatch $guardPattern) {
        throw "The missing-$entityKind 404 rule is not guarded by both !-f and !-d; existing entity URLs would be broken."
    }
}
if ($htaccess -notmatch 'RewriteRule \^images/\(journeymap\\\.png\|xaeroplus\\\.webp\)\$ Images/\$1 \[L\]' -or
    $htaccess -notmatch 'A missing static asset must not receive the Blazor shell with HTTP 200') {
    throw 'The package is missing its case-compatibility or false-200 static-asset safeguards.'
}
$semanticFiles = @(
    Get-ChildItem (Join-Path $siteRoot 'entities') -Recurse -File |
        Where-Object Extension -in '.html', '.json', '.jsonl'
    Get-ChildItem (Join-Path $siteRoot 'locations') -Recurse -File |
        Where-Object Extension -eq '.html'
    Get-Item (Join-Path $siteRoot 'llms.txt'), (Join-Path $siteRoot 'sitemap.xml'), (Join-Path $siteRoot 'dataset.json')
)
$corruptSemanticFiles = @($semanticFiles | Where-Object {
    [IO.File]::ReadAllText($_.FullName) -match '(?i)rnrn|\\[rn]'
})
if ($corruptSemanticFiles.Count -gt 0) {
    throw "Generated semantic files contain legacy newline artifacts: $($corruptSemanticFiles.FullName -join ', ')"
}

Remove-Item $publishRoot -Recurse -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = "$OutputDirectory.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($siteRoot, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "Namecheap package: $zipPath"
Write-Host "Extract its contents into public_html."
