$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$scripts=Split-Path -Parent $PSScriptRoot
. (Join-Path $scripts 'archive-capture-recovery.ps1')
$StatePath=Join-Path $env:TEMP ('atlas-oom-test-'+[guid]::NewGuid().ToString('N')+'\state.json')
$gameRoot='C:\fixture\collector\game'
$script:process=[pscustomobject]@{HasExited=$false}
$script:downloadActive=$true
$script:captureStartIndex=0
$script:lines=New-Object 'Collections.Generic.List[string]'
$script:lines.Add('java.lang.OutOfMemoryError: Java heap space')
$script:polls=0
function Send-CollectorCommand($Command) { }
function Pump-CollectorOutput { $script:polls++ }
function Start-Sleep { }
function Get-CimInstance {
    # A live game after OOM must still be held. Once it exits, its launcher
    # and another worker's game must not prevent this capture's disk recovery.
    if($script:polls -lt 3){[pscustomobject]@{CommandLine="net.fabricmc.loader.impl.launch.knot.KnotClient --gameDir $gameRoot"}}
    [pscustomobject]@{CommandLine="java -Dhmc.gamedir=$gameRoot -jar headlessmc.jar"}
    [pscustomobject]@{CommandLine='net.fabricmc.loader.impl.launch.knot.KnotClient --gameDir C:\other\game'}
}
Wait-InterruptedCaptureFlush
if($script:polls -lt 3 -or !$script:downloadActive){throw 'OOM handling accepted a live writer or marked the partial save complete'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $scripts 'invoke-archive-collector.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw $errors[0]}
$wait=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Wait-CollectorMatch'},$true)
Invoke-Expression $wait.Extent.Text
$script:polls=0
$held=$false
try { Wait-CollectorMatch @('never') 2 0 } catch { $held=$_.Exception.Message -like '*exhausted its heap*' }
if(!$held){throw 'Fatal OOM was not surfaced promptly'}
'PASS: OOM holds live writers, recovers after game exit with a surviving launcher, isolates other workers, and never marks a partial save complete.'
