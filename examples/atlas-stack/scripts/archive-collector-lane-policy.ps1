Set-StrictMode -Version 2.0

function Get-ArchiveCollectorLanePolicy([string]$RunRoot) {
    # Kept with the run so watchdog/default command lines cannot undo an
    # operator's drain mode. Supervisors apply changes on their next start.
    $path = Join-Path $RunRoot 'collector-lane-policy.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return [pscustomobject]@{ mode = 'balanced'; allLong = $false }
    }
    $policy = Read-AtlasJsonWithRetry -Path $path
    if ($null -eq $policy.PSObject.Properties['mode'] -or $policy.mode -notin @('balanced', 'all-long')) {
        throw "Invalid collector lane policy in $path; expected balanced or all-long."
    }
    return [pscustomobject]@{ mode = [string]$policy.mode; allLong = $policy.mode -eq 'all-long' }
}
