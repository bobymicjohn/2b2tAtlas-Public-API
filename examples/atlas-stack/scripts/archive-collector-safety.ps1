# Shared stop latch. A paused fleet must require an explicit operator resume.
function Assert-ArchiveCollectorSafety {
    param([string]$PauseSignalPath, [string[]]$StoragePaths, [int]$MinimumFreeGiB = 100,
          [scriptblock]$ReadFreeGiB = $null)
    if (Test-Path -LiteralPath $PauseSignalPath) {
        throw 'Collector safety hold: operator pause is active; retain the current save.'
    }
    foreach ($path in $StoragePaths) {
        try {
            $free = if ($ReadFreeGiB) { & $ReadFreeGiB $path } else {
                $root = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($path))
                (New-Object IO.DriveInfo($root)).AvailableFreeSpace / 1GB
            }
            if ($null -eq $free -or [double]$free -lt $MinimumFreeGiB) {
                throw "Working storage is below the $MinimumFreeGiB GiB reserve."
            }
        } catch {
            $reason = "Collector safety hold: $($_.Exception.Message)"
            [IO.Directory]::CreateDirectory((Split-Path -Parent $PauseSignalPath)) | Out-Null
            [IO.File]::WriteAllText($PauseSignalPath, ([datetime]::UtcNow.ToString('o') + ' ' + $reason))
            throw $reason
        }
    }
}
