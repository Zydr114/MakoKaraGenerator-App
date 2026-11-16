#!/usr/bin/env python3
"""Finite G1 acceptance against an already published App deployment; never builds.

Run with --dist <pnpm-build-dist> --output <new-directory>. Profiles are private
temporary directories. HTTP bypasses proxies; credentials stay in memory except
for host.py's private bootstrap file, which that launcher owns and removes.
Only allowlisted evidence is written, never raw HTTP JSON or child output.
"""
import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import platform
import queue
import struct
import subprocess
import sys
import tempfile
import threading
import time
import traceback
from urllib.parse import urlencode, urlsplit
import urllib.error
import urllib.request
import wave
import zipfile

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / 'backend/tools/backend-web-host/host.py'


def default_tool(name):
    # Keep native-tool discovery identical to the host (including Windows WinGet).
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location('g1_host_resolver', HOST)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    value = module.resolve_default_tool(name)
    return str(value) if value else None


def sha(data):
    return hashlib.sha256(data).hexdigest()


def check(condition, label):
    if not condition:
        raise AssertionError(label)


def native(command, data=None):
    result = subprocess.run(command, input=data, capture_output=True, timeout=180)
    check(result.returncode == 0, 'native verification failed: ' + Path(command[0]).name)
    return result.stdout


