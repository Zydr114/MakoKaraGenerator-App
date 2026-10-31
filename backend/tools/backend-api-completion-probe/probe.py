#!/usr/bin/env python3
"""Representative P1/P2 or incremental P3/P4 over real authenticated HTTP."""
import argparse
import base64
from fractions import Fraction
import hashlib
import importlib.util
import io
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import time
import wave
import zlib

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('basic_business', ROOT / 'tools/backend-basic-business-http-probe/probe.py')
basic = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(basic)
save_json = basic.save_json


class CompletionProbe(basic.BasicBusinessProbe):
    """Reuse BasicBusinessProbe -> OrchestrationProbe -> Smoke -> Probe lifecycle."""
    def __init__(self, output, dist, job_timeout, oracle=None, package_font=None, phase="p1p2"):
        super().__init__(output, dist)
        self.job_timeout = job_timeout
        self.deployment = {}
        self.last_heartbeat = 0
        self.flows = []
        self.oracle = oracle
        self.package_font = package_font
        self.phase = phase

    def request(self, name, method, path, **kwargs):
        # Three strict read-only POSTs deliberately have no receipt metadata.
        value = kwargs.get('value')
        if method == 'POST' and isinstance(value, dict) and path != '/api/v1/clients' and not (
                path.endswith('/commands') or path.endswith('/orchestration/measurements')
                or path in ('/api/v1/lyrics-import-plans', '/api/v1/fonts/inspect',
                    '/api/v1/effect-packages/inspect', '/api/v1/template-preview-options')):
            kwargs['value'] = {**value, 'metadata': value.get('metadata', self.metadata(name))}
        return basic.g20.OrchestrationProbe.request(self, name, method, path, **kwargs)

    def heartbeat(self, name, force=False):
        if force or time.monotonic() - self.last_heartbeat >= 30:
            self.request(name + '-heartbeat', 'POST', '/api/v1/clients/' + self.client + '/heartbeat')
            self.last_heartbeat = time.monotonic()

    def long_job(self, name, accepted, expected="succeeded"):
        # Same terminal polling as existing Probe.job; longer bounded wall time for
        # the actual full song, with production TTL kept alive through heartbeat.
        self.check(bool(accepted.get('jobId')), name + ': accepted job identity')
        deadline = time.monotonic() + self.job_timeout
        count = 0
        while time.monotonic() < deadline:
            self.heartbeat(name)
            count += 1
            current = self.request(name + '-poll-' + str(count), 'GET', '/api/v1/jobs/' + accepted['jobId'])
            if current['state'] in ('succeeded', 'failed', 'cancelled', 'unknown'):
                save_json(self.output / (name + '-terminal.json'), self.redact(current))
                self.check(current['state'] == expected, name + (': ' + expected + ' terminal' if current['state'] == expected else ': unexpected terminal: ' + json.dumps(self.redact(current))))
                self.check(not current['canCancel'], name + ': terminal cannot be cancelled')
                self.terminals.append({key: current[key] for key in ('jobId', 'kind', 'state')})
                return current
            time.sleep(1)
        raise AssertionError(name + ': no terminal within configured probe wall time')

    def settings(self, name):
        return self.request(name, 'GET', self.project_path + '/settings?expectedRevision=' + self.revision)

    def resources(self, name):
        return self.request(name, 'GET', self.project_path + '/resources?expectedRevision=' + self.revision)['resources']

    @staticmethod
    def image_bytes():
        # Original, deterministic RGB striped bitmap; no external asset or license.
        width, height = 64, 32
        rows = b''.join(b'\0' + b''.join(bytes((40 + x * 2, 30 + y * 3, 120)) for x in range(width)) for y in range(height))
        def chunk(kind, payload):
            return struct.pack('>I', len(payload)) + kind + payload + struct.pack('>I', zlib.crc32(kind + payload))
        return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows)) + chunk(b'IEND', b'')

    @staticmethod
    def song_audio_bytes():
        # Original 180s stereo PCM16 sample exceeds the historical 16MiB cap.
        second = b''.join(struct.pack('<hh', sample, -sample) for sample in
            (int(9000 * math.sin(2 * math.pi * 220 * i / 48000)) for i in range(48000)))
        output = io.BytesIO()
        with wave.open(output, 'wb') as stream:
            stream.setnchannels(2)
            stream.setsampwidth(2)
            stream.setframerate(48000)
            stream.writeframes(second * 180)
        return output.getvalue()

    def decode_png(self, name, png):
        self.check(png[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack('>II', png[16:24]) == (320, 180), name + ': PNG dimensions')
        result = subprocess.run(['ffmpeg', '-v', 'error', '-i', 'pipe:0', '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'],
                                input=png, capture_output=True, timeout=30)
        self.check(result.returncode == 0 and len(result.stdout) == 320 * 180 * 4, name + ': complete RGBA decode')
        (self.output / (name + '.png')).write_bytes(png)
        save_json(self.output / (name + '-decode.json'), {'pngSha256': hashlib.sha256(png).hexdigest(),
            'rgbaSha256': hashlib.sha256(result.stdout).hexdigest(), 'width': 320, 'height': 180})
        return result.stdout

    def frame(self, name, preview, time_us='500000', sequence=1, intent='playback'):
        path = '/api/v1/preview-sessions/' + preview['previewId']
        revision = preview['source']['sourceRevision']
        previous_generation = preview['previewGeneration']
        accepted = self.request(name + '-request', 'POST', path + '/frame-requests', status=202, value={
            'expectedSourceRevision': revision, 'previewGeneration': previous_generation,
            'requestSeq': str(sequence), 'timeUs': time_us, 'intent': intent})
        # seek returns a new generation; subsequent reads and close must use it.
        preview['previewGeneration'] = accepted['previewGeneration']
        self.check(accepted['previewId'] == preview['previewId'] and accepted['requestSeq'] == str(sequence)
            and accepted['timeUs'] == time_us and accepted['state'] == 'accepted', name + ': accepted frame identity')
        self.check(int(preview['previewGeneration']) > int(previous_generation) if intent == 'seek'
            else preview['previewGeneration'] == previous_generation, name + ': seek/playback generation')
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            state = self.request(name + '-state', 'GET', path)
            self.check(state['session']['state'] != 'failed', name + (': Worker frame pending/ready' if state['session']['state'] != 'failed' else ': Worker frame failed: ' + json.dumps(self.redact(state))))
            if state['session']['state'] == 'ready' and state['session'].get('latestFrame', {}).get('requestSeq') == str(sequence):
                frame = state['session']['latestFrame']
                self.check(frame['sourceRevision'] == revision and frame['previewGeneration'] == preview['previewGeneration'], name + ': fixed source/generation')
                _, data, _ = self.fetch(name + '-content', frame['content']['contentUrl'])
                self.check(len(data) == frame['content']['sizeBytes'] and hashlib.sha256(data).hexdigest() == frame['content']['sha256'], name + ': immutable PNG size/SHA')
                return self.decode_png(name, data)
            time.sleep(.15)
        raise AssertionError(name + ': frame never ready')

    def close_preview(self, name, preview):
        self.request(name + '-close', 'POST', '/api/v1/preview-sessions/' + preview['previewId'] + '/close', value={
            'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': preview['source']['sourceRevision']})

    def cancel_job(self, name, job_id):
        # Job cancellation consumes the metadata object directly; bypass the
        # normal POST envelope injector used by command-style requests.
        return basic.g20.smoke.existing.Probe.request(self, name, 'POST',
            '/api/v1/jobs/' + job_id + '/cancel', value=self.metadata(name))

    def project_preview(self, name):
        preview = self.request(name + '-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': self.revision},
            'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview'})
        try:
            return self.frame(name, preview)
        finally:
            self.close_preview(name, preview)

    def make_empty_project(self):
        created = self.request('empty-create', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': 'Original completion song',
            'composition': {'canvasWidth': 320, 'canvasHeight': 180, 'frameRateNumerator': 24,
                'frameRateDenominator': 1, 'outputDurationUs': 180000000}})
        self.project, self.revision = created['projectSessionId'], created['projectRevision']
        self.check(not created.get('audioAssetId') and not self.lyrics('empty-window')['lines'], 'direct public asset-free creation establishes empty workspace')
        self.flows.append({'name': 'empty-workspace', 'scope': 'Direct public create without audio/lyrics asset IDs; no seed or filesystem fixture injection.'})

    def p1_inputs(self):
        lyrics = ''.join(f'[{i // 6:02d}:{i % 6 * 10:02d}.00]<{i // 6:02d}:{i % 6 * 10:02d}.00>Original phrase {i + 1:02d}\n' for i in range(18)).encode()
        asset = self.upload('song-lyrics-upload', 'original-song.lrc', 'text/plain', lyrics)
        plan = self.request('lyrics-import-plan', 'POST', '/api/v1/lyrics-import-plans', value={
            'assetId': asset['assetId'], 'options': {'importerId': 'lrc.enhanced', 'encodingName': 'utf-8'}})
        self.check(not plan.get('preparationError') and plan['report']['lineCount'] == len(plan['previewLines']) == 18, 'complete import report and source preview')
        self.check(any(item['importerId'] == 'lrc.enhanced' for item in plan['formatCandidates']), 'real enhanced LRC detection candidate')
        imported = self.send('song-import', {'type': 'lyrics.importAsset', 'assetId': asset['assetId'],
            'destination': {'kind': 'existing', 'branchId': 'primary'}, 'options': {'importerId': 'lrc.enhanced', 'encodingName': 'utf-8'}})
        self.check(imported['importResult']['lineCount'] == 18, 'actual import complete source')
        for index, line in enumerate(self.lyrics('imported-window')['lines']):
            self.check(line['text'] == f'Original phrase {index + 1:02d}', 'original authored lyric text ' + str(index))
            start, end = index * 10000000, (index + 1) * 10000000
            self.send('line-time-' + str(index), {'type': 'lyrics.setTimeRange', 'target': {'branchId': 'primary', 'lineId': line['id']}, 'startUs': str(start), 'endUs': str(end)})
            self.check(bool(line['tokens']), 'real imported tokens')
            count = len(line['tokens'])
            self.send('token-times-' + str(index), {'type': 'lyrics.setTokenTimes', 'changes': [
                {'target': {'branchId': 'primary', 'lineId': line['id'], 'tokenId': token['id']},
                 'startUs': str(start + 10000000 * j // count), 'endUs': str(start + 10000000 * (j + 1) // count)}
                for j, token in enumerate(line['tokens'])]})
        audio = self.song_audio_bytes()
        self.check(len(audio) > 16 * 1024 * 1024, 'original song audio exceeds historical 16MiB upload cap')
        self.song_audio = self.upload('song-audio-upload', 'original-song.wav', 'audio/wav', audio)
        self.send('song-audio-replace', {'type': 'audio.replaceAsset', 'assetId': self.song_audio['assetId']})
        waveform = self.request('song-waveform', 'GET', self.project_path + '/waveform')
        self.check(waveform['available'] and int(waveform['durationUs']) == 180000000 and int(waveform['sampleCount']) == 180 * 48000
            and waveform['sampleRate'] == 48000 and waveform['sourceChannels'] == 2, 'real managed 180-second stereo audio preparation')
        self.picture = self.image_bytes()
        self.image = self.upload('background-upload', 'original-stripes.png', 'image/png', self.picture)
        self.send('background-set', {'type': 'composition.setBackgroundAsset', 'assetId': self.image['assetId'], 'kind': 'image', 'fit': 'contain', 'playback': 'loop'})
        self.send('background-fit', {'type': 'composition.setBackgroundFit', 'fit': 'cover'})
        self.check(self.settings('background-settings')['background']['fit'] == 'cover', 'background fit readback')
        resource = next(item for item in self.resources('background-resources') if 'project.background' in item['roles'])
        self.fetch('background-source-content', resource['contentUrl'], expected=self.picture)
        fonts = self.request('actual-fonts', 'GET', '/api/v1/fonts')
        self.check(bool(fonts['families']) and bool(fonts['environment']), 'actual Worker font catalogue')
        family = 'DejaVu Sans' if 'DejaVu Sans' in fonts['families'] else fonts['families'][0]
        inspected = self.request('actual-font-inspect', 'POST', '/api/v1/fonts/inspect', value={
            'text': 'Original phrase 01', 'family': family, 'fontSizePx': 32, 'fontWeight': 400, 'italic': False,
            'fallbackFamilies': None, 'sessionId': self.project, 'expectedRevision': self.revision})
        self.check(inspected['environment'] == fonts['environment'] and inspected['requestedAvailable'] and bool(inspected['resolvedFamily']), 'font inspection binds real Worker environment')
        self.flows.append({'name': 'p1-inputs', 'durationUs': 180000000, 'lineCount': 18, 'fontEnvironment': fonts['environment'], 'fontFamily': inspected['resolvedFamily']})

    def p1_candidate(self):
        before = self.request('candidate-before-summary', 'GET', self.project_path)
        history = self.request('candidate-before-history', 'GET', self.project_path + '/history')
        settings = self.settings('candidate-before-settings')
        edits = [{'type': 'composition.clearBackground'}, {'type': 'composition.setBackgroundColor', 'red': 20, 'green': 40, 'blue': 80, 'alpha': 255},
                 {'type': 'project.rename', 'name': 'Original completion song edited'}]
        preview = self.request('candidate-create', 'POST', self.project_path + '/edit-previews', status=201, value={
            'expectedRevision': self.revision, 'edits': edits, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque'})
        candidate = preview['editCandidate']
        self.check(candidate['baseProjectRevision'] == self.revision and candidate['candidateRevision'] == preview['source']['sourceRevision'], 'candidate fixed base/source revision')
        self.check([r['type'] for r in candidate['editResults']] == [e['type'] for e in edits], 'complete ordered per-edit feedback')
        try:
            rgba = self.frame('candidate-frame', preview)
            measured = self.request('candidate-measurement', 'POST', self.project_path + '/orchestration/measurements', value={
                'expectedRevision': self.revision, 'mode': 'time', 'timeUs': '500000', 'width': 320, 'height': 180,
                'alphaMode': 'opaque', 'previewId': preview['previewId'], 'expectedPreviewGeneration': preview['previewGeneration'],
                'candidateRevision': candidate['candidateRevision']})
            self.check(measured['projectRevision'] == candidate['candidateRevision'], 'measurement binds exact candidate revision')
            data = measured['measurement']
            png = base64.b64decode(data['png'], validate=True)
            self.check(hashlib.sha256(png).hexdigest() == data['pngSha256'] and bool(data['elements']), 'measurement complete PNG SHA and geometry')
            self.check(rgba == self.decode_png('candidate-measured', png), 'candidate preview/measurement same-time full RGBA equality')
            self.check(rgba[:4] == bytes((20, 40, 80, 255)), 'candidate Color background survives complete measurement path')
            after = self.request('candidate-after-summary', 'GET', self.project_path)
            # Preview events advance the client-wide event cursor without editing the project.
            self.check({key: value for key, value in before.items() if key != 'eventCursor'}
                == {key: value for key, value in after.items() if key != 'eventCursor'},
                'candidate preview and measurement preserve every project summary field except event cursor')
            self.check(history == self.request('candidate-after-history', 'GET', self.project_path + '/history'), 'candidate preview and measurement preserve full history')
        finally:
            self.close_preview('candidate', preview)
        applied = self.send('apply-edits', {'type': 'project.applyEdits', 'edits': edits})
        self.check(len(applied['editResults']) == len(edits), 'atomic apply preserves all edit feedback')
        expected = self.settings('applied-settings')
        self.send('apply-undo', {'type': 'history.undo'})
        undone = self.settings('undone-settings')
        self.check(all(undone[key] == value for key, value in settings.items() if key != 'projectRevision'), 'single undo restores all applied settings including image')
        self.send('apply-redo', {'type': 'history.redo'})
        redone = self.settings('redone-settings')
        self.check(all(redone[key] == value for key, value in expected.items() if key != 'projectRevision'), 'single redo restores all applied settings')
        self.send('restore-image', {'type': 'composition.setBackgroundAsset', 'assetId': self.image['assetId'], 'kind': 'image', 'fit': 'cover', 'playback': 'loop'})
        self.flows.append({'name': 'p1-candidate', 'measurementSourceProfile': data['sourceProfile'], 'rgbaEqual': True,
            'scope': 'Image remains in base project; Color candidate is measured. No claim of Image measurement support or independent Runtime oracle.'})

    def p1_persistence(self):
        before = self.settings('before-save-settings')
        lines = self.lyrics('before-save-lyrics')['lines']
        self.handle = self.request('save-handle', 'POST', '/api/v1/file-handles', status=201, value={
            'rootGrantId': 'projects', 'relativePath': 'completion-song'})['handleId']
        self.save_close('p1')
        opened = self.open('p1-reopen')
        restored = self.settings('after-reopen-settings')
        self.check(not opened['isDirty'], 'saved project reopens clean')
        self.check(all(restored[key] == value for key, value in before.items() if key not in ('projectRevision', 'projectSessionId')), 'complete settings survive save-close-open')
        self.check(self.lyrics('after-reopen-lyrics')['lines'] == lines, 'complete timed lyrics survive save-close-open')
        resource = next(item for item in self.resources('reopened-resources') if 'project.background' in item['roles'])
        self.fetch('reopened-background-content', resource['contentUrl'], expected=self.picture)
        rgba = self.project_preview('reopened-background')
        self.check(rgba[2] == 120 and set(rgba[3::4]) == {255}, 'reopened background frame contains authored image and opaque alpha')
        self.flows.append({'name': 'p1-persistence', 'backgroundSha256': hashlib.sha256(self.picture).hexdigest()})

    def verify_media(self, name, terminal, duration, fmt, quality):
        path = '/api/v1/jobs/' + terminal['jobId']
        refs = terminal['exportResult']
        _, raw, _ = self.fetch(name + '-report', path + '/export-result')
        self.check(len(raw) == refs['report']['sizeBytes'] and hashlib.sha256(raw).hexdigest() == refs['report']['sha256'], name + ': immutable report identity')
        report = json.loads(raw)
        frames = duration * 24
        self.check(report['expectedFrames'] == report['decodedFrames'] == report['framesWritten'] == frames
                   and report['failedFrames'] == report['droppedFrames'] == 0 and report['strictlyVerified'], name + ': actual complete strict frame plan')
        self.check(report['requestedQuality'] == report['actualQuality'] == quality and report['format'] == fmt and report['audioIncluded'], name + ': requested/actual quality and audio')
        _, media, _ = self.fetch(name + '-content', path + '/export-content')
        self.check(len(media) == refs['artifact']['sizeBytes'] == report['mediaSize'] and hashlib.sha256(media).hexdigest() == refs['artifact']['sha256'] == report['sha256'], name + ': actual downloadable media identity')
        file = self.output / (name + ('.mp4' if fmt == 'opaqueMp4' else '.mov'))
        file.write_bytes(media)
        command = ['ffprobe', '-v', 'error', '-count_frames', '-show_streams', '-show_format', '-of', 'json', str(file)]
        observed = subprocess.run(command, capture_output=True, timeout=90)
        self.check(observed.returncode == 0, name + ': ffprobe complete media read')
        independent = json.loads(observed.stdout)
        save_json(self.output / (name + '-ffprobe.json'), independent)
        video = next(s for s in independent['streams'] if s['codec_type'] == 'video')
        audio = next(s for s in independent['streams'] if s['codec_type'] == 'audio')
        self.check(int(video['nb_read_frames']) == frames and Fraction(video['r_frame_rate']) == 24 and video['width'] == 320 and video['height'] == 180, name + ': ffprobe actual frame count/rate/dimensions')
        self.check(abs(float(video['duration']) - duration) <= .05 and abs(float(audio['duration']) - duration) <= .1, name + ': actual video/audio duration')
        self.check(video['codec_name'] == ('h264' if fmt == 'opaqueMp4' else 'prores') and audio['codec_name'] == ('aac' if fmt == 'opaqueMp4' else 'pcm_s16le'), name + ': actual video/audio codecs')
        self.check(int(report['derivedVideoDurationUs']) == duration * 1000000 and abs(int(report['actualVideoDurationUs']) - duration * 1000000) <= 50000, name + ': report/ffprobe duration agreement')
        if fmt != 'opaqueMp4':
            decoded = subprocess.run(['ffmpeg', '-v', 'error', '-i', str(file), '-map', '0:v:0', '-ss', '0.5', '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'], capture_output=True, timeout=30)
            self.check(decoded.returncode == 0 and len(decoded.stdout) == 320 * 180 * 4 and min(decoded.stdout[3::4]) == 0 and max(decoded.stdout[3::4]) > 0, name + ': actual transparent and visible sampled pixels')
        self.artifacts[name] = {'jobId': terminal['jobId'], 'frames': frames, 'seconds': duration, 'quality': quality,
            'format': fmt, 'sha256': report['sha256'], 'sizeBytes': report['mediaSize'], 'videoCodec': video['codec_name'], 'audioCodec': audio['codec_name']}

    def cancel_representative_export(self):
        name = 'full-song-cancel'
        accepted = self.request(name + '-export', 'POST', self.project_path + '/export-jobs', status=202,
            value={'expectedRevision': self.revision, 'format': 'opaqueMp4', 'includeAudio': True, 'quality': 'standard'})
        deadline = time.monotonic() + 60
        workers = []
        while time.monotonic() < deadline:
            self.observe(self.current_ready['pid'])
            current = self.request(name + '-active', 'GET', '/api/v1/jobs/' + accepted['jobId'])
            workers = []
            for pid, ticks in list(self.observed):
                try:
                    stat = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
                    if stat[19] == ticks and Path(f'/proc/{pid}/exe').resolve().name == 'MakoKaraGenerator.Backend.Worker':
                        workers.append({'pid': pid, 'startTicks': ticks})
                except (OSError, IndexError):
                    pass
            if current['state'] == 'running' and workers:
                break
            self.check(current['state'] not in ('succeeded', 'failed', 'cancelled', 'unknown'), name + ': active before cancellation')
            time.sleep(.05)
        self.check(current['state'] == 'running' and bool(workers), name + ': running export and actual live Worker observed')
        save_json(self.output / (name + '-workers.json'), workers)
        # Cancellation consumes metadata directly, so bypass the envelope wrapper.
        basic.g20.smoke.existing.Probe.request(self, name + '-request', 'POST',
            '/api/v1/jobs/' + accepted['jobId'] + '/cancel', value=self.metadata(name + '-cancel'))
        terminal = self.long_job(name, accepted, expected='cancelled')
        self.check(terminal.get('exportResult') is None and terminal.get('failureReportRef') is None,
            name + ': cancelled export publishes no media or report')
        self.request(name + '-no-report', 'GET', '/api/v1/jobs/' + accepted['jobId'] + '/export-result',
            status=404, code='exportArtifactNotFound')

    def p2_exports(self):
        capabilities = self.request('export-capabilities', 'GET', '/api/v1/export-capabilities')
        self.check(capabilities['enabled'] and set(capabilities['qualities']) == {'draft', 'standard', 'high'}, 'actual quality-enabled export deployment')
        for field in ('maxFrames', 'maxDurationUs', 'maxMediaBytes', 'maxAudioBytes', 'maxCompressedAudioDurationUs', 'operationTimeoutSeconds', 'maxArtifactStorageBytes'):
            self.check(capabilities.get(field) is None, 'default export policy has no deployment limit: ' + field)
        self.cancel_representative_export()
        # Full song consumes the actual saved image closure and the full audio.
        for name, duration, fmt, quality in [('full-song', 180, 'opaqueMp4', 'standard'),
                ('transparent-short', 2, 'transparentProRes4444', 'standard'),
                ('draft-short', 2, 'opaqueMp4', 'draft'), ('high-short', 2, 'opaqueMp4', 'high')]:
            self.heartbeat(name, force=True)
            if duration == 2 and name == 'transparent-short':
                self.send('short-duration', {'type': 'composition.setOutputDuration', 'durationUs': '2000000'})
                self.send('transparent-clear-image', {'type': 'composition.clearBackground'})
                self.send('transparent-base', {'type': 'composition.setBackgroundColor', 'red': 0, 'green': 0, 'blue': 0, 'alpha': 0})
            accepted = self.request(name + '-export', 'POST', self.project_path + '/export-jobs', status=202,
                value={'expectedRevision': self.revision, 'format': fmt, 'includeAudio': True, 'quality': quality})
            self.verify_media(name, self.long_job(name, accepted), duration, fmt, quality)
        self.flows.append({'name': 'p2-exports', 'scope': '180-second original song/image/audio MP4; short transparent and draft/high. Independent ffprobe media read, report identity; no independent Runtime content oracle or performance matrix.'})

    def oracle_run(self, name, arguments):
        observed = subprocess.run(['dotnet', str(self.oracle), *map(str, arguments)], capture_output=True, text=True, timeout=120)
        (self.output / (name + '-oracle.log')).write_text(observed.stdout + observed.stderr)
        self.check(observed.returncode == 0, name + ': original fixture/Runtime oracle exits0')

    def runtime_measurement(self, name, mode, expected_rgba=None):
        value = {'expectedRevision': self.revision, 'mode': mode, 'timeUs': '250000' if mode == 'time' else None,
            'width': 320, 'height': 180, 'alphaMode': 'opaque'}
        response = self.request(name, 'POST', self.project_path + '/orchestration/measurements', value=value)
        self.check(response['projectRevision'] == self.revision, name + ': current fixed project revision')
        measured = response['measurement']
        self.check(measured['sourceProfile'] == 'runtime-project-input-v1' and bool(measured['elements']), name + ': complete runtime input and geometry')
        png = base64.b64decode(measured['png'], validate=True)
        self.check(hashlib.sha256(png).hexdigest() == measured['pngSha256'], name + ': immutable measured PNG SHA')
        rgba = self.decode_png(name, png)
        if expected_rgba is not None:
            self.check(rgba == expected_rgba, name + ': preview and measurement full same-time RGBA equality')
        return measured

    def runtime_inputs(self):
        if self.oracle is None:
            return
        archive = self.output / 'completion-advanced.mktpl'
        self.oracle_run('advanced-template', ['completion-template', archive, self.package_font])
        original = json.loads(Path(str(archive) + '.json').read_text())
        self.check(original['fontBytes'] > 0 and original['fontFamily'].startswith('Mako')
            and original['fontSha256'] != original['originalFontSha256'] and original['seed'] == str(2 ** 64 - 1),
            'unique package font bytes and advanced recipe preserve complete fixed input')
        asset = self.upload('advanced-template-upload', archive.name, 'application/octet-stream', archive.read_bytes())
        preflight = self.request('advanced-template-preflight', 'POST', '/api/v1/template-libraries/personal/imports/preflight', status=201, value={'assetId': asset['assetId']})
        personal = self.request('advanced-personal-before', 'GET', '/api/v1/template-libraries/personal/templates')
        imported = self.request('advanced-template-import', 'POST', '/api/v1/template-libraries/personal/imports', value={
            'expectedRevision': personal['revision'], 'preflightId': preflight['preflightId']})
        template = imported['importedTemplates'][0]
        first = self.lyrics('advanced-targets')['lines'][0]
        self.send('advanced-apply', {'type': 'presetApplications.apply', 'templateLibraryId': 'personal',
            'templateId': template['templateId'], 'templateRevision': imported['revision'],
            'targets': [{'kind': 'line', 'branchId': 'primary', 'lineId': first['id']}], 'branchInputs': {'vocals': 'primary'}})
        # Color + a real package font must use the runtime closure too.
        color = self.request('font-color-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': self.revision},
            'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview'})
        try:
            color_rgba = self.frame('font-color-frame', color, '250000')
            self.runtime_measurement('font-color-time', 'time', color_rgba)
        finally:
            self.close_preview('font-color', color)
        video = self.output / 'original-motion.mp4'
        generated = subprocess.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24:duration=2',
            '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-an', '-y', str(video)], capture_output=True, timeout=30)
        self.check(generated.returncode == 0 and video.is_file(), 'original two-second motion source generated')
        motion = self.upload('motion-upload', video.name, 'video/mp4', video.read_bytes())
        self.send('motion-background', {'type': 'composition.setBackgroundAsset', 'assetId': motion['assetId'],
            'kind': 'video', 'fit': 'cover', 'playback': 'loop'})
        self.send('motion-opaque-base', {'type': 'composition.setBackgroundColor', 'red': 0, 'green': 0, 'blue': 0, 'alpha': 255})
        reference_handle = self.request('motion-reference-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': 'runtime-reference'})['handleId']
        self.long_job('motion-reference-save', self.request('motion-reference-save', 'POST', self.project_path + '/save-jobs', status=202,
            value={'expectedRevision': self.revision, 'targetHandleId': reference_handle}))
        reference = self.output / 'profile/projects/runtime-reference'
        oracle_prefix = self.output / 'motion-reference'
        self.oracle_run('motion-reference', ['render', reference, oracle_prefix, 320, 180, False, '250000,1250000,250000', 'none'])
        preview = self.request('motion-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': self.revision},
            'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview'})
        try:
            frames = [self.frame('motion-' + str(sequence), preview, str(time_us), sequence, intent)
                for sequence, (time_us, intent) in enumerate(
                    ((250000, 'playback'), (1250000, 'playback'), (250000, 'seek')), 1)]
            for index, pixels in enumerate(frames):
                expected = self.decode_png('motion-oracle-' + str(index), Path(str(oracle_prefix) + '-' + str(index) + '.png').read_bytes())
                self.check(pixels == expected, 'video advanced preset/font frame matches original Runtime complete RGBA ' + str(index))
            self.check(frames[0] != frames[1] and frames[0] == frames[2], 'real video advances and backward seek reproduces complete original frame')
            self.runtime_measurement('motion-time', 'time', frames[0])
            self.runtime_measurement('motion-layout', 'layout')
        finally:
            self.close_preview('motion', preview)
        accepted = self.request('motion-validation', 'POST', self.project_path + '/validation-jobs', status=202,
            value={'expectedRevision': self.revision, 'framesPerOutput': 2})
        terminal = self.long_job('motion-validation', accepted)
        refs = terminal['validationResult']['report']
        _, raw, _ = self.fetch('motion-validation-report', '/api/v1/jobs/' + terminal['jobId'] + '/validation-result')
        report = json.loads(raw)
        self.check(len(raw) == refs['sizeBytes'] and hashlib.sha256(raw).hexdigest() == refs['sha256']
            and report['succeeded'] and report['opaqueFrames'] == report['transparentFrames'] == 2, 'video advanced preset font validation completes actual 2+2 Runtime frames')
        accepted = self.request('motion-export', 'POST', self.project_path + '/export-jobs', status=202,
            value={'expectedRevision': self.revision, 'format': 'opaqueMp4', 'includeAudio': True, 'quality': 'standard'})
        self.verify_media('motion-export', self.long_job('motion-export', accepted), 2, 'opaqueMp4', 'standard')
        self.flows.append({'name': 'runtime-inputs', 'font': original, 'videoSha256': hashlib.sha256(video.read_bytes()).hexdigest(),
            'scope': 'One Color+package-font advanced preset and one video+same preset/font representative; backward seek and three complete original Runtime frame comparisons, timed/layout measurement, 2+2 validation and 48-frame audio export. No full cross-product matrix.'})

    def run(self):
        deployment = json.loads((self.dist / 'deployment.json').read_text())
        self.deployment = {key: deployment.get(key) for key in ('version', 'sourceCommit', 'sourceDirty', 'rid', 'selfContained')}
        self.start('deployed')
        self.connect('deployed')
        self.check(self.current_ready['version'] == deployment['version'], 'matched launcher deployment version')
        self.make_empty_project()
        if self.phase in ('p3', 'p4'):
            workflow_name = self.phase + '_workflow.py'
            workflow_spec = importlib.util.spec_from_file_location('completion_' + self.phase, Path(__file__).with_name(workflow_name))
            workflow = importlib.util.module_from_spec(workflow_spec)
            workflow_spec.loader.exec_module(workflow)
            workflow.run(self)
            return
        self.p1_inputs()
        self.p1_candidate()
        self.p1_persistence()
        self.p2_exports()
        self.runtime_inputs()
        self.save_close('final')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--phase', choices=('p1p2', 'p3', 'p4'), default='p1p2')
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--job-timeout-seconds', type=int, default=1800, help='Probe wall time for each complete export; does not change Server policy')
    parser.add_argument('--oracle', type=Path, help='Existing image FixtureOracle built against the matched Worker; enables representative runtime combinations')
    parser.add_argument('--package-font', type=Path, help='Actual local font included in the generated owned .mktpl')
    args = parser.parse_args()
    if args.phase in ('p3', 'p4') and not args.oracle:
        parser.error('--phase ' + args.phase + ' requires --oracle with the p3-package fixture mode')
    if args.phase == 'p4' and args.package_font:
        parser.error('--phase p4 does not use --package-font')
    if args.phase not in ('p3', 'p4') and bool(args.oracle) != bool(args.package_font):
        parser.error('--oracle and --package-font must be supplied together')
    if args.job_timeout_seconds <= 0:
        parser.error('--job-timeout-seconds must be positive')
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    probe = CompletionProbe(output, args.dist.resolve(), args.job_timeout_seconds,
        args.oracle.resolve() if args.oracle else None, args.package_font.resolve() if args.package_font else None, args.phase)
    failure = None
    try:
        probe.run()
    except BaseException as error:
        failure = error
        save_json(output / 'failure.json', {'errorType': type(error).__name__, 'detail': str(error)})
    finally:
        try:
            probe.stop('deployed')
            if probe.current_ready is not None:
                probe.check(bool(probe.worker_identities), 'real Worker identities observed and drained')
        except BaseException as error:
            failure = failure or error
            save_json(output / 'shutdown-failure.json', {'errorType': type(error).__name__, 'detail': str(error)})
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        scanned = None
    summary = {'status': 'failed' if failure else 'passed', 'errorType': type(failure).__name__ if failure else None,
        'httpRequests': probe.request_count, 'assertions': len(probe.assertions), 'credentialScannedFiles': scanned,
        'deploymentManifestSha256': hashlib.sha256((probe.dist / 'deployment.json').read_bytes()).hexdigest() if (probe.dist / 'deployment.json').is_file() else None,
        'deployment': probe.deployment, 'flows': probe.flows, 'jobTerminals': probe.terminals, 'artifacts': probe.artifacts, 'runs': probe.runs,
        'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(probe.worker_identities)],
        'assertionLabels': probe.assertions, 'phase': args.phase, 'scope': 'Finite real backend representative workflows for the selected phase; browser, cross-platform, arbitrary full input combinations and performance matrix remain separate.'}
    save_json(output / 'summary.json', summary)
    save_json(output / 'evidence-manifest.json', {'files': {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.rglob('*')) if p.is_file()}})
    print(json.dumps({k: summary[k] for k in ('status', 'httpRequests', 'assertions', 'errorType')}))
    return 1 if failure else 0


if __name__ == '__main__':
    sys.exit(main())
