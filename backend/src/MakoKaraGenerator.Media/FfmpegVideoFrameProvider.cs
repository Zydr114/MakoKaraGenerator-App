using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media;

/// <summary>
/// 用 FFmpeg 解码背景视频的帧来源（roadmap P1「背景视频完整支持」）。
/// </summary>
/// <remarks>
/// <para>
/// 一次 <see cref="Open(string, out string)"/> 会同步探测一次元数据（宽高、帧率、时长），随后<b>常驻一个解码进程</b>，
/// 顺序向前读帧；只有"往回跳"或"往前跳太远"才重新用输入侧定位重启进程。
/// 请求最近成功的同一源帧直接复用借用缓冲；部分读取、失败及释放使缓存失效。
/// 这样预览与导出（都是时间向前走）只花一次启动成本，而随机跳转会退化成一次 seek。
/// </para>
/// <para>
/// 同步入口桥接无同步上下文的异步管道读取；一次探测与一次取帧各有有限期限，
/// 取消或超时终止自身进程并等待释放，不能在stdout读取阶段永久等待。
/// </para>
/// <para>
/// 已知取舍：帧率按常帧率处理（变帧率视频的帧号会略有偏差），定位精度是"目标时刻或其后最近的一帧"。
/// 两者都只影响一帧以内的画面，对背景视频可接受；真正需要精确逐帧的场合不在这一版范围内。
/// </para>
/// </remarks>
public sealed partial class FfmpegVideoFrameProvider : IVideoFrameProvider
{
    /// <summary>默认的 FFmpeg 可执行文件名。</summary>
    public const string DefaultExecutableName = "ffmpeg";

    /// <summary>默认的 FFprobe 可执行文件名。</summary>
    public const string DefaultProbeExecutableName = "ffprobe";

    /// <summary>进程重启后允许最多向前读多少帧（超了就 seek，避免为了前进两秒而读完两秒）。</summary>
    private const int MaxSequentialSkipFrames = 30;

    /// <summary>探测超时（毫秒）。</summary>
    private const int ProbeTimeoutMs = 10_000;

    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly TimeSpan _operationTimeout;
    private readonly bool _selfContained;

    /// <summary>受管 Worker 固定授权使用原逐帧解码器，同时限定自包含 MOV/MP4。</summary>
    public static FfmpegVideoFrameProvider CreateSelfContained() => new(DefaultExecutableName, DefaultProbeExecutableName,
        TimeSpan.FromMilliseconds(ProbeTimeoutMs), true);

    /// <summary>按实际签名固定受管输入家族；MOV检查外部引用，Matroska不启用通用格式发现。</summary>
    public static string ValidateSelfContainedInput(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[12]; int count = stream.Read(header);
        if (count >= 4 && header[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return "matroska";
        SelfContainedMovInputValidator.ValidateFile(path, required: true, cancellationToken);
        return "mov";
    }

    private static void AddSelfContainedArguments(List<string> args, string? demuxer)
    {
        if (demuxer is null) return;
        args.AddRange(["-f", demuxer, "-protocol_whitelist", "file,pipe"]);
        if (demuxer == "mov") args.AddRange(["-enable_drefs", "0", "-use_absolute_path", "0"]);
    }

    /// <summary>构造提供者。</summary>
    /// <param name="ffmpegPath">FFmpeg 可执行文件路径。</param>
    /// <param name="ffprobePath">FFprobe 可执行文件路径。</param>
    /// <exception cref="ArgumentException">路径为空白时抛出。</exception>
    public FfmpegVideoFrameProvider(
        string ffmpegPath = DefaultExecutableName,
        string ffprobePath = DefaultProbeExecutableName)
        : this(ffmpegPath, ffprobePath, TimeSpan.FromMilliseconds(ProbeTimeoutMs)) { }

    internal FfmpegVideoFrameProvider(string ffmpegPath, string ffprobePath, TimeSpan operationTimeout)
        : this(ffmpegPath, ffprobePath, operationTimeout, false) { }

    private FfmpegVideoFrameProvider(string ffmpegPath, string ffprobePath, TimeSpan operationTimeout, bool selfContained)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);
        _operationTimeout = operationTimeout;
        _selfContained = selfContained;
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);

