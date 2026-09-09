Set-StrictMode -Version 2.0

function Get-ArchiveAdaptiveCapturePolicy {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Candidate
    )

    $categoryText = @($Candidate.archiveCategoryPath | ForEach-Object { [string]$_ }) -join ' > '
    $displayName = [string]$Candidate.archiveDisplayName
    $warp = [string]$Candidate.warp

    # The Archive's "Along the Axes" tree contains coordinate milestones, road
    # starts, and other regional/infrastructure views. Inside the known +/-100k
    # merged background square they can contain ordinary 2b2t terrain plus a
    # connected highway network. Outside it, a long strip may be the exhibit
    # itself and void remains the authoritative stopping signal.
    $isAxisCatalogEntry = $categoryText -match '(?i)\balong\s+the\s+axes\b'
    $identityText = "$displayName $warp"
    $isNamedExhibit = $identityText -match '(?i)\b(base|monument|pyramid|temple|town|city|castle|station|island|lodge|hotel|farm|inn|outpost|fort|bridge|spawn|build|ruins?)\b'
    $isLinearInfrastructure = $identityText -match '(?i)\b(road|highway|tunnel|canal|axis)\b'
    $hasCoordinates = $null -ne $Candidate.PSObject.Properties['x'] -and
        $null -ne $Candidate.PSObject.Properties['z']
    $insideMergedBackground = $hasCoordinates -and
        [Math]::Max([Math]::Abs([double]$Candidate.x), [Math]::Abs([double]$Candidate.z)) -le 100000
    if ($insideMergedBackground -and $isAxisCatalogEntry -and (-not $isNamedExhibit -or $isLinearInfrastructure)) {
        return [pscustomobject]@{
            name = 'archive-axis-region'
            runawayIterationThreshold = 5
            rationale = 'Archive catalog classifies this inside-background warp as an Along the Axes regional/infrastructure view.'
        }
    }

    return [pscustomobject]@{
        name = 'unbounded-build'
        runawayIterationThreshold = 0
        rationale = 'Named builds remain unbounded so genuinely large bases can close naturally.'
    }
}

function Get-ArchiveAdaptiveRunawayReason {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Line,
        [Parameter(Mandatory = $true)]
        [object]$Policy
    )

    $threshold = [int]$Policy.runawayIterationThreshold
    if ($threshold -le 0) { return '' }

    $match = [regex]::Match(
        $Line,
        'ATLAS_COVER adaptive-expand iteration=(\d+) bounds=([^\s]+) componentBounds=([^\s]+) componentBuild=(\d+) expand=(true|false),(true|false),(true|false),(true|false) newWaypoints=(\d+) targetChunks=(\d+)',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $match.Success) { return '' }

    $iteration = [int]$match.Groups[1].Value
    if ($iteration -lt $threshold) { return '' }

    $directions = @('west', 'east', 'north', 'south')
    $openDirections = @()
    for ($index = 0; $index -lt 4; $index++) {
        if ([bool]::Parse($match.Groups[5 + $index].Value)) {
            $openDirections += $directions[$index]
        }
    }
    if ($openDirections.Count -eq 0) { return '' }

    return ('policy={0} iteration={1} open={2} componentBuild={3} targetChunks={4}; {5}' -f
        [string]$Policy.name,
        $iteration,
        ($openDirections -join ','),
        [int]$match.Groups[4].Value,
        [int]$match.Groups[10].Value,
        [string]$Policy.rationale)
}
