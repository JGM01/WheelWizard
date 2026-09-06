#!/usr/bin/env python3
"""Integration tests against the real bundled helper; run build.sh first."""
import json, os, pathlib, queue, signal, subprocess, tempfile, threading, unittest, uuid
HELPER = pathlib.Path(__file__).resolve().parent / 'artifacts/WheelWizardNative.app/Contents/Resources/helper/WheelWizard.Host'

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
        self.child = subprocess.Popen([str(HELPER)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1, env={**os.environ, 'WHEELWIZARD_NATIVE_ROOT': str(self.root / 'managed'), 'DOTNET_ROOT': '/nonexistent', 'DOTNET_MULTILEVEL_LOOKUP': '0', 'PATH': '/usr/bin:/bin'})
        def reader():
            for line in self.child.stdout:
                try: self.events.put(json.loads(line))
                except ValueError: self.events.put({'invalid': line})
        threading.Thread(target=reader, daemon=True).start()
        threading.Thread(target=lambda: self.diagnostics.extend(self.child.stderr), daemon=True).start()
    def tearDown(self):
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

if __name__ == '__main__': unittest.main()
