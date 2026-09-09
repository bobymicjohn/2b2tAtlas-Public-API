$ErrorActionPreference='Stop'
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$root=Join-Path $repo ('build\media-scratch-test-'+[guid]::NewGuid().ToString('N'))
$work=Join-Path $root 'work'
$border=Join-Path $root 'border'
New-Item -ItemType Directory -Path $border -Force | Out-Null
$global:atlasMediaFixturePng=Join-Path $border 'fixture.png'
$global:atlasMediaMagick=(Get-Command magick -ErrorAction Stop).Source
& $global:atlasMediaMagick -size 2x2 xc:navy $global:atlasMediaFixturePng
if ($LASTEXITCODE -ne 0) { throw 'Could not create fixture image.' }
$global:atlasMediaCalls=0
$global:atlasMediaScratch=$work
function Invoke-WebRequest {
    param($Uri,$OutFile,[switch]$UseBasicParsing,$TimeoutSec)
    Copy-Item -LiteralPath $global:atlasMediaFixturePng -Destination $OutFile
}
function magick {
    $output=[string]$args[-1]
    if (-not $output.StartsWith($global:atlasMediaScratch,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Image processing wrote outside scratch.'
    }
    $global:atlasMediaCalls++
    & $global:atlasMediaMagick @args
}
$parameters=@{DestinationRoot=(Join-Path $root 'served');BorderSourceRoot=$border;ArchiveRoot=(Join-Path $root 'preserved');WorkRoot=$work}
& (Join-Path $repo 'scripts\sync-historical-location-media.ps1') @parameters | Out-Null
$first=$global:atlasMediaCalls
if ($first -lt 30) { throw 'Fixture did not exercise media generation.' }
& (Join-Path $repo 'scripts\sync-historical-location-media.ps1') @parameters | Out-Null
if ($global:atlasMediaCalls -ne $first) { throw 'Current thumbnails were generated again.' }
if (-not (Test-Path (Join-Path $root 'served\imperators-base\imperators-base.webp'))) { throw 'Completed thumbnail was not published.' }
"PASS: $first thumbnails generated only in scratch; repeat run generated zero; landing copy exists; no network requests."
