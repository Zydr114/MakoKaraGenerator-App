using System.Globalization;
using MakoKaraGenerator.Core.Lyrics;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>沿编辑器既有编号样式，为一个候选分配全歌词命名空间唯一的标识。</summary>
/// <remarks>扫描所有来源、行、元素和注音；同一候选须复用本实例，分配即保留标识。编号下界为1000，数字上界为32位有符号整数。</remarks>
public sealed class LyricsEditIdAllocator
{
    private readonly HashSet<string> _identifiers = new(StringComparer.Ordinal);
    private readonly Action<string>? _beforeIdentifier;
    private long _nextLine = 1000;
    private long _nextToken = 1000;

    /// <summary>捕获当前文档中已经占用的全局歌词标识。</summary>
    /// <param name="document">全部歌词来源；不扫描场景对象命名空间。</param>
    public LyricsEditIdAllocator(LyricsDocument document) : this(document, null) { }

    /// <summary>使用原唯一编号算法，并在每次标识集合插入前通知可选准备配额。</summary>
    /// <param name="document">全部歌词来源。</param>
    /// <param name="beforeIdentifier">已有和新标识保留前调用；不改变编号规则。</param>
    public LyricsEditIdAllocator(LyricsDocument document, Action<string>? beforeIdentifier)
    {
        _beforeIdentifier = beforeIdentifier;
        ArgumentNullException.ThrowIfNull(document);
        foreach (LyricBranch branch in document.Branches)
        {
            Observe(branch.Id);
            foreach (LyricLine line in branch.Lines)
            {
                Observe(line.Id);
                foreach (LyricToken token in line.Tokens) Observe(token.Id);
                foreach (RubyAnnotation ruby in line.RubyAnnotations)
                {
                    Observe(ruby.Id);
                    foreach (RubyTextSegment segment in ruby.Segments) Observe(segment.Id);
                }
            }
        }
    }

    /// <summary>分配并保留一个 line- 前缀的行标识。</summary>
    /// <returns>全局唯一的行标识。</returns>
    /// <exception cref="LyricsIdentifierExhaustedException">既有数字范围已经用尽。</exception>
    public string NextLineId() => Allocate("line-", ref _nextLine);

    /// <summary>分配并保留一个 token- 前缀的元素标识；复制注音也沿用此编号规则。</summary>
    /// <returns>全局唯一的元素或复制注音标识。</returns>
    /// <exception cref="LyricsIdentifierExhaustedException">既有数字范围已经用尽。</exception>
    public string NextTokenId() => Allocate("token-", ref _nextToken);

    internal string NextRubyId()
    {
        string id = "ruby-" + Guid.NewGuid().ToString("N");
        _beforeIdentifier?.Invoke(id);
        if (!_identifiers.Add(id)) throw new LyricsIdentifierExhaustedException();
        return id;
    }

    internal string NextRubySegmentId()
    {
        string id = "ruby-segment-" + Guid.NewGuid().ToString("N");
        _beforeIdentifier?.Invoke(id);
        if (!_identifiers.Add(id)) throw new LyricsIdentifierExhaustedException();
        return id;
    }

    internal string NextBranchId(string prefix)
    {
        string id = prefix + "-" + Guid.NewGuid().ToString("N");
        _beforeIdentifier?.Invoke(id);
        if (!_identifiers.Add(id)) throw new LyricsIdentifierExhaustedException();
        return id;
    }

    private void Observe(string id)
    {
        _beforeIdentifier?.Invoke(id);
        _identifiers.Add(id);
        if (id.StartsWith("line-", StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int lineNumber))
            _nextLine = Math.Max(_nextLine, (long)lineNumber + 1);
        if (id.StartsWith("token-", StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int tokenNumber))
            _nextToken = Math.Max(_nextToken, (long)tokenNumber + 1);
    }

    private string Allocate(string prefix, ref long next)
    {
        if (next > int.MaxValue) throw new LyricsIdentifierExhaustedException();
        string id = prefix + next.ToString(CultureInfo.InvariantCulture);
        next++;
        _beforeIdentifier?.Invoke(id);
        if (!_identifiers.Add(id)) throw new LyricsIdentifierExhaustedException();
        return id;
    }
}

/// <summary>编辑器既有标识范围已经用尽，调用方应拒绝整个候选并提示用户。</summary>
public sealed class LyricsIdentifierExhaustedException : InvalidOperationException
{
    /// <summary>创建一个可由适配层精确捕获并本地化的标识耗尽错误。</summary>
    public LyricsIdentifierExhaustedException() : base("Lyric identifier range is exhausted.") { }
}
