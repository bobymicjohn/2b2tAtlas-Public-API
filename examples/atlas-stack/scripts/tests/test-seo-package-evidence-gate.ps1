[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildScript = Join-Path $repoRoot 'scripts\build-namecheap-package.ps1'
$wrapperScript = Join-Path $repoRoot 'scripts\invoke-atlas-seo-package.ps1'
$missingEvidence = Join-Path $env:TEMP ('atlas-missing-group-evidence-' + [Guid]::NewGuid().ToString('N') + '.json')
$outputRoot = Join-Path $env:TEMP ('atlas-missing-evidence-output-' + [Guid]::NewGuid().ToString('N'))
$stateRoot = Join-Path $env:TEMP ('atlas-missing-evidence-state-' + [Guid]::NewGuid().ToString('N'))

function Assert-ThrowsLike {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Label
    )

    try {
        & $Action
    } catch {
        if ($_.Exception.Message -like $Pattern) { return }
        throw "$Label threw an unexpected error: $($_.Exception.Message)"
    }
    throw "$Label did not fail closed."
}

Assert-ThrowsLike -Label 'Direct package builder' -Pattern 'Group evidence index was not found:*' -Action {
    & $buildScript -ApiBaseUrl 'http://127.0.0.1:5297' `
        -OutputDirectory $outputRoot -GroupEvidenceIndexPath $missingEvidence
}

Assert-ThrowsLike -Label 'Scheduled package wrapper' `
    -Pattern 'The revision-pinned group evidence index is required*' -Action {
    & $wrapperScript -DryRun -SkipNotification -StateRoot $stateRoot -GroupEvidenceIndexPath $missingEvidence
}

if (Test-Path -LiteralPath $outputRoot) {
    throw 'Direct package builder created output before validating the evidence index.'
}
if (Test-Path -LiteralPath $stateRoot) {
    throw 'Scheduled package wrapper created state before validating the evidence index.'
}

Write-Output '{"tests":4,"failed":[]}'
