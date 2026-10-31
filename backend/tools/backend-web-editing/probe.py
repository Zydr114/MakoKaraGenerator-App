#!/usr/bin/env python3
"""Real HTTP acceptance for Web editing; owns its profile and never persists credentials."""

import argparse
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import shutil
import sys
import wave
from urllib.parse import urlencode

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
spec = importlib.util.spec_from_file_location('web_backend_smoke', HERE.parent / 'backend-web-host' / 'smoke.py')
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
save_json = smoke.existing.save_json


class EditingProbe(smoke.Smoke):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.worker_identities = set()
        self.fixtures = []

    def observe(self, pid):
        super().observe(pid)
        # Observe only executable basenames, never process arguments/environment.
        for identity in list(self.observed):
            try:
                if Path(f'/proc/{identity[0]}/exe').resolve().name == 'MakoKaraGenerator.Backend.Worker':
                    self.worker_identities.add(identity)
            except OSError:
                pass

    def get(self, name, path, endpoint, revision, **query):
        result = self.request(name, 'GET', path + endpoint + '?' + urlencode({'expectedRevision': revision, **query}))
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1], name + ': exact session identity')
        self.check(result['projectRevision'] == revision and isinstance(revision, str), name + ': exact string revision')
        if endpoint.endswith('window'):
            self.check(result['branchId'] == query['branchId'], name + ': exact branch identity')
        return result

    def content(self, name, path, revision):
        branches = self.get(name + '-branches', path, '/lyrics/branches', revision)
        settings = self.get(name + '-settings', path, '/settings', revision)
        windows = {b['id']: self.get(name + '-' + b['id'], path, '/lyrics/window', revision,
                                    branchId=b['id']) for b in branches['branches']}
        return branches, settings, windows

    def check_page(self, page, key, offset, total, following):
        self.check(page['offset'] == offset and page['totalCount'] == total, key + ': exact offset/count')
        self.check(page.get('nextOffset') == following, key + ': nextOffset or omitted end')
        self.check(isinstance(page['projectRevision'], str), key + ': revision is string')
        if following is None:
            self.check('nextOffset' not in page, key + ': terminal nextOffset omitted')

    def invalid_queries(self, path, revision, branch, total):
        endpoints = ('/lyrics/branches', '/lyrics/window', '/settings')
        for endpoint in endpoints:
            prefix = endpoint.replace('/', '-')
            suffix = '&branchId=' + branch if endpoint.endswith('window') else ''
            invalid = [('', 'missing'), ('expectedRevision=', 'empty'),
                       (f'expectedRevision={revision}&expectedRevision={revision}', 'duplicate'),
                       ('expectedRevision=-1', 'negative'), ('expectedRevision=%2B0', 'plus'),
                       ('expectedRevision=%200', 'whitespace'), ('expectedRevision=1.0', 'fraction'),
                       ('expectedRevision=9223372036854775808', 'overflow')]
            for query, label in invalid:
                self.request(prefix + '-' + label, 'GET', path + endpoint + '?' + query + suffix,
                             status=400, code='invalidRequest')
        for endpoint, count in (('/lyrics/branches', 1), ('/lyrics/window', total)):
            base = path + endpoint + '?' + urlencode({'expectedRevision': revision})
            if endpoint.endswith('window'):
                base += '&' + urlencode({'branchId': branch})
            invalid = [('offset=-1', 'negative-offset'), ('offset=1.0', 'fraction-offset'),
                       ('offset=2147483648', 'overflow-offset'), (f'offset={count + 1}', 'past-end'),
                       ('offset=0&offset=0', 'duplicate-offset'), ('limit=0', 'zero-limit'),
                       ('limit=101', 'large-limit'), ('limit=-1', 'negative-limit'),
                       ('limit=1&limit=1', 'duplicate-limit'), ('limit=', 'empty-limit')]
            for query, label in invalid:
                self.request(endpoint.replace('/', '-') + '-' + label, 'GET', base + '&' + query,
                             status=400, code='invalidRequest')
        for query, label in ((f'expectedRevision={revision}', 'missing-branch'),
                             (f'expectedRevision={revision}&branchId=', 'empty-branch'),
                             (f'expectedRevision={revision}&branchId={branch}&branchId={branch}', 'duplicate-branch')):
            self.request(label, 'GET', path + '/lyrics/window?' + query, status=400, code='invalidRequest')
        self.request('unknown-branch', 'GET', path + '/lyrics/window?' + urlencode({
            'expectedRevision': revision, 'branchId': 'unknown'}), status=404, code='commandRejected')

    def authorization(self, path, revision, branch):
        other = self.register('other-client', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        for endpoint in ('/lyrics/branches', '/lyrics/window', '/settings'):
            query = {'expectedRevision': revision}
            if endpoint.endswith('window'):
                query['branchId'] = branch
            suffix = endpoint + '?' + urlencode(query)
            self.request('cross-client' + endpoint.replace('/', '-'), 'GET', path + suffix,
                         client=other['clientId'], status=404, code='sessionNotFound')
            self.request('missing-session' + endpoint.replace('/', '-'), 'GET',
                         '/api/v1/project-sessions/missing' + suffix, status=404, code='sessionNotFound')

    def close(self, name, path, revision):
        terminal = self.job(name, self.request(name, 'POST', path + '/close', status=202,
            value={'expectedRevision': revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))
        self.request(name + '-session-gone', 'GET', path, status=404, code='sessionNotFound')
        return terminal

    def open(self, name, relative):
        handle = self.request(name + '-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': relative})
        opened = self.job(name, self.request(name, 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId']}))['result']
        self.check(opened['hasPath'] and not opened['isDirty'], name + ': actual clean persisted session')
        return '/api/v1/project-sessions/' + opened['projectSessionId'], opened

    @staticmethod
    def normalize_content(models):
        branches, settings, windows = copy.deepcopy(models)
        for value in (branches, settings, *windows.values()):
            value.pop('projectSessionId', None)
            value.pop('projectRevision', None)
        return branches, settings, windows

    def save_close_open(self, name, path, revision, relative):
        expected = self.content(name + '-before', path, revision)
        handle = self.request(name + '-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': relative})
        saved = self.job(name + '-save', self.request(name + '-save', 'POST', path + '/save-jobs', status=202,
            value={'expectedRevision': revision, 'targetHandleId': handle['handleId']}))
        self.check(saved['savedProjectRevision'] == revision, name + ': save exact terminal revision')
        self.close(name + '-close', path, revision)
        new_path, opened = self.open(name + '-open', relative)
        self.check(new_path != path, name + ': reopened session has new identity')
        actual = self.content(name + '-after', new_path, opened['projectRevision'])
        self.check(self.normalize_content(actual) == self.normalize_content(expected),
                   name + ': complete branches/settings/lyrics values and IDs survive')
        for branch in actual[0]['branches']:
            self.check(branch['id'] in actual[2], name + ': source list discovers ' + branch['id'])
        return new_path, opened

    def fixture(self, name, modify):
        """Clone only the probe's closed template; write a new hash-addressed lyrics revision."""
        root = self.output / 'profile' / 'projects'
        destination = root / name
        self.check(not destination.exists(), name + ': fixture destination unused')
        shutil.copytree(root / 'editing-original', destination)
        manifest = destination / 'project.json'
        project = json.loads(manifest.read_text(encoding='utf-8'))
        original_reference = project['lyricsFile']
        lyrics = json.loads((destination / original_reference).read_text(encoding='utf-8'))
        modify(project, lyrics)
        data = (json.dumps(lyrics, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
        digest = hashlib.sha256(data).hexdigest()
        reference = 'data/lyrics/' + digest + '.json'
        (destination / reference).write_bytes(data)
        project['lyricsFile'] = reference
        save_json(manifest, project)
        entry = {'name': name, 'relativeDirectory': name, 'lyricsFile': reference, 'lyricsSha256': digest,
                 'projectSha256': hashlib.sha256(manifest.read_bytes()).hexdigest(),
                 'schemaVersion': project['schemaVersion'], 'lyricsSchemaVersion': lyrics['schemaVersion'],
                 'applicationVersion': project.get('applicationVersion'), 'originalLyricsFile': original_reference,
                 'scope': 'offline fixture built from probe-owned closed save; not HTTP source/ruby editing'}
        self.fixtures.append(entry)
        save_json(self.output / 'fixtures.json', self.fixtures)
        return name

    def rich_fixture(self, project, lyrics):
        statuses = ('Missing', 'Imported', 'Estimated', 'Edited', 'Confirmed', 'Invalid')
        lines = []
        for index, status in enumerate(statuses):
            line = {'id': 'rich-line-' + str(index), 'text': '行の原文 🎵' if index == 0 else status,
                    'timingState': status, 'tokens': [{'id': 'rich-token-' + str(index),
                    'text': '基', 'timingState': status}]}
            if status != 'Missing':
                line.update(startUs=9 if status == 'Invalid' else 0,
                            endUs=8 if status == 'Invalid' else 9007199254740993)
                line['tokens'][0].update(startUs=line['startUs'], endUs=line['endUs'])
            lines.append(line)
        lines[0]['rubyAnnotations'] = [{'id': 'rich-ruby', 'baseTokenId': 'rich-token-0',
            'text': 'き\u3099', 'language': 'ja-JP', 'relativeScale': .75, 'gapPx': 4.25, 'alignment': 'End'}]
        lyrics['primaryBranchId'] = 'primary'
        lyrics['branches'] = [
            {'id': 'primary', 'name': '主歌词', 'role': 'Primary', 'language': 'ja-JP',
             'timingMode': 'Independent', 'lines': lines},
            {'id': 'translation', 'name': '翻译', 'role': 'Translation', 'language': 'zh-CN',
             'timingMode': 'FollowPrimary', 'lines': [{'id': 'translated', 'text': '译文',
              'primaryLineId': 'rich-line-1', 'timingState': 'Missing', 'tokens': []}]},
            {'id': 'annotation', 'name': '读音', 'role': 'Annotation', 'timingMode': 'None', 'lines': []}]
        project['composition'].update(canvasWidth=641, canvasHeight=359,
            frameRate={'numerator': 30000, 'denominator': 1001}, backgroundColor='#01020304',
            outputDurationUs=9007199254740993)

    def check_rich(self, path, revision):
        branches, settings, windows = self.content('rich', path, revision)
        self.check(branches['primaryBranchId'] == 'primary' and branches['totalCount'] == 3,
                   'rich: discover all three sources and actual primary')
        self.check(branches['branches'] == [
            {'id': 'primary', 'name': '主歌词', 'role': 'primary', 'language': 'ja-JP', 'timingMode': 'independent', 'lineCount': 6},
            {'id': 'translation', 'name': '翻译', 'role': 'translation', 'language': 'zh-CN', 'timingMode': 'followPrimary', 'lineCount': 1},
            {'id': 'annotation', 'name': '读音', 'role': 'annotation', 'timingMode': 'none', 'lineCount': 0}],
                   'rich: all source metadata exact')
        lines = windows['primary']['lines']
        for index, line in enumerate(lines):
            self.check(line['id'] == 'rich-line-' + str(index) and line['branchId'] == 'primary' and
                       line['tokens'][0]['id'] == 'rich-token-' + str(index) and line['tokens'][0]['text'] == '基',
                       'rich: every stable line/token identity and token original text exact')
            self.check('primaryLineId' not in line, 'rich: independent line omits absent association')
            if index:
                self.check(line['text'] == ('Imported', 'Estimated', 'Edited', 'Confirmed', 'Invalid')[index - 1] and
                           line['rubyAnnotations'] == [], 'rich: other original text and empty ruby list exact')
        self.check([x['timingStatus'] for x in lines] == ['missing', 'imported', 'estimated', 'edited', 'confirmed', 'invalid'],
                   'rich: six original line time states')
        self.check([x['tokens'][0]['timingStatus'] for x in lines] == [x['timingStatus'] for x in lines],
                   'rich: six original token time states')
        self.check(lines[0]['text'] == '行の原文 🎵' and lines[0]['tokens'][0]['text'] == '基',
                   'rich: authoritative line text differs from concatenated tokens')
        self.check(lines[0]['time'] == {} and lines[0]['tokens'][0]['time'] == {}, 'rich: missing is empty time object')
        for line in lines[1:5]:
            self.check(line['time'] == {'startUs': '0', 'endUs': '9007199254740993'} and
                       line['tokens'][0]['time'] == line['time'], 'rich: zero and beyond-JS-safe time strings')
        self.check(lines[5]['time'] == {'startUs': '9', 'endUs': '8'} and lines[5]['tokens'][0]['time'] == lines[5]['time'],
                   'rich: invalid original time remains visible')
        self.check(lines[0]['rubyAnnotations'] == [{'id': 'rich-ruby', 'baseTokenId': 'rich-token-0',
            'text': 'き\u3099', 'language': 'ja-JP', 'relativeScale': .75, 'gapPx': 4.25, 'alignment': 'end'}],
                   'rich: all ruby metadata exact including combining Unicode')
        self.check('time' not in lines[0]['rubyAnnotations'][0], 'rich: ruby has no independent time')
        translated = windows['translation']['lines'][0]
        self.check(translated == {'branchId': 'translation', 'id': 'translated', 'text': '译文',
                   'primaryLineId': 'rich-line-1', 'time': {}, 'timingStatus': 'missing', 'tokens': [], 'rubyAnnotations': []},
                   'rich: FollowPrimary keeps association and unsynthesized missing original time')
        self.check(windows['annotation']['lines'] == [], 'rich: empty annotation source remains discoverable')
        self.check([settings[k] for k in ('canvasWidth', 'canvasHeight', 'frameRateNumerator', 'frameRateDenominator',
            'backgroundRed', 'backgroundGreen', 'backgroundBlue', 'backgroundAlpha')] == [641, 359, 30000, 1001, 1, 2, 3, 4],
            'rich: complete rational scalar settings exact')
        self.check(settings['outputDurationUs'] == '9007199254740993' and settings['audioDurationUs'] == '2000000',
                   'rich: scalar microseconds are exact strings')
        for offset in range(4):
            page = self.get('rich-branch-page-' + str(offset), path, '/lyrics/branches', revision, offset=offset, limit=1)
            self.check_page(page, 'rich branches', offset, 3, offset + 1 if offset < 2 else None)
            self.check(page['branches'] == branches['branches'][offset:offset + 1], 'rich: ordered branch page exact')

    def budget_fixture(self, project, lyrics):
        def branch(identifier, lines):
            return {'id': identifier, 'name': identifier, 'role': 'Primary' if identifier == 'primary' else 'Custom',
                    'timingMode': 'Independent', 'lines': lines}
        def line(identifier, text, tokens=None):
            return {'id': identifier, 'text': text, 'timingState': 'Missing', 'tokens': tokens or []}
        # Valid sequential times/text avoid 20000 unrelated MissingTime diagnostics
        # overflowing the existing persistence-job ledger before S1 can read the fixture.
        item_line = {'id': 'too-many', 'text': 'a' * 20000, 'timingState': 'Imported',
                     'startUs': 0, 'endUs': 20000, 'tokens': [
                         {'id': 't' + str(i), 'text': 'a', 'timingState': 'Imported',
                          'startUs': i, 'endUs': i + 1} for i in range(20000)]}
        lyrics['branches'] = [branch('primary', [line('large-a', 'a' * 600000), line('large-b', 'b' * 600000)]),
            branch('single', [line('too-large', '汉' * 180000)]),
            branch('items', [item_line])]

    def check_budgets(self, path, revision):
        summary = self.request('budget-before-summary', 'GET', path)
        history = self.request('budget-before-history', 'GET', path + '/history')
        state_path = '/api/v1/clients/' + self.client + '/state'
        client_state = self.request('budget-before-client-state', 'GET', state_path)
        for branch in ('primary', 'single', 'items'):
            self.request('budget-reject-' + branch, 'GET', path + '/lyrics/window?' + urlencode({
                'expectedRevision': revision, 'branchId': branch}), status=413, code='resourceTooLarge')
        self.request('budget-single-no-truncation', 'GET', path + '/lyrics/window?' + urlencode({
            'expectedRevision': revision, 'branchId': 'single', 'limit': 1}), status=413, code='resourceTooLarge')
        for offset, character in ((0, 'a'), (1, 'b')):
            page = self.get('budget-retry-' + str(offset), path, '/lyrics/window', revision,
                            branchId='primary', offset=offset, limit=1)
            self.check(page['lines'][0]['text'] == character * 600000 and len(page['lines']) == 1,
                       'budget: smaller window returns whole line without truncation')
        self.check(self.request('budget-after-summary', 'GET', path) == summary,
                   'budget: 413 and retries preserve exact summary/dirty/revision')
        self.check(self.request('budget-after-history', 'GET', path + '/history') == history,
                   'budget: 413 and retries preserve exact history')
        self.check(self.request('budget-after-client-state', 'GET', state_path)['eventCursor'] == client_state['eventCursor'],
                   'budget: 413 and retries preserve exact event cursor')

    def run(self):
        self.start('editing')
        self.connect('editing')
        capabilities = self.request('editing-capabilities', 'GET', '/api/v1/capabilities')
        encoded_capabilities = json.dumps(capabilities)
        for capability in ('project-sessions.lyrics.branches.read', 'project-sessions.lyrics.window.read', 'project-sessions.settings.read'):
            self.check(capability in encoded_capabilities, 'advertised ' + capability)
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000)
            wav.writeframes(b'\x00\x00' * 16000)
        lyrics = ('[00:00.00]<00:00.00>空 <00:00.50>の🎵\n[00:01.00]Second line\n').encode('utf-8')
        assets = []
        for name, data, media in (('editing.wav', buf.getvalue(), 'audio/wav'), ('editing.lrc', lyrics, 'text/plain')):
            assets.append(self.request('upload-' + name, 'POST', '/api/v1/assets?fileName=' + name, raw=data,
                content_type=media, status=201, headers={'X-Mako-Request-Id': 'upload-' + name,
                'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']}))
        created = self.request('create', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': 'S1 HTTP editing probe', 'audioAssetId': assets[0]['assetId'], 'lyricsAssetId': assets[1]['assetId'],
            'composition': {'canvasWidth': 320, 'canvasHeight': 180, 'frameRateNumerator': 30,
                'frameRateDenominator': 1, 'backgroundRed': 0, 'backgroundGreen': 0, 'backgroundBlue': 0, 'backgroundAlpha': 255}})
        path = '/api/v1/project-sessions/' + created['projectSessionId']
        revision = created['projectRevision']
        summary = self.request('before-summary', 'GET', path)
        history = self.request('before-history', 'GET', path + '/history')
        client_state = self.request('before-client-state', 'GET', '/api/v1/clients/' + self.client + '/state')
        branches, settings, windows = self.content('original', path, revision)
        branch = branches['primaryBranchId']
        full = windows[branch]
        self.check_page(full, 'original window', 0, 2, None)
        self.check(settings['audioDurationUs'] == '2000000' and 'outputDurationUs' not in settings,
                   'real Worker prepared duration string and omitted unspecified output duration')
        self.check(full['lines'][0]['time']['startUs'] == '0', 'zero line start remains exact string')
        self.check(any(token['time'] == {} and token['timingStatus'] == 'missing' for token in full['lines'][0]['tokens']),
                   'enhanced LRC retains intentionally untimed whitespace')
        legacy = self.request('old-lines', 'GET', path + '/lyrics/lines?' + urlencode({'branchId': branch}))
        expected_legacy = [{k: v for k, v in line.items() if k in ('branchId', 'id', 'text', 'time', 'tokens')} for line in full['lines']]
        for line in expected_legacy:
            line['tokens'] = [{k: v for k, v in token.items() if k != 'timingStatus'} for token in line['tokens']]
        self.check(legacy['lines'] == expected_legacy, 'old endpoint retains original minimal projection')
        assembled = []
        for offset in range(3):
            page = self.get('line-page-' + str(offset), path, '/lyrics/window', revision, branchId=branch, offset=offset, limit=1)
            self.check_page(page, 'original line page', offset, 2, 1 if offset == 0 else None)
            assembled.extend(page['lines'])
        self.check(assembled == full['lines'], 'complete pagination preserves exact stable IDs and values')
        end = self.get('branches-empty-end', path, '/lyrics/branches', revision, offset=1, limit=1)
        self.check_page(end, 'branch end', 1, 1, None)
        self.check(end['branches'] == [], 'branch terminal empty page')
        self.invalid_queries(path, revision, branch, 2)
        self.authorization(path, revision, branch)
        self.check(self.request('after-summary', 'GET', path) == summary, 'all GETs/errors preserve exact summary/dirty/history/cursor')
        self.check(self.request('after-history', 'GET', path + '/history') == history, 'all GETs/errors preserve undo/redo')
        after_state = self.request('after-client-state', 'GET', '/api/v1/clients/' + self.client + '/state')
        self.check(after_state['eventCursor'] == client_state['eventCursor'], 'all GETs/errors emit no project events')
        # The second LRC line has no timed tokens; narrowing it is a valid existing command.
        # The enhanced first line must retain its imported token containment semantics.
        line = full['lines'][1]
        self.request('write-between-pages', 'POST', path + '/commands', value=self.command(revision, 'between-pages', {
            'type': 'lyrics.setTimeRange', 'target': {'branchId': branch, 'lineId': line['id']}, 'startUs': '1100000', 'endUs': '1900000'}))
        for stage, current in (('after-write', '1'), ('after-undo', '2')):
            if stage == 'after-undo':
                self.request('undo', 'POST', path + '/commands', value=self.command('1', 'undo', {'type': 'history.undo'}))
            for endpoint in ('/lyrics/branches', '/lyrics/window', '/settings'):
                query = {'expectedRevision': revision}
                if endpoint.endswith('window'):
                    query.update(branchId=branch, offset=1, limit=1)
                self.request(stage + endpoint.replace('/', '-'), 'GET', path + endpoint + '?' + urlencode(query),
                             status=409, code='revisionConflict')
            current_page = self.get(stage + '-current', path, '/lyrics/window', current, branchId=branch)
            self.check(current_page['projectRevision'] == current, stage + ': current monotonic revision')
            if stage == 'after-write':
                self.check(current_page['lines'][1]['time'] == {'startUs': '1100000', 'endUs': '1900000'} and
                           current_page['lines'][1]['timingStatus'] == 'edited', 'accepted write updates time/state')
            else:
                self.check(current_page['lines'] == full['lines'], 'undo restores content while rejecting old revision')
        before_redo = self.request('redo-before-reads', 'GET', path + '/history')
        self.content('redo-read', path, '2')
        self.check(self.request('redo-after-reads', 'GET', path + '/history') == before_redo and before_redo['canRedo'],
                   'read models preserve available redo')
        path, opened = self.save_close_open('original-roundtrip', path, '2', 'editing-original')
        self.close('original-template-close', path, opened['projectRevision'])
        rich = self.fixture('editing-rich', self.rich_fixture)
        path, opened = self.open('rich-open', rich)
        self.check_rich(path, opened['projectRevision'])
        path, opened = self.save_close_open('rich-roundtrip', path, opened['projectRevision'], 'editing-rich-saved')
        self.check_rich(path, opened['projectRevision'])
        self.close('rich-close', path, opened['projectRevision'])
        budget = self.fixture('editing-budget', self.budget_fixture)
        path, opened = self.open('budget-open', budget)
        self.check_budgets(path, opened['projectRevision'])
        self.close('budget-close', path, opened['projectRevision'])
        self.check(bool(self.worker_identities), 'actual Worker executable identities observed during create/open preparation')
        self.stop('editing')
        scanned = 0
        for file in self.output.rglob('*'):
            if file.is_file():
                self.check(not any(secret.encode() in file.read_bytes() for secret in self.secrets),
                           'credential scan: ' + str(file.relative_to(self.output)))
                scanned += 1
        final_label = 'credential scan: summary.json'
        candidate = self.output / '.summary.candidate'
        save_json(candidate, {'status': 'passed', 'httpRequests': self.request_count,
            'assertions': len(self.assertions) + 1, 'assertionLabels': self.assertions + [final_label],
            'credentialScannedFiles': scanned + 1,
            'runs': self.runs, 'fixtures': self.fixtures,
            'workerIdentities': [{'pid': p, 'startTicks': t} for p, t in sorted(self.worker_identities)],
            'scope': 'S1 real HTTP read models and existing timing/history/persistence; offline fixture construction is not HTTP source/ruby editing; item fixture exceeds 20000 and may also exceed byte budget; no browser acceptance'})
        # Validate the exact final summary bytes before atomically making passed visible.
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), final_label)
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(REPO / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    args.output.mkdir(parents=True, exist_ok=False)
    probe = EditingProbe(args.output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
