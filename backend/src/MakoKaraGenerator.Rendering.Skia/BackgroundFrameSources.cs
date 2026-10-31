using MakoKaraGenerator.Core.Projects;
using SkiaSharp;
using System.Runtime.InteropServices;
using MakoKaraGenerator.Core.Media;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>
/// 背景层在某一时刻的画面来源。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是视频背景的预留接口</b>（docs/decisions.md ADR-0036）。渲染管线每帧只问一次
/// "这个时刻该画什么"：图片背景永远返回同一张，而视频背景将来在这里做 seek 与解码，
/// 于是<b>绘制代码不需要任何改动</b>，预览与导出也自动一起获得视频背景
/// （两者共用同一条管线与同一份快照，docs §3.2）。
/// </para>
/// <para>
/// 实现必须能被界面线程（预览）与后台导出线程同时调用：解码后的位图只读，
/// 因此缓存与共享是安全的。
/// </para>
/// </remarks>
public interface IBackgroundFrameSource : IDisposable
{
    /// <summary>取该时刻要画的画面。</summary>
    /// <param name="timeUs">时刻（微秒）。</param>
    /// <returns>位图；没有可画的内容（文件缺失、解码失败、该时刻无帧）时为 <see langword="null"/>。</returns>
    SKBitmap? GetFrame(long timeUs);

    /// <summary>取消抛出，不降级为缺失背景；阻塞IO实现须覆盖此入口。</summary>
    SKBitmap? GetFrame(long timeUs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = GetFrame(timeUs);
        cancellationToken.ThrowIfCancellationRequested();
        return frame;
    }
}

/// <summary>把一个背景描述解析成帧来源；同一个背景只解析一次。</summary>
/// <remarks>
/// ADR-0036 第 5 节预留的接法就是这一步：按 <see cref="BackgroundKind"/> 分派到
/// <see cref="ImageBackgroundFrameSource"/> 或 <see cref="VideoBackgroundFrameSource"/>，
/// 视频解码由注入的 <see cref="IVideoFrameProvider"/> 提供。管线的绘制部分一行没改。
/// </remarks>
public interface IBackgroundFrameSourceResolver : IDisposable
{
    /// <summary>取该背景的帧来源；不需要画面（纯色）或路径为空时为 <see langword="null"/>。</summary>
    /// <param name="background">背景源。</param>
    /// <returns>帧来源，或 <see langword="null"/>。</returns>
    IBackgroundFrameSource? Resolve(BackgroundSource background);

    /// <summary>解析时传递取消；阻塞IO实现须覆盖此入口。</summary>
    IBackgroundFrameSource? Resolve(BackgroundSource background, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(background);
        cancellationToken.ThrowIfCancellationRequested();
        return source;
    }
}

/// <summary>单张图片：任何时刻都返回同一张解码后的位图。</summary>
public sealed class ImageBackgroundFrameSource : IBackgroundFrameSource, IDisposable
{
    private readonly string _path;
    private readonly object _gate = new();
    private SKBitmap? _bitmap;
    private bool _loaded;
    private bool _disposed;

    /// <summary>创建图片帧来源（此时还不解码）。</summary>
    /// <param name="path">图片路径，不可为空白。</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空白时抛出。</exception>
    public ImageBackgroundFrameSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
    }

    /// <summary>图片路径。</summary>
    public string Path => _path;

    /// <summary>加载失败的原因；成功时为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 读取失败<b>不抛异常</b>：一张图坏掉不该让预览和整段导出都失败，
    /// 界面按"这帧只有背景色"渲染并把这个原因说给用户听。
    /// </remarks>
    public string? LoadError { get; private set; }

    /// <inheritdoc/>
    public SKBitmap? GetFrame(long timeUs)
    {
        lock (_gate)
        {
            if (_disposed || _loaded)
            {
                return _bitmap;
            }

            _loaded = true;

            try
            {
                _bitmap = File.Exists(_path)
                    ? SKBitmap.Decode(_path)
                    : null;

                if (_bitmap is null)
                {
                    LoadError = File.Exists(_path) ? "图片无法解码。" : "图片文件不存在。";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LoadError = exception.Message;
                _bitmap = null;
            }

            return _bitmap;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bitmap?.Dispose();
            _bitmap = null;
            _loaded = true;
        }
    }
}

