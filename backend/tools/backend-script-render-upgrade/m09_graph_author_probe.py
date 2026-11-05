#!/usr/bin/env python3
"""Finite M09 graph/Base/group HTTP acceptance. Does not run on import."""
import argparse
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import zipfile

sys.dont_write_bytecode = True
REPO = next(p for p in Path(__file__).resolve().parents if (p / 'backend/tools/backend-script-render-upgrade/m09_author_probe.py').is_file())
SPEC = importlib.util.spec_from_file_location('m09_existing', REPO / 'backend/tools/backend-script-render-upgrade/m09_author_probe.py')
m09 = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m09)
selection, save_json, sha = m09.selection, m09.save_json, m09.sha
B = REPO / 'backend'
GRAPH = 'graphs/probe.graph.json'
PROGRAM = 'shaders/probe.program.json'
SHADER = 'shaders/probe.sksl'


def raw_source(file, symbol):
    value = (B / 'tests/MakoKaraGenerator.Rendering.Tests' / file).read_text()
    match = re.search(r'private const string ' + re.escape(symbol) + r' = """\n(.*?)\n        """;', value, re.S)
    if not match:
        raise AssertionError('Refresh exact fixture raw string deliberately: ' + file + ':' + symbol)
    return '\n'.join(line[8:] if line.startswith('        ') else line for line in match.group(1).splitlines()) + '\n'


def output(id, target, kind='ImageGraph', requires=()):
    return dict(id=id, kind=kind, target=target, requires=list(requires), dependsOn=[])


def parameter(id, default, low=0, high=1):
    return dict(id=id, type='number', label=id, default=default, min=low, max=high)


def graph():
    capture = dict(includeRuby=True, includeStroke=True, includeShadow=True, includeDescendantGroups=True,
        includeOwnedOverlay=False, region='fillAndStroke')
    return dict(graphVersion=1, id='probe', inputs=[dict(id='original', source='TargetColor', type='color',
        availability='required', backgroundRequirement='none', space='canvas', capture=capture)], parameters=['amount'],
        nodes=[dict(id='probe', op='RuntimeShader', program=PROGRAM, children=dict(original='input:original'),
            uniforms=dict(amount='parameter:amount'), outputType='color', bounds=dict(expandPx=0, maxExpandPx=0, maxSampleOffsetPx=0), edge='transparent')],
        output='node:probe', anchorMode='ColorOnly', bounds=dict(space='canvas', source='targetRegion', maxExpandPx=0), composite=dict(mode='sourceOver'))


def common(name, target='line'):
    manifest = dict(**m09.profile(), packageId='mako.m09.http.' + name, name='M09 ' + name, version='2.0.0',
        requires=['image.graph'], lineExit=dict(durationUs=0), emitters=[], parameters=[parameter('amount', .5)], exports=[
            dict(id='tint', kind='graph', name='Tint', graph=GRAPH, parameters=['amount'], bindingKinds=['project', 'node'],
                outputs=[output('color', target)])])
    program = dict(programVersion=1, source=SHADER, profile='skia-raster-3.119.4-v1',
        uniforms=[dict(name='amount', type='float')], children=[dict(name='original', type='color')])
    files = {GRAPH: graph(), PROGRAM: program, SHADER: 'uniform float amount; uniform shader original; half4 main(float2 p) { half4 c=original.eval(p); return half4(c.rgb*amount,c.a); }'}
    roles = {GRAPH: 'graph', PROGRAM: 'shaderProgram', SHADER: 'shader'}
    return manifest, files, roles


BASE_SOURCE = '''using System.Collections.Generic;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
public sealed record BaseData(double Factor, double Stroke) : PreparedEffect;
public sealed class BaseEffect : IPureKaraokeEffect
{
    public PreparedEffect Declare(DeclareContext c) => new BaseData(c.Parameters.GetNumber("factor"), c.Parameters.GetNumber("stroke"));
    public void Evaluate(in TextFrame f, PreparedEffect data, TextOutput o)
    {
        var d=(BaseData)data; var handle=f.Resources.GetMaterial("ink");
        var parameters=new ParameterValues(new[] { KeyValuePair.Create<string,ParameterValue>("amount",new ParameterValue.Number(1)) });
        var material=MaterialValue.PackageExport(handle,parameters);
        foreach(var token in f.Geometry)
        {
            TextTarget family=new TextTarget.Family(token.WithRuby); TextTarget basic=new TextTarget.Base(token.BaseOnly);
            foreach(var state in new[] { FillState.Unsung, FillState.Sung })
            {
                o.SetFill(family,state,MaterialValue.Solid(new(0,0,1,1)));
                o.SetFill(basic,state,material);
                o.SetMaterialUniform(basic,state==FillState.Sung?MaterialSlot.SungFill:MaterialSlot.UnsungFill,handle,"probe","amount",UniformValue.Float(d.Factor));
            }
            if(d.Stroke>0) { o.SetStroke(basic,new StrokeValue(4,material)); o.SetMaterialUniform(basic,MaterialSlot.Stroke,handle,"probe","amount",UniformValue.Float(d.Factor)); }
        }
    }
}
'''

