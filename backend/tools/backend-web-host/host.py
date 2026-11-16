#!/usr/bin/env python3
"""Publish and run a matched, loopback-only Linux x64 / Windows x64 Web development backend."""
import argparse
import base64
from contextlib import contextmanager
import ctypes
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import time
from urllib.parse import urlsplit

if platform.system() == 'Windows':
    import ctypes.wintypes
    import msvcrt

IS_WINDOWS = platform.system() == 'Windows'
EXE_SUFFIX = '.exe' if IS_WINDOWS else ''
FILE_PERSISTENT_ACLS = 0x00000008
LOCKFILE_EXCLUSIVE_LOCK = 0x00000002

REPO = Path(__file__).resolve().parents[2]
LINUX_LOCK = '/tmp/mkg-backend-b4-dotnet.lock'
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

_CACHED_USER_SID = None
_VERIFIED_VOLUME_ROOTS = set()
_PROTECTED_DIRS = set()


def target_rid():
    system = platform.system()
    machine = platform.machine().upper()
    if system == 'Linux' and machine == 'X86_64':
        return 'linux-x64'
    if system == 'Windows' and machine in ('AMD64', 'X86_64'):
        return 'win-x64'
    raise ValueError(f'this launcher is validated for Linux x64 and Windows x64 only (got {system}/{platform.machine()})')


def role_executable(role):
    return ROLES[role] + EXE_SUFFIX


def default_windows_runtime_dir():
    local_app_data = os.environ.get('LOCALAPPDATA')
    base = Path(local_app_data) if local_app_data and Path(local_app_data).is_absolute() else (Path.home() / 'AppData' / 'Local')
    return base / 'makokara-generator'


def default_publish_lock_path():
    if IS_WINDOWS:
        return default_windows_runtime_dir() / 'locks' / 'dotnet-publish.lock'
    return Path(LINUX_LOCK)


def _current_user_sid():
    global _CACHED_USER_SID
    if _CACHED_USER_SID:
        return _CACHED_USER_SID
    out = subprocess.run(
        ['whoami.exe', '/user', '/fo', 'csv', '/nh'],
        check=True,
        capture_output=True,
        text=True,
        encoding='utf-8',
    ).stdout.strip()
    match = re.search(r'S-1-\d+(?:-\d+)+', out)
    if not match:
        raise ValueError(f'Unable to determine current Windows user SID: {out}')
    _CACHED_USER_SID = match.group(0)
    return _CACHED_USER_SID


def _assert_no_symlink_ancestors(target: Path):
    cursor = Path(os.path.abspath(target))
    while True:
        if cursor.is_symlink() or getattr(cursor, 'is_junction', lambda: False)():
            raise ValueError(f'Private path cannot be or traverse a symlink: {cursor}')
        if cursor.parent == cursor:
            break
        cursor = cursor.parent


def _assert_windows_persistent_acl_volume(target: Path):
    existing = target.resolve()
    while not existing.exists():
        if existing.parent == existing:
            raise ValueError(f'Target volume does not exist: {target}')
        existing = existing.parent
    drive_key = (existing.drive or str(existing)).upper()
    if drive_key in _VERIFIED_VOLUME_ROOTS:
        return
    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel32.GetVolumePathNameW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.wintypes.DWORD]
    kernel32.GetVolumePathNameW.restype = ctypes.wintypes.BOOL
    kernel32.GetVolumeInformationW.argtypes = [
        ctypes.c_wchar_p,
        ctypes.c_wchar_p,
        ctypes.wintypes.DWORD,
        ctypes.POINTER(ctypes.wintypes.DWORD),
        ctypes.POINTER(ctypes.wintypes.DWORD),
        ctypes.POINTER(ctypes.wintypes.DWORD),
        ctypes.c_wchar_p,
        ctypes.wintypes.DWORD,
    ]
    kernel32.GetVolumeInformationW.restype = ctypes.wintypes.BOOL

    vol_buf = ctypes.create_unicode_buffer(1024)
    if not kernel32.GetVolumePathNameW(str(existing), vol_buf, len(vol_buf)):
        raise ctypes.WinError(ctypes.get_last_error())
    flags = ctypes.wintypes.DWORD(0)
    fs_buf = ctypes.create_unicode_buffer(256)
    if not kernel32.GetVolumeInformationW(vol_buf.value, None, 0, None, None, ctypes.byref(flags), fs_buf, len(fs_buf)):
        raise ctypes.WinError(ctypes.get_last_error())
    if (flags.value & FILE_PERSISTENT_ACLS) == 0:
        raise ValueError(
            f'Private runtime directory requires NTFS FILE_PERSISTENT_ACLS (0x8 missing on {vol_buf.value}, fs={fs_buf.value}): {target}'
        )
    if existing.drive and vol_buf.value.upper().rstrip('\\') == existing.drive.upper():
        _VERIFIED_VOLUME_ROOTS.add(drive_key)