/// <summary>打不开的视频：任何时刻都没有画面，只留下原因。</summary>
/// <remarks>
/// 用一个"总是没有帧"的来源占位，是为了让解析器仍有<b>记忆</b>：
/// 打不开的视频不会每次渲染都重新去启动一次探测进程。
/// </remarks>
internal sealed class MissingVideoBackgroundFrameSource : IBackgroundFrameSource
{
    public MissingVideoBackgroundFrameSource(string? failureReason)
    {
        FailureReason = failureReason;
    }

    /// <summary>打不开的原因。</summary>
    public string? FailureReason { get; }

    /// <inheritdoc/>
    public SKBitmap? GetFrame(long timeUs) => null;

    /// <inheritdoc/>
    public void Dispose()
    {
        // 没有需要释放的资源。
    }
}

/// <summary>
/// 把 <see cref="IVideoFrameSource"/> 的裸 BGRA 帧搬进 Skia 位图。
/// </summary>
/// <remarks>
/// <para>
/// 时间策略在 <see cref="BackgroundPlayback"/> 里（纯函数、可单测）：这里只做搬运。
/// 位图与目标缓冲都复用，避免 30 fps 下每帧新建几 MB 的对象。
/// </para>
/// <para>
/// 像素格式声明为 <see cref="SKAlphaType.Unpremul"/>：FFmpeg 的 <c>bgra</c> 是<b>直乘</b> alpha
/// （与 ADR-0037 里"渲染帧是预乘、写进 FFmpeg 前要换算"是同一个坑的另一个方向）。
/// 声明错了会让半透明像素被二次压暗。
/// </para>
/// <para>
/// 解帧是有状态的（一个常驻解码进程 + 顺序读），因此这里加锁串行化：
/// 预览与导出若共用同一个来源，正确性优先于吞吐。
/// </para>
/// </remarks>
public sealed class VideoBackgroundFrameSource : IBackgroundFrameSource, IDisposable
{
    private readonly IVideoFrameSource _source;
    private readonly BackgroundPlaybackMode _playback;
    private readonly object _gate = new();
    private SKBitmap? _bitmap;
    private bool _disposed;

    /// <summary>创建视频帧来源。</summary>
    /// <param name="source">底层帧来源。</param>
    /// <param name="playback">短于音频时的时间策略。</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 为 <see langword="null"/> 时抛出。</exception>
    public VideoBackgroundFrameSource(IVideoFrameSource source, BackgroundPlaybackMode playback = BackgroundPlaybackMode.Loop)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _playback = playback;
    }

    /// <summary>视频时长（微秒）。</summary>
    public long DurationUs => _source.DurationUs;

    /// <summary>最近一次失败的说明；没有失败时为 <see langword="null"/>。</summary>
    public string? LastError => _source.LastError;

    /// <inheritdoc/>
    public SKBitmap? GetFrame(long timeUs) => GetFrame(timeUs, CancellationToken.None);

    /// <inheritdoc/>
    public SKBitmap? GetFrame(long timeUs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed)
            {
                return null;
            }

            long sourceTimeUs = BackgroundPlayback.ResolveSourceTimeUs(timeUs, _source.DurationUs, _playback);

            if (!_source.TryGetFrame(sourceTimeUs, out VideoFrame? frame, cancellationToken) || frame is null)
            {
                return null;
            }

            _bitmap ??= new SKBitmap(new SKImageInfo(
                frame.Width,
                frame.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Unpremul));

            cancellationToken.ThrowIfCancellationRequested();
            Copy(frame, _bitmap);

            return _bitmap;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bitmap?.Dispose();
            _bitmap = null;
            _source.Dispose();
        }
    }

    /// <summary>逐行搬运：Skia 的行距可能比源行距大（对齐填充），因此不能整块拷。</summary>
    private static void Copy(VideoFrame frame, SKBitmap bitmap)
    {
        int width = Math.Min(frame.Width, bitmap.Width);
        int height = Math.Min(frame.Height, bitmap.Height);
        int rowBytes = width * 4;
        int targetStride = bitmap.RowBytes;

        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(
                frame.Pixels,
                (y * frame.Stride),
                bitmap.GetPixels() + (y * targetStride),
                Math.Min(rowBytes, targetStride));
        }
    }
}
