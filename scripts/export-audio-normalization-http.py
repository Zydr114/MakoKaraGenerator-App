#!/usr/bin/env python3
"""Finite export audio normalization acceptance against a published deployment.

Uses private temporary project copies and an isolated loopback host. The output
folder contains only an allowlisted summary, never media, credentials, or logs.
"""
import argparse
import hashlib
import importlib.util
import json
import math
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import traceback
from pathlib import Path
from urllib.request import Request

ROOT = Path(__file__).resolve().parents[1]
sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('g1_http', ROOT / 'scripts/cross-platform-g1-http.py')
g1 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g1)
check, native, sha = g1.check, g1.native, g1.sha


class Session(g1.Session):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.owned_processes = {}

    @staticmethod
    def process_tree():
        result = {}
        if sys.platform == 'linux':
            for entry in Path('/proc').iterdir():
                if not entry.name.isdecimal():
                    continue
                try:
                    fields = (entry / 'stat').read_text().rsplit(') ', 1)[1].split()
                    result[int(entry.name)] = (int(fields[1]), fields[19], fields[0])
                except (OSError, IndexError, ValueError):
                    pass
        return result

    def track_processes(self):
        if not self.process:
            return
        tree = self.process_tree()
        parents = {self.process.pid}
        while parents:
            next_parents = set()
            for pid, (parent, start, _) in tree.items():
                if pid in parents or parent in parents:
                    self.owned_processes[pid] = start
                    if pid not in parents:
                        next_parents.add(pid)
            parents = next_parents

    def request(self, *args, **kwargs):
        self.track_processes()
        return super().request(*args, **kwargs)

    def stop(self):
        self.track_processes()
        super().stop()
        if sys.platform == 'linux':
            tree = self.process_tree()
            remaining = [pid for pid, start in self.owned_processes.items()
                         if pid in tree and tree[pid][1] == start and tree[pid][2] != 'Z']
            check(not remaining, 'owned host, server, worker and media processes must drain')
            self.evidence['ownedProcessesObserved'] = len(self.owned_processes)
            self.evidence['ownedProcessesDrained'] = True

    def wait(self, accepted, expected='succeeded'):
        accepted = accepted.get('job', accepted)
        deadline = time.monotonic() + self.args.timeout
        next_progress = time.monotonic() + 30
        while time.monotonic() < deadline:
            self.heartbeat()
            current = self.request('GET', '/api/v1/jobs/' + accepted['jobId'])
            if current['state'] in ('succeeded', 'failed', 'cancelled', 'unknown'):
                check(current['state'] == expected, 'job terminal must be ' + expected + '; actual '
                      + current['state'] + '; code ' + str(current.get('failureCode')))
                check(not current['canCancel'], 'terminal job cannot cancel')
                return current
            if time.monotonic() >= next_progress:
                progress = current.get('progress') or {}
                print('job progress: state={} frames={}/{}'.format(current['state'],
                    progress.get('completedFrames', '?'), progress.get('totalFrames', '?')), flush=True)
                next_progress = time.monotonic() + 30
            time.sleep(.5)
        raise AssertionError('job timeout')


def stage(name):
    print(name, flush=True)


def file_hash(path):
    digest = hashlib.sha256()
    with path.open('rb') as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b''):
            digest.update(chunk)
    return digest.hexdigest()


def snapshot(project):
    files = [project / 'project.json']
    data = project / 'data'
    if data.exists():
        files += sorted(p for p in data.rglob('*') if p.is_file())
    check(all(p.is_file() and not p.is_symlink() for p in files), 'project files must be regular and not symlinks')
    return {str(p.relative_to(project)): file_hash(p) for p in files}


def make_audio(args, target, seconds, codec):
    native([args.ffmpeg, '-v', 'error', '-y', '-f', 'lavfi', '-i', 'sine=frequency=997:sample_rate=48000',
            '-t', str(seconds), '-ac', '1', '-c:a', codec, str(target)])
    raw = native([args.ffmpeg, '-v', 'error', '-xerror', '-i', str(target), '-map', '0:a:0',
                  '-ac', '1', '-ar', '48000', '-f', 's16le', 'pipe:1'])
    return len(raw) * 1000000 // (2 * 48000)