def _windows_directory_acl(target: Path, sid: str):
    # Replace the entire Access section: icacls /grant:r retains foreign ACEs.
    # Return the actual post-write ACL, never trust a successful setter alone.
    script = r"""
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Stop'
$acl = Get-Acl -LiteralPath $env:MAKO_ACL_TARGET
$rules = @($acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier]))
$safe = $acl.AreAccessRulesProtected -and $rules.Count -eq 1
if ($safe) {
  $rule = $rules[0]
  $safe = $rule.IdentityReference.Value -eq $env:MAKO_ACL_SID -and $rule.AccessControlType.ToString() -eq 'Allow' -and [int]$rule.FileSystemRights -eq 2032127 -and [int]$rule.InheritanceFlags -eq 3 -and [int]$rule.PropagationFlags -eq 0
}
if (-not $safe) {
  $access = New-Object System.Security.AccessControl.DirectorySecurity
  $access.SetSecurityDescriptorSddlForm("D:P(A;OICI;FA;;;" + $env:MAKO_ACL_SID + ")", [System.Security.AccessControl.AccessControlSections]::Access)
  [System.IO.Directory]::SetAccessControl($env:MAKO_ACL_TARGET, $access)
}
$acl = Get-Acl -LiteralPath $env:MAKO_ACL_TARGET
$items = @($acl.Access | ForEach-Object {
  [PSCustomObject]@{ sid = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value; type = $_.AccessControlType.ToString(); rights = [int]$_.FileSystemRights; inheritance = [int]$_.InheritanceFlags; propagation = [int]$_.PropagationFlags }
})
ConvertTo-Json -InputObject @{ protected = $acl.AreAccessRulesProtected; rules = $items } -Compress
"""
    script = 'try {\n' + script + '\n} catch { [Console]::WriteLine($_.Exception.Message); exit 1 }'
    encoded = base64.b64encode(script.encode('utf-16le')).decode('ascii')
    child_env = {**os.environ, 'MAKO_ACL_TARGET': str(target), 'MAKO_ACL_SID': sid}
    child_env = {key: value for key, value in child_env.items() if key.upper() != 'PSMODULEPATH'}
    # Do not inherit PowerShell 7 module paths in Windows PowerShell 5.1.
    result = subprocess.run(
        ['powershell.exe', '-NoProfile', '-NonInteractive', '-EncodedCommand', encoded],
        env=child_env,
        check=False, stdin=subprocess.DEVNULL, capture_output=True, text=True, encoding='utf-8', errors='replace',
        creationflags=subprocess.CREATE_NO_WINDOW if IS_WINDOWS else 0,
    )
    if result.returncode:
        # Only this fixed ACL script runs here; no tokens or user file contents
        # enter it. Preserve its diagnostic instead of the opaque encoded argv.
        detail = (result.stderr or result.stdout).strip()
        message = f'Private directory ACL initialization failed for {target}: {detail}'
        # The launcher may inherit a legacy console encoding while its caller
        # reads UTF-8. Keep this startup diagnostic unambiguous on that pipe.
        raise ValueError(message.encode('ascii', errors='backslashreplace').decode('ascii'))
    return json.loads(result.stdout)


