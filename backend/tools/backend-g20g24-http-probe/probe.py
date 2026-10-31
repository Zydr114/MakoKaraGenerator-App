#!/usr/bin/env python3
"""Finite G20–G24 acceptance against a matched self-contained Server/Worker deployment."""
import argparse
import base64
import hashlib
import importlib.util
import io
import json
import math
from pathlib import Path
import sys
import wave

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('backend_smoke', ROOT / 'tools/backend-web-host/smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
save_json = smoke.existing.save_json


class OrchestrationProbe(smoke.Smoke):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.worker_identities = set()
        self.project = None
        self.revision = '0'
        self.measurements = {}

    @property
    def project_path(self):
        return '/api/v1/project-sessions/' + self.project

    def observe(self, pid):
        super().observe(pid)
        for identity in list(self.observed):
            try:
                if Path(f'/proc/{identity[0]}/exe').resolve().name == 'MakoKaraGenerator.Backend.Worker':
                    self.worker_identities.add(identity)
            except OSError:
                pass

    def send(self, name, value):
        previous = self.revision
        result = self.request(name, 'POST', self.project_path + '/commands',
            value=self.command(previous, name, value))
        self.check(result['previousRevision'] == previous, name + ': exact previous revision')
        self.check(int(result['projectRevision']) == int(previous) + int(result['changed']),
            name + ': one revision per actual change')
        self.revision = result['projectRevision']
        return result

    def orchestration(self, name):
        result = self.request(name, 'GET', self.project_path + '/orchestration?expectedRevision=' + self.revision)
        self.check(result['projectRevision'] == self.revision, name + ': exact read revision')
        return result

    def lyrics(self, name):
        result = self.request(name, 'GET', self.project_path + '/lyrics/window?branchId=primary&expectedRevision=' + self.revision)
        self.check(result['projectRevision'] == self.revision, name + ': exact lyric revision')
        return result

    def save_close(self, name):
        saved = self.job(name + '-save', self.request(name + '-save', 'POST', self.project_path + '/save-jobs',
            status=202, value={'expectedRevision': self.revision, 'targetHandleId': self.handle}))
        self.check(saved['savedProjectRevision'] == self.revision, name + ': saved terminal revision')
        self.job(name + '-close', self.request(name + '-close', 'POST', self.project_path + '/close', status=202,
            value={'expectedRevision': self.revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))

    def open(self, name):
        opened = self.job(name, self.request(name, 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': self.handle}))['result']
        self.project, self.revision = opened['projectSessionId'], opened['projectRevision']
        return opened

    def create_no_audio_fixture(self):
        # Public create currently requires audio. Remove only the audio declaration
        # from our own closed save, then reopen through the authenticated public API.
        audio = io.BytesIO()
        with wave.open(audio, 'wb') as stream:
            stream.setnchannels(1)
            stream.setsampwidth(2)
            stream.setframerate(8000)
            stream.writeframes(b'\x00\x00' * 64000)
        assets = []
        for kind, filename, content_type, data in (
            ('audio', 'g20g24.wav', 'audio/wav', audio.getvalue()),
            ('lyrics', 'g20g24.lrc', 'text/plain',
             b'[00:02.00]<00:02.00>FIRST<00:04.00>\n[00:05.00]<00:05.00>SECOND<00:07.00>\n')):
            name = 'seed-' + kind
            assets.append(self.request(name, 'POST', '/api/v1/assets?fileName=' + filename, raw=data,
                content_type=content_type, status=201, headers={'X-Mako-Request-Id': name,
                'X-Mako-Valid-Until-Utc': self.metadata(name)['validUntilUtc']})['assetId'])
        created = self.request('seed-create', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': 'G20 G24 deployed HTTP fixture', 'audioAssetId': assets[0], 'lyricsAssetId': assets[1],
            'importerId': 'lrc.enhanced', 'composition': {'canvasWidth': 320, 'canvasHeight': 180,
            'frameRateNumerator': 30, 'frameRateDenominator': 1, 'outputDurationUs': 8000000}})
        self.project, self.revision = created['projectSessionId'], created['projectRevision']
        handle = self.request('fixture-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': 'g20g24-fixture'})
        self.handle = handle['handleId']
        self.save_close('seed')
        manifest = self.output / 'profile/projects/g20g24-fixture/project.json'
        before = manifest.read_bytes()
        model = json.loads(before)
        model.pop('audioPath', None)
        model['media']['audio'] = None
        model['audioAnalysis'] = {'state': 'noAudio'}
        save_json(manifest, model)
        save_json(self.output / 'fixture.json', {'relativeDirectory': 'g20g24-fixture',
            'originalProjectSha256': hashlib.sha256(before).hexdigest(),
            'projectSha256': hashlib.sha256(manifest.read_bytes()).hexdigest(),
            'lyricsFile': model['lyricsFile'], 'applicationVersion': model.get('applicationVersion'),
            'scope': 'Only probe-owned closed HTTP save: remove audio declaration; not public no-audio creation acceptance.'})
        opened = self.open('no-audio-open')
        self.check(not opened.get('audioAssetId'), 'no-audio fixture has no active audio asset')
        lines = self.lyrics('timed-lines')['lines']
        self.check(len(lines) == 2, 'no-audio fixture contains exactly two real lyric lines')
        for index, (start, end) in enumerate((('2000000', '4000000'), ('5000000', '7000000'))):
            line = lines[index]
            if line['tokens']:
                start_us, duration_us = int(start), int(end) - int(start)
                count = len(line['tokens'])
                self.send('token-times-' + str(index), {'type': 'lyrics.setTokenTimes', 'changes': [
                    {'target': {'branchId': 'primary', 'lineId': line['id'], 'tokenId': token['id']},
                     'startUs': str(start_us + duration_us * token_index // count),
                     'endUs': str(start_us + duration_us * (token_index + 1) // count)}
                    for token_index, token in enumerate(line['tokens'])]})
            self.send('line-time-' + str(index), {'type': 'lyrics.setTimeRange',
                'target': {'branchId': 'primary', 'lineId': line['id']}, 'startUs': start, 'endUs': end})
        return [line['id'] for line in lines]

    def measure(self, name, mode, time_us):
        result = self.request(name, 'POST', self.project_path + '/orchestration/measurements', value={
            'expectedRevision': self.revision, 'mode': mode, 'timeUs': time_us,
            'width': 320, 'height': 180, 'alphaMode': 'transparent'})
        self.check(result['projectSessionId'] == self.project and result['projectRevision'] == self.revision,
            name + ': project identity and revision')
        measured = result['measurement']
        self.check(measured['mode'] == mode and measured['timeUs'] == time_us,
            name + ': exact sampling input')
        self.check(measured['samplingProfile'] == ('time-v1' if mode == 'time' else 'layout-reference-v1'),
            name + ': sampling profile')
        for key in ('sourceSha256', 'sampleInputFingerprint', 'pngSha256'):
            self.check(len(measured[key]) == 64 and all(char in '0123456789abcdef' for char in measured[key]),
                name + ': canonical ' + key)
        png = base64.b64decode(measured['png'], validate=True)
        self.check(hashlib.sha256(png).hexdigest() == measured['pngSha256'], name + ': full PNG SHA256')
        self.check(png[:8] == b'\x89PNG\r\n\x1a\n' and int.from_bytes(png[16:20], 'big') == 320
            and int.from_bytes(png[20:24], 'big') == 180, name + ': PNG signature and dimensions')
        (self.output / (name + '.png')).write_bytes(png)
        ids = [item['id'] for item in measured['elements']]
        self.check(len(ids) == len(set(ids)), name + ': unique element identities')
        def finite(value):
            if isinstance(value, dict): return all(finite(item) for item in value.values())
            if isinstance(value, list): return all(finite(item) for item in value)
            return not isinstance(value, (float, int)) or math.isfinite(value)
        for item in measured['elements']:
            self.check(finite(item), name + ': finite geometry ' + item['id'])
            self.check(0 <= item['opacity'] <= 1, name + ': bounded opacity ' + item['id'])
            if item['visible']:
                self.check(item['worldMatrix'] is not None and item['pivot'] is not None,
                    name + ': visible element matrix and pivot ' + item['id'])
        self.measurements[name] = {key: measured[key] for key in (
            'sourceSha256', 'sampleInputFingerprint', 'sourceProfile', 'samplingProfile', 'mode', 'timeUs', 'pngSha256')}
        self.measurements[name].update(projectRevision=self.revision, elementCount=len(ids))
        return measured

    def run(self):
        deployment = json.loads((self.dist / 'deployment.json').read_text())
        self.check(deployment['version'] == '0.94.0', 'candidate deployment version 0.94.0')
        self.start('deployed')  # host.py validates the complete matched deployment before start.
        self.check(self.current_ready['version'] == deployment['version'], 'launcher reports deployment version')
        self.connect('deployed')
        capabilities = self.request('measurement-capability', 'GET', '/api/v1/capabilities')
        self.check('orchestration.measurements.scene-measurement-v1' in capabilities['features'],
            'public matched scene-measurement-v1 capability')
        first, second = self.create_no_audio_fixture()
        initial = self.orchestration('initial-orchestration')
        root = next(item for item in initial['programs'] if item['branchId'] == 'primary')['rootContainerId']
        container = self.send('create-container', {'type': 'orchestration.createNode', 'kind': 'auto',
            'parentId': root, 'name': 'HTTP measurement container', 'slotCount': None})['structureResult']['nodeId']
        self.send('default-container', {'type': 'orchestration.setDefaultContainer', 'branchId': 'primary', 'containerId': container})
        animation = {'durationUs': '400000', 'easing': 'Linear', 'startOpacity': .2, 'startScale': .8, 'startOffsetPx': 12}
        self.send('project-enter-animation', {'type': 'orchestration.setProjectEnterAnimation', 'animation': animation})
        self.send('line-enter-animation', {'type': 'orchestration.setLineEnterAnimation', 'branchId': 'primary',
            'lineId': first, 'animation': {'durationUs': '200000', 'easing': 'Linear', 'startOpacity': .4,
                'startScale': .9, 'startOffsetPx': 8}})
        self.send('line-local-transform', {'type': 'orchestration.setLineLocalTransform', 'branchId': 'primary',
            'lineId': first, 'transform': {'offsetXRatio': .05, 'offsetYRatio': -.03, 'rotationDegrees': 8, 'scale': 1.1}})
        presentation = self.request('line-presentation', 'GET', self.project_path +
            '/orchestration/line-presentation?branchId=primary&lineId=' + first + '&expectedRevision=' + self.revision)
        self.check(presentation['localEnterAnimation']['durationUs'] == '200000'
            and presentation['localTransform']['rotationDegrees'] == 8, 'line presentation retains local animation and transform')
        self.send('layout-kind', {'type': 'orchestration.setLayoutKind', 'containerId': container,
            'kind': 'SlotSequence', 'slotIds': [], 'newSlotCount': 2})
        tree = self.orchestration('slot-orchestration')
        slots = next(item for item in tree['nodes'] if item['id'] == container)['layout']['slotIds']
        self.check(len(slots) == 2 and len(set(slots)) == 2, 'layout switch creates two stable distinct slots')
        countdown = self.send('countdown', {'type': 'orchestration.createCountdown', 'parentId': root,
            'name': 'Deployed countdown', 'count': 3})['leadInResult']
        indicator, mark = countdown['indicatorContainerId'], countdown['markNodeIds'][0]
        settings = {'includeOpening': True, 'includeLongGaps': False, 'gapThresholdUs': '8000000',
            'durationUs': '1000000', 'indicatorContainerId': indicator, 'excludedLineIds': []}
        preview = self.send('generation-preview', {'type': 'orchestration.previewLeadInGeneration',
            'branchId': 'primary', 'settings': settings})
        self.check(not preview['changed'] and any(item['targetLineId'] == first and item['status'] == 'New'
            for item in preview['leadInGenerationResult']['candidates']), 'generation preview is read-only with selected first-line candidate')
        self.send('generation-apply', {'type': 'orchestration.applyLeadInGeneration', 'branchId': 'primary',
            'settings': settings, 'selectedLineIds': [first]})
        self.send('mark-text', {'type': 'orchestration.setMarkText', 'nodeId': mark, 'text': 'GO'})
        tree = self.orchestration('cue-orchestration')
        cues = next(item for item in tree['programs'] if item['branchId'] == 'primary')['leadIns']
        self.check(len(cues) == 1 and cues[0]['targetLineId'] == first and cues[0]['targetLineId'] != second,
            'selected application creates only first-line cue')
        before_read = self.request('before-measurements', 'GET', self.project_path)
        cue = self.measure('cue-time', 'time', '1500000')
        line = self.measure('line-time', 'time', '3000000')
        layout = self.measure('layout', 'layout', None)
        repeated = self.measure('layout-repeat', 'layout', None)
        self.check(cue['sourceSha256'] == line['sourceSha256'] == layout['sourceSha256'], 'sampling modes share fixed source')
        self.check(layout['sampleInputFingerprint'] == repeated['sampleInputFingerprint']
            and layout['pngSha256'] == repeated['pngSha256'], 'repeated layout input and PNG are stable')
        self.check(cue['sampleInputFingerprint'] != line['sampleInputFingerprint'] != layout['sampleInputFingerprint'],
            'distinct sampling inputs have distinct fingerprints')
        self.check(any(item['id'] == mark and item['visible'] for item in cue['elements']), 'real Worker measures visible countdown mark')
        visible_line = next(item for item in line['elements'] if item['kind'] == 'line' and item.get('lineId') == first)
        self.check(visible_line['visible'] and len(visible_line['worldCorners']) >= 4
            and visible_line['slotId'] in slots, 'real Worker line ink uses stable slot identity')
        self.check(all(any(item['representative'] and item.get('slotId') == slot for item in layout['elements']) for slot in slots),
            'layout mode measures representatives in both real slots')
        after_read = self.request('after-measurements', 'GET', self.project_path)
        self.check(all(before_read[key] == after_read[key] for key in ('projectRevision', 'isDirty', 'canUndo', 'canRedo')),
            'measurement changes no revision dirty or history state')
        changed = self.send('attachment-fixed', {'type': 'orchestration.setLeadInAttachment', 'branchId': 'primary',
            'leadInId': cues[0]['id'], 'containerId': indicator, 'mode': 'FixedParent'})
        self.check(changed['changed'], 'attachment mode switch commits actual Worker reference')
        attached = self.orchestration('attached-orchestration')
        self.check(next(item for item in attached['nodes'] if item['id'] == indicator)['content']['attachmentMode'] == 'FixedParent',
            'read projection reports fixed-parent attachment')
        final = self.measure('attached-time', 'time', '1500000')
        self.check(final['sourceSha256'] != cue['sourceSha256'], 'attachment edits fixed source identity')
        stale_before = self.request('before-stale-measurement', 'GET', self.project_path + '/history')
        self.request('stale-measurement', 'POST', self.project_path + '/orchestration/measurements',
            status=409, code='revisionConflict', value={'expectedRevision': before_read['projectRevision'],
            'mode': 'time', 'timeUs': '1500000', 'width': 320, 'height': 180, 'alphaMode': 'transparent'})
        stale_after = self.request('after-stale-measurement', 'GET', self.project_path + '/history')
        self.check(all(stale_before[key] == stale_after[key] for key in
            ('projectRevision', 'canUndo', 'canRedo', 'undoCount', 'redoCount')),
            'stale measurement rejects with 409 and changes no revision or history')
        stale_tree = self.orchestration('after-stale-orchestration')
        self.check(all(attached[key] == stale_tree[key] for key in ('nodes', 'programs', 'projectEnterAnimation')),
            'stale measurement preserves complete orchestration state')
        lyric_snapshot = self.lyrics('before-save-lyrics')['lines']
        old_session = self.project
        self.save_close('final')
        reopened = self.open('final-reopen')
        self.check(self.project != old_session and not reopened['isDirty'], 'saved project reopens clean under new identity')
        restored = self.orchestration('restored-orchestration')
        self.check(all(attached[key] == restored[key] for key in ('nodes', 'programs', 'projectEnterAnimation')),
            'save and reopen preserve full orchestration and project animation')
        self.check(lyric_snapshot == self.lyrics('restored-lyrics')['lines'], 'save and reopen preserve full lyric identities and timing')
        restored_time = self.measure('restored-time', 'time', '1500000')
        self.check(all(final[key] == restored_time[key] for key in ('sourceSha256', 'sampleInputFingerprint', 'pngSha256', 'elements')),
            'save and reopen preserve exact source fingerprint geometry and PNG')
        self.save_close('restored')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    probe = OrchestrationProbe(output, args.dist.resolve())
    failure = None
    try:
        probe.run()
    except BaseException as error:
        failure = error
    finally:
        try:
            probe.stop('deployed')
            if probe.current_ready is not None:
                probe.check(bool(probe.worker_identities), 'actual Worker process identities observed and drained')
        except BaseException as error:
            failure = failure or error
    try:
        scan_count = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error
        scan_count = None
    summary = {'status': 'failed' if failure else 'passed', 'httpRequests': probe.request_count,
        'assertions': len(probe.assertions), 'credentialScannedFiles': scan_count,
        'errorType': type(failure).__name__ if failure else None,
        'deploymentManifestSha256': hashlib.sha256((probe.dist / 'deployment.json').read_bytes()).hexdigest()
            if (probe.dist / 'deployment.json').is_file() else None,
        'runs': probe.runs, 'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(probe.worker_identities)],
        'measurements': probe.measurements, 'assertionLabels': probe.assertions,
        'scope': 'Finite deployed HTTP and actual Worker measurement/attachment/save/reopen/drain chain; offline probe-owned no-audio fixture; no browser, export pixel oracle or full platform acceptance.'}
    save_json(output / 'summary.json', summary)
    save_json(output / 'evidence-manifest.json', {'files': {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in sorted(output.rglob('*')) if path.is_file()}})
    print(json.dumps({key: summary[key] for key in ('status', 'httpRequests', 'assertions', 'errorType')}))
    return 1 if failure else 0


if __name__ == '__main__':
    sys.exit(main())
