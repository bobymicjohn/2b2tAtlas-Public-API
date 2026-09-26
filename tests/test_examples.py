"""Run all three clients against a local API, including a second page and failures."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
import unittest
from http.server import ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

import mock_atlas_api as fixture

ROOT = Path(__file__).resolve().parents[1]


class ExampleHandler(fixture.Handler):
    requests = []
    row_count = 1
    fail_locations = False

    def do_GET(self):
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        self.requests.append((parsed.path, query))
        if parsed.path == '/api/locations' and self.fail_locations:
            self.send_error(503, 'Fixture unavailable')
            return
        if parsed.path in ('/api/warps', '/api/renders'):
            offset = int(query.get('offset', ['0'])[0])
            limit = int(query.get('limit', ['500'])[0])
            if parsed.path == '/api/warps':
                rows = [dict(fixture.WARP, id=number, name=f'Warp {number}')
                        for number in range(offset, min(offset + limit, self.row_count))]
            else:
                rows = [dict(fixture.RENDER, renderId=number, blueMapUrl=None,
                             blueMapPath=None, blueMapProfileVersion=None)
                        for number in range(offset, min(offset + limit, self.row_count))]
            self.send_json(rows)
            return
        super().do_GET()


class ExamplesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dotnet = shutil.which('dotnet')
        cls.node = shutil.which('node')
        if not cls.dotnet or not cls.node:
            raise RuntimeError('Install .NET 10 and Node.js before running the example tests.')
        build = subprocess.run([cls.dotnet, 'build', 'examples/csharp', '-c', 'Release', '--nologo'],
                               cwd=ROOT, capture_output=True, text=True, timeout=120)
        if build.returncode:
            raise RuntimeError(build.stdout + build.stderr)
        cls.clients = {
            'Python': [sys.executable, str(ROOT / 'examples/python/atlas_examples.py')],
            'JavaScript': [cls.node, str(ROOT / 'examples/javascript/atlas-examples.mjs')],
            'C#': [cls.dotnet, str(ROOT / 'examples/csharp/bin/Release/net10.0/AtlasExamples.dll')],
        }
        cls.server = ThreadingHTTPServer(('127.0.0.1', 0), ExampleHandler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=5)

    def setUp(self):
        ExampleHandler.requests = []
        ExampleHandler.row_count = 1
        ExampleHandler.fail_locations = False

    def run_client(self, command, search='Mu Megabase'):
        ExampleHandler.requests = []
        environment = os.environ.copy()
        environment['ATLAS_API_BASE_URL'] = f'http://127.0.0.1:{self.server.server_port}'
        return subprocess.run(command + [search], cwd=ROOT, env=environment,
                              capture_output=True, text=True, encoding='utf-8', timeout=30)

    def test_small_catalog_and_missing_optional_3d_links(self):
        for name, command in self.clients.items():
            with self.subTest(client=name):
                result = self.run_client(command)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn('Mu Megabase', result.stdout)
                self.assertIn('+Z Highway', result.stdout)

    def test_location_warps_and_renders_continue_after_the_first_thousand(self):
        ExampleHandler.row_count = 1001
        for name, command in self.clients.items():
            with self.subTest(client=name):
                result = self.run_client(command)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                for route in ['/api/warps', '/api/renders']:
                    offsets = [query.get('offset', ['0'])[0] for path, query in ExampleHandler.requests
                               if path == route and query.get('locationId') == ['5']]
                    self.assertEqual(offsets, ['0', '1000'], (name, route, offsets))

    def test_http_failure_returns_a_failure_exit_code(self):
        ExampleHandler.fail_locations = True
        for name, command in self.clients.items():
            with self.subTest(client=name):
                result = self.run_client(command)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('503', result.stderr)

    def test_unknown_location_does_not_request_unrelated_data(self):
        for name, command in self.clients.items():
            with self.subTest(client=name):
                result = self.run_client(command, 'not in this fixture')
                self.assertIn('No location matched', result.stdout + result.stderr)
                self.assertEqual([path for path, _ in ExampleHandler.requests], ['/api/locations'])


if __name__ == '__main__':
    unittest.main()
