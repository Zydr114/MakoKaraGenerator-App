using System.Collections.Immutable;

namespace MakoKaraGenerator.Media;

/// <summary>一个媒体流的信息（视频或音频）。</summary>
/// <param name="CodecType">流类型：<c>video</c>、<c>audio</c> 等。</param>
/// <param name="CodecName">编码器名，例如 <c>h264</c>、<c>aac</c>、<c>png</c>。</param>
/// <param name="Width">画面宽度；非视频流为 0。</param>
/// <param name="Height">画面高度；非视频流为 0。</param>
/// <param name="FrameRate">帧率；非视频流或无法解析时为 0。</param>
/// <param name="DurationUs">该流时长（微秒）；未知为 0。</param>
public sealed record MediaStreamInfo(
    string CodecType,
    string CodecName,
    int Width,
    int Height,
    double FrameRate,
    long DurationUs)
{
    /// <summary>
    /// 像素格式（例如 <c>yuv420p</c>、<c>yuva444p12le</c>）；非视频流或未探测到时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 存在的理由是 <c>EXPORT-07</c>：透明输出必须验证<b>实际</b> Alpha，
    /// 而不是根据文件扩展名猜（docs/development.md §12.3）。
    /// </remarks>
    public string? PixelFormat { get; init; }

    /// <summary>Actual frames decoded by explicit -count_frames probing; null for ordinary inexpensive probes.</summary>
    public long? DecodedFrameCount { get; init; }

    /// <summary>Exact reported rational frame rate numerator; zero when unavailable.</summary>
    public int FrameRateNumerator { get; init; }

    /// <summary>Exact reported rational frame rate denominator; zero when unavailable.</summary>
    public int FrameRateDenominator { get; init; }

    /// <summary>该像素格式是否含 Alpha 通道。</summary>
    /// <remarks>
    /// 判断依据是像素格式名：带 Alpha 的格式要么是 <c>yuva*</c>（如 <c>yuva444p12le</c>），
    /// 要么是 RGBA 四种通道排列（<c>rgba</c>/<c>bgra</c>/<c>argb</c>/<c>abgr</c>）及其高位宽变体，
    /// 另有少数特例（<c>gbrap</c>、<c>pal8</c>、<c>ya8</c>）。
    /// 拿不到像素格式时返回 <see langword="false"/>：<b>不知道就不算验证通过</b>。
    /// </remarks>
    public bool HasAlpha => PixelFormat is { Length: > 0 } format && HasAlphaPixelFormat(format);

    /// <summary>判断给定像素格式名是否带 Alpha 通道。</summary>
    /// <param name="pixelFormat">像素格式名。</param>
    /// <returns>带 Alpha 时为 <see langword="true"/>。</returns>
    public static bool HasAlphaPixelFormat(string pixelFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pixelFormat);

        return pixelFormat.StartsWith("yuva", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("rgba", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("bgra", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("argb", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("abgr", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.StartsWith("gbrap", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.Equals("pal8", StringComparison.OrdinalIgnoreCase)
            || pixelFormat.Equals("ya8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是否为视频流。</summary>
    public bool IsVideo => string.Equals(CodecType, "video", StringComparison.Ordinal);

    /// <summary>是否为音频流。</summary>
    public bool IsAudio => string.Equals(CodecType, "audio", StringComparison.Ordinal);
}

/// <summary>一个媒体文件的整体信息。</summary>
/// <param name="ContainerName">容器名，例如 <c>mov,mp4,m4a,3gp,3g2,mj2</c>、<c>wav</c>。</param>
/// <param name="DurationUs">文件时长（微秒）；未知为 0。</param>
/// <param name="Streams">全部流。</param>
/// <remarks>
/// 导出后的校验要回答"产物里到底有没有画面、是哪个编码器、多久"，
/// 这些都不在只看音频流的 <see cref="AudioStreamInfo"/> 范围里，
/// 因此另立一个覆盖全部流的模型（docs/roadmap.md §10 的 <c>EXPORT-01</c>、<c>EXPORT-09</c>）。
/// </remarks>
public sealed record MediaFileInfo(
    string ContainerName,
    long DurationUs,
    ImmutableArray<MediaStreamInfo> Streams)
{
    /// <summary>第一条视频流；没有时为 <see langword="null"/>。</summary>
    public MediaStreamInfo? Video => Streams.FirstOrDefault(static stream => stream.IsVideo);

    /// <summary>第一条音频流；没有时为 <see langword="null"/>。</summary>
    public MediaStreamInfo? Audio => Streams.FirstOrDefault(static stream => stream.IsAudio);

    /// <summary>是否含视频流。</summary>
    public bool HasVideo => Video is not null;

    /// <summary>是否含音频流。</summary>
    public bool HasAudio => Audio is not null;
}
