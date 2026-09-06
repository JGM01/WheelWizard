#!/usr/bin/env python3
"""Run the real helper protocol, preserving all events. No simulated build success."""
import argparse, json, os, pathlib, subprocess, sys, threading, uuid
p = argparse.ArgumentParser()
p.add_argument('workspace', type=pathlib.Path)
p.add_argument('wbfs', type=pathlib.Path)
p.add_argument('commands', nargs='+', help='preflight, install, build:base, build:retro-rewind, launch:base, launch:retro-rewind, status')
p.add_argument('--volume', type=float, default=1.0)
p.add_argument('--resolution', type=float, default=1.0)
p.add_argument('--stop-after', type=float, help='Stop a launched game after this many seconds; startup verification only')
a = p.parse_args()
native = pathlib.Path(__file__).resolve().parent
app = native / 'artifacts/WheelWizardNative.app/Contents/Resources'
helper = app / 'helper/WheelWizard.Host'
setup = {'wbfs': str(a.wbfs.resolve()), 'workspace': str(a.workspace.resolve()), 'cmake': '/opt/homebrew/bin/cmake', 'ninja': '/opt/homebrew/bin/ninja', 'nodtool': str(app / 'tools/nodtool'), 'translator': str(app / 'tools/translator/Translator.Cli')}
child = subprocess.Popen([str(helper)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr, text=True, bufsize=1)
lock = threading.Lock()
def send(command, product=None):
    request = {'version': 1, 'id': str(uuid.uuid4()), 'command': command, 'setup': setup}
    if product: request['product'] = product
    if command == 'config-write': request['settings'] = {'volume': a.volume, 'resolutionMultiplier': a.resolution}
    with lock: child.stdin.write(json.dumps(request) + '\n'); child.stdin.flush()
    return request['id']
try:
    for entry in a.commands:
        command, _, product = entry.partition(':')
        request_id = send(command, product or None)
        timer = None
        if command == 'launch' and a.stop_after:
            timer = threading.Timer(a.stop_after, lambda: send('cancel')); timer.start()
        while True:
            line = child.stdout.readline()
            if not line: raise RuntimeError('Helper terminated unexpectedly')
            print(line, end='', flush=True)
            event = json.loads(line)
            if event.get('id') == request_id and event.get('kind') == 'result':
                if timer: timer.cancel()
                if event['outcome'] == 'failure': sys.exit(1)
                if command == 'preflight' and event.get('data', {}).get('errors'): sys.exit(1)
                break
finally:
    child.stdin.close()
    child.wait(timeout=30)
