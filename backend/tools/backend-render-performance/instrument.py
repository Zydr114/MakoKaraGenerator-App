#!/usr/bin/env python3
"""Apply auditable, source-version-specific tracing to an isolated source copy."""

import argparse
import difflib
import hashlib
import json
from pathlib import Path


TOOL_DIR = Path(__file__).resolve().parent
REPOSITORY = TOOL_DIR.parent.parent.resolve()
TRACE_PATH = "src/MakoKaraGenerator.Rendering.Abstractions/RenderPerformanceTrace.cs"


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def instrument(root):
    """Instrument root and return a manifest containing the complete unified patch."""
    root = Path(root).resolve()
    if root == REPOSITORY or not (root / "src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSession.cs").is_file():
        raise ValueError("source must be an isolated repository copy, never the production checkout")
    if (root / TRACE_PATH).exists():
        raise ValueError("trace template already exists in source copy")
    originals = {}
    modified = {}
    checks = []

    def replace(path, old, new, count=1):
        full = root / path
        if path not in originals:
            originals[path] = full.read_text(encoding="utf-8")
            modified[path] = originals[path]
        actual = modified[path].count(old)
        if actual != count:
            raise ValueError(f"{path}: expected {count} matches, found {actual}: {old[:120]!r}")
        modified[path] = modified[path].replace(old, new)
        checks.append({"path": path, "matches": actual, "needleSha256": sha256(old.encode())})

    factory = "src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs"
    replace(factory, '            device = SkiaRenderDeviceFactory.Create(_renderBackendOptions);',
            '            using (RenderPerformanceTrace.Measure("session.device"))\n                device = SkiaRenderDeviceFactory.Create(_renderBackendOptions);')
    replace(factory, '            var compiled = await CompiledProjectEffects.CompileAsync(snapshot, _compiler.Value, new PackageImageValidator(), new PackageShaderValidator(),\n                new PackageImageGraphValidator(), new PackageMaterialValidator(), token).ConfigureAwait(false);',
            '            CompiledProjectEffects compiled;\n            using (RenderPerformanceTrace.Measure("session.compile"))\n                compiled = await CompiledProjectEffects.CompileAsync(snapshot, _compiler.Value, new PackageImageValidator(), new PackageShaderValidator(),\n                    new PackageImageGraphValidator(), new PackageMaterialValidator(), token).ConfigureAwait(false);')
    replace(factory, '            foreach (var package in compiled.Packages)\n            {',
            '            using (RenderPerformanceTrace.Measure("session.resources"))\n            foreach (var package in compiled.Packages)\n            {')
    replace(factory, '            source = await compiled.CreateFactory(() => layoutReference || editingLineId is not null\n                ? new MeasurementGeometryProvider(Renderer(), layoutReference, editingLineId, editingCueId)\n                : Renderer(), reason).CreateAsync(snapshot, token).ConfigureAwait(false);',
            '            using (RenderPerformanceTrace.Measure("session.source"))\n                source = await compiled.CreateFactory(() => layoutReference || editingLineId is not null\n                    ? new MeasurementGeometryProvider(Renderer(), layoutReference, editingLineId, editingCueId)\n                    : Renderer(), reason).CreateAsync(snapshot, token).ConfigureAwait(false);')
    replace(factory, '    private async Task<IRenderFrameSession> CreateCoreAsync(ProjectSnapshot snapshot, CancellationToken token)\n    {',
            '    private async Task<IRenderFrameSession> CreateCoreAsync(ProjectSnapshot snapshot, CancellationToken token)\n    {\n        using var performanceSessionCreate = RenderPerformanceTrace.Measure("session.create.core");')

    session = "src/MakoKaraGenerator.Runtime.Hosting/ProjectRenderSession.cs"
    old_prepare = '            var prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,\n                request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, request.ResolveTarget(), linked.Token).ConfigureAwait(false);'
    telemetry_prepare = '                using (PerformanceTelemetry.Measure(PerformancePhase.Prepare))\n                    prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,\n                        request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, target, linked.Token).ConfigureAwait(false);'
    session_source = (root / session).read_text(encoding="utf-8")
    runtime_counts = (session_source.count(old_prepare), session_source.count(telemetry_prepare))
    if runtime_counts not in {(2, 0), (0, 2)}:
        raise ValueError(f"unknown or mixed runtime preparation layout: {runtime_counts}")
    runtime_layout = "legacy" if runtime_counts == (2, 0) else "telemetry"
    replace(session, '    private async Task<RenderFrame> RenderCoreAsync(RenderRequest request, CancellationToken token)\n    {',
            '    private async Task<RenderFrame> RenderCoreAsync(RenderRequest request, CancellationToken token)\n    {\n        using var performanceRuntimeRender = RenderPerformanceTrace.Measure("runtime.render.core");')
    replace(session, '        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);\n        try',
            '        using (RenderPerformanceTrace.Measure("runtime.gate"))\n            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);\n        try', count=2)
    if runtime_layout == "legacy":
        replace(session, old_prepare,
                '            PreparedRenderFrame? prepared;\n            using (RenderPerformanceTrace.Measure("runtime.prepare"))\n                prepared = source is null ? null : await source.PrepareAsync(request.TimeUs,\n                    request.TransparentBackground ? OutputMode.Transparent : OutputMode.Opaque, request.ResolveTarget(), linked.Token).ConfigureAwait(false);', count=2)
        replace(session, '            return renderer.Render(request with { PreparedFrame = prepared, CancellationToken = linked.Token });',
                '            using (RenderPerformanceTrace.Measure("runtime.skia"))\n                return renderer.Render(request with { PreparedFrame = prepared, CancellationToken = linked.Token });')
    else:
        replace(session, telemetry_prepare,
                '                using (RenderPerformanceTrace.Measure("runtime.prepare"))\n' + telemetry_prepare, count=2)
        replace(session, '                RenderFrame frame = renderer.Render(request with { PreparedFrame = prepared, CancellationToken = linked.Token });',
                '                RenderFrame frame;\n                using (RenderPerformanceTrace.Measure("runtime.skia"))\n                    frame = renderer.Render(request with { PreparedFrame = prepared, CancellationToken = linked.Token });')

    frame = "src/MakoKaraGenerator.Scripting/Execution/FrameSession.cs"
    replace(frame, '        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);',
            '        using var performanceFramePrepare = RenderPerformanceTrace.Measure(_anchorQuery ? "script.anchor.prepare" : "script.frame.prepare");\n        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);')
    replace(frame, '        var previous = _boundaryFrame; var previousHistory = _history;',
            '        using var performanceBoundary = RenderPerformanceTrace.Measure("script.boundary");\n        var previous = _boundaryFrame; var previousHistory = _history;')
    host = "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.cs"
    replace(host, '        SceneFrame scene = _geometry.GetSceneFrame(_snapshot, timeUs);',
            '        using var performanceEvaluate = RenderPerformanceTrace.Measure("script.evaluate");\n        SceneFrame scene = _geometry.GetSceneFrame(_snapshot, timeUs);')
    diagnostic = "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Diagnostics.cs"
    replace(diagnostic, '        RuntimeDiagnostic? fatal = null;',
            '        using var performanceInvoke = RenderPerformanceTrace.Measure("script.invoke." + stage);\n        RuntimeDiagnostic? fatal = null;')
    particles = "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Particles.cs"
    replace(particles, '        var geometry = BirthGeometry(mode, target, history, cancellationToken);',
            '        using var performanceParticleCommit = RenderPerformanceTrace.Measure("particle.commit");\n        var geometry = BirthGeometry(mode, target, history, cancellationToken);')
    replace(particles, '            cancellationToken.ThrowIfCancellationRequested();\n            if (cachedTime != timeUs)',
            '            using var performanceBirthGeometry = RenderPerformanceTrace.Measure("particle.birthGeometry");\n            cancellationToken.ThrowIfCancellationRequested();\n            if (cachedTime != timeUs)')
    emitters = "src/MakoKaraGenerator.Scripting/Execution/FrameEffectHost.Emitters.cs"
    replace(emitters, '        List<ResolvedEmitter> emitters = [];',
            '        using var performanceEmitters = RenderPerformanceTrace.Measure("script.emitters");\n        List<ResolvedEmitter> emitters = [];')
    replace(emitters, '        var particles = ParticleOperation(() => _particles.Read(text.TimeUs,',
            '        using var performanceParticleRead = RenderPerformanceTrace.Measure("particle.read");\n        var particles = ParticleOperation(() => _particles.Read(text.TimeUs,')
    anchor = "src/MakoKaraGenerator.Scripting/Execution/SemanticAnchorQuery.cs"
    replace(anchor, '        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(target);',
            '        using var performanceAnchor = RenderPerformanceTrace.Measure("script.anchor.query");\n        ArgumentNullException.ThrowIfNull(identity); ArgumentNullException.ThrowIfNull(target);')

    geometry = "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.Geometry.cs"
    replace(geometry, '    public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)\n    {\n        return MeasureSceneFrame(snapshot, timeUs, editingLineId, false);',
            '    public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)\n    {\n        using var performanceGeometry = global::MakoKaraGenerator.Rendering.RenderPerformanceTrace.Measure("geometry.scene");\n        return MeasureSceneFrame(snapshot, timeUs, editingLineId, false);')
    text = "src/MakoKaraGenerator.Rendering.Skia/SkiaTextLayoutEngine.cs"
    replace(text, '    public ShapedLine Shape(string text, TextLayoutOptions options)\n    {\n        ArgumentNullException.ThrowIfNull(text);',
            '    public ShapedLine Shape(string text, TextLayoutOptions options)\n    {\n        using var performanceShape = global::MakoKaraGenerator.Rendering.RenderPerformanceTrace.Measure("text.shape");\n        ArgumentNullException.ThrowIfNull(text);')

    skia = "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs"
    graph = "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.Drawing.cs"
    coverage = "src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.MaterialCoverage.cs"
    scene = "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.cs"
    markers = {
        "old": ((skia, '        var image = surface.Image;\n        var device = surface.Device ?? _device;'),
                (graph, '    private void ValidatePremultiplied(byte[] buffer, int width, int height, string field, CancellationToken cancellationToken)'),
                (coverage, '        byte[] pixels = ReadImagePixels(surface, field);\n        byte[] mask = ReadImagePixels(region, "material.region");')),
        "lease": ((scene, '    private unsafe bool IsEmptyOriginalSurface(SkiaGraphSurface surface, ImageObjectState state, CancellationToken cancellationToken)'),
                  (graph, '    private void ValidatePremultiplied(ImagePixelReadbackLease buffer, int width, int height, string field, CancellationToken cancellationToken)'),
                  (coverage, '        using var pixels = ReadImagePixels(surface, field);\n        using var mask = ReadImagePixels(region, "material.region");')),
    }
    marker_counts = {name: [((root / path).read_text(encoding="utf-8").count(needle)) for path, needle in entries]
                     for name, entries in markers.items()}
    layouts = [name for name, counts in marker_counts.items() if counts == [1, 1, 1]]
    if len(layouts) != 1 or any(count != 0 for name, counts in marker_counts.items() if name != layouts[0] for count in counts):
        raise ValueError(f"unknown or mixed readback layout: {marker_counts}")
    layout = layouts[0]
    if layout == "old":
        replace(skia, '        var image = surface.Image;\n        var device = surface.Device ?? _device;',
                '        using var performanceImageRead = RenderPerformanceTrace.Measure("graph.sceneReadback");\n        var image = surface.Image;\n        var device = surface.Device ?? _device;')
    else:
        scene_old = '        fixed (byte* destination = pixels)\n        {\n            if (!device.ReadPixels(image, new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul),\n                (IntPtr)destination, rowBytes))\n                throw new InvalidOperationException($"Skia {device.Info.Backend} 图像 readback 失败（{device.Info}）。");\n        }'
        scene_new = '        fixed (byte* destination = pixels)\n        {\n            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);\n            bool read = surface.HasOwnedImageOn(device) ? device.ReadOwnedImagePixels(image, info, (IntPtr)destination, rowBytes)\n                : device.ReadPixels(image, info, (IntPtr)destination, rowBytes);\n            if (!read)\n                throw new InvalidOperationException($"Skia {device.Info.Backend} 图像 readback 失败（{device.Info}）。");\n        }'
        scene_source = (root / scene).read_text(encoding="utf-8")
        scene_counts = (scene_source.count(scene_old), scene_source.count(scene_new))
        if scene_counts not in {(1, 0), (0, 1)}:
            raise ValueError(f"unknown or mixed scene readback layout: {scene_counts}")
        if scene_counts == (1, 0):
            replace(scene, scene_old,
                    '        using (RenderPerformanceTrace.Measure("graph.sceneReadback"))\n        {\n            fixed (byte* destination = pixels)\n            {\n                if (!device.ReadPixels(image, new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul),\n                    (IntPtr)destination, rowBytes))\n                    throw new InvalidOperationException($"Skia {device.Info.Backend} 图像 readback 失败（{device.Info}）。");\n            }\n        }')
        else:
            replace(scene, scene_new,
                    '        using (RenderPerformanceTrace.Measure("graph.sceneReadback"))\n        {\n            fixed (byte* destination = pixels)\n            {\n                var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);\n                bool read = surface.HasOwnedImageOn(device) ? device.ReadOwnedImagePixels(image, info, (IntPtr)destination, rowBytes)\n                    : device.ReadPixels(image, info, (IntPtr)destination, rowBytes);\n                if (!read)\n                    throw new InvalidOperationException($"Skia {device.Info.Backend} 图像 readback 失败（{device.Info}）。");\n            }\n        }')
    replace(skia, '        ArgumentNullException.ThrowIfNull(request);\n        ObjectDisposedException.ThrowIf(_disposed, this);\n\n        request.CancellationToken.ThrowIfCancellationRequested();',
            '        using var performanceSkia = RenderPerformanceTrace.Measure("skia.render");\n        ArgumentNullException.ThrowIfNull(request);\n        ObjectDisposedException.ThrowIf(_disposed, this);\n\n        request.CancellationToken.ThrowIfCancellationRequested();')
    replace(skia, '        PreparedRenderFrame.ValidateRequest(request);\n        ValidateNativePreparedFrame(request.PreparedFrame);',
            '        using (RenderPerformanceTrace.Measure("skia.validate"))\n        {\n            PreparedRenderFrame.ValidateRequest(request);\n            ValidateNativePreparedFrame(request.PreparedFrame);\n        }')
    replace(skia, '            using (SKSurface surface = _device.CreateSurface(info))\n            {\n                SKCanvas canvas = surface.Canvas;',
            '            SKSurface surface;\n            using (RenderPerformanceTrace.Measure("skia.surface.create")) surface = _device.CreateSurface(info);\n            using (surface)\n            {\n                using var performanceDraw = RenderPerformanceTrace.Measure("skia.draw");\n                SKCanvas canvas = surface.Canvas;')
    replace(skia, '                    DrawBackground(canvas, request, composition);',
            '                    using (RenderPerformanceTrace.Measure("skia.draw.background"))\n                        DrawBackground(canvas, request, composition);')
    replace(skia, '                DrawOverlays(canvas, request, Scripting.OverlayLayer.BehindText);',
            '                using (RenderPerformanceTrace.Measure("skia.draw.overlays"))\n                    DrawOverlays(canvas, request, Scripting.OverlayLayer.BehindText);')
    replace(skia, '                    imageDiagnostics = DrawImageEffectScene(surface, request, frameResources, out var drawingDiagnostics, out materialDiagnostics);',
            '                    ImmutableArray<Scripting.RuntimeDiagnostic> drawingDiagnostics;\n                    using (RenderPerformanceTrace.Measure("skia.draw.imageScene"))\n                        imageDiagnostics = DrawImageEffectScene(surface, request, frameResources, out drawingDiagnostics, out materialDiagnostics);')
    replace(skia, '                else DrawLyrics(canvas, request, composition, measuredScene);\n                DrawOverlays(canvas, request, Scripting.OverlayLayer.AboveText);',
            '                else using (RenderPerformanceTrace.Measure("skia.draw.lyrics")) DrawLyrics(canvas, request, composition, measuredScene);\n                using (RenderPerformanceTrace.Measure("skia.draw.overlays"))\n                    DrawOverlays(canvas, request, Scripting.OverlayLayer.AboveText);')
    replace(skia, '                canvas.Restore();\n                _device.FlushAndSubmit(surface);\n                unsafe',
            '                canvas.Restore();\n                performanceDraw.Dispose();\n                using (RenderPerformanceTrace.Measure("skia.finalSubmit")) _device.FlushAndSubmit(surface);\n                using (RenderPerformanceTrace.Measure("skia.finalReadback"))\n                unsafe')

    replace(graph, '        int width = checked((int)Math.Round(shape.Width * frame.DensityX)), height = checked((int)Math.Round(shape.Height * frame.DensityY));',
            '        using var performanceRaster = RenderPerformanceTrace.Measure("graph.raster");\n        int width = checked((int)Math.Round(shape.Width * frame.DensityX)), height = checked((int)Math.Round(shape.Height * frame.DensityY));')
    replace(graph, '            using var surface = _device.CreateSurface(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));',
            '            SKSurface createdSurface;\n            using (RenderPerformanceTrace.Measure("graph.surface.create"))\n                createdSurface = _device.CreateSurface(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));\n            using var surface = createdSurface;')
    replace(graph, '            cancellationToken.ThrowIfCancellationRequested(); draw(canvas); cancellationToken.ThrowIfCancellationRequested();\n            _device.FlushAndSubmit(surface);',
            '            cancellationToken.ThrowIfCancellationRequested();\n            using (RenderPerformanceTrace.Measure("graph.draw")) draw(canvas);\n            cancellationToken.ThrowIfCancellationRequested();\n            using (RenderPerformanceTrace.Measure("graph.submit")) _device.FlushAndSubmit(surface);')
    validation = markers[layout][1][1] + '\n    {'
    replace(graph, validation, validation + '\n        using var performanceValidate = RenderPerformanceTrace.Measure("graph.validate.premultiplied");')
    replace(graph, '        device ??= _device;\n        int rowBytes = checked(image.Width * 4);',
            '        using var performanceRead = RenderPerformanceTrace.Measure("graph.readback");\n        device ??= _device;\n        int rowBytes = checked(image.Width * 4);')
    coverage_reads = markers[layout][2][1]
    replace(coverage, coverage_reads, '        using var performanceCoverage = RenderPerformanceTrace.Measure("graph.validate.region");\n' + coverage_reads)
    device = "src/MakoKaraGenerator.Rendering.Skia/SkiaRenderBackend.cs"
    replace(device, '    public void FlushAndSubmit(SKSurface surface)\n    {\n        ArgumentNullException.ThrowIfNull(surface);',
            '    public void FlushAndSubmit(SKSurface surface)\n    {\n        using var performanceSubmit = RenderPerformanceTrace.Measure("device.submit");\n        ArgumentNullException.ThrowIfNull(surface);', count=2)
    replace(device, '    public SKImage? RasterizeImage(SKImage image)\n    {\n        ArgumentNullException.ThrowIfNull(image);',
            '    public SKImage? RasterizeImage(SKImage image)\n    {\n        using var performanceRasterize = RenderPerformanceTrace.Measure("device.rasterizeImage");\n        ArgumentNullException.ThrowIfNull(image);', count=2)
    replace(device, '    public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)\n    {\n        ArgumentNullException.ThrowIfNull(image);',
            '    public bool ReadPixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)\n    {\n        using var performanceRead = RenderPerformanceTrace.Measure("device.imageReadback");\n        RenderPerformanceTrace.AddBytes("device.imageReadback.requested", checked((long)rowBytes * imageInfo.Height));\n        ArgumentNullException.ThrowIfNull(image);', count=2)
    owned_device = '    public bool ReadOwnedImagePixels(SKImage image, SKImageInfo imageInfo, IntPtr destination, int rowBytes)\n    {\n        ArgumentNullException.ThrowIfNull(image);\n        ObjectDisposedException.ThrowIf(_disposed, this);'
    owned_source = (root / device).read_text(encoding="utf-8")
    owned_count = owned_source.count(owned_device)
    if owned_count not in {0, 1}:
        raise ValueError(f"unexpected owned image readback methods: {owned_count}")
    if owned_count == 1:
        owned_fast = '            return ReadPixels(image, imageInfo, destination, rowBytes);\n\n        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Readback);\n        SkiaReadbackTelemetry.Record(true, imageInfo);'
        replace(device, owned_fast,
                '            return ReadPixels(image, imageInfo, destination, rowBytes);\n\n        using var performanceRead = RenderPerformanceTrace.Measure("device.imageReadback");\n        RenderPerformanceTrace.AddBytes("device.imageReadback.requested", checked((long)rowBytes * imageInfo.Height));\n        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Readback);\n        SkiaReadbackTelemetry.Record(true, imageInfo);')

    trace = (TOOL_DIR / "RenderPerformanceTrace.cs").read_text(encoding="utf-8")
    originals[TRACE_PATH] = ""
    modified[TRACE_PATH] = trace
    patch = "".join("".join(difflib.unified_diff(originals[path].splitlines(keepends=True), modified[path].splitlines(keepends=True),
                                               fromfile="a/" + path if originals[path] else "/dev/null", tofile="b/" + path))
                    for path in sorted(modified))
    manifest = {"schemaVersion": 1, "readbackLayout": layout, "runtimeLayout": runtime_layout, "files": [{"path": path, "beforeSha256": sha256(originals[path].encode()) if originals[path] else None,
                "afterSha256": sha256(modified[path].encode())} for path in sorted(modified)], "checks": checks,
                "patchSha256": sha256(patch.encode()), "patch": patch}
    for path, text in modified.items():
        (root / path).write_text(text, encoding="utf-8")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="manifest JSON path; patch is also written beside it")
    args = parser.parse_args()
    manifest = instrument(args.source)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix(".patch").write_text(manifest["patch"], encoding="utf-8")
    args.output.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
