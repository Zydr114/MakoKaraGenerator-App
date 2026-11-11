using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>一次图场景绘制内各对象共用的输入与收集器。</summary>
    private sealed class ImageSceneRun(RenderRequest request, ImageSurfaceBudgetFrame resources, SceneBackdropFrame? backdrop,
        SKSurface sceneSurface, ObjectMaterialBinding[] materials)
    {
        internal RenderRequest Request { get; } = request;
        internal ImageSurfaceBudgetFrame Resources { get; } = resources;
        /// <summary>前缀调度的下层帧；遗留调度为 null。</summary>
        internal SceneBackdropFrame? Backdrop { get; } = backdrop;
        /// <summary>最终画面所在的表面；对象画到别处（组隔离的临时表面）时原样绘制要记入捕获账。</summary>
        internal SKSurface SceneSurface { get; } = sceneSurface;
        /// <summary>本帧全部材质绑定（脚本材质与基础字材质）。</summary>
        internal ObjectMaterialBinding[] Materials { get; } = materials;
        internal HashSet<PreparedImageEffectBinding> Consumed { get; } = [];
        internal HashSet<ObjectMaterialBinding> MaterialConsumed { get; } = [];
        internal ImmutableArray<RenderImageEffectDiagnostic>.Builder Diagnostics { get; } = ImmutableArray.CreateBuilder<RenderImageEffectDiagnostic>();
        internal ImmutableArray<RenderMaterialDiagnostic>.Builder MaterialWarnings { get; } = ImmutableArray.CreateBuilder<RenderMaterialDiagnostic>();
    }

    /// <summary>画一个对象：没有图、材质且不在组合导出中时原样绘制，否则先建计划再执行。</summary>
    private void DrawSceneObject(ImageSceneRun run, SKSurface main, SceneDrawItem item, ImageObjectState? export = null, SceneCompositionPrefix? outsidePrefix = null)
    {
        var request = run.Request; var prepared = request.PreparedFrame!;
        request.CancellationToken.ThrowIfCancellationRequested();
        var bindings = prepared.ImageEffects.Where(binding => Belongs(binding.Binding.Target, item)).ToArray();
        var materials = run.Materials.Where(binding => Belongs(binding.Target, item)).ToArray();
        if (bindings.Length == 0 && materials.Length == 0 && export is null) { DrawOriginalObject(run, main, item); return; }
        run.Consumed.UnionWith(bindings); run.MaterialConsumed.UnionWith(materials);
        ObjectOwnerCell owner = new(bindings.Length > 0 ? new(bindings[0]) : materials.Length > 0
            ? new(materials[0].Owner, "RenderMaterials", materials[0].TextTarget) : export!.CompositionOwner!);
        try
        {
            var plan = BuildObjectPlan(run, item, bindings, materials, export, owner);
            owner.Value = plan.ExecutionOwner;
            ExecuteObjectPlan(run, main, plan, export, outsidePrefix, owner);
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
            throw SceneOwnerFailure(owner.Value, owner.Value.Stage, request.TimeUs, code, error);
        }
    }

    private void DrawOriginalObject(ImageSceneRun run, SKSurface main, SceneDrawItem item)
    {
        var request = run.Request; var resources = run.Resources;
        var output = request.ResolveTarget();
        CapturePaint? paint = ReferenceEquals(main, run.SceneSurface) ? null : new(GraphInputSource.TargetColor,
            new(true, true, true, false, SceneCaptureRegion.FillAndStroke), null, resources, checked((long)output.PixelWidth * output.PixelHeight * 4));
        if (item.Line is { } line) DrawSceneLine(main.Canvas, request, request.Project.Composition, line, paint, resources);
        else DrawTextMark(main.Canvas, request, request.Project.Composition, item.Mark!, paint, resources);
    }

    /// <summary>照计划分配、捕获、执行并合成一个对象；不再做任何「画什么、多大、什么顺序」的决定。</summary>
    private void ExecuteObjectPlan(ImageSceneRun run, SKSurface main, ObjectRenderPlan plan, ImageObjectState? export,
        SceneCompositionPrefix? outsidePrefix, ObjectOwnerCell owner)
    {
        var request = run.Request; var resources = run.Resources; var item = plan.Item; var bounds = plan.WorkBounds;
        using var state = new ImageObjectState(bounds, _surfaceBudget.Limits, resources)
        {
            ScopeBudget = export?.ScopeBudget, Backdrop = run.Backdrop, OutsidePrefix = outsidePrefix, TargetRegion = plan.TargetRegion,
            GraphTargets = plan.GraphTargets,
        };
        // Prefix scheduling exports C/E and resolves Below lazily per owner.
        // Legacy scheduling retains its real destination snapshot.
        using var capturedBelow = plan.Below == ObjectBelowSource.DestinationSnapshot ? SnapshotSceneBelow(main, request, bounds, state) : null;
        var below = capturedBelow;
        bool opacityKnown = false;
        foreach (var step in plan.Parts)
        {
            switch (step.Kind)
            {
                case ObjectPartKind.Material: AddMaterial(step); break;
                case ObjectPartKind.KnownEmpty: AddEmpty(step); break;
                default: AddOriginal(step); break;
            }
        }
        state.Backgrounds.Dispose();

        // 子元素先完成，再把真实产物交给整行图；每个目标内保留工程实例顺序。
        foreach (var graph in plan.Graphs)
        {
            owner.Value = new(graph.Binding);
            state.ScopeBudget?.Result();
            ApplyImageObjectBinding(request, graph.Binding, graph.Program, state, below, run.Diagnostics);
        }
        if (!state.Changed && !plan.ClosedExport) { DrawOriginalObject(run, main, item); return; }
        if (plan.ClosedExport) { state.SetLayerBudget(state.Layers.Count); ExportClosedImageObject(request, state, export!); return; }

        using var processed = AssembleImageLayers(request, state, state.Layers, below!);
        using var final = ApplyImageObjectOpacity(request, state, processed, below!);
        DrawGraphSurface(main.Canvas, final, SKBlendMode.Src);

        void AddMaterial(ObjectPartStep step)
        {
            var binding = step.Material!;
            owner.Value = new(binding.Owner, "RenderMaterials", binding.TextTarget);
            // Admit the actual additional source layer before any material input is captured.
            state.SetLayerBudget(checked(state.Layers.Count + 1));
            state.ScopeBudget?.Result();
            var result = state.Own(ExecuteObjectMaterial(request, binding, step.MaterialProgram!, step.Part, step.Sung, state, below));
            run.MaterialWarnings.AddRange(result.Diagnostics.Select(diagnostic => new RenderMaterialDiagnostic(binding.Owner,
                binding.Material.Handle, diagnostic.InputId, diagnostic.Reason)));
            SetOpacity(MaterialCommonOpacity(request, step.Member));
            state.Layers.Add(new(step.Order, [new(step.Member, step.Part)], result.Output, [result], step.MaterialProgram!.Graph, owner.Value));
            state.Changed = true;
        }
        bool AddOriginal(ObjectPartStep step)
        {
            // Empty parts are still real source layers; admission must precede capture.
            state.SetLayerBudget(checked(state.Layers.Count + 1));
            // 检查关闭时不为共享空表面同步读回：未发起任何部件绘制即可证明仍是初始透明；
            // 其余按非空保留独立表面，像素不变，只可能比开启时多占表面账。
            var probe = _graphPixelChecks ? null : new CaptureDrawProbe();
            // 对象成员的部件不含有自己图或材质的元素族。
            var captured = CaptureColorPart(request, CaptureTarget(step.Member), step.Part, bounds, state.Budget, resources,
                step.Member == plan.Target ? plan.ExcludedTokens : null, step.Sung, probe);
            state.Own(captured);
            SetOpacity(captured.QuantizedOpacity);
            var surface = captured.Surface;
            bool empty = IsRuby(step.Part) && item.Line?.Frame.Line?.RubyAnnotations.IsEmpty == true;
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
            state.Layers.Add(new(step.Order, [new(step.Member, step.Part)], surface, [], null, null));
            return empty;
        }
        void AddEmpty(ObjectPartStep step)
        {
            // 检查模式按原样部件捕获（表面与读回同原来）并核对判空条件没有漏掉样式来源；注音材质同样不执行。
            if (_graphPixelChecks)
            {
                if (!AddOriginal(step))
                    throw new PreparedFrameException("SceneColorPartKnownEmptyMismatch", "预判为空的对象部件实际有像素。");
                return;
            }
            state.SetLayerBudget(checked(state.Layers.Count + 1));
            state.Empty ??= state.Own(RasterSceneSurface(request, bounds, state.Budget, ShaderSurfaceKind.Color, resources,
                "CaptureSceneColorPart", static (_, _, _) => { }));
            state.Layers.Add(new(step.Order, [new(step.Member, step.Part)], state.Empty, [], null, null));
        }
        // 每个成员的填充部件从不预判为空，共同透明度总能取到；跳过的空层不参与核对。
        void SetOpacity(double value)
        {
            if (!opacityKnown) { state.Opacity = value; opacityKnown = true; }
            else if (state.Opacity != value) throw new PreparedFrameException("SceneColorPartFrameMismatch", "对象部件共同透明度不一致。");
        }
    }
}
