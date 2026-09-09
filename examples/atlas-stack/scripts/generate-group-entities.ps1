[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://')]
    [string]$ApiBaseUrl,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^https://')]
    [string]$SiteBaseUrl,

    [string]$DataApiBaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')
$SiteBaseUrl = $SiteBaseUrl.TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($DataApiBaseUrl)) { $DataApiBaseUrl = $ApiBaseUrl }
$DataApiBaseUrl = $DataApiBaseUrl.TrimEnd('/')
if ($DataApiBaseUrl -notmatch '^https://' -and
    $DataApiBaseUrl -notmatch '^http://(127\.0\.0\.1|localhost)(:\d+)?$') {
    throw 'DataApiBaseUrl must use HTTPS, except for loopback staging endpoints.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$utf8NoBom = New-Object Text.UTF8Encoding($false)

# Canonical, human-reviewed aliases from GroupSeeder. The public groups API does not
# currently persist aliases, so keep this deliberately small and exact rather than
# attempting to infer alternate names from prose.
$reviewedGroupAliases = @{
    'Valkyria'                            = @('Space Valkyria')
    'The Emperium'                       = @('Emperium')
    'DGA'                                = @('Democratic Group Alliance', 'Small Groups Alliance')
    'Shortbus Caliphate'                 = @('SBC')
    'The Mew Revolution'                 = @('Mew Revolution')
    'Vapepens Elite Alliance'            = @('VEA')
    'The Society Project'                = @('The Society')
    'Spawn Infrastructure Group (SIG)'   = @('SIG')
    'Independent Interstate Society (IIS)' = @('IIS')
    'Motorway Extension Gurus (MEG)'     = @('MEG')
    'Headpats4All'                       = @('H4A')
    'Peacekeepers'                       = @('PK')
    'The Watchmen'                       = @('Watchmen')
}

function Get-ReviewedGroupAliases {
    param(
        [AllowNull()][string]$Name,
        [AllowNull()][object[]]$ApiAliases
    )
    if ([string]::IsNullOrWhiteSpace($Name)) { return @() }
    $candidates = @($ApiAliases | ForEach-Object { [string]$_ } | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    })
    if ($candidates.Count -eq 0 -and $reviewedGroupAliases.ContainsKey($Name)) {
        $candidates = @($reviewedGroupAliases[$Name])
    }
    return @($candidates | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_) -and
        -not [string]::Equals([string]$_, $Name, [StringComparison]::OrdinalIgnoreCase)
    } | Select-Object -Unique)
}

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

function Encode-Html {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return '' }
    return [Net.WebUtility]::HtmlEncode([string]$Value)
}

