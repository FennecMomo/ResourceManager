"""Exercise backup/restore against disposable folders, never a running server."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

script = Path(__file__).resolve().parents[1] / 'deploy/server/maintenance.py'
with tempfile.TemporaryDirectory(prefix='rm-maintenance-check-') as folder:
    root = Path(folder)
    config = root / 'config folder'
    program = root / 'program'
    data = root / 'data'
    uploads = root / 'separate uploads'
    for path in [config, program, data, uploads]:
        path.mkdir()
    (config / 'server.json').write_text(json.dumps({'data-dir': str(data), 'upload-dir': str(uploads)}))
    (program / 'server').write_text('old program')
    (data / 'identity').write_text('stable private identity')
    (uploads / 'resource').write_text('uploaded content')
    backup = root / 'backup'
    env = dict(os.environ)
    env.pop('RM_DATA_DIR', None)
    env.pop('RM_UPLOAD_DIR', None)
    def run(action, expected=0):
        result = subprocess.run([sys.executable, str(script), action, '--config', str(config / 'server.json'), '--program', str(program), '--backup', str(backup)], capture_output=True, text=True, env=env)
        assert result.returncode == expected, result.stderr
        return result
    run('backup')
    (data / 'identity').write_text('changed identity')
    (uploads / 'resource').write_text('changed bytes')
    (program / 'server').write_text('new program')
    run('restore')
    assert (data / 'identity').read_text() == 'stable private identity'
    assert (uploads / 'resource').read_text() == 'uploaded content'
    assert (program / 'server').read_text() == 'old program'
    assert list(root.glob('data.before-restore-*'))
    (backup / 'data/identity').write_text('tampered backup')
    result = run('restore', 3)
    assert 'BACKUP_HASH_MISMATCH' in result.stderr
    assert (data / 'identity').read_text() == 'stable private identity'
    print('PASS: offline backup/restore preserves config, identity, program and external uploads; retains previous trees; rejects corrupt backup before changing live data')
