using System.Collections.Immutable;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>注音在基础字上方的水平对齐方式。</summary>
public enum RubyAlignment
{
    /// <summary>与基础字中心对齐。</summary>
    Center = 0,

    /// <summary>与基础字起点对齐。</summary>
    Start = 1,

    /// <summary>与基础字终点对齐。</summary>
    End = 2,
}

/// <summary>
/// 绑定基础文本元素的注音组；片段可拥有独立绝对时间，无时间兼容片段沿旧规则跟随基础元素。
/// </summary>
public sealed record RubyAnnotation
{
    private string _id = string.Empty;
    private string _text = string.Empty;
    private ImmutableArray<RubyTextSegment> _segments;

    /// <summary>注音标识。</summary>
    public required string Id { get => _id; init => _id = value; }

    /// <summary>关联的基础文本元素标识。</summary>
    public required string BaseTokenId { get; init; }

    /// <summary>注音文本，例如日文汉字的假名读音。</summary>
    public required string Text
    {
        get => _text;
        // 旧 setRubyText/with { Text = ... } 路径保留，但明确重置为一个无时间片段，
        // 避免悄悄保留已计时片段的旧文本。
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _text = value;
            string segmentId = _segments.IsDefaultOrEmpty
                ? (string.IsNullOrWhiteSpace(_id) ? "segment-1" : RubyTextSegment.CompatibilityId(_id))
                : _segments[0].Id;
            _segments = value.Length == 0 ? [] : [RubyTextSegment.Create(segmentId, value)];
        }
    }

    /// <summary>按文本顺序排列的注音片段；这是注音时间的权威来源。</summary>
    public ImmutableArray<RubyTextSegment> Segments
    {
        get => _segments.IsDefault
            ? (string.IsNullOrEmpty(_text)
                ? []
                : [RubyTextSegment.Create(string.IsNullOrWhiteSpace(_id) ? "segment-1" : RubyTextSegment.CompatibilityId(_id), _text)])
            : _segments;
        init => _segments = value;
    }

    /// <summary>注音语言标签。</summary>
    public string? Language { get; init; }

    /// <summary>相对于基础字字号的比例。</summary>
    public double RelativeScale { get; init; } = 0.5d;

    /// <summary>注音与基础字之间的画布像素间距。</summary>
    public double GapPx { get; init; } = 2d;

    /// <summary>注音的水平对齐方式。</summary>
    public RubyAlignment Alignment { get; init; } = RubyAlignment.Center;

    /// <summary>注音数据是否有效。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id)
        && !string.IsNullOrWhiteSpace(BaseTokenId)
        && !string.IsNullOrEmpty(Text)
        && !Segments.IsDefaultOrEmpty
        && string.Equals(Text, string.Concat(Segments.Select(segment => segment.Text)), StringComparison.Ordinal)
        && Segments.All(segment => segment.IsValid)
        && Enum.IsDefined(Alignment)
        && double.IsFinite(RelativeScale)
        && RelativeScale > 0d
        && double.IsFinite(GapPx)
        && GapPx >= 0d;

    /// <summary>返回可供 TTML 编解码器使用的片段视图。</summary>
    public IReadOnlyList<RubyTextSegment> EffectiveSegments => Segments.Length > 0
        ? Segments
        : string.IsNullOrEmpty(Text) ? [] : [RubyTextSegment.Create(RubyTextSegment.CompatibilityId(Id), Text)];

    /// <summary>按片段内容比较，避免 <see cref="ImmutableArray{T}"/> 的数组引用比较破坏持久化往返。</summary>
    public bool Equals(RubyAnnotation? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(BaseTokenId, other.BaseTokenId, StringComparison.Ordinal)
            && string.Equals(Text, other.Text, StringComparison.Ordinal)
            && string.Equals(Language, other.Language, StringComparison.Ordinal)
            && RelativeScale.Equals(other.RelativeScale)
            && GapPx.Equals(other.GapPx)
            && Alignment == other.Alignment
            && Segments.SequenceEqual(other.Segments);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(BaseTokenId, StringComparer.Ordinal);
        hash.Add(Text, StringComparer.Ordinal);
        hash.Add(Language, StringComparer.Ordinal);
        hash.Add(RelativeScale);
        hash.Add(GapPx);
        hash.Add(Alignment);
        foreach (RubyTextSegment segment in Segments) hash.Add(segment);
        return hash.ToHashCode();
    }

    /// <summary>创建一个已校验的注音对象。</summary>
    /// <param name="id">注音标识。</param>
    /// <param name="baseTokenId">基础文本元素标识。</param>
    /// <param name="text">注音文本。</param>
    /// <param name="language">语言标签。</param>
    /// <param name="relativeScale">相对字号比例，必须为正。</param>
    /// <param name="gapPx">与基础字的间距，必须为非负数。</param>
    /// <param name="alignment">水平对齐方式。</param>
    /// <returns>新的注音对象。</returns>
    /// <exception cref="ArgumentException">文本标识为空或参数越界时抛出。</exception>
    public static RubyAnnotation Create(
        string id,
        string baseTokenId,
        string text,
        string? language = null,
        double relativeScale = 0.5d,
        double gapPx = 2d,
        RubyAlignment alignment = RubyAlignment.Center)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseTokenId);
        ArgumentException.ThrowIfNullOrEmpty(text);

        RubyAnnotation result = new()
        {
            Id = id,
            BaseTokenId = baseTokenId,
            Text = text,
            Segments = [RubyTextSegment.Create(RubyTextSegment.CompatibilityId(id), text)],
            Language = language,
            RelativeScale = relativeScale,
            GapPx = gapPx,
            Alignment = alignment,
        };

        return result.IsValid
            ? result
            : throw new ArgumentException("注音的字号比例、间距或对齐方式无效。", nameof(relativeScale));
    }
}
