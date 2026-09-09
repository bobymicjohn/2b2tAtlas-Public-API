"""Regression checks for completion gates; never contacts production or Pushover."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('monitor', Path(__file__).with_name('watch-atlas-storage-completion.py'))
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)


class Gates(unittest.TestCase):
    def test_disconnect_retries_and_live_cleanup_fix_gate_completion(self):
        with tempfile.TemporaryDirectory() as folder:
            log = Path(folder) / 'worker.log'
            state = Path(folder) / 'state.json'
            recovery = dict(captures=[dict(warp='Lorraine', statePath=str(state))])
            parallel = dict(workers=[dict(id=2, stdout=str(log))])
            log.write_text('WARP something-else', encoding='utf-8')
            m.save(state, dict(entries=[dict(warp='Lorraine', status='retryable')]))
            self.assertEqual(m.collector_recovery_status(recovery, parallel),
                             dict(pendingSafetyReload=[2], pendingCaptureRecovery=['Lorraine']))
            log.write_text('COLLECTOR-SAFETY interrupted-retention-v1: enabled', encoding='utf-8')
            m.save(state, dict(entries=[dict(warp='Lorraine', status='captured', adaptive=dict(standardVersion=2, componentSelection='landing'))]))
            self.assertEqual(m.collector_recovery_status(recovery, parallel),
                             dict(pendingSafetyReload=[], pendingCaptureRecovery=[]))
            recovery['requiredWorkerMarkers'] = ['COLLECTOR-SAFETY interrupted-retention-v1:', 'COLLECTOR-RESUME saved-terrain-v1:']
            self.assertEqual(m.collector_recovery_status(recovery, parallel)['pendingSafetyReload'], [2])
            with log.open('a') as stream: stream.write('\nCOLLECTOR-RESUME saved-terrain-v1: enabled')
            self.assertEqual(m.collector_recovery_status(recovery, parallel)['pendingSafetyReload'], [])

    def test_windows_and_restic_timestamps(self):
        self.assertEqual(m.date('2026-09-07T23:17:37.0660286Z').microsecond, 66028)
        self.assertEqual(m.date('2026-09-07T17:17:37.066028612-06:00'), m.date('2026-09-07T23:17:37.066028Z'))

    def test_backup_must_be_verified_on_nas_after_final_cutover(self):
        good = dict(state='verified', repository=m.NAS + r'\restic-preservation', startedUtc='2026-09-08T02:00:00Z')
        self.assertTrue(m.preservation_ready(good, '2026-09-08T01:00:00Z'))
        for changes in [dict(state='running'), dict(state='failed'), dict(repository=r'I:\backup'), dict(startedUtc='2026-09-07T00:00:00Z')]:
            self.assertFalse(m.preservation_ready(good | changes, '2026-09-08T01:00:00Z'))

    def test_snapshot_requires_every_root_and_nonempty_content(self):
        good = dict(time='2026-09-08T02:00:00Z', paths=m.REQUIRED, summary=dict(total_files_processed=100))
        m.validate_snapshot(good, '2026-09-08T01:00:00Z')
        for changes in [dict(paths=m.REQUIRED[1:]), dict(paths=[p for p in m.REQUIRED if not p.startswith('D:')]), dict(summary={}), dict(time='2026-09-07T01:00:00Z')]:
            with self.assertRaises(RuntimeError):
                m.validate_snapshot(good | changes, '2026-09-08T01:00:00Z')

    def test_every_active_collector_and_bluemap_must_progress(self):
        old = dict(collectors=dict(workers=[dict(id=1, warp='a', chunksReceived=1, waypoint=1, completed=1, outcome='live')]), blueMap=dict(remainingRenderCount=2, validatedRenderCount=10))
        new = dict(collectors=dict(workers=[dict(id=1, warp='a', chunksReceived=2, waypoint=1, completed=1, outcome='live')]), blueMap=dict(remainingRenderCount=1, validatedRenderCount=11))
        self.assertTrue(m.advancing(old, new))
        self.assertFalse(m.advancing(old, old))
        self.assertFalse(m.advancing(old, dict(new, blueMap=old['blueMap'])))

    def test_pending_collectors_never_alert_or_launch_backup(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            m.save(root / 'local-ingestion-cutover.json', dict(state='deployed', livePublicationVerified=True))
            m.save(root / 'storage-e-cutover.json', dict(state='verified'))
            m.save(root / 'deferred-scratch-cutover.json.status.json', dict(state='waiting-for-capture-boundaries', pendingCollectors=[{}]))
            with patch.object(m, 'ROOT', root), patch.object(m, 'STATE', root / 'state.json'), patch.object(m, 'powershell') as shell, patch.object(m, 'notify_owner') as notify:
                m.run()
                notify.assert_not_called()
                self.assertNotIn('Start-ScheduledTask', shell.call_args[0][0])
                self.assertEqual(m.read(root / 'state.json')['state'], 'waiting-for-collectors')

    def test_missing_gate_fails_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with patch.object(m, 'ROOT', root), patch.object(m, 'STATE', root / 'state.json'), patch.object(m, 'notify_owner') as notify:
                m.run()
                notify.assert_not_called()
                self.assertEqual(m.read(root / 'state.json')['state'], 'waiting-for-verification')

    def test_already_notified_does_not_send_again(self):
        with tempfile.TemporaryDirectory() as folder:
            state = Path(folder) / 'state.json'
            m.save(state, dict(notifiedUtc='2026-09-08T02:00:00Z'))
            with patch.object(m, 'STATE', state), patch.object(m, 'notify_owner') as notify, patch.object(m, 'powershell') as shell:
                m.run()
                notify.assert_not_called()
                shell.assert_not_called()

    def test_final_success_and_restore_failure(self):
        # Exercise the complete durable orchestration without touching the host.
        for restore_fails in [False, True]:
            with self.subTest(restore_fails=restore_fails), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                m.save(root / 'local-ingestion-cutover.json', dict(state='deployed', livePublicationVerified=True, completedUtc='2026-09-08T00:00:00Z'))
                m.save(root / 'storage-e-cutover.json', dict(state='verified'))
                m.save(root / 'deferred-scratch-cutover.json.status.json', dict(state='complete', updatedUtc='2026-09-08T01:00:00Z'))
                m.save(root / 'Preservation-status.json', dict(state='verified', repository=m.NAS + r'\restic-preservation', startedUtc='2026-09-08T02:00:00Z', completedUtc='2026-09-08T03:00:00Z'))
                m.save(root / 'Metadata-status.json', dict(state='verified', nasRepository=m.NAS + r'\restic-metadata', startedUtc='2026-09-08T03:10:00Z'))
                prior = dict(checkedUtc='2026-09-08T03:20:00Z', collectors=dict(workers=[dict(id=1, warp='a', chunksReceived=1, waypoint=1, completed=1, outcome='live')]), blueMap=dict(remainingRenderCount=2, validatedRenderCount=10))
                current = dict(checkedUtc='2026-09-08T03:40:00Z', collectors=dict(workers=[dict(id=1, warp='a', chunksReceived=2, waypoint=1, completed=1, outcome='live')]), blueMap=dict(remainingRenderCount=1, validatedRenderCount=11), samples=['wdl', 'tile', 'bluemap', 'image'])
                m.save(root / 'state.json', dict(healthyObservation=prior))
                preservation = dict(id='preservation', short_id='pres', time='2026-09-08T02:00:00Z', paths=m.REQUIRED, summary=dict(total_files_processed=100))
                metadata = dict(id='metadata', paths=[r'B:\AtlasExample\Backups\atlas-20260908-031000.db'])
                with patch.object(m, 'ROOT', root), patch.object(m, 'STATE', root / 'state.json'), patch.object(m, 'snapshot', side_effect=[preservation, metadata]), patch.object(m, 'audit', return_value=current), patch.object(m, 'powershell', return_value='{"status":"healthy"}'), patch.object(m, 'dump_verify', side_effect=RuntimeError('corrupt restore') if restore_fails else None, return_value={'verified': True}) as restore, patch.object(m, 'notify_owner') as notify:
                    m.run()
                    result = m.read(root / 'state.json')
                    if restore_fails:
                        notify.assert_not_called()
                        self.assertEqual(result['state'], 'waiting-for-verification')
                        self.assertNotIn('notifiedUtc', result)
                    else:
                        notify.assert_called_once()
                        self.assertEqual(restore.call_count, 5)
                        self.assertEqual(result['state'], 'verified')
                        self.assertIn('notifiedUtc', result)


if __name__ == '__main__':
    unittest.main()
