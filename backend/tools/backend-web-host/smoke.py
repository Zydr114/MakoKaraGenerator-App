#!/usr/bin/env python3
"""Real HTTP smoke for the reusable Web backend launcher; credentials remain in memory."""
import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import queue
import signal
import subprocess
import sys
import threading
import time
import wave

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('existing_http_probe', HERE.parent / 'backend-http-probe' / 'probe.py')
existing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(existing)


class Smoke(existing.Probe):
    def __init__(self, output, dist):
        super().__init__(output, [])
        self.dist = dist
        self.launcher = None
        self.observed = {}
        self.monitor_stop = threading.Event()
        self.secrets = []
        self.runs = []
        self.current_ready = None
        self.monitor = None

    def observe(self, pid):
        try:
            stat = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
            self.observed[(pid, stat[19])] = True
            children = set()
            for task in Path(f'/proc/{pid}/task').iterdir():
                try:
                    children.update((task / 'children').read_text().split())
                except OSError:
                    pass
            for child in children:
                self.observe(int(child))
        except (OSError, IndexError):
            pass

    def start(self, name):
        self.current_ready = None
        self.monitor = None
        self.launcher = subprocess.Popen([sys.executable, str(HERE / 'host.py'), 'run', '--dist', str(self.dist),
            '--profile', str(self.output / 'profile'), '--origin', 'http://localhost:5173',
            '--origin', 'http://127.0.0.1:5173'], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        lines = queue.Queue()
        def read():
            for line in self.launcher.stdout:
                lines.put(line)
            lines.put(None)
        threading.Thread(target=read, daemon=True).start()
        deadline = time.monotonic() + 90
        logged = []
        while time.monotonic() < deadline:
            try:
                line = lines.get(timeout=0.2)
            except queue.Empty:
                continue
            if line is None:
                break
            logged.append(line)
            if line.startswith('WEB_BACKEND_READY '):
                ready = json.loads(line.split(' ', 1)[1])
                credential = Path(ready['credentialFile'])
                self.check(credential.stat().st_mode & 0o777 == 0o600, name + ': credential0600')
                self.check(credential.parent.stat().st_mode & 0o777 == 0o700, name + ': run0700')
                boot = json.loads(credential.read_text())
                self.bootstrap = boot['bootstrapToken']
                self.secrets.append(self.bootstrap)
                self.host, self.base_url = ready['hostInstanceId'], ready['addresses'][0]
                self.client = None
                self.check(self.base_url.startswith('http://127.0.0.1:'), name + ': random loopback')
                self.current_ready = ready
                self.runs.append({'ready': ready, 'launcherPid': self.launcher.pid})
                def monitor():
                    while not self.monitor_stop.wait(.02):
                        self.observe(ready['pid'])
                self.monitor_stop.clear()
                self.monitor = threading.Thread(target=monitor, daemon=True)
                self.monitor.start()
                existing.save_json(self.output / (name + '-launcher.json'), {'stdout': logged, **ready})
                return
        raise AssertionError(name + ': launcher failed to become ready: ' + ''.join(logged))

    def stop(self, name):
        if self.launcher is None:
            return
        if self.launcher.poll() is None:
            self.launcher.send_signal(signal.SIGTERM)
        code = self.launcher.wait(timeout=90)
        self.monitor_stop.set()
        if self.monitor is not None:
            self.monitor.join()
        self.launcher = None
        if self.current_ready is not None:
            self.check(code == 0, name + ': launcher/server SIGTERM exit0')
            self.check(not Path(self.current_ready['credentialFile']).exists(), name + ': owned bootstrap removed')
        remaining = []
        for (pid, identity) in self.observed:
            try:
                if Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()[19] == identity:
                    remaining.append(pid)
            except OSError:
                pass
        self.check(not remaining, name + ': all observed process identities gone')
        existing.save_json(self.output / (name + '-shutdown.json'), {'exitCode': code,
            'observedIdentities': [{'pid': p, 'startTicks': t} for p, t in self.observed], 'remaining': remaining})

    def connect(self, name):
        self.request(name + '-health', 'GET', '/health/live', client=None, host=None)
        registration = self.register(name + '-register')
        self.secrets.extend((registration['accessToken'], registration['resumeToken']))
        self.request(name + '-heartbeat', 'POST', '/api/v1/clients/' + self.client + '/heartbeat')
        resumed = self.register(name + '-resume', resume=registration)
        self.secrets.extend((resumed['accessToken'], resumed['resumeToken']))
        self.check(resumed['clientId'] == registration['clientId'], name + ': resume retains client')
        cap = self.request(name + '-capabilities', 'GET', '/api/v1/capabilities', headers={'Origin': 'http://localhost:5173'})
        self.check(cap['audioPreparation']['enabled'] is True, name + ': Worker audio preparation enabled')
        self.check(cap['maxAssetBytes'] is None, name + ': default asset size has no configured limit')
        self.check(cap['clientTokenLifetimeSeconds'] == 120 and cap['clientLeaseLifetimeSeconds'] == 300, name + ': production default120/300 TTL')
        runtime = self.request(name + '-runtime', 'GET', '/api/v1/runtime-info')
        self.check(runtime['enabled'] is True, name + ': Worker preview configured')
        self.request(name + '-state', 'GET', '/api/v1/clients/' + self.client + '/state')

    def scan_credentials(self):
        """Check persisted evidence using the credentials retained only in memory."""
        scan_count = 0
        for file in self.output.rglob('*'):
            if not file.is_file(): continue
            data = file.read_bytes()
            self.check(not any(secret.encode() in data for secret in self.secrets), 'no credentials in ' + str(file.relative_to(self.output)))
            scan_count += 1
        return scan_count

    def preview(self, project, revision):
        value = self.request('preview-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': project, 'sourceRevision': revision},
            'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview',
            'metadata': self.metadata('preview-create')})
        path = '/api/v1/preview-sessions/' + value['previewId']
        self.request('frame-request', 'POST', path + '/frame-requests', status=202, value={
            'expectedSourceRevision': revision, 'previewGeneration': value['previewGeneration'],
            'requestSeq': '1', 'timeUs': '500000', 'intent': 'playback', 'metadata': self.metadata('frame-request')})
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            state = self.request('frame-state', 'GET', path)
            if state['session']['state'] == 'ready':
                break
            self.check(state['session']['state'] != 'failed', 'preview did not fail')
            time.sleep(.1)
        else:
            raise AssertionError('preview never ready')
        frame = state['session']['latestFrame']
        self.check(frame['sourceRevision'] == revision and frame['requestSeq'] == '1', 'PNG source revision and sequence')
        headers, png = self.binary('frame-content', frame['content']['contentUrl'])
        self.check(hashlib.sha256(png).hexdigest() == frame['content']['sha256'], 'PNG full SHA')
        self.check(len(png) == frame['content']['sizeBytes'], 'PNG size')
        self.check(png[:8] == b'\x89PNG\r\n\x1a\n', 'PNG signature')
        self.check(int.from_bytes(png[16:20], 'big') == 320 and int.from_bytes(png[20:24], 'big') == 180, 'PNG dimensions')
        decoded = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', 'pipe:0', '-f', 'rawvideo',
                                  '-pix_fmt', 'rgba', 'pipe:1'], input=png, capture_output=True, check=True).stdout
        self.check(len(decoded) == 320 * 180 * 4, 'PNG fully decoded RGBA')
        self.check(len(set(decoded[i:i+3] for i in range(0, len(decoded), 4))) > 1, 'PNG contains visible lyric content')
        self.check(set(decoded[3::4]) == {255}, 'opaque PNG alpha')
        existing.save_json(self.output / 'png-decode.json', {'width': 320, 'height': 180,
            'pngSha256': hashlib.sha256(png).hexdigest(), 'rgbaSha256': hashlib.sha256(decoded).hexdigest(),
            'scope': 'full decoding, content presence and transport identity; no independent Runtime pixel oracle'})
        self.request('preview-close', 'POST', path + '/close', value={
            'expectedPreviewGeneration': value['previewGeneration'], 'expectedSourceRevision': revision,
            'metadata': self.metadata('preview-close')})

    def run(self):
        self.start('first')
        self.connect('first')
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000)
            wav.writeframes(b'\x00\x00' * 16000)
        audio = self.request('audio-upload', 'POST', '/api/v1/assets?fileName=w0.wav', raw=buf.getvalue(),
            content_type='audio/wav', headers={
                'X-Mako-Request-Id': 'audio-upload', 'X-Mako-Valid-Until-Utc': self.metadata('audio')['validUntilUtc']}, status=201)
        lyrics = self.request('lyrics-upload', 'POST', '/api/v1/assets?fileName=w0.lrc', raw=b'[00:00.00]Web backend ready\n[00:01.00]Second line\n',
            content_type='text/plain', headers={
                'X-Mako-Request-Id': 'lyrics-upload', 'X-Mako-Valid-Until-Utc': self.metadata('lyrics')['validUntilUtc']}, status=201)
        created = self.request('create', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': 'W0 backend smoke', 'audioAssetId': audio['assetId'], 'lyricsAssetId': lyrics['assetId'],
            'composition': {'canvasWidth': 320, 'canvasHeight': 180, 'frameRateNumerator': 30,
                'frameRateDenominator': 1, 'backgroundRed': 0, 'backgroundGreen': 0, 'backgroundBlue': 0,
                'backgroundAlpha': 255, 'outputDurationUs': 2000000}})
        project = created['projectSessionId']; path = '/api/v1/project-sessions/' + project
        original = self.request('lyrics-original', 'GET', path + '/lyrics/lines?branchId=primary')
        line = original['lines'][0]
        timing = {'type': 'lyrics.setTimeRange', 'target': {'branchId': 'primary', 'lineId': line['id']},
                  'startUs': '100000', 'endUs': '900000'}
        for revision, cmd in ((0, timing), (1, {'type': 'history.undo'}), (2, {'type': 'history.redo'})):
            result = self.request('command-' + str(revision), 'POST', path + '/commands',
                value=self.command(revision, 'command-' + str(revision), cmd))
            self.check(result['projectRevision'] == str(revision + 1), 'command increments revision')
            now = self.request('lyrics-' + str(revision), 'GET', path + '/lyrics/lines?branchId=primary')
            self.check(now['lines'][0]['time'] == (line['time'] if revision == 1 else {'startUs': '100000', 'endUs': '900000'}), 'undo/redo exact time projection')
        self.preview(project, '3')
        handle = self.request('save-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': 'w0-smoke'})
        saved = self.job('save', self.request('save', 'POST', path + '/save-jobs', status=202,
            value={'expectedRevision': '3', 'targetHandleId': handle['handleId']}))
        self.check(saved['savedProjectRevision'] == '3', 'save exact revision')
        self.job('close', self.request('close', 'POST', path + '/close', status=202,
            value={'expectedRevision': '3', 'policy': 'requireClean', 'activeJobs': 'wait'}))
        reopened = self.job('reopen', self.request('reopen', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId']}))['result']
        restored = self.request('reopen-lyrics', 'GET', '/api/v1/project-sessions/' + reopened['projectSessionId'] + '/lyrics/lines?branchId=primary')
        self.check(restored['lines'] == now['lines'], 'reopen preserves lyric identities/text/times')
        old = self.registration.copy(); old_host = self.host
        self.stop('first')
        self.start('restart')
        self.check(self.host != old_host, 'restart changes host identity')
        self.request('old-auth-first401', 'GET', '/api/v1/capabilities', client=old['clientId'], host=old_host,
                     status=401, code='unauthorizedClient')
        self.connect('restart')
        handle = self.request('restart-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': 'w0-smoke'})
        opened = self.job('restart-open', self.request('restart-open', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId']}))['result']
        after = self.request('restart-lyrics', 'GET', '/api/v1/project-sessions/' + opened['projectSessionId'] + '/lyrics/lines?branchId=primary')
        self.check(after['lines'] == restored['lines'], 'restart saved project unchanged')
        self.check(opened['hasPath'] and not opened['isDirty'], 'restart clean saved project')
        self.request('restart-recovery-ledger', 'GET', '/api/v1/recovery-jobs')
        self.stop('restart')
        scan_count = self.scan_credentials()
        existing.save_json(self.output / 'summary.json', {'status': 'passed', 'httpRequests': self.request_count,
            'assertions': len(self.assertions), 'credentialScannedFiles': scan_count, 'runs': self.runs,
            'assertionLabels': self.assertions, 'scope': 'backend W0 prerequisites, no browser W0 acceptance; production default TTL and 16MiB retained'})
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    smoke = Smoke(args.output, args.dist.resolve())
    try:
        smoke.run()
    finally:
        if smoke.launcher is not None:
            smoke.stop('failure-cleanup')


if __name__ == '__main__':
    main()
