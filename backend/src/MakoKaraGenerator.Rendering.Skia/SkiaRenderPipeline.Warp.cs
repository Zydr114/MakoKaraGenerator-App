using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;
using MakoKaraGenerator.Text;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private readonly ConditionalWeakTable<ImageSurfaceBudgetFrame, TextGeometryProjectionBudget> _warpProjectionBudgets = new();

    private static bool HasEffectiveWarp(TextGeometryMapping? mapping)
        => mapping?.Steps.OfType<TextGeometryMapping.Grid>().Any(static grid => !grid.Warp.IsIdentity) == true;

    /// <summary>
    /// 将一次文字绘制先在源坐标中光栅化，再用带源坐标的精确三角网格绘制到目标画布。
    /// 纹理坐标使用局部表面的像素坐标；表面外沿留出透明 gutter，避免线性采样把边缘颜色带入邻接三角。
    /// </summary>
    private void DrawWarpedTextLayer(SKCanvas target, RenderRequest request, TextTarget textTarget,
        TextGeometryMapping mapping, Rect2 sourceBounds, ImageSurfaceBudgetFrame? resources, CapturePaint? capture, Action<SKCanvas, CapturePaint?> drawSource)
    {
        request.CancellationToken.ThrowIfCancellationRequested();
        try { Draw(); }
        catch (Exception error) when (error is not (OperationCanceledException or EffectFailureException)
            && (capture?.MaterialOperation != true || error is EffectOutputException
                { Code: "WarpOutsideDomain" or "WarpGeometryBudgetExceeded" or "InvalidTextGeometryMapping" }))
        {
            string code = error switch
            {
                PreparedFrameException frame => frame.Code, EffectOutputException output => output.Code,
                ImageSurfaceBudgetException budget => budget.Code, _ => "WarpRenderingFailed",
            };
            var owner = FindWarpOwner(request.PreparedFrame!, textTarget);
            throw new EffectFailureException(owner.InstanceId, owner.ArtifactHash, "RenderTextWarp", request.TimeUs,
                code, error, owner.PackageIdentity);
        }
        void Draw()
        {
            if (!HasEffectiveWarp(mapping))
            {
                target.Save();
                try
                {
                    // Canvas concatenation is parent-first; mapping steps are leaf-first.
                    foreach (var step in mapping.Steps.Reverse())
                        if (step is TextGeometryMapping.Affine affine)
                        {
                            var m = affine.Matrix;
                            target.Concat(new SKMatrix((float)m.A, (float)m.C, (float)m.Tx,
                                (float)m.B, (float)m.D, (float)m.Ty, 0, 0, 1));
                        }
                    drawSource(target, capture);
                }
                finally { target.Restore(); }
                return;
            }
            using var ownFrame = resources is null ? _surfaceBudget.BeginFrame() : null;
            var budget = resources ?? ownFrame!;
            var mesh = mapping.ProjectRectangle(sourceBounds, _warpProjectionBudgets.GetValue(budget, static _ => new()),
                request.CancellationToken);
            if (mesh.TriangleCount == 0) return;

            const int gutter = 2;
            double density = WarpRasterDensity(mesh, target.TotalMatrix);
            double pixelLeft = Math.Floor(sourceBounds.Left * density) - gutter;
            double pixelTop = Math.Floor(sourceBounds.Top * density) - gutter;
            double left = pixelLeft / density, top = pixelTop / density;
            double width = Math.Ceiling(sourceBounds.Right * density) + gutter - pixelLeft;
            double height = Math.Ceiling(sourceBounds.Bottom * density) + gutter - pixelTop;
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || width > _surfaceBudget.Limits.MaximumPixelDimension
                || height > _surfaceBudget.Limits.MaximumPixelDimension)
                throw new PreparedFrameException("WarpSurfaceBudgetExceeded", "文字网格源表面尺寸超过固定上限。");
            long bytes = checked((long)width * (long)height * 4);
            using var reservation = budget.Reserve(1, bytes, "WarpTextSource");
            using var surface = _device.CreateSurface(new SKImageInfo((int)width, (int)height, SKColorType.Bgra8888, SKAlphaType.Premul));
            request.CancellationToken.ThrowIfCancellationRequested();
            surface.Canvas.ClipRect(new(0, 0, (float)width, (float)height));
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)density);
            surface.Canvas.Translate((float)-left, (float)-top);
            var sourceCapture = capture ?? new CapturePaint(GraphInputSource.TargetColor,
                new(true, true, true, false, SceneCaptureRegion.FillAndStroke), null);
            drawSource(surface.Canvas, sourceCapture with { Budget = budget, PixelBytes = bytes });
            request.CancellationToken.ThrowIfCancellationRequested();
            _device.FlushAndSubmit(surface);
            request.CancellationToken.ThrowIfCancellationRequested();
            using var image = surface.Snapshot()
                ?? throw new PreparedFrameException("WarpSurfaceSnapshotFailed", "文字网格源表面无法建立纹理快照。");

            SKPoint[] positions = [.. mesh.DestinationVertices.Select(ToSkPoint)];
            SKPoint[] texture = [.. mesh.SourceVertices.Select(point =>
                new SKPoint((float)((point.X - left) * density), (float)((point.Y - top) * density)))];
            using var vertices = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, texture, null, mesh.TriangleIndices.ToArray())
                ?? throw new PreparedFrameException("WarpMeshAllocationFailed", "文字网格无法分配原生顶点。");
            using var shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal,
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.SrcOver, IsAntialias = true };
            request.CancellationToken.ThrowIfCancellationRequested();
            target.DrawVertices(vertices, SKBlendMode.Modulate, paint);
        }
    }

    // 每个输出三角的 source→destination Jacobian，再乘实际画布/输出密度。
    // 最大奇异值决定全源表面采样密度，不夹低密度或静默降质；超限由表面账拒绝。
    private static double WarpRasterDensity(TextGeometryProjectionMesh mesh, SKMatrix canvas)
    {
        double maximum = 1;
        for (int index = 0; index < mesh.TriangleIndices.Length; index += 3)
        {
            int i = mesh.TriangleIndices[index], j = mesh.TriangleIndices[index + 1], k = mesh.TriangleIndices[index + 2];
            var a = mesh.SourceVertices[i]; var b = mesh.SourceVertices[j]; var c = mesh.SourceVertices[k];
            double bx = b.X - a.X, by = b.Y - a.Y, cx = c.X - a.X, cy = c.Y - a.Y;
            double determinant = bx * cy - by * cx;
            if (determinant == 0) continue;
            var x = mesh.DestinationVertices[i]; var y = mesh.DestinationVertices[j]; var z = mesh.DestinationVertices[k];
            double ja = ((y.X - x.X) * cy - (z.X - x.X) * by) / determinant;
            double jb = ((y.Y - x.Y) * cy - (z.Y - x.Y) * by) / determinant;
            double jc = ((z.X - x.X) * bx - (y.X - x.X) * cx) / determinant;
            double jd = ((z.Y - x.Y) * bx - (y.Y - x.Y) * cx) / determinant;
            double ma = canvas.ScaleX * ja + canvas.SkewX * jb, mb = canvas.SkewY * ja + canvas.ScaleY * jb;
            double mc = canvas.ScaleX * jc + canvas.SkewX * jd, md = canvas.SkewY * jc + canvas.ScaleY * jd;
            double first = ma * ma + mb * mb, second = mc * mc + md * md, cross = ma * mc + mb * md;
            double discriminant = Math.Sqrt((first - second) * (first - second) + 4 * cross * cross);
            double scale = Math.Sqrt((first + second + discriminant) / 2);
            if (!double.IsFinite(scale) || scale > float.MaxValue)
                throw new PreparedFrameException("WarpSurfaceBudgetExceeded", "网格源采样密度超出有限表面范围。");
            maximum = Math.Max(maximum, scale);
        }
        // Directed ceiling removes unstable sub-pixel density changes and never undersamples;
        // tolerate tiny IEEE-754 rounding noise above integer scales (e.g. pure translations).
        return Math.Max(1, Math.Ceiling(maximum - 1e-9));
    }

    private static PreparedTextWarpBinding FindWarpOwner(PreparedRenderFrame prepared, TextTarget target)
    {
        var exact = prepared.TextWarps.FirstOrDefault(binding => binding.Target == target && binding.Value is not null);
        if (exact is not null) return exact;
        TokenFamilyTarget? family = target switch
        {
            TextTarget.Family value => value.Value,
            TextTarget.Base value => new(value.Value.TokenId, value.Value.BindingId),
            TextTarget.Cluster value => new(value.Value.TokenId, value.Value.BindingId), _ => null,
        };
        if (family is not null)
        {
            if (target is not TextTarget.Cluster { Value.RubyId: not null })
            {
                var basic = new TextTarget.Base(new(family.TokenId, family.BindingId));
                var basis = prepared.TextWarps.FirstOrDefault(binding => binding.Target == basic && binding.Value is not null);
                if (basis is not null) return basis;
            }
            return prepared.TextWarps.FirstOrDefault(binding => binding.Target == new TextTarget.Family(family) && binding.Value is not null)
                ?? prepared.TextWarps.First(binding => binding.Value is not null && binding.Target is TextTarget.Cluster cluster
                    && cluster.Value.TokenId == family.TokenId && cluster.Value.BindingId == family.BindingId);
        }
        throw new PreparedFrameException("InvalidWarpTarget", "网格像素输出缺少固定作者归属。");
    }

    private static SKPoint ToSkPoint(Point2 point)
    {
        float x = (float)point.X, y = (float)point.Y;
        if (!float.IsFinite(x) || !float.IsFinite(y))
            throw new PreparedFrameException("UnsupportedNativeCoordinates", "文字网格顶点超出后端有限坐标范围。");
        return new(x, y);
    }

    /// <summary>准备帧映射最后一步固定为原始 world matrix；主画布已应用该矩阵时只保留局部步骤。</summary>
    private static TextGeometryMapping WithoutWorld(TextGeometryMapping mapping)
    {
        if (mapping.Steps.IsEmpty) return mapping;
        var last = mapping.Steps[^1];
        return last is TextGeometryMapping.Affine
            ? new TextGeometryMapping(mapping.Steps.RemoveAt(mapping.Steps.Length - 1))
            : mapping;
    }

    private static Rect2 TextSourceBounds(ShapedLine shaped, float origin, float baseline,
        ImmutableArray<int> drawUnitIndices)
    {
        IEnumerable<ShapedDrawUnit> units = drawUnitIndices.IsDefault
            ? shaped.DrawUnits
            : drawUnitIndices.Select(index => shaped.DrawUnits[index]);
        var selected = units.ToArray();
        if (selected.Length == 0) return new(origin, baseline, origin, baseline);
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var unit in selected)
        {
            var glyphs = unit.Glyphs;
            foreach (var glyph in glyphs)
            {
                if (glyph.InkBounds is not { } ink) continue;
                left = Math.Min(left, origin + ink.Left); top = Math.Min(top, baseline + ink.Top);
                right = Math.Max(right, origin + ink.Right); bottom = Math.Max(bottom, baseline + ink.Bottom);
            }
            if (glyphs.IsEmpty)
            {
                left = Math.Min(left, origin + unit.X); right = Math.Max(right, origin + unit.X + unit.Advance);
                top = Math.Min(top, baseline + unit.Ascent); bottom = Math.Max(bottom, baseline + unit.Descent);
            }
        }
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom))
        {
            left = origin + selected.Min(static unit => unit.X); right = origin + selected.Max(static unit => unit.X + unit.Advance);
            top = baseline + selected.Min(static unit => unit.Ascent); bottom = baseline + selected.Max(static unit => unit.Descent);
        }
        return new(left, top, Math.Max(right, left), Math.Max(bottom, top));
    }
}
