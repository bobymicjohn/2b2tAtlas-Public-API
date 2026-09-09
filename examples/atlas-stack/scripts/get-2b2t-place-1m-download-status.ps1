param(
    [int]$RpcPort = 9092
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rpcUri = "http://127.0.0.1:$RpcPort/transmission/rpc"
$expectedInfoHash = '7ebd1291770e88ce41fa250463fac8e8610ebcb2'
$sessionId = $null
$statusNames = @{
    0 = 'stopped'
    1 = 'queued-to-verify'
    2 = 'verifying'
    3 = 'queued-to-download'
    4 = 'downloading'
    5 = 'queued-to-seed'
    6 = 'seeding'
}

try {
    Invoke-WebRequest -Uri $rpcUri -Method Post -Body '{}' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 20 | Out-Null
}
catch {
    $response = $_.Exception.Response
    if ($null -eq $response -or $null -eq $response.Headers) {
        throw "The isolated preservation downloader did not answer RPC within 20 seconds. The transfer process may still be active: $($_.Exception.Message)"
    }
    $sessionId = $response.Headers['X-Transmission-Session-Id']
    if ([string]::IsNullOrWhiteSpace($sessionId)) {
        throw
    }
}

if ([string]::IsNullOrWhiteSpace($sessionId)) {
    throw 'The isolated preservation downloader is not responding.'
}

$body = @{
    method = 'torrent-get'
    arguments = @{
        fields = @(
            'id', 'name', 'hashString', 'status', 'totalSize', 'haveValid',
            'leftUntilDone', 'percentDone', 'downloadDir', 'rateDownload',
            'rateUpload', 'eta', 'peersConnected', 'peersSendingToUs',
            'error', 'errorString', 'addedDate', 'activityDate'
        )
    }
} | ConvertTo-Json -Depth 8 -Compress

$response = Invoke-RestMethod -Uri $rpcUri -Method Post -Headers @{ 'X-Transmission-Session-Id' = $sessionId } -Body $body -ContentType 'application/json' -TimeoutSec 20
$torrent = @($response.arguments.torrents) |
    Where-Object { $_.hashString -eq $expectedInfoHash } |
    Select-Object -First 1

if (-not $torrent) {
    throw 'The 2b2t.place 1M torrent is not registered in the isolated downloader.'
}

[pscustomobject]@{
    Name = $torrent.name
    InfoHash = $torrent.hashString
    State = if ($statusNames.ContainsKey([int]$torrent.status)) { $statusNames[[int]$torrent.status] } else { "unknown-$($torrent.status)" }
    TotalTiB = [math]::Round([double]$torrent.totalSize / 1TB, 3)
    VerifiedTiB = [math]::Round([double]$torrent.haveValid / 1TB, 3)
    RemainingTiB = [math]::Round([double]$torrent.leftUntilDone / 1TB, 3)
    PercentDone = [math]::Round([double]$torrent.percentDone * 100, 4)
    DownloadMiBPerSecond = [math]::Round([double]$torrent.rateDownload / 1MB, 2)
    UploadMiBPerSecond = [math]::Round([double]$torrent.rateUpload / 1MB, 2)
    EtaSeconds = $torrent.eta
    PeersConnected = $torrent.peersConnected
    PeersSending = $torrent.peersSendingToUs
    Error = $torrent.error
    ErrorMessage = $torrent.errorString
    DownloadRoot = $torrent.downloadDir
    AddedUtc = if ($torrent.addedDate -gt 0) { [DateTimeOffset]::FromUnixTimeSeconds([long]$torrent.addedDate).UtcDateTime } else { $null }
    LastActivityUtc = if ($torrent.activityDate -gt 0) { [DateTimeOffset]::FromUnixTimeSeconds([long]$torrent.activityDate).UtcDateTime } else { $null }
}
