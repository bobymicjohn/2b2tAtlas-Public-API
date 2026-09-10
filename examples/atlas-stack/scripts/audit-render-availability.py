"""Read-only checks for missing generated 2D tiles and marker/footprint mismatches.

Checks generation directories and day/night overview tiles, not every tile at
every zoom. A marker outside a WDL is a review candidate: museum relocation,
partial snapshots and bad location matches need different repairs.
"""
import argparse
import json
import sqlite3
from pathlib import Path
from urllib.parse import unquote, urlparse


def audit(database, tiles_root):
    root = Path(tiles_root).resolve(strict=True)
    connection = sqlite3.connect(Path(database).resolve(strict=True).as_uri() + '?mode=ro', uri=True)
    connection.row_factory = sqlite3.Row
    report = {'checkedGenerations': 0, 'missingVariantsOrOverviews': [], 'markersOutsideRender': []}
    try:
        rows = connection.execute('''SELECT r.*, l.X, l.Z, l.Dimension AS LocationDimension,
            l.Name AS LocationName FROM Renders r JOIN Locations l ON l.Rowid=r.LocationRowid
            WHERE r.IsPublic=1 AND r.CoordinateScheme='atlas-sparse-v1' ''')
        for row in rows:
            path = urlparse(row['TilesPath']).path
            if not path.startswith('/AtlasTiles/') or '/{dn}/' not in path:
                continue
            relative = unquote(path[len('/AtlasTiles/'):].split('/{dn}/')[0])
            generation = (root / relative).resolve()
            if not generation.is_relative_to(root):
                raise ValueError(f"Render {row['Id']} has an escaping tile path")
            report['checkedGenerations'] += 1
            for variant in ['day', 'night'] if row['HasDayNight'] else ['day']:
                overview = generation / variant / '0'
                if not overview.is_dir() or not any(overview.glob('*/*.png')):
                    report['missingVariantsOrOverviews'].append({
                        'renderId': row['Id'], 'locationId': row['LocationRowid'],
                        'name': row['LocationName'], 'variant': variant})
            bounds = [row[key] for key in ('MinX', 'MinZ', 'MaxXExclusive', 'MaxZExclusive')]
            if all(value is not None for value in bounds) and row['Dimension'] == row['LocationDimension']:
                if not (bounds[0] <= row['X'] < bounds[2] and bounds[1] <= row['Z'] < bounds[3]):
                    report['markersOutsideRender'].append({
                        'renderId': row['Id'], 'locationId': row['LocationRowid'],
                        'name': row['LocationName'], 'x': row['X'], 'z': row['Z'], 'bounds': bounds})
    finally:
        connection.close()
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--database', type=Path, required=True)
    parser.add_argument('--tiles-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.database, args.tiles_root)
    with args.output.open('x', encoding='utf-8') as output:
        json.dump(result, output, indent=2)
    print(json.dumps({key: len(value) if isinstance(value, list) else value for key, value in result.items()}))
