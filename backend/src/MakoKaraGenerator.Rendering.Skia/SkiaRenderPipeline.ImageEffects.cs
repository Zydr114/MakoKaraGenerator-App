using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    /// <summary>合成本帧一个真实目标的完整有序实例链，保留之前的替换范围；不调用脚本，不写主画布。</summary>
    /// <remarks>调用者负责依次执行各图、提供前一 TargetColor 及保留其产物；正式父子调度后续接入。</remarks>
    public SkiaGraphSurface CompositeSceneImageEffectChain(RenderRequest request, IReadOnlyList<SceneImageEffectResult> results,
        SceneImageGraphProgram lastProgram, SkiaGraphSurface below, ImageSurfaceBudgetFrame? surfaceBudget = null, double opacity = 1)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(results); ArgumentNullException.ThrowIfNull(lastProgram);
        ArgumentNullException.ThrowIfNull(below); ObjectDisposedException.ThrowIf(_disposed, this);
        request.CancellationToken.ThrowIfCancellationRequested();
        if (results.Count == 0 || results.Any(result => result is null)) throw new PreparedFrameException("ImageEffectChainEmpty", "合成需要本帧完整实例链。");
        var owner = results[^1].Binding;
        try
        {
            PreparedRenderFrame.ValidateRequest(request);
            if (request.PreparedFrame is not { } prepared || !prepared.ImageEffects.Where(binding => binding.Binding.Target == owner.Binding.Target)
                .SequenceEqual(results.Select(result => result.Binding)))
                throw new PreparedFrameException("ImageEffectChainBindingMismatch", "合成链必须按本帧实际顺序包含该目标所有实例。");
            if (owner.Binding.Graph != lastProgram.Handle)
                throw new PreparedFrameException("ImageEffectResourceMismatch", "最后图不是绑定的固定修订和导出。");
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            return lastProgram.Graph.CompositeChain(results.Select(result => result.GraphResult).ToArray(), below,
                surfaceBudget ?? ownFrame!, opacity, request.CancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            string code = exception switch { PreparedFrameException error => error.Code, ImageGraphException error => error.Code,
                ImageSurfaceBudgetException error => error.Code, _ => "SceneImageEffectCompositeFailed" };
            throw new EffectFailureException(owner.InstanceId, owner.ArtifactHash, "CompositeImageGraph", request.TimeUs, code, exception, owner.PackageIdentity);
        }
    }

    /// <summary>执行本帧真实绑定，产出调用方拥有的图结果；不向主画布自动合成，不调用作者代码。</summary>
    /// <param name="request">匹配的固定准备帧和输出密度。</param><param name="binding">本准备帧的真实实例请求。</param>
    /// <param name="program">帧外固定资源／已编译图及显式捕获配方。</param>
    /// <param name="externalInputs">由调用者提供的下层／包图片及显式外部媒体；未提供媒体／底色时捕获真实工程背景，不补缺失默认。</param>
    /// <param name="previousTarget">同目标上一实例产物；null 表示捕获处理前目标，借用不释放。</param>
    /// <param name="surfaceBudget">本帧共享活动账；null 时自建本次调用的帧账，产物仍保留会话计费。</param>
    public SceneImageEffectResult ExecuteSceneImageEffect(RenderRequest request, PreparedImageEffectBinding binding,
        SceneImageGraphProgram program, IReadOnlyDictionary<string, SkiaGraphInput> externalInputs, SkiaGraphSurface? previousTarget,
        ImageSurfaceBudgetFrame? surfaceBudget = null)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(externalInputs); ObjectDisposedException.ThrowIf(_disposed, this);
        using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
        var resources = surfaceBudget ?? ownFrame!;
        ImageSurfaceReservations? externalReservations = null;
        List<SkiaGraphSurface> captures = []; bool transferred = false;
        try
        {
            request.CancellationToken.ThrowIfCancellationRequested(); PreparedRenderFrame.ValidateRequest(request);
            if (request.PreparedFrame is not { } prepared || !prepared.ImageEffects.Contains(binding))
                throw new PreparedFrameException("ImageEffectBindingUnavailable", "执行请求必须属于本帧实际脚本输出。");
            if (binding.Binding.Graph != program.Handle)
                throw new PreparedFrameException("ImageEffectResourceMismatch", "执行程序不是绑定的固定包修订和图导出。");
            var target = binding.Binding.Target switch
            {
                ImageEffectTarget.Line line => (SceneCaptureTarget)new SceneCaptureTarget.Line(line.Target),
                ImageEffectTarget.Token token => new SceneCaptureTarget.Token(token.Target),
                ImageEffectTarget.Mark mark => new SceneCaptureTarget.Mark(mark.Target),
                ImageEffectTarget.Group => throw GroupImageEffectUnsupported(),
                _ => throw new PreparedFrameException("InvalidPreparedImageEffectTarget", "不支持的图目标。"),
            };
            var actual = request.ResolveTarget(); var composition = request.Project.Composition;
            double dx = actual.ScaleXOf(composition), dy = actual.ScaleYOf(composition);
            var context = new ImageGraphFrameContext(request.TimeUs, composition.CanvasWidth, composition.CanvasHeight, program.Bounds,
                dx, dy, binding.Binding.Parameters, request.TransparentBackground);
            ImageGraphBindings.ValidateParameters(program.Graph.Plan, binding.Binding.Parameters);
            var declarations = program.Graph.ActiveInputs(context);
            if (previousTarget is not null && program.HasPerInputRecipes && declarations.Any(input =>
                (input.ChoiceParameter is { } parameter ? input.Choices[binding.Binding.Parameters.GetEnum(parameter)] : input.Source) == GraphInputSource.TargetColor))
                throw new PreparedFrameException("ImageEffectCaptureReassemblyPending", "逐输入配方须从真实部件重新组装，不能将上一张整体产物替代全部输入；混合配方协调器尚未接入。");
            bool usesPrevious = previousTarget is not null && declarations.Any(input =>
                (input.ChoiceParameter is { } parameter ? input.Choices[binding.Binding.Parameters.GetEnum(parameter)] : input.Source) == GraphInputSource.TargetColor);
            externalReservations = resources.BorrowInputs(declarations
                .Where(input => externalInputs.ContainsKey(input.Id)).Select(input => externalInputs[input.Id].Surface)
                .Concat(usesPrevious ? [previousTarget!] : []));
            foreach (string id in externalInputs.Keys)
            {
                var declaration = program.Graph.Plan.Definition.Inputs.SingleOrDefault(input => input.Id == id)
                    ?? throw new PreparedFrameException("GraphInputUnknown", "外部图输入未声明：" + id);
                GraphInputSource source = declaration.ChoiceParameter is { } choice
                    ? declaration.Choices[binding.Binding.Parameters.GetEnum(choice)] : declaration.Source;
                if (SceneCaptureChannels.IsSupported(source))
                    throw new PreparedFrameException("CaptureInputOverrideForbidden", "真实目标捕获输入不能由外部图片覆盖：" + id);
            }
            Dictionary<string, SkiaGraphInput> inputs = new(StringComparer.Ordinal);
            Dictionary<GraphInputSource, SkiaGraphSurface> reused = [];
            Dictionary<(GraphInputSource Source, SceneCaptureRecipe Recipe), SkiaGraphSurface> reusedCaptures = [];
            HashSet<nint> handles = []; long bytes = 0;
            foreach (var declaration in declarations)
            {
                GraphInputSource source = declaration.ChoiceParameter is { } choice
                    ? declaration.Choices[binding.Binding.Parameters.GetEnum(choice)] : declaration.Source;
                if (SceneCaptureChannels.IsSupported(source)) continue;
                if (externalInputs.TryGetValue(declaration.Id, out var external))
                { inputs.Add(declaration.Id, external); Charge(external.Surface); continue; }
                if (source is GraphInputSource.BackgroundMedia or GraphInputSource.BackgroundColor)
                {
                    if (!reused.TryGetValue(source, out var background))
                    {
                        background = CaptureSceneBackground(request, source, program.Bounds, program.Graph.Plan.Budget, resources);
                        if (background is null) continue; // 缺失策略由唯一图执行器报告。
                        reused.Add(source, background); captures.Add(background);
                    }
                    inputs.Add(declaration.Id, new(background, source)); Charge(background);
                }
            }
            foreach (var declaration in declarations)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                GraphInputSource source = declaration.ChoiceParameter is { } choice
                    ? declaration.Choices[binding.Binding.Parameters.GetEnum(choice)] : declaration.Source;
                if (!SceneCaptureChannels.IsSupported(source)) continue;
                var recipe = program.GetInputRecipe(declaration.Id);
                if (!reusedCaptures.TryGetValue((source, recipe), out var surface))
                {
                    if (source == GraphInputSource.TargetColor && previousTarget is not null) surface = previousTarget;
                    else
                    {
                        double width = Math.Ceiling(program.Bounds.Right * dx) - Math.Floor(program.Bounds.Left * dx);
                        double height = Math.Ceiling(program.Bounds.Bottom * dy) - Math.Floor(program.Bounds.Top * dy);
                        CheckBudget(handles.Count + 1, bytes + width * height * 4);
                        surface = CaptureSceneTarget(request, target, recipe, source, program.Bounds, program.Graph.Plan.Budget, resources);
                        captures.Add(surface);
                    }
                    Charge(surface); reusedCaptures.Add((source, recipe), surface);
                }
                inputs.Add(declaration.Id, new(surface, source));
            }
            var result = program.Graph.Execute(context, inputs, resources, request.CancellationToken);
            transferred = true; return new(binding, result, [.. captures]);
            void Charge(SkiaGraphSurface surface)
            {
                if (!handles.Add(surface.Image.Handle)) return;
                bytes = checked(bytes + (long)surface.Image.Width * surface.Image.Height * 4); CheckBudget(handles.Count, bytes);
            }
            void CheckBudget(int count, double requestedBytes)
            {
                if (!double.IsFinite(requestedBytes) || count > program.Graph.Plan.Budget.MaximumSurfaces || requestedBytes > program.Graph.Plan.Budget.MaximumBytes)
                    throw new PreparedFrameException("SceneImageInputBudgetExceeded", "活跃图输入超出明确表面／字节预算，分配前拒绝。");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (EffectFailureException) { throw; }
        catch (Exception error)
        {
            string code = error switch { PreparedFrameException frame => frame.Code, ImageGraphException graph => graph.Code,
                ShaderProgramException shader => shader.Code, ImageSurfaceBudgetException resource => resource.Code, _ => "SceneImageGraphExecutionFailed" };
            throw new EffectFailureException(binding.InstanceId, binding.ArtifactHash, "ExecuteImageGraph", request.TimeUs, code, error, binding.PackageIdentity);
        }
        finally { externalReservations?.Dispose(); if (!transferred) foreach (var capture in captures) capture.Dispose(); }
    }
}
