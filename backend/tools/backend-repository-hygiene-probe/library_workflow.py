"""Finite public saves and same-profile reloads for the two independent libraries."""
import copy
import hashlib
import json
from pathlib import PurePosixPath
from urllib.parse import urlencode
import zipfile

SAMPLE = 'samples/ScriptEffects/WordEmphasis'
CONFIGURATIONS = '/api/v1/script-configurations'
TEMPLATES = '/api/v1/template-libraries'


def sample_inputs(root):
    manifest = json.loads((root / SAMPLE / 'manifest.json').read_bytes())
    declared = [item['path'] for item in manifest['files']]
    actual = sorted(str(path.relative_to(root / SAMPLE)) for path in (root / SAMPLE).rglob('*') if path.is_file() and path.name != 'manifest.json')
    if sorted(declared) != actual:
        raise ValueError('Sample manifest does not cover exactly every tracked sample file.')
    paths = [SAMPLE + '/manifest.json']
    for item in manifest['files']:
        logical = PurePosixPath(item['path'])
        if logical.is_absolute() or '..' in logical.parts or str(logical) != item['path']:
            raise ValueError('Sample manifest path is not a canonical relative file.')
        paths.append(SAMPLE + '/' + item['path'])
    if len(set(paths)) != len(paths):
        raise ValueError('Sample manifest declares duplicate file paths.')
    return paths


