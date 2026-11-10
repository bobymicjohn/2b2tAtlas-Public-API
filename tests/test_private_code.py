"""Offline guard checks using isolated Git repositories, with no remotes or hooks.

Set ATLAS_TEST_TEMP_ROOT to place generated fixtures under a chosen local folder.
"""

import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


GUARD = Path(__file__).resolve().parents[1] / 'scripts/check-private-code.py'


class PrivateCodeGateTests(unittest.TestCase):
    def setUp(self):
        self.temp_root = Path(os.environ.get('ATLAS_TEST_TEMP_ROOT', tempfile.gettempdir())).resolve()
        self.temp_root.mkdir(parents=True, exist_ok=True)
        self.repo = Path(tempfile.mkdtemp(prefix='atlas-private-gate-', dir=self.temp_root)).resolve()
        self.assertTrue(self.repo.is_relative_to(self.temp_root))
        (self.repo / 'scripts').mkdir()
        self.guard = self.repo / 'scripts/check-private-code.py'
        shutil.copyfile(GUARD, self.guard)
        self.git('init', '--quiet', '--initial-branch=main')
        self.git('config', 'user.name', 'Guard fixture')
        self.git('config', 'user.email', 'guard@example.invalid')
        self.git('config', 'core.autocrlf', 'false')
        self.write('README.md', 'Public fixture.\n')
        self.commit('Initial public fixture')
        self.base = self.git('rev-parse', 'HEAD').strip()
        self.git('update-ref', 'refs/remotes/origin/main', self.base)
        self.secret = 'gh' + 'p_' + 'Q' * 30

    def tearDown(self):
        # Resolve again before recursive removal and keep deletion within the
        # exact fixture directory created by this test, never a repository parent.
        target = self.repo.resolve()
        if target == self.temp_root or not target.is_relative_to(self.temp_root):
            raise RuntimeError('Refusing cleanup outside the fixture directory.')
        def remove_readonly(action, name, error):
            item = Path(name).resolve()
            if not item.is_relative_to(target):
                raise RuntimeError('Refusing cleanup outside the fixture directory.')
            # Git marks loose objects read-only on Windows.
            os.chmod(item, stat.S_IREAD | stat.S_IWRITE)
            action(name)
        shutil.rmtree(target, onerror=remove_readonly)

    def git(self, *arguments):
        result = subprocess.run(['git', *arguments], cwd=self.repo, capture_output=True, text=True)
        if result.returncode:
            self.fail('Fixture Git command failed: ' + arguments[0])
        return result.stdout

    def write(self, name, text):
        path = self.repo / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding='utf-8')

    def commit(self, message):
        self.git('add', '--all')
        self.git('commit', '--quiet', '-m', message)
        return self.git('rev-parse', 'HEAD').strip()

    def check(self, *arguments, cwd=None):
        result = subprocess.run(
            [sys.executable, str(self.guard), *map(str, arguments)],
            cwd=cwd or self.repo, capture_output=True, text=True,
        )
        output = result.stdout + result.stderr
        self.assertFalse(self.secret in output, 'The guard exposed a synthetic secret value.')
        return result.returncode, output

    def test_legacy_hook_arguments_accept_complete_clean_snapshot(self):
        code, output = self.check('--repo', self.repo, '--ref', 'HEAD', cwd=self.temp_root)
        self.assertEqual(code, 0)
        self.assertIn('2 snapshot file versions', output)

    def test_legacy_ref_checks_unchanged_files_in_whole_snapshot(self):
        self.write('configuration.txt', self.secret)
        self.commit('Private fixture')
        self.write('README.md', 'Only documentation changed.\n')
        self.commit('Later change')
        code, output = self.check('--repo', self.repo, '--ref', 'HEAD')
        self.assertNotEqual(code, 0)
        self.assertIn('Inspect possible private content: configuration.txt', output)

    def test_wrong_repository_and_nonexistent_target_fail_closed(self):
        for target in [self.temp_root, self.repo / 'missing', self.repo / 'scripts']:
            with self.subTest(target=target.name):
                code, output = self.check('--repo', target, '--ref', 'HEAD')
                self.assertNotEqual(code, 0)
                self.assertIn('--repo must identify', output)
                self.assertNotIn('gate passed', output)

    def test_unknown_and_option_like_revisions_fail_closed(self):
        for arguments in [('--ref', 'missing-commit'), ('--ref=--all',), ('--head', 'missing-commit')]:
            with self.subTest(arguments=arguments):
                code, output = self.check(*arguments)
                self.assertNotEqual(code, 0)
                self.assertIn('not a readable commit', output)
                self.assertNotIn('Traceback', output)

    def test_conflicting_modes_are_rejected(self):
        for arguments in [('--ref', 'HEAD', '--head', 'HEAD'), ('--ref', 'HEAD', '--base', self.base)]:
            with self.subTest(arguments=arguments):
                code, output = self.check(*arguments)
                self.assertNotEqual(code, 0)
                self.assertNotIn('gate passed', output)

    def test_default_staged_mode_reads_index_not_worktree(self):
        self.write('configuration.txt', self.secret)
        self.git('add', 'configuration.txt')
        self.write('configuration.txt', 'Clean unstaged worktree.\n')
        code, output = self.check()
        self.assertNotEqual(code, 0)
        self.assertIn('Inspect possible private content: configuration.txt', output)
        self.git('add', 'configuration.txt')
        self.write('configuration.txt', self.secret)
        code, output = self.check()
        self.assertEqual(code, 0)
        self.assertIn('1 changed file versions', output)

    def test_private_intermediate_commit_is_rejected_after_later_removal(self):
        self.write('configuration.txt', self.secret)
        private_commit = self.commit('Private intermediate fixture')
        self.git('rm', '--quiet', 'configuration.txt')
        head = self.commit('Remove private fixture')
        code, _ = self.check('--repo', self.repo, '--ref', head)
        self.assertEqual(code, 0)
        code, output = self.check('--repo', self.repo, '--ref', private_commit)
        self.assertNotEqual(code, 0)
        self.assertIn('configuration.txt', output)
        code, output = self.check('--base', self.base, '--head', head)
        self.assertNotEqual(code, 0)
        self.assertIn('configuration.txt', output)

    def test_private_paths_runtime_material_and_wiring_keep_existing_policy(self):
        for filename, content in [
            ('commerce/notes.txt', 'Private feature fixture.\n'),
            ('data/cache.sqlite', 'Synthetic runtime fixture.\n'),
            ('Service.cs', 'class Example { ShopService service; }\n'),
        ]:
            self.write(filename, content)
        self.commit('Policy fixtures')
        code, output = self.check('--ref', 'HEAD')
        self.assertNotEqual(code, 0)
        self.assertIn('Private feature path: commerce/notes.txt', output)
        self.assertIn('Private/runtime material: data/cache.sqlite', output)
        self.assertIn('Private feature wiring: Service.cs', output)

    def test_private_merge_resolution_is_checked_in_historical_mode(self):
        self.git('checkout', '--quiet', '-b', 'side')
        self.write('side.txt', 'Public side.\n')
        self.commit('Side commit')
        self.git('checkout', '--quiet', 'main')
        self.write('main.txt', 'Public main.\n')
        self.commit('Main commit')
        self.git('merge', '--no-commit', '--no-ff', 'side')
        self.write('configuration.txt', self.secret)
        head = self.commit('Merge fixture with private resolution')
        code, output = self.check('--base', self.base, '--head', head)
        self.assertNotEqual(code, 0)
        self.assertIn('configuration.txt', output)


if __name__ == '__main__':
    unittest.main()
