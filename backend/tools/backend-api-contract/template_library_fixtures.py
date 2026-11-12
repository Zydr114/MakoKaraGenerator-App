#!/usr/bin/env python3
"""Incremental 0.84 template-library cases; frozen older examples stay untouched."""
import copy
import json

# Historical generator: contract 1.50.0 migrates these cases forward (style_v2_fixtures).
from style_v2_fixtures import LEGACY_TEMPLATE_PROPERTIES, LEGACY_TEMPLATE_TOKEN_PROPERTIES
PROPERTIES = LEGACY_TEMPLATE_PROPERTIES[:21]
TOKEN_PROPERTIES = tuple(p for p in PROPERTIES if p in LEGACY_TEMPLATE_TOKEN_PROPERTIES)


def static_definition(name='  自有模板 e\u0301 🎵  ', kind='line', properties=None):
    color = dict(red=12, green=34, blue=56, alpha=200)
    material = {'kind': 'solid', 'color': color}
    return {'name': name, 'kind': kind, 'properties': list(properties or PROPERTIES), 'style': {
        'typography': {'fontFamilyName': None, 'fallbackFamilyNames': [' Noto Sans ', '日本語e\u0301'], 'fontSizePx': 72.5,
            'fontWeight': 451, 'isBold': True, 'isItalic': False, 'letterSpacingPx': -.75},
        'unsungFill': {'material': material}, 'sungFill': {'material': copy.deepcopy(material)}, 'stroke': None, 'shadow': None,
        'verticalCenterRatio': .625, 'horizontalAlignment': 'right', 'opacity': .875, 'scale': 1.125}}


