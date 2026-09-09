[CmdletBinding()]
param(
    [string]$DestinationRoot = 'E:\2b2t\MiscRenders\LocationAttachments',
    [string]$BorderSourceRoot = 'E:\2b2t\MiscRenders\+Z Border',
    [string]$ArchiveRoot = 'E:\AtlasExample\HistoricalMedia',
    [string]$WorkRoot = 'D:\AtlasExample\Ingest\media-work\historical'
)

$ErrorActionPreference = 'Stop'

function Copy-IncrementalArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & robocopy $Source $Destination /E /COPY:DAT /DCOPY:DAT /FFT /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) { throw "Robocopy failed with exit code $LASTEXITCODE while archiving $Source." }
}
$downloads = @(
    @{ Slug='imperators-base'; File='imperators-base.png'; Url='https://static.wikitide.net/2b2twiki/0/02/Imperator%27s_base.png' },
    @{ Slug='space-valkyria'; File='space-valkyria.png'; Url='https://static.wikitide.net/2b2twiki/3/30/Space_Valkyria.png' },
    @{ Slug='summermelon'; File='summermelon.png'; Url='https://static.wikitide.net/2b2twiki/4/47/Summermelonbaserender.png' },
    @{ Slug='valley-of-wheat'; File='valley-of-wheat.png'; Url='https://static.wikitide.net/2b2twiki/1/13/EXIjh8O.png' },
    @{ Slug='rocket-town'; File='rocket-town.png'; Url='https://static.wikitide.net/2b2twiki/1/1f/Rocket_Town_1.png' },
    @{ Slug='sky-masons'; File='sky-masons.jpg'; Url='https://static.wikitide.net/2b2twiki/6/6a/SkyCroppedWikiColor.jpg' },
    @{ Slug='smibville'; File='smibville.png'; Url='https://static.wikitide.net/2b2twiki/9/9e/Smibville_2020-01-12a.png' },
    @{ Slug='2k2k'; File='2k2k.png'; Url='https://static.wikitide.net/2b2twiki/f/f5/2k2k%282012-04-02_224532%29.png' },
    @{ Slug='camp-facepunch'; File='camp-facepunch.png'; Url='https://static.wikitide.net/2b2twiki/c/cc/CampF.png' }
    @{ Slug='space-valkyria-iii'; File='space-valkyria-iii-after-grief-2022.png'; Url='https://i.redd.it/nhz4n9n0qo191.png' }
    @{ Slug='minus-z-border'; File='minus-z-world-border-2025.png'; Url='https://i.redd.it/udybmn1eayke1.png' }
    @{ Slug='minus-x-plus-z-corner'; File='jumboman32-border-render-2021.png'; Url='https://i.redd.it/rno7tbdpu6u61.png' }
    @{ Slug='plus-x-border'; File='plus-x-overworld-border-2022.png'; Url='https://i.imgur.com/5bUkXqr.png' }
    @{ Slug='plus-x-nether-border'; File='plus-x-nether-border-2022.png'; Url='https://i.imgur.com/G7I7tX0.png' }
)

$muCathedralImages = @(
    @{ Hash='9cRtk8E'; Extension='.png' },
    @{ Hash='eDGCI3K'; Extension='.png' },
    @{ Hash='Ycwz6sp'; Extension='.jpg' },
    @{ Hash='WOwqNRh'; Extension='.png' },
    @{ Hash='wR2xDcw'; Extension='.png' },
    @{ Hash='0iwnRpE'; Extension='.png' },
    @{ Hash='Zl912se'; Extension='.png' },
    @{ Hash='9hgb9gJ'; Extension='.png' },
    @{ Hash='SmAubJV'; Extension='.jpg' },
    @{ Hash='qgGyDzA'; Extension='.png' },
    @{ Hash='P0mV00X'; Extension='.png' },
    @{ Hash='xvgPjxy'; Extension='.png' },
    @{ Hash='MmTT5uH'; Extension='.png' },
    @{ Hash='BuXQ3Rr'; Extension='.png' },
    @{ Hash='s3phK5O'; Extension='.png' },
    @{ Hash='hc6lgO6'; Extension='.png' },
    @{ Hash='PIiYDMp'; Extension='.png' },
    @{ Hash='sICkox2'; Extension='.png' },
    @{ Hash='oI6Bovr'; Extension='.png' }
)

