namespace MakoKaraGenerator.Export;

/// <summary>导出质量档位。</summary>
/// <remarks>
/// 三档的取值由 ADR-0031 定下（容器与编码 MP4 + <c>libx264</c>）：
/// 只改 CRF 与编码 preset，不改分辨率与帧率——后者属于工程设置，导出不该偷偷改它们。
/// </remarks>
public enum ExportQuality
{
    /// <summary>草稿：编码最快，用于快速检查节奏；画质明显有损。</summary>
    Draft,

    /// <summary>标准：默认档。</summary>
    Standard,

    /// <summary>高：成片档，最慢但画质最好。</summary>
    High,
}

/// <summary>一档质量对应的编码参数。</summary>
/// <param name="Quality">档位。</param>
/// <param name="Crf">x264 的恒定质量因子（越小越清晰、文件越大）。</param>
/// <param name="EncoderPreset">x264 的速度/压缩权衡预设名。</param>
/// <param name="DisplayName">界面显示名。</param>
public sealed record ExportQualityPreset(
    ExportQuality Quality,
    int Crf,
    string EncoderPreset,
    string DisplayName)
{
    /// <summary>三档预设，按质量从低到高。</summary>
    public static IReadOnlyList<ExportQualityPreset> All { get; } =
    [
        new(ExportQuality.Draft, 26, "fast", "草稿"),
        new(ExportQuality.Standard, 20, "medium", "标准"),
        new(ExportQuality.High, 16, "slow", "高"),
    ];

    /// <summary>取某个档位的预设。</summary>
    /// <param name="quality">档位。</param>
    /// <returns>对应的预设。</returns>
    /// <exception cref="ArgumentOutOfRangeException">档位不在枚举范围内时抛出。</exception>
    public static ExportQualityPreset For(ExportQuality quality) => quality switch
    {
        ExportQuality.Draft => All[0],
        ExportQuality.Standard => All[1],
        ExportQuality.High => All[2],
        _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "未知的导出质量档位。"),
    };
}