def _assert_private_windows_acl(acl, sid):
    expected = {'sid': sid, 'type': 'Allow', 'rights': 2032127, 'inheritance': 3, 'propagation': 0}
    if acl.get('protected') is not True or acl.get('rules') != [expected]:
        raise ValueError('Private directory requires a protected current-user-only inheritable DACL')


def ensure_private_dir(target: Path):
    # resolve() erases the links that this check must reject.
    resolved = Path(os.path.abspath(target))
    _assert_no_symlink_ancestors(resolved)
    if not IS_WINDOWS:
        resolved.mkdir(parents=True, exist_ok=True, mode=0o700)
        if resolved.is_symlink() or not resolved.is_dir():
            raise ValueError(f'Private directory cannot be a symlink: {resolved}')
        resolved.chmod(0o700)
        return resolved

    norm_key = str(resolved).lower()
    # Only this exact path has been verified. A protected ancestor says nothing
    # about explicit ACEs or disabled inheritance on an existing child. Same-user
    # ACL mutation after admission is outside this process-local trust boundary.
    if norm_key in _PROTECTED_DIRS:
        if not resolved.is_dir():
            raise ValueError(f'Private directory must exist: {resolved}')
        return resolved
    _assert_windows_persistent_acl_volume(resolved)
    resolved.mkdir(parents=True, exist_ok=True)
    _assert_no_symlink_ancestors(resolved)
    if not resolved.is_dir():
        raise ValueError(f'Private directory cannot be a symlink: {resolved}')
    sid = _current_user_sid()
    _assert_private_windows_acl(_windows_directory_acl(resolved, sid), sid)
    _PROTECTED_DIRS.add(norm_key)
    return resolved


if IS_WINDOWS:
    class _OVERLAPPED(ctypes.Structure):
        _fields_ = [
            ('Internal', ctypes.c_void_p),
            ('InternalHigh', ctypes.c_void_p),
            ('Offset', ctypes.wintypes.DWORD),
            ('OffsetHigh', ctypes.wintypes.DWORD),
            ('hEvent', ctypes.wintypes.HANDLE),
        ]


@contextmanager
def acquire_publish_lock(lock_path: Path | None = None):
    target = lock_path or default_publish_lock_path()
    ensure_private_dir(target.parent)
    if not IS_WINDOWS:
        import fcntl
        with target.open('a+b') as stream:
            fcntl.flock(stream.fileno(), fcntl.LOCK_EX)
            try:
                yield target
            finally:
                fcntl.flock(stream.fileno(), fcntl.LOCK_UN)
        return

    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel32.LockFileEx.argtypes = [
        ctypes.wintypes.HANDLE,
        ctypes.wintypes.DWORD,
        ctypes.wintypes.DWORD,
        ctypes.wintypes.DWORD,
        ctypes.wintypes.DWORD,
        ctypes.POINTER(_OVERLAPPED),
    ]
    kernel32.LockFileEx.restype = ctypes.wintypes.BOOL
    kernel32.UnlockFileEx.argtypes = [
        ctypes.wintypes.HANDLE,
        ctypes.wintypes.DWORD,
        ctypes.wintypes.DWORD,
        ctypes.wintypes.DWORD,
        ctypes.POINTER(_OVERLAPPED),
    ]
    kernel32.UnlockFileEx.restype = ctypes.wintypes.BOOL

    with target.open('a+b') as stream:
        handle = msvcrt.get_osfhandle(stream.fileno())
        overlapped = _OVERLAPPED()
        if not kernel32.LockFileEx(handle, LOCKFILE_EXCLUSIVE_LOCK, 0, 1, 0, ctypes.byref(overlapped)):
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            yield target
        finally:
            kernel32.UnlockFileEx(handle, 0, 1, 0, ctypes.byref(overlapped))


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
    return {p.relative_to(root).as_posix(): digest(p) for p in sorted(root.rglob('*')) if p.is_file()}


