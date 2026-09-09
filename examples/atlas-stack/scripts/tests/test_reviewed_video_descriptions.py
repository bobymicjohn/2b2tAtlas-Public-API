import importlib.util
import json
import sqlite3
import tempfile
import unittest
from pathlib import Path


spec = importlib.util.spec_from_file_location('review', Path(__file__).parents[1] / 'apply-reviewed-location-descriptions.py')
review = importlib.util.module_from_spec(spec)
spec.loader.exec_module(review)


class ReviewedDescriptionsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.db = self.root / 'atlas.db'
        self.manifest = self.root / 'review.json'
        with sqlite3.connect(self.db) as c:
            c.executescript('''
                CREATE TABLE Locations (Rowid INTEGER PRIMARY KEY, Name TEXT, Description TEXT,
                                        ModifiedUtc TEXT, X INTEGER);
                INSERT INTO Locations VALUES (1, 'One', NULL, 'old', 123), (2, 'Two', 'existing', 'old', 456);
                CREATE TABLE AuditLogs (Id INTEGER PRIMARY KEY, Action TEXT, EntityType TEXT,
                    EntityId INTEGER, Username TEXT, Summary TEXT, DetailsJson TEXT, CreatedUtc TEXT);
            ''')
        self.rows = [dict(locationId=1, locationName='One', expectedDescription=None,
                         description='Reviewed history.', sources=[dict(url='https://www.youtube.com/watch?v=12345678901')])]

    def save(self):
        self.manifest.write_text(json.dumps(self.rows), encoding='utf-8')

    def test_dry_run_apply_backup_audit_and_idempotency(self):
        self.save()
        self.assertEqual(review.apply_manifest(self.db, self.manifest)['pending'], 1)
        with sqlite3.connect(self.db) as c:
            self.assertIsNone(c.execute('SELECT Description FROM Locations WHERE Rowid=1').fetchone()[0])
        result = review.apply_manifest(self.db, self.manifest, True, self.root / 'backups')
        with sqlite3.connect(result['backupPath']) as c:
            self.assertIsNone(c.execute('SELECT Description FROM Locations WHERE Rowid=1').fetchone()[0])
        with sqlite3.connect(self.db) as c:
            self.assertEqual(c.execute('SELECT Description, X FROM Locations WHERE Rowid=1').fetchone(), ('Reviewed history.', 123))
            details = json.loads(c.execute('SELECT DetailsJson FROM AuditLogs').fetchone()[0])
            self.assertEqual(details['before'], {'Description': None})
            self.assertEqual(details['sources'], self.rows[0]['sources'])
        self.assertEqual(review.apply_manifest(self.db, self.manifest, True)['applied'], 0)
        with sqlite3.connect(self.db) as c:
            self.assertEqual(c.execute('SELECT count(*) FROM AuditLogs').fetchone()[0], 1)

    def test_concurrent_edit_blocks_whole_batch(self):
        self.rows.append(dict(locationId=2, locationName='Two', expectedDescription='stale',
                              description='Do not overwrite.', sources=[{'url': 'source'}]))
        self.save()
        with self.assertRaisesRegex(ValueError, 'changed since review'):
            review.apply_manifest(self.db, self.manifest, True, self.root / 'backups')
        with sqlite3.connect(self.db) as c:
            self.assertIsNone(c.execute('SELECT Description FROM Locations WHERE Rowid=1').fetchone()[0])
            self.assertEqual(c.execute('SELECT count(*) FROM AuditLogs').fetchone()[0], 0)

    def test_wrong_identity_and_duplicate_ids_block(self):
        self.rows[0]['locationName'] = 'Wrong'
        self.save()
        with self.assertRaisesRegex(ValueError, 'identity changed'):
            review.apply_manifest(self.db, self.manifest)
        self.rows[0]['locationName'] = 'One'
        self.rows.append(self.rows[0].copy())
        self.save()
        with self.assertRaisesRegex(ValueError, 'Duplicate'):
            review.apply_manifest(self.db, self.manifest)

    def test_audit_failure_rolls_back_descriptions(self):
        self.save()
        with sqlite3.connect(self.db) as c:
            c.execute('DROP TABLE AuditLogs')
        with self.assertRaises(sqlite3.OperationalError):
            review.apply_manifest(self.db, self.manifest, True, self.root / 'backups')
        with sqlite3.connect(self.db) as c:
            self.assertIsNone(c.execute('SELECT Description FROM Locations WHERE Rowid=1').fetchone()[0])


if __name__ == '__main__':
    unittest.main()
