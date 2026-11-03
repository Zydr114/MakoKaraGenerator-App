#!/usr/bin/env python3
"""Real HTTP acceptance for independent profile-shared template libraries."""
import argparse
import copy
import fcntl
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import socket
import struct
import subprocess
import threading
import time
from urllib.parse import urlencode, urlsplit
import urllib.error
import urllib.request
import wave

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('library_style_base', ROOT/'tools/backend-style-http-probe/probe.py')
style = importlib.util.module_from_spec(spec); spec.loader.exec_module(style)
spec = importlib.util.spec_from_file_location('library_fixtures', ROOT/'tools/backend-api-contract/template_library_fixtures.py')
fixtures = importlib.util.module_from_spec(spec)
# Fixture module imports the reviewed incremental schema beside it.
import sys
sys.path.insert(0, str(ROOT/'tools/backend-api-contract'))
spec.loader.exec_module(fixtures)
save_json = style.save_json
LIBRARIES = '/api/v1/template-libraries'
REVISION = re.compile(r'^(missing|[0-9a-f]{64})$')
SUMMARY_FIELDS = {'templateId','name','kind','templateRevision','properties','hasEffectRecipes','hasPackageMaterials','hasPackages'}
RECEIPT_FIELDS = {'libraryId','previousRevision','revision','changed','templateId','protocolVersion'}


def definition(name='  自有模板 e\u0301 🎵  ', kind='line', properties=None):
    return fixtures.static_definition(name, kind, properties)


def create(value):return {'type':'create','definition':copy.deepcopy(value)}
def replace(identifier,value):return {'type':'replace','templateId':identifier,'definition':copy.deepcopy(value)}
def rename(identifier,name):return {'type':'rename','templateId':identifier,'name':name}
def delete(identifier):return {'type':'delete','templateId':identifier}
def duplicate(identifier,name,source='personal',revision=None):
    return {'type':'copy','sourceLibraryId':source,'sourceRevision':revision,'templateId':identifier,'name':name}


