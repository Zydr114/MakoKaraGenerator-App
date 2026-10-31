"""Finite P3 increment; all transport, Worker and evidence lifecycle belongs to CompletionProbe."""
import copy
import hashlib
import json
import zipfile
from urllib.parse import urlencode


TEMPLATES = '/api/v1/template-libraries'


def run(p):
    archive = p.output / 'p3-original.mkfx'
    p.oracle_run('p3-fixture', ['p3-package', archive])
    fixture = json.loads(archive.with_name(archive.name + '.json').read_text())
    asset = p.upload('p3-package-upload', archive.name, 'application/octet-stream', archive.read_bytes())
    inspected = p.request('p3-package-inspect', 'POST', '/api/v1/effect-packages/inspect', value={
        'packageAssetId': asset['assetId'], 'expectedPackageRevision': fixture['packageRevision']})
    p.check(inspected['structureValid'] and not inspected['diagnostics']
        and inspected['packageId'] == fixture['packageId'] and inspected['packageRevision'] == fixture['packageRevision'],
        'p3 inspect fixes complete owned package identity')
    with zipfile.ZipFile(archive) as owned:
        p.check(all(len(owned.read(item['path'])) == item['size']
            and hashlib.sha256(owned.read(item['path'])).hexdigest() == item['sha256'] for item in inspected['files']),
            'p3 inspect complete file sizes SHA match the original fixture archive')
    exports = {item['id']: item for item in inspected['exports']}
    p.check({'controller', 'ink', 'tint'} <= exports.keys()
        and {'node', 'clips'} <= set(exports['controller']['bindingKinds'])
        and inspected['parameters'][0]['id'] == 'amount' and inspected['files'] and inspected['inputs'],
        'p3 inspect exposes controller material graph parameters inputs and file identities without running')
    builtin = p.request('p3-builtin', 'GET', TEMPLATES + '/builtin/templates')
    base = next(item for item in builtin['templates'] if item['kind'] == 'line')
    detail = p.request('p3-base-style', 'GET', TEMPLATES + '/builtin/templates/detail?' + urlencode({
        'expectedRevision': builtin['revision'], 'templateId': base['templateId']}))
    definition = copy.deepcopy(detail['definition'])
    definition.update(name='Original P3 advanced workshop', kind='line', properties=['unsungColor', 'opacity'],
        packages=[inspected['reference']])
    definition['style']['unsungFill']['material'] = dict(kind='package', packageId=fixture['packageId'],
        revision=fixture['packageRevision'], exportId='ink', parametersJson='{"amount":0.75}')
    definition['style']['opacity'] = 0.85
    definition['style']['typography']['fontSizePx'] = 36
    def recipe(key, amount, enabled=True):
        return dict(recipeId=key, name='Original ' + key, target='application', packageId=fixture['packageId'],
            revision=fixture['packageRevision'], exportId='controller', parametersJson=json.dumps({'amount': amount}, separators=(',', ':')),
            overlayLayer='package', enabled=enabled, readScope='branchInput', branchInput='vocals',
            fixedSeed=str(2 ** 64 - 1), configurationId='p3-configuration-' + key)
    definition['effectRecipes'] = [recipe('first', 0.8), recipe('second', 0.6, False)]
    personal = p.request('p3-personal', 'GET', TEMPLATES + '/personal/templates')
    def command(name, revision, command):
        return p.request(name, 'POST', TEMPLATES + '/personal/commands', value={
            'expectedRevision': revision, 'metadata': p.metadata(name), 'command': command})
    created = command('p3-full-create', personal['revision'], dict(type='create', definitionFormat='full', definition=definition))
    template_id, library_revision = created['templateId'], created['revision']
    def full(name, template=template_id, revision=None):
        return p.request(name, 'GET', TEMPLATES + '/personal/templates/detail?' + urlencode({
            'expectedRevision': revision or library_revision, 'templateId': template, 'definitionFormat': 'full'}))
    first = full('p3-full-read')
    p.check(first['definitionFormat'] == 'full' and first['definition']['effectRecipes'] == definition['effectRecipes']
        and first['definition']['style'] == definition['style'] and first['packageClosureDetails'][0]['packageRevision'] == fixture['packageRevision'],
        'p3 full create read preserves two recipes seed full unselected style material and package closure')
    # Full responses use stable library references. Preview/edit this captured complete value.
    revised = copy.deepcopy(first['definition'])
    revised['effectRecipes'].reverse()
    revised['effectRecipes'][0]['enabled'] = True
    revised['effectRecipes'][0]['parametersJson'] = '{"amount":0.4}'
    revised['style']['typography']['letterSpacingPx'] = 1.25  # not selected: must survive roundtrip
    draft = dict(kind='draft', draftRevision='p3-workshop-draft-1', definitionFormat='full', definition=revised)
    options = p.request('p3-draft-options', 'POST', '/api/v1/template-preview-options', value={'source': draft})
    p.check({False, True} <= {sample['multiLine'] for sample in options['samples']}
        and any(slot['slot'] == 'unsungFill' or slot['selected'] for slot in options['materialSlots']),
        'p3 original workshop options contain single multi samples and material slots')
    for multiline in (False, True):
        sample = next(item for item in options['samples'] if item['multiLine'] == multiline and item['targets'])
        target = next((item for item in sample['targets'] if item['binding']['kind'] == 'node'), sample['targets'][0])
        preview = p.request('p3-workshop-' + str(multiline), 'POST', '/api/v1/template-preview-sessions', status=201,
            value=dict(source=draft, sampleId=sample['sampleId'], size=dict(width=320, height=180), targetKey=target['key'], materialSlot='unsungFill'))
        try:
            p.frame('p3-workshop-frame-' + str(multiline), preview, '1500000')
        finally:
            p.close_preview('p3-workshop-' + str(multiline), preview)
    p.request('p3-stale-replace', 'POST', TEMPLATES + '/personal/commands', status=409, code='revisionConflict', value={
        'expectedRevision': '0' * 64, 'metadata': p.metadata('p3-stale'),
        'command': dict(type='replace', templateId=template_id, definitionFormat='full', definition=revised)})
    replaced = command('p3-full-replace', library_revision,
        dict(type='replace', templateId=template_id, definitionFormat='full', definition=revised))
    library_revision = replaced['revision']
    second = full('p3-full-reloaded')
    p.check(second['definition']['effectRecipes'] == revised['effectRecipes']
        and second['definition']['style'] == revised['style'], 'p3 replace preserves recipe order parameters and unselected style')
    # Two short original lines, no audio or long export; same public import path as P1.
    lyrics_asset = p.upload('p3-lyrics', 'p3-original.lrc', 'text/plain', b'[00:00.00]Original workshop one\n[00:02.00]Original workshop two\n')
    p.send('p3-import', dict(type='lyrics.importAsset', assetId=lyrics_asset['assetId'],
        destination=dict(kind='existing', branchId='primary'), options=dict(importerId='lrc.plain', encodingName='utf-8')))
    lines = p.lyrics('p3-lines')['lines']
    p.check(len(lines) == 2, 'p3 two authored lines imported')
    for index, line in enumerate(lines):
        p.send('p3-line-time-' + str(index), dict(type='lyrics.setTimeRange', target=dict(branchId='primary', lineId=line['id']),
            startUs=str(index * 2000000), endUs=str((index + 1) * 2000000)))
    program = next(item for item in p.orchestration('p3-tree')['programs'] if item['branchId'] == 'primary')
    p.send('p3-clip', dict(type='orchestration.createClip', branchId='primary', clipId='clip/p3', name='P3 clip',
        lineIds=[lines[0]['id']], containerId=program['defaultContainerId']))
    p.send('p3-duration', dict(type='composition.setOutputDuration', durationUs='4000000'))
    inputs = {'vocals': 'primary'}
    def applications(name):
        return p.request(name, 'GET', p.project_path + '/preset-applications?expectedRevision=' + p.revision)['applications']
    for kind, target in [('clips', dict(kind='clips', branchId='primary', clipIds=['clip/p3'])),
                         ('node', dict(kind='node', nodeId=program['rootContainerId'], includeDescendants=True))]:
        before_revision = p.revision
        candidate = p.request('p3-' + kind + '-candidate', 'POST', p.project_path + '/preset-application-previews', status=201,
            value=dict(expectedProjectRevision=p.revision, templateLibraryId='personal', templateLibraryRevision=library_revision,
                templateId=template_id, templateRevision=second['summary']['templateRevision'], targets=[target], branchInputs=inputs,
                size=dict(width=320, height=180)))
        try:
            pixels = p.frame('p3-' + kind + '-candidate-frame', candidate)
        finally:
            p.close_preview('p3-' + kind + '-candidate', candidate)
        p.check(p.request('p3-' + kind + '-unchanged', 'GET', p.project_path)['projectRevision'] == before_revision,
            'p3 ' + kind + ' candidate does not write project')
        p.send('p3-' + kind + '-apply', dict(type='presetApplications.apply', templateLibraryId='personal', templateId=template_id,
            templateRevision=library_revision, targets=[target], branchInputs=inputs))
        item = next(item for item in applications('p3-' + kind + '-read') if item['target']['kind'] == kind)
        p.check(item['targetValid'] and item['branchInputs'] == inputs and item['snapshot']['hasPackages']
            and item['snapshot']['hasEffectRecipes'], 'p3 ' + kind + ' read exposes complete target availability and fixed advanced snapshot')
        p.check(item['target'].get('clipIds') == target['clipIds'] and item['target'].get('branchId') == 'primary' if kind == 'clips'
            else item['target'].get('nodeId') == target['nodeId'] and item['target']['includeDescendants'], 'p3 ' + kind + ' exact binding readback')
        p.check(pixels == p.project_preview('p3-' + kind + '-applied-frame'), 'p3 ' + kind + ' candidate equals committed complete RGBA')
    before_replace = applications('p3-before-library-update')
    newer = copy.deepcopy(second['definition'])
    newer['name'] = 'Original P3 explicitly updated'
    newer['style']['opacity'] = 0.65
    latest = command('p3-library-update', library_revision,
        dict(type='replace', templateId=template_id, definitionFormat='full', definition=newer))
    library_revision = latest['revision']
    p.check(applications('p3-fixed-snapshots') == before_replace, 'p3 library replace never rewrites project fixed applications')
    for item in before_replace:
        p.send('p3-explicit-update-' + item['target']['kind'], dict(type='presetApplications.update', applicationId=item['applicationId'],
            templateLibraryId='personal', templateId=template_id, templateRevision=library_revision))
    after_update = applications('p3-updated-applications')
    p.check([item['applicationId'] for item in after_update] == [item['applicationId'] for item in before_replace]
        and all(item['snapshot']['name'] == newer['name'] and item['branchInputs'] == inputs for item in after_update),
        'p3 explicit update preserves application identities and branch inputs')
    expected = full('p3-export-definition')['definition']
    exported = p.request('p3-export', 'POST', TEMPLATES + '/personal/exports', status=201,
        value={'expectedRevision': library_revision, 'templateIds': [template_id]})
    _, encoded, _ = p.fetch('p3-export-content', exported['contentUrl'])
    p.check(len(encoded) == exported['sizeBytes'] and hashlib.sha256(encoded).hexdigest() == exported['sha256'], 'p3 full mktpl immutable size SHA')
    reasset = p.upload('p3-reimport-upload', 'p3-shared.mktpl', 'application/octet-stream', encoded)
    preflight = p.request('p3-reimport-preflight', 'POST', TEMPLATES + '/personal/imports/preflight', status=201, value={'assetId': reasset['assetId']})
    imported = p.request('p3-reimport', 'POST', TEMPLATES + '/personal/imports', value={
        'expectedRevision': library_revision, 'preflightId': preflight['preflightId']})
    actual = full('p3-reimport-full', imported['importedTemplates'][0]['templateId'], imported['revision'])['definition']
    def comparable(value):
        result = copy.deepcopy(value)
        result['packages'] = sorted((item['packageId'], item['packageRevision']) for item in result['packages'])
        return result
    p.check(comparable(expected) == comparable(actual), 'p3 mktpl roundtrip full style ordered recipes parameters seeds package identity equivalence')
    p.handle = p.request('p3-save-handle', 'POST', '/api/v1/file-handles', status=201,
        value={'rootGrantId': 'projects', 'relativePath': 'p3-original'})['handleId']
    p.save_close('p3-final')
    p.flows.append(dict(name='p3-workshop', scope='Owned package inspect; full create/read/replace; single/multi workshop Worker PNG; clips/node candidate/apply/update; fixed snapshots; mktpl full roundtrip. No P1/P2 long export replay, browser or cross-platform claim.'))
