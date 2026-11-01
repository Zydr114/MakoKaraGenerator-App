using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void ApplyImageObjectBinding(RenderRequest request, PreparedImageEffectBinding binding, SceneImageGraphProgram program,
        ImageObjectState state, SkiaGraphSurface below, ImmutableArray<RenderImageEffectDiagnostic>.Builder diagnostics)
    {
        if (program.Graph.Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
            throw new PreparedFrameException("ImageEffectMappingPending", "实际图像映射尚未接入，不能将映射图当作颜色图执行。");
        var composition = request.Project.Composition; var output = request.ResolveTarget();
        var context = new ImageGraphFrameContext(request.TimeUs, composition.CanvasWidth, composition.CanvasHeight, program.Bounds,
            output.ScaleXOf(composition), output.ScaleYOf(composition), binding.Binding.Parameters, request.TransparentBackground);
        ImageGraphBindings.ValidateParameters(program.Graph.Plan, binding.Binding.Parameters);
        var declarations = program.Graph.ActiveInputs(context);
        Dictionary<string, SkiaGraphInput> inputs = new(StringComparer.Ordinal);
        Dictionary<(GraphInputSource, SceneCaptureRecipe), SkiaGraphSurface> captures = [];
        Dictionary<GraphInputSource, SkiaGraphSurface?> backgrounds = [];
        HashSet<PartSource> selected = [];
        var external = _imageEffects!.GetImages(program.Handle);
        foreach (var declaration in declarations)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var source = declaration.ChoiceParameter is { } choice ? declaration.Choices[binding.Binding.Parameters.GetEnum(choice)] : declaration.Source;
            if (SceneCaptureChannels.IsSupported(source))
            {
                var recipe = program.GetInputRecipe(declaration.Id);
                var target = CaptureTarget(binding.Binding.Target);
                // 跨对象父组隔离后再开放；不能把未处理原始孩子作为已处理产物。
                if (recipe.IncludeDescendantGroups && SelectCaptureItems(request.PreparedFrame!.HierarchyScene, target, recipe).Length > 1)
                    throw new PreparedFrameException("ImageEffectDescendantCompositionPending", "真实运行后代的父组隔离协调尚未接入。");
                var layers = SelectImageLayers(state, binding.Binding.Target, recipe, source);
                selected.UnionWith(layers.SelectMany(layer => layer.Sources));
                if (!captures.TryGetValue((source, recipe), out var surface))
                {
                    surface = source == GraphInputSource.TargetColor
                        ? state.Own(AssembleImageLayers(request, state, layers, null))
                        : state.Own(CaptureSceneTarget(request, target, recipe, source, state.Bounds, program.Graph.Plan.Budget, state.Resources));
                    captures.Add((source, recipe), surface);
                }
                inputs.Add(declaration.Id, new(surface, source));
            }
            else if (source == GraphInputSource.BackdropBelow)
            {
                if (request.PreparedFrame!.OverlayBatches.Any(batch => batch.InstanceId == binding.InstanceId && batch.Layer == OverlayLayer.BehindText && batch.ItemCount > 0))
                    throw new PreparedFrameException("ImageEffectOwnedBackdropPending", "下层排除本实例拥有的后置粒子尚未接入，不能让效果采样自身粒子。");
                inputs.Add(declaration.Id, new(below, source));
            }
            else if (source is GraphInputSource.BackgroundColor or GraphInputSource.BackgroundMedia)
            {
                if (!backgrounds.TryGetValue(source, out var surface))
                {
                    surface = CaptureSceneBackground(request, source, state.Bounds, program.Graph.Plan.Budget, state.Resources);
                    backgrounds.Add(source, surface); if (surface is not null) state.Own(surface);
                }
                if (surface is not null) inputs.Add(declaration.Id, new(surface, source));
            }
            else if (source == GraphInputSource.ResourceImage && external.TryGetValue(declaration.Id, out var image)) inputs.Add(declaration.Id, image);
        }
        // 无目标输入的图，按用户确认替换该绑定的所有部件。
        if (selected.Count == 0)
            selected.UnionWith(SelectImageLayers(state, binding.Binding.Target, new(true, true, true, false, SceneCaptureRegion.FillAndStroke), GraphInputSource.TargetColor)
                .SelectMany(layer => layer.Sources));
        var chosen = state.Layers.Where(layer => layer.Sources.Overlaps(selected)).ToArray();
        foreach (var layer in chosen) if (!layer.Sources.IsSubsetOf(selected)) throw PartialFusion();
        var result = state.Own(program.Graph.Execute(context, inputs, state.Resources, request.CancellationToken));
        if (!result.Diagnostics.IsEmpty)
        {
            diagnostics.AddRange(result.Diagnostics.Select(diagnostic => new RenderImageEffectDiagnostic(binding.InstanceId, binding.ArtifactHash,
                binding.Binding.Graph, diagnostic.InputId, diagnostic.Reason)));
            return; // 原输入旁路不形成融合层，不重排原部件。
        }
        var history = chosen.SelectMany(layer => layer.History).Distinct().Append(result).ToImmutableArray();
        state.Layers.RemoveAll(layer => chosen.Contains(layer));
        state.Layers.Add(new(chosen.Max(layer => layer.Order), selected.ToImmutableHashSet(), result.Output, history, program.Graph, new(binding)));
        state.Changed = true;
    }
    private static ColorLayer[] SelectImageLayers(ImageObjectState state, ImageEffectTarget scope, SceneCaptureRecipe recipe, GraphInputSource source)
    {
        bool Matches(PartSource part)
        {
            if (scope is not ImageEffectTarget.Line && scope != part.Member) return false;
            if (IsRuby(part.Part) && !recipe.IncludeRuby) return false;
            bool fill = part.Part is SceneColorPart.BaseFill or SceneColorPart.RubyFill;
            bool stroke = part.Part is SceneColorPart.BaseStroke or SceneColorPart.RubyStroke;
            bool glow = part.Part is SceneColorPart.BaseGlow or SceneColorPart.RubyGlow;
            bool shadow = part.Part is SceneColorPart.BaseShadow or SceneColorPart.RubyShadow;
            return source switch
            {
                GraphInputSource.TargetColor => fill || stroke && recipe.IncludeStroke || shadow && recipe.IncludeShadow || glow && recipe.IncludeGlow,
                GraphInputSource.GlyphCoverage or GraphInputSource.SungCoverage => fill,
                GraphInputSource.StrokeCoverage => stroke,
                GraphInputSource.TargetRegionCoverage => fill && recipe.Region != SceneCaptureRegion.Stroke || stroke && recipe.Region != SceneCaptureRegion.Fill,
                _ => false,
            };
        }
        var layers = state.Layers.Where(layer => layer.Sources.Any(Matches)).OrderBy(layer => layer.Order).ToArray();
        foreach (var layer in layers) if (layer.Sources.Any(source => !Matches(source))) throw PartialFusion();
        return layers;
    }
    private static PreparedFrameException PartialFusion() => new("ImageEffectPartialFusion", "后续捕获只选择已融合图片的一部分；请调整效果顺序或捕获范围。");
}
