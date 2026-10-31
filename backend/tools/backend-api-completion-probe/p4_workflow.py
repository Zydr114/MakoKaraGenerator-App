"""Finite P4 author draft and personal configuration workflow."""
import copy
import hashlib
import json
import time
import zipfile
from urllib.parse import urlencode


TEMPLATES = '/api/v1/template-libraries'


def _configuration_input(configuration, *, name=None, source=None):
    """Project the server configuration into the writable input shape."""
    value = {
        'name': name or configuration['name'],
        'packageId': configuration['packageId'],
        'sourceRevision': source or configuration['sourceRevision'],
        'exportId': configuration['exportId'],
        'parametersJson': configuration['parametersJson'],
        'relativeTarget': configuration['relativeTarget'],
        'readScope': configuration['readScope'],
        'branchInput': configuration.get('branchInput'),
        'fixedSeed': configuration.get('fixedSeed'),
        'overlayLayer': configuration['overlayLayer'],
    }
    return value


def _library_configuration_input(configuration, source, *, name=None):
    """Library writes select their fixed package only through the source union."""
    value = _configuration_input(configuration, name=name)
    value.pop('packageId')
    value.pop('sourceRevision')
    value['source'] = source
    return value


def _sample_target(draft, sample_id=None):
    options = draft['sampleOptions']
    selected = next((item for item in options['samples'] if item['sampleId'] == sample_id), None)
    selected = selected or next(item for item in options['samples'] if item['sampleId'] == options['defaultSampleId'])
    # The generated contract names the array entries ``targets`` and each entry
    # carries a stable key/binding pair.  Keep selection entirely server-driven.
    target = selected['targets'][0]
    material = options['materialSlots'][0] if options['materialSlots'] else None
    return selected, target, material


