using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Rendering;

/// <summary>
/// 一个进度的来源，决定界面应当如何描述它。
/// </summary>
/// <remarks>
/// 三档刻意区分"数值"与"可信度"：缺失时间时进度是 0，但那**不等于**"还没唱"，
/// 界面必须能区分"未定时"和"确实尚未演唱"，否则用户会以为软件已经对齐好了
/// （docs/development.md §5.4、§19）。
/// </remarks>
public enum ProgressSource
{
    /// <summary>没有可用时间：进度恒为 0，含义是"不知道"，不是"未开始"。</summary>
    Unavailable,

    /// <summary>行级精度：只来自行自己的时间区间，不推算元素时间。</summary>
    FromLine,

    /// <summary>元素级精度：来自该元素自己的时间区间。</summary>
    FromToken,

    /// <summary>注音片段级精度：来自注音片段自身的时间区间。</summary>
    FromRubySegment,
}

/// <summary>某一时刻下注音文本片段的状态。</summary>
/// <param name="RubyId">所属注音组标识。</param>
/// <param name="SegmentId">片段稳定标识。</param>
/// <param name="SegmentIndex">片段在注音组中的序号。</param>
/// <param name="Utf16Start">片段在注音拼接文本中的起始下标。</param>
/// <param name="Utf16Length">片段占用的 UTF-16 代码单元数量。</param>
/// <param name="Progress">片段已演唱的比例。</param>
/// <param name="Source">进度来源。</param>
/// <param name="IsCurrent">时刻是否位于片段的半开时间区间内。</param>
public readonly record struct RubyTextSegmentSample(
    string RubyId, string SegmentId, int SegmentIndex, int Utf16Start, int Utf16Length,
    double Progress, ProgressSource Source, bool IsCurrent)
{
    /// <summary>结束下标（不含）。</summary>
    public int Utf16End => Utf16Start + Utf16Length;

    /// <summary>是否已经唱完。</summary>
    public bool IsSung => Progress >= 1d;

    /// <summary>是否完全没有可用时间。</summary>
    public bool IsUnavailable => Source == ProgressSource.Unavailable;

    /// <summary>进度所依据的时间段：分段自己的时间，或跟随元素时元素的时间；没有可用时间、或进度被置零时为空。</summary>
    /// <remarks>扫字羽化据此按帧时刻求不夹值的演唱位置（P3 设计 §10.1 第 6、8 条）。</remarks>
    public TimeRange? Time { get; init; }
}

/// <summary>某个时刻下一个元素的状态。</summary>
/// <param name="Index">元素在行内的序号（从 0 开始）。</param>
/// <param name="Utf16Start">元素在行文本中的起始下标。</param>
/// <param name="Utf16Length">元素占用的 UTF-16 代码单元数量。</param>
/// <param name="Progress">该元素已演唱的比例，恒在 0 到 1 之间。</param>
/// <param name="Source"><paramref name="Progress"/> 的精度来源。</param>
/// <param name="IsCurrent">时刻是否位于元素的半开时间区间内。</param>
public readonly record struct TokenSample(
    int Index,
    int Utf16Start,
    int Utf16Length,
    double Progress,
    ProgressSource Source,
    bool IsCurrent)
{
    /// <summary>结束下标（不含）。</summary>
    public int Utf16End => Utf16Start + Utf16Length;

    /// <summary>是否已经唱完。</summary>
    public bool IsSung => Progress >= 1d;

    /// <summary>是否完全没有可用时间。</summary>
    public bool IsUnavailable => Source == ProgressSource.Unavailable;

    /// <summary>元素自己的时间段，只在进度来自元素时间时提供；进度被置零时为空。</summary>
    /// <remarks>扫字羽化据此按帧时刻求不夹值的演唱位置（P3 设计 §10.1 第 8 条）；脚本可见的演唱采样不变。</remarks>
    public TimeRange? Time { get; init; }
}

/// <summary>
/// 某一时刻的歌词采样结果：当前显示哪一行、整行与逐元素的进度。
/// </summary>
/// <remarks>
/// <para>
/// 本类型是"时间 → 画面"的唯一解释（docs/development.md §10.1 的"时间轴采样器"），
/// 预览与最终渲染都必须用它，不允许各自再算一遍进度（§3.2 硬性约束）。
/// </para>
/// <para>
/// 采样是<b>纯函数</b>：相同的文档与时刻永远得到相同结果，
/// 因此可以放心地拿去做帧缓存键与金样测试（roadmap RENDER-03 / RENDER-05）。
/// </para>
/// </remarks>
public sealed record LyricFrameSample
{
    /// <summary>没有任何可显示内容的采样（时间在首行之前，或文档为空）。</summary>
    public static LyricFrameSample Empty { get; } = new();

    /// <summary>采样时刻（微秒）。</summary>
    public long TimeUs { get; init; }

    /// <summary>当前行的下标；<see cref="Empty"/> 为 -1。</summary>
    public int LineIndex { get; init; } = -1;

