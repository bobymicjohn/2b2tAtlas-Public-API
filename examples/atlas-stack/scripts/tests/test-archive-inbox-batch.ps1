$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root = Join-Path $repo ('build\inbox-batch-test-' + [guid]::NewGuid().ToString('N'))
$ready = Join-Path $root 'ready'
$intake = Join-Path $root 'intake'
New-Item -ItemType Directory -Path $ready,$intake -Force | Out-Null
$config = Join-Path $root 'worker.json'
@{ intakeRoot=$intake; apiKeyEnvironment='ATLAS_BATCH_FIXTURE_KEY' } | ConvertTo-Json | Set-Content $config
1..200 | ForEach-Object { [IO.File]::WriteAllText((Join-Path $ready "old-$_.zip"), 'accepted history') }
$new = Join-Path $ready 'new.zip'
[IO.File]::WriteAllText($new, 'new capture')
$global:atlasBatchHashed = New-Object Collections.Generic.List[string]
function Get-FileHash {
    param([string]$LiteralPath,[string]$Algorithm)
    $global:atlasBatchHashed.Add($LiteralPath)
    if ([IO.Path]::GetFileName($LiteralPath) -like 'old-*') { throw 'Accepted history was rehashed.' }
    Microsoft.PowerShell.Utility\Get-FileHash -LiteralPath $LiteralPath -Algorithm $Algorithm
}
$argsImport = @{ReadyRoot=$ready; WorkerConfigPath=$config; StatePath=(Join-Path $root 'state.json');
    MinimumStableSeconds=0; DelayBetweenJobsSeconds=0; DryRun=$true}
$result = @(& (Join-Path $repo 'scripts\import-archive-inbox.ps1') @argsImport -ReadyFiles @($new))
if ($global:atlasBatchHashed.Count -ne 1 -or $global:atlasBatchHashed[0] -ne $new -or $result.Count -ne 1) {
    throw 'Batch import did not limit hashing to exactly one new capture.'
}
$global:atlasBatchHashed.Clear()
& (Join-Path $repo 'scripts\import-archive-inbox.ps1') @argsImport -ReadyFiles @() | Out-Null
if ($global:atlasBatchHashed.Count -ne 0) { throw 'An empty batch fell back to a history scan.' }
$rejected = $false
try { & (Join-Path $repo 'scripts\import-archive-inbox.ps1') @argsImport -ReadyFiles @((Join-Path $root 'outside.zip')) | Out-Null }
catch { $rejected = $_.Exception.Message -like '*escaped*' }
if (-not $rejected) { throw 'An escaped batch path was accepted.' }
'PASS: 200 historical files untouched; one new file hashed; empty batch stays empty; escaped paths rejected.'
