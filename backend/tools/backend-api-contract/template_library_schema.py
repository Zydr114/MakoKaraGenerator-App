"""Reviewed independent template-library transport; never mutates 0.83 style schemas."""
import copy

from static_style_schema import PROPERTIES, TOKEN_PROPERTIES, audit, followable, obj, ref, strokes_array

REVISION = {'type': 'string', 'pattern': '^(missing|[0-9a-f]{64})$'}
IDENTIFIER = {'type': 'string', 'minLength': 1, 'pattern': r'\S'}
NEW_NAME = {**IDENTIFIER, 'maxLength': 256, 'x-max-utf16-code-units': 256}
LIMITS = {'maxIntentBytes': 262144, 'maxNameUtf16CodeUnits': 256, 'maxFontUtf16CodeUnits': 256,
          'maxFallbacks': 32, 'maxGradientStops': 256, 'maxReadBytes': 1048576,
          'maxReadValues': 20000, 'maxReceiptBytes': 65536,
          'description': '完整command canonical JSON不含metadata；完整响应/回执提交前预算。新定义限额不套旧模板复制/改名/删除。索引原16MiB、包原256MiB/1024文件/单64MiB；分页只约束响应，不承诺全库IO被分页限制。'}


def query_parameters(detail=False):
    result = [{'name': 'expectedRevision', 'in': 'query', 'required': detail,
               'schema': copy.deepcopy(REVISION), 'description': '唯一精确索引字节修订；missing与已发布空库不同，过期409。'}]
    if detail:
        result.append({'name': 'templateId', 'in': 'query', 'required': True,
                       'schema': copy.deepcopy(IDENTIFIER), 'description': '精确原始ID，允许Unicode/空格/斜杠；不trim或新增128字符限制。'})
    else:
        result.extend([{'name': 'offset', 'in': 'query', 'schema': {'type': 'integer', 'minimum': 0, 'maximum': 2147483647, 'default': 0}},
                       {'name': 'limit', 'in': 'query', 'schema': {'type': 'integer', 'minimum': 1, 'maximum': 100, 'default': 50}}])
    return result


def add_canonical_style(schemas):
    """Complete static values need a separate input object, not 43 patch paths.

    Read clones deliberately preserve legal legacy lengths. Every package slot,
    including an unselected one, makes a template advanced and is not a public
    complete static definition.
    """
    before = copy.deepcopy(schemas)
    static_material = {'oneOf': [ref('SolidStyleMaterialDto'), ref('GradientStyleMaterialDto')]}
    schemas['TemplateLibraryStaticMaterial'] = static_material
    for suffix in ('', 'Input'):
        material = ref('StyleMaterialDtoInput') if suffix else ref('TemplateLibraryStaticMaterial')
        source = copy.deepcopy(schemas['CanonicalStyleDto'])
        typography = copy.deepcopy(schemas['StyleTypographyDto'])
        stroke = copy.deepcopy(schemas['StyleStrokeDto' + suffix])
        stroke['properties']['material'] = copy.deepcopy(material)
        strokes = strokes_array(ref('TemplateLibraryStroke' + suffix), response=False)
        unsung = copy.deepcopy(schemas['StyleAppearanceDto' + suffix])
        unsung['properties'].update(fill=copy.deepcopy(material), strokes=copy.deepcopy(strokes))
        sung = copy.deepcopy(schemas['StyleSungAppearanceDto' + suffix])
        sung['properties'].update(fill=copy.deepcopy(material), strokes=followable(strokes, response=False))
        for value in (unsung, sung):
            value.pop('x-clr-type', None)
        if suffix:
            font = {'type': 'string', 'maxLength': 256, 'x-max-utf16-code-units': 256}
            typography['properties']['fontFamilyName'] = {'anyOf': [font, {'type': 'null'}]}
            typography['properties']['fallbackFamilyNames'] = {'type': 'array', 'maxItems': 32, 'uniqueItems': True,
                'items': {**copy.deepcopy(font), 'minLength': 1, 'pattern': r'\S'}}
        schemas['TemplateLibraryTypography' + suffix] = typography
        schemas['TemplateLibraryStroke' + suffix] = stroke
        schemas['TemplateLibraryAppearance' + suffix] = unsung
        schemas['TemplateLibrarySungAppearance' + suffix] = sung
        source['properties']['typography'] = ref('TemplateLibraryTypography' + suffix)
        source['properties']['unsung'] = ref('TemplateLibraryAppearance' + suffix)
        source['properties']['sung'] = ref('TemplateLibrarySungAppearance' + suffix)
        source['properties']['wipe'] = ref('StyleWipeDto' + suffix)
        source['description'] = '完整静态两态canonical style，所有字段必填；字体/阴影/辉光/扫字线高亮允许显式null；不携带包身份的残缺高级定义。'
        schemas['TemplateLibraryStyle' + suffix] = source
    assert all(schemas[name] == value for name, value in before.items()), 'Template extension changed an older schema'


