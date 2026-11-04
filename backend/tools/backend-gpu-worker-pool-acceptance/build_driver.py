#!/usr/bin/env python3
"""Compile only M04 tool entry points against byte-exact matched deployment DLLs."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
from xml.sax.saxutils import escape


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dist", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    dist, output = args.dist.resolve(), args.output.resolve()
    root = Path(__file__).resolve().parents[3]
    output.relative_to(root / ".local")
    if output == dist or dist in output.parents or output in dist.parents:
        raise ValueError("tool output must be separate from immutable deployment")
    deployment = json.loads((dist / "deployment.json").read_text())
    if deployment["sourceDirty"] or deployment["rid"] != "linux-x64" or not deployment["selfContained"]:
        raise ValueError("requires source-clean linux-x64 self-contained matched publish")
    for name, expected in deployment["files"].items():
        if digest(dist / name) != expected:
            raise ValueError("deployment bytes changed: " + name)
    worker, server = dist / "worker", dist / "server"
    compiler_names = ("MakoKaraGenerator.Scripting.Compiler.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll")
    for name in compiler_names:
        if (worker / name).exists():
            raise ValueError("render Worker contains forbidden compiler dependency: " + name)
        if not (server / name).is_file():
            raise ValueError("server compiler dependency missing: " + name)
    references = {}
    # The trusted driver composes the actual Host. Shared runtime/SDK assemblies
    # must agree with the Worker before compiler SDK identity can be trusted.
    for path in sorted(server.glob("*.dll")):
        if path.name.startswith(("MakoKaraGenerator.", "Microsoft.CodeAnalysis")) or path.name in (
                "CliWrap.dll", "SkiaSharp.dll", "HarfBuzzSharp.dll", "SharpVk.dll",
                "SkiaSharp.HarfBuzz.dll", "SkiaSharp.Vulkan.SharpVk.dll", "SoundFlow.dll"):
            if path.name == "MakoKaraGenerator.Backend.Server.dll":
                continue
            other = worker / path.name
            if other.is_file() and digest(other) != digest(path):
                raise ValueError("server/worker shared assembly mismatch: " + path.name)
            references[path.name] = path
    for name in ("MakoKaraGenerator.Backend.Hosting.dll", "MakoKaraGenerator.Core.dll",
                 "MakoKaraGenerator.Scripting.Abstractions.dll", *compiler_names):
        if name not in references:
            raise ValueError("required explicit dependency missing: " + name)
    output.mkdir(parents=True, exist_ok=False)
    sources = [Path(__file__).resolve().parent / name for name in ("Program.cs", "DualConsumerDriver.cs", "MalformedWorkerProbe.cs")]
    items = ['<Compile Include="' + escape(str(path)) + '" />' for path in sources]
    items += ['<Reference Include="' + escape(path.stem) + '"><HintPath>' + escape(str(path))
              + '</HintPath><Private>true</Private></Reference>' for path in references.values()]
    project = output / "M04.Acceptance.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                       '<OutputType>Exe</OutputType><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
                       '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
                       '<AssemblyName>MakoKaraGenerator.Backend.Hosting.Tests</AssemblyName></PropertyGroup><ItemGroup>'
                       + '\n'.join(items) + '</ItemGroup></Project>\n')
    publish = output / "publish"
    command = ['dotnet', 'publish', str(project), '-c', 'Release', '-r', 'linux-x64',
               '--self-contained', 'true', '-m:1', '/p:UseSharedCompilation=false', '-o', str(publish)]
    with (output / "publish.log").open("w") as log:
        result = subprocess.run(command, cwd=root, stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError("tool publish failed; see " + str(output / "publish.log"))
    for path in sorted(worker.rglob("*.so")):
        target = publish / path.relative_to(worker)
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    for name, path in references.items():
        if digest(publish / name) != digest(path):
            raise ValueError("tool changed fixed deployment reference: " + name)
    manifest = {
        "scope": "Only independent M04 tool entry points compiled; all production SDK/runtime/Host/compiler DLLs are byte-exact explicit deployment references; no production project rebuilt or deployment overwritten",
        "deploymentSourceCommit": deployment["sourceCommit"],
        "deploymentManifestSha256": digest(dist / "deployment.json"),
        "sources": {path.name: digest(path) for path in sources},
        "references": {str(path.relative_to(dist)): digest(path) for path in references.values()},
        "command": command, "projectSha256": digest(project),
        "files": {str(path.relative_to(publish)): digest(path) for path in sorted(publish.rglob("*")) if path.is_file()},
    }
    (output / "input-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(publish / "MakoKaraGenerator.Backend.Hosting.Tests.dll")


if __name__ == "__main__":
    main()
