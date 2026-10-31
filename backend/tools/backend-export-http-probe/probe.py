#!/usr/bin/env python3
"""Bounded authenticated HTTP -> actual owned Worker -> original exporter probe.
Only authentication/redaction helpers are imported. No product algorithm is copied.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import secrets
import signal
import struct
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.request
import wave
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('backend_http_helper', ROOT / 'tools/backend-http-probe/probe.py')
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)


class ExportProbe(HELPER.Probe):
    def __init__(self, output, server, worker, oracle):
        super().__init__(output, server)
        self.worker, self.oracle = Path(worker).resolve(), oracle
        self.process = None
        self.decoders = []
        self.identities, self.credentials, self.results, self.comparisons, self.server_runs = {}, [], [], [], []
        self.launcher = output / 'owned-worker-launcher'
        self.saved_handles = {}

    def register(self, *args, **kwargs):
        value = super().register(*args, **kwargs)
        self.credentials.extend([value['accessToken'], value['resumeToken']])
        return value

    def mutate(self, name, method, path, value, *, metadata=None, **kwargs):
        return self.request(name, method, path, value={**value, 'metadata': metadata or self.metadata(name)}, **kwargs)

    def heartbeat(self, name):
        return self.request(name, 'POST', f'/api/v1/clients/{self.client}/heartbeat', value={})

    @staticmethod
    def identity(pid):
        try:
            parts = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
            return {'pid': pid, 'parent': int(parts[1]), 'group': int(parts[2]), 'startTicks': parts[19], 'state': parts[0]}
        except (FileNotFoundError, ProcessLookupError, IndexError):
            return None

    def capture(self):
        if self.process is None:
            return
        entries = []
        for entry in Path('/proc').iterdir():
            if entry.name.isdigit():
                value = self.identity(int(entry.name))
                if value:
                    entries.append(value)
        groups = {v['group'] for v in self.identities.values()}
        for value in entries:
            if value['parent'] == self.process.pid:
                try:
                    command = Path(f"/proc/{value['pid']}/cmdline").read_bytes()
                except (FileNotFoundError, ProcessLookupError):
                    continue
                if b'MakoKaraGenerator.Backend.Worker' in command:
                    # Process.Start precedes Program's before-hello setpgid.
                    # Do not claim its inherited shared group during that startup window.
                    if value['group'] != value['pid']:
                        continue
                    self.check(value['group'] == value['pid'], 'ownership: actual Worker is its private group leader')
                    self.identities[value['pid']] = value
                    groups.add(value['group'])
        for value in entries:
            if value['group'] in groups:
                prior = self.identities.get(value['pid'])
                self.check(prior is None or prior['startTicks'] == value['startTicks'], 'ownership: observed PID was not reused')
                self.identities[value['pid']] = value

    def live(self):
        return [pid for pid, value in self.identities.items()
                if (actual := self.identity(pid)) and actual['startTicks'] == value['startTicks']]

    def signal_owned(self, pid, action):
        actual, known = self.identity(pid), self.identities[pid]
        self.check(actual is not None and actual['startTicks'] == known['startTicks'] and actual['group'] == known['group'],
                   'signal: exact owned pid/startTicks/private group verified')
        os.kill(pid, action)

    def wait_job(self, name, job, expected='succeeded'):
        deadline, polls = time.monotonic() + 25, 0
        while time.monotonic() < deadline:
            self.capture()
            polls += 1
            value = self.request(f'{name}-poll-{polls}', 'GET', '/api/v1/jobs/' + job['jobId'])
            if value['state'] in ('succeeded', 'failed', 'cancelled', 'unknown'):
                self.check(value['state'] == expected, name + ': drained terminal ' + expected)
                self.check(not value['canCancel'], name + ': terminal cannot be cancelled')
                self.results.append(value)
                return value
            time.sleep(.04)
        raise AssertionError(name + ': no bounded terminal')

    def start_export(self, name, source, fmt='opaqueMp4', audio=True, metadata=None, **kwargs):
        self.heartbeat(name + '-heartbeat')
        return self.mutate(name, 'POST', f"/api/v1/project-sessions/{source['projectSessionId']}/export-jobs",
                           {'expectedRevision': source['projectRevision'], 'format': fmt, 'includeAudio': audio},
                           metadata=metadata, status=202, **kwargs)

    def active(self, name, job, encoder=False, excluded=()):
        deadline, polls = time.monotonic() + 8, 0
        while time.monotonic() < deadline:
            polls += 1
            self.capture()
            value = self.request(f'{name}-active-{polls}', 'GET', '/api/v1/jobs/' + job['jobId'])
            for pid in self.live():
                if pid in excluded:
                    continue
                ident = self.identities[pid]
                try:
                    command = Path(f'/proc/{pid}/cmdline').read_bytes()
                except (FileNotFoundError, ProcessLookupError):
                    continue
                if encoder and b'rawvideo' in command:
                    return value, pid
                if not encoder and pid == ident['group']:
                    return value, pid
            self.check(value['state'] not in ('succeeded', 'failed', 'cancelled'), name + ': active before controlled action')
            time.sleep(.002)
        raise AssertionError(name + ': no actual owned process')

    def cancel(self, name, job):
        return self.request(name, 'POST', '/api/v1/jobs/' + job['jobId'] + '/cancel', value=self.metadata(name + '-cancel-command'))

    def terminal_events(self, name, cursor, job):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + '/api/v1/events', headers={
            'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host,
            'Authorization': 'Bearer ' + self.tokens[self.client], 'Last-Event-ID': cursor})
        events, current = [], []
        with self.http.open(request, timeout=10) as response:
            self.check(response.status == 200, name + ': authenticated SSE replay')
            while len(events) < 256:
                line = response.readline().decode().rstrip('\r\n')
                if line:
                    current.append(line); continue
                if not current:
                    continue
                data = [value[6:] for value in current if value.startswith('data: ')]
                current = []
                if not data:
                    continue
                event = json.loads('\n'.join(data)); events.append(event)
                if event.get('type') == 'job.terminal' and (event.get('job') or {}).get('jobId') == job['jobId']:
                    self.check(event['job']['state'] == job['state'], name + ': SSE and authoritative terminal match')
                    progress = [v['job']['progress'] for v in events if (v.get('job') or {}).get('jobId') == job['jobId'] and (v['job'].get('progress') or {}).get('stage') == 'rendering']
                    counts = [value['completedFrames'] for value in progress]
                    self.check(counts == sorted(counts) and all(0 <= value <= 90 for value in counts)
                               and all(value['totalFrames'] == 90 for value in progress),
                               name + ': observed rendering progress is bounded and monotonic (intermediate samples may coalesce)')
                    final_progress = event['job']['progress']
                    self.check(final_progress['completedFrames'] == final_progress['totalFrames'] == 90,
                               name + ': authoritative terminal preserves actual completed frame count')
                    HELPER.save_json(self.output / (name + '-sse.json'), events)
                    return
        raise AssertionError(name + ': bounded replay missing terminal')

    def native(self, name, args):
        result = subprocess.run(self.oracle + args, capture_output=True, timeout=45)
        (self.output / (name + '.stdout.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': independent original Runtime oracle exit0')

    def create(self, name, duration=3_000_000, width=320, height=180, numerator=30, audio=True):
        self.heartbeat(name + '-create-heartbeat')
        value = {'name': name, 'audioAssetId': self.audio['assetId'], 'lyricsAssetId': self.lyrics['assetId'], 'importerId': 'lrc.enhanced', 'composition': {
            'canvasWidth': width, 'canvasHeight': height, 'frameRateNumerator': numerator,
            'frameRateDenominator': 1, 'outputDurationUs': duration}}
        return self.mutate(name, 'POST', '/api/v1/project-sessions', value, status=201)

    def open(self, name, handle):
        job = self.mutate(name, 'POST', '/api/v1/project-sessions/open-jobs',
                          {'projectDirectoryHandleId': handle['handleId']}, status=202)
        return self.wait_job(name, job)['result']

    def fixture(self, name, numerator=30):
        source = self.create(name)
        handle = self.mutate(name + '-handle', 'POST', '/api/v1/file-handles',
                             {'rootGrantId': 'export-probe-root', 'relativePath': name}, status=201)
        saved = self.mutate(name + '-save', 'POST', f"/api/v1/project-sessions/{source['projectSessionId']}/save-jobs",
                            {'expectedRevision': source['projectRevision'], 'targetHandleId': handle['handleId']}, status=202)
        self.wait_job(name + '-saved', saved)
        close = self.mutate(name + '-close-preparation', 'POST', f"/api/v1/project-sessions/{source['projectSessionId']}/close",
                            {'expectedRevision': source['projectRevision'], 'policy': 'requireClean', 'activeJobs': 'reject'}, status=202)
        self.wait_job(name + '-closed-preparation', close)
        directory = self.output / 'projects' / name
        self.native(name + '-prepare', ['prepare', str(directory), str(numerator)])
        self.heartbeat(name + '-open-heartbeat')
        opened = self.open(name + '-open-owned', handle)
        pinned = self.output / 'state/sessions' / opened['projectSessionId'] / 'loaded-input'
        self.native(name + '-oracle', ['render', str(pinned), str(self.output / (name + '-oracle'))])
        self.saved_handles[name] = handle
        return opened

    def read_result(self, name, job, audio=True):
        path = '/api/v1/jobs/' + job['jobId']
        ref = job['exportResult']
        headers, raw = self.binary(name + '-report', path + '/export-result')
        self.check(len(raw) == ref['report']['sizeBytes'] and hashlib.sha256(raw).hexdigest() == ref['report']['sha256'], name + ': canonical sidecar exact hash and size')
        report = json.loads(raw)
        self.check(report['strictlyVerified'] and report['decodedFrames'] == report['expectedFrames'] == report['framesWritten'], name + ': actual strict decoded frame plan')
        self.check(report['failedFrames'] == report['droppedFrames'] == 0 and report['diagnostics'] == [], name + ': real static success has empty diagnostics')
        self.check(report['audioIncluded'] == audio, name + ': actual requested audio mode')
        page = self.request(name + '-diagnostics', 'GET', path + '/export-diagnostics?limit=1')
        self.check(page['diagnostics'] == [] and page.get('nextCursor') is None and page['reportSha256'] == ref['report']['sha256'], name + ': bounded empty diagnostics binds report')
        headers, media = self.binary(name + '-media', path + '/export-content')
        self.check(len(media) == ref['artifact']['sizeBytes'] == report['mediaSize'] and hashlib.sha256(media).hexdigest() == ref['artifact']['sha256'] == report['sha256'], name + ': actual downloadable media exact hash and size')
        self.check(headers['Content-Type'].split(';')[0] == ref['artifact']['contentType'], name + ': media type')
        file = self.output / (name + ('.mov' if report['hasAlpha'] else '.mp4'))
        file.write_bytes(media)
        return report, raw, media, file

    def decode(self, name, file, report, oracle_name):
        observation = subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-count_frames', '-show_streams', '-of', 'json', str(file)], capture_output=True, check=True, timeout=15)
        observed = json.loads(observation.stdout)
        HELPER.save_json(self.output / (name + '-independent-probe.json'), observed)
        video = next(s for s in observed['streams'] if s['codec_type'] == 'video')
        self.check(int(video['nb_read_frames']) == report['expectedFrames'], name + ': independent actual decoded frame count')
        self.check(video['r_frame_rate'] == f"{report['frameRateNumerator']}/{report['frameRateDenominator']}", name + ': exact rational rate')
        process = subprocess.Popen(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(file), '-map', '0:v:0', '-f', 'rawvideo', '-pix_fmt', 'bgra', 'pipe:1'], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.decoders.append(process)
        size = report['width'] * report['height'] * 4
        prefix = self.output / (oracle_name + '-oracle')
        expected_info = json.loads(Path(str(prefix) + '.json').read_text())
        self.check(expected_info['snapshotSha256'] == self.current_success['input']['sourceSha256'], name + ': independent saved snapshot exact accepted source SHA')
        self.check(expected_info['totalFrames'] == report['expectedFrames'] and expected_info['rubyChangedPixels'] > 0 and expected_info['sceneChangedPixels'] > 0, name + ': real ruby and scene contribute pixels in accepted fixture')
        alpha = report['hasAlpha']
        frames = 0
        absolute_error, channels, glyph, glyph_match, transparent, semi = 0, 0, 0, 0, 0, 0
        with Path(str(prefix) + ('-transparent.bgra' if alpha else '-opaque.bgra')).open('rb') as reference:
            while True:
                data = process.stdout.read(size)
                if not data:
                    break
                self.check(len(data) == size, name + ': complete decoded BGRA frame')
                expected = np.frombuffer(reference.read(size), dtype=np.uint8).reshape(-1, 4).astype(np.int32)
                actual = np.frombuffer(data, dtype=np.uint8).reshape(-1, 4).astype(np.int32)
                if alpha:
                    opacity = expected[:, 3]
                    mask = opacity > 40
                    glyph += int(np.count_nonzero(mask))
                    glyph_match += int(np.count_nonzero(np.abs(actual[mask, 3] - opacity[mask]) <= 8))
                    transparent += int(np.count_nonzero(actual[:, 3] == 0))
                    semi += int(np.count_nonzero((actual[:, 3] > 20) & (actual[:, 3] < 240)))
                    expected[:, :3] = np.where(opacity[:, None] > 0, np.minimum(255, (expected[:, :3] * 255 + opacity[:, None] // 2) // np.maximum(1, opacity[:, None])), 0)
                    error = np.abs(actual[mask, :3] - expected[mask, :3])
                else:
                    error = np.abs(actual[:, :3] - expected[:, :3])
                    mask = np.max(expected[:, :3], axis=1) > 150
                    glyph += int(np.count_nonzero(mask))
                    glyph_match += int(np.count_nonzero(np.max(actual[mask, :3], axis=1) > 130))
                absolute_error += int(error.sum())
                channels += error.size
                frames += 1
            self.check(reference.read(1) == b'', name + ': every original Runtime frame was compared')
        stderr = process.stderr.read()
        self.check(process.wait(timeout=10) == 0 and stderr == b'', name + ': full video decode exit0')
        self.check(frames == report['expectedFrames'] and glyph > 100 and glyph_match >= .90 * glyph, name + ': real lyric/ruby/scene glyph coverage matches independent Runtime')
        mae = absolute_error / max(1, channels)
        self.check(mae < (15 if alpha else 8), name + ': bounded lossy codec RGB error against original Runtime')
        if alpha:
            self.check(transparent > 1000 and semi > 100, name + ': decoded transparent and semitransparent pixels preserved')
        pcm = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(file), '-map', '0:a:0', '-ac', '1', '-ar', '44100', '-f', 's16le', 'pipe:1'], capture_output=True, check=True, timeout=15)
        samples = np.frombuffer(pcm.stdout, dtype='<i2').astype(np.float64)
        energy = float(np.sqrt(np.mean(samples * samples)))
        planned = int(report['derivedVideoDurationUs']) * 44100 // 1_000_000
        self.check(abs(len(samples) - planned) <= (1 if alpha else 1024) and energy > 1000, name + ': complete non-silent decoded real audio samples')
        self.comparisons.append({'name': name, 'decodedFrames': frames, 'rgbMeanAbsoluteError': mae, 'glyphMatchingPixels': glyph_match, 'glyphPixels': glyph, 'transparentPixels': transparent, 'semiTransparentPixels': semi, 'decodedMonoAudioSamples': len(samples), 'audioRmsPcm16': energy, 'oracle': expected_info})

    def downloads(self, job, raw, media):
        path = '/api/v1/jobs/' + job['jobId']
        headers, _ = self.binary('download-etag', path + '/export-content', expected=media)
        etag = headers['ETag']
        self.binary('download-first-range', path + '/export-content', headers={'Range': 'bytes=0-31'}, status=206, expected=media[:32])
        self.binary('download-suffix-range', path + '/export-content', headers={'Range': 'bytes=-16'}, status=206, expected=media[-16:])
        self.binary('download-not-modified', path + '/export-content', headers={'If-None-Match': etag}, status=304, expected=b'')
        self.binary('download-unsatisfiable-range', path + '/export-content', headers={'Range': 'bytes=999999999-'}, status=416, expected=b'')
        self.request_count += 1
        request = urllib.request.Request(self.base_url + path + '/export-content', method='HEAD', headers={'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host, 'Authorization': 'Bearer ' + self.tokens[self.client]})
        with self.http.open(request, timeout=10) as response:
            self.check(response.status == 200 and response.read() == b'' and int(response.headers['Content-Length']) == len(media) and response.headers['ETag'] == etag, 'HEAD: authorized exact media length/ETag without bytes')
        outsider = self.register('live-outsider', select=False)
        for suffix in ('', '/export-result', '/export-content', '/export-diagnostics'):
            self.request('cross-client' + suffix.replace('/', '-'), 'GET', path + suffix, client=outsider['clientId'], status=404,
                         code='jobNotFound' if suffix == '' else 'exportArtifactNotFound')
        self.request('bad-auth-media', 'GET', path + '/export-content', headers={'Authorization': 'Bearer ' + '0' * 64}, status=401, code='unauthorizedClient')
        self.request('diagnostic-small-budget', 'GET', path + '/export-diagnostics?limit=0', status=400, code='diagnosticLimitExceeded')
        self.request('diagnostic-large-budget', 'GET', path + '/export-diagnostics?limit=129', status=400, code='diagnosticLimitExceeded')
        self.request('diagnostic-foreign-cursor', 'GET', path + '/export-diagnostics?cursor=foreign&limit=1', status=409, code='diagnosticCursorExpired')
        bundle = self.output / 'state/export-artifacts' / job['exportResult']['artifact']['artifactId']
        sidecar = bundle / 'report.json'
        try:
            sidecar.write_bytes(bytes([raw[0] ^ 1]) + raw[1:])
            self.request('corrupt-owned-sidecar', 'GET', path + '/export-result', status=500, code='taskExecutionFailed')
        finally:
            sidecar.write_bytes(raw)
        media_path = bundle / ('media.mov' if media[4:8] == b'wide' else 'media.mp4')
        media_path = next(bundle.glob('media.*'))
        try:
            with media_path.open('r+b') as stream:
                first = stream.read(1); stream.seek(0); stream.write(bytes([first[0] ^ 1]))
            self.request('corrupt-owned-media', 'GET', path + '/export-content', status=500, code='taskExecutionFailed')
        finally:
            with media_path.open('r+b') as stream:
                stream.write(first)
        self.binary('restored-owned-media', path + '/export-content', expected=media)
        self.check(HELPER.datetime.fromisoformat(job['expiresAtUtc'].replace('Z', '+00:00')) > HELPER.utc_now(), 'TTL: real returned result expiration is future (no one-hour passage claimed)')

    def controls(self, source):
        running = self.start_export('cancel-running', source, audio=False)
        _, pid = self.active('cancel-running', running)
        self.signal_owned(pid, signal.SIGSTOP)
        queued = [self.start_export('cancel-queued-' + str(i), source, audio=False) for i in range(4)]
        self.check(all(j['state'] == 'queued' for j in queued), 'queue: exactly one running and four per-client queued')
        self.heartbeat('queue-budget-heartbeat')
        self.mutate('queue-over-limit', 'POST', f"/api/v1/project-sessions/{source['projectSessionId']}/export-jobs", {'expectedRevision': source['projectRevision'], 'includeAudio': False}, status=429, code='requestQuotaExceeded')
        for i, job in enumerate(queued):
            self.cancel('cancel-queued-' + str(i), job)
            terminal = self.wait_job('cancel-queued-' + str(i), job, 'cancelled')
            self.check(terminal.get('exportResult') is None and terminal.get('progress') is None, 'queued cancellation: no rendered or published result')
        self.cancel('cancel-active', running)
        terminal = self.wait_job('cancel-active', running, 'cancelled')
        self.check(pid not in self.live() and terminal.get('exportResult') is None, 'active cancellation: actual stopped Worker drained before terminal')
        job = self.start_export('deadline', source, audio=False)
        _, pid = self.active('deadline', job)
        self.signal_owned(pid, signal.SIGSTOP)
        terminal = self.wait_job('deadline', job, 'failed')
        self.check(terminal['failureCode'] == 'WorkerExportTimedOut' and not terminal['cancellationRequested'] and pid not in self.live(), 'deadline: stable timeout differs from explicit cancellation and drains Worker')
        for name, encoder in [('worker-failure', False), ('ffmpeg-failure', True)]:
            job = self.start_export(name, source, audio=False)
            _, pid = self.active(name, job, encoder=encoder)
            self.signal_owned(pid, signal.SIGKILL)
            terminal = self.wait_job(name, job, 'failed')
            self.check(terminal.get('exportResult') is None and terminal.get('failureReportRef') is None and terminal.get('failureCode') and terminal['failureCode'] != 'WorkerExportTimedOut', name + ': actual transport/native failure has stable code and no invented Runtime occurrence')
            self.request(name + '-no-report', 'GET', '/api/v1/jobs/' + job['jobId'] + '/export-result', status=404)
        original = self.launcher.read_bytes()
        try:
            self.launcher.unlink()
            job = self.start_export('worker-unavailable', source, audio=False)
            terminal = self.wait_job('worker-unavailable', job, 'failed')
            self.check(terminal['failureCode'] == 'WorkerExportUnavailable', 'first-open: deleted owned launcher retains stable WorkerExportUnavailable')
        finally:
            self.launcher.write_bytes(original); self.launcher.chmod(0o700)

    def reservation_quota(self, source):
        first_owner, first_registration = self.client, self.registration
        first_audio, first_lyrics = self.audio, self.lyrics
        jobs = [self.start_export('reserve-owner-one-running', source, audio=False)]
        _, pid = self.active('reserve-owner-one-running', jobs[0]); self.signal_owned(pid, signal.SIGSTOP)
        jobs.extend(self.start_export('reserve-owner-one-queued-' + str(i), source, audio=False) for i in range(4))
        first_processes = self.live()
        self.register('quota-second-owner')
        second_owner = self.client
        self.audio = self.request('quota-audio-upload', 'POST', '/api/v1/assets', raw=(self.output / 'input.wav').read_bytes(), content_type='audio/wav', headers={'X-Mako-Asset-Name': 'input.wav'}, status=201)
        self.lyrics = self.request('quota-lyrics-upload', 'POST', '/api/v1/assets', raw=b'[00:00.00]<00:00.00>KARAOKE<00:03.00>\n', content_type='text/plain', headers={'X-Mako-Asset-Name': 'input.lrc'}, status=201)
        second = self.create('quota-second-controlled', duration=10_000_000, width=1920, height=1080, numerator=60)
        jobs.append(self.start_export('reserve-owner-two-running', second, audio=False))
        _, pid = self.active('reserve-owner-two-running', jobs[-1], excluded=first_processes); self.signal_owned(pid, signal.SIGSTOP)
        jobs.append(self.start_export('reserve-owner-two-queued', second, audio=False))
        self.mutate('media-plus-report-reservation-full', 'POST', f"/api/v1/project-sessions/{second['projectSessionId']}/export-jobs", {'expectedRevision': second['projectRevision'], 'includeAudio': False}, status=429, code='resourceQuotaExceeded')
        self.check(len(jobs) == 7, 'quota: seven live 64MiB+256KiB reservations fit, eighth exceeds 512MiB including retained bundles')
        for index, job in enumerate(jobs):
            self.client = first_owner if index < 5 else second_owner
            self.heartbeat('reserve-cancel-heartbeat-' + str(index))
            self.cancel('reserve-cancel-' + str(index), job)
            self.wait_job('reserve-cancel-' + str(index), job, 'cancelled')
        self.client, self.registration = first_owner, first_registration
        self.audio, self.lyrics = first_audio, first_lyrics
        self.heartbeat('reserve-restored-first-owner')

    def exercise(self):
        self.host = self.request('health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.register('owner')
        caps = self.request('export-capabilities', 'GET', '/api/v1/export-capabilities')
        for key, expected in {'enabled': True, 'sourceProfile': 'static-lyrics-scene-v1', 'executionProfile': 'static-export-pcmwav-v1', 'maxFrames': 600, 'maxDurationUs': '10000000', 'maxFrameRate': 60, 'maxCanvasWidth': 1920, 'maxCanvasHeight': 1080, 'maxRawFrameBytes': 8*1024*1024, 'operationTimeoutSeconds': 8, 'cancellationGraceSeconds': 2, 'maxMediaBytes': 64*1024*1024, 'maxReportBytes': 256*1024, 'maxOccurrences': 512, 'maxArtifactStorageBytes': 512*1024*1024, 'outputFileGuard': 'linux-x64-rlimit-fsize'}.items():
            self.check(caps[key] == expected, 'capability: actual ' + key)
        self.check(caps['formats'] == ['opaqueMp4', 'transparentProRes4444'] and caps['audioProfiles'] == ['pcm16-wave-self-contained'], 'capability: fixed formats and PCM WAV input only')
        wav = self.output / 'input.wav'
        with wave.open(str(wav), 'wb') as stream:
            stream.setparams((2, 2, 44100, 0, 'NONE', ''))
            cycle = b''.join(struct.pack('<hh', int(10000*math.sin(2*math.pi*440*i/44100)), int(8000*math.sin(2*math.pi*660*i/44100))) for i in range(44100))
            stream.writeframes(cycle*4)
        self.audio = self.request('audio-upload', 'POST', '/api/v1/assets', raw=wav.read_bytes(), content_type='audio/wav', headers={'X-Mako-Asset-Name': 'input.wav'}, status=201)
        self.lyrics = self.request('enhanced-timed-lyrics-upload', 'POST', '/api/v1/assets', raw=b'[00:00.00]<00:00.00>KARAOKE<00:03.00>\n', content_type='text/plain', headers={'X-Mako-Asset-Name': 'input.lrc'}, status=201)
        source = self.fixture('movie30')
        path = f"/api/v1/project-sessions/{source['projectSessionId']}"
        cursor = self.request('before-movie-state', 'GET', f'/api/v1/clients/{self.client}/state')['eventCursor']
        accepted_metadata = self.metadata('movie-once')
        first = self.start_export('ordinary-three-seconds', source, metadata=accepted_metadata)
        _, actual_pid = self.active('ordinary-fixed-source', first)
        self.signal_owned(actual_pid, signal.SIGSTOP)
        repeated = self.start_export('ordinary-identical-retry', source, metadata=accepted_metadata)
        self.check(first == repeated, 'receipt: identical export acceptance returned')
        self.mutate('receipt-payload-conflict', 'POST', path + '/export-jobs',
                    {'expectedRevision': source['projectRevision'], 'format': 'transparentProRes4444'}, metadata=accepted_metadata, status=409, code='requestIdReuse')
        lines = self.request('owned-source-lines', 'GET', path + '/lyrics/lines?branchId=primary')
        edited = self.request('edit-after-acceptance', 'POST', path + '/commands', value=self.command(source['projectRevision'], 'edit-after-acceptance',
            {'type': 'lyrics.setTimeRange', 'target': {'branchId': 'primary', 'lineId': lines['lines'][0]['id']}, 'startUs': '800000', 'endUs': '2950000'}))
        self.check(edited['projectRevision'] != source['projectRevision'], 'fixed input: edit changed current revision after acceptance')
        self.signal_owned(actual_pid, signal.SIGCONT)
        terminal = self.wait_job('ordinary-three-seconds', first)
        self.check(terminal['input']['sourceRevision'] == source['projectRevision'], 'fixed input: completed movie retains accepted old revision')
        self.terminal_events('ordinary', cursor, terminal)
        self.current_success = terminal
        report, raw, media, file = self.read_result('ordinary', terminal)
        self.check(report['expectedFrames'] == 90 and report['audioCodec'] == 'aac', 'ordinary: actual ninety frames with original AAC output algorithm')
        self.decode('ordinary', file, report, 'movie30')
        self.downloads(terminal, raw, media)
        self.success, self.success_raw, self.success_media = terminal, raw, media
        self.heartbeat('undo-after-fixed-input-heartbeat')
        undone = self.request('restore-owned-source-content', 'POST', path + '/commands', value=self.command(edited['projectRevision'], 'restore-owned-source-content', {'type': 'history.undo'}))
        source = {**source, 'projectRevision': undone['projectRevision']}
        self.heartbeat('transparent-heartbeat')
        first = self.start_export('transparent-three-seconds', source, 'transparentProRes4444')
        terminal2 = self.wait_job('transparent-three-seconds', first)
        self.current_success = terminal2
        report2, raw2, media2, file2 = self.read_result('transparent', terminal2)
        self.check(report2['expectedFrames'] == 90 and report2['audioCodec'] == 'pcm_s16le', 'transparent: actual ninety frames and PCM output')
        self.decode('transparent', file2, report2, 'movie30')
        ntsc = self.fixture('movie-ntsc', 30_000)
        first = self.start_export('ntsc', ntsc, 'transparentProRes4444')
        ntsc_terminal = self.wait_job('ntsc', first)
        self.current_success = ntsc_terminal
        nr, _, _, nf = self.read_result('ntsc', ntsc_terminal)
        self.check(nr['expectedFrames'] == 90 and nr['frameRateNumerator'] == 30_000 and nr['frameRateDenominator'] == 1001, 'NTSC: exact shared RoundAway frame plan')
        self.decode('ntsc', nf, nr, 'movie-ntsc')
        first = self.start_export('pure-video', source, audio=False)
        pure = self.wait_job('pure-video', first)
        pr, _, _, pf = self.read_result('pure-video', pure, audio=False)
        streams = json.loads(subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(pf)], check=True, capture_output=True).stdout)['streams']
        self.check(all(s['codec_type'] != 'audio' for s in streams) and pr.get('audioCodec') is None, 'pure video: real output has no audio stream')
        noaudio = self.fixture('no-audio-source', 'noaudio')
        self.heartbeat('missing-audio-heartbeat')
        self.mutate('missing-audio-rejected', 'POST', f"/api/v1/project-sessions/{noaudio['projectSessionId']}/export-jobs", {'expectedRevision': noaudio['projectRevision']}, status=422, code='exportAudioUnsupported')
        oversized = self.create('oversized-source', width=1922, height=1080, audio=False)
        self.mutate('oversized-plan-rejected', 'POST', f"/api/v1/project-sessions/{oversized['projectSessionId']}/export-jobs", {'expectedRevision': oversized['projectRevision'], 'includeAudio': False}, status=422, code='exportInputRejected')
        control = self.create('owned-control', duration=10_000_000, width=1920, height=1080, numerator=60, audio=False)
        self.controls(control)
        self.reservation_quota(control)
        self.publication_failure(source)
        self.lifecycle(control, source)
        self.restart_recovery(control)

    def publication_failure(self, source):
        job = self.start_export('atomic-publication-refused', source)
        _, pid = self.active('atomic-publication-refused', job)
        self.signal_owned(pid, signal.SIGSTOP)
        directory = self.output / 'state/export-artifacts'
        before = {p.name for p in directory.glob('artifact-*')}
        mode = directory.stat().st_mode & 0o777
        try:
            directory.chmod(0o500)
            self.signal_owned(pid, signal.SIGCONT)
            failed = self.wait_job('atomic-publication-refused', job, 'failed')
            self.check(failed.get('exportResult') is None and {p.name for p in directory.glob('artifact-*')} == before, 'publication refusal: previous immutable media preserved without new public bundle')
        finally:
            directory.chmod(mode)
        self.check(list((directory / '.staging').iterdir()) == [], 'publication refusal: owned candidate released after drain')
        self.binary('old-success-after-publication-failure', '/api/v1/jobs/' + self.success['jobId'] + '/export-content', expected=self.success_media)

    def lifecycle(self, control, source):
        job = self.start_export('dormant-owner-export', control, audio=False)
        _, pid = self.active('dormant-owner-export', job)
        self.signal_owned(pid, signal.SIGSTOP)
        lease = self.heartbeat('before-dormancy')
        expires = HELPER.datetime.fromisoformat(lease['leaseExpiresAtUtc'].replace('Z', '+00:00'))
        time.sleep(max(0, (expires - HELPER.utc_now()).total_seconds()) + .1)
        state = self.request('dormant-state', 'GET', f'/api/v1/clients/{self.client}/state')
        self.check(state['dormant'], 'lease: actual HTTP client lease expires')
        running = self.request('dormant-job-retained', 'GET', '/api/v1/jobs/' + job['jobId'])
        self.check(running['state'] == 'running' and not running['cancellationRequested'], 'lease: accepted export survives dormancy')
        owner = self.client
        self.register('same-host-resume', resume=self.registration)
        self.check(self.client == owner, 'resume: task ownership restored by real resume credential')
        self.cancel('resumed-cancel', job); self.wait_job('resumed-cancel', job, 'cancelled')
        job = self.start_export('close-active-export', control, audio=False)
        _, pid = self.active('close-active-export', job); self.signal_owned(pid, signal.SIGSTOP)
        path = f"/api/v1/project-sessions/{control['projectSessionId']}/close"
        self.mutate('close-active-reject', 'POST', path, {'expectedRevision': control['projectRevision'], 'policy': 'discard', 'activeJobs': 'reject'}, status=409, code='activeJobs')
        closed = self.mutate('close-cancel-wait', 'POST', path, {'expectedRevision': control['projectRevision'], 'policy': 'discard', 'activeJobs': 'cancelAndWait'}, status=202)
        self.wait_job('close-cancel-wait', closed); self.wait_job('close-cancelled-export', job, 'cancelled')
        self.check(pid not in self.live(), 'close: real owned Worker drained')
        self.binary('closed-project-retained-media', '/api/v1/jobs/' + self.success['jobId'] + '/export-content', expected=self.success_media)

    def start_server(self, name):
        path = self.output / (name + '.stdout.log')
        with path.open('wb') as log:
            self.process = subprocess.Popen(self.server_command + ['--urls', 'http://127.0.0.1:0'], env=self.environment, stdout=log, stderr=subprocess.STDOUT)
        until = time.monotonic() + 35
        while time.monotonic() < until:
            ready = [v for v in path.read_text().splitlines() if v.startswith('BACKEND_READY ')]
            if ready:
                self.base_url = ready[-1].split()[1]
                return
            self.check(self.process.poll() is None, name + ': process alive before ready')
            time.sleep(.05)
        raise AssertionError(name + ': server not ready')

    def stop_server(self, name):
        self.capture(); self.process.terminate()
        code = self.process.wait(timeout=25)
        self.check(code == 0 and not self.live(), name + ': normal Server exit0 and all owned group members drained')
        self.server_runs.append({'name': name, 'pid': self.process.pid, 'exitCode': code})

    def restart_recovery(self, control):
        source = self.create('shutdown-controlled-source', duration=10_000_000, width=1920, height=1080, numerator=60, audio=False)
        job = self.start_export('shutdown-active-export', source, audio=False)
        _, pid = self.active('shutdown-active-export', job); self.signal_owned(pid, signal.SIGSTOP)
        oldhost = self.host
        self.stop_server('shutdown-with-active-export')
        self.start_server('restarted-server')
        self.host = self.request('restart-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.check(self.host != oldhost, 'restart: new host identity')
        self.register('restart-profile-authorized-client')
        jobs = self.request('recovery-jobs', 'GET', '/api/v1/recovery-jobs')['jobs']
        recovered = next(v for v in jobs if v['job']['jobId'] == self.success['jobId'])
        self.check(recovered['previousHostInstanceId'] == oldhost and recovered['job']['exportResult'] == self.success['exportResult'], 'recovery: only registered previous-host media/report reference retained')
        cancelled = next(v['job'] for v in jobs if v['job']['jobId'] == job['jobId'])
        self.check(cancelled['state'] == 'cancelled' and cancelled.get('exportResult') is None, 'shutdown: active cancelled export drained and persisted')
        path = '/api/v1/recovery-jobs/' + self.success['jobId']
        self.binary('recovery-report', path + '/export-result', expected=self.success_raw)
        self.binary('recovery-media', path + '/export-content', expected=self.success_media)
        outsider = self.register('recovery-second-authorized-client', select=False)
        self.binary('recovery-profile-second-client-media', path + '/export-content', client=outsider['clientId'], expected=self.success_media)
        self.request('recovery-unauthenticated-media', 'GET', path + '/export-content', headers={'Authorization': 'Bearer ' + '0'*64}, status=401, code='unauthorizedClient')
        self.request('no-orphan-recovery-fallback', 'GET', '/api/v1/recovery-jobs/job-' + '0'*32 + '/export-content', status=404, code='exportArtifactNotFound')
        self.check(not self.live(), 'recovery: no Worker automatically replays old export')

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False)
        (self.output / 'projects').mkdir()
        self.bootstrap = secrets.token_hex(32)
        launcher = ("#!/bin/sh\nexec '" + str(self.worker).replace("'", "'\\''") + "' \"$@\"\n").encode()
        self.launcher.write_bytes(launcher); self.launcher.chmod(0o700)
        self.environment = os.environ.copy()
        for key in ('DISPLAY', 'WAYLAND_DISPLAY', 'MAKO_BACKEND_FAKE_MEDIA', 'MAKO_BACKEND_CREDENTIAL_FILE'):
            self.environment.pop(key, None)
        self.environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap, MAKO_BACKEND_RESOURCE_ROOT=str(self.output / 'resources'),
            MAKO_BACKEND_STATE_ROOT=str(self.output / 'state'), MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({'export-probe-root': str(self.output / 'projects')}),
            MAKO_BACKEND_CLIENT_TOKEN_SECONDS='3600', MAKO_BACKEND_CLIENT_LEASE_SECONDS='2', MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS='8',
            MAKO_BACKEND_WORKER_PATH=str(self.launcher), MAKO_BACKEND_FFMPEG_PATH='/usr/bin/ffmpeg', MAKO_BACKEND_FFPROBE_PATH='/usr/bin/ffprobe')
        failure = None
        try:
            self.start_server('server'); self.exercise()
        except Exception as error:
            failure = str(error); (self.output / 'failure.txt').write_text(traceback.format_exc())
        finally:
            for decoder in self.decoders:
                if decoder.poll() is None:
                    decoder.kill(); decoder.wait(timeout=5)
                    if failure is None: failure = 'Oracle video decoder required forced cleanup'
            if self.process and self.process.poll() is None:
                try:
                    self.stop_server('final-shutdown')
                except Exception:
                    self.process.kill(); self.process.wait(timeout=5)
                    self.server_runs.append({'name': 'forced-final-cleanup', 'pid': self.process.pid, 'exitCode': self.process.returncode})
                    if failure is None: failure = 'Server required forced cleanup'
            for pid in self.live():
                self.signal_owned(pid, signal.SIGKILL)
            self.launcher.unlink(missing_ok=True)
            for name in ('bootstrap.json', 'bootstrap.cap'):
                (self.output / 'resources' / name).unlink(missing_ok=True)
            scan_start = len(self.assertions)
            for path in self.output.rglob('*'):
                if path.is_file():
                    self.check(not any(v.encode() in path.read_bytes() for v in [self.bootstrap, *self.credentials]), 'credential artifact scan: ' + str(path.relative_to(self.output)))
            manifest = {'succeeded': failure is None, 'failure': failure, 'requestCount': self.request_count,
                'workflowAssertions': scan_start, 'credentialScanAssertions': len(self.assertions)-scan_start, 'assertionCount': len(self.assertions),
                'assertions': self.assertions, 'results': self.results, 'comparisons': self.comparisons, 'serverRuns': self.server_runs,
                'serverPid': self.process.pid if self.process else None,
                'observedProcesses': list(self.identities.values()), 'survivingOwnedPids': self.live(),
                'oracleDecoderProcesses': [{'pid': value.pid, 'exitCode': value.returncode} for value in self.decoders],
                'codeEndpoint': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip(), 'pythonNumpyVersion': np.__version__,
                'serverSha256': hashlib.sha256(Path(self.server_command[0]).read_bytes()).hexdigest(),
                'workerSha256': hashlib.sha256(self.worker.read_bytes()).hexdigest(), 'launcherSha256': hashlib.sha256(launcher).hexdigest(),
                'boundaries': ['Success static diagnostics are empty; no script/Runtime failure occurrence fabricated.', 'Actual one-hour TTL passage and active-read expiry accounting use Host TimeProvider tests, not elapsed HTTP acceptance.', 'Previous-host recovery is bootstrap-authorized profile access; current live jobs/results remain owner-only.', 'FSIZE hard-limit execution is in owned C# child regression; HTTP checks advertised policy and reservation/input budgets.']}
            HELPER.save_json(self.output / 'manifest.json', manifest)
        if failure: raise SystemExit(failure)
        print(json.dumps({'succeeded': True, 'requests': self.request_count, 'assertions': len(self.assertions)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--server', nargs='+', required=True)
    parser.add_argument('--worker', required=True)
    parser.add_argument('--oracle', nargs='+', required=True)
    args = parser.parse_args()
    ExportProbe(args.output.resolve(), args.server, args.worker, args.oracle).run()
