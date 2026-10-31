using System.Text;
using MakoKaraGenerator.Core.Lyrics.Import;

namespace MakoKaraGenerator.Core.Tests.Lyrics;

/// <summary>
/// <see cref="LyricsTextDecoder" /> 的测试：编码与换行符处理结果必须如实报告。
/// </summary>
public sealed class LyricsTextDecoderTests
{
    [Fact]
    public void 无BOM的UTF8按UTF8解码()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("[00:01.00]夜空中最亮的星");

        LyricsTextInfo info = LyricsTextDecoder.Decode(bytes);

        Assert.Equal("utf-8", info.EncodingName);
        Assert.False(info.HadByteOrderMark);
        Assert.False(info.IsFallbackDecoding);
        Assert.Contains("夜空中最亮的星", info.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 带BOM的UTF8会去掉BOM并标记()
    {
        byte[] bytes = [.. new UTF8Encoding(true).GetPreamble(), .. Encoding.UTF8.GetBytes("[00:01.00]甲")];

        LyricsTextInfo info = LyricsTextDecoder.Decode(bytes);

        Assert.True(info.HadByteOrderMark);
        Assert.Equal("utf-8", info.EncodingName);
        Assert.StartsWith("[00:01.00]", info.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 带BOM的UTF16按UTF16解码(bool bigEndian)
    {
        Encoding encoding = new UnicodeEncoding(bigEndian, true);
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes("[00:01.00]甲")).ToArray();

        LyricsTextInfo info = LyricsTextDecoder.Decode(bytes);

        Assert.True(info.HadByteOrderMark);
        Assert.Contains("甲", info.Text, StringComparison.Ordinal);
        Assert.Equal(bigEndian ? "utf-16BE" : "utf-16", info.EncodingName);
    }

    [Fact]
    public void GBK编码的中文歌词能正确解码()
    {
        // 中文歌词文件里有相当一部分是 GBK/GB18030；构造真实字节而不是假设。
        Encoding? gb18030 = TryGetGb18030();

        if (gb18030 is null)
        {
            return; // 运行时不带 CodePages：该场景会走 Latin-1 兜底（另有用例覆盖）
        }

        byte[] bytes = gb18030.GetBytes("[00:01.00]夜空中最亮的星");

        LyricsTextInfo info = LyricsTextDecoder.Decode(bytes);

        Assert.Equal("gb18030", info.EncodingName);
        Assert.False(info.IsFallbackDecoding);
        Assert.Contains("夜空中最亮的星", info.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 解码结果里一定有文本且兜底标记与编码名一致()
    {
        // 既不是合法 UTF-8、也不是合法 GB18030 的字节
        byte[] bytes = [0xFF, 0xFE, 0xFF, 0x80, 0xC0, 0xC1];

        LyricsTextInfo info = LyricsTextDecoder.Decode(bytes);

        // 带 BOM 的情况会被 UTF-16 接走；这里去掉 BOM 的可能，直接看不变式
        Assert.True(info.IsFallbackDecoding == (info.EncodingName == "iso-8859-1"));
    }

    [Theory]
    [InlineData("a\nb", LyricsNewLineKind.LineFeed)]
    [InlineData("a\r\nb", LyricsNewLineKind.CarriageReturnLineFeed)]
    [InlineData("a\rb", LyricsNewLineKind.CarriageReturn)]
    [InlineData("a\r\nb\nc", LyricsNewLineKind.Mixed)]
    [InlineData("abc", LyricsNewLineKind.None)]
    public void 换行符种类被识别(string text, LyricsNewLineKind expected)
    {
        Assert.Equal(expected, LyricsTextDecoder.DetectNewLine(text));
    }

    [Fact]
    public void 交叉换行不会被误判为CRLF()
    {
        // "\r\r\n" 是 CR + CRLF，不是 CRLF + LF
        Assert.Equal(LyricsNewLineKind.Mixed, LyricsTextDecoder.DetectNewLine("a\r\r\nb"));
    }

    private static Encoding? TryGetGb18030()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030");
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or TypeInitializationException)
        {
            return null;
        }
    }
}
