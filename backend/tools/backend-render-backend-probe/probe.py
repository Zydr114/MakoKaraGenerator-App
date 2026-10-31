#!/usr/bin/env python3
"""Fresh authenticated HTTP acceptance for explicit CPU/GPU selection on Linux."""
import argparse
import base64
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import queue
import signal
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from urllib.parse import urlencode

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent
LOCK = '/tmp/mkg-gpu-render-dotnet.lock'


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


completion = module('selection_completion', ROOT / 'tools/backend-api-completion-probe/probe.py')
gpu = module('selection_gpu', ROOT / 'tools/backend-gpu-render-probe/probe.py')
host = module('selection_host', HERE / 'host.py').host
save_json = completion.save_json
sha = gpu.sha
process_identity = gpu.process_identity


class ResponseFingerprints:
    """Retain only response byte hashes, never credential-bearing raw JSON."""
    def __init__(self, opener):
        self.opener = opener
        self.last = None

    def open(self, *args, **kwargs):
        try:
            response = self.opener.open(*args, **kwargs)
        except urllib.error.HTTPError as error:
            response = error
        owner = self
        class Response:
            def __enter__(self):
                response.__enter__()
                return self
            def __exit__(self, *arguments):
                return response.__exit__(*arguments)
            def __getattr__(self, key):
                return getattr(response, key)
            def read(self, *arguments):
                data = response.read(*arguments)
                owner.last = {'sizeBytes': len(data), 'sha256': sha(data)}
                return data
        return Response()


