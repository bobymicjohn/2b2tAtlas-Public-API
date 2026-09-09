#!/usr/bin/env python3
"""Apply a reviewed, source-backed description manifest. Dry run by default.

Every row must include the exact old description. A changed name or description
blocks the entire batch. Existing text is saved in both an online SQLite backup
and the audit log; rerunning the same manifest is harmless.
"""
import argparse
import hashlib
import json
import sqlite3
from datetime import datetime, timezone
from pathlib import Path


def validate(connection, rows):
    pending = []
    seen = set()
    for row in rows:
        ident = row['locationId']
        if ident in seen:
            raise ValueError(f'Duplicate location ID: {ident}')
        seen.add(ident)
        if not row.get('description', '').strip() or not row.get('sources'):
            raise ValueError(f'Missing description or sources: {ident}')
        current = connection.execute(
            'SELECT Name, Description FROM Locations WHERE Rowid=?', (ident,)).fetchone()
        if current is None or current[0] != row['locationName']:
            raise ValueError(f'Location identity changed: {ident}')
        if current[1] == row['description']:
            continue
        if current[1] != row['expectedDescription']:
            raise ValueError(f'Description changed since review: {ident}')
        pending.append(row)
    return pending


def apply_manifest(database, manifest, apply=False, backup_dir=None):
    database, manifest = Path(database).resolve(), Path(manifest).resolve()
    payload = manifest.read_bytes()
    rows = json.loads(payload.decode('utf-8'))
    digest = hashlib.sha256(payload).hexdigest()
    connection = sqlite3.connect(database.as_uri() + ('?mode=rw' if apply else '?mode=ro'),
                                 uri=True, timeout=30)
    try:
        pending = validate(connection, rows)
        result = {'reviewed': len(rows), 'pending': len(pending),
                  'alreadyApplied': len(rows) - len(pending), 'applied': 0}
        if not apply or not pending:
            return result
        if backup_dir is None:
            raise ValueError('--backup-dir is required when applying changes')
        backup_dir = Path(backup_dir).resolve()
        backup_dir.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now(timezone.utc).strftime('%Y%m%d-%H%M%S-%f')
        backup = backup_dir / f'atlas-before-description-review-{stamp}.db'
        with sqlite3.connect(backup) as target:
            connection.backup(target)
            if target.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
                raise RuntimeError('Backup failed SQLite integrity check')
        backup_hash = hashlib.sha256(backup.read_bytes()).hexdigest()
        # Recheck under the write lock: an operator may have edited during backup.
        connection.execute('BEGIN IMMEDIATE')
        try:
            pending = validate(connection, rows)
            now = datetime.now(timezone.utc).isoformat()
            for row in pending:
                connection.execute('UPDATE Locations SET Description=?, ModifiedUtc=? WHERE Rowid=?',
                                   (row['description'], now, row['locationId']))
                details = {'before': {'Description': row['expectedDescription']},
                           'after': {'Description': row['description']},
                           'sources': row['sources'], 'manifestSha256': digest,
                           'backupPath': str(backup), 'backupSha256': backup_hash}
                connection.execute('''INSERT INTO AuditLogs
                    (Action, EntityType, EntityId, Username, Summary, DetailsJson, CreatedUtc)
                    VALUES (?, ?, ?, ?, ?, ?, ?)''',
                    ('location.description.research', 'Location', row['locationId'],
                     'local-media-review', f"Reviewed video sources: {row['locationName']}",
                     json.dumps(details, ensure_ascii=False), now))
            connection.commit()
        except BaseException:
            connection.rollback()
            raise
        result.update(applied=len(pending), backupPath=str(backup), backupSha256=backup_hash)
        return result
    finally:
        connection.close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', required=True)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--apply', action='store_true')
    parser.add_argument('--backup-dir')
    args = parser.parse_args()
    print(json.dumps(apply_manifest(args.database, args.manifest, args.apply, args.backup_dir), indent=2))
