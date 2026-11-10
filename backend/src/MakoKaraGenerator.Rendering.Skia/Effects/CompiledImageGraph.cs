using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>共享 CPU 图后端；固定编译、严格输入／预算，独立图像产物不修改源工程。</summary>
public sealed partial class CompiledImageGraph : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CompiledRuntimeShader> _programs = new(StringComparer.Ordinal);
    private readonly CompiledRuntimeShader _mask, _replace, _coverageUnion, _chainReplace;
    private readonly CompositionPayloadPrograms _payloadPrograms;
    private readonly ImageSurfaceBudgetSession _surfaceBudget;
    private readonly ISkiaRenderDevice _device;
    private readonly SkiaRenderDeviceLifetime? _deviceLifetime;
    // 逐像素检查（颜色预乘、材质区域越界）只在作者草稿、包验证与测试中开启；
    // 关闭时节点之间不为检查同步提交或读回，产物留在设备上。
    private readonly bool _pixelChecks;
    private bool _disposed;
    private CompiledImageGraph(CompiledImageGraphDefinition plan, CompiledRuntimeShader mask, CompiledRuntimeShader replace,
        CompiledRuntimeShader coverageUnion, CompiledRuntimeShader chainReplace, CompositionPayloadPrograms payloadPrograms,
        ISkiaRenderDevice device, bool ownsDevice, bool pixelChecks)
    { Plan = plan; _mask = mask; _replace = replace; _coverageUnion = coverageUnion; _chainReplace = chainReplace; _payloadPrograms = payloadPrograms; _surfaceBudget = new(); _device = device; _deviceLifetime = ownsDevice ? new(device) : null; _pixelChecks = pixelChecks; }
    /// <summary>固定中立图计划。</summary>
    public CompiledImageGraphDefinition Plan { get; }
    /// <summary>验证全部节点的 native 程序，包括透明模式不执行的源码。</summary>
    public static CompiledImageGraph Compile(CompiledImageGraphDefinition plan, CancellationToken cancellationToken = default)
        => CompileCore(plan, null, true, cancellationToken);

    internal static CompiledImageGraph Compile(CompiledImageGraphDefinition plan, ISkiaRenderDevice device,
        bool pixelChecks = true, CancellationToken cancellationToken = default)
        => CompileCore(plan, device, pixelChecks, cancellationToken);

    private static CompiledImageGraph CompileCore(CompiledImageGraphDefinition plan, ISkiaRenderDevice? device, bool pixelChecks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan); cancellationToken.ThrowIfCancellationRequested();
        CompiledRuntimeShader? mask = null, replace = null, coverageUnion = null, chainReplace = null;
        CompositionPayloadPrograms? payloadPrograms = null; CompiledImageGraph? graph = null; ISkiaRenderDevice? createdDevice = null;
        try
        {
            mask = CompiledRuntimeShader.Compile(new("host/mask.sksl", "uniform shader color; uniform shader coverage; half4 main(float2 p) { return color.eval(p)*coverage.eval(p).a; }",
                ShaderProgramDefinition.RasterProfile, [], [new("color", ShaderSurfaceKind.Color), new("coverage", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            replace = CompiledRuntimeShader.Compile(new("host/replace.sksl", "uniform shader result; uniform shader below; uniform shader coverage; uniform float opacity; half4 main(float2 p) { return result.eval(p)*opacity + below.eval(p)*(1-coverage.eval(p).a*opacity); }",
                ShaderProgramDefinition.RasterProfile, [new("opacity", ShaderUniformKind.Float)], [new("result", ShaderSurfaceKind.Color), new("below", ShaderSurfaceKind.Color), new("coverage", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            coverageUnion = CompiledRuntimeShader.Compile(new("host/coverage-union.sksl", "uniform shader a; uniform shader b; half4 main(float2 p) { return half4(0,0,0,max(a.eval(p).a,b.eval(p).a)); }",
                ShaderProgramDefinition.RasterProfile, [], [new("a", ShaderSurfaceKind.Coverage), new("b", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            chainReplace = CompiledRuntimeShader.Compile(new("host/chain-replace.sksl", "uniform shader result; uniform shader below; uniform shader coverage; uniform float opacity; uniform int sourceOver; half4 main(float2 p) { half4 c=result.eval(p); half e=max(coverage.eval(p).a,sourceOver==1 ? c.a : half(0)); return c*opacity+below.eval(p)*(1-e*opacity); }",
                ShaderProgramDefinition.RasterProfile, [new("opacity", ShaderUniformKind.Float), new("sourceOver", ShaderUniformKind.Int)],
                [new("result", ShaderSurfaceKind.Color), new("below", ShaderSurfaceKind.Color), new("coverage", ShaderSurfaceKind.Coverage)]), cancellationToken: cancellationToken);
            payloadPrograms = new(cancellationToken);
            createdDevice = device is null ? SkiaRenderDeviceFactory.Create() : null;
            graph = new(plan, mask, replace, coverageUnion, chainReplace, payloadPrograms, device ?? createdDevice!, device is null, pixelChecks);
            foreach (var pair in plan.Programs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                graph._programs.Add(pair.Key, CompiledRuntimeShader.Compile(pair.Value, plan.Budget.MaximumUniformScalars, cancellationToken));
            return graph;
        }
        catch { if (graph != null) graph.Dispose(); else { mask?.Dispose(); replace?.Dispose(); coverageUnion?.Dispose(); chainReplace?.Dispose(); payloadPrograms?.Dispose(); createdDevice?.Dispose(); } throw; }
    }
    /// <summary>执行完整图；输入借用，返回产物由调用者释放，取消不返回部分画面。可传共享帧账；null 时使用本编译图的固定会话账。</summary>
    public ImageGraphResult Execute(ImageGraphFrameContext frame, IReadOnlyDictionary<string, SkiaGraphInput> inputs, CancellationToken cancellationToken = default)
        => Execute(frame, inputs, null, cancellationToken);
    /// <summary>使用宿主共享帧账执行完整图；输入借用和产物计费保留至结果释放。</summary>
    public ImageGraphResult Execute(ImageGraphFrameContext frame, IReadOnlyDictionary<string, SkiaGraphInput> inputs,
        ImageSurfaceBudgetFrame? surfaceBudget, CancellationToken cancellationToken = default)
    {
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Graph);
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(inputs);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            ValidateFrame(frame); ImageGraphBindings.ValidateParameters(Plan, frame.Parameters);
            MaterialUniformValidation.Validate(Plan, frame.Uniforms);
            var active = ActiveReferences(Plan.Definition.Output, Plan.Definition.CompositeCoverage);
            var ordered = Plan.Nodes.Where(node => active.Contains(GraphReference.Node(node.Id))).ToArray();
            var transforms = ResolveTransforms(frame, ordered, cancellationToken);
            var shapes = Shapes(frame, ordered, transforms, cancellationToken);
            var declarations = Plan.Definition.Inputs.ToDictionary(input => input.Id, StringComparer.Ordinal);
            foreach (string key in inputs.Keys) if (!declarations.ContainsKey(key)) throw Error("GraphInputUnknown", "inputs." + key, "输入未声明。");
            var backgrounds = declarations.Values.Where(input => active.Contains(GraphReference.Input(input.Id)) && input.BackgroundRequirement != GraphBackgroundRequirement.None).ToArray();
            if (frame.Transparent && backgrounds.Length > 0) return Bypass(backgrounds, "TransparentBackgroundDependency");
            // 不透明却缺必需背景媒体（未设置、文件丢失、该时刻无帧）：与透明输出同走旁路，不让整帧失败。
            var missingMedia = MissingRequiredMedia(frame, backgrounds, inputs);
            if (missingMedia.Length > 0) return Bypass(missingMedia, BackgroundMediaUnavailable);
            var surfaces = new Dictionary<GraphReference, SkiaGraphSurface>();
            ImageGraphResult Bypass(GraphInputDefinition[] skipped, string reason)
            {
                if (Plan.Definition.BypassInput is not { } bypass) throw Error("GraphBackgroundBypassMissing", "bypassInput", "背景依赖输出须明确旁路。");
                var original = ResolveInput(declarations[bypass.Id], frame, inputs, cancellationToken);
                CheckBudget(1, Bytes(original));
                var borrowed = resources.BorrowInputs([original]);
                return new(Plan, original, null, false, false, frame, [.. skipped.Select(input => new ImageGraphBypassDiagnostic(Plan.Definition.Id, input.Id, reason))], 1, Bytes(original), borrowed, AcquireDeviceLease());
            }
            foreach (var reference in active.Where(reference => reference.IsInput)) surfaces.Add(reference, ResolveInput(declarations[reference.Id], frame, inputs, cancellationToken));
            var uses = Uses(ordered, Plan.Definition.Output, Plan.Definition.CompositeCoverage);
            var peaks = Preflight(frame, surfaces, ordered, shapes, uses, transforms, cancellationToken);
            ImageSurfaceReservations? inputReservations = resources.BorrowInputs(surfaces.Values);
            var owned = new HashSet<SkiaGraphSurface>();
            try
            {
                var distinct = surfaces.Values.DistinctBy(surface => surface.Image.Handle).ToArray();
                resources.CheckAdditional(peaks.Surfaces - distinct.Length, peaks.Bytes - distinct.Sum(Bytes), "ExecuteImageGraphPeak");
                foreach (var node in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SkiaGraphSurface output = DrawNode(node, shapes[node.Id], frame, surfaces, transforms, resources, cancellationToken);
                    owned.Add(output); surfaces.Add(GraphReference.Node(node.Id), output);
                    foreach (var reference in ImageGraphCompiler.References(node))
                        if (--uses[reference] == 0 && !reference.IsInput)
                        { var old = surfaces[reference]; old.Dispose(); owned.Remove(old); surfaces.Remove(reference); }
                }
                cancellationToken.ThrowIfCancellationRequested();
                var final = surfaces[Plan.Definition.Output];
                var coverage = Plan.Definition.CompositeCoverage is { } coverageRef ? surfaces[coverageRef] : null;
                bool ownsFinal = owned.Remove(final), ownsCoverage = coverage != null && owned.Remove(coverage);
                var result = new ImageGraphResult(Plan, final, coverage, ownsFinal, ownsCoverage, frame, [], peaks.Surfaces, peaks.Bytes, inputReservations, AcquireDeviceLease());
                inputReservations = null; return result;
            }
            finally { foreach (var surface in owned) surface.Dispose(); inputReservations?.Dispose(); }
        }
    }
    /// <summary>按单次预乘或独立覆盖替换合成；下层必须是该位置之前的真实图像。</summary>
    public SkiaGraphSurface Composite(ImageGraphResult result, SkiaGraphSurface below, double opacity = 1, CancellationToken cancellationToken = default)
        => Composite(result, below, null, opacity, cancellationToken);
    /// <summary>在共享帧账中执行合成，计入已有图像和新合成产物，不夺取下层所有权。</summary>
    public SkiaGraphSurface Composite(ImageGraphResult result, SkiaGraphSurface below, ImageSurfaceBudgetFrame? surfaceBudget,
        double opacity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(below);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            if (!ReferenceEquals(result.Plan, Plan)) throw Error("GraphResultMismatch", "composite", "产物不属于固定图代次。");
            if (!double.IsFinite(opacity) || opacity is < 0 or > 1) throw Error("InvalidGraphOpacity", "composite", "透明度必须在 [0,1]。");
            ValidateSurface(below, ShaderSurfaceKind.Color, "composite.below", cancellationToken);
            ValidateSurface(result.Output, ShaderSurfaceKind.Color, "composite.output", cancellationToken);
            var bounds = below.Descriptor.Bounds;
            if (!bounds.Contains(result.Output.Descriptor.Bounds)) throw Error("GraphCompositeBoundsExceeded", "composite", "下层框不能裁掉图外扩。");
            var shape = Aligned(bounds, result.Context);
            bool replace = result.Diagnostics.IsEmpty && Plan.Definition.Composite == GraphCompositeMode.ReplaceCoveredBackdrop;
            IEnumerable<SkiaGraphSurface> compositeInputs = result.Coverage is { } mask ? [below, result.Output, mask] : [below, result.Output];
            var distinct = compositeInputs.DistinctBy(surface => surface.Image.Handle).ToArray();
            long bytes = checked(distinct.Sum(Bytes) + SizeBytes(shape, result.Context));
            CheckBudget(distinct.Length + 1, bytes);
            using var borrowed = resources.BorrowInputs(distinct);
            resources.CheckAdditional(1, SizeBytes(shape, result.Context), "CompositeImageGraphPeak");
            if (replace)
            {
                var coverage = result.Coverage ?? throw Error("GraphCompositeCoverageMissing", "composite", "覆盖不存在。");
                ValidateSurface(coverage, ShaderSurfaceKind.Coverage, "composite.coverage", cancellationToken);
                using var a = InputShader(result.Output, GraphEdgeMode.Transparent); using var b = InputShader(below, GraphEdgeMode.Transparent); using var c = InputShader(coverage, GraphEdgeMode.Transparent);
                using var shader = _replace.CreateShader(new([new("opacity", UniformValue.Float(opacity))]), new Dictionary<string, RuntimeShaderInput>
                    { ["result"] = new(a, ShaderSurfaceKind.Color), ["below"] = new(b, ShaderSurfaceKind.Color), ["coverage"] = new(c, ShaderSurfaceKind.Coverage) }, cancellationToken);
                return Raster(shape, result.Context, ShaderSurfaceKind.Color, canvas => Draw(canvas, shape, shader), resources, cancellationToken);
            }
            using var belowShader = InputShader(below, GraphEdgeMode.Transparent); using var outputShader = InputShader(result.Output, GraphEdgeMode.Transparent);
            return Raster(shape, result.Context, ShaderSurfaceKind.Color, canvas =>
            { Draw(canvas, shape, belowShader); Draw(canvas, shape, outputShader, SKBlendMode.SrcOver, opacity); }, resources, cancellationToken);
        }
    }
    private SkiaGraphSurface ResolveInput(GraphInputDefinition declaration, ImageGraphFrameContext frame, IReadOnlyDictionary<string, SkiaGraphInput> inputs, CancellationToken cancellationToken)
    {
        if (!inputs.TryGetValue(declaration.Id, out var input) || input == null)
            throw Error(declaration.Required ? "GraphRequiredInputUnavailable" : "GraphOptionalInputPolicyPending", "inputs." + declaration.Id,
                declaration.Required ? "必需的真实输入缺失，不伪造空图像。" : "可选输入缺失策略尚待用户确认。");
        var source = declaration.ChoiceParameter is { } parameter ? declaration.Choices[frame.Parameters.GetEnum(parameter)] : declaration.Source;
        // 可选背景媒体缺失时宿主以工程底色代替（见 SkiaRenderPipeline.BackgroundInput）。
        bool colorForMedia = !declaration.Required && source == GraphInputSource.BackgroundMedia && input.Source == GraphInputSource.BackgroundColor;
        if (input.Source != source && !colorForMedia || (source == GraphInputSource.ResourceImage && input.ResourceId != declaration.ResourceId))
            throw Error("GraphInputSourceMismatch", "inputs." + declaration.Id, "真实来源或固定资源身份与声明不同。");
        ValidateSurface(input.Surface, declaration.Kind, "inputs." + declaration.Id, cancellationToken); return input.Surface;
    }
    /// <summary>不透明输出缺背景媒体时的诊断原因；图旁路、材质退回包内兜底。</summary>
    internal const string BackgroundMediaUnavailable = "BackgroundMediaUnavailable";
    /// <summary>宿主未提供的必需背景媒体输入（按本帧参数解析选择来源）。</summary>
    internal static GraphInputDefinition[] MissingRequiredMedia(ImageGraphFrameContext frame, IEnumerable<GraphInputDefinition> declarations,
        IReadOnlyDictionary<string, SkiaGraphInput> inputs)
        => [.. declarations.Where(input => input.Required && !inputs.ContainsKey(input.Id)
            && (input.ChoiceParameter is { } choice ? input.Choices[frame.Parameters.GetEnum(choice)] : input.Source) == GraphInputSource.BackgroundMedia)];
    private HashSet<GraphReference> ActiveReferences(GraphReference output, GraphReference? coverage)
    {
        var nodes = Plan.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal); var active = new HashSet<GraphReference>();
        void Visit(GraphReference reference) { if (!active.Add(reference) || reference.IsInput) return; foreach (var input in ImageGraphCompiler.References(nodes[reference.Id])) Visit(input); }
        Visit(output); if (coverage.HasValue) Visit(coverage.Value); return active;
    }
    internal ImmutableArray<GraphInputDefinition> ActiveInputs(ImageGraphFrameContext frame)
    {
        var active = ActiveReferences(Plan.Definition.Output, Plan.Definition.CompositeCoverage);
        if (frame.Transparent && Plan.Definition.Inputs.Any(input => active.Contains(GraphReference.Input(input.Id)) && input.BackgroundRequirement != GraphBackgroundRequirement.None))
        {
            if (Plan.Definition.BypassInput is not { } bypass) throw Error("GraphBackgroundBypassMissing", "bypassInput", "透明背景依赖须明确旁路。");
            return [Plan.Definition.Inputs.Single(input => input.Id == bypass.Id)];
        }
        return [.. Plan.Definition.Inputs.Where(input => active.Contains(GraphReference.Input(input.Id)))];
    }
    /// <summary>宿主据本帧背景媒体是否可用决定要捕获的输入：不透明却缺必需背景媒体时，与透明输出一样只需旁路输入。</summary>
    /// <remarks><paramref name="backgroundMediaAvailable"/> 仅在仍需宿主捕获必需背景媒体时求值，免得无关图解码背景；
    /// <paramref name="provided"/> 为调用方显式提供的输入，不需捕获。</remarks>
    internal ImmutableArray<GraphInputDefinition> ActiveInputs(ImageGraphFrameContext frame, Func<bool> backgroundMediaAvailable,
        IReadOnlyDictionary<string, SkiaGraphInput>? provided = null)
    {
        var declarations = ActiveInputs(frame);
        if (frame.Transparent || MissingRequiredMedia(frame, declarations, provided ?? EmptyInputs).Length == 0 || backgroundMediaAvailable()) return declarations;
        if (Plan.Definition.BypassInput is not { } bypass) throw Error("GraphBackgroundBypassMissing", "bypassInput", "背景依赖须明确旁路。");
        return [Plan.Definition.Inputs.Single(input => input.Id == bypass.Id)];
    }
    private static readonly IReadOnlyDictionary<string, SkiaGraphInput> EmptyInputs = new Dictionary<string, SkiaGraphInput>();
    private static Dictionary<GraphReference, int> Uses(ImageGraphNode[] nodes, GraphReference output, GraphReference? coverage)
    {
        var uses = new Dictionary<GraphReference, int>();
        void Add(GraphReference reference) => uses[reference] = uses.GetValueOrDefault(reference) + 1;
        foreach (var node in nodes) foreach (var reference in ImageGraphCompiler.References(node)) Add(reference);
        Add(output); if (coverage.HasValue) Add(coverage.Value); return uses;
    }
    private Dictionary<string, GraphRect> Shapes(ImageGraphFrameContext frame, ImageGraphNode[] nodes,
        Dictionary<string, ResolvedGraphTransform> transforms, CancellationToken cancellationToken)
    {
        var expansions = new Dictionary<GraphReference, double>(); var bounds = new Dictionary<string, GraphRect>(StringComparer.Ordinal);
        foreach (var input in Plan.Definition.Inputs) expansions.Add(GraphReference.Input(input.Id), 0);
        GraphRect limit = frame.TargetBounds.Expand(Plan.Definition.MaximumExpandPx);
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double expand = ImageGraphBindings.Expansion(node.Bounds.Expand, frame.Parameters);
            double total = ImageGraphCompiler.References(node).Select(reference => expansions[reference]).DefaultIfEmpty(0).Max() + expand;
            if (!double.IsFinite(total) || expand > node.Bounds.MaximumExpandPx || total > Plan.Definition.MaximumExpandPx)
                throw Error("GraphExpansionExceeded", "nodes." + node.Id, "当前值的累计外扩超过声明。");
            if (node is ImageGraphNode.Blur blur && expand < Math.Ceiling(3 * ImageGraphBindings.Number(blur.SigmaPx, frame.Parameters)))
                throw Error("GraphBlurBoundsInsufficient", "nodes." + node.Id, "当前外扩不能覆盖 3σ。");
            var shape = node is ImageGraphNode.Transform ? transforms[node.Id].OutputBounds : frame.TargetBounds.Expand(total);
            if (!shape.IsValid || !limit.IsValid || !limit.Contains(shape)) throw Error("GraphBoundsExceeded", "nodes." + node.Id, "输出框超出目标最大外扩。");
            var physical = node is ImageGraphNode.Transform ? transforms[node.Id].PhysicalBounds : Aligned(shape, frame);
            expansions.Add(GraphReference.Node(node.Id), total); bounds.Add(node.Id, physical);
        }
        return bounds;
    }
    private sealed record BudgetInput(object Identity, GraphSurfaceDescriptor Descriptor, long Bytes);

    private (int Surfaces, long Bytes) Preflight(ImageGraphFrameContext frame, Dictionary<GraphReference, SkiaGraphSurface> inputs, ImageGraphNode[] nodes,
        Dictionary<string, GraphRect> shapes, Dictionary<GraphReference, int> useCounts,
        Dictionary<string, ResolvedGraphTransform> transforms, CancellationToken cancellationToken)
        => PreflightLayout(frame, inputs.ToDictionary(pair => pair.Key,
            pair => new BudgetInput(pair.Value.Image.Handle, pair.Value.Descriptor, Bytes(pair.Value))),
            nodes, shapes, useCounts, transforms, cancellationToken);

    // Shared lifetime arithmetic for real Execute inputs and zero-allocation
    // empty-group admission layouts. No native image or surface is fabricated.
    private (int Surfaces, long Bytes) PreflightLayout(ImageGraphFrameContext frame,
        Dictionary<GraphReference, BudgetInput> inputs, ImageGraphNode[] nodes,
        Dictionary<string, GraphRect> shapes, Dictionary<GraphReference, int> useCounts,
        Dictionary<string, ResolvedGraphTransform> transforms, CancellationToken cancellationToken)
    {
        var distinct = inputs.Values.DistinctBy(input => input.Identity).ToArray();
        long bytes = distinct.Sum(input => input.Bytes); int count = distinct.Length, peakCount = count; long peakBytes = bytes;
        var uses = new Dictionary<GraphReference, int>(useCounts); var activeBytes = new Dictionary<GraphReference, long>();
        // 输入由调用者拥有，在整个执行期间仍活动；节点在最后使用后及时释放。
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (node is ImageGraphNode.Transform transform)
            {
                var descriptor = transform.Input.IsInput ? inputs[transform.Input].Descriptor
                    : new GraphSurfaceDescriptor(shapes[transform.Input.Id], GraphCoordinateSpace.Canvas, SceneMatrix.Identity, frame.DensityX, frame.DensityY);
                PreflightTransformInput(transform, transforms[node.Id], descriptor, cancellationToken);
            }
            long allocation = SizeBytes(shapes[node.Id], frame);
            int scratchCount = node is ImageGraphNode.Blur ? 2 : 0;
            long candidateBytes = checked(bytes + allocation * (1 + scratchCount)); int candidateCount = count + 1 + scratchCount;
            CheckBudget(candidateCount, candidateBytes); peakBytes = Math.Max(peakBytes, candidateBytes); peakCount = Math.Max(peakCount, candidateCount);
            bytes = checked(bytes + allocation); count++; activeBytes.Add(GraphReference.Node(node.Id), allocation);
            foreach (var reference in ImageGraphCompiler.References(node))
                if (--uses[reference] == 0 && !reference.IsInput) { bytes -= activeBytes[reference]; activeBytes.Remove(reference); count--; }
        }
        CheckBudget(peakCount, peakBytes); return (peakCount, peakBytes);
    }
    private void ValidateFrame(ImageGraphFrameContext frame)
    {
        if (Plan.Definition.Space != GraphCoordinateSpace.Canvas) throw Error("UnsupportedGraphExecutionSpace", "space", "首个执行后端要求明确 Canvas 适配。");
        if (frame.Parameters == null || frame.Uniforms is null || frame.Transforms is null || frame.TimeUs < 0 || !frame.TargetBounds.IsValid || !Positive(frame.CanvasWidth) || !Positive(frame.CanvasHeight)
            || !Positive(frame.DensityX) || !Positive(frame.DensityY)) throw Error("InvalidGraphFrame", "frame", "固定帧、逻辑尺寸或密度无效。");
    }
    private void ValidateSurface(SkiaGraphSurface surface, ShaderSurfaceKind kind, string field, CancellationToken cancellationToken)
    {
        var image = surface.Image; var d = surface.Descriptor;
        if (image.Handle == nint.Zero || surface.Kind != kind || d.Space != GraphCoordinateSpace.Canvas || d.LocalToCanvas != SceneMatrix.Identity
            || !d.Bounds.IsValid || !Positive(d.DensityX) || !Positive(d.DensityY) || image.AlphaType != SKAlphaType.Premul
            || image.ColorType is not (SKColorType.Bgra8888 or SKColorType.Rgba8888) || Math.Abs(d.Bounds.Width * d.DensityX - image.Width) > 1e-6
            || Math.Abs(d.Bounds.Height * d.DensityY - image.Height) > 1e-6)
            throw Error("InvalidGraphSurface", field, "真实图像、编码、Canvas 几何或密度不符。");
        if (image.Width > Plan.Budget.MaximumPixelDimension || image.Height > Plan.Budget.MaximumPixelDimension)
            throw Error("GraphSurfaceDimensionExceeded", field, "输入每边像素超预算。");
        if (kind == ShaderSurfaceKind.Color)
        {
            if (image.ColorSpace?.IsSrgb != true) throw Error("GraphColorSpaceMismatch", field, "v1 颜色输入须明确采用 sRGB 编码。");
            if (_pixelChecks) ValidateColorPixels(surface, field, cancellationToken);
        }
    }
    private GraphRect Aligned(GraphRect bounds, ImageGraphFrameContext frame)
    {
        double left = Math.Floor(bounds.Left * frame.DensityX), top = Math.Floor(bounds.Top * frame.DensityY), right = Math.Ceiling(bounds.Right * frame.DensityX), bottom = Math.Ceiling(bounds.Bottom * frame.DensityY);
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) || right <= left || bottom <= top
            || right - left > Plan.Budget.MaximumPixelDimension || bottom - top > Plan.Budget.MaximumPixelDimension)
            throw Error("GraphSurfaceDimensionExceeded", "bounds", "实际像素框无效或每边超预算。");
        var aligned = new GraphRect(left / frame.DensityX, top / frame.DensityY, right / frame.DensityX, bottom / frame.DensityY);
        if (!aligned.IsValid) throw Error("InvalidGraphFrame", "bounds", "像素对齐无法保留有限框。"); return aligned;
    }
    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
    private static long Bytes(SkiaGraphSurface surface) => checked((long)surface.Image.Width * surface.Image.Height * 4);
    private static long SizeBytes(GraphRect shape, ImageGraphFrameContext frame) => checked((long)Math.Round(shape.Width * frame.DensityX) * (long)Math.Round(shape.Height * frame.DensityY) * 4);
    private void CheckBudget(int count, long bytes)
    { if (count > Plan.Budget.MaximumSurfaces || bytes > Plan.Budget.MaximumBytes) throw Error("GraphSurfaceBudgetExceeded", "budget", $"活动表面 {count}/{Plan.Budget.MaximumSurfaces}，字节 {bytes}/{Plan.Budget.MaximumBytes}"); }
    private ImageGraphException Error(string code, string field, string message) => new(code, Plan.Definition.Id, field, message);
    private Action? AcquireDeviceLease() => _deviceLifetime?.Acquire();

    /// <summary>释放编译程序；自有设备等待返回表面及结果全部释放。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var program in _programs.Values) program.Dispose(); _programs.Clear();
            _mask.Dispose(); _replace.Dispose(); _coverageUnion.Dispose(); _chainReplace.Dispose(); _payloadPrograms.Dispose(); _surfaceBudget.Dispose();
        }
        _deviceLifetime?.Dispose();
    }
}
