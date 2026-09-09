[CmdletBinding()]
param(
    [string]$CandidateRoot = 'D:\AtlasExample\Ingest\nocom-review\contrast-20260908-r2',
    [string]$StagingRoot = 'F:\AtlasExample\NocomReleaseStaging\v2-20260908',
    [string]$OriginalRoot = 'F:\AtlasExample\AtlasTiles\Nocom\v1',
    [string]$DestinationRoot = 'F:\AtlasExample\AtlasTiles\Nocom\v2',
    [switch]$WaitForCompletion
)
$ErrorActionPreference = 'Stop'
function Get-ReleaseSha256([string]$Path) {
    # Standalone Windows PowerShell can inherit a PS7-only PSModulePath. Avoid
    # relying on the script-based Get-FileHash command in that launch context.
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
$completionPath = Join-Path $CandidateRoot 'production-complete.json'
$stagingCompletionPath = Join-Path $StagingRoot 'staging-complete.json'
while (-not (Test-Path -LiteralPath $stagingCompletionPath -PathType Leaf)) {
    if (-not $WaitForCompletion) { throw 'The staged generation has not passed full verification.' }
    Start-Sleep -Seconds 10
}
$complete = Get-Content -LiteralPath $completionPath -Raw | ConvertFrom-Json
$staged = Get-Content -LiteralPath $stagingCompletionPath -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath (Join-Path $CandidateRoot 'manifest.json') -Raw | ConvertFrom-Json
$originalHash = Get-ReleaseSha256 (Join-Path $OriginalRoot 'manifest.json')
$inventoryHash = Get-ReleaseSha256 (Join-Path $CandidateRoot 'production-files.jsonl')
if (-not $complete.complete -or -not $staged.everyStagedFileHashVerified -or
    $complete.tiles -ne $staged.tiles -or $complete.bytes -ne $staged.bytes -or
    $complete.inventorySha256 -ne $inventoryHash -or $staged.inventorySha256 -ne $inventoryHash -or
    $originalHash -ne $manifest.originalManifestSha256 -or $complete.originalManifestSha256 -ne $originalHash -or
    $staged.profile -ne $manifest.displayProfile -or $complete.profile -ne $manifest.displayProfile) {
    throw 'Nocom publication evidence does not match the approved generation.'
}

$stagingFull = [IO.Path]::GetFullPath($StagingRoot).TrimEnd('\')
$destinationFull = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\')
$originalFull = [IO.Path]::GetFullPath($OriginalRoot).TrimEnd('\')
if ($stagingFull -ne 'F:\AtlasExample\NocomReleaseStaging\v2-20260908' -or
    $destinationFull -ne 'F:\AtlasExample\AtlasTiles\Nocom\v2' -or
    $originalFull -ne 'F:\AtlasExample\AtlasTiles\Nocom\v1') {
    throw 'Review the exact release paths before adapting this v2 publication script.'
}
if (Test-Path -LiteralPath $destinationFull) { throw 'Refusing to overwrite an existing immutable release.' }
foreach ($tree in @('total', 'monthly')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stagingFull $tree) -PathType Container)) { throw "Missing staged $tree tree." }
}
New-Item -ItemType Directory -Path $destinationFull | Out-Null
foreach ($tree in @('total', 'monthly')) {
    $from = [IO.Path]::GetFullPath((Join-Path $stagingFull $tree))
    $to = [IO.Path]::GetFullPath((Join-Path $destinationFull $tree))
    if (-not $from.StartsWith($stagingFull + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $to.StartsWith($destinationFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Release move escaped its validated roots.' }
    Move-Item -LiteralPath $from -Destination $to
}

$publishedAt = [DateTimeOffset]::UtcNow.ToString('o')
$manifest | Add-Member -NotePropertyName dataGeneratedAtUtc -NotePropertyValue $manifest.generatedAtUtc -Force
$manifest.generatedAtUtc = $publishedAt
$manifest.version = 'nocom-world-pulse-v2'
$manifest.reviewOnly = $false
$manifest.materialization = 'complete'
$manifest | Add-Member -NotePropertyName publishedAtUtc -NotePropertyValue $publishedAt -Force
$manifest | Add-Member -NotePropertyName originalManifestUrl -NotePropertyValue 'https://tiles.atlas.example/AtlasTiles/Nocom/v1/manifest.json' -Force
$manifest | Add-Member -NotePropertyName displayDefaults -NotePropertyValue ([ordered]@{style='contrast'; opacity=0.85; qualitativeDensity=$true; overviewGlowBelowMapZoom=-2}) -Force
$manifest | Add-Member -NotePropertyName release -NotePropertyValue ([ordered]@{tiles=$complete.tiles; bytes=$complete.bytes; inventorySha256=$inventoryHash.ToLowerInvariant(); everyPublishedFileHashVerified=$true}) -Force
$temporaryManifest = Join-Path $destinationFull '.manifest.partial'
[IO.File]::WriteAllText($temporaryManifest, ($manifest | ConvertTo-Json -Depth 30), (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath $temporaryManifest -Destination (Join-Path $destinationFull 'manifest.json')
[ordered]@{status='tiles-published-frontend-pending'; publishedAtUtc=$publishedAt; root=$destinationFull; tiles=$complete.tiles; bytes=$complete.bytes; originalRetained=$originalFull; manifestSha256=(Get-ReleaseSha256 (Join-Path $destinationFull 'manifest.json'))} |
    ConvertTo-Json | Set-Content -LiteralPath 'C:\AtlasExample\Recovery\nocom-production-publication.json' -Encoding UTF8
Get-Content -LiteralPath 'C:\AtlasExample\Recovery\nocom-production-publication.json'
