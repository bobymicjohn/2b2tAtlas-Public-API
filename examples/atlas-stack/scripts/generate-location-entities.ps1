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
    [string]$OutputDirectory,

    [string]$GroupEvidenceIndexPath = ''
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
$script:groupEvidenceByLocationId = @{}
$script:groupEvidenceIndexSha256 = $null
$script:groupEvidenceGeneratedUtc = $null

function Get-GroupEvidence {
    param([int]$LocationId)

    if ($script:groupEvidenceByLocationId.ContainsKey($LocationId)) {
        return $script:groupEvidenceByLocationId[$LocationId].ToArray()
    }
    return @()
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
    # Normalize escaped CR/LF text before collapsing semantic descriptions.
    $Value = [Text.RegularExpressions.Regex]::Replace($Value, '(?i)\\[rn]', ' ')
    # Legacy wiki imports persisted paragraph separators as the literal text
    # "rnrn" (and sometimes repeated it more than once). Normalize the whole
    # run so a value such as "rnrnrn" cannot leave a trailing "rn" behind.
    $Value = [Text.RegularExpressions.Regex]::Replace($Value, '(?i)(?:rn){2,}', ' ')
    return ([Text.RegularExpressions.Regex]::Replace($Value, '\s+', ' ')).Trim()
}

