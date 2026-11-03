#!/usr/bin/env python3
"""Incremental 0.95 basic-business cases; older frozen fixtures remain unchanged."""
import copy
import json
from template_library_fixtures import static_definition
from template_library_schema import PROPERTIES as CURRENT_PROPERTIES
PROPERTIES = CURRENT_PROPERTIES[:21]


def fixtures():
    cases = []
    def add(name, schema, value, valid=True):
        cases.append(dict(name='basic-business-' + name, schema=schema, value=copy.deepcopy(value), valid=valid))
    metadata = dict(requestId='basic-request', hostInstanceId='basic-host', validUntilUtc='2030-01-01T00:00:00Z')
    commands = [dict(type='audio.replaceAsset', assetId='owned-audio'), dict(type='audio.clear'),
        dict(type='project.rename', name=' 工程 🎵 '),
        dict(type='orchestration.setClipMembers', branchId='branch', clipId='clip', lineIds=['line1','line2']),
        dict(type='orchestration.splitClip', branchId='branch', clipId='clip', splitBeforeLineId='line2', newClipId='right'),
        dict(type='orchestration.mergeClips', branchId='branch', clipIds=['clip','right'], targetClipId='clip')]
    for command in commands:
        name=command['type']
        add(name, 'ProjectCommandDtoInput', command)
        add(name+'-envelope', 'ProjectCommandRequest', dict(expectedRevision='0',command=command,**metadata))
        for field in command:
            add(name+'-missing-'+field, 'ProjectCommandDtoInput', {k:v for k,v in command.items() if k!=field}, False)
            add(name+'-null-'+field, 'ProjectCommandDtoInput', {**command,field:None}, False)
            if isinstance(command[field],str):
                add(name+'-blank-'+field, 'ProjectCommandDtoInput', {**command,field:' \t'}, False)
        for field in ('target','startUs','unexpected'):
            add(name+'-cross-'+field, 'ProjectCommandDtoInput', {**command,field:None}, False)
        for field in ('branchId','clipId','splitBeforeLineId','newClipId','assetId','targetClipId'):
            if field in command:add(name+'-long-'+field, 'ProjectCommandDtoInput', {**command,field:'x'*129}, False)
        for field in ('lineIds','clipIds'):
            if field not in command:continue
            for suffix,value in [('empty',[]),('duplicate',['a','a']),('blank',['a',' ']),('null-item',['a',None]),('1001',['i'+str(i) for i in range(1001)])]:
                add(name+'-'+field+'-'+suffix, 'ProjectCommandDtoInput', {**command,field:value},False)
            add(name+'-'+field+'-1000', 'ProjectCommandDtoInput', {**command,field:['i'+str(i) for i in range(1000)]})
    add('rename-256','ProjectCommandDtoInput',dict(type='project.rename',name='x'*256))
    add('rename-257','ProjectCommandDtoInput',dict(type='project.rename',name='x'*257),False)
    add('merge-single','ProjectCommandDtoInput',dict(type='orchestration.mergeClips',branchId='b',clipIds=['c'],targetClipId='c'),False)
    requests = {
        'LyricsExportPlanRequest':dict(expectedRevision='0',branchId='branch',format='lrc.enhanced',options={},metadata=metadata),
        'LyricsExportRequest':dict(expectedRevision='0',planId='plan',acceptLosses=True,metadata=metadata),
        'TemplateImportPreflightRequest':dict(assetId='owned-mktpl',metadata=metadata),
        'TemplateImportRequest':dict(expectedRevision='missing',preflightId='preflight',metadata=metadata),
        'TemplateArchiveExportRequest':dict(expectedRevision='a'*64,templateIds=[' /模板🎵/ '],metadata=metadata)}
    for schema, request in requests.items():
        add(schema,schema,request)
        for field in request:
            add(schema+'-missing-'+field,schema,{k:v for k,v in request.items() if k!=field},False)
            add(schema+'-null-'+field,schema,{**request,field:None},False)
        add(schema+'-unknown',schema,{**request,'path':'/tmp/forbidden'},False)
        if schema.startswith('Lyrics'):
            for rev in ('01','+1','-1','9223372036854775808',0):
                add(schema+'-revision-'+repr(rev),schema,{**request,'expectedRevision':rev},False)
        if schema in ('TemplateImportRequest','TemplateArchiveExportRequest'):
            for rev in ('0','A'*64,'a'*63,' missing '):
                add(schema+'-revision-'+repr(rev),schema,{**request,'expectedRevision':rev},False)
    plan=requests['LyricsExportPlanRequest']
    add('lrc-plain','LyricsExportPlanRequest',{**plan,'format':'lrc.plain'})
    add('ttml-project','LyricsExportPlanRequest',{**plan,'format':'ttml.project'})
    add('ttml-amll','LyricsExportPlanRequest',{**plan,'format':'ttml.amll'})
    add('lrc-invalid-format','LyricsExportPlanRequest',{**plan,'format':'srt'},False)
    for value in ('',None):
        add('lrc-empty-branch-'+repr(value),'LyricsExportPlanRequest',{**plan,'branchId':value},False)
    for title in ('歌曲 🎵','x'*1024,None):
        add('options-title-'+str(len(title) if title else title),'LyricsExportOptionsDtoInput',dict(title=title))
    for title in ('x'*1025,'bad]tag','bad\nline','\u0085'):
        add('options-title-invalid-'+repr(title[:12]),'LyricsExportOptionsDtoInput',dict(title=title),False)
    add('options-complete','LyricsExportOptionsDtoInput',dict(title='歌',artist='艺术家',album='专辑',newLine='\r\n',writeByteOrderMark=True,millisecondPrecision=True))
    add('options-newline-invalid','LyricsExportOptionsDtoInput',dict(newLine='\r'),False)
    add('options-null-newline','LyricsExportOptionsDtoInput',dict(newLine=None),False)
    add('options-extra','LyricsExportOptionsDtoInput',dict(unexpected=1),False)
    archive=requests['TemplateArchiveExportRequest']
    for suffix,ids,valid in [('one',['id'],True),('100',['id'+str(i) for i in range(100)],True),('101',['id'+str(i) for i in range(101)],False),('empty',[],False),('dup',['id','id'],False),('blank',[' '],False),('null',[None],False),('long',['x'*1000],True)]:
        add('archive-ids-'+suffix,'TemplateArchiveExportRequest',{**archive,'templateIds':ids},valid)
    query=dict(expectedRevision='0',branchId='branch',lineId='line')
    add('resolved-query','ProjectResolvedStyleQuery',query)
    add('resolved-query-full','ProjectResolvedStyleQuery',{**query,'tokenId':'token','clipId':'clip','nodeId':'slot'})
    for field in query:add('resolved-query-missing-'+field,'ProjectResolvedStyleQuery',{k:v for k,v in query.items() if k!=field},False)
    for schema,value in [('ProjectResolvedStyleQuery',query),('ProjectResourcesQuery',dict(expectedRevision='0')),('ProjectDirectoryEntriesQuery',{})]:
        add(schema,schema,value)
        add(schema+'-unknown',schema,{**value,'unexpected':'x'},False)
    for schema,base in [('ProjectResourcesQuery',dict(expectedRevision='0')),('ProjectDirectoryEntriesQuery',{})]:
        for limit in (0,1,100,101):add(schema+'-limit-'+str(limit),schema,{**base,'limit':limit},1<=limit<=100)
        add(schema+'-offset-negative',schema,{**base,'offset':-1},False)
    context=dict(branchId='branch',lineId='line',tokenId=None,clipId=None,containerId='root',nodeId='root',textNodeId='text:line',nodePath=['root','text:line'],contextKind='container')
    resolved=dict(projectSessionId='session',projectRevision='0',context=context,style=static_definition()['style'],sources=[dict(property=p,sourcePath='project.baseStyle') for p in PROPERTIES],resolution='staticPresetHierarchy',finalPixelAppearance=False,protocolVersion='1')
    add('resolved-style','ProjectResolvedStyleResponse',resolved)
    for field in ('tokenId','clipId'):
        add('resolved-context-missing-'+field,'ResolvedStyleContextDto',{k:v for k,v in context.items() if k!=field},False)
    add('resolved-style-no-final-pixels','ProjectResolvedStyleResponse',{**resolved,'finalPixelAppearance':True},False)
    add('resolved-style-missing-source','ProjectResolvedStyleResponse',{**resolved,'sources':resolved['sources'][:-1]},False)
    add('resolved-style-source-order','ProjectResolvedStyleResponse',{**resolved,'sources':list(reversed(resolved['sources']))},False)
    resource=dict(resourceId='resource',kind='media',roles=['audio'],status='available',sizeBytes='9223372036854775807',sha256='a'*64,assetId='owned',contentUrl='/api/v1/assets/owned/content',preparationStatus='prepared',waveformStatus='available')
    add('resource','ProjectResourceDto',resource)
    add('resource-int64-number-rejected','ProjectResourceDto',{**resource,'sizeBytes':1},False)
    for field,value in [('kind','filePath'),('status','unknown'),('preparationStatus','pending'),('waveformStatus','pending')]:add('resource-invalid-'+field,'ProjectResourceDto',{**resource,field:value},False)
    package_revision='sha256:'+'b'*64
    package_summary=dict(packageId='demo.closed',packageRevision=package_revision,manifestSizeBytes='123',manifestSha256='a'*64,totalSizeBytes='456',fileCount=3,validatedFileCount=3,unsupportedFileCount=0,status='validated')
    package_manifest=dict(resourceId='packageManifest:'+package_revision,kind='effectPackageManifest',roles=['project.effects:demo.closed:'+package_revision],status='validated',sizeBytes='123',sha256='a'*64,packageId='demo.closed',packageRevision=package_revision,packagePath='manifest.json',packageRole='manifest',packageClosure=package_summary)
    package_file=dict(resourceId='packageFile:'+package_revision+':src/PackEffect.cs',kind='effectPackageFile',roles=['project.effects:demo.closed:src/PackEffect.cs'],status='validated',sizeBytes='64',sha256='c'*64,packageId='demo.closed',packageRevision=package_revision,packagePath='src/PackEffect.cs',packageRole='csharp')
    add('package-manifest-resource','ProjectResourceDto',package_manifest)
    add('package-file-resource','ProjectResourceDto',package_file)
    add('package-unsupported-resource','ProjectResourceDto',{**package_manifest,'status':'unsupported','packageClosure':{**package_summary,'unsupportedFileCount':1,'status':'unsupported'}},True)
    add('package-summary-missing-status','ProjectPackageClosureSummaryDto',{k:v for k,v in package_summary.items() if k!='status'},False)
    resources=dict(projectSessionId='session',projectRevision='0',offset=0,totalCount=1,resources=[resource],protocolVersion='1')
    add('resources-last-page','ProjectResourcesResponse',resources)
    add('resources-next-page','ProjectResourcesResponse',{**resources,'totalCount':2,'nextOffset':1})
    add('resources-null-next','ProjectResourcesResponse',{**resources,'nextOffset':None},False)
    entries=dict(rootGrantId='root',relativePath='',offset=0,totalCount=1,entries=[dict(name='工程',relativePath='工程',isProject=True)],protocolVersion='1')
    add('directory-entries','ProjectDirectoryEntriesResponse',entries)
    assessment=dict(losses=[],exportableLineCount=1,skippedLineCount=0,issues=[],hasLosses=False)
    options=dict(newLine='\n',writeByteOrderMark=False,millisecondPrecision=False)
    expires='2030-01-01T00:00:00Z'
    planned=dict(planId='plan',projectSessionId='session',projectRevision='0',branchId='branch',format='lrc.plain',options=options,encoding='utf-8',expiresAtUtc=expires,plan=assessment,additionalLosses=[],requiresLossAcceptance=False,omittedBranchCount=0,scope='branch',protocolVersion='1')
    add('lyrics-plan-response','LyricsExportPlanResponse',planned)
    add('lyrics-plan-no-all-sources','LyricsExportPlanResponse',{**planned,'scope':'project'},False)
    report=dict(exporterId='lrc.plain',exporterName='LRC',sourceLineCount=1,exportedLineCount=1,skippedLineCount=0,droppedTokenTimeCount=0,roundedTimeCount=0,writtenByteCount=32,metadataCount=0,plan=assessment,writtenRubySegmentCount=0,skippedRubySegmentCount=0,droppedRubySegmentTimeCount=0)
    exported=dict(exportId='export',planId='plan',projectSessionId='session',projectRevision='0',branchId='branch',format='lrc.plain',encoding='utf-8',sizeBytes=32,sha256='a'*64,contentUrl='/api/v1/lyrics-exports/export/content',expiresAtUtc=expires,report=report,additionalLosses=[],protocolVersion='1',scope='branch')
    add('lyrics-export-response','LyricsExportResponse',exported)
    add('lyrics-export-size-number','LyricsExportResponse',{**exported,'sizeBytes':'32'},False)
    add('lyrics-report-bytes-number','LyricsExportReportDto',{**report,'writtenByteCount':'32'},False)
    summary=dict(templateId='template',name='模板',kind='line',templateRevision='a'*64,properties=['opacity'],hasEffectRecipes=False,hasPackageMaterials=False,hasPackages=False)
    preflight=dict(preflightId='preflight',assetSha256='a'*64,archiveBytes=128,expandedBytes=256,templates=[dict(summary=summary,definitionAvailability='staticAvailable')],expiresAtUtc=expires,identityPolicy='generateNewIds',protocolVersion='1')
    add('preflight-response','TemplateImportPreflightResponse',preflight)
    add('preflight-bytes-number','TemplateImportPreflightResponse',{**preflight,'archiveBytes':'128'},False)
    add('preflight-identity-policy','TemplateImportPreflightResponse',{**preflight,'identityPolicy':'preserveIds'},False)
    imported=dict(libraryId='personal',previousRevision='missing',revision='b'*64,changed=True,importedTemplates=[dict(sourceTemplateId='source',templateId='fresh')],protocolVersion='1')
    add('import-response','TemplateImportResponse',imported)
    add('import-revision-not-project','TemplateImportResponse',{**imported,'revision':'1'},False)
    transferred=dict(exportId='transfer',libraryId='personal',revision='b'*64,templateIds=['template'],sizeBytes=128,sha256='a'*64,contentUrl='/api/v1/template-transfers/transfer/archive',expiresAtUtc=expires,protocolVersion='1')
    add('archive-response','TemplateArchiveExportResponse',transferred)
    add('archive-size-number','TemplateArchiveExportResponse',{**transferred,'sizeBytes':'128'},False)
    clip=dict(id='clip',lineIds=['line1'],containerId='container',scheduleContext=dict(lineIds=['line1','line2'],isDefaultSegment=False))
    clip_result=dict(operation='mergeClips',branchId='branch',clips=[clip],removedClipIds=['right'],changed=True,createdApplicationIds=[],removedApplicationIds=['consumed-static-app'])
    add('clip-result-static-consumption','OrchestrationClipMembersCommandResultDto',clip_result)
    add('clip-result-no-op','OrchestrationClipMembersCommandResultDto',{**clip_result,'operation':'setClipMembers','changed':False,'removedClipIds':[],'removedApplicationIds':[]})
    add('clip-result-unknown-operation','OrchestrationClipMembersCommandResultDto',{**clip_result,'operation':'repartition'},False)
    add('clip-result-duplicate-application','OrchestrationClipMembersCommandResultDto',{**clip_result,'removedApplicationIds':['app','app']},False)
    # New response required fields are individually exercised, including false and empty arrays.
    for schema,value in [('ProjectResolvedStyleResponse',resolved),('ProjectResourcesResponse',resources),('ProjectDirectoryEntriesResponse',entries),('LyricsExportPlanResponse',planned),('LyricsExportResponse',exported),('TemplateImportPreflightResponse',preflight),('TemplateImportResponse',imported),('TemplateArchiveExportResponse',transferred),('OrchestrationClipMembersCommandResultDto',clip_result)]:
        for field in value:
            add(schema+'-response-missing-'+field,schema,{k:v for k,v in value.items() if k!=field},False)
            add(schema+'-response-null-'+field,schema,{**value,field:None},False)
    return cases


if __name__ == '__main__':
    print(json.dumps(dict(cases=fixtures()),ensure_ascii=False,indent=2))
