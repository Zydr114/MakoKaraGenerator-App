using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private ImageGraphResult ExecuteObjectMaterial(RenderRequest request, PreparedMaterialBinding binding, SceneMaterialProgram program,
        SceneColorPart part, bool? sung, ImageObjectState state, SkiaGraphSurface below)
    {
        var output = request.ResolveTarget(); var composition = request.Project.Composition;
        var regionKind = binding.Slot == PreparedMaterialSlot.Stroke ? SceneCaptureRegion.Stroke : SceneCaptureRegion.Fill;
        var context = new ImageGraphFrameContext(request.TimeUs, composition.CanvasWidth, composition.CanvasHeight, program.Bounds,
            output.ScaleXOf(composition), output.ScaleYOf(composition), binding.Material.Parameters, request.TransparentBackground)
            { Uniforms = binding.Material.Uniforms };
        var definition = program.Package.GetMaterialDefinition(binding.Material.Handle);
        using var captured = CaptureSceneMaterialRegion(request, CaptureTarget(binding.Target), part, definition.Fallback, sung,
            state.Bounds, state.Budget, state.Resources);
        Dictionary<string, SkiaGraphInput> inputs = new(StringComparer.Ordinal);
        List<SkiaGraphSurface> owned = [];
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
                        var surface = CaptureSceneTarget(request, CaptureTarget(binding.Target), recipe, source, state.Bounds, state.Budget, state.Resources);
                        owned.Add(surface); inputs.Add(input.Id, new(surface, source));
                    }
                    else if (source == GraphInputSource.BackdropBelow)
                    {
                        if (binding.Owner is PreparedMaterialOwner.Script script && request.PreparedFrame!.OverlayBatches.Any(batch => batch.InstanceId == script.InstanceId && batch.Layer == OverlayLayer.BehindText && batch.ItemCount > 0))
                            throw new PreparedFrameException("ImageEffectOwnedBackdropPending", "材质下层尚不能排除自身后置粒子。");
                        inputs.Add(input.Id, new(below, source));
                    }
                    else if (source is GraphInputSource.BackgroundColor or GraphInputSource.BackgroundMedia)
                    {
                        var surface = CaptureSceneBackground(request, source, state.Bounds, state.Budget, state.Resources);
                        if (surface is not null) { owned.Add(surface); inputs.Add(input.Id, new(surface, source)); }
                    }
                    else if (source == GraphInputSource.ResourceImage && images.TryGetValue(input.Id, out var image)) inputs.Add(input.Id, image);
                }
            // 标记样式alpha已在共同opacity；元素自身alpha单独烘入颜色／擦除，各一次。
            double opacity = binding.Target is ImageEffectTarget.Token token ? request.PreparedFrame!.GetTokenDrawingStyle(token.Target).Opacity : 1;
            return program.Package.ExecuteMaterialShared(binding.Material.Handle, regionKind, context, captured.Region, captured.TargetColor,
                inputs, opacity, state.Resources, state.AuthorGraphs, request.CancellationToken);
        }
        finally { foreach (var surface in owned) surface.Dispose(); }
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
        if (request.PreparedFrame is not { Materials.IsEmpty: false } prepared) return;
        if (selected.IsDefault) selected = SelectCaptureItems(prepared.HierarchyScene, target, new(true, true, true, true, SceneCaptureRegion.FillAndStroke));
        if (prepared.Materials.Any(binding => target is SceneCaptureTarget.Token token
            ? binding.Target == new ImageEffectTarget.Token(token.Target) : selected.Any(item => Belongs(binding.Target, item))))
            throw new PreparedFrameException("MaterialProcessedCaptureRequired", "最终材质颜色须从主画布处理后的部件读取，原始捕获不能返回白色几何种子。");
    }
}