GROUP_SOURCE = '''using System.Collections.Generic;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
public sealed record GroupData(double Role, double Own, double Nested, double Amount) : PreparedEffect;
public sealed class GroupEffect : IPureKaraokeEffect, IPureVisualEffect
{
    public PreparedEffect Declare(DeclareContext c) => new GroupData(c.Parameters.GetNumber("role"),c.Parameters.GetNumber("own"),c.Parameters.GetNumber("nested"),c.Parameters.GetNumber("amount"))
        { VisualOutputs=[new(new("dot"),VisualPrimitiveKind.Dots,OverlayLayer.BehindText)] };
    public void Evaluate(in TextFrame f, PreparedEffect data, TextOutput o)
    {
        var d=(GroupData)data; if(d.Role>0) return;
        var root=f.Source.Scene.Programs[0].RootNodeId;
        foreach(var group in f.GroupGeometry)
        {
            if(d.Nested==0 && group.Target.NodeId!=root) continue;
            string graph=d.Nested>0?"tint":"below";
            var values=new ParameterValues(new[] { KeyValuePair.Create<string,ParameterValue>("amount",new ParameterValue.Number(d.Amount)) });
            o.BindGraph(new TextTarget.Group(group.Target),new GraphBindingValue(f.Resources.GetGraph(graph),values,GraphUniformValues.Empty,GraphTransformValues.Empty));
            if(d.Nested>0 && group.Target.NodeId!=root) o.MultiplyOpacity(new TextTarget.Group(group.Target),.8);
        }
    }
    public void EvaluateVisuals(in VisualFrame f, PreparedEffect data, VisualOutput o)
    {
        var d=(GroupData)data; if(d.Role==0 && d.Own==0) return;
        var color=d.Role>0?new ColorRgba(0,1,0,.5):new ColorRgba(0,0,1,1);
        o.DrawDots(new("dot"),[new(new(f.CanvasSize.X*30/640,f.CanvasSize.Y*20/360),f.CanvasSize.X*8/640,color)],OverlayLayer.BehindText);
    }
}
'''


