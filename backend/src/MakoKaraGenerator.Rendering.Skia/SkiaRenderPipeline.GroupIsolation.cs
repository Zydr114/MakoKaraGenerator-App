using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private sealed class IsolationUnit(PreparedGroupOpacity? opacity = null, SceneDrawItem? item = null)
    {
        internal PreparedGroupOpacity? Opacity { get; } = opacity;
        internal string? GroupId { get; init; }
        internal ImmutableArray<PreparedImageEffectBinding> GroupBindings { get; init; } = [];
        internal SceneDrawItem? Item { get; } = item;
        internal List<IsolationUnit> Children { get; } = [];
        internal List<int> Indices { get; } = [];
        internal int Order => Indices[0];
    }
    private void DrawIsolatedScene(SKSurface main, RenderRequest request, ImageSurfaceBudgetFrame resources,
        Action<SKSurface, SceneDrawItem> drawObject, ImmutableArray<RuntimeDiagnostic>.Builder diagnostics)
    {
        var frame = request.PreparedFrame!;
        var items = DrawItems(frame.HierarchyScene).ToArray();
        var nodes = frame.HierarchyScene.Elements.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        // Frozen parent aliases preserve geometry but still belong to the same logical
        // group. Composite active and exiting children together before applying alpha.
        var outputs = frame.GroupOpacities.Where(item => nodes[item.Opacity.Target.NodeId.Value].SourceNodeId is null)
            .ToDictionary(item => item.Opacity.Target.NodeId.Value, StringComparer.Ordinal);
        if (outputs.Count == 0) { foreach (var item in items) drawObject(main, item); return; }
        IsolationUnit root = new(); Dictionary<string, IsolationUnit> groups = new(StringComparer.Ordinal);
        for (int index = 0; index < items.Length; index++)
        {
            List<string> chain = []; HashSet<string> seen = new(StringComparer.Ordinal);
            string? current = items[index].NodeId;
            while (current is not null)
            {
                if (!seen.Add(current)) throw new PreparedFrameException("InvalidSceneIsolationTree", "隔离运行父树含环。");
                if (!nodes.TryGetValue(current, out var node)) throw new PreparedFrameException("InvalidSceneIsolationTree", "隔离运行父树缺少节点。");
                string source = node.SourceNodeId ?? current;
                if (outputs.ContainsKey(source)) chain.Add(source);
                current = node.ParentId;
            }
            IsolationUnit unit = root;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (!groups.TryGetValue(chain[i], out var group))
                {
                    group = new(outputs[chain[i]]); groups.Add(chain[i], group); unit.Children.Add(group);
                }
                group.Indices.Add(index); unit = group;
            }
            IsolationUnit leaf = new(item: items[index]); leaf.Indices.Add(index); unit.Children.Add(leaf);
        }
        foreach (var group in groups.Values)
            if (group.Indices[^1] - group.Indices[0] + 1 != group.Indices.Count)
            {
                var owner = group.Opacity!;
                diagnostics.Add(new(owner.InstanceId, owner.ArtifactHash, "RenderGroupIsolation", request.TimeUs,
                    new(DiagnosticSeverity.Warning, "GroupIsolationChangesOrder", "组 " + owner.Opacity.Target.NodeId.Value + " 的后代原本与外部内容交错；组隔离后作为连续单位在首项位置合成喵。")) { PackageIdentity = owner.PackageIdentity });
            }
        DrawChildren(main, root);

        void DrawChildren(SKSurface destination, IsolationUnit unit)
        {
            foreach (var child in unit.Children.OrderBy(item => item.Order))
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (child.Item is { } item) drawObject(destination, item);
                else DrawGroup(destination, child);
            }
        }
        void DrawGroup(SKSurface destination, IsolationUnit unit)
        {
            var owner = unit.Opacity!;
            try
            {
                var composition = request.Project.Composition;
                using var state = new ImageObjectState(new(0, 0, composition.CanvasWidth, composition.CanvasHeight), _surfaceBudget.Limits, resources)
                    { Opacity = owner.Opacity.Factor };
                using var below = SnapshotSceneBelow(destination, request, state.Bounds, state);
                // 即使 alpha 为零仍执行子图：非法 shader／缺资源不能被透明度掩盖。
                using var processed = RenderGroupChildren(request, state, below, surface => DrawChildren(surface, unit));
                using var final = ApplyImageObjectOpacity(request, state, processed, below);
                DrawGraphSurface(destination.Canvas, final, SKBlendMode.Src);
            }
            catch (OperationCanceledException) { throw; }
            catch (EffectFailureException) { throw; }
            catch (Exception error)
            {
                string code = error switch { PreparedFrameException e => e.Code, ImageGraphException e => e.Code,
                    ImageSurfaceBudgetException e => e.Code, ShaderProgramException e => e.Code, _ => "GroupIsolationFailed" };
                throw new EffectFailureException(owner.InstanceId, owner.ArtifactHash, "RenderGroupIsolation", request.TimeUs, code, error, owner.PackageIdentity);
            }
        }
    }
    private SkiaGraphSurface RenderGroupChildren(RenderRequest request, ImageObjectState state, SkiaGraphSurface below, Action<SKSurface> draw)
    {
        var target = request.ResolveTarget(); var composition = request.Project.Composition;
        long bytes = checked((long)target.PixelWidth * target.PixelHeight * 4);
        if (target.PixelWidth > state.Budget.MaximumPixelDimension || target.PixelHeight > state.Budget.MaximumPixelDimension || bytes > state.Budget.MaximumBytes)
            throw new PreparedFrameException("CaptureBudgetExceeded", "实际组隔离尺寸超过明确预算，不缩图。");
        ImageSurfaceReservation? reservation = state.Resources.Reserve(1, bytes, "SceneGroupChildren");
        try
        {
            using var srgb = SKColorSpace.CreateSrgb();
            using var surface = _device.CreateSurface(new SKImageInfo(target.PixelWidth, target.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            surface.Canvas.ClipRect(new(0, 0, target.PixelWidth, target.PixelHeight)); surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)target.ScaleXOf(composition), (float)target.ScaleYOf(composition));
            DrawGraphSurface(surface.Canvas, below, SKBlendMode.Src);
            draw(surface); request.CancellationToken.ThrowIfCancellationRequested();
            _device.FlushAndSubmit(surface);
            var image = surface.Snapshot();
            try
            {
                var result = SkiaGraphSurface.Own(image, new(state.Bounds, GraphCoordinateSpace.Canvas, SceneMatrix.Identity,
                    target.ScaleXOf(composition), target.ScaleYOf(composition)), ShaderSurfaceKind.Color, _device, reservation, _deviceLifetime?.Acquire());
                reservation = null; return result;
            }
            catch { image.Dispose(); throw; }
        }
        finally { reservation?.Dispose(); }
    }
}
