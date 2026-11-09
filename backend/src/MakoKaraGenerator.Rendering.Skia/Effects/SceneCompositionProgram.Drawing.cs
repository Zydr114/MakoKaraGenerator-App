using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using SkiaSharp;

namespace MakoKaraGenerator.Rendering.Skia;

internal sealed partial class SceneCompositionProgram
{
    private SkiaGraphSurface Raster(GraphRect shape, ImageGraphFrameContext frame, ShaderSurfaceKind kind, Action<SKCanvas> draw,
        ImageSurfaceBudgetFrame resources, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        resources.ChargeScenePass(cancellationToken);
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Raster);
        int width = checked((int)Math.Round(shape.Width * frame.DensityX)), height = checked((int)Math.Round(shape.Height * frame.DensityY));
        ImageSurfaceReservation? reservation = resources.Reserve(1, checked((long)width * height * 4), "ImageGraphRaster");
        try
        {
            using var srgb = SKColorSpace.CreateSrgb();
            using var surface = _device.CreateSurface(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
            var canvas = surface.Canvas; canvas.ClipRect(new(0, 0, width, height)); canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(Matrix(new(frame.DensityX, 0, 0, frame.DensityY, -shape.Left * frame.DensityX, -shape.Top * frame.DensityY)));
            cancellationToken.ThrowIfCancellationRequested(); draw(canvas); cancellationToken.ThrowIfCancellationRequested();
            if (_pixelChecks) _device.FlushAndSubmit(surface);
            var image = surface.Snapshot();
            if (image == null) throw Error("GraphSurfaceSnapshotFailed", "bounds", "无法固定产物。");
            try
            {
                bool check = _pixelChecks && kind == ShaderSurfaceKind.Color;
                if (check) ValidateColorPixels(image, "output", cancellationToken);
                var result = SkiaGraphSurface.Own(image, new(shape, GraphCoordinateSpace.Canvas, SceneMatrix.Identity, frame.DensityX, frame.DensityY), kind, _device, reservation, AcquireDeviceLease());
                if (check) result.MarkColorPixelsValidated(_device);
                reservation = null; return result;
            }
            catch { image.Dispose(); throw; }
        }
        finally { reservation?.Dispose(); }
    }
    private void ValidateColorPixels(SKImage image, string field, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = ReadImagePixels(image, field, owned: true);
        ValidatePremultiplied(buffer, image.Width, image.Height, field, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ValidateColorPixels(SkiaGraphSurface surface, string field, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (surface.HasValidatedColorPixels(_device)) return;
        using var buffer = ReadImagePixels(surface, field);
        ValidatePremultiplied(buffer, surface.Image.Width, surface.Image.Height, field, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        surface.MarkColorPixelsValidated(_device);
    }

    private static void ValidatePremultiplied(ImagePixelReadbackLease buffer, int width, int height, string field, CancellationToken cancellationToken)
    {
        using var performance = PerformanceTelemetry.Measure(PerformancePhase.Validation);
        int rowBytes = checked(width * 4);
        if (buffer.Length < checked(rowBytes * height)) throw Error("GraphPixelsUnavailable", field, "图像读回缓冲长度不足。");
        ReadOnlySpan<byte> bytes = buffer.Buffer.AsSpan(0, checked(rowBytes * height));
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> row = bytes.Slice(checked(y * rowBytes), rowBytes);
            for (int x = 0; x < row.Length; x += 4)
                if (row[x] > row[x + 3] || row[x + 1] > row[x + 3] || row[x + 2] > row[x + 3])
                    throw Error("GraphColorNotPremultiplied", field, "实际颜色像素的 RGB 超过 alpha；不静默修正 shader 或输入图像。");
        }
    }

    private ImagePixelReadbackLease ReadImagePixels(SKImage image, string field, ISkiaRenderDevice? device = null, bool owned = false)
    {
        device ??= _device;
        int rowBytes = checked(image.Width * 4);
        var pixels = ImagePixelReadbackPool.Rent(checked(rowBytes * image.Height));
        unsafe
        {
            try
            {
                fixed (byte* destination = pixels.Buffer)
                {
                    var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    bool read = owned ? device.ReadOwnedImagePixels(image, info, (IntPtr)destination, rowBytes)
                        : device.ReadPixels(image, info, (IntPtr)destination, rowBytes);
                    if (!read)
                        throw Error("GraphPixelsUnavailable", field, $"{device.Info.Backend} 图像无法 readback。");
                }
                return pixels;
            }
            catch { pixels.Dispose(); throw; }
        }
    }

    private ImagePixelReadbackLease ReadImagePixels(SkiaGraphSurface surface, string field)
    {
        var device = surface.Device ?? _device;
        return ReadImagePixels(surface.Image, field, device, surface.HasOwnedImageOn(device));
    }
    private static void Draw(SKCanvas canvas, GraphRect bounds, SKShader shader, SKBlendMode blend = SKBlendMode.Src, double opacity = 1, SKImageFilter? filter = null)
    {
        using var paint = new SKPaint { Shader = shader, BlendMode = blend, ColorF = new(1, 1, 1, (float)opacity), ImageFilter = filter, IsAntialias = false };
        canvas.DrawRect(new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom), paint);
    }
    private SKShader InputShader(SkiaGraphSurface surface, GraphEdgeMode edge)
    {
        var d = surface.Descriptor;
        SKMatrix imageToLogical = Matrix(new(1 / d.DensityX, 0, 0, 1 / d.DensityY, d.Bounds.Left, d.Bounds.Top));
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        SKImage source = surface.Image;
        SKImage? raster = null;
        if (!ReferenceEquals(surface.Device, _device))
        {
            raster = surface.Device?.RasterizeImage(source) ?? source.ToRasterImage(true);
            if (raster is null) throw Error("GraphSurfaceReadbackFailed", "input", "跨 Skia device 的图像无法转换为 CPU 图像。");
            source = raster;
        }
        try
        {
            return surface.Kind == ShaderSurfaceKind.Coverage
                ? source.ToRawShader(Edge(edge), Edge(edge), sampling, imageToLogical)
                : source.ToShader(Edge(edge), Edge(edge), sampling, imageToLogical);
        }
        finally
        {
            if (raster is not null && !ReferenceEquals(raster, surface.Image)) raster.Dispose();
        }
    }
    private static SKShaderTileMode Edge(GraphEdgeMode edge) => edge switch
    { GraphEdgeMode.Transparent => SKShaderTileMode.Decal, GraphEdgeMode.Clamp => SKShaderTileMode.Clamp, GraphEdgeMode.Repeat => SKShaderTileMode.Repeat, _ => throw new ArgumentOutOfRangeException(nameof(edge)) };
    private static SKMatrix Matrix(SceneMatrix matrix)
    {
        var native = new SKMatrix(Native(matrix.A), Native(matrix.C), Native(matrix.Tx), Native(matrix.B), Native(matrix.D), Native(matrix.Ty), 0, 0, 1);
        if (!native.TryInvert(out var inverse) || !float.IsFinite(inverse.ScaleX) || !float.IsFinite(inverse.ScaleY) || !float.IsFinite(inverse.SkewX)
            || !float.IsFinite(inverse.SkewY) || !float.IsFinite(inverse.TransX) || !float.IsFinite(inverse.TransY))
            throw Error("GraphNativeMatrixRange", "matrix", "double 矩阵无法作为可逆 float32 执行。");
        return native;
    }
    private static float Native(double value)
    {
        float converted = (float)value;
        if (!double.IsFinite(value) || !float.IsFinite(converted) || (value != 0 && converted == 0))
            throw Error("GraphNativeValueRange", "geometry", "实际 float32 溢出或非零下溢。");
        return converted;
    }
}