def fixtures():
    # F1: no controller and no DLL. This is independent of author compilation.
    manifest, files, roles = common('direct-line')
    yield dict(name='direct-line', manifest=manifest, files=files, roles=roles, export='tint', params={'amount': .5}, control={'amount': 1})
    # F2: exact source from the admitted typed graph package; preserve uniforms and Transform.
    manifest, files, roles = common('typed-dynamic')
    source = raw_source('V2GraphControllerPackageTests.cs', 'Controller')
    source=source.replace('new Rect2(0, 0, 640, 360)', 'new Rect2(0, 0, frame.CanvasSize.X, frame.CanvasSize.Y)')
    files['src/GraphEffect.cs'] = source; roles['src/GraphEffect.cs'] = 'csharp'
    files[SHADER] = 'uniform shader original; uniform float amount; half4 main(float2 p) { return original.eval(p)*amount; }'
    dynamic = files.pop(GRAPH); roles.pop(GRAPH)
    dynamic['inputs'][0]['capture']['includeDescendantGroups'] = False
    dynamic['nodes'][0]['id'] = 'tint'
    dynamic['nodes'].append(dict(id='move', op='Transform', input='node:tint', matrix=[1,0,0,1,0,0], outputBounds=[0,0,640,360],
        bounds=dict(expandPx=0,maxExpandPx=0,maxSampleOffsetPx=32),edge='transparent'))
    dynamic['output'] = 'node:move'
    for id in ['a','b']:
        files['graphs/'+id+'.graph.json'] = copy.deepcopy(dynamic); roles['graphs/'+id+'.graph.json'] = 'graph'
    values = [('target',1,0,2),('writes',1,0,257),('bad',0,0,5),('clear',0,0,1),('value',.5,0,1),('vary',0,0,1),('catchFailure',0,0,1),('amount',1,0,1)]
    manifest['parameters'] = [parameter(*item) for item in values]
    outputs = [output('family','tokenFamily'),output('line','line'),output('mark','textMark')]
    kinds=['project','branch','lines','tokens','clips','node']
    manifest['exports'] = [dict(id='controller',kind='controller',name='Typed',entryType='GraphEffect',parameters=[x[0] for x in values[:-1]],bindingKinds=kinds,outputs=outputs)]
    for id in ['a','b','onlyFamily']:
        manifest['exports'].append(dict(id=id,kind='graph',name=id,graph='graphs/'+('a' if id=='onlyFamily' else id)+'.graph.json',parameters=['amount'],bindingKinds=kinds,
            outputs=[output('family','tokenFamily')] if id=='onlyFamily' else outputs))
    defaults={x[0]:x[1] for x in values[:-1]}
    yield dict(name='typed-dynamic',manifest=manifest,files=files,roles=roles,export='controller',params=defaults,control={**defaults,'value':1})
    # F3: real Base material + per-slot uniform; Family/Ruby remain solid blue in BOTH controls.
    manifest, files, roles = common('base-uniform','tokenFamily')
    manifest['requires']=['style.fill','style.stroke','material.uniform']
    # This fixture exposes only its controller and material, not the generic direct graph.
    manifest['exports']=[]
    material=files[GRAPH]
    material['inputs']=[dict(id='region',source='TargetRegionCoverage',type='coverage',availability='required',
        backgroundRequirement='none',space='canvas',capture=dict(includeRuby=False,includeStroke=True,includeShadow=False,
            includeDescendantGroups=False,includeOwnedOverlay=False,includeGlow=False,region='materialRegion'))]
    material['nodes'][0]['children']={'region':'input:region'}
    files[PROGRAM]['children']=[dict(name='region',type='coverage')]
    # Emit premultiplied color already bounded by the actual fill/stroke region.
    files[SHADER]='uniform float amount; uniform shader region; half4 main(float2 p) { half a=region.eval(p).a*(160.0/255.0); return half4(half3(51.0/255.0,128.0/255.0,204.0/255.0)*amount*a,a); }'
    manifest['parameters'] += [parameter('factor',.25),parameter('stroke',0)]
    manifest['exports'].insert(0,dict(id='controller',kind='controller',name='Base uniform',entryType='BaseEffect',parameters=['factor','stroke'],bindingKinds=['project','node'],
        outputs=[output('base','baseToken','StyleModulation'),output('family','tokenFamily','StyleModulation')]))
    manifest['exports'].append(dict(id='ink',kind='material',name='Ink',graph=GRAPH,parameters=['amount'],regions=['fill','stroke'],coverageMode='masked',
        fallback=dict(kind='solid',color='#3380CCA0'),outputs=[{**output('color','region'),'bypass':dict(kind='materialFallback',mapping='identity')}]))
    files['src/BaseEffect.cs']=BASE_SOURCE; roles['src/BaseEffect.cs']='csharp'
    yield dict(name='base-uniform',manifest=manifest,files=files,roles=roles,export='controller',params=dict(factor=.25,stroke=1),control=dict(factor=.75,stroke=1))
    # F4: same production package bytes support direct groups, nested controllers, and owned Below.
    manifest, files, roles = common('group-owned','group')
    manifest['requires']=['image.graph','image.graph.group','group.opacity','visuals.dots']
    manifest['exports'][0]['outputs'][0]['requires']=['image.graph.group']
    manifest['parameters'] += [parameter('role',0),parameter('own',1),parameter('nested',0)]
    source='src/GroupEffect.cs'; files[source]=GROUP_SOURCE; roles[source]='csharp'
    manifest['exports'].insert(0,dict(id='controller',kind='controller',name='Group Below',entryType='GroupEffect',parameters=['role','own','nested','amount'],bindingKinds=['project','node'],
        outputs=[output('group','group',requires=['image.graph.group']),output('opacity','group','StyleModulation'),output('dot','canvas','Overlay',requires=['visuals.dots'])]))
    below=copy.deepcopy(files[GRAPH]); below['id']='below'
    below['inputs'].append(dict(id='below',source='BackdropBelow',type='color',availability='required',backgroundRequirement='none',space='canvas'))
    below['nodes'][0].update(program='shaders/below.program.json',children=dict(original='input:original',below='input:below'),uniforms={},bounds=dict(expandPx=0,maxExpandPx=0,maxSampleOffsetPx=256))
    below['nodes'].append(dict(id='mask',op='RuntimeShader',program='shaders/mask.program.json',children={},uniforms={},outputType='coverage',bounds=dict(expandPx=0,maxExpandPx=0,maxSampleOffsetPx=0),edge='transparent'))
    below['composite']=dict(mode='replaceCoveredBackdrop',coverage='node:mask')
    region='p.x>=canvasSize.x*.4 && p.x<canvasSize.x*.41875 && p.y>=canvasSize.y/30 && p.y<canvasSize.y*7/90'
    below['nodes'][0]['uniforms']={'canvasSize':'host:canvasSize'}
    below['nodes'][1]['uniforms']={'canvasSize':'host:canvasSize'}
    files['graphs/below.graph.json']=below; roles['graphs/below.graph.json']='graph'
    files['shaders/below.sksl']='uniform float2 canvasSize; uniform shader original; uniform shader below; half4 main(float2 p) { return ('+region+') ? below.eval(float2(canvasSize.x*30/640+.5,canvasSize.y*20/360+.5)) : original.eval(p); }'
    files['shaders/mask.sksl']='uniform float2 canvasSize; half4 main(float2 p) { return half4(0,0,0,('+region+')?1:0); }'
    for id,children in [('below',[dict(name='original',type='color'),dict(name='below',type='color')]),('mask',[])]:
        path='shaders/'+id+'.program.json'
        files[path]=dict(programVersion=1,source='shaders/'+id+'.sksl',profile='skia-raster-3.119.4-v1',uniforms=[dict(name='canvasSize',type='float2')],children=children)
        roles[path]='shaderProgram'; roles['shaders/'+id+'.sksl']='shader'
    manifest['exports'].append(dict(id='below',kind='graph',name='Below',graph='graphs/below.graph.json',parameters=['amount'],bindingKinds=['project','node'],outputs=[output('color','group',requires=['image.graph.group'])]))
    params=dict(role=0,own=1,nested=0,amount=1)
    yield dict(name='group-owned',manifest=manifest,files=files,roles=roles,export='controller',params=params,control={**params,'own':0})
    yield dict(name='group-nested',manifest=manifest,files=files,roles=roles,export='controller',params={**params,'nested':1,'amount':.7},control={**params,'nested':1,'amount':1})
    # Truly resource-only direct variant: erase C# and controller and all unused visual requirements.
    direct=copy.deepcopy(manifest); direct['requires']=['image.graph','image.graph.group']; direct['exports']=[copy.deepcopy(manifest['exports'][1])]
    no_cs={p:v for p,v in files.items() if roles[p]!='csharp'}
    yield dict(name='group-direct',manifest=direct,files=no_cs,roles={p:roles[p] for p in no_cs},export='tint',params={'amount':.7},control={'amount':1})


