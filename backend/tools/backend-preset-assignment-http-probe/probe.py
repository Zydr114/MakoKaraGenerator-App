#!/usr/bin/env python3
"""Bounded real HTTP acceptance for project preset assignment and static rendering."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
import shutil
import subprocess
import sys
import time
import wave
from pathlib import Path
from urllib.parse import urlencode

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('backend_smoke', ROOT / 'tools/backend-web-host/smoke.py')
smoke = importlib.util.module_from_spec(spec); spec.loader.exec_module(smoke)
spec = importlib.util.spec_from_file_location('template_fixtures', ROOT / 'tools/backend-api-contract/template_library_fixtures.py')
sys.path.insert(0, str(ROOT / 'tools/backend-api-contract'))
fixtures = importlib.util.module_from_spec(spec); spec.loader.exec_module(fixtures)


class AssignmentProbe(smoke.Smoke):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.worker_identities = set()
        self.project = None
        self.project_path = None
        self.preview_hashes = {}
        self.preview_revisions = {}

    def observe(self, pid):
        super().observe(pid)
        for identity in list(self.observed):
            try:
                if Path(f'/proc/{identity[0]}/exe').resolve().name == 'MakoKaraGenerator.Backend.Worker':
                    self.worker_identities.add(identity)
            except OSError:
                pass

    def create_project(self):
        audio = io.BytesIO()
        with wave.open(audio, 'wb') as stream:
            stream.setnchannels(1); stream.setsampwidth(2); stream.setframerate(8000)
            stream.writeframes(b'\x00\x00' * 16000)
        audio_value = self.request('audio-upload', 'POST', '/api/v1/assets?fileName=assignment.wav', raw=audio.getvalue(),
            content_type='audio/wav', headers={'X-Mako-Request-Id': 'assignment-audio',
            'X-Mako-Valid-Until-Utc': self.metadata('assignment-audio')['validUntilUtc']}, status=201)
        lyrics_value = self.request('lyrics-upload', 'POST', '/api/v1/assets?fileName=assignment.lrc',
            raw=b'[00:00.00]<00:00.00>Assignment<00:00.80>\n[00:00.80]<00:00.80>Second<00:01.60>\n', content_type='text/plain',
            headers={'X-Mako-Request-Id': 'assignment-lyrics',
            'X-Mako-Valid-Until-Utc': self.metadata('assignment-lyrics')['validUntilUtc']}, status=201)
        created = self.request('project-create', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': 'Preset assignment HTTP', 'audioAssetId': audio_value['assetId'], 'lyricsAssetId': lyrics_value['assetId'],
            'importerId': 'lrc.enhanced',
            'composition': {'canvasWidth': 320, 'canvasHeight': 180, 'frameRateNumerator': 30,
                'frameRateDenominator': 1, 'backgroundRed': 0, 'backgroundGreen': 0,
                'backgroundBlue': 0, 'backgroundAlpha': 255, 'outputDurationUs': 2000000}})
        self.project = created['projectSessionId']
        self.project_path = '/api/v1/project-sessions/' + self.project
        self.check(created['projectRevision'] == '0', 'new project starts at revision zero')
        return audio.getvalue()

    def capture(self, name, revision):
        created = self.request(name + '-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': revision},
            'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'purpose': 'preview',
            'metadata': self.metadata(name + '-create')})
        path = '/api/v1/preview-sessions/' + created['previewId']
        self.request(name + '-frame-request', 'POST', path + '/frame-requests', status=202, value={
            'expectedSourceRevision': revision, 'previewGeneration': created['previewGeneration'],
            'requestSeq': '1', 'timeUs': '500000', 'intent': 'playback', 'metadata': self.metadata(name + '-frame')})
        state = None
        for index in range(300):
            state = self.request(name + '-state-' + str(index), 'GET', path)
            if state['session']['state'] == 'ready': break
            self.check(state['session']['state'] != 'failed', name + ': preview did not fail')
            time.sleep(.05)
        else: raise AssertionError(name + ': preview never became ready')
        frame = state['session']['latestFrame']; self.check(frame['sourceRevision'] == revision, name + ': frame source revision')
        _, data = self.binary(name + '-content', frame['content']['contentUrl'])
        digest = hashlib.sha256(data).hexdigest(); self.check(digest == frame['content']['sha256'], name + ': frame SHA')
        self.preview_hashes[name] = digest
        self.request(name + '-close', 'POST', path + '/close', value={
            'expectedPreviewGeneration': created['previewGeneration'], 'expectedSourceRevision': revision,
            'metadata': self.metadata(name + '-close')})
        return digest

    def assert_preset_preview(self, name, revision, expected=None):
        digest = self.capture(name, revision)
        self.preview_revisions[name] = revision
        if expected is not None:
            self.check(digest == expected, name + ': preview PNG restores expected pixels')
        return digest

    def run_static_worker_acceptance(self, revision):
        profile = 'static-lyrics-scene-presets-v1'
        validation = self.request('preset-validation-accept', 'POST', self.project_path + '/validation-jobs', status=202,
            value={'expectedRevision': str(revision), 'framesPerOutput': 1,
                   'metadata': self.metadata('preset-validation-accept')})
        validation_terminal = self.job('preset-validation', validation)
        self.check(validation_terminal['input']['sourceProfile'] == profile,
                   'validation captures the preset static source profile')
        validation_ref = validation_terminal['validationResult']['report']
        _, validation_raw = self.binary('preset-validation-report',
            '/api/v1/jobs/' + validation_terminal['jobId'] + '/validation-result')
        self.check(len(validation_raw) == validation_ref['sizeBytes']
                   and hashlib.sha256(validation_raw).hexdigest() == validation_ref['sha256'],
                   'validation report has exact immutable hash and size')
        validation_report = json.loads(validation_raw)
        self.check(validation_report['succeeded'] and validation_report['opaqueFrames'] == 1
                   and validation_report['transparentFrames'] == 1,
                   'preset validation completes one opaque and one transparent Worker frame')
        validation_page = self.request('preset-validation-diagnostics', 'GET',
            '/api/v1/jobs/' + validation_terminal['jobId'] + '/validation-diagnostics?limit=1')
        self.check(validation_page['diagnostics'] == []
                   and validation_page['reportSha256'] == validation_ref['sha256'],
                   'preset validation diagnostics bind the complete report')

        exported = self.request('preset-export-accept', 'POST', self.project_path + '/export-jobs', status=202,
            value={'expectedRevision': str(revision), 'format': 'opaqueMp4', 'includeAudio': False,
                   'metadata': self.metadata('preset-export-accept')})
        export_terminal = self.job('preset-export', exported)
        self.check(export_terminal['input']['sourceProfile'] == profile,
                   'export captures the preset static source profile')
        export_ref = export_terminal['exportResult']
        report_ref = export_ref['report']
        _, export_raw = self.binary('preset-export-report',
            '/api/v1/jobs/' + export_terminal['jobId'] + '/export-result')
        self.check(len(export_raw) == report_ref['sizeBytes']
                   and hashlib.sha256(export_raw).hexdigest() == report_ref['sha256'],
                   'export report has exact immutable hash and size')
        export_report = json.loads(export_raw)
        self.check(export_report['strictlyVerified']
                   and export_report['expectedFrames'] == export_report['framesWritten']
                   and export_report['failedFrames'] == export_report['droppedFrames'] == 0
                   and not export_report['audioIncluded'],
                   'preset export completes strict pure-video frame accounting')
        _, media = self.binary('preset-export-content',
            '/api/v1/jobs/' + export_terminal['jobId'] + '/export-content')
        artifact = export_ref['artifact']
        self.check(len(media) == artifact['sizeBytes']
                   and hashlib.sha256(media).hexdigest() == artifact['sha256']
                   and artifact['contentType'] == 'video/mp4',
                   'preset export content has exact immutable media hash and type')
        media_path = self.output / 'preset-export.mp4'
        media_path.write_bytes(media)
        probe = subprocess.run(['/usr/bin/ffprobe', '-v', 'error', '-count_frames', '-show_streams', '-of', 'json',
                                str(media_path)], capture_output=True, check=True, timeout=20)
        streams = json.loads(probe.stdout)['streams']
        video = next(stream for stream in streams if stream['codec_type'] == 'video')
        self.check(int(video['nb_read_frames']) == export_report['expectedFrames']
                   and all(stream['codec_type'] != 'audio' for stream in streams),
                   'preset export media independently decodes to the reported video frame count')

    def command(self, name, revision, value):
        result = self.request(name, 'POST', self.project_path + '/commands', value=self.command_payload(name, revision, value))
        self.check(result['previousRevision'] == str(revision), name + ': previous revision')
        return result

    def command_payload(self, name, revision, value):
        return {'requestId': name, 'hostInstanceId': self.host, 'validUntilUtc': self.metadata(name)['validUntilUtc'],
                'expectedRevision': str(revision), 'command': value}

    def template_list(self, name, library='builtin'):
        return self.request(name, 'GET', '/api/v1/template-libraries/' + library + '/templates?limit=100')

    def create_personal(self, name='personal-create'):
        definition = fixtures.static_definition('HTTP个人预设 ' + name, 'line')
        current = self.template_list(name + '-before', 'personal')
        payload = {'expectedRevision': current['revision'], 'metadata': self.metadata(name),
                   'command': {'type': 'create', 'definition': definition}}
        result = self.request(name, 'POST', '/api/v1/template-libraries/personal/commands', value=payload)
        self.check(result['changed'] and result['libraryId'] == 'personal', 'personal template created')
        return result['templateId'], result['revision'], definition

    def save_and_reopen(self, revision):
        handle = self.request('save-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': 'preset-assignment-project'})
        saved = self.job('save', self.request('save', 'POST', self.project_path + '/save-jobs', status=202,
            value={'expectedRevision': str(revision), 'targetHandleId': handle['handleId']}))
        self.check(saved['savedProjectRevision'] == str(revision), 'saved assignment revision')
        self.job('close', self.request('close', 'POST', self.project_path + '/close', status=202,
            value={'expectedRevision': str(revision), 'policy': 'requireClean', 'activeJobs': 'wait'}))
        reopened = self.job('reopen', self.request('reopen', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId']}))['result']
        self.project = reopened['projectSessionId']; self.project_path = '/api/v1/project-sessions/' + self.project
        self.check(reopened['projectRevision'] == '0' and not reopened['isDirty'] and reopened['hasPath'],
                   'reopened assignment is clean with a fresh session revision')
        return reopened

    def snapshot_application_id(self, name, revision, relative_path='preset-assignment-snapshot'):
        handle = self.request(name + '-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': relative_path})
        saved = self.job(name + '-save', self.request(name + '-save', 'POST', self.project_path + '/save-jobs', status=202,
            value={'expectedRevision': str(revision), 'targetHandleId': handle['handleId']}))
        self.check(saved['savedProjectRevision'] == str(revision), name + ': snapshot save revision')
        project_file = self.output / 'profile' / 'projects' / relative_path / 'project.json'
        document = json.loads(project_file.read_text())
        items = document.get('presetApplications', {}).get('items', [])
        self.check(len(items) == 1 and isinstance(items[0].get('id'), str), name + ': persisted application identity')
        return items[0]['id']

    def run(self):
        self.start('assignment'); self.connect('assignment')
        self.create_project()
        builtin = self.template_list('builtin-list')
        self.check(builtin['libraryId'] == 'builtin' and builtin['templates'], 'builtin templates available')
        classic = next(item for item in builtin['templates'] if item['templateId'] == 'builtin:line-classic')
        detail = self.request('builtin-detail', 'GET', '/api/v1/template-libraries/builtin/templates/detail?' + urlencode({
            'templateId': classic['templateId'], 'expectedRevision': builtin['revision']}))
        self.check(detail['definitionAvailability'] == 'staticAvailable' and detail['definition'] is not None,
                   'builtin template exposes complete static definition')
        before = self.capture('before-apply', '0')
        personal_id, personal_revision, personal_definition = self.create_personal()
        personal_list = self.template_list('personal-after-create', 'personal')
        applied = self.command('apply-personal', 0, {'type': 'presetApplications.apply',
            'templateLibraryId': 'personal', 'templateId': personal_id,
            'templateRevision': personal_revision, 'targets': [{'kind': 'project'}]})
        self.check(applied['changed'] and applied['projectRevision'] == '1', 'apply commits one project revision')
        assigned = self.assert_preset_preview('after-apply', '1')
        self.check(assigned != before, 'preset assignment changes the public preview PNG')
        self.run_static_worker_acceptance(1)
        application_id = self.snapshot_application_id('assignment-snapshot', 1)
        undone = self.command('undo-apply', 1, {'type': 'history.undo'})
        self.check(undone['projectRevision'] == '2', 'undo increments project revision')
        self.assert_preset_preview('after-undo', '2', before)
        redone = self.command('redo-apply', 2, {'type': 'history.redo'})
        self.check(redone['projectRevision'] == '3', 'redo restores assignment')
        self.assert_preset_preview('after-redo', '3', assigned)
        removed_personal = self.command('remove-personal', 3, {'type': 'presetApplications.remove', 'applicationIds': [application_id]})
        self.check(removed_personal['changed'] and removed_personal['projectRevision'] == '4',
                   'removing the personal application commits one revision')
        personal_id, _, personal_definition = self.create_personal('personal-create-2')
        personal_list = self.template_list('personal-after-create-2', 'personal')
        applied_personal = self.command('apply-personal-2', 4, {'type': 'presetApplications.apply',
            'templateLibraryId': 'personal', 'templateId': personal_id, 'templateRevision': personal_list['revision'],
            'targets': [{'kind': 'project'}]})
        self.check(applied_personal['changed'] and applied_personal['projectRevision'] == '5',
                   'applying a personal template commits one revision')
        application_id = self.snapshot_application_id('personal-assignment-snapshot', 5, 'personal-assignment-snapshot')
        personal_file = self.output / 'profile' / 'projects' / 'personal-assignment-snapshot' / 'project.json'
        personal_before_library_update = json.loads(personal_file.read_text())
        replace_payload = {'expectedRevision': personal_list['revision'], 'metadata': self.metadata('personal-replace'),
            'command': {'type': 'replace', 'templateId': personal_id, 'definition': {**personal_definition, 'name': '改后的个人模板'}}}
        replaced = self.request('personal-replace', 'POST', '/api/v1/template-libraries/personal/commands', value=replace_payload)
        self.check(replaced['changed'], 'personal library replacement commits independently')
        persisted_before_update = json.loads(personal_file.read_text())
        self.check(persisted_before_update['presetApplications'] == personal_before_library_update['presetApplications'],
                   'library update does not mutate the captured project template snapshot')
        personal_after_replace = self.template_list('personal-after-replace', 'personal')
        updated = self.command('update-personal', 5, {'type': 'presetApplications.update', 'applicationId': application_id,
            'templateLibraryId': 'personal', 'templateId': personal_id, 'templateRevision': personal_after_replace['revision']})
        self.check(updated['changed'] and updated['projectRevision'] == '6', 'update preserves application identity and commits once')
        self.assert_preset_preview('after-update', '6')
        undone_update = self.command('undo-update', 6, {'type': 'history.undo'})
        self.check(undone_update['projectRevision'] == '7', 'undo update increments revision')
        history_before_noop = self.request('history-before-noop', 'GET', self.project_path + '/history')
        self.check(history_before_noop['canRedo'], 'undo exposes redo before no-op')
        noop = self.command('noop-enabled', 7, {'type': 'presetApplications.setEnabled', 'applicationId': application_id, 'enabled': True})
        self.check(not noop['changed'] and noop['projectRevision'] == '7', 'setEnabled no-op preserves revision')
        history_after_noop = self.request('history-after-noop', 'GET', self.project_path + '/history')
        self.check(history_after_noop['canRedo'], 'setEnabled no-op preserves redo')
        redone_update = self.command('redo-update', 7, {'type': 'history.redo'})
        self.check(redone_update['projectRevision'] == '8', 'redo update after no-op')
        self.command('disable', 8, {'type': 'presetApplications.setEnabled', 'applicationId': application_id, 'enabled': False})
        removed = self.command('remove', 9, {'type': 'presetApplications.remove', 'applicationIds': [application_id]})
        self.check(removed['changed'] and removed['projectRevision'] == '10', 'remove commits one revision')
        self.save_and_reopen(10)
        project_file = self.output / 'profile' / 'projects' / 'preset-assignment-project' / 'project.json'
        persisted = json.loads(project_file.read_text())
        self.check(persisted.get('presetApplications', {}).get('items') == [], 'remove persists empty assignment list')
        restored = self.capture('after-remove-reopen', '0')
        self.check(restored == before, 'removing the assignment restores the baseline preview pixels')
        persisted_after = json.loads(project_file.read_text())
        self.check(persisted_after.get('presetApplications', {}).get('items') == [], 'library update does not resurrect removed application')
        self.stop('assignment')
        self.check(self.worker_identities, 'actual Worker identities observed')
        files = {}
        for file in sorted(self.output.rglob('*')):
            if file.is_file():
                data = file.read_bytes(); relative = str(file.relative_to(self.output))
                self.check(not any(secret.encode() in data for secret in self.secrets), 'credential scan: ' + relative)
                files[relative] = hashlib.sha256(data).hexdigest()
        smoke.existing.save_json(self.output / 'evidence-manifest.json', {'files': files,
            'deploymentManifestSha256': hashlib.sha256((self.dist / 'deployment.json').read_bytes()).hexdigest()})
        smoke.existing.save_json(self.output / 'summary.json', {'status': 'passed', 'httpRequests': self.request_count,
            'assertions': len(self.assertions), 'assertionLabels': self.assertions, 'runs': self.runs,
            'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(self.worker_identities)],
            'previewHashes': self.preview_hashes, 'previewRevisions': self.preview_revisions,
            'scope': 'project preset assignment, undo/redo/no-op, template snapshot boundary, public static preset preview PNG, real validation/export preset profile and persistence; no browser or full B3/B4 claim'})
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args(); args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    probe = AssignmentProbe(args.output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('assignment-failure-cleanup')


if __name__ == '__main__':
    main()