function Get-LocationTags {
    param($Location)

    return @([string]$Location.tags -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-MetaDescription {
    param($Location)

    $description = Normalize-Text $Location.description
    $context = '{0} is a documented 2b2t {1} location at X {2}, Z {3}.' -f `
        $Location.name, $Location.dimensionName, $Location.x, $Location.z
    if ([string]::IsNullOrWhiteSpace($description)) {
        $description = $context
    } else {
        $description = "$context $description"
    }
    if ($description.Length -gt 160) {
        return $description.Substring(0, 157).TrimEnd() + '...'
    }
    return $description
}

function Get-ImageUrl {
    param($Location)

    foreach ($attachment in @($Location.attachments)) {
        $path = [string]$attachment.path
        if ($path -match '(?i)^https://.*\.(avif|gif|jpe?g|png|webp)(\?.*)?$') {
            return $path
        }
    }
    foreach ($render in @($Location.renders)) {
        $path = [string]$render.previewImagePath
        if ($path -match '^https://') { return $path }
    }
    return $null
}

function Get-IsoDate {
    param([AllowNull()]$Value)

    if ($null -eq $Value) { return $null }
    try { return ([DateTime]$Value).ToUniversalTime().ToString('yyyy-MM-dd') } catch { return $null }
}

function Get-MediaEncodingFormat {
    param($Attachment)

    $path = [string]$Attachment.path
    switch -Regex ($path) {
        '(?i)\.avif(?:\?.*)?$' { return 'image/avif' }
        '(?i)\.gif(?:\?.*)?$' { return 'image/gif' }
        '(?i)\.jpe?g(?:\?.*)?$' { return 'image/jpeg' }
        '(?i)\.png(?:\?.*)?$' { return 'image/png' }
        '(?i)\.webp(?:\?.*)?$' { return 'image/webp' }
        '(?i)\.mp4(?:\?.*)?$' { return 'video/mp4' }
        '(?i)\.webm(?:\?.*)?$' { return 'video/webm' }
    }
    switch -Regex ([string]$Attachment.mediaType) {
        '(?i)^image$' { return 'image/*' }
        '(?i)^video$' { return 'video/*' }
        default { return 'text/html' }
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

function New-LocationJsonLd {
    param($Location, [string]$CanonicalUrl, [string]$InteractiveUrl, [string]$ApiUrl)

    $properties = @(
        [ordered]@{ '@type' = 'PropertyValue'; name = 'Dimension'; value = [string]$Location.dimensionName },
        [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft X coordinate'; value = [int]$Location.x },
        [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft Z coordinate'; value = [int]$Location.z }
    )
    if ($null -ne $Location.y) {
        $properties += [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft Y coordinate'; value = [int]$Location.y }
    }

    $groupRelations = @($Location.groups | ForEach-Object {
        $groupUrl = "$SiteBaseUrl/entities/groups/$($_.groupId)/"
        [ordered]@{
            '@type' = 'Organization'
            '@id' = "$groupUrl#organization"
            name = [string]$_.groupName
            url = $groupUrl
            additionalProperty = [ordered]@{ '@type' = 'PropertyValue'; name = 'Attribution role'; value = [string]$_.role }
        }
    })
    $warpIdentifiers = @($Location.warps | ForEach-Object {
        [ordered]@{
            '@type' = 'PropertyValue'
            '@id' = "$ApiBaseUrl/api/warps/$($_.id)#identifier"
            propertyID = 'The Archive warp'
            name = 'Archive warp'
            value = "/warp $($_.name)"
            url = "$ApiBaseUrl/api/warps/$($_.id)"
            additionalProperty = [ordered]@{ '@type' = 'PropertyValue'; name = 'Single-player concept'; value = [bool]$_.isSinglePlayerConcept }
        }
    })
    $worldDownloads = @($Location.warps | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    } | ForEach-Object {
        [ordered]@{
            '@type' = 'DataDownload'
            '@id' = "$($_.worldDownloadUrl)#download"
            name = "Bounded Minecraft world download for /warp $($_.name)"
            contentUrl = [string]$_.worldDownloadUrl
            url = [string]$_.worldDownloadMetadataUrl
            encodingFormat = 'application/zip'
            description = if ([bool]$_.isSinglePlayerConcept) { 'Preserved single-player concept build; not a live 2b2t snapshot. This is a playable partial Minecraft Java world.' } else { 'Playable partial Minecraft Java world snapshot; chunks outside the retained footprint are absent.' }
            identifier = "sha256:$($_.archiveSha256)"
            about = [ordered]@{ '@id' = "$CanonicalUrl#entity" }
            additionalProperty = @(
                [ordered]@{ '@type' = 'PropertyValue'; name = 'Capture scope'; value = [string]$_.worldDownloadScope },
                [ordered]@{ '@type' = 'PropertyValue'; name = 'Single-player concept'; value = [bool]$_.isSinglePlayerConcept }
            )
        }
    })
    $worldDownloads += @($Location.renders | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    } | ForEach-Object {
        [ordered]@{
            '@type' = 'DataDownload'
            '@id' = "$($_.worldDownloadUrl)#download"
            name = "Preserved Minecraft source world for render $($_.id)"
            contentUrl = [string]$_.worldDownloadUrl
            url = [string]$_.worldDownloadMetadataUrl
            encodingFormat = 'application/zip'
            description = 'Playable preserved source WDL used for this Atlas render; coverage follows the historical source save and is not a complete 2b2t world.'
            identifier = "sha256:$($_.worldDownloadSha256)"
            about = [ordered]@{ '@id' = "$CanonicalUrl#entity" }
            additionalProperty = [ordered]@{ '@type' = 'PropertyValue'; name = 'Capture scope'; value = [string]$_.worldDownloadScope }
        }
    })
    $renderWorks = @($Location.renders | ForEach-Object {
        $render = [ordered]@{
            '@type' = 'CreativeWork'
            '@id' = "$ApiBaseUrl/api/renders/$($_.id)#render"
            name = if ([string]::IsNullOrWhiteSpace([string]$_.name)) { "2b2t Atlas render $($_.id)" } else { [string]$_.name }
            url = "$ApiBaseUrl/api/renders/$($_.id)"
            encodingFormat = 'application/json'
            about = [ordered]@{ '@id' = "$CanonicalUrl#entity" }
            additionalProperty = @(
                [ordered]@{ '@type' = 'PropertyValue'; name = 'Minecraft dimension'; value = Get-DimensionLabel $_.dimension },
                [ordered]@{ '@type' = 'PropertyValue'; name = 'Render source'; value = [string]$_.source },
                [ordered]@{ '@type' = 'PropertyValue'; name = 'Single-player concept'; value = [bool]$_.isSinglePlayerConcept }
            )
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadDate)) { $render.dateCreated = [string]$_.worldDownloadDate }
        if (-not [string]::IsNullOrWhiteSpace([string]$_.previewImagePath)) { $render.thumbnailUrl = [string]$_.previewImagePath }
        $render
    })
    $mediaObjects = @($Location.attachments | ForEach-Object {
        $mediaType = switch -Regex ([string]$_.mediaType) {
            '(?i)^image$' { 'ImageObject'; break }
            '(?i)^video$' { 'VideoObject'; break }
            default { 'MediaObject' }
        }
        $media = [ordered]@{
            '@type' = $mediaType
            '@id' = "$ApiBaseUrl/api/attachments/$($_.id)#media"
            name = [string]$_.fileName
            url = "$ApiBaseUrl/api/attachments/$($_.id)"
            contentUrl = [string]$_.path
            encodingFormat = Get-MediaEncodingFormat $_
            about = [ordered]@{ '@id' = "$CanonicalUrl#entity" }
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$_.thumbnailPath)) { $media.thumbnailUrl = [string]$_.thumbnailPath }
        if (-not [string]::IsNullOrWhiteSpace([string]$_.caption)) { $media.caption = [string]$_.caption }
        if (-not [string]::IsNullOrWhiteSpace([string]$_.attribution)) { $media.creditText = [string]$_.attribution }
        if (-not [string]::IsNullOrWhiteSpace([string]$_.sourceUrl)) {
            $media.sameAs = [string]$_.sourceUrl
            $media.isBasedOn = [string]$_.sourceUrl
        }
        $media
    })
    $groupEvidence = @(Get-GroupEvidence ([int]$Location.rowid))

    $entity = [ordered]@{
        '@type' = 'Thing'
        '@id' = "$CanonicalUrl#entity"
        name = [string]$Location.name
        description = Get-MetaDescription $Location
        url = $CanonicalUrl
        identifier = "2b2tAtlas-location-$($Location.rowid)"
        mainEntityOfPage = [ordered]@{ '@id' = "$CanonicalUrl#webpage" }
        additionalProperty = $properties
        potentialAction = [ordered]@{ '@type' = 'ViewAction'; target = $InteractiveUrl }
        subjectOf = @([ordered]@{ '@type' = 'Dataset'; name = '2b2t Atlas location API record'; url = $ApiUrl; encodingFormat = 'application/json' }) + $renderWorks + $worldDownloads
    }
    if ($groupRelations.Count -gt 0) { $entity.contributor = $groupRelations }
    if ($warpIdentifiers.Count -gt 0) { $entity.identifier = @($entity.identifier) + $warpIdentifiers }
    if ($mediaObjects.Count -gt 0) { $entity.associatedMedia = $mediaObjects }
    if ($groupEvidence.Count -gt 0) {
        $entity.citation = @($groupEvidence | ForEach-Object {
            [ordered]@{
                '@type' = 'WebPage'
                name = "Revision-pinned $($_.groupName) build evidence"
                url = [string]$_.revisionUrl
                isBasedOn = [string]$_.groupUrl
                identifier = "MediaWiki revision $($_.revisionId)"
            }
        })
    }
    $created = Get-IsoDate $Location.dateAddedUtc
    $modified = Get-IsoDate $Location.modifiedUtc
    if (-not $modified) { $modified = $created }
    $tags = @([string]$Location.tags -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if (-not [string]::IsNullOrWhiteSpace([string]$Location.wiki)) { $entity.sameAs = @([string]$Location.wiki) }
    $imageUrl = Get-ImageUrl $Location
    $entity.image = if ($imageUrl) { $imageUrl } else { "$SiteBaseUrl/Images/banner_web.png" }

    $webPage = [ordered]@{
        '@type' = 'WebPage'
        '@id' = "$CanonicalUrl#webpage"
        url = $CanonicalUrl
        name = [string]$Location.name
        description = Get-MetaDescription $Location
        mainEntity = [ordered]@{ '@id' = "$CanonicalUrl#entity" }
        breadcrumb = [ordered]@{ '@id' = "$CanonicalUrl#breadcrumb" }
        isPartOf = [ordered]@{ '@type' = 'WebSite'; '@id' = "$SiteBaseUrl/#website"; name = '2b2t Atlas'; url = "$SiteBaseUrl/" }
    }
    if ($created) { $webPage.datePublished = $created }
    if ($modified) {
        $webPage.dateModified = $modified
        $entity.dateModified = $modified
    }
    if ($tags.Count -gt 0) { $webPage.keywords = $tags }

    $graph = [ordered]@{
        '@context' = 'https://schema.org'
        '@graph' = @(
            $webPage,
            $entity,
            [ordered]@{
                '@type' = 'BreadcrumbList'
                '@id' = "$CanonicalUrl#breadcrumb"
                itemListElement = @(
                    [ordered]@{ '@type' = 'ListItem'; position = 1; name = '2b2t Atlas'; item = "$SiteBaseUrl/" },
                    [ordered]@{ '@type' = 'ListItem'; position = 2; name = 'Location entities'; item = "$SiteBaseUrl/entities/locations/" },
                    [ordered]@{ '@type' = 'ListItem'; position = 3; name = [string]$Location.name; item = $CanonicalUrl }
                )
            }
        )
    }
    $json = $graph | ConvertTo-Json -Depth 10 -Compress
    return $json.Replace('&', '\u0026').Replace('<', '\u003C').Replace('>', '\u003E')
}

function New-LocationHtml {
    param($Location, [array]$RelatedLocations)

    $entityPath = "/entities/locations/$($Location.rowid)/"
    $canonicalUrl = "$SiteBaseUrl$entityPath"
    $interactiveUrl = "$SiteBaseUrl/location/$($Location.rowid)"
    $apiUrl = "$ApiBaseUrl/api/locations/$($Location.rowid)"
    $titleKey = ('{0}|{1}' -f $Location.name, $Location.dimensionName).ToLowerInvariant()
    $title = if ($duplicateTitleKeys.Contains($titleKey)) {
        '{0} #{1} - {2} 2b2t Location | 2b2t Atlas' -f $Location.name, $Location.rowid, $Location.dimensionName
    } else {
        '{0} - {1} 2b2t Location | 2b2t Atlas' -f $Location.name, $Location.dimensionName
    }
    $description = Get-MetaDescription $Location
    $jsonLd = New-LocationJsonLd $Location $canonicalUrl $interactiveUrl $apiUrl
    $imageUrl = Get-ImageUrl $Location
    if (-not $imageUrl) { $imageUrl = "$SiteBaseUrl/Images/banner_web.png" }
    $imageMeta = '<meta property="og:image" content="{0}"><meta name="twitter:image" content="{0}">' -f (Encode-Html $imageUrl)
    $yCoordinate = if ($null -eq $Location.y) { 'Unknown' } else { [string]$Location.y }
    $fullDescription = Normalize-Text $Location.description
    if ([string]::IsNullOrWhiteSpace($fullDescription)) {
        $fullDescription = $description
    }

    $tagItems = @(Get-LocationTags $Location | ForEach-Object { '<li>{0}</li>' -f (Encode-Html $_) })
    $tagsSection = if ($tagItems.Count -gt 0) { '<section><h2>Tags</h2><ul class="tags">{0}</ul></section>' -f ($tagItems -join '') } else { '' }
    $groupItems = @($Location.groups | ForEach-Object {
        '<li><a href="{0}/entities/groups/{1}/">{2}</a> <span>{3}</span></li>' -f $SiteBaseUrl, $_.groupId, (Encode-Html $_.groupName), (Encode-Html $_.role)
    })
    $groupsSection = if ($groupItems.Count -gt 0) { '<section id="groups"><h2>Attributed groups</h2><ul>{0}</ul></section>' -f ($groupItems -join '') } else { '' }
    $warpItems = @($Location.warps | ForEach-Object {
        $warpDate = if ([string]::IsNullOrWhiteSpace([string]$_.worldDownloadDate)) { '' } else { ' <span>{0}</span>' -f (Encode-Html $_.worldDownloadDate) }
        $concept = if ([bool]$_.isSinglePlayerConcept) { ' <strong>[single-player concept; not live 2b2t]</strong>' } else { '' }
        $download = if ([string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)) { '' } else {
            ' <a href="{0}" download>[WDL ZIP]</a> <a href="{1}">[metadata]</a>' -f (Encode-Html $_.worldDownloadUrl), (Encode-Html $_.worldDownloadMetadataUrl)
        }
        '<li id="warp-{0}"><a href="{1}/api/warps/{0}"><code>/warp {2}</code></a>{3}{4}{5}</li>' -f $_.id, $ApiBaseUrl, (Encode-Html $_.name), $concept, $warpDate, $download
    })
    $warpsSection = if ($warpItems.Count -gt 0) { '<section id="warps"><h2>Archived warps</h2><ul>{0}</ul></section>' -f ($warpItems -join '') } else { '' }
    $renderItems = @($Location.renders | ForEach-Object {
        $renderLabel = if ([string]::IsNullOrWhiteSpace([string]$_.name)) { "Render $($_.id)" } else { [string]$_.name }
        $concept = if ([bool]$_.isSinglePlayerConcept) { ' <strong>[single-player concept; not live 2b2t]</strong>' } else { '' }
        $renderMeta = @((Get-DimensionLabel $_.dimension), [string]$_.worldDownloadDate, [string]$_.source) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $renderMeta = $renderMeta -join ' | '
        $preview = if ([string]$_.previewImagePath -match '^https://') { ' <a href="{0}">preview</a>' -f (Encode-Html $_.previewImagePath) } else { '' }
        $download = if ([string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)) { '' } else {
            ' <a href="{0}" download>[source WDL ZIP]</a> <a href="{1}">[WDL metadata]</a>' -f (Encode-Html $_.worldDownloadUrl), (Encode-Html $_.worldDownloadMetadataUrl)
        }
        '<li id="render-{0}"><a href="{1}/api/renders/{0}">{2}</a>{3} <span>{4}</span>{5}{6}</li>' -f $_.id, $ApiBaseUrl, (Encode-Html $renderLabel), $concept, (Encode-Html $renderMeta), $preview, $download
    })
    $rendersSection = if ($renderItems.Count -gt 0) { '<section id="renders"><h2>World-download renders</h2><ul>{0}</ul></section>' -f ($renderItems -join '') } else { '' }
    $sourceLinks = @()
    if (-not [string]::IsNullOrWhiteSpace([string]$Location.wiki)) {
        $sourceLinks += '<li><a href="{0}" rel="external">2b2t Wiki reference</a></li>' -f (Encode-Html $Location.wiki)
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Location.videoUrl)) {
        $sourceLinks += '<li><a href="{0}" rel="external">Location video</a></li>' -f (Encode-Html $Location.videoUrl)
    }
    foreach ($evidence in @(Get-GroupEvidence ([int]$Location.rowid))) {
        $label = '{0} build attribution ({1})' -f $evidence.groupName, (@($evidence.evidence) -join ', ')
        $sourceLinks += '<li><a href="{0}" rel="external">{1}</a></li>' -f `
            (Encode-Html $evidence.revisionUrl), (Encode-Html $label)
    }
    $mediaItems = @()
    foreach ($attachment in @($Location.attachments)) {
        $mediaUrl = [string]$attachment.path
        if ([string]::IsNullOrWhiteSpace($mediaUrl)) { continue }
        $mediaLabel = if ([string]::IsNullOrWhiteSpace([string]$attachment.fileName)) { 'Historical media' } else { [string]$attachment.fileName }
        $mediaDetail = @([string]$attachment.caption, [string]$attachment.attribution | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ' | '
        $detailHtml = if ($mediaDetail) { ' &mdash; {0}' -f (Encode-Html $mediaDetail) } else { '' }
        $sourceHtml = if (-not [string]::IsNullOrWhiteSpace([string]$attachment.sourceUrl)) {
            ' <a href="{0}" rel="external nofollow">[source]</a>' -f (Encode-Html $attachment.sourceUrl)
        } else { '' }
        if ([string]$attachment.mediaType -match '(?i)^image$') {
            $thumbnail = if ([string]$attachment.thumbnailPath -match '^https://') { [string]$attachment.thumbnailPath } else { $mediaUrl }
            $mediaItems += '<li id="attachment-{0}"><figure><a href="{1}"><img src="{2}" alt="{3}" loading="lazy" decoding="async"></a><figcaption><strong>{4}</strong>{5}{6} <a href="{7}/api/attachments/{0}">[metadata]</a></figcaption></figure></li>' -f $attachment.id, (Encode-Html $mediaUrl), (Encode-Html $thumbnail), (Encode-Html "$mediaLabel - $($Location.name)"), (Encode-Html $mediaLabel), $detailHtml, $sourceHtml, $ApiBaseUrl
        } else {
            $mediaItems += '<li id="attachment-{0}"><a href="{1}">{2}</a>{3}{4} <a href="{5}/api/attachments/{0}">[metadata]</a></li>' -f $attachment.id, (Encode-Html $mediaUrl), (Encode-Html $mediaLabel), $detailHtml, $sourceHtml, $ApiBaseUrl
        }
    }
    $mediaSection = if ($mediaItems.Count -gt 0) { '<section><h2>Historical media</h2><ul>{0}</ul></section>' -f ($mediaItems -join '') } else { '' }
    $sourcesSection = if ($sourceLinks.Count -gt 0) { '<section><h2>References</h2><ul>{0}</ul></section>' -f ($sourceLinks -join '') } else { '' }
    $relatedItems = @($RelatedLocations | ForEach-Object {
        '<li><a href="{0}/entities/locations/{1}/">{2}</a> <span>{3} - X {4}, Z {5}</span></li>' -f `
            $SiteBaseUrl, $_.rowid, (Encode-Html $_.name), (Encode-Html $_.dimensionName), $_.x, $_.z
    })
    $relatedSection = if ($relatedItems.Count -gt 0) { '<section><h2>Explore more 2b2t locations</h2><ul>{0}</ul></section>' -f ($relatedItems -join '') } else { '' }
    $dateAdded = Get-IsoDate $Location.dateAddedUtc

    return @"
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>$(Encode-Html $title)</title>
<link rel="icon" type="image/png" href="$SiteBaseUrl/favicon.png">
<meta name="description" content="$(Encode-Html $description)">
<meta name="robots" content="index,follow,max-image-preview:large,max-snippet:-1,max-video-preview:-1">
<link rel="canonical" href="$(Encode-Html $canonicalUrl)">
<link rel="alternate" type="application/json" href="$(Encode-Html $apiUrl)" title="Location API record">
<meta property="og:type" content="website">
<meta property="og:site_name" content="2b2t Atlas">
<meta property="og:title" content="$(Encode-Html $title)">
<meta property="og:description" content="$(Encode-Html $description)">
<meta property="og:url" content="$(Encode-Html $canonicalUrl)">
$imageMeta
<meta name="twitter:card" content="summary_large_image">
<script type="application/ld+json">$jsonLd</script>
<style>body{margin:0;background:#111518;color:#e8edf0;font:16px/1.6 Georgia,serif}header,main,footer{max-width:860px;margin:auto;padding:24px}header{border-bottom:1px solid #354047}a{color:#83bdff}h1,h2{font-family:Arial,sans-serif;line-height:1.2}h1{font-size:clamp(2rem,6vw,4rem);margin:.3em 0}.eyebrow{color:#8ca0ab;font:700 .8rem Arial,sans-serif;text-transform:uppercase}.coords{display:grid;grid-template-columns:repeat(3,1fr);gap:1px;background:#354047;margin:24px 0}.coords div{background:#1a2024;padding:16px}.coords dt{color:#8ca0ab;font:700 .75rem Arial,sans-serif}.coords dd{font:700 1.25rem Consolas,monospace;margin:4px 0}.actions{display:flex;gap:12px;flex-wrap:wrap}.actions a{background:#e8edf0;color:#111518;padding:9px 14px;text-decoration:none;font:700 .9rem Arial,sans-serif}.tags{display:flex;flex-wrap:wrap;gap:8px;list-style:none;padding:0}.tags li{border:1px solid #52616a;padding:3px 9px}section>ul{padding-left:24px}section>ul figure{margin:12px 0}section>ul img{display:block;max-width:100%;height:auto;max-height:480px;object-fit:contain;background:#0b0e10}section>ul figcaption{margin-top:6px}footer{color:#8ca0ab;border-top:1px solid #354047;font-size:.85rem}@media(max-width:520px){.coords{grid-template-columns:1fr}}</style>
</head>
<body>
<header><a href="$SiteBaseUrl/">2b2t Atlas</a> / <a href="$SiteBaseUrl/entities/locations/">Location entities</a></header>
<main>
<article>
<p class="eyebrow">$(Encode-Html $Location.dimensionName) location - Entity $($Location.rowid)</p>
<h1>$(Encode-Html $Location.name)</h1>
<p>$(Encode-Html $fullDescription)</p>
<dl class="coords"><div><dt>X</dt><dd>$(Encode-Html $Location.x)</dd></div><div><dt>Y</dt><dd>$(Encode-Html $yCoordinate)</dd></div><div><dt>Z</dt><dd>$(Encode-Html $Location.z)</dd></div></dl>
<p class="actions"><a href="$(Encode-Html $interactiveUrl)">Explore on the interactive Atlas</a><a href="$(Encode-Html $apiUrl)">View JSON record</a></p>
$tagsSection
$groupsSection
$warpsSection
$rendersSection
$mediaSection
$sourcesSection
$relatedSection
<section><h2>Record details</h2><p>Dimension: $(Encode-Html $Location.dimensionName). Added to 2b2t Atlas: $(Encode-Html $dateAdded). Related archive material: $(Encode-Html $Location.relatedContentSummary).</p></section>
</article>
</main>
<footer>Canonical public entity record from 2b2t Atlas. Minecraft coordinates are game-world coordinates, not geographic latitude or longitude.</footer>
</body>
</html>
"@
}

if (-not (Test-Path $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$locationsResponse = Invoke-RestMethod "$DataApiBaseUrl/api/locations" -TimeoutSec 120
$locations = @($locationsResponse | ForEach-Object { $_ })
if ($locations.Count -eq 0) { throw 'The locations API returned no records.' }
$duplicateIds = @($locations | Group-Object rowid | Where-Object Count -ne 1)
if ($duplicateIds.Count -gt 0) { throw 'The locations API returned duplicate row IDs.' }
$invalidUuids = @($locations | Where-Object { [string]$_.locationUuid -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' })
if ($invalidUuids.Count -gt 0) { throw 'The locations API returned a missing or invalid location UUID.' }
$duplicateUuids = @($locations | Group-Object { ([string]$_.locationUuid).ToLowerInvariant() } | Where-Object Count -ne 1)
if ($duplicateUuids.Count -gt 0) { throw 'The locations API returned duplicate location UUIDs.' }

$duplicateTitleKeys = New-Object 'Collections.Generic.HashSet[string]'
foreach ($nameGroup in ($locations | Group-Object { ('{0}|{1}' -f $_.name, $_.dimensionName).ToLowerInvariant() } | Where-Object Count -gt 1)) {
    [void]$duplicateTitleKeys.Add([string]$nameGroup.Name)
}

$locationsById = @{}
$relatedById = @{}
foreach ($location in $locations) {
    $locationId = [int]$location.rowid
    $locationsById[$locationId] = $location
    $relatedById[$locationId] = New-Object 'Collections.Generic.HashSet[int]'
}

function Add-RelatedPair {
    param([int]$FirstId, [int]$SecondId)

    if ($FirstId -eq $SecondId) { return }
    [void]$relatedById[$FirstId].Add($SecondId)
    [void]$relatedById[$SecondId].Add($FirstId)
}

# Adjacent entries in each dimension create deterministic, bidirectional crawl paths.
foreach ($dimensionGroup in ($locations | Group-Object dimension)) {
    $ordered = @($dimensionGroup.Group | Sort-Object x, z, rowid)
    for ($index = 0; $index -lt $ordered.Count; $index++) {
        for ($offset = 1; $offset -le 2; $offset++) {
            if ($index + $offset -lt $ordered.Count) {
                Add-RelatedPair ([int]$ordered[$index].rowid) ([int]$ordered[$index + $offset].rowid)
            }
        }
    }
}

# Shared curated tags add topical links without inferring facts from prose.
$tagGroups = @{}
foreach ($location in $locations) {
    foreach ($tag in (Get-LocationTags $location)) {
        $tagKey = $tag.ToLowerInvariant()
        if (-not $tagGroups.ContainsKey($tagKey)) { $tagGroups[$tagKey] = New-Object Collections.Generic.List[int] }
        $tagGroups[$tagKey].Add([int]$location.rowid)
    }
}
foreach ($tagIds in $tagGroups.Values) {
    $orderedIds = @($tagIds | Sort-Object -Unique)
    for ($index = 1; $index -lt $orderedIds.Count; $index++) {
        Add-RelatedPair $orderedIds[$index - 1] $orderedIds[$index]
    }
}

$entityRoot = Join-Path $OutputDirectory 'entities\locations'
$indexItems = New-Object Collections.Generic.List[string]
$jsonLines = New-Object Collections.Generic.List[string]
$worldDownloadRecords = New-Object Collections.Generic.List[object]
$worldDownloadJsonLines = New-Object Collections.Generic.List[string]
$worldDownloadIndexItems = New-Object Collections.Generic.List[string]
$sitemapItems = New-Object Collections.Generic.List[string]
$legacyRedirectRules = New-Object Collections.Generic.List[string]

$generatedUtc = [DateTime]::UtcNow.ToString('o')
$catalogLastModified = @($locations | ForEach-Object {
    $value = Get-IsoDate $_.modifiedUtc
    if (-not $value) { $value = Get-IsoDate $_.dateAddedUtc }
    $value
} | Where-Object { $_ } | Sort-Object -Descending | Select-Object -First 1)
if ($catalogLastModified.Count -eq 0) { $catalogLastModified = @(([DateTime]::UtcNow.ToString('yyyy-MM-dd'))) }
$catalogLastModified = [string]$catalogLastModified[0]
foreach ($staticUrl in @(
    "$SiteBaseUrl/",
    "$SiteBaseUrl/about",
    "$SiteBaseUrl/api",
    "$SiteBaseUrl/extras",
    "$SiteBaseUrl/groups",
    "$SiteBaseUrl/map",
    "$SiteBaseUrl/entities/locations/",
    "$SiteBaseUrl/entities/media/",
    "$SiteBaseUrl/entities/world-downloads/",
    "$SiteBaseUrl/mcp/",
    "$SiteBaseUrl/nocom/",
    "$SiteBaseUrl/locations/",
    "$SiteBaseUrl/locations/overworld/",
    "$SiteBaseUrl/locations/nether/",
    "$SiteBaseUrl/locations/end/"
)) {
    $sitemapItems.Add("<url><loc>$(Encode-Html $staticUrl)</loc><lastmod>$catalogLastModified</lastmod></url>")
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
    $script:groupEvidenceIndexSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $script:groupEvidenceGeneratedUtc = [string]$evidenceIndex.generatedUtc

    foreach ($candidate in @($evidenceIndex.exactBuildCandidates)) {
        $locationId = [int]$candidate.Rowid
        if (-not $locationsById.ContainsKey($locationId)) { continue }
        $location = $locationsById[$locationId]
        $reviewedGroupIds = @($location.groups | ForEach-Object { [int]$_.groupId })
        foreach ($groupId in @($candidate.groupIds | ForEach-Object { [int]$_ })) {
            if ($groupId -notin $reviewedGroupIds) { continue }
            $relation = @($location.groups | Where-Object { [int]$_.groupId -eq $groupId } | Select-Object -First 1)
            if ($relation.Count -eq 0) { continue }
            $groupUrl = [string]$candidate.groupUrl
            $revisionId = [int]$candidate.revisionId
            if ([string]::IsNullOrWhiteSpace($groupUrl) -or $revisionId -le 0) { continue }
            $revisionSeparator = if ($groupUrl.Contains('?')) { '&' } else { '?' }
            $record = [ordered]@{
                groupId = $groupId
                groupName = [string]$relation[0].groupName
                role = [string]$relation[0].role
                evidence = @($candidate.evidence | ForEach-Object { [string]$_ })
                revisionId = $revisionId
                groupUrl = $groupUrl
                revisionUrl = "$groupUrl${revisionSeparator}oldid=$revisionId"
            }
            if (-not $script:groupEvidenceByLocationId.ContainsKey($locationId)) {
                $script:groupEvidenceByLocationId[$locationId] = New-Object Collections.Generic.List[object]
            }
            $existing = @($script:groupEvidenceByLocationId[$locationId] | Where-Object {
                [int]$_.groupId -eq $groupId -and [int]$_.revisionId -eq $revisionId
            })
            if ($existing.Count -eq 0) { $script:groupEvidenceByLocationId[$locationId].Add($record) }
        }
    }
}

foreach ($location in ($locations | Sort-Object rowid)) {
    $locationDirectory = Join-Path $entityRoot ([string]$location.rowid)
    $canonicalUrl = "$SiteBaseUrl/entities/locations/$($location.rowid)/"
    $apiUrl = "$ApiBaseUrl/api/locations/$($location.rowid)"
    $relatedLocations = @($relatedById[[int]$location.rowid] | Sort-Object | Select-Object -First 12 | ForEach-Object { $locationsById[[int]$_] })
    Write-Utf8File (Join-Path $locationDirectory 'index.html') (New-LocationHtml $location $relatedLocations)
    $locationLastModified = Get-IsoDate $location.modifiedUtc
    if (-not $locationLastModified) { $locationLastModified = Get-IsoDate $location.dateAddedUtc }
    $indexItem = '<li><a href="{0}">{1}</a> <span>{2} - X {3}, Z {4}</span></li>' -f `
        (Encode-Html $canonicalUrl), (Encode-Html $location.name), (Encode-Html $location.dimensionName), `
        (Encode-Html $location.x), (Encode-Html $location.z)
    $indexItems.Add($indexItem)

    $catalogRecord = [ordered]@{
        id = [int]$location.rowid
        uuid = ([string]$location.locationUuid).ToLowerInvariant()
        name = [string]$location.name
        type = 'location'
        server = '2b2t'
        description = Normalize-Text $location.description
        tags = @(Get-LocationTags $location)
        dimension = [string]$location.dimensionName
        coordinates = [ordered]@{ x = [int]$location.x; y = $location.y; z = [int]$location.z; coordinateSystem = 'Minecraft game-world blocks' }
        archiveWarps = @($location.warps | ForEach-Object { [string]$_.name })
        groups = @($location.groups | ForEach-Object { [ordered]@{
            id = [int]$_.groupId
            name = [string]$_.groupName
            role = [string]$_.role
            url = "$SiteBaseUrl/entities/groups/$($_.groupId)/"
            interactiveUrl = "$SiteBaseUrl/group/$($_.groupId)"
            apiUrl = "$ApiBaseUrl/api/groups/$($_.groupId)"
        } })
        groupEvidence = @(Get-GroupEvidence ([int]$location.rowid))
        warps = @($location.warps | ForEach-Object { [ordered]@{
            id = [int]$_.id
            name = [string]$_.name
            worldDownloadDate = [string]$_.worldDownloadDate
            source = [string]$_.source
            apiUrl = "$ApiBaseUrl/api/warps/$($_.id)"
            worldDownloadUrl = [string]$_.worldDownloadUrl
            worldDownloadMetadataUrl = [string]$_.worldDownloadMetadataUrl
            worldDownloadScope = [string]$_.worldDownloadScope
            archiveSha256 = [string]$_.archiveSha256
            isSinglePlayerConcept = [bool]$_.isSinglePlayerConcept
        } })
        renders = @($location.renders | ForEach-Object { [ordered]@{
            id = [int]$_.id
            name = [string]$_.name
            dimension = Get-DimensionLabel $_.dimension
            worldDownloadDate = [string]$_.worldDownloadDate
            source = [string]$_.source
            apiUrl = "$ApiBaseUrl/api/renders/$($_.id)"
            previewUrl = [string]$_.previewImagePath
            tileUrlTemplate = [string]$_.tilesPath
            archiveWarpId = $_.archiveWarpId
            archiveWarpApiUrl = if ($null -eq $_.archiveWarpId) { $null } else { "$ApiBaseUrl/api/warps/$($_.archiveWarpId)" }
            worldDownloadUrl = [string]$_.worldDownloadUrl
            worldDownloadMetadataUrl = [string]$_.worldDownloadMetadataUrl
            worldDownloadScope = [string]$_.worldDownloadScope
            worldDownloadSha256 = [string]$_.worldDownloadSha256
            worldDownloadSource = [string]$_.worldDownloadSource
            isSinglePlayerConcept = [bool]$_.isSinglePlayerConcept
        } })
        sources = @(@($location.wiki, $location.videoUrl) +
            @($location.attachments | ForEach-Object { $_.sourceUrl }) +
            @(Get-GroupEvidence ([int]$location.rowid) | ForEach-Object { $_.revisionUrl }) |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } | Sort-Object -Unique)
        attachments = @($location.attachments | ForEach-Object {
            [ordered]@{
                id = [int]$_.id
                name = [string]$_.fileName
                mediaType = [string]$_.mediaType
                url = [string]$_.path
                thumbnailUrl = [string]$_.thumbnailPath
                sourceUrl = [string]$_.sourceUrl
                caption = [string]$_.caption
                attribution = [string]$_.attribution
                apiUrl = "$ApiBaseUrl/api/attachments/$($_.id)"
            }
        })
        relationshipCounts = [ordered]@{
            groups = @($location.groups).Count
            warps = @($location.warps).Count
            worldDownloads = @($location.warps | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count +
                @($location.renders | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl) }).Count
            renders = @($location.renders).Count
            attachments = @($location.attachments).Count
        }
        relatedEntities = @($relatedLocations | ForEach-Object { [ordered]@{ id = [int]$_.rowid; name = [string]$_.name; url = "$SiteBaseUrl/entities/locations/$($_.rowid)/" } })
        url = $canonicalUrl
        legacyUrl = "$SiteBaseUrl/$(([string]$location.locationUuid).ToLowerInvariant())"
        interactiveUrl = "$SiteBaseUrl/location/$($location.rowid)"
        apiUrl = $apiUrl
        wiki = $location.wiki
        videoUrl = $location.videoUrl
        dateAdded = Get-IsoDate $location.dateAddedUtc
        dateModified = $locationLastModified
    }
    $jsonLines.Add(($catalogRecord | ConvertTo-Json -Depth 6 -Compress))
    foreach ($warp in @($location.warps | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    } | Sort-Object id)) {
        $linkedRenderIds = @($location.renders | Where-Object { $null -ne $_.archiveWarpId -and [int]$_.archiveWarpId -eq [int]$warp.id } | ForEach-Object { [int]$_.id })
        $worldDownloadRecord = [ordered]@{
            schemaVersion = 2
            id = [int]$warp.id
            type = 'world-download'
            sourceType = 'archive-warp'
            name = "Bounded Minecraft world download for /warp $($warp.name)"
            server = '2b2t'
            isSinglePlayerConcept = [bool]$warp.isSinglePlayerConcept
            scope = [string]$warp.worldDownloadScope
            isCompleteWorld = $false
            playability = 'partial-java-save'
            fidelityNotice = if ([bool]$warp.isSinglePlayerConcept) { 'Preserved single-player concept build, not a live 2b2t snapshot. It is also a playable bounded-footprint Minecraft Java save; chunks outside the retained footprint are absent.' } else { 'Playable bounded-footprint Minecraft Java save. Chunks outside the retained capture footprint are intentionally absent; this is not a complete 2b2t world copy.' }
            encodingFormat = 'application/zip'
            contentUrl = [string]$warp.worldDownloadUrl
            metadataUrl = [string]$warp.worldDownloadMetadataUrl
            sha256 = ([string]$warp.archiveSha256).ToLowerInvariant()
            dateCreated = Get-IsoDate $warp.worldDownloadDate
            source = [string]$warp.source
            warp = [ordered]@{
                id = [int]$warp.id
                name = [string]$warp.name
                command = "/warp $($warp.name)"
                apiUrl = "$ApiBaseUrl/api/warps/$($warp.id)"
            }
            location = [ordered]@{
                id = [int]$location.rowid
                name = [string]$location.name
                dimension = [string]$location.dimensionName
                coordinates = [ordered]@{ x = [int]$location.x; y = $location.y; z = [int]$location.z }
                url = "$SiteBaseUrl/entities/locations/$($location.rowid)/"
                interactiveUrl = "$SiteBaseUrl/location/$($location.rowid)"
                apiUrl = "$ApiBaseUrl/api/locations/$($location.rowid)"
            }
            renderIds = $linkedRenderIds
        }
        $worldDownloadRecords.Add($worldDownloadRecord)
        $worldDownloadJsonLines.Add(($worldDownloadRecord | ConvertTo-Json -Depth 7 -Compress))
        $conceptText = if ([bool]$warp.isSinglePlayerConcept) { '; <strong>single-player concept, not live 2b2t</strong>' } else { '' }
        $worldDownloadIndexItems.Add(('<li><a href="{0}">Download /warp {1}</a> for <a href="{2}">{3}</a> <span>{4}; bounded partial Java world{5}</span> <a href="{6}">metadata</a></li>' -f `
            (Encode-Html $worldDownloadRecord.contentUrl), (Encode-Html $worldDownloadRecord.warp.name), `
            (Encode-Html $worldDownloadRecord.location.url), (Encode-Html $worldDownloadRecord.location.name), `
            (Encode-Html $worldDownloadRecord.location.dimension), $conceptText, (Encode-Html $worldDownloadRecord.metadataUrl)))
    }
    foreach ($render in @($location.renders | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.worldDownloadUrl)
    } | Sort-Object id)) {
        $worldDownloadRecord = [ordered]@{
            schemaVersion = 2
            id = [int]$render.id
            type = 'world-download'
            sourceType = 'render'
            name = "Preserved Minecraft source world for $($render.name)"
            server = '2b2t'
            scope = [string]$render.worldDownloadScope
            isCompleteWorld = $false
            playability = 'partial-java-save'
            fidelityNotice = 'Playable source WDL preserved for an Atlas render. Coverage follows the historical source save; it is not a complete 2b2t world copy.'
            encodingFormat = 'application/zip'
            contentUrl = [string]$render.worldDownloadUrl
            metadataUrl = [string]$render.worldDownloadMetadataUrl
            sha256 = ([string]$render.worldDownloadSha256).ToLowerInvariant()
            dateCreated = Get-IsoDate $render.worldDownloadDate
            source = [string]$render.worldDownloadSource
            warp = $null
            location = [ordered]@{
                id = [int]$location.rowid
                name = [string]$location.name
                dimension = [string]$location.dimensionName
                coordinates = [ordered]@{ x = [int]$location.x; y = $location.y; z = [int]$location.z }
                url = "$SiteBaseUrl/entities/locations/$($location.rowid)/"
                interactiveUrl = "$SiteBaseUrl/location/$($location.rowid)"
                apiUrl = "$ApiBaseUrl/api/locations/$($location.rowid)"
            }
            renderIds = @([int]$render.id)
            render = [ordered]@{
                id = [int]$render.id
                name = [string]$render.name
                apiUrl = "$ApiBaseUrl/api/renders/$($render.id)"
            }
        }
        $worldDownloadRecords.Add($worldDownloadRecord)
        $worldDownloadJsonLines.Add(($worldDownloadRecord | ConvertTo-Json -Depth 7 -Compress))
        $worldDownloadIndexItems.Add(('<li><a href="{0}">Download source WDL for render {1}</a> at <a href="{2}">{3}</a> <span>{4}; preserved partial Java world</span> <a href="{5}">metadata</a></li>' -f `
            (Encode-Html $worldDownloadRecord.contentUrl), (Encode-Html $worldDownloadRecord.render.name), `
            (Encode-Html $worldDownloadRecord.location.url), (Encode-Html $worldDownloadRecord.location.name), `
            (Encode-Html $worldDownloadRecord.location.dimension), (Encode-Html $worldDownloadRecord.metadataUrl)))
    }
    $lastModified = $locationLastModified
    $lastModElement = if ($lastModified) { "<lastmod>$lastModified</lastmod>" } else { '' }
    $imageElements = @($location.attachments | Where-Object {
        [string]$_.mediaType -match '(?i)^image$' -and [string]$_.path -match '^https://'
    } | ForEach-Object {
        '<image:image><image:loc>{0}</image:loc></image:image>' -f (Encode-Html ([string]$_.path))
    }) -join ''
    $sitemapItems.Add("<url><loc>$(Encode-Html $canonicalUrl)</loc>$lastModElement$imageElements</url>")
    $legacyRedirectRules.Add(('RewriteRule ^{0}/?$ {1}/entities/locations/{2}/ [R=301,L,NE,NC]' -f `
        ([regex]::Escape(([string]$location.locationUuid).ToLowerInvariant())), $SiteBaseUrl, [int]$location.rowid))
}

