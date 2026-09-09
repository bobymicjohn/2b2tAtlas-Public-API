param(
    [string]$TaskName = '2b2t Atlas 1M Preservation Download',
    [string]$LauncherPath = (Join-Path $PSScriptRoot 'start-2b2t-place-1m-download.ps1')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedLauncher = (Resolve-Path -LiteralPath $LauncherPath).Path
$arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$resolvedLauncher`""
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$intervalTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 15)
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Limited

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger @($logonTrigger, $intervalTrigger) `
    -Settings $settings `
    -Principal $principal `
    -Description 'Resumes the isolated official 2b2t.place 1M preservation torrent on X. This is not Atlas ingestion.' `
    -Force | Out-Null

Get-ScheduledTask -TaskName $TaskName