$servingRoot = $DestinationRoot
$DestinationRoot = Join-Path $WorkRoot 'public'
if (Test-Path -LiteralPath $servingRoot -PathType Container) {
    Copy-IncrementalArchive -Source $servingRoot -Destination $DestinationRoot
}
New-Item -ItemType Directory -Path $DestinationRoot -Force | Out-Null
foreach ($item in $downloads) {
    $directory = Join-Path $DestinationRoot $item.Slug
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $destination = Join-Path $directory $item.File
    if (-not (Test-Path -LiteralPath $destination)) {
        Invoke-WebRequest -Uri $item.Url -OutFile $destination -UseBasicParsing -TimeoutSec 120
    }
    $thumbnail = Join-Path $directory ($item.Slug + '.webp')
    if (-not (Test-Path -LiteralPath $thumbnail) -or
        (Get-Item -LiteralPath $thumbnail).LastWriteTimeUtc -lt (Get-Item -LiteralPath $destination).LastWriteTimeUtc) {
        & magick $destination -auto-orient -thumbnail '960x640>' -strip -quality 82 $thumbnail
        if ($LASTEXITCODE -ne 0) { throw "ImageMagick failed for $destination" }
    }
}

$borderDirectory = Join-Path $DestinationRoot 'z-border'
New-Item -ItemType Directory -Path $borderDirectory -Force | Out-Null
$borderImages = Get-ChildItem -LiteralPath $BorderSourceRoot -File | Where-Object Extension -Match '^\.(png|jpe?g|webp)$'
foreach ($image in $borderImages) {
    $thumbnailName = switch ($image.Name) {
        'Z+BorderRender6k.png' { 'z-border-6k.webp'; break }
        '2b2t - 4k emitters on (NIGHT)-42.png' { 'z-border-night.webp'; break }
        default {
            $slug = ([IO.Path]::GetFileNameWithoutExtension($image.Name) -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
            "$slug-thumb.webp"
        }
    }
    $thumbnail = Join-Path $borderDirectory $thumbnailName
    if (-not (Test-Path -LiteralPath $thumbnail) -or
        (Get-Item -LiteralPath $thumbnail).LastWriteTimeUtc -lt $image.LastWriteTimeUtc) {
        $localBorderRoot = Join-Path $WorkRoot 'border-source'
        New-Item -ItemType Directory -Path $localBorderRoot -Force | Out-Null
        $localBorder = Join-Path $localBorderRoot $image.Name
        Copy-Item -LiteralPath $image.FullName -Destination $localBorder -Force
        & magick $localBorder -auto-orient -thumbnail '960x640>' -strip -quality 82 $thumbnail
        if ($LASTEXITCODE -ne 0) { throw "ImageMagick failed for +Z border image $($image.FullName)." }
    }
}

$muDirectory = Join-Path $DestinationRoot 'mu-megabase-cathedral'
New-Item -ItemType Directory -Path $muDirectory -Force | Out-Null
$muIndex = 0
foreach ($image in $muCathedralImages) {
    $muIndex++
    $baseName = ('cathedral-{0:D2}-{1}' -f $muIndex, $image.Hash)
    $destination = Join-Path $muDirectory ($baseName + $image.Extension)
    if (-not (Test-Path -LiteralPath $destination)) {
        Invoke-WebRequest -Uri ("https://i.imgur.com/$($image.Hash)$($image.Extension)") -OutFile $destination -UseBasicParsing -TimeoutSec 120
    }
    $thumbnail = Join-Path $muDirectory ($baseName + '-thumb.webp')
    if (-not (Test-Path -LiteralPath $thumbnail) -or
        (Get-Item -LiteralPath $thumbnail).LastWriteTimeUtc -lt (Get-Item -LiteralPath $destination).LastWriteTimeUtc) {
        & magick $destination -auto-orient -thumbnail '960x640>' -strip -quality 82 $thumbnail
        if ($LASTEXITCODE -ne 0) { throw "ImageMagick failed for $destination" }
    }
}

$publicArchive = Join-Path $ArchiveRoot 'public'
$borderArchive = Join-Path $ArchiveRoot 'operator\+Z Border'
Copy-IncrementalArchive -Source $DestinationRoot -Destination $servingRoot
Copy-IncrementalArchive -Source $DestinationRoot -Destination $publicArchive
Copy-IncrementalArchive -Source $BorderSourceRoot -Destination $borderArchive

$files = Get-ChildItem -LiteralPath $DestinationRoot -Recurse -File
[pscustomobject]@{
    destination = $servingRoot
    workRoot = $WorkRoot
    publicArchive = $publicArchive
    borderArchive = $borderArchive
    files = $files.Count
    bytes = ($files | Measure-Object -Property Length -Sum).Sum
    generatedUtc = [DateTime]::UtcNow.ToString('o')
} | ConvertTo-Json