def fixed_archive(spec):
    manifest=copy.deepcopy(spec['manifest'])
    files={p:(json.dumps(v,separators=(',',':'),ensure_ascii=False).encode() if isinstance(v,dict) else v.encode() if isinstance(v,str) else v) for p,v in spec['files'].items()}
    assert set(files)==set(spec['roles'])
    manifest['files']=[dict(path=p,role=spec['roles'][p],sha256=sha(data)) for p,data in sorted(files.items())]
    output=io.BytesIO()
    with zipfile.ZipFile(output,'w',zipfile.ZIP_DEFLATED) as z:
        for p,data in [('manifest.json',json.dumps(manifest,separators=(',',':'),ensure_ascii=False).encode()),*sorted(files.items())]:
            entry=zipfile.ZipInfo(p,(2026,11,5,0,0,0)); entry.compress_type=zipfile.ZIP_DEFLATED; z.writestr(entry,data)
    return manifest,files,output.getvalue()


class GraphM09Probe(m09.M09AuthorProbe):
    def __init__(self,*args):
        super().__init__(*args)
        self.case=None
        self.previews_by_case={}

    def configure(self,label,draft,parameters):
        # The server canonicalizes parameter object keys. Keep the inherited
        # complete configuration equality check with canonical request ordering.
        return super().configure(label,draft,dict(sorted(parameters.items())))

    def request(self,name,method,path,**kwargs):
        value=kwargs.get('value')
        # Supply the second independent author instance without modifying the shared template helper.
        if self.case and self.case['name']=='group-owned' and method=='POST' and path=='/api/v1/template-libraries/personal/commands':
            command=(value or {}).get('command',{})
            if command.get('type')=='create':
                definition=command['definition']; original=definition['effectRecipes'][0]
                other=copy.deepcopy(original); other['recipeId']+='-other'; other['name']='other green owner'
                params=json.loads(other['parametersJson']); params['role']=1
                other['parametersJson']=json.dumps(params,separators=(',',':'))
                definition['effectRecipes']=[other,original]
        return super().request(name,method,path,**kwargs)

    def import_case(self,spec):
        name=spec['name']; manifest,files,data=fixed_archive(spec)
        (self.output/(name+'-fixture.mkfx')).write_bytes(data)
        asset=self.upload(name+'-upload',name+'.mkfx','application/octet-stream',data)
        inspected=self.request(name+'-inspect','POST','/api/v1/effect-packages/inspect',value={'packageAssetId':asset['assetId']})
        self.check(inspected['structureValid'],name+': actual fixed archive structurally admitted')
        draft=self.request(name+'-draft','POST','/api/v1/drafts',status=201,value={'packageAssetId':asset['assetId'],'exportId':spec['export'],'name':name})
        has_cs=any(item['role']=='csharp' for item in manifest['files'])
        self.check(draft['canCompile']==has_cs,name+': canCompile matches real C# membership; no dummy DLL')
        self.check(draft['packageVersion']==3 and draft['sdkVersion'].split('.')[0]=='2',name+': exact v3 SDK2')
        self.check(draft['currentSourceRevision']==inspected['packageRevision'],name+': inspected immutable source retained')
        self.fixtures.append(dict(name=name,manifest=manifest,archiveSha256=sha(data),sourceRevision=draft['currentSourceRevision'],files={p:sha(v) for p,v in files.items()}))
        save_json(self.output/'fixtures.json',self.fixtures)
        return self.record(name+'-imported',draft)

    def author_case(self,spec,draft):
        name=spec['name']; package=self.package_read(name+'-read',draft)
        text_files=[dict(path=x['path'],text=x['text']) for x in package['files'] if x['editable']]
        shader=next(x for x in text_files if x['path']==SHADER)
        shader['text']+='\n// M09 authored exact graph revision\n'
        before=draft
        draft=self.record(name+'-source-save',self.save_files(name+'-source-save',draft,package,text_files=text_files))
        self.assert_in_use(name+'-saved-not-adopted',before,draft)
        source=draft['currentSourceRevision']
        self.check(source!=before['currentSourceRevision'],name+': true shader source revision changed')
        declaration=self.request(name+'-exact-inspection','GET',self.path(draft)+'/source-inspection?'+m09.urlencode(dict(expectedDraftRevision=draft['draftRevision'],sourceRevision=source)))
        self.check(declaration['draftRevision']==draft['draftRevision'] and declaration['sourceRevision']==source and declaration['packageRevision']==source,
            name+': inspection pins CAS and exact requested source')
        self.check(any(x['id']==spec['export'] for x in declaration['exports']),name+': selected export belongs to source inspection')
        if draft['canCompile']:
            self.compile_source(name+'-compile',draft,source,asynchronous=True)
            draft=self.refreshed(name+'-compiled',draft)
        configuration={key:draft['configuration'].get(key) for key in ['name','exportId','parametersJson','relativeTarget','readScope','branchInput','fixedSeed','overlayLayer']}
        configuration.update(name='M09 '+name,exportId=spec['export'],parametersJson=json.dumps(spec['params'],separators=(',',':')),relativeTarget='application',readScope='boundSources',branchInput=None,fixedSeed='17')
        self.request(name+'-atomic-adopt','POST',self.path(draft)+'/adopt-source-revision',value=dict(expectedDraftRevision=draft['draftRevision'],sourceRevision=source,configuration=configuration,metadata=self.metadata(name+'-atomic-adopt')))
        draft=self.refreshed(name+'-adopted',draft)
        self.check(draft['configuration']['sourceRevision']==source and draft['configuration']['exportId']==spec['export']
            and json.loads(draft['configuration']['parametersJson'])==spec['params'],name+': exact source/export/complete configuration adopted atomically')
        return draft

    def sample(self,label,draft):
        preview=self.create_preview(label,draft)
        first=self.transparent_frame(label+'-first',preview,1_250_000,1)
        same=self.transparent_frame(label+'-same',preview,1_250_000,2)
        self.transparent_frame(label+'-away',preview,1_750_000,3)
        returned=self.transparent_frame(label+'-return',preview,1_250_000,4)
        self.check(first==same==returned,label+': full same-time/seek-return RGBA deterministic')
        self.close(label+'-close',preview); self.require_workers_drained(label+'-drain')
        return first

    def project_frame(self,label):
        source=dict(kind='project',sourceId=self.project,sourceRevision=self.revision)
        request=dict(source=source,size=dict(width=320,height=180),alphaMode='straight',purpose='preview',previewKind='project',committable=True,renderBackend='gpu')
        preview=self.request(label+'-create','POST','/api/v1/preview-sessions',status=201,value=request)
        self.open_previews.append((self.client,preview))
        self.check(preview['source']==source,label+': exact project source accepted')
        rgba=self.transparent_frame(label,preview,1_250_000,1)
        repeat=self.transparent_frame(label+'-repeat',preview,1_250_000,2)
        self.check(rgba==repeat,label+': project same-time RGBA deterministic')
        self.close(label+'-close',preview); self.require_workers_drained(label+'-drain')
        return rgba

    def save_reopen(self,label):
        handle=self.request(label+'-handle','POST','/api/v1/file-handles',status=201,value=dict(rootGrantId='projects',relativePath=label))['handleId']
        revision=self.revision
        saved=self.long_job(label+'-save',self.request(label+'-save','POST',self.project_path+'/save-jobs',status=202,value=dict(expectedRevision=revision,targetHandleId=handle)))
        self.check(saved['savedProjectRevision']==revision,label+': fixed input saved terminal')
        self.close_project(label+'-close')
        opened=self.long_job(label+'-open',self.request(label+'-open','POST','/api/v1/project-sessions/open-jobs',status=202,value=dict(projectDirectoryHandleId=handle)))['result']
        self.project,self.revision=opened['projectSessionId'],opened['projectRevision']; self.projects[self.project]=self.revision

    def project_for_export(self,name,draft):
        # Retain the existing managed-image/save/reopen workflow, then add real Ruby and groups via HTTP.
        image=super().project_for_export(name,draft)
        lines=self.lyrics(name+'-ruby-lines')['lines']; line=lines[0]; token=line['tokens'][0]
        self.send(name+'-ruby',dict(type='lyrics.setRubyText',target=dict(branchId='primary',lineId=line['id'],tokenId=token['id']),text='xy'))
        if self.case['name'].startswith('group-'):
            tree=self.request(name+'-tree','GET',self.project_path+'/orchestration?expectedRevision='+self.revision)
            program=next(x for x in tree['programs'] if x['branchId']=='primary')
            parent=self.send(name+'-parent',dict(type='orchestration.createNode',kind='group',parentId=program['rootContainerId'],name='M09 parent',slotCount=None))['structureResult']['nodeId']
            child=self.send(name+'-child',dict(type='orchestration.createNode',kind='group',parentId=parent,name='M09 child',slotCount=None))['structureResult']['nodeId']
            self.send(name+'-reparent',dict(type='orchestration.reparent',nodeId=program['defaultContainerId'],parentId=child))
            final=self.request(name+'-nested-tree','GET',self.project_path+'/orchestration?expectedRevision='+self.revision)
            nodes={x['id']:x for x in final['nodes']}
            self.check(nodes[child]['parentId']==parent and nodes[program['defaultContainerId']]['parentId']==child,name+': real HTTP parent/child source tree contains lyrics')
        before=self.project_frame(name+'-before-reopen')
        self.save_reopen(name+'-final')
        after=self.project_frame(name+'-after-reopen')
        self.check(before==after,name+': complete Ruby/group/effect pixels survive actual save/reopen')
        self.previews_by_case[name]=after
        if self.case['name']=='group-owned': self.owned_block(name+'-PNG-owned',after,(0,255,0,128))
        return image

    def owned_block(self,label,rgba,expected,tolerance=2):
        # Ratio coordinates make the same remote stripe visible in author and project inputs.
        width=320
        locations=[(x,y) for y in range(8,12) for x in range(130,133)]
        pixels=[rgba[(y*width+x)*4:(y*width+x)*4+4] for x,y in locations]
        self.check(all(abs(p[c]-expected[c])<=tolerance for p in pixels for c in range(4)),label+': remote Below stripe matches independent RGBA '+str(expected))

    def image_export(self,name,draft,svg=False):
        self.project_for_export(name,draft)
        png=self.previews_by_case[name]
        accepted=self.request(name+'-export','POST',self.project_path+'/export-jobs',status=202,value=dict(expectedRevision=self.revision,format='transparentProRes4444',includeAudio=False,quality='standard',renderBackend='gpu'))
        self.check(accepted['input']['sourceRevision']==self.revision and accepted['input']['sourceId']==self.project
            and accepted['input']['renderBackend']=='gpu' and accepted['exportInput']['renderBackend']=='gpu',name+': accepted exact project/GPU export input')
        terminal=selection.SelectionProbe.long_job(self,name+'-export',accepted); self.pending_jobs.pop(accepted['jobId'],None)
        self.check(terminal['input']==accepted['input'] and terminal['exportInput']==accepted['exportInput'],name+': terminal exact accepted inputs')
        report=self.report(name+'-export',terminal,'export'); reference=terminal['exportResult']['artifact']
        _,media,_=self.fetch(name+'-media','/api/v1/jobs/'+terminal['jobId']+'/export-content')
        self.check(sha(media)==reference['sha256']==report['sha256'] and len(media)==reference['sizeBytes']==report['mediaSize'],name+': complete downloadable artifact identity')
        path=self.output/(name+'-transparent.mov'); path.write_bytes(media)
        decoded=subprocess.run(['ffmpeg','-v','error','-i',str(path),'-f','rawvideo','-pix_fmt','rgba','pipe:1'],capture_output=True,timeout=90)
        count=320*180*4
        self.check(report['expectedFrames']==48 and report['width']==320 and report['height']==180 and report['hasAlpha']
            and decoded.returncode==0 and len(decoded.stdout)==48*count,name+': all 48 transparent export frames fully decode')
        self.check(min(decoded.stdout[3::4])==0 and max(decoded.stdout[3::4])>0,name+': actual transparent and visible video pixels')
        sampled=decoded.stdout[30*count:31*count]
        # Compare stable opaque interiors only; never use codec tolerance for PNG edges.
        stable=[]
        for y in range(2,178):
            for x in range(2,318):
                offset=(y*320+x)*4; p=png[offset:offset+4]
                if p[3]<250: continue
                neighbours=[png[((y+dy)*320+x+dx)*4:((y+dy)*320+x+dx)*4+4] for dy in [-1,0,1] for dx in [-1,0,1]]
                if all(abs(q[c]-p[c])<=2 for q in neighbours for c in range(4)): stable.append(offset)
        if not stable:
            for y in range(2,178):
                for x in range(2,318):
                    i=(y*320+x)*4; p=png[i:i+4]
                    if p[3]<100: continue
                    neighbours=[png[((y+dy)*320+x+dx)*4:((y+dy)*320+x+dx)*4+4] for dy in [-1,0,1] for dx in [-1,0,1]]
                    if all(abs(q[c]-p[c])<=2 for q in neighbours for c in range(4)): stable.append(i)
        self.check(len(stable)>10,name+': independent PNG has enough stable comparison pixels')
        self.check(all(abs(png[i+c]-sampled[i+c])<=12 for i in stable for c in range(4)),name+': decoded ProRes interiors match exact-source PNG within separately recorded 12/255')
        if self.case['name']=='group-owned': self.owned_block(name+'-video-owned',sampled,(0,255,0,128),12)
        result=dict(report=report,terminal=terminal,mediaSha256=sha(media),sampleRgbaSha256=sha(sampled),pngRgbaSha256=sha(png),stableInteriorCount=len(stable),codecInteriorTolerance=12)
        save_json(self.output/(name+'-export-pixels.json'),self.redact(result))
        self.close_project(name+'-close-project'); self.require_workers_drained(name+'-export-drain')
        return result

    def compare_controls(self,name,original,baseline):
        self.check(sum(original[i:i+4]!=baseline[i:i+4] for i in range(0,len(original),4))>10,
            name+': same-time real parameter control changes actual pixels')
        if name=='base-uniform':
            # The 4px Base stroke overlaps the lowest Ruby antialiasing pixels.
            # Pure Family blue selects only unoccluded Ruby, independently of
            # the Base material (whose maximum straight blue is 204/255).
            ruby=[i for i in range(0,len(original),4) if original[i+3]>150 and original[i:i+3]==bytes((0,0,255))]
            self.check(len(ruby)>20,name+': independent unoccluded Family-blue Ruby pixels actually visible')
            self.check(all(original[i:i+4]==baseline[i:i+4] for i in ruby),name+': Base uniform control leaves unoccluded Ruby complete RGBA unchanged')
        if name in ('direct-line','group-direct'):
            # The shader changes RGB only, leaving alpha untouched. This distinguishes actual graph tint from opacity.
            self.check(original[3::4]==baseline[3::4],name+': graph RGB parameter changes preserve every alpha pixel')

    def run(self,include_warp=False,include_export=True):
        deployment=selection.host.validate_dist(self.dist)
        self.deployment={key:deployment.get(key) for key in ['version','sourceCommit','sourceDirty','rid','selfContained']}
        save_json(self.output/'deployment-reference.json',{**self.deployment,'manifestSha256':sha((self.dist/'deployment.json').read_bytes())})
        self.start('m09-graph-start'); self.connect('m09-graph')
        self.check(self.current_ready['version']==deployment['version'],'matched graph deployment ready')
        available=self.request('graph-backends','GET','/api/v1/render-backends')
        gpu=next(x for x in available['backends'] if x['renderBackend']=='gpu')
        self.check(gpu['available'] and gpu['api']=='vulkan' and bool(gpu.get('device')),'actual Vulkan hardware offered')
        for spec in fixtures():
            self.case=spec; name=spec['name']; self.stage=name+':author'
            draft=self.author_case(spec,self.import_case(spec))
            original=self.sample(name+'-authored',draft)
            control=self.configure(name+'-control-config',draft,spec['control'])
            baseline=self.sample(name+'-control',control)
            if name=='group-owned':
                self.owned_block(name+'-author-owned',original,(0,0,0,0))
                self.owned_block(name+'-author-control',baseline,(0,0,0,0))
            self.compare_controls(name,original,baseline)
            restored=self.configure(name+'-restore',control,spec['params'])
            result=dict(name=name,draft=restored,authorRgbaSha256=sha(original),controlRgbaSha256=sha(baseline),export=None)
            if include_export:
                self.stage=name+':project-export'
                if name=='group-owned':
                    controlled=self.configure(name+'-project-own-off',restored,spec['control'])
                    self.project_for_export(name+'-own-off',controlled)
                    without=self.previews_by_case[name+'-own-off']
                    self.close_project(name+'-own-off-project-close'); self.require_workers_drained(name+'-own-off-drain')
                    restored=self.configure(name+'-project-own-on',controlled,spec['params'])
                    result['export']=self.image_export(name,restored)
                    with_own=self.previews_by_case[name]
                    self.check(without!=with_own,name+': actual own BehindText changes screen pixels')
                    self.owned_block(name+'-own-off-remote',without,(0,255,0,128))
                    self.owned_block(name+'-own-on-remote',with_own,(0,255,0,128))
                else:
                    result['export']=self.image_export(name,restored)
            self.m09_runs.append(result); save_json(self.output/'m09-runs.json',self.redact(self.m09_runs))
        self.stage='complete'; self.require_workers_drained('m09-graph-all-workers')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist',type=Path,required=True); parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--icd',type=Path,default=Path('/usr/share/vulkan/icd.d/radeon_icd.json'))
    parser.add_argument('--skip-export',action='store_true')
    args=parser.parse_args(); icd=selection.gpu.hardware_icd(args.icd); output=args.output.resolve(); output.mkdir(parents=True,exist_ok=False)
    os.environ['MAKO_RENDER_BACKEND']='cpu'; os.environ['VK_DRIVER_FILES']=os.environ['VK_ICD_FILENAMES']=icd['path']
    probe=GraphM09Probe(output,args.dist.resolve(),icd); failure=None
    save_json(output/'environment.json',dict(defaultBackend='cpu',scenarioBackend='gpu',icd=icd,includeExport=not args.skip_export))
    try: probe.run(include_export=not args.skip_export)
    except BaseException as error:
        failure=error; save_json(output/'failure.json',dict(stage=probe.stage,errorType=type(error).__name__,detail=probe.safe_text(error),lastRequest=probe.last_request))
    finally:
        try: probe.drain()
        except BaseException as error:
            failure=failure or error; save_json(output/'shutdown-failure.json',dict(errorType=type(error).__name__,detail=probe.safe_text(error)))
    with probe.gpu_lock: hardware=list(probe.gpu_samples.values())
    save_json(output/'gpu-workers.json',dict(observations=hardware,assignments=probe.gpu_assignments,exportHardware=probe.task_hardware))
    scanned=None
    try: scanned=probe.scan_credentials()
    except BaseException as error:
        failure=failure or error; save_json(output/'scan-failure.json',dict(errorType=type(error).__name__,detail=probe.safe_text(error)))
    summary=dict(status='failed' if failure else 'passed',errorType=type(failure).__name__ if failure else None,httpRequests=probe.request_count,
        assertions=len(probe.assertions),assertionLabels=probe.assertions,deployment=probe.deployment,jobTerminals=probe.terminals,
        fixtures=probe.fixtures,m09Runs=probe.redact(probe.m09_runs),authorFrames=probe.author_frames,credentialScannedFiles=scanned,
        workerIdentities=[dict(pid=pid,startTicks=ticks) for pid,ticks in sorted(probe.worker_identities)],probeSha256=sha(Path(__file__).read_bytes()),
        scope='Finite M09 direct/typed graph, Base uniform, real group direct/nested/owned Below; exact source adoption, hardware Vulkan PNG controls, managed Image project save/reopen and optional transparent ProRes export. No M10 media-feature analysis, M11 named samples, UI, cross-platform or performance claim.')
    save_json(output/'summary.json',summary)
    save_json(output/'evidence-manifest.json',dict(files={str(p.relative_to(output)):sha(p.read_bytes()) for p in sorted(output.rglob('*')) if p.is_file()}))
    print(json.dumps({key:summary[key] for key in ['status','httpRequests','assertions','errorType']}))
    return int(failure is not None)


if __name__=='__main__': sys.exit(main())
