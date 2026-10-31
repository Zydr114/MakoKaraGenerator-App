#!/usr/bin/env python3
"""S3a real HTTP content/settings acceptance with owned immutable persistence fixtures."""

import argparse
import copy
import importlib.util
import io
import json
from pathlib import Path
import wave

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('s2_timing_probe', HERE / 'timing_probe.py')
timing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(timing)
save_json = timing.save_json
LINE = {'branchId': 'primary', 'lineId': 'one'}
TOKEN = {**LINE, 'tokenId': 'first'}
SECOND = {**LINE, 'tokenId': 'second'}
CONTENT = ('lyrics.setLineText', 'lyrics.setRubyText', 'lyrics.addLine', 'lyrics.duplicateLine', 'lyrics.removeLine')
SETTINGS = ('composition.setCanvasSize', 'composition.setFrameRate', 'composition.setBackgroundColor',
            'composition.setOutputDuration', 'composition.useLyricsEnd', 'composition.useAudioDuration')


def text(value, target=LINE):
    return {'type': 'lyrics.setLineText', 'target': target, 'text': value}


def ruby(value, target=TOKEN):
    return {'type': 'lyrics.setRubyText', 'target': target, 'text': value}


def add(branch='primary', after=None):
    return {'type': 'lyrics.addLine', 'branchId': branch, 'afterLineId': after}


def duplicate(target=LINE):
    return {'type': 'lyrics.duplicateLine', 'target': target}


def remove(target=LINE):
    return {'type': 'lyrics.removeLine', 'target': target}


def canvas(width, height):
    return {'type': 'composition.setCanvasSize', 'width': width, 'height': height}


def rate(numerator, denominator):
    return {'type': 'composition.setFrameRate', 'numerator': numerator, 'denominator': denominator}


def color(red, green, blue, alpha):
    return {'type': 'composition.setBackgroundColor', 'red': red, 'green': green, 'blue': blue, 'alpha': alpha}


def duration(value):
    return {'type': 'composition.setOutputDuration', 'durationUs': value}


def use(which):
    return {'type': 'composition.use' + which}


