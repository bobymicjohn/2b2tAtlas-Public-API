[CmdletBinding()]
param(
    [ValidatePattern('^https://')]
    [string]$SiteBaseUrl = 'https://atlas.example',

    [ValidateRange(1, 100)]
    [int]$SampleCount = 25
)

$ErrorActionPreference = 'Stop'
$SiteBaseUrl = $SiteBaseUrl.TrimEnd('/')

function Get-Page {
    param([string]$Url)

    $response = Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 30
    if ($response.StatusCode -ne 200) { throw "$Url returned HTTP $($response.StatusCode)." }
    $contentType = [string]$response.Headers['Content-Type']
    if ($response.Content -is [byte[]] -and $contentType -match '(?i)^(text/|application/(json|x-ndjson|xml|javascript))') {
        $encoding = [Text.Encoding]::UTF8
        $charsetMatch = [regex]::Match($contentType, '(?i)charset\s*=\s*["'']?([^;"'']+)')
        if ($charsetMatch.Success) {
            try { $encoding = [Text.Encoding]::GetEncoding($charsetMatch.Groups[1].Value.Trim()) } catch { }
        }
        $decodedContent = $encoding.GetString([byte[]]$response.Content)
        $response | Add-Member -MemberType NoteProperty -Name Content -Value $decodedContent -Force
    }
    return $response
}

function Get-HttpStatusNoRedirect {
    param([string]$Url)

    # Windows PowerShell 5.1 treats -MaximumRedirection 0 as an error and does
    # not reliably retain the redirect response on the thrown exception. Use
    # HttpWebRequest directly so 3xx status and Location remain inspectable.
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.Method = 'GET'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.UserAgent = '2b2tAtlas production SEO test'
    $response = $null
    try {
        try {
            $response = [Net.HttpWebResponse]$request.GetResponse()
        } catch [Net.WebException] {
            if ($null -eq $_.Exception.Response) { throw }
            $response = [Net.HttpWebResponse]$_.Exception.Response
        }

        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Location = [string]$response.Headers['Location']
        }
    } finally {
        if ($null -ne $response) { $response.Close() }
    }
}

function Get-MatchValue {
    param([string]$Html, [string]$Pattern, [string]$Label, [string]$Url)

    $match = [regex]::Match($Html, $Pattern, 'IgnoreCase,Singleline')
    if (-not $match.Success) { throw "$Url is missing $Label." }
    return [Net.WebUtility]::HtmlDecode($match.Groups[1].Value.Trim())
}

$rootResponse = Get-Page "$SiteBaseUrl/"
if ($rootResponse.Content -match '(?i)unhandled error|locations could not be loaded|interactive Atlas could not start|0 locations') {
    throw 'The homepage raw response contains a crawler-visible failure state.'
}
if ($rootResponse.Content -notmatch '(?i)<h1[^>]*>\s*2b2t Atlas\s*</h1>') {
    throw 'The homepage raw response is missing its semantic H1.'
}
if ($rootResponse.Content -notmatch '"@type":\s*"WebSite"') {
    throw 'The homepage raw response is missing its WebSite JSON-LD.'
}
if ($rootResponse.Content -notmatch '"@type":\s*"WebAPI"' -or $rootResponse.Content -notmatch '/mcp/') {
    throw 'The homepage raw response is missing MCP WebAPI discovery.'
}

$wwwResponse = Get-HttpStatusNoRedirect 'https://atlas.example/'
if ($wwwResponse.StatusCode -notin 301, 308) { throw "www host returned HTTP $($wwwResponse.StatusCode), expected 301 or 308." }
$redirectTarget = $wwwResponse.Location
if ($redirectTarget -ne "$SiteBaseUrl/") { throw "www host redirects to '$redirectTarget', expected '$SiteBaseUrl/'." }

$goneUuid = '00000000-0000-0000-0000-000000000000'
$goneResponse = Get-HttpStatusNoRedirect "$SiteBaseUrl/$goneUuid"
if ($goneResponse.StatusCode -ne 410) { throw "Unknown legacy UUID returned HTTP $($goneResponse.StatusCode), expected 410." }

