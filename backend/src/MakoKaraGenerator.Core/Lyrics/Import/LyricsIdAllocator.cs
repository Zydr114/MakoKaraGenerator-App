namespace MakoKaraGenerator.Core.Lyrics.Import;

/// <summary>
/// 导入时给行与文本元素分配标识。
/// </summary>
/// <remarks>
/// 行与元素<b>共用同一个标识命名空间</b>（ADR-0006），因此两类前缀必须不同，
/// 否则一个文件的第 3 行和第 3 个元素会撞成同一个标识。
/// </remarks>
public sealed class LyricsIdAllocator
{
    private int _lineNumber;
    private int _tokenNumber;
    private int _rubyNumber;
    private int _rubySegmentNumber;

    /// <summary>下一个行标识。</summary>
    /// <returns>形如 <c>line-0001</c> 的标识。</returns>
    public string NextLineId()
    {
        _lineNumber++;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"line-{_lineNumber:D4}");
    }

    /// <summary>下一个元素标识。</summary>
    /// <returns>形如 <c>token-0001</c> 的标识。</returns>
    public string NextTokenId()
    {
        _tokenNumber++;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"token-{_tokenNumber:D4}");
    }

    /// <summary>下一个注音组标识。</summary>
    public string NextRubyId()
    {
        _rubyNumber++;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ruby-{_rubyNumber:D4}");
    }

    /// <summary>下一个注音片段标识。</summary>
    public string NextRubySegmentId()
    {
        _rubySegmentNumber++;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ruby-segment-{_rubySegmentNumber:D4}");
    }
}
