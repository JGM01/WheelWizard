#!/usr/bin/env python3
"""Integration tests against the real bundled helper; run build.sh first."""
import http.server, io, json, os, pathlib, queue, signal, socketserver, subprocess, tempfile, threading, unittest, uuid, zipfile
HELPER = pathlib.Path(__file__).resolve().parent / 'artifacts/WheelWizardNative.app/Contents/Resources/helper/WheelWizard.Host'

# A tiny offline GameBanana stub. The helper reads WHEELWIZARD_GAMEBANANA_URL and downloads only from
# the same host, so every catalog command in this suite stays hermetic.
CATALOG = {'search': '', 'details': '', 'archive': b''}

class CatalogServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True

class CatalogHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass
    def do_GET(self):
        if self.path.startswith('/Util/Search/Results'):
            body = CATALOG['search'].encode(); kind = 'application/json'
        elif self.path.startswith('/Mod/') and self.path.endswith('/ProfilePage'):
            body = CATALOG['details'].encode(); kind = 'application/json'
        elif self.path.startswith('/dl/'):
            body = CATALOG['archive']; kind = 'application/octet-stream'
        else:
            self.send_response(404); self.end_headers(); return
        self.send_response(200)
        self.send_header('Content-Type', kind)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

class BridgeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='wheel wizard bridge ')
        self.root = pathlib.Path(self.temp.name)
        self.workspace = self.root / 'workspace with spaces'
        (self.workspace / 'Launcher').mkdir(parents=True)
        (self.workspace / 'projects/mkwii').mkdir(parents=True)
        (self.workspace / 'projects/mkwii/recomp.yml').write_text('fixture')
        for args in [['init'], ['add', '.'], ['-c', 'user.name=Fixture', '-c', 'user.email=test@example.test', 'commit', '-m', 'fixture']]:
            subprocess.run(['git', *args], cwd=self.workspace, check=True, capture_output=True)
        game = self.root / 'fixture.wbfs'; game.write_text('fixture')
        self.setup = dict(wbfs=str(game), workspace=str(self.workspace), cmake='/bin/bash', ninja='/bin/bash', nodtool='/bin/bash', translator='/bin/bash')
        self.script = self.workspace / 'Launcher/local-build-macos.command'
        self.script.write_text('echo MKWCBUILD:STEP:waiting fixture\necho stderr-fixture >&2\nsleep 120\n')
        self.events = queue.Queue(); self.diagnostics = []
        self.catalog = CatalogServer(('127.0.0.1', 0), CatalogHandler)
        threading.Thread(target=self.catalog.serve_forever, daemon=True).start()
        catalog_base = 'http://127.0.0.1:%d' % self.catalog.server_address[1]
        self.child = subprocess.Popen([str(HELPER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1, env={**os.environ, 'WHEELWIZARD_NATIVE_ROOT': str(self.root / 'managed'), 'DOTNET_ROOT': '/nonexistent', 'DOTNET_MULTILEVEL_LOOKUP': '0', 'PATH': '/usr/bin:/bin', 'WHEELWIZARD_GAMEBANANA_URL': catalog_base})
        def reader():
            for line in self.child.stdout:
                try: self.events.put(json.loads(line))
                except ValueError: self.events.put({'invalid': line})
        threading.Thread(target=reader, daemon=True).start()
        threading.Thread(target=lambda: self.diagnostics.extend(self.child.stderr), daemon=True).start()
    def tearDown(self):
        self.catalog.shutdown()
        self.catalog.server_close()
        if self.child.poll() is None:
            self.child.stdin.close()
            try: self.child.wait(timeout=10)
            except subprocess.TimeoutExpired: self.child.kill(); self.child.wait(); raise
        if not self.child.stdin.closed: self.child.stdin.close()
        self.child.stdout.close(); self.child.stderr.close()
        self.temp.cleanup()
    def send(self, command, **extra):
        ident = str(uuid.uuid4())
        self.child.stdin.write(json.dumps(dict(version=1, id=ident, command=command, setup=self.setup, **extra)) + '\n'); self.child.stdin.flush()
        return ident
    def until(self, predicate):
        for _ in range(1000):
            event = self.events.get(timeout=15)
            self.assertNotIn('invalid', event)
            if predicate(event): return event
        self.fail('No matching event')
    def result(self, ident): return self.until(lambda e: e['id'] == ident and e['kind'] == 'result')
    def test_self_contained_protocol_failure_and_recovery(self):
        runtime = json.loads(pathlib.Path(str(HELPER) + '.runtimeconfig.json').read_text())['runtimeOptions']
        self.assertIn('includedFrameworks', runtime)
        self.assertNotIn('framework', runtime)
        self.assertTrue((HELPER.parent / 'libcoreclr.dylib').is_file())
        self.assertFalse(any('Avalonia' in f.name for f in HELPER.parent.parent.rglob('*')))
        self.child.stdin.write('{bad json}\n'); self.child.stdin.flush()
        self.assertEqual('failure', self.result('')['outcome'])
        result = self.result(self.send('preflight'))
        self.assertEqual('success', result['outcome']); self.assertEqual([], result['data']['errors'])
        self.assertEqual('success', self.result(self.send('config-write', settings={'volume': 0.4, 'resolutionMultiplier': 1.5}))['outcome'])
        self.assertEqual(0.4, self.result(self.send('config-read'))['data']['volume'])
    def test_busy_cancellation_and_failed_build_not_ready(self):
        build = self.send('build', product='base')
        self.until(lambda e: e['id'] == build and e['kind'] == 'progress')
        self.assertEqual('failure', self.result(self.send('config-write', settings={'volume': 0.9, 'resolutionMultiplier': 1}))['outcome'])
        self.assertEqual('failure', self.result(self.send('mods-list'))['outcome'])
        self.send('cancel')
        self.assertEqual('cancelled', self.result(build)['outcome'])
        status = self.result(self.send('status'))
        self.assertFalse(any(p['ready'] for p in status['data']['products']))
        self.assertEqual([], list((self.root / 'managed/Staging').iterdir()))
        self.script.write_text('echo deliberate-failure >&2\nexit 7\n')
        self.assertEqual('failure', self.result(self.send('build', product='base'))['outcome'])
        self.assertFalse(any(p['ready'] for p in self.result(self.send('status'))['data']['products']))
    def test_termination_cancels_owned_child(self):
        self.script.write_text('echo $$ > "' + str(self.root / 'child.pid') + '"\necho MKWCBUILD:STEP:waiting fixture\nsleep 120\n')
        build = self.send('build', product='base')
        self.until(lambda e: e['id'] == build and e['kind'] == 'progress')
        pid = int((self.root / 'child.pid').read_text())
        self.child.send_signal(signal.SIGTERM)
        self.child.wait(timeout=10)
        with self.assertRaises(ProcessLookupError): os.kill(pid, 0)
        self.assertEqual([], list((self.root / 'managed/Staging').iterdir()))

    def test_mod_library_and_preview(self):
        def archive(name, content):
            path = self.root / (name + '.zip')
            with zipfile.ZipFile(path, 'w') as z:
                z.writestr('nested/course.bin', content)
            return str(path)
        def ok(command, **fields):
            result = self.result(self.send(command, **fields))
            self.assertEqual('success', result['outcome'], result)
            return result['data']
        self.assertEqual([], ok('mods-list')['mods'])
        first = archive('first', 'first'); second = archive('second', 'second')
        ok('mods-import', archivePath=first, modTitle='First')
        state = ok('mods-import', archivePath=second, modTitle='Second')
        self.assertEqual(['First', 'Second'], [m['title'] for m in state['mods']])
        conflict = ok('mods-preview')['files'][0]
        self.assertEqual('First', conflict['winner']['modTitle'])
        self.assertEqual('Second', conflict['overwritten'][0]['modTitle'])
        ok('mods-move', modTitle='Second', direction=-1)
        self.assertEqual('Second', ok('mods-preview')['files'][0]['winner']['modTitle'])
        ok('mods-enabled', modTitle='Second', enabled=False)
        self.assertEqual([], ok('mods-preview')['files'][0]['overwritten'])
        self.assertFalse(ok('mods-list')['mods'][0]['isEnabled'])
        self.assertEqual('failure', self.result(self.send('mods-import', archivePath=first, modTitle='FIRST'))['outcome'])
        self.assertEqual('failure', self.result(self.send('mods-remove', modTitle='../outside'))['outcome'])
        self.assertEqual('failure', self.result(self.send('mods-enabled', modTitle='First'))['outcome'])
        self.assertEqual('failure', self.result(self.send('mods-move', modTitle='First', direction=9))['outcome'])
        ok('mods-remove', modTitle='Second')
        self.assertEqual(['First'], [m['title'] for m in ok('mods-list')['mods']])
        self.assertTrue(pathlib.Path(first).exists())
        # A new library instance is loaded for each command; also verify persistence across helper processes.
        self.child.stdin.close(); self.child.wait(timeout=10)
        request = dict(version=1, id='restart', command='mods-list')
        restarted = subprocess.Popen([str(HELPER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, env={**os.environ, 'WHEELWIZARD_NATIVE_ROOT': str(self.root / 'managed')})
        try:
            restarted.stdin.write(json.dumps(request)+'\n'); restarted.stdin.flush()
            while True:
                terminal = json.loads(restarted.stdout.readline())
                if terminal['kind'] == 'result': break
            self.assertEqual('success', terminal['outcome'])
            self.assertEqual('First', terminal['data']['mods'][0]['title'])
        finally:
            restarted.stdin.close(); restarted.wait(timeout=10)
            restarted.stdout.close(); restarted.stderr.close()

    def test_mod_import_traversal_and_cancellation(self):
        bad = self.root / 'bad.zip'
        with zipfile.ZipFile(bad, 'w') as archive:
            archive.writestr('valid.bin', 'ok')
            archive.writestr('../escaped', 'bad')
        result = self.result(self.send('mods-import', archivePath=str(bad), modTitle='Bad'))
        self.assertEqual('failure', result['outcome'])
        self.assertEqual([], list((self.root / 'managed').glob('.ww-mod-*')))
        large = self.root / 'large.zip'
        block = bytes(4 * 1024 * 1024)
        with zipfile.ZipFile(large, 'w', compression=zipfile.ZIP_DEFLATED) as archive:
            for i in range(64): archive.writestr(str(i) + '.bin', block)
        operation = self.send('mods-import', archivePath=str(large), modTitle='Cancelled')
        self.until(lambda e: e['id'] == operation and e['kind'] == 'progress')
        self.send('cancel')
        self.assertEqual('cancelled', self.result(operation)['outcome'])
        self.assertFalse((self.root / 'managed/Mods/Cancelled').exists())
        self.assertEqual([], list((self.root / 'managed').glob('.ww-mod-*')))
        self.assertEqual('success', self.result(self.send('mods-list'))['outcome'])

    def test_mods_reorder(self):
        def archive(name, content):
            path = self.root / (name + '.zip')
            with zipfile.ZipFile(path, 'w') as z:
                z.writestr('a.bin', content)
            return str(path)
        def ok(command, **fields):
            result = self.result(self.send(command, **fields))
            self.assertEqual('success', result['outcome'], result)
            return result['data']
        for name in ['First', 'Second', 'Third']:
            ok('mods-import', archivePath=archive(name, name), modTitle=name)
        ok('mods-reorder', titles=['Third', 'First', 'Second'])
        state = ok('mods-list')
        self.assertEqual(['Third', 'First', 'Second'], [m['title'] for m in state['mods']])
        self.assertEqual([0, 1, 2], [m['priority'] for m in state['mods']])
        conflict = ok('mods-preview')['files'][0]
        self.assertEqual('Third', conflict['winner']['modTitle'])
        self.assertEqual('failure', self.result(self.send('mods-reorder', titles=['First']))['outcome'])
        self.assertEqual('First', ok('mods-list')['mods'][1]['title'])

    def test_mod_catalog_search_details_and_install(self):
        def preview(mid, name, tags=None, model='Mod'):
            tags = tags if tags is not None else []
            return {
                '_idRow': mid, '_sName': name, '_sVersion': '1.0', '_aTags': tags,
                '_sProfileUrl': 'https://gamebanana.com/mods/%d' % mid,
                '_aPreviewMedia': {'_aImages': []},
                '_bHasContentRatings': False, '_nLikeCount': 3, '_nViewCount': 40,
                '_tsDateAdded': 1, '_tsDateModified': 2,
                '_aSubmitter': {'_sName': 'Builder %d' % mid, '_sProfileUrl': 'x', '_sAvatarUrl': None},
                '_aGame': {'_sName': 'Mario Kart Wii', '_sProfileUrl': 'x', '_sIconUrl': ''},
                '_aRootCategory': {'_sName': 'Maps', '_sProfileUrl': 'x', '_sIconUrl': None},
                '_sModelName': model,
            }
        record = preview(77, 'Bridge Course', ['patch'])
        CATALOG['search'] = json.dumps({
            '_aMetadata': {'_nRecordCount': 1, '_nPerpage': 10, '_bIsComplete': True},
            '_aRecords': [record],
        })
        port = self.catalog.server_address[1]
        details = {
            '_idRow': 77, '_sName': 'Bridge Course', '_sVersion': '1.0',
            '_sProfileUrl': 'https://gamebanana.com/mods/77',
            '_aPreviewMedia': {'_aImages': []},
            '_nLikeCount': 3, '_nViewCount': 40, '_tsDateAdded': 1, '_tsDateModified': 2,
            '_bIsObsolete': False,
            '_aSubmitter': {'_sName': 'Builder 77', '_sProfileUrl': 'x', '_sAvatarUrl': None},
            '_aGame': {'_sName': 'Mario Kart Wii', '_sProfileUrl': 'x', '_sIconUrl': ''},
            '_aCategory': {'_sName': 'Maps', '_sProfileUrl': 'x', '_sIconUrl': None},
            '_aSuperCategory': None, '_sText': '<p>Rides across the bay.</p>', '_sLicense': 'MIT',
            '_aLicenseCheckList': None, '_nDownloadCount': 9,
            '_aFiles': [{'_sFile': 'bridge.zip', '_nFilesize': 10, '_sDownloadUrl': 'http://127.0.0.1:%d/dl/bridge.zip' % port}],
            '_aArchivedFiles': [],
        }
        CATALOG['details'] = json.dumps(details)
        archive_bytes = io.BytesIO()
        with zipfile.ZipFile(archive_bytes, 'w') as z:
            z.writestr('race/course.bin', 'bridge')
        CATALOG['archive'] = archive_bytes.getvalue()

        def ok(command, **fields):
            result = self.result(self.send(command, **fields))
            self.assertEqual('success', result['outcome'], result)
            return result['data']

        search = ok('mods-search')
        self.assertTrue(search['isComplete'])
        self.assertEqual(1, search['recordCount'])
        row = search['results'][0]
        self.assertEqual(77, row['id']); self.assertEqual('Bridge Course', row['name'])
        self.assertEqual('Builder 77', row['author']); self.assertTrue(row['usesPatches'])

        detail = ok('mods-details', modId=77)
        self.assertEqual('Bridge Course', detail['name'])
        self.assertEqual('<p>Rides across the bay.</p>', detail['text'])
        self.assertEqual('bridge.zip', detail['files'][0]['fileName'])

        installed = ok('mods-install', url=detail['files'][0]['downloadUrl'], modTitle='Bridge Course', author='Builder 77', modId=77)
        saved = next(mod for mod in installed['mods'] if mod['title'] == 'Bridge Course')
        self.assertEqual('Builder 77', saved['author']); self.assertEqual(77, saved['modID'])
        self.assertTrue((self.root / 'managed/Mods/Bridge Course/race/course.bin').exists())
        downloads = self.root / 'managed/Mods/.downloads'
        self.assertEqual([], [] if not downloads.exists() else list(downloads.iterdir()))
        duplicate = self.result(self.send('mods-install', url='http://127.0.0.1:%d/dl/bridge.zip' % port,
            modTitle='Bridge Course', author='Builder 77', modId=77))
        self.assertEqual('failure', duplicate['outcome'])
        self.assertEqual([], list((self.root / 'managed').glob('.ww-mod-*')))
        self.assertEqual([], [] if not downloads.exists() else list(downloads.iterdir()))

    def test_mod_catalog_rejects_insecure_download(self):
        # Only the override host (or https) is accepted; a foreign http:// URL must fail before download.
        result = self.result(self.send('mods-install', url='http://example.test/dl/bridge.zip',
            modTitle='Foreign', author='x', modId=1))
        self.assertEqual('failure', result['outcome'])
        downloads = self.root / 'managed/Mods/.downloads'
        self.assertEqual([], [] if not downloads.exists() else list(downloads.iterdir()))

if __name__ == '__main__': unittest.main()
