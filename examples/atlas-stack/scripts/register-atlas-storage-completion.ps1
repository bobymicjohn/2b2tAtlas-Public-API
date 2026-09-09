[CmdletBinding()]
param([string]$User = $env:USERNAME)
$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'watch-atlas-storage-completion.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument (
    '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $scriptPath)
$logon = New-ScheduledTaskTrigger -AtLogOn -User $User
$logon.Delay = 'PT4M'
$repeat = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 15)
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 4)
Register-ScheduledTask -TaskName '2b2t Atlas Storage Completion' -Action $action -Trigger @($logon,$repeat) `
    -Principal $principal -Settings $settings -Description `
    'Verify final C/D ingestion, E live data, F publication, X snapshots/restores and live progress; send the owner one completion Pushover.' -Force | Out-Null
Start-ScheduledTask -TaskName '2b2t Atlas Storage Completion'
'Registered storage completion verification every 15 minutes and after logon.'
