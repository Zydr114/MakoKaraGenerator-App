#!/usr/bin/env python3
"""S2 timing commands over real HTTP, using only this probe's profile and saved fixtures."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import copy
import importlib.util
import io
import json
from pathlib import Path
import threading
import time
import urllib.error
import urllib.request
import wave

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('s1_editing_probe', HERE / 'probe.py')
s1 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s1)
save_json = s1.save_json
FIRST = {'branchId': 'primary', 'lineId': 'one', 'tokenId': 'first'}
SECOND = {**FIRST, 'tokenId': 'second'}
THIRD = {**FIRST, 'tokenId': 'third'}
LINE = {'branchId': 'primary', 'lineId': 'one'}


def stamp(which, target, playhead):
    return {'type': 'lyrics.stampToken' + which, 'target': target, 'playheadUs': str(playhead)}


def nudge(target, frames):
    return {'type': 'lyrics.nudgeTokenTime', 'target': target, 'frameDelta': frames}


def shift(delta):
    return {'type': 'lyrics.shiftLine', 'target': LINE, 'deltaUs': str(delta)}


def clear(target):
    return {'type': 'lyrics.clearTokenTime', 'target': target}


def change(target, start, end):
    return {'target': target, 'startUs': str(start), 'endUs': str(end)}


def batch(*changes):
    return {'type': 'lyrics.setTokenTimes', 'changes': list(changes)}


class TimingProbe(s1.EditingProbe):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.samples = []
        self.command_counter = 0

    def request(self, name, method, path, **kwargs):
        started = time.perf_counter()
        try:
            return super().request(name, method, path, **kwargs)
        finally:
            self.samples.append({'name': name, 'method': method, 'path': path,
                                 'elapsedMs': round((time.perf_counter() - started) * 1000, 3)})

    def payload(self, name, revision, command):
        self.command_counter += 1
        return self.command(revision, f's2-{self.command_counter}-{name}', command)

    def state(self, name, path):
        summary = self.request(name + '-summary', 'GET', path)
        history = self.request(name + '-history', 'GET', path + '/history')
        models = self.content(name, path, summary['projectRevision'])
        client = self.request(name + '-client', 'GET', '/api/v1/clients/' + self.client + '/state')
        self.check(history['projectRevision'] == summary['projectRevision'], name + ': history exact revision')
        return {'summary': summary, 'history': history, 'models': models, 'eventCursor': client['eventCursor']}

    def same_state(self, before, after, name):
        self.check(after == before, name + ': exact projection/summary/history/dirty/redo/cursor preserved')

    def same_content(self, before, after, name):
        self.check(self.normalize_content(before['models']) == self.normalize_content(after['models']),
                   name + ': all original lyric/source/ruby/settings values exact')

    @staticmethod
    def line(state, branch='primary'):
        return state['models'][2][branch]['lines'][0]

    def token(self, state, identifier, start=None, end=None, status=None, branch='primary'):
        value = next(x for x in self.line(state, branch)['tokens'] if x['id'] == identifier)
        self.check(value['time'] == ({} if start is None else {'startUs': str(start), 'endUs': str(end)}),
                   identifier + ': exact string microseconds or missing object')
        if status is not None:
            self.check(value['timingStatus'] == status, identifier + ': exact original time status ' + status)
        return value

    def accepted(self, name, path, command, *, changed=True, undo_name=None, estimated=None,
                 check=None, expected_tokens=None, full_refresh=False):
        before = self.state(name + '-before', path)
        revision = before['summary']['projectRevision']
        payload = self.payload(name, revision, command)
        result = self.request(name, 'POST', path + '/commands', value=payload)
        after = self.state(name + '-after', path)
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1], name + ': response session identity')
        self.check(result['previousRevision'] == revision and result['projectRevision'] == str(int(revision) + int(changed)),
                   name + ': exact previous/current revision')
        self.check(result['projectRevision'] == after['summary']['projectRevision'] and result['changed'] is changed,
                   name + ': changed agrees with current read model')
        self.check('failureCode' not in result, name + ': accepted response omits failureCode')
        if estimated is None:
            self.check('timingResult' not in result, name + ': non-stamp omits timingResult')
        else:
            self.check(result.get('timingResult') == {'estimatedPlaceholder': estimated}, name + ': actual stamp timingResult')
        if changed:
            self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and after['history']['redoCount'] == 0,
                       name + ': one HTTP action creates exactly one independent undo')
            self.check(result['undoName'] == undo_name and after['history']['undoName'] == undo_name,
                       name + ': shared Application history name exact')
            self.check(set(result['changeSet']['areas']) == {'lyricsTiming', 'timeline', 'renderInputs'} and
                       result['changeSet']['requiresFullRefresh'] is full_refresh, name + ': bounded time change areas and refresh policy')
            if full_refresh:
                self.full_refresh(result, name)
            if expected_tokens is not None:
                self.check(result['changeSet']['tokenIds'] == expected_tokens, name + ': exact affected token IDs/order')
            self.check(after['eventCursor'] != before['eventCursor'] and result['isDirty'], name + ': real change publishes event and dirty')
        else:
            self.same_state(before, after, name)
            self.check(result['changeSet'] == {'areas': [], 'branchIds': [], 'lineIds': [], 'tokenIds': [], 'requiresFullRefresh': False},
                       name + ': no-op has no changeSet')
        if check:
            check(after)
        return before, after, result, payload

    def history(self, name, path, action, *, full_refresh=False):
        summary = self.request(name + '-current', 'GET', path)
        # Preserve the known nullable fields accepted by the old five command wire forms.
        command = {'type': 'history.' + action, 'target': None, 'startUs': None, 'endUs': None, 'analysisJobId': None}
        result = self.request(name, 'POST', path + '/commands', value=self.payload(name, summary['projectRevision'], command))
        self.check(result['changed'] and 'timingResult' not in result and 'failureCode' not in result,
                   name + ': legacy history accepted with omitted new result fields')
        self.check(result['projectRevision'] == str(int(summary['projectRevision']) + 1), name + ': monotonic history revision')
        if full_refresh:
            self.full_refresh(result, name)
        return self.state(name + '-state', path)

    def exercise(self, name, path, command, **kwargs):
        before, after, result, payload = self.accepted(name, path, command, **kwargs)
        undone = self.history(name + '-undo', path, 'undo')
        self.same_content(before, undone, name + '-undo')
        self.check(undone['history']['undoCount'] == before['history']['undoCount'] and undone['history']['redoCount'] == 1,
                   name + ': one undo restores entire preceding state')
        redone = self.history(name + '-redo', path, 'redo')
        self.same_content(after, redone, name + '-redo')
        self.check(redone['history']['undoCount'] == after['history']['undoCount'] and redone['history']['redoCount'] == 0,
                   name + ': one redo restores entire committed state')
        return redone

    def rejected(self, name, path, command, status, code, failure=None, *, raw_command=None, legacy_field=None):
        before = self.state(name + '-before', path)
        payload = self.payload(name, before['summary']['projectRevision'], command)
        if raw_command is None:
            result = self.request(name, 'POST', path + '/commands', value=payload, status=status, code=code)
        else:
            prefix = json.dumps({k: v for k, v in payload.items() if k != 'command'})
            data = (prefix[:-1] + ',"command":' + raw_command + '}').encode('utf-8')
            save_json(self.output / (name + '-raw-command.json'), {'command': raw_command, 'scope': 'probe-owned malformed command only'})
            result = self.request(name, 'POST', path + '/commands', raw=data, content_type='application/json', status=status, code=code)
        if failure is None:
            self.check('failureCode' not in result, name + ': non-domain/legacy rejection omits failureCode')
        else:
            self.check(result.get('failureCode') == failure, name + ': stable Application failureCode ' + failure)
            if command['type'] == 'lyrics.setTokenTimes':
                expected_field = 'command.changes'
            elif failure in ('Untimed', 'TargetStale'):
                expected_field = 'command.target'
            else:
                expected_field = {'lyrics.stampTokenStart': 'command.playheadUs', 'lyrics.stampTokenEnd': 'command.playheadUs',
                                  'lyrics.nudgeTokenTime': 'command.frameDelta', 'lyrics.shiftLine': 'command.deltaUs'}[command['type']]
            self.check(result.get('field') == expected_field and result['field'] != failure,
                       name + ': domain field is actual request path ' + expected_field)
        if legacy_field is not None:
            self.check(result.get('field') == legacy_field, name + ': legacy domain field remains unchanged')
        self.check('timingResult' not in result, name + ': rejection omits timingResult')
        self.same_state(before, self.state(name + '-after', path), name)

    def timing_fixture(self, project, lyrics, *, extended=False):
        line_end = 6000000 if extended else 1500000
        def token(identifier, text, start=None, end=None):
            result = {'id': identifier, 'text': text, 'timingState': 'Missing' if start is None else 'Imported'}
            if start is not None:
                result.update(startUs=start, endUs=end)
            return result
        one = {'id': 'one', 'text': '甲乙丙', 'startUs': 0, 'endUs': line_end, 'timingState': 'Imported',
               'tokens': [token('first', '甲', 100000, 200000), token('second', '乙', 500000, 700000), token('third', '丙')],
               'rubyAnnotations': [{'id': 'timing-ruby', 'baseTokenId': 'first', 'text': 'こう', 'language': 'ja-JP',
                                    'relativeScale': .6, 'gapPx': 2.5, 'alignment': 'Center'}]}
        lyrics['primaryBranchId'] = 'primary'
        lyrics['branches'] = [{'id': 'primary', 'name': '主歌词', 'role': 'Primary', 'language': 'ja-JP',
                               'timingMode': 'Independent', 'lines': [one]}]
        if not extended:
            lyrics['branches'].extend([
                {'id': 'translation', 'name': '翻译', 'role': 'Translation', 'language': 'zh-CN', 'timingMode': 'FollowPrimary',
                 'lines': [{'id': 'translated', 'primaryLineId': 'one', 'text': '译', 'startUs': 0, 'endUs': line_end,
                            'timingState': 'Imported', 'tokens': [token('translated-token', '译', 100000, 200000)]}]},
                {'id': 'bulk', 'name': '1000个元素', 'role': 'Custom', 'timingMode': 'Independent',
                 'lines': [{'id': 'bulk-line', 'text': 'a' * 1000, 'startUs': 0, 'endUs': 1500000, 'timingState': 'Imported',
                            'tokens': [token('bulk-' + str(i), 'a', i * 1000, i * 1000 + 500) for i in range(1000)]}]}])
        project['composition'].update(canvasWidth=320, canvasHeight=180,
            frameRate={'numerator': 30, 'denominator': 1}, backgroundColor='#010203FF')

    def receipts(self, path):
        cases = [
            ('stamp-start', stamp('Start', FIRST, '300003'), stamp('Start', FIRST, '+0300003'), stamp('Start', FIRST, '300004'), 'token.stamp', False),
            ('stamp-end', stamp('End', FIRST, '400009'), stamp('End', FIRST, '+0400009'), stamp('End', FIRST, '400010'), 'token.stamp', False),
            ('nudge', nudge(FIRST, 1), nudge(FIRST, 1), nudge(FIRST, 2), 'token.time', None),
            ('shift', shift('1'), shift('+01'), shift('2'), 'line.shift', None),
            ('batch', batch(change(FIRST, '350001', '450007')), batch(change(FIRST, '+0350001', '+0450007')),
             batch(change(FIRST, '350001', '450008')), 'token.time', None),
            ('clear', clear(FIRST), clear(FIRST), clear(SECOND), 'token.time.clear', None)]
        for name, command, same, different, undo_name, estimated in cases:
            _, after, original, payload = self.accepted('receipt-' + name, path, command, undo_name=undo_name, estimated=estimated)
            retry = {**payload, 'expectedRevision': '000' + payload['expectedRevision'], 'command': same}
            result = self.request('receipt-' + name + '-normalized-stale-retry', 'POST', path + '/commands', value=retry)
            self.check(result == original, name + ': original receipt returned before stale revision gate with canonical microseconds')
            self.request('receipt-' + name + '-derived-field-reuse', 'POST', path + '/commands',
                         value={**payload, 'command': different}, status=409, code='requestIdReuse')
            self.request('receipt-' + name + '-new-revision-reuse', 'POST', path + '/commands',
                         value={**payload, 'expectedRevision': after['summary']['projectRevision']}, status=409, code='requestIdReuse')
            different_deadline = s1.smoke.existing.iso(s1.smoke.existing.utc_now() + s1.smoke.existing.timedelta(seconds=95))
            self.request('receipt-' + name + '-deadline-reuse', 'POST', path + '/commands',
                         value={**payload, 'validUntilUtc': different_deadline}, status=409, code='requestIdReuse')
            self.same_state(after, self.state('receipt-' + name + '-stable', path), name + ': receipt replay/reuse')

    def malformed(self, path):
        cases = [
            ('missing-target', stamp('Start', None, '1')),
            ('missing-token', clear(LINE)), ('empty-id', clear({**FIRST, 'tokenId': ''})),
            ('long-id', clear({**FIRST, 'tokenId': 't' * 129})),
            ('line-has-token', {'type': 'lyrics.shiftLine', 'target': FIRST, 'deltaUs': '1'}),
            ('missing-playhead', {'type': 'lyrics.stampTokenStart', 'target': FIRST}),
            ('numeric-playhead', {'type': 'lyrics.stampTokenStart', 'target': FIRST, 'playheadUs': 1}),
            ('bool-playhead', {'type': 'lyrics.stampTokenEnd', 'target': FIRST, 'playheadUs': True}),
            ('fraction-playhead', stamp('Start', FIRST, '1.1')),
            ('overflow-playhead', stamp('Start', FIRST, '9223372036854775808')),
            ('underflow-playhead', stamp('Start', FIRST, '-9223372036854775809')),
            ('space-playhead', stamp('Start', FIRST, ' 1')),
            ('missing-frames', {'type': 'lyrics.nudgeTokenTime', 'target': FIRST}),
            ('string-frames', nudge(FIRST, '1')), ('fraction-frames', nudge(FIRST, 1.5)),
            ('overflow-frames', nudge(FIRST, 2147483648)), ('bool-frames', nudge(FIRST, True)),
            ('numeric-shift', {'type': 'lyrics.shiftLine', 'target': LINE, 'deltaUs': 1}),
            ('overflow-shift', shift('9223372036854775808')),
            ('stamp-cross-null', {**stamp('Start', FIRST, '1'), 'analysisJobId': None}),
            ('end-cross-delta', {**stamp('End', FIRST, '1'), 'deltaUs': None}),
            ('clear-cross-null', {**clear(FIRST), 'startUs': None}),
            ('nudge-cross-playhead', {**nudge(FIRST, 1), 'playheadUs': '1'}),
            ('shift-cross-end', {**shift('1'), 'endUs': None}),
            ('extra-target', clear({**FIRST, 'extra': 1})),
            ('unknown-property', {**clear(FIRST), 'extra': True}),
            ('batch-null', {'type': 'lyrics.setTokenTimes', 'changes': None}),
            ('batch-empty', batch()), ('batch-null-item', batch(None)),
            ('batch-1001', batch(*[change(FIRST, 1, 2)] * 1001)),
            ('batch-duplicate', batch(change(FIRST, 1, 2), change(FIRST, 3, 4))),
            ('batch-line-target', batch(change(LINE, 1, 2))),
            ('batch-missing-end', batch({'target': FIRST, 'startUs': '1'})),
            ('batch-numeric-end', batch({'target': FIRST, 'startUs': '1', 'endUs': 2})),
            ('batch-overflow-end', batch(change(FIRST, '1', '9223372036854775808'))),
            ('batch-extra-item', batch({**change(FIRST, 1, 2), 'extra': None})),
            ('batch-cross-target', {**batch(change(FIRST, 1, 2)), 'target': None}),
        ]
        for name, command in cases:
            self.rejected('shape-' + name, path, command, 400, 'invalidRequest')
        raw = '{"type":"lyrics.clearTokenTime","type":"lyrics.clearTokenTime","target":' + json.dumps(FIRST) + '}'
        self.rejected('shape-duplicate-type', path, clear(FIRST), 400, 'invalidRequest', raw_command=raw)
        raw = '{"type":"lyrics.stampTokenStart","target":' + json.dumps(FIRST) + ',"playheadUs":"1","playheadUs":"1"}'
        self.rejected('shape-duplicate-playhead', path, clear(FIRST), 400, 'invalidRequest', raw_command=raw)
        self.rejected('unknown-type', path, {'type': 'unknown.command', 'target': None,
                      'startUs': None, 'endUs': None, 'analysisJobId': None}, 422, 'commandNotSupported')

    def domain_failures(self, path):
        state = self.state('domain-baseline', path)
        second = next(x for x in self.line(state)['tokens'] if x['id'] == 'second')
        second_start = int(second['time']['startUs'])
        cases = [
            ('end-untimed', stamp('End', THIRD, 1), 'Untimed'),
            ('nudge-untimed', nudge(THIRD, 1), 'Untimed'),
            ('start-media-end', stamp('Start', THIRD, 2000000), 'BeyondContentDuration'),
            ('end-invalid-range', stamp('End', SECOND, second_start), 'InvalidRange'),
            ('nudge-negative', nudge(SECOND, -100), 'NudgeOutOfBounds'),
            ('nudge-intmin', nudge(SECOND, -2147483648), 'NudgeOutOfBounds'),
            ('shift-overflow', shift('9223372036854775807'), 'DomainValidationFailed'),
            ('shift-underflow', shift('-9223372036854775808'), 'DomainValidationFailed'),
            ('shift-media-bound', shift(1000000), 'BeyondContentDuration'),
            ('batch-negative', batch(change(FIRST, -1, 1)), 'DomainValidationFailed'),
            ('batch-equal', batch(change(FIRST, 1, 1)), 'DomainValidationFailed'),
            ('batch-outside-line', batch(change(FIRST, 0, 10)), 'DomainValidationFailed'),
            ('batch-overlap', batch(change(FIRST, second_start + 1, second_start + 10)), 'DomainValidationFailed'),
            ('start-overlap', stamp('Start', FIRST, second_start + 1), 'DomainValidationFailed'),
            ('end-outside-line', stamp('End', SECOND, 1900000), 'DomainValidationFailed'),
            ('missing-target', clear({**FIRST, 'tokenId': 'not-present'}), 'TargetStale'),
            ('batch-second-invalid', batch(change(FIRST, 300001, 400007), change(SECOND, 350001, 450007)), 'DomainValidationFailed'),
        ]
        for name, command, failure in cases:
            self.rejected('domain-' + name, path, command, 422, 'commandRejected', failure)
        self.rejected('legacy-domain-omits-failure-code', path,
            {'type': 'lyrics.setTimeRange', 'target': SECOND, 'startUs': '-1', 'endUs': '10', 'analysisJobId': None},
            422, 'commandRejected', legacy_field='DomainValidationFailed')
        # Both final times are valid, but the first candidate in this input order overlaps.
        first_start, first_end = second_start + 1, second_start + 50000
        second_new_start, second_new_end = second_start + 300000, second_start + 400000
        self.rejected('batch-input-order-reject', path,
            batch(change(FIRST, first_start, first_end), change(SECOND, second_new_start, second_new_end)),
            422, 'commandRejected', 'DomainValidationFailed')
        self.exercise('batch-reverse-order-accept', path,
            batch(change(SECOND, second_new_start, second_new_end), change(FIRST, first_start, first_end)), undo_name='token.time',
            check=lambda state: (self.token(state, 'first', first_start, first_end, 'edited'),
                                 self.token(state, 'second', second_new_start, second_new_end, 'edited')))

    def concurrent(self, name, path, payloads):
        barrier = threading.Barrier(len(payloads))
        def send(payload):
            body = json.dumps(payload, ensure_ascii=False).encode('utf-8')
            request = urllib.request.Request(self.base_url + path + '/commands', data=body, method='POST', headers={
                'Content-Type': 'application/json', 'Authorization': 'Bearer ' + self.tokens[self.client],
                'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host})
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            barrier.wait(timeout=10)
            started = time.perf_counter()
            try:
                response = opener.open(request, timeout=30)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                result = json.loads(response.read())
                return response.status, dict(response.headers.items()), result, round((time.perf_counter() - started) * 1000, 3)
        with ThreadPoolExecutor(max_workers=len(payloads)) as pool:
            futures = [pool.submit(send, payload) for payload in payloads]
            results = [future.result() for future in futures]
        for index, (payload, (status, headers, result, elapsed)) in enumerate(zip(payloads, results)):
            self.request_count += 1
            prefix = f'{self.request_count:03d}-{name}-{index}'
            save_json(self.output / (prefix + '.request.json'), {'method': 'POST', 'path': path + '/commands',
                      'headers': {'Authorization': '<redacted>', 'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host},
                      'json': self.redact(payload), 'concurrencyGroup': name})
            save_json(self.output / (prefix + '.response.json'), {'status': status, 'headers': headers,
                      'json': self.redact(result), 'elapsedMs': elapsed, 'concurrencyGroup': name})
            self.samples.append({'name': name + '-' + str(index), 'method': 'POST', 'path': path + '/commands', 'elapsedMs': elapsed})
            self.check(headers['Content-Type'].split(';')[0] == ('application/json' if status == 200 else 'application/problem+json'),
                       name + ': concurrent media type')
            self.check(result.get('protocolVersion') == '1' and 'timingResult' not in result and 'failureCode' not in result,
                       name + ': concurrent protocol and omitted optional fields')
        return results

    def concurrency(self, path):
        before = self.state('race-before', path)
        revision = before['summary']['projectRevision']
        original = next(x for x in self.line(before)['tokens'] if x['id'] == 'first')['time']
        start, end = int(original['startUs']), int(original['endUs'])
        payloads = [self.payload('race-' + str(i), revision, nudge(FIRST, i + 1)) for i in range(2)]
        results = self.concurrent('different-request-race', path, payloads)
        self.check(sorted(x[0] for x in results) == [200, 409], 'same revision distinct IDs: exactly one accepted writer')
        winning = next(i for i, result in enumerate(results) if result[0] == 200)
        self.check(next(result[2]['code'] for result in results if result[0] == 409) == 'revisionConflict',
                   'same revision losing writer: stable revisionConflict')
        after = self.state('race-after', path)
        delta = 33333 * (winning + 1)
        # TimeUs rounds each requested frame delta once; two frames at 30fps are 66667us.
        if winning == 1:
            delta = 66667
        self.token(after, 'first', start + delta, end + delta, 'edited')
        self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and
                   after['summary']['projectRevision'] == str(int(revision) + 1), 'race: one mutation/history/revision')
        payload = self.payload('same-receipt-race', after['summary']['projectRevision'], nudge(FIRST, 1))
        results = self.concurrent('same-request-race', path, [payload, copy.deepcopy(payload)])
        self.check([x[0] for x in results] == [200, 200] and results[0][2] == results[1][2], 'same request concurrent: identical complete receipt')
        latest = self.state('same-receipt-race-after', path)
        self.check(latest['summary']['projectRevision'] == str(int(revision) + 2) and
                   latest['history']['undoCount'] == after['history']['undoCount'] + 1, 'same receipt race mutates exactly once')
        self.token(latest, 'first', start + delta + 33333, end + delta + 33333, 'edited')

    def bulk(self, path):
        targets = [{'branchId': 'bulk', 'lineId': 'bulk-line', 'tokenId': 'bulk-' + str(i)} for i in range(1000)]
        changes = [change(target, i * 1000 + 10, i * 1000 + 510) for i, target in enumerate(targets)]
        def verify(state):
            for i, token in enumerate(self.line(state, 'bulk')['tokens']):
                self.check(token['id'] == 'bulk-' + str(i) and token['time'] == {'startUs': str(i * 1000 + 10), 'endUs': str(i * 1000 + 510)}
                           and token['timingStatus'] == 'edited', '1000 batch: complete ordered token ' + str(i))
        self.exercise('batch-limit-1000', path, batch(*changes), undo_name='token.time', check=verify,
                      expected_tokens=[target['tokenId'] for target in targets])
        before, after, _, _ = self.accepted('batch-second-request', path,
            batch(*[change(target, i * 1000 + 20, i * 1000 + 520) for i, target in enumerate(targets)]), undo_name='token.time')
        undone = self.history('batch-second-request-undo', path, 'undo')
        self.same_content(before, undone, 'batch requests never merge')
        self.accepted('batch-noop-preserves-redo', path, batch(*changes), changed=False)
        redone = self.history('batch-second-request-redo', path, 'redo')
        self.same_content(after, redone, 'batch second redo')

    def unicode_fixture(self, project, lyrics):
        self.timing_fixture(project, lyrics, extended=True)
        project['name'] = 'S2 timing Unicode ID fixture'
        lyrics['branches'][0]['lines'] = [{'id': 'one', 'text': '字' * 100, 'startUs': 0, 'endUs': 1500000,
            'timingState': 'Imported', 'tokens': [{'id': '字' * 124 + f'{i:04d}', 'text': '字',
            'startUs': i * 1000, 'endUs': i * 1000 + 500, 'timingState': 'Imported'} for i in range(100)]}]

    def full_refresh(self, result, name):
        value = result['changeSet']
        self.check(value == {'areas': ['lyricsTiming', 'timeline', 'renderInputs'], 'branchIds': [],
                             'lineIds': [], 'tokenIds': [], 'requiresFullRefresh': True},
                   name + ': retain all change areas and empty every ID array on full invalidation')
        self.check(len(json.dumps(value, ensure_ascii=True, separators=(',', ':')).encode()) <= 16 * 1024,
                   name + ': bounded changeSet fits public 16KiB budget')

    def unicode(self, path):
        ids = ['字' * 124 + f'{i:04d}' for i in range(100)]
        complete = {'areas': ['lyricsTiming', 'timeline', 'renderInputs'], 'branchIds': ['primary'],
                    'lineIds': ['one'], 'tokenIds': ids, 'requiresFullRefresh': False}
        complete_bytes = len(json.dumps(complete, ensure_ascii=True, separators=(',', ':')).encode())
        self.check(complete_bytes > 16 * 1024, 'Unicode complete escaped changeSet exceeds public 16KiB budget')
        save_json(self.output / 'unicode-change-set-budget.json', {
            'targetCount': len(ids), 'idCharacterCount': 128, 'maxChangeSetBytes': 16 * 1024,
            'completeDefaultEscapedJsonBytes': complete_bytes,
            'scope': 'independently constructed complete changeSet using default JSON ASCII escaping; actual response must request full refresh with no partial IDs'})
        command = batch(*[change({**FIRST, 'tokenId': identifier}, i * 1000 + 10, i * 1000 + 510)
                          for i, identifier in enumerate(ids)])
        def verify(state):
            tokens = self.line(state)['tokens']
            self.check(len(tokens) == 100, 'Unicode: no token omitted from actual read model')
            for i, token in enumerate(tokens):
                self.check(token['id'] == ids[i] and token['text'] == '字' and token['timingStatus'] == 'edited' and
                           token['time'] == {'startUs': str(i * 1000 + 10), 'endUs': str(i * 1000 + 510)},
                           'Unicode batch: complete exact token ' + str(i))
        before, after, receipt, payload = self.accepted('unicode-batch', path, command, undo_name='token.time',
                                                       full_refresh=True, check=verify)
        replay = self.request('unicode-receipt-stale-replay', 'POST', path + '/commands', value=payload)
        self.check(replay == receipt, 'Unicode bounded successful receipt replays unchanged before revision gate')
        self.same_state(after, self.state('unicode-after-replay', path), 'Unicode replay')
        undone = self.history('unicode-undo', path, 'undo', full_refresh=True)
        self.same_content(before, undone, 'Unicode atomic undo')
        self.check(undone['history']['undoCount'] == before['history']['undoCount'] and undone['history']['redoCount'] == 1,
                   'Unicode: exactly one undo history entry')
        redone = self.history('unicode-redo', path, 'redo', full_refresh=True)
        self.same_content(after, redone, 'Unicode atomic redo')
        verify(redone)

    def extended(self, path):
        self.exercise('batch-beyond-real-audio', path, batch(change(THIRD, 4000001, 4500007)), undo_name='token.time',
            check=lambda state: (self.token(state, 'third', 4000001, 4500007, 'edited'),
                                 self.check(state['models'][1]['audioDurationUs'] == '2000000', 'batch: actual media duration remains 2s')))
        self.rejected('nudge-beyond-real-audio', path, nudge(THIRD, 1), 422, 'commandRejected', 'NudgeOutOfBounds')
        self.rejected('legacy-beyond-real-audio', path, {'type': 'lyrics.setTimeRange', 'target': THIRD,
            'startUs': '4000001', 'endUs': '4500007'}, 422, 'commandRejected', legacy_field='BeyondContentDuration')
        self.exercise('prepare-clamped-end', path, batch(change(THIRD, 1500001, 1700007)), undo_name='token.time')
        self.exercise('stamp-end-clamps-real-audio', path, stamp('End', THIRD, 999999999), undo_name='token.stamp', estimated=False,
                      check=lambda state: self.token(state, 'third', 1500001, 2000000, 'edited'))
        self.exercise('stamp-start-clamps-negative', path, stamp('Start', FIRST, -999999), undo_name='token.stamp', estimated=False,
                      check=lambda state: self.token(state, 'first', 0, 200000, 'edited'))
        self.rejected('stamp-start-no-room-at-media-end', path, stamp('Start', THIRD, 999999999),
                      422, 'commandRejected', 'BeyondContentDuration')
        self.rejected('extended-line-shift-media-bound', path, shift(1), 422, 'commandRejected', 'BeyondContentDuration')

    def finish(self):
        self.check(bool(self.worker_identities), 'actual Worker preparation identities observed')
        self.stop('timing')
        self.check(len(self.samples) == self.request_count, 'every HTTP call has a local elapsed sample')
        save_json(self.output / 'http-elapsed.json', {'samples': self.samples,
            'scope': 'single local short samples including client/log overhead; no performance threshold, throughput or platform guarantee'})
        scanned = 0
        for file in self.output.rglob('*'):
            if file.is_file():
                self.check(not any(secret.encode() in file.read_bytes() for secret in self.secrets), 'credential scan: ' + str(file.relative_to(self.output)))
                scanned += 1
        label = 'credential scan: summary.json'
        candidate = self.output / '.summary.candidate'
        save_json(candidate, {'status': 'passed', 'httpRequests': self.request_count,
            'assertions': len(self.assertions) + 1, 'assertionLabels': self.assertions + [label], 'credentialScannedFiles': scanned + 1,
            'runs': self.runs, 'fixtures': self.fixtures,
            'workerIdentities': [{'pid': p, 'startTicks': t} for p, t in sorted(self.worker_identities)],
            'scope': 'S2 six real HTTP timing commands; offline fixture is not HTTP source/ruby editing; local elapsed samples are not performance/browser/full-B3-B4 acceptance'})
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), label)
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))

    def run(self):
        self.start('timing')
        self.connect('timing')
        capabilities = self.request('timing-capabilities', 'GET', '/api/v1/capabilities')
        for name in ('stampTokenStart', 'stampTokenEnd', 'clearTokenTime', 'nudgeTokenTime', 'shiftLine', 'setTokenTimes'):
            self.check('project-sessions.commands.lyrics.' + name in capabilities['features'], 'advertised timing command ' + name)
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000); wav.writeframes(b'\x00\x00' * 16000)
        assets = []
        for name, data, media in (('timing.wav', buf.getvalue(), 'audio/wav'), ('timing.lrc', b'[00:00.00]Timing fixture\n', 'text/plain')):
            assets.append(self.request('upload-' + name, 'POST', '/api/v1/assets?fileName=' + name, raw=data,
                content_type=media, status=201, headers={'X-Mako-Request-Id': 'upload-' + name,
                    'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']}))
        created = self.request('create-template', 'POST', '/api/v1/project-sessions', status=201, value={
            'name': 'S2 timing HTTP probe', 'audioAssetId': assets[0]['assetId'], 'lyricsAssetId': assets[1]['assetId']})
        path = '/api/v1/project-sessions/' + created['projectSessionId']
        path, opened = self.save_close_open('template', path, created['projectRevision'], 'editing-original')
        self.close('template-close', path, opened['projectRevision'])
        normal = self.fixture('timing-normal', self.timing_fixture)
        path, opened = self.open('normal-open', normal)
        other = self.register('timing-other-client', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        self.request('timing-cross-client', 'POST', path + '/commands', client=other['clientId'],
                     value=self.payload('cross-client', opened['projectRevision'], nudge(FIRST, 1)),
                     status=404, code='sessionNotFound')
        self.exercise('stamp-estimated-start', path, stamp('Start', THIRD, 1000001), undo_name='token.stamp', estimated=True,
            expected_tokens=['third'], check=lambda state: self.token(state, 'third', 1000001, 1033334, 'estimated'))
        self.exercise('stamp-confirm-same-end', path, stamp('End', THIRD, 1033334), undo_name='token.stamp', estimated=False,
            expected_tokens=['third'], check=lambda state: self.token(state, 'third', 1000001, 1033334, 'edited'))
        self.exercise('clear-third', path, clear(THIRD), undo_name='token.time.clear', expected_tokens=['third'],
            check=lambda state: self.token(state, 'third', status='missing'))
        self.exercise('nudge-first', path, nudge(FIRST, 1), undo_name='token.time', expected_tokens=['first'],
            check=lambda state: self.token(state, 'first', 133333, 233333, 'edited'))
        before, after, _, _ = self.accepted('nudge-second-request', path, nudge(FIRST, 1), undo_name='token.time',
            check=lambda state: self.token(state, 'first', 166666, 266666, 'edited'))
        undone = self.history('nudge-second-request-undo', path, 'undo')
        self.same_content(before, undone, 'nudge requests never merge')
        for name, command, estimated in (('zero-nudge', nudge(FIRST, 0), None), ('zero-shift', shift(0), None),
            ('clear-missing', clear(THIRD), None), ('equal-batch', batch(change(FIRST, 133333, 233333)), None),
            ('same-start', stamp('Start', FIRST, 133333), False), ('same-end', stamp('End', FIRST, 233333), False)):
            self.accepted('noop-' + name, path, command, changed=False, estimated=estimated)
        self.same_content(after, self.history('nudge-second-request-redo', path, 'redo'), 'nudge second redo')
        def shifted(state):
            self.check(self.line(state)['time'] == {'startUs': '123457', 'endUs': '1623457'}, 'shift: exact line range')
            self.token(state, 'first', 290123, 390123, 'edited')
            self.token(state, 'second', 623457, 823457, 'edited')
            self.token(state, 'third', status='missing')
        self.exercise('shift-exact-microseconds', path, shift('+123457'), undo_name='line.shift', check=shifted,
                      expected_tokens=['first', 'second'])
        self.exercise('batch-two', path, batch(change(FIRST, 300001, 400007), change(SECOND, 900001, 1100007)),
            undo_name='token.time', expected_tokens=['first', 'second'],
            check=lambda state: (self.token(state, 'first', 300001, 400007, 'edited'), self.token(state, 'second', 900001, 1100007, 'edited')))
        self.receipts(path)
        self.malformed(path)
        self.domain_failures(path)
        self.concurrency(path)
        target = {'branchId': 'translation', 'lineId': 'translated', 'tokenId': 'translated-token'}
        before = self.state('follow-before', path)
        after = self.exercise('follow-primary-explicit-target', path, nudge(target, 1), undo_name='token.time',
            expected_tokens=['translated-token'], check=lambda state: self.token(state, 'translated-token', 133333, 233333, 'edited', 'translation'))
        self.check(after['models'][2]['primary']['lines'] == before['models'][2]['primary']['lines'], 'explicit secondary source leaves primary contents unchanged')
        self.check(self.line(after, 'translation')['primaryLineId'] == 'one' and after['models'][0]['branches'][1]['timingMode'] == 'followPrimary',
                   'explicit secondary source preserves FollowPrimary association/policy')
        self.bulk(path)
        path, opened = self.save_close_open('timing-roundtrip', path,
            self.request('timing-save-current', 'GET', path)['projectRevision'], 'timing-saved')
        self.close('timing-normal-close', path, opened['projectRevision'])
        extended = self.fixture('timing-extended', lambda project, lyrics: self.timing_fixture(project, lyrics, extended=True))
        path, opened = self.open('extended-open', extended)
        self.extended(path)
        path, opened = self.save_close_open('extended-roundtrip', path,
            self.request('extended-save-current', 'GET', path)['projectRevision'], 'timing-extended-saved')
        self.close('timing-extended-close', path, opened['projectRevision'])
        unicode = self.fixture('timing-unicode', self.unicode_fixture)
        path, opened = self.open('unicode-open', unicode)
        self.unicode(path)
        path, opened = self.save_close_open('unicode-roundtrip', path,
            self.request('unicode-save-current', 'GET', path)['projectRevision'], 'timing-unicode-saved')
        self.close('timing-unicode-close', path, opened['projectRevision'])
        self.finish()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(s1.REPO / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    args.output.mkdir(parents=True, exist_ok=False)
    probe = TimingProbe(args.output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