def run(p, root, host, save):
    files = {name[len(SAMPLE) + 1:]: (root / name).read_bytes() for name in sample_inputs(root)}
    manifest = json.loads(files['manifest.json'])
    for item in manifest['files']:
        p.check(hashlib.sha256(files[item['path']]).hexdigest() == item['sha256'],
                'libraries: tracked sample file matches its original manifest SHA256: ' + item['path'])
    archive = p.output / 'library-sample.mkfx'
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED) as output:
        for name, content in files.items():
            entry = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            output.writestr(entry, content)
    asset = p.request('library-package-upload', 'POST', '/api/v1/assets', raw=archive.read_bytes(),
                      content_type='application/octet-stream',
                      headers={'X-Mako-Asset-Name': archive.name}, status=201)
    inspected = p.request('library-package-inspect', 'POST', '/api/v1/effect-packages/inspect',
                          value={'packageAssetId': asset['assetId']})
    canonical_manifest = inspected['manifestJson'].encode('utf-8')
    stored_files = {**files, 'manifest.json': canonical_manifest}

    def package_matches(name, package):
        p.check(package['structureValid'] and package['diagnostics'] == []
                and package['packageId'] == manifest['packageId']
                and package['packageRevision'] == inspected['packageRevision']
                and package['manifest'] == manifest
                and json.loads(package['manifestJson']) == manifest
                and package['manifestJson'].encode('utf-8') == canonical_manifest,
                name + ': complete original manifest values, canonical bytes, fixed identity and valid declarations')
        expected = {item['path']: {'path': item['path'], 'role': item['role'],
                    'size': len(files[item['path']]), 'sha256': item['sha256']} for item in manifest['files']}
        p.check({item['path']: item for item in package['files']} == expected,
                name + ': all declared package files retain exact roles, sizes and SHA256 values')

    package_matches('library-inspection', inspected)
    directories = {
        'configuration': p.output / 'profile/state/script-configurations-v1',
        'template': p.output / 'profile/state/libraries/templates',
    }

    def disk(name, revision):
        directory = directories[name]
        raw = (directory / 'index.json').read_bytes()
        p.check(hashlib.sha256(raw).hexdigest() == revision,
                name + ': public revision is SHA256 of the actual complete published index bytes')
        model = json.loads(raw)
        p.check(len(model['packages']) == 1, name + ': index registers exactly one complete package')
        entry = model['packages'][0]
        p.check(entry['packageId'] == inspected['packageId'] and entry['revision'] == inspected['packageRevision'],
                name + ': on-disk package registration fixes the inspected identity')
        package_root = directory / entry['path']
        p.check(host.inventory(package_root) == {path: hashlib.sha256(content).hexdigest()
                                               for path, content in stored_files.items()},
                name + ': stored canonical manifest and every declared file match the complete fixed closure')
        inventory = host.inventory(directory)
        save(p.output / (name + '-library-files-' + revision + '.json'), inventory)
        return inventory

    configuration_before = p.request('configuration-library-empty', 'GET', CONFIGURATIONS)
    template_before = p.request('template-library-empty', 'GET', TEMPLATES + '/personal/templates')
    p.check(configuration_before['revision'] == template_before['revision'] == 'missing'
            and configuration_before['configurations'] == [] and template_before['templates'] == [],
            'libraries: unique probe profile initially has two missing empty libraries')
    configuration_input = {
        'name': '仓库卫生完整配置', 'exportId': 'word-emphasis',
        'parametersJson': '{"peakScale":1.2345678901234567,"returnUs":120000}',
        'relativeTarget': 'tokenFamily', 'readScope': 'branchInput', 'branchInput': 'vocals',
        'fixedSeed': str(2 ** 64 - 1), 'overlayLayer': 'package',
        'source': {'package': inspected['reference']},
    }

    def configuration_command(name, expected, command, status=200):
        return p.post(name, CONFIGURATIONS + '/commands',
                      {'expectedRevision': expected, 'command': command}, status=status,
                      code='revisionConflict' if status == 409 else None)

    def configuration_detail(name, item, expected_name):
        detail = p.request(name, 'GET', CONFIGURATIONS + '/' + item['configurationId'] + '?'
                           + urlencode({'expectedRevision': item['revision']}))
        actual = detail['configuration']
        expected = {key: value for key, value in configuration_input.items() if key != 'source'}
        expected['name'] = expected_name
        p.check(detail['revision'] == item['revision']
                and all(actual[key] == value for key, value in expected.items())
                and actual['configurationId'] == item['configurationId']
                and actual['packageId'] == inspected['packageId']
                and actual['packageRevision'] == actual['sourceRevision'] == inspected['packageRevision']
                and actual['kind'] == 'Effect'
                and actual['bindingKinds'] == inspected['exports'][0]['bindingKinds'],
                name + ': full configuration fields, decimal precision, UInt64 seed and package identity retained')
        package_matches(name, detail['package'])
        return detail

    created = configuration_command('configuration-create', 'missing',
                                    {'type': 'create', 'configuration': configuration_input})
    p.check(created['changed'] and created['previousRevision'] == 'missing',
            'configuration: create publishes a changed index from the exact missing revision')
    configuration_detail('configuration-created-detail', created, configuration_input['name'])
    before_stale = disk('configuration', created['revision'])
    configuration_command('configuration-stale-replace', 'missing',
                          {'type': 'replace', 'configurationId': created['configurationId'],
                           'configuration': configuration_input}, status=409)
    p.check(host.inventory(directories['configuration']) == before_stale,
            'configuration: stale revision409 leaves the complete library directory byte-identical')
    replaced_input = copy.deepcopy(configuration_input)
    replaced_input['name'] = '仓库卫生配置替换后'
    replaced = configuration_command('configuration-replace', created['revision'],
                                     {'type': 'replace', 'configurationId': created['configurationId'],
                                      'configuration': replaced_input})
    p.check(replaced['changed'] and replaced['previousRevision'] == created['revision']
            and replaced['revision'] != created['revision']
            and replaced['configurationId'] == created['configurationId'],
            'configuration: replacement advances exact revision while preserving configuration identity')
    configuration = configuration_detail('configuration-replaced-detail', replaced, replaced_input['name'])
    configuration_files = disk('configuration', replaced['revision'])

    builtin = p.request('template-builtin', 'GET', TEMPLATES + '/builtin/templates')
    line = next(item for item in builtin['templates'] if item['kind'] == 'line')
    base = p.request('template-builtin-detail', 'GET', TEMPLATES + '/builtin/templates/detail?'
                     + urlencode({'expectedRevision': builtin['revision'], 'templateId': line['templateId'],
                                  'definitionFormat': 'full'}))
    definition = copy.deepcopy(base['definition'])
    definition.update(name='仓库卫生完整模板', properties=['opacity'],
                      packages=[configuration['package']['reference']], effectRecipes=[{
        'recipeId': 'hygiene-recipe', 'name': '完整配置配方', 'target': 'tokenFamily',
        'packageId': inspected['packageId'], 'revision': inspected['packageRevision'],
        'exportId': configuration_input['exportId'], 'parametersJson': configuration_input['parametersJson'],
        'overlayLayer': 'package', 'enabled': True, 'readScope': 'branchInput', 'branchInput': 'vocals',
        'fixedSeed': configuration_input['fixedSeed'], 'configurationId': replaced['configurationId']}])

    def template_command(name, expected, value, status=200):
        return p.post(name, TEMPLATES + '/personal/commands',
                      {'expectedRevision': expected, 'command': value}, status=status,
                      code='revisionConflict' if status == 409 else None)

    def template_detail(name, item, expected):
        detail = p.request(name, 'GET', TEMPLATES + '/personal/templates/detail?'
                           + urlencode({'expectedRevision': item['revision'], 'templateId': item['templateId'],
                                        'definitionFormat': 'full'}))
        p.check(detail['revision'] == item['revision'] and detail['summary']['templateId'] == item['templateId']
                and detail['definitionFormat'] == 'full' and detail['definitionAvailability'] == 'fullAvailable'
                and all(detail['definition'][key] == expected[key] for key in
                        ('name', 'kind', 'style', 'properties', 'effectRecipes'))
                and len(detail['packageClosureDetails']) == len(detail['definition']['packages']) == 1,
                name + ': complete selected and unselected style, ordered recipe and package closure retained')
        closure = detail['packageClosureDetails'][0]
        package_matches(name, closure)
        p.check(closure['reference']['kind'] == 'library'
                and closure['reference']['libraryId'] == 'personal'
                and closure['reference']['libraryRevision'] == item['revision']
                and closure['reference']['templateId'] == item['templateId']
                and detail['definition']['packages'][0] == closure['reference'],
                name + ': complete stored package reference belongs to the saved template and exact library revision')
        return detail

    template_created = template_command('template-create', 'missing',
        {'type': 'create', 'definitionFormat': 'full', 'definition': definition})
    p.check(template_created['changed'] and template_created['previousRevision'] == 'missing',
            'template: create publishes a changed index from the exact missing revision')
    template_detail('template-created-detail', template_created, definition)
    before_stale = disk('template', template_created['revision'])
    template_command('template-stale-replace', 'missing',
        {'type': 'replace', 'templateId': template_created['templateId'],
         'definitionFormat': 'full', 'definition': definition}, status=409)
    p.check(host.inventory(directories['template']) == before_stale,
            'template: stale revision409 leaves the complete library directory byte-identical')
    definition['name'] = '仓库卫生模板替换后'
    template_replaced = template_command('template-replace', template_created['revision'],
        {'type': 'replace', 'templateId': template_created['templateId'],
         'definitionFormat': 'full', 'definition': definition})
    p.check(template_replaced['changed'] and template_replaced['previousRevision'] == template_created['revision']
            and template_replaced['revision'] != template_created['revision']
            and template_replaced['templateId'] == template_created['templateId'],
            'template: replacement advances exact revision while preserving template identity')
    template = template_detail('template-replaced-detail', template_replaced, definition)
    template_files = disk('template', template_replaced['revision'])
    configuration_list = p.request('configuration-list-before-restart', 'GET', CONFIGURATIONS + '?'
                                    + urlencode({'expectedRevision': replaced['revision']}))
    template_list = p.request('template-list-before-restart', 'GET', TEMPLATES + '/personal/templates?'
                              + urlencode({'expectedRevision': template_replaced['revision']}))
    old_host, old_client = p.host, p.client
    p.stop('libraries-saved-shutdown')
    p.start('libraries-restart')
    p.connect('libraries-restart')
    p.check(p.host != old_host and p.client != old_client,
            'libraries: orderly same-profile restart creates a new host and a new client')
    p.check(configuration_detail('configuration-reloaded-detail', replaced, replaced_input['name']) == configuration
            and template_detail('template-reloaded-detail', template_replaced, definition) == template,
            'libraries: new client reads byte-value-identical complete details after disk reload')
    p.check(p.request('configuration-list-after-restart', 'GET', CONFIGURATIONS + '?'
                      + urlencode({'expectedRevision': replaced['revision']})) == configuration_list
            and p.request('template-list-after-restart', 'GET', TEMPLATES + '/personal/templates?'
                          + urlencode({'expectedRevision': template_replaced['revision']})) == template_list,
            'libraries: complete lists, identities and exact revisions survive reload')
    p.check(disk('configuration', replaced['revision']) == configuration_files
            and disk('template', template_replaced['revision']) == template_files,
            'libraries: full indexes and package directories remain byte-identical across restart reload')
    result = {'configurationRevision': replaced['revision'], 'templateRevision': template_replaced['revision'],
              'packageId': inspected['packageId'], 'packageRevision': inspected['packageRevision'],
              'sampleFiles': {name: hashlib.sha256(content).hexdigest() for name, content in files.items()},
              'storedCanonicalManifestSha256': hashlib.sha256(canonical_manifest).hexdigest(),
              'archiveSha256': host.digest(archive), 'oldRevisionRejectedWithoutByteChanges': True,
              'fullDetailsAndFilesEqualAfterRestart': True,
              'scope': 'Two library create/replace, complete package bytes, CAS and orderly same-profile new-client reload. No script compilation, execution or author draft recovery.'}
    save(p.output / 'libraries-summary.json', result)
    return result
