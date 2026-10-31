#!/usr/bin/env python3
"""Thin B4-H export delta. Original transport, full-frame oracle and store checks are reused."""
import argparse
import copy
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import time
import wave

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('accepted_export_probe', ROOT / 'tools/backend-export-http-probe/probe.py')
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)
WAV, MP3, FLAC = 'pcm16-wave-self-contained', 'mp3-layer3-self-contained', 'native-flac-self-contained'
OLD, COMPRESSED = 'static-export-pcmwav-v1', 'static-export-compressed-audio-v1'


class CompressedExportProbe(BASE.ExportProbe):
    def __init__(self, output, server, worker, oracle, analysis_oracle, legacy_worker):
        super().__init__(output, server, worker, oracle)
        self.analysis_oracle = analysis_oracle
        self.legacy_worker = pathlib.Path(legacy_worker).resolve()
        self.records, self.audio_comparisons, self.native_observations = [], [], []
        self.server_identities = {}
        self.native_launcher = output / 'owned-ffmpeg-control-launcher'
        self.source_start, self.deployments_start = None, None

    @staticmethod
    def source_hashes():
        files = subprocess.run(['git', 'ls-files', '-z'], cwd=ROOT, capture_output=True, check=True).stdout.decode().split('\0')
        selected = [name for name in files if name.startswith(('src/', 'tests/', 'tools/'))
            or name in ('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'MakoKaraGenerator.slnx', 'NuGet.Config')]
        return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in selected}

    def deployment_hashes(self):
        roles = {'server': self.server_command[0], 'worker': str(self.worker), 'exportOracle': self.oracle[0],
            'analysisOracle': self.analysis_oracle[0], 'legacyWorker': str(self.legacy_worker)}
        return {role: {str(path.relative_to(pathlib.Path(executable).parent)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in pathlib.Path(executable).parent.rglob('*') if path.is_file()} for role, executable in roles.items()}

    def start_server(self, name):
        if self.source_start is None:
            self.source_start, self.deployments_start = self.source_hashes(), self.deployment_hashes()
            BASE.HELPER.save_json(self.output / 'source-start.json', self.source_start)
            BASE.HELPER.save_json(self.output / 'deployment-start.json', self.deployments_start)
        marker = self.output / 'owned-rate-control-source'
        if not self.native_launcher.exists():
            self.native_launcher.write_text('#!/bin/sh\nsource=$(cat "' + str(marker) + '" 2>/dev/null)\nmatched=0\ndecode=0\n'
                'for arg do\n [ "$arg" = "$source" ] && matched=1\n [ "$arg" = "pcm_f32le" ] && decode=1\ndone\n'
                'if [ -n "$source" ] && [ "$matched" = 1 ] && [ "$decode" = 1 ]; then exec /usr/bin/ffmpeg -re "$@"; fi\nexec /usr/bin/ffmpeg "$@"\n')
            self.native_launcher.chmod(0o700)
        self.environment.update(MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS='120', MAKO_BACKEND_CLIENT_LEASE_SECONDS='3600',
            MAKO_BACKEND_FFMPEG_PATH=str(self.native_launcher))
        result = super().start_server(name)
        self.server_identities[self.process.pid] = self.identity(self.process.pid)
        return result

    def stop_server(self, name):
        identity = self.server_identities[self.process.pid]
        current = self.identity(self.process.pid)
        self.check(current is not None and current['startTicks'] == identity['startTicks'],
            name + ': exact owned Server PID/startTicks verified before shutdown')
        super().stop_server(name)
        self.server_runs[-1]['startTicks'] = identity['startTicks']

    def encode(self, name, rate, channels, format, variable=False, seconds=3.013, sample_frames=None):
        original = self.output / (name + '-preencoding.wav')
        sample_count = int(rate * seconds) if sample_frames is None else sample_frames
        times = np.arange(sample_count, dtype=np.float64) / rate
        samples = np.column_stack([np.sin(times * 2 * np.pi * (440 if index == 0 else 659)) * 10000 for index in range(channels)]).astype('<i2')
        with wave.open(str(original), 'wb') as output:
            output.setnchannels(channels); output.setsampwidth(2); output.setframerate(rate); output.writeframes(samples.tobytes())
        if format == 'wav': return original
        target = self.output / (name + '.' + format)
        command = ['/usr/bin/ffmpeg', '-v', 'error', '-i', str(original), '-c:a', 'libmp3lame' if format == 'mp3' else 'flac']
        if format == 'mp3': command += ['-q:a', '4'] if variable else ['-b:a', '128k']
        result = subprocess.run(command + [str(target)], capture_output=True, timeout=20)
        (self.output / (name + '-encoding.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': owned encoded fixture exits0')
        return target

    def upload_audio(self, name, path):
        mime = {'.mp3': 'audio/mpeg', '.flac': 'audio/flac', '.wav': 'audio/wav'}[path.suffix]
        return self.request(name, 'POST', '/api/v1/assets', raw=path.read_bytes(), content_type=mime,
            headers={'X-Mako-Asset-Name': path.name}, status=201)

    def wait_job(self, name, job, expected='succeeded'):
        terminal = super().wait_job(name, job, expected)
        self.check(not self.live(), name + ': every observed private-group identity gone at authoritative terminal')
        return terminal

    def source_fixture(self, name, path, numerator=30):
        self.audio = self.upload_audio(name + '-upload', path)
        source = self.fixture(name, numerator)
        original = self.output / 'state/sessions' / source['projectSessionId'] / 'loaded-input'
        return source, original

    def adopt(self, name, source, path):
        job = self.mutate(name + '-analysis', 'POST', '/api/v1/project-sessions/' + source['projectSessionId'] + '/analysis-jobs',
            {'expectedRevision': source['projectRevision']}, status=202)
        terminal = self.wait_job(name + '-analysis', job)
        _, raw = self.binary(name + '-canonical', '/api/v1/jobs/' + terminal['jobId'] + '/analysis-result')
        prefix = self.output / (name + '-analysis-oracle')
        oracle = subprocess.run(self.analysis_oracle + [str(path), str(prefix)], capture_output=True, timeout=30)
        (self.output / (name + '-analysis-oracle.log')).write_bytes(oracle.stdout + oracle.stderr)
        self.check(oracle.returncode == 0 and pathlib.Path(str(prefix) + '.analysis.json').read_bytes() == raw,
            name + ': original complete Analyzer canonical matches same encoded bytes')
        self.request(name + '-apply', 'POST', '/api/v1/project-sessions/' + source['projectSessionId'] + '/commands',
            value={**self.metadata(name + '-apply'), 'expectedRevision': source['projectRevision'],
                'command': {'type': 'audio.analysis.apply', 'analysisJobId': terminal['jobId']}})
        source = self.request(name + '-adopted-summary', 'GET', '/api/v1/project-sessions/' + source['projectSessionId'])
        handle = self.saved_handles[name]
        saved = self.mutate(name + '-analysis-save', 'POST', '/api/v1/project-sessions/' + source['projectSessionId'] + '/save-jobs',
            {'expectedRevision': source['projectRevision'], 'targetHandleId': handle['handleId']}, status=202)
        self.wait_job(name + '-analysis-save', saved)
        close = self.mutate(name + '-analysis-close', 'POST', '/api/v1/project-sessions/' + source['projectSessionId'] + '/close',
            {'expectedRevision': source['projectRevision'], 'policy': 'requireClean', 'activeJobs': 'reject'}, status=202)
        self.wait_job(name + '-analysis-close', close)
        source = self.open(name + '-analysis-reopen', handle)
        pinned = self.output / 'state/sessions' / source['projectSessionId'] / 'loaded-input'
        self.check((pinned / 'data/analysis' / (terminal['analysisResult']['contentHash'] + '.json')).read_bytes() == raw,
            name + ': save/reopen keeps complete adopted canonical')
        self.native(name + '-oracle', ['render', str(pinned), str(self.output / (name + '-oracle'))])
        return source, pinned

    def audio_oracle(self, name, path, movie, report):
        inspection = subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(path)], capture_output=True, timeout=15)
        self.check(inspection.returncode == 0, name + ': original encoded input inspection exit0')
        source_info = next(item for item in json.loads(inspection.stdout)['streams'] if item['codec_type'] == 'audio')
        rate, channels = int(source_info['sample_rate']), int(source_info['channels'])
        format = 'wav' if source_info['codec_name'] == 'pcm_s16le' else source_info['codec_name']
        original = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-xerror', '-err_detect', 'explode', '-protocol_whitelist', 'file,pipe',
            '-f', format, '-i', str(path), '-map', '0:a:0', '-c:a', 'pcm_s16le', '-f', 's16le', 'pipe:1'], capture_output=True, timeout=20)
        actual = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(movie), '-map', '0:a:0', '-c:a', 'pcm_s16le', '-f', 's16le', 'pipe:1'],
            capture_output=True, timeout=20)
        self.check(original.returncode == actual.returncode == 0 and not original.stderr and not actual.stderr,
            name + ': full original encoded source and actual movie audio decode exit0')
        self.check(len(original.stdout) % (channels * 2) == len(actual.stdout) % (channels * 2) == 0,
            name + ': complete interleaved native-rate PCM16 frames')
        reference = np.frombuffer(original.stdout, dtype='<i2').reshape(-1, channels)
        decoded = np.frombuffer(actual.stdout, dtype='<i2').reshape(-1, channels)
        count = int(report['derivedVideoDurationUs']) * rate // 1_000_000
        self.check(int(report['audioDurationUs']) == len(reference) * 1_000_000 // rate,
            name + ': report source duration equals full actual source samples, not probe duration')
        self.check(abs(len(decoded) - count) <= (1 if report['hasAlpha'] else 1024), name + ': full output audio covers derived plan within codec quantization')
        facts = []
        for channel in range(channels):
            matched = min(count, len(decoded)); expected = reference[:matched, channel].astype(np.float64); observed = decoded[:matched, channel].astype(np.float64)
            correlation = float(np.corrcoef(expected, observed)[0, 1]); rms_ratio = float(np.sqrt(np.mean(observed ** 2)) / np.sqrt(np.mean(expected ** 2)))
            rmse = float(np.sqrt(np.mean((expected - observed) ** 2)))
            self.check(correlation > .99 and .95 < rms_ratio < 1.05 and rmse < 500, name + ': own encoded source channel content within declared AAC/PCM tolerance')
            if report['hasAlpha']:
                self.check(len(decoded) == count and np.array_equal(decoded[:, channel], reference[:count, channel]),
                    name + ': transparent PCM track bitexact to same encoded-source decode for movie interval')
            facts.append({'channel': channel, 'correlation': correlation, 'rmsRatio': rms_ratio, 'rmsePcm16': rmse, 'sameEncodedSourcePcmExact': report['hasAlpha']})
        source_pcm = self.output / (name + '-source-decoded.s16le'); output_pcm = self.output / (name + '-output-decoded.s16le')
        source_pcm.write_bytes(original.stdout); output_pcm.write_bytes(actual.stdout)
        observation = {'name': name, 'nativeSampleRate': rate, 'channels': channels, 'sourceFrames': len(reference), 'outputFrames': len(decoded),
            'plannedFrames': count, 'sourceEncodedSha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'sourceDecodedPcmSha256': hashlib.sha256(original.stdout).hexdigest(), 'outputDecodedPcmSha256': hashlib.sha256(actual.stdout).hexdigest(), 'channelComparisons': facts}
        self.audio_comparisons.append(observation)
        BASE.HELPER.save_json(self.output / (name + '-full-audio-comparison.json'), observation)

    def export_case(self, name, fixture_name, source, path, profile, fmt='opaqueMp4'):
        terminal = self.wait_job(name, self.start_export(name + '-export', source, fmt))
        audio = terminal['exportInput']['audio']
        if profile == WAV:
            self.check('executionProfile' not in terminal['exportInput'] and 'audioProfile' not in audio, name + ': legacy WAV wire markers omitted')
        else:
            self.check(terminal['exportInput']['executionProfile'] == COMPRESSED and audio['audioProfile'] == profile,
                name + ': compressed public execution and fixed audio markers explicit')
        self.check(audio['sha256'] == hashlib.sha256(path.read_bytes()).hexdigest(), name + ': separate grant binds original encoded bytes')
        report, raw, media, movie = self.read_result(name, terminal)
        self.check(report['audioCodec'] == ('pcm_s16le' if fmt == 'transparentProRes4444' else 'aac'),
            name + ': fixed actual output audio codec matches managed strategy')
        self.check(any(report['audioCodec'] in warning for warning in report['warnings'])
            and (fmt != 'transparentProRes4444' or all('aac' not in warning for warning in report['warnings'])),
            name + ': managed report accurately names actual audio encoding strategy')
        self.current_success = terminal
        self.decode(name, movie, report, fixture_name)
        self.audio_oracle(name, path, movie, report)
        self.records.append((copy.deepcopy(terminal), raw, media))
        return terminal, raw, media

    def owned_resource(self, source, path):
        matches = [item for item in (self.output / 'resources').rglob('*') if item.is_file()
            and item.stem == source['audioAssetId'] and item.read_bytes() == path.read_bytes()]
        self.check(len(matches) == 1, 'uniquely pin current-session assetId and original encoded bytes')
        return matches[0]

    def no_result(self, name, job):
        self.check('exportResult' not in job and job['error']['code'] == 'taskExecutionFailed', name + ': no registered partial artifact')
        self.request(name + '-unreadable', 'GET', '/api/v1/jobs/' + job['jobId'] + '/export-content', status=404, code='exportArtifactNotFound')

    def active_decoder(self, resource):
        deadline = time.monotonic() + 8
        while time.monotonic() < deadline:
            self.capture()
            for pid in self.live():
                try: args = pathlib.Path('/proc/' + str(pid) + '/cmdline').read_bytes().split(b'\0')
                except FileNotFoundError: continue
                if b'pcm_f32le' in args and str(resource).encode() in args:
                    self.check(args[0] == b'/usr/bin/ffmpeg' and b'-re' in args and b'-xerror' in args
                        and args[args.index(b'-f') + 1] == b'mp3' and args[args.index(b'-protocol_whitelist') + 1] == b'file,pipe',
                        'actual original strict MP3 decoder policy inside exact owned private group')
                    return pid, args, copy.deepcopy(self.identities[pid])
            time.sleep(.005)
        raise AssertionError('No actual owned strict source decoder observed.')

    def failures(self, source, path):
        short_path = self.encode('short-compressed', 48000, 1, 'mp3', seconds=.213)
        self.audio = self.upload_audio('short-upload', short_path); short = self.create('short-source')
        failed = self.wait_job('actual-short-source', self.start_export('actual-short-source', short), 'failed')
        self.check(failed['failureCode'] == 'WorkerExportAudioTooShort', 'actual short decoded source refuses before rendering')
        self.no_result('actual-short-source', failed)
        for name, frames, expected in [('one-sample-frame-short', 48000 * 3 - 1, 'WorkerExportAudioTooShort'),
            ('one-sample-frame-over-budget', 8000 * 300 + 1, 'WorkerExportAudioBudgetExceeded')]:
            rate = 8000 if 'budget' in name else 48000
            exact = self.encode(name, rate, 1, 'flac', sample_frames=frames)
            self.audio = self.upload_audio(name + '-upload', exact); exact_source = self.create(name + '-source')
            rejected = self.wait_job(name, self.start_export(name, exact_source), 'failed')
            self.check(rejected['failureCode'] == expected, name + ': complete actual source-rate EOF sample count enforces exact boundary')
            self.no_result(name, rejected)
        resource = self.owned_resource(source, path); original = resource.read_bytes()
        damaged_path = self.encode('damaged-flac', 48000, 1, 'flac'); damaged_bytes = damaged_path.read_bytes(); offset = 4
        while True:
            last = bool(damaged_bytes[offset] & 128); length = int.from_bytes(damaged_bytes[offset + 1:offset + 4], 'big'); offset += 4 + length
            if last: break
        damaged_path.write_bytes(damaged_bytes[:offset] + bytes(len(damaged_bytes) - offset))
        bad = self.upload_audio('damaged-upload', damaged_path)
        before = [item['projectSessionId'] for item in self.request('sessions-before-bad-input', 'GET', '/api/v1/clients/' + self.client + '/state')['sessions']]
        self.mutate('damaged-default-preparation-rejected', 'POST', '/api/v1/project-sessions',
            {'name': 'damaged-default-preparation', 'audioAssetId': bad['assetId'], 'lyricsAssetId': self.lyrics['assetId']},
            status=422, code='audioPreparationFailed')
        claimed = self.output / 'claimed.wav'; claimed.write_bytes(b"ffconcat version 1.0\nfile 'owned-sentinel.wav'\n")
        sentinel = self.output / 'resources' / self.client / 'owned-sentinel.wav'; sentinel.write_bytes(self.legacy_path.read_bytes())
        concat = self.upload_audio('owned-concat-upload', claimed)
        self.mutate('owned-concat-preparation-rejected', 'POST', '/api/v1/project-sessions',
            {'name': 'owned-concat-preparation', 'audioAssetId': concat['assetId'], 'lyricsAssetId': self.lyrics['assetId']},
            status=422, code='audioPreparationFailed')
        self.check([item['projectSessionId'] for item in self.request('sessions-after-bad-input', 'GET', '/api/v1/clients/' + self.client + '/state')['sessions']] == before,
            'bad-frame and owned ffconcat preparation reject without a partial published session')
        marker = self.output / 'owned-rate-control-source'; marker.write_text(str(resource))
        try:
            changed = self.start_export('changed-after-admission', source); changed_pid, changed_args, changed_identity = self.active_decoder(resource)
            try:
                resource.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
                failed = self.wait_job('changed-after-admission', changed, 'failed'); self.no_result('changed-after-admission', failed)
                current = self.identity(changed_pid)
                self.check(not current or current['startTicks'] != changed_identity['startTicks'], 'source change: actual predecoder identity gone at failed terminal')
                self.native_observations.append({'kind': 'sourceChangedAfterAdmission', 'identity': changed_identity,
                    'argv': [arg.decode() for arg in changed_args if arg], 'controlledOwnedReadRate': True})
            finally:
                resource.write_bytes(original)
            job = self.start_export('strict-decode-cancel', source); pid, args, identity = self.active_decoder(resource)
            self.cancel('strict-decode-cancel', job); terminal = self.wait_job('strict-decode-cancel', job, 'cancelled')
            current = self.identity(pid)
            self.check(not current or current['startTicks'] != identity['startTicks'], 'cancel: exact decoder identity gone at authoritative terminal')
            self.check('exportResult' not in terminal, 'cancel: no partial registered artifact')
            self.request('cancel-unreadable', 'GET', '/api/v1/jobs/' + terminal['jobId'] + '/export-content', status=404, code='exportArtifactNotFound')
            self.native_observations.append({'kind': 'cancel', 'identity': identity, 'argv': [arg.decode() for arg in args if arg], 'controlledOwnedReadRate': True})
        finally:
            marker.unlink(missing_ok=True)
        retry, _, _ = self.export_case('strict-decode-same-handle-retry', 'cbr-mono441', source, path, MP3)
        self.check(retry['exportInput']['audio'] == self.success['exportInput']['audio'],
            'strict decode cancellation: retry binds the exact same registered source handle/hash/size/profile')

    def restart_delta(self):
        original_host = self.host; self.stop_server('current-normal-shutdown'); launcher = self.launcher.read_bytes()
        self.launcher.write_text("#!/bin/sh\nexec '" + str(self.legacy_worker).replace("'", "'\\''") + "' \"$@\"\n"); self.launcher.chmod(0o700)
        try:
            self.start_server('actual-legacy-worker-server'); self.host = self.request('legacy-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
            self.register('legacy-owner'); caps = self.request('legacy-export-disabled', 'GET', '/api/v1/export-capabilities')
            self.check(not caps['enabled'] and caps['audioProfiles'] == [] and caps['sourceProfiles'] == [],
                'actual old0.70.1 Worker disables export; tool startup is insufficient')
            self.stop_server('legacy-normal-shutdown')
        finally:
            self.launcher.write_bytes(launcher); self.launcher.chmod(0o700)
        self.start_server('current-recovery-server'); self.host = self.request('recovery-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.register('recovery-owner'); recovered = self.request('recovery-jobs', 'GET', '/api/v1/recovery-jobs')['jobs']
        for original, raw, media in self.records:
            value = next(item for item in recovered if item['job']['jobId'] == original['jobId'])
            self.check(value['previousHostInstanceId'] == original_host and value['job'] == original, 'recovery: whole old/new terminal object remains exact')
            path = '/api/v1/recovery-jobs/' + original['jobId']
            self.binary('recovery-report', path + '/export-result', expected=raw)
            self.binary('recovery-media', path + '/export-content', expected=media)
        self.check(not self.live(), 'recovery: no old job automatically reruns')

    def exercise(self):
        self.host = self.request('health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']; self.register('compressed-export-owner')
        caps = self.request('export-capabilities', 'GET', '/api/v1/export-capabilities')
        self.check(caps['enabled'] and set(caps['audioProfiles']) == {WAV, MP3, FLAC}
            and len(caps['audioProfiles']) == 3 and len(caps['sourceProfiles']) == 2
            and set(caps['sourceProfiles']) == {'static-lyrics-scene-v1', 'static-lyrics-scene-analysis-v1'}
            and caps['executionProfile'] == OLD and set(caps['executionProfiles']) == {OLD, COMPRESSED} and len(caps['executionProfiles']) == 2
            and caps['maxCompressedAudioDurationUs'] == '300000000' and caps['compressedAudioDurationBasis'] == 'strict-source-rate-decoded-sample-frames',
            'real current Worker fixed audio/source/execution capabilities and explicit decoded-sample duration budget')
        self.lyrics = self.request('lyrics', 'POST', '/api/v1/assets', raw=b'[00:00.00]<00:00.00>KARAOKE<00:03.00>\n',
            content_type='text/plain', headers={'X-Mako-Asset-Name': 'owned.lrc'}, status=201)
        cases = [('cbr-mono441', 44100, 1, 'mp3', False, MP3), ('vbr-stereo48', 48000, 2, 'mp3', True, MP3),
            ('flac-stereo48-exact-tail', 48000, 2, 'flac', False, FLAC), ('legacy-wave', 44100, 2, 'wav', False, WAV)]
        for name, rate, channels, format, variable, profile in cases:
            path = self.encode(name, rate, channels, format, variable, sample_frames=144144 if profile == FLAC else None)
            source, pinned = self.source_fixture(name, path, 30000 if profile == FLAC else 30)
            if profile == FLAC: source, pinned = self.adopt(name, source, path)
            job, raw, media = self.export_case(name, name, source, path, profile)
            if name == 'cbr-mono441': self.cancel_source, self.cancel_path = source, path; self.success, self.success_raw, self.success_media = job, raw, media
            if profile == FLAC:
                self.export_case('flac-transparent', name, source, path, profile, 'transparentProRes4444')
                self.analysis_source, self.analysis_pinned = source, pinned
            if profile == WAV:
                self.wave_source, self.wave_pinned, self.legacy_path = source, pinned, path
        self.downloads(self.success, self.success_raw, self.success_media)
        # Missing declared source remains a valid explicit pure-video input.
        project = json.loads((self.analysis_pinned / 'project.json').read_text()); declared = self.analysis_pinned / project['audioPath']; backup = declared.read_bytes()
        try:
            declared.unlink(); terminal = self.wait_job('pure-video-missing-declaration', self.start_export('pure-video-missing-declaration', self.analysis_source, audio=False))
            report, raw, media, movie = self.read_result('pure-video-missing-declaration', terminal, audio=False)
            decoded = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(movie), '-map', '0:v:0', '-f', 'rawvideo', '-pix_fmt', 'bgra', 'pipe:1'], capture_output=True, timeout=20)
            inspected = subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(movie)], capture_output=True, timeout=15)
            self.check(decoded.returncode == inspected.returncode == 0 and len(decoded.stdout) == 90 * 320 * 180 * 4
                and not any(item['codec_type'] == 'audio' for item in json.loads(inspected.stdout)['streams']), 'pure video: all90frames and no actual audio source IO/track')
            self.check(terminal['input']['sourceProfile'] == 'static-lyrics-scene-analysis-v1'
                and terminal['exportInput'].get('audio') is None and 'executionProfile' not in terminal['exportInput'],
                'pure video: full adopted analysis and compressed declaration keep original static profile without audio grant or new execution marker')
        finally:
            declared.write_bytes(backup)
        self.failures(self.cancel_source, self.cancel_path)
        self.binary('old-success-after-failures', '/api/v1/jobs/' + self.success['jobId'] + '/export-content', expected=self.success_media)
        self.restart_delta()

    def run(self):
        try:
            super().run()
        finally:
            manifest_path = self.output / 'manifest.json'
            if manifest_path.exists():
                manifest = json.loads(manifest_path.read_text())
                source_final, deployment_final = self.source_hashes(), self.deployment_hashes()
                BASE.HELPER.save_json(self.output / 'source-final.json', source_final)
                BASE.HELPER.save_json(self.output / 'deployment-final.json', deployment_final)
                integrity = self.source_start == source_final and self.deployments_start == deployment_final
                if not integrity: manifest['succeeded'] = False; manifest['failure'] = 'Source or deployed executable files drifted during actual HTTP run.'
                if manifest['survivingOwnedPids']:
                    manifest['succeeded'] = False; manifest['failure'] = 'Observed exact owned process identities did not drain.'
                manifest.update(audioComparisons=self.audio_comparisons, strictDecodeCancellation=self.native_observations,
                    sourceFilesUnchanged=self.source_start == source_final, deploymentFilesUnchanged=self.deployments_start == deployment_final,
                    legacyWorkerSha256=hashlib.sha256(self.legacy_worker.read_bytes()).hexdigest(),
                    boundaries=['Independent B4-H delta; previous acceptance counters are not copied.',
                        'Same encoded CBR/VBR/native FLAC source bytes form independent complete audio oracle; no preencoding PCM losslessness claim.',
                        'MP4 AAC uses explicit lossy channel/content bounds; transparent FLAC PCM compares full planned interval bitexact.',
                        'Actual old0.70.1 Worker disables export; old/new registered whole terminal and original bytes recover without rerun.',
                        'Only one exact owned MP3 predecode is rate-controlled for source-change and cancellation; retry and all ordinary jobs are unthrottled.',
                        'No full suite, performance, GUI, long media, screenshots or manual platform acceptance.'])
                BASE.HELPER.save_json(manifest_path, manifest)
                if not manifest['succeeded']: raise SystemExit(manifest['failure'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--server', nargs='+', required=True); parser.add_argument('--worker', required=True)
    parser.add_argument('--oracle', nargs='+', required=True); parser.add_argument('--analysis-oracle', nargs='+', required=True)
    parser.add_argument('--legacy-worker', required=True); args = parser.parse_args()
    CompressedExportProbe(args.output.resolve(), args.server, args.worker, args.oracle, args.analysis_oracle, args.legacy_worker).run()
