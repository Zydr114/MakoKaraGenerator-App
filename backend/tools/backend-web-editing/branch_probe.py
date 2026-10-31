#!/usr/bin/env python3
"""S3b source management over real HTTP; owns every profile, fixture and process."""

import argparse
import copy
import importlib.util
import io
import json
from pathlib import Path
import wave

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('s3a_content_probe', HERE / 'content_probe.py')
content = importlib.util.module_from_spec(spec)
spec.loader.exec_module(content)
save_json = content.save_json
TYPES = ('lyrics.addTranslationBranch', 'lyrics.configureBranch', 'lyrics.removeBranch', 'lyrics.linkPrimaryLine')


def add(name, language=None):
    return {'type': TYPES[0], 'name': name, 'language': language}


def configure(branch='translation', name='翻译', language='zh-CN', role='translation', mode='followPrimary'):
    return {'type': TYPES[1], 'branchId': branch, 'name': name, 'language': language, 'role': role, 'timingMode': mode}


def remove(branch):
    return {'type': TYPES[2], 'branchId': branch}


def link(primary, branch='translation', line='translated'):
    return {'type': TYPES[3], 'branchId': branch, 'lineId': line, 'primaryLineId': primary}


class BranchProbe(content.ContentProbe):
    def request(self, name, method, path, **kwargs):
        result = super().request(name, method, path, **kwargs)
        command = (kwargs.get('value') or {}).get('command', {})
        if command.get('type') in ('history.undo', 'history.redo'):
            self.check('branchResult' not in result, name + ': history omits source selection suggestion')
        return result

    def content(self, name, path, revision):
        # The contract budgets one source independently. A complete project can
        # exceed a default page, so capture a consistent aggregate of real one-source pages.
        branches = []
        offset = 0
        first = None
        while True:
            page = self.get(name + '-source-page-' + str(offset), path, '/lyrics/branches', revision, offset=offset, limit=1)
            if first is None:
                first = page
            self.check(page['offset'] == offset and page['totalCount'] == first['totalCount'] and
                       page['primaryBranchId'] == first['primaryBranchId'], name + ': exact same-revision source pages')
            branches.extend(page['branches'])
            if 'nextOffset' not in page:
                break
            self.check(page['nextOffset'] == offset + len(page['branches']), name + ': source pagination advances exactly')
            offset = page['nextOffset']
        self.check(len(branches) == first['totalCount'], name + ': complete source list without truncation')
        aggregate = {**first, 'offset': 0, 'branches': branches}
        aggregate.pop('nextOffset', None)
        settings = self.get(name + '-settings', path, '/settings', revision)
        windows = {branch['id']: self.get(name + '-window-' + str(i), path, '/lyrics/window', revision,
                   branchId=branch['id']) for i, branch in enumerate(branches)}
        return aggregate, settings, windows

    @staticmethod
    def source(state, branch):
        return next(x for x in state['models'][0]['branches'] if x['id'] == branch)

    def accepted(self, name, path, command, *, changed=True, undo_name=None, check=None, **unused):
        before = self.state(name + '-before', path)
        revision = before['summary']['projectRevision']
        payload = self.payload(name, revision, command)
        result = self.request(name, 'POST', path + '/commands', value=payload)
        after = self.state(name + '-after', path)
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1] and result['previousRevision'] == revision and
                   result['projectRevision'] == str(int(revision) + int(changed)) == after['summary']['projectRevision'],
                   name + ': exact identity and monotonic revision')
        self.check(result['changed'] is changed and all(key not in result for key in ('timingResult', 'feedback', 'failureCode')),
                   name + ': source response has no unrelated timing/content/error result')
        if changed:
            self.check(result.get('undoName') == undo_name and after['history']['undoName'] == undo_name and
                       after['history']['undoCount'] == before['history']['undoCount'] + 1 and after['history']['redoCount'] == 0,
                       name + ': one complete HTTP action is one independent shared history')
            self.check(result['changeSet'] == {'areas': ['project'], 'branchIds': [], 'lineIds': [], 'tokenIds': [], 'requiresFullRefresh': True},
                       name + ': complete structural invalidation without truncated IDs')
            self.check(after['eventCursor'] != before['eventCursor'] and result['isDirty'], name + ': real event and dirty')
            if command['type'] == TYPES[0]:
                selected = result.get('branchResult', {}).get('selectedBranchId')
                old = {b['id'] for b in before['models'][0]['branches']}
                self.check(isinstance(selected, str) and selected not in old and self.source(after, selected)['id'] == selected,
                           name + ': actual new source selected exactly once')
            elif command['type'] == TYPES[2]:
                self.check(result.get('branchResult') == {'selectedBranchId': before['models'][0]['primaryBranchId']} and
                           command['branchId'] not in {b['id'] for b in after['models'][0]['branches']}, name + ': removal selects real primary source')
            else:
                self.check('branchResult' not in result, name + ': configure/link do not change client source selection')
        else:
            self.same_state(before, after, name)
            self.check('branchResult' not in result and result['changeSet'] == {
                'areas': [], 'branchIds': [], 'lineIds': [], 'tokenIds': [], 'requiresFullRefresh': False}, name + ': no-op has no selection/changeSet')
        if check:
            check(after)
        return before, after, result, payload

    def rejected(self, name, path, command, status, code, failure=None, *, field=None, raw_command=None):
        before = self.state(name + '-before', path)
        payload = self.payload(name, before['summary']['projectRevision'], command)
        if raw_command is None:
            result = self.request(name, 'POST', path + '/commands', value=payload, status=status, code=code)
        else:
            prefix = json.dumps({key: value for key, value in payload.items() if key != 'command'})
            body = (prefix[:-1] + ',"command":' + raw_command + '}').encode()
            save_json(self.output / (name + '-raw-command.json'), {'command': raw_command, 'scope': 'probe-owned malformed command only'})
            result = self.request(name, 'POST', path + '/commands', raw=body, content_type='application/json', status=status, code=code)
        self.check(all(key not in result for key in ('branchResult', 'feedback', 'timingResult')), name + ': rejection has no success feedback')
        self.check(result.get('failureCode') == failure if failure else 'failureCode' not in result, name + ': exact optional domain reason')
        if field:
            self.check(result.get('field') == field and field != failure, name + ': actual request field path')
        self.same_state(before, self.state(name + '-after', path), name)
        return result

    def branch_fixture(self, project, lyrics):
        self.fixture_content(project, lyrics)
        translated = lyrics['branches'][1]['lines'][0]
        translated.pop('primaryLineId', None)
        translated['rubyAnnotations'] = [{'id': 'translated-ruby', 'baseTokenId': 'translated-token', 'text': 'yi',
            'language': 'zh-CN', 'relativeScale': .65, 'gapPx': 2.75, 'alignment': 'End'}]
        lyrics['branches'][1]['styleOverride'] = {'typography': {'fontSizePx': 25}, 'opacity': .75}

    def basics(self, path):
        original = self.state('source-original', path)
        added = self.exercise('source-add-trim', path, add(' 新翻译 ', ' ja-JP '), undo_name='branch.add')
        added_id = next(b['id'] for b in added['models'][0]['branches'] if b['name'] == '新翻译')
        self.check(self.source(added, added_id) == {'id': added_id, 'name': '新翻译', 'language': ' ja-JP ',
                   'role': 'translation', 'timingMode': 'followPrimary', 'lineCount': 0} and self.rows(added, added_id) == [],
                   'add: trims name only, preserves raw language, creates empty Translation/FollowPrimary source')
        self.exercise('source-configure-metadata', path, configure(added_id, ' 原名保空白 ', None, 'annotation', 'none'),
            undo_name='branch.configure', check=lambda state: self.check(self.source(state, added_id) == {
                'id': added_id, 'name': ' 原名保空白 ', 'role': 'annotation', 'timingMode': 'none', 'lineCount': 0},
                'configure: name exact, language null clears field, explicit role/mode'))
        translated = copy.deepcopy(self.rows(original, 'translation'))
        for mode in ('none', 'independent', 'followPrimary'):
            current = self.state('mode-' + mode + '-current', path)
            changed = self.source(current, 'translation')['timingMode'] != mode
            if changed:
                self.exercise('source-mode-' + mode, path, configure(mode=mode), undo_name='branch.configure')
            for primary in ('one', None):
                def linked(state, primary=primary):
                    expected = copy.deepcopy(translated)
                    if primary is None:
                        expected[0].pop('primaryLineId', None)
                    else:
                        expected[0]['primaryLineId'] = primary
                    self.check(self.rows(state, 'translation') == expected, 'link: only association changes, raw time/tokens/ruby remain exact')
                    self.check(self.rows(state) == self.rows(original), 'link: primary source contents unchanged')
                self.exercise('source-link-' + mode + ('-one' if primary else '-null'), path, link(primary),
                              undo_name='branch.configure', check=linked)
        self.exercise('source-remove-added', path, remove(added_id), undo_name='branch.remove')
        self.accepted('source-noop-setup-link', path, link('one'), undo_name='branch.configure')
        self.history('source-noop-setup-undo', path, 'undo')
        self.accepted('source-unlink-noop-preserves-redo', path, link(None), changed=False)
        self.accepted('source-configure-noop-preserves-redo', path, configure(), changed=False)
        self.history('source-noop-preserved-redo', path, 'redo')

    def errors(self, path):
        invalid = [('add-missing-name', {'type': TYPES[0]}), ('add-null-name', add(None)), ('add-numeric-name', add(1)),
            ('add-language-number', add('x', 1)), ('configure-null-branch', configure(None)), ('configure-null-name', configure(name=None)),
            ('configure-null-role', configure(role=None)), ('configure-null-mode', configure(mode=None)),
            ('configure-numeric-role', configure(role=0)), ('configure-numeric-string-role', configure(role='0')),
            ('configure-unknown-role', configure(role='unknown')), ('configure-numeric-mode', configure(mode=1)),
            ('configure-numeric-string-mode', configure(mode='1')), ('configure-unknown-mode', configure(mode='unknown')),
            ('remove-missing-branch', {'type': TYPES[2]}), ('remove-null-branch', remove(None)), ('remove-empty-branch', remove('')),
            ('link-null-line', link(None, line=None)), ('link-empty-line', link(None, line='')),
            ('link-numeric-primary', link(1)), ('link-missing-primary', {'type': TYPES[3], 'branchId': 'translation', 'lineId': 'translated'}),
            ('add-cross-null', {**add('x'), 'target': None}), ('configure-cross-null', {**configure(), 'text': None}),
            ('remove-cross-null', {**remove('empty'), 'language': None}), ('link-cross-null', {**link(None), 'startUs': None})]
        for missing in ('branchId', 'name', 'language', 'role', 'timingMode'):
            command = configure()
            command.pop(missing)
            invalid.append(('configure-missing-' + missing, command))
        for name, command in invalid:
            self.rejected('shape-' + name, path, command, 400, 'invalidRequest')
        raw = json.dumps(configure())[:-1] + ',"language":null}'
        self.rejected('shape-duplicate-language', path, configure(), 400, 'invalidRequest', raw_command=raw)
        raw = json.dumps(link(None))[:-1] + ',"primaryLineId":null}'
        self.rejected('shape-duplicate-link-null', path, link(None), 400, 'invalidRequest', raw_command=raw)
        cases = [('remove-primary', remove('primary'), 'PrimaryBranchProtected', 'command.branchId'),
            ('configure-primary-role', configure('primary', '主歌词', 'ja-JP', 'custom', 'independent'), 'PrimaryBranchProtected', 'command.role'),
            ('nonprimary-cannot-be-primary', configure(role='primary'), 'PrimaryBranchProtected', 'command.role'),
            ('link-primary-source', link('one', 'primary', 'one'), 'PrimaryBranchProtected', 'command.branchId'),
            ('link-nonprimary-row', link('translated'), 'InvalidPrimaryLine', 'command.primaryLineId'),
            ('link-unknown-primary', link('missing-primary'), 'InvalidPrimaryLine', 'command.primaryLineId'),
            ('link-empty-primary', link(''), 'InvalidPrimaryLine', 'command.primaryLineId'),
            ('missing-branch-configure', configure('missing'), 'TargetStale', 'command.branchId'),
            ('missing-branch-remove', remove('missing'), 'TargetStale', 'command.branchId'),
            ('missing-branch-link', link(None, 'missing'), 'TargetStale', 'command.branchId'),
            ('missing-line-link', link(None, line='missing'), 'TargetStale', 'command.lineId'),
            ('blank-add-name', add(' \n '), 'InvalidConfiguration', 'command.name'),
            ('blank-configure-name', configure(name=' '), 'InvalidConfiguration', 'command.name')]
        for name, command, failure, field in cases:
            self.rejected('domain-' + name, path, command, 422, 'commandRejected', failure, field=field)
        self.rejected('unknown-type', path, {'type': 'unknown.command'}, 422, 'commandNotSupported')

    def budgets(self, path):
        self.accepted('budget-setup-configure', path, configure(name='临时名称'), undo_name='branch.configure')
        self.history('budget-setup-undo', path, 'undo')
        huge = '汉' * 180000
        for name, command in [('add-name', add(huge)), ('add-language', add('new', huge)),
            ('configure-name', configure(name=huge)), ('configure-language', configure(language=huge))]:
            self.rejected('budget-' + name, path, command, 413, 'resourceTooLarge', field='command')
        self.accepted('budget-noop-preserves-redo', path, configure(), changed=False)
        self.history('budget-preserved-redo', path, 'redo')

    def receipts(self, path):
        cases = [('add', add('回执来源'), {'type': TYPES[0], 'name': '回执来源'}, [add(' 回执来源 '), add('回执来源', 'ja')], 'branch.add'),
            ('configure', configure(name=' 回执原名 ', language=None, role='annotation', mode='none'),
             configure(name=' 回执原名 ', language=None, role='annotation', mode='none'),
             [configure('empty', ' 回执原名 ', None, 'annotation', 'none'), configure(name='回执原名', language=None, role='annotation', mode='none'),
              configure(name=' 回执原名 ', language='ja', role='annotation', mode='none'), configure(name=' 回执原名 ', language=None, role='custom', mode='none'),
              configure(name=' 回执原名 ', language=None, role='annotation', mode='independent')], 'branch.configure'),
            ('link', link('two'), {'primaryLineId': 'two', 'lineId': 'translated', 'branchId': 'translation', 'type': TYPES[3]},
             [link(None), link('one'), link('two', 'empty'), link('two', line='other')], 'branch.configure'),
            ('remove', remove('empty'), remove('empty'), [remove('translation')], 'branch.remove')]
        for name, command, same, differences, undo_name in cases:
            _, after, original, payload = self.accepted('receipt-source-' + name, path, command, undo_name=undo_name)
            retry = {**payload, 'expectedRevision': '000' + payload['expectedRevision'], 'command': same}
            self.check(self.request('receipt-source-' + name + '-normalized-stale-retry', 'POST', path + '/commands', value=retry) == original,
                       name + ': entire source receipt/selected ID returned before stale revision gate')
            for index, different in enumerate(differences):
                self.request('receipt-source-' + name + '-field-reuse-' + str(index), 'POST', path + '/commands',
                    value={**payload, 'command': different}, status=409, code='requestIdReuse')
            self.request('receipt-source-' + name + '-revision-reuse', 'POST', path + '/commands',
                value={**payload, 'expectedRevision': str(int(payload['expectedRevision']) + 1)}, status=409, code='requestIdReuse')
            self.request('receipt-source-' + name + '-deadline-reuse', 'POST', path + '/commands', value={**payload,
                'validUntilUtc': content.timing.s1.smoke.existing.iso(content.timing.s1.smoke.existing.utc_now() +
                    content.timing.s1.smoke.existing.timedelta(seconds=95))}, status=409, code='requestIdReuse')
            self.same_state(after, self.state('receipt-source-' + name + '-stable', path), name + ': retry/reuse does not allocate or commit again')

    def races(self, path):
        before = self.state('source-race-before', path)
        revision = before['summary']['projectRevision']
        results = self.concurrent('source-two-add-intents', path, [self.payload('source-race-' + str(i), revision, add('race-' + str(i))) for i in range(2)])
        self.check(sorted(x[0] for x in results) == [200, 409] and next(x[2]['code'] for x in results if x[0] == 409) == 'revisionConflict',
                   'source race: one exact revision accepts one independent intent')
        after = self.state('source-race-after', path)
        self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and
                   len(after['models'][0]['branches']) == len(before['models'][0]['branches']) + 1, 'source race: one source and history created')
        payload = self.payload('source-same-receipt', after['summary']['projectRevision'], add('same-intent'))
        results = self.concurrent('source-same-add-receipt', path, [payload, copy.deepcopy(payload)])
        self.check([x[0] for x in results] == [200, 200] and results[0][2] == results[1][2], 'source same receipt concurrent: complete identical selection/response')
        latest = self.state('source-same-receipt-after', path)
        selected = results[0][2]['branchResult']['selectedBranchId']
        self.check(sum(b['id'] == selected for b in latest['models'][0]['branches']) == 1 and
                   latest['history']['undoCount'] == after['history']['undoCount'] + 1, 'same source receipt: exactly one allocated ID/history')

    def readonly_and_auth(self, path):
        before = self.state('source-auth-before', path)
        other = self.register('source-other-client', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        for command in (add('other'), configure(), remove('translation'), link('one')):
            self.request('source-cross-client-' + command['type'].rsplit('.', 1)[1], 'POST', path + '/commands', client=other['clientId'],
                value=self.payload('cross-client', before['summary']['projectRevision'], command), status=404, code='sessionNotFound')
        self.request('source-missing-session', 'POST', '/api/v1/project-sessions/missing/commands',
            value=self.payload('missing-session', '0', add('x')), status=404, code='sessionNotFound')
        self.same_state(before, self.state('source-auth-after', path), 'source cross-client/missing session')
        relative = self.fixture('source-readonly', self.branch_fixture)
        handle = self.request('source-readonly-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': relative})
        opened = self.job('source-readonly-open', self.request('source-readonly-open', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId'], 'access': 'read'}))['result']
        self.check(opened['isReadOnly'], 'source readonly: actual read access session')
        readonly_path = '/api/v1/project-sessions/' + opened['projectSessionId']
        for command in (add('readonly'), configure(name='readonly'), remove('translation'), link('one')):
            self.rejected('readonly-source-' + command['type'].rsplit('.', 1)[1], readonly_path, command, 403, 'readOnlySession')
        self.close('source-readonly-close', readonly_path, opened['projectRevision'])

    def long_and_dangling(self):
        long_branch, long_line, long_primary = '来源' * 100, '译文' * 100, '主行' * 100
        def long_ids(project, lyrics):
            self.branch_fixture(project, lyrics)
            lyrics['branches'][0]['lines'][0]['id'] = long_primary
            lyrics['branches'][1]['id'] = long_branch
            lyrics['branches'][1]['lines'][0]['id'] = long_line
        relative = self.fixture('source-long-ids', long_ids)
        path, opened = self.open('source-long-open', relative)
        self.exercise('source-long-configure', path, configure(long_branch, ' 长ID名称 ', None, 'custom', 'independent'), undo_name='branch.configure')
        self.exercise('source-long-link', path, link(long_primary, long_branch, long_line), undo_name='branch.configure',
            check=lambda state: self.check(self.rows(state, long_branch)[0]['primaryLineId'] == long_primary and
                self.rows(state, long_branch)[0]['time'] == {}, 'source long IDs: no inherited 128 limit or synthesized time'))
        self.exercise('source-long-remove', path, remove(long_branch), undo_name='branch.remove')
        path, opened = self.save_close_open('source-long-roundtrip', path,
            self.request('source-long-current', 'GET', path)['projectRevision'], 'source-long-saved')
        self.close('source-long-close', path, opened['projectRevision'])
        def dangling(project, lyrics):
            self.branch_fixture(project, lyrics)
            lyrics['branches'][1]['lines'][0]['primaryLineId'] = 'gone-primary-line'
        relative = self.fixture('source-existing-dangling', dangling)
        path, opened = self.open('source-dangling-open', relative)
        self.accepted('source-existing-dangling-noop', path, link('gone-primary-line'), changed=False)
        self.exercise('source-existing-dangling-unlink', path, link(None), undo_name='branch.configure')
        path, opened = self.save_close_open('source-dangling-roundtrip', path,
            self.request('source-dangling-current', 'GET', path)['projectRevision'], 'source-dangling-saved')
        self.close('source-dangling-close', path, opened['projectRevision'])
        def modes(project, lyrics):
            self.branch_fixture(project, lyrics)
            project['lyricPresentation'] = {'nodes': [], 'programs': [], 'lineOverrides': []}
        relative = self.fixture('source-primary-modes', modes)
        path, opened = self.open('source-primary-modes-open', relative)
        for mode in ('none', 'followPrimary', 'independent'):
            self.exercise('primary-mode-' + mode, path, configure('primary', '主歌词', 'ja-JP', 'primary', mode),
                undo_name='branch.configure', check=lambda state, mode=mode: self.check(self.source(state, 'primary')['timingMode'] == mode,
                'primary: valid time strategy follows original presentation constraints, role remains protected'))
        path, opened = self.save_close_open('source-primary-mode-roundtrip', path,
            self.request('source-primary-mode-current', 'GET', path)['projectRevision'], 'source-primary-mode-saved')
        self.close('source-primary-modes-close', path, opened['projectRevision'])

    def compact(self, name, path, *, translation=False):
        summary = self.request(name + '-summary', 'GET', path)
        history = self.request(name + '-history', 'GET', path + '/history')
        state = self.request(name + '-client', 'GET', '/api/v1/clients/' + self.client + '/state')
        result = [summary, history, state['eventCursor']]
        if translation:
            result.append(self.get(name + '-translation', path, '/lyrics/window', summary['projectRevision'], branchId='translation'))
        return result

    def specialized_budgets(self):
        relative = self.fixture('source-per-item-budget', self.branch_fixture)
        path, opened = self.open('source-per-item-open', relative)
        ids = []
        for index in range(2):
            _, after, result, _ = self.accepted('source-large-readable-' + str(index), path, add(('甲' if index == 0 else '乙') * 100000), undo_name='branch.add')
            ids.append(result['branchResult']['selectedBranchId'])
        revision = self.request('source-large-page-current', 'GET', path)['projectRevision']
        self.request('source-large-combined-page-rejected', 'GET', path + '/lyrics/branches?expectedRevision=' + revision,
                     status=413, code='resourceTooLarge')
        for index in (3, 4):
            page = self.get('source-large-individual-' + str(index), path, '/lyrics/branches', revision, offset=index, limit=1)
            self.check(len(page['branches']) == 1 and page['branches'][0]['id'] == ids[index - 3], 'source budget: each whole source remains independently readable')
        path, opened = self.save_close_open('source-large-roundtrip', path, revision, 'source-large-saved')
        self.close('source-large-close', path, opened['projectRevision'])
        def oversized(project, lyrics):
            self.branch_fixture(project, lyrics)
            lyrics['branches'][1]['name'] = '汉' * 180000
        relative = self.fixture('source-oversized-old', oversized)
        path, opened = self.open('source-oversized-old-open', relative)
        before = self.compact('source-oversized-old-before', path)
        self.request('source-oversized-single-read', 'GET', path + '/lyrics/branches?expectedRevision=0&offset=1&limit=1', status=413, code='resourceTooLarge')
        result = self.request('source-oversized-old-remove', 'POST', path + '/commands', value=self.payload('source-old-remove', '0', remove('translation')))
        self.check(result['changed'] and result['branchResult'] == {'selectedBranchId': 'primary'}, 'remove: oversized old source can be repaired without reading deleted source')
        path, opened = self.save_close_open('source-old-repair-roundtrip', path, result['projectRevision'], 'source-old-repaired')
        self.close('source-old-repair-close', path, opened['projectRevision'])
        def receipt(project, lyrics):
            self.branch_fixture(project, lyrics)
            identifier = '源' * 11000
            lyrics['primaryBranchId'] = identifier
            lyrics['branches'][0]['id'] = identifier
            project['lyricPresentation'] = {'nodes': [], 'programs': [], 'lineOverrides': []}
        relative = self.fixture('source-large-selection-receipt', receipt)
        path, opened = self.open('source-large-selection-open', relative)
        before = self.compact('source-receipt-bound-before', path)
        error = self.request('source-large-selection-remove-rejected', 'POST', path + '/commands',
            value=self.payload('large-selection', '0', remove('translation')), status=413, code='resourceTooLarge')
        self.check(error.get('field') == 'command' and all(key not in error for key in ('branchResult', 'failureCode')), 'source selection: full receipt budget rejected before mutation')
        self.check(self.compact('source-receipt-bound-after', path) == before, 'source selection receipt rejection preserves summary/history/cursor')
        self.close('source-large-selection-close', path, opened['projectRevision'])
        def link_budget(project, lyrics):
            self.branch_fixture(project, lyrics)
            lyrics['branches'][0]['lines'][0]['id'] = '汉' * 180000
        relative = self.fixture('source-link-line-budget', link_budget)
        path, opened = self.open('source-link-line-open', relative)
        before = self.compact('source-link-budget-before', path, translation=True)
        self.request('source-link-huge-primary-rejected', 'POST', path + '/commands',
            value=self.payload('link-large-primary', '0', link('汉' * 180000)), status=413, code='resourceTooLarge')
        self.check(self.compact('source-link-budget-after', path, translation=True) == before, 'link budget: exact raw translated line/metadata/history/cursor preserved')
        self.close('source-link-line-close', path, opened['projectRevision'])

    def finish(self):
        self.check(bool(self.worker_identities), 'actual Worker preparation identities observed')
        self.stop('source')
        self.check(len(self.samples) == self.request_count, 'every HTTP call has local elapsed sample')
        save_json(self.output / 'http-elapsed.json', {'samples': self.samples,
            'scope': 'single local short samples including client validation/log overhead; no performance/throughput/platform guarantee'})
        scanned = 0
        for file in self.output.rglob('*'):
            if file.is_file():
                self.check(not any(secret.encode() in file.read_bytes() for secret in self.secrets), 'credential scan: ' + str(file.relative_to(self.output)))
                scanned += 1
        label = 'credential scan: summary.json'
        candidate = self.output / '.summary.candidate'
        save_json(candidate, {'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions) + 1,
            'assertionLabels': self.assertions + [label], 'credentialScannedFiles': scanned + 1, 'runs': self.runs, 'fixtures': self.fixtures,
            'workerIdentities': [{'pid': p, 'startTicks': t} for p, t in sorted(self.worker_identities)],
            'scope': 'S3b four real HTTP source commands; owned fixtures and existing read/history/persistence; excludes browser/import/authoring/complete-B3-B4/performance acceptance'})
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), label)
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))

    def run(self):
        self.start('source')
        self.connect('source')
        capabilities = self.request('source-capabilities', 'GET', '/api/v1/capabilities')
        for command in TYPES:
            self.check('project-sessions.commands.' + command in capabilities['features'], 'advertised S3b command ' + command)
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000); wav.writeframes(b'\x00\x00' * 16000)
        assets = []
        for name, data, media in (('source.wav', buf.getvalue(), 'audio/wav'), ('source.lrc', '[00:00.00]甲乙\n'.encode(), 'text/plain')):
            assets.append(self.request('upload-' + name, 'POST', '/api/v1/assets?fileName=' + name, raw=data, content_type=media, status=201,
                headers={'X-Mako-Request-Id': 'upload-' + name, 'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']}))
        created = self.request('create-template', 'POST', '/api/v1/project-sessions', status=201,
            value={'name': 'S3b source HTTP probe', 'audioAssetId': assets[0]['assetId'], 'lyricsAssetId': assets[1]['assetId']})
        path = '/api/v1/project-sessions/' + created['projectSessionId']
        path, opened = self.save_close_open('source-template', path, created['projectRevision'], 'editing-original')
        self.close('source-template-close', path, opened['projectRevision'])
        relative = self.fixture('source-normal', self.branch_fixture)
        path, opened = self.open('source-normal-open', relative)
        self.basics(path)
        self.errors(path)
        self.budgets(path)
        self.receipts(path)
        self.races(path)
        self.readonly_and_auth(path)
        path, opened = self.save_close_open('source-roundtrip', path,
            self.request('source-current', 'GET', path)['projectRevision'], 'source-saved')
        # Styles are intentionally not in S1. Check this owned saved closure separately.
        root = self.output / 'profile/projects/source-saved'
        project = json.loads((root / 'project.json').read_text())
        lyrics = json.loads((root / project['lyricsFile']).read_text())
        style = next(branch for branch in lyrics['branches'] if branch['id'] == 'translation')['styleOverride']
        self.check(style == {'typography': {'fontSizePx': 25}, 'opacity': .75}, 'configure/link preserve complete owned source style override through save')
        original = json.loads((self.output / 'profile/projects/source-normal/project.json').read_text())
        self.check(project['lyricPresentation'] == original['lyricPresentation'], 'configure/link/add/remove without source presentation preserve existing presentation')
        self.close('source-normal-close', path, opened['projectRevision'])
        self.long_and_dangling()
        self.specialized_budgets()
        self.finish()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(content.timing.s1.REPO / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    args.output.mkdir(parents=True, exist_ok=False)
    probe = BranchProbe(args.output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
