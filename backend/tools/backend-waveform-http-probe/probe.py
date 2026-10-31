#!/usr/bin/env python3
"""Owned real HTTP waveform verification against independent mapped first-stream PCM."""
import argparse
import array
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import struct
import subprocess
import sys
import urllib.error
import urllib.request
from urllib.parse import urlencode
import wave

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('waveform_editing', ROOT / 'tools/backend-web-editing/probe.py')
editing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(editing)


def sha(data):
    return hashlib.sha256(data).hexdigest()


def extrema(values):
    # Match IEEE min/max signed-zero semantics without changing finite float32 bits.
    low, high = min(values), max(values)
    if low == 0 and any(math.copysign(1, v) < 0 for v in values if v == 0):
        low = -0.0
    if high == 0 and any(math.copysign(1, v) > 0 for v in values if v == 0):
        high = 0.0
    return low, high


class WaveformProbe(editing.EditingProbe):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.media = []
        self.windows = []
        self.successful_jobs = set()

    def job(self, name, accepted):
        result = super().job(name, accepted)
        self.successful_jobs.add(result['jobId'])
        return result

    def upload(self, name, data, mime):
        return self.request('upload-' + name, 'POST', '/api/v1/assets?' + urlencode({'fileName': name}),
            raw=data, content_type=mime, status=201, headers={'X-Mako-Request-Id': 'upload-' + name,
            'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']})

    def transport(self, name, method, path, *, status=200, headers=None, client=None):
        """Binary/HEAD evidence remains paired with its redacted request, including errors/304."""
        self.request_count += 1
        prefix = f'{self.request_count:03d}-{name}'
        selected = client or self.client
        sent = {'X-Mako-Client-Id': selected, 'Authorization': 'Bearer ' + self.tokens[selected],
                'X-Mako-Host-Instance-Id': self.host, **(headers or {})}
        editing.save_json(self.output / (prefix + '.request.json'), {
            'method': method, 'path': path, 'headers': {k: '<redacted>' if k.lower() == 'authorization' else v
            for k, v in sent.items()}, 'json': None, 'bodyBytes': 0, 'bodySha256': sha(b'')})
        try:
            response = self.http.open(urllib.request.Request(self.base_url + path, headers=sent, method=method), timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body, actual, received = response.read(), response.status, dict(response.headers.items())
        parsed = json.loads(body) if body and received.get('Content-Type', '').startswith('application/problem+json') else None
        editing.save_json(self.output / (prefix + '.response.json'), {'status': actual, 'headers': received,
            'json': self.redact(parsed), 'bodyBytes': len(body), 'bodySha256': sha(body)})
        if body and actual == 200:
            (self.output / (prefix + '.bin')).write_bytes(body)
        self.check(actual == status, f'{name}: HTTP{status}, got{actual}')
        return {k.lower(): v for k, v in received.items()}, body

    def build_fixtures(self):
        fixtures = self.output / 'media'
        fixtures.mkdir()
        # Own deterministic stereo PCM, with an incomplete last 64-sample bucket.
        for name, rate, count in (('long-stereo', 8000, 536009), ('short-stereo', 16000, 32019)):
            values = array.array('h')
            for index in range(count):
                values.extend((((index * 137) % 16001) - 8000, ((index * 271) % 12001) - 6000))
            if sys.byteorder != 'little':
                values.byteswap()
            with wave.open(str(fixtures / (name + '.wav')), 'wb') as stream:
                stream.setnchannels(2); stream.setsampwidth(2); stream.setframerate(rate)
                stream.writeframes(values.tobytes())
        for suffix, codec in (('mp3', 'libmp3lame'), ('flac', 'flac')):
            subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(fixtures / 'short-stereo.wav'),
                '-map', '0:a:0', '-c:a', codec, str(fixtures / ('short.' + suffix))], check=True, capture_output=True, timeout=30)
        dual = fixtures / 'dual-second-default.mov'
        subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', 'sine=frequency=440:sample_rate=8000:duration=1',
            '-f', 'lavfi', '-i', 'sine=frequency=880:sample_rate=48000:duration=1', '-map', '0:a:0', '-map', '1:a:0',
            '-c:a', 'pcm_s16le', '-ac:a:1', '2', '-disposition:a:0', '0', '-disposition:a:1', 'default', str(dual)],
            check=True, capture_output=True, timeout=30)
        return [(fixtures / 'long-stereo.wav', 'wav', 'audio/wav'),
                (fixtures / 'short.mp3', 'mp3', 'audio/mpeg'), (fixtures / 'short.flac', 'flac', 'audio/flac'),
                (dual, 'mov', 'audio/mp4')]

    def oracle(self, path, demuxer):
        streams = json.loads(subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-show_streams', '-of', 'json', str(path)],
            check=True, capture_output=True, timeout=30).stdout)['streams']
        audio = [s for s in streams if s['codec_type'] == 'audio']
        first = audio[0]
        if demuxer == 'mov':
            self.check(len(audio) == 2 and first['sample_rate'] == '8000' and first['channels'] == 1
                and audio[1]['sample_rate'] == '48000' and audio[1]['channels'] == 2
                and audio[1]['disposition']['default'] == 1 and first['disposition']['default'] == 0,
                'dualMOV: first8kmono and second48kstereo default fixture')
        pcm = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-f', demuxer, '-i', str(path), '-map', '0:a:0',
            '-vn', '-ac', '1', '-f', 'f32le', '-acodec', 'pcm_f32le', '-'],
            check=True, capture_output=True, timeout=30).stdout
        self.check(len(pcm) % 4 == 0, path.name + ': complete native first-stream float32 output')
        samples = array.array('f'); samples.frombytes(pcm)
        if sys.byteorder != 'little':
            samples.byteswap()
        self.check(all(math.isfinite(v) for v in samples), path.name + ': native float samples finite')
        current = [extrema(samples[i:i + 64]) for i in range(0, len(samples), 64)]
        levels = []
        while True:
            levels.append(b''.join(struct.pack('<ff', low, high) for low, high in current))
            if len(current) <= 1:
                break
            current = [extrema([v for pair in current[i:i + 2] for v in pair]) for i in range(0, len(current), 2)]
        rate = int(first['sample_rate'])
        # WaveformPeakPyramid.DurationUs rounds to nearest microsecond, ties to even.
        result = {'sourceSha256': sha(path.read_bytes()), 'sampleRate': rate, 'sampleCount': str(len(samples)),
            'durationUs': str(round(len(samples) * 1000000 / rate)), 'sourceChannels': int(first['channels']),
            'waveformChannels': 1, 'baseBucketSamples': 64, 'format': 'minmax-f32le-v1',
            'profile': 'self-contained-native-waveform-v1', 'levels': [{'level': n, 'bucketSamples': str(64 << n),
            'bucketCount': len(data) // 8} for n, data in enumerate(levels)]}
        for n, data in enumerate(levels):
            (self.output / ('oracle-' + path.name + '-' + str(n) + '.bin')).write_bytes(data)
        self.media.append({'file': str(path.relative_to(self.output)), 'sourceSha256': result['sourceSha256'],
            'streams': audio, 'mappedFirstPcmSha256': sha(pcm), 'sampleCount': len(samples),
            'metadata': result, 'levelSha256': [sha(data) for data in levels]})
        editing.save_json(self.output / 'media-fixtures.json', self.media)
        return result, levels

    def read_metadata(self, name, path, expected):
        actual = self.request(name, 'GET', path + '/waveform')
        self.check(actual['available'] is True and 'reason' not in actual, name + ': original preparation available')
        self.check(actual['projectSessionId'] == path.rsplit('/', 1)[1] and actual['protocolVersion'] == '1', name + ': session/protocol')
        for key, value in expected.items():
            self.check(actual[key] == value and type(actual[key]) is type(value), name + ': exact native ' + key)
        self.check(set(actual) == {*expected, 'projectSessionId', 'protocolVersion', 'available', 'waveformId'},
                   name + ': exact available metadata shape')
        return actual

    @staticmethod
    def window_path(path, metadata, level=0, start=0, count=1):
        return path + '/waveform/peaks?' + urlencode({'waveformId': metadata['waveformId'], 'level': level, 'start': start, 'count': count})

    def read_levels(self, name, path, metadata, oracle):
        for level, expected in enumerate(oracle):
            assembled = bytearray()
            buckets = len(expected) // 8
            for start in range(0, buckets, 8192):
                count = min(8192, buckets - start)
                headers, data = self.transport(f'{name}-level{level}-start{start}', 'GET',
                    self.window_path(path, metadata, level, start, count))
                self.check(headers['content-type'] == 'application/octet-stream', name + ': exact binary MIME')
                self.check(len(data) == count * 8 and int(headers['content-length']) == count * 8, name + ': exact8Nbytes')
                self.check(data == expected[start * 8:(start + count) * 8], name + ': independent native minmax floatbits exact')
                self.check(headers['cache-control'] == 'no-store' and headers['x-content-type-options'] == 'nosniff', name + ': private/security headers')
                self.check('accept-ranges' not in headers and 'content-range' not in headers, name + ': no byteRange advertisement')
                assembled.extend(data)
            self.check(bytes(assembled) == expected, name + f': entire level{level} exact including final partial bucket')
            self.windows.append({'name': name, 'waveformId': metadata['waveformId'], 'level': level,
                                 'bucketCount': buckets, 'sha256': sha(assembled)})
        editing.save_json(self.output / 'window-oracles.json', self.windows)

    def boundaries(self, path, metadata):
        base = self.window_path(path, metadata)
        origin = {'Origin': 'http://localhost:5173'}
        headers, content = self.transport('window-etag', 'GET', base, headers=origin)
        etag = headers['etag']
        self.check(headers['access-control-allow-origin'] == origin['Origin'], 'CORS exact approved origin')
        self.check({s.strip().lower() for s in headers['access-control-expose-headers'].split(',')} >= {'etag', 'content-length'}, 'CORS exposes ETag and length')
        for method in ('GET', 'HEAD'):
            head, empty = self.transport('window-head' if method == 'HEAD' else 'window-repeat', method, base, headers=origin)
            self.check(head['etag'] == etag and int(head['content-length']) == 8, method + ': same ETag and selected representation length')
            self.check(empty == (b'' if method == 'HEAD' else content), method + ': exact body behavior')
            for condition in (etag, 'W/' + etag, '"unrelated", W/' + etag, '*'):
                cached, empty = self.transport('conditional-' + method, method, base, status=304,
                    headers={**origin, 'If-None-Match': condition})
                self.check(empty == b'' and cached['etag'] == etag and cached['cache-control'] == 'no-store', method + ': authenticated304 empty/security')
                self.check('etag' in cached['access-control-expose-headers'].lower(), method + ':304 ETag exposed')
            ranged, body = self.transport('range-refused-' + method, method, base, status=400, headers={'Range': 'bytes=0-7'})
            self.check('content-range' not in ranged and 'accept-ranges' not in ranged, method + ': Range400 no byte-range headers')
            if method == 'HEAD':
                self.check(body == b'', 'HEAD Range error body empty')
            else:
                self.check(json.loads(body)['field'] == 'Range', 'GET Range error actual field')
        changed, _ = self.transport('window-other-coordinate', 'GET', self.window_path(path, metadata, start=1))
        self.check(changed['etag'] != etag, 'ETag includes window coordinates even for coincident peaks')
        query = base.split('?', 1)[1]
        bad = [(query + '&x=1', 'x'), (query + '&expectedRevision=0', 'expectedRevision')]
        for key in ('waveformId', 'level', 'start', 'count'):
            value = {'waveformId': metadata['waveformId'], 'level': '0', 'start': '0', 'count': '1'}[key]
            bad += [(query + '&' + key + '=' + value, key),
                    ('&'.join(p for p in query.split('&') if not p.startswith(key + '=')), key),
                    (query.replace(key + '=' + value, key + '='), key)]
        for key in ('level', 'start', 'count'):
            for value in ('-1', '%2B1', '%201', '1.0', '2147483648'):
                old = '1' if key == 'count' else '0'
                bad.append((query.replace(key + '=' + old, key + '=' + value), key))
        bad += [(query.replace('count=1', 'count=0'), 'count'), (query.replace('count=1', 'count=8193'), 'count'),
                (query.replace('level=0', 'level=100'), 'level'),
                (query.replace('start=0', 'start=' + str(metadata['levels'][0]['bucketCount'])), 'start')]
        for index, (suffix, field) in enumerate(bad):
            error = self.request('query-invalid-' + str(index), 'GET', path + '/waveform/peaks?' + suffix, status=400, code='invalidRequest')
            self.check(error['field'] == field, 'query rejected with actual field ' + field)
        for query in ('expectedRevision=0', 'unknown=1'):
            error = self.request('metadata-unknown-query', 'GET', path + '/waveform?' + query, status=400, code='invalidRequest')
            self.check(error['field'] == query.split('=')[0], 'metadata accepts no query/revision')
        self.request('unknown-waveform', 'GET', base.replace(metadata['waveformId'], 'unknown'), status=404, code='resourceNotFound')
        other = self.register('foreign-client', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        self.request('cross-client-metadata', 'GET', path + '/waveform', client=other['clientId'], status=404, code='sessionNotFound')
        self.request('cross-client-conditional', 'GET', base, client=other['clientId'], headers={'If-None-Match': etag}, status=404, code='sessionNotFound')
        self.request('unauthenticated-conditional', 'GET', base, headers={'Authorization': 'Bearer invalid', 'If-None-Match': '*'}, status=401, code='unauthorizedClient')
        self.request('wrong-host-conditional', 'GET', base, host='old-host', headers={'If-None-Match': etag}, status=409, code='hostRestarted')
        self.request('foreign-origin-conditional', 'GET', base, headers={'Origin': 'http://foreign.invalid', 'If-None-Match': etag}, status=403, code='unauthorizedClient')

    def run(self):
        fixtures = self.build_fixtures()
        self.start('waveform')
        self.connect('waveform')
        lyrics = self.upload('waveform.lrc', b'[00:00.00]Waveform original line\n', 'text/plain')
        for index, (audio, demuxer, mime) in enumerate(fixtures):
            expected, oracle = self.oracle(audio, demuxer)
            asset = self.upload(audio.name, audio.read_bytes(), mime)
            name = 'fixture-' + str(index)
            created = self.request(name + '-create', 'POST', '/api/v1/project-sessions', status=201, value={
                'name': name, 'audioAssetId': asset['assetId'], 'lyricsAssetId': lyrics['assetId'],
                'composition': {'canvasWidth': 320, 'canvasHeight': 180}})
            path = '/api/v1/project-sessions/' + created['projectSessionId']
            self.check(created['audioDurationUs'] == int(expected['durationUs']), name + ': create duration is complete mapped PCM')
            metadata = self.read_metadata(name + '-metadata', path, expected)
            before = self.request(name + '-history-before', 'GET', path + '/history')
            self.read_levels(name, path, metadata, oracle)
            if index == 0:
                self.boundaries(path, metadata)
            self.check(self.request(name + '-history-after', 'GET', path + '/history') == before, name + ': reads/errors do not edit history')
            lines = self.request(name + '-lines', 'GET', path + '/lyrics/lines?branchId=primary')['lines']
            edited = self.request(name + '-lyrics-edit', 'POST', path + '/commands', value=self.command('0', name + '-edit', {
                'type': 'lyrics.setLineText', 'target': {'branchId': 'primary', 'lineId': lines[0]['id']}, 'text': 'Changed lyric only'}))
            self.check(edited['projectRevision'] == '1', name + ': lyrics revision changes')
            self.check(self.read_metadata(name + '-after-edit', path, expected) == metadata, name + ': audio identity independent of lyrics revision')
            old_path = path
            path, opened = self.save_close_open(name + '-roundtrip', path, '1', name + '-saved')
            self.request(name + '-old-metadata-gone', 'GET', old_path + '/waveform', status=404, code='sessionNotFound')
            self.request(name + '-old-peaks-gone', 'GET', self.window_path(old_path, metadata), status=404, code='sessionNotFound')
            reopened = self.read_metadata(name + '-reopened', path, expected)
            self.check(reopened['waveformId'] == metadata['waveformId'], name + ': save-close-open preserves complete audio identity')
            self.read_levels(name + '-reopened', path, reopened, oracle)
            self.close(name + '-close', path, opened['projectRevision'])
            self.request(name + '-closed-waveform', 'GET', path + '/waveform', status=404, code='sessionNotFound')
            self.request(name + '-closed-peaks', 'GET', self.window_path(path, reopened), status=404, code='sessionNotFound')
        self.check(bool(self.worker_identities), 'actual managed Worker executable identities observed')
        self.stop('waveform')
        # Includes deployment input/evidence identity, but never writes access/resume/bootstrap secrets.
        files = {str(p.relative_to(self.output)): sha(p.read_bytes()) for p in self.output.rglob('*') if p.is_file()}
        for name in files:
            self.check(not any(secret.encode() in (self.output / name).read_bytes() for secret in self.secrets), 'credential scan: ' + name)
        editing.save_json(self.output / 'evidence-manifest.json', {'files': files, 'deploymentManifestSha256': sha((self.dist / 'deployment.json').read_bytes())})
        self.check(not any(secret.encode() in (self.output / 'evidence-manifest.json').read_bytes() for secret in self.secrets),
                   'credential scan: evidence-manifest.json')
        summary = {'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions) + 1,
            'assertionLabels': self.assertions + ['credential scan: summary.json'], 'runs': self.runs,
            'successfulTerminalJobIds': sorted(self.successful_jobs), 'completeLevelComparisons': len(self.windows),
            'workerIdentities': [{'pid': p, 'startTicks': t} for p, t in sorted(self.worker_identities)],
            'scope': 'Owned WAV/MP3/FLAC/dualMOV real Worker HTTP waveform complete level/window bytes against independent mapped native mono PCM; '
                     'no browser/sample-aligned playback, performance, total-heap cap or real64MiB exhaustion claim. Low-budget cache admission/lifetime is separate Hosting regression.'}
        candidate = self.output / '.summary.candidate'
        editing.save_json(candidate, summary)
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), 'credential scan: summary.json')
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({key: summary[key] for key in ('status', 'httpRequests', 'assertions', 'completeLevelComparisons')}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    output.mkdir(parents=True, exist_ok=False)
    probe = WaveformProbe(output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
