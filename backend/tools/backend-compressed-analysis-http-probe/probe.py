#!/usr/bin/env python3
"""B4-F bounded compressed-analysis delta using accepted transport/oracle helpers."""
import argparse
import copy
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('accepted_rendering_probe', ROOT / 'tools/backend-analysis-rendering-http-probe/probe.py')
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)
WAV, MP3, FLAC = 'pcm16-wave-self-contained', 'mp3-layer3-self-contained', 'native-flac-self-contained'
LEGACY, COMPRESSED = 'pcm16-wave-analysis-v1', 'fixed-compressed-audio-analysis-v1'


class CompressedProbe(BASE.RenderingProbe):
    EXTRA_EVIDENCE_SOURCES = BASE.RenderingProbe.EXTRA_EVIDENCE_SOURCES + (
        'tools/backend-compressed-analysis-http-probe/probe.py', 'tools/backend-compressed-analysis-http-probe/README.md')
    EVIDENCE_BOUNDARIES = ['Independent B4-F delta counts; no earlier acceptance counts copied.',
        'Three encoded CBR/VBR MP3/native FLAC inputs compare complete canonical to original Analyzer on identical encoded bytes; no comparison to pre-encoding PCM.',
        'Tool demuxer/decoder lists only confirm configuration/startup; actual format decode evidence is real HTTP Worker analysis.',
        'Normal inputs are unthrottled; one explicitly owned MP3 cancellation resource alone prepends -re to original FFmpeg.',
        'One accepted FLAC PNG is all57600 premultiplied pixels byteexact to original Runtime; formal ordinary1+transparent1 report. No export expansion.',
        'Old0.69 Worker actual mixed-version probe disables analysis; old/new results restart read-only.',
        'Snapshot source SHA normalizes only owned declaration after complete package load; not original package-path SHA or an audio IO grant.',
        'No GUI/screenshots/full suite/performance/long media/manual platform acceptance.']

    def start_server(self, name, controlled=False):
        if not self.native_launcher.exists():
            marker = self.output / 'owned-rate-control-source'
            self.native_launcher.write_text('#!/bin/sh\nsource=$(cat "' + str(marker) + '" 2>/dev/null)\n'
                'for arg do\n if [ -n "$source" ] && [ "$arg" = "$source" ]; then exec /usr/bin/ffmpeg -re "$@"; fi\ndone\nexec /usr/bin/ffmpeg "$@"\n')
            self.native_launcher.chmod(0o700)
        return super().start_server(name, controlled=True)

    def upload(self, name, path):
        mime = {'.mp3': 'audio/mpeg', '.flac': 'audio/flac', '.wav': 'audio/wav'}[path.suffix]
        return self.request(name, 'POST', '/api/v1/assets', raw=path.read_bytes(), content_type=mime,
            headers={'X-Mako-Asset-Name': path.name}, status=201)

    def owned_resource(self, path, source):
        matches = [value for value in (self.output / 'resources').rglob('*') if value.is_file() and value.stem == source['audioAssetId'] and value.read_bytes() == path.read_bytes()]
        self.check(len(matches) == 1, path.name + ': uniquely bind current session assetId to exact own immutable resource')
        return matches[0]

    def encoded(self, name, rate, channels, format, variable=False):
        original = self.fixture(name + '-preencoding', rate, channels); target = self.output / (name + '.' + format)
        command = ['/usr/bin/ffmpeg', '-v', 'error', '-i', str(original), '-c:a', 'libmp3lame' if format == 'mp3' else 'flac']
        if format == 'mp3': command += ['-q:a', '4'] if variable else ['-b:a', '128k']
        command += [str(target)]
        result = subprocess.run(command, capture_output=True, timeout=20)
        (self.output / (name + '-encoding.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': owned encoding fixture exits0'); return target

    def normal(self):
        self.host = self.request('health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']; self.register('compressed-owner')
        caps = self.request('analysis-capabilities', 'GET', '/api/v1/analysis-capabilities')
        self.check(caps['enabled'] and caps['toolStartupVerified'] and set(caps['audioProfiles']) == {WAV, MP3, FLAC}
            and caps['sourceProfile'] == LEGACY and set(caps['sourceProfiles']) == {LEGACY, COMPRESSED}, 'actual Worker: fixed engine/format/source capability sets')
        self.lyrics = self.lyrics_upload('lyrics'); self.analysis_records = []
        cases = [('cbr-mono441', self.encoded('cbr-mono441', 44100, 1, 'mp3'), MP3),
            ('vbr-stereo48', self.encoded('vbr-stereo48', 48000, 2, 'mp3', True), MP3),
            ('flac-stereo441', self.encoded('flac-stereo441', 44100, 2, 'flac'), FLAC),
            ('legacy-wave', self.fixture('legacy-wave', 48000, 1), WAV)]
        for name, path, profile in cases:
            source = self.create(name, self.upload(name + '-upload', path)); meta, canonical = self.oracle_run(name, path)
            terminal = self.wait_job(name, self.start_analysis(name + '-analysis', source)); raw = self.result(name + '-canonical', terminal, canonical, meta)
            info = terminal['analysisInput']; self.check(info['sourceProfile'] == (LEGACY if profile == WAV else COMPRESSED), name + ': fixed actual source profile')
            if profile == WAV: self.check(int(info['sourceDurationUs']) == 2_013_000, name + ': legacy exact duration retained')
            else:
                self.check('sourceDurationUs' not in info and info['audio']['audioProfile'] == profile, name + ': compressed duration unknown and omitted; no probe estimate')
                self.apply(name + '-apply', source, terminal); self.expect_analysis(name + '-adopted', source, terminal)
                handle, _ = self.save(name + '-save', source, name + '-package'); package = self.output / 'projects' / (name + '-package')
                stored = package / 'data/analysis' / (terminal['analysisResult']['contentHash'] + '.json')
                self.check(stored.read_bytes() == raw, name + ': saved complete canonical byteexact')
                self.close(name + '-close', source); source = self.open(name + '-reopen', handle)
                self.expect_analysis(name + '-reopened', source, terminal); self.check(stored.read_bytes() == raw, name + ': reopened complete data unchanged')
                self.adoptions.append({'name': name, 'audioProfile': profile, 'canonicalBytesEqual': True, 'contentHash': meta['contentHash']})
            self.analysis_records.append((copy.deepcopy(terminal), raw))
            self.comparisons.append({'name': name, 'audioProfile': profile, 'canonicalBytesEqual': True, 'metadata': meta})
            if profile == FLAC: self.static_source, self.static_package, self.static_path = source, package, path; self.success, self.raw = terminal, raw
            if name == 'cbr-mono441': self.cancel_source, self.cancel_path = source, path
        relative = json.loads((self.static_package / 'project.json').read_text())['audioPath']
        self.declaration = self.output / 'state/sessions' / self.static_source['projectSessionId'] / 'loaded-input' / relative
        self.check(self.declaration.is_file() and self.declaration.read_bytes() == self.static_path.read_bytes(),
            'reopened source: exact owned pinned declaration; separate current asset is the analysis IO grant')
        created = self.mutate('compressed-preview', '/api/v1/preview-sessions', {'source': {'kind': 'project',
            'sourceId': self.static_source['projectSessionId'], 'sourceRevision': self.static_source['projectRevision']},
            'size': {'width': 320, 'height': 180}, 'alphaMode': 'straight'}, status=201)
        preview = created.get('session', created)
        self.preview_frame('compressed-static', preview, self.static_source['projectRevision'], 1, self.static_package, BASE.ANALYSIS)
        self.mutate('close-preview', '/api/v1/preview-sessions/' + preview['previewId'] + '/close',
            {'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': self.static_source['projectRevision']})
        validation = self.wait_job('compressed-validation', self.mutate('compressed-validation', '/api/v1/project-sessions/'
            + self.static_source['projectSessionId'] + '/validation-jobs', {'expectedRevision': self.static_source['projectRevision'], 'framesPerOutput': 1}, status=202))
        _, raw = self.binary('compressed-validation-report', '/api/v1/jobs/' + validation['jobId'] + '/validation-result'); report = json.loads(raw)
        reference = validation['validationResult']['report']
        self.check(report['succeeded'] and report['opaqueFrames'] == report['transparentFrames'] == 1
            and not any(issue['isError'] for issue in report['issues']) and len(raw) == reference['sizeBytes']
            and hashlib.sha256(raw).hexdigest() == reference['sha256'], 'formal complete ordinary1+transparent1 frame report succeeds with exact identity')
        self.source_evidence('compressed-validation', validation, 'compressed-static-oracle', BASE.ANALYSIS)
        self.command_for('clear-compressed', self.static_source, {'type': 'audio.analysis.clear'}); self.expect_analysis('cleared-compressed', self.static_source)
        self.command_for('undo-clear', self.static_source, {'type': 'history.undo'}); self.expect_analysis('undo-compressed', self.static_source, self.success)
        self.corrupt_and_cancel()

    def corrupt_and_cancel(self):
        resource = self.owned_resource(self.cancel_path, self.cancel_source); original = resource.read_bytes()
        try:
            resource.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
            terminal = self.wait_job('bad-source', self.start_analysis('bad-source', self.cancel_source), 'failed')
            self.check('analysisResult' not in terminal and terminal['error']['code'] == 'taskExecutionFailed', 'bad source: no partial registered result')
            self.request('bad-source-no-result', 'GET', '/api/v1/jobs/' + terminal['jobId'] + '/analysis-result', status=404, code='jobNotFound')
        finally:
            resource.write_bytes(original)
        marker = self.output / 'owned-rate-control-source'; marker.write_text(str(resource))
        try:
            job = self.start_analysis('real-cancel', self.cancel_source); end = time.monotonic() + 8
            while time.monotonic() < end:
                self.capture()
                candidates = []
                for pid in self.live():
                    try: args = pathlib.Path('/proc/' + str(pid) + '/cmdline').read_bytes().split(b'\0')
                    except FileNotFoundError: continue
                    if b'pcm_f32le' in args: candidates.append((pid, args))
                if candidates: break
                time.sleep(.005)
            self.check(bool(candidates), 'cancel: actual owned original FFmpeg decoding observed')
            pid, args = candidates[0]
            self.check(args[0] == b'/usr/bin/ffmpeg' and b'-re' in args and args[args.index(b'-f') + 1] == b'mp3'
                and b'-xerror' in args and args[args.index(b'-protocol_whitelist') + 1] == b'file,pipe', 'cancel: strict MP3/owned controlled actual decode policy')
            identity = copy.deepcopy(self.processes[pid]); self.cancel('real-cancel', job)
            terminal = self.wait_job('real-cancel', job, 'cancelled'); current = self.identity(pid)
            self.check(not current or current['startTicks'] != identity['startTicks'], 'cancel: owned decoder identity fully gone before terminal')
            self.check('analysisResult' not in terminal, 'cancel: no partial result registered')
            observation = {'identity': identity, 'argv': [arg.decode() for arg in args if arg], 'controlledInputReadRate': True}
            self.native_observations.append(observation)
            (self.output / 'cancel-native-policy.json').write_text(json.dumps(observation, indent=2) + '\n')
            self.request('cancel-no-result', 'GET', '/api/v1/jobs/' + terminal['jobId'] + '/analysis-result', status=404, code='jobNotFound')
        finally:
            marker.unlink(missing_ok=True)

    def restart(self):
        first = self.host; self.stop_server('first-normal-shutdown'); launcher = self.worker_launcher.read_bytes()
        self.worker_launcher.write_text("#!/bin/sh\nexec '" + str(self.legacy_worker).replace("'", "'\\''") + "' \"$@\"\n"); self.worker_launcher.chmod(0o700)
        try:
            self.start_server('legacy-worker-server'); self.host = self.request('legacy-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
            self.register('legacy-owner'); caps = self.request('legacy-analysis-disabled', 'GET', '/api/v1/analysis-capabilities')
            self.check(not caps['enabled'] and caps['audioProfiles'] == [] and caps['sourceProfiles'] == [], 'actual old0.69 Worker: missing current engine/format capability disables analysis')
            self.stop_server('legacy-normal-shutdown')
        finally:
            self.worker_launcher.write_bytes(launcher); self.worker_launcher.chmod(0o700)
        self.start_server('current-recovery-server'); self.host = self.request('recovery-health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']
        self.register('recovery-owner'); self.check(self.host != first, 'restart: new host identity')
        recovered = self.request('recovery-jobs', 'GET', '/api/v1/recovery-jobs')['jobs']
        for original, raw in self.analysis_records:
            entry = next(item for item in recovered if item['job']['jobId'] == original['jobId'])
            self.check(entry['previousHostInstanceId'] == first and entry['job'] == original, 'restart: entire old/new terminal unchanged')
            self.result('recovered-complete', original, raw, recovered=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=pathlib.Path, required=True); parser.add_argument('--server', nargs='+', required=True)
    parser.add_argument('--worker', required=True); parser.add_argument('--oracle', nargs='+', required=True)
    parser.add_argument('--runtime-oracle', nargs='+', required=True); parser.add_argument('--legacy-worker', required=True)
    args = parser.parse_args()
    CompressedProbe(args.output.resolve(), args.server, args.worker, args.oracle, args.runtime_oracle, args.legacy_worker).run()
