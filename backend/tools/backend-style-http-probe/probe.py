#!/usr/bin/env python3
"""Owned real HTTP static-style editing acceptance; never uses user projects."""
import argparse
import copy
import hashlib
import importlib.util
import io
import json
import struct
import subprocess
import time
from pathlib import Path
from urllib.parse import urlencode
import wave

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('style_token_base', ROOT / 'tools/backend-web-editing/token_probe.py')
base = importlib.util.module_from_spec(spec)
spec.loader.exec_module(base)
content = base.content
save_json = base.save_json
TYPES = ('styles.patch', 'styles.restore')
PROPERTIES = ('typography.fontFamilyName', 'typography.fallbackFamilyNames', 'typography.fontSizePx', 'typography.fontWeight',
    'typography.isBold', 'typography.isItalic', 'typography.letterSpacingPx', 'unsungFill.material', 'sungFill.material',
    'stroke', 'stroke.material', 'stroke.widthPx', 'stroke.join', 'stroke.position', 'shadow', 'shadow.color',
    'shadow.opacity', 'shadow.angleDegrees', 'shadow.distancePx', 'shadow.blurRadiusPx', 'verticalCenterRatio',
    'horizontalAlignment', 'opacity', 'scale')
PROJECT = {'kind': 'project'}
BRANCH = {'kind': 'branch', 'branchId': 'primary'}
LINE = {'kind': 'line', 'branchId': 'primary', 'lineId': 'one'}
TOKEN = {**LINE, 'kind': 'token', 'tokenId': 'first'}
OTHER = {'kind': 'branch', 'branchId': 'translation'}
OTHER_LINE = {**OTHER, 'kind': 'line', 'lineId': 'translated'}
OTHER_TOKEN = {**OTHER_LINE, 'kind': 'token', 'tokenId': 'translated-token'}


def rgba(red=255, green=0, blue=0, alpha=255):
    return dict(red=red, green=green, blue=blue, alpha=alpha)


def solid(color=None):
    return {'kind': 'solid', 'color': color if color is not None else rgba()}


def gradient(count=4, **kwargs):
    result = dict(kind='linear', angleDegrees=17.25, centerX=.375, centerY=.625, radiusX=.25, radiusY=.75,
                  coordinateSpace='line', colorSpace='hsv', huePath='longer', stops=[
        {'offset': i / (count - 1), 'color': rgba(i % 256, (255 - i) % 256, (i * 37) % 256, (i * 53) % 256)} for i in range(count)])
    result.update(kwargs)
    return {'kind': 'gradient', 'gradient': result}


def stroke(material=None):
    return dict(material=material or solid(rgba(10, 20, 30, 200)), widthPx=2.5, join='round', position='outside')


def shadow():
    return dict(color=rgba(20, 30, 40, 180), opacity=.75, angleDegrees=27.5, distancePx=3.5, blurRadiusPx=4.5)


def baseline():
    return dict(typography=dict(fontFamilyName=None, fallbackFamilyNames=[], fontSizePx=40, fontWeight=450,
        isBold=False, isItalic=True, letterSpacingPx=.25), unsungFill={'material': solid(rgba(210, 100, 30, 255))},
        sungFill={'material': solid(rgba(40, 200, 230, 255))}, stroke=stroke(), shadow=shadow(),
        verticalCenterRatio=.5, horizontalAlignment='center', opacity=1, scale=1)


def key(target):
    return tuple(target.get(k) for k in ('kind', 'branchId', 'lineId', 'tokenId'))


def patch(targets, *changes):
    return {'type': TYPES[0], 'targets': copy.deepcopy(targets if isinstance(targets, list) else [targets]),
            'changes': [{'property': prop, 'value': copy.deepcopy(value)} for prop, value in changes]}


def restore(targets, *properties):
    return {'type': TYPES[1], 'targets': copy.deepcopy(targets if isinstance(targets, list) else [targets]), 'properties': list(properties)}


def flatten(entries):
    result = {}
    for entry in entries:
        prop, value = entry['property'], entry['value']
        if prop in ('stroke', 'shadow') and value is not None:
            result.update({prop + '.' + child: copy.deepcopy(v) for child, v in value.items()})
        else:
            result[prop] = copy.deepcopy(value)
    return result


def set_path(style, path, value):
    parts = path.split('.')
    if len(parts) == 1:
        style[path] = copy.deepcopy(value)
    else:
        group, field = parts
        if style[group] is None:
            style[group] = (dict(material=solid(rgba(0, 0, 0, 0)), widthPx=0, join='round', position='outside') if group == 'stroke' else
                dict(color=rgba(0, 0, 0, 0), opacity=1, angleDegrees=0, distancePx=0, blurRadiusPx=0))
        style[group][field] = copy.deepcopy(value)


class StyleOracle:
    """Small independent property/presence oracle, with no production imports."""
    def __init__(self, style=None):
        self.baseline = copy.deepcopy(style or baseline())
        self.locals = {}

    def local(self, target):
        return self.locals.get(key(target), {})

    def effective(self, target):
        result = copy.deepcopy(self.baseline)
        if target['kind'] == 'project':
            return result
        hierarchy = [dict(kind='branch', branchId=target['branchId'])]
        if target['kind'] in ('line', 'token'):
            hierarchy.append(dict(kind='line', branchId=target['branchId'], lineId=target['lineId']))
        if target['kind'] == 'token':
            hierarchy.append(target)
        for item in hierarchy:
            for prop, value in self.local(item).items():
                set_path(result, prop, value)
        return result

    def apply(self, command):
        changed = 0
        for target in command['targets']:
            before = copy.deepcopy(self.baseline if target['kind'] == 'project' else self.local(target))
            if target['kind'] == 'project':
                for entry in command['changes']:
                    set_path(self.baseline, entry['property'], entry['value'])
                after = self.baseline
            else:
                after = copy.deepcopy(before)
                if command['type'] == TYPES[1]:
                    for prop in command['properties']:
                        if prop in ('stroke', 'shadow'):
                            after = {k: v for k, v in after.items() if k != prop and not k.startswith(prop + '.')}
                        else:
                            after.pop(prop, None)
                else:
                    for entry in command['changes']:
                        prop, value = entry['property'], entry['value']
                        if prop in ('stroke', 'shadow'):
                            after = {k: v for k, v in after.items() if k != prop and not k.startswith(prop + '.')}
                        elif prop.startswith(('stroke.', 'shadow.')):
                            after.pop(prop.split('.')[0], None)
                        after.update(flatten([entry]))
                if after:
                    self.locals[key(target)] = after
                else:
                    self.locals.pop(key(target), None)
            changed += before != after
        return changed


def persistence_color(color):
    return '#' + ''.join(f'{color[k]:02X}' for k in ('red', 'green', 'blue', 'alpha'))


def persistence_material(value):
    result = copy.deepcopy(value)
    if result['kind'] == 'solid':
        result['color'] = persistence_color(result['color'])
    elif result['kind'] == 'gradient':
        g = result['gradient']
        for field in ('kind', 'coordinateSpace', 'colorSpace', 'huePath'):
            g[field] = g[field][0].upper() + g[field][1:]
        for stop in g['stops']:
            stop['color'] = persistence_color(stop['color'])
    return result


def persistence_overlay(overlay):
    result = {}
    for prop, value in overlay.items():
        parts = prop.split('.')
        if parts[-1] == 'material':
            value = persistence_material(value)
        if prop.startswith('stroke.'):
            item = result.setdefault('stroke', {})
            if parts[1] == 'material':
                item['material'] = value
            else:
                item.setdefault('geometry', {})[parts[1]] = value.capitalize() if isinstance(value, str) else value
        elif prop.startswith('shadow.'):
            result.setdefault('shadow', {})[parts[1]] = persistence_color(value) if parts[1] == 'color' else value
        elif prop in ('unsungFill.material', 'sungFill.material'):
            result[parts[0]] = value
        elif len(parts) == 2:
            result.setdefault(parts[0], {})[parts[1]] = copy.deepcopy(value)
        else:
            result[prop] = value.capitalize() if prop == 'horizontalAlignment' else copy.deepcopy(value)
    return result


