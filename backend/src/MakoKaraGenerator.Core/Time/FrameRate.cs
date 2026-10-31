namespace MakoKaraGenerator.Core.Time;

/// <summary>
/// 有理数帧率，例如 <c>30000/1001</c> 表示 29.97 fps。
/// </summary>
/// <remarks>
/// <para>
/// 帧率<b>不使用</b> <see cref="double"/> 表示。29.97、23.976 等值无法用二进制浮点精确表达，
/// 会在帧号与微秒之间反复换算时产生累积误差（docs/development.md §5.1 要求由帧率换算时间，
/// 且导出时报告舍入）。
/// </para>
/// <para>
/// 构造时按最大公约数约分，因此 <c>new FrameRate(25, 1)</c> 与 <c>new FrameRate(50, 2)</c> 相等。
/// </para>
/// </remarks>
public readonly record struct FrameRate
{
    /// <summary>23.976 fps（<c>24000/1001</c>），NTSC 电影帧率。</summary>
    public static FrameRate NtscFilm { get; } = new(24000, 1001);

    /// <summary>29.97 fps（<c>30000/1001</c>），NTSC 视频帧率。</summary>
    public static FrameRate NtscVideo { get; } = new(30000, 1001);

    /// <summary>59.94 fps（<c>60000/1001</c>），NTSC 双倍帧率。</summary>
    public static FrameRate NtscDouble { get; } = new(60000, 1001);

    /// <summary>24 fps。</summary>
    public static FrameRate Fps24 { get; } = new(24, 1);

    /// <summary>25 fps（PAL / SECAM）。</summary>
    public static FrameRate Fps25 { get; } = new(25, 1);

    /// <summary>30 fps。</summary>
    public static FrameRate Fps30 { get; } = new(30, 1);

    /// <summary>50 fps。</summary>
    public static FrameRate Fps50 { get; } = new(50, 1);

    /// <summary>60 fps。</summary>
    public static FrameRate Fps60 { get; } = new(60, 1);

    /// <summary>构造一个已约分的帧率。</summary>
    /// <param name="numerator">分子（每秒帧数的整数部分），必须为正。</param>
    /// <param name="denominator">分母，必须为正。</param>
    /// <exception cref="ArgumentOutOfRangeException">分子或分母不是正数时抛出。</exception>
    public FrameRate(int numerator, int denominator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(numerator);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(denominator);

        int divisor = GreatestCommonDivisor(numerator, denominator);
        Numerator = numerator / divisor;
        Denominator = denominator / divisor;
    }

    /// <summary>已约分的分子。</summary>
    public int Numerator { get; }

    /// <summary>已约分的分母。</summary>
    public int Denominator { get; }

    /// <summary>以 <see cref="double"/> 表示的帧率值。仅用于显示，不得用于时间换算。</summary>
    public double Value => (double)Numerator / Denominator;

    /// <summary>是否为整数帧率（分母为 1）。</summary>
    public bool IsInteger => Denominator == 1;

    /// <summary>以 <c>"30000/1001"</c> 或 <c>"30"</c> 形式返回。</summary>
    public override string ToString() =>
        IsInteger
            ? Numerator.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{Numerator}/{Denominator}");

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return left;
    }
}
