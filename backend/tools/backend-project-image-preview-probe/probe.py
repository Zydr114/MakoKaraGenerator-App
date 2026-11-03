#!/usr/bin/env python3
"""Finite real HTTP acceptance for saved project Image background preview."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
SHARED = ROOT / 'tools/backend-public-package-preview-probe'
sys.path.insert(0, str(SHARED))
spec = importlib.util.spec_from_file_location('package_image_preview_base', SHARED / 'probe.py')
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
PROFILE = 'project-image-preview-v1'
LOCK = '/tmp/mkg-backend-b4-dotnet.lock'
REQUIRED_ORACLE_ASSEMBLIES = {
    'MakoKaraGenerator.Backend.Contracts.dll', 'MakoKaraGenerator.Backend.Rendering.dll',
    'MakoKaraGenerator.Core.dll', 'MakoKaraGenerator.Media.dll', 'MakoKaraGenerator.Processes.dll',
    'MakoKaraGenerator.Rendering.Abstractions.dll', 'MakoKaraGenerator.Rendering.Skia.dll',
    'MakoKaraGenerator.Runtime.Hosting.dll', 'MakoKaraGenerator.Scripting.Abstractions.dll',
    'MakoKaraGenerator.Scripting.dll', 'MakoKaraGenerator.Text.dll',
    'MakoKaraGenerator.Scripting.Compiler.dll', 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll',
}

COMPILER_ASSEMBLIES = {'MakoKaraGenerator.Scripting.Compiler.dll', 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll'}


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class ImagePreviewProbe(base.PackagePreviewProbe):
    def __init__(self, output, dist, oracle):
        super().__init__(output, dist, oracle)
        self.boundaries = []
        self.closed_projects = set()

    def verify_oracle_inputs(self, deployment):
        oracle_root = self.oracle.parent
        worker_root = self.dist / 'worker'
        # ScriptCompiler includes the actual TokenId SDK DLL bytes in ReferenceIdentity.
        # Equal versions and equal framework refs alone do not imply equal artifactHash.
        assemblies = {p.name for p in oracle_root.glob('*.dll') if p != self.oracle}
        oracle_refs = {p.name for p in (oracle_root / 'script-refs/net10.0').glob('*.dll')}
        worker_refs = {p.name for p in (worker_root / 'script-refs/net10.0').glob('*.dll')}
        names = sorted(assemblies | REQUIRED_ORACLE_ASSEMBLIES)
        names += ['script-refs/net10.0/' + name for name in sorted(oracle_refs | worker_refs)]
        inputs = []
        for name in names:
            component = 'server' if name in COMPILER_ASSEMBLIES else 'worker'
            oracle_file, worker_file = oracle_root / name, self.dist / component / name
            if name in COMPILER_ASSEMBLIES:
                self.check(not (worker_root / name).exists(), 'render Worker excludes compiler: ' + name)
            oracle_sha = digest(oracle_file) if oracle_file.is_file() else None
            worker_sha = digest(worker_file) if worker_file.is_file() else None
            deployed_sha = deployment['files'].get(component + '/' + name)
            inputs.append({'path': name, 'deploymentComponent': component, 'oracleSha256': oracle_sha, 'componentSha256': worker_sha,
                           'deploymentSha256': deployed_sha,
                           'matched': oracle_sha is not None and oracle_sha == worker_sha == deployed_sha})
        base.save_json(self.output / 'oracle-input-identity.json', {
            'deploymentSourceCommit': deployment['sourceCommit'], 'oraclePath': str(self.oracle),
            'oracleSha256': digest(self.oracle) if self.oracle.is_file() else None,
            'assemblyCount': len(assemblies), 'frameworkReferenceCount': len(oracle_refs), 'inputs': inputs,
            'scope': 'Pre-HTTP byte identity of every adjacent Oracle dependency DLL, including the actual scripting SDK, original Runtime/render chain and Roslyn, plus the complete framework reference set. Runtime matches Worker; compiler and Roslyn match Server. All bytes also match the immutable deployment manifest; Worker excludes compiler dependencies. No HTTP output constructs expectations.'})
        self.check(self.oracle.is_file(), 'independent Oracle DLL exists before HTTP')
        self.check(REQUIRED_ORACLE_ASSEMBLIES <= assemblies, 'complete original Runtime/compiler Oracle dependencies before HTTP')
        self.check('System.Runtime.dll' in oracle_refs and oracle_refs == worker_refs, 'complete identical Oracle/Worker framework reference set before HTTP')
        for item in inputs:
            self.check(item['matched'], 'Oracle/component/deployment byte identity before HTTP: ' + item['path'])

    def oracle_run(self, name, arguments):
        result = subprocess.run(['flock', '-o', LOCK, 'dotnet', str(self.oracle), *map(str, arguments)], capture_output=True, text=True)
        (self.output / (name + '-oracle.log')).write_text(result.stdout + result.stderr)
        self.check(result.returncode == 0, name + ': independent Runtime/fixture exit0')

    def prepare(self):
        self.oracle_run('fixture-generation', ['generate', self.output / 'profile/projects'])
        fixtures = self.output / 'profile/projects'
        self.fixture_inputs = {str(p.relative_to(fixtures)): digest(p) for p in sorted(fixtures.rglob('*')) if p.is_file()}
        self.fixtures = {item['kind']: item for item in json.loads((fixtures / 'fixtures.json').read_text())['fixtures']}
        self.check(self.fixtures['image-analysis']['baseProfile'] == 'static-lyrics-scene-analysis-v1'
                   and self.fixtures['image-analysis']['analysisContentHash'], 'saved Image fixture has complete Available static-analysis base source')
        base.save_json(self.output / 'fixture-source-manifest.json', {'files': self.fixture_inputs, 'fixtures': self.fixtures,
            'scope': 'Initial probe-owned saved ProjectStore closures. Mutations are recorded separately and only touch owned fixtures or the owned loaded-input fault-injection boundary.'})

    def direct(self, label, kind, width, height, transparent, times, counterfactual='none'):
        return super().direct(label, kind, width, height, transparent, times, counterfactual)

    def compare_frame(self, label, state, expected, kind, contribution=None):
        frame = state['session']['latestFrame']
        oracle = json.loads(expected.with_suffix('.json').read_text())
        self.check(frame['width'] == oracle['width'] and frame['height'] == oracle['height'], label + ': exact frame geometry metadata')
        self.check(str(frame['alphaMode']).lower() == oracle['alphaMode'], label + ': exact frame alpha metadata')
        self.check(frame['timeUs'] == oracle['timeUs'], label + ': exact frame time metadata')
        self.check(oracle['pixelBytes'] == frame['width'] * frame['height'] * 4 and oracle['bgraSha256'] == hashlib.sha256(expected.with_suffix('.bgra').read_bytes()).hexdigest(), label + ': complete oracle BGRA metadata')
        self.check(frame['previewId'] == state['session']['previewId'] and frame['previewGeneration'] == state['session']['previewGeneration'] and frame['sourceRevision'] == state['session']['source']['sourceRevision'], label + ': frame binds exact preview input generation')
        self.check(frame['content']['contentType'] == 'image/png' and int(frame['content']['sizeBytes']) == expected.with_suffix('.png').stat().st_size, label + ': exact PNG content metadata')
        return super().compare_frame(label, state, expected, kind, contribution)

    def compare(self, label, state, expected, kind, background=None, package=None):
        oracle = self.compare_frame(label, state, expected, kind)
        self.check(oracle['baseProfile'] == self.fixtures[kind]['baseProfile'], label + ': exact independent saved base source profile')
        self.check(oracle['analysisContentHash'] == self.fixtures[kind]['analysisContentHash'], label + ': exact saved analysis content identity')
        actual = expected.with_suffix('.bgra').read_bytes()
        if background is not None:
            self.check(actual != background.with_suffix('.bgra').read_bytes(), label + ': actual saved image contributes pixels versus Color background')
        if package is not None:
            self.check(actual != package.with_suffix('.bgra').read_bytes(), label + ': actual direct package contributes pixels versus disabled instance')
        self.comparisons[-1].update(backgroundCounterfactual=background is not None, packageCounterfactual=package is not None,
                                   savedImageSha256=oracle['imageSha256'])
        diagnostics, _ = self.diagnostics(label, state['session'])
        self.compare_diagnostics(label, diagnostics, oracle)
        return oracle

    def close_project(self, label, source):
        accepted = self.request(label + '-project-close', 'POST', '/api/v1/project-sessions/' + source['sourceId'] + '/close', status=202,
            value={'expectedRevision': source['sourceRevision'], 'policy': 'discard', 'activeJobs': 'wait'})
        self.job(label + '-project-close', accepted)
        self.closed_projects.add(source['sourceId'])
        fixed = self.output / 'profile/state/sessions' / source['sourceId'] / 'loaded-input'
        self.check(not fixed.exists(), label + ': owned loaded-input removed after project close')

    def matrix(self, kind, transparent):
        source = self.projects.get(kind) or self.open_fixture(kind)
        label = kind + ('-transparent' if transparent else '-opaque')
        preview = self.create_preview(label, source, transparent)
        times = [0, 100000, 300000, 500000]
        direct = self.direct(label, kind, 320, 180, transparent, times)
        background = None if transparent else self.direct(label + '-no-background', kind, 320, 180, False, times, 'background')
        package = self.direct(label + '-no-package', kind, 320, 180, transparent, times, 'package') if self.fixtures[kind]['package'] else None
        cursor = self.request(label + '-cursor', 'GET', '/api/v1/clients/' + self.client + '/state')['eventCursor']
        for sequence, when in enumerate(times, 1):
            preview = self.frame_request(label + '-' + str(sequence), preview, sequence, when)
            state = self.terminal(label + '-' + str(sequence), preview, sequence)
            self.compare(label + '-' + str(sequence), state, direct[sequence - 1], kind,
                         None if background is None else background[sequence - 1], None if package is None else package[sequence - 1])
            if sequence == 1:
                base.lifecycle.preview_events(self, cursor, state['session']['latestFrame'])
                # Preserve each matrix's actual SSE replay independently.
                (self.output / 'lifecycle-actual-sse.json').rename(self.output / (label + '-actual-sse.json'))
        previous = preview['previewGeneration']
        preview = self.frame_request(label + '-seek', preview, 5, 100000, 'seek')
        self.check(preview['previewGeneration'] != previous, label + ': backward seek changes generation')
        state = self.terminal(label + '-seek', preview, 5)
        self.compare(label + '-seek', state, self.direct(label + '-seek', kind, 320, 180, transparent, [100000])[0], kind)
        self.request(label + '-stale-generation', 'POST', '/api/v1/preview-sessions/' + preview['previewId'] + '/frame-requests', status=409, code='previewGenerationConflict',
            value={'expectedSourceRevision': source['sourceRevision'], 'previewGeneration': previous, 'requestSeq': '6', 'timeUs': '100000', 'intent': 'playback'})
        self.close_preview(label, preview)

    def lifecycle(self):
        kind = 'image-static'; source = self.projects[kind]
        preview = self.create_preview('lifecycle', source)
        preview = self.frame_request('lifecycle', preview, 1, 100000)
        first = self.terminal('lifecycle', preview, 1)['session']['latestFrame']
        foreign = self.register('image-foreign', select=False)['clientId']
        path = '/api/v1/preview-sessions/' + preview['previewId']
        self.request('image-foreign-state', 'GET', path, client=foreign, status=404, code='previewSessionNotFound')
        self.request('image-foreign-create', 'POST', '/api/v1/preview-sessions', client=foreign, status=404, code='sessionNotFound',
            value={'source': source, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview'})
        self.request('image-foreign-diagnostics', 'GET', path + '/diagnostics', client=foreign, status=401, code='resourceUnauthorized')
        self.fetch('image-foreign-frame', first['content']['contentUrl'], client=foreign, status=401)
        self.fetch('image-anonymous-frame', first['content']['contentUrl'], anonymous=True, status=401)
        self.request('image-stale-source', 'POST', '/api/v1/preview-sessions', status=409, code='revisionConflict',
            value={'source': {**source, 'sourceRevision': '999'}, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque'})
        preview = base.lifecycle.update(self, 'image-resize', preview, source, True, 480, 270)
        self.fetch('image-old-frame-after-resize', first['content']['contentUrl'], status=404)
        preview = self.frame_request('image-resize', preview, 2, 300000)
        state = self.terminal('image-resize', preview, 2)
        self.compare('image-resize', state, self.direct('image-resize', kind, 480, 270, True, [300000])[0], kind)
        alternate = self.open_fixture('image-alternate')
        preview = base.lifecycle.update(self, 'image-switch', preview, alternate, False)
        preview = self.frame_request('image-switch', preview, 3, 300000)
        state = self.terminal('image-switch', preview, 3)
        self.compare('image-switch', state, self.direct('image-switch', 'image-alternate', 320, 180, False, [300000])[0], 'image-alternate')
        self.close_preview('image-switch', preview)
        self.request('image-closed-diagnostics', 'GET', path + '/diagnostics', status=404, code='previewSessionNotFound')

    def mutate(self, name, file):
        original = file.read_bytes()
        changed = bytes([original[0] ^ 1]) + original[1:]
        file.write_bytes(changed)
        self.boundaries.append({'name': name, 'path': str(file.relative_to(self.output)), 'beforeSha256': hashlib.sha256(original).hexdigest(),
            'afterSha256': digest(file), 'sizeBytes': len(original), 'scope': 'Owned fixture mutation; loaded-input paths are explicit internal fault injection, not a public operation.'})
        base.save_json(self.output / 'owned-fixture-mutations.json', self.boundaries)

    def fixed_media(self, kind, source):
        filename = Path(self.fixtures[kind]['fixedPath']).name
        path = self.output / 'profile/state/sessions' / source['sourceId'] / 'loaded-input/data/media' / filename
        self.check(path.is_file() and path.stat().st_size == self.fixtures[kind]['sizeBytes'] and digest(path) == self.fixtures[kind]['sha256'], kind + ': exact owned loaded-input before explicit fault injection')
        return path

    def source_mutations(self):
        kind = 'image-source-change'; source = self.open_fixture(kind)
        expected = self.direct('source-change-original', kind, 320, 180, False, [100000])[0]
        # Changing the original saved project after open must not alter its pinned closure.
        self.mutate('original-saved-background-after-open', self.output / 'profile/projects' / self.fixtures[kind]['fixedPath'])
        preview = self.create_preview('source-change-original', source)
        preview = self.frame_request('source-change-original', preview, 1, 100000)
        self.compare('source-change-original', self.terminal('source-change-original', preview, 1), expected, kind)
        self.close_preview('source-change-original', preview)
        # Internal fault injection only: public callers cannot write this path.
        fixed = self.fixed_media(kind, source)
        self.mutate('loaded-input-before-capture', fixed)
        self.reject_source('source-change-fixed', source, 'ProjectImageSourceChanged')
        before = digest(fixed); size = fixed.stat().st_size; fixed.unlink()
        self.boundaries.append({'name': 'loaded-input-missing-before-capture', 'path': str(fixed.relative_to(self.output)),
            'beforeSha256': before, 'afterSha256': None, 'sizeBytes': size, 'scope': 'Owned loaded-input deletion is an explicit internal fault injection, not a public operation.'})
        base.save_json(self.output / 'owned-fixture-mutations.json', self.boundaries)
        self.reject_source('source-unavailable-fixed', source, 'ProjectImageSourceUnavailable')
        self.close_project(kind, source)
        kind = 'image-capture-stable'; source = self.open_fixture(kind)
        expected = self.direct('capture-original', kind, 320, 180, False, [100000])[0]
        preview = self.create_preview('capture-stable', source)
        self.mutate('loaded-input-after-capture', self.fixed_media(kind, source))
        preview = self.frame_request('capture-stable', preview, 1, 100000)
        self.compare('capture-stable', self.terminal('capture-stable', preview, 1), expected, kind)
        response = self.request('capture-stable-new-input-rejected', 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/input', status=422,
            value={'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': source['sourceRevision'], 'source': source,
                   'size': {'width': 480, 'height': 270}, 'alphaMode': 'opaque', 'metadata': self.metadata('changed-input')})
        self.check(response.get('failureCode') == 'ProjectImageSourceChanged' and response.get('field') == 'background', 'changed captured input stable failure')
        state = self.state('capture-stable-rejected-input-state', preview)
        self.check(state['session']['previewGeneration'] == preview['previewGeneration'], 'rejected input keeps captured preview generation')
        preview = self.frame_request('capture-stable-after-rejected-input', preview, 2, 100000)
        self.compare('capture-stable-after-rejected-input', self.terminal('capture-stable-after-rejected-input', preview, 2), expected, kind)
        self.close_preview('capture-stable', preview)
        self.close_project(kind, source)

    def reject_source(self, label, source, failure_code):
        response = self.request(label, 'POST', '/api/v1/preview-sessions', status=422, code='commandNotSupported',
            value={'source': source, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview'})
        self.check(response.get('failureCode') == failure_code and response.get('field') == 'background', label + ': stable fixed input rejection')

    def corrupt(self):
        source = self.open_fixture('image-corrupt')
        for transparent in (False, True):
            label = 'image-corrupt-' + ('transparent' if transparent else 'opaque')
            preview = self.create_preview(label, source, transparent)
            preview = self.frame_request(label, preview, 1, 100000)
            state = self.terminal(label, preview, 1, 'failed')
            self.check(state['session'].get('latestFrame') is None, label + ': corrupt image never publishes PNG')
            self.check(state.get('renderFailureCode') == 'PackageImageDecodeFailed', label + ': stable Worker decode failure')
            diagnostics, _ = self.diagnostics(label, preview)
            self.check(any(item['code'] == 'PackageImageDecodeFailed' for item in diagnostics), label + ': diagnostic page retains stable decode failure')
            self.close_preview(label, preview)
        self.close_project('image-corrupt', source)

    def project_close_lease(self):
        kind = 'image-lease'; source = self.open_fixture(kind)
        expected = self.direct('image-lease', kind, 320, 180, False, [100000, 300000])
        preview = self.create_preview('image-lease', source)
        self.close_project('image-lease', source)
        time.sleep(3.2)
        for sequence, when in enumerate([100000, 300000], 1):
            preview = self.frame_request('image-lease-' + str(sequence), preview, sequence, when)
            self.compare('image-lease-' + str(sequence), self.terminal('image-lease-' + str(sequence), preview, sequence), expected[sequence - 1], kind)
        self.close_preview('image-lease', preview)

    def verify_drained_files(self, label):
        profile = self.output / 'profile'
        pinned = sorted(str(p.relative_to(profile)) for p in profile.rglob('loaded-input') if p.is_dir())
        staging = sorted(str(p.relative_to(profile)) for p in profile.rglob('*') if p.is_dir() and any(word in p.name.lower() for word in ('image-stage', 'image-grant', 'image-pinned')))
        self.check(not pinned, label + ': all owned loaded-input closures drained')
        self.check(not staging, label + ': no unexpected image staging/grant directories retained')
        base.save_json(self.output / (label + '-filesystem-drain.json'), {'remainingLoadedInputs': pinned, 'remainingImageStagingDirectories': staging,
            'scope': 'Supplementary owned-profile filesystem observation; fixed image payload is in memory, no public staging/refcount endpoint claimed.'})

    def verify_fixture_inputs(self):
        fixtures = self.output / 'profile/projects'
        initial = self.fixture_inputs
        mutations = {entry['path'].removeprefix('profile/projects/'): entry['afterSha256'] for entry in self.boundaries if entry['path'].startswith('profile/projects/')}
        actual = {name: digest(fixtures / name) for name in initial}
        expected = {name: mutations.get(name, value) for name, value in initial.items()}
        self.check(actual == expected, 'all original fixture input bytes stable except recorded owned mutation')
        base.save_json(self.output / 'fixture-inputs-final.json', {'files': actual, 'recordedChanges': mutations, 'scope': 'Exact initial saved fixture inputs; runtime caches and loaded-input closures are separate evidence.'})

    def runaway_controls(self):
        for stage in ('Initialize', 'Evaluate'):
            for action in ('cancel', 'deadline', 'close'):
                base.lifecycle.exercise_runaway(self, stage, action, 'image-static', 'image-package')
                source = self.projects['runaway-' + stage + '-' + action]
                self.closed_projects.add(source['sourceId'])
                fixed = self.output / 'profile/state/sessions' / source['sourceId'] / 'loaded-input'
                self.check(not fixed.exists(), 'runaway-' + stage + '-' + action + ': owned Image loaded-input drained after close')

    def run(self):
        deployment = json.loads((self.dist / 'deployment.json').read_text())
        self.check(not deployment['sourceDirty'], 'source-clean matched Server/Worker deployment')
        self.source_inputs = deployment['sourceInputs']
        self.check(all(digest(ROOT / name) == value for name, value in self.source_inputs.items()), 'production inputs equal deployment manifest')
        self.verify_oracle_inputs(deployment)
        self.prepare(); self.start('image'); self.connect('image')
        runtime = self.request('image-runtime-capability', 'GET', '/api/v1/runtime-info')
        self.check(PROFILE in runtime['sourceProfiles'], 'actual Worker advertises image preview profile')
        for kind in ['image-static', 'image-analysis', 'image-package']:
            for transparent in [False, True]: self.matrix(kind, transparent)
        self.lifecycle(); self.source_mutations(); self.corrupt(); self.project_close_lease()
        self.runaway_controls()
        for kind, source in list(self.projects.items()):
            if source['sourceId'] not in self.closed_projects: self.close_project(kind, source)
        self.verify_drained_files('after-project-close')
        # Keep a real image Worker/session open for normal Server shutdown drain.
        source = self.open_fixture('image-shutdown')
        preview = self.create_preview('image-shutdown', source)
        preview = self.frame_request('image-shutdown', preview, 1, 100000)
        state = self.terminal('image-shutdown', preview, 1)
        self.compare('image-shutdown', state, self.direct('image-shutdown', 'image-shutdown', 320, 180, False, [100000])[0], 'image-shutdown')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--oracle', type=Path, required=True)
    args = parser.parse_args(); output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    probe = ImagePreviewProbe(output, args.dist.resolve(), args.oracle.resolve()); failure = None
    try: probe.run()
    except BaseException as error: failure = error
    finally:
        try:
            probe.stop('image')
            if probe.current_ready is not None: probe.verify_drained_files('after-server-shutdown')
            probe.check(bool(probe.worker_identities), 'actual Worker identities observed and drained')
            if hasattr(probe, 'source_inputs'):
                probe.check(all(digest(ROOT / name) == value for name, value in probe.source_inputs.items()), 'production source inputs stable through HTTP run')
            if hasattr(probe, 'fixture_inputs'): probe.verify_fixture_inputs()
        except BaseException as error: failure = failure or error
    try: scanned = probe.scan_credentials()
    except BaseException as error: failure = failure or error; scanned = None
    base.save_json(output / 'summary.json', {'status': 'failed' if failure else 'passed', 'error': str(failure) if failure else None,
        'httpRequests': probe.request_count, 'assertions': len(probe.assertions), 'credentialScannedFiles': scanned,
        'comparisons': probe.comparisons, 'diagnostics': probe.diagnostic_results, 'mutations': probe.boundaries, 'runaway': probe.runaway_results, 'terminals': probe.terminals,
        'workerIdentities': [{'pid': p, 'startTicks': t} for p, t in sorted(probe.worker_identities)], 'assertionLabels': probe.assertions,
        'scope': 'Finite saved project Image static/static-analysis/direct-package HTTP preview. Original Runtime PNG/pixels + ordinary background and package counterfactual; actual Initialize/Evaluate x cancel/deadline/close Worker/child drain. Loaded-input mutations are explicit internal fault injection. No video/font/preset/validation/export/browser/performance/GPU claim.'})
    base.save_json(output / 'evidence-manifest.json', {'files': {str(p.relative_to(output)): digest(p) for p in sorted(output.rglob('*')) if p.is_file()}})
    print(json.dumps({'status': 'failed' if failure else 'passed', 'httpRequests': probe.request_count, 'assertions': len(probe.assertions), 'error': str(failure) if failure else None}))
    return 1 if failure else 0


if __name__ == '__main__': sys.exit(main())