class SelectionProbe(completion.CompletionProbe):
    def __init__(self, output, dist, oracle, icd, unavailable):
        super().__init__(output, dist, 240, oracle)
        self.icd = icd
        self.unavailable = unavailable
        self.gpu_samples = {}
        self.gpu_attempts = {}
        self.gpu_lock = threading.Lock()
        self.gpu_assignments = []
        self.driver_hashes = {}
        self.completed = []
        self.frames = []
        self.open_previews = []
        self.task_intervals = {}
        self.task_hardware = []
        self.measurement_hardware = []
        self.http = ResponseFingerprints(self.http)
        self.response_fingerprints = {}
        self.client_heartbeat_times = {}

    def request(self, name, method, path, **kwargs):
        selected = kwargs.get('client', 'default')
        selected = self.client if selected == 'default' else selected
        if selected in self.tokens and not path.endswith('/heartbeat') and path != '/api/v1/clients':
            now = time.monotonic()
            if now - self.client_heartbeat_times.get(selected, 0) >= 25:
                super().request('auto-heartbeat-' + str(self.request_count + 1), 'POST',
                    '/api/v1/clients/' + selected + '/heartbeat', client=selected)
                self.client_heartbeat_times[selected] = time.monotonic()
        task = method == 'POST' and path.endswith(('/validation-jobs', '/export-jobs'))
        if task:
            self.observe(self.current_ready['pid'])
            baseline = set(self.worker_identities)
            started = time.monotonic()
        response = super().request(name, method, path, **kwargs)
        if path.endswith('/heartbeat') and selected in self.tokens:
            self.client_heartbeat_times[selected] = time.monotonic()
        self.response_fingerprints[name] = dict(self.http.last)
        if task and 'jobId' in response:
            self.observe(self.current_ready['pid'])
            self.task_intervals[response['jobId']] = {'name': name, 'beforeAdmission': baseline,
                'admissionStarted': started, 'acceptedAt': time.monotonic()}
        return response

    inspect_worker = gpu.GpuProbe.inspect_worker
    require_gpu = gpu.GpuProbe.require_gpu
    worker_identities_now = gpu.GpuProbe.worker_identities_now
    require_workers_drained = gpu.GpuProbe.require_workers_drained

    def observe(self, pid):
        super().observe(pid)
        # The shared observer only follows this launcher's descendants. Retain
        # only the two whitelisted Vulkan/backend environment selectors.
        for identity in list(self.worker_identities):
            if process_identity(identity[0]) != identity:
                continue
            with self.gpu_lock:
                if self.gpu_samples.get(identity, {}).get('hardwareConfirmed'):
                    continue
                now = time.monotonic()
                if now - self.gpu_attempts.get(identity, 0) < .05:
                    continue
                self.gpu_attempts[identity] = now
            sample = self.inspect_worker(identity)
            with self.gpu_lock:
                if sample.get('hardwareConfirmed') or not self.gpu_samples.get(identity, {}).get('hardwareConfirmed'):
                    self.gpu_samples[identity] = sample

    def start(self, name):
        self.current_ready = None
        self.monitor = None
        self.launcher = subprocess.Popen([sys.executable, str(HERE / 'host.py'), 'run', '--dist', str(self.dist),
            '--profile', str(self.output / 'profile'), '--origin', 'http://localhost:5173',
            '--origin', 'http://127.0.0.1:5173'], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        launcher_pid = self.launcher.pid
        def monitor():
            while not self.monitor_stop.wait(.01):
                self.observe(launcher_pid)
        self.monitor_stop.clear()
        self.monitor = threading.Thread(target=monitor, daemon=True)
        self.monitor.start()
        lines = queue.Queue()
        def read():
            for line in self.launcher.stdout:
                lines.put(line)
            lines.put(None)
        threading.Thread(target=read, daemon=True).start()
        deadline = time.monotonic() + 90
        logged = []
        while time.monotonic() < deadline:
            try:
                line = lines.get(timeout=.2)
            except queue.Empty:
                continue
            if line is None:
                break
            logged.append(line)
            if line.startswith('WEB_BACKEND_READY '):
                ready = json.loads(line.split(' ', 1)[1])
                credential = Path(ready['credentialFile'])
                self.check(credential.stat().st_mode & 0o777 == 0o600, name + ': credential0600')
                self.check(credential.parent.stat().st_mode & 0o777 == 0o700, name + ': run0700')
                self.bootstrap = json.loads(credential.read_text())['bootstrapToken']
                self.secrets.append(self.bootstrap)
                self.host, self.base_url = ready['hostInstanceId'], ready['addresses'][0]
                self.client = None
                self.check(self.base_url.startswith('http://127.0.0.1:'), name + ': random loopback')
                self.current_ready = ready
                self.runs.append({'ready': ready, 'launcherPid': self.launcher.pid})
                save_json(self.output / (name + '-launcher.json'), {'stdout': logged, **ready})
                return
        raise AssertionError(name + ': launcher not ready: ' + ''.join(logged))

    def oracle_run(self, name, arguments):
        result = subprocess.run(['flock', '-o', LOCK, 'dotnet', str(self.oracle), *map(str, arguments)],
            capture_output=True, text=True, timeout=180)
        (self.output / (name + '-oracle.log')).write_text(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': original fixture generation exits0')

    def long_job(self, name, accepted, expected='succeeded'):
        interval = self.task_intervals[accepted['jobId']]
        backend = accepted['input']['renderBackend']
        deadline = time.monotonic() + self.job_timeout
        hardware = set()
        count = 0
        while time.monotonic() < deadline:
            self.heartbeat(name)
            count += 1
            current = self.request(name + '-poll-' + str(count), 'GET', '/api/v1/jobs/' + accepted['jobId'])
            self.check(current['input'] == accepted['input'], name + ': task input remains exactly fixed')
            self.observe(self.current_ready['pid'])
            if backend == 'gpu' and current['state'] == 'running':
                live = {(item['pid'], item['startTicks']) for item in self.worker_identities_now()}
                with self.gpu_lock:
                    matching = {identity for identity, sample in self.gpu_samples.items()
                        if sample.get('hardwareConfirmed') and identity in live
                        and identity not in interval['beforeAdmission']}
                hardware.update(matching)
                if matching:
                    self.task_hardware.append({'name': name, 'jobId': accepted['jobId'],
                        'jobState': current['state'], 'progress': current.get('progress'),
                        'workers': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(matching)]})
            if current['state'] in ('succeeded', 'failed', 'cancelled', 'unknown'):
                save_json(self.output / (name + '-terminal.json'), self.redact(current))
                self.check(current['state'] == expected, name + ': expected terminal ' + expected + ', actual ' + json.dumps(self.redact(current)))
                self.check(not current['canCancel'], name + ': terminal cannot cancel')
                self.terminals.append({'jobId': current['jobId'], 'kind': current['kind'], 'state': current['state']})
                if backend == 'gpu':
                    self.check(bool(hardware), name + ': actual running job sampled fresh published Vulkan Worker with hardware driver and DRM fd')
                return current
            time.sleep(.1)
        raise AssertionError(name + ': no terminal within configured wall time')

    def seed_project(self, name):
        self.make_empty_project()
        self.send(name + '-duration', {'type': 'composition.setOutputDuration', 'durationUs': '1000000'})
        audio, _ = completion.CompletionProbe.wav(1, 8000, 220, 8000)
        audio_asset = self.upload(name + '-audio-upload', name + '.wav', 'audio/wav', audio)
        self.send(name + '-audio-attach', {'type': 'audio.replaceAsset', 'assetId': audio_asset['assetId']})
        lyrics = self.upload(name + '-lyrics', name + '.lrc', 'text/plain', b'[00:00.00]CPU GPU original\n')
        self.send(name + '-import', {'type': 'lyrics.importAsset', 'assetId': lyrics['assetId'],
            'destination': {'kind': 'existing', 'branchId': 'primary'},
            'options': {'importerId': 'lrc.plain', 'encodingName': 'utf-8'}})
        line = self.lyrics(name + '-window')['lines'][0]
        self.send(name + '-token-times', {'type': 'lyrics.setTokenTimes', 'changes': [
            {'target': {'branchId': 'primary', 'lineId': line['id'], 'tokenId': token['id']},
             'startUs': str(1000000 * index // len(line['tokens'])),
             'endUs': str(1000000 * (index + 1) // len(line['tokens']))}
            for index, token in enumerate(line['tokens'])]})
        self.send(name + '-time', {'type': 'lyrics.setTimeRange',
            'target': {'branchId': 'primary', 'lineId': line['id']}, 'startUs': '0', 'endUs': '1000000'})
        program = next(item for item in self.orchestration(name + '-tree')['programs'] if item['branchId'] == 'primary')
        self.send(name + '-clip', {'type': 'orchestration.createClip', 'branchId': 'primary', 'clipId': 'clip/render-selection',
            'name': 'Render selection clip', 'lineIds': [line['id']], 'containerId': program['defaultContainerId']})

    def project_create(self, name, backend=None):
        body = {'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': self.revision},
            'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview'}
        if backend is not None:
            body['renderBackend'] = backend
        value = self.request(name, 'POST', '/api/v1/preview-sessions', status=201, value=body)
        self.open_previews.append((self.client, value))
        return value

    def state(self, name, preview):
        return self.request(name, 'GET', '/api/v1/preview-sessions/' + preview['previewId'])['session']

    def diagnostic(self, name, preview):
        return self.request(name, 'GET', '/api/v1/preview-sessions/' + preview['previewId'] + '/diagnostics?limit=128')

    def frame(self, name, preview, time_us='500000', sequence=1, intent='playback'):
        pixels = super().frame(name, preview, time_us, sequence, intent)
        current = self.state(name + '-final', preview)
        frame = current['latestFrame']
        self.check(current['renderBackend'] == frame['renderBackend'] == preview['renderBackend'], name + ': session and complete frame echo fixed backend')
        self.check(set(pixels[3::4]) == {255}, name + ': opaque full readback alpha')
        if preview['renderBackend'] == 'gpu':
            live = {(item['pid'], item['startTicks']) for item in self.worker_identities_now()}
            self.require_gpu(name, identities=live)
        self.frames.append({'name': name, 'kind': preview['previewKind'], 'renderBackend': frame['renderBackend'],
            'previewId': frame['previewId'], 'generation': frame['previewGeneration'],
            'frameId': frame['frameId'], 'pngSha256': frame['content']['sha256'], 'rgbaSha256': sha(pixels)})
        return pixels

    def switch(self, name, preview, backend, *, status=200, generation=None, client='default', code=None):
        metadata = getattr(self, '_switch_metadata', {}).get(name)
        if metadata is None:
            metadata = self.metadata(name)
        result = self.request(name, 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/render-backend',
            status=status, code=code, client=client, value={'expectedPreviewGeneration': generation or preview['previewGeneration'],
                'renderBackend': backend, 'metadata': metadata})
        if status == 200:
            preview.clear()
            preview.update(result)
        return result

    def switch_cycle(self, name, preview):
        self.check(preview['renderBackend'] == 'cpu', name + ': begins explicit CPU')
        pixels = self.frame(name + '-cpu-before', preview)
        before = self.state(name + '-before-noop', preview)
        diagnostics = self.diagnostic(name + '-before-noop-diagnostics', preview)
        replay_metadata = self.metadata(name + '-replay')
        self._switch_metadata = {name + '-noop': replay_metadata, name + '-replay': replay_metadata}
        noop = self.switch(name + '-noop', preview, 'cpu')
        replay = self.switch(name + '-replay', preview, 'cpu')
        self.check(replay == noop and self.response_fingerprints[name + '-noop'] == self.response_fingerprints[name + '-replay'],
            name + ': same metadata replays byte-identical no-op response')
        changed_request = dict(replay_metadata, requestId=name + '-replay')
        self.request(name + '-replay-conflict', 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/render-backend',
            status=409, code='requestIdReuse', value={'expectedPreviewGeneration': preview['previewGeneration'],
                'renderBackend': 'gpu', 'metadata': changed_request})
        self.request(name + '-missing-generation', 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/render-backend',
            status=400, code='invalidRequest', value={'renderBackend': 'gpu', 'metadata': self.metadata(name + '-missing-generation')})
        self.request(name + '-invalid-generation', 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/render-backend',
            status=400, code='invalidRequest', value={'expectedPreviewGeneration': '01', 'renderBackend': 'gpu', 'metadata': self.metadata(name + '-invalid-generation')})
        self.request(name + '-invalid-backend', 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/render-backend',
            status=400, code='invalidRequest', value={'expectedPreviewGeneration': preview['previewGeneration'], 'renderBackend': 'vulkan', 'metadata': self.metadata(name + '-invalid-backend')})
        self.check(preview == before, name + ': same-backend no-op preserves full session frame generation and diagnostics cursor')
        self.check(self.diagnostic(name + '-after-noop-diagnostics', preview) == diagnostics, name + ': no-op preserves complete diagnostics page')
        pinned = {key: copy.deepcopy(preview.get(key)) for key in
            ('source', 'size', 'alphaMode', 'previewKind', 'committable', 'editCandidate', 'packageClosure')}
        old_generation = preview['previewGeneration']
        if name == 'project':
            self.send(name + '-mutate-project-after-capture', {'type': 'project.rename', 'name': 'Changed after preview capture'})
            self.check(preview['source']['sourceRevision'] != self.revision, name + ': preview remains pinned while source project revision changes')
        self.switch(name + '-gpu', preview, 'gpu')
        self.check(int(preview['previewGeneration']) > int(old_generation) and preview['renderBackend'] == 'gpu'
            and preview.get('latestFrame') is None and preview.get('diagnosticsCursor') is None,
            name + ': changed backend advances generation and invalidates frame diagnostics')
        self.check(all(preview.get(key) == value for key, value in pinned.items()), name + ': all fixed source and candidate fields retained')
        self.switch(name + '-stale', preview, 'cpu', generation=old_generation, status=409, code='previewGenerationConflict')
        self.frame(name + '-gpu-frame', preview, sequence=2)
        self.switch(name + '-cpu', preview, 'cpu')
        self.check(all(preview.get(key) == value for key, value in pinned.items()), name + ': return CPU retains exact fixed input')
        self.check(self.frame(name + '-cpu-after', preview, sequence=3) == pixels, name + ': CPU return renders identical full fixed-input RGBA')
        self.completed.append(name)

    def update_input_backends(self, name, preview):
        self.switch(name + '-begin-gpu', preview, 'gpu')
        original_source = copy.deepcopy(preview['source'])
        for sequence, backend in enumerate((None, 'cpu'), 1):
            body = {'expectedPreviewGeneration': preview['previewGeneration'],
                'expectedSourceRevision': preview['source']['sourceRevision'],
                'source': preview['source'], 'size': preview['size'], 'alphaMode': preview['alphaMode'],
                'metadata': self.metadata(name + '-input-' + str(sequence))}
            if backend is not None:
                body['renderBackend'] = backend
            updated = self.request(name + '-input-' + str(sequence), 'PUT',
                '/api/v1/preview-sessions/' + preview['previewId'] + '/input', value=body)
            preview.clear()
            preview.update(updated)
            self.check(preview['renderBackend'] == (backend or 'gpu'), name + ': input omission preserves GPU and explicit CPU selects CPU')
            self.check(preview['source'] == original_source, name + ': input source retained')
            self.frame(name + '-frame-' + str(sequence), preview, sequence=sequence)
        self.completed.append(name)

    def close(self, name, preview):
        self.close_preview(name, preview)
        self.open_previews = [(client, item) for client, item in self.open_previews if item is not preview]

    def previews(self):
        main = self.client
        project, revision = self.project, self.revision
        preview = self.project_create('project-preview', 'cpu')
        self.switch_cycle('project', preview)
        revision = self.revision
        peer = self.register('peer-register', select=False)
        self.secrets.extend((peer['accessToken'], peer['resumeToken']))
        self.switch('cross-client-switch', preview, 'gpu', client=peer['clientId'], status=404, code='previewSessionNotFound')
        self.close('project', preview)
        input_preview = self.project_create('current-project-input-preview', 'cpu')
        self.update_input_backends('project-input-update', input_preview)
        self.close('project-input-update', input_preview)
        self.client = peer['clientId']
        self.seed_project('peer')
        peer_preview = self.project_create('peer-gpu', 'gpu')
        self.client, self.project, self.revision = main, project, revision
        own_preview = self.project_create('own-cpu', 'cpu')
        self.frame('distinct-peer-own-cpu', own_preview)
        self.client = peer['clientId']
        self.frame('distinct-peer-gpu', peer_preview)
        self.client = main
        self.check(self.state('distinct-own-unchanged', own_preview)['renderBackend'] == 'cpu', 'two live clients retain distinct CPU GPU choices')
        self.close('own', own_preview)
        self.client = peer['clientId']
        self.close('peer', peer_preview)
        self.client, self.project, self.revision = main, project, revision
        self.completed.append('two-clients-distinct-backends')

        edits = self.request('edit-create', 'POST', self.project_path + '/edit-previews', status=201, value={
            'expectedRevision': self.revision, 'edits': [{'type': 'composition.setBackgroundColor',
                'red': 20, 'green': 40, 'blue': 80, 'alpha': 255}], 'size': {'width': 320, 'height': 180}, 'renderBackend': 'cpu'})
        self.open_previews.append((main, edits))
        self.switch_cycle('editCandidate', edits)
        self.switch('edit-measurement-gpu', edits, 'gpu')
        measurement = self.measure('candidate-measurement-omitted', None, edits)
        self.check(measurement['renderBackend'] == 'gpu', 'omitted candidate measurement uses pinned GPU')
        self.check(self.measure('candidate-measurement-cpu', 'cpu', edits)['renderBackend'] == 'cpu', 'explicit candidate measurement uses CPU')
        self.check(self.state('candidate-measurement-retained', edits)['renderBackend'] == 'gpu', 'one measurement does not change candidate GPU')
        self.close('edit', edits)

        library = self.request('builtin-templates', 'GET', '/api/v1/template-libraries/builtin/templates')
        template = next(item for item in library['templates'] if item['kind'] == 'line')
        source = {'kind': 'savedTemplate', 'libraryId': 'builtin', 'libraryRevision': library['revision'],
            'templateId': template['templateId'], 'templateRevision': template['templateRevision']}
        options = self.request('template-options', 'POST', '/api/v1/template-preview-options', value={'source': source})
        sample = next(item for item in options['samples'] if item['targets'] and not item['multiLine'])
        target = sample['targets'][0]
        trial = self.request('template-preview', 'POST', '/api/v1/template-preview-sessions', status=201,
            value={'source': source, 'sampleId': sample['sampleId'], 'targetKey': target['key'],
                'size': {'width': 320, 'height': 180}, 'renderBackend': 'cpu'})
        self.open_previews.append((main, trial))
        self.switch_cycle('template', trial)
        self.close('template', trial)
        line = self.lyrics('preset-target')['lines'][0]
        candidate = self.request('preset-preview', 'POST', self.project_path + '/preset-application-previews', status=201,
            value={'expectedProjectRevision': self.revision, 'source': source,
                'targets': [{'kind': 'line', 'branchId': 'primary', 'lineId': line['id']}],
                'size': {'width': 320, 'height': 180}, 'renderBackend': 'cpu'})
        self.open_previews.append((main, candidate))
        self.switch_cycle('presetCandidate', candidate)
        self.close('preset', candidate)
        self.author()

    def author(self):
        archive = self.output / 'original.mkfx'
        self.oracle_run('author-fixture', ['p3-package', archive])
        uploaded = self.upload('author-package', archive.name, 'application/octet-stream', archive.read_bytes())
        draft = self.request('author-create', 'POST', '/api/v1/drafts', status=201,
            value={'packageAssetId': uploaded['assetId'], 'exportId': 'controller', 'name': 'Render selection original'})
        options = draft['sampleOptions']
        sample = next(item for item in options['samples'] if item['sampleId'] == options['defaultSampleId'])
        target = sample['targets'][0]
        material = options['materialSlots'][0] if options['materialSlots'] else None
        selection = {'sampleId': sample['sampleId'], 'targetKey': target['key'], 'materialSlot': material}
        source = {'kind': 'authorDraft', 'sourceId': draft['draftId'], 'sourceRevision': draft['draftRevision'],
            'authorSourceRevision': draft['configuration']['sourceRevision'], 'templateSample': selection}
        preview = self.request('author-preview', 'POST', '/api/v1/preview-sessions', status=201,
            value={'source': source, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque',
                'purpose': 'preview', 'previewKind': 'authorDraft', 'committable': False, 'renderBackend': 'cpu'})
        self.open_previews.append((self.client, preview))
        self.switch_cycle('authorDraft', preview)
        self.close('author', preview)
        input_preview = self.request('author-input-preview', 'POST', '/api/v1/preview-sessions', status=201,
            value={'source': source, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque',
                'purpose': 'preview', 'previewKind': 'authorDraft', 'committable': False, 'renderBackend': 'cpu'})
        self.open_previews.append((self.client, input_preview))
        self.update_input_backends('author-input-update', input_preview)
        self.close('author-input-update', input_preview)
        self.require_workers_drained('author-validation-before')
        accepted = self.request('author-validation', 'POST', '/api/v1/drafts/' + draft['draftId'] + '/validation-jobs', status=202,
            value={'expectedDraftRevision': draft['draftRevision'], **selection, 'renderBackend': 'gpu'})
        self.check(accepted['authorValidationInput']['renderBackend'] == 'gpu' and accepted['input']['renderBackend'] == 'gpu', 'author validation pins GPU in both input records')
        terminal = self.long_job('author-validation', accepted)
        self.report('author-validation', terminal, 'validation')
        self.completed.append('author-validation-gpu')

    def measure(self, name, backend, candidate=None):
        value = {'expectedRevision': self.revision, 'mode': 'time', 'timeUs': '500000', 'width': 320, 'height': 180, 'alphaMode': 'opaque'}
        if backend is not None:
            value['renderBackend'] = backend
        if candidate:
            value.update(previewId=candidate['previewId'], expectedPreviewGeneration=candidate['previewGeneration'],
                candidateRevision=candidate['editCandidate']['candidateRevision'])
        self.observe(self.current_ready['pid'])
        baseline = set(self.worker_identities)
        started = time.monotonic()
        data = self.request(name, 'POST', self.project_path + '/orchestration/measurements', value=value)['measurement']
        self.observe(self.current_ready['pid'])
        if data['renderBackend'] == 'gpu':
            with self.gpu_lock:
                workers = [sample for identity, sample in self.gpu_samples.items()
                    if identity not in baseline and sample.get('hardwareConfirmed')]
            self.check(bool(workers), name + ': own fresh Vulkan hardware Worker observed in synchronous measurement interval')
            self.measurement_hardware.append({'name': name, 'startedAtMonotonic': started,
                'completedAtMonotonic': time.monotonic(), 'workers': [{'pid': item['pid'], 'startTicks': item['startTicks']} for item in workers],
                'scope': 'Own fresh hardware Workers during synchronous request; availability and measurement Worker identities are not separately attributed.'})
        png = base64.b64decode(data['png'], validate=True)
        self.check(sha(png) == data['pngSha256'], name + ': full measured PNG SHA')
        self.decode_png(name, png)
        if backend:
            self.check(data['renderBackend'] == backend, name + ': fixed measurement backend')
        return data

    def report(self, name, terminal, kind):
        reference = terminal[kind + 'Result']['report']
        _, raw, _ = self.fetch(name + '-report', '/api/v1/jobs/' + terminal['jobId'] + '/' + kind + '-result')
        data = json.loads(raw)
        self.check(len(raw) == reference['sizeBytes'] and sha(raw) == reference['sha256'], name + ': immutable complete report size SHA')
        self.check(data['renderBackend'] == terminal['input']['renderBackend'], name + ': report and fixed task input backend')
        if kind == 'validation':
            self.check(data['succeeded'] and data['opaqueFrames'] == data['transparentFrames'] == terminal['input']['framesPerOutput'], name + ': both complete fixed validation frame plans succeed')
        else:
            self.check(terminal['exportInput']['renderBackend'] == data['renderBackend'], name + ': export execution input backend matches report')
            self.check(data['isVerified'] and data['strictlyVerified'] and data['expectedFrames'] == data['decodedFrames'] == data['framesWritten']
                and data['failedFrames'] == data['droppedFrames'] == 0, name + ': complete strict export report frame counts')
            self.check(not data['audioIncluded'] and data.get('audioCodec') is None, name + ': explicit no-audio output report')
        return data

    def tasks(self):
        self.require_workers_drained('tasks-before')
        for backend in ('cpu', 'gpu'):
            self.measure('project-measurement-' + backend, backend)
        accepted_cpu = self.request('validation-cpu', 'POST', self.project_path + '/validation-jobs', status=202,
            value={'expectedRevision': self.revision, 'framesPerOutput': 24, 'renderBackend': 'cpu'})
        accepted = self.request('validation-gpu', 'POST', self.project_path + '/validation-jobs', status=202,
            value={'expectedRevision': self.revision, 'framesPerOutput': 24, 'renderBackend': 'gpu'})
        self.check(accepted_cpu['input']['renderBackend'] == 'cpu' and accepted['input']['renderBackend'] == 'gpu', 'simultaneous CPU and GPU validation inputs are fixed independently')
        self.send('task-after-acceptance-rename', {'type': 'project.rename', 'name': 'Modified after fixed CPU and GPU jobs'})
        self.check(accepted_cpu['input']['sourceRevision'] != self.revision and accepted['input']['sourceRevision'] != self.revision, 'registered CPU and GPU task sources remain pinned while project revision advances')
        self.report('validation-gpu', self.long_job('validation-gpu', accepted), 'validation')
        self.report('validation-cpu', self.long_job('validation-cpu', accepted_cpu), 'validation')
        accepted = self.request('export-gpu', 'POST', self.project_path + '/export-jobs', status=202,
            value={'expectedRevision': self.revision, 'format': 'opaqueMp4', 'includeAudio': False, 'renderBackend': 'gpu'})
        self.check(accepted['input']['renderBackend'] == 'gpu', 'accepted export pins GPU')
        terminal = self.long_job('export-gpu', accepted)
        report = self.report('export-gpu', terminal, 'export')
        artifact = terminal['exportResult']['artifact']
        _, media, _ = self.fetch('export-media', '/api/v1/jobs/' + terminal['jobId'] + '/export-content')
        self.check(len(media) == artifact['sizeBytes'] == report['mediaSize'] and sha(media) == artifact['sha256'] == report['sha256'], 'export full media bytes size SHA')
        media_file = self.output / 'gpu-opaque.mp4'
        media_file.write_bytes(media)
        result = subprocess.run(['ffmpeg', '-v', 'error', '-i', str(media_file), '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'],
            capture_output=True, timeout=60)
        expected = int(report['expectedFrames']) * int(report['width']) * int(report['height']) * 4
        self.check(result.returncode == 0 and len(result.stdout) == expected, 'export independently decodes all complete RGBA frames')
        self.check(set(result.stdout[3::4]) == {255} and not report['hasAlpha'], 'opaque complete media alpha is fully opaque')
        accepted = self.request('export-gpu-transparent', 'POST', self.project_path + '/export-jobs', status=202,
            value={'expectedRevision': self.revision, 'format': 'transparentProRes4444', 'includeAudio': False,
                'renderBackend': 'gpu'})
        self.check(accepted['input']['renderBackend'] == 'gpu', 'accepted transparent export pins GPU')
        terminal = self.long_job('export-gpu-transparent', accepted)
        report = self.report('export-gpu-transparent', terminal, 'export')
        artifact = terminal['exportResult']['artifact']
        _, media, _ = self.fetch('export-transparent-media', '/api/v1/jobs/' + terminal['jobId'] + '/export-content')
        self.check(len(media) == artifact['sizeBytes'] == report['mediaSize'] and sha(media) == artifact['sha256'] == report['sha256'], 'transparent export full media bytes size SHA')
        media_file = self.output / 'gpu-transparent.mov'
        media_file.write_bytes(media)
        result = subprocess.run(['ffmpeg', '-v', 'error', '-i', str(media_file), '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'], capture_output=True, timeout=60)
        expected = int(report['expectedFrames']) * int(report['width']) * int(report['height']) * 4
        self.check(result.returncode == 0 and len(result.stdout) == expected, 'transparent export independently decodes all complete RGBA frames')
        self.check(report['hasAlpha'] and min(result.stdout[3::4]) == 0 and max(result.stdout[3::4]) > 0,
            'transparent complete media has zero and visible alpha values')
        self.completed.extend(['measurement-cpu-gpu', 'validation-gpu', 'export-gpu'])

    def closed_source(self):
        preview = self.project_create('closed-source-preview', 'cpu')
        pixels = self.frame('closed-source-before', preview)
        source = copy.deepcopy(preview['source'])
        accepted = self.request('source-project-close', 'POST', self.project_path + '/close', status=202,
            value={'expectedRevision': self.revision, 'policy': 'discard', 'activeJobs': 'wait'})
        completion.CompletionProbe.long_job(self, 'source-project-close', accepted)
        self.request('source-project-gone', 'GET', self.project_path, status=404, code='sessionNotFound')
        self.switch('closed-source-gpu', preview, 'gpu')
        self.check(preview['source'] == source, 'closed-source GPU switch retains captured source identity')
        self.frame('closed-source-gpu', preview, sequence=2)
        self.switch('closed-source-cpu', preview, 'cpu')
        self.check(self.frame('closed-source-cpu', preview, sequence=3) == pixels,
            'source project close preserves fixed input through GPU and CPU return full readback')
        self.close('closed-source', preview)
        self.completed.append('closed-source-switch')

    def host_environment(self):
        identity = process_identity(self.current_ready['pid'])
        allowed = {b'MAKO_RENDER_BACKEND', b'VK_DRIVER_FILES', b'VK_ICD_FILENAMES'}
        result = {}
        for value in Path('/proc/' + str(identity[0]) + '/environ').read_bytes().split(b'\0'):
            key, separator, text = value.partition(b'=')
            if separator and key in allowed:
                result[key.decode()] = text.decode('utf-8', 'replace')
        self.check(process_identity(identity[0]) == identity, 'owned Host environment sample identity rechecked')
        return {'pid': identity[0], 'startTicks': identity[1], 'environment': result}

    def unavailable_flow(self):
        preview = self.project_create('unavailable-cpu', 'cpu')
        pixels = self.frame('unavailable-before', preview)
        before = self.state('unavailable-original', preview)
        diagnostics = self.diagnostic('unavailable-original-diagnostics', preview)
        rejection = self.switch('unavailable-switch', preview, 'gpu', status=503, code='taskUnavailable')
        self.check(rejection['field'] == 'renderBackend' and rejection['failureCode'] == 'RenderBackendUnavailable', 'explicit unavailable GPU has stable renderBackend failure')
        self.check(self.state('unavailable-retained', preview) == before and self.diagnostic('unavailable-retained-diagnostics', preview) == diagnostics,
            'failed GPU switch preserves complete CPU generation frame diagnostics')
        _, png, _ = self.fetch('unavailable-existing-frame', before['latestFrame']['content']['contentUrl'])
        self.check(self.decode_png('unavailable-existing-frame', png) == pixels, 'failed GPU switch preserves existing complete PNG handle bytes')
        self.check(self.frame('unavailable-cpu-after', preview, sequence=2) == pixels, 'CPU continues rendering after GPU rejection')
        self.close('unavailable-cpu', preview)
        body = {'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': self.revision},
            'size': {'width': 320, 'height': 180}, 'renderBackend': 'gpu'}
        rejection = self.request('unavailable-gpu-create', 'POST', '/api/v1/preview-sessions', status=503, code='taskUnavailable', value=body)
        self.check(rejection['failureCode'] == 'RenderBackendUnavailable', 'explicit GPU create does not fall back CPU')
        self.completed.append('unavailable-gpu-preserves-cpu')

    def run(self):
        deployment = host.validate_dist(self.dist)
        self.check(not deployment['sourceDirty'], 'deployment records clean production inputs')
        save_json(self.output / 'deployment-reference.json', {'dist': str(self.dist), 'manifestSha256': sha((self.dist / 'deployment.json').read_bytes()),
            'version': deployment['version'], 'sourceCommit': deployment['sourceCommit'], 'sourceDirty': deployment['sourceDirty']})
        self.start('selection-start')
        environment_before = self.host_environment()
        self.connect('selection')
        availability = self.request('render-backends', 'GET', '/api/v1/render-backends')
        backends = {item['renderBackend']: item for item in availability['backends']}
        self.check(availability['defaultBackend'] == 'cpu' and set(backends) == {'cpu', 'gpu'}, 'startup CPU default with exactly two explicit backends')
        self.check(backends['cpu']['available'] and backends['cpu']['api'] == 'raster', 'actual CPU device available')
        self.check(backends['gpu']['available'] is not self.unavailable and backends['gpu']['api'] == 'vulkan', 'actual GPU availability matches isolated environment')
        if self.unavailable:
            self.check(backends['gpu']['unavailableReason']['failureCode'] == 'RenderBackendUnavailable', 'unavailable list has stable device failure code')
        else:
            self.check(bool(backends['gpu']['device']), 'available GPU device summary present')
        self.seed_project('main')
        if self.unavailable:
            self.unavailable_flow()
        else:
            self.previews()
            self.tasks()
            self.closed_source()
        environment_after = self.host_environment()
        self.check(environment_before == environment_after and environment_after['environment'].get('MAKO_RENDER_BACKEND') == 'cpu',
            'per-request CPU GPU choices preserve owned Host deployment environment')
        save_json(self.output / 'host-environment.json', {'before': environment_before, 'after': environment_after})
        self.require_workers_drained('all-preview-and-task-workers')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--oracle', type=Path, default=ROOT / 'tools/backend-project-image-preview-probe/FixtureOracle/bin/Release/net10.0/FixtureOracle.dll')
    parser.add_argument('--icd', type=Path, default=Path('/usr/share/vulkan/icd.d/radeon_icd.json'))
    parser.add_argument('--unavailable', action='store_true', help='pin nonexistent ICD and require explicit GPU rejection with CPU survival')
    args = parser.parse_args()
    icd = gpu.hardware_icd(args.icd)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    selector = str(output / 'nonexistent-vulkan-icd.json') if args.unavailable else icd['path']
    os.environ['MAKO_RENDER_BACKEND'] = 'cpu'
    os.environ['VK_DRIVER_FILES'] = selector
    os.environ['VK_ICD_FILENAMES'] = selector
    probe = SelectionProbe(output, args.dist.resolve(), args.oracle.resolve(), icd, args.unavailable)
    save_json(output / 'environment.json', {'defaultBackend': 'cpu', 'icd': icd, 'vulkanSelector': selector, 'unavailable': args.unavailable})
    failure = None
    try:
        probe.run()
    except BaseException as error:
        failure = error
        (output / 'failure.txt').write_text(str(error) + '\n')
    finally:
        try:
            probe.stop('selection-final')
        except BaseException as error:
            failure = failure or error
    with probe.gpu_lock:
        samples = [sample for _, sample in sorted(probe.gpu_samples.items())]
    save_json(output / 'gpu-worker-evidence.json', {'workers': samples, 'assignments': probe.gpu_assignments,
        'runningTaskSamples': probe.task_hardware, 'measurementIntervalSamples': probe.measurement_hardware,
        'scope': 'Only owned published Worker descendants; PID and start ticks rechecked; two selector environment keys; mapped Vulkan loader, pinned hardware ICD driver and DRM render fd.'})
    summary = {'status': 'failed' if failure else 'passed', 'error': str(failure) if failure else None,
        'httpRequests': probe.request_count, 'completed': probe.completed, 'frames': probe.frames,
        'runs': probe.runs, 'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(probe.worker_identities)],
        'hardwareWorkerCount': sum(bool(item.get('hardwareConfirmed')) for item in samples),
        'scope': 'Finite Linux local CPU/GPU selection HTTP acceptance. Full PNG and complete short video decode; CPU return consistency only, no cross-backend pixel equality, independent Runtime oracle, browser, platform or performance claim.'}
    save_json(output / 'summary.json', summary)
    save_json(output / 'response-fingerprints.json', probe.response_fingerprints)
    try:
        summary['credentialScannedFiles'] = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        summary.update(status='failed', error=str(failure))
    summary.update(assertions=len(probe.assertions), assertionLabels=probe.assertions)
    save_json(output / 'summary.json', summary)
    save_json(output / 'evidence-manifest.json', {'files': {str(path.relative_to(output)): sha(path.read_bytes())
        for path in sorted(output.rglob('*')) if path.is_file() and path.name != 'evidence-manifest.json'}})
    print(json.dumps({key: summary.get(key) for key in ('status', 'error', 'httpRequests', 'assertions', 'completed', 'hardwareWorkerCount', 'credentialScannedFiles')}))
    return 1 if failure else 0


if __name__ == '__main__':
    sys.exit(main())
