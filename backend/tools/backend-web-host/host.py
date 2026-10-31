#!/usr/bin/env python3
"""Publish and run a matched, loopback-only Linux x64 Web development backend."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import re
import signal
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import urlsplit

REPO = Path(__file__).resolve().parents[2]
LOCK = '/tmp/mkg-backend-b4-dotnet.lock'
ROLES = {'server': 'MakoKaraGenerator.Backend.Server', 'worker': 'MakoKaraGenerator.Backend.Worker'}
LIMIT_OPTIONS = {
    'max-asset-bytes': 'MAKO_BACKEND_MAX_ASSET_BYTES',
    'max-asset-quota-bytes': 'MAKO_BACKEND_MAX_ASSET_QUOTA_BYTES',
    'export-timeout-seconds': 'MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS',
    'export-max-frames': 'MAKO_BACKEND_EXPORT_MAX_FRAMES',
    'export-max-duration-us': 'MAKO_BACKEND_EXPORT_MAX_DURATION_US',
    'export-max-fps': 'MAKO_BACKEND_EXPORT_MAX_FPS',
    'export-max-canvas-width': 'MAKO_BACKEND_EXPORT_MAX_CANVAS_WIDTH',
    'export-max-canvas-height': 'MAKO_BACKEND_EXPORT_MAX_CANVAS_HEIGHT',
    'export-max-raw-frame-bytes': 'MAKO_BACKEND_EXPORT_MAX_RAW_FRAME_BYTES',
    'export-max-media-bytes': 'MAKO_BACKEND_EXPORT_MAX_MEDIA_BYTES',
    'export-max-audio-bytes': 'MAKO_BACKEND_EXPORT_MAX_AUDIO_BYTES',
    'export-max-compressed-audio-duration-us': 'MAKO_BACKEND_EXPORT_MAX_COMPRESSED_AUDIO_DURATION_US',
    'export-max-artifact-storage-bytes': 'MAKO_BACKEND_EXPORT_MAX_ARTIFACT_STORAGE_BYTES',
}


def resource_limit(value):
    if value.lower() in ('none', 'unlimited'):
        return None
    if not value.isascii() or not value.isdecimal() or not 0 < int(value) <= 2 ** 63 - 1:
        raise argparse.ArgumentTypeError('resource limit must be a positive Int64 or none')
    return int(value)


def resource_environment(args):
    result = {name: os.environ[name] for name in LIMIT_OPTIONS.values() if name in os.environ}
    for option, name in LIMIT_OPTIONS.items():
        field = option.replace('-', '_')
        if hasattr(args, field):
            value = getattr(args, field)
            if value is None:
                result.pop(name, None)
            else:
                result[name] = str(value)
    return result


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def inventory(root):
    return {str(p.relative_to(root)): digest(p) for p in sorted(root.rglob('*')) if p.is_file()}


def validate_dist(dist):
    manifest = json.loads((dist / 'deployment.json').read_text())
    if manifest.get('rid') != 'linux-x64' or manifest.get('selfContained') is not True:
        raise ValueError('deployment.json is not a self-contained linux-x64 deployment')
    for name, expected in manifest['files'].items():
        path = dist / name
        if path.is_symlink() or not path.is_file() or digest(path) != expected:
            raise ValueError(f'deployment integrity mismatch: {name}')
    actual = inventory(dist)
    actual.pop('deployment.json', None)
    if actual != manifest['files']:
        raise ValueError('deployment contains extra or missing files; publish a fresh directory')
    for role, app in ROLES.items():
        if not os.access(dist / role / app, os.X_OK):
            raise ValueError(f'{role} apphost is not executable')
    refs = dist / 'worker' / 'script-refs' / 'net10.0'
    if not (refs / 'System.Runtime.dll').is_file():
        raise ValueError('complete Worker script-refs are required')
    return manifest


def source_inputs():
    paths = subprocess.run(['git', 'ls-files', '-co', '--exclude-standard', '--', 'src',
                            'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
                            'global.json', 'NuGet.Config', 'NuGet.config'], cwd=REPO,
                           check=True, capture_output=True, text=True).stdout.splitlines()
    return {p: digest(REPO / p) for p in sorted(set(paths)) if (REPO / p).is_file()}


def publish(args):
    dist = args.dist.resolve()
    dist.mkdir(parents=True, exist_ok=False)  # Never merge or delete a caller's deployment.
    git = subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=REPO, check=True, capture_output=True, text=True).stdout.strip()
    frozen_inputs = source_inputs()
    diff = subprocess.run(['git', 'diff', '--binary', 'HEAD', '--', 'src', 'Directory.Build.props',
                           'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.Config',
                           'NuGet.config'], cwd=REPO, check=True, capture_output=True).stdout
    version = re.search(r'<VersionPrefix>(.*?)</VersionPrefix>', (REPO / 'Directory.Build.props').read_text()).group(1)
    for role, app in ROLES.items():
        command = ['flock', '-o', LOCK, 'dotnet', 'publish', str(REPO / 'src' / app / (app + '.csproj')),
                   '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true', '-m:1',
                   '/p:UseSharedCompilation=false', '-o', str(dist / role)]
        with (dist / (role + '-publish.log')).open('w') as log:
            result = subprocess.run(command, cwd=REPO, stdout=log, stderr=subprocess.STDOUT)
        if result.returncode:
            raise ValueError(f'{role} publish failed ({result.returncode}); see {dist / (role + "-publish.log")}')
        print(f'Published {role}', flush=True)
    if source_inputs() != frozen_inputs:
        raise ValueError('production inputs changed during publish; preserve this failed directory and publish a fresh one')
    files = inventory(dist)
    manifest = {'version': version, 'sourceCommit': git, 'rid': 'linux-x64', 'selfContained': True,
                'configuration': 'Release', 'sourceInputs': frozen_inputs,
                'sourceDiffSha256': hashlib.sha256(diff).hexdigest(),
                'sourceDirty': bool(diff) or bool(subprocess.run(['git', 'ls-files', '--others', '--exclude-standard', '--', 'src'], cwd=REPO, check=True, capture_output=True).stdout),
                'files': files}
    (dist / 'deployment.json').write_text(json.dumps(manifest, indent=2) + '\n')
    validate_dist(dist)
    print(f'DEPLOYMENT_READY {dist}', flush=True)
    return 0


def origin(value):
    parsed = urlsplit(value)
    if parsed.scheme not in ('http', 'https') or not parsed.hostname or parsed.path or parsed.query or parsed.fragment or parsed.username or parsed.password:
        raise argparse.ArgumentTypeError('origin must be an exact http(s) origin with no path or credentials')
    try:
        parsed.port
    except ValueError as error:
        raise argparse.ArgumentTypeError(str(error)) from error
    return value


def run(args):
    dist = args.dist.resolve()
    manifest = validate_dist(dist)
    for tool in (args.ffmpeg, args.ffprobe):
        if not tool.is_absolute() or not tool.is_file() or not os.access(tool, os.X_OK):
            raise ValueError(f'native tool must be an absolute executable file: {tool}')
    profile = args.profile.resolve()
    profile.mkdir(parents=True, exist_ok=True)
    for name in ('state', 'projects', 'runs'):
        (profile / name).mkdir(exist_ok=True)
    run_dir = Path(tempfile.mkdtemp(prefix='run-', dir=profile / 'runs'))
    run_dir.chmod(0o700)
    credential = run_dir / 'bootstrap.json'
    env = {k: v for k, v in os.environ.items() if not k.startswith('MAKO_BACKEND_')}
    env.update(resource_environment(args))
    env.update(MAKO_BACKEND_RESOURCE_ROOT=str(run_dir / 'resources'), MAKO_BACKEND_CREDENTIAL_FILE=str(credential),
               MAKO_BACKEND_STATE_ROOT=str(profile / 'state'),
               MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({'projects': str(profile / 'projects')}),
               MAKO_BACKEND_ALLOWED_ORIGINS=';'.join(args.origin),
               MAKO_BACKEND_WORKER_PATH=str(dist / 'worker' / ROLES['worker']),
               MAKO_BACKEND_FFMPEG_PATH=str(args.ffmpeg), MAKO_BACKEND_FFPROBE_PATH=str(args.ffprobe))
    lines = queue.Queue()
    process = subprocess.Popen([str(dist / 'server' / ROLES['server']), '--urls', 'http://127.0.0.1:0'],
                               cwd=REPO, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, start_new_session=True)
    previous = {}
    def forward(signum, _frame):
        if process.poll() is None:
            process.send_signal(signum)
    for signum in (signal.SIGINT, signal.SIGTERM):
        previous[signum] = signal.signal(signum, forward)
    def reader():
        for line in process.stdout:
            lines.put(line)
        lines.put(None)
    threading.Thread(target=reader, daemon=True).start()
    ready = False
    deadline = time.monotonic() + 60
    code = None
    eof = False
    try:
        with (run_dir / 'server.log').open('w') as log:
            while True:
                try:
                    line = lines.get(timeout=0.2)
                except queue.Empty:
                    line = ''
                if line is None:
                    eof = True
                if line:
                    # Program's startup log contains addresses/host only, never credentials.
                    log.write(line)
                    log.flush()
                    if line.startswith('BACKEND_READY '):
                        bootstrap = json.loads(credential.read_text())
                        if credential.stat().st_mode & 0o777 != 0o600:
                            raise ValueError('bootstrap credential file must have mode 0600')
                        addresses = bootstrap['addresses']
                        if not addresses or any(urlsplit(url).hostname != '127.0.0.1' for url in addresses):
                            raise ValueError('backend did not bind the requested loopback address')
                        print(line.rstrip(), flush=True)
                        print('WEB_BACKEND_READY ' + json.dumps({'addresses': addresses,
                              'hostInstanceId': bootstrap['hostInstanceId'], 'credentialFile': str(credential),
                              'runDirectory': str(run_dir), 'version': manifest['version'], 'pid': process.pid}), flush=True)
                        ready = True
                if not ready and time.monotonic() >= deadline:
                    raise ValueError(f'backend readiness timed out; see {run_dir / "server.log"}')
                if eof and process.poll() is not None:
                    code = process.wait()
                    break
    finally:
        if process.poll() is None:
            process.terminate()
            # Shutdown is cooperative: never kill Worker/native processes by name.
            process.wait()
        for signum, handler in previous.items():
            signal.signal(signum, handler)
        credential.unlink(missing_ok=True)  # This invocation owns this fresh private file only.
    if not ready or code:
        raise ValueError(f'backend exited {code}; see {run_dir / "server.log"}')
    print('WEB_BACKEND_STOPPED ' + json.dumps({'exitCode': code, 'runDirectory': str(run_dir)}), flush=True)
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    build = sub.add_parser('publish', help='publish matched Server + complete Worker into a NEW directory')
    build.add_argument('--dist', type=Path, required=True)
    serve = sub.add_parser('run', help='verify deployment, run in foreground, forward SIGINT/SIGTERM and drain')
    serve.add_argument('--dist', type=Path, required=True)
    serve.add_argument('--profile', type=Path, required=True)
    serve.add_argument('--origin', type=origin, action='append', required=True)
    serve.add_argument('--ffmpeg', type=Path, default=Path('/usr/bin/ffmpeg'))
    serve.add_argument('--ffprobe', type=Path, default=Path('/usr/bin/ffprobe'))
    for option, variable in LIMIT_OPTIONS.items():
        serve.add_argument('--' + option, type=resource_limit, default=argparse.SUPPRESS,
                           help=f'positive limit or none; overrides {variable}, omitted keeps that environment value')
    args = parser.parse_args()
    if platform.system() != 'Linux' or platform.machine() != 'x86_64':
        parser.error('this launcher is validated for Linux x64 only')
    try:
        return publish(args) if args.command == 'publish' else run(args)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f'backend-web-host: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
