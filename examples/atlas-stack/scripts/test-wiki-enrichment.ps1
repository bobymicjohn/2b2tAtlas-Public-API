[CmdletBinding()]
param(
    [string]$AtlasUrl = 'http://127.0.0.1:5297/api/locations',
    [string]$WikiApi = 'https://2b2t.miraheze.org/w/api.php',
    [string]$OutputDirectory,
    [int]$MaxWikiPages = 20000,
    [int]$MaxDescriptionLength = 600
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repoRoot ".artifacts\wiki-enrichment-$stamp"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) {
    throw "Output directory already exists: $OutputDirectory"
}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

function Invoke-WikiApi {
    param([hashtable]$Parameters)

    $Parameters['format'] = 'json'
    $Parameters['formatversion'] = '2'
    $pairs = foreach ($key in $Parameters.Keys) {
        '{0}={1}' -f [Uri]::EscapeDataString([string]$key), [Uri]::EscapeDataString([string]$Parameters[$key])
    }
    $uri = $WikiApi.TrimEnd('?') + '?' + ($pairs -join '&')
    $headers = @{ 'User-Agent' = '2b2tAtlas metadata matcher/1.0 (administrator review only)' }
    return Invoke-RestMethod -Uri $uri -Headers $headers -TimeoutSec 30
}

function Normalize-Title {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $normalized = $Value.Normalize([Text.NormalizationForm]::FormKD).ToLowerInvariant()
    $normalized = $normalized -replace '&', ' and '
    $normalized = $normalized -replace '[^a-z0-9]+', ' '
    return ($normalized -replace '\s+', ' ').Trim()
}

function Get-Description {
    param([string]$Extract, [int]$Limit)

    if ([string]::IsNullOrWhiteSpace($Extract)) { return $null }
    $text = ($Extract -replace '\s+', ' ').Trim()
    if ($text -match '^(?i)this (article|page) is about\b') { return $null }
    if ($text.Length -le $Limit) { return $text }

    $candidate = $text.Substring(0, $Limit)
    $sentenceEnd = [Math]::Max($candidate.LastIndexOf('. '), [Math]::Max($candidate.LastIndexOf('! '), $candidate.LastIndexOf('? ')))
    if ($sentenceEnd -ge 120) {
        return $candidate.Substring(0, $sentenceEnd + 1).Trim()
    }
    $wordEnd = $candidate.LastIndexOf(' ')
    if ($wordEnd -gt 0) { $candidate = $candidate.Substring(0, $wordEnd) }
    return $candidate.Trim() + '...'
}

Write-Host "Loading Atlas locations from $AtlasUrl"
$locationResponse = Invoke-RestMethod -Uri $AtlasUrl -TimeoutSec 30
$locations = @()
foreach ($locationItem in $locationResponse) {
    $locations += $locationItem
}
if ($locations.Count -eq 0) { throw 'Atlas returned no locations.' }

Write-Host "Loading wiki page titles from $WikiApi"
$wikiTitles = New-Object System.Collections.Generic.List[string]
$continue = $null
do {
    $parameters = @{
        action = 'query'
        list = 'allpages'
        apnamespace = '0'
        aplimit = 'max'
    }
    if ($continue) { $parameters['apcontinue'] = $continue }
    $response = Invoke-WikiApi $parameters
    foreach ($page in @($response.query.allpages)) {
        if ($wikiTitles.Count -ge $MaxWikiPages) { break }
        $wikiTitles.Add([string]$page.title)
    }
    $continue = if ($response.continue) { [string]$response.continue.apcontinue } else { $null }
} while ($continue -and $wikiTitles.Count -lt $MaxWikiPages)

$titleIndex = @{}
foreach ($title in $wikiTitles) {
    $key = Normalize-Title $title
    if (-not $key) { continue }
    if (-not $titleIndex.ContainsKey($key)) {
        $titleIndex[$key] = New-Object System.Collections.Generic.List[string]
    }
    $titleIndex[$key].Add($title)
}

$locationNameCounts = @{}
foreach ($location in $locations) {
    $key = Normalize-Title ([string]$location.name)
    if (-not $key) { continue }
    $locationNameCounts[$key] = 1 + [int]$locationNameCounts[$key]
}

$candidates = New-Object System.Collections.Generic.List[object]
$duplicateAtlasNameRejected = 0
foreach ($location in $locations) {
    $key = Normalize-Title ([string]$location.name)
    if (-not $key -or -not $titleIndex.ContainsKey($key)) { continue }
    if ($locationNameCounts[$key] -ne 1) {
        $duplicateAtlasNameRejected++
        continue
    }
    $titles = $titleIndex[$key].ToArray()
    if ($titles.Count -ne 1) { continue }
    $candidates.Add([pscustomobject]@{
        rowid = [int]$location.rowid
        locationName = [string]$location.name
        wikiTitle = [string]$titles[0]
        confidence = 1.0
        matchReason = 'Unique normalized title match'
        existingDescription = [string]$location.description
        existingWiki = [string]$location.wiki
    })
}