def persistence_baseline(style):
    result = copy.deepcopy(style)
    for group in ('unsungFill', 'sungFill'):
        result[group] = persistence_material(result[group]['material'])
    if result['stroke'] is not None:
        value = result['stroke']
        result['stroke'] = {'material': persistence_material(value['material']), 'geometry': {
            'widthPx': value['widthPx'], 'join': value['join'].capitalize(), 'position': value['position'].capitalize()}}
    if result['shadow'] is not None:
        result['shadow']['color'] = persistence_color(result['shadow']['color'])
    result['horizontalAlignment'] = result['horizontalAlignment'].capitalize()
    if result['typography'].get('fontFamilyName') is None:
        result['typography'].pop('fontFamilyName', None)
    return {k: v for k, v in result.items() if v is not None}


class StyleProbe(base.TokenProbe):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.oracle = StyleOracle()
        self.targets = [PROJECT, BRANCH, LINE, TOKEN, OTHER, OTHER_LINE, OTHER_TOKEN]
        self.unreadable = set()
        self.undo_models = []
        self.redo_models = []
        self.inputs = []
        self.preview_frames = []
        self.has_presets = False

    def content(self, name, path, revision):
        branches = self.get(name + '-branches0', path, '/lyrics/branches', revision, limit=100)
        sources = list(branches['branches'])
        offset = branches.get('nextOffset')
        while offset is not None:
            page = self.get(name + '-branches' + str(offset), path, '/lyrics/branches', revision, offset=offset, limit=100)
            self.check(page['offset'] == len(sources) and page['totalCount'] == branches['totalCount'], name + ': complete ordered source pagination')
            sources.extend(page['branches']); offset = page.get('nextOffset')
        self.check(len(sources) == branches['totalCount'], name + ': every source discovered')
        branches['branches'] = sources; branches.pop('nextOffset', None)
        settings = self.get(name + '-settings', path, '/settings', revision)
        windows = {}
        for index, source in enumerate(sources):
            pages = [self.get(name + '-window-' + str(index) + '-' + str(i), path, '/lyrics/window', revision,
                branchId=source['id'], offset=i, limit=1) for i in range(max(1, source['lineCount']))]
            rows = [row for page in pages for row in page['lines']]
            self.check(len(rows) == source['lineCount'], name + ': complete actual source windows ' + str(index))
            windows[source['id']] = {**pages[0], 'offset': 0, 'lines': rows}
            windows[source['id']].pop('nextOffset', None)
        return branches, settings, windows

    def is_unreadable(self, target):
        if key(target) not in self.unreadable:
            return False
        values = self.oracle.local(target)
        return len(values.get('typography.fontFamilyName') or '') > 500000 or len(values.get('unsungFill.material', {}).get('gradient', {}).get('stops', [])) > 10000

    def style(self, name, path, revision, target):
        result = self.request(name, 'GET', path + '/style?' + urlencode({'expectedRevision': revision, **target}),
            status=413 if self.is_unreadable(target) else 200, code='resourceTooLarge' if self.is_unreadable(target) else None)
        if self.is_unreadable(target):
            self.check(result.get('field') == 'style' and 'failureCode' not in result, name + ': complete old read-budget failure field')
            return {'unavailable': 'resourceTooLarge', 'target': target}
        self.check(set(result) == {'projectSessionId', 'projectRevision', 'protocolVersion', 'target', 'baseline', 'local',
            'staticHierarchy', 'hasPresetApplications', 'resolution'}, name + ': closed complete style read shape')
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1] and result['projectRevision'] == revision and
            result['protocolVersion'] == '1' and result['target'] == target, name + ': exact session/revision/stable target')
        self.check(result['resolution'] == 'staticHierarchy' and result['hasPresetApplications'] is self.has_presets,
            name + ': static hierarchy and whole-project preset presence are explicitly labelled')
        self.check(result['baseline'] == self.oracle.baseline and result['staticHierarchy'] == self.oracle.effective(target),
            name + ': independent full canonical baseline and four-level hierarchy oracle')
        self.check(all(set(item) == {'property', 'value'} and item['property'] in PROPERTIES for item in result['local']) and
            len({item['property'] for item in result['local']}) == len(result['local']), name + ': raw presence list, no unknown/duplicate/missing value')
        self.check(flatten(result['local']) == ({} if target['kind'] == 'project' else self.oracle.local(target)),
            name + ': raw local members retain absent/null distinction without frozen inherited fields')
        for prop in ('stroke', 'shadow'):
            if self.oracle.local(target).get(prop, 'absent') is None:
                self.check({'property': prop, 'value': None} in result['local'], name + ': explicit ' + prop + ' null survives global JSON omission policy')
        return result

    def state(self, name, path):
        state = super().state(name, path)
        state['styles'] = [self.style(name + '-style-' + str(i), path, state['summary']['projectRevision'], target)
                           for i, target in enumerate(self.targets)]
        return state

    @staticmethod
    def normalized_styles(state):
        values = copy.deepcopy(state['styles'])
        for value in values:
            value.pop('projectSessionId', None); value.pop('projectRevision', None)
        return values

    def same_content(self, before, after, name):
        super().same_content(before, after, name)
        self.check(self.normalized_styles(before) == self.normalized_styles(after), name + ': complete raw and inherited styles preserved')

    def accepted(self, name, path, command, *, changed=True, refresh=None, check=None, **unused):
        before = self.state(name + '-before', path)
        old_model = copy.deepcopy(self.oracle)
        new_model = copy.deepcopy(self.oracle)
        count = new_model.apply(command)
        self.check(bool(count) is changed, name + ': independent original property/presence change count')
        payload = self.payload(name, before['summary']['projectRevision'], command)
        result = self.request(name, 'POST', path + '/commands', value=payload)
        self.oracle = new_model
        if changed:
            self.undo_models.append(old_model); self.redo_models.clear()
        after = self.state(name + '-after', path)
        previous = before['summary']['projectRevision']
        self.check(result['projectSessionId'] == path.rsplit('/', 1)[1] and result['previousRevision'] == previous and
            result['projectRevision'] == str(int(previous) + int(changed)) == after['summary']['projectRevision'] and result['changed'] is changed,
            name + ': exact single commit and revision identity')
        self.check(result.get('styleResult') == {'changedTargetCount': count} and
            all(field not in result for field in ('failureCode', 'feedback', 'tokenResult', 'timingResult', 'branchResult', 'importResult')),
            name + ': bounded truthful style-only result')
        if changed:
            self.check(after['history']['undoCount'] == before['history']['undoCount'] + 1 and after['history']['redoCount'] == 0 and
                result['undoName'] == command['type'] == after['history']['undoName'] and result['isDirty'] and
                after['eventCursor'] != before['eventCursor'], name + ': one HTTP intent is one history/revision/event')
            changes = result['changeSet']
            self.check(isinstance(changes['areas'], list) and bool(changes['areas']) and all(isinstance(changes[k], list)
                for k in ('branchIds', 'lineIds', 'tokenIds')), name + ': complete bounded style changeSet')
            self.check(changes['areas'] == ['styles', 'renderInputs'] and changes['requiresFullRefresh'] is True, name + ': exact original style refresh areas')
            if refresh is False:
                for field, id_field in [('branchIds', 'branchId'), ('lineIds', 'lineId'), ('tokenIds', 'tokenId')]:
                    self.check(changes[field] == list(dict.fromkeys(t[id_field] for t in command['targets'] if id_field in t)), name + ': exact complete selected ' + field)
            if refresh:
                self.check(all(changes[k] == [] for k in ('branchIds', 'lineIds', 'tokenIds')), name + ': fallback never returns partial IDs')
        else:
            self.same_state(before, after, name)
            self.check(result['changeSet'] == {'areas': [], 'branchIds': [], 'lineIds': [], 'tokenIds': [], 'requiresFullRefresh': False},
                name + ': no-op zero changeSet and no new event')
        # S1 content intentionally excludes style, so this also proves text/time,
        # ruby/links/source metadata/composition are not silently rewritten.
        self.check(self.normalize_content(before['models']) == self.normalize_content(after['models']), name + ': every non-style editable field unchanged')
        if check:
            check(before, after, result)
        return before, after, result, payload

    def history(self, name, path, action, **unused):
        previous = self.request(name + '-current', 'GET', path)['projectRevision']
        source, destination = (self.undo_models, self.redo_models) if action == 'undo' else (self.redo_models, self.undo_models)
        self.check(bool(source), name + ': expected real history model exists')
        next_model = source.pop(); destination.append(copy.deepcopy(self.oracle))
        result = self.request(name, 'POST', path + '/commands', value=self.payload(name, previous,
            {'type': 'history.' + action, 'target': None, 'startUs': None, 'endUs': None, 'analysisJobId': None}))
        self.check(result['changed'] and result['projectRevision'] == str(int(previous) + 1) and 'styleResult' not in result,
            name + ': actual history action changes revision and omits style-only feedback')
        self.oracle = next_model
        return self.state(name + '-after', path)

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
        self.check(result.get('failureCode') == failure if failure else 'failureCode' not in result, name + ': precise optional domain reason')
        self.check(all(field not in result for field in ('styleResult', 'feedback', 'tokenResult', 'importResult', 'branchResult', 'timingResult')),
            name + ': rejection contains no success feedback')
        if failure and field is None:
            field = 'command.targets' if failure == 'InvalidTarget' else ('command.properties' if command['type'] == TYPES[1] else 'command.changes')
        if field:
            self.check(result.get('field') == field, name + ': actual input error path')
        self.same_state(before, self.state(name + '-after', path), name)
        return result

    def standard_fixture(self, project, lyrics):
        self.fixture_content(project, lyrics)
        project['lyricStyle'] = persistence_baseline(baseline())
        project['lyricStylePresetId'] = 'probe-owned-baseline-origin'
        self.has_presets = False
        self.unreadable.clear()
        self.targets = [PROJECT, BRANCH, LINE, TOKEN, OTHER, OTHER_LINE, OTHER_TOKEN]
        for branch_id, line_id, token_id in [('b' * 129, 'l' * 129, 't' * 129), (' source ', ' line ', ' token ')]:
            lyrics['branches'].append({'id': branch_id, 'name': 'same visible source', 'role': 'Custom', 'timingMode': 'Independent',
                'lines': [{'id': line_id, 'text': '甲', 'startUs': 0, 'endUs': 1000000, 'timingState': 'Imported',
                    'tokens': [{'id': token_id, 'text': '甲', 'startUs': 0, 'endUs': 1000000, 'timingState': 'Imported'}]}]})
            self.targets.extend([{'kind': 'branch', 'branchId': branch_id}, {'kind': 'line', 'branchId': branch_id, 'lineId': line_id},
                {'kind': 'token', 'branchId': branch_id, 'lineId': line_id, 'tokenId': token_id}])
        self.oracle = StyleOracle()
        local = {key(BRANCH): {'opacity': .75, 'typography.fontSizePx': 42}, key(LINE): {'stroke.widthPx': 3.5, 'shadow.blurRadiusPx': 6.5},
            key(TOKEN): {'opacity': .5}, key(OTHER): {'typography.fontFamilyName': None, 'sungFill.material': gradient()},
            key(OTHER_LINE): {'shadow': None}, key(OTHER_TOKEN): {'stroke': None}}
        self.oracle.locals = copy.deepcopy(local)
        for source in lyrics['branches']:
            target = {'kind': 'branch', 'branchId': source['id']}
            if key(target) in local:
                source['styleOverride'] = persistence_overlay(local[key(target)])
            for line in source['lines']:
                target = {**target, 'kind': 'line', 'lineId': line['id']}
                if key(target) in local:
                    line['styleOverride'] = persistence_overlay(local[key(target)])
                for tok in line['tokens']:
                    target_token = {**target, 'kind': 'token', 'tokenId': tok['id']}
                    if key(target_token) in local:
                        tok['styleOverride'] = persistence_overlay(local[key(target_token)])

    def reset_fixture(self, name, modify=None):
        relative = self.fixture(name, modify or self.standard_fixture)
        self.undo_models.clear(); self.redo_models.clear()
        path, opened = self.open(name + '-open', relative)
        return path

    def roundtrip(self, name, path):
        before = self.state(name + '-before-roundtrip', path)
        path, opened = self.save_close_open(name, path, before['summary']['projectRevision'], name + '-saved')
        self.undo_models.clear(); self.redo_models.clear()
        after = self.state(name + '-reopened-styles', path)
        self.same_content(before, after, name + ': all raw/inherited styles survive new session')
        self.close(name + '-close', path, opened['projectRevision'])
        return self.output / 'profile/projects' / (name + '-saved')

    def core_sequence(self, path):
        original = self.state('original', path)
        self.exercise('seed-history-redo', path, patch(LINE, ('opacity', .625)))
        self.history('seed-history-undo', path, 'undo')
        for prop in ('typography.fontFamilyName', 'typography.fallbackFamilyNames', 'stroke', 'shadow'):
            value = self.oracle.baseline[prop] if '.' not in prop else self.oracle.baseline['typography'][prop.split('.')[1]]
            self.accepted('baseline-equal-noop-' + prop, path, patch(PROJECT, (prop, value)), changed=False)
        self.accepted('closed-effect-leaf-restore-noop', path, restore(OTHER_TOKEN, 'stroke.widthPx'), changed=False)
        self.accepted('already-inherited-restore-noop', path, restore(LINE, 'typography.fontFamilyName'), changed=False)
        handle = self.request('origin-noop-handle', 'POST', '/api/v1/file-handles', status=201,
            value={'rootGrantId': 'projects', 'relativePath': 'style-origin-noop'})
        revision = self.request('origin-current', 'GET', path)['projectRevision']
        self.job('origin-noop-save', self.request('origin-noop-save', 'POST', path + '/save-jobs', status=202,
            value={'expectedRevision': revision, 'targetHandleId': handle['handleId']}))
        saved = json.loads((self.output / 'profile/projects/style-origin-noop/project.json').read_text())
        self.check(saved['lyricStylePresetId'] == 'probe-owned-baseline-origin', 'value-equal/no-op preserves original preset identity')
        self.history('seed-history-redo-final', path, 'redo')
        values = [None, [' Noto Sans ', 'フォント e\u0301'], 52.5, 451, True, False, -.75, gradient(), solid(rgba(2, 3, 4, 5)),
            stroke(gradient()), solid(rgba(3, 5, 7, 9)), 5.5, 'bevel', 'outside', shadow(), rgba(5, 7, 9, 11), .5, 721.25, 9.5, 10.5,
            .625, 'right', .875, 1.125]
        for index, (prop, value) in enumerate(zip(PROPERTIES, values)):
            same = value == self.oracle.baseline.get(prop) if '.' not in prop else value == self.oracle.baseline[prop.split('.')[0]][prop.split('.')[1]]
            if same:
                self.accepted('project-member-noop-' + str(index), path, patch(PROJECT, (prop, value)), changed=False)
            else:
                self.exercise('project-member-' + str(index), path, patch(PROJECT, (prop, value)))
        self.exercise('secondary-branch-typography', path, patch(OTHER, ('typography.fontSizePx', 23.5), ('opacity', .625)))
        self.exercise('secondary-line-font-null', path, patch(OTHER_LINE, ('typography.fontFamilyName', None)))
        self.exercise('secondary-token-fill', path, patch(OTHER_TOKEN, ('sungFill.material', gradient())))
        self.exercise('leaf-width-retains-inherited-material', path, patch(LINE, ('stroke.widthPx', 7.5)))
        self.exercise('leaf-blur-retains-shadow-other-members', path, patch(LINE, ('shadow.blurRadiusPx', 8.5)))
        self.exercise('line-local-font', path, patch(LINE, ('typography.fontFamilyName', ' 本地 e\u0301 👩🏽‍🚀 ')))
        self.exercise('parent-font-below-local', path, patch(BRANCH, ('typography.fontFamilyName', 'Parent A')))
        self.exercise('restore-line-font', path, restore(LINE, 'typography.fontFamilyName'))
        self.exercise('parent-font-after-restore', path, patch(BRANCH, ('typography.fontFamilyName', 'Parent B')))
        for effect in ('stroke', 'shadow'):
            self.exercise('explicit-close-' + effect, path, patch(LINE, (effect, None)))
            self.exercise('parent-edit-below-closed-' + effect, path, patch(BRANCH, (effect, stroke(gradient()) if effect == 'stroke' else shadow())))
            self.accepted('closed-leaf-restore-noop-' + effect, path, restore(LINE, effect + ('.widthPx' if effect == 'stroke' else '.blurRadiusPx')), changed=False)
            self.exercise('restore-whole-' + effect, path, restore(LINE, effect))
        self.exercise('leaf-overrides-closed-token', path, patch(OTHER_TOKEN, ('stroke.widthPx', 6.5)))
        self.exercise('restore-token-last-member', path, restore(OTHER_TOKEN, 'stroke.widthPx', 'sungFill.material'))
        for i in range(4):
            value = gradient(kind=('linear', 'radial')[i % 2], coordinateSpace=('canvas', 'line', 'glyph', 'progress')[i],
                colorSpace=('linearRgb', 'hsv', 'oklch')[i % 3], huePath=('shorter', 'longer', 'increasing', 'decreasing')[i])
            self.exercise('gradient-full-enums-' + str(i), path, patch(OTHER_TOKEN, ('unsungFill.material', value)))
        value = gradient(stops=[{'offset': value, 'color': rgba(i * 50, 5, 7, i * 60)} for i, value in enumerate((0, .5, .5, 1))])
        self.exercise('gradient-equal-stop-offsets', path, patch(OTHER_TOKEN, ('unsungFill.material', value)))
        self.history('gradient-equal-undo', path, 'undo')
        current = self.oracle.local(OTHER_TOKEN)['unsungFill.material']
        self.accepted('gradient-new-array-valueequal-noop', path, patch(OTHER_TOKEN, ('unsungFill.material', copy.deepcopy(current))), changed=False)
        self.history('gradient-equal-redo', path, 'redo')
        self.exercise('fallback-leaf-preserves-font', path, patch(OTHER, ('typography.fallbackFamilyNames', ['A', '日本語', 'e\u0301'])))
        self.history('fallback-undo', path, 'undo')
        current = self.oracle.local(OTHER).get('typography.fallbackFamilyNames')
        if current is None:
            self.accepted('fallback-restore-noop', path, restore(OTHER, 'typography.fallbackFamilyNames'), changed=False)
        else:
            self.accepted('fallback-new-array-valueequal-noop', path, patch(OTHER, ('typography.fallbackFamilyNames', copy.deepcopy(current))), changed=False)
        self.history('fallback-redo', path, 'redo')
        self.check(original['models'][2]['primary']['lines'] == self.state('all-styles-final', path)['models'][2]['primary']['lines'],
            'all style actions retain original primary authoritative text/ruby/time/IDs')

    def malformed(self, path):
        good = patch(LINE, ('opacity', .5))
        cases = [dict(type=TYPES[0]), {**good, 'targets': None}, {**good, 'targets': []}, {**good, 'targets': [None]},
            {**good, 'targets': [PROJECT, BRANCH]}, {**good, 'targets': [LINE, LINE]}, {**good, 'changes': None},
            {**good, 'changes': []}, {**good, 'changes': [None]}, {**good, 'changes': [{'property': 'opacity'}]},
            {**good, 'changes': [{'property': 'opacity', 'value': None}]}, {**good, 'changes': good['changes'] * 2},
            {**good, 'changes': [{'property': 'Opacity', 'value': 1}]}, {**good, 'changes': [{'property': 'unknown', 'value': 1}]},
            patch(LINE, ('stroke', None), ('stroke.widthPx', 2)), patch(LINE, ('shadow', None), ('shadow.opacity', .5)),
            {**good, 'properties': []}, {**restore(LINE, 'opacity'), 'changes': []}, restore(LINE),
            restore(LINE, 'opacity', 'opacity'), restore(LINE, 'stroke', 'stroke.material')]
        for target in ({}, {'kind': 'Line', 'branchId': 'primary', 'lineId': 'one'}, {**PROJECT, 'branchId': None},
            {**BRANCH, 'lineId': None}, {**LINE, 'tokenId': None}, {**TOKEN, 'extra': None},
            {'kind': 'branch', 'branchId': None}, {'kind': 'line', 'branchId': 'primary'},
            {'kind': 'token', 'branchId': 'primary', 'lineId': 'one'}, {**LINE, 'lineId': ''}):
            cases.append({**good, 'targets': [target]})
        for prop, values in {'typography.fontSizePx': ['20', True], 'typography.fontWeight': ['400', 450.5, True],
            'typography.fallbackFamilyNames': [None, 'A', [None], [1]], 'typography.isBold': ['false', 1, None],
            'horizontalAlignment': ['Right', '1', 1, None], 'stroke.join': ['Round', '0', 0], 'stroke.position': ['inside', None],
            'shadow.color': [None, '#FF0000FF', {'red': 255, 'green': 0, 'blue': 0}, {**rgba(), 'red': 256}, {**rgba(), 'alpha': -1}, {**rgba(), 'red': '255'}],
            'sungFill.material': [None, {'kind': 'solid'}, {**solid(), 'extra': None}, {'kind': 'package', 'packageId': 'p', 'revision': 'sha256:' + 'a' * 64, 'exportId': 'ink'}]}.items():
            cases.extend(patch(LINE, (prop, value)) for value in values)
        for field in ('target', 'startUs', 'endUs', 'analysisJobId', 'extra'):
            cases.append({**good, field: None})
        for index, command in enumerate(cases):
            self.rejected('shape-' + str(index), path, command, 400, 'invalidRequest')
        raw = json.dumps(good)
        for name, command in [('type', raw[:-1] + ',"type":"styles.patch"}'),
            ('target', '{"type":"styles.patch","targets":[{"kind":"line","branchId":"primary","lineId":"one","lineId":"one"}],"changes":[{"property":"opacity","value":0.5}]}'),
            ('value', '{"type":"styles.patch","targets":[{"kind":"project"}],"changes":[{"property":"opacity","value":0.5,"value":0.5}]}')]:
            self.rejected('shape-duplicate-' + name, path, good, 400, 'invalidRequest', raw_command=command)
        state = self.state('query-before', path)
        revision = state['summary']['projectRevision']
        query = urlencode({'expectedRevision': revision, **TOKEN})
        cases = ['kind=project', 'expectedRevision=' + revision, query + '&extra=1', query + '&expectedRevision=' + revision,
            query + '&kind=token', query + '&lineId=one', query + '&branchId=primary', query + '&tokenId=first',
            query.replace('tokenId=first', 'tokenId='), query.replace('expectedRevision=' + revision, 'expectedRevision=%2B0'),
            urlencode({'expectedRevision': revision, **PROJECT, 'branchId': 'primary'}),
            urlencode({'expectedRevision': revision, **BRANCH, 'lineId': 'one'}),
            urlencode({'expectedRevision': revision, **LINE, 'tokenId': 'first'})]
        for i, value in enumerate(cases):
            self.request('style-query-' + str(i), 'GET', path + '/style?' + value, status=400, code='invalidRequest')
        self.same_state(state, self.state('query-after', path), 'all GET errors preserve full state')

    def domain(self, path):
        for prop, value in [('typography.fontSizePx', 0), ('typography.fontWeight', 99), ('typography.fontWeight', 901),
            ('opacity', -.1), ('opacity', 1.1), ('scale', 0), ('verticalCenterRatio', -1), ('verticalCenterRatio', 1.1),
            ('stroke.widthPx', -1), ('shadow.opacity', 2), ('shadow.distancePx', -1), ('shadow.blurRadiusPx', -1),
            ('typography.fallbackFamilyNames', ['A', 'A']), ('typography.fallbackFamilyNames', [' '])]:
            self.rejected('domain-value-' + str(self.command_counter), path, patch(LINE, (prop, value)), 422, 'commandRejected', 'InvalidValue')
        for prop in ('typography.fontFamilyName', 'typography.fallbackFamilyNames', 'typography.fontSizePx', 'typography.fontWeight',
            'typography.isBold', 'typography.isItalic', 'typography.letterSpacingPx', 'verticalCenterRatio', 'horizontalAlignment', 'scale'):
            value = self.oracle.baseline[prop] if '.' not in prop else self.oracle.baseline['typography'][prop.split('.')[1]]
            self.rejected('unsupported-token-' + str(self.command_counter), path, patch(TOKEN, (prop, value)), 422, 'commandRejected', 'UnsupportedProperty')
        self.rejected('project-restore-no-parent', path, restore(PROJECT, 'opacity'), 422, 'commandRejected', 'UnsupportedProperty')
        for command in [patch([LINE, {**LINE, 'lineId': 'missing'}], ('opacity', .5)),
            patch([TOKEN, {**TOKEN, 'tokenId': 'missing'}], ('opacity', .5)), restore([{**OTHER, 'branchId': 'missing'}], 'opacity')]:
            self.rejected('second-target-atomic-' + str(self.command_counter), path, command, 422, 'commandRejected', 'InvalidTarget')
        for value in [gradient(stops=[{'offset': 0, 'color': rgba()}]), gradient(stops=[{'offset': .1, 'color': rgba()}, {'offset': 1, 'color': rgba()}]),
            gradient(stops=[{'offset': 0, 'color': rgba()}, {'offset': .75, 'color': rgba()}, {'offset': .5, 'color': rgba()}, {'offset': 1, 'color': rgba()}]),
            gradient(kind='radial', centerX=-.1), gradient(kind='radial', radiusX=0)]:
            self.rejected('invalid-gradient-' + str(self.command_counter), path, patch(LINE, ('unsungFill.material', value)), 422, 'commandRejected', 'InvalidValue')

    def stable_ids(self, path):
        for i, target in enumerate(self.targets[7:]):
            self.exercise('long-or-spaced-id-' + str(i), path, patch(target, ('opacity', .625)), refresh=False)
        state = self.state('trim-before', path)
        revision = state['summary']['projectRevision']
        for target in self.targets[-3:]:
            trimmed = {k: v.strip() if k.endswith('Id') else v for k, v in target.items()}
            result = self.request('trimmed-target-' + target['kind'], 'GET', path + '/style?' + urlencode({'expectedRevision': revision, **trimmed}),
                status=404, code='commandRejected')
            self.check(result.get('failureCode') == 'TargetStale' and result.get('field') == 'target', 'trimmed ID never aliases exact stable target')
            self.rejected('trimmed-write-' + target['kind'], path, patch(trimmed, ('opacity', .5)), 422, 'commandRejected', 'InvalidTarget')
        self.same_state(state, self.state('trim-after', path), 'exact whitespace IDs')

    def receipts_and_races(self, path):
        commands = [patch(LINE, ('typography.fallbackFamilyNames', ['A', 'B']), ('unsungFill.material', gradient()), ('shadow', shadow())),
                    restore(LINE, 'typography.fallbackFamilyNames', 'unsungFill.material', 'shadow')]
        for index, command in enumerate(commands):
            name = 'receipt-' + str(index)
            _, after, original, payload = self.accepted(name, path, command)
            retry = {**payload, 'expectedRevision': '000' + payload['expectedRevision'], 'command': copy.deepcopy(command)}
            self.check(self.request(name + '-stale-canonical-retry', 'POST', path + '/commands', value=retry) == original,
                name + ': original complete receipt before current revision gate')
            variants = []
            changed = copy.deepcopy(command); changed['targets'] = [OTHER_LINE]; variants.append(changed)
            if index == 0:
                for entry_index in range(len(command['changes'])):
                    changed = copy.deepcopy(command)
                    entry = changed['changes'][entry_index]
                    if entry_index == 0:entry['value'][0] = 'different'
                    elif entry_index == 1:entry['value']['gradient']['stops'][1]['color']['red'] = 199
                    else:entry['value']['distancePx'] = 19
                    variants.append(changed)
                changed = copy.deepcopy(command); changed['changes'].reverse(); variants.append(changed)
            else:
                changed = copy.deepcopy(command); changed['properties'][0] = 'opacity'; variants.append(changed)
            for i, different in enumerate(variants):
                self.request(name + '-payload-reuse-' + str(i), 'POST', path + '/commands', value={**payload, 'command': different}, status=409, code='requestIdReuse')
            self.request(name + '-revision-reuse', 'POST', path + '/commands', value={**payload, 'expectedRevision': after['summary']['projectRevision']}, status=409, code='requestIdReuse')
            deadline = content.timing.s1.smoke.existing.iso(content.timing.s1.smoke.existing.utc_now() + content.timing.s1.smoke.existing.timedelta(seconds=95))
            self.request(name + '-deadline-reuse', 'POST', path + '/commands', value={**payload, 'validUntilUtc': deadline}, status=409, code='requestIdReuse')
            self.same_state(after, self.state(name + '-stable', path), name + ': replay/reuse zero mutation')
            undone = self.history(name + '-undo', path, 'undo')
            self.check(self.request(name + '-retry-after-undo', 'POST', path + '/commands', value=payload) == original, name + ': undo does not expire immutable receipt')
            self.same_state(undone, self.state(name + '-undo-retry-stable', path), name)
            self.same_content(after, self.history(name + '-redo', path, 'redo'), name + ': redo restores all layers')
        before = self.state('race-before', path); revision = before['summary']['projectRevision']
        payloads = [self.payload('race-' + str(i), revision, patch(TOKEN, ('opacity', value))) for i, value in enumerate((.375, .625))]
        results = self.concurrent('styles-distinct-race', path, payloads)
        self.check(sorted(r[0] for r in results) == [200, 409], 'one accepted exact-revision writer')
        winner = next(i for i, r in enumerate(results) if r[0] == 200)
        self.check(next(r[2]['code'] for r in results if r[0] == 409) == 'revisionConflict', 'losing writer exact revision conflict')
        self.undo_models.append(copy.deepcopy(self.oracle)); self.redo_models.clear(); self.oracle.apply(payloads[winner]['command'])
        after = self.state('race-after', path)
        self.check(after['summary']['projectRevision'] == str(int(revision) + 1) and after['history']['undoCount'] == before['history']['undoCount'] + 1, 'race only one commit/history')
        command = patch(TOKEN, ('opacity', .875))
        payload = self.payload('same-owner', after['summary']['projectRevision'], command)
        results = self.concurrent('styles-same-receipt', path, [payload, copy.deepcopy(payload)])
        self.check([r[0] for r in results] == [200, 200] and results[0][2] == results[1][2], 'same receipt concurrent complete equality')
        self.undo_models.append(copy.deepcopy(self.oracle)); self.oracle.apply(command)
        final = self.state('same-owner-after', path)
        self.check(final['summary']['projectRevision'] == str(int(revision) + 2) and final['history']['undoCount'] == after['history']['undoCount'] + 1, 'same owner commits once')
        self.request('stale-new-intent', 'POST', path + '/commands', value=self.payload('stale-new', revision, patch(LINE, ('opacity', .125))), status=409, code='revisionConflict')
        self.request('stale-get-style', 'GET', path + '/style?' + urlencode({'expectedRevision': revision, **LINE}), status=409, code='revisionConflict')
        self.same_state(final, self.state('stale-after', path), 'stale read/write zero mutation')

    def auth(self, path):
        before = self.state('auth-before', path)
        other = self.register('style-other', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        for target in self.targets[:7]:
            query = urlencode({'expectedRevision': before['summary']['projectRevision'], **target})
            self.request('cross-client-get-' + str(self.command_counter), 'GET', path + '/style?' + query, client=other['clientId'], status=404, code='sessionNotFound')
        for command in (patch(LINE, ('opacity', .125)), restore(LINE, 'opacity')):
            self.request('cross-client-' + command['type'], 'POST', path + '/commands', client=other['clientId'],
                value=self.payload('foreign', before['summary']['projectRevision'], command), status=404, code='sessionNotFound')
        self.same_state(before, self.state('auth-after', path), 'cross-client exact zero mutation')
        # Read-only reopens the same immutable owned fixture; independent oracle is
        # reset to that fixture, then restored for the still-live writing session.
        model, targets, undo, redo = copy.deepcopy(self.oracle), self.targets, self.undo_models, self.redo_models
        relative = self.fixture('style-readonly', self.standard_fixture)
        handle = self.request('readonly-handle', 'POST', '/api/v1/file-handles', status=201, value={'rootGrantId': 'projects', 'relativePath': relative})
        opened = self.job('readonly-open', self.request('readonly-open', 'POST', '/api/v1/project-sessions/open-jobs', status=202,
            value={'projectDirectoryHandleId': handle['handleId'], 'access': 'read'}))['result']
        readonly = '/api/v1/project-sessions/' + opened['projectSessionId']
        self.check(opened['isReadOnly'], 'actual read-only session')
        self.state('readonly-readable', readonly)
        for command in (patch(LINE, ('opacity', .125)), restore(LINE, 'opacity')):
            result = self.rejected('readonly-' + command['type'], readonly, command, 403, 'readOnlySession')
            self.check('field' not in result, 'shared pre-dispatch read-only rejection omits field')
        self.close('readonly-close', readonly, opened['projectRevision'])
        self.oracle, self.targets, self.undo_models, self.redo_models = model, targets, undo, redo

    def binary(self, name, path, **kwargs):
        prefix = f'{self.request_count + 1:03d}-{name}'
        save_json(self.output / (prefix + '.request.json'), {'method': 'GET', 'path': path, 'headers': {
            'Authorization': '<redacted>', 'X-Mako-Client-Id': self.client, 'X-Mako-Host-Instance-Id': self.host}, 'json': None})
        started = time.perf_counter()
        try:
            return super().binary(name, path, **kwargs)
        finally:
            self.samples.append({'name': name, 'method': 'GET', 'path': path, 'elapsedMs': round((time.perf_counter() - started) * 1000, 3)})

    def preview_png(self, name, preview_path, preview, revision, sequence):
        self.request(name + '-request', 'POST', preview_path + '/frame-requests', status=202, value={
            'expectedSourceRevision': revision, 'previewGeneration': preview['previewGeneration'], 'requestSeq': str(sequence),
            'timeUs': '500000', 'intent': 'playback', 'metadata': self.metadata(name + '-frame')})
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            state = self.request(name + '-poll', 'GET', preview_path)['session']
            frame = state.get('latestFrame')
            if state['state'] == 'ready' and frame and frame['requestSeq'] == str(sequence):
                break
            self.check(state['state'] != 'failed', name + ': real Worker frame has no failure')
            time.sleep(.05)
        else:
            raise AssertionError(name + ': real preview did not finish')
        self.check(frame['sourceRevision'] == revision and frame['previewGeneration'] == preview['previewGeneration'], name + ': actual fixed input generation/revision')
        headers, png = self.binary(name + '-png', frame['content']['contentUrl'])
        self.check(headers['Content-Type'].split(';')[0] == 'image/png' and hashlib.sha256(png).hexdigest() == frame['content']['sha256'] and
            len(png) == frame['content']['sizeBytes'] and png[:8] == b'\x89PNG\r\n\x1a\n', name + ': complete authenticated PNG identity')
        rgba_bytes = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', 'pipe:0', '-f', 'rawvideo', '-pix_fmt', 'rgba', 'pipe:1'],
            input=png, check=True, capture_output=True, timeout=30).stdout
        self.check(len(rgba_bytes) == 320 * 180 * 4 and len(set(rgba_bytes[i:i+3] for i in range(0, len(rgba_bytes), 4))) > 1,
            name + ': entire PNG decoded, actual nonuniform visible content')
        self.preview_frames.append({'name': name, 'projectRevision': revision, 'pngSha256': hashlib.sha256(png).hexdigest(),
            'rgbaSha256': hashlib.sha256(rgba_bytes).hexdigest(), 'width': 320, 'height': 180})
        save_json(self.output / 'preview-input-handoff.json', {'frames': self.preview_frames,
            'scope': 'fixed static rendering input handoff and actual decoded pixel change/restoration; no independent Runtime/final-context oracle'})
        return rgba_bytes

    def preview_handoff(self):
        path = self.reset_fixture('style-preview')
        revision = self.request('preview-source', 'GET', path)['projectRevision']
        preview = self.request('style-preview-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': path.rsplit('/', 1)[1], 'sourceRevision': revision}, 'size': {'width': 320, 'height': 180},
            'alphaMode': 'opaque', 'purpose': 'preview', 'metadata': self.metadata('style-preview-create')})
        preview_path = '/api/v1/preview-sessions/' + preview['previewId']
        first = self.preview_png('preview-original', preview_path, preview, revision, 1)
        before, after, result, _ = self.accepted('preview-color-patch', path, patch(PROJECT,
            ('sungFill.material', solid(rgba(0, 255, 0))), ('unsungFill.material', solid(rgba(0, 255, 0)))))
        def update(name, old_preview, old_revision, new_revision):
            return self.request(name, 'PUT', preview_path + '/input', value={
                'expectedPreviewGeneration': old_preview['previewGeneration'], 'expectedSourceRevision': old_revision,
                'source': {'kind': 'project', 'sourceId': path.rsplit('/', 1)[1], 'sourceRevision': new_revision},
                'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque', 'metadata': self.metadata(name)})
        preview = update('preview-explicit-new-input', preview, revision, result['projectRevision'])
        changed = self.preview_png('preview-patched', preview_path, preview, result['projectRevision'], 2)
        self.check(changed != first, 'styles.patch becomes an accepted explicit preview input with real changed pixels')
        undone = self.history('preview-color-undo', path, 'undo')
        preview = update('preview-explicit-undone-input', preview, result['projectRevision'], undone['summary']['projectRevision'])
        restored = self.preview_png('preview-undone', preview_path, preview, undone['summary']['projectRevision'], 3)
        self.check(restored == first, 'undo source pixels restore exactly through explicit preview input replacement')
        self.request('style-preview-close', 'POST', preview_path + '/close', value={'expectedPreviewGeneration': preview['previewGeneration'],
            'expectedSourceRevision': undone['summary']['projectRevision'], 'metadata': self.metadata('style-preview-close')})
        self.roundtrip('style-preview', path)

    def budgets(self, path):
        self.exercise('new-font-256-utf16', path, patch(LINE, ('typography.fontFamilyName', '😀' * 128)))
        self.exercise('new-fallback-32', path, patch(LINE, ('typography.fallbackFamilyNames', ['Font ' + str(i) for i in range(32)])))
        self.exercise('new-stops-256', path, patch(TOKEN, ('sungFill.material', gradient(256))))
        self.history('budget-redo-seed', path, 'undo')
        cases = [('font-258-utf16', patch(LINE, ('typography.fontFamilyName', '😀' * 129))),
            ('fallback-33', patch(LINE, ('typography.fallbackFamilyNames', ['F' + str(i) for i in range(33)]))),
            ('fallback-name-257', patch(LINE, ('typography.fallbackFamilyNames', ['F' * 257]))),
            ('gradient-stops-257', patch(TOKEN, ('sungFill.material', gradient(257)))),
            ('intent-64k', patch([{'kind': 'branch', 'branchId': str(i) + 'x' * 129} for i in range(1000)], ('opacity', .5)))]
        for name, command in cases:
            self.rejected('budget-' + name, path, command, 413, 'resourceTooLarge', field='command')
        self.rejected('target-1001', path, patch([{'kind': 'branch', 'branchId': 'x' + str(i)} for i in range(1001)], ('opacity', .5)), 400, 'invalidRequest')
        self.history('budget-redo-survives', path, 'redo')

    def old_large_values(self):
        def medium(project, lyrics):
            self.standard_fixture(project, lyrics)
            value = gradient(257)
            self.oracle.locals[key(OTHER)]['unsungFill.material'] = value
            lyrics['branches'][1]['styleOverride']['unsungFill'] = persistence_material(value)
        path = self.reset_fixture('style-old-257-stops', medium)
        self.state('old-257-stops-readable', path)
        self.exercise('old-gradient-patch-other-leaf', path, patch(OTHER, ('opacity', .875)))
        self.exercise('old-gradient-restore', path, restore(OTHER, 'unsungFill.material'))
        self.history('old-gradient-restore-undo', path, 'undo')
        self.roundtrip('style-old-257-stops', path)
        for name, member, value in [('bytes', 'typography.fontFamilyName', 'z' * 600000),
                                    ('items', 'unsungFill.material', gradient(20001))]:
            target = {'kind': 'branch', 'branchId': 'old-' + name}
            def large(project, lyrics, target=target, member=member, value=value):
                self.standard_fixture(project, lyrics)
                overlay = {member: value}
                lyrics['branches'].append({'id': target['branchId'], 'name': 'Old owned oversized style', 'role': 'Custom',
                    'timingMode': 'None', 'lines': [], 'styleOverride': persistence_overlay(overlay)})
                self.oracle.locals[key(target)] = overlay
                self.targets.append(target); self.unreadable.add(key(target))
            path = self.reset_fixture('style-old-read-' + name, large)
            before = self.state('old-read-' + name + '-before', path)
            self.accepted('old-read-noop-preserves-' + name, path, restore(target, 'opacity'), changed=False)
            self.rejected('changed-unreadable-target-' + name, path, patch(target, ('opacity', .5)), 413, 'resourceTooLarge', field='command')
            self.same_state(before, self.state('old-read-' + name + '-stable', path), 'old full GET budget rejection has no state effects')
            self.exercise('old-read-repair-' + name, path, restore(target, member))
            self.roundtrip('style-old-read-' + name, path)

    def bulk(self):
        def make_fixture(identifiers):
            def fixture(project, lyrics):
                self.standard_fixture(project, lyrics)
                for identifier in identifiers:
                    lyrics['branches'].append({'id': identifier, 'name': 'same text source', 'role': 'Custom', 'timingMode': 'Independent',
                        'lines': [{'id': 'bulk-line-' + identifier, 'text': '甲', 'startUs': 0, 'endUs': 1000000, 'timingState': 'Confirmed',
                            'tokens': [{'id': 'bulk-token-' + identifier, 'text': '甲', 'startUs': 0, 'endUs': 1000000, 'timingState': 'Confirmed'}]}]})
                    self.targets.append({'kind': 'branch', 'branchId': identifier})
            return fixture
        identifiers = ['b' + str(i) for i in range(1000)]
        path = self.reset_fixture('style-expanded-budget', make_fixture(identifiers))
        targets = [{'kind': 'branch', 'branchId': identifier} for identifier in identifiers]
        command = patch(targets[:600], ('unsungFill.material', gradient(192)), ('sungFill.material', gradient(192)))
        canonical = json.dumps(command, ensure_ascii=True, separators=(',', ':')).encode()
        expanded = len(json.dumps(command['changes'], ensure_ascii=True, separators=(',', ':')).encode()) * len(command['targets'])
        self.check(len(canonical) < 60 * 1024 and expanded > 18 * 1024 * 1024, 'fixture isolates new-value expansion from 64KiB intent budget with ample margins')
        self.rejected('new-value-expanded-16mib', path, command, 413, 'resourceTooLarge', field='command')
        self.exercise('bulk-1000-valid-small-patch', path, patch(targets, ('opacity', .75)), refresh=False)
        self.roundtrip('style-expanded-budget', path)
        identifiers = ['源界界界界界' + str(i) for i in range(400)]
        path = self.reset_fixture('style-long-change-set', make_fixture(identifiers))
        self.exercise('long-unicode-change-set-full-refresh', path, patch([{'kind': 'branch', 'branchId': identifier} for identifier in identifiers], ('opacity', .625)), refresh=True)
        self.roundtrip('style-long-change-set', path)

    def owned_package(self, directory):
        """Copy a tracked complete sample; only human-readable labels change.

        The small ASCII manifest has an unambiguous Utf8JsonWriter canonical
        form. Revision follows EffectPackageContents length-prefixed hashing.
        No controller or package shader is compiled/executed by this fixture.
        """
        source = ROOT / 'samples/ScriptEffectsV2/GlassInk'
        manifest = json.loads((source / 'manifest.json').read_text())
        def labels(value):
            if isinstance(value, dict):
                return {k: ('Probe ' + hashlib.sha256(str(v).encode()).hexdigest()[:12] if k in ('name', 'label') else labels(v)) for k, v in value.items()}
            if isinstance(value, list):return [labels(v) for v in value]
            return value
        manifest = labels(manifest)
        data = json.dumps(manifest, ensure_ascii=True, sort_keys=True, separators=(',', ':')).encode()
        self.check(all(byte < 128 for byte in data) and not any(token in data for token in (b'\\u', b'<', b'>', b'&', b"'", b'+')),
            'owned sample canonical manifest has only safe ASCII and exact stable number spellings')
        files = {}
        for entry in manifest['files']:
            path = source / entry['path']; content_bytes = path.read_bytes()
            self.check(hashlib.sha256(content_bytes).hexdigest() == entry['sha256'], 'tracked sample resource hash ' + entry['path'])
            files[entry['path']] = content_bytes
            self.inputs.append({'sourceFile': str(path.relative_to(ROOT)), 'sha256': entry['sha256'], 'bytes': len(content_bytes)})
        digest = hashlib.sha256()
        def add(value):digest.update(struct.pack('<q', len(value))); digest.update(value)
        add(data)
        for name, content_bytes in sorted(files.items()):
            add(name.encode()); add(struct.pack('<q', len(content_bytes))); add(hashlib.sha256(content_bytes).digest())
        revision = 'sha256:' + digest.hexdigest()
        self.check(revision == 'sha256:b6a192a71f2b04890a68195354aba34967caae6abbb32fa279bb998b603f5fab',
            'owned sample closure revision matches independent published-Core validation')
        package = directory / 'effects/packages' / revision[7:]; package.mkdir(parents=True)
        (package / 'manifest.json').write_bytes(data)
        for name, content_bytes in files.items():
            path = package / name; path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(content_bytes)
        self.inputs.append({'file': str((package / 'manifest.json').relative_to(self.output)), 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)})
        return {'packageId': manifest['packageId'], 'revision': revision, 'exportId': 'glass-backdrop'}, package

    def advanced_preservation(self):
        name = 'style-advanced-owned'
        directory = self.output / 'profile/projects' / name
        identity = {}; package_files = {}
        def fixture(project, lyrics):
            self.standard_fixture(project, lyrics)
            info, package = self.owned_package(directory); identity.update(info)
            project['effects']['packages'].append({'packageId': info['packageId'], 'revision': info['revision'], 'path': 'effects/packages/' + info['revision'][7:]})
            material = {'kind': 'packageMaterial', **info, 'parameters': {'highlight': '__PRECISE_NUMBER__', 'opacity': .75}}
            project['lyricStyle']['unsungFill'] = material
            self.oracle.baseline['unsungFill']['material'] = {'kind': 'package', **info}
            # A legal fixed static preset changes a contextual opacity; the new
            # API must still report only baseline->branch->line->token hierarchy.
            snapshot_style = baseline(); snapshot_style['opacity'] = .25
            project['presetApplications']['items'].append({'id': 'probe-fixed-context', 'enabled': True, 'target': {'kind': 'project'},
                'snapshot': {'id': 'probe-template', 'name': 'Owned contextual static appearance', 'kind': 'Composite',
                    'properties': ['opacity'], 'style': persistence_baseline(snapshot_style), 'effectRecipes': []}, 'branchInputs': {}, 'identities': []})
            self.has_presets = True
            package_files.update({str(p.relative_to(directory)): hashlib.sha256(p.read_bytes()).hexdigest() for p in package.rglob('*') if p.is_file()})
        relative = self.fixture(name, fixture)
        project_path = directory / 'project.json'
        raw = project_path.read_text(); self.check(raw.count('"__PRECISE_NUMBER__"') == 1, 'one owned exact-precision parameter placeholder')
        project_path.write_text(raw.replace('"__PRECISE_NUMBER__"', '0.12345678901234567'))
        self.fixtures[-1]['projectSha256'] = hashlib.sha256(project_path.read_bytes()).hexdigest(); save_json(self.output / 'fixtures.json', self.fixtures)
        original = json.loads(project_path.read_text())
        self.undo_models.clear(); self.redo_models.clear()
        path, opened = self.open(name + '-open', relative)
        state = self.state('advanced-canonical-readonly-package', path)
        self.check(state['styles'][0]['baseline']['unsungFill']['material'] == {'kind': 'package', **identity} and
            state['styles'][0]['hasPresetApplications'] and state['styles'][0]['staticHierarchy']['opacity'] == 1,
            'package read has only true identity and static hierarchy explicitly excludes contextual preset opacity')
        self.exercise('advanced-project-font-only', path, patch(PROJECT, ('typography.fontSizePx', 49.5)))
        self.exercise('advanced-local-width-only', path, patch(LINE, ('stroke.widthPx', 8.5)))
        self.exercise('advanced-explicit-slot-replacement', path, patch(PROJECT, ('unsungFill.material', solid(rgba(9, 8, 7)))))
        self.history('advanced-restore-original-package', path, 'undo')
        saved = self.roundtrip('style-advanced-owned', path)
        current = json.loads((saved / 'project.json').read_text())
        self.check(current['effects'] == original['effects'] and current['presetApplications'] == original['presetApplications'] and
            current['lyricPresentation'] == original['lyricPresentation'] and current.get('audioAnalysis') == original.get('audioAnalysis'),
            'all unselected advanced source, fixed preset, presentation and analysis fields preserved')
        self.check(current['lyricStyle']['unsungFill'] == original['lyricStyle']['unsungFill'] and
            '0.12345678901234567' in (saved / 'project.json').read_text(), 'unselected package parameters preserve exact original decimal spelling')
        for file, expected in package_files.items():
            self.check(hashlib.sha256((saved / file).read_bytes()).hexdigest() == expected, 'complete package closure unchanged: ' + file)
        save_json(self.output / 'advanced-preservation.json', {'identity': identity, 'packageFiles': package_files,
            'fixedPresetPreserved': True, 'parameterLiteral': '0.12345678901234567',
            'scope': 'owned valid persisted package/preset source closure retained; no package upload/edit/execute or actual-context visual claim'})

    def upload_owned(self, name, data, mime):
        directory = self.output / 'inputs'; directory.mkdir(exist_ok=True)
        path = directory / name; path.write_bytes(data)
        self.inputs.append({'file': str(path.relative_to(self.output)), 'sha256': hashlib.sha256(data).hexdigest(), 'bytes': len(data)})
        return self.request('upload-' + name, 'POST', '/api/v1/assets?' + urlencode({'fileName': name}), raw=data,
            content_type=mime, status=201, headers={'X-Mako-Request-Id': 'style-upload-' + name,
                'X-Mako-Valid-Until-Utc': self.metadata('upload')['validUntilUtc']})['assetId']

    def finish(self):
        self.check(bool(self.worker_identities), 'actual managed Worker executable identities observed')
        self.stop('style')
        for item in self.inputs:
            path = ROOT / item['sourceFile'] if 'sourceFile' in item else self.output / item['file']
            data = path.read_bytes()
            self.check(len(data) == item['bytes'] and hashlib.sha256(data).hexdigest() == item['sha256'],
                'original owned input unchanged: ' + item.get('file', item.get('sourceFile')))
        save_json(self.output / 'input-manifest.json', self.inputs)
        self.check(len(self.samples) == self.request_count, 'every actual JSON/binary HTTP call has one local short elapsed sample')
        save_json(self.output / 'http-elapsed.json', {'samples': self.samples,
            'scope': 'local short samples including client validation/log overhead; no performance/throughput/allocation guarantee'})
        files = {}
        for file in sorted(self.output.rglob('*')):
            if file.is_file():
                data = file.read_bytes(); name = str(file.relative_to(self.output))
                self.check(not any(secret.encode() in data for secret in self.secrets), 'credential scan: ' + name)
                files[name] = hashlib.sha256(data).hexdigest()
        manifest = self.output / 'evidence-manifest.json'
        save_json(manifest, {'files': files, 'deploymentManifestSha256': hashlib.sha256((self.dist / 'deployment.json').read_bytes()).hexdigest()})
        self.check(not any(secret.encode() in manifest.read_bytes() for secret in self.secrets), 'credential scan: evidence-manifest.json')
        label = 'credential scan: summary.json'; candidate = self.output / '.summary.candidate'
        save_json(candidate, {'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions) + 1,
            'assertionLabels': self.assertions + [label], 'credentialScannedFiles': len(files) + 2, 'runs': self.runs,
            'fixtures': self.fixtures, 'inputManifest': self.inputs, 'previewFrames': self.preview_frames,
            'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(self.worker_identities)],
            'scope': 'two static style commands, exact raw presence and four-level hierarchy; own valid persisted advanced source preservation; '
                'explicit PUT preview input/actual PNG pixel change and undo restoration, no independent Runtime or preset/node/clip final-context oracle; '
                'no public package edit/execute, browser/performance/platform/heap-cap claim'})
        self.check(not any(secret.encode() in candidate.read_bytes() for secret in self.secrets), label)
        candidate.rename(self.output / 'summary.json')
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))

    def run(self):
        self.start('style'); self.connect('style')
        caps = self.request('style-capabilities', 'GET', '/api/v1/capabilities')
        for command in TYPES:
            self.check('project-sessions.commands.' + command in caps['features'], 'actual advertised ' + command)
        self.check('project-sessions.style.read' in caps['features'], 'actual advertised exact style read')
        audio = io.BytesIO()
        with wave.open(audio, 'wb') as stream:
            stream.setnchannels(1); stream.setsampwidth(2); stream.setframerate(8000); stream.writeframes(b'\x00\x00' * 16000)
        audio_id = self.upload_owned('style.wav', audio.getvalue(), 'audio/wav')
        lyrics_id = self.upload_owned('style.lrc', '[00:00.00]甲乙\n'.encode(), 'text/plain')
        created = self.request('create-template', 'POST', '/api/v1/project-sessions', status=201,
            value={'name': 'Owned static style HTTP template', 'audioAssetId': audio_id, 'lyricsAssetId': lyrics_id})
        path = '/api/v1/project-sessions/' + created['projectSessionId']
        path, opened = self.save_close_open('style-template', path, created['projectRevision'], 'editing-original')
        self.close('style-template-close', path, opened['projectRevision'])
        path = self.reset_fixture('style-normal')
        waveform = self.request('style-waveform-before', 'GET', path + '/waveform')
        self.core_sequence(path)
        self.stable_ids(path)
        self.malformed(path)
        self.domain(path)
        self.receipts_and_races(path)
        self.auth(path)
        self.budgets(path)
        self.check(self.request('style-waveform-after', 'GET', path + '/waveform') == waveform, 'all style edits retain original prepared waveform identity')
        saved = self.roundtrip('style-normal', path)
        project = json.loads((saved / 'project.json').read_text())
        self.check('lyricStylePresetId' not in project, 'a real project baseline change clears the old preset-origin label')
        self.old_large_values()
        self.bulk()
        self.advanced_preservation()
        self.preview_handoff()
        self.finish()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args(); output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'artifacts'):
        parser.error('--output must be a new directory beneath repository artifacts/')
    output.mkdir(parents=True, exist_ok=False)
    probe = StyleProbe(output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
