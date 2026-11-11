using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    // Frame owner: the already drawn real background is captured exactly once,
    // before BehindText. Media providers and author graphs are never re-entered.
    private sealed class SceneBackdropFrame : IDisposable
    {
        private readonly SkiaRenderPipeline _pipeline;
        private readonly RenderRequest _request;
        private readonly PreparedRenderFrame _prepared;
        private readonly ImageSurfaceBudgetFrame _resources;
        private readonly SvgRenderWorkBudget _svgWork;
        private readonly SkiaGraphSurface _background;
        private readonly IDisposable _passLease;
        internal GroupCompositionBudget Work { get; }
        internal SceneEffectOwner Owner { get; }

        internal SceneBackdropFrame(SkiaRenderPipeline pipeline, RenderRequest request, ImageSurfaceBudgetFrame resources,
            SvgRenderWorkBudget svgWork, SkiaGraphSurface background, GroupCompositionBudget work, SceneEffectOwner owner, IDisposable passLease)
        { _pipeline = pipeline; _request = request; _prepared = request.PreparedFrame!; _resources = resources;
            _svgWork = svgWork; _background = background; Work = work; Owner = owner; _passLease = passLease; }

        internal SkiaGraphSurface Resolve(PreparedMaterialOwner? requester, SceneCompositionPrefix? outsidePrefix,
            GraphRect bounds, ImageGraphFrameContext context, GroupCompositionBudget work)
        {
            _request.CancellationToken.ThrowIfCancellationRequested();
            // 对象的工作框可以比进入前缀的框小（对象级计划收紧）：只要求同帧、同画布、同密度与输出模式，
            // 下层只在请求的框内解析，前缀落在框外的部分与这个对象无关。
            if (!ReferenceEquals(work, Work) || outsidePrefix is not null && outsidePrefix.Context with { TargetBounds = context.TargetBounds } != context)
                throw new PreparedFrameException("SceneBackdropContextMismatch", "下层只能借用同帧、同画布、同密度的进入前缀与共享预算。");
            EffectInstanceId? excluded = requester is PreparedMaterialOwner.Script script ? script.InstanceId : null;
            var backdrop = _pipeline.RasterSceneSurface(_request, bounds, _pipeline._surfaceBudget.Limits,
                ShaderSurfaceKind.Color, _resources, "SceneOwnedBackdrop", (canvas, _, _) =>
                {
                    _pipeline.DrawGraphSurface(canvas, _background, SKBlendMode.Src);
                    var composition = _request.Project.Composition;
                    // Prefix bounds may expand outside the output; real overlay
                    // rendering was clipped by the main canvas and must stay so.
                    canvas.ClipRect(new(0, 0, composition.CanvasWidth, composition.CanvasHeight));
                    using SKPaint paint = new() { IsAntialias = _request.Quality != RenderQuality.Draft };
                    foreach (var batch in _prepared.OverlayBatches)
                    {
                        _request.CancellationToken.ThrowIfCancellationRequested();
                        if (batch.Layer != OverlayLayer.BehindText || excluded is not null && batch.InstanceId == excluded) continue;
                        if (batch.ItemCount == 0) continue;
                        // A -> B -> A remains in original order. Charge actual
                        // replay primitives/points for all six closed kinds.
                        OverlayOperation(_prepared, batch, "BackdropReplay", () =>
                        {
                            work.Replay(batch); work.Pass();
                            _pipeline.DrawOverlayBatch(canvas, _request, _prepared, batch, paint, _resources, _svgWork);
                            return true;
                        });
                    }
                });
            if (outsidePrefix?.Payload is not { } foreground) return backdrop;
            using (backdrop)
                return _pipeline._compositionProgram.CompositePayload(foreground, backdrop, _resources,
                    cancellationToken: _request.CancellationToken, clipToBelow: bounds != outsidePrefix.Context.TargetBounds);
        }

        public void Dispose() { try { _background.Dispose(); } finally { _passLease.Dispose(); } }
    }

    private SceneBackdropFrame? CaptureSceneBackdrop(SKSurface main, RenderRequest request,
        ImageSurfaceBudgetFrame resources, SvgRenderWorkBudget svgWork)
    {
        SceneEffectOwner? owner = FindSceneBackdropOwner(request);
        if (owner is null) return null;
        try
        {
            var work = new GroupCompositionBudget();
            var composition = request.Project.Composition;
            using var state = new ImageObjectState(new(0, 0, composition.CanvasWidth, composition.CanvasHeight), _surfaceBudget.Limits, resources);
            var passLease = resources.BeginScenePassBudget(() => work.Pass());
            try
            {
                var background = SnapshotSceneBelow(main, request, state.Bounds, state);
                return new(this, request, resources, svgWork, background, work, owner, passLease);
            }
            catch { passLease.Dispose(); throw; }
        }
        catch (OperationCanceledException) { throw; }
        catch (EffectFailureException) { throw; }
        catch (SourceMaterialFailureException) { throw; }
        catch (Exception error) { throw SceneOwnerFailure(owner, owner.Stage, request.TimeUs, SceneBackdropCode(error), error); }
    }

    // The existing group scheduler also needs a foreground prefix even if its own
    // graph has no Below input. Otherwise activate only for an actually selected
    // Below input sharing an instance with a nonempty BehindText batch.
    private SceneEffectOwner? FindSceneBackdropOwner(RenderRequest request)
    {
        if (request.PreparedFrame is not { } prepared) return null;
        if (prepared.ImageEffects.FirstOrDefault(value => value.Binding.Target is ImageEffectTarget.Group) is { } group)
            return new(group);
        var overlayOwners = prepared.OverlayBatches.Where(value => value.Layer == OverlayLayer.BehindText && value.ItemCount > 0)
            .Select(value => value.InstanceId).ToHashSet();
        foreach (var binding in prepared.ImageEffects.Where(value => overlayOwners.Contains(value.InstanceId)))
        {
            var owner = new SceneEffectOwner(binding);
            try
            {
                var program = _imageEffects!.GetProgram(binding.Binding.Graph);
                var context = CompositionContext(request, program.Bounds) with
                    { Parameters = binding.Binding.Parameters, Uniforms = binding.Binding.Uniforms, Transforms = binding.Binding.Transforms };
                program.Graph.ValidateFrameBindings(context, request.CancellationToken);
                if (program.Graph.ActiveInputs(context).Any(value => SelectedInputSource(value, context.Parameters) == GraphInputSource.BackdropBelow)) return owner;
            }
            catch (OperationCanceledException) { throw; }
            catch (EffectFailureException) { throw; }
            catch (Exception error) { throw SceneOwnerFailure(owner, owner.Stage, request.TimeUs, SceneBackdropCode(error), error); }
        }
        foreach (var binding in prepared.Materials.Select(ObjectMaterialBinding.From).Concat(prepared.BaseMaterials.Select(ObjectMaterialBinding.From)))
        {
            if (binding.Owner is not PreparedMaterialOwner.Script script || !overlayOwners.Contains(script.InstanceId)) continue;
            var owner = new SceneEffectOwner(binding.Owner, "RenderMaterials", binding.TextTarget);
            try
            {
                var program = _imageEffects!.GetMaterial(binding.Material.Handle);
                var context = CompositionContext(request, program.Bounds) with { Parameters = binding.Material.Parameters, Uniforms = binding.Material.Uniforms };
                program.Graph.ValidateFrameBindings(context, request.CancellationToken);
                // Keep whole-material transparent fallback and its original producer.
                if (context.Transparent && program.Graph.ActiveInputs(context with { Transparent = false })
                    .Any(value => value.BackgroundRequirement != GraphBackgroundRequirement.None)) continue;
                if (program.Graph.ActiveInputs(context).Any(value => SelectedInputSource(value, context.Parameters) == GraphInputSource.BackdropBelow)) return owner;
            }
            catch (OperationCanceledException) { throw; }
            catch (EffectFailureException) { throw; }
            catch (Exception error) { throw SceneOwnerFailure(owner, owner.Stage, request.TimeUs, SceneBackdropCode(error), error); }
        }
        return null;
    }

    private static GraphInputSource SelectedInputSource(GraphInputDefinition declaration, ParameterValues parameters)
        => declaration.ChoiceParameter is { } choice ? declaration.Choices[parameters.GetEnum(choice)] : declaration.Source;

    private static SkiaGraphSurface ResolveImageBelow(RenderRequest request, ImageObjectState state,
        PreparedMaterialOwner? requester, SkiaGraphSurface? legacyBelow)
    {
        if (state.Backdrop is null) return legacyBelow
            ?? throw new PreparedFrameException("SceneBackdropUnavailable", "下层捕获或同帧前缀缺失。");
        string key = requester is PreparedMaterialOwner.Script script ? script.InstanceId.Value : "";
        if (state.ResolvedBelow.TryGetValue(key, out var cached)) return cached;
        // Cache lifetime is one leaf/group state with fixed entry prefix, bounds
        // and density. Different owners never reuse a filtered surface.
        var surface = state.Own(state.Backdrop.Resolve(requester, state.OutsidePrefix, state.Bounds,
            CompositionContext(request, state.Bounds), state.ScopeBudget!));
        state.ResolvedBelow.Add(key, surface); return surface;
    }

    private static string SceneBackdropCode(Exception error) => error switch
    {
        PreparedFrameException value => value.Code, ImageGraphException value => value.Code,
        ImageSurfaceBudgetException value => value.Code, EffectPackageException value => value.Code,
        ShaderProgramException value => value.Code, EffectOutputException value => value.Code,
        _ => "SceneBackdropFailed",
    };
}
