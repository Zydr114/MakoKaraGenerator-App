using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Media;

namespace MakoKaraGenerator.Media.Tests;

public sealed class MediaSdrFrameConverterTests
{
    [Theory]
    [InlineData(MediaNativePixelFormat.Rgb24, 1, 3)]
    [InlineData(MediaNativePixelFormat.Rgba32, 1, 4)]
    [InlineData(MediaNativePixelFormat.Rgb48Le, 2, 3)]
    [InlineData(MediaNativePixelFormat.Rgba64Le, 2, 4)]
    public void Packed不透明全部格式与transfer分段两侧均按原生位深(MediaNativePixelFormat format, int bytes, int components)
    {
        int maximum = bytes == 1 ? 255 : 65535;
        foreach (bool bt709 in new[] { false, true })
        {
            int boundaryBelow = (int)Math.Floor((bt709 ? .081 : .04045) * maximum);
            int[] codes = [0, boundaryBelow, boundaryBelow + 1, maximum];
            byte[] packed = MediaSdrFixture.Plane(components * 4, 1, bytes, 0,
                (component, _) => component % components == 3 ? 0 : codes[component / components]);
            var source = MediaSdrFixture.Source(format, 4, 1, transfer: bt709 ? MediaTransfer.Bt709 : MediaTransfer.Srgb);
            float[] output = new float[16];
            MediaSdrFrameConverter.Convert(source, new(new(packed, packed.Length), default, default), output);
            for (int i = 0; i < 4; i++)
            {
                float linear = (float)MediaSdrFixture.Transfer(codes[i] / (double)maximum, !bt709);
                MediaSdrFixture.Close([linear, linear, linear, 1], output.AsSpan(i * 4, 4));
            }
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void PackedRGB8和16的两种transfer与straightAlpha对独立公式(bool sixteenBit, bool bt709)
    {
        int maximum = sixteenBit ? 65535 : 255, sampleBytes = sixteenBit ? 2 : 1;
        int[][] pixels = [[0, maximum, maximum / 2, 0], [maximum, maximum / 2, maximum / 4, maximum / 2],
            [maximum / 25, maximum / 13, maximum / 10, maximum], [maximum, maximum, maximum, maximum]];
        byte[] packed = MediaSdrFixture.Plane(16, 1, sampleBytes, 4,
            (component, _) => pixels[component / 4][component % 4]);
        var source = MediaSdrFixture.Source(sixteenBit ? MediaNativePixelFormat.Rgba64Le : MediaNativePixelFormat.Rgba32,
            4, 1, transfer: bt709 ? MediaTransfer.Bt709 : MediaTransfer.Srgb, alpha: MediaAlpha.Straight);
        float[] output = new float[16];
        MediaSdrFrameConverter.Convert(source, new(new(packed, 16 * sampleBytes + 4), default, default), output);
        for (int pixel = 0; pixel < 4; pixel++)
        {
            double alpha = pixels[pixel][3] / (double)maximum;
            float[] expected = [
                (float)(MediaSdrFixture.Transfer(pixels[pixel][0] / (double)maximum, !bt709) * alpha),
                (float)(MediaSdrFixture.Transfer(pixels[pixel][1] / (double)maximum, !bt709) * alpha),
                (float)(MediaSdrFixture.Transfer(pixels[pixel][2] / (double)maximum, !bt709) * alpha), (float)alpha];
            MediaSdrFixture.Close(expected, output.AsSpan(pixel * 4, 4));
        }
        Assert.Equal(0, BitConverter.SingleToInt32Bits(output[0]));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(output[1]));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(output[2]));
        Assert.Equal(0, BitConverter.SingleToInt32Bits(output[3]));
    }

