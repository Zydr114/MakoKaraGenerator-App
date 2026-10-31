#!/usr/bin/env python3
"""Create auditable probe-owned Worker variants without changing the canonical publish."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile

ROOT=Path(__file__).resolve().parents[2]
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def inventory(root):return {str(p.relative_to(root)):digest(p) for p in sorted(root.rglob('*')) if p.is_file()}
def save(path,value):path.write_text(json.dumps(value,indent=2)+'\n')


def historical(args):
    output=args.output.resolve();output.mkdir(parents=True,exist_ok=False)
    commit=subprocess.check_output(['git','rev-parse',args.commit],cwd=ROOT,text=True).strip()
    archive=subprocess.check_output(['git','archive',commit],cwd=ROOT)
    source=output/'source';source.mkdir()
    with tarfile.open(fileobj=io.BytesIO(archive)) as bundle:bundle.extractall(source,filter='data')
    before=inventory(source)
    command=['flock','-o','/tmp/mkg-ttml-dotnet.lock','dotnet','publish',str(source/'src/MakoKaraGenerator.Backend.Worker/MakoKaraGenerator.Backend.Worker.csproj'),
        '-c','Release','-r','linux-x64','--self-contained','true','-m:1','-p:UseSharedCompilation=false','-o',str(output/'worker')]
    if args.version_override:command.append('-p:VersionPrefix='+args.version_override)
    with (output/'publish.log').open('w') as log:result=subprocess.run(command,cwd=source,stdout=log,stderr=subprocess.STDOUT)
    assert all(digest(source/name)==value for name,value in before.items()),'Historical tracked source mutated during build'
    report={'kind':'historical-source-rebuilt-version-variant' if args.version_override else 'historical-source-rebuilt-original-version',
        'sourceCommit':commit,'archiveSha256':hashlib.sha256(archive).hexdigest(),'sourceInputs':before,'versionOverride':args.version_override,
        'command':command,'exitCode':result.returncode,'workerFiles':inventory(output/'worker') if (output/'worker').exists() else {},
        'scope':'Real historical production source rebuilt by SDK; version override is explicit compiler input, not an unmodified historical binary.'}
    save(output/'historical-build.json',report)
    return result.returncode


def deployment(args):
    base=args.dist.resolve();output=args.output.resolve()
    original=json.loads((base/'deployment.json').read_text())
    for name,value in original['files'].items():assert digest(base/name)==value,name
    shutil.copytree(base,output,copy_function=os.link)
    if args.kind=='missing-refs':
        shutil.rmtree(output/'worker/script-refs')
    else:
        if args.worker is None:raise ValueError('Historical variant requires --worker')
        shutil.rmtree(output/'worker');shutil.copytree(args.worker.resolve(),output/'worker')
    files=inventory(output);files.pop('deployment.json',None)
    delta={name:{'canonical':original['files'].get(name),'variant':files.get(name)} for name in sorted(set(original['files'])|set(files)) if original['files'].get(name)!=files.get(name)}
    # Unlink the hard-linked original manifest before writing our own explicit variant manifest.
    (output/'deployment.json').unlink()
    manifest={**original,'files':files,'probeVariant':{'kind':args.kind,'canonicalManifestSha256':digest(base/'deployment.json'),'workerSourceEvidence':str(args.worker.resolve().parent/'historical-build.json') if args.worker else None,'fileChanges':delta}}
    save(output/'deployment.json',manifest)
    print(json.dumps({'kind':args.kind,'fileChanges':len(delta),'manifestSha256':digest(output/'deployment.json')}))
    return 0

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);sub=parser.add_subparsers(dest='command',required=True)
    old=sub.add_parser('historical');old.add_argument('--commit',default='15246253987168aa5870ed0660721282fbf8e086');old.add_argument('--output',type=Path,required=True);old.add_argument('--version-override')
    variant=sub.add_parser('deployment');variant.add_argument('--dist',type=Path,required=True);variant.add_argument('--output',type=Path,required=True);variant.add_argument('--kind',choices=('missing-refs','historical-original-version','historical-current-version'),required=True);variant.add_argument('--worker',type=Path)
    args=parser.parse_args();raise SystemExit(historical(args) if args.command=='historical' else deployment(args))
