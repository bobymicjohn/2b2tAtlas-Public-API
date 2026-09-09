function Write-AtlasJsonAtomically {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [ValidateRange(2, 100)]
        [int]$Depth = 24,
        [ValidateRange(1, 50)]
        [int]$MaximumAttempts = 12
    )

    $utf8 = New-Object Text.UTF8Encoding($false)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    # The temporary file must live beside the destination. File.Replace is then
    # an atomic same-volume operation and, unlike Move-Item -Force on Windows
    # PowerShell 5.1, reliably replaces a checkpoint while readers are active.
    $operationId = [guid]::NewGuid().ToString('N')
    $temporary = "$fullPath.$operationId.tmp"
    $backup = "$fullPath.$operationId.bak"
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth $Depth), $utf8)
        for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
            try {
                if ([IO.File]::Exists($fullPath)) {
                    # Windows PowerShell 5.1/.NET Framework rejects a null backup
                    # path even though newer runtimes accept it. A unique sibling
                    # backup preserves atomic replacement and is removed below.
                    [IO.File]::Replace($temporary, $fullPath, $backup, $true)
                } else {
                    [IO.File]::Move($temporary, $fullPath)
                }
                return
            } catch [IO.IOException] {
                if ($attempt -eq $MaximumAttempts) { throw }
                Start-Sleep -Milliseconds ([Math]::Min(500, 20 * $attempt))
            } catch [UnauthorizedAccessException] {
                if ($attempt -eq $MaximumAttempts) { throw }
                Start-Sleep -Milliseconds ([Math]::Min(500, 20 * $attempt))
            }
        }
    } finally {
        # File.Delete is intentionally idempotent. Avoid Test-Path/Remove-Item's
        # check-then-delete race, which can emit spurious errors under heavy
        # concurrent checkpoint traffic even with ErrorAction SilentlyContinue.
        try { [IO.File]::Delete($temporary) } catch { }
        try { [IO.File]::Delete($backup) } catch { }
    }
}

function Read-AtlasJsonWithRetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [switch]$AllowMissing,
        [ValidateRange(1, 50)]
        [int]$MaximumAttempts = 12
    )

    $utf8 = New-Object Text.UTF8Encoding($false)
    $fullPath = [IO.Path]::GetFullPath($Path)
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            if (-not [IO.File]::Exists($fullPath)) {
                if ($AllowMissing) { return $null }
                throw [IO.FileNotFoundException]::new("JSON file was not found: $fullPath", $fullPath)
            }

            # File.Replace briefly swaps the checkpoint name while supervisors
            # and handoff readers are active. Share read/write/delete so a reader
            # can finish the old complete file while the writer atomically installs
            # the new one; retry also covers antivirus/NAS and parse timing races.
            $share = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
            $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
            try {
                $reader = New-Object IO.StreamReader($stream, $utf8, $true)
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            } finally {
                $stream.Dispose()
            }
            if ([string]::IsNullOrWhiteSpace($text)) {
                throw [IO.InvalidDataException]::new("JSON file was empty: $fullPath")
            }
            return $text | ConvertFrom-Json -ErrorAction Stop
        } catch {
            if ($attempt -eq $MaximumAttempts) { throw }
            Start-Sleep -Milliseconds ([Math]::Min(500, 20 * $attempt))
        }
    }
}
