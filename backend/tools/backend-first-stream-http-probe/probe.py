#!/usr/bin/env python3
"""Owned real HTTP first-audio-stream duration regression; no waveform API is claimed."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('first_stream_editing', ROOT / 'tools/backend-web-editing/probe.py')
editing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(editing)


class FirstStreamProbe(editing.EditingProbe):
    def __init__(self, output, dist, expected_dual_duration):
        super().__init__(output, dist)
        self.expected_dual_duration = expected_dual_duration
        self.media = []

    def fixture_audio(self, name, dual):
        path = self.output / (name + '.mov')
        args = ['/usr/bin/ffmpeg', '-v', 'error', '-f', 'lavfi', '-i',
                'sine=frequency=440:sample_rate=8000:duration=1']
        if dual:
            args += ['-f', 'lavfi', '-i', 'sine=frequency=880:sample_rate=48000:duration=1']
        args += ['-map', '0:a:0']
        if dual:
            args += ['-map', '1:a:0']
        args += ['-c:a', 'pcm_s16le']
        if dual:
            args += ['-ac:a:1', '2', '-disposition:a:0', '0', '-disposition:a:1', 'default']
        subprocess.run(args + [str(path)], check=True, capture_output=True, timeout=20)
        probe = subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(path)],
                               check=True, capture_output=True, timeout=20)
        streams = json.loads(probe.stdout)['streams']
        self.check(streams[0]['sample_rate'] == '8000' and streams[0]['channels'] == 1,
                   name + ': first audio stream8kHz mono')
        self.check(len(streams) == (2 if dual else 1), name + ': exact audio stream count')
        if dual:
            self.check(streams[1]['sample_rate'] == '48000' and streams[1]['channels'] == 2
                       and streams[1]['disposition']['default'] == 1
                       and streams[0]['disposition']['default'] == 0,
                       name + ': second audio stream48kHz stereo explicitly default')
        oracle = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(path), '-map', '0:a:0',
                                 '-vn', '-ac', '1', '-f', 'f32le', '-acodec', 'pcm_f32le', '-'],
                                check=True, capture_output=True, timeout=20).stdout
        self.check(len(oracle) == 8000 * 4, name + ': complete mapped first-stream PCM oracle8000samples')
        self.media.append({'file': path.name, 'sourceSha256': hashlib.sha256(path.read_bytes()).hexdigest(),
                           'streams': streams, 'firstStreamOracleSamples': len(oracle) // 4,
                           'firstStreamOraclePcmSha256': hashlib.sha256(oracle).hexdigest()})
        editing.save_json(self.output / 'media-fixtures.json', self.media)
        return path

    def upload(self, name, data, mime):
        return self.request('upload-' + name, 'POST', '/api/v1/assets?fileName=' + name, raw=data,
            content_type=mime, status=201, headers={'X-Mako-Request-Id': 'upload-' + name,
            'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']})

    def run(self):
        self.start('first-stream')
        self.connect('first-stream')
        lyrics = self.upload('first-stream.lrc', b'[00:00.00]first stream\n', 'text/plain')
        for name, dual in (('dual-second-default', True), ('single-first-stream', False)):
            audio = self.fixture_audio(name, dual)
            asset = self.upload(audio.name, audio.read_bytes(), 'audio/mp4')
            project = self.request(name + '-create', 'POST', '/api/v1/project-sessions', status=201,
                value={'name': name, 'audioAssetId': asset['assetId'], 'lyricsAssetId': lyrics['assetId'],
                       'composition': {'canvasWidth': 320, 'canvasHeight': 180}})
            duration = self.expected_dual_duration if dual else 1000000
            self.check(project['audioDurationUs'] == duration, name + ': create actual prepared duration')
            path = '/api/v1/project-sessions/' + project['projectSessionId']
            settings = self.get(name + '-settings', path, '/settings', '0')
            self.check(settings['audioDurationUs'] == str(duration), name + ': settings exact duration string')
            path, opened = self.save_close_open(name + '-roundtrip', path, '0', name + '-saved')
            self.check(opened['audioDurationUs'] == duration, name + ': reopened Worker preparation duration')
            self.close(name + '-final-close', path, opened['projectRevision'])
        self.check(bool(self.worker_identities), 'actual managed Worker executable identities observed')
        self.stop('first-stream')
        scanned = 0
        for path in self.output.rglob('*'):
            if path.is_file():
                self.check(not any(secret.encode() in path.read_bytes() for secret in self.secrets),
                           'credential scan: ' + str(path.relative_to(self.output)))
                scanned += 1
        summary = {'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions) + 1,
                   'assertionLabels': self.assertions + ['credential scan: summary.json'], 'runs': self.runs,
                   'workerIdentities': [{'pid': p, 'startTicks': t} for p, t in sorted(self.worker_identities)],
                   'credentialScannedFiles': scanned + 1, 'expectedDualDurationUs': self.expected_dual_duration,
                   'scope': 'Real HTTP create/settings/save/close/open duration; first-stream PCM is independent fixture oracle. '
                            'Complete Worker floatbit/pyramid comparison is in WorkerFirstAudioStreamTests; no waveform HTTP, browser or performance acceptance.'}
        candidate = self.output / '.summary.candidate'
        editing.save_json(candidate, summary)
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), 'credential scan: summary.json')
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({k: summary[k] for k in ('status', 'httpRequests', 'assertions', 'expectedDualDurationUs')}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--expected-dual-duration-us', type=int, choices=(1000000, 6000000), default=1000000,
                        help='6000000 records only the known pre-repair defect; default requires corrected first stream.')
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    output.mkdir(parents=True, exist_ok=False)
    probe = FirstStreamProbe(output, args.dist.resolve(), args.expected_dual_duration_us)
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