def decoded_duration(args, source):
    probe = subprocess.run([args.ffprobe, '-v', 'error', '-select_streams', 'a:0',
        '-show_entries', 'stream=sample_rate', '-of', 'json', str(source)],
        capture_output=True, timeout=args.timeout)
    check(probe.returncode == 0, 'source audio probe')
    streams = json.loads(probe.stdout)['streams']
    check(len(streams) == 1, 'source audio stream present')
    rate = int(streams[0]['sample_rate'])
    check(rate > 0, 'source audio sample rate')
    # Count decoded mono samples on disk, keeping the full track out of memory.
    with tempfile.TemporaryFile() as decoded:
        result = subprocess.run([args.ffmpeg, '-v', 'error', '-xerror', '-i', str(source), '-map', '0:a:0',
            '-ac', '1', '-ar', str(rate), '-f', 's16le', 'pipe:1'], stdout=decoded,
            stderr=subprocess.PIPE, timeout=args.timeout)
        check(result.returncode == 0, 'source audio complete decode')
        samples_bytes = decoded.tell()
    check(samples_bytes > 0 and samples_bytes % 2 == 0, 'source audio decoded samples')
    return samples_bytes // 2 * 1000000 // rate


def download(session, path, target):
    session.heartbeat()
    session.evidence['requests'] += 1
    headers = {'X-Mako-Host-Instance-Id': session.host, 'X-Mako-Client-Id': session.client,
               'Authorization': 'Bearer ' + session.token}
    digest, size = hashlib.sha256(), 0
    with session.http.open(Request(session.base + path, headers=headers), timeout=120) as response, target.open('wb') as out:
        check(response.status == 200, 'media download HTTP status')
        for chunk in iter(lambda: response.read(1024 * 1024), b''):
            out.write(chunk)
            digest.update(chunk)
            size += len(chunk)
    return size, digest.hexdigest()


def run_export(session, args, project, revision, name, fmt, requested_audio, effective_audio,
               expected_frames, source_duration=None, tail_silent=False, generated=False):
    path = '/api/v1/project-sessions/' + project
    accepted = session.request('POST', path + '/export-jobs', status=202, value={
        'expectedRevision': revision, 'format': fmt, 'includeAudio': requested_audio,
        'renderBackend': args.render_backend})
    check(accepted['exportInput']['includeAudio'] is effective_audio, name + ' effective accepted includeAudio')
    terminal = session.wait(accepted)
    check(terminal['exportInput']['includeAudio'] is effective_audio, name + ' effective terminal includeAudio')
    base = '/api/v1/jobs/' + terminal['jobId']
    report_bytes = session.request('GET', base + '/export-result', binary=True)
    ref = terminal['exportResult']['report']
    check(len(report_bytes) == int(ref['sizeBytes']) and sha(report_bytes) == ref['sha256'], name + ' report size/hash')
    report = json.loads(report_bytes)
    check(report['audioIncluded'] is effective_audio and report['strictlyVerified'], name + ' report verification and audio')
    check(report['expectedFrames'] == report['decodedFrames'] == report['framesWritten'] == expected_frames
          and report['failedFrames'] == report['droppedFrames'] == 0, name + ' exact frame plan')
    duration = int(report['derivedVideoDurationUs'])
    check(abs(int(report['actualVideoDurationUs']) - duration) <= 50000, name + ' actual video duration')
    if effective_audio:
        check(report['audioDurationUs'] is not None and report['actualAudioDurationUs'] is not None, name + ' reported audio durations')
        if source_duration is not None:
            check(abs(int(report['audioDurationUs']) - source_duration) <= 1000, name + ' source decoded audio duration')
        check(abs(int(report['actualAudioDurationUs']) - duration) <= 50000, name + ' output audio duration')
    else:
        check(report['audioDurationUs'] is None and report['actualAudioDurationUs'] is None and report['audioCodec'] is None,
              name + ' no reported audio')
    target = session.profile / ('result-' + name + ('.mov' if fmt == 'transparentProRes4444' else '.mp4'))
    size, digest = download(session, base + '/export-content', target)
    artifact = terminal['exportResult']['artifact']
    check(size == int(artifact['sizeBytes']) == report['mediaSize'] and digest == artifact['sha256'] == report['sha256'], name + ' media size/hash')
    probed = subprocess.run([args.ffprobe, '-v', 'error', '-count_frames', '-show_streams', '-of', 'json', str(target)],
                            capture_output=True, timeout=args.timeout)
    check(probed.returncode == 0, name + ' independent media probe')
    probe = json.loads(probed.stdout)
    video = [s for s in probe['streams'] if s['codec_type'] == 'video']
    audio = [s for s in probe['streams'] if s['codec_type'] == 'audio']
    check(len(video) == 1 and int(video[0]['nb_read_frames']) == expected_frames, name + ' independently counted frames')
    check(video[0]['width'] == report['width'] and video[0]['height'] == report['height']
          and video[0]['r_frame_rate'] == str(report['frameRateNumerator']) + '/' + str(report['frameRateDenominator']),
          name + ' independently probed dimensions and rational frame rate')
    check(len(audio) == int(effective_audio), name + ' actual audio stream presence')
    if effective_audio:
        check(audio[0]['codec_name'] == ('aac' if fmt == 'opaqueMp4' else 'pcm_s16le'), name + ' audio codec')
        check(abs(float(audio[0]['duration']) * 1000000 - int(report['actualAudioDurationUs'])) <= 2000, name + ' independently probed output audio duration')
    decoded = subprocess.run([args.ffmpeg, '-v', 'error', '-xerror', '-i', str(target), '-map', '0:v:0',
            *(['-map', '0:a:0'] if effective_audio else []), '-f', 'null', '-'],
            stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, timeout=args.timeout)
    check(decoded.returncode == 0, name + ' complete media decode')
    if generated and effective_audio:
        def peak(start):
            pcm = native([args.ffmpeg, '-v', 'error', '-xerror', '-ss', str(start), '-t', '0.4', '-i', str(target),
                          '-map', '0:a:0', '-ac', '1', '-ar', '48000', '-f', 'f32le', 'pipe:1'])
            check(len(pcm) >= 48000, name + ' audio sample window present')
            return max(abs(x[0]) for x in struct.iter_unpack('<f', pcm))
        check(peak(0.2) > .01, name + ' source tone retained')
        if tail_silent:
            check(peak((duration / 1000000) - .45) < .01, name + ' padded tail silent')
        else:
            check(peak((duration / 1000000) - .45) > .01, name + ' long source retained through cutoff')
    target.unlink()
    return {'name': name, 'requestedAudio': requested_audio, 'effectiveAudio': effective_audio,
            'frames': expected_frames, 'videoDurationUs': duration, 'sourceAudioDurationUs': report['audioDurationUs'],
            'outputAudioDurationUs': report['actualAudioDurationUs'], 'mediaBytes': size, 'mediaSha256': digest,
            'reportSha256': sha(report_bytes), 'completeDecode': True}


