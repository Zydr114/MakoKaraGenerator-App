namespace MakoKaraGenerator.Core.Lyrics.Lrc;

/// <summary>
/// 解析 LRC 标签里的时间戳正文（方括号内部的内容）。
/// </summary>
/// <remarks>
/// <para>
/// LRC 的时间戳在实际文件里有好几种写法，本解析器接受：
/// <c>mm:ss</c>、<c>mm:ss.f</c>～<c>mm:ss.fff</c>（点号后 1～3 位，按十进制小数补齐到毫秒）、
/// 以及 <c>mm:ss:ff</c>（第二段分隔符是冒号，末段按<b>百分秒</b>解释，
/// 这是部分工具的输出，例如 <c>[01:02:50]</c> 表示 1 分 2.50 秒）。
/// </para>
/// <para>
/// 分不限制在 60 以内（与 <see cref="Time.TimeUs.FormatClockShort" /> 对称），
/// 秒必须在 0～59。<b>不接受负时间</b>：本项目的时间轴以音频零点为原点。
/// </para>
/// <para>
/// 不认识的内容一律返回 <see langword="false" />，由调用方记成诊断而不是猜测
/// （docs/development.md §7.2、§19）。
/// </para>
/// </remarks>
public static class LrcTimeTagParser
{
    /// <summary>尝试解析一个时间戳标签的正文。</summary>
    /// <param name="inner">方括号内部的内容，例如 <c>00:12.34</c>。</param>
    /// <param name="timeUs">解析出的微秒数。</param>
    /// <returns>解析成功时为 <see langword="true" />。</returns>
    public static bool TryParse(ReadOnlySpan<char> inner, out long timeUs)
    {
        timeUs = 0L;
        inner = inner.Trim();

        int firstColon = inner.IndexOf(':');

        if (firstColon <= 0)
        {
            return false;
        }

        if (!TryParseDigits(inner[..firstColon], out long minutes))
        {
            return false;
        }

        ReadOnlySpan<char> rest = inner[(firstColon + 1)..];
        int dotIndex = rest.IndexOf('.');
        int secondColon = rest.IndexOf(':');

        long seconds;
        long milliseconds;

        if (dotIndex >= 0)
        {
            // mm:ss.fff（点号后 1～3 位，补齐到毫秒）
            if (!TryParseDigits(rest[..dotIndex], out seconds))
            {
                return false;
            }

            ReadOnlySpan<char> fraction = rest[(dotIndex + 1)..];

            if (fraction.IsEmpty || fraction.Length > 3 || !TryParseDigits(fraction, out long raw))
            {
                return false;
            }

            milliseconds = raw;

            for (int i = fraction.Length; i < 3; i++)
            {
                milliseconds *= 10L;
            }
        }
        else if (secondColon >= 0)
        {
            // mm:ss:ff（末段是百分秒，必须恰好两位）
            if (!TryParseDigits(rest[..secondColon], out seconds))
            {
                return false;
            }

            ReadOnlySpan<char> hundredths = rest[(secondColon + 1)..];

            if (hundredths.Length != 2 || !TryParseDigits(hundredths, out long raw))
            {
                return false;
            }

            milliseconds = raw * 10L;
        }
        else
        {
            // mm:ss
            if (!TryParseDigits(rest, out seconds))
            {
                return false;
            }

            milliseconds = 0L;
        }

        if (seconds >= 60)
        {
            return false;
        }

        Int128 total = ((Int128)minutes * 60L * Time.TimeUs.PerSecond)
            + ((Int128)seconds * Time.TimeUs.PerSecond)
            + ((Int128)milliseconds * Time.TimeUs.PerMillisecond);

        if (total > long.MaxValue)
        {
            return false;
        }

        timeUs = (long)total;
        return true;
    }

    /// <summary>解析一段纯 ASCII 数字（不接受符号、空白与小数点）。</summary>
    private static bool TryParseDigits(ReadOnlySpan<char> span, out long value)
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
}