class LibraryProbe(style.StyleProbe):
    def __init__(self,output,dist):
        super().__init__(output,dist)
        self.project = None
        self.models = {}
        self.builtin_models = {}
        self.saved_receipts = []
        self.offline_fixtures = []
        self.restart_number = 0
        self.thread_lock = threading.Lock()
        self.index = output/'profile/state/libraries/templates-v7/index.json'

    def index_files(self):
        directory=self.index.parent
        return {str(p.relative_to(directory)):hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(directory.rglob('*')) if p.is_file() and not p.name.endswith('.commit.lock')}

    def project_state(self,name):
        return self.state(name,self.project) if self.project else None

    def list_page(self,name,library='personal',*,revision=None,offset=None,limit=None,client='default'):
        query={}
        if revision is not None:query['expectedRevision']=revision
        if offset is not None:query['offset']=offset
        if limit is not None:query['limit']=limit
        path=LIBRARIES+'/'+library+'/templates'+('?' + urlencode(query) if query else '')
        result=self.request(name,'GET',path,client=client)
        self.check(set(result)=={'libraryId','revision','offset','totalCount','templates','protocolVersion'}|({'nextOffset'} if 'nextOffset' in result else set()),name+': exact list envelope')
        self.check(result['libraryId']==library and REVISION.fullmatch(result['revision']) and result['protocolVersion']=='1',name+': exact library revision identity')
        self.check(result['offset']==(offset or 0) and len(result['templates'])<= (limit or 50),name+': real requested bounded page')
        if revision is not None:self.check(result['revision']==revision,name+': fixed requested revision')
        for item in result['templates']:
            self.check(set(item)==SUMMARY_FIELDS and re.fullmatch('[0-9a-f]{64}',item['templateRevision']) and
                len(item['properties'])==len(set(item['properties'])) and all(p in fixtures.PROPERTIES for p in item['properties']),name+': complete truthful summary')
        return result

    def detail(self,name,identifier,revision,library='personal',*,client='default'):
        result=self.request(name,'GET',LIBRARIES+'/'+library+'/templates/detail?'+urlencode({'templateId':identifier,'expectedRevision':revision}),client=client)
        self.check(set(result)=={'libraryId','revision','summary','definitionAvailability','definition','protocolVersion'} and
            result['libraryId']==library and result['revision']==revision and result['summary']['templateId']==identifier,name+': detail exact identity and required definition presence')
        self.check(set(result['summary'])==SUMMARY_FIELDS and re.fullmatch('[0-9a-f]{64}',result['summary']['templateRevision']),name+': complete stable template identity')
        advanced=any(result['summary'][key] for key in ('hasEffectRecipes','hasPackageMaterials','hasPackages'))
        self.check(result['definitionAvailability']==('advancedUnsupported' if advanced else 'staticAvailable') and
            ((result['definition'] is None) if advanced else isinstance(result['definition'],dict)),name+': advanced never downgraded to partial definition')
        if not advanced:
            value=result['definition'];self.check(set(value)=={'name','kind','style','properties'} and
                all(value[k]==result['summary'][k] for k in ('name','kind','properties')),name+': full static definition matches summary')
            self.check('fontFamilyName' in value['style']['typography'] and all(k in value['style'] for k in ('stroke','shadow')),
                name+': semantic null fields retain presence')
        return result

    def snapshot(self,name,library='personal',*,client='default',check_model=True):
        first=self.list_page(name+'-list',library,limit=37,client=client)
        entries=list(first['templates']);offset=first.get('nextOffset')
        while offset is not None:
            page=self.list_page(name+'-page-'+str(offset),library,revision=first['revision'],offset=offset,limit=37,client=client)
            self.check(page['totalCount']==first['totalCount'] and page['offset']==len(entries),name+': exact contiguous pages')
            entries.extend(page['templates']);offset=page.get('nextOffset')
        self.check(len(entries)==first['totalCount'] and len({v['templateId'] for v in entries})==len(entries),name+': entire ordered library discovered')
        details=[self.detail(name+'-detail-'+str(i),v['templateId'],first['revision'],library,client=client) for i,v in enumerate(entries)]
        self.check([v['summary'] for v in details]==entries,name+': list/detail same fixed source snapshot')
        model=self.models if library=='personal' else self.builtin_models
        if check_model:
            self.check([v['templateId'] for v in entries]==list(model),name+': exact persistent order and IDs')
            for item in details:
                expected=model[item['summary']['templateId']]
                for k,v in expected['summary'].items():self.check(item['summary'][k]==v,name+': expected summary '+k)
                self.check(item['definition']==expected['definition'],name+': all canonical style/Unicode/null/property values exact')
        return {'revision':first['revision'],'templates':entries,'details':details}

    def intent(self,name,revision,command):
        self.command_counter+=1
        return {'expectedRevision':revision,'metadata':self.metadata('library-'+str(self.command_counter)+'-'+name),'command':copy.deepcopy(command)}

    @staticmethod
    def normalized_definition(value):
        value=copy.deepcopy(value);value['name']=value['name'].strip();return value

    def apply_model(self,command,identifier):
        typ=command['type']
        if typ in ('create','replace'):
            value=self.normalized_definition(command['definition'])
            self.models[identifier]={'definition':value,'summary':{**{k:value[k] for k in ('name','kind','properties')},
                'hasEffectRecipes':False,'hasPackageMaterials':False,'hasPackages':False}}
        elif typ=='copy':
            source=self.models if command['sourceLibraryId']=='personal' else self.builtin_models
            item=copy.deepcopy(source[command['templateId']]);item['summary'].pop('templateRevision',None)
            item['summary']['templateId']=identifier
            item['summary']['name']=command['name'].strip()
            if item['definition'] is not None:item['definition']['name']=command['name'].strip()
            self.models[identifier]=item
        elif typ=='rename':
            item=self.models[identifier];item['summary'].pop('templateRevision',None);item['summary']['name']=command['name'].strip()
            if item['definition'] is not None:item['definition']['name']=command['name'].strip()
        elif typ=='delete':self.models.pop(identifier)
        else:raise AssertionError('unknown model command')

    def mutate(self,name,command,*,changed=True,client='default'):
        before=self.snapshot(name+'-before');project=self.project_state(name+'-project-before');disk=self.index_files()
        payload=self.intent(name,before['revision'],command)
        result=self.request(name,'POST',LIBRARIES+'/personal/commands',value=payload,client=client)
        self.check(set(result)==RECEIPT_FIELDS and result['libraryId']=='personal' and result['previousRevision']==before['revision'] and
            REVISION.fullmatch(result['revision']) and result['changed'] is changed,name+': small complete CAS receipt')
        identifier=result['templateId']
        self.check(bool(identifier.strip()) and (command['type'] in ('create','copy') or identifier==command['templateId']),name+': exact stable mutation identity')
        if command['type'] in ('create','copy'):self.check(identifier not in self.models,name+': newly allocated identity exactly once')
        if changed:self.apply_model(command,identifier)
        after=self.snapshot(name+'-after')
        self.check(result['revision']==after['revision'] and (after['revision']!=before['revision']) is changed,name+': revision changes iff real publication')
        if not changed:self.check(after==before and self.index_files()==disk,name+': no-op preserves exact original index/closure bytes')
        self.check(self.project_state(name+'-project-after')==project,name+': library mutation never changes any project history/dirty/revision/content/events')
        self.saved_receipts.append((name,payload,result))
        return result,payload

    def reject(self,name,command,status,code,*,library='personal',field=None,failure=None,expected=None,raw=None,client='default'):
        before=self.snapshot(name+'-before');project=self.project_state(name+'-project-before');disk=self.index_files()
        payload=self.intent(name,before['revision'] if expected is None else expected,command)
        if raw is None:result=self.request(name,'POST',LIBRARIES+'/'+library+'/commands',value=payload,status=status,code=code,client=client)
        else:
            save_json(self.output/(name+'-raw.json'),{'body':raw})
            result=self.request(name,'POST',LIBRARIES+'/'+library+'/commands',raw=raw.encode(),content_type='application/json',status=status,code=code,client=client)
        if field is not None:self.check(result.get('field')==field,name+': precise actual error field')
        self.check(result.get('failureCode')==failure if failure is not None else 'failureCode' not in result,name+': precise optional failure reason')
        self.check(self.snapshot(name+'-after')==before and self.index_files()==disk and self.project_state(name+'-project-after')==project,
            name+': complete library/project/disk state unchanged')
        return result

    def discover(self,name):
        result=self.request(name,'GET',LIBRARIES)
        self.check(set(result)=={'libraries','protocolVersion'} and [v['libraryId'] for v in result['libraries']]==['builtin','personal'],name+': fixed logical descriptors')
        self.check(all(set(v)=={'libraryId','name','readOnly'} and v['readOnly']==(v['libraryId']=='builtin') for v in result['libraries']),name+': original builtin read-only identity')
        return result

    def start_library(self,name):
        self.start(name);self.connect(name);self.restart_number+=1
        registration=self.register(name+'-second-client',select=False)
        self.secrets.extend((registration['accessToken'],registration['resumeToken']))
        self.second=registration['clientId']

    def concurrent(self,name,payloads,clients=None):
        """Barrier starts real independent sockets; no shared counter mutation in workers."""
        clients=clients or [self.client]*len(payloads);barrier=threading.Barrier(len(payloads));results=[None]*len(payloads)
        def worker(index,payload,client):
            barrier.wait();started=time.perf_counter();data=json.dumps(payload,ensure_ascii=False).encode()
            request=urllib.request.Request(self.base_url+LIBRARIES+'/personal/commands',data=data,method='POST',headers={
                'Content-Type':'application/json','Authorization':'Bearer '+self.tokens[client],'X-Mako-Client-Id':client,'X-Mako-Host-Instance-Id':self.host})
            try:
                try:response=self.http.open(request,timeout=40)
                except urllib.error.HTTPError as error:response=error
                with response:body=response.read();status=response.status;headers=dict(response.headers.items())
                results[index]=(status,headers,json.loads(body),round((time.perf_counter()-started)*1000,3))
            except Exception as error:results[index]=error
        threads=[threading.Thread(target=worker,args=(i,p,c)) for i,(p,c) in enumerate(zip(payloads,clients))]
        for thread in threads:thread.start()
        for thread in threads:thread.join(45);self.check(not thread.is_alive(),name+': bounded real HTTP terminal')
        for i,(payload,client,result) in enumerate(zip(payloads,clients,results)):
            if isinstance(result,Exception):raise result
            status,headers,value,elapsed=result;self.request_count+=1;prefix=str(self.request_count).zfill(3)+'-'+name+'-'+str(i)
            save_json(self.output/(prefix+'.request.json'),{'method':'POST','path':LIBRARIES+'/personal/commands',
                'headers':{'Authorization':'<redacted>','X-Mako-Client-Id':client,'X-Mako-Host-Instance-Id':self.host,'Content-Type':'application/json'},'json':self.redact(payload)})
            save_json(self.output/(prefix+'.response.json'),{'status':status,'headers':headers,'json':self.redact(value),'body':json.dumps(self.redact(value),ensure_ascii=False)})
            self.samples.append({'name':name+'-'+str(i),'method':'POST','path':LIBRARIES+'/personal/commands','elapsedMs':elapsed})
        return [(v[0],v[2]) for v in results]

    def builtin(self):
        actual=self.snapshot('builtin-first','builtin',check_model=False)
        expected_style={'typography':{'fontFamilyName':None,'fallbackFamilyNames':[],'fontSizePx':72,'fontWeight':400,
            'isBold':False,'isItalic':False,'letterSpacingPx':0},'unsungFill':{'material':style.solid(style.rgba(74,80,96))},
            'sungFill':{'material':style.solid(style.rgba(76,141,255))},'stroke':None,'shadow':None,'verticalCenterRatio':.5,
            'horizontalAlignment':'center','opacity':1,'scale':1}
        expected=[('builtin:line-classic','经典整行','line',list(fixtures.PROPERTIES)),
            ('builtin:token-highlight','逐字强调','token',['unsungColor','sungColor'])]
        self.check(len(actual['details'])==2,'exact original two builtin templates')
        for detail,(identifier,name,kind,properties) in zip(actual['details'],expected):
            definition_value={'name':name,'kind':kind,'style':expected_style,'properties':properties}
            self.check(detail['summary']['templateId']==identifier and detail['definition']==definition_value,'builtin original values and meaning '+identifier)
            self.builtin_models[identifier]={'definition':copy.deepcopy(definition_value),'summary':copy.deepcopy(detail['summary'])}
        self.snapshot('builtin-second-client','builtin',client=self.second)
        self.builtin_revision=actual['revision']

    def prepare_project(self):
        audio=io.BytesIO()
        with wave.open(audio,'wb') as stream:
            stream.setnchannels(1);stream.setsampwidth(2);stream.setframerate(8000);stream.writeframes(b'\x00\x00'*16000)
        audio_id=self.upload_owned('library.wav',audio.getvalue(),'audio/wav')
        lyrics_id=self.upload_owned('library.lrc','[00:00.00]甲乙\n'.encode(),'text/plain')
        created=self.request('library-project-create','POST','/api/v1/project-sessions',status=201,
            value={'name':'Owned library noninterference project','audioAssetId':audio_id,'lyricsAssetId':lyrics_id})
        path='/api/v1/project-sessions/'+created['projectSessionId']
        path,opened=self.save_close_open('library-template',path,created['projectRevision'],'editing-original')
        self.close('library-template-close',path,opened['projectRevision'])
        self.project=self.reset_fixture('library-project')
        self.initial_project=self.project_state('library-original-project')

    def static_commands(self):
        first,_=self.mutate('create-without-project',create(definition()))
        self.check(self.project is None,'library create requires no project session/target')
        self.snapshot('personal-shared-second-client',client=self.second)
        self.prepare_project()
        identifier=first['templateId']
        # All 21 original names, including legacy aliases, are independent of the
        # direct style patch property union and have no invented parent conflict.
        for prop in fixtures.PROPERTIES:
            value=definition('属性 '+prop,properties=[prop])
            self.mutate('replace-property-'+prop,replace(identifier,value))
        value=definition('数组值相等 e\u0301')
        self.mutate('replace-whole-original',replace(identifier,value))
        self.mutate('replace-value-equal-new-arrays',replace(identifier,copy.deepcopy(value)),changed=False)
        self.mutate('rename-trim-noop',rename(identifier,'  '+value['name']+'  '),changed=False)
        for kind,properties in [('token',list(fixtures.TOKEN_PROPERTIES)),('composite',[p for p in fixtures.PROPERTIES if p not in ('verticalCenterRatio','horizontalAlignment')])]:
            self.mutate('create-kind-'+kind,create(definition('同名模板',kind,properties)))
        rich=definition('渐变描边阴影 e\u0301 🎵')
        rich['style']['typography']['fontFamilyName']=' 原值 日本語 🎵 '
        # Glyph/progress and Oklch are deliberately outside the current
        # persistence/rendering support matrix. Keep this rich fixture within
        # the canonical static template contract while exercising radial,
        # HSV, and repeated-stop values.
        rich['style']['unsungFill']['material']=style.gradient(4,kind='radial',coordinateSpace='line',colorSpace='linearRgb',huePath='decreasing')
        rich['style']['sungFill']['material']=style.gradient(4,kind='linear',coordinateSpace='canvas',colorSpace='linearRgb',huePath='shorter')
        rich['style']['unsungFill']['material']['gradient']['stops'][2]['offset']=rich['style']['unsungFill']['material']['gradient']['stops'][1]['offset']
        rich['style']['stroke']=style.stroke(style.gradient(3,coordinateSpace='line',colorSpace='hsv',huePath='increasing'))
        rich['style']['shadow']=style.shadow()
        self.mutate('replace-complete-rich',replace(identifier,rich))
        for properties in (['strokeSpec','strokeWidthPx'],['shadowSpec','shadowBlurPx'],['unsungColor','unsungGradientSpec']):
            other=copy.deepcopy(rich);other['properties']=properties
            self.mutate('legacy-aliases-'+properties[0],replace(identifier,other))
        self.mutate('restore-complete-rich',replace(identifier,rich))
        copied,_=self.mutate('copy-personal-second-client',duplicate(identifier,'  复制 原始 e\u0301  ',revision=self.snapshot('copy-current')['revision']),client=self.second)
        self.mutate('rename-copied-unicode',rename(copied['templateId'],'  再命名 🎵  '))
        self.mutate('copy-builtin-to-personal',duplicate('builtin:line-classic','  内置副本  ',source='builtin',revision=self.builtin_revision))
        self.primary_identifier=identifier

    def replay(self):
        # Every successful type gets an original receipt, then its target is
        # deleted. Replays must still return the identical complete old result.
        original,_=self.mutate('receipt-create',create(definition('receipt-create')))
        identifier=original['templateId']
        self.mutate('receipt-replace',replace(identifier,definition('receipt-replaced',properties=['opacity'])))
        self.mutate('receipt-rename',rename(identifier,'receipt-renamed'))
        revision=self.snapshot('receipt-source')['revision']
        copied,_=self.mutate('receipt-copy',duplicate(identifier,'receipt-copy',revision=revision))
        self.mutate('receipt-delete-original',delete(identifier))
        self.mutate('receipt-delete-copy',delete(copied['templateId']))
        current=self.snapshot('receipt-final-before');disk=self.index_files();project=self.project_state('receipt-project-before')
        for name,payload,expected in list(self.saved_receipts):
            if not name.startswith('receipt-'):continue
            actual=self.request(name+'-replay-after-delete','POST',LIBRARIES+'/personal/commands',value=payload)
            self.check(actual==expected,name+': original complete receipt survives target deletion and stale library revision')
            for field in ('expectedRevision','metadata','command'):
                changed=copy.deepcopy(payload)
                if field=='expectedRevision':changed[field]='b'*64 if payload[field]!='b'*64 else 'c'*64
                elif field=='metadata':changed[field]['validUntilUtc']=style.base.content.timing.s1.smoke.existing.iso(style.base.content.timing.s1.smoke.existing.utc_now()+style.base.content.timing.s1.smoke.existing.timedelta(seconds=170))
                else:
                    cmd=changed['command']
                    if cmd['type'] in ('create','replace'):cmd['definition']['style']['shadow']=style.shadow()
                    elif cmd['type'] in ('copy','rename'):cmd['name']+=' changed'
                    else:cmd['templateId']+='-changed'
                self.request(name+'-reuse-'+field,'POST',LIBRARIES+'/personal/commands',value=changed,status=409,code='requestIdReuse')
        self.check(self.snapshot('receipt-final-after')==current and self.index_files()==disk and self.project_state('receipt-project-after')==project,
            'all receipt replay/reuse leaves current complete state untouched')

    def races(self):
        before=self.snapshot('race-before');project=self.project_state('race-project-before')
        payloads=[self.intent('race-'+str(i),before['revision'],create(definition('race-'+str(i)))) for i in range(2)]
        results=self.concurrent('two-clients-same-CAS',payloads,[self.client,self.second])
        self.check(sorted(status for status,_ in results)==[200,409],'two actual clients race on same CAS: exactly one publication')
        for index,(status,value) in enumerate(results):
            if status==200:self.apply_model(payloads[index]['command'],value['templateId'])
            else:self.check(value['code']=='revisionConflict','losing CAS reports actual revisionConflict')
        self.snapshot('race-after')
        before=self.snapshot('same-receipt-before');payload=self.intent('same-receipt',before['revision'],create(definition('same receipt')))
        results=self.concurrent('same-receipt-owner',[payload,payload])
        self.check([status for status,_ in results]==[200,200] and results[0][1]==results[1][1],'same receipt concurrent waiters see one identical generated ID and complete result')
        self.apply_model(payload['command'],results[0][1]['templateId']);after=self.snapshot('same-receipt-after')
        self.check(len(after['templates'])==len(before['templates'])+1 and self.project_state('race-project-after')==project,'receipt owner publishes exactly once without project state changes')

    def strict(self):
        valid=create(definition('strict'));identifier=self.primary_identifier
        commands=[valid,replace(identifier,definition('replace')),duplicate(identifier,'copy',revision=self.snapshot('strict-revision')['revision']),rename(identifier,'rename'),delete(identifier)]
        for command in commands:
            typ=command['type']
            for field in command:
                self.reject('shape-'+typ+'-missing-'+field,{k:v for k,v in command.items() if k!=field},400,'invalidRequest')
                self.reject('shape-'+typ+'-null-'+field,{**command,field:None},400,'invalidRequest')
            self.reject('shape-'+typ+'-unknown',{**command,'unknown':None},400,'invalidRequest')
            for field in ('target','projectSessionId','name','definition','sourceRevision'):
                if field not in command:self.reject('shape-'+typ+'-foreign-'+field,{**command,field:None},400,'invalidRequest')
        for field in ('name','kind','style','properties'):
            value=definition('missing');value.pop(field)
            self.reject('definition-missing-'+field,create(value),400,'invalidRequest')
            value=definition('null');value[field]=None
            self.reject('definition-null-'+field,create(value),400,'invalidRequest')
        full=definition('complete')
        for section,original in [('style',full['style']),('typography',full['style']['typography'])]:
            for field in original:
                value=copy.deepcopy(full);target=value['style'] if section=='style' else value['style']['typography'];target.pop(field)
                self.reject('required-'+section+'-'+field,create(value),400,'invalidRequest')
        payload=self.intent('raw-duplicates',self.snapshot('raw-current')['revision'],valid)
        raw=json.dumps(payload,ensure_ascii=False)
        for name,body in [('top',raw[:-1]+',"expectedRevision":"missing"}'),
            ('type',raw.replace('"type": "create"','"type":"create","type":"create"')),
            ('nested',raw.replace('"fontWeight": 451','"fontWeight":451,"fontWeight":451'))]:
            self.reject('duplicate-'+name,valid,400,'invalidRequest',raw=body)
        for field in ('expectedRevision','metadata','command'):
            omitted=copy.deepcopy(payload);omitted.pop(field)
            self.reject('request-missing-'+field,valid,400,'invalidRequest',raw=json.dumps(omitted))
            null=copy.deepcopy(payload);null[field]=None
            self.reject('request-null-'+field,valid,400,'invalidRequest',raw=json.dumps(null))
        for value in ('2030-01-01','2030-01-01T00:00:00',None):
            changed=copy.deepcopy(payload);changed['metadata']['validUntilUtc']=value
            self.reject('metadata-zone-'+str(value),valid,400,'invalidRequest',raw=json.dumps(changed))
        for suffix in ('?unknown=1','?offset=-1','?limit=0','?limit=101','?limit=1&limit=2','?offset=0&offset=1','?expectedRevision=missing&expectedRevision=missing','?expectedRevision=sha256:'+'a'*64):
            self.request('strict-list-query','GET',LIBRARIES+'/personal/templates'+suffix,status=400,code='invalidRequest')
        for suffix in ('','?templateId=x','?expectedRevision=missing','?templateId=x&expectedRevision=missing&unknown=1','?templateId=x&templateId=x&expectedRevision=missing'):
            self.request('strict-detail-query','GET',LIBRARIES+'/personal/templates/detail'+suffix,status=400,code='invalidRequest')
        self.request('descriptor-unknown-query','GET',LIBRARIES+'?unknown=1',status=400,code='invalidRequest')

    def permissions_and_cas(self):
        before=self.snapshot('permission-before');disk=self.index_files()
        self.request('library-no-auth','GET',LIBRARIES,client=None,status=401,code='unauthorizedClient')
        self.request('library-bad-origin','GET',LIBRARIES,headers={'Origin':'http://untrusted.invalid'},status=403,code='unauthorizedClient')
        self.request('library-good-origin','GET',LIBRARIES,headers={'Origin':'http://localhost:5173'})
        self.reject('builtin-readonly',delete('builtin:line-classic'),403,'readOnlySession',library='builtin',expected=self.builtin_revision,field='libraryId')
        self.reject('unknown-library',create(definition()),404,'resourceNotFound',library='unknown',field='libraryId')
        self.reject('stale-destination',create(definition()),409,'revisionConflict',expected='missing',field='expectedRevision')
        self.reject('stale-source',duplicate(self.primary_identifier,'stale',revision='missing'),409,'revisionConflict',field='command.sourceRevision')
        self.request('stale-list','GET',LIBRARIES+'/personal/templates?expectedRevision=missing',status=409,code='revisionConflict')
        self.request('stale-detail','GET',LIBRARIES+'/personal/templates/detail?'+urlencode({'expectedRevision':'missing','templateId':self.primary_identifier}),status=409,code='revisionConflict')
        self.check(self.snapshot('permission-after')==before and self.index_files()==disk,'authorization/CAS rejects preserve exact library')

    def budgets(self):
        for label,value in [('name-ascii','x'*257),('name-utf16','🎵'*129)]:
            self.reject(label,rename(self.primary_identifier,value),413,'resourceTooLarge')
        for field,value in [('fontFamilyName','x'*257),('fallbackFamilyNames',['font'+str(i) for i in range(33)])]:
            candidate=definition('budget');candidate['style']['typography'][field]=value
            self.reject('budget-'+field,create(candidate),413,'resourceTooLarge')
        candidate=definition('gradient-budget');candidate['style']['sungFill']['material']=style.gradient(257)
        self.reject('budget-gradient257',create(candidate),413,'resourceTooLarge')
        self.mutate('boundary-name256',rename(self.primary_identifier,'x'*256))
        self.mutate('boundary-utf16-256',rename(self.primary_identifier,'🎵'*128))
        # Ensure large but permitted new values are exercised as accepted input.
        candidate=definition('boundary-fallback32');candidate['style']['typography']['fallbackFamilyNames']=['font'+str(i) for i in range(32)]
        candidate['style']['sungFill']['material']=style.gradient(256)
        self.mutate('boundary-gradient256-fallback32',replace(self.primary_identifier,candidate))

    def restart(self):
        before=self.snapshot('restart-before');disk=self.index_files()
        self.stop('library-restart');self.project=None
        self.start_library('library-restarted')
        self.check(self.snapshot('restart-after')==before and self.index_files()==disk,'restart retains exact complete persistent library/revision/order')
        self.discover('restart-discovery');self.snapshot('restart-second-client',client=self.second)

    def corrupt_library(self):
        before=self.snapshot('corrupt-before');original=self.index.read_bytes()
        for label,invalid in [('truncated',b'{broken'),('version',b'{"schemaVersion":999,"packages":[],"templates":[]}')]:
            self.index.write_bytes(invalid)
            try:
                self.discover('corrupt-'+label+'-discovery')
                self.snapshot('corrupt-'+label+'-builtin','builtin')
                self.request('corrupt-'+label+'-list','GET',LIBRARIES+'/personal/templates',status=422,code='commandRejected')
                payload=self.intent('corrupt-'+label,'missing',create(definition('must not overwrite')))
                self.request('corrupt-'+label+'-write','POST',LIBRARIES+'/personal/commands',value=payload,status=422,code='commandRejected')
                self.check(self.index.read_bytes()==invalid,'corrupt '+label+' never replaced by empty/new library')
            finally:self.index.write_bytes(original)
        self.check(self.snapshot('corrupt-restored')==before,'repair only restores original owned fixture bytes')

    def advanced_legacy(self):
        original=self.index.read_bytes();document=json.loads(original)
        info,package=self.owned_package(self.index.parent)
        target=self.index.parent/'packages'/info['revision'][7:]
        target.parent.mkdir(exist_ok=True)
        shutil.move(str(package),str(target))
        for item in self.inputs:
            if item.get('file')==str((package/'manifest.json').relative_to(self.output)):
                item['file']=str((target/'manifest.json').relative_to(self.output))
        closure={str(p.relative_to(self.index.parent)):hashlib.sha256(p.read_bytes()).hexdigest() for p in target.rglob('*') if p.is_file()}
        value=definition('旧高级 '+('x'*300),properties=['opacity'])
        persisted=style.persistence_baseline(value['style'])
        persisted['unsungFill']={'kind':'packageMaterial',**info,'parameters':{'highlight':0.12345678901234567,'opacity':.75}}
        legacy={'id':' /旧高级 🎵/ ','name':value['name'],'kind':'Line','properties':['opacity'],'style':persisted,'effectRecipes':[]}
        document['packages'].append({**{k:info[k] for k in ('packageId','revision')},'path':'packages/'+info['revision'][7:]})
        document['templates'].append(legacy)
        self.index.write_text(json.dumps(document,ensure_ascii=False,indent=2))
        before=self.snapshot('advanced-read',check_model=False)
        entry=before['details'][-1]
        self.check(entry['summary']['hasPackageMaterials'] and entry['summary']['hasPackages'] and entry['definition'] is None,'unselected legacy package slot makes complete definition advanced')
        self.models[legacy['id']]={'summary':entry['summary'],'definition':None}
        copied,_=self.mutate('advanced-copy-complete',duplicate(legacy['id'],'高级复制',revision=before['revision']))
        self.mutate('advanced-rename-preserves-closure',rename(legacy['id'],'高级改名'))
        self.reject('advanced-replace-rejected',replace(legacy['id'],definition('overwrite')),422,'commandRejected',failure='AdvancedDefinitionUnsupported',field='command')
        current=json.loads(self.index.read_text())
        for identifier in (legacy['id'],copied['templateId']):
            item=next(t for t in current['templates'] if t['id']==identifier)
            self.check(item['style']==legacy['style'] and item['effectRecipes']==legacy['effectRecipes'],'copy/rename retain all unselected raw legacy advanced values')
        self.mutate('advanced-delete-original',delete(legacy['id']))
        self.mutate('advanced-delete-copy',delete(copied['templateId']))
        self.check(all(hashlib.sha256((self.index.parent/path).read_bytes()).hexdigest()==digest for path,digest in closure.items()),'delete retains all original package closure files byte for byte')
        self.restart()

    def finish(self):
        self.stop('library-final')
        save_json(self.output/'http-elapsed.json',{'samples':self.samples,'scope':'local observations only; no performance claim'})
        save_json(self.output/'input-manifest.json',self.inputs)
        files={}
        for path in sorted(self.output.rglob('*')):
            if path.is_file():
                data=path.read_bytes();self.check(not any(secret.encode() in data for secret in self.secrets),'credential scan: '+str(path.relative_to(self.output)))
                files[str(path.relative_to(self.output))]=hashlib.sha256(data).hexdigest()
        save_json(self.output/'evidence-manifest.json',{'files':files,'deploymentManifestSha256':hashlib.sha256((self.dist/'deployment.json').read_bytes()).hexdigest()})
        summary={'status':'passed','httpRequests':self.request_count,'assertions':len(self.assertions),'assertionLabels':self.assertions,'runs':self.runs,
            'scope':'independent template library static/read/CAS/receipt/auth/strict/budgets/persistence/legacy advanced preservation; no package execution/browser/platform claim'}
        save_json(self.output/'summary.json',summary)
        print(json.dumps({k:summary[k] for k in ('status','httpRequests','assertions')}))

    def run(self):
        self.start_library('library');self.discover('library-discovery');self.builtin()
        self.static_commands();self.replay();self.races();self.strict()
        self.permissions_and_cas();self.budgets();self.corrupt_library();self.restart();self.advanced_legacy();self.finish()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args();output=args.output.resolve()
    if not output.is_relative_to(ROOT/'artifacts'):parser.error('--output must be a new directory beneath repository artifacts/')
    output.mkdir(parents=True,exist_ok=False)
    probe=LibraryProbe(output,args.dist.resolve())
    try:probe.run()
    finally:
        if probe.launcher is not None:probe.stop('library-failure-cleanup')


if __name__=='__main__':main()
