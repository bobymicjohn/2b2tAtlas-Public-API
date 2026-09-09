[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EntityRoot,

    [string]$GroupEvidenceIndexPath = ''
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($EntityRoot)
$locationsPath = Join-Path $root 'locations.jsonl'
$groupsPath = Join-Path $root 'groups.jsonl'

foreach ($requiredPath in @($locationsPath, $groupsPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required semantic entity catalog was not found: $requiredPath"
    }
}

function Read-JsonLines {
    param([Parameter(Mandatory = $true)][string]$Path)

    $records = @()
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $records += ($line | ConvertFrom-Json)
    }
    return @($records)
}

function Get-PresentCount {
    param($Value)

    if ($null -eq $Value) { return 0 }
    return @($Value).Count
}

$locations = @(Read-JsonLines -Path $locationsPath)
$groups = @(Read-JsonLines -Path $groupsPath)
$linkedLocations = @($locations | Where-Object { (Get-PresentCount -Value $_.groups) -gt 0 })
$groupStats = @{}
$locationGaps = @()

foreach ($location in $linkedLocations) {
    $missingDescription = [string]::IsNullOrWhiteSpace([string]$location.description)
    $missingSources = (Get-PresentCount -Value $location.sources) -eq 0
    $missingRenders = (Get-PresentCount -Value $location.renders) -eq 0

    $locationGaps += [pscustomobject]@{
        locationId = [int]$location.id
        name = [string]$location.name
        dimension = [string]$location.dimension
        url = [string]$location.url
        apiUrl = [string]$location.apiUrl
        groupIds = @($location.groups | ForEach-Object { [int]$_.id })
        groupNames = @($location.groups | ForEach-Object { [string]$_.name })
        missingDescription = $missingDescription
        missingSources = $missingSources
        missingDescriptionAndSources = ($missingDescription -and $missingSources)
        noPublicRenders = $missingRenders
        archiveWarpCount = Get-PresentCount -Value $location.archiveWarps
        renderCount = Get-PresentCount -Value $location.renders
        attachmentCount = Get-PresentCount -Value $location.attachments
    }

    foreach ($relationship in @($location.groups)) {
        $groupId = [int]$relationship.id
        $key = [string]$groupId
        if (-not $groupStats.ContainsKey($key)) {
            $groupStats[$key] = [pscustomobject]@{
                groupId = $groupId
                groupName = [string]$relationship.name
                linkedLocations = 0
                missingDescriptions = 0
                missingSources = 0
                missingBoth = 0
                noPublicRenders = 0
            }
        }

        $stats = $groupStats[$key]
        $stats.linkedLocations++
        if ($missingDescription) { $stats.missingDescriptions++ }
        if ($missingSources) { $stats.missingSources++ }
        if ($missingDescription -and $missingSources) { $stats.missingBoth++ }
        if ($missingRenders) { $stats.noPublicRenders++ }
    }
}

$knownGroupIds = @{}
foreach ($group in $groups) { $knownGroupIds[[string][int]$group.id] = $true }
$unknownRelationshipGroupIds = @($groupStats.Keys | Where-Object { -not $knownGroupIds.ContainsKey($_) } | Sort-Object)
$evidenceBackedPriorities = @()