$directoryJsonLd = [ordered]@{
    '@context' = 'https://schema.org'
    '@graph' = @(
        [ordered]@{
            '@type' = 'WebPage'
            '@id' = "$SiteBaseUrl/entities/locations/#webpage"
            url = "$SiteBaseUrl/entities/locations/"
            name = '2b2t Location Entity Directory'
            description = "Human-readable directory of $($locations.Count) documented 2b2t locations."
            dateModified = $catalogLastModified
            mainEntity = [ordered]@{ '@id' = "$SiteBaseUrl/entities/locations/#dataset" }
            isPartOf = [ordered]@{ '@id' = "$SiteBaseUrl/#website" }
        },
        [ordered]@{
            '@type' = 'Dataset'
            '@id' = "$SiteBaseUrl/entities/locations/#dataset"
            name = '2b2t Atlas location entities'
            description = 'Structured historical and Minecraft-coordinate records for documented locations on 2b2t.'
            url = "$SiteBaseUrl/entities/locations/"
            dateModified = $catalogLastModified
            isAccessibleForFree = $true
            creator = [ordered]@{ '@type' = 'Organization'; '@id' = "$SiteBaseUrl/#organization"; name = '2b2t Atlas'; url = "$SiteBaseUrl/" }
            includedInDataCatalog = [ordered]@{ '@type' = 'DataCatalog'; name = '2b2t Atlas'; url = "$SiteBaseUrl/entities/locations/" }
            distribution = @(
                [ordered]@{ '@type' = 'DataDownload'; encodingFormat = 'application/x-ndjson'; contentUrl = "$SiteBaseUrl/entities/locations.jsonl" },
                [ordered]@{ '@type' = 'DataDownload'; encodingFormat = 'application/json'; contentUrl = "$ApiBaseUrl/api/locations" }
            )
            variableMeasured = @('Location name', 'Minecraft dimension', 'Minecraft X coordinate', 'Minecraft Y coordinate', 'Minecraft Z coordinate', 'Archive warp', 'Historical description')
        }
    )
}
$directoryJsonLdText = ($directoryJsonLd | ConvertTo-Json -Depth 10 -Compress).Replace('&', '\u0026').Replace('<', '\u003C').Replace('>', '\u003E')

