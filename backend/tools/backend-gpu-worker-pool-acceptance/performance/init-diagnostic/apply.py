#!/usr/bin/env python3
"""Apply identical actual-creation diagnostics to an isolated M03 or M04 source archive; never build/run."""
import argparse
import hashlib
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent

def sha(data):
    return hashlib.sha256(data).hexdigest()

def replace_once(text, anchor, replacement):
    if text.count(anchor) != 1:
        raise ValueError(f"Expected one stable source anchor: {anchor[:100]}")
    return text.replace(anchor, replacement)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    root = args.root.resolve()
    if "/.local/" not in str(root) or not (root / "backend/src").is_dir():
        raise ValueError("Target must be an isolated App .local source archive, never active source.")
    paths = {
        "skia": root / "backend/src/MakoKaraGenerator.Rendering.Skia/SkiaRenderBackend.cs",
        "package": root / "backend/src/MakoKaraGenerator.Backend.Worker/PackageRenderSession.cs",
        "program": root / "backend/src/MakoKaraGenerator.Backend.Worker/Program.cs",
        "factory": root / "backend/src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs",
        "probe": root / "backend/src/MakoKaraGenerator.Runtime.Hosting/RuntimeRenderBackendProbe.cs",
    }
    changed = {}
    for key, path in paths.items():
        before = path.read_text()
        if "GpuInitializationDiagnostic" in before:
            raise ValueError(f"Already instrumented: {path}")
        after = before
        if key == "skia":
            anchor = "null, deviceExtensions.Length == 0 ? null : deviceExtensions, null, null, null, null, null);"
            after = replace_once(after, anchor, anchor + "\n            string gpuDiagnosticPair = GpuInitializationDiagnostic.DeviceCreated();")
            anchor = '?? throw new InvalidOperationException("Skia Ganesh 无法基于 Vulkan device 创建 GRContext。");'
            after = replace_once(after, anchor, anchor + "\n            GpuInitializationDiagnostic.ContextCreated(gpuDiagnosticPair);")
            anchor = "vulkanLibrary, getRawProc, getSharpProc, info, (uint)family, enableGpuPreview);"
            after = replace_once(after, anchor, anchor + "\n            GpuInitializationDiagnostic.Track(result, gpuDiagnosticPair);")
        elif key == "package":
            anchor = 'if (_session is not null) throw new WorkerProtocolException("Worker 已持有固定会话。", "WorkerSessionAlreadyOpen");'
            # The outer request SessionId exists before decode and before M04 Bind.
            after = replace_once(after, anchor, anchor + '\n                if (enableGpuPreview) RuntimeGpuInitializationDiagnostic.Mark("business-before", request.SessionId);')
            anchor = "try { session = await factory.CreateAsync(snapshot, token).ConfigureAwait(false); }"
            after = replace_once(after, anchor, 'try { session = await factory.CreateAsync(snapshot, token).ConfigureAwait(false); if (enableGpuPreview) RuntimeGpuInitializationDiagnostic.Mark("business-after", request.SessionId); }')
            anchor = "_gpuOpen = open;"
            after = replace_once(after, anchor, anchor + '\n                    RuntimeGpuInitializationDiagnostic.Mark("stream-opened", request.SessionId);')
        elif key == "program":
            anchor = "string renderBackend = RuntimeRenderBackendProbe.ResolveEnvironment();"
            after = replace_once(after, anchor, anchor + '\n        RuntimeGpuInitializationDiagnostic.Mark("worker-start");')
        elif key == "factory":
            anchor = "if (enableGpuPreview && device is not VulkanSkiaRenderDevice)"
            after = replace_once(after, anchor, 'if (enableGpuPreview) GpuInitializationDiagnostic.MarkDevice("business-device", device);\n            ' + anchor)
        else:
            # Both baseline Probe and retained M04 Probe use the same actual-device
            # validation anchor, without inserting a second probe or construction.
            anchor = 'bool gpu = backend == "vulkan";'
            after = replace_once(after, anchor, 'GpuInitializationDiagnostic.MarkDevice("probe-device", device);\n        ' + anchor)
        changed[path] = (before.encode(), after.encode())
    for project, filename in (("MakoKaraGenerator.Rendering.Skia", "GpuInitializationDiagnostic.cs"), ("MakoKaraGenerator.Runtime.Hosting", "RuntimeGpuInitializationDiagnostic.cs")):
        target = root / "backend/src" / project / filename
        if target.exists():
            raise ValueError(f"Diagnostic helper already exists: {target}")
        changed[target] = (None, (HERE / filename).read_bytes())
    tool_files = [Path(__file__), HERE / "GpuInitializationDiagnostic.cs", HERE / "RuntimeGpuInitializationDiagnostic.cs"]
    tools = {p.name: sha(p.read_bytes()) for p in tool_files}
    # Finish all anchor checks before any mutation. The tool does not touch a dist.
    for path, (_, after) in changed.items():
        path.write_bytes(after)
    manifest = {"schemaVersion": 1, "toolFiles": tools, "toolHash": sha(json.dumps(tools, sort_keys=True, separators=(",", ":")).encode()), "root": str(root), "diagnosticOnly": True,
        "files": {str(p.relative_to(root)): {"before": sha(before) if before is not None else None, "after": sha(after)} for p, (before, after) in changed.items()},
        "boundary": "Same helper/tool SHA required across old/new; separately built diagnostic dist only, never used for timing AB."}
    args.manifest.parent.mkdir(parents=True, exist_ok=True)
    args.manifest.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")

if __name__ == "__main__":
    main()
