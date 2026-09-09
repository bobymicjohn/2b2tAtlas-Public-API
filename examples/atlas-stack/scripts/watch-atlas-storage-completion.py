"""Durable, fail-closed storage completion checks; invoked by the limited-user task.

Only --run can start existing backup tasks or send the owner completion notification.
--audit-only is read-only except for its private report. No production data is changed.
"""
import argparse
import base64
import datetime as dt
import hashlib
import hmac
import json
import os
from pathlib import Path
import re
import shutil
import sqlite3
import subprocess
import time
import urllib.parse
import urllib.request

ROOT = Path('C:/AtlasRecovery')
STATE = ROOT / 'layout-completion-status.json'
SCRIPTS = Path(__file__).resolve().parent
NAS = r'\\192.0.2.10\public\AtlasBackups'
RESTIC = r'C:\AtlasExample\Ops\tools\restic\restic_0.19.1_windows_amd64.exe'
PASSWORD = r'C:\AtlasExample\Api\config\backup-password.txt'
REQUIRED = [r'E:\AtlasExample\HistoricalMedia', r'E:\AtlasExample\WorldDownloads\objects',
            r'E:\AtlasExample\WorldDownloads\collector', r'E:\AtlasExample\WorldDownloads\manual-primary',
            r'D:\AtlasExample\Ingest\DeferredCaptures', r'E:\2b2t\Exploits', r'E:\2b2t\WorldDownloads',
            r'E:\2b2t\Personal World Downloads', r'E:\2b2t\RawRenders', r'E:\2b2t\MiscRenders',
            r'E:\2b2t\256k Journey Map Data', r'E:\2b2t\AtlasTiles',
            r'F:\AtlasExample\AtlasTiles', r'F:\AtlasExample\AtlasBlueMap']


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def date(value):
    # .NET/restic write 7-9 fractional digits; Python 3.10 accepts at most six.
    return dt.datetime.fromisoformat(re.sub(r'(\.\d{6})\d+', r'\1', value).replace('Z', '+00:00'))


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def save(path, data):
    temp = Path(str(path) + '.tmp')
    temp.write_text(json.dumps(data, indent=2), encoding='utf-8')
    os.replace(temp, path)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def powershell(code):
    result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command',
                             "$ErrorActionPreference='Stop'; " + code],
                            capture_output=True, text=True, timeout=90)
    require(result.returncode == 0, 'PowerShell operation failed: ' + result.stderr[-700:])
    return result.stdout.strip()


def restic(repo, *args, output=None):
    result = subprocess.run([RESTIC, '--repo', repo, '--password-file', PASSWORD,
                             '--cache-dir', str(ROOT / 'cache'), *args],
                            stdout=output or subprocess.PIPE, stderr=subprocess.PIPE, timeout=3600)
    require(result.returncode == 0, 'Restic verification failed: ' + result.stderr.decode(errors='replace')[-700:])
    return result.stdout


def snapshot(repo, tier, since):
    snapshots = json.loads(restic(repo, 'snapshots', '--json', '--tag', 'atlas-' + tier))
    candidates = [s for s in snapshots if s['hostname'].lower() == 'example-host' and date(s['time']) >= date(since)]
    require(candidates, 'No NAS snapshot started after the required completion boundary: ' + tier)
    return max(candidates, key=lambda s: date(s['time']))


def collector_recovery_status(recovery, parallel):
    pending_reload = []
    for worker in parallel['workers']:
        path = Path(worker.get('stdout', ''))
        text = path.read_text(encoding='utf-8-sig', errors='replace') if path.is_file() else ''
        markers = recovery.get('requiredWorkerMarkers', ['COLLECTOR-SAFETY interrupted-retention-v1:'])
        if not all(marker in text for marker in markers):
            pending_reload.append(worker['id'])
    pending_captures = []
    for request in recovery['captures']:
        entries = read(request['statePath'])['entries']
        record = next((entry for entry in entries if entry['warp'] == request['warp']), {})
        adaptive = record.get('adaptive', {})
        if (record.get('status') not in ('captured', 'ready') or
                adaptive.get('standardVersion', 0) < 2 or not adaptive.get('componentSelection')):
            pending_captures.append(request['warp'])
    return dict(pendingSafetyReload=pending_reload, pendingCaptureRecovery=pending_captures)


def preservation_ready(status, ready):
    return (status.get('state') == 'verified' and
            status.get('repository', '').lower() == (NAS + r'\restic-preservation').lower() and
            date(status['startedUtc']) >= date(ready))


