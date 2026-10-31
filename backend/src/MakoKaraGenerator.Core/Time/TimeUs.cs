namespace MakoKaraGenerator.Core.Time;

/// <summary>
/// 整数微秒时间的换算与格式化工具。
/// </summary>
/// <remarks>
/// <para>
/// 项目的内部时间统一使用整数微秒（<see cref="long"/>，单位 1 µs），
/// 理由见 docs/development.md §5.1：避免 <see cref="double"/> 的累积误差，
/// 并能同时表达时间码、毫秒和帧。
/// </para>
/// <para>
/// 所有换算函数的舍入方式为<b>四舍五入、远离零</b>，结果确定且与平台无关。
/// 换算的中间值使用 <see cref="Int128"/>，因此不会因大时间值而溢出。
/// </para>
/// <para>
/// 除格式化函数外，本类的公共成员要求时间值非负（docs/development.md §5.3：<c>startUs >= 0</c>）。
/// 负值属于非法数据，应在校验阶段被发现，而不是被静默接受。
/// </para>
/// </remarks>
public static class TimeUs
{
    /// <summary>一秒对应的微秒数。</summary>
    public const long PerSecond = 1_000_000;

    /// <summary>一毫秒对应的微秒数。</summary>
    public const long PerMillisecond = 1_000;

    /// <summary>一分钟对应的微秒数。</summary>
    public const long PerMinute = 60 * PerSecond;

    /// <summary>一小时对应的微秒数。</summary>
    public const long PerHour = 60 * PerMinute;

    /// <summary>把毫秒换算为微秒。</summary>
    /// <param name="milliseconds">毫秒数，必须非负。</param>
    /// <returns>对应的微秒数。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数为负或换算结果溢出 <see cref="long"/> 时抛出。</exception>
    public static long FromMilliseconds(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        return checked(milliseconds * PerMillisecond);
    }

