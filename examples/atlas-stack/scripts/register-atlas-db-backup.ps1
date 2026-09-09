<#
    Registers the verified 2b2t Atlas SQLite online backup for 04:20 local time daily.
    The backup script uses SQLite's online backup command, integrity-checks the copy,
    writes a SHA-256 manifest, and retains the newest verified copies.
#>
[CmdletBinding()]
param(
    [string]$User = $env:USERNAME,
    [string]$Script = 'C:\AtlasExample\Ops\backup-atlas-db.ps1',
    [datetime]$At = '04:20'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Script -PathType Leaf)) { throw "Backup script missing: $Script" }

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $Script)
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 1)

Register-ScheduledTask -TaskName '2b2t Atlas DB Backup' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings `
    -Description 'Daily verified online SQLite backup for 2b2t Atlas production.' -Force | Out-Null

Write-Host "Registered '2b2t Atlas DB Backup' (daily at $($At.ToString('HH:mm')), as $User)."
