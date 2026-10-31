#!/usr/bin/env python3
"""B4-E delta workflow; transport/process/static checks stay in accepted helpers."""
import argparse
import array
import copy
import hashlib
import importlib.util
import io
import json
import math
import pathlib
import shutil
import subprocess
import sys
import time
import wave

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('accepted_adoption_probe', ROOT / 'tools/backend-analysis-apply-http-probe/probe.py')
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)
OLD, ANALYSIS = 'static-lyrics-scene-v1', 'static-lyrics-scene-analysis-v1'


class RenderingProbe(BASE.AdoptionProbe):
    EXTRA_EVIDENCE_SOURCES = ('tools/backend-analysis-rendering-http-probe/probe.py',
        'tools/backend-analysis-rendering-http-probe/README.md', 'tools/backend-preview-http-probe/RuntimeOracle/RuntimeOracle.csproj')
    EVIDENCE_BOUNDARIES = ['Independent B4-E delta counts; no old acceptance counts copied.',
        'Original complete Analyzer canonical and original Runtime; effects disabled, no analysis-driven visuals.',
        'Snapshot SHA comparison normalizes only the owned declaration after complete package load; not unmodified package-path SHA or an audio IO grant.',
        'Three1second320x18030frame clips only; pure MP4, PCM16-source audio MP4, transparent pure MOV.',
        'Owned missing audio proves pure-video/static no-source-IO; old0.68 Worker mixed-version Server only checks export disabled.',
        'Canonical/total-budget/unsupported-profile corruption uses separate controlled codec/Host tests.',
        'No GUI/screenshots/full suite/performance/long media/manual platform acceptance.']

    def __init__(self, output, server, worker, oracle, runtime_oracle, legacy_worker):
        super().__init__(output, server, worker, oracle, runtime_oracle)
        self.legacy_worker = pathlib.Path(legacy_worker).resolve()
        self.recovery_records = []

    def extra_evidence_executables(self):
        return {'legacyWorker': str(self.legacy_worker)}

    def runtime_frame(self, name, package, time_us, transparent):
        prefix = self.output / name
        result = subprocess.run(self.runtime_oracle + [str(package), str(prefix), str(time_us), '320', '180',
            str(transparent).lower(), str(self.declaration)], capture_output=True, timeout=40)
        (self.output / (name + '.stdout.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': original Runtime and declaration-normalized complete codec exit0')
        return pathlib.Path(str(prefix) + '.bgra').read_bytes()

    def source_evidence(self, name, job, prefix, profile):
        expected = json.loads((self.output / (prefix + '.source.json')).read_text())
        self.check(expected['audioDeclarationNormalized'] and expected['sourceProfile'] == profile
            and job['input']['sourceProfile'] == profile and job['input']['sourceSha256'] == expected['sourceSha256'],
            name + ': entire source SHA equals independent codec after explicit same-declaration normalization')
        if profile == ANALYSIS:
            self.check(expected['analysisContentHash'] == self.success['analysisResult']['contentHash']
                and (self.output / (prefix + '.analysis.json')).read_bytes() == self.raw,
                name + ': original complete analysis canonical bytes included without loss')

    def preview_frame(self, name, preview, revision, sequence, package, profile):
        path = '/api/v1/preview-sessions/' + preview['previewId']
        self.mutate(name + '-request', path + '/frame-requests', {'expectedSourceRevision': revision,
            'previewGeneration': preview['previewGeneration'], 'requestSeq': str(sequence), 'timeUs': '200000', 'intent': 'playback'}, status=202)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            self.capture(); state = self.request(name + '-poll', 'GET', path); state = state.get('session', state)
            frame = state.get('latestFrame')
            if frame and frame['requestSeq'] == str(sequence):
                break
            self.check(state['state'] != 'failed', name + ': real captured Runtime has not failed'); time.sleep(.04)
        else:
            raise AssertionError(name + ': no bounded real frame')
        self.check(frame['sourceRevision'] == revision, name + ': frame keeps captured source revision')
        _, raw = self.binary(name + '-png', frame['content']['contentUrl'])
        self.check(len(raw) == frame['content']['sizeBytes'] and hashlib.sha256(raw).hexdigest() == frame['content']['sha256'], name + ': exact PNG handle identity')
        rgba = Image.open(io.BytesIO(raw)).convert('RGBA').tobytes(); premul = bytearray(len(rgba))
        for offset in range(0, len(rgba), 4):
            for channel, target in ((0, 2), (1, 1), (2, 0)):
                premul[offset + target] = (rgba[offset + channel] * rgba[offset + 3] + 127) // 255
            premul[offset + 3] = rgba[offset + 3]
        expected = self.runtime_frame(name + '-oracle', package, 200000, True)
        self.check(premul == expected, name + ': all57600 premultiplied RGB and alpha pixels equal original Runtime')
        source = json.loads((self.output / (name + '-oracle.source.json')).read_text())
        self.check(source['sourceProfile'] == profile, name + ': independent selected source profile')
        return frame

    def extra_export(self, name, source, package, alpha=False, audio=False):
        fmt = 'transparentProRes4444' if alpha else 'opaqueMp4'
        job = self.wait_job(name, self.mutate(name, '/api/v1/project-sessions/' + source['projectSessionId'] + '/export-jobs',
            {'expectedRevision': source['projectRevision'], 'format': fmt, 'includeAudio': audio}, status=202))
        _, report_raw = self.binary(name + '-report', '/api/v1/jobs/' + job['jobId'] + '/export-result'); report = json.loads(report_raw)
        _, media = self.binary(name + '-content', '/api/v1/jobs/' + job['jobId'] + '/export-content')
        ref, artifact = job['exportResult']['report'], job['exportResult']['artifact']
        self.check(hashlib.sha256(report_raw).hexdigest() == ref['sha256'] and len(report_raw) == ref['sizeBytes'], name + ': complete registered report SHA/size')
        self.check(hashlib.sha256(media).hexdigest() == artifact['sha256'] == report['sha256'] and len(media) == artifact['sizeBytes'], name + ': complete registered movie SHA/size')
        self.check(report['strictlyVerified'] and report['expectedFrames'] == report['framesWritten'] == report['decodedFrames'] == 30
            and report['failedFrames'] == report['droppedFrames'] == 0 and report['audioIncluded'] == audio and report['hasAlpha'] == alpha,
            name + ': strict original30frame plan, alpha and audio flags')
        movie = self.output / (name + ('.mov' if alpha else '.mp4')); movie.write_bytes(media)
        inspected = subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(movie)], capture_output=True, timeout=15)
        self.check(inspected.returncode == 0, name + ': original ffprobe exits0'); streams = json.loads(inspected.stdout)['streams']
        videos, audios = [v for v in streams if v['codec_type'] == 'video'], [v for v in streams if v['codec_type'] == 'audio']
        self.check(len(videos) == 1 and len(audios) == int(audio) and videos[0]['width'] == 320 and videos[0]['height'] == 180
            and videos[0]['codec_name'] == ('prores' if alpha else 'h264'), name + ': actual video format and audio track presence')
        pixel = 'rgba' if alpha else 'rgb24'; channels = 4 if alpha else 3
        decoded = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(movie), '-f', 'rawvideo', '-pix_fmt', pixel, 'pipe:1'], capture_output=True, timeout=20)
        self.check(decoded.returncode == 0 and len(decoded.stdout) == 30 * 320 * 180 * channels, name + ': all30 frames completely decode')
        expected = self.runtime_frame(name + '-oracle', package, 0, alpha)
        oracle = Image.frombytes('RGBA', (320, 180), expected, 'raw', 'BGRA').convert('RGBA' if alpha else 'RGB').tobytes()
        first = bytearray(decoded.stdout[:len(oracle)]); alpha_error = 0
        if alpha:
            # The Runtime is premultiplied; decoded MOV is straight. Compare the same representation.
            for offset in range(0, len(first), 4):
                alpha_error = max(alpha_error, abs(first[offset + 3] - oracle[offset + 3]))
                for channel in range(3):
                    first[offset + channel] = (first[offset + channel] * first[offset + 3] + 127) // 255
        differences = [abs(left - right) for index, (left, right) in enumerate(zip(first, oracle)) if not alpha or index % 4 != 3]
        self.check(sum(differences) / len(differences) < 5 and sum(value > 40 for value in differences) / len(differences) < .02
            and alpha_error <= 1, name + ': original Runtime content within declared lossy RGB/alpha bounds')
        self.source_evidence(name, job, name + '-oracle', ANALYSIS)
        audio_facts = {}
        if audio:
            self.check(audios[0]['codec_name'] == report['audioCodec'] == 'aac' and abs(float(audios[0]['duration']) - 1) <= .05, name + ': real AAC track plan duration')
            decoded_audio = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(movie), '-map', '0:a:0', '-ar', '48000', '-ac', '1', '-f', 'f32le', 'pipe:1'], capture_output=True, timeout=15)
            self.check(decoded_audio.returncode == 0 and len(decoded_audio.stdout) >= 48_000 * 4, name + ': complete actual AAC samples decode')
            samples = array.array('f'); samples.frombytes(decoded_audio.stdout)
            if sys.byteorder != 'little': samples.byteswap()
            with wave.open(str(self.wav), 'rb') as original:
                pcm = array.array('h'); pcm.frombytes(original.readframes(48_000))
            if sys.byteorder != 'little': pcm.byteswap()
            left, right = [value / 32768 for value in pcm], samples[:48_000]
            mae = sum(abs(a - b) for a, b in zip(left, right)) / 48_000
            correlation = sum(a * b for a, b in zip(left, right)) / math.sqrt(sum(a * a for a in left) * sum(b * b for b in right))
            self.check(mae < .03 and correlation > .98, name + ': AAC decodes owned source PCM within explicit lossy audio bounds')
            audio_facts = {'decodedAudioSamples': len(samples), 'sourcePcmMeanAbsoluteError': mae, 'sourcePcmCorrelation': correlation}
        self.restorations.append({'name': name, 'sourceProfile': ANALYSIS, 'decodedFrames': 30, 'alphaMaxError': alpha_error,
            'meanRgbError': sum(differences) / len(differences), **audio_facts})
        self.recovery_records.append(copy.deepcopy(job)); return job

    def normal(self):
        self.host = self.request('health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']; self.register('owner')
        for route in ('runtime-info', 'validation-capabilities', 'export-capabilities'):
            value = self.request(route, 'GET', '/api/v1/' + route)
            self.check(value['enabled'] and value['sourceProfile'] == OLD and set(value['sourceProfiles']) == {OLD, ANALYSIS}, route + ': actual configured dual-profile capability')
        self.lyrics = self.lyrics_upload('lyrics'); self.wav = self.fixture('rendering-source', 48000, 1)
        audio = self.upload('audio', self.wav); source = self.create('complete-analysis-static', audio)
        matches = [path for path in (self.output / 'resources').rglob('*') if path.is_file() and path.read_bytes() == self.wav.read_bytes()]
        self.check(len(matches) == 1, 'owned audio: identify exact immutable declaration resource'); self.declaration = matches[0]
        meta, canonical = self.oracle_run('complete', self.wav)
        self.success = self.wait_job('analysis', self.start_analysis('analysis', source)); self.raw = self.result('canonical', self.success, canonical, meta)
        self.apply('explicit-adopt', source, self.success); self.expect_analysis('adopted', source, self.success)
        self.save('save-analysis', source, 'analysis-package'); package = self.output / 'projects/analysis-package'
        backup = self.declaration.with_name(self.declaration.name + '.owned-renamed')
        self.declaration.rename(backup)
        try:
            context = self.restore_static(source, package, ANALYSIS, keep_preview=True)
            self.expect_no_change('missing-source-audio-inclusion', source, lambda: self.mutate('missing-source-audio-inclusion',
                '/api/v1/project-sessions/' + source['projectSessionId'] + '/export-jobs',
                {'expectedRevision': source['projectRevision'], 'includeAudio': True}, status=422, code='exportInputRejected'))
            self.check(not self.declaration.exists(), 'pure static operations succeeded with actual owned source absent')
        finally:
            backup.rename(self.declaration)
        self.source_evidence('new-validation', context['validation'], 'restored-preview-oracle', ANALYSIS)
        self.source_evidence('pure-video', context['export'], 'restored-export-oracle', ANALYSIS)
        self.recovery_records.extend([context['validation'], context['export']])
        self.extra_export('audio-mp4', source, package, audio=True); self.extra_export('transparent-pure', source, package, alpha=True)
        captured_revision = source['projectRevision']; captured = copy.deepcopy(self.recovery_records)
        self.command_for('explicit-clear', source, {'type': 'audio.analysis.clear'}); self.expect_analysis('cleared', source)
        self.preview_frame('captured-after-clear', context['preview'], captured_revision, 2, package, ANALYSIS)
        preview = context['preview']; self.mutate('close-captured', '/api/v1/preview-sessions/' + preview['previewId'] + '/close',
            {'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': captured_revision})
        for terminal in captured:
            self.check(self.request('fixed-input-after-clear', 'GET', '/api/v1/jobs/' + terminal['jobId']) == terminal,
                'clear: complete captured task input/result stays unchanged')
        self.save('save-null-analysis', source, 'null-package'); null_package = self.output / 'projects/null-package'
        created = self.mutate('null-preview', '/api/v1/preview-sessions', {'source': {'kind': 'project', 'sourceId': source['projectSessionId'],
            'sourceRevision': source['projectRevision']}, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'straight'}, status=201)
        preview = created.get('session', created); self.preview_frame('null-profile', preview, source['projectRevision'], 1, null_package, OLD)
        self.mutate('close-null-preview', '/api/v1/preview-sessions/' + preview['previewId'] + '/close',
            {'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': source['projectRevision']})
        old = self.wait_job('null-validation', self.mutate('null-validation', '/api/v1/project-sessions/' + source['projectSessionId'] + '/validation-jobs',
            {'expectedRevision': source['projectRevision'], 'framesPerOutput': 1}, status=202))
        self.source_evidence('null-validation', old, 'null-profile-oracle', OLD); self.recovery_records.append(old)
        self.unsupported(package)
        self.comparisons.append({'name': 'complete-original-analysis', 'canonicalBytesEqual': True, 'metadata': meta})
        self.adoptions.append({'sourceProfile': ANALYSIS, 'contentHash': meta['contentHash'], 'fixedAfterClear': True})

    def unsupported(self, package):
        for mode in ('nondefault-effect-budget', 'background-image'):
            fixture = self.output / 'projects' / mode; shutil.copytree(package, fixture)
            manifest = json.loads((fixture / 'project.json').read_text())
            if mode == 'nondefault-effect-budget': manifest['effects']['budgets']['drawCommands'] -= 1
            else:
                output = io.BytesIO(); Image.new('RGBA', (8, 8), (20, 30, 40, 255)).save(output, format='PNG'); raw = output.getvalue()
                sha = hashlib.sha256(raw).hexdigest(); relative = 'data/media/' + sha + '.media'; (fixture / relative).write_bytes(raw)
                manifest['media']['background'] = {'hash': sha, 'byteLength': len(raw), 'originalName': 'owned.png'}
                manifest['background'] = {'kind': 'image', 'path': relative, 'fit': 'contain', 'playback': 'loop'}
            (fixture / 'project.json').write_text(json.dumps(manifest))
            handle = self.mutate(mode + '-handle', '/api/v1/file-handles', {'rootGrantId': 'adoption-probe-root', 'relativePath': mode}, status=201)
            opened = self.open(mode + '-open', handle); self.static_rejected(mode, opened); self.close(mode + '-close', opened)

    def restart(self):
        first_host = self.host; self.stop_server('first-normal-shutdown'); original_launcher = self.worker_launcher.read_bytes()
        self.worker_launcher.write_text("#!/bin/sh\nexec '" + str(self.legacy_worker).replace("'", "'\\''") + "' \"$@\"\n")
        self.worker_launcher.chmod(0o700)
        try:
            self.start_server('legacy-worker-server'); self.host = self.request('legacy-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
            self.register('legacy-profile-owner'); caps = self.request('legacy-export-disabled', 'GET', '/api/v1/export-capabilities')
            self.check(not caps['enabled'] and caps['sourceProfiles'] == [], 'old0.68 Worker: actual startup probe rejects missing engine/profile support')
            self.stop_server('legacy-normal-shutdown')
        finally:
            self.worker_launcher.write_bytes(original_launcher); self.worker_launcher.chmod(0o700)
        self.start_server('second-current-server'); self.host = self.request('recovery-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.register('recovery-profile-owner'); self.check(self.host != first_host, 'recovery: actual new host identity')
        recovered = self.request('recover-jobs', 'GET', '/api/v1/recovery-jobs')['jobs']
        for original in self.recovery_records:
            saved = next(value for value in recovered if value['job']['jobId'] == original['jobId'])
            self.check(saved['previousHostInstanceId'] == first_host and saved['job'] == original, 'recovery: full original terminal/source profile retained')
            kind = 'validation' if original['kind'] == 'validation' else 'export'
            _, raw = self.binary('recovered-' + kind, '/api/v1/recovery-jobs/' + original['jobId'] + '/' + kind + '-result')
            reference = original['validationResult']['report'] if kind == 'validation' else original['exportResult']['report']
            self.check(len(raw) == reference['sizeBytes'] and hashlib.sha256(raw).hexdigest() == reference['sha256'], 'recovery: exact registered full report for each old/new profile')
            if kind == 'export':
                _, media = self.binary('recovered-content', '/api/v1/recovery-jobs/' + original['jobId'] + '/export-content')
                artifact = original['exportResult']['artifact']; self.check(len(media) == artifact['sizeBytes'] and hashlib.sha256(media).hexdigest() == artifact['sha256'], 'recovery: exact original media without re-export')
        self.result('recovered-original-analysis', self.success, self.raw, recovered=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=pathlib.Path, required=True); parser.add_argument('--server', nargs='+', required=True)
    parser.add_argument('--worker', required=True); parser.add_argument('--oracle', nargs='+', required=True)
    parser.add_argument('--runtime-oracle', nargs='+', required=True); parser.add_argument('--legacy-worker', required=True)
    args = parser.parse_args()
    RenderingProbe(args.output.resolve(), args.server, args.worker, args.oracle, args.runtime_oracle, args.legacy_worker).run()