    /// <summary>把秒换算为微秒。</summary>
    /// <param name="seconds">秒数，必须为有限值且落在 <see cref="long"/> 微秒可表示范围内。</param>
    /// <returns>对应的微秒数（四舍五入、远离零）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">参数为 NaN、无穷或超出可表示范围时抛出。</exception>
    public static long FromSeconds(double seconds)
    {
        if (!double.IsFinite(seconds))
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "秒数必须是有限值。");
        }

        if (seconds < long.MinValue / (double)PerSecond || seconds > long.MaxValue / (double)PerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "秒数超出微秒可表示的范围。");
        }

        return (long)Math.Round(seconds * PerSecond, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 把帧号换算为帧起始时间，公式为 <c>round(frameIndex * 1_000_000 / frameRate)</c>
    /// （docs/development.md §5.1）。
    /// </summary>
    /// <param name="frameIndex">帧号，必须非负。</param>
    /// <param name="frameRate">帧率。</param>
    /// <returns>该帧起始时刻的微秒数。</returns>
    /// <exception cref="ArgumentOutOfRangeException">帧号为负，或结果溢出 <see cref="long"/> 时抛出。</exception>
    public static long FromFrames(long frameIndex, FrameRate frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        Int128 numerator = (Int128)frameIndex * PerSecond * frameRate.Denominator;
        return DivideRoundHalfUp(numerator, frameRate.Numerator);
    }

    /// <summary>
    /// 把时间换算为最接近的帧号，公式为 <c>round(timeUs * frameRate / 1_000_000)</c>。
    /// </summary>
    /// <param name="timeUs">微秒数，必须非负。</param>
    /// <param name="frameRate">帧率。</param>
    /// <returns>最接近该时刻的帧号。</returns>
    /// <exception cref="ArgumentOutOfRangeException">时间为负，或结果溢出 <see cref="long"/> 时抛出。</exception>
    public static long ToFrames(long timeUs, FrameRate frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        Int128 numerator = (Int128)timeUs * frameRate.Numerator;
        return DivideRoundHalfUp(numerator, (Int128)PerSecond * frameRate.Denominator);
    }

    /// <summary>把微秒换算为秒。会丢失精度，仅用于显示或与外部数值 API 交换。</summary>
    /// <param name="timeUs">微秒数。</param>
    /// <returns>对应的秒数。</returns>
    public static double ToSeconds(long timeUs) => (double)timeUs / PerSecond;

    /// <summary>把微秒向下取整换算为毫秒。</summary>
    /// <param name="timeUs">微秒数，必须非负。</param>
    /// <returns>不超过 <paramref name="timeUs"/> 的最大整数毫秒数。</returns>
    /// <exception cref="ArgumentOutOfRangeException">时间为负时抛出。</exception>
    public static long ToMillisecondsFloor(long timeUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        return timeUs / PerMillisecond;
    }

    /// <summary>把微秒四舍五入换算为毫秒。</summary>
    /// <param name="timeUs">微秒数，必须非负。</param>
    /// <returns>最接近的整数毫秒数。</returns>
    /// <exception cref="ArgumentOutOfRangeException">时间为负时抛出。</exception>
    public static long ToMillisecondsRounded(long timeUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        return DivideRoundHalfUp(timeUs, PerMillisecond);
    }

    /// <summary>
    /// 格式化为完整时间码 <c>"hh:mm:ss.mmm"</c>（例：<c>00:01:02.345</c>）。
    /// </summary>
    /// <param name="timeUs">微秒数，允许为负（负值前置 <c>-</c>）。</param>
    /// <returns>固定宽度的时间码字符串，使用不变文化。</returns>
    public static string FormatClock(long timeUs)
    {
        bool negative = timeUs < 0;
        long totalMilliseconds = DivideRoundHalfUp(Int128.Abs((Int128)timeUs), PerMillisecond);

        long milliseconds = totalMilliseconds % 1_000;
        long totalSeconds = totalMilliseconds / 1_000;
        long seconds = totalSeconds % 60;
        long totalMinutes = totalSeconds / 60;
        long minutes = totalMinutes % 60;
        long hours = totalMinutes / 60;

        string body = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{hours:D2}:{minutes:D2}:{seconds:D2}.{milliseconds:D3}");

        return negative ? "-" + body : body;
    }

    /// <summary>
    /// 格式化为紧凑时间码 <c>"mm:ss.mmm"</c>（分钟不截断到 60，例：<c>102:03.500</c>）。
    /// </summary>
    /// <param name="timeUs">微秒数，允许为负（负值前置 <c>-</c>）。</param>
    /// <returns>紧凑时间码字符串，使用不变文化。</returns>
    public static string FormatClockShort(long timeUs)
    {
        bool negative = timeUs < 0;
        long totalMilliseconds = DivideRoundHalfUp(Int128.Abs((Int128)timeUs), PerMillisecond);

        long milliseconds = totalMilliseconds % 1_000;
        long totalSeconds = totalMilliseconds / 1_000;
        long seconds = totalSeconds % 60;
        long minutes = totalSeconds / 60;

        string body = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{minutes:D2}:{seconds:D2}.{milliseconds:D3}");

        return negative ? "-" + body : body;
    }

    /// <summary>
    /// 解析时间码文本。
    /// </summary>
    /// <param name="text">待解析的文本，前后空白会被忽略。</param>
    /// <param name="timeUs">解析出的微秒数。</param>
    /// <returns>解析成功时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>接受 <c>mm:ss.mmm</c>、<c>mm:ss.ff</c>（两位按百分秒补齐）、<c>mm:ss</c>、
    /// <c>ss.mmm</c>、<c>ss</c> 这些写法；分钟不限制在 60 以内（与
    /// <see cref="FormatClockShort"/> 对称）。</para>
    /// <para>没有冒号时整段按<b>秒</b>解释，不会按毫秒解释 —— 界面上显示的就是
    /// <c>mm:ss.mmm</c>，省略分钟的自然读法就是秒。</para>
    /// <para>只接受非负值、只接受 ASCII 数字、小数最多三位（界面只显示到毫秒，
    /// 接受更高精度只会制造"输入的值与看到的值不一致"的困惑）。
    /// 无法识别的输入一律返回 <see langword="false"/>，绝不猜测或静默修正
    /// （docs/development.md §19）。</para>
    /// </remarks>
    public static bool TryParseClock(string? text, out long timeUs)
    {
        timeUs = 0L;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        int colonIndex = span.IndexOf(':');

        // 最多一个冒号。不接受 hh:mm:ss —— 免得把 01:02:03 误读成 62 分 3 秒。
        if (colonIndex >= 0 && span[(colonIndex + 1)..].IndexOf(':') >= 0)
        {
            return false;
        }

        long minutes = 0L;
        ReadOnlySpan<char> secondsSpan = span;

        if (colonIndex >= 0)
        {
            if (!TryParseNonNegativeInteger(span[..colonIndex], out minutes))
            {
                return false;
            }

            secondsSpan = span[(colonIndex + 1)..];
        }

        int dotIndex = secondsSpan.IndexOf('.');
        long seconds;
        long milliseconds = 0L;

        if (dotIndex >= 0)
        {
            ReadOnlySpan<char> wholeSeconds = secondsSpan[..dotIndex];
            seconds = 0L;

            // 允许写成 ".5"（整数部分省略），但不允许写成 "."。
            if (!wholeSeconds.IsEmpty && !TryParseNonNegativeInteger(wholeSeconds, out seconds))
            {
                return false;
            }

            ReadOnlySpan<char> digits = secondsSpan[(dotIndex + 1)..];

            if (digits.IsEmpty
                || digits.Length > 3
                || !TryParseNonNegativeInteger(digits, out long rawDigits))
            {
                return false;
            }

            // 补齐到毫秒：".5" → 500、".05" → 50、".005" → 5。
            milliseconds = rawDigits;

            for (int i = digits.Length; i < 3; i++)
            {
                milliseconds *= 10L;
            }
        }
        else if (!TryParseNonNegativeInteger(secondsSpan, out seconds))
        {
            return false;
        }

        // 写了分钟才要求秒在 0..59；不写分钟时 "75" 就是 75 秒。
        if (colonIndex >= 0 && seconds >= 60)
        {
            return false;
        }

        Int128 total = ((Int128)minutes * PerMinute)
            + ((Int128)seconds * PerSecond)
            + ((Int128)milliseconds * PerMillisecond);

        if (total > long.MaxValue)
        {
            return false;
        }

        timeUs = (long)total;
        return true;
    }

    /// <summary>解析一个非负十进制整数（只认 ASCII 数字，拒绝符号与空白）。</summary>
    private static bool TryParseNonNegativeInteger(ReadOnlySpan<char> span, out long value)
    {
        if (span.IsEmpty)
        {
            value = 0L;
            return false;
        }

        return long.TryParse(
            span,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// 格式化为 LRC 时间戳正文 <c>"mm:ss.xx"</c>（不含方括号，例：<c>01:02.34</c>）。
    /// </summary>
    /// <param name="timeUs">微秒数，必须非负。</param>
    /// <returns>LRC 时间戳正文，使用不变文化。</returns>
    /// <exception cref="ArgumentOutOfRangeException">时间为负时抛出。</exception>
    /// <remarks>
    /// LRC 只支持 10 ms（1/100 秒）精度，因此这里先换算为「总厘秒」再做分解，
    /// 避免 1:59.998 这类值在分解各字段时出现进位错误。
    /// </remarks>
    public static string FormatLrcTimestamp(long timeUs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);

        const long perCentisecond = PerMillisecond * 10;
        long totalCentiseconds = DivideRoundHalfUp(timeUs, perCentisecond);

        long centiseconds = totalCentiseconds % 100;
        long totalSeconds = totalCentiseconds / 100;
        long seconds = totalSeconds % 60;
        long minutes = totalSeconds / 60;

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{minutes:D2}:{seconds:D2}.{centiseconds:D2}");
    }

    /// <summary>计算 <paramref name="numerator"/> / <paramref name="denominator"/>，四舍五入、远离零。</summary>
    /// <exception cref="ArgumentOutOfRangeException">分子为负、分母非正，或结果溢出 <see cref="long"/> 时抛出。</exception>
    private static long DivideRoundHalfUp(Int128 numerator, Int128 denominator)
    {
        if (denominator <= Int128.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "分母必须为正数。");
        }

        if (numerator < Int128.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(numerator), numerator, "分子必须为非负数。");
        }

        Int128 quotient = numerator / denominator;
        Int128 remainder = numerator % denominator;

        if (remainder + remainder >= denominator)
        {
            quotient++;
        }

        return checked((long)quotient);
    }
}