def run(p):
    # 1. Inspect an owned package and retain an independent archive byte oracle.
    archive = p.output / 'p4-author.mkfx'
    p.oracle_run('p4-package-fixture', ['p3-package', archive])
    fixture = json.loads(archive.with_name(archive.name + '.json').read_text())
    package_asset = p.upload('p4-package-upload', archive.name, 'application/octet-stream', archive.read_bytes())
    inspected = p.request('p4-package-inspect', 'POST', '/api/v1/effect-packages/inspect', value={
        'packageAssetId': package_asset['assetId'], 'expectedPackageRevision': fixture['packageRevision']})
    with zipfile.ZipFile(archive) as owned:
        entries = {info.filename: owned.read(info.filename) for info in owned.infolist() if not info.is_dir()}
    p.check(inspected['structureValid'] and not inspected['diagnostics'], 'p4 package inspect has no structural diagnostics')
    p.check(inspected['packageId'] == fixture['packageId'] and inspected['packageRevision'] == fixture['packageRevision'],
            'p4 package inspect fixes package identity and revision')
    p.check({item['path'] for item in inspected['files']} == set(entries) - {'manifest.json'}
            and all(item['path'] in entries and item['size'] == len(entries[item['path']])
                and item['sha256'] == hashlib.sha256(entries[item['path']]).hexdigest() for item in inspected['files']),
            'p4 package inspect reports complete file sizes and SHA values')
    exports = {item['id']: item for item in inspected['exports']}
    export_id = 'controller' if 'controller' in exports else next(iter(exports))
    parameter_id = inspected['parameters'][0]['id'] if inspected['parameters'] else None
    parameters = json.dumps({parameter_id: 0.75} if parameter_id else {}, separators=(',', ':'))
    p.check(export_id in exports and inspected['files'] and inspected['inputs'],
            'p4 package inspect exposes export, parameter, input and file declarations')

    # 2. Create, list, read and read the complete C# source collection.
    draft = p.request('p4-draft-create', 'POST', '/api/v1/drafts', status=201, value={
        'packageAssetId': package_asset['assetId'], 'exportId': export_id, 'name': 'Original P4 author draft'})
    draft_id = draft['draftId']
    listed = p.request('p4-draft-list', 'GET', '/api/v1/drafts')
    p.check(any(item['draftId'] == draft_id for item in listed['drafts']), 'p4 draft list contains created draft')
    detail = p.request('p4-draft-read', 'GET', '/api/v1/drafts/' + draft_id)
    p.check(detail['draftId'] == draft_id and detail['draftRevision'] == draft['draftRevision']
            and detail['packageId'] == inspected['packageId'] and detail['sampleOptions']['samples'],
            'p4 draft read returns fixed package and server sample options')
    source_query = urlencode({'expectedDraftRevision': draft['draftRevision'], 'sourceRevision': draft['currentSourceRevision']})
    sources = p.request('p4-sources-read', 'GET', '/api/v1/drafts/' + draft_id + '/sources?' + source_query)
    p.check(sources['draftId'] == draft_id and sources['sourceRevision'] == draft['currentSourceRevision']
            and {item['path']: item['text'] for item in sources['files']}
                == {item['path']: entries[item['path']].decode('utf-8-sig')
                    for item in inspected['files'] if item['role'] == 'csharp'},
            'p4 sources read returns complete C# paths and text')

    # Save a changed complete source set.  The service derives a new package
    # revision and updates a default configuration to that revision.
    changed_files = [dict(item, text=item['text'] + '\n// p4 source revision one\n') for item in sources['files']]
    changed = p.request('p4-source-revision', 'POST', '/api/v1/drafts/' + draft_id + '/source-revision-jobs', value={
        'expectedDraftRevision': draft['draftRevision'], 'files': changed_files})
    draft = p.request('p4-draft-after-source', 'GET', '/api/v1/drafts/' + draft_id)
    p.check(changed['draftRevision'] == draft['draftRevision']
            and changed['sourceRevision'] == draft['currentSourceRevision']
            and changed['sourceRevision'] != sources['sourceRevision'],
            'p4 source write advances draft and source revisions')
    source_query = urlencode({'expectedDraftRevision': draft['draftRevision'], 'sourceRevision': draft['currentSourceRevision']})
    reread = p.request('p4-sources-after-source', 'GET', '/api/v1/drafts/' + draft_id + '/sources?' + source_query)
    p.check(reread['files'] == changed_files, 'p4 source read preserves complete changed C# text')

    # 3. Save configuration, compile synchronously, then compile an immutable
    # async source while advancing the draft underneath it.
    config = _configuration_input(draft['configuration'], name='Original P4 configuration', source=draft['currentSourceRevision'])
    config.update(parametersJson=parameters, relativeTarget='application', readScope='branchInput',
                  branchInput='vocals', fixedSeed=str(2 ** 64 - 1), overlayLayer='package')
    draft = p.request('p4-configuration-save', 'PUT', '/api/v1/drafts/' + draft_id + '/configuration', value={
        'expectedDraftRevision': draft['draftRevision'], 'configuration': config,
        'metadata': p.metadata('p4-configuration-save')})
    p.check(draft['configuration']['sourceRevision'] == draft['currentSourceRevision']
            and draft['configuration']['fixedSeed'] == str(2 ** 64 - 1)
            and draft['configuration']['branchInput'] == 'vocals',
            'p4 configuration persists exact parameters seed target and branch input')
    sync = p.request('p4-compilation-sync', 'POST', '/api/v1/drafts/' + draft_id + '/compilation-jobs', value={
        'expectedDraftRevision': draft['draftRevision'], 'executionMode': 'sync'})
    p.check(sync['draftId'] == draft_id and sync['draftRevision'] == draft['draftRevision']
            and sync['sourceRevision'] == draft['currentSourceRevision'] and sync['succeeded']
            and isinstance(sync['diagnostics'], list) and sync['artifactHash'],
            'p4 synchronous compilation returns successful complete diagnostics')
    persisted = p.request('p4-compilation-read', 'GET', '/api/v1/drafts/' + draft_id + '/compilation')
    p.check(persisted == sync, 'p4 synchronous compilation is readable as the latest complete result')
    # The current implementation keeps the revision while recording diagnostics.
    # Read current authority before the next CAS instead of assuming that policy.
    draft = p.request('p4-draft-after-sync', 'GET', '/api/v1/drafts/' + draft_id)
    p.check(draft['currentSourceRevision'] == sync['sourceRevision']
            and draft['lastCompilation']['diagnosticCount'] == len(sync['diagnostics']),
            'p4 latest draft records the synchronous fixed source and diagnostic count')

    async_draft_revision = draft['draftRevision']
    async_source_revision = draft['currentSourceRevision']
    accepted = p.request('p4-compilation-async', 'POST', '/api/v1/drafts/' + draft_id + '/compilation-jobs', status=202, value={
        'expectedDraftRevision': async_draft_revision, 'executionMode': 'async'})
    p.check(accepted['kind'] == 'author-compilation' and accepted['authorCompilationInput']['sourceRevision'] == async_source_revision,
            'p4 async compilation job captures draft and source revision')
    newest_files = [dict(item, text=item['text'] + '\n// p4 source revision two\n') for item in reread['files']]
    advanced = p.request('p4-source-revision-after-async', 'POST', '/api/v1/drafts/' + draft_id + '/source-revision-jobs', value={
        'expectedDraftRevision': async_draft_revision, 'files': newest_files})
    terminal = p.long_job('p4-compilation-async', accepted)
    _, raw, _ = p.fetch('p4-compilation-result', '/api/v1/jobs/' + accepted['jobId'] + '/compilation-result')
    async_result = json.loads(raw)
    p.check(async_result['draftRevision'] == async_draft_revision and async_result['sourceRevision'] == async_source_revision,
            'p4 async compilation result remains bound to the old immutable source')
    p.check(async_result['succeeded'] and async_result['diagnostics'] == sync['diagnostics']
            and async_result['artifactHash'] == sync['artifactHash'],
            'p4 synchronous and asynchronous compilation return the same complete fixed-source result')
    p.check(advanced['sourceRevision'] != async_source_revision and terminal['state'] == 'succeeded',
            'p4 source mutation after async acceptance leaves an old successful result')
    draft = p.request('p4-draft-after-async', 'GET', '/api/v1/drafts/' + draft_id)

    # 4. Author preview uses the server-provided sample/target identities.
    sample, target, material = _sample_target(draft)
    author_source = {'kind': 'authorDraft', 'sourceId': draft_id, 'sourceRevision': draft['draftRevision'],
                     'authorSourceRevision': draft['configuration']['sourceRevision'],
                     'templateSample': {'sampleId': sample['sampleId'], 'targetKey': target['key'], 'materialSlot': material}}
    preview = p.request('p4-author-preview-create', 'POST', '/api/v1/preview-sessions', status=201, value={
        'source': author_source, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque',
        'purpose': 'preview', 'previewKind': 'authorDraft', 'committable': False})
    try:
        p.frame('p4-author-preview-frame', preview)
        alternatives = [item for item in draft['sampleOptions']['samples'] if item['sampleId'] != sample['sampleId'] and item['targets']]
        if alternatives:
            alternate = alternatives[0]
            alternate_source = copy.deepcopy(author_source)
            alternate_source['templateSample'] = {'sampleId': alternate['sampleId'], 'targetKey': alternate['targets'][0]['key'],
                                                   'materialSlot': material}
            old_generation = preview['previewGeneration']
            updated = p.request('p4-author-preview-input', 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/input', value={
                'expectedPreviewGeneration': old_generation, 'expectedSourceRevision': author_source['sourceRevision'],
                'source': alternate_source, 'size': {'width': 320, 'height': 180}, 'alphaMode': 'opaque',
                'metadata': p.metadata('p4-author-preview-input')})
            preview = updated
            p.check(preview['previewGeneration'] != old_generation and preview['source']['templateSample']['sampleId'] == alternate['sampleId'],
                    'p4 author preview input changes generation and keeps source identity')
            p.frame('p4-author-preview-alternate-frame', preview, sequence=2)
    finally:
        p.close_preview('p4-author-preview', preview)

    # 5. Run one complete author validation, read report/diagnostics, and
    # cancel a second accepted task while it is running.
    validation = p.request('p4-author-validation', 'POST', '/api/v1/drafts/' + draft_id + '/validation-jobs', status=202, value={
        'expectedDraftRevision': draft['draftRevision'], 'sampleId': sample['sampleId'], 'targetKey': target['key'],
        'materialSlot': material})
    validation_terminal = p.long_job('p4-author-validation', validation)
    p.check(validation_terminal['authorValidationInput']['draftId'] == draft_id
            and validation_terminal['authorValidationInput']['sourceRevision'] == draft['configuration']['sourceRevision'],
            'p4 validation terminal preserves author draft and source identities')
    report_ref = validation_terminal['validationResult']['report']
    _, report_bytes, _ = p.fetch('p4-author-validation-report', '/api/v1/jobs/' + validation['jobId'] + '/validation-result')
    report = json.loads(report_bytes)
    p.check(len(report_bytes) == report_ref['sizeBytes'] and hashlib.sha256(report_bytes).hexdigest() == report_ref['sha256']
            and report['succeeded'] and report['opaqueFrames'] == report['transparentFrames'] == 360,
            'p4 validation report bytes SHA and fixed 360-frame outputs')
    diagnostics = p.request('p4-author-validation-diagnostics', 'GET', '/api/v1/jobs/' + validation['jobId'] + '/validation-diagnostics?limit=128')
    p.check(diagnostics['jobId'] == validation['jobId'] and diagnostics['reportSha256'] == report_ref['sha256'],
            'p4 validation diagnostics page binds the published report')

    cancel_validation = p.request('p4-author-validation-cancel-start', 'POST', '/api/v1/drafts/' + draft_id + '/validation-jobs', status=202, value={
        'expectedDraftRevision': draft['draftRevision'], 'sampleId': sample['sampleId'], 'targetKey': target['key'],
        'materialSlot': material})
    deadline = time.monotonic() + 60
    current = None
    while time.monotonic() < deadline:
        current = p.request('p4-author-validation-cancel-state', 'GET', '/api/v1/jobs/' + cancel_validation['jobId'])
        if current['state'] == 'running':
            break
        if current['state'] in ('succeeded', 'failed', 'cancelled', 'unknown'):
            break
        time.sleep(.05)
    p.check(current['state'] == 'running', 'p4 cancellation observes a running author validation job')
    p.cancel_job('p4-author-validation-cancel', cancel_validation['jobId'])
    cancelled = p.long_job('p4-author-validation-cancel', cancel_validation, expected='cancelled')
    p.check(cancelled.get('validationResult') is None, 'p4 cancelled validation publishes no report')
    p.request('p4-author-validation-cancel-no-report', 'GET', '/api/v1/jobs/' + cancel_validation['jobId'] + '/validation-result',
        status=404, code='jobNotFound')

    # 6. Adopt an explicitly saved source revision.
    current_draft = p.request('p4-draft-before-adopt', 'GET', '/api/v1/drafts/' + draft_id)
    adopted = p.request('p4-adopt-source', 'POST', '/api/v1/drafts/' + draft_id + '/adopt-source-revision', value={
        'expectedDraftRevision': current_draft['draftRevision'], 'sourceRevision': async_source_revision})
    draft = p.request('p4-draft-after-adopt', 'GET', '/api/v1/drafts/' + draft_id)
    p.check(adopted['adoptedSourceRevision'] == async_source_revision
            and draft['adoptedSourceRevision'] == async_source_revision
            and draft['configuration']['sourceRevision'] == async_source_revision,
            'p4 explicit adopt fixes the selected source in draft and configuration')

    # 7. Personal configuration CRUD/CAS and create a second draft from it.
    library = p.request('p4-library-before', 'GET', '/api/v1/script-configurations')
    source_ref = {'draftId': draft_id, 'expectedDraftRevision': draft['draftRevision'],
                  'sourceRevision': draft['configuration']['sourceRevision']}
    library_configuration = _library_configuration_input(draft['configuration'], source_ref,
        name='Original P4 library configuration')
    def command(name, expected, command_value, status=200, code=None):
        return p.request(name, 'POST', '/api/v1/script-configurations/commands', status=status, code=code, value={
            'expectedRevision': expected, 'metadata': p.metadata(name), 'command': command_value})
    created = command('p4-library-create', library['revision'], {'type': 'create', 'configuration': library_configuration})
    configuration_id, library_revision = created['configurationId'], created['revision']
    detail = p.request('p4-library-detail', 'GET', '/api/v1/script-configurations/' + configuration_id + '?expectedRevision=' + library_revision)
    p.check(detail['configuration']['configurationId'] == configuration_id
            and detail['configuration']['fixedSeed'] == str(2 ** 64 - 1)
            and detail['package']['reference']['packageId'] == detail['configuration']['packageId'],
            'p4 library detail preserves full configuration and package closure')
    command('p4-library-stale-replace', '0' * 64, {'type': 'replace', 'configurationId': configuration_id,
        'configuration': library_configuration}, status=409, code='revisionConflict')
    replaced_input = copy.deepcopy(library_configuration); replaced_input['name'] = 'Original P4 library replaced'
    replaced = command('p4-library-replace', library_revision, {'type': 'replace', 'configurationId': configuration_id,
        'configuration': replaced_input})
    library_revision = replaced['revision']
    copied = command('p4-library-copy', library_revision, {'type': 'copy', 'configurationId': configuration_id, 'name': 'Original P4 library copy'})
    copied_id, library_revision = copied['configurationId'], copied['revision']
    renamed = command('p4-library-rename', library_revision, {'type': 'rename', 'configurationId': copied_id, 'name': 'Original P4 library renamed'})
    library_revision = renamed['revision']
    copied_detail = p.request('p4-library-renamed-detail', 'GET', '/api/v1/script-configurations/' + copied_id + '?expectedRevision=' + library_revision)
    created_draft = p.request('p4-draft-from-configuration', 'POST', '/api/v1/drafts', status=201, value={
        'packageAssetId': None, 'exportId': copied_detail['configuration']['exportId'], 'name': 'Original P4 restored draft',
        'configurationId': copied_id, 'expectedConfigurationRevision': library_revision})
    p.check(created_draft['configuration']['sourceRevision'] == copied_detail['configuration']['sourceRevision']
            and created_draft['packageId'] == copied_detail['configuration']['packageId'],
            'p4 draft creation from personal configuration fixes source and package')
    deleted = command('p4-library-delete', library_revision, {'type': 'delete', 'configurationId': configuration_id})
    after_delete = p.request('p4-library-after-delete', 'GET', '/api/v1/script-configurations?expectedRevision=' + deleted['revision'])
    p.check(all(item['configurationId'] != configuration_id for item in after_delete['configurations'])
            and any(item['configurationId'] == copied_id for item in after_delete['configurations']),
            'p4 personal configuration delete is CAS protected and leaves copied entry')

    # A material configuration has no controller subscription, seed or bindings.
    p.check('ink' in exports and exports['ink']['kind'] == 'material', 'p4 fixture declares the ink material export')
    material_input = copy.deepcopy(library_configuration)
    material_input.update(name='Original P4 ink material', exportId='ink', parametersJson='{"amount":0.4}',
        relativeTarget='application', readScope='boundSources', branchInput=None, fixedSeed=None, overlayLayer='package')
    material_created = command('p4-material-create', deleted['revision'], {'type': 'create', 'configuration': material_input})
    library_revision = material_created['revision']
    material_detail = p.request('p4-material-detail', 'GET', '/api/v1/script-configurations/'
        + material_created['configurationId'] + '?expectedRevision=' + library_revision)
    material_config = material_detail['configuration']
    p.check(material_config['kind'] == 'Material' and material_config['exportId'] == 'ink'
            and material_config['parametersJson'] == material_input['parametersJson']
            and not material_config['bindingKinds'] and material_config.get('fixedSeed') is None
            and material_config['sourceRevision'] == source_ref['sourceRevision'],
            'p4 ink create and complete detail preserve material parameters and fixed package without controller bindings')
    material_draft = p.request('p4-material-draft-create', 'POST', '/api/v1/drafts', status=201, value={
        'packageAssetId': None, 'exportId': 'ink', 'name': 'Original P4 material draft',
        'configurationId': material_created['configurationId'], 'expectedConfigurationRevision': library_revision})
    material_draft_read = p.request('p4-material-draft-read', 'GET', '/api/v1/drafts/' + material_draft['draftId'])
    p.check(material_draft_read == material_draft and material_draft_read['configuration']['kind'] == 'Material'
            and material_draft_read['configuration']['sourceRevision'] == material_config['sourceRevision']
            and set(material_draft_read['sampleOptions']['materialSlots']) == {'unsungFill', 'sungFill', 'stroke'}
            and {False, True} == {item['multiLine'] for item in material_draft_read['sampleOptions']['samples']}
            and all(item['targets'] for item in material_draft_read['sampleOptions']['samples']),
            'p4 material draft create read exposes all three material slots and nonempty single multi sample targets')
    # Every library mutation invalidates previous configuration package references.
    copied_detail = p.request('p4-library-current-detail', 'GET', '/api/v1/script-configurations/'
        + copied_id + '?expectedRevision=' + library_revision)

    # 8. Reuse P3's full template command with a configuration-backed package
    # reference and verify closure/configuration identity in the full response.
    builtin = p.request('p4-template-builtin', 'GET', TEMPLATES + '/builtin/templates')
    base = next(item for item in builtin['templates'] if item['kind'] == 'line')
    base_detail = p.request('p4-template-builtin-detail', 'GET', TEMPLATES + '/builtin/templates/detail?' + urlencode({
        'expectedRevision': builtin['revision'], 'templateId': base['templateId']}))
    definition = copy.deepcopy(base_detail['definition'])
    definition.update(name='Original P4 configuration closure', kind='line', properties=['opacity', 'unsungColor'])
    package = copied_detail['package']['reference']
    definition['packages'] = [package]
    definition['style']['unsungFill']['material'] = {
        'kind': 'package', 'packageId': material_config['packageId'], 'revision': material_config['sourceRevision'],
        'exportId': material_config['exportId'], 'parametersJson': material_config['parametersJson']}
    definition['effectRecipes'] = [{
        'recipeId': 'p4-configuration-recipe', 'name': 'Original P4 configuration recipe', 'target': copied_detail['configuration']['relativeTarget'],
        'packageId': copied_detail['configuration']['packageId'], 'revision': copied_detail['configuration']['sourceRevision'],
        'exportId': copied_detail['configuration']['exportId'], 'parametersJson': copied_detail['configuration']['parametersJson'],
        'overlayLayer': copied_detail['configuration']['overlayLayer'], 'enabled': True,
        'readScope': copied_detail['configuration']['readScope'], 'branchInput': copied_detail['configuration'].get('branchInput'),
        'fixedSeed': copied_detail['configuration'].get('fixedSeed'), 'configurationId': copied_id}]
    personal = p.request('p4-template-personal-before', 'GET', TEMPLATES + '/personal/templates')
    created_template = p.request('p4-template-full-create', 'POST', TEMPLATES + '/personal/commands', value={
        'expectedRevision': personal['revision'], 'metadata': p.metadata('p4-template-full-create'),
        'command': {'type': 'create', 'definitionFormat': 'full', 'definition': definition}})
    template_id, template_revision = created_template['templateId'], created_template['revision']
    full = p.request('p4-template-full-read', 'GET', TEMPLATES + '/personal/templates/detail?' + urlencode({
        'expectedRevision': template_revision, 'templateId': template_id, 'definitionFormat': 'full'}))
    closure = full['packageClosureDetails'][0]
    p.check(full['definitionFormat'] == 'full' and full['definition']['effectRecipes'] == definition['effectRecipes']
            and full['definition']['style'] == definition['style']
            and closure['packageId'] == package['packageId'] and closure['packageRevision'] == package['packageRevision']
            and closure['files'] == copied_detail['package']['files']
            and closure['reference']['kind'] == 'library' and closure['reference']['templateId'] == template_id
            and closure['reference']['libraryRevision'] == template_revision,
            'p4 full template retains copied recipe identity and material bytes in its own normalized library closure')

    # Render the saved template, then apply the same fixed template to one real
    # short project line and render through the existing project preview path.
    sample_source = {'kind': 'savedTemplate', 'libraryId': 'personal', 'libraryRevision': template_revision,
        'templateId': template_id, 'templateRevision': full['summary']['templateRevision']}
    options = p.request('p4-saved-template-options', 'POST', '/api/v1/template-preview-options', value={'source': sample_source})
    sample_option = next(item for item in options['samples'] if not item['multiLine'] and item['targets'])
    saved_preview = p.request('p4-saved-template-preview', 'POST', '/api/v1/template-preview-sessions', status=201, value={
        'source': sample_source, 'sampleId': sample_option['sampleId'], 'targetKey': sample_option['targets'][0]['key'],
        'materialSlot': 'unsungFill', 'size': {'width': 320, 'height': 180}})
    try:
        p.frame('p4-saved-template-frame', saved_preview)
    finally:
        p.close_preview('p4-saved-template', saved_preview)
    lyrics_asset = p.upload('p4-project-lyrics', 'p4-authored.lrc', 'text/plain', b'[00:00.00]Original P4 authored line\n')
    p.send('p4-project-import', {'type': 'lyrics.importAsset', 'assetId': lyrics_asset['assetId'],
        'destination': {'kind': 'existing', 'branchId': 'primary'}, 'options': {'importerId': 'lrc.plain', 'encodingName': 'utf-8'}})
    line = p.lyrics('p4-project-lines')['lines'][0]
    p.send('p4-project-line-time', {'type': 'lyrics.setTimeRange', 'target': {'branchId': 'primary', 'lineId': line['id']},
        'startUs': '0', 'endUs': '2000000'})
    p.send('p4-project-duration', {'type': 'composition.setOutputDuration', 'durationUs': '2000000'})
    p.send('p4-project-apply-template', {'type': 'presetApplications.apply', 'templateLibraryId': 'personal',
        'templateId': template_id, 'templateRevision': template_revision,
        'targets': [{'kind': 'line', 'branchId': 'primary', 'lineId': line['id']}], 'branchInputs': {'vocals': 'primary'}})
    p.project_preview('p4-applied-template')

    # Keep the project session lifecycle covered by the inherited harness.
    p.handle = p.request('p4-final-handle', 'POST', '/api/v1/file-handles', status=201,
        value={'rootGrantId': 'projects', 'relativePath': 'p4-author'})['handleId']
    p.save_close('p4-final')
    p.flows.append({'name': 'p4-author-configuration',
        'scope': 'Owned package inspect; author draft list/source/configuration; sync and immutable async compilation; authorDraft preview/update; 360-frame validation/report/diagnostics and cancellation; explicit source adoption; personal configuration CAS CRUD, ink material and draft creation; full template closure, saved-template and applied-project Worker frames. No browser, restart recovery, arbitrary script sandbox, cross-platform or performance claim.'})