class Session:
    def __init__(self, args, profile, limit=None):
        self.args, self.profile, self.limit = args, profile, limit
        self.host = self.client = self.token = self.base = None
        self.counter, self.last_heartbeat = 0, time.monotonic()
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self.process = None
        self.ready = None
        self.reader_thread = None
        self.credentials = []
        self.host_diagnostic = None
        self.evidence = {'limitBytes': limit, 'requests': 0}

    def __enter__(self):
        env = {**os.environ, 'MAKO_HOST_SHUTDOWN_STDIN': '1'}
        command = [sys.executable, str(HOST), 'run', '--dist', str(self.args.dist),
                   '--profile', str(self.profile), '--origin', 'http://127.0.0.1:5173',
                   '--ffmpeg', self.args.ffmpeg, '--ffprobe', self.args.ffprobe]
        if self.limit:
            command += ['--export-max-media-bytes', str(self.limit)]
        self.process = subprocess.Popen(command, env=env, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8',
            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        lines = queue.Queue()
        def reader():
            for line in self.process.stdout:
                # Never persist raw output: a future diagnostic could contain a credential.
                if line.startswith('backend-web-host: '):
                    self.host_diagnostic = ('host startup PowerShell invocation failed' if 'powershell.exe' in line
                                            else 'host startup validation failed; inspect private host diagnostic')
                lines.put(line)
            lines.put(None)
        self.reader_thread = threading.Thread(target=reader, daemon=True)
        self.reader_thread.start()
        try:
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                try:
                    line = lines.get(timeout=.2)
                except queue.Empty:
                    continue
                check(line is not None, 'host exited before readiness')
                if line.startswith('WEB_BACKEND_READY '):
                    self.ready = json.loads(line.split(' ', 1)[1])
                    break
            check(self.ready is not None, 'host readiness timeout')
            self.base = self.ready['addresses'][0]
            check(all(urlsplit(url).hostname == '127.0.0.1' for url in self.ready['addresses']), 'loopback only')
            bootstrap = json.loads(Path(self.ready['credentialFile']).read_text(encoding='utf-8'))
            self.host = self.ready['hostInstanceId']
            bootstrap_token = bootstrap['bootstrapToken']
            self.credentials.append(bootstrap_token)
            registered = self.request('POST', '/api/v1/clients', {'protocolVersion': '1'}, status=201,
                                      authorization=bootstrap_token, metadata=False)
            self.client, self.token = registered['clientId'], registered['accessToken']
            self.credentials.extend([self.token, registered['resumeToken']])
            self.evidence.update(loopback=True, proxyBypassed=True, version=self.ready['version'])
            return self
        except BaseException:
            try:
                self.stop()
            except BaseException:
                pass
            if self.host_diagnostic:
                raise AssertionError(self.host_diagnostic)
            raise

    def stop(self):
        if self.process is None:
            return
        if self.process.poll() is None:
            try:
                self.process.stdin.write('SHUTDOWN\n')
                self.process.stdin.flush()
            except (BrokenPipeError, OSError):
                # The owned host may have exited between poll and the write.
                pass
            finally:
                try:
                    self.process.stdin.close()
                except OSError:
                    pass
        try:
            code = self.process.wait(timeout=35)
        except subprocess.TimeoutExpired:
            # This handle belongs to exactly the launcher we started, never a process name.
            self.process.terminate()
            self.process.wait(timeout=10)
            raise AssertionError('host did not complete cooperative stdin shutdown')
        self.evidence['gracefulExitCode'] = code
        if self.reader_thread:
            self.reader_thread.join(timeout=2)
        self.process.stdout.close()
        check(code == 0, 'host cooperative exit must be zero')
        if self.ready:
            check(not Path(self.ready['credentialFile']).exists(), 'launcher must remove its bootstrap file')
            self.evidence['bootstrapRemoved'] = True

    def __exit__(self, *ignored):
        self.stop()

    def metadata(self):
        self.counter += 1
        return {'requestId': 'g1-' + str(self.counter), 'hostInstanceId': self.host,
                'validUntilUtc': (datetime.now(timezone.utc) + timedelta(seconds=90)).isoformat()}

    def request(self, method, path, value=None, *, raw=None, status=200, content_type=None,
                headers=None, authorization=None, metadata=True, binary=False):
        if self.client and self.token and not path.endswith('/heartbeat') and time.monotonic() - self.last_heartbeat > 20:
            self.heartbeat()
        self.evidence['requests'] += 1
        selected = dict(headers or {})
        if self.host:
            selected['X-Mako-Host-Instance-Id'] = self.host
        if self.client:
            selected['X-Mako-Client-Id'] = self.client
        if authorization or self.token:
            selected['Authorization'] = 'Bearer ' + (authorization or self.token)
        if metadata and method in ('POST', 'PUT') and raw is None:
            value = {**(value or {}), 'metadata': self.metadata()}
        if value is not None:
            raw = json.dumps(value).encode('utf-8')
            content_type = 'application/json'
        if content_type:
            selected['Content-Type'] = content_type
        req = urllib.request.Request(self.base + path, data=raw, headers=selected, method=method)
        try:
            response = self.http.open(req, timeout=120)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            payload, actual = response.read(), response.status
        check(actual == status, f'{method} {path.split("?")[0]}: expected HTTP {status}, got {actual}')
        return payload if binary else json.loads(payload)

    def heartbeat(self):
        if time.monotonic() - self.last_heartbeat > 20:
            self.request('POST', '/api/v1/clients/' + self.client + '/heartbeat')
            self.last_heartbeat = time.monotonic()

    def upload(self, name, data, content_type):
        metadata = self.metadata()
        result = self.request('POST', '/api/v1/assets?' + urlencode({'fileName': name}), raw=data,
            content_type=content_type, headers={'X-Mako-Request-Id': metadata['requestId'],
                'X-Mako-Valid-Until-Utc': metadata['validUntilUtc']}, status=201)
        check(result['sha256'] == sha(data), 'uploaded asset hash')
        return result['assetId']

    def wait(self, accepted, expected='succeeded'):
        accepted = accepted.get('job', accepted)
        deadline = time.monotonic() + self.args.timeout
        while time.monotonic() < deadline:
            self.heartbeat()
            current = self.request('GET', '/api/v1/jobs/' + accepted['jobId'])
            if current['state'] in ('succeeded', 'failed', 'cancelled', 'unknown'):
                check(current['state'] == expected, 'job terminal must be ' + expected + '; actual ' + current['state'])
                check(not current['canCancel'], 'terminal job cannot cancel')
                return current
            time.sleep(.5)
        raise AssertionError('job timeout')

    def create_project(self):
        samples = [int(12000 * math.sin(2 * math.pi * 440 * index / 8000)) for index in range(16000)]
        data = io.BytesIO()
        with wave.open(data, 'wb') as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(8000)
            audio.writeframes(struct.pack('<' + 'h' * len(samples), *samples))
        audio_bytes = data.getvalue()
        audio_id = self.upload('g1.wav', audio_bytes, 'audio/wav')
        lyrics_id = self.upload('g1.lrc', b'[00:00.00]G1 CPU GPU original\n', 'text/plain')
        project = self.request('POST', '/api/v1/project-sessions', status=201, value={
            'name': 'G1 isolated acceptance', 'audioAssetId': audio_id, 'lyricsAssetId': lyrics_id,
            'composition': {'canvasWidth': 320, 'canvasHeight': 180, 'frameRateNumerator': 24,
                'frameRateDenominator': 1, 'outputDurationUs': 2000000}})
        path = '/api/v1/project-sessions/' + project['projectSessionId']
        revision = project['projectRevision']
        check(int(project['audioDurationUs']) == 2000000, 'real native audio preparation duration')
        waveform = self.request('GET', path + '/waveform')
        check(waveform['available'] and waveform['sampleRate'] == 8000 and int(waveform['sampleCount']) == 16000
              and int(waveform['durationUs']) == 2000000 and waveform['sourceSha256'] == sha(audio_bytes), 'waveform source and duration')
        values = [(min(samples[index:index + waveform['baseBucketSamples']]) / 32768,
                   max(samples[index:index + waveform['baseBucketSamples']]) / 32768)
                  for index in range(0, len(samples), waveform['baseBucketSamples'])]
        for level in waveform['levels']:
            check(int(level['bucketCount']) == len(values), 'waveform complete pyramid dimensions')
            peaks = self.request('GET', path + '/waveform/peaks?' + urlencode({'waveformId': waveform['waveformId'],
                'level': level['level'], 'start': 0, 'count': len(values)}), binary=True)
            check(peaks == b''.join(struct.pack('<ff', low, high) for low, high in values), 'waveform independent PCM min/max float bits')
            values = [(min(pair[0] for pair in values[index:index + 2]), max(pair[1] for pair in values[index:index + 2]))
                      for index in range(0, len(values), 2)]
        self.evidence['audio'] = {'sourceSha256': sha(audio_bytes), 'sampleCount': 16000, 'durationUs': 2000000,
                                  'waveformLevels': len(waveform['levels']), 'fullPyramidExact': True}
        def send(command):
            nonlocal revision
            result = self.request('POST', path + '/commands', value={
                **self.metadata(), 'expectedRevision': revision, 'command': command}, metadata=False)
            revision = result['projectRevision']
        line = self.request('GET', path + '/lyrics/window?branchId=primary&expectedRevision=' + revision)['lines'][0]
        send({'type': 'lyrics.setTimeRange', 'target': {'branchId': 'primary', 'lineId': line['id']},
              'startUs': '0', 'endUs': '2000000'})
        send({'type': 'lyrics.setTokenTimes', 'changes': [
            {'target': {'branchId': 'primary', 'lineId': line['id'], 'tokenId': token['id']},
             'startUs': str(2000000 * index // len(line['tokens'])),
             'endUs': str(2000000 * (index + 1) // len(line['tokens']))} for index, token in enumerate(line['tokens'])]})
        tree = self.request('GET', path + '/orchestration?expectedRevision=' + revision)
        program = next(item for item in tree['programs'] if item['branchId'] == 'primary')
        send({'type': 'orchestration.createClip', 'branchId': 'primary', 'clipId': 'clip/g1', 'name': 'G1 clip',
              'lineIds': [line['id']], 'containerId': program['defaultContainerId']})
        return path, revision

    def frame(self, source, backend, name, time_us='500000', author=False):
        value = {'source': source, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque',
                 'purpose': 'preview', 'renderBackend': backend}
        if author:
            value.update(previewKind='authorDraft', committable=False)
        preview = self.request('POST', '/api/v1/preview-sessions', value=value, status=201)
        preview = preview.get('session', preview)
        path = '/api/v1/preview-sessions/' + preview['previewId']
        identity = {'expectedSourceRevision': source['sourceRevision'], 'previewGeneration': preview['previewGeneration']}
        self.request('POST', path + '/frame-requests', status=202, value={**identity,
            'requestSeq': '1', 'timeUs': time_us, 'intent': 'playback'})
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            self.heartbeat()
            state = self.request('GET', path)['session']
            check(state['state'] != 'failed', name + ' preview worker failed')
            if state['state'] == 'ready' and state.get('latestFrame', {}).get('requestSeq') == '1':
                break
            time.sleep(.5)
        else:
            raise AssertionError(name + ' frame timeout')
        frame = state['latestFrame']
        check(state['renderBackend'] == frame['renderBackend'] == backend, name + ' fixed actual backend')
        check(frame['sourceRevision'] == source['sourceRevision'] and frame['previewGeneration'] == preview['previewGeneration'], name + ' exact source/generation')
        png = self.request('GET', frame['content']['contentUrl'], binary=True)
        check(len(png) == int(frame['content']['sizeBytes']) and sha(png) == frame['content']['sha256'], name + ' PNG bytes/hash')
        check(png[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack('>II', png[16:24]) == (320, 180), name + ' PNG dimensions')
        rgba = native([self.args.ffmpeg, '-v', 'error', '-i', 'pipe:0', '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'], png)
        check(len(rgba) == 320 * 180 * 4 and set(rgba[3::4]) == {255}, name + ' complete opaque RGBA decode')
        check(len(set(rgba[index:index + 3] for index in range(0, len(rgba), 4))) > 8, name + ' nonblank rendered content')
        diagnostics = self.request('GET', path + '/diagnostics?limit=128')
        check(not diagnostics['hasMore'] and not any(item['severity'] == 'error' for item in diagnostics['diagnostics']), name + ' no runtime diagnostic errors')
        check(diagnostics['sourceRevision'] == source['sourceRevision'] and diagnostics['previewGeneration'] == preview['previewGeneration'], name + ' diagnostics bind exact source/generation')
        (self.args.output / (name + '.png')).write_bytes(png)
        self.request('POST', path + '/close', value={'expectedPreviewGeneration': preview['previewGeneration'],
                     'expectedSourceRevision': source['sourceRevision']})
        return {'backend': backend, 'pngBytes': len(png), 'pngSha256': sha(png), 'rgbaSha256': sha(rgba), 'diagnosticErrors': 0}

    def export(self, path, revision, backend, expected='succeeded'):
        accepted = self.request('POST', path + '/export-jobs', status=202, value={
            'expectedRevision': revision, 'format': 'opaqueMp4', 'includeAudio': False, 'renderBackend': backend})
        check(accepted['exportInput']['renderBackend'] == accepted['input']['renderBackend'] == backend, 'export pins requested backend in source and execution input')
        terminal = self.wait(accepted, expected)
        if expected == 'failed':
            # Only the actual size guard failure is acceptable, not a general FFmpeg/Worker failure.
            check(terminal['failureCode'] == 'WorkerExportArtifactBudgetExceeded', 'small media budget must fail with exact size guard code')
            check(not terminal.get('exportResult'), 'over-limit job must not publish a result')
            problem = self.request('GET', '/api/v1/jobs/' + terminal['jobId'] + '/export-content', status=404)
            check(problem['code'] == 'exportArtifactNotFound', 'failed export content unavailable')
            artifact_root = self.profile / 'state/export-artifacts'
            deadline = time.monotonic() + 10
            while time.monotonic() < deadline and any((artifact_root / '.staging').iterdir()):
                time.sleep(.2)
            check(not any((artifact_root / '.staging').iterdir()), 'over-limit staging directories fully removed')
            failure_ref = terminal.get('failureReportRef')
            files = [item for item in artifact_root.rglob('*') if item.is_file()]
            # RunExportAsync retains reports only when an actual runtime/source
            # diagnostic exists; pure byte-budget failures normally have none.
            if failure_ref is not None:
                failed_report = self.request('GET', '/api/v1/jobs/' + terminal['jobId'] + '/export-result', binary=True)
                check(len(failed_report) == int(failure_ref['sizeBytes']) and sha(failed_report) == failure_ref['sha256'], 'immutable failure report hash/bytes')
                check(len(files) == 1 and files[0].name == 'report.json' and files[0].read_bytes() == failed_report,
                      'only exact registered failure report remains, no media')
            else:
                check(not files, 'plain size-budget failure removes all candidate/temporary/published artifacts')
            return {'backend': backend, 'state': 'failed', 'failureCode': 'WorkerExportArtifactBudgetExceeded',
                    'leftoverMediaFiles': 0, 'registeredFailureReport': failure_ref is not None}
        report_bytes = self.request('GET', '/api/v1/jobs/' + terminal['jobId'] + '/export-result', binary=True)
        report = json.loads(report_bytes)
        report_ref = terminal['exportResult']['report']
        check(len(report_bytes) == int(report_ref['sizeBytes']) and sha(report_bytes) == report_ref['sha256'], 'export report bytes/hash')
        check(report['renderBackend'] == terminal['exportInput']['renderBackend'] == backend, 'export execution/report actual backend')
        check(report['isVerified'] and report['strictlyVerified'] and report['expectedFrames'] == report['decodedFrames'] == report['framesWritten'] == 48
              and report['failedFrames'] == report['droppedFrames'] == 0, 'all 48 planned export frames strictly verified')
        check(report.get('fallbackFrameCount', 0) == 0, 'export has no fallback-rendered frames')
        media = self.request('GET', '/api/v1/jobs/' + terminal['jobId'] + '/export-content', binary=True)
        artifact = terminal['exportResult']['artifact']
        check(len(media) == int(artifact['sizeBytes']) == report['mediaSize'] and sha(media) == artifact['sha256'] == report['sha256'], 'complete media size/hash')
        target = self.args.output / ('export-' + backend + '.mp4')
        target.write_bytes(media)
        ffprobe = json.loads(native([self.args.ffprobe, '-v', 'error', '-count_frames', '-show_streams', '-show_format', '-of', 'json', str(target)]))
        video = next(stream for stream in ffprobe['streams'] if stream['codec_type'] == 'video')
        check(int(video['nb_read_frames']) == 48 and video['width'] == 320 and video['height'] == 180
              and video['codec_name'] == 'h264', 'ffprobe counts all 48 h264 frames and dimensions')
        rgba = native([self.args.ffmpeg, '-v', 'error', '-xerror', '-i', str(target), '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'])
        check(len(rgba) == 48 * 320 * 180 * 4, 'independent FFmpeg complete decode byte count')
        return {'backend': backend, 'frames': 48, 'mediaBytes': len(media), 'mediaSha256': sha(media),
                'decodedBytes': len(rgba), 'decodedSha256': sha(rgba), 'strictlyVerified': True, 'ffprobeFrames': int(video['nb_read_frames'])}

    def sdk2(self):
        directory = ROOT / 'backend/samples/ScriptEffectsV2/WordEmphasis'
        raw_manifest = (directory / 'manifest.json').read_bytes()
        manifest = json.loads(raw_manifest)
        check(manifest['packageVersion'] == 3 and manifest['sdkVersion'].split('.')[0] == '2', 'real built-in SDK2 manifest')
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as package:
            package.writestr('manifest.json', raw_manifest)
            for item in manifest['files']:
                raw = (directory / item['path']).read_bytes()
                check(sha(raw) == item['sha256'], 'built-in SDK2 source manifest hash')
                package.writestr(item['path'], raw)
        package_id = self.upload('WordEmphasis.mkfx', archive.getvalue(), 'application/octet-stream')
        inspected = self.request('POST', '/api/v1/effect-packages/inspect', value={'packageAssetId': package_id}, metadata=False)
        check(inspected['structureValid'], 'SDK2 package inspection')
        draft = self.request('POST', '/api/v1/drafts', status=201, value={
            'packageAssetId': package_id, 'exportId': 'word-emphasis', 'name': 'G1 SDK2 WordEmphasis'})
        check(draft['canCompile'] and draft['sdkVersion'].split('.')[0] == '2', 'SDK2 draft compiler enabled')
        compiled = self.request('POST', '/api/v1/drafts/' + draft['draftId'] + '/compilation-jobs', value={
            'expectedDraftRevision': draft['draftRevision'], 'sourceRevision': draft['currentSourceRevision'], 'executionMode': 'sync'})
        check(compiled['succeeded'] and not compiled['stale'] and not any(item['isError'] for item in compiled['diagnostics']), 'SDK2 real C# compilation succeeds')
        options = draft['sampleOptions']
        sample = next(item for item in options['samples'] if item['sampleId'] == options['defaultSampleId'])
        selection = {'sampleId': sample['sampleId'], 'targetKey': sample['targets'][0]['key']}
        if options['materialSlots']:
            selection['materialSlot'] = options['materialSlots'][0]
        frame = self.frame({'kind': 'authorDraft', 'sourceId': draft['draftId'], 'sourceRevision': draft['draftRevision'],
            'authorSourceRevision': draft['configuration']['sourceRevision'], 'templateSample': selection}, 'gpu', 'sdk2-word-emphasis', '3000000', author=True)
        accepted = self.request('POST', '/api/v1/drafts/' + draft['draftId'] + '/validation-jobs', status=202,
            value={'expectedDraftRevision': draft['draftRevision'], **selection, 'renderBackend': 'gpu'})
        terminal = self.wait(accepted)
        report = self.request('GET', '/api/v1/jobs/' + terminal['jobId'] + '/validation-result')
        check(report['succeeded'] and report['renderBackend'] == 'gpu' and report['opaqueFrames'] == report['transparentFrames'] == 360,
              'SDK2 GPU default sample executes all 720 formal validation frames')
        return {'sample': 'ScriptEffectsV2/WordEmphasis', 'manifestSha256': sha(raw_manifest), 'archiveSha256': sha(archive.getvalue()),
                'compilationSucceeded': True, 'gpuFrame': frame, 'opaqueFrames': 360, 'transparentFrames': 360}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--ffmpeg', default=default_tool('ffmpeg'))
    parser.add_argument('--ffprobe', default=default_tool('ffprobe'))
    parser.add_argument('--timeout', type=float, default=600)
    args = parser.parse_args()
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    check(args.ffmpeg and args.ffprobe, 'ffmpeg/ffprobe required')
    args.ffmpeg, args.ffprobe = str(Path(args.ffmpeg).resolve()), str(Path(args.ffprobe).resolve())
    args.output.mkdir(parents=True, exist_ok=False)
    deployment_bytes = (args.dist / 'deployment.json').read_bytes()
    deployment = json.loads(deployment_bytes)
    summary = {'status': 'running', 'stage': 'host-startup', 'platform': {'system': platform.system(), 'machine': platform.machine(), 'python': platform.python_version()},
               'deployment': {key: deployment.get(key) for key in ('rid', 'version', 'sourceCommit', 'sourceDirty', 'selfContained')},
               'deploymentManifestSha256': sha(deployment_bytes), 'probeSha256': sha(Path(__file__).read_bytes()),
               'hostLauncherSha256': sha(HOST.read_bytes()),
               'tools': {name: native([getattr(args, name), '-version']).decode('utf-8', errors='replace').splitlines()[0] for name in ('ffmpeg', 'ffprobe')}}
    sessions = []
    try:
        with tempfile.TemporaryDirectory(prefix='mako-g1-private-') as private:
            profile_root = Path(private)
            with Session(args, profile_root / 'normal') as normal:
                sessions.append(normal)
                summary['stage'] = 'render-backends'
                backends = normal.request('GET', '/api/v1/render-backends')
                gpu = next(item for item in backends['backends'] if item['renderBackend'] == 'gpu')
                check(gpu['available'] and gpu['api'] == 'vulkan' and bool(gpu.get('device')), 'real available Vulkan device')
                check(not any(name in json.dumps(gpu['device']).lower() for name in ('llvmpipe', 'lavapipe', 'swiftshader', 'software rasterizer')),
                      'GPU availability must name hardware, not a software Vulkan implementation')
                summary['gpu'] = {key: gpu.get(key) for key in ('renderBackend', 'available', 'api', 'device')}
                summary['stage'] = 'audio-waveform-project'
                path, revision = normal.create_project()
                summary['staticPng'] = {}
                for backend in ('cpu', 'gpu'):
                    summary['stage'] = 'static-png-' + backend
                    summary['staticPng'][backend] = normal.frame({'kind': 'project', 'sourceId': path.rsplit('/', 1)[1],
                        'sourceRevision': revision}, backend, 'static-' + backend)
                summary['exports'] = []
                for backend in ('cpu', 'gpu'):
                    summary['stage'] = 'export-' + backend
                    summary['exports'].append(normal.export(path, revision, backend))
                summary['stage'] = 'sdk2-compilation-gpu-preview-validation'
                summary['sdk2'] = normal.sdk2()
            with Session(args, profile_root / 'small-budget', limit=256) as limited:
                sessions.append(limited)
                summary['stage'] = 'small-budget-project'
                path, revision = limited.create_project()
                summary['stage'] = 'real-export-over-limit'
                summary['overLimit'] = limited.export(path, revision, 'cpu', expected='failed')
        summary['profilesRemoved'] = True
        summary['status'] = 'passed'
        summary['stage'] = 'complete'
    except BaseException as error:
        # Persist fixed assertion labels/types only. Never copy server responses or URLs with credentials.
        summary['status'] = 'failed'
        message = str(error) if isinstance(error, AssertionError) else 'probe exception; inspect stage and exception type'
        summary['failure'] = {'type': type(error).__name__, 'message': message}
        summary['failure']['trace'] = [{'file': Path(frame.filename).name, 'line': frame.lineno, 'function': frame.name}
                                       for frame in traceback.extract_tb(error.__traceback__)]
    summary['sessions'] = [session.evidence for session in sessions]
    raw = json.dumps(summary, ensure_ascii=False, indent=2)
    for session in sessions:
        for credential in session.credentials:
            check(credential not in raw, 'summary credential leak')
    (args.output / 'summary.json').write_text(raw + '\n', encoding='utf-8')
    print(json.dumps({'status': summary['status'], 'output': str(args.output)}, ensure_ascii=False))
    return 0 if summary['status'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