if (-not [string]::IsNullOrWhiteSpace($GroupEvidenceIndexPath)) {
    $evidencePath = [IO.Path]::GetFullPath($GroupEvidenceIndexPath)
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
        throw "Group evidence index was not found: $evidencePath"
    }

    $evidenceIndex = Get-Content -LiteralPath $evidencePath -Raw -Encoding UTF8 | ConvertFrom-Json
    $locationGapsById = @{}
    foreach ($gap in $locationGaps) { $locationGapsById[[string]$gap.locationId] = $gap }
    $seenEvidencePairs = @{}

    foreach ($candidate in @($evidenceIndex.exactBuildCandidates)) {
        $locationId = [int]$candidate.Rowid
        $locationKey = [string]$locationId
        if (-not $locationGapsById.ContainsKey($locationKey)) { continue }

        $gap = $locationGapsById[$locationKey]
        if (-not $gap.missingDescription -and -not $gap.missingSources) { continue }
        $evidenceLabels = @($candidate.evidence | ForEach-Object { [string]$_ })

        foreach ($candidateGroupId in @($candidate.groupIds)) {
            if ($null -eq $candidateGroupId) { continue }
            $groupId = [int]$candidateGroupId
            if (@($gap.groupIds) -notcontains $groupId) { continue }

            $pairKey = '{0}:{1}' -f $locationId, $groupId
            if ($seenEvidencePairs.ContainsKey($pairKey)) { continue }
            $seenEvidencePairs[$pairKey] = $true

            $evidenceBackedPriorities += [pscustomobject]@{
                locationId = $locationId
                locationName = [string]$gap.name
                groupId = $groupId
                groupName = [string]$candidate.group
                groupUrl = [string]$candidate.groupUrl
                revisionId = [long]$candidate.revisionId
                evidence = $evidenceLabels
                strictInfoboxIdentity = @($evidenceLabels | Where-Object { $_ -eq 'Infobox group bases' }).Count -gt 0
                missingDescription = [bool]$gap.missingDescription
                missingSources = [bool]$gap.missingSources
                noPublicRenders = [bool]$gap.noPublicRenders
            }
        }
    }
}

$result = [ordered]@{
    schemaVersion = 1
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    source = [ordered]@{
        entityRoot = $root
        locations = $locationsPath
        groups = $groupsPath
    }
    totals = [ordered]@{
        locations = $locations.Count
        groups = $groups.Count
        groupLinkedLocations = $linkedLocations.Count
        groupLocationEdges = [int](@($linkedLocations | ForEach-Object { Get-PresentCount -Value $_.groups }) | Measure-Object -Sum).Sum
        missingDescriptions = @($linkedLocations | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.description) }).Count
        missingSources = @($linkedLocations | Where-Object { (Get-PresentCount -Value $_.sources) -eq 0 }).Count
        missingDescriptionAndSources = @($linkedLocations | Where-Object {
            [string]::IsNullOrWhiteSpace([string]$_.description) -and (Get-PresentCount -Value $_.sources) -eq 0
        }).Count
        noPublicRenders = @($linkedLocations | Where-Object { (Get-PresentCount -Value $_.renders) -eq 0 }).Count
        unknownRelationshipGroupIds = $unknownRelationshipGroupIds
        evidenceBackedPairs = $evidenceBackedPriorities.Count
        evidenceBackedLocations = @($evidenceBackedPriorities | Select-Object -ExpandProperty locationId -Unique).Count
        strictInfoboxIdentityPairs = @($evidenceBackedPriorities | Where-Object { $_.strictInfoboxIdentity }).Count
    }
    groups = @($groupStats.Values | Sort-Object `
        @{ Expression = 'missingBoth'; Descending = $true }, `
        @{ Expression = 'linkedLocations'; Descending = $true }, `
        @{ Expression = 'groupName'; Descending = $false })
    locations = @($locationGaps | Sort-Object `
        @{ Expression = 'missingDescriptionAndSources'; Descending = $true }, `
        @{ Expression = 'missingDescription'; Descending = $true }, `
        @{ Expression = 'noPublicRenders'; Descending = $true }, `
        @{ Expression = 'name'; Descending = $false })
    evidenceBackedPriorities = @($evidenceBackedPriorities | Sort-Object `
        @{ Expression = 'strictInfoboxIdentity'; Descending = $true }, `
        @{ Expression = 'missingDescription'; Descending = $true }, `
        @{ Expression = 'missingSources'; Descending = $true }, `
        @{ Expression = 'locationName'; Descending = $false })
}

$result | ConvertTo-Json -Depth 6
