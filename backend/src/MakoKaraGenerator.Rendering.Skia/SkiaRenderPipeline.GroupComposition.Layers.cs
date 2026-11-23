using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // Only this boundary observes existing leaf History. Each independent output is
    // copied to C/E and exported without History, Graph or ChainTarget.
    private void ExportClosedImageObject(RenderRequest request, ImageObjectState leaf, ImageObjectState destination)
    {
        var ordered = leaf.Layers.OrderBy(layer => layer.Order).ToArray();
        if (ordered.Length == 0) return;
        destination.SetLayerBudget(checked(destination.Layers.Count + ordered.Length));
        var context = CompositionContext(request, leaf.Bounds);
        // 扫字两侧合并层本身就是闭合对（P3 设计 §10.2 第 4 条），已在 leaf 的拥有表中，直接导出。
        var closed = ordered.Select(layer => layer.ClosedPayload ?? leaf.Own(layer.History.IsEmpty
            ? _compositionProgram.CaptureCompositionPayload(layer.Surface, context, leaf.Resources, cancellationToken: request.CancellationToken)
            : _compositionProgram.CaptureCompositionPayload(layer.History, leaf.Resources, cancellationToken: request.CancellationToken))).ToArray();
        // Every closed pair is now an independent copy. The original captures,
        // Below surfaces, material author cache and graph results no longer serve
        // any input: only ordered Sources/Owner metadata is used below. Release
        // them BEFORE allocating whole aggregate and opacity-resolved members.
        leaf.AuthorGraphs.Clear();
        var retained = closed.Cast<IDisposable>().ToHashSet();
        for (int index = leaf.Owned.Count - 1; index >= 0; index--)
            if (!retained.Contains(leaf.Owned[index])) leaf.Owned[index].Dispose();
        leaf.Owned.RemoveAll(value => !retained.Contains(value));
        leaf.AlphaReadback.Dispose(); leaf.ResolvedBelow.Clear(); leaf.Empty = null;
        // Preserve one complete result with common alpha applied only after
        // source-order composition. Effective members remain available to partial
        // recipes; they are not the numerical oracle for a whole capture.
        var aggregate = destination.Own(_compositionProgram.ComposePayloads(closed, leaf.Resources,
            leaf.Opacity, request.CancellationToken));
        var effective = _compositionProgram.ResolveCompositionOpacityLayers(closed, leaf.Resources, leaf.Opacity, request.CancellationToken);
        // Transfer ownership of every returned pair before any subsequent operation can throw.
        foreach (var payload in effective) destination.Own(payload);
        int first = destination.Layers.Count;
        for (int index = 0; index < ordered.Length; index++)
            destination.Layers.Add(ClosedColorLayer(destination.Layers.Count, ordered[index].Sources, effective[index], ordered[index].Owner));
        destination.ExactRanges.Add(new([.. destination.Layers.Skip(first)], aggregate));
        destination.SetLayerBudget(destination.Layers.Count);
    }

    private static ImageGraphFrameContext CompositionContext(RenderRequest request, GraphRect bounds)
    {
        var output = request.ResolveTarget(); var composition = request.Project.Composition;
        return new(request.TimeUs, composition.CanvasWidth, composition.CanvasHeight, bounds,
            output.ScaleXOf(composition), output.ScaleYOf(composition), ParameterValues.Empty, request.TransparentBackground);
    }
    private static ColorLayer ClosedColorLayer(int order, ImmutableHashSet<PartSource> sources,
        ImageCompositionPayload payload, SceneEffectOwner? owner)
        => new(order, sources, payload.Color, [], null, owner) { ClosedPayload = payload };

    private ImageCompositionPayload ComposeClosedLayers(RenderRequest request, ImageObjectState state, IEnumerable<ColorLayer> layers,
        double opacity = 1, Func<ColorLayer, ImageCompositionPayload>? payloadForLayer = null)
    {
        var ordered = layers.OrderBy(layer => layer.Order).ToArray();
        if (ordered.Length == 0)
        {
            // An empty recipe in an otherwise nonempty real group has a transparent
            // color/zero-erasure input. Empty real groups are handled before Execute.
            using var transparent = RasterSceneSurface(request, state.Bounds, state.Budget, ShaderSurfaceKind.Color,
                state.Resources, "SceneEmptyGroupRecipe", (_, _, _) => { });
            return _compositionProgram.CaptureCompositionPayload(transparent, CompositionContext(request, state.Bounds),
                state.Resources, opacity, request.CancellationToken);
        }
        return _compositionProgram.ComposePayloads(ClosedLayerPayloads(request, state, ordered, payloadForLayer).ToArray(),
            state.Resources, opacity, request.CancellationToken);
    }

    private void CloseGroupOpacity(RenderRequest request, ImageObjectState state, double opacity)
    {
        var ordered = state.Layers.OrderBy(layer => layer.Order).ToArray();
        if (ordered.Length == 0) return;
        var aggregate = state.Own(ComposeClosedLayers(request, state, ordered, opacity));
        var effective = _compositionProgram.ResolveCompositionOpacityLayers(ordered.Select(layer => layer.ClosedPayload!).ToArray(),
            state.Resources, opacity, request.CancellationToken);
        // Scope is closed: no own-chain result or intermediate capture crosses this
        // boundary. Release them before retaining only the copied effective layers.
        foreach (var payload in effective) state.Own(payload);
        var retained = effective.Cast<IDisposable>().Append(aggregate).ToHashSet();
        for (int index = state.Owned.Count - 1; index >= 0; index--)
            if (!retained.Contains(state.Owned[index])) state.Owned[index].Dispose();
        state.Owned.Clear();
        foreach (var payload in effective) state.Own(payload);
        state.Own(aggregate);
        state.Layers.Clear(); state.ExactRanges.Clear();
        for (int index = 0; index < ordered.Length; index++)
            state.Layers.Add(ClosedColorLayer(index, ordered[index].Sources, effective[index], ordered[index].Owner));
        state.ExactRanges.Add(new([.. state.Layers], aggregate));
    }

    private static bool MatchesGroupPart(PartSource part, SceneCaptureRecipe recipe, GraphInputSource source)
    {
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
    private static IEnumerable<PartSource> SelectGroupSources(RenderRequest request, ImageObjectState state,
        ImageEffectTarget.Group scope, SceneCaptureRecipe recipe, GraphInputSource source)
    {
        var items = SelectCaptureItems(request.PreparedFrame!.HierarchyScene, new SceneCaptureTarget.Group(scope.Target), recipe);
        return state.Layers.SelectMany(layer => layer.Sources)
            .Where(part => MatchesGroupPart(part, recipe, source) && items.Any(item => Belongs(part.Member, item)));
    }
    private static ColorLayer[] SelectGroupLayers(RenderRequest request, ImageObjectState state,
        ImageEffectTarget.Group scope, SceneCaptureRecipe recipe, GraphInputSource source)
    {
        var items = SelectCaptureItems(request.PreparedFrame!.HierarchyScene, new SceneCaptureTarget.Group(scope.Target), recipe);
        bool Matches(PartSource part) => MatchesGroupPart(part, recipe, source) && items.Any(item => Belongs(part.Member, item));
        var layers = state.Layers.Where(layer => layer.Sources.Any(Matches)).OrderBy(layer => layer.Order).ToArray();
        foreach (var layer in layers) if (layer.Sources.Any(part => !Matches(part))) throw PartialFusion();
        return layers;
    }
}
