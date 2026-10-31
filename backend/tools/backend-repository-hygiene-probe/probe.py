#!/usr/bin/env python3
"""Finite audio analysis/export, diagnostics and two-library save/reload HTTP acceptance."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import time
import traceback

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[2]


def load(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


HOST = load('hygiene_host', 'tools/backend-web-host/host.py')
SMOKE = load('hygiene_smoke', 'tools/backend-web-host/smoke.py')
ANALYSIS = load('hygiene_analysis', 'tools/backend-analysis-http-probe/probe.py')
LIBRARIES = load('hygiene_libraries', 'tools/backend-repository-hygiene-probe/library_workflow.py')
SAVE = SMOKE.existing.save_json
FIXTURES = ROOT / 'tests/MakoKaraGenerator.Backend.Worker.Tests/Fixtures/AudioPreparationBaseline'
CASES = (('m4aaac', 'm4a', 'audio/mp4', 'mov-audio-self-contained'),
         ('m4aalac', 'm4a', 'audio/mp4', 'mov-audio-self-contained'),
         ('oggvorbis', 'ogg', 'audio/ogg', 'ogg-audio-self-contained'),
         ('oggopus', 'ogg', 'audio/ogg', 'ogg-audio-self-contained'))


class HygieneProbe(SMOKE.Smoke):
    def __init__(self, output, dist, oracle):
        super().__init__(output, dist)
        self.oracle = [str(oracle)]
        self.oracle_path = oracle
        self.cases = []
        self.terminals = []
        self.libraries = None
        self.last_heartbeat = 0

    def request(self, name, method, path, **kwargs):
        heartbeat = '/api/v1/clients/' + str(self.client) + '/heartbeat'
        if self.client is not None and path not in ('/api/v1/clients', heartbeat) \
                and time.monotonic() - self.last_heartbeat >= 30:
            super().request(name + '-keepalive', 'POST', heartbeat)
            self.last_heartbeat = time.monotonic()
        result = super().request(name, method, path, **kwargs)
        if path == heartbeat:
            self.last_heartbeat = time.monotonic()
        return result

    def tool_inputs(self):
        names = ['tools/backend-repository-hygiene-probe/probe.py',
                 'tools/backend-repository-hygiene-probe/library_workflow.py',
                 'tools/backend-repository-hygiene-probe/README.md',
                 'tools/backend-web-host/host.py', 'tools/backend-web-host/smoke.py',
                 'tools/backend-http-probe/probe.py', 'tools/backend-analysis-http-probe/probe.py',
                 'tools/backend-analysis-http-probe/RuntimeOracle/Program.cs',
                 'tools/backend-analysis-http-probe/RuntimeOracle/RuntimeOracle.csproj']
        names += [str((FIXTURES / (name + '.input')).relative_to(ROOT)) for name, *_ in CASES]
        names += LIBRARIES.sample_inputs(ROOT)
        return {name: HOST.digest(ROOT / name) for name in names}

    def finish_job(self, name, accepted):
        terminal = self.job(name, accepted)
        self.check(not terminal['canCancel'], name + ': authoritative terminal is drained and non-cancellable')
        self.terminals.append(terminal)
        return terminal

    def post(self, name, route, value, status=202, code=None):
        return self.request(name, 'POST', route,
                            value={**value, 'metadata': self.metadata(name)}, status=status, code=code)

    def native(self, name, command):
        result = subprocess.run(command, capture_output=True, timeout=30)
        (self.output / (name + '.stderr.log')).write_bytes(result.stderr)
        self.check(result.returncode == 0, name + ': independent native command exit0')
        return result.stdout

    def export(self, name, source, profile, source_hash):
        route = '/api/v1/project-sessions/' + source['projectSessionId']
        terminal = self.finish_job(name + '-export', self.post(name + '-export', route + '/export-jobs',
            {'expectedRevision': source['projectRevision'], 'format': 'opaqueMp4',
             'includeAudio': True, 'renderBackend': 'cpu'}))
        audio = terminal['exportInput']['audio']
        self.check(audio['audioProfile'] == profile and audio['sha256'] == source_hash
                   and terminal['exportInput']['executionProfile'] == 'static-export-compressed-audio-v1',
                   name + ': Host fixes actual compressed source profile and complete encoded identity')
        path = '/api/v1/jobs/' + terminal['jobId']
        headers, raw = self.binary(name + '-export-report', path + '/export-result')
        report = json.loads(raw)
        reference = terminal['exportResult']['report']
        self.check(headers['Content-Type'].split(';')[0] == 'application/json'
                   and len(raw) == reference['sizeBytes'] and hashlib.sha256(raw).hexdigest() == reference['sha256'],
                   name + ': complete export report has registered size and SHA256')
        self.check(report['strictlyVerified'] and report['isVerified'] and report['audioIncluded']
                   and report['audioCodec'] == 'aac'
                   and report['framesWritten'] == report['decodedFrames'] == report['expectedFrames'] == 3
                   and report['failedFrames'] == report['droppedFrames'] == 0,
                   name + ': actual Worker verifies three complete frames and requested audio')
        headers, media = self.binary(name + '-movie', path + '/export-content')
        artifact = terminal['exportResult']['artifact']
        self.check(headers['Content-Type'].split(';')[0] == artifact['contentType'] == 'video/mp4'
                   and len(media) == artifact['sizeBytes'] == report['mediaSize']
                   and hashlib.sha256(media).hexdigest() == artifact['sha256'] == report['sha256'],
                   name + ': complete authenticated movie download matches registered length and SHA256')
        movie = self.output / (name + '.mp4')
        movie.write_bytes(media)
        observed = json.loads(self.native(name + '-ffprobe', ['/usr/bin/ffprobe', '-v', 'error',
            '-count_frames', '-show_streams', '-of', 'json', str(movie)]))
        SAVE(self.output / (name + '-ffprobe.json'), observed)
        video = [stream for stream in observed['streams'] if stream['codec_type'] == 'video']
        tracks = [stream for stream in observed['streams'] if stream['codec_type'] == 'audio']
        self.check(len(video) == len(tracks) == 1 and int(video[0]['nb_read_frames']) == 3
                   and video[0]['width'] == 320 and video[0]['height'] == 180
                   and tracks[0]['codec_name'] == 'aac' and int(tracks[0]['nb_read_frames']) > 0,
                   name + ': independent ffprobe finds three video frames and decoded AAC track')
        video_bytes = self.native(name + '-decode-video', ['/usr/bin/ffmpeg', '-v', 'error', '-i', str(movie),
            '-map', '0:v:0', '-f', 'rawvideo', '-pix_fmt', 'bgra', 'pipe:1'])
        audio_bytes = self.native(name + '-decode-audio', ['/usr/bin/ffmpeg', '-v', 'error', '-i', str(movie),
            '-map', '0:a:0', '-c:a', 'pcm_s16le', '-f', 's16le', 'pipe:1'])
        self.check(len(video_bytes) == 3 * 320 * 180 * 4 and len(audio_bytes) > 0
                   and len(audio_bytes) % (int(tracks[0]['channels']) * 2) == 0,
                   name + ': all downloaded video and audio decode completely with full sample-frame alignment')
        SAVE(self.output / (name + '-decode.json'), {
            'videoBytes': len(video_bytes), 'videoSha256': hashlib.sha256(video_bytes).hexdigest(),
            'audioBytes': len(audio_bytes), 'audioSha256': hashlib.sha256(audio_bytes).hexdigest(),
            'scope': 'Full decode, stream presence and transport identity; no independent pixel or lossy audio quality comparison.'})
        return terminal, report

    def diagnostics_queries(self, name, kind, terminal):
        route = '/api/v1/jobs/' + terminal['jobId'] + '/' + kind + '-diagnostics'
        baseline = self.request(name + '-baseline', 'GET', route)
        self.check(baseline['diagnostics'] == [] and baseline.get('nextCursor') is None,
                   name + ': complete success report has empty diagnostics')
        for index, query in enumerate(('limit=1', 'limit=0001', 'future=one&future=two&limit=1')):
            page = self.request(name + '-allowed-' + str(index), 'GET', route + '?' + query)
            self.check(page == baseline, name + ': integer parsing and ignored unknown query policy unchanged')
        for index, (query, field, message) in enumerate((
                ('cursor=a&cursor=b&limit=bad', None, '分页参数只能出现一次。'),
                ('limit=1&limit=2', None, '分页参数只能出现一次。'),
                ('limit=%2B1', 'limit', '诊断页大小必须为整数。'),
                ('limit=%201', 'limit', '诊断页大小必须为整数。'),
                ('limit=2147483648', 'limit', '诊断页大小必须为整数。'))):
            error = self.request(name + '-invalid-' + str(index), 'GET', route + '?' + query,
                                 status=400, code='invalidRequest')
            self.check(error.get('field') == field and error['message'] == message,
                       name + ': original rejection ordering, message and field unchanged')

    def exercise(self):
        self.connect('hygiene')
        analysis_cap = self.request('analysis-capabilities', 'GET', '/api/v1/analysis-capabilities')
        export_cap = self.request('export-capabilities', 'GET', '/api/v1/export-capabilities')
        self.check(analysis_cap['enabled'] and export_cap['enabled']
                   and all(profile in analysis_cap['audioProfiles'] and profile in export_cap['audioProfiles']
                           for _, _, _, profile in CASES), 'actual Worker capabilities include all selected container profiles')
        lyrics = self.request('lyrics-upload', 'POST', '/api/v1/assets',
            raw=b'[00:00.00]<00:00.00>Repository<00:00.10>\n', content_type='text/plain',
            headers={'X-Mako-Asset-Name': 'hygiene.lrc'}, status=201)
        for index, (name, extension, mime, profile) in enumerate(CASES):
            source_file = FIXTURES / (name + '.input')
            encoded = source_file.read_bytes()
            source_hash = hashlib.sha256(encoded).hexdigest()
            uploaded = self.request(name + '-upload', 'POST', '/api/v1/assets', raw=encoded,
                content_type=mime, headers={'X-Mako-Asset-Name': name + '.' + extension}, status=201)
            source = self.post(name + '-create', '/api/v1/project-sessions', {
                'name': name, 'audioAssetId': uploaded['assetId'], 'lyricsAssetId': lyrics['assetId'],
                'importerId': 'lrc.enhanced', 'composition': {'canvasWidth': 320, 'canvasHeight': 180,
                    'frameRateNumerator': 30, 'frameRateDenominator': 1, 'outputDurationUs': 100000}}, status=201)
            self.check(source['audioDurationUs'] >= 100000, name + ': actual prepared audio covers the short export plan')
            route = '/api/v1/project-sessions/' + source['projectSessionId']
            terminal = self.finish_job(name + '-analysis', self.post(name + '-analysis', route + '/analysis-jobs',
                {'expectedRevision': source['projectRevision']}))
            grant = terminal['analysisInput']
            self.check(grant['sourceProfile'] == 'fixed-compressed-audio-analysis-v1'
                       and grant['audio']['audioProfile'] == profile and grant['audio']['sha256'] == source_hash
                       and 'sourceDurationUs' not in grant,
                       name + ': Host classifies actual bytes and omits unmeasured compressed duration')
            metadata, canonical = ANALYSIS.AnalysisProbe.oracle_run(self, name, source_file)
            ANALYSIS.AnalysisProbe.result(self, name + '-canonical', terminal, canonical, metadata)
            exported, report = self.export(name, source, profile, source_hash)
            self.cases.append({'name': name, 'profile': profile, 'sourceSha256': source_hash,
                'analysisJobId': terminal['jobId'], 'exportJobId': exported['jobId'],
                'analysis': metadata, 'exportFrames': report['expectedFrames'],
                'mediaSha256': report['sha256'], 'canonicalBytesEqual': True})
            if index == 0:
                validation = self.finish_job('diagnostics-validation', self.post('diagnostics-validation', route + '/validation-jobs',
                    {'expectedRevision': source['projectRevision'], 'framesPerOutput': 1, 'renderBackend': 'cpu'}))
                _, raw = self.binary('validation-report', '/api/v1/jobs/' + validation['jobId'] + '/validation-result')
                result = json.loads(raw); reference = validation['validationResult']['report']
                self.check(result['succeeded'] and result['opaqueFrames'] == result['transparentFrames'] == 1
                           and len(raw) == reference['sizeBytes'] and hashlib.sha256(raw).hexdigest() == reference['sha256'],
                           'validation: actual ordinary and transparent frame report has exact registered identity')
                self.diagnostics_queries('validation-query', 'validation', validation)
                self.diagnostics_queries('export-query', 'export', exported)

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False)
        failure = None; scanned = 0; frozen = None; cleanup_failures = []
        try:
            deployment = HOST.validate_dist(self.dist)
            self.check(deployment.get('sourceDirty') is False, 'deployment was published from clean production inputs')
            self.check(deployment['sourceInputs'] == HOST.source_inputs(), 'current production inputs match the published Server and Worker')
            frozen = {'production': deployment['sourceInputs'], 'tools': self.tool_inputs(),
                      'deploymentManifestSha256': HOST.digest(self.dist / 'deployment.json'),
                      'oracleFiles': HOST.inventory(self.oracle_path.parent)}
            SAVE(self.output / 'freeze-start.json', frozen)
            SAVE(self.output / 'deployment.json', deployment)
            self.start('matched-server')
            self.exercise()
            self.libraries = LIBRARIES.run(self, ROOT, HOST, SAVE)
        except Exception as error:
            failure = str(error)
            (self.output / 'failure.txt').write_text(traceback.format_exc())
        finally:
            actions = []
            if self.launcher is not None:
                actions.append(('shutdown', lambda: self.stop('final-shutdown')))
            if frozen is not None:
                def verify_freeze():
                    deployment = HOST.validate_dist(self.dist)
                    after = {'production': HOST.source_inputs(), 'tools': self.tool_inputs(),
                             'deploymentManifestSha256': HOST.digest(self.dist / 'deployment.json'),
                             'oracleFiles': HOST.inventory(self.oracle_path.parent)}
                    SAVE(self.output / 'freeze-end.json', after)
                    self.check(after == frozen and deployment['sourceInputs'] == after['production'],
                               'production, selected probe inputs, complete deployment and oracle bytes remained frozen')
                actions.append(('integrity', verify_freeze))
            for stage, action in actions:
                try:
                    action()
                except Exception as error:
                    cleanup_failures.append({'stage': stage, 'failure': str(error)})
                    (self.output / (stage + '-failure.txt')).write_text(traceback.format_exc())
            try:
                scanned = self.scan_credentials()
            except Exception as error:
                cleanup_failures.append({'stage': 'credentials', 'failure': str(error)})
                (self.output / 'credentials-failure.txt').write_text(traceback.format_exc())
            if cleanup_failures:
                failure = failure or 'Cleanup or integrity verification failed.'
            SAVE(self.output / 'manifest.json', {
                'succeeded': failure is None, 'failure': failure, 'httpRequests': self.request_count,
                'cleanupFailures': cleanup_failures,
                'assertionCount': len(self.assertions), 'credentialScannedFiles': scanned,
                'cases': self.cases, 'libraries': self.libraries, 'terminals': self.terminals, 'runs': self.runs,
                'observedIdentities': [{'pid': pid, 'startTicks': tick} for pid, tick in self.observed],
                'assertions': self.assertions,
                'gitHeadAtRun': subprocess.run(['git', 'rev-parse', 'HEAD'], cwd=ROOT, capture_output=True,
                                             text=True, check=True).stdout.strip(),
                'boundaries': ['Four fixed local container fixtures; current Linux machine and CPU backend only.',
                    'Analysis uses full original Analyzer canonical byte equality on identical encoded inputs.',
                    'Export checks complete transport identity, full video/audio decode and stream presence; no independent pixel or lossy audio quality oracle.',
                    'Negative Ogg version and short-header classification belong to unit red/green, not this success HTTP matrix.',
                    'Two libraries use one tracked complete sample package, exact published index identity, CAS rejection and same-profile restart reload; no script compilation or author draft recovery.',
                    'Observed process identities are sampled; final drain proves exit only for those observed.',
                    'No browser, cross-platform, performance, long-media, general recovery matrix or repository-wide runtime acceptance.']})
        if failure:
            raise SystemExit(failure)
        print(json.dumps({'succeeded': True, 'httpRequests': self.request_count,
                          'assertions': len(self.assertions), 'credentialScannedFiles': scanned}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, required=True)
    parser.add_argument('--analysis-oracle', type=Path, required=True,
                        help='Fresh matched standalone analysis RuntimeOracle executable')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    HygieneProbe(args.output.resolve(), args.dist.resolve(), args.analysis_oracle.resolve()).run()


if __name__ == '__main__':
    main()