$indexHtml = @"
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>2b2t Location Entity Directory | 2b2t Atlas</title>
<meta name="description" content="Machine-readable and human-readable directory of $($locations.Count) documented 2b2t locations, bases, landmarks, and historical sites.">
<meta name="robots" content="index,follow,max-snippet:-1"><link rel="canonical" href="$SiteBaseUrl/entities/locations/">
<link rel="alternate" type="application/x-ndjson" href="$SiteBaseUrl/entities/locations.jsonl" title="Complete location entity catalog">
<meta property="og:type" content="website"><meta property="og:site_name" content="2b2t Atlas">
<meta property="og:title" content="2b2t Location Entity Directory"><meta property="og:description" content="$($locations.Count) canonical records of documented 2b2t locations.">
<meta property="og:url" content="$SiteBaseUrl/entities/locations/"><meta property="og:image" content="$SiteBaseUrl/Images/banner_web.png">
<meta name="twitter:card" content="summary_large_image"><meta name="twitter:image" content="$SiteBaseUrl/Images/banner_web.png">
<script type="application/ld+json">$directoryJsonLdText</script>
<style>body{max-width:980px;margin:auto;padding:24px;background:#111518;color:#e8edf0;font:16px/1.5 Arial,sans-serif}a{color:#83bdff}li{margin:8px 0}span{color:#91a0a8}</style></head>
<body><main><h1>2b2t location entities</h1><p>$($locations.Count) canonical records from 2b2t Atlas. Coordinates use Minecraft game-world blocks.</p><p><a href="$SiteBaseUrl/entities/locations.jsonl">Download the JSONL entity catalog</a> or <a href="$ApiBaseUrl/api/locations">query the public API</a>.</p><ol>$($indexItems -join "`n")</ol></main></body></html>
"@
Write-Utf8File (Join-Path $entityRoot 'index.html') $indexHtml
Write-Utf8File (Join-Path $OutputDirectory 'entities\locations.jsonl') (($jsonLines -join "`n") + "`n")

$mediaRecords = New-Object Collections.Generic.List[object]
$mediaJsonLines = New-Object Collections.Generic.List[string]
$mediaIndexItems = New-Object Collections.Generic.List[string]
foreach ($location in ($locations | Sort-Object rowid)) {
    foreach ($attachment in @($location.attachments | Sort-Object id)) {
        $record = [ordered]@{
            schemaVersion = 1
            id = [int]$attachment.id
            type = 'media'
            name = [string]$attachment.fileName
            mediaType = [string]$attachment.mediaType
            encodingFormat = Get-MediaEncodingFormat $attachment
            contentUrl = [string]$attachment.path
            thumbnailUrl = [string]$attachment.thumbnailPath
            sourceUrl = [string]$attachment.sourceUrl
            caption = [string]$attachment.caption
            attribution = [string]$attachment.attribution
            location = [ordered]@{
                id = [int]$location.rowid
                name = [string]$location.name
                dimension = [string]$location.dimensionName
                url = "$SiteBaseUrl/entities/locations/$($location.rowid)/"
                apiUrl = "$ApiBaseUrl/api/locations/$($location.rowid)"
            }
            apiUrl = "$ApiBaseUrl/api/attachments/$($attachment.id)"
            dateAdded = Get-IsoDate $attachment.dateAddedUtc
        }
        $mediaRecords.Add($record)
        $mediaJsonLines.Add(($record | ConvertTo-Json -Depth 6 -Compress))
        $mediaIndexItems.Add(('<li><a href="{0}">{1}</a> for <a href="{2}">{3}</a> <span>{4}</span> <a href="{5}">metadata</a></li>' -f `
            (Encode-Html $record.contentUrl), (Encode-Html $record.name), (Encode-Html $record.location.url), `
            (Encode-Html $record.location.name), (Encode-Html $record.mediaType), (Encode-Html $record.apiUrl)))
    }
}
Write-Utf8File (Join-Path $OutputDirectory 'entities\media.jsonl') (($mediaJsonLines -join "`n") + "`n")
$mediaDirectoryUrl = "$SiteBaseUrl/entities/media/"
$mediaDirectoryJsonLd = [ordered]@{
    '@context' = 'https://schema.org'
    '@graph' = @(
        [ordered]@{
            '@type' = 'CollectionPage'; '@id' = "$mediaDirectoryUrl#webpage"; url = $mediaDirectoryUrl
            name = '2b2t Historical Media Catalog'; description = "Sourced media and reference records for $($mediaRecords.Count) 2b2t Atlas attachments."
            dateModified = $catalogLastModified; mainEntity = [ordered]@{ '@id' = "$mediaDirectoryUrl#dataset" }
            isPartOf = [ordered]@{ '@id' = "$SiteBaseUrl/#website" }
        },
        [ordered]@{
            '@type' = 'Dataset'; '@id' = "$mediaDirectoryUrl#dataset"; name = '2b2t Atlas historical media records'
            description = 'Machine-readable provenance, captions, source URLs, media URLs, and owning-location relationships for reviewed 2b2t historical media.'
            url = $mediaDirectoryUrl; dateModified = $catalogLastModified; isAccessibleForFree = $true
            creator = [ordered]@{ '@type' = 'Organization'; '@id' = "$SiteBaseUrl/#organization"; name = '2b2t Atlas'; url = "$SiteBaseUrl/" }
            includedInDataCatalog = [ordered]@{ '@type' = 'DataCatalog'; name = '2b2t Atlas'; url = "$SiteBaseUrl/dataset.json" }
            distribution = @(
                [ordered]@{ '@type' = 'DataDownload'; encodingFormat = 'application/x-ndjson'; contentUrl = "$SiteBaseUrl/entities/media.jsonl" },
                [ordered]@{ '@type' = 'DataDownload'; encodingFormat = 'application/json'; contentUrl = "$ApiBaseUrl/api/attachments" }
            )
            variableMeasured = @('Media type', 'Content URL', 'Thumbnail URL', 'Original source URL', 'Caption', 'Attribution', 'Owning Atlas location')
        }
    )
}
$mediaDirectoryJsonLdText = ($mediaDirectoryJsonLd | ConvertTo-Json -Depth 10 -Compress).Replace('&', '\u0026').Replace('<', '\u003C').Replace('>', '\u003E')
$mediaDirectoryHtml = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>2b2t Historical Media Catalog | 2b2t Atlas</title><meta name="description" content="Sourced images, videos, Wiki references, and historical media attached to documented 2b2t locations.">
<meta name="robots" content="index,follow,max-image-preview:large,max-snippet:-1"><link rel="canonical" href="$mediaDirectoryUrl">
<link rel="alternate" type="application/x-ndjson" href="$SiteBaseUrl/entities/media.jsonl" title="Complete media record catalog">
<script type="application/ld+json">$mediaDirectoryJsonLdText</script>
<style>body{max-width:980px;margin:auto;padding:24px;background:#111518;color:#e8edf0;font:16px/1.5 Arial,sans-serif}a{color:#83bdff}li{margin:8px 0}span{color:#91a0a8}</style></head>
<body><main><p><a href="$SiteBaseUrl/">2b2t Atlas</a> / Media</p><h1>2b2t historical media records</h1><p>$($mediaRecords.Count) sourced attachment records linked to canonical Atlas locations. The JSONL catalog preserves captions, attribution, original source URLs, and API identities.</p><p><a href="$SiteBaseUrl/entities/media.jsonl">Download media JSONL</a> or <a href="$ApiBaseUrl/api/attachments">query the live attachments API</a>.</p><ol>$($mediaIndexItems -join "`n")</ol></main></body></html>
"@
Write-Utf8File (Join-Path $OutputDirectory 'entities\media\index.html') $mediaDirectoryHtml

Write-Utf8File (Join-Path $OutputDirectory 'entities\world-downloads.jsonl') (($worldDownloadJsonLines -join "`n") + "`n")
$worldDownloadDirectoryUrl = "$SiteBaseUrl/entities/world-downloads/"
$worldDownloadDirectoryJsonLd = [ordered]@{
    '@context' = 'https://schema.org'
    '@graph' = @(
        [ordered]@{
            '@type' = 'CollectionPage'; '@id' = "$worldDownloadDirectoryUrl#webpage"; url = $worldDownloadDirectoryUrl
            name = '2b2t World Download Catalog'; description = "Downloadable provenance-linked Minecraft Java saves for $($worldDownloadRecords.Count) documented Archive warps and historical Atlas renders."
            dateModified = $catalogLastModified; mainEntity = [ordered]@{ '@id' = "$worldDownloadDirectoryUrl#dataset" }
            isPartOf = [ordered]@{ '@id' = "$SiteBaseUrl/#website" }
        },
        [ordered]@{
            '@type' = 'Dataset'; '@id' = "$worldDownloadDirectoryUrl#dataset"; name = '2b2t Atlas preserved world downloads'
            description = 'A provenance-aware catalog of playable partial Minecraft Java saves retained from Archive warp captures and verified historical render sources. Coverage follows each retained source and these are not complete server worlds.'
            url = $worldDownloadDirectoryUrl; dateModified = $catalogLastModified; isAccessibleForFree = $true
            creator = [ordered]@{ '@type' = 'Organization'; '@id' = "$SiteBaseUrl/#organization"; name = '2b2t Atlas'; url = "$SiteBaseUrl/" }
            includedInDataCatalog = [ordered]@{ '@type' = 'DataCatalog'; name = '2b2t Atlas'; url = "$SiteBaseUrl/dataset.json" }
            distribution = @(
                [ordered]@{ '@type' = 'DataDownload'; name = 'World-download metadata catalog'; encodingFormat = 'application/x-ndjson'; contentUrl = "$SiteBaseUrl/entities/world-downloads.jsonl" },
                [ordered]@{ '@type' = 'DataDownload'; name = 'Live Archive warp metadata'; encodingFormat = 'application/json'; contentUrl = "$ApiBaseUrl/api/warps" },
                [ordered]@{ '@type' = 'DataDownload'; name = 'Live render metadata'; encodingFormat = 'application/json'; contentUrl = "$ApiBaseUrl/api/renders" }
            )
            variableMeasured = @('Source type', 'Archive warp when applicable', 'Owning Atlas location', 'Minecraft dimension and coordinates', 'Capture date', 'SHA-256 digest', 'Capture scope', 'Linked render IDs')
        }
    )
}
$worldDownloadDirectoryJsonLdText = ($worldDownloadDirectoryJsonLd | ConvertTo-Json -Depth 10 -Compress).Replace('&', '\u0026').Replace('<', '\u003C').Replace('>', '\u003E')
$worldDownloadDirectoryHtml = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>2b2t World Downloads | Preserved WDL Catalog | 2b2t Atlas</title><meta name="description" content="$($worldDownloadRecords.Count) downloadable, provenance-linked partial Minecraft Java saves from historic Archive warps and verified Atlas render sources.">
<meta name="robots" content="index,follow,max-snippet:-1"><link rel="canonical" href="$worldDownloadDirectoryUrl">
<link rel="alternate" type="application/x-ndjson" href="$SiteBaseUrl/entities/world-downloads.jsonl" title="Complete 2b2t world-download metadata catalog">
<script type="application/ld+json">$worldDownloadDirectoryJsonLdText</script>
<style>body{max-width:1100px;margin:auto;padding:24px;background:#111518;color:#e8edf0;font:16px/1.5 Arial,sans-serif}a{color:#83bdff}li{margin:9px 0}span{color:#91a0a8}</style></head>
<body><main><p><a href="$SiteBaseUrl/">2b2t Atlas</a> / World downloads</p><h1>2b2t preserved world downloads</h1><p>$($worldDownloadRecords.Count) downloadable Minecraft Java saves linked to an Archive warp or historical source render, canonical Atlas location, coordinates, metadata, and SHA-256 digest. Each ZIP is a playable <strong>partial world</strong>: coverage follows its retained historical source and it is not a complete 2b2t server world.</p><p><a href="$SiteBaseUrl/entities/world-downloads.jsonl">Download the WDL metadata JSONL</a>, <a href="$ApiBaseUrl/api/warps">query live warp metadata</a>, or <a href="$ApiBaseUrl/api/renders">query live render metadata</a>.</p><ol>$($worldDownloadIndexItems -join "`n")</ol></main></body></html>
"@
Write-Utf8File (Join-Path $OutputDirectory 'entities\world-downloads\index.html') $worldDownloadDirectoryHtml

$mcpGuideUrl = "$SiteBaseUrl/mcp/"
$mcpEndpoint = "$ApiBaseUrl/mcp"
$mcpRegistryUrl = 'https://registry.modelcontextprotocol.io/?q=io.github.example%2F2b2t-atlas'
$mcpJsonLd = [ordered]@{
    '@context' = 'https://schema.org'
    '@graph' = @(
        [ordered]@{
            '@type' = 'WebPage'
            '@id' = "$mcpGuideUrl#webpage"
            url = $mcpGuideUrl
            name = '2b2t Atlas MCP server'
            description = 'Public, read-only Model Context Protocol access to 2b2t Atlas locations, groups, highways, warps, renders, world downloads, and dataset statistics.'
            isPartOf = [ordered]@{ '@id' = "$SiteBaseUrl/#website" }
            mainEntity = [ordered]@{ '@id' = "$mcpGuideUrl#service" }
        },
        [ordered]@{
            '@type' = 'WebAPI'
            '@id' = "$mcpGuideUrl#service"
            name = '2b2t Atlas MCP server'
            url = $mcpGuideUrl
            documentation = "$SiteBaseUrl/api#mcp"
            description = 'A stateless Streamable HTTP MCP server for bounded, deterministic, read-only queries over the public 2b2t Atlas knowledge graph.'
            provider = [ordered]@{ '@id' = "$SiteBaseUrl/#organization" }
            serviceUrl = $mcpEndpoint
            identifier = 'io.github.example/2b2t-atlas'
            sameAs = $mcpRegistryUrl
            termsOfService = "$SiteBaseUrl/about"
        }
    )
}
$mcpJsonLdText = $mcpJsonLd | ConvertTo-Json -Depth 7 -Compress
$mcpTools = @(
    'search_locations', 'get_location', 'find_locations_near', 'find_locations_by_time_range',
    'find_preserved_builds', 'research_location', 'search_groups', 'get_group', 'get_group_builds',
    'search_highways', 'get_highway', 'get_warps', 'get_world_downloads',
    'get_render_metadata', 'get_dataset_stats', 'get_nocom_dataset', 'get_nocom_periods', 'get_nocom_highway_activity'
)
$mcpToolItems = @($mcpTools | ForEach-Object { '<li><code>{0}</code></li>' -f (Encode-Html $_) }) -join "`n"
$mcpClientConfig = @"
{
  &quot;mcpServers&quot;: {
    &quot;2b2t-atlas&quot;: {
      &quot;type&quot;: &quot;http&quot;,
      &quot;url&quot;: &quot;$mcpEndpoint&quot;
    }
  }
}
"@
$mcpHtml = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>2b2t Atlas MCP Server | AI and Agent Access</title>
<meta name="description" content="Connect AI assistants and research agents to the public, read-only 2b2t Atlas MCP server for structured locations, groups, highways, warps, renders, and world downloads.">
<meta name="robots" content="index,follow,max-snippet:-1"><link rel="canonical" href="$mcpGuideUrl">
<meta property="og:type" content="website"><meta property="og:site_name" content="2b2t Atlas"><meta property="og:title" content="2b2t Atlas MCP Server"><meta property="og:description" content="Public read-only Model Context Protocol access to the structured history and map data in 2b2t Atlas."><meta property="og:url" content="$mcpGuideUrl"><meta property="og:image" content="$SiteBaseUrl/Images/banner_web.png">
<script type="application/ld+json">$mcpJsonLdText</script>
<style>body{max-width:980px;margin:auto;padding:24px;background:#111518;color:#e8edf0;font:16px/1.55 Arial,sans-serif}a{color:#83bdff}code,pre{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}pre{overflow:auto;padding:16px;border:1px solid #38434a;border-radius:8px;background:#171c20;color:#d9f2ff}li{margin:7px 0}.tools{columns:2}@media(max-width:640px){.tools{columns:1}}</style></head>
<body><main><p><a href="$SiteBaseUrl/">2b2t Atlas</a> / MCP</p><h1>2b2t Atlas MCP server</h1>
<p>The Atlas MCP server gives AI assistants, research agents, bots, and developer tools deterministic read-only access to the same structured catalog used by the public Atlas. It covers canonical locations, groups and their builds, highways, Archive warps, render provenance, downloadable partial WDL metadata, nearby-place searches, historical date ranges, and live dataset statistics.</p>
<p><strong>Endpoint:</strong> <code>$mcpEndpoint</code><br><strong>Transport:</strong> Streamable HTTP<br><strong>Access:</strong> public, stateless, bounded, and read-only</p>
<p><strong>Official MCP Registry:</strong> <a href="$mcpRegistryUrl"><code>io.github.example/2b2t-atlas</code></a></p>
<h2>Connect an MCP client</h2><pre>$mcpClientConfig</pre>
<p>The server returns metadata and public download URLs rather than transferring render images or WDL ZIP bytes through MCP. Preserve canonical entity URLs and original provenance links when citing results.</p>
<h2>Available tools</h2><ul class="tools">$mcpToolItems</ul>
<h2>Resources</h2><p>Clients can read stable resources at <code>2b2tatlas://location/{id}</code>, <code>2b2tatlas://group/{id}</code>, <code>2b2tatlas://highway/{id}</code>, and <code>2b2tatlas://dataset</code>.</p>
<p><a href="$SiteBaseUrl/api#mcp">Full API and MCP documentation</a> | <a href="$mcpRegistryUrl">Official MCP Registry listing</a> | <a href="$SiteBaseUrl/about">About the preservation project</a> | <a href="https://github.com/bobymicjohn/2b2tAtlas-Public-API">Examples on GitHub</a></p>
</main></body></html>
"@
Write-Utf8File (Join-Path $OutputDirectory 'mcp\index.html') $mcpHtml

function New-LocationCollectionHtml {
    param([string]$Title, [string]$Description, [string]$CanonicalUrl, [array]$CollectionLocations)

    $items = @($CollectionLocations | Sort-Object name, rowid | ForEach-Object {
        '<li><a href="{0}/entities/locations/{1}/">{2}</a> <span>{3} - X {4}, Z {5}</span></li>' -f `
            $SiteBaseUrl, $_.rowid, (Encode-Html $_.name), (Encode-Html $_.dimensionName), $_.x, $_.z
    })
    return @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>$(Encode-Html $Title) | 2b2t Atlas</title><meta name="description" content="$(Encode-Html $Description)"><meta name="robots" content="index,follow,max-snippet:-1">
<link rel="canonical" href="$(Encode-Html $CanonicalUrl)"><meta property="og:type" content="website"><meta property="og:site_name" content="2b2t Atlas">
<meta property="og:title" content="$(Encode-Html $Title)"><meta property="og:description" content="$(Encode-Html $Description)"><meta property="og:url" content="$(Encode-Html $CanonicalUrl)">
<style>body{max-width:980px;margin:auto;padding:24px;background:#111518;color:#e8edf0;font:16px/1.5 Arial,sans-serif}a{color:#83bdff}li{margin:8px 0}span{color:#91a0a8}</style></head>
<body><main><p><a href="$SiteBaseUrl/">2b2t Atlas</a> / Locations</p><h1>$(Encode-Html $Title)</h1><p>$(Encode-Html $Description)</p><nav><a href="$SiteBaseUrl/locations/">All</a> | <a href="$SiteBaseUrl/locations/overworld/">Overworld</a> | <a href="$SiteBaseUrl/locations/nether/">Nether</a> | <a href="$SiteBaseUrl/locations/end/">End</a></nav><ol>$($items -join "`n")</ol></main></body></html>
"@
}

$allCollectionUrl = "$SiteBaseUrl/locations/"
Write-Utf8File (Join-Path $OutputDirectory 'locations\index.html') (New-LocationCollectionHtml '2b2t Locations' "$($locations.Count) documented bases, landmarks, portals, and historical sites across 2b2t." $allCollectionUrl $locations)
foreach ($dimensionName in 'Overworld', 'Nether', 'End') {
    $dimensionSlug = $dimensionName.ToLowerInvariant()
    $dimensionLocations = @($locations | Where-Object dimensionName -eq $dimensionName)
    $dimensionUrl = "$SiteBaseUrl/locations/$dimensionSlug/"
    Write-Utf8File (Join-Path $OutputDirectory "locations\$dimensionSlug\index.html") (New-LocationCollectionHtml "2b2t $dimensionName Locations" "$($dimensionLocations.Count) documented 2b2t locations in the $dimensionName dimension." $dimensionUrl $dimensionLocations)
}

$datasetMetadata = [ordered]@{
    schemaVersion = 5
    dataset = '2b2t Atlas location entities'
    generatedUtc = $generatedUtc
    recordCount = $locations.Count
    groupRelationshipCount = [int](@($locations | ForEach-Object { @($_.groups).Count } | Measure-Object -Sum).Sum)
    groupEvidenceCitationCount = [int](@($locations | ForEach-Object { @(Get-GroupEvidence ([int]$_.rowid)).Count } | Measure-Object -Sum).Sum)
    groupEvidenceLocationCount = @($locations | Where-Object { @(Get-GroupEvidence ([int]$_.rowid)).Count -gt 0 }).Count
    groupEvidenceIndexSha256 = $script:groupEvidenceIndexSha256
    groupEvidenceGeneratedUtc = $script:groupEvidenceGeneratedUtc
    warpCount = [int](@($locations | ForEach-Object { @($_.warps).Count } | Measure-Object -Sum).Sum)
    worldDownloadCount = $worldDownloadRecords.Count
    renderCount = [int](@($locations | ForEach-Object { @($_.renders).Count } | Measure-Object -Sum).Sum)
    attachmentCount = [int](@($locations | ForEach-Object { @($_.attachments).Count } | Measure-Object -Sum).Sum)
    canonicalUrl = "$SiteBaseUrl/entities/locations.jsonl"
    mediaCatalogUrl = "$SiteBaseUrl/entities/media.jsonl"
    worldDownloadCatalogUrl = "$SiteBaseUrl/entities/world-downloads.jsonl"
    historicalObservationDatasets = @([ordered]@{
        name = 'Nocom World Pulse'; url = "$SiteBaseUrl/nocom/"; metadataUrl = "$SiteBaseUrl/nocom/dataset.json"
        periodsUrl = "$SiteBaseUrl/nocom/periods.jsonl"; highwaysUrl = "$SiteBaseUrl/nocom/highways.jsonl"
        apiUrl = "$ApiBaseUrl/api/nocom"; scope = 'Historical loaded-chunk aggregates; not players, visits or ownership. Original-source terms apply separately.'
    })
    dateModified = $catalogLastModified
    attribution = 'Atlas attribution is optional and appreciated. When practical, cite the canonical entity URL and retain original source/provenance links.'
    license = 'Atlas-authored dataset metadata and factual catalog records may be reused for any purpose without permission. Third-party media may retain separate terms from its original creator.'
    coordinateSystem = 'Minecraft game-world blocks; not geographic latitude or longitude.'
    ingestionPipeline = [ordered]@{
        documentationUrl = "$SiteBaseUrl/about#ingestion-pipeline"
        sourceWorldScope = 'Bounded captures from preserved Archive copies and separately supplied historical Minecraft Java saves. Coverage audits verify the surveyed area, not the full historical extent of a build.'
        scheduling = '2D publication, downstream 3D generation and historical enrichment progress independently; stage positions describe the process, not a single blocking queue.'
        publicationFreshness = 'API and MCP expose published records; static HTML and catalog exports reflect the latest site publication and may lag behind the interactive Atlas.'
        dimensions = @('Overworld', 'Nether', 'End')
        integrity = @('Structural source inspection', 'SHA-256 source digest', 'Exact retained-footprint audit', 'Transactional catalog registration')
        software = @(
            [ordered]@{ name = 'Archive World Downloader'; role = 'Bounded Minecraft chunk capture'; url = 'https://github.com/thearchive-world/archive-world-downloader' },
            [ordered]@{ name = 'Local AI enrichment'; role = 'Source-backed history and relationship suggestions for review' },
            [ordered]@{ name = 'uNmINeD'; role = 'Day and night 2D web tile rendering'; url = 'https://unmined.net/' },
            [ordered]@{ name = 'BlueMap'; role = 'Interactive quality-gated 3D rendering'; url = 'https://bluemap.bluecolored.de/' }
        )
        enrichment = [ordered]@{
            execution = 'Local model; optional and isolated from core Atlas availability.'
            evidence = @('Revision-pinned 2b2t Wiki index', 'Reviewed group/build relationships', 'Source-attributed external images, videos, and renders')
            output = @('Location histories', 'Wiki matches', 'Group/build relationships', 'Attachment provenance')
            reviewPolicy = 'Model output is a suggestion. Ambiguous identity or attribution changes require administrator review. Eligible evidence-backed suggestions may auto-apply under the review policy, preserve human group credits and remain reviewable.'
        }
        stages = @(
            [ordered]@{ position = 1; name = 'Collect the saved world'; output = 'Resumable Archive capture or an existing supplied WDL' },
            [ordered]@{ position = 2; name = 'Check and preserve'; output = 'Structurally inspected, SHA-256-identified source; collector coverage audit or private repair/review hold' },
            [ordered]@{ position = 3; name = 'Match the location and builders'; output = 'Location match or human review, with reviewed warp/name evidence for group credits' },
            [ordered]@{ position = 4; name = 'Render and publish the 2D map'; output = 'Verified dimension-aware day/night tiles, Nether cutaway settings, source WDL and catalog registration' },
            [ordered]@{ position = 5; name = 'Add the interactive 3D view'; output = 'Independently queued BlueMap generation after disposable-copy relighting and exact saved-chunk-set checks' },
            [ordered]@{ position = 6; name = 'Build out the history'; output = 'Independently researched, sourced histories, group relationships and media; reviewable AI suggestions' },
            [ordered]@{ position = 7; name = 'Make it useful beyond the map'; output = 'Available location assets and provenance linked through the site, public API and MCP; static catalogs refreshed with site publication' }
        )
        failureIsolation = 'A BlueMap retry does not roll back an already verified 2D ingestion.'
    }
}
$datasetJson = ($datasetMetadata | ConvertTo-Json -Depth 7) + "`n"
Write-Utf8File (Join-Path $OutputDirectory 'dataset.json') $datasetJson
Write-Utf8File (Join-Path $OutputDirectory 'entities\dataset.json') $datasetJson
Write-Utf8File (Join-Path $OutputDirectory 'entities\legacy-uuid-redirects.inc') (($legacyRedirectRules -join "`n") + "`n")

$homePath = Join-Path $OutputDirectory 'index.html'
$homeHtml = [IO.File]::ReadAllText($homePath)
$featured = @($locations | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.description) } | Sort-Object name | Select-Object -First 12 | ForEach-Object { '<li><a href="{0}/entities/locations/{1}/">{2}</a></li>' -f $SiteBaseUrl, $_.rowid, (Encode-Html $_.name) })
$homeFallback = @"
<!-- SEO_HOME_FALLBACK_START --><main style="max-width:900px;margin:0 auto;padding:32px;color:#e8edf0;background:#111518;font:16px/1.6 Georgia,serif"><p style="font:700 12px Arial,sans-serif;text-transform:uppercase;color:#8ca0ab">Public 2b2t archive</p><h1 style="font:700 42px/1.1 Arial,sans-serif">2b2t Atlas</h1><p>Explore $($locations.Count) documented 2b2t bases, landmarks, highways, historical sites, preserved partial world downloads, uNmINeD 2D map renders, and quality-gated BlueMap 3D views across the Overworld, Nether, and End. This directory remains readable even when the interactive map or API is unavailable.</p><p>The <a href="/about#ingestion-pipeline">provenance-preserving ingestion pipeline</a> connects each eligible derivative to its bounded source WDL, Archive warp when applicable, coordinates, dimension, capture date, checksum, and canonical location record.</p><nav aria-label="Atlas collections"><a href="/locations/">All locations</a> | <a href="/locations/overworld/">Overworld</a> | <a href="/locations/nether/">Nether</a> | <a href="/locations/end/">End</a> | <a href="/entities/groups/">Groups</a> | <a href="/entities/media/">Historical media</a> | <a href="/entities/world-downloads/">World downloads</a> | <a href="/nocom/">Nocom historical data</a> | <a href="/mcp/">MCP server</a></nav><h2>Featured location records</h2><ul>$($featured -join '')</ul><p><a href="/entities/locations.jsonl">Machine-readable location catalog</a> | <a href="/entities/media.jsonl">Historical media catalog</a> | <a href="/entities/world-downloads.jsonl">World-download catalog</a> | <a href="/dataset.json">Dataset and pipeline metadata</a> | <a href="/mcp/">Agent access via MCP</a></p></main><!-- SEO_HOME_FALLBACK_END -->
"@
$homePattern = '(?s)<!-- SEO_HOME_FALLBACK_START -->.*?<!-- SEO_HOME_FALLBACK_END -->'
if ([regex]::Matches($homeHtml, $homePattern).Count -ne 1) { throw 'Could not identify exactly one SEO homepage fallback block.' }
Write-Utf8File $homePath ([regex]::Replace($homeHtml, $homePattern, [Text.RegularExpressions.MatchEvaluator]{ param($match) $homeFallback }))

$sitemap = '<?xml version="1.0" encoding="UTF-8"?>' + "`n" + `
    '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:image="http://www.google.com/schemas/sitemap-image/1.1">' + "`n" + `
    ($sitemapItems -join "`n") + "`n</urlset>`n"
Write-Utf8File (Join-Path $OutputDirectory 'sitemap.xml') $sitemap

$robots = @"
User-agent: *
Allow: /
Disallow: /jsonapi

Sitemap: $SiteBaseUrl/sitemap.xml
"@
Write-Utf8File (Join-Path $OutputDirectory 'robots.txt') $robots

$llms = @"
# 2b2t Atlas

> A public, community-maintained directory and interactive map of documented locations, bases, landmarks, highways, and historical sites on the Minecraft anarchy server 2b2t.

Minecraft X, Y, and Z values are game-world block coordinates. They are not geographic latitude, longitude, or elevation.

The Atlas distinguishes a named location from its historical world downloads: one Archive `/warp` identifies one WDL snapshot and render, while one location may have several dated warps and renders. Renders may identify their collector, manual-upload, ingestion, or legacy provenance directly.

Location JSONL records may include `groupEvidence`: revision-pinned wiki citations are emitted only when an exact build candidate's group ID matches an already-reviewed Atlas location/group relationship. These citations support the relationship, not any additional unsourced narrative.

## Preservation and rendering pipeline

- [End-to-end pipeline documentation]($SiteBaseUrl/about#ingestion-pipeline): Archive collectors reuse verified saved chunks during resume; separately supplied WDLs enter the same inspection, source preservation, matching and 2D publication process without another Minecraft download.
- Group attribution starts with reviewed warp/name evidence. Independent enrichment uses a local model with revision-pinned Wiki and group evidence; eligible suggestions may auto-apply but remain reviewable, while ambiguous identity or attribution changes wait for an administrator.
- [uNmINeD](https://unmined.net/): Produces the Atlas day and night 2D tile pyramids after dimension-aware bounds and optional cutaway settings are applied.
- [BlueMap](https://bluemap.bluecolored.de/): Produces independently queued interactive 3D views after exact-footprint relighting and validation.
- 2D publication and BlueMap generation are failure-isolated. A failed or retried 3D derivative never rolls back an already verified source-backed 2D ingestion.
- Coverage checks validate the surveyed capture area, not a build's full historical extent. Missing upstream terrain cannot be reconstructed by the collector.
- API/MCP records can update before the static HTML and catalog exports, which reflect the latest site publication.
- Overworld, Nether, and End captures are treated independently. When a warp has both Overworld and Nether evidence, each dimension receives its own source-backed render record.

## Location entities

- [Human-readable entity directory]($SiteBaseUrl/entities/locations/): Links to every canonical location entity.
- [Complete JSONL entity catalog]($SiteBaseUrl/entities/locations.jsonl): One normalized location record per line for retrieval and analysis.
- [Historical media catalog]($SiteBaseUrl/entities/media/): Human-readable source, caption, attribution, and owning-location relationships.
- [Historical media JSONL]($SiteBaseUrl/entities/media.jsonl): One normalized attachment record per line for retrieval and provenance-aware ingestion.
- [World-download catalog]($SiteBaseUrl/entities/world-downloads/): Crawlable downloads linked to Archive warps, canonical locations, dates, source renders, checksums, and explicit partial-world scope.
- [World-download JSONL]($SiteBaseUrl/entities/world-downloads.jsonl): One bounded WDL record per line for bots, mods, preservation tooling, and LLM retrieval.
- [Public attachments API]($ApiBaseUrl/api/attachments): Live, pageable attachment metadata with canonical owning-location links.
- [Dataset metadata]($SiteBaseUrl/dataset.json): Schema version, generation time, attribution, record count, and reuse terms.
- [XML sitemap]($SiteBaseUrl/sitemap.xml): Discovery list for all public location and group entity pages.
- [Location collections]($SiteBaseUrl/locations/): Server-readable hierarchy with Overworld, Nether, and End indexes.
- [Public locations API]($ApiBaseUrl/api/locations): Full live location data in JSON.
- [OpenAPI document]($ApiBaseUrl/openapi/v1.json): Machine-readable API contract.

## Agent access

- [Nocom World Pulse dataset]($SiteBaseUrl/nocom/): First-class historical observation dataset, source provenance, caveats and native-dimension time series.
- [Nocom JSON metadata]($SiteBaseUrl/nocom/dataset.json), [period aggregates]($SiteBaseUrl/nocom/periods.jsonl), and [highway aggregates]($SiteBaseUrl/nocom/highways.jsonl).
- [Nocom public API]($ApiBaseUrl/api/nocom): Use get_nocom_dataset, get_nocom_periods and get_nocom_highway_activity through MCP. Counts are scanner-biased observations, not players or ownership. The aggregate starts March 2020; the broader exploit history starts in 2018. Periods are fixed 30-day buckets, not calendar months.

- [MCP guide]($SiteBaseUrl/mcp/): Connection instructions, tool inventory, resource templates, scope, and citation guidance for AI assistants and research agents.
- [Remote MCP endpoint]($ApiBaseUrl/mcp): Public, stateless Streamable HTTP endpoint for bounded read-only access to Atlas locations, groups, highways, warps, renders, WDL metadata, and dataset statistics.

## Interactive Atlas

- [2b2t Atlas]($SiteBaseUrl/): Search, filter, map, and inspect the public archive.
- [Interactive map]($SiteBaseUrl/map): Explore locations, highways, terrain renders, and World Pulse overlays.

## Attribution

Treat each entity URL as the canonical citation for that Atlas location record. Preserve source links and distinguish Atlas records from linked wiki or video sources.

The Archive and other WDL/render sources preserve historical snapshots; they do not imply that terrain still exists on current 2b2t. Dates belong to the WDL/render record and are not part of an automatically created location's canonical name.
"@
Write-Utf8File (Join-Path $OutputDirectory 'llms.txt') $llms

& (Join-Path $PSScriptRoot 'generate-nocom-page.ps1') -DataApiBaseUrl $DataApiBaseUrl -ApiBaseUrl $ApiBaseUrl -SiteBaseUrl $SiteBaseUrl -OutputDirectory $OutputDirectory

Write-Host "Generated $($locations.Count) static location entities in $entityRoot"
