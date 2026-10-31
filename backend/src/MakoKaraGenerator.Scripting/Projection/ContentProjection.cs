using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>把不可变源文档投影为中立目录；不返回 Core 对象，也不修改源数据。</summary>
public static class ContentProjection
{
    /// <summary>取得完整源目录；源身份或时间非法时拒绝，不裁成当前可见集合。</summary>
    public static ContentCatalog Create(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<SourceBranch> branches = []; List<SourceLine> lines = [];
        List<SourceToken> tokens = []; List<SourceRuby> ruby = [];
        foreach (var (branch, branchIndex) in snapshot.Lyrics.Branches.Select((branch, index) => (branch, index)))
        {
            BranchId branchId = new(branch.Id);
            branches.Add(new(branchId, branch.Name, branch.Role.ToString(), branchIndex,
                [.. branch.Lines.Select(static line => new LineId(line.Id))]));
            foreach (var (line, lineIndex) in branch.Lines.Select((line, index) => (line, index)))
            {
                LineId lineId = new(line.Id);
                lines.Add(new(lineId, branchId, line.Text, Range(line.Time), Status(line.TimingStatus), lineIndex,
                    [.. line.Tokens.Select(static token => new TokenId(token.Id))],
                    [.. line.RubyAnnotations.Select(static annotation => new RubyId(annotation.Id))]));
                foreach (var (token, tokenIndex) in line.Tokens.Select((token, index) => (token, index)))
                    tokens.Add(new(new(token.Id), branchId, lineId, token.Text, Range(token.Time), Status(token.TimingStatus), tokenIndex));
                foreach (RubyAnnotation annotation in line.RubyAnnotations)
                    ruby.Add(new SourceRuby(new(annotation.Id), lineId, new(annotation.BaseTokenId), annotation.Text, annotation.Language)
                    { Segments = [.. annotation.EffectiveSegments.Select(segment => new SourceRubySegment(segment.Id, segment.Text,
                        Range(segment.Time), Status(segment.TimingStatus)))] });
            }
        }
        return new([.. branches], [.. lines], [.. tokens], [.. ruby]);
    }

    /// <summary>投影共享采样器的元素进度；Missing 不填零或假装行进度。</summary>
    public static PerformanceSample TokenPerformance(LyricToken token, TokenSample sample, long timeUs, bool tokensMatchLineText)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        TimeRange? range = Range(token.Time);
        bool available = tokensMatchLineText && range is not null
            && sample.Source == Rendering.ProgressSource.FromToken;
        return new(range, range?.DurationUs, range is { } time ? timeUs - time.StartUs : null,
            available ? sample.Progress : null, available && sample.IsCurrent, available && sample.IsSung,
            available ? ProgressSource.Token : range is null ? ProgressSource.Missing : ProgressSource.Unavailable);
    }

    /// <summary>投影注音片段的独立时间性能；视觉的基础元素 fallback 不伪造注音自身时间。</summary>
    public static PerformanceSample RubySegmentPerformance(RubyTextSegment segment, RubyTextSegmentSample sample, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        TimeRange? range = Range(segment.Time);
        bool available = range is not null && sample.Source == Rendering.ProgressSource.FromRubySegment;
        return new(range, range?.DurationUs, range is { } time ? timeUs - time.StartUs : null,
            available ? sample.Progress : null, available && sample.IsCurrent, available && sample.IsSung,
            available ? ProgressSource.RubySegment : range is null ? ProgressSource.Missing : ProgressSource.Unavailable);
    }

    /// <summary>行级依据由共享采样器取得；不为缺时间的元素复制这个进度。</summary>
    public static PerformanceSample LinePerformance(LyricLine line, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        TimeRange? range = Range(line.Time);
        var sampled = LyricSampler.ComputeLineProgress(line, timeUs);
        return new(range, range?.DurationUs, range is { } time ? timeUs - time.StartUs : null,
            range is not null ? sampled.Progress : null, range?.Contains(timeUs) == true,
            range is { } ended && timeUs >= ended.EndUs, range is null ? ProgressSource.Missing : ProgressSource.Line);
    }

    private static TimeRange? Range(Core.Time.TimeRange? range)
        => range is { } time ? new(time.StartUs, time.EndUs) : null;

    private static SourceTimingStatus Status(TimingStatus status) => status switch
    {
        TimingStatus.Missing => SourceTimingStatus.Missing,
        TimingStatus.Imported => SourceTimingStatus.Imported,
        TimingStatus.Estimated => SourceTimingStatus.Estimated,
        TimingStatus.Edited => SourceTimingStatus.Edited,
        TimingStatus.Confirmed => SourceTimingStatus.Confirmed,
        TimingStatus.Invalid => SourceTimingStatus.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
