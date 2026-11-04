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
    private sealed record SceneEffectOwner(PreparedMaterialOwner Origin, string Stage = "RenderImageEffects")
    {
        public SceneEffectOwner(PreparedImageEffectBinding binding)
            : this(new PreparedMaterialOwner.Script(binding.InstanceId, binding.ArtifactHash) { PackageIdentity = binding.PackageIdentity }) { }
    }
    private sealed record ColorLayer(int Order, ImmutableHashSet<PartSource> Sources, SkiaGraphSurface Surface,
        ImmutableArray<ImageGraphResult> History, CompiledImageGraph? Graph, SceneEffectOwner? Owner);
    private sealed class ImageObjectState(GraphRect bounds, GraphExecutionBudget budget, ImageSurfaceBudgetFrame resources) : IDisposable
    {
        internal GraphRect Bounds { get; } = bounds;
        internal GraphExecutionBudget Budget { get; } = budget;
        internal ImageSurfaceBudgetFrame Resources { get; } = resources;
        internal List<ColorLayer> Layers { get; } = [];
        internal List<IDisposable> Owned { get; } = [];
        internal double Opacity { get; set; }
        internal bool Changed { get; set; }
        internal SkiaGraphSurface? Empty { get; set; }
        internal OriginalAlphaReadbackBuffer AlphaReadback { get; } = new();
        internal MaterialAuthorGraphCache AuthorGraphs { get; } = new(resources);
        internal T Own<T>(T value) where T : IDisposable { Owned.Add(value); return value; }
        public void Dispose() { AuthorGraphs.Dispose(); AlphaReadback.Dispose(); for (int i = Owned.Count - 1; i >= 0; i--) Owned[i].Dispose(); }
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

    private ImmutableArray<RenderImageEffectDiagnostic> DrawImageEffectScene(SKSurface main, RenderRequest request, ImageSurfaceBudgetFrame resources, out ImmutableArray<RuntimeDiagnostic> sceneDiagnostics, out ImmutableArray<RenderMaterialDiagnostic> materialDiagnostics)
    {
        var prepared = request.PreparedFrame!;
        if (prepared.ImageEffects.FirstOrDefault(binding => binding.Binding.Target is ImageEffectTarget.Group) is { } groupBinding)
            throw new EffectFailureException(groupBinding.InstanceId, groupBinding.ArtifactHash, "RenderImageEffects", request.TimeUs,
                "GroupImageEffectUnsupported", GroupImageEffectUnsupported(), groupBinding.PackageIdentity);
        var diagnostics = ImmutableArray.CreateBuilder<RenderImageEffectDiagnostic>();
        var materialWarnings = ImmutableArray.CreateBuilder<RenderMaterialDiagnostic>();
        HashSet<PreparedMaterialBinding> materialConsumed = [];
        HashSet<PreparedImageEffectBinding> consumed = [];
        var sceneSurface = main;
        var sceneWarnings = ImmutableArray.CreateBuilder<RuntimeDiagnostic>();
        DrawIsolatedScene(main, request, resources, DrawObject, sceneWarnings);
        sceneDiagnostics = sceneWarnings.ToImmutable();
        materialDiagnostics = materialWarnings.Distinct().ToImmutableArray();
        if (prepared.Materials.FirstOrDefault(binding => !materialConsumed.Contains(binding)) is { } missingMaterial)
            throw missingMaterial.Owner.Failure("RenderMaterials", request.TimeUs,
                "MaterialSceneTargetUnavailable", new PreparedFrameException("MaterialSceneTargetUnavailable", "材质目标未参与实际绘制。"));
        if (prepared.ImageEffects.FirstOrDefault(binding => !consumed.Contains(binding)) is { } unavailable)
            throw new EffectFailureException(unavailable.InstanceId, unavailable.ArtifactHash, "RenderImageEffects", request.TimeUs,
                "ImageEffectSceneTargetUnavailable", new PreparedFrameException("ImageEffectSceneTargetUnavailable", "图目标未参与本帧绘制，不能悄悄丢弃绑定。"), unavailable.PackageIdentity);
        return diagnostics.ToImmutable();

        void DrawObject(SKSurface main, SceneDrawItem item)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var bindings = prepared.ImageEffects.Where(binding => Belongs(binding.Binding.Target, item)).ToArray();
            var materials = prepared.Materials.Where(binding => Belongs(binding.Target, item)).ToArray();
            if (bindings.Length == 0 && materials.Length == 0) { DrawOriginal(); return; }
            consumed.UnionWith(bindings); materialConsumed.UnionWith(materials);
            SceneEffectOwner owner = bindings.Length > 0 ? new(bindings[0]) : new(materials[0].Owner, "RenderMaterials");
            try
            {
                var programs = bindings.Select(binding => { owner = new(binding); return _imageEffects!.GetProgram(binding.Binding.Graph); }).ToArray();
                var materialPrograms = materials.Select(binding => { owner = new(binding.Owner, "RenderMaterials"); return _imageEffects!.GetMaterial(binding.Material.Handle); }).ToArray();
                var bounds = programs.Select(program => program.Bounds.Expand(program.Graph.Plan.Definition.MaximumExpandPx))
                    .Concat(materialPrograms.Select(program => program.Bounds)).Aggregate(Union);
                bool hasGlow = HasOriginalGlow(request, item);
                if (hasGlow) bounds = IncludeOriginalGlowBounds(request, item, bounds);
                using var state = new ImageObjectState(bounds, _surfaceBudget.Limits, resources);
                var target = item.Line is { } line
                    ? (ImageEffectTarget)new ImageEffectTarget.Line(new(new(line.Frame.Line!.Id), new(line.BindingId!)))
                    : new ImageEffectTarget.Mark(new(new(item.Mark!.NodeId), new(item.Mark.CueId), new(item.Mark.Geometry.BindingId!)));
                var tokenTargets = bindings.Select(binding => binding.Binding.Target).Concat(materials.Select(binding => binding.Target)).OfType<ImageEffectTarget.Token>()
                    .Distinct().OrderBy(token => Array.FindIndex(item.Line!.Frame.Line!.Tokens.ToArray(), source => source.Id == token.Target.TokenId.Value)).ToArray();
                HashSet<string> excluded = tokenTargets.Select(token => token.Target.TokenId.Value).ToHashSet(StringComparer.Ordinal);
                using var below = SnapshotSceneBelow(main, request, bounds, state);
                AddParts(target, excluded, 0);
                for (int i = 0; i < tokenTargets.Length; i++) AddParts(tokenTargets[i], null, i + 1);

                // 子元素先完成，再把真实产物交给整行图；每个目标内保留工程实例顺序。
                foreach (var token in tokenTargets)
                    foreach (var binding in bindings.Where(binding => binding.Binding.Target == token)) Apply(binding);
                foreach (var binding in bindings.Where(binding => binding.Binding.Target == target)) Apply(binding);
                if (!state.Changed) { DrawOriginal(); return; }

                using var processed = AssembleImageLayers(request, state, state.Layers, below);
                using var final = ApplyImageObjectOpacity(request, state, processed, below);
                DrawGraphSurface(main.Canvas, final, SKBlendMode.Src);

                void AddParts(ImageEffectTarget member, HashSet<string>? exclusions, int memberOrder)
                {
                    for (int partOrder = 0; partOrder < ColorPartOrder.Length; partOrder++)
                    {
                        var part = ColorPartOrder[partOrder];
                        if (!hasGlow && part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow) continue;
                        if (member is ImageEffectTarget.Mark && IsRuby(part)) continue;
                        var slotBindings = materials.Where(binding => binding.Target == member && (part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke
                            ? binding.Slot == PreparedMaterialSlot.Stroke : part is SceneColorPart.BaseFill or SceneColorPart.RubyFill
                                && binding.Slot != PreparedMaterialSlot.Stroke)).ToArray();
                        int order = partOrder * (tokenTargets.Length + 1) + memberOrder;
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
                        void AddMaterial(PreparedMaterialBinding binding, bool? sung)
                        {
                            owner = new(binding.Owner, "RenderMaterials");
                            var program = _imageEffects!.GetMaterial(binding.Material.Handle);
                            var result = state.Own(ExecuteObjectMaterial(request, binding, program, part, sung, state, below));
                            materialWarnings.AddRange(result.Diagnostics.Select(diagnostic => new RenderMaterialDiagnostic(binding.Owner,
                                binding.Material.Handle, diagnostic.InputId, diagnostic.Reason)));
                            SetOpacity(MaterialCommonOpacity(request, member));
                            state.Layers.Add(new(order, [new(member, part)], result.Output, [result], program.Graph, owner));
                            state.Changed = true;
                        }
                        void AddOriginal(bool? sung)
                        {
                            var captured = CaptureColorPart(request, CaptureTarget(member), part, bounds, state.Budget, resources, exclusions, sung);
                            state.Own(captured);
                            SetOpacity(captured.QuantizedOpacity);
                            var surface = captured.Surface;
                            bool empty = IsRuby(part) && item.Line?.Frame.Line?.RubyAnnotations.IsEmpty == true;
                            if (!empty)
                            {
                                empty = IsEmptyOriginalSurface(surface, state, request.CancellationToken);
                            }
                            else request.CancellationToken.ThrowIfCancellationRequested();
                            if (empty)
                            {
                                if (state.Empty is null) state.Empty = surface;
                                else { captured.Dispose(); surface = state.Empty; }
                            }
                            state.Layers.Add(new(order, [new(member, part)], surface, [], null, null));
                        }
                    }
                    void SetOpacity(double value)
                    {
                        if (state.Layers.Count == 0) state.Opacity = value;
                        else if (state.Opacity != value) throw new PreparedFrameException("SceneColorPartFrameMismatch", "对象部件共同透明度不一致。");
                    }
                }
                void Apply(PreparedImageEffectBinding binding)
                {
                    owner = new(binding);
                    ApplyImageObjectBinding(request, binding, _imageEffects!.GetProgram(binding.Binding.Graph), state, below, diagnostics);
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
                throw owner.Origin.Failure(owner.Stage, request.TimeUs, code, error);
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
        if (item.Mark is { } mark) return mark.Appearance.Glow is { IsEnabled: true };
        var line = item.Line!;
        var basis = MakoKaraGenerator.Core.Projects.LyricStyleResolver.Resolve(request.Project.LyricStyle, preset: null,
            lineOverride: line.Frame.Line!.StyleOverride, branchOverride: line.Branch.StyleOverride).Style;
        return basis.Glow is { IsEnabled: true } || line.Frame.Line.Tokens.Any(token =>
            token.StyleOverride is { Glow.IsSet: true } overlay && ResolveElementStyle(basis, overlay).Glow is { IsEnabled: true });
    }
    private static GraphRect IncludeOriginalGlowBounds(RenderRequest request, SceneDrawItem item, GraphRect bounds)
    {
        var prepared = request.PreparedFrame!;
        if (item.Line is { } line)
        {
            var basis = MakoKaraGenerator.Core.Projects.LyricStyleResolver.Resolve(request.Project.LyricStyle, preset: null,
                lineOverride: line.Frame.Line!.StyleOverride, branchOverride: line.Branch.StyleOverride).Style;
            if (basis.Glow is not { IsEnabled: true } && !line.Frame.Line.Tokens.Any(token => token.StyleOverride is { Glow.IsSet: true })) return bounds;
            var geometry = line.Geometry!;
            Add(basis, new(geometry.TopLeft.X, geometry.TopLeft.Y, geometry.BottomRight.X, geometry.BottomRight.Y),
                new(geometry.WorldMatrix!.Value.A, geometry.WorldMatrix.Value.B, geometry.WorldMatrix.Value.C,
                    geometry.WorldMatrix.Value.D, geometry.WorldMatrix.Value.Tx, geometry.WorldMatrix.Value.Ty));
            foreach (var token in prepared.FinalGeometry.Where(token => token.WithRuby.BindingId.Value == line.BindingId))
            {
                var source = line.Frame.Line.Tokens.First(value => value.Id == token.WithRuby.TokenId.Value);
                var style = prepared.TokenStyles.Any(output => output.Target == token.WithRuby)
                    ? prepared.GetTokenDrawingStyle(token.WithRuby) : source.StyleOverride is { } overlay ? ResolveElementStyle(basis, overlay) : basis;
                if (token.FamilyGeometry.InkBounds is { } ink) Add(style, ink, token.FamilyGeometry.BaseWorldMatrix);
            }
        }
        else
        {
            var mark = item.Mark!;
            var final = prepared.FinalMarkGeometry.Single(value => value.Target.NodeId.Value == mark.NodeId
                && value.Target.CueId.Value == mark.CueId && value.Target.BindingId.Value == mark.Geometry.BindingId);
            var style = prepared.MarkStyles.Any(output => output.Target == final.Target)
                ? prepared.GetMarkDrawingStyle(final.Target) : mark.Appearance.ToLyricStyle();
            if (final.Geometry.InkBounds is { } ink) Add(style, ink, final.Geometry.BaseWorldMatrix);
        }
        return bounds;

        void Add(MakoKaraGenerator.Core.Projects.LyricStyle style, Rect2 ink, Affine2 matrix)
        {
            if (style.Glow is not { IsEnabled: true } glow) return;
            double extent = ValidateGlow(glow) + 2;
            Point2[] corners = [matrix.Map(new(ink.Left - extent, ink.Top - extent)), matrix.Map(new(ink.Right + extent, ink.Top - extent)),
                matrix.Map(new(ink.Right + extent, ink.Bottom + extent)), matrix.Map(new(ink.Left - extent, ink.Bottom + extent))];
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