def fixtures():
    cases = []
    def add(name, schema, value, valid=True):
        cases.append({'name': 'template-library-' + name, 'schema': schema, 'valid': valid, 'value': copy.deepcopy(value)})
    definition = static_definition()
    metadata = {'requestId': 'template-owned', 'hostInstanceId': 'owned-host', 'validUntilUtc': '2030-01-01T00:00:00Z'}
    revision = 'a' * 64
    commands = {'create': {'type': 'create', 'definition': definition},
        'replace': {'type': 'replace', 'templateId': ' /自有 🎵/ ', 'definition': definition},
        'copy': {'type': 'copy', 'sourceLibraryId': 'builtin', 'sourceRevision': revision, 'templateId': 'builtin:line-classic', 'name': '复制'},
        'rename': {'type': 'rename', 'templateId': ' /自有 🎵/ ', 'name': '改名 e\u0301'},
        'delete': {'type': 'delete', 'templateId': ' /自有 🎵/ '}}
    for kind, command in commands.items():
        add('command-' + kind, 'TemplateLibraryCommandDtoInput', command)
        add('request-' + kind, 'TemplateLibraryCommandRequest', {'expectedRevision': revision, 'metadata': metadata, 'command': command})
        add('missing-type-' + kind, 'TemplateLibraryCommandDtoInput', {k:v for k,v in command.items() if k != 'type'}, False)
        add('case-type-' + kind, 'TemplateLibraryCommandDtoInput', {**command, 'type': kind.upper()}, False)
        for field in command:
            if field == 'type':continue
            add('missing-' + kind + '-' + field, 'TemplateLibraryCommandDtoInput', {k:v for k,v in command.items() if k != field}, False)
            add('null-' + kind + '-' + field, 'TemplateLibraryCommandDtoInput', {**command, field:None}, False)
        for field in ('projectSessionId', 'target', 'sourceLibraryId', 'sourceRevision', 'templateId', 'name', 'definition', 'extra'):
            if field not in command:add('cross-field-' + kind + '-' + field, 'TemplateLibraryCommandDtoInput', {**command, field:None}, False)
    request = {'expectedRevision': revision, 'metadata': metadata, 'command': commands['create']}
    for field in request:
        add('request-missing-' + field, 'TemplateLibraryCommandRequest', {k:v for k,v in request.items() if k != field}, False)
        add('request-null-' + field, 'TemplateLibraryCommandRequest', {**request, field:None}, False)
    add('request-unknown', 'TemplateLibraryCommandRequest', {**request, 'path':'/tmp/not-accepted'}, False)
    for invalid in ('', 'sha256:' + revision, 'A' * 64, 'a' * 63, ' missing ', 0, None):
        add('revision-invalid-' + str(invalid), 'TemplateLibraryCommandRequest', {**request, 'expectedRevision':invalid}, False)
    add('revision-missing-index', 'TemplateLibraryCommandRequest', {**request, 'expectedRevision':'missing'})
    add('copy-personal', 'TemplateLibraryCommandDtoInput', {**commands['copy'], 'sourceLibraryId':'personal'})
    add('copy-unknown-library', 'TemplateLibraryCommandDtoInput', {**commands['copy'], 'sourceLibraryId':'arbitrary'}, False)
    for field in ('templateId', 'name'):
        for invalid in ('', ' \t'):
            add('rename-blank-' + field + '-' + repr(invalid), 'TemplateLibraryCommandDtoInput', {**commands['rename'], field:invalid}, False)
    add('new-name-256', 'TemplateLibraryCommandDtoInput', {**commands['rename'], 'name':'x'*256})
    add('new-name-257', 'TemplateLibraryCommandDtoInput', {**commands['rename'], 'name':'x'*257}, False)
    add('legacy-long-id', 'TemplateLibraryCommandDtoInput', {**commands['delete'], 'templateId':'x'*1000})
    for prop in PROPERTIES:
        add('property-' + prop, 'StaticTemplateDefinitionDtoInput', static_definition(properties=[prop]))
        add('token-property-' + prop, 'StaticTemplateDefinitionDtoInput', static_definition(kind='token',properties=[prop]), prop in TOKEN_PROPERTIES)
        add('composite-property-' + prop, 'StaticTemplateDefinitionDtoInput', static_definition(kind='composite',properties=[prop]), prop not in ('verticalCenterRatio','horizontalAlignment'))
    add('original-width-and-whole-not-patch-conflict','StaticTemplateDefinitionDtoInput',static_definition(properties=['strokeSpec','strokeWidthPx']))
    for invalid in ([], ['opacity','opacity'], ['typography.fontSizePx'], ['Opacity'], ['unsupported'], [None], None):
        add('properties-invalid-' + repr(invalid), 'StaticTemplateDefinitionDtoInput', {**definition,'properties':invalid},False)
    for invalid in ('Line','unknown',0,None):
        add('kind-invalid-' + str(invalid),'StaticTemplateDefinitionDtoInput',{**definition,'kind':invalid},False)
    for field in definition:
        add('definition-missing-' + field,'StaticTemplateDefinitionDtoInput',{k:v for k,v in definition.items() if k!=field},False)
        add('definition-null-' + field,'StaticTemplateDefinitionDtoInput',{**definition,field:None},False)
    add('definition-unknown','StaticTemplateDefinitionDtoInput',{**definition,'effectRecipes':[]},False)
    for section, value, schema in [('style',definition['style'],'TemplateLibraryStyleInput'),
        ('typography',definition['style']['typography'],'TemplateLibraryTypographyInput')]:
        for field in value:
            add(section+'-missing-'+field,schema,{k:v for k,v in value.items() if k!=field},False)
            add(section+'-null-'+field,schema,{**value,field:None},field in ('fontFamilyName','stroke','shadow'))
        add(section+'-unknown',schema,{**value,'extra':None},False)
    old=copy.deepcopy(definition);old['name']='x'*300;old['style']['typography']['fontFamilyName']='x'*300
    add('legacy-long-complete-read','StaticTemplateDefinitionDto',old)
    add('legacy-long-new-input-rejected','StaticTemplateDefinitionDtoInput',old,False)
    for count in (32,33):
        value=copy.deepcopy(definition);value['style']['typography']['fallbackFamilyNames']=['font'+str(i) for i in range(count)]
        add('fallback-'+str(count),'StaticTemplateDefinitionDtoInput',value,count==32)
    value=copy.deepcopy(definition);value['style']['typography']['fallbackFamilyNames']=['same','same']
    add('fallback-duplicate','StaticTemplateDefinitionDtoInput',value,False)
    rgba=dict(red=1,green=2,blue=3,alpha=255)
    gradient={'kind':'gradient','gradient':{'kind':'radial','angleDegrees':721.25,'centerX':.25,'centerY':.75,'radiusX':.5,'radiusY':.625,
        'coordinateSpace':'progress','colorSpace':'oklch','huePath':'increasing','stops':[{'offset':0,'color':rgba},{'offset':.5,'color':rgba},{'offset':.5,'color':rgba},{'offset':1,'color':rgba}]}}
    value=copy.deepcopy(definition);value['style']['sungFill']['material']=gradient
    add('gradient-all-fields-hard-stops','StaticTemplateDefinitionDtoInput',value)
    value['style']['sungFill']['material']['gradient']['stops']=[{'offset':i/256,'color':rgba} for i in range(257)]
    add('gradient-257-new','StaticTemplateDefinitionDtoInput',value,False)
    add('gradient-257-legacy-read','StaticTemplateDefinitionDto',value)
    package={'kind':'package','packageId':'owned','revision':revision,'exportId':'ink'}
    for field in ('sungFill','unsungFill'):
        value=copy.deepcopy(definition);value['style'][field]['material']=package
        add('package-even-unselected-'+field,'StaticTemplateDefinitionDtoInput',value,False)
        add('package-never-static-definition-'+field,'StaticTemplateDefinitionDto',value,False)
    summary={'templateId':' /自有 🎵/ ','name':'模板','kind':'line','templateRevision':revision,'properties':['opacity'],
        'hasEffectRecipes':False,'hasPackageMaterials':False,'hasPackages':False}
    detail={'libraryId':'personal','revision':revision,'summary':summary,'definitionAvailability':'staticAvailable','definition':definition,'protocolVersion':'1'}
    add('detail-static','TemplateDetailResponse',detail)
    advanced={**detail,'definitionAvailability':'advancedUnsupported','definition':None,'summary':{**summary,'hasPackages':True}}
    add('detail-advanced-required-null','TemplateDetailResponse',advanced)
    add('detail-advanced-missing-definition','TemplateDetailResponse',{k:v for k,v in advanced.items() if k!='definition'},False)
    add('detail-advanced-not-static-descriptor','TemplateDetailResponse',{**advanced,'definition':definition},False)
    add('detail-static-not-null','TemplateDetailResponse',{**detail,'definition':None},False)
    add('detail-static-not-advanced-summary','TemplateDetailResponse',{**detail,'summary':advanced['summary']},False)
    add('detail-missing-protocol','TemplateDetailResponse',{k:v for k,v in detail.items() if k!='protocolVersion'},False)
    add('summary-no-fake-modified-time','TemplateSummaryDto',{**summary,'modifiedAt':'2030-01-01T00:00:00Z'},False)
    add('summary-no-partial-definition','TemplateSummaryDto',{**summary,'parameters':{}},False)
    libraries={'libraries':[{'libraryId':'builtin','name':'内置','readOnly':True},{'libraryId':'personal','name':'个人','readOnly':False}],'protocolVersion':'1'}
    add('discovery','TemplateLibrariesResponse',libraries)
    add('builtin-readonly','TemplateLibraryDescriptorDto',libraries['libraries'][0])
    add('builtin-cannot-write','TemplateLibraryDescriptorDto',{**libraries['libraries'][0],'readOnly':False},False)
    listed={'libraryId':'personal','revision':revision,'offset':0,'totalCount':1,'templates':[summary],'protocolVersion':'1'}
    add('list-last','TemplateListResponse',listed)
    add('list-next','TemplateListResponse',{**listed,'totalCount':2,'nextOffset':1})
    add('list-no-null-next','TemplateListResponse',{**listed,'nextOffset':None},False)
    add('list-empty-missing','TemplateListResponse',{**listed,'revision':'missing','totalCount':0,'templates':[]})
    add('list-too-many','TemplateListResponse',{**listed,'templates':[summary]*101},False)
    result={'libraryId':'personal','previousRevision':'missing','revision':revision,'changed':True,'templateId':summary['templateId'],'protocolVersion':'1'}
    add('receipt-create','TemplateLibraryCommandResponse',result)
    add('receipt-delete-keeps-id','TemplateLibraryCommandResponse',{**result,'previousRevision':revision,'revision':'b'*64})
    add('receipt-noop','TemplateLibraryCommandResponse',{**result,'changed':False,'previousRevision':revision})
    for field in result:add('receipt-missing-'+field,'TemplateLibraryCommandResponse',{k:v for k,v in result.items() if k!=field},False)
    for field in ('definition','projectSessionId','projectRevision','undoName','changeSet'):
        add('receipt-no-project-'+field,'TemplateLibraryCommandResponse',{**result,field:None},False)
    return cases


if __name__ == '__main__':
    print(json.dumps({'cases':fixtures()},ensure_ascii=False,indent=2))
