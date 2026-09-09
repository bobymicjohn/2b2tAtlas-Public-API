$ErrorActionPreference='Stop'
$fixture=Join-Path $env:TEMP ('atlas-compact-'+[guid]::NewGuid().ToString('N'))
$root=Join-Path $fixture 'interrupted'
$paths=@('a','b' | ForEach-Object { Join-Path $root ('20260101-000000-'+($_*32)+'\archive-test\region\r.0.0.mca') })
try {
    foreach($p in $paths) { New-Item -ItemType Directory (Split-Path -Parent $p) -Force | Out-Null; [IO.File]::WriteAllBytes($p,(New-Object byte[] 1048576)) }
    $hash=(Get-FileHash $paths[0]).Hash
    $inventory=Join-Path $fixture 'inventory.json'
    @{root=$root;duplicates=@(@{bytes=1048576;sha256=$hash;paths=$paths})} | ConvertTo-Json -Depth 5 | Set-Content $inventory
    $script=Join-Path (Split-Path -Parent $PSScriptRoot) 'compact-interrupted-captures.ps1'
    & powershell.exe -NoProfile -File $script -InventoryPath $inventory -InterruptedRoot $root -AuditPath (Join-Path $fixture 'audit.jsonl') -Apply
    if($LASTEXITCODE -ne 0) { throw 'Compaction failed' }
    foreach($p in $paths){if((Get-FileHash $p).Hash -ne $hash){throw 'Content changed'}}
    $lines=@(fsutil hardlink list $paths[0])
    if($lines.Count -ne 2){throw 'Expected two paths to one preserved file'}
    & powershell.exe -NoProfile -File $script -InventoryPath $inventory -InterruptedRoot $root -AuditPath (Join-Path $fixture 'replay.jsonl') -Apply
    if($LASTEXITCODE -ne 0){throw 'Replay failed'}
    $replay=Get-Content (Join-Path $fixture 'replay.jsonl') -Raw | ConvertFrom-Json
    if($replay.reclaimedBytes -ne 0){throw 'Replay claimed additional space'}
    # Fresh mismatch must not replace either side.
    [IO.File]::Delete($paths[1]); [IO.File]::WriteAllBytes($paths[1],(New-Object byte[] 1048576))
    $f=[IO.File]::OpenWrite($paths[1]); $f.WriteByte(1); $f.Dispose()
    $ErrorActionPreference='Continue'
    & powershell.exe -NoProfile -File $script -InventoryPath $inventory -InterruptedRoot $root -AuditPath (Join-Path $fixture 'mismatch.jsonl') -Apply 2>$null
    $ErrorActionPreference='Stop'
    if($LASTEXITCODE -eq 0 -or (Get-FileHash $paths[1]).Hash -eq $hash){throw 'Hash mismatch accepted'}
    # A live writer must prevent consolidation, even with read sharing enabled.
    $held=[IO.File]::Open($paths[0],[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
    try {
        $ErrorActionPreference='Continue'
        & powershell.exe -NoProfile -File $script -InventoryPath $inventory -InterruptedRoot $root -AuditPath (Join-Path $fixture 'locked.jsonl') -Apply 2>$null
        $ErrorActionPreference='Stop'
        if($LASTEXITCODE -eq 0){throw 'Writer accepted'}
    } finally { $held.Dispose() }
    $outside=Join-Path $fixture 'outside.mca'
    [IO.File]::WriteAllBytes($outside,(New-Object byte[] 1048576))
    @{root=$root;duplicates=@(@{bytes=1048576;sha256=$hash;paths=@($paths[0],$outside)})} | ConvertTo-Json -Depth 5 | Set-Content $inventory
    $ErrorActionPreference='Continue'
    & powershell.exe -NoProfile -File $script -InventoryPath $inventory -InterruptedRoot $root -AuditPath (Join-Path $fixture 'escape.jsonl') -Apply 2>$null
    $ErrorActionPreference='Stop'
    if($LASTEXITCODE -eq 0 -or -not (Test-Path $outside)){throw 'Escaped preservation root'}
    'PASS: paths/bytes retained, replay is idempotent; mismatches, live writers and escaped paths rejected.'
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
