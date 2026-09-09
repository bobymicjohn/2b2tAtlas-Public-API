# Shared stage admission. This file declares helpers only; dot-sourcing is safe.
function Test-BlueMapStageCapacity([long]$Reserved, [long]$Requested, [long]$DockerTotal, [long]$OtherUsage, [long]$HostAvailable) {
    return $Reserved + $Requested -le 32GB -and
        $Reserved + $Requested + $OtherUsage + 4GB -le $DockerTotal -and
        $HostAvailable -ge $Requested + 4GB
}

function Convert-BlueMapMemoryBytes([string]$Text) {
    if ($Text -notmatch '^\s*([0-9.]+)\s*(GiB|MiB|KiB|B)\s*$') { throw "Unrecognized Docker memory measure: $Text" }
    $factor = switch ($Matches[2]) { 'GiB' {1GB}; 'MiB' {1MB}; 'KiB' {1KB}; 'B' {1} }
    return [long]([double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture) * $factor)
}

function Enter-BlueMapStage([ValidateSet('relighting','rendering')][string]$Stage, [long]$Bytes, [string]$ContainerName = '') {
    $leaseRoot = Join-Path $coordinationRoot 'stage-leases'
    New-Item -ItemType Directory -Path $leaseRoot -Force | Out-Null
    $leasePath = Join-Path $leaseRoot "$PID.json"
    while ($true) {
        Set-WorkerStage "waiting for $Stage resources"
        $gate = $null
        try {
            $gate = [IO.File]::Open((Join-Path $coordinationRoot 'stage-admission.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        } catch [IO.IOException] { Start-Sleep -Milliseconds 250; continue }
        try {
            # Failure to inspect resources is fail-closed. Never infer free capacity.
            $dockerTotal = [long]((& docker info --format '{{.MemTotal}}' | Out-String).Trim())
            if ($LASTEXITCODE -ne 0 -or $dockerTotal -le 0) { throw 'Docker memory capacity unavailable.' }
            $stats = @(& docker stats --no-stream --format '{{json .}}' | ForEach-Object { $_ | ConvertFrom-Json })
            if ($LASTEXITCODE -ne 0) { throw 'Docker memory usage unavailable.' }
            $reserved = 0L
            $ownedNames = @()
            foreach ($file in Get-ChildItem -LiteralPath $leaseRoot -Filter '*.json' -File) {
                $lease = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
                $owner = Get-Process -Id ([int]$lease.OwnerPid) -ErrorAction SilentlyContinue
                $alive = $null -ne $owner -and $owner.StartTime.ToUniversalTime().Ticks -eq [long]$lease.OwnerStartTicks
                # A dead shell can leave a live container. Reserve until watchdog reaps it.
                $containers = @($stats | Where-Object { $_.Name -match ('^atlas-bluemap-(?:relight|render)-\d+-' + $lease.OwnerPid + '-[a-f0-9]+$') })
                if ($alive -or $containers.Count -gt 0) {
                    $reserved += [long]$lease.Bytes
                    $ownedNames += @($containers | ForEach-Object { $_.Name })
                } else { Remove-Item -LiteralPath $file.FullName -Force }
            }
            if (Test-Path -LiteralPath $leasePath) { throw 'Worker already holds a BlueMap stage lease.' }
            $other = 0L
            foreach ($stat in $stats) {
                if ($stat.Name -notin $ownedNames) { $other += Convert-BlueMapMemoryBytes (($stat.MemUsage -split '/')[0]) }
            }
            $available = [long](Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory * 1KB
            if (Test-BlueMapStageCapacity $reserved $Bytes $dockerTotal $other $available) {
                Write-AtomicJson -Path $leasePath -Value @{
                    OwnerPid=$PID; OwnerStartTicks=(Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks
                    WorkerId=$WorkerId; Stage=$Stage; Bytes=$Bytes; ContainerName=$ContainerName; CreatedUtc=[datetime]::UtcNow.ToString('o')
                }
                Set-WorkerStage $Stage
                return $leasePath
            }
        } finally { $gate.Dispose() }
        Start-Sleep -Seconds 10
    }
}

function Exit-BlueMapStage([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    # Keep an orphan's reservation if cleanup failed; the watchdog owns recovery.
    $running = @(& docker ps --format '{{.Names}}')
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Retaining stage reservation: Docker inspection unavailable.'; return }
    if (@($running | Where-Object { $_ -match ('^atlas-bluemap-(?:relight|render)-\d+-' + $PID + '-[a-f0-9]+$') }).Count -gt 0) {
        Write-Warning 'Retaining stage reservation for surviving owned container.'
        return
    }
    # Serialize removal with admission scans so they never observe a file that
    # disappears between enumeration and reading its reservation.
    $gate = $null
    while ($null -eq $gate) {
        try { $gate = [IO.File]::Open((Join-Path $coordinationRoot 'stage-admission.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch [IO.IOException] { Start-Sleep -Milliseconds 250 }
    }
    try { Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue }
    finally { $gate.Dispose() }
}
