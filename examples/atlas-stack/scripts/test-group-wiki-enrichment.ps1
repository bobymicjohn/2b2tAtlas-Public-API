[CmdletBinding()]
param(
    [string]$AtlasGroupsUrl = 'http://127.0.0.1:5297/api/groups',
    [string]$WikiApi = 'https://2b2t.miraheze.org/w/api.php',
    [string]$PublicWikiBase = 'https://2b2t.wikioasis.org/wiki/',
    [string]$OutputDirectory,
    [int]$MaxSummaryLength = 600
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $repoRoot ".artifacts\group-wiki-enrichment-$stamp"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $OutputDirectory) { throw "Output directory already exists: $OutputDirectory" }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null

function Get-Summary {
    param([string]$Text, [int]$Limit)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $value = ($Text -replace '\s+', ' ').Trim()
    if ($value.Length -le $Limit) { return $value }
    $candidate = $value.Substring(0, $Limit)
    $end = $candidate.LastIndexOf('. ')
    if ($end -ge 120) { return $candidate.Substring(0, $end + 1).Trim() }
    $word = $candidate.LastIndexOf(' ')
    if ($word -gt 0) { $candidate = $candidate.Substring(0, $word) }
    return $candidate.Trim() + '...'
}

$groupResponse = Invoke-RestMethod -Uri $AtlasGroupsUrl -TimeoutSec 30
$groups = @()
foreach ($group in $groupResponse) { $groups += $group }
$headers = @{ 'User-Agent' = '2b2tAtlas group metadata matcher/1.0 (administrator review only)' }
$matches = @()
foreach ($group in $groups) {
    $parameters = [ordered]@{
        action = 'query'
        prop = 'extracts|info|pageprops'
        redirects = '1'
        exintro = '1'
        explaintext = '1'
        inprop = 'url'
        format = 'json'
        formatversion = '2'
        titles = [string]$group.name
    }
    $pairs = foreach ($key in $parameters.Keys) {
        '{0}={1}' -f [Uri]::EscapeDataString([string]$key), [Uri]::EscapeDataString([string]$parameters[$key])
    }
    $response = Invoke-RestMethod -Uri ($WikiApi + '?' + ($pairs -join '&')) -Headers $headers -TimeoutSec 30
    $page = @($response.query.pages)[0]
    if ($null -ne $page.missing) { continue }
    $disambiguation = $null -ne $page.pageprops -and $null -ne $page.pageprops.disambiguation
    $matches += [pscustomobject]@{
        groupId = [int]$group.id
        groupName = [string]$group.name
        groupType = [string]$group.type
        wikiTitle = [string]$page.title
        wikiUrl = $PublicWikiBase.TrimEnd('/') + '/' + [Uri]::EscapeDataString(([string]$page.title -replace ' ', '_'))
        sourceRevisionId = if ($page.lastrevid) { [long]$page.lastrevid } else { $null }
        confidence = 1.0
        matchReason = 'Exact title or MediaWiki redirect'
        disambiguation = $disambiguation
        suggestedWikiUrl = if ([string]::IsNullOrWhiteSpace([string]$group.wikiUrl)) { [string]$page.fullurl } else { $null }
        suggestedDescription = if (-not $disambiguation -and [string]::IsNullOrWhiteSpace([string]$group.description)) { Get-Summary ([string]$page.extract) $MaxSummaryLength } else { $null }
    }
    Start-Sleep -Milliseconds 150
}

$report = [ordered]@{
    schemaVersion = 1
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    atlasGroupsUrl = $AtlasGroupsUrl
    wikiApi = $WikiApi
    policy = 'Dry run only; exact title or MediaWiki redirect; no database writes; no model inference.'
    stats = [ordered]@{
        groups = $groups.Count
        highConfidenceMatches = $matches.Count
        suggestedSummaries = @($matches | Where-Object { $_.suggestedDescription }).Count
    }
    matches = $matches
}

$jsonPath = Join-Path $OutputDirectory 'group-wiki-enrichment-report.json'
$markdownPath = Join-Path $OutputDirectory 'group-wiki-enrichment-review.md'
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($jsonPath, ($report | ConvertTo-Json -Depth 8), $utf8NoBom)
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Group wiki enrichment review')
$lines.Add('')
$lines.Add("- Groups: $($report.stats.groups)")
$lines.Add("- High-confidence matches: $($report.stats.highConfidenceMatches)")
$lines.Add("- Suggested summaries: $($report.stats.suggestedSummaries)")
$lines.Add('')
$lines.Add('No changes were applied. Review group identity, aliases, and source revision before approval.')
$lines.Add('')
foreach ($match in $matches) {
    $lines.Add("## $($match.groupName) (Group $($match.groupId))")
    $lines.Add('')
    $lines.Add("- Wiki: [$($match.wikiTitle)]($($match.wikiUrl))")
    $lines.Add("- Match: $($match.matchReason)")
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
