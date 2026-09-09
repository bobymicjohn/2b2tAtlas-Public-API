# Deduplicate immutable preservation copies only. Never use on live game saves.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InventoryPath,
    [Parameter(Mandatory=$true)][string]$InterruptedRoot,
    [Parameter(Mandatory=$true)][string]$AuditPath,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath($InterruptedRoot).TrimEnd('\')
if ((Split-Path -Leaf $root) -ne 'interrupted') { throw 'Root must be the private interrupted preservation directory.' }
$inventory = Get-Content -LiteralPath $InventoryPath -Raw | ConvertFrom-Json
if ([IO.Path]::GetFullPath($inventory.root).TrimEnd('\') -ne $root) { throw 'Inventory root mismatch.' }
function Assert-PreservedPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Path escaped preservation root.' }
    $relative = $full.Substring($root.Length+1)
    if ($relative -notmatch '^\d{8}-\d{6}-[a-f0-9]{32}\\archive-[A-Za-z0-9._-]+(?:\\|\.zip$)') { throw 'Not a preserved capture path.' }
    $parent = $full
    while ($parent) {
        $item = Get-Item -LiteralPath $parent -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point in preserved path.' }
        $parent = Split-Path -Parent $parent
    }
    return $full
}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class AtlasPreservedLinks {
    [StructLayout(LayoutKind.Sequential)] struct Info {
        public uint attributes; public System.Runtime.InteropServices.ComTypes.FILETIME creation, access, write;
        public uint volume, sizeHigh, sizeLow, links, indexHigh, indexLow;
    }
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out Info info);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateHardLink(string name, string existing, IntPtr reserved);
    static string Hash(FileStream f) { f.Position=0; using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", ""); }
    public static long Compact(string source, string target, string expected, long bytes) {
        // Reject existing writers and deny new writes during verification.
        // Windows replacement needs closed handles. These preservation copies
        // are immutable; retain the replaced file until post-replacement verification.
        using(var a=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
        using(var b=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete)) {
            Info ai,bi;
            if(!GetFileInformationByHandle(a.SafeFileHandle,out ai)||!GetFileInformationByHandle(b.SafeFileHandle,out bi)) throw new IOException("Cannot inspect file identity");
            if(ai.volume!=bi.volume) throw new IOException("Different volumes");
            if(ai.indexHigh==bi.indexHigh && ai.indexLow==bi.indexLow) return 0;
            if(a.Length!=bytes||b.Length!=bytes || !Hash(a).Equals(expected,StringComparison.OrdinalIgnoreCase) || !Hash(b).Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new IOException("Fresh hash/length mismatch; originals retained");
            string temporary=target+".atlas-link-"+Guid.NewGuid().ToString("N");
            if(!CreateHardLink(temporary,source,IntPtr.Zero)) throw new IOException("CreateHardLink: "+new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
            string backup=target+".atlas-original-"+Guid.NewGuid().ToString("N");
            try {
                a.Dispose(); b.Dispose();
                File.Replace(temporary,target,backup);
                using(var verified=new FileStream(target,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(verified.Length!=bytes || !Hash(verified).Equals(expected,StringComparison.OrdinalIgnoreCase)) throw new IOException("Verification failed; original retained at "+backup);
                    File.Delete(backup);
                }
            } finally { if(File.Exists(temporary)) File.Delete(temporary); }
            return bi.links==1 ? bytes : 0;
        }
    }
}
'@
# Validate the entire plan before the first replacement. Receipts are never edited.
$groups = @($inventory.duplicates | Where-Object { $_.bytes -ge 1MB } | Sort-Object { [long]$_.bytes * (@($_.paths).Count-1) } -Descending)
foreach ($group in $groups) {
    if ($group.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid receipt hash.' }
    foreach ($path in $group.paths) { Assert-PreservedPath $path | Out-Null }
}
$reclaimed = 0L; $count = 0; $lastNotice = [datetime]::UtcNow
if ($Apply) {
    New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($AuditPath))) -Force | Out-Null
    $audit = New-Object IO.StreamWriter([IO.File]::Open($AuditPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read))
    $audit.AutoFlush = $true
}
try {
    foreach ($group in $groups) {
        $paths = @($group.paths | Sort-Object -Unique)
        foreach ($target in @($paths | Select-Object -Skip 1)) {
            if ($Apply) {
                $bytes = [AtlasPreservedLinks]::Compact($paths[0],$target,$group.sha256,[long]$group.bytes)
                $reclaimed += $bytes
                $audit.WriteLine((@{utc=[datetime]::UtcNow.ToString('o');source=$paths[0];target=$target;sha256=$group.sha256;reclaimedBytes=$bytes}|ConvertTo-Json -Compress))
            }
            $count++
            if (([datetime]::UtcNow-$lastNotice).TotalSeconds -gt 20) {
                Write-Output "Checked $count copies; reclaimed $([math]::Round($reclaimed/1GB,2)) GiB."
                $lastNotice=[datetime]::UtcNow
            }
        }
    }
} finally { if ($Apply) { $audit.Dispose() } }
[pscustomobject]@{applied=[bool]$Apply;copies=$count;reclaimedGiB=[math]::Round($reclaimed/1GB,2)}
