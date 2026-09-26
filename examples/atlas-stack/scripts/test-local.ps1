[CmdletBinding()]
param(
    [string]$Python = 'python',
    [string]$Node = 'node',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root ('.artifacts/local-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$shell = Join-Path $PSHOME 'pwsh.exe'
if (-not (Test-Path -LiteralPath $shell)) { $shell = Join-Path $PSHOME 'powershell.exe' }
$failures = New-Object 'System.Collections.Generic.List[string]'

function Invoke-LocalCheck([string]$Command, [string[]]$Arguments, [string]$Name) {
    Get-Command $Command -ErrorAction Stop | Out-Null
    Write-Host "Running $Name"
    $log = Join-Path $OutputDirectory ($Name + '.log')
    $previousPreference = $ErrorActionPreference
    try {
        # unittest writes its summary to stderr even when every test passes.
        $ErrorActionPreference = 'Continue'
        $global:LASTEXITCODE = 0
        & $Command @Arguments 2>&1 | ForEach-Object { $_.ToString() } | Set-Content -LiteralPath $log
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($code -ne 0) {
        $failures.Add($Name)
        Get-Content -LiteralPath $log -Tail 20 | Write-Host
    }
}

Push-Location $root
try {
    Invoke-LocalCheck 'dotnet' @('test', '2b2tAtlas.sln', '-c', 'Release', '--nologo') 'dotnet'
    Invoke-LocalCheck $Python @('scripts/test-atlas-storage-completion.py') 'test-atlas-storage-completion.py'
    Invoke-LocalCheck $Python @('scripts/test-media-research.py') 'test-media-research.py'
    Invoke-LocalCheck $Python @('scripts/test-nocom-contrast.py') 'test-nocom-contrast.py'
    Invoke-LocalCheck $Python @('scripts/test-nocom-production.py') 'test-nocom-production.py'
    Invoke-LocalCheck $Python @('scripts/test_archive_capture_recovery.py') 'test_archive_capture_recovery.py'
    Invoke-LocalCheck $Python @('scripts/test_archive_capture_resume.py') 'test_archive_capture_resume.py'
    Invoke-LocalCheck $Python @('scripts/tests/test_audit_2b2t_wiki_groups.py') 'test_audit_2b2t_wiki_groups.py'
    Invoke-LocalCheck $Python @('scripts/tests/test_compare_wdl_payloads.py') 'test_compare_wdl_payloads.py'
    Invoke-LocalCheck $Python @('scripts/tests/test_review_wdl_footprint.py') 'test_review_wdl_footprint.py'
    Invoke-LocalCheck $Python @('scripts/tests/test_reviewed_video_descriptions.py') 'test_reviewed_video_descriptions.py'
    Invoke-LocalCheck $Python @('scripts/tests/test_youtube_research_catalog.py') 'test_youtube_research_catalog.py'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-capture-checkpoint.ps1') 'test-archive-capture-checkpoint.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-capture-recovery.ps1') 'test-archive-capture-recovery.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-collector-safety.ps1') 'test-archive-collector-safety.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-fast-lane-refill.ps1') 'test-archive-fast-lane-refill.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-fast-lane-routing.ps1') 'test-archive-fast-lane-routing.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-footprint-policy.ps1') 'test-archive-footprint-policy.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-inbox-batch.ps1') 'test-archive-inbox-batch.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-interrupted-captures.ps1') 'test-archive-interrupted-captures.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-local-handoff.ps1') 'test-archive-local-handoff.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-observed-neighbors.ps1') 'test-archive-observed-neighbors.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-archive-survey-handoff.ps1') 'test-archive-survey-handoff.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-atlas-bluemap-camera.ps1') 'test-atlas-bluemap-camera.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-audit-seo-enrichment-gaps.ps1') 'test-audit-seo-enrichment-gaps.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-collector-oom-recovery.ps1') 'test-collector-oom-recovery.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-collector-resume-keepalive.ps1') 'test-collector-resume-keepalive.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-compact-interrupted-captures.ps1') 'test-compact-interrupted-captures.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-get-archive-recovery-status.ps1') 'test-get-archive-recovery-status.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-historical-media-scratch.ps1') 'test-historical-media-scratch.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/tests/test-seo-package-evidence-gate.ps1') 'test-seo-package-evidence-gate.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/test-atlas-bluemap-coordination.ps1') 'test-atlas-bluemap-coordination.ps1'
    Invoke-LocalCheck $shell @('-NoProfile', '-File', 'scripts/test-bluemap-legacy-version-binding.ps1') 'test-bluemap-legacy-version-binding.ps1'
    Invoke-LocalCheck $Node @('scripts/test-map-primary-layers.mjs') 'test-map-primary-layers.mjs'
    Invoke-LocalCheck $Node @('scripts/test-highway-edit-coordinates.mjs') 'test-highway-edit-coordinates.mjs'
    Invoke-LocalCheck $Node @('scripts/test-bluemap-controls.mjs') 'test-bluemap-controls.mjs'
} finally {
    Pop-Location
}
if ($failures.Count -gt 0) {
    throw "Failed checks: $($failures -join ', '). Logs: $OutputDirectory"
}
Write-Output "All local checks passed. Logs: $OutputDirectory"
