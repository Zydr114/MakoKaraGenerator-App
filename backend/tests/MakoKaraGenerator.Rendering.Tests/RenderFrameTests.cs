using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>
/// 渲染输出（裸像素帧）的访问与生命周期测试。
/// </summary>
/// <remarks>
/// <see cref="RenderFrame" /> 是预览、导出、金样测试的共同出口，
/// 因此它的像素约定（BGRA 预乘、直通换算、行距）必须被固化下来。
/// </remarks>
public sealed class RenderFrameTests
{
    private static readonly RenderTarget Target = new() { PixelWidth = 3, PixelHeight = 2 };

    [Fact]
    public void 尺寸与行距按目标推导()
    {
        using RenderFrame frame = new(new byte[3 * 2 * 4], Target, PixelFormat.Bgra8888Premultiplied, timeUs: 1234);

        Assert.Equal(3, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(12, frame.Stride);
        Assert.Equal(24, frame.Pixels.Length);
        Assert.Equal(1234, frame.TimeUs);
        Assert.Equal(PixelFormat.Bgra8888Premultiplied, frame.Format);
    }

    [Fact]
    public void 按行读取不会串行()
    {
        byte[] buffer = new byte[3 * 2 * 4];

        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)i;
        }

        using RenderFrame frame = new(buffer, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        Assert.Equal(buffer.AsSpan(0, 12).ToArray(), frame.GetRow(0).ToArray());
        Assert.Equal(buffer.AsSpan(12, 12).ToArray(), frame.GetRow(1).ToArray());
    }

