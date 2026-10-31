#!/usr/bin/env python3
"""Owned real HTTP audio-analysis probe; original analyzer is the only oracle."""
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
import urllib.request
import wave

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('auth_http_helper', ROOT / 'tools/backend-http-probe/probe.py')
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)


class AnalysisProbe(HELPER.Probe):
    def __init__(self, output, server, worker, oracle):
        super().__init__(output, server)
        self.worker, self.oracle = Path(worker).resolve(), oracle
        self.process = None
        self.processes, self.credentials, self.results, self.comparisons, self.server_runs = {}, [], [], [], []
        self.worker_launcher = output / 'owned-worker-launcher'
        self.native_launcher = output / 'owned-ffmpeg-control-launcher'
        self.native_observations = []

    def register(self, *args, **kwargs):
        value = super().register(*args, **kwargs)
        self.credentials.extend([value['accessToken'], value['resumeToken']])
        return value

    def mutate(self, name, path, value, *, metadata=None, **kwargs):
        return self.request(name, 'POST', path, value={**value, 'metadata': metadata or self.metadata(name)}, **kwargs)

    def heartbeat(self, name):
        return self.request(name, 'POST', f'/api/v1/clients/{self.client}/heartbeat', value={})

    @staticmethod
    def identity(pid):
        try:
            fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
            return {'pid': pid, 'parent': int(fields[1]), 'group': int(fields[2]), 'startTicks': fields[19], 'state': fields[0]}
        except (FileNotFoundError, ProcessLookupError, IndexError):
            return None

    def capture(self):
        entries = []
        for path in Path('/proc').iterdir():
            if path.name.isdigit() and (value := self.identity(int(path.name))):
                entries.append(value)
        groups = {v['group'] for v in self.processes.values()}
        for value in entries:
            if value['parent'] != self.process.pid or value['group'] != value['pid']:
                continue
            try:
                cmd = Path(f"/proc/{value['pid']}/cmdline").read_bytes()
            except (FileNotFoundError, ProcessLookupError):
                continue
            if b'MakoKaraGenerator.Backend.Worker' in cmd:
                prior = self.processes.get(value['pid'])
                self.check(prior is None or prior['startTicks'] == value['startTicks'], 'ownership: Worker pid/startTicks not reused')
                self.processes[value['pid']] = value
                groups.add(value['group'])
        for value in entries:
            if value['group'] in groups:
                prior = self.processes.get(value['pid'])
                self.check(prior is None or prior['startTicks'] == value['startTicks'], 'ownership: private group member identity stable')
                self.processes[value['pid']] = value

    def live(self):
        return [pid for pid, old in self.processes.items()
                if (value := self.identity(pid)) and value['startTicks'] == old['startTicks']]

    def signal_owned(self, pid, action):
        value, old = self.identity(pid), self.processes[pid]
        self.check(value and value['startTicks'] == old['startTicks'] and value['group'] == old['group'], 'signal: exact owned PID/startTicks/privategroup verified')
        os.kill(pid, action)

    def wait_job(self, name, job, expected='succeeded'):
        end, polls = time.monotonic() + 25, 0
        while time.monotonic() < end:
            self.capture(); polls += 1
            value = self.request(f'{name}-poll-{polls}', 'GET', '/api/v1/jobs/' + job['jobId'])
            if value['state'] in ('succeeded', 'failed', 'cancelled', 'unknown'):
                self.check(value['state'] == expected and not value['canCancel'], name + ': drained terminal ' + expected)
                self.results.append(value)
                return value
            time.sleep(.04)
        raise AssertionError(name + ': no bounded terminal')

    def active(self, name, job, native=False, excluded=()):
        end, polls = time.monotonic() + 8, 0
        while time.monotonic() < end:
            self.capture(); polls += 1
            state = self.request(f'{name}-active-{polls}', 'GET', '/api/v1/jobs/' + job['jobId'])
            for pid in self.live():
                if pid in excluded:
                    continue
                value = self.processes[pid]
                try:
                    argv = Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')
                except (FileNotFoundError, ProcessLookupError):
                    continue
                if (native and b'pcm_f32le' in argv) or (not native and value['group'] == pid):
                    if native:
                        self.check(argv[0] == b'/usr/bin/ffmpeg' and b'-re' in argv
                                   and argv[argv.index(b'-protocol_whitelist') + 1] == b'file,pipe'
                                   and argv[argv.index(b'-f') + 1] == b'wav'
                                   and argv[argv.index(b'-ar') + 1] == b'48000',
                                   name + ': actual controlled FFmpeg uses trusted WAV/file,pipe/48k policy')
                        self.native_observations.append({'identity': value, 'argv': [part.decode() for part in argv if part], 'controlledInputReadRate': True})
                    return state, pid
            self.check(state['state'] not in ('failed', 'cancelled', 'succeeded'), name + ': controlled process observed before terminal')
            time.sleep(.002)
        raise AssertionError(name + ': no actual owned native process')

    def start_analysis(self, name, source, metadata=None, **kwargs):
        self.heartbeat(name + '-heartbeat')
        return self.mutate(name, f"/api/v1/project-sessions/{source['projectSessionId']}/analysis-jobs",
                           {'expectedRevision': source['projectRevision']}, metadata=metadata, status=202, **kwargs)

    def cancel(self, name, job):
        return self.request(name, 'POST', '/api/v1/jobs/' + job['jobId'] + '/cancel', value=self.metadata(name + '-cancel-command'))

    def upload(self, name, path):
        return self.request(name, 'POST', '/api/v1/assets', raw=path.read_bytes(), content_type='audio/wav', headers={'X-Mako-Asset-Name': path.name}, status=201)

    def lyrics_upload(self, name):
        return self.request(name, 'POST', '/api/v1/assets', raw=b'[00:00.00]<00:00.00>KARAOKE<00:01.00>\n',
                            content_type='text/plain', headers={'X-Mako-Asset-Name': 'owned.lrc'}, status=201)

    def create(self, name, audio):
        self.heartbeat(name + '-heartbeat')
        return self.mutate(name, '/api/v1/project-sessions', {'name': name, 'audioAssetId': audio['assetId'], 'lyricsAssetId': self.lyrics['assetId'],
            'importerId': 'lrc.enhanced', 'composition': {'canvasWidth': 320, 'canvasHeight': 180, 'outputDurationUs': 1_000_000}}, status=201)

    def project_state(self, name, source):
        path = '/api/v1/project-sessions/' + source['projectSessionId']
        summary = self.request(name + '-summary', 'GET', path)
        history = self.request(name + '-history', 'GET', path + '/history')
        return {key: summary[key] for key in ('projectRevision', 'isDirty', 'canUndo', 'canRedo')}, {key: history[key] for key in ('projectRevision', 'undoCount', 'redoCount')}

    def oracle_run(self, name, wav):
        prefix = self.output / (name + '-oracle')
        result = subprocess.run(self.oracle + [str(wav), str(prefix)], capture_output=True, timeout=30)
        (self.output / (name + '-oracle.stdout.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': independent original analyzer and canonical encoder exit0')
        return json.loads(Path(str(prefix) + '.json').read_text()), Path(str(prefix) + '.analysis.json').read_bytes()

    def result(self, name, terminal, expected=None, metadata=None, recovered=False):
        ref = terminal['analysisResult']['report']
        path = ('/api/v1/recovery-jobs/' if recovered else '/api/v1/jobs/') + terminal['jobId'] + '/analysis-result'
        headers, raw = self.binary(name, path, expected=expected)
        report = json.loads(raw)
        self.check(len(raw) == ref['sizeBytes'] and hashlib.sha256(raw).hexdigest() == ref['sha256'] == terminal['analysisResult']['contentHash'], name + ': complete canonical sidecar exact size and content hash')
        self.check(headers['Content-Type'].split(';')[0] == 'application/json', name + ': canonical JSON content type')
        if metadata is not None:
            for key in ('algorithm', 'sampleRate', 'hopSamples', 'originUs', 'sourceHash', 'pcmHash', 'sampleCount', 'durationUs', 'revision', 'contentHash', 'pointCount'):
                self.check(terminal['analysisResult'][key] == metadata[key], name + ': original analyzer exact ' + key)
            self.check(terminal['analysisInput']['audio']['sha256'] == metadata['sourceHash'] == report['sourceHash'], name + ': fixed authorized source hash')
            self.check(metadata['endAvailability'] == 'OutsideRange', name + ': original half-open duration remains exact')
        self.check(len(report['points']) == (report['sampleCount'] + 959) // 960 and report['durationUs'] == (report['sampleCount'] * 1_000_000 + 47999) // 48000, name + ': complete real hops and ceil duration')
        return raw

    def events(self, name, cursor, terminal):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + '/api/v1/events', headers={'X-Mako-Client-Id': self.client,
            'X-Mako-Host-Instance-Id': self.host, 'Authorization': 'Bearer ' + self.tokens[self.client], 'Last-Event-ID': cursor})
        events, current = [], []
        with self.http.open(request, timeout=10) as response:
            self.check(response.status == 200, name + ': authenticated SSE replay')
            while len(events) < 256:
                line = response.readline().decode().rstrip('\r\n')
                if line:
                    current.append(line); continue
                if not current:
                    continue
                data = [v[6:] for v in current if v.startswith('data: ')]
                current = []
                if not data:
                    continue
                event = json.loads('\n'.join(data)); events.append(event)
                if event.get('type') == 'job.terminal' and (event.get('job') or {}).get('jobId') == terminal['jobId']:
                    progress = [v['job']['analysisProgress'] for v in events if (v.get('job') or {}).get('jobId') == terminal['jobId'] and v['job'].get('analysisProgress')]
                    counts = [int(v['completedSamples']) for v in progress]
                    self.check(counts == sorted(counts) and all(0 <= n <= int(terminal['analysisResult']['sampleCount']) for n in counts), name + ': observed actual sample progress monotonic and bounded; gaps allowed')
                    self.check(event['job']['analysisProgress']['completedSamples'] == terminal['analysisResult']['sampleCount'], name + ': authoritative terminal keeps full actual sample count')
                    HELPER.save_json(self.output / (name + '-sse.json'), events)
                    return
        raise AssertionError(name + ': missing bounded replay terminal')

    def downloads(self, terminal, raw):
        path = '/api/v1/jobs/' + terminal['jobId'] + '/analysis-result'
        headers, _ = self.binary('canonical-download', path, expected=raw)
        self.check(headers['ETag'] == '"' + terminal['analysisResult']['contentHash'] + '"', 'download: ETag is complete content hash')
        self.binary('canonical-prefix-range', path, headers={'Range': 'bytes=0-31'}, status=206, expected=raw[:32])
        self.binary('canonical-suffix-range', path, headers={'Range': 'bytes=-16'}, status=206, expected=raw[-16:])
        self.binary('canonical-304', path, headers={'If-None-Match': headers['ETag']}, status=304, expected=b'')
        self.binary('canonical-416', path, headers={'Range': 'bytes=999999999-'}, status=416, expected=b'')
        self.request_count += 1
        request = urllib.request.Request(self.base_url + path, method='HEAD', headers={'X-Mako-Client-Id': self.client,
            'X-Mako-Host-Instance-Id': self.host, 'Authorization': 'Bearer ' + self.tokens[self.client]})
        with self.http.open(request, timeout=10) as response:
            self.check(response.status == 200 and response.read() == b'' and int(response.headers['Content-Length']) == len(raw) and response.headers['ETag'] == headers['ETag'], 'HEAD: exact length/hash without content')
        outsider = self.register('normal-outsider', select=False)
        for suffix in ('', '/analysis-result'):
            self.request('current-owner-404-' + str(len(suffix)), 'GET', '/api/v1/jobs/' + terminal['jobId'] + suffix, client=outsider['clientId'], status=404, code='jobNotFound')
        self.request('unauthenticated-result', 'GET', path, headers={'Authorization': 'Bearer ' + '0'*64}, status=401, code='unauthorizedClient')
        self.request('current-host-not-recovery', 'GET', '/api/v1/recovery-jobs/' + terminal['jobId'] + '/analysis-result', status=404, code='jobNotFound')
        file = self.output / 'state/analysis-results' / (terminal['analysisResult']['report']['reportId'] + '.json')
        self.check(file.read_bytes() == raw, 'sidecar: registered persisted canonical bytes exactly match HTTP')
        try:
            file.write_bytes(bytes([raw[0] ^ 1]) + raw[1:])
            self.request('corrupt-owned-sidecar', 'GET', path, status=500, code='taskExecutionFailed')
            self.check(self.request('job-retained-after-corruption', 'GET', '/api/v1/jobs/' + terminal['jobId']) == terminal, 'integrity failure: original terminal and result reference unchanged')
        finally:
            file.write_bytes(raw)
        self.binary('restored-owned-sidecar', path, expected=raw)
        self.check(HELPER.datetime.fromisoformat(terminal['expiresAtUtc'].replace('Z', '+00:00')) > HELPER.utc_now(), 'TTL: returned real expiry is future; no one-hour elapsed claim')

    def fixtures(self):
        cases = [('silence-48', 48000, 48624, 1), ('bands-48', 48000, 144000, 1), ('pulse-tail-48', 48000, 312624, 1),
                 ('resample-32', 32000, 32416, 1), ('resample-441-stereo', 44100, 44673, 2), ('resample-96', 96000, 97248, 1)]
        for name, rate, count, channels in cases:
            data = bytearray()
            for i in range(count):
                t = i / rate
                if name.startswith('silence'):
                    value = 0
                elif name.startswith('bands'):
                    value = .7 * math.sin(2*math.pi*(80 if t < 1 else 1000 if t < 2 else 5)*t)
                elif name.startswith('pulse'):
                    value = .8*math.sin(2*math.pi*80*t) if .2 <= t < 4.3 and (t-.2) % .5 < .06 else 0
                else:
                    value = .7*math.sin(2*math.pi*80*t)
                data.extend(struct.pack('<h', int(value*32767)))
                if channels == 2:
                    data.extend(struct.pack('<h', int(.5*32767*math.sin(2*math.pi*1000*t))))
            path = self.output / (name + '.wav')
            with wave.open(str(path), 'wb') as stream:
                stream.setparams((channels, 2, rate, 0, 'NONE', '')); stream.writeframes(data)
            yield name, path

    def normal(self):
        self.host = self.request('normal-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.register('normal-owner')
        caps = self.request('analysis-capabilities', 'GET', '/api/v1/analysis-capabilities')
        for key, expected in {'enabled': True, 'sourceProfile': 'pcm16-wave-analysis-v1', 'algorithm': 'bass-pulse-rbj-v1',
            'maxAudioBytes': 16*1024*1024, 'maxSourceSampleRate': 192000, 'maxSourceDurationUs': '300000000', 'sampleRate': 48000,
            'hopSamples': 960, 'maxSamples': '14400000', 'maxPoints': 15000, 'maxResultBytes': 4*1024*1024,
            'maxResultStorageBytes': 64*1024*1024, 'operationTimeoutSeconds': 8, 'cancellationGraceSeconds': 2,
            'guardAvailable': True, 'toolStartupVerified': True, 'maxRunning': 2, 'maxRunningPerClient': 1, 'maxRunningPerProject': 1,
            'maxQueued': 16, 'maxQueuedPerClient': 4}.items(): self.check(caps[key] == expected, 'capability: actual ' + key)
        self.lyrics = self.lyrics_upload('normal-lyrics')
        self.normal_sources = []
        for index, (name, path) in enumerate(self.fixtures()):
            audio = self.upload(name + '-audio', path)
            source = self.create(name + '-project', audio)
            self.normal_sources.append(source)
            meta, canonical = self.oracle_run(name, path)
            before = self.project_state(name + '-before', source)
            cursor = self.request(name + '-client-state', 'GET', f'/api/v1/clients/{self.client}/state')['eventCursor']
            receipt = self.metadata(name + '-once')
            job = self.start_analysis(name + '-analysis', source, receipt)
            if index == 0:
                _, pid = self.active('normal-fixed-source', job); self.signal_owned(pid, signal.SIGSTOP)
                self.check(self.start_analysis('normal-identical-retry', source, receipt) == job, 'receipt: exact accepted response retained')
                self.mutate('normal-receipt-payload-conflict', f"/api/v1/project-sessions/{source['projectSessionId']}/analysis-jobs", {'expectedRevision': '999'}, metadata=receipt, status=409, code='requestIdReuse')
                lines = self.request('normal-lines', 'GET', f"/api/v1/project-sessions/{source['projectSessionId']}/lyrics/lines?branchId=primary")
                edited = self.request('normal-edit-after-acceptance', 'POST', f"/api/v1/project-sessions/{source['projectSessionId']}/commands",
                    value=self.command(source['projectRevision'], 'normal-edit-after-acceptance', {'type': 'lyrics.setTimeRange',
                        'target': {'branchId': 'primary', 'lineId': lines['lines'][0]['id'],
                                   'tokenId': lines['lines'][0]['tokens'][0]['id']}, 'startUs': '0', 'endUs': '1011000'}))
                before = self.project_state('normal-after-explicit-edit', source)
                self.check(edited['projectRevision'] != source['projectRevision'], 'fixed source: accepted revision predates explicit edit')
                self.signal_owned(pid, signal.SIGCONT)
            terminal = self.wait_job(name, job)
            self.check(terminal['analysisInput']['sourceRevision'] == source['projectRevision'], name + ': exact accepted project revision retained')
            self.check(terminal.get('validationResult') is None and terminal.get('exportResult') is None and terminal.get('failureReportRef') is None, name + ': result-only analysis shares original job without fabricated other result')
            self.events(name, cursor, terminal)
            raw = self.result(name + '-canonical', terminal, canonical, meta)
            self.check(self.project_state(name + '-after', source) == before, name + ': task completion does not change project revision, dirty state or history')
            points = json.loads(raw)['points']
            if name.startswith('silence'):
                self.check(meta['sampleCount'] == '48624' and meta['pointCount'] == 51 and meta['durationUs'] == '1013000', 'silence: real nonmultiple-hop sample count/ceil duration/last point')
                self.check(all(v['bassLevel'] == v['pulse'] == v['beatConfidence'] == 0 and v['beatPhase'] is None for v in points), 'silence: actual analyzed zero features, no invented beat')
                self.success, self.success_raw = terminal, raw
                self.downloads(terminal, raw)
            elif name.startswith('bands'):
                self.check(.65 < points[40]['bassLevel'] < .85 and points[90]['bassLevel'] < .1 and points[140]['bassLevel'] < .15, 'bands: real low/high/out-of-band frequency response preserved')
            elif name.startswith('pulse'):
                self.check(points[123]['beatPhase'] is not None and .35 < points[123]['beatPhase'] < .65, 'pulse: original approximate stable rhythm fixture preserved, not precise beat recognition')
            self.comparisons.append({'name': name, 'canonicalBytesEqual': True, 'allFeaturePointsEqual': True, 'metadata': meta, 'sourceFileSha256': hashlib.sha256(path.read_bytes()).hexdigest()})
            if index == 0:
                source['projectRevision'] = edited['projectRevision']
        self.normal_shutdown_source = self.normal_sources[-1]

    def restart_control(self):
        job = self.start_analysis('normal-shutdown-active-analysis', self.normal_shutdown_source)
        _, pid = self.active('normal-shutdown-active-analysis', job); self.signal_owned(pid, signal.SIGSTOP)
        oldhost = self.host
        self.stop_server('normal-server-shutdown-with-active-analysis')
        self.start_server('controlled-server', controlled=True)
        self.host = self.request('controlled-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.check(self.host != oldhost, 'restart: new authoritative host identity')
        self.register('controlled-profile-authorized-owner')
        recovered = self.request('recovery-jobs', 'GET', '/api/v1/recovery-jobs')['jobs']
        prior = next(v for v in recovered if v['job']['jobId'] == self.success['jobId'])
        self.check(prior['previousHostInstanceId'] == oldhost and prior['job']['analysisResult'] == self.success['analysisResult'], 'recovery: only registered original complete result is retained')
        cancelled = next(v['job'] for v in recovered if v['job']['jobId'] == job['jobId'])
        self.check(cancelled['state'] == 'cancelled' and cancelled.get('analysisResult') is None, 'shutdown: real active analysis cancelled and drained, no replayed result')
        self.result('recovery-complete-canonical', self.success, self.success_raw, recovered=True)
        self.request('previous-host-not-live-job', 'GET', '/api/v1/jobs/' + self.success['jobId'], status=404, code='jobNotFound')
        other = self.register('recovery-second-profile-client', select=False)
        self.binary('recovery-profile-result', '/api/v1/recovery-jobs/' + self.success['jobId'] + '/analysis-result', client=other['clientId'], expected=self.success_raw)
        self.request('recovery-unknown-no-fallback', 'GET', '/api/v1/recovery-jobs/job-' + '0'*32 + '/analysis-result', status=404, code='jobNotFound')
        self.request('recovery-no-auth', 'GET', '/api/v1/recovery-jobs/' + self.success['jobId'] + '/analysis-result', headers={'Authorization': 'Bearer ' + '0'*64}, status=401, code='unauthorizedClient')
        self.lyrics = self.lyrics_upload('controlled-lyrics')
        self.control_audio = self.upload('controlled-audio', self.output / 'pulse-tail-48.wav')
        self.control_source = self.create('controlled-project', self.control_audio)

    def controls(self):
        source = self.control_source
        job = self.start_analysis('active-cancel', source)
        _, native = self.active('active-cancel', job, native=True); self.signal_owned(native, signal.SIGSTOP)
        queued = [self.start_analysis('queued-' + str(i), source) for i in range(4)]
        self.check(all(v['state'] == 'queued' for v in queued), 'queue: one active/four queued per client')
        self.mutate('queue-over-cap', f"/api/v1/project-sessions/{source['projectSessionId']}/analysis-jobs", {'expectedRevision': source['projectRevision']}, status=429, code='requestQuotaExceeded')
        for index, value in enumerate(queued):
            self.cancel('cancel-queued-' + str(index), value)
            terminal = self.wait_job('cancel-queued-' + str(index), value, 'cancelled')
            self.check(terminal.get('analysisProgress') is None and terminal.get('analysisResult') is None, 'queued cancel: no decode samples or partial result')
        self.cancel('cancel-active-native', job)
        terminal = self.wait_job('cancel-active-native', job, 'cancelled')
        self.check(native not in self.live() and terminal.get('analysisResult') is None, 'cancel: actual stopped native decoder drained before terminal')
        job = self.start_analysis('deadline', source)
        _, native = self.active('deadline', job, native=True); self.signal_owned(native, signal.SIGSTOP)
        terminal = self.wait_job('deadline', job, 'failed')
        self.check(terminal['failureCode'] == 'WorkerAnalysisTimedOut' and not terminal['cancellationRequested'] and native not in self.live(), 'deadline: neutral failure differs from cancellation and drains actual native process')
        for name, root_death in [('worker-root-death', True), ('native-failure', False)]:
            job = self.start_analysis(name, source)
            _, native = self.active(name, job, native=True)
            group = self.processes[native]['group']
            members = [v for v in self.processes.values() if v['group'] == group]
            if root_death:
                self.signal_owned(native, signal.SIGSTOP); self.signal_owned(group, signal.SIGKILL)
            else:
                self.signal_owned(native, signal.SIGKILL)
            terminal = self.wait_job(name, job, 'failed')
            expected_code = 'WorkerSessionConflict' if root_death else 'AudioAnalysisFailed'
            self.check(terminal.get('analysisResult') is None and terminal.get('failureCode') == expected_code,
                       name + ': real failure keeps exact stable reason and no partial canonical result')
            self.check(not any(v['pid'] in self.live() for v in members), name + ': root and all observed native group identities gone before terminal read')
            self.request(name + '-no-result', 'GET', '/api/v1/jobs/' + job['jobId'] + '/analysis-result', status=404, code='jobNotFound')
        original = self.worker_launcher.read_bytes()
        try:
            self.worker_launcher.unlink()
            job = self.start_analysis('worker-unavailable', source)
            failed = self.wait_job('worker-unavailable', job, 'failed')
            self.check(failed['failureCode'] == 'WorkerAnalysisUnavailable', 'unavailable: owned launcher removal preserves first-open neutral reason')
        finally:
            self.worker_launcher.write_bytes(original); self.worker_launcher.chmod(0o700)

    def quota(self):
        first_owner, registration, first_lyrics = self.client, self.registration, self.lyrics
        jobs = []
        first_group = []
        for owner in range(4):
            if owner:
                self.register('quota-owner-' + str(owner))
                self.lyrics = self.lyrics_upload('quota-lyrics-' + str(owner))
                audio = self.upload('quota-audio-' + str(owner), self.output / 'pulse-tail-48.wav')
                source = self.create('quota-source-' + str(owner), audio)
            else:
                source = self.control_source
            count = 5 if owner < 2 else 4 if owner == 2 else 1
            for index in range(count):
                job = self.start_analysis(f'quota-{owner}-{index}', source)
                active = owner < 2 and index == 0
                if active:
                    _, pid = self.active(f'quota-{owner}', job, excluded=first_group)
                    self.signal_owned(pid, signal.SIGSTOP); first_group = self.live()
                jobs.append((self.client, job, active))
            if owner == 3:
                self.mutate('result-reservation-over-cap', f"/api/v1/project-sessions/{source['projectSessionId']}/analysis-jobs", {'expectedRevision': source['projectRevision']}, status=429, code='resourceQuotaExceeded')
        self.check(len(jobs) == 15, 'storage: fifteen4MiB reservations fit; sixteenth exceeds64MiB with retained real reports')
        for index, (owner, job, active) in enumerate(sorted(jobs, key=lambda value: value[2])):
            self.client = owner; self.heartbeat('quota-cancel-heartbeat-' + str(index))
            self.cancel('quota-cancel-' + str(index), job); self.wait_job('quota-cancel-' + str(index), job, 'cancelled')
        self.client, self.registration, self.lyrics = first_owner, registration, first_lyrics
        self.heartbeat('quota-restored-owner')

    def lifecycle(self):
        source = self.control_source
        job = self.start_analysis('dormancy', source)
        _, native = self.active('dormancy', job, native=True); self.signal_owned(native, signal.SIGSTOP)
        lease = self.heartbeat('before-dormancy')
        expires = HELPER.datetime.fromisoformat(lease['leaseExpiresAtUtc'].replace('Z', '+00:00'))
        time.sleep(max(0, (expires - HELPER.utc_now()).total_seconds()) + .1)
        state = self.request('dormant-state', 'GET', f'/api/v1/clients/{self.client}/state')
        self.check(state['dormant'], 'lease: actual client became dormant')
        current = self.request('dormant-task-retained', 'GET', '/api/v1/jobs/' + job['jobId'])
        self.check(current['state'] == 'running' and not current['cancellationRequested'], 'lease: accepted analysis remains alive independently of client heartbeat')
        owner = self.client; self.register('same-host-resume', resume=self.registration)
        self.check(self.client == owner, 'resume: original client task ownership retained')
        self.cancel('resumed-cancel', job); self.wait_job('resumed-cancel', job, 'cancelled')
        job = self.start_analysis('close-active', source)
        _, native = self.active('close-active', job, native=True); self.signal_owned(native, signal.SIGSTOP)
        path = f"/api/v1/project-sessions/{source['projectSessionId']}/close"
        self.mutate('close-reject-active', path, {'expectedRevision': source['projectRevision'], 'policy': 'discard', 'activeJobs': 'reject'}, status=409, code='activeJobs')
        close = self.mutate('close-cancel-wait', path, {'expectedRevision': source['projectRevision'], 'policy': 'discard', 'activeJobs': 'cancelAndWait'}, status=202)
        self.wait_job('close-cancel-wait', close); self.wait_job('close-cancelled-analysis', job, 'cancelled')
        self.check(native not in self.live(), 'close: all owned native decode pipes drained')
        self.result('old-result-after-close', self.success, self.success_raw, recovered=True)

    def publication_failure(self):
        source = self.control_source
        job = self.start_analysis('publication-refused', source)
        _, pid = self.active('publication-refused', job); self.signal_owned(pid, signal.SIGSTOP)
        directory = self.output / 'state/analysis-results'
        before, mode = set(directory.iterdir()), directory.stat().st_mode & 0o777
        try:
            directory.chmod(0o500); self.signal_owned(pid, signal.SIGCONT)
            failed = self.wait_job('publication-refused', job, 'failed')
            self.check(failed.get('analysisResult') is None and failed.get('failureCode') and set(directory.iterdir()) == before, 'publication: real filesystem refusal preserves old complete reports and leaves no new sidecar')
        finally:
            directory.chmod(mode)
        self.result('old-report-after-publication-failure', self.success, self.success_raw, recovered=True)

    def input_rejection(self):
        self.heartbeat('input-budget-heartbeat')
        self.request('asset-over-size', 'POST', '/api/v1/assets', raw=b'x'*(16*1024*1024+1), content_type='audio/wav', headers={'X-Mako-Asset-Name': 'oversized.wav'}, status=413, code='resourceTooLarge')
        path = self.output / 'float-input.wav'
        with path.open('wb') as stream:
            data = b'\0'*48000*4
            stream.write(b'RIFF'+struct.pack('<I',36+len(data))+b'WAVEfmt '+struct.pack('<IHHIIHH',16,3,1,48000,192000,4,32)+b'data'+struct.pack('<I',len(data))+data)
        audio = self.upload('unsupported-float-upload', path)
        source = self.create('unsupported-float-source', audio)
        self.mutate('unsupported-float-analysis', f"/api/v1/project-sessions/{source['projectSessionId']}/analysis-jobs", {'expectedRevision': source['projectRevision']}, status=400, code='invalidRequest')
        # A small low-rate header exercises the300-second admission check. The
        # analysis Worker is never accepted; no300-second48k analysis is run.
        path = self.output / 'duration-boundary.wav'
        with wave.open(str(path), 'wb') as stream:
            stream.setparams((1,2,100,0,'NONE','')); stream.writeframes(b'\0\0'*30100)
        audio = self.upload('duration-boundary-upload', path)
        source = self.create('duration-boundary-source', audio)
        self.mutate('duration-boundary-analysis-rejected', f"/api/v1/project-sessions/{source['projectSessionId']}/analysis-jobs", {'expectedRevision': source['projectRevision']}, status=413, code='resourceTooLarge')

    def start_server(self, name, controlled=False):
        environment = {**self.environment, 'MAKO_BACKEND_FFMPEG_PATH': str(self.native_launcher) if controlled else '/usr/bin/ffmpeg'}
        log = self.output / (name + '.stdout.log')
        with log.open('wb') as stream:
            self.process = subprocess.Popen(self.server_command + ['--urls','http://127.0.0.1:0'], env=environment, stdout=stream, stderr=subprocess.STDOUT)
        end = time.monotonic() + 35
        while time.monotonic() < end:
            self.capture()
            ready = [v for v in log.read_text().splitlines() if v.startswith('BACKEND_READY ')]
            if ready:
                self.base_url = ready[-1].split()[1]; return
            self.check(self.process.poll() is None, name + ': process alive before readiness')
            time.sleep(.05)
        raise AssertionError(name + ': no bounded readiness')

    def stop_server(self, name):
        self.capture(); self.process.terminate()
        code = self.process.wait(timeout=25)
        self.check(code == 0 and not self.live(), name + ': normal exit0 with every observed owned group member gone')
        self.server_runs.append({'name': name, 'pid': self.process.pid, 'exitCode': code, 'forcedKill': False})

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False); (self.output/'projects').mkdir()
        self.bootstrap = secrets.token_hex(32)
        worker_bytes = ("#!/bin/sh\nexec '" + str(self.worker).replace("'", "'\\''") + "' \"$@\"\n").encode()
        native_bytes = b'#!/bin/sh\nfor arg do\n if [ "$arg" = "-i" ]; then exec /usr/bin/ffmpeg -re "$@"; fi\ndone\nexec /usr/bin/ffmpeg "$@"\n'
        for path, content in [(self.worker_launcher,worker_bytes),(self.native_launcher,native_bytes)]: path.write_bytes(content); path.chmod(0o700)
        self.environment = os.environ.copy()
        for key in ('DISPLAY','WAYLAND_DISPLAY','MAKO_BACKEND_FAKE_MEDIA','MAKO_BACKEND_CREDENTIAL_FILE'): self.environment.pop(key,None)
        self.environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap,MAKO_BACKEND_RESOURCE_ROOT=str(self.output/'resources'),MAKO_BACKEND_STATE_ROOT=str(self.output/'state'),
            MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({'analysis-probe-root':str(self.output/'projects')}),MAKO_BACKEND_CLIENT_TOKEN_SECONDS='3600',MAKO_BACKEND_CLIENT_LEASE_SECONDS='2',
            MAKO_BACKEND_ANALYSIS_TIMEOUT_SECONDS='8',MAKO_BACKEND_WORKER_PATH=str(self.worker_launcher),MAKO_BACKEND_FFPROBE_PATH='/usr/bin/ffprobe')
        failure = None
        try:
            self.start_server('normal-server'); self.normal(); self.restart_control()
            self.input_rejection(); self.controls(); self.quota(); self.publication_failure(); self.lifecycle()
        except Exception as error:
            failure = str(error); (self.output/'failure.txt').write_text(traceback.format_exc())
        finally:
            if self.process and self.process.poll() is None:
                try: self.stop_server('final-shutdown')
                except Exception:
                    self.process.kill(); self.process.wait(timeout=5)
                    self.server_runs.append({'name':'forced-cleanup','pid':self.process.pid,'exitCode':self.process.returncode,'forcedKill':True})
                    if failure is None: failure='Server required forced cleanup'
            for pid in self.live():
                self.signal_owned(pid,signal.SIGKILL)
                if failure is None: failure='Owned native process survived Server shutdown'
            for path in [self.worker_launcher,self.native_launcher,self.output/'resources/bootstrap.cap',self.output/'resources/bootstrap.json']: path.unlink(missing_ok=True)
            scan_start = len(self.assertions)
            for path in self.output.rglob('*'):
                if path.is_file():
                    data=path.read_bytes()
                    self.check(not any(v.encode() in data for v in [self.bootstrap,*self.credentials]),'credential artifact scan: '+str(path.relative_to(self.output)))
            HELPER.save_json(self.output/'manifest.json',{'succeeded':failure is None,'failure':failure,'requestCount':self.request_count,'workflowAssertions':scan_start,
                'credentialScanAssertions':len(self.assertions)-scan_start,'assertionCount':len(self.assertions),'assertions':self.assertions,'results':self.results,'comparisons':self.comparisons,
                'serverRuns':self.server_runs,'serverPid':self.process.pid if self.process else None,'observedProcesses':list(self.processes.values()),'survivingOwnedPids':self.live(),
                'nativeProcessControlObservations':self.native_observations,'workerLauncherSha256':hashlib.sha256(worker_bytes).hexdigest(),'nativeControlLauncherSha256':hashlib.sha256(native_bytes).hexdigest(),
                'gitHeadAtRun':subprocess.run(['git','rev-parse','HEAD'],cwd=ROOT,capture_output=True,text=True,check=True).stdout.strip(),
                'serverSha256':hashlib.sha256(Path(self.server_command[0]).read_bytes()).hexdigest(),'workerSha256':hashlib.sha256(self.worker.read_bytes()).hexdigest(),
                'boundaries':['Normal successful analysis uses actual unthrottled/usr/bin/ffmpeg and independent original analyzer canonical bytes.',
                    'Only secondServer native process-control fixtures prepend-re on actual decode-i; version startup is direct. This is not default production performance evidence.',
                    'Small100Hz/30100frame duration-boundary input is rejected before analysis; no300second48k task is performed.',
                    'No analysis is applied; static rendering profile/algorithm remains unchanged. TTL passage/expired-active-read accounting use separate Host clock tests.',
                    'Previous-host recovery is bootstrap-authorized local profile access; live job/result routes remain owner-only.']})
        if failure: raise SystemExit(failure)
        print(json.dumps({'succeeded':True,'requests':self.request_count,'assertions':len(self.assertions)}))


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--output',type=Path,required=True);parser.add_argument('--server',nargs='+',required=True)
    parser.add_argument('--worker',required=True);parser.add_argument('--oracle',nargs='+',required=True)
    args=parser.parse_args();AnalysisProbe(args.output.resolve(),args.server,args.worker,args.oracle).run()
