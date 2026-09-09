[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$lock = $null
try {
    try { $lock = [IO.File]::Open('C:\AtlasExample\Recovery\layout-completion.lock', 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { return }
    & python (Join-Path $PSScriptRoot 'watch-atlas-storage-completion.py') --run
    if ($LASTEXITCODE -ne 0) { throw "Storage completion monitor failed ($LASTEXITCODE)." }
} finally { if ($null -ne $lock) { $lock.Dispose() } }
