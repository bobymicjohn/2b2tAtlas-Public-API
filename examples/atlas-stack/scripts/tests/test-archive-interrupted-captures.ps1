$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scripts = Split-Path -Parent $PSScriptRoot
. (Join-Path $scripts 'archive-json-io.ps1')
. (Join-Path $scripts 'archive-interrupted-captures.ps1')
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Get-NormalizedWarp([string]$Value) { return $Value.ToLowerInvariant() }
$fixture = Join-Path $env:TEMP ('atlas-interrupted-' + [guid]::NewGuid().ToString('N'))
$saves = Join-Path $fixture 'saves'
$recovery = Join-Path $fixture 'recovery'
New-Item -ItemType Directory -Path (Join-Path $saves 'archive-test\region') -Force | Out-Null
try {
    $region = Join-Path $saves 'archive-test\region\r.0.0.mca'
    [IO.File]::WriteAllBytes($region, (New-Object byte[] 8192))
    $saved = Save-InterruptedCaptureFiles -SavesRoot $saves -CaptureNames @('archive-test') -WarpName 'Test' -RecoveryRoot $recovery
    Assert (Test-Path -LiteralPath $region) 'Original was removed'
    $receipt = Read-AtlasJsonWithRetry (Join-Path $saved 'receipt.json')
    Assert ($receipt.completeWorld -eq $false -and $receipt.files.Count -eq 1) 'Partial marked complete or manifest missing'
    Assert ($receipt.files[0].sha256 -eq (Get-FileHash $region).Hash) 'Hash mismatch'
    $blocked = $false
    try { Save-InterruptedCaptureFiles -SavesRoot $saves -CaptureNames @('../outside') -WarpName 'Test' -RecoveryRoot $recovery }
    catch { $blocked = $true }
    Assert $blocked 'Traversal accepted'
    $held = [IO.File]::Open($region,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        $blocked = $false
        try { Save-InterruptedCaptureFiles -SavesRoot $saves -CaptureNames @('archive-test') -WarpName 'Test' -RecoveryRoot $recovery }
        catch { $blocked = $true }
        Assert $blocked 'Active writer accepted as a stable copy'
    } finally { $held.Dispose() }
    Assert (Test-Path $region) 'Failed preservation removed original'
    $queue = [pscustomobject]@{entries=@([pscustomobject]@{warp='Test';normalizedWarp='test'})}
    $state = @{test=[pscustomobject]@{status='retryable'}}
    $requests = Join-Path $fixture 'retry-warps.json'
    Write-AtlasJsonAtomically -Path $requests -Value @{schemaVersion=1;warps=@('Test')}
    Assert (@(Get-RequestedCollectorRetries $requests $queue $state).Count -eq 1) 'Retryable not scheduled'
    foreach ($status in @('captured','ready','needs-footprint-review','missing')) {
        $state.test.status=$status
        Assert (@(Get-RequestedCollectorRetries $requests $queue $state).Count -eq 0) "Request overrides $status"
    }
    $absentRequest = Join-Path $fixture 'no-request.json'
    $state.test = [pscustomobject]@{status='retryable';interruptionCount=1;automaticRetryAfterUtc=[datetimeoffset]::UtcNow.AddMinutes(-1).ToString('o')}
    Assert (@(Get-RequestedCollectorRetries $absentRequest $queue $state).Count -eq 1) 'Disconnected long capture stranded'
    $state.test.automaticRetryAfterUtc=[datetimeoffset]::UtcNow.AddMinutes(5).ToString('o')
    Assert (@(Get-RequestedCollectorRetries $absentRequest $queue $state).Count -eq 0) 'Retry ignored backoff'
    $state.test.automaticRetryAfterUtc=[datetimeoffset]::UtcNow.AddMinutes(-1).ToString('o')
    $state.test.interruptionCount=4
    Assert (@(Get-RequestedCollectorRetries $absentRequest $queue $state).Count -eq 0) 'Unbounded automatic retries'
    # A saved operator request authorizes the failure it was written for, not
    # every later disconnect forever. It must not bypass backoff or retry caps.
    $oldRequestUtc = [datetimeoffset]::UtcNow.AddHours(-2)
    Write-AtlasJsonAtomically -Path $requests -Value @{schemaVersion=1;warps=@('Test');updatedUtc=$oldRequestUtc.ToString('o')}
    $state.test | Add-Member failedUtc ([datetimeoffset]::UtcNow.AddHours(-1).ToString('o')) -Force
    Assert (@(Get-RequestedCollectorRetries $requests $queue $state).Count -eq 0) 'Stale manual request bypassed automatic retry cap'
    $state.test.interruptionCount=1
    $state.test.automaticRetryAfterUtc=[datetimeoffset]::UtcNow.AddMinutes(5).ToString('o')
    Assert (@(Get-RequestedCollectorRetries $requests $queue $state).Count -eq 0) 'Stale manual request bypassed automatic backoff'
    $state.test.automaticRetryAfterUtc=[datetimeoffset]::UtcNow.AddMinutes(-1).ToString('o')
    Assert (@(Get-RequestedCollectorRetries $requests $queue $state).Count -eq 1) 'Stale request suppressed a valid bounded automatic retry'
    $state.test.interruptionCount=4
    Write-AtlasJsonAtomically -Path $requests -Value @{schemaVersion=1;warps=@('Test');updatedUtc=[datetimeoffset]::UtcNow.ToString('o')}
    Assert (@(Get-RequestedCollectorRetries $requests $queue $state).Count -eq 1) 'Fresh operator request could not retry an exhausted failure'
    $state.test.failedUtc=[datetimeoffset]::UtcNow.AddSeconds(1).ToString('o')
    Assert (@(Get-RequestedCollectorRetries $requests $queue $state).Count -eq 0) 'Fresh request authorized more than one subsequent failure'
    $tokens=$null; $parseErrors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $scripts 'invoke-archive-collector.ps1'),[ref]$tokens,[ref]$parseErrors)
    Assert ($parseErrors.Count -eq 0) 'Collector parse errors'
    $cleanup = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Remove-TransientWdlCapture'},$true)
    Invoke-Expression $cleanup.Extent.Text
    $blocked=$false
    try { Remove-TransientWdlCapture 'archive-test' } catch { $blocked=$true }
    Assert $blocked 'Cleanup permitted without durable-copy authorization'
    'PASS: interrupted data retained, verified private copy, active-writer/traversal rejection, targeted retries, cleanup gate.'
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    $tempPrefix=[IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\'
    if (-not $resolved.StartsWith($tempPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped temp root' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
