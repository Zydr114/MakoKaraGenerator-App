using System.Buffers;
using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>
/// 渲染输出：一块只读的像素数据，以及它的尺寸与对应时刻。
/// </summary>
/// <remarks>
/// <para>
/// 本类型刻意<b>不含任何图形库类型</b>（没有 Skia、没有 Avalonia 的 <c>Bitmap</c>）：
/// 上游只按 <see cref="Width" /> / <see cref="Height" /> / <see cref="Stride" /> 与
/// <see cref="Pixels" /> 处理字节，于是预览、离屏导出、金样测试可以共用同一份输出。
/// </para>
/// <para>
/// 缓冲区可以来自共享缓冲池（<see cref="IsPooled" />）。此时 <see cref="Dispose" />
/// 会把它归还，之后本实例不可再使用；越过池的边界读像素（例如保存起来逐帧比对）
/// 属于调用方责任——需要留存的场合请先复制出来。
/// </para>
/// </remarks>
public sealed class RenderFrame : IDisposable
{
    private readonly bool _pooled;
    private byte[]? _buffer;

    /// <summary>用一块已有缓冲区构造一帧。</summary>
    /// <param name="buffer">像素缓冲区，长度必须至少为 <c>width * height * 4</c>。</param>
    /// <param name="target">本帧的目标尺寸。</param>
    /// <param name="format">像素格式。</param>
    /// <param name="timeUs">本帧对应的时刻（微秒）。</param>
    /// <param name="pooledBuffer">缓冲区是否来自共享缓冲池（决定 <see cref="Dispose" /> 的行为）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> 或 <paramref name="target"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException">缓冲区长度不足时抛出。</exception>
    /// <remarks>
    /// 预留这种构造方式是给渲染后端与测试用的：后端已经从池里租到缓冲区并直接画了进去，
    /// 再复制一次纯属浪费；测试则可以拿一个普通数组来断言像素。
    /// </remarks>
    public RenderFrame(
        byte[] buffer,
        RenderTarget target,
        PixelFormat format,
        long timeUs,
        bool pooledBuffer = false)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(target);

        int stride = checked(target.PixelWidth * 4);
        int required = checked(stride * target.PixelHeight);

        if (buffer.Length < required)
        {
            throw new ArgumentException(
                $"缓冲区至少需要 {required} 字节，实际 {buffer.Length} 字节。",
                nameof(buffer));
        }

