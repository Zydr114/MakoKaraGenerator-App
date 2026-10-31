namespace MakoKaraGenerator.Rendering.Abstractions;

/// <summary>
/// 一帧视频画面：BGRA8888、<b>直乘</b> alpha，行间可能有填充。
/// </summary>
/// <remarks>
/// <para>
/// 故意不带任何绘图库类型：抽象层不知道 Skia，将来换渲染后端也不用改这里。
/// </para>
/// <para>
/// <b>缓冲由帧来源持有并复用</b>——每帧都新建几十 MB 的数组在 30 fps 下毫无意义。
/// 因此约定：<see cref="Pixels"/> 的内容在下一次取帧之后失效，需要留存请自行拷贝。
/// </para>
/// </remarks>
public sealed class VideoFrame
{
    /// <summary>构造一帧。</summary>
    /// <param name="width">宽度（像素）。</param>
    /// <param name="height">高度（像素）。</param>
    /// <param name="stride">每行字节数。</param>
    /// <param name="pixels">像素缓冲；长度不得小于 <paramref name="stride"/> × <paramref name="height"/>。</param>
    /// <exception cref="ArgumentOutOfRangeException">尺寸或步长非正时抛出。</exception>
    /// <exception cref="ArgumentException">缓冲太小或为 <see langword="null"/> 时抛出。</exception>
    public VideoFrame(int width, int height, int stride, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stride);
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length < (long)stride * height)
        {
            throw new ArgumentException("像素缓冲小于 步长 × 高度。", nameof(pixels));
        }

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
    }

    /// <summary>画面宽度（像素）。</summary>
    public int Width { get; }

    /// <summary>画面高度（像素）。</summary>
    public int Height { get; }

    /// <summary>每行字节数。</summary>
    public int Stride { get; }

    /// <summary>像素缓冲（BGRA8888）。</summary>
    public byte[] Pixels { get; }
}

/// <summary>
/// 一段视频的帧来源：回答"这一时刻该显示哪一帧"。
/// </summary>
/// <remarks>
/// 这是 ADR-0036 第 5 节给视频背景预留的接入点：渲染器每帧只问一次，
/// 解码、定位与策略都不进渲染代码。
/// </remarks>
public interface IVideoFrameSource : IDisposable
{
    /// <summary>视频时长（微秒）；探测不到时为 0。</summary>
    long DurationUs { get; }

    /// <summary>帧率（每秒帧数）；探测不到时为 0。</summary>
    double FrameRate { get; }

    /// <summary>画面宽度（像素）。</summary>
    int Width { get; }

    /// <summary>画面高度（像素）。</summary>
    int Height { get; }

    /// <summary>最近一次失败的说明；没有失败时为 <see langword="null"/>。</summary>
    string? LastError { get; }

    /// <summary>取某一源时刻的一帧。</summary>
    /// <param name="sourceTimeUs">源时刻（微秒），由 <c>BackgroundPlayback</c> 映射得到。</param>
    /// <param name="frame">取到的帧；<b>内容在下一次取帧后失效</b>。</param>
    /// <returns>取到帧时为 <see langword="true"/>；失败时为 <see langword="false"/> 并在 <see cref="LastError"/> 里说明。</returns>
    bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame);

    /// <summary>取帧时传递取消；取消抛出，不作为缺失帧返回。</summary>
    /// <remarks>旧实现的默认桥只检查调用前后；有阻塞IO的宿主必须覆盖此入口。</remarks>
    bool TryGetFrame(long sourceTimeUs, out VideoFrame? frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool result = TryGetFrame(sourceTimeUs, out frame);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

/// <summary>
/// 打开视频帧来源的能力（由组合根注入渲染管线）。
/// </summary>
/// <remarks>
/// 实现放在 <c>Media</c>（FFmpeg 进程在那里），抽象放在这里：
/// 渲染层因此不需要引用媒体工具，也就不需要知道命令行参数（ADR-0025 的依赖方向）。
/// </remarks>
public interface IVideoFrameProvider : IDisposable
{
    /// <summary>打开一段视频。</summary>
    /// <param name="path">视频文件路径。</param>
    /// <param name="failureReason">打不开时的说明。</param>
    /// <returns>帧来源；打不开时为 <see langword="null"/>。</returns>
    IVideoFrameSource? Open(string path, out string? failureReason);

    /// <summary>打开时传递取消；取消抛出，不返回缺失背景。</summary>
    /// <remarks>旧实现的默认桥只检查调用前后；有阻塞IO的宿主必须覆盖此入口。</remarks>
    IVideoFrameSource? Open(string path, out string? failureReason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IVideoFrameSource? source = Open(path, out failureReason);
        if (cancellationToken.IsCancellationRequested)
        {
            source?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return source;
    }
}
