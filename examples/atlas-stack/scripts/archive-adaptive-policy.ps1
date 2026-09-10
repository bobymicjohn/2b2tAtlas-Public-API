Set-StrictMode -Version 2.0

function Get-ArchiveAdaptiveCapturePolicy {
    param([Parameter(Mandatory = $true)][object]$Candidate)
    # Catalog/location coordinates can describe a different dated exhibit. They
    # must never select a capture center or exempt a warp from a size review.
    $parts = foreach ($name in @('archiveCategoryPath', 'archiveDisplayName', 'warp')) {
        if ($null -ne $Candidate.PSObject.Properties[$name]) { @($Candidate.$name) -join ' ' }
    }
    $identity = ($parts -join ' ') -replace '_', ' '
    $name = 'individual-build'
    $area = 65536L; $span = 8192L; $iterations = 12
    $reason = 'Large or continuously expanding individual exhibit; preserve the save for boundary review.'
    if ($identity -match '(?i)\blodge\b') {
        $name = 'individual-lodge'; $area = 16384L; $span = 4096L; $iterations = 10
    } elseif ($identity -match '(?i)\b(along the axes|regional|region save|world download|spawn download)\b') {
        $name = 'archive-region'; $area = 262144L; $span = 16384L; $iterations = 16
        if ($identity -match '(?i)\balong the axes\b') { $iterations = 5 }
        $reason = 'Regional source requires explicit extent review before it can be published as an exhibit.'
    } elseif ($identity -match '(?i)\b(highway|road|tunnel|canal|bridge)\b') {
        $name = 'linear-build'; $area = 131072L; $span = 32768L; $iterations = 24
        $reason = 'Linear exhibit exceeds its automatic survey budget; review its endpoints without clipping the save.'
    }
    [pscustomobject]@{
        name = $name
        maximumSurveyChunks = $area
        maximumSpanBlocks = $span
        runawayIterationThreshold = $iterations
        rationale = $reason
    }
}

function Get-ArchiveAdaptiveRunawayReason {
    param([Parameter(Mandatory = $true)][string]$Line,
          [Parameter(Mandatory = $true)][object]$Policy)
    # Expansion bounds are inclusive chunk coordinates; completion bounds are
    # exclusive block coordinates. Keep the conversions separate.
    $expand = [regex]::Match($Line, 'ATLAS_COVER adaptive-expand iteration=(\d+) bounds=(-?\d+),(-?\d+)\.\.(-?\d+),(-?\d+).*?targetChunks=(\d+)')
    $complete = [regex]::Match($Line, 'ATLAS_COVER adaptive-complete .*?bounds=(-?\d+),(-?\d+)\.\.(-?\d+),(-?\d+).*? target=(\d+)')
    if ($expand.Success) {
        $area = [long]$expand.Groups[6].Value
        $span = 16L * [Math]::Max(([long]$expand.Groups[4].Value - [long]$expand.Groups[2].Value + 1),
                                ([long]$expand.Groups[5].Value - [long]$expand.Groups[3].Value + 1))
        $iteration = [int]$expand.Groups[1].Value
        if ($area -gt $Policy.maximumSurveyChunks -or $span -gt $Policy.maximumSpanBlocks -or
            $iteration -ge $Policy.runawayIterationThreshold) {
            return "policy=$($Policy.name) iteration=$iteration targetChunks=$area spanBlocks=$span; $($Policy.rationale)"
        }
    } elseif ($complete.Success) {
        $area = [long]$complete.Groups[5].Value
        $span = [Math]::Max(([long]$complete.Groups[3].Value - [long]$complete.Groups[1].Value),
                          ([long]$complete.Groups[4].Value - [long]$complete.Groups[2].Value))
        if ($area -gt $Policy.maximumSurveyChunks -or $span -gt $Policy.maximumSpanBlocks) {
            return "policy=$($Policy.name) completed targetChunks=$area spanBlocks=$span; oversized footprint requires review."
        }
        $component = [regex]::Match($Line, ' componentBuild=(\d+)')
        $orphan = [regex]::Match($Line, ' orphanBuild=(\d+)')
        if ($component.Success -and $orphan.Success -and $area -ge 16384 -and
            [long]$orphan.Groups[1].Value -ge 1024 -and
            [long]$orphan.Groups[1].Value -ge [long]$component.Groups[1].Value * 0.5) {
            return "policy=$($Policy.name) componentBuild=$($component.Groups[1].Value) orphanBuild=$($orphan.Groups[1].Value); large rectangle contains substantial construction outside the selected component."
        }
    }
    return ''
}

function Get-ArchiveAdaptiveCompletedReviewReason {
    param([Parameter(Mandatory = $true)][object]$Candidate,
          [Parameter(Mandatory = $true)][object]$Adaptive)
    foreach ($field in @('bounds','targetChunks','primaryComponentBuildChunks','orphanBuildChunksInsideBounds')) {
        if ($null -eq $Adaptive.PSObject.Properties[$field]) { return "Missing footprint review evidence: $field" }
    }
    $b = @($Adaptive.bounds)
    if ($b.Count -ne 4 -or [long]$b[2] -le [long]$b[0] -or [long]$b[3] -le [long]$b[1]) { return 'Invalid footprint bounds' }
    $line = 'ATLAS_COVER adaptive-complete confidence=high bounds={0},{1}..{2},{3} surveyBounds={0},{1}..{2},{3} target={4} componentBuild={5} orphanBuild={6}' -f $b[0],$b[1],$b[2],$b[3],$Adaptive.targetChunks,$Adaptive.primaryComponentBuildChunks,$Adaptive.orphanBuildChunksInsideBounds
    Get-ArchiveAdaptiveRunawayReason $line (Get-ArchiveAdaptiveCapturePolicy $Candidate)
}
