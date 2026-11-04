using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class CompiledImageGraph
{
    // One native interpretation for shape admission, input preflight and drawing.
    // Semantic bounds remain separate from density-aligned physical allocation.
    private sealed record ResolvedGraphTransform(SKMatrix Matrix, SKMatrix Inverse, GraphRect OutputBounds,
        GraphRect PhysicalBounds, bool IsOverride);

    /// <summary>在捕获或旁路前校验完整帧覆盖，不分配表面；Execute 仍独立重验。</summary>
    internal void ValidateFrameBindings(ImageGraphFrameContext frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this); cancellationToken.ThrowIfCancellationRequested();
            ValidateFrame(frame); ImageGraphBindings.ValidateParameters(Plan, frame.Parameters);
            MaterialUniformValidation.Validate(Plan, frame.Uniforms);
            var active = ActiveReferences(Plan.Definition.Output, Plan.Definition.CompositeCoverage);
            var ordered = Plan.Nodes.Where(node => active.Contains(GraphReference.Node(node.Id))).ToArray();
            var transforms = ResolveTransforms(frame, ordered, cancellationToken);
            _ = Shapes(frame, ordered, transforms, cancellationToken);
        }
    }

    private Dictionary<string, ResolvedGraphTransform> ResolveTransforms(ImageGraphFrameContext frame,
        ImageGraphNode[] activeNodes, CancellationToken cancellationToken)
    {
        var nodes = Plan.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (string id in frame.Transforms.Values.Keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!nodes.TryGetValue(id, out var node)) throw Error("GraphTransformNodeUnknown", "transforms." + id, "覆盖节点未在固定图中声明。");
            if (node is not ImageGraphNode.Transform) throw Error("GraphTransformNodeTypeMismatch", "transforms." + id, "覆盖只能指向真实 Transform 节点。");
        }
        // A key can name only one of the already pass-bounded compiled nodes.
        // Validate dormant overrides too; opacity/transparent bypass is not admission.
        var selected = activeNodes.OfType<ImageGraphNode.Transform>().Select(node => node.Id)
            .Concat(frame.Transforms.Values.Keys).ToHashSet(StringComparer.Ordinal);
        var expansions = new Dictionary<GraphReference, double>();
        foreach (var input in Plan.Definition.Inputs) expansions.Add(GraphReference.Input(input.Id), 0);
        var result = new Dictionary<string, ResolvedGraphTransform>(StringComparer.Ordinal);
        foreach (var node in Plan.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double expand = ImageGraphBindings.Expansion(node.Bounds.Expand, frame.Parameters);
            double total = ImageGraphCompiler.References(node).Select(reference => expansions[reference]).DefaultIfEmpty(0).Max() + expand;
            expansions.Add(GraphReference.Node(node.Id), total);
            if (node is not ImageGraphNode.Transform transform || !selected.Contains(node.Id)) continue;
            bool isOverride = frame.Transforms.Values.TryGetValue(node.Id, out var value);
            var matrix = isOverride ? new SceneMatrix(value!.Matrix.A, value.Matrix.B, value.Matrix.C, value.Matrix.D, value.Matrix.Tx, value.Matrix.Ty) : transform.Matrix;
            var bounds = isOverride ? new GraphRect(value!.OutputBounds.Left, value.OutputBounds.Top, value.OutputBounds.Right, value.OutputBounds.Bottom) : transform.OutputBounds;
            string field = "nodes." + node.Id;
            try
            {
                SKMatrix native = Matrix(matrix);
                if (!native.TryInvert(out var inverse)) throw Error("GraphNativeMatrixRange", field + ".matrix", "原生矩阵不可逆。");
                ValidateNativeBounds(bounds, field + ".outputBounds");
                var physical = Aligned(bounds, frame);
                ValidateNativeBounds(physical, field + ".physicalBounds");
                // Validate the actual density and origin matrix before any allocation.
                _ = Matrix(new(frame.DensityX, 0, 0, frame.DensityY, -physical.Left * frame.DensityX, -physical.Top * frame.DensityY));
                if (isOverride)
                {
                    var nodeLimit = frame.TargetBounds.Expand(total);
                    var graphLimit = frame.TargetBounds.Expand(Plan.Definition.MaximumExpandPx);
                    if (!double.IsFinite(total) || expand > node.Bounds.MaximumExpandPx || total > Plan.Definition.MaximumExpandPx
                        || !nodeLimit.IsValid || !graphLimit.IsValid || !nodeLimit.Contains(bounds) || !graphLimit.Contains(bounds))
                        throw Error("GraphTransformBoundsExceeded", field + ".outputBounds", "动态输出框超出当前节点外扩或图最大外扩声明。");
                }
                var resolved = new ResolvedGraphTransform(native, inverse, bounds, physical, isOverride);
                _ = InverseSampleBounds(resolved, node, field); // Includes aligned output ROI, never just its center.
                result.Add(node.Id, resolved);
            }
            catch (ImageGraphException error) when (error.Field is "matrix" or "geometry" or "bounds")
            { throw Error(error.Code, field + ".transform", error.Message); }
        }
        return result;
    }

    private GraphRect InverseSampleBounds(ResolvedGraphTransform transform, ImageGraphNode node, string field)
    {
        var bounds = transform.PhysicalBounds;
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var point in Corners(bounds))
        {
            // Float32 inverse and coordinates are the same values consumed by Skia.
            float x = Native(point.X), y = Native(point.Y);
            var mapped = transform.Inverse.MapPoint(x, y);
            if (!float.IsFinite(mapped.X) || !float.IsFinite(mapped.Y))
                throw Error("GraphTransformSamplingRange", field + ".matrix", "逆映射采样坐标超出原生范围。");
            // The declaration is an axis-aligned per-axis logical offset budget.
            // Existing v1 fixed matrices historically had no such check; preserve it.
            if (transform.IsOverride && (Math.Abs((double)mapped.X - x) > node.Bounds.MaximumSampleOffsetPx
                || Math.Abs((double)mapped.Y - y) > node.Bounds.MaximumSampleOffsetPx))
                throw Error("GraphTransformSamplingExceeded", field + ".matrix", "动态逆映射采样偏移超出节点声明。");
            left = Math.Min(left, mapped.X); top = Math.Min(top, mapped.Y); right = Math.Max(right, mapped.X); bottom = Math.Max(bottom, mapped.Y);
        }
        var sampled = new GraphRect(left, top, right, bottom);
        if (!sampled.IsValid) throw Error("GraphTransformSamplingRange", field + ".matrix", "逆采样 ROI 在原生精度下退化。");
        return sampled;
    }

    private void PreflightTransformInput(ImageGraphNode.Transform node, ResolvedGraphTransform transform,
        GraphSurfaceDescriptor source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); string field = "nodes." + node.Id;
        try
        {
            ValidateNativeBounds(source.Bounds, field + ".inputBounds");
            var inputToLogical = new SceneMatrix(1 / source.DensityX, 0, 0, 1 / source.DensityY, source.Bounds.Left, source.Bounds.Top);
            var inputMatrix = Matrix(inputToLogical);
            // Use native concatenation too: double cancellation must not hide an
            // overflowing float32 product inside InputShader.WithLocalMatrix.
            var combined = SKMatrix.Concat(transform.Matrix, inputMatrix);
            _ = Matrix(ToScene(combined));
            if (!inputMatrix.TryInvert(out var logicalToInput))
                throw Error("GraphNativeMatrixRange", field + ".inputMatrix", "输入像素映射在原生精度下不可逆。");
            var sampled = InverseSampleBounds(transform, node, field);
            // Linear filtering may touch the adjacent source texel. Do not clip this
            // footprint to output bounds, especially at reduced/anisotropic density.
            double gutterX = .5 / source.DensityX, gutterY = .5 / source.DensityY;
            var footprint = new GraphRect(sampled.Left - gutterX, sampled.Top - gutterY, sampled.Right + gutterX, sampled.Bottom + gutterY);
            ValidateNativeBounds(footprint, field + ".sampleBounds");
            foreach (var point in Corners(footprint))
            {
                var pixel = logicalToInput.MapPoint(Native(point.X), Native(point.Y));
                if (!float.IsFinite(pixel.X) || !float.IsFinite(pixel.Y))
                    throw Error("GraphTransformSamplingRange", field + ".inputDensity", "逆采样像素坐标超出原生范围。");
            }
            // No ROI allocation is introduced here. Preserve the full finite source
            // domain: Decal needs its transparent exterior, Clamp its real border,
            // Repeat its whole period. Enlarging or cropping it changes semantics.
        }
        catch (ImageGraphException error) when (error.Field is "matrix" or "geometry" or "bounds")
        { throw Error(error.Code, field + ".inputTransform", error.Message); }
    }
    private void ValidateNativeBounds(GraphRect bounds, string field)
    {
        if (!bounds.IsValid) throw Error("GraphNativeBoundsRange", field, "有限非空逻辑框必需。");
        float left = Native(bounds.Left), top = Native(bounds.Top), right = Native(bounds.Right), bottom = Native(bounds.Bottom);
        if (right <= left || bottom <= top) throw Error("GraphNativeBoundsRange", field, "逻辑框在原生 float32 精度下退化。");
    }
    private static SceneMatrix ToScene(SKMatrix matrix) => new(matrix.ScaleX, matrix.SkewY, matrix.SkewX, matrix.ScaleY, matrix.TransX, matrix.TransY);
    private static IEnumerable<CanvasPoint> Corners(GraphRect bounds)
    {
        yield return new(bounds.Left, bounds.Top); yield return new(bounds.Right, bounds.Top);
        yield return new(bounds.Right, bounds.Bottom); yield return new(bounds.Left, bounds.Bottom);
    }
}