class ContentProbe(timing.TimingProbe):
    def request(self, name, method, path, **kwargs):
        result = super().request(name, method, path, **kwargs)
        command = (kwargs.get('value') or {}).get('command', {})
        if method == 'POST' and path.endswith('/commands') and command.get('type') in ('history.undo', 'history.redo'):
            self.check('feedback' not in result, name + ': history omits content feedback')
        return result

    @staticmethod
    def rows(state, branch='primary'):
        return state['models'][2][branch]['lines']

    @staticmethod
    def row(state, identifier='one', branch='primary'):
        return next(x for x in ContentProbe.rows(state, branch) if x['id'] == identifier)

    def accepted(self, name, path, command, *, changed=True, undo_name=None, check=None,
                 feedback=None, **unused):
        before = self.state(name + '-before', path)
        revision = before['summary']['projectRevision']
        payload = self.payload(name, revision, command)
        result = self.request(name, 'POST', path + '/commands', value=payload)
        after = self.state(name + '-after', path)
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1] and result['previousRevision'] == revision and
                   result['projectRevision'] == str(int(revision) + int(changed)), name + ': exact session/previous/current revision')
        self.check(result['projectRevision'] == after['summary']['projectRevision'] and result['changed'] is changed,
                   name + ': real change agrees with current projection')
        self.check('timingResult' not in result and 'failureCode' not in result, name + ': content/settings omit timing result and error fields')
        is_content = command['type'] in CONTENT
        if is_content:
            value = result.get('feedback')
            self.check(isinstance(value, dict) and set(value) <= {'selection', 'createdLineId', 'deletedLineId', 'insertedIndex',
                       'overlapsNext', 'preservedTokenCount', 'resetTokenCount'}, name + ': small actual content feedback only')
            if command['type'] in ('lyrics.setLineText', 'lyrics.setRubyText'):
                self.check('selection' not in value, name + ': text/ruby does not force client selection')
            if feedback:
                feedback(before, after, value)
        else:
            self.check('feedback' not in result, name + ': settings omit content feedback')
        if changed:
            self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and after['history']['redoCount'] == 0,
                       name + ': one HTTP action is one independent history entry')
            self.check(result.get('undoName') == undo_name and after['history'].get('undoName') == undo_name,
                       name + ': exact shared history name')
            self.check(after['eventCursor'] != before['eventCursor'] and result['isDirty'], name + ': real event and dirty')
            change_set = result['changeSet']
            expected_areas = ['lyricsContent', 'lyricsStructure', 'timeline', 'renderInputs'] if is_content else ['composition', 'timeline', 'renderInputs']
            self.check(change_set['areas'] == expected_areas and change_set['requiresFullRefresh'] is True and change_set['tokenIds'] == [],
                       name + ': structural/settings full refresh with complete change areas')
            if is_content:
                branch = command.get('branchId') or command['target']['branchId']
                line = result['feedback'].get('createdLineId') or command.get('target', {}).get('lineId')
                self.check(change_set['branchIds'] == [branch] and change_set['lineIds'] == [line], name + ': necessary source/line IDs')
            else:
                self.check(change_set['branchIds'] == [] and change_set['lineIds'] == [], name + ': settings have no fabricated lyric IDs')
        else:
            self.same_state(before, after, name)
            self.check(result['changeSet'] == {'areas': [], 'branchIds': [], 'lineIds': [], 'tokenIds': [], 'requiresFullRefresh': False},
                       name + ': no-op has no changeSet')
            if command['type'] == 'lyrics.setLineText':
                self.check(result['feedback'] == {'preservedTokenCount': 0, 'resetTokenCount': 0}, name + ': no-op text reports 0/0')
        if check:
            check(after)
        return before, after, result, payload

    def rejected(self, name, path, command, status, code, failure=None, *, raw_command=None):
        before = self.state(name + '-before', path)
        payload = self.payload(name, before['summary']['projectRevision'], command)
        if raw_command is None:
            result = self.request(name, 'POST', path + '/commands', value=payload, status=status, code=code)
        else:
            prefix = json.dumps({k: v for k, v in payload.items() if k != 'command'})
            body = (prefix[:-1] + ',"command":' + raw_command + '}').encode()
            save_json(self.output / (name + '-raw-command.json'), {'command': raw_command, 'scope': 'probe-owned malformed command only'})
            result = self.request(name, 'POST', path + '/commands', raw=body, content_type='application/json', status=status, code=code)
        self.check('feedback' not in result and 'timingResult' not in result, name + ': rejected operation has no success feedback')
        if failure is None:
            self.check('failureCode' not in result, name + ': non-domain error omits failureCode')
        else:
            self.check(result.get('failureCode') == failure, name + ': stable Application failureCode ' + failure)
            expected_field = 'command'
            if failure == 'TargetStale':
                expected_field = ('command.afterLineId' if command.get('afterLineId') is not None else 'command.branchId') if command['type'] == 'lyrics.addLine' else 'command.target'
            elif failure == 'DomainValidationFailed' and command['type'] in ('lyrics.setLineText', 'lyrics.setRubyText'):
                expected_field = 'command.text'
            elif command['type'] == 'composition.setCanvasSize':
                expected_field = 'command.width' if command['width'] <= 0 else 'command.height'
            elif command['type'] == 'composition.setFrameRate':
                expected_field = 'command.numerator' if command['numerator'] <= 0 else 'command.denominator'
            elif command['type'] == 'composition.setOutputDuration':
                expected_field = 'command.durationUs'
            self.check(result.get('field') == expected_field and result['field'] != failure,
                       name + ': actual command input path ' + expected_field)
        self.same_state(before, self.state(name + '-after', path), name)
        return result

    def fixture_content(self, project, lyrics):
        lyrics['primaryBranchId'] = 'primary'
        lyrics['branches'] = [
            {'id': 'primary', 'name': '主歌词', 'role': 'Primary', 'language': 'ja-JP', 'timingMode': 'Independent', 'lines': [
                {'id': 'one', 'text': '甲乙', 'startUs': 0, 'endUs': 1500000, 'timingState': 'Imported', 'tokens': [
                    {'id': 'first', 'text': '甲', 'startUs': 100000, 'endUs': 200000, 'timingState': 'Imported'},
                    {'id': 'second', 'text': '乙', 'startUs': 500000, 'endUs': 700000, 'timingState': 'Imported'}],
                 'rubyAnnotations': [{'id': 'ruby', 'baseTokenId': 'first', 'text': 'こう', 'language': 'ja-JP',
                                      'relativeScale': .7, 'gapPx': 3.25, 'alignment': 'Start'}]},
                {'id': 'two', 'text': '丙', 'startUs': 1500000, 'endUs': 1800000, 'timingState': 'Imported', 'tokens': [
                    {'id': 'third', 'text': '丙', 'startUs': 1500000, 'endUs': 1700000, 'timingState': 'Imported'}]}]},
            {'id': 'translation', 'name': '翻译', 'role': 'Translation', 'language': 'zh-CN', 'timingMode': 'FollowPrimary',
             'lines': [{'id': 'translated', 'primaryLineId': 'one', 'text': '译', 'timingState': 'Missing',
                        'tokens': [{'id': 'translated-token', 'text': '译', 'timingState': 'Missing'}]}]},
            {'id': 'empty', 'name': '空来源', 'role': 'Custom', 'timingMode': 'None', 'lines': []}]
        project['composition'].update(canvasWidth=320, canvasHeight=180, frameRate={'numerator': 30, 'denominator': 1}, backgroundColor='#000000FF')
        project['composition'].pop('outputDurationUs', None)

    def texts(self, path):
        original = self.state('text-original', path)
        original_tokens = self.row(original)['tokens']
        original_ruby = self.row(original)['rubyAnnotations']
        def first_text(state):
            row = self.row(state)
            self.check(row['text'] == '甲新乙' and ''.join(t['text'] for t in row['tokens']) == row['text'], 'simple text: authoritative Unicode value and token coverage')
            self.check(row['tokens'][0] == original_tokens[0] and row['tokens'][2] == original_tokens[1], 'simple text: original IDs/text/microseconds/status preserved')
            self.check(row['tokens'][1]['time'] == {} and row['tokens'][1]['timingStatus'] == 'missing', 'simple text: new token is untimed')
            self.check(row['rubyAnnotations'] == original_ruby, 'simple text: original ruby metadata retained')
        self.exercise('text-insert', path, text('甲新乙'), undo_name='lyrics.set-line-text', check=first_text,
            feedback=lambda before, after, value: self.check(value == {'preservedTokenCount': 2, 'resetTokenCount': 1}, 'simple text: exact resplit counts'))
        unicode_text = ' 甲新👨‍👩‍👧‍👦き\u3099乙 \n'
        def complex_text(state):
            row = self.row(state)
            self.check(row['text'] == unicode_text and ''.join(t['text'] for t in row['tokens']) == unicode_text,
                       'complex text: no trim/newline loss/Unicode normalization')
            self.check(sum(t['text'] == '👨‍👩‍👧‍👦' for t in row['tokens']) == 1 and sum(t['text'] == 'き\u3099' for t in row['tokens']) == 1,
                       'complex text: family emoji and decomposed combining text remain single graphemes')
            self.check(all(t['id'] not in {'first', 'second'} and t['time'] == {} and t['timingStatus'] == 'missing'
                           for t in row['tokens']), 'complex text: changing both edges resets the entire shared-edge edit region')
            self.check(row['rubyAnnotations'] == original_ruby, 'complex text: preserve genuine now-dangling ruby metadata')
        def actual_counts(before, after, value):
            old = {t['id']: t for t in self.row(before)['tokens']}
            preserved = sum(t['id'] in old and t == old[t['id']] for t in self.row(after)['tokens'])
            reset = sum(t['id'] not in old for t in self.row(after)['tokens'])
            self.check(value == {'preservedTokenCount': preserved, 'resetTokenCount': reset}, 'complex text: feedback matches all retained/new tokens')
        before, after, _, _ = self.accepted('text-complex', path, text(unicode_text), undo_name='lyrics.set-line-text', check=complex_text, feedback=actual_counts)
        undone = self.history('text-complex-undo', path, 'undo')
        self.same_content(before, undone, 'text requests never merge')
        self.accepted('text-noop-preserve-redo', path, text('甲新乙'), changed=False)
        self.same_content(after, self.history('text-complex-redo', path, 'redo'), 'complex text redo')
        self.same_content(before, self.history('text-complex-restore-for-ruby', path, 'undo'), 'restore original retained-token targets before ruby edits')
        def ruby_updated(state):
            expected = copy.deepcopy(original_ruby)
            expected[0]['text'] = 'き\u3099 🎵'
            self.check(self.row(state)['rubyAnnotations'] == expected, 'ruby: identity/base/language/scale/gap/alignment retained, text exact')
            self.check('time' not in expected[0], 'ruby has no independent timing')
        self.exercise('ruby-update', path, ruby('き\u3099 🎵'), undo_name='line.ruby', check=ruby_updated,
            feedback=lambda before, after, value: self.check(value == {}, 'ruby feedback does not fabricate selection/mappings'))
        self.exercise('ruby-delete', path, ruby(''), undo_name='line.ruby',
            check=lambda state: self.check(self.row(state)['rubyAnnotations'] == [], 'empty ruby removes original annotation'))
        before, after, _, _ = self.accepted('ruby-add', path, ruby('再'), undo_name='line.ruby',
            check=lambda state: self.check(len(self.row(state)['rubyAnnotations']) == 1 and
                self.row(state)['rubyAnnotations'][0]['baseTokenId'] == 'first' and self.row(state)['rubyAnnotations'][0]['text'] == '再', 'ruby addition returns actual new annotation'))
        self.same_content(before, self.history('ruby-add-undo', path, 'undo'), 'ruby addition undo')
        self.accepted('ruby-empty-noop-preserve-redo', path, ruby(''), changed=False,
            feedback=lambda before, after, value: self.check(value == {}, 'repeated ruby clear preserves selection and creates no mapping'))
        self.same_content(after, self.history('ruby-add-redo', path, 'redo'), 'ruby addition redo')

    def structure(self, path):
        def copied(before, after, value):
            source = self.row(before)
            copy_row = self.row(after, value['createdLineId'])
            old_ids = set()
            for branch, window in before['models'][2].items():
                old_ids.add(branch)
                for row in window['lines']:
                    old_ids.add(row['id'])
                    old_ids.update(t['id'] for t in row['tokens'])
                    old_ids.update(r['id'] for r in row['rubyAnnotations'])
            self.check(value == {'selection': {'branchId': 'primary', 'hasSelection': True, 'lineId': copy_row['id']},
                       'createdLineId': copy_row['id'], 'insertedIndex': 1, 'overlapsNext': True}, 'duplicate: complete selection/index/overlap feedback')
            self.check(self.rows(after)[1]['id'] == copy_row['id'] and copy_row['id'] not in [row['id'] for row in self.rows(before)], 'duplicate: unique stable new line immediately follows source')
            self.check(copy_row['text'] == source['text'] and copy_row['time'] == {'startUs': '1500000', 'endUs': '3000000'} and
                       copy_row['timingStatus'] == source['timingStatus'], 'duplicate: copied line state and exact shifted duration beyond real media')
            self.check(len(copy_row['tokens']) == len(source['tokens']) and len(copy_row['rubyAnnotations']) == len(source['rubyAnnotations']) and
                       copy_row.get('primaryLineId') == source.get('primaryLineId'), 'duplicate: complete tokens/ruby and original association')
            created_ids = [copy_row['id']] + [t['id'] for t in copy_row['tokens']] + [r['id'] for r in copy_row['rubyAnnotations']]
            self.check(len(created_ids) == len(set(created_ids)) and all(identifier not in old_ids for identifier in created_ids),
                       'duplicate: all new line/token/ruby identities globally unique')
            original_ids = {t['id'] for t in source['tokens']}
            for old, new in zip(source['tokens'], copy_row['tokens']):
                self.check(new['id'] not in original_ids and new['text'] == old['text'] and new['timingStatus'] == old['timingStatus'], 'duplicate: new token ID preserves original text/status')
                expected = {key: str(int(val) + 1500000) for key, val in old['time'].items()}
                self.check(new['time'] == expected, 'duplicate: exact shifted timed/missing token')
            for old, new in zip(source['rubyAnnotations'], copy_row['rubyAnnotations']):
                mapped = copy_row['tokens'][next(i for i, t in enumerate(source['tokens']) if t['id'] == old['baseTokenId'])]['id']
                self.check(new['id'] != old['id'] and new == {**old, 'id': new['id'], 'baseTokenId': mapped}, 'duplicate: all ruby metadata and new base mapping exact')
        self.exercise('duplicate-rich-line', path, duplicate(), undo_name='lyrics.duplicate-line', feedback=copied)
        def added(before, after, value):
            row = self.row(after, value['createdLineId'], 'empty')
            self.check(value['selection'] == {'branchId': 'empty', 'hasSelection': True, 'lineId': row['id']} and
                       value['insertedIndex'] == len(self.rows(before, 'empty')), 'add: selected stable ID and actual append index')
            self.check(row['text'] == '' and row['time'] == {} and row['timingStatus'] == 'missing' and row['tokens'] == [] and row['rubyAnnotations'] == [], 'add: truly untimed empty line')
        _, first, result, _ = self.accepted('add-empty-first', path, add('empty'), undo_name='lyrics.add-line', feedback=added)
        first_id = result['feedback']['createdLineId']
        _, second, result, _ = self.accepted('add-empty-after', path, add('empty', first_id), undo_name='lyrics.add-line', feedback=added)
        second_id = result['feedback']['createdLineId']
        self.exercise('remove-select-next', path, remove({'branchId': 'empty', 'lineId': first_id}), undo_name='lyrics.remove-line',
            feedback=lambda before, after, value: self.check(value == {'selection': {'branchId': 'empty', 'hasSelection': True, 'lineId': second_id},
                'deletedLineId': first_id}, 'remove: select actual next stable line'))
        self.exercise('remove-last-clear-selection', path, remove({'branchId': 'empty', 'lineId': second_id}), undo_name='lyrics.remove-line',
            feedback=lambda before, after, value: self.check(value == {'selection': {'branchId': 'empty', 'hasSelection': False},
                'deletedLineId': second_id}, 'remove last: explicit clear and omitted lineId'))
        self.exercise('add-after-deleted-empty-source', path, {'type': 'lyrics.addLine', 'branchId': 'empty'},
                      undo_name='lyrics.add-line', feedback=added)

    def scalar(self, path):
        cases = [
            ('canvas-odd', canvas(641, 361), 'composition.canvas', {'canvasWidth': 641, 'canvasHeight': 361}),
            ('rate-rational', rate(60000, 2002), 'composition.frame-rate', {'frameRateNumerator': 30000, 'frameRateDenominator': 1001}),
            ('background-rgba', color(1, 2, 3, 4), 'composition.background', {'backgroundRed': 1, 'backgroundGreen': 2, 'backgroundBlue': 3, 'backgroundAlpha': 4}),
            ('duration-large-int64', duration('9007199254740993'), 'composition.duration', {'outputDurationUs': '9007199254740993'}),
            ('duration-lyrics-end', use('LyricsEnd'), 'composition.duration', {'outputDurationUs': '3000000'}),
            ('duration-real-audio', use('AudioDuration'), 'composition.duration', {'outputDurationUs': '2000000', 'audioDurationUs': '2000000'})]
        for name, command, undo_name, expected in cases:
            def verify(state, expected=expected):
                self.check(all(state['models'][1].get(key) == value for key, value in expected.items()), name + ': actual scalar settings exact')
            self.exercise(name, path, command, undo_name=undo_name, check=verify)
        # Build a real redo entry while retaining all current settings for no-op probes.
        self.accepted('noop-setup-canvas', path, canvas(643, 363), undo_name='composition.canvas')
        self.history('noop-setup-undo', path, 'undo')
        for name, command in (('canvas', canvas(641, 361)), ('rate-reduced', rate(30000, 1001)),
            ('background', color(1, 2, 3, 4)), ('duration', duration('+02000000')), ('audio-duration', use('AudioDuration'))):
            self.accepted('noop-' + name, path, command, changed=False)
        self.history('noop-setup-redo', path, 'redo')
        self.exercise('duration-explicit-null', path, duration(None), undo_name='composition.duration',
            check=lambda state: self.check('outputDurationUs' not in state['models'][1], 'explicit null means unspecified duration and omits settings field'))

    def shape_and_domain(self, path):
        invalid = [('text-missing', {'type': 'lyrics.setLineText', 'target': LINE}), ('text-null', text(None)), ('text-number', text(1)),
            ('text-token-target', text('x', TOKEN)), ('ruby-line-target', ruby('x', LINE)), ('ruby-null', ruby(None)),
            ('add-missing-branch', {'type': 'lyrics.addLine'}), ('add-empty-after', add('primary', '')),
            ('duplicate-token-target', duplicate(TOKEN)), ('remove-token-target', remove(TOKEN)),
            ('canvas-missing', {'type': 'composition.setCanvasSize', 'width': 1}), ('canvas-string', canvas('1', 2)),
            ('canvas-fraction', canvas(1.5, 2)), ('canvas-overflow', canvas(2147483648, 2)),
            ('rate-string', rate('30', 1)), ('rate-bool', rate(True, 1)), ('rate-missing', {'type': 'composition.setFrameRate', 'numerator': 30}),
            ('background-missing-alpha', {'type': 'composition.setBackgroundColor', 'red': 1, 'green': 2, 'blue': 3}),
            ('background-overflow', color(256, 0, 0, 255)), ('background-negative', color(-1, 0, 0, 255)),
            ('duration-missing', {'type': 'composition.setOutputDuration'}), ('duration-number', duration(1)),
            ('duration-fraction', duration('1.5')), ('duration-overflow', duration('9223372036854775808')),
            ('add-cross-target-null', {**add(), 'target': None}), ('text-cross-null', {**text('x'), 'startUs': None}),
            ('ruby-extra', {**ruby('x'), 'extra': 1}), ('canvas-cross-null', {**canvas(1, 2), 'text': None}),
            ('duration-cross-null', {**duration(None), 'target': None}), ('lyrics-end-client-duration', {**use('LyricsEnd'), 'durationUs': None}),
            ('audio-client-duration', {**use('AudioDuration'), 'audioDurationUs': '10000000'}),
            ('nested-extra', text('x', {**LINE, 'extra': 1})), ('empty-id', text('x', {**LINE, 'lineId': ''})),
            ('long-id', text('x', {**LINE, 'lineId': 'x' * 129}))]
        for name, command in invalid:
            self.rejected('shape-' + name, path, command, 400, 'invalidRequest')
        raw = '{"type":"lyrics.setLineText","target":' + json.dumps(LINE) + ',"text":"x","text":"x"}'
        self.rejected('shape-duplicate-text', path, text('x'), 400, 'invalidRequest', raw_command=raw)
        raw = '{"type":"composition.setOutputDuration","durationUs":null,"durationUs":null}'
        self.rejected('shape-duplicate-null-duration', path, duration(None), 400, 'invalidRequest', raw_command=raw)
        for name, command, failure in [
            ('missing-line', text('x', {**LINE, 'lineId': 'missing'}), 'TargetStale'),
            ('missing-token', ruby('x', {**TOKEN, 'tokenId': 'missing'}), 'TargetStale'),
            ('missing-after', add('primary', 'missing'), 'TargetStale'), ('missing-branch', add('missing'), 'TargetStale'),
            ('canvas-zero', canvas(0, 10), 'InvalidCanvasSize'), ('canvas-negative', canvas(-1, 10), 'InvalidCanvasSize'),
            ('canvas-zero-height', canvas(10, 0), 'InvalidCanvasSize'),
            ('rate-zero', rate(0, 1), 'InvalidFrameRate'), ('rate-negative', rate(-1, 1), 'InvalidFrameRate'),
            ('rate-zero-denominator', rate(30, 0), 'InvalidFrameRate'),
            ('duration-zero', duration('0'), 'InvalidOutputDuration'), ('duration-negative', duration('-1'), 'InvalidOutputDuration')]:
            self.rejected('domain-' + name, path, command, 422, 'commandRejected', failure)
        self.rejected('unknown-type', path, {'type': 'unknown.command'}, 422, 'commandNotSupported')

    def budgets(self, path):
        self.accepted('budget-setup-edit', path, canvas(640, 360), undo_name='composition.canvas')
        self.history('budget-setup-undo', path, 'undo')
        self.accepted('noop-null-duration-preserves-redo', path, duration(None), changed=False)
        for name, command in [('text-items', text('字' * 20001)), ('text-raw-too-long', text('x' * (1024 * 1024 + 1))),
            ('ruby-raw-too-long', ruby('x' * (1024 * 1024 + 1))), ('ruby-escaped-byte-budget', ruby('字' * 200000)),
            ('text-final-row-byte-budget', text('x' * 19000))]:
            result = self.rejected('budget-' + name, path, command, 413, 'resourceTooLarge')
            self.check(result.get('field') == 'command', name + ': budget rejection identifies command')
        self.history('budget-existing-redo', path, 'redo')

    def receipts(self, path):
        cases = [
            ('text', text('甲き\u3099乙'), text('甲き\u3099乙'), [text('甲ぎ乙'), text('x', {**LINE, 'lineId': 'two'})], 'lyrics.set-line-text'),
            ('ruby', ruby('き\u3099'), ruby('き\u3099'), [ruby('ぎ'), ruby('x', SECOND)], 'line.ruby'),
            ('add', add('primary'), {'type': 'lyrics.addLine', 'branchId': 'primary'}, [add('empty'), add('primary', 'one')], 'lyrics.add-line'),
            ('duplicate', duplicate(), duplicate(), [duplicate({**LINE, 'lineId': 'two'})], 'lyrics.duplicate-line'),
            ('canvas', canvas(645, 365), canvas(645, 365), [canvas(647, 365), canvas(645, 367)], 'composition.canvas'),
            ('rate', rate(50, 2), rate(25, 1), [rate(24, 1), rate(50, 3)], 'composition.frame-rate'),
            ('color', color(10, 20, 30, 40), color(10, 20, 30, 40), [color(11, 20, 30, 40), color(10, 21, 30, 40), color(10, 20, 31, 40), color(10, 20, 30, 41)], 'composition.background'),
            ('duration', duration('4000007'), duration('+04000007'), [duration(None)], 'composition.duration'),
            ('lyrics-end', use('LyricsEnd'), use('LyricsEnd'), [use('AudioDuration')], 'composition.duration'),
            ('audio-duration', use('AudioDuration'), use('AudioDuration'), [use('LyricsEnd')], 'composition.duration')]
        copied_id = None
        for name, command, same, differences, undo_name in cases:
            _, after, original, payload = self.accepted('receipt-' + name, path, command, undo_name=undo_name)
            if name == 'duplicate':
                copied_id = original['feedback']['createdLineId']
            self.receipt_checks(name, path, after, original, payload, same, differences)
        command = remove({**LINE, 'lineId': copied_id})
        _, after, original, payload = self.accepted('receipt-remove', path, command, undo_name='lyrics.remove-line')
        self.receipt_checks('remove', path, after, original, payload, command, [remove({**LINE, 'lineId': 'two'})])

    def receipt_checks(self, name, path, after, original, payload, same, differences):
        retry = {**payload, 'expectedRevision': '000' + payload['expectedRevision'], 'command': same}
        result = self.request('receipt-' + name + '-normalized-stale-retry', 'POST', path + '/commands', value=retry)
        self.check(result == original, name + ': complete feedback/IDs replay before stale revision gate')
        for index, command in enumerate(differences):
            error = self.request('receipt-' + name + '-field-reuse-' + str(index), 'POST', path + '/commands',
                                 value={**payload, 'command': command}, status=409, code='requestIdReuse')
            self.check('feedback' not in error and 'failureCode' not in error, name + ': requestIdReuse omits success/domain fields')
        self.request('receipt-' + name + '-revision-reuse', 'POST', path + '/commands', value={**payload,
            'expectedRevision': str(int(payload['expectedRevision']) + 1)}, status=409, code='requestIdReuse')
        self.request('receipt-' + name + '-deadline-reuse', 'POST', path + '/commands', value={**payload,
            'validUntilUtc': timing.s1.smoke.existing.iso(timing.s1.smoke.existing.utc_now() + timing.s1.smoke.existing.timedelta(seconds=95))},
            status=409, code='requestIdReuse')
        self.same_state(after, self.state('receipt-' + name + '-stable', path), name + ': no reallocation or second history on retry/reuse')

    def auth_and_races(self, path):
        before = self.state('auth-before', path)
        other = self.register('content-other-client', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        self.request('content-cross-client', 'POST', path + '/commands', client=other['clientId'], value=self.payload('cross-client',
            before['summary']['projectRevision'], text('x')), status=404, code='sessionNotFound')
        self.request('content-missing-session', 'POST', '/api/v1/project-sessions/missing/commands', value=self.payload('missing-session', '0', canvas(1, 2)),
            status=404, code='sessionNotFound')
        self.same_state(before, self.state('auth-after', path), 'unauthorized/missing session')
        revision = before['summary']['projectRevision']
        requests = [self.payload('race-content', revision, text('甲并发乙')), self.payload('race-time', revision, timing.nudge(SECOND, 1))]
        results = self.concurrent('content-time-same-revision', path, requests)
        self.check(sorted(x[0] for x in results) == [200, 409] and next(x[2]['code'] for x in results if x[0] == 409) == 'revisionConflict',
                   'content/time share the same exact-revision writer gate')
        current = self.state('content-time-race-after', path)
        self.check(current['history']['undoCount'] == before['history']['undoCount'] + 1, 'content/time race: exactly one history')
        revision = current['summary']['projectRevision']
        requests = [self.payload('race-add-' + str(i), revision, add('empty')) for i in range(2)]
        results = self.concurrent('different-add-request-race', path, requests)
        self.check(sorted(x[0] for x in results) == [200, 409], 'same revision two add intents: exactly one accepted')
        after = self.state('add-race-after', path)
        self.check(len(self.rows(after, 'empty')) == len(self.rows(current, 'empty')) + 1 and after['history']['undoCount'] == current['history']['undoCount'] + 1,
                   'add race: exactly one new line and history')
        payload = self.payload('same-duplicate-race', after['summary']['projectRevision'], duplicate())
        results = self.concurrent('same-duplicate-request-race', path, [payload, copy.deepcopy(payload)])
        self.check([x[0] for x in results] == [200, 200] and results[0][2] == results[1][2], 'same duplicate intent: identical whole feedback/receipt')
        latest = self.state('duplicate-race-after', path)
        created_id = results[0][2]['feedback']['createdLineId']
        self.check(sum(row['id'] == created_id for row in self.rows(latest)) == 1 and
                   latest['history']['undoCount'] == after['history']['undoCount'] + 1, 'same duplicate receipt: one allocation and history')

    def readonly(self):
        name = self.fixture('content-readonly', self.fixture_content)
        handle = self.request('readonly-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': name})
        opened = self.job('readonly-open', self.request('readonly-open', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId'], 'access': 'read'}))['result']
        self.check(opened['isReadOnly'], 'readonly: actual read session')
        path = '/api/v1/project-sessions/' + opened['projectSessionId']
        commands = [text('x'), ruby('x'), add(), duplicate(), remove(), canvas(1, 2), rate(25, 1), color(1, 2, 3, 4), duration(None), use('LyricsEnd'), use('AudioDuration')]
        for command in commands:
            self.rejected('readonly-' + command['type'].replace('.', '-'), path, command, 403, 'readOnlySession')
        self.close('readonly-close', path, opened['projectRevision'])

    def specialized(self):
        def merged(project, lyrics):
            self.fixture_content(project, lyrics)
            line = lyrics['branches'][0]['lines'][0]
            line['text'] = '字' * 30000
            line['tokens'] = [{'id': 'first', 'text': line['text'], 'startUs': 100000, 'endUs': 200000, 'timingState': 'Imported'}]
            line['rubyAnnotations'] = []
        relative = self.fixture('content-large-merged', merged)
        path, opened = self.open('merged-open', relative)
        self.exercise('large-merged-append', path, text('字' * 30000 + '新'), undo_name='lyrics.set-line-text',
            feedback=lambda before, after, value: self.check(value == {'preservedTokenCount': 1, 'resetTokenCount': 1}, 'merged token: grapheme length is not new-token count'))
        path, opened = self.save_close_open('merged-roundtrip', path,
            self.request('merged-current', 'GET', path)['projectRevision'], 'content-merged-saved')
        self.close('merged-close', path, opened['projectRevision'])
        def empty(project, lyrics):
            self.fixture_content(project, lyrics)
            for branch in lyrics['branches']:
                branch['lines'] = []
        relative = self.fixture('content-no-lyrics-end', empty)
        path, opened = self.open('no-end-open', relative)
        self.accepted('no-end-setup-edit', path, canvas(400, 200), undo_name='composition.canvas')
        self.history('no-end-setup-undo', path, 'undo')
        self.accepted('no-valid-lyrics-end-noop', path, use('LyricsEnd'), changed=False)
        self.history('no-end-preserved-redo', path, 'redo')
        path, opened = self.save_close_open('no-end-roundtrip', path,
            self.request('no-end-current', 'GET', path)['projectRevision'], 'content-no-end-saved')
        self.close('no-end-close', path, opened['projectRevision'])
        def no_audio(project, lyrics):
            empty(project, lyrics)
            project['media']['audio'] = None
            project.pop('audioPath', None)
            project['audioAnalysis'] = {'state': 'noAudio'}
        relative = self.fixture('content-no-audio', no_audio)
        path, opened = self.open('no-audio-open', relative)
        self.rejected('no-prepared-audio-duration', path, use('AudioDuration'), 422, 'commandRejected', 'MediaDurationUnavailable')
        self.close('no-audio-close', path, opened['projectRevision'])
        def cleared(project, lyrics):
            self.fixture_content(project, lyrics)
        relative = self.fixture('content-clear-text', cleared)
        path, opened = self.open('clear-text-open', relative)
        original = self.state('clear-text-original', path)
        self.exercise('clear-text-retains-dangling-ruby', path, text(''), undo_name='lyrics.set-line-text',
            check=lambda state: self.check(self.row(state)['text'] == '' and self.row(state)['tokens'] == [] and
                self.row(state)['rubyAnnotations'] == self.row(original)['rubyAnnotations'], 'empty text: preserves original dangling ruby without inventing tokens'))
        path, opened = self.save_close_open('cleared-roundtrip', path,
            self.request('clear-text-current', 'GET', path)['projectRevision'], 'content-cleared-saved')
        self.close('clear-text-close', path, opened['projectRevision'])
        def exhausted(project, lyrics):
            self.fixture_content(project, lyrics)
            row = lyrics['branches'][0]['lines'][0]
            row['id'] = 'line-2147483647'
            row['tokens'][0]['id'] = 'token-2147483647'
            row['rubyAnnotations'][0]['baseTokenId'] = 'token-2147483647'
            lyrics['branches'][1]['lines'][0]['primaryLineId'] = row['id']
        relative = self.fixture('content-exhausted-ids', exhausted)
        path, opened = self.open('exhausted-open', relative)
        target = {**LINE, 'lineId': 'line-2147483647'}
        for name, command in [('add', add()), ('text', text('甲新乙', target)), ('duplicate', duplicate(target))]:
            self.rejected('exhausted-' + name, path, command, 422, 'commandRejected', 'IdentifierExhausted')
        self.close('exhausted-close', path, opened['projectRevision'])
        def invalid(project, lyrics):
            self.fixture_content(project, lyrics)
            lyrics['branches'][0]['lines'][0].update(startUs=9, endUs=8, timingState='Invalid')
        relative = self.fixture('content-invalid-existing-line', invalid)
        path, opened = self.open('invalid-line-open', relative)
        self.rejected('text-domain-existing-invalid-line', path, text('甲新乙'), 422, 'commandRejected', 'DomainValidationFailed')
        self.close('invalid-line-close', path, opened['projectRevision'])

    def null_duration_export(self):
        capabilities = self.request('duration-export-capabilities', 'GET', '/api/v1/export-capabilities')
        if not capabilities['enabled']:
            save_json(self.output / 'null-duration-export.json', {'status': 'not-run', 'reason': 'deployment export capability disabled',
                'scope': 'null omission is covered by settings commands; no export-duration-gate acceptance claimed'})
            return
        self.check('opaqueMp4' in capabilities['formats'], 'duration export witness: actual opaque MP4 capability')
        def valid(project, lyrics):
            self.fixture_content(project, lyrics)
            lyrics['branches'] = [lyrics['branches'][0]]
            project['composition']['outputDurationUs'] = 2000000
        relative = self.fixture('content-null-export-gate', valid)
        path, opened = self.open('export-duration-open', relative)
        accepted = self.request('export-duration-positive-control', 'POST', path + '/export-jobs', status=202,
            value={'expectedRevision': opened['projectRevision'], 'format': 'opaqueMp4', 'includeAudio': False,
                   'metadata': self.metadata('export-duration-positive')})
        terminal = self.job('export-duration-positive', accepted)
        self.check(terminal['input']['outputDurationUs'] == '2000000', 'duration positive control: actual export input has explicit duration')
        before, after, _, _ = self.accepted('export-duration-set-null', path, duration(None), undo_name='composition.duration',
            check=lambda state: self.check('outputDurationUs' not in state['models'][1], 'null duration remains unspecified before export'))
        expected = list(copy.deepcopy(self.normalize_content(before['models'])))
        expected[1].pop('outputDurationUs')
        self.check(tuple(expected) == self.normalize_content(after['models']), 'null witness: only output duration changed since valid successful export')
        error = self.request('export-duration-null-rejected', 'POST', path + '/export-jobs', status=422, code='exportInputRejected',
            value={'expectedRevision': after['summary']['projectRevision'], 'format': 'opaqueMp4', 'includeAudio': False,
                   'metadata': self.metadata('export-duration-null')})
        self.check('jobId' not in error, 'unspecified duration rejected before accepting export job')
        self.same_state(after, self.state('export-duration-after-reject', path), 'null export rejection')
        save_json(self.output / 'null-duration-export.json', {'status': 'passed', 'positiveJobId': terminal['jobId'],
            'positiveTerminal': terminal['state'], 'positiveDurationUs': '2000000', 'nullSettingsFieldOmitted': True,
            'negativeCode': error['code'], 'scope': 'otherwise identical legal input exports successfully with explicit duration then is rejected after setting only duration null; no independent pixel oracle'})
        path, opened = self.save_close_open('export-null-roundtrip', path,
            after['summary']['projectRevision'], 'content-null-export-saved')
        self.close('export-duration-close', path, opened['projectRevision'])

    def oversized_old(self):
        def large(project, lyrics):
            self.fixture_content(project, lyrics)
            lyrics['branches'] = [lyrics['branches'][0]]
            lyrics['branches'][0]['lines'] = [{'id': 'one', 'text': 'a' * 20000, 'startUs': 0, 'endUs': 1500000,
                'timingState': 'Imported', 'tokens': [{'id': 'old-' + str(i), 'text': 'a', 'startUs': i * 10,
                    'endUs': i * 10 + 9, 'timingState': 'Imported'} for i in range(20000)]}]
        relative = self.fixture('content-oversized-old', large)
        path, opened = self.open('oversized-open', relative)
        def compact(name):
            return (self.request(name + '-summary', 'GET', path), self.request(name + '-history', 'GET', path + '/history'),
                    self.request(name + '-state', 'GET', '/api/v1/clients/' + self.client + '/state')['eventCursor'])
        before = compact('oversized-before')
        self.request('oversized-old-window', 'GET', path + '/lyrics/window?expectedRevision=0&branchId=primary&limit=1', status=413, code='resourceTooLarge')
        result = self.request('oversized-old-duplicate', 'POST', path + '/commands', value=self.payload('oversized-duplicate', '0', duplicate()), status=413, code='resourceTooLarge')
        self.check('feedback' not in result and 'failureCode' not in result, 'oversized duplicate: atomic budget rejection without success feedback')
        self.check(compact('oversized-after-reject') == before, 'oversized old duplicate: exact summary/history/cursor preserved')
        result = self.request('oversized-old-remove', 'POST', path + '/commands', value=self.payload('oversized-remove', '0', remove()))
        self.check(result['changed'] and result['projectRevision'] == '1' and result['feedback'] == {
            'selection': {'branchId': 'primary', 'hasSelection': False}, 'deletedLineId': 'one'}, 'oversized old line can be removed as repair')
        self.check(self.get('oversized-repaired-window', path, '/lyrics/window', '1', branchId='primary')['lines'] == [], 'oversized repair: readable empty source')
        path, opened = self.save_close_open('oversized-repair-roundtrip', path, '1', 'content-oversized-repaired')
        self.close('oversized-close', path, opened['projectRevision'])

    def finish(self):
        self.check(bool(self.worker_identities), 'actual Worker preparation identities observed')
        self.stop('content')
        self.check(len(self.samples) == self.request_count, 'every HTTP call has a local elapsed sample')
        save_json(self.output / 'http-elapsed.json', {'samples': self.samples,
            'scope': 'single local short samples including client/log overhead; no allocation/performance/throughput/platform guarantee'})
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
            'scope': 'S3a eleven real HTTP content/settings commands, offline fixtures and existing time/history/persistence; excludes S3b/browser/performance/allocation/full-B3-B4 acceptance'})
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), label)
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))

    def run(self):
        self.start('content')
        self.connect('content')
        capabilities = self.request('content-capabilities', 'GET', '/api/v1/capabilities')
        for name in CONTENT + SETTINGS:
            self.check('project-sessions.commands.' + name in capabilities['features'], 'advertised S3a command ' + name)
        buf = io.BytesIO()
        with wave.open(buf, 'wb') as wav:
            wav.setnchannels(1); wav.setsampwidth(2); wav.setframerate(8000); wav.writeframes(b'\x00\x00' * 16000)
        assets = []
        for name, data, media in (('content.wav', buf.getvalue(), 'audio/wav'), ('content.lrc', '[00:00.00]甲乙\n'.encode(), 'text/plain')):
            assets.append(self.request('upload-' + name, 'POST', '/api/v1/assets?fileName=' + name, raw=data, content_type=media, status=201,
                headers={'X-Mako-Request-Id': 'upload-' + name, 'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']}))
        created = self.request('create-template', 'POST', '/api/v1/project-sessions', status=201,
            value={'name': 'S3a content HTTP probe', 'audioAssetId': assets[0]['assetId'], 'lyricsAssetId': assets[1]['assetId']})
        path = '/api/v1/project-sessions/' + created['projectSessionId']
        path, opened = self.save_close_open('content-template', path, created['projectRevision'], 'editing-original')
        self.close('content-template-close', path, opened['projectRevision'])
        relative = self.fixture('content-normal', self.fixture_content)
        path, opened = self.open('normal-open', relative)
        self.texts(path)
        self.structure(path)
        self.scalar(path)
        self.shape_and_domain(path)
        self.budgets(path)
        self.receipts(path)
        self.auth_and_races(path)
        before = self.state('translation-before', path)
        after = self.exercise('translation-explicit-text', path, text('译文', {'branchId': 'translation', 'lineId': 'translated'}),
            undo_name='lyrics.set-line-text')
        self.check(self.rows(before) == self.rows(after) and self.row(after, 'translated', 'translation')['primaryLineId'] == 'one' and
                   after['models'][0]['branches'][1]['timingMode'] == 'followPrimary', 'secondary-source edit preserves primary contents and FollowPrimary relation/policy')
        path, opened = self.save_close_open('content-roundtrip', path,
            self.request('content-current', 'GET', path)['projectRevision'], 'content-saved')
        self.close('normal-close', path, opened['projectRevision'])
        self.readonly()
        self.specialized()
        self.null_duration_export()
        self.oversized_old()
        self.finish()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    args.output = args.output.resolve()
    if not args.output.is_relative_to(timing.s1.REPO / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    args.output.mkdir(parents=True, exist_ok=False)
    probe = ContentProbe(args.output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