def close(session, project, revision):
    accepted = session.request('POST', '/api/v1/project-sessions/' + project + '/close', status=202,
        value={'expectedRevision': revision, 'policy': 'discard', 'activeJobs': 'wait'})
    session.wait(accepted)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--project', type=Path)
    parser.add_argument('--render-backend', choices=['cpu', 'gpu'], default='cpu')
    parser.add_argument('--timeout', type=float, default=1800)
    parser.add_argument('--ffmpeg', default=g1.default_tool('ffmpeg'))
    parser.add_argument('--ffprobe', default=g1.default_tool('ffprobe'))
    args = parser.parse_args()
    check(args.ffmpeg and args.ffprobe and args.timeout > 0, 'ffmpeg, ffprobe and positive timeout required')
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    args.ffmpeg, args.ffprobe = str(Path(args.ffmpeg).resolve()), str(Path(args.ffprobe).resolve())
    if args.project:
        args.project = args.project.resolve()
        check(args.project.is_dir(), 'project directory required')
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = (args.dist / 'deployment.json').read_bytes()
    deployment = json.loads(manifest)
    summary = {'status': 'running', 'stage': 'prepare', 'exports': [],
               'deployment': {k: deployment.get(k) for k in ('rid', 'version', 'sourceCommit', 'sourceDirty', 'selfContained')},
               'deploymentManifestSha256': sha(manifest), 'probeSha256': file_hash(Path(__file__))}
    session = None
    original = None
    try:
        with tempfile.TemporaryDirectory(prefix='mako-audio-http-private-') as temporary:
            profile = Path(temporary) / 'profile'
            (profile / 'projects').mkdir(parents=True)
            if args.project:
                stage('copy-real-project')
                original = snapshot(args.project)
                target = profile / 'projects' / 'acceptance-copy'
                target.mkdir()
                shutil.copy2(args.project / 'project.json', target / 'project.json')
                if (args.project / 'data').exists():
                    shutil.copytree(args.project / 'data', target / 'data', symlinks=False)
                check(snapshot(target) == original, 'copied project exact content')
            with Session(args, profile) as session:
                fixtures = [
                    ('short-wav-mp4', 'wav', 'pcm_s16le', 1, 'opaqueMp4', 24, 1),
                    ('short-wav-mov', 'wav', 'pcm_s16le', 1, 'transparentProRes4444', 24, 1),
                    ('short-mp3', 'mp3', 'libmp3lame', 1, 'opaqueMp4', 24, 1),
                    ('long-flac', 'flac', 'flac', 4, 'opaqueMp4', 24, 1),
                    ('rational-short-wav', 'wav', 'pcm_s16le', 1, 'opaqueMp4', 30000, 1001),
                    ('no-audio-true', None, None, 0, 'opaqueMp4', 24, 1),
                    ('no-audio-false', None, None, 0, 'opaqueMp4', 24, 1),
                ]
                for name, ext, codec, seconds, fmt, num, den in fixtures:
                    summary['stage'] = name
                    stage(name)
                    duration = None
                    value = {'name': 'isolated ' + name, 'composition': {'canvasWidth': 320, 'canvasHeight': 180,
                             'frameRateNumerator': num, 'frameRateDenominator': den, 'outputDurationUs': 3000000}}
                    if ext:
                        audio_path = profile / ('fixture.' + ext)
                        duration = make_audio(args, audio_path, seconds, codec)
                        value['audioAssetId'] = session.upload('fixture.' + ext, audio_path.read_bytes(), 'application/octet-stream')
                    project = session.request('POST', '/api/v1/project-sessions', status=201, value=value)
                    project_id, revision = project['projectSessionId'], project['projectRevision']
                    frames = math.floor(3 * num / den + .5)
                    summary['exports'].append(run_export(session, args, project_id, revision, name, fmt,
                        name != 'no-audio-false', bool(ext), frames, duration, seconds == 1, bool(ext)))
                    close(session, project_id, revision)
                if args.project:
                    summary['stage'] = 'real-project-open-export'
                    stage('real-project-open-export')
                    roots = session.request('GET', '/api/v1/file-roots')['rootGrantIds']
                    check('projects' in roots, 'project root granted')
                    handle = session.request('POST', '/api/v1/file-handles', status=201,
                        value={'rootGrantId': 'projects', 'relativePath': 'acceptance-copy', 'access': 'read'})
                    accepted = session.request('POST', '/api/v1/project-sessions/open-jobs', status=202,
                        value={'projectDirectoryHandleId': handle['handleId'], 'access': 'read'})
                    project = session.wait(accepted)['result']
                    document = json.loads((target / 'project.json').read_text())
                    composition = document['composition']
                    rate = composition['frameRate']
                    frames = max(1, math.floor(composition['outputDurationUs'] * rate['numerator'] / (1000000 * rate['denominator']) + .5))
                    has_audio = bool(project['audioAssetId'])
                    check(has_audio == bool(document.get('audioPath')), 'opened project audio matches saved project')
                    source_duration = None
                    if has_audio:
                        source = (target / document['audioPath']).resolve()
                        check(source.is_relative_to((target / 'data').resolve()) and source.is_file(), 'project audio is within copied data')
                        audio_metadata = document.get('media', {}).get('audio')
                        if audio_metadata:
                            check(source.stat().st_size == audio_metadata['byteLength'] and file_hash(source) == audio_metadata['hash'],
                                  'copied project audio identity')
                        source_duration = decoded_duration(args, source)
                    summary['exports'].append(run_export(session, args, project['projectSessionId'], project['projectRevision'],
                        'real-project', 'opaqueMp4', True, has_audio, frames, source_duration))
                    close(session, project['projectSessionId'], project['projectRevision'])
        summary.update(status='passed', stage='complete', profilesRemoved=True)
    except BaseException as error:
        summary.update(status='failed', failure={'type': type(error).__name__,
            'message': str(error) if isinstance(error, AssertionError) else 'probe exception; inspect stage and type',
            'trace': [{'file': Path(f.filename).name, 'line': f.lineno, 'function': f.name} for f in traceback.extract_tb(error.__traceback__)]})
    if args.project and original is not None:
        try:
            summary['originalProjectUnchanged'] = snapshot(args.project) == original
        except (OSError, AssertionError):
            summary['originalProjectUnchanged'] = False
        if not summary['originalProjectUnchanged']:
            summary.update(status='failed', failure={'type': 'AssertionError', 'message': 'original project content unchanged'})
    if session:
        summary['session'] = session.evidence
    raw = json.dumps(summary, ensure_ascii=False, indent=2)
    if session:
        for credential in session.credentials:
            check(credential not in raw, 'summary credential leak')
    (args.output / 'summary.json').write_text(raw + '\n', encoding='utf-8')
    print(json.dumps({'status': summary['status'], 'stage': summary['stage'], 'output': str(args.output)}, ensure_ascii=False), flush=True)
    return 0 if summary['status'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