def validate_snapshot(snap, since):
    require(date(snap['time']) >= date(since), 'Preservation snapshot predates final layout')
    actual = {p.lower() for p in snap['paths']}
    require(all(p.lower() in actual for p in REQUIRED), 'Preservation snapshot omits required live roots')
    require(snap.get('summary', {}).get('total_files_processed', 0) > 0, 'Empty preservation snapshot')


def fetch(url, bearer=None, byte_range=False):
    headers = {'User-Agent': 'AtlasStorageCompletion/1.0'}
    if bearer:
        require(url.startswith('http://127.0.0.1:5297/'), 'Owner token is local-only')
        headers['Authorization'] = 'Bearer ' + bearer
    if byte_range:
        headers['Range'] = 'bytes=0-1023'
    with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=45) as response:
        require(response.status == (206 if byte_range else 200), 'Unexpected HTTP status for ' + url)
        data = response.read(2 * 1024 * 1024)
        require(data, 'Empty HTTP response for ' + url)
        return data


def owner_token(db):
    text = Path('C:/AtlasApi/config/secrets.ps1').read_text(encoding='utf-8-sig')
    env = {n: v for n, q, v in re.findall(r'''\$env:([A-Za-z0-9_]+)\s*=\s*(['"])(.*?)\2''', text)}
    key = env['JwtSettings__SecretKey'].encode()
    uid, name, password = db.execute('SELECT Id,Username,PasswordHash FROM Users WHERE Id=1').fetchone()
    require(name == 'atlas-owner', 'Unexpected owner identity')
    stamp = hmac.new(key, f'atlas-session-v1:{uid}:{password}'.encode(), hashlib.sha256).hexdigest().upper()
    claims = {'iss': env.get('JwtSettings__Issuer', '2b2tAtlas'),
              'aud': env.get('JwtSettings__Audience', '2b2tAtlas'), 'exp': int(time.time()) + 180,
              'nbf': int(time.time()) - 10, 'atlas_session': stamp,
              'http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier': str(uid)}
    b64 = lambda data: base64.urlsafe_b64encode(data).rstrip(b'=').decode()
    body = b64(b'{"alg":"HS256","typ":"JWT"}') + '.' + b64(json.dumps(claims).encode())
    return body + '.' + b64(hmac.new(key, body.encode(), hashlib.sha256).digest())


