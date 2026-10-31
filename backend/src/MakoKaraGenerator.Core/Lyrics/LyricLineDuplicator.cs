using System.Collections.Immutable;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>
/// 复制歌词行（docs/development.md §4.1 P0 的"新建、删除、复制、合并、拆分歌词行"）。
/// </summary>
/// <remarks>
/// <para>
/// 文本、切分、逐字时间、样式覆盖照搬；展示配置由工程级复制命令处理，但<b>时间整体向后平移一个"原行时长"</b>，
/// 于是副本紧跟在原行之后（做重复段落时只需微调，不必重新打轴）。
/// </para>
/// <para>
/// 只有在原行时间合法时才平移：原行没有时间（普通 LRC 导入后的常见状态）或时间非法时
/// <b>原样保留</b>那份"没有时间/非法"的状态——不得为了让副本有个位置而编造时间
/// （docs/development.md §19）。因此副本可能与下一行重叠，那属于真实的数据冲突，
/// 由校验器报出来、由用户决定怎么调。
/// </para>
/// <para>
/// 标识必须由调用方提供：行与元素共用同一个标识命名空间（ADR-0006），
/// 分配器要知道整份文档才知道哪些号是空的，因此这里只接受"造一个新号"的函数。
/// </para>
/// </remarks>
public static class LyricLineDuplicator
{
    /// <summary>复制一行。</summary>
    /// <param name="source">原行。</param>
    /// <param name="lineId">副本的行标识。</param>
    /// <param name="nextTokenId">造一个可用元素标识的函数，副本的每个元素调用一次。</param>
    /// <param name="nextRubySegmentId">造一个可用注音片段标识的函数；省略时由副本注音标识派生。</param>
    /// <returns>副本。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> 或 <paramref name="nextTokenId"/> 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="lineId"/> 为空白时抛出。</exception>
    public static LyricLine Duplicate(
        LyricLine source,
        string lineId,
        Func<string> nextTokenId,
        Func<string>? nextRubySegmentId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nextTokenId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lineId);

        long shiftUs = source.Time is { IsValid: true } lineTime
            ? lineTime.DurationUs
            : 0L;

        ImmutableArray<LyricToken> tokens = source.Tokens
            .Select(token => DuplicateToken(token, nextTokenId(), shiftUs))
            .ToImmutableArray();

        LyricLine copy = LyricLine.Create(
            lineId,
            source.Text,
            Shifted(source.Time, shiftUs),
            source.TimingStatus,
            tokens,
            source.StyleOverride);

        return copy with
        {
            PrimaryLineId = source.PrimaryLineId,
            RubyAnnotations = [.. source.RubyAnnotations.Select(ruby =>
            {
                string rubyId = nextTokenId();
                ImmutableArray<RubyTextSegment> segments = [.. ruby.Segments.Select((segment, segmentIndex) => segment with
                {
                    Id = nextRubySegmentId is not null
                        ? nextRubySegmentId()
                        : rubyId + "-segment-" + (segmentIndex + 1),
                    Time = Shifted(segment.Time, shiftUs),
                })];
                return ruby with
                {
                    Id = rubyId,
                    BaseTokenId = Array.FindIndex(source.Tokens.ToArray(), token => token.Id == ruby.BaseTokenId) is >= 0 and var index
                        ? tokens[index].Id : ruby.BaseTokenId,
                    Segments = segments,
                };
            })],
        };
    }

    private static LyricToken DuplicateToken(LyricToken source, string tokenId, long shiftUs)
    {
        LyricToken copy = LyricToken.Create(
            tokenId,
            source.Text,
            Shifted(source.Time, shiftUs),
            source.TimingStatus);

        return copy with { StyleOverride = source.StyleOverride };
    }

    /// <summary>把时间区间整体后移；没有时间或区间非法时原样返回。</summary>
    private static TimeRange? Shifted(TimeRange? time, long shiftUs)
    {
        if (time is not { IsValid: true } range)
        {
            // 缺失与非法都原样保留：副本不该比原行"多知道"什么。
            return time;
        }

        return new TimeRange(range.StartUs + shiftUs, range.EndUs + shiftUs);
    }
}
