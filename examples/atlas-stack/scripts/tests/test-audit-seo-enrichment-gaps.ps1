[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $PSScriptRoot
$auditScript = Join-Path $scriptRoot 'audit-seo-enrichment-gaps.ps1'
$fixtureRoot = Join-Path $PSScriptRoot 'fixtures\seo-enrichment-entities'
$evidenceFixture = Join-Path $fixtureRoot 'group-evidence.json'

$json = & $auditScript -EntityRoot $fixtureRoot -GroupEvidenceIndexPath $evidenceFixture
$result = $json | ConvertFrom-Json
$totals = $result.totals

$expected = [ordered]@{
    locations = 3
    groups = 2
    groupLinkedLocations = 2
    groupLocationEdges = 3
    missingDescriptions = 1
    missingSources = 1
    missingDescriptionAndSources = 1
    noPublicRenders = 1
}

foreach ($entry in $expected.GetEnumerator()) {
    if ([int]$totals.($entry.Key) -ne [int]$entry.Value) {
        throw "Expected $($entry.Key)=$($entry.Value), got $($totals.($entry.Key))."
    }
}

if (@($totals.unknownRelationshipGroupIds).Count -ne 0) {
    throw 'Fixture unexpectedly contains an unknown relationship group ID.'
}

$buildersOne = @($result.groups | Where-Object { $_.groupId -eq 1 })
if ($buildersOne.Count -ne 1 -or $buildersOne[0].linkedLocations -ne 2 -or $buildersOne[0].missingBoth -ne 1) {
    throw 'Per-group gap aggregation did not match the fixture.'
}

$thinBuild = @($result.locations | Where-Object { $_.locationId -eq 1 })
if ($thinBuild.Count -ne 1 -or
    -not $thinBuild[0].missingDescriptionAndSources -or
    -not $thinBuild[0].noPublicRenders -or
    $thinBuild[0].archiveWarpCount -ne 0 -or
    @($thinBuild[0].groupIds).Count -ne 1) {
    throw 'Per-location gap output did not match the fixture.'
}

$priority = @($result.evidenceBackedPriorities)
if ($priority.Count -ne 1 -or
    $totals.evidenceBackedPairs -ne 1 -or
    $totals.evidenceBackedLocations -ne 1 -or
    $totals.strictInfoboxIdentityPairs -ne 1 -or
    -not $priority[0].strictInfoboxIdentity -or
    $priority[0].revisionId -ne 42) {
    throw 'Evidence-backed priority output did not match the fixture.'
}

'SEO enrichment gap audit fixture passed.'
