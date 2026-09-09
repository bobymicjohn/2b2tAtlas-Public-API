$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'invoke-atlas-bluemap-render.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'BlueMap script parse failed.' }
$function = $ast.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-AtlasWorldRelight'}, $true)
Invoke-Expression $function.Extent.Text
$RelightToolRoot = 'D:\AtlasExample\Ingest\bluemap-tools'
function Get-OrDownloadPinnedArtifact {
    param($Path,$Uri,$Sha256)
    $script:selectedArtifact = [IO.Path]::GetFileName($Path)
    throw 'fixture-stop-before-io'
}
foreach ($case in @(@{version='';expected='paper-1.21.10-130.jar'},@{version='1.21.11';expected='paper-1.21.11-132.jar'})) {
    $script:selectedArtifact = $null
    try { Invoke-AtlasWorldRelight -ScratchJob 'D:\fixture' -ExtractedWorldRoot 'D:\fixture\world' -Dimension overworld -SourceVersionName $case.version -RenderId 41 }
    catch { if ($_.Exception.Message -ne 'fixture-stop-before-io') { throw } }
    if ($script:selectedArtifact -ne $case.expected) { throw 'Incorrect relight runtime selected.' }
}
'Legacy empty version and modern version select the correct pinned runtime.'