def validate_script_abi(dist):
    # This exact assembly is appended to the script-reference identity in both processes.
    # SourceLink changes its bytes even when production inputs and product versions match.
    name = 'MakoKaraGenerator.Scripting.Abstractions.dll'
    paths = [dist / role / name for role in ROLES]
    if any(path.is_symlink() or not path.is_file() for path in paths) or digest(paths[0]) != digest(paths[1]):
        raise ValueError(f'Server/Worker {name} bytes differ or are missing; publish a fresh matched deployment')


def validate_dist(dist):
    rid = target_rid()
    manifest = json.loads((dist / 'deployment.json').read_text(encoding='utf-8'))
    if manifest.get('rid') != rid or manifest.get('selfContained') is not True:
        raise ValueError(f'deployment.json is not a self-contained {rid} deployment')
    for name, expected in manifest['files'].items():
        path = dist / name
        if path.is_symlink() or not path.is_file() or digest(path) != expected:
            raise ValueError(f'deployment integrity mismatch: {name}')
    actual = inventory(dist)
    actual.pop('deployment.json', None)
    if actual != manifest['files']:
        raise ValueError('deployment contains extra or missing files; publish a fresh directory')
    for role in ROLES:
        app_path = dist / role / role_executable(role)
        if app_path.is_symlink() or not app_path.is_file() or (not IS_WINDOWS and not os.access(app_path, os.X_OK)):
            raise ValueError(f'{role} apphost is not executable')
    refs = dist / 'worker' / 'script-refs' / 'net10.0'
    if not (refs / 'System.Runtime.dll').is_file():
        raise ValueError('complete Worker script-refs are required')
    validate_script_abi(dist)
    return manifest


def source_inputs():
    paths = subprocess.run(
        ['git', 'ls-files', '-co', '--exclude-standard', '--', 'src',
         'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
         'global.json', 'NuGet.Config', 'NuGet.config'],
        cwd=REPO,
        check=True,
        capture_output=True,
        text=True,
        encoding='utf-8',
    ).stdout.splitlines()
    return {p: digest(REPO / p) for p in sorted(set(paths)) if (REPO / p).is_file()}


def git_head():
    return subprocess.run(
        ['git', 'rev-parse', 'HEAD'],
        cwd=REPO,
        check=True,
        capture_output=True,
        text=True,
        encoding='utf-8',
    ).stdout.strip()


def require_publish_head(expected):
    if git_head() != expected:
        raise ValueError('Git HEAD changed during publish; preserve this failed directory and publish a fresh one')