function Normalize-Text {
    param([AllowNull()][string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $Value = [Text.RegularExpressions.Regex]::Replace($Value, '(?i)\\[rn]', ' ')
    $Value = [Text.RegularExpressions.Regex]::Replace($Value, '(?i)(?:rn){2,}', ' ')
    return ([Text.RegularExpressions.Regex]::Replace($Value, '\s+', ' ')).Trim()
}

function Get-IsoDate {
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return $null }
    try { return ([DateTime]$Value).ToUniversalTime().ToString('yyyy-MM-dd') } catch { return $null }
}

function Get-TypeLabel {
    param($Value)
    switch ([int]$Value) {
        0 { return 'Building group' }
        1 { return 'Infrastructure group' }
        2 { return 'Building and infrastructure group' }
        default { return 'Faction or community' }
    }
}

function Get-DimensionLabel {
    param($Value)
    switch ([int]$Value) {
        0 { return 'Overworld' }
        1 { return 'Nether' }
        2 { return 'End' }
        default { return 'Unknown dimension' }
    }
}

function Get-MetaDescription {
    param($Group)
    $description = Normalize-Text $Group.description
    if ([string]::IsNullOrWhiteSpace($description)) {
        $description = '{0} is a documented 2b2t {1}.' -f $Group.name, (Get-TypeLabel $Group.type).ToLowerInvariant()
    }
    if ($description.Length -gt 160) { return $description.Substring(0, 157).TrimEnd() + '...' }
    return $description
}

function New-GroupJsonLd {
    param($Group, [string]$CanonicalUrl, [string]$InteractiveUrl, [string]$ApiUrl)
    $sameAs = @(@($Group.wikiUrl, $Group.websiteUrl, $Group.discordUrl) |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Select-Object -Unique)
    $locationItems = New-Object Collections.Generic.List[object]
    $position = 0
    foreach ($location in @($Group.locations)) {
        $position++
        $locationUrl = "$SiteBaseUrl/entities/locations/$($location.locationId)/"
        $locationItems.Add([ordered]@{
            '@type' = 'ListItem'
            position = $position
            item = [ordered]@{
                '@type' = 'Place'
                '@id' = "$locationUrl#entity"
                name = [string]$location.name
                url = $locationUrl
                additionalProperty = @(
                    [ordered]@{ '@type' = 'PropertyValue'; name = 'Attribution role'; value = [string]$location.role },
                    [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft dimension'; value = Get-DimensionLabel $location.dimension },
                    [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft X coordinate'; value = [int]$location.x },
                    [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft Z coordinate'; value = [int]$location.z },
                    [ordered]@{ '@type' = 'PropertyValue'; name = 'Public render count'; value = [int]$location.renderCount }
                )
            }
        })
    }
    $highwayItems = New-Object Collections.Generic.List[object]
    $position = 0
    foreach ($highway in @($Group.highways)) {
        $position++
        $highwayApiUrl = "$ApiBaseUrl/api/highways/$($highway.highwayId)"
        $highwayItems.Add([ordered]@{
            '@type' = 'ListItem'
            position = $position
            item = [ordered]@{
                '@type' = 'Place'
                '@id' = "$highwayApiUrl#highway"
                name = [string]$highway.name
                url = $highwayApiUrl
                additionalProperty = @(
                    [ordered]@{ '@type' = 'PropertyValue'; name = 'Attribution role'; value = [string]$highway.role },
                    [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft dimension'; value = Get-DimensionLabel $highway.dimension }
                )
            }
        })
    }
    $buildList = [ordered]@{
        '@type' = 'ItemList'
        '@id' = "$CanonicalUrl#attributed-builds"
        name = "Atlas locations attributed to $($Group.name)"
        numberOfItems = $locationItems.Count
        itemListOrder = 'https://schema.org/ItemListOrderAscending'
        itemListElement = $locationItems.ToArray()
    }
    $highwayList = [ordered]@{
        '@type' = 'ItemList'
        '@id' = "$CanonicalUrl#attributed-highways"
        name = "Atlas highways attributed to $($Group.name)"
        numberOfItems = $highwayItems.Count
        itemListOrder = 'https://schema.org/ItemListOrderAscending'
        itemListElement = $highwayItems.ToArray()
    }
    $aliases = @(Get-ReviewedGroupAliases ([string]$Group.name) @($Group.aliases))
    $organization = [ordered]@{
        '@type' = 'Organization'
        '@id' = "$CanonicalUrl#organization"
        name = [string]$Group.name
        description = Get-MetaDescription $Group
        url = $CanonicalUrl
        identifier = "2b2tAtlas-group-$($Group.id)"
        sameAs = $sameAs
        mainEntityOfPage = [ordered]@{ '@id' = "$CanonicalUrl#webpage" }
        potentialAction = [ordered]@{ '@type' = 'ViewAction'; target = $InteractiveUrl }
        subjectOf = @(
            [ordered]@{ '@type' = 'Dataset'; name = '2b2t Atlas group API record'; url = $ApiUrl; encodingFormat = 'application/json' },
            [ordered]@{ '@id' = "$CanonicalUrl#attributed-builds" },
            [ordered]@{ '@id' = "$CanonicalUrl#attributed-highways" }
        )
    }
    if ($aliases.Count -gt 0) { $organization.alternateName = $aliases }
    if (-not [string]::IsNullOrWhiteSpace([string]$Group.logoUrl)) { $organization.logo = [string]$Group.logoUrl }
    $organization.additionalProperty = @(
        [ordered]@{ '@type' = 'PropertyValue'; name = 'Founded'; value = if ([string]::IsNullOrWhiteSpace([string]$Group.founded)) { 'Unknown' } else { [string]$Group.founded } },
        [ordered]@{ '@type' = 'PropertyValue'; name = 'Status'; value = if ([string]::IsNullOrWhiteSpace([string]$Group.status)) { 'Unknown' } else { [string]$Group.status } }
    )

    $webPage = [ordered]@{
        '@type' = 'WebPage'
        '@id' = "$CanonicalUrl#webpage"
        url = $CanonicalUrl
        name = [string]$Group.name
        description = Get-MetaDescription $Group
        mainEntity = [ordered]@{ '@id' = "$CanonicalUrl#organization" }
        about = @(
            [ordered]@{ '@id' = "$CanonicalUrl#organization" },
            [ordered]@{ '@id' = "$CanonicalUrl#attributed-builds" },
            [ordered]@{ '@id' = "$CanonicalUrl#attributed-highways" }
        )
        breadcrumb = [ordered]@{ '@id' = "$CanonicalUrl#breadcrumb" }
        isPartOf = [ordered]@{ '@type' = 'WebSite'; '@id' = "$SiteBaseUrl/#website"; name = '2b2t Atlas'; url = "$SiteBaseUrl/" }
    }
    $modified = Get-IsoDate $Group.modifiedUtc
    $created = Get-IsoDate $Group.dateAddedUtc
    if (-not $modified) { $modified = $created }
    if ($created) { $webPage.datePublished = $created }
    if ($modified) { $webPage.dateModified = $modified; $organization.dateModified = $modified }

    $graph = [ordered]@{
        '@context' = 'https://schema.org'
        '@graph' = @(
            $webPage,
            $organization,
            [ordered]@{
                '@type' = 'BreadcrumbList'
                '@id' = "$CanonicalUrl#breadcrumb"
                itemListElement = @(
                    [ordered]@{ '@type' = 'ListItem'; position = 1; name = '2b2t Atlas'; item = "$SiteBaseUrl/" },
                    [ordered]@{ '@type' = 'ListItem'; position = 2; name = 'Group entities'; item = "$SiteBaseUrl/entities/groups/" },
                    [ordered]@{ '@type' = 'ListItem'; position = 3; name = [string]$Group.name; item = $CanonicalUrl }
                )
            },
            $buildList,
            $highwayList
        )
    }
    return (($graph | ConvertTo-Json -Depth 10 -Compress).Replace('&', '\u0026').Replace('<', '\u003C').Replace('>', '\u003E'))
}

function New-GroupHtml {
    param($Group)
    $canonicalUrl = "$SiteBaseUrl/entities/groups/$($Group.id)/"
    $interactiveUrl = "$SiteBaseUrl/group/$($Group.id)"
    $apiUrl = "$ApiBaseUrl/api/groups/$($Group.id)"
    $description = Get-MetaDescription $Group
    $jsonLd = New-GroupJsonLd $Group $canonicalUrl $interactiveUrl $apiUrl
    $typeLabel = Get-TypeLabel $Group.type
    $history = Normalize-Text $Group.description
    if ([string]::IsNullOrWhiteSpace($history)) { $history = $description }
    $aliases = @(Get-ReviewedGroupAliases ([string]$Group.name) @($Group.aliases))
    $aliasText = if ($aliases.Count -eq 0) { '' } else {
        '<p class="meta">Also known as: {0}</p>' -f (Encode-Html ($aliases -join ', '))
    }
    $logo = if ([string]::IsNullOrWhiteSpace([string]$Group.logoUrl)) { '' } else {
        '<p><img class="logo" src="{0}" alt="{1} emblem" loading="lazy"></p>' -f (Encode-Html $Group.logoUrl), (Encode-Html $Group.name)
    }
    $sourceLinks = New-Object Collections.Generic.List[string]
    foreach ($link in @(
        [pscustomobject]@{ Label = '2b2t Wiki'; Url = [string]$Group.wikiUrl },
        [pscustomobject]@{ Label = 'Official website'; Url = [string]$Group.websiteUrl },
        [pscustomobject]@{ Label = 'Discord'; Url = [string]$Group.discordUrl },
        [pscustomobject]@{ Label = 'Logo source'; Url = [string]$Group.logoSourceUrl }
    )) {
        if (-not [string]::IsNullOrWhiteSpace($link.Url)) {
            $sourceLinks.Add(('<li><a href="{0}" rel="noopener">{1}</a></li>' -f (Encode-Html $link.Url), (Encode-Html $link.Label)))
        }
    }
    $sources = if ($sourceLinks.Count -eq 0) { '' } else { '<section><h2>Sources and links</h2><ul>{0}</ul></section>' -f ($sourceLinks -join '') }
    $buildItems = @($Group.locations | ForEach-Object {
        '<li><a href="{0}/entities/locations/{1}/">{2}</a> <span>{3}; {4}; X {5}, Z {6}</span></li>' -f `
            $SiteBaseUrl, $_.locationId, (Encode-Html $_.name), (Encode-Html $_.role), (Encode-Html (Get-DimensionLabel $_.dimension)), $_.x, $_.z
    })
    $builds = if ($buildItems.Count -eq 0) { '<p>No Atlas locations have a reviewed attribution yet.</p>' } else { '<ul>{0}</ul>' -f ($buildItems -join '') }
    $highwayItems = @($Group.highways | ForEach-Object {
        $mapUrl = "$SiteBaseUrl/map?dimension=$((Get-DimensionLabel $_.dimension).ToLowerInvariant())"
        '<li><a href="{0}">{1}</a> <span>{2}; {3}</span> <a href="{4}/api/highways/{5}">[data]</a></li>' -f `
            (Encode-Html $mapUrl), (Encode-Html $_.name), (Encode-Html $_.role), (Encode-Html (Get-DimensionLabel $_.dimension)), $ApiBaseUrl, $_.highwayId
    })
    $highways = if ($highwayItems.Count -eq 0) { '<p>No public Atlas highways have a reviewed attribution yet.</p>' } else { '<ul>{0}</ul>' -f ($highwayItems -join '') }
    $founded = if ([string]::IsNullOrWhiteSpace([string]$Group.founded)) { 'Unknown' } else { Encode-Html $Group.founded }
    $status = if ([string]::IsNullOrWhiteSpace([string]$Group.status)) { 'Unknown' } else { Encode-Html $Group.status }
    $openGraphImage = if ([string]::IsNullOrWhiteSpace([string]$Group.logoUrl)) { "$SiteBaseUrl/Images/banner_web.png" } else { [string]$Group.logoUrl }

    return @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>$(Encode-Html $Group.name) - 2b2t Group | 2b2t Atlas</title><meta name="description" content="$(Encode-Html $description)">
<meta name="robots" content="index,follow,max-snippet:-1"><link rel="canonical" href="$(Encode-Html $canonicalUrl)">
<link rel="alternate" type="application/json" href="$(Encode-Html $apiUrl)" title="Group API record">
<meta property="og:type" content="website"><meta property="og:site_name" content="2b2t Atlas"><meta property="og:title" content="$(Encode-Html $Group.name)">
<meta property="og:description" content="$(Encode-Html $description)"><meta property="og:url" content="$(Encode-Html $canonicalUrl)">
<meta property="og:image" content="$(Encode-Html $openGraphImage)"><meta name="twitter:card" content="summary"><meta name="twitter:image" content="$(Encode-Html $openGraphImage)">
<script type="application/ld+json">$jsonLd</script>
<style>body{max-width:980px;margin:auto;padding:24px;background:#111518;color:#e8edf0;font:16px/1.55 Arial,sans-serif}a{color:#83bdff}.logo{width:144px;height:144px;object-fit:contain;border-radius:16px;background:#20262b}article{background:#181d21;padding:24px;border-radius:12px}h1{margin-bottom:6px}li{margin:8px 0}span,.meta{color:#9caab2}</style></head>
<body><main><p><a href="$SiteBaseUrl/">2b2t Atlas</a> / <a href="$SiteBaseUrl/entities/groups/">Groups</a></p><article>$logo<h1>$(Encode-Html $Group.name)</h1>
<p class="meta">$(Encode-Html $typeLabel) &middot; Founded: $founded &middot; Status: $status</p>$aliasText<p>$(Encode-Html $history)</p>
<p><a href="$(Encode-Html $interactiveUrl)">Open the interactive group page</a></p>
<section><h2>Attributed bases and builds</h2>$builds</section><section><h2>Attributed highways</h2>$highways</section>$sources
</article></main></body></html>
"@
}

$groupsResponse = Invoke-RestMethod "$DataApiBaseUrl/api/groups" -TimeoutSec 120
$groups = @($groupsResponse | ForEach-Object { $_ })
if ($groups.Count -eq 0) { throw 'The groups API returned no records.' }
if (@($groups | Group-Object id | Where-Object Count -ne 1).Count -gt 0) { throw 'The groups API returned duplicate IDs.' }

$entityRoot = Join-Path $OutputDirectory 'entities\groups'
$indexItems = New-Object Collections.Generic.List[string]
$jsonLines = New-Object Collections.Generic.List[string]
$sitemapItems = New-Object Collections.Generic.List[string]
foreach ($summary in ($groups | Sort-Object id)) {
    $group = Invoke-RestMethod "$DataApiBaseUrl/api/groups/$($summary.id)" -TimeoutSec 60
    if ([int]$group.locationCount -ne @($group.locations).Count -or [int]$group.highwayCount -ne @($group.highways).Count) {
        throw "Group $($group.id) summary counts do not match its relationship arrays."
    }
    $canonicalUrl = "$SiteBaseUrl/entities/groups/$($group.id)/"
    Write-Utf8File (Join-Path $entityRoot "$($group.id)\index.html") (New-GroupHtml $group)
    $indexItems.Add(('<li><a href="{0}">{1}</a> <span>{2}; {3} build(s); {4} highway(s)</span></li>' -f `
        (Encode-Html $canonicalUrl), (Encode-Html $group.name), (Encode-Html (Get-TypeLabel $group.type)), $group.locationCount, $group.highwayCount))
    $modified = Get-IsoDate $group.modifiedUtc
    if (-not $modified) { $modified = Get-IsoDate $group.dateAddedUtc }
    $record = [ordered]@{
        schemaVersion = 2
        id = [int]$group.id
        name = [string]$group.name
        aliases = @(Get-ReviewedGroupAliases ([string]$group.name) @($group.aliases))
        type = Get-TypeLabel $group.type
        server = '2b2t'
        description = Normalize-Text $group.description
        founded = [string]$group.founded
        status = [string]$group.status
        logoUrl = [string]$group.logoUrl
        sources = @(@($group.wikiUrl, $group.websiteUrl, $group.discordUrl, $group.logoSourceUrl) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
            Select-Object -Unique)
        locations = @($group.locations | ForEach-Object { [ordered]@{
            id = [int]$_.locationId
            name = [string]$_.name
            role = [string]$_.role
            dimension = Get-DimensionLabel $_.dimension
            x = [int]$_.x
            z = [int]$_.z
            publicRenderCount = [int]$_.renderCount
            url = "$SiteBaseUrl/entities/locations/$($_.locationId)/"
            interactiveUrl = "$SiteBaseUrl/location/$($_.locationId)"
            apiUrl = "$ApiBaseUrl/api/locations/$($_.locationId)"
        } })
        highways = @($group.highways | ForEach-Object { [ordered]@{
            id = [int]$_.highwayId
            name = [string]$_.name
            role = [string]$_.role
            dimension = Get-DimensionLabel $_.dimension
            apiUrl = "$ApiBaseUrl/api/highways/$($_.highwayId)"
            mapUrl = "$SiteBaseUrl/map?dimension=$((Get-DimensionLabel $_.dimension).ToLowerInvariant())"
        } })
        relationshipCounts = [ordered]@{
            locations = @($group.locations).Count
            highways = @($group.highways).Count
        }
        url = $canonicalUrl
        interactiveUrl = "$SiteBaseUrl/group/$($group.id)"
        apiUrl = "$ApiBaseUrl/api/groups/$($group.id)"
        dateAdded = Get-IsoDate $group.dateAddedUtc
        dateModified = $modified
    }
    $jsonLines.Add(($record | ConvertTo-Json -Depth 7 -Compress))
    $lastMod = if ($modified) { "<lastmod>$modified</lastmod>" } else { '' }
    $sitemapItems.Add("<url><loc>$(Encode-Html $canonicalUrl)</loc>$lastMod</url>")
}

$catalogModified = @($groups | ForEach-Object { Get-IsoDate $_.modifiedUtc } | Where-Object { $_ } | Sort-Object -Descending | Select-Object -First 1)
if ($catalogModified.Count -eq 0) { $catalogModified = @(([DateTime]::UtcNow.ToString('yyyy-MM-dd'))) }
$catalogModified = [string]$catalogModified[0]
$directoryUrl = "$SiteBaseUrl/entities/groups/"
$directoryJsonLd = [ordered]@{
    '@context' = 'https://schema.org'
    '@graph' = @(
        [ordered]@{
            '@type' = 'CollectionPage'
            '@id' = "$directoryUrl#webpage"
            url = $directoryUrl
            name = '2b2t Group Directory'
            description = "Sourced histories and reviewed build and highway attributions for $($groups.Count) documented 2b2t groups."
            dateModified = $catalogModified
            mainEntity = [ordered]@{ '@id' = "$directoryUrl#dataset" }
            breadcrumb = [ordered]@{ '@id' = "$directoryUrl#breadcrumb" }
            isPartOf = [ordered]@{ '@id' = "$SiteBaseUrl/#website" }
        },
        [ordered]@{
            '@type' = 'Dataset'
            '@id' = "$directoryUrl#dataset"
            name = '2b2t Atlas group entities'
            description = 'Structured public records for 2b2t groups and their reviewed Atlas location and highway attributions.'
            url = $directoryUrl
            dateModified = $catalogModified
            isAccessibleForFree = $true
            creator = [ordered]@{ '@type' = 'Organization'; '@id' = "$SiteBaseUrl/#organization"; name = '2b2t Atlas'; url = "$SiteBaseUrl/" }
            includedInDataCatalog = [ordered]@{ '@type' = 'DataCatalog'; name = '2b2t Atlas'; url = $directoryUrl }
            distribution = @(
                [ordered]@{ '@type' = 'DataDownload'; encodingFormat = 'application/x-ndjson'; contentUrl = "$SiteBaseUrl/entities/groups.jsonl" },
                [ordered]@{ '@type' = 'DataDownload'; encodingFormat = 'application/json'; contentUrl = "$ApiBaseUrl/api/groups" }
            )
            variableMeasured = @('Group name', 'Classification', 'Founding era', 'Status', 'Verified source links', 'Attributed Atlas locations', 'Attributed highways')
        },
        [ordered]@{
            '@type' = 'BreadcrumbList'
            '@id' = "$directoryUrl#breadcrumb"
            itemListElement = @(
                [ordered]@{ '@type' = 'ListItem'; position = 1; name = '2b2t Atlas'; item = "$SiteBaseUrl/" },
                [ordered]@{ '@type' = 'ListItem'; position = 2; name = 'Group entities'; item = $directoryUrl }
            )
        }
    )
}
$directoryJsonLdText = ($directoryJsonLd | ConvertTo-Json -Depth 10 -Compress).Replace('&', '\u0026').Replace('<', '\u003C').Replace('>', '\u003E')
$directory = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>2b2t Group Directory | 2b2t Atlas</title><meta name="description" content="Documented 2b2t building groups, infrastructure crews, factions, and their attributed bases.">
<meta name="robots" content="index,follow,max-snippet:-1"><link rel="canonical" href="$directoryUrl">
<link rel="alternate" type="application/x-ndjson" href="$SiteBaseUrl/entities/groups.jsonl" title="Complete group entity catalog">
<meta property="og:type" content="website"><meta property="og:site_name" content="2b2t Atlas"><meta property="og:title" content="2b2t Group Directory">
<meta property="og:description" content="$($groups.Count) documented 2b2t groups with sourced histories and reviewed attributions."><meta property="og:url" content="$directoryUrl">
<meta property="og:image" content="$SiteBaseUrl/Images/banner_web.png"><meta name="twitter:card" content="summary_large_image"><meta name="twitter:image" content="$SiteBaseUrl/Images/banner_web.png">
<script type="application/ld+json">$directoryJsonLdText</script>
<style>body{max-width:980px;margin:auto;padding:24px;background:#111518;color:#e8edf0;font:16px/1.5 Arial,sans-serif}a{color:#83bdff}li{margin:8px 0}span{color:#91a0a8}</style></head>
<body><main><p><a href="$SiteBaseUrl/">2b2t Atlas</a></p><h1>2b2t group entities</h1><p>$($groups.Count) documented groups with sourced histories and reviewed Atlas attributions.</p><p><a href="$SiteBaseUrl/entities/groups.jsonl">Download the JSONL catalog</a> or <a href="$ApiBaseUrl/api/groups">query the public groups API</a>.</p><ol>$($indexItems -join "`n")</ol></main></body></html>
"@
Write-Utf8File (Join-Path $entityRoot 'index.html') $directory
Write-Utf8File (Join-Path $OutputDirectory 'entities\groups.jsonl') (($jsonLines -join "`n") + "`n")

$sitemapPath = Join-Path $OutputDirectory 'sitemap.xml'
$sitemap = [IO.File]::ReadAllText($sitemapPath)
if ([regex]::Matches($sitemap, '</urlset>').Count -ne 1) { throw 'Could not identify the sitemap urlset terminator.' }
$directoryEntry = "<url><loc>$(Encode-Html $directoryUrl)</loc><lastmod>$catalogModified</lastmod></url>"
$sitemap = $sitemap.Replace('</urlset>', $directoryEntry + "`n" + ($sitemapItems -join "`n") + "`n</urlset>")
Write-Utf8File $sitemapPath $sitemap

foreach ($datasetPath in @((Join-Path $OutputDirectory 'dataset.json'), (Join-Path $OutputDirectory 'entities\dataset.json'))) {
    $dataset = [IO.File]::ReadAllText($datasetPath) | ConvertFrom-Json
    $dataset.schemaVersion = 5
    $dataset.dataset = '2b2t Atlas public entity catalogs'
    $dataset.canonicalUrl = "$SiteBaseUrl/dataset.json"
    $dataset | Add-Member -NotePropertyName groupCount -NotePropertyValue $groups.Count -Force
    $dataset | Add-Member -NotePropertyName groupLocationRelationshipCount -NotePropertyValue ([int](@($groups | ForEach-Object { [int]$_.locationCount } | Measure-Object -Sum).Sum)) -Force
    $dataset | Add-Member -NotePropertyName groupHighwayRelationshipCount -NotePropertyValue ([int](@($groups | ForEach-Object { [int]$_.highwayCount } | Measure-Object -Sum).Sum)) -Force
    $dataset | Add-Member -NotePropertyName locationCatalogUrl -NotePropertyValue "$SiteBaseUrl/entities/locations.jsonl" -Force
    $dataset | Add-Member -NotePropertyName groupCatalogUrl -NotePropertyValue "$SiteBaseUrl/entities/groups.jsonl" -Force
    $dataset | Add-Member -NotePropertyName mediaCatalogUrl -NotePropertyValue "$SiteBaseUrl/entities/media.jsonl" -Force
    $dataset | Add-Member -NotePropertyName publicApiIndexUrl -NotePropertyValue "$ApiBaseUrl/api" -Force
    Write-Utf8File $datasetPath (($dataset | ConvertTo-Json -Depth 6) + "`n")
}

$llmsPath = Join-Path $OutputDirectory 'llms.txt'
$llms = [IO.File]::ReadAllText($llmsPath)
$groupSection = @"
## Group entities

- [Human-readable group directory]($SiteBaseUrl/entities/groups/): Sourced histories and reviewed build/highway attributions.
- [Complete group JSONL catalog]($SiteBaseUrl/entities/groups.jsonl): One normalized group record per line.
- [Public groups API]($ApiBaseUrl/api/groups): Live group metadata and attribution counts.

"@
if ($llms -notmatch '(?m)^## Interactive Atlas\r?$') { throw 'Could not identify the llms.txt insertion point.' }
Write-Utf8File $llmsPath ($llms.Replace('## Interactive Atlas', $groupSection + '## Interactive Atlas'))

Write-Host "Generated $($groups.Count) static group entities in $entityRoot"
