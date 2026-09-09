<#
    Registers the "Atlas Example Watchdog" scheduled task, which runs
    C:\AtlasExample\Ops\atlas-ensure-running.ps1 every 5 minutes (and shortly after logon)
    to relaunch the Atlas API/worker if either process has died.

    Runs as atlas-operator with Interactive logon (same as the API/worker tasks), so NO
    stored password is required — it relies on the box's existing AutoAdminLogon.
    Idempotent: re-run any time to update the definition.
#>
[CmdletBinding()]
param(
    [string]$User   = $env:USERNAME,
    [string]$Script = 'C:\AtlasExample\Ops\atlas-ensure-running.ps1',
    [int]$IntervalMinutes = 5,
    [int]$InitialDelayMinutes = 4
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Script)) { throw "Watchdog script missing: $Script" }

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $Script)

# Task Scheduler does not persist a repetition pattern transplanted onto a logon
# trigger. Keep the delayed logon start and add a real repeating time trigger.
$logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $User
$logonTrigger.Delay = ('PT{0}M' -f $InitialDelayMinutes)
$repeatTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)

$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew `
    -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName 'Atlas Example Watchdog' -Action $action -Trigger @($logonTrigger, $repeatTrigger) `
    -Principal $principal -Settings $settings -Description `
    'Self-heals the Atlas Example API and WDL worker: relaunches either if its process has died.' -Force | Out-Null

Write-Host "Registered 'Atlas Example Watchdog' (delayed logon + every $IntervalMinutes min, as $User)."