def audit(since, before=None, strict=True):
    config = read('C:/AtlasIngest/config/worker.json')
    expected = dict(intakeRoot=r'D:\AtlasExample\Ingest\intake', workRoot=r'D:\AtlasExample\Ingest\work',
                    archiveRoot=r'E:\AtlasExample\WorldDownloads', publishRoot=r'F:\AtlasExample\AtlasTiles')
    require(all(config[k].lower() == v.lower() for k, v in expected.items()), 'Worker storage layout differs')
    require(Path('F:/AtlasIngest/work').resolve() == Path('D:/AtlasIngest/work').resolve(), 'Work compatibility junction differs')
    require(all(Path(p).is_dir() for p in REQUIRED), 'Required live data directory missing')
    free = {drive: shutil.disk_usage(drive + ':/').free for drive in ['C', 'D', 'E', 'F', 'X']}
    require(all(free[d] > limit * 1024**3 for d, limit in [('C', 30), ('D', 100), ('E', 100), ('F', 100), ('X', 1024)]), 'Storage headroom below operating floor')
    db = sqlite3.connect('file:C:/AtlasApi/data/atlas.db?mode=ro', uri=True, timeout=20)
    try:
        require(db.execute('PRAGMA quick_check').fetchone()[0] == 'ok', 'Live SQLite quick_check failed')
        token = owner_token(db)
        collector = json.loads(fetch('http://127.0.0.1:5297/api/admin/collector', token))
        blue = json.loads(fetch('http://127.0.0.1:5297/api/admin/bluemap', token))
        recovery = json.loads(fetch('http://127.0.0.1:5297/api/admin/recovery', token))
        require(collector['available'] and not collector['stale'], 'Collector status unavailable or stale')
        if collector['remaining'] > 0:
            require(collector['activeWorkers'] == 6, 'Not all six collector lanes are active')
            require(all(w['running'] and not w['restarting'] and w['outcome'] == 'live' for w in collector['workers']), 'Collector lane is not capturing normally')
        require(blue['available'] and not blue['stale'], 'BlueMap status unavailable or stale')
        require(blue['remainingRenderCount'] == 0 or blue['active'], 'BlueMap queue is not running')
        issues = []
        if blue['failedRenderCount']:
            issues.append('BlueMap has outstanding failed renders awaiting recovery')
        require(not strict or not issues, '; '.join(issues))
        require(not next(b['needsAttention'] for b in recovery['backups'] if b['tier'] == 'Metadata'), 'Metadata backup health needs attention')
        failed = db.execute("SELECT PublicId FROM IngestionJobs WHERE Status='failed' AND julianday(UpdatedUtc)>=julianday(?)", (since,)).fetchall()
        require(not failed, 'Ingestion failures since layout boundary: ' + str(len(failed)))
        expired = db.execute("SELECT count(*) FROM IngestionJobs WHERE Status NOT IN ('completed','failed','cancelled','needs-match','queued') AND LeaseExpiresUtc IS NOT NULL AND julianday(LeaseExpiresUtc)<julianday('now','-10 minutes')").fetchone()[0]
        require(expired == 0, 'Ingestion has expired active leases')
        hashes = [r[0] for r in db.execute("SELECT DISTINCT ArchiveSha256 FROM IngestionJobs WHERE Status='completed' AND ArchiveSha256 IS NOT NULL")]
        require(all(re.fullmatch('[0-9a-f]{64}', h) and (Path(expected['archiveRoot']) / 'objects' / h[:2] / (h + '.zip')).is_file() for h in hashes), 'A completed job is missing its E canonical WDL')
        boundary_column = 'RequestedUtc' if strict else 'CompletedUtc'
        row = db.execute(f"""SELECT j.PublicId,j.ArchiveSha256,j.CompletedUtc,j.Dimension,r.Id,r.TilesPath,r.ArchiveWarpId
            FROM IngestionJobs j JOIN Renders r ON r.Id=j.RenderId
            WHERE j.Status='completed' AND r.IsPublic=1 AND julianday(j.{boundary_column})>=julianday(?)
            AND julianday(j.CompletedUtc)<=julianday(?) ORDER BY j.CompletedUtc DESC LIMIT 1""", (since, before or utc())).fetchone()
        require(row, 'Waiting for a new capture to complete ingestion and public publication after the layout boundary')
        job, digest, completed, dimension, render_id, tile_url, warp_id = row
        local_job = Path(expected['workRoot']) / (dimension or 'overworld') / digest / 'job-state.json'
        require(local_job.is_file(), 'New publication has no D processing receipt')
        require(read(local_job).get('stage') == 'published', 'D processing receipt is not published')
        prefix = tile_url.split('{')[0].rstrip('/')
        require(prefix.startswith('https://tiles.atlas.example/AtlasTiles/'), 'Unexpected public tile root')
        tile_root = Path(expected['publishRoot']) / prefix.split('/AtlasTiles/')[1]
        tile = next(tile_root.glob('*/*/*/*.png'), None)
        require(tile, 'New publication has no tile')
        actual_url = prefix + '/' + tile.relative_to(tile_root).as_posix()
        require(fetch(actual_url) == tile.read_bytes(), 'Public tile differs from F publication')
        wdl = Path(expected['archiveRoot']) / 'objects' / digest[:2] / (digest + '.zip')
        with wdl.open('rb') as stream:
            first = stream.read(1024)
        for base in ['http://127.0.0.1:5297', 'http://127.0.0.1:5297']:
            route = f'/api/warps/{warp_id}' if warp_id is not None else f'/api/renders/{render_id}'
            require(fetch(base + route + '/world-download.zip', byte_range=True) == first, 'Served WDL differs from E source')
        for ext in ['png', 'webp']:
            fetch('https://tiles.atlas.example/2b2t/MiscRenders/LocationAttachments/imperators-base/imperators-base.' + ext)
        blue_root = Path('F:/2b2t/AtlasBlueMap/location-renders')
        blue_manifest = next(p for p in blue_root.glob('*/manifest.json') if read(p).get('Status') == 'complete' and read(p).get('QualityGate', {}).get('Passed'))
        blue_web = blue_manifest.parent / 'web/index.html'
        blue_url = 'http://127.0.0.1:5297/bluemap/' + blue_manifest.parent.name + '/web/'
        require(b'id="map-container"' in fetch(blue_url + 'index.html'), 'Public BlueMap client did not load')
        blue_settings = blue_manifest.parent / 'web/maps/atlas/settings.json'
        require(fetch(blue_url + 'maps/atlas/settings.json') == blue_settings.read_bytes(), 'Public BlueMap map settings differ from F publication')
        return dict(checkedUtc=utc(), issues=issues, layout=expected, freeBytes=free, canonicalWdlReferences=len(hashes),
                    collectors=collector, blueMap=blue, renderId=render_id, jobId=job, completedUtc=completed,
                    samples=[str(wdl), str(tile), str(blue_web),
                    'E:/2b2tAtlas/HistoricalMedia/2b2t-wiki/objects/00/006851f35781de5b66ee319b679284c0892854d7a6a6fb81b40d05d8036eda8b.png'])
    finally:
        db.close()


