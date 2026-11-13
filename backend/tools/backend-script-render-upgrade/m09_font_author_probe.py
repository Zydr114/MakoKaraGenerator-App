#!/usr/bin/env python3
"""Finite v3 fixed-font/Ruby author-to-export acceptance using the M09 graph helpers."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys

sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location('m09_graph', Path(__file__).with_name('m09_graph_author_probe.py'))
g = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(g)
FAMILY = 'M09X Sans CJK SC'


def fixture(font_path):
    original = font_path.read_bytes()
    old, new = 'Noto'.encode('utf-16-be'), 'M09X'.encode('utf-16-be')
    if old not in original:
        raise AssertionError('The controlled Noto TTC fixture must contain its original family marker')
    font = original.replace(old, new)
    manifest, files, roles = g.common('fixed-font')
    files['fonts/fixed-cjk.ttc'] = font
    roles['fonts/fixed-cjk.ttc'] = 'font'
    return dict(name='fixed-font', manifest=manifest, files=files, roles=roles, export='tint',
        params={'amount': .5}, control={'amount': 1}), dict(sourceSha256=g.sha(original),
        fixedSha256=g.sha(font), byteLength=len(font), family=FAMILY)


class FontProbe(g.GraphM09Probe):
    def request(self,name,method,path,**kwargs):
        value=kwargs.get('value')
        if method=='POST' and path=='/api/v1/template-libraries/personal/commands':
            command=(value or {}).get('command',{})
            if command.get('type')=='create':
                # 合同 1.50.0 起模板属性用 43 项属性路径（typography.*、unsung.*、sung.*、wipe.*）。
                command['definition']['properties']=['typography.fontFamilyName','typography.fontSizePx']
                command['definition']['style']['typography'].update(fontFamilyName=FAMILY,fontSizePx=42)
        return super().request(name,method,path,**kwargs)

    def upload(self,name,filename,mime,data):
        if name.endswith('-lyrics'):
            data='[00:00.00]歌词测试\n'.encode()
        return super().upload(name,filename,mime,data)

    def send(self,name,command):
        if command.get('type')=='lyrics.setRubyText':
            command={**command,'text':'かし'}
        return super().send(name,command)

    def run_font(self,spec,font):
        deployment=g.selection.host.validate_dist(self.dist)
        self.deployment={k:deployment.get(k) for k in ['version','sourceCommit','sourceDirty','rid','selfContained']}
        g.save_json(self.output/'deployment-reference.json',{**self.deployment,'manifestSha256':g.sha((self.dist/'deployment.json').read_bytes())})
        g.save_json(self.output/'fixed-font.json',font)
        self.start('m09-font-start'); self.connect('m09-font')
        self.check(self.current_ready['version']==deployment['version'],'matched fixed-font deployment ready')
        backends=self.request('font-backends','GET','/api/v1/render-backends')
        gpu=next(x for x in backends['backends'] if x['renderBackend']=='gpu')
        self.check(gpu['available'] and gpu['api']=='vulkan' and bool(gpu.get('device')),'fixed font actual Vulkan offered')
        self.case=spec; name=spec['name']; self.stage='fixed-font:author'
        draft=self.author_case(spec,self.import_case(spec))
        package=self.package_read('font-readonly',draft)
        fixed=next(x for x in package['files'] if x['path']=='fonts/fixed-cjk.ttc')
        self.check(not fixed['editable'] and fixed['sha256']==font['fixedSha256'],'font binary remains exact and read-only after authored shader save')
        original=self.sample(name+'-authored',draft)
        control=self.configure(name+'-control-config',draft,spec['control'])
        baseline=self.sample(name+'-control',control)
        self.compare_controls(name,original,baseline)
        draft=self.configure(name+'-restore',control,spec['params'])
        self.stage='fixed-font:project-export'
        result=self.image_export(name,draft)
        self.m09_runs.append(dict(name=name,draft=draft,export=result))
        self.stage='complete'; self.require_workers_drained('m09-font-all-workers')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist',type=Path,required=True); parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--font',type=Path,default=Path('/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc'))
    parser.add_argument('--icd',type=Path,default=Path('/usr/share/vulkan/icd.d/radeon_icd.json'))
    args=parser.parse_args(); spec,font=fixture(args.font); icd=g.selection.gpu.hardware_icd(args.icd)
    output=args.output.resolve(); output.mkdir(parents=True,exist_ok=False)
    os.environ['MAKO_RENDER_BACKEND']='cpu'; os.environ['VK_DRIVER_FILES']=os.environ['VK_ICD_FILENAMES']=icd['path']
    probe=FontProbe(output,args.dist.resolve(),icd); failure=None
    g.save_json(output/'environment.json',dict(defaultBackend='cpu',scenarioBackend='gpu',icd=icd))
    try: probe.run_font(spec,font)
    except BaseException as error:
        failure=error; g.save_json(output/'failure.json',dict(stage=probe.stage,errorType=type(error).__name__,detail=probe.safe_text(error),lastRequest=probe.last_request))
    finally:
        try: probe.drain()
        except BaseException as error:
            failure=failure or error; g.save_json(output/'shutdown-failure.json',dict(errorType=type(error).__name__,detail=probe.safe_text(error)))
    with probe.gpu_lock: hardware=list(probe.gpu_samples.values())
    g.save_json(output/'gpu-workers.json',dict(observations=hardware,assignments=probe.gpu_assignments,exportHardware=probe.task_hardware))
    scanned=None
    try: scanned=probe.scan_credentials()
    except BaseException as error:
        failure=failure or error; g.save_json(output/'scan-failure.json',dict(errorType=type(error).__name__,detail=probe.safe_text(error)))
    summary=dict(status='failed' if failure else 'passed',errorType=type(failure).__name__ if failure else None,
        httpRequests=probe.request_count,assertions=len(probe.assertions),assertionLabels=probe.assertions,
        deployment=probe.deployment,font=font,jobTerminals=probe.terminals,fixtures=probe.fixtures,
        m09Runs=probe.redact(probe.m09_runs),authorFrames=probe.author_frames,credentialScannedFiles=scanned,
        probeSha256=g.sha(Path(__file__).read_bytes()),helperSha256=g.sha(Path(g.__file__).read_bytes()),
        scope='One real v3 role:font closure, read-only binary preservation, fixed-font CJK/Ruby project, hardware Vulkan save/reopen and 48-frame transparent export. Glyph family and independent CPU oracle are separate Runtime tests; no performance or platform claim.')
    g.save_json(output/'summary.json',summary)
    g.save_json(output/'evidence-manifest.json',dict(files={str(p.relative_to(output)):g.sha(p.read_bytes()) for p in sorted(output.rglob('*')) if p.is_file()}))
    print(json.dumps({k:summary[k] for k in ['status','httpRequests','assertions','errorType']}))
    return int(failure is not None)


if __name__=='__main__': sys.exit(main())