    /// <summary>当前行；没有可显示行时为 <see langword="null"/>。</summary>
    public LyricLine? Line { get; init; }

    /// <summary>整行已演唱的比例，恒在 0 到 1 之间。</summary>
    public double LineProgress { get; init; }

    /// <summary><see cref="LineProgress"/> 的精度来源。</summary>
    public ProgressSource LineProgressSource { get; init; } = ProgressSource.Unavailable;

    /// <summary>逐元素状态，按行内顺序。</summary>
    public ImmutableArray<TokenSample> Tokens { get; init; } = [];

    /// <summary>注音片段状态，按行内注音及片段顺序；不改变 Tokens 语义。</summary>
    public ImmutableArray<RubyTextSegmentSample> RubySegments { get; init; } = [];

    /// <summary>此刻正在演唱的元素序号；没有则为 -1。</summary>
    public int CurrentTokenIndex { get; init; } = -1;

    /// <summary>元素的文本拼接是否与行文本完全一致；不一致时逐字高亮不可信。</summary>
    public bool TokensMatchLineText { get; init; }

    /// <summary>当前行是否已经唱完（整行进度为 1）。</summary>
    public bool IsLineComplete => LineProgress >= 1d;

    /// <summary>是否有可显示的行。</summary>
    public bool HasLine => Line is not null;

    /// <summary>当前行是否带有元素。</summary>
    public bool HasTokens => !Tokens.IsEmpty;
}

/// <summary>
/// 把"时间"解释成"画面状态"的采样器。
/// </summary>
/// <remarks>
/// <para>只显示时间区间包含当前时刻的行。元素进度只来自自身区间；未定时元素
/// 保持不可用，在行内空档里整行仍可见而没有活动元素。</para>
/// <para>
/// 采样器不修改任何数据（docs/development.md §19）。
/// </para>
/// </remarks>
public static class LyricSampler
{
    /// <summary>采样某个时刻的画面状态。</summary>
    /// <param name="lyrics">歌词文档。</param>
    /// <param name="timeUs">时刻（微秒）。</param>
    /// <returns>该时刻的行与元素状态；没有可显示行时是 <see cref="LyricFrameSample.Empty"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="lyrics"/> 为 <see langword="null"/> 时抛出。</exception>
    public static LyricFrameSample Sample(LyricsDocument lyrics, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(lyrics);

        int lineIndex = FindLineIndexAt(lyrics, timeUs);

        if (lineIndex < 0)
        {
            return LyricFrameSample.Empty with { TimeUs = timeUs };
        }

        return SampleLine(lyrics.Lines[lineIndex], timeUs, lineIndex);
    }

    /// <summary>采样明确指定的行；元素时间语义与普通采样相同。</summary>
    /// <param name="line">被场景布局选择的行。</param>
    /// <param name="timeUs">时刻。</param>
    /// <param name="lineIndex">行在所属分支中的序号。</param>
    /// <returns>不修改源数据的行进度。</returns>
    public static LyricFrameSample SampleLine(LyricLine line, long timeUs, int lineIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(line);
        (double lineProgress, ProgressSource lineSource) = ComputeLineProgress(line, timeUs);
        ImmutableArray<TokenSample> tokens = SampleTokens(line, timeUs);
        bool matches = TokensMatchLineText(line, tokens);

        return new LyricFrameSample
        {
            TimeUs = timeUs,
            LineIndex = lineIndex,
            Line = line,
            LineProgress = lineProgress,
            LineProgressSource = lineSource,
            Tokens = tokens,
            RubySegments = SampleRubySegments(line, timeUs, tokens),
            CurrentTokenIndex = FindCurrentTokenIndex(tokens),
            TokensMatchLineText = matches,
        };
    }

    /// <summary>
    /// 找出某个时刻应当显示的行。
    /// </summary>
    /// <param name="lyrics">歌词文档。</param>
    /// <param name="timeUs">时刻（微秒）。</param>
    /// <returns>行下标；没有可显示行时为 -1。</returns>
    /// <remarks>
    /// 取"时间区间包含当前时刻的最后一行"，因此结果与文档中行的排列顺序无关，
    /// 乱序数据也不会让画面随机跳动。
    /// </remarks>
    public static int FindLineIndexAt(LyricsDocument lyrics, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(lyrics);

        int best = -1;
        long bestStart = long.MinValue;

        for (int i = 0; i < lyrics.Lines.Length; i++)
        {
            if (!TryGetUsableRange(lyrics.Lines[i].Time, out long start, out long end))
            {
                continue;
            }

            if (start > timeUs || timeUs >= end || start < bestStart)
            {
                continue;
            }

            best = i;
            bestStart = start;
        }

        return best;
    }

    /// <summary>计算整行进度。</summary>
    /// <param name="line">歌词行。</param>
    /// <param name="timeUs">时刻（微秒）。</param>
    /// <returns>进度（0 到 1）与来源。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> 为 <see langword="null"/> 时抛出。</exception>
    public static (double Progress, ProgressSource Source) ComputeLineProgress(LyricLine line, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(line);

        return TryGetUsableRange(line.Time, out long start, out long end)
            ? (Clamp01(Ratio(timeUs - start, end - start)), ProgressSource.FromLine)
            : (0d, ProgressSource.Unavailable);
    }

