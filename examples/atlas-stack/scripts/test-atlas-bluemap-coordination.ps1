$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Load only pure helper declarations, never the production coordinator loop.
foreach ($file in @('start-atlas-bluemap-coordinator.ps1','invoke-atlas-bluemap-render.ps1')) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw "Parse failure in $file" }
    foreach ($fn in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)) {
        if ($fn.Name -in @('Next-Job','Test-RetryDue','Read-State','Write-State','Test-Complete','Write-AtomicJson','Get-ManifestUsage','Enter-PublicationLock','Reserve-Output','Remove-OwnedDockerResource')) {
            . ([scriptblock]::Create($fn.Extent.Text))
        }
    }
}
function Assert([bool]$Condition,[string]$Message) { if(-not $Condition){throw $Message} }
. (Join-Path $PSScriptRoot 'atlas-bluemap-resources.ps1')
Assert (Test-BlueMapStageCapacity 24GB 8GB 48GB 11GB 50GB) 'Two relighters plus renderer should fit'
Assert (-not (Test-BlueMapStageCapacity 24GB 12GB 48GB 11GB 50GB)) 'Three relighters exceeded stage budget'
Assert (-not (Test-BlueMapStageCapacity 24GB 8GB 48GB 14GB 50GB)) 'Docker pressure was ignored'
Assert (-not (Test-BlueMapStageCapacity 0 12GB 48GB 11GB 15GB)) 'Host pressure was ignored'
Assert ((Convert-BlueMapMemoryBytes '1.5GiB') -eq 1.5GB) 'Docker memory parsing failed'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('atlas-coordination-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    function docker { throw 'Error response from daemon: No such container: already-auto-removed' }
    Remove-OwnedDockerResource -Name 'atlas-bluemap-render-test'
    Remove-Item Function:\docker
    $cooldownTest=Join-Path $testRoot 'cooldowns.json'
    Write-State @() $cooldownTest
    Assert (((Get-Content -LiteralPath $cooldownTest -Raw) -replace '\s','') -eq '[]') 'Empty cooldown list did not round-trip'
    Write-State @(@{RenderId=1}) $cooldownTest
    Assert ((Read-State $cooldownTest).RenderId -eq 1) 'Single cooldown did not round-trip'
    $utcNow=[datetime]::UtcNow
    Assert (-not (Test-RetryDue @{RetryAfterUtc=$utcNow.AddMinutes(30).ToString('o')} $utcNow)) 'Future UTC retry was treated as due'
    Assert (Test-RetryDue @{RetryAfterUtc=$utcNow.AddMinutes(-1).ToString('o')} $utcNow) 'Past UTC retry was not due'
    $coordinationRoot=$testRoot; $OutputRoot=Join-Path $testRoot 'output'
    function Set-WorkerStage([string]$Stage) { }
    function docker {
        $global:LASTEXITCODE=0
        if ($args[0] -eq 'info') { return [string](48GB) }
        if ($args[0] -eq 'stats') { return '{"Name":"unrelated-service","MemUsage":"11GiB / 48GiB"}' }
        if ($args[0] -eq 'ps') { return }
        throw 'Unexpected Docker command in stage regression'
    }
    $WorkerId=3
    # The admission fixture models a host independently of the runner's RAM.
    # Production still queries real memory; an undersized CI VM must not wait forever.
    function Get-CimInstance {
        param([string]$ClassName)
        if ($ClassName -ne 'Win32_OperatingSystem') { throw "Unexpected CIM query in admission fixture: $ClassName" }
        [pscustomobject]@{FreePhysicalMemory=(50GB / 1KB)}
    }
    function Start-Sleep { throw 'Admission fixture unexpectedly waited for resources.' }
    try { $lease=Enter-BlueMapStage 'rendering' 8GB }
    finally {
        Remove-Item Function:\Get-CimInstance
        Remove-Item Function:\Start-Sleep
    }
    $record=Get-Content -LiteralPath $lease -Raw | ConvertFrom-Json
    Assert ($record.WorkerId -eq 3 -and $record.Bytes -eq 8GB) 'Real admission did not persist the third worker reservation'
    Exit-BlueMapStage $lease
    Assert (-not (Test-Path -LiteralPath $lease)) 'Released stage reservation remained'
    Remove-Item Function:\docker
    New-Item -ItemType Directory -Path $OutputRoot | Out-Null
    $OutputQuotaBytes=100MB; $MinimumFreeBytes=0
    $jobs=@([pscustomobject]@{RenderId=1;CompletedUtc='2020';X=10;Z=20;ArchiveSha256=('a'*64)},[pscustomobject]@{RenderId=2;CompletedUtc='2026'})
    Assert ((Next-Job $jobs 0).RenderId -eq 2) 'Fresh lane did not prioritize newest work'
    Assert ((Next-Job $jobs 1).RenderId -eq 1) 'Backfill lane did not prioritize oldest work'
    Assert ((Next-Job @() 0) -eq $null) 'Empty queue did not return null'
    Assert (-not (Test-Complete $jobs[0])) 'Missing generation was treated as complete'
    $generation=Join-Path $OutputRoot ("render-1-"+('a'*64)+'-v5.23-p7')
    $settingsPath=Join-Path $generation 'web\maps\atlas\settings.json'
    Write-AtomicJson -Path $settingsPath -Value @{startPos=@(10,20)}
    $manifest=@{Status='complete';SourceSha256=('a'*64);RendererProfileVersion=7;OutputBytes=20MB;QualityGate=@{Passed=$true;LocationStartExact=$true};RenderingProfile=@{Relight=@{FootprintAuditExact=$true}}}
    Write-AtomicJson -Path (Join-Path $generation 'manifest.json') -Value $manifest
    Assert (Test-Complete $jobs[0]) 'Valid source/profile/position was not skipped'
    $jobs[0].X=11
    Assert (-not (Test-Complete $jobs[0])) 'Wrong canonical position passed'
    $lock=Enter-PublicationLock
    try {
        $blocked=$false
        try {$duplicate=[IO.File]::Open((Join-Path $testRoot 'publication.lock'),'OpenOrCreate','ReadWrite','None');$duplicate.Dispose()}
        catch [IO.IOException] {$blocked=$true}
        Assert $blocked 'Exclusive publication lock permitted a second owner'
    } finally {$lock.Dispose()}
    $reservation=Reserve-Output 50MB
    Assert (Test-Path -LiteralPath $reservation) 'Output space was not reserved'
    $rejected=$false
    try {Reserve-Output 40MB | Out-Null} catch {$rejected=$true}
    Assert $rejected 'Concurrent reservations exceeded the output quota'
    'PASS: fresh/backfill scheduling, completed-generation validation, exclusive publication, and reserved quota.'
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -like 'atlas-coordination-test-*') {Remove-Item -LiteralPath $resolved -Recurse -Force}
}