        _buffer = buffer;
        _pooled = pooledBuffer;
        Target = target;
        Format = format;
        TimeUs = timeUs;
        Stride = stride;
    }

    /// <summary>本帧的目标尺寸。</summary>
    public RenderTarget Target { get; }

    /// <summary>本帧对应的时刻（微秒）。</summary>
    public long TimeUs { get; }

    /// <summary>生产图实际透明旁路提示；不包含 native 对象，不以成功提示替代错误。</summary>
    public ImmutableArray<RenderImageEffectDiagnostic> ImageEffectDiagnostics { get; init; } = [];
    /// <summary>透明输出跳过材质的真实提示；缓存和导出保留。</summary>
    public ImmutableArray<RenderMaterialDiagnostic> MaterialDiagnostics { get; init; } = [];
    /// <summary>准备帧作者诊断及真实绘制追加的提示；缓存和导出保留，不包含原生资源。</summary>
    public ImmutableArray<Scripting.RuntimeDiagnostic> SceneDiagnostics
    { get => DiagnosticSnapshot.ToImmutableArray(); init => DiagnosticSnapshot = RuntimeDiagnosticSnapshot.FromArray(value); }
    private RuntimeDiagnosticSnapshot _diagnosticSnapshot = RuntimeDiagnosticSnapshot.Empty;
    /// <summary>准备及绘制报告的稳定发生快照；释放像素后仍可读取。</summary>
    public RuntimeDiagnosticSnapshot DiagnosticSnapshot
    { get => _diagnosticSnapshot; init => _diagnosticSnapshot = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>像素格式。</summary>
    public PixelFormat Format { get; }

    /// <summary>宽度（像素）。</summary>
    public int Width => Target.PixelWidth;

    /// <summary>高度（像素）。</summary>
    public int Height => Target.PixelHeight;

    /// <summary>每行字节数。</summary>
    public int Stride { get; }

    /// <summary>缓冲区是否来自共享缓冲池。</summary>
    public bool IsPooled => _pooled;

    /// <summary>本帧是否已被释放。</summary>
    public bool IsDisposed => _buffer is null;

    /// <summary>全部像素数据（长度等于 <see cref="Stride" /> 乘 <see cref="Height" />）。</summary>
    /// <exception cref="ObjectDisposedException">已释放时抛出。</exception>
    /// <summary>
    /// 与 <see cref="Pixels"/> 相同内容的 <see cref="ReadOnlyMemory{T}"/> 视图。
    /// </summary>
    /// <remarks>
    /// 导出时要把整帧写进编码进程的标准输入，而 <c>Stream.WriteAsync</c> 只接受
    /// <see cref="ReadOnlyMemory{T}"/>：走这个属性可以零拷贝地写，不必先 <c>ToArray()</c>
    /// 复制一份（1080p 一帧就是 8 MB，每帧复制一次是不可接受的）。
    /// </remarks>
    public ReadOnlyMemory<byte> PixelMemory =>
        _buffer is null
            ? throw new ObjectDisposedException(nameof(RenderFrame))
            : _buffer.AsMemory(0, Stride * Height);

    /// <summary>帧内像素的只读视图，长度等于 <c>Stride × Height</c>。</summary>
    public ReadOnlySpan<byte> Pixels =>
        _buffer is null
            ? throw new ObjectDisposedException(nameof(RenderFrame))
            : _buffer.AsSpan(0, Stride * Height);

    /// <summary>取得某一行的像素。</summary>
    /// <param name="y">行号，从 0 开始。</param>
    /// <returns>该行的像素数据。</returns>
    /// <exception cref="ArgumentOutOfRangeException">行号越界时抛出。</exception>
    /// <exception cref="ObjectDisposedException">已释放时抛出。</exception>
    public ReadOnlySpan<byte> GetRow(int y)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        return Pixels.Slice(y * Stride, Stride);
    }

    /// <summary>
    /// 读取一个像素的颜色。
    /// </summary>
    /// <param name="x">横坐标，从 0 开始。</param>
    /// <param name="y">纵坐标，从 0 开始。</param>
    /// <param name="color">读到的<b>直通</b>（非预乘）颜色。</param>
    /// <returns>坐标在范围内时为 <see langword="true"/>。</returns>
    /// <exception cref="ObjectDisposedException">已释放时抛出。</exception>
    /// <remarks>
    /// 主要给金样测试与自检用。预乘格式下 alpha 为 0 的像素颜色信息已经丢失，
    /// 此时返回全透明，而不是把预乘值当直通值返回。
    /// </remarks>
    public bool TryGetPixel(int x, int y, out RgbaColor color)
    {
        color = RgbaColor.Transparent;

        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return false;
        }

        ReadOnlySpan<byte> pixel = GetRow(y).Slice(x * 4, 4);
        byte alpha = pixel[3];

        color = alpha == 0
            ? RgbaColor.Transparent
            : new RgbaColor(
                Unpremultiply(pixel[2], alpha),
                Unpremultiply(pixel[1], alpha),
                Unpremultiply(pixel[0], alpha),
                alpha);

        return true;
    }

    /// <summary>
    /// 把整帧像素复制到一个非托管缓冲区。
    /// </summary>
    /// <param name="destination">目标缓冲区首地址。</param>
    /// <param name="destinationStride">目标每行字节数；小于 <see cref="Stride" /> 时会逐行紧凑复制。</param>
    /// <param name="height">要复制的行数；小于 0 表示整帧高度。</param>
    /// <exception cref="ObjectDisposedException">已释放时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="destinationStride"/> 非正时抛出。</exception>
    /// <remarks>
    /// <paramref name="height"/> 的小于 0 是<b>有意</b>作为"整帧"的哨兵值，
    /// 因此不能对它做"非负"校验——曾经这里先校验后解释，默认参数一进来就抛
    /// <see cref="ArgumentOutOfRangeException"/>，界面表现为"预览只有一条红条"。
    /// 超过帧高的行数按帧高截断，不报错。
    /// </remarks>
    /// <remarks>
    /// 这是给"客户端已经持有一块画布内存"的场合用的：界面的锁定帧缓冲、
    /// 编码器进程的标准输入缓冲都属于此类，两边都不需要为了复制而改造成托管数组。
    /// 目标行距大于本帧行距时，多出来的字节保持原样，不做清零。
    /// </remarks>
    public void CopyPixelsTo(IntPtr destination, int destinationStride, int height = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationStride);

        if (destination == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        byte[] buffer = _buffer ?? throw new ObjectDisposedException(nameof(RenderFrame));

        int rows = height < 0 ? Height : Math.Min(height, Height);
        int copyBytes = Math.Min(Stride, destinationStride);

        for (int y = 0; y < rows; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                buffer,
                y * Stride,
                destination + (y * destinationStride),
                copyBytes);
        }
    }

    /// <summary>释放本帧；缓冲区来自缓冲池时归还它。</summary>
    public void Dispose()
    {
        byte[]? buffer = _buffer;

        if (buffer is null)
        {
            return;
        }

        _buffer = null;

        if (_pooled)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static byte Unpremultiply(byte value, byte alpha) =>
        alpha == 255 ? value : (byte)Math.Min((value * 255 + (alpha / 2)) / alpha, 255);
}
