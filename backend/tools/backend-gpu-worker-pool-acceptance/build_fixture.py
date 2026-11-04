#!/usr/bin/env python3
"""Export a current-version original WordEmphasis snapshot without rendering."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
from xml.sax.saxutils import escape


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[3]
    dist, output = args.dist.resolve(), args.output.resolve()
    output.relative_to(root / '.local')
    if output == dist or dist in output.parents or output in dist.parents:
        raise ValueError('output must be separate from immutable deployment')
    manifest = json.loads((dist / 'deployment.json').read_text())
    if manifest['sourceDirty']:
        raise ValueError('requires source-clean matched deployment')
    for name, expected in manifest['files'].items():
        if digest(dist / name) != expected:
            raise ValueError('deployment content changed: ' + name)
    references = sorted((dist / 'server').glob('MakoKaraGenerator.*.dll'))
    sources = [Path(__file__).resolve().parent / 'ExportFixture.cs',
               root / 'backend/tools/backend-render-performance/Runner/Fixtures.cs']
    output.mkdir(parents=True, exist_ok=False)
    project = output / 'ExportFixture.csproj'
    items = ['<Compile Include="' + escape(str(path)) + '" />' for path in sources]
    items += ['<Reference Include="' + escape(path.stem) + '"><HintPath>' + escape(str(path))
              + '</HintPath><Private>true</Private></Reference>' for path in references]
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                       '<OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
                       '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
                       '</PropertyGroup><ItemGroup>' + ''.join(items) + '</ItemGroup></Project>\n')
    command = ['dotnet', 'build', str(project), '--nologo', '-m:1', '-p:UseSharedCompilation=false']
    with (output / 'build.log').open('w') as log:
        subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT, check=True)
    binary = output / 'bin/Debug/net10.0/ExportFixture.dll'
    snapshot = output / 'word-emphasis.snapshot.json'
    run = ['dotnet', str(binary), str(root / 'backend'), str(snapshot)]
    with (output / 'export.log').open('w') as log:
        subprocess.run(run, cwd=root, stdout=log, stderr=subprocess.STDOUT, check=True)
    for path in references:
        if digest(output / 'bin/Debug/net10.0' / path.name) != digest(path):
            raise ValueError('exporter changed deployment dependency: ' + path.name)
    (output / 'tool-provenance.json').write_text(json.dumps({
        'scope': 'tool-only existing fixture builder and production codec; no author compile, render, or device',
        'deploymentManifestSha256': digest(dist / 'deployment.json'),
        'deploymentSourceCommit': manifest['sourceCommit'],
        'sources': {str(path.relative_to(root)): digest(path) for path in sources},
        'references': {str(path.relative_to(dist)): digest(path) for path in references},
        'buildCommand': command, 'runCommand': run, 'snapshotSha256': digest(snapshot),
    }, indent=2) + '\n')
    print(snapshot)


if __name__ == '__main__':
    main()
