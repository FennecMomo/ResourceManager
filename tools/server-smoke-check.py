"""Black-box packaged server checks; owns only an isolated temporary server process."""
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

exe = str(Path(sys.argv[1]).resolve())
version = subprocess.check_output([exe, '--version'], text=True,
    creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0).strip()


def port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def request(url, body=None):
    req = urllib.request.Request(url, data=body)
    if body is not None:
        req.add_header('Content-Type', 'application/json')
    try:
        with urllib.request.urlopen(req, timeout=3) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


with tempfile.TemporaryDirectory(prefix='rm-packaged-server-') as directory:
    api, admin = port(), port()
    while api == admin:
        admin = port()
    args = ['--data-dir', directory, '--server-name', 'doctor', '--api-address', '127.0.0.1', '--api-port', str(api), '--admin-port', str(admin), '--discovery-enabled', 'false']
    flags = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
    env = {key: value for key, value in os.environ.items() if not key.startswith('RM_')}
    for command in ['check-config', 'doctor', 'migrate', 'migrate']:
        result = subprocess.run([exe, command, *args], capture_output=True, text=True, env=env, creationflags=flags)
        assert result.returncode == 0, result.stderr
        assert json.loads(result.stdout)['code'] == 'OK'
    invalid = subprocess.run([exe, 'check-config', '--api-port', 'nope'], capture_output=True, text=True, env=env, creationflags=flags)
    assert invalid.returncode == 2 and json.loads(invalid.stderr)['code'] == 'CONFIG_INVALID'
    with open(Path(directory) / 'stdout.log', 'w') as log:
        process = subprocess.Popen([exe, *args], stdout=log, stderr=log, env=env, creationflags=flags)
        try:
            base = f'http://127.0.0.1:{api}'
            management = f'http://127.0.0.1:{admin}'
            for attempt in range(100):
                try:
                    if request(base + '/health/ready')[0] == 200:
                        break
                except OSError:
                    pass
                assert process.poll() is None, 'Isolated server failed to start'
                time.sleep(.1)
            else:
                raise AssertionError('Isolated server not ready')
            capabilities = json.loads(request(base + '/api/v1/workspace/capabilities')[1])
            assert capabilities['version'] == version and capabilities['protocol'] == 'workspace-v1'
            assert request(base + '/admin/api/workspace/members')[0] == 403
            assert request(base + '/admin/api/issues')[0] == 403
            assert request(management + '/admin/api/workspace/members')[0] == 200
            assert request(management + '/admin/api/issues')[0] == 200
            assert json.loads(request(base + '/api/v1/capabilities')[1])['protocol'] == 'feedback-v1'
            assert request(base + '/api/v1/workspace/members')[0] == 401
            assert request(base + '/api/v1/workspace/join', b' ' * (1024 * 1024 + 1))[0] == 413
            print('PASS: packaged server CLI, migration, health, protocols, admin isolation, authentication, request limit')
        finally:
            # This subprocess was created here with a new temporary data directory and ports.
            # Never discovers, stops, or updates the user's independent server.
            process.terminate()
            process.wait(timeout=15)
