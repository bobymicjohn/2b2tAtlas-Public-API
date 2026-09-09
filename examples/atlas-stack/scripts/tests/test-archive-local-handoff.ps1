$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=Join-Path $repo ('build\handoff-test-'+[guid]::NewGuid().ToString('N'))
$fakeRepo=Join-Path $root 'fake-repo'
$scripts=Join-Path $fakeRepo 'scripts'
$staged=Join-Path $root 'staged'
$ready=Join-Path $root 'ready'
New-Item -ItemType Directory -Path $scripts,$staged,$ready -Force | Out-Null
$digest='a'*64
$state=Join-Path $root 'collector-state.json'
$config=Join-Path $root 'worker.json'
'{}' | Set-Content $config
@{entries=@(@{status='captured';warp='fixture';normalizedWarp='fixture';archiveCategoryPath=@();completedUtc=[datetime]::UtcNow.ToString('o');
    adaptive=@{standardVersion=2;componentSelection=@{};footprintArtifact=@{sha256=$digest;path=(Join-Path $staged 'fixture.zip')}}})} |
    ConvertTo-Json -Depth 12 | Set-Content $state
@'
param($StatePath,$StagedRoot,$ArchiveRoot)
$s=Get-Content $StatePath -Raw | ConvertFrom-Json
$s.entries[0].adaptive.footprintArtifact.path=Join-Path $ArchiveRoot 'fixture.zip'
$s | ConvertTo-Json -Depth 12 | Set-Content $StatePath
[pscustomobject]@{archivedCount=1}
'@ | Set-Content (Join-Path $scripts 'archive-collector-captures.ps1')
@'
param($StatePath,$CapturedRoot,$ReadyRoot,$ManifestPath)
$s=Get-Content $StatePath -Raw | ConvertFrom-Json
if ($s.entries[0].adaptive.footprintArtifact.path -ne (Join-Path $CapturedRoot 'fixture.zip')) {
    throw 'Promotion is reading the landing zone instead of original scratch.'
}
[pscustomobject]@{promotedCount=1;quarantinedCount=0;quarantined=@();promoted=@(
    [pscustomobject]@{sha256=('a'*64);destination=(Join-Path $ReadyRoot 'fixture.zip')})}
'@ | Set-Content (Join-Path $scripts 'promote-archive-adaptive-batch.ps1')
@'
param($ReadyRoot,$ApiBase,$WorkerConfigPath,$StatePath,$MinimumStableSeconds,$DelayBetweenJobsSeconds,$AdaptiveBackgroundRadiusBlocks,[string[]]$ReadyFiles)
if ($ReadyFiles.Count -ne 1 -or $ReadyFiles[0] -ne (Join-Path $ReadyRoot 'fixture.zip')) {
    throw 'Importer was not scoped to exactly the promoted capture.'
}
@{('a'*64)=@{status='accepted'}} | ConvertTo-Json | Set-Content $StatePath
'@ | Set-Content (Join-Path $scripts 'import-archive-inbox.ps1')
$arguments=@{RunRoot=$root;RepositoryRoot=$fakeRepo;StatePath=$state;StagedRoot=$staged;
    ArchiveRoot=(Join-Path $root 'landing');ReadyRoot=$ready;WorkerConfigPath=$config}
$result=& (Join-Path $repo 'scripts\invoke-archive-rolling-handoff.ps1') @arguments
if ($result.submitted -ne 1 -or $result.failed -ne 0) { throw 'Local handoff did not complete.' }
$again=& (Join-Path $repo 'scripts\invoke-archive-rolling-handoff.ps1') @arguments
if ($again.stage -ne 'idle') { throw 'Accepted capture was processed again.' }
'PASS: archiving path rewrite isolated; promotion uses scratch; batch import scoped; repeat handoff idle.'