# This NFE URL was observed in a search index as the generic Atlas homepage.
# Keep it as a stable regression probe in addition to the sampled catalog URLs.
$indexedLegacyUuid = '0cb0080f-e0e1-11ea-bb3c-0200516ae545'
$indexedLegacyEntityId = 43
foreach ($legacyHost in $SiteBaseUrl, 'https://atlas.example') {
    $indexedResponse = Get-HttpStatusNoRedirect "$legacyHost/$indexedLegacyUuid"
    $indexedTarget = "$SiteBaseUrl/entities/locations/$indexedLegacyEntityId/"
    if ($indexedResponse.StatusCode -ne 301 -or $indexedResponse.Location -ne $indexedTarget) {
        throw "$legacyHost/$indexedLegacyUuid did not redirect directly to '$indexedTarget'."
    }
}

$robots = Get-Page "$SiteBaseUrl/robots.txt"
$llms = Get-Page "$SiteBaseUrl/llms.txt"
$sitemapResponse = Get-Page "$SiteBaseUrl/sitemap.xml"
$catalogResponse = Get-Page "$SiteBaseUrl/entities/locations.jsonl"
$groupCatalogResponse = Get-Page "$SiteBaseUrl/entities/groups.jsonl"
$mediaCatalogResponse = Get-Page "$SiteBaseUrl/entities/media.jsonl"
$worldDownloadCatalogResponse = Get-Page "$SiteBaseUrl/entities/world-downloads.jsonl"
$datasetResponse = Get-Page "$SiteBaseUrl/dataset.json"
foreach ($collectionPath in '/locations/', '/locations/overworld/', '/locations/nether/', '/locations/end/') {
    $collection = Get-Page "$SiteBaseUrl$collectionPath"
    if ($collection.Content -notmatch '(?i)<h1') { throw "$collectionPath is missing an H1." }
}
if ($robots.Content -notmatch [regex]::Escape("$SiteBaseUrl/sitemap.xml")) { throw 'robots.txt does not reference the canonical sitemap.' }
if ($robots.Content -notmatch '(?m)^Disallow: /jsonapi$') { throw 'robots.txt does not identify the duplicate machine surface.' }
if ($llms.Content -notmatch 'entities/locations.jsonl') { throw 'llms.txt does not reference the entity catalog.' }
if ($llms.Content -notmatch 'entities/groups.jsonl') { throw 'llms.txt does not reference the group entity catalog.' }
if ($llms.Content -notmatch 'entities/media.jsonl') { throw 'llms.txt does not reference the historical media catalog.' }
if ($llms.Content -notmatch 'entities/world-downloads.jsonl') { throw 'llms.txt does not reference the world-download catalog.' }
if ($llms.Content -notmatch '(?m)^## Agent access\r?$' -or $llms.Content -notmatch 'http://127\.0\.0\.1:5297/mcp') {
    throw 'llms.txt does not advertise the public MCP service.'
}
if ([string]$sitemapResponse.Headers['Content-Type'] -notmatch '(?i)application/xml|text/xml') { throw 'sitemap.xml has the wrong content type.' }
$groupDirectory = Get-Page "$SiteBaseUrl/entities/groups/"
if ($groupDirectory.Content -notmatch '"@type":"Dataset"' -or $groupDirectory.Content -notmatch '"@type":"DataDownload"') {
    throw 'The group directory is missing Dataset/DataDownload JSON-LD.'
}
$mediaDirectory = Get-Page "$SiteBaseUrl/entities/media/"
if ($mediaDirectory.Content -notmatch '"@type":"Dataset"' -or $mediaDirectory.Content -notmatch '"@type":"DataDownload"') {
    throw 'The historical media directory is missing Dataset/DataDownload JSON-LD.'
}
$worldDownloadDirectory = Get-Page "$SiteBaseUrl/entities/world-downloads/"
if ($worldDownloadDirectory.Content -notmatch '"@type":"Dataset"' -or
    $worldDownloadDirectory.Content -notmatch '"@type":"DataDownload"' -or
    $worldDownloadDirectory.Content -notmatch 'partial world') {
    throw 'The world-download directory is missing Dataset/DataDownload JSON-LD or its partial-world fidelity notice.'
}
$mcpGuide = Get-Page "$SiteBaseUrl/mcp/"
if ($mcpGuide.Content -notmatch '"@type":"WebAPI"' -or
    $mcpGuide.Content -notmatch 'http://127\.0\.0\.1:5297/mcp' -or
    $mcpGuide.Content -notmatch 'search_locations' -or
    $mcpGuide.Content -notmatch '2b2tatlas://location/\{id\}') {
    throw 'The MCP guide is missing WebAPI schema, endpoint, tools, or stable resources.'
}

