"""Apply the reviewed Isle lodge labels without merging locations or WDLs.

Requires an explicit SQLite path and a new private backup directory. The online
backup and before/after receipt permit rollback; a later editor's text is never
silently overwritten. No coordinates, links, source dates or assets are changed.
"""
import argparse
import hashlib
import json
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

LODGES = {
    1594: 'Opera Lodge',
    1655: 'Hatch Lodge',
    1659: 'Phoenix Fortress',
    1669: 'Escaping from Sky to our New Home',
    1684: 'Time is Running Out',
}
BOUNDS = (-24768, 5952, -23504, 7200)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', type=Path, required=True)
    parser.add_argument('--backup-directory', type=Path, required=True)
    args = parser.parse_args()
    if not args.database.is_file():
        raise ValueError('Database must already exist')
    args.backup_directory.mkdir(parents=True, exist_ok=False)
    connection = sqlite3.connect(args.database, timeout=30)
    connection.row_factory = sqlite3.Row
    backup = args.backup_directory / 'before.db'
    with sqlite3.connect(backup) as destination:
        connection.backup(destination)
        if destination.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
            raise ValueError('Backup integrity check failed')
    changes = []
    timestamp = datetime.now(timezone.utc).isoformat()
    connection.execute('BEGIN IMMEDIATE')
    try:
        for location_id, name in LODGES.items():
            row = connection.execute('SELECT * FROM Locations WHERE Rowid=?', (location_id,)).fetchone()
            if row is None or row['Name'] != name or row['Dimension'] != 2 or row['Description']:
                raise ValueError(f'Location {location_id} changed since review; inspect before applying')
            if not (BOUNDS[0] <= row['X'] < BOUNDS[2] and BOUNDS[1] <= row['Z'] < BOUNDS[3]):
                raise ValueError('Unexpected location coordinates')
            renamed = f'{name} (The Isle)'
            description = (f'{name} is a SpawnMason lodge at The Isle in the End. '
                'The marker is its own Archive warp arrival point. '
                'The available world download and 2D/3D renders include the surrounding Isle '
                'and neighboring lodges, which is why several lodge pages show the same island. '
                'Their separate Archive warp names and catalog dates are retained.')
            changes.append({'table': 'Locations', 'id': location_id,
                'before': {'Name': row['Name'], 'Description': row['Description'], 'ModifiedUtc': row['ModifiedUtc']},
                'after': {'Name': renamed, 'Description': description, 'ModifiedUtc': timestamp}})
            connection.execute('UPDATE Locations SET Name=?,Description=?,ModifiedUtc=? WHERE Rowid=?',
                               (renamed, description, timestamp, location_id))
            renders = connection.execute('SELECT * FROM Renders WHERE LocationRowid=?', (location_id,)).fetchall()
            if len(renders) != 1:
                raise ValueError('Render set changed since review')
            render = renders[0]
            if tuple(render[k] for k in ['MinX','MinZ','MaxXExclusive','MaxZExclusive']) != BOUNDS or render['Description']:
                raise ValueError('Render changed since review')
            render_name = f'The Isle area - {name} warp'
            render_description = ('Shared-area capture of The Isle, including neighboring lodges. '
                                  'The warp identifies the named lodge; the render is not limited to that structure.')
            changes.append({'table':'Renders', 'id':render['Id'],
                'before':{'Name':render['Name'],'Description':render['Description']},
                'after':{'Name':render_name,'Description':render_description}})
            connection.execute('UPDATE Renders SET Name=?,Description=? WHERE Id=?',
                               (render_name,render_description,render['Id']))
        parent = connection.execute('SELECT * FROM Locations WHERE Rowid=515').fetchone()
        if parent is None or parent['Name'] != 'The Isle' or parent['Description']:
            raise ValueError('The Isle record changed since review')
        description = ('SpawnMason base in the End. The Atlas also lists individual lodges here: '
            'Opera Lodge, Hatch Lodge, Phoenix Fortress, Escaping from Sky to our New Home, '
            'and Time is Running Out. Their Archive warps lead to different points around the base; '
            'the currently available lodge WDLs and renders include this shared surrounding area.')
        changes.append({'table':'Locations','id':515,
            'before':{'Name':parent['Name'],'Description':parent['Description'],'ModifiedUtc':parent['ModifiedUtc']},
            'after':{'Name':parent['Name'],'Description':description,'ModifiedUtc':timestamp}})
        connection.execute('UPDATE Locations SET Description=?,ModifiedUtc=? WHERE Rowid=515',(description,timestamp))
        receipt = {'backupSha256':hashlib.sha256(backup.read_bytes()).hexdigest(),
                   'createdUtc':timestamp,'changes':changes}
        (args.backup_directory/'changes.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8')
        for change in changes:
            connection.execute('INSERT INTO AuditLogs (Action,EntityType,EntityId,Username,Summary,DetailsJson,CreatedUtc) VALUES (?,?,?,?,?,?,?)',
                ('Update','Location' if change['table']=='Locations' else 'Render',change['id'],
                 'operator-maintenance','Clarify reviewed lodge relationship and shared Isle render footprint',json.dumps(change),timestamp))
        if connection.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
            raise ValueError('Database integrity check failed')
        connection.commit()
        print(json.dumps({'updatedRecords':len(changes),'backupSha256':receipt['backupSha256'],'integrity':'ok'}))
    except BaseException:
        connection.rollback()
        raise
    finally:
        connection.close()


if __name__ == '__main__':
    main()