    [Theory]
    [InlineData(MediaNativePixelFormat.Yuv420P8, 2, 2, 8)]
    [InlineData(MediaNativePixelFormat.Yuv422P8, 2, 1, 8)]
    [InlineData(MediaNativePixelFormat.Yuv444P8, 1, 1, 8)]
    [InlineData(MediaNativePixelFormat.Yuv420P10Le, 2, 2, 10)]
    [InlineData(MediaNativePixelFormat.Yuv422P10Le, 2, 1, 10)]
    [InlineData(MediaNativePixelFormat.Yuv444P10Le, 1, 1, 10)]
    public void YUV709全部位深子采样range和位置对独立KrKb与样本中心oracle(MediaNativePixelFormat format, int sx, int sy, int bits)
    {
        const int width = 5, height = 3; int bytes = bits == 8 ? 1 : 2, scale = bits == 8 ? 1 : 4;
        int cw = (width + sx - 1) / sx, ch = (height + sy - 1) / sy;
        int Luma(int x, int y) => (32 + x * 31 + y * 17) * scale;
        int Blue(int x, int y) => (32 + x * 31 + y * 17) * scale;
        int Red(int x, int y) => (220 - x * 29 - y * 17) * scale;
        byte[] yPlane = MediaSdrFixture.Plane(width, height, bytes, 2, Luma);
        byte[] cbPlane = MediaSdrFixture.Plane(cw, ch, bytes, 2, Blue);
        byte[] crPlane = MediaSdrFixture.Plane(cw, ch, bytes, 2, Red);
        var frame = new MediaNativeFrameBorrow(new(yPlane, width * bytes + 2), new(cbPlane, cw * bytes + 2), new(crPlane, cw * bytes + 2));
        foreach (bool limited in new[] { false, true })
        foreach (bool left in sx == 1 ? new[] { false } : new[] { false, true })
        {
            var source = MediaSdrFixture.Source(format, width, height, MediaMatrix.Bt709, MediaTransfer.Bt709,
                limited ? MediaRange.Limited : MediaRange.Full, chroma: sx == 1 ? MediaChromaLocation.None : left ? MediaChromaLocation.Left : MediaChromaLocation.Center);
            float[] output = new float[width * height * 4];
            MediaSdrFrameConverter.Convert(source, frame, output);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                double cb = MediaSdrFixture.ChromaOracle(x, y, width, height, sx, sy, left, Blue);
                double cr = MediaSdrFixture.ChromaOracle(x, y, width, height, sx, sy, left, Red);
                MediaSdrFixture.Close(MediaSdrFixture.YuvOracle(Luma(x, y), cb, cr, bits, limited, false), output.AsSpan((y * width + x) * 4, 4));
            }
            Assert.All(output, value => Assert.True(float.IsFinite(value) && value >= 0 && value <= 1));
        }
    }

    [Theory]
    [InlineData(MediaNativePixelFormat.Yuv420P8, 2, 2)]
    [InlineData(MediaNativePixelFormat.Yuv422P8, 2, 1)]
    [InlineData(MediaNativePixelFormat.Yuv444P8, 1, 1)]
    public void JPEG601明确full和sRGB且灰阶及饱和端点符合独立公式(MediaNativePixelFormat format, int sx, int sy)
    {
        int width = 5, height = 3, cw = (width + sx - 1) / sx, ch = (height + sy - 1) / sy;
        foreach (bool gray in new[] { false, true })
        {
            int Y(int x, int y) => x == 0 ? 0 : x == 4 ? 255 : 40 + x * 35 + y * 9;
            int Cb(int x, int y) => gray ? 128 : 20 + x * 35 + y * 23;
            int Cr(int x, int y) => gray ? 128 : 235 - x * 33 - y * 21;
            var source = MediaSdrFixture.Source(format, width, height, MediaMatrix.Bt601Jpeg, MediaTransfer.Srgb, chroma:
                sx == 1 ? MediaChromaLocation.None : MediaChromaLocation.Center);
            var frame = new MediaNativeFrameBorrow(new(MediaSdrFixture.Plane(width, height, 1, 0, Y), width),
                new(MediaSdrFixture.Plane(cw, ch, 1, 0, Cb), cw), new(MediaSdrFixture.Plane(cw, ch, 1, 0, Cr), cw));
            float[] output = new float[width * height * 4]; MediaSdrFrameConverter.Convert(source, frame, output);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                double cb = MediaSdrFixture.ChromaOracle(x, y, width, height, sx, sy, false, Cb);
                double cr = MediaSdrFixture.ChromaOracle(x, y, width, height, sx, sy, false, Cr);
                MediaSdrFixture.Close(MediaSdrFixture.YuvOracle(Y(x, y), cb, cr, 8, false, true), output.AsSpan((y * width + x) * 4, 4));
                if (gray) Assert.Equal(output[(y * width + x) * 4], output[(y * width + x) * 4 + 1]);
            }
        }
    }

    [Theory]
    [InlineData(0, false, new[] { 0, 1, 2, 3, 4, 5 })]
    [InlineData(1, false, new[] { 3, 0, 4, 1, 5, 2 })]
    [InlineData(2, false, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(3, false, new[] { 2, 5, 1, 4, 0, 3 })]
    [InlineData(0, true, new[] { 2, 1, 0, 5, 4, 3 })]
    [InlineData(1, true, new[] { 5, 2, 4, 1, 3, 0 })]
    [InlineData(2, true, new[] { 3, 4, 5, 0, 1, 2 })]
    [InlineData(3, true, new[] { 0, 3, 1, 4, 2, 5 })]
    public void 颜色转换直接按独立像素表写旋转canonical不二次重采样(int turn, bool mirror, int[] order)
    {
        byte[] packed = MediaSdrFixture.Plane(9, 2, 1, 1, (component, y) => component % 3 == 0 ? 20 + (y * 3 + component / 3) * 30 : 0);
        var source = MediaSdrFixture.Source(MediaNativePixelFormat.Rgb24, 3, 2, turn: (MediaQuarterTurn)turn, mirror: mirror);
        float[] output = new float[24]; MediaSdrFrameConverter.Convert(source, new(new(packed, 10), default, default), output);
        for (int i = 0; i < order.Length; i++)
            MediaSdrFixture.Close([(float)MediaSdrFixture.Transfer((20 + order[i] * 30) / 255d, true), 0, 0, 1], output.AsSpan(i * 4, 4));
    }

    [Fact]
    public void 容量无效plane十位高位额外plane及预取消在发布前拒绝()
    {
        var source = MediaSdrFixture.Source(MediaNativePixelFormat.Rgb24, 1, 1);
        var frame = new MediaNativeFrameBorrow(new(new byte[] { 255, 0, 0 }, 3), default, default);
        float[] output = Enumerable.Repeat(-1f, 4).ToArray();
        Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(source, frame, new float[5]));
        Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(source, new(new(new byte[2], 3), default, default), output));
        Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(source, new(new(new byte[3], -3), default, default), output));
        Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(source, new(frame.First, new(new byte[1], 1), default), output));
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(source, frame, output,
            MediaAbCapacityV1.MaximumDecodeWorkingBytes - 18)).Failure);
        MediaSdrFrameConverter.Convert(source, frame, output, MediaAbCapacityV1.MaximumDecodeWorkingBytes - 19);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, output);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Array.Fill(output, -1);
        Assert.ThrowsAny<OperationCanceledException>(() => MediaSdrFrameConverter.Convert(source, frame, output, cancellationToken: cancellation.Token));
        Assert.All(output, value => Assert.Equal(-1, value));
        var tenBit = MediaSdrFixture.Source(MediaNativePixelFormat.Yuv444P10Le, 1, 1,
            MediaMatrix.Bt709, MediaTransfer.Bt709);
        var invalid = new MediaNativeFrameBorrow(new(new byte[] { 0, 4 }, 2), new(new byte[] { 0, 2 }, 2), new(new byte[] { 0, 2 }, 2));
        Assert.Equal(MediaAbFailure.ColorUnsupported, Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(tenBit, invalid, output)).Failure);
        Assert.All(output, value => Assert.Equal(-1, value));
        Assert.Equal(MediaAbFailure.CapacityExceeded, Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.GetRequiredCanonicalFloatCount(8192, 8192)).Failure);
    }

    [Fact]
    public void 未知解释摘要篡改与重叠内存不被clamp修复()
    {
        var source = MediaSdrFixture.Source(MediaNativePixelFormat.Rgb24, 1, 1);
        var color = source.Presentation.Color;
        var forged = new MediaColorInterpretation((MediaTransfer)99, color.Matrix, color.Range, color.Alpha, color.ChromaLocation,
            color.NativePixelFormat, false, color.AuthorityDigest, color.Digest);
        var descriptor = source.Presentation;
        var badDescriptor = new MediaFilePresentationDescriptor(descriptor.StreamIndex, 1, 1, 1, 1, null, 1, 1, 0, false, forged, descriptor.Digest);
        var badSource = new MediaFeatureFileSource(source.Key, source.Kind, source.Content, badDescriptor, source.Tool, source.Profile);
        float[] output = Enumerable.Repeat(-1f, 4).ToArray();
        Assert.Equal(MediaAbFailure.ColorUnsupported, Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(badSource,
            new(new(new byte[3], 3), default, default), output)).Failure);
        using var owner = new FloatBacking(4);
        MemoryMarshal.AsBytes(owner.Values.AsSpan()).Fill(255);
        Assert.Throws<MediaAbException>(() => MediaSdrFrameConverter.Convert(source,
            new(new(owner.Memory.Slice(0, 3), 3), default, default), owner.Values));
    }

    private sealed class FloatBacking(int count) : System.Buffers.MemoryManager<byte>
    {
        internal float[] Values { get; } = new float[count];
        public override Span<byte> GetSpan() => MemoryMarshal.AsBytes(Values.AsSpan());
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
