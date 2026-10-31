#!/usr/bin/env python3
"""S3c token structure acceptance over real HTTP with owned, hash-addressed fixtures."""

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
LINE = {'branchId': 'primary', 'lineId': 'one'}
FIRST = {**LINE, 'tokenId': 'first'}
SECOND = {**LINE, 'tokenId': 'second'}
TYPES = ('lyrics.splitToken', 'lyrics.mergeToken', 'lyrics.reorderToken', 'lyrics.resplitLine', 'lyrics.resplitBranch')


def split(target=FIRST):
    return {'type': TYPES[0], 'target': target}


def merge(target=FIRST):
    return {'type': TYPES[1], 'target': target}


def reorder(destination, reassign, target=FIRST):
    return {'type': TYPES[2], 'target': target, 'destinationIndex': destination, 'reassignTimeSlots': reassign}


def resplit(segments, estimate=False, target=LINE):
    return {'type': TYPES[3], 'target': target, 'segments': segments, 'estimateTimes': estimate}


def branch(rule, estimate=False, identifier='primary'):
    return {'type': TYPES[4], 'branchId': identifier, 'rule': rule, 'estimateTimes': estimate}


class TokenProbe(content.ContentProbe):
    def content(self, name, path, revision):
        branches = self.get(name + '-branches', path, '/lyrics/branches', revision)
        settings = self.get(name + '-settings', path, '/settings', revision)
        windows = {}
        # Large operation fixtures can exceed a whole-page budget. Fetch complete,
        # real single-line windows at one exact revision, never silently truncate.
        for index, source in enumerate(branches['branches']):
            pages = [self.get(name + '-window-' + str(index) + '-' + str(offset), path,
                '/lyrics/window', revision, branchId=source['id'], offset=offset, limit=1)
                for offset in range(max(1, source['lineCount']))]
            rows = [row for page in pages for row in page['lines']]
            self.check(len(rows) == source['lineCount'] and all(page['totalCount'] == source['lineCount'] for page in pages),
                       name + ': complete same-revision single-line windows')
            aggregate = {**pages[0], 'offset': 0, 'lines': rows}
            aggregate.pop('nextOffset', None)
            windows[source['id']] = aggregate
        return branches, settings, windows

    def accepted(self, name, path, command, *, changed=True, undo_name=None, check=None,
                 counts=None, **unused):
        before = self.state(name + '-before', path)
        revision = before['summary']['projectRevision']
        payload = self.payload(name, revision, command)
        result = self.request(name, 'POST', path + '/commands', value=payload)
        after = self.state(name + '-after', path)
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1] and result['previousRevision'] == revision and
                   result['projectRevision'] == str(int(revision) + int(changed)) == after['summary']['projectRevision'],
                   name + ': exact session and previous/current revision')
        self.check(result['changed'] is changed and all(k not in result for k in ('feedback', 'branchResult', 'timingResult', 'failureCode')),
                   name + ': token command omits unrelated result fields')
        value = result.get('tokenResult')
        self.check(isinstance(value, dict) and set(value) <= {'changedLineCount', 'selection', 'preservedTokenCount',
                   'createdTokenCount', 'estimatedTokenCount'}, name + ': bounded actual token result')
        source = command.get('branchId') or command['target']['branchId']
        old_rows, new_rows = self.rows(before, source), self.rows(after, source)
        actual_changed = sum(old != new for old, new in zip(old_rows, new_rows))
        self.check(len(old_rows) == len(new_rows) and value['changedLineCount'] == actual_changed,
                   name + ': changed line count agrees with entire source snapshot')
        counted = command['type'] in (TYPES[0], TYPES[3], TYPES[4])
        if counted:
            self.check(all(isinstance(value.get(k), int) and value[k] >= 0 for k in
                       ('preservedTokenCount', 'createdTokenCount', 'estimatedTokenCount')), name + ': explicit original count fields')
            if counts is not None:
                self.check([value[k] for k in ('preservedTokenCount', 'createdTokenCount', 'estimatedTokenCount')] == list(counts),
                           name + ': exact shared-rule preserved/created/estimated counts')
        else:
            self.check(all(k not in value for k in ('preservedTokenCount', 'createdTokenCount', 'estimatedTokenCount')),
                       name + ': merge/reorder omit fabricated statistics')
        if changed:
            self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and after['history']['redoCount'] == 0 and
                       result.get('undoName') == undo_name == after['history']['undoName'], name + ': exactly one independent history action')
            target = command.get('target')
            self.check(result['changeSet'] == {'areas': ['lyricsContent', 'lyricsStructure', 'timeline', 'renderInputs'],
                'branchIds': [source], 'lineIds': [] if target is None else [target['lineId']], 'tokenIds': [], 'requiresFullRefresh': True},
                name + ': complete structure refresh without large ID mapping')
            self.check(result['isDirty'] and after['eventCursor'] != before['eventCursor'], name + ': real dirty/event')
            if command['type'] in (TYPES[0], TYPES[1], TYPES[3]):
                selection = value.get('selection')
                self.check(isinstance(selection, dict) and selection['branchId'] == source and selection['lineId'] == target['lineId'],
                           name + ': actual selected line/source')
                if command['type'] == TYPES[3]:
                    self.check(selection == {'branchId': source, 'lineId': target['lineId'], 'hasTokenSelection': False},
                               name + ': manual resplit clears token selection explicitly')
                else:
                    identifiers = [t['id'] for t in self.row(after, target['lineId'], source)['tokens']]
                    self.check(selection['hasTokenSelection'] is True and selection.get('tokenId') in identifiers,
                               name + ': split/merge select actual resulting token')
                    if command['type'] == TYPES[1]:
                        self.check(selection['tokenId'] == target['tokenId'], name + ': merge selects retained left ID')
                    else:
                        old_ids = {t['id'] for row in old_rows for t in row['tokens']}
                        self.check(selection['tokenId'] not in old_ids, name + ': split selects new right ID')
            else:
                self.check('selection' not in value, name + ': reorder/source operation preserves client selection')
        else:
            self.same_state(before, after, name)
            expected = {'changedLineCount': 0}
            if counted:
                expected.update(preservedTokenCount=0, createdTokenCount=0, estimatedTokenCount=0)
            self.check(value == expected and result['changeSet'] == {'areas': [], 'branchIds': [], 'lineIds': [],
                'tokenIds': [], 'requiresFullRefresh': False}, name + ': no-op zero result and no selection/changeSet')
        if check:
            check(before, after, value)
        return before, after, result, payload

    def rejected(self, name, path, command, status, code, failure=None, *, field=None, raw_command=None):
        before = self.state(name + '-before', path)
        payload = self.payload(name, before['summary']['projectRevision'], command)
        if raw_command is None:
            result = self.request(name, 'POST', path + '/commands', value=payload, status=status, code=code)
        else:
            prefix = json.dumps({k: v for k, v in payload.items() if k != 'command'})
            save_json(self.output / (name + '-raw-command.json'), {'command': raw_command})
            result = self.request(name, 'POST', path + '/commands', raw=(prefix[:-1] + ',"command":' + raw_command + '}').encode(),
                                  content_type='application/json', status=status, code=code)
        self.check(all(k not in result for k in ('tokenResult', 'feedback', 'branchResult', 'timingResult')), name + ': rejection has no success result')
        self.check(result.get('failureCode') == failure if failure else 'failureCode' not in result, name + ': exact optional domain reason')
        if field:
            self.check(result.get('field') == field, name + ': actual command input path')
        self.same_state(before, self.state(name + '-after', path), name)
        return result

    def history(self, name, path, action, **kwargs):
        state = super().history(name, path, action, **kwargs)
        return state

    def request(self, name, method, path, **kwargs):
        result = super().request(name, method, path, **kwargs)
        command = (kwargs.get('value') or {}).get('command', {})
        if command.get('type') in ('history.undo', 'history.redo'):
            self.check('tokenResult' not in result and 'branchResult' not in result, name + ': history omits token/source result')
        return result

    def token_fixture(self, project, lyrics):
        self.fixture_content(project, lyrics)
        # Keep only two primary tokens for the fixed five-command oracle.
        lyrics['branches'] = [lyrics['branches'][0], lyrics['branches'][1], lyrics['branches'][2]]
        row = lyrics['branches'][0]['lines'][0]
        row['tokens'][0]['styleOverride'] = {'typography': {'fontSizePx': 31}, 'opacity': .8}
        row['rubyAnnotations'].append({'id': 'ruby-right', 'baseTokenId': 'second', 'text': 'おつ',
            'language': 'ja-JP', 'relativeScale': .6, 'gapPx': 2.5, 'alignment': 'End'})

    def core_sequence(self, path):
        original = self.state('core-original', path)
        ruby = self.row(original)['rubyAnnotations']
        def merged(before, after, value):
            tokens = self.row(after)['tokens']
            self.check(tokens == [{'id': 'first', 'text': '甲乙', 'time': {'startUs': '100000', 'endUs': '700000'}, 'timingStatus': 'imported'}],
                       'merge: retains left ID, same status and union range')
            self.check(self.row(after)['rubyAnnotations'] == ruby, 'merge: preserves now-dangling right annotation, never rebinds')
        self.exercise('merge-real', path, merge(), undo_name='line.merge', check=merged)
        def split_check(before, after, value):
            left, right = self.row(after)['tokens']
            self.check(left == {'id': 'first', 'text': '甲', 'time': {'startUs': '100000', 'endUs': '400000'}, 'timingStatus': 'estimated'} and
                       right['text'] == '乙' and right['time'] == {'startUs': '400000', 'endUs': '700000'} and right['timingStatus'] == 'estimated',
                       'split: exact midpoint ranges, left identity, new right and estimated status')
            self.check(right['id'] == value['selection']['tokenId'] and self.row(after)['rubyAnnotations'] == ruby, 'split: actual new selection and unchanged annotations')
        self.exercise('split-real', path, split(), undo_name='line.split', counts=(1, 1, 2), check=split_check)
        def ordered(before, after, value):
            old = self.row(before)['tokens']; new = self.row(after)['tokens']
            self.check(self.row(after)['text'] == '乙甲' and [t['id'] for t in new] == [old[1]['id'], old[0]['id']] and
                [t['time'] for t in new] == [t['time'] for t in old] and all(t['timingStatus'] == 'edited' for t in new),
                'reorder explicit slots: new text/order, original slots, edited status')
            self.check(self.row(after)['rubyAnnotations'] == ruby, 'reorder: preserve original ruby IDs/base references')
        self.exercise('reorder-real', path, reorder(1, True), undo_name='line.reorder', check=ordered)
        def manual(before, after, value):
            tokens = self.row(after)['tokens']
            self.check(len(tokens) == 1 and tokens[0]['id'] not in {t['id'] for t in self.row(before)['tokens']} and
                       tokens[0]['text'] == '乙甲' and tokens[0]['time'] == {'startUs': '100000', 'endUs': '700000'} and
                       tokens[0]['timingStatus'] == 'edited', 'manual merge: real new ID, union time and same status')
        self.exercise('resplit-line-real', path, resplit(['乙甲']), undo_name='line.split-rule', counts=(0, 1, 0), check=manual)
        self.exercise('resplit-branch-real', path, branch('perTextElement', True), undo_name='lyrics.split-rule', counts=(0, 2, 2),
            check=lambda before, after, value: self.check([t['text'] for t in self.row(after)['tokens']] == ['乙', '甲'] and
                [t['time'] for t in self.row(after)['tokens']] == [{'startUs': '100000', 'endUs': '400000'}, {'startUs': '400000', 'endUs': '700000'}],
                'branch: exact fixed estimated segments and microseconds'))
        final = self.state('core-final', path)
        for index in range(5):
            current = self.history('all-five-undo-' + str(index), path, 'undo')
        self.same_content(original, current, 'five commands independent undo chain')
        for command in (split(), merge(SECOND), reorder(0, False), resplit(['甲', '乙']), branch('none', True), branch('mixed', False, 'empty')):
            self.accepted('noop-' + command['type'].rsplit('.', 1)[1] + '-' + str(self.command_counter), path, command, changed=False)
        for index in range(5):
            current = self.history('all-five-redo-' + str(index), path, 'redo')
        self.same_content(final, current, 'five commands complete redo chain')

    def malformed(self, path):
        invalid = [('split-no-target', {'type': TYPES[0]}), ('split-line-target', split(LINE)), ('merge-line-target', merge(LINE)),
            ('missing-token', split({**FIRST, 'tokenId': ''})), ('long-token', split({**FIRST, 'tokenId': 'x' * 129})),
            ('reorder-no-destination', {'type': TYPES[2], 'target': FIRST, 'reassignTimeSlots': False}),
            ('reorder-no-bool', {'type': TYPES[2], 'target': FIRST, 'destinationIndex': 0}),
            ('reorder-string-destination', reorder('0', False)), ('reorder-fraction', reorder(0.5, False)),
            ('reorder-overflow', reorder(2147483648, False)), ('reorder-numeric-bool', reorder(0, 0)),
            ('reorder-null-bool', reorder(0, None)), ('line-no-estimate', {'type': TYPES[3], 'target': LINE, 'segments': ['乙甲']}),
            ('line-null-array', resplit(None)), ('line-numeric-array', resplit([1])), ('line-null-element', resplit([None])),
            ('line-object-array', resplit([{}])), ('line-token-target', resplit(['乙甲'], target=FIRST)),
            ('line-null-bool', resplit(['乙甲'], None)), ('branch-missing-rule', {'type': TYPES[4], 'branchId': 'primary', 'estimateTimes': False}),
            ('branch-no-bool', {'type': TYPES[4], 'branchId': 'primary', 'rule': 'none'}), ('branch-null-rule', branch(None)),
            ('branch-numeric-rule', branch(0)), ('branch-string-number-rule', branch('0')), ('branch-uppercase-rule', branch('Mixed')),
            ('branch-unknown-rule', branch('unknown')), ('branch-null-bool', branch('none', None)),
            ('split-cross-null', {**split(), 'estimateTimes': None}), ('merge-cross-null', {**merge(), 'segments': None}),
            ('reorder-cross-null', {**reorder(0, False), 'playheadUs': None}), ('line-cross-rule', {**resplit(['乙甲']), 'rule': None}),
            ('branch-cross-target', {**branch('none'), 'target': None}), ('target-unknown', split({**FIRST, 'extra': None}))]
        for name, command in invalid:
            self.rejected('shape-' + name, path, command, 400, 'invalidRequest')
        raw = json.dumps(reorder(0, False))[:-1] + ',"reassignTimeSlots":false}'
        self.rejected('shape-duplicate-bool', path, reorder(0, False), 400, 'invalidRequest', raw_command=raw)
        for count in (20000, 20001):
            self.rejected('segments-length-priority-' + str(count), path, resplit([1] * count),
                413 if count > 20000 else 400, 'resourceTooLarge' if count > 20000 else 'invalidRequest',
                field='command.segments' if count > 20000 else None)
        for name, command, failure, field in [
            ('target-stale', split({**FIRST, 'tokenId': 'missing'}), 'TargetStale', 'command.target'),
            ('branch-stale', branch('none', False, 'missing'), 'TargetStale', 'command.branchId'),
            ('segments-mismatch', resplit(['不等']), 'InvalidTarget', 'command.segments'),
            ('segments-empty-piece', resplit(['乙', '', '甲']), 'InvalidTarget', 'command.segments'),
            ('segments-empty-array', resplit([]), 'InvalidTarget', 'command.segments'),
            ('reorder-negative', reorder(-1, True), 'DomainValidationFailed', 'command'),
            ('reorder-past-end', reorder(9, True), 'DomainValidationFailed', 'command'),
            ('reorder-time-conflict', reorder(1, False), 'DomainValidationFailed', 'command')]:
            # IDs have been rebuilt by branch resplit; use its real current first token.
            if command['type'] == TYPES[2]:
                command['target'] = {**FIRST, 'tokenId': self.row(self.state('shape-current-' + name, path))['tokens'][0]['id']}
            self.rejected('domain-' + name, path, command, 422, 'commandRejected', failure, field=field)
        self.rejected('unknown-type', path, {'type': 'unknown.command'}, 422, 'commandNotSupported')

    def receipts_and_races(self, path):
        current = self.state('receipt-start', path)
        first = {**FIRST, 'tokenId': self.row(current)['tokens'][0]['id']}
        # Successful merge -> split -> manual merge -> branch and reorder no-op cover
        # every concrete derived field while replaying the original complete result.
        commands = [(merge(first), 'line.merge', [merge({**first, 'tokenId': 'missing'})]),
                    (split(first), 'line.split', [split({**first, 'lineId': 'two'})])]
        for index, (command, undo_name, different) in enumerate(commands):
            _, after, result, payload = self.accepted('receipt-token-' + str(index), path, command, undo_name=undo_name)
            self.receipt_check(path, 'token-' + str(index), after, result, payload, different)
        _, after, result, payload = self.accepted('receipt-segments', path, resplit(['乙甲']), undo_name='line.split-rule', counts=(0, 1, 0))
        frozen = copy.deepcopy(payload)
        payload['command']['segments'][0] = '不等'
        self.check(self.request('receipt-segments-frozen-retry', 'POST', path + '/commands', value=frozen) == result,
                   'HTTP admitted segments snapshot: later client-array mutation does not change original receipt')
        self.request('receipt-segments-mutated-reuse', 'POST', path + '/commands', value=payload, status=409, code='requestIdReuse')
        self.receipt_check(path, 'segments', after, result, frozen, [resplit(['乙', '甲']), resplit(['乙甲'], True)])
        _, after, result, payload = self.accepted('receipt-branch', path, branch('perTextElement', True), undo_name='lyrics.split-rule', counts=(0, 2, 2))
        self.receipt_check(path, 'branch', after, result, payload, [branch('mixed', True), branch('perTextElement', False), branch('perTextElement', True, 'empty')])
        first = {**FIRST, 'tokenId': self.row(after)['tokens'][0]['id']}
        _, after, result, payload = self.accepted('receipt-reorder-noop', path, reorder(0, False, first), changed=False)
        self.receipt_check(path, 'reorder', after, result, payload, [reorder(1, False, first), reorder(0, True, first)])
        revision = after['summary']['projectRevision']
        races = [self.payload('race-merge-' + str(i), revision, merge(first)) for i in range(2)]
        results = self.concurrent('token-same-revision', path, races)
        self.check(sorted(x[0] for x in results) == [200, 409] and next(x[2]['code'] for x in results if x[0] == 409) == 'revisionConflict', 'token concurrent intents: exactly one writer')
        now = self.state('token-race-after', path)
        self.check(now['history']['undoCount'] == after['history']['undoCount'] + 1, 'token race: one history entry')
        payload = self.payload('race-split-receipt', now['summary']['projectRevision'], split(first))
        results = self.concurrent('token-same-receipt', path, [payload, copy.deepcopy(payload)])
        self.check([x[0] for x in results] == [200, 200] and results[0][2] == results[1][2], 'token same receipt concurrent: same new token ID/result')
        latest = self.state('token-same-receipt-after', path)
        self.check(latest['history']['undoCount'] == now['history']['undoCount'] + 1 and len(self.row(latest)['tokens']) == 2,
                   'token same receipt: one ID allocation and history')

    def receipt_check(self, path, name, after, result, payload, differences):
        self.check(self.request('receipt-' + name + '-stale-normalized-retry', 'POST', path + '/commands',
            value={**payload, 'expectedRevision': '000' + payload['expectedRevision']}) == result, name + ': complete original token result before revision gate')
        for index, command in enumerate(differences):
            self.request('receipt-' + name + '-derived-reuse-' + str(index), 'POST', path + '/commands',
                value={**payload, 'command': command}, status=409, code='requestIdReuse')
        self.request('receipt-' + name + '-revision-reuse', 'POST', path + '/commands', value={**payload,
            'expectedRevision': str(int(payload['expectedRevision']) + 1)}, status=409, code='requestIdReuse')
        self.request('receipt-' + name + '-deadline-reuse', 'POST', path + '/commands', value={**payload,
            'validUntilUtc': content.timing.s1.smoke.existing.iso(content.timing.s1.smoke.existing.utc_now() + content.timing.s1.smoke.existing.timedelta(seconds=95))},
            status=409, code='requestIdReuse')
        self.same_state(after, self.state('receipt-' + name + '-stable', path), name + ': no second mutation/ID on receipt retry/reuse')

    def auth(self, path):
        before = self.state('token-auth-before', path)
        other = self.register('token-other-client', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        commands = [split(), merge(), reorder(0, False), resplit(['乙甲']), branch('none')]
        for command in commands:
            self.request('token-cross-client-' + command['type'].rsplit('.', 1)[1], 'POST', path + '/commands', client=other['clientId'],
                value=self.payload('cross-client', before['summary']['projectRevision'], command), status=404, code='sessionNotFound')
        self.request('token-missing-session', 'POST', '/api/v1/project-sessions/missing/commands',
            value=self.payload('missing-session', '0', split()), status=404, code='sessionNotFound')
        self.same_state(before, self.state('token-auth-after', path), 'token cross-client rejection')
        relative = self.fixture('token-readonly', self.token_fixture)
        handle = self.request('token-readonly-handle', 'POST', '/api/v1/file-handles', status=201, value={'rootGrantId': 'projects', 'relativePath': relative})
        opened = self.job('token-readonly-open', self.request('token-readonly-open', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId'], 'access': 'read'}))['result']
        readonly_path = '/api/v1/project-sessions/' + opened['projectSessionId']
        self.check(opened['isReadOnly'], 'token readonly actual read session')
        for command in commands:
            self.rejected('token-readonly-' + command['type'].rsplit('.', 1)[1], readonly_path, command, 403, 'readOnlySession')
        self.close('token-readonly-close', readonly_path, opened['projectRevision'])

    def simple_fixture(self, project, lyrics, rows):
        self.token_fixture(project, lyrics)
        lyrics['branches'] = [lyrics['branches'][0]]
        lyrics['branches'][0]['lines'] = rows

    @staticmethod
    def fixed_line(identifier, text, tokens, end=2000000):
        return {'id': identifier, 'text': text, 'startUs': 0, 'endUs': end, 'timingState': 'Imported', 'tokens': tokens}

    @staticmethod
    def fixed_token(identifier, text, start=None, end=None, status='Imported'):
        value = {'id': identifier, 'text': text, 'timingState': status if start is not None else 'Missing'}
        if start is not None:
            value.update(startUs=start, endUs=end)
        return value

    def close_roundtrip(self, name, path):
        path, opened = self.save_close_open(name, path, self.request(name + '-current', 'GET', path)['projectRevision'], name + '-saved')
        self.close(name + '-close', path, opened['projectRevision'])

    def unicode_and_rules(self):
        unicode = 'e\u0301👩🏽‍🚀'
        def fixture(project, lyrics):
            self.simple_fixture(project, lyrics, [self.fixed_line('one', unicode, [self.fixed_token('first', unicode, 1, 6)])])
        relative = self.fixture('token-unicode', fixture)
        path, opened = self.open('token-unicode-open', relative)
        self.rejected('unicode-boundary-split-combining', path, resplit(['e', '\u0301👩🏽‍🚀']), 422, 'commandRejected', 'InvalidTarget', field='command.segments')
        def verify_split(before, after, value):
            left, right = self.row(after)['tokens']
            self.check(left['text'] == 'e\u0301' and right['text'] == '👩🏽‍🚀' and left['id'] == 'first' and
                left['time'] == {'startUs': '1', 'endUs': '3'} and right['time'] == {'startUs': '3', 'endUs': '6'},
                'Unicode split: intact graphemes and ties-to-even 5/2 midpoint')
        self.exercise('unicode-grapheme-split', path, split(), undo_name='line.split', counts=(1, 1, 2), check=verify_split)
        self.history('unicode-restore-merged', path, 'undo')
        def first_inherits(before, after, value):
            left, right = self.row(after)['tokens']
            self.check(left['id'] == 'first' and left['time'] == {'startUs': '1', 'endUs': '6'} and left['timingStatus'] == 'imported' and
                       right['time'] == {} and right['timingStatus'] == 'missing', 'manual no-estimate: first piece inherits full old time, rest missing')
        self.exercise('unicode-manual-no-estimate', path, resplit(['e\u0301', '👩🏽‍🚀']), undo_name='line.split-rule', counts=(1, 1, 0), check=first_inherits)
        self.history('unicode-restore-before-estimate', path, 'undo')
        def estimated(before, after, value):
            left, right = self.row(after)['tokens']
            # UTF-16 lengths are 2 and 7; 5*2/9 rounds to1, added to start1.
            self.check(left['id'] != 'first' and right['id'] != 'first' and left['time'] == {'startUs': '1', 'endUs': '2'} and
                       right['time'] == {'startUs': '2', 'endUs': '6'} and all(t['timingStatus'] == 'estimated' for t in (left, right)),
                       'manual estimated: UTF-16 weights, new IDs and original endpoint preserved')
        self.exercise('unicode-manual-estimate', path, resplit(['e\u0301', '👩🏽‍🚀'], True), undo_name='line.split-rule', counts=(0, 2, 2), check=estimated)
        self.close_roundtrip('token-unicode', path)
        text = 'ab cd,ef'
        rules = {'mixed': ['ab ', 'cd,', 'ef'], 'perTextElement': list(text), 'latinWords': ['ab ', 'cd,', 'ef'],
                 'byPunctuation': ['ab cd,', 'ef'], 'byWhitespace': ['ab ', 'cd,ef']}
        def rule_fixture(project, lyrics):
            self.simple_fixture(project, lyrics, [self.fixed_line('one', text, [self.fixed_token('first', text, 100000, 900000)])])
        relative = self.fixture('token-rules', rule_fixture)
        path, opened = self.open('token-rules-open', relative)
        self.accepted('rule-none-noop', path, branch('none', True), changed=False)
        for rule, parts in rules.items():
            def verify(before, after, value, parts=parts):
                tokens = self.row(after)['tokens']
                self.check([t['text'] for t in tokens] == parts and self.row(after)['text'] == text and
                    tokens[0]['id'] == 'first' and tokens[0]['time'] == {'startUs': '100000', 'endUs': '900000'} and
                    all(t['time'] == {} for t in tokens[1:]), 'rule exact fixed segments/first inherited range')
            self.exercise('rule-' + rule, path, branch(rule), undo_name='lyrics.split-rule', counts=(1, len(parts) - 1, 0), check=verify)
            self.history('rule-' + rule + '-restore-baseline', path, 'undo')
        self.close_roundtrip('token-rules', path)

    def missing_and_status(self):
        def fixture(project, lyrics):
            self.simple_fixture(project, lyrics, [self.fixed_line('one', '甲乙丙', [self.fixed_token('first', '甲', 100000, 200000),
                self.fixed_token('second', '乙', 500000, 700000, 'Confirmed'), self.fixed_token('third', '丙')]),
                self.fixed_line('empty-line', '', [])])
        relative = self.fixture('token-missing-status', fixture)
        path, opened = self.open('token-missing-open', relative)
        self.exercise('merge-different-status', path, merge(), undo_name='line.merge',
            check=lambda before, after, value: self.check(self.row(after)['tokens'][0]['timingStatus'] == 'estimated' and
                self.row(after)['tokens'][0]['time'] == {'startUs': '100000', 'endUs': '700000'}, 'merge different states yields estimated union'))
        self.exercise('merge-with-missing', path, merge(), undo_name='line.merge',
            check=lambda before, after, value: self.check(self.row(after)['tokens'] == [{'id': 'first', 'text': '甲乙丙',
                'time': {}, 'timingStatus': 'missing'}], 'merge any missing source yields missing left identity'))
        self.accepted('split-untimed-noop', path, split(), changed=False)
        self.accepted('empty-line-segments-noop', path, resplit([], target={**LINE, 'lineId': 'empty-line'}), changed=False)
        self.history('missing-merge-restore', path, 'undo')
        self.exercise('reorder-preserve-missing-slot', path, reorder(0, True, {**FIRST, 'tokenId': 'third'}), undo_name='line.reorder',
            check=lambda before, after, value: self.check(self.row(after)['tokens'][0] == {'id': 'third', 'text': '丙', 'time': {}, 'timingStatus': 'missing'} and
                self.row(after)['tokens'][1]['time'] == self.row(before)['tokens'][0]['time'], 'reorder: untimed moved token stays missing, timed slot remains valid'))
        self.close_roundtrip('token-missing', path)

    def secondary_and_styles(self):
        def fixture(project, lyrics):
            self.token_fixture(project, lyrics)
            translated = lyrics['branches'][1]['lines'][0]
            translated['text'] = '译文'
            translated['tokens'][0]['text'] = '译文'
        relative = self.fixture('token-secondary-style', fixture)
        path, opened = self.open('token-secondary-style-open', relative)
        original = self.state('token-secondary-original', path)
        target = {'branchId': 'translation', 'lineId': 'translated'}
        self.exercise('token-secondary-resplit', path, resplit(['译', '文'], False, target), undo_name='line.split-rule', counts=(0, 2, 0),
            check=lambda before, after, value: self.check(self.rows(before) == self.rows(after) and
                self.rows(after, 'translation')[0]['primaryLineId'] == 'one' and self.rows(after, 'translation')[0]['time'] == {} and
                all(t['time'] == {} for t in self.rows(after, 'translation')[0]['tokens']) and
                after['models'][0]['branches'][1]['timingMode'] == 'followPrimary', 'secondary split: explicit target preserves primary and FollowPrimary raw-time semantics'))
        self.exercise('token-style-merge', path, merge(), undo_name='line.merge')
        self.exercise('token-style-split', path, split(), undo_name='line.split', counts=(1, 1, 2))
        path, opened = self.save_close_open('token-secondary-style-roundtrip', path,
            self.request('token-style-current', 'GET', path)['projectRevision'], 'token-secondary-style-saved')
        root = self.output / 'profile/projects/token-secondary-style-saved'
        project = json.loads((root / 'project.json').read_text())
        lyrics = json.loads((root / project['lyricsFile']).read_text())
        tokens = lyrics['branches'][0]['lines'][0]['tokens']
        self.check(tokens[0]['id'] == 'first' and tokens[0]['styleOverride'] == {'typography': {'fontSizePx': 31}, 'opacity': .8}
                   and 'styleOverride' not in tokens[1], 'persisted token style: split/merge retain left override, new right follows original default')
        self.check(self.rows(original)[0]['rubyAnnotations'] == self.get('style-ruby-read', path, '/lyrics/window', opened['projectRevision'],
                   branchId='primary', limit=1)['lines'][0]['rubyAnnotations'], 'persisted style fixture: original ruby references remain unchanged')
        self.close('token-secondary-style-close', path, opened['projectRevision'])

    def compact(self, name, path):
        summary = self.request(name + '-summary', 'GET', path)
        history = self.request(name + '-history', 'GET', path + '/history')
        cursor = self.request(name + '-client', 'GET', '/api/v1/clients/' + self.client + '/state')['eventCursor']
        # Existing unbounded compatibility read retains complete old rows where
        # new S1 windows deliberately reject oversized legacy input.
        legacy = self.request(name + '-legacy', 'GET', path + '/lyrics/lines?branchId=primary')
        return summary, history, cursor, legacy

    def specialized_budgets(self):
        for name, text, operation in [('items', '字' * 20001, branch('perTextElement')),
                                      ('bytes', 'a' * 530000, split())]:
            def fixture(project, lyrics, text=text):
                self.simple_fixture(project, lyrics, [self.fixed_line('one', text, [self.fixed_token('first', text, 0, 2000000)])])
            relative = self.fixture('token-budget-' + name, fixture)
            path, opened = self.open('token-budget-' + name + '-open', relative)
            before = self.compact('budget-' + name + '-before', path)
            self.request('budget-' + name + '-rejected', 'POST', path + '/commands', value=self.payload('budget-' + name, '0', operation), status=413, code='resourceTooLarge')
            self.check(self.compact('budget-' + name + '-after', path) == before, 'budget ' + name + ': exact complete old content and history/cursor unchanged')
            result = self.request('budget-' + name + '-repair-delete', 'POST', path + '/commands', value=self.payload('repair-' + name, '0', content.remove()))
            self.check(result['changed'] and result['feedback']['selection'] == {'branchId': 'primary', 'hasSelection': False}, 'delete oversized legacy row remains available')
            self.close_roundtrip('token-budget-' + name + '-repair', path)
        def items_exact(project, lyrics):
            self.simple_fixture(project, lyrics, [self.fixed_line('one', '字' * 20000, [self.fixed_token('first', '字' * 20000, 0, 2000000)])])
        relative = self.fixture('token-final-item-count', items_exact)
        path, opened = self.open('token-final-items-open', relative)
        self.rejected('segments-20000-but-row-20001-items', path, resplit(['字'] * 20000), 413, 'resourceTooLarge', field='command.segments')
        self.close('token-final-items-close', path, opened['projectRevision'])
        def long_merged(project, lyrics):
            text = 'a' * 40000
            self.simple_fixture(project, lyrics, [self.fixed_line('one', text, [self.fixed_token('first', text, 0, 2000000)])])
        relative = self.fixture('token-long-merged', long_merged)
        path, opened = self.open('token-long-merged-open', relative)
        self.exercise('long-merged-split', path, split(), undo_name='line.split', counts=(1, 1, 2),
            check=lambda before, after, value: self.check([len(t['text']) for t in self.row(after)['tokens']] == [20000, 20000], 'long merged: count actual new tokens, not graphemes'))
        self.exercise('long-merged-latinWords', path, branch('latinWords'), undo_name='lyrics.split-rule', counts=(0, 1, 0))
        self.close_roundtrip('token-long-merged', path)
        def cumulative(project, lyrics):
            text = 'a' * 240000 + ' b'
            self.simple_fixture(project, lyrics, [self.fixed_line('large-' + str(i), text,
                [self.fixed_token('large-token-' + str(i), text, 0, 2000000)]) for i in range(36)])
        relative = self.fixture('token-branch-16mib', cumulative)
        path, opened = self.open('token-branch-16mib-open', relative)
        # A real redo exists before resource rejection, without modifying this
        # branch's content, so the full no-mutation state can be compared.
        revision = self.request('cumulative-current', 'GET', path)['projectRevision']
        self.request('cumulative-setup-edit', 'POST', path + '/commands', value=self.payload('cumulative-setup', revision, content.canvas(400, 200)))
        self.history('cumulative-setup-undo', path, 'undo')
        self.accepted('cumulative-none-noop', path, branch('none'), changed=False)
        self.rejected('cumulative-16mib-reject', path, branch('latinWords'), 413, 'resourceTooLarge', field='command')
        self.exercise('cumulative-one-line-succeeds', path, resplit(['a' * 240000, ' ', 'b'], target={**LINE, 'lineId': 'large-0'}),
                      undo_name='line.split-rule', counts=(1, 2, 0))
        self.close_roundtrip('token-cumulative', path)

    def finish(self):
        self.check(bool(self.worker_identities), 'actual Worker preparation identities observed')
        self.stop('token')
        self.check(len(self.samples) == self.request_count, 'every HTTP call has a local short elapsed sample')
        save_json(self.output / 'http-elapsed.json', {'samples': self.samples,
            'scope': 'single local short samples including client validation/log overhead; no allocation/performance/throughput/platform guarantee'})
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
            'scope': 'S3c five actual HTTP token commands; owned fixtures/read/history/persistence; HTTP snapshot replay does not reproduce internal queued-array mutation; excludes browser/whole-render/performance/platform acceptance'})
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), label)
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))

    def run(self):
        self.start('token')
        self.connect('token')
        capabilities = self.request('token-capabilities', 'GET', '/api/v1/capabilities')
        for command in TYPES:
            self.check('project-sessions.commands.' + command in capabilities['features'], 'advertised S3c command ' + command)
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000); wav.writeframes(b'\x00\x00' * 16000)
        assets = []
        for name, data, media in (('token.wav', buf.getvalue(), 'audio/wav'), ('token.lrc', '[00:00.00]甲乙\n'.encode(), 'text/plain')):
            assets.append(self.request('upload-' + name, 'POST', '/api/v1/assets?fileName=' + name, raw=data, content_type=media, status=201,
                headers={'X-Mako-Request-Id': 'upload-' + name, 'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']}))
        created = self.request('create-template', 'POST', '/api/v1/project-sessions', status=201,
            value={'name': 'S3c token HTTP probe', 'audioAssetId': assets[0]['assetId'], 'lyricsAssetId': assets[1]['assetId']})
        path = '/api/v1/project-sessions/' + created['projectSessionId']
        path, opened = self.save_close_open('token-template', path, created['projectRevision'], 'editing-original')
        self.close('token-template-close', path, opened['projectRevision'])
        relative = self.fixture('token-normal', self.token_fixture)
        path, opened = self.open('token-normal-open', relative)
        self.core_sequence(path)
        self.malformed(path)
        self.receipts_and_races(path)
        self.auth(path)
        self.close_roundtrip('token-normal', path)
        # Inspect source token style in the probe-owned persisted closure because
        # S1 intentionally excludes style values from the editable projection.
        saved = self.output / 'profile/projects/token-normal-saved'
        project = json.loads((saved / 'project.json').read_text())
        lyrics = json.loads((saved / project['lyricsFile']).read_text())
        self.check(lyrics['branches'][0]['lines'][0]['rubyAnnotations'][1]['baseTokenId'] == 'second', 'persisted closure: original dangling ruby is not automatically rebound')
        self.unicode_and_rules()
        self.missing_and_status()
        self.secondary_and_styles()
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
    probe = TokenProbe(args.output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
