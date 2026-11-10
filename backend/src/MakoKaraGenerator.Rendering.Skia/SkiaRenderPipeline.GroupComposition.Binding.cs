using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void ApplyGroupImageBinding(RenderRequest request, PreparedImageEffectBinding binding, SceneImageGraphProgram program,
        ImageObjectState state, ImmutableArray<RenderImageEffectDiagnostic>.Builder diagnostics,
        ImmutableArray<RuntimeDiagnostic>.Builder sceneDiagnostics)
    {
        var scope = (ImageEffectTarget.Group)binding.Binding.Target;
        var context = CompositionContext(request, program.Bounds) with { Parameters = binding.Binding.Parameters, Uniforms = binding.Binding.Uniforms, Transforms = binding.Binding.Transforms };
        Dictionary<string, SkiaGraphInput> inputs = new(StringComparer.Ordinal);
        Dictionary<(GraphInputSource, SceneCaptureRecipe), SkiaGraphSurface> captures = [];
        Dictionary<GraphInputSource, SkiaGraphSurface?> backgrounds = [];
        HashSet<PartSource> selected = [];
        bool hasTargetInput = false;
        var external = _imageEffects!.GetImages(program.Handle);
        foreach (var declaration in program.Graph.ActiveInputs(context, () => BackgroundMediaAvailable(request)))
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            var source = declaration.ChoiceParameter is { } choice ? declaration.Choices[binding.Binding.Parameters.GetEnum(choice)] : declaration.Source;
            if (SceneCaptureChannels.IsSupported(source))
            {
                hasTargetInput = true;
                var recipe = program.GetInputRecipe(declaration.Id);
                var layers = SelectGroupLayers(request, state, scope, recipe, source);
                selected.UnionWith(layers.SelectMany(layer => layer.Sources));
                if (!captures.TryGetValue((source, recipe), out var surface))
                {
                    if (source == GraphInputSource.TargetColor)
                    {
                        // Color only: C_sel never contains B, even for replacement children.
                        var payload = state.Own(ComposeClosedLayers(request, state, layers)); surface = payload.Color;
                    }
                    else
                    {
                        surface = state.Own(CaptureSceneTarget(request, new SceneCaptureTarget.Group(scope.Target), recipe, source,
                            state.Bounds, program.Graph.Plan.Budget, state.Resources));
                    }
                    captures.Add((source, recipe), surface);
                }
                inputs.Add(declaration.Id, new(surface, source));
            }
            else if (source == GraphInputSource.BackdropBelow)
            {
                var owner = new PreparedMaterialOwner.Script(binding.InstanceId, binding.ArtifactHash) { PackageIdentity = binding.PackageIdentity };
                inputs.Add(declaration.Id, new(ResolveImageBelow(request, state, owner, null), source));
            }
            else if (source is GraphInputSource.BackgroundColor or GraphInputSource.BackgroundMedia)
            {
                if (BackgroundInput(declaration, source, Background) is { } background) inputs.Add(declaration.Id, background);
            }
            else if (source == GraphInputSource.ResourceImage && external.TryGetValue(declaration.Id, out var image)) inputs.Add(declaration.Id, image);
        }
        // Explicit target inputs with an empty source union do not grant
        // permission to replace unrelated descendants. Preserve all closed C/E,
        // membership, order and owners after the complete frame preflight.
        if (hasTargetInput && selected.Count == 0)
        {
            sceneDiagnostics.Add(new(binding.InstanceId, binding.ArtifactHash, "RenderImageEffects", request.TimeUs,
                new(DiagnosticSeverity.Info, "GroupImageEffectNoSelectedChildren", "组图的显式目标配方未选择实际来源；绑定已验证并消费，未执行图。"))
                { PackageIdentity = binding.PackageIdentity });
            return;
        }
        // Only a graph without active target inputs owns the whole binding scope.
        if (!hasTargetInput)
            selected.UnionWith(SelectGroupLayers(request, state, scope, new(true, true, true, true, SceneCaptureRegion.FillAndStroke),
                GraphInputSource.TargetColor).SelectMany(layer => layer.Sources));
        var chosen = state.Layers.Where(layer => layer.Sources.Overlaps(selected)).OrderBy(layer => layer.Order).ToArray();
        foreach (var layer in chosen) if (!layer.Sources.IsSubsetOf(selected)) throw PartialFusion();
        if (chosen.Length == 0) throw new PreparedFrameException("GroupCompositionNoSelectedChildren", "非空组绑定没有可选择的实际来源层。");
        state.ScopeBudget?.Result();
        var result = state.Own(program.Graph.Execute(context, inputs, state.Resources, request.CancellationToken));
        if (!result.Diagnostics.IsEmpty)
        {
            diagnostics.AddRange(result.Diagnostics.Select(diagnostic => new RenderImageEffectDiagnostic(binding.InstanceId, binding.ArtifactHash,
                binding.Binding.Graph, diagnostic.InputId, diagnostic.Reason)));
            return; // Preserve exact input identities, ordering, closed E and chain state.
        }

        bool continuation = chosen.Length == 1 && chosen[0].ChainTarget == scope && !chosen[0].History.IsEmpty;
        ImageCompositionPayload? inherited;
        ImmutableArray<ImageGraphResult> history;
        if (continuation)
        {
            inherited = chosen[0].InheritedPayload;
            history = chosen[0].History.Add(result);
        }
        else
        {
            // A wider capture closes independent branches, but a branch belonging to
            // this same target contributes only its persistent floor to the new chain.
            inherited = state.Own(ComposeClosedLayers(request, state, chosen, payloadForLayer: layer =>
                layer.ChainTarget == scope && !layer.History.IsEmpty
                    ? state.Own(_compositionProgram.CapturePersistentFloor(layer.History, layer.InheritedPayload,
                        state.Resources, request.CancellationToken))
                    : layer.ClosedPayload!));
            history = [result];
        }
        GroupCompositionBudget.Layers(history.Length);
        var own = state.Own(_compositionProgram.CaptureCompositionPayload(history, state.Resources, cancellationToken: request.CancellationToken));
        var closed = inherited is null ? own : state.Own(_compositionProgram.InheritCompositionErasure(own, inherited,
            state.Resources, request.CancellationToken));
        // A bypass returned before this point. Successful writes invalidate every
        // touched complete range; subsequent reads cannot reuse pre-graph C/E.
        InvalidateExactLayerRanges(state, chosen);
        state.Layers.RemoveAll(layer => chosen.Contains(layer));
        state.Layers.Add(new(chosen.Max(layer => layer.Order), selected.ToImmutableHashSet(), closed.Color, history, program.Graph, new(binding))
            { ClosedPayload = closed, ChainTarget = scope, InheritedPayload = inherited });
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
}