def definition_constraints(properties):
    """Original template property enum/kind compatibility, without patch conflicts."""
    return [{'if': {'properties': {'kind': {'const': kind}}, 'required': ['kind']},
             'then': {'properties': {'properties': {'items': {'enum': list(allowed)}}}}}
            for kind, allowed in [('token', TOKEN_PROPERTIES),
                                  ('composite', tuple(p for p in properties if p not in ('verticalCenterRatio', 'horizontalAlignment')))]]


def extend_template_library_schemas(schemas, metadata, schema_for):
    previous = copy.deepcopy(schemas)
    for name, values in [('TemplateKind', ['line', 'token', 'composite'])]:
        if metadata[name] != {'kind': 'enum', 'values': values}:
            raise ValueError('Unreviewed template enum: ' + name)
        schemas[name] = {'type': 'string', 'enum': values}
    if 'TemplateProperty' in metadata:
        raise ValueError('Template properties are style property paths, not a separate enum')
    add_canonical_style(schemas)
    library_id = {'type': 'string', 'enum': ['builtin', 'personal']}
    hash_revision = {'type': 'string', 'pattern': '^[0-9a-f]{64}$'}
    def typed(name, fields, *, nullable=(), optional=(), schema=None):
        audit(metadata, name, fields, nullable=nullable, optional=optional)
        value = obj({field: schema_for(clr) for field, clr in fields.items()}) if schema is None else schema
        value['x-clr-type'] = 'MakoKaraGenerator.Backend.Contracts.' + name
        schemas[name] = value
        return value
    descriptor = typed('TemplateLibraryDescriptorDto', {'libraryId': 'String', 'name': 'String', 'readOnly': 'Boolean'})
    descriptor['properties']['libraryId'] = copy.deepcopy(library_id)
    descriptor['allOf'] = [{'if': {'properties': {'libraryId': {'const': identity}}},
        'then': {'properties': {'readOnly': {'const': identity == 'builtin'}}}} for identity in ('builtin', 'personal')]
    discovery = typed('TemplateLibrariesResponse', {'libraries': 'TemplateLibraryDescriptorDto[]', 'protocolVersion': 'String'}, optional=('protocolVersion',))
    discovery['properties']['libraries'].update(minItems=2, maxItems=2)
    discovery['properties']['protocolVersion']['const'] = '1'
    properties = {'type': 'array', 'maxItems': len(PROPERTIES), 'uniqueItems': True, 'items': ref('StylePropertyPath'),
                  'description': '与styles.patch相同的样式属性路径；不排序，拒绝重复与未知路径。'}
    summary = typed('TemplateSummaryDto', {'templateId': 'String', 'name': 'String', 'kind': 'TemplateKind', 'templateRevision': 'String',
        'properties': 'String[]', 'hasEffectRecipes': 'Boolean', 'hasPackageMaterials': 'Boolean', 'hasPackages': 'Boolean'})
    summary['properties']['templateId'] = copy.deepcopy(IDENTIFIER)
    summary['properties']['templateRevision'] = hash_revision
    summary['properties']['properties'] = copy.deepcopy(properties)
    audit(metadata, 'StaticTemplateDefinitionDto', {'name': 'String', 'kind': 'TemplateKind', 'style': 'CanonicalStyleDto', 'properties': 'String[]'})
    for suffix in ('', 'Input'):
        selected = {**copy.deepcopy(properties), 'minItems': 1}
        definition = obj({'name': copy.deepcopy(NEW_NAME if suffix else IDENTIFIER), 'kind': ref('TemplateKind'),
            'style': ref('TemplateLibraryStyle' + suffix), 'properties': selected})
        definition['allOf'] = definition_constraints(PROPERTIES)
        definition['x-clr-type'] = 'MakoKaraGenerator.Backend.Contracts.StaticTemplateDefinitionDto'
        schemas['StaticTemplateDefinitionDto' + suffix] = definition
    listed = typed('TemplateListResponse', {'libraryId': 'String', 'revision': 'String', 'offset': 'Int32', 'totalCount': 'Int32',
        'nextOffset': 'Int32', 'templates': 'TemplateSummaryDto[]', 'protocolVersion': 'String'}, nullable=('nextOffset',), optional=('protocolVersion',))
    listed['required'].remove('nextOffset')
    for field in ('offset', 'totalCount', 'nextOffset'):listed['properties'][field]['minimum'] = 0
    listed['properties']['templates']['maxItems'] = 100
    listed['properties']['revision'] = copy.deepcopy(REVISION)
    listed['properties']['libraryId'] = copy.deepcopy(library_id)
    listed['properties']['protocolVersion']['const'] = '1'
    detail = typed('TemplateDetailResponse', {'libraryId': 'String', 'revision': 'String', 'summary': 'TemplateSummaryDto',
        'definitionAvailability': 'String', 'definition': 'StaticTemplateDefinitionDto', 'protocolVersion': 'String', 'definitionFormat': 'String', 'packageClosureDetails': 'EffectPackageInspectResponse[]'},
        nullable=('definition', 'definitionFormat', 'packageClosureDetails'), optional=('protocolVersion', 'definitionFormat', 'packageClosureDetails'))
    detail['properties']['libraryId'] = copy.deepcopy(library_id)
    detail['properties']['revision'] = copy.deepcopy(REVISION)
    detail['properties']['protocolVersion']['const'] = '1'
    detail['properties']['definitionAvailability']['enum'] = ['staticAvailable', 'advancedUnsupported']
    detail['properties']['definition'] = {'anyOf': [ref('StaticTemplateDefinitionDto'), {'type': 'null'}]}
    detail['allOf'] = [
        {'if': {'properties': {'definitionAvailability': {'const': 'staticAvailable'}}},
         'then': {'properties': {'definition': ref('StaticTemplateDefinitionDto'), 'summary': {'properties': {
            field: {'const': False} for field in ('hasEffectRecipes', 'hasPackageMaterials', 'hasPackages')}}}}},
        {'if': {'properties': {'definitionAvailability': {'const': 'advancedUnsupported'}}},
         'then': {'properties': {'definition': {'type': 'null'}}}}]
    audit(metadata, 'TemplateLibraryCommandDto', {'type': 'String'})
    commands = [
        ('create', 'CreateTemplateCommandDto', {'definition': 'StaticTemplateDefinitionDto'}),
        ('replace', 'ReplaceTemplateCommandDto', {'templateId': 'String', 'definition': 'StaticTemplateDefinitionDto'}),
        ('copy', 'CopyTemplateCommandDto', {'sourceLibraryId': 'String', 'sourceRevision': 'String', 'templateId': 'String', 'name': 'String'}),
        ('rename', 'RenameTemplateCommandDto', {'templateId': 'String', 'name': 'String'}),
        ('delete', 'DeleteTemplateCommandDto', {'templateId': 'String'}),
    ]
    variants = []
    for typ, name, fields in commands:
        audit(metadata, name, {'type': 'String', **fields, **({'definitionFormat': 'String'} if typ in ('create', 'replace') else {})}, base='TemplateLibraryCommandDto', nullable=('definitionFormat',) if typ in ('create', 'replace') else (), optional=('definitionFormat',) if typ in ('create', 'replace') else ())
        members = {'type': {'type': 'string', 'const': typ}}
        for field, clr in fields.items():
            members[field] = (ref('StaticTemplateDefinitionDtoInput') if field == 'definition' else
                copy.deepcopy(REVISION) if field == 'sourceRevision' else copy.deepcopy(library_id) if field == 'sourceLibraryId' else
                copy.deepcopy(NEW_NAME) if field == 'name' else copy.deepcopy(IDENTIFIER))
        schemas[name + 'Input'] = {**obj(members), 'x-clr-type': 'MakoKaraGenerator.Backend.Contracts.' + name,
                                  'x-template-library-limits': copy.deepcopy(LIMITS)}
        variants.append(ref(name + 'Input'))
    schemas['TemplateLibraryCommandDtoInput'] = {'oneOf': variants}
    audit(metadata, 'TemplateLibraryCommandRequest', {'expectedRevision': 'String', 'metadata': 'RequestMetadataDto', 'command': 'TemplateLibraryCommandDto'})
    schemas['TemplateLibraryCommandRequest'] = {**obj({'expectedRevision': copy.deepcopy(REVISION),
        'metadata': ref('RequestMetadataDtoInput'), 'command': ref('TemplateLibraryCommandDtoInput')}),
        'x-clr-type': 'MakoKaraGenerator.Backend.Contracts.TemplateLibraryCommandRequest'}
    mutated = typed('TemplateLibraryCommandResponse', {'libraryId': 'String', 'previousRevision': 'String', 'revision': 'String',
        'changed': 'Boolean', 'templateId': 'String', 'protocolVersion': 'String'}, optional=('protocolVersion',))
    mutated['properties']['libraryId'] = {'type': 'string', 'const': 'personal'}
    for field in ('previousRevision', 'revision'):mutated['properties'][field] = copy.deepcopy(REVISION)
    mutated['properties']['templateId'] = copy.deepcopy(IDENTIFIER)
    mutated['properties']['protocolVersion']['const'] = '1'
    # Only new components are narrowed. Generic CLR traversal may have made new
    # aliases before the 0.83 style overlay; none may change an older component.
    baseline = set(previous) - {name for name in previous if name.startswith(('Template', 'StaticTemplate', 'CreateTemplate', 'ReplaceTemplate', 'CopyTemplate', 'RenameTemplate', 'DeleteTemplate'))}
    assert all(schemas[name] == previous[name] for name in baseline), 'Template extension changed an existing schema'
