[CmdletBinding()]
param(
    [string]$DatabasePath = 'C:\AtlasExample\Api\data\atlas.db',
    [string]$RuntimeIndexPath = 'C:\AtlasExample\Api\data\enrichment\2b2t-wiki-group-audit.json',
    [string]$ReportRoot = 'C:\AtlasExample\Api\data\enrichment\history',
    [ValidateRange(2, 90)]
    [int]$RetentionCount = 30,
    [ValidateRange(1, 10000)]
    [int]$MinimumArticleCount = 100,
    [ValidateRange(0.0, 1.0)]
    [double]$MinimumStrongAgreementRate = 0.98
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$auditScript = Join-Path $PSScriptRoot 'audit-2b2t-wiki-groups.py'
$database = [IO.Path]::GetFullPath($DatabasePath)
$runtimeIndex = [IO.Path]::GetFullPath($RuntimeIndexPath)
$historyRoot = [IO.Path]::GetFullPath($ReportRoot)
if (-not (Test-Path -LiteralPath $database -PathType Leaf)) { throw "Atlas database not found: $database" }
if (-not (Test-Path -LiteralPath $auditScript -PathType Leaf)) { throw "Audit script not found: $auditScript" }

$python = (Get-Command python -ErrorAction Stop).Source
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$output = Join-Path $historyRoot $stamp
New-Item -ItemType Directory -Path $historyRoot -Force | Out-Null
$runtimeDirectory = Split-Path -Parent $runtimeIndex
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
$candidateRuntime = Join-Path $runtimeDirectory ('.group-evidence-candidate-{0}.json' -f $stamp)
$replacementBackup = Join-Path $runtimeDirectory ('.group-evidence-replaced-{0}.json' -f $stamp)

try {
    & $python $auditScript `
        --atlas-db $database `
        --output-dir $output `
        --runtime-index $candidateRuntime
    if ($LASTEXITCODE -ne 0) { throw "Group evidence audit exited with code $LASTEXITCODE." }

    $runtime = Get-Content -LiteralPath $candidateRuntime -Raw | ConvertFrom-Json
    $articleCount = @($runtime.articles).Count
    if ($runtime.schemaVersion -ne 1 -or $articleCount -lt $MinimumArticleCount) {
        throw "Generated group evidence index failed schema/coverage validation ($articleCount articles)."
    }
    $benchmark = $runtime.reviewedRegressionBenchmark
    if ($null -eq $benchmark -or [int]$benchmark.reviewedLinks -lt 1) {
        throw 'Generated group evidence index has no reviewed-corpus regression benchmark.'
    }
    $strongAgreement = [double]$benchmark.strongCandidateAgreementRate
    if ($strongAgreement -lt $MinimumStrongAgreementRate) {
        throw "Strong attribution agreement $strongAgreement is below the required $MinimumStrongAgreementRate."
    }

    # Promote only after all checks pass. File.Replace keeps readers on either the old or new complete file.
    if (Test-Path -LiteralPath $runtimeIndex -PathType Leaf) {
        # Windows PowerShell 5.1/.NET Framework rejects a null backup path for this overload.
        [IO.File]::Replace($candidateRuntime, $runtimeIndex, $replacementBackup, $true)
        Remove-Item -LiteralPath $replacementBackup -Force
    } else {
        [IO.File]::Move($candidateRuntime, $runtimeIndex)
    }
} finally {
    if (Test-Path -LiteralPath $candidateRuntime -PathType Leaf) {
        Remove-Item -LiteralPath $candidateRuntime -Force
    }
    if (Test-Path -LiteralPath $replacementBackup -PathType Leaf) {
        Remove-Item -LiteralPath $replacementBackup -Force
    }
}

$reports = @(Get-ChildItem -LiteralPath $historyRoot -Directory | Sort-Object Name -Descending)
foreach ($old in $reports | Select-Object -Skip $RetentionCount) {
    Remove-Item -LiteralPath $old.FullName -Recurse -Force
}

[pscustomobject]@{
    RuntimeIndex = $runtimeIndex
    ReportDirectory = $output
    GeneratedUtc = $runtime.generatedUtc
    Articles = @($runtime.articles).Count
    ExactCandidates = $runtime.stats.exactBuildCandidates
    CandidateAgreementRate = $runtime.reviewedRegressionBenchmark.candidateAgreementRate
    StrongCandidateAgreementRate = $runtime.reviewedRegressionBenchmark.strongCandidateAgreementRate
    ReviewedCoverageRate = $runtime.reviewedRegressionBenchmark.reviewedCoverageRate
}
