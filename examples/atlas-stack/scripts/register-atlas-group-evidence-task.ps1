[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TaskName = '2b2t Atlas Group Evidence Refresh',
    [string]$RunAt = '05:25'
)

$ErrorActionPreference = 'Stop'
$script = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'invoke-atlas-group-evidence-refresh.ps1'))
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "Refresh script not found: $script" }
$time = [DateTime]::ParseExact($RunAt, 'HH:mm', [Globalization.CultureInfo]::InvariantCulture)
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument (
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}"' -f $script)
$trigger = New-ScheduledTaskTrigger -Daily -At $time
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 2)

if ($PSCmdlet.ShouldProcess($TaskName, 'Register daily revision-pinned Atlas group evidence refresh')) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
        -Description 'Refreshes the read-only 2b2t Wiki group/build evidence index used by Atlas AI enrichment.' `
        -Force | Out-Null
    Get-ScheduledTask -TaskName $TaskName
}
