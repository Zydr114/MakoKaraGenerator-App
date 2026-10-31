#!/usr/bin/env python3
"""Independent bounded B4-D real HTTP adoption/clear acceptance."""
import argparse
import copy
import hashlib
import importlib.util
import io
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

from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('b4c_analysis_helpers', ROOT / 'tools/backend-analysis-http-probe/probe.py')
ANALYSIS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ANALYSIS)
HELPER = ANALYSIS.HELPER


class AdoptionProbe(ANALYSIS.AnalysisProbe):
    EXTRA_EVIDENCE_SOURCES = ()
    EVIDENCE_BOUNDARIES = None

    def __init__(self, output, server, worker, oracle, runtime_oracle):
        super().__init__(output, server, worker, oracle)
        self.runtime_oracle = runtime_oracle
        self.adoptions, self.restorations = [], []

    def summary(self, name, source):
        return self.request(name, 'GET', '/api/v1/project-sessions/' + source['projectSessionId'])

    def state(self, name, source):
        summary = self.summary(name + '-summary', source)
        history = self.request(name + '-history', 'GET', '/api/v1/project-sessions/' + source['projectSessionId'] + '/history')
        return {'summary': summary, 'history': history}

    def command_for(self, name, source, command, *, receipt=None, revision=None, **kwargs):
        value = {**(receipt or self.metadata(name)), 'expectedRevision': revision if revision is not None else source['projectRevision'], 'command': command}
        return self.request(name, 'POST', '/api/v1/project-sessions/' + source['projectSessionId'] + '/commands', value=value, **kwargs)

    def apply(self, name, source, job, **kwargs):
        return self.command_for(name, source, {'type': 'audio.analysis.apply', 'analysisJobId': job['jobId']}, **kwargs)

    def refresh(self, name, source):
        current = self.summary(name, source)
        source.update(current)
        # An absent optional property must also clear a previously present value.
        if 'audioAnalysis' not in current:
            source.pop('audioAnalysis', None)
        return current

    def expect_no_change(self, name, source, action, *, concurrent_job_events=False):
        before = self.state(name + '-before', source)
        value = action()
        after = self.state(name + '-after', source)
        if concurrent_job_events:
            before['summary'].pop('eventCursor', None)
            after['summary'].pop('eventCursor', None)
        self.check(after == before, name + (': project summary/history unchanged; concurrent job events may advance cursor' if concurrent_job_events
                                           else ': rejected/no-op preserves exact summary, event cursor and history'))
        return value

    def expect_analysis(self, name, source, job=None):
        summary = self.refresh(name, source)
        actual = summary.get('audioAnalysis')
        if job is None:
            self.check(actual is None, name + ': no accepted analysis')
        else:
            expected = {key: value for key, value in job['analysisResult'].items() if key != 'report'}
            self.check(actual == {'status': 'available', **expected}, name + ': bounded accepted analysis exactly matches trusted complete result')
            self.check('points' not in actual and 'report' not in actual and 'path' not in actual, name + ': summary contains no points, sidecar handle or local path')
        return summary

    def save(self, name, source, directory):
        handle = self.mutate(name + '-handle', '/api/v1/file-handles', {'rootGrantId': 'adoption-probe-root', 'relativePath': directory}, status=201)
        job = self.mutate(name + '-accept', '/api/v1/project-sessions/' + source['projectSessionId'] + '/save-jobs',
                          {'expectedRevision': source['projectRevision'], 'targetHandleId': handle['handleId']}, status=202)
        terminal = self.wait_job(name, job)
        self.refresh(name + '-summary', source)
        self.check(not source['isDirty'] and terminal['savedProjectRevision'] == source['projectRevision'], name + ': exact current snapshot saved cleanly')
        return handle, terminal

    def close(self, name, source):
        accepted = self.mutate(name, '/api/v1/project-sessions/' + source['projectSessionId'] + '/close',
                               {'expectedRevision': source['projectRevision'], 'policy': 'discard', 'activeJobs': 'wait'}, status=202)
        return self.wait_job(name, accepted)

    def open(self, name, handle, readonly=False):
        accepted = self.mutate(name, '/api/v1/project-sessions/open-jobs',
            {'projectDirectoryHandleId': handle['handleId'], **({'access': 'read'} if readonly else {})}, status=202)
        return copy.deepcopy(self.wait_job(name, accepted)['result'])

    def fixture(self, name, rate, channels):
        frames = rate * 2 + rate * 13 // 1000
        path = self.output / (name + '.wav')
        samples = bytearray()
        for index in range(frames):
            for channel in range(channels):
                amplitude = .5 if index % (rate // 4) < rate // 9 else .12
                value = amplitude * math.sin(2 * math.pi * (80 if channel == 0 else 750) * index / rate)
                samples.extend(struct.pack('<h', int(32767 * value)))
        with wave.open(str(path), 'wb') as stream:
            stream.setparams((channels, 2, rate, 0, 'NONE', ''))
            stream.writeframes(samples)
        return path

    def sse_project(self, name, cursor, source, revision, expected_analysis):
        self.request_count += 1
        request = urllib.request.Request(self.base_url + '/api/v1/events', headers={'X-Mako-Client-Id': self.client,
            'X-Mako-Host-Instance-Id': self.host, 'Authorization': 'Bearer ' + self.tokens[self.client], 'Last-Event-ID': cursor})
        events, lines = [], []
        with self.http.open(request, timeout=10) as response:
            self.check(response.status == 200, name + ': authenticated actual SSE replay')
            while len(events) < 128:
                line = response.readline().decode().rstrip('\r\n')
                if line:
                    lines.append(line)
                    continue
                payload = [entry[6:] for entry in lines if entry.startswith('data: ')]
                lines = []
                if not payload:
                    continue
                event = json.loads('\n'.join(payload))
                events.append(event)
                summary = event.get('summary', event.get('project', event.get('session', {})))
                # Event DTO has a fixed session field; inspect no serialized text or credentials.
                if event.get('type') == 'project.changed':
                    candidates = [value for value in event.values() if isinstance(value, dict) and value.get('projectSessionId') == source['projectSessionId']]
                    if candidates and candidates[0].get('projectRevision') == revision:
                        summary = candidates[0]
                        self.check(summary.get('audioAnalysis') == expected_analysis, name + ': committed SSE summary carries same accepted analysis')
                        self.check(sum(1 for value in events if value.get('type') == 'project.changed') == 1, name + ': exactly one project event after isolated command')
                        HELPER.save_json(self.output / (name + '.json'), events)
                        return
        raise AssertionError(name + ': missing actual committed project event')

    def static_rejected(self, name, source):
        base = '/api/v1/project-sessions/' + source['projectSessionId']
        self.expect_no_change(name, source, lambda: self.mutate(name + '-preview', '/api/v1/preview-sessions',
            {'source': {'kind': 'project', 'sourceId': source['projectSessionId'], 'sourceRevision': source['projectRevision']},
             'size': {'width': 320, 'height': 180}}, status=422, code='commandNotSupported'))
        self.expect_no_change(name + '-validation', source, lambda: self.mutate(name + '-validation', base + '/validation-jobs',
            {'expectedRevision': source['projectRevision'], 'framesPerOutput': 1}, status=422, code='commandNotSupported'))
        self.expect_no_change(name + '-export', source, lambda: self.mutate(name + '-export', base + '/export-jobs',
            {'expectedRevision': source['projectRevision'], 'format': 'opaqueMp4', 'includeAudio': False}, status=422, code='commandNotSupported'))

    def corrupted(self, name, source, job, path, raw):
        backup = path.with_name(path.name + '.owned-backup')
        for kind in ('hash', 'size', 'missing', 'link'):
            try:
                if kind == 'hash':
                    offset = 44 if name == 'audio' else 0
                    path.write_bytes(raw[:offset] + bytes([raw[offset] ^ 1]) + raw[offset + 1:])
                elif kind == 'size':
                    path.write_bytes(raw[:-1])
                elif kind == 'missing':
                    path.rename(backup)
                else:
                    path.rename(backup)
                    path.symlink_to(backup)
                self.expect_no_change(name + '-' + kind, source, lambda: self.apply(name + '-' + kind + '-apply', source, job,
                    status=500, code='taskExecutionFailed'))
                if name == 'result' and kind == 'hash':
                    self.expect_no_change('receipt-before-corrupt-result', source, lambda: self.check(
                        self.apply('receipt-before-corrupt-result', source, job, **self.first_apply_request) == self.first_apply_response,
                        'receipt: original successful response wins before current revision and damaged result checks'))
            finally:
                if backup.exists():
                    path.unlink(missing_ok=True)
                    backup.rename(path)
                else:
                    path.write_bytes(raw)
        self.check(path.read_bytes() == raw, name + ': owned file fully restored after every rejected corruption')

    def normal(self):
        self.host = self.request('first-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.owner = self.register('first-owner')
        self.check(self.request('analysis-enabled', 'GET', '/api/v1/analysis-capabilities')['enabled'], 'deployment: real analysis enabled')
        self.lyrics = self.lyrics_upload('owned-lyrics')
        wav = self.fixture('pulse-mono-tail', 48000, 1)
        audio = self.upload('owned-mono', wav)
        source = self.create('adoption-main', audio)
        self.source, self.audio, self.wav = source, audio, wav
        before = self.project_state('before-analysis', source)
        meta, canonical = self.oracle_run('mono', wav)
        job = self.start_analysis('real-analysis', source)
        terminal = self.wait_job('real-analysis', job)
        self.check(self.project_state('after-analysis', source) == before, 'analysis: real completion performs no automatic project edit')
        self.raw = self.result('original-complete-analysis', terminal, canonical, meta)
        self.success = terminal
        self.comparisons.append({'name': 'mono-tail', 'canonicalBytesEqual': True, 'metadata': meta})
        lines = self.request('original-lyrics', 'GET', '/api/v1/project-sessions/' + source['projectSessionId'] + '/lyrics/lines?branchId=primary')
        target = {'branchId': 'primary', 'lineId': lines['lines'][0]['id'], 'tokenId': lines['lines'][0]['tokens'][0]['id']}
        edit = self.command_for('explicit-lyric-edit', source, {'type': 'lyrics.setTimeRange', 'target': target, 'startUs': '1000', 'endUs': '1000000'})
        source['projectRevision'] = edit['projectRevision']
        self.check(terminal['analysisInput']['sourceRevision'] != source['projectRevision'], 'adoption: analysis provenance revision predates explicit current lyric edit')
        for name, command in [('apply-missing-id', {'type': 'audio.analysis.apply'}),
                ('apply-extra-time', {'type': 'audio.analysis.apply', 'analysisJobId': terminal['jobId'], 'startUs': '0'}),
                ('clear-extra-job', {'type': 'audio.analysis.clear', 'analysisJobId': terminal['jobId']}),
                ('undo-extra-job', {'type': 'history.undo', 'analysisJobId': terminal['jobId']}),
                ('timing-extra-job', {'type': 'lyrics.setTimeRange', 'target': target, 'startUs': '1000', 'endUs': '1000000', 'analysisJobId': terminal['jobId']})]:
            self.expect_no_change(name, source, lambda name=name, command=command: self.command_for(name, source, command, status=400, code='invalidRequest'))
        self.expect_no_change('stale-revision', source, lambda: self.apply('stale-revision', source, terminal, revision='0', status=409, code='revisionConflict'))
        self.expect_no_change('unknown-job', source, lambda: self.apply('unknown-job', source, {'jobId': 'job-' + '0' * 32}, status=404, code='jobNotFound'))
        self.expect_no_change('unauthenticated-apply', source, lambda: self.apply('unauthenticated-apply', source, terminal,
            headers={'Authorization': 'Bearer ' + '0' * 64}, status=401, code='unauthorizedClient'))
        self.expect_no_change('expired-command', source, lambda: self.apply('expired-command', source, terminal,
            receipt=self.metadata('expired-command', seconds=-1), status=409, code='requestExpired'))
        before_apply = self.state('before-apply', source)
        receipt = self.metadata('apply-once')
        self.first_apply_request = {'receipt': receipt, 'revision': source['projectRevision']}
        accepted = self.apply('apply-once', source, terminal, receipt=receipt)
        self.first_apply_response = accepted
        self.check(accepted['changed'] and int(accepted['projectRevision']) == int(source['projectRevision']) + 1, 'adoption: one exact project revision increment')
        self.check(accepted['undoName'] == 'audio.analysis' and accepted['changeSet']['requiresFullRefresh'], 'adoption: single named domain history and conservative refresh')
        self.check(self.apply('apply-retry', source, terminal, receipt=receipt) == accepted, 'receipt: retry returns exact original response after revision changed')
        self.command_for('apply-receipt-conflict', source, {'type': 'audio.analysis.clear'}, receipt=receipt, status=409, code='requestIdReuse')
        summary = self.expect_analysis('adopted', source, terminal)
        client_state = self.request('adopted-client-state', 'GET', '/api/v1/clients/' + self.client + '/state')
        owned = [value for value in client_state['sessions'] if value['projectSessionId'] == source['projectSessionId']]
        self.check(len(owned) == 1 and owned[0]['audioAnalysis'] == summary['audioAnalysis'] and owned[0]['projectRevision'] == summary['projectRevision'],
            'adoption: authenticated client state projects the same committed bounded analysis summary')
        after_apply = self.state('after-apply', source)
        self.check(after_apply['history']['undoCount'] == before_apply['history']['undoCount'] + 1, 'adoption: exactly one undo entry')
        self.sse_project('adoption-sse', before_apply['summary']['eventCursor'], source, accepted['projectRevision'], summary['audioAnalysis'])
        replay_lines = self.request('lyrics-after-adoption', 'GET', '/api/v1/project-sessions/' + source['projectSessionId'] + '/lyrics/lines?branchId=primary')
        self.check(replay_lines['lines'][0]['tokens'][0]['time']['startUs'] == '1000', 'adoption: older job does not overwrite newer lyric edit')
        noop_receipt = self.metadata('same-result-noop')
        noop_revision = source['projectRevision']
        noop = self.expect_no_change('same-result-noop', source, lambda: self.apply('same-result-noop', source, terminal, receipt=noop_receipt))
        self.check(not noop['changed'] and noop['changeSet']['areas'] == [], 'adoption: full canonical equality is no-op')
        self.check(self.apply('same-result-noop-retry', source, terminal, receipt=noop_receipt) == noop, 'receipt: no-op response replays exactly')
        self.command_for('undo-adoption', source, {'type': 'history.undo'})
        self.expect_analysis('undo-restores-null', source)
        self.expect_no_change('old-noop-receipt-after-undo', source, lambda: self.check(
            self.apply('old-noop-receipt-after-undo', source, terminal, receipt=noop_receipt, revision=noop_revision) == noop,
            'receipt: replayed old no-op does not adopt after a later undo'))
        empty = self.expect_no_change('empty-clear-keeps-redo', source, lambda: self.command_for('empty-clear-keeps-redo', source, {'type': 'audio.analysis.clear'}))
        self.check(not empty['changed'] and empty['canRedo'], 'clear: empty no-op preserves redo')
        self.command_for('redo-adoption', source, {'type': 'history.redo'})
        self.expect_analysis('redo-restores-complete', source, terminal)
        self.command_for('temporary-lyric-edit', source, {'type': 'lyrics.setTimeRange', 'target': target, 'startUs': '2000', 'endUs': '1000000'})
        self.refresh('temporary-edit-summary', source)
        self.command_for('undo-temporary-lyric-edit', source, {'type': 'history.undo'})
        self.expect_analysis('analysis-still-same-with-redo', source, terminal)
        noop_redo = self.expect_no_change('same-analysis-keeps-redo', source, lambda: self.apply('same-analysis-keeps-redo', source, terminal))
        self.check(not noop_redo['changed'] and noop_redo['canRedo'], 'adoption: same analysis no-op preserves unrelated lyric redo')
        self.static_rejected('adopted-static-rejected', source)
        handle, saved = self.save('save-adopted', source, 'adopted-package')
        package = self.output / 'projects/adopted-package'
        stored = package / 'data/analysis' / (terminal['analysisResult']['contentHash'] + '.json')
        self.check(stored.read_bytes() == canonical, 'save: complete original canonical analysis bytes preserved in package')
        self.package_handle = handle
        self.expect_no_change('nonanalysis-job', source, lambda: self.apply('nonanalysis-job', source, saved, status=422, code='commandRejected'))
        self.corrupted('result', source, terminal, self.output / 'state/analysis-results' / (terminal['analysisResult']['report']['reportId'] + '.json'), canonical)
        candidates = [path for path in (self.output / 'resources').rglob('*') if path.is_file() and path.stat().st_size == len(wav.read_bytes()) and path.read_bytes() == wav.read_bytes()]
        self.check(len(candidates) == 1, 'audio corruption: exact owned resource identified by complete bytes')
        self.corrupted('audio', source, terminal, candidates[0], wav.read_bytes())
        self.other_session = self.create('same-audio-other-session', audio)
        self.expect_no_change('other-session', self.other_session, lambda: self.apply('other-session', self.other_session, terminal, status=422, code='commandRejected'))
        other = self.register('foreign-client', select=False)
        self.command_for('foreign-session', source, {'type': 'audio.analysis.apply', 'analysisJobId': terminal['jobId']}, client=other['clientId'], status=404, code='sessionNotFound')
        old_client = self.client
        self.client = other['clientId']
        self.lyrics = self.lyrics_upload('foreign-lyrics')
        foreign_source = self.create('foreign-project', self.upload('foreign-audio', wav))
        self.expect_no_change('foreign-job', foreign_source, lambda: self.apply('foreign-job', foreign_source, terminal, status=404, code='jobNotFound'))
        self.client = old_client
        self.lyrics = self.lyrics_upload('owner-lyrics-restored')
        ro = self.open('readonly-open', handle, readonly=True)
        self.expect_no_change('readonly-apply', ro, lambda: self.apply('readonly-apply', ro, terminal, status=403, code='readOnlySession'))
        self.expect_no_change('readonly-clear', ro, lambda: self.command_for('readonly-clear', ro, {'type': 'audio.analysis.clear'}, status=403, code='readOnlySession'))
        self.close('readonly-close', ro)
        self.failure_cases()
        self.close('close-adopted', source)
        reopened = self.open('reopen-adopted', handle)
        self.expect_analysis('reopen-complete-summary', reopened, terminal)
        history = self.state('reopen-fresh-history', reopened)['history']
        self.check(history['undoCount'] == history['redoCount'] == 0 and not reopened['isDirty'], 'reopen: complete persisted analysis with clean new session history')
        self.check(stored.read_bytes() == canonical, 'reopen: package keeps full canonical bytes independently of job sidecar')
        self.command_for('clear-reopened', reopened, {'type': 'audio.analysis.clear'})
        self.expect_analysis('cleared-reopened', reopened)
        self.command_for('undo-clear', reopened, {'type': 'history.undo'})
        self.expect_analysis('undo-clear-exact', reopened, terminal)
        self.command_for('redo-clear', reopened, {'type': 'history.redo'})
        self.expect_analysis('redo-clear-null', reopened)
        restored_handle, _ = self.save('save-cleared', reopened, 'cleared-package')
        self.check(json.loads((self.output / 'projects/cleared-package/project.json').read_text()).get('audioAnalysis') == {'state': 'notAnalyzed'},
            'save cleared: original schema28 explicitly records notAnalyzed without accepted file/revision reference')
        self.restore_static(reopened, self.output / 'projects/cleared-package')
        self.close('close-cleared', reopened)
        cleared_open = self.open('reopen-cleared', restored_handle)
        self.expect_analysis('reopen-cleared-null', cleared_open)
        self.adoptions.append({'jobId': terminal['jobId'], 'sourceRevision': terminal['analysisInput']['sourceRevision'], 'appliedRevision': accepted['projectRevision'],
            'completeCanonicalBytes': len(canonical), 'contentHash': terminal['analysisResult']['contentHash'], 'savedReopenedCanonicalEqual': True})

    def failure_cases(self):
        source = self.other_session
        running = self.start_analysis('controlled-running', source)
        _, pid = self.active('controlled-running', running)
        self.signal_owned(pid, signal.SIGSTOP)
        try:
            self.expect_no_change('running-job', source, lambda: self.apply('running-job', source, running, status=422, code='commandRejected'), concurrent_job_events=True)
            queued = self.start_analysis('controlled-queued', source)
            self.expect_no_change('queued-job', source, lambda: self.apply('queued-job', source, queued, status=422, code='commandRejected'), concurrent_job_events=True)
            self.cancel('cancel-owned-queued', queued)
            cancelled = self.wait_job('cancel-owned-queued', queued, 'cancelled')
            self.expect_no_change('cancelled-job', source, lambda: self.apply('cancelled-job', source, cancelled, status=422, code='commandRejected'), concurrent_job_events=True)
        finally:
            if pid in self.live():
                self.signal_owned(pid, signal.SIGCONT)
        self.wait_job('controlled-running-completed', running)
        launcher = self.worker_launcher.read_bytes()
        self.worker_launcher.unlink()
        try:
            failed = self.wait_job('real-worker-unavailable', self.start_analysis('real-worker-unavailable', source), 'failed')
            self.expect_no_change('failed-job', source, lambda: self.apply('failed-job', source, failed, status=422, code='commandRejected'))
        finally:
            self.worker_launcher.write_bytes(launcher)
            self.worker_launcher.chmod(0o700)
        wav = self.fixture('stereo-tail', 44100, 2)
        audio = self.upload('stereo-audio', wav)
        stereo = self.create('stereo-project', audio)
        metadata, raw = self.oracle_run('stereo', wav)
        terminal = self.wait_job('stereo-analysis', self.start_analysis('stereo-analysis', stereo))
        self.result('stereo-complete', terminal, raw, metadata)
        self.apply('stereo-apply', stereo, terminal)
        self.expect_analysis('stereo-adopted', stereo, terminal)
        self.comparisons.append({'name': 'stereo-tail', 'canonicalBytesEqual': True, 'metadata': metadata})

    def runtime_frame(self, name, package, time_us, transparent):
        prefix = self.output / name
        result = subprocess.run(self.runtime_oracle + [str(package), str(prefix), str(time_us), '320', '180', str(transparent).lower()], capture_output=True, timeout=40)
        (self.output / (name + '.stdout.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': independent original Runtime exits0')
        return Path(str(prefix) + '.bgra').read_bytes()

    def restore_static(self, source, package, expected_profile='static-lyrics-scene-v1', keep_preview=False):
        created = self.mutate('clear-restores-preview', '/api/v1/preview-sessions',
            {'source': {'kind': 'project', 'sourceId': source['projectSessionId'], 'sourceRevision': source['projectRevision']},
             'size': {'width': 320, 'height': 180}, 'alphaMode': 'straight'}, status=201)
        preview = created.get('session', created)
        path = '/api/v1/preview-sessions/' + preview['previewId']
        self.mutate('restored-frame-request', path + '/frame-requests', {'expectedSourceRevision': source['projectRevision'],
            'previewGeneration': preview['previewGeneration'], 'requestSeq': '1', 'timeUs': '200000', 'intent': 'playback'}, status=202)
        deadline = time.monotonic() + 30
        frame = None
        while time.monotonic() < deadline:
            self.capture()
            state = self.request('restored-preview-poll', 'GET', path)
            state = state.get('session', state)
            if state.get('latestFrame'):
                frame = state['latestFrame']
                break
            self.check(state.get('state') != 'failed', 'clear restores preview: actual runtime did not fail')
            time.sleep(.04)
        self.check(frame is not None, 'clear restores preview: actual PNG published')
        _, png = self.binary('restored-png', frame['content']['contentUrl'])
        self.check(len(png) == frame['content']['sizeBytes'] and hashlib.sha256(png).hexdigest() == frame['content']['sha256'], 'restored PNG: registered complete size and SHA exact')
        with Image.open(io.BytesIO(png)) as image:
            image.load()
            rgba = image.convert('RGBA').tobytes()
        expected = self.runtime_frame('restored-preview-oracle', package, 200000, True)
        self.check(len(rgba) == len(expected) == 320 * 180 * 4, 'restored preview: complete PNG and Runtime pixel counts match')
        premultiplied = bytearray(len(rgba))
        for offset in range(0, len(rgba), 4):
            alpha = rgba[offset + 3]
            for channel, source_channel in ((0, 2), (1, 1), (2, 0)):
                premultiplied[offset + source_channel] = (rgba[offset + channel] * alpha + 127) // 255
            premultiplied[offset + 3] = alpha
        self.check(premultiplied == expected, 'clear restores preview: every premultiplied BGRA byte equals independent original Runtime')
        HELPER.save_json(self.output / 'restored-preview-pixel-comparison.json', {'pixels': 320 * 180, 'maxPremultipliedRgbError': 0,
            'maxAlphaError': 0, 'pngEncoding': 'straight RGBA', 'runtimeEncoding': 'premultiplied BGRA',
            'independentRuntimeSha256': hashlib.sha256(expected).hexdigest(), 'convertedPngSha256': hashlib.sha256(premultiplied).hexdigest()})
        if not keep_preview:
            self.mutate('close-restored-preview', path + '/close', {'expectedPreviewGeneration': preview['previewGeneration'],
                'expectedSourceRevision': source['projectRevision']}, status=200)
        base = '/api/v1/project-sessions/' + source['projectSessionId']
        validation = self.wait_job('restored-validation', self.mutate('restored-validation', base + '/validation-jobs',
            {'expectedRevision': source['projectRevision'], 'framesPerOutput': 1}, status=202))
        _, validation_raw = self.binary('restored-validation-report', '/api/v1/jobs/' + validation['jobId'] + '/validation-result')
        report = json.loads(validation_raw)
        reference = validation['validationResult']['report']
        self.check(len(validation_raw) == reference['sizeBytes'] and hashlib.sha256(validation_raw).hexdigest() == reference['sha256'], 'restored validation: complete registered report size/SHA exact')
        self.check(validation['validationResult']['passed'] and report['succeeded'] and report['opaqueFrames'] == report['transparentFrames'] == 1
            and not any(value['isError'] for value in report['issues']), 'clear restores validation: actual ordinary and transparent frame reports pass')
        self.check(validation['input']['sourceRevision'] == source['projectRevision'] and validation['input']['sourceProfile'] == expected_profile, 'restored validation: exact revision and selected static profile')
        exported = self.wait_job('restored-export', self.mutate('restored-export', base + '/export-jobs',
            {'expectedRevision': source['projectRevision'], 'format': 'opaqueMp4', 'includeAudio': False}, status=202))
        _, media = self.binary('restored-export-content', '/api/v1/jobs/' + exported['jobId'] + '/export-content')
        _, export_report_raw = self.binary('restored-export-report', '/api/v1/jobs/' + exported['jobId'] + '/export-result')
        export_report = json.loads(export_report_raw)
        artifact, report_reference = exported['exportResult']['artifact'], exported['exportResult']['report']
        self.check(len(media) == artifact['sizeBytes'] and hashlib.sha256(media).hexdigest() == artifact['sha256'] == export_report['sha256'], 'restored export: complete media identity matches registered report and artifact')
        self.check(len(export_report_raw) == report_reference['sizeBytes'] and hashlib.sha256(export_report_raw).hexdigest() == report_reference['sha256'], 'restored export: complete report identity matches registered reference')
        self.check(export_report['strictlyVerified'] and export_report['expectedFrames'] == export_report['decodedFrames'] == export_report['framesWritten'] == 30
            and export_report['droppedFrames'] == export_report['failedFrames'] == 0 and not export_report['audioIncluded'], 'restored export: strict30frame pure-video report verified')
        self.check(exported['input']['sourceRevision'] == source['projectRevision'] and exported['input']['sourceProfile'] == expected_profile, 'restored export: exact revision and selected static profile')
        movie = self.output / 'restored-static.mp4'
        movie.write_bytes(media)
        probe = subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(movie)], capture_output=True, timeout=15)
        self.check(probe.returncode == 0, 'restored movie: real ffprobe exit0')
        streams = json.loads(probe.stdout)['streams']
        self.check(len(streams) == 1 and streams[0]['codec_type'] == 'video' and streams[0]['width'] == 320 and streams[0]['height'] == 180, 'restored movie: actual 320x180 pure video')
        decoded = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(movie), '-f', 'rawvideo', '-pix_fmt', 'rgb24', 'pipe:1'], capture_output=True, timeout=20)
        self.check(decoded.returncode == 0 and len(decoded.stdout) == 30 * 320 * 180 * 3, 'clear restores export: all30 real frames fully decode')
        opaque = self.runtime_frame('restored-export-oracle', package, 0, False)
        oracle_rgb = Image.frombytes('RGBA', (320, 180), opaque, 'raw', 'BGRA').convert('RGB').tobytes()
        first = decoded.stdout[:len(oracle_rgb)]
        differences = [abs(left - right) for left, right in zip(first, oracle_rgb)]
        self.check(sum(value > 16 for value in oracle_rgb) > 100, 'restored export oracle: rendered lyric content is nonempty')
        self.check(sum(differences) / len(differences) < 5 and sum(value > 40 for value in differences) / len(differences) < .02,
            'restored export: first decoded frame matches original Runtime within bounded lossy codec tolerance')
        self.restorations.append({'pngBytes': len(png), 'pngRuntimePremultipliedByteExact': True, 'pngMaxPremultipliedRgbError': 0, 'pngMaxAlphaError': 0, 'validationJobId': validation['jobId'],
            'exportJobId': exported['jobId'], 'movieBytes': len(media), 'decodedFrames': 30,
            'firstFrameMeanAbsoluteError': sum(differences) / len(differences), 'firstFrameFractionErrorOver40': sum(value > 40 for value in differences) / len(differences)})
        return {'preview': copy.deepcopy(preview), 'validation': copy.deepcopy(validation), 'export': copy.deepcopy(exported)}

    def extra_evidence_executables(self):
        return {}

    def restart(self):
        old_host = self.host
        self.stop_server('first-normal-shutdown')
        self.start_server('second-server')
        self.host = self.request('second-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.check(self.host != old_host, 'restart: new actual host identity')
        self.register('second-owner')
        recovered = self.request('registered-recovery-jobs', 'GET', '/api/v1/recovery-jobs')['jobs']
        found = [value for value in recovered if value['job']['jobId'] == self.success['jobId']]
        self.check(len(found) == 1 and found[0]['previousHostInstanceId'] == old_host, 'restart: original result only in registered previous-host ledger')
        self.result('previous-host-original-canonical', self.success, self.raw, recovered=True)
        handle = self.mutate('second-package-handle', '/api/v1/file-handles', {'rootGrantId': 'adoption-probe-root', 'relativePath': 'adopted-package'}, status=201)
        reopened = self.open('second-reopen-adopted', handle)
        self.expect_analysis('second-package-analysis', reopened, self.success)
        self.expect_no_change('previous-host-cannot-apply', reopened, lambda: self.apply('previous-host-cannot-apply', reopened, self.success, status=404, code='jobNotFound'))
        self.request('previous-host-not-live', 'GET', '/api/v1/jobs/' + self.success['jobId'], status=404, code='jobNotFound')
        self.command_for('second-clear-still-explicit', reopened, {'type': 'audio.analysis.clear'})
        self.expect_analysis('second-clear-null', reopened)

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False)
        (self.output / 'projects').mkdir()
        self.bootstrap = secrets.token_hex(32)
        launcher = ("#!/bin/sh\nexec '" + str(self.worker).replace("'", "'\\''") + "' \"$@\"\n").encode()
        self.worker_launcher.write_bytes(launcher)
        self.worker_launcher.chmod(0o700)
        self.environment = os.environ.copy()
        for key in ('DISPLAY', 'WAYLAND_DISPLAY', 'MAKO_BACKEND_FAKE_MEDIA', 'MAKO_BACKEND_CREDENTIAL_FILE'):
            self.environment.pop(key, None)
        self.environment.update(MAKO_BACKEND_BOOTSTRAP_TOKEN=self.bootstrap, MAKO_BACKEND_RESOURCE_ROOT=str(self.output / 'resources'),
            MAKO_BACKEND_STATE_ROOT=str(self.output / 'state'), MAKO_BACKEND_ALLOWED_ROOTS=json.dumps({'adoption-probe-root': str(self.output / 'projects')}),
            MAKO_BACKEND_CLIENT_TOKEN_SECONDS='3600', MAKO_BACKEND_CLIENT_LEASE_SECONDS='3600', MAKO_BACKEND_WORKER_PATH=str(self.worker_launcher),
            MAKO_BACKEND_FFMPEG_PATH='/usr/bin/ffmpeg', MAKO_BACKEND_FFPROBE_PATH='/usr/bin/ffprobe')
        failure = None
        try:
            dirty_product = subprocess.run(['git', 'diff', '--name-only', 'HEAD', '--', 'src'], cwd=ROOT, capture_output=True, text=True, check=True).stdout
            self.check(not dirty_product.strip(), 'formal run: tracked production sources frozen at committed endpoint')
            self.start_server('first-server')
            self.normal()
            self.restart()
        except Exception as error:
            failure = str(error)
            (self.output / 'failure.txt').write_text(traceback.format_exc())
        finally:
            if self.process and self.process.poll() is None:
                try:
                    self.stop_server('final-normal-shutdown')
                except Exception:
                    self.process.kill()
                    self.process.wait(timeout=5)
                    self.server_runs.append({'name': 'forced-cleanup', 'pid': self.process.pid, 'exitCode': self.process.returncode, 'forcedKill': True})
                    if failure is None:
                        failure = 'Server required forced cleanup'
            for pid in self.live():
                self.signal_owned(pid, signal.SIGKILL)
                if failure is None:
                    failure = 'Owned process survived normal Server shutdown'
            for path in (self.worker_launcher, self.output / 'resources/bootstrap.cap', self.output / 'resources/bootstrap.json'):
                path.unlink(missing_ok=True)
            workflow = len(self.assertions)
            for path in self.output.rglob('*'):
                if path.is_file():
                    data = path.read_bytes()
                    self.check(not any(value.encode() in data for value in (self.bootstrap, *self.credentials)), 'credential artifact scan: ' + str(path.relative_to(self.output)))
            source_paths = ['tools/backend-analysis-apply-http-probe/probe.py', 'tools/backend-analysis-http-probe/probe.py',
                'tools/backend-http-probe/probe.py', 'tools/backend-analysis-http-probe/RuntimeOracle/Program.cs', 'tools/backend-preview-http-probe/RuntimeOracle/Program.cs']
            source_paths.extend(self.EXTRA_EVIDENCE_SOURCES)
            executables = {'server': self.server_command[0], 'worker': str(self.worker), 'analysisOracle': self.oracle[0], 'renderOracle': self.runtime_oracle[0]}
            executables.update(self.extra_evidence_executables())
            deployment_files = {}
            for key, executable in executables.items():
                directory = Path(executable).resolve().parent
                deployment_files[key] = {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
                    for path in sorted(directory.rglob('*')) if path.is_file()}
            HELPER.save_json(self.output / 'deployment-sha256.json', deployment_files)
            HELPER.save_json(self.output / 'manifest.json', {'succeeded': failure is None, 'failure': failure, 'requestCount': self.request_count,
                'workflowAssertions': workflow, 'credentialScanAssertions': len(self.assertions) - workflow, 'assertionCount': len(self.assertions),
                'assertions': self.assertions, 'results': self.results, 'comparisons': self.comparisons, 'adoptions': self.adoptions, 'restorations': self.restorations,
                'serverRuns': self.server_runs, 'observedProcesses': list(self.processes.values()), 'survivingOwnedPids': self.live(),
                'gitHeadAtRun': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip(),
                'sourceSha256': {path: hashlib.sha256((ROOT / path).read_bytes()).hexdigest() for path in source_paths},
                'sourceLastCommit': {path: subprocess.run(['git', 'log', '-1', '--format=%H', '--', path], cwd=ROOT,
                    capture_output=True, text=True, check=True).stdout.strip() for path in source_paths},
                'deploymentSha256': {key: hashlib.sha256(Path(path).read_bytes()).hexdigest() for key, path in executables.items()},
                'deploymentTreeSha256': hashlib.sha256((self.output / 'deployment-sha256.json').read_bytes()).hexdigest(),
                'workerLauncherSha256': hashlib.sha256(launcher).hexdigest(),
                'boundaries': self.EVIDENCE_BOUNDARIES or ['Independent B4-D counts; no B4-C results or assertions copied into acceptance.',
                    'Real unthrottled original Analyzer complete canonical byte comparisons; no new analysis algorithm.',
                    'Static analysis inputs remain unsupported; one1second320x18030frame pure-video recovery clip only.',
                    'Default1h TTL and injected cleanup failures covered separately by controlled Hosting tests.',
                    'Previous-host result recovery is local profile-authorized read-only; no recovery apply.',
                    'No GUI, screenshots, long audio/video, full suite, performance or platform-manual acceptance.']})
        if failure:
            raise SystemExit(failure)
        print(json.dumps({'succeeded': True, 'requests': self.request_count, 'assertions': len(self.assertions)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--server', nargs='+', required=True)
    parser.add_argument('--worker', required=True)
    parser.add_argument('--oracle', nargs='+', required=True)
    parser.add_argument('--runtime-oracle', nargs='+', required=True)
    args = parser.parse_args()
    AdoptionProbe(args.output.resolve(), args.server, args.worker, args.oracle, args.runtime_oracle).run()
