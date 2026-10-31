namespace MakoKaraGenerator.Media;

/// <summary>
/// 探测得到的音频流信息。
/// </summary>
/// <param name="DurationUs">时长（微秒）。</param>
/// <param name="SampleRate">采样率（Hz）。</param>
/// <param name="Channels">声道数。</param>
/// <param name="CodecName">编码器名称，例如 <c>pcm_s16le</c>、<c>mp3</c>。</param>
/// <param name="ContainerName">容器名称，例如 <c>wav</c>、<c>mov,mp4,m4a</c>。</param>
public sealed record AudioStreamInfo(
    long DurationUs,
    int SampleRate,
    int Channels,
    string CodecName,
    string ContainerName)
{
    /// <summary>每秒采样数换算出的总采样数（按时长估算，可能与实际略有出入）。</summary>
    public long EstimatedSampleCount => (long)Math.Round((double)DurationUs / 1_000_000d * SampleRate);
}

/// <summary>
/// 媒体探测抽象。
/// </summary>
/// <remarks>
/// docs/roadmap.md §10 的 <c>EXPORT-01</c> 要求"UI 与 FFmpeg 解耦"，
/// docs/roadmap.md §8 的 <c>MEDIA-01</c> 要求"FFprobe 媒体探测适配"。
/// 把探测放在接口后面，是为了让 ViewModel 与控件不出现 <c>ffprobe</c> 这类字符串。
/// </remarks>
public interface IMediaProbe
{
    /// <summary>探测首个音频流的信息。</summary>
    /// <param name="filePath">媒体文件路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>音频流信息；文件中没有音频流时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="IOException">探测进程失败或输出无法解析时抛出。</exception>
    Task<AudioStreamInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>探测整个媒体文件：容器、时长与全部流。</summary>
    /// <param name="filePath">媒体文件路径。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>文件信息；解析不出任何流时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="IOException">探测进程失败或输出无法解析时抛出。</exception>
    /// <remarks>
    /// 导入时只关心音频（用 <see cref="ProbeAsync"/>），导出后要确认"产物到底有没有画面、
    /// 是哪个编码器"，那需要看到全部流（docs/roadmap.md §10 的 <c>EXPORT-06</c>、<c>EXPORT-09</c>）。
    /// </remarks>
    Task<MediaFileInfo?> ProbeFileAsync(string filePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// 把媒体文件解码为单声道 PCM 采样的抽象。
/// </summary>
/// <remarks>
/// docs/roadmap.md §8 的 <c>MEDIA-02</c> 要求"FFmpeg 音频转 PCM，生成稳定的采样流"。
/// 采样值归一化到 <c>[-1, 1]</c>。
/// </remarks>
public interface IPcmSampleReader
{
    /// <summary>
    /// 流式读取单声道采样，边读边交给回调，不把整首歌保留在内存里。
    /// </summary>
    /// <param name="filePath">媒体文件路径。</param>
    /// <param name="onSamples">每块采样的回调；块内采样为归一化到 <c>[-1, 1]</c> 的单声道值。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>实际读出的采样总数。</returns>
    /// <exception cref="ArgumentNullException">参数为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="IOException">解码进程失败时抛出。</exception>
    Task<long> ReadMonoSamplesAsync(
        string filePath,
        Func<ReadOnlyMemory<float>, ValueTask> onSamples,
        CancellationToken cancellationToken = default);
}

/// <summary>显式重采样的流式单声道读取；分析固定采样率，不改变现有波形的源采样率。</summary>
public interface IResampledPcmSampleReader : IPcmSampleReader
{
    /// <summary>读取指定采样率的单声道 float PCM；回调缓冲仅在本次回调中有效。</summary>
    /// <param name="filePath">源媒体文件。</param>
    /// <param name="sampleRate">明确输出采样率，必须为正。</param>
    /// <param name="onSamples">顺序处理实际采样的回调。</param>
    /// <param name="cancellationToken">解码和回调的取消标记。</param>
    /// <returns>实际输出的采样数量。</returns>
    Task<long> ReadMonoSamplesAtRateAsync(string filePath, int sampleRate,
        Func<ReadOnlyMemory<float>, ValueTask> onSamples, CancellationToken cancellationToken = default);
}
