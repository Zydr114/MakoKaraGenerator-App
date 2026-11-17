using System.Collections.Immutable;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export;

/// <summary>音频进入 MP4 的方式（复制或重编码）。</summary>
/// <param name="Copy">是否直接复制源音频流。</param>
/// <param name="CodecName">目标音频编码器名（复制时为源编码器名）。</param>
/// <param name="Reason">给人看的理由，会进导出报告。</param>
public sealed record AudioEncodingPlan(bool Copy, string CodecName, string Reason)
{
    /// <summary>源编码器可以放进 MP4 时直接复制。</summary>
    /// <remarks>
    /// 复制是<b>无损且快</b>的，所以优先；但 MP4 只接受少数几种音频编码
    /// （AAC / MP3 / AC-3 / E-AC-3 / ALAC / Opus…），其余（PCM、FLAC、Vorbis）必须重编码，
    /// 否则封装阶段会直接失败。
    /// </remarks>
    private static readonly ImmutableHashSet<string> CopyableCodecs =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "aac",
            "mp3",
            "ac3",
            "eac3",
            "alac",
            "opus");

    /// <summary>重编码时使用的目标音频编码器。</summary>
    public const string FallbackCodecName = "aac";

    /// <summary>按源音频编码器决定进入 MP4 的方式。</summary>
    /// <param name="sourceCodecName">源音频编码器名；未知时传 <see langword="null"/>。</param>
    /// <returns>音频处理方案。</returns>
    /// <remarks>未知编码器按"不能复制"处理：宁可重编码一次，也不要让封装在最后一步失败。</remarks>
    public static AudioEncodingPlan Decide(string? sourceCodecName)
    {
        if (!string.IsNullOrWhiteSpace(sourceCodecName) && CopyableCodecs.Contains(sourceCodecName))
        {
            return new AudioEncodingPlan(
                Copy: true,
                CodecName: sourceCodecName,
                Reason: $"源音频编码 {sourceCodecName} 可直接放进 MP4，无损复制。");
        }

        string source = string.IsNullOrWhiteSpace(sourceCodecName) ? "未知" : sourceCodecName;

        return new AudioEncodingPlan(
            Copy: false,
            CodecName: FallbackCodecName,
            Reason: $"源音频编码 {source} 不能直接放进 MP4，重编码为 {FallbackCodecName}。");
    }
}

/// <summary>一次编码会话的请求。</summary>
/// <remarks>
/// 分辨率与帧率由调用方给定（来自工程合成设置）——编码器不做"取个整"之类的修正，
/// 尺寸不合法的组合在开始前就被拒绝。
/// </remarks>
public sealed record VideoEncodingRequest
{
    /// <summary>输出文件路径。</summary>
    public required string OutputPath { get; init; }

    /// <summary>画面宽度（像素）。</summary>
    public required int Width { get; init; }

    /// <summary>画面高度（像素）。</summary>
    public required int Height { get; init; }

    /// <summary>帧率。</summary>
    public required FrameRate FrameRate { get; init; }

    /// <summary>计划写出的总帧数。</summary>
    public required int TotalFrames { get; init; }

    /// <summary>质量档位。</summary>
    public ExportQualityPreset Quality { get; init; } = ExportQualityPreset.For(ExportQuality.Standard);

    /// <summary>输出格式（决定编码器、容器，以及喂进去的像素是不是直乘 Alpha）。</summary>
    public ExportVideoFormat Format { get; init; } = ExportVideoFormat.OpaqueMp4;

    /// <summary>音频源路径；为 <see langword="null"/> 时只输出画面。</summary>
    public string? AudioPath { get; init; }

    /// <summary>Explicit restricted audio demuxer for managed tasks; null preserves desktop container detection.</summary>
    public string? AudioInputFormat { get; init; }

    /// <summary>将音轨补静音或截断到视频帧计划，要求音频重编码。</summary>
    public bool NormalizeAudioDuration { get; init; }

    /// <summary>Optional FFmpeg output size stop; actual completeness and the OS hard limit remain independently checked.</summary>
    public long? MaximumOutputBytes { get; init; }

    /// <summary>音频进入容器的方式。</summary>
    public AudioEncodingPlan? AudioPlan { get; init; }

    /// <summary>已存在时是否覆盖。</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>编码超时；为 <see langword="null"/> 时不额外限时。</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>一次编码会话的结果。</summary>
/// <param name="ExitCode">进程退出码；0 表示成功。</param>
/// <param name="FramesWritten">实际写入的帧数。</param>
/// <param name="DroppedFrames">编码器报告的丢帧数。</param>
/// <param name="OutputByteSize">产物字节数。</param>
/// <param name="Log">编码器 stderr 的日志行（已截断到合理上限）。</param>
/// <param name="Artifact">完成的暂存产物；调用方接管其发布／清理责任，最终输出尚未改变。</param>
public sealed record VideoEncodingResult(
    int ExitCode,
    int FramesWritten,
    int DroppedFrames,
    long OutputByteSize,
    ImmutableArray<string> Log,
    VideoEncodingArtifact Artifact);

/// <summary>把裸像素帧写成视频文件的编码器抽象（docs/roadmap.md §10 的 <c>EXPORT-01</c>）。</summary>
/// <remarks>
/// <para>
/// 存在的理由与 <see cref="MakoKaraGenerator.Media.IMediaProbe"/> 相同：让界面与 ViewModel
/// 永远不出现 FFmpeg 的命令行或可执行文件名（docs/development.md §12.2）。
/// 所有命令参数都由强类型的 <see cref="VideoEncodingRequest"/> 生成。
/// </para>
/// <para>
/// 调用形态是"会话式"的：先 <see cref="StartAsync"/> 起进程，再逐帧
/// <see cref="IVideoEncodingSession.WriteFrameAsync"/>，最后 <see cref="IVideoEncodingSession.CompleteAsync"/>。
/// 一次性接口无法表达"边渲染边写、失败能取消、临时文件要清理"这三件事。
/// </para>
/// </remarks>
public interface IMediaEncoder
{
    /// <summary>启动一次编码会话。</summary>
    /// <param name="request">编码请求。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>可用于写帧的会话。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="IOException">编码进程无法启动时抛出。</exception>
    Task<IVideoEncodingSession> StartAsync(
        VideoEncodingRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>一次正在进行的编码会话。</summary>
/// <remarks>
/// <b>帧的所有权</b>：交给 <see cref="WriteFrameAsync"/> 的帧由会话负责释放，
/// 调用方不应再使用或释放它——否则每一次写帧都要在调用方与编码器之间传一遍所有权，
/// 迟早会漏。
/// </remarks>
public interface IVideoEncodingSession : IAsyncDisposable
{
    /// <summary>已写入的帧数。</summary>
    int FramesWritten { get; }

    /// <summary>把一帧裸像素写给编码器。</summary>
    /// <param name="frame">渲染帧；本方法接管它的所有权并负责释放。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>写帧完成的等待任务。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="InvalidOperationException">帧尺寸与请求不一致时抛出。</exception>
    Task WriteFrameAsync(RenderFrame frame, CancellationToken cancellationToken = default);

    /// <summary>结束输入、等待暂存文件封装完成；不会发布最终输出。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>编码结果及转交给调用方的暂存产物；调用方必须释放产物，会话释放不再清理它。</returns>
    /// <exception cref="IOException">编码进程失败（非零退出码）时抛出。</exception>
    Task<VideoEncodingResult> CompleteAsync(CancellationToken cancellationToken = default);
}
