$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../archive-collector-safety.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('atlas-safety-' + [guid]::NewGuid().ToString('N'))
$signal = Join-Path $testRoot 'pause'
function Check([string]$Name, [scriptblock]$Action, [bool]$ShouldThrow) {
    $threw = $false
    try { & $Action } catch { $threw = $true; if ($_.Exception.Message -notlike 'Collector safety hold:*') { throw } }
    if ($threw -ne $ShouldThrow) { throw "FAILED $Name" }
    Write-Output "PASS $Name"
}
try {
    Check 'healthy storage allows collection' { Assert-ArchiveCollectorSafety $signal @('C:\work','D:\output') 100 { 101 } } $false
    Check 'full output drive stops even when saves drive has space' {
        Assert-ArchiveCollectorSafety $signal @('C:\work','D:\output') 100 { param($p) if ($p.StartsWith('C:')) { 400 } else { 99 } }
    } $true
    Check 'free space recovery does not silently release pause' { Assert-ArchiveCollectorSafety $signal @('C:\work') 100 { 400 } } $true
    Remove-Item -LiteralPath $signal
    Check 'unreadable working drive fails closed' { Assert-ArchiveCollectorSafety $signal @('D:\output') 100 { throw 'disk offline' } } $true
    Remove-Item -LiteralPath $signal
    Check 'low saves drive stops' { Assert-ArchiveCollectorSafety $signal @('C:\work') 100 { 99 } } $true
    # Exercise the real wait loop: safety cannot depend on new telemetry, and
    # a held fleet must still be allowed to finish its normal save flush.
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../invoke-archive-collector.ps1'),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $fn=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-CollectorMatch'},$true)
    Invoke-Expression $fn.Extent.Text
    function Pump-CollectorOutput { }
    $PauseSignalPath=$signal;$savesRoot='C:\work';$CapturedRoot='D:\output';$MinimumFreeGiB=100
    $script:downloadActive=$true;$script:safetyStopping=$false;$script:nextSafetyCheck=[datetime]::MinValue
    $script:lines=New-Object 'Collections.Generic.List[string]'
    Check 'quiet active capture notices operator pause' { Wait-CollectorMatch @('never') 1 0 } $true
    $script:safetyStopping=$true;$script:lines.Add('Downloaded test')
    Check 'flush can complete while paused' { Wait-CollectorMatch @('Downloaded test') 1 0 | Out-Null } $false
} finally {
    if (Test-Path -LiteralPath $signal) { Remove-Item -LiteralPath $signal }
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot }
}