    /// <summary>
    /// 判断一个时间区间是否可以用于计算进度。
    /// </summary>
    /// <param name="time">待判断的时间区间。</param>
    /// <param name="startUs">可用的起始时刻。</param>
    /// <param name="endUs">可用的结束时刻。</param>
    /// <returns>区间存在、起点非负且终点严格晚于起点时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 零长度区间被排除：它无法换算成比例，而"点时刻"在卡拉 OK 里没有可用语义
    /// （需要时应当由数据修正，而不是由渲染层猜测，docs/development.md §19）。
    /// </remarks>
    public static bool TryGetUsableRange(TimeRange? time, out long startUs, out long endUs)
    {
        startUs = 0;
        endUs = 0;

        if (time is not { } range)
        {
            return false;
        }

        startUs = range.StartUs;
        endUs = range.EndUs;

        return range.IsValid && range.StartUs >= 0 && range.EndUs > range.StartUs;
    }

    /// <summary>为一行里的每个元素计算进度。</summary>
    private static ImmutableArray<TokenSample> SampleTokens(LyricLine line, long timeUs)
    {
        if (line.Tokens.IsEmpty)
        {
            return [];
        }

        int count = line.Tokens.Length;
        TokenSample[] samples = new TokenSample[count];
        int offset = 0;

        for (int i = 0; i < count; i++)
        {
            LyricToken token = line.Tokens[i];
            int length = token.Text.Length;

            if (TryGetUsableRange(token.Time, out long start, out long end))
            {
                samples[i] = new TokenSample(
                    i,
                    offset,
                    length,
                    Clamp01(Ratio(timeUs - start, end - start)),
                    ProgressSource.FromToken,
                    IsCurrent: timeUs >= start && timeUs < end) { Time = new TimeRange(start, end) };
            }
            else
            {
                samples[i] = new TokenSample(i, offset, length, 0d, ProgressSource.Unavailable, IsCurrent: false);
            }

            offset += length;
        }

        return [.. samples];
    }

    private static ImmutableArray<RubyTextSegmentSample> SampleRubySegments(
        LyricLine line, long timeUs, ImmutableArray<TokenSample> tokens)
    {
        if (line.RubyAnnotations.IsEmpty) return [];
        List<RubyTextSegmentSample> result = [];
        foreach (RubyAnnotation annotation in line.RubyAnnotations)
        {
            int tokenIndex = Array.FindIndex(line.Tokens.ToArray(), token => token.Id == annotation.BaseTokenId);
            TokenSample tokenSample = tokenIndex >= 0 && tokenIndex < tokens.Length
                ? tokens[tokenIndex] : default;
            int offset = 0;
            IReadOnlyList<RubyTextSegment> segments = annotation.EffectiveSegments;
            int segmentIndex = 0;
            foreach (RubyTextSegment segment in segments)
            {
                ProgressSource source = ProgressSource.Unavailable;
                double progress = 0d;
                bool current = false;
                TimeRange? time = null;
                if (segment.TimingStatus == TimingStatus.Missing && segment.Time is null && tokenIndex >= 0)
                {
                    progress = tokenSample.Progress; source = tokenSample.Source; current = tokenSample.IsCurrent; time = tokenSample.Time;
                }
                else if (segment.TimingStatus is TimingStatus.Imported or TimingStatus.Estimated or TimingStatus.Edited or TimingStatus.Confirmed
                    && TryGetUsableRange(segment.Time, out long start, out long end))
                {
                    progress = Clamp01(Ratio(timeUs - start, end - start));
                    source = ProgressSource.FromRubySegment; current = timeUs >= start && timeUs < end; time = new TimeRange(start, end);
                }
                result.Add(new(annotation.Id, segment.Id, segmentIndex++, offset, segment.Text.Length, progress, source, current) { Time = time });
                offset += segment.Text.Length;
            }
        }
        return [.. result];
    }

    private static int FindCurrentTokenIndex(ImmutableArray<TokenSample> tokens)
    {
        foreach (TokenSample token in tokens)
        {
            if (token.IsCurrent)
            {
                return token.Index;
            }
        }

        return -1;
    }

    private static bool TokensMatchLineText(LyricLine line, ImmutableArray<TokenSample> tokens)
    {
        if (tokens.IsEmpty)
        {
            return false;
        }

        int expected = tokens[^1].Utf16End;

        if (expected != line.Text.Length)
        {
            return false;
        }

        foreach (TokenSample token in tokens)
        {
            if (!line.Text.AsSpan(token.Utf16Start, token.Utf16Length).SequenceEqual(line.Tokens[token.Index].Text))
            {
                return false;
            }
        }

        return true;
    }

    private static double Ratio(long elapsed, long span) =>
        span <= 0 ? 0d : (double)elapsed / span;

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);
}
