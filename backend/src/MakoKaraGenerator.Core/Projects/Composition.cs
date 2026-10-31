using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>
/// 合成设置：画布尺寸、帧率、背景色与输出时长。
/// </summary>
/// <remarks>
/// 对应 docs/development.md §6.1 中 <c>project.json</c> 的画布与时间基准部分，
/// 以及 §4.1 P0 要求的"画布宽度、高度、帧率、背景颜色和输出时长"。
/// </remarks>
public sealed record Composition
{
    /// <summary>画布宽度（像素）。默认 1920。</summary>
    public int CanvasWidth { get; init; } = 1920;

    /// <summary>画布高度（像素）。默认 1080。</summary>
    public int CanvasHeight { get; init; } = 1080;

    /// <summary>输出帧率。默认 30 fps。</summary>
    public FrameRate FrameRate { get; init; } = FrameRate.Fps30;

    /// <summary>画布背景色。默认不透明黑。</summary>
    public RgbaColor BackgroundColor { get; init; } = RgbaColor.Black;

    /// <summary>
    /// 输出时长（微秒）。
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> 表示尚未确定——项目刚建立、还没有导入音频或背景时属于正常状态。
    /// 校验器会在导出前把"仍为 <see langword="null"/>"报为阻断性错误，
    /// 而不是在这里用某个猜测值静默替代。
    /// </remarks>
    public long? OutputDurationUs { get; init; }

    /// <summary>所有设置是否都在有效范围内。</summary>
    /// <remarks>
    /// 本属性只用于快速判断。具体是哪一项不合法应由校验器（CORE-05）逐项报告。
    /// </remarks>
    public bool IsValid =>
        CanvasWidth > 0
        && CanvasHeight > 0
        && OutputDurationUs is null or > 0;

    /// <summary>创建一个已校验的合成设置。</summary>
    /// <param name="canvasWidth">画布宽度，必须为正。</param>
    /// <param name="canvasHeight">画布高度，必须为正。</param>
    /// <param name="frameRate">输出帧率。</param>
    /// <param name="backgroundColor">背景色。</param>
    /// <param name="outputDurationUs">输出时长（微秒），可为 <see langword="null"/>。</param>
    /// <returns>新的合成设置实例。</returns>
    /// <exception cref="ArgumentOutOfRangeException">画布尺寸非正，或输出时长已给出但非正时抛出。</exception>
    public static Composition Create(
        int canvasWidth,
        int canvasHeight,
        FrameRate frameRate,
        RgbaColor? backgroundColor = null,
        long? outputDurationUs = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(canvasHeight);

        if (outputDurationUs is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outputDurationUs),
                outputDurationUs,
                "输出时长必须为正数，或留空表示尚未确定。");
        }

        return new Composition
        {
            CanvasWidth = canvasWidth,
            CanvasHeight = canvasHeight,
            FrameRate = frameRate,
            BackgroundColor = backgroundColor ?? RgbaColor.Black,
            OutputDurationUs = outputDurationUs,
        };
    }
}
