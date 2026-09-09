"""Check the staged full-stack source without printing potential secret values."""
import re
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[1]
repo = Path(subprocess.check_output(['git', 'rev-parse', '--show-toplevel'], cwd=root).decode().strip())
prefix = root.relative_to(repo).as_posix() + '/'
paths = subprocess.check_output(['git', 'ls-files', '-z', '--', prefix], cwd=repo).decode().split('\0')
paths = [p for p in paths if p]
if not paths:
    raise SystemExit('Stage the exported source before checking it.')
errors = []
patterns = [
    r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    r'\b(?:gh[pousr]_[A-Za-z0-9]{25,}|github_pat_[A-Za-z0-9_]{35,})',
    r'\bAIza[0-9A-Za-z_-]{35}',
    r'\beyJ[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}\.[A-Za-z0-9_-]{15,}',
    r'(?i)blackportal[.]cloud',
    r'(?i)blackportal\\[.]cloud',
    r'(?i)[A-Z]:[\\/]+Users[\\/]+(?!atlas-operator\b|example\b)[^\\/\s"\']+',
    r'\b10[.]0[.]0[.]\d+\b',
]
for path in paths:
    local = path[len(prefix):]
    if re.search(r'(?i)(?:\.(?:db|sqlite|sqlite3)(?:-wal|-shm)?|\.(?:zip|gz|rar|7z|jar|exe|dll|pfx|p12|pem|key|mca|mcr|dat|log)|(?:^|/)(?:secrets\.(?:ps1|json)|seed-credentials\.txt|backup-password\.txt|\.env))$', local):
        errors.append('Runtime material: ' + local)
    if any(p in {'.git','.local','bin','obj','.history','node_modules','__pycache__'} for p in Path(local).parts):
        errors.append('Private/generated directory: ' + local)
    data = subprocess.check_output(['git', 'show', ':' + path], cwd=repo)
    try: text = data.decode('utf-8-sig')
    except UnicodeDecodeError: continue
    for pattern in patterns:
        for match in re.finditer(pattern, text):
            errors.append(f'Inspect potentially private content: {local}:{text[:match.start()].count(chr(10))+1}')

required = ['2b2tAtlas.sln', 'LICENSE', 'README.md', 'SECURITY.md',
    '2b2tAtlas.Client/Program.cs', '2b2tAtlas.Server/Program.cs',
    '2b2tAtlas.Ingestor/Worker/IngestionWorker.cs',
    'scripts/archive_capture_recovery.py', 'scripts/invoke-archive-collector.ps1',
    'scripts/invoke-atlas-bluemap-render.ps1', 'scripts/restore-atlas-record.py',
    'tools/AtlasArchiveCoverage/src/main/java/com/b2btatlas/archive/coverage/SavedTerrainReader.java']
for name in required:
    if prefix + name not in paths: errors.append('Missing stack source: ' + name)
if errors: raise SystemExit('\n'.join(sorted(set(errors))))
print(f'Public export gate passed: {len(paths)} staged files; required stack source present.')
