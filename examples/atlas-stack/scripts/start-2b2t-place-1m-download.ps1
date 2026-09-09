param(
    [string]$StateRoot = 'C:\AtlasExample\Ingest\external-downloads\2b2t-place-1million-2026',
    [string]$DownloadRoot = 'X:\AtlasExample\WorldDownloads\ExternalArchives\2b2t.place-1million-2026',
    [string]$TransmissionPath = 'C:\Program Files\Transmission\transmission-daemon.exe',
    [int]$RpcPort = 9092,
    [int]$PeerPort = 51414
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$torrentUrl = 'https://2b2t.place/7ebd1291770e88ce41fa250463fac8e8610ebcb2.torrent'
$expectedInfoHash = '7ebd1291770e88ce41fa250463fac8e8610ebcb2'
$expectedTorrentSha256 = '11A494E22C3026B4514DF75AAE4233E549FEF9D8C41A6A67794285860BB97624'
$torrentPath = Join-Path $StateRoot '1million_2b2t.torrent'
$configRoot = Join-Path $StateRoot 'transmission'
$logPath = Join-Path $StateRoot 'transmission.log'
$pidPath = Join-Path $StateRoot 'transmission.pid'
$rpcUri = "http://127.0.0.1:$RpcPort/transmission/rpc"
$registeredTorrentRoot = Join-Path $configRoot 'Torrents'

function Get-TransmissionSessionId {
    param([string]$Uri)

    try {
        Invoke-WebRequest -Uri $Uri -Method Post -Body '{}' -ContentType 'application/json' -UseBasicParsing -TimeoutSec 20 | Out-Null
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response -or $null -eq $response.Headers) {
            throw "Transmission RPC did not answer within 20 seconds: $($_.Exception.Message)"
        }
        $sessionId = $response.Headers['X-Transmission-Session-Id']
        if (-not [string]::IsNullOrWhiteSpace($sessionId)) {
            return $sessionId
        }
        throw
    }

    throw 'Transmission RPC did not request a session ID.'
}

function Invoke-TransmissionRpc {
    param(
        [string]$Method,
        [hashtable]$Arguments = @{}
    )

    $sessionId = Get-TransmissionSessionId -Uri $rpcUri
    $body = @{ method = $Method; arguments = $Arguments } | ConvertTo-Json -Depth 8 -Compress
    Invoke-RestMethod -Uri $rpcUri -Method Post -Headers @{ 'X-Transmission-Session-Id' = $sessionId } -Body $body -ContentType 'application/json' -TimeoutSec 20
}

if (-not (Test-Path -LiteralPath $TransmissionPath -PathType Leaf)) {
    throw "Transmission daemon was not found at $TransmissionPath"
}

New-Item -ItemType Directory -Path $StateRoot -Force | Out-Null
New-Item -ItemType Directory -Path $configRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath 'X:\')) {
    throw 'The X: archive share is unavailable. The preservation download was not started.'
}

New-Item -ItemType Directory -Path $DownloadRoot -Force | Out-Null

if (-not (Test-Path -LiteralPath $torrentPath -PathType Leaf)) {
    Invoke-WebRequest -Uri $torrentUrl -OutFile $torrentPath -UseBasicParsing
}

$torrentSha256 = (Get-FileHash -LiteralPath $torrentPath -Algorithm SHA256).Hash
if ($torrentSha256 -ne $expectedTorrentSha256) {
    throw "The retained 2b2t.place torrent file failed SHA-256 verification: $torrentSha256"
}

$listener = Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -in @('127.0.0.1', '0.0.0.0', '::1', '::') } |
    Select-Object -First 1

if (-not $listener) {
    $arguments = @(
        '--foreground',
        '--config-dir', $configRoot,
        '--download-dir', $DownloadRoot,
        '--rpc-bind-address', '127.0.0.1',
        '--port', [string]$RpcPort,
        '--allowed', '127.0.0.1,::1',
        '--no-auth',
        '--peerport', [string]$PeerPort,
        '--portmap',
        '--dht',
        '--utp',
        '--log-level', 'info',
        '--logfile', $logPath,
        '--pid-file', $pidPath
    )

    Start-Process -FilePath $TransmissionPath -ArgumentList $arguments -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $listener = Get-NetTCPConnection -State Listen -LocalPort $RpcPort -ErrorAction SilentlyContinue |
            Select-Object -First 1
    } while (-not $listener -and (Get-Date) -lt $deadline)

    if (-not $listener) {
        throw "Transmission did not start RPC on port $RpcPort. Review $logPath"
    }
}

# Transmission restores registered torrents from this exact state directory.
# The 15-minute keepalive task only needs to ensure the daemon exists; querying
# torrent details on every invocation can block behind large-torrent disk work.
$registeredTorrent = Get-ChildItem -LiteralPath $registeredTorrentRoot -Filter "$expectedInfoHash*.torrent" -File -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($registeredTorrent) {
    [pscustomobject]@{
        Name = '1million_2b2t'
        InfoHash = $expectedInfoHash
        DaemonState = 'running-registered'
        DownloadRoot = $DownloadRoot
        RpcPort = $RpcPort
    }
    exit 0
}

$response = Invoke-TransmissionRpc -Method 'torrent-get' -Arguments @{
    fields = @('id', 'name', 'hashString', 'status', 'totalSize', 'percentDone', 'downloadDir', 'rateDownload', 'eta', 'peersConnected')
}
$torrent = @($response.arguments.torrents) |
    Where-Object { $_.hashString -eq $expectedInfoHash } |
    Select-Object -First 1

if (-not $torrent) {
    $response = Invoke-TransmissionRpc -Method 'torrent-add' -Arguments @{
        filename = $torrentPath
        'download-dir' = $DownloadRoot
        paused = $false
    }

    $added = $response.arguments.'torrent-added'
    if (-not $added -or $added.hashString -ne $expectedInfoHash) {
        throw 'Transmission did not register the expected 2b2t.place torrent.'
    }

    $response = Invoke-TransmissionRpc -Method 'torrent-get' -Arguments @{
        fields = @('id', 'name', 'hashString', 'status', 'totalSize', 'percentDone', 'downloadDir', 'rateDownload', 'eta', 'peersConnected')
    }
    $torrent = @($response.arguments.torrents) |
        Where-Object { $_.hashString -eq $expectedInfoHash } |
        Select-Object -First 1
}

[pscustomobject]@{
    Name = $torrent.name
    InfoHash = $torrent.hashString
    TotalTiB = [math]::Round([double]$torrent.totalSize / 1TB, 3)
    PercentDone = [math]::Round([double]$torrent.percentDone * 100, 4)
    RateMiBPerSecond = [math]::Round([double]$torrent.rateDownload / 1MB, 2)
    EtaSeconds = $torrent.eta
    Peers = $torrent.peersConnected
    DownloadRoot = $torrent.downloadDir
    RpcPort = $RpcPort
}
