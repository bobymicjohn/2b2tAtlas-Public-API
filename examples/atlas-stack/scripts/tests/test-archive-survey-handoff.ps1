$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'archive-survey-handoff.ps1')
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
$now=[datetimeoffset]::UtcNow
$r=[pscustomobject]@{schemaVersion=1;planningOnly=$true;server='thearchive.world';warp='Base_2022-07-20';dimension='minecraft:overworld';x=-111;z=222;createdUtc=$now.AddHours(-1).ToString('o')}
Assert (Test-SurveyHandoffIdentity $r 'thearchive.world' 'Base_2022-07-20' 'minecraft:overworld' -111 222 $now) 'Exact dated identity rejected'
Assert (-not (Test-SurveyHandoffIdentity $r 'elsewhere' 'Base_2022-07-20' 'minecraft:overworld' -111 222 $now)) 'Server mismatch accepted'
Assert (-not (Test-SurveyHandoffIdentity $r 'thearchive.world' 'Base_2023-07-20' 'minecraft:overworld' -111 222 $now)) 'Snapshot mismatch accepted'
Assert (-not (Test-SurveyHandoffIdentity $r 'thearchive.world' 'Base_2022-07-20' 'minecraft:the_nether' -111 222 $now)) 'Dimension mismatch accepted'
Assert (-not (Test-SurveyHandoffIdentity $r 'thearchive.world' 'Base_2022-07-20' 'minecraft:overworld' -112 222 $now)) 'Landing mismatch accepted'
Assert (Test-SurveyHandoffIdentity $r 'thearchive.world' 'Base_2022-07-20' 'minecraft:overworld' -111 222 $now.AddDays(30)) 'Queued dated snapshot lost progress after 24 hours'
$r.schemaVersion=2; $r.planningOnly=$false
Assert (Test-SurveyHandoffIdentity $r 'thearchive.world' 'Base_2022-07-20' 'minecraft:overworld' -111 222 $now) 'Full checkpoint rejected'
Assert (-not (Test-SurveyHandoffIdentity $r 'thearchive.world' 'Base_2022-07-20' 'minecraft:overworld' -111 222 $now.AddDays(-1))) 'Future checkpoint accepted'
$r.warp='UndatedBase'
Assert (-not (Test-SurveyHandoffIdentity $r 'thearchive.world' 'UndatedBase' 'minecraft:overworld' -111 222 $now)) 'Undated hint accepted'
Assert ((Get-SurveyHandoffDirectory 'thearchive.world' '../Base') -match '^D:\\AtlasExample\\Ingest\\DeferredCaptures\\[a-f0-9]{64}$') 'Handoff identity escaped private store'
'PASS: exact snapshot/server/dimension/landing, long-queued dated checkpoints, future rejection, and path isolation.'
