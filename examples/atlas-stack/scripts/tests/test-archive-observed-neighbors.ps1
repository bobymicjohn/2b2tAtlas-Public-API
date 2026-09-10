$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot '../archive-json-io.ps1')
. (Join-Path $PSScriptRoot '../archive-observed-neighbors.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../invoke-archive-collector.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Get-NormalizedWarp','Get-DateInsensitiveWarpIdentity')) {
    $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Invoke-Expression $fn.Extent.Text
}
function Entry($name,$x,$z,$world='end-a',$server='archive.example') {
    [pscustomobject]@{warp=$name;sourceServer=$server;atlasX=0;atlasZ=0;
        adaptive=[pscustomobject]@{liveDimensionId=$world;archiveWarpPosition=[pscustomobject]@{x=$x;z=$z}}}
}
function Check($name,$items,$expected) {
    $found=@(Get-ObservedArchiveNeighbors -Entries @($items) -Server 'archive.example' -LiveDimensionId 'end-a' -Warp 'Hatch_2023-06-10@End' -Bounds @(-24784,5952,-23440,7200))
    if ($found.Count -ne $expected) { throw "FAILED $name : expected $expected, got $($found.Count)" }
    Write-Output "PASS $name"
}
Check 'unknown Atlas coordinates still detect observed neighboring exhibit' (Entry 'Phoenix' -24398.3 6852.09) 1
Check 'different Archive world at identical coordinates is distinct' (Entry 'Phoenix' -24398.3 6852.09 'end-b') 0
Check 'different server is distinct' (Entry 'Phoenix' -24398.3 6852.09 'end-a' 'other.example') 0
Check 'same exhibit date variants do not flag themselves' (Entry 'Hatch_2022-01-01@End' -24016 6409) 0
Check 'bounds use exact fractional position rather than rounded Atlas coordinate' (Entry 'Outside' -23440.01 6500) 1
Check 'exclusive maximum boundary' (Entry 'Outside' -23440 6500) 0
Check 'partial states with no observed position are ignored' ([pscustomobject]@{warp='Unvisited'}) 0
Check 'same observation in multiple state snapshots appears once' @((Entry 'Phoenix' -24398 6852),(Entry 'Phoenix' -24398 6852)) 1
$root=Join-Path ([IO.Path]::GetTempPath()) ('atlas-neighbors-'+[guid]::NewGuid().ToString('N'))
try {
    $local=Join-Path $root 'worker-1/state.json';$peer=Join-Path $root 'worker-2/state.json'
    Write-AtlasJsonAtomically @{entries=@()} $local
    Write-AtlasJsonAtomically @{entries=@((Entry 'Phoenix' -24398 6852))} $peer
    Write-AtlasJsonAtomically @{warp='Time';server='archive.example';dimension='end-a';x=-23862;z=6908} (Join-Path $root 'worker-2/active-capture.json')
    $entries=@(Read-ArchiveNeighborStates -StatePath $local -PeerStateRoot $root)
    Check 'completed and still-capturing peers are detected across worker queues' $entries 2
    [IO.File]::WriteAllText($peer,'broken json')
    $failed=$false
    try { Read-ArchiveNeighborStates -StatePath $local -KnownWarpStatePath $peer | Out-Null } catch { $failed=$true }
    if (-not $failed) { throw 'FAILED unreadable configured evidence must not imply no neighbors' }
    Write-Output 'PASS unreadable evidence fails closed'
} finally {
    # Delete only this test-owned directory after checking its resolved root.
    $resolved=[IO.Path]::GetFullPath($root)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolved) -like 'atlas-neighbors-*') { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
