$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path (Split-Path -Parent $PSScriptRoot) 'invoke-archive-collector.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Collector parse errors'}
$fn=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-CollectorMatch'},$true)
Invoke-Expression $fn.Extent.Text
$script:downloadActive=$false;$script:safetyStopping=$false;$script:nextSafetyCheck=[datetime]::MaxValue
$script:lines=New-Object 'Collections.Generic.List[string]'
$script:process=[pscustomobject]@{HasExited=$false}
$script:commands=New-Object 'Collections.Generic.List[string]'
$script:polls=0;$script:mode='success'
function Send-CollectorCommand($Command){$script:commands.Add($Command)}
function Pump-CollectorOutput {
    $script:polls++
    if($script:mode -eq 'success' -and $script:polls -eq 3){$script:lines.Add('ATLAS_COVER resume-start planningOnly=false restored=588782')}
    if($script:mode -eq 'disconnect' -and $script:polls -eq 1){$script:lines.Add('Client disconnected with reason: kicked')}
}
$result=Wait-CollectorMatch @('ATLAS_COVER resume-start') 2 0 -KeepAliveCommand 'msg /gamemode spectator' -KeepAliveIntervalSeconds 0.05
if($script:commands.Count -ne 1 -or $script:commands[0] -ne 'msg /gamemode spectator' -or $result.Line -notmatch 'restored=588782'){throw 'Keepalive disturbed completion or did not run'}
$script:lines.Clear();$script:commands.Clear();$script:polls=0;$script:mode='disconnect'
$held=$false
try{Wait-CollectorMatch @('ATLAS_COVER resume-start') 2 0 -KeepAliveCommand 'msg /gamemode spectator'}catch{$held=$_.Exception.Message -like 'Archive disconnected*'}
if(-not $held -or $script:commands.Count){throw 'Disconnect not surfaced before sending commands'}
$script:lines.Clear();$script:polls=0;$script:mode='timeout'
$watch=[Diagnostics.Stopwatch]::StartNew();$held=$false
try{Wait-CollectorMatch @('ATLAS_COVER resume-start') 1 0 -KeepAliveCommand 'msg /gamemode spectator' -KeepAliveIntervalSeconds 0.05}catch{$held=$_.Exception.Message -like 'Timed out*'}
if(-not $held -or $watch.Elapsed.TotalSeconds -gt 3 -or $script:commands.Count -eq 0){throw 'Keepalive renewed the timeout'}
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'archive-json-io.ps1')
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'archive-survey-handoff.ps1')
$fixture=Join-Path $env:TEMP ('atlas-resume-wait-'+[guid]::NewGuid().ToString('N'))
try {
    $attempt='20260101-000000-'+('a'*32)
    $dir=Join-Path $fixture $attempt
    New-Item -ItemType Directory $dir -Force|Out-Null
    [IO.File]::WriteAllText((Join-Path $dir 'partial-wdl.zip'),'hash fixture; reader not reached')
    $hint=@{core=512;expansion=256;radius=8;step=8}
    Write-AtlasJsonAtomically -Path (Join-Path $dir 'survey-hint.json') -Value $hint
    $Server='archive.example';$warp='Fixture_2024-01-01';$adaptiveLiveDimensionId='minecraft:overworld'
    $adaptiveWarpX=0;$adaptiveWarpZ=0;$AdaptiveMaxRadiusBlocks=0
    $AdaptiveCoreRadiusBlocks=512;$AdaptiveExpansionBlocks=256;$AdaptiveTerrainRadiusChunks=8;$AdaptiveStepChunks=8
    $gameRoot=Join-Path $fixture 'game'
    $receipt=@{schemaVersion=3;server=$Server;warp=$warp;dimension=$adaptiveLiveDimensionId;x=0;z=0;createdUtc=[datetimeoffset]::UtcNow.ToString('o');directory=$attempt;
        hintSha256=(Get-FileHash (Join-Path $dir 'survey-hint.json')).Hash;zipSha256=(Get-FileHash (Join-Path $dir 'partial-wdl.zip')).Hash}
    Write-AtlasJsonAtomically -Path (Join-Path $fixture 'latest.json') -Value $receipt
    function Get-SurveyHandoffDirectory($ArchiveServer,$WarpName){$fixture}
    function Save-JsonAtomically($Value,$Path){Write-AtlasJsonAtomically -Path $Path -Value $Value}
    $script:cancelCalls=0;$script:failure='Archive disconnected while waiting for a result: kicked'
    function Invoke-CollectorCommand($Command,$Patterns,$TimeoutSeconds,$KeepAliveCommand) {
        if($Command -eq 'msg /atlascover resume') {
            if($KeepAliveCommand -ne 'msg /gamemode spectator' -or $TimeoutSeconds -ne 900){throw 'Resume keepalive contract broken'}
            throw $script:failure
        }
        $script:cancelCalls++;throw 'Cancellation timeout'
    }
    $held=$false
    try{Restore-SurveyHandoff $warp 'archive-test'}catch{$held=$_.Exception.Message -like 'Archive disconnected during an active WDL resume:*'}
    if(-not $held -or $script:cancelCalls){throw 'Network error was hidden or reclassified as a validation hold'}
    $script:failure='Bad saved NBT';$held=$false
    try{Restore-SurveyHandoff $warp 'archive-test'}catch{$held=$_.Exception.Message -eq 'Resume checkpoint held: Bad saved NBT'}
    if(-not $held -or $script:cancelCalls -ne 1){throw 'Cancellation replaced the original validation error'}
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
'PASS: keepalive preserves landing, completion matching and deadline; disconnect aborts promptly.'