        _ffmpegPath = ffmpegPath;
        _ffprobePath = ffprobePath;
    }

    /// <inheritdoc/>
    public IVideoFrameSource? Open(string path, out string? failureReason) => Open(path, out failureReason, CancellationToken.None);

    /// <summary>打开视频；调用者取消抛出，不返回缺失背景。</summary>
    public IVideoFrameSource? Open(string path, out string? failureReason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            failureReason = $"找不到背景视频「{Path.GetFileName(path)}」，画面上只显示背景色。";

            return null;
        }

        FfmpegVideoFrameSource? source = null;

        try
        {
            string? demuxer = _selfContained ? ValidateSelfContainedInput(path, cancellationToken) : null;
            VideoMetadata metadata = ProbeAsync(path, demuxer, cancellationToken).GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();

            if (metadata.Width <= 0 || metadata.Height <= 0)
            {
                failureReason = $"「{Path.GetFileName(path)}」里没有可用的视频画面，已按「只有背景色」处理。";

                return null;
            }

            source = new FfmpegVideoFrameSource(_ffmpegPath, path, metadata, _operationTimeout, demuxer);
            cancellationToken.ThrowIfCancellationRequested();
            failureReason = null;

            return source;
        }
        catch (OperationCanceledException) { source?.Dispose(); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            source?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            failureReason = $"无法打开背景视频「{Path.GetFileName(path)}」：{exception.Message}";

            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // 进程归各自的帧来源管（打开一个视频就有一个），这里没有需要释放的资源；
        // 保留这个成员是为了让组合根能用同一套生命周期处理所有提供者。
    }

    /// <summary>探测视频元数据（宽高、帧率、时长）。</summary>
    private async Task<VideoMetadata> ProbeAsync(string path, string? demuxer, CancellationToken caller)
    {
        using var deadline = new MediaOperationDeadline(_operationTimeout, caller);
        var args = new List<string> { "-v", "error" };
        AddSelfContainedArguments(args, demuxer);
        args.AddRange(["-select_streams", "v:0", "-show_entries", "stream=width,height,avg_frame_rate:format=duration", "-of", "json", path]);
        using var process = OwnedMediaProcess.Start(_ffprobePath, args);
        using var cancellation = deadline.Token.Register(process.Terminate);
        string json;
        try
        {
            json = await process.ReadMetadataAsync(deadline.Token).ConfigureAwait(false);
            deadline.ThrowIfExpired();
        }
        catch (OperationCanceledException error) { throw deadline.ClassifyCancellation(error); }
        finally { await process.CloseAsync().ConfigureAwait(false); }
        try
        {
            JsonNode? root = JsonNode.Parse(json);

            if (root?["streams"] is not JsonArray { Count: > 0 } streams)
            {
                return default;
            }

            JsonNode stream = streams[0]!;

            return new VideoMetadata(
                ReadInt(stream, "width"),
                ReadInt(stream, "height"),
                ReadFrameRate(ReadString(stream, "avg_frame_rate")),
                ReadDurationUs(root));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        { throw new IOException("FFprobe 输出不是有效的视频元数据。", error); }
    }

    private static double ReadFrameRate(string? text)
    {
        // ffprobe 写成 "10/1" 这种分数。
        if (text is null)
        {
            return 0d;
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);

        if (slash <= 0)
        {
            return double.TryParse(text, CultureInfo.InvariantCulture, out double single) ? single : 0d;
        }

        return double.TryParse(text[..slash], CultureInfo.InvariantCulture, out double numerator)
            && double.TryParse(text[(slash + 1)..], CultureInfo.InvariantCulture, out double denominator)
            && denominator > 0d
                ? numerator / denominator
                : 0d;
    }

    private static long ReadDurationUs(JsonNode root)
    {
        double seconds = ReadDouble(root["format"]?["duration"]);

        return seconds > 0d ? (long)Math.Round(seconds * 1_000_000d) : 0L;
    }

    private static int ReadInt(JsonNode node, string name)
    {
        double value = ReadDouble(node[name]);

        return value > 0d ? (int)Math.Round(value) : 0;
    }

    /// <summary>读一个数值字段：FFprobe 有时给数字、有时给字符串，两种都要认。</summary>
    /// <remarks>
    /// 宽高在 JSON 里是数字，时长是字符串，帧率是 <c>"10/1"</c> 这样的分数——
    /// 只按字符串读会让宽高静默变成 0，表现为"这个视频里没有画面"（踩过一次）。
    /// </remarks>
    private static double ReadDouble(JsonNode? node)
    {
        if (node is null)
        {
            return 0d;
        }

        if (node.GetValueKind() == JsonValueKind.Number)
        {
            return node.GetValue<double>();
        }

        return node is JsonValue value
            && value.TryGetValue(out string? text)
            && double.TryParse(text, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : 0d;
    }

    private static string? ReadString(JsonNode node, string name) =>
        node[name] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>探测结果。</summary>
    private readonly record struct VideoMetadata(int Width, int Height, double FrameRate, long DurationUs);
}