def publish(args):
    rid = target_rid()
    dist = args.dist.resolve()
    dist.mkdir(parents=True, exist_ok=False)  # Never merge or delete a caller's deployment.
    git = git_head()
    frozen_inputs = source_inputs()
    diff = subprocess.run(
        ['git', 'diff', '--binary', 'HEAD', '--', 'src', 'Directory.Build.props',
         'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.Config',
         'NuGet.config'],
        cwd=REPO,
        check=True,
        capture_output=True,
    ).stdout
    version = re.search(
        r'<VersionPrefix>(.*?)</VersionPrefix>',
        (REPO / 'Directory.Build.props').read_text(encoding='utf-8'),
    ).group(1)
    for role, app in ROLES.items():
        require_publish_head(git)
        command = [
            'dotnet', 'publish', str(REPO / 'src' / app / (app + '.csproj')),
            '-c', 'Release', '-r', rid, '--self-contained', 'true', '-m:1',
            '/p:UseSharedCompilation=false', '-o', str(dist / role),
        ]
        with (dist / (role + '-publish.log')).open('w', encoding='utf-8') as log:
            if IS_WINDOWS:
                with acquire_publish_lock():
                    result = subprocess.run(command, cwd=REPO, stdout=log, stderr=subprocess.STDOUT)
            else:
                result = subprocess.run(['flock', '-o', LINUX_LOCK, *command], cwd=REPO, stdout=log, stderr=subprocess.STDOUT)
        require_publish_head(git)
        if result.returncode:
            raise ValueError(f'{role} publish failed ({result.returncode}); see {dist / (role + "-publish.log")}')
        print(f'Published {role}', flush=True)
    if source_inputs() != frozen_inputs:
        raise ValueError('production inputs changed during publish; preserve this failed directory and publish a fresh one')
    files = inventory(dist)
    manifest = {
        'version': version,
        'sourceCommit': git,
        'rid': rid,
        'selfContained': True,
        'configuration': 'Release',
        'sourceInputs': frozen_inputs,
        'sourceDiffSha256': hashlib.sha256(diff).hexdigest(),
        'sourceDirty': bool(diff) or bool(
            subprocess.run(
                ['git', 'ls-files', '--others', '--exclude-standard', '--', 'src'],
                cwd=REPO,
                check=True,
                capture_output=True,
            ).stdout
        ),
        'files': files,
    }
    require_publish_head(git)
    validate_script_abi(dist)
    (dist / 'deployment.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    validate_dist(dist)
    require_publish_head(git)
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


def sanitized_backend_environment(source):
    """Keep the benchmark provider opt-in exact; all other backend knobs are owned here."""
    env = {k: v for k, v in source.items() if not k.startswith('MAKO_BACKEND_')}
    if source.get('MAKO_BACKEND_GPU_PREVIEW_BENCHMARK') == '1':
        env['MAKO_BACKEND_GPU_PREVIEW_BENCHMARK'] = '1'
    return env


def resolve_default_tool(name):
    found = shutil.which(name)
    if found:
        return Path(found)
    if IS_WINDOWS:
        local_app_data = os.environ.get('LOCALAPPDATA')
        if local_app_data:
            winget_link = Path(local_app_data) / 'Microsoft' / 'WinGet' / 'Links' / f'{name}.exe'
            if winget_link.is_file():
                return winget_link
            winget_packages = Path(local_app_data) / 'Microsoft' / 'WinGet' / 'Packages'
            if winget_packages.is_dir():
                for pkg_dir in sorted(winget_packages.iterdir()):
                    if not pkg_dir.is_dir() or 'ffmpeg' not in pkg_dir.name.lower():
                        continue
                    for match in sorted(pkg_dir.rglob(f'{name}.exe')):
                        if match.is_file():
                            return match
        return None
    posix_sys = Path('/usr/bin') / name
    return posix_sys if posix_sys.is_file() else None


def _request_graceful_shutdown(process):
    if process.poll() is not None:
        return
    if process.stdin is not None:
        try:
            process.stdin.write('SHUTDOWN\n')
            process.stdin.flush()
            process.stdin.close()
        except OSError:
            pass
    if not IS_WINDOWS and process.poll() is None:
        process.terminate()


def run(args):
    dist = args.dist.resolve()
    manifest = validate_dist(dist)
    ffmpeg_path = args.ffmpeg or resolve_default_tool('ffmpeg')
    ffprobe_path = args.ffprobe or resolve_default_tool('ffprobe')
    if ffmpeg_path is None or ffprobe_path is None:
        raise ValueError('Cannot locate ffmpeg/ffprobe on PATH; pass --ffmpeg and --ffprobe explicitly.')
    for tool in (ffmpeg_path, ffprobe_path):
        if not tool.is_absolute() or not tool.is_file() or (not IS_WINDOWS and not os.access(tool, os.X_OK)):
            raise ValueError(f'native tool must be an absolute executable file: {tool}')

    profile = ensure_private_dir(args.profile)
    for name in ('state', 'projects', 'runs'):
        ensure_private_dir(profile / name)
    run_dir = Path(tempfile.mkdtemp(prefix='run-', dir=profile / 'runs'))
    ensure_private_dir(run_dir)
    credential = run_dir / 'bootstrap.json'

    env = sanitized_backend_environment(os.environ)
    env.update(resource_environment(args))
    env.update(
        MAKO_BACKEND_RESOURCE_ROOT=str(run_dir / 'resources'),
        MAKO_BACKEND_CREDENTIAL_FILE=str(credential),
        MAKO_BACKEND_STATE_ROOT=str(profile / 'state'),
        MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({'projects': str(profile / 'projects')}),
        MAKO_BACKEND_ALLOWED_ORIGINS=';'.join(args.origin),
        MAKO_BACKEND_WORKER_PATH=str(dist / 'worker' / role_executable('worker')),
        MAKO_BACKEND_FFMPEG_PATH=str(ffmpeg_path),
        MAKO_BACKEND_FFPROBE_PATH=str(ffprobe_path),
        MAKO_BACKEND_SHUTDOWN_STDIN='1',
    )
    lines = queue.Queue()
    popen_kwargs = {
        'cwd': REPO,
        'env': env,
        'stdin': subprocess.PIPE,
        'stdout': subprocess.PIPE,
        'stderr': subprocess.STDOUT,
        'text': True,
        'encoding': 'utf-8',
    }
    if IS_WINDOWS:
        popen_kwargs['creationflags'] = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
    else:
        popen_kwargs['start_new_session'] = True

    process = subprocess.Popen([str(dist / 'server' / role_executable('server')), '--urls', 'http://127.0.0.1:0'], **popen_kwargs)
    previous = {}

    def forward(signum, _frame):
        if process.poll() is None:
            if IS_WINDOWS:
                _request_graceful_shutdown(process)
            else:
                process.send_signal(signum)

    handled_signals = [signal.SIGINT, signal.SIGTERM]
    if IS_WINDOWS and hasattr(signal, 'SIGBREAK'):
        handled_signals.append(signal.SIGBREAK)
    for signum in handled_signals:
        previous[signum] = signal.signal(signum, forward)

    def reader():
        for line in process.stdout:
            lines.put(line)
        lines.put(None)

    threading.Thread(target=reader, daemon=True).start()
    if os.environ.get('MAKO_HOST_SHUTDOWN_STDIN') == '1':
        def stdin_watcher():
            for raw in sys.stdin:
                if raw.strip() == 'SHUTDOWN':
                    break
            if process.poll() is None:
                _request_graceful_shutdown(process)
        threading.Thread(target=stdin_watcher, daemon=True).start()
    ready = False
    deadline = time.monotonic() + 60
    code = None
    eof = False
    try:
        with (run_dir / 'server.log').open('w', encoding='utf-8') as log:
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
                        if credential.is_symlink() or not credential.is_file():
                            raise ValueError('bootstrap credential file must be a regular non-symlink file')
                        if not IS_WINDOWS and credential.stat().st_mode & 0o777 != 0o600:
                            raise ValueError('bootstrap credential file must have mode 0600')
                        bootstrap = json.loads(credential.read_text(encoding='utf-8'))
                        addresses = bootstrap['addresses']
                        if not addresses or any(urlsplit(url).hostname != '127.0.0.1' for url in addresses):
                            raise ValueError('backend did not bind the requested loopback address')
                        print(line.rstrip(), flush=True)
                        print('WEB_BACKEND_READY ' + json.dumps({
                            'addresses': addresses,
                            'hostInstanceId': bootstrap['hostInstanceId'],
                            'credentialFile': str(credential),
                            'runDirectory': str(run_dir),
                            'version': manifest['version'],
                            'pid': process.pid,
                        }), flush=True)
                        ready = True
                if not ready and time.monotonic() >= deadline:
                    raise ValueError(f'backend readiness timed out; see {run_dir / "server.log"}')
                if eof and process.poll() is not None:
                    code = process.wait()
                    break
    finally:
        if process.poll() is None:
            _request_graceful_shutdown(process)
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                process.terminate()
                # Shutdown is cooperative: never kill Worker/native processes by name.
                process.wait(timeout=10)
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
    serve.add_argument('--ffmpeg', type=Path, default=None)
    serve.add_argument('--ffprobe', type=Path, default=None)
    for option, variable in LIMIT_OPTIONS.items():
        serve.add_argument('--' + option, type=resource_limit, default=argparse.SUPPRESS,
                           help=f'positive limit or none; overrides {variable}, omitted keeps that environment value')
    args = parser.parse_args()
    try:
        target_rid()
    except ValueError as error:
        parser.error(str(error))
    try:
        return publish(args) if args.command == 'publish' else run(args)
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f'backend-web-host: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
