using System.Collections.Immutable;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledImageGraph
{
    /// <summary>复制普通静态颜色为独立前景及自身 raw alpha 擦除；不合入下层。</summary>
    /// <remarks>借用颜色输入，两张返回表面由调用方拥有；共同透明度同时作用于前景和擦除一次。</remarks>
    public ImageCompositionPayload CaptureCompositionPayload(SkiaGraphSurface color, ImageGraphFrameContext context,
        ImageSurfaceBudgetFrame? surfaceBudget = null, double opacity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(color); ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            ValidateFrame(context); ValidateCompositionOpacity(opacity); ValidateSurface(color, ShaderSurfaceKind.Color, "payload.color", cancellationToken);
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            using var borrowed = resources.BorrowInputs([color]);
            var bounds = Aligned(color.Descriptor.Bounds, context);
            CheckBudget(3, checked(Bytes(color) + SizeBytes(bounds, context) * 2));
            resources.CheckAdditional(2, checked(SizeBytes(bounds, context) * 2), "CaptureStaticCompositionPayload");
            using var foreground = InputShader(color, GraphEdgeMode.Transparent);
            using var rawAlpha = color.BorrowAs(ShaderSurfaceKind.Coverage);
            using var coverage = InputShader(rawAlpha, GraphEdgeMode.Transparent);
            using var extract = _payloadPrograms.Extract.CreateShader(new([new("sourceOver", UniformValue.Int(0)), new("opacity", UniformValue.Float(opacity))]),
                new Dictionary<string, RuntimeShaderInput> { ["color"] = new(foreground, ShaderSurfaceKind.Color), ["coverage"] = new(coverage, ShaderSurfaceKind.Coverage) }, cancellationToken);
            return RasterPayloadPair(bounds, context, foreground, extract, resources, opacity, 1, cancellationToken);
        }
    }

    /// <summary>将孩子共同透明度分解到原顺序的有效层，完整重组等于完整孩子前景／擦除各乘一次透明度。</summary>
    /// <remarks>返回层保持原框、顺序和独立所有权；部分捕获读取此边界后的有效层，不复原已遮挡内容。失败回收全部新层，输入始终借用。</remarks>
    public ImmutableArray<ImageCompositionPayload> ResolveCompositionOpacityLayers(IReadOnlyList<ImageCompositionPayload> layers,
        ImageSurfaceBudgetFrame? surfaceBudget = null, double opacity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layers);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            if (layers.Count == 0 || layers.Any(layer => layer is null))
                throw Error("GraphCompositionPayloadEmpty", "payload.layers", "共同透明度分解须提供存活的有序层。");
            ValidateCompositionOpacity(opacity);
            var context = layers[0].Context;
            foreach (var layer in layers) { ValidateCompositionContext(layer.Context, context); ValidatePayload(layer, cancellationToken); }
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = layers.SelectMany(layer => new[] { layer.Color, layer.Erasure }).DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            long inputBytes = inputs.Sum(Bytes), outputBytes = 0;
            int outputCount = 0;
            ImageCompositionPayload[] resolved = new ImageCompositionPayload[layers.Count];
            SkiaGraphSurface? suffix = null;
            bool completed = false;
            try
            {
                for (int index = layers.Count - 1; index >= 0; index--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var layer = layers[index]; var bounds = Aligned(layer.Color.Descriptor.Bounds, context);
                    Preflight(2, checked(SizeBytes(bounds, context) * 2), "ResolveCompositionOpacityLayers");
                    using var sourceColor = InputShader(layer.Color, GraphEdgeMode.Transparent);
                    using var sourceErasure = InputShader(layer.Erasure, GraphEdgeMode.Transparent);
                    if (suffix is null)
                        resolved[index] = RasterPayloadPair(bounds, context, sourceColor, sourceErasure, resources, opacity, opacity, cancellationToken);
                    else
                    {
                        using var suffixShader = InputShader(suffix, GraphEdgeMode.Transparent);
                        var uniforms = new UniformValues([new("opacity", UniformValue.Float(opacity))]);
                        using var colorShader = _payloadPrograms.OpacityColor.CreateShader(uniforms, new Dictionary<string, RuntimeShaderInput>
                            { ["source"] = new(sourceColor, ShaderSurfaceKind.Color), ["suffix"] = new(suffixShader, ShaderSurfaceKind.Coverage) }, cancellationToken);
                        using var erasureShader = _payloadPrograms.OpacityErasure.CreateShader(uniforms, new Dictionary<string, RuntimeShaderInput>
                            { ["source"] = new(sourceErasure, ShaderSurfaceKind.Coverage), ["suffix"] = new(suffixShader, ShaderSurfaceKind.Coverage) }, cancellationToken);
                        resolved[index] = RasterPayloadPair(bounds, context, colorShader, erasureShader, resources, 1, 1, cancellationToken);
                    }
                    outputCount += 2; outputBytes = checked(outputBytes + SizeBytes(bounds, context) * 2);
                    if (index == 0) continue;
                    var suffixBounds = Aligned(suffix is null ? bounds : PayloadUnion(bounds, suffix.Descriptor.Bounds), context);
                    Preflight(1, SizeBytes(suffixBounds, context), "ResolveCompositionOpacitySuffix");
                    SkiaGraphSurface nextSuffix;
                    if (suffix is null)
                        nextSuffix = Raster(suffixBounds, context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, suffixBounds, sourceErasure), resources, cancellationToken);
                    else
                    {
                        using var previousSuffix = InputShader(suffix, GraphEdgeMode.Transparent);
                        using var combined = _payloadPrograms.Erasure.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                            { ["first"] = new(sourceErasure, ShaderSurfaceKind.Coverage), ["next"] = new(previousSuffix, ShaderSurfaceKind.Coverage) }, cancellationToken);
                        nextSuffix = Raster(suffixBounds, context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, suffixBounds, combined), resources, cancellationToken);
                    }
                    suffix?.Dispose(); suffix = nextSuffix;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var result = resolved.ToImmutableArray(); completed = true; return result;
            }
            finally { suffix?.Dispose(); if (!completed) foreach (var layer in resolved) layer?.Dispose(); }

            void Preflight(int count, long bytes, string operation)
            {
                CheckBudget(checked(inputs.Length + outputCount + (suffix is null ? 0 : 1) + count),
                    checked(inputBytes + outputBytes + (suffix is null ? 0 : Bytes(suffix)) + bytes));
                resources.CheckAdditional(count, bytes, operation);
            }
        }
    }

    /// <summary>父图保留已经按独立孩子顺序合成的擦除量，与自身擦除逐像素取 max；不展开孩子图历史。</summary>
    /// <remarks>复制结果前景及两者擦除的框并集；借用输入，返回两张独立拥有的表面，透明前景仍保留继承擦除。</remarks>
    public ImageCompositionPayload InheritCompositionErasure(ImageCompositionPayload result, ImageCompositionPayload inherited,
        ImageSurfaceBudgetFrame? surfaceBudget = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(inherited);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            ValidateCompositionContext(result.Context, inherited.Context); ValidatePayload(result, cancellationToken); ValidatePayload(inherited, cancellationToken);
            var context = result.Context; var bounds = Aligned(PayloadUnion(result.Color.Descriptor.Bounds, inherited.Erasure.Descriptor.Bounds), context);
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = new[] { result.Color, result.Erasure, inherited.Erasure }.DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            CheckBudget(inputs.Length + 2, checked(inputs.Sum(Bytes) + SizeBytes(bounds, context) * 2));
            resources.CheckAdditional(2, checked(SizeBytes(bounds, context) * 2), "InheritCompositionErasure");
            using var color = InputShader(result.Color, GraphEdgeMode.Transparent);
            using var ownErasure = InputShader(result.Erasure, GraphEdgeMode.Transparent); using var childErasure = InputShader(inherited.Erasure, GraphEdgeMode.Transparent);
            using var erasure = _coverageUnion.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                { ["a"] = new(ownErasure, ShaderSurfaceKind.Coverage), ["b"] = new(childErasure, ShaderSurfaceKind.Coverage) }, cancellationToken);
            return RasterPayloadPair(bounds, context, color, erasure, resources, 1, 1, cancellationToken);
        }
    }

    private ImageCompositionPayload RasterPayloadPair(GraphRect bounds, ImageGraphFrameContext context, SKShader color, SKShader erasure,
        ImageSurfaceBudgetFrame resources, double colorOpacity, double erasureOpacity, CancellationToken cancellationToken)
    {
        var foreground = Raster(bounds, context, ShaderSurfaceKind.Color, canvas => Draw(canvas, bounds, color, opacity: colorOpacity), resources, cancellationToken);
        try
        {
            var coverage = Raster(bounds, context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, bounds, erasure, opacity: erasureOpacity), resources, cancellationToken);
            return new(foreground, coverage, context, AcquireDeviceLease());
        }
        catch { foreground.Dispose(); throw; }
    }
}
