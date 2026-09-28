#!/usr/bin/env python3
"""Offline, non-interactive backup/restore. Caller must stop the server first."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import time


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def tree_files(root):
    for path in root.rglob('*'):
        if path.is_symlink():
            raise ValueError('SYMLINK_IN_BACKUP_SOURCE')
        if path.is_file():
            yield path


def targets(config, program):
    values = json.loads(config.read_text(encoding='utf-8-sig'))
    data = Path(os.environ.get('RM_DATA_DIR', values.get('data-dir', '/var/lib/resourcemanager'))).resolve()
    uploads = Path(os.environ.get('RM_UPLOAD_DIR', values.get('upload-dir', str(data / 'uploads')))).resolve()
    result = {'config': config.parent.resolve(), 'program': program.resolve(), 'data': data}
    if not uploads.is_relative_to(data):
        result['uploads'] = uploads
    for path in result.values():
        if path == Path(path.anchor) or len(path.parts) < 3:
            raise ValueError('UNSAFE_TARGET_DIRECTORY')
    paths = list(result.values())
    if any(a.is_relative_to(b) or b.is_relative_to(a) for i, a in enumerate(paths) for b in paths[i + 1:]):
        raise ValueError('OVERLAPPING_TARGET_DIRECTORIES')
    return result


def run(args):
    destination = args.backup.resolve()
    locations = targets(args.config.resolve(), args.program.resolve())
    if any(destination.is_relative_to(p) or p.is_relative_to(destination) for p in locations.values()):
        raise ValueError('BACKUP_OVERLAPS_LIVE_DIRECTORIES')
    if args.action == 'backup':
        destination.mkdir(mode=0o700, parents=True, exist_ok=False)
        manifest = {'format': 1, 'targets': {k: str(v) for k, v in locations.items()}, 'files': {}, 'ownership': {}}
        for key, source in locations.items():
            list(tree_files(source))  # Reject links before copying anything from this tree.
            if source.is_symlink():
                raise ValueError('SYMLINK_IN_BACKUP_SOURCE')
            if source.exists():
                for original in [source, *source.rglob('*')]:
                    stat = original.stat()
                    manifest['ownership'][key + '/' + original.relative_to(source).as_posix()] = [stat.st_uid, stat.st_gid]
                shutil.copytree(source, destination / key)
            else:
                (destination / key).mkdir()
            for file in tree_files(destination / key):
                manifest['files'][file.relative_to(destination).as_posix()] = digest(file)
        (destination / 'manifest.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    else:
        manifest = json.loads((destination / 'manifest.json').read_text(encoding='utf-8'))
        if manifest.get('format') != 1 or manifest['targets'] != {k: str(v) for k, v in locations.items()}:
            raise ValueError('RESTORE_TARGETS_DO_NOT_MATCH_CONFIGURATION')
        actual = {f.relative_to(destination).as_posix(): digest(f) for key in locations for f in tree_files(destination / key)}
        if actual != manifest['files']:
            raise ValueError('BACKUP_HASH_MISMATCH')
        suffix = '.before-restore-' + str(time.time_ns())
        # Preserve old trees for recovery. No recursive deletion of live data.
        for key, target in locations.items():
            staged = target.with_name(target.name + '.restore-' + str(os.getpid()))
            shutil.copytree(destination / key, staged)
            if hasattr(os, 'geteuid') and os.geteuid() == 0:
                for copied in [staged, *staged.rglob('*')]:
                    owner = manifest.get('ownership', {}).get(key + '/' + copied.relative_to(staged).as_posix())
                    if owner is not None:
                        os.chown(copied, *owner)
            if target.exists():
                target.rename(target.with_name(target.name + suffix))
            staged.rename(target)
    print(json.dumps({'ok': True, 'code': args.action.upper() + '_COMPLETE', 'backup': str(destination)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['backup', 'restore'])
    parser.add_argument('--config', type=Path, default=Path('/etc/resourcemanager/server.json'))
    parser.add_argument('--program', type=Path, default=Path('/opt/resourcemanager'))
    parser.add_argument('--backup', type=Path, required=True)
    try:
        run(parser.parse_args())
    except Exception as error:
        print(json.dumps({'ok': False, 'code': 'MAINTENANCE_FAILED', 'error': str(error)}), file=sys.stderr)
        sys.exit(3)
