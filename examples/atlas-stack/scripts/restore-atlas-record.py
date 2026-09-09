"""Preview or restore one catalog record and its saved relationships without rolling back other records.

Local operator tool only; never exposed through the public API. Existing additions
made after the snapshot are retained. Reused/moved child IDs fail closed.
"""
import argparse, datetime, hashlib, json, pathlib, sqlite3

GRAPHS = {
    'Location': [('Locations', 'Rowid'), ('Warps', 'LocationRowid'), ('Renders', 'LocationRowid'),
                 ('Attachments', 'LocationRowid'), ('LocationGroups', 'LocationRowid')],
    'Group': [('Groups', 'Id'), ('LocationGroups', 'GroupId'), ('HighwayGroups', 'GroupId')],
    'Highway': [('Highways', 'Id'), ('HighwayGroups', 'HighwayId')],
}

def digest(path):
    with open(path, 'rb') as stream:
        result = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b''): result.update(block)
        return result.hexdigest()

def restore(snapshot, expected_hash, database, entity, entity_id, apply=False, recovery_root=None):
    snapshot, database = pathlib.Path(snapshot).resolve(), pathlib.Path(database).resolve()
    if snapshot == database: raise ValueError('Snapshot and target database must differ.')
    if digest(snapshot).lower() != expected_hash.lower(): raise ValueError('Snapshot SHA-256 mismatch.')
    source = sqlite3.connect(snapshot.as_uri() + '?mode=ro', uri=True)
    target = sqlite3.connect(database.as_uri() + ('?mode=rw' if apply else '?mode=ro'), uri=True, timeout=30)
    source.row_factory = target.row_factory = sqlite3.Row
    try:
        if source.execute('PRAGMA integrity_check').fetchone()[0] != 'ok': raise ValueError('Snapshot is corrupt.')
        if apply:
            root = pathlib.Path(recovery_root or r'B:\AtlasExample\Backups\record-restores')
            root.mkdir(parents=True, exist_ok=True)
            before = root / (datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%d-%H%M%S-%f') + '.db')
            with sqlite3.connect(before) as backup: target.backup(backup)
            pathlib.Path(str(before) + '.json').write_text(json.dumps({'sha256': digest(before), 'source': str(database)}))
            target.execute('PRAGMA foreign_keys=ON')
            target.execute('BEGIN IMMEDIATE')
        changes = []
        for table, owner_column in GRAPHS[entity]:
            columns = source.execute(f'PRAGMA table_info("{table}")').fetchall()
            names = [c['name'] for c in columns]
            keys = [c['name'] for c in sorted(columns, key=lambda c: c['pk']) if c['pk']]
            target_names = [r['name'] for r in target.execute(f'PRAGMA table_info("{table}")')]
            if names != target_names: raise ValueError(f'{table}: schema differs; migration review required.')
            rows = source.execute(f'SELECT * FROM "{table}" WHERE "{owner_column}"=?', (entity_id,)).fetchall()
            if table == GRAPHS[entity][0][0] and len(rows) != 1: raise ValueError('Record not found uniquely in snapshot.')
            for row in rows:
                where = ' AND '.join(f'"{k}"=?' for k in keys)
                old = target.execute(f'SELECT * FROM "{table}" WHERE {where}', tuple(row[k] for k in keys)).fetchone()
                if old is not None and old[owner_column] != entity_id:
                    raise ValueError(f'{table}: a saved ID now belongs to another record; manual review required.')
                fields = [n for n in names if old is None or old[n] != row[n]]
                if not fields: continue
                changes.append({'table': table, 'key': {k: row[k] for k in keys}, 'operation': 'insert' if old is None else 'update', 'fields': fields})
                if apply:
                    quoted = ','.join(f'"{n}"' for n in names)
                    conflict = ','.join(f'"{k}"' for k in keys)
                    updates = ','.join(f'"{n}"=excluded."{n}"' for n in names if n not in keys)
                    target.execute(f'INSERT INTO "{table}" ({quoted}) VALUES ({",".join("?" for _ in names)}) '
                                   f'ON CONFLICT ({conflict}) DO UPDATE SET {updates}', tuple(row[n] for n in names))
        if apply:
            # Validate affected tables; unrelated historical DB violations do not get silently changed.
            for table, _ in GRAPHS[entity]:
                if target.execute(f'PRAGMA foreign_key_check("{table}")').fetchone():
                    raise ValueError(f'{table}: foreign-key validation failed; changes rolled back.')
            target.execute('INSERT INTO AuditLogs(Action,EntityType,EntityId,Username,Summary,DetailsJson,CreatedUtc) VALUES(?,?,?,?,?,?,?)',
                ('recovery.record.restore', entity, entity_id, 'local-recovery', 'Restored saved fields and relationships; retained later additions.',
                 json.dumps({'snapshotSha256': expected_hash, 'changes': changes}), datetime.datetime.now(datetime.timezone.utc).isoformat()))
            target.commit()
        return {'applied': apply, 'entity': entity, 'id': entity_id, 'changes': changes, 'laterAdditionsRetained': True}
    finally:
        target.close(); source.close()

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', required=True)
    parser.add_argument('--sha256', required=True)
    parser.add_argument('--database', default=r'C:\AtlasExample\Api\data\atlas.db')
    parser.add_argument('--entity', choices=GRAPHS, default='Location')
    parser.add_argument('--id', type=int, required=True)
    parser.add_argument('--apply', action='store_true', help='Apply the previewed changes; first creates an independent current-state backup.')
    parser.add_argument('--recovery-root')
    args = parser.parse_args()
    print(json.dumps(restore(args.snapshot, args.sha256, args.database, args.entity, args.id, args.apply, args.recovery_root), indent=2))
