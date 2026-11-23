using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void ApplyImageObjectBinding(RenderRequest request, PreparedImageEffectBinding binding, SceneImageGraphProgram program,
        ImageObjectState state, SkiaGraphSurface? below, ImmutableArray<RenderImageEffectDiagnostic>.Builder diagnostics)
    {
        if (program.Graph.Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
            throw new PreparedFrameException("ImageEffectMappingPending", "实际图像映射尚未接入，不能将映射图当作颜色图执行。");
        var composition = request.Project.Composition; var output = request.ResolveTarget();
        // 第 12 条：目标框取绑定目标的实际区域（见 GraphTargetBounds），捕获与下层仍按对象工作框。
        var targetBounds = state.GraphTargets is { } targets && targets.TryGetValue(binding, out var tight) ? tight : program.Bounds;
        var context = new ImageGraphFrameContext(request.TimeUs, composition.CanvasWidth, composition.CanvasHeight, targetBounds,
            output.ScaleXOf(composition), output.ScaleYOf(composition), binding.Binding.Parameters, request.TransparentBackground)
                { Uniforms = binding.Binding.Uniforms, Transforms = binding.Binding.Transforms };
        program.Graph.ValidateFrameBindings(context, request.CancellationToken);
        var declarations = program.Graph.ActiveInputs(context, () => BackgroundMediaAvailable(request));
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
                ColorLayer[]? layers = null;
                if (source == GraphInputSource.TargetColor)
                {
                    layers = SelectImageLayers(state, binding.Binding.Target, recipe, source);
                    selected.UnionWith(layers.SelectMany(layer => layer.Sources));
                }
                else
                {
                    // 覆盖率遮罩直接由原始几何捕获，不读取已融合彩色图层；只记录其声明覆盖的部件供整图替换一致性检查。
                    selected.UnionWith(SelectImageSources(state, binding.Binding.Target, recipe, source));
                }
                if (!captures.TryGetValue((source, recipe), out var surface))
                {
                    surface = source == GraphInputSource.TargetColor
                        ? state.Own(AssembleImageLayers(request, state, layers!, null))
                        : state.Own(CaptureSceneTarget(request, target, recipe, source, state.Bounds, program.Graph.Plan.Budget, state.Resources));
                    captures.Add((source, recipe), surface);
                }
                inputs.Add(declaration.Id, new(surface, source));
            }
            else if (source == GraphInputSource.BackdropBelow)
            {
                var owner = new PreparedMaterialOwner.Script(binding.InstanceId, binding.ArtifactHash) { PackageIdentity = binding.PackageIdentity };
                inputs.Add(declaration.Id, new(ResolveImageBelow(request, state, owner, below), source));
            }
            else if (source is GraphInputSource.BackgroundColor or GraphInputSource.BackgroundMedia)
            {
                if (BackgroundInput(declaration, source, Background) is { } background) inputs.Add(declaration.Id, background);
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
        state.SetLayerBudget(state.Layers.Count);
        state.Changed = true;

        SkiaGraphSurface? Background(GraphInputSource source)
        {
            if (!backgrounds.TryGetValue(source, out var surface))
            {
                surface = CaptureSceneBackground(request, source, state.Bounds, program.Graph.Plan.Budget, state.Resources);
                backgrounds.Add(source, surface); if (surface is not null) state.Own(surface);
            }
            return surface;
        }
    }
    private static bool MatchesImagePart(ImageEffectTarget scope, SceneCaptureRecipe recipe, GraphInputSource source, PartSource part)
    {
        if (scope is not ImageEffectTarget.Line && scope != part.Member) return false;
        if (IsRuby(part.Part) && !recipe.IncludeRuby) return false;
        bool fill = part.Part is SceneColorPart.BaseFill or SceneColorPart.RubyFill;
        bool stroke = IsStrokePart(part.Part);
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
    private static IEnumerable<PartSource> SelectImageSources(ImageObjectState state, ImageEffectTarget scope, SceneCaptureRecipe recipe, GraphInputSource source)
        => state.Layers.SelectMany(layer => layer.Sources).Where(part => MatchesImagePart(scope, recipe, source, part));
    private static ColorLayer[] SelectImageLayers(ImageObjectState state, ImageEffectTarget scope, SceneCaptureRecipe recipe, GraphInputSource source)
    {
        bool Matches(PartSource part) => MatchesImagePart(scope, recipe, source, part);
        var layers = state.Layers.Where(layer => layer.Sources.Any(Matches)).OrderBy(layer => layer.Order).ToArray();
        foreach (var layer in layers) if (layer.Sources.Any(part => !Matches(part))) throw PartialFusion();
        return layers;
    }
    private static PreparedFrameException PartialFusion() => new("ImageEffectPartialFusion", "后续捕获只选择已融合图片的一部分；请调整效果顺序或捕获范围。");
}
