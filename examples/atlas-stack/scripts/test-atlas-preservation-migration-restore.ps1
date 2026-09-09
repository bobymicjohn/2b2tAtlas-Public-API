[CmdletBinding()]
param(
    [string]$Repository='\\192.0.2.10\public\AtlasBackups\restic-preservation',
    [string]$Snapshot='latest',
    [string]$ReportRoot='C:\AtlasExample\Recovery\nas-preservation-migration-restore'
)
$ErrorActionPreference='Stop'
$restic='C:\AtlasExample\Ops\tools\restic\restic_0.19.1_windows_amd64.exe'
$key='C:\AtlasExample\Api\config\backup-password.txt'
$root=[IO.Path]::GetFullPath($ReportRoot)
if (-not $root.StartsWith('C:\AtlasExample\Recovery\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Restore drill must stay inside private recovery storage.' }
New-Item -ItemType Directory -Path $root -Force | Out-Null
$samples=@(
    @{Name='00093baa925b06b71ac1b7b37669be96b10e58eb3bc80b1cbe393a1cc9f6d663.zip'; Subtree='WorldDownloads/objects'; Folder='wdl'},
    @{Name='006851f35781de5b66ee319b679284c0892854d7a6a6fb81b40d05d8036eda8b.png'; Subtree='HistoricalMedia/2b2t-wiki/objects'; Folder='image'}
)
$results=@()
foreach ($sample in $samples) {
    # The transferred historical snapshot has a UNC source root. Restoring its
    # subtree avoids restic's Windows root-name limitation; new E snapshots do not need this.
    $selector=$Snapshot + ':/\\192.0.2.10\public/2b2tAtlas/' + $sample.Subtree
    $target=Join-Path $root $sample.Folder
    & $restic --repo $Repository --password-file $key restore $selector --include ('*'+$sample.Name) --target $target --tag atlas-Assets |
        Out-File -LiteralPath (Join-Path $root ($sample.Folder+'.log')) -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Restic restore failed for $($sample.Folder)." }
    $file=Join-Path $target ('00\'+$sample.Name)
    $actual=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne [IO.Path]::GetFileNameWithoutExtension($sample.Name)) { throw "Restored $($sample.Folder) SHA-256 mismatch." }
    $results+=@{name=$sample.Name;sha256=$actual;bytes=(Get-Item -LiteralPath $file).Length;verified=$true}
}
@{repository=$Repository;checkedUtc=[datetime]::UtcNow.ToString('o');samples=$results;state='verified'} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'verification.json') -Encoding UTF8
'Preserved WDL and image restored with matching SHA-256.'
