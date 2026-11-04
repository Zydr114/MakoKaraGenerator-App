#!/usr/bin/env python3
"""Finite real HTTP video-background/PNG/export acceptance with full-size Runtime oracle."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import struct
import subprocess
import sys
import time
import numpy as np

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('m06_completion', ROOT / 'tools/backend-api-completion-probe/probe.py')
completion = importlib.util.module_from_spec(spec)
spec.loader.exec_module(completion)
save_json = completion.save_json

def sha(data):
    return hashlib.sha256(data).hexdigest()

class VideoProbe(completion.CompletionProbe):
    def __init__(self, output, dist, oracle):
        super().__init__(output, dist, 300, oracle)
        self.comparisons = []

    def safe_text(self, value):
        text = str(value)
        for secret in sorted(set(self.secrets + list(self.tokens.values())), key=len, reverse=True):
            if secret:
                text = text.replace(secret, '<redacted>')
        return re.sub(r'(?i)(bearer\s+)[^\s\"\'<>]+', r'\1<redacted>', text)

    def command_run(self, name, command, *, binary=False, timeout=180):
        result = subprocess.run(command, capture_output=True, timeout=timeout)
        (self.output / (name + '-stderr.log')).write_text(self.safe_text(result.stderr.decode(errors='replace')))
        save_json(self.output / (name + '-command.json'), {'argv': list(map(str, command)), 'exitCode': result.returncode})
        self.check(result.returncode == 0, name + ': command exit zero')
        if not binary:
            (self.output / (name + '-stdout.log')).write_text(self.safe_text(result.stdout.decode(errors='replace')))
        return result.stdout

    def decode_png(self, name, png):
        self.check(png[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack('>II', png[16:24]) == (480, 270), name + ': PNG dimensions')
        command = ['/usr/bin/ffmpeg', '-v', 'error', '-i', 'pipe:0', '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1']
        result = subprocess.run(command, input=png, capture_output=True, timeout=30)
        self.check(result.returncode == 0 and len(result.stdout) == 480 * 270 * 4, name + ': complete PNG RGBA')
        (self.output / (name + '.png')).write_bytes(png)
        save_json(self.output / (name + '-decode.json'), {'command': command, 'pngSha256': sha(png), 'rgbaSha256': sha(result.stdout), 'width': 480, 'height': 270})
        return result.stdout

    def make_source(self, kind):
        target = self.output / (kind + '.mkv')
        filter_arg = 'testsrc2=size=1920x1080:rate=4:duration=2'
        command = ['/usr/bin/ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', filter_arg, '-an']
        if kind == 'alpha':
            command += ['-vf', 'format=rgba,colorchannelmixer=aa=0.5', '-pix_fmt', 'bgra']
        else:
            command += ['-pix_fmt', 'yuv420p']
        command += ['-c:v', 'ffv1', '-y', str(target)]
        self.command_run(kind + '-generate', command)
        probe_command = ['/usr/bin/ffprobe', '-v', 'error', '-count_frames', '-show_streams', '-of', 'json', str(target)]
        info = json.loads(self.command_run(kind + '-source-probe', probe_command))
        stream = next(s for s in info['streams'] if s['codec_type'] == 'video')
        self.check(stream['width'] == 1920 and stream['height'] == 1080 and int(stream['nb_read_frames']) == 8, kind + ': 1920x1080 8 source frames')
        raw = self.command_run(kind + '-source-alpha', ['/usr/bin/ffmpeg', '-v', 'error', '-i', str(target), '-frames:v', '3', '-f', 'rawvideo', '-pix_fmt', 'bgra', 'pipe:1'], binary=True)
        size = 1920 * 1080 * 4
        self.check(len(raw) == size * 3 and sha(raw[:size]) != sha(raw[2*size:3*size]), kind + ': three complete source frames with actual temporal change')
        alpha = np.frombuffer(raw[:size], dtype=np.uint8)[3::4]
        self.check((int(alpha.min()) < 255 and int(alpha.max()) > 0) if kind == 'alpha' else int(alpha.min()) == 255, kind + ': actual decoded alpha')
        save_json(self.output / (kind + '-source.json'), {'sha256': sha(target.read_bytes()), 'bytes': target.stat().st_size, 'stream': stream,
            'decodedAlphaMin': int(alpha.min()), 'decodedAlphaMax': int(alpha.max()), 'decodedFrameSha256': [sha(raw[i*size:(i+1)*size]) for i in range(3)],
            'ffmpegVersion': self.command_run(kind + '-ffmpeg-version', ['/usr/bin/ffmpeg', '-version']).decode().splitlines()[0],
            'scope': 'Probe-owned FFV1 video; decoder remains full-resolution; source sample alpha independently decoded.'})
        return target

    def create_project(self, kind, source):
        audio, _ = self.wav(2, 8000, 220, 0)
        audio_asset = self.upload(kind + '-audio', kind + '.wav', 'audio/wav', audio)
        lyric = self.upload(kind + '-lyrics', kind + '.lrc', 'text/plain', b'[00:00.00]<00:00.00>VIDEO\n')
        value = {'name': kind, 'audioAssetId': audio_asset['assetId'], 'lyricsAssetId': lyric['assetId'], 'importerId': 'lrc.enhanced',
                 'composition': {'canvasWidth': 480, 'canvasHeight': 270, 'frameRateNumerator': 24, 'frameRateDenominator': 1, 'outputDurationUs': 2000000}}
        created = self.request(kind + '-create', 'POST', '/api/v1/project-sessions', status=201, value=value)
        self.project, self.revision = created['projectSessionId'], created['projectRevision']
        lines = self.lyrics(kind + '-lines')['lines']
        self.check(len(lines) == 1 and len(lines[0]['tokens']) == 1, kind + ': deterministic timed lyric')
        target = {'branchId': 'primary', 'lineId': lines[0]['id']}
        self.send(kind + '-line-time', {'type': 'lyrics.setTimeRange', 'target': target, 'startUs': '0', 'endUs': '2000000'})
        self.send(kind + '-token-time', {'type': 'lyrics.setTokenTimes', 'changes': [{'target': {**target, 'tokenId': lines[0]['tokens'][0]['id']}, 'startUs': '0', 'endUs': '2000000'}]})
        asset = self.upload(kind + '-video-upload', source.name, 'video/x-matroska', source.read_bytes())
        self.send(kind + '-background', {'type': 'composition.setBackgroundAsset', 'assetId': asset['assetId'], 'kind': 'video', 'fit': 'contain', 'playback': 'loop'})
        handle = self.request(kind + '-handle', 'POST', '/api/v1/file-handles', status=201, value={'rootGrantId': 'projects', 'relativePath': kind})['handleId']
        self.long_job(kind + '-save', self.request(kind + '-save', 'POST', self.project_path + '/save-jobs', status=202,
            value={'expectedRevision': self.revision, 'targetHandleId': handle}))
        self.long_job(kind + '-saved-close', self.request(kind + '-saved-close', 'POST', self.project_path + '/close', status=202,
            value={'expectedRevision': self.revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))
        opened = self.long_job(kind + '-reopen', self.request(kind + '-reopen', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle}))['result']
        self.project, self.revision = opened['projectSessionId'], opened['projectRevision']
        saved = self.output / 'profile/projects' / kind
        self.check((saved / 'project.json').is_file(), kind + ': persisted owned project')
        return saved

    def oracle_frames(self, kind, saved, transparent):
        label = kind + ('-transparent' if transparent else '-opaque')
        prefix = self.output / (label + '-oracle')
        self.command_run(label + '-oracle', [str(self.oracle), str(saved), str(prefix), str(transparent).lower()], timeout=300)
        info = json.loads(prefix.with_suffix('.json').read_text())
        expected_media = json.loads((self.output / (kind + '-source.json')).read_text())
        self.check(info['mediaSha256'] == expected_media['sha256'] == info['hash'] and info['byteLength'] == expected_media['bytes'], label + ': exact saved media closure')
        self.check(info['totalFrames'] == 48 and info['frameRateNumerator'] == 24 and info['frameRateDenominator'] == 1, label + ': oracle frame plan')
        self.check(info['transparent'] == transparent and (transparent or info['reads'] > 0) and not info['badSize'], label + ': original-size oracle path')
        return label, prefix, info

    def png_check(self, label, prefix, info, transparent):
        preview = self.request(label + '-preview', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': self.revision},
            'size': {'width': 480, 'height': 270}, 'alphaMode': 'straight' if transparent else 'opaque', 'purpose': 'preview', 'renderBackend': 'cpu'})
        try:
            results = []
            for sequence, index in enumerate((0, 6, 12, 18), 1):
                item = info['frames'][index]
                actual = self.frame(label + '-png-' + str(index), preview, item['timeUs'], sequence)
                expected_png = Path(str(prefix) + f'-{index:03d}.png').read_bytes()
                expected = self.decode_png(label + '-expected-' + str(index), expected_png)
                delta = np.abs(np.frombuffer(actual, dtype=np.uint8).astype(np.int16) - np.frombuffer(expected, dtype=np.uint8).astype(np.int16))
                maximum = int(delta.max())
                self.check(maximum <= 2, label + f': original-size PNG oracle frame {index} maximum channel difference <=2')
                results.append({'index': index, 'timeUs': item['timeUs'], 'actualRgbaSha256': sha(actual), 'oracleRgbaSha256': sha(expected), 'maxChannelDifference': maximum})
            self.comparisons.append({'kind': 'png', 'label': label, 'frames': results})
        finally:
            self.close_preview(label, preview)

    def export_check(self, label, prefix, info, transparent):
        fmt = 'transparentProRes4444' if transparent else 'opaqueMp4'
        job = self.request(label + '-export', 'POST', self.project_path + '/export-jobs', status=202,
            value={'expectedRevision': self.revision, 'format': fmt, 'includeAudio': False, 'quality': 'standard', 'renderBackend': 'cpu'})
        terminal = self.long_job(label + '-export', job)
        self.check(terminal['input']['sourceProfile'] == info['runtimeInputProfile'] == 'runtime-project-input-v1', label + ': actual video runtime input profile')
        refs = terminal['exportResult']; path = '/api/v1/jobs/' + terminal['jobId']
        _, report_raw, _ = self.fetch(label + '-report', path + '/export-result')
        _, media, _ = self.fetch(label + '-movie', path + '/export-content')
        report = json.loads(report_raw)
        self.check(sha(report_raw) == refs['report']['sha256'] and len(report_raw) == refs['report']['sizeBytes'], label + ': report identity')
        self.check(sha(media) == refs['artifact']['sha256'] == report['sha256'] and len(media) == refs['artifact']['sizeBytes'], label + ': movie identity')
        self.check(report['strictlyVerified'] and report['expectedFrames'] == report['decodedFrames'] == report['framesWritten'] == 48
                   and report['failedFrames'] == report['droppedFrames'] == 0 and report['hasAlpha'] == transparent, label + ': export complete frame plan')
        file = self.output / (label + ('.mov' if transparent else '.mp4'))
        file.write_bytes(media)
        probe = json.loads(self.command_run(label + '-export-probe', ['/usr/bin/ffprobe', '-v', 'error', '-count_frames', '-show_streams', '-of', 'json', str(file)]))
        stream = next(s for s in probe['streams'] if s['codec_type'] == 'video')
        self.check(stream['width'] == 480 and stream['height'] == 270 and int(stream['nb_read_frames']) == 48 and stream['r_frame_rate'] == '24/1', label + ': independently probed frame plan')
        raw = self.command_run(label + '-export-decode', ['/usr/bin/ffmpeg', '-v', 'error', '-i', str(file), '-map', '0:v:0', '-f', 'rawvideo', '-pix_fmt', 'bgra', 'pipe:1'], binary=True, timeout=300)
        self.check(len(raw) == 48 * 480 * 270 * 4, label + ': complete video decoded')
        results = []; size = 480 * 270 * 4
        total_err = total_values = alpha_visible = alpha_match = alpha_clear = 0
        for index, item in enumerate(info['frames']):
            actual = np.frombuffer(raw[index * size:(index + 1) * size], dtype=np.uint8).reshape(-1, 4).astype(np.int32)
            expected = np.fromfile(str(prefix) + f'-{index:03d}.bgra', dtype=np.uint8).reshape(-1, 4).astype(np.int32)
            if transparent:
                opacity = expected[:, 3]; mask = opacity > 40
                alpha_visible += int(mask.sum()); alpha_match += int(np.count_nonzero(np.abs(actual[mask, 3] - opacity[mask]) <= 8))
                alpha_clear += int(np.count_nonzero(actual[:, 3] == 0))
                expected[:, :3] = np.where(opacity[:, None] > 0, np.minimum(255, (expected[:, :3] * 255 + opacity[:, None] // 2) // np.maximum(1, opacity[:, None])), 0)
                error = np.abs(actual[mask, :3] - expected[mask, :3])
            else:
                error = np.abs(actual[:, :3] - expected[:, :3])
            total_err += int(error.sum()); total_values += error.size
            results.append({'index': index, 'timeUs': item['timeUs'], 'decodedBgraSha256': sha(raw[index * size:(index + 1) * size]), 'oraclePremulBgraSha256': item['bgraSha256'], 'rgbAbsoluteErrorSum': int(error.sum()), 'rgbChannels': int(error.size)})
        mae = total_err / max(total_values, 1)
        self.check(mae < (15 if transparent else 8), label + ': full sequence RGB MAE within lossy bound')
        if transparent:
            self.check(alpha_visible > 100 and alpha_match >= .9 * alpha_visible and alpha_clear > 1000, label + ': alpha coverage and agreement')
        sequence_scores = []
        if not transparent:
            indices = (0, 12, 24, 36)
            references = [np.fromfile(str(prefix) + f'-{index:03d}.bgra', dtype=np.uint8).reshape(-1, 4)[:, :3].astype(np.int16) for index in indices]
            for row, index in enumerate(indices):
                sample = np.frombuffer(raw[index * size:(index + 1) * size], dtype=np.uint8).reshape(-1, 4)[:, :3].astype(np.int16)
                scores = [float(np.abs(sample - reference).mean()) for reference in references]
                self.check(scores[row] == min(scores) and all(scores[row] < score for i, score in enumerate(scores) if i != row),
                           label + f': decoded frame {index} matches its own oracle time most closely')
                sequence_scores.append({'index': index, 'candidateIndices': list(indices), 'rgbMae': scores})
        self.comparisons.append({'kind': 'export', 'label': label, 'frameCount': len(results), 'rgbMae': mae,
            'alphaVisible': alpha_visible, 'alphaMatched': alpha_match, 'alphaClear': alpha_clear,
            'sequenceScores': sequence_scores, 'frames': results})

    def run_video(self):
        self.deployment = json.loads((self.dist / 'deployment.json').read_text())
        self.check(not self.deployment['sourceDirty'], 'clean source deployment')
        self.start('video'); self.connect('video')
        self.check(self.current_ready['version'] == self.deployment['version'], 'running deployment version')
        for kind in ('yuv', 'alpha'):
            source = self.make_source(kind)
            saved = self.create_project(kind, source)
            for transparent in (False, True):
                label, prefix, info = self.oracle_frames(kind, saved, transparent)
                self.png_check(label, prefix, info, transparent)
                self.export_check(label, prefix, info, transparent)
            self.send(kind + '-long-duration', {'type': 'composition.setOutputDuration', 'durationUs': '60000000'})
            job = self.request(kind + '-cancel-export', 'POST', self.project_path + '/export-jobs', status=202,
                value={'expectedRevision': self.revision, 'format': 'opaqueMp4', 'includeAudio': False, 'quality': 'standard', 'renderBackend': 'cpu'})
            deadline = time.monotonic() + 30
            while True:
                state = self.request(kind + '-cancel-before', 'GET', '/api/v1/jobs/' + job['jobId'])
                progress = state.get('progress') or {}
                if state['state'] == 'running' and progress.get('stage') == 'rendering' and progress.get('completedFrames', 0) > 0:
                    break
                self.check(state['state'] not in ('succeeded', 'failed', 'cancelled', 'unknown') and time.monotonic() < deadline,
                           kind + ': export renders a frame before cancellation')
                time.sleep(.05)
            self.cancel_job(kind + '-cancel', job['jobId'])
            cancelled = self.long_job(kind + '-cancelled', job, expected='cancelled')
            self.check(cancelled.get('exportResult') is None, kind + ': cancelled export has no published result')
            self.long_job(kind + '-close', self.request(kind + '-close', 'POST', self.project_path + '/close', status=202,
                value={'expectedRevision': self.revision, 'policy': 'discard', 'activeJobs': 'cancelAndWait'}))

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--oracle', required=True, type=Path, help='matched video RuntimeOracle apphost')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    oracle, dist, output = args.oracle.resolve(), args.dist.resolve(), args.output.resolve()
    subprocess.run([sys.executable, str(Path(__file__).with_name('verify_oracle.py')), '--dist', str(dist), '--kind', 'video', '--oracle-dir', str(oracle.parent.parent)], check=True)
    output.mkdir(parents=True, exist_ok=False)
    save_json(output / 'tool-inputs.json', {'argv': sys.argv, 'sources': {
        str(path.resolve()): sha(path.read_bytes()) for path in
        (Path(__file__), Path(__file__).with_name('VideoOracle.cs'), Path(__file__).with_name('verify_oracle.py'),
         Path(__file__).with_name('build_oracle.py'), ROOT / 'tools/backend-api-completion-probe/probe.py',
         ROOT / 'tools/backend-basic-business-http-probe/probe.py', ROOT / 'tools/backend-g20g24-http-probe/probe.py',
         ROOT / 'tools/backend-http-probe/probe.py', ROOT / 'tools/backend-web-host/smoke.py',
         ROOT / 'tools/backend-web-host/host.py')},
        'oracleInputManifestSha256': sha((oracle.parent.parent / 'input-manifest.json').read_bytes()),
        'deploymentManifestSha256': sha((dist / 'deployment.json').read_bytes())})
    probe = VideoProbe(output, dist, oracle); failure = None
    try:
        probe.run_video()
    except BaseException as error:
        failure = error
        save_json(output / 'failure.json', {'errorType': type(error).__name__, 'detail': probe.safe_text(error)})
    finally:
        try:
            probe.stop('video')
            if probe.current_ready is not None:
                probe.check(bool(probe.worker_identities), 'actual Worker identities observed and drained')
        except BaseException as error:
            failure = failure or error
            save_json(output / 'shutdown-failure.json', {'errorType': type(error).__name__, 'detail': probe.safe_text(error)})
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error; scanned = None
    save_json(output / 'summary.json', {'status': 'failed' if failure else 'passed', 'error': probe.safe_text(failure) if failure else None,
        'httpRequests': probe.request_count, 'assertions': len(probe.assertions), 'assertionLabels': probe.assertions,
        'credentialScannedFiles': scanned, 'terminals': probe.terminals, 'comparisons': probe.comparisons,
        'deploymentManifestSha256': sha((dist / 'deployment.json').read_bytes()),
        'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(probe.worker_identities)],
        'scope': 'Real HTTP video input; direct original-size Runtime oracle. Transparent rendering omits background. No performance, decoder-native scaling, or peak-memory claim.'})
    save_json(output / 'evidence-manifest.json', {'files': {str(p.relative_to(output)): sha(p.read_bytes()) for p in sorted(output.rglob('*')) if p.is_file()}})
    print(json.dumps({'status': 'failed' if failure else 'passed', 'error': probe.safe_text(failure) if failure else None}))
    return 1 if failure else 0

if __name__ == '__main__':
    sys.exit(main())
