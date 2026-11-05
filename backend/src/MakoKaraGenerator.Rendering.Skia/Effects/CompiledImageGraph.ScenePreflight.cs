using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledImageGraph
{
    // Validation-only path also runs for empty group bindings. Never executes a graph,
    // allocates a capture, invents a target bound or hides a bad inactive node.
    internal void ValidateScenePreflight(ImageGraphFrameContext frame,
        IReadOnlyDictionary<string, SkiaGraphInput> resourceImages, Func<string, SceneCaptureRecipe> captureRecipe, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            ValidateFrame(frame); ImageGraphBindings.ValidateParameters(Plan, frame.Parameters);
            MaterialUniformValidation.Validate(Plan, frame.Uniforms);
            if (Plan.Definition.AnchorMode != GraphAnchorMode.ColorOnly)
                throw Error("ImageEffectMappingPending", "anchorMode", "组调度只接入已经批准的颜色图。");
            var nodes = Plan.Nodes.ToArray();
            var transforms = ResolveTransforms(frame, nodes, cancellationToken);
            var shapes = Shapes(frame, nodes, transforms, cancellationToken);
            foreach (var node in Plan.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node is ImageGraphNode.RuntimeShader runtime)
                    foreach (var pair in ImageGraphBindings.Uniforms(runtime, Plan, frame).Values)
                        MaterialUniformValidation.Validate(Plan, runtime.Id, pair.Key, pair.Value);
            }
            foreach (var declaration in Plan.Definition.Inputs)
            {
                var source = declaration.ChoiceParameter is { } choice ? declaration.Choices[frame.Parameters.GetEnum(choice)] : declaration.Source;
                if (source != GraphInputSource.ResourceImage) continue;
                if (!resourceImages.TryGetValue(declaration.Id, out var input) || input.Source != source || input.ResourceId != declaration.ResourceId)
                    throw Error("GraphRequiredInputUnavailable", "inputs." + declaration.Id, "固定资源输入缺失或代次身份不符。");
                ValidateSurface(input.Surface, declaration.Kind, "inputs." + declaration.Id, cancellationToken);
            }
            ValidateSceneBudget(frame, resourceImages, captureRecipe, nodes, shapes, transforms, cancellationToken);
        }
    }

    private void ValidateSceneBudget(ImageGraphFrameContext frame,
        IReadOnlyDictionary<string, SkiaGraphInput> resourceImages, Func<string, SceneCaptureRecipe> captureRecipe,
        ImageGraphNode[] nodes, Dictionary<string, GraphRect> shapes,
        Dictionary<string, ResolvedGraphTransform> transforms, CancellationToken cancellationToken)
    {
        var active = ActiveReferences(Plan.Definition.Output, Plan.Definition.CompositeCoverage);
        var declarations = ActiveInputs(frame);
        Dictionary<GraphReference, BudgetInput> inputs = [];
        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = declaration.ChoiceParameter is { } choice ? declaration.Choices[frame.Parameters.GetEnum(choice)] : declaration.Source;
            BudgetInput layout;
            if (source == GraphInputSource.ResourceImage)
            {
                var surface = resourceImages[declaration.Id].Surface;
                layout = new(surface.Image.Handle, surface.Descriptor, Bytes(surface));
            }
            else
            {
                // This is the fixed graph-bound layout, not a captured image.
                // Actual nonempty inputs are rechecked by Execute's exact peak.
                var bounds = Aligned(frame.TargetBounds, frame);
                object identity = SceneCaptureChannels.IsSupported(source)
                    ? (source, (object)captureRecipe(declaration.Id)) : (source, (object)"background");
                layout = new(identity, new(bounds, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, frame.DensityX, frame.DensityY), SizeBytes(bounds, frame));
            }
            inputs.Add(GraphReference.Input(declaration.Id), layout);
        }
        // Transparent dependency bypass borrows exactly one original input and
        // never allocates the author's nodes. Keep that same budget semantics.
        bool bypass = frame.Transparent && Plan.Definition.Inputs.Any(input =>
            active.Contains(GraphReference.Input(input.Id)) && input.BackgroundRequirement != GraphBackgroundRequirement.None);
        if (bypass)
        {
            var input = inputs.Single().Value; CheckBudget(1, input.Bytes); return;
        }
        var ordered = nodes.Where(node => active.Contains(GraphReference.Node(node.Id))).ToArray();
        _ = PreflightLayout(frame, inputs, ordered, shapes,
            Uses(ordered, Plan.Definition.Output, Plan.Definition.CompositeCoverage), transforms, cancellationToken);
    }
}
