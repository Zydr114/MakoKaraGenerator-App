using System.Runtime.InteropServices;
using MakoKaraGenerator.Rendering.Skia;
using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class VectorOverlayRenderingTests
{
    private static readonly Fx.ColorRgba Red = new(1, .2, .1, .5);
    private static Fx.Trail Straight => new([new(new(40, 24), 8, Red), new(new(80, 24), 8, Red)]);
    [Theory]
    [InlineData(320, 180)] [InlineData(640, 360)] [InlineData(640, 180)]
    public void 直带与平端线段在三种密度与独立矩形Alpha相同且RGB误差至多一(int width, int height)
    {
        var snapshot = VisualTestFixture.Snapshot(); var target = new RenderTarget { PixelWidth = width, PixelHeight = height };
        using SkiaRenderPipeline renderer = new();
        PreparedRenderFrame frame = new(snapshot, 18_001, Fx.OutputMode.Transparent, target, renderer.GetSceneFrame(snapshot, 18_001), []);
        using var basis = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { Target = target, PreparedFrame = frame, TransparentBackground = true });
        foreach (Fx.OverlayBatch batch in new Fx.OverlayBatch[] {
            new Fx.TrailBatch(new("fx"), new("path"), Fx.OverlayLayer.AboveText, [Straight]),
            new Fx.SegmentBatch(new("fx"), new("path"), Fx.OverlayLayer.AboveText, [new(new(40, 24), new(80, 24), 8, Red)]) })
        {
            var output = frame.WithOverlayOutput([batch], []);
            using var actual = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { Target = target, PreparedFrame = output, TransparentBackground = true });
            var expected = Rectangle(basis, target.ScaleXOf(snapshot.Composition), target.ScaleYOf(snapshot.Composition), Red);
            var pixels = actual.PixelMemory.ToArray();
            for (int index = 0; index < pixels.Length; index++)
                if (index % 4 == 3) Assert.Equal(expected[index], pixels[index]);
                else Assert.InRange(Math.Abs(expected[index] - pixels[index]), 0, 1);
            Assert.True(actual.TryGetPixel((int)(60 * target.ScaleXOf(snapshot.Composition)), (int)(24 * target.ScaleYOf(snapshot.Composition)), out var pixel));
            Assert.Equal(128, pixel.Alpha); Assert.Equal(255, pixel.Red);
        }
    }
    [Fact]
    public void 重复截面使用最后颜色宽度且零宽尖端折点反向不会生成非法几何()
    {
        var snapshot = VisualTestFixture.Snapshot(); using SkiaRenderPipeline renderer = new(); var target = VisualTestFixture.Target(snapshot);
        PreparedRenderFrame frame = new(snapshot, 18_001, Fx.OutputMode.Transparent, target, renderer.GetSceneFrame(snapshot, 18_001), []);
        using var basis = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { PreparedFrame = frame, TransparentBackground = true });
        Fx.ColorRgba blue = new(0, 0, 1, .5);
        var repeated = new Fx.Trail([new(new(40, 24), 3, Red), new(new(40, 24), 8, blue), new(new(80, 24), 8, blue)]);
        using var duplicate = Render(repeated); Assert.Equal(Rectangle(basis, 1, 1, blue), duplicate.PixelMemory.ToArray());
        using var zero = Render(new([new(new(40, 24), 0, Red), new(new(40, 24), 0, Red)]));
        Assert.Equal(basis.PixelMemory.ToArray(), zero.PixelMemory.ToArray());
        using var zeroWidth = Render(new([new(new(40, 24), 0, Red), new(new(80, 24), 0, Red)]));
        Assert.Equal(basis.PixelMemory.ToArray(), zeroWidth.PixelMemory.ToArray());
        using var tinySeparation = Render(new([new(new(0, 24), 8, Red), new(new(1e-200, 24), 8, Red)]));
        Assert.Equal(basis.PixelMemory.ToArray(), tinySeparation.PixelMemory.ToArray());
        using var zeroSegment = renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { TransparentBackground = true,
            PreparedFrame = frame.WithOverlayOutput([new Fx.SegmentBatch(new("fx"), new("zero"), Fx.OverlayLayer.AboveText, [new(new(40, 24), new(40, 24), 8, Red)])], []) });
        Assert.Equal(basis.PixelMemory.ToArray(), zeroSegment.PixelMemory.ToArray());
        using var tapered = Render(new([new(new(40, 24), 0, Red), new(new(80, 24), 16, Red)]));
        Assert.True(tapered.TryGetPixel(70, 24, out var tip)); Assert.Equal(128, tip.Alpha);
        Assert.True(tapered.TryGetPixel(70, 31, out var outside)); Assert.Equal(0, outside.Alpha);
        using var corner = Render(new([new(new(40, 24), 8, Red), new(new(80, 24), 8, Red), new(new(80, 50), 8, Red)]));
        Assert.True(corner.TryGetPixel(78, 24, out var joined)); Assert.Equal(128, joined.Alpha);
        using var reverse = Render(new([new(new(40, 24), 8, Red), new(new(80, 24), 8, Red), new(new(40, 24), 8, Red)]));
        Assert.True(reverse.TryGetPixel(60, 24, out var overlap)); Assert.InRange(overlap.Alpha, 191, 192);
        using var nearReverse = Render(new([new(new(40, 24), 8, Red), new(new(80, 48), 8, Red), new(new(40, 24 + 1e-14), 8, Red)]));
        Assert.True(nearReverse.TryGetPixel(60, 36, out var reversed)); Assert.InRange(reversed.Alpha, 191, 192);
        using var fading = Render(new([new(new(40, 24), 8, new(1, 0, 0, 0)), new(new(80, 24), 8, new(1, 0, 0, 1))]));
        Assert.True(fading.TryGetPixel(60, 24, out var gradient)); Assert.InRange(gradient.Alpha, 130, 132); Assert.Equal(255, gradient.Red);
        RenderFrame Render(Fx.Trail trail) => renderer.Render(RenderRequest.At(snapshot, frame.TimeUs) with { TransparentBackground = true,
            PreparedFrame = frame.WithOverlayOutput([new Fx.TrailBatch(new("fx"), new("path"), Fx.OverlayLayer.AboveText, [trail])], []) });
    }
    [Fact]
    public void 准备帧跨类型同名失败且图或组透明度复制保留完整图元()
    {
        var snapshot = VisualTestFixture.Snapshot(); using SkiaRenderPipeline renderer = new();
        PreparedRenderFrame frame = new(snapshot, 18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), renderer.GetSceneFrame(snapshot, 18_001), []);
        Fx.TrailBatch trail = new(new("fx"), new("shared"), Fx.OverlayLayer.AboveText, [Straight]);
        Fx.DotBatch dots = new(new("fx"), new("shared"), Fx.OverlayLayer.AboveText, []);
        Assert.Equal("InvalidPreparedOverlay", Assert.Throws<PreparedFrameException>(() => frame.WithOverlayOutput([trail, dots], [])).Code);
        Assert.Equal("VisualOutputBudgetExceeded", Assert.Throws<PreparedFrameException>(() => frame.WithOverlayOutput([trail], [], new(1))).Code);
        var prepared = frame.WithOverlayOutput([trail], []); Assert.Empty(prepared.DotBatches); Assert.Single(prepared.OverlayBatches);
        var group = prepared.FinalGroupGeometry[0].Target;
        var copied = prepared.WithGroupOpacities([new(new("fx"), "artifact", new(group, .5))]);
        Assert.NotSame(prepared, copied); Assert.Equal(prepared.OverlayBatches, copied.OverlayBatches);
        var graph = new Fx.ResourceCatalog("package", "sha256:" + new string('a', 64), [], ["graph"], []).GetGraph("graph");
        var withGraph = copied.WithImageEffects([new(new("fx"), "artifact", new(new Fx.ImageEffectTarget.Group(group), graph, Fx.ParameterValues.Empty))]);
        Assert.NotSame(copied, withGraph); Assert.Equal(prepared.OverlayBatches, withGraph.OverlayBatches);
        var removed = withGraph.WithImageEffects([]).WithGroupOpacities([]); Assert.Equal(prepared.OverlayBatches, removed.OverlayBatches);
    }
    [Theory]
    [InlineData("segment-large")] [InlineData("segment-tiny")] [InlineData("trail-large")] [InlineData("trail-tiny")] [InlineData("dot-half-underflow")]
    public void 各图元后端精度越限具名拒绝并不输出无效果原图(string mode)
    {
        var snapshot = VisualTestFixture.Snapshot(); using SkiaRenderPipeline renderer = new();
        PreparedRenderFrame frame = new(snapshot, 18_001, Fx.OutputMode.Transparent, VisualTestFixture.Target(snapshot), renderer.GetSceneFrame(snapshot, 18_001), []);
        var point = new Fx.Point2(mode.EndsWith("large", StringComparison.Ordinal) ? 1e100 : 40, 24);
        double width = mode.EndsWith("tiny", StringComparison.Ordinal) ? double.Epsilon : 8;
        Fx.OverlayBatch batch = mode.StartsWith("segment", StringComparison.Ordinal)
            ? new Fx.SegmentBatch(new("fx"), new("path"), Fx.OverlayLayer.AboveText, [new(point, new(80, 24), width, Red)])
            : mode.StartsWith("trail", StringComparison.Ordinal)
            ? new Fx.TrailBatch(new("fx"), new("path"), Fx.OverlayLayer.AboveText, [new([new(point, width, Red), new(new(80, 24), width, Red)])])
            : new Fx.DotBatch(new("fx"), new("path"), Fx.OverlayLayer.AboveText, [new(new(40, 24), (double)float.Epsilon * .4, Red)]);
        Assert.Equal("UnsupportedNativeOverlay", Assert.Throws<PreparedFrameException>(() => renderer.Render(RenderRequest.At(snapshot, frame.TimeUs)
            with { TransparentBackground = true, PreparedFrame = frame.WithOverlayOutput([batch], []) })).Code);
    }
    private static byte[] Rectangle(RenderFrame basis, double sx, double sy, Fx.ColorRgba color)
    {
        using SKBitmap bitmap = new(new SKImageInfo(basis.Width, basis.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var bytes = basis.PixelMemory.ToArray(); Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        using (SKCanvas canvas = new(bitmap))
        {
            canvas.Scale((float)sx, (float)sy);
            using SKPaint paint = new() { IsAntialias = true, Color = new((byte)Math.Round(color.Red * 255), (byte)Math.Round(color.Green * 255),
                (byte)Math.Round(color.Blue * 255), (byte)Math.Round(color.Alpha * 255)) };
            canvas.DrawRect(new(40, 20, 80, 28), paint);
        }
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length); return bytes;
    }
}
