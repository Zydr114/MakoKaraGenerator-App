using System.Collections.Immutable;
using MakoKaraGenerator.Core.Internal;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 一行歌词。
/// </summary>
/// <remarks>
/// <para>
/// 行同时保存整行文本与可选的文本元素列表。按 docs/development.md §9.4，
/// 渲染必须使用<b>整行文本</b>做一次整形与布局，再在稳定布局上施加逐字裁切；
/// 因此 <see cref="Text"/> 不是 <see cref="Tokens"/> 的冗余副本，而是布局的权威来源。
/// </para>
/// <para>
/// 本类型不可变；详见 <see cref="LyricToken"/> 的说明。
/// </para>
/// </remarks>
public sealed record LyricLine
{
    private ImmutableArray<LyricToken> _tokens;
    private ImmutableArray<RubyAnnotation> _rubyAnnotations;

    /// <summary>关联的主歌词行稳定 ID；为空时使用本行独立时间。</summary>
    public string? PrimaryLineId { get; init; }

    /// <summary>绑定基础字的注音组；各组可包含独立计时的文本片段。</summary>
    public ImmutableArray<RubyAnnotation> RubyAnnotations
    {
        get => _rubyAnnotations.IsDefault ? [] : _rubyAnnotations;
        init => _rubyAnnotations = value;
    }

    /// <summary>行标识，在同一文档内必须唯一。</summary>
    public required string Id { get; init; }

    /// <summary>整行原始文本。允许为空字符串（校验器会把空文本报为错误）。</summary>
    public required string Text { get; init; }

    /// <summary>行的时间区间；<see langword="null"/> 表示时间缺失。</summary>
    public TimeRange? Time { get; init; }

    /// <summary>行级时间的来源状态。行级时间<b>不得</b>自动冒充逐字精确时间。</summary>
    public TimingStatus TimingStatus { get; init; }

    /// <summary>
    /// 本行的样式覆盖；<see langword="null"/> 表示完全继承基线样式。
    /// </summary>
    /// <remarks>
    /// 覆盖只记录"用户显式改过的属性"，未涉及的属性仍然继承（docs/roadmap.md STYLE-02：
    /// 恢复继承不能丢失默认值）。合并规则见 <see cref="LyricStyleResolver" />。
    /// </remarks>
    public LyricStyleOverride? StyleOverride { get; init; }

    /// <summary>
    /// 本行拆分出的文本元素。空序列表示尚未拆分。
    /// </summary>
    /// <remarks>
    /// 取值时会把 <c>default</c>（未初始化的 <see cref="ImmutableArray{T}"/>）规范化为空序列，
    /// 因为对 <c>default</c> 直接枚举会抛 <see cref="NullReferenceException"/>。
    /// </remarks>
    public ImmutableArray<LyricToken> Tokens
    {
        get => _tokens.IsDefault ? ImmutableArray<LyricToken>.Empty : _tokens;
        init => _tokens = value;
    }

    /// <summary>本行是否已经拆分为文本元素。</summary>
    public bool IsTokenized => !Tokens.IsEmpty;

    /// <summary>行的时长（微秒）。</summary>
    /// <exception cref="InvalidOperationException">行没有时间，或时间区间非法时抛出。</exception>
    public long DurationUs
    {
        get
        {
            if (Time is not { } range || !range.TryGetDurationUs(out long durationUs))
            {
                throw new InvalidOperationException($"歌词行 '{Id}' 没有可用的时间区间。");
            }

            return durationUs;
        }
    }

    /// <summary>本行记录的状态是否与其时间数据相符；语义同 <see cref="LyricToken.IsTimeStatusConsistent"/>。</summary>
    public bool IsTimeStatusConsistent => LyricToken.IsTimeStatusPairConsistent(Time, TimingStatus);

    /// <summary>创建一个已校验的歌词行。</summary>
    /// <param name="id">行标识，不可为空白。</param>
    /// <param name="text">整行文本，不可为 <see langword="null"/>（允许为空字符串）。</param>
    /// <param name="time">行时间区间，默认没有时间。</param>
    /// <param name="timingStatus">行级时间来源状态，默认为 <see cref="TimingStatus.Missing"/>。</param>
    /// <param name="tokens">文本元素，默认尚未拆分。</param>
    /// <param name="styleOverride">行级样式覆盖，默认不覆盖。</param>
    /// <returns>新的歌词行实例。</returns>
    /// <exception cref="ArgumentException">标识为空白，"时间 + 状态"组合不自洽，或元素标识在本行内重复时抛出。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> 为 <see langword="null"/> 时抛出。</exception>
    public static LyricLine Create(
        string id,
        string text,
        TimeRange? time = null,
        TimingStatus timingStatus = TimingStatus.Missing,
        IEnumerable<LyricToken>? tokens = null,
        LyricStyleOverride? styleOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);

        if (!LyricToken.IsTimeStatusPairConsistent(time, timingStatus))
        {
            throw new ArgumentException(
                $"时间与状态不自洽：状态 {timingStatus} 与时间 {time?.ToString() ?? "<无>"} 不匹配。",
                nameof(timingStatus));
        }

        ImmutableArray<LyricToken> tokenArray = tokens is null
            ? ImmutableArray<LyricToken>.Empty
            : [.. tokens];

        EnsureUniqueTokenIds(id, tokenArray);

        return new LyricLine
        {
            Id = id,
            Text = text,
            Time = time,
            TimingStatus = timingStatus,
            Tokens = tokenArray,
            StyleOverride = styleOverride,
        };
    }

    /// <summary>
    /// 手写的结构相等性。
    /// </summary>
    /// <remarks>
    /// <see cref="ImmutableArray{T}"/> 的 <c>Equals</c> 比较的是内部数组<b>引用</b>而不是内容，
    /// 若沿用记录自动生成的相等性，内容相同的两份数据会被判为不等，
    /// 进而破坏往返测试、撤销栈去重和渲染缓存的失效判断（docs/development.md §10.4）。
    /// </remarks>
    /// <param name="other">另一个歌词行。</param>
    /// <returns>所有字段与元素内容均相等时为 <see langword="true"/>。</returns>
    public bool Equals(LyricLine? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return string.Equals(Id, other.Id, StringComparison.Ordinal)
            && string.Equals(Text, other.Text, StringComparison.Ordinal)
            && Time == other.Time
            && TimingStatus == other.TimingStatus
            && Equals(StyleOverride, other.StyleOverride)
            && PrimaryLineId == other.PrimaryLineId
            && SequenceEquality.ContentEquals(RubyAnnotations, other.RubyAnnotations)
            && SequenceEquality.ContentEquals(Tokens, other.Tokens);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Id, StringComparer.Ordinal);
        hash.Add(Text, StringComparer.Ordinal);
        hash.Add(Time);
        hash.Add(TimingStatus);
        hash.Add(StyleOverride);
        hash.Add(PrimaryLineId, StringComparer.Ordinal);
        SequenceEquality.AddContentHash(ref hash, RubyAnnotations);
        SequenceEquality.AddContentHash(ref hash, Tokens);
        return hash.ToHashCode();
    }

    private static void EnsureUniqueTokenIds(string lineId, ImmutableArray<LyricToken> tokens)
    {
        if (tokens.IsEmpty)
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (LyricToken token in tokens)
        {
            if (!seen.Add(token.Id))
            {
                throw new ArgumentException(
                    $"歌词行 '{lineId}' 内的元素标识重复：'{token.Id}'。",
                    nameof(tokens));
            }
        }
    }
}