    [Fact]
    public void 取像素会还原成直通颜色()
    {
        byte[] buffer = new byte[3 * 2 * 4];

        // 第二个像素：A=128，预乘后的 B/G/R 都是 64，
        // 直通值应为 64 * 255 / 128 ≈ 128。
        int offset = 4;
        buffer[offset + 0] = 64;  // B
        buffer[offset + 1] = 64;  // G
        buffer[offset + 2] = 64;  // R
        buffer[offset + 3] = 128; // A

        using RenderFrame frame = new(buffer, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        Assert.True(frame.TryGetPixel(1, 0, out RgbaColor color));
        Assert.Equal(128, color.Red);
        Assert.Equal(128, color.Green);
        Assert.Equal(128, color.Blue);
        Assert.Equal(128, color.Alpha);
    }

    [Fact]
    public void 不透明像素按原值返回()
    {
        byte[] buffer = new byte[3 * 2 * 4];
        buffer[0] = 0x20; // B
        buffer[1] = 0x40; // G
        buffer[2] = 0x60; // R
        buffer[3] = 0xFF; // A

        using RenderFrame frame = new(buffer, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        Assert.True(frame.TryGetPixel(0, 0, out RgbaColor color));
        Assert.Equal(0x60, color.Red);
        Assert.Equal(0x40, color.Green);
        Assert.Equal(0x20, color.Blue);
        Assert.Equal(0xFF, color.Alpha);
    }

    [Fact]
    public void 透明像素返回全透明()
    {
        // 预乘格式下 A=0 的像素颜色信息已经丢失，不能把残留字节当直通值返回。
        byte[] buffer = new byte[3 * 2 * 4];
        buffer[0] = 0xFF;

        using RenderFrame frame = new(buffer, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        Assert.True(frame.TryGetPixel(0, 0, out RgbaColor color));
        Assert.Equal(RgbaColor.Transparent, color);
    }

    [Fact]
    public void 越界取像素返回失败而不是抛异常()
    {
        using RenderFrame frame = new(new byte[3 * 2 * 4], Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        Assert.False(frame.TryGetPixel(-1, 0, out _));
        Assert.False(frame.TryGetPixel(0, -1, out _));
        Assert.False(frame.TryGetPixel(3, 0, out _));
        Assert.False(frame.TryGetPixel(0, 2, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetRow(2));
    }

    [Fact]
    public void 默认参数复制整帧()
    {
        // 回归：height 的默认值 -1 是"整帧"的哨兵，曾因"先校验后解释"必抛异常。
        // 这条路径只有界面在用（锁定帧缓冲），测试以前全走 Pixels，所以漏了。
        byte[] source = [.. Enumerable.Range(0, 3 * 2 * 4).Select(static value => (byte)value)];
        using RenderFrame frame = new(source, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        byte[] destination = CopyOut(frame, destinationStride: 3 * 4);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void 目标行距更大时只写前几列()
    {
        byte[] source = [.. Enumerable.Range(0, 3 * 2 * 4).Select(static value => (byte)value)];
        using RenderFrame frame = new(source, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        // 目标缓冲区先铺满 0xEE，用来验证"多出来的字节保持原样、不清零"。
        byte[] destination = CopyOut(frame, destinationStride: 16, fill: 0xEE);

        for (int y = 0; y < 2; y++)
        {
            Assert.Equal(source.AsSpan(y * 12, 12).ToArray(), destination.AsSpan(y * 16, 12).ToArray());
            Assert.Equal([0xEE, 0xEE, 0xEE, 0xEE], destination.AsSpan((y * 16) + 12, 4).ToArray());
        }
    }

    [Fact]
    public void 指定行数时只复制这些行()
    {
        byte[] source = [.. Enumerable.Range(0, 3 * 2 * 4).Select(static value => (byte)value)];
        using RenderFrame frame = new(source, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        byte[] destination = CopyOut(frame, destinationStride: 3 * 4, height: 1);

        Assert.Equal(source.AsSpan(0, 12).ToArray(), destination.AsSpan(0, 12).ToArray());
        Assert.All(destination.AsSpan(12).ToArray(), static value => Assert.Equal(0, value));
    }

    [Fact]
    public void 行数超过帧高时按帧高截断()
    {
        byte[] source = [.. Enumerable.Range(0, 3 * 2 * 4).Select(static value => (byte)value)];
        using RenderFrame frame = new(source, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        byte[] destination = CopyOut(frame, destinationStride: 3 * 4, height: 99);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void 目标行距非正时抛出()
    {
        using RenderFrame frame = new(new byte[3 * 2 * 4], Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        nint destination = Marshal.AllocHGlobal(64);

        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => frame.CopyPixelsTo(destination, destinationStride: 0));
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    /// <summary>按"客户端已有一块非托管内存"的用法复制一帧，再收回托管数组比较。</summary>
    private static byte[] CopyOut(RenderFrame frame, int destinationStride, int height = -1, byte fill = 0)
    {
        int bytes = destinationStride * Target.PixelHeight;
        nint destination = Marshal.AllocHGlobal(bytes);

        try
        {
            Marshal.Copy(new byte[bytes].Select(_ => fill).ToArray(), 0, destination, bytes);
            frame.CopyPixelsTo(destination, destinationStride, height);

            byte[] result = new byte[bytes];
            Marshal.Copy(destination, result, 0, bytes);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
        }
    }

    [Fact]
    public void 缓冲区不足时拒绝构造()
    {
        Assert.Throws<ArgumentException>(
            () => new RenderFrame(new byte[23], Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0));
        Assert.Throws<ArgumentNullException>(
            () => new RenderFrame(null!, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0));
    }

    [Fact]
    public void 释放后不能再读像素()
    {
        RenderFrame frame = new(new byte[3 * 2 * 4], Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0);

        frame.Dispose();

        Assert.True(frame.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Pixels);

        // 重复释放无害。
        frame.Dispose();
    }

    [Fact]
    public void 池化缓冲区在释放后由缓冲池回收()
    {
        System.Buffers.ArrayPool<byte> pool = System.Buffers.ArrayPool<byte>.Shared;
        byte[] buffer = pool.Rent(Target.PixelWidth * Target.PixelHeight * 4);
        RenderFrame frame = new(buffer, Target, PixelFormat.Bgra8888Premultiplied, timeUs: 0, pooledBuffer: true);

        Assert.True(frame.IsPooled);

        // 归还同一个缓冲池后，再租一块不应当比原来更小（说明归还路径没有把数组丢掉）。
        frame.Dispose();
        byte[] second = pool.Rent(Target.PixelWidth * Target.PixelHeight * 4);

        Assert.True(second.Length >= Target.PixelWidth * Target.PixelHeight * 4);
        pool.Return(second);
    }
}
