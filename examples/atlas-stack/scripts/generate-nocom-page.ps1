[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$DataApiBaseUrl,
    [Parameter(Mandatory = $true)][string]$ApiBaseUrl,
    [Parameter(Mandatory = $true)][string]$SiteBaseUrl,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$dataset = Invoke-RestMethod -Uri "$DataApiBaseUrl/api/nocom" -TimeoutSec 30
$periodResponse = Invoke-RestMethod -Uri "$DataApiBaseUrl/api/nocom/periods" -TimeoutSec 30
$overworldHighways = Invoke-RestMethod -Uri "$DataApiBaseUrl/api/nocom/highways?dimension=overworld" -TimeoutSec 30
$netherHighways = Invoke-RestMethod -Uri "$DataApiBaseUrl/api/nocom/highways?dimension=nether" -TimeoutSec 30
$periods = @($periodResponse)
$highways = @($overworldHighways) + @($netherHighways)
if ($dataset.id -ne 'nocom-world-pulse-v1' -or $periods.Count -ne 39 -or $highways.Count -ne 272 -or
    [long]$dataset.observations -ne [long](($periods | Measure-Object observations -Sum).Sum)) {
    throw 'Nocom release metadata or aggregate count mismatch.'
}
$root = Join-Path $OutputDirectory 'nocom'
New-Item -ItemType Directory -Path $root -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
function Write-NocomFile([string]$Name, [string]$Text) { [IO.File]::WriteAllText((Join-Path $root $Name), $Text, $utf8) }
function Html([object]$Value) { [Net.WebUtility]::HtmlEncode([string]$Value) }
function Number([long]$Value) { $Value.ToString('N0', [Globalization.CultureInfo]::InvariantCulture) }
Write-NocomFile 'dataset.json' ($dataset | ConvertTo-Json -Depth 20)
Write-NocomFile 'periods.jsonl' ((@($periods | ForEach-Object { $_ | ConvertTo-Json -Depth 12 -Compress }) -join "`n") + "`n")
Write-NocomFile 'highways.jsonl' ((@($highways | ForEach-Object { $_ | ConvertTo-Json -Depth 12 -Compress }) -join "`n") + "`n")
$highwaySummary = @('overworld', 'nether' | ForEach-Object {
    $dimension = $_
    $dimensionHighways = @($highways | Where-Object dimension -eq $dimension)
    $dimensionTotal = [long](($dimensionHighways | Measure-Object observations -Sum).Sum)
    $dimensionHighways | Group-Object direction | ForEach-Object {
        $observations = [long](($_.Group | Measure-Object observations -Sum).Sum)
        [pscustomobject][ordered]@{ dimension = $dimension; direction = $_.Name; observations = $observations;
            shareOfDirectionalObservations = [math]::Round($observations / [double]$dimensionTotal, 6);
            periodCount = $_.Count }
    }
})
if ($highwaySummary.Count -ne 16 -or [long](($highwaySummary | Measure-Object observations -Sum).Sum) -ne
    [long](($highways | Measure-Object observations -Sum).Sum)) { throw 'Highway comparison totals do not reconcile.' }
Write-NocomFile 'highway-summary.json' ($highwaySummary | ConvertTo-Json -Depth 8)
$highwayRows = @($highwaySummary | ForEach-Object {
    '<tr data-highway-dimension="{0}" data-highway-direction="{1}"><th scope="row">{0} / {1}</th><td class="highway-count">{2}</td><td><meter min="0" max="1" value="{3}" aria-label="Share of directional observations"></meter> <span class="highway-share">{4}%</span></td></tr>' -f
        (Html $_.dimension), (Html $_.direction), (Number $_.observations),
        ([double]$_.shareOfDirectionalObservations).ToString([Globalization.CultureInfo]::InvariantCulture),
        (100 * [double]$_.shareOfDirectionalObservations).ToString('F1', [Globalization.CultureInfo]::InvariantCulture)
}) -join "`n"
$highwayOptions = @($highways | Select-Object -ExpandProperty periodStartUtc -Unique | Sort-Object | ForEach-Object {
    '<option value="{0}">{1} (30-day bucket)</option>' -f (Html $_), (Html ([string]$_).Substring(0,10))
}) -join "`n"
$highwayData = ($highways | ConvertTo-Json -Depth 8 -Compress).Replace('<', '\u003c')
$highwayScript = @'
(() => {
  const data = JSON.parse(document.getElementById('highway-data').textContent);
  const select = document.getElementById('highway-period');
  select.addEventListener('change', () => {
    const filtered = data.filter(row => !select.value || row.periodStartUtc === select.value);
    document.querySelectorAll('[data-highway-direction]').forEach(row => {
      const dimension = row.dataset.highwayDimension, direction = row.dataset.highwayDirection;
      const total = filtered.filter(r => r.dimension === dimension).reduce((sum, r) => sum + r.observations, 0);
      const count = filtered.filter(r => r.dimension === dimension && r.direction === direction).reduce((sum, r) => sum + r.observations, 0);
      const share = total ? count / total : 0;
      row.querySelector('.highway-count').textContent = count.toLocaleString('en-US');
      row.querySelector('meter').value = share;
      row.querySelector('.highway-share').textContent = (share * 100).toFixed(1) + '%';
    });
    document.getElementById('highway-status').textContent = select.value
      ? 'Showing the whole 30-day bucket beginning ' + select.value.slice(0, 10) + '.'
      : 'Showing all 17 published buckets.';
  });
})();
'@
$description = 'Historical Nocom loaded-chunk observations: 39 period layers, three dimensions, source hashes, observation totals, highway aggregates, JSON/API and AI access. Counts are observations, not players.'
$jsonLd = [ordered]@{
    '@context' = 'https://schema.org'; '@type' = 'Dataset'; '@id' = "$SiteBaseUrl/nocom/#dataset"
    name = 'Nocom World Pulse historical observations'; url = "$SiteBaseUrl/nocom/"; description = $description
    creator = [ordered]@{ '@type' = 'Organization'; name = 'Nerds Inc and Nocom contributors'; url = 'https://github.com/nerdsinspace' }
    publisher = [ordered]@{ '@type' = 'Organization'; name = '2b2t Atlas'; url = "$SiteBaseUrl/" }
    isBasedOn = $dataset.sourceUrl
    temporalCoverage = '2020-03-09/2021-07-15'
    measurementTechnique = 'Positive remote loaded-chunk observations aggregated into fixed 30-day UTC buckets; scanner-biased and not a census.'
    variableMeasured = @('Positive loaded-chunk observations', 'Grouped chunk-period rows')
    spatialCoverage = @($dataset.dimensions | ForEach-Object { [ordered]@{ '@type' = 'Place'; name = "Minecraft $($_.dimension) (native game coordinates, not geographic coordinates)" } })
    distribution = @(
        [ordered]@{ '@type' = 'DataDownload'; name = 'Dataset provenance and coverage'; contentUrl = "$SiteBaseUrl/nocom/dataset.json"; encodingFormat = 'application/json' },
        [ordered]@{ '@type' = 'DataDownload'; name = '39 dimension-period aggregates'; contentUrl = "$SiteBaseUrl/nocom/periods.jsonl"; encodingFormat = 'application/x-ndjson' },
        [ordered]@{ '@type' = 'DataDownload'; name = '272 highway aggregate rows'; contentUrl = "$SiteBaseUrl/nocom/highways.jsonl"; encodingFormat = 'application/x-ndjson' },
        [ordered]@{ '@type' = 'DataDownload'; name = 'Public JSON API'; contentUrl = "$ApiBaseUrl/api/nocom"; encodingFormat = 'application/json' }
    )
}
$structured = ($jsonLd | ConvertTo-Json -Depth 20 -Compress).Replace('<', '\u003c')
$dimensionRows = @($dataset.dimensions | ForEach-Object {
    '<tr><th scope="row">{0}</th><td>{1}</td><td>{2}</td><td>{3}</td></tr>' -f (Html $_.dimension), $_.periodCount, (Number $_.groupedRows), (Number $_.observations)
}) -join "`n"
$caveatItems = @($dataset.caveats | ForEach-Object { '<li>{0}</li>' -f (Html $_) }) -join "`n"
$series = @($dataset.dimensions | ForEach-Object {
    $dimension = [string]$_.dimension
    $rows = @($periods | Where-Object dimension -eq $dimension | ForEach-Object {
        '<tr><td>{0}</td><td>{1}</td><td>{2}</td></tr>' -f (Html $_.key), (Number $_.observations), (Number $_.groupedRows)
    }) -join "`n"
    '<details><summary>{0}: {1} fixed 30-day periods</summary><div class="table-scroll"><table><thead><tr><th>Bucket starts (UTC)</th><th>Observations</th><th>Grouped rows</th></tr></thead><tbody>{2}</tbody></table></div></details>' -f (Html $dimension), $_.periodCount, $rows
}) -join "`n"
$page = @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Nocom World Pulse data | 2b2t Atlas</title><meta name="description" content="$(Html $description)"><link rel="canonical" href="$SiteBaseUrl/nocom/">
<meta property="og:title" content="Nocom World Pulse historical data"><meta property="og:description" content="$(Html $description)"><meta property="og:url" content="$SiteBaseUrl/nocom/">
<script type="application/ld+json">$structured</script>
<style>body{margin:0;background:#101619;color:#e8edf0;font:16px/1.65 system-ui,sans-serif}main{max-width:1040px;margin:auto;padding:32px 24px 64px}a{color:#80d8ef}nav{display:flex;gap:20px;flex-wrap:wrap}h1{font-size:clamp(2.2rem,6vw,3.8rem);line-height:1.1;margin-bottom:20px}h2{margin-top:38px}.eyebrow{color:#93abb5;text-transform:uppercase;letter-spacing:.15em;font-size:12px}.lead{max-width:780px;color:#c8d6dc;font-size:20px}.stats{display:grid;grid-template-columns:repeat(3,1fr);gap:16px;margin:30px 0}.stat{padding:18px;background:#19262c;border:1px solid #30464e;border-radius:10px}.stat strong{display:block;color:#7adcee;font-size:clamp(1.1rem,3vw,1.8rem)}.stat span{color:#b1c1c8;font-size:14px}table{border-collapse:collapse;width:100%;text-align:left;font-variant-numeric:tabular-nums}th,td{padding:12px;border-bottom:1px solid #30434c}th{color:#b6d9e6}code{overflow-wrap:anywhere;font-size:14px;color:#b5e7f3}.table-scroll{overflow:auto}details{padding:14px 0;border-bottom:1px solid #30434c}summary{cursor:pointer;color:#b6d9e6}li{margin:9px 0}.note{padding:18px;border-left:3px solid #69c9db;background:#172329}@media(max-width:600px){main{padding:24px 16px}.stats{grid-template-columns:1fr}.lead{font-size:18px}th,td{padding:9px}}</style></head>
<body><main><nav aria-label="Atlas"><a href="/">2b2t Atlas</a><a href="/map">Interactive map</a><a href="/mcp/">AI / MCP access</a></nav>
<p class="eyebrow">Historical dataset / Nerds Inc research</p><h1>Nocom World Pulse</h1><p class="lead">Explore the historical record of loaded chunks across the Overworld, Nether and End. Atlas connects the published Nocom aggregates to readable timelines, map tiles and public data tools.</p>
<div class="stats"><div class="stat"><strong>$(Number $dataset.observations)</strong><span>positive observations</span></div><div class="stat"><strong>39 period layers</strong><span>plus three all-time map layers</span></div><div class="stat"><strong>2020-2021</strong><span>published aggregate coverage</span></div></div>
<p class="note">A loaded chunk is evidence of activity nearby. It is not an exact player position, a unique visitor, a confirmed base, or proof of ownership. The broader exploit history began in 2018; this aggregate product begins on March 9, 2020.</p>
<h2>Coverage by dimension</h2><div class="table-scroll"><table><thead><tr><th>Native dimension</th><th>Periods</th><th>Grouped chunk-period rows</th><th>Observations</th></tr></thead><tbody>$dimensionRows</tbody></table></div>
<p>End coverage begins February 2, 2021. Buckets span 30 days, not calendar months. July's bucket ends August 1, but Nocom was patched on July 15, 2021; the bucket boundary does not extend the historical observation record.</p>
<h2>Explore and download</h2><ul><li><a href="/map">Open the map</a>, then enable World Pulse for the desired dimension, all-time layer or period.</li><li><a href="dataset.json">Dataset metadata</a> and <a href="periods.jsonl">39 period records (JSONL)</a>.</li><li><a href="highways.jsonl">272 highway observation records (JSONL)</a>: the authors' temporary aggregate for eight compass directions in the Overworld and Nether.</li><li><a href="$ApiBaseUrl/api/nocom">Public dataset API</a>, <a href="$ApiBaseUrl/api/nocom/periods?dimension=overworld">Overworld time series</a>, and <a href="$ApiBaseUrl/api/nocom/highways?dimension=nether&amp;direction=northeast">Northeast Nether highway series</a>.</li><li><a href="$(Html $dataset.manifestUrl)">Original Atlas tile manifest</a> with sparse PNG templates, zoom levels and source hashes.</li><li><a href="$(Html $dataset.sourceUrl)">Nerds Inc's original data release</a> and <a href="https://github.com/nerdsinspace/nocom-explanation">first-party explanation</a>.</li></ul>
<h2>AI and research access</h2><p>The public MCP endpoint is <code>$ApiBaseUrl/mcp</code>. Use <code>get_nocom_dataset</code>, <code>get_nocom_periods</code> and <code>get_nocom_highway_activity</code>. Cite this dataset page and retain the original source attribution. Date filters return overlapping whole buckets, never exact-day counts.</p>
<h2>Observation time series</h2>$series
<h2>Highway direction comparison</h2><p>Compare the eight published directions within each dimension, across all observations or one historical bucket. The bars show each direction's share of this highway aggregate. Scanner coverage and repeated observations affect these values; this is not a ranking of unique travelers or the proportion of all world activity.</p>
<label for="highway-period">Observation period</label> <select id="highway-period"><option value="">All published buckets</option>$highwayOptions</select>
<p id="highway-status" aria-live="polite">Showing all 17 published buckets.</p><div class="table-scroll"><table><thead><tr><th>Dimension / direction</th><th>Observations</th><th>Share within dimension</th></tr></thead><tbody>$highwayRows</tbody></table></div>
<p><a href="highway-summary.json">Download the 16 direction totals (JSON)</a>. Native Minecraft axes: east +X, west -X, south +Z, north -Z. Nether positions remain in native Nether coordinates. The underlying authors' temporary aggregate omits server identifiers; retain that source limitation when interpreting it.</p>
<script id="highway-data" type="application/json">$highwayData</script><script>$highwayScript</script>
<h2>Interpretation and provenance</h2><ul>$caveatItems<li>Extent rectangles describe outer observed bounds; they do not establish coverage at every point inside them. Repeated chunks across periods are separate grouped rows.</li><li>PNG color and alpha are visualization values; do not decode them as exact counts.</li></ul>
<p>$(Html $dataset.groupedServerScope)</p><p>$(Html $dataset.highwaySourceScope)</p><p>$(Html $dataset.attribution)</p><p>Grouped source SHA-256: <code>$(Html $dataset.groupedSourceSha256)</code><br>Highway source SHA-256: <code>$(Html $dataset.highwaySourceSha256)</code></p>
</main></body></html>
"@
Write-NocomFile 'index.html' $page
Write-Output 'Generated crawlable Nocom dataset page, 39 period records and 272 highway records.'
