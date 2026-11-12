using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private ImageGraphResult ExecuteObjectMaterial(RenderRequest request, ObjectMaterialBinding binding, SceneMaterialProgram program,
        SceneColorPart part, bool? sung, ImageObjectState state, SkiaGraphSurface? below, WipeBandProbe? wipeBand = null)
    {
        var output = request.ResolveTarget(); var composition = request.Project.Composition;
        var regionKind = binding.Slot == PreparedMaterialSlot.Stroke ? SceneCaptureRegion.Stroke : SceneCaptureRegion.Fill;
        var context = new ImageGraphFrameContext(request.TimeUs, composition.CanvasWidth, composition.CanvasHeight,
            MaterialTargetBounds(state.TargetRegion, program),
            output.ScaleXOf(composition), output.ScaleYOf(composition), binding.Material.Parameters, request.TransparentBackground)
            { Uniforms = binding.Material.Uniforms };
        var definition = program.Package.GetMaterialDefinition(binding.Material.Handle);
        using var captured = CaptureSceneMaterialRegion(request, binding.CaptureTarget, part, definition.Fallback, sung,
            state.Bounds, state.Budget, state.Resources, wipeBand);
        Dictionary<string, SkiaGraphInput> inputs = new(StringComparer.Ordinal);
        List<SkiaGraphSurface> owned = [];
        Dictionary<GraphInputSource, SkiaGraphSurface> backgrounds = [];
        var images = _imageEffects!.GetMaterialImages(binding.Material.Handle);
        try
        {
            // 透明模式由材质执行器整体回退；不要求本已不存在的背景。
            if (!context.Transparent || !program.Graph.ActiveInputs(context with { Transparent = false })
                    .Any(input => input.BackgroundRequirement != GraphBackgroundRequirement.None))
                foreach (var input in program.Graph.ActiveInputs(context))
                {
                    var source = input.ChoiceParameter is { } choice ? input.Choices[context.Parameters.GetEnum(choice)] : input.Source;
                    if (source == GraphInputSource.TargetColor) inputs.Add(input.Id, new(captured.TargetColor, source));
                    else if (source == GraphInputSource.TargetRegionCoverage) inputs.Add(input.Id, new(captured.Region, source));
                    else if (SceneCaptureChannels.IsSupported(source))
                    {
                        var recipe = program.GetInputRecipe(input.Id, regionKind);
                        var surface = CaptureSceneTargetCore(request, binding.CaptureTarget, recipe, source, state.Bounds, state.Budget, state.Resources, true);
                        owned.Add(surface); inputs.Add(input.Id, new(surface, source));
                    }
                    else if (source == GraphInputSource.BackdropBelow)
                    {
                        inputs.Add(input.Id, new(ResolveImageBelow(request, state, binding.Owner, below), source));
                    }
                    else if (source is GraphInputSource.BackgroundColor or GraphInputSource.BackgroundMedia)
                    {
                        if (BackgroundInput(input, source, Background) is { } background) inputs.Add(input.Id, background);
                    }
                    else if (source == GraphInputSource.ResourceImage && images.TryGetValue(input.Id, out var image)) inputs.Add(input.Id, image);
                }
            // 标记样式alpha已在共同opacity；元素自身alpha单独烘入颜色／擦除，各一次。
            double opacity = binding.CaptureTarget switch
            {
                SceneCaptureTarget.Base basic => request.PreparedFrame!.GetBaseAppearance(basic.Target).Opacity,
                SceneCaptureTarget.Token token => request.PreparedFrame!.GetTokenDrawingStyle(token.Target).Opacity,
                _ => 1,
            };
            return program.Package.ExecuteMaterialShared(binding.Material.Handle, regionKind, context, captured.Region, captured.TargetColor,
                inputs, opacity, state.Resources, state.AuthorGraphs, request.CancellationToken);
        }
        finally
        {
            foreach (var surface in owned) surface.Dispose();
            foreach (var (source, surface) in backgrounds) state.Backgrounds.Return(source, surface);
        }

        // 同一对象各次执行的捕获框与请求相同，背景捕获可共用（见 ObjectBackgroundCache）。
        SkiaGraphSurface? Background(GraphInputSource source)
        {
            if (!backgrounds.TryGetValue(source, out var surface))
            {
                surface = state.Backgrounds.Take(source) ?? CaptureSceneBackground(request, source, state.Bounds, state.Budget, state.Resources);
                if (surface is not null) backgrounds.Add(source, surface);
            }
            return surface;
        }
    }

    /// <summary>同一行／标记的多次材质执行（未唱／已唱、正文／注音）共用背景捕获，而非每次重画整框背景。</summary>
    /// <remarks>
    /// 执行期间取出独占；空闲时登记为可回收，预算不足时先被释放，因此不新增预算失败。
    /// 对象的部件全部加入后即清空，不把占用带进图效果与层组装阶段。
    /// </remarks>
    private sealed class ObjectBackgroundCache(ImageSurfaceBudgetFrame resources) : IDisposable
    {
        private readonly Dictionary<GraphInputSource, (SkiaGraphSurface Surface, IDisposable Reclaim)> _idle = [];

        internal SkiaGraphSurface? Take(GraphInputSource source)
        {
            if (!_idle.Remove(source, out var entry)) return null;
            entry.Reclaim.Dispose();
            return entry.Surface;
        }

        internal void Return(GraphInputSource source, SkiaGraphSurface surface)
        {
            if (_idle.ContainsKey(source) || resources.Closed) { surface.Dispose(); return; }
            var reclaim = resources.RegisterReclaimable(surface.Image.Handle, () => Evict(source, surface));
            _idle.Add(source, (surface, reclaim));
        }

        private void Evict(GraphInputSource source, SkiaGraphSurface surface)
        {
            if (_idle.TryGetValue(source, out var entry) && ReferenceEquals(entry.Surface, surface)) _idle.Remove(source);
            surface.Dispose();
        }

        public void Dispose()
        {
            foreach (var (surface, reclaim) in _idle.Values) { reclaim.Dispose(); surface.Dispose(); }
            _idle.Clear();
        }
    }
    private static double MaterialCommonOpacity(RenderRequest request, ImageEffectTarget member)
    {
        var scene = request.PreparedFrame!.HierarchyScene;
        double opacity = member switch
        {
            ImageEffectTarget.Token token => scene.Lines.Single(line => line.BindingId == token.Target.BindingId.Value).Transform.Opacity,
            ImageEffectTarget.Mark mark => scene.TextMarks.Single(item => item.NodeId == mark.Target.NodeId.Value && item.CueId == mark.Target.CueId.Value).Opacity
                * request.PreparedFrame.GetMarkDrawingStyle(mark.Target).Opacity,
            _ => throw new PreparedFrameException("InvalidMaterialRegionTarget", "材质没有独立行或组区域。"),
        };
        return ToOpacityByte((float)opacity) / 255d;
    }
    private static void RejectRawMaterialColor(RenderRequest request, SceneCaptureTarget target, ImmutableArray<SceneDrawItem> selected = default)
    {
        if (request.PreparedFrame is not { } prepared || prepared.Materials.IsEmpty && prepared.BaseMaterials.IsEmpty) return;
        if (selected.IsDefault) selected = SelectCaptureItems(prepared.HierarchyScene, target, new(true, true, true, true, SceneCaptureRegion.FillAndStroke));
        bool baseMaterial = prepared.BaseMaterials.Any(binding => target switch
        {
            SceneCaptureTarget.Base basic => binding.Target == basic.Target,
            SceneCaptureTarget.Token token => binding.Target == new BaseTokenTarget(token.Target.TokenId, token.Target.BindingId),
            _ => selected.Any(item => item.Line?.BindingId == binding.Target.BindingId.Value),
        });
        bool familyMaterial = prepared.Materials.Any(binding => target switch
        {
            SceneCaptureTarget.Base basic => !prepared.BaseAppearances.ContainsKey(basic.Target)
                && binding.Target == new ImageEffectTarget.Token(new(basic.Target.TokenId, basic.Target.BindingId)),
            SceneCaptureTarget.Token token => binding.Target == new ImageEffectTarget.Token(token.Target),
            _ => selected.Any(item => Belongs(binding.Target, item)),
        });
        if (baseMaterial || familyMaterial)
            throw new PreparedFrameException("MaterialProcessedCaptureRequired", "最终材质颜色须从主画布处理后的部件读取，原始捕获不能返回白色几何种子。");
    }
}
