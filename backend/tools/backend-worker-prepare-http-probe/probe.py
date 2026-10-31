#!/usr/bin/env python3
"""B4-I prepare delta; accepted HTTP and original waveform oracle are reused."""
import argparse
import copy
import hashlib
import http.client
import importlib.util
import json
import os
import pathlib
import secrets
import signal
import socket
import subprocess
import sys
import threading
import time
import traceback
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('accepted_safe_waveform', ROOT / 'tools/backend-safe-waveform-http-probe/probe.py')
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)
HELPER = BASE.BASE.HELPER
PROFILE = 'self-contained-native-waveform-v1'
FAMILIES = ['wav', 'mp3', 'flac', 'aac', 'mov', 'ogg']


class WorkerPreparationProbe(BASE.WaveformProbe):
    def __init__(self, output, server, worker, oracle, legacy_worker):
        super().__init__(output, server, worker, oracle)
        self.legacy_worker = pathlib.Path(legacy_worker).resolve()
        self.process_lock = threading.RLock()
        self.monitor_errors, self.server_identities, self.control_observations = [], {}, []
        self.invocation_path = output / 'native-invocations.jsonl'
        self.native_tools = output / 'owned-native-tools'
        self.source_start = self.deployment_start = None
        self.prepare_timeout = 8
        self.family_paths, self.recovery_sources, self.recoveries = {}, [], []

    def family(self, family):
        path = super().family(family)
        self.family_paths[family] = path
        return path

    @staticmethod
    def source_hashes():
        paths = subprocess.run(['git', 'ls-files', '-z'], cwd=ROOT, capture_output=True, check=True).stdout.decode().split('\0')
        paths = [path for path in paths if path.startswith(('src/', 'tests/', 'tools/')) or path in
                 ('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json',
                  'MakoKaraGenerator.slnx', 'NuGet.Config')]
        return {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest() for path in paths}

    def deployment_hashes(self):
        roles = {'server': self.server_command[0], 'worker': str(self.worker),
                 'waveformOracle': self.oracle[0], 'legacyWorker': str(self.legacy_worker)}
        return {role: {str(path.relative_to(pathlib.Path(executable).resolve().parent)): hashlib.sha256(path.read_bytes()).hexdigest()
                      for path in sorted(pathlib.Path(executable).resolve().parent.rglob('*')) if path.is_file()}
                for role, executable in roles.items()}

    def verify_previous_deployments(self):
        oracle_manifest = ROOT / 'artifacts/backend-extraction/b4-safe-waveform/http/deployment-final1-manifest.json'
        legacy_manifest = ROOT / 'artifacts/backend-extraction/b4-compressed-export/http/http-final3/deployment-start.json'
        original = json.loads(oracle_manifest.read_text())
        old_worker = json.loads(legacy_manifest.read_text())['worker']
        self.check(self.deployment_start['waveformOracle'] == original['roles']['oracle']['files'],
                   'baseline oracle: all files exactly match the previously published pre-B4-I deployment')
        self.check(self.deployment_start['legacyWorker'] == old_worker,
                   'legacy Worker: all files exactly match the original final B4-H HTTP deployment')
        # Assembly informational/file versions are original metadata strings, not a rebuilt version claim.
        media_bytes = (pathlib.Path(self.oracle[0]).resolve().parent / 'MakoKaraGenerator.Media.dll').read_bytes()
        worker_bytes = (self.legacy_worker.parent / 'MakoKaraGenerator.Backend.Worker.dll').read_bytes()
        self.check(b'0.70.1.0' in media_bytes and b'0.70.1' in media_bytes, 'baseline oracle: original Media version0.70.1')
        self.check(b'0.71.0.0' in worker_bytes and b'0.71.0' in worker_bytes, 'legacy Worker: original version0.71.0')
        value = {'oracleOriginalManifest': str(oracle_manifest), 'oracleOriginalManifestSha256': hashlib.sha256(oracle_manifest.read_bytes()).hexdigest(),
            'oracleProductEndpoint': original['productEndpoint'], 'oracleToolEndpoint': original['toolEndpoint'],
            'oracleMediaVersion': '0.70.1', 'oracleFileCount': len(self.deployment_start['waveformOracle']),
            'legacyOriginalManifest': str(legacy_manifest), 'legacyOriginalManifestSha256': hashlib.sha256(legacy_manifest.read_bytes()).hexdigest(),
            'legacyWorkerVersion': '0.71.0', 'legacyWorkerFileCount': len(old_worker), 'originalFilesAllExact': True}
        HELPER.save_json(self.output / 'previous-deployments-provenance.json', value)
        return value

    def wrappers(self):
        self.native_tools.mkdir()
        for tool in ('ffmpeg', 'ffprobe'):
            path = self.native_tools / tool
            path.write_text('#!/usr/bin/python3\nimport json,os,sys\n'
                'def identity(pid):\n'
                ' try:\n'
                '  p=open("/proc/"+str(pid)+"/stat").read().rsplit(")",1)[1].split()\n'
                '  return dict(pid=pid,parent=int(p[1]),group=int(p[2]),startTicks=p[19],state=p[0])\n'
                ' except (OSError,IndexError): return None\n'
                'args=sys.argv[1:]\nname=' + repr(tool) + '\n'
                'controlled=name=="ffmpeg" and "-i" in args and "pcm_f32le" in args and os.path.exists(' + repr(str(self.marker)) + ')\n'
                'record=dict(identity=identity(os.getpid()),parentIdentity=identity(os.getppid()),tool=name,originalArguments=args,injectedReadRate=controlled)\n'
                'try: record["parentIsWorker"]=b"MakoKaraGenerator.Backend.Worker" in open("/proc/"+str(os.getppid())+"/cmdline","rb").read()\n'
                'except OSError: record["parentIsWorker"]=False\n'
                'with open(' + repr(str(self.invocation_path)) + ',"a") as target: target.write(json.dumps(record)+"\\n")\n'
                'os.execv("/usr/bin/"+name,["/usr/bin/"+name]+(["-re"] if controlled else [])+args)\n')
            path.chmod(0o700)

    def invocations(self):
        if not self.invocation_path.exists(): return []
        values = []
        for line in self.invocation_path.read_text().splitlines():
            try: values.append(json.loads(line))
            except json.JSONDecodeError: pass  # A monitor can observe an unfinished single append.
        return values

    def capture(self):
        if self.process is None: return
        entries = []
        for path in pathlib.Path('/proc').iterdir():
            if path.name.isdigit() and (value := self.identity(int(path.name))): entries.append(value)
        with self.process_lock:
            groups = {value['group'] for value in self.processes.values()}
            for value in entries:
                if value['parent'] != self.process.pid or value['group'] != value['pid']: continue
                try: command = pathlib.Path('/proc/' + str(value['pid']) + '/cmdline').read_bytes()
                except (FileNotFoundError, ProcessLookupError): continue
                if b'MakoKaraGenerator.Backend.Worker' in command:
                    self.remember(value); groups.add(value['group'])
            for invocation in self.invocations():
                parent, child = invocation.get('parentIdentity'), invocation.get('identity')
                if invocation.get('parentIsWorker') and parent and parent['parent'] in self.server_identities and parent['pid'] == parent['group']:
                    self.remember(parent); groups.add(parent['group'])
                    if child and child['group'] == parent['group']: self.remember(child)
            for value in entries:
                if value['group'] in groups: self.remember(value)

    def remember(self, value):
        prior = self.processes.get(value['pid'])
        if prior is not None and prior['startTicks'] != value['startTicks']:
            message = 'Observed private-group PID reused: ' + str(value['pid'])
            if message not in self.monitor_errors: self.monitor_errors.append(message)
            return
        self.processes[value['pid']] = value

    def live(self):
        with self.process_lock:
            return [pid for pid, old in self.processes.items()
                    if (now := self.identity(pid)) and now['startTicks'] == old['startTicks']]

    def watch(self):
        while not self.monitor_stop.is_set():
            try:
                self.capture()
                for pid in self.live():
                    try:
                        for fd in pathlib.Path('/proc/' + str(pid) + '/fd').iterdir():
                            try: target = os.readlink(fd)
                            except OSError: continue
                            if target.endswith('/owned-sentinel.wav'):
                                self.sentinel_reads.append({'identity': self.identity(pid), 'openedPath': target})
                    except (FileNotFoundError, ProcessLookupError): pass
            except Exception as error:
                self.monitor_errors.append(type(error).__name__ + ': ' + str(error))
                return
            self.monitor_stop.wait(.003)

    def start_server(self, name, controlled=False):
        self.environment.update(MAKO_BACKEND_AUDIO_PREPARATION_TIMEOUT_SECONDS=str(self.prepare_timeout),
            MAKO_BACKEND_FFMPEG_PATH=str(self.native_tools / 'ffmpeg'), MAKO_BACKEND_FFPROBE_PATH=str(self.native_tools / 'ffprobe'))
        log = self.output / (name + '.stdout.log')
        with log.open('wb') as target:
            self.process = subprocess.Popen(self.server_command + ['--urls', 'http://127.0.0.1:0'],
                env=self.environment, stdout=target, stderr=subprocess.STDOUT)
        self.server_identities[self.process.pid] = self.identity(self.process.pid)
        self.monitor_stop = threading.Event()
        self.monitor = threading.Thread(target=self.watch, daemon=True)
        self.monitor.start()
        end = time.monotonic() + 35
        while time.monotonic() < end:
            ready = [line for line in log.read_text().splitlines() if line.startswith('BACKEND_READY ')]
            if ready:
                self.base_url = ready[-1].split()[1]; return
            self.check(self.process.poll() is None, name + ': process alive before readiness')
            time.sleep(.05)
        raise AssertionError(name + ': no bounded readiness')

    def stop_server(self, name):
        old = self.server_identities[self.process.pid]
        now = self.identity(self.process.pid)
        self.check(now is not None and now['startTicks'] == old['startTicks'], name + ': exact owned Server identity verified')
        BASE.BASE.AdoptionProbe.stop_server(self, name)
        self.monitor_stop.set(); self.monitor.join(timeout=3); self.capture()
        self.server_runs[-1]['startTicks'] = old['startTicks']
        self.check(not self.monitor_errors, name + ': continuous private-group monitor succeeded')

    def capabilities(self, name, enabled=True):
        value = self.request(name, 'GET', '/api/v1/capabilities')['audioPreparation']
        expected = {'enabled': enabled, 'profile': PROFILE, 'families': FAMILIES,
            'maxPayloadBytes': 4 * 1024 * 1024, 'maxBaseBuckets': 500000, 'baseBucketSamples': 64,
            'operationTimeoutSeconds': self.prepare_timeout, 'cancellationGraceSeconds': 2,
            'maxRunning': 1, 'maxRunningPerClient': 1, 'maxQueued': 4, 'maxQueuedPerClient': 4}
        for key, wanted in expected.items(): self.check(value[key] == wanted, name + ': exact ' + key)
        self.check(bool(value.get('disabledReason')) == (not enabled), name + ': explicit disabled reason only when disabled')
        return value

    def wait_job(self, name, job, expected='succeeded'):
        terminal = super().wait_job(name, job, expected)
        self.capture()
        self.check(not self.live(), name + ': all observed private-group identities gone at authoritative terminal')
        return terminal

    def native_active(self, name, job=None):
        end, polls = time.monotonic() + 5, 0
        while time.monotonic() < end:
            self.capture(); polls += 1
            if job is not None:
                state = self.request(name + '-active-' + str(polls), 'GET', '/api/v1/jobs/' + job['jobId'])
                self.check(state['state'] not in ('succeeded', 'failed', 'cancelled'), name + ': native observed before terminal')
            for pid in self.live():
                try: args = pathlib.Path('/proc/' + str(pid) + '/cmdline').read_bytes().split(b'\0')
                except (FileNotFoundError, ProcessLookupError): continue
                if b'pcm_f32le' not in args or b'-re' not in args: continue
                with self.process_lock: identity = copy.deepcopy(self.processes[pid])
                self.check(args[0] == b'/usr/bin/ffmpeg' and args[args.index(b'-f') + 1] == b'wav'
                    and args[args.index(b'-protocol_whitelist') + 1] == b'file,pipe' and b'-ar' not in args,
                    name + ': actual typed WAV/file,pipe decoder preserves native source rate')
                self.check(identity['group'] != self.process.pid and identity['group'] in self.processes
                    and identity['parent'] == identity['group'], name + ': native belongs to exact private Worker group')
                return identity, [arg.decode() for arg in args if arg]
            time.sleep(.005)
        raise AssertionError(name + ': no actual controlled owned waveform decoder')

    def candidate_pins(self):
        return {str(path.relative_to(self.output / 'state')) for path in (self.output / 'state').glob('sessions/*/loaded-input')}

    def healthy(self, name):
        self.check(self.request(name, 'GET', '/health/live', client=None, host=None)['hostInstanceId'] == self.host,
                   name + ': same Server continues healthy')

    def open_control(self, kind):
        before, pins = self.sessions(kind + '-before-sessions'), self.candidate_pins()
        self.marker.write_text('one owned controlled WAV preparation')
        try:
            accepted = self.mutate(kind + '-accept', '/api/v1/project-sessions/open-jobs',
                {'projectDirectoryHandleId': self.wave_handle['handleId']}, status=202)
            native, argv = self.native_active(kind, accepted)
            with self.process_lock: leader = copy.deepcopy(self.processes[native['group']])
            self.healthy(kind + '-active-health')
            if kind == 'cancel': self.cancel(kind, accepted)
            elif kind == 'deadline':
                self.signal_owned(native['pid'], signal.SIGSTOP); self.signal_owned(leader['pid'], signal.SIGSTOP)
            else: self.signal_owned(leader['pid'], signal.SIGKILL)
            terminal = self.wait_job(kind, accepted, 'cancelled' if kind == 'cancel' else 'failed')
            if kind != 'cancel':
                self.check(terminal['error']['code'] == 'taskExecutionFailed', kind + ': transport/timeout is execution failure')
                self.check(terminal['failureCode'] == ('WorkerAudioPreparationTimedOut' if kind == 'deadline' else 'WorkerAudioPreparationTransportFailed'),
                           kind + ': exact stable failure code')
                self.check(not terminal['cancellationRequested'], kind + ': failure differs from explicit cancellation')
            self.check('result' not in terminal and self.sessions(kind + '-after-sessions') == before,
                       kind + ': no partial published session')
            self.check(self.candidate_pins() == pins, kind + ': owned candidate pinned closure released')
            self.control_observations.append({'kind': kind, 'nativeIdentity': native, 'workerIdentity': leader,
                'argv': argv, 'jobId': terminal['jobId'], 'terminal': terminal, 'controlledOwnedReadRate': True})
        finally: self.marker.unlink(missing_ok=True)
        reopened = self.open(kind + '-same-handle-retry', self.wave_handle)
        self.check(not reopened['isDirty'] and reopened['projectRevision'] == '0', kind + ': same handle lease and preparation restored')
        self.close(kind + '-retry-close', reopened); self.healthy(kind + '-continued-health')

    def cancel_open(self):
        for kind in ('cancel', 'deadline', 'worker-kill'): self.open_control(kind)
        self.create_disconnect()
        self.create_deadline()
        self.queue_controls()

    def create_disconnect(self):
        before = self.sessions('disconnect-before-sessions')
        audio = self.upload('disconnect-audio-upload', self.output / 'wav-source.wav')
        receipt = self.metadata('create-disconnect-receipt')
        value = {'name': 'create-disconnect', 'audioAssetId': audio['assetId'], 'lyricsAssetId': self.lyrics['assetId'],
                 'importerId': 'lrc.enhanced', 'metadata': receipt}
        url = urllib.parse.urlsplit(self.base_url)
        connection = http.client.HTTPConnection(url.hostname, url.port, timeout=10)
        self.request_count += 1
        evidence = self.output / (str(self.request_count).zfill(3) + '-create-disconnect.json')
        self.marker.write_text('one owned create accepted before HTTP disconnect')
        try:
            connection.request('POST', '/api/v1/project-sessions', body=json.dumps(value).encode(), headers={
                'Content-Type': 'application/json', 'Authorization': 'Bearer ' + self.tokens[self.client],
                'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host})
            native, argv = self.native_active('disconnect-create')
            # Body has been accepted and actual native work has begun; close before any response read.
            connection.sock.shutdown(socket.SHUT_RDWR); connection.close()
            HELPER.save_json(evidence, {'request': self.redact(value), 'actualNativeIdentity': native,
                'argv': argv, 'responseBodyConsumed': False, 'disconnectedAfterActualPreparationBegan': True})
            retry = self.mutate('disconnect-create-same-receipt', '/api/v1/project-sessions',
                {key: item for key, item in value.items() if key != 'metadata'}, metadata=receipt, status=201)
            self.check(retry['projectRevision'] == '0' and retry['isDirty'] and not retry['hasPath'],
                       'create disconnect: original accepted receipt returns revision0 unsaved session')
            sessions = self.sessions('disconnect-after-sessions')
            self.check(len(sessions) == len(before) + 1 and sum(item['projectSessionId'] == retry['projectSessionId'] for item in sessions) == 1,
                       'create disconnect: exactly one published session and no cancellation')
            self.capture(); self.check(not self.live(), 'create disconnect: accepted preparation drained before receipt success')
            self.close('disconnect-create-close', retry)
            self.control_observations.append({'kind': 'createDisconnectContinues', 'nativeIdentity': native,
                'receipt': receipt, 'session': retry, 'controlledOwnedReadRate': True})
        finally:
            connection.close(); self.marker.unlink(missing_ok=True)

    def create_deadline(self):
        before = self.sessions('create-deadline-before-sessions')
        audio = self.upload('create-deadline-audio-upload', self.output / 'wav-source.wav')
        value = {'name': 'create-deadline', 'audioAssetId': audio['assetId'], 'lyricsAssetId': self.lyrics['assetId'],
                 'importerId': 'lrc.enhanced', 'metadata': self.metadata('create-deadline-receipt')}
        url = urllib.parse.urlsplit(self.base_url)
        connection = http.client.HTTPConnection(url.hostname, url.port, timeout=20)
        self.request_count += 1
        prefix = str(self.request_count).zfill(3) + '-create-deadline'
        self.marker.write_text('one owned synchronous create deadline')
        try:
            connection.request('POST', '/api/v1/project-sessions', body=json.dumps(value).encode(), headers={
                'Content-Type': 'application/json', 'Authorization': 'Bearer ' + self.tokens[self.client],
                'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host})
            native, argv = self.native_active('create-deadline')
            with self.process_lock: leader = copy.deepcopy(self.processes[native['group']])
            self.signal_owned(native['pid'], signal.SIGSTOP); self.signal_owned(leader['pid'], signal.SIGSTOP)
            response = connection.getresponse(); raw = response.read(); body = json.loads(raw)
            HELPER.save_json(self.output / (prefix + '.request.json'), {'method': 'POST', 'path': '/api/v1/project-sessions', 'json': value})
            HELPER.save_json(self.output / (prefix + '.response.json'), {'status': response.status,
                'headers': dict(response.getheaders()), 'json': self.redact(body)})
            self.check(response.status == 500 and response.getheader('Content-Type').split(';')[0] == 'application/problem+json',
                       'synchronous create deadline: HTTP500 structured error')
            self.check(body['code'] == 'taskExecutionFailed' and body['failureCode'] == 'WorkerAudioPreparationTimedOut',
                       'synchronous create deadline: exact public stable timeout failure')
            self.capture(); self.check(not self.live(), 'synchronous create deadline: entire observed private group drained before response')
            self.check(self.sessions('create-deadline-after-sessions') == before, 'synchronous create deadline: no partial session')
            self.control_observations.append({'kind': 'synchronousCreateDeadline', 'nativeIdentity': native,
                'workerIdentity': leader, 'argv': argv, 'error': body, 'controlledOwnedReadRate': True})
        finally:
            connection.close(); self.marker.unlink(missing_ok=True)
        retry = self.create('create-deadline-same-asset-retry', audio)
        self.check(retry['projectRevision'] == '0' and retry['isDirty'], 'synchronous create deadline: same asset retries successfully')
        self.close('create-deadline-retry-close', retry); self.healthy('create-deadline-continued-health')

    def queue_controls(self):
        before, pins = self.sessions('queue-before-sessions'), self.candidate_pins()
        audio = self.upload('queue-audio-upload', self.output / 'wav-source.wav')
        connections = []
        self.marker.write_text('one owned queue blocker')
        try:
            blocker = self.mutate('queue-blocker-accept', '/api/v1/project-sessions/open-jobs',
                {'projectDirectoryHandleId': self.wave_handle['handleId'], 'access': 'read'}, status=202)
            native, argv = self.native_active('queue-blocker', blocker)
            with self.process_lock: leader = copy.deepcopy(self.processes[native['group']])
            self.signal_owned(native['pid'], signal.SIGSTOP); self.signal_owned(leader['pid'], signal.SIGSTOP)
            invocation_offset = len(self.invocations())
            queued = self.mutate('queue-open-cancel-accept', '/api/v1/project-sessions/open-jobs',
                {'projectDirectoryHandleId': self.wave_handle['handleId'], 'access': 'read'}, status=202)
            end = time.monotonic() + 3
            while len(self.candidate_pins()) < len(pins) + 2 and time.monotonic() < end: time.sleep(.005)
            self.check(len(self.candidate_pins()) == len(pins) + 2, 'queued open: fixed candidate entered behind active preparation')
            self.cancel('queue-open-explicit-cancel', queued)
            # A queued cancellation owns no Worker; the unrelated blocked group is expected to remain alive.
            cancelled = BASE.BASE.AdoptionProbe.wait_job(self, 'queue-open-cancelled', queued, 'cancelled')
            self.check('result' not in cancelled and self.identity(leader['pid']) is not None,
                       'queued open cancellation: no result and active preparation remains independently owned')
            self.check(len(self.candidate_pins()) == len(pins) + 1, 'queued open cancellation: only its candidate closure released')
            url = urllib.parse.urlsplit(self.base_url)
            for index in range(4):
                value = {'name': 'queued-create-' + str(index), 'audioAssetId': audio['assetId'], 'lyricsAssetId': self.lyrics['assetId'],
                         'metadata': self.metadata('queued-create-receipt-' + str(index))}
                connection = http.client.HTTPConnection(url.hostname, url.port, timeout=20)
                connection.request('POST', '/api/v1/project-sessions', body=json.dumps(value).encode(), headers={
                    'Content-Type': 'application/json', 'Authorization': 'Bearer ' + self.tokens[self.client],
                    'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host})
                self.request_count += 1
                prefix = str(self.request_count).zfill(3) + '-queued-create-' + str(index)
                HELPER.save_json(self.output / (prefix + '.request.json'), {'method': 'POST', 'path': '/api/v1/project-sessions', 'json': value})
                connections.append((connection, prefix))
            # The following request is serialized through Kestrel after four complete small bodies.
            # A quota rejection proves the four internal queue reservations have been accepted.
            time.sleep(.1)
            self.mutate('prepare-queue-over-quota', '/api/v1/project-sessions',
                {'name': 'over-prepare-queue-quota', 'audioAssetId': audio['assetId'], 'lyricsAssetId': self.lyrics['assetId']},
                status=429, code='requestQuotaExceeded')
            self.healthy('queue-blocked-continued-health')
            errors = []
            for connection, prefix in connections:
                response = connection.getresponse(); body = json.loads(response.read())
                HELPER.save_json(self.output / (prefix + '.response.json'), {'status': response.status,
                    'headers': dict(response.getheaders()), 'json': self.redact(body)})
                self.check(response.status == 500 and body['code'] == 'taskExecutionFailed'
                    and body['failureCode'] == 'WorkerAudioPreparationTimedOut',
                    prefix + ': preparation queue consumes the same bounded deadline')
                errors.append(body); connection.close()
            terminal = self.wait_job('queue-blocker-timeout', blocker, 'failed')
            self.check(terminal['failureCode'] == 'WorkerAudioPreparationTimedOut' and not terminal['cancellationRequested'],
                       'queue blocker: active timeout remains failure, not explicit cancellation')
            self.check(not any('-i' in item['originalArguments'] for item in self.invocations()[invocation_offset:]),
                       'queued cancellation/deadlines: no queued preparation decoder starts')
            self.check(self.sessions('queue-after-sessions') == before and self.candidate_pins() == pins,
                       'queue controls: no partial sessions and all candidate pinned closures released')
            self.control_observations.append({'kind': 'queuedCancelDeadlineQuota', 'nativeIdentity': native,
                'workerIdentity': leader, 'argv': argv, 'activeTerminal': terminal, 'queuedCancelledTerminal': cancelled,
                'queuedCreateErrors': errors, 'controlledOwnedReadRate': True})
        finally:
            for connection, _ in connections: connection.close()
            self.marker.unlink(missing_ok=True)
        reopened = self.open('queue-controls-same-handle-retry', self.wave_handle)
        self.close('queue-controls-retry-close', reopened)

    def normal(self):
        # Existing six-family workflow/negative fixture calls our Worker controls.
        super().normal()
        self.capabilities('current-prepare-capabilities')
        invocations = self.invocations()
        media = [value for value in invocations if '-i' in value['originalArguments']]
        self.check(media and all(value['parentIsWorker'] and value['identity']['group'] == value['parentIdentity']['pid']
            and value['parentIdentity']['group'] == value['parentIdentity']['pid'] for value in media),
            'all actual prepare decodes run in managed private Worker groups without Host native fallback')
        for comparison in self.comparisons:
            family = comparison['family']
            asset = self.upload('recovery-' + family + '-upload', self.family_paths[family])
            source = self.create('recovery-' + family, asset)
            self.check(source['isDirty'] and not source['hasPath'], family + ': recovery seed is an actual unsaved session')
            self.recovery_sources.append({'family': family, 'name': source['name'],
                'projectSessionId': source['projectSessionId'], 'oracle': comparison['oracle']})

    def recover_families(self):
        candidates = self.request('six-family-recovery-candidates', 'GET', '/api/v1/recovery-candidates')['candidates']
        for source in self.recovery_sources:
            family = source['family']
            matching = [item for item in candidates if item['projectName'] == source['name']]
            self.check(len(matching) == 1 and not matching[0]['hasOriginalPath'],
                       family + ': actual shutdown autosave exposes one unsaved recovery marker')
            candidate = matching[0]
            accepted = self.mutate('recover-' + family + '-accept', '/api/v1/project-sessions/recover-jobs',
                {'recoveryId': candidate['recoveryId']}, status=202)
            terminal = self.wait_job('recover-' + family, accepted)
            recovered = copy.deepcopy(terminal['result']); state = self.state('recover-' + family + '-state', recovered)
            self.check(terminal['kind'] == 'recover' and recovered['audioDurationUs'] == source['oracle']['durationUs']
                and recovered['isDirty'] and not recovered['hasPath'] and recovered['projectRevision'] == '0'
                and state['history']['undoCount'] == state['history']['redoCount'] == 0,
                family + ': actual recover prepares original source duration with dirty revision0 and fresh history')
            copied = list((self.output / 'resources' / self.client).glob(recovered['audioAssetId'] + '.*'))
            self.check(len(copied) == 1 and copied[0].read_bytes() == self.family_paths[family].read_bytes(),
                       family + ': recovered imported asset preserves complete identical encoded source bytes')
            remaining = self.request('recover-' + family + '-remaining', 'GET', '/api/v1/recovery-candidates')['candidates']
            self.check(not any(item['recoveryId'] == candidate['recoveryId'] for item in remaining),
                       family + ': successful recovery consumes its original autosave marker')
            self.recoveries.append({'family': family, 'candidate': candidate, 'terminal': terminal,
                'originalProjectSessionId': source['projectSessionId'], 'originalDurationUs': source['oracle']['durationUs'],
                'encodedSourceSha256': hashlib.sha256(self.family_paths[family].read_bytes()).hexdigest()})
            self.close('recover-' + family + '-close', recovered)
        self.check(not self.candidate_pins(), 'six-family recovery: all recovered session pinned closures released after close')

    def restart(self):
        first_host = self.host
        self.stop_server('current-normal-shutdown')
        self.start_server('current-recovery-server')
        self.host = self.request('six-family-recovery-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.check(self.host != first_host, 'six-family recovery: actual new Server host identity')
        self.register('six-family-recovery-owner'); self.capabilities('six-family-recovery-enabled')
        self.recover_families(); self.stop_server('recovery-normal-shutdown')
        original_launcher = self.worker_launcher.read_bytes()
        self.worker_launcher.write_text('#!/bin/sh\nexec ' + "'" + str(self.legacy_worker).replace("'", "'\\''") + "' \"$@\"\n")
        self.worker_launcher.chmod(0o700)
        try:
            self.start_server('actual-old071-worker-server')
            self.host = self.request('legacy-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
            self.register('legacy-prepare-owner'); self.capabilities('actual-old071-prepare-disabled', enabled=False)
            self.lyrics = self.lyrics_upload('legacy-lyrics-upload')
            audio = self.upload('legacy-audio-upload', self.output / 'wav-source.wav')
            before, offset = self.sessions('legacy-before-sessions'), len(self.invocations())
            self.mutate('legacy-create-no-fallback', '/api/v1/project-sessions',
                {'name': 'legacy-disabled-create', 'audioAssetId': audio['assetId'], 'lyricsAssetId': self.lyrics['assetId']},
                status=503, code='taskUnavailable')
            handle = self.mutate('legacy-current-package-handle', '/api/v1/file-handles',
                {'rootGrantId': 'adoption-probe-root', 'relativePath': 'wav-package'}, status=201)
            accepted = self.mutate('legacy-open-no-fallback', '/api/v1/project-sessions/open-jobs',
                {'projectDirectoryHandleId': handle['handleId']}, status=202)
            terminal = self.wait_job('legacy-open', accepted, 'failed')
            self.check(terminal['error']['code'] == 'taskUnavailable' and 'result' not in terminal,
                       'old Worker: original open job fails unavailable without partial session')
            self.check(self.sessions('legacy-after-sessions') == before and not self.candidate_pins(),
                       'old Worker: candidate sessions and pinned closures absent')
            self.check(not any('-i' in value['originalArguments'] for value in self.invocations()[offset:]),
                       'old Worker: no Host native decode fallback')
            self.healthy('legacy-disabled-continued-health'); self.stop_server('legacy-normal-shutdown')
        finally:
            self.worker_launcher.write_bytes(original_launcher); self.worker_launcher.chmod(0o700)
        self.start_server('current-after-legacy-server')
        self.host = self.request('current-after-legacy-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.register('current-after-legacy-owner'); self.capabilities('current-after-legacy-enabled')
        handle = self.mutate('current-after-legacy-package-handle', '/api/v1/file-handles',
            {'rootGrantId': 'adoption-probe-root', 'relativePath': 'wav-package'}, status=201)
        opened = self.open('current-after-legacy-open-retry', handle)
        self.check(opened['audioDurationUs'] == self.comparisons[0]['oracle']['durationUs'] and not opened['isDirty'],
                   'current Worker: same saved package retries with exact original duration')
        self.close('current-after-legacy-close', opened)
        self.shutdown_active()

    def shutdown_active(self):
        self.marker.write_text('one owned active shutdown preparation')
        try:
            pins = self.candidate_pins()
            handle = self.mutate('shutdown-package-handle', '/api/v1/file-handles',
                {'rootGrantId': 'adoption-probe-root', 'relativePath': 'wav-package'}, status=201)
            accepted = self.mutate('shutdown-active-open', '/api/v1/project-sessions/open-jobs',
                {'projectDirectoryHandleId': handle['handleId']}, status=202)
            native, argv = self.native_active('shutdown-active', accepted)
            with self.process_lock: leader = copy.deepcopy(self.processes[native['group']])
            self.signal_owned(native['pid'], signal.SIGSTOP); self.signal_owned(leader['pid'], signal.SIGSTOP)
            invocation_offset = len(self.invocations())
            queued = self.mutate('shutdown-queued-open', '/api/v1/project-sessions/open-jobs',
                {'projectDirectoryHandleId': handle['handleId'], 'access': 'read'}, status=202)
            end = time.monotonic() + 3
            while len(self.candidate_pins()) < len(pins) + 2 and time.monotonic() < end: time.sleep(.005)
            self.check(len(self.candidate_pins()) == len(pins) + 2,
                       'shutdown: actual queued candidate entered behind stopped active preparation')
            self.stop_server('active-prepare-normal-shutdown')
            raw = (self.output / 'state/jobs' / (accepted['jobId'] + '.json')).read_bytes()
            ledger = json.loads(raw)
            self.check(ledger['Job']['State'] == 'cancelled' and ledger['Job']['Result'] is None,
                       'shutdown: original owned open ledger records cancellation without partial session')
            self.check(not self.candidate_pins(), 'shutdown: owned candidate pinned closure released')
            (self.output / 'shutdown-original-ledger.json').write_bytes(raw)
            queued_raw = (self.output / 'state/jobs' / (queued['jobId'] + '.json')).read_bytes()
            queued_ledger = json.loads(queued_raw)
            self.check(queued_ledger['Job']['State'] == 'cancelled' and queued_ledger['Job']['Result'] is None,
                       'shutdown: original queued open ledger records cancellation without partial session')
            self.check(not any('-i' in item['originalArguments'] for item in self.invocations()[invocation_offset:]),
                       'shutdown: queued preparation launches no decoder')
            (self.output / 'shutdown-queued-original-ledger.json').write_bytes(queued_raw)
            self.control_observations.append({'kind': 'activeAndQueuedShutdown', 'nativeIdentity': native,
                'workerIdentity': leader, 'argv': argv, 'originalLedgerEntry': ledger,
                'queuedOriginalLedgerEntry': queued_ledger, 'controlledOwnedReadRate': True})
        finally: self.marker.unlink(missing_ok=True)

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False); (self.output / 'projects').mkdir()
        self.bootstrap = secrets.token_hex(32)
        launcher = ('#!/bin/sh\nexec ' + "'" + str(self.worker).replace("'", "'\\''") + "' \"$@\"\n").encode()
        self.worker_launcher.write_bytes(launcher); self.worker_launcher.chmod(0o700); self.wrappers()
        self.environment = os.environ.copy()
        for key in ('DISPLAY', 'WAYLAND_DISPLAY', 'MAKO_BACKEND_FAKE_MEDIA', 'MAKO_BACKEND_CREDENTIAL_FILE'):
            self.environment.pop(key, None)
        self.environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap, MAKO_BACKEND_RESOURCE_ROOT=str(self.output / 'resources'),
            MAKO_BACKEND_STATE_ROOT=str(self.output / 'state'), MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({'adoption-probe-root': str(self.output / 'projects')}),
            MAKO_BACKEND_CLIENT_TOKEN_SECONDS='3600', MAKO_BACKEND_CLIENT_LEASE_SECONDS='3600', MAKO_BACKEND_WORKER_PATH=str(self.worker_launcher))
        self.source_start, self.deployment_start = self.source_hashes(), self.deployment_hashes()
        HELPER.save_json(self.output / 'source-start.json', self.source_start)
        HELPER.save_json(self.output / 'deployment-start.json', self.deployment_start)
        failure = None
        previous_provenance = None
        try:
            dirty = subprocess.run(['git', 'diff', '--name-only', 'HEAD', '--', 'src', 'tests', 'tools', 'Directory.Build.props'],
                                   cwd=ROOT, capture_output=True, text=True, check=True).stdout
            self.check(not dirty.strip(), 'formal run: production and acceptance sources frozen at committed endpoint')
            previous_provenance = self.verify_previous_deployments()
            self.start_server('current-server'); self.normal(); self.restart()
        except Exception as error:
            failure = str(error); (self.output / 'failure.txt').write_text(traceback.format_exc())
        finally:
            if self.process and self.process.poll() is None:
                try: self.stop_server('final-normal-shutdown')
                except Exception:
                    self.process.kill(); self.process.wait(timeout=5)
                    self.server_runs.append({'name': 'forced-cleanup', 'pid': self.process.pid, 'exitCode': self.process.returncode, 'forcedKill': True})
                    if failure is None: failure = 'Server required forced cleanup'
            self.monitor_stop.set()
            if hasattr(self, 'monitor'): self.monitor.join(timeout=3)
            self.capture()
            survivors = self.live()
            for pid in survivors:
                self.signal_owned(pid, signal.SIGKILL)
                if failure is None: failure = 'Owned process survived normal Server shutdown'
            for path in (self.worker_launcher, self.output / 'resources/bootstrap.cap', self.output / 'resources/bootstrap.json'):
                path.unlink(missing_ok=True)
            source_final, deployment_final = self.source_hashes(), self.deployment_hashes()
            HELPER.save_json(self.output / 'source-final.json', source_final)
            HELPER.save_json(self.output / 'deployment-final.json', deployment_final)
            if source_final != self.source_start or deployment_final != self.deployment_start:
                failure = failure or 'Source or deployment files drifted during actual HTTP run'
            if self.monitor_errors: failure = failure or 'Private-group monitor failed: ' + '; '.join(self.monitor_errors)
            workflow = len(self.assertions)
            for path in self.output.rglob('*'):
                if path.is_file():
                    data = path.read_bytes()
                    try:
                        self.check(not any(value.encode() in data for value in (self.bootstrap, *self.credentials)),
                                   'credential artifact scan: ' + str(path.relative_to(self.output)))
                    except AssertionError as error: failure = failure or str(error)
            identities = [dict(old, gone=not (now := self.identity(old['pid'])) or now['startTicks'] != old['startTicks'])
                          for old in self.processes.values()]
            HELPER.save_json(self.output / 'process-evidence.json', {'observedPrivateGroupMembers': identities,
                'serverRuns': self.server_runs, 'nativeInvocations': self.invocations(), 'sentinelReads': self.sentinel_reads,
                'monitorErrors': self.monitor_errors, 'survivingOwnedPidsBeforeCleanup': survivors})
            manifest = {'succeeded': failure is None, 'failure': failure, 'requestCount': self.request_count,
                'workflowAssertions': workflow, 'credentialScanAssertions': len(self.assertions) - workflow,
                'assertionCount': len(self.assertions), 'assertions': self.assertions, 'results': self.results,
                'comparisons': self.comparisons, 'adoptions': self.adoptions, 'controls': self.control_observations,
                'recoveries': self.recoveries,
                'serverRuns': self.server_runs, 'observedProcesses': identities, 'survivingOwnedPids': self.live(),
                'gitHeadAtRun': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip(),
                'sourceFilesUnchanged': source_final == self.source_start, 'deploymentFilesUnchanged': deployment_final == self.deployment_start,
                'previousDeploymentsProvenance': previous_provenance,
                'workerLauncherSha256': hashlib.sha256(launcher).hexdigest(),
                'boundaries': ['Independent B4-I delta; older acceptance counts and original failures are preserved separately.',
                    'Six real families reuse the original default waveform loader on identical encoded bytes; HTTP proves exact duration and state only.',
                    'Six real unsaved sessions produce original shutdown autosaves; a new Host recovers each through its actual marker and retains complete encoded bytes.',
                    'Complete info/sample count/base float bits/full pyramid equality belongs to actual Worker author regressions, not a new waveform HTTP route.',
                    'Marker-only owned WAV decodes are rate-controlled for actual process lifecycle observations; normal workflows are unthrottled.',
                    'Synchronous create disconnect preserves the accepted receipt operation; it is not a cancellation request or a new job.',
                    'Actual old0.71 Worker disables preparation without a Host native fallback; same current-version package retries with current Worker.',
                    'Owned same-client ffconcat sentinel fixtures do not claim arbitrary or cross-client path isolation.',
                    'No GUI/screenshots/full suite/performance/long media/manual platform acceptance.']}
            HELPER.save_json(self.output / 'manifest.json', manifest)
            evidence = {str(path.relative_to(self.output)): hashlib.sha256(path.read_bytes()).hexdigest()
                        for path in sorted(self.output.rglob('*')) if path.is_file()}
            HELPER.save_json(self.output / 'evidence-sha256.json', evidence)
        if failure: raise SystemExit(failure)
        print(json.dumps({'succeeded': True, 'requests': self.request_count, 'assertions': len(self.assertions)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--server', nargs='+', required=True)
    parser.add_argument('--worker', required=True)
    parser.add_argument('--oracle', nargs='+', required=True)
    parser.add_argument('--legacy-worker', required=True)
    args = parser.parse_args()
    WorkerPreparationProbe(args.output.resolve(), args.server, args.worker, args.oracle, args.legacy_worker).run()