def dump_verify(repo, snap, source, folder, database=False):
    source = Path(source)
    # Windows native restic source C:\... is stored below /C/...
    stored = '/' + source.drive.rstrip(':') + '/' + '/'.join(source.parts[1:])
    target = folder / (('db-' if database else '') + hashlib.sha256(stored.encode()).hexdigest()[:12] + source.suffix)
    with target.open('wb') as output:
        restic(repo, 'dump', snap['id'], stored, output=output)
    def sha(path):
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for block in iter(lambda: stream.read(4 * 1024 * 1024), b''):
                digest.update(block)
        return digest.hexdigest()
    digest = sha(target)
    require(digest == sha(source), 'Restored bytes differ: ' + str(source))
    if database:
        with sqlite3.connect('file:' + target.as_posix() + '?mode=ro', uri=True) as db:
            require(db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok', 'Restored database integrity check failed')
            require(db.execute('SELECT count(*) FROM Renders').fetchone()[0] > 0, 'Restored database has no renders')
    return dict(source=str(source), restored=str(target), sha256=digest, bytes=target.stat().st_size, snapshot=snap['id'])


def advancing(prior, current):
    old = {w['id']: (w['warp'], w['chunksReceived'], w['waypoint'], w['completed']) for w in prior['collectors']['workers']}
    return all(w['outcome'] == 'complete' or old.get(w['id']) != (w['warp'], w['chunksReceived'], w['waypoint'], w['completed']) for w in current['collectors']['workers']) and (
        current['blueMap']['remainingRenderCount'] == 0 or current['blueMap']['validatedRenderCount'] > prior['blueMap']['validatedRenderCount'])


def notify_owner(message):
    env = {}
    for line in Path('C:/Scripts/blackbrain/.env').read_text(encoding='utf-8-sig').splitlines():
        match = re.match(r'\s*(PUSHOVER_APP_TOKEN|PUSHOVER_USER_KEY)\s*=\s*(.*?)\s*$', line)
        if match:
            env[match[1]] = match[2].strip('"\'')
    body = urllib.parse.urlencode(dict(token=env['PUSHOVER_APP_TOKEN'], user=env['PUSHOVER_USER_KEY'],
                                      title='Atlas storage migration verified', message=message, sound='pushover')).encode()
    with urllib.request.urlopen('https://api.pushover.net/1/messages.json', data=body, timeout=30) as response:
        require(json.load(response).get('status') == 1, 'Pushover did not confirm completion alert')


def run():
    state = read(STATE) if STATE.exists() else {}
    if state.get('notifiedUtc'):
        return
    state['checkedUtc'] = utc()
    try:
        cutover = read(ROOT / 'local-ingestion-cutover.json')
        require(cutover['state'] == 'deployed' and cutover['livePublicationVerified'], 'Local worker cutover incomplete')
        require(read(ROOT / 'storage-e-cutover.json')['state'] == 'verified', 'E data migration incomplete')
        deferred = read(ROOT / 'deferred-scratch-cutover.json.status.json')
        if deferred['state'] != 'complete':
            # Restart only the safe-boundary migration helper, never an active collector.
            helper = str(SCRIPTS / 'complete-atlas-deferred-scratch-cutover.ps1')
            powershell("if (-not (Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { $_.CommandLine -like '*-File*complete-atlas-deferred-scratch-cutover.ps1*' -and $_.ProcessId -ne $PID })) { Start-Process powershell.exe -WindowStyle Hidden -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"" + helper + "\"' -RedirectStandardOutput C:\\AtlasExample\\Recovery\\deferred-scratch-cutover.out.log -RedirectStandardError C:\\AtlasExample\\Recovery\\deferred-scratch-cutover.err.log }")
            state.update(state='waiting-for-collectors', pendingCollectors=len(deferred.get('pendingCollectors', [])))
            return
        state['pendingCollectors'] = 0
        ready = max(cutover['completedUtc'], deferred['updatedUtc'], key=date)
        recovery_path = ROOT / 'collector-disconnect-recovery.json'
        if recovery_path.is_file():
            recovery = read(recovery_path)
            progress = collector_recovery_status(recovery, read(recovery['parallelStatusPath']))
            state.update(progress)
            if progress['pendingSafetyReload'] or progress['pendingCaptureRecovery']:
                state.update(state='waiting-for-collector-recovery', layoutReadyUtc=ready)
                return
            if not recovery.get('verifiedUtc'):
                recovery['verifiedUtc'] = utc()
                save(recovery_path, recovery)
            ready = max(ready, recovery['verifiedUtc'], key=date)
        state['layoutReadyUtc'] = ready
        status = read(ROOT / 'Preservation-status.json')
        if not preservation_ready(status, ready):
            state['state'] = 'waiting-for-final-layout-backup'
            if status.get('state') != 'running':
                powershell("if ((Get-ScheduledTask -TaskName '2b2t Atlas Preservation Backup').State -ne 'Running') { Start-ScheduledTask -TaskName '2b2t Atlas Preservation Backup' }")
            else:
                # Recover an interrupted run after reboot; the backup's own lock prevents overlap.
                powershell("if ((Get-ScheduledTask -TaskName '2b2t Atlas Preservation Backup').State -ne 'Running' -and -not (Get-Process restic* -ErrorAction SilentlyContinue)) { Start-ScheduledTask -TaskName '2b2t Atlas Preservation Backup' }")
            return
        preservation = snapshot(NAS + r'\restic-preservation', 'Preservation', ready)
        validate_snapshot(preservation, ready)
        state['preservationSnapshot'] = preservation['id']
        metadata_status = read(ROOT / 'Metadata-status.json')
        if not (metadata_status.get('state') == 'verified' and metadata_status.get('nasRepository') == NAS + r'\restic-metadata' and date(metadata_status['startedUtc']) >= date(status['completedUtc'])):
            state['state'] = 'waiting-for-fresh-nas-metadata'
            powershell("if ((Get-ScheduledTask -TaskName '2b2t Atlas Metadata Backup').State -ne 'Running') { Start-ScheduledTask -TaskName '2b2t Atlas Metadata Backup' }")
            return
        metadata = snapshot(NAS + r'\restic-metadata', 'Metadata', status['completedUtc'])
        state['metadataSnapshot'] = metadata['id']
        state['state'] = 'verifying-live-pipeline'
        current = audit(ready, preservation['time'])
        previous = state.get('healthyObservation')
        if not previous or not advancing(previous, current):
            state.update(state='observing-pipeline-progress', healthyObservation=current)
            return
        require((date(current['checkedUtc']) - date(previous['checkedUtc'])).total_seconds() >= 600, 'Healthy observations must be at least ten minutes apart')
        mcp_script = str(SCRIPTS / 'test-atlas-mcp.ps1').replace("'", "''")
        state['publicMcp'] = json.loads(powershell("& '" + mcp_script + "' -Endpoint http://127.0.0.1:5297/mcp -ExpectedToolCount 18"))
        folder = ROOT / 'layout-completion-restores' / preservation['short_id']
        folder.mkdir(parents=True, exist_ok=True)
        samples = [dump_verify(NAS + r'\restic-preservation', preservation, p, folder) for p in current['samples']]
        database = next(p for p in metadata['paths'] if re.search(r'\\atlas-[0-9-]+\.db$', p))
        samples.append(dump_verify(NAS + r'\restic-metadata', metadata, database, folder, database=True))
        # Restore reads may take time on the NAS. Recheck the live system before
        # announcing success rather than relying on the pre-restore observation.
        final_health = audit(ready, preservation['time'])
        state.update(state='verified', restoreSamples=samples, verifiedUtc=utc(), finalHealth=final_health)
        state.pop('error', None)
        save(STATE, state)  # Durable evidence precedes the external notification.
        notify_owner('Verified: C/D ingestion and staging; E WDLs/media; F published tiles/BlueMap; X NAS backups. '
                     'Fresh preservation + metadata snapshots checked; DB, WDL, image, tile and BlueMap restores passed. '
                     'Collector/BlueMap progress and new ingestion through public downloads passed. '
                     'Report: C:\\AtlasExample\\Recovery\\layout-completion-status.json')
        state['notifiedUtc'] = utc()
    except Exception as error:
        state.update(state='waiting-for-verification', error=str(error), errorUtc=utc())
        state.pop('healthyObservation', None)
    finally:
        save(STATE, state)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--run', action='store_true')
    parser.add_argument('--audit-only', action='store_true')
    args = parser.parse_args()
    if args.run:
        run()
    elif args.audit_only:
        report = audit(read(ROOT / 'local-ingestion-cutover.json')['completedUtc'], strict=False)
        save(ROOT / 'layout-live-audit.json', report)
        print(json.dumps(report, indent=2))
    else:
        parser.error('Choose --run or --audit-only')
