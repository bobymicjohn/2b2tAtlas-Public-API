[CmdletBinding()]
param(
    [string]$ReadyRoot = 'C:\AtlasExample\Ingest\archive-sync\ready',
    [string[]]$ReadyFiles = @(),
    [string]$IntakeRoot = '',
    [string]$ApiBase = 'http://127.0.0.1:5297',
    [string]$WorkerConfigPath = 'C:\AtlasExample\Ingest\config\worker.json',
    [string]$StatePath = 'C:\AtlasExample\Ingest\archive-sync\state.json',
    [int]$MinimumStableSeconds = 120,
    [int]$DelayBetweenJobsSeconds = 2,
    [ValidateRange(0, 2000000000)]
    [int]$AdaptiveBackgroundRadiusBlocks = 0,
    [ValidateRange(1, 10)]
    [int]$SubmissionAttempts = 5,
    [ValidateRange(1, 60)]
    [int]$SubmissionRetryDelaySeconds = 2,
    [switch]$AllowAdaptivePolicyOverride,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'archive-json-io.ps1')

function Read-JsonUtf8([string]$Path) {
    return [IO.File]::ReadAllText($Path, (New-Object Text.UTF8Encoding($false))) | ConvertFrom-Json
}

function Get-HttpErrorDetail([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    $message = $ErrorRecord.Exception.Message
    $response = $ErrorRecord.Exception.Response
    if ($null -eq $response) { return $message }

    try {
        $stream = $response.GetResponseStream()
        if ($null -eq $stream) { return $message }
        $reader = New-Object System.IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
        try {
            $body = $reader.ReadToEnd().Trim()
            if (-not [string]::IsNullOrWhiteSpace($body)) { return "$message Response: $body" }
        } finally {
            $reader.Dispose()
        }
    } catch { }
    return $message
}

function Test-RetryableSubmissionFailure([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    $response = $ErrorRecord.Exception.Response
    if ($null -ne $response) {
        try {
            $statusCode = [int]$response.StatusCode
            return $statusCode -in @(408, 429, 500, 502, 503, 504)
        } catch { }
    }

    return $ErrorRecord.Exception -is [System.Net.WebException] -and
        $ErrorRecord.Exception.Status -in @(
            [System.Net.WebExceptionStatus]::ConnectFailure,
            [System.Net.WebExceptionStatus]::ConnectionClosed,
            [System.Net.WebExceptionStatus]::KeepAliveFailure,
            [System.Net.WebExceptionStatus]::NameResolutionFailure,
            [System.Net.WebExceptionStatus]::ReceiveFailure,
            [System.Net.WebExceptionStatus]::SendFailure,
            [System.Net.WebExceptionStatus]::Timeout
        )
}

function ConvertTo-AtlasSlug([string]$Value, [string]$Digest) {
    $stem = ($Value.ToLowerInvariant() -replace '[^a-z0-9]+', '-').Trim('-')
    if ([string]::IsNullOrWhiteSpace($stem)) { $stem = 'archive-wdl' }
    $suffix = $Digest.Substring(0, 10)
    $maximumStem = 54 - $suffix.Length - 1
    if ($stem.Length -gt $maximumStem) { $stem = $stem.Substring(0, $maximumStem).TrimEnd('-') }
    return "$stem-$suffix"
}

function ConvertTo-DisplayName([string]$BaseName) {
    $value = ($BaseName -replace '[_-]+', ' ' -replace '\s+', ' ').Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { return 'The Archive world download' }
    if ($value.Length -gt 90) { $value = $value.Substring(0, 90).Trim() }
    return $value
}

function Get-ArchiveDate([string]$BaseName, [System.IO.FileInfo]$File) {
    $match = [regex]::Match($BaseName, '(?<!\d)(20\d{2}|19\d{2})[-_. ](0[1-9]|1[0-2])[-_. ]([0-2]\d|3[01])(?!\d)')
    if ($match.Success) {
        return '{0}-{1}-{2}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value
    }
    return $File.LastWriteTimeUtc.ToString('yyyy-MM-dd')
}

function Save-State([hashtable]$State, [string]$Path) {
    Write-AtlasJsonAtomically -Value $State -Path $Path -Depth 8
}

if (-not (Test-Path -LiteralPath $WorkerConfigPath -PathType Leaf)) {
    throw "Worker config was not found: $WorkerConfigPath"
}
$workerConfig = Read-JsonUtf8 $WorkerConfigPath
$apiBase = $ApiBase.TrimEnd('/')
$IntakeRoot = if ([string]::IsNullOrWhiteSpace($IntakeRoot)) { [string]$workerConfig.intakeRoot } else { $IntakeRoot }
$keyEnvironment = [string]$workerConfig.apiKeyEnvironment
if ([string]::IsNullOrWhiteSpace($apiBase) -or [string]::IsNullOrWhiteSpace($keyEnvironment)) {
    throw 'Worker config must define apiBase and apiKeyEnvironment.'
}
$workerKey = [Environment]::GetEnvironmentVariable($keyEnvironment)
if (-not $DryRun -and [string]::IsNullOrWhiteSpace($workerKey)) {
    throw "Worker key environment variable '$keyEnvironment' is not available to this process."
}
if (-not (Test-Path -LiteralPath $ReadyRoot -PathType Container)) {
    New-Item -ItemType Directory -Path $ReadyRoot -Force | Out-Null
}
if (-not (Test-Path -LiteralPath $IntakeRoot -PathType Container)) {
    throw "Atlas intake root was not found: $IntakeRoot"
}

$state = @{}
if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
    $saved = Read-JsonUtf8 $StatePath
    foreach ($property in $saved.PSObject.Properties) { $state[$property.Name] = $property.Value }
}

$lockPath = "$StatePath.lock"
$lockParent = Split-Path -Parent $lockPath
if (-not (Test-Path -LiteralPath $lockParent)) { New-Item -ItemType Directory -Path $lockParent -Force | Out-Null }
$lock = $null
try {
    $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
} catch [System.IO.IOException] {
    throw 'Another Archive inbox import is already running.'
}

try {
    $cutoff = [DateTime]::UtcNow.AddSeconds(-[Math]::Max(0, $MinimumStableSeconds))
    # Rolling handoff supplies only its durable promotion manifest's paths. Never
    # enumerate or rehash the complete accepted history for each new capture.
    $candidates = if ($PSBoundParameters.ContainsKey('ReadyFiles')) {
        $prefix = [IO.Path]::GetFullPath($ReadyRoot).TrimEnd('\') + '\'
        foreach ($candidate in $ReadyFiles | Select-Object -Unique) {
            $full = [IO.Path]::GetFullPath($candidate)
            if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetExtension($full) -ine '.zip') {
                throw "Import candidate escaped the ready ZIP root: $candidate"
            }
            Get-Item -LiteralPath $full -ErrorAction Stop
        }
    } else { Get-ChildItem -LiteralPath $ReadyRoot -Filter '*.zip' -File -Recurse }
    $files = $candidates |
        Where-Object { $_.LastWriteTimeUtc -le $cutoff } |
        Sort-Object FullName
    $seenDigests = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($file in $files) {
        $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $seenDigests.Add($digest)) {
            Write-Output "SKIP duplicate bytes $($file.FullName) [$($digest.Substring(0, 12))]"
            continue
        }
        if ($state.ContainsKey($digest) -and $state[$digest].status -eq 'accepted') {
            Write-Output "SKIP accepted $($file.FullName) [$($digest.Substring(0, 12))]"
            continue
        }

        $baseName = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
        $captureMetadata = $null
        $metadataPath = "$($file.FullName).metadata.json"
        if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
            $captureMetadata = Read-JsonUtf8 $metadataPath
            if ([int]$captureMetadata.schemaVersion -ne 1) {
                throw "Unsupported Archive capture metadata schema: $metadataPath"
            }
            if ([string]$captureMetadata.sha256 -ne $digest) {
                throw "Archive capture metadata SHA-256 mismatch: $metadataPath"
            }
            $metadataWarp = ([string]$captureMetadata.warp).Trim()
            if ([string]::IsNullOrWhiteSpace($metadataWarp) -or $metadataWarp.Length -gt 240 -or
                [regex]::IsMatch($metadataWarp, '[\x00-\x1F\x7F]')) {
                throw "Archive capture metadata has an invalid warp: $metadataPath"
            }
        }
        $identityName = if ($null -ne $captureMetadata -and -not [string]::IsNullOrWhiteSpace([string]$captureMetadata.warp)) {
            [string]$captureMetadata.warp
        } else { $baseName }
        $displayName = if ($null -ne $captureMetadata -and -not [string]::IsNullOrWhiteSpace([string]$captureMetadata.displayName)) {
            [string]$captureMetadata.displayName
        } else { $identityName }
        $intakeName = "archive-sync-$digest.zip"
        $slug = ConvertTo-AtlasSlug $identityName $digest
        $request = [ordered]@{
            IntakeFileName = $intakeName
            Slug = $slug
            Name = ConvertTo-DisplayName $displayName
            WorldDownloadDate = Get-ArchiveDate $identityName $file
            Source = 'The Archive automated sync'
            Scale = '1'
            DayNight = $true
            # The live Archive catalog date describes the historical WDL/exhibit. The museum
            # client's level.dat LastPlayed value is the capture date and must not replace it.
            UseArchiveLastPlayed = $false
            Dimension = if ($null -ne $captureMetadata -and
                $null -ne $captureMetadata.PSObject.Properties['adaptive'] -and
                [string]$captureMetadata.adaptive.dimension -in @('Overworld', 'Nether', 'End')) {
                ([string]$captureMetadata.adaptive.dimension).ToLowerInvariant()
            } else {
                'auto'
            }
        }
        if ($null -ne $captureMetadata) {
            $request.ArchiveWarpName = ([string]$captureMetadata.warp).Trim()
            $captureMode = ([string]$captureMetadata.captureMode).Trim().ToLowerInvariant()
            if ($captureMode -eq 'adaptive-footprint' -and -not $AllowAdaptivePolicyOverride) {
                $adaptiveProperty = $captureMetadata.PSObject.Properties['adaptive']
                if ($null -eq $adaptiveProperty -or $null -eq $adaptiveProperty.Value) {
                    throw "Adaptive footprint metadata is missing its adaptive evidence: $metadataPath"
                }
                $adaptiveEvidence = $adaptiveProperty.Value
                $confidenceProperty = $adaptiveEvidence.PSObject.Properties['confidence']
                if ($null -eq $confidenceProperty -or [string]$confidenceProperty.Value -ne 'high') {
                    throw "Adaptive footprint is not high-confidence and cannot enter the initial ingestion batch: $metadataPath"
                }

                $distanceFromOrigin = $null
                $positionProperty = $adaptiveEvidence.PSObject.Properties['archiveWarpPosition']
                if ($null -ne $positionProperty -and $null -ne $positionProperty.Value) {
                    $positionValue = $positionProperty.Value
                    $xProperty = $positionValue.PSObject.Properties['x']
                    $yProperty = $positionValue.PSObject.Properties['y']
                    $zProperty = $positionValue.PSObject.Properties['z']
                    if ($null -ne $xProperty -and $null -ne $yProperty -and $null -ne $zProperty) {
                        $request.ArchiveWarpX = [double]$xProperty.Value
                        $request.ArchiveWarpY = [double]$yProperty.Value
                        $request.ArchiveWarpZ = [double]$zProperty.Value
                    }
                    $distanceProperty = $positionProperty.Value.PSObject.Properties['distanceFromOriginBlocks']
                    if ($null -ne $distanceProperty) { $distanceFromOrigin = [double]$distanceProperty.Value }
                }
                if ($null -eq $distanceFromOrigin) {
                    $probeProperty = $adaptiveEvidence.PSObject.Properties['probe']
                    if ($null -ne $probeProperty) {
                        $probeMatch = [regex]::Match([string]$probeProperty.Value,
                            '\sx=(-?\d+(?:\.\d+)?)\sy=-?\d+(?:\.\d+)?\sz=(-?\d+(?:\.\d+)?)\s')
                        if ($probeMatch.Success) {
                            $probeX = [double]::Parse($probeMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
                            $probeZ = [double]::Parse($probeMatch.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
                            $distanceFromOrigin = [Math]::Sqrt($probeX * $probeX + $probeZ * $probeZ)
                        }
                    }
                }
                if ($AdaptiveBackgroundRadiusBlocks -gt 0 -and
                    ($null -eq $distanceFromOrigin -or $distanceFromOrigin -lt $AdaptiveBackgroundRadiusBlocks)) {
                    $distanceText = if ($null -eq $distanceFromOrigin) { 'unknown' } else { '{0:N0}' -f $distanceFromOrigin }
                    throw "Adaptive footprint distance ($distanceText blocks) is not outside the initial $AdaptiveBackgroundRadiusBlocks-block Archive background radius: $metadataPath"
                }

                $policyProperty = $adaptiveEvidence.PSObject.Properties['initialIngestionPolicy']
                if ($AdaptiveBackgroundRadiusBlocks -gt 0 -and
                    $null -ne $policyProperty -and -not [bool]$policyProperty.Value.eligible) {
                    throw "Adaptive footprint was explicitly marked ineligible at capture time: $metadataPath"
                }
            }
        }
        $queueRequest = [ordered]@{
            Metadata = $request
            OriginalFileName = $file.Name
        }

        if ($DryRun) {
            Write-Output ("DRY RUN {0} -> {1}" -f $file.FullName, ($queueRequest | ConvertTo-Json -Depth 6 -Compress))
            continue
        }

        $destination = Join-Path $IntakeRoot $intakeName
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
            $partial = Join-Path $IntakeRoot "$intakeName.$([guid]::NewGuid().ToString('N')).partial"
            Copy-Item -LiteralPath $file.FullName -Destination $partial
            $copiedDigest = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($copiedDigest -ne $digest) {
                Remove-Item -LiteralPath $partial -Force
                throw "Copied archive failed SHA-256 verification: $($file.FullName)"
            }
            Move-Item -LiteralPath $partial -Destination $destination
        } else {
            $existingDigest = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($existingDigest -ne $digest) { throw "Intake filename collision: $destination" }
        }

        try {
            $headers = @{ 'X-Atlas-Worker-Key' = $workerKey }
            $requestJson = $queueRequest | ConvertTo-Json -Depth 6
            $requestBytes = [Text.Encoding]::UTF8.GetBytes($requestJson)
            $response = $null
            for ($submissionAttempt = 1; $submissionAttempt -le $SubmissionAttempts; $submissionAttempt++) {
                try {
                    $response = Invoke-RestMethod -Method Post -Uri "$apiBase/api/ingestion-jobs/local-intake" `
                        -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $requestBytes
                    break
                } catch {
                    if ($submissionAttempt -ge $SubmissionAttempts -or -not (Test-RetryableSubmissionFailure $_)) {
                        throw
                    }
                    $retryDelay = [Math]::Min(30, $SubmissionRetryDelaySeconds * $submissionAttempt)
                    $retryDetail = Get-HttpErrorDetail $_
                    Write-Warning "RETRY $submissionAttempt/$SubmissionAttempts $($file.FullName) in ${retryDelay}s: $retryDetail"
                    Start-Sleep -Seconds $retryDelay
                }
            }
            if ($null -eq $response) { throw "Production intake returned no response after $SubmissionAttempts attempt(s)." }
            $state[$digest] = [ordered]@{
                status = 'accepted'
                sourcePath = $file.FullName
                intakeFileName = $intakeName
                jobId = $response.id
                jobStatus = $response.status
                acceptedUtc = [DateTime]::UtcNow.ToString('o')
            }
            Save-State $state $StatePath
            Write-Output "ACCEPT $($file.FullName) -> job $($response.id) ($($response.status))"
        } catch {
            $errorDetail = Get-HttpErrorDetail $_
            $state[$digest] = [ordered]@{
                status = 'failed'
                sourcePath = $file.FullName
                intakeFileName = $intakeName
                failedUtc = [DateTime]::UtcNow.ToString('o')
                error = $errorDetail
            }
            Save-State $state $StatePath
            Write-Warning "FAILED $($file.FullName): $errorDetail"
        }

        if ($DelayBetweenJobsSeconds -gt 0) { Start-Sleep -Seconds $DelayBetweenJobsSeconds }
    }
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) { Remove-Item -LiteralPath $lockPath -Force }
}
