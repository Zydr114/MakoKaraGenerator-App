using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

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
        var diagnostics = ImmutableArray.CreateBuilder<RenderImageEffectDiagnostic>();
        var materialWarnings = ImmutableArray.CreateBuilder<RenderMaterialDiagnostic>();
        var allMaterials = prepared.Materials.Select(ObjectMaterialBinding.From).Concat(prepared.BaseMaterials.Select(ObjectMaterialBinding.From)).ToArray();
        HashSet<ObjectMaterialBinding> materialConsumed = [];
        HashSet<PreparedImageEffectBinding> consumed = [];
        var sceneSurface = main;
        var sceneWarnings = ImmutableArray.CreateBuilder<RuntimeDiagnostic>();
        if (backdropFrame is not null)
            DrawGroupCompositionScene(main, request, resources, backdropFrame, (surface, item, export, prefix) => DrawObject(surface, item, export, prefix),
                consumed, diagnostics, sceneWarnings);
        else DrawIsolatedScene(main, request, resources, (surface, item) => DrawObject(surface, item), sceneWarnings);
        sceneDiagnostics = sceneWarnings.ToImmutable();
        materialDiagnostics = materialWarnings.Distinct().ToImmutableArray();
        if (allMaterials.FirstOrDefault(binding => !materialConsumed.Contains(binding)) is { } missingMaterial)
            throw missingMaterial.Owner.Failure("RenderMaterials", request.TimeUs,
                "MaterialSceneTargetUnavailable", MaterialTargetError(new PreparedFrameException("MaterialSceneTargetUnavailable", "材质目标未参与实际绘制。"), missingMaterial.TextTarget, "MaterialSceneTargetUnavailable"));
        if (prepared.ImageEffects.FirstOrDefault(binding => !consumed.Contains(binding)) is { } unavailable)
            throw new EffectFailureException(unavailable.InstanceId, unavailable.ArtifactHash, "RenderImageEffects", request.TimeUs,
                "ImageEffectSceneTargetUnavailable", new PreparedFrameException("ImageEffectSceneTargetUnavailable", "图目标未参与本帧绘制，不能悄悄丢弃绑定。"), unavailable.PackageIdentity);
        return diagnostics.ToImmutable();

        void DrawObject(SKSurface main, SceneDrawItem item, ImageObjectState? export = null, SceneCompositionPrefix? outsidePrefix = null)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var bindings = prepared.ImageEffects.Where(binding => Belongs(binding.Binding.Target, item)).ToArray();
            var materials = allMaterials.Where(binding => Belongs(binding.Target, item)).ToArray();
            if (bindings.Length == 0 && materials.Length == 0 && export is null) { DrawOriginal(); return; }
            consumed.UnionWith(bindings); materialConsumed.UnionWith(materials);
            SceneEffectOwner owner = bindings.Length > 0 ? new(bindings[0]) : materials.Length > 0
                ? new(materials[0].Owner, "RenderMaterials", materials[0].TextTarget) : export!.CompositionOwner!;
            try
            {
                var programs = bindings.Select(binding => { owner = new(binding); return _imageEffects!.GetProgram(binding.Binding.Graph); }).ToArray();
                var materialPrograms = materials.Select(binding => { owner = new(binding.Owner, "RenderMaterials", binding.TextTarget); return _imageEffects!.GetMaterial(binding.Material.Handle); }).ToArray();
                // O7：材质按本帧对象实际区域执行，两条调度路径一致；独立对象的工作框随之收紧，组合导出沿用调度器给定的框。
                var region = materials.Length > 0 ? ObjectTargetRegion(request, item, resources) : null;
                var bounds = export?.Bounds ?? programs.Select(program => program.Bounds.Expand(program.Graph.Plan.Definition.MaximumExpandPx))
                    .Concat(materialPrograms.Select(program => MaterialWorkBounds(region, program))).Aggregate(Union);
                bool hasGlow = HasOriginalGlow(request, item);
                if (hasGlow) bounds = IncludeOriginalGlowBounds(request, item, bounds, resources);
                if (export is null && region is not null) bounds = AlignToOutputPixels(request, bounds);
                using var state = new ImageObjectState(bounds, _surfaceBudget.Limits, resources)
                { ScopeBudget = export?.ScopeBudget, Backdrop = backdropFrame, OutsidePrefix = outsidePrefix, TargetRegion = region };
                var target = item.Line is { } line
                    ? (ImageEffectTarget)new ImageEffectTarget.Line(new(new(line.Frame.Line!.Id), new(line.BindingId!)))
                    : new ImageEffectTarget.Mark(new(new(item.Mark!.NodeId), new(item.Mark.CueId), new(item.Mark.Geometry.BindingId!)));
                var tokenTargets = bindings.Select(binding => binding.Binding.Target).Concat(materials.Select(binding => binding.Target)).OfType<ImageEffectTarget.Token>()
                    .Distinct().OrderBy(token => Array.FindIndex(item.Line!.Frame.Line!.Tokens.ToArray(), source => source.Id == token.Target.TokenId.Value)).ToArray();
                HashSet<string> excluded = tokenTargets.Select(token => token.Target.TokenId.Value).ToHashSet(StringComparer.Ordinal);
                // Prefix scheduling exports C/E and resolves Below lazily per owner.
                // Legacy scheduling retains its real destination snapshot.
                using var capturedBelow = backdropFrame is null ? SnapshotSceneBelow(main, request, bounds, state) : null;
                var below = capturedBelow;
                var knownEmpty = SkipKnownEmptyParts ? ObjectKnownEmptyParts(request, item) : default;
                bool opacityKnown = false;
                AddParts(target, excluded, 0);
                for (int i = 0; i < tokenTargets.Length; i++) AddParts(tokenTargets[i], null, i + 1);
                state.Backgrounds.Dispose();

                // 子元素先完成，再把真实产物交给整行图；每个目标内保留工程实例顺序。
                foreach (var token in tokenTargets)
                    foreach (var binding in bindings.Where(binding => binding.Binding.Target == token)) Apply(binding);
                foreach (var binding in bindings.Where(binding => binding.Binding.Target == target)) Apply(binding);
                if (!state.Changed && export is null) { DrawOriginal(); return; }
                if (export is not null) { state.SetLayerBudget(state.Layers.Count); ExportClosedImageObject(request, state, export); return; }

                using var processed = AssembleImageLayers(request, state, state.Layers, below!);
                using var final = ApplyImageObjectOpacity(request, state, processed, below!);
                DrawGraphSurface(main.Canvas, final, SKBlendMode.Src);

                void AddParts(ImageEffectTarget member, HashSet<string>? exclusions, int memberOrder)
                {
                    for (int partOrder = 0; partOrder < ColorPartOrder.Length; partOrder++)
                    {
                        var part = ColorPartOrder[partOrder];
                        if (!hasGlow && part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow) continue;
                        if (member is ImageEffectTarget.Mark && IsRuby(part)) continue;
                        // An explicit Base appearance shadows the parent's slot even when it is Solid/Gradient/null.
                        var captureTarget = member is ImageEffectTarget.Token token && !IsRuby(part)
                            && prepared.BaseAppearances.ContainsKey(new(token.Target.TokenId, token.Target.BindingId))
                            ? (SceneCaptureTarget)new SceneCaptureTarget.Base(new(token.Target.TokenId, token.Target.BindingId)) : CaptureTarget(member);
                        var slotBindings = materials.Where(binding => binding.CaptureTarget == captureTarget && (part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke
                            ? binding.Slot == PreparedMaterialSlot.Stroke : part is SceneColorPart.BaseFill or SceneColorPart.RubyFill
                                && binding.Slot != PreparedMaterialSlot.Stroke)).ToArray();
                        int order = partOrder * (tokenTargets.Length + 1) + memberOrder;
                        // F3：本帧必然为空的部件先判空再分配，不捕获、不执行材质，只占共享空层；层数与层序不变。
                        if (knownEmpty.Contains(part, slotBindings.Length > 0))
                        {
                            if (slotBindings.Length > 0 && part is SceneColorPart.BaseFill or SceneColorPart.RubyFill) { AddEmpty(false); AddEmpty(true); }
                            else AddEmpty(null);
                            continue;
                        }
                        if (slotBindings.Length > 0)
                        {
                            if (part is SceneColorPart.BaseFill or SceneColorPart.RubyFill)
                            {
                                AddSide(false); AddSide(true);
                            }
                            else AddMaterial(slotBindings[0], null);
                            continue;
                        }
                        AddOriginal(null);
                        void AddSide(bool sung)
                        {
                            var binding = slotBindings.SingleOrDefault(binding => binding.Slot == (sung ? PreparedMaterialSlot.SungFill : PreparedMaterialSlot.UnsungFill));
                            if (binding is null) AddOriginal(sung); else AddMaterial(binding, sung);
                        }
                        void AddMaterial(ObjectMaterialBinding binding, bool? sung)
                        {
                            owner = new(binding.Owner, "RenderMaterials", binding.TextTarget);
                            var program = _imageEffects!.GetMaterial(binding.Material.Handle);
                            // Admit the actual additional source layer before any material input is captured.
                            state.SetLayerBudget(checked(state.Layers.Count + 1));
                            state.ScopeBudget?.Result();
                            var result = state.Own(ExecuteObjectMaterial(request, binding, program, part, sung, state, below));
                            materialWarnings.AddRange(result.Diagnostics.Select(diagnostic => new RenderMaterialDiagnostic(binding.Owner,
                                binding.Material.Handle, diagnostic.InputId, diagnostic.Reason)));
                            SetOpacity(MaterialCommonOpacity(request, member));
                            state.Layers.Add(new(order, [new(member, part)], result.Output, [result], program.Graph, owner));
                            state.Changed = true;
                        }
                        bool AddOriginal(bool? sung)
                        {
                            // Empty parts are still real source layers; admission must precede capture.
                            state.SetLayerBudget(checked(state.Layers.Count + 1));
                            // 检查关闭时不为共享空表面同步读回：未发起任何部件绘制即可证明仍是初始透明；
                            // 其余按非空保留独立表面，像素不变，只可能比开启时多占表面账。
                            var probe = _graphPixelChecks ? null : new CaptureDrawProbe();
                            var captured = CaptureColorPart(request, CaptureTarget(member), part, bounds, state.Budget, resources, exclusions, sung, probe);
                            state.Own(captured);
                            SetOpacity(captured.QuantizedOpacity);
                            var surface = captured.Surface;
                            bool empty = IsRuby(part) && item.Line?.Frame.Line?.RubyAnnotations.IsEmpty == true;
                            if (!empty && probe is null)
                            {
                                empty = IsEmptyOriginalSurface(surface, state, request.CancellationToken);
                            }
                            else
                            {
                                empty |= probe?.Drew == false;
                                request.CancellationToken.ThrowIfCancellationRequested();
                            }
                            if (empty)
                            {
                                if (state.Empty is null) state.Empty = surface;
                                else { captured.Dispose(); surface = state.Empty; }
                            }
                            state.Layers.Add(new(order, [new(member, part)], surface, [], null, null));
                            return empty;
                        }
                        void AddEmpty(bool? sung)
                        {
                            // 检查模式按原样部件捕获（表面与读回同原来）并核对判空条件没有漏掉样式来源；注音材质同样不执行。
                            if (_graphPixelChecks)
                            {
                                if (!AddOriginal(sung))
                                    throw new PreparedFrameException("SceneColorPartKnownEmptyMismatch", "预判为空的对象部件实际有像素。");
                                return;
                            }
                            state.SetLayerBudget(checked(state.Layers.Count + 1));
                            state.Empty ??= state.Own(RasterSceneSurface(request, bounds, state.Budget, ShaderSurfaceKind.Color, resources,
                                "CaptureSceneColorPart", static (_, _, _) => { }));
                            state.Layers.Add(new(order, [new(member, part)], state.Empty, [], null, null));
                        }
                    }
                    // 每个成员的填充部件从不预判为空，共同透明度总能取到；跳过的空层不参与核对。
                    void SetOpacity(double value)
                    {
                        if (!opacityKnown) { state.Opacity = value; opacityKnown = true; }
                        else if (state.Opacity != value) throw new PreparedFrameException("SceneColorPartFrameMismatch", "对象部件共同透明度不一致。");
                    }
                }
                void Apply(PreparedImageEffectBinding binding)
                {
                    owner = new(binding);
                    var program = _imageEffects!.GetProgram(binding.Binding.Graph);
                    state.ScopeBudget?.Result();
                    ApplyImageObjectBinding(request, binding, program, state, below, diagnostics);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (EffectFailureException) { throw; }
            catch (SourceMaterialFailureException) { throw; }
            catch (Exception error)
            {
                string code = error switch { PreparedFrameException e => e.Code, ImageGraphException e => e.Code,
                    ImageSurfaceBudgetException e => e.Code, EffectPackageException e => e.Code, ShaderProgramException e => e.Code,
                    EffectOutputException e => e.Code,
                    _ => "SceneImageCompositionFailed" };
                throw SceneOwnerFailure(owner, owner.Stage, request.TimeUs, code, error);
            }
            void DrawOriginal()
            {
                var output = request.ResolveTarget();
                CapturePaint? paint = ReferenceEquals(main, sceneSurface) ? null : new(GraphInputSource.TargetColor,
                    new(true, true, true, false, SceneCaptureRegion.FillAndStroke), null, resources, checked((long)output.PixelWidth * output.PixelHeight * 4));
                if (item.Line is { } line) DrawSceneLine(main.Canvas, request, request.Project.Composition, line, paint, resources);
                else DrawTextMark(main.Canvas, request, request.Project.Composition, item.Mark!, paint, resources);
            }
        }
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
