using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledImageGraph
{
    /// <summary>提取同目标有序图链的独立前景与实际擦除量；复制产物、应用共同透明度一次，不捕获背景。</summary>
    /// <remarks>同目标历史替换范围取 max；独立孩子的顺序合成另用 ComposePayloads。最后产物须属于此编译图。</remarks>
    public ImageCompositionPayload CaptureCompositionPayload(IReadOnlyList<ImageGraphResult> results,
        ImageSurfaceBudgetFrame? surfaceBudget = null, double opacity = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            var last = ValidateCompositionChain(results, opacity, cancellationToken);
            var masks = CompositionMasks(results);
            foreach (var mask in masks) ValidateSurface(mask, ShaderSurfaceKind.Coverage, "payload.coverage", cancellationToken);
            var context = last.Context;
            var bounds = Aligned(masks.Select(mask => mask.Descriptor.Bounds).Append(last.Output.Descriptor.Bounds).Aggregate(PayloadUnion), context);
            using var ownFrame = surfaceBudget is null ? _surfaceBudget.BeginFrame() : null;
            var resources = surfaceBudget ?? ownFrame!;
            var inputs = masks.Append(last.Output).DistinctBy(surface => surface.Image.Handle).ToArray();
            using var borrowed = resources.BorrowInputs(inputs);
            long inputBytes = inputs.Sum(Bytes);
            SkiaGraphSurface? union = null, color = null, erasure = null;
            // 无替换时借用前景的 raw alpha；没有额外空蒙版或伪覆盖分配。
            using var alpha = masks.Length == 0 ? last.Output.BorrowAs(ShaderSurfaceKind.Coverage) : null;
            var accumulated = masks.Length == 0 ? alpha! : masks[0];
            try
            {
                for (int i = 1; i < masks.Length; i++)
                {
                    var shape = Aligned(PayloadUnion(accumulated.Descriptor.Bounds, masks[i].Descriptor.Bounds), context);
                    CheckBudget(inputs.Length + (union is null ? 1 : 2), checked(inputBytes + (union is null ? 0 : Bytes(union)) + SizeBytes(shape, context)));
                    using var left = InputShader(accumulated, GraphEdgeMode.Transparent); using var right = InputShader(masks[i], GraphEdgeMode.Transparent);
                    using var shader = _coverageUnion.CreateShader(UniformValues.Empty, new Dictionary<string, RuntimeShaderInput>
                        { ["a"] = new(left, ShaderSurfaceKind.Coverage), ["b"] = new(right, ShaderSurfaceKind.Coverage) }, cancellationToken);
                    var next = Raster(shape, context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, shape, shader), resources, cancellationToken);
                    union?.Dispose(); union = next; accumulated = next;
                }
                int count = inputs.Length + (union is null ? 0 : 1);
                long bytes = checked(inputBytes + (union is null ? 0 : Bytes(union)));
                CheckBudget(count + 2, checked(bytes + SizeBytes(bounds, context) * 2));
                resources.CheckAdditional(2, checked(SizeBytes(bounds, context) * 2), "CaptureCompositionPayload");
                using var foreground = InputShader(last.Output, GraphEdgeMode.Transparent);
                color = Raster(bounds, context, ShaderSurfaceKind.Color, canvas => Draw(canvas, bounds, foreground, opacity: opacity), resources, cancellationToken);
                using var coverage = InputShader(accumulated, GraphEdgeMode.Transparent);
                using var extraction = _payloadPrograms.Extract.CreateShader(new([new("sourceOver", UniformValue.Int(IsReplacement(last) ? 0 : 1)), new("opacity", UniformValue.Float(opacity))]),
                    new Dictionary<string, RuntimeShaderInput> { ["color"] = new(foreground, ShaderSurfaceKind.Color), ["coverage"] = new(coverage, ShaderSurfaceKind.Coverage) }, cancellationToken);
                erasure = Raster(bounds, context, ShaderSurfaceKind.Coverage, canvas => Draw(canvas, bounds, extraction), resources, cancellationToken);
                var payload = new ImageCompositionPayload(color, erasure, context, AcquireDeviceLease()); color = null; erasure = null; return payload;
            }
            finally { color?.Dispose(); erasure?.Dispose(); union?.Dispose(); }
        }
    }
    private static GraphRect PayloadUnion(GraphRect a, GraphRect b)
        => new(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
}
