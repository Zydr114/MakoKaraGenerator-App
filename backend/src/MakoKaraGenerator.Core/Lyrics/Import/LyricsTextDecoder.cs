using System.Text;

namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 解码文本时确定下来的事实（编码、BOM、换行符）。
/// </summary>
/// <param name="Text">解码后的文本（已去掉 BOM）。</param>
/// <param name="EncodingName">实际采用的编码名。</param>
/// <param name="HadByteOrderMark">文件是否带 BOM。</param>
/// <param name="NewLineKind">检测到的换行符种类。</param>
/// <param name="IsFallbackDecoding">是否使用了"无法确定编码"的近似解码。</param>
public sealed record LyricsTextInfo(
    string Text,
    string EncodingName,
    bool HadByteOrderMark,
    LyricsNewLineKind NewLineKind,
    bool IsFallbackDecoding);

/// <summary>
/// 把歌词文件的字节解码成文本，并报告编码与换行符的处理结果。
/// </summary>
/// <remarks>
/// <para>
/// 顺序是：BOM → 严格 UTF-8 → GB18030 → Latin-1 近似。
/// </para>
/// <para>
/// 严格 UTF-8 排在前面是因为它是当今的绝大多数；GB18030 排在后面是因为中文歌词文件
/// 里有相当一部分是 GBK/GB18030 编码，而 GBK 的字节能通过 UTF-8 校验的比例很低，
/// 因此"UTF-8 校验失败就按 GB18030 试"在实践中足够可靠。
/// </para>
/// <para>
/// 兜底的 Latin-1 一定不会抛异常，因此永远返回文本；但这种情况会在导入报告里
/// 明确标成"编码不确定"（docs/development.md §7.3 要求报告编码处理结果）。
/// </para>
/// </remarks>
public static class LyricsTextDecoder
{
    private static int _codePagesRegistered;

    /// <summary>把一个流解码成文本。</summary>
    /// <param name="input">输入流（会被完整读到内存；歌词文件很小）。</param>
    /// <returns>解码结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input" /> 为 <see langword="null" /> 时抛出。</exception>
    public static LyricsTextInfo Decode(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        using MemoryStream buffer = new();
        input.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    /// <summary>把一个流解码成文本（可强制编码）。</summary>
    /// <param name="input">输入流（会被完整读到内存；歌词文件很小）。</param>
    /// <param name="forcedEncoding">强制使用的编码；为 <see langword="null" /> 时自动检测。</param>
    /// <returns>解码结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input" /> 为 <see langword="null" /> 时抛出。</exception>
    public static LyricsTextInfo Decode(Stream input, Encoding? forcedEncoding)
    {
        ArgumentNullException.ThrowIfNull(input);

        using MemoryStream buffer = new();
        input.CopyTo(buffer);
        return Decode(buffer.ToArray(), forcedEncoding);
    }

    /// <summary>把一段字节解码成文本。</summary>
    /// <param name="bytes">原始字节。</param>
    /// <returns>解码结果。</returns>
    public static LyricsTextInfo Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, forcedEncoding: null);

    /// <summary>把一段字节解码成文本（可强制编码）。</summary>
    /// <param name="bytes">原始字节。</param>
    /// <param name="forcedEncoding">强制使用的编码；为 <see langword="null" /> 时自动检测。</param>
    /// <returns>解码结果。</returns>
    public static LyricsTextInfo Decode(ReadOnlySpan<byte> bytes, Encoding? forcedEncoding)
    {
        (Encoding? bomEncoding, int bomLength) = DetectByteOrderMark(bytes);
        string text;
        string encodingName;
        bool hadBom = bomEncoding is not null;
        bool fallback = false;

        if (forcedEncoding is not null)
        {
            // 强制编码：跳过自动检测，但仍如实报告有没有 BOM；BOM 字节本身不参与解码。
            int skip = bomEncoding is null ? 0 : bomLength;
            text = forcedEncoding.GetString(bytes[skip..]);
            encodingName = forcedEncoding.WebName;
        }
        else if (bomEncoding is not null)
        {
            text = bomEncoding.GetString(bytes[bomLength..]);
            encodingName = bomEncoding.WebName;
        }
        else if (TryDecodeStrict(bytes, new UTF8Encoding(false, true), out text))
        {
            encodingName = "utf-8";
        }
        else if (TryDecodeStrict(bytes, TryGetCodePageEncoding("GB18030"), out text))
        {
            encodingName = "gb18030";
        }
        else
        {
            text = Encoding.Latin1.GetString(bytes);
            encodingName = "iso-8859-1";
            fallback = true;
        }

        return new LyricsTextInfo(text, encodingName, hadBom, DetectNewLine(text), fallback);
    }

    /// <summary>检测文本里的换行符种类。</summary>
    /// <param name="text">文本。</param>
    /// <returns>换行符种类。</returns>
    public static LyricsNewLineKind DetectNewLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        bool lf = false;
        bool crlf = false;
        bool cr = false;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf = true;
                    i++;
                }
                else
                {
                    cr = true;
                }
            }
            else if (text[i] == '\n')
            {
                lf = true;
            }
        }

        int kinds = (lf ? 1 : 0) + (crlf ? 1 : 0) + (cr ? 1 : 0);

        return kinds switch
        {
            0 => LyricsNewLineKind.None,
            > 1 => LyricsNewLineKind.Mixed,
            _ when crlf => LyricsNewLineKind.CarriageReturnLineFeed,
            _ when cr => LyricsNewLineKind.CarriageReturn,
            _ => LyricsNewLineKind.LineFeed,
        };
    }

    /// <summary>识别 BOM。</summary>
    private static (Encoding? Encoding, int Length) DetectByteOrderMark(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(false), 3);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
        {
            return (new UTF32Encoding(false, false), 4);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return (new UnicodeEncoding(false, false), 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return (new UnicodeEncoding(true, false), 2);
        }

        return (null, 0);
    }

    /// <summary>用指定编码严格解码；出现非法字节序列时返回 <see langword="false" />。</summary>
    private static bool TryDecodeStrict(ReadOnlySpan<byte> bytes, Encoding? encoding, out string text)
    {
        text = string.Empty;

        if (encoding is null)
        {
            return false;
        }

        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>取一个代码页编码；运行时不支持时返回 <see langword="null" />。</summary>
    private static Encoding? TryGetCodePageEncoding(string name)
    {
        // CodePages 在部分目标框架里需要显式注册；注册是幂等的进程级副作用。
        if (Interlocked.Exchange(ref _codePagesRegistered, 1) == 0)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            }
            catch (NotSupportedException)
            {
                // 运行时不带 CodePages：后面的解析会退到 Latin-1 并如实报告。
            }
        }

        try
        {
            return Encoding.GetEncoding(
                name,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ExceptionFallback);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
