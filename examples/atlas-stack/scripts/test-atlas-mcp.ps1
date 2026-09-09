[CmdletBinding()]
param(
    [ValidatePattern('^https?://')]
    [string]$Endpoint = 'http://127.0.0.1:5297/mcp',

    [ValidateRange(1, 100)]
    [int]$ExpectedToolCount = 18
)

$ErrorActionPreference = 'Stop'
$Endpoint = $Endpoint.TrimEnd('/')
$requestId = 0

function Invoke-AtlasMcp {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [AllowNull()][object]$Params
    )

    $script:requestId++
    $payload = [ordered]@{
        jsonrpc = '2.0'
        id = $script:requestId
        method = $Method
    }
    if ($null -ne $Params) { $payload.params = $Params }

    $response = Invoke-WebRequest `
        -Uri $Endpoint `
        -Method Post `
        -ContentType 'application/json' `
        -Headers @{ Accept = 'application/json, text/event-stream'; 'MCP-Protocol-Version' = '2025-11-25' } `
        -Body ($payload | ConvertTo-Json -Depth 20 -Compress) `
        -UseBasicParsing

    $dataLines = @([regex]::Matches([string]$response.Content, '(?m)^data:\s*(.+?)\r?$') | ForEach-Object { $_.Groups[1].Value })
    if ($dataLines.Count -eq 0) {
        throw "MCP response for $Method did not contain an SSE data event."
    }

    $message = $dataLines[-1] | ConvertFrom-Json
    if ($null -ne $message.error) {
        throw "MCP $Method failed: $($message.error.code) $($message.error.message)"
    }
    return $message.result
}

$initialize = Invoke-AtlasMcp 'initialize' ([ordered]@{
    protocolVersion = '2025-11-25'
    capabilities = [ordered]@{}
    clientInfo = [ordered]@{ name = 'atlas-mcp-smoke-test'; version = '1.0.0' }
})
if ([string]$initialize.serverInfo.name -ne '2b2t-atlas') {
    throw "Unexpected MCP server name: $($initialize.serverInfo.name)"
}

$toolList = Invoke-AtlasMcp 'tools/list' ([ordered]@{})
$tools = @($toolList.tools)
if ($tools.Count -ne $ExpectedToolCount) {
    throw "Expected $ExpectedToolCount MCP tools but received $($tools.Count)."
}

$requiredTools = @(
    'search_locations', 'get_location', 'find_locations_near', 'find_locations_by_time_range',
    'find_preserved_builds', 'research_location', 'search_groups', 'get_group', 'get_group_builds',
    'search_highways', 'get_highway', 'get_warps', 'get_world_downloads',
    'get_render_metadata', 'get_dataset_stats', 'get_nocom_dataset', 'get_nocom_periods', 'get_nocom_highway_activity'
)
$toolNames = @($tools | ForEach-Object { [string]$_.name })
$missingTools = @($requiredTools | Where-Object { $_ -notin $toolNames })
if ($missingTools.Count -gt 0) { throw "MCP tools missing: $($missingTools -join ', ')" }

$unsafeTools = @($tools | Where-Object {
    $null -eq $_.annotations -or
    $_.annotations.readOnlyHint -ne $true -or
    $_.annotations.destructiveHint -ne $false -or
    $_.annotations.openWorldHint -ne $false
})
if ($unsafeTools.Count -gt 0) { throw "MCP read-only annotations are missing or unsafe for: $(@($unsafeTools.name) -join ', ')" }

$statsCall = Invoke-AtlasMcp 'tools/call' ([ordered]@{
    name = 'get_dataset_stats'
    arguments = [ordered]@{}
})
if ($statsCall.isError -eq $true -or [int]$statsCall.structuredContent.locations -lt 1) {
    throw 'MCP dataset stats did not return a populated Atlas catalog.'
}

$searchCall = Invoke-AtlasMcp 'tools/call' ([ordered]@{
    name = 'search_groups'
    arguments = [ordered]@{ query = 'Spawn Masons'; limit = 10 }
})
if ($searchCall.isError -eq $true -or @($searchCall.structuredContent.result).Count -lt 1) {
    throw 'MCP normalized group search did not resolve Spawn Masons.'
}

$resourceList = Invoke-AtlasMcp 'resources/list' ([ordered]@{})
$nocomCall = Invoke-AtlasMcp 'tools/call' ([ordered]@{ name = 'get_nocom_dataset'; arguments = [ordered]@{} })
if ($nocomCall.isError -eq $true -or [long]$nocomCall.structuredContent.observations -ne 3133950352) { throw 'Nocom dataset counts failed MCP verification.' }
$periodCall = Invoke-AtlasMcp 'tools/call' ([ordered]@{ name = 'get_nocom_periods'; arguments = [ordered]@{ dimension = 'end' } })
if ($periodCall.isError -eq $true -or @($periodCall.structuredContent.result).Count -ne 5) { throw 'Nocom End period discovery failed MCP verification.' }
$highwayCall = Invoke-AtlasMcp 'tools/call' ([ordered]@{ name = 'get_nocom_highway_activity'; arguments = [ordered]@{ dimension = 'nether'; direction = 'northwest' } })
if ($highwayCall.isError -eq $true -or @($highwayCall.structuredContent.result).Count -ne 17) { throw 'Nocom highway series failed MCP verification.' }
if (@($resourceList.resources | Where-Object uri -eq '2b2tatlas://dataset').Count -ne 1) {
    throw 'MCP dataset resource is missing.'
}

$resourceTemplates = Invoke-AtlasMcp 'resources/templates/list' ([ordered]@{})
$templateUris = @($resourceTemplates.resourceTemplates | ForEach-Object { [string]$_.uriTemplate })
foreach ($requiredTemplate in '2b2tatlas://location/{id}', '2b2tatlas://group/{id}', '2b2tatlas://highway/{id}') {
    if ($requiredTemplate -notin $templateUris) { throw "MCP resource template is missing: $requiredTemplate" }
}

[ordered]@{
    endpoint = $Endpoint
    server = [string]$initialize.serverInfo.name
    version = [string]$initialize.serverInfo.version
    protocol = [string]$initialize.protocolVersion
    toolCount = $tools.Count
    locationCount = [int]$statsCall.structuredContent.locations
    groupCount = [int]$statsCall.structuredContent.groups
    resourceTemplateCount = $templateUris.Count
    status = 'healthy'
} | ConvertTo-Json
