#!/usr/bin/env python3
"""Finite ruby spacing HTTP acceptance, reusing the existing deployed Server/Worker harness."""
import argparse
import base64
import hashlib
import importlib.util
import json
import math
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('business', ROOT / 'tools/backend-basic-business-http-probe/probe.py')
business = importlib.util.module_from_spec(spec)
spec.loader.exec_module(business)
save_json = business.save_json


def fixture(kind):
    """Project-profile source with explicit stable IDs, times, alignment and segment boundaries."""
    tt, mkg, tts = 'http://www.w3.org/ns/ttml', 'urn:makokara:ttml', 'http://www.w3.org/ns/ttml#styling'
    ET.register_namespace('', tt); ET.register_namespace('mkg', mkg); ET.register_namespace('tts', tts)
    def attrs(**items): return {'{' + mkg + '}' + key.replace('_', '-'): str(value) for key, value in items.items()}
    root = ET.Element('{' + tt + '}tt', attrs(profile='ttml.project', version='1', lyrics_schema='17', timing_profile='ttml2-media', primary_branch_id='primary'))
    branch = ET.SubElement(ET.SubElement(root, '{' + tt + '}body'), '{' + tt + '}div', attrs(branch_id='primary', branch_name='Primary', branch_role='Primary', timing_mode='Independent'))
    values = [('歌', 'せん', 'Center')] if kind == 'legacy-single' else [('歌', 'ながいルビひとつ', 'Center')]
    if kind == 'collision': values = [('歌', 'ながいせん', 'End'), ('声', 'ひろいこえ', 'Start'), ('空', 'とおいそら', 'Center')]
    line = ET.SubElement(branch, '{' + tt + '}p', {**attrs(line_id='line', line_text=''.join(v[0] for v in values), start_us=1000000, end_us=3000000, timing_status='Confirmed'), 'begin': '1s', 'end': '3s'})
    for i, (text, ruby, alignment) in enumerate(values):
        token_start = 1000000 + 2000000 * i // len(values)
        token_end = 1000000 + 2000000 * (i + 1) // len(values)
        token = ET.SubElement(line, '{' + tt + '}span', {**attrs(token_id='token-' + str(i), start_us=token_start, end_us=token_end, timing_status='Confirmed'),
            'begin': f'{token_start / 1000000:.6f}s', 'end': f'{token_end / 1000000:.6f}s'})
        container = ET.SubElement(token, '{' + tt + '}span', {**attrs(ruby_id='ruby-' + str(i), base_token_id='token-' + str(i), scale='.5', gap='2', alignment=alignment), '{' + tts + '}ruby': 'container'})
        ET.SubElement(container, '{' + tt + '}span', {'{' + tts + '}ruby': 'base'}).text = text
        ruby_container = ET.SubElement(container, '{' + tt + '}span', {'{' + tts + '}ruby': 'textContainer'})
        for j, part in enumerate((ruby[:len(ruby)//2], ruby[len(ruby)//2:])):
            start, end = ((1100000, 1500000), (1800000, 2200000))[j]
            ET.SubElement(ruby_container, '{' + tt + '}span', {**attrs(segment_id=f'segment-{i}-{j}', start_us=start, end_us=end, timing_status='Confirmed'), '{' + tts + '}ruby': 'text', 'begin': str(start/1000000) + 's', 'end': str(end/1000000) + 's'}).text = part
    return ET.tostring(root, encoding='utf-8', xml_declaration=True)


class SpacingProbe(business.BasicBusinessProbe):
    def __init__(self, output, dist, oracle):
        super().__init__(output, dist)
        self.oracle = oracle
        self.comparisons = []
        self.audio_normalizations = []

    def create(self, kind):
        xml = fixture(kind)
        (self.output / (kind + '.ttml')).write_bytes(xml)
        audio, _ = self.wav(4, 8000, 220, 0)
        asset = self.upload(kind + '-audio', kind + '.wav', 'audio/wav', audio)['assetId']
        lyrics = self.upload(kind + '-ttml', kind + '.ttml', 'application/ttml+xml', xml)['assetId']
        created = self.request(kind + '-create', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': kind, 'audioAssetId': asset, 'lyricsAssetId': lyrics, 'importerId': 'ttml',
            'composition': {'canvasWidth': 320, 'canvasHeight': 180, 'frameRateNumerator': 30, 'frameRateDenominator': 1, 'outputDurationUs': 4000000}})
        self.project, self.revision = created['projectSessionId'], created['projectRevision']
        window = self.lyrics(kind + '-lyrics')
        rubies = window['lines'][0]['rubyAnnotations']
        self.check(len(rubies) == (3 if kind == 'collision' else 1), kind + ': complete ruby annotations imported')
        self.check(all(len(ruby['segments']) == 2 for ruby in rubies), kind + ': independent timed segments imported')
        self.handle = self.request(kind + '-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': kind})['handleId']
        self.job(kind + '-save', self.request(kind + '-save', 'POST', self.project_path + '/save-jobs', status=202,
            value={'expectedRevision': self.revision, 'targetHandleId': self.handle}))
        # Reopen the public save before comparing full snapshots: both independent
        # consumers now resolve the same owned persisted audio declaration.
        self.job(kind + '-seed-close', self.request(kind + '-seed-close', 'POST', self.project_path + '/close', status=202,
            value={'expectedRevision': self.revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))
        self.open(kind + '-seed-open')
        return window

    def measure_oracle(self, kind, label, mode, time_us, transparent):
        name = kind + '-' + label
        result = self.request(name, 'POST', self.project_path + '/orchestration/measurements', value={
            'expectedRevision': self.revision, 'mode': mode, 'timeUs': time_us,
            'width': 320, 'height': 180, 'alphaMode': 'transparent' if transparent else 'opaque'})
        self.check(result['projectRevision'] == self.revision, name + ': exact revision')
        measured = result['measurement']
        png = base64.b64decode(measured['png'], validate=True)
        self.check(hashlib.sha256(png).hexdigest() == measured['pngSha256'], name + ': response PNG SHA')
        (self.output / (name + '.png')).write_bytes(png)
        prefix = self.output / (name + '-oracle')
        saved_directory = self.output / 'profile/projects' / kind
        pinned_directory = self.output / 'profile/state/sessions' / self.project / 'loaded-input'
        saved_audio = saved_directory / json.loads((saved_directory / 'project.json').read_text())['audioPath']
        pinned_audio = pinned_directory / json.loads((pinned_directory / 'project.json').read_text())['audioPath']
        saved_bytes, pinned_bytes = saved_audio.read_bytes(), pinned_audio.read_bytes()
        saved_sha, pinned_sha = hashlib.sha256(saved_bytes).hexdigest(), hashlib.sha256(pinned_bytes).hexdigest()
        self.check(len(saved_bytes) == len(pinned_bytes) and saved_sha == pinned_sha, name + ': public save and owned pinned audio size/SHA identical')
        self.audio_normalizations.append({'name': name, 'savedRelativePath': str(saved_audio.resolve().relative_to(self.output)),
            'pinnedRelativePath': str(pinned_audio.resolve().relative_to(self.output)), 'sizeBytes': len(saved_bytes),
            'savedSha256': saved_sha, 'pinnedSha256': pinned_sha, 'scope': 'Only direct oracle AudioPath declaration normalized; all other snapshot fields independently loaded.'})
        command = ['flock', '-o', '/tmp/mkg-ttml-dotnet.lock', 'dotnet', str(self.oracle),
            str(saved_directory), str(prefix), str(time_us or 0), str(transparent).lower(), str(mode == 'layout').lower(), str(pinned_audio)]
        run = subprocess.run(command, capture_output=True, text=True)
        (self.output / (name + '-oracle.log')).write_text(run.stdout + run.stderr)
        self.check(run.returncode == 0, name + ': independent Runtime oracle exit0')
        oracle = json.loads(prefix.with_suffix('.json').read_text())
        self.check(oracle['engineIdentity'] == 'MakoKaraGenerator:0.97.0', name + ': matched direct Runtime engine identity')
        self.check(measured['sourceSha256'] == oracle['sourceSha256'], name + ': exact complete source SHA matches direct Runtime')
        self.check(png == prefix.with_suffix('.png').read_bytes(), name + ': complete PNG byteexact direct Runtime')
        actual = {item['id']: item for item in measured['elements']}
        self.check(len(actual) == len(measured['elements']), name + ': unique HTTP element IDs')
        self.check(set(actual) == {item['id'] for item in oracle['elements']}, name + ': exact direct Runtime element ID set')
        for item in oracle['elements']:
            other = actual[item['id']]
            self.check(other['visible'] == item['isVisible'], name + ': same visibility ' + item['id'])
            for key in ('opacity', 'worldMatrix', 'pivot', 'worldCorners', 'branchId', 'lineId', 'tokenId', 'rubyId'):
                actual_value = other.get(key) if key in ('branchId', 'lineId', 'tokenId', 'rubyId') else other[key]
                self.check(actual_value == item[key], name + ': same direct geometry ' + key + ' ' + item['id'])
        rubies = sorted((item for item in measured['elements'] if item.get('rubyId') and item['visible']), key=lambda item: item['rubyId'])
        self.check(len(rubies) == (0 if mode == 'layout' else 3 if kind == 'collision' else 1), name + ': visible ruby count follows time/layout sampling contract')
        if kind == 'collision':
            for left, right in zip(rubies, rubies[1:]):
                gap = min(p['x'] for p in right['worldCorners']) - max(p['x'] for p in left['worldCorners'])
                matrix = left['worldMatrix']
                minimum = math.hypot(matrix['a'], matrix['b'])
                self.check(gap >= minimum - 1e-4, name + ': world ruby gap >= scaled 1px ' + left['rubyId'])
        self.comparisons.append({'name': name, 'mode': mode, 'timeUs': time_us, 'transparent': transparent, 'sourceSha256': measured['sourceSha256'], 'oracleSourceSha256': oracle['sourceSha256'], 'pngSha256': measured['pngSha256'], 'directGeometryCount': len(oracle['elements'])})
        return measured

    def run(self):
        deployment = json.loads((self.dist / 'deployment.json').read_text())
        self.check(deployment['version'] == '0.97.0', 'matched 0.97.0 deployment')
        self.check(not deployment['sourceDirty'], 'production source clean at publish')
        self.source_inputs = deployment['sourceInputs']
        self.check(all((ROOT / name).is_file() and hashlib.sha256((ROOT / name).read_bytes()).hexdigest() == expected
            for name, expected in self.source_inputs.items()), 'current direct oracle source matches deployment source inputs')
        self.start('spacing'); self.connect('spacing')
        for kind in ('collision', 'legacy-single', 'single-wide'):
            window = self.create(kind)
            before = self.request(kind + '-before', 'GET', self.project_path)
            frames = []
            for transparent in (False, True):
                for index, time in enumerate(('1200000', '1400000', '2000000')):
                    frames.append(self.measure_oracle(kind, ('transparent' if transparent else 'opaque') + '-' + str(index), 'time', time, transparent))
                self.measure_oracle(kind, ('transparent' if transparent else 'opaque') + '-layout', 'layout', None, transparent)
            def geometry(frame): return [{key: item[key] for key in ('id', 'worldMatrix', 'pivot', 'worldCorners')} for item in frame['elements']]
            self.check(all(geometry(frame) == geometry(frames[0]) for frame in frames), kind + ': timed/alpha changes preserve geometry')
            self.check(frames[0]['pngSha256'] != frames[1]['pngSha256'], kind + ': segment time changes paint')
            after = self.request(kind + '-after', 'GET', self.project_path)
            self.check(before == after, kind + ': measurement does not mutate state')
            self.request(kind + '-stale', 'POST', self.project_path + '/orchestration/measurements', status=409,
                value={'expectedRevision': '999', 'mode': 'time', 'timeUs': '1200000', 'width': 320, 'height': 180, 'alphaMode': 'transparent'})
            self.save_close(kind)
            self.open(kind + '-reopen')
            restored = self.lyrics(kind + '-restored')
            self.check(restored['lines'] == window['lines'], kind + ': save/reopen preserves all lyric times/IDs/text')
            repeat = self.measure_oracle(kind, 'reopened', 'time', '1200000', False)
            self.check(repeat['pngSha256'] == frames[0]['pngSha256'], kind + ': reopen same PNG')
            self.job(kind + '-final-close', self.request(kind + '-final-close', 'POST', self.project_path + '/close', status=202,
                value={'expectedRevision': self.revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--oracle', type=Path, required=True)
    args = parser.parse_args(); output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    probe = SpacingProbe(output, args.dist.resolve(), args.oracle.resolve()); failure = None
    try: probe.run()
    except BaseException as error: failure = error
    finally:
        try:
            probe.stop('spacing')
            probe.check(bool(probe.worker_identities), 'actual Worker identities observed and drained')
            probe.check(bool(getattr(probe, 'source_inputs', {})) and all((ROOT / name).is_file() and hashlib.sha256((ROOT / name).read_bytes()).hexdigest() == expected
                for name, expected in probe.source_inputs.items()), 'production source remains byteexact through HTTP acceptance')
        except BaseException as error: failure = failure or error
    try: scanned = probe.scan_credentials()
    except BaseException as error: failure = failure or error; scanned = None
    save_json(output / 'summary.json', {'status': 'failed' if failure else 'passed', 'error': str(failure) if failure else None,
        'httpRequests': probe.request_count, 'assertions': len(probe.assertions), 'credentialScannedFiles': scanned,
        'terminals': probe.terminals, 'comparisons': probe.comparisons, 'audioPathNormalizations': probe.audio_normalizations, 'assertionLabels': probe.assertions,
        'oracleSha256': hashlib.sha256(probe.oracle.read_bytes()).hexdigest(),
        'productionSourceStable': all((ROOT / name).is_file() and hashlib.sha256((ROOT / name).read_bytes()).hexdigest() == expected
            for name, expected in getattr(probe, 'source_inputs', {}).items()),
        'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(probe.worker_identities)],
        'deploymentManifestSha256': hashlib.sha256((probe.dist / 'deployment.json').read_bytes()).hexdigest(),
        'scope': 'Only oracle AudioPath declaration normalized to size/SHA-verified same-byte owned pinned closure; full source SHA still required. Finite real HTTP TTML import/time-layout same-frame PNG and geometry/direct Runtime/save-reopen/stale/drain. Legacy golden and old Worker identity incompatibility are separate regressions.'})
    save_json(output / 'evidence-manifest.json', {'files': {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(output.rglob('*')) if p.is_file()}})
    print(json.dumps({'status': 'failed' if failure else 'passed', 'httpRequests': probe.request_count, 'assertions': len(probe.assertions), 'error': str(failure) if failure else None}))
    return 1 if failure else 0

if __name__ == '__main__': sys.exit(main())
