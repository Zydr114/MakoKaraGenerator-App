#!/usr/bin/env python3
"""Publish only the independent Oracle entry point against immutable Worker DLLs."""
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
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    dist, output = args.dist.resolve(), args.output.resolve()
    deployment = json.loads((dist / 'deployment.json').read_text())
    if deployment['sourceDirty'] or deployment['rid'] != 'linux-x64' or not deployment['selfContained']:
        raise ValueError('requires source-clean linux-x64 self-contained deployment')
    for name, expected in deployment['files'].items():
        if digest(dist / name) != expected:
            raise ValueError('deployment bytes changed: ' + name)
    output.mkdir(parents=True, exist_ok=False)
    worker, publish = dist / 'worker', output / 'publish'
    sources = [ROOT / 'tools/backend-project-image-preview-probe/FixtureOracle/Program.cs',
               ROOT / 'tools/backend-public-package-preview-probe/FixtureOracle/FixtureSources.cs']
    sources += [ROOT / 'tests/Shared' / name for name in
                ('EffectPackageFixture.cs', 'MixedEffectPackageFixture.cs', 'EffectPackageShaderFixture.cs', 'EffectPackageGraphFixture.cs')]
    references = [path for path in sorted(worker.glob('*.dll'))
                  if path.name.startswith(('MakoKaraGenerator.', 'Microsoft.CodeAnalysis'))
                  or path.name in ('SkiaSharp.dll', 'CliWrap.dll')]
    items = ['<Compile Include="' + escape(str(path)) + '" />' for path in sources]
    items += ['<Reference Include="' + escape(path.stem) + '"><HintPath>' + escape(str(path))
              + '</HintPath><Private>true</Private></Reference>' for path in references]
    project = output / 'FixtureOracle.csproj'
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                       '<OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
                       '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
                       '<AssemblyName>FixtureOracle</AssemblyName></PropertyGroup><ItemGroup>'
                       + '\n'.join(items) + '</ItemGroup></Project>\n')
    command = ['flock', '-o', '/tmp/mkg-backend-b4-dotnet.lock', 'dotnet', 'publish', str(project),
               '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true', '-m:1',
               '/p:UseSharedCompilation=false', '-o', str(publish)]
    with (output / 'publish.log').open('w') as log:
        result = subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError('Oracle publish failed; see ' + str(output / 'publish.log'))
    shutil.copytree(worker / 'script-refs', publish / 'script-refs')
    for path in sorted(worker.rglob('*.so')):
        target = publish / path.relative_to(worker)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    # The entry point is new; every production/compiler dependency remains byte-exact.
    for path in references:
        if digest(publish / path.name) != digest(path):
            raise ValueError('published Oracle changed a fixed Worker dependency: ' + path.name)
    manifest = {'scope': 'Only independent fixture/Oracle tool sources compiled. Production DLLs are explicit immutable Worker AssemblyReferences; no production project rebuilt and no HTTP output constructs expected pixels.',
                'deploymentSourceCommit': deployment['sourceCommit'],
                'deploymentManifestSha256': digest(dist / 'deployment.json'), 'command': command,
                'sources': {str(path.relative_to(ROOT)): digest(path) for path in sources},
                'references': {str(path.relative_to(dist)): digest(path) for path in references},
                'projectSha256': digest(project),
                'files': {str(path.relative_to(publish)): digest(path) for path in sorted(publish.rglob('*')) if path.is_file()}}
    (output / 'input-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    print(publish / 'FixtureOracle.dll')


if __name__ == '__main__':
    main()
