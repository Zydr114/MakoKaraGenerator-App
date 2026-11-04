#!/usr/bin/env python3
"""Build the original-size video oracle against one immutable deployment."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--kind', required=True, choices=('video',))
    args = parser.parse_args()
    dist, output = args.dist.resolve(), args.output.resolve()
    deployment = json.loads((dist / 'deployment.json').read_text())
    if deployment['sourceDirty'] or deployment['rid'] != 'linux-x64' or not deployment['selfContained']:
        raise ValueError('requires clean self-contained linux-x64 deployment')
    for name, expected in deployment['files'].items():
        if digest(dist / name) != expected:
            raise ValueError('deployment mismatch: ' + name)
    worker, server = dist / 'worker', dist / 'server'
    sources = [Path(__file__).resolve().with_name('VideoOracle.cs')]
    references = [p for p in sorted(worker.glob('*.dll')) if p.name.startswith('MakoKaraGenerator.')
                  or p.name in ('SkiaSharp.dll', 'CliWrap.dll')]
    for name in ('MakoKaraGenerator.Scripting.Compiler.dll', 'Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll'):
        if (worker / name).exists() or not (server / name).is_file():
            raise ValueError('unexpected Worker/Server compiler dependency: ' + name)
        references.append(server / name)
    output.mkdir(parents=True, exist_ok=False)
    project, publish = output / 'RuntimeOracle.csproj', output / 'publish'
    items = ['<Compile Include="' + escape(str(source)) + '" />' for source in sources]
    items += ['<Reference Include="' + escape(p.stem) + '"><HintPath>' + escape(str(p)) + '</HintPath><Private>true</Private></Reference>' for p in references]
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
        '<OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
        '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>RuntimeOracle</AssemblyName>'
        '</PropertyGroup><ItemGroup>' + '\n'.join(items) + '</ItemGroup></Project>\n')
    command = ['flock', '-o', '/tmp/mkg-backend-b4-dotnet.lock', 'dotnet', 'publish', str(project), '-c', 'Release',
        '-r', 'linux-x64', '--self-contained', 'true', '-m:1', '/p:UseSharedCompilation=false', '-o', str(publish)]
    with (output / 'publish.log').open('w') as log:
        subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True)
    # Pin the full runtime dependency set, not only the assemblies compiled against.
    for path in sorted(worker.glob('*.dll')):
        shutil.copy2(path, publish / path.name)
    shutil.copytree(worker / 'script-refs', publish / 'script-refs')
    for path in sorted(worker.rglob('*.so')):
        target = publish / path.relative_to(worker)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    for path in references:
        if digest(publish / path.name) != digest(path):
            raise ValueError('oracle dependency mismatch: ' + path.name)
    for path in worker.glob('*.dll'):
        if digest(publish / path.name) != digest(path):
            raise ValueError('oracle runtime mismatch: ' + path.name)
    manifest = {'kind': args.kind, 'deploymentManifestSha256': digest(dist / 'deployment.json'),
        'sourceCommit': deployment['sourceCommit'], 'sources': {str(source): digest(source) for source in sources}, 'command': command,
        'references': {str(p.relative_to(dist)): digest(p) for p in references},
        'files': {str(p.relative_to(publish)): digest(p) for p in sorted(publish.rglob('*')) if p.is_file()}}
    (output / 'input-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(publish / 'RuntimeOracle.dll')

if __name__ == '__main__':
    main()
