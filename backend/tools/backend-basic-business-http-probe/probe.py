#!/usr/bin/env python3
"""Finite public basic-business workflow using a matched self-contained Server and real Worker."""
import argparse
import hashlib
import importlib.util
import io
import json
import math
from pathlib import Path
import struct
import sys
import urllib.error
from urllib.parse import urlencode
import urllib.request
import wave
import zipfile

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('g20_acceptance', ROOT / 'tools/backend-g20g24-http-probe/probe.py')
g20 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g20)
save_json = g20.save_json


class BasicBusinessProbe(g20.OrchestrationProbe):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.terminals = []
        self.artifacts = {}

    def request(self, name, method, path, **kwargs):
        value = kwargs.get('value')
        if (method == 'POST' and isinstance(value, dict) and path != '/api/v1/clients'
                and not path.endswith('/commands') and not path.endswith('/orchestration/measurements')):
            kwargs['value'] = {**value, 'metadata': value.get('metadata', self.metadata('basic-' + str(self.request_count + 1) + '-' + name))}
        return super().request(name, method, path, **kwargs)

    def job(self, name, accepted, **kwargs):
        terminal = super().job(name, accepted, **kwargs)
        self.terminals.append({'name': name, 'jobId': terminal['jobId'], 'kind': terminal['kind'], 'state': terminal['state']})
        return terminal

    def fetch(self, name, path, *, method='GET', headers=None, status=200, expected=None, client=None, anonymous=False,
              require_error_body=True):
        """Keep a sanitized request/response pair for binary, HEAD, 304, and rejected content reads."""
        self.request_count += 1
        selected = client or self.client
        request_headers = {'X-Mako-Host-Instance-Id': self.host, 'X-Mako-Client-Id': selected,
                           'Authorization': 'Bearer ' + ('invalid' if anonymous else self.tokens[selected]), **(headers or {})}
        prefix = f'{self.request_count:03d}-{name}'
        save_json(self.output / (prefix + '.request.json'), {'method': method, 'path': path,
            'headers': {key: '<redacted>' if key.lower() == 'authorization' else value for key, value in request_headers.items()},
            'bodyBytes': 0, 'bodySha256': hashlib.sha256(b'').hexdigest()})
        request = urllib.request.Request(self.base_url + path, headers=request_headers, method=method)
        try:
            response = self.http.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            data, actual, result_headers = response.read(), response.status, dict(response.headers.items())
        body = None
        if actual != 206 and result_headers.get('Content-Type', '').startswith(('application/json', 'application/problem+json')) and data:
            body = self.redact(json.loads(data))
        save_json(self.output / (prefix + '.response.json'), {'status': actual, 'headers': result_headers,
            'json': body, 'bodyBytes': len(data), 'bodySha256': hashlib.sha256(data).hexdigest()})
        if status < 300 and method == 'GET':
            (self.output / (prefix + '.bin')).write_bytes(data)
        self.check(actual == status, name + f': HTTP{status}, actual{actual}')
        if expected is not None:
            self.check(data == expected, name + ': exact immutable bytes')
        if actual >= 400 and method != 'HEAD' and require_error_body:
            self.check(isinstance(body, dict) and 'code' in body, name + ': structured rejection')
        return result_headers, data, body

    def upload(self, name, filename, content_type, data):
        return self.request(name, 'POST', '/api/v1/assets?' + urlencode({'fileName': filename}), raw=data,
            content_type=content_type, status=201, headers={'X-Mako-Request-Id': name,
                'X-Mako-Valid-Until-Utc': self.metadata(name)['validUntilUtc']})

    @staticmethod
    def wav(seconds, sample_rate, frequency, amplitude):
        samples = [int(amplitude * math.sin(2 * math.pi * frequency * i / sample_rate)) for i in range(seconds * sample_rate)]
        output = io.BytesIO()
        with wave.open(output, 'wb') as stream:
            stream.setnchannels(1); stream.setsampwidth(2); stream.setframerate(sample_rate)
            stream.writeframes(struct.pack('<' + 'h' * len(samples), *samples))
        return output.getvalue(), samples

    def assert_waveform(self, name, samples, sample_rate, source_bytes, expected_id=None):
        model = self.request(name, 'GET', self.project_path + '/waveform')
        self.check(model['available'] and model['sampleRate'] == sample_rate and int(model['sampleCount']) == len(samples), name + ': complete prepared source dimensions')
        self.check(model['sourceSha256'] == hashlib.sha256(source_bytes).hexdigest(), name + ': exact source bytes identity')
        self.check(int(model['durationUs']) == len(samples) * 1000000 // sample_rate, name + ': actual audio duration')
        if expected_id is not None:
            self.check(model['waveformId'] == expected_id, name + ': same retained waveform identity')
        values = [(min(samples[i:i + model['baseBucketSamples']]) / 32768, max(samples[i:i + model['baseBucketSamples']]) / 32768)
                  for i in range(0, len(samples), model['baseBucketSamples'])]
        for level in model['levels']:
            self.check(level['bucketCount'] == len(values), name + ': complete level ' + str(level['level']) + ' bucket count')
            query = urlencode({'waveformId': model['waveformId'], 'level': level['level'], 'start': 0, 'count': len(values)})
            expected = b''.join(struct.pack('<ff', low, high) for low, high in values)
            self.fetch(name + '-level-' + str(level['level']), self.project_path + '/waveform/peaks?' + query, expected=expected)
            values = [(min(pair[0] for pair in values[i:i + 2]), max(pair[1] for pair in values[i:i + 2])) for i in range(0, len(values), 2)]
        return model

    def create(self):
        roots = self.request('authorized-roots', 'GET', '/api/v1/file-roots')
        self.check(roots['rootGrantIds'] == ['projects'], 'launcher grants exactly its profile project root')
        empty = self.request('empty-directory', 'GET', '/api/v1/file-roots/projects/entries?limit=1')
        self.check(empty['totalCount'] == 0 and not empty['entries'], 'empty authorized directory is navigable')
        for name, query in [('directory-duplicate', 'limit=1&limit=2'), ('directory-unknown', 'unknown=1')]:
            self.request(name, 'GET', '/api/v1/file-roots/projects/entries?' + query, status=400, code='invalidRequest')
        self.request('directory-traversal', 'GET', '/api/v1/file-roots/projects/entries?relativePath=..%2Foutside', status=400, code='pathNotAllowed')
        self.audio_a, self.samples_a = self.wav(8, 8000, 440, 12000)
        self.audio_b, self.samples_b = self.wav(3, 16000, 880, 24000)
        self.asset_a = self.upload('audio-a-upload', 'original.wav', 'audio/wav', self.audio_a)
        self.asset_b = self.upload('audio-b-upload', 'replacement.wav', 'audio/wav', self.audio_b)
        self.texts = ['FIRST', 'SECOND', 'THIRD', 'FOURTH']
        self.starts = [500000, 2000000, 4000000, 6000000]
        lyrics_bytes = b''.join(f'[{s // 60000000:02d}:{s // 1000000 % 60:02d}.{s // 10000 % 100:02d}]<{s // 60000000:02d}:{s // 1000000 % 60:02d}.{s // 10000 % 100:02d}>{t}\n'.encode() for s, t in zip(self.starts, self.texts))
        lyrics = self.upload('lyrics-upload', 'business.lrc', 'text/plain', lyrics_bytes)
        created = self.request('project-create', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': 'Basic business deployed HTTP', 'audioAssetId': self.asset_a['assetId'], 'lyricsAssetId': lyrics['assetId'],
            'importerId': 'lrc.enhanced', 'composition': {'canvasWidth': 320, 'canvasHeight': 180,
                'frameRateNumerator': 30, 'frameRateDenominator': 1, 'outputDurationUs': 8000000}})
        self.project, self.revision = created['projectSessionId'], created['projectRevision']
        lines = self.lyrics('created-lyrics')['lines']
        self.check([line['text'] for line in lines] == self.texts, 'real importer preserves all four deterministic lines')
        self.lines = [line['id'] for line in lines]
        for index, line in enumerate(lines):
            start = self.starts[index]; end = self.starts[index + 1] if index + 1 < len(lines) else 8000000
            self.send('exact-line-time-' + str(index), {'type': 'lyrics.setTimeRange', 'target': {'branchId': 'primary', 'lineId': line['id']}, 'startUs': str(start), 'endUs': str(end)})
            self.check(len(line['tokens']) == 1 and line['tokens'][0]['text'] == self.texts[index], 'enhanced input has one exact token per line')
            self.send('exact-token-time-' + str(index), {'type': 'lyrics.setTokenTimes', 'changes': [
                {'target': {'branchId': 'primary', 'lineId': line['id'], 'tokenId': line['tokens'][0]['id']}, 'startUs': str(start), 'endUs': str(end)}]})
        self.handle = self.request('project-directory-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': 'basic-business'})['handleId']

    def lyrics_exports(self):
        baseline = self.request('lrc-before-summary', 'GET', self.project_path)
        for format, enhanced in [('lrc.plain', False), ('lrc.enhanced', True)]:
            name = 'lrc-enhanced' if enhanced else 'lrc-plain'
            options = {'newLine': '\r\n' if enhanced else '\n', 'writeByteOrderMark': enhanced, 'millisecondPrecision': enhanced}
            payload = {'expectedRevision': self.revision, 'branchId': 'primary', 'format': format,
                'options': options, 'metadata': self.metadata(name + '-plan')}
            plan = self.request(name + '-plan', 'POST', self.project_path + '/lyrics-export-plans', status=201, value=payload)
            replay = self.request(name + '-plan-replay', 'POST', self.project_path + '/lyrics-export-plans', status=201, value=payload)
            self.check(plan == replay and plan['requiresLossAcceptance'] and plan['scope'] == 'branch', name + ': exact plan replay and explicit scope/loss assessment')
            self.request(name + '-loss-rejected', 'POST', self.project_path + '/lyrics-exports', status=422, code='commandRejected',
                value={'expectedRevision': self.revision, 'planId': plan['planId'], 'acceptLosses': False})
            exporting = {'expectedRevision': self.revision, 'planId': plan['planId'], 'acceptLosses': True, 'metadata': self.metadata(name + '-execute')}
            result = self.request(name + '-execute', 'POST', self.project_path + '/lyrics-exports', status=201, value=exporting)
            self.check(result == self.request(name + '-execute-replay', 'POST', self.project_path + '/lyrics-exports', status=201, value=exporting), name + ': one retained result identity on replay')
            precision = 3 if enhanced else 2
            def timestamp(start):
                return f'{start // 60000000:02d}:{start // 1000000 % 60:02d}.{start // (1000 if enhanced else 10000) % (10 ** precision):0{precision}d}'
            text = ''.join('[' + timestamp(start) + ']' + ('<' + timestamp(start) + '>' if enhanced else '') + line + options['newLine'] for start, line in zip(self.starts, self.texts))
            expected = (b'\xef\xbb\xbf' if enhanced else b'') + text.encode()
            headers, data, _ = self.fetch(name + '-content', result['contentUrl'], expected=expected)
            self.check(result['sizeBytes'] == len(expected) and result['sha256'] == hashlib.sha256(expected).hexdigest(), name + ': exact deterministic UTF8 byte oracle')
            self.check(result['report']['sourceLineCount'] == result['report']['exportedLineCount'] == 4 and result['report']['skippedLineCount'] == 0, name + ': complete Core report counters')
            self.fetch(name + '-head', result['contentUrl'], method='HEAD', expected=b'')
            self.fetch(name + '-range', result['contentUrl'], headers={'Range': 'bytes=1-7'}, status=206, expected=data[1:8])
            self.fetch(name + '-cached', result['contentUrl'], headers={'If-None-Match': headers['ETag']}, status=304, expected=b'')
            self.fetch(name + '-anonymous', result['contentUrl'], status=401, anonymous=True)
            other = self.register(name + '-other-client', select=False)
            self.secrets.extend((other['accessToken'], other['resumeToken']))
            self.fetch(name + '-foreign', result['contentUrl'], status=404, client=other['clientId'])
            self.artifacts[name] = {'sha256': result['sha256'], 'sizeBytes': result['sizeBytes'], 'planId': result['planId'], 'exportId': result['exportId']}
        self.check(baseline == self.request('lrc-after-summary', 'GET', self.project_path), 'LRC plan/export/read leaves project and history unchanged')
        old_plan = self.request('lrc-old-plan', 'POST', self.project_path + '/lyrics-export-plans', status=201,
            value={'expectedRevision': self.revision, 'branchId': 'primary', 'format': 'lrc.plain', 'options': {}})
        old_revision = self.revision
        self.send('project-rename', {'type': 'project.rename', 'name': '  Basic business renamed  '})
        for revision in [old_revision, self.revision]:
            self.request('lrc-stale-' + revision, 'POST', self.project_path + '/lyrics-exports', status=409, code='revisionConflict',
                value={'expectedRevision': revision, 'planId': old_plan['planId'], 'acceptLosses': True})

    def audio_history(self):
        original = self.assert_waveform('original-waveform', self.samples_a, 8000, self.audio_a)
        accepted = self.request('original-analysis', 'POST', self.project_path + '/analysis-jobs', status=202, value={'expectedRevision': self.revision})
        analysis = self.job('original-analysis', accepted)
        self.send('apply-original-analysis', {'type': 'audio.analysis.apply', 'analysisJobId': analysis['jobId']})
        summary_a = self.request('analyzed-summary', 'GET', self.project_path)
        self.check(summary_a['audioAnalysis']['contentHash'] == analysis['analysisResult']['contentHash'], 'actual Worker result explicitly applied')
        self.send('replace-audio', {'type': 'audio.replaceAsset', 'assetId': self.asset_b['assetId']})
        replacement = self.assert_waveform('replacement-waveform', self.samples_b, 16000, self.audio_b)
        summary_b = self.request('replacement-summary', 'GET', self.project_path)
        self.check(summary_b['audioAssetId'] == self.asset_b['assetId'] and summary_b.get('audioAnalysis') is None, 'replace uses new resource and clears same-source analysis')
        self.check(summary_b['audioDurationUs'] == 3000000, 'replacement uses actual Worker-prepared duration')
        self.send('undo-audio-replacement', {'type': 'history.undo'})
        restored = self.request('undo-audio-summary', 'GET', self.project_path)
        self.check(restored['audioAssetId'] == self.asset_a['assetId'] and restored['audioAnalysis'] == summary_a['audioAnalysis'], 'undo restores exact old resource and adopted analysis')
        self.assert_waveform('undo-original-waveform', self.samples_a, 8000, self.audio_a, original['waveformId'])
        noop = self.send('same-audio-noop', {'type': 'audio.replaceAsset', 'assetId': self.asset_a['assetId']})
        self.check(not noop['changed'] and noop['canRedo'], 'same-resource no-op preserves redo')
        self.send('redo-audio-replacement', {'type': 'history.redo'})
        self.assert_waveform('redo-replacement-waveform', self.samples_b, 16000, self.audio_b, replacement['waveformId'])
        self.send('clear-audio', {'type': 'audio.clear'})
        cleared = self.request('clear-waveform', 'GET', self.project_path + '/waveform')
        self.check(not cleared['available'] and cleared['reason'] == 'noAudio', 'clear reports explicit noAudio waveform state')
        self.check(not self.send('clear-audio-noop', {'type': 'audio.clear'})['changed'], 'clearing absent audio is a history no-op')
        self.send('undo-audio-clear', {'type': 'history.undo'})
        self.assert_waveform('clear-undo-waveform', self.samples_b, 16000, self.audio_b, replacement['waveformId'])
        settings = self.request('replacement-settings', 'GET', self.project_path + '/settings?expectedRevision=' + self.revision)
        self.check(settings['name'] == 'Basic business renamed' and settings['outputDurationUs'] == '8000000', 'rename trims name and audio edit preserves explicit output duration')
        resources = self.request('project-resources', 'GET', self.project_path + '/resources?' + urlencode({'expectedRevision': self.revision, 'limit': 1}))
        audio = next(item for item in resources['resources'] if 'project.audio' in item['roles'])
        self.check(audio['sha256'] == hashlib.sha256(self.audio_b).hexdigest() and audio['preparationStatus'] == 'prepared' and audio['waveformStatus'] == 'available', 'resource read reports actual prepared resource status')
        self.fetch('resource-current-audio', audio['contentUrl'], expected=self.audio_b)
        self.fetch('history-old-audio-lease', '/api/v1/assets/' + self.asset_a['assetId'] + '/content', expected=self.audio_a)
        self.save_close('audio-persistence')
        page = self.request('directory-after-save', 'GET', '/api/v1/file-roots/projects/entries?limit=1')
        self.check(page['totalCount'] == 1 and page['entries'][0] == {'name': 'basic-business', 'relativePath': 'basic-business', 'isProject': True}, 'directory identifies actual saved project without absolute path')
        reopened = self.open('audio-reopen')
        self.check(reopened['name'] == 'Basic business renamed' and reopened['audioDurationUs'] == 3000000 and not reopened['isDirty'], 'rename and actual replacement survive save/open')
        self.assert_waveform('reopened-waveform', self.samples_b, 16000, self.audio_b, replacement['waveformId'])
        self.send('public-clear-for-preview', {'type': 'audio.clear'})

    def templates(self):
        libraries = self.request('template-libraries', 'GET', '/api/v1/template-libraries')
        self.check(any(item['libraryId'] == 'personal' and not item['readOnly'] for item in libraries['libraries']), 'personal template destination available')
        builtin = self.request('builtin-templates', 'GET', '/api/v1/template-libraries/builtin/templates?limit=100')
        selected = next(item for item in builtin['templates'] if item['templateId'] == 'builtin:line-classic')
        exported = self.request('builtin-archive-export', 'POST', '/api/v1/template-libraries/builtin/exports', status=201,
            value={'expectedRevision': builtin['revision'], 'templateIds': [selected['templateId']]})
        headers, archive, _ = self.fetch('builtin-archive', exported['contentUrl'])
        self.check(hashlib.sha256(archive).hexdigest() == exported['sha256'] and len(archive) == exported['sizeBytes'], 'full fixed template ZIP SHA and size')
        with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
            self.check(zipped.testzip() is None and 'index.json' in zipped.namelist(), 'complete template ZIP CRC and index')
            original_index = json.loads(zipped.read('index.json'))
        asset = self.upload('template-archive-upload', 'shared.mktpl', 'application/octet-stream', archive)
        preflight_input = {'assetId': asset['assetId'], 'metadata': self.metadata('template-preflight')}
        preflight = self.request('template-preflight', 'POST', '/api/v1/template-libraries/personal/imports/preflight', status=201, value=preflight_input)
        self.check(preflight == self.request('template-preflight-replay', 'POST', '/api/v1/template-libraries/personal/imports/preflight', status=201, value=preflight_input), 'preflight replay fixes parsed identity and complete report')
        personal = self.request('personal-before-import', 'GET', '/api/v1/template-libraries/personal/templates')
        self.check(preflight['assetSha256'] == exported['sha256'] and len(preflight['templates']) == 1, 'preflight refers to exact uploaded archive')
        self.request('template-import-stale', 'POST', '/api/v1/template-libraries/personal/imports', status=409, code='revisionConflict',
            value={'expectedRevision': '0' * 64, 'preflightId': preflight['preflightId']})
        importing = {'expectedRevision': personal['revision'], 'preflightId': preflight['preflightId'], 'metadata': self.metadata('template-import')}
        imported = self.request('template-import', 'POST', '/api/v1/template-libraries/personal/imports', value=importing)
        self.check(imported == self.request('template-import-replay', 'POST', '/api/v1/template-libraries/personal/imports', value=importing), 'one CAS commit and stable generated IDs on import replay')
        imported_id = imported['importedTemplates'][0]['templateId']
        self.check(imported_id != selected['templateId'] and imported['changed'], 'import generates isolated destination identity')
        self.request('template-import-consumed', 'POST', '/api/v1/template-libraries/personal/imports', status=404, code='resourceNotFound',
            value={'expectedRevision': imported['revision'], 'preflightId': preflight['preflightId']})
        reexport = self.request('personal-archive-export', 'POST', '/api/v1/template-libraries/personal/exports', status=201,
            value={'expectedRevision': imported['revision'], 'templateIds': [imported_id]})
        reheaders, rebytes, _ = self.fetch('personal-archive', reexport['contentUrl'])
        self.fetch('personal-archive-head', reexport['contentUrl'], method='HEAD', expected=b'')
        self.fetch('personal-archive-range', reexport['contentUrl'], headers={'Range': 'bytes=0-15'}, status=206, expected=rebytes[:16])
        self.fetch('personal-archive-etag', reexport['contentUrl'], headers={'If-None-Match': reheaders['ETag']}, status=304, expected=b'')
        with zipfile.ZipFile(io.BytesIO(rebytes)) as zipped:
            self.check(zipped.testzip() is None, 'reexported complete archive CRC')
            new_index = json.loads(zipped.read('index.json'))
        def remove_ids(value):
            if isinstance(value, dict): return {key: remove_ids(item) for key, item in value.items() if key != 'id'}
            if isinstance(value, list): return [remove_ids(item) for item in value]
            return value
        self.check(remove_ids(original_index) == remove_ids(new_index), 'complete template definition roundtrip equal apart from generated identity')
        self.personal_template = (imported_id, imported['revision'])
        self.artifacts['templateArchive'] = {'originalSha256': exported['sha256'], 'reexportSha256': reexport['sha256'], 'importedTemplateId': imported_id}

    def clips_and_style(self):
        tree = self.orchestration('clip-initial-tree')
        program = next(item for item in tree['programs'] if item['branchId'] == 'primary')
        clip = 'clip/basic-business'; right = 'clip/basic-business-right'
        self.send('create-clip', {'type': 'orchestration.createClip', 'branchId': 'primary', 'clipId': clip, 'name': 'Business clip', 'lineIds': self.lines[1:3], 'containerId': program['defaultContainerId']})
        self.send('expand-clip', {'type': 'orchestration.setClipMembers', 'branchId': 'primary', 'clipId': clip, 'lineIds': self.lines})
        self.send('shrink-clip', {'type': 'orchestration.setClipMembers', 'branchId': 'primary', 'clipId': clip, 'lineIds': self.lines[:3]})
        self.send('restore-clip-members', {'type': 'orchestration.setClipMembers', 'branchId': 'primary', 'clipId': clip, 'lineIds': self.lines})
        template_id, template_revision = self.personal_template
        self.send('apply-static-line-template', {'type': 'presetApplications.apply', 'templateLibraryId': 'personal',
            'templateId': template_id, 'templateRevision': template_revision, 'targets': [{'kind': 'line', 'branchId': 'primary', 'lineId': self.lines[0]}]})
        applications = self.request('applications-before-split', 'GET', self.project_path + '/preset-applications?expectedRevision=' + self.revision)
        before_split = self.measure('before-split-real-frame', 'time', '1000000')
        split = self.send('split-clip', {'type': 'orchestration.splitClip', 'branchId': 'primary', 'clipId': clip, 'splitBeforeLineId': self.lines[2], 'newClipId': right})
        self.check(len(split['clipMembersResult']['clips']) == 2 and all(item.get('scheduleContext') for item in split['clipMembersResult']['clips']), 'split returns complete preserved schedule contexts')
        query = urlencode({'expectedRevision': self.revision, 'branchId': 'primary', 'lineId': self.lines[2], 'clipId': right})
        style = self.request('resolved-static-clip-style', 'GET', self.project_path + '/resolved-style?' + query)
        self.check(style['context']['clipId'] == right and len(style['sources']) == 21 and style['resolution'] == 'staticPresetHierarchy' and not style['finalPixelAppearance'], 'resolved style reports true static context and full property provenance')
        after_split = self.measure('after-split-real-frame', 'time', '1000000')
        self.check(before_split['pngSha256'] == after_split['pngSha256'], 'clip split preserves actual Worker rendered PNG at same time')
        self.send('merge-clips', {'type': 'orchestration.mergeClips', 'branchId': 'primary', 'clipIds': [clip, right], 'targetClipId': clip})
        after_applications = self.request('applications-after-merge', 'GET', self.project_path + '/preset-applications?expectedRevision=' + self.revision)
        self.check({key: value for key, value in applications.items() if key != 'projectRevision'} == {key: value for key, value in after_applications.items() if key != 'projectRevision'}, 'split/merge preserves unrelated public line preset references and snapshots')
        merged = self.orchestration('merged-tree')
        clips = next(item for item in merged['programs'] if item['branchId'] == 'primary')['clips']
        self.check(len(clips) == 1 and clips[0]['id'] == clip and clips[0]['lineIds'] == self.lines, 'merge retains chosen stable clip identity and all original members')
        for name, cmd, field, failure in [
            ('r8-missing-branch', {'branchId': 'missing-branch', 'leadInId': 'any', 'containerId': 'any'}, 'command.branchId', 'TargetStale'),
            ('r8-missing-cue', {'branchId': 'primary', 'leadInId': 'any', 'containerId': 'any'}, 'command.leadInId', 'TargetStale')]:
            baseline = self.request(name + '-before', 'GET', self.project_path + '/history')
            payload = self.command(self.revision, name, {'type': 'orchestration.setLeadInAttachment', **cmd, 'mode': 'FixedParent'})
            rejected = self.request(name, 'POST', self.project_path + '/commands', status=422, code='commandRejected', value=payload)
            self.check(rejected['field'] == field and rejected['failureCode'] == failure, name + ': precise stable wire')
            self.check(rejected == self.request(name + '-replay', 'POST', self.project_path + '/commands', status=422, code='commandRejected', value=payload), name + ': exact rejection replay')
            self.check(baseline == self.request(name + '-after', 'GET', self.project_path + '/history'), name + ': no revision/history mutation')
        countdown = self.send('r8-countdown', {'type': 'orchestration.createCountdown', 'parentId': program['rootContainerId'], 'name': 'R8 marker', 'count': 1})
        indicator = countdown['leadInResult']['indicatorContainerId']
        self.send('r8-existing-cue', {'type': 'orchestration.createLeadIn', 'branchId': 'primary', 'leadInId': 'cue/basic-r8', 'name': None,
            'targetLineId': self.lines[0], 'indicatorContainerId': indicator, 'durationUs': '100000', 'enabled': False})
        baseline = self.request('r8-container-before', 'GET', self.project_path + '/history')
        rejected = self.request('r8-non-countdown-container', 'POST', self.project_path + '/commands', status=422, code='commandRejected',
            value=self.command(self.revision, 'r8-non-countdown-container', {'type': 'orchestration.setLeadInAttachment', 'branchId': 'primary',
                'leadInId': 'cue/basic-r8', 'containerId': program['defaultContainerId'], 'mode': 'FixedParent'}))
        self.check(rejected['field'] == 'command.containerId' and rejected['failureCode'] == 'ContainerInvalid', 'R8 existing cue with non-countdown container has precise rejection')
        self.check(baseline == self.request('r8-container-after', 'GET', self.project_path + '/history'), 'R8 invalid container changes no history')
        self.measure('after-r8-errors-real-frame', 'time', '1000000')
        self.preview(self.project, self.revision)
        before_save = self.orchestration('before-final-save')
        lyric_snapshot = self.lyrics('before-final-save-lyrics')['lines']
        self.save_close('final-business')
        reopened = self.open('final-business-reopen')
        self.check(not reopened.get('audioAssetId') and reopened['name'] == 'Basic business renamed', 'public clear and rename survive full saved workflow')
        restored = self.orchestration('final-restored-orchestration')
        self.check(before_save['nodes'] == restored['nodes'] and before_save['programs'] == restored['programs'], 'all clip identities and scheduling contexts persist exactly')
        self.check(lyric_snapshot == self.lyrics('final-restored-lyrics')['lines'], 'lyrics and exact timing persist')
        self.save_close('final-restored')

    def run(self):
        deployment = json.loads((self.dist / 'deployment.json').read_text())
        self.start('deployed'); self.connect('deployed')
        self.check(self.current_ready['version'] == deployment['version'], 'launcher validates and reports matched deployment version')
        cap = self.request('business-capabilities', 'GET', '/api/v1/capabilities')
        for feature in ['file-roots.entries', 'project-sessions.lyrics-export-plans', 'project-sessions.lyrics-exports',
                        'project-sessions.commands.audio.replaceAsset', 'project-sessions.commands.audio.clear', 'project-sessions.commands.project.rename',
                        'project-sessions.resources.read', 'template-libraries.imports', 'template-libraries.exports',
                        'project-sessions.resolved-style.read', 'project-sessions.commands.orchestration.setClipMembers']:
            self.check(feature in cap['features'], 'advertised capability ' + feature)
        self.create(); self.lyrics_exports(); self.audio_history(); self.templates(); self.clips_and_style()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    probe = BasicBusinessProbe(output, args.dist.resolve()); failure = None
    try:
        probe.run()
    except BaseException as error:
        failure = error
        # Exception messages from the HTTP harness contain sanitized response details only.
        save_json(output / 'failure.json', {'errorType': type(error).__name__, 'detail': str(error)})
    finally:
        try:
            probe.stop('deployed')
            if probe.current_ready is not None:
                probe.check(bool(probe.worker_identities), 'actual Worker process identities observed and fully drained')
        except BaseException as error:
            failure = failure or error
    try:
        scanned = probe.scan_credentials()
    except BaseException as error:
        failure = failure or error; scanned = None
    summary = {'status': 'failed' if failure else 'passed', 'httpRequests': probe.request_count, 'assertions': len(probe.assertions),
        'credentialScannedFiles': scanned, 'errorType': type(failure).__name__ if failure else None,
        'deploymentManifestSha256': hashlib.sha256((probe.dist / 'deployment.json').read_bytes()).hexdigest() if (probe.dist / 'deployment.json').is_file() else None,
        'runs': probe.runs, 'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(probe.worker_identities)],
        'jobTerminals': probe.terminals, 'artifacts': probe.artifacts, 'measurements': probe.measurements, 'assertionLabels': probe.assertions,
        'scope': 'Public matched Server/Worker directory, LRC, managed audio/history/analysis/waveform/persistence, static template archive roundtrip, static style/clip/PNG and R8 error chain. No fixture injection, browser acceptance, advanced template execution or whole-platform claim.'}
    save_json(output / 'summary.json', summary)
    save_json(output / 'evidence-manifest.json', {'files': {str(path.relative_to(output)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(output.rglob('*')) if path.is_file()}})
    print(json.dumps({key: summary[key] for key in ('status', 'httpRequests', 'assertions', 'errorType')}))
    return 1 if failure else 0


if __name__ == '__main__':
    sys.exit(main())
