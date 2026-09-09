[CmdletBinding()]
param(
    [string]$TaskName = '2b2t Atlas Archive Weekly Sync',
    [ValidateSet('Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday')]
    [string]$DayOfWeek = 'Sunday',
    [datetime]$At = '06:00',
    [string]$User = $env:USERNAME,
    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack'
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $RepositoryRoot 'scripts\invoke-archive-weekly-sync.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { throw "Weekly sync script was not found: $scriptPath" }

$arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $scriptPath
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $RepositoryRoot
$trigger = New-ScheduledTaskTrigger -Weekly -WeeksInterval 1 -DaysOfWeek $DayOfWeek -At $At
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName $TaskName -Description `
    'After the initial Archive pass is complete, refresh /warps weekly, capture only new eligible WDLs, strictly promote them, and submit them to the production Atlas pipeline.' `
    -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null

$task = Get-ScheduledTask -TaskName $TaskName
$info = $task | Get-ScheduledTaskInfo
[pscustomobject]@{
    TaskName = $task.TaskName
    State = $task.State
    Enabled = $task.Settings.Enabled
    NextRunTime = $info.NextRunTime
    User = $task.Principal.UserId
    LogonType = $task.Principal.LogonType
    Action = $task.Actions.Execute + ' ' + $task.Actions.Arguments
}
