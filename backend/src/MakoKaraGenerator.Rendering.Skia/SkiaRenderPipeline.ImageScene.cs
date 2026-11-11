using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;
using static MakoKaraGenerator.Rendering.Skia.SkiaSceneGeometry;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private static readonly SceneColorPart[] ColorPartOrder = [SceneColorPart.BaseGlow, SceneColorPart.BaseStroke, SceneColorPart.BaseShadow, SceneColorPart.BaseFill,
        SceneColorPart.RubyGlow, SceneColorPart.RubyStroke, SceneColorPart.RubyShadow, SceneColorPart.RubyFill];
    private const int MaxRetainedAlphaReadbackBytes = 16 * 1024 * 1024;
    private sealed record PartSource(ImageEffectTarget Member, SceneColorPart Part);
    private sealed record SceneEffectOwner(PreparedMaterialOwner Origin, string Stage = "RenderImageEffects", Scripting.V2.TextTarget? TextTarget = null)
    {
        public SceneEffectOwner(PreparedImageEffectBinding binding)
            : this(new PreparedMaterialOwner.Script(binding.InstanceId, binding.ArtifactHash) { PackageIdentity = binding.PackageIdentity }) { }
    }
    private sealed record ColorLayer(int Order, ImmutableHashSet<PartSource> Sources, SkiaGraphSurface Surface,
        ImmutableArray<ImageGraphResult> History, CompiledImageGraph? Graph, SceneEffectOwner? Owner)
    {
        // ClosedPayload and InheritedPayload borrow from this state's Owned list.
        // Child exports clear History/Graph/ChainTarget; only this target's live chain
        // may retain History and the original independent-child inheritance floor.
        internal ImageCompositionPayload? ClosedPayload { get; init; }
        internal ImageEffectTarget? ChainTarget { get; init; }
        internal ImageCompositionPayload? InheritedPayload { get; init; }
    }
    private sealed class ImageObjectState(GraphRect bounds, GraphExecutionBudget budget, ImageSurfaceBudgetFrame resources) : IDisposable
    {
        internal GraphRect Bounds { get; } = bounds;
        internal GraphExecutionBudget Budget { get; } = budget;
        internal ImageSurfaceBudgetFrame Resources { get; } = resources;
        internal List<ColorLayer> Layers { get; } = [];
        internal List<ClosedLayerRange> ExactRanges { get; } = [];
        internal List<IDisposable> Owned { get; } = [];
        internal double Opacity { get; set; }
        internal bool Changed { get; set; }
        internal GroupCompositionBudget? ScopeBudget { get; init; }
        internal SceneBackdropFrame? Backdrop { get; init; }
        internal SceneCompositionPrefix? OutsidePrefix { get; init; }
        /// <summary>本帧对象实际区域（O7），材质据此执行；null 时沿用程序框。</summary>
        internal GraphRect? TargetRegion { get; init; }
        /// <summary>各图绑定本次执行的目标框（第 12 条）；缺项时沿用程序框。</summary>
        internal IReadOnlyDictionary<PreparedImageEffectBinding, GraphRect>? GraphTargets { get; init; }
        internal Dictionary<string, SkiaGraphSurface> ResolvedBelow { get; } = new(StringComparer.Ordinal);
        internal SceneEffectOwner? CompositionOwner { get; init; }
        private int _accountedLayers;
        internal void SetLayerBudget(int count) => ScopeBudget?.ActiveLayers(ref _accountedLayers, count);
        internal SkiaGraphSurface? Empty { get; set; }
        internal OriginalAlphaReadbackBuffer AlphaReadback { get; } = new();
        internal MaterialAuthorGraphCache AuthorGraphs { get; } = new(resources);
        internal ObjectBackgroundCache Backgrounds { get; } = new(resources);
        internal T Own<T>(T value) where T : IDisposable { Owned.Add(value); return value; }
        public void Dispose() { try { Backgrounds.Dispose(); AuthorGraphs.Dispose(); AlphaReadback.Dispose(); for (int i = Owned.Count - 1; i >= 0; i--) Owned[i].Dispose(); } finally { SetLayerBudget(0); } }
    }

    internal sealed class OriginalAlphaReadbackBuffer : IDisposable
    {
        private byte[]? _retained;
        internal byte[] Get(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            if (length > MaxRetainedAlphaReadbackBytes)
            {
                _retained = null;
                return new byte[length];
            }
            if (_retained is null || _retained.Length < length) _retained = new byte[length];
            return _retained;
        }
        public void Dispose() => _retained = null;
    }

    private ImmutableArray<RenderImageEffectDiagnostic> DrawImageEffectScene(SKSurface main, RenderRequest request, ImageSurfaceBudgetFrame resources, SceneBackdropFrame? backdropFrame, out ImmutableArray<RuntimeDiagnostic> sceneDiagnostics, out ImmutableArray<RenderMaterialDiagnostic> materialDiagnostics)
    {
        var prepared = request.PreparedFrame!;
        var allMaterials = prepared.Materials.Select(ObjectMaterialBinding.From).Concat(prepared.BaseMaterials.Select(ObjectMaterialBinding.From)).ToArray();
        var run = new ImageSceneRun(request, resources, backdropFrame, main, allMaterials);
        var sceneWarnings = ImmutableArray.CreateBuilder<RuntimeDiagnostic>();
        // 两个调度器只决定对象的先后、隔离与下层来源；每个对象怎么画由 DrawSceneObject 建计划并执行（见 ObjectPlan）。
        if (backdropFrame is not null)
            DrawGroupCompositionScene(main, request, resources, backdropFrame, (surface, item, export, prefix) => DrawSceneObject(run, surface, item, export, prefix),
                run.Consumed, run.Diagnostics, sceneWarnings);
        else DrawIsolatedScene(main, request, resources, (surface, item) => DrawSceneObject(run, surface, item), sceneWarnings);
        sceneDiagnostics = sceneWarnings.ToImmutable();
        materialDiagnostics = run.MaterialWarnings.Distinct().ToImmutableArray();
        if (allMaterials.FirstOrDefault(binding => !run.MaterialConsumed.Contains(binding)) is { } missingMaterial)
            throw missingMaterial.Owner.Failure("RenderMaterials", request.TimeUs,
                "MaterialSceneTargetUnavailable", MaterialTargetError(new PreparedFrameException("MaterialSceneTargetUnavailable", "材质目标未参与实际绘制。"), missingMaterial.TextTarget, "MaterialSceneTargetUnavailable"));
        if (prepared.ImageEffects.FirstOrDefault(binding => !run.Consumed.Contains(binding)) is { } unavailable)
            throw new EffectFailureException(unavailable.InstanceId, unavailable.ArtifactHash, "RenderImageEffects", request.TimeUs,
                "ImageEffectSceneTargetUnavailable", new PreparedFrameException("ImageEffectSceneTargetUnavailable", "图目标未参与本帧绘制，不能悄悄丢弃绑定。"), unavailable.PackageIdentity);
        return run.Diagnostics.ToImmutable();
    }
    private static bool HasOriginalGlow(RenderRequest request, SceneDrawItem item)
    {
        var prepared = request.PreparedFrame!;
        if (item.Mark is { } mark)
        {
            var target = prepared.FinalMarkGeometry.Single(value => value.Target.NodeId.Value == mark.NodeId && value.Target.CueId.Value == mark.CueId
                && value.Target.BindingId.Value == mark.Geometry.BindingId).Target;
            return (prepared.MarkStyles.Any(output => output.Target == target) ? prepared.GetMarkDrawingStyle(target) : mark.Appearance.ToLyricStyle()).Glow is { IsEnabled: true };
        }
        var source = StaticStyleProjection.LoadedSourceStyles(request.Project, prepared.HierarchyScene);
        return prepared.FinalGeometry.Where(token => token.WithRuby.BindingId.Value == item.Line!.BindingId).Any(token =>
            FamilyStyle(token).Glow is { IsEnabled: true } || prepared.BaseAppearances.ContainsKey(token.BaseOnly)
                && prepared.GetBaseDrawingStyle(token.BaseOnly).Glow is { IsEnabled: true });
        MakoKaraGenerator.Core.Projects.LyricStyle FamilyStyle(TokenGeometrySample token)
            => prepared.TokenStyles.Any(output => output.Target == token.WithRuby) ? prepared.GetTokenDrawingStyle(token.WithRuby) : source[token.WithRuby];
    }
    private GraphRect IncludeOriginalGlowBounds(RenderRequest request, SceneDrawItem item, GraphRect bounds, ImageSurfaceBudgetFrame resources)
    {
        var prepared = request.PreparedFrame!;
        var projection = _warpProjectionBudgets.GetValue(resources, static _ => new());
        if (item.Line is { } line)
        {
            var sources = StaticStyleProjection.LoadedSourceStyles(request.Project, prepared.HierarchyScene);
            foreach (var token in prepared.FinalGeometry.Where(token => token.WithRuby.BindingId.Value == line.BindingId))
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                var family = prepared.TokenStyles.Any(output => output.Target == token.WithRuby)
                    ? prepared.GetTokenDrawingStyle(token.WithRuby) : sources[token.WithRuby];
                var basic = prepared.BaseAppearances.ContainsKey(token.BaseOnly) ? prepared.GetBaseDrawingStyle(token.BaseOnly) : family;
                if (token.Clusters.IsEmpty)
                {
                    Add(basic, token.BaseGeometry);
                    foreach (var ruby in token.Ruby) Add(family, ruby.Geometry);
                }
                else foreach (var cluster in token.Clusters) Add(cluster.Target.RubyId is null ? basic : family, cluster.Geometry);
            }
        }
        else
        {
            var mark = item.Mark!;
            var final = prepared.FinalMarkGeometry.Single(value => value.Target.NodeId.Value == mark.NodeId
                && value.Target.CueId.Value == mark.CueId && value.Target.BindingId.Value == mark.Geometry.BindingId);
            var style = prepared.MarkStyles.Any(output => output.Target == final.Target)
                ? prepared.GetMarkDrawingStyle(final.Target) : mark.Appearance.ToLyricStyle();
            Add(style, final.Geometry);
        }
        return bounds;

        void Add(MakoKaraGenerator.Core.Projects.LyricStyle style, GeometrySample geometry)
        {
            if (style.Glow is not { IsEnabled: true } glow || geometry.InkBounds is not { } ink) return;
            double extent = ValidateGlow(glow);
            Rect2 decorated = new(ink.Left - extent, ink.Top - extent, ink.Right + extent, ink.Bottom + extent);
            var corners = geometry.Mapping is { } mapping ? mapping.MapRectangle(decorated, projection)
                : new[] { new Point2(decorated.Left, decorated.Top), new(decorated.Right, decorated.Top), new(decorated.Right, decorated.Bottom), new(decorated.Left, decorated.Bottom) }
                    .Select(geometry.BaseWorldMatrix.Map).ToImmutableArray();
            bounds = Union(bounds, new(corners.Min(point => point.X), corners.Min(point => point.Y), corners.Max(point => point.X), corners.Max(point => point.Y)));
        }
    }
    private static bool Belongs(ImageEffectTarget target, SceneDrawItem item) => target switch
    {
        ImageEffectTarget.Line line => item.Line?.Frame.Line?.Id == line.Target.LineId.Value && item.Line.BindingId == line.Target.BindingId.Value,
        ImageEffectTarget.Token token => item.Line?.BindingId == token.Target.BindingId.Value,
        ImageEffectTarget.Mark mark => item.Mark?.NodeId == mark.Target.NodeId.Value && item.Mark.CueId == mark.Target.CueId.Value
            && item.Mark.Geometry.BindingId == mark.Target.BindingId.Value,
        _ => false,
    };
    private static SceneCaptureTarget CaptureTarget(ImageEffectTarget target) => target switch
    {
        ImageEffectTarget.Line line => new SceneCaptureTarget.Line(line.Target),
        ImageEffectTarget.Token token => new SceneCaptureTarget.Token(token.Target),
        ImageEffectTarget.Mark mark => new SceneCaptureTarget.Mark(mark.Target),
        _ => throw new PreparedFrameException("InvalidPreparedImageEffectTarget", "没有此目标的真实字形部件。"),
    };
    private static GraphRect Union(GraphRect a, GraphRect b) => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
    private static PreparedFrameException GroupImageEffectUnsupported() => new("GroupImageEffectUnsupported",
        "R3 Core 当前不执行父组图像效果；请将组图绑定留到 R3.1 父级合成阶段。");
    private static bool IsRuby(SceneColorPart part) => part is SceneColorPart.RubyStroke or SceneColorPart.RubyShadow or SceneColorPart.RubyFill or SceneColorPart.RubyGlow;
    private unsafe bool IsEmptyOriginalSurface(SkiaGraphSurface surface, ImageObjectState state, CancellationToken cancellationToken)
    {
        var image = surface.Image;
        int rowBytes = checked(image.Width * 4);
        int length = checked(rowBytes * image.Height);
        byte[] pixels = state.AlphaReadback.Get(length);
        var device = surface.Device ?? _device;
        fixed (byte* destination = pixels)
        {
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            bool read = surface.HasOwnedImageOn(device) ? device.ReadOwnedImagePixels(image, info, (IntPtr)destination, rowBytes)
                : device.ReadPixels(image, info, (IntPtr)destination, rowBytes);
            if (!read)
                throw new InvalidOperationException($"Skia {device.Info.Backend} 图像 readback 失败（{device.Info}）。");
        }
        return IsEmptyOriginalPixels(pixels.AsSpan(0, length), rowBytes, image.Height, cancellationToken);
    }

    internal static bool IsEmptyOriginalPixels(ReadOnlySpan<byte> pixels, int rowBytes, int height, CancellationToken cancellationToken)
    {
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = pixels.Slice(y * rowBytes, rowBytes);
            for (int x = 3; x < row.Length; x += 4) if (row[x] != 0) return false;
        }
        return true;
    }
    private void DrawGraphSurface(SKCanvas canvas, SkiaGraphSurface surface, SKBlendMode mode)
    {
        var b = surface.Descriptor.Bounds;
        using var paint = new SKPaint { BlendMode = mode, IsAntialias = false };
        var image = surface.ImageForDevice(_device, out var raster);
        try { canvas.DrawImage(image, new SKRect((float)b.Left, (float)b.Top, (float)b.Right, (float)b.Bottom), paint); }
        finally { if (raster is not null && !ReferenceEquals(raster, surface.Image)) raster.Dispose(); }
    }
}
