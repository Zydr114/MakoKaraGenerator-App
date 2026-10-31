#!/usr/bin/env python3
"""Finite public direct-package preview acceptance against a matched Server/Worker deployment."""
import argparse
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
from urllib.parse import urlencode

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('basic', ROOT / 'tools/backend-basic-business-http-probe/probe.py')
basic = importlib.util.module_from_spec(spec); spec.loader.exec_module(basic)
save_json = basic.save_json
import lifecycle
import uuid
from launcher import ConfiguredLauncher

PACKAGE_PROFILE = 'package-lyrics-scene-v1'


def compact(value):
    if isinstance(value, dict): return {k: compact(v) for k, v in value.items() if v is not None}
    if isinstance(value, list): return [compact(v) for v in value]
    return value


class PackagePreviewProbe(ConfiguredLauncher, basic.BasicBusinessProbe):
    def __init__(self, output, dist, oracle):
        super().__init__(output, dist)
        self.oracle = oracle
        self.projects = {}
        self.active_previews = {}
        self.comparisons = []
        self.runaway_results = []
        self.diagnostic_results = []
        self.last_auth_refresh = time.monotonic()

    def request(self, name, method, path, **kwargs):
        if self.client and path != '/api/v1/clients' and time.monotonic() - self.last_auth_refresh > 75:
            self.last_auth_refresh = time.monotonic()
            registration = self.register('periodic-resume-' + str(self.request_count), resume=self.registration)
            self.secrets.extend((registration['accessToken'], registration['resumeToken']))
        return super().request(name,method,path,**kwargs)

    def register(self, name, **kwargs):
        result=super().register(name,**kwargs)
        self.secrets.extend((result['accessToken'],result['resumeToken']))
        return result

    def oracle_run(self, name, arguments):
        command = ['flock', '-o', '/tmp/mkg-ttml-dotnet.lock', 'dotnet', str(self.oracle), *map(str, arguments)]
        result = subprocess.run(command, capture_output=True, text=True)
        (self.output / (name + '-oracle.log')).write_text(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': direct fixture/oracle exit0')

    def prepare(self):
        self.oracle_run('fixture-generation', ['generate', self.output / 'profile/projects'])
        fixtures=self.output/'profile/projects'
        self.fixture_inputs={str(p.relative_to(fixtures)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(fixtures.rglob('*')) if p.is_file()}
        save_json(self.output/'fixture-source-manifest.json',{'files':self.fixture_inputs,'scope':'Probe-owned ProjectStore.Save closure bytes before public open. Runtime caches and author markers are outside these original fixture inputs.'})

    def open_fixture(self, kind):
        handle = self.request(kind + '-directory', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': kind})['handleId']
        result = self.job(kind + '-open', self.request(kind + '-open', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle}))['result']
        source = {'kind': 'project', 'sourceId': result['projectSessionId'], 'sourceRevision': result['projectRevision']}
        self.projects[kind] = source
        return source

    def create_preview(self, label, source, transparent=False, width=320, height=180):
        result = self.request(label + '-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': source, 'size': {'width': width, 'height': height}, 'alphaMode': 'straight' if transparent else 'opaque', 'purpose': 'preview'})
        preview = result.get('session', result)
        self.check(preview['source'] == source, label + ': exact fixed source identity/revision')
        self.active_previews[preview['previewId']] = preview
        return preview

    def state(self, label, preview):
        return self.request(label, 'GET', '/api/v1/preview-sessions/' + preview['previewId'])

    def frame_request(self, label, preview, sequence, time_us, intent='playback'):
        result = self.request(label + '-request', 'POST', '/api/v1/preview-sessions/' + preview['previewId'] + '/frame-requests', status=202, value={
            'expectedSourceRevision': preview['source']['sourceRevision'], 'previewGeneration': preview['previewGeneration'],
            'requestSeq': str(sequence), 'timeUs': str(time_us), 'intent': intent})
        if result['previewGeneration'] != preview['previewGeneration']:
            preview = {**preview, 'previewGeneration': result['previewGeneration']}
            self.active_previews[preview['previewId']] = preview
        return preview

    def terminal(self, label, preview, sequence, expected='ready', timeout=50):
        deadline = time.monotonic() + timeout; count = 0
        while time.monotonic() < deadline:
            count += 1
            state = self.state(label + '-state-' + str(count), preview); current = state['session']
            if current['state'] == 'failed' or (current['state'] == 'ready' and current.get('latestFrame', {}).get('requestSeq') == str(sequence)):
                self.check(current['state'] == expected, label + ': terminal ' + expected + ', actual ' + current['state'])
                self.check(current['previewGeneration'] == preview['previewGeneration'], label + ': terminal exact generation')
                return state
            time.sleep(.1)
        raise AssertionError(label + ': preview terminal deadline')

    def diagnostics(self, label, preview):
        path = '/api/v1/preview-sessions/' + preview['previewId'] + '/diagnostics'
        cursor, values, pages, seen = None, [], [], set()
        while True:
            query = {'limit': 128}
            if cursor: query['cursor'] = cursor
            page = self.request(label + '-page-' + str(len(pages)), 'GET', path + '?' + urlencode(query))
            self.check(page['previewGeneration'] == preview['previewGeneration'] and page['sourceRevision'] == preview['source']['sourceRevision'], label + ': diagnostic page fixed generation/revision')
            self.check(len(page['diagnostics']) <= 128, label + ': fixed page bound')
            pages.append(page); values.extend(page['diagnostics'])
            if not page['hasMore']: break
            cursor = page['nextCursor']; self.check(cursor and cursor not in seen, label + ': finite unique next cursor'); seen.add(cursor)
        self.check(len(pages) <= 32, label + ': page chain bound')
        return values, pages

    def direct(self, label, kind, width, height, transparent, times, counterfactual='none'):
        prefix = self.output / (label + '-direct')
        self.oracle_run(label, ['render', self.output / 'profile/projects' / kind, prefix, width, height,
            str(transparent).lower(), ','.join(map(str, times)), counterfactual])
        return [prefix.parent / (prefix.name + '-' + str(index)) for index in range(len(times))]

    def compare_frame(self, label, state, expected, kind, contribution=None):
        frame = state['session']['latestFrame']; prefix = expected
        oracle = json.loads(prefix.with_suffix('.json').read_text())
        self.check(not oracle['hasError'], label + ': direct oracle has no domain error')
        self.check(oracle['engineIdentity'] == 'MakoKaraGenerator:' + json.loads((self.dist/'deployment.json').read_text())['version'], label + ': exact oracle engine version')
        _, png, _ = self.fetch(label + '-content', frame['content']['contentUrl'])
        self.check(hashlib.sha256(png).hexdigest() == frame['content']['sha256'], label + ': immutable PNG SHA')
        self.check(png == prefix.with_suffix('.png').read_bytes(), label + ': complete direct Runtime PNG byteexact')
        decoded = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', 'pipe:0', '-f', 'rawvideo', '-pix_fmt', 'bgra', 'pipe:1'], input=png, capture_output=True, check=True).stdout
        # PNG is straight alpha; compare its complete decoded bytes to the independently encoded oracle PNG.
        direct_decoded = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', str(prefix.with_suffix('.png')), '-f', 'rawvideo', '-pix_fmt', 'bgra', 'pipe:1'], capture_output=True, check=True).stdout
        self.check(decoded == direct_decoded, label + ': entire decoded PNG pixels match direct oracle')
        if contribution is not None:
            self.check(prefix.with_suffix('.bgra').read_bytes() != contribution.with_suffix('.bgra').read_bytes(), label + ': actual package contributes complete BGRA against disabled instance/material')
        self.comparisons.append({'name': label, 'kind': kind, 'timeUs': frame['timeUs'], 'size': [frame['width'], frame['height']],
            'alphaMode': frame['alphaMode'], 'pngSha256': frame['content']['sha256'], 'directSourceSha256': oracle['sourceSha256'], 'directBgraSha256': oracle['bgraSha256'], 'counterfactual': contribution is not None})
        return oracle

    def compare_diagnostics(self, label, actual, oracle, exact_runtime=False):
        source = [item for item in actual if item.get('originKind') == 'compileSource']
        image = [item for item in actual if item.get('originKind') == 'imageEffect']
        material = [item for item in actual if item.get('originKind') == 'material']
        self.check([compact(item['source']) for item in source] == compact(oracle['sourceDiagnostics']), label + ': complete ordered compilation source diagnostics')
        self.check([compact(item['imageEffect']) for item in image] == compact(oracle['imageEffects']), label + ': complete ordered image diagnostics')
        self.check([compact(item['material']) for item in material] == compact(oracle['materials']), label + ': complete ordered material diagnostics and owner union')
        for item in source + image + material:
            self.check('occurrence' not in item, label + ': no synthetic runtime occurrence for ' + item['originKind'])
        def verify_identity(records, side):
            epochs=set(); current=None; sequence=0
            for occurrence in records:
                parsed=uuid.UUID(occurrence['epoch'])
                self.check(parsed.int != 0,label+': '+side+' runtime UUID nonempty')
                if occurrence['epoch'] != current:
                    self.check(occurrence['epoch'] not in epochs,label+': '+side+' runtime epoch appears in one complete run')
                    current=occurrence['epoch'];epochs.add(current);sequence=0
                sequence+=1
                self.check(int(occurrence['sequence'])==sequence,label+': '+side+' runtime complete contiguous sequence from one')
        verify_identity([item['occurrence'] for item in actual if item.get('originKind')=='runtime'],'HTTP Worker')
        verify_identity(oracle['runtime'],'independent Runtime')
        runtime = []
        for item in actual:
            if item.get('originKind') != 'runtime': continue
            occurrence = item['occurrence']; self.check(bool(occurrence['epoch']) and int(occurrence['sequence']) > 0, label + ': actual runtime occurrence identity')
            runtime.append(compact({**{k:v for k,v in occurrence.items() if k not in ('epoch','sequence')}, 'code': item['code'], 'severity': item['severity'], 'message': item['message']}))
        expected = compact([{k:v for k,v in item.items() if k not in ('epoch','sequence')} for item in oracle['runtime']])
        if exact_runtime: self.check(runtime == expected, label + ': all direct runtime diagnostic fields/order (process-local epoch/sequence checked separately)')
        else:
            for item in expected: self.check(item in runtime, label + ': latest direct actual runtime occurrence fields')
        self.diagnostic_results.append({'name': label, 'source': len(source), 'image': len(image), 'material': len(material), 'runtime': len(runtime)})

    def close_preview(self, label, preview):
        result = self.request(label + '-close', 'POST', '/api/v1/preview-sessions/' + preview['previewId'] + '/close', value={
            'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': preview['source']['sourceRevision']})
        self.active_previews.pop(preview['previewId'], None)
        self.request(label + '-closed-read', 'GET', '/api/v1/preview-sessions/' + preview['previewId'], status=404, code='previewSessionNotFound')
        return result

    def healthy(self, kind, transparent):
        source = self.projects.get(kind) or self.open_fixture(kind)
        label = kind + ('-transparent' if transparent else '-opaque')
        preview = self.create_preview(label, source, transparent)
        times = [0, 100000, 300000, 500000]
        direct = self.direct(label, kind, 320, 180, transparent, times)
        without = None if kind == 'static' else self.direct(label + '-counterfactual', kind, 320, 180, transparent, times, kind)
        last = None
        for sequence, time_us in enumerate(times, 1):
            preview = self.frame_request(label + '-' + str(sequence), preview, sequence, time_us)
            state = self.terminal(label + '-' + str(sequence), preview, sequence)
            oracle = self.compare_frame(label + '-' + str(sequence), state, direct[sequence-1], kind, None if without is None else without[sequence-1])
            actual, _ = self.diagnostics(label + '-' + str(sequence), preview)
            self.compare_diagnostics(label + '-' + str(sequence), actual, oracle)
            last = state['session']['latestFrame']
        # Explicit backward seek activates a new generation and invalidates its predecessor.
        previous_generation = preview['previewGeneration']
        preview = self.frame_request(label + '-seek', preview, 5, 100000, 'seek')
        self.check(preview['previewGeneration'] != previous_generation, label + ': seek activates generation')
        state = self.terminal(label + '-seek', preview, 5)
        seek = self.direct(label + '-seek', kind, 320, 180, transparent, [100000])[0]
        self.compare_frame(label + '-seek', state, seek, kind)
        self.request(label + '-old-generation', 'POST', '/api/v1/preview-sessions/' + preview['previewId'] + '/frame-requests', status=409,
            code='previewGenerationConflict', value={'expectedSourceRevision': source['sourceRevision'], 'previewGeneration': previous_generation, 'requestSeq': '6', 'timeUs': '100000', 'intent': 'playback'})
        self.close_preview(label, preview)

    def diagnostic_fixture(self, kind):
        source = self.open_fixture(kind); label = kind
        preview = self.create_preview(label, source, True)
        direct = self.direct(label, kind, 320, 180, True, [100000])[0]
        expected = json.loads(direct.with_suffix('.json').read_text())
        preview = self.frame_request(label, preview, 1, 100000)
        state = self.terminal(label, preview, 1, 'failed' if expected['hasError'] else 'ready')
        if expected['hasError']: self.check(state['session'].get('latestFrame') is None, label + ': domain Error publishes no frame')
        else: self.compare_frame(label, state, direct, kind)
        actual, pages = self.diagnostics(label, preview)
        self.compare_diagnostics(label, actual, expected, exact_runtime=True)
        if kind == 'pages':
            self.check(len(actual) == 130 and len(pages) == 2, label + ': real complete cross-page diagnostics')
            self.request(label + '-limit-too-small', 'GET', '/api/v1/preview-sessions/' + preview['previewId'] + '/diagnostics?limit=1', status=400, code='diagnosticLimitExceeded')
        self.close_preview(label, preview)

    def run_variant(self):
        deployment=json.loads((self.dist/'deployment.json').read_text())
        variant=deployment['probeVariant']['kind']
        self.launcher_name='variant_host.py'
        self.prepare();self.start('variant')
        self.request('variant-health','GET','/health/live',client=None,host=None)
        registration=self.register('variant-register');self.secrets.extend((registration['accessToken'],registration['resumeToken']))
        resumed=self.register('variant-resume',resume=registration);self.secrets.extend((resumed['accessToken'],resumed['resumeToken']))
        self.check(resumed['clientId']==registration['clientId'],'variant authenticated resume retains owner')
        capability=self.request('variant-capabilities','GET','/api/v1/capabilities')
        save_json(self.output/'variant-actual-capabilities.json',capability)
        runtime=self.request('variant-runtime','GET','/api/v1/runtime-info')
        self.check(PACKAGE_PROFILE not in runtime['sourceProfiles'],'incompatible actual Worker does not advertise package')
        source=self.open_fixture('scale')
        response=self.request('variant-package-rejected','POST','/api/v1/preview-sessions',status=503,code='previewUnavailable',value={
            'source':source,'size':{'width':320,'height':180},'alphaMode':'opaque','purpose':'preview'})
        self.check(response['failureCode']=='WorkerPackageProfileMismatch','incompatible Worker stable package gate')
        if variant in ('missing-refs','historical-current-version'):
            source=self.open_fixture('static');preview=self.create_preview('variant-static',source)
            preview=self.frame_request('variant-static',preview,1,100000)
            actual=self.terminal('variant-static',preview,1)
            direct=self.direct('variant-static','static',320,180,False,[100000])[0]
            self.compare_frame('variant-static',actual,direct,'static');self.close_preview('variant-static',preview)

    def run(self, stages):
        deployment = json.loads((self.dist / 'deployment.json').read_text())
        self.check(deployment['version'] == '0.98.1' and not deployment['sourceDirty'], 'clean matched 0.98.1 publish')
        self.source_inputs = deployment['sourceInputs']
        self.check(all(hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==value for name,value in self.source_inputs.items()), 'current production inputs equal publish manifest')
        self.prepare(); self.start('package'); self.connect('package')
        runtime = self.request('public-package-runtime-info', 'GET', '/api/v1/runtime-info')
        self.check(PACKAGE_PROFILE in runtime['sourceProfiles'], 'real package capability advertised')
        if 'healthy' in stages:
            for kind in ('scale','graph','sprite','material','static'):
                for transparent in (False,True): self.healthy(kind, transparent)
        if 'diagnostics' in stages:
            for kind in ('diagnostic-compile','diagnostic-compile-source','diagnostic-image','diagnostic-material','diagnostic-script-material','pages'):
                self.diagnostic_fixture(kind)
        if 'lifecycle' in stages: lifecycle.exercise_lifecycle(self)
        if 'assets' in stages:
            lifecycle.exercise_package_assets(self)
            lifecycle.exercise_sprite_assets_and_matrix(self)
        if 'audio' in stages: lifecycle.exercise_audio(self)
        if 'error' in stages: lifecycle.exercise_error(self)
        if 'budget' in stages: lifecycle.exercise_budget(self)
        if 'runaway' in stages:
            for stage in ('Initialize','Evaluate'):
                for action in ('cancel','deadline','close'): lifecycle.exercise_runaway(self,stage,action)
        if 'b4' in stages: lifecycle.exercise_b4_rejections(self)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist',type=Path,required=True);parser.add_argument('--output',type=Path,required=True);parser.add_argument('--oracle',type=Path,required=True)
    parser.add_argument('--variant',action='store_true')
    parser.add_argument('--stages',default='healthy,diagnostics,lifecycle,assets,audio,error,budget,runaway,b4')
    args=parser.parse_args();output=args.output.resolve();output.mkdir(parents=True,exist_ok=False)
    probe=PackagePreviewProbe(output,args.dist.resolve(),args.oracle.resolve());failure=None
    try:
        if args.variant: probe.run_variant()
        else: probe.run(set(args.stages.split(',')))
    except BaseException as error: failure=error
    finally:
        try:
            probe.stop('package')
            probe.check(bool(probe.worker_identities),'actual Worker identities observed and drained')
            if hasattr(probe,'source_inputs'): probe.check(all(hashlib.sha256((ROOT/name).read_bytes()).hexdigest()==value for name,value in probe.source_inputs.items()), 'production inputs stable through HTTP')
        except BaseException as error: failure=failure or error
    try: scanned=probe.scan_credentials()
    except BaseException as error: failure=failure or error;scanned=None
    save_json(output/'summary.json',{'status':'failed' if failure else 'passed','error':str(failure) if failure else None,
        'httpRequests':probe.request_count,'assertions':len(probe.assertions),'credentialScannedFiles':scanned,
        'fixtureInputs':getattr(probe,'fixture_inputs',{}),'comparisons':probe.comparisons,'diagnostics':probe.diagnostic_results,'runaway':probe.runaway_results,'terminals':probe.terminals,
        'workerIdentities':[{'pid':p,'startTicks':t} for p,t in sorted(probe.worker_identities)],'assertionLabels':probe.assertions,
        'scope':'Finite public direct-package preview; direct oracle pixels/diagnostics, process-local runtime epoch/sequence separately checked. No browser/author/B4 package support claimed.'})
    save_json(output/'evidence-manifest.json',{'files':{str(p.relative_to(output)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.rglob('*')) if p.is_file()}})
    print(json.dumps({'status':'failed' if failure else 'passed','httpRequests':probe.request_count,'assertions':len(probe.assertions),'error':str(failure) if failure else None}))
    return 1 if failure else 0
if __name__=='__main__':sys.exit(main())
