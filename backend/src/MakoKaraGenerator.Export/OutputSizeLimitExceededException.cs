namespace MakoKaraGenerator.Export;

/// <summary>
/// 当编码输出体积超过请求的 <see cref="VideoEncodingRequest.MaximumOutputBytes"/> 上限时抛出，
/// 防止 FFmpeg 触发 <c>-fs</c> 后以退出码 <c>0</c> 静默产出截断视频。
/// </summary>
public sealed class OutputSizeLimitExceededException : IOException
{
    /// <summary>创建一个输出超限异常。</summary>
    public OutputSizeLimitExceededException(long maximumBytes, long actualBytes, string message)
        : base(message)
    {
        MaximumBytes = maximumBytes;
        ActualBytes = actualBytes;
    }

    /// <summary>请求允许的最大输出字节数。</summary>
    public long MaximumBytes { get; }

    /// <summary>实际观测到的临时输出字节数。</summary>
    public long ActualBytes { get; }
}