Write-Host "Fetching intro extracts for $($candidates.Count) high-confidence matches"
$pageDetails = @{}
$titleAliases = @{}
$candidateArray = $candidates.ToArray()
$requestedTitles = @($candidateArray | Select-Object -ExpandProperty wikiTitle -Unique)
for ($offset = 0; $offset -lt $requestedTitles.Count; $offset += 50) {
    $last = [Math]::Min($offset + 49, $requestedTitles.Count - 1)
    $batch = @($requestedTitles[$offset..$last])
    $response = Invoke-WikiApi @{
        action = 'query'
        prop = 'extracts|info|pageprops'
        titles = ($batch -join '|')
        redirects = '1'
        exintro = '1'
        explaintext = '1'
        inprop = 'url'
    }
    foreach ($normalized in @($response.query.normalized)) {
        $titleAliases[(Normalize-Title ([string]$normalized.from))] = Normalize-Title ([string]$normalized.to)
    }
    foreach ($redirect in @($response.query.redirects)) {
        $titleAliases[(Normalize-Title ([string]$redirect.from))] = Normalize-Title ([string]$redirect.to)
    }
    foreach ($page in @($response.query.pages)) {
        if ($page.missing) { continue }
        $pageDetails[(Normalize-Title ([string]$page.title))] = $page
    }
    Start-Sleep -Milliseconds 150
}

$matches = New-Object System.Collections.Generic.List[object]
foreach ($candidate in $candidates) {
    $pageKey = Normalize-Title $candidate.wikiTitle
    for ($aliasDepth = 0; $aliasDepth -lt 4 -and $titleAliases.ContainsKey($pageKey); $aliasDepth++) {
        $pageKey = $titleAliases[$pageKey]
    }
    $page = $pageDetails[$pageKey]
    if (-not $page) { continue }
    $isDisambiguation = $null -ne $page.pageprops -and $null -ne $page.pageprops.disambiguation
    $description = if ($isDisambiguation) { $null } else { Get-Description ([string]$page.extract) $MaxDescriptionLength }
    $matches.Add([pscustomobject]@{
        rowid = $candidate.rowid
        locationName = $candidate.locationName
        wikiTitle = [string]$page.title
        wikiUrl = [string]$page.fullurl
        confidence = $candidate.confidence
        matchReason = $candidate.matchReason
        disambiguation = $isDisambiguation
        suggestedWiki = if ([string]::IsNullOrWhiteSpace($candidate.existingWiki)) { [string]$page.fullurl } else { $null }
        suggestedDescription = if ([string]::IsNullOrWhiteSpace($candidate.existingDescription)) { $description } else { $null }
        sourceRevisionId = if ($page.lastrevid) { [long]$page.lastrevid } else { $null }
    })
}

$matchArray = $matches.ToArray()
$withDescriptions = @($matchArray | Where-Object { -not [string]::IsNullOrWhiteSpace($_.suggestedDescription) })
$report = [ordered]@{
    schemaVersion = 1
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    atlasUrl = $AtlasUrl
    wikiApi = $WikiApi
    policy = 'Dry run only; unique normalized title matches; no database writes; no model inference.'
    stats = [ordered]@{
        atlasLocations = $locations.Count
        wikiPagesScanned = $wikiTitles.Count
        highConfidenceMatches = $matches.Count
        suggestedDescriptions = $withDescriptions.Count
        disambiguationPagesRejected = @($matchArray | Where-Object { $_.disambiguation }).Count
        duplicateAtlasNameRejected = $duplicateAtlasNameRejected
    }
    matches = $matchArray
}

$jsonPath = Join-Path $OutputDirectory 'wiki-enrichment-report.json'
$markdownPath = Join-Path $OutputDirectory 'wiki-enrichment-review.md'
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($jsonPath, ($report | ConvertTo-Json -Depth 8), $utf8NoBom)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Wiki enrichment review')
$lines.Add('')
$lines.Add("Generated: $($report.generatedUtc)")
$lines.Add('')
$lines.Add("- Atlas locations: $($report.stats.atlasLocations)")
$lines.Add("- Wiki pages scanned: $($report.stats.wikiPagesScanned)")
$lines.Add("- High-confidence matches: $($report.stats.highConfidenceMatches)")
$lines.Add("- Suggested descriptions: $($report.stats.suggestedDescriptions)")
$lines.Add('')
$lines.Add('No changes were applied. Review source links and descriptions before importing.')
$lines.Add('')
foreach ($match in $matchArray) {
    $lines.Add("## $($match.locationName) (Atlas ID $($match.rowid))")
    $lines.Add('')
    $lines.Add("- Wiki: [$($match.wikiTitle)]($($match.wikiUrl))")
    $lines.Add("- Confidence: $($match.confidence) - $($match.matchReason)")
    if ($match.disambiguation) { $lines.Add('- Rejected for description: disambiguation page') }
    if ($match.suggestedDescription) {
        $lines.Add('')
        $lines.Add($match.suggestedDescription)
    }
    $lines.Add('')
}
[IO.File]::WriteAllLines($markdownPath, $lines, $utf8NoBom)

Write-Host "Report: $jsonPath"
Write-Host "Review: $markdownPath"
$report.stats | Format-List
