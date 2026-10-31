using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledImageGraph
{
    /// <summary>按绘制顺序合成独立孩子的前景与擦除量，再应用本层共同透明度；不合入背景。</summary>
    /// <remarks>借用输入；返回两个独立拥有的表面。不能用此方法串接同一目标的历史图结果。</remarks>
    public ImageCompositionPayload ComposePayloads(IReadOnlyList<ImageCompositionPayload> payloads,
        ImageSurfaceBudgetFrame? surfaceBudget = null, double opacity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            if (payloads.Count == 0 || payloads.Any(payload => payload is null))
                throw Error("GraphCompositionPayloadEmpty", "payloads", "顺序合成须提供存活的独立孩子产物。");
            ValidateCompositionOpacity(opacity);
            var context = payloads[0].Context;
            foreach (var payload in payloads) { ValidateCompositionContext(payload.Context, context); ValidatePayload(payload, cancellationToken); }
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = payloads.SelectMany(payload => new[] { payload.Color, payload.Erasure }).DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            long inputBytes = inputs.Sum(Bytes);
            SkiaGraphSurface currentColor = payloads[0].Color, currentErasure = payloads[0].Erasure;
            ImageCompositionPayload? owned = null;
            try
            {
                // 一个孩子也复制独立产物，返回对象不会夺取或依赖调用者的表面生命周期。
                if (payloads.Count == 1)
                {
                    var bounds = Aligned(currentColor.Descriptor.Bounds, context);
                    PreflightPair(bounds);
                    using var color = InputShader(currentColor, GraphEdgeMode.Transparent); using var erasure = InputShader(currentErasure, GraphEdgeMode.Transparent);
                    return RasterPair(bounds, color, erasure, opacity);
                }
                for (int index = 1; index < payloads.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = payloads[index];
                    var bounds = Aligned(PayloadUnion(currentColor.Descriptor.Bounds, next.Color.Descriptor.Bounds), context);
                    PreflightPair(bounds);
                    using var firstColor = InputShader(currentColor, GraphEdgeMode.Transparent); using var nextColor = InputShader(next.Color, GraphEdgeMode.Transparent);
                    using var firstErasure = InputShader(currentErasure, GraphEdgeMode.Transparent); using var nextErasure = InputShader(next.Erasure, GraphEdgeMode.Transparent);
                    using var colorShader = _payloadPrograms.Color.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                        { ["first"] = new(firstColor, ShaderSurfaceKind.Color), ["next"] = new(nextColor, ShaderSurfaceKind.Color), ["erase"] = new(nextErasure, ShaderSurfaceKind.Coverage) }, cancellationToken);
                    using var erasureShader = _payloadPrograms.Erasure.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                        { ["first"] = new(firstErasure, ShaderSurfaceKind.Coverage), ["next"] = new(nextErasure, ShaderSurfaceKind.Coverage) }, cancellationToken);
                    var combined = RasterPair(bounds, colorShader, erasureShader, index == payloads.Count - 1 ? opacity : 1);
                    owned?.Dispose(); owned = combined; currentColor = combined.Color; currentErasure = combined.Erasure;
                }
                var result = owned!; owned = null; return result;
            }
            finally { owned?.Dispose(); }

            void PreflightPair(GraphRect bounds)
            {
                int count = inputs.Length + (owned is null ? 0 : 2);
                long bytes = checked(inputBytes + (owned is null ? 0 : Bytes(owned.Color) + Bytes(owned.Erasure)) + SizeBytes(bounds, context) * 2);
                CheckBudget(count + 2, bytes);
                resources.CheckAdditional(2, checked(SizeBytes(bounds, context) * 2), "ComposePayloads");
            }
            ImageCompositionPayload RasterPair(GraphRect bounds, SkiaSharp.SKShader color, SkiaSharp.SKShader erasure, double factor)
            {
                var foreground = Raster(bounds, context, ShaderSurfaceKind.Color, canvas => Draw(canvas, bounds, color, opacity: factor), resources, cancellationToken);
                try
                {
                    var coverage = Raster(bounds, context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, bounds, erasure, opacity: factor), resources, cancellationToken);
                    return new(foreground, coverage, context, AcquireDeviceLease());
                }
                catch { foreground.Dispose(); throw; }
            }
        }
    }

    /// <summary>将可传递产物应用到真实下层，保留独立擦除量；可再应用一层共同透明度。</summary>
    /// <remarks>借用产物和下层，返回拥有的实际合成表面；不编译作者源码。</remarks>
    public SkiaGraphSurface CompositePayload(ImageCompositionPayload payload, SkiaGraphSurface below,
        ImageSurfaceBudgetFrame? surfaceBudget = null, double opacity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload); ArgumentNullException.ThrowIfNull(below);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested(); ValidateCompositionOpacity(opacity);
            ValidatePayload(payload, cancellationToken); ValidateSurface(below, ShaderSurfaceKind.Color, "payload.below", cancellationToken);
            if (!below.Descriptor.Bounds.Contains(payload.Color.Descriptor.Bounds) || !below.Descriptor.Bounds.Contains(payload.Erasure.Descriptor.Bounds))
                throw Error("GraphCompositeBoundsExceeded", "payload", "下层框不能裁掉前景或真实擦除范围。");
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = new[] { payload.Color, payload.Erasure, below }.DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            var context = payload.Context; var bounds = Aligned(below.Descriptor.Bounds, context);
            CheckBudget(inputs.Length + 1, checked(inputs.Sum(Bytes) + SizeBytes(bounds, context)));
            using var color = InputShader(payload.Color, GraphEdgeMode.Transparent); using var erasure = InputShader(payload.Erasure, GraphEdgeMode.Transparent);
            using var background = InputShader(below, GraphEdgeMode.Transparent);
            using var shader = _replace.CreateShader(new([new("opacity", UniformValue.Float(opacity))]), new Dictionary<string, RuntimeShaderInput>
                { ["result"] = new(color, ShaderSurfaceKind.Color), ["coverage"] = new(erasure, ShaderSurfaceKind.Coverage), ["below"] = new(background, ShaderSurfaceKind.Color) }, cancellationToken);
            return Raster(bounds, context, ShaderSurfaceKind.Color, canvas => Draw(canvas, bounds, shader), resources, cancellationToken);
        }
    }

    private void ValidatePayload(ImageCompositionPayload payload, CancellationToken cancellationToken)
    {
        ValidateFrame(payload.Context);
        ValidateSurface(payload.Color, ShaderSurfaceKind.Color, "payload.color", cancellationToken);
        ValidateSurface(payload.Erasure, ShaderSurfaceKind.Coverage, "payload.erasure", cancellationToken);
        if (payload.Color.Descriptor != payload.Erasure.Descriptor)
            throw Error("GraphCompositionPayloadGeometryMismatch", "payload", "前景和擦除量必须共享真实框及密度。");
    }
}
