namespace MakoKaraGenerator.Core.Persistence;

/// <summary>可显式调整的持久IO限额；不裁切媒体或改变分析密度。</summary>
public sealed record ProjectStoreLimits
{
    /// <summary>单媒体允许的最大真实字节数，默认16GiB。</summary>
    public long MaximumMediaBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    /// <summary>单分析JSON的最大字节数，默认256MiB。</summary>
    public int MaximumAnalysisBytes { get; init; } = 256 * 1024 * 1024;
    /// <summary>限额必须为正数。</summary>
    public bool IsValid => MaximumMediaBytes > 0 && MaximumAnalysisBytes > 0;
}
