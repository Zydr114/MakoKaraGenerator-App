using System.Globalization;
using System.Runtime.InteropServices;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Backend.Rendering;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Media;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Runtime.Hosting;
using SkiaSharp;

namespace MakoKaraGenerator.Backend.Worker;

internal static class SceneMeasurementWorker
{
    internal static async Task<byte[]> ExecuteAsync(byte[] payload, CancellationToken token, string? renderBackend = null)
    {
        var prepared = WorkerScriptInputs.Read(payload, token);
        var (input, source) = WorkerSceneMeasurementContract.Decode(prepared.Source.ToArray());
        using var runtimeInput = await ProjectRuntimeInput.OpenAsync(source, token, prepared.Compiler).ConfigureAwait(false);
        var snapshot = runtimeInput.Snapshot;
        if (snapshot.LyricPresentation.Nodes.Length > ProjectGeometryReferenceLimits.MaxNodes)
            throw new WorkerProtocolException("测量树超过1024节点。", "SceneMeasurementBudgetExceeded");
        if (snapshot.LineCount > 2048
            || snapshot.Lyrics.AllLines.Sum(line => (long)line.RubyAnnotations.Length + line.RubyAnnotations.Sum(ruby => ruby.EffectiveSegments.Count))
                + snapshot.LineCount + snapshot.LyricPresentation.Nodes.Length > SceneMeasurementContract.MaximumElements
            || snapshot.Lyrics.AllLines.Sum(line => (long)line.Text.Length
                + line.RubyAnnotations.Sum(ruby => ruby.EffectiveSegments.Sum(segment => (long)segment.Text.Length))) > 200_000)
            throw new WorkerProtocolException("完整测量文本/定义超过预算。", "SceneMeasurementBudgetExceeded");
        var projectedTree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        if (projectedTree.Nodes.Length > ProjectGeometryReferenceLimits.MaxNodes) throw new WorkerProtocolException("投影测量树超过节点预算。", "SceneMeasurementBudgetExceeded");
        foreach (var node in projectedTree.Nodes)
        {
            var current = node; int depth = 0;
            while (current.ParentId is { } parent)
            {
                if (++depth > ProjectGeometryReferenceLimits.MaxDepth) throw new WorkerProtocolException("测量树超过深度预算。", "SceneMeasurementBudgetExceeded");
                current = projectedTree.FindNode(parent) ?? throw new WorkerProtocolException("测量父节点不存在。");
            }
        }
        token.ThrowIfCancellationRequested();
        await using var session = await runtimeInput.CreateFactory(input.Request.Mode == "layout", input.EditingLineId, input.EditingCueId,
            renderBackend).CreateAsync(snapshot, token).ConfigureAwait(false);
        if (session is not ISceneMeasurementSession measurable) throw new WorkerProtocolException("Runtime没有同帧测量能力。", "SceneMeasurementUnavailable");
        var request = input.Request;
        long time = request.TimeUs is null ? 0 : long.Parse(request.TimeUs, CultureInfo.InvariantCulture);
        using var measured = await measurable.MeasureAsync(RenderRequest.At(snapshot, time) with {
            Target = new() { PixelWidth = request.Width, PixelHeight = request.Height },
            TransparentBackground = request.AlphaMode == "transparent", CancellationToken = token }, request.Mode == "layout",
            input.EditingLineId, input.EditingCueId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (measured.Frame.DiagnosticSnapshot.HasError)
            throw new WorkerProtocolException("实际Runtime诊断禁止测量像素发布。", "WorkerRuntimeDiagnosticError");
        // 缺背景的图效果／材质已按声明兜底；同帧测量照常返回，兜底诊断去重后随结果给出。
        BackendFallbackDiagnosticCollector fallback = new();
        fallback.Add(measured.Frame);
        if (fallback.Truncated) throw new WorkerProtocolException("测量兜底诊断超过预算。", "SceneMeasurementBudgetExceeded");
        var scene = measured.Scene;
        if (scene.Definition.Elements.Length > SceneMeasurementContract.MaximumElements
            || scene.Elements.Sum(element => (long)element.WorldCorners.Length) > SceneMeasurementContract.MaximumCorners)
            throw new WorkerProtocolException("完整测量几何超过预算。", "SceneMeasurementBudgetExceeded");
        var elements = scene.Elements.ToDictionary(element => element.Id, StringComparer.Ordinal);
        var lines = scene.Lines.ToDictionary(line => line.ElementId, StringComparer.Ordinal);
        var output = scene.Definition.Elements.Select(definition => {
            elements.TryGetValue(definition.Id, out var element);
            lines.TryGetValue(definition.Id, out var line);
            var persistent = measured.Nodes.GetValueOrDefault(definition.Id);
            var parent = element?.PlacementReference ?? (line is not null ? measured.Nodes.GetValueOrDefault(line.ParentId) : null);
            return new SceneMeasurementElementDto(definition.Id, element?.ParentId ?? definition.ParentId,
                persistent is not null ? definition.Content is TextMarkContent ? "mark" : projectedTree.Nodes.Any(node => node.Layout?.SlotIds.Contains(definition.Id) == true) ? "slot" : "container"
                    : definition.SourceBinding.RubyId is not null ? "ruby" : "line",
                definition.SourceBinding.BranchId, definition.SourceBinding.LineId, definition.SourceBinding.TokenId, definition.SourceBinding.RubyId,
                element?.BindingId,
                // SceneLineSample.SlotId is the scheduler role; the public identity is the persisted slot node.
                line?.SlotId is not null ? line.ParentId : null, request.Mode == "layout" && line is not null,
                element?.IsVisible ?? false, element?.Opacity ?? 0, element is null ? "notVisible" : element.WorldCorners.IsEmpty ? "emptyReference" : "measuredInk",
                element is null ? persistent is null ? null : Matrix(persistent.WorldMatrix) : Matrix(element.WorldMatrix),
                element is null ? persistent is null ? null : Point(persistent.Pivot) : Point(element.Pivot),
                element is null ? [] : element.WorldCorners.Select(Point).ToArray(), persistent is null ? null : Reference(persistent), parent is null ? null : Reference(parent));
        }).ToArray();
        byte[] png = EncodePng(measured.Frame);
        var data = new SceneMeasurementDataDto(input.SourceSha256, input.SampleInputFingerprint, runtimeInput.SourceProfile,
            request.Mode == "layout" ? SceneMeasurementContract.LayoutProfile : input.EditingLineId is null ? SceneMeasurementContract.TimedProfile : "line-reference-v1",
            request.Mode, request.Mode == "layout" ? null : scene.TimeUs.ToString(CultureInfo.InvariantCulture), snapshot.Composition.CanvasWidth,
            snapshot.Composition.CanvasHeight, request.Width, request.Height, request.AlphaMode, output, WorkerProtocol.ComputeSha256(png), png,
            FallbackDiagnostics: fallback.ToArray());
        WorkerAttachmentGeometryDto? attachment = null;
        if (input.AttachmentContainerId is { } container)
        {
            if (input.EditingLineId is not { } lineId || !measured.Nodes.TryGetValue(container, out var previous)
                || !SceneAttachmentReferences.Resolve(snapshot, scene, includeFixed: true, modeOverride: SceneAttachmentMode.CueTargetText).TryGetValue(container, out var targetText)
                || !SceneAttachmentReferences.Resolve(snapshot, scene, includeFixed: true, modeOverride: SceneAttachmentMode.CueTargetAboveStart).TryGetValue(container, out var targetAboveStart)
                || !scene.Lines.Any(line => line.Frame.Line!.Id == lineId && line.Geometry is { } geometry
                    && geometry.BottomRight.X > geometry.TopLeft.X && geometry.BottomRight.Y > geometry.TopLeft.Y))
                throw new WorkerProtocolException("所选前奏目标没有可用的真实文字参考。", "SceneAttachmentReferenceUnavailable");
            var node = projectedTree.FindNode(container)!;
            var fixedNodes = SceneTreeGeometryResolver.Resolve(projectedTree.Nodes, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight);
            var parent = node.ParentId is { } parentId ? fixedNodes[parentId]
                : new SceneTreeGeometry(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, SceneMatrix.Identity, new(0, 0));
            attachment = new(container, lineId, Reference(previous), Reference(parent), Reference(targetText), Reference(targetAboveStart));
        }
        return SceneMeasurementContract.EncodeBounded(new WorkerSceneMeasurementOutput(data, attachment));
    }
    private static ProjectGeometryPointDto Point(CanvasPoint point) => new(point.X, point.Y);
    private static ProjectGeometryMatrixDto Matrix(SceneMatrix matrix) => new(matrix.A, matrix.B, matrix.C, matrix.D, matrix.Tx, matrix.Ty);
    private static ProjectGeometryReferenceDto Reference(SceneTreeGeometry value) => new(value.Width, value.Height, Matrix(value.WorldMatrix), Point(value.Pivot),
        [Point(value.WorldMatrix.Map(new(0,0))), Point(value.WorldMatrix.Map(new(value.Width,0))),
            Point(value.WorldMatrix.Map(new(value.Width,value.Height))), Point(value.WorldMatrix.Map(new(0,value.Height)))]);
    private static byte[] EncodePng(RenderFrame frame)
    {
        if (frame.Format != PixelFormat.Bgra8888Premultiplied || frame.Width is < 1 or > 1280 || frame.Height is < 1 or > 720
            || frame.Stride != frame.Width * 4 || (long)frame.Stride * frame.Height > 4 * 1024 * 1024)
            throw new WorkerProtocolException("测量像素格式或预算无效。");
        using var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (bitmap.GetPixels() == IntPtr.Zero || bitmap.RowBytes != frame.Stride) throw new WorkerProtocolException("测量PNG缓冲分配失败。");
        var pixels = frame.PixelMemory.ToArray(); Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new WorkerProtocolException("测量PNG编码失败。");
        if (encoded.Size is <= 0 or > SceneMeasurementContract.MaximumResponseBytes)
            throw new WorkerProtocolException("完整PNG超过测量响应预算。", "SceneMeasurementBudgetExceeded");
        return encoded.ToArray();
    }
}