foreach ($iconPath in '/Images/journeymap.png', '/Images/xaeroplus.webp', '/images/journeymap.png', '/images/xaeroplus.webp') {
    $iconResponse = Get-Page "$SiteBaseUrl$iconPath"
    if ([string]$iconResponse.Headers['Content-Type'] -notmatch '^image/') { throw "$iconPath did not return an image." }
}
$missingAsset = Get-HttpStatusNoRedirect "$SiteBaseUrl/Images/atlas-deliberately-missing.png"
if ($missingAsset.StatusCode -ne 404) { throw "A missing static image returned HTTP $($missingAsset.StatusCode), expected 404." }

[xml]$sitemap = $sitemapResponse.Content
$sitemapUrls = New-Object 'Collections.Generic.HashSet[string]'
$sitemapLastModified = @{}
foreach ($urlNode in $sitemap.urlset.url) {
    [void]$sitemapUrls.Add([string]$urlNode.loc)
    $sitemapLastModified[[string]$urlNode.loc] = [string]$urlNode.lastmod
}
$records = @($catalogResponse.Content -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$groupRecords = @($groupCatalogResponse.Content -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$mediaRecords = @($mediaCatalogResponse.Content -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$worldDownloadRecords = @($worldDownloadCatalogResponse.Content -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
$dataset = $datasetResponse.Content | ConvertFrom-Json
if ($records.Count -eq 0 -or $dataset.recordCount -ne $records.Count) { throw 'Dataset metadata and JSONL record counts do not match.' }
if ($groupRecords.Count -eq 0 -or $dataset.groupCount -ne $groupRecords.Count -or $dataset.schemaVersion -ne 5) {
    throw 'Dataset metadata and group JSONL record counts do not match.'
}
$warpCount = [int](@($records | ForEach-Object { @($_.warps).Count } | Measure-Object -Sum).Sum)
$renderCount = [int](@($records | ForEach-Object { @($_.renders).Count } | Measure-Object -Sum).Sum)
$attachmentCount = [int](@($records | ForEach-Object { @($_.attachments).Count } | Measure-Object -Sum).Sum)
if ($mediaRecords.Count -ne $attachmentCount -or $dataset.mediaCatalogUrl -ne "$SiteBaseUrl/entities/media.jsonl") {
    throw 'Historical media catalog count or dataset discovery metadata is not synchronized.'
}
$downloadableWorldCount = @($records | ForEach-Object { @($_.warps) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count +
    @($records | ForEach-Object { @($_.renders) } | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count
if ($worldDownloadRecords.Count -ne $downloadableWorldCount -or
    $dataset.worldDownloadCount -ne $downloadableWorldCount -or
    $dataset.worldDownloadCatalogUrl -ne "$SiteBaseUrl/entities/world-downloads.jsonl") {
    throw 'World-download catalog count or dataset discovery metadata is not synchronized.'
}
foreach ($worldDownloadRecord in $worldDownloadRecords) {
    if ([int]$worldDownloadRecord.schemaVersion -ne 2 -or
        [string]$worldDownloadRecord.sourceType -notin @('archive-warp', 'render') -or
        [string]$worldDownloadRecord.type -ne 'world-download' -or
        [bool]$worldDownloadRecord.isCompleteWorld -or [string]$worldDownloadRecord.playability -ne 'partial-java-save' -or
        [string]$worldDownloadRecord.contentUrl -notmatch '^https://' -or [string]$worldDownloadRecord.metadataUrl -notmatch '^https://' -or
        [string]$worldDownloadRecord.sha256 -notmatch '^[0-9a-f]{64}$' -or [string]$worldDownloadRecord.location.url -notmatch '^https://') {
        throw "World-download record $($worldDownloadRecord.id) is incomplete or lacks bounded-world semantics."
    }
}
foreach ($mediaRecord in $mediaRecords) {
    if ([int]$mediaRecord.schemaVersion -ne 1 -or
        [string]$mediaRecord.contentUrl -notmatch '^https://' -or
        [string]$mediaRecord.apiUrl -notmatch '^https://' -or
        [string]$mediaRecord.location.url -notmatch '^https://' -or
        [string]::IsNullOrWhiteSpace([string]$mediaRecord.encodingFormat)) {
        throw "Historical media record $($mediaRecord.id) is incomplete or uses a non-canonical URL."
    }
}
$locationGroupCount = [int](@($records | ForEach-Object { @($_.groups).Count } | Measure-Object -Sum).Sum)
$groupLocationCount = [int](@($groupRecords | ForEach-Object { @($_.locations).Count } | Measure-Object -Sum).Sum)
$groupHighwayCount = [int](@($groupRecords | ForEach-Object { @($_.highways).Count } | Measure-Object -Sum).Sum)
if ([int]$dataset.warpCount -ne $warpCount -or [int]$dataset.renderCount -ne $renderCount -or
    [int]$dataset.attachmentCount -ne $attachmentCount -or
    [int]$dataset.groupRelationshipCount -ne $locationGroupCount -or
    [int]$dataset.groupLocationRelationshipCount -ne $groupLocationCount -or
    [int]$dataset.groupHighwayRelationshipCount -ne $groupHighwayCount -or
    $locationGroupCount -ne $groupLocationCount) {
    throw 'Dataset resource/relationship totals are not synchronized with the entity catalogs.'
}
$locationsById = @{}
foreach ($record in $records) {
    $locationsById[[int]$record.id] = $record
    if ([int]$record.relationshipCounts.groups -ne @($record.groups).Count -or
        [int]$record.relationshipCounts.warps -ne @($record.warps).Count -or
        [int]$record.relationshipCounts.worldDownloads -ne (@($record.warps | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count +
            @($record.renders | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count) -or
        [int]$record.relationshipCounts.renders -ne @($record.renders).Count -or
        [int]$record.relationshipCounts.attachments -ne @($record.attachments).Count) {
        throw "Location $($record.id) relationship counts do not match its arrays."
    }
    foreach ($resource in @($record.warps) + @($record.renders) + @($record.attachments)) {
        if ([string]::IsNullOrWhiteSpace([string]$resource.apiUrl)) {
            throw "Location $($record.id) contains a resource without an API URL."
        }
    }
}
$groupsById = @{}
foreach ($record in $groupRecords) {
    $groupsById[[int]$record.id] = $record
    if ([int]$record.relationshipCounts.locations -ne @($record.locations).Count -or
        [int]$record.relationshipCounts.highways -ne @($record.highways).Count) {
        throw "Group $($record.id) relationship counts do not match its arrays."
    }
}
foreach ($locationRecord in $records) {
    foreach ($groupLink in @($locationRecord.groups)) {
        $groupRecord = $groupsById[[int]$groupLink.id]
        if ($null -eq $groupRecord -or -not @($groupRecord.locations | Where-Object { [int]$_.id -eq [int]$locationRecord.id }).Count) {
            throw "Location $($locationRecord.id) -> group $($groupLink.id) is not reciprocal."
        }
    }
}
foreach ($groupRecord in $groupRecords) {
    foreach ($locationLink in @($groupRecord.locations)) {
        $locationRecord = $locationsById[[int]$locationLink.id]
        if ($null -eq $locationRecord -or -not @($locationRecord.groups | Where-Object { [int]$_.id -eq [int]$groupRecord.id }).Count) {
            throw "Group $($groupRecord.id) -> location $($locationLink.id) is not reciprocal."
        }
    }
}
if (@($records | Where-Object { [string]$_.description -match '(?i)rnrn' }).Count -gt 0) { throw 'The entity catalog contains literal rnrn corruption.' }
if (@($records | Where-Object { [string]$_.uuid -notmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' }).Count -gt 0) { throw 'The entity catalog contains a missing or invalid UUID.' }
if (@($records | Where-Object { [string]$_.dateModified -notmatch '^20\d{2}-\d{2}-\d{2}$' }).Count -gt 0) { throw 'The entity catalog contains a missing or invalid modification date.' }

$step = [Math]::Max(1, [Math]::Floor($records.Count / [Math]::Min($SampleCount, $records.Count)))
$sample = @()
for ($index = 0; $index -lt $records.Count -and $sample.Count -lt $SampleCount; $index += $step) {
    $sample += $records[$index]
}

$titles = New-Object 'Collections.Generic.HashSet[string]'
$descriptions = New-Object 'Collections.Generic.HashSet[string]'
$results = New-Object Collections.Generic.List[object]
foreach ($record in $sample) {
    $entityResponse = Get-Page ([string]$record.url)
    $html = $entityResponse.Content
    if ($html -match '(?i)unhandled error') { throw "$($record.url) contains an unhandled-error message." }
    if ($html -match '(?i)rnrn') { throw "$($record.url) contains literal rnrn corruption." }
    if ($html.Length -lt 1000) { throw "$($record.url) returned too little semantic HTML." }

    $title = Get-MatchValue $html '<title>(.*?)</title>' 'title' $record.url
    $description = Get-MatchValue $html '<meta\s+name="description"\s+content="([^"]+)"' 'meta description' $record.url
    $heading = Get-MatchValue $html '<h1[^>]*>(.*?)</h1>' 'H1' $record.url
    $canonical = Get-MatchValue $html '<link\s+rel="canonical"\s+href="([^"]+)"' 'canonical link' $record.url
    $imageUrl = Get-MatchValue $html '<meta\s+property="og:image"\s+content="([^"]+)"' 'OG image' $record.url
    $jsonLdText = Get-MatchValue $html '<script\s+type="application/ld\+json">(.*?)</script>' 'JSON-LD' $record.url
    $jsonLd = $jsonLdText | ConvertFrom-Json
    $graphTypes = @($jsonLd.'@graph' | ForEach-Object { $_.'@type' })

    if (-not $titles.Add($title)) { throw "Duplicate sampled title: $title" }
    if (-not $descriptions.Add($description)) { throw "Duplicate sampled meta description: $description" }
    if ($heading -ne [string]$record.name) { throw "$($record.url) H1 '$heading' does not match '$($record.name)'." }
    if ($canonical -ne [string]$record.url) { throw "$($record.url) canonical is '$canonical'." }
    if ($graphTypes -notcontains 'WebPage' -or $graphTypes -notcontains 'Thing') { throw "$($record.url) JSON-LD is missing WebPage or Thing." }
    if (-not $sitemapUrls.Contains([string]$record.url)) { throw "$($record.url) is absent from sitemap.xml." }
    if ($sitemapLastModified[[string]$record.url] -ne [string]$record.dateModified) {
        throw "$($record.url) sitemap lastmod does not match its public modification date."
    }
    foreach ($groupLink in @($record.groups)) {
        if ($html -notmatch [regex]::Escape([string]$groupLink.url)) {
            throw "$($record.url) HTML omits group relationship $($groupLink.id)."
        }
    }

    $legacyResponse = Get-HttpStatusNoRedirect "$SiteBaseUrl/$($record.uuid)"
    if ($legacyResponse.StatusCode -ne 301) { throw "$SiteBaseUrl/$($record.uuid) did not return HTTP 301." }
    $expectedRedirects = @("/entities/locations/$($record.id)/", "$SiteBaseUrl/entities/locations/$($record.id)/")
    if ($legacyResponse.Location -notin $expectedRedirects) {
        throw "$SiteBaseUrl/$($record.uuid) redirects to '$($legacyResponse.Location)'."
    }

    [void](Get-Page $imageUrl)
    [void](Get-Page ([string]$record.apiUrl))
    $interactiveResponse = Get-Page ([string]$record.interactiveUrl)
    if ([string]$interactiveResponse.Headers['X-Robots-Tag'] -notmatch '(?i)noindex') {
        throw "$($record.interactiveUrl) is missing the duplicate-route noindex header."
    }
    $results.Add([pscustomobject]@{ Id = $record.id; Name = $record.name; Status = 'pass' })
}

# Group URLs are few enough to verify exhaustively. This catches one broken
# physical-directory rewrite even when a sampled subset would appear healthy.
$groupSample = $groupRecords
$groupResults = New-Object Collections.Generic.List[object]
foreach ($record in $groupSample) {
    $entityResponse = Get-Page ([string]$record.url)
    $html = $entityResponse.Content
    $heading = Get-MatchValue $html '<h1[^>]*>(.*?)</h1>' 'H1' $record.url
    $canonical = Get-MatchValue $html '<link\s+rel="canonical"\s+href="([^"]+)"' 'canonical link' $record.url
    $jsonLdText = Get-MatchValue $html '<script\s+type="application/ld\+json">(.*?)</script>' 'JSON-LD' $record.url
    $jsonLd = $jsonLdText | ConvertFrom-Json
    $graph = @($jsonLd.'@graph')
    $graphTypes = @($graph | ForEach-Object { $_.'@type' })
    if ($heading -ne [string]$record.name -or $canonical -ne [string]$record.url) {
        throw "$($record.url) group heading or canonical does not match its catalog record."
    }
    if ($graphTypes -notcontains 'Organization' -or $graphTypes -notcontains 'WebPage' -or
        @($graphTypes | Where-Object { $_ -eq 'ItemList' }).Count -ne 2) {
        throw "$($record.url) does not expose Organization plus build/highway ItemList schema."
    }
    if (-not $sitemapUrls.Contains([string]$record.url) -or
        $sitemapLastModified[[string]$record.url] -ne [string]$record.dateModified) {
        throw "$($record.url) is missing from the sitemap or has the wrong lastmod."
    }
    [void](Get-Page ([string]$record.apiUrl))
    $interactiveResponse = Get-Page ([string]$record.interactiveUrl)
    if ([string]$interactiveResponse.Headers['X-Robots-Tag'] -notmatch '(?i)noindex') {
        throw "$($record.interactiveUrl) is missing the duplicate-route noindex header."
    }
    $groupResults.Add([pscustomobject]@{ Id = $record.id; Name = $record.name; Status = 'pass' })
}

foreach ($resource in @(
    ($records | ForEach-Object { @($_.warps) } | Where-Object { $_ } | Select-Object -First 1),
    ($records | ForEach-Object { @($_.renders) } | Where-Object { $_ } | Select-Object -First 1),
    ($records | ForEach-Object { @($_.attachments) } | Where-Object { $_ } | Select-Object -First 1)
)) {
    if ($null -ne $resource) { [void](Get-Page ([string]$resource.apiUrl)) }
}

[pscustomobject]@{
    Homepage = 'pass'
    CanonicalHostRedirect = 'pass'
    Collections = 4
    DatasetRecords = $records.Count
    GroupRecords = $groupRecords.Count
    GroupBuildRelationships = $locationGroupCount
    GroupHighwayRelationships = $groupHighwayCount
    Warps = $warpCount
    Renders = $renderCount
    Attachments = $attachmentCount
    SitemapUrls = $sitemapUrls.Count
    SampledEntities = $results.Count
    SampledGroups = $groupResults.Count
    Result = 'pass'
} | Format-List
