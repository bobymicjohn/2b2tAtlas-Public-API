[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportPath,

    [Parameter(Mandatory = $true)]
    [string]$DatabasePath,

    [switch]$ConfirmApply
)

$ErrorActionPreference = 'Stop'
if (-not $ConfirmApply) {
    throw 'This command writes to a disposable database copy. Pass -ConfirmApply after reviewing the report.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.artifacts'))
$database = [IO.Path]::GetFullPath($DatabasePath)
$reportFile = [IO.Path]::GetFullPath($ReportPath)
$artifactPrefix = $artifactsRoot.TrimEnd('\') + '\'
if (-not $database.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing non-artifact database: $database"
}
if (-not (Test-Path $database -PathType Leaf)) { throw "Database not found: $database" }
if (-not (Test-Path $reportFile -PathType Leaf)) { throw "Report not found: $reportFile" }

$sqlite = (Get-Command sqlite3 -ErrorAction Stop).Source
$report = Get-Content $reportFile -Raw | ConvertFrom-Json
if ($report.schemaVersion -ne 1 -or -not $report.policy.StartsWith('Dry run only')) {
    throw 'Report schema or policy is not recognized.'
}

function Quote-Sql {
    param([string]$Value)
    if ($null -eq $Value) { return 'NULL' }
    return "'" + $Value.Replace("'", "''") + "'"
}

$matches = @($report.matches | Where-Object {
    -not $_.disambiguation -and
    ($_.confidence -eq 1) -and
    (-not [string]::IsNullOrWhiteSpace($_.suggestedWiki))
})
if ($matches.Count -eq 0) { throw 'Report has no applicable high-confidence matches.' }

$columns = & $sqlite $database "PRAGMA table_info('Locations');"
foreach ($required in 'Description','Wiki') {
    if (-not ($columns -match "\|$required\|")) {
        throw "Locations.$required does not exist. Run SchemaUpgrader on the copy first."
    }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = "$database.bak-$stamp"
& $sqlite $database ".backup '$($backup.Replace("'", "''"))'"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $backup)) { throw 'SQLite backup failed.' }

$sqlPath = Join-Path (Split-Path -Parent $reportFile) "wiki-enrichment-apply-$stamp.sql"
$receiptPath = Join-Path (Split-Path -Parent $reportFile) "wiki-enrichment-apply-$stamp.json"
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('BEGIN IMMEDIATE;')
foreach ($match in $matches) {
    $id = [int]$match.rowid
    $wiki = Quote-Sql ([string]$match.suggestedWiki)
    $description = Quote-Sql ([string]$match.suggestedDescription)
    $lines.Add("UPDATE Locations SET Wiki = CASE WHEN Wiki IS NULL OR trim(Wiki) = '' THEN $wiki ELSE Wiki END, Description = CASE WHEN (Description IS NULL OR trim(Description) = '') AND $description IS NOT NULL THEN $description ELSE Description END WHERE Rowid = $id;")
}
$lines.Add('COMMIT;')
$utf8NoBom = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllLines($sqlPath, $lines, $utf8NoBom)

$beforeWiki = [int](& $sqlite $database "SELECT COUNT(*) FROM Locations WHERE Wiki IS NOT NULL AND trim(Wiki) <> '';" )
$beforeDescription = [int](& $sqlite $database "SELECT COUNT(*) FROM Locations WHERE Description IS NOT NULL AND trim(Description) <> '';" )
& $sqlite $database ".read '$($sqlPath.Replace("'", "''"))'"
if ($LASTEXITCODE -ne 0) { throw 'SQLite import failed; restore the generated backup.' }
$afterWiki = [int](& $sqlite $database "SELECT COUNT(*) FROM Locations WHERE Wiki IS NOT NULL AND trim(Wiki) <> '';" )
$afterDescription = [int](& $sqlite $database "SELECT COUNT(*) FROM Locations WHERE Description IS NOT NULL AND trim(Description) <> '';" )

$receipt = [ordered]@{
    schemaVersion = 1
    appliedUtc = (Get-Date).ToUniversalTime().ToString('o')
    database = $database
    backup = $backup
    sourceReport = $reportFile
    consideredMatches = $matches.Count
    wikiRowsAdded = $afterWiki - $beforeWiki
    descriptionRowsAdded = $afterDescription - $beforeDescription
    policy = 'Applied only confidence-1, non-disambiguation matches to blank fields in an artifact database copy.'
}
[IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 5), $utf8NoBom)
$receipt | Format-List
Write-Host "Receipt: $receiptPath"
