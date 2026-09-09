[CmdletBinding()]
param(
    [string]$TaskName = '2b2t Atlas SEO Package',

    [datetime]$At = '06:30',

    [string]$User = $env:USERNAME,

    [string]$RepositoryRoot = 'C:\Source\2b2tAtlas-Public-API\examples\atlas-stack'
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $RepositoryRoot 'scripts\invoke-atlas-seo-package.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "Atlas SEO package script was not found: $scriptPath"
}

$arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f $scriptPath
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments -WorkingDirectory $RepositoryRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$principal = New-ScheduledTaskPrincipal -UserId $User -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Hours 1) -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 10)

Register-ScheduledTask -TaskName $TaskName -Description `
    'Daily change-detected 2b2t Atlas SEO/static entity build. Creates a pending Namecheap ZIP and sends one Pushover alert per new package; public seo-release.json confirms deployment.' `
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
