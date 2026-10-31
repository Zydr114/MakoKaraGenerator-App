#!/usr/bin/env python3
"""B4-G finite default waveform preparation; accepted transport/receipts/jobs reused."""
import argparse
import copy
import hashlib
import importlib.util
import json
import os
import pathlib
import shutil
import subprocess
import sys
import threading
import time

ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('accepted_adoption', ROOT / 'tools/backend-analysis-apply-http-probe/probe.py')
BASE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BASE)


class WaveformProbe(BASE.AdoptionProbe):
    EXTRA_EVIDENCE_SOURCES = ('tools/backend-safe-waveform-http-probe/probe.py', 'tools/backend-safe-waveform-http-probe/README.md')
    EVIDENCE_BOUNDARIES = ['Independent B4-G counts; original old0.70 HTTP reproduction separate.',
        'Six real container families create/save/close/open against original default waveform loader on identical encoded bytes.',
        'HTTP exposes duration/state only; full info/PCM/pyramid compatibility belongs to author regressions, not a new waveform HTTP route.',
        'Fresh Server-saved current-version malicious package with correct manifest hash/size; same-client owned sentinel only, no arbitrary/cross-client paths.',
        'Controlled one-open -re injection observes actual direct Server decoder cancellation; not a Worker group or production speed proof.',
        'Create disconnect cancellation/deadline/Worker preparation isolation are outside this batch.',
        'No GUI/screenshots/full suite/performance/long media/manual platform acceptance.']

    def __init__(self, output, server, worker, oracle):
        super().__init__(output, server, worker, oracle, oracle)
        self.direct, self.sentinel_reads = {}, []
        self.monitor_stop = threading.Event()
        self.marker = output / 'controlled-one-open'

    def start_server(self, name, controlled=False):
        tools = self.output / 'owned-path-tools'; tools.mkdir()
        for tool in ('ffmpeg', 'ffprobe'):
            wrapper = tools / tool
            wrapper.write_text('#!/usr/bin/python3\nimport json,os,sys\nargs=sys.argv[1:]\nname=' + repr(tool)
                + '\ncontrolled=name=="ffmpeg" and "-i" in args and os.path.exists(' + repr(str(self.marker)) + ')\n'
                + 'record=dict(pid=os.getpid(),parent=os.getppid(),startTicks=open("/proc/self/stat").read().rsplit(")",1)[1].split()[19],tool=name,originalArguments=args,injectedReadRate=controlled)\n'
                + 'with open(' + repr(str(self.output / 'native-invocations.jsonl')) + ',"a") as target: target.write(json.dumps(record)+"\\n")\n'
                + 'os.execv("/usr/bin/"+name,[name]+(["-re"] if controlled else [])+args)\n')
            wrapper.chmod(0o700)
        self.environment['PATH'] = str(tools) + os.pathsep + self.environment['PATH']
        super().start_server(name)
        self.monitor = threading.Thread(target=self.watch, daemon=True); self.monitor.start()

    def watch(self):
        while not self.monitor_stop.is_set():
            for path in pathlib.Path('/proc').iterdir():
                if not path.name.isdigit(): continue
                identity = self.identity(int(path.name))
                if not identity or identity['parent'] != self.process.pid: continue
                try: args = (path / 'cmdline').read_bytes().split(b'\0')
                except (FileNotFoundError, ProcessLookupError): continue
                if not args or args[0] not in (b'ffmpeg', b'ffprobe', b'/usr/bin/ffmpeg', b'/usr/bin/ffprobe'): continue
                self.direct[(identity['pid'], identity['startTicks'])] = dict(identity, argv=[arg.decode() for arg in args if arg])
                try:
                    for fd in (path / 'fd').iterdir():
                        try: target = os.readlink(fd)
                        except OSError: continue
                        if target.endswith('/owned-sentinel.wav'):
                            self.sentinel_reads.append({'identity': identity, 'openedPath': target})
                except FileNotFoundError: pass
            self.monitor_stop.wait(.003)

    def stop_server(self, name):
        self.monitor_stop.set()
        if hasattr(self, 'monitor'): self.monitor.join(timeout=3)
        super().stop_server(name)
        invocations = [json.loads(line) for line in (self.output / 'native-invocations.jsonl').read_text().splitlines()]
        for value in invocations:
            if value['parent'] == self.process.pid:
                self.direct.setdefault((value['pid'], value['startTicks']), value)
        identities = []
        for old in self.direct.values():
            now = self.identity(old['pid']); gone = not now or now['startTicks'] != old['startTicks']
            self.check(gone, 'direct native: exact owned PID/startTicks gone'); identities.append(dict(old, gone=gone))
        (self.output / 'direct-native-evidence.json').write_text(json.dumps({'identities': identities,
            'sentinelReads': self.sentinel_reads, 'serverPid': self.process.pid,
            'ownership': 'Direct Server native identities; separate from inherited observed Worker private-group members.'}, indent=2) + '\n')

    def upload(self, name, path):
        mime = {'.wav': 'audio/wav', '.mp3': 'audio/mpeg', '.flac': 'audio/flac', '.m4a': 'audio/mp4', '.aac': 'audio/aac', '.ogg': 'audio/ogg'}[path.suffix]
        return self.request(name, 'POST', '/api/v1/assets', raw=path.read_bytes(), content_type=mime,
            headers={'X-Mako-Asset-Name': path.name}, status=201)

    def sessions(self, name):
        return self.request(name, 'GET', '/api/v1/clients/' + self.client + '/state')['sessions']

    def oracle_waveform(self, name, path):
        prefix = self.output / (name + '-original')
        result = subprocess.run(self.oracle + [str(path), str(prefix), 'waveform'], capture_output=True, timeout=30)
        (self.output / (name + '-original.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': original default waveform loader exits0')
        expected = json.loads(pathlib.Path(str(prefix) + '.waveform.json').read_text())
        self.check(expected['sourceHash'] == hashlib.sha256(path.read_bytes()).hexdigest(), name + ': oracle loads complete identical encoded bytes')
        return expected

    def family(self, family):
        original = self.fixture(family + '-source', 44100 if family in ('mp3', 'm4a', 'ogg') else 48000, 2)
        if family == 'wav': return original
        target = self.output / ('healthy.' + family)
        codec = {'mp3': 'libmp3lame', 'flac': 'flac', 'm4a': 'aac', 'aac': 'aac', 'ogg': 'libvorbis'}[family]
        result = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(original), '-c:a', codec, str(target)], capture_output=True, timeout=20)
        (self.output / (family + '-encoding.log')).write_bytes(result.stdout + result.stderr)
        self.check(result.returncode == 0, family + ': real own encoding exits0'); return target

    def normal(self):
        self.host = self.request('health', 'GET', '/health/live', client=None, host=None)['hostInstanceId']; self.register('waveform-owner')
        self.lyrics = self.lyrics_upload('waveform-lyrics')
        for family in ('wav', 'mp3', 'flac', 'm4a', 'aac', 'ogg'):
            path = self.family(family); expected = self.oracle_waveform(family, path); asset = self.upload(family + '-upload', path)
            source = self.create(family + '-create', asset)
            self.check(source['audioDurationUs'] == expected['durationUs'] and source['projectRevision'] == '0' and source['isDirty'], family + ': create actual source-rate duration and revision0')
            handle, _ = self.save(family + '-save', source, family + '-package'); self.close(family + '-close', source)
            opened = self.open(family + '-open', handle); state = self.state(family + '-opened', opened)
            self.check(opened['audioDurationUs'] == expected['durationUs'] and not opened['isDirty']
                and state['history']['undoCount'] == state['history']['redoCount'] == 0, family + ': opened original duration cleanly with fresh history')
            copied = list((self.output / 'resources' / self.client).glob(opened['audioAssetId'] + '.*'))
            self.check(len(copied) == 1 and copied[0].read_bytes() == path.read_bytes(), family + ': current imported asset is identical complete encoded bytes')
            self.comparisons.append({'family': family, 'oracle': expected, 'createDurationUs': source['audioDurationUs'], 'openDurationUs': opened['audioDurationUs']})
            self.close(family + '-opened-close', opened)
            if family == 'wav': self.wave_handle, self.wave_package = handle, self.output / 'projects/wav-package'
        self.negative(); self.cancel_open()

    def negative(self):
        sentinel = self.output / 'resources' / self.client / 'owned-sentinel.wav'; shutil.copyfile(self.output / 'wav-source.wav', sentinel)
        claimed = self.output / 'claimed.wav'; claimed.write_bytes(b"ffconcat version 1.0\nfile 'owned-sentinel.wav'\n")
        asset = self.upload('concat-upload', claimed); before = self.sessions('before-rejected-create')
        self.mutate('concat-create', '/api/v1/project-sessions', {'name': 'concat-create', 'audioAssetId': asset['assetId'],
            'lyricsAssetId': self.lyrics['assetId']}, status=422, code='audioPreparationFailed')
        self.check(self.sessions('after-rejected-create') == before, 'rejected create: no published partial session')
        fixture = self.output / 'projects/current-version-malicious'; shutil.copytree(self.wave_package, fixture)
        manifest = json.loads((fixture / 'project.json').read_text()); digest = hashlib.sha256(claimed.read_bytes()).hexdigest()
        relative = 'data/media/' + digest + '.media'; (fixture / relative).write_bytes(claimed.read_bytes())
        manifest['audioPath'] = relative; manifest['media']['audio'] = {'hash': digest, 'byteLength': claimed.stat().st_size, 'originalName': 'claimed.wav'}
        (fixture / 'project.json').write_text(json.dumps(manifest)); handle = self.mutate('malicious-handle', '/api/v1/file-handles',
            {'rootGrantId': 'adoption-probe-root', 'relativePath': 'current-version-malicious'}, status=201)
        baseline = {str(value.relative_to(self.output / 'state')) for value in (self.output / 'state').glob('sessions/*/loaded-input')}
        for suffix in ('present', 'deleted'):
            if suffix == 'deleted': sentinel.unlink()
            terminal = self.wait_job('malicious-open-' + suffix, self.mutate('malicious-open-' + suffix, '/api/v1/project-sessions/open-jobs',
                {'projectDirectoryHandleId': handle['handleId']}, status=202), 'failed')
            self.check(terminal['error']['code'] == 'projectLoadFailed' and 'result' not in terminal, 'rejected open: stable failure without half session')
            self.check(self.sessions('after-rejected-open-' + suffix) == before, 'rejected open: no session published; failed jobs/events allowed')
            after = {str(value.relative_to(self.output / 'state')) for value in (self.output / 'state').glob('sessions/*/loaded-input')}
            self.check(after == baseline, 'rejected open: owned candidate pinned closure released')
        self.check(not self.sentinel_reads, 'new native loader never opened owned referenced sentinel')
        self.adoptions.append({'negativePackageApplicationVersion': manifest['applicationVersion'], 'schemaVersion': manifest['schemaVersion'],
            'manifestBytesSha256': digest, 'noSentinelRead': True, 'correctMediaHashAndSize': True})

    def cancel_open(self):
        before = self.sessions('before-cancel-open'); self.marker.write_text('one controlled own open')
        try:
            accepted = self.mutate('controlled-open', '/api/v1/project-sessions/open-jobs', {'projectDirectoryHandleId': self.wave_handle['handleId']}, status=202)
            end = time.monotonic() + 8; observed = None
            while time.monotonic() < end:
                for value in list(self.direct.values()):
                    argv = value.get('argv', [])
                    if '-re' in argv and '-i' in argv and (now := self.identity(value['pid'])) and now['startTicks'] == value['startTicks']:
                        observed = copy.deepcopy(value); break
                if observed: break
                time.sleep(.005)
            self.check(observed is not None, 'cancel: actual directly owned controlled FFmpeg decoder observed')
            self.check('-protocol_whitelist' in observed['argv'] and observed['argv'][observed['argv'].index('-f') + 1] == 'wav', 'cancel: actual strong WAV/file,pipe policy')
            self.cancel('controlled-open', accepted); terminal = self.wait_job('controlled-open', accepted, 'cancelled')
            now = self.identity(observed['pid']); self.check(not now or now['startTicks'] != observed['startTicks'], 'cancel: native PID/startTicks gone before job terminal')
            self.check('result' not in terminal and self.sessions('after-cancel-open') == before, 'cancel: no partial session/history publication')
            (self.output / 'cancel-direct-native.json').write_text(json.dumps(observed, indent=2) + '\n')
        finally:
            self.marker.unlink(missing_ok=True)
        reopened = self.open('retry-cancelled-open', self.wave_handle)
        self.check(not reopened['isDirty'] and reopened['projectRevision'] == '0', 'same-handle retry: directory lease/native preparation restored')
        self.close('retry-open-close', reopened)

    def restart(self): pass


if __name__ == '__main__':
    parser = argparse.ArgumentParser(); parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--server', nargs='+', required=True); parser.add_argument('--worker', required=True); parser.add_argument('--oracle', nargs='+', required=True)
    args = parser.parse_args(); WaveformProbe(args.output.resolve(), args.server, args.worker, args.oracle).run()
